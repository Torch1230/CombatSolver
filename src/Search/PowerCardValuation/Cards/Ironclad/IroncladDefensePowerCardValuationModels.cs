using MegaCrit.Sts2.Core.Models.Cards;
using Req = CombatSolver.PowerCardValuationRequirements;

namespace CombatSolver;

/// <summary>铁甲战士防御族（Draft）。</summary>
internal static class IroncladDefensePowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        PowerCardModelRegistration.Register<Barricade>(
            PowerCardPool.Ironclad,
            IroncladPowerRoutePolicy.FamilyFor,
            IroncladPowerRoutePolicy.For,
            Req.BlockSkills | Req.IncomingForecast | Req.CardValues | Req.Resources,
            static (card, context) =>
            {
                _ = card;
                int raw = PowerCardValuationMath.SaturatingProduct(
                    PowerCardValueFacts.TotalBlockSkills(in context),
                    Math.Max(1, context.AverageCardValue));
                int triggers = PowerCardValueFacts.TotalBlockSkills(in context);
                return new(
                    new PowerCardValuationReward(
                        prevention: PowerCardValueFacts.BoundedPrevention(
                            raw,
                            PowerCardValueFacts.IncomingDamage(in context))),
                    PowerCardValueFacts.Penalty(in context, triggers, delayed: true),
                    PowerCardTiming.BeforeTurnEnd | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<CrimsonMantle>(
            PowerCardPool.Ironclad,
            IroncladPowerRoutePolicy.FamilyFor,
            IroncladPowerRoutePolicy.For,
            Req.RemainingTurns | Req.IncomingForecast | Req.SelfDamage | Req.Resources,
            static (card, context) =>
            {
                int turns = PowerCardValueFacts.FutureTurnStarts(in context);
                int raw = PowerCardValuationMath.SaturatingProduct(
                    PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 7, 10),
                    turns);
                return new(
                    new PowerCardValuationReward(
                        prevention: PowerCardValueFacts.BoundedPrevention(
                            raw,
                            PowerCardValueFacts.IncomingDamage(in context))),
                    PowerCardValueFacts.Penalty(
                        in context,
                        turns,
                        delayed: true,
                        antiSynergy: Math.Max(0, turns)),
                    PowerCardTiming.BeforeTurnEnd | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<StoneArmor>(
            PowerCardPool.Ironclad,
            IroncladPowerRoutePolicy.FamilyFor,
            IroncladPowerRoutePolicy.For,
            Req.RemainingTurns | Req.IncomingForecast | Req.Plating | Req.Resources,
            static (card, context) => PowerCardValueFacts.BlockPerTrigger(
                card,
                in context,
                4,
                6,
                PowerCardValueFacts.FutureTurnStarts(in context),
                delayed: true)),
        PowerCardModelRegistration.Register<Unmovable>(
            PowerCardPool.Ironclad,
            IroncladPowerRoutePolicy.FamilyFor,
            IroncladPowerRoutePolicy.For,
            Req.BlockSkills | Req.IncomingForecast | Req.CardValues | Req.Resources,
            static (card, context) =>
            {
                _ = card;
                int raw = PowerCardValuationMath.SaturatingProduct(
                    Math.Max(1, PowerCardValueFacts.TotalBlockSkills(in context)),
                    Math.Max(1, context.AverageCardValue));
                int triggers = PowerCardValueFacts.TotalBlockSkills(in context);
                return new(
                    new PowerCardValuationReward(
                        prevention: PowerCardValueFacts.BoundedPrevention(
                            raw,
                            PowerCardValueFacts.IncomingDamage(in context))),
                    PowerCardValueFacts.Penalty(in context, triggers, delayed: true),
                    PowerCardTiming.BeforeIncomingDamage
                    | PowerCardTiming.CurrentTurn
                    | PowerCardTiming.FutureTurns);
            }),
    ];
}

