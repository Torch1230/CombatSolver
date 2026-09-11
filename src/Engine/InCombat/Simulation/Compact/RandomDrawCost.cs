namespace CombatSolver.Engine.InCombat.Simulation.Compact;

/// <summary>Captured absolute, end-of-combat modifiers for a random-cost draw effect.</summary>
internal sealed class RandomDrawCost
{
    private readonly int[] _modifiers;
    internal int Count => _modifiers.Length;
    internal int this[int index] => _modifiers[index];

    internal RandomDrawCost(ReadOnlySpan<int> modifiers)
    {
        _modifiers = modifiers.ToArray();
        if (_modifiers.Any(value => value is < 0 or > 3))
            throw new NotSupportedException("Random draw cost requires captured absolute costs in [0, 3].");
    }
}

/// <summary>Immutable root layout; modifier lists and all five RNG fields are journaled.</summary>
internal sealed class RandomDrawCostLayout
{
    private readonly ReversibleValueBuffer?[] _modifiers;
    private readonly int _rng;

    internal RandomDrawCostLayout(ReversibleValueState state, RandomDrawCost?[] definitions, ValueRng rng)
    {
        _rng = state.Allocate(5);
        WriteRng(state, rng);
        _modifiers = new ReversibleValueBuffer?[definitions.Length];
        for (int card = 0; card < definitions.Length; card++)
        {
            if (definitions[card] is not { } definition) continue;
            var buffer = _modifiers[card] = new(state);
            for (int index = 0; index < definition.Count; index++) buffer.Append(state, [definition[index]]);
        }
    }

    internal ValueRng Rng(ReversibleValueState state) => new(checked((int)state[_rng]),
        unchecked((ulong)state[_rng + 1]), unchecked((ulong)state[_rng + 2]),
        unchecked((ulong)state[_rng + 3]), unchecked((ulong)state[_rng + 4]));
    internal int Count(ReversibleValueState state, int card) => Buffer(card).Count(state);
    internal int At(ReversibleValueState state, int card, int index) => checked((int)Buffer(card).Read(state, index));
    internal int Current(ReversibleValueState state, int card, int baseCost)
    {
        int count = Count(state, card);
        return count > 0 ? At(state, card, count - 1) : baseCost;
    }

    internal int Draw(ReversibleValueState state, int card)
    {
        var next = Rng(state).NextInt(4, out int cost);
        WriteRng(state, next);
        Buffer(card).Append(state, [cost]);
        return cost;
    }

    private ReversibleValueBuffer Buffer(int card) => _modifiers[card]
        ?? throw new InvalidOperationException("Card has no captured random draw-cost effect.");
    private void WriteRng(ReversibleValueState state, ValueRng rng)
    {
        state.Write(_rng, rng.Counter);
        state.Write(_rng + 1, unchecked((long)rng.State0));
        state.Write(_rng + 2, unchecked((long)rng.State1));
        state.Write(_rng + 3, unchecked((long)rng.State2));
        state.Write(_rng + 4, unchecked((long)rng.State3));
    }
}
