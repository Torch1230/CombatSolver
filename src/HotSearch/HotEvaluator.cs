using System.Runtime.CompilerServices;
using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Powers;

namespace CombatSolver.HotSearch;

internal enum HotEvalMode : byte
{
    Balanced = 0,
    SetupAndSurvival = 1,
    Offense = 2,
}

/// <summary>
/// Stage 1 zero-allocation fast summary extracted directly from <see cref="CombatHotState"/>
/// before transposition lookup, certified HP incumbent pruning, and Stage 2 heuristic scoring.
/// </summary>
internal readonly struct HotFastSummary
{
    public readonly int PlayerHp;
    public readonly int PlayerMaxHp;
    public readonly int PlayerBlock;
    public readonly int Energy;
    public readonly int Stars;
    public readonly int Turn;
    public readonly int HpLost;
    public readonly int HpRecovered;
    public readonly int EnemyHpSum;
    public readonly int EnemyBlockSum;
    public readonly int DeathSaveHpRestored;
    public readonly int OutstandingStolen;
    public readonly int GrowthHpCredit;
    public readonly int GrowthRewardCount;
    public readonly byte AliveEnemyCount;
    public readonly byte RevivingEnemyCount;
    public readonly byte DeathSaveUseCount;
    public readonly byte AlchemizeCount;
    public readonly byte EmptyPotionSlots;
    public readonly bool ForfeitsGold;
    public readonly HotCombatOutcome Outcome;

    public HotFastSummary(
        int playerHp,
        int playerMaxHp,
        int playerBlock,
        int energy,
        int stars,
        int turn,
        int hpLost,
        int hpRecovered,
        int enemyHpSum,
        int enemyBlockSum,
        int deathSaveHpRestored,
        int outstandingStolen,
        int growthHpCredit,
        int growthRewardCount,
        byte aliveEnemyCount,
        byte revivingEnemyCount,
        byte deathSaveUseCount,
        byte alchemizeCount,
        byte emptyPotionSlots,
        bool forfeitsGold,
        HotCombatOutcome outcome)
    {
        PlayerHp = playerHp;
        PlayerMaxHp = playerMaxHp;
        PlayerBlock = playerBlock;
        Energy = energy;
        Stars = stars;
        Turn = turn;
        HpLost = hpLost;
        HpRecovered = hpRecovered;
        EnemyHpSum = enemyHpSum;
        EnemyBlockSum = enemyBlockSum;
        DeathSaveHpRestored = deathSaveHpRestored;
        OutstandingStolen = outstandingStolen;
        GrowthHpCredit = growthHpCredit;
        GrowthRewardCount = growthRewardCount;
        AliveEnemyCount = aliveEnemyCount;
        RevivingEnemyCount = revivingEnemyCount;
        DeathSaveUseCount = deathSaveUseCount;
        AlchemizeCount = alchemizeCount;
        EmptyPotionSlots = emptyPotionSlots;
        ForfeitsGold = forfeitsGold;
        Outcome = outcome;
    }

    public bool IsVictory => Outcome == HotCombatOutcome.Victory && PlayerHp > 0;
    public bool IsDefeat => Outcome == HotCombatOutcome.Defeat || PlayerHp <= 0;
}

/// <summary>
/// Certified HP incumbent bound in exact centi-HP frozen at the start of each wave so DOP=1 and DOP=N
/// make 100% deterministic identical pruning decisions.
/// Preserves exact tie-break priority (<c>V = NetPersistentHpDeltaCenti - NetPotionCostCenti</c> first,
/// then <c>NetPersistentHpDeltaCenti</c> before <c>EndTurn</c>) so Stage 2 potion winners tying on V are NEVER pruned!
/// </summary>
internal readonly struct HotIncumbentBound
{
    public readonly bool HasCertifiedZeroPotionBound;
    public readonly int BestZeroPotionHpDeltaCenti;
    public readonly int BestZeroPotionEndTurn;
    public readonly bool HasCertifiedOverallBound;
    public readonly long BestOverallValueCenti;
    public readonly int BestOverallHpDeltaCenti;
    public readonly int BestOverallEndTurn;

    public HotIncumbentBound(
        bool hasCertifiedZeroPotionBound,
        int bestZeroPotionHpDeltaCenti,
        int bestZeroPotionEndTurn,
        bool hasCertifiedOverallBound,
        long bestOverallValueCenti,
        int bestOverallHpDeltaCenti,
        int bestOverallEndTurn)
    {
        HasCertifiedZeroPotionBound = hasCertifiedZeroPotionBound;
        BestZeroPotionHpDeltaCenti = bestZeroPotionHpDeltaCenti;
        BestZeroPotionEndTurn = bestZeroPotionEndTurn;
        HasCertifiedOverallBound = hasCertifiedOverallBound;
        BestOverallValueCenti = bestOverallValueCenti;
        BestOverallHpDeltaCenti = bestOverallHpDeltaCenti;
        BestOverallEndTurn = bestOverallEndTurn;
    }

    public static HotIncumbentBound None => default;
}

/// <summary>
/// Pre-resolved per-root dense power DefIds so Stage 2 evaluation never does string/dictionary lookups.
/// </summary>
internal readonly struct HotEvalLookup
{
    public readonly ushort StrengthDefId;
    public readonly ushort DexterityDefId;
    public readonly ushort FocusDefId;
    public readonly ushort VulnerableDefId;
    public readonly ushort WeakDefId;
    public readonly ushort FrailDefId;
    public readonly ushort BarricadeDefId;
    public readonly ushort BlurDefId;
    public readonly ushort MetallicizeDefId;
    public readonly ushort PlatingDefId;
    public readonly ushort ArtifactDefId;
    public readonly ushort ThornsDefId;
    public readonly ushort RitualDefId;
    public readonly ushort PoisonDefId;
    public readonly ushort DoomDefId;
    public readonly ushort EchoFormDefId;
    public readonly ushort BufferDefId;
    public readonly ushort WraithFormDefId;
    public readonly ushort IntangibleDefId;
    public readonly ushort AfterimageDefId;
    public readonly ushort DemonFormDefId;
    public readonly ushort BlockNextTurnDefId;
    public readonly ushort EnergyNextTurnDefId;
    public readonly ushort DrawCardsNextTurnDefId;
    public readonly ushort StarNextTurnDefId;
    public readonly ushort SummonNextTurnDefId;
    public readonly ushort ReattachDefId;
    public readonly ushort SandpitDefId;
    public readonly ushort FurnaceDefId;
    public readonly ushort SelfFormingClayDefId;
    public readonly ushort ToricToughnessDefId;
    public readonly bool HasPaperKrane;
    public readonly bool HasOddMushroom;
    public readonly bool HasPaperPhrog;

    public HotEvalLookup(HotRules rules)
    {
        StrengthDefId = rules.StrengthPowerDefId != 0 ? rules.StrengthPowerDefId : rules.GetPowerDefId(HotPowerId.StrengthPower);
        DexterityDefId = rules.DexterityPowerDefId != 0 ? rules.DexterityPowerDefId : rules.GetPowerDefId(HotPowerId.DexterityPower);
        FocusDefId = rules.GetPowerDefId(HotPowerId.FocusPower);
        VulnerableDefId = rules.VulnerablePowerDefId != 0 ? rules.VulnerablePowerDefId : rules.GetPowerDefId(HotPowerId.VulnerablePower);
        WeakDefId = rules.WeakPowerDefId != 0 ? rules.WeakPowerDefId : rules.GetPowerDefId(HotPowerId.WeakPower);
        FrailDefId = rules.FrailPowerDefId != 0 ? rules.FrailPowerDefId : rules.GetPowerDefId(HotPowerId.FrailPower);
        BarricadeDefId = rules.BarricadePowerDefId != 0 ? rules.BarricadePowerDefId : rules.GetPowerDefId(HotPowerId.BarricadePower);
        BlurDefId = rules.GetPowerDefId(HotPowerId.BlurPower);
        MetallicizeDefId = rules.MetallicizePowerDefId;
        PlatingDefId = rules.PlatingPowerDefId != 0 ? rules.PlatingPowerDefId : rules.GetPowerDefId(HotPowerId.PlatingPower);
        ArtifactDefId = rules.ArtifactPowerDefId != 0 ? rules.ArtifactPowerDefId : rules.GetPowerDefId(HotPowerId.ArtifactPower);
        ThornsDefId = rules.ThornsPowerDefId != 0 ? rules.ThornsPowerDefId : rules.GetPowerDefId(HotPowerId.ThornsPower);
        RitualDefId = rules.RitualPowerDefId != 0 ? rules.RitualPowerDefId : rules.GetPowerDefId(HotPowerId.RitualPower);
        PoisonDefId = rules.GetPowerDefId(HotPowerId.PoisonPower);
        DoomDefId = rules.GetPowerDefId(HotPowerId.DoomPower);
        EchoFormDefId = rules.GetPowerDefId(HotPowerId.EchoFormPower);
        BufferDefId = rules.GetPowerDefId(HotPowerId.BufferPower);
        WraithFormDefId = rules.GetPowerDefId(HotPowerId.WraithFormPower);
        IntangibleDefId = rules.GetPowerDefId(HotPowerId.IntangiblePower);
        AfterimageDefId = rules.GetPowerDefId(HotPowerId.AfterimagePower);
        DemonFormDefId = rules.GetPowerDefId(HotPowerId.DemonFormPower);
        BlockNextTurnDefId = rules.GetPowerDefId(HotPowerId.BlockNextTurnPower);
        EnergyNextTurnDefId = rules.GetPowerDefId(HotPowerId.EnergyNextTurnPower);
        DrawCardsNextTurnDefId = rules.GetPowerDefId(HotPowerId.DrawCardsNextTurnPower);
        StarNextTurnDefId = rules.GetPowerDefId(HotPowerId.StarNextTurnPower);
        SummonNextTurnDefId = rules.GetPowerDefId(HotPowerId.SummonNextTurnPower);
        ReattachDefId = rules.GetPowerDefId(HotPowerId.ReattachPower);
        SandpitDefId = rules.GetPowerDefId(HotPowerId.SandpitPower);
        FurnaceDefId = rules.GetPowerDefId(HotPowerId.FurnacePower);
        SelfFormingClayDefId = rules.GetPowerDefId(HotPowerId.SelfFormingClayPower);
        ToricToughnessDefId = rules.GetPowerDefId(HotPowerId.ToricToughnessPower);
        HasPaperKrane = rules.HasPaperKrane;
        HasOddMushroom = rules.HasOddMushroom;
        HasPaperPhrog = rules.HasPaperPhrog;
    }
}

/// <summary>
/// 2-stage zero-allocation evaluator over <see cref="CombatHotState"/> aligned 1:1 with
/// <see cref="HotRouteQuality"/> and <see cref="HotPotionPolicy"/>'s unified centi-HP objective.
/// </summary>
internal static class HotEvaluator
{
    private const long DeathBaseScore = -1_000_000_000_000L;
    private const long VictoryBaseScore = 10_000_000_000L;
    private const long DeathSavePenaltyScore = 500_000_000L;
    private const long HpWeightPerCenti = 300L;       // 30_000L per 1.00 Persistent HP
    private const long EnemyHpWeight = 10_000L;
    private const long EnemyBlockWeight = 2_000L;
    private const long EnemyKillBonus = 85_000L;
    private const long IncomingThreatWeight = 24_000L;
    private const long SetupWeightScale = 14_000L;
    private const long EnergyWeight = 55_000L;
    private const long StarWeight = 18_000L;

    /// <summary>
    /// Stage 1: Zero-allocation scalar extraction straight from <see cref="CombatHotState"/>.
    /// </summary>
    public static HotFastSummary ExtractStage1(in CombatHotState s, in HotSearchPolicy policy)
    {
        int enemyHpSum = 0;
        int enemyBlockSum = 0;
        byte aliveEnemies = 0;
        byte revivingEnemies = 0;

        int rosterCount = s.RosterCount;
        for (int r = 0; r < rosterCount; r++)
        {
            byte slot = s.ActiveEnemyRoster[r];
            ref readonly HotEnemy enemy = ref s.Enemies[slot];
            if ((enemy.Vital.Flags & HotCreatureFlags.Alive) == 0)
            {
                if (enemy.DeathPhase == HotDeathPhase.Reviving)
                    revivingEnemies++;
                continue;
            }
            aliveEnemies++;
            enemyHpSum += Math.Max(0, enemy.Vital.CurrentHp);
            enemyBlockSum += Math.Max(0, enemy.Vital.Block);
        }

        int emptyPotionSlots = 0;
        int slotCount = Math.Min((int)s.PotionSlotCount, HotLimits.MaxPotions);
        for (int i = 0; i < slotCount; i++)
        {
            if (s.Potions[i] == 0)
                emptyPotionSlots++;
        }

        int hpLost = s.Counters[(int)HotCounterSlot.PlayerCumulativeHpLost];
        int hpRecovered = s.Counters[(int)HotCounterSlot.PlayerRecoveredHp];
        int deathSaveHp = s.DeathSaveRelicHpRestored + s.DeathSavePotionHpRestored;
        int stolen = Math.Max(0, s.OutstandingStolenGold) + Math.Max(0, s.OutstandingStolenCards);
        bool forfeitsGold = (s.CombatFlags & HotCombatFlags.EncounterGoldForfeited) != 0;

        ComputeGrowthSummary(in s, in policy, out int growthHpCredit, out int growthCount);

        HotCombatOutcome outcome = s.TerminalOutcome;
        if (outcome == HotCombatOutcome.Ongoing)
        {
            if ((s.Player.Flags & HotCreatureFlags.Alive) == 0 || s.Player.CurrentHp <= 0)
                outcome = HotCombatOutcome.Defeat;
            else if (aliveEnemies == 0 && revivingEnemies == 0)
                outcome = HotCombatOutcome.Victory;
        }

        return new HotFastSummary(
            playerHp: s.Player.CurrentHp,
            playerMaxHp: s.Player.MaxHp,
            playerBlock: s.Player.Block,
            energy: s.Energy,
            stars: s.Stars,
            turn: s.PlayerTurnNumber,
            hpLost: hpLost,
            hpRecovered: hpRecovered,
            enemyHpSum: enemyHpSum,
            enemyBlockSum: enemyBlockSum,
            deathSaveHpRestored: deathSaveHp,
            outstandingStolen: stolen,
            growthHpCredit: growthHpCredit,
            growthRewardCount: growthCount,
            aliveEnemyCount: aliveEnemies,
            revivingEnemyCount: revivingEnemies,
            deathSaveUseCount: (byte)Math.Clamp(s.DeathSaveUseCount, 0, byte.MaxValue),
            alchemizeCount: (byte)Math.Clamp((int)s.Growth[4], 0, byte.MaxValue),
            emptyPotionSlots: (byte)emptyPotionSlots,
            forfeitsGold: forfeitsGold,
            outcome: outcome);
    }

    /// <summary>
    /// Certified centi-HP incumbent bound check right after Stage 1 (before Stage 2 scoring).
    /// Sound only when <c>!policy.HasAnyHealOrUnknownGeneration</c> and no growth/forced/theft trade-offs override HP.
    /// Respects exact tie-break order (<c>V</c> -> <c>NetPersistentHpDeltaCenti</c> -> <c>Turn</c>) so Stage 2
    /// potion routes tying on <c>V</c> while winning on <c>NetPersistentHpDeltaCenti</c> are NEVER pruned.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPrunedByCertifiedIncumbent(
        in HotFastSummary summary,
        int optionalPaidCount,
        int explicitPotionCount,
        int netPotionCostCenti,
        in HotIncumbentBound bound,
        in HotSearchPolicy policy)
    {
        if (policy.HasAnyHealOrUnknownGeneration || policy.HasGrowthTargets || policy.HasForcedDirectives)
            return false;
        if (policy.TheftPolicy == SolverTheftPolicy.PreserveResources)
            return false;
        if (summary.IsDefeat)
            return false;

        int maxPostCombatHeal = policy.PostCombatRelicHeal.MonotoneHealFor(summary.PlayerHp, summary.PlayerMaxHp);
        int upperBoundHpDeltaCenti = HotRouteQuality.ComputeNetPersistentHpDeltaCenti(
            endPlayerHp: summary.PlayerHp,
            postCombatMonotoneHeal: maxPostCombatHeal,
            initialPlayerHp: policy.InitialPlayerHp,
            endPlayerMaxHp: summary.PlayerMaxHp,
            initialPlayerMaxHp: policy.InitialPlayerMaxHp,
            deathSaveHpRestored: summary.DeathSaveHpRestored,
            growthHpCredit: 0,
            bossHpRelief: policy.StrategicBossHpRelief,
            ignoreLongTermRewards: policy.IgnoreLongTermRewards);

        // Zero-potion certified bound (only applies to 0-potion branches when no paid potion has been spent yet)
        if (optionalPaidCount == 0 && explicitPotionCount == 0 && bound.HasCertifiedZeroPotionBound)
        {
            if (summary.DeathSaveUseCount > 0)
                return true;
            if (upperBoundHpDeltaCenti < bound.BestZeroPotionHpDeltaCenti)
                return true;
            if (upperBoundHpDeltaCenti == bound.BestZeroPotionHpDeltaCenti
                && !summary.IsVictory
                && summary.Turn > bound.BestZeroPotionEndTurn)
            {
                return true;
            }
        }

        // Overall unified economic value bound: V = NetPersistentHpDeltaCenti - NetPotionCostCenti
        if (bound.HasCertifiedOverallBound)
        {
            if (summary.DeathSaveUseCount > 0)
                return true;

            long upperBoundValueCenti = (long)upperBoundHpDeltaCenti - netPotionCostCenti;
            if (upperBoundValueCenti < bound.BestOverallValueCenti)
                return true;

            if (upperBoundValueCenti == bound.BestOverallValueCenti)
            {
                // Tie on V is broken by NetPersistentHpDeltaCenti DESC BEFORE Turn ASC!
                if (upperBoundHpDeltaCenti < bound.BestOverallHpDeltaCenti)
                    return true;
                if (upperBoundHpDeltaCenti == bound.BestOverallHpDeltaCenti
                    && !summary.IsVictory
                    && summary.Turn > bound.BestOverallEndTurn)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Stage 2: Single-pass zero-allocation heuristic over <see cref="CombatHotState"/> contiguous arrays.
    /// Uses the EXACT same <c>NetPersistentHpDeltaCenti</c> and <c>NetPotionOpportunityCostCenti</c> as
    /// <see cref="HotRouteQuality"/> so heuristic ranking and terminal arbitration never contradict each other.
    /// </summary>
    public static void EvaluateState(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup lookup,
        in HotFastSummary summary,
        int netPotionCostCenti,
        int actionCount,
        HotEvalMode evalMode,
        out long score,
        out int setupScore,
        out byte playableHandCount)
    {
        setupScore = 0;
        playableHandCount = 0;

        // 1. Terminal Defeat
        if (summary.IsDefeat)
        {
            score = DeathBaseScore
                + (long)summary.Turn * 100_000L
                - (long)summary.EnemyHpSum * 100L
                - (long)(summary.AliveEnemyCount + summary.RevivingEnemyCount) * 50_000L;
            return;
        }

        // 2. Terminal Victory
        if (summary.IsVictory)
        {
            int postCombatHeal = HotRouteQuality.RankedPostCombatRelicHeal(
                policy.PostCombatRelicHeal,
                completeVictory: true,
                summary.PlayerHp,
                summary.PlayerMaxHp);

            int netHpDeltaCenti = HotRouteQuality.ComputeNetPersistentHpDeltaCenti(
                endPlayerHp: summary.PlayerHp,
                postCombatMonotoneHeal: postCombatHeal,
                initialPlayerHp: policy.InitialPlayerHp,
                endPlayerMaxHp: summary.PlayerMaxHp,
                initialPlayerMaxHp: policy.InitialPlayerMaxHp,
                deathSaveHpRestored: summary.DeathSaveHpRestored,
                growthHpCredit: summary.GrowthHpCredit,
                bossHpRelief: policy.StrategicBossHpRelief,
                ignoreLongTermRewards: policy.IgnoreLongTermRewards);

            long vCenti = (long)netHpDeltaCenti - netPotionCostCenti;

            long victoryScore = VictoryBaseScore
                - (long)summary.DeathSaveUseCount * DeathSavePenaltyScore
                + vCenti * HpWeightPerCenti
                + (long)netHpDeltaCenti * 2L
                - (long)summary.Turn * 5_000L
                - (long)actionCount * 10L
                + (long)summary.GrowthRewardCount * 5_000L;

            if (policy.TheftPolicy == SolverTheftPolicy.PreserveResources)
            {
                victoryScore -= (long)summary.OutstandingStolen * 1_000_000L;
                if (summary.ForfeitsGold)
                    victoryScore -= 2_000_000L;
            }

            score = victoryScore;
            return;
        }

        // Mode multipliers (integer percentages for zero float variance)
        long setupScale = evalMode switch
        {
            HotEvalMode.SetupAndSurvival => (SetupWeightScale * 155L) / 100L,
            HotEvalMode.Offense => (SetupWeightScale * 85L) / 100L,
            _ => SetupWeightScale,
        };
        long threatWeight = evalMode switch
        {
            HotEvalMode.SetupAndSurvival => (IncomingThreatWeight * 120L) / 100L,
            HotEvalMode.Offense => (IncomingThreatWeight * 85L) / 100L,
            _ => IncomingThreatWeight,
        };
        long enemyHpWeight = evalMode switch
        {
            HotEvalMode.Offense => (EnemyHpWeight * 140L) / 100L,
            HotEvalMode.SetupAndSurvival => (EnemyHpWeight * 92L) / 100L,
            _ => EnemyHpWeight,
        };

        // 3. Ongoing combat: single-pass scan over powers, enemies, orbs, hand, and resources.
        int playerFocus = 0;
        int playerVuln = 0;
        int playerIntangible = 0;
        int playerEndTurnBlock = 0;
        int nextTurnBlock = 0;
        int nextTurnEnergy = 0;
        int nextTurnDraw = 0;
        int nextTurnStars = 0;
        int nextTurnSummon = 0;
        int furnacePower = 0;
        bool preservesExcessBlock = false;
        int playerSetup = 0;
        int enemyDebuffValue = 0;
        int enemyThreatPowerPenalty = 0;
        int minSandpitTurns = int.MaxValue;

        Span<int> enemyStr = stackalloc int[HotLimits.MaxEnemies];
        Span<byte> enemyWeak = stackalloc byte[HotLimits.MaxEnemies];
        Span<int> enemyPoison = stackalloc int[HotLimits.MaxEnemies];
        Span<int> enemyDoom = stackalloc int[HotLimits.MaxEnemies];
        Span<int> enemyReattachHp = stackalloc int[HotLimits.MaxEnemies];

        int powerCount = s.PowerCount;
        for (int i = 0; i < powerCount; i++)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.Amount == 0)
                continue;

            ushort defId = p.PowerDefId;
            if (p.OwnerSlot == 0)
            {
                if (defId == lookup.FocusDefId) playerFocus += p.Amount;
                else if (defId == lookup.VulnerableDefId) playerVuln += p.Amount;
                else if (defId == lookup.IntangibleDefId) playerIntangible += p.Amount;
                else if (defId == lookup.BarricadeDefId || defId == lookup.BlurDefId) preservesExcessBlock = true;
                else if (defId == lookup.MetallicizeDefId || defId == lookup.PlatingDefId) playerEndTurnBlock += p.Amount;
                else if (defId == lookup.BlockNextTurnDefId || defId == lookup.SelfFormingClayDefId || defId == lookup.ToricToughnessDefId)
                    nextTurnBlock += p.Amount;
                else if (defId == lookup.EnergyNextTurnDefId) nextTurnEnergy += p.Amount;
                else if (defId == lookup.DrawCardsNextTurnDefId) nextTurnDraw += p.Amount;
                else if (defId == lookup.StarNextTurnDefId) nextTurnStars += p.Amount;
                else if (defId == lookup.SummonNextTurnDefId) nextTurnSummon += p.Amount;
                else if (defId == lookup.FurnaceDefId) furnacePower += p.Amount;

                playerSetup += ComputePlayerPowerSetupWeight(defId, p.Amount, rules, in lookup);
            }
            else if (p.OwnerSlot >= 3)
            {
                int eSlot = p.OwnerSlot - 3;
                if ((uint)eSlot >= HotLimits.MaxEnemies)
                    continue;

                ref readonly HotEnemy ownerEnemy = ref s.Enemies[eSlot];
                bool isAlive = (ownerEnemy.Vital.Flags & HotCreatureFlags.Alive) != 0;

                if (defId == lookup.ReattachDefId && p.Amount > 0)
                {
                    enemyReattachHp[eSlot] = p.Amount;
                    continue;
                }

                if (!isAlive)
                    continue;

                if (defId == lookup.StrengthDefId)
                {
                    enemyStr[eSlot] += p.Amount;
                    if (p.Amount > 0)
                        enemyThreatPowerPenalty += Math.Min(p.Amount, 20) * 3;
                }
                else if (defId == lookup.WeakDefId && p.Amount > 0)
                {
                    enemyWeak[eSlot] = 1;
                    enemyDebuffValue += Math.Min(p.Amount, 5) * 4;
                }
                else if (defId == lookup.VulnerableDefId && p.Amount > 0)
                {
                    enemyDebuffValue += Math.Min(p.Amount, 5) * 5;
                }
                else if (defId == lookup.PoisonDefId && p.Amount > 0)
                {
                    enemyPoison[eSlot] += p.Amount;
                    enemyDebuffValue += Math.Min(p.Amount, 40) * 3;
                }
                else if (defId == lookup.DoomDefId && p.Amount > 0)
                {
                    enemyDoom[eSlot] += p.Amount;
                }
                else if (defId == lookup.RitualDefId && p.Amount > 0)
                {
                    enemyThreatPowerPenalty += Math.Min(p.Amount, 10) * 5;
                }
                else if (defId == lookup.SandpitDefId && p.Amount > 0)
                {
                    if (p.Amount < minSandpitTurns)
                        minSandpitTurns = p.Amount;
                }
            }
        }

        // Scan active roster: incoming attack threat, reviving segment revive-HP penalty, Doom lethal, and focus/AOE shape.
        int incomingDamage = 0;
        int lowestEnemyHp = int.MaxValue;
        int highestAliveEnemyHp = 0;
        int revivingHpPenalty = 0;
        int reattachAliveCount = 0;
        int doomLethalCount = 0;
        int effectiveDoomValue = 0;
        int rosterCount = s.RosterCount;

        for (int r = 0; r < rosterCount; r++)
        {
            byte eSlot = s.ActiveEnemyRoster[r];
            ref readonly HotEnemy enemy = ref s.Enemies[eSlot];
            if ((enemy.Vital.Flags & HotCreatureFlags.Alive) == 0)
            {
                if (enemy.DeathPhase == HotDeathPhase.Reviving)
                {
                    int revHp = enemyReattachHp[eSlot] > 0
                        ? enemyReattachHp[eSlot]
                        : Math.Max(15, enemy.Vital.MaxHp / 2);
                    revivingHpPenalty += revHp;
                }
                continue;
            }

            int curHp = Math.Max(0, enemy.Vital.CurrentHp);
            bool hasReattach = enemyReattachHp[eSlot] > 0;
            if (hasReattach)
                reattachAliveCount++;

            if (enemyDoom[eSlot] > 0)
            {
                if (enemyDoom[eSlot] >= curHp && !hasReattach)
                {
                    doomLethalCount++;
                    effectiveDoomValue += curHp;
                }
                else
                {
                    effectiveDoomValue += Math.Min(enemyDoom[eSlot], curHp);
                }
            }

            int effectiveEnemyHp = Math.Max(0, curHp - enemyPoison[eSlot] - enemyDoom[eSlot]);
            if (effectiveEnemyHp < lowestEnemyHp)
                lowestEnemyHp = effectiveEnemyHp;
            if (curHp > highestAliveEnemyHp)
                highestAliveEnemyHp = curHp;

            if ((enemy.Flags & HotEnemyFlags.SkipNextMove) != 0)
                continue;

            int rawIncoming = EstimateEnemyIncomingDamage(
                in enemy,
                rules,
                enemyStr[eSlot],
                enemyWeak[eSlot] != 0,
                playerVuln > 0,
                playerIntangible > 0,
                in lookup);
            incomingDamage += rawIncoming;
        }

        // Effective defense buffer = current block + Plating/Metallicize + Wuppy/Osty absorption
        int effectiveDefense = summary.PlayerBlock + playerEndTurnBlock;
        if ((s.CombatFlags & HotCombatFlags.HasWuppy) != 0 && (s.Wuppy.Flags & HotCreatureFlags.Alive) != 0)
            effectiveDefense += s.Wuppy.CurrentHp;
        if ((s.CombatFlags & HotCombatFlags.HasOsty) != 0 && (s.Osty.Flags & HotCreatureFlags.Alive) != 0)
            effectiveDefense += s.Osty.CurrentHp;

        int unblockedIncoming = Math.Max(0, incomingDamage - effectiveDefense);

        // Unified centi-HP persistent delta minus exact net potion opportunity cost
        int ongoingNetHpDeltaCenti = HotRouteQuality.ComputeNetPersistentHpDeltaCenti(
            endPlayerHp: summary.PlayerHp,
            postCombatMonotoneHeal: 0,
            initialPlayerHp: policy.InitialPlayerHp,
            endPlayerMaxHp: summary.PlayerMaxHp,
            initialPlayerMaxHp: policy.InitialPlayerMaxHp,
            deathSaveHpRestored: summary.DeathSaveHpRestored,
            growthHpCredit: summary.GrowthHpCredit,
            bossHpRelief: policy.StrategicBossHpRelief,
            ignoreLongTermRewards: policy.IgnoreLongTermRewards);

        long runningScore = (long)ongoingNetHpDeltaCenti * HpWeightPerCenti
            - (long)netPotionCostCenti * HpWeightPerCenti
            - (long)summary.DeathSaveUseCount * DeathSavePenaltyScore;

        // Threat evaluation: aligned with BossHpRelief while still heavily penalizing lethal incoming damage
        if (unblockedIncoming > 0)
        {
            int scaledThreat = policy.StrategicBossHpRelief switch
            {
                BossHpRelief.RunEnding => unblockedIncoming >= summary.PlayerHp ? unblockedIncoming : unblockedIncoming / 4,
                BossHpRelief.ActClearHeal => unblockedIncoming >= summary.PlayerHp ? unblockedIncoming : (unblockedIncoming + 2) / 5,
                _ => unblockedIncoming,
            };

            if (unblockedIncoming >= summary.PlayerHp)
                runningScore -= summary.Energy == 0 ? 4_000_000L : 800_000L;

            runningScore -= (long)scaledThreat * threatWeight;
        }
        else if (preservesExcessBlock && summary.PlayerBlock > incomingDamage)
        {
            int carriedBlock = Math.Min(summary.PlayerBlock - incomingDamage, 60);
            runningScore += (long)carriedBlock * 14_000L;
        }

        // Next-turn defensive/resource carryovers
        long carryoverMult = evalMode == HotEvalMode.SetupAndSurvival ? 125L : 100L;
        if (nextTurnBlock > 0)
            runningScore += ((long)Math.Min(nextTurnBlock, 60) * 14_000L * carryoverMult) / 100L;
        if (nextTurnEnergy > 0)
            runningScore += ((long)Math.Min(nextTurnEnergy, 6) * 34_000L * carryoverMult) / 100L;
        if (nextTurnDraw > 0)
            runningScore += ((long)Math.Min(nextTurnDraw, 6) * 18_000L * carryoverMult) / 100L;
        if (nextTurnStars > 0)
            runningScore += (long)Math.Min(nextTurnStars, 8) * 14_000L;
        if (nextTurnSummon > 0)
            runningScore += (long)Math.Min(nextTurnSummon, 20) * 6_000L;

        // Offense, permanent kills, reviving-enemy correction, Doom lethal, and focus/AOE shaping
        int remainingNonDead = summary.AliveEnemyCount + summary.RevivingEnemyCount;
        int permanentKills = Math.Max(0, policy.InitialEnemyCount - remainingNonDead) + doomLethalCount;
        runningScore += (long)permanentKills * EnemyKillBonus;
        runningScore -= (long)summary.EnemyHpSum * enemyHpWeight;
        runningScore -= (long)summary.EnemyBlockSum * EnemyBlockWeight;

        if (revivingHpPenalty > 0)
            runningScore -= (long)revivingHpPenalty * ((enemyHpWeight * 92L) / 100L);

        if (effectiveDoomValue > 0)
            runningScore += (long)effectiveDoomValue * 5_500L;

        bool allReattachEncounter = reattachAliveCount > 0
            && (reattachAliveCount + summary.RevivingEnemyCount) >= summary.AliveEnemyCount;

        if (allReattachEncounter && summary.AliveEnemyCount > 1)
        {
            runningScore -= (long)highestAliveEnemyHp * 2_500L;
        }
        else if (summary.AliveEnemyCount > 1 && lowestEnemyHp < int.MaxValue)
        {
            int avgHp = summary.EnemyHpSum / summary.AliveEnemyCount;
            int focusGap = Math.Max(0, avgHp - lowestEnemyHp);
            runningScore += (long)Math.Min(focusGap, 60) * 1_200L;
        }

        if (minSandpitTurns < int.MaxValue)
        {
            runningScore += (long)Math.Min(minSandpitTurns, 6) * 30_000L;
            if (minSandpitTurns <= 1 && summary.EnemyHpSum > 60)
                runningScore -= 140_000L;
        }

        // Orbs (Defect)
        int orbScore = 0;
        int orbCount = s.OrbCount;
        for (int o = 0; o < orbCount; o++)
        {
            HotOrb orb = s.Orbs[o];
            if (orb.OrbTypeId == 0 || orb.OrbTypeId >= rules.Orbs.Length)
                continue;
            HotOrbId orbId = rules.Orbs[orb.OrbTypeId].OrbId;
            int val = orbId switch
            {
                HotOrbId.Lightning => Math.Max(0, orb.PassiveVal + playerFocus) * 2 + Math.Max(0, orb.EvokeVal + playerFocus),
                HotOrbId.Frost => Math.Max(0, orb.PassiveVal + playerFocus) * 3 + Math.Max(0, orb.EvokeVal + playerFocus),
                HotOrbId.Dark => orb.EvokeVal + Math.Max(0, orb.PassiveVal + playerFocus) * 2,
                HotOrbId.Plasma => 18,
                HotOrbId.Glass => Math.Max(0, (int)orb.PassiveVal) * 2 + orb.EvokeVal,
                _ => orb.PassiveVal + orb.EvokeVal / 2,
            };
            orbScore += val;
        }
        playerSetup += Math.Min(orbScore / 3, 24);
        runningScore += (long)Math.Min(orbScore, 120) * 2_500L;

        // Osty (Necrobinder)
        if ((s.CombatFlags & HotCombatFlags.HasOsty) != 0 && (s.Osty.Flags & HotCreatureFlags.Alive) != 0)
        {
            runningScore += (long)Math.Min(s.Osty.CurrentHp, 80) * 4_500L;
            playerSetup += Math.Min(s.Osty.CurrentHp / 4, 12);
        }

        // Fool arrows & melodies
        if (s.ArrowCount > 0)
        {
            int readyArrows = 0;
            for (int a = 0; a < s.ArrowCount; a++)
            {
                int status = s.Fool.Arrows[a] & 0xF;
                if (status != 0)
                    readyArrows++;
            }
            runningScore += (long)readyArrows * 6_000L;
            runningScore += (long)s.Fool.MelodyCardsFinished * 5_000L;
        }

        // Hand potential, retained scaling attacks, and deck clutter
        int handCount = s.HandCount;
        int playableCards = 0;
        int zeroCostPlayable = 0;
        int handClutter = 0;
        long retainedAttackBonus = 0;

        for (int h = 0; h < handCount; h++)
        {
            ref readonly HotCard c = ref s.Cards[h];
            HotCardDef def = rules.Cards[c.CardDefId];
            if (def.Type is CardType.Status or CardType.Curse)
            {
                handClutter++;
                continue;
            }

            if (def.CardId == HotCustomCardId.SovereignBlade
                && ((c.LocalKeywordsMask & HotCardKeywordMask.Retain) != 0 || (c.Flags & HotCardFlags.SingleTurnRetain) != 0))
            {
                int bladeDmg = Math.Max(0, def.GetDamage(c.UpgradeLevel, c.Var0Delta));
                if (furnacePower > 0 && summary.EnemyHpSum > bladeDmg * 2)
                {
                    long perDmg = evalMode == HotEvalMode.SetupAndSurvival ? 6_500L : 4_800L;
                    retainedAttackBonus += (long)Math.Min(bladeDmg + furnacePower, 70) * perDmg;
                }
            }

            if ((c.LocalKeywordsMask & HotCardKeywordMask.Unplayable) != 0)
                continue;

            int cost = (c.Flags & HotCardFlags.CostsX) != 0
                ? summary.Energy
                : Math.Max(0, HotCostModifiers.GetLocalEnergyCost(in c, cold));
            int starCost = (c.Flags & HotCardFlags.HasStarCostX) != 0
                ? summary.Stars
                : Math.Max(0, HotCostModifiers.GetEffectiveStarCost(in c, cold));
            if (cost <= summary.Energy && starCost <= summary.Stars)
            {
                playableCards++;
                if (cost == 0 && starCost == 0)
                    zeroCostPlayable++;
            }
        }

        int deckClutter = handClutter;
        int livePileEnd = s.DiscardStart + s.DiscardCount;
        for (int idx = s.DrawStart; idx < livePileEnd; idx++)
        {
            CardType ct = rules.Cards[s.Cards[idx].CardDefId].Type;
            if (ct is CardType.Status or CardType.Curse)
                deckClutter++;
        }

        playableHandCount = (byte)Math.Clamp(playableCards, 0, byte.MaxValue);

        runningScore += (long)Math.Min(summary.Energy, 6) * EnergyWeight;
        runningScore += (long)Math.Min(summary.Stars, 8) * StarWeight;
        runningScore += (long)Math.Min(playableCards, 10) * 18_000L;
        runningScore += (long)Math.Min(zeroCostPlayable, 8) * 11_000L;
        runningScore += retainedAttackBonus;
        runningScore -= (long)handClutter * 4_000L;
        runningScore -= (long)deckClutter * 4_500L;

        int totalSetup = Math.Clamp(playerSetup + enemyDebuffValue - enemyThreatPowerPenalty, -80, 96);
        setupScore = totalSetup;
        runningScore += (long)Math.Clamp(totalSetup, -48, 64) * setupScale;

        // Long-term resources, anger copies, theft (GrowthHpCredit already included inside ongoingNetHpDeltaCenti!)
        runningScore += (long)summary.GrowthRewardCount * 5_000L;
        runningScore += (long)Math.Min(s.LongTermResourceValue, 20) * 3_500L;
        runningScore -= (long)s.AngerCopiesGenerated * 10_000L;
        runningScore -= (long)actionCount * 5L;

        if (policy.TheftPolicy == SolverTheftPolicy.PreserveResources)
        {
            runningScore -= (long)summary.OutstandingStolen * 1_000_000L;
            if (summary.ForfeitsGold)
                runningScore -= 2_000_000L;
        }

        score = runningScore;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EstimateEnemyIncomingDamage(
        in HotEnemy enemy,
        HotRules rules,
        int enemyStrength,
        bool enemyWeak,
        bool playerVulnerable,
        bool playerIntangible,
        in HotEvalLookup lookup)
    {
        if (enemy.MonsterDefId == 0 || enemy.CurrentMoveNode < 0)
            return 0;

        HotMonsterMachine? machine = rules.GetMonsterMachine(enemy.MonsterDefId);
        if (machine == null || enemy.CurrentMoveNode >= machine.Nodes.Length)
            return 0;

        HotMonsterNode node = machine.Nodes[enemy.CurrentMoveNode];
        int total = 0;

        if (node.Attacks.Length > 0)
        {
            for (int i = 0; i < node.Attacks.Length; i++)
            {
                HotMonsterAttack atk = node.Attacks[i];
                if (atk.Repeats == 0)
                    continue;
                int hit = ScaleEnemyHit(atk.BaseDamage, enemyStrength, enemyWeak, playerVulnerable, playerIntangible, in lookup);
                total += hit * atk.Repeats;
            }
        }
        else if (node.Repeats > 0)
        {
            int hits = node.Repeats;
            if (rules.Monsters[enemy.MonsterDefId].StateKind == HotMonsterStateKind.TestSubject
                && string.Equals(node.MoveId, "MULTI_CLAW_MOVE", StringComparison.Ordinal))
            {
                hits = Math.Max(1, rules.Monsters[enemy.MonsterDefId].TestSubjectBaseMultiClawCount + enemy.AiRegs[1]);
            }
            int hit = ScaleEnemyHit(node.BaseDamage, enemyStrength, enemyWeak, playerVulnerable, playerIntangible, in lookup);
            total += hit * hits;
        }

        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScaleEnemyHit(
        int baseDamage,
        int enemyStrength,
        bool enemyWeak,
        bool playerVulnerable,
        bool playerIntangible,
        in HotEvalLookup lookup)
    {
        int dmg = baseDamage + enemyStrength;
        if (dmg <= 0)
            return 0;
        if (enemyWeak)
            dmg = lookup.HasPaperKrane ? (dmg * 60) / 100 : (dmg * 75) / 100;
        if (playerVulnerable)
            dmg = lookup.HasOddMushroom ? (dmg * 125) / 100 : (dmg * 150) / 100;
        if (playerIntangible && dmg > 1)
            dmg = 1;
        return Math.Max(0, dmg);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputePlayerPowerSetupWeight(
        ushort defId,
        int amount,
        HotRules rules,
        in HotEvalLookup lookup)
    {
        if (defId >= rules.Powers.Length)
            return 0;

        HotPowerDef def = rules.Powers[defId];

        if (def.Type == PowerType.Debuff)
        {
            int mag = Math.Clamp(Math.Abs(amount), 1, 6);
            return def.PowerId switch
            {
                HotPowerId.WeakPower => -mag * 3,
                HotPowerId.VulnerablePower => -mag * 3,
                HotPowerId.FrailPower => -mag * 3,
                HotPowerId.WraithFormPower => -mag * 2,
                HotPowerId.BiasedCognitionPower => -mag * 2,
                HotPowerId.NoBlockPower => -mag * 4,
                HotPowerId.NoDrawPower => -3,
                HotPowerId.NoEnergyGainPower => -4,
                HotPowerId.ConstrictPower => -Math.Min(Math.Abs(amount), 12),
                HotPowerId.BorrowedTimePower => -3,
                HotPowerId.ChainsOfBindingPower => -mag * 2,
                HotPowerId.SlothPower => -4,
                HotPowerId.SlowPower => -2,
                HotPowerId.MindRotPower => -mag * 3,
                HotPowerId.TangledPower => -3,
                HotPowerId.TheGambitPower => -2,
                _ => -Math.Min(mag, 3),
            };
        }

        switch (def.PowerId)
        {
            case HotPowerId.BlockNextTurnPower:
            case HotPowerId.SelfFormingClayPower:
            case HotPowerId.ToricToughnessPower:
                return Math.Min(Math.Max(0, amount) / 4, 14);
            case HotPowerId.EnergyNextTurnPower:
                return Math.Min(Math.Max(0, amount) * 3, 12);
            case HotPowerId.DrawCardsNextTurnPower:
                return Math.Min(Math.Max(0, amount) * 2, 10);
            case HotPowerId.StarNextTurnPower:
            case HotPowerId.SummonNextTurnPower:
                return Math.Min(Math.Max(0, amount) * 2, 8);
        }

        int posAmt = Math.Clamp(amount, 0, 16);
        int explicitWeight = def.PowerId switch
        {
            HotPowerId.VoidFormPower => Math.Min(posAmt, 3) * 14,
            HotPowerId.SerpentFormPower => Math.Min(posAmt, 12) * 3,
            HotPowerId.SpeedsterPower => Math.Min(posAmt, 10) * 2,
            HotPowerId.SpiritOfAshPower => Math.Min(posAmt, 12) * 2,
            HotPowerId.VeilpiercerPower => Math.Min(posAmt, 4) * 5,
            HotPowerId.MayhemPower => Math.Min(posAmt, 3) * 10,
            HotPowerId.AutomationPower => Math.Min(posAmt, 3) * 6,
            HotPowerId.ReaperFormPower => Math.Min(posAmt, 3) * 12,
            HotPowerId.FurnacePower => 8 + Math.Min(posAmt, 12) * 2,
            HotPowerId.OrbitPower => Math.Min(posAmt, 4) * 12,
            HotPowerId.PagestormPower => Math.Min(posAmt, 4) * 10,
            HotPowerId.PlatingPower => Math.Min(posAmt, 16) * 2,
            HotPowerId.ThornsPower => Math.Min(posAmt, 12) * 2,
            HotPowerId.IntangiblePower => Math.Min(posAmt, 4) * 16,
            HotPowerId.EchoFormPower => Math.Min(posAmt, 3) * 16,
            HotPowerId.DemonFormPower => Math.Min(posAmt, 6) * 6,
            HotPowerId.BufferPower => Math.Min(posAmt, 4) * 8,
            HotPowerId.AfterimagePower => Math.Min(posAmt, 4) * 8,
            HotPowerId.BarricadePower => 14,
            HotPowerId.BlurPower => Math.Min(posAmt, 4) * 5,
            HotPowerId.CalcifyPower => Math.Min(posAmt, 10) * 2,
            HotPowerId.DevourLifePower => Math.Min(posAmt, 6) * 4,
            HotPowerId.HauntPower => Math.Min(posAmt, 6) * 4,
            HotPowerId.CountdownPower => Math.Min(posAmt, 8) * 3,
            HotPowerId.NecroMasteryPower => Math.Min(posAmt, 6) * 4,
            HotPowerId.BlackHolePower => Math.Min(posAmt, 8) * 3,
            HotPowerId.ArsenalPower => Math.Min(posAmt, 6) * 4,
            HotPowerId.PillarOfCreationPower => Math.Min(posAmt, 8) * 3,
            HotPowerId.MonarchsGazePower => Math.Min(posAmt, 4) * 5,
            HotPowerId.SeekingEdgePower => 10,
            HotPowerId.HammerTimePower => 12,
            HotPowerId.RadiancePower => Math.Min(posAmt, 6) * 4,
            HotPowerId.ParryPower => Math.Min(posAmt, 8) * 2,
            HotPowerId.CrueltyPower => Math.Min(posAmt, 6) * 3,
            HotPowerId.AccelerantPower => Math.Min(posAmt, 4) * 4,
            HotPowerId.FanOfKnivesPower => 8,
            HotPowerId.PhantomBladesPower => Math.Min(posAmt, 10) * 2,
            HotPowerId.PanachePower => Math.Min(posAmt, 16),
            HotPowerId.StratagemPower => Math.Min(posAmt, 4) * 5,
            HotPowerId.NostalgiaPower => Math.Min(posAmt, 4) * 5,
            HotPowerId.MasterPlannerPower => 10,
            HotPowerId.FeralPower => Math.Min(posAmt, 4) * 5,
            HotPowerId.SubroutinePower => Math.Min(posAmt, 4) * 6,
            HotPowerId.ThunderPower => Math.Min(posAmt, 10) * 2,
            HotPowerId.IterationPower => Math.Min(posAmt, 4) * 5,
            HotPowerId.CoolantPower => Math.Min(posAmt, 8) * 3,
            HotPowerId.CalamityPower => Math.Min(posAmt, 4) * 6,
            HotPowerId.HellraiserPower => 10,
            HotPowerId.CrimsonMantlePower => Math.Min(posAmt, 8) * 3,
            HotPowerId.InfernoPower => Math.Min(posAmt, 8) * 3,
            HotPowerId.EntropyPower => Math.Min(posAmt, 4) * 5,
            HotPowerId.DuplicationPower => Math.Min(posAmt, 4) * 8,
            HotPowerId.BurstPower => Math.Min(posAmt, 4) * 6,
            HotPowerId.SignalBoostPower => Math.Min(posAmt, 4) * 6,
            HotPowerId.OneTwoPunchPower => Math.Min(posAmt, 4) * 6,
            HotPowerId.FreeCardPower => Math.Min(posAmt, 4) * 6,
            HotPowerId.FreeSkillPower => Math.Min(posAmt, 4) * 5,
            HotPowerId.FreePowerPower => Math.Min(posAmt, 4) * 6,
            HotPowerId.FreeAttackPower => Math.Min(posAmt, 4) * 5,
            HotPowerId.RetainHandPower => Math.Min(posAmt, 4) * 4,
            _ => 0,
        };
        if (explicitWeight != 0)
            return Math.Clamp(explicitWeight, -24, 30);

        short catalogWeight = def.SetupWeight;
        if (catalogWeight != 0)
        {
            if (def.StackType == PowerStackType.Single)
                return Math.Clamp(catalogWeight / 2, -24, 28);
            int clampedAmt = Math.Clamp(amount, -10, 10);
            return Math.Clamp((catalogWeight * clampedAmt) / 3, -24, 28);
        }

        int clamped = Math.Clamp(amount, -10, 10);
        if (defId == lookup.StrengthDefId) return clamped * 3;
        if (defId == lookup.DexterityDefId) return clamped * 3;
        if (defId == lookup.FocusDefId) return clamped * 4;
        if (defId == lookup.MetallicizeDefId) return Math.Min(posAmt, 12) * 2;
        if (defId == lookup.ArtifactDefId) return Math.Min(posAmt, 4) * 3;
        if (defId == lookup.RitualDefId) return Math.Min(posAmt, 5) * 5;

        return 0;
    }

    private static void ComputeGrowthSummary(
        in CombatHotState s,
        in HotSearchPolicy policy,
        out int credit,
        out int count)
    {
        int totalCount = 0;
        for (int i = 0; i < 11; i++)
            totalCount += s.Growth[i];
        count = totalCount;

        if (totalCount == 0 || policy.IgnoreLongTermRewards || !policy.EffectiveGrowthBudgets.IsEnabled)
        {
            credit = 0;
            return;
        }

        GrowthValues b = policy.EffectiveGrowthBudgets;
        credit = s.Growth[0] * b.HandOfGreed
            + s.Growth[1] * b.TheHunt
            + s.Growth[2] * b.Feed
            + s.Growth[3] * b.Royalties
            + s.Growth[4] * b.Alchemize
            + s.Growth[5] * b.GeneticAlgorithm
            + s.Growth[6] * b.TheScythe
            + s.Growth[7] * b.Goopy
            + s.Growth[8] * b.ForbiddenGrimoire
            + s.Growth[9] * b.MadScience
            + s.Growth[10] * b.ShowOff;
    }
}
