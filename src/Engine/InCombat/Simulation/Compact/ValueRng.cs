using System.Numerics;

namespace CombatSolver.Engine.InCombat.Simulation.Compact;

/// <summary>Owned xoshiro256** state. Integer sampling uses the game's 53-bit double scaling.</summary>
internal readonly record struct ValueRng(int Counter, ulong State0, ulong State1, ulong State2, ulong State3)
{
    // Native TakeRandom always copies and shuffles the entire ordered population,
    // including when Take(count) returns no elements. The caller maps these indices
    // to its frozen definitions and owns scratch until it consumes/copies the result.
    internal ValueRng TakeDistinctIndices(
        int population, int count, Span<int> scratch, out int selectedCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(population);
        if (scratch.Length < population)
            throw new ArgumentException("Selection scratch must hold the full population.", nameof(scratch));
        for (int index = 0; index < population; index++) scratch[index] = index;
        ValueRng next = this;
        for (int index = population - 1; index > 0; index--)
        {
            next = next.NextInt(index + 1, out int other);
            (scratch[index], scratch[other]) = (scratch[other], scratch[index]);
        }
        selectedCount = Math.Clamp(count, 0, population);
        return next;
    }

    // xoshiro256**: David Blackman and Sebastiano Vigna, 2018, public domain (CC0).
    internal ValueRng NextInt(int exclusiveMaximum, out int value)
    {
        if (exclusiveMaximum <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));
        ulong output = unchecked(BitOperations.RotateLeft(State1 * 5, 7) * 9);
        value = (int)((output >> 11) * (1.0 / (1UL << 53)) * exclusiveMaximum);
        ulong s2 = State2 ^ State0, s3 = State3 ^ State1;
        return new(unchecked(Counter + 1), State0 ^ s3, State1 ^ s2,
            s2 ^ (State1 << 17), BitOperations.RotateLeft(s3, 45));
    }
}
