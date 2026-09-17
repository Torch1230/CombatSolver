using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver;

internal sealed class AccelerantPowerCardValuationModel : SilentPowerCardValuationModel<Accelerant>
{
    public override PowerCardPool Pool => PowerCardPool.Silent;
    public override PowerCardValuationRequirements Requirements =>
        PowerCardValuationRequirements.EnemyHp |
        PowerCardValuationRequirements.Poison |
        PowerCardValuationRequirements.CardValues |
        PowerCardValuationRequirements.Resources;

    protected override PowerCardValuationResult Evaluate(
        Accelerant card,
        in PowerCardValuationContext context)
    {
        int extraTriggers = SilentPowerCardValueFacts.Value(card.IsUpgraded, 1, 2);
        int damage = PowerCardValuationMath.SaturatingProduct(
            extraTriggers,
            context.PoisonTriggerDamage);
        int triggers = context.PoisonTriggerDamage > 0 ? extraTriggers : 0;
        return new(
            new PowerCardValuationReward(
                damage: SilentPowerCardValueFacts.Damage(damage, in context)),
            SilentPowerCardValueFacts.Penalty(in context, triggers, delayed: true),
            PowerCardTiming.BeforePoison |
            PowerCardTiming.CurrentTurn |
            PowerCardTiming.FutureTurns);
    }
}

internal sealed class EnvenomPowerCardValuationModel : SilentPowerCardValuationModel<Envenom>
{
    public override PowerCardPool Pool => PowerCardPool.Silent;
    public override PowerCardValuationRequirements Requirements =>
        PowerCardValuationRequirements.EnemyHp |
        PowerCardValuationRequirements.UnblockedAttackHits |
        PowerCardValuationRequirements.Poison |
        PowerCardValuationRequirements.CardValues |
        PowerCardValuationRequirements.Resources;

    protected override PowerCardValuationResult Evaluate(
        Envenom card,
        in PowerCardValuationContext context)
    {
        int triggers = SilentPowerCardValueFacts.TotalUnblockedAttackHits(in context);
        int poisonStacks = PowerCardValuationMath.SaturatingProduct(
            SilentPowerCardValueFacts.Value(card.IsUpgraded, 1, 2),
            triggers);
        int damage = PowerCardValuationMath.SaturatingProduct(
            poisonStacks,
            context.PoisonStackValue);
        return new(
            new PowerCardValuationReward(
                damage: SilentPowerCardValueFacts.Damage(damage, in context)),
            SilentPowerCardValueFacts.Penalty(in context, triggers, delayed: true),
            PowerCardTiming.BeforeAttack |
            PowerCardTiming.CurrentTurn |
            PowerCardTiming.FutureTurns);
    }
}

internal sealed class NoxiousFumesPowerCardValuationModel : SilentPowerCardValuationModel<NoxiousFumes>
{
    public override PowerCardPool Pool => PowerCardPool.Silent;
    public override PowerCardValuationRequirements Requirements =>
        PowerCardValuationRequirements.EnemyHp |
        PowerCardValuationRequirements.EnemyCount |
        PowerCardValuationRequirements.RemainingTurns |
        PowerCardValuationRequirements.Poison |
        PowerCardValuationRequirements.CardValues |
        PowerCardValuationRequirements.Resources;

    protected override PowerCardValuationResult Evaluate(
        NoxiousFumes card,
        in PowerCardValuationContext context)
    {
        int futureTurnStarts = Math.Max(0, context.RemainingTurns - 1);
        int poisonStacks = PowerCardValuationMath.SaturatingProduct(
            SilentPowerCardValueFacts.Value(card.IsUpgraded, 2, 3),
            context.EnemyCount,
            futureTurnStarts);
        int damage = PowerCardValuationMath.SaturatingProduct(
            poisonStacks,
            context.PoisonStackValue);
        return new(
            new PowerCardValuationReward(
                damage: SilentPowerCardValueFacts.Damage(damage, in context)),
            SilentPowerCardValueFacts.Penalty(
                in context,
                poisonStacks,
                delayed: true),
            PowerCardTiming.BeforeTurnEnd |
            PowerCardTiming.FutureTurns);
    }
}

