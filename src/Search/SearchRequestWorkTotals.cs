namespace CombatSolver;

internal enum DirectSearchPurpose
{
    PrimaryBeam,
    RefinementBeam,
    PotionFreeAudit,
    RequiredPotionAudit,
    SmartPotionGradient,
    TurnBoundaryDiscovery,
    EarlyTurnScout,
    NoveltyExploration,
    AdaptiveNoveltyRefinement,
    NarrowOpeningIncumbent,
    BossTempo,
}

internal readonly record struct SearchRequestWorkSnapshot(
    long ExpandedNodes, long TransitionCount, long ChoiceBranchesEvaluated,
    TimeSpan Elapsed, long WorkerAllocatedBytes,
    long Gen0Collections, long Gen1Collections, long Gen2Collections,
    TimeSpan GcPauseDuration, TimeSpan MaxObservedGcPause, int RecordedSolverCount, int CycleReplayActions = 0);

internal readonly record struct SearchSolverWorkContribution(
    int ExpandedNodes, int TransitionCount, int ChoiceBranchesEvaluated,
    TimeSpan Elapsed, long WorkerAllocatedBytes,
    int Gen0Collections, int Gen1Collections, int Gen2Collections,
    TimeSpan GcPauseDuration, TimeSpan MaxObservedGcPause);

internal readonly record struct SearchWorkAttribution(
    string Mechanism,
    long ExpandedNodes,
    long Transitions,
    long ChoiceBranches,
    double ElapsedMilliseconds,
    long WorkerAllocatedBytes,
    long Gen0Collections,
    long Gen1Collections,
    long Gen2Collections,
    double GcPauseMilliseconds,
    double MaxObservedGcPauseMilliseconds,
    int RecordedSolverCount);

/// <summary>Each solver contributes once, including cancellation and failed policy searches.</summary>
internal sealed class SearchRequestWorkTotals
{
    internal const int MaximumCycleReplayActions = 4096;
    private int _cycleReplayActions;
    internal int RemainingCycleReplayActions => MaximumCycleReplayActions - Volatile.Read(ref _cycleReplayActions);

    internal bool TryConsumeCycleReplayAction()
    {
        int used = Volatile.Read(ref _cycleReplayActions);
        while (used < MaximumCycleReplayActions)
        {
            int observed = Interlocked.CompareExchange(ref _cycleReplayActions, used + 1, used);
            if (observed == used) return true;
            used = observed;
        }
        return false;
    }

    private readonly Lock _gate = new();
    private SearchRequestWorkSnapshot _totals;
    private readonly Dictionary<string, SearchWorkAttribution> _attributions =
        new(StringComparer.Ordinal);
    internal int RecordedSolverCountForTesting { get { lock (_gate) return _totals.RecordedSolverCount; } }

    public void Record(SearchSolverWorkContribution work, ContinuationPurpose? purpose = null,
        DirectSearchPurpose? directSearchPurpose = null)
    {
        if (purpose != null && directSearchPurpose != null)
            throw new ArgumentException("A search contribution cannot have two purposes.");
        ArgumentOutOfRangeException.ThrowIfNegative(work.ExpandedNodes);
        ArgumentOutOfRangeException.ThrowIfNegative(work.TransitionCount);
        ArgumentOutOfRangeException.ThrowIfNegative(work.ChoiceBranchesEvaluated);
        ValidateWork(work.Elapsed, work.WorkerAllocatedBytes, work.Gen0Collections, work.Gen1Collections,
            work.Gen2Collections, work.GcPauseDuration, work.MaxObservedGcPause);
        lock (_gate)
        {
            Accumulate(work.Elapsed, work.WorkerAllocatedBytes, work.Gen0Collections, work.Gen1Collections,
                work.Gen2Collections, work.GcPauseDuration, work.MaxObservedGcPause);
            _totals = _totals with
            {
                ExpandedNodes = _totals.ExpandedNodes + work.ExpandedNodes,
                TransitionCount = _totals.TransitionCount + work.TransitionCount,
                ChoiceBranchesEvaluated = _totals.ChoiceBranchesEvaluated + work.ChoiceBranchesEvaluated,
                RecordedSolverCount = _totals.RecordedSolverCount + 1,
            };
            RecordAttribution(purpose?.ToString()
                ?? directSearchPurpose?.ToString()
                ?? "UnattributedDirect", work, 1);
        }
    }

    public void RecordCoordinatorOverhead(TimeSpan elapsed, long allocatedBytes,
        int gen0Collections, int gen1Collections, int gen2Collections,
        TimeSpan gcPauseDuration, TimeSpan? maxObservedGcPause)
    {
        // An absent sample contributes no pause to the observed-maximum aggregate.
        TimeSpan observedMaximum = maxObservedGcPause is { } observed ? observed : TimeSpan.Zero;
        ValidateWork(elapsed, allocatedBytes, gen0Collections, gen1Collections, gen2Collections, gcPauseDuration, observedMaximum);
        lock (_gate)
        {
            Accumulate(elapsed, allocatedBytes, gen0Collections, gen1Collections, gen2Collections, gcPauseDuration, observedMaximum);
            RecordAttribution("CoordinatorOverhead", new SearchSolverWorkContribution(
                0, 0, 0, elapsed, allocatedBytes, gen0Collections, gen1Collections,
                gen2Collections, gcPauseDuration, observedMaximum), 0);
        }
    }

    private void RecordAttribution(
        string mechanism, SearchSolverWorkContribution work, int solverCount)
    {
        SearchWorkAttribution prior = _attributions.GetValueOrDefault(mechanism);
        _attributions[mechanism] = new SearchWorkAttribution(
            mechanism,
            prior.ExpandedNodes + work.ExpandedNodes,
            prior.Transitions + work.TransitionCount,
            prior.ChoiceBranches + work.ChoiceBranchesEvaluated,
            prior.ElapsedMilliseconds + work.Elapsed.TotalMilliseconds,
            prior.WorkerAllocatedBytes + work.WorkerAllocatedBytes,
            prior.Gen0Collections + work.Gen0Collections,
            prior.Gen1Collections + work.Gen1Collections,
            prior.Gen2Collections + work.Gen2Collections,
            prior.GcPauseMilliseconds + work.GcPauseDuration.TotalMilliseconds,
            Math.Max(prior.MaxObservedGcPauseMilliseconds,
                work.MaxObservedGcPause.TotalMilliseconds),
            prior.RecordedSolverCount + solverCount);
    }

    private static void ValidateWork(TimeSpan elapsed, long allocatedBytes, int gen0Collections,
        int gen1Collections, int gen2Collections, TimeSpan gcPauseDuration, TimeSpan maxObservedGcPause)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(allocatedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(gen0Collections);
        ArgumentOutOfRangeException.ThrowIfNegative(gen1Collections);
        ArgumentOutOfRangeException.ThrowIfNegative(gen2Collections);
        if (elapsed < TimeSpan.Zero || gcPauseDuration < TimeSpan.Zero || maxObservedGcPause < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed));
    }

    private void Accumulate(TimeSpan elapsed, long allocatedBytes, int gen0Collections,
        int gen1Collections, int gen2Collections, TimeSpan gcPauseDuration, TimeSpan maxObservedGcPause)
    {
        _totals = _totals with
        {
            Elapsed = _totals.Elapsed + elapsed,
            WorkerAllocatedBytes = _totals.WorkerAllocatedBytes + allocatedBytes,
            Gen0Collections = _totals.Gen0Collections + gen0Collections,
            Gen1Collections = _totals.Gen1Collections + gen1Collections,
            Gen2Collections = _totals.Gen2Collections + gen2Collections,
            GcPauseDuration = _totals.GcPauseDuration + gcPauseDuration,
            MaxObservedGcPause = maxObservedGcPause > _totals.MaxObservedGcPause ? maxObservedGcPause : _totals.MaxObservedGcPause,
        };
    }

    public SearchRequestWorkSnapshot Snapshot() { lock (_gate) return _totals with { CycleReplayActions = Volatile.Read(ref _cycleReplayActions) }; }

    public SearchWorkAttribution[] AttributionSnapshot()
    {
        lock (_gate)
            return _attributions.Values.OrderBy(item => item.Mechanism, StringComparer.Ordinal).ToArray();
    }
}
