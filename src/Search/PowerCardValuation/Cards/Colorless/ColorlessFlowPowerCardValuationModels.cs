using MegaCrit.Sts2.Core.Models.Cards;
using Req = CombatSolver.PowerCardValuationRequirements;

namespace CombatSolver;

/// <summary>无色牌流、生成与延迟伤害族（Draft）。无色能力可在任意角色持有。</summary>
internal static class ColorlessFlowPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        PowerCardModelRegistration.Register<Calamity>(
            PowerCardPool.Colorless,
            ColorlessPowerRoutePolicy.FamilyFor,
            ColorlessPowerRoutePolicy.For,
            Req.CardGeneration | Req.CurrentTurnCards | Req.FutureCards | Req.CardValues,
            static (card, context) => PowerCardValueFacts.CardAccessPerTrigger(
                card,
                in context,
                1,
                PowerCardValueFacts.TotalAttackPlays(in context),
                delayed: true)),
        PowerCardModelRegistration.Register<Entropy>(
            PowerCardPool.Colorless,
            ColorlessPowerRoutePolicy.FamilyFor,
            ColorlessPowerRoutePolicy.For,
            Req.RemainingTurns | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.CardAccessPerTrigger(
                card,
                in context,
                1,
                PowerCardValueFacts.FutureTurnStarts(in context),
                delayed: true)),
        PowerCardModelRegistration.Register<Mayhem>(
            PowerCardPool.Colorless,
            ColorlessPowerRoutePolicy.FamilyFor,
            ColorlessPowerRoutePolicy.For,
            Req.RemainingTurns | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.CardAccessPerTrigger(
                card,
                in context,
                1,
                PowerCardValueFacts.FutureTurnStarts(in context),
                delayed: true)),
        PowerCardModelRegistration.Register<Nostalgia>(
            PowerCardPool.Colorless,
            ColorlessPowerRoutePolicy.FamilyFor,
            ColorlessPowerRoutePolicy.For,
            Req.RetainedHandValue | Req.CurrentTurnCards | Req.FutureCards | Req.CardValues,
            static (card, context) =>
            {
                _ = card;
                int turns = PowerCardValueFacts.FutureTurnStarts(in context);
                return new(
                    new PowerCardValuationReward(
                        cardAccess: PowerCardValuationMath.SaturatingProduct(
                            Math.Max(1, context.BestCardValue),
                            Math.Max(1, turns))),
                    PowerCardValueFacts.Penalty(in context, turns, delayed: true),
                    PowerCardTiming.BeforeTurnEnd | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<Panache>(
            PowerCardPool.Colorless,
            ColorlessPowerRoutePolicy.FamilyFor,
            ColorlessPowerRoutePolicy.For,
            Req.EnemyHp | Req.EnemyCount | Req.CurrentTurnCards | Req.FutureCards,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(
                card,
                in context,
                10,
                14,
                PowerCardValuationMath.SaturatingProduct(
                    Math.Max(1, context.EnemyCount),
                    Math.Max(0, PowerCardValueFacts.TotalCards(in context) / 5)))),
        PowerCardModelRegistration.Register<RollingBoulder>(
            PowerCardPool.Colorless,
            ColorlessPowerRoutePolicy.FamilyFor,
            ColorlessPowerRoutePolicy.For,
            Req.EnemyHp | Req.EnemyCount | Req.RemainingTurns | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(
                card,
                in context,
                5,
                10,
                PowerCardValuationMath.SaturatingProduct(
                    Math.Max(1, context.EnemyCount),
                    PowerCardValueFacts.FutureTurnStarts(in context)),
                delayed: true)),
        PowerCardModelRegistration.Register<Stratagem>(
            PowerCardPool.Colorless,
            ColorlessPowerRoutePolicy.FamilyFor,
            ColorlessPowerRoutePolicy.For,
            Req.Shuffles | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.CardAccessPerTrigger(
                card,
                in context,
                1,
                context.ShuffleTriggers,
                delayed: true)),
    ];
}

