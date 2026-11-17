using MegaCrit.Sts2.Core.Models.Cards;
using Req = CombatSolver.PowerCardValuationRequirements;

namespace CombatSolver;

/// <summary>愚者射箭族（Specified）。</summary>
internal static class FoolArrowPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        PowerCardModelRegistration.Register<Archery>(
            PowerCardPool.Fool,
            FoolPowerRoutePolicy.FamilyFor,
            FoolPowerRoutePolicy.For,
            Req.RemainingTurns | Req.EnemyHp | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(card, in context, 4, 5, PowerCardValueFacts.FutureTurnStarts(in context), delayed: true)),
        PowerCardModelRegistration.Register<ExplosiveArrows>(
            PowerCardPool.Fool,
            FoolPowerRoutePolicy.FamilyFor,
            FoolPowerRoutePolicy.For,
            Req.EnemyHp | Req.EnemyCount | Req.FutureCards | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(card, in context, 6, 8, PowerCardValuationMath.SaturatingProduct(Math.Max(1, context.EnemyCount), PowerCardValueFacts.TotalAttackPlays(in context)))),
        PowerCardModelRegistration.Register<Honing>(
            PowerCardPool.Fool,
            FoolPowerRoutePolicy.FamilyFor,
            FoolPowerRoutePolicy.For,
            Req.EnemyHp | Req.FutureCards | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(card, in context, Math.Max(1, context.StrongestAttackDamage / 2), Math.Max(1, context.StrongestAttackDamage / 2), PowerCardValueFacts.TotalAttackPlays(in context), delayed: true)),
        PowerCardModelRegistration.Register<Quiver>(
            PowerCardPool.Fool,
            FoolPowerRoutePolicy.FamilyFor,
            FoolPowerRoutePolicy.For,
            Req.EnemyHp | Req.RemainingTurns | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(card, in context, Math.Max(1, context.StrongestAttackDamage), Math.Max(1, context.StrongestAttackDamage) * 2, PowerCardValueFacts.FutureTurnStarts(in context), delayed: true)),
        PowerCardModelRegistration.Register<SerratedArrows>(
            PowerCardPool.Fool,
            FoolPowerRoutePolicy.FamilyFor,
            FoolPowerRoutePolicy.For,
            Req.EnemyHp | Req.FutureCards | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(card, in context, Math.Max(1, context.StrongestAttackDamage / 4), Math.Max(1, context.StrongestAttackDamage / 2), PowerCardValueFacts.TotalAttackPlays(in context), delayed: true)),
        PowerCardModelRegistration.Register<Yah>(
            PowerCardPool.Fool,
            FoolPowerRoutePolicy.FamilyFor,
            FoolPowerRoutePolicy.For,
            Req.RemainingTurns | Req.EnergyGain | Req.Resources,
            static (card, context) => PowerCardValueFacts.EnergyPerTurn(card, in context, 1, 2)),
    ];
}
