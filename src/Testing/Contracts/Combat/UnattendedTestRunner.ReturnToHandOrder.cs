using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // Isolates the return scheduling boundary, not a complete actual/simulated battle.
    // Eligibility is seeded through the same lifecycle callback as a completed card play.
    private static void AssertReturningCardsUsePileOrderAcrossForks(CombatState combat, Player player)
    {
        CombatPredictionSimulator continuous = CreateIsolatedDrawSimulator(combat, player);
        continuous.RemoveFromCombat(continuous.State.GetPlayerCombatState(player).AllCards.ToArray());
        continuous.AddGeneratedCardToCombat(
            PredictedCard.Create(ModelDb.Card<Bolas>(), player), PileType.Hand, player,
            resultKind: CardGenerationResultKind.Fixed);
        continuous.AddGeneratedCardToCombat(
            PredictedCard.Create(ModelDb.Card<ThrummingHatchet>(), player), PileType.Hand, player,
            resultKind: CardGenerationResultKind.Fixed);

        ScheduleReturningCards(continuous, player);
        ResolveReturningCards(continuous, player);
        AssertReturningHand(continuous, player, "first return");

        // Removing both members leaves free slots in the original eligibility set;
        // a fork recreates an empty set without that storage history.
        CombatPredictionSimulator forked = continuous.Fork();
        ScheduleReturningCards(continuous, player);
        ScheduleReturningCards(forked, player);
        ResolveReturningCards(continuous, player);
        ResolveReturningCards(forked, player);
        AssertReturningHand(continuous, player, "second continuous return");
        AssertReturningHand(forked, player, "second forked return");

        // The current pile order, not the order of eligibility registration, is authoritative.
        CombatPredictionSimulator reordered = forked.Fork();
        ScheduleReturningCards(reordered, player);
        SimPlayerCombatState state = reordered.State.GetPlayerCombatState(player);
        PredictedCard hatchet = state.AllCards.Single(card => card.Preview is ThrummingHatchet);
        reordered.AddToPile(hatchet, PileType.Draw);
        ResolveReturningCards(reordered, player);
        string[] actual = state.Hand.Cards.Select(card => card.Preview.Id.Entry).ToArray();
        if (!actual.SequenceEqual(new[] { "THRUMMING_HATCHET", "BOLAS" }))
            throw new InvalidOperationException("回手未遵守入口牌堆顺序：" + string.Join(',', actual));

        ScheduleReturningCards(reordered, player);
        reordered.RemoveFromCombat(state.AllCards.ToArray());
        System.Reflection.FieldInfo eligibilityField = typeof(SimulatedCombatState).GetField(
            "_returnToHandNextTurn", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(SimulatedCombatState), "_returnToHandNextTurn");
        if (eligibilityField.GetValue(reordered.State.CombatState) is HashSet<PredictedCard> { Count: > 0 })
            throw new InvalidOperationException("移出战斗的回手牌仍被资格集合持有。");
        _ = reordered.Fork();
    }

    private static void ScheduleReturningCards(CombatPredictionSimulator simulator, Player player)
    {
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        foreach (string id in new[] { "BOLAS", "THRUMMING_HATCHET" })
        {
            PredictedCard card = state.AllCards.Single(card => card.Preview.Id.Entry == id);
            combat.RecordCardLifecycle(simulator, card);
            simulator.AddToPile(card, PileType.Discard);
        }
    }

    private static void ResolveReturningCards(CombatPredictionSimulator simulator, Player player)
    {
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        if (combat.PrepareBeforeHandDraw(simulator, player, new TurnStartChoiceCursor(null)))
            throw new InvalidOperationException("最小回手夹具出现了意外的选牌挂起。");
    }

    private static void AssertReturningHand(CombatPredictionSimulator simulator, Player player, string phase)
    {
        string[] actual = simulator.State.GetPlayerCombatState(player).Hand.Cards
            .Select(card => card.Preview.Id.Entry).ToArray();
        if (!actual.SequenceEqual(new[] { "BOLAS", "THRUMMING_HATCHET" }))
            throw new InvalidOperationException($"回手顺序在 {phase} 不一致：{string.Join(',', actual)}。");
    }
}
