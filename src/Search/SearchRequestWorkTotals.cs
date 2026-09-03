namespace CombatSolver;

internal readonly record struct SearchRequestWorkSnapshot(
    long ExpandedNodes,
    long TransitionCount,
    long ChoiceBranchesEvaluated,
    TimeSpan ShortElapsed,
    TimeSpan DeepElapsed,
    long WorkerAllocatedBytes,
    long ShortExpandedNodes,
    long DeepExpandedNodes,
    long ShortTransitionCount,
    long DeepTransitionCount,
    long Gen0Collections,
    long Gen1Collections,
    long Gen2Collections,
    TimeSpan GcPauseDuration,
    TimeSpan MaxObservedGcPause,
    bool DeepSearchTriggered,
    int RecordedSolverCount);

internal readonly record struct SearchSolverWorkContribution(
    int ExpandedNodes,
    int TransitionCount,
    int ChoiceBranchesEvaluated,
    TimeSpan ShortElapsed,
    TimeSpan DeepElapsed,
    long WorkerAllocatedBytes,
    int ShortExpandedNodes,
    int DeepExpandedNodes,
    int ShortTransitionCount,
    int DeepTransitionCount,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    TimeSpan GcPauseDuration,
    TimeSpan MaxObservedGcPause,
    bool DeepSearchTriggered);

/// <summary>
/// Accumulates Beam-solver work for one coordinator request.
/// A solver contributes exactly once after a normal return, a potion-policy miss, or cancellation.
/// </summary>
internal sealed class SearchRequestWorkTotals
{
    private readonly Lock _gate = new();
    private long _expandedNodes;
    private long _transitionCount;
    private long _choiceBranchesEvaluated;
    private TimeSpan _shortElapsed;
    private TimeSpan _deepElapsed;
    private long _workerAllocatedBytes;
    private long _shortExpandedNodes;
    private long _deepExpandedNodes;
    private long _shortTransitionCount;
    private long _deepTransitionCount;
    private long _gen0Collections;
    private long _gen1Collections;
    private long _gen2Collections;
    private TimeSpan _gcPauseDuration;
    private TimeSpan _maxObservedGcPause;
    private bool _deepSearchTriggered;
    private int _recordedSolverCount;

    internal int RecordedSolverCountForTesting
    {
        get
        {
            lock (_gate)
                return _recordedSolverCount;
        }
    }

    public void Record(SearchSolverWorkContribution completedSolver)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(completedSolver.ExpandedNodes);
        ArgumentOutOfRangeException.ThrowIfNegative(completedSolver.TransitionCount);
        ArgumentOutOfRangeException.ThrowIfNegative(completedSolver.ChoiceBranchesEvaluated);
        ArgumentOutOfRangeException.ThrowIfNegative(completedSolver.WorkerAllocatedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(completedSolver.ShortExpandedNodes);
        ArgumentOutOfRangeException.ThrowIfNegative(completedSolver.DeepExpandedNodes);
        ArgumentOutOfRangeException.ThrowIfNegative(completedSolver.ShortTransitionCount);
        ArgumentOutOfRangeException.ThrowIfNegative(completedSolver.DeepTransitionCount);
        ArgumentOutOfRangeException.ThrowIfNegative(completedSolver.Gen0Collections);
        ArgumentOutOfRangeException.ThrowIfNegative(completedSolver.Gen1Collections);
        ArgumentOutOfRangeException.ThrowIfNegative(completedSolver.Gen2Collections);

        lock (_gate)
        {
            _expandedNodes += completedSolver.ExpandedNodes;
            _transitionCount += completedSolver.TransitionCount;
            _choiceBranchesEvaluated += completedSolver.ChoiceBranchesEvaluated;
            _shortElapsed += completedSolver.ShortElapsed;
            _deepElapsed += completedSolver.DeepElapsed;
            _workerAllocatedBytes += completedSolver.WorkerAllocatedBytes;
            _shortExpandedNodes += completedSolver.ShortExpandedNodes;
            _deepExpandedNodes += completedSolver.DeepExpandedNodes;
            _shortTransitionCount += completedSolver.ShortTransitionCount;
            _deepTransitionCount += completedSolver.DeepTransitionCount;
            _gen0Collections += completedSolver.Gen0Collections;
            _gen1Collections += completedSolver.Gen1Collections;
            _gen2Collections += completedSolver.Gen2Collections;
            _gcPauseDuration += completedSolver.GcPauseDuration;
            if (completedSolver.MaxObservedGcPause > _maxObservedGcPause)
                _maxObservedGcPause = completedSolver.MaxObservedGcPause;
            _deepSearchTriggered |= completedSolver.DeepSearchTriggered;
            _recordedSolverCount++;
        }
    }

    /// <summary>
    /// Records coordinator work that is part of the request but not owned by any Solve() scope,
    /// such as the explicit heap reset between finite Smart-potion layers. It intentionally does
    /// not increment the completed-solver count.
    /// </summary>
    public void RecordCoordinatorOverhead(
        TimeSpan elapsed,
        bool deepPhase,
        long allocatedBytes,
        int gen0Collections,
        int gen1Collections,
        int gen2Collections,
        TimeSpan gcPauseDuration,
        TimeSpan maxObservedGcPause)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(allocatedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(gen0Collections);
        ArgumentOutOfRangeException.ThrowIfNegative(gen1Collections);
        ArgumentOutOfRangeException.ThrowIfNegative(gen2Collections);
        if (elapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (gcPauseDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(gcPauseDuration));
        if (maxObservedGcPause < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxObservedGcPause));

        lock (_gate)
        {
            if (deepPhase)
                _deepElapsed += elapsed;
            else
                _shortElapsed += elapsed;
            _workerAllocatedBytes += allocatedBytes;
            _gen0Collections += gen0Collections;
            _gen1Collections += gen1Collections;
            _gen2Collections += gen2Collections;
            _gcPauseDuration += gcPauseDuration;
            if (maxObservedGcPause > _maxObservedGcPause)
                _maxObservedGcPause = maxObservedGcPause;
        }
    }

    public SearchRequestWorkSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new SearchRequestWorkSnapshot(
                _expandedNodes,
                _transitionCount,
                _choiceBranchesEvaluated,
                _shortElapsed,
                _deepElapsed,
                _workerAllocatedBytes,
                _shortExpandedNodes,
                _deepExpandedNodes,
                _shortTransitionCount,
                _deepTransitionCount,
                _gen0Collections,
                _gen1Collections,
                _gen2Collections,
                _gcPauseDuration,
                _maxObservedGcPause,
                _deepSearchTriggered,
                _recordedSolverCount);
        }
    }
}
