using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace CombatSolver.HotKernel;

/// <summary>
/// Local energy cost modifiers and temporary star costs for HotCard with 4 inline slots + whole-card COW cold overflow.
/// When HotCardFlags.HasCostOverflow is set, HotColdSidecar.CostOverflow holds ALL energy modifiers and ALL temporary
/// star costs for that card (inline slots stay zeroed), matching HotRules.GetCardLocalEnergyCost/GetCardCurrentStarCost.
/// Matches STS2 0.113.1 CardEnergyCost and CardModel._temporaryStarCosts semantics.
/// </summary>
internal static class HotCostModifiers
{
    private const ushort TransientOverflowUidBase = 0xF000;

    /// <summary>
    /// Computes CardEnergyCost.GetWithModifiers(CostModifiers.Local).
    /// </summary>
    public static int GetLocalEnergyCost(in HotCard card, HotColdSidecar? cold)
    {
        int cost = card.BaseEnergyCost;
        if (cost < 0 || (card.Flags & HotCardFlags.CostsX) != 0)
            return cost;

        if ((card.Flags & HotCardFlags.HasCostOverflow) != 0)
        {
            HotCardCostOverflow? overflow = FindOverflow(cold, card.CardUid);
            if (overflow != null)
            {
                HotEnergyCostMod[] mods = overflow.EnergyMods;
                for (int i = 0; i < mods.Length; i++)
                    cost = ApplySingleEnergyMod(cost, in mods[i]);
                return Math.Max(0, cost);
            }
        }

        int inlineCount = Math.Min((int)card.CostModCount, HotLimits.MaxEnergyCostMods);
        if (inlineCount > 0)
        {
            ReadOnlySpan<HotEnergyCostMod> inlineMods = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<HotEnergyCostMod4, HotEnergyCostMod>(ref Unsafe.AsRef(in card.EnergyCostMods)),
                inlineCount);
            for (int i = 0; i < inlineCount; i++)
                cost = ApplySingleEnergyMod(cost, in inlineMods[i]);
        }

        return Math.Max(0, cost);
    }

    /// <summary>
    /// Computes CardModel.CurrentStarCost (local effective star cost before global hooks / X-star resolution).
    /// </summary>
    public static int GetEffectiveStarCost(in HotCard card, HotColdSidecar? cold)
    {
        if ((card.Flags & HotCardFlags.HasCostOverflow) != 0)
        {
            HotCardCostOverflow? overflow = FindOverflow(cold, card.CardUid);
            if (overflow != null)
            {
                if (overflow.TempStarCosts.Length > 0)
                {
                    int tempCost = overflow.TempStarCosts[^1].Cost;
                    return tempCost == 0 && card.BaseStarCost < 0 ? card.BaseStarCost : tempCost;
                }
                return card.BaseStarCost;
            }
        }

        if ((card.TempStarCostPacked & 0x80) != 0)
        {
            int tempCost = card.TempStarCostPacked & 0x1F;
            return tempCost == 0 && card.BaseStarCost < 0 ? card.BaseStarCost : tempCost;
        }

        return card.BaseStarCost;
    }

    /// <summary>
    /// Adds a local energy cost modifier verbatim in the 4 inline slots;
    /// spills to HotColdSidecar.CostOverflow when &gt;4 modifiers accumulate,
    /// preserving exact Vanilla cost-state and UpgradeBy clamping semantics.
    /// <paramref name="expiration"/> uses <see cref="LocalCostModifierExpiration"/> flag values
    /// (0=EndOfCombat, 2=EndOfTurn, 4=WhenPlayed, 6=EndOfTurn|WhenPlayed).
    /// </summary>
    public static void AddEnergyModifier(
        ref HotCard card,
        ref HotColdSidecar? cold,
        short amount,
        bool isAbsolute,
        int expiration,
        bool isReduceOnly)
    {
        if (!isAbsolute && amount == 0)
            return;
        if (isAbsolute && amount == 0 && card.BaseEnergyCost < 0)
            return;

        AddEnergyModifierCore(ref card, ref cold, amount, isAbsolute, expiration, isReduceOnly, allowMerge: false);
    }

    /// <summary>
    /// Appends a local energy cost modifier verbatim without merging (for exact root import parity).
    /// </summary>
    public static void AddEnergyModifierExact(
        ref HotCard card,
        ref HotColdSidecar? cold,
        short amount,
        bool isAbsolute,
        int expiration,
        bool isReduceOnly)
    {
        AddEnergyModifierCore(ref card, ref cold, amount, isAbsolute, expiration, isReduceOnly, allowMerge: false);
    }

    /// <summary>
    /// Appends a temporary star cost entry (matching CardModel.AddTemporaryStarCost).
    /// </summary>
    public static void AddTempStarCost(
        ref HotCard card,
        ref HotColdSidecar? cold,
        int cost,
        bool clearsWhenTurnEnds,
        bool clearsWhenCardIsPlayed)
    {
        int clamped = Math.Clamp(cost, 0, byte.MaxValue);
        bool hasOverflow = (card.Flags & HotCardFlags.HasCostOverflow) != 0;

        if (!hasOverflow && (card.TempStarCostPacked & 0x80) == 0 && clamped <= 31)
        {
            card.TempStarCostPacked = PackTempStarCost((byte)clamped, clearsWhenTurnEnds, clearsWhenCardIsPlayed);
            return;
        }

        HotTempStarCost added = new((byte)clamped, clearsWhenTurnEnds, clearsWhenCardIsPlayed);

        if (hasOverflow)
        {
            HotCardCostOverflow? existing = FindOverflow(cold, card.CardUid);
            HotEnergyCostMod[] energyMods = existing?.EnergyMods ?? ExtractInlineEnergyMods(in card);
            HotTempStarCost[] oldStars = existing?.TempStarCosts ?? [];
            HotTempStarCost[] nextStars = new HotTempStarCost[oldStars.Length + 1];
            if (oldStars.Length > 0)
                oldStars.CopyTo(nextStars, 0);
            nextStars[oldStars.Length] = added;

            ClearInlineCostFields(ref card);
            EnsureOverflowCardUid(ref card, cold);
            card.Flags |= HotCardFlags.HasCostOverflow;
            UpsertOverflow(ref cold, card.CardUid, energyMods, nextStars);
            return;
        }

        // Transition from inline -> whole-card overflow.
        HotEnergyCostMod[] promotedEnergy = ExtractInlineEnergyMods(in card);
        HotTempStarCost[] promotedStars = (card.TempStarCostPacked & 0x80) != 0
            ? [UnpackTempStarCost(card.TempStarCostPacked), added]
            : [added];

        ClearInlineCostFields(ref card);
        EnsureOverflowCardUid(ref card, cold);
        card.Flags |= HotCardFlags.HasCostOverflow;
        UpsertOverflow(ref cold, card.CardUid, promotedEnergy, promotedStars);
    }

    /// <summary>
    /// Clones a card's cost-overflow entry (energy modifiers and temporary star costs) from
    /// <paramref name="sourceCardUid"/> to <paramref name="destCardUid"/> without array aliasing.
    /// Safe no-op when <paramref name="cold"/> is null or <paramref name="sourceCardUid"/> has no entry.
    /// </summary>
    internal static void CloneOverflow(ref HotColdSidecar? cold, ushort sourceCardUid, ushort destCardUid)
    {
        if (sourceCardUid == 0 || destCardUid == 0 || sourceCardUid == destCardUid)
            return;

        HotCardCostOverflow? src = FindOverflow(cold, sourceCardUid);
        if (src == null)
            return;

        HotEnergyCostMod[] energyCopy = src.EnergyMods.Length > 0
            ? (HotEnergyCostMod[])src.EnergyMods.Clone()
            : [];
        HotTempStarCost[] starCopy = src.TempStarCosts.Length > 0
            ? (HotTempStarCost[])src.TempStarCosts.Clone()
            : [];
        UpsertOverflow(ref cold, destCardUid, energyCopy, starCopy);
    }

    /// <summary>
    /// Expires all EndOfTurn energy modifiers and ClearsWhenTurnEnds temporary star costs across all combat piles.
    /// </summary>
    public static void ExpireTurnEnd(ref CombatHotState s, ref HotColdSidecar? cold)
    {
        int total = s.TotalCardCount;
        for (int i = 0; i < total; i++)
        {
            ExpireCardModifiers(
                ref s.Cards[i],
                ref cold,
                (int)LocalCostModifierExpiration.EndOfTurn,
                clearStarTurnEnd: true,
                clearStarPlayed: false);
        }
    }

    /// <summary>
    /// Expires all WhenPlayed energy modifiers and ClearsWhenCardIsPlayed temporary star costs on a single card.
    /// </summary>
    public static void ExpireAfterPlay(ref HotCard card, ref HotColdSidecar? cold)
    {
        ExpireCardModifiers(
            ref card,
            ref cold,
            (int)LocalCostModifierExpiration.WhenPlayed,
            clearStarTurnEnd: false,
            clearStarPlayed: true);
    }

    /// <summary>
    /// Mirrors STS2 0.113.1 <c>CardEnergyCost.UpgradeBy(int addend)</c>:
    /// updates <see cref="HotCard.BaseEnergyCost"/> to <c>Math.Max(0, card.BaseEnergyCost + addend)</c>
    /// and, when <c>newBase &lt; oldBase</c>, clamps every <see cref="HotEnergyCostMod.IsAbsolute"/>
    /// local modifier with <c>Amount &gt; newBase</c> down to <c>newBase</c> (inline slots and
    /// COW-cloned <see cref="HotColdSidecar.CostOverflow"/>).
    /// </summary>
    public static void UpgradeEnergyCostBy(ref HotCard card, ref HotColdSidecar? cold, int addend)
    {
        if ((card.Flags & HotCardFlags.CostsX) != 0 || card.BaseEnergyCost < 0 || addend == 0)
            return;

        int oldBase = card.BaseEnergyCost;
        int newBase = Math.Clamp(oldBase + addend, 0, sbyte.MaxValue);

        if (newBase < oldBase)
        {
            if ((card.Flags & HotCardFlags.HasCostOverflow) != 0)
            {
                HotCardCostOverflow? overflow = FindOverflow(cold, card.CardUid);
                if (overflow != null && overflow.EnergyMods.Length > 0)
                {
                    HotEnergyCostMod[] oldMods = overflow.EnergyMods;
                    bool anyChanged = false;
                    for (int i = 0; i < oldMods.Length; i++)
                    {
                        if (oldMods[i].IsAbsolute && oldMods[i].Amount > newBase)
                        {
                            anyChanged = true;
                            break;
                        }
                    }

                    if (anyChanged)
                    {
                        HotEnergyCostMod[] updated = (HotEnergyCostMod[])oldMods.Clone();
                        for (int i = 0; i < updated.Length; i++)
                        {
                            if (updated[i].IsAbsolute && updated[i].Amount > newBase)
                                updated[i].Amount = (short)newBase;
                        }
                        UpsertOverflow(ref cold, card.CardUid, updated, overflow.TempStarCosts);
                    }
                }
            }
            else
            {
                int inlineCount = Math.Min((int)card.CostModCount, HotLimits.MaxEnergyCostMods);
                for (int i = 0; i < inlineCount; i++)
                {
                    if (card.EnergyCostMods[i].IsAbsolute && card.EnergyCostMods[i].Amount > newBase)
                        card.EnergyCostMods[i].Amount = (short)newBase;
                }
            }
        }

        card.BaseEnergyCost = (sbyte)newBase;
    }

    public static int GetEnergyModifierCount(in HotCard card, HotColdSidecar? cold)
    {
        if ((card.Flags & HotCardFlags.HasCostOverflow) != 0)
        {
            HotCardCostOverflow? overflow = FindOverflow(cold, card.CardUid);
            if (overflow != null)
                return overflow.EnergyMods.Length;
        }
        return Math.Min((int)card.CostModCount, HotLimits.MaxEnergyCostMods);
    }

    public static HotEnergyCostMod GetEnergyModifierAt(in HotCard card, HotColdSidecar? cold, int index)
    {
        if ((card.Flags & HotCardFlags.HasCostOverflow) != 0)
        {
            HotCardCostOverflow? overflow = FindOverflow(cold, card.CardUid);
            if (overflow != null)
                return overflow.EnergyMods[index];
        }

        int inlineCount = Math.Min((int)card.CostModCount, HotLimits.MaxEnergyCostMods);
        if ((uint)index < (uint)inlineCount)
        {
            ReadOnlySpan<HotEnergyCostMod> inlineMods = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<HotEnergyCostMod4, HotEnergyCostMod>(ref Unsafe.AsRef(in card.EnergyCostMods)),
                inlineCount);
            return inlineMods[index];
        }

        throw new ArgumentOutOfRangeException(nameof(index), index, "Energy cost modifier index out of range.");
    }

    public static int GetTempStarCostCount(in HotCard card, HotColdSidecar? cold)
    {
        if ((card.Flags & HotCardFlags.HasCostOverflow) != 0)
        {
            HotCardCostOverflow? overflow = FindOverflow(cold, card.CardUid);
            if (overflow != null)
                return overflow.TempStarCosts.Length;
        }
        return (card.TempStarCostPacked & 0x80) != 0 ? 1 : 0;
    }

    public static HotTempStarCost GetTempStarCostAt(in HotCard card, HotColdSidecar? cold, int index)
    {
        if ((card.Flags & HotCardFlags.HasCostOverflow) != 0)
        {
            HotCardCostOverflow? overflow = FindOverflow(cold, card.CardUid);
            if (overflow != null)
                return overflow.TempStarCosts[index];
        }
        if (index == 0 && (card.TempStarCostPacked & 0x80) != 0)
            return UnpackTempStarCost(card.TempStarCostPacked);

        throw new ArgumentOutOfRangeException(nameof(index), index, "Temporary star cost index out of range.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ApplySingleEnergyMod(int currentCost, in HotEnergyCostMod mod)
    {
        if (mod.IsAbsolute)
            return mod.IsReduceOnly ? Math.Min(currentCost, mod.Amount) : mod.Amount;
        return mod.IsReduceOnly ? Math.Min(currentCost, currentCost + mod.Amount) : (currentCost + mod.Amount);
    }

    private static void AddEnergyModifierCore(
        ref HotCard card,
        ref HotColdSidecar? cold,
        short amount,
        bool isAbsolute,
        int expiration,
        bool isReduceOnly,
        bool allowMerge)
    {
        HotEnergyCostMod incoming = new()
        {
            Amount = amount,
            PackedFlags = HotEnergyCostMod.PackFlags(isAbsolute, expiration, isReduceOnly),
        };

        bool hasOverflow = (card.Flags & HotCardFlags.HasCostOverflow) != 0;

        if (!hasOverflow)
        {
            int inlineCount = Math.Min((int)card.CostModCount, HotLimits.MaxEnergyCostMods);

            if (allowMerge && inlineCount > 0)
            {
                ref HotEnergyCostMod prev = ref card.EnergyCostMods[inlineCount - 1];
                if (TryMergeConsecutive(ref prev, in incoming, out bool removePrev))
                {
                    if (removePrev)
                    {
                        inlineCount--;
                        card.EnergyCostMods[inlineCount] = default;
                        card.CostModCount = (byte)inlineCount;
                    }
                    return;
                }
            }

            if (inlineCount < HotLimits.MaxEnergyCostMods)
            {
                card.EnergyCostMods[inlineCount] = incoming;
                card.CostModCount = (byte)(inlineCount + 1);
                return;
            }

            // Transition from 4 inline slots -> whole-card overflow with 5 modifiers.
            HotEnergyCostMod[] promotedEnergy = new HotEnergyCostMod[inlineCount + 1];
            for (int i = 0; i < inlineCount; i++)
                promotedEnergy[i] = card.EnergyCostMods[i];
            promotedEnergy[inlineCount] = incoming;

            HotTempStarCost[] promotedStars = (card.TempStarCostPacked & 0x80) != 0
                ? [UnpackTempStarCost(card.TempStarCostPacked)]
                : [];

            ClearInlineCostFields(ref card);
            EnsureOverflowCardUid(ref card, cold);
            card.Flags |= HotCardFlags.HasCostOverflow;
            UpsertOverflow(ref cold, card.CardUid, promotedEnergy, promotedStars);
            return;
        }

        // Already in overflow mode.
        HotCardCostOverflow? existing = FindOverflow(cold, card.CardUid);
        HotEnergyCostMod[] oldMods = existing?.EnergyMods ?? ExtractInlineEnergyMods(in card);
        HotTempStarCost[] existingStars = existing?.TempStarCosts ?? [];

        if (allowMerge && oldMods.Length > 0)
        {
            HotEnergyCostMod prev = oldMods[^1];
            if (TryMergeConsecutive(ref prev, in incoming, out bool removePrev))
            {
                if (removePrev)
                {
                    HotEnergyCostMod[] shrunk = oldMods.Length == 1
                        ? []
                        : oldMods.AsSpan(0, oldMods.Length - 1).ToArray();
                    CommitOverflowOrDemote(ref card, ref cold, shrunk, existingStars);
                }
                else
                {
                    HotEnergyCostMod[] updated = (HotEnergyCostMod[])oldMods.Clone();
                    updated[^1] = prev;
                    UpsertOverflow(ref cold, card.CardUid, updated, existingStars);
                }
                return;
            }
        }

        HotEnergyCostMod[] appended = new HotEnergyCostMod[oldMods.Length + 1];
        if (oldMods.Length > 0)
            oldMods.CopyTo(appended, 0);
        appended[oldMods.Length] = incoming;

        ClearInlineCostFields(ref card);
        EnsureOverflowCardUid(ref card, cold);
        card.Flags |= HotCardFlags.HasCostOverflow;
        UpsertOverflow(ref cold, card.CardUid, appended, existingStars);
    }

    private static bool TryMergeConsecutive(ref HotEnergyCostMod prev, in HotEnergyCostMod next, out bool removePrev)
    {
        removePrev = false;
        if (prev.Expiration != next.Expiration)
            return false;

        // 1. Unconditional absolute modifier completely shadows any previous modifier with the same expiration.
        if (next.IsAbsolute && !next.IsReduceOnly)
        {
            prev = next;
            return true;
        }

        // 2. Two reduce-only absolute modifiers with the same expiration: min(min(c, a), b) == min(c, min(a, b)).
        if (prev.IsAbsolute && next.IsAbsolute && prev.IsReduceOnly && next.IsReduceOnly)
        {
            prev.Amount = Math.Min(prev.Amount, next.Amount);
            return true;
        }

        // 3. Two relative modifiers with the same expiration & matching reduce-only semantics.
        if (!prev.IsAbsolute && !next.IsAbsolute && prev.IsReduceOnly == next.IsReduceOnly)
        {
            if (!next.IsReduceOnly || (prev.Amount <= 0 && next.Amount <= 0))
            {
                int sum = prev.Amount + next.Amount;
                if (sum >= short.MinValue && sum <= short.MaxValue)
                {
                    if (sum == 0)
                    {
                        removePrev = true;
                        return true;
                    }
                    prev.Amount = (short)sum;
                    return true;
                }
            }
        }

        return false;
    }

    private static void ExpireCardModifiers(
        ref HotCard card,
        ref HotColdSidecar? cold,
        int expirationFlag,
        bool clearStarTurnEnd,
        bool clearStarPlayed)
    {
        bool hasOverflow = (card.Flags & HotCardFlags.HasCostOverflow) != 0;
        if (card.CostModCount == 0 && (card.TempStarCostPacked & 0x80) == 0 && !hasOverflow)
            return;

        if (!hasOverflow)
        {
            int inlineCount = Math.Min((int)card.CostModCount, HotLimits.MaxEnergyCostMods);
            if (inlineCount > 0)
            {
                int write = 0;
                for (int read = 0; read < inlineCount; read++)
                {
                    HotEnergyCostMod mod = card.EnergyCostMods[read];
                    if ((mod.PackedFlags & expirationFlag) == 0)
                    {
                        if (write != read)
                            card.EnergyCostMods[write] = mod;
                        write++;
                    }
                }
                for (int m = write; m < HotLimits.MaxEnergyCostMods; m++)
                    card.EnergyCostMods[m] = default;
                card.CostModCount = (byte)write;
            }

            if ((card.TempStarCostPacked & 0x80) != 0)
            {
                bool shouldClear = (clearStarTurnEnd && (card.TempStarCostPacked & 0x40) != 0)
                    || (clearStarPlayed && (card.TempStarCostPacked & 0x20) != 0);
                if (shouldClear)
                    card.TempStarCostPacked = 0;
            }
            return;
        }

        HotCardCostOverflow? overflow = FindOverflow(cold, card.CardUid);
        if (overflow == null)
        {
            card.Flags &= ~HotCardFlags.HasCostOverflow;
            return;
        }

        HotEnergyCostMod[] oldEnergy = overflow.EnergyMods;
        int keptEnergy = 0;
        for (int i = 0; i < oldEnergy.Length; i++)
        {
            if ((oldEnergy[i].PackedFlags & expirationFlag) == 0)
                keptEnergy++;
        }

        HotTempStarCost[] oldStars = overflow.TempStarCosts;
        int keptStars = 0;
        for (int i = 0; i < oldStars.Length; i++)
        {
            bool shouldClear = (clearStarTurnEnd && oldStars[i].ClearsWhenTurnEnds)
                || (clearStarPlayed && oldStars[i].ClearsWhenCardIsPlayed);
            if (!shouldClear)
                keptStars++;
        }

        if (keptEnergy == oldEnergy.Length && keptStars == oldStars.Length)
            return;

        HotEnergyCostMod[] nextEnergy;
        if (keptEnergy == 0)
        {
            nextEnergy = [];
        }
        else if (keptEnergy == oldEnergy.Length)
        {
            nextEnergy = oldEnergy;
        }
        else
        {
            nextEnergy = new HotEnergyCostMod[keptEnergy];
            int dst = 0;
            for (int i = 0; i < oldEnergy.Length; i++)
            {
                if ((oldEnergy[i].PackedFlags & expirationFlag) == 0)
                    nextEnergy[dst++] = oldEnergy[i];
            }
        }

        HotTempStarCost[] nextStars;
        if (keptStars == 0)
        {
            nextStars = [];
        }
        else if (keptStars == oldStars.Length)
        {
            nextStars = oldStars;
        }
        else
        {
            nextStars = new HotTempStarCost[keptStars];
            int dst = 0;
            for (int i = 0; i < oldStars.Length; i++)
            {
                bool shouldClear = (clearStarTurnEnd && oldStars[i].ClearsWhenTurnEnds)
                    || (clearStarPlayed && oldStars[i].ClearsWhenCardIsPlayed);
                if (!shouldClear)
                    nextStars[dst++] = oldStars[i];
            }
        }

        CommitOverflowOrDemote(ref card, ref cold, nextEnergy, nextStars);
    }

    private static void CommitOverflowOrDemote(
        ref HotCard card,
        ref HotColdSidecar? cold,
        HotEnergyCostMod[] energyMods,
        HotTempStarCost[] tempStarCosts)
    {
        bool fitsInline = energyMods.Length <= HotLimits.MaxEnergyCostMods
            && (tempStarCosts.Length == 0 || (tempStarCosts.Length == 1 && tempStarCosts[0].Cost <= 31));

        if (fitsInline)
        {
            for (int i = 0; i < energyMods.Length; i++)
                card.EnergyCostMods[i] = energyMods[i];
            for (int i = energyMods.Length; i < HotLimits.MaxEnergyCostMods; i++)
                card.EnergyCostMods[i] = default;
            card.CostModCount = (byte)energyMods.Length;
            card.TempStarCostPacked = tempStarCosts.Length == 1
                ? PackTempStarCost(tempStarCosts[0].Cost, tempStarCosts[0].ClearsWhenTurnEnds, tempStarCosts[0].ClearsWhenCardIsPlayed)
                : (byte)0;

            RemoveOverflowEntry(ref card, ref cold);
            return;
        }

        ClearInlineCostFields(ref card);
        UpsertOverflow(ref cold, card.CardUid, energyMods, tempStarCosts);
    }

    private static HotEnergyCostMod[] ExtractInlineEnergyMods(in HotCard card)
    {
        int count = Math.Min((int)card.CostModCount, HotLimits.MaxEnergyCostMods);
        if (count == 0)
            return [];
        HotEnergyCostMod[] result = new HotEnergyCostMod[count];
        ReadOnlySpan<HotEnergyCostMod> inlineMods = MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<HotEnergyCostMod4, HotEnergyCostMod>(ref Unsafe.AsRef(in card.EnergyCostMods)),
            count);
        inlineMods.CopyTo(result);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClearInlineCostFields(ref HotCard card)
    {
        card.CostModCount = 0;
        for (int i = 0; i < HotLimits.MaxEnergyCostMods; i++)
            card.EnergyCostMods[i] = default;
        card.TempStarCostPacked = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte PackTempStarCost(byte cost, bool clearsWhenTurnEnds, bool clearsWhenCardIsPlayed)
        => (byte)(0x80 | (clearsWhenTurnEnds ? 0x40 : 0) | (clearsWhenCardIsPlayed ? 0x20 : 0) | (cost & 0x1F));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HotTempStarCost UnpackTempStarCost(byte packed)
        => new((byte)(packed & 0x1F), (packed & 0x40) != 0, (packed & 0x20) != 0);

    private static HotCardCostOverflow? FindOverflow(HotColdSidecar? cold, ushort cardUid)
    {
        if (cardUid == 0 || cold?.CostOverflow is not { Length: > 0 } entries)
            return null;
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].CardUid == cardUid)
                return entries[i];
        }
        return null;
    }

    private static void EnsureOverflowCardUid(ref HotCard card, HotColdSidecar? cold)
    {
        if (card.CardUid != 0)
            return;
        card.CardUid = AllocateTransientOverflowUid(cold);
    }

    private static ushort AllocateTransientOverflowUid(HotColdSidecar? cold)
    {
        ushort candidate = TransientOverflowUidBase;
        while (candidate != 0)
        {
            if (!IsUidInCold(cold, candidate))
                return candidate;
            candidate++;
        }
        throw new HotCapacityExceededException("Exhausted transient CardUid range for cost overflow.");
    }

    private static bool IsUidInCold(HotColdSidecar? cold, ushort uid)
    {
        if (cold == null)
            return false;
        if (cold.CostOverflow is { Length: > 0 } co)
            for (int i = 0; i < co.Length; i++)
                if (co[i].CardUid == uid) return true;
        if (cold.DecimalVars is { Length: > 0 } decs)
            for (int i = 0; i < decs.Length; i++)
                if (decs[i].CardUid == uid) return true;
        if (cold.RuptureEntries is { Length: > 0 } ru)
            for (int i = 0; i < ru.Length; i++)
                if (ru[i].CardUid == uid) return true;
        if (cold.DampenEntries is { Length: > 0 } da)
            for (int i = 0; i < da.Length; i++)
                if (da[i].CardUid == uid) return true;
        if (cold.DetachedCards is { Length: > 0 } dc)
            for (int i = 0; i < dc.Length; i++)
                if (dc[i].CardUid == uid) return true;
        return false;
    }

    private static void UpsertOverflow(
        ref HotColdSidecar? cold,
        ushort cardUid,
        HotEnergyCostMod[] energyMods,
        HotTempStarCost[] tempStarCosts)
    {
        HotCardCostOverflow entry = new(cardUid, energyMods, tempStarCosts);
        HotCardCostOverflow[]? existing = cold?.CostOverflow;
        if (existing == null || existing.Length == 0)
        {
            cold = cold == null
                ? new HotColdSidecar(CostOverflow: [entry])
                : cold with { CostOverflow = [entry] };
            return;
        }

        for (int i = 0; i < existing.Length; i++)
        {
            if (existing[i].CardUid == cardUid)
            {
                HotCardCostOverflow[] replaced = (HotCardCostOverflow[])existing.Clone();
                replaced[i] = entry;
                cold = cold! with { CostOverflow = replaced };
                return;
            }
        }

        HotCardCostOverflow[] appended = new HotCardCostOverflow[existing.Length + 1];
        existing.CopyTo(appended, 0);
        appended[existing.Length] = entry;
        cold = cold! with { CostOverflow = appended };
    }

    private static void RemoveOverflowEntry(ref HotCard card, ref HotColdSidecar? cold)
    {
        card.Flags &= ~HotCardFlags.HasCostOverflow;
        HotCardCostOverflow[]? existing = cold?.CostOverflow;
        if (existing == null || existing.Length == 0)
            return;

        int idx = -1;
        for (int i = 0; i < existing.Length; i++)
        {
            if (existing[i].CardUid == card.CardUid)
            {
                idx = i;
                break;
            }
        }
        if (idx < 0)
            return;

        HotCardCostOverflow[]? next;
        if (existing.Length == 1)
        {
            next = null;
        }
        else
        {
            next = new HotCardCostOverflow[existing.Length - 1];
            if (idx > 0)
                Array.Copy(existing, 0, next, 0, idx);
            if (idx + 1 < existing.Length)
                Array.Copy(existing, idx + 1, next, idx, existing.Length - idx - 1);
        }

        cold = cold! with { CostOverflow = next };
        if (cold.IsEmpty)
            cold = null;
    }
}
