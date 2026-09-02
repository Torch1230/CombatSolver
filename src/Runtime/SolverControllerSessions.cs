using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

internal sealed record CompleteProjectionBaseline(
    int StartTurnNumber,
    int ProjectedBattleHpLost,
    string StateDifference);

internal sealed record ManualProjectionBaseline(
    int StartTurnNumber,
    int ProjectedBattleHpLost,
    string StateDifference);

internal sealed record ManualProjectionComparison(
    int OriginalTurnNumber,
    int CurrentTurnNumber,
    int PreviousProjectedBattleHpLost,
    int CurrentProjectedBattleHpLost,
    string StateDifference)
{
    public int Difference => CurrentProjectedBattleHpLost - PreviousProjectedBattleHpLost;
}

internal readonly record struct SearchMemoryUsageSnapshot(
    long ProcessWorkingSetBytes,
    long ConfiguredMemoryBudgetBytes,
    bool SearchActive,
    long SearchAllocatedBytes,
    long SearchAllocationLimitBytes,
    bool Reclaiming,
    bool BackgroundReclaiming)
{
    public bool HasGcWall => SearchAllocationLimitBytes != long.MaxValue;
    public double GcWallRatio => HasGcWall
        ? Math.Clamp(SearchAllocatedBytes / (double)Math.Max(1, SearchAllocationLimitBytes), 0d, 1d)
        : 0d;
    public double ConfiguredBudgetRatio
        => Math.Clamp(ProcessWorkingSetBytes / (double)Math.Max(1, ConfiguredMemoryBudgetBytes), 0d, 1d);
}

internal sealed class SearchProgressDisplayState(long startedAtTick)
{
    public SearchProgressDisplayState() : this(Environment.TickCount64)
    {
    }

    public long StartedAtTick { get; private set; } = startedAtTick;
    public long LastRenderAtTick { get; private set; } = startedAtTick;
    public SolverProgress? RenderedProgress { get; private set; }

    public void Restart(long nowTick)
    {
        StartedAtTick = nowTick;
        LastRenderAtTick = nowTick;
        RenderedProgress = null;
    }

    public bool TryCreate(
        SolverProgress? progress,
        long nowTick,
        out SolverProgress displayProgress)
    {
        if (progress == null
            || nowTick - LastRenderAtTick < SolverWeights.ProgressUiIntervalMilliseconds)
        {
            displayProgress = null!;
            return false;
        }

        long elapsedMilliseconds = Math.Max(
            progress.ElapsedMilliseconds,
            Math.Max(
                RenderedProgress?.ElapsedMilliseconds ?? 0L,
                Math.Max(0L, nowTick - StartedAtTick)));
        displayProgress = elapsedMilliseconds == progress.ElapsedMilliseconds
            ? progress
            : progress with { ElapsedMilliseconds = elapsedMilliseconds };
        LastRenderAtTick = nowTick;
        RenderedProgress = displayProgress;
        return true;
    }
}

internal sealed class SolverCombatSession
{
    public CombatState? State { get; set; }
    public SolverResult? LatestResult { get; set; }
    public LiveCombatStamp? LatestStamp { get; set; }
    public SolverResult? ContinuationSource { get; set; }
    public SearchInteractionState? StoppedSearch { get; set; }
    public bool FullAutoEnabled { get; set; }
    public SolverTheftPolicy? TheftPolicy { get; set; }
    public CompleteProjectionBaseline? PendingCompleteProjectionBaseline { get; set; }
    public ManualProjectionBaseline? PendingManualProjectionBaseline { get; set; }
    public ManualProjectionComparison? LastManualProjectionComparison { get; set; }
    public bool ManualRouteImprovementDetected { get; set; }
    public bool AutomaticSearchPaused { get; set; }
    public int? AutomaticSearchPausedTurn { get; set; }
    public bool ManualSearchAfterTurnSetupRequested { get; set; }
    public int? DeployAfterTurnSetupTurn { get; set; }
    public CombatState? TurnSetupResumeState { get; set; }
    public Dictionary<ReplanCause, int> ReplanCounts { get; } = [];
    public HashSet<SolverResult> ReviewedWorldlineResults { get; } = [];
    public int SearchesStarted { get; set; }
    public long ReviewedWorldlinesTotal { get; set; }
    public int ContinuationsReused { get; set; }
    public IReadOnlyList<string> LastContinuationDifferences { get; set; } = [];
    public int? LastSolverDeployedTurn { get; set; }
    public bool ManualControlObserved { get; set; }
    public CombatBugReportIssueLedger BugReportIssues { get; } = new();
}

internal sealed class SolverSearchSession(
    int generation,
    CombatState state,
    LiveCombatStamp stamp,
    bool deployWhenReady)
{
    private static readonly double[] FrameBucketUpperBounds =
        [16.7d, 25d, 33d, 50d, 100d, double.MaxValue];
    private readonly int[] _frameBuckets = new int[FrameBucketUpperBounds.Length];

    public int Generation { get; } = generation;
    public CombatState State { get; } = state;
    public LiveCombatStamp Stamp { get; } = stamp;
    public CancellationTokenSource Cancellation { get; } = new();
    public Task WorkerCompletion { get; set; } = Task.CompletedTask;
    public TaskCompletionSource CallbackCompletion { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public bool CallbackScheduled { get; set; }
    public int ReferenceReleaseState;
    public int CancellationDisposeState;
    public bool DeployWhenReady { get; set; } = deployWhenReady;
    public int MaxDegreeOfParallelism { get; set; } = 1;
    public SearchMemoryPressureSignal? MemoryPressureSignal { get; set; }
    public SearchInteractionState Interaction { get; } = new();
    public int FrameCount { get; private set; }
    public int FramesOver33Milliseconds { get; private set; }
    public int FramesOver50Milliseconds { get; private set; }
    public int FramesOver100Milliseconds { get; private set; }
    public double MaxFrameGapMilliseconds { get; private set; }

    public long ProcessAllocatedBytesAtStart { get; } = GC.GetTotalAllocatedBytes(precise: false);
    public TimeSpan ProcessGcPauseAtStart { get; } = GC.GetTotalPauseDuration();

    public void ObserveFrame(double milliseconds)
    {
        FrameCount++;
        if (milliseconds > MaxFrameGapMilliseconds)
            MaxFrameGapMilliseconds = milliseconds;
        if (milliseconds >= 33d)
            FramesOver33Milliseconds++;
        if (milliseconds >= 50d)
            FramesOver50Milliseconds++;
        if (milliseconds >= 100d)
            FramesOver100Milliseconds++;
        for (int index = 0; index < FrameBucketUpperBounds.Length; index++)
        {
            if (milliseconds > FrameBucketUpperBounds[index])
                continue;
            _frameBuckets[index]++;
            break;
        }
    }

    public double FramePercentile(double percentile)
    {
        if (FrameCount == 0)
            return 0d;
        int rank = Math.Max(1, (int)Math.Ceiling(FrameCount * percentile));
        int cumulative = 0;
        for (int index = 0; index < _frameBuckets.Length; index++)
        {
            cumulative += _frameBuckets[index];
            if (cumulative >= rank)
            {
                return index == FrameBucketUpperBounds.Length - 1
                    ? MaxFrameGapMilliseconds
                    : FrameBucketUpperBounds[index];
            }
        }
        return MaxFrameGapMilliseconds;
    }
}

internal sealed class SolverDeploymentSession
{
    public CancellationTokenSource Cancellation { get; } = new();
    public Task Operation { get; set; } = Task.CompletedTask;
    public int ReferenceReleaseState;
    public int CancellationDisposeState;
}
