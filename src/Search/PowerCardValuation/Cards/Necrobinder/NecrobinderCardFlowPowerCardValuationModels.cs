using MegaCrit.Sts2.Core.Models.Cards;
using Req = CombatSolver.PowerCardValuationRequirements;

namespace CombatSolver;

/// <summary>亡灵契约师能量㡣�手牌与生成族（Draft）㡣</summary>
internal static class NecrobinderCardFlowPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        PowerCardModelRegistration.Register<CallOfTheVoid>(
            PowerCardPool.Necrobinder,
            NecrobinderPowerRoutePolicy.FamilyFor,
            NecrobinderPowerRoutePolicy.For,
            Req.RemainingTurns | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.CardAccessPerTrigger(
                card,
                in context,
                1,
                PowerCardValueFacts.FutureTurnStarts(in context),
                delayed: true)),
        PowerCardModelRegistration.Register<DanseMacabre>(
            PowerCardPool.Necrobinder,
            NecrobinderPowerRoutePolicy.FamilyFor,
            NecrobinderPowerRoutePolicy.For,
            Req.IncomingForecast | Req.CurrentTurnCards | Req.FutureCards | Req.Resources,
            static (card, context) => PowerCardValueFacts.BlockPerTrigger(
                card,
                in context,
                4,
                6,
                PowerCardValueFacts.TotalCards(in context))),
        PowerCardModelRegistration.Register<Demesne>(
            PowerCardPool.Necrobinder,
            NecrobinderPowerRoutePolicy.FamilyFor,
            NecrobinderPowerRoutePolicy.For,
            Req.RemainingTurns | Req.Draws | Req.Resources,
            static (card, context) => PowerCardValueFacts.EnergyPerTurn(
                card,
                in context,
                1,
                1)),
        PowerCardModelRegistration.Register<ForbiddenGrimoire>(
            PowerCardPool.Necrobinder,
            NecrobinderPowerRoutePolicy.FamilyFor,
            NecrobinderPowerRoutePolicy.For,
            Req.Resources,
            static (card, context) => PowerCardValueFacts.CrossCombat(
                card,
                in context,
                PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 24, 24))),
        PowerCardModelRegistration.Register<Friendship>(
            PowerCardPool.Necrobinder,
            NecrobinderPowerRoutePolicy.FamilyFor,
            NecrobinderPowerRoutePolicy.For,
            Req.RemainingTurns | Req.Resources,
            static (card, context) =>
            {
                int turns = PowerCardValueFacts.FutureTurnStarts(in context);
                return new(
                    new PowerCardValuationReward(
                        resource: PowerCardValuationMath.SaturatingProduct(
                            Math.Max(1, turns),
                            PowerCardValueFacts.EnergyUnit(in context))),
                    PowerCardValueFacts.Penalty(
                        in context,
                        turns,
                        delayed: true,
                        antiSynergy: PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 2, 1)
                            * Math.Max(1, PowerCardValueFacts.TotalAttackPlays(in context))),
                    PowerCardTiming.BeforeTurnEnd | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<Lethality>(
            PowerCardPool.Necrobinder,
            NecrobinderPowerRoutePolicy.FamilyFor,
            NecrobinderPowerRoutePolicy.For,
            Req.EnemyHp | Req.FutureCards | Req.CardValues | Req.Resources,
            static (card, context) =>
            {
                int percent = PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 50, 75);
                int damage = (int)Math.Min(
                    int.MaxValue,
                    (long)Math.Max(1, context.StrongestAttackDamage) * percent / 100
                        * Math.Max(1, context.RemainingTurns - 1));
                return new(
                    new PowerCardValuationReward(
                        damage: PowerCardValueFacts.DamageCap(damage, in context)),
                    PowerCardValueFacts.Penalty(in context, 1, delayed: true),
                    PowerCardTiming.BeforeAttack
                    | PowerCardTiming.CurrentTurn
                    | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<Neurosurge>(
            PowerCardPool.Necrobinder,
            NecrobinderPowerRoutePolicy.FamilyFor,
            NecrobinderPowerRoutePolicy.For,
            Req.Resources | Req.Draws | Req.EnemyHp,
            static (card, context) =>
            {
                int energy = PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 3, 4);
                return new(
                    new PowerCardValuationReward(
                        resource: PowerCardValuationMath.SaturatingProduct(
                            energy,
                            PowerCardValueFacts.EnergyUnit(in context)),
                        cardAccess: Math.Max(1, context.AverageCardValue) * 2),
                    PowerCardValueFacts.Penalty(
                        in context,
                        1,
                        antiSynergy: PowerCardValueFacts.EnergyUnit(in context)),
                    PowerCardTiming.CurrentTurn | PowerCardTiming.BeforeDraw);
            }),
        PowerCardModelRegistration.Register<Pagestorm>(
            PowerCardPool.Necrobinder,
            NecrobinderPowerRoutePolicy.FamilyFor,
            NecrobinderPowerRoutePolicy.For,
            Req.EtherealPlays | Req.Draws | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.CardAccessPerTrigger(
                card,
                in context,
                1,
                context.EtherealPlays,
                delayed: true)),
        PowerCardModelRegistration.Register<SentryMode>(
            PowerCardPool.Necrobinder,
            NecrobinderPowerRoutePolicy.FamilyFor,
            NecrobinderPowerRoutePolicy.For,
            Req.Summons | Req.RemainingTurns | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.CardAccessPerTrigger(
                card,
                in context,
                1,
                PowerCardValueFacts.FutureTurnStarts(in context),
                delayed: true)),
    ];
}

