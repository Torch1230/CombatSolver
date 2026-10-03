namespace CombatSolver;

// Research-only pure-value novelty table: evaluated and frozen at generation time.
internal sealed class BfwsResearchNovelty(int width, int maximumEntries)
{
    private readonly Dictionary<string, int> _atoms = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Health, int Potions), HashSet<ulong>> _tables = [];
    public int Entries { get; private set; }
    public int AtomCount => _atoms.Count;
    public int Partitions => _tables.Count;
    public bool LimitReached { get; private set; }
    public long Unary { get; private set; }
    public long Binary { get; private set; }
    public long Familiar { get; private set; }

    public int Evaluate((int Health, int Potions) partition, IEnumerable<string> features)
    {
        if (width is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(width));
        int[] facts = features.Distinct(StringComparer.Ordinal).Select(fact =>
        {
            if (!_atoms.TryGetValue(fact, out int id)) _atoms.Add(fact, id = _atoms.Count + 1);
            return id;
        }).Order().ToArray();
        if (!_tables.TryGetValue(partition, out HashSet<ulong>? seen))
            _tables.Add(partition, seen = []);
        int novelty = width + 1;
        foreach (int atom in facts)
            if (!seen.Contains((uint)atom)) novelty = 1;
        if (width == 2 && novelty > 1)
            for (int i = 0; i < facts.Length; i++)
                for (int j = i + 1; j < facts.Length; j++)
                    if (!seen.Contains(Pair(facts[i], facts[j]))) novelty = 2;
        // Insert all true tuples even when novelty is already 1. This matters on later states.
        foreach (int atom in facts) Add((uint)atom);
        if (width == 2)
            for (int i = 0; i < facts.Length; i++)
                for (int j = i + 1; j < facts.Length; j++) Add(Pair(facts[i], facts[j]));
        if (novelty == 1) Unary++; else if (novelty == 2 && width == 2) Binary++; else Familiar++;
        return novelty;
        void Add(ulong tuple)
        {
            if (seen.Contains(tuple)) return;
            if (Entries >= maximumEntries) { LimitReached = true; return; }
            seen.Add(tuple); Entries++;
        }
    }
    internal static bool IsPrunedByWidth(int novelty, int maximumWidth, bool enabled)
        => enabled && novelty > maximumWidth;

    private static ulong Pair(int a, int b) => ((ulong)(uint)a << 32) | (uint)b;
}
