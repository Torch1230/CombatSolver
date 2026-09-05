namespace CompactStatePrototype;

internal readonly record struct EntityId(int Value);
internal readonly record struct EntityDefinition(EntityId Id, int MaximumHp);

// A deliberately small, synthetic domain. No game object, hook or mutable model enters the root.
internal sealed class RootSnapshot
{
    private readonly EntityDefinition[] _entities;
    private readonly long[] _initialWords;

    public RootSnapshot(int entityCount, ulong seed)
    {
        _entities = new EntityDefinition[entityCount];
        _initialWords = new long[Layout.HeaderWords + entityCount * Layout.EntityWords];
        _initialWords[Layout.RandomState] = unchecked((long)seed);
        _initialWords[Layout.Round] = 1;
        for (int slot = 0; slot < entityCount; slot++)
        {
            _entities[slot] = new EntityDefinition(new EntityId(1000 + slot * 3), 80 + slot % 31);
            _initialWords[Layout.Entity(slot, Layout.Hp)] = _entities[slot].MaximumHp;
            _initialWords[Layout.Entity(slot, Layout.Energy)] = 3;
        }
    }

    public int EntityCount => _entities.Length;
    public int WordCount => _initialWords.Length;
    public EntityDefinition EntityAt(int slot) => _entities[slot];
    public long[] CreateInitialState() => (long[])_initialWords.Clone();

    public int Resolve(EntityId id)
    {
        int offset = id.Value - 1000;
        if (offset < 0 || offset % 3 != 0 || offset / 3 >= EntityCount)
            throw new ArgumentOutOfRangeException(nameof(id), "Entity identity is outside the immutable root.");
        return offset / 3;
    }
}

internal static class Layout
{
    public const int RandomState = 0;
    public const int RandomDraws = 1;
    public const int Round = 2;
    public const int Transitions = 3;
    public const int HeaderWords = 4;
    public const int EntityWords = 4;
    public const int Hp = 0;
    public const int Block = 1;
    public const int Energy = 2;
    public const int Plays = 3;

    public static int Entity(int slot, int field) => HeaderWords + slot * EntityWords + field;
}

internal interface ICompactState
{
    RootSnapshot Root { get; }
    long Read(int word);
    void Write(int word, long value);
}

internal interface IBranchState : ICompactState
{
    string Name { get; }
    int TransactionDepth { get; }
    void BeginBranch();
    void CommitBranch();
    void RollbackBranch();
    IBranchState ForkRetained();
}

internal enum WritePattern { Sparse, Dense }

internal static class Workload
{
    public static void Apply(
        ICompactState state, ulong path, WritePattern pattern, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        EntityId actorId = state.Root.EntityAt((int)(path % (ulong)state.Root.EntityCount)).Id;
        EntityId targetId = state.Root.EntityAt((int)(NextRandom(state) % (ulong)state.Root.EntityCount)).Id;
        int actor = state.Root.Resolve(actorId);
        int target = state.Root.Resolve(targetId);
        int energyWord = Layout.Entity(actor, Layout.Energy);
        long energy = state.Read(energyWord);
        state.Write(energyWord, energy > 0 ? energy - 1 : 3);
        int playsWord = Layout.Entity(actor, Layout.Plays);
        state.Write(playsWord, state.Read(playsWord) + 1);
        cancellation.ThrowIfCancellationRequested();

        long amount = 1 + (long)(NextRandom(state) % 9);
        int blockWord = Layout.Entity(target, Layout.Block);
        int hpWord = Layout.Entity(target, Layout.Hp);
        long block = state.Read(blockWord);
        state.Write(blockWord, Math.Max(0, block - amount));
        state.Write(hpWord, Math.Max(0, state.Read(hpWord) - Math.Max(0, amount - block)));
        if (pattern == WritePattern.Dense)
        {
            for (int slot = 0; slot < state.Root.EntityCount; slot++)
            {
                cancellation.ThrowIfCancellationRequested();
                int word = Layout.Entity(slot, Layout.Block);
                state.Write(word, (state.Read(word) + amount + slot) % 13);
            }
        }
        long transitions = state.Read(Layout.Transitions) + 1;
        state.Write(Layout.Transitions, transitions);
        if (transitions % 4 == 0)
            state.Write(Layout.Round, state.Read(Layout.Round) + 1);
    }

    private static ulong NextRandom(ICompactState state)
    {
        ulong value = unchecked((ulong)state.Read(Layout.RandomState));
        value ^= value >> 12;
        value ^= value << 25;
        value ^= value >> 27;
        state.Write(Layout.RandomState, unchecked((long)value));
        state.Write(Layout.RandomDraws, state.Read(Layout.RandomDraws) + 1);
        return unchecked(value * 2685821657736338717UL);
    }

    public static long[] Snapshot(ICompactState state)
    {
        long[] words = new long[state.Root.WordCount];
        for (int word = 0; word < words.Length; word++)
            words[word] = state.Read(word);
        return words;
    }

    public static ulong Digest(ICompactState state)
    {
        ulong hash = 1469598103934665603UL;
        for (int word = 0; word < state.Root.WordCount; word++)
            hash = unchecked((hash ^ (ulong)state.Read(word)) * 1099511628211UL);
        return hash;
    }

    // The benchmark observes a fixed small sample. Full-state equality is checked separately.
    public static ulong Sample(ICompactState state, ulong path)
        => unchecked((ulong)state.Read(Layout.RandomState)
            + (ulong)state.Read(Layout.RandomDraws) * 1099511628211UL
            + (ulong)state.Read(Layout.Entity((int)(path % (ulong)state.Root.EntityCount), Layout.Hp)));
}
