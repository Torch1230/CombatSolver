using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task RunPriorityCardContinuationContractAsync(CombatState live, Player player, string cardId)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (int upgrade in new[] { 0, 1 })
        {
            await ClearPlayerPilesAsync(player);
            await InjectCardAsync(live, player, new UnattendedCardInjection { CardId = cardId, Pile = "Hand", UpgradeLevels = upgrade });
            string[] others = ["DEFEND_SILENT", "STRIKE_SILENT", "BACKFLIP", "DEADLY_POISON", "PIERCING_WAIL", "SURVIVOR", "ACROBATICS"];
            foreach (string id in others.Take(cardId == "ACROBATICS" ? 6 : 7))
                await InjectCardAsync(live, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
            foreach (string id in new[] { "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "STRIKE_DEFECT", "DEFEND_DEFECT", "STRIKE_REGENT", "DEFEND_REGENT" })
                await InjectCardAsync(live, player, new UnattendedCardInjection { CardId = id, Pile = "Draw" });
            SetEnergy(player, 5);
            CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
            SolverDisplayNames names = SolverDisplayNames.Capture(live);
            CombatPredictionSimulator parent = root.ForkSimulator();
            PredictedCard Card(CombatPredictionSimulator sim) => sim.State.GetPlayerCombatState(player).Hand.Cards.Single(c => c.Preview.Id.Entry == cardId);
            int draws = cardId == "ACROBATICS" ? 3 + upgrade : 1 + upgrade;
            int discards = cardId == "ACROBATICS" ? 1 : 1 + upgrade;
            string title = Card(parent).Preview.Title;
            string Stamp(CombatPredictionSimulator sim)
            {
                var combat = (SimulatedCombatState)sim.State.CombatState;
                StateFingerprintBuilder key = new();
                combat.AppendFingerprint(ref key, sim);
                return ContinuationStamp.CapturePredicted(player, sim, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText
                    + "\nKEY=" + key.Finish() + ";SHUFFLES=" + sim.ShuffleEventCount + ";TERMINAL=" + sim.TerminalStamp
                    + ";PROGRESS=" + sim.IsInProgress + ";LOSING=" + sim.IsAboutToLose
                    + "\nHISTORY=" + CardContinuationContractHistoryText(sim);
            }
            void Finish(CombatPredictionSimulator sim, ForkableSet<uint> deaths)
            {
                var combat = (SimulatedCombatState)sim.State.CombatState;
                if (!CorePowerSupport.ApplyEnemyDeathPowers(sim, combat, combat.KnownEnemies, deaths)
                    || !CombatBeamSolver.SettleReplayActionBoundary(sim, combat))
                    throw new InvalidOperationException("Priority fixture produced a post-action choice.");
                sim.CheckWinCondition(root.StartTurnNumber);
            }
            CombatPredictionSimulator Legacy(CombatPredictionSimulator basis, TurnStartChoiceCursor cursor)
            {
                using IDisposable isolation = SimulationNotificationIsolation.Enter();
                var sim = basis.Fork();
                var combat = (SimulatedCombatState)sim.State.CombatState;
                ForkableSet<uint> deaths = [];
                combat.BeginActionChoices(cursor);
                try
                {
                    using (combat.BeginCardExecutionScope(deaths))
                        if (sim.ManualPlay(Card(sim), null, out _)) Finish(sim, deaths);
                }
                finally { combat.EndActionChoices(); }
                return sim;
            }
            CombatPredictionSimulator Replay(CombatPredictionSimulator basis, IReadOnlyList<PlanCardChoice>? choices)
                => Legacy(basis, new(choices));
            CardContinuationContractCheckpoint Pause(CombatPredictionSimulator basis)
            {
                using IDisposable isolation = SimulationNotificationIsolation.Enter();
                var seed = basis.Fork();
                return CaptureCardContinuationContract(seed, Card(seed), null, new ForkableSet<uint>())
                    ?? throw new InvalidOperationException(cardId + " rejected by production continuation eligibility.");
            }
            CombatPredictionSimulator Resume(CardContinuationContractCheckpoint checkpoint, PlanCardChoice choice)
            {
                using IDisposable isolation = SimulationNotificationIsolation.Enter();
                var result = checkpoint.Resume([choice], CancellationToken.None);
                if (!result.Completed) throw new InvalidOperationException("Priority resume unexpectedly incomplete.");
                Finish(result.Simulator, result.Deaths);
                return result.Simulator;
            }
            PlanCardChoice[] Choices(CombatPredictionSimulator basis)
            {
                var discovery = Replay(basis, null);
                if (CardChoiceContinuation.Take(discovery, new()) != null)
                    throw new InvalidOperationException("Ordinary replay unexpectedly captured a continuation.");
                var request = ((SimulatedCombatState)discovery.State.CombatState).PendingTurnStartChoice
                    ?? throw new InvalidOperationException("Missing own choice.");
                var choices = CardChoiceSupport.BuildChoices(request.Spec!, names, 256, 256).ToArray();
                if (choices.Any(c => c.Cards.Count != discards)) throw new InvalidOperationException("Selection cardinality changed.");
                return choices;
            }
            void Equal(string expected, string actual, string label)
            {
                if (expected != actual) throw new InvalidOperationException(cardId + "+" + upgrade + " " + label
                    + " differs:\nEXPECTED\n" + expected + "\nACTUAL\n" + actual);
            }
            string parentBefore = Stamp(parent);
            string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
            PlanCardChoice[] choices = Choices(parent);
            int expectedChoices = cardId == "ACROBATICS" ? 9 + upgrade : (upgrade == 0 ? 8 : 36);
            if (choices.Length != expectedChoices) throw new InvalidOperationException($"Expected all {expectedChoices} combinations, got {choices.Length}.");
            string[] references = choices.Select(choice => Stamp(Replay(parent, [choice]))).ToArray();
            using (var checkpoint = Pause(parent))
            {
                CombatPredictionSimulator first = Resume(checkpoint, choices[0]);
                string firstBefore = Stamp(first);
                for (int i = choices.Length - 1; i >= 0; i--)
                {
                    var sim = Resume(checkpoint, choices[i]);
                    Equal(references[i], Stamp(sim), "all combinations option " + i);
                    AssertCardContinuationContractIdentity(sim, player, cardId);
                    if (sim.History.OfType<CombatPredictionCardDrawnEntry>().Count() != draws)
                        throw new InvalidOperationException("Wrong actual prefix draw count.");
                    Equal(Stamp(sim), Stamp(sim.Fork()), "completed fork");
                }
                Equal(firstBefore, Stamp(first), "earliest sibling unchanged");
                first.State.GetPlayerCombatState(player).DiscardPile.Cards.Single(c => c.Preview.Id.Entry == cardId).Upgrade();
                Equal(references[0], Stamp(Resume(checkpoint, choices[0])), "revisit after card upgrade");
                using Barrier barrier = new(2);
                string[] results = new string[2];
                await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(() =>
                {
                    using IDisposable isolation = SimulationNotificationIsolation.Enter();
                    var result = checkpoint.Resume([choices[i]], CancellationToken.None, () =>
                    {
                        if (!barrier.SignalAndWait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("No two-worker execution overlap.");
                    });
                    if (!result.Completed) throw new InvalidOperationException("Parallel priority resume incomplete.");
                    Finish(result.Simulator, result.Deaths);
                    results[i] = Stamp(result.Simulator);
                })));
                for (int i = 0; i < 2; i++) Equal(references[i], results[i], "DOP2");
            }
            var shuffled = parent.Fork();
            using (SimulationNotificationIsolation.Enter())
                shuffled.AddToPile(shuffled.State.GetPlayerCombatState(player).DrawPile.Cards.ToArray(), PileType.Discard);
            string shuffleBefore = Stamp(shuffled);
            var shuffledChoices = Choices(shuffled);
            using (var checkpoint = Pause(shuffled))
                foreach (var choice in shuffledChoices)
                {
                    var sim = Resume(checkpoint, choice);
                    if (sim.ShuffleEventCount != shuffled.ShuffleEventCount + 1) throw new InvalidOperationException("Shuffle was not exercised.");
                    Equal(Stamp(Replay(shuffled, [choice])), Stamp(sim), "shuffle + all combinations");
                }
            Equal(shuffleBefore, Stamp(shuffled), "shuffle parent");

            // A discarded Sly card opens another selector. The incomplete child is abandoned,
            // and the retained original action plus the complete choice chain is replayed.
            var nested = parent.Fork();
            using (SimulationNotificationIsolation.Enter())
                nested.State.GetPlayerCombatState(player).Hand.Cards.Single(c => c.Preview.Id.Entry == "SURVIVOR").MutablePreview.GiveSingleTurnSly();
            var nestedDiscovery = Replay(nested, null);
            var nestedRequest = ((SimulatedCombatState)nestedDiscovery.State.CombatState).PendingTurnStartChoice!;
            var outer = CardChoiceSupport.BuildRequestedChoice(nestedRequest.Spec!, discards == 1 ? ["SURVIVOR"] : ["SURVIVOR", "STRIKE_SILENT"]);
            using (var checkpoint = Pause(nested))
            {
                using IDisposable isolation = SimulationNotificationIsolation.Enter();
                var interrupted = checkpoint.Resume([outer], CancellationToken.None);
                if (interrupted.Completed || !interrupted.Simulator.HasPendingChoice)
                    throw new InvalidOperationException("Nested Sly selector was incorrectly marked complete.");
                List<PlanCardChoice> chain = [];
                var full = Legacy(nested, TurnStartChoiceCursor.ForAutomaticPolicy(request =>
                {
                    if (chain.Count >= 2) throw new InvalidOperationException("Unexpected third choice.");
                    PlanCardChoice choice = chain.Count == 0 ? outer : CardChoiceSupport.BuildRequestedChoice(request.Spec!, ["DEFEND_SILENT"])
                        with { SourceId = request.SourceId, ContextId = request.ContextId, Timing = request.Timing };
                    chain.Add(choice);
                    return choice;
                }));
                if (chain.Count != 2 || full.HasPendingChoice) throw new InvalidOperationException("Full nested fallback did not finish.");
                Equal(Stamp(full), Stamp(Replay(nested, chain)), "complete nested choice fallback");
            }
            Equal(parentBefore, Stamp(parent), "parent");
            Equal(liveBefore, ContinuationStamp.CaptureLive(live).StateText, "live");
            _completedChecks.Add($"CardContinuationContract:{cardId}+{upgrade}:all={choices.Length}:draw={draws}:discard={discards}:full-state-history-rng:shuffle:DOP2:nested-fallback");

            var expectedNative = Replay(parent, [choices[0]]);
            string expected = ContinuationStamp.CapturePredicted(player, expectedNative, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
            CardModel nativeCard = player.PlayerCombatState!.Hand.Cards.Single(c => c.Id.Entry == cardId);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
            using NativeChoiceSession session = NativeChoiceRuntime.Begin(live, player, "test:priority-card-continuation-contract");
            session.SetPlanAndStartDriving(NGame.Instance!, [choices[0] with { SourceId = cardId }], deadline.Token);
            GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is PlayCardAction played && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), nativeCard),
                () => { if (!nativeCard.TryManualPlay(null)) throw new InvalidOperationException("Native priority card not playable."); }, deadline.Token);
            await session.AwaitProducerAndCompleteAsync(action.CompletionTask).WaitAsync(deadline.Token);
            Equal(expected, ContinuationStamp.CaptureLive(live).StateText, "native full continuation");
            _completedChecks.Add($"CardContinuationContract:{cardId}+{upgrade}:{title}:native-full-continuation");
        }
    }
}
