namespace CombatSolver;

// Offline, bounded two-stage reservoir: one depth per observed turn, then one
// actual competition at that depth. Repeated shallow pools do not get extra
// chances to displace another depth. Neither stream is a battle RNG stream.
internal sealed class SearchCorrectionSampler<T>(int seed) where T : class
{
    internal const int MaximumTurns = 3;
    private sealed class TurnStratum(int turn, int slot)
    {
        internal readonly int Turn = turn, Slot = slot;
        internal readonly int[] Competitions = new int[SearchWitnessPrefix.MaximumActions + 1];
        internal int Depths, SelectedDepth = -1, Selections;
    }
    internal sealed record Stratum(int Turn, int Slot, int SelectedDepth, int DistinctDepths,
        int Selections, int[] CompetitionsByDepth);

    private readonly Random _depthRandom = new(seed);
    private readonly Random _competitionRandom = new(unchecked(seed ^ 0x5a17d39b));
    private readonly Dictionary<int, TurnStratum> _turns = [];
    private readonly T?[] _samples = new T?[MaximumTurns];
    internal int Seed { get; } = seed;
    internal int Count => _turns.Count;
    internal T? GetSample(int slot) => _samples[slot];
    internal T this[int slot] => _samples[slot]
        ?? throw new InvalidOperationException("Correction slot has no captured competition.");
    internal bool CanObserve(int turn) => _turns.ContainsKey(turn) || Count < MaximumTurns;

    // capture runs synchronously only for a selected replacement and is never
    // retained. The caller must return detached values, not live search nodes.
    internal bool Offer(int turn, int depth, Func<int, T> capture)
    {
        if (turn < 1 || depth is < 0 or > SearchWitnessPrefix.MaximumActions)
            throw new ArgumentOutOfRangeException(nameof(depth), "Invalid correction turn/depth.");
        if (!_turns.TryGetValue(turn, out var stratum))
        {
            if (Count == MaximumTurns) return false;
            stratum = new(turn, Count);
            _turns.Add(turn, stratum);
        }
        int competitions = checked(++stratum.Competitions[depth]);
        if (competitions == 1)
        {
            stratum.Depths++;
            if (_depthRandom.Next(stratum.Depths) != 0) return false;
            stratum.SelectedDepth = depth;
        }
        if (stratum.SelectedDepth != depth || _competitionRandom.Next(competitions) != 0)
            return false;
        _samples[stratum.Slot] = capture(stratum.Slot)
            ?? throw new InvalidOperationException("Correction capture returned no competition.");
        stratum.Selections++;
        return true;
    }

    internal Stratum[] Describe() => _turns.Values.OrderBy(s => s.Slot)
        .Select(s => new Stratum(s.Turn, s.Slot, s.SelectedDepth, s.Depths,
            s.Selections, s.Competitions.ToArray())).ToArray();
}
