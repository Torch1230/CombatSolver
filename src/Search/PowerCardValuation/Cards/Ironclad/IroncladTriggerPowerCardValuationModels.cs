using MegaCrit.Sts2.Core.Models.Cards;
using Req = CombatSolver.PowerCardValuationRequirements;

namespace CombatSolver;

/// <summary>铁甲战士触发伤害与状态放大族（Draft）。</summary>
internal static class IroncladTriggerPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        PowerCardModelRegistration.Register<Juggernaut>(
            PowerCardPool.Ironclad,
            IroncladPowerRoutePolicy.FamilyFor,
            IroncladPowerRoutePolicy.For,
            Req.EnemyHp | Req.BlockSkills | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(
                card,
                in context,
                6,
                8,
                PowerCardValueFacts.TotalBlockSkills(in context))),
        PowerCardModelRegistration.Register<Inferno>(
            PowerCardPool.Ironclad,
            IroncladPowerRoutePolicy.FamilyFor,
            IroncladPowerRoutePolicy.For,
            Req.EnemyHp | Req.EnemyCount | Req.SelfDamage | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(
                card,
                in context,
                6,
                9,
                PowerCardValuationMath.SaturatingProduct(
                    Math.Max(1, context.EnemyCount),
                    context.SelfDamageTriggers),
                delayed: true,
                PowerCardTiming.BeforeTurnEnd | PowerCardTiming.FutureTurns)),
        PowerCardModelRegistration.Register<Vicious>(
            PowerCardPool.Ironclad,
            IroncladPowerRoutePolicy.FamilyFor,
            IroncladPowerRoutePolicy.For,
            Req.Vulnerable | Req.Debuffs | Req.Draws | Req.Resources,
            static (card, context) => PowerCardValueFacts.CardAccessPerTrigger(
                card,
                context,
                PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 1, 2),
                context.DebuffTriggers,
                delayed: false)),
    ];
}

