using MegaCrit.Sts2.Core.Models.Cards;
using Req = CombatSolver.PowerCardValuationRequirements;

namespace CombatSolver;

/// <summary>储君星星、铸造与生成牌族（Draft）㡣</summary>
internal static class RegentStarPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        PowerCardModelRegistration.Register<Arsenal>(
            PowerCardPool.Regent,
            RegentPowerRoutePolicy.FamilyFor,
            RegentPowerRoutePolicy.For,
            Req.CardGeneration | Req.FutureCards | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.StatGrowth(
                card,
                in context,
                1,
                1,
                perTurn: false)),
        PowerCardModelRegistration.Register<BlackHole>(
            PowerCardPool.Regent,
            RegentPowerRoutePolicy.FamilyFor,
            RegentPowerRoutePolicy.For,
            Req.Stars | Req.EnemyHp | Req.EnemyCount | Req.Resources,
            static (card, context) => PowerCardValueFacts.DamagePerTrigger(
                card,
                in context,
                3,
                4,
                PowerCardValuationMath.SaturatingProduct(
                    Math.Max(1, context.EnemyCount),
                    context.StarGainTriggers + context.StarSpendTriggers))),
        PowerCardModelRegistration.Register<ChildOfTheStars>(
            PowerCardPool.Regent,
            RegentPowerRoutePolicy.FamilyFor,
            RegentPowerRoutePolicy.For,
            Req.Stars | Req.IncomingForecast | Req.Resources,
            static (card, context) => PowerCardValueFacts.BlockPerTrigger(
                card,
                in context,
                2,
                3,
                context.StarSpendAmount,
                delayed: true)),
        PowerCardModelRegistration.Register<Furnace>(
            PowerCardPool.Regent,
            RegentPowerRoutePolicy.FamilyFor,
            RegentPowerRoutePolicy.For,
            Req.Forge | Req.RemainingTurns | Req.Resources,
            static (card, context) => PowerCardValueFacts.EnergyPerTurn(
                card,
                in context,
                5,
                7)),
        PowerCardModelRegistration.Register<Genesis>(
            PowerCardPool.Regent,
            RegentPowerRoutePolicy.FamilyFor,
            RegentPowerRoutePolicy.For,
            Req.Stars | Req.RemainingTurns | Req.Resources,
            static (card, context) => PowerCardValueFacts.EnergyPerTurn(
                card,
                in context,
                2,
                3)),
        PowerCardModelRegistration.Register<SeekingEdge>(
            PowerCardPool.Regent,
            RegentPowerRoutePolicy.FamilyFor,
            RegentPowerRoutePolicy.For,
            Req.Forge | Req.EnemyHp | Req.EnemyCount | Req.Resources,
            static (card, context) =>
            {
                int forge = PowerCardValueFacts.UpgradeValue(card.IsUpgraded, 7, 11);
                int triggers = Math.Max(1, context.ForgeAmount);
                return new(
                    new PowerCardValuationReward(
                        damage: PowerCardValueFacts.DamageCap(
                            PowerCardValuationMath.SaturatingProduct(
                                forge,
                                Math.Max(1, context.EnemyCount)),
                            in context)),
                    PowerCardValueFacts.Penalty(in context, triggers, delayed: true),
                    PowerCardTiming.BeforeAttack | PowerCardTiming.FutureTurns);
            }),
        PowerCardModelRegistration.Register<TheSealedThrone>(
            PowerCardPool.Regent,
            RegentPowerRoutePolicy.FamilyFor,
            RegentPowerRoutePolicy.For,
            Req.Stars | Req.CurrentTurnCards | Req.FutureCards | Req.Resources,
            static (card, context) => PowerCardValueFacts.CostReduction(
                card,
                in context,
                energySaved: 1,
                triggers: PowerCardValueFacts.TotalCards(in context))),
        PowerCardModelRegistration.Register<SpectrumShift>(
            PowerCardPool.Regent,
            RegentPowerRoutePolicy.FamilyFor,
            RegentPowerRoutePolicy.For,
            Req.RemainingTurns | Req.CardValues | Req.Resources,
            static (card, context) => PowerCardValueFacts.CardAccessPerTrigger(
                card,
                in context,
                1,
                PowerCardValueFacts.FutureTurnStarts(in context),
                delayed: true)),
    ];
}

