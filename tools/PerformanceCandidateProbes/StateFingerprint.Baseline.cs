using System.Numerics;

namespace CombatSolver;

internal readonly record struct StateFingerprint(ulong First, ulong Second);

internal struct StateFingerprintBuilder
{
    private const ulong FirstPrime = 1099511628211UL;
    private const ulong SecondPrime = 14029467366897019727UL;
    private ulong _first;
    private ulong _second;

    public StateFingerprintBuilder()
    {
        _first = 14695981039346656037UL;
        _second = 7809847782465536322UL;
    }

    public void Add(bool value) => Add(value ? 1UL : 0UL);
    public void Add(char value) => Add((ulong)value);
    public void Add(int value) => Add(unchecked((ulong)(long)value));
    public void Add(uint value) => Add((ulong)value);
    public void Add(long value) => Add(unchecked((ulong)value));

    public void Add(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        for (int index = 0; index < bits.Length; index++)
            Add(bits[index]);
    }

    public void Add(ulong value)
    {
        // Keep each independent mixing chain in registers. Repeated field updates
        // otherwise generate store/reload dependencies in the non-inlined Tier1 body.
        ulong first = (_first ^ value) * FirstPrime;
        ulong second = BitOperations.RotateLeft(_second + value + 0x9e3779b97f4a7c15UL, 27)
            * SecondPrime;
        _first = first ^ (first >> 32);
        _second = second ^ (second >> 29);
    }

    public void Add(string? value)
    {
        if (value == null)
        {
            Add(ulong.MaxValue);
            return;
        }
        Add(value.Length);
        foreach (char character in value)
            Add(character);
    }

    public readonly StateFingerprint Finish() => new(_first, _second);

    public static ulong MixFirst(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58476d1ce4e5b9UL;
        value ^= value >> 27;
        value *= 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }

    public static ulong MixSecond(ulong value)
    {
        value ^= value >> 33;
        value *= 0xff51afd7ed558ccdUL;
        value ^= value >> 33;
        value *= 0xc4ceb9fe1a85ec53UL;
        return value ^ (value >> 33);
    }
}
