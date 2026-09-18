using MegaCrit.Sts2.Core.Models.Cards;
using Req = CombatSolver.PowerCardValuationRequirements;

namespace CombatSolver;

/// <summary>铁甲战士消耗与费用族（Draft）。</summary>
internal static class IroncladExhaustPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        PowerCardModelRegistration.Register<DarkEmbrace>(
            PowerCardPool.Ironclad,
            IroncladPowerRoutePolicy.FamilyFor,
            IroncladPowerRoutePolicy.For,
            Req.Exhausts | Req.Draws | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.CardAccessPerTrigger(
                card,
                in context,
                PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 1, 1),
                PowerCardValueFacts.TotalExhausts(in context),
                delayed: true)),
        PowerCardModelRegistration.Register<FeelNoPain>(
            PowerCardPool.Ironclad,
            IroncladPowerRoutePolicy.FamilyFor,
            IroncladPowerRoutePolicy.For,
            Req.Exhausts | Req.IncomingForecast | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.BlockPerTrigger(
                card,
                in context,
                3,
                4,
                PowerCardValueFacts.TotalExhausts(in context))),
        PowerCardModelRegistration.Register<Corruption>(
            PowerCardPool.Ironclad,
            IroncladPowerRoutePolicy.FamilyFor,
            IroncladPowerRoutePolicy.For,
            Req.Exhausts | Req.CurrentTurnCards | Req.FutureCards | Req.Resources,
            static (card, context) => PowerCardValueFacts.CostReduction(
                card,
                in context,
                energySaved: 1,
                triggers: PowerCardValueFacts.TotalSkillPlays(in context))),
    ];
}

