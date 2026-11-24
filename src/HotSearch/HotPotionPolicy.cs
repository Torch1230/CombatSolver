using System.Runtime.CompilerServices;
using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Entities.Potions;

namespace CombatSolver.HotSearch;

/// <summary>
/// Single unified source of truth for potion slot legality, root vs combat-generated identity,
/// sound pre-expansion no-op pruning, transparent fixed-threshold opportunity cost (built-in catalog defaults
/// 0 / 9 / 14 / 18 and Ambergris = max(18, ceil(40% MaxHp)), overridable per potion ID; RunEnding near-zero;
/// WhiteBeastStatue / PredictPotionReward / Alchemize non-double-counted replacement credit),
/// hard constraints, and exact sound Stage 2 gating.
/// Zero contradictory post-hoc Smart/Ambergris vetoes!
/// </summary>
internal static class HotPotionPolicy
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static HotCustomPotionId ResolvePotionId(HotPotionDef def)
        => def.PotionId != HotCustomPotionId.None
            ? def.PotionId
            : HotPotionCatalog.FindByEntry(def.Entry);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDefCombatUsable(HotPotionDef def)
    {
        HotCustomPotionId id = ResolvePotionId(def);
        return id != HotCustomPotionId.None
            ? HotPotionCatalog.Get(id).IsCombatUsable
            : def.IsCombatUsable;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HpSavedWholeHp(in HotRouteKey baseline, in HotRouteKey candidate)
    {
        if (baseline.HasCentiPrecision && candidate.HasCentiPrecision)
        {
            int deltaCenti = candidate.NetPersistentHpDeltaCenti - baseline.NetPersistentHpDeltaCenti;
            return deltaCenti > 0 ? HotRouteQuality.RoundCentiToHp(deltaCenti) : 0;
        }
        return Math.Max(0, baseline.StrategicHpDeficit - candidate.StrategicHpDeficit);
    }

    /// <summary>
    /// Builds a root-frozen strategic HP cost array indexed by potion slot (<c>0..HotLimits.MaxPotions-1</c>).
    /// Empty slots are initialized to <c>-1</c> sentinel (never <c>0</c>) so mid-combat generated potions
    /// can never accidentally inherit stale root costs.
    /// </summary>
    public static short[] BuildRootSlotCosts(
        CombatRootSnapshot root,
        in CombatHotState rootState,
        HotRules rules,
        PotionHpThresholds? thresholds = null)
    {
        short[] costs = new short[HotLimits.MaxPotions];
        Array.Fill(costs, (short)-1);

        PotionHpThresholds effectiveThresholds = thresholds ?? PotionHpThresholds.Default;
        bool hasRenewableRock = root.HasRenewablePotionShapedRock || rules.HasRenewablePotionShapedRock;
        int initialMaxHp = Math.Max(1, rootState.Player.MaxHp > 0 ? rootState.Player.MaxHp : root.InitialPlayerMaxHp);
        int slotCount = Math.Min((int)rootState.PotionSlotCount, HotLimits.MaxPotions);

        for (int slot = 0; slot < slotCount; slot++)
        {
            ushort defId = rootState.Potions[slot];
            if (defId == 0 || defId >= rules.Potions.Length)
                continue;

            costs[slot] = ComputePotionDefCost(
                rules.Potions[defId],
                hasRenewableRock,
                initialMaxHp,
                effectiveThresholds);
        }

        return costs;
    }

    /// <summary>
    /// Legacy 2-argument catalog cost lookup retained for <c>HotPlanMaterializer</c> telemetry compatibility.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short ComputePotionDefCost(HotPotionDef def, bool hasRenewablePotionShapedRock)
        => ComputePotionDefCost(def, hasRenewablePotionShapedRock, initialPlayerMaxHp: 0, PotionHpThresholds.Default);

    /// <summary>
    /// Authoritative root potion HP opportunity cost: uses the player's per-potion-ID override if configured,
    /// otherwise falls back to built-in catalog defaults (<c>0</c> for Token / renewable <c>PotionShapedRock</c>,
    /// <c>max(18, ceil(40% MaxHp))</c> for <c>Ambergris</c>, and <c>9 / 14 / 18</c> from <see cref="HotPotionCatalog"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short ComputePotionDefCost(
        HotPotionDef def,
        bool hasRenewablePotionShapedRock,
        int initialPlayerMaxHp,
        PotionHpThresholds thresholds)
    {
        HotCustomPotionId id = ResolvePotionId(def);
        string? entry = !string.IsNullOrEmpty(def.Entry)
            ? def.Entry
            : (id != HotCustomPotionId.None ? HotPotionCatalog.Get(id).Entry : null);
        if (!string.IsNullOrEmpty(entry) && thresholds.TryGetOverride(entry, out int overrideHp))
            return (short)Math.Clamp(overrideHp, PotionHpThresholds.MinValue, PotionHpThresholds.MaxValue);

        if (def.Rarity == PotionRarity.Token
            || (hasRenewablePotionShapedRock && id == HotCustomPotionId.PotionShapedRock))
        {
            return 0;
        }

        if (id == HotCustomPotionId.Ambergris)
            return (short)Math.Clamp(PotionHpThresholds.DefaultAmbergrisCost(initialPlayerMaxHp), 0, short.MaxValue);

        short catalogBase = id != HotCustomPotionId.None
            ? HotPotionCatalog.Get(id).BaseStrategicHpCost
            : (def.StrategicHpCost > 0 ? def.StrategicHpCost : (short)PotionHpThresholds.DefaultStandard);

        return (short)Math.Clamp((int)catalogBase, 0, short.MaxValue);
    }

    public static int ResolveEntryStrategicHpCost(
        string? entry,
        bool hasRenewablePotionShapedRock,
        int initialPlayerMaxHp,
        PotionHpThresholds thresholds)
    {
        if (string.IsNullOrEmpty(entry))
            return PotionHpThresholds.DefaultStandard;
        if (thresholds.TryGetOverride(entry, out int overrideHp))
            return overrideHp;
        HotCustomPotionId id = HotPotionCatalog.FindByEntry(entry);
        if (id == HotCustomPotionId.None)
            return PotionHpThresholds.DefaultStandard;
        if (hasRenewablePotionShapedRock && id == HotCustomPotionId.PotionShapedRock)
            return 0;
        if (id == HotCustomPotionId.Ambergris)
            return PotionHpThresholds.DefaultAmbergrisCost(initialPlayerMaxHp);
        return HotPotionCatalog.Get(id).BaseStrategicHpCost;
    }

    /// <summary>
    /// Fast O(1) legality + cost classification for a manual potion use at <paramref name="slot"/>,
    /// distinguishing original root potions from mid-combat generated potions via
    /// <see cref="CombatHotState.CombatGeneratedPotionMask"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryClassifyPotionUseForPass(
        in CombatHotState state,
        int slot,
        ushort potionDefId,
        HotRules rules,
        short[] rootPotionCosts,
        in HotSearchPolicy policy,
        bool allowPaidOptionalPotions,
        bool allowZeroCostOptionalPotions,
        bool allowForcedPotions,
        int currentExplicitPotionCount,
        int maxPotionUses,
        out bool isGenerated,
        out bool isForced,
        out short optionalPaidCostDelta,
        out byte optionalPaidCountDelta,
        out ushort rootPotionSlotBit)
    {
        isGenerated = false;
        isForced = false;
        optionalPaidCostDelta = 0;
        optionalPaidCountDelta = 0;
        rootPotionSlotBit = 0;

        if ((uint)slot >= HotLimits.MaxPotions || (uint)slot >= (uint)state.PotionSlotCount)
            return false;
        if (maxPotionUses >= 0 && currentExplicitPotionCount >= maxPotionUses)
            return false;
        if (potionDefId == 0 || potionDefId >= rules.Potions.Length)
            return false;

        HotPotionDef pDef = rules.Potions[potionDefId];
        if (!IsDefCombatUsable(pDef))
            return false;

        ushort slotBit = (ushort)(1 << slot);
        isGenerated = (state.CombatGeneratedPotionMask & slotBit) != 0;

        if (isGenerated)
        {
            // Mid-combat generated potions (EntropicBrew / Alchemize / PetrifiedToad):
            // 0 opportunity cost, NEVER blocked by root DisabledPotionSlotMask, NEVER satisfies root ForcedPotionSlotMask!
            if (!allowZeroCostOptionalPotions)
                return false;
            return true;
        }

        // Original root potion at slot: respect root Disabled/Forced masks and root cost.
        if ((policy.DisabledPotionSlotMask & slotBit) != 0)
            return false;

        rootPotionSlotBit = slotBit;
        isForced = (policy.ForcedPotionSlotMask & slotBit) != 0;
        if (isForced)
        {
            // Forced root potion: 0 optional opportunity cost!
            return allowForcedPotions;
        }

        short slotCost = (uint)slot < (uint)rootPotionCosts.Length && rootPotionCosts[slot] >= 0
            ? rootPotionCosts[slot]
            : ComputePotionDefCost(pDef, policy.HasRenewablePotionShapedRock, policy.InitialPlayerMaxHp, policy.PotionThresholds);

        if (slotCost == 0)
        {
            return allowZeroCostOptionalPotions;
        }

        if (!allowPaidOptionalPotions)
            return false;

        optionalPaidCostDelta = slotCost;
        optionalPaidCountDelta = 1;
        return true;
    }

    /// <summary>
    /// Sound pre-expansion hygiene: returns <c>true</c> only when using <paramref name="def"/> in
    /// <paramref name="s"/> is provably a zero-effect no-op (exempting forced directives and
    /// <c>OnPotionUsed</c> relics such as <c>ReptileTrinket</c> / <c>BeltBuckle</c>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsProvableNoOpPotionUse(
        in CombatHotState s,
        HotPotionDef def,
        bool isForced,
        in HotSearchPolicy policy)
    {
        if (isForced || policy.HasOnPotionUsedRelic)
            return false;

        HotCustomPotionId id = ResolvePotionId(def);
        return id switch
        {
            HotCustomPotionId.Fortifier => s.Player.Block <= 0,
            HotCustomPotionId.BloodPotion => s.Player.CurrentHp >= s.Player.MaxHp,
            HotCustomPotionId.DropletOfPrecognition => s.DrawCount == 0,
            HotCustomPotionId.LiquidMemories => s.DiscardCount == 0,
            HotCustomPotionId.TouchOfInsanity => s.HandCount == 0,
            HotCustomPotionId.EntropicBrew => policy.HasSozu,
            _ => false,
        };
    }

    /// <summary>
    /// Single unified net opportunity cost in centi-HP (<c>100 = 1.00 Persistent HP</c>) shared identically
    /// by <see cref="HotRouteQuality"/>, <see cref="HotEvaluator"/>, <see cref="HotIncumbentBound"/>, and
    /// <see cref="HotSearchCoordinator"/>.
    /// Transparent fixed-threshold pricing (no hidden low-HP or Elite/Boss multipliers):
    /// - Forced / Token / PetrifiedToad Rock / combat-generated = <c>0</c>.
    /// - Paid optional root potions = user-configured Standard/Elevated/High (or Ambergris <c>max(High, 40% MaxHp)</c>).
    /// - Final Boss (<see cref="BossHpRelief.RunEnding"/>) = tiny <c>0.50 HP</c> tie-break per paid potion.
    /// - Full-belt replacement credit subtracted ONCE per freed slot (PredictPotionReward exact cost, or
    ///   WhiteBeastStatue deterministic Standard credit, plus Alchemize slot refill without double-counting).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeNetPotionOpportunityCostCenti(
        int optionalPaidRawHpCost,
        int optionalPaidCount,
        int alchemizeCount,
        int endEmptyPotionSlots,
        in HotSearchPolicy policy)
    {
        if (optionalPaidCount <= 0 || optionalPaidRawHpCost <= 0)
            return 0;

        if (policy.EncounterBossHpRelief == BossHpRelief.RunEnding)
            return optionalPaidCount * HotSearchConstants.RunEndingPotionTieBreakCenti;

        int rawCenti = optionalPaidRawHpCost * HotSearchConstants.HpFixedScale;

        int replacementCenti = 0;
        if (!policy.HasSozu)
        {
            bool dropLands = policy.BeltFullAtRoot
                && policy.BaseReplacementCreditCenti > 0
                && (endEmptyPotionSlots > 0 || alchemizeCount == 0);
            int remainingSlots = optionalPaidCount;
            if (dropLands)
            {
                replacementCenti += policy.BaseReplacementCreditCenti;
                remainingSlots--;
            }

            int alchemizeRefills = Math.Min(
                remainingSlots,
                Math.Max(0, alchemizeCount - policy.EmptyRootPotionSlots));
            if (alchemizeRefills > 0)
            {
                // Avoid double-crediting a refilled potion slot beyond real standard potion value
                // when Alchemize growth budget already credits it in NetPersistentHpDeltaCenti.
                int alchemizePerRefillHp = Math.Max(
                    0,
                    PotionHpThresholds.DefaultStandard - policy.EffectiveGrowthBudgets.Alchemize);
                if (alchemizePerRefillHp > 0)
                {
                    replacementCenti += alchemizeRefills
                        * (alchemizePerRefillHp * HotSearchConstants.HpFixedScale);
                }
            }
        }

        int afterReplacementCenti = rawCenti - replacementCenti;
        return afterReplacementCenti <= HotSearchConstants.ReplacedPotionFloorCenti
            ? HotSearchConstants.ReplacedPotionFloorCenti
            : afterReplacementCenti;
    }

    /// <summary>
    /// Enforces ONLY hard constraints (forced root potion slots, Disabled, RequireAtLeastOne).
    /// Smart potion trade-offs are governed purely by the unified economic objective
    /// <c>V(route) = NetPersistentHpDelta - NetPotionOpportunityCost</c> — zero post-hoc vetoes!
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool SatisfiesHardConstraints(
        in HotRouteKey candidate,
        in HotSearchPolicy policy)
    {
        if (policy.ForcedPotionSlotMask != 0
            && (candidate.PotionMask & policy.ForcedPotionSlotMask) != policy.ForcedPotionSlotMask)
        {
            return false;
        }

        return policy.EffectivePotionPolicy switch
        {
            SolverPotionPolicy.Disabled => candidate.OptionalPaidPotionCount == 0
                && candidate.ExplicitPotionCount <= policy.ForcedDirectiveCount,
            SolverPotionPolicy.RequireAtLeastOne => candidate.ExplicitPotionCount >= policy.MinimumRequiredPotionUses,
            _ => true,
        };
    }

    /// <summary>
    /// Exact sound Stage 2 trigger: returns <c>false</c> ONLY when no optional paid potion can possibly
    /// beat Stage 1's <paramref name="baseline"/> under the unified objective <c>V(route)</c>.
    /// </summary>
    public static bool ShouldRunSmartPotionPass(
        in HotRouteKey baseline,
        in HotSearchPolicy policy)
    {
        if (policy.EffectivePotionPolicy != SolverPotionPolicy.Smart)
            return false;
        if (policy.PaidOptionalPotionCount == 0)
            return false;

        if (!baseline.Won || !baseline.Survives || baseline.DeathSaveUseCount > 0)
            return true;
        if (policy.TheftPolicy == SolverTheftPolicy.PreserveResources
            && (baseline.OutstandingStolen > 0 || baseline.ForfeitsGold))
        {
            return true;
        }
        if (policy.ForcedPotionSlotMask != 0
            && (baseline.PotionMask & policy.ForcedPotionSlotMask) != policy.ForcedPotionSlotMask)
        {
            return true;
        }

        return CanAnyPaidPotionImproveOn(in baseline, in policy);
    }

    /// <summary>
    /// Sound upper-bound check: returns <c>true</c> if any allowed paid optional root potion could
    /// theoretically produce <c>PersistentHpGain &gt;= MinNetOptionalPotionCost</c> relative to
    /// <paramref name="key"/> (accounting for blocked damage, missing-HP healing headroom, unrealized
    /// Growth/Relic targets, Alchemize full-belt refills, and FruitJuice permanent +MaxHp gain).
    /// </summary>
    public static bool CanAnyPaidPotionImproveOn(
        in HotRouteKey key,
        in HotSearchPolicy policy)
    {
        ushort remainingPaidMask = (ushort)(policy.PaidOptionalSlotMask & ~key.PotionMask);
        if (policy.PaidOptionalPotionCount == 0 || remainingPaidMask == 0)
            return false;
        if (!key.Won || !key.Survives || key.DeathSaveUseCount > 0)
            return true;

        int hpScale = HotRouteQuality.PersistentHpScale(policy.StrategicBossHpRelief);

        int minAlchemizeRefill = policy.HasAlchemizeInDeck && !policy.HasSozu ? 1 : 0;
        int minNetCostCenti = ComputeNetPotionOpportunityCostCenti(
            policy.MinPaidOptionalRawHpCost,
            optionalPaidCount: 1,
            alchemizeCount: minAlchemizeRefill,
            endEmptyPotionSlots: 1,
            in policy);

        bool hasRemainingHeal = (remainingPaidMask & policy.PaidHealPotionSlotMask) != 0;
        bool hasRemainingFruitJuice = (remainingPaidMask & policy.PaidFruitJuiceSlotMask) != 0;
        bool hasRemainingEntropic = (remainingPaidMask & policy.PaidEntropicBrewSlotMask) != 0;

        int endMaxHp = key.EndPlayerMaxHp > 0 ? key.EndPlayerMaxHp : policy.InitialPlayerMaxHp;
        int endPostHealHp = key.PlayerHp + HotRouteQuality.RankedPostCombatRelicHeal(
            policy.PostCombatRelicHeal,
            key.Won,
            key.PlayerHp,
            endMaxHp);

        int maxRawHpGain;
        if (hasRemainingHeal || hasRemainingEntropic || policy.HasAnyHealOrUnknownGeneration)
        {
            maxRawHpGain = Math.Max(0, policy.InitialPlayerMaxHp - endPostHealHp);
        }
        else
        {
            int bestUnhealedEndHp = Math.Min(
                policy.InitialPlayerMaxHp,
                policy.InitialPlayerHp + policy.PostCombatRelicHeal.UnconditionalHeal);
            maxRawHpGain = Math.Max(0, bestUnhealedEndHp - endPostHealHp);
        }

        long maxPossibleGainCenti = (long)maxRawHpGain * hpScale;

        int maxHpLoss = Math.Max(0, policy.InitialPlayerMaxHp - endMaxHp);
        if (maxHpLoss > 0 && policy.StrategicBossHpRelief != BossHpRelief.RunEnding)
            maxPossibleGainCenti += (long)maxHpLoss * HotSearchConstants.MaxHpLossWeightCenti;

        if (hasRemainingFruitJuice || hasRemainingEntropic)
        {
            int maxHpGainBonus = (policy.StrategicBossHpRelief != BossHpRelief.RunEnding && !policy.IgnoreLongTermRewards)
                ? 5 * HotSearchConstants.MaxHpGainWeightCenti
                : 0;
            maxPossibleGainCenti += 5L * hpScale + maxHpGainBonus;
        }

        if (policy.HasAnyStrategyTargets && !key.AllStrategyTargetsSatisfied)
        {
            if (policy.HasPureGrowthTargets && !key.AllGrowthTargetsSatisfied && policy.EffectiveGrowthBudgets.IsEnabled)
            {
                if (!policy.HasBoundedGrowthTargets)
                    return true;

                int[] req = policy.RequiredGrowthCounts;
                GrowthValues b = policy.EffectiveGrowthBudgets;
                long maxGrowthCreditHp = (long)req[0] * b.HandOfGreed
                    + (long)req[1] * b.TheHunt
                    + (long)req[2] * b.Feed
                    + (long)req[3] * b.Royalties
                    + (long)req[4] * b.Alchemize
                    + (long)req[5] * b.GeneticAlgorithm
                    + (long)req[6] * b.TheScythe
                    + (long)req[7] * b.Goopy
                    + (long)req[8] * b.ForbiddenGrimoire
                    + (long)req[9] * b.MadScience
                    + (long)req[10] * b.ShowOff;
                long unrealizedGrowthHp = Math.Max(0L, maxGrowthCreditHp - key.GrowthHpCredit);
                maxPossibleGainCenti += unrealizedGrowthHp * HotSearchConstants.HpFixedScale;
            }

            if (policy.HasRelicTargets && !key.AllRelicTargetsSatisfied)
            {
                HotRelicTargetEntry[] targets = policy.RelicTargets;
                long unrealizedRelicHp = 0L;
                for (int i = 0; i < targets.Length; i++)
                {
                    HotRelicTargetEntry t = targets[i];
                    if ((key.RelicSatisfiedMask & (1U << (int)t.Id)) != 0)
                        continue;
                    if (t.Id == RelicCounterId.MeatOnTheBone)
                    {
                        unrealizedRelicHp += ActEndingBossPolicy.PersistentValueOfRecoveredHp(
                            policy.PostCombatRelicHeal.WoundedHeal,
                            policy.StrategicBossHpRelief);
                    }
                    else if (t.HpAllowance > 0)
                    {
                        unrealizedRelicHp += t.HpAllowance;
                    }
                }
                maxPossibleGainCenti += unrealizedRelicHp * HotSearchConstants.HpFixedScale;
            }
        }

        return maxPossibleGainCenti >= minNetCostCenti;
    }
}
