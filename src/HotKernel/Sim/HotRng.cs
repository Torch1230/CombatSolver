using System.Numerics;
using System.Runtime.CompilerServices;
using CombatSolver.Engine.Common;

namespace CombatSolver.HotKernel;

/// <summary>
/// Bit-exact xoshiro256** + Splitmix64 PRNG matching STS2 0.113.1 MegaRandom and Rng.
/// </summary>
internal static class HotRng
{
    private const double IncrDouble = 1.1102230246251565E-16; // 2^-53

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Splitmix64(ref ulong x)
    {
        ulong z = x += 11400714819323198485UL;
        z = (z ^ (z >> 30)) * 13787848793156543929UL;
        z = (z ^ (z >> 27)) * 10723151780598845931UL;
        return z ^ (z >> 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static HotRngState CreateFromSeed(ulong seed)
    {
        HotRngState rng = default;
        rng.S0 = Splitmix64(ref seed);
        rng.S1 = Splitmix64(ref seed);
        rng.S2 = Splitmix64(ref seed);
        rng.S3 = Splitmix64(ref seed);
        rng.Counter = 0;
        rng.Pad = 0;
        return rng;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static HotRngState FromNative(PredictionRngState s) => new()
    {
        S0 = s.State0,
        S1 = s.State1,
        S2 = s.State2,
        S3 = s.State3,
        Counter = s.Counter,
        Pad = 0,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PredictionRngState ToNative(in HotRngState s)
        => new(s.Counter, s.S0, s.S1, s.S2, s.S3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong NextULongRaw(ref HotRngState rng)
    {
        ulong s0 = rng.S0;
        ulong s1 = rng.S1;
        ulong s2 = rng.S2;
        ulong s3 = rng.S3;

        ulong result = BitOperations.RotateLeft(s1 * 5UL, 7) * 9UL;
        ulong t = s1 << 17;

        s2 ^= s0;
        s3 ^= s1;
        s1 ^= s2;
        s0 ^= s3;
        s2 ^= t;
        s3 = BitOperations.RotateLeft(s3, 45);

        rng.S0 = s0;
        rng.S1 = s1;
        rng.S2 = s2;
        rng.S3 = s3;
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong NextULong(ref HotRngState rng)
    {
        rng.Counter++;
        return NextULongRaw(ref rng);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double NextDouble(ref HotRngState rng)
    {
        rng.Counter++;
        return (double)(NextULongRaw(ref rng) >> 11) * IncrDouble;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double NextDouble(ref HotRngState rng, double min, double max)
    {
        if (min > max)
            throw new ArgumentOutOfRangeException(nameof(min), "Minimum must not be higher than maximum.");
        return NextDouble(ref rng) * (max - min) + min;
    }

    /// <summary>
    /// Matches Rng.NextFloat() which delegates to Rng.NextFloat(0f, 1f): (float)(_random.NextDouble() * (double)(1f - 0f) + 0.0).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float NextFloat(ref HotRngState rng)
        => (float)NextDouble(ref rng);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float NextFloat(ref HotRngState rng, float max)
        => NextFloat(ref rng, 0f, max);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float NextFloat(ref HotRngState rng, float min, float max)
    {
        if (min > max)
            throw new ArgumentOutOfRangeException(nameof(min), "Minimum must not be higher than maximum.");
        return (float)(NextDouble(ref rng) * (double)(max - min) + (double)min);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NextInt(ref HotRngState rng, int maxExclusive = int.MaxValue)
    {
        if (maxExclusive < 1)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "maxExclusive must be > 0.");
        return (int)(NextDouble(ref rng) * (double)maxExclusive);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NextInt(ref HotRngState rng, int minInclusive, int maxExclusive)
    {
        if (minInclusive >= maxExclusive)
            throw new ArgumentOutOfRangeException(nameof(minInclusive), "Minimum must be lower than maximum.");

        long range = (long)maxExclusive - (long)minInclusive;
        if (range <= int.MaxValue)
            return (int)(NextDouble(ref rng) * (double)(int)range) + minInclusive;
        return (int)((long)(NextDouble(ref rng) * (double)range) + minInclusive);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool NextBool(ref HotRngState rng)
        => NextInt(ref rng, 2) == 0;

    public static void Advance(ref HotRngState rng, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        for (int i = 0; i < count; i++)
            _ = NextULong(ref rng);
    }

    /// <summary>
    /// Zero-allocation Fisher-Yates shuffle matching MegaCrit.Sts2.Core.Extensions.ListExtensions.UnstableShuffle.
    /// Always consumes one NextULong from <paramref name="rng"/> to seed the temporary shuffle RNG.
    /// </summary>
    public static void UnstableShuffle<T>(Span<T> items, ref HotRngState rng)
    {
        HotRngState shuffleRng = CreateFromSeed(NextULong(ref rng));
        int n = items.Length;
        while (n > 1)
        {
            n--;
            int k = NextInt(ref shuffleRng, n + 1);
            (items[k], items[n]) = (items[n], items[k]);
        }
    }
}
