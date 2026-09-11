namespace CombatSolver.Engine.InCombat.Simulation.Compact;

/// <summary>Captured eligible-card pools and their own five-field generation stream.</summary>
internal sealed class CardGenerationPools
{
    private readonly int[][] _pools;
    private readonly int _rng;

    internal CardGenerationPools(ReversibleValueState state, int[][] pools, ValueRng rng)
    {
        if (pools.Length == 0) throw new ArgumentException("Pool generation requires captured pools.", nameof(pools));
        _pools = new int[pools.Length][];
        int longest = 0;
        for (int pool = 0; pool < pools.Length; pool++)
        {
            // Native selects from the frozen candidate list, so a later mutation of the
            // admitting array must never reach this captured root.
            if (pools[pool] == null) throw new ArgumentException("Captured generation pools cannot be null.", nameof(pools));
            _pools[pool] = (int[])pools[pool].Clone();
            longest = Math.Max(longest, _pools[pool].Length);
        }
        MaxPoolLength = longest;
        _rng = state.Allocate(5);
        WriteRng(state, rng);
    }

    internal int PoolCount => _pools.Length;
    internal int MaxPoolLength { get; }
    internal int PoolLength(int pool) => _pools[pool].Length;
    internal int PoolEntry(int pool, int index) => _pools[pool][index];

    internal ValueRng Rng(ReversibleValueState state) => new(checked((int)state[_rng]),
        unchecked((ulong)state[_rng + 1]), unchecked((ulong)state[_rng + 2]),
        unchecked((ulong)state[_rng + 3]), unchecked((ulong)state[_rng + 4]));

    // Whole-pool selection owns only the caller's lane-exclusive scratch; the selected
    // entry is copied out before the span is reusable. The population guard stays in
    // the shared shuffle primitive.
    internal int SelectOne(ReversibleValueState state, int pool, Span<int> scratch)
    {
        ValueRng next = Rng(state).TakeDistinctIndices(PoolLength(pool), 1, scratch, out int count);
        WriteRng(state, next);
        return count == 0 ? -1 : PoolEntry(pool, scratch[0]);
    }

    private void WriteRng(ReversibleValueState state, ValueRng rng)
    {
        state.Write(_rng, rng.Counter);
        state.Write(_rng + 1, unchecked((long)rng.State0));
        state.Write(_rng + 2, unchecked((long)rng.State1));
        state.Write(_rng + 3, unchecked((long)rng.State2));
        state.Write(_rng + 4, unchecked((long)rng.State3));
    }
}
