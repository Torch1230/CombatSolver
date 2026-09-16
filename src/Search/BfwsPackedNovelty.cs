namespace CombatSolver;

internal enum BfwsFactKind : byte { Scalar, Energy, Block, Hp, Zone, Draw, Power }

// Identity only: names are immutable model IDs, never live model references or rendered text.
internal readonly record struct BfwsFact(BfwsFactKind Kind, int Slot = 0, string? Key = null,
    int Value = 0, int Detail = 0);

internal sealed class BfwsPackedNovelty(int width, int maximumEntries)
{
    private readonly Dictionary<BfwsFact, int> _atoms = [];
    private readonly Dictionary<(int Health, int Potions), HashSet<ulong>> _tables = [];
    private readonly List<int> _facts = [];
    private readonly List<int> _changed = [];
    private readonly HashSet<int> _parentFacts = [];
    private (int Health, int Potions)? _parentPartition;
    private bool _parentComplete;

    public int Entries { get; private set; }
    public int AtomCount => _atoms.Count;
    public int Partitions => _tables.Count;
    public bool LimitReached { get; private set; }
    public long Unary { get; private set; }
    public long Binary { get; private set; }
    public long Familiar { get; private set; }
    public long PairVisits { get; private set; }

    // Every tuple of this already-generated parent is in its partition's history. Within
    // that same partition, pairs of unchanged facts therefore need neither lookup nor insertion.
    public void PrepareParent((int Health, int Potions) partition, IReadOnlyList<BfwsFact> facts)
    {
        _parentPartition = partition;
        _parentComplete = !LimitReached;
        _parentFacts.Clear();
        foreach (BfwsFact fact in facts)
        {
            if (!_atoms.TryGetValue(fact, out int id))
                throw new InvalidOperationException("Novelty parent was not previously generated.");
            _parentFacts.Add(id);
        }
    }

    public int Evaluate((int Health, int Potions) partition, IReadOnlyList<BfwsFact> features)
    {
        if (width is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(width));
        _facts.Clear();
        foreach (BfwsFact feature in features)
        {
            if (!_atoms.TryGetValue(feature, out int id))
                _atoms.Add(feature, id = _atoms.Count + 1);
            _facts.Add(id);
        }
        _facts.Sort();
        int distinct = 0;
        for (int i = 0; i < _facts.Count; i++)
            if (distinct == 0 || _facts[i] != _facts[distinct - 1])
                _facts[distinct++] = _facts[i];
        if (distinct < _facts.Count) _facts.RemoveRange(distinct, _facts.Count - distinct);
        if (!_tables.TryGetValue(partition, out HashSet<ulong>? seen))
            _tables.Add(partition, seen = []);

        bool reuseParent = _parentComplete && _parentPartition == partition;
        _changed.Clear();
        int novelty = width + 1;
        for (int i = 0; i < _facts.Count; i++)
        {
            if (reuseParent && _parentFacts.Contains(_facts[i])) continue;
            _changed.Add(i);
            if (Record((uint)_facts[i], seen)) novelty = 1;
        }
        if (width == 2)
        {
            int changedCursor = 0;
            for (int i = 0; i < _facts.Count; i++)
            {
                bool changed = changedCursor < _changed.Count && _changed[changedCursor] == i;
                if (changed)
                {
                    changedCursor++;
                    for (int j = i + 1; j < _facts.Count; j++) ObservePair(i, j);
                }
                else
                {
                    for (int c = changedCursor; c < _changed.Count; c++) ObservePair(i, _changed[c]);
                }
            }
        }
        if (novelty == 1) Unary++; else if (novelty == 2 && width == 2) Binary++; else Familiar++;
        return novelty;

        void ObservePair(int i, int j)
        {
            PairVisits++;
            // Preserve the original increasing-(i,j) insertion order, including at capacity.
            if (Record(((ulong)(uint)_facts[i] << 32) | (uint)_facts[j], seen) && novelty > 1)
                novelty = 2;
        }
    }

    private bool Record(ulong tuple, HashSet<ulong> seen)
    {
        if (seen.Contains(tuple)) return false;
        if (Entries >= maximumEntries) { LimitReached = true; return true; }
        seen.Add(tuple);
        Entries++;
        return true;
    }
}
