using MegaCrit.Sts2.Core.Models.Cards;
using Req = CombatSolver.PowerCardValuationRequirements;

namespace CombatSolver;

/// <summary>故障机器人属性成长与费用族（Draft）。</summary>
internal static class DefectGrowthPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        PowerCardModelRegistration.Register<MegaCrit.Sts2.Core.Models.Cards.Buffer>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.IncomingForecast | Req.Resources,
            static (card, context) =>
            {
                int charges = PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 1, 2);
                int raw = PowerCardValuationMath.SaturatingProduct(
                    charges,
                    Math.Max(1, context.NextTurnIncomingDamage));
                return new(
                    new PowerCardValuationReward(
                        prevention: PowerCardValueFacts.BoundedPrevention(
                            raw,
                            PowerCardValueFacts.IncomingDamage(in context))),
                    PowerCardValueFacts.Penalty(
                        in context,
                        context.NextTurnIncomingDamage > 0 ? 1 : 0,
                        delayed: true),
                    PowerCardTiming.BeforeIncomingDamage
                    | PowerCardTiming.CurrentTurn
                    | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<BulkUp>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.Orbs | Req.FutureCards | Req.CardValues | Req.Resources,
            static (card, context) =>
            {
                int stat = PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 2, 3);
                int scaling = PowerCardValuationMath.SaturatingProduct(
                    stat,
                    Math.Max(1, context.CurrentTurn.UsefulCardPlays + context.Future.UsefulCardPlays));
                return new(
                    new PowerCardValuationReward(scaling: scaling),
                    PowerCardValueFacts.Penalty(
                        in context,
                        Math.Max(1, context.OrbCount),
                        delayed: true,
                        antiSynergy: context.OrbCount > 0 ? context.AverageCardValue : 0),
                    PowerCardTiming.BeforeAttack | PowerCardTiming.BeforeSkill
                    | PowerCardTiming.CurrentTurn | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<EchoForm>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.FutureCards | Req.CardValues | Req.Resources,
            static (card, context) =>
            {
                _ = card;
                int turns = Math.Max(0, context.RemainingTurns - 1);
                int doubleValue = PowerCardValuationMath.SaturatingProduct(
                    Math.Max(1, context.BestCardValue),
                    turns);
                return new(
                    new PowerCardValuationReward(cardAccess: doubleValue),
                    PowerCardValueFacts.Penalty(in context, turns, delayed: true),
                    PowerCardTiming.BeforeCard | PowerCardTiming.CurrentTurn
                    | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<Feral>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.ZeroCostAttacks | Req.FutureCards | Req.Resources,
            static (card, context) => PowerCardValueFacts.CostReduction(
                card,
                in context,
                energySaved: 1,
                triggers: context.ZeroCostAttackPlays)),
    ];
}

