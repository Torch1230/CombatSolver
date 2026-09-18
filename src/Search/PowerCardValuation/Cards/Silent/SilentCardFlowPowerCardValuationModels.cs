using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver;

internal sealed class MasterPlannerPowerCardValuationModel : SilentPowerCardValuationModel<MasterPlanner>
{
    public override PowerCardPool Pool => PowerCardPool.Silent;
    public override PowerCardValuationRequirements Requirements =>
        PowerCardValuationRequirements.CurrentTurnCards |
        PowerCardValuationRequirements.FutureCards |
        PowerCardValuationRequirements.Discards |
        PowerCardValuationRequirements.SlyValue |
        PowerCardValuationRequirements.CardValues |
        PowerCardValuationRequirements.Resources;

    protected override PowerCardValuationResult Evaluate(
        MasterPlanner card,
        in PowerCardValuationContext context)
    {
        int triggers = Math.Min(
            SilentPowerCardValueFacts.TotalSkills(in context),
            SilentPowerCardValueFacts.TotalDiscards(in context));
        int cardAccess = PowerCardValuationMath.SaturatingProduct(
            triggers,
            context.SlyCardValue);
        return new(
            new PowerCardValuationReward(cardAccess: cardAccess),
            SilentPowerCardValueFacts.Penalty(
                in context,
                triggers,
                delayed: true),
            PowerCardTiming.BeforeSkill |
            PowerCardTiming.BeforeDiscard |
            PowerCardTiming.CurrentTurn |
            PowerCardTiming.FutureTurns);
    }
}

internal sealed class SpeedsterPowerCardValuationModel : SilentPowerCardValuationModel<Speedster>
{
    public override PowerCardPool Pool => PowerCardPool.Silent;
    public override PowerCardValuationRequirements Requirements =>
        PowerCardValuationRequirements.EnemyHp |
        PowerCardValuationRequirements.EnemyCount |
        PowerCardValuationRequirements.Draws |
        PowerCardValuationRequirements.CardValues |
        PowerCardValuationRequirements.Resources;

    protected override PowerCardValuationResult Evaluate(
        Speedster card,
        in PowerCardValuationContext context)
    {
        int triggers = SilentPowerCardValueFacts.TotalDraws(in context);
        int damage = PowerCardValuationMath.SaturatingProduct(
            2,
            triggers,
            context.EnemyCount);
        return new(
            new PowerCardValuationReward(
                damage: SilentPowerCardValueFacts.Damage(damage, in context)),
            SilentPowerCardValueFacts.Penalty(in context, triggers),
            PowerCardTiming.BeforeDraw |
            PowerCardTiming.CurrentTurn |
            PowerCardTiming.FutureTurns);
    }
}

internal sealed class ToolsOfTheTradePowerCardValuationModel : SilentPowerCardValuationModel<ToolsOfTheTrade>
{
    public override PowerCardPool Pool => PowerCardPool.Silent;
    public override PowerCardValuationRequirements Requirements =>
        PowerCardValuationRequirements.RemainingTurns |
        PowerCardValuationRequirements.CardValues |
        PowerCardValuationRequirements.DiscardPayoff |
        PowerCardValuationRequirements.Resources;

    protected override PowerCardValuationResult Evaluate(
        ToolsOfTheTrade card,
        in PowerCardValuationContext context)
    {
        int futureTurnStarts = Math.Max(0, context.RemainingTurns - 1);
        int selectionValue = Math.Max(
            1,
            PowerCardValuationMath.SaturatingSum(
                context.AverageCardValue / 2,
                Math.Max(0, context.BestCardValue - context.AverageCardValue),
                context.DiscardPayoffValue));
        int cardAccess = PowerCardValuationMath.SaturatingProduct(
            futureTurnStarts,
            selectionValue);
        return new(
            new PowerCardValuationReward(cardAccess: cardAccess),
            SilentPowerCardValueFacts.Penalty(
                in context,
                futureTurnStarts,
                delayed: true),
            PowerCardTiming.BeforeTurnEnd |
            PowerCardTiming.FutureTurns);
    }
}

internal sealed class WellLaidPlansPowerCardValuationModel : SilentPowerCardValuationModel<WellLaidPlans>
{
    public override PowerCardPool Pool => PowerCardPool.Silent;
    public override PowerCardValuationRequirements Requirements =>
        PowerCardValuationRequirements.RetainedHandValue |
        PowerCardValuationRequirements.CardValues |
        PowerCardValuationRequirements.Resources;

    protected override PowerCardValuationResult Evaluate(
        WellLaidPlans card,
        in PowerCardValuationContext context)
    {
        int triggers = context.RetainedHandValue > 0 ? 1 : 0;
        return new(
            new PowerCardValuationReward(cardAccess: context.RetainedHandValue),
            SilentPowerCardValueFacts.Penalty(in context, triggers, delayed: true),
            PowerCardTiming.BeforeTurnEnd |
            PowerCardTiming.FutureTurns);
    }
}

