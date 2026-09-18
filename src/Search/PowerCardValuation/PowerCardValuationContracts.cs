namespace CombatSolver;

internal enum PowerCardPool
{
    Ironclad,
    Silent,
    Defect,
    Regent,
    Necrobinder,
    Colorless,
}

/// <summary>
/// 能力承诺的机制族。所有角色共用同一组族标签，具体到卡牌的归属由各卡池目录登记；
/// 公共搜索层只按族与优先级做有界保路，不区分具体卡牌。
/// </summary>
[Flags]
internal enum PowerCommitmentFamily
{
    None = 0,
    DefenseEfficiency = 1 << 0,
    ShivEngine = 1 << 1,
    PoisonEngine = 1 << 2,
    HandEngine = 1 << 3,
    DamageEngine = 1 << 4,
    StrengthGrowth = 1 << 5,
    DexterityGrowth = 1 << 6,
    BlockTriggerEngine = 1 << 7,
    ExhaustEngine = 1 << 8,
    EnergyEngine = 1 << 9,
    OrbEngine = 1 << 10,
    FocusEngine = 1 << 11,
    StarEngine = 1 << 12,
    SummonEngine = 1 << 13,
    DoomEngine = 1 << 14,
    CardGenerationEngine = 1 << 15,
    RetainEngine = 1 << 16,
    CostReductionEngine = 1 << 17,
    AutoPlayEngine = 1 << 18,
    StatusAmplifier = 1 << 19,
    LifeInvestment = 1 << 20,
    CrossCombatGrowth = 1 << 21,
    RandomGeneration = 1 << 22,
}

/// <summary>能力路线的保留优先级。只影响中间保路席位，不进入终局比较。</summary>
internal enum PowerRoutePriority
{
    Low,
    Normal,
    Strong,
    Core,
    Dedicated,
}

/// <summary>
/// 逐卡路线准入政策。字段沿用静默猎手第二批生产语义，新增角色只登记数据，不改公共判定顺序。
/// </summary>
internal readonly record struct PowerRouteAdmissionPolicy(
    PowerRoutePriority Priority = PowerRoutePriority.Low,
    int MinimumProjection = 1,
    bool AllowTriggerBackedProjectionFloor = false,
    bool PreferFreeActivation = false,
    bool RequireFreeOrSpareActivation = false,
    bool RequireImmediateDefenseGain = false,
    bool PreferDedicatedSearch = false,
    bool RequirePositiveProjection = false,
    bool NoInCombatCommitment = false);

/// <summary>注册表对外暴露的通用能力承诺描述。公共搜索层只依赖它。</summary>
internal readonly record struct PowerCommitmentDescriptor(
    PowerCardPool Pool,
    string CardId,
    PowerCommitmentFamily Family,
    PowerRouteAdmissionPolicy Admission)
{
    public PowerRoutePriority Priority => Admission.Priority;
}

[Flags]
internal enum PowerCardValuationRequirements : ulong
{
    None = 0,
    EnemyHp = 1UL << 0,
    EnemyCount = 1UL << 1,
    RemainingTurns = 1UL << 2,
    CurrentTurnCards = 1UL << 3,
    FutureCards = 1UL << 4,
    UnblockedAttackHits = 1UL << 5,
    BlockSkills = 1UL << 6,
    Shivs = 1UL << 7,
    Draws = 1UL << 8,
    Discards = 1UL << 9,
    IncomingForecast = 1UL << 10,
    Poison = 1UL << 11,
    WeakTargetDamage = 1UL << 12,
    CardValues = 1UL << 13,
    Resources = 1UL << 14,
    RetainedHandValue = 1UL << 15,
    SlyValue = 1UL << 16,
    DiscardPayoff = 1UL << 17,
    DexterityLoss = 1UL << 18,
    Exhausts = 1UL << 19,
    Orbs = 1UL << 20,
    Focus = 1UL << 21,
    Stars = 1UL << 22,
    Summons = 1UL << 23,
    Doom = 1UL << 24,
    SelfDamage = 1UL << 25,
    CardGeneration = 1UL << 26,
    StatusCards = 1UL << 27,
    Shuffles = 1UL << 28,
    PowerPlays = 1UL << 29,
    ZeroCostAttacks = 1UL << 30,
    Debuffs = 1UL << 31,
    EnergyGain = 1UL << 32,
    Vulnerable = 1UL << 33,
    Forge = 1UL << 34,
    Vigor = 1UL << 35,
    Plating = 1UL << 36,
    EtherealPlays = 1UL << 37,
    SoulPlays = 1UL << 38,
}

[Flags]
internal enum PowerCardTiming
{
    None = 0,
    BeforeCard = 1 << 0,
    BeforeAttack = 1 << 1,
    BeforeSkill = 1 << 2,
    BeforePower = 1 << 3,
    BeforeExhaust = 1 << 4,
    BeforeDraw = 1 << 5,
    BeforeDiscard = 1 << 6,
    BeforePoison = 1 << 7,
    BeforeIncomingDamage = 1 << 8,
    BeforeTurnEnd = 1 << 9,
    CurrentTurn = 1 << 10,
    FutureTurns = 1 << 11,
    ExpiresAtTurnEnd = 1 << 12,
    BeforeStarSpend = 1 << 13,
    BeforeOrbEvoke = 1 << 14,
}

internal readonly record struct PowerCardTurnProjection(
    int UsefulCardPlays,
    int AttackPlays,
    int UnblockedAttackHits,
    int SkillPlays,
    int BlockSkillPlays,
    int PowerPlays,
    int Exhausts,
    int ShivPlays,
    int DrawsAfterOpening,
    int Discards,
    int WeakTargetAttackDamage,
    int IncomingDamage,
    int IncomingHitCount);

internal readonly record struct PowerCardValuationContext(
    int EnemyHp,
    int EnemyCount,
    int RemainingTurns,
    int CurrentEnergy,
    int CurrentStars,
    int EffectiveEnergyCost,
    int EffectiveStarCost,
    int AverageCardValue,
    int BestCardValue,
    int ShivDamage,
    int ShivTargetsPerPlay,
    int PoisonStackValue,
    int PoisonTriggerDamage,
    int RetainedHandValue,
    int SlyCardValue,
    int DiscardPayoffValue,
    int NextTurnIncomingDamage,
    int NextTurnIncomingHitCount,
    int FollowingTurnIncomingDamage,
    int FollowingTurnIncomingHitCount,
    int DexterityLossValue,
    PowerCardTurnProjection CurrentTurn,
    PowerCardTurnProjection Future,
    int OrbCount = 0,
    int DistinctOrbTypes = 0,
    int FrostOrbs = 0,
    int LightningOrbs = 0,
    int DarkOrbs = 0,
    int FocusAmount = 0,
    int StarGainTriggers = 0,
    int StarSpendTriggers = 0,
    int StarSpendAmount = 0,
    int SummonTriggers = 0,
    int SummonAmount = 0,
    int OstyCount = 0,
    int SoulPlays = 0,
    int DoomApplicationTriggers = 0,
    int EnemyDoom = 0,
    int EnergyGainTriggers = 0,
    int StatusCardTriggers = 0,
    int GeneratedCardTriggers = 0,
    int ShuffleTriggers = 0,
    int PowerPlayTriggers = 0,
    int SelfDamageTriggers = 0,
    int DebuffTriggers = 0,
    int ZeroCostAttackPlays = 0,
    int PlayerHpLossWindows = 0,
    int CurseOrStatusInHand = 0,
    int EtherealPlays = 0,
    int StrongestAttackDamage = 0,
    int WeakTargetAttackCount = 0,
    int VulnerableTargetAttackDamage = 0,
    int ForgeAmount = 0);

internal readonly record struct PowerCardValuationReward
{
    public int Damage { get; }
    public int Prevention { get; }
    public int Resource { get; }
    public int CardAccess { get; }
    public int Scaling { get; }
    public int Control { get; }

    public PowerCardValuationReward(
        int damage = 0,
        int prevention = 0,
        int resource = 0,
        int cardAccess = 0,
        int scaling = 0,
        int control = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(damage);
        ArgumentOutOfRangeException.ThrowIfNegative(prevention);
        ArgumentOutOfRangeException.ThrowIfNegative(resource);
        ArgumentOutOfRangeException.ThrowIfNegative(cardAccess);
        ArgumentOutOfRangeException.ThrowIfNegative(scaling);
        ArgumentOutOfRangeException.ThrowIfNegative(control);
        Damage = damage;
        Prevention = prevention;
        Resource = resource;
        CardAccess = cardAccess;
        Scaling = scaling;
        Control = control;
    }

    public int Total => PowerCardValuationMath.SaturatingSum(
        Damage,
        Prevention,
        Resource,
        CardAccess,
        Scaling,
        Control);
}

internal readonly record struct PowerCardValuationPenalty
{
    public int ActivationCost { get; }
    public int DelayedPayoff { get; }
    public int TriggerScarcity { get; }
    public int AntiSynergy { get; }
    public int ExpirationLoss { get; }

    public PowerCardValuationPenalty(
        int activationCost = 0,
        int delayedPayoff = 0,
        int triggerScarcity = 0,
        int antiSynergy = 0,
        int expirationLoss = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(activationCost);
        ArgumentOutOfRangeException.ThrowIfNegative(delayedPayoff);
        ArgumentOutOfRangeException.ThrowIfNegative(triggerScarcity);
        ArgumentOutOfRangeException.ThrowIfNegative(antiSynergy);
        ArgumentOutOfRangeException.ThrowIfNegative(expirationLoss);
        ActivationCost = activationCost;
        DelayedPayoff = delayedPayoff;
        TriggerScarcity = triggerScarcity;
        AntiSynergy = antiSynergy;
        ExpirationLoss = expirationLoss;
    }

    public int Total => PowerCardValuationMath.SaturatingSum(
        ActivationCost,
        DelayedPayoff,
        TriggerScarcity,
        AntiSynergy,
        ExpirationLoss);
}

internal readonly record struct PowerCardValuationResult(
    PowerCardValuationReward Reward,
    PowerCardValuationPenalty Penalty,
    PowerCardTiming Timing)
{
    public int NetStrategicValue => (int)Math.Clamp(
        (long)Reward.Total - Penalty.Total,
        int.MinValue,
        int.MaxValue);
}

internal static class PowerCardValuationMath
{
    public static int SaturatingSum(params ReadOnlySpan<int> values)
    {
        long total = 0;
        foreach (int value in values)
            total += value;
        return (int)Math.Min(int.MaxValue, total);
    }

    public static int SaturatingProduct(params ReadOnlySpan<int> values)
    {
        long product = 1;
        foreach (int value in values)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            product *= value;
            if (product >= int.MaxValue)
                return int.MaxValue;
        }
        return (int)product;
    }

    public static int SaturatingAddNonNegative(int left, int right)
        => (int)Math.Clamp((long)left + right, 0L, int.MaxValue);

    public static int SaturatingMultiplyNonNegative(params ReadOnlySpan<int> values)
    {
        long product = 1;
        foreach (int value in values)
        {
            int factor = Math.Max(0, value);
            if (factor == 0)
                return 0;
            if (product > int.MaxValue / factor)
                return int.MaxValue;
            product *= factor;
        }
        return (int)product;
    }
}
