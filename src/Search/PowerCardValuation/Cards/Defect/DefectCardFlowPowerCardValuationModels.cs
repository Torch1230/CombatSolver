using MegaCrit.Sts2.Core.Models.Cards;
using Req = CombatSolver.PowerCardValuationRequirements;

namespace CombatSolver;

/// <summary>故障机器人牌流、能量与状态放大族（Draft）。</summary>
internal static class DefectCardFlowPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        PowerCardModelRegistration.Register<CreativeAi>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.RemainingTurns | Req.CardValues | Req.Resources,
            static (card, context) =>
            {
                _ = card;
                int turns = Math.Max(0, context.RemainingTurns - 1);
                return new(
                    new PowerCardValuationReward(
                        cardAccess: PowerCardValuationMath.SaturatingProduct(
                            Math.Max(1, context.AverageCardValue),
                            turns)),
                    PowerCardValueFacts.Penalty(in context, turns, delayed: true),
                    PowerCardTiming.BeforeDraw | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<Iteration>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.StatusCards | Req.Draws | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.CardAccessPerTrigger(
                card,
                in context,
                PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 2, 3),
                context.StatusCardTriggers,
                delayed: true)),
        PowerCardModelRegistration.Register<MachineLearning>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.RemainingTurns | Req.CardValues | Req.Resources,
            static (card, context) =>
            {
                _ = card;
                int turns = Math.Max(0, context.RemainingTurns - 1);
                return new(
                    new PowerCardValuationReward(
                        cardAccess: PowerCardValuationMath.SaturatingProduct(
                            Math.Max(1, context.AverageCardValue),
                            turns)),
                    PowerCardValueFacts.Penalty(in context, turns, delayed: true),
                    PowerCardTiming.BeforeDraw | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<Hailstorm>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.Orbs | Req.EnemyHp | Req.EnemyCount | Req.RemainingTurns | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(
                card,
                in context,
                6,
                8,
                context.FrostOrbs > 0
                    ? PowerCardValuationMath.SaturatingProduct(
                        Math.Max(1, context.EnemyCount),
                        Math.Max(1, context.RemainingTurns - 1))
                    : 0,
                delayed: true)),
        PowerCardModelRegistration.Register<Smokestack>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.StatusCards | Req.EnemyHp | Req.EnemyCount | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(
                card,
                in context,
                5,
                7,
                PowerCardValuationMath.SaturatingProduct(
                    Math.Max(1, context.EnemyCount),
                    context.StatusCardTriggers),
                delayed: true)),
        PowerCardModelRegistration.Register<Subroutine>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.PowerPlays | Req.Resources,
            static (card, context) =>
            {
                _ = card;
                int triggers = PowerCardValueFacts.TotalPowerPlays(in context);
                return new(
                    new PowerCardValuationReward(
                        resource: PowerCardValuationMath.SaturatingProduct(
                            triggers,
                            PowerCardValueFacts.EnergyUnit(in context))),
                    PowerCardValueFacts.Penalty(in context, triggers),
                    PowerCardTiming.BeforePower
                    | PowerCardTiming.CurrentTurn
                    | PowerCardTiming.FutureTurns);
            }),
    ];
}

