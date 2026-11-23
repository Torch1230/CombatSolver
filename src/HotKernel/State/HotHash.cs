using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CombatSolver.HotKernel;

internal readonly record struct HotHash128(ulong Lo, ulong Hi)
{
    public override string ToString() => $"{Hi:X16}{Lo:X16}";
}

internal struct HotHash128Accumulator
{
    private const ulong P1 = 0x9E3779B185EBCA87UL;
    private const ulong P2 = 0xC2B2AE3D27D4EB4FUL;
    private const ulong P3 = 0x165667B19E3779F9UL;
    private const ulong P4 = 0x85EBCA77C2B2AE63UL;
    private const ulong P5 = 0x27D4EB2F165667C5UL;

    private ulong _v0;
    private ulong _v1;
    private ulong _v2;
    private ulong _v3;
    private ulong _len;

    public static HotHash128Accumulator Create(ulong seed = 0) => new()
    {
        _v0 = seed + P1 + P2,
        _v1 = seed + P2,
        _v2 = seed,
        _v3 = seed - P1,
        _len = 0,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Round(ulong acc, ulong lane)
    {
        acc += lane * P2;
        acc = BitOperations.RotateLeft(acc, 31);
        return acc * P1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Avalanche(ulong x)
    {
        x ^= x >> 33;
        x *= 0xFF51AFD7ED558CCDUL;
        x ^= x >> 33;
        x *= 0xC4CEB9FE1A85EC53UL;
        x ^= x >> 33;
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MixUInt64(ulong val)
    {
        _v0 = Round(_v0, val);
        _v1 = Round(_v1, val ^ P3);
        _len += 8;
    }

    public unsafe void MixBytes(ReadOnlySpan<byte> data)
    {
        _len += (ulong)data.Length;
        fixed (byte* pBase = data)
        {
            byte* p = pBase;
            int remaining = data.Length;
            ulong v0 = _v0, v1 = _v1, v2 = _v2, v3 = _v3;

            while (remaining >= 32)
            {
                v0 = Round(v0, Unsafe.ReadUnaligned<ulong>(p));
                v1 = Round(v1, Unsafe.ReadUnaligned<ulong>(p + 8));
                v2 = Round(v2, Unsafe.ReadUnaligned<ulong>(p + 16));
                v3 = Round(v3, Unsafe.ReadUnaligned<ulong>(p + 24));
                p += 32;
                remaining -= 32;
            }

            if (remaining >= 8)
            {
                v0 = Round(v0, Unsafe.ReadUnaligned<ulong>(p));
                p += 8;
                remaining -= 8;
            }
            if (remaining >= 8)
            {
                v1 = Round(v1, Unsafe.ReadUnaligned<ulong>(p));
                p += 8;
                remaining -= 8;
            }
            if (remaining >= 8)
            {
                v2 = Round(v2, Unsafe.ReadUnaligned<ulong>(p));
                p += 8;
                remaining -= 8;
            }
            if (remaining > 0)
            {
                ulong tail = 0;
                for (int i = 0; i < remaining; i++)
                    tail |= (ulong)p[i] << (i * 8);
                v3 = Round(v3, tail ^ ((ulong)remaining << 56));
            }

            _v0 = v0;
            _v1 = v1;
            _v2 = v2;
            _v3 = v3;
        }
    }

    public readonly HotHash128 Finish()
    {
        ulong lo = BitOperations.RotateLeft(_v0, 1)
            + BitOperations.RotateLeft(_v1, 7)
            + BitOperations.RotateLeft(_v2, 12)
            + BitOperations.RotateLeft(_v3, 18)
            + _len * P5;
        ulong hi = BitOperations.RotateLeft(_v0 ^ P3, 13)
            + BitOperations.RotateLeft(_v1 ^ P4, 29)
            + BitOperations.RotateLeft(_v2 + P1, 37)
            + BitOperations.RotateLeft(_v3 + P2, 47)
            + (_len ^ P4);
        lo = Avalanche(lo ^ hi);
        hi = Avalanche(hi ^ BitOperations.RotateLeft(lo, 23));
        return new HotHash128(lo, hi);
    }
}

internal static class HotHash
{
    public static HotHash128 ComputeHash(ReadOnlySpan<byte> packed, HotColdSidecar? cold)
    {
        HotHash128Accumulator acc = HotHash128Accumulator.Create();
        acc.MixBytes(packed);
        if (cold != null && !cold.IsEmpty)
        {
            acc.MixUInt64(0x5349444543415231UL);
            cold.MixInto(ref acc);
        }
        return acc.Finish();
    }

    public static bool PackedEquals(
        ReadOnlySpan<byte> left,
        HotColdSidecar? leftCold,
        ReadOnlySpan<byte> right,
        HotColdSidecar? rightCold)
        => left.SequenceEqual(right) && HotColdSidecar.StructuralEquals(leftCold, rightCold);
}
