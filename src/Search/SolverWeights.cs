namespace CombatSolver;

/// <summary>
/// 生存仍优先，但允许用小额战损换取足够输出、易伤与更早击杀。
/// </summary>
internal static class SolverWeights
{
    public const double DeathPenalty = -1_000_000_000_000d;
    public const double VictoryBonus = 10_000_000_000d;
    // 稳健预设的 Beam 排序中，保住 1 HP 约等于造成 3 点即时伤害。
    // 最终路线不靠这个比例决定胜负，只用于避免保血长线在抵达终点前被即时输出挤掉。
    public const double Hp = 30_000d;
    public const double EnemyHp = -10_000d;
    public const double RiskPenalty = -2_000d;
    public const double ActionPenalty = -1d;
    // Vulnerable is an attack multiplier, so its setup value scales with the attacks that can
    // actually consume its remaining turns. One point represents the normal 50% damage bonus.
    public const int VulnerableAttackWindowCap = 24;
    public const double VulnerableAttackMultiplierBeamValue = 5_000d;
    public const double OffTargetVulnerableAttackMultiplierBeamValue = 1_000d;
    // Current-turn energy keeps additional cards executable. This Beam-only value is deliberately
    // higher than one point of raw damage while the actual HP payment remains in the base score.
    public const int CurrentEnergyBeamCap = 6;
    public const double CurrentEnergyBeamValue = 60_000d;
    public const int ExactStatesPerProjectedShuffleOrder = 1;
    public const int PotionEndTurnExactStatesPerProjectedShuffleOrder = 3;
    public const int OrderedPileVariantsPerTacticalState = 24;
    public const int PocketwatchParetoCandidatesPerCadence = 32;
    // 持续能力按真实组合逐步增值。旧的总值 3 点封顶会让 Echo Form、Curious、Buffer
    // 任意一个生效后立刻饱和，Beam 无法区分完整成长引擎和单张能力。
    public const int PersistentBuffDeltaBeamCap = 32;
    public const double PersistentBuffDeltaBeamValue = 50_000d;
    public const int StandardPersistentBuffDeltaBeamCap = 4;
    public const double StandardPersistentBuffDeltaBeamValue = 150_000d;
    public const int LatentSetupBeamCap = 24;
    public const double LatentSetupBeamValue = 12_000d;
    // This represents future attack quality that is still present in live piles. It prevents a
    // destructive choice from looking superior merely because it trades a reusable attack for a
    // little more damage now. Final route selection continues to use actual combat loss.
    // Replay potential is measured in damage-equivalent future card executions. It only keeps setup
    // routes alive until their repeated card effects become concrete combat state.
    public const int ReplayPotentialBeamCap = 64;
    public const double ReplayPotentialBeamValue = 10_000d;
    // Permanent card growth and post-combat rewards get their own Beam value. Final selection is
    // lexicographic, so this value only keeps low-immediate-impact growth routes searchable.
    public const double LongTermResourceBeamValue = 25_000d;
    public const double AngerCopyBeamPenalty = -15_000d;
    public const int RetainedAttackGrowthBeamCap = 16;
    public const double RetainedAttackGrowthBeamValue = 20_000d;
    public const double FutureResourceBeamValue = 10_000d;
    public const double DelayedDamageBeamValue = 10_000d;
    public const double SandpitTurnBeamValue = 30_000d;
    public const int EnemyStrengthSuppressionBeamCap = 16;
    public const int EnemyWeakTurnsBeamCap = 16;
    public const int StandardEnemyStrengthSuppressionHorizon = 4;
    public const int BossEnemyStrengthSuppressionHorizon = 8;
    public const int StandardEnemyWeakExpectedHpSaved = 1;
    public const int BossEnemyWeakExpectedHpSaved = 2;
    // Status/Curse cards that still occupy a live pile reduce future draw quality. Exhausting one is
    // worth slightly less than one point of immediate damage, enough to retain cleanup lines without
    // making them beat a materially stronger attack or block play.
    public const double LiveDeckClutterPenalty = -8_000d;
    // In preserve-resources mode, one stolen card or one stolen gold must outweigh any survivable
    // HP trade inside Beam ranking. Final selection also compares the value lexicographically.
    public const double OutstandingStolenResourcePenalty = -1_000_000d;
    // HP 本身已按 30_000 计价；额外 20_000 使主动卖血总成本仍约等于 5 点伤害。
    public const double SoldHpPenalty = -20_000d;
    public const int NormalSoldHpThreshold = 5;
    public const int EliteSoldHpThreshold = 10;
    public const int BossSoldHpThreshold = 15;
    public const int PotionMinimumHpSaved = 9;

    /// <summary>Potions whose effect is worth roughly twice a baseline potion.</summary>
    public const int PotionHighValueHpSaved = PotionMinimumHpSaved * 2;

    /// <summary>Potions worth roughly one and a half times a baseline potion. Rounded up from 13.5.</summary>
    public const int PotionElevatedValueHpSaved = (PotionMinimumHpSaved * 3 + 1) / 2;
    // This is the minimum cross-turn no-progress horizon and the UI projection horizon. It is not a
    // total turn cap: every new historical combat improvement restarts the no-progress window.
    public const int SetupValueHorizonTurns = 16;
    public const int IncrementalVerificationMaxTurns = 32;
    public const int UiTurnRows = SetupValueHorizonTurns;
    public const int MaximumSearchMaxDegreeOfParallelism = 16;
    public static int DefaultSearchMaxDegreeOfParallelism
        => ResolveDefaultSearchMaxDegreeOfParallelism(Environment.ProcessorCount);

    internal static int ResolveDefaultSearchMaxDegreeOfParallelism(int logicalProcessorCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalProcessorCount, 1);
        if (logicalProcessorCount >= 4)
            return 4;
        return logicalProcessorCount >= 2 ? 2 : 1;
    }

    public const int BackgroundWorkSliceMilliseconds = 4;
    public const int BackgroundYieldCheckInterval = 16;
    public const int ProgressUiIntervalMilliseconds = 200;
}
