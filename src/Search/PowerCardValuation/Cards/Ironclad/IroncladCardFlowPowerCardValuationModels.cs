using MegaCrit.Sts2.Core.Models.Cards;
using Req = CombatSolver.PowerCardValuationRequirements;

namespace CombatSolver;

/// <summary>铁甲战士牌流与自动出牌族（Draft）㡣</summary>
internal static class IroncladCardFlowPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        PowerCardModelRegistration.Register<Aggression>(
            PowerCardPool.Ironclad,
            IroncladPowerRoutePolicy.FamilyFor,
            IroncladPowerRoutePolicy.For,
            Req.RemainingTurns | Req.CurrentTurnCards | Req.FutureCards | Req.CardValues,
            static (card, context) =>
            {
                _ = card;
                int turns = PowerCardValueFacts.FutureTurnStarts(in context);
                return new(
                    new PowerCardValuationReward(
                        cardAccess: PowerCardValuationMath.SaturatingProduct(
                            Math.Max(1, context.AverageCardValue),
                            turns)),
                    PowerCardValueFacts.Penalty(in context, turns, delayed: true),
                    PowerCardTiming.BeforeTurnEnd | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<Hellraiser>(
            PowerCardPool.Ironclad,
            IroncladPowerRoutePolicy.FamilyFor,
            IroncladPowerRoutePolicy.For,
            Req.EnemyHp | Req.Draws | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(
                card,
                in context,
                Math.Max(1, context.AverageCardValue),
                Math.Max(1, context.AverageCardValue),
                PowerCardValueFacts.TotalDraws(in context),
                delayed: true)),
        PowerCardModelRegistration.Register<Juggling>(
            PowerCardPool.Ironclad,
            IroncladPowerRoutePolicy.FamilyFor,
            IroncladPowerRoutePolicy.For,
            Req.RemainingTurns | Req.FutureCards | Req.CardValues | Req.Resources,
            static (card, context) =>
            {
                _ = card;
                int turns = PowerCardValueFacts.FutureTurnStarts(in context);
                return new(
                    new PowerCardValuationReward(
                        cardAccess: PowerCardValuationMath.SaturatingProduct(
                            Math.Max(1, context.BestCardValue),
                            turns)),
                    PowerCardValueFacts.Penalty(in context, turns, delayed: true),
                    PowerCardTiming.BeforeTurnEnd | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<Stampede>(
            PowerCardPool.Ironclad,
            IroncladPowerRoutePolicy.FamilyFor,
            IroncladPowerRoutePolicy.For,
            Req.EnemyHp | Req.RemainingTurns | Req.FutureCards | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(
                card,
                in context,
                Math.Max(1, context.AverageCardValue),
                Math.Max(1, context.AverageCardValue),
                PowerCardValueFacts.FutureTurnStarts(in context),
                delayed: true)),
        PowerCardModelRegistration.Register<Pyre>(
            PowerCardPool.Ironclad,
            IroncladPowerRoutePolicy.FamilyFor,
            IroncladPowerRoutePolicy.For,
            Req.RemainingTurns | Req.Resources,
            static (card, context) => PowerCardValueFacts.EnergyPerTurn(
                card,
                in context,
                1,
                2)),
    ];
}

