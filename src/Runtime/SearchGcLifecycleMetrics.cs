using System.Runtime;

namespace CombatSolver;

internal enum SearchGcLifecycleAttribution
{
    ExclusiveSearchScope,
    SharedProcessWindow,
}

internal interface ISearchGcScope : IDisposable
{
    /// <summary>Frozen after Dispose; excludes admission waits and work after admission is released.</summary>
    SearchGcLifecycleSnapshot Lifecycle { get; }
    SearchGcLifecycleAttribution LifecycleAttribution { get; }
    bool IsLifecycleCompleted { get; }
}

/// <summary>
/// The runtime captures both boundaries while holding its admission gate. A closed scope never
/// reads global counters again, so a later request cannot change the previous request's result.
/// </summary>
internal abstract class SearchGcScope(
    SearchGcLifecycleSnapshot lifecycleAtEntry,
    SearchGcLifecycleAttribution attribution) : ISearchGcScope
{
    private SearchGcLifecycleSnapshot _lifecycle;
    private int _lifecycleCompleted;

    public SearchGcLifecycleAttribution LifecycleAttribution { get; } = attribution;

    public bool IsLifecycleCompleted => Volatile.Read(ref _lifecycleCompleted) != 0;

    public SearchGcLifecycleSnapshot Lifecycle
        => IsLifecycleCompleted
            ? _lifecycle
            : throw new InvalidOperationException("GC scope lifecycle is available only after its admission is released.");

    // Called by the runtime under Gate, before decrementing active-search counts or scheduling
    // post-search reclamation. The volatile publication makes the frozen struct safe to read
    // after a worker publishes its completed result to the controller.
    internal void CompleteLifecycle(SearchGcLifecycleSnapshot lifecycleAtExit)
    {
        if (IsLifecycleCompleted)
            return;
        _lifecycle = lifecycleAtExit.DeltaFrom(lifecycleAtEntry);
        Volatile.Write(ref _lifecycleCompleted, 1);
    }

    public abstract void Dispose();
}

/// <summary>Runtime API calls and observed region transitions, not CLR generation-count deltas.</summary>
internal readonly record struct SearchGcLifecycleSnapshot(
    long ForcedCollections,
    long NoGcStartAttempts,
    long NoGcStarts,
    long NoGcEndAttempts,
    long NoGcEnds,
    long NoGcRestarts,
    long NoGcLosses)
{
    public SearchGcLifecycleSnapshot DeltaFrom(SearchGcLifecycleSnapshot earlier)
        => new(
            ForcedCollections - earlier.ForcedCollections,
            NoGcStartAttempts - earlier.NoGcStartAttempts,
            NoGcStarts - earlier.NoGcStarts,
            NoGcEndAttempts - earlier.NoGcEndAttempts,
            NoGcEnds - earlier.NoGcEnds,
            NoGcRestarts - earlier.NoGcRestarts,
            NoGcLosses - earlier.NoGcLosses);

    public string ToDiagnosticString()
        => $"forced_collects={ForcedCollections} no_gc_start_attempts={NoGcStartAttempts} " +
            $"no_gc_starts={NoGcStarts} no_gc_end_attempts={NoGcEndAttempts} " +
            $"no_gc_ends={NoGcEnds} no_gc_restarts={NoGcRestarts} no_gc_losses={NoGcLosses}";
}

internal sealed class SearchGcLifecycleCounters
{
    private readonly Lock _gate = new();
    private SearchGcLifecycleSnapshot _totals;
    private bool _observedRegionActive;

    public SearchGcLifecycleSnapshot Capture()
    {
        lock (_gate)
            return _totals;
    }

    public void RecordForcedCollection()
    {
        lock (_gate)
            _totals = _totals with { ForcedCollections = _totals.ForcedCollections + 1 };
    }

    public void RecordStartAttempt()
    {
        lock (_gate)
            _totals = _totals with { NoGcStartAttempts = _totals.NoGcStartAttempts + 1 };
    }

    public void RecordStarted(bool restart)
    {
        lock (_gate)
        {
            _totals = _totals with
            {
                NoGcStarts = _totals.NoGcStarts + 1,
                NoGcRestarts = _totals.NoGcRestarts + (restart ? 1 : 0),
            };
            _observedRegionActive = true;
        }
    }

    public void RecordEndAttempt()
    {
        lock (_gate)
            _totals = _totals with { NoGcEndAttempts = _totals.NoGcEndAttempts + 1 };
    }

    public void RecordEnded()
    {
        lock (_gate)
        {
            _totals = _totals with { NoGcEnds = _totals.NoGcEnds + 1 };
            _observedRegionActive = false;
        }
    }

    public void RecordUnexpectedLoss()
    {
        lock (_gate)
        {
            // Multiple probes can observe the same lost region before its safe-point cleanup.
            if (!_observedRegionActive)
                return;
            _totals = _totals with { NoGcLosses = _totals.NoGcLosses + 1 };
            _observedRegionActive = false;
        }
    }
}

/// <summary>
/// Latest completed GC of each kind. This is an observed maximum, not an event-stream maximum:
/// several collections of one kind between samples can still be missed.
/// </summary>
internal readonly record struct SearchGcPauseSnapshot(
    long EphemeralIndex,
    long FullBlockingIndex,
    long BackgroundIndex)
{
    public static SearchGcPauseSnapshot Capture()
        => new(
            GC.GetGCMemoryInfo(GCKind.Ephemeral).Index,
            GC.GetGCMemoryInfo(GCKind.FullBlocking).Index,
            GC.GetGCMemoryInfo(GCKind.Background).Index);

    public TimeSpan ObserveMaximumSince()
    {
        TimeSpan maximum = TimeSpan.Zero;
        Observe(GC.GetGCMemoryInfo(GCKind.Ephemeral), EphemeralIndex, ref maximum);
        Observe(GC.GetGCMemoryInfo(GCKind.FullBlocking), FullBlockingIndex, ref maximum);
        Observe(GC.GetGCMemoryInfo(GCKind.Background), BackgroundIndex, ref maximum);
        return maximum;
    }

    private static void Observe(GCMemoryInfo current, long previousIndex, ref TimeSpan maximum)
    {
        TimeSpan observed = MaximumNewPause(previousIndex, current.Index, current.PauseDurations);
        if (observed > maximum)
            maximum = observed;
    }

    internal static TimeSpan MaximumNewPause(
        long previousIndex,
        long currentIndex,
        ReadOnlySpan<TimeSpan> pauses)
    {
        if (currentIndex <= previousIndex)
            return TimeSpan.Zero;
        TimeSpan maximum = TimeSpan.Zero;
        foreach (TimeSpan pause in pauses)
        {
            if (pause > maximum)
                maximum = pause;
        }
        return maximum;
    }
}
