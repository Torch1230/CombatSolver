using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // Independent inventory of all 41 single-player native OnPlay selection sources (0.111.0).
    private static readonly string[] ExpandedContinuationCards =
    [
        "ABUNDANCE", "ACROBATICS", "ARMAMENTS", "BEGONE", "BRAND", "BURNING_PACT", "CHARGE",
        "CLEANSE", "COSMIC_INDIFFERENCE", "DAGGER_THROW", "DECISIONS_DECISIONS", "DISCOVERY",
        "DREDGE", "DUAL_WIELD", "GLIMMER", "GRAVEBLAST", "GUARDS", "HAND_TRICK", "HEADBUTT",
        "HEIRLOOM_HAMMER", "HIDDEN_DAGGERS", "HOLOGRAM", "NEOWS_FURY", "NIGHTMARE", "PHOTON_CUT",
        "PREPARED", "PURITY", "QUASAR", "SCAVENGE", "SCULPTING_STRIKE", "SEANCE", "SECRET_TECHNIQUE",
        "SECRET_WEAPON", "SEEKER_STRIKE", "SNAP", "SPLASH", "SURVIVOR", "THINKING_AHEAD", "TRANSFIGURE",
        "TRUE_GRIT", "WISH",
    ];

    private async Task RunExpandedCardContinuationContractAsync(CombatState live, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        await OstyCmd.Summon(new BlockingPlayerChoiceContext(), player, 10, null);
        foreach (string id in ExpandedContinuationCards)
        foreach (int upgrade in new[] { 0, 1 })
        {
            await ClearPlayerPilesAsync(player);
            await InjectCardAsync(live, player, new() { CardId = id, UpgradeLevels = upgrade, Pile = "Hand" });
            foreach (string other in new[] { "STRIKE_SILENT", "DEFEND_SILENT", "BACKFLIP", "FINESSE" })
                await InjectCardAsync(live, player, new() { CardId = other, Pile = "Hand" });
            foreach (string other in new[] { "STRIKE_DEFECT", "DEFEND_DEFECT", "DEADLY_POISON", "FLASH_OF_STEEL" })
                await InjectCardAsync(live, player, new() { CardId = other, Pile = "Draw" });
            foreach (string other in new[] { "BASH", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD" })
                await InjectCardAsync(live, player, new() { CardId = other, Pile = "Discard" });
            SetEnergy(player, 30); SetStars(player, 10);
            CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
            SolverDisplayNames names = SolverDisplayNames.Capture(live);
            var parent = root.ForkSimulator();
            Creature enemy = ((SimulatedCombatState)parent.State.CombatState).HittableEnemies.First();
            PredictedCard Card(CombatPredictionSimulator sim) => sim.State.GetPlayerCombatState(player).Hand.Cards.Single(c => c.Preview.Id.Entry == id);
            Creature? target = parent.GetTargetType(Card(parent)) == TargetType.AnyEnemy ? enemy : null;
            string Stamp(CombatPredictionSimulator sim) => DescribeContinuationContractState(sim, root, player);
            void Equal(string expected, string actual, string stage)
            {
                if (expected != actual) throw new InvalidOperationException($"{id}+{upgrade} {stage}:\nEXPECTED\n{expected}\nACTUAL\n{actual}");
            }
            void Finish(CombatPredictionSimulator sim, ForkableSet<uint> deaths)
            {
                var combat = (SimulatedCombatState)sim.State.CombatState;
                if (!CorePowerSupport.ApplyEnemyDeathPowers(sim, combat, combat.KnownEnemies, deaths)
                    || !CombatBeamSolver.SettleReplayActionBoundary(sim, combat))
                    throw new InvalidOperationException(id + " produced an unexpected post-action selector.");
                sim.CheckWinCondition(root.StartTurnNumber);
            }
            CombatPredictionSimulator Legacy(IReadOnlyList<PlanCardChoice>? choices)
            {
                using var isolation = SimulationNotificationIsolation.Enter();
                var sim = parent.Fork(); var combat = (SimulatedCombatState)sim.State.CombatState;
                ForkableSet<uint> deaths = [];
                combat.BeginActionChoices(choices);
                try
                {
                    using (combat.BeginCardExecutionScope(deaths))
                        if (sim.ManualPlay(Card(sim), target, out _)) Finish(sim, deaths);
                }
                finally { combat.EndActionChoices(); }
                return sim;
            }
            string parentBefore = Stamp(parent), liveBefore = ContinuationStamp.CaptureLive(live).StateText;
            var discovery = Legacy(null);
            var request = ((SimulatedCombatState)discovery.State.CombatState).PendingTurnStartChoice;
            bool hasChoice = !(id == "ARMAMENTS" && upgrade == 1 || id == "TRUE_GRIT" && upgrade == 0);
            if (hasChoice != (request != null)) throw new InvalidOperationException(id + " unexpected selection availability.");
            var seed = parent.Fork();
            CardContinuationContractCheckpoint? captured;
            using (SimulationNotificationIsolation.Enter())
                captured = CaptureCardContinuationContract(seed, Card(seed), target, []);
            using var checkpoint = captured;
            if (!hasChoice)
            {
                if (checkpoint != null) throw new InvalidOperationException(id + " captured a nonexistent selector.");
                _completedChecks.Add($"ExpandedCardContinuation:{id}+{upgrade}:no-selection:no-checkpoint");
                continue;
            }
            if (checkpoint == null) throw new InvalidOperationException($"{id}+{upgrade} failed to capture own selection.");
            var choices = CardChoiceSupport.BuildChoices(request!.Spec!, names, 256, 256).ToArray();
            if (choices.Length == 0) throw new InvalidOperationException(id + " fixture has no choices.");
            var references = choices.Select(choice => Stamp(Legacy([choice]))).ToArray();
            CombatPredictionSimulator Resume(int index)
            {
                using var isolation = SimulationNotificationIsolation.Enter();
                var child = checkpoint.Resume([choices[index]], CancellationToken.None);
                if (!child.Completed) throw new InvalidOperationException(id + " unexpectedly needs a nested choice.");
                Finish(child.Simulator, child.Deaths);
                return child.Simulator;
            }
            for (int index = choices.Length - 1; index >= 0; index--)
            {
                var resumed = Resume(index);
                Equal(references[index], Stamp(resumed), "all selections " + index);
                var starts = resumed.History.OfType<CombatPredictionCardPlayStartedEntry>().ToArray();
                var ends = resumed.History.OfType<CombatPredictionCardPlayFinishedEntry>().ToArray();
                foreach (var started in starts)
                    if (!ends.Any(end => ReferenceEquals(started.CardPlay, end.CardPlay) && ReferenceEquals(started.Trace, end.Trace)))
                        throw new InvalidOperationException(id + " lost CardPlay/trace identity.");
                Equal(Stamp(resumed), Stamp(resumed.Fork()), "completed state Fork");
            }
            var first = Resume(0); string firstBefore = Stamp(first);
            _ = Resume(choices.Length - 1);
            Equal(firstBefore, Stamp(first), "earliest sibling preserved");
            var generated = first.History.OfType<CombatPredictionCardGenerationOptionsEntry>().LastOrDefault();
            using (SimulationNotificationIsolation.Enter())
            {
                if (generated != null) generated.Options[0].Upgrade();
                else first.State.GetPlayerCombatState(player).Hand.Cards[0].Upgrade();
            }
            Equal(references[0], Stamp(Resume(0)), "revisit after sibling mutation");
            string[] parallel = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(() => Stamp(Resume(index % choices.Length)))));
            for (int index = 0; index < parallel.Length; index++) Equal(references[index % choices.Length], parallel[index], "DOP2");
            Equal(parentBefore, Stamp(parent), "parent preserved");
            Equal(liveBefore, ContinuationStamp.CaptureLive(live).StateText, "live preserved");
            _completedChecks.Add($"ExpandedCardContinuation:{id}+{upgrade}:choices={choices.Length}:state:history:RNG:identity:siblings:DOP2");

            // Representatives cross each changed prefix family through the actual native action.
            if (upgrade == 0 && id is "THINKING_AHEAD" or "GLIMMER" or "PHOTON_CUT" or "SURVIVOR"
                or "ARMAMENTS" or "HOLOGRAM" or "SEEKER_STRIKE" or "ABUNDANCE" or "CLEANSE" or "SNAP")
            {
                int index = Array.FindIndex(choices, choice => choice.Cards.Count > 0);
                var expected = Legacy([choices[index]]);
                string expectedNative = ContinuationStamp.CapturePredicted(player, expected, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
                CardModel nativeCard = player.PlayerCombatState!.Hand.Cards.Single(c => c.Id.Entry == id);
                using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
                using var session = NativeChoiceRuntime.Begin(live, player, "test:expanded-card-continuation");
                session.SetPlanAndStartDriving(NGame.Instance!, [choices[index] with { SourceId = id }], deadline.Token);
                GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                    queued => queued is PlayCardAction played && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), nativeCard),
                    () => { if (!nativeCard.TryManualPlay(target)) throw new InvalidOperationException("Native expanded card not playable: " + id); }, deadline.Token);
                await session.AwaitProducerAndCompleteAsync(action.CompletionTask).WaitAsync(deadline.Token);
                Equal(expectedNative, ContinuationStamp.CaptureLive(live).StateText, "native full continuation");
                _completedChecks.Add($"ExpandedCardContinuation:{id}+{upgrade}:native-full-continuation");
            }
        }
    }

    private static string DescribeContinuationContractState(CombatPredictionSimulator simulator, CombatRootSnapshot root, Player player)
    {
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        StateFingerprintBuilder key = new(); combat.AppendFingerprint(ref key, simulator);
        return ContinuationStamp.CapturePredicted(player, simulator, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText
            + "\nKEY=" + key.Finish() + ";SHUFFLES=" + simulator.ShuffleEventCount + ";TERMINAL=" + simulator.TerminalStamp
            + ";PROGRESS=" + simulator.IsInProgress + ";LOSING=" + simulator.IsAboutToLose
            + "\nHISTORY=" + CardContinuationContractHistoryText(simulator);
    }
}
