using MegaCrit.Sts2.Core.Models.Cards;
using Req = CombatSolver.PowerCardValuationRequirements;

namespace CombatSolver;

/// <summary>閾佺敳鎴樺＋瑙﹀彂浼ゅ涓庣姸鎬佹斁澶ф棌锛圖raft锛夈€?/summary>
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
                in context,
                PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 1, 2),
                context.DebuffTriggers,
                delayed: true)),
    ];
}

