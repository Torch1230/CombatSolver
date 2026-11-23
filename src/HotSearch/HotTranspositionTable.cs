using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CombatSolver.HotKernel;

namespace CombatSolver.HotSearch;

internal enum HotTtResult : byte
{
    Admitted = 0,
    Dominated = 1,
    BypassedLimit = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal struct HotTtEntry
{
    public HotHash128 Hash;       // 16 B
    public uint Generation;       // 4 B
    public int FirstLabelIdx;     // 4 B (-1 when empty)
}

[StructLayout(LayoutKind.Sequential)]
internal struct HotTtLabel
{
    public HotStateHandle StateHandle;  // 12 B
    public HotColdSidecar? Cold;        // 8 B
    public long Score;                  // 8 B
    public int HpLost;                  // 4 B
    public int NextLabelIdx;            // 4 B (-1 = tail)
    public short PotionStrategicCost;   // 2 B
    public ushort ActionCount;          // 2 B
    public ushort ForcedPotionMask;     // 2 B (subset of forced root slots consumed)
    public byte PotionCount;            // 1 B
    public byte DeathSaveUseCount;      // 1 B
    public byte Active;                 // 1 B (1 = live, 0 = evicted/dominated)
    public byte Pad;                    // 1 B
}

internal readonly record struct HotTtProbeData(
    HotHash128 Hash,
    long Score,
    int HpLost,
    short PotionStrategicCost,
    ushort ActionCount,
    ushort ForcedPotionMask,
    byte PotionCount,
    byte DeathSaveUseCount);

/// <summary>
/// Open-addressed power-of-2 transposition table with epoch-based O(1) pass reset,
/// intrusive pooled label slab (zero per-collision <c>List&lt;T&gt;</c> allocations),
/// exact <see cref="HotHash.PackedEquals"/> verification on hash matches, and multi-objective
/// Pareto dominance across <c>(PotionCount, PotionStrategicCost, DeathSaveUseCount, HpLost, ActionCount, Score)</c>.
/// </summary>
internal sealed class HotTranspositionTable : IDisposable
{
    private const int DefaultInitialCapacity = 16_384; // power of 2
    private const int DefaultInitialLabelCapacity = 32_768;
    private const int MaxLabelsPerHashKey = 8;

    private HotTtEntry[] _entries;
    private HotTtLabel[] _labels;
    private int _mask;
    private int _activeEntries;
    private int _labelCount;
    private uint _generation;
    private readonly int _entryLimit;
    private bool _disposed;

    public HotTranspositionTable(int entryLimit = SearchPolicySnapshot.DefaultTranspositionEntryLimit)
    {
        _entryLimit = entryLimit;
        _entries = RentZeroedEntries(DefaultInitialCapacity);
        _mask = DefaultInitialCapacity - 1;
        _labels = ArrayPool<HotTtLabel>.Shared.Rent(DefaultInitialLabelCapacity);
        _generation = 1;
    }

    public int ActiveEntryCount => _activeEntries;
    public int LabelCount => _labelCount;
    public int DominatedPrunes { get; private set; }
    public int LimitBypasses { get; private set; }
    public int HashCollisionGuards { get; private set; }

    /// <summary>
    /// O(1) reset between search passes by bumping the generation counter (zero allocation, zero GC).
    /// </summary>
    public void ResetForNextPass()
    {
        if (_disposed)
            return;

        if (_labelCount > 0)
        {
            // Clear managed Cold references inside active label range.
            Array.Clear(_labels, 0, Math.Min(_labelCount, _labels.Length));
        }

        _generation++;
        if (_generation == 0)
        {
            Array.Clear(_entries, 0, _entries.Length);
            _generation = 1;
        }

        _activeEntries = 0;
        _labelCount = 0;
    }

    /// <summary>
    /// Checks whether <paramref name="probe"/> is dominated by an existing identical state in the table.
    /// Does NOT mutate the table; safe to call before committing packed bytes to the main arena.
    /// Returns the bucket slot index in <paramref name="resolvedSlot"/> for fast insertion if admitted.
    /// </summary>
    public HotTtResult CheckDominance(
        in HotTtProbeData probe,
        ReadOnlySpan<byte> candidatePacked,
        HotColdSidecar? candidateCold,
        PackedStateArena mainArena,
        out int resolvedSlot)
    {
        HotTtEntry[] entries = _entries;
        int mask = _mask;
        uint gen = _generation;
        int slot = (int)(probe.Hash.Lo & (ulong)mask);

        while (true)
        {
            ref readonly HotTtEntry entry = ref entries[slot];
            if (entry.Generation != gen)
            {
                resolvedSlot = slot;
                return _entryLimit > 0 && _activeEntries >= _entryLimit
                    ? HotTtResult.BypassedLimit
                    : HotTtResult.Admitted;
            }

            if (entry.Hash == probe.Hash)
            {
                int labelIdx = entry.FirstLabelIdx;
                while (labelIdx >= 0)
                {
                    ref readonly HotTtLabel existing = ref _labels[labelIdx];
                    if (existing.Active != 0
                        && CanCompareLabels(in existing, in probe)
                        && LabelDominatesProbe(in existing, in probe))
                    {
                        if (HotHash.PackedEquals(
                                candidatePacked,
                                candidateCold,
                                mainArena.GetSpan(existing.StateHandle),
                                existing.Cold))
                        {
                            DominatedPrunes++;
                            resolvedSlot = slot;
                            return HotTtResult.Dominated;
                        }
                        HashCollisionGuards++;
                    }
                    labelIdx = existing.NextLabelIdx;
                }

                resolvedSlot = slot;
                return HotTtResult.Admitted;
            }

            slot = (slot + 1) & mask;
        }
    }

    /// <summary>
    /// Inserts an already-committed packed state into the table, evicting any existing exact-state labels
    /// that are Pareto-dominated by the new label.
    /// </summary>
    public void InsertAdmitted(
        in HotTtProbeData probe,
        HotStateHandle committedHandle,
        HotColdSidecar? cold,
        PackedStateArena mainArena)
    {
        if (_entryLimit > 0 && _activeEntries >= _entryLimit)
        {
            // Only update existing hash keys once the limit is reached; never grow/insert new keys.
            if (!TryFindExistingSlot(probe.Hash, out int existingSlot))
            {
                LimitBypasses++;
                return;
            }
            AttachLabelToEntry(existingSlot, in probe, committedHandle, cold, mainArena);
            return;
        }

        // Maintain load factor <= 0.625 for short probe chains.
        if ((_activeEntries + 1) * 8 >= (_mask + 1) * 5)
            GrowTable();

        HotTtEntry[] entries = _entries;
        int mask = _mask;
        uint gen = _generation;
        int slot = (int)(probe.Hash.Lo & (ulong)mask);

        while (true)
        {
            ref HotTtEntry entry = ref entries[slot];
            if (entry.Generation != gen)
            {
                int newLabelIdx = AllocateLabel(in probe, committedHandle, cold, nextLabelIdx: -1);
                entry.Hash = probe.Hash;
                entry.Generation = gen;
                entry.FirstLabelIdx = newLabelIdx;
                _activeEntries++;
                return;
            }

            if (entry.Hash == probe.Hash)
            {
                AttachLabelToEntry(slot, in probe, committedHandle, cold, mainArena);
                return;
            }

            slot = (slot + 1) & mask;
        }
    }

    private void AttachLabelToEntry(
        int slot,
        in HotTtProbeData probe,
        HotStateHandle committedHandle,
        HotColdSidecar? cold,
        PackedStateArena mainArena)
    {
        ReadOnlySpan<byte> committedSpan = mainArena.GetSpan(committedHandle);
        ref HotTtEntry entry = ref _entries[slot];

        int activeCount = 0;
        int worstIdx = -1;
        long worstScore = long.MaxValue;
        int reusableIdx = -1;

        int labelIdx = entry.FirstLabelIdx;
        while (labelIdx >= 0)
        {
            ref HotTtLabel existing = ref _labels[labelIdx];
            if (existing.Active == 0)
            {
                if (reusableIdx < 0)
                    reusableIdx = labelIdx;
                labelIdx = existing.NextLabelIdx;
                continue;
            }

            if (CanCompareLabels(in probe, in existing)
                && ProbeDominatesLabel(in probe, in existing)
                && HotHash.PackedEquals(
                    committedSpan,
                    cold,
                    mainArena.GetSpan(existing.StateHandle),
                    existing.Cold))
            {
                // New state strictly dominates existing label -> deactivate and mark slot reusable.
                existing.Active = 0;
                existing.Cold = null;
                if (reusableIdx < 0)
                    reusableIdx = labelIdx;
            }
            else
            {
                activeCount++;
                if (existing.Score < worstScore)
                {
                    worstScore = existing.Score;
                    worstIdx = labelIdx;
                }
            }

            labelIdx = existing.NextLabelIdx;
        }

        if (reusableIdx >= 0)
        {
            OverwriteLabel(reusableIdx, in probe, committedHandle, cold);
            return;
        }

        if (activeCount >= MaxLabelsPerHashKey && worstIdx >= 0)
        {
            if (probe.Score > worstScore)
                OverwriteLabel(worstIdx, in probe, committedHandle, cold);
            return;
        }

        int newIdx = AllocateLabel(in probe, committedHandle, cold, entry.FirstLabelIdx);
        entry.FirstLabelIdx = newIdx;
    }

    private bool TryFindExistingSlot(HotHash128 hash, out int foundSlot)
    {
        HotTtEntry[] entries = _entries;
        int mask = _mask;
        uint gen = _generation;
        int slot = (int)(hash.Lo & (ulong)mask);

        while (true)
        {
            ref readonly HotTtEntry entry = ref entries[slot];
            if (entry.Generation != gen)
            {
                foundSlot = -1;
                return false;
            }
            if (entry.Hash == hash)
            {
                foundSlot = slot;
                return true;
            }
            slot = (slot + 1) & mask;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CanCompareLabels(in HotTtLabel dominator, in HotTtProbeData candidate)
        => (dominator.ForcedPotionMask & candidate.ForcedPotionMask) == candidate.ForcedPotionMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CanCompareLabels(in HotTtProbeData dominator, in HotTtLabel candidate)
        => (dominator.ForcedPotionMask & candidate.ForcedPotionMask) == candidate.ForcedPotionMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool LabelDominatesProbe(in HotTtLabel existing, in HotTtProbeData probe)
        => existing.PotionCount <= probe.PotionCount
            && existing.PotionStrategicCost <= probe.PotionStrategicCost
            && existing.DeathSaveUseCount <= probe.DeathSaveUseCount
            && existing.HpLost <= probe.HpLost
            && existing.Score >= probe.Score
            && (existing.ActionCount <= probe.ActionCount || existing.Score > probe.Score);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ProbeDominatesLabel(in HotTtProbeData probe, in HotTtLabel existing)
        => probe.PotionCount <= existing.PotionCount
            && probe.PotionStrategicCost <= existing.PotionStrategicCost
            && probe.DeathSaveUseCount <= existing.DeathSaveUseCount
            && probe.HpLost <= existing.HpLost
            && probe.Score >= existing.Score
            && (probe.ActionCount <= existing.ActionCount || probe.Score > existing.Score)
            && (probe.PotionCount < existing.PotionCount
                || probe.PotionStrategicCost < existing.PotionStrategicCost
                || probe.DeathSaveUseCount < existing.DeathSaveUseCount
                || probe.HpLost < existing.HpLost
                || probe.Score > existing.Score
                || probe.ActionCount < existing.ActionCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OverwriteLabel(
        int labelIdx,
        in HotTtProbeData probe,
        HotStateHandle handle,
        HotColdSidecar? cold)
    {
        ref HotTtLabel slot = ref _labels[labelIdx];
        int preservedNext = slot.NextLabelIdx;
        slot = new HotTtLabel
        {
            StateHandle = handle,
            Cold = cold,
            Score = probe.Score,
            HpLost = probe.HpLost,
            NextLabelIdx = preservedNext,
            PotionStrategicCost = probe.PotionStrategicCost,
            ActionCount = probe.ActionCount,
            ForcedPotionMask = probe.ForcedPotionMask,
            PotionCount = probe.PotionCount,
            DeathSaveUseCount = probe.DeathSaveUseCount,
            Active = 1,
        };
    }

    private int AllocateLabel(
        in HotTtProbeData probe,
        HotStateHandle handle,
        HotColdSidecar? cold,
        int nextLabelIdx)
    {
        if (_labelCount >= _labels.Length)
            GrowLabels();

        int idx = _labelCount++;
        _labels[idx] = new HotTtLabel
        {
            StateHandle = handle,
            Cold = cold,
            Score = probe.Score,
            HpLost = probe.HpLost,
            NextLabelIdx = nextLabelIdx,
            PotionStrategicCost = probe.PotionStrategicCost,
            ActionCount = probe.ActionCount,
            ForcedPotionMask = probe.ForcedPotionMask,
            PotionCount = probe.PotionCount,
            DeathSaveUseCount = probe.DeathSaveUseCount,
            Active = 1,
        };
        return idx;
    }

    private void GrowTable()
    {
        HotTtEntry[] oldEntries = _entries;
        int oldCapacity = _mask + 1;
        int newCapacity = oldCapacity << 1;
        HotTtEntry[] newEntries = RentZeroedEntries(newCapacity);
        int newMask = newCapacity - 1;
        uint gen = _generation;

        for (int i = 0; i < oldCapacity; i++)
        {
            ref readonly HotTtEntry old = ref oldEntries[i];
            if (old.Generation != gen)
                continue;

            int dst = (int)(old.Hash.Lo & (ulong)newMask);
            while (newEntries[dst].Generation == gen)
                dst = (dst + 1) & newMask;
            newEntries[dst] = old;
        }

        _entries = newEntries;
        _mask = newMask;
        // Return previous array cleanly to prevent ArrayPool leaks.
        ArrayPool<HotTtEntry>.Shared.Return(oldEntries, clearArray: false);
    }

    private void GrowLabels()
    {
        HotTtLabel[] oldLabels = _labels;
        int newCapacity = (int)BitOperations.RoundUpToPowerOf2((uint)(oldLabels.Length * 2));
        HotTtLabel[] newLabels = ArrayPool<HotTtLabel>.Shared.Rent(newCapacity);
        Array.Copy(oldLabels, 0, newLabels, 0, _labelCount);
        _labels = newLabels;
        ArrayPool<HotTtLabel>.Shared.Return(oldLabels, clearArray: true);
    }

    private static HotTtEntry[] RentZeroedEntries(int powerOfTwoCapacity)
    {
        HotTtEntry[] rented = ArrayPool<HotTtEntry>.Shared.Rent(powerOfTwoCapacity);
        Array.Clear(rented, 0, powerOfTwoCapacity);
        return rented;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_entries.Length > 0)
        {
            ArrayPool<HotTtEntry>.Shared.Return(_entries, clearArray: false);
            _entries = [];
        }
        if (_labels.Length > 0)
        {
            ArrayPool<HotTtLabel>.Shared.Return(_labels, clearArray: true);
            _labels = [];
        }
    }
}
