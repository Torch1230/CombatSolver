using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertGainStarsSuspensionBoundaries(CombatState combat, Player player)
    {
        AssertGainStarsReportsNestedPending(combat, player);
        AssertResourceEffectStopsAfterPendingStars(combat, player);
        AssertOnPlayEffectStopsAfterPendingStars(combat, player);
        AssertBulkExhaustStopsAtNestedPending(combat, player);
    }

    private static void AssertGainStarsReportsNestedPending(CombatState combat, Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        _ = fixture.Combat.AddPowerInstance<BlackHolePower>(
            player.Creature,
            1,
            player.Creature);

        bool completed = fixture.Simulator.GainStars(player, 1);

        AssertSuspended(fixture, completed, "GainStars hook");
    }

    private static void AssertResourceEffectStopsAfterPendingStars(CombatState combat, Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        _ = fixture.Combat.AddPowerInstance<BlackHolePower>(
            player.Creature,
            1,
            player.Creature);
        PredictedCard blade = PredictedCard.Create(ModelDb.Card<SovereignBlade>(), player);
        fixture.Simulator.AddToPile(blade, PileType.Hand);
        decimal damageBefore = blade.Preview.DynamicVars.Damage.BaseValue;
        PredictedCard card = PredictedCard.Create(ModelDb.Card<BigBang>(), player);

        bool supported = CardEffectSpecRegistry.Apply(
            fixture.Simulator,
            fixture.Combat,
            card,
            target: null);

        if (!supported)
            throw new InvalidOperationException("资源效果挂起回归没有进入确定性处理器。");
        AssertPendingChoice(
            fixture.Combat,
            fixture.Hellraiser.Id.Entry,
            PlanChoiceEffect.MoveToHand,
            "resource effect stars");
        if (blade.Preview.DynamicVars.Damage.BaseValue != damageBefore)
            throw new InvalidOperationException("资源效果在 GainStars 挂起后仍执行了后续锻造。");
    }

    private static void AssertOnPlayEffectStopsAfterPendingStars(CombatState combat, Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        _ = fixture.Combat.AddPowerInstance<BlackHolePower>(
            player.Creature,
            1,
            player.Creature);
        PredictedCard card = PredictedCard.Create(ModelDb.Card<HiddenCache>(), player);

        CardOnPlaySupport.Apply(
            fixture.Simulator,
            fixture.Combat,
            card,
            CreatePendingTailCardPlay(card, player),
            target: null,
            processedEnemyDeaths: new HashSet<uint>());

        AssertPendingChoice(
            fixture.Combat,
            fixture.Hellraiser.Id.Entry,
            PlanChoiceEffect.MoveToHand,
            "on-play stars");
        if (fixture.Combat.GetAmount<StarNextTurnPower>(player.Creature) != 0)
            throw new InvalidOperationException("OnPlay 效果在 GainStars 挂起后仍施加了后续状态。");
    }

    private static void AssertBulkExhaustStopsAtNestedPending(CombatState combat, Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        HellraiserPower hellraiser = PrepareDamagePendingChoiceFixture(
            simulator,
            simulatedCombat,
            player);
        _ = simulatedCombat.AddPowerInstance<DarkEmbracePower>(
            player.Creature,
            1,
            player.Creature);
        PredictedCard first = PredictedCard.Create(ModelDb.Card<PommelStrike>(), player);
        PredictedCard sentinel = PredictedCard.Create(ModelDb.Card<DefendDefect>(), player);
        simulator.AddToPile(first, PileType.Hand);
        simulator.AddToPile(sentinel, PileType.Hand);

        simulatedCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        try
        {
            simulator.ExhaustHand(player);

            AssertPendingChoice(
                simulatedCombat,
                hellraiser.Id.Entry,
                PlanChoiceEffect.MoveToHand,
                "bulk exhaust");
            if (first.GetPile(simulator.State)?.Type != PileType.Exhaust
                || sentinel.GetPile(simulator.State)?.Type != PileType.Hand)
            {
                throw new InvalidOperationException("批量穷尽在首个内层选择挂起后仍处理了后续卡牌。");
            }
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
    }
}
