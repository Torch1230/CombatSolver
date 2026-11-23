using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace CombatSolver.Beam;

internal sealed class ForecastWorkGroup(
    Action<int, ForecastWorkItem> handler,
    CancellationToken ct,
    SearchWorkLedger? ledger,
    long initialPending,
    int homeLane,
    int maxConcurrency = int.MaxValue)
{
    public readonly Action<int, ForecastWorkItem> Handler = handler;
    public readonly CancellationToken Ct = ct;
    public readonly SearchWorkLedger? Ledger = ledger;
    public readonly ConcurrentQueue<ForecastWorkItem> PendingInbox = new();
    public readonly int HomeLane = homeLane;
    public readonly int MaxConcurrency = Math.Max(1, maxConcurrency);
    public readonly object DoneGate = new();
    private int _activeLanes;
    public long PendingCount = initialPending;
    public int AbortRequested;
    public int Disposed;
    public Exception? FirstException;
    public readonly ManualResetEventSlim Done = new(initialPending <= 0);

    public bool TryAcquireLane()
    {
        while (true)
        {
            int current = Volatile.Read(ref _activeLanes);
            if (current >= MaxConcurrency)
                return false;
            if (Interlocked.CompareExchange(ref _activeLanes, current + 1, current) == current)
                return true;
        }
    }

    public void ReleaseLane() => Interlocked.Decrement(ref _activeLanes);

    public void RecordException(Exception ex)
    {
        Interlocked.CompareExchange(ref FirstException, ex, null);
        Volatile.Write(ref AbortRequested, 1);
    }

    public void SignalDone()
    {
        lock (DoneGate)
        {
            if (Disposed == 0)
                Done.Set();
        }
    }

    public void DecrementPending()
    {
        if (Interlocked.Decrement(ref PendingCount) == 0)
            SignalDone();
    }
}

internal sealed class ForecastWorkItem
{
    public const byte KindParent = 0;
    public const byte KindAction = 1;
    public const byte KindChoice = 2;
    public const byte KindCustom = 3;

    public ForecastWorkGroup Group = null!;
    public byte Kind;
    public int ParentIndex;
    public int ActionIndex;
    public int ChoiceDepth;
    public SearchNode ParentNode = null!;
    public PlannedAction? Action;
    public ChoiceScriptNode? Script;
    public ForecastWaveExecutor.ChoiceTreeNode? ChoiceNode;
    public object? Payload;
}

internal sealed class ForecastLanePool : IDisposable
{
    private const int MaxLocalPopStreak = 4;

    internal sealed class WorkStealingDeque
    {
        private const int Capacity = 1024;
        private const int Mask = Capacity - 1;
        private readonly ForecastWorkItem?[] _buffer = new ForecastWorkItem?[Capacity];
        private long _top;
        private long _bottom;

        public bool TryPush(ForecastWorkItem item)
        {
            long b = Volatile.Read(ref _bottom);
            long t = Volatile.Read(ref _top);
            if (b - t >= Capacity - 1)
                return false;
            Volatile.Write(ref _buffer[(int)(b & Mask)], item);
            Volatile.Write(ref _bottom, b + 1);
            return true;
        }

        public bool TryPop(out ForecastWorkItem? item)
        {
            long b = Volatile.Read(ref _bottom) - 1;
            Volatile.Write(ref _bottom, b);
            Interlocked.MemoryBarrier();
            long t = Volatile.Read(ref _top);
            if (t <= b)
            {
                item = Volatile.Read(ref _buffer[(int)(b & Mask)]);
                if (t == b)
                {
                    if (Interlocked.CompareExchange(ref _top, t + 1, t) != t)
                    {
                        item = null;
                        Volatile.Write(ref _bottom, t + 1);
                        return false;
                    }
                    Volatile.Write(ref _bottom, t + 1);
                }
                // Only the owner can push to this end. A finished job's group
                // holds the entire expansion wave through its handler.
                Volatile.Write(ref _buffer[(int)(b & Mask)], null);
                return true;
            }

            Volatile.Write(ref _bottom, t);
            item = null;
            return false;
        }

        public bool TrySteal(out ForecastWorkItem? item)
        {
            long t = Volatile.Read(ref _top);
            Interlocked.MemoryBarrier();
            long b = Volatile.Read(ref _bottom);
            if (t < b)
            {
                item = Volatile.Read(ref _buffer[(int)(t & Mask)]);
                if (Interlocked.CompareExchange(ref _top, t + 1, t) != t)
                {
                    item = null;
                    return false;
                }
                // The owner may already have wrapped around since we advanced top.
                Interlocked.CompareExchange(ref _buffer[(int)(t & Mask)], null, item);
                return true;
            }

            item = null;
            return false;
        }

        public void Clear()
        {
            Array.Clear(_buffer);
            _top = 0;
            _bottom = 0;
        }
    }

    private readonly WorkStealingDeque[] _deques;
    private readonly Stack<ForecastWorkItem>[] _localOverflows;
    private readonly int[] _localPopStreak;
    private readonly Thread[] _workerThreads;
    private readonly HashSet<ForecastWorkGroup> _activeGroups = [];
    private volatile ForecastWorkGroup[] _activeGroupSnapshot = [];
    private readonly object _gate = new();
    private long _globalPendingCount;
    private int _roundRobinCursor;
    private int _sleepingWorkers;
    private bool _disposed;

    public int LaneCount { get; }

    public ForecastLanePool(int laneCount, Action<Thread>? threadStarter = null)
    {
        LaneCount = Math.Max(1, laneCount);
        _deques = new WorkStealingDeque[LaneCount];
        _localOverflows = new Stack<ForecastWorkItem>[LaneCount];
        _localPopStreak = new int[LaneCount];
        for (int i = 0; i < LaneCount; i++)
        {
            _deques[i] = new WorkStealingDeque();
            _localOverflows[i] = new Stack<ForecastWorkItem>(16);
        }

        int workerCount = LaneCount > 1 ? LaneCount : 0;
        _workerThreads = new Thread[workerCount];
        if (workerCount > 0)
        {
            int startedCount = 0;
            try
            {
                using (ExecutionContext.SuppressFlow())
                {
                    for (int i = 0; i < workerCount; i++)
                    {
                        int laneIndex = i;
                        var thread = new Thread(() => WorkerLoop(laneIndex))
                        {
                            Name = $"Sts2Sim.ForecastLane.{laneIndex}",
                            IsBackground = true,
                            Priority = ThreadPriority.Normal,
                        };
                        _workerThreads[i] = thread;
                        if (threadStarter is not null)
                            threadStarter(thread);
                        else
                            thread.Start();
                        startedCount++;
                    }
                }
            }
            catch
            {
                // fail-open: 线程池启动中途异常时回收已启动的工作线程再重抛原始异常。
                lock (_gate)
                {
                    _disposed = true;
                    Monitor.PulseAll(_gate);
                }
                for (int j = 0; j < startedCount; j++)
                    _workerThreads[j].Join();
                throw;
            }
        }
    }

    private void RefreshActiveSnapshotLocked()
    {
        _activeGroupSnapshot = _activeGroups.Count == 0
            ? []
            : _activeGroups.ToArray();
    }

    public void ReservePending(ForecastWorkGroup group, int count)
    {
        if (count <= 0)
            return;
        Interlocked.Add(ref group.PendingCount, count);
        Interlocked.Add(ref _globalPendingCount, count);
        if (Volatile.Read(ref _sleepingWorkers) > 0)
        {
            lock (_gate)
            {
                Monitor.PulseAll(_gate);
            }
        }
    }

    public void PushReserved(int laneIndex, ForecastWorkItem item, bool shareOverflow = false)
    {
        if (_deques[laneIndex].TryPush(item))
            return;
        // A whole action may run its entire DFS locally. Let other lanes take
        // excess independent actions while this lane is still busy with one.
        if (shareOverflow && LaneCount > 1)
            item.Group.PendingInbox.Enqueue(item);
        else
            _localOverflows[laneIndex].Push(item);
    }

    public void PushChild(int laneIndex, ForecastWorkItem item)
    {
        ReservePending(item.Group, 1);
        PushReserved(laneIndex, item);
    }

    public void ExecuteGroup(
        IReadOnlyList<ForecastWorkItem> initialItems,
        int homeLaneOffset,
        Action<int, ForecastWorkItem> handler,
        SearchWorkLedger? ledger,
        CancellationToken ct,
        int maxConcurrency = int.MaxValue)
    {
        if (initialItems.Count == 0)
            return;

        if (_workerThreads.Length == 0)
        {
            ExecuteSerialInline(initialItems, handler, ledger, ct);
            return;
        }

        int homeLane = ((homeLaneOffset % LaneCount) + LaneCount) % LaneCount;
        int groupCap = Math.Clamp(maxConcurrency, 1, LaneCount);
        var group = new ForecastWorkGroup(handler, ct, ledger, initialItems.Count, homeLane, groupCap);
        try
        {
            for (int i = 0; i < initialItems.Count; i++)
            {
                ForecastWorkItem item = initialItems[i];
                item.Group = group;
                group.PendingInbox.Enqueue(item);
            }

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _activeGroups.Add(group);
                RefreshActiveSnapshotLocked();
                Interlocked.Add(ref _globalPendingCount, initialItems.Count);
                if (_sleepingWorkers > 0)
                    Monitor.PulseAll(_gate);
            }

            group.Done.Wait();
            if (group.FirstException is { } err)
                ExceptionDispatchInfo.Capture(err).Throw();
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            lock (_gate)
            {
                if (_activeGroups.Remove(group))
                    RefreshActiveSnapshotLocked();
            }
            lock (group.DoneGate)
            {
                group.Disposed = 1;
                group.Done.Dispose();
            }
        }
    }

    private void ExecuteSerialInline(
        IReadOnlyList<ForecastWorkItem> initialItems,
        Action<int, ForecastWorkItem> handler,
        SearchWorkLedger? ledger,
        CancellationToken ct)
    {
        var group = new ForecastWorkGroup(handler, ct, ledger, initialItems.Count, homeLane: 0, maxConcurrency: 1);
        try
        {
            for (int i = 0; i < initialItems.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                ForecastWorkItem item = initialItems[i];
                item.Group = group;
                RunSerialItem(item, handler, ledger, ct);
                DrainSerialQueue(handler, ledger, ct);
            }
        }
        finally
        {
            _deques[0].Clear();
            _localOverflows[0].Clear();
            group.Done.Dispose();
        }

        static void RunSerialItem(
            ForecastWorkItem item,
            Action<int, ForecastWorkItem> handler,
            SearchWorkLedger? ledger,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ledger?.EnterActiveLane();
            try
            {
                handler(0, item);
            }
            finally
            {
                ledger?.ExitActiveLane();
            }
        }

        void DrainSerialQueue(
            Action<int, ForecastWorkItem> serialHandler,
            SearchWorkLedger? serialLedger,
            CancellationToken serialCt)
        {
            while (true)
            {
                ForecastWorkItem? queued = null;
                if (_localOverflows[0].Count > 0)
                    queued = _localOverflows[0].Pop();
                else if (!_deques[0].TryPop(out queued))
                    break;
                RunSerialItem(queued!, serialHandler, serialLedger, serialCt);
            }
        }
    }

    private void WorkerLoop(int laneIndex)
    {
        var spin = new SpinWait();
        while (true)
        {
            if (Volatile.Read(ref _globalPendingCount) <= 0)
            {
                lock (_gate)
                {
                    if (!_disposed && Volatile.Read(ref _globalPendingCount) <= 0)
                    {
                        Interlocked.Increment(ref _sleepingWorkers);
                        try
                        {
                            while (!_disposed && Volatile.Read(ref _globalPendingCount) <= 0)
                            {
                                Monitor.Wait(_gate);
                            }
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _sleepingWorkers);
                        }
                    }
                    if (_disposed)
                        return;
                }
                spin.Reset();
            }

            if (_disposed)
                return;

            if (TryPopOrSteal(laneIndex, out ForecastWorkItem? item, out bool acquiredLanePermit))
            {
                spin.Reset();
                ForecastWorkGroup group = item!.Group;
                try
                {
                    if (acquiredLanePermit
                        && Volatile.Read(ref group.AbortRequested) == 0
                        && !group.Ct.IsCancellationRequested)
                    {
                        group.Ledger?.EnterActiveLane();
                        try
                        {
                            group.Handler(laneIndex, item);
                        }
                        finally
                        {
                            group.Ledger?.ExitActiveLane();
                        }
                    }
                }
                catch (OperationCanceledException) when (group.Ct.IsCancellationRequested)
                {
                    // fail-open: Signal sibling items in this group to exit and rethrow OperationCanceledException on caller.
                    Volatile.Write(ref group.AbortRequested, 1);
                }
                catch (Exception ex)
                {
                    // fail-open: Capture first unhandled worker exception to rethrow via ExceptionDispatchInfo on caller.
                    group.RecordException(ex);
                }
                finally
                {
                    if (acquiredLanePermit)
                        group.ReleaseLane();
                    Interlocked.Decrement(ref _globalPendingCount);
                    group.DecrementPending();
                    if (acquiredLanePermit
                        && !group.PendingInbox.IsEmpty
                        && Volatile.Read(ref _sleepingWorkers) > 0)
                    {
                        lock (_gate)
                        {
                            Monitor.PulseAll(_gate);
                        }
                    }
                }
            }
            else
            {
                spin.SpinOnce(sleep1Threshold: 8);
            }
        }
    }

    private bool TryPopOrSteal(int laneIndex, out ForecastWorkItem? item, out bool acquiredLanePermit)
    {
        ForecastWorkGroup[] groups = _activeGroupSnapshot;
        if (groups.Length > 0
            && TryDequeueFromGroups(groups, laneIndex, homeOnly: true, activeOnly: true, out item, out acquiredLanePermit))
        {
            _localPopStreak[laneIndex] = 0;
            return true;
        }

        if (groups.Length > 1
            && _localPopStreak[laneIndex] >= MaxLocalPopStreak
            && TryDequeueFromGroups(groups, laneIndex, homeOnly: false, activeOnly: true, out item, out acquiredLanePermit))
        {
            _localPopStreak[laneIndex] = 0;
            return true;
        }

        Stack<ForecastWorkItem> overflow = _localOverflows[laneIndex];
        while (overflow.Count > 0)
        {
            ForecastWorkItem candidate = overflow.Pop();
            if (TryClaimDequeuedItem(candidate, out acquiredLanePermit))
            {
                _localPopStreak[laneIndex]++;
                item = candidate;
                return true;
            }
        }

        while (_deques[laneIndex].TryPop(out ForecastWorkItem? localCandidate))
        {
            if (TryClaimDequeuedItem(localCandidate!, out acquiredLanePermit))
            {
                _localPopStreak[laneIndex]++;
                item = localCandidate;
                return true;
            }
        }

        _localPopStreak[laneIndex] = 0;

        if (groups.Length > 0)
        {
            if (TryDequeueFromGroups(groups, laneIndex, homeOnly: false, activeOnly: true, out item, out acquiredLanePermit))
                return true;
            if (TryDequeueFromGroups(groups, laneIndex, homeOnly: false, activeOnly: false, out item, out acquiredLanePermit))
                return true;
        }

        for (int offset = 1; offset < LaneCount; offset++)
        {
            int victim = laneIndex + offset;
            if (victim >= LaneCount)
                victim -= LaneCount;
            if (_deques[victim].TrySteal(out ForecastWorkItem? stolenCandidate))
            {
                if (TryClaimDequeuedItem(stolenCandidate!, out acquiredLanePermit))
                {
                    item = stolenCandidate;
                    return true;
                }
            }
        }

        item = null;
        acquiredLanePermit = false;
        return false;
    }

    private bool TryDequeueFromGroups(
        ForecastWorkGroup[] groups,
        int laneIndex,
        bool homeOnly,
        bool activeOnly,
        out ForecastWorkItem? item,
        out bool acquiredLanePermit)
    {
        uint cursor = unchecked((uint)Interlocked.Increment(ref _roundRobinCursor));
        for (int i = 0; i < groups.Length; i++)
        {
            ForecastWorkGroup group = groups[(int)((cursor + (uint)i) % (uint)groups.Length)];
            if (homeOnly && group.HomeLane != laneIndex)
                continue;
            if (group.PendingInbox.IsEmpty)
                continue;

            if (activeOnly)
            {
                if (Volatile.Read(ref group.AbortRequested) != 0 || group.Ct.IsCancellationRequested)
                    continue;
                if (!group.TryAcquireLane())
                    continue;
                if (group.PendingInbox.TryDequeue(out item))
                {
                    acquiredLanePermit = true;
                    return true;
                }
                group.ReleaseLane();
            }
            else if ((Volatile.Read(ref group.AbortRequested) != 0 || group.Ct.IsCancellationRequested)
                && group.PendingInbox.TryDequeue(out item))
            {
                acquiredLanePermit = false;
                return true;
            }
        }

        item = null;
        acquiredLanePermit = false;
        return false;
    }

    private static bool TryClaimDequeuedItem(ForecastWorkItem candidate, out bool acquiredLanePermit)
    {
        ForecastWorkGroup targetGroup = candidate.Group;
        if (Volatile.Read(ref targetGroup.AbortRequested) != 0 || targetGroup.Ct.IsCancellationRequested)
        {
            acquiredLanePermit = false;
            return true;
        }
        if (targetGroup.TryAcquireLane())
        {
            acquiredLanePermit = true;
            return true;
        }

        targetGroup.PendingInbox.Enqueue(candidate);
        acquiredLanePermit = false;
        return false;
    }

    public void Dispose()
    {
        Thread[] threads;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            threads = _workerThreads;
            foreach (ForecastWorkGroup group in _activeGroups)
            {
                group.RecordException(new ObjectDisposedException(nameof(ForecastLanePool)));
                group.SignalDone();
            }
            Monitor.PulseAll(_gate);
        }

        for (int i = 0; i < threads.Length; i++)
            threads[i].Join();

        for (int i = 0; i < LaneCount; i++)
        {
            _deques[i].Clear();
            _localOverflows[i].Clear();
        }
    }
}
