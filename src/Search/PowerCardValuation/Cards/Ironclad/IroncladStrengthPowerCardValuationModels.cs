using MegaCrit.Sts2.Core.Models.Cards;
using Req = CombatSolver.PowerCardValuationRequirements;

namespace CombatSolver;

/// <summary>铁甲战士力量与易伤成长族（Draft）。</summary>
internal static class IroncladStrengthPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        PowerCardModelRegistration.Register<Inflame>(
            PowerCardPool.Ironclad,
            IroncladPowerRoutePolicy.FamilyFor,
            IroncladPowerRoutePolicy.For,
            Req.EnemyHp | Req.FutureCards | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.StatGrowth(card, in context, 2, 3, perTurn: false)),
        PowerCardModelRegistration.Register<DemonForm>(
            PowerCardPool.Ironclad,
            IroncladPowerRoutePolicy.FamilyFor,
            IroncladPowerRoutePolicy.For,
            Req.RemainingTurns | Req.FutureCards | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.StatGrowth(card, in context, 3, 4, perTurn: true)),
        PowerCardModelRegistration.Register<Rupture>(
            PowerCardPool.Ironclad,
            IroncladPowerRoutePolicy.FamilyFor,
            IroncladPowerRoutePolicy.For,
            Req.SelfDamage | Req.FutureCards | Req.CardValues | Req.Resources,
            static (card, context) =>
            {
                int stat = PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 1, 2);
                int triggers = context.SelfDamageTriggers;
                int scaling = PowerCardValuationMath.SaturatingProduct(
                    stat,
                    Math.Max(1, PowerCardValueFacts.TotalAttackPlays(in context)));
                return new(
                    new PowerCardValuationReward(scaling: triggers > 0 ? scaling : 0),
                    PowerCardValueFacts.Penalty(in context, triggers, delayed: true),
                    PowerCardTiming.BeforeAttack
                    | PowerCardTiming.CurrentTurn
                    | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<Cruelty>(
            PowerCardPool.Ironclad,
            IroncladPowerRoutePolicy.FamilyFor,
            IroncladPowerRoutePolicy.For,
            Req.Vulnerable | Req.EnemyHp | Req.CardValues | Req.Resources,
            static (card, context) =>
            {
                int percent = PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 25, 50);
                int damage = (int)Math.Min(
                    int.MaxValue,
                    (long)context.VulnerableTargetAttackDamage * percent / 100);
                int triggers = context.VulnerableTargetAttackDamage > 0 ? 1 : 0;
                return new(
                    new PowerCardValuationReward(
                        damage: PowerCardValueFacts.DamageCap(damage, in context)),
                    PowerCardValueFacts.Penalty(in context, triggers, delayed: true),
                    PowerCardTiming.BeforeAttack
                    | PowerCardTiming.CurrentTurn
                    | PowerCardTiming.FutureTurns);
            }),
    ];
}

