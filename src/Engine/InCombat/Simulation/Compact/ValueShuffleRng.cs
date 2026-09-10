using System.Numerics;

namespace CombatSolver.Engine.InCombat.Simulation.Compact;

/// <summary>Owned xoshiro256** state. Integer sampling uses the game's 53-bit double scaling.</summary>
internal readonly record struct ValueShuffleRng(int Counter, ulong State0, ulong State1, ulong State2, ulong State3)
{
    // xoshiro256**: David Blackman and Sebastiano Vigna, 2018, public domain (CC0).
    internal ValueShuffleRng NextInt(int exclusiveMaximum, out int value)
    {
        if (exclusiveMaximum <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));
        ulong output = unchecked(BitOperations.RotateLeft(State1 * 5, 7) * 9);
        value = (int)((output >> 11) * (1.0 / (1UL << 53)) * exclusiveMaximum);
        ulong s2 = State2 ^ State0, s3 = State3 ^ State1;
        return new(unchecked(Counter + 1), State0 ^ s3, State1 ^ s2,
            s2 ^ (State1 << 17), BitOperations.RotateLeft(s3, 45));
    }
}
