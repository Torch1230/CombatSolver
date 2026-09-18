namespace CombatSolver;

internal static class SilentPowerCardValueFacts
{
    public static int EnergyUnit(in PowerCardValuationContext context)
        => Math.Max(3, context.AverageCardValue);

    public static int ActivationCost(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            PowerCardValuationMath.SaturatingProduct(
                context.EffectiveEnergyCost,
                EnergyUnit(in context)),
            PowerCardValuationMath.SaturatingProduct(
                context.EffectiveStarCost,
                Math.Max(EnergyUnit(in context), context.BestCardValue)));

    public static PowerCardValuationPenalty Penalty(
        in PowerCardValuationContext context,
        int triggers,
        bool delayed = false,
        int antiSynergy = 0)
        => new(
            activationCost: ActivationCost(in context),
            delayedPayoff: delayed && triggers > 0
                ? Math.Max(1, EnergyUnit(in context) / 2)
                : 0,
            triggerScarcity: triggers == 0 ? EnergyUnit(in context) : 0,
            antiSynergy: antiSynergy);

    public static int Damage(int potential, in PowerCardValuationContext context)
        => Math.Min(context.EnemyHp, potential);

    public static int TotalCards(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.UsefulCardPlays,
            context.Future.UsefulCardPlays);

    public static int TotalUnblockedAttackHits(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.UnblockedAttackHits,
            context.Future.UnblockedAttackHits);

    public static int TotalBlockSkills(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.BlockSkillPlays,
            context.Future.BlockSkillPlays);

    public static int TotalShivs(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.ShivPlays,
            context.Future.ShivPlays);

    public static int TotalSkills(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.SkillPlays,
            context.Future.SkillPlays);

    public static int TotalDiscards(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.Discards,
            context.Future.Discards);

    public static int TotalDraws(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.DrawsAfterOpening,
            context.Future.DrawsAfterOpening);

    public static int TotalWeakTargetDamage(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.WeakTargetAttackDamage,
            context.Future.WeakTargetAttackDamage);

    public static int Value(bool upgraded, int normal, int upgradedValue)
        => upgraded ? upgradedValue : normal;

    public static int IntangiblePrevention(int damage, int hits)
        => Math.Max(0, damage - hits);
}
