using MegaCrit.Sts2.Core.Models.Cards;
using Req = CombatSolver.PowerCardValuationRequirements;

namespace CombatSolver;

/// <summary>储君防御、控制与资源族（Draft）。</summary>
internal static class RegentControlPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        PowerCardModelRegistration.Register<MonarchsGaze>(
            PowerCardPool.Regent,
            RegentPowerRoutePolicy.FamilyFor,
            RegentPowerRoutePolicy.For,
            Req.WeakTargetDamage | Req.FutureCards | Req.Resources,
            static (card, context) =>
            {
                _ = card;
                int triggers = PowerCardValueFacts.TotalAttackPlays(in context);
                return new(
                    new PowerCardValuationReward(
                        prevention: PowerCardValueFacts.BoundedPrevention(
                            triggers,
                            PowerCardValueFacts.IncomingDamage(in context))),
                    PowerCardValueFacts.Penalty(in context, triggers),
                    PowerCardTiming.BeforeAttack
                    | PowerCardTiming.CurrentTurn
                    | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<NeutronAegis>(
            PowerCardPool.Regent,
            RegentPowerRoutePolicy.FamilyFor,
            RegentPowerRoutePolicy.For,
            Req.Plating | Req.RemainingTurns | Req.Resources,
            static (card, context) => PowerCardValueFacts.BlockPerTrigger(
                card,
                in context,
                8,
                11,
                PowerCardValueFacts.FutureTurnStarts(in context),
                delayed: true)),
        PowerCardModelRegistration.Register<Orbit>(
            PowerCardPool.Regent,
            RegentPowerRoutePolicy.FamilyFor,
            RegentPowerRoutePolicy.For,
            Req.EnergyGain | Req.RemainingTurns | Req.Resources,
            static (card, context) => PowerCardValueFacts.EnergyPerTurn(
                card,
                in context,
                1,
                1)),
        PowerCardModelRegistration.Register<PaleBlueDot>(
            PowerCardPool.Regent,
            RegentPowerRoutePolicy.FamilyFor,
            RegentPowerRoutePolicy.For,
            Req.Draws | Req.RemainingTurns | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.CardAccessPerTrigger(
                card,
                in context,
                PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 1, 2),
                PowerCardValueFacts.FutureTurnStarts(in context),
                delayed: true)),
        PowerCardModelRegistration.Register<Parry>(
            PowerCardPool.Regent,
            RegentPowerRoutePolicy.FamilyFor,
            RegentPowerRoutePolicy.For,
            Req.RemainingTurns | Req.IncomingForecast | Req.Resources,
            static (card, context) => PowerCardValueFacts.BlockPerTrigger(
                card,
                in context,
                10,
                14,
                PowerCardValueFacts.FutureTurnStarts(in context),
                delayed: true)),
        PowerCardModelRegistration.Register<PillarOfCreation>(
            PowerCardPool.Regent,
            RegentPowerRoutePolicy.FamilyFor,
            RegentPowerRoutePolicy.For,
            Req.CardGeneration | Req.IncomingForecast | Req.Resources,
            static (card, context) => PowerCardValueFacts.BlockPerTrigger(
                card,
                in context,
                2,
                3,
                context.GeneratedCardTriggers)),
        PowerCardModelRegistration.Register<Royalties>(
            PowerCardPool.Regent,
            RegentPowerRoutePolicy.FamilyFor,
            RegentPowerRoutePolicy.For,
            Req.Resources,
            static (card, context) => PowerCardValueFacts.CrossCombat(
                card,
                in context,
                PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 30, 40))),
        PowerCardModelRegistration.Register<SwordSage>(
            PowerCardPool.Regent,
            RegentPowerRoutePolicy.FamilyFor,
            RegentPowerRoutePolicy.For,
            Req.EnemyHp | Req.RemainingTurns | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(
                card,
                in context,
                Math.Max(1, context.AverageCardValue),
                Math.Max(1, context.AverageCardValue),
                PowerCardValueFacts.FutureTurnStarts(in context),
                delayed: true)),
        PowerCardModelRegistration.Register<Tyranny>(
            PowerCardPool.Regent,
            RegentPowerRoutePolicy.FamilyFor,
            RegentPowerRoutePolicy.For,
            Req.Draws | Req.Exhausts | Req.RemainingTurns | Req.CardValues,
            static (card, context) =>
            {
                _ = card;
                int turns = PowerCardValueFacts.FutureTurnStarts(in context);
                int access = PowerCardValuationMath.SaturatingProduct(
                    Math.Max(1, context.AverageCardValue),
                    turns);
                return new(
                    new PowerCardValuationReward(cardAccess: access),
                    PowerCardValueFacts.Penalty(
                        in context,
                        turns,
                        delayed: true,
                        antiSynergy: turns),
                    PowerCardTiming.BeforeDraw | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<VoidForm>(
            PowerCardPool.Regent,
            RegentPowerRoutePolicy.FamilyFor,
            RegentPowerRoutePolicy.For,
            Req.CurrentTurnCards | Req.FutureCards | Req.Resources,
            static (card, context) => PowerCardValueFacts.CostReduction(
                card,
                in context,
                energySaved: 1,
                triggers: PowerCardValuationMath.SaturatingProduct(
                    2,
                    Math.Max(1, context.RemainingTurns)))),
    ];
}

