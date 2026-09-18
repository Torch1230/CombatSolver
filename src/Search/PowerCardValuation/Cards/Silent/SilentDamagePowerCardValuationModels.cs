using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver;

internal sealed class SerpentFormPowerCardValuationModel : SilentPowerCardValuationModel<SerpentForm>
{
    public override PowerCardPool Pool => PowerCardPool.Silent;
    public override PowerCardValuationRequirements Requirements =>
        PowerCardValuationRequirements.EnemyHp |
        PowerCardValuationRequirements.CurrentTurnCards |
        PowerCardValuationRequirements.FutureCards |
        PowerCardValuationRequirements.CardValues |
        PowerCardValuationRequirements.Resources;

    protected override PowerCardValuationResult Evaluate(
        SerpentForm card,
        in PowerCardValuationContext context)
    {
        int triggers = SilentPowerCardValueFacts.TotalCards(in context);
        int damage = PowerCardValuationMath.SaturatingProduct(
            SilentPowerCardValueFacts.Value(card.IsUpgraded, 4, 6),
            triggers);
        return new(
            new PowerCardValuationReward(
                damage: SilentPowerCardValueFacts.Damage(damage, in context)),
            SilentPowerCardValueFacts.Penalty(in context, triggers),
            PowerCardTiming.BeforeCard |
            PowerCardTiming.CurrentTurn |
            PowerCardTiming.FutureTurns);
    }
}

internal sealed class TrackingPowerCardValuationModel : SilentPowerCardValuationModel<Tracking>
{
    public override PowerCardPool Pool => PowerCardPool.Silent;
    public override PowerCardValuationRequirements Requirements =>
        PowerCardValuationRequirements.EnemyHp |
        PowerCardValuationRequirements.WeakTargetDamage |
        PowerCardValuationRequirements.CardValues |
        PowerCardValuationRequirements.Resources;

    protected override PowerCardValuationResult Evaluate(
        Tracking card,
        in PowerCardValuationContext context)
    {
        int weakTargetDamage = SilentPowerCardValueFacts.TotalWeakTargetDamage(in context);
        int damage = (int)Math.Min(int.MaxValue, (long)weakTargetDamage * 50 / 100);
        int triggers = weakTargetDamage > 0 ? 1 : 0;
        return new(
            new PowerCardValuationReward(
                damage: SilentPowerCardValueFacts.Damage(damage, in context)),
            SilentPowerCardValueFacts.Penalty(in context, triggers),
            PowerCardTiming.BeforeAttack |
            PowerCardTiming.CurrentTurn |
            PowerCardTiming.FutureTurns);
    }
}

