using MegaCrit.Sts2.Core.Models.Cards;
using Req = CombatSolver.PowerCardValuationRequirements;

namespace CombatSolver;

/// <summary>无色防御与属性成长族（Draft）。无色能力可在任意角色持有。</summary>
internal static class ColorlessGrowthPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        PowerCardModelRegistration.Register<EternalArmor>(
            PowerCardPool.Colorless,
            ColorlessPowerRoutePolicy.FamilyFor,
            ColorlessPowerRoutePolicy.For,
            Req.Plating | Req.RemainingTurns | Req.IncomingForecast | Req.Resources,
            static (card, context) => PowerCardValueFacts.BlockPerTrigger(
                card,
                in context,
                9,
                12,
                PowerCardValueFacts.FutureTurnStarts(in context),
                delayed: true)),
        PowerCardModelRegistration.Register<Fasten>(
            PowerCardPool.Colorless,
            ColorlessPowerRoutePolicy.FamilyFor,
            ColorlessPowerRoutePolicy.For,
            Req.BlockSkills | Req.IncomingForecast | Req.Resources,
            static (card, context) => PowerCardValueFacts.BlockPerTrigger(
                card,
                in context,
                4,
                6,
                PowerCardValueFacts.TotalBlockSkills(in context))),
        PowerCardModelRegistration.Register<PrepTime>(
            PowerCardPool.Colorless,
            ColorlessPowerRoutePolicy.FamilyFor,
            ColorlessPowerRoutePolicy.For,
            Req.Vigor | Req.FutureCards | Req.EnemyHp | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(
                card,
                in context,
                4,
                6,
                PowerCardValuationMath.SaturatingProduct(
                    Math.Max(1, PowerCardValueFacts.TotalAttackPlays(in context)),
                    1),
                delayed: true)),
        PowerCardModelRegistration.Register<Prowess>(
            PowerCardPool.Colorless,
            ColorlessPowerRoutePolicy.FamilyFor,
            ColorlessPowerRoutePolicy.For,
            Req.CurrentTurnCards | Req.FutureCards | Req.CardValues | Req.Resources,
            static (card, context) =>
            {
                int stat = PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 1, 2);
                int triggers = PowerCardValuationMath.SaturatingSum(
                    PowerCardValueFacts.TotalAttackPlays(in context),
                    PowerCardValueFacts.TotalBlockSkills(in context));
                return new(
                    new PowerCardValuationReward(
                        scaling: PowerCardValuationMath.SaturatingProduct(stat, triggers)),
                    PowerCardValueFacts.Penalty(
                        in context,
                        Math.Max(1, triggers),
                        delayed: false),
                    PowerCardTiming.BeforeAttack | PowerCardTiming.BeforeSkill
                    | PowerCardTiming.CurrentTurn | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<Automation>(
            PowerCardPool.Colorless,
            ColorlessPowerRoutePolicy.FamilyFor,
            ColorlessPowerRoutePolicy.For,
            Req.Draws | Req.RemainingTurns | Req.Resources,
            static (card, context) => PowerCardValueFacts.EnergyPerTurn(
                card,
                in context,
                1,
                1)),
    ];
}

