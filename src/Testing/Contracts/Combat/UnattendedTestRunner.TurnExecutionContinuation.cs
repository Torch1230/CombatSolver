using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task RunTurnExecutionContinuationContractAsync(CombatState live, Player player, IReadOnlyList<string> fixtures)
    {
        foreach (string fixture in fixtures)
        {
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            foreach (var power in live.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
            await ClearPlayerPilesAsync(player);
            bool beforeHandDraw = fixture.StartsWith("Before", StringComparison.Ordinal);
            foreach (string id in new[] { "STRIKE_SILENT", "DEFEND_SILENT", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "STRIKE_DEFECT", "DEFEND_DEFECT" })
                await InjectCardAsync(live, player, new() { CardId = id, Pile = !beforeHandDraw ? "Hand" : fixture == "BeforeShuffle" ? "Discard" : "Draw" });
            if (fixture.EndsWith("Nested", StringComparison.Ordinal))
                foreach (string id in new[] { "BACKFLIP", "DEADLY_POISON", "STRIKE_SILENT", "DEFEND_SILENT" })
                    await InjectCardAsync(live, player, new() { CardId = id, Pile = "Discard" });
            string[] powers = fixture switch
            {
                "After" => ["ENTROPY_POWER", "TOOLS_OF_THE_TRADE_POWER", "TYRANNY_POWER"],
                "BeforeShuffle" => ["INFINITE_BLADES_POWER", "FOREGONE_CONCLUSION_POWER", "STRATAGEM_POWER"],
                "ExhaustNested" => ["TYRANNY_POWER", "DARK_EMBRACE_POWER", "STRATAGEM_POWER"],
                "GamblingNested" => ["STRATAGEM_POWER"],
                _ => ["INFINITE_BLADES_POWER", "FOREGONE_CONCLUSION_POWER"],
            };
            foreach (string id in powers) await InjectPowerAsync(live, player, new() { PowerId = id,
                Amount = fixture == "ExhaustNested" && id == "TYRANNY_POWER" ? 2 : 1, Target = "Player" });
            string[] relics = fixture switch
            {
                "After" => ["CHOICES_PARADOX", "GAMBLING_CHIP", "TOASTY_MITTENS"],
                "ExhaustNested" => ["TOASTY_MITTENS"],
                "GamblingNested" => ["GAMBLING_CHIP", "TOASTY_MITTENS"],
                _ => ["TOOLBOX"],
            };
            foreach (string id in relics) await InjectRelicAsync(player, new() { RelicId = id });
            var root = CombatRootSnapshot.Capture(live);
            var parent = root.ForkSimulator();
            var names = SolverDisplayNames.Capture(live);
            string Stamp(CombatPredictionSimulator sim) => DescribeContinuationContractState(sim, root, player);
            void Equal(string expected, string actual, string stage)
            {
                if (expected != actual) throw new InvalidOperationException($"Turn execution {fixture}/{stage}:\nEXPECTED\n{expected}\nACTUAL\n{actual}");
            }
            CombatPredictionSimulator Replay(IReadOnlyList<PlanCardChoice>? choices, bool capture = false)
            {
                using var isolation = SimulationNotificationIsolation.Enter();
                var sim = parent.Fork(); var combat = (SimulatedCombatState)sim.State.CombatState;
                if (capture && !sim.BeginExecutionContinuationCapture()) throw new InvalidOperationException("Turn capture did not start.");
                combat.BeginActionChoices(choices);
                try
                {
                    if (!beforeHandDraw) combat.TriggerAfterPlayerTurnStart(sim, player.Creature, combat.ActiveExecutionChoices);
                    else combat.PrepareBeforeHandDraw(sim, player, combat.ActiveExecutionChoices);
                    if (!sim.HasPendingChoice) CombatBeamSolver.SettleReplayActionBoundary(sim, combat);
                }
                finally { combat.EndActionChoices(); if (capture) sim.EndExecutionContinuationCapture(); }
                return sim;
            }
            string parentBefore = Stamp(parent), liveBefore = ContinuationStamp.CaptureLive(live).StateText;
            var seed = Replay(null, capture: true);
            List<PlanCardChoice> prefix = [];
            List<string> visited = [];
            while (seed.HasPendingChoice)
            {
                if (visited.Count >= 12) throw new InvalidOperationException("Turn continuation did not make bounded progress.");
                var request = ((SimulatedCombatState)seed.State.CombatState).PendingTurnStartChoice
                    ?? throw new InvalidOperationException("Turn continuation lost its pending request.");
                visited.Add(request.SourceId);
                var continuation = seed.TakeExecutionContinuation()
                    ?? throw new InvalidOperationException($"Turn capture lost {fixture}/{request.SourceId} at {prefix.Count}.");
                PlanCardChoice[] choices = CardChoiceSupport.BuildChoices(request.Spec!, names, 12, 12)
                    .Select(choice => choice with { SourceId = request.SourceId, Timing = request.Timing, ContextId = request.ContextId }).ToArray();
                if (fixture == "GamblingNested" && request.SourceId == "GAMBLING_CHIP")
                    choices = choices.OrderByDescending(choice => choice.Cards.Count).ToArray();
                if (choices.Length < 2 && request.Spec!.Options.Count > 1)
                    throw new InvalidOperationException("Turn fixture did not exercise alternative selections.");
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
                CombatPredictionSimulator next = Resume(0);
                string nextBefore = Stamp(next);
                for (int index = choices.Length - 1; index >= 0; index--)
                    Equal(expected[index], Stamp(Resume(index)), $"{request.SourceId} branch {index}");
                Equal(nextBefore, Stamp(next), "sibling preserved");
                string[] parallel = await Task.WhenAll(Enumerable.Range(0, Math.Min(2, choices.Length))
                    .Select(index => Task.Run(() => Stamp(Resume(index)))));
                for (int index = 0; index < parallel.Length; index++) Equal(expected[index], parallel[index], "DOP2");
                Equal(seedBefore, Stamp(seed), "seed preserved");
                prefix.Add(choices[0]);
                seed = next;
            }
            string[] required = fixture switch
            {
                "After" => [.. powers, .. relics],
                "BeforeShuffle" => ["STRATAGEM_POWER", "FOREGONE_CONCLUSION_POWER", "TOOLBOX"],
                "ExhaustNested" => ["TYRANNY_POWER", "STRATAGEM_POWER", "TOASTY_MITTENS"],
                "GamblingNested" => ["GAMBLING_CHIP", "STRATAGEM_POWER", "TOASTY_MITTENS"],
                _ => ["FOREGONE_CONCLUSION_POWER", "TOOLBOX"],
            };
            if (!visited.SequenceEqual(required))
                throw new InvalidOperationException($"Turn source order {fixture}: {string.Join(',', visited)}.");
            Equal(Stamp(Replay(prefix)), Stamp(seed), "completed replay");
            Equal(Stamp(seed), Stamp(seed.Fork()), "completed Fork");
            Equal(parentBefore, Stamp(parent), "parent preserved");
            Equal(liveBefore, ContinuationStamp.CaptureLive(live).StateText, "live preserved");
            string expectedNative = ContinuationStamp.CapturePredicted(player, seed, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
            using var session = NativeChoiceRuntime.Begin(live, player, "test:turn-execution-continuation");
            session.SetPlanAndStartDriving(NGame.Instance!, prefix, deadline.Token);
            Task native = !beforeHandDraw
                ? Hook.AfterPlayerTurnStart(live, new BlockingPlayerChoiceContext(), player)
                : Hook.BeforeHandDraw(live, player, new BlockingPlayerChoiceContext());
            await session.AwaitProducerAndCompleteAsync(native).WaitAsync(deadline.Token);
            Equal(expectedNative, ContinuationStamp.CaptureLive(live).StateText, "native full continuation");
            _completedChecks.Add($"TurnExecutionContinuation:{fixture}:sources={string.Join(',', visited)}:all-branches:repeated-suspension:state:history:RNG:DOP2:native");
        }
    }
}
