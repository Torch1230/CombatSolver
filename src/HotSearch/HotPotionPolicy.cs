using System.Runtime.CompilerServices;
using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Entities.Potions;

namespace CombatSolver.HotSearch;

/// <summary>
/// Single zero-allocation source of truth for potion slot permissions, strategic HP cost,
/// replacement credit, Ambergris threshold, and final Smart/Disabled/RequireAtLeastOne eligibility.
/// </summary>
internal static class HotPotionPolicy
{
    /// <summary>
    /// Builds a root-frozen strategic HP cost array indexed by potion slot (<c>0..HotLimits.MaxPotions-1</c>).
    /// Uses <see cref="HotPotionDef.StrategicHpCost"/> from <see cref="HotRules"/> with root snapshot fallback.
    /// </summary>
    public static short[] BuildRootSlotCosts(
        CombatRootSnapshot root,
        in CombatHotState rootState,
        HotRules rules)
    {
        short[] costs = new short[HotLimits.MaxPotions];
        int slotCount = Math.Min((int)rootState.PotionSlotCount, HotLimits.MaxPotions);

        for (int slot = 0; slot < slotCount; slot++)
        {
            ushort defId = rootState.Potions[slot];
            if (defId == 0 || defId >= rules.Potions.Length)
                continue;

            costs[slot] = ComputePotionDefCost(rules.Potions[defId], root.HasRenewablePotionShapedRock || rules.HasRenewablePotionShapedRock);
        }

        // Prefer exact root snapshot strategic costs when captured for the same slot.
        for (int i = 0; i < root.SearchablePotions.Count; i++)
        {
            SearchablePotionSlotSnapshot snap = root.SearchablePotions[i];
            if ((uint)snap.Slot < (uint)costs.Length)
                costs[snap.Slot] = (short)Math.Clamp(snap.StrategicHpCost, 0, short.MaxValue);
        }

        return costs;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short ComputePotionDefCost(HotPotionDef def, bool hasRenewablePotionShapedRock)
    {
        if (def.Rarity == PotionRarity.Token
            || (hasRenewablePotionShapedRock && def.PotionId == HotCustomPotionId.PotionShapedRock))
            return 0;
        if (def.StrategicHpCost > 0)
            return def.StrategicHpCost;
        return (short)SolverWeights.PotionMinimumHpSaved;
    }

    /// <summary>
    /// Fast O(1) bitmask check determining whether a manual potion use at <paramref name="slot"/>
    /// is permitted during the current search pass.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSlotAllowedForPass(
        int slot,
        ushort potionDefId,
        HotRules rules,
        in HotSearchPolicy policy,
        bool disableAllPotions,
        bool onlyForcedPotions,
        int currentExplicitPotionCount,
        int maxPotionUses)
    {
        if (disableAllPotions || (uint)slot >= HotLimits.MaxPotions)
            return false;
        if (maxPotionUses >= 0 && currentExplicitPotionCount >= maxPotionUses)
            return false;
        if (potionDefId == 0 || potionDefId >= rules.Potions.Length)
            return false;
        if (!rules.Potions[potionDefId].IsCombatUsable)
            return false;

        ushort bit = (ushort)(1 << slot);
        if ((policy.DisabledPotionSlotMask & bit) != 0)
            return false;
        if (onlyForcedPotions)
            return (policy.ForcedPotionSlotMask & bit) != 0;
        if (policy.EffectivePotionPolicy == SolverPotionPolicy.Disabled)
            return (policy.ForcedPotionSlotMask & bit) != 0;

        return true;
    }

    /// <summary>
    /// Computes the exact HP-equivalent opportunity cost of optional (non-forced) potions used by a route,
    /// incorporating replacement reward credit, Ambergris 40% MaxHP threshold, and Boss HP relief scaling.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeOptionalPotionRequiredHp(
        int totalPotionStrategicCost,
        int explicitPotionCount,
        int ambergrisCount,
        ushort usedPotionMask,
        in HotSearchPolicy policy)
    {
        int forcedUsedCount = 0;
        int forcedUsedCost = 0;
        int forcedUsedAmbergris = 0;

        if (policy.ForcedPotionSlotMask != 0 && (usedPotionMask & policy.ForcedPotionSlotMask) != 0)
        {
            if ((usedPotionMask & policy.ForcedPotionSlotMask) == policy.ForcedPotionSlotMask)
            {
                forcedUsedCount = policy.ForcedDirectiveCount;
                forcedUsedCost = policy.ForcedStrategicHpCost;
                forcedUsedAmbergris = policy.ForcedAmbergrisCount;
            }
            else
            {
                ushort overlap = (ushort)(usedPotionMask & policy.ForcedPotionSlotMask);
                forcedUsedCount = System.Numerics.BitOperations.PopCount(overlap);
                forcedUsedAmbergris = System.Numerics.BitOperations.PopCount((uint)(overlap & policy.AmbergrisSlotMask));
                forcedUsedCost = forcedUsedCount * SolverWeights.PotionMinimumHpSaved;
            }
        }

        int optionalExplicitCount = Math.Max(0, explicitPotionCount - forcedUsedCount);
        int optionalAmbergrisCount = Math.Max(0, ambergrisCount - forcedUsedAmbergris);
        int optionalRawCost = Math.Max(0, totalPotionStrategicCost - forcedUsedCost);

        if (optionalExplicitCount == 0 && optionalRawCost == 0)
            return 0;

        int afterCredit = PotionUsePolicy.ApplyReplacementCredit(
            optionalRawCost,
            optionalExplicitCount,
            policy.ReplacementHpCredit);

        int effectiveCost = PotionUsePolicy.EffectiveStrategicHpCost(
            afterCredit,
            optionalAmbergrisCount,
            policy.InitialPlayerMaxHp);

        return PotionUsePolicy.SmartRequiredHpSaved(effectiveCost, policy.StrategicBossHpRelief);
    }

    /// <summary>
    /// Checks whether a potion-allowed pass can possibly improve upon the Pass 1 baseline under Smart policy.
    /// </summary>
    public static bool ShouldRunSmartPotionPass(
        in HotRouteKey baseline,
        in HotSearchPolicy policy)
    {
        if (policy.EffectivePotionPolicy != SolverPotionPolicy.Smart)
            return false;
        if (policy.SearchablePotionCount == 0)
            return false;

        // Count non-disabled, non-forced potions available to explore.
        int availableOptional = policy.SearchablePotionCount - policy.ForcedDirectiveCount;
        if (availableOptional <= 0)
            return false;

        // If baseline failed to win, died, burned a death save, or missed theft recovery, potions can rescue.
        if (!baseline.Won || !baseline.Survives || baseline.DeathSaveUseCount > 0)
            return true;
        if (policy.TheftPolicy == SolverTheftPolicy.PreserveResources
            && (baseline.OutstandingStolen > 0 || baseline.ForfeitsGold))
        {
            return true;
        }

        // If baseline already satisfied the acceptable battle HP loss target, skip potion pass.
        if (HotRouteQuality.MeetsVictoryStopTarget(in baseline, in policy))
            return false;

        // Check minimum HP savings bar for spending at least 1 optional potion.
        int minBar = PotionUsePolicy.SmartRequiredHpSaved(
            PotionUsePolicy.ApplyReplacementCredit(
                SolverWeights.PotionMinimumHpSaved,
                1,
                policy.ReplacementHpCredit),
            policy.StrategicBossHpRelief);

        // If we have a 0-cost potion (e.g. renewable Potion-Shaped Rock), allow even if HP loss is small.
        if (policy.HasRenewablePotionShapedRock && baseline.StrategicHpDeficit > 0)
            return true;

        return baseline.StrategicHpDeficit >= minBar;
    }

    /// <summary>
    /// Verifies that a candidate route satisfies the configured potion policy (Disabled / RequireAtLeastOne / Smart),
    /// all forced potion slot directives, and the Ambergris minimum-HP-saved restriction.
    /// </summary>
    public static bool IsEligibleFinalRoute(
        in HotRouteKey candidate,
        in HotRouteKey? baseline,
        bool anyRouteWon,
        in HotSearchPolicy policy)
    {
        // All forced directives must be satisfied when enforced.
        if (policy.ForcedPotionSlotMask != 0
            && (candidate.PotionMask & policy.ForcedPotionSlotMask) != policy.ForcedPotionSlotMask)
        {
            return false;
        }

        // Ambergris restriction check (against 0-potion baseline when present).
        int optionalAmbergris = Math.Max(0, candidate.AmbergrisCount - policy.ForcedAmbergrisCount);
        if (optionalAmbergris > 0
            && policy.EffectivePotionPolicy == SolverPotionPolicy.Smart
            && baseline is { } bAmbergris)
        {
            if (!bAmbergris.Won && candidate.Won)
            {
                // Winning where baseline could not win justifies Ambergris.
            }
            else if (!PotionUsePolicy.MeetsAmbergrisRestriction(
                hasPotionFreeBaseline: true,
                ambergrisCount: optionalAmbergris,
                strategicHpCost: Math.Max(0, candidate.RawPotionStrategicCost - policy.ForcedStrategicHpCost),
                maximumHp: policy.InitialPlayerMaxHp,
                potionFreePlayerHp: bAmbergris.PlayerHp,
                potionRoutePlayerHp: candidate.PlayerHp))
            {
                return false;
            }
        }

        switch (policy.EffectivePotionPolicy)
        {
            case SolverPotionPolicy.Disabled:
                return candidate.ExplicitPotionCount <= policy.ForcedDirectiveCount;

            case SolverPotionPolicy.RequireAtLeastOne:
            {
                if (candidate.ExplicitPotionCount < policy.MinimumRequiredPotionUses)
                    return false;
                if (anyRouteWon && !candidate.Won)
                    return false;
                return true;
            }

            case SolverPotionPolicy.Smart:
            {
                if (candidate.ExplicitPotionCount <= policy.ForcedDirectiveCount)
                    return true;
                if (baseline is not { } b)
                    return true;
                // Winning, surviving, preserving a death save, or recovering stolen loot always justifies potions.
                if (candidate.Won && !b.Won)
                    return true;
                if (candidate.Survives && !b.Survives)
                    return true;
                if (candidate.DeathSaveUseCount < b.DeathSaveUseCount)
                    return true;
                if (policy.TheftPolicy == SolverTheftPolicy.PreserveResources
                    && (candidate.OutstandingStolen < b.OutstandingStolen
                        || (!candidate.ForfeitsGold && b.ForfeitsGold)))
                {
                    return true;
                }

                // Otherwise the HP saved must meet or exceed the optional potion strategic cost bar.
                int hpSaved = PotionUsePolicy.HpSaved(b.StrategicHpDeficit, candidate.StrategicHpDeficit);
                return hpSaved >= candidate.OptionalPotionStrategicCost
                    && (candidate.OptionalPotionStrategicCost > 0 || hpSaved > 0);
            }

            default:
                return true;
        }
    }
}
