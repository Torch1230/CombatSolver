using MegaCrit.Sts2.Core.Models.Cards;
using Req = CombatSolver.PowerCardValuationRequirements;

namespace CombatSolver;

/// <summary>故障机器人球与集中族（Draft）。</summary>
internal static class DefectOrbPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        PowerCardModelRegistration.Register<BiasedCognition>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.Orbs | Req.Focus | Req.RemainingTurns | Req.Resources,
            static (card, context) =>
            {
                int focus = PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 5, 6);
                int scaling = PowerCardValuationMath.SaturatingProduct(
                    focus,
                    Math.Max(1, context.OrbCount),
                    Math.Max(1, context.RemainingTurns));
                return new(
                    new PowerCardValuationReward(scaling: scaling),
                    PowerCardValueFacts.Penalty(
                        in context,
                        context.OrbCount,
                        delayed: true,
                        antiSynergy: context.RemainingTurns),
                    PowerCardTiming.BeforeOrbEvoke | PowerCardTiming.CurrentTurn
                    | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<Defragment>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.Orbs | Req.Focus | Req.RemainingTurns | Req.Resources,
            static (card, context) =>
            {
                int focus = PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 1, 2);
                int scaling = PowerCardValuationMath.SaturatingProduct(
                    focus,
                    Math.Max(1, context.OrbCount),
                    Math.Max(1, context.RemainingTurns));
                return new(
                    new PowerCardValuationReward(scaling: scaling),
                    PowerCardValueFacts.Penalty(in context, context.OrbCount, delayed: true),
                    PowerCardTiming.BeforeOrbEvoke | PowerCardTiming.CurrentTurn
                    | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<Capacitor>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.Orbs | Req.CardValues | Req.Resources,
            static (card, context) =>
            {
                int slots = PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 2, 3);
                return new(
                    new PowerCardValuationReward(
                        resource: PowerCardValuationMath.SaturatingProduct(
                            slots,
                            Math.Max(1, context.AverageCardValue))),
                    PowerCardValueFacts.Penalty(in context, context.OrbCount, delayed: true),
                    PowerCardTiming.BeforeOrbEvoke | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<ConsumingShadow>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.Orbs | Req.EnemyHp | Req.RemainingTurns | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(
                card,
                in context,
                6,
                6,
                PowerCardValuationMath.SaturatingProduct(
                    Math.Max(0, context.DarkOrbs),
                    Math.Max(0, context.RemainingTurns - 1)),
                delayed: true)),
        PowerCardModelRegistration.Register<Coolant>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.Orbs | Req.IncomingForecast | Req.RemainingTurns | Req.Resources,
            static (card, context) => PowerCardValueFacts.BlockPerTrigger(
                card,
                in context,
                2,
                3,
                PowerCardValuationMath.SaturatingProduct(
                    Math.Max(0, context.DistinctOrbTypes),
                    Math.Max(0, context.RemainingTurns - 1)),
                delayed: true)),
        PowerCardModelRegistration.Register<Loop>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.Orbs | Req.RemainingTurns | Req.CardValues | Req.Resources,
            static (card, context) =>
            {
                int triggers = Math.Max(1, context.OrbCount);
                int scaling = PowerCardValuationMath.SaturatingProduct(
                    triggers,
                    Math.Max(1, context.AverageCardValue),
                    Math.Max(1, context.RemainingTurns - 1));
                return new(
                    new PowerCardValuationReward(scaling: scaling),
                    PowerCardValueFacts.Penalty(in context, context.OrbCount, delayed: true),
                    PowerCardTiming.BeforeOrbEvoke | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<Spinner>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.Orbs | Req.RemainingTurns | Req.Resources,
            static (card, context) =>
            {
                int turns = Math.Max(0, context.RemainingTurns - 1);
                return new(
                    new PowerCardValuationReward(resource: Math.Max(1, turns)),
                    PowerCardValueFacts.Penalty(in context, turns, delayed: true),
                    PowerCardTiming.BeforeOrbEvoke | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<Storm>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.Orbs | Req.PowerPlays | Req.EnemyHp | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(
                card,
                in context,
                Math.Max(1, context.AverageCardValue),
                Math.Max(1, context.AverageCardValue),
                PowerCardValueFacts.TotalPowerPlays(in context),
                delayed: true)),
        PowerCardModelRegistration.Register<Thunder>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.Orbs | Req.EnemyHp | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(
                card,
                in context,
                8,
                11,
                Math.Max(0, context.LightningOrbs),
                delayed: true)),
        PowerCardModelRegistration.Register<TrashToTreasure>(
            PowerCardPool.Defect,
            DefectPowerRoutePolicy.FamilyFor,
            DefectPowerRoutePolicy.For,
            Req.Orbs | Req.StatusCards | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.CardAccessPerTrigger(
                card,
                in context,
                1,
                context.StatusCardTriggers,
                delayed: true)),
    ];
}

