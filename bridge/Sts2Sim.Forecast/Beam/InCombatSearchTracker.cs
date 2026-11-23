using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Sts2Sim.Core.Combat;

namespace CombatSolver.Beam;

internal sealed class InCombatPassTrack(InCombatSearchTracker owner)
{
    private sealed class CompletedSegment(SearchNode[] items, CompletedSegment? previous)
    {
        public readonly SearchNode[] Items = items;
        public readonly CompletedSegment? Previous = previous;
    }

    internal readonly record struct PassUsableSnapshot(
        StateEvaluator Evaluator,
        int InitialHp,
        SearchNode[] Pool,
        ForecastSearchBoundary Boundary,
        ForecastPassOutcome? FinishedOutcome);

    private sealed class WaveSnapshot(
        long generation,
        CompletedSegment? completedTail,
        SearchNode[] frontierOrEnded,
        SearchNode[] lastActive,
        SearchNode[] truncatedParents,
        SearchNode? potionFreeFallback,
        SearchNode? potionFallback,
        ForecastSearchBoundary boundary)
    {
        public static readonly WaveSnapshot Empty = new(
            0, null, [], [], [], null, null, ForecastSearchBoundary.None);

        public readonly long Generation = generation;
        public readonly CompletedSegment? CompletedTail = completedTail;
        public readonly SearchNode[] FrontierOrEnded = frontierOrEnded;
        public readonly SearchNode[] LastActive = lastActive;
        public readonly SearchNode[] TruncatedParents = truncatedParents;
        public readonly SearchNode? PotionFreeFallback = potionFreeFallback;
        public readonly SearchNode? PotionFallback = potionFallback;
        public readonly ForecastSearchBoundary Boundary = boundary;
    }

    private sealed class InFlightChainNode(SearchNode node, long waveGeneration, InFlightChainNode? next)
    {
        public readonly SearchNode Node = node;
        public readonly long WaveGeneration = waveGeneration;
        public readonly InFlightChainNode? Next = next;
    }

    private sealed class InFlightLaneSlot
    {
        public long ActiveWaveGen;
        public SearchNode? BestNext;
        public InFlightChainNode? TerminalHead;

        public void RecordCandidate(SearchNode node, bool isTerminalOrBoundary, long waveGeneration)
        {
            if (isTerminalOrBoundary)
            {
                if (Volatile.Read(ref ActiveWaveGen) != waveGeneration)
                {
                    Volatile.Write(ref BestNext, null);
                    Volatile.Write(ref TerminalHead, null);
                    Volatile.Write(ref ActiveWaveGen, waveGeneration);
                }
                var chain = new InFlightChainNode(node, waveGeneration, Volatile.Read(ref TerminalHead));
                Volatile.Write(ref TerminalHead, chain);
            }
            else
            {
                if (Volatile.Read(ref ActiveWaveGen) != waveGeneration)
                {
                    Volatile.Write(ref BestNext, null);
                    Volatile.Write(ref TerminalHead, null);
                    Volatile.Write(ref ActiveWaveGen, waveGeneration);
                }
                SearchNode? current = Volatile.Read(ref BestNext);
                if (current is null || node.Score > current.Score)
                    Volatile.Write(ref BestNext, node);
            }
        }

        public void ResetForWave(long nextWaveGeneration)
        {
            Volatile.Write(ref BestNext, null);
            Volatile.Write(ref TerminalHead, null);
            Volatile.Write(ref ActiveWaveGen, nextWaveGeneration);
        }
    }

    private StateEvaluator? _evaluator;
    private int _initialHp;
    private InFlightLaneSlot[] _lanes = [];
    private CompletedSegment? _completedTail;
    private int _committedCompletedCount;
    private WaveSnapshot _waveSnapshot = WaveSnapshot.Empty;
    private ForecastPassOutcome? _finishedOutcome;
    private ForecastNodeBudgetTracker? _budgetTracker;
    private long _observedExpandedNodes;

    public long CurrentWaveGeneration => Volatile.Read(ref _waveSnapshot).Generation;

    public StateEvaluator? Evaluator => Volatile.Read(ref _evaluator);

    public int InitialHp => Volatile.Read(ref _initialHp);

    public ForecastNodeBudgetTracker? BudgetTracker => Volatile.Read(ref _budgetTracker);

    public long GetFallbackObservedNodes() => Interlocked.Read(ref _observedExpandedNodes);

    public void AttachBudgetTracker(ForecastNodeBudgetTracker tracker) =>
        Volatile.Write(ref _budgetTracker, tracker);

    public void AttachEvaluator(StateEvaluator evaluator, int initialHp, int laneCount = 1)
    {
        int count = Math.Max(1, laneCount);
        var lanes = new InFlightLaneSlot[count];
        for (int i = 0; i < count; i++)
            lanes[i] = new InFlightLaneSlot();
        Volatile.Write(ref _lanes, lanes);
        Volatile.Write(ref _initialHp, initialHp);
        Volatile.Write(ref _evaluator, evaluator);
    }

    public void ObserveExpansion(long delta, int turn)
    {
        if (delta > 0)
            Interlocked.Add(ref _observedExpandedNodes, delta);
        owner.ObserveTurn(turn);
    }

    public void RecordInFlightCandidate(int laneIndex, SearchNode node, bool isTerminalOrBoundary)
    {
        InFlightLaneSlot[] lanes = Volatile.Read(ref _lanes);
        if ((uint)laneIndex >= (uint)lanes.Length)
            return;
        long gen = Volatile.Read(ref _waveSnapshot).Generation;
        lanes[laneIndex].RecordCandidate(node, isTerminalOrBoundary, gen);
    }

    public void ObserveParentResult(ParentExpansionResult result)
    {
        InFlightLaneSlot[] lanes = Volatile.Read(ref _lanes);
        if (lanes.Length == 0)
            return;
        InFlightLaneSlot lane = lanes[0];
        long gen = Volatile.Read(ref _waveSnapshot).Generation;
        for (int i = 0; i < result.Completed.Count; i++)
            lane.RecordCandidate(result.Completed[i], isTerminalOrBoundary: true, gen);
        for (int i = 0; i < result.Ended.Count; i++)
            lane.RecordCandidate(result.Ended[i], isTerminalOrBoundary: true, gen);
        if (result.PotionFreeBoundaryFallback is not null)
            lane.RecordCandidate(result.PotionFreeBoundaryFallback, isTerminalOrBoundary: true, gen);
        if (result.PotionBoundaryFallback is not null)
            lane.RecordCandidate(result.PotionBoundaryFallback, isTerminalOrBoundary: true, gen);
        if (result.Next.Count > 0)
        {
            SearchNode? bestNext = null;
            for (int i = 0; i < result.Next.Count; i++)
            {
                SearchNode candidate = result.Next[i];
                if (bestNext is null || candidate.Score > bestNext.Score)
                    bestNext = candidate;
            }
            if (bestNext is not null)
                lane.RecordCandidate(bestNext, isTerminalOrBoundary: false, gen);
        }
    }

    public void CommitWave(
        IReadOnlyList<SearchNode> completed,
        IReadOnlyList<SearchNode> frontierOrEnded,
        IReadOnlyList<SearchNode> lastActive,
        IReadOnlyList<SearchNode> truncatedParents,
        SearchNode? potionFreeBoundaryFallback,
        SearchNode? potionBoundaryFallback,
        ForecastSearchBoundary passBoundary)
    {
        if (completed.Count > _committedCompletedCount)
        {
            int deltaCount = completed.Count - _committedCompletedCount;
            var deltaItems = new SearchNode[deltaCount];
            for (int i = 0; i < deltaCount; i++)
                deltaItems[i] = completed[_committedCompletedCount + i];
            _completedTail = new CompletedSegment(deltaItems, _completedTail);
            _committedCompletedCount = completed.Count;
        }

        long nextGeneration = _waveSnapshot.Generation + 1;
        var nextSnapshot = new WaveSnapshot(
            nextGeneration,
            _completedTail,
            ToArray(frontierOrEnded),
            ToArray(lastActive),
            ToArray(truncatedParents),
            potionFreeBoundaryFallback,
            potionBoundaryFallback,
            passBoundary);

        Volatile.Write(ref _waveSnapshot, nextSnapshot);

        InFlightLaneSlot[] lanes = Volatile.Read(ref _lanes);
        for (int i = 0; i < lanes.Length; i++)
            lanes[i].ResetForWave(nextGeneration);

        owner.NotifyWaveCommitted();

        static SearchNode[] ToArray(IReadOnlyList<SearchNode> source)
        {
            if (source.Count == 0)
                return [];
            var arr = new SearchNode[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return arr;
        }
    }

    public void Complete(ForecastPassOutcome outcome)
    {
        Volatile.Write(ref _finishedOutcome, outcome);
        owner.NotifyWaveCommitted();
    }

    public bool TrySnapshotUsablePool(out PassUsableSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        StateEvaluator? evaluator = Volatile.Read(ref _evaluator);
        int initialHp = Volatile.Read(ref _initialHp);
        ForecastPassOutcome? finishedOutcome = Volatile.Read(ref _finishedOutcome);
        if (evaluator is null)
        {
            snapshot = default;
            return false;
        }

        if (finishedOutcome is not null)
        {
            var seenFinished = new HashSet<SearchNode>(
                finishedOutcome.EvaluatedCandidates.Length,
                ReferenceEqualityComparer.Instance);
            var usable = new List<SearchNode>(finishedOutcome.EvaluatedCandidates.Length);
            for (int i = 0; i < finishedOutcome.EvaluatedCandidates.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                SearchNode node = finishedOutcome.EvaluatedCandidates[i].Node;
                if ((node.Depth > 0 || node.Ended) && seenFinished.Add(node))
                    usable.Add(node);
            }
            if (usable.Count == 0)
            {
                snapshot = default;
                return false;
            }
            snapshot = new PassUsableSnapshot(
                evaluator,
                initialHp,
                usable.ToArray(),
                finishedOutcome.PassBoundary,
                finishedOutcome);
            return true;
        }

        // Optimistic lock-free snapshot: retry only if coordinator publishes a newer WaveSnapshot mid-read.
        WaveSnapshot snap;
        List<SearchNode> inFlightSnapshot = [];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            snap = Volatile.Read(ref _waveSnapshot);
            long genBefore = snap.Generation;
            inFlightSnapshot.Clear();
            InFlightLaneSlot[] lanes = Volatile.Read(ref _lanes);
            for (int i = 0; i < lanes.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                InFlightLaneSlot slot = lanes[i];
                if (Volatile.Read(ref slot.ActiveWaveGen) != genBefore)
                    continue;
                SearchNode? bestNext = Volatile.Read(ref slot.BestNext);
                if (bestNext is not null)
                    inFlightSnapshot.Add(bestNext);
                for (InFlightChainNode? cur = Volatile.Read(ref slot.TerminalHead); cur is not null; cur = cur.Next)
                {
                    ct.ThrowIfCancellationRequested();
                    if (cur.WaveGeneration < genBefore)
                        break;
                    if (cur.WaveGeneration == genBefore)
                        inFlightSnapshot.Add(cur.Node);
                }
            }

            if (ReferenceEquals(snap, Volatile.Read(ref _waveSnapshot)))
                break;
        }

        ForecastSearchBoundary boundary = snap.Boundary;
        var seen = new HashSet<SearchNode>(ReferenceEqualityComparer.Instance);
        var list = new List<SearchNode>(inFlightSnapshot.Count + 64);
        for (CompletedSegment? seg = snap.CompletedTail; seg is not null; seg = seg.Previous)
            AddUsable(seg.Items, seen, list, ct);
        AddUsable(snap.FrontierOrEnded, seen, list, ct);
        AddUsable(snap.LastActive, seen, list, ct);
        AddUsable(snap.TruncatedParents, seen, list, ct);
        AddUsableOne(snap.PotionFreeFallback, seen, list);
        AddUsableOne(snap.PotionFallback, seen, list);

        for (int i = 0; i < inFlightSnapshot.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            AddUsableOne(inFlightSnapshot[i], seen, list);
        }

        if (list.Count == 0)
        {
            snapshot = default;
            return false;
        }

        snapshot = new PassUsableSnapshot(
            evaluator,
            initialHp,
            list.ToArray(),
            boundary,
            FinishedOutcome: null);
        return true;

        static void AddUsable(SearchNode[] items, HashSet<SearchNode> seen, List<SearchNode> dest, CancellationToken token)
        {
            for (int i = 0; i < items.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                SearchNode item = items[i];
                if ((item.Depth > 0 || item.Ended) && seen.Add(item))
                    dest.Add(item);
            }
        }

        static void AddUsableOne(SearchNode? item, HashSet<SearchNode> seen, List<SearchNode> dest)
        {
            if (item is not null && (item.Depth > 0 || item.Ended) && seen.Add(item))
                dest.Add(item);
        }
    }
}

internal sealed class ProgressCallbackGate
{
    private const int StateOpenIdle = 0;
    private const int StateOpenInvoking = 1;
    private const int StateClosed = 2;

    private readonly object _sync = new();
    private int _state;

    public bool IsClosed => (Volatile.Read(ref _state) & StateClosed) != 0;

    public bool TryInvoke(Action action, Func<bool>? canInvoke = null)
    {
        if (IsClosed)
            return false;
        if (canInvoke is not null && !canInvoke())
            return false;

        lock (_sync)
        {
            if (IsClosed)
                return false;
            if (canInvoke is not null && !canInvoke())
                return false;
            if (Interlocked.CompareExchange(ref _state, StateOpenInvoking, StateOpenIdle) != StateOpenIdle)
                return false;

            try
            {
                action();
                return true;
            }
            finally
            {
                Interlocked.And(ref _state, ~StateOpenInvoking);
            }
        }
    }

    public bool Close(TimeSpan drainBudget)
    {
        int previous = Interlocked.Or(ref _state, StateClosed);
        if ((previous & StateOpenInvoking) == 0)
            return true;

        if (Monitor.TryEnter(_sync, drainBudget))
        {
            Monitor.Exit(_sync);
            return true;
        }
        return false;
    }
}

internal sealed class InCombatSearchTracker
{
    private const long ProgressIntervalMilliseconds = 200;
    private static readonly TimeSpan FinalMetricsFlushWaitBudget = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan NormalShutdownWaitBudget = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ExpiredDeadlineWaitBudget = TimeSpan.FromMilliseconds(25);
    internal static readonly TimeSpan CallbackCloseDrainBudget = TimeSpan.FromMilliseconds(50);

    private readonly CombatState _root;
    private readonly int _priorBattleHpLost;
    private readonly Stopwatch _watch;
    private readonly Func<InCombatProgressUpdate, bool>? _progressCallback;
    private readonly CancellationToken _searchCancellation;
    private readonly CancellationToken _hardDeadlineToken;
    private CancellationToken _stopCancellation;
    private CancellationToken _stopHardDeadline;
    private readonly SearchWorkLedger? _ledger;
    private readonly bool _emitExtendedMetrics;
    private readonly object _passesLock = new();
    private readonly object _traceSync = new();
    private readonly List<InCombatPassTrack> _passes = [];
    private readonly object _reporterGate = new();
    private readonly CancellationTokenSource _reporterStopCts = new();
    private readonly ManualResetEventSlim _reporterExited = new(false);
    private Thread? _reporterThread;
    private volatile bool _samplingStopped;
    private volatile bool _stopRequested;
    private readonly ProgressCallbackGate _callbackGate = new();
    private int _finalMetricsFlushRequested;
    private int _reporterJoinedCleanly;
    private int _disposed;
    private int _cleanupRefs;
    private int _waveCommittedFlag;
    private Exception? _reporterException;

    private long _directExpandedNodes;
    private int _maxTurnReached;
    private long _lastProgressEmitMs = -ProgressIntervalMilliseconds;
    private long _lastEmittedExpandedNodes = -1;
    private long _lastEmittedExpandedParents = -1;
    private long _lastEmittedTransitions = -1;
    private long _lastEmittedAttempts = -1;
    private SearchNode? _lastTracedNode;
    private InCombatBeamResult? _lastTracedResult;
    private InCombatBeamResult? _lastEmittedBestResult;

    public InCombatSearchTracker(
        CombatState root,
        int priorBattleHpLost,
        Stopwatch watch,
        Action<InCombatProgressUpdate>? progressCallback,
        CancellationToken searchCancellation = default,
        CancellationToken hardDeadlineToken = default,
        SearchWorkLedger? ledger = null,
        bool emitExtendedMetrics = false)
        : this(
            root,
            priorBattleHpLost,
            watch,
            progressCallback is null
                ? null
                : update =>
                {
                    progressCallback(update);
                    return true;
                },
            searchCancellation,
            hardDeadlineToken,
            ledger,
            emitExtendedMetrics)
    {
    }

    internal static InCombatSearchTracker CreateWithDeliveryTracking(
        CombatState root,
        int priorBattleHpLost,
        Stopwatch watch,
        Func<InCombatProgressUpdate, bool>? progressCallback,
        CancellationToken searchCancellation = default,
        CancellationToken hardDeadlineToken = default,
        SearchWorkLedger? ledger = null,
        bool emitExtendedMetrics = false) =>
        new(
            root,
            priorBattleHpLost,
            watch,
            progressCallback,
            searchCancellation,
            hardDeadlineToken,
            ledger,
            emitExtendedMetrics);

    private InCombatSearchTracker(
        CombatState root,
        int priorBattleHpLost,
        Stopwatch watch,
        Func<InCombatProgressUpdate, bool>? progressCallback,
        CancellationToken searchCancellation,
        CancellationToken hardDeadlineToken,
        SearchWorkLedger? ledger,
        bool emitExtendedMetrics)
    {
        _root = root;
        _priorBattleHpLost = priorBattleHpLost;
        _watch = watch;
        _progressCallback = progressCallback;
        _searchCancellation = searchCancellation;
        _hardDeadlineToken = hardDeadlineToken;
        _ledger = ledger;
        _emitExtendedMetrics = emitExtendedMetrics;
        _maxTurnReached = Math.Max(1, root.Players[0].PlayerCombatState?.TurnNumber ?? 1);

        if (_progressCallback is not null)
        {
            _cleanupRefs = 2;
            try
            {
                _reporterThread = StartReporterThread();
            }
            catch
            {
                Volatile.Write(ref _cleanupRefs, 1);
                _reporterExited.Set();
                throw;
            }
        }
        else
        {
            _cleanupRefs = 1;
            _reporterExited.Set();
        }
    }

    private void ReleaseCleanupRef()
    {
        if (Interlocked.Decrement(ref _cleanupRefs) == 0)
        {
            _reporterStopCts.Dispose();
            _reporterExited.Dispose();
        }
    }

    private Thread StartReporterThread()
    {
        var thread = new Thread(ReporterLoop)
        {
            Name = "Sts2Sim.InCombatReporter",
            IsBackground = true,
            Priority = ThreadPriority.Normal,
        };
        using (ExecutionContext.SuppressFlow())
        {
            thread.Start();
        }
        return thread;
    }

    public InCombatPassTrack RegisterPass()
    {
        var track = new InCombatPassTrack(this);
        lock (_passesLock)
            _passes.Add(track);
        return track;
    }

    public void ObserveExpansion(long delta, int turn)
    {
        if (delta > 0)
            Interlocked.Add(ref _directExpandedNodes, delta);
        ObserveTurn(turn);
    }

    internal void ObserveTurn(int turn)
    {
        if (turn > Volatile.Read(ref _maxTurnReached))
            UpdateMaxTurn(turn);
    }

    internal void NotifyWaveCommitted()
    {
        if (_reporterThread is not null
            && !_stopRequested
            && Volatile.Read(ref _waveCommittedFlag) == 0
            && Interlocked.Exchange(ref _waveCommittedFlag, 1) == 0)
        {
            lock (_reporterGate)
            {
                if (!_stopRequested)
                    Monitor.Pulse(_reporterGate);
            }
        }
    }

    public long GetTotalExpandedNodes()
    {
        InCombatPassTrack[] tracks;
        lock (_passesLock)
            tracks = _passes.ToArray();

        long total = Interlocked.Read(ref _directExpandedNodes);
        var seenTrackers = new HashSet<ForecastNodeBudgetTracker>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < tracks.Length; i++)
        {
            InCombatPassTrack track = tracks[i];
            long observed = track.GetFallbackObservedNodes();
            if (track.BudgetTracker is { } budgetTracker)
            {
                if (seenTrackers.Add(budgetTracker))
                    total += Math.Max(budgetTracker.Consumed, observed);
            }
            else
            {
                total += observed;
            }
        }
        return total;
    }

    private long GetReportedExpandedNodes() =>
        _ledger is not null
            ? (_ledger.UseParentBudget
                ? _ledger.BudgetUnits
                : Math.Max(_ledger.BudgetUnits, GetTotalExpandedNodes()))
            : GetTotalExpandedNodes();

    public void StopRegularSampling()
    {
        _samplingStopped = true;
        if (Volatile.Read(ref _disposed) != 0)
            return;
        _reporterStopCts.Cancel();
        lock (_reporterGate)
            Monitor.PulseAll(_reporterGate);
    }

    public void StopAndJoin(
        CancellationToken hardDeadlineToken = default,
        bool emitFinalMetricsFlush = false,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        CancellationToken effectiveHard = hardDeadlineToken.CanBeCanceled ? hardDeadlineToken : _hardDeadlineToken;
        CancellationToken effectiveCancel = cancellationToken.CanBeCanceled ? cancellationToken : _searchCancellation;

        bool requestFlush = (emitFinalMetricsFlush
            || effectiveCancel.IsCancellationRequested
            || effectiveHard.IsCancellationRequested)
            && _emitExtendedMetrics
            && _progressCallback is not null;
        _stopCancellation = effectiveCancel;
        _stopHardDeadline = effectiveHard;
        if (requestFlush)
            Volatile.Write(ref _finalMetricsFlushRequested, 1);

        _samplingStopped = true;
        _stopRequested = true;
        Thread? thread = Interlocked.Exchange(ref _reporterThread, null);
        _reporterStopCts.Cancel();
        lock (_reporterGate)
            Monitor.PulseAll(_reporterGate);

        bool joined = thread is null;
        if (thread is not null)
        {
            if (requestFlush)
            {
                joined = WaitForReporterExitBounded(thread, effectiveHard, FinalMetricsFlushWaitBudget);
            }
            else
            {
                joined = WaitForNormalShutdown(thread, effectiveCancel, effectiveHard);
                if (!joined
                    && (effectiveCancel.IsCancellationRequested || effectiveHard.IsCancellationRequested)
                    && _emitExtendedMetrics
                    && _progressCallback is not null)
                {
                    Volatile.Write(ref _finalMetricsFlushRequested, 1);
                    joined = WaitForReporterExitBounded(thread, effectiveHard, FinalMetricsFlushWaitBudget);
                }
            }
        }

        bool callbacksDrained = _callbackGate.Close(
            effectiveHard.IsCancellationRequested ? ExpiredDeadlineWaitBudget : CallbackCloseDrainBudget);
        if (!joined && callbacksDrained && thread is not null && _reporterExited.Wait(ExpiredDeadlineWaitBudget))
        {
            thread.Join();
            joined = true;
        }
        if (joined)
            Volatile.Write(ref _reporterJoinedCleanly, 1);

        ReleaseCleanupRef();

        if (joined)
        {
            Exception? reporterError = Volatile.Read(ref _reporterException);
            if (reporterError is not null)
                ExceptionDispatchInfo.Capture(reporterError).Throw();
        }
    }

    private bool WaitForNormalShutdown(Thread thread, CancellationToken effectiveCancel, CancellationToken effectiveHard)
    {
        if (effectiveHard.IsCancellationRequested || effectiveCancel.IsCancellationRequested)
        {
            if (_reporterExited.Wait(ExpiredDeadlineWaitBudget))
            {
                thread.Join();
                return true;
            }
            return false;
        }

        using CancellationTokenSource waitCts = CreateLinkedWaitCts(effectiveCancel, effectiveHard);
        waitCts.CancelAfter(NormalShutdownWaitBudget);
        try
        {
            _reporterExited.Wait(waitCts.Token);
            thread.Join();
            return true;
        }
        catch (OperationCanceledException)
        {
            // fail-open: 正常关闭等待期间收到取消、硬期限或短宽限超时，避免阻塞调用方退出路径。
            return false;
        }
    }

    private bool WaitForReporterExitBounded(Thread thread, CancellationToken effectiveHard, TimeSpan budget)
    {
        if (effectiveHard.IsCancellationRequested)
        {
            if (_reporterExited.Wait(ExpiredDeadlineWaitBudget))
            {
                thread.Join();
                return true;
            }
            return false;
        }

        if (effectiveHard.CanBeCanceled)
        {
            using CancellationTokenSource flushWaitCts = CancellationTokenSource.CreateLinkedTokenSource(effectiveHard);
            flushWaitCts.CancelAfter(budget);
            try
            {
                _reporterExited.Wait(flushWaitCts.Token);
                thread.Join();
                return true;
            }
            catch (OperationCanceledException)
            {
                // fail-open: 最终指标刷新等待超出硬期限或短等待上限，避免阻塞取消或超时退出路径。
                return false;
            }
        }

        if (_reporterExited.Wait(budget))
        {
            thread.Join();
            return true;
        }
        return false;
    }

    private static CancellationTokenSource CreateLinkedWaitCts(CancellationToken first, CancellationToken second)
    {
        if (first.CanBeCanceled && second.CanBeCanceled)
            return CancellationTokenSource.CreateLinkedTokenSource(first, second);
        if (first.CanBeCanceled)
            return CancellationTokenSource.CreateLinkedTokenSource(first);
        if (second.CanBeCanceled)
            return CancellationTokenSource.CreateLinkedTokenSource(second);
        return new CancellationTokenSource();
    }

    public InCombatBeamResult? LastCachedBestResult => Volatile.Read(ref _lastEmittedBestResult);

    public InCombatBeamResult? TryGetBestSoFarOnCancel(
        CancellationToken hardDeadlineToken,
        TimeSpan fallbackBudget)
    {
        InCombatBeamResult? cached = LastCachedBestResult;
        if (hardDeadlineToken.IsCancellationRequested || fallbackBudget <= TimeSpan.Zero)
            return RestampMetrics(cached);

        using var traceCts = hardDeadlineToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(hardDeadlineToken)
            : new CancellationTokenSource();
        traceCts.CancelAfter(fallbackBudget);
        try
        {
            return RestampMetrics(TryBuildBestSoFarResult(traceCts.Token) ?? cached);
        }
        catch (OperationCanceledException)
        {
            // fail-open: 取消后补提部分最优路线触及硬期限或短回退预算，退回已缓存的最优结果避免阻塞调用方。
            return RestampMetrics(cached);
        }
    }

    private InCombatBeamResult? RestampMetrics(InCombatBeamResult? result)
    {
        if (result is null)
            return null;

        long reported = GetReportedExpandedNodes();
        double elapsedMs = Math.Max(result.SearchElapsedMilliseconds, _watch.Elapsed.TotalMilliseconds);
        if (_emitExtendedMetrics && _ledger is not null)
        {
            SearchWorkSnapshot work = _ledger.Snapshot();
            return result with
            {
                ExpandedNodes = Math.Max(result.ExpandedNodes, work.ExpandedParents),
                SearchElapsedMilliseconds = elapsedMs,
                ExpandedParents = work.ExpandedParents,
                TransitionCount = work.TransitionCount,
                ChoiceReplayAttempts = work.ChoiceReplayAttempts,
                ObservedPeakParallelism = work.ObservedPeakParallelism,
                ActivePhaseLabel = work.ActivePhaseLabel,
            };
        }

        long drained = Math.Max(result.ExpandedNodes, Math.Max(reported, GetTotalExpandedNodes()));
        return result with
        {
            ExpandedNodes = drained,
            SearchElapsedMilliseconds = elapsedMs,
        };
    }

    public InCombatBeamResult? TryBuildBestSoFarResult(CancellationToken traceCancellation = default)
    {
        lock (_traceSync)
            return TryBuildOrRefreshBest(_watch.Elapsed.TotalMilliseconds, updateCachedBest: false, traceCancellation);
    }

    private void ReporterLoop()
    {
        try
        {
            try
            {
                using CancellationTokenSource linkedTraceCts = CancellationTokenSource.CreateLinkedTokenSource(
                    _reporterStopCts.Token,
                    _searchCancellation,
                    _hardDeadlineToken);

                while (!_stopRequested && !_samplingStopped && !linkedTraceCts.IsCancellationRequested)
                {
                    long lastEmit = Volatile.Read(ref _lastProgressEmitMs);
                    if (lastEmit < 0)
                    {
                        lock (_reporterGate)
                        {
                            if (_stopRequested || _samplingStopped || linkedTraceCts.IsCancellationRequested)
                                break;
                            if (GetReportedExpandedNodes() == 0 && Volatile.Read(ref _waveCommittedFlag) == 0)
                            {
                                Monitor.Wait(_reporterGate, 25);
                                continue;
                            }
                        }
                    }
                    else
                    {
                        long remainingWait = ProgressIntervalMilliseconds - (_watch.ElapsedMilliseconds - lastEmit);
                        if (remainingWait > 0)
                        {
                            lock (_reporterGate)
                            {
                                if (_stopRequested || _samplingStopped || linkedTraceCts.IsCancellationRequested)
                                    break;
                                Monitor.Wait(_reporterGate, (int)Math.Min(int.MaxValue, remainingWait));
                            }
                        }
                    }

                    if (_stopRequested || _samplingStopped || linkedTraceCts.IsCancellationRequested)
                        break;

                    lastEmit = Volatile.Read(ref _lastProgressEmitMs);
                    if (lastEmit < 0 && GetReportedExpandedNodes() == 0)
                        continue;
                    if (lastEmit >= 0 && _watch.ElapsedMilliseconds - lastEmit < ProgressIntervalMilliseconds)
                        continue;

                    Volatile.Write(ref _waveCommittedFlag, 0);
                    EmitProgressSnapshot(linkedTraceCts.Token);
                }
            }
            catch (OperationCanceledException) when (_stopRequested || _samplingStopped || _searchCancellation.IsCancellationRequested || _hardDeadlineToken.IsCancellationRequested)
            {
                // fail-open: 搜索取消或超出硬期限时停止周期采样，待汇合信号到达后决定是否发送最终指标刷新。
            }

            if (!_stopRequested)
            {
                lock (_reporterGate)
                {
                    while (!_stopRequested)
                        Monitor.Wait(_reporterGate);
                }
            }

            bool shouldFlush = (Volatile.Read(ref _finalMetricsFlushRequested) != 0
                || _searchCancellation.IsCancellationRequested
                || _hardDeadlineToken.IsCancellationRequested
                || _stopCancellation.IsCancellationRequested
                || _stopHardDeadline.IsCancellationRequested)
                && _emitExtendedMetrics
                && _progressCallback is not null;
            if (shouldFlush
                && !_callbackGate.IsClosed)
            {
                EmitFinalMetricsOnReporterThread();
            }
        }
        catch (Exception ex)
        {
            // fail-open: 捕获后台报告线程异常，并在 StopAndJoin 汇合后同步重抛给调用方。
            Volatile.Write(ref _reporterException, ex);
        }
        finally
        {
            _reporterExited.Set();
            ReleaseCleanupRef();
        }
    }

    private void EmitProgressSnapshot(CancellationToken traceCt)
    {
        if (_progressCallback is null
            || _callbackGate.IsClosed
            || _samplingStopped
            || _stopRequested
            || traceCt.IsCancellationRequested)
        {
            return;
        }

        InCombatBeamResult? bestResult;
        lock (_traceSync)
        {
            if (_callbackGate.IsClosed || _samplingStopped || _stopRequested || traceCt.IsCancellationRequested)
                return;
            bestResult = TryBuildOrRefreshBest(_watch.Elapsed.TotalMilliseconds, updateCachedBest: true, traceCt);
            Volatile.Write(ref _lastProgressEmitMs, _watch.ElapsedMilliseconds);
        }

        if (_callbackGate.IsClosed || _samplingStopped || _stopRequested || traceCt.IsCancellationRequested)
            return;

        long expanded = GetReportedExpandedNodes();
        SearchWorkSnapshot? work = _emitExtendedMetrics && _ledger is not null
            ? _ledger.Snapshot()
            : null;
        int maxTurn = Volatile.Read(ref _maxTurnReached);
        int projectedHpLoss = bestResult?.Terminal.ProjectedBattleHpLost ?? _priorBattleHpLost;
        bool won = bestResult is { Terminal.AllEnemiesDead: true, Terminal.PlayerDead: false };
        int? endedTurn = bestResult?.Terminal.CombatEndedTurn;
        int potionCount = 0;
        if (bestResult is { Terminal.Turn: > 0 })
        {
            IReadOnlyList<InCombatTurnOutcomeTrace> outcomes = bestResult.TurnOutcomes;
            for (int i = 0; i < outcomes.Count; i++)
                potionCount += outcomes[i].PotionCount;
        }
        if (_emitExtendedMetrics && work is { } snap && bestResult is not null)
        {
            bestResult = bestResult with
            {
                ExpandedNodes = snap.ExpandedParents,
                ExpandedParents = snap.ExpandedParents,
                TransitionCount = snap.TransitionCount,
                ChoiceReplayAttempts = snap.ChoiceReplayAttempts,
                ObservedPeakParallelism = snap.ObservedPeakParallelism,
                ActivePhaseLabel = snap.ActivePhaseLabel,
            };
        }

        var update = new InCombatProgressUpdate(
            ExpandedNodes: expanded,
            MaxTurnReached: maxTurn,
            ElapsedMilliseconds: _watch.Elapsed.TotalMilliseconds,
            CurrentBestProjectedHpLoss: projectedHpLoss,
            CurrentBestWon: won,
            CurrentBestCombatEndedTurn: endedTurn,
            CurrentBestPotionCount: potionCount,
            CurrentBest: bestResult,
            ExpandedParents: work?.ExpandedParents,
            TransitionCount: work?.TransitionCount,
            ChoiceReplayAttempts: work?.ChoiceReplayAttempts,
            ObservedPeakParallelism: work?.ObservedPeakParallelism,
            ActivePhaseLabel: work?.ActivePhaseLabel);

        if (_callbackGate.IsClosed || _samplingStopped || _stopRequested || traceCt.IsCancellationRequested)
            return;

        _callbackGate.TryInvoke(
            () =>
            {
                if (_progressCallback(update))
                    RecordEmittedMetrics(expanded, work);
            },
            canInvoke: () => !_samplingStopped && !_stopRequested && !traceCt.IsCancellationRequested);
    }

    private void RecordEmittedMetrics(long expanded, SearchWorkSnapshot? work)
    {
        Volatile.Write(ref _lastEmittedExpandedNodes, expanded);
        if (work is { } sent)
        {
            Volatile.Write(ref _lastEmittedExpandedParents, sent.ExpandedParents);
            Volatile.Write(ref _lastEmittedTransitions, sent.TransitionCount);
            Volatile.Write(ref _lastEmittedAttempts, sent.ChoiceReplayAttempts);
        }
    }

    private void EmitFinalMetricsOnReporterThread()
    {
        if (!_emitExtendedMetrics || _progressCallback is null || _callbackGate.IsClosed)
            return;

        long expanded = GetReportedExpandedNodes();
        SearchWorkSnapshot? work = _emitExtendedMetrics && _ledger is not null
            ? _ledger.Snapshot()
            : null;
        bool hasNonZero = expanded > 0
            || (work is { } w && (w.ExpandedParents > 0 || w.TransitionCount > 0 || w.ChoiceReplayAttempts > 0));
        if (!hasNonZero)
            return;

        bool changed = expanded > Volatile.Read(ref _lastEmittedExpandedNodes)
            || (work is { } ws && (
                ws.ExpandedParents > Volatile.Read(ref _lastEmittedExpandedParents)
                || ws.TransitionCount > Volatile.Read(ref _lastEmittedTransitions)
                || ws.ChoiceReplayAttempts > Volatile.Read(ref _lastEmittedAttempts)));
        if (!changed)
            return;

        InCombatBeamResult? cached = LastCachedBestResult;
        int projectedHpLoss = cached?.Terminal.ProjectedBattleHpLost ?? _priorBattleHpLost;
        bool won = cached is { Terminal.AllEnemiesDead: true, Terminal.PlayerDead: false };
        int? endedTurn = cached?.Terminal.CombatEndedTurn;
        int potionCount = 0;
        if (cached is { Terminal.Turn: > 0 })
        {
            IReadOnlyList<InCombatTurnOutcomeTrace> outcomes = cached.TurnOutcomes;
            for (int i = 0; i < outcomes.Count; i++)
                potionCount += outcomes[i].PotionCount;
        }

        var update = new InCombatProgressUpdate(
            ExpandedNodes: expanded,
            MaxTurnReached: Volatile.Read(ref _maxTurnReached),
            ElapsedMilliseconds: _watch.Elapsed.TotalMilliseconds,
            CurrentBestProjectedHpLoss: projectedHpLoss,
            CurrentBestWon: won,
            CurrentBestCombatEndedTurn: endedTurn,
            CurrentBestPotionCount: potionCount,
            CurrentBest: null,
            ExpandedParents: work?.ExpandedParents,
            TransitionCount: work?.TransitionCount,
            ChoiceReplayAttempts: work?.ChoiceReplayAttempts,
            ObservedPeakParallelism: work?.ObservedPeakParallelism,
            ActivePhaseLabel: work?.ActivePhaseLabel,
            IsFinalMetricsFlush: true);

        if (_callbackGate.IsClosed)
            return;

        _callbackGate.TryInvoke(() =>
        {
            if (_progressCallback(update))
                RecordEmittedMetrics(expanded, work);
        });
    }

    private void UpdateMaxTurn(int turn)
    {
        while (true)
        {
            int current = Volatile.Read(ref _maxTurnReached);
            if (turn <= current)
                return;
            if (Interlocked.CompareExchange(ref _maxTurnReached, turn, current) == current)
                return;
        }
    }

    private InCombatBeamResult? TryBuildOrRefreshBest(
        double elapsedMs,
        bool updateCachedBest,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!TrySelectWinningCandidate(
                out SearchNode? bestNode,
                out StateEvaluator? selectedEvaluator,
                out int selectedProjectedHp,
                out bool onlyDeathRoutes,
                out ForecastSearchBoundary passBoundary,
                ct))
        {
            return updateCachedBest ? LastCachedBestResult : null;
        }

        long currentExpanded = GetReportedExpandedNodes();
        SearchWorkSnapshot? work = _emitExtendedMetrics && _ledger is not null
            ? _ledger.Snapshot()
            : null;
        ForecastSearchBoundary boundary = bestNode!.Won
            ? ForecastSearchBoundary.None
            : bestNode.NodeBoundary != ForecastSearchBoundary.None
                ? bestNode.NodeBoundary
                : passBoundary != ForecastSearchBoundary.None
                    ? passBoundary
                    : !bestNode.Ended
                        ? ForecastSearchBoundary.NodeLimit
                        : ForecastSearchBoundary.None;

        InCombatBeamResult traced;
        if (ReferenceEquals(bestNode, _lastTracedNode) && _lastTracedResult is not null)
        {
            ForecastConfidence updatedConfidence = onlyDeathRoutes
                ? ForecastConfidence.DeathOnly
                : _lastTracedResult.Terminal.CombatEndedTurn.HasValue
                    ? ForecastConfidence.Complete
                    : ForecastConfidence.Bounded;
            traced = _lastTracedResult with
            {
                Terminal = _lastTracedResult.Terminal with
                {
                    OnlyDeathRoutes = onlyDeathRoutes,
                    Confidence = updatedConfidence,
                    SearchBoundary = boundary,
                    HasRisk = !bestNode.Ended && boundary != ForecastSearchBoundary.None,
                },
                ExpandedNodes = currentExpanded,
                SearchElapsedMilliseconds = elapsedMs,
                ExpandedParents = work?.ExpandedParents,
                TransitionCount = work?.TransitionCount,
                ChoiceReplayAttempts = work?.ChoiceReplayAttempts,
                ObservedPeakParallelism = work?.ObservedPeakParallelism,
                ActivePhaseLabel = work?.ActivePhaseLabel,
            };
        }
        else
        {
            ct.ThrowIfCancellationRequested();
            traced = InCombatRouteTracer.TraceRoute(
                _root,
                bestNode.Route(),
                bestNode,
                selectedProjectedHp,
                onlyDeathRoutes,
                boundary,
                currentExpanded,
                _priorBattleHpLost,
                elapsedMs,
                selectedEvaluator!,
                ct,
                workSnapshot: work);
            _lastTracedNode = bestNode;
            _lastTracedResult = traced;
        }

        if (updateCachedBest)
            Volatile.Write(ref _lastEmittedBestResult, traced);
        return traced;
    }

    private bool TrySelectWinningCandidate(
        out SearchNode? bestNode,
        out StateEvaluator? selectedEvaluator,
        out int selectedProjectedHp,
        out bool onlyDeathRoutes,
        out ForecastSearchBoundary bestBoundary,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        InCombatPassTrack[] tracks;
        lock (_passesLock)
            tracks = _passes.ToArray();

        var passSnapshots = new List<InCombatPassTrack.PassUsableSnapshot>(tracks.Length);
        for (int t = 0; t < tracks.Length; t++)
        {
            ct.ThrowIfCancellationRequested();
            if (tracks[t].TrySnapshotUsablePool(out InCombatPassTrack.PassUsableSnapshot snap, ct))
                passSnapshots.Add(snap);
        }

        bestNode = null;
        selectedEvaluator = null;
        selectedProjectedHp = 0;
        onlyDeathRoutes = true;
        bestBoundary = ForecastSearchBoundary.None;
        if (passSnapshots.Count == 0)
            return false;

        var seenNodes = new HashSet<SearchNode>(ReferenceEqualityComparer.Instance);
        var allCandidates = new List<SearchNode>(128);
        var nodeMeta = new Dictionary<SearchNode, (StateEvaluator Evaluator, ForecastSearchBoundary Boundary, int? ProjectedHp, bool InProgress)>(
            ReferenceEqualityComparer.Instance);
        var deathCheckPool = new List<(SearchNode Node, StateEvaluator Evaluator, int? PrecomputedHp)>(128);

        for (int p = 0; p < passSnapshots.Count; p++)
        {
            ct.ThrowIfCancellationRequested();
            InCombatPassTrack.PassUsableSnapshot snap = passSnapshots[p];
            if (snap.FinishedOutcome is { } finished)
            {
                EvaluatedForecastCandidate[] evals = finished.EvaluatedCandidates;
                for (int i = 0; i < evals.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    EvaluatedForecastCandidate evalCand = evals[i];
                    nodeMeta.TryAdd(
                        evalCand.Node,
                        (snap.Evaluator, finished.PassBoundary, evalCand.ProjectedPlayerHp, false));
                    deathCheckPool.Add((evalCand.Node, snap.Evaluator, evalCand.ProjectedPlayerHp));
                }
            }

            SearchNode[] pool = snap.Pool;
            for (int i = 0; i < pool.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                SearchNode candidate = pool[i];
                if (seenNodes.Add(candidate))
                    allCandidates.Add(candidate);
                if (!nodeMeta.ContainsKey(candidate))
                {
                    nodeMeta.Add(candidate, (snap.Evaluator, snap.Boundary, null, snap.FinishedOutcome is null));
                    deathCheckPool.Add((candidate, snap.Evaluator, null));
                }
            }
        }

        if (allCandidates.Count == 0)
            return false;

        SearchNode? winner = PlanSelector.TrySelectWithPool(
            allCandidates,
            passSnapshots[0].InitialHp,
            passSnapshots[0].Evaluator,
            out var eligible, ct);
        if (winner is null)
            return false;

        var win = nodeMeta[winner];
        try
        {
            ct.ThrowIfCancellationRequested();
            if (winner.State.Players[0].Creature.IsDead && CanUseBoundedPreview(win.Boundary, win.InProgress))
            {
                winner = PlanSelector.PreferSurvivingTurnBoundary(winner, eligible, _root.RoundNumber,
                    passSnapshots[0].InitialHp, passSnapshots[0].Evaluator, node =>
                    {
                        var source = nodeMeta[node];
                        return CanUseBoundedPreview(source.Boundary, source.InProgress)
                            && (source.ProjectedHp ?? source.Evaluator.ComputeProjectedPlayerHp(node, node.NodeBoundary)) > 0;
                    }, ct);
                win = nodeMeta[winner];
            }
            selectedProjectedHp = win.ProjectedHp ?? win.Evaluator.ComputeProjectedPlayerHp(winner, winner.NodeBoundary);
            if (selectedProjectedHp > 0
                && winner.State.Players[0].Creature.IsAlive
                && winner.State.Players[0].Creature.CurrentHp > 0)
            {
                onlyDeathRoutes = false;
            }
            else
            {
                for (int i = 0; i < deathCheckPool.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    (SearchNode cand, StateEvaluator candEval, int? preHp) = deathCheckPool[i];
                    if (ReferenceEquals(cand, winner))
                        continue;
                    if (!cand.State.Players[0].Creature.IsAlive || cand.State.Players[0].Creature.CurrentHp <= 0)
                        continue;
                    int hp = preHp ?? candEval.ComputeProjectedPlayerHp(cand, cand.NodeBoundary);
                    if (hp > 0)
                    {
                        onlyDeathRoutes = false;
                        break;
                    }
                }
            }
        }
        catch (ProjectionUnsupportedException)
        {
            // fail-open: 未建模的死亡防线在流式中间预览或部分结果提取阶段跳过该次预览，交由主流程决定回退或失败处理。
            return false;
        }

        bestNode = winner;
        selectedEvaluator = win.Evaluator;
        bestBoundary = win.Boundary;
        return true;

        static bool CanUseBoundedPreview(ForecastSearchBoundary boundary, bool inProgress) =>
            boundary is ForecastSearchBoundary.NodeLimit or ForecastSearchBoundary.TimeLimit
            || (boundary == ForecastSearchBoundary.None && inProgress);
    }
}
