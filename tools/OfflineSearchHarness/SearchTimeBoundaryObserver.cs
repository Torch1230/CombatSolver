using CombatSolver;

namespace OfflineSearchHarness;

/// <summary>Per-request, log-driven observation of explicit search time-limit evidence.</summary>
internal sealed class SearchTimeBoundaryObserver
{
    private int _observed;

    public bool TimeBoundaryObserved => Volatile.Read(ref _observed) != 0;

    public SearchDiagnosticsSink Wrap(SearchDiagnosticsSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return new SearchDiagnosticsSink(message =>
        {
            ObserveMessage(message);
            sink.Info(message);
        }, sink.Debug, sink.PathObserver);
    }

    public void ObserveSelectedResult(SearchBoundaryReason boundaryReason)
    {
        if (boundaryReason == SearchBoundaryReason.TimeLimit)
            Volatile.Write(ref _observed, 1);
    }

    internal void ObserveMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Contains("TURN_LAYER_BUDGET reason=time", StringComparison.Ordinal)
            || message.Contains("SEARCH_TIME_BUDGET", StringComparison.Ordinal)
            || message.Contains("SMART_POTION_GRADIENT result", StringComparison.Ordinal)
                && message.Contains("stop=deadline", StringComparison.Ordinal)
            || message.Contains("SUPPLEMENTAL_AUDIT_BUDGET", StringComparison.Ordinal)
                && message.Contains("exhausted=true", StringComparison.Ordinal)
            || message.Contains("NOVELTY_SEARCH_STOP reason=time_limit", StringComparison.Ordinal))
            Volatile.Write(ref _observed, 1);
    }
}
