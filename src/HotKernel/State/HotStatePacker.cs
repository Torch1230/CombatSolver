using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CombatSolver.HotKernel;

internal static unsafe class HotStatePacker
{
    public static int ComputePackedSize(in CombatHotState state)
    {
        int totalCards = state.TotalCardCount;
        return CombatHotState.FixedCoreSize
            + state.EnemyCount * sizeof(HotEnemy)
            + state.PowerCount * sizeof(HotPower)
            + state.RelicStateCount * sizeof(HotRelicState)
            + totalCards * sizeof(HotCard);
    }

    public static ReadOnlySpan<byte> AsReadOnlyBytes<T>(in T value) where T : unmanaged
        => MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in value)),
            sizeof(T));

    public static Span<byte> AsBytes<T>(ref T value) where T : unmanaged
        => MemoryMarshal.CreateSpan(
            ref Unsafe.As<T, byte>(ref value),
            sizeof(T));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ZeroCardPadding(ref HotCard c)
    {
        int modCount = Math.Min((int)c.CostModCount, HotLimits.MaxEnergyCostMods);
        for (int m = modCount; m < HotLimits.MaxEnergyCostMods; m++)
            c.EnergyCostMods[m] = default;
        if ((c.TempStarCostPacked & 0x80) == 0)
            c.TempStarCostPacked = 0;
    }

    public static void ZeroExplicitPadding(ref CombatHotState s)
    {
        s.PadCore1 = 0;
        s.PadCore2 = 0;
        s.PadCore3 = 0;
        s.Growth[11] = 0;

        s.Player.Pad = 0;
        if ((s.CombatFlags & HotCombatFlags.HasOsty) == 0)
            s.Osty = default;
        else
            s.Osty.Pad = 0;

        if ((s.CombatFlags & HotCombatFlags.HasWuppy) == 0)
            s.Wuppy = default;
        else
            s.Wuppy.Pad = 0;

        s.Fool.Pad0 = 0;
        s.Fool.Pad1 = 0;

        for (int i = 0; i < HotLimits.MaxRngStreams; i++)
            s.Rng[i].Pad = 0;

        for (int i = Math.Min((int)s.RosterCount, HotLimits.MaxEnemies); i < HotLimits.MaxEnemies; i++)
            s.ActiveEnemyRoster[i] = 0;

        int activePotionSlots = Math.Min((int)s.PotionSlotCount, HotLimits.MaxPotions);
        ushort validPotionMask = 0;
        for (int i = 0; i < activePotionSlots; i++)
        {
            if (s.Potions[i] != 0)
                validPotionMask |= (ushort)(1 << i);
        }
        s.CombatGeneratedPotionMask &= validPotionMask;
        for (int i = activePotionSlots; i < HotLimits.MaxPotions; i++)
            s.Potions[i] = 0;

        for (int i = Math.Min((int)s.OrbCount, HotLimits.MaxOrbs); i < HotLimits.MaxOrbs; i++)
            s.Orbs[i] = default;

        int activeArrows = Math.Min((int)s.ArrowCount, HotLimits.MaxArrows);
        for (int i = 0; i < activeArrows; i++)
            s.Fool.Arrows[i] &= HotFoolState.ArrowValidMask;
        for (int i = activeArrows; i < HotLimits.MaxArrows; i++)
            s.Fool.Arrows[i] = 0;

        if ((s.CombatFlags & HotCombatFlags.HasHistoryCourseCard) == 0)
            s.HistoryCourseCard = default;
        else
            ZeroCardPadding(ref s.HistoryCourseCard);

        int enemyCount = Math.Min((int)s.EnemyCount, HotLimits.MaxEnemies);
        for (int i = 0; i < enemyCount; i++)
        {
            ref HotEnemy enemy = ref s.Enemies[i];
            enemy.Vital.Pad = 0;
            int recentCount = Math.Min((int)enemy.RecentMoveCount, HotLimits.MaxMonsterRecentMoves);
            for (int r = recentCount; r < HotLimits.MaxMonsterRecentMoves; r++)
                enemy.RecentMoves[r] = 0;
        }

        int totalCards = Math.Min(s.TotalCardCount, HotLimits.MaxCards);
        for (int i = 0; i < totalCards; i++)
            ZeroCardPadding(ref s.Cards[i]);
    }

    /// <summary>
    /// Canonicalizes CardUid values so only cards actively referenced by powers/melodies/sidecars
    /// carry dense 1..k UIDs in pile order, and unreferenced cards carry CardUid=0 for dedupe.
    /// Also zeroes all explicit pad bytes and unused tail slots inside FixedCore.
    /// </summary>
    public static void CanonicalizeForPack(ref CombatHotState s, ref HotColdSidecar? cold)
    {
        ZeroExplicitPadding(ref s);

        if (cold != null && cold.IsEmpty)
            cold = null;

        int totalCards = Math.Min(s.TotalCardCount, HotLimits.MaxCards);
        int powerCount = Math.Min((int)s.PowerCount, HotLimits.MaxPowers);

        bool hasColdCardRefs = cold != null && (
            cold.DetachedCards is { Length: > 0 }
            || cold.DecimalVars is { Length: > 0 }
            || cold.CostOverflow is { Length: > 0 }
            || cold.RuptureEntries is { Length: > 0 }
            || cold.DampenEntries is { Length: > 0 });

        bool anyRef = hasColdCardRefs
            || s.HistoryCourseUid != 0
            || s.Fool.FirstPlayCardUid[0] != 0
            || s.Fool.FirstPlayCardUid[1] != 0
            || s.Fool.FirstPlayCardUid[2] != 0;

        if (!anyRef)
        {
            for (int i = 0; i < powerCount; i++)
            {
                if (s.Powers[i].AuxCardUid != 0)
                {
                    anyRef = true;
                    break;
                }
            }
        }

        if (!anyRef)
        {
            for (int i = 0; i < totalCards; i++)
                s.Cards[i].CardUid = 0;
            if ((s.CombatFlags & HotCombatFlags.HasHistoryCourseCard) != 0)
                s.HistoryCourseCard.CardUid = 0;
            s.NextCardUid = 1;
            return;
        }

        Span<ushort> remap = stackalloc ushort[HotLimits.MaxCards];
        int mapCount = 0;

        for (int i = 0; i < totalCards; i++)
        {
            ushort oldUid = s.Cards[i].CardUid;
            if (oldUid != 0 && HasCardUidReference(in s, cold, powerCount, oldUid))
            {
                ushort mapped = LookupMappedUid(remap, mapCount, oldUid);
                if (mapped == 0)
                {
                    if (mapCount >= remap.Length)
                        throw new HotCapacityExceededException("Referenced CardUid count exceeded HotLimits.MaxCards.");
                    remap[mapCount++] = oldUid;
                    mapped = (ushort)mapCount;
                }
                s.Cards[i].CardUid = mapped;
            }
            else
            {
                s.Cards[i].CardUid = 0;
            }
        }

        if (cold?.DetachedCards is { Length: > 0 } detached)
        {
            bool detachedChanged = false;
            HotCard[]? nextDetached = null;
            for (int i = 0; i < detached.Length; i++)
            {
                HotCard c = detached[i];
                ZeroCardPadding(ref c);
                ushort oldUid = c.CardUid;
                ushort newUid = 0;
                if (oldUid != 0 && HasCardUidReference(in s, cold, powerCount, oldUid))
                {
                    newUid = LookupMappedUid(remap, mapCount, oldUid);
                    if (newUid == 0)
                    {
                        if (mapCount >= remap.Length)
                            throw new HotCapacityExceededException("Referenced CardUid count exceeded HotLimits.MaxCards.");
                        remap[mapCount++] = oldUid;
                        newUid = (ushort)mapCount;
                    }
                }
                c.CardUid = newUid;
                if (!detachedChanged && !AsReadOnlyBytes(in detached[i]).SequenceEqual(AsReadOnlyBytes(in c)))
                {
                    detachedChanged = true;
                    nextDetached = new HotCard[detached.Length];
                    for (int prev = 0; prev < i; prev++)
                        nextDetached[prev] = detached[prev];
                }
                if (nextDetached != null)
                    nextDetached[i] = c;
            }
            if (nextDetached != null)
                cold = cold with { DetachedCards = nextDetached };
        }

        s.HistoryCourseUid = LookupMappedUid(remap, mapCount, s.HistoryCourseUid);
        if ((s.CombatFlags & HotCombatFlags.HasHistoryCourseCard) != 0)
            s.HistoryCourseCard.CardUid = LookupMappedUid(remap, mapCount, s.HistoryCourseCard.CardUid);

        for (int i = 0; i < 3; i++)
            s.Fool.FirstPlayCardUid[i] = LookupMappedUid(remap, mapCount, s.Fool.FirstPlayCardUid[i]);

        for (int i = 0; i < powerCount; i++)
            s.Powers[i].AuxCardUid = LookupMappedUid(remap, mapCount, s.Powers[i].AuxCardUid);

        if (cold != null)
        {
            cold = RemapColdSidecar(cold, remap, mapCount);
            if (cold.IsEmpty)
                cold = null;
        }

        s.NextCardUid = (ushort)(mapCount + 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort LookupMappedUid(ReadOnlySpan<ushort> remap, int mapCount, ushort oldUid)
    {
        if (oldUid == 0)
            return 0;
        for (int i = 0; i < mapCount; i++)
        {
            if (remap[i] == oldUid)
                return (ushort)(i + 1);
        }
        return 0;
    }

    private static bool HasCardUidReference(in CombatHotState s, HotColdSidecar? cold, int powerCount, ushort uid)
    {
        if (s.HistoryCourseUid == uid)
            return true;

        ReadOnlySpan<ushort> firstPlayUids = MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<HotUShort3, ushort>(ref Unsafe.AsRef(in s.Fool.FirstPlayCardUid)),
            3);
        for (int i = 0; i < 3; i++)
        {
            if (firstPlayUids[i] == uid)
                return true;
        }

        if (powerCount > 0)
        {
            ReadOnlySpan<HotPower> powers = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<HotPower96, HotPower>(ref Unsafe.AsRef(in s.Powers)),
                powerCount);
            for (int i = 0; i < powerCount; i++)
            {
                if (powers[i].AuxCardUid == uid)
                    return true;
            }
        }

        if (cold != null)
        {
            if (cold.DecimalVars is { Length: > 0 } decs)
            {
                for (int i = 0; i < decs.Length; i++)
                    if (decs[i].CardUid == uid) return true;
            }
            if (cold.CostOverflow is { Length: > 0 } co)
            {
                for (int i = 0; i < co.Length; i++)
                    if (co[i].CardUid == uid) return true;
            }
            if (cold.RuptureEntries is { Length: > 0 } ru)
            {
                for (int i = 0; i < ru.Length; i++)
                    if (ru[i].CardUid == uid) return true;
            }
            if (cold.DampenEntries is { Length: > 0 } da)
            {
                for (int i = 0; i < da.Length; i++)
                    if (da[i].CardUid == uid) return true;
            }
        }

        return false;
    }

    private static HotColdSidecar RemapColdSidecar(HotColdSidecar cold, ReadOnlySpan<ushort> remap, int mapCount)
    {
        if (cold.DecimalVars is { Length: > 0 } decs)
        {
            bool changed = false;
            int kept = 0;
            for (int i = 0; i < decs.Length; i++)
            {
                ushort mapped = LookupMappedUid(remap, mapCount, decs[i].CardUid);
                if (mapped != 0) kept++;
                if (mapped != decs[i].CardUid) changed = true;
            }
            if (changed)
            {
                HotDecimalVarEntry[]? next = kept == 0 ? null : new HotDecimalVarEntry[kept];
                int dstIdx = 0;
                for (int i = 0; i < decs.Length; i++)
                {
                    ushort mapped = LookupMappedUid(remap, mapCount, decs[i].CardUid);
                    if (mapped != 0)
                        next![dstIdx++] = new HotDecimalVarEntry(mapped, decs[i].Delta);
                }
                cold = cold with { DecimalVars = next };
            }
        }

        if (cold.CostOverflow is { Length: > 0 } costs)
        {
            bool changed = false;
            int kept = 0;
            for (int i = 0; i < costs.Length; i++)
            {
                ushort mapped = LookupMappedUid(remap, mapCount, costs[i].CardUid);
                if (mapped != 0) kept++;
                if (mapped != costs[i].CardUid) changed = true;
            }
            if (changed)
            {
                HotCardCostOverflow[]? next = kept == 0 ? null : new HotCardCostOverflow[kept];
                int dstIdx = 0;
                for (int i = 0; i < costs.Length; i++)
                {
                    ushort mapped = LookupMappedUid(remap, mapCount, costs[i].CardUid);
                    if (mapped != 0)
                        next![dstIdx++] = new HotCardCostOverflow(mapped, costs[i].EnergyMods, costs[i].TempStarCosts);
                }
                cold = cold with { CostOverflow = next };
            }
        }

        if (cold.RuptureEntries is { Length: > 0 } ruptures)
        {
            bool changed = false;
            int kept = 0;
            for (int i = 0; i < ruptures.Length; i++)
            {
                ushort mapped = LookupMappedUid(remap, mapCount, ruptures[i].CardUid);
                if (mapped != 0) kept++;
                if (mapped != ruptures[i].CardUid) changed = true;
            }
            if (changed)
            {
                HotRuptureEntry[]? next = kept == 0 ? null : new HotRuptureEntry[kept];
                int dstIdx = 0;
                for (int i = 0; i < ruptures.Length; i++)
                {
                    ushort mapped = LookupMappedUid(remap, mapCount, ruptures[i].CardUid);
                    if (mapped != 0)
                        next![dstIdx++] = new HotRuptureEntry(mapped, ruptures[i].Strength);
                }
                cold = cold with { RuptureEntries = next };
            }
        }

        if (cold.DampenEntries is { Length: > 0 } dampens)
        {
            bool changed = false;
            int kept = 0;
            for (int i = 0; i < dampens.Length; i++)
            {
                ushort mapped = LookupMappedUid(remap, mapCount, dampens[i].CardUid);
                if (mapped != 0) kept++;
                if (mapped != dampens[i].CardUid) changed = true;
            }
            if (changed)
            {
                HotDampenEntry[]? next = kept == 0 ? null : new HotDampenEntry[kept];
                int dstIdx = 0;
                for (int i = 0; i < dampens.Length; i++)
                {
                    ushort mapped = LookupMappedUid(remap, mapCount, dampens[i].CardUid);
                    if (mapped != 0)
                        next![dstIdx++] = new HotDampenEntry(mapped, dampens[i].OriginalUpgrade, dampens[i].CasterPowerIdx);
                }
                cold = cold with { DampenEntries = next };
            }
        }

        return cold;
    }

    public static int Pack(ref CombatHotState src, ref HotColdSidecar? cold, Span<byte> dst, bool canonicalize = true)
    {
        if (canonicalize)
            CanonicalizeForPack(ref src, ref cold);

        int packedSize = ComputePackedSize(in src);
        if (dst.Length < packedSize)
            throw new ArgumentException($"Destination buffer too small ({dst.Length} < {packedSize}).", nameof(dst));

        int offset = 0;

        // 1. FixedCore
        MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<CombatHotState, byte>(ref src),
                CombatHotState.FixedCoreSize)
            .CopyTo(dst.Slice(offset, CombatHotState.FixedCoreSize));
        offset += CombatHotState.FixedCoreSize;

        // 2. Active Enemies
        int enemyBytes = src.EnemyCount * sizeof(HotEnemy);
        if (enemyBytes > 0)
        {
            MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<HotEnemy, byte>(ref src.Enemies[0]),
                    enemyBytes)
                .CopyTo(dst.Slice(offset, enemyBytes));
            offset += enemyBytes;
        }

        // 3. Active Powers
        int powerBytes = src.PowerCount * sizeof(HotPower);
        if (powerBytes > 0)
        {
            MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<HotPower, byte>(ref src.Powers[0]),
                    powerBytes)
                .CopyTo(dst.Slice(offset, powerBytes));
            offset += powerBytes;
        }

        // 4. Active RelicStates
        int relicBytes = src.RelicStateCount * sizeof(HotRelicState);
        if (relicBytes > 0)
        {
            MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<HotRelicState, byte>(ref src.RelicStates[0]),
                    relicBytes)
                .CopyTo(dst.Slice(offset, relicBytes));
            offset += relicBytes;
        }

        // 5. Active Cards
        int cardBytes = src.TotalCardCount * sizeof(HotCard);
        if (cardBytes > 0)
        {
            MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<HotCard, byte>(ref src.Cards[0]),
                    cardBytes)
                .CopyTo(dst.Slice(offset, cardBytes));
        }

        return packedSize;
    }

    public static void Unpack(ReadOnlySpan<byte> src, ref CombatHotState dst)
    {
        if (src.Length < CombatHotState.FixedCoreSize)
            throw new ArgumentException($"Packed state shorter than FixedCore ({src.Length}).", nameof(src));

        int offset = 0;

        // 1. FixedCore
        src.Slice(offset, CombatHotState.FixedCoreSize)
            .CopyTo(MemoryMarshal.CreateSpan(
                ref Unsafe.As<CombatHotState, byte>(ref dst),
                CombatHotState.FixedCoreSize));
        offset += CombatHotState.FixedCoreSize;

        if (dst.EnemyCount > HotLimits.MaxEnemies
            || dst.PowerCount > HotLimits.MaxPowers
            || dst.RelicStateCount > HotLimits.MaxRelicStates
            || dst.TotalCardCount > HotLimits.MaxCards)
        {
            throw new InvalidOperationException(
                $"Corrupt packed state counts: enemies={dst.EnemyCount}, powers={dst.PowerCount}, relics={dst.RelicStateCount}, cards={dst.TotalCardCount}.");
        }

        int expectedSize = ComputePackedSize(in dst);
        if (src.Length < expectedSize)
            throw new ArgumentException($"Packed state truncated ({src.Length} < {expectedSize}).", nameof(src));

        // 2. Active Enemies
        int enemyBytes = dst.EnemyCount * sizeof(HotEnemy);
        if (enemyBytes > 0)
        {
            src.Slice(offset, enemyBytes)
                .CopyTo(MemoryMarshal.CreateSpan(
                    ref Unsafe.As<HotEnemy, byte>(ref dst.Enemies[0]),
                    enemyBytes));
            offset += enemyBytes;
        }

        // 3. Active Powers
        int powerBytes = dst.PowerCount * sizeof(HotPower);
        if (powerBytes > 0)
        {
            src.Slice(offset, powerBytes)
                .CopyTo(MemoryMarshal.CreateSpan(
                    ref Unsafe.As<HotPower, byte>(ref dst.Powers[0]),
                    powerBytes));
            offset += powerBytes;
        }

        // 4. Active RelicStates
        int relicBytes = dst.RelicStateCount * sizeof(HotRelicState);
        if (relicBytes > 0)
        {
            src.Slice(offset, relicBytes)
                .CopyTo(MemoryMarshal.CreateSpan(
                    ref Unsafe.As<HotRelicState, byte>(ref dst.RelicStates[0]),
                    relicBytes));
            offset += relicBytes;
        }

        // 5. Active Cards
        int cardBytes = dst.TotalCardCount * sizeof(HotCard);
        if (cardBytes > 0)
        {
            src.Slice(offset, cardBytes)
                .CopyTo(MemoryMarshal.CreateSpan(
                    ref Unsafe.As<HotCard, byte>(ref dst.Cards[0]),
                    cardBytes));
        }
    }
}
