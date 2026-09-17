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
internal enum PowerCardValuationRequirements
{
    None = 0,
    EnemyHp = 1 << 0,
    IncomingDamage = 1 << 1,
    RemainingTurns = 1 << 2,
    CurrentTurnAttacks = 1 << 3,
    CurrentTurnSkills = 1 << 4,
    CurrentTurnPowers = 1 << 5,
    CurrentTurnExhausts = 1 << 6,
    FutureAttacks = 1 << 7,
    FutureSkills = 1 << 8,
    FuturePowers = 1 << 9,
    FutureExhausts = 1 << 10,
    CardValues = 1 << 11,
    Resources = 1 << 12,
}

[Flags]
internal enum PowerCardTiming
{
    None = 0,
    BeforeAttack = 1 << 0,
    BeforeSkill = 1 << 1,
    BeforePower = 1 << 2,
    BeforeExhaust = 1 << 3,
    CurrentTurn = 1 << 4,
    ExpiresAtTurnEnd = 1 << 5,
}

internal readonly record struct PowerCardValuationContext(
    int EnemyHp,
    int IncomingDamage,
    int IncomingHitCount,
    int RemainingTurns,
    int CurrentEnergy,
    int CurrentStars,
    int EffectiveEnergyCost,
    int EffectiveStarCost,
    int CurrentTurnAttacks,
    int CurrentTurnSkills,
    int CurrentTurnPowers,
    int CurrentTurnExhausts,
    int FutureAttacks,
    int FutureSkills,
    int FuturePowers,
    int FutureExhausts,
    int AverageCardValue,
    int BestCardValue,
    bool ExpiresAtTurnEnd);

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
}
