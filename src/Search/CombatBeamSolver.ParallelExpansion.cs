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
    private CancellationToken SearchCancellationToken => cancellationToken;
    private SearchMemoryPressureSignal SearchMemoryPressure => policy.MemoryPressureSignal;
    private object? _parallelActionReplayForkGate;

    private readonly record struct RawCardCandidate(
        SearchNode Node,
        CardType CardType,
        uint? TargetCombatId);

    private readonly record struct PreparedCardAction(
        PlanAction Action,
        CardModel OriginalCard,
        CardType CardType,
        uint? TargetCombatId,
        bool RequiresUnsupportedExistingChoice,
        PlanCardChoice? RequiredEmptyChoice);

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

    private sealed record DeferredRoundChoiceFrontier(
        PreparedCardAction PreparedAction,
        IReadOnlyList<PendingChoiceReplayBranch> Branches,
        PrimaryChoiceMatch? UnresolvedPrimaryChoice,
        int MaxFinalBranchesPerBranch);

    private sealed record PreparedCardActionEvaluation(
        ExpansionBatch? Batch,
        DeferredCardActionProbe? DeferredProbe);

    /// <summary>
    /// A parent simulator cannot be forked concurrently: prediction history seals its mutable
    /// tail and several COW containers publish a shared bit during Fork. The coordinator creates
    /// one seed at a time; exactly one worker then consumes and mutates that private fork.
    /// </summary>
    private sealed class ReplayForkSeed(
        CombatPredictionSimulator simulator,
        ForkableSet<uint> processedEnemyDeaths) : IDisposable
    {
        private CombatPredictionSimulator? _simulator = simulator;
        private ForkableSet<uint>? _processedEnemyDeaths = processedEnemyDeaths;

        public (CombatPredictionSimulator Simulator, ForkableSet<uint> ProcessedEnemyDeaths) Take()
        {
            CombatPredictionSimulator ownedSimulator = Interlocked.Exchange(ref _simulator, null)
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
            Interlocked.Exchange(ref _processedEnemyDeaths, null);
        }
    }

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

        public void Adopt(RawCardCandidate candidate)
        {
            Own(candidate.Node.Snapshot);
            try
            {
                Cards.Add(candidate);
            }
            catch
            {
                _owned.Remove(candidate.Node.Snapshot);
                throw;
            }
        }

        public void TransferTo(ExpansionBatch target, RawCardCandidate candidate)
        {
            if (!_owned.Contains(candidate.Node.Snapshot))
                throw new InvalidOperationException("并行展开快照没有可移交的所有权。");
            target.Adopt(candidate);
            if (!_owned.Remove(candidate.Node.Snapshot))
            {
                target.Release(candidate.Node.Snapshot);
                throw new InvalidOperationException("并行展开快照移交时丢失所有权。");
            }
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
            if (_owned.Contains(snapshot))
            {
                // Register the transfer before dropping batch ownership. If growing the
                // bookkeeping set throws (for example under memory pressure), Dispose still
                // owns and releases the simulator instead of leaking it across the failed wave.
                _transferred.Add(snapshot);
                _owned.Remove(snapshot);
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
            Cards.Clear();
            Potions.Clear();
            EndTurns.Clear();
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

    private sealed record ActionReplayWorkerOutcome(
        CombatBeamSolver? Worker,
        ExpansionBatch? Batch,
        DeferredCardActionProbe? DeferredProbe,
        ExceptionDispatchInfo? Error,
        long WorkerAllocatedBytes,
        long OutcomeAllocatedBytes);

    /// <summary>
    /// 一次 Solve 复用固定数量的后台 lane；coordinator 自己执行 lane 0，避免为每个父节点
    /// 创建 Task 和 worker。候选只在各 lane 内物化，transposition/dominance 仍由 coordinator
    /// 按父节点原顺序提交，因此 DOP 不改变搜索结果。
    /// </summary>
    private sealed class ParallelExpansionExecutor : IDisposable
    {
        private const long InitialActionReplayAllocationHighWater = 16L * 1024 * 1024;
        private const long InitialRoundChoiceReplayAllocationHighWater = 16L * 1024 * 1024;

        private readonly CombatBeamSolver _coordinator;
        private readonly object _actionReplayForkGate = new();
        private ExpansionLane[]? _backgroundLanes;
        private long _actionReplayAllocatedHighWater;
        private long _roundChoiceReplayAllocatedHighWater;
        private bool _actionReplayAllocationObserved;
        private bool _roundChoiceReplayAllocationObserved;
        private int _activeWorkers;
        private int _maximumActiveWorkers;
        private int _activeActionReplayWorkers;
        private int _maximumActiveActionReplayWorkers;
        private int _activeRoundChoiceReplayWorkers;
        private int _maximumActiveRoundChoiceReplayWorkers;
        private bool _disposed;

        public ParallelExpansionExecutor(CombatBeamSolver coordinator, int degreeOfParallelism)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(degreeOfParallelism, 2);
            _coordinator = coordinator;
            DegreeOfParallelism = degreeOfParallelism;
        }

        public int DegreeOfParallelism { get; }

        public ExpansionWorkerOutcome[] Evaluate(
            IReadOnlyList<SearchNode> nodes,
            bool enableSingleParentActionReplay)
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
                includeWorkerMetrics: false,
                actionReplayExecutor: enableSingleParentActionReplay && nodes.Count == 1
                    ? this
                    : null);
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
            bool includeWorkerMetrics,
            ParallelExpansionExecutor? actionReplayExecutor = null)
        {
            int activeWorkers = Interlocked.Increment(ref _activeWorkers);
            UpdateMaximum(ref _maximumActiveWorkers, activeWorkers);
            long allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                ExpansionBatch batch = worker.EvaluateRawExpansion(node, actionReplayExecutor);
                long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart;
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
                long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart;
                wave.Outcomes[outcomeIndex] = new ExpansionWorkerOutcome(
                    includeWorkerMetrics ? worker : null,
                    null,
                    ExceptionDispatchInfo.Capture(error),
                    allocatedBytes);
            }
            finally
            {
                Interlocked.Decrement(ref _activeWorkers);
            }
        }

        public ExpansionBatch EvaluateCardActions(
            SearchNode parent,
            IReadOnlyList<PreparedCardAction> actions)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (actions.Count < 2)
            {
                throw new ArgumentException(
                    "并行卡牌动作回放要求至少两个已预枚举 action/target。",
                    nameof(actions));
            }

            ExpansionLane[] backgroundLanes = EnsureBackgroundLanes();
            ExpansionBatch aggregate = new();
            bool completed = false;
            try
            {
                int actionIndex = 0;
                while (actionIndex < actions.Count)
                {
                    _coordinator.SearchCancellationToken.ThrowIfCancellationRequested();
                    int workItemCount = ResolveActionReplayMicrobatchCapacity(
                        actions.Count - actionIndex);
                    using ActionReplayWave wave = new(workItemCount);

                    for (int offset = 0; offset < workItemCount; offset++)
                    {
                        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                        try
                        {
                            ReplayForkSeed seed = _coordinator.PrepareReplayForkSeed(
                                parent.Snapshot,
                                _actionReplayForkGate);
                            wave.SetSeed(
                                offset,
                                seed,
                                Math.Max(
                                    0,
                                    GC.GetAllocatedBytesForCurrentThread() - allocatedBefore));
                        }
                        catch (System.Exception error)
                        {
                            long seedAllocatedBytes = Math.Max(
                                0,
                                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                            wave.Outcomes[offset] = new ActionReplayWorkerOutcome(
                                null,
                                null,
                                null,
                                ExceptionDispatchInfo.Capture(error),
                                WorkerAllocatedBytes: 0,
                                OutcomeAllocatedBytes: seedAllocatedBytes);
                            wave.StopDispatchAt(offset + 1);
                            break;
                        }
                    }

                    for (int offset = 1; offset < wave.OutcomeCount; offset++)
                    {
                        if (wave.Outcomes[offset] != null)
                            break;
                        ReplayForkSeed? seed = wave.TakeSeed(offset);
                        bool backgroundRegistered = false;
                        try
                        {
                            wave.RegisterBackgroundWork();
                            backgroundRegistered = true;
                            backgroundLanes[offset - 1].Dispatch(
                                new ActionReplayWorkItem(
                                    parent,
                                    actions[actionIndex + offset],
                                    seed,
                                    wave,
                                    offset));
                            seed = null;
                        }
                        catch (System.Exception error)
                        {
                            seed?.Dispose();
                            if (backgroundRegistered)
                                wave.CancelBackgroundRegistration();
                            wave.Outcomes[offset] = new ActionReplayWorkerOutcome(
                                null,
                                null,
                                null,
                                ExceptionDispatchInfo.Capture(error),
                                WorkerAllocatedBytes: 0,
                                OutcomeAllocatedBytes: wave.SeedAllocatedBytes[offset]);
                            wave.StopDispatchAt(offset + 1);
                            break;
                        }
                    }

                    if (wave.Outcomes[0] == null)
                    {
                        ReplayForkSeed seed = wave.TakeSeed(0);
                        ExecuteActionReplay(
                            _coordinator,
                            parent,
                            actions[actionIndex],
                            seed,
                            wave,
                            outcomeIndex: 0,
                            includeWorkerMetrics: false,
                            trackActiveWorker: false);
                    }
                    wave.CompleteDispatch();
                    wave.BackgroundCompleted.Wait();
                    int executedWorkItems = wave.ExecutedWorkItemCount;
                    try
                    {
                        if (executedWorkItems > 1)
                        {
                            _coordinator._run.ParallelExpansionWaves++;
                            _coordinator._run.ParallelExpansionWorkItems += executedWorkItems;
                            _coordinator._run.ParallelActionReplayWaves++;
                            _coordinator._run.ParallelActionReplayWorkItems += executedWorkItems;
                            _coordinator._run.MaxParallelExpansionConcurrency = Math.Max(
                                _coordinator._run.MaxParallelExpansionConcurrency,
                                Volatile.Read(ref _maximumActiveWorkers));
                            _coordinator._run.MaxParallelActionReplayConcurrency = Math.Max(
                                _coordinator._run.MaxParallelActionReplayConcurrency,
                                Volatile.Read(ref _maximumActiveActionReplayWorkers));
                        }

                        for (int offset = 0; offset < wave.OutcomeCount; offset++)
                        {
                            ActionReplayWorkerOutcome? outcome = wave.Outcomes[offset];
                            if (outcome != null)
                            {
                                _coordinator.MergeExpansionWorker(
                                    outcome.Worker,
                                    outcome.WorkerAllocatedBytes);
                            }
                        }

                        ObserveActionReplayAllocation(wave.Outcomes, wave.OutcomeCount);
                        for (int offset = 0; offset < wave.OutcomeCount; offset++)
                        {
                            ActionReplayWorkerOutcome outcome = wave.Outcomes[offset]
                                ?? throw new InvalidOperationException(
                                    "并行卡牌动作没有返回 worker outcome。");
                            outcome.Error?.Throw();
                            if (outcome.Batch != null && outcome.DeferredProbe != null)
                            {
                                throw new InvalidOperationException(
                                    "并行卡牌动作同时返回了候选批次与 deferred probe。");
                            }
                            using ExpansionBatch? deferredBatch = outcome.DeferredProbe == null
                                ? null
                                : EvaluateDeferredCardAction(parent, outcome.DeferredProbe);
                            ExpansionBatch batch = outcome.Batch
                                ?? deferredBatch
                                ?? throw new InvalidOperationException(
                                    "并行卡牌动作既没有候选批次，也没有 deferred probe。");
                            foreach (RawCardCandidate candidate in batch.Cards)
                                batch.TransferTo(aggregate, candidate);
                        }
                    }
                    finally
                    {
                        foreach (ActionReplayWorkerOutcome? outcome in wave.Outcomes)
                        {
                            outcome?.Batch?.Dispose();
                            outcome?.DeferredProbe?.Dispose();
                        }
                    }

                    actionIndex += wave.OutcomeCount;
                }

                completed = true;
                return aggregate;
            }
            finally
            {
                if (!completed)
                    aggregate.Dispose();
            }
        }

        private ExpansionBatch EvaluateDeferredCardAction(
            SearchNode parent,
            DeferredCardActionProbe deferredProbe)
        {
            ExpansionBatch aggregate = new();
            bool completed = false;
            try
            {
                DeferredRoundChoiceFrontier? frontier =
                    _coordinator.PrepareDeferredRoundChoiceFrontier(
                        parent,
                        deferredProbe,
                        aggregate);
                if (frontier == null)
                {
                    completed = true;
                    return aggregate;
                }

                ExpansionLane[] backgroundLanes = EnsureBackgroundLanes();
                int branchIndex = 0;
                while (branchIndex < frontier.Branches.Count)
                {
                    _coordinator.SearchCancellationToken.ThrowIfCancellationRequested();
                    int workItemCount = ResolveRoundChoiceReplayMicrobatchCapacity(
                        frontier.Branches.Count - branchIndex);
                    if (workItemCount < 2)
                    {
                        List<PendingChoiceReplayBranch> remainingBranches = new(
                            frontier.Branches.Count - branchIndex);
                        for (; branchIndex < frontier.Branches.Count; branchIndex++)
                            remainingBranches.Add(frontier.Branches[branchIndex]);
                        _coordinator.AddResolvedCardCandidates(
                            parent,
                            frontier.PreparedAction,
                            _coordinator.ResolveDeferredRoundChoiceBranchesSerially(
                                parent,
                                frontier,
                                remainingBranches),
                            aggregate);
                        break;
                    }

                    using ActionReplayWave wave = new(workItemCount);
                    for (int offset = 0; offset < workItemCount; offset++)
                    {
                        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                        try
                        {
                            ReplayForkSeed seed = _coordinator.PrepareReplayForkSeed(
                                parent.Snapshot,
                                _actionReplayForkGate);
                            wave.SetSeed(
                                offset,
                                seed,
                                Math.Max(
                                    0,
                                    GC.GetAllocatedBytesForCurrentThread() - allocatedBefore));
                        }
                        catch (System.Exception error)
                        {
                            long seedAllocatedBytes = Math.Max(
                                0,
                                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                            wave.Outcomes[offset] = new ActionReplayWorkerOutcome(
                                null,
                                null,
                                null,
                                ExceptionDispatchInfo.Capture(error),
                                WorkerAllocatedBytes: 0,
                                OutcomeAllocatedBytes: seedAllocatedBytes);
                            wave.StopDispatchAt(offset + 1);
                            break;
                        }
                    }

                    for (int offset = 1; offset < wave.OutcomeCount; offset++)
                    {
                        if (wave.Outcomes[offset] != null)
                            break;
                        ReplayForkSeed? seed = wave.TakeSeed(offset);
                        bool backgroundRegistered = false;
                        try
                        {
                            wave.RegisterBackgroundWork();
                            backgroundRegistered = true;
                            backgroundLanes[offset - 1].Dispatch(
                                new RoundChoiceReplayWorkItem(
                                    parent,
                                    frontier,
                                    frontier.Branches[branchIndex + offset],
                                    seed,
                                    wave,
                                    offset));
                            seed = null;
                        }
                        catch (System.Exception error)
                        {
                            seed?.Dispose();
                            if (backgroundRegistered)
                                wave.CancelBackgroundRegistration();
                            wave.Outcomes[offset] = new ActionReplayWorkerOutcome(
                                null,
                                null,
                                null,
                                ExceptionDispatchInfo.Capture(error),
                                WorkerAllocatedBytes: 0,
                                OutcomeAllocatedBytes: wave.SeedAllocatedBytes[offset]);
                            wave.StopDispatchAt(offset + 1);
                            break;
                        }
                    }

                    if (wave.Outcomes[0] == null)
                    {
                        ReplayForkSeed seed = wave.TakeSeed(0);
                        ExecuteRoundChoiceReplay(
                            _coordinator,
                            parent,
                            frontier,
                            frontier.Branches[branchIndex],
                            seed,
                            wave,
                            outcomeIndex: 0,
                            includeWorkerMetrics: false,
                            trackActiveWorker: false);
                    }
                    wave.CompleteDispatch();
                    wave.BackgroundCompleted.Wait();
                    int executedWorkItems = wave.ExecutedWorkItemCount;
                    try
                    {
                        if (executedWorkItems > 1)
                        {
                            _coordinator._run.ParallelExpansionWaves++;
                            _coordinator._run.ParallelExpansionWorkItems += executedWorkItems;
                            _coordinator._run.ParallelRoundChoiceReplayWaves++;
                            _coordinator._run.ParallelRoundChoiceReplayWorkItems += executedWorkItems;
                            _coordinator._run.MaxParallelExpansionConcurrency = Math.Max(
                                _coordinator._run.MaxParallelExpansionConcurrency,
                                Volatile.Read(ref _maximumActiveWorkers));
                            _coordinator._run.MaxParallelRoundChoiceReplayConcurrency = Math.Max(
                                _coordinator._run.MaxParallelRoundChoiceReplayConcurrency,
                                Volatile.Read(ref _maximumActiveRoundChoiceReplayWorkers));
                        }

                        for (int offset = 0; offset < wave.OutcomeCount; offset++)
                        {
                            ActionReplayWorkerOutcome? outcome = wave.Outcomes[offset];
                            if (outcome != null)
                            {
                                _coordinator.MergeExpansionWorker(
                                    outcome.Worker,
                                    outcome.WorkerAllocatedBytes);
                            }
                        }
                        ObserveRoundChoiceReplayAllocation(wave.Outcomes, wave.OutcomeCount);
                        for (int offset = 0; offset < wave.OutcomeCount; offset++)
                        {
                            ActionReplayWorkerOutcome outcome = wave.Outcomes[offset]
                                ?? throw new InvalidOperationException(
                                    "并行 round-choice 没有返回 worker outcome。");
                            outcome.Error?.Throw();
                            if (outcome.DeferredProbe != null)
                            {
                                throw new InvalidOperationException(
                                    "round-choice worker 不得再次请求嵌套并行。");
                            }
                            ExpansionBatch batch = outcome.Batch
                                ?? throw new InvalidOperationException(
                                    "并行 round-choice 没有返回候选批次。");
                            foreach (RawCardCandidate candidate in batch.Cards)
                                batch.TransferTo(aggregate, candidate);
                        }
                    }
                    finally
                    {
                        foreach (ActionReplayWorkerOutcome? outcome in wave.Outcomes)
                        {
                            outcome?.Batch?.Dispose();
                            outcome?.DeferredProbe?.Dispose();
                        }
                    }

                    branchIndex += wave.OutcomeCount;
                }

                completed = true;
                return aggregate;
            }
            finally
            {
                if (!completed)
                    aggregate.Dispose();
            }
        }

        private int ResolveRoundChoiceReplayMicrobatchCapacity(int remainingBranches)
        {
            if (remainingBranches < 2)
                return 0;
            int capacity = Math.Min(DegreeOfParallelism, remainingBranches);
            SearchMemoryPressureSignal signal = _coordinator.SearchMemoryPressure;
            if (!signal.IsEnabled)
                return capacity;

            if (!_roundChoiceReplayAllocationObserved)
                capacity = Math.Min(capacity, 2);
            long observedHighWater = _roundChoiceReplayAllocationObserved
                ? Math.Max(1, Volatile.Read(ref _roundChoiceReplayAllocatedHighWater))
                : InitialRoundChoiceReplayAllocationHighWater;
            long reserve = AddAllocationSafetyMargin(observedHighWater);
            long remainingBytes = signal.RemainingBytes;
            if (remainingBytes != long.MaxValue && remainingBytes < reserve)
                return 0;
            int memoryCapacity = remainingBytes == long.MaxValue
                ? capacity
                : (int)Math.Min(capacity, remainingBytes / reserve);
            return memoryCapacity >= 2 ? memoryCapacity : 0;
        }

        private void ObserveRoundChoiceReplayAllocation(
            IReadOnlyList<ActionReplayWorkerOutcome?> outcomes,
            int outcomeCount)
        {
            long maximum = 0;
            for (int index = 0; index < outcomeCount; index++)
                maximum = Math.Max(maximum, outcomes[index]?.OutcomeAllocatedBytes ?? 0);
            if (maximum > _roundChoiceReplayAllocatedHighWater)
                _roundChoiceReplayAllocatedHighWater = maximum;
            _roundChoiceReplayAllocationObserved = true;
        }

        private static long AddAllocationSafetyMargin(long value)
            => value > long.MaxValue / 3
                ? long.MaxValue
                : Math.Max(1, (value * 3 + 1) / 2);

        private int ResolveActionReplayMicrobatchCapacity(int remainingActions)
        {
            int capacity = Math.Min(DegreeOfParallelism, remainingActions);
            SearchMemoryPressureSignal signal = _coordinator.SearchMemoryPressure;
            if (!signal.IsEnabled)
                return capacity;

            if (!_actionReplayAllocationObserved)
                capacity = Math.Min(capacity, 2);
            long reserve = _actionReplayAllocationObserved
                ? Math.Max(1, Volatile.Read(ref _actionReplayAllocatedHighWater))
                : InitialActionReplayAllocationHighWater;
            long remainingBytes = signal.RemainingBytes;
            int memoryCapacity = reserve <= 0 || remainingBytes == long.MaxValue
                ? capacity
                : (int)Math.Min(capacity, remainingBytes / reserve);
            return memoryCapacity >= 2 ? memoryCapacity : 1;
        }

        private void ObserveActionReplayAllocation(
            IReadOnlyList<ActionReplayWorkerOutcome?> outcomes,
            int outcomeCount)
        {
            long maximum = 0;
            for (int index = 0; index < outcomeCount; index++)
                maximum = Math.Max(maximum, outcomes[index]?.OutcomeAllocatedBytes ?? 0);
            if (maximum > _actionReplayAllocatedHighWater)
                _actionReplayAllocatedHighWater = maximum;
            _actionReplayAllocationObserved = true;
        }

        private void ExecuteActionReplay(
            CombatBeamSolver worker,
            SearchNode parent,
            PreparedCardAction action,
            ReplayForkSeed seed,
            ActionReplayWave wave,
            int outcomeIndex,
            bool includeWorkerMetrics,
            bool trackActiveWorker)
        {
            wave.RecordExecutedWorkItem();
            int activeActionReplayWorkers = Interlocked.Increment(
                ref _activeActionReplayWorkers);
            UpdateMaximum(
                ref _maximumActiveActionReplayWorkers,
                activeActionReplayWorkers);
            if (trackActiveWorker)
            {
                int activeWorkers = Interlocked.Increment(ref _activeWorkers);
                UpdateMaximum(ref _maximumActiveWorkers, activeWorkers);
            }
            long allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                PreparedCardActionEvaluation evaluation = worker.EvaluatePreparedCardAction(
                    parent,
                    action,
                    seed,
                    _actionReplayForkGate);
                long workerAllocatedBytes = Math.Max(
                    0,
                    GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart);
                wave.Outcomes[outcomeIndex] = new ActionReplayWorkerOutcome(
                    includeWorkerMetrics ? worker : null,
                    evaluation.Batch,
                    evaluation.DeferredProbe,
                    null,
                    workerAllocatedBytes,
                    SaturatingAdd(
                        wave.SeedAllocatedBytes[outcomeIndex],
                        workerAllocatedBytes));
            }
            catch (System.Exception error)
            {
                long workerAllocatedBytes = Math.Max(
                    0,
                    GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart);
                wave.Outcomes[outcomeIndex] = new ActionReplayWorkerOutcome(
                    includeWorkerMetrics ? worker : null,
                    null,
                    null,
                    ExceptionDispatchInfo.Capture(error),
                    workerAllocatedBytes,
                    SaturatingAdd(
                        wave.SeedAllocatedBytes[outcomeIndex],
                        workerAllocatedBytes));
            }
            finally
            {
                seed.Dispose();
                if (trackActiveWorker)
                    Interlocked.Decrement(ref _activeWorkers);
                Interlocked.Decrement(ref _activeActionReplayWorkers);
            }
        }

        private void ExecuteRoundChoiceReplay(
            CombatBeamSolver worker,
            SearchNode parent,
            DeferredRoundChoiceFrontier frontier,
            PendingChoiceReplayBranch branch,
            ReplayForkSeed seed,
            ActionReplayWave wave,
            int outcomeIndex,
            bool includeWorkerMetrics,
            bool trackActiveWorker)
        {
            wave.RecordExecutedWorkItem();
            int activeRoundChoiceReplayWorkers = Interlocked.Increment(
                ref _activeRoundChoiceReplayWorkers);
            UpdateMaximum(
                ref _maximumActiveRoundChoiceReplayWorkers,
                activeRoundChoiceReplayWorkers);
            if (trackActiveWorker)
            {
                int activeWorkers = Interlocked.Increment(ref _activeWorkers);
                UpdateMaximum(ref _maximumActiveWorkers, activeWorkers);
            }
            long allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                ExpansionBatch batch = worker.EvaluateDeferredRoundChoiceBranch(
                    parent,
                    frontier,
                    branch,
                    seed,
                    _actionReplayForkGate);
                long workerAllocatedBytes = Math.Max(
                    0,
                    GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart);
                wave.Outcomes[outcomeIndex] = new ActionReplayWorkerOutcome(
                    includeWorkerMetrics ? worker : null,
                    batch,
                    null,
                    null,
                    workerAllocatedBytes,
                    SaturatingAdd(
                        wave.SeedAllocatedBytes[outcomeIndex],
                        workerAllocatedBytes));
            }
            catch (System.Exception error)
            {
                long workerAllocatedBytes = Math.Max(
                    0,
                    GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart);
                wave.Outcomes[outcomeIndex] = new ActionReplayWorkerOutcome(
                    includeWorkerMetrics ? worker : null,
                    null,
                    null,
                    ExceptionDispatchInfo.Capture(error),
                    workerAllocatedBytes,
                    SaturatingAdd(
                        wave.SeedAllocatedBytes[outcomeIndex],
                        workerAllocatedBytes));
            }
            finally
            {
                seed.Dispose();
                if (trackActiveWorker)
                    Interlocked.Decrement(ref _activeWorkers);
                Interlocked.Decrement(ref _activeRoundChoiceReplayWorkers);
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

        private sealed class ExpansionWave(int workItemCount) : IDisposable
        {
            public ExpansionWorkerOutcome[] Outcomes { get; } = new ExpansionWorkerOutcome[workItemCount];
            public CountdownEvent BackgroundCompleted { get; } = new(Math.Max(0, workItemCount - 1));

            public void Dispose()
            {
                BackgroundCompleted.Dispose();
            }
        }

        private interface IExpansionLaneWorkItem
        {
            void Execute(ParallelExpansionExecutor owner, CombatBeamSolver worker);
            void Signal();
        }

        private sealed record ExpansionWorkItem(
            SearchNode Node,
            ExpansionWave Wave,
            int OutcomeIndex) : IExpansionLaneWorkItem
        {
            public void Execute(ParallelExpansionExecutor owner, CombatBeamSolver worker)
                => owner.Execute(
                    worker,
                    Node,
                    Wave,
                    OutcomeIndex,
                    includeWorkerMetrics: true);

            public void Signal() => Wave.BackgroundCompleted.Signal();
        }

        private sealed class ActionReplayWave(int workItemCount) : IDisposable
        {
            private readonly ReplayForkSeed?[] _seeds = new ReplayForkSeed?[workItemCount];
            private int _executedWorkItemCount;

            public ActionReplayWorkerOutcome?[] Outcomes { get; } =
                new ActionReplayWorkerOutcome?[workItemCount];
            public long[] SeedAllocatedBytes { get; } = new long[workItemCount];
            public CountdownEvent BackgroundCompleted { get; } = new(1);
            public int OutcomeCount { get; private set; } = workItemCount;
            public int ExecutedWorkItemCount => Volatile.Read(ref _executedWorkItemCount);

            public void SetSeed(int outcomeIndex, ReplayForkSeed seed, long allocatedBytes)
            {
                if (Interlocked.CompareExchange(ref _seeds[outcomeIndex], seed, null) != null)
                    throw new InvalidOperationException("并行动作 seed 被重复设置。");
                SeedAllocatedBytes[outcomeIndex] = allocatedBytes;
            }

            public ReplayForkSeed TakeSeed(int outcomeIndex)
                => Interlocked.Exchange(ref _seeds[outcomeIndex], null)
                    ?? throw new InvalidOperationException("并行动作 seed 缺失或已移交。");

            public void RecordExecutedWorkItem()
                => Interlocked.Increment(ref _executedWorkItemCount);

            public void RegisterBackgroundWork()
                => BackgroundCompleted.AddCount();

            public void CancelBackgroundRegistration()
                => BackgroundCompleted.Signal();

            public void StopDispatchAt(int outcomeCount)
                => OutcomeCount = Math.Min(OutcomeCount, outcomeCount);

            public void CompleteDispatch() => BackgroundCompleted.Signal();

            public void Dispose()
            {
                foreach (ReplayForkSeed? seed in _seeds)
                    seed?.Dispose();
                BackgroundCompleted.Dispose();
            }
        }

        private sealed record ActionReplayWorkItem(
            SearchNode Parent,
            PreparedCardAction Action,
            ReplayForkSeed Seed,
            ActionReplayWave Wave,
            int OutcomeIndex) : IExpansionLaneWorkItem
        {
            public void Execute(ParallelExpansionExecutor owner, CombatBeamSolver worker)
                => owner.ExecuteActionReplay(
                    worker,
                    Parent,
                    Action,
                    Seed,
                    Wave,
                    OutcomeIndex,
                    includeWorkerMetrics: true,
                    trackActiveWorker: true);

            public void Signal() => Wave.BackgroundCompleted.Signal();
        }

        private sealed record RoundChoiceReplayWorkItem(
            SearchNode Parent,
            DeferredRoundChoiceFrontier Frontier,
            PendingChoiceReplayBranch Branch,
            ReplayForkSeed Seed,
            ActionReplayWave Wave,
            int OutcomeIndex) : IExpansionLaneWorkItem
        {
            public void Execute(ParallelExpansionExecutor owner, CombatBeamSolver worker)
                => owner.ExecuteRoundChoiceReplay(
                    worker,
                    Parent,
                    Frontier,
                    Branch,
                    Seed,
                    Wave,
                    OutcomeIndex,
                    includeWorkerMetrics: true,
                    trackActiveWorker: true);

            public void Signal() => Wave.BackgroundCompleted.Signal();
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

    private ExpansionBatch EvaluateRawExpansion(
        SearchNode node,
        ParallelExpansionExecutor? actionReplayExecutor)
    {
        ExpansionBatch batch = new();
        bool completed = false;
        try
        {
            GenerateRawCardCandidates(node, batch, actionReplayExecutor);
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

    private PreparedCardActionEvaluation EvaluatePreparedCardAction(
        SearchNode parent,
        PreparedCardAction action,
        ReplayForkSeed? seed,
        object replayForkGate)
    {
        ExpansionBatch batch = new();
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

    private void GenerateRawCardCandidates(
        SearchNode node,
        ExpansionBatch batch,
        ParallelExpansionExecutor? actionReplayExecutor)
    {
        List<PreparedCardAction> actions = PrepareCardActions(node);
        if (actionReplayExecutor != null && actions.Count >= 2)
        {
            using ExpansionBatch replayed = actionReplayExecutor.EvaluateCardActions(node, actions);
            foreach (RawCardCandidate candidate in replayed.Cards)
                replayed.TransferTo(batch, candidate);
            return;
        }
        foreach (PreparedCardAction action in actions)
        {
            DeferredCardActionProbe? deferredProbe = GeneratePreparedCardAction(
                node,
                action,
                seed: null,
                replayForkGate: null,
                batch,
                allowPendingChoiceDeferral: false);
            if (deferredProbe != null)
            {
                deferredProbe.Dispose();
                throw new InvalidOperationException(
                    "串行卡牌展开意外返回 deferred choice probe。");
            }
        }
    }

    private List<PreparedCardAction> PrepareCardActions(SearchNode node)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SimulationSnapshot snapshot = node.Snapshot;
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
        if (snapshot.PlayerDead || snapshot.AllEnemiesDead)
            return [];

        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        IReadOnlyList<PredictedCard> hand = playerState.Hand.Cards;
        List<PreparedCardAction> actions = new(hand.Count);
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
            bool requiresUnsupportedExistingChoice =
                CardChoiceSupport.RequiresUnsupportedExistingChoice(card.Preview);
            PlanCardChoice? requiredEmptyChoice =
                CardChoiceSupport.BuildRequiredEmptyChoice(card.Preview);
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
                PlanAction planAction = new(
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
                actions.Add(new PreparedCardAction(
                    planAction,
                    card.Original,
                    card.Preview.Type,
                    target?.CombatId,
                    requiresUnsupportedExistingChoice,
                    requiredEmptyChoice));
            }
        }
        return actions;
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
            int actionChoiceBranchLimit = ResolveWholeActionChoiceBranchLimit(
                action.Action,
                primaryChoiceSpec);
            IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> resolvedBranches =
                HasChoiceBeforePrimary(probeSnapshot, primaryChoiceSpec)
                    ? ResolveRoundChoiceBranches(
                        node,
                        action.Action,
                        probeSnapshot,
                        BuildPrimaryChoiceMatch(primaryChoiceSpec),
                        actionChoiceBranchLimit)
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
            bool forcedTurnEnd = finalSnapshot.Turn > node.Turn;
            PlanAction nodeAction = finalAction with { EndsPlayerTurn = forcedTurnEnd };
            bool terminal = finalSnapshot.PlayerDead
                || finalSnapshot.AllEnemiesDead
                || finalSnapshot.BoundaryReason != SearchBoundaryReason.None;
            bool repeatableNoProgress = IsRepeatableNoProgressStep(
                snapshot,
                finalSnapshot,
                action.OriginalCard);
            string? repeatableCardId = repeatableNoProgress
                ? action.Action.CardId
                : null;
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
                RepeatableNoProgressCount: repeatableCount)
            {
                CumulativeEnemyHpLost = AccumulateEnemyHpLost(node, finalSnapshot),
            };
            if (ShouldPruneRepeatableNoProgress(child)
                || ShouldPruneCrossTurnNoProgress(child))
            {
                _run.RepeatableNoProgressBranchesPruned++;
                finalSnapshot.ReleaseSimulator();
                continue;
            }
            batch.Add(new RawCardCandidate(
                child,
                action.CardType,
                action.TargetCombatId));
        }
    }

    private DeferredRoundChoiceFrontier? PrepareDeferredRoundChoiceFrontier(
        SearchNode node,
        DeferredCardActionProbe deferredProbe,
        ExpansionBatch completedBatch)
    {
        _run.DeferredRoundChoiceActions++;
        PreparedCardAction preparedAction = deferredProbe.Action;
        SimulationSnapshot? currentSnapshot = deferredProbe.TakeSnapshot();
        PlanAction currentAction = preparedAction.Action;
        PrimaryChoiceMatch? unresolvedPrimaryChoice = null;
        int lastLayerWidth = 0;
        try
        {
            CardChoiceSpec? choiceSpec = BuildPrimaryCardChoiceSpec(currentSnapshot);
            if (choiceSpec == null && preparedAction.RequiresUnsupportedExistingChoice)
            {
                currentSnapshot.ReleaseSimulator();
                currentSnapshot = null;
                RecordDeferredRoundChoiceLayer(width: 0);
                return null;
            }

            CardChoiceSpec? primaryChoiceSpec = choiceSpec
                ?? BuildRequiredEmptyChoiceSpec(preparedAction.RequiredEmptyChoice);
            if (HasChoiceBeforePrimary(currentSnapshot, primaryChoiceSpec))
            {
                unresolvedPrimaryChoice = BuildPrimaryChoiceMatch(primaryChoiceSpec);
                int maxFinalBranches = ResolveWholeActionChoiceBranchLimit(
                    currentAction,
                    primaryChoiceSpec);
                PendingChoiceReplayLayer layer = BuildPendingChoiceReplayLayer(
                    node,
                    currentAction,
                    currentSnapshot,
                    unresolvedPrimaryChoice,
                    maxFinalBranches);
                lastLayerWidth = layer.Branches.Count;
                currentSnapshot.ReleaseSimulator();
                currentSnapshot = null;
                if (maxFinalBranches != int.MaxValue)
                {
                    RecordDeferredRoundChoiceLayer(
                        lastLayerWidth,
                        finitePendingFallback: true);
                    AddResolvedCardCandidates(
                        node,
                        preparedAction,
                        ResolvePendingChoiceReplayLayer(
                            node,
                            layer,
                            unresolvedPrimaryChoice,
                            maxFinalBranches),
                        completedBatch);
                    return null;
                }
                if (layer.Branches.Count >= 2)
                {
                    RecordDeferredRoundChoiceLayer(lastLayerWidth);
                    return new DeferredRoundChoiceFrontier(
                        preparedAction,
                        layer.Branches,
                        unresolvedPrimaryChoice,
                        MaxFinalBranchesPerBranch: int.MaxValue);
                }
                if (layer.Branches.Count == 0)
                {
                    RecordDeferredRoundChoiceLayer(lastLayerWidth);
                    return null;
                }
                PendingChoiceReplayBranch onlyBranch = layer.Branches[0];
                currentAction = onlyBranch.Action;
                currentSnapshot = ReplayPendingChoiceBranch(node, onlyBranch);
            }
            else
            {
                PrimaryCardChoiceLayer layer = BuildPrimaryCardChoiceLayer(
                    currentAction,
                    currentSnapshot,
                    choiceSpec,
                    preparedAction.RequiredEmptyChoice);
                lastLayerWidth = layer.Choices.Count;
                bool finitePrimaryLayer =
                    layer.DownstreamChoiceBranchQuota != int.MaxValue;
                if (layer.UnregisteredPendingChoice)
                {
                    currentSnapshot.ReleaseSimulator();
                    currentSnapshot = null;
                    throw new InvalidOperationException(
                        $"卡牌 {currentAction.CardId} 产生了未登记的分支选择，不能静默回退到原生重扫。");
                }
                if (layer.Choices.Count >= 2)
                {
                    List<PendingChoiceReplayBranch> branches = new(layer.Choices.Count);
                    foreach (PlanCardChoice? choice in layer.Choices)
                    {
                        if (choice == null)
                        {
                            throw new InvalidOperationException(
                                "多分支 primary choice 不能包含复用 probe 的空分支。");
                        }
                        branches.Add(new PendingChoiceReplayBranch(
                            currentAction with { Choice = choice },
                            PruneInvalidBranch: true));
                    }
                    currentSnapshot.ReleaseSimulator();
                    currentSnapshot = null;
                    RecordDeferredRoundChoiceLayer(
                        lastLayerWidth,
                        finitePrimaryLayer: finitePrimaryLayer);
                    return new DeferredRoundChoiceFrontier(
                        preparedAction,
                        branches,
                        UnresolvedPrimaryChoice: null,
                        MaxFinalBranchesPerBranch: layer.DownstreamChoiceBranchQuota);
                }
                if (finitePrimaryLayer)
                {
                    RecordDeferredRoundChoiceLayer(
                        lastLayerWidth,
                        finitePrimaryLayer: true);
                    SimulationSnapshot ownedProbe = currentSnapshot;
                    currentSnapshot = null;
                    AddResolvedCardCandidates(
                        node,
                        preparedAction,
                        ResolvePrimaryCardChoiceLayer(
                            node,
                            currentAction,
                            ownedProbe,
                            layer),
                        completedBatch);
                    return null;
                }

                PlanCardChoice? onlyChoice = layer.Choices[0];
                currentAction = currentAction with { Choice = onlyChoice };
                if (onlyChoice != null)
                {
                    currentSnapshot.ReleaseSimulator();
                    currentSnapshot = null;
                    currentSnapshot = ReplayPlannedChoiceBranch(node, currentAction);
                }
            }

            while (currentSnapshot != null)
            {
                if (currentSnapshot.BoundaryReason != SearchBoundaryReason.PendingChoice)
                {
                    SimulationSnapshot finalSnapshot = currentSnapshot;
                    currentSnapshot = null;
                    AddResolvedCardCandidates(
                        node,
                        preparedAction,
                        [(currentAction, finalSnapshot)],
                        completedBatch);
                    RecordDeferredRoundChoiceLayer(lastLayerWidth);
                    return null;
                }

                PendingChoiceReplayLayer layer = BuildPendingChoiceReplayLayer(
                    node,
                    currentAction,
                    currentSnapshot,
                    unresolvedPrimaryChoice,
                    int.MaxValue);
                lastLayerWidth = layer.Branches.Count;
                currentSnapshot.ReleaseSimulator();
                currentSnapshot = null;
                if (layer.Branches.Count >= 2)
                {
                    RecordDeferredRoundChoiceLayer(lastLayerWidth);
                    return new DeferredRoundChoiceFrontier(
                        preparedAction,
                        layer.Branches,
                        unresolvedPrimaryChoice,
                        MaxFinalBranchesPerBranch: int.MaxValue);
                }
                if (layer.Branches.Count == 0)
                {
                    RecordDeferredRoundChoiceLayer(lastLayerWidth);
                    return null;
                }
                PendingChoiceReplayBranch onlyBranch = layer.Branches[0];
                currentAction = onlyBranch.Action;
                currentSnapshot = ReplayPendingChoiceBranch(node, onlyBranch);
            }

            RecordDeferredRoundChoiceLayer(lastLayerWidth);
            return null;
        }
        finally
        {
            currentSnapshot?.ReleaseSimulator();
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

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)>
        ResolveDeferredRoundChoiceBranchesSerially(
            SearchNode node,
            DeferredRoundChoiceFrontier frontier,
            IReadOnlyList<PendingChoiceReplayBranch> branches)
    {
        foreach (PendingChoiceReplayBranch branch in branches)
        {
            SimulationSnapshot? branchSnapshot = ReplayPendingChoiceBranch(node, branch);
            if (branchSnapshot == null)
                continue;
            foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in
                     ResolveRoundChoiceBranches(
                         node,
                         branch.Action,
                         branchSnapshot,
                         frontier.UnresolvedPrimaryChoice,
                         frontier.MaxFinalBranchesPerBranch))
            {
                yield return (finalAction, finalSnapshot);
            }
        }
    }

    private ExpansionBatch EvaluateDeferredRoundChoiceBranch(
        SearchNode node,
        DeferredRoundChoiceFrontier frontier,
        PendingChoiceReplayBranch branch,
        ReplayForkSeed? seed,
        object? replayForkGate)
    {
        if (_parallelActionReplayForkGate != null)
            throw new InvalidOperationException("不能嵌套并行 round-choice replay 上下文。");
        _parallelActionReplayForkGate = replayForkGate;
        ExpansionBatch batch = new();
        bool completed = false;
        try
        {
            SimulationSnapshot? branchSnapshot = ReplayPendingChoiceBranch(node, branch, seed);
            if (branchSnapshot != null)
            {
                AddResolvedCardCandidates(
                    node,
                    frontier.PreparedAction,
                    ResolveRoundChoiceBranches(
                        node,
                        branch.Action,
                        branchSnapshot,
                        frontier.UnresolvedPrimaryChoice,
                        frontier.MaxFinalBranchesPerBranch),
                    batch);
            }
            completed = true;
            return batch;
        }
        finally
        {
            _parallelActionReplayForkGate = null;
            if (!completed)
                batch.Dispose();
        }
    }

    private void GenerateRawPotionCandidates(SearchNode node, ExpansionBatch batch)
    {
        SimulationSnapshot snapshot = node.Snapshot;
        if (snapshot.PlayerDead || snapshot.AllEnemiesDead
            || _maximumPotionUses != null
                && ExplicitPotionUseCount(node) >= _maximumPotionUses.Value)
        {
            return;
        }

        CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
        for (int potionSlot = 0; potionSlot < root.PotionSlotCount; potionSlot++)
        {
            PotionModel? potion = simulatedCombat.GetPotionAtSlot(_player, potionSlot);
            if (potion == null
                || !simulatedCombat.IsPotionAvailable(_player, potionSlot)
                || !PotionOnUseSupport.CanSearch(potion)
                || !AllowsPotionUse(potionSlot, potion.Id.Entry)
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
                            node.CombatProgress)
                        {
                            CumulativeEnemyHpLost = AccumulateEnemyHpLost(node, finalSnapshot),
                        };
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
                node.CombatProgress.Advance(endSnapshot))
            {
                CumulativeEnemyHpLost = AccumulateEnemyHpLost(node, endSnapshot),
            };
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
        _run.ShuffleBranchesPruned += source.ShuffleBranchesPruned;
        _run.SoldHpBranchesPruned += source.SoldHpBranchesPruned;
        _run.HpInvestmentBranchesProtected += source.HpInvestmentBranchesProtected;
        _run.ReplayCount += source.ReplayCount;
        _run.ForkCount += source.ForkCount;
        _run.TransitionCount += source.TransitionCount;
        _run.RepeatableNoProgressBranchesPruned +=
            source.RepeatableNoProgressBranchesPruned;
        _run.StandPatProbes += source.StandPatProbes;
        source.DuplicateCardBranchesPruned = 0;
        source.ActionAdmissionRepresentativesProtected = 0;
        source.ChoiceBranchesEvaluated = 0;
        source.ShuffleBranchesPruned = 0;
        source.SoldHpBranchesPruned = 0;
        source.HpInvestmentBranchesProtected = 0;
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

        List<ActionCandidate> queuedCandidates = SelectActionCandidates(parent, nonDominated);
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
