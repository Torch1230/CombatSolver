using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertNativeHandChoiceReplayAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (bool replay in new[] { false, true })
        {
            if (replay) await InjectRelicAsync(player, new UnattendedRelicInjection { RelicId = "THROWING_AXE" });
            await ClearPlayerPilesAsync(player);
            foreach (string id in new[] { "BURNING_PACT", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD" })
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
            for (int i = 0; i < 6; i++)
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Draw" });
            SetEnergy(player, 3);
            CardModel[] hand = player.PlayerCombatState!.Hand.Cards.ToArray();
            PlanCardChoice Choice(CardModel card) => new(PlanChoiceEffect.Exhaust, PileType.Hand,
                [new PlanCardToken(card.Id.Entry, card.CurrentUpgradeLevel, CardChoiceSupport.ChoiceCardKey(card), 0, 0, card.Title)],
                SourceId: "BURNING_PACT");
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
            using NativeChoiceSession session = NativeChoiceRuntime.Begin(combat, player, "test:burning_pact");
            session.SetPlanAndStartDriving(NGame.Instance!, replay ? [Choice(hand[1]), Choice(hand[2])] : [Choice(hand[1])], deadline.Token);
            GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is PlayCardAction played && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), hand[0]),
                () => { if (!hand[0].TryManualPlay(null)) throw new InvalidOperationException("Burning Pact was not playable."); }, deadline.Token);
            await session.AwaitProducerAndCompleteAsync(action.CompletionTask).WaitAsync(deadline.Token);
            int exhausted = player.PlayerCombatState.ExhaustPile.Cards.Count;
            if (exhausted != (replay ? 2 : 1)) throw new InvalidOperationException($"Native choices incomplete: replay={replay}, exhausted={exhausted}.");
            _completedChecks.Add($"NativeHandChoice:BurningPact:ThrowingAxe={replay}:exhausted={exhausted}");
        }
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "BURNING_PACT", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        SetEnergy(player, 3);
        CardModel pact = player.PlayerCombatState!.Hand.Cards.First(c => c.Id.Entry == "BURNING_PACT");
        using CancellationTokenSource recoveryDeadline = new(TimeSpan.FromSeconds(12));
        using NativeChoiceSession broken = NativeChoiceRuntime.Begin(combat, player, "test:choice_mismatch");
        broken.SetPlanAndStartDriving(NGame.Instance!, [new PlanCardChoice(PlanChoiceEffect.Exhaust, PileType.Hand,
            [new PlanCardToken("ABSENT_CARD", 0, "absent", 0, 0, "absent")], SourceId: "BURNING_PACT")], recoveryDeadline.Token);
        GameAction interrupted = await SolverController.EnqueueAndCaptureActionAsync(
            queued => queued is PlayCardAction played && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), pact),
            () => { if (!pact.TryManualPlay(null)) throw new InvalidOperationException("Recovery card was not playable."); }, recoveryDeadline.Token);
        try
        {
            await broken.AwaitProducerAndCompleteAsync(interrupted.CompletionTask).WaitAsync(recoveryDeadline.Token);
            throw new InvalidOperationException("Expected a mismatched choice plan.");
        }
        catch (NativeChoicePlanMismatchException)
        {
            broken.ReleaseVisibleSurface();
        }
        if (!MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand.Instance!.IsInCardSelection)
            throw new InvalidOperationException("Choice drift canceled the live selection instead of handing control back.");
        await broken.SelectVisibleCardsForTesting(NGame.Instance!, [player.PlayerCombatState.Hand.Cards.First(c => c.Id.Entry == "DEFEND_IRONCLAD")], recoveryDeadline.Token);
        await interrupted.CompletionTask.WaitAsync(recoveryDeadline.Token);
        _completedChecks.Add("NativeHandChoice:Mismatch:ManualRecovery:ActionCompleted");
    }
}
