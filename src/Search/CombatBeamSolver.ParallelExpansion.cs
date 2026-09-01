using System.Runtime.ExceptionServices;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private readonly record struct RawCardCandidate(
        SearchNode Node,
        CardType CardType,
        uint? TargetCombatId);

    private sealed class ExpansionBatch : IDisposable
    {
        private readonly HashSet<SimulationSnapshot> _owned =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<SimulationSnapshot> _transferred =
            new(ReferenceEqualityComparer.Instance);

        public List<RawCardCandidate> Cards { get; } = new(16);
        public List<SearchNode> Potions { get; } = [];
        public List<SearchNode> EndTurns { get; } = [];

        public void Add(RawCardCandidate candidate)
        {
            Own(candidate.Node.Snapshot);
            Cards.Add(candidate);
        }

        public void AddPotion(SearchNode candidate)
        {
            Own(candidate.Snapshot);
            Potions.Add(candidate);
        }

        public void AddEndTurn(SearchNode candidate)
        {
            Own(candidate.Snapshot);
            EndTurns.Add(candidate);
        }

        public void Transfer(SimulationSnapshot snapshot)
        {
            if (_owned.Remove(snapshot))
            {
                _transferred.Add(snapshot);
                return;
            }
            if (!_transferred.Contains(snapshot))
                throw new InvalidOperationException("并行展开快照没有可移交的所有权。");
        }

        public void Release(SimulationSnapshot snapshot)
        {
            if (!_owned.Remove(snapshot))
                throw new InvalidOperationException("并行展开快照被重复释放或已经移交。");
            snapshot.ReleaseSimulator();
        }

        public void Dispose()
        {
            foreach (SimulationSnapshot snapshot in _owned)
                snapshot.ReleaseSimulator();
            _owned.Clear();
            _transferred.Clear();
        }

        private void Own(SimulationSnapshot snapshot)
        {
            if (!_owned.Add(snapshot))
                throw new InvalidOperationException("并行展开生成了共享的子快照所有权。");
        }
    }

    private sealed record ExpansionWorkerOutcome(
        CombatBeamSolver? Worker,
        ExpansionBatch? Batch,
        ExceptionDispatchInfo? Error,
        long AllocatedBytes);

    /// <summary>
    /// 一次 Solve 复用固定数量的后台 lane；coordinator 自己执行 lane 0，避免为每个父节点
    /// 创建 Task 和 worker。候选只在各 lane 内物化，transposition/dominance 仍由 coordinator
    /// 按父节点原顺序提交，因此 DOP 不改变搜索结果。
    /// </summary>
    private sealed class ParallelExpansionExecutor : IDisposable
    {
        private readonly CombatBeamSolver _coordinator;
        private ExpansionLane[]? _backgroundLanes;
        private int _activeWorkers;
        private int _maximumActiveWorkers;
        private bool _disposed;

        public ParallelExpansionExecutor(CombatBeamSolver coordinator, int degreeOfParallelism)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(degreeOfParallelism, 2);
            _coordinator = coordinator;
            DegreeOfParallelism = degreeOfParallelism;
        }

        public int DegreeOfParallelism { get; }

        public ExpansionWorkerOutcome[] Evaluate(IReadOnlyList<SearchNode> nodes)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (nodes.Count == 0)
                return [];
            if (nodes.Count > DegreeOfParallelism)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(nodes),
                    $"并行展开 wave={nodes.Count} 超过 lane={DegreeOfParallelism}。");
            }

            ExpansionLane[] backgroundLanes = EnsureBackgroundLanes();
            using ExpansionWave wave = new(nodes.Count);
            for (int index = 1; index < nodes.Count; index++)
            {
                backgroundLanes[index - 1].Dispatch(
                    new ExpansionWorkItem(nodes[index], wave, index));
            }

            Execute(
                _coordinator,
                nodes[0],
                wave,
                outcomeIndex: 0,
                includeWorkerMetrics: false);
            wave.BackgroundCompleted.Wait();

            if (nodes.Count > 1)
            {
                _coordinator._run.ParallelExpansionWaves++;
                _coordinator._run.ParallelExpansionWorkItems += nodes.Count;
                _coordinator._run.MaxParallelExpansionConcurrency = Math.Max(
                    _coordinator._run.MaxParallelExpansionConcurrency,
                    Volatile.Read(ref _maximumActiveWorkers));
            }
            return wave.Outcomes;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_backgroundLanes != null)
            {
                foreach (ExpansionLane lane in _backgroundLanes)
                    lane.Dispose();
            }
        }

        public void ResetRebuildableCaches()
        {
            if (_backgroundLanes == null)
                return;
            foreach (ExpansionLane lane in _backgroundLanes)
                lane.ResetRebuildableCaches();
        }

        private ExpansionLane[] EnsureBackgroundLanes()
        {
            if (_backgroundLanes != null)
                return _backgroundLanes;
            List<ExpansionLane> lanes = new(DegreeOfParallelism - 1);
            try
            {
                for (int index = 1; index < DegreeOfParallelism; index++)
                    lanes.Add(new ExpansionLane(this, _coordinator.CreateExpansionWorker(), index));
                _backgroundLanes = lanes.ToArray();
                return _backgroundLanes;
            }
            catch
            {
                foreach (ExpansionLane lane in lanes)
                    lane.Dispose();
                throw;
            }
        }

        private void Execute(
            CombatBeamSolver worker,
            SearchNode node,
            ExpansionWave wave,
            int outcomeIndex,
            bool includeWorkerMetrics)
        {
            int activeWorkers = Interlocked.Increment(ref _activeWorkers);
            UpdateMaximum(ref _maximumActiveWorkers, activeWorkers);
            long allocatedAtStart = includeWorkerMetrics
                ? GC.GetAllocatedBytesForCurrentThread()
                : 0;
            try
            {
                ExpansionBatch batch = worker.EvaluateRawExpansion(node);
                long allocatedBytes = includeWorkerMetrics
                    ? GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart
                    : 0;
                wave.Outcomes[outcomeIndex] = new ExpansionWorkerOutcome(
                    includeWorkerMetrics ? worker : null,
                    batch,
                    null,
                    allocatedBytes);
            }
            // Background lanes cannot throw across a Thread boundary. Capture the original stack;
            // the coordinator always rethrows it after every lane reaches the completion barrier.
            catch (System.Exception error)
            {
                wave.Outcomes[outcomeIndex] = new ExpansionWorkerOutcome(
                    includeWorkerMetrics ? worker : null,
                    null,
                    ExceptionDispatchInfo.Capture(error),
                    0);
            }
            finally
            {
                Interlocked.Decrement(ref _activeWorkers);
            }
        }

        private static void UpdateMaximum(ref int target, int value)
        {
            int observed = Volatile.Read(ref target);
            while (observed < value)
            {
                int previous = Interlocked.CompareExchange(ref target, value, observed);
                if (previous == observed)
                    return;
                observed = previous;
            }
        }

        private sealed class ExpansionWave(int workItemCount) : IDisposable
        {
            public ExpansionWorkerOutcome[] Outcomes { get; } = new ExpansionWorkerOutcome[workItemCount];
            public CountdownEvent BackgroundCompleted { get; } = new(Math.Max(0, workItemCount - 1));

            public void Dispose()
            {
                BackgroundCompleted.Dispose();
            }
        }

        private sealed record ExpansionWorkItem(
            SearchNode Node,
            ExpansionWave Wave,
            int OutcomeIndex);

        private sealed class ExpansionLane : IDisposable
        {
            private readonly ParallelExpansionExecutor _owner;
            private readonly CombatBeamSolver _worker;
            private readonly AutoResetEvent _workAvailable = new(false);
            private readonly ManualResetEventSlim _started = new(false);
            private readonly object _gate = new();
            private readonly Thread _thread;
            private ExpansionWorkItem? _workItem;
            private ExceptionDispatchInfo? _startupError;
            private bool _stopping;

            public ExpansionLane(
                ParallelExpansionExecutor owner,
                CombatBeamSolver worker,
                int laneIndex)
            {
                _owner = owner;
                _worker = worker;
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = $"CombatSolver expansion {laneIndex}",
                };
                try
                {
                    _thread.Start();
                    _started.Wait();
                }
                catch
                {
                    _started.Dispose();
                    _workAvailable.Dispose();
                    throw;
                }
                _started.Dispose();
                if (_startupError != null)
                {
                    _thread.Join();
                    _workAvailable.Dispose();
                    _startupError.Throw();
                }
            }

            public void Dispatch(ExpansionWorkItem workItem)
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_stopping, this);
                    if (_workItem != null)
                        throw new InvalidOperationException("并行展开 lane 尚未完成上一个工作项。");
                    _workItem = workItem;
                }
                _workAvailable.Set();
            }

            public void Dispose()
            {
                lock (_gate)
                    _stopping = true;
                _workAvailable.Set();
                _thread.Join();
                _workAvailable.Dispose();
            }

            public void ResetRebuildableCaches()
                => _worker._run.ResetRebuildableCaches([]);

            private void Run()
            {
                IDisposable notificationIsolation;
                try
                {
                    try
                    {
                        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                    }
                    catch (PlatformNotSupportedException)
                    {
                        // Priority is a scheduling hint; unsupported platforms still run the same work.
                    }
                    catch (ThreadStateException)
                    {
                        // A platform that rejects the hint still runs the same work at normal priority.
                    }
                    notificationIsolation = SimulationNotificationIsolation.Enter();
                }
                catch (System.Exception error)
                {
                    _startupError = ExceptionDispatchInfo.Capture(error);
                    _started.Set();
                    return;
                }
                _started.Set();
                using (notificationIsolation)
                {
                    while (true)
                    {
                        _workAvailable.WaitOne();
                        ExpansionWorkItem? workItem;
                        lock (_gate)
                        {
                            if (_stopping)
                                return;
                            workItem = _workItem;
                            _workItem = null;
                        }
                        if (workItem == null)
                            continue;
                        try
                        {
                            _owner.Execute(
                                _worker,
                                workItem.Node,
                                workItem.Wave,
                                workItem.OutcomeIndex,
                                includeWorkerMetrics: true);
                        }
                        finally
                        {
                            workItem.Wave.BackgroundCompleted.Signal();
                        }
                    }
                }
            }
        }
    }

    private bool TryPrepareParallelExpansion(SearchNode node)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node.IsTerminal
            || node.Snapshot.PlayerDead
            || node.Snapshot.AllEnemiesDead
            || node.Snapshot.BoundaryReason != SearchBoundaryReason.None)
        {
            throw new InvalidOperationException("终结搜索节点不应进入并行展开阶段。");
        }
        _run.ReusedNodeSnapshots++;
        if (!TryMarkExpandedState(node))
            return false;
        _run.Expanded++;
        return true;
    }

    private CombatBeamSolver CreateExpansionWorker()
    {
        CombatBeamSolver worker = new(
            root,
            displayNames,
            battleDamage,
            policy,
            cancellationToken,
            progressCallback: null,
            searchProfile: _profile,
            shortCheckpointMilliseconds: _shortCheckpointMilliseconds,
            potionPolicyOverride: _potionPolicy,
            potionFreePolicyBaseline: potionFreePolicyBaseline,
            maximumPotionUses: _maximumPotionUses);
        worker._run.InitialPersistentBuffValue = _run.InitialPersistentBuffValue;
        worker._run.InitialEnemyStrengthSuppression = _run.InitialEnemyStrengthSuppression;
        worker._run.InitialEnemyWeakTurns = _run.InitialEnemyWeakTurns;
        worker._run.InitialRetainedAttackValue = _run.InitialRetainedAttackValue;
        return worker;
    }

    private ExpansionBatch EvaluateRawExpansion(SearchNode node)
    {
        ExpansionBatch batch = new();
        bool completed = false;
        try
        {
            GenerateRawCardCandidates(node, batch);
            GenerateRawPotionCandidates(node, batch);
            GenerateRawEndTurnCandidates(node, batch);
            completed = true;
            return batch;
        }
        finally
        {
            if (!completed)
                batch.Dispose();
        }
    }

    private void GenerateRawCardCandidates(SearchNode node, ExpansionBatch batch)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SimulationSnapshot snapshot = node.Snapshot;
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
        if (snapshot.PlayerDead || snapshot.AllEnemiesDead)
            return;

        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        IReadOnlyList<PredictedCard> hand = playerState.Hand.Cards;
        HandFingerprintBuffer seenCards = default;
        int seenCardCount = 0;
        for (int handIndex = 0; handIndex < hand.Count; handIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PredictedCard card = hand[handIndex];
            string cardId = card.Preview.Id.Entry;
            int occurrence = 0;
            for (int priorIndex = 0; priorIndex < handIndex; priorIndex++)
            {
                if (string.Equals(hand[priorIndex].Preview.Id.Entry, cardId, StringComparison.Ordinal))
                    occurrence++;
            }
            if (!simulatedCombat.CanPlayCard(simulator, card))
                continue;
            StateFingerprint playableKey = BuildPlayableCardKey(card);
            bool duplicate = false;
            for (int seenIndex = 0; seenIndex < seenCardCount; seenIndex++)
            {
                if (seenCards[seenIndex] == playableKey)
                {
                    duplicate = true;
                    break;
                }
            }
            if (duplicate)
            {
                _run.DuplicateCardBranchesPruned++;
                continue;
            }
            seenCards[seenCardCount++] = playableKey;
            string cardStateKey = CardChoiceSupport.ChoiceCardKey(card);
            int cardStateOccurrence = 0;
            for (int priorIndex = 0; priorIndex < handIndex; priorIndex++)
            {
                if (string.Equals(
                        CardChoiceSupport.ChoiceCardKey(hand[priorIndex]),
                        cardStateKey,
                        StringComparison.Ordinal))
                {
                    cardStateOccurrence++;
                }
            }
            foreach ((int targetIndex, Creature? target) in TargetsFor(card, simulator))
            {
                if (node.ActionCount == 0 && !card.Original.CanPlayTargeting(target))
                    continue;
                PlanAction action = new(
                    PlanActionKind.PlayCard,
                    node.Turn,
                    card.Preview.Id.Entry,
                    occurrence,
                    targetIndex,
                    target?.CombatId,
                    displayNames.Card(card.Preview),
                    displayNames.Creature(target),
                    ReplayCount: Math.Max(0, card.Preview.GetEnchantedReplayCount()),
                    CardStateKey: cardStateKey,
                    CardStateOccurrence: cardStateOccurrence);
                SimulationSnapshot probeSnapshot = ReplayAction(node, action);

                CombatPredictionSimulator probeSimulator = (CombatPredictionSimulator)probeSnapshot.Simulator;
                CardChoiceSpec? choiceSpec = CardChoiceSupport.GetSpec(probeSimulator, card);
                if (choiceSpec == null && CardChoiceSupport.RequiresUnsupportedExistingChoice(card.Preview))
                {
                    probeSnapshot.ReleaseSimulator();
                    continue;
                }
                PlanCardChoice? requiredEmptyChoice = CardChoiceSupport.BuildRequiredEmptyChoice(card.Preview);
                CardChoiceSpec? primaryChoiceSpec = choiceSpec
                    ?? BuildRequiredEmptyChoiceSpec(requiredEmptyChoice);
                IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> resolvedBranches =
                    HasChoiceBeforePrimary(probeSnapshot, primaryChoiceSpec)
                        ? ResolveRoundChoiceBranches(node, action, probeSnapshot, primaryChoiceSpec)
                        : ResolvePrimaryCardChoiceBranches(
                            node,
                            action,
                            probeSnapshot,
                            choiceSpec,
                            requiredEmptyChoice);
                foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in resolvedBranches)
                {
                    bool forcedTurnEnd = finalSnapshot.Turn > node.Turn;
                    PlanAction nodeAction = finalAction with { EndsPlayerTurn = forcedTurnEnd };
                    bool terminal = finalSnapshot.PlayerDead
                        || finalSnapshot.AllEnemiesDead
                        || finalSnapshot.BoundaryReason != SearchBoundaryReason.None;
                    bool repeatableNoProgress = IsRepeatableNoProgressStep(snapshot, finalSnapshot, card);
                    string? repeatableCardId = repeatableNoProgress ? card.Preview.Id.Entry : null;
                    int repeatableCount = repeatableNoProgress
                        ? string.Equals(
                            node.RepeatableNoProgressCardId,
                            repeatableCardId,
                            StringComparison.Ordinal)
                            ? node.RepeatableNoProgressCount + 1
                            : 1
                        : 0;
                    SearchNode child = new(
                        nodeAction,
                        node.ActionCount + 1,
                        finalSnapshot.PotionUseCount,
                        finalSnapshot.PotionStrategicCost,
                        forcedTurnEnd ? node.Turn + 1 : node.Turn,
                        node.Traits,
                        node.FutureSoldHp,
                        ApplySoldHpPenalty(finalSnapshot.Score, node.FutureSoldHp),
                        finalSnapshot.StateKey,
                        finalSnapshot.HasRisk,
                        finalSnapshot.BoundaryReason,
                        terminal,
                        node,
                        finalSnapshot,
                        forcedTurnEnd
                            ? node.CombatProgress.Advance(finalSnapshot)
                            : node.CombatProgress,
                        RepeatableNoProgressCardId: repeatableCardId,
                        RepeatableNoProgressCount: repeatableCount);
                    if (ShouldPruneRepeatableNoProgress(child)
                        || ShouldPruneCrossTurnNoProgress(child))
                    {
                        _run.RepeatableNoProgressBranchesPruned++;
                        finalSnapshot.ReleaseSimulator();
                        continue;
                    }
                    batch.Add(new RawCardCandidate(
                        child,
                        card.Preview.Type,
                        target?.CombatId));
                }
            }
        }
    }

    private void GenerateRawPotionCandidates(SearchNode node, ExpansionBatch batch)
    {
        SimulationSnapshot snapshot = node.Snapshot;
        if (snapshot.PlayerDead || snapshot.AllEnemiesDead
            || _potionPolicy == SolverPotionPolicy.Disabled
            || _maximumPotionUses != null && node.PotionCount >= _maximumPotionUses.Value)
        {
            return;
        }

        CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
        for (int potionSlot = 0; potionSlot < root.PotionSlotCount; potionSlot++)
        {
            PotionModel? potion = simulatedCombat.GetPotionAtSlot(_player, potionSlot);
            if (potion == null
                || _bannedPotionSlots.Contains(potionSlot)
                || !simulatedCombat.IsPotionAvailable(_player, potionSlot)
                || !PotionOnUseSupport.CanSearch(potion)
                || PotionUsePolicy.RequiresOpeningUse(potion)
                    && node.Actions.Any(action => action.Kind != PlanActionKind.UsePotion))
            {
                continue;
            }

            foreach ((int targetIndex, Creature? target) in TargetsForPotion(potion, simulator))
            {
                PlanAction baseAction = new(
                    PlanActionKind.UsePotion,
                    node.Turn,
                    TargetIndex: targetIndex,
                    TargetCombatId: target?.CombatId,
                    TargetName: displayNames.Creature(target),
                    PotionSlot: potionSlot,
                    PotionId: potion.Id.Entry,
                    PotionTitle: displayNames.Potion(potion));
                SimulationSnapshot? probeSnapshot = null;
                IReadOnlyList<PlanCardChoice?> choices;
                if (PotionChoiceSupport.RequiresChoice(potion))
                {
                    CombatPredictionSimulator choiceSimulator = simulator;
                    if (PotionChoiceSupport.GeneratesCardChoice(potion))
                    {
                        probeSnapshot = ReplayAction(node, baseAction);
                        choiceSimulator = (CombatPredictionSimulator)probeSnapshot.Simulator;
                    }
                    CardChoiceSpec spec = PotionChoiceSupport.GetSpec(choiceSimulator, potion);
                    choices = CardChoiceSupport.BuildChoices(
                            spec,
                            displayNames,
                            _profile.MaxPileChoiceBranchesPerAction,
                            _profile.MaxHandChoiceBranchesPerAction)
                        .Select(choice => choice with { SourceId = potion.Id.Entry })
                        .Cast<PlanCardChoice?>()
                        .ToList();
                    _run.ChoiceBranchesEvaluated += choices.Count;
                    probeSnapshot?.ReleaseSimulator();
                }
                else
                {
                    probeSnapshot = ReplayAction(node, baseAction);
                    choices = [null];
                }

                foreach (PlanCardChoice? choice in choices)
                {
                    PlanAction action = baseAction with { Choice = choice };
                    SimulationSnapshot childSnapshot;
                    if (choice == null)
                    {
                        childSnapshot = probeSnapshot
                            ?? throw new InvalidOperationException("无选牌药水缺少动作快照。");
                    }
                    else
                    {
                        SimulationSnapshot? replayedChoice = ReplayPlannedChoiceBranch(node, action);
                        if (replayedChoice == null)
                            continue;
                        childSnapshot = replayedChoice;
                    }

                    foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in
                             ResolveRoundChoiceBranches(node, action, childSnapshot))
                    {
                        bool terminal = finalSnapshot.PlayerDead
                            || finalSnapshot.AllEnemiesDead
                            || finalSnapshot.BoundaryReason != SearchBoundaryReason.None;
                        SearchNode child = new(
                            finalAction,
                            node.ActionCount + 1,
                            finalSnapshot.PotionUseCount,
                            finalSnapshot.PotionStrategicCost,
                            node.Turn,
                            ClassifyPotionTraits(node.Traits, snapshot, finalSnapshot),
                            node.FutureSoldHp,
                            ApplySoldHpPenalty(finalSnapshot.Score, node.FutureSoldHp),
                            finalSnapshot.StateKey,
                            finalSnapshot.HasRisk,
                            finalSnapshot.BoundaryReason,
                            terminal,
                            node,
                            finalSnapshot,
                            node.CombatProgress);
                        batch.AddPotion(child);
                    }
                }
            }
        }
    }

    private void GenerateRawEndTurnCandidates(SearchNode node, ExpansionBatch batch)
    {
        SimulationSnapshot snapshot = node.Snapshot;
        if (snapshot.PlayerDead || snapshot.AllEnemiesDead)
            return;

        foreach ((PlanAction endAction, SimulationSnapshot endSnapshot) in BuildEndTurnBranches(node, []))
        {
            int nextTurn = node.Turn + 1;
            bool combatEnded = endSnapshot.PlayerDead || endSnapshot.AllEnemiesDead;
            bool endTerminal = combatEnded || endSnapshot.BoundaryReason != SearchBoundaryReason.None;
            SearchNode endNode = new(
                endAction,
                node.ActionCount + 1,
                endSnapshot.PotionUseCount,
                endSnapshot.PotionStrategicCost,
                nextTurn,
                ClassifyRoundTransitionTraits(node.Traits, snapshot, endSnapshot),
                node.FutureSoldHp,
                ApplySoldHpPenalty(endSnapshot.Score, node.FutureSoldHp),
                endSnapshot.StateKey,
                endSnapshot.HasRisk,
                endSnapshot.BoundaryReason,
                endTerminal,
                node,
                endSnapshot,
                node.CombatProgress.Advance(endSnapshot));
            if (ShouldPruneCrossTurnNoProgress(endNode))
            {
                _run.RepeatableNoProgressBranchesPruned++;
                endSnapshot.ReleaseSimulator();
                continue;
            }
            batch.AddEndTurn(endNode);
        }
    }

    private void MergeExpansionWorker(ExpansionWorkerOutcome outcome)
    {
        _run.OffThreadAllocatedBytes += outcome.AllocatedBytes;
        if (outcome.Worker == null)
            return;
        CombatBeamSolver worker = outcome.Worker
            ?? throw new InvalidOperationException("并行展开 worker 没有成功结果。");
        SearchRunContext source = worker._run;
        _run.DuplicateCardBranchesPruned += source.DuplicateCardBranchesPruned;
        _run.ChoiceBranchesEvaluated += source.ChoiceBranchesEvaluated;
        _run.ShuffleBranchesPruned += source.ShuffleBranchesPruned;
        _run.SoldHpBranchesPruned += source.SoldHpBranchesPruned;
        _run.ReplayCount += source.ReplayCount;
        _run.ForkCount += source.ForkCount;
        _run.TransitionCount += source.TransitionCount;
        _run.RepeatableNoProgressBranchesPruned += source.RepeatableNoProgressBranchesPruned;
        _run.StandPatProbes += source.StandPatProbes;
        source.DuplicateCardBranchesPruned = 0;
        source.ChoiceBranchesEvaluated = 0;
        source.ShuffleBranchesPruned = 0;
        source.SoldHpBranchesPruned = 0;
        source.ReplayCount = 0;
        source.ForkCount = 0;
        source.TransitionCount = 0;
        source.RepeatableNoProgressBranchesPruned = 0;
        source.StandPatProbes = 0;
        _run.Performance.DrainFrom(source.Performance);
        _run.WorkPacer.DrainFrom(source.WorkPacer);
    }

    private void CommitExpansionBatch(
        SearchNode parent,
        ExpansionBatch batch,
        Action<SearchNode> acceptChild)
    {
        List<ActionCandidate> nonDominated = new(16);
        foreach (RawCardCandidate raw in batch.Cards)
        {
            if (!TryAcceptTransposition(raw.Node))
            {
                batch.Release(raw.Node.Snapshot);
                continue;
            }
            AddNonDominatedParallelCandidate(
                nonDominated,
                BuildCandidate(
                    parent.Snapshot,
                    raw.Node.Snapshot,
                    raw.Node,
                    raw.CardType,
                    raw.TargetCombatId),
                batch);
        }

        nonDominated.Sort(static (left, right) =>
        {
            int byScore = right.Node.Score.CompareTo(left.Node.Score);
            return byScore != 0
                ? byScore
                : right.NormalizedValue.CompareTo(left.NormalizedValue);
        });
        int regularQueuedCount = Math.Min(_profile.MaxCardBranchesPerNode, nonDominated.Count);
        List<ActionCandidate> queuedCandidates = nonDominated.Take(regularQueuedCount).ToList();
        ActionCandidate? revivalWindowCandidate = nonDominated
            .Where(candidate =>
                candidate.Node.Snapshot.RevivingEnemyCount > parent.Snapshot.RevivingEnemyCount)
            .OrderByDescending(candidate => candidate.Node.Snapshot.RevivingEnemyCount)
            .ThenBy(candidate => candidate.Node.Snapshot.RawEnemyHp)
            .ThenBy(candidate => candidate.Node.Snapshot.MaxCurrentEnemyHp)
            .ThenByDescending(candidate => candidate.Node.Snapshot.ProjectedPlayerHp)
            .Select(candidate => (ActionCandidate?)candidate)
            .FirstOrDefault();
        if (revivalWindowCandidate is { } revivalCandidate
            && !queuedCandidates.Any(candidate => ReferenceEquals(candidate.Node, revivalCandidate.Node)))
        {
            queuedCandidates.Add(revivalCandidate);
        }
        foreach (ActionCandidate candidate in nonDominated.Skip(regularQueuedCount))
        {
            if (CurrentTurnRoutingChoice(candidate.Node) != null)
                queuedCandidates.Add(candidate);
        }
        _run.TopQueueActionsDropped += nonDominated.Count - queuedCandidates.Count;
        foreach (ActionCandidate candidate in nonDominated)
        {
            if (!queuedCandidates.Any(retained => ReferenceEquals(retained.Node, candidate.Node)))
                batch.Release(candidate.Node.Snapshot);
        }
        foreach (ActionCandidate candidate in queuedCandidates)
        {
            batch.Transfer(candidate.Node.Snapshot);
            acceptChild(candidate.Node);
        }

        foreach (SearchNode child in batch.Potions)
        {
            if (!TryAcceptTransposition(child))
            {
                batch.Release(child.Snapshot);
                continue;
            }
            batch.Transfer(child.Snapshot);
            acceptChild(child);
        }

        foreach (SearchNode child in batch.EndTurns)
        {
            if (!TryAcceptTransposition(child))
            {
                batch.Release(child.Snapshot);
                continue;
            }
            batch.Transfer(child.Snapshot);
            acceptChild(child);
        }
    }

    private void AddNonDominatedParallelCandidate(
        List<ActionCandidate> candidates,
        ActionCandidate candidate,
        ExpansionBatch batch)
    {
        for (int index = candidates.Count - 1; index >= 0; index--)
        {
            ActionCandidate current = candidates[index];
            if (Dominates(current, candidate))
            {
                _run.DominatedActionsPruned++;
                batch.Release(candidate.Node.Snapshot);
                return;
            }
            if (!Dominates(candidate, current))
                continue;
            candidates.RemoveAt(index);
            _run.DominatedActionsPruned++;
            batch.Release(current.Node.Snapshot);
        }
        candidates.Add(candidate);
    }
}
