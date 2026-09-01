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

internal sealed class SolverCombatSession
{
    public CombatState? State { get; set; }
    public SolverResult? LatestResult { get; set; }
    public LiveCombatStamp? LatestStamp { get; set; }
    public SolverResult? ContinuationSource { get; set; }
    public bool FullAutoEnabled { get; set; }
    public SolverTheftPolicy? TheftPolicy { get; set; }

    /// <summary>
    /// Potion slots the player has taken off the table for this combat. Keyed by slot, not by potion id, because
    /// a player can carry two of the same potion and may well want to spend exactly one of them.
    /// </summary>
    public HashSet<int> BannedPotionSlots { get; } = [];
    public CompleteProjectionBaseline? PendingCompleteProjectionBaseline { get; set; }
    public ManualProjectionBaseline? PendingManualProjectionBaseline { get; set; }
    public ManualProjectionComparison? LastManualProjectionComparison { get; set; }
    public bool ManualRouteImprovementDetected { get; set; }
    public bool AutomaticSearchPaused { get; set; }
    public Dictionary<ReplanCause, int> ReplanCounts { get; } = [];
    public int SearchesStarted { get; set; }
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
    public bool DeployWhenReady { get; set; } = deployWhenReady;
    public int MaxDegreeOfParallelism { get; set; } = 1;
    public SolverProgress? Progress;
    public SolverProgress? RenderedProgress { get; set; }
    public long LastProgressRenderAt { get; set; } = Environment.TickCount64;
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
}
