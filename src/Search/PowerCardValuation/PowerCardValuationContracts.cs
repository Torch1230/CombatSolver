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

[Flags]
internal enum PowerCommitmentFamily
{
    None = 0,
    DefenseEfficiency = 1 << 0,
    ShivEngine = 1 << 1,
    PoisonEngine = 1 << 2,
    HandEngine = 1 << 3,
    DamageEngine = 1 << 4,
}

[Flags]
internal enum SilentPowerCardIdentity : ulong
{
    None = 0,
    Abrasive = 1UL << 0,
    Accelerant = 1UL << 1,
    Accuracy = 1UL << 2,
    Afterimage = 1UL << 3,
    Envenom = 1UL << 4,
    FanOfKnives = 1UL << 5,
    Footwork = 1UL << 6,
    InfiniteBlades = 1UL << 7,
    MasterPlanner = 1UL << 8,
    NoxiousFumes = 1UL << 9,
    PhantomBlades = 1UL << 10,
    SerpentForm = 1UL << 11,
    Speedster = 1UL << 12,
    ToolsOfTheTrade = 1UL << 13,
    Tracking = 1UL << 14,
    WellLaidPlans = 1UL << 15,
    WraithForm = 1UL << 16,
}

internal readonly record struct PowerCommitmentDescriptor(
    PowerCommitmentFamily Family,
    SilentPowerCardIdentity Card);

[Flags]
internal enum PowerCardValuationRequirements
{
    None = 0,
    EnemyHp = 1 << 0,
    EnemyCount = 1 << 1,
    RemainingTurns = 1 << 2,
    CurrentTurnCards = 1 << 3,
    FutureCards = 1 << 4,
    UnblockedAttackHits = 1 << 5,
    BlockSkills = 1 << 6,
    Shivs = 1 << 7,
    Draws = 1 << 8,
    Discards = 1 << 9,
    IncomingForecast = 1 << 10,
    Poison = 1 << 11,
    WeakTargetDamage = 1 << 12,
    CardValues = 1 << 13,
    Resources = 1 << 14,
    RetainedHandValue = 1 << 15,
    SlyValue = 1 << 16,
    DiscardPayoff = 1 << 17,
    DexterityLoss = 1 << 18,
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
    PowerCardTurnProjection Future);

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
}
