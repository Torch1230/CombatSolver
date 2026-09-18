using MegaCrit.Sts2.Core.Models.Cards;
using Req = CombatSolver.PowerCardValuationRequirements;

namespace CombatSolver;

/// <summary>亡灵契约师灾厄、召唤与状态放大族（Draft）。</summary>
internal static class NecrobinderDoomPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        PowerCardModelRegistration.Register<Calcify>(
            PowerCardPool.Necrobinder,
            NecrobinderPowerRoutePolicy.FamilyFor,
            NecrobinderPowerRoutePolicy.For,
            Req.Summons | Req.EnemyHp | Req.RemainingTurns | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(
                card,
                in context,
                4,
                6,
                PowerCardValuationMath.SaturatingProduct(
                    Math.Max(0, context.OstyCount),
                    Math.Max(0, context.RemainingTurns)),
                delayed: true)),
        PowerCardModelRegistration.Register<Countdown>(
            PowerCardPool.Necrobinder,
            NecrobinderPowerRoutePolicy.FamilyFor,
            NecrobinderPowerRoutePolicy.For,
            Req.Doom | Req.EnemyHp | Req.EnemyCount | Req.RemainingTurns,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(
                card,
                in context,
                6,
                9,
                PowerCardValueFacts.FutureTurnStarts(in context),
                delayed: true)),
        PowerCardModelRegistration.Register<DevourLife>(
            PowerCardPool.Necrobinder,
            NecrobinderPowerRoutePolicy.FamilyFor,
            NecrobinderPowerRoutePolicy.For,
            Req.Summons | Req.SoulPlays | Req.Resources,
            static (card, context) =>
            {
                int perSoul = PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 1, 2);
                int triggers = context.SoulPlays;
                return new(
                    new PowerCardValuationReward(
                        resource: PowerCardValuationMath.SaturatingProduct(
                            perSoul,
                            Math.Max(0, triggers),
                            PowerCardValueFacts.EnergyUnit(in context))),
                    PowerCardValueFacts.Penalty(in context, triggers, delayed: true),
                    PowerCardTiming.BeforeCard | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<Haunt>(
            PowerCardPool.Necrobinder,
            NecrobinderPowerRoutePolicy.FamilyFor,
            NecrobinderPowerRoutePolicy.For,
            Req.Doom | Req.SoulPlays | Req.EnemyHp | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(
                card,
                in context,
                7,
                9,
                context.SoulPlays,
                delayed: true)),
        PowerCardModelRegistration.Register<NecroMastery>(
            PowerCardPool.Necrobinder,
            NecrobinderPowerRoutePolicy.FamilyFor,
            NecrobinderPowerRoutePolicy.For,
            Req.Summons | Req.EnemyHp | Req.RemainingTurns | Req.Resources,
            static (card, context) =>
            {
                _ = card;
                int turns = Math.Max(0, context.RemainingTurns);
                return new(
                    new PowerCardValuationReward(scaling: Math.Max(1, turns)),
                    PowerCardValueFacts.Penalty(
                        in context,
                        context.OstyCount,
                        delayed: true),
                    PowerCardTiming.BeforeTurnEnd | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<ReaperForm>(
            PowerCardPool.Necrobinder,
            NecrobinderPowerRoutePolicy.FamilyFor,
            NecrobinderPowerRoutePolicy.For,
            Req.Doom | Req.EnemyHp | Req.FutureCards | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(
                card,
                in context,
                Math.Max(1, context.StrongestAttackDamage),
                Math.Max(1, context.StrongestAttackDamage),
                PowerCardValueFacts.TotalAttackPlays(in context),
                delayed: true)),
        PowerCardModelRegistration.Register<Shroud>(
            PowerCardPool.Necrobinder,
            NecrobinderPowerRoutePolicy.FamilyFor,
            NecrobinderPowerRoutePolicy.For,
            Req.Doom | Req.IncomingForecast | Req.Resources,
            static (card, context) => PowerCardValueFacts.BlockPerTrigger(
                card,
                in context,
                3,
                4,
                context.DoomApplicationTriggers)),
        PowerCardModelRegistration.Register<SleightOfFlesh>(
            PowerCardPool.Necrobinder,
            NecrobinderPowerRoutePolicy.FamilyFor,
            NecrobinderPowerRoutePolicy.For,
            Req.Debuffs | Req.EnemyHp | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(
                card,
                in context,
                9,
                13,
                context.DebuffTriggers)),
        PowerCardModelRegistration.Register<SpiritOfAsh>(
            PowerCardPool.Necrobinder,
            NecrobinderPowerRoutePolicy.FamilyFor,
            NecrobinderPowerRoutePolicy.For,
            Req.IncomingForecast | Req.CurrentTurnCards | Req.FutureCards | Req.Resources,
            static (card, context) => PowerCardValueFacts.BlockPerTrigger(
                card,
                in context,
                4,
                5,
                context.EtherealPlays)),
    ];
}

