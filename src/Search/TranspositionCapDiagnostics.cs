namespace CombatSolver;

/// <summary>只记录表的观测值；不参与准入、支配、预算或回收决策。</summary>
internal sealed class TranspositionCapDiagnostics
{
    private int _peakEntries;
    private int? _firstCapExpanded;

    public void ObserveEntries(int entries, int limit, int expanded)
    {
        _peakEntries = Math.Max(_peakEntries, entries);
        if (limit > 0 && entries >= limit)
            _firstCapExpanded ??= expanded;
    }

    public Snapshot Capture(int limit, int expanded, int bypasses, IEnumerable<int> labelCounts)
    {
        int entries = 0;
        long labels = 0;
        SortedDictionary<int, int> distribution = [];
        foreach (int count in labelCounts)
        {
            entries++;
            labels += count;
            distribution[count] = distribution.GetValueOrDefault(count) + 1;
        }
        return new Snapshot(limit, entries, Math.Max(_peakEntries, entries),
            _firstCapExpanded.HasValue, _firstCapExpanded, expanded, bypasses, labels, distribution);
    }

    internal sealed record Snapshot(int Limit, int CurrentEntries, int PeakEntries,
        bool ReachedCap, int? FirstCapExpanded, int FinalExpanded, int LimitBypasses,
        long TotalLabels, IReadOnlyDictionary<int, int> LabelsPerEntry);
}
