namespace CombatSolver.HotKernel;

internal readonly record struct HotDecimalVarEntry(ushort CardUid, decimal Delta);

internal readonly record struct HotTempStarCost(
    byte Cost,
    bool ClearsWhenTurnEnds,
    bool ClearsWhenCardIsPlayed);

internal sealed record HotCardCostOverflow(
    ushort CardUid,
    HotEnergyCostMod[] EnergyMods,
    HotTempStarCost[] TempStarCosts);

internal sealed record HotRuptureEntry(ushort CardUid, int Strength);

internal sealed record HotDampenEntry(ushort CardUid, byte OriginalUpgrade, int CasterPowerIdx);

internal sealed record HotMonsterStateLogOverflow(byte EnemySlot, ushort[] MoveNodeIds);

/// <summary>
/// Immutable COW cold sidecar. Stays null in &gt;99% of vanilla fights (zero per-branch alloc).
/// </summary>
internal sealed record HotColdSidecar(
    HotCard[]? DetachedCards = null,
    HotDecimalVarEntry[]? DecimalVars = null,
    HotCardCostOverflow[]? CostOverflow = null,
    HotRuptureEntry[]? RuptureEntries = null,
    HotDampenEntry[]? DampenEntries = null,
    HotMonsterStateLogOverflow[]? MonsterLogs = null)
{
    public bool IsEmpty =>
        (DetachedCards == null || DetachedCards.Length == 0)
        && (DecimalVars == null || DecimalVars.Length == 0)
        && (CostOverflow == null || CostOverflow.Length == 0)
        && (RuptureEntries == null || RuptureEntries.Length == 0)
        && (DampenEntries == null || DampenEntries.Length == 0)
        && (MonsterLogs == null || MonsterLogs.Length == 0);

    public static decimal GetDecimalVarDelta(HotColdSidecar? cold, ushort cardUid)
    {
        if (cardUid == 0 || cold?.DecimalVars is not { Length: > 0 } entries)
            return 0m;
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].CardUid == cardUid)
                return entries[i].Delta;
        }
        return 0m;
    }

    public static void SetDecimalVarDelta(ref HotColdSidecar? cold, ushort cardUid, decimal delta)
    {
        if (cardUid == 0)
            return;
        if (delta == 0m)
        {
            RemoveDecimalVar(ref cold, cardUid);
            return;
        }
        if (delta.Scale != 0)
            delta /= 1.0000000000000000000000000000m;

        HotDecimalVarEntry[]? prev = cold?.DecimalVars;
        if (prev == null || prev.Length == 0)
        {
            HotDecimalVarEntry[] single = [new HotDecimalVarEntry(cardUid, delta)];
            cold = cold == null
                ? new HotColdSidecar(DecimalVars: single)
                : cold with { DecimalVars = single };
            return;
        }

        int insertAt = prev.Length;
        for (int i = 0; i < prev.Length; i++)
        {
            if (prev[i].CardUid == cardUid)
            {
                if (prev[i].Delta == delta && prev[i].Delta.Scale == delta.Scale)
                    return;
                HotDecimalVarEntry[] updated = new HotDecimalVarEntry[prev.Length];
                Array.Copy(prev, updated, prev.Length);
                updated[i] = new HotDecimalVarEntry(cardUid, delta);
                cold = cold! with { DecimalVars = updated };
                return;
            }
            if (prev[i].CardUid > cardUid)
            {
                insertAt = i;
                break;
            }
        }

        HotDecimalVarEntry[] grown = new HotDecimalVarEntry[prev.Length + 1];
        if (insertAt > 0)
            Array.Copy(prev, 0, grown, 0, insertAt);
        grown[insertAt] = new HotDecimalVarEntry(cardUid, delta);
        if (insertAt < prev.Length)
            Array.Copy(prev, insertAt, grown, insertAt + 1, prev.Length - insertAt);
        cold = cold! with { DecimalVars = grown };
    }

    public static void CloneDecimalVar(ref HotColdSidecar? cold, ushort sourceUid, ushort targetUid)
    {
        if (sourceUid == 0 || targetUid == 0 || sourceUid == targetUid)
            return;
        decimal delta = GetDecimalVarDelta(cold, sourceUid);
        if (delta != 0m)
            SetDecimalVarDelta(ref cold, targetUid, delta);
    }

    public static void RemoveDecimalVar(ref HotColdSidecar? cold, ushort cardUid)
    {
        if (cardUid == 0 || cold?.DecimalVars is not { Length: > 0 } prev)
            return;

        int idx = -1;
        for (int i = 0; i < prev.Length; i++)
        {
            if (prev[i].CardUid == cardUid)
            {
                idx = i;
                break;
            }
        }
        if (idx < 0)
            return;

        HotDecimalVarEntry[]? nextEntries;
        if (prev.Length == 1)
        {
            nextEntries = null;
        }
        else
        {
            nextEntries = new HotDecimalVarEntry[prev.Length - 1];
            if (idx > 0)
                Array.Copy(prev, 0, nextEntries, 0, idx);
            if (idx + 1 < prev.Length)
                Array.Copy(prev, idx + 1, nextEntries, idx, prev.Length - idx - 1);
        }

        HotColdSidecar next = cold with { DecimalVars = nextEntries };
        cold = next.IsEmpty ? null : next;
    }

    public void MixInto(ref HotHash128Accumulator acc)
    {
        if (DetachedCards is { Length: > 0 } detached)
        {
            acc.MixUInt64((ulong)detached.Length);
            for (int i = 0; i < detached.Length; i++)
                acc.MixBytes(HotStatePacker.AsReadOnlyBytes(in detached[i]));
        }
        if (DecimalVars is { Length: > 0 } decimals)
        {
            acc.MixUInt64((ulong)decimals.Length);
            Span<int> bits = stackalloc int[4];
            for (int i = 0; i < decimals.Length; i++)
            {
                acc.MixUInt64(decimals[i].CardUid);
                decimal.GetBits(decimals[i].Delta, bits);
                acc.MixUInt64((uint)bits[0] | ((ulong)(uint)bits[1] << 32));
                acc.MixUInt64((uint)bits[2] | ((ulong)(uint)bits[3] << 32));
            }
        }
        if (CostOverflow is { Length: > 0 } costs)
        {
            acc.MixUInt64((ulong)costs.Length);
            for (int i = 0; i < costs.Length; i++)
            {
                HotCardCostOverflow c = costs[i];
                acc.MixUInt64(c.CardUid | ((ulong)c.EnergyMods.Length << 16) | ((ulong)c.TempStarCosts.Length << 32));
                for (int j = 0; j < c.EnergyMods.Length; j++)
                    acc.MixUInt64((ushort)c.EnergyMods[j].Amount | ((ulong)c.EnergyMods[j].PackedFlags << 16));
                for (int j = 0; j < c.TempStarCosts.Length; j++)
                {
                    HotTempStarCost s = c.TempStarCosts[j];
                    acc.MixUInt64(s.Cost | (s.ClearsWhenTurnEnds ? 256UL : 0UL) | (s.ClearsWhenCardIsPlayed ? 512UL : 0UL));
                }
            }
        }
        if (RuptureEntries is { Length: > 0 } ruptures)
        {
            acc.MixUInt64((ulong)ruptures.Length);
            for (int i = 0; i < ruptures.Length; i++)
                acc.MixUInt64(ruptures[i].CardUid | ((ulong)(uint)ruptures[i].Strength << 32));
        }
        if (DampenEntries is { Length: > 0 } dampens)
        {
            acc.MixUInt64((ulong)dampens.Length);
            for (int i = 0; i < dampens.Length; i++)
                acc.MixUInt64(dampens[i].CardUid | ((ulong)dampens[i].OriginalUpgrade << 16) | ((ulong)(uint)dampens[i].CasterPowerIdx << 32));
        }
        if (MonsterLogs is { Length: > 0 } logs)
        {
            acc.MixUInt64((ulong)logs.Length);
            for (int i = 0; i < logs.Length; i++)
            {
                acc.MixUInt64(logs[i].EnemySlot | ((ulong)logs[i].MoveNodeIds.Length << 8));
                foreach (ushort move in logs[i].MoveNodeIds)
                    acc.MixUInt64(move);
            }
        }
    }

    public static bool StructuralEquals(HotColdSidecar? left, HotColdSidecar? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left == null || left.IsEmpty)
            return right == null || right.IsEmpty;
        if (right == null || right.IsEmpty)
            return false;
        return CardsEqual(left.DetachedCards, right.DetachedCards)
            && ArrayEquals(left.DecimalVars, right.DecimalVars)
            && CostOverflowEquals(left.CostOverflow, right.CostOverflow)
            && ArrayEquals(left.RuptureEntries, right.RuptureEntries)
            && ArrayEquals(left.DampenEntries, right.DampenEntries)
            && MonsterLogsEqual(left.MonsterLogs, right.MonsterLogs);
    }

    private static bool CardsEqual(HotCard[]? a, HotCard[]? b)
    {
        int lenA = a?.Length ?? 0, lenB = b?.Length ?? 0;
        if (lenA != lenB) return false;
        for (int i = 0; i < lenA; i++)
        {
            if (!HotStatePacker.AsReadOnlyBytes(in a![i]).SequenceEqual(HotStatePacker.AsReadOnlyBytes(in b![i])))
                return false;
        }
        return true;
    }

    private static bool ArrayEquals<T>(T[]? a, T[]? b)
    {
        int lenA = a?.Length ?? 0, lenB = b?.Length ?? 0;
        if (lenA != lenB) return false;
        if (lenA == 0) return true;
        EqualityComparer<T> comparer = EqualityComparer<T>.Default;
        for (int i = 0; i < lenA; i++)
        {
            if (!comparer.Equals(a![i], b![i]))
                return false;
        }
        return true;
    }

    private static bool CostOverflowEquals(HotCardCostOverflow[]? a, HotCardCostOverflow[]? b)
    {
        int lenA = a?.Length ?? 0, lenB = b?.Length ?? 0;
        if (lenA != lenB) return false;
        for (int i = 0; i < lenA; i++)
        {
            HotCardCostOverflow x = a![i], y = b![i];
            if (x.CardUid != y.CardUid || x.EnergyMods.Length != y.EnergyMods.Length || x.TempStarCosts.Length != y.TempStarCosts.Length)
                return false;
            for (int j = 0; j < x.EnergyMods.Length; j++)
            {
                if (x.EnergyMods[j].Amount != y.EnergyMods[j].Amount || x.EnergyMods[j].PackedFlags != y.EnergyMods[j].PackedFlags)
                    return false;
            }
            if (!x.TempStarCosts.AsSpan().SequenceEqual(y.TempStarCosts.AsSpan()))
                return false;
        }
        return true;
    }

    private static bool MonsterLogsEqual(HotMonsterStateLogOverflow[]? a, HotMonsterStateLogOverflow[]? b)
    {
        int lenA = a?.Length ?? 0, lenB = b?.Length ?? 0;
        if (lenA != lenB) return false;
        for (int i = 0; i < lenA; i++)
        {
            if (a![i].EnemySlot != b![i].EnemySlot || !a[i].MoveNodeIds.AsSpan().SequenceEqual(b[i].MoveNodeIds.AsSpan()))
                return false;
        }
        return true;
    }
}
