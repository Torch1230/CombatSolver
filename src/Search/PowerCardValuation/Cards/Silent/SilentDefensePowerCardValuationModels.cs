using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver;

internal sealed class AbrasivePowerCardValuationModel : SilentPowerCardValuationModel<Abrasive>
{
    public override PowerCardPool Pool => PowerCardPool.Silent;
    public override PowerCardValuationRequirements Requirements =>
        PowerCardValuationRequirements.EnemyHp |
        PowerCardValuationRequirements.BlockSkills |
        PowerCardValuationRequirements.IncomingForecast |
        PowerCardValuationRequirements.CardValues |
        PowerCardValuationRequirements.Resources;

    protected override PowerCardValuationResult Evaluate(
        Abrasive card,
        in PowerCardValuationContext context)
    {
        int thornDamage = PowerCardValuationMath.SaturatingProduct(
            SilentPowerCardValueFacts.Value(card.IsUpgraded, 4, 6),
            PowerCardValuationMath.SaturatingSum(
                context.CurrentTurn.IncomingHitCount,
                context.Future.IncomingHitCount));
        int dexterityPrevention = SilentPowerCardValueFacts.TotalBlockSkills(in context);
        int triggers = PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.IncomingHitCount,
            context.Future.IncomingHitCount,
            SilentPowerCardValueFacts.TotalBlockSkills(in context));
        return new(
            new PowerCardValuationReward(
                damage: SilentPowerCardValueFacts.Damage(thornDamage, in context),
                prevention: dexterityPrevention),
            SilentPowerCardValueFacts.Penalty(in context, triggers),
            PowerCardTiming.BeforeSkill |
            PowerCardTiming.BeforeIncomingDamage |
            PowerCardTiming.CurrentTurn |
            PowerCardTiming.FutureTurns);
    }
}

internal sealed class AfterimagePowerCardValuationModel : SilentPowerCardValuationModel<Afterimage>
{
    public override PowerCardPool Pool => PowerCardPool.Silent;
    public override PowerCardValuationRequirements Requirements =>
        PowerCardValuationRequirements.CurrentTurnCards |
        PowerCardValuationRequirements.FutureCards |
        PowerCardValuationRequirements.CardValues |
        PowerCardValuationRequirements.Resources;

    protected override PowerCardValuationResult Evaluate(
        Afterimage card,
        in PowerCardValuationContext context)
    {
        int triggers = SilentPowerCardValueFacts.TotalCards(in context);
        return new(
            new PowerCardValuationReward(prevention: triggers),
            SilentPowerCardValueFacts.Penalty(in context, triggers),
            PowerCardTiming.BeforeCard |
            PowerCardTiming.CurrentTurn |
            PowerCardTiming.FutureTurns);
    }
}

internal sealed class FootworkPowerCardValuationModel : SilentPowerCardValuationModel<Footwork>
{
    public override PowerCardPool Pool => PowerCardPool.Silent;
    public override PowerCardValuationRequirements Requirements =>
        PowerCardValuationRequirements.BlockSkills |
        PowerCardValuationRequirements.CardValues |
        PowerCardValuationRequirements.Resources;

    protected override PowerCardValuationResult Evaluate(
        Footwork card,
        in PowerCardValuationContext context)
    {
        int triggers = SilentPowerCardValueFacts.TotalBlockSkills(in context);
        int prevention = PowerCardValuationMath.SaturatingProduct(
            SilentPowerCardValueFacts.Value(card.IsUpgraded, 2, 3),
            triggers);
        return new(
            new PowerCardValuationReward(prevention: prevention),
            SilentPowerCardValueFacts.Penalty(in context, triggers),
            PowerCardTiming.BeforeSkill |
            PowerCardTiming.CurrentTurn |
            PowerCardTiming.FutureTurns);
    }
}

internal sealed class WraithFormPowerCardValuationModel : SilentPowerCardValuationModel<WraithForm>
{
    public override PowerCardPool Pool => PowerCardPool.Silent;
    public override PowerCardValuationRequirements Requirements =>
        PowerCardValuationRequirements.IncomingForecast |
        PowerCardValuationRequirements.DexterityLoss |
        PowerCardValuationRequirements.CardValues |
        PowerCardValuationRequirements.Resources;

    protected override PowerCardValuationResult Evaluate(
        WraithForm card,
        in PowerCardValuationContext context)
    {
        int prevention = SilentPowerCardValueFacts.IntangiblePrevention(
            context.CurrentTurn.IncomingDamage,
            context.CurrentTurn.IncomingHitCount);
        prevention = PowerCardValuationMath.SaturatingSum(
            prevention,
            SilentPowerCardValueFacts.IntangiblePrevention(
                context.NextTurnIncomingDamage,
                context.NextTurnIncomingHitCount));
        if (card.IsUpgraded)
        {
            prevention = PowerCardValuationMath.SaturatingSum(
                prevention,
                SilentPowerCardValueFacts.IntangiblePrevention(
                    context.FollowingTurnIncomingDamage,
                    context.FollowingTurnIncomingHitCount));
        }

        int triggers = prevention > 0 ? 1 : 0;
        return new(
            new PowerCardValuationReward(prevention: prevention),
            SilentPowerCardValueFacts.Penalty(
                in context,
                triggers,
                antiSynergy: context.DexterityLossValue),
            PowerCardTiming.BeforeIncomingDamage |
            PowerCardTiming.CurrentTurn |
            PowerCardTiming.FutureTurns);
    }
}

