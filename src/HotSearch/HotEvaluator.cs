using System.Collections.Frozen;
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
    DesperateRescue = 3,
}

[Flags]
internal enum HotCardRoleFlags : byte
{
    None = 0,
    HasDraw = 1 << 0,
    HasEnergyOrStarGain = 1 << 1,
    HasEngineOp = 1 << 2,
    HasSelfBuff = 1 << 3,
    HasEnemyDebuff = 1 << 4,
    AppliesWeak = 1 << 5,
    HasBlock = 1 << 6,
    HasSelfHpLoss = 1 << 7,
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
    public readonly short RelicHpCredit;
    public readonly short RelicHealingHpCredit;
    public readonly ushort RelicSatisfiedMask;
    public readonly ushort RelicDistance;
    public readonly ushort RelicSatisfiedPriority;
    public readonly byte FeedCount;
    public readonly bool AllGrowthTargetsSatisfied;
    public readonly bool AllRelicTargetsSatisfied;

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
        HotCombatOutcome outcome,
        short relicHpCredit = 0,
        short relicHealingHpCredit = 0,
        ushort relicSatisfiedMask = 0,
        ushort relicDistance = 0,
        ushort relicSatisfiedPriority = 0,
        byte feedCount = 0,
        bool allGrowthTargetsSatisfied = true,
        bool allRelicTargetsSatisfied = true)
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
        RelicHpCredit = relicHpCredit;
        RelicHealingHpCredit = relicHealingHpCredit;
        RelicSatisfiedMask = relicSatisfiedMask;
        RelicDistance = relicDistance;
        RelicSatisfiedPriority = relicSatisfiedPriority;
        FeedCount = feedCount;
        AllGrowthTargetsSatisfied = allGrowthTargetsSatisfied;
        AllRelicTargetsSatisfied = allRelicTargetsSatisfied;
    }

    public bool IsVictory => Outcome == HotCombatOutcome.Victory && PlayerHp > 0;
    public bool IsDefeat => Outcome == HotCombatOutcome.Defeat || PlayerHp <= 0;
    public bool IsEventDefeat => Outcome == HotCombatOutcome.EventDefeat && PlayerHp > 0;
    public int StrategyGoalHpCredit => GrowthHpCredit + RelicHpCredit;
    public int StrategicHpCredit => GrowthHpCredit + RelicHpCredit + RelicHealingHpCredit;
    public int StrategyGoalCount => GrowthRewardCount + RelicSatisfiedPriority;
    public bool AllStrategyTargetsSatisfied => AllGrowthTargetsSatisfied && AllRelicTargetsSatisfied;
}

/// <summary>
/// Zero-allocation Stage 2 evaluation outputs consumed by <see cref="HotSearchNode"/>,
/// <see cref="HotBeamRetention"/>'s 5 domain-agnostic MAP-Elites niches, and unclamped turn-progress tracking.
/// </summary>
internal readonly struct HotEvalFeatures
{
    public readonly long Score;
    public readonly int SetupScore;
    public readonly int BoardInvestment;
    public readonly int MaxDarkEvoke;
    public readonly int TotalPoisonAndDoom;
    public readonly short EffectiveDefense;
    public readonly short OptionalityScore;
    public readonly byte PlayableHandCount;

    public HotEvalFeatures(
        long score,
        int setupScore,
        int boardInvestment,
        int maxDarkEvoke,
        int totalPoisonAndDoom,
        short effectiveDefense,
        short optionalityScore,
        byte playableHandCount)
    {
        Score = score;
        SetupScore = setupScore;
        BoardInvestment = boardInvestment;
        MaxDarkEvoke = maxDarkEvoke;
        TotalPoisonAndDoom = totalPoisonAndDoom;
        EffectiveDefense = effectiveDefense;
        OptionalityScore = optionalityScore;
        PlayableHandCount = playableHandCount;
    }
}

/// <summary>
/// Certified HP incumbent bound in exact centi-HP frozen at the start of each wave so DOP=1 and DOP=N
/// make 100% deterministic identical pruning decisions.
/// Preserves exact tie-break priority (<c>V = NetPersistentHpDeltaCenti - NetPotionCostCenti</c> first,
/// then <c>NetPersistentHpDeltaCenti</c>, then <c>ProjectedBattleHpLost</c>, then <c>ProjectedBattlePotionCount</c>,
/// then <c>EndTurn</c>) so Stage 2 potion winners and 0-potion branches are NEVER unsoundly pruned!
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
    public readonly int BestZeroPotionBattleHpLost;
    public readonly int BestOverallBattleHpLost;
    public readonly int BestOverallPotionCount;

    public HotIncumbentBound(
        bool hasCertifiedZeroPotionBound,
        int bestZeroPotionHpDeltaCenti,
        int bestZeroPotionEndTurn,
        bool hasCertifiedOverallBound,
        long bestOverallValueCenti,
        int bestOverallHpDeltaCenti,
        int bestOverallEndTurn,
        int bestZeroPotionBattleHpLost = int.MaxValue,
        int bestOverallBattleHpLost = int.MaxValue,
        int bestOverallPotionCount = int.MaxValue)
    {
        HasCertifiedZeroPotionBound = hasCertifiedZeroPotionBound;
        BestZeroPotionHpDeltaCenti = bestZeroPotionHpDeltaCenti;
        BestZeroPotionEndTurn = bestZeroPotionEndTurn;
        HasCertifiedOverallBound = hasCertifiedOverallBound;
        BestOverallValueCenti = bestOverallValueCenti;
        BestOverallHpDeltaCenti = bestOverallHpDeltaCenti;
        BestOverallEndTurn = bestOverallEndTurn;
        BestZeroPotionBattleHpLost = bestZeroPotionBattleHpLost;
        BestOverallBattleHpLost = bestOverallBattleHpLost;
        BestOverallPotionCount = bestOverallPotionCount;
    }

    public static HotIncumbentBound None => new(false, 0, 0, false, 0, 0, 0, int.MaxValue, int.MaxValue, int.MaxValue);
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
    public readonly ushort AccelerantDefId;
    public readonly ushort DoomDefId;
    public readonly ushort EchoFormDefId;
    public readonly ushort BufferDefId;
    public readonly ushort WraithFormDefId;
    public readonly ushort IntangibleDefId;
    public readonly ushort SlipperyDefId;
    public readonly ushort HardToKillDefId;
    public readonly ushort HardenedShellDefId;
    public readonly ushort AfterimageDefId;
    public readonly ushort DemonFormDefId;
    public readonly ushort BlockNextTurnDefId;
    public readonly ushort EnergyNextTurnDefId;
    public readonly ushort DrawCardsNextTurnDefId;
    public readonly ushort StarNextTurnDefId;
    public readonly ushort SummonNextTurnDefId;
    public readonly ushort ReattachDefId;
    public readonly ushort AdaptableDefId;
    public readonly ushort IllusionDefId;
    public readonly ushort SandpitDefId;
    public readonly ushort FurnaceDefId;
    public readonly ushort SelfFormingClayDefId;
    public readonly ushort ToricToughnessDefId;
    public readonly ushort VigorDefId;
    public readonly ushort PaperCutsDefId;
    public readonly ushort NightmareDefId;
    public readonly ushort RetainHandDefId;
    public readonly ushort WellLaidPlansDefId;
    public readonly ushort CorruptionDefId;
    public readonly ushort HexDefId;
    public readonly ushort SeekingEdgeDefId;
    public readonly ushort ParryDefId;
    public readonly ushort NoDrawDefId;
    public readonly ushort DisintegrationDefId;
    public readonly ushort ConstrictDefId;
    public readonly ushort DemiseDefId;
    public readonly ushort MinionDefId;
    public readonly ushort SovereignBladeDefId;
    public readonly ushort TheHuntCardDefId;
    public readonly ushort TheHuntPowerDefId;
    public readonly ushort BurnCardDefId;
    public readonly ushort DecayCardDefId;
    public readonly ushort BeckonCardDefId;
    public readonly ushort FeedCardDefId;
    public readonly ushort HandOfGreedCardDefId;
    public readonly ushort LessonLearnedCardDefId;
    public readonly ushort RitualDaggerCardDefId;
    public readonly byte BeckonHpLoss;
    public readonly byte PaelsEyeStateSlot;
    public readonly byte PaelsLegionStateSlot;
    public readonly byte ArtOfWarStateSlot;
    public readonly byte PocketwatchStateSlot;
    public readonly bool HasPaperKrane;
    public readonly bool HasOddMushroom;
    public readonly bool HasPaperPhrog;
    public readonly bool HasGoldPlatedCables;
    public readonly bool HasRunicPyramid;
    public readonly HotCardRoleFlags[] CardRoles;

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
        AccelerantDefId = rules.GetPowerDefId(HotPowerId.AccelerantPower);
        DoomDefId = rules.GetPowerDefId(HotPowerId.DoomPower);
        EchoFormDefId = rules.GetPowerDefId(HotPowerId.EchoFormPower);
        BufferDefId = rules.GetPowerDefId(HotPowerId.BufferPower);
        WraithFormDefId = rules.GetPowerDefId(HotPowerId.WraithFormPower);
        IntangibleDefId = rules.GetPowerDefId(HotPowerId.IntangiblePower);
        SlipperyDefId = rules.GetPowerDefId(HotPowerId.SlipperyPower);
        HardToKillDefId = rules.GetPowerDefId(HotPowerId.HardToKillPower);
        HardenedShellDefId = rules.GetPowerDefId(HotPowerId.HardenedShellPower);
        AfterimageDefId = rules.GetPowerDefId(HotPowerId.AfterimagePower);
        DemonFormDefId = rules.GetPowerDefId(HotPowerId.DemonFormPower);
        BlockNextTurnDefId = rules.GetPowerDefId(HotPowerId.BlockNextTurnPower);
        EnergyNextTurnDefId = rules.GetPowerDefId(HotPowerId.EnergyNextTurnPower);
        DrawCardsNextTurnDefId = rules.GetPowerDefId(HotPowerId.DrawCardsNextTurnPower);
        StarNextTurnDefId = rules.GetPowerDefId(HotPowerId.StarNextTurnPower);
        SummonNextTurnDefId = rules.GetPowerDefId(HotPowerId.SummonNextTurnPower);
        ReattachDefId = rules.GetPowerDefId(HotPowerId.ReattachPower);
        AdaptableDefId = rules.GetPowerDefId(HotPowerId.AdaptablePower);
        IllusionDefId = rules.GetPowerDefId(HotPowerId.IllusionPower);
        SandpitDefId = rules.GetPowerDefId(HotPowerId.SandpitPower);
        FurnaceDefId = rules.GetPowerDefId(HotPowerId.FurnacePower);
        SelfFormingClayDefId = rules.GetPowerDefId(HotPowerId.SelfFormingClayPower);
        ToricToughnessDefId = rules.GetPowerDefId(HotPowerId.ToricToughnessPower);
        VigorDefId = rules.GetPowerDefId(HotPowerId.VigorPower);
        PaperCutsDefId = rules.GetPowerDefId(HotPowerId.PaperCutsPower);
        NightmareDefId = rules.GetPowerDefId(HotPowerId.NightmarePower);
        RetainHandDefId = rules.GetPowerDefId(HotPowerId.RetainHandPower);
        WellLaidPlansDefId = rules.GetPowerDefId(HotPowerId.WellLaidPlansPower);
        CorruptionDefId = rules.GetPowerDefId(HotPowerId.CorruptionPower);
        HexDefId = rules.GetPowerDefId(HotPowerId.HexPower);
        SeekingEdgeDefId = rules.GetPowerDefId(HotPowerId.SeekingEdgePower);
        ParryDefId = rules.GetPowerDefId(HotPowerId.ParryPower);
        NoDrawDefId = rules.GetPowerDefId(HotPowerId.NoDrawPower);
        DisintegrationDefId = rules.GetPowerDefId(HotPowerId.DisintegrationPower);
        ConstrictDefId = rules.GetPowerDefId(HotPowerId.ConstrictPower);
        DemiseDefId = rules.GetPowerDefId(HotPowerId.DemisePower);
        MinionDefId = HotEvaluator.TryGetPowerDefIdByName(rules, "MinionPower");
        SovereignBladeDefId = rules.GetCardDefId(HotCustomCardId.SovereignBlade);
        TheHuntCardDefId = rules.GetCardDefId(HotCustomCardId.TheHunt);
        TheHuntPowerDefId = rules.GetPowerDefId(HotPowerId.TheHuntPower);
        BurnCardDefId = rules.FindCardDefId("BURN");
        DecayCardDefId = rules.FindCardDefId("DECAY");
        BeckonCardDefId = rules.FindCardDefId("BECKON");
        FeedCardDefId = rules.FindCardDefId("FEED");
        HandOfGreedCardDefId = rules.FindCardDefId("HAND_OF_GREED");
        LessonLearnedCardDefId = rules.FindCardDefId("LESSON_LEARNED");
        RitualDaggerCardDefId = rules.FindCardDefId("RITUAL_DAGGER");
        BeckonHpLoss = HotEvaluator.ResolveCardHpLoss(rules, BeckonCardDefId, fallback: 6);
        PaelsEyeStateSlot = rules.HasRelic(HotRelicId.PaelsEye) ? rules.GetRelicStateSlot(HotRelicId.PaelsEye) : (byte)0xFF;
        PaelsLegionStateSlot = rules.HasRelic(HotRelicId.PaelsLegion) ? rules.GetRelicStateSlot(HotRelicId.PaelsLegion) : (byte)0xFF;
        ArtOfWarStateSlot = rules.HasRelic(HotRelicId.ArtOfWar) ? rules.GetRelicStateSlot(HotRelicId.ArtOfWar) : (byte)0xFF;
        PocketwatchStateSlot = rules.HasRelic(HotRelicId.Pocketwatch) ? rules.GetRelicStateSlot(HotRelicId.Pocketwatch) : (byte)0xFF;
        HasPaperKrane = rules.HasPaperKrane;
        HasOddMushroom = rules.HasOddMushroom;
        HasPaperPhrog = rules.HasPaperPhrog;
        HasGoldPlatedCables = rules.HasRelic(HotRelicId.GoldPlatedCables);
        HasRunicPyramid = rules.HasRelic(HotRelicId.RunicPyramid);
        CardRoles = HotEvaluator.BuildCardRoles(rules);
    }
}

/// <summary>
/// 2-stage zero-allocation evaluator over <see cref="CombatHotState"/> aligned 1:1 with
/// <see cref="HotRouteQuality"/> and <see cref="HotPotionPolicy"/>'s unified centi-HP objective.
/// </summary>
internal static class HotEvaluator
{
    private const long DeathBaseScore = -1_000_000_000_000L;
    private const long EventDefeatBaseScore = -500_000_000_000L;
    private const long VictoryBaseScore = 10_000_000_000L;
    private const long DeathSavePenaltyScore = 500_000_000L;
    private const long HpWeightPerCenti = 390L;       // 39_000L per 1.00 Persistent HP
    private const long EnemyHpWeight = 10_000L;
    private const long EnemyBlockWeight = 2_000L;
    private const long EnemyKillBonus = 110_000L;
    private const long IncomingThreatWeight = 34_000L;
    private const long SetupWeightScale = 16_500L;
    private const long EnergyWeight = 55_000L;

    private static readonly FrozenDictionary<string, HotPowerId> PowerIdByName = BuildPowerIdByName();

    private static FrozenDictionary<string, HotPowerId> BuildPowerIdByName()
    {
        Dictionary<string, HotPowerId> map = new((int)HotPowerId.Count, StringComparer.Ordinal);
        for (int i = 1; i < (int)HotPowerId.Count; i++)
        {
            HotPowerId id = (HotPowerId)i;
            map[id.ToString()] = id;
        }
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    internal static ushort TryGetPowerDefIdByName(HotRules rules, string powerName)
        => PowerIdByName.TryGetValue(powerName, out HotPowerId powerId)
            ? rules.GetPowerDefId(powerId)
            : (ushort)0;

    internal static byte ResolveCardHpLoss(HotRules rules, ushort cardDefId, byte fallback)
    {
        if (cardDefId == 0 || cardDefId >= rules.Cards.Length)
            return fallback;
        HotCardDef? def = rules.Cards[cardDefId];
        if (def == null)
            return fallback;
        string[] varNames = def.OrderedVarNames;
        for (int i = 0; i < varNames.Length && i < def.BaseVarValues.Length; i++)
        {
            if (string.Equals(varNames[i], "HpLoss", StringComparison.Ordinal))
            {
                int val = (int)def.BaseVarValues[i];
                if (val > 0)
                    return (byte)Math.Min(val, byte.MaxValue);
            }
        }
        return fallback;
    }

    internal static HotCardRoleFlags[] BuildCardRoles(HotRules rules)
    {
        HotCardDef[] cards = rules.Cards;
        HotCardRoleFlags[] roles = new HotCardRoleFlags[cards.Length];
        for (int i = 1; i < cards.Length; i++)
            roles[i] = ComputeCardRoleFlags(rules, (ushort)i);
        return roles;
    }

    private static HotCardRoleFlags ComputeCardRoleFlags(HotRules rules, ushort cardDefId)
    {
        HotCardDef? def = rules.Cards[cardDefId];
        if (def == null)
            return HotCardRoleFlags.None;

        HotCardRoleFlags flags = HotCardRoleFlags.None;
        bool hasNextTurnEnergyOp = false;
        bool hasNextTurnDrawOp = false;

        // 1. Compiled bytecode ops (exact parity with HotRolloutEvaluator).
        ReadOnlySpan<HotCardEffectOp> ops = rules.GetCardOps(cardDefId);
        for (int k = 0; k < ops.Length; k++)
        {
            switch (ops[k].Op)
            {
                case HotCardOpKind.Draw:
                    flags |= HotCardRoleFlags.HasDraw;
                    break;
                case HotCardOpKind.GainEnergy:
                case HotCardOpKind.GainStars:
                    flags |= HotCardRoleFlags.HasEnergyOrStarGain;
                    break;
                case HotCardOpKind.ChannelOrb:
                case HotCardOpKind.EvokeOrb:
                case HotCardOpKind.SummonOsty:
                case HotCardOpKind.Forge:
                case HotCardOpKind.ShootArrow:
                    flags |= HotCardRoleFlags.HasEngineOp;
                    break;
                case HotCardOpKind.ApplyPowerSelf:
                {
                    ushort pDefId = (ushort)ops[k].Arg0;
                    if (pDefId < rules.Powers.Length)
                    {
                        HotPowerDef pDef = rules.Powers[pDefId];
                        if (pDef.Type != PowerType.Debuff)
                            flags |= HotCardRoleFlags.HasSelfBuff;
                        if (pDef.PowerId is HotPowerId.EnergyNextTurnPower or HotPowerId.StarNextTurnPower)
                            hasNextTurnEnergyOp = true;
                        else if (pDef.PowerId == HotPowerId.DrawCardsNextTurnPower)
                            hasNextTurnDrawOp = true;
                    }
                    else
                    {
                        flags |= HotCardRoleFlags.HasSelfBuff;
                    }
                    break;
                }
                case HotCardOpKind.ApplyTempStat:
                    flags |= HotCardRoleFlags.HasSelfBuff;
                    break;
                case HotCardOpKind.ApplyPowerTarget:
                case HotCardOpKind.ApplyPowerAllEnemies:
                {
                    flags |= HotCardRoleFlags.HasEnemyDebuff;
                    ushort pDefId = (ushort)ops[k].Arg0;
                    if (pDefId < rules.Powers.Length && rules.Powers[pDefId].PowerId == HotPowerId.WeakPower)
                        flags |= HotCardRoleFlags.AppliesWeak;
                    break;
                }
                case HotCardOpKind.GainBlock:
                    flags |= HotCardRoleFlags.HasBlock;
                    break;
                case HotCardOpKind.LoseHp:
                    flags |= HotCardRoleFlags.HasSelfHpLoss;
                    break;
            }
        }

        // 2. Block, power, and tag metadata on HotCardDef.
        if (def.GainsBlock || def.BaseBlock > 0 || def.UpgradedBlock > 0)
            flags |= HotCardRoleFlags.HasBlock;
        if (def.Type == CardType.Power || def.SetupWeight > 0)
            flags |= HotCardRoleFlags.HasSelfBuff;
        if (def.IsShootTag || def.CalcVar == HotCalcVarKind.BeatIntoShape)
            flags |= HotCardRoleFlags.HasEngineOp;

        // 3. Semantic DynamicVar names (covers custom-handler cards with OpCount == 0).
        string[] varNames = def.OrderedVarNames;
        if (varNames.Length > 0)
        {
            if (def.Type is CardType.Status or CardType.Curse)
            {
                for (int v = 0; v < varNames.Length; v++)
                {
                    if (string.Equals(varNames[v], "HpLoss", StringComparison.Ordinal))
                        flags |= HotCardRoleFlags.HasSelfHpLoss;
                }
            }
            else
            {
                bool targetsEnemy = def.Target is TargetType.AnyEnemy or TargetType.AllEnemies or TargetType.RandomEnemy;
                bool hasNextTurnEnergyVar = hasNextTurnEnergyOp;
                bool hasNextTurnDrawVar = hasNextTurnDrawOp;
                for (int v = 0; v < varNames.Length; v++)
                {
                    string vn = varNames[v];
                    if (vn is "EnergyNextTurnPower" or "StarNextTurnPower")
                        hasNextTurnEnergyVar = true;
                    else if (vn == "DrawCardsNextTurnPower")
                        hasNextTurnDrawVar = true;
                }

                for (int v = 0; v < varNames.Length; v++)
                {
                    string name = varNames[v];
                    switch (name)
                    {
                        case "Cards":
                        case "WisdomCards":
                        case "Draw":
                            if (!hasNextTurnDrawVar)
                                flags |= HotCardRoleFlags.HasDraw;
                            break;
                        case "Energy":
                        case "EnergizedEnergy":
                            if (!hasNextTurnEnergyVar)
                                flags |= HotCardRoleFlags.HasEnergyOrStarGain;
                            break;
                        case "Stars":
                            flags |= HotCardRoleFlags.HasEnergyOrStarGain;
                            break;
                        case "Forge":
                        case "Summon":
                        case "OrbSlots":
                        case "Shivs":
                            flags |= HotCardRoleFlags.HasEngineOp;
                            break;
                        case "Block":
                            flags |= HotCardRoleFlags.HasBlock;
                            break;
                        case "BlockNextTurn":
                            flags |= HotCardRoleFlags.HasSelfBuff;
                            break;
                        case "HpLoss":
                            flags |= HotCardRoleFlags.HasSelfHpLoss;
                            break;
                        case "Weak":
                        case "SappingWeak":
                            flags |= HotCardRoleFlags.HasEnemyDebuff | HotCardRoleFlags.AppliesWeak;
                            break;
                        case "Vulnerable":
                        case "SappingVulnerable":
                        case "Poison":
                        case "Doom":
                        case "StrengthLoss":
                        case "EnemyStrengthLoss":
                            flags |= HotCardRoleFlags.HasEnemyDebuff;
                            break;
                        case "ExpertiseStrength":
                        case "ExpertiseDexterity":
                        case "BaseAim":
                        case "DamageBack":
                            flags |= HotCardRoleFlags.HasSelfBuff;
                            break;
                        default:
                            if (PowerIdByName.TryGetValue(name, out HotPowerId powerId))
                            {
                                if (powerId == HotPowerId.WeakPower)
                                {
                                    if (targetsEnemy)
                                        flags |= HotCardRoleFlags.HasEnemyDebuff | HotCardRoleFlags.AppliesWeak;
                                }
                                else if (powerId is HotPowerId.EnergyNextTurnPower
                                    or HotPowerId.StarNextTurnPower
                                    or HotPowerId.DrawCardsNextTurnPower
                                    or HotPowerId.BlockNextTurnPower)
                                {
                                    flags |= HotCardRoleFlags.HasSelfBuff;
                                }
                                else
                                {
                                    PowerType pType = HotPowerCatalog.Get(powerId).Type;
                                    if (pType == PowerType.Debuff)
                                    {
                                        if (targetsEnemy)
                                            flags |= HotCardRoleFlags.HasEnemyDebuff;
                                    }
                                    else
                                    {
                                        bool isNegativeBase = v < def.BaseVarValues.Length && def.BaseVarValues[v] < 0m;
                                        if (def.CardId == HotCustomCardId.Hyperbeam && powerId == HotPowerId.FocusPower)
                                            break;
                                        if (isNegativeBase)
                                        {
                                            if (targetsEnemy)
                                                flags |= HotCardRoleFlags.HasEnemyDebuff;
                                        }
                                        else
                                        {
                                            flags |= HotCardRoleFlags.HasSelfBuff;
                                        }
                                    }
                                }
                            }
                            break;
                    }
                }
            }
        }

        // 4. Custom-handler cards whose primary role has no dedicated DynamicVar key.
        if (ops.Length == 0)
        {
            switch (def.CardId)
            {
                case HotCustomCardId.Zap:
                case HotCustomCardId.Dualcast:
                case HotCustomCardId.Glacier:
                case HotCustomCardId.Chill:
                case HotCustomCardId.Chaos:
                case HotCustomCardId.Darkness:
                case HotCustomCardId.Fusion:
                case HotCustomCardId.Rainbow:
                case HotCustomCardId.Tempest:
                case HotCustomCardId.MultiCast:
                case HotCustomCardId.Quadcast:
                case HotCustomCardId.Glasswork:
                case HotCustomCardId.IceLance:
                case HotCustomCardId.MeteorStrike:
                case HotCustomCardId.Voltaic:
                case HotCustomCardId.Refract:
                case HotCustomCardId.Shatter:
                case HotCustomCardId.TeslaCoil:
                case HotCustomCardId.BladeDance:
                case HotCustomCardId.BladeOfInk:
                case HotCustomCardId.CloakAndDagger:
                case HotCustomCardId.HiddenDaggers:
                case HotCustomCardId.StormOfSteel:
                    flags |= HotCardRoleFlags.HasEngineOp;
                    break;
                case HotCustomCardId.DoubleEnergy:
                case HotCustomCardId.Turbo:
                    flags |= HotCardRoleFlags.HasEnergyOrStarGain;
                    break;
                case HotCustomCardId.CalculatedGamble:
                case HotCustomCardId.DaggerThrow:
                case HotCustomCardId.EscapePlan:
                case HotCustomCardId.Pillage:
                case HotCustomCardId.Scrawl:
                case HotCustomCardId.MasterOfStrategy:
                case HotCustomCardId.Acrobatics:
                case HotCustomCardId.BurningPact:
                case HotCustomCardId.Skim:
                    flags |= HotCardRoleFlags.HasDraw;
                    break;
                case HotCustomCardId.Uppercut:
                case HotCustomCardId.Malaise:
                case HotCustomCardId.Shockwave:
                case HotCustomCardId.CrushUnder:
                case HotCustomCardId.DyingStar:
                    flags |= HotCardRoleFlags.HasEnemyDebuff | HotCardRoleFlags.AppliesWeak;
                    break;
                case HotCustomCardId.Expose:
                case HotCustomCardId.Dominate:
                case HotCustomCardId.MoltenFist:
                    flags |= HotCardRoleFlags.HasEnemyDebuff;
                    break;
                case HotCustomCardId.DodgeAndRoll:
                case HotCustomCardId.Prolong:
                    flags |= HotCardRoleFlags.HasSelfBuff;
                    break;
                case HotCustomCardId.Entrench:
                    flags |= HotCardRoleFlags.HasBlock;
                    break;
            }
        }

        return flags;
    }

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

        bool dummyTimedOut = (s.CombatFlags & HotCombatFlags.BattlewornDummyTimedOut) != 0;
        if (dummyTimedOut)
        {
            // Preserve un-dealt HP of escaped BattlewornDummy so unwinnable fallbacks maximize real damage dealt.
            int enemyCount = Math.Min((int)s.EnemyCount, HotLimits.MaxEnemies);
            for (int e = 0; e < enemyCount; e++)
            {
                ref readonly HotEnemy enemy = ref s.Enemies[e];
                if ((enemy.Vital.Flags & HotCreatureFlags.Escaped) != 0)
                    enemyHpSum += Math.Max(0, enemy.Vital.CurrentHp);
            }
        }

        HotCombatOutcome outcome = s.TerminalOutcome;
        if ((s.Player.Flags & HotCreatureFlags.Alive) == 0 || s.Player.CurrentHp <= 0)
        {
            outcome = HotCombatOutcome.Defeat;
        }
        else if (dummyTimedOut)
        {
            outcome = HotCombatOutcome.EventDefeat;
        }
        else if (outcome == HotCombatOutcome.Ongoing && aliveEnemies == 0 && revivingEnemies == 0)
        {
            outcome = HotCombatOutcome.Victory;
        }

        short relicHpCredit = 0;
        short relicHealingHpCredit = 0;
        ushort relicSatisfiedMask = 0;
        ushort relicDistance = 0;
        ushort relicSatisfiedPriority = 0;
        bool allRelicSatisfied = true;
        if (policy.HasRelicTargets)
        {
            bool isVictory = outcome == HotCombatOutcome.Victory && s.Player.CurrentHp > 0;
            RelicCounterEvaluation re = policy.EvaluateRelicCounters(in s, won: isVictory);
            relicHpCredit = (short)Math.Clamp(re.HpCredit, short.MinValue, short.MaxValue);
            relicHealingHpCredit = (short)Math.Clamp(re.HealingHpCredit, short.MinValue, short.MaxValue);
            relicSatisfiedMask = (ushort)re.SatisfiedMask;
            relicDistance = (ushort)Math.Clamp(re.Distance, 0, ushort.MaxValue);
            relicSatisfiedPriority = (ushort)Math.Clamp(re.SatisfiedPriority, 0, ushort.MaxValue);
            allRelicSatisfied = policy.AreAllRelicTargetsSatisfied(relicSatisfiedMask);
        }

        bool allGrowthSatisfied = !policy.HasPureGrowthTargets
            || policy.AreBoundedGrowthTargetsSatisfied(in s);

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
            outcome: outcome,
            relicHpCredit: relicHpCredit,
            relicHealingHpCredit: relicHealingHpCredit,
            relicSatisfiedMask: relicSatisfiedMask,
            relicDistance: relicDistance,
            relicSatisfiedPriority: relicSatisfiedPriority,
            feedCount: (byte)Math.Clamp((int)s.Growth[2], 0, byte.MaxValue),
            allGrowthTargetsSatisfied: allGrowthSatisfied,
            allRelicTargetsSatisfied: allRelicSatisfied);
    }

    /// <summary>
    /// Certified centi-HP incumbent bound check right after Stage 1 (before Stage 2 scoring).
    /// Sound only when <c>!policy.HasAnyHealOrUnknownGeneration</c> and no growth/forced/theft trade-offs override HP.
    /// Respects exact tie-break order (<c>V</c> -> <c>NetPersistentHpDeltaCenti</c> -> <c>ProjectedBattleHpLost</c> -> <c>ProjectedBattlePotionCount</c> -> <c>Turn</c>)
    /// so Stage 2 potion routes tying on <c>V</c> while winning on <c>NetPersistentHpDeltaCenti</c> or using fewer potions are NEVER pruned.
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
        if (summary.IsDefeat || summary.IsEventDefeat)
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
            ignoreLongTermRewards: policy.IgnoreLongTermRewards,
            feedMaxHpExcluded: summary.FeedCount * policy.FeedMaxHpPerKill);

        long projectedBattleHpLost = (long)policy.BattleHpLostSoFar + summary.HpLost;

        // Zero-potion certified bound (only applies to 0-potion branches when no paid potion has been spent yet)
        if (optionalPaidCount == 0 && explicitPotionCount == 0 && bound.HasCertifiedZeroPotionBound)
        {
            if (summary.DeathSaveUseCount > 0)
                return true;
            if (upperBoundHpDeltaCenti < bound.BestZeroPotionHpDeltaCenti)
                return true;
            if (upperBoundHpDeltaCenti == bound.BestZeroPotionHpDeltaCenti
                && !summary.IsVictory
                && projectedBattleHpLost >= bound.BestZeroPotionBattleHpLost
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
                // Tie on V is broken by NetPersistentHpDeltaCenti DESC -> BattleHpLost ASC -> PotionCount ASC -> Turn ASC!
                if (upperBoundHpDeltaCenti < bound.BestOverallHpDeltaCenti)
                    return true;
                int projectedBattlePotions = policy.BattlePotionsUsedSoFar + explicitPotionCount;
                if (upperBoundHpDeltaCenti == bound.BestOverallHpDeltaCenti
                    && !summary.IsVictory
                    && projectedBattleHpLost >= bound.BestOverallBattleHpLost
                    && (projectedBattleHpLost > bound.BestOverallBattleHpLost
                        || projectedBattlePotions > bound.BestOverallPotionCount
                        || (projectedBattlePotions == bound.BestOverallPotionCount && summary.Turn > bound.BestOverallEndTurn)))
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
        out HotEvalFeatures features)
    {
        // 1. Terminal Defeat
        if (summary.IsDefeat)
        {
            long defeatScore = DeathBaseScore
                + (long)summary.Turn * 100_000L
                - (long)summary.EnemyHpSum * 100L
                - (long)(summary.AliveEnemyCount + summary.RevivingEnemyCount) * 50_000L;
            features = new HotEvalFeatures(
                score: defeatScore,
                setupScore: 0,
                boardInvestment: 0,
                maxDarkEvoke: 0,
                totalPoisonAndDoom: 0,
                effectiveDefense: 0,
                optionalityScore: 0,
                playableHandCount: 0);
            return;
        }

        // 1b. Terminal EventDefeat (e.g. BattlewornDummy timed out & escaped): strictly below Victory/Ongoing, above player death.
        if (summary.IsEventDefeat)
        {
            long eventDefeatScore = EventDefeatBaseScore
                - (long)summary.DeathSaveUseCount * DeathSavePenaltyScore
                - (long)summary.EnemyHpSum * EnemyHpWeight
                - (long)summary.HpLost * 50_000L
                - (long)netPotionCostCenti * HpWeightPerCenti;
            features = new HotEvalFeatures(
                score: eventDefeatScore,
                setupScore: 0,
                boardInvestment: 0,
                maxDarkEvoke: 0,
                totalPoisonAndDoom: 0,
                effectiveDefense: 0,
                optionalityScore: 0,
                playableHandCount: 0);
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
                ignoreLongTermRewards: policy.IgnoreLongTermRewards,
                relicHpCredit: summary.RelicHpCredit,
                relicHealingHpCredit: summary.RelicHealingHpCredit,
                feedMaxHpExcluded: summary.FeedCount * policy.FeedMaxHpPerKill);

            long vCenti = (long)netHpDeltaCenti - netPotionCostCenti;

            long victoryScore = VictoryBaseScore
                - (long)summary.DeathSaveUseCount * DeathSavePenaltyScore
                + vCenti * HpWeightPerCenti
                + (long)netHpDeltaCenti * 2L
                - (long)summary.Turn * 5_000L
                - (long)actionCount * 10L;

            if (!policy.IgnoreLongTermRewards && summary.GrowthRewardCount > 0)
            {
                victoryScore += (long)summary.GrowthRewardCount
                    * (policy.HasPureGrowthTargets ? 25_000L : 5_000L);
            }

            if (policy.HasRelicTargets)
            {
                victoryScore += ComputeRelicGuidanceScore(
                    summary.RelicSatisfiedPriority,
                    summary.RelicDistance);
            }

            if (policy.TheftPolicy == SolverTheftPolicy.PreserveResources)
            {
                victoryScore -= (long)summary.OutstandingStolen * 1_000_000L;
                if (summary.ForfeitsGold)
                    victoryScore -= 2_000_000L;
            }

            features = new HotEvalFeatures(
                score: victoryScore,
                setupScore: 0,
                boardInvestment: 0,
                maxDarkEvoke: 0,
                totalPoisonAndDoom: 0,
                effectiveDefense: (short)Math.Clamp(summary.PlayerBlock, 0, short.MaxValue),
                optionalityScore: 0,
                playableHandCount: 0);
            return;
        }

        // Mode weights (integer math for zero float variance)
        long hpWeightPerCenti;
        long enemyHpWeight;
        long threatWeight;
        long setupScale;
        long carryoverMult;
        long deathSavePenalty;
        switch (evalMode)
        {
            case HotEvalMode.SetupAndSurvival:
                hpWeightPerCenti = 420L;
                enemyHpWeight = 9_500L;
                threatWeight = 36_000L;
                setupScale = 21_500L;
                carryoverMult = 125L;
                deathSavePenalty = DeathSavePenaltyScore;
                break;
            case HotEvalMode.Offense:
                hpWeightPerCenti = 300L;
                enemyHpWeight = 13_500L;
                threatWeight = 25_000L;
                setupScale = 15_000L;
                carryoverMult = 100L;
                deathSavePenalty = 1_500_000L;
                break;
            case HotEvalMode.DesperateRescue:
                hpWeightPerCenti = 240L;
                enemyHpWeight = 10_000L;
                threatWeight = 24_000L;
                setupScale = 30_000L;
                carryoverMult = 135L;
                deathSavePenalty = 1_500_000L;
                break;
            default: // HotEvalMode.Balanced
                hpWeightPerCenti = HpWeightPerCenti;
                enemyHpWeight = EnemyHpWeight;
                threatWeight = IncomingThreatWeight;
                setupScale = SetupWeightScale;
                carryoverMult = 100L;
                deathSavePenalty = DeathSavePenaltyScore;
                break;
        }

        long ongoingHpWeight = hpWeightPerCenti;
        long nonLethalThreatWeight = threatWeight;
        if (policy.StrategicBossHpRelief != BossHpRelief.None)
        {
            if (evalMode == HotEvalMode.Offense)
            {
                ongoingHpWeight = (hpWeightPerCenti * 55L) / 100L;
                nonLethalThreatWeight = (threatWeight * 55L) / 100L;
            }
            else if (evalMode == HotEvalMode.DesperateRescue)
            {
                ongoingHpWeight = (hpWeightPerCenti * 40L) / 100L;
                nonLethalThreatWeight = (threatWeight * 40L) / 100L;
            }
        }

        // 3. Ongoing combat: single-pass scan over powers, enemies, orbs, hand, and resources.
        int playerVuln = 0;
        int playerIntangible = 0;
        int playerBuffer = 0;
        int playerDisintegration = 0;
        int playerConstrict = 0;
        int playerDemise = 0;
        int playerAccelerant = 0;
        int playerEndTurnBlock = 0;
        int playerRetainHand = 0;
        bool hasPlayerCorruption = false;
        bool hasBladeSynergy = false;
        int noDrawSetupPenalty = 0;
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
        int playerBuffStacks = 0;
        int enemyDebuffStacks = 0;
        int totalPoisonAndDoom = 0;
        int minSandpitTurns = int.MaxValue;
        int rewardedTheHunts = 0;

        Span<int> enemyStr = stackalloc int[HotLimits.MaxEnemies];
        Span<int> enemyTempStrRecovery = stackalloc int[HotLimits.MaxEnemies];
        Span<int> enemyVigor = stackalloc int[HotLimits.MaxEnemies];
        Span<int> enemyPaperCuts = stackalloc int[HotLimits.MaxEnemies];
        Span<int> enemyWeak = stackalloc int[HotLimits.MaxEnemies];
        Span<int> enemyPoison = stackalloc int[HotLimits.MaxEnemies];
        Span<int> enemyDoom = stackalloc int[HotLimits.MaxEnemies];
        Span<int> enemyReattachHp = stackalloc int[HotLimits.MaxEnemies];
        Span<byte> enemyHasDamageCap = stackalloc byte[HotLimits.MaxEnemies];
        Span<byte> enemyCanRevive = stackalloc byte[HotLimits.MaxEnemies];
        Span<byte> enemyIsMinion = stackalloc byte[HotLimits.MaxEnemies];

        int powerCount = s.PowerCount;
        for (int i = 0; i < powerCount; i++)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.Amount == 0)
                continue;

            ushort defId = p.PowerDefId;
            if (defId == 0)
                continue;
            if (p.OwnerSlot == 0)
            {
                bool isOneTurnCarryover = false;
                if (defId == lookup.VulnerableDefId) playerVuln += p.Amount;
                else if (defId == lookup.IntangibleDefId) playerIntangible += p.Amount;
                else if (defId == lookup.BufferDefId && p.Amount > 0) playerBuffer += p.Amount;
                else if (defId == lookup.DisintegrationDefId && p.Amount > 0) playerDisintegration += p.Amount;
                else if (defId == lookup.ConstrictDefId && p.Amount > 0) playerConstrict += p.Amount;
                else if (defId == lookup.DemiseDefId && p.Amount > 0) playerDemise += p.Amount;
                else if (defId == lookup.AccelerantDefId) playerAccelerant += p.Amount;
                else if ((defId == lookup.RetainHandDefId || defId == lookup.WellLaidPlansDefId) && p.Amount > 0) playerRetainHand += p.Amount;
                else if (defId == lookup.CorruptionDefId && p.Amount > 0) hasPlayerCorruption = true;
                else if (defId == lookup.BarricadeDefId || defId == lookup.BlurDefId) preservesExcessBlock = true;
                else if (defId == lookup.MetallicizeDefId || defId == lookup.PlatingDefId) playerEndTurnBlock += p.Amount;
                else if (defId == lookup.BlockNextTurnDefId || defId == lookup.SelfFormingClayDefId || defId == lookup.ToricToughnessDefId)
                {
                    nextTurnBlock += p.Amount;
                    isOneTurnCarryover = true;
                }
                else if (defId == lookup.EnergyNextTurnDefId) { nextTurnEnergy += p.Amount; isOneTurnCarryover = true; }
                else if (defId == lookup.DrawCardsNextTurnDefId) { nextTurnDraw += p.Amount; isOneTurnCarryover = true; }
                else if (defId == lookup.StarNextTurnDefId) { nextTurnStars += p.Amount; isOneTurnCarryover = true; }
                else if (defId == lookup.SummonNextTurnDefId) { nextTurnSummon += p.Amount; isOneTurnCarryover = true; }
                else if (defId == lookup.FurnaceDefId) furnacePower += p.Amount;
                else if ((defId == lookup.SeekingEdgeDefId || defId == lookup.ParryDefId) && p.Amount > 0) hasBladeSynergy = true;
                else if (policy.HasPureGrowthTargets && defId == lookup.TheHuntPowerDefId && p.Amount > 0) rewardedTheHunts += p.Amount;

                // Domain-agnostic persistent player buff investment (ZERO card/power-ID hardcoding):
                // permanent Buff powers outweigh transient debuffs/exhaust; counts distinct presence + unclamped stacks.
                if (!isOneTurnCarryover && p.Amount > 0 && defId < rules.Powers.Length
                    && rules.Powers[defId].Type == PowerType.Buff)
                {
                    playerBuffStacks += 6 + Math.Min(p.Amount, 10_000);
                }

                if (defId == lookup.NightmareDefId && p.Amount > 0)
                {
                    int nightmareWeight = EvaluateNightmareBonus(in s, cold, rules, in p, out bool resolvedNightmare);
                    playerSetup += resolvedNightmare
                        ? nightmareWeight
                        : ComputePlayerPowerSetupWeight(defId, p.Amount, rules, in lookup);
                }
                else
                {
                    int powerSetupWeight = ComputePlayerPowerSetupWeight(defId, p.Amount, rules, in lookup);
                    playerSetup += powerSetupWeight;
                    if (defId == lookup.NoDrawDefId && powerSetupWeight < 0)
                        noDrawSetupPenalty += powerSetupWeight;
                }
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

                if (defId == lookup.MinionDefId && p.Amount > 0)
                    enemyIsMinion[eSlot] = 1;

                bool isTempStrDown = defId < rules.Powers.Length
                    && IsTemporaryEnemyStrengthDownPower(rules.Powers[defId].PowerId);

                if (defId < rules.Powers.Length && rules.Powers[defId].Type == PowerType.Debuff && p.Amount > 0 && !isTempStrDown)
                {
                    // Cumulative win-condition DoTs (Poison/Doom) stay unclamped; short-duration multiplier debuffs
                    // (Weak/Vulnerable) cap per enemy so AoE temporary debuffs do not drown out permanent Powers.
                    bool isCumulativeDot = defId == lookup.PoisonDefId || defId == lookup.DoomDefId;
                    enemyDebuffStacks += isCumulativeDot ? Math.Min(p.Amount, 10_000) : Math.Min(p.Amount, 4);
                }

                if (isTempStrDown && p.Amount > 0)
                {
                    enemyTempStrRecovery[eSlot] += p.Amount;
                }
                else if (defId == lookup.StrengthDefId)
                {
                    enemyStr[eSlot] += p.Amount;
                }
                else if (defId == lookup.VigorDefId && p.Amount > 0)
                {
                    enemyVigor[eSlot] += p.Amount;
                }
                else if (defId == lookup.PaperCutsDefId && p.Amount > 0)
                {
                    enemyPaperCuts[eSlot] += p.Amount;
                }
                else if (defId == lookup.WeakDefId && p.Amount > 0)
                {
                    enemyWeak[eSlot] += p.Amount;
                    enemyDebuffValue += Math.Min(p.Amount, 5) * 4;
                }
                else if (defId == lookup.VulnerableDefId && p.Amount > 0)
                {
                    enemyDebuffValue += Math.Min(p.Amount, 5) * 5;
                }
                else if (defId == lookup.PoisonDefId && p.Amount > 0)
                {
                    enemyPoison[eSlot] += p.Amount;
                    totalPoisonAndDoom += Math.Min(p.Amount, 100_000);
                }
                else if (defId == lookup.DoomDefId && p.Amount > 0)
                {
                    enemyDoom[eSlot] += p.Amount;
                    totalPoisonAndDoom += Math.Min(p.Amount, 100_000);
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
                else if (p.Amount > 0
                    && (defId == lookup.IntangibleDefId
                        || defId == lookup.BufferDefId
                        || defId == lookup.SlipperyDefId
                        || defId == lookup.HardToKillDefId
                        || defId == lookup.HardenedShellDefId))
                {
                    enemyHasDamageCap[eSlot] = 1;
                }
                else if (p.Amount > 0
                    && (defId == lookup.AdaptableDefId
                        || defId == lookup.IllusionDefId))
                {
                    enemyCanRevive[eSlot] = 1;
                }
            }
        }

        // Scan orbs once: orb.PassiveVal & orb.EvokeVal already include player Focus via HotCombatEngine.Orbs.
        // Frost orbs trigger at BeforeTurnEnd BEFORE enemies attack, so their passive block enters effectiveDefense.
        int orbScore = 0;
        int frostPassiveBlock = 0;
        int darkEvokeSum = 0;
        int maxDarkEvoke = 0;
        int orbCount = s.OrbCount;
        for (int o = 0; o < orbCount; o++)
        {
            HotOrb orb = s.Orbs[o];
            HotOrbId orbId = HotCombatEngine.GetOrbId(rules, orb.OrbTypeId);
            if (orbId == HotOrbId.None)
                continue;

            int passiveVal = Math.Max(0, (int)orb.PassiveVal);
            int evokeVal = Math.Max(0, orb.EvokeVal);
            if (orbId == HotOrbId.Frost)
            {
                int triggerCount = (o == 0 && lookup.HasGoldPlatedCables) ? 2 : 1;
                frostPassiveBlock += passiveVal * triggerCount;
            }
            else if (orbId == HotOrbId.Dark)
            {
                darkEvokeSum += evokeVal;
                if (evokeVal > maxDarkEvoke)
                    maxDarkEvoke = evokeVal;
            }

            int val = orbId switch
            {
                HotOrbId.Lightning => passiveVal * 2 + evokeVal,
                HotOrbId.Frost => passiveVal * 3 + evokeVal,
                HotOrbId.Dark => evokeVal + passiveVal * 2,
                HotOrbId.Plasma => 18,
                HotOrbId.Glass => passiveVal * 2 + evokeVal,
                _ => passiveVal + evokeVal / 2,
            };
            orbScore += val;
        }
        int orbCapacity = Math.Min((int)s.OrbCapacity, HotLimits.MaxOrbs);

        // Precompute end-of-turn defense pools so multi-hit Osty/Wuppy, Block, Buffer, and PaperCuts track accurately.
        int endTurnBlock = summary.PlayerBlock + playerEndTurnBlock + frostPassiveBlock;
        int wuppyHp = ((s.CombatFlags & HotCombatFlags.HasWuppy) != 0 && (s.Wuppy.Flags & HotCreatureFlags.Alive) != 0)
            ? Math.Max(0, s.Wuppy.CurrentHp)
            : 0;
        int ostyHp = ((s.CombatFlags & HotCombatFlags.HasOsty) != 0 && (s.Osty.Flags & HotCreatureFlags.Alive) != 0)
            ? Math.Max(0, s.Osty.CurrentHp)
            : 0;
        int petDefenseBuffer = wuppyHp + ostyHp;
        int remainingBlock = endTurnBlock;
        int remainingOsty = ostyHp;
        int remainingWuppy = wuppyHp;
        int remainingBuffer = playerBuffer;
        int ostyAbsorbed = 0;

        // Player turn-end self-damage (hand Burn/Decay/Beckon and Disintegration/Constrict/Demise powers)
        // resolves BEFORE enemy attacks and bypasses Osty/Wuppy, consuming player Block/Buffer/Intangible.
        int incomingDamage = 0;
        int enemyAttackDamage = 0;
        int unblockedIncoming = 0;
        if (lookup.BurnCardDefId != 0 || lookup.DecayCardDefId != 0 || lookup.BeckonCardDefId != 0)
        {
            int hc = s.HandCount;
            for (int h = 0; h < hc; h++)
            {
                ref readonly HotCard c = ref s.Cards[h];
                ushort cDefId = c.CardDefId;
                if (cDefId == 0 || cDefId >= rules.Cards.Length)
                    continue;
                if (cDefId == lookup.BurnCardDefId)
                {
                    int dmg = rules.Cards[cDefId].GetDamage(c.UpgradeLevel, c.Var0Delta);
                    if (dmg <= 0)
                        dmg = c.UpgradeLevel > 0 ? 4 : 2;
                    ApplySelfEndTurnHit(dmg, blockable: true, playerIntangible > 0, ref remainingBlock, ref remainingBuffer, ref incomingDamage, ref unblockedIncoming);
                }
                else if (cDefId == lookup.DecayCardDefId)
                {
                    int dmg = rules.Cards[cDefId].GetDamage(c.UpgradeLevel, c.Var0Delta);
                    if (dmg <= 0)
                        dmg = 2;
                    ApplySelfEndTurnHit(dmg, blockable: true, playerIntangible > 0, ref remainingBlock, ref remainingBuffer, ref incomingDamage, ref unblockedIncoming);
                }
                else if (cDefId == lookup.BeckonCardDefId)
                {
                    ApplySelfEndTurnHit(lookup.BeckonHpLoss, blockable: false, playerIntangible > 0, ref remainingBlock, ref remainingBuffer, ref incomingDamage, ref unblockedIncoming);
                }
            }
        }
        if (playerDisintegration > 0)
            ApplySelfEndTurnHit(playerDisintegration, blockable: true, playerIntangible > 0, ref remainingBlock, ref remainingBuffer, ref incomingDamage, ref unblockedIncoming);
        if (playerConstrict > 0)
            ApplySelfEndTurnHit(playerConstrict, blockable: true, playerIntangible > 0, ref remainingBlock, ref remainingBuffer, ref incomingDamage, ref unblockedIncoming);
        if (playerDemise > 0)
            ApplySelfEndTurnHit(playerDemise, blockable: false, playerIntangible > 0, ref remainingBlock, ref remainingBuffer, ref incomingDamage, ref unblockedIncoming);

        // Scan active roster: incoming attack threat, permanent vs temp -Strength, PaperCuts, conditional phase enrage, reviving HP, Doom, Poison, and focus/AOE shape.
        int paperCutsMaxHpThreat = 0;
        int enragePhaseThreatDmg = 0;
        int lowestEnemyHp = int.MaxValue;
        int highestAliveEnemyHp = 0;
        int revivingHpPenalty = 0;
        int reattachAliveCount = 0;
        int doomLethalCount = 0;
        int poisonLethalCount = 0;
        int poisonLethalHpOffset = 0;
        int effectiveDoomValue = 0;
        int aliveNonMinionEnemies = 0;
        long enemyPoisonScore = 0;
        long poisonWeight = Math.Min(4_800L, (enemyHpWeight * 48L) / 100L);
        int clampedAccelerant = Math.Clamp(playerAccelerant, 0, 16);
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
            bool isRevivingEnemy = hasReattach || enemyCanRevive[eSlot] != 0;
            if (hasReattach)
                reattachAliveCount++;
            if (curHp > highestAliveEnemyHp)
                highestAliveEnemyHp = curHp;

            // Poison ticks at start of enemy turn BEFORE enemy attacks (1 + Accelerant ticks, HotCombatEngine.Turn.cs:402-446).
            int poison = enemyPoison[eSlot];
            long poisonTickDamage = 0;
            if (poison > 0)
            {
                int ticks = Math.Min(poison, 1 + clampedAccelerant);
                poisonTickDamage = ((long)ticks * (2L * poison - ticks + 1L)) / 2L;
            }
            bool poisonLethalBeforeAttack = curHp > 0
                && poisonTickDamage >= curHp
                && !isRevivingEnemy
                && enemyHasDamageCap[eSlot] == 0;

            if (poisonLethalBeforeAttack)
            {
                poisonLethalCount++;
                poisonLethalHpOffset += curHp;
                if (0 < lowestEnemyHp)
                    lowestEnemyHp = 0;
                continue;
            }

            bool doomLethal = false;
            if (enemyDoom[eSlot] > 0)
            {
                if (enemyDoom[eSlot] >= curHp && !isRevivingEnemy)
                {
                    doomLethal = true;
                    doomLethalCount++;
                    effectiveDoomValue += curHp;
                }
                else
                {
                    effectiveDoomValue += Math.Min(enemyDoom[eSlot], curHp);
                }
            }

            if (poison > 0 && curHp > 0 && !doomLethal)
            {
                int expectedTicks = Math.Min(poison, 2 + clampedAccelerant);
                long expectedPoisonDmg = ((long)expectedTicks * (2L * poison - expectedTicks + 1L)) / 2L;
                long effectivePoisonHp = Math.Min((long)curHp, expectedPoisonDmg);
                long enemyPoisonWeight = isRevivingEnemy ? (poisonWeight / 2L) : poisonWeight;
                enemyPoisonScore += effectivePoisonHp * enemyPoisonWeight;
            }

            int effectivePoisonReduction = (int)Math.Min((long)curHp, poisonTickDamage);
            int effectiveEnemyHp = Math.Max(0, curHp - effectivePoisonReduction - enemyDoom[eSlot]);
            if (effectiveEnemyHp < lowestEnemyHp)
                lowestEnemyHp = effectiveEnemyHp;

            if (!doomLethal && enemyIsMinion[eSlot] == 0)
                aliveNonMinionEnemies++;

            // Distinguish permanent Strength / -Strength (e.g. Malaise) from temporary 1-turn -Strength (PiercingWail/EnfeeblingTouch).
            int permanentEnemyStr = enemyStr[eSlot] + Math.Max(0, enemyTempStrRecovery[eSlot]);
            if (permanentEnemyStr > 0)
            {
                enemyThreatPowerPenalty += Math.Min(permanentEnemyStr, 20) * 3;
            }
            else if (permanentEnemyStr < 0)
            {
                int permNegStr = -permanentEnemyStr;
                enemyDebuffStacks += Math.Min(permNegStr, 12);
                enemyThreatPowerPenalty -= Math.Min(permNegStr, 20) * 6;
            }

            if ((enemy.Flags & HotEnemyFlags.SkipNextMove) != 0)
                continue;

            int rawIncoming = EstimateEnemyIncomingDamage(
                in enemy,
                rules,
                enemyStr[eSlot],
                enemyVigor[eSlot],
                enemyPaperCuts[eSlot],
                enemyWeak[eSlot] > 0,
                playerVuln > 0,
                playerIntangible > 0,
                in lookup,
                ref remainingBlock,
                ref remainingOsty,
                ref remainingWuppy,
                ref remainingBuffer,
                ref ostyAbsorbed,
                out int enemyUnblocked,
                out int enemyPaperCutsLoss);
            enemyAttackDamage += rawIncoming;
            incomingDamage += rawIncoming;
            unblockedIncoming += enemyUnblocked;
            paperCutsMaxHpThreat += enemyPaperCutsLoss;

            if (rawIncoming == 0)
            {
                enragePhaseThreatDmg += EstimateActivatedPhaseThreat(
                    in enemy,
                    rules,
                    enemyStr[eSlot],
                    enemyWeak[eSlot] > 1,
                    playerVuln > 1,
                    playerIntangible > 1,
                    summary.AliveEnemyCount,
                    in lookup);
            }
        }

        bool lethalIncoming = unblockedIncoming >= summary.PlayerHp;

        // In-combat survival runway computes un-discounted NetPersistentHpDeltaCenti; Offense/DesperateRescue
        // apply controlled BossHpRelief non-lethal scaling while keeping lethal guards 100% intact.
        int ongoingNetHpDeltaCenti = HotRouteQuality.ComputeNetPersistentHpDeltaCenti(
            endPlayerHp: summary.PlayerHp,
            postCombatMonotoneHeal: 0,
            initialPlayerHp: policy.InitialPlayerHp,
            endPlayerMaxHp: summary.PlayerMaxHp,
            initialPlayerMaxHp: policy.InitialPlayerMaxHp,
            deathSaveHpRestored: summary.DeathSaveHpRestored,
            growthHpCredit: summary.GrowthHpCredit,
            bossHpRelief: BossHpRelief.None,
            ignoreLongTermRewards: policy.IgnoreLongTermRewards,
            relicHpCredit: summary.RelicHpCredit,
            relicHealingHpCredit: summary.RelicHealingHpCredit,
            feedMaxHpExcluded: summary.FeedCount * policy.FeedMaxHpPerKill);

        long runningScore = (long)ongoingNetHpDeltaCenti * ongoingHpWeight
            - (long)netPotionCostCenti * hpWeightPerCenti
            - (long)summary.DeathSaveUseCount * deathSavePenalty;

        // Threat evaluation: full threat weight on lethal incoming hits (lethal guard applied after hand scan once playableCards is known);
        // mode-scaled non-lethal threat weight otherwise.
        if (unblockedIncoming > 0)
        {
            runningScore -= (long)unblockedIncoming * (lethalIncoming ? threatWeight : nonLethalThreatWeight);
        }
        else if (preservesExcessBlock && remainingBlock > 0)
        {
            int carriedBlock = Math.Min(remainingBlock, 60);
            runningScore += (long)carriedBlock * 20_000L;
        }

        // Armed PaelsLegion preserved-readiness credit on 0-attack-damage turns (cooldown <= 0 and unspent).
        if (enemyAttackDamage == 0 && lookup.PaelsLegionStateSlot < s.RelicStateCount)
        {
            ref readonly HotRelicState legionState = ref s.RelicStates[lookup.PaelsLegionStateSlot];
            if (legionState.Current <= 0 && (legionState.PreviousOrAux & 2) == 0)
                runningScore += 35_000L;
        }

        if (paperCutsMaxHpThreat > 0)
        {
            long maxHpLossPenaltyWeight = policy.StrategicBossHpRelief != BossHpRelief.RunEnding
                ? Math.Min(26_000L, threatWeight)
                : 10_000L;
            runningScore -= (long)paperCutsMaxHpThreat * maxHpLossPenaltyWeight;
        }

        if (enragePhaseThreatDmg > 0)
        {
            int excessCarriedBlock = preservesExcessBlock ? remainingBlock : 0;
            int intangibleShield = playerIntangible > 1 ? enragePhaseThreatDmg : 0;
            int unbufferedEnrage = Math.Max(0, enragePhaseThreatDmg - excessCarriedBlock - nextTurnBlock - intangibleShield);
            if (unbufferedEnrage > 0)
                runningScore -= (long)unbufferedEnrage * (nonLethalThreatWeight / 3L);
        }

        // Cross-turn relic carryovers (verified against HotRelics state semantics)
        if (s.CurrentSide == HotCombatSide.Player)
        {
            if (lookup.ArtOfWarStateSlot < s.RelicStateCount
                && s.RelicStates[lookup.ArtOfWarStateSlot].Current == 0
                && s.Counters[(int)HotCounterSlot.AttacksPlayedThisTurn] == 0)
            {
                nextTurnEnergy += 1;
            }

            if (lookup.PocketwatchStateSlot < s.RelicStateCount
                && s.RelicStates[lookup.PocketwatchStateSlot].Current <= 3
                && s.Counters[(int)HotCounterSlot.CardsPlayedThisTurn] <= 3)
            {
                nextTurnDraw += 3;
            }
        }

        // Next-turn defensive/resource carryovers
        if (nextTurnBlock > 0)
            runningScore += ((long)Math.Min(nextTurnBlock, 60) * 20_000L * carryoverMult) / 100L;
        if (nextTurnEnergy > 0)
            runningScore += ((long)Math.Min(nextTurnEnergy, 6) * 34_000L * carryoverMult) / 100L;
        if (nextTurnDraw > 0)
            runningScore += ((long)Math.Min(nextTurnDraw, 6) * 18_000L * carryoverMult) / 100L;
        if (nextTurnStars > 0)
            runningScore += ((long)Math.Min(nextTurnStars, 8) * 20_000L * carryoverMult) / 100L;
        if (nextTurnSummon > 0)
            runningScore += (long)Math.Min(nextTurnSummon, 20) * 6_000L;

        // Offense, permanent kills, reviving-enemy correction, Doom/Poison lethal, enemy Poison credit, and focus/AOE shaping
        int remainingNonDead = summary.AliveEnemyCount + summary.RevivingEnemyCount;
        int permanentKills = Math.Max(0, policy.InitialEnemyCount - remainingNonDead) + doomLethalCount + poisonLethalCount;
        runningScore += (long)permanentKills * EnemyKillBonus;
        runningScore -= (long)summary.EnemyHpSum * enemyHpWeight;
        runningScore -= (long)summary.EnemyBlockSum * EnemyBlockWeight;

        if (poisonLethalHpOffset > 0)
            runningScore += (long)poisonLethalHpOffset * ((enemyHpWeight * 95L) / 100L);

        if (enemyPoisonScore > 0)
            runningScore += enemyPoisonScore;

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

        int sandpitBoardInvestment = 0;
        if (minSandpitTurns < int.MaxValue && summary.EnemyHpSum > 40)
        {
            int clampedSandpit = Math.Min(minSandpitTurns, 6);
            runningScore += (long)clampedSandpit * 60_000L;
            if (minSandpitTurns <= 1)
                runningScore -= 1_200_000L;
            else if (minSandpitTurns == 2)
                runningScore -= 450_000L;
            else if (minSandpitTurns == 3)
                runningScore -= 150_000L;
            playerSetup += clampedSandpit * 8;
            sandpitBoardInvestment = clampedSandpit * 3;
        }

        // Orbs & OrbCapacity (Defect): smooth diminishing returns with non-zero tail (no hard 120 cliff), plus OrbCapacity.
        if (orbCapacity > 0)
        {
            playerSetup += orbCapacity * 2;
            runningScore += (long)orbCapacity * 18_000L;
        }
        if (orbScore > 0)
        {
            playerSetup += SoftCapPositive(orbScore / 3, knee: 24, secondKnee: 72);
            runningScore += (long)SoftCapPositive(orbScore, knee: 120, secondKnee: 360) * 2_500L;
        }

        // Osty (Necrobinder): keep full Osty HP for setup/board-investment niches so soaking hits
        // does not evict Necrobinder lines, while scoring the direct HP bonus on surviving post-hit HP.
        int survivingOstyHp = Math.Max(0, ostyHp - ostyAbsorbed);
        if (survivingOstyHp > 0)
            runningScore += (long)SoftCapPositive(survivingOstyHp, knee: 80, secondKnee: 320) * 4_500L;
        if (ostyHp > 0)
            playerSetup += SoftCapPositive(ostyHp / 4, knee: 12, secondKnee: 48);

        // Fool arrows & melodies (melodies scored regardless of arrow count)
        if (s.ArrowCount > 0)
        {
            int readyArrows = 0;
            for (int a = 0; a < s.ArrowCount; a++)
            {
                int status = s.Fool.Arrows[a] & HotFoolState.ArrowStatusMask;
                if (status == 0)
                    readyArrows++;
            }
            runningScore += (long)readyArrows * 6_000L;
        }
        if (s.Fool.MelodyCardsFinished > 0)
            runningScore += (long)s.Fool.MelodyCardsFinished * 5_000L;

        // Hand potential, retained cards, block-scalers, cost-zeroed plays, SovereignBlade, and deck clutter
        int handCount = s.HandCount;
        int playableCards = 0;
        int zeroCostPlayable = 0;
        int retainedCards = 0;
        int extraRetainedCandidates = 0;
        bool hasCopyOrPowerInHand = false;
        bool hasPlayableBlockScaler = false;
        int handClutter = 0;
        long costZeroedHandBonus = 0;
        long forgedBladeBonus = 0;
        int totalForgedDelta = 0;
        long bladeSynergyMult = hasBladeSynergy ? 130L : 100L;
        long retainedHoldBonus = 0;
        bool hasNightmareInHand = false;
        bool trackOngoingGrowth = !policy.IgnoreLongTermRewards && policy.HasPureGrowthTargets && !summary.AllGrowthTargetsSatisfied;
        int activeGrowthHandBudget = -1;
        int activeGrowthDeckBudget = -1;
        bool handRetainedAtTurnEnd = playerRetainHand > 0 || lookup.HasRunicPyramid;
        byte hexedAfflId = lookup.HexDefId != 0 ? rules.GetAfflictionDefId(HotAfflictionId.Hexed) : (byte)0;
        bool hasPlayerHex = hexedAfflId != 0 && s.ActiveHookMask != 0 && HasPlayerPower(in s, lookup.HexDefId);

        for (int h = 0; h < handCount; h++)
        {
            ref readonly HotCard c = ref s.Cards[h];
            HotCardDef def = rules.Cards[c.CardDefId];
            if (def.Type is CardType.Status or CardType.Curse)
            {
                handClutter++;
                continue;
            }

            if (trackOngoingGrowth)
            {
                int growthBudget = GetTrackedFatalGrowthBudget(c.CardDefId, in lookup, in policy);
                if (growthBudget >= 0)
                {
                    if (growthBudget > activeGrowthHandBudget)
                        activeGrowthHandBudget = growthBudget;
                    if (growthBudget > activeGrowthDeckBudget)
                        activeGrowthDeckBudget = growthBudget;
                }
            }

            bool isEthereal = (c.LocalKeywordsMask & HotCardKeywordMask.Ethereal) != 0
                || (hasPlayerHex && c.AfflId == hexedAfflId);
            bool isIntrinsicOrSingleRetain = (c.LocalKeywordsMask & HotCardKeywordMask.Retain) != 0
                || (c.Flags & HotCardFlags.SingleTurnRetain) != 0;
            bool isRetained = isIntrinsicOrSingleRetain || (handRetainedAtTurnEnd && !isEthereal);

            if (!isEthereal
                && (def.Type == CardType.Power || def.CardId is HotCustomCardId.Nightmare or HotCustomCardId.DualWield))
            {
                hasCopyOrPowerInHand = true;
            }

            if (isIntrinsicOrSingleRetain)
            {
                retainedCards++;
            }
            else if (handRetainedAtTurnEnd && !isEthereal && def.Rarity != CardRarity.Basic
                && (c.LocalKeywordsMask & HotCardKeywordMask.Unplayable) == 0)
            {
                extraRetainedCandidates++;
            }

            if (playerRetainHand > 0 && !isEthereal && (c.LocalKeywordsMask & HotCardKeywordMask.Unplayable) == 0)
            {
                if (def.CardId == HotCustomCardId.Nightmare)
                    hasNightmareInHand = true;

                if (unblockedIncoming == 0 && (def.GainsBlock || def.BaseBlock > 0 || def.UpgradedBlock > 0))
                {
                    int blk = Math.Clamp(def.GetBlock(c.UpgradeLevel, c.Var1Delta), 0, 24);
                    if (blk > 0)
                        retainedHoldBonus += Math.Min(90_000L - Math.Min(retainedHoldBonus, 90_000L), (long)blk * 1_500L);
                }

                if (def.Type == CardType.Power || def.SetupWeight > 0)
                {
                    int holdW = Math.Clamp(def.SetupWeight > 0 ? def.SetupWeight : 10, 6, 24);
                    long add = (long)holdW * 1_000L;
                    long room = Math.Max(0L, 60_000L - retainedHoldBonus);
                    retainedHoldBonus += Math.Min(room, add);
                }
            }

            if (def.CardId == HotCustomCardId.SovereignBlade)
            {
                int bladeDmg = Math.Max(0, def.GetDamage(c.UpgradeLevel, c.Var0Delta));
                int forgedDelta = Math.Max(0, bladeDmg - 10);
                totalForgedDelta += forgedDelta;

                long perForged = isRetained ? 4_500L : 3_000L;
                long generalBonus = ((long)forgedDelta * perForged * bladeSynergyMult) / 100L;
                long furnaceHoldBonus = 0;
                if (isRetained && furnacePower > 0 && summary.EnemyHpSum > bladeDmg * 2)
                {
                    long perDmg = evalMode is HotEvalMode.SetupAndSurvival or HotEvalMode.DesperateRescue ? 6_500L : 4_800L;
                    furnaceHoldBonus = (long)Math.Min(bladeDmg + furnacePower, 70) * perDmg;
                }
                forgedBladeBonus += Math.Max(generalBonus, furnaceHoldBonus);
            }

            if ((c.LocalKeywordsMask & HotCardKeywordMask.Unplayable) != 0)
                continue;

            bool isCostsX = (c.Flags & HotCardFlags.CostsX) != 0;
            bool isStarCostsX = (c.Flags & HotCardFlags.HasStarCostX) != 0;
            int liveCost = isCostsX
                ? summary.Energy
                : Math.Max(0, HotCostModifiers.GetLocalEnergyCost(in c, cold));
            if (hasPlayerCorruption && def.Type == CardType.Skill && !isCostsX)
                liveCost = 0;
            int starCost = isStarCostsX
                ? summary.Stars
                : Math.Max(0, HotCostModifiers.GetEffectiveStarCost(in c, cold));
            if (liveCost <= summary.Energy && starCost <= summary.Stars)
            {
                playableCards++;
                if (def.CardId is HotCustomCardId.Prolong or HotCustomCardId.Entrench or HotCustomCardId.BodySlam)
                    hasPlayableBlockScaler = true;

                if (liveCost == 0 && starCost == 0)
                {
                    zeroCostPlayable++;
                    if (!isCostsX && !isStarCostsX && def.BaseCost > 0 && def.Rarity != CardRarity.Basic)
                    {
                        long bonus = (long)Math.Min((int)def.BaseCost, 3) * 24_000L;
                        long room = 160_000L - costZeroedHandBonus;
                        if (room > 0)
                            costZeroedHandBonus += Math.Min(room, bonus);
                    }
                }
            }
        }

        // Lethal guard gated on playableCards == 0 (respecting Stars > 0, 0-cost cards, and Energy > 0 with no playable cards).
        if (lethalIncoming)
            runningScore -= playableCards == 0 ? 4_000_000L : 800_000L;

        int extraRetainedCap = hasCopyOrPowerInHand ? 4 : 2;
        retainedCards += Math.Min(extraRetainedCandidates, extraRetainedCap);

        // Effective defense: cap temporary current-turn block at incomingDamage unless excess block is preserved
        // or a playable hand card scales off current Player.Block (Prolong/Entrench/BodySlam).
        int effectiveDefense = preservesExcessBlock
            ? endTurnBlock
            : hasPlayableBlockScaler
                ? Math.Min(endTurnBlock, Math.Max(incomingDamage, 40))
                : Math.Min(endTurnBlock, incomingDamage);
        effectiveDefense += petDefenseBuffer;

        if (hasPlayableBlockScaler)
        {
            int excessCurrentBlock = Math.Min(Math.Max(0, summary.PlayerBlock - incomingDamage), 40);
            if (excessCurrentBlock > 0)
                runningScore += (long)excessCurrentBlock * 12_000L;
        }

        if (noDrawSetupPenalty < 0 && playableCards >= 4 && zeroCostPlayable >= 3)
            playerSetup -= noDrawSetupPenalty;

        int deckClutter = handClutter;
        int maxUndrawnSetupWeight = 0;
        int livePileEnd = s.DiscardStart + s.DiscardCount;
        for (int idx = s.DrawStart; idx < livePileEnd; idx++)
        {
            ref readonly HotCard pileCard = ref s.Cards[idx];
            HotCardDef pileDef = rules.Cards[pileCard.CardDefId];
            if (pileDef.Type is CardType.Status or CardType.Curse)
            {
                deckClutter++;
                continue;
            }

            if (trackOngoingGrowth)
            {
                int growthBudget = GetTrackedFatalGrowthBudget(pileCard.CardDefId, in lookup, in policy);
                if (growthBudget > activeGrowthDeckBudget)
                    activeGrowthDeckBudget = growthBudget;
            }

            if (pileDef.CardId == HotCustomCardId.SovereignBlade)
            {
                int bladeDmg = Math.Max(0, pileDef.GetDamage(pileCard.UpgradeLevel, pileCard.Var0Delta));
                int forgedDelta = Math.Max(0, bladeDmg - 10);
                totalForgedDelta += forgedDelta;
                if (forgedDelta > 0)
                    forgedBladeBonus += ((long)forgedDelta * 3_000L * bladeSynergyMult) / 100L;
            }

            if (playerRetainHand > 0 && hasNightmareInHand)
            {
                int candW = pileDef.SetupWeight;
                if (pileDef.Type == CardType.Power && candW < 18)
                    candW = 18;
                if (pileCard.UpgradeLevel > 0 && candW > 0)
                    candW += 5;
                if (candW > maxUndrawnSetupWeight)
                    maxUndrawnSetupWeight = candW;
            }
        }

        if (playerRetainHand > 0 && hasNightmareInHand && maxUndrawnSetupWeight >= 20)
        {
            retainedHoldBonus += (long)Math.Min(maxUndrawnSetupWeight, 50) * 12_000L;
            playerSetup += Math.Min(maxUndrawnSetupWeight / 3, 16);
        }

        int goodThinnedCards = 0;
        int wastedEngineExhausts = 0;
        int exhaustedTheHunts = 0;
        int exhaustEnd = s.ExhaustStart + s.ExhaustCount;
        for (int idx = s.ExhaustStart; idx < exhaustEnd; idx++)
        {
            ref readonly HotCard exCard = ref s.Cards[idx];
            if (exCard.CardDefId == 0 || exCard.CardDefId >= rules.Cards.Length)
                continue;
            if (policy.HasPureGrowthTargets && exCard.CardDefId == lookup.TheHuntCardDefId)
                exhaustedTheHunts++;
            HotCardDef exDef = rules.Cards[exCard.CardDefId];
            if (exDef.Domain == HotCardDomain.Token)
                continue;

            bool hasExhaustKeyword = ((exDef.BaseKeywords | (byte)exCard.LocalKeywordsMask) & (byte)HotCardKeywordMask.Exhaust) != 0;
            bool isGoodThin = exDef.Type is CardType.Status or CardType.Curse
                || (exDef.Rarity == CardRarity.Basic && (exDef.IsStrikeTag || exDef.IsDefendTag));
            if (isGoodThin)
            {
                goodThinnedCards++;
            }
            else if (!hasExhaustKeyword
                && (exCard.Flags & HotCardFlags.IsClone) == 0
                && (!hasPlayerCorruption || exDef.Type != CardType.Skill))
            {
                wastedEngineExhausts++;
            }
        }

        // Exempt PaelsEye extra-turn hand exhaust from wastedEngineExhausts once PaelsEye has triggered this combat.
        if (wastedEngineExhausts > 0
            && lookup.PaelsEyeStateSlot < s.RelicStateCount
            && s.RelicStates[lookup.PaelsEyeStateSlot].Current != 0)
        {
            int paelsEyeAllowance = Math.Max((int)s.MaxHandSize, 10);
            wastedEngineExhausts = Math.Max(0, wastedEngineExhausts - paelsEyeAllowance);
        }

        byte playableHandCount = (byte)Math.Clamp(playableCards, 0, byte.MaxValue);

        runningScore += (long)Math.Min(summary.Energy, 6) * EnergyWeight;
        int stars = Math.Max(0, summary.Stars);
        if (stars > 0)
        {
            int firstSixStars = Math.Min(stars, 6);
            int nextSixStars = Math.Clamp(stars - 6, 0, 6);
            runningScore += (long)firstSixStars * 26_000L + (long)nextSixStars * 14_000L;
        }
        runningScore += (long)Math.Min(playableCards, 10) * 18_000L;
        runningScore += (long)Math.Min(zeroCostPlayable, 8) * 11_000L;
        runningScore += costZeroedHandBonus;
        runningScore += forgedBladeBonus + retainedHoldBonus;
        runningScore -= (long)handClutter * 4_000L;
        runningScore -= (long)deckClutter * 4_500L;
        runningScore -= (long)wastedEngineExhausts * 20_000L;

        int rawTotalSetup = playerSetup + enemyDebuffValue - enemyThreatPowerPenalty;
        int totalSetup = Math.Clamp(rawTotalSetup, -200, 2000);
        int effectiveSetup = totalSetup >= 0
            ? SoftCapPositive(totalSetup, knee: 80, secondKnee: 240)
            : Math.Max(totalSetup, -96);
        runningScore += (long)effectiveSetup * setupScale;

        // Long-term resources, relic guidance, anger copies, theft (GrowthHpCredit & Relic credits already in ongoingNetHpDeltaCenti!)
        if (!policy.IgnoreLongTermRewards)
        {
            if (policy.HasPureGrowthTargets)
            {
                if (summary.GrowthRewardCount > 0)
                    runningScore += (long)summary.GrowthRewardCount * 25_000L;
                int missedTheHuntRewards = Math.Max(0, exhaustedTheHunts - rewardedTheHunts);
                if (missedTheHuntRewards > 0)
                    runningScore -= (long)missedTheHuntRewards * 30_000L;

                // Gentle, budget-bounded ongoing Growth shaping: reward preserving an active fatal growth card
                // while targets are unsatisfied, capped below 1 HP when safe and smoothly tapered so it never
                // induces taking avoidable HP damage worth more than the growth budget or max HP gain.
                if (!summary.AllGrowthTargetsSatisfied && !lethalIncoming && aliveNonMinionEnemies > 0 && activeGrowthDeckBudget >= 0)
                {
                    if (activeGrowthDeckBudget > 0)
                    {
                        long deckBonus = Math.Min(24_000L, (long)activeGrowthDeckBudget * 6_000L);
                        long handBonus = activeGrowthHandBudget > 0
                            ? Math.Min(14_000L, (long)activeGrowthHandBudget * 3_500L)
                            : 0L;
                        if (unblockedIncoming == 0)
                        {
                            runningScore += deckBonus + handBonus;
                        }
                        else
                        {
                            int maxSafeDmg = Math.Min(2, activeGrowthDeckBudget);
                            if (unblockedIncoming <= maxSafeDmg
                                && 2L * (summary.PlayerHp - unblockedIncoming) >= (long)summary.PlayerMaxHp)
                            {
                                long totalBonus = deckBonus + handBonus;
                                runningScore += unblockedIncoming == 1 ? (totalBonus / 2L) : (totalBonus / 4L);
                            }
                        }
                    }
                    else if (unblockedIncoming == 0 && lowestEnemyHp <= 20)
                    {
                        // Zero-budget (free-only) fatal growth: never incentivize taking any damage or stalling
                        // high-HP fights; apply only a small tie-break (< 1 enemy HP) when a lethal finish is close.
                        runningScore += 4_000L;
                        if (activeGrowthHandBudget >= 0)
                            runningScore += 4_000L;
                    }
                }
            }
            else if (summary.GrowthRewardCount > 0)
            {
                runningScore += (long)summary.GrowthRewardCount * 5_000L;
            }

            if (s.LongTermResourceValue > 0)
                runningScore += (long)Math.Min(s.LongTermResourceValue, 20) * 3_500L;
        }

        if (policy.HasRelicTargets)
        {
            runningScore += ComputeRelicGuidanceScore(
                summary.RelicSatisfiedPriority,
                summary.RelicDistance);
        }

        runningScore -= (long)Math.Max(0, s.AngerCopiesGenerated - 2) * 2_000L;
        runningScore -= (long)actionCount * 5L;

        if (policy.TheftPolicy == SolverTheftPolicy.PreserveResources)
        {
            runningScore -= (long)summary.OutstandingStolen * 1_000_000L;
            if (summary.ForfeitsGold)
                runningScore -= 2_000_000L;
        }

        // Niche 4 (Generic Persistent Board Investment — UNCLAMPED):
        // PlayerBuffStacks + EnemyDebuffStacks + OrbCount + OrbCapacity + DarkEvokeCharge/6 + OstyHp/4
        // + RetainedCards*3 + ThinDeckDelta + Melodies + Sandpit + ForgedBlade + PersistentStars
        int currentLiveDeckCount = s.HandCount + s.DrawCount + s.DiscardCount;
        int rawThinDeckDelta = Math.Max(0, policy.InitialLiveDeckCount - currentLiveDeckCount);
        int netGoodThinned = Math.Max(0, Math.Min(rawThinDeckDelta, goodThinnedCards) - wastedEngineExhausts);
        int thinDeckDelta = Math.Min((netGoodThinned + 1) >> 1, 6);
        int boardInvestment = playerBuffStacks
            + enemyDebuffStacks
            + orbCount
            + orbCapacity
            + (darkEvokeSum / 6)
            + (ostyHp / 4)
            + (retainedCards * 3)
            + thinDeckDelta
            + s.Fool.MelodyCardsFinished
            + sandpitBoardInvestment
            + Math.Min(totalForgedDelta / 3, 20)
            + Math.Min(stars / 2, 5);

        // Niche 5 (Optionality): Energy + Stars + PlayableHandCount + DrawNextTurn
        int drawNextTurnTotal = Math.Max(0, s.DrawNextTurn) + nextTurnDraw;
        int rawOptionality = Math.Max(0, summary.Energy) + Math.Max(0, summary.Stars) + playableCards + drawNextTurnTotal;

        features = new HotEvalFeatures(
            score: runningScore,
            setupScore: totalSetup,
            boardInvestment: boardInvestment,
            maxDarkEvoke: maxDarkEvoke,
            totalPoisonAndDoom: totalPoisonAndDoom,
            effectiveDefense: (short)Math.Clamp(effectiveDefense, 0, short.MaxValue),
            optionalityScore: (short)Math.Clamp(rawOptionality, 0, short.MaxValue),
            playableHandCount: playableHandCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SoftCapPositive(int value, int knee, int secondKnee)
    {
        if (value <= knee)
            return value;
        int firstBand = knee + ((value - knee) >> 1);
        if (firstBand <= secondKnee)
            return firstBand;
        int tail = secondKnee + ((firstBand - secondKnee) >> 1);
        return tail > 10_000 ? 10_000 : tail;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasPlayerPower(in CombatHotState s, ushort powerDefId)
    {
        if (powerDefId == 0)
            return false;
        int powerCount = s.PowerCount;
        for (int i = 0; i < powerCount; i++)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.OwnerSlot == 0 && p.PowerDefId == powerDefId && p.Amount > 0)
                return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsTemporaryEnemyStrengthDownPower(HotPowerId powerId)
        => powerId is
            HotPowerId.ShacklingPotionPower
            or HotPowerId.PiercingWailPower
            or HotPowerId.DarkShacklesPower
            or HotPowerId.EnfeeblingTouchPower
            or HotPowerId.CrushUnderPower
            or HotPowerId.DyingStarPower
            or HotPowerId.ManglePower
            or HotPowerId.MonarchsGazeStrengthDownPower;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetTrackedFatalGrowthBudget(
        ushort cardDefId,
        in HotEvalLookup lookup,
        in HotSearchPolicy policy)
    {
        if (cardDefId == 0)
            return -1;
        GrowthValues b = policy.EffectiveGrowthBudgets;
        if (cardDefId == lookup.FeedCardDefId)
            return Math.Max(0, b.Feed);
        if (cardDefId == lookup.HandOfGreedCardDefId)
            return Math.Max(0, b.HandOfGreed);
        if (cardDefId == lookup.TheHuntCardDefId)
            return Math.Max(0, b.TheHunt);
        if (cardDefId == lookup.LessonLearnedCardDefId || cardDefId == lookup.RitualDaggerCardDefId)
            return 0;
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplySelfEndTurnHit(
        int rawDamage,
        bool blockable,
        bool playerIntangible,
        ref int remainingBlock,
        ref int remainingBuffer,
        ref int incomingDamage,
        ref int unblockedIncoming)
    {
        if (rawDamage <= 0)
            return;
        int dmg = (playerIntangible && rawDamage > 1) ? 1 : rawDamage;
        if (blockable)
        {
            incomingDamage += dmg;
            if (remainingBlock > 0)
            {
                int blocked = Math.Min(remainingBlock, dmg);
                remainingBlock -= blocked;
                dmg -= blocked;
            }
        }
        if (dmg > 0)
        {
            if (remainingBuffer > 0)
                remainingBuffer--;
            else
                unblockedIncoming += dmg;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplySingleEnemyHit(
        int hit,
        int enemyPaperCuts,
        ref int remainingBlock,
        ref int remainingOsty,
        ref int remainingWuppy,
        ref int remainingBuffer,
        ref int ostyAbsorbed,
        ref int unblockedDamage,
        ref int paperCutsMaxHpLoss)
    {
        int rem = hit;
        if (remainingOsty > 0 && rem > 0)
        {
            int absorbed = Math.Min(remainingOsty, rem);
            remainingOsty -= absorbed;
            ostyAbsorbed += absorbed;
            rem -= absorbed;
        }
        if (remainingWuppy > 0 && rem > 0)
        {
            int absorbed = Math.Min(remainingWuppy, rem);
            remainingWuppy -= absorbed;
            rem -= absorbed;
        }
        if (remainingBlock > 0 && rem > 0)
        {
            int blocked = Math.Min(remainingBlock, rem);
            remainingBlock -= blocked;
            rem -= blocked;
        }
        if (rem > 0)
        {
            if (remainingBuffer > 0)
            {
                remainingBuffer--;
            }
            else
            {
                unblockedDamage += rem;
                if (enemyPaperCuts > 0)
                    paperCutsMaxHpLoss += enemyPaperCuts;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EstimateEnemyIncomingDamage(
        in HotEnemy enemy,
        HotRules rules,
        int enemyStrength,
        int enemyVigor,
        int enemyPaperCuts,
        bool enemyWeak,
        bool playerVulnerable,
        bool playerIntangible,
        in HotEvalLookup lookup,
        ref int remainingBlock,
        ref int remainingOsty,
        ref int remainingWuppy,
        ref int remainingBuffer,
        ref int ostyAbsorbed,
        out int unblockedDamage,
        out int paperCutsMaxHpLoss)
    {
        unblockedDamage = 0;
        paperCutsMaxHpLoss = 0;
        if (enemy.MonsterDefId == 0 || enemy.CurrentMoveNode < 0)
            return 0;

        HotMonsterMachine? machine = rules.GetMonsterMachine(enemy.MonsterDefId);
        if (machine == null || enemy.CurrentMoveNode >= machine.Nodes.Length)
            return 0;

        HotMonsterNode node = machine.Nodes[enemy.CurrentMoveNode];
        return EstimateNodeAttackDamage(
            in enemy,
            rules,
            node,
            enemyStrength,
            enemyVigor,
            enemyPaperCuts,
            enemyWeak,
            playerVulnerable,
            playerIntangible,
            in lookup,
            ref remainingBlock,
            ref remainingOsty,
            ref remainingWuppy,
            ref remainingBuffer,
            ref ostyAbsorbed,
            out unblockedDamage,
            out paperCutsMaxHpLoss);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EstimateNodeAttackDamage(
        in HotEnemy enemy,
        HotRules rules,
        HotMonsterNode node,
        int enemyStrength,
        int enemyVigor,
        int enemyPaperCuts,
        bool enemyWeak,
        bool playerVulnerable,
        bool playerIntangible,
        in HotEvalLookup lookup,
        ref int remainingBlock,
        ref int remainingOsty,
        ref int remainingWuppy,
        ref int remainingBuffer,
        ref int ostyAbsorbed,
        out int unblockedDamage,
        out int paperCutsMaxHpLoss)
    {
        unblockedDamage = 0;
        paperCutsMaxHpLoss = 0;
        int total = 0;
        int vigorRemaining = Math.Max(0, enemyVigor);

        if (node.Attacks.Length > 0)
        {
            for (int i = 0; i < node.Attacks.Length; i++)
            {
                HotMonsterAttack atk = node.Attacks[i];
                if (atk.Repeats == 0)
                    continue;
                int hit = ScaleEnemyHit(atk.BaseDamage, enemyStrength + vigorRemaining, enemyWeak, playerVulnerable, playerIntangible, in lookup);
                vigorRemaining = 0;
                total += hit * atk.Repeats;
                if (hit > 0)
                {
                    for (int rep = 0; rep < atk.Repeats; rep++)
                    {
                        ApplySingleEnemyHit(
                            hit,
                            enemyPaperCuts,
                            ref remainingBlock,
                            ref remainingOsty,
                            ref remainingWuppy,
                            ref remainingBuffer,
                            ref ostyAbsorbed,
                            ref unblockedDamage,
                            ref paperCutsMaxHpLoss);
                    }
                }
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
            int hit = ScaleEnemyHit(node.BaseDamage, enemyStrength + vigorRemaining, enemyWeak, playerVulnerable, playerIntangible, in lookup);
            total += hit * hits;
            if (hit > 0)
            {
                for (int rep = 0; rep < hits; rep++)
                {
                    ApplySingleEnemyHit(
                        hit,
                        enemyPaperCuts,
                        ref remainingBlock,
                        ref remainingOsty,
                        ref remainingWuppy,
                        ref remainingBuffer,
                        ref ostyAbsorbed,
                        ref unblockedDamage,
                        ref paperCutsMaxHpLoss);
                }
            }
        }

        return total;
    }

    private static int EstimateActivatedPhaseThreat(
        in HotEnemy enemy,
        HotRules rules,
        int enemyStrength,
        bool enemyWeak,
        bool playerVulnerable,
        bool playerIntangible,
        int aliveEnemies,
        in HotEvalLookup lookup)
    {
        if (enemy.MonsterDefId == 0 || enemy.CurrentMoveNode < 0)
            return 0;

        HotMonsterMachine? machine = rules.GetMonsterMachine(enemy.MonsterDefId);
        if (machine == null || enemy.CurrentMoveNode >= machine.Nodes.Length)
            return 0;

        HotMonsterNode cur = machine.Nodes[enemy.CurrentMoveNode];
        bool amalgamEnraged = (enemy.Flags & HotEnemyFlags.AmalgamDied) != 0;
        bool soloShieldSlam = false;
        bool halfHpFrogCharge = false;

        int nextId = cur.NextNode;
        if (nextId >= 0 && nextId < machine.Nodes.Length)
        {
            HotMonsterNode next = machine.Nodes[nextId];
            if (next.NodeKind == HotMonsterNodeKind.ConditionalBranch)
            {
                if (next.ConditionalKind == HotMonsterConditionalKind.LivingShieldSlam && aliveEnemies <= 1)
                    soloShieldSlam = true;
                else if (next.ConditionalKind == HotMonsterConditionalKind.FrogKnightHalfHealth
                    && (enemy.Flags & HotEnemyFlags.BeetleCharged) == 0
                    && enemy.Vital.CurrentHp < enemy.Vital.MaxHp / 2)
                    halfHpFrogCharge = true;
            }
        }

        if (!amalgamEnraged && !soloShieldSlam && !halfHpFrogCharge)
            return 0;

        int bonusStr = 0;
        ushort strDefId = lookup.StrengthDefId;
        for (int e = 0; e < cur.MoveEffects.Length; e++)
        {
            HotMonsterMoveEffect eff = cur.MoveEffects[e];
            if (eff.Op == HotCardOpKind.ApplyPowerSelf && eff.TargetDefId == strDefId && eff.Amount > 0)
                bonusStr += eff.Amount;
        }

        int cursor = cur.NextNode;
        for (int hop = 0; hop < 4 && cursor >= 0 && cursor < machine.Nodes.Length; hop++)
        {
            HotMonsterNode node = machine.Nodes[cursor];
            if (node.NodeKind == HotMonsterNodeKind.Move)
            {
                int dummyBlock = 0;
                int dummyOsty = 0;
                int dummyWuppy = 0;
                int dummyBuffer = 0;
                int dummyOstyAbsorbed = 0;
                return EstimateNodeAttackDamage(
                    in enemy,
                    rules,
                    node,
                    enemyStrength + bonusStr,
                    enemyVigor: 0,
                    enemyPaperCuts: 0,
                    enemyWeak,
                    playerVulnerable,
                    playerIntangible,
                    in lookup,
                    ref dummyBlock,
                    ref dummyOsty,
                    ref dummyWuppy,
                    ref dummyBuffer,
                    ref dummyOstyAbsorbed,
                    out _,
                    out _);
            }

            if (node.NodeKind == HotMonsterNodeKind.ConditionalBranch)
            {
                short resolved = node.ConditionalKind switch
                {
                    HotMonsterConditionalKind.QueenAmalgam when amalgamEnraged => machine.FindNode("OFF_WITH_YOUR_HEAD_MOVE"),
                    HotMonsterConditionalKind.LivingShieldSlam when aliveEnemies <= 1 => machine.FindNode("SMASH_MOVE"),
                    HotMonsterConditionalKind.FrogKnightHalfHealth when halfHpFrogCharge => machine.FindNode("BEETLE_CHARGE"),
                    _ => node.NextNode,
                };
                cursor = resolved >= 0 ? resolved : node.NextNode;
            }
            else
            {
                break;
            }
        }

        return 0;
    }

    private static int EvaluateNightmareBonus(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        in HotPower power,
        out bool resolved)
    {
        resolved = false;
        HotCard template = default;

        if (power.AuxCardUid == 0
            && power.Aux0 >= 0
            && cold?.DetachedCards is { Length: > 0 } detached
            && power.Aux0 < detached.Length)
        {
            template = detached[power.Aux0];
            resolved = true;
        }
        else if (power.AuxCardUid != 0)
        {
            int total = s.TotalCardCount;
            for (int i = 0; i < total; i++)
            {
                if (s.Cards[i].CardUid == power.AuxCardUid)
                {
                    template = s.Cards[i];
                    resolved = true;
                    break;
                }
            }
            if (!resolved && power.Aux0 > 0 && power.Aux0 < rules.Cards.Length)
            {
                template.CardDefId = (ushort)power.Aux0;
                resolved = true;
            }
        }

        if (!resolved || template.CardDefId == 0 || template.CardDefId >= rules.Cards.Length)
        {
            resolved = false;
            return 0;
        }

        HotCardDef def = rules.Cards[template.CardDefId];
        if (def.Type is CardType.Status or CardType.Curse)
            return -18;

        int cardVal = def.SetupWeight > 0 ? def.SetupWeight : 0;
        if (def.Type == CardType.Power)
            cardVal += 16;
        else if (def.Rarity == CardRarity.Rare)
            cardVal += 10;
        else if (def.Rarity == CardRarity.Uncommon)
            cardVal += 4;
        else if (def.Rarity == CardRarity.Basic)
            cardVal -= 8;

        if (template.UpgradeLevel > 0)
            cardVal += 5;

        if (def.GainsBlock || def.BaseBlock > 0 || def.UpgradedBlock > 0)
            cardVal += 5;

        if (def.CanHealPlayer || def.CardId == HotCustomCardId.Alchemize)
            cardVal += 10;

        int cost = (template.Flags & HotCardFlags.CostsX) != 0
            ? 2
            : Math.Max(0, HotCostModifiers.GetLocalEnergyCost(in template, cold));
        if (cost == 0 && def.Rarity != CardRarity.Basic)
            cardVal += 8;
        else if (cost >= 3 && def.Type != CardType.Power)
            cardVal -= 4;

        int copies = Math.Clamp(power.Amount, 1, 3);
        return Math.Clamp((cardVal * copies) / 3, 2, 80);
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
            int rawMag = Math.Max(1, Math.Abs(amount));
            int mag = Math.Clamp(rawMag, 1, 6);
            return def.PowerId switch
            {
                HotPowerId.WeakPower => -mag * 3,
                HotPowerId.VulnerablePower => -mag * 3,
                HotPowerId.FrailPower => -mag * 3,
                HotPowerId.WraithFormPower => -mag * 2,
                HotPowerId.BiasedCognitionPower => -mag * 2,
                HotPowerId.DisintegrationPower => -Math.Min(rawMag * 8, 80),
                HotPowerId.ConstrictPower => -Math.Min(rawMag * 8, 80),
                HotPowerId.WasteAwayPower => -Math.Min(rawMag * 28, 84),
                HotPowerId.AmnesiaPower => -Math.Min(rawMag * 12, 36),
                HotPowerId.MindRotPower => -Math.Min(rawMag * 5, 25),
                HotPowerId.NoBlockPower => -Math.Min(rawMag * 8, 32),
                HotPowerId.HexPower => -10,
                HotPowerId.TangledPower => -Math.Min(rawMag * 6, 18),
                HotPowerId.NoDrawPower => -5,
                HotPowerId.NoEnergyGainPower => -6,
                HotPowerId.BorrowedTimePower => -Math.Min(rawMag * 4, 16),
                HotPowerId.ChainsOfBindingPower => -Math.Min(rawMag * 4, 16),
                HotPowerId.SlothPower => -Math.Max(6, (6 - Math.Min(rawMag, 5)) * 5),
                HotPowerId.SlowPower => -2,
                HotPowerId.NeurosurgePower => -Math.Min(rawMag * 6, 30),
                HotPowerId.DemisePower => -Math.Min(rawMag * 5, 40),
                HotPowerId.StranglePower => -Math.Min(rawMag * 4, 24),
                HotPowerId.TenderPower => -6,
                HotPowerId.DampenPower => -10,
                HotPowerId.RingingPower => -6,
                HotPowerId.SmoggyPower => -8,
                HotPowerId.SurroundedPower => -8,
                HotPowerId.YouAreMinePower => -8,
                HotPowerId.ConfusedPower => -4,
                HotPowerId.TheGambitPower => -4,
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
                return Math.Min(Math.Max(0, amount), 4) * 3;
            case HotPowerId.DrawCardsNextTurnPower:
                return Math.Min(Math.Max(0, amount), 5) * 2;
            case HotPowerId.StarNextTurnPower:
            case HotPowerId.SummonNextTurnPower:
                return Math.Min(Math.Max(0, amount), 4) * 2;
        }

        int posAmt = Math.Clamp(amount, 0, 16);
        int explicitWeight = def.PowerId switch
        {
            HotPowerId.VoidFormPower => Math.Min(posAmt, 3) * 20,
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
            HotPowerId.IntangiblePower => Math.Min(posAmt, 3) * 16,
            HotPowerId.EchoFormPower => Math.Min(posAmt, 3) * 16,
            HotPowerId.DemonFormPower => Math.Min(posAmt, 6) * 6,
            HotPowerId.BufferPower => Math.Min(posAmt, 5) * 10,
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
            HotPowerId.FreeSkillPower => Math.Min(posAmt, 4) * 5,
            HotPowerId.FreePowerPower => Math.Min(posAmt, 4) * 6,
            HotPowerId.FreeAttackPower => Math.Min(posAmt, 4) * 5,
            HotPowerId.RetainHandPower => Math.Min(posAmt, 4) * 5,
            HotPowerId.UnmovablePower => Math.Min(posAmt, 3) * 14,
            HotPowerId.FriendshipPower => Math.Min(posAmt, 4) * 18,
            HotPowerId.PyrePower => Math.Min(posAmt, 4) * 18,
            HotPowerId.SentryModePower => Math.Min(posAmt, 4) * 10,
            HotPowerId.LightningRodPower => Math.Min(posAmt, 4) * 8,
            HotPowerId.CorrosiveWavePower => Math.Min(posAmt, 8) * 3,
            HotPowerId.StampedePower => Math.Min(posAmt, 4) * 12,
            HotPowerId.ShadowStepPower => Math.Min(posAmt, 4) * 10,
            HotPowerId.JugglingPower => Math.Min(posAmt, 3) * 12,
            HotPowerId.AggressionPower => Math.Min(posAmt, 3) * 12,
            HotPowerId.CacophonyPower => Math.Min(posAmt, 12) * 2,
            HotPowerId.CallOfTheVoidPower => Math.Min(posAmt, 4) * 8,
            HotPowerId.ClarityPower => Math.Min(posAmt, 4) * 5,
            HotPowerId.ColossusPower => Math.Min(posAmt, 4) * 6,
            HotPowerId.ConcoctPower => Math.Min(posAmt, 6) * 2,
            HotPowerId.ConsumingShadowPower => Math.Min(posAmt, 3) * 10,
            HotPowerId.FastenPower => Math.Min(posAmt, 10) * 2,
            HotPowerId.ForegoneConclusionPower => Math.Min(posAmt, 4) * 5,
            HotPowerId.HailstormPower => Math.Min(posAmt, 12) * 2,
            HotPowerId.MonologuePower => Math.Min(posAmt, 6) * 2,
            HotPowerId.PrepTimePower => Math.Min(posAmt, 10) * 2,
            HotPowerId.ReflectPower => Math.Min(posAmt, 4) * 6,
            HotPowerId.RollingBoulderPower => 10 + Math.Min(posAmt, 15),
            HotPowerId.SmokestackPower => Math.Min(posAmt, 10) * 2,
            HotPowerId.TheBombPower => Math.Min(posAmt, 3) * 8,
            HotPowerId.TrackingPower => Math.Min(Math.Max(0, amount) / 25, 4) * 6,
            HotPowerId.TrashToTreasurePower => Math.Min(posAmt, 3) * 8,
            HotPowerId.ArcheryPower => Math.Min(posAmt, 8) * 3,
            HotPowerId.HiddenRootsPower => Math.Min(posAmt, 8) * 3,
            HotPowerId.StretchPower => Math.Min(posAmt, 16),
            HotPowerId.SerratedArrowsPower => Math.Min(Math.Max(0, amount) / 25, 4) * 6,
            HotPowerId.HoningPower => Math.Min(Math.Max(0, amount) / 25, 4) * 5,
            HotPowerId.ExplosiveArrowsPower => Math.Min(posAmt, 12) * 2,
            HotPowerId.MythicFormPower => Math.Min(posAmt, 3) * 12,
            HotPowerId.EnnuiPower => Math.Min(posAmt, 3) * 8,
            HotPowerId.LostLutePower => Math.Min(posAmt, 3) * 12,
            HotPowerId.MotifPower => Math.Min(posAmt, 4) * 8,
            HotPowerId.SongPathPower => Math.Min(posAmt, 12) * 2,
            HotPowerId.ViolentChordPower => Math.Min(posAmt, 12) * 2,
            HotPowerId.KiVersePower => Math.Min(posAmt, 3) * 8,
            HotPowerId.TunePower => Math.Min(posAmt, 3) * 8,
            HotPowerId.EcstasyPower => Math.Min(posAmt, 4) * 6,
            HotPowerId.AllIsWellPower => Math.Min(posAmt, 10) * 2,
            HotPowerId.CompostPower => Math.Min(posAmt, 3) * 6,
            HotPowerId.SimpletonPower => Math.Min(posAmt, 3) * 10,
            HotPowerId.GodByrdPower => Math.Min(posAmt, 12) * 2,
            _ => 0,
        };
        if (explicitWeight != 0)
            return Math.Clamp(explicitWeight, -24, 48);

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ComputeRelicGuidanceScore(ushort satisfiedPriority, ushort distance)
    {
        // Decode 4-bit nibble-packed priority counts (P1=1<<0, P2=1<<4, P3=1<<8) so free-alignment
        // guidance stays monotonic in P3 > P2 > P1 and -distance while remaining strictly below 1 HP (39_000L).
        int p1 = satisfiedPriority & 0xF;
        int p2 = (satisfiedPriority >> 4) & 0xF;
        int p3 = (satisfiedPriority >> 8) & 0xF;
        long prioScore = (long)p3 * 20_000L + (long)p2 * 9_500L + (long)p1 * 3_000L;
        if (prioScore > 24_000L)
            prioScore = Math.Min(35_000L, 24_000L + ((prioScore - 24_000L) >> 4));
        return prioScore - (long)Math.Min((int)distance, 30) * 400L;
    }

    private static void ComputeGrowthSummary(
        in CombatHotState s,
        in HotSearchPolicy policy,
        out int credit,
        out int count)
    {
        if (policy.IgnoreLongTermRewards)
        {
            credit = 0;
            count = 0;
            return;
        }

        int totalCount = 0;
        for (int i = 0; i < 11; i++)
            totalCount += s.Growth[i];
        count = totalCount;

        if (totalCount == 0 || !policy.EffectiveGrowthBudgets.IsEnabled)
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
