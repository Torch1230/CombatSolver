using MegaCrit.Sts2.Core.Models.Cards;
using Req = CombatSolver.PowerCardValuationRequirements;

namespace CombatSolver;

/// <summary>愚者旋律族（Specified）。旋律触发次数按未来可打出牌数的三分之一保守代理。</summary>
internal static class FoolMelodyPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        PowerCardModelRegistration.Register<ViolentChord>(
            PowerCardPool.Fool,
            FoolPowerRoutePolicy.FamilyFor,
            FoolPowerRoutePolicy.For,
            Req.EnemyHp | Req.EnemyCount | Req.FutureCards | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(card, in context, 9, 12, PowerCardValuationMath.SaturatingProduct(Math.Max(1, context.EnemyCount), MelodyTriggers(in context)))),
        PowerCardModelRegistration.Register<SongPath>(
            PowerCardPool.Fool,
            FoolPowerRoutePolicy.FamilyFor,
            FoolPowerRoutePolicy.For,
            Req.IncomingForecast | Req.FutureCards | Req.Resources,
            static (card, context) => PowerCardValueFacts.BlockPerTrigger(card, in context, 5, 7, MelodyTriggers(in context))),
        PowerCardModelRegistration.Register<Tune>(
            PowerCardPool.Fool,
            FoolPowerRoutePolicy.FamilyFor,
            FoolPowerRoutePolicy.For,
            Req.RemainingTurns | Req.Resources,
            static (card, context) => PowerCardValueFacts.CostReduction(card, in context, energySaved: 1, triggers: PowerCardValueFacts.FutureTurnStarts(in context))),
        PowerCardModelRegistration.Register<Motif>(
            PowerCardPool.Fool,
            FoolPowerRoutePolicy.FamilyFor,
            FoolPowerRoutePolicy.For,
            Req.FutureCards | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.CardAccessPerTrigger(card, in context, 1, MelodyTriggers(in context))),
        PowerCardModelRegistration.Register<Lullaby>(
            PowerCardPool.Fool,
            FoolPowerRoutePolicy.FamilyFor,
            FoolPowerRoutePolicy.For,
            Req.IncomingForecast | Req.FutureCards | Req.Resources,
            static (card, context) => PowerCardValueFacts.BlockPerTrigger(card, in context, 20, 20, Math.Max(1, MelodyTriggers(in context) / 3))),
        PowerCardModelRegistration.Register<LostLute>(
            PowerCardPool.Fool,
            FoolPowerRoutePolicy.FamilyFor,
            FoolPowerRoutePolicy.For,
            Req.FutureCards | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.CardAccessPerTrigger(card, in context, 1, MelodyTriggers(in context))),
    ];

    private static int MelodyTriggers(in PowerCardValuationContext context)
        => PowerCardValueFacts.TotalCards(in context) / 3;
}
