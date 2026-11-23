using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Sts2Sim.Core.Combat;

namespace CombatSolver.Beam;

internal sealed class InCombatPassTrack(InCombatSearchTracker owner)
{
    internal readonly record struct PassUsableSnapshot(
        StateEvaluator Evaluator,
        int InitialHp,
        SearchNode[] Pool,
        ForecastSearchBoundary Boundary,
        ForecastPassOutcome? FinishedOutcome);

    private readonly object _sync = new();
    private StateEvaluator? _evaluator;
    private int _initialHp;
    private SearchNode[] _committedPool = [];
    private ForecastSearchBoundary _committedBoundary = ForecastSearchBoundary.None;
    private readonly List<SearchNode> _inFlightNodes = [];
    private ForecastPassOutcome? _finishedOutcome;
    private ForecastNodeBudgetTracker? _budgetTracker;
    private long _observedExpandedNodes;

    public StateEvaluator? Evaluator
    {
        get
        {
            lock (_sync)
                return _evaluator;
        }
    }

    public int InitialHp
    {
        get
        {
            lock (_sync)
                return _initialHp;
        }
    }

    public ForecastNodeBudgetTracker? BudgetTracker => Volatile.Read(ref _budgetTracker);

    public long GetFallbackObservedNodes() => Interlocked.Read(ref _observedExpandedNodes);

    public void AttachBudgetTracker(ForecastNodeBudgetTracker tracker) =>
        Volatile.Write(ref _budgetTracker, tracker);

    public void AttachEvaluator(StateEvaluator evaluator, int initialHp)
    {
        lock (_sync)
        {
            _evaluator = evaluator;
            _initialHp = initialHp;
        }
    }

    public void ObserveExpansion(long delta, int turn)
    {
        if (delta > 0)
            Interlocked.Add(ref _observedExpandedNodes, delta);
        owner.ObserveTurn(turn);
    }

    public void ObserveParentResult(ParentExpansionResult result)
    {
        if (result.Completed.Count == 0
            && result.Ended.Count == 0
            && result.Next.Count == 0
            && result.PotionFreeBoundaryFallback is null
            && result.PotionBoundaryFallback is null)
        {
            return;
        }

        lock (_sync)
        {
            _inFlightNodes.AddRange(result.Completed);
            _inFlightNodes.AddRange(result.Ended);
            if (result.PotionFreeBoundaryFallback is not null)
                _inFlightNodes.Add(result.PotionFreeBoundaryFallback);
            if (result.PotionBoundaryFallback is not null)
                _inFlightNodes.Add(result.PotionBoundaryFallback);
            if (result.Next.Count > 0)
            {
                SearchNode? bestNext = null;
                foreach (SearchNode node in result.Next)
                {
                    if (bestNext is null || node.Score > bestNext.Score)
                        bestNext = node;
                }
                if (bestNext is not null)
                    _inFlightNodes.Add(bestNext);
            }
        }
    }

    public void CommitWave(
        IReadOnlyList<SearchNode> completed,
        IReadOnlyList<SearchNode> frontier,
        IReadOnlyList<SearchNode> lastActive,
        IReadOnlyList<SearchNode> truncatedParents,
        SearchNode? potionFreeBoundaryFallback,
        SearchNode? potionBoundaryFallback,
        ForecastSearchBoundary passBoundary)
    {
        var seen = new HashSet<SearchNode>(ReferenceEqualityComparer.Instance);
        var merged = new List<SearchNode>(
            completed.Count + frontier.Count + lastActive.Count + truncatedParents.Count + 2);

        AddRange(completed);
        AddRange(frontier);
        AddRange(lastActive);
        AddRange(truncatedParents);
        if (potionFreeBoundaryFallback is not null && seen.Add(potionFreeBoundaryFallback))
            merged.Add(potionFreeBoundaryFallback);
        if (potionBoundaryFallback is not null && seen.Add(potionBoundaryFallback))
            merged.Add(potionBoundaryFallback);

        lock (_sync)
        {
            _inFlightNodes.Clear();
            _committedPool = merged.ToArray();
            _committedBoundary = passBoundary;
        }

        owner.NotifyWaveCommitted();

        void AddRange(IReadOnlyList<SearchNode> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                SearchNode item = items[i];
                if (seen.Add(item))
                    merged.Add(item);
            }
        }
    }

    public void Complete(ForecastPassOutcome outcome)
    {
        lock (_sync)
        {
            _finishedOutcome = outcome;
            _inFlightNodes.Clear();
            _committedPool = [];
            _committedBoundary = outcome.PassBoundary;
        }
        owner.NotifyWaveCommitted();
    }

    public bool TrySnapshotUsablePool(out PassUsableSnapshot snapshot, CancellationToken ct = default)
    {
        lock (_sync)
        {
            ct.ThrowIfCancellationRequested();
            if (_evaluator is null)
            {
                snapshot = default;
                return false;
            }

            if (_finishedOutcome is not null)
            {
                var seen = new HashSet<SearchNode>(
                    _finishedOutcome.EvaluatedCandidates.Length,
                    ReferenceEqualityComparer.Instance);
                var usable = new List<SearchNode>(_finishedOutcome.EvaluatedCandidates.Length);
                foreach (EvaluatedForecastCandidate cand in _finishedOutcome.EvaluatedCandidates)
                {
                    ct.ThrowIfCancellationRequested();
                    if ((cand.Node.Depth > 0 || cand.Node.Ended) && seen.Add(cand.Node))
                        usable.Add(cand.Node);
                }
                if (usable.Count == 0)
                {
                    snapshot = default;
                    return false;
                }
                snapshot = new PassUsableSnapshot(
                    _evaluator,
                    _initialHp,
                    usable.ToArray(),
                    _committedBoundary,
                    _finishedOutcome);
                return true;
            }

            var mergedSeen = new HashSet<SearchNode>(ReferenceEqualityComparer.Instance);
            var list = new List<SearchNode>(_committedPool.Length + _inFlightNodes.Count);
            foreach (SearchNode node in _committedPool)
            {
                ct.ThrowIfCancellationRequested();
                if ((node.Depth > 0 || node.Ended) && mergedSeen.Add(node))
                    list.Add(node);
            }
            foreach (SearchNode node in _inFlightNodes)
            {
                ct.ThrowIfCancellationRequested();
                if ((node.Depth > 0 || node.Ended) && mergedSeen.Add(node))
                    list.Add(node);
            }
            if (list.Count == 0)
            {
                snapshot = default;
                return false;
            }
            snapshot = new PassUsableSnapshot(
                _evaluator,
                _initialHp,
                list.ToArray(),
                _committedBoundary,
                FinishedOutcome: null);
            return true;
        }
    }
}

internal sealed class InCombatSearchTracker
{
    private const long ProgressIntervalMilliseconds = 200;

    private readonly CombatState _root;
    private readonly int _priorBattleHpLost;
    private readonly Stopwatch _watch;
    private readonly Action<InCombatProgressUpdate>? _progressCallback;
    private readonly CancellationToken _searchCancellation;
    private readonly CancellationToken _hardDeadlineToken;
    private readonly object _passesLock = new();
    private readonly object _traceSync = new();
    private readonly List<InCombatPassTrack> _passes = [];
    private readonly object _reporterGate = new();
    private readonly CancellationTokenSource _reporterStopCts = new();
    private readonly ManualResetEventSlim _reporterExited = new(false);
    private Thread? _reporterThread;
    private volatile bool _stopRequested;
    private int _callbacksClosed;
    private int _disposed;
    private int _cleanupRefs;
    private int _waveCommittedFlag;
    private Exception? _reporterException;

    private long _directExpandedNodes;
    private int _maxTurnReached;
    private long _lastProgressEmitMs = -ProgressIntervalMilliseconds;
    private SearchNode? _lastTracedNode;
    private InCombatBeamResult? _lastTracedResult;
    private InCombatBeamResult? _lastEmittedBestResult;

    public InCombatSearchTracker(
        CombatState root,
        int priorBattleHpLost,
        Stopwatch watch,
        Action<InCombatProgressUpdate>? progressCallback,
        CancellationToken searchCancellation = default,
        CancellationToken hardDeadlineToken = default)
    {
        _root = root;
        _priorBattleHpLost = priorBattleHpLost;
        _watch = watch;
        _progressCallback = progressCallback;
        _searchCancellation = searchCancellation;
        _hardDeadlineToken = hardDeadlineToken;
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

    public void StopAndJoin(CancellationToken hardDeadlineToken = default)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Volatile.Write(ref _callbacksClosed, 1);
        _stopRequested = true;
        Thread? thread = Interlocked.Exchange(ref _reporterThread, null);
        _reporterStopCts.Cancel();
        lock (_reporterGate)
            Monitor.PulseAll(_reporterGate);

        CancellationToken waitCt = hardDeadlineToken.CanBeCanceled ? hardDeadlineToken : _hardDeadlineToken;
        bool joined = thread is null;
        if (thread is not null)
        {
            if (!waitCt.CanBeCanceled)
            {
                thread.Join();
                joined = true;
            }
            else if (!waitCt.IsCancellationRequested)
            {
                try
                {
                    _reporterExited.Wait(waitCt);
                    thread.Join();
                    joined = true;
                }
                catch (OperationCanceledException) when (waitCt.IsCancellationRequested)
                {
                    // fail-open: 硬期限在等待后台进度报告线程退出期间到期，不再阻塞调用方退出路径。
                }
            }
        }

        ReleaseCleanupRef();

        if (joined)
        {
            Exception? reporterError = Volatile.Read(ref _reporterException);
            if (reporterError is not null)
                ExceptionDispatchInfo.Capture(reporterError).Throw();
        }
    }

    public InCombatBeamResult? LastCachedBestResult => Volatile.Read(ref _lastEmittedBestResult);

    public InCombatBeamResult? TryGetBestSoFarOnCancel(
        CancellationToken hardDeadlineToken,
        TimeSpan fallbackBudget)
    {
        InCombatBeamResult? cached = LastCachedBestResult;
        if (hardDeadlineToken.IsCancellationRequested || fallbackBudget <= TimeSpan.Zero)
            return StampExpandedNodes(cached);

        using var traceCts = hardDeadlineToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(hardDeadlineToken)
            : new CancellationTokenSource();
        traceCts.CancelAfter(fallbackBudget);
        try
        {
            return StampExpandedNodes(TryBuildBestSoFarResult(traceCts.Token) ?? cached);
        }
        catch (OperationCanceledException)
        {
            // fail-open: 取消后补提部分最优路线触及硬期限或短回退预算，退回已缓存的最优结果避免阻塞调用方。
            return StampExpandedNodes(cached);
        }
    }

    private InCombatBeamResult? StampExpandedNodes(InCombatBeamResult? result)
    {
        if (result is null)
            return null;
        long drainedExpandedNodes = GetTotalExpandedNodes();
        long updatedExpandedNodes = Math.Max(result.ExpandedNodes, drainedExpandedNodes);
        return updatedExpandedNodes == result.ExpandedNodes
            ? result
            : result with { ExpandedNodes = updatedExpandedNodes };
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
            using CancellationTokenSource linkedTraceCts = CancellationTokenSource.CreateLinkedTokenSource(
                _reporterStopCts.Token,
                _searchCancellation,
                _hardDeadlineToken);

            while (!_stopRequested && !linkedTraceCts.IsCancellationRequested)
            {
                long lastEmit = Volatile.Read(ref _lastProgressEmitMs);
                if (lastEmit < 0)
                {
                    lock (_reporterGate)
                    {
                        if (_stopRequested || linkedTraceCts.IsCancellationRequested)
                            break;
                        if (GetTotalExpandedNodes() == 0 && Volatile.Read(ref _waveCommittedFlag) == 0)
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
                            if (_stopRequested || linkedTraceCts.IsCancellationRequested)
                                break;
                            Monitor.Wait(_reporterGate, (int)Math.Min(int.MaxValue, remainingWait));
                        }
                    }
                }

                if (_stopRequested || linkedTraceCts.IsCancellationRequested)
                    break;

                lastEmit = Volatile.Read(ref _lastProgressEmitMs);
                if (lastEmit < 0 && GetTotalExpandedNodes() == 0)
                    continue;
                if (lastEmit >= 0 && _watch.ElapsedMilliseconds - lastEmit < ProgressIntervalMilliseconds)
                    continue;

                Volatile.Write(ref _waveCommittedFlag, 0);
                EmitProgressSnapshot(linkedTraceCts.Token);
            }
        }
        catch (OperationCanceledException) when (_stopRequested || _searchCancellation.IsCancellationRequested || _hardDeadlineToken.IsCancellationRequested)
        {
            // fail-open: 搜索取消或超出硬期限时正常结束后台进度报告线程。
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
            || Volatile.Read(ref _callbacksClosed) != 0
            || _stopRequested
            || traceCt.IsCancellationRequested)
        {
            return;
        }

        InCombatBeamResult? bestResult;
        lock (_traceSync)
        {
            if (Volatile.Read(ref _callbacksClosed) != 0 || _stopRequested || traceCt.IsCancellationRequested)
                return;
            bestResult = TryBuildOrRefreshBest(_watch.Elapsed.TotalMilliseconds, updateCachedBest: true, traceCt);
            Volatile.Write(ref _lastProgressEmitMs, _watch.ElapsedMilliseconds);
        }

        if (Volatile.Read(ref _callbacksClosed) != 0 || _stopRequested || traceCt.IsCancellationRequested)
            return;

        long expanded = GetTotalExpandedNodes();
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

        var update = new InCombatProgressUpdate(
            ExpandedNodes: expanded,
            MaxTurnReached: maxTurn,
            ElapsedMilliseconds: _watch.Elapsed.TotalMilliseconds,
            CurrentBestProjectedHpLoss: projectedHpLoss,
            CurrentBestWon: won,
            CurrentBestCombatEndedTurn: endedTurn,
            CurrentBestPotionCount: potionCount,
            CurrentBest: bestResult);

        if (Volatile.Read(ref _callbacksClosed) != 0 || _stopRequested || traceCt.IsCancellationRequested)
            return;

        _progressCallback(update);
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

        long currentExpanded = GetTotalExpandedNodes();
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
                ct);
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
        var nodeMeta = new Dictionary<SearchNode, (StateEvaluator Evaluator, ForecastSearchBoundary Boundary, int? ProjectedHp)>(
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
                        (snap.Evaluator, finished.PassBoundary, evalCand.ProjectedPlayerHp));
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
                    nodeMeta.Add(candidate, (snap.Evaluator, snap.Boundary, null));
                    deathCheckPool.Add((candidate, snap.Evaluator, null));
                }
            }
        }

        if (allCandidates.Count == 0)
            return false;

        SearchNode? winner = PlanSelector.TrySelect(
            allCandidates,
            passSnapshots[0].InitialHp,
            passSnapshots[0].Evaluator,
            ct);
        if (winner is null)
            return false;

        (StateEvaluator winEval, ForecastSearchBoundary winBoundary, int? precomputedWinHp) = nodeMeta[winner];
        try
        {
            ct.ThrowIfCancellationRequested();
            selectedProjectedHp = precomputedWinHp ?? winEval.ComputeProjectedPlayerHp(winner, winner.NodeBoundary);
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
        selectedEvaluator = winEval;
        bestBoundary = winBoundary;
        return true;
    }
}
