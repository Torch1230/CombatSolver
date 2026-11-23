using System.Runtime.CompilerServices;
using CombatSolver.HotKernel;

namespace CombatSolver.HotSearch;

/// <summary>
/// Standalone search constants for <see cref="CombatSolver.HotSearch"/> so Phase 4 can delete legacy
/// <c>SolverWeights.cs</c> without breaking any HotSearch compile boundary.
/// </summary>
internal static class HotSearchConstants
{
    public const int PotionMinimumHpSaved = 9;
    public const int DeathSavePremiumPercent = 900;
    public const int MaximumSearchMaxDegreeOfParallelism = 16;
    public const int MinimumTurnLayerExpandedNodes = 500;
    public const int SetupValueHorizonTurns = 16;
    public const int ProgressUiIntervalMilliseconds = 200;
    public const decimal AmbergrisMinimumHpSavedFraction = 0.40m;
}

/// <summary>
/// Zero-allocation distilled search policy frozen once at root entry.
/// Single source of truth consumed by evaluator, potion policy, retention, incumbent pruning, and coordinator.
/// </summary>
internal readonly struct HotSearchPolicy
{
    public readonly SolverPotionPolicy ConfiguredPotionPolicy;
    public readonly SolverPotionPolicy EffectivePotionPolicy;
    public readonly BossHpRelief EncounterBossHpRelief;
    public readonly BossHpRelief StrategicBossHpRelief;
    public readonly PostCombatRelicHealProfile PostCombatRelicHeal;
    public readonly SolverTheftPolicy? TheftPolicy;
    public readonly int StartTurnNumber;
    public readonly int InitialPlayerHp;
    public readonly int InitialPlayerMaxHp;
    public readonly int InitialEnemyCount;
    public readonly int BattleHpLostSoFar;
    public readonly int BattlePotionsUsedSoFar;
    public readonly int AcceptableBattleHpLoss;
    public readonly int ReplacementHpCredit;
    public readonly int MinimumRequiredPotionUses;
    public readonly int EarlyStopPotionUses;
    public readonly ushort ForcedPotionSlotMask;
    public readonly ushort DisabledPotionSlotMask;
    public readonly ushort AmbergrisSlotMask;
    public readonly byte ForcedDirectiveCount;
    public readonly byte SearchablePotionCount;
    public readonly bool HasRenewablePotionShapedRock;
    public readonly bool HasAnyHealOrUnknownGeneration;
    public readonly bool HasGrowthTargets;
    public readonly bool CanStopAtHpTarget;
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
        BattleHpLostSoFar = battleDamage.HpLostSoFar;
        BattlePotionsUsedSoFar = battleDamage.PotionsUsedSoFar;
        AcceptableBattleHpLoss = policy.AcceptableBattleHpLoss;
        ReplacementHpCredit = root.PotionRewardOutlook.ReplacementHpCredit;
        HasRenewablePotionShapedRock = root.HasRenewablePotionShapedRock || rules.HasRenewablePotionShapedRock;
        HasAnyHealOrUnknownGeneration = rules.HasAnyHealOrUnknownGeneration;
        HasGrowthTargets = policy.EffectiveHasGrowthTargets || policy.RelicTargets.Count > 0;
        CanStopAtHpTarget = policy.CanStopAtHpTarget && !HasGrowthTargets;
        IsActEndingBoss = root.IsActEndingBoss;
        IsBossEncounter = rules.IsBossEncounter || root.IsActEndingBoss;
        IsEliteEncounter = rules.IsEliteEncounter;
        IsTheftEncounter = rules.IsTheftEncounter || policy.TheftPolicy != null;
        IgnoreLongTermRewards = policy.IgnoreLongTermRewards;
        EffectiveGrowthBudgets = policy.EffectiveGrowthBudgets;
        TranspositionEntryLimit = policy.TranspositionEntryLimit;
        MaxDegreeOfParallelism = Math.Clamp(policy.MaxDegreeOfParallelism, 1, HotSearchConstants.MaximumSearchMaxDegreeOfParallelism);

        ushort forcedMask = 0;
        ushort disabledMask = 0;
        ushort ambergrisMask = 0;
        byte forcedCount = 0;
        byte searchableCount = 0;
        int forcedCost = 0;
        byte forcedAmbergris = 0;

        int slotCount = Math.Min((int)rootState.PotionSlotCount, HotLimits.MaxPotions);
        for (int slot = 0; slot < slotCount; slot++)
        {
            ushort defId = rootState.Potions[slot];
            if (defId == 0 || defId >= rules.Potions.Length)
                continue;

            HotPotionDef pDef = rules.Potions[defId];
            if (!pDef.IsCombatUsable)
                continue;

            searchableCount++;
            bool isAmbergris = pDef.PotionId == HotCustomPotionId.Ambergris
                || string.Equals(pDef.Entry, "AMBERGRIS", StringComparison.Ordinal);
            if (isAmbergris)
                ambergrisMask |= (ushort)(1 << slot);

            SolverPotionDirective directive = policy.PotionStrategy.Resolve(slot, pDef.Entry);
            if (directive == SolverPotionDirective.Force)
            {
                forcedMask |= (ushort)(1 << slot);
                forcedCount++;
                forcedCost += slot < rootSlotStrategicCosts.Length ? rootSlotStrategicCosts[slot] : pDef.StrategicHpCost;
                if (isAmbergris)
                    forcedAmbergris++;
            }
            else if (directive == SolverPotionDirective.Disabled)
            {
                disabledMask |= (ushort)(1 << slot);
            }
        }

        ForcedPotionSlotMask = forcedMask;
        DisabledPotionSlotMask = disabledMask;
        AmbergrisSlotMask = ambergrisMask;
        ForcedDirectiveCount = forcedCount;
        SearchablePotionCount = searchableCount;
        ForcedStrategicHpCost = forcedCost;
        ForcedAmbergrisCount = forcedAmbergris;
        MinimumRequiredPotionUses = Math.Max(
            (int)forcedCount,
            EffectivePotionPolicy == SolverPotionPolicy.RequireAtLeastOne ? 1 : 0);
        EarlyStopPotionUses = policy.MinimumRequiredPotionUses(battleDamage.PotionsUsedSoFar);
    }

    public bool HasForcedDirectives => ForcedDirectiveCount > 0;
}

/// <summary>
/// Pure zero-allocation lexicographic route comparison key.
/// Single source of truth unifying primary quality, interim promotion, and Smart potion comparisons.
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
    byte AmbergrisCount);

/// <summary>
/// Standalone authoritative route quality evaluator and comparator.
/// Zero dependencies on legacy <c>ActEndingBossPolicy</c>, <c>RouteQualityPolicy</c>, or <c>CombatSearchCoordinator</c>.
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RawHpRequiredForPersistentValue(
        int persistentHpValue,
        BossHpRelief bossHpRelief)
    {
        if (persistentHpValue <= 0)
            return 0;
        return bossHpRelief switch
        {
            BossHpRelief.ActClearHeal => persistentHpValue * 5,
            BossHpRelief.RunEnding => int.MaxValue / 4,
            _ => persistentHpValue,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PersistentValueOfRecoveredHp(int recoveredHp, BossHpRelief bossHpRelief)
        => recoveredHp <= 0
            ? 0
            : recoveredHp / RawHpRequiredForPersistentValue(1, bossHpRelief);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DeathSavePremium(int deathSaveHpRestored)
        => Math.Max(0, deathSaveHpRestored)
            * HotSearchConstants.DeathSavePremiumPercent
            / 100;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int StrategicHpDeficit(
        int cumulativeHpLost,
        int maxHpDeficit,
        int recoveredHp,
        BossHpRelief bossHpRelief,
        int deathSaveHpRestored = 0)
        => cumulativeHpLost
            + maxHpDeficit
            - PersistentValueOfRecoveredHp(recoveredHp - deathSaveHpRestored, bossHpRelief)
            + DeathSavePremium(deathSaveHpRestored);

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

        int playerMaxHp = Math.Max(1, policy.InitialPlayerMaxHp - node.PlayerMaxHpDelta);
        int postCombatHeal = RankedPostCombatRelicHeal(
            policy.PostCombatRelicHeal,
            won,
            node.PlayerHp,
            playerMaxHp);

        int strategicDeficit = StrategicHpDeficit(
            node.HpLost,
            Math.Max(0, (int)node.PlayerMaxHpDelta),
            node.HpRecovered + postCombatHeal,
            policy.StrategicBossHpRelief,
            node.DeathSaveHpRestored) - node.GrowthHpCredit;

        int optionalPotionRequired = HotPotionPolicy.ComputeOptionalPotionRequiredHp(
            node.PotionStrategicCost,
            node.ExplicitPotionCount,
            node.AmbergrisCount,
            node.PotionMask,
            in policy);

        return new HotRouteKey(
            Won: won,
            Survives: survives,
            DeathSaveUseCount: node.DeathSaveUseCount,
            OutstandingStolen: node.OutstandingStolen,
            ForfeitsGold: (node.Flags & HotNodeFlags.ForfeitsGold) != 0,
            StrategicHpDeficit: strategicDeficit,
            OptionalPotionStrategicCost: optionalPotionRequired,
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
            AmbergrisCount: node.AmbergrisCount);
    }

    /// <summary>
    /// Captures a comparable <see cref="HotRouteKey"/> directly from an already-materialized <see cref="SolverResult"/>
    /// (used for witness comparison without calling legacy <c>CombatSearchCoordinator.CapturePortfolioQuality</c>).
    /// </summary>
    public static HotRouteKey FromSolverResult(SolverResult result, in HotSearchPolicy policy)
    {
        if (result.ComparisonQuality is { } q)
        {
            return new HotRouteKey(
                Won: q.Won,
                Survives: q.Survives,
                DeathSaveUseCount: (byte)Math.Clamp(q.DeathSaveUseCount, 0, byte.MaxValue),
                OutstandingStolen: q.OutstandingStolenResource,
                ForfeitsGold: q.ForfeitsEncounterGold,
                StrategicHpDeficit: q.StrategicHpDeficit,
                OptionalPotionStrategicCost: q.PotionStrategicCost,
                RawPotionStrategicCost: q.PotionStrategicCost,
                GrowthHpCredit: q.GrowthHpCredit,
                GrowthRewardCount: q.GrowthRewardCount,
                CombatEndedTurn: q.CombatEndedTurn ?? int.MaxValue,
                ProjectedBattleHpLost: q.ProjectedBattleHpLost,
                ProjectedBattlePotionCount: q.ProjectedBattlePotionCount,
                ExplicitPotionCount: result.ExplicitPotionCount,
                PlayerHp: result.Snapshot.PlayerHp,
                EnemyHp: q.EnemyHp,
                Score: (long)Math.Clamp(q.Score, long.MinValue, long.MaxValue),
                PotionMask: 0,
                AmbergrisCount: 0);
        }

        SolverSnapshot snap = result.Snapshot;
        bool won = snap.AllEnemiesDead && !snap.PlayerDead && !result.OnlyDeathRoutesFound;
        bool survives = !snap.PlayerDead && snap.PlayerHp > 0;
        int postCombatHeal = RankedPostCombatRelicHeal(
            policy.PostCombatRelicHeal,
            won,
            snap.PlayerHp,
            snap.PlayerMaxHp);
        int deficit = StrategicHpDeficit(
            snap.CumulativePlayerHpLost,
            Math.Max(0, policy.InitialPlayerMaxHp - snap.PlayerMaxHp),
            snap.RecoveredPlayerHp + postCombatHeal,
            policy.StrategicBossHpRelief,
            snap.DeathSaveHpRestored) - snap.StrategicHpCredit;

        return new HotRouteKey(
            Won: won,
            Survives: survives,
            DeathSaveUseCount: (byte)Math.Clamp(snap.ProjectedDeathSaveUseCount, 0, byte.MaxValue),
            OutstandingStolen: snap.OutstandingStolenResource,
            ForfeitsGold: snap.ForfeitsEncounterGold,
            StrategicHpDeficit: deficit,
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
            Score: (long)Math.Clamp(result.BestNode.Score, long.MinValue, long.MaxValue),
            PotionMask: 0,
            AmbergrisCount: 0);
    }

    /// <summary>
    /// Compares two routes under the unified lexicographic policy.
    /// Returns negative if <paramref name="a"/> is better than <paramref name="b"/>,
    /// positive if <paramref name="b"/> is better, 0 if equal.
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

        // 5. Strategic HP deficit (+ optional potion opportunity cost under Smart/PotionPolicy)
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

        // 6. Growth credit & realized reward count
        cmp = b.GrowthHpCredit.CompareTo(a.GrowthHpCredit);
        if (cmp != 0)
            return cmp;

        cmp = b.GrowthRewardCount.CompareTo(a.GrowthRewardCount);
        if (cmp != 0)
            return cmp;

        // 7. Faster victory turn
        cmp = a.CombatEndedTurn.CompareTo(b.CombatEndedTurn);
        if (cmp != 0)
            return cmp;

        // 8. Lower raw projected battle HP loss on equal strategic deficit
        cmp = a.ProjectedBattleHpLost.CompareTo(b.ProjectedBattleHpLost);
        if (cmp != 0)
            return cmp;

        // 9. Fewer potions used
        cmp = a.ProjectedBattlePotionCount.CompareTo(b.ProjectedBattlePotionCount);
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
    /// Checks whether a complete victory meets the configured zero/acceptable HP loss stop target.
    /// </summary>
    public static bool MeetsVictoryStopTarget(in HotRouteKey key, in HotSearchPolicy policy)
        => policy.CanStopAtHpTarget
            && key.Won
            && key.Survives
            && key.DeathSaveUseCount == 0
            && (policy.TheftPolicy != SolverTheftPolicy.PreserveResources
                || (key.OutstandingStolen == 0 && !key.ForfeitsGold))
            && (key.PotionMask & policy.ForcedPotionSlotMask) == policy.ForcedPotionSlotMask
            && key.ExplicitPotionCount <= policy.EarlyStopPotionUses
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
