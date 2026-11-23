namespace CombatSolver.Beam.Novelty;

internal enum BfwsFactKind : byte
{
    Scalar,
    Energy,
    Block,
    Hp,
    Zone,
    Draw,
    Power,
}

internal readonly record struct BfwsFact(
    BfwsFactKind Kind,
    int Slot = 0,
    string? Key = null,
    int Value = 0,
    int Detail = 0);

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

    public void PrepareParent((int Health, int Potions) partition, IReadOnlyList<BfwsFact> facts)
    {
        _parentPartition = partition;
        _parentComplete = !LimitReached;
        _parentFacts.Clear();
        for (int i = 0; i < facts.Count; i++)
        {
            if (!_atoms.TryGetValue(facts[i], out int id))
                throw new InvalidOperationException("Novelty parent was not previously generated.");
            _parentFacts.Add(id);
        }
    }

    public int Evaluate((int Health, int Potions) partition, IReadOnlyList<BfwsFact> features)
    {
        if (width is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(width));

        _facts.Clear();
        for (int i = 0; i < features.Count; i++)
        {
            BfwsFact feature = features[i];
            if (!_atoms.TryGetValue(feature, out int id))
                _atoms.Add(feature, id = _atoms.Count + 1);
            _facts.Add(id);
        }
        _facts.Sort();
        int distinct = 0;
        for (int i = 0; i < _facts.Count; i++)
        {
            if (distinct == 0 || _facts[i] != _facts[distinct - 1])
                _facts[distinct++] = _facts[i];
        }
        if (distinct < _facts.Count)
            _facts.RemoveRange(distinct, _facts.Count - distinct);

        if (!_tables.TryGetValue(partition, out HashSet<ulong>? seen))
            _tables.Add(partition, seen = []);

        bool reuseParent = _parentComplete && _parentPartition == partition;
        _changed.Clear();
        int novelty = width + 1;
        for (int i = 0; i < _facts.Count; i++)
        {
            if (reuseParent && _parentFacts.Contains(_facts[i]))
                continue;
            _changed.Add(i);
            if (Record((uint)_facts[i], seen))
                novelty = 1;
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
                    for (int j = i + 1; j < _facts.Count; j++)
                        ObservePair(i, j);
                }
                else
                {
                    for (int c = changedCursor; c < _changed.Count; c++)
                        ObservePair(i, _changed[c]);
                }
            }
        }
        if (novelty == 1)
            Unary++;
        else if (novelty == 2 && width == 2)
            Binary++;
        else
            Familiar++;
        return novelty;

        void ObservePair(int i, int j)
        {
            PairVisits++;
            if (Record(((ulong)(uint)_facts[i] << 32) | (uint)_facts[j], seen) && novelty > 1)
                novelty = 2;
        }
    }

    private bool Record(ulong tuple, HashSet<ulong> seen)
    {
        if (seen.Contains(tuple))
            return false;
        if (Entries >= maximumEntries)
        {
            LimitReached = true;
            return true;
        }
        seen.Add(tuple);
        Entries++;
        return true;
    }
}

internal sealed class BfwsBoundedOpen<T>(int capacity)
{
    private readonly SortedSet<Entry> _entries = new(Comparer<Entry>.Create(
        static (a, b) => a.Priority.CompareTo(b.Priority)));

    private sealed record Entry(T Value, (double Primary, double Secondary, long Sequence) Priority);

    public int Count => _entries.Count;
    public IEnumerable<T> Values => _entries.Select(static entry => entry.Value);

    public bool Enqueue(T value, (double Primary, double Secondary, long Sequence) priority, out T dropped)
    {
        if (!_entries.Add(new Entry(value, priority)))
            throw new InvalidOperationException("OPEN priorities require a unique sequence.");
        if (_entries.Count <= capacity)
        {
            dropped = default!;
            return false;
        }
        Entry worst = _entries.Max!;
        _entries.Remove(worst);
        dropped = worst.Value;
        return true;
    }

    public T Dequeue()
    {
        Entry best = _entries.Min ?? throw new InvalidOperationException("OPEN is empty.");
        _entries.Remove(best);
        return best.Value;
    }

    public void Clear() => _entries.Clear();
}

internal sealed class BfwsEscapeBudget(int maximum)
{
    public int Admitted { get; private set; }

    public bool TryAdmit()
    {
        if (Admitted >= maximum)
            return false;
        Admitted++;
        return true;
    }
}
