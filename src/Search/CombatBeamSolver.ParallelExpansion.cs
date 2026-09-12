using System.Diagnostics;
using System.Runtime.ExceptionServices;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private CancellationToken SearchCancellationToken => cancellationToken;
    private SearchMemoryPressureSignal SearchMemoryPressure => policy.MemoryPressureSignal;
    private object? _parallelActionReplayForkGate;

    private void ReportParallelWork(ParallelExpansionWorkProfile profile)
    {
        foreach (string distribution in profile.Describe())
            policy.Diagnostics.Info($"[CombatSolver] SEARCH_PARALLEL_WORK {distribution}");
    }

    private readonly record struct RawCardCandidate(
        SearchNode Node,
        CardType CardType,
        uint? TargetCombatId);

    private sealed class DeferredCardActionProbe(
        PreparedCardAction action,
        SimulationSnapshot snapshot) : IDisposable
    {
        private SimulationSnapshot? _snapshot = snapshot;

        public PreparedCardAction Action { get; } = action;

        public SimulationSnapshot TakeSnapshot()
            => Interlocked.Exchange(ref _snapshot, null)
                ?? throw new InvalidOperationException(
                    "并行卡牌动作的 deferred probe 已被消费或释放。");

        public void Dispose()
            => Interlocked.Exchange(ref _snapshot, null)?.ReleaseSimulator();
    }

    private sealed record PreparedCardActionEvaluation(
        ExpansionBatch? Batch,
        DeferredCardActionProbe? DeferredProbe);

    /// <summary>
    /// A parent simulator cannot be forked concurrently: prediction history seals its mutable
    /// tail and several COW containers publish a shared bit during Fork. Lanes serialize seed
    /// creation through the parent's gate; each worker then consumes only its private fork.
    /// </summary>
    private sealed class ReplayForkSeed : IDisposable
    {
        private CombatPredictionSimulator? _simulator;
        private CompactCombatCandidate? _compact;
        private ForkableSet<uint>? _processedEnemyDeaths;

        public ReplayForkSeed(CombatPredictionSimulator simulator, ForkableSet<uint> processedEnemyDeaths)
        { _simulator = simulator; _processedEnemyDeaths = processedEnemyDeaths; }

        public ReplayForkSeed(CompactCombatCandidate compact, ForkableSet<uint> processedEnemyDeaths)
        { _compact = compact; _processedEnemyDeaths = processedEnemyDeaths; }

        public ForkableSet<uint> TakeCompact(CompactCombatCandidate parent)
        {
            if (!ReferenceEquals(_compact, parent))
                throw new InvalidOperationException("Compact replay seed belongs to a different parent.");
            _compact = null;
            return Interlocked.Exchange(ref _processedEnemyDeaths, null)
                ?? throw new InvalidOperationException("Compact replay seed was already consumed.");
        }

        public (CombatPredictionSimulator Simulator, ForkableSet<uint> ProcessedEnemyDeaths) Take()
        {
            CompactCombatCandidate? compact = Interlocked.Exchange(ref _compact, null);
            CombatPredictionSimulator ownedSimulator = Interlocked.Exchange(ref _simulator, null) ?? compact?.Materialize()
                ?? throw new InvalidOperationException("并行动作 Fork seed 已被消费或释放。");
            ForkableSet<uint> ownedDeaths = Interlocked.Exchange(ref _processedEnemyDeaths, null)
                ?? throw new InvalidOperationException("并行动作死亡集合 seed 已被消费或释放。");
            return (ownedSimulator, ownedDeaths);
        }

        public void Dispose()
        {
            // Simulators do not own native resources. Clearing both roots is the explicit release
            // boundary for a seed that failed before dispatch or was canceled before consumption.
            Interlocked.Exchange(ref _simulator, null);
            Interlocked.Exchange(ref _compact, null);
            Interlocked.Exchange(ref _processedEnemyDeaths, null);
        }
    }

    private ExpansionBatch RentExpansionBatch() => new(_run.ExpansionBatchPool);

    private sealed class ExpansionBatch(
        OwnedExpansionBatch<SimulationSnapshot, RawCardCandidate, SearchNode>.Pool pool)
        : OwnedExpansionBatch<SimulationSnapshot, RawCardCandidate, SearchNode>(pool)
    {
        public void Add(RawCardCandidate candidate)
            => AddCard(candidate.Node.Snapshot, candidate);

        public void TransferTo(ExpansionBatch target, RawCardCandidate candidate)
            => base.TransferTo(target, candidate.Node.Snapshot, candidate);

        public void AddPotion(SearchNode candidate)
            => base.AddPotion(candidate.Snapshot, candidate);

        public void TransferPotionTo(ExpansionBatch target, SearchNode candidate)
            => base.TransferPotionTo(target, candidate.Snapshot, candidate);

        public void AddEndTurn(SearchNode candidate)
            => base.AddEndTurn(candidate.Snapshot, candidate);
    }

    private sealed record ExpansionWorkerOutcome(
        CombatBeamSolver? Worker,
        ExpansionBatch? Batch,
        ExceptionDispatchInfo? Error,
        long AllocatedBytes,
        long ElapsedTicks = 0);

    /// <summary>
    /// 一次 Solve 复用固定数量的后台 lane，消费已准入父节点的动作/选择作业。
    /// coordinator 独占按序提交；自然单父节点使用同一调度器，不嵌套线程池。
    /// 候选只在各 lane 内物化，transposition/dominance 仍按父节点原序提交。
    /// </summary>
    private sealed partial class ParallelExpansionExecutor : IDisposable
    {
        private readonly CombatBeamSolver _coordinator;
        private readonly ParallelExpansionWorkProfile _workProfile = new();
        private ExpansionLane[]? _backgroundLanes;
        private int _activeWorkers;
        private int _maximumActiveWorkers;
        private int _activeActionReplayWorkers;
        private int _maximumActiveActionReplayWorkers;
        private bool _disposed;

        public ParallelExpansionExecutor(CombatBeamSolver coordinator, int degreeOfParallelism)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(degreeOfParallelism, 2);
            _coordinator = coordinator;
            DegreeOfParallelism = degreeOfParallelism;
            if (coordinator._run.ActiveParallelExpansion != null)
                throw new InvalidOperationException("同一搜索不能嵌套并行执行器。");
            coordinator._run.ActiveParallelExpansion = this;
        }

        public int DegreeOfParallelism { get; }

        public int MaximumQueuedParents => SearchWaveMemoryPolicy.MaximumQueuedParents(DegreeOfParallelism);

        public ExpansionWorkerOutcome[] Evaluate(
            IReadOnlyList<SearchNode> nodes,
            Action<int, ExpansionBatch> commitOrdered)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (nodes.Count == 0)
                return [];
            if (nodes.Count > MaximumQueuedParents)
                throw new ArgumentOutOfRangeException(nameof(nodes));
            long startedAt = Stopwatch.GetTimestamp();
            long commitTicks = 0;
            try
            {
                return EvaluateQueuedParents(nodes, (index, batch) =>
                {
                    long commitStarted = Stopwatch.GetTimestamp();
                    try { commitOrdered(index, batch); }
                    finally { commitTicks += Stopwatch.GetTimestamp() - commitStarted; }
                });
            }
            finally
            {
                _workProfile.Record(ParallelExpansionWorkProfile.Kind.Wave,
                    Stopwatch.GetTimestamp() - startedAt);
                _workProfile.Record(ParallelExpansionWorkProfile.Kind.Commit, commitTicks);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _coordinator._run.ActiveParallelExpansion = null;
            if (_backgroundLanes != null)
            {
                foreach (ExpansionLane lane in _backgroundLanes)
                    lane.Dispose();
            }
            _coordinator.ReportParallelWork(_workProfile);
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
            List<ExpansionLane> lanes = new(DegreeOfParallelism);
            try
            {
                for (int index = 1; index <= DegreeOfParallelism; index++)
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

        private static long SaturatingAdd(long left, long right)
            => left > long.MaxValue - right ? long.MaxValue : left + right;

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

        private interface IExpansionLaneWorkItem
        {
            void Execute(ParallelExpansionExecutor owner, CombatBeamSolver worker);
            void Signal();
        }

        private sealed class ExpansionLane : IDisposable
        {
            private readonly ParallelExpansionExecutor _owner;
            private readonly CombatBeamSolver _worker;
            private readonly AutoResetEvent _workAvailable = new(false);
            private readonly ManualResetEventSlim _started = new(false);
            private readonly object _gate = new();
            private readonly Thread _thread;
            private IExpansionLaneWorkItem? _workItem;
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

            public void Dispatch(IExpansionLaneWorkItem workItem)
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
                        IExpansionLaneWorkItem? workItem;
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
                            workItem.Execute(_owner, _worker);
                        }
                        finally
                        {
                            workItem.Signal();
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
        if (!TryConsumeCycleExitProbeExpansionBudget(node))
        {
            _run.CycleContinuationsStopped++;
            ObserveSearchPath(node, SearchPathObservationStage.ExpansionBlocked, "cycle_exit_budget");
            return false;
        }
        _run.Expanded++;
        ObserveSearchPath(node, SearchPathObservationStage.Expanded, "parallel_parent");
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
            potionPolicyOverride: _potionPolicy,
            potionFreePolicyBaseline: _potionFreePolicyBaseline,
            maximumPotionUses: _maximumPotionUses);
        worker._run.InitialPersistentBuffValue = _run.InitialPersistentBuffValue;
        worker._run.InitialEnemyStrengthSuppression = _run.InitialEnemyStrengthSuppression;
        worker._run.InitialEnemyWeakTurns = _run.InitialEnemyWeakTurns;
        worker._run.InitialRetainedAttackValue = _run.InitialRetainedAttackValue;
        worker._run.PathDiagnosticsSolverId = _run.PathDiagnosticsSolverId;
        return worker;
    }

    private PreparedCardActionEvaluation EvaluatePreparedCardAction(
        SearchNode parent,
        PreparedCardAction action,
        ReplayForkSeed? seed,
        object replayForkGate)
    {
        ExpansionBatch batch = RentExpansionBatch();
        bool completed = false;
        try
        {
            DeferredCardActionProbe? deferredProbe = GeneratePreparedCardAction(
                parent,
                action,
                seed,
                replayForkGate,
                batch,
                allowPendingChoiceDeferral: true);
            if (deferredProbe != null)
            {
                batch.Dispose();
                completed = true;
                return new PreparedCardActionEvaluation(null, deferredProbe);
            }
            completed = true;
            return new PreparedCardActionEvaluation(batch, null);
        }
        finally
        {
            if (!completed)
                batch.Dispose();
        }
    }

    private DeferredCardActionProbe? GeneratePreparedCardAction(
        SearchNode node,
        PreparedCardAction action,
        ReplayForkSeed? seed,
        object? replayForkGate,
        ExpansionBatch batch,
        bool allowPendingChoiceDeferral)
    {
        if (_parallelActionReplayForkGate != null)
            throw new InvalidOperationException("不能嵌套并行卡牌动作 replay 上下文。");
        _parallelActionReplayForkGate = replayForkGate;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SimulationSnapshot snapshot = node.Snapshot;
            SimulationSnapshot probeSnapshot = ReplayAction(node, action.Action, seed);
            if (allowPendingChoiceDeferral
                && probeSnapshot.BoundaryReason == SearchBoundaryReason.PendingChoice)
            {
                try
                {
                    return new DeferredCardActionProbe(action, probeSnapshot);
                }
                catch
                {
                    // Ownership transfers only after the wrapper allocation succeeds. An OOM can
                    // happen before the constructor body starts, so a constructor-local catch
                    // cannot reliably release this simulator.
                    probeSnapshot.ReleaseSimulator();
                    throw;
                }
            }
            CardChoiceSpec? choiceSpec = BuildPrimaryCardChoiceSpec(probeSnapshot);
            if (choiceSpec == null && action.RequiresUnsupportedExistingChoice)
            {
                probeSnapshot.ReleaseSimulator();
                return null;
            }
            CardChoiceSpec? primaryChoiceSpec = choiceSpec
                ?? BuildRequiredEmptyChoiceSpec(action.RequiredEmptyChoice);
            IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> resolvedBranches =
                HasChoiceBeforePrimary(probeSnapshot, primaryChoiceSpec)
                    ? ResolveRoundChoiceBranches(
                        node,
                        action.Action,
                        probeSnapshot,
                        BuildPrimaryChoiceMatch(primaryChoiceSpec),
                        budgetPrimaryChoiceSpec: primaryChoiceSpec)
                    : ResolvePrimaryCardChoiceBranches(
                        node,
                        action.Action,
                        probeSnapshot,
                        choiceSpec,
                        action.RequiredEmptyChoice);
            AddResolvedCardCandidates(node, action, resolvedBranches, batch);
            return null;
        }
        finally
        {
            _parallelActionReplayForkGate = null;
        }
    }

    private void AddResolvedCardCandidates(
        SearchNode node,
        PreparedCardAction action,
        IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> resolvedBranches,
        ExpansionBatch batch)
    {
        SimulationSnapshot snapshot = node.Snapshot;
        foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in resolvedBranches)
        {
            bool published = false;
            try
            {
                bool forcedTurnEnd = finalSnapshot.Turn > node.Turn;
                PlanAction nodeAction = finalAction with { EndsPlayerTurn = forcedTurnEnd };
                bool terminal = finalSnapshot.PlayerDead
                    || finalSnapshot.AllEnemiesDead
                    || finalSnapshot.BoundaryReason != SearchBoundaryReason.None;
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
                        : node.CombatProgress)
                {
                    CumulativeEnemyHpLost = AccumulateEnemyHpLost(node, finalSnapshot),
                };
                child = AttachCycleSchedulingEvidence(child);
                batch.Add(new RawCardCandidate(
                    child,
                    action.CardType,
                    action.TargetCombatId));
                published = true;
            }
            finally
            {
                if (!published)
                    finalSnapshot.ReleaseSimulator();
            }
        }
    }

    private PrimaryChoiceReplayFrontier? ResolveDeferredRoundChoiceAction(
        SearchNode node,
        DeferredCardActionProbe deferredProbe,
        ExpansionBatch completedBatch)
    {
        // Physical-occurrence slots are selected only after every semantic branch of this action
        // has had a chance to expose an identity-sensitive frontier. Keep that two-phase collector
        // with one exclusive choice-job owner: different actions may run on different lanes,
        // while each action uses the same ordered algorithm as DOP1 without shared atomic quota.
        _run.DeferredRoundChoiceActions++;
        PreparedCardAction preparedAction = deferredProbe.Action;
        SimulationSnapshot? snapshot = deferredProbe.TakeSnapshot();
        try
        {
            CardChoiceSpec? choiceSpec = BuildPrimaryCardChoiceSpec(snapshot);
            if (choiceSpec == null && preparedAction.RequiresUnsupportedExistingChoice)
            {
                snapshot.ReleaseSimulator();
                snapshot = null;
                RecordDeferredRoundChoiceLayer(width: 0);
                return null;
            }

            CardChoiceSpec? primaryChoiceSpec = choiceSpec
                ?? BuildRequiredEmptyChoiceSpec(preparedAction.RequiredEmptyChoice);
            IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> resolvedBranches;
            SimulationSnapshot ownedSnapshot = snapshot;
            if (HasChoiceBeforePrimary(ownedSnapshot, primaryChoiceSpec))
            {
                resolvedBranches = ResolveRoundChoiceBranches(
                    node,
                    preparedAction.Action,
                    ownedSnapshot,
                    BuildPrimaryChoiceMatch(primaryChoiceSpec),
                    budgetPrimaryChoiceSpec: primaryChoiceSpec);
                RecordDeferredRoundChoiceLayer(
                    width: 1,
                    finitePendingFallback: true);
            }
            else
            {
                PrimaryCardChoiceLayer layer = BuildPrimaryCardChoiceLayer(
                    preparedAction.Action,
                    ownedSnapshot,
                    choiceSpec,
                    preparedAction.RequiredEmptyChoice);
                if (layer.UnregisteredPendingChoice)
                {
                    throw new InvalidOperationException(
                        $"卡牌 {preparedAction.Action.CardId} 产生了未登记的分支选择，" +
                        "不能静默回退到原生重扫。");
                }
                RecordDeferredRoundChoiceLayer(layer.Choices.Count, finitePrimaryLayer: true);
                PrimaryChoiceReplayFrontier? frontier = PreparePrimaryChoiceReplays(
                    layer, card: preparedAction);
                if (frontier != null)
                {
                    snapshot.ReleaseSimulator();
                    snapshot = null;
                    return frontier;
                }
                resolvedBranches = ResolvePrimaryCardChoiceLayer(
                    node,
                    preparedAction.Action,
                    ownedSnapshot,
                    layer);
            }

            snapshot = null; // The iterator now owns the original probe, including early failure.
            AddResolvedCardCandidates(
                node,
                preparedAction,
                resolvedBranches,
                completedBatch);
            return null;
        }
        finally
        {
            snapshot?.ReleaseSimulator();
        }
    }


    private void RecordDeferredRoundChoiceLayer(
        int width,
        bool finitePrimaryLayer = false,
        bool finitePendingFallback = false)
    {
        _run.DeferredRoundChoiceLayerWidthTotal += width;
        _run.MaxDeferredRoundChoiceLayerWidth = Math.Max(
            _run.MaxDeferredRoundChoiceLayerWidth,
            width);
        if (finitePrimaryLayer)
            _run.DeferredRoundChoiceFinitePrimaryLayers++;
        if (finitePendingFallback)
        {
            _run.DeferredRoundChoiceFinitePendingFallbacks++;
            // Compatibility metric: after finite direct-primary layers became independently
            // parallelizable, only finite pending/HasChoiceBeforePrimary layers remain fallbacks.
            _run.DeferredRoundChoiceFiniteQuotaFallbacks++;
        }
    }


    private void GenerateRawPotionCandidates(SearchNode node, ExpansionBatch batch)
    {
        foreach (PreparedPotionAction action in PreparePotionActions(node))
        {
            if (GeneratePreparedPotionAction(node, action, batch) != null)
                throw new InvalidOperationException("串行药水展开意外返回了并行选择 frontier。");
        }
    }

    private PrimaryChoiceReplayFrontier? GeneratePreparedPotionAction(
        SearchNode node,
        PreparedPotionAction action,
        ExpansionBatch batch,
        bool allowPrimaryReplays = false)
    {
        SimulationSnapshot snapshot = node.Snapshot;
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
        PlanAction baseAction = action.Action;
        PotionModel potion = action.Potion;
        SimulationSnapshot? probeSnapshot = null;
        IReadOnlyList<PlanCardChoice?> choices;
        CardChoiceSpec? choiceSpec = null;
        if (PotionChoiceSupport.RequiresChoice(potion))
        {
            CombatPredictionSimulator choiceSimulator = simulator;
            if (PotionChoiceSupport.GeneratesCardChoice(potion))
            {
                probeSnapshot = ReplayAction(node, baseAction);
                choiceSimulator = (CombatPredictionSimulator)probeSnapshot.Simulator;
            }
            choiceSpec = PotionChoiceSupport.GetSpec(choiceSimulator, potion);
            choices = CardChoiceSupport.BuildChoices(
                    choiceSpec,
                    displayNames,
                    _profile.MaxPileChoiceBranchesPerAction,
                    _profile.MaxHandChoiceBranchesPerAction)
                .Select(choice => choice with { SourceId = potion.Id.Entry })
                .Cast<PlanCardChoice?>()
                .ToList();
            probeSnapshot?.ReleaseSimulator();
            probeSnapshot = null;
        }
        else
        {
            probeSnapshot = ReplayAction(node, baseAction);
            choices = [null];
        }
        if (allowPrimaryReplays && choices.Count >= 2)
        {
            bool identityChangingLayer = choiceSpec != null
                && CardChoiceSupport.IsIdentityChangingPersistentChoiceEffect(choiceSpec.Effect);
            int semanticCount = identityChangingLayer
                ? CardChoiceSupport.CountSemanticChoices(
                    choices.Where(choice => choice != null).Cast<PlanCardChoice>().ToList())
                : choices.Count;
            PrimaryCardChoiceLayer layer = new(choices, UnregisteredPendingChoice: false,
                semanticCount, identityChangingLayer,
                CreateWholeActionChoiceBudget(choiceSpec, semanticCount));
            PrimaryChoiceReplayFrontier? frontier = PreparePrimaryChoiceReplays(layer, potion: action);
            if (frontier != null)
            {
                probeSnapshot?.ReleaseSimulator();
                return frontier;
            }
        }
        AddResolvedPotionCandidates(node,
            ResolveExplicitCardChoiceBranches(node, baseAction, probeSnapshot, choices, choiceSpec),
            batch);
        return null;
    }

    private void AddResolvedPotionCandidates(
        SearchNode node,
        IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> resolvedBranches,
        ExpansionBatch batch)
    {
        SimulationSnapshot snapshot = node.Snapshot;
        foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in resolvedBranches)
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
                node.CombatProgress)
            {
                CumulativeEnemyHpLost = AccumulateEnemyHpLost(node, finalSnapshot),
            };
            child = AttachCycleSchedulingEvidence(child);
            batch.AddPotion(child);
        }
    }

    private void GenerateRawEndTurnCandidates(SearchNode node, ExpansionBatch batch)
    {
        SimulationSnapshot snapshot = node.Snapshot;
        if (snapshot.PlayerDead || snapshot.AllEnemiesDead)
            return;

        List<CrossTurnStandPatBaseline>? directStandPatBaselines =
            ReferenceEquals(FindTurnStart(node), node)
            ? []
            : null;
        foreach ((PlanAction endAction, SimulationSnapshot endSnapshot) in BuildEndTurnBranches(node, []))
        {
            int nextTurn = endSnapshot.Turn;
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
                node.CombatProgress.Advance(endSnapshot))
            {
                CumulativeEnemyHpLost = AccumulateEnemyHpLost(node, endSnapshot),
            };
            endNode = AttachCycleSchedulingEvidence(endNode);
            if (directStandPatBaselines != null
                && IsComparableCrossTurnOutcome(endSnapshot.BoundaryReason))
            {
                directStandPatBaselines.Add(new CrossTurnStandPatBaseline(
                    endNode.StateKey,
                    MeasureCycleExitQuality(node, endNode)));
            }
            batch.AddEndTurn(endNode);
        }
        if (directStandPatBaselines != null)
            PublishCrossTurnStandPatBaselines(node, directStandPatBaselines);
    }

    private void MergeExpansionWorker(ExpansionWorkerOutcome outcome)
        => MergeExpansionWorker(outcome.Worker, outcome.AllocatedBytes);

    private void MergeExpansionWorker(CombatBeamSolver? worker, long allocatedBytes)
    {
        if (worker == null)
            return;
        _run.OffThreadAllocatedBytes += allocatedBytes;
        SearchRunContext source = worker._run;
        _run.DuplicateCardBranchesPruned += source.DuplicateCardBranchesPruned;
        _run.ActionAdmissionRepresentativesProtected +=
            source.ActionAdmissionRepresentativesProtected;
        _run.ChoiceBranchesEvaluated += source.ChoiceBranchesEvaluated;
        _run.ChoiceReplayAttempts += source.ChoiceReplayAttempts;
        _run.ChoiceReplayBudgetExhaustions += source.ChoiceReplayBudgetExhaustions;
        _run.ChoiceBranchesDroppedByBudget += source.ChoiceBranchesDroppedByBudget;
        _run.ShuffleBranchesPruned += source.ShuffleBranchesPruned;
        _run.SoldHpBranchesPruned += source.SoldHpBranchesPruned;
        _run.HpInvestmentBranchesProtected += source.HpInvestmentBranchesProtected;
        _run.ReplayCount += source.ReplayCount;
        _run.ForkCount += source.ForkCount;
        _run.RoundPrefixCaptures += source.RoundPrefixCaptures;
        _run.RoundPrefixResumes += source.RoundPrefixResumes;
        _run.TransitionCount += source.TransitionCount;
        _run.RepeatableNoProgressBranchesPruned +=
            source.RepeatableNoProgressBranchesPruned;
        _run.CycleShapesDetected += source.CycleShapesDetected;
        _run.CycleProbeContinuationsExpanded += source.CycleProbeContinuationsExpanded;
        _run.CycleCandidatesProtected += source.CycleCandidatesProtected;
        _run.CycleContinuationsStopped += source.CycleContinuationsStopped;
        _run.CrossTurnCandidatesProtected += source.CrossTurnCandidatesProtected;
        _run.CrossTurnContinuationsStopped += source.CrossTurnContinuationsStopped;
        _run.StandPatProbes += source.StandPatProbes;
        _run.DeferredRoundChoiceActions += source.DeferredRoundChoiceActions;
        _run.DeferredRoundChoiceLayerWidthTotal +=
            source.DeferredRoundChoiceLayerWidthTotal;
        _run.MaxDeferredRoundChoiceLayerWidth = Math.Max(
            _run.MaxDeferredRoundChoiceLayerWidth,
            source.MaxDeferredRoundChoiceLayerWidth);
        _run.DeferredRoundChoiceFiniteQuotaFallbacks +=
            source.DeferredRoundChoiceFiniteQuotaFallbacks;
        _run.DeferredRoundChoiceFinitePrimaryLayers +=
            source.DeferredRoundChoiceFinitePrimaryLayers;
        _run.DeferredRoundChoiceFinitePendingFallbacks +=
            source.DeferredRoundChoiceFinitePendingFallbacks;
        source.DuplicateCardBranchesPruned = 0;
        source.ActionAdmissionRepresentativesProtected = 0;
        source.ChoiceBranchesEvaluated = 0;
        source.ChoiceReplayAttempts = 0;
        source.ChoiceReplayBudgetExhaustions = 0;
        source.ChoiceBranchesDroppedByBudget = 0;
        source.ShuffleBranchesPruned = 0;
        source.SoldHpBranchesPruned = 0;
        source.HpInvestmentBranchesProtected = 0;
        source.ReplayCount = 0;
        source.ForkCount = 0;
        source.RoundPrefixCaptures = 0;
        source.RoundPrefixResumes = 0;
        source.TransitionCount = 0;
        source.RepeatableNoProgressBranchesPruned = 0;
        source.CycleShapesDetected = 0;
        source.CycleProbeContinuationsExpanded = 0;
        source.CycleCandidatesProtected = 0;
        source.CycleContinuationsStopped = 0;
        source.CrossTurnCandidatesProtected = 0;
        source.CrossTurnContinuationsStopped = 0;
        source.StandPatProbes = 0;
        source.DeferredRoundChoiceActions = 0;
        source.DeferredRoundChoiceLayerWidthTotal = 0;
        source.MaxDeferredRoundChoiceLayerWidth = 0;
        source.DeferredRoundChoiceFiniteQuotaFallbacks = 0;
        source.DeferredRoundChoiceFinitePrimaryLayers = 0;
        source.DeferredRoundChoiceFinitePendingFallbacks = 0;
        _run.Performance.DrainFrom(source.Performance);
        _run.WorkPacer.DrainFrom(source.WorkPacer);
    }

    private void CommitExpansionBatch(
        SearchNode parent,
        ExpansionBatch batch,
        Action<SearchNode> acceptChild)
    {
        List<ActionCandidate> nonDominated = new(16);
        List<ActionCandidate>? deferredCycleCandidates = null;
        foreach (RawCardCandidate raw in batch.Cards)
        {
            PromoteOrderedMutationProgressTail(raw.Node);
            CommitCycleExitObservation(raw.Node);
            if (ShouldPruneCrossTurnNoProgress(raw.Node))
            {
                _run.RepeatableNoProgressBranchesPruned++;
                batch.Release(raw.Node.Snapshot);
                continue;
            }
            ActionCandidate actionCandidate = BuildCandidate(
                parent.Snapshot,
                raw.Node.Snapshot,
                raw.Node,
                raw.CardType,
                raw.TargetCombatId);
            if (CanRetainOrderedMutationLease(_run, raw.Node))
            {
                nonDominated.Add(actionCandidate);
                continue;
            }
            if (ShouldDeferCycleTranspositionUntilActionAdmission(raw.Node))
            {
                deferredCycleCandidates ??= [];
                deferredCycleCandidates.Add(actionCandidate);
                continue;
            }
            if (!TryAcceptTransposition(raw.Node))
            {
                batch.Release(raw.Node.Snapshot);
                continue;
            }
            AddNonDominatedParallelCandidate(
                nonDominated,
                actionCandidate,
                batch);
        }

        PruneCommittedCrossTurnCandidates(batch.Potions, batch);
        PruneCommittedCrossTurnCandidates(batch.EndTurns, batch);
        CommitDeferredCycleCandidates(
            nonDominated,
            deferredCycleCandidates,
            batch);
        if (NeedsCycleExitAdmission(parent, nonDominated, batch.Potions, batch.EndTurns))
        {
            SearchNode[] directChildren = nonDominated.Select(candidate => candidate.Node)
                .Concat(batch.Potions)
                .Concat(batch.EndTurns)
                .ToArray();
            AnnotateCycleExitProgress(parent, directChildren);
            _ = MaterializeAdmittedCycleExitObservation(
                directChildren,
                _run.CycleFamilyLedger);
        }
        List<ActionCandidate> queuedCandidates = SelectActionCandidates(parent, nonDominated);
        AdmitCycleProbeCandidate(nonDominated, queuedCandidates);
        AdmitCycleExitProbeCandidate(nonDominated, queuedCandidates);
        for (int index = queuedCandidates.Count - 1; index >= 0; index--)
        {
            ActionCandidate candidate = queuedCandidates[index];
            if (!ShouldRejectCycleCandidate(candidate.Node))
                continue;
            queuedCandidates.RemoveAt(index);
            nonDominated.RemoveAll(item => ReferenceEquals(item.Node, candidate.Node));
            batch.Release(candidate.Node.Snapshot);
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
            EnsureBoundedCycleProbeLease(child);
            if (ShouldRejectCycleCandidate(child)
                || !TryAcceptTransposition(child))
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

    private void PruneCommittedCrossTurnCandidates(
        List<SearchNode> candidates,
        ExpansionBatch batch)
    {
        int writeIndex = 0;
        for (int readIndex = 0; readIndex < candidates.Count; readIndex++)
        {
            SearchNode child = candidates[readIndex];
            PromoteOrderedMutationProgressTail(child);
            CommitCycleExitObservation(child);
            if (ShouldPruneCrossTurnNoProgress(child))
            {
                _run.RepeatableNoProgressBranchesPruned++;
                batch.Release(child.Snapshot);
                continue;
            }
            candidates[writeIndex++] = child;
        }
        if (writeIndex < candidates.Count)
            candidates.RemoveRange(writeIndex, candidates.Count - writeIndex);
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
