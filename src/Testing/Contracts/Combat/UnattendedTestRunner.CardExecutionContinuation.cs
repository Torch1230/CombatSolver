using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task RunCardExecutionContinuationContractAsync(CombatState live, Player player, IReadOnlyList<string> fixtures)
    {
        foreach (string fixture in fixtures)
        {
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            foreach (var power in live.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
            await ClearPlayerPilesAsync(player);
            string id = fixture switch { "Havoc" => "HAVOC", "Cascade" or "RemovedPrefix" => "CASCADE", "DrawPrefix" => "ACROBATICS",
                "Decisions" => "DECISIONS_DECISIONS", _ => "PREPARED" };
            await InjectCardAsync(live, player, new() { CardId = id, Pile = "Hand" });
            var nativeCard = player.PlayerCombatState!.Hand.Cards.Single();
            if (fixture == "Repeat") nativeCard.BaseReplayCount++;
            foreach (string other in new[] { "STRIKE_SILENT", "DEFEND_SILENT", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD" })
                await InjectCardAsync(live, player, new() { CardId = other, Pile = "Hand" });
            if (fixture == "Decisions") await InjectCardAsync(live, player, new() { CardId = "PREPARED", Pile = "Hand" });
            string[] draw = fixture switch
            {
                "Havoc" => ["PREPARED", "STRIKE_DEFECT", "DEFEND_DEFECT"],
                "Cascade" => ["PREPARED", "ACROBATICS", "PREPARED", "STRIKE_DEFECT", "DEFEND_DEFECT", "STRIKE_REGENT", "DEFEND_REGENT", "DEADLY_POISON", "BACKFLIP"],
                "RemovedPrefix" => ["FOOTWORK", "PREPARED", "PREPARED", "STRIKE_DEFECT", "DEFEND_DEFECT", "STRIKE_REGENT", "DEFEND_REGENT"],
                "DrawPrefix" => ["STRIKE_DEFECT"],
                _ => ["STRIKE_DEFECT", "DEFEND_DEFECT", "STRIKE_REGENT", "DEFEND_REGENT"],
            };
            foreach (string other in draw) await InjectCardAsync(live, player, new() { CardId = other, Pile = "Draw" });
            foreach (string other in new[] { "BASH", "STRIKE_NECROBINDER", "DEFEND_NECROBINDER", "FINESSE", "FLASH_OF_STEEL", "DEADLY_POISON" })
                await InjectCardAsync(live, player, new() { CardId = other, Pile = "Discard" });
            if (fixture == "DrawPrefix") await InjectPowerAsync(live, player, new() { PowerId = "STRATAGEM_POWER", Amount = 1, Target = "Player" });
            SetEnergy(player, 3); SetStars(player, fixture == "Decisions" ? 6 : 0);
            var root = CombatRootSnapshot.Capture(live);
            var parent = root.ForkSimulator();
            if (!parent.CanPlay(parent.State.FindCard(nativeCard)!))
                throw new InvalidOperationException("Nested fixture root card is not playable: " + fixture);
            var names = SolverDisplayNames.Capture(live);
            string Stamp(CombatPredictionSimulator sim) => DescribeContinuationContractState(sim, root, player);
            void Equal(string expected, string actual, string stage)
            {
                if (expected != actual) throw new InvalidOperationException($"Card execution {fixture}/{stage}:\nEXPECTED\n{expected}\nACTUAL\n{actual}");
            }
            CombatPredictionSimulator Replay(IReadOnlyList<PlanCardChoice>? choices, bool capture = false)
            {
                using var isolation = SimulationNotificationIsolation.Enter();
                var sim = parent.Fork(); var combat = (SimulatedCombatState)sim.State.CombatState;
                if (capture && !sim.BeginExecutionContinuationCapture()) throw new InvalidOperationException("Card execution capture did not start.");
                combat.BeginActionChoices(choices);
                try
                {
                    using (combat.BeginCardExecutionScope(new ForkableSet<uint>()))
                        sim.ManualPlay(sim.State.FindCard(nativeCard)!, target: null, out _);
                    if (!sim.HasPendingChoice) CombatBeamSolver.SettleReplayActionBoundary(sim, combat);
                }
                finally { combat.EndActionChoices(); if (capture) sim.EndExecutionContinuationCapture(); }
                return sim;
            }
            string parentBefore = Stamp(parent), liveBefore = ContinuationStamp.CaptureLive(live).StateText;
            var seed = Replay(null, capture: true);
            if (fixture == "RemovedPrefix")
            {
                var removed = seed.History.OfType<CombatPredictionCardPlayFinishedEntry>()
                    .Single(entry => entry.CardPlay.Card.Id.Entry == "FOOTWORK");
                if (!removed.CardPlay.Card.HasBeenRemovedFromState || seed.State.FindCard(removed.Card.Original) != null)
                    throw new InvalidOperationException("Automatic-prefix fixture did not remove its completed power card from every pile.");
            }
            List<PlanCardChoice> prefix = [];
            List<string> visited = [];
            while (seed.HasPendingChoice)
            {
                if (visited.Count >= 12) throw new InvalidOperationException("Card execution did not make bounded progress.");
                var request = ((SimulatedCombatState)seed.State.CombatState).PendingTurnStartChoice
                    ?? throw new InvalidOperationException("Card execution lost its pending request.");
                visited.Add(request.SourceId);
                var continuation = seed.TakeExecutionContinuation()
                    ?? throw new InvalidOperationException($"Card capture lost {fixture}/{request.SourceId} at {prefix.Count}.");
                PlanCardChoice[] choices = CardChoiceSupport.BuildChoices(request.Spec!, names, 12, 12)
                    .Select(choice => choice with { SourceId = request.SourceId, Timing = request.Timing, ContextId = request.ContextId }).ToArray();
                if (fixture == "Decisions" && prefix.Count == 0)
                    choices = choices.OrderByDescending(choice => choice.Cards.Any(token => token.CardId == "PREPARED")).ToArray();
                string seedBefore = Stamp(seed);
                CombatPredictionSimulator Resume(int index)
                {
                    using var isolation = SimulationNotificationIsolation.Enter();
                    CombatPredictionSimulator sim; PredictionExecutionContinuation copied;
                    lock (seed) sim = seed.ForkExecutionContinuation(continuation, out copied);
                    var combat = (SimulatedCombatState)sim.State.CombatState;
                    combat.BeginActionChoices([choices[index]]);
                    try
                    {
                        sim.ResumeExecutionContinuation(copied);
                        if (!sim.HasPendingChoice) CombatBeamSolver.SettleReplayActionBoundary(sim, combat);
                    }
                    finally { combat.EndActionChoices(); }
                    return sim;
                }
                string[] expected = choices.Select(choice => Stamp(Replay([.. prefix, choice]))).ToArray();
                var next = Resume(0); string nextBefore = Stamp(next);
                for (int index = choices.Length - 1; index >= 0; index--)
                    Equal(expected[index], Stamp(Resume(index)), $"choice {prefix.Count}/{index}");
                Equal(nextBefore, Stamp(next), "sibling preserved");
                string[] parallel = await Task.WhenAll(Enumerable.Range(0, Math.Min(2, choices.Length))
                    .Select(index => Task.Run(() => Stamp(Resume(index)))));
                for (int index = 0; index < parallel.Length; index++) Equal(expected[index], parallel[index], "DOP2");
                Equal(seedBefore, Stamp(seed), "seed preserved");
                prefix.Add(choices[0]); seed = next;
            }
            string[] required = fixture switch
            {
                "Havoc" => ["PREPARED"],
                "Cascade" => ["PREPARED", "ACROBATICS", "PREPARED"],
                "RemovedPrefix" => ["PREPARED", "PREPARED"],
                "DrawPrefix" => ["STRATAGEM_POWER", ""],
                "Decisions" => ["", .. Enumerable.Repeat("DECISIONS_DECISIONS", nativeCard.DynamicVars.Repeat.IntValue)],
                _ => ["", ""],
            };
            if (!visited.SequenceEqual(required)) throw new InvalidOperationException($"Card source order {fixture}: {string.Join(',', visited)}.");
            Equal(Stamp(Replay(prefix)), Stamp(seed), "completed replay");
            Equal(Stamp(seed), Stamp(seed.Fork()), "completed Fork");
            foreach (var started in seed.History.OfType<CombatPredictionCardPlayStartedEntry>())
                if (!seed.History.OfType<CombatPredictionCardPlayFinishedEntry>().Any(finished => ReferenceEquals(started.CardPlay, finished.CardPlay)))
                    throw new InvalidOperationException("Card execution lost its paired CardPlay identity.");
            Equal(parentBefore, Stamp(parent), "parent preserved");
            Equal(liveBefore, ContinuationStamp.CaptureLive(live).StateText, "live preserved");
            string expectedNative = ContinuationStamp.CapturePredicted(player, seed, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
            using var session = NativeChoiceRuntime.Begin(live, player, "test:card-execution-continuation");
            session.SetPlanAndStartDriving(NGame.Instance!, prefix.Select(choice => choice.SourceId.Length == 0 ? choice with { SourceId = id } : choice).ToArray(), deadline.Token);
            GameAction native = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is PlayCardAction played && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), nativeCard),
                () => { if (!nativeCard.TryManualPlay(null)) throw new InvalidOperationException("Native nested card could not play: " + id); }, deadline.Token);
            await session.AwaitProducerAndCompleteAsync(native.CompletionTask).WaitAsync(deadline.Token);
            Equal(expectedNative, ContinuationStamp.CaptureLive(live).StateText, "native full continuation");
            _completedChecks.Add($"CardExecutionContinuation:{fixture}:sources={string.Join(',', visited)}:all-branches:recapture:state:history:identity:RNG:DOP2:native");
        }
    }
}
