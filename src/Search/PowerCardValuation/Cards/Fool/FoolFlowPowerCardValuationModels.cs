using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using Req = CombatSolver.PowerCardValuationRequirements;

namespace CombatSolver;

/// <summary>愚者牌流、防御与生成族（Specified）。</summary>
internal static class FoolFlowPowerCardValuationModels
{
    internal static int SadnessGeneratedByPlay(CardModel card)
        => card switch
        {
            SorrowfulStrike or Jest => card.DynamicVars.Cards.IntValue,
            BrightenUp => card.DynamicVars["Sadness"].IntValue,
            Rummage or Vitriol => 1,
            _ => 0,
        };

    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        PowerCardModelRegistration.Register<HiddenRoots>(
            PowerCardPool.Fool,
            FoolPowerRoutePolicy.FamilyFor,
            FoolPowerRoutePolicy.For,
            Req.IncomingForecast | Req.RemainingTurns | Req.Resources,
            static (card, context) => PowerCardValueFacts.BlockPerTrigger(card, in context, 3, 4, PowerCardValueFacts.FutureTurnStarts(in context), delayed: true)),
        PowerCardModelRegistration.Register<AllIsWell>(
            PowerCardPool.Fool,
            FoolPowerRoutePolicy.FamilyFor,
            FoolPowerRoutePolicy.For,
            Req.IncomingForecast | Req.StatusCards | Req.Resources,
            static (card, context) => PowerCardValueFacts.BlockPerTrigger(card, in context, 4, 5, context.StatusCardTriggers, delayed: true)),
        PowerCardModelRegistration.Register<Ennui>(
            PowerCardPool.Fool,
            FoolPowerRoutePolicy.FamilyFor,
            FoolPowerRoutePolicy.For,
            Req.RemainingTurns | Req.Draws | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.CardAccessPerTrigger(card, in context, card.IsUpgraded ? 3 : 2, Math.Min(5, PowerCardValueFacts.FutureTurnStarts(in context)), delayed: true)),
        PowerCardModelRegistration.Register<Ecstasy>(
            PowerCardPool.Fool,
            FoolPowerRoutePolicy.FamilyFor,
            FoolPowerRoutePolicy.For,
            Req.StatusCards | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.CardAccessPerTrigger(card, in context, 1, context.StatusCardTriggers, delayed: true)),
        PowerCardModelRegistration.Register<Simpleton>(
            PowerCardPool.Fool,
            FoolPowerRoutePolicy.FamilyFor,
            FoolPowerRoutePolicy.For,
            Req.RemainingTurns | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.CardAccessPerTrigger(card, in context, 1, PowerCardValueFacts.FutureTurnStarts(in context), delayed: true)),
        PowerCardModelRegistration.Register<MythicForm>(
            PowerCardPool.Fool,
            FoolPowerRoutePolicy.FamilyFor,
            FoolPowerRoutePolicy.For,
            Req.RemainingTurns | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.CardAccessPerTrigger(card, in context, 1, PowerCardValueFacts.FutureTurnStarts(in context), delayed: true)),
    ];
}
