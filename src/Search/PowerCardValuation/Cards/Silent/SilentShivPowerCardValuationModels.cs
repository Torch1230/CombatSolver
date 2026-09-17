using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver;

internal sealed class AccuracyPowerCardValuationModel : SilentPowerCardValuationModel<Accuracy>
{
    public override PowerCardPool Pool => PowerCardPool.Silent;
    public override PowerCardValuationRequirements Requirements =>
        PowerCardValuationRequirements.EnemyHp |
        PowerCardValuationRequirements.Shivs |
        PowerCardValuationRequirements.CardValues |
        PowerCardValuationRequirements.Resources;

    protected override PowerCardValuationResult Evaluate(
        Accuracy card,
        in PowerCardValuationContext context)
    {
        int triggers = SilentPowerCardValueFacts.TotalShivs(in context);
        int damage = PowerCardValuationMath.SaturatingProduct(
            SilentPowerCardValueFacts.Value(card.IsUpgraded, 4, 6),
            triggers,
            Math.Max(1, context.ShivTargetsPerPlay));
        return new(
            new PowerCardValuationReward(
                damage: SilentPowerCardValueFacts.Damage(damage, in context)),
            SilentPowerCardValueFacts.Penalty(in context, triggers),
            PowerCardTiming.BeforeAttack |
            PowerCardTiming.CurrentTurn |
            PowerCardTiming.FutureTurns);
    }
}

internal sealed class FanOfKnivesPowerCardValuationModel : SilentPowerCardValuationModel<FanOfKnives>
{
    public override PowerCardPool Pool => PowerCardPool.Silent;
    public override PowerCardValuationRequirements Requirements =>
        PowerCardValuationRequirements.EnemyHp |
        PowerCardValuationRequirements.EnemyCount |
        PowerCardValuationRequirements.Shivs |
        PowerCardValuationRequirements.CardValues |
        PowerCardValuationRequirements.Resources;

    protected override PowerCardValuationResult Evaluate(
        FanOfKnives card,
        in PowerCardValuationContext context)
    {
        int enemyCount = Math.Max(1, context.EnemyCount);
        int generatedShivs = SilentPowerCardValueFacts.Value(card.IsUpgraded, 4, 5);
        int generatedDamage = PowerCardValuationMath.SaturatingProduct(
            generatedShivs,
            context.ShivDamage,
            enemyCount);
        int additionalTargets = Math.Max(0, enemyCount - Math.Max(1, context.ShivTargetsPerPlay));
        int existingShivUplift = PowerCardValuationMath.SaturatingProduct(
            SilentPowerCardValueFacts.TotalShivs(in context),
            context.ShivDamage,
            additionalTargets);
        int damage = PowerCardValuationMath.SaturatingSum(generatedDamage, existingShivUplift);
        int triggers = PowerCardValuationMath.SaturatingSum(
            generatedShivs,
            SilentPowerCardValueFacts.TotalShivs(in context));
        return new(
            new PowerCardValuationReward(
                damage: SilentPowerCardValueFacts.Damage(damage, in context)),
            SilentPowerCardValueFacts.Penalty(in context, triggers),
            PowerCardTiming.BeforeAttack |
            PowerCardTiming.CurrentTurn |
            PowerCardTiming.FutureTurns);
    }
}

internal sealed class InfiniteBladesPowerCardValuationModel : SilentPowerCardValuationModel<InfiniteBlades>
{
    public override PowerCardPool Pool => PowerCardPool.Silent;
    public override PowerCardValuationRequirements Requirements =>
        PowerCardValuationRequirements.EnemyHp |
        PowerCardValuationRequirements.RemainingTurns |
        PowerCardValuationRequirements.Shivs |
        PowerCardValuationRequirements.CardValues |
        PowerCardValuationRequirements.Resources;

    protected override PowerCardValuationResult Evaluate(
        InfiniteBlades card,
        in PowerCardValuationContext context)
    {
        int futureTurnStarts = Math.Max(0, context.RemainingTurns - 1);
        int damage = PowerCardValuationMath.SaturatingProduct(
            futureTurnStarts,
            context.ShivDamage,
            Math.Max(1, context.ShivTargetsPerPlay));
        return new(
            new PowerCardValuationReward(
                damage: SilentPowerCardValueFacts.Damage(damage, in context)),
            SilentPowerCardValueFacts.Penalty(
                in context,
                futureTurnStarts,
                delayed: true),
            PowerCardTiming.BeforeTurnEnd |
            PowerCardTiming.FutureTurns);
    }
}

internal sealed class PhantomBladesPowerCardValuationModel : SilentPowerCardValuationModel<PhantomBlades>
{
    public override PowerCardPool Pool => PowerCardPool.Silent;
    public override PowerCardValuationRequirements Requirements =>
        PowerCardValuationRequirements.EnemyHp |
        PowerCardValuationRequirements.RemainingTurns |
        PowerCardValuationRequirements.Shivs |
        PowerCardValuationRequirements.CardValues |
        PowerCardValuationRequirements.Resources;

    protected override PowerCardValuationResult Evaluate(
        PhantomBlades card,
        in PowerCardValuationContext context)
    {
        int triggers = Math.Min(
            SilentPowerCardValueFacts.TotalShivs(in context),
            context.RemainingTurns);
        int damage = PowerCardValuationMath.SaturatingProduct(
            SilentPowerCardValueFacts.Value(card.IsUpgraded, 9, 12),
            triggers,
            Math.Max(1, context.ShivTargetsPerPlay));
        return new(
            new PowerCardValuationReward(
                damage: SilentPowerCardValueFacts.Damage(damage, in context)),
            SilentPowerCardValueFacts.Penalty(in context, triggers),
            PowerCardTiming.BeforeAttack |
            PowerCardTiming.CurrentTurn |
            PowerCardTiming.FutureTurns);
    }
}

