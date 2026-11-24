using System.Runtime.CompilerServices;
using CombatSolver.HotKernel;

namespace CombatSolver.HotSearch;

/// <summary>
/// Standalone search constants for <see cref="CombatSolver.HotSearch"/>.
/// Economic objective uses fixed-point centi-HP (<c>100 = 1.00 Persistent Post-Combat HP</c>) for exact integer determinism.
/// </summary>
internal static class HotSearchConstants
{
    public const int PotionMinimumHpSaved = PotionHpThresholds.DefaultStandard;
    public const int HpFixedScale = 100;
    public const int MaxHpGainWeightCenti = 200;       // 2.00 persistent HP per permanent +1 MaxHP (e.g. FruitJuice)
    public const int MaxHpLossWeightCenti = 100;       // 1.00 persistent HP per permanent -1 MaxHP
    public const int RunEndingPotionTieBreakCenti = 50;// 0.50 HP tie-break on Final Boss (potions expire after fight)
    public const int ReplacedPotionFloorCenti = 50;    // 0.50 HP floor after replacement credit (never waste for 0 gain)
    public const int DeathSavePremiumPercent = 900;
    public const int MaxBeamWidth = 640;
    public const int MaximumSearchMaxDegreeOfParallelism = 16;
    public const int MinimumTurnLayerExpandedNodes = 500;
    public const int SetupValueHorizonTurns = 16;
    public const int ProgressUiIntervalMilliseconds = 200;
    public const decimal AmbergrisMinimumHpSavedFraction = PotionHpThresholds.AmbergrisMaxHpFraction;
}

/// <summary>
/// Zero-allocation distilled search policy frozen once at root entry.
/// Single source of truth consumed by evaluator, potion policy, retention, incumbent pruning, and coordinator.
/// </summary>
internal readonly struct HotSearchPolicy
{
    public readonly SolverPotionPolicy ConfiguredPotionPolicy;
    public readonly SolverPotionPolicy EffectivePotionPolicy;
    public readonly PotionHpThresholds PotionThresholds;
    public readonly BossHpRelief EncounterBossHpRelief;
    public readonly BossHpRelief StrategicBossHpRelief;
    public readonly PostCombatRelicHealProfile PostCombatRelicHeal;
    public readonly SolverTheftPolicy? TheftPolicy;
    public readonly int StartTurnNumber;
    public readonly int InitialPlayerHp;
    public readonly int InitialPlayerMaxHp;
    public readonly int InitialEnemyCount;
    public readonly int InitialLiveDeckCount;
    public readonly int BattleHpLostSoFar;
    public readonly int BattlePotionsUsedSoFar;
    public readonly int AcceptableBattleHpLoss;
    public readonly int ReplacementHpCredit;
    public readonly int BaseReplacementCreditCenti;
    public readonly int MinimumRequiredPotionUses;
    public readonly int EarlyStopPotionUses;
    public readonly ushort ForcedPotionSlotMask;
    public readonly ushort DisabledPotionSlotMask;
    public readonly ushort AmbergrisSlotMask;
    public readonly ushort PaidOptionalSlotMask;
    public readonly ushort PaidHealPotionSlotMask;
    public readonly ushort PaidFruitJuiceSlotMask;
    public readonly ushort PaidEntropicBrewSlotMask;
    public readonly byte ForcedDirectiveCount;
    public readonly byte DisabledDirectiveCount;
    public readonly byte SearchablePotionCount;
    public readonly byte PaidOptionalPotionCount;
    public readonly byte ZeroCostOptionalPotionCount;
    public readonly byte RootPotionSlotCount;
    public readonly byte EmptyRootPotionSlots;
    public readonly int MinPaidOptionalRawHpCost;
    public readonly bool BeltFullAtRoot;
    public readonly bool HasSozu;
    public readonly bool HasWhiteBeastStatue;
    public readonly bool HasOnPotionUsedRelic;
    public readonly bool HasAlchemizeInDeck;
    public readonly bool HasPaidHealPotion;
    public readonly bool HasPaidFruitJuice;
    public readonly bool HasPaidEntropicBrew;
    public readonly bool HasRenewablePotionShapedRock;
    public readonly bool HasAnyHealOrUnknownGeneration;
    public readonly bool HasGrowthTargets;
    public readonly bool CanStopAtHpTarget;
    public readonly bool IncludeTurnSetup;
    public readonly bool IsActEndingBoss;
    public readonly bool IsBossEncounter;
    public readonly bool IsEliteEncounter;
    public readonly bool IsTheftEncounter;
    public readonly bool IgnoreLongTermRewards;
    public readonly GrowthValues EffectiveGrowthBudgets;
    public readonly int TranspositionEntryLimit;
    public readonly int MaxDegreeOfParallelism;
    public readonly int ForcedStrategicHpCost;
    public readonly byte ForcedAmbergrisCount;

    public HotSearchPolicy(
        CombatRootSnapshot root,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        HotRules rules,
        in CombatHotState rootState,
        short[] rootSlotStrategicCosts)
    {
        ConfiguredPotionPolicy = policy.PotionPolicy;
        EffectivePotionPolicy = policy.TheftPolicy == SolverTheftPolicy.PreserveResources
            ? SolverPotionPolicy.Smart
            : policy.PotionPolicy == SolverPotionPolicy.RequireAtLeastOne && battleDamage.PotionsUsedSoFar > 0
                ? SolverPotionPolicy.Smart
                : policy.PotionPolicy;
        PotionThresholds = policy.PotionStrategy.Thresholds;

        EncounterBossHpRelief = root.BossHpRelief;
        StrategicBossHpRelief = HotRouteQuality.ResolveStrategicHpRelief(
            root.BossHpRelief,
            policy.ActTransitionBossHpStrategy,
            policy.FinalBossHpStrategy);
        PostCombatRelicHeal = root.PostCombatRelicHeal.HasAnyHeal
            ? root.PostCombatRelicHeal
            : new PostCombatRelicHealProfile(
                rules.PostCombatRelicUnconditionalHeal,
                rules.PostCombatRelicWoundedHeal,
                rules.PostCombatRelicWoundedHpPercent);

        TheftPolicy = policy.TheftPolicy;
        StartTurnNumber = root.StartTurnNumber;
        InitialPlayerHp = root.InitialPlayerHp;
        InitialPlayerMaxHp = root.InitialPlayerMaxHp;
        InitialEnemyCount = rootState.RosterCount;
        InitialLiveDeckCount = rootState.HandCount + rootState.DrawCount + rootState.DiscardCount;
        BattleHpLostSoFar = battleDamage.HpLostSoFar;
        BattlePotionsUsedSoFar = battleDamage.PotionsUsedSoFar;
        AcceptableBattleHpLoss = policy.AcceptableBattleHpLoss;
        HasRenewablePotionShapedRock = root.HasRenewablePotionShapedRock || rules.HasRenewablePotionShapedRock;
        HasAnyHealOrUnknownGeneration = rules.HasAnyHealOrUnknownGeneration;
        HasGrowthTargets = policy.EffectiveHasGrowthTargets || policy.RelicTargets.Count > 0;
        CanStopAtHpTarget = policy.CanStopAtHpTarget && !HasGrowthTargets;
        IncludeTurnSetup = policy.IncludeTurnSetup;
        IsActEndingBoss = root.IsActEndingBoss;
        IsBossEncounter = rules.IsBossEncounter || root.IsActEndingBoss;
        IsEliteEncounter = rules.IsEliteEncounter;
        IsTheftEncounter = rules.IsTheftEncounter || policy.TheftPolicy != null;
        IgnoreLongTermRewards = policy.IgnoreLongTermRewards;
        EffectiveGrowthBudgets = policy.EffectiveGrowthBudgets;
        TranspositionEntryLimit = policy.TranspositionEntryLimit;
        MaxDegreeOfParallelism = Math.Clamp(policy.MaxDegreeOfParallelism, 1, HotSearchConstants.MaximumSearchMaxDegreeOfParallelism);

        HasSozu = rules.HasRelic(HotRelicId.Sozu);
        HasWhiteBeastStatue = rules.HasRelic(HotRelicId.WhiteBeastStatue);
        HasOnPotionUsedRelic = rules.HasRelic(HotRelicId.ReptileTrinket) || rules.HasRelic(HotRelicId.BeltBuckle);
        HasAlchemizeInDeck = root.PlayerCardIds.Contains("ALCHEMIZE");

        int slotCount = Math.Min((int)rootState.PotionSlotCount, HotLimits.MaxPotions);
        RootPotionSlotCount = (byte)slotCount;
        int emptySlots = 0;

        ushort forcedMask = 0;
        ushort disabledMask = 0;
        ushort ambergrisMask = 0;
        ushort paidOptionalMask = 0;
        ushort paidHealMask = 0;
        ushort paidFruitJuiceMask = 0;
        ushort paidEntropicMask = 0;
        byte forcedCount = 0;
        byte disabledCount = 0;
        byte searchableCount = 0;
        byte paidOptionalCount = 0;
        byte zeroCostOptionalCount = 0;
        int minPaidOptionalRawCost = int.MaxValue;
        bool hasPaidHeal = false;
        bool hasPaidFruitJuice = false;
        bool hasPaidEntropicBrew = false;
        int forcedCost = 0;
        byte forcedAmbergris = 0;

        for (int slot = 0; slot < slotCount; slot++)
        {
            ushort defId = rootState.Potions[slot];
            if (defId == 0 || defId >= rules.Potions.Length)
            {
                emptySlots++;
                continue;
            }

            HotPotionDef pDef = rules.Potions[defId];
            if (!HotPotionPolicy.IsDefCombatUsable(pDef))
                continue;

            searchableCount++;
            HotCustomPotionId pId = HotPotionPolicy.ResolvePotionId(pDef);
            bool isAmbergris = pId == HotCustomPotionId.Ambergris;
            if (isAmbergris)
                ambergrisMask |= (ushort)(1 << slot);

            short slotCost = slot < rootSlotStrategicCosts.Length && rootSlotStrategicCosts[slot] >= 0
                ? rootSlotStrategicCosts[slot]
                : HotPotionPolicy.ComputePotionDefCost(pDef, HasRenewablePotionShapedRock, InitialPlayerMaxHp, PotionThresholds);

            SolverPotionDirective directive = policy.PotionStrategy.Resolve(slot, pDef.Entry);
            if (directive == SolverPotionDirective.Force)
            {
                forcedMask |= (ushort)(1 << slot);
                forcedCount++;
                forcedCost += slotCost;
                if (isAmbergris)
                    forcedAmbergris++;
            }
            else if (directive == SolverPotionDirective.Disabled)
            {
                disabledMask |= (ushort)(1 << slot);
                disabledCount++;
            }
            else
            {
                // Optional Smart/Allow slot
                if (slotCost == 0)
                {
                    zeroCostOptionalCount++;
                }
                else
                {
                    ushort slotBit = (ushort)(1 << slot);
                    paidOptionalCount++;
                    paidOptionalMask |= slotBit;
                    if (slotCost < minPaidOptionalRawCost)
                        minPaidOptionalRawCost = slotCost;

                    if (pId is HotCustomPotionId.BloodPotion
                        or HotCustomPotionId.RegenPotion
                        or HotCustomPotionId.Ambergris
                        or HotCustomPotionId.FruitJuice)
                    {
                        hasPaidHeal = true;
                        paidHealMask |= slotBit;
                    }
                    else if (pDef.CanHealPlayer && pId != HotCustomPotionId.HeartOfIron)
                    {
                        hasPaidHeal = true;
                        paidHealMask |= slotBit;
                    }

                    if (pId == HotCustomPotionId.FruitJuice)
                    {
                        hasPaidFruitJuice = true;
                        paidFruitJuiceMask |= slotBit;
                    }
                    if (pId == HotCustomPotionId.EntropicBrew)
                    {
                        hasPaidEntropicBrew = true;
                        paidEntropicMask |= slotBit;
                    }
                }
            }
        }

        EmptyRootPotionSlots = (byte)emptySlots;
        BeltFullAtRoot = slotCount > 0 && emptySlots == 0;

        int replacementHp = 0;
        if (!HasSozu && EncounterBossHpRelief != BossHpRelief.RunEnding && BeltFullAtRoot)
        {
            PotionRewardOutlook outlook = root.PotionRewardOutlook;
            if (outlook.Enabled && outlook.Forecast == PotionRewardForecast.Drop)
            {
                replacementHp = HotPotionPolicy.ResolveEntryStrategicHpCost(
                    outlook.ForecastPotionId,
                    hasRenewablePotionShapedRock: false,
                    InitialPlayerMaxHp,
                    PotionThresholds);
            }
            else if (HasWhiteBeastStatue
                && (!outlook.Enabled || outlook.Forecast == PotionRewardForecast.Unknown))
            {
                // White Beast Statue guarantees a post-combat potion drop with zero RNG peeking.
                replacementHp = PotionHpThresholds.DefaultStandard;
            }
        }

        ReplacementHpCredit = replacementHp;
        BaseReplacementCreditCenti = replacementHp * HotSearchConstants.HpFixedScale;

        ForcedPotionSlotMask = forcedMask;
        DisabledPotionSlotMask = disabledMask;
        AmbergrisSlotMask = ambergrisMask;
        PaidOptionalSlotMask = paidOptionalMask;
        PaidHealPotionSlotMask = paidHealMask;
        PaidFruitJuiceSlotMask = paidFruitJuiceMask;
        PaidEntropicBrewSlotMask = paidEntropicMask;
        ForcedDirectiveCount = forcedCount;
        DisabledDirectiveCount = disabledCount;
        SearchablePotionCount = searchableCount;
        PaidOptionalPotionCount = paidOptionalCount;
        ZeroCostOptionalPotionCount = zeroCostOptionalCount;
        MinPaidOptionalRawHpCost = paidOptionalCount > 0 ? minPaidOptionalRawCost : 0;
        HasPaidHealPotion = hasPaidHeal;
        HasPaidFruitJuice = hasPaidFruitJuice;
        HasPaidEntropicBrew = hasPaidEntropicBrew;
        ForcedStrategicHpCost = forcedCost;
        ForcedAmbergrisCount = forcedAmbergris;
        MinimumRequiredPotionUses = Math.Max(
            (int)forcedCount,
            EffectivePotionPolicy == SolverPotionPolicy.RequireAtLeastOne ? 1 : 0);
        EarlyStopPotionUses = policy.MinimumRequiredPotionUses(battleDamage.PotionsUsedSoFar);
    }

    public bool HasForcedDirectives => ForcedDirectiveCount > 0;
    public bool HasHardPotionConstraint => ForcedDirectiveCount > 0 || EffectivePotionPolicy == SolverPotionPolicy.RequireAtLeastOne;
}

/// <summary>
/// Pure zero-allocation lexicographic route comparison key.
/// Primary 19-parameter shape stays compatible with <c>OfflineSearchHarness.QualityComparison</c>
/// while carrying exact fixed-point centi-HP values (<see cref="NetPersistentHpDeltaCenti"/>,
/// <see cref="NetPotionOpportunityCostCenti"/>) for internal unified economic ranking.
/// </summary>
internal readonly record struct HotRouteKey(
    bool Won,
    bool Survives,
    byte DeathSaveUseCount,
    int OutstandingStolen,
    bool ForfeitsGold,
    int StrategicHpDeficit,
    int OptionalPotionStrategicCost,
    int RawPotionStrategicCost,
    int GrowthHpCredit,
    int GrowthRewardCount,
    int CombatEndedTurn,
    int ProjectedBattleHpLost,
    int ProjectedBattlePotionCount,
    int ExplicitPotionCount,
    int PlayerHp,
    int EnemyHp,
    long Score,
    ushort PotionMask,
    byte AmbergrisCount)
{
    public bool HasCentiPrecision { get; init; }
    public int NetPersistentHpDeltaCenti { get; init; }
    public int NetPotionOpportunityCostCenti { get; init; }
    public byte OptionalPaidPotionCount { get; init; }
    public int EndPlayerMaxHp { get; init; }
    public byte AlchemizeCount { get; init; }
    public byte EmptyPotionSlots { get; init; }
    public byte RouteTurn { get; init; }
    public bool IsEventDefeat { get; init; }
}

/// <summary>
/// Standalone authoritative route quality evaluator and comparator built on ONE unified economic objective:
/// <c>V(route) = NetPersistentHpDeltaCenti - NetPotionOpportunityCostCenti</c> (in centi-HP).
/// </summary>
internal static class HotRouteQuality
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BossHpRelief ResolveStrategicHpRelief(
        BossHpRelief encounterHpRelief,
        BossHpStrategy actTransitionStrategy,
        BossHpStrategy finalBossStrategy)
        => encounterHpRelief switch
        {
            BossHpRelief.ActClearHeal when actTransitionStrategy == BossHpStrategy.MinimizeHpLoss
                => BossHpRelief.None,
            BossHpRelief.RunEnding when finalBossStrategy == BossHpStrategy.MinimizeHpLoss
                => BossHpRelief.None,
            _ => encounterHpRelief,
        };

    /// <summary>
    /// Returns the persistent post-combat HP weight per 1 raw HP in centi-HP:
    /// <c>100</c> (1.0x Normal/MinimizeHpLoss), <c>35</c> (0.35x ActClearHeal), <c>20</c> (0.20x RunEnding).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PersistentHpScale(BossHpRelief bossHpRelief)
        => bossHpRelief switch
        {
            BossHpRelief.ActClearHeal => 35,
            BossHpRelief.RunEnding => 20,
            _ => 100,
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RoundCentiToHp(int centi)
        => centi >= 0
            ? (centi + 50) / HotSearchConstants.HpFixedScale
            : -((-centi + 50) / HotSearchConstants.HpFixedScale);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DeathSavePremium(int deathSaveHpRestored)
        => Math.Max(0, deathSaveHpRestored)
            * HotSearchConstants.DeathSavePremiumPercent
            / 100;

    /// <summary>
    /// Single canonical persistent post-combat HP delta in centi-HP (<c>100 = 1.00 Persistent HP</c>).
    /// Normalizes ALL HP terms (damage blocked, healing, post-combat relic heal, MaxHP gain/loss, growth credit)
    /// into the exact same unit BEFORE combining. Higher is strictly better.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeNetPersistentHpDeltaCenti(
        int endPlayerHp,
        int postCombatMonotoneHeal,
        int initialPlayerHp,
        int endPlayerMaxHp,
        int initialPlayerMaxHp,
        int deathSaveHpRestored,
        int growthHpCredit,
        BossHpRelief bossHpRelief,
        bool ignoreLongTermRewards)
    {
        int hpScale = PersistentHpScale(bossHpRelief);
        int deathSaveHp = Math.Max(0, deathSaveHpRestored);
        int rawHpDelta = (endPlayerHp + postCombatMonotoneHeal - deathSaveHp) - initialPlayerHp;

        int maxHpGain = Math.Max(0, endPlayerMaxHp - initialPlayerMaxHp);
        int maxHpLoss = Math.Max(0, initialPlayerMaxHp - endPlayerMaxHp);

        int maxHpGainCreditCenti = (bossHpRelief != BossHpRelief.RunEnding && !ignoreLongTermRewards)
            ? maxHpGain * HotSearchConstants.MaxHpGainWeightCenti
            : 0;
        int maxHpLossPenaltyCenti = bossHpRelief != BossHpRelief.RunEnding
            ? maxHpLoss * HotSearchConstants.MaxHpLossWeightCenti
            : 0;
        int deathSavePenaltyCenti = DeathSavePremium(deathSaveHp) * HotSearchConstants.HpFixedScale;

        return rawHpDelta * hpScale
            + maxHpGainCreditCenti
            - maxHpLossPenaltyCenti
            + growthHpCredit * HotSearchConstants.HpFixedScale
            - deathSavePenaltyCenti;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RankedPostCombatRelicHeal(
        PostCombatRelicHealProfile profile,
        bool completeVictory,
        int finalHp,
        int finalMaxHp)
        => completeVictory && finalHp > 0
            ? profile.MonotoneHealFor(finalHp, finalMaxHp)
            : 0;

    public static HotRouteKey Capture(in HotSearchNode node, in HotSearchPolicy policy)
    {
        bool won = node.ActionCount > 0 && node.IsVictory;
        bool survives = !node.IsDefeat && node.PlayerHp > 0;

        int endPlayerMaxHp = Math.Max(1, policy.InitialPlayerMaxHp - node.PlayerMaxHpDelta);
        int postCombatHeal = RankedPostCombatRelicHeal(
            policy.PostCombatRelicHeal,
            won,
            node.PlayerHp,
            endPlayerMaxHp);

        int netHpDeltaCenti = ComputeNetPersistentHpDeltaCenti(
            endPlayerHp: node.PlayerHp,
            postCombatMonotoneHeal: postCombatHeal,
            initialPlayerHp: policy.InitialPlayerHp,
            endPlayerMaxHp: endPlayerMaxHp,
            initialPlayerMaxHp: policy.InitialPlayerMaxHp,
            deathSaveHpRestored: node.DeathSaveHpRestored,
            growthHpCredit: node.GrowthHpCredit,
            bossHpRelief: policy.StrategicBossHpRelief,
            ignoreLongTermRewards: policy.IgnoreLongTermRewards);

        int netPotionCostCenti = HotPotionPolicy.ComputeNetPotionOpportunityCostCenti(
            node.PotionStrategicCost,
            node.OptionalPaidPotionCount,
            node.AlchemizeCount,
            node.EmptyPotionSlots,
            in policy);

        int strategicDeficitWholeHp = RoundCentiToHp(-netHpDeltaCenti);
        int optionalPotionWholeHp = netPotionCostCenti > 0
            ? Math.Max(1, RoundCentiToHp(netPotionCostCenti))
            : 0;

        return new HotRouteKey(
            Won: won,
            Survives: survives,
            DeathSaveUseCount: node.DeathSaveUseCount,
            OutstandingStolen: node.OutstandingStolen,
            ForfeitsGold: (node.Flags & HotNodeFlags.ForfeitsGold) != 0,
            StrategicHpDeficit: strategicDeficitWholeHp,
            OptionalPotionStrategicCost: optionalPotionWholeHp,
            RawPotionStrategicCost: node.PotionStrategicCost,
            GrowthHpCredit: node.GrowthHpCredit,
            GrowthRewardCount: node.GrowthRewardCount,
            CombatEndedTurn: won ? node.Turn : int.MaxValue,
            ProjectedBattleHpLost: policy.BattleHpLostSoFar + node.HpLost,
            ProjectedBattlePotionCount: policy.BattlePotionsUsedSoFar + node.PotionCount,
            ExplicitPotionCount: node.ExplicitPotionCount,
            PlayerHp: node.PlayerHp,
            EnemyHp: node.EnemyHp,
            Score: node.Score,
            PotionMask: node.PotionMask,
            AmbergrisCount: node.AmbergrisCount)
        {
            HasCentiPrecision = true,
            NetPersistentHpDeltaCenti = netHpDeltaCenti,
            NetPotionOpportunityCostCenti = netPotionCostCenti,
            OptionalPaidPotionCount = node.OptionalPaidPotionCount,
            EndPlayerMaxHp = endPlayerMaxHp,
            AlchemizeCount = node.AlchemizeCount,
            EmptyPotionSlots = node.EmptyPotionSlots,
            RouteTurn = node.Turn,
            IsEventDefeat = node.IsEventDefeat,
        };
    }

    /// <summary>
    /// Captures a comparable <see cref="HotRouteKey"/> directly from an already-materialized <see cref="SolverResult"/>.
    /// </summary>
    public static HotRouteKey FromSolverResult(SolverResult result, in HotSearchPolicy policy)
    {
        bool isEventDefeat = result.BoundaryReason == SearchBoundaryReason.EventDefeat
            || result.Snapshot.BoundaryReason == SearchBoundaryReason.EventDefeat;

        if (result.ComparisonQuality is { } q)
        {
            return new HotRouteKey(
                Won: q.Won && !isEventDefeat,
                Survives: q.Survives,
                DeathSaveUseCount: (byte)Math.Clamp(q.DeathSaveUseCount, 0, byte.MaxValue),
                OutstandingStolen: q.OutstandingStolenResource,
                ForfeitsGold: q.ForfeitsEncounterGold,
                StrategicHpDeficit: q.StrategicHpDeficit,
                OptionalPotionStrategicCost: q.PotionStrategicCost,
                RawPotionStrategicCost: q.PotionStrategicCost,
                GrowthHpCredit: q.GrowthHpCredit,
                GrowthRewardCount: q.GrowthRewardCount,
                CombatEndedTurn: (q.Won && !isEventDefeat ? q.CombatEndedTurn : null) ?? int.MaxValue,
                ProjectedBattleHpLost: q.ProjectedBattleHpLost,
                ProjectedBattlePotionCount: q.ProjectedBattlePotionCount,
                ExplicitPotionCount: result.ExplicitPotionCount,
                PlayerHp: result.Snapshot.PlayerHp,
                EnemyHp: q.EnemyHp,
                Score: double.IsFinite(q.Score)
                    ? (long)Math.Clamp(q.Score, -1_000_000_000_000_000d, 1_000_000_000_000_000d)
                    : 0L,
                PotionMask: 0,
                AmbergrisCount: 0)
            {
                RouteTurn = (byte)Math.Clamp(result.DeathTurn ?? result.CombatEndedTurn ?? result.Snapshot.Turn, 0, byte.MaxValue),
                IsEventDefeat = isEventDefeat,
            };
        }

        SolverSnapshot snap = result.Snapshot;
        bool won = snap.AllEnemiesDead && !snap.PlayerDead && !result.OnlyDeathRoutesFound && !isEventDefeat;
        bool survives = !snap.PlayerDead && snap.PlayerHp > 0;
        int postCombatHeal = RankedPostCombatRelicHeal(
            policy.PostCombatRelicHeal,
            won,
            snap.PlayerHp,
            snap.PlayerMaxHp);
        int netHpDeltaCenti = ComputeNetPersistentHpDeltaCenti(
            endPlayerHp: snap.PlayerHp,
            postCombatMonotoneHeal: postCombatHeal,
            initialPlayerHp: policy.InitialPlayerHp,
            endPlayerMaxHp: snap.PlayerMaxHp,
            initialPlayerMaxHp: policy.InitialPlayerMaxHp,
            deathSaveHpRestored: snap.DeathSaveHpRestored,
            growthHpCredit: snap.StrategicHpCredit,
            bossHpRelief: policy.StrategicBossHpRelief,
            ignoreLongTermRewards: policy.IgnoreLongTermRewards);

        int potionCostCenti = Math.Max(0, result.PotionHpRequired) * HotSearchConstants.HpFixedScale;

        return new HotRouteKey(
            Won: won,
            Survives: survives,
            DeathSaveUseCount: (byte)Math.Clamp(snap.ProjectedDeathSaveUseCount, 0, byte.MaxValue),
            OutstandingStolen: snap.OutstandingStolenResource,
            ForfeitsGold: snap.ForfeitsEncounterGold,
            StrategicHpDeficit: RoundCentiToHp(-netHpDeltaCenti),
            OptionalPotionStrategicCost: result.PotionHpRequired,
            RawPotionStrategicCost: result.PotionHpRequired,
            GrowthHpCredit: snap.GrowthHpCredit,
            GrowthRewardCount: snap.GrowthRewards.Total,
            CombatEndedTurn: result.CombatEndedTurn ?? int.MaxValue,
            ProjectedBattleHpLost: result.ProjectedBattleHpLost,
            ProjectedBattlePotionCount: result.BattlePotionsUsedSoFar + result.PotionCount,
            ExplicitPotionCount: result.ExplicitPotionCount,
            PlayerHp: snap.PlayerHp,
            EnemyHp: snap.EnemyHp,
            Score: double.IsFinite(result.BestNode.Score)
                ? (long)Math.Clamp(result.BestNode.Score, -1_000_000_000_000_000d, 1_000_000_000_000_000d)
                : 0L,
            PotionMask: 0,
            AmbergrisCount: 0)
        {
            HasCentiPrecision = true,
            NetPersistentHpDeltaCenti = netHpDeltaCenti,
            NetPotionOpportunityCostCenti = potionCostCenti,
            EndPlayerMaxHp = snap.PlayerMaxHp,
            RouteTurn = (byte)Math.Clamp(result.DeathTurn ?? result.CombatEndedTurn ?? snap.Turn, 0, byte.MaxValue),
            IsEventDefeat = isEventDefeat,
        };
    }

    /// <summary>
    /// Compares two routes under the single unified lexicographic objective:
    /// <c>(Won, Survives, -DeathSaveCount, -TheftLoss, NetPersistentHpDelta - NetPotionOpportunityCost, NetPersistentHpDelta, Growth, RawHpLost, Potions, Turn, EnemyHp, Score)</c>
    /// (with defeat-only routes ranked by optional paid potion cost/count ASC when <paramref name="includeOptionalPotionCost"/> is enabled, then <c>(EnemyHp ASC, RouteTurn DESC, Score DESC)</c>).
    /// Returns negative if <paramref name="a"/> is better than <paramref name="b"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Compare(
        in HotRouteKey a,
        in HotRouteKey b,
        in HotSearchPolicy policy,
        bool includeOptionalPotionCost = true)
    {
        // 1. Victory > Non-victory
        int cmp = b.Won.CompareTo(a.Won);
        if (cmp != 0)
            return cmp;

        // 2. Survival > Death
        cmp = b.Survives.CompareTo(a.Survives);
        if (cmp != 0)
            return cmp;

        // 3. Preserve one-shot death saves (Lizard Tail / Fairy)
        cmp = a.DeathSaveUseCount.CompareTo(b.DeathSaveUseCount);
        if (cmp != 0)
            return cmp;

        // 4. Theft recovery (PreserveResources mode)
        if (policy.TheftPolicy == SolverTheftPolicy.PreserveResources)
        {
            cmp = a.OutstandingStolen.CompareTo(b.OutstandingStolen);
            if (cmp != 0)
                return cmp;
            cmp = a.ForfeitsGold.CompareTo(b.ForfeitsGold);
            if (cmp != 0)
                return cmp;
        }

        // Defeat-only routes: hopeless unwinnable fights must never burn optional paid potions just to shave enemy HP
        // (Phase B still freely uses potions whenever they achieve survival or victory in Steps 1-2 above).
        // Rank by lower paid-potion cost/count first, then lower remaining enemy HP, longer survival turn, and higher score.
        if (!a.Won && !a.Survives && !b.Won && !b.Survives)
        {
            if (includeOptionalPotionCost)
            {
                if (a.HasCentiPrecision && b.HasCentiPrecision)
                {
                    cmp = a.NetPotionOpportunityCostCenti.CompareTo(b.NetPotionOpportunityCostCenti);
                    if (cmp != 0)
                        return cmp;

                    cmp = a.OptionalPaidPotionCount.CompareTo(b.OptionalPaidPotionCount);
                    if (cmp != 0)
                        return cmp;
                }
                else
                {
                    cmp = a.OptionalPotionStrategicCost.CompareTo(b.OptionalPotionStrategicCost);
                    if (cmp != 0)
                        return cmp;
                }
            }

            cmp = a.EnemyHp.CompareTo(b.EnemyHp);
            if (cmp != 0)
                return cmp;

            cmp = b.RouteTurn.CompareTo(a.RouteTurn);
            if (cmp != 0)
                return cmp;

            return b.Score.CompareTo(a.Score);
        }

        // 5. Unified persistent-HP economic value V = NetPersistentHpDelta - NetPotionOpportunityCost
        if (a.HasCentiPrecision && b.HasCentiPrecision)
        {
            if (includeOptionalPotionCost)
            {
                long aV = (long)a.NetPersistentHpDeltaCenti - a.NetPotionOpportunityCostCenti;
                long bV = (long)b.NetPersistentHpDeltaCenti - b.NetPotionOpportunityCostCenti;
                cmp = bV.CompareTo(aV);
                if (cmp != 0)
                    return cmp;
            }

            // Tie-break on exact NetPersistentHpDelta (higher ending HP wins when HpGain == PotionCost!)
            cmp = b.NetPersistentHpDeltaCenti.CompareTo(a.NetPersistentHpDeltaCenti);
            if (cmp != 0)
                return cmp;
        }
        else
        {
            if (includeOptionalPotionCost)
            {
                long aCost = (long)a.StrategicHpDeficit + a.OptionalPotionStrategicCost;
                long bCost = (long)b.StrategicHpDeficit + b.OptionalPotionStrategicCost;
                cmp = aCost.CompareTo(bCost);
                if (cmp != 0)
                    return cmp;
            }

            cmp = a.StrategicHpDeficit.CompareTo(b.StrategicHpDeficit);
            if (cmp != 0)
                return cmp;
        }

        // 6. Growth credit & realized reward count
        cmp = b.GrowthHpCredit.CompareTo(a.GrowthHpCredit);
        if (cmp != 0)
            return cmp;

        cmp = b.GrowthRewardCount.CompareTo(a.GrowthRewardCount);
        if (cmp != 0)
            return cmp;

        // 7. Lower raw projected battle HP loss on equal persistent HP
        cmp = a.ProjectedBattleHpLost.CompareTo(b.ProjectedBattleHpLost);
        if (cmp != 0)
            return cmp;

        // 8. Fewer potions used (prevents wasting 0-cost/free potions for 0 gain)
        cmp = a.ProjectedBattlePotionCount.CompareTo(b.ProjectedBattlePotionCount);
        if (cmp != 0)
            return cmp;

        // 9. Faster victory turn
        cmp = a.CombatEndedTurn.CompareTo(b.CombatEndedTurn);
        if (cmp != 0)
            return cmp;

        // 10. Lower remaining enemy HP (for non-victory fallbacks)
        cmp = a.EnemyHp.CompareTo(b.EnemyHp);
        if (cmp != 0)
            return cmp;

        // 11. Higher heuristic score
        return b.Score.CompareTo(a.Score);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsBetter(
        in HotRouteKey candidate,
        in HotRouteKey current,
        in HotSearchPolicy policy,
        bool includeOptionalPotionCost = true)
        => Compare(in candidate, in current, in policy, includeOptionalPotionCost) < 0;

    /// <summary>
    /// Checks whether a complete victory meets the configured zero/acceptable HP loss stop target
    /// with no paid optional potions spent (0-cost Token/Rock/Generated potions are allowed in Stage 1).
    /// </summary>
    public static bool MeetsVictoryStopTarget(in HotRouteKey key, in HotSearchPolicy policy)
        => policy.CanStopAtHpTarget
            && key.Won
            && key.Survives
            && key.DeathSaveUseCount == 0
            && (policy.TheftPolicy != SolverTheftPolicy.PreserveResources
                || (key.OutstandingStolen == 0 && !key.ForfeitsGold))
            && (key.PotionMask & policy.ForcedPotionSlotMask) == policy.ForcedPotionSlotMask
            && key.OptionalPaidPotionCount == 0
            && (policy.EffectivePotionPolicy != SolverPotionPolicy.RequireAtLeastOne
                || key.ExplicitPotionCount >= policy.MinimumRequiredPotionUses)
            && key.ProjectedBattleHpLost <= policy.AcceptableBattleHpLoss;

    /// <summary>
    /// Converts a <see cref="HotRouteKey"/> into the UI/Coordinator <see cref="SolverInterimResult"/> contract.
    /// </summary>
    public static SolverInterimResult ToInterimResult(in HotRouteKey key, in HotSearchPolicy policy)
        => new(
            Won: key.Won,
            OutstandingStolenResource: key.OutstandingStolen,
            ProjectedBattleHpLost: key.ProjectedBattleHpLost,
            StrategicHpDeficit: key.StrategicHpDeficit,
            PotionStrategicCost: key.OptionalPotionStrategicCost,
            ProjectedBattlePotionCount: key.ProjectedBattlePotionCount,
            EnemyHp: key.EnemyHp,
            Score: key.Score,
            CombatEndedTurn: key.Won ? key.CombatEndedTurn : null)
        {
            TheftPolicy = policy.TheftPolicy,
            ForfeitsEncounterGold = key.ForfeitsGold,
            Survives = key.Survives,
            DeathSaveUseCount = key.DeathSaveUseCount,
            GrowthHpCredit = key.GrowthHpCredit,
            GrowthRewardCount = key.GrowthRewardCount,
        };
}
