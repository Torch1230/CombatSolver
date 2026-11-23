using System.Runtime.CompilerServices;
using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace CombatSolver.HotSearch;

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
    public readonly byte DeathSaveUseCount;
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
        byte deathSaveUseCount,
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
        DeathSaveUseCount = deathSaveUseCount;
        ForfeitsGold = forfeitsGold;
        Outcome = outcome;
    }

    public bool IsVictory => Outcome == HotCombatOutcome.Victory && PlayerHp > 0;
    public bool IsDefeat => Outcome == HotCombatOutcome.Defeat || PlayerHp <= 0;
}

/// <summary>
/// Simple 2-bit certified HP incumbent bound frozen at the start of each wave so DOP=1 and DOP=N
/// make 100% deterministic identical pruning decisions.
/// </summary>
internal readonly struct HotIncumbentBound
{
    public readonly bool HasCertifiedZeroPotionBound;
    public readonly int BestZeroPotionHpLost;
    public readonly int BestZeroPotionEndTurn;
    public readonly bool HasCertifiedOverallBound;
    public readonly int BestOverallCost; // StrategicHpDeficit + OptionalPotionStrategicCost
    public readonly int BestOverallEndTurn;

    public HotIncumbentBound(
        bool hasCertifiedZeroPotionBound,
        int bestZeroPotionHpLost,
        int bestZeroPotionEndTurn,
        bool hasCertifiedOverallBound,
        int bestOverallCost,
        int bestOverallEndTurn)
    {
        HasCertifiedZeroPotionBound = hasCertifiedZeroPotionBound;
        BestZeroPotionHpLost = bestZeroPotionHpLost;
        BestZeroPotionEndTurn = bestZeroPotionEndTurn;
        HasCertifiedOverallBound = hasCertifiedOverallBound;
        BestOverallCost = bestOverallCost;
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
    public readonly ushort AfterimageDefId;
    public readonly ushort DemonFormDefId;
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
        AfterimageDefId = rules.GetPowerDefId(HotPowerId.AfterimagePower);
        DemonFormDefId = rules.GetPowerDefId(HotPowerId.DemonFormPower);
        HasPaperKrane = rules.HasPaperKrane;
        HasOddMushroom = rules.HasOddMushroom;
        HasPaperPhrog = rules.HasPaperPhrog;
    }
}

/// <summary>
/// 2-stage zero-allocation evaluator over <see cref="CombatHotState"/>.
/// ZERO references to <c>MegaCrit.Sts2.Core.Models.*</c>; ZERO per-state managed allocations.
/// </summary>
internal static class HotEvaluator
{
    private const long DeathBaseScore = -1_000_000_000_000L;
    private const long VictoryBaseScore = 10_000_000_000L;
    private const long DeathSavePenaltyScore = 500_000_000L;
    private const long HpWeight = 30_000L;
    private const long EnemyHpWeight = 10_000L;
    private const long EnemyBlockWeight = 2_000L;
    private const long EnemyKillBonus = 85_000L;
    private const long IncomingThreatWeight = 22_000L;
    private const long SetupWeightScale = 12_000L;
    private const long EnergyWeight = 55_000L;
    private const long StarWeight = 18_000L;
    private const long PotionCostWeight = 28_000L;

    /// <summary>
    /// Stage 1: Zero-allocation scalar extraction straight from <see cref="CombatHotState"/>.
    /// </summary>
    public static HotFastSummary ExtractStage1(in CombatHotState s, in HotSearchPolicy policy)
    {
        int enemyHpSum = 0;
        int enemyBlockSum = 0;
        byte aliveEnemies = 0;

        int rosterCount = s.RosterCount;
        for (int r = 0; r < rosterCount; r++)
        {
            byte slot = s.ActiveEnemyRoster[r];
            ref readonly HotEnemy enemy = ref s.Enemies[slot];
            if ((enemy.Vital.Flags & HotCreatureFlags.Alive) == 0)
                continue;
            aliveEnemies++;
            enemyHpSum += Math.Max(0, enemy.Vital.CurrentHp);
            enemyBlockSum += Math.Max(0, enemy.Vital.Block);
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
            else if (aliveEnemies == 0)
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
            deathSaveUseCount: (byte)Math.Clamp(s.DeathSaveUseCount, 0, byte.MaxValue),
            forfeitsGold: forfeitsGold,
            outcome: outcome);
    }

    /// <summary>
    /// Checks the 2-bit certified HP incumbent bound right after Stage 1 (before Stage 2 scoring).
    /// Sound only when <c>!policy.HasAnyHealOrUnknownGeneration</c> and no growth/forced/theft trade-offs override HP.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPrunedByCertifiedIncumbent(
        in HotFastSummary summary,
        int explicitPotionCount,
        int optionalPotionRequiredHp,
        in HotIncumbentBound bound,
        in HotSearchPolicy policy)
    {
        if (policy.HasAnyHealOrUnknownGeneration || policy.HasGrowthTargets || policy.HasForcedDirectives)
            return false;
        if (policy.TheftPolicy == SolverTheftPolicy.PreserveResources)
            return false;
        if (summary.IsDefeat)
            return false;

        int maxHpDeficit = Math.Max(0, policy.InitialPlayerMaxHp - summary.PlayerMaxHp);
        int postCombatMaxPossible = policy.PostCombatRelicHeal.UnconditionalHeal;
        // Sound lower bound on final strategic HP deficit when no in-combat healing exists:
        // player can at most avoid further damage and receive post-combat unconditional heal on victory.
        int minPossibleDeficit = ActEndingBossPolicy.StrategicHpDeficit(
            summary.HpLost,
            maxHpDeficit,
            summary.HpRecovered + postCombatMaxPossible,
            policy.StrategicBossHpRelief,
            summary.DeathSaveHpRestored);

        if (explicitPotionCount == 0 && bound.HasCertifiedZeroPotionBound)
        {
            if (summary.DeathSaveUseCount > 0)
                return true;
            if (minPossibleDeficit > bound.BestZeroPotionHpLost)
                return true;
            if (minPossibleDeficit == bound.BestZeroPotionHpLost
                && !summary.IsVictory
                && summary.Turn >= bound.BestZeroPotionEndTurn)
            {
                return true;
            }
        }

        if (bound.HasCertifiedOverallBound)
        {
            long minTotalCost = (long)minPossibleDeficit + optionalPotionRequiredHp;
            if (summary.DeathSaveUseCount > 0)
                return true;
            if (minTotalCost > bound.BestOverallCost)
                return true;
            if (minTotalCost == bound.BestOverallCost
                && !summary.IsVictory
                && summary.Turn >= bound.BestOverallEndTurn)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Stage 2: Single-pass zero-allocation heuristic over <see cref="CombatHotState"/> contiguous arrays.
    /// Returns both total <paramref name="score"/> and <paramref name="setupScore"/> (for scaling diversity lane).
    /// </summary>
    public static void EvaluateState(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup lookup,
        in HotFastSummary summary,
        int potionStrategicCost,
        int actionCount,
        out long score,
        out int setupScore)
    {
        setupScore = 0;

        // 1. Terminal Defeat
        if (summary.IsDefeat)
        {
            score = DeathBaseScore
                + (long)summary.Turn * 100_000L
                - (long)summary.EnemyHpSum * 100L
                - (long)summary.AliveEnemyCount * 50_000L;
            return;
        }

        int maxHpDelta = Math.Max(0, policy.InitialPlayerMaxHp - summary.PlayerMaxHp);

        // 2. Terminal Victory
        if (summary.IsVictory)
        {
            int postCombatHeal = ActEndingBossPolicy.RankedPostCombatRelicHeal(
                policy.PostCombatRelicHeal,
                completeVictory: true,
                summary.PlayerHp,
                summary.PlayerMaxHp);

            int strategicDeficit = ActEndingBossPolicy.StrategicHpDeficit(
                summary.HpLost,
                maxHpDelta,
                summary.HpRecovered + postCombatHeal,
                policy.StrategicBossHpRelief,
                summary.DeathSaveHpRestored) - summary.GrowthHpCredit;

            long victoryScore = VictoryBaseScore
                - (long)summary.DeathSaveUseCount * DeathSavePenaltyScore
                - (long)strategicDeficit * HpWeight
                - (long)potionStrategicCost * PotionCostWeight
                - (long)summary.Turn * 5_000L
                - (long)actionCount * 10L
                + (long)summary.GrowthHpCredit * 20_000L
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

        // 3. Ongoing combat: single-pass scan over powers, enemies, orbs, hand, and resources.
        int playerStrength = 0;
        int playerDexterity = 0;
        int playerFocus = 0;
        int playerVuln = 0;
        int playerEndTurnBlock = 0;
        bool hasBarricade = false;
        int playerSetup = 0;
        int enemyDebuffValue = 0;
        int enemyThreatPowerPenalty = 0;

        // Stack-local per-enemy stat cache (max 16 enemies -> 64 bytes on stack!)
        Span<int> enemyStr = stackalloc int[HotLimits.MaxEnemies];
        Span<byte> enemyWeak = stackalloc byte[HotLimits.MaxEnemies];
        Span<int> enemyPoisonOrDoom = stackalloc int[HotLimits.MaxEnemies];

        int powerCount = s.PowerCount;
        for (int i = 0; i < powerCount; i++)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.Amount == 0)
                continue;

            ushort defId = p.PowerDefId;
            if (p.OwnerSlot == 0)
            {
                if (defId == lookup.StrengthDefId) playerStrength += p.Amount;
                else if (defId == lookup.DexterityDefId) playerDexterity += p.Amount;
                else if (defId == lookup.FocusDefId) playerFocus += p.Amount;
                else if (defId == lookup.VulnerableDefId) playerVuln += p.Amount;
                else if (defId == lookup.BarricadeDefId) hasBarricade = true;
                else if (defId == lookup.MetallicizeDefId || defId == lookup.PlatingDefId) playerEndTurnBlock += p.Amount;

                playerSetup += ComputePlayerPowerSetupWeight(defId, p.Amount, rules, in lookup);
            }
            else if (p.OwnerSlot >= 3)
            {
                int eSlot = p.OwnerSlot - 3;
                if ((uint)eSlot >= HotLimits.MaxEnemies)
                    continue;
                if ((s.Enemies[eSlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                    continue;

                if (defId == lookup.StrengthDefId)
                {
                    enemyStr[eSlot] += p.Amount;
                    if (p.Amount > 0)
                        enemyThreatPowerPenalty += p.Amount * 2;
                }
                else if (defId == lookup.WeakDefId && p.Amount > 0)
                {
                    enemyWeak[eSlot] = 1;
                    enemyDebuffValue += Math.Min(p.Amount, 4) * 3;
                }
                else if (defId == lookup.VulnerableDefId && p.Amount > 0)
                {
                    enemyDebuffValue += Math.Min(p.Amount, 4) * 4;
                }
                else if (defId == lookup.PoisonDefId && p.Amount > 0)
                {
                    enemyPoisonOrDoom[eSlot] += p.Amount;
                    enemyDebuffValue += Math.Min(p.Amount, 30) * 3;
                }
                else if (defId == lookup.DoomDefId && p.Amount > 0)
                {
                    enemyPoisonOrDoom[eSlot] += p.Amount;
                    enemyDebuffValue += Math.Min(p.Amount, 40) * 2;
                }
                else if (defId == lookup.RitualDefId && p.Amount > 0)
                {
                    enemyThreatPowerPenalty += p.Amount * 4;
                }
            }
        }

        // Scan active alive enemies: incoming attack threat + focus-fire concentration.
        int incomingDamage = 0;
        int lowestEnemyHp = int.MaxValue;
        int rosterCount = s.RosterCount;

        for (int r = 0; r < rosterCount; r++)
        {
            byte eSlot = s.ActiveEnemyRoster[r];
            ref readonly HotEnemy enemy = ref s.Enemies[eSlot];
            if ((enemy.Vital.Flags & HotCreatureFlags.Alive) == 0)
                continue;

            int effectiveEnemyHp = Math.Max(0, enemy.Vital.CurrentHp - enemyPoisonOrDoom[eSlot]);
            if (effectiveEnemyHp < lowestEnemyHp)
                lowestEnemyHp = effectiveEnemyHp;

            if ((enemy.Flags & HotEnemyFlags.SkipNextMove) != 0)
                continue;

            incomingDamage += EstimateEnemyIncomingDamage(
                in enemy,
                rules,
                enemyStr[eSlot],
                enemyWeak[eSlot] != 0,
                playerVuln > 0,
                in lookup);
        }

        // Effective defense buffer = current block + Plating/Metallicize + Wuppy/Osty absorption
        int effectiveDefense = summary.PlayerBlock + playerEndTurnBlock;
        if ((s.CombatFlags & HotCombatFlags.HasWuppy) != 0 && (s.Wuppy.Flags & HotCreatureFlags.Alive) != 0)
            effectiveDefense += s.Wuppy.CurrentHp;
        if ((s.CombatFlags & HotCombatFlags.HasOsty) != 0 && (s.Osty.Flags & HotCreatureFlags.Alive) != 0)
            effectiveDefense += s.Osty.CurrentHp;

        int unblockedIncoming = Math.Max(0, incomingDamage - effectiveDefense);

        // Compute HP score respecting BossHpRelief
        int ongoingDeficit = ActEndingBossPolicy.StrategicHpDeficit(
            summary.HpLost,
            maxHpDelta,
            summary.HpRecovered,
            policy.StrategicBossHpRelief,
            summary.DeathSaveHpRestored) - summary.GrowthHpCredit;

        long runningScore = -(long)ongoingDeficit * HpWeight
            - (long)summary.DeathSaveUseCount * DeathSavePenaltyScore;

        // Threat evaluation: heavy penalty when unblocked incoming damage threatens lethal
        if (unblockedIncoming > 0)
        {
            int scaledThreat = policy.StrategicBossHpRelief switch
            {
                BossHpRelief.RunEnding => unblockedIncoming >= summary.PlayerHp ? unblockedIncoming : unblockedIncoming / 4,
                BossHpRelief.ActClearHeal => unblockedIncoming >= summary.PlayerHp ? unblockedIncoming : (unblockedIncoming + 2) / 5,
                _ => unblockedIncoming,
            };

            if (unblockedIncoming >= summary.PlayerHp)
            {
                // Lethal warning: stronger when player is out of energy to defend.
                runningScore -= summary.Energy == 0 ? 4_000_000L : 800_000L;
            }
            runningScore -= (long)scaledThreat * IncomingThreatWeight;
        }
        else if (hasBarricade && summary.PlayerBlock > incomingDamage)
        {
            // Under Barricade, excess block carries into future turns.
            int carriedBlock = Math.Min(summary.PlayerBlock - incomingDamage, 60);
            runningScore += (long)carriedBlock * 12_000L;
        }

        // Offense, kills & focus-fire pressure
        int killedCount = Math.Max(0, policy.InitialEnemyCount - summary.AliveEnemyCount);
        runningScore += (long)killedCount * EnemyKillBonus;
        runningScore -= (long)summary.EnemyHpSum * EnemyHpWeight;
        runningScore -= (long)summary.EnemyBlockSum * EnemyBlockWeight;
        if (summary.AliveEnemyCount > 1 && lowestEnemyHp < int.MaxValue)
        {
            // Focus-fire bonus: damaging the weakest enemy brings a kill closer.
            int avgHp = summary.EnemyHpSum / summary.AliveEnemyCount;
            int focusGap = Math.Max(0, avgHp - lowestEnemyHp);
            runningScore += (long)Math.Min(focusGap, 60) * 1_200L;
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
            runningScore += (long)Math.Min(s.Osty.CurrentHp, 80) * 3_500L;
            playerSetup += Math.Min(s.Osty.CurrentHp / 5, 10);
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

        // Hand potential & energy/stars
        int handCount = s.HandCount;
        int playableCards = 0;
        int zeroCostPlayable = 0;
        int handClutter = 0;
        for (int h = 0; h < handCount; h++)
        {
            ref readonly HotCard c = ref s.Cards[h];
            HotCardDef def = rules.Cards[c.CardDefId];
            if (def.Type is CardType.Status or CardType.Curse)
            {
                handClutter++;
                continue;
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

        runningScore += (long)Math.Min(summary.Energy, 6) * EnergyWeight;
        runningScore += (long)Math.Min(summary.Stars, 8) * StarWeight;
        runningScore += (long)Math.Min(playableCards, 8) * 14_000L;
        runningScore += (long)Math.Min(zeroCostPlayable, 6) * 8_000L;
        runningScore -= (long)handClutter * 6_000L;

        // Setup & debuff totals
        int totalSetup = Math.Clamp(playerSetup + enemyDebuffValue - enemyThreatPowerPenalty, -64, 64);
        setupScore = totalSetup;
        runningScore += (long)Math.Clamp(totalSetup, -32, 32) * SetupWeightScale;

        // Potion cost, growth, long-term resources, anger copies, theft
        runningScore -= (long)potionStrategicCost * PotionCostWeight;
        runningScore += (long)summary.GrowthHpCredit * 25_000L;
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
                int hit = ScaleEnemyHit(atk.BaseDamage, enemyStrength, enemyWeak, playerVulnerable, in lookup);
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
            int hit = ScaleEnemyHit(node.BaseDamage, enemyStrength, enemyWeak, playerVulnerable, in lookup);
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
        in HotEvalLookup lookup)
    {
        int dmg = baseDamage + enemyStrength;
        if (dmg <= 0)
            return 0;
        if (enemyWeak)
            dmg = lookup.HasPaperKrane ? (dmg * 60) / 100 : (dmg * 75) / 100;
        if (playerVulnerable)
            dmg = lookup.HasOddMushroom ? (dmg * 125) / 100 : (dmg * 150) / 100;
        return Math.Max(0, dmg);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputePlayerPowerSetupWeight(
        ushort defId,
        int amount,
        HotRules rules,
        in HotEvalLookup lookup)
    {
        if (defId < rules.Powers.Length)
        {
            short catalogWeight = rules.Powers[defId].SetupWeight;
            if (catalogWeight != 0)
                return Math.Clamp(catalogWeight * Math.Clamp(amount, -10, 10) / 2, -24, 24);
        }

        int clamped = Math.Clamp(amount, -10, 10);
        if (defId == lookup.StrengthDefId) return clamped * 3;
        if (defId == lookup.DexterityDefId) return clamped * 3;
        if (defId == lookup.FocusDefId) return clamped * 4;
        if (defId == lookup.BarricadeDefId) return 8;
        if (defId == lookup.MetallicizeDefId || defId == lookup.PlatingDefId) return Math.Min(amount, 12) * 2;
        if (defId == lookup.EchoFormDefId) return Math.Min(amount, 3) * 10;
        if (defId == lookup.BufferDefId) return Math.Min(amount, 4) * 6;
        if (defId == lookup.WraithFormDefId) return Math.Min(amount, 5) * 6;
        if (defId == lookup.AfterimageDefId) return Math.Min(amount, 4) * 6;
        if (defId == lookup.DemonFormDefId) return Math.Min(amount, 6) * 5;
        if (defId == lookup.ArtifactDefId) return Math.Min(amount, 4) * 3;
        if (defId == lookup.ThornsDefId) return Math.Min(amount, 8);
        if (defId == lookup.RitualDefId) return Math.Min(amount, 5) * 5;
        if (defId == lookup.WeakDefId || defId == lookup.FrailDefId || defId == lookup.VulnerableDefId)
            return -Math.Min(amount, 4) * 2;

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
