using System.Text.Json;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCompactHandEndAsync(CombatState combat, Player player)
    {
        List<object> evidence = [];
        // Capture once on the main thread. The worker receives only an explicit phase
        // argument, and never reads changing native animation settings.
        FastModeType nativeMode = SaveManager.Instance.PrefsSave.FastMode;
        if (nativeMode is not (FastModeType.Normal or FastModeType.Instant))
            throw new NotSupportedException("Hand-end fixture admits Normal or Instant native staging.");
        bool together = nativeMode == FastModeType.Instant;
        var staging = together ? ResumableDiscardProgram.HandEndStaging.Together : ResumableDiscardProgram.HandEndStaging.Sequential;
        for (int mode = 0; mode < 3; mode++)
        {
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
            ClearRunDeck((RunState)combat.RunState, player);
            await ClearPlayerPilesAsync(player);
            foreach (string id in new[] { "BURN", "DEFY", "BURN", "DEFEND_SILENT", "BURN" })
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
            if (mode == 1)
            {
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "ESCAPE_PLAN", Pile = "Hand" });
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "BURN", Pile = "Draw" });
            }
            await CreatureCmd.SetCurrentHp(player.Creature, mode == 2 ? 1 : 10);
            await SetBlockAsync(player.Creature, mode == 0 ? 6 : mode == 1 ? 1 : 0);
            await PowerCmd.Apply<StrengthPower>(new BlockingPlayerChoiceContext(), player.Creature, 3, player.Creature, null);
            await PowerCmd.Apply<DexterityPower>(new BlockingPlayerChoiceContext(), player.Creature, 4, player.Creature, null);
            await PowerCmd.Apply<ArtifactPower>(new BlockingPlayerChoiceContext(), player.Creature, 2, player.Creature, null);
            SetEnergy(player, 3); SetStars(player, 0);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            var enemy = combat.Enemies[0];
            var captured = CombatRootSnapshot.Capture(combat);
            var root = captured.ForkSimulator();
            CardModel[] cards = player.PlayerCombatState!.AllCards.ToArray();
            CardModel defend = cards.Single(card => card.Id.Entry == "DEFEND_SILENT");
            CardModel? escape = cards.SingleOrDefault(card => card.Id.Entry == "ESCAPE_PLAN");
            var display = SolverDisplayNames.Capture(combat);
            var damage = BattleDamageTracker.Observe(combat);
            var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
            CompactDiscardProjection adapter;
            ResumableDiscardProgram.Candidate initial;
            List<(bool Defend, ResumableDiscardProgram.Candidate State, SimulationSnapshot Evaluation)> samples = [];
            MoveStateSnapshot? beforeEnd = null;
            MoveStateSnapshot? expected = null;
            int[][]? expectedPiles = null;
            using (SimulationNotificationIsolation.Enter())
            {
                adapter = new(root, player, includeAttacks: true, includeHandEnd: true);
                var lane = adapter.Program;
                initial = lane.Freeze();
                var reader = adapter.CreateReadView();
                var uncached = adapter.CreateReadView(false);
                var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
                foreach (bool playDefend in new[] { false, true })
                {
                    var mark = lane.State.Mark();
                    var oracle = root.Fork();
                    Prefix(lane, oracle, playDefend);
                    if (!playDefend) beforeEnd = CaptureSimulated(oracle, (SimulatedCombatState)oracle.State.CombatState, player, enemy);
                    lane.EndHandEffects(staging);
                    if (lane.Creature(0).CurrentHp == 0)
                    {
                        var pending = adapter.Materialize(lane);
                        if (!pending.IsAboutToLose || pending.TerminalStamp != null || !lane.CreaturePresent(0))
                            throw new InvalidOperationException("Hand-end death skipped pending loss or removed the player roster.");
                    }
                    lane.CheckWinCondition();
                    if (!oracle.SimulateEndPlayerTurnAfterOrbPassives(1, together))
                        throw new InvalidOperationException("Hand-end oracle unexpectedly suspended.");
                    var projection = adapter.Materialize(lane);
                    adapter.AssertValues(lane, oracle); adapter.AssertValues(lane, projection);
                    var oracleState = CaptureSimulated(oracle, (SimulatedCombatState)oracle.State.CombatState, player, enemy);
                    AssertSnapshotEqual(oracleState, CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState, player, enemy),
                        "CompactHandEnd", $"Mode{mode}-Defend{playDefend}-Projection");
                    if (!CompactHistory(oracle, adapter).SequenceEqual(CompactHistory(projection, adapter))
                        || !CompactPowerValues(((SimulatedCombatState)oracle.State.CombatState).EffectivePowers())
                            .SequenceEqual(CompactPowerValues(((SimulatedCombatState)projection.State.CombatState).EffectivePowers())))
                        throw new InvalidOperationException("Hand-end source history or complete Power fields differ.");
                    AssertCompactRngSet(oracle.Rng, projection.Rng);
                    var evaluation = Release(evaluator.Evaluate(oracle));
                    AssertCompactEvaluation(evaluation, Release(evaluator.Evaluate(projection)));
                    reader.Read(lane); uncached.Read(lane);
                    AssertCompactEvaluation(evaluation, evaluator.Evaluate(reader));
                    AssertCompactEvaluation(evaluation, evaluator.Evaluate(uncached));
                    samples.Add((playDefend, lane.Freeze(), evaluation));
                    if (!playDefend)
                    {
                        expected = oracleState;
                        expectedPiles = Enumerable.Range(0, 5).Select(pile => lane.Cards((ResumableDiscardProgram.Pile)pile)).ToArray();
                    }
                    lane.State.Rollback(mark);
                    if (!lane.State.Freeze().ContentEquals(initial.Open().State.Freeze()))
                        throw new InvalidOperationException("Hand-end rollback retained counters, Power retirement or terminal state.");
                }
                foreach (var sample in samples.AsEnumerable().Reverse())
                {
                    sample.State.RestoreInto(lane); reader.Read(lane);
                    AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader));
                }
                initial.RestoreInto(lane); reader.Read(lane);
                AssertCompactEvaluation(Release(evaluator.Evaluate(root)), evaluator.Evaluate(reader));
                AssertSnapshotEqual(CaptureActual(combat, player, enemy),
                    CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy), "CompactHandEnd", $"Mode{mode}-RootUnchanged");
            }
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                using var isolation = SimulationNotificationIsolation.Enter();
                var lane = initial.Open(); var reader = adapter.CreateReadView();
                var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
                foreach (var sample in samples.AsEnumerable().Reverse())
                {
                    initial.RestoreInto(lane); Prefix(lane, null, sample.Defend);
                    lane.EndHandEffects(staging); lane.CheckWinCondition();
                    if (!lane.State.Freeze().ContentEquals(sample.State.Open().State.Freeze()))
                        throw new InvalidOperationException("Hand-end worker diverged from frozen values.");
                    reader.Read(lane); AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader));
                }
            })));
            if (escape != null)
            {
                if (!escape.TryManualPlay(null)) throw new InvalidOperationException("Native status draw rejected EscapePlan.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                AssertSnapshotEqual(beforeEnd!, CaptureActual(combat, player, enemy), "CompactHandEnd", $"Mode{mode}-StatusDraw");
            }
            await CombatManager.Instance.DoTurnEnd(CombatManager.Instance._turnState!, player, new BlockingPlayerChoiceContext());
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            var actual = CaptureActual(combat, player, enemy);
            CardPile[] piles = [player.PlayerCombatState.Hand, player.PlayerCombatState.DrawPile, player.PlayerCombatState.DiscardPile,
                player.PlayerCombatState.PlayPile, player.PlayerCombatState.ExhaustPile];
            var actualPiles = piles.Select(pile => pile.Cards.Select(adapter.IndexOf).ToArray()).ToArray();
            evidence.Add(new { mode, staging = staging.ToString(), nativeMode = nativeMode.ToString(), branches = samples.Count,
                expected, actual, expectedPiles, actualPiles });
            if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
            {
                Directory.CreateDirectory(_request.EvidenceDirectory);
                File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-hand-end.json"),
                    JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            }
            AssertSnapshotEqual(expected!, actual, "CompactHandEnd", $"Mode{mode}-Native");
            if (expectedPiles!.Where((pile, index) => !pile.SequenceEqual(actualPiles[index])).Any())
                throw new InvalidOperationException("Native hand-end ordered piles, including Play, differ.");
            using (SimulationNotificationIsolation.Enter())
            {
                var lane = initial.Open(); Prefix(lane, null, false);
                lane.EndHandEffects(staging); lane.CheckWinCondition();
                var reader = adapter.CreateReadView(); reader.Read(lane);
                var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
                AssertCompactEvaluation(samples[0].Evaluation, evaluator.Evaluate(reader));
                var projection = adapter.Materialize(lane);
                AssertSnapshotEqual(expected!, CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState, player, enemy),
                    "CompactHandEnd", $"Mode{mode}-AfterNative");
            }

            void Prefix(ResumableDiscardProgram lane, CombatPredictionSimulator? oracle, bool playDefend)
            {
                if (escape != null) Play(escape);
                if (playDefend) Play(defend);
                void Play(CardModel card)
                {
                    lane.Begin(adapter.IndexOf(card)); lane.Run();
                    if (!lane.Complete || oracle != null && !oracle.ManualPlay(oracle.State.FindCard(card)!, null, out _))
                        throw new InvalidOperationException("Hand-end prefix suspended.");
                }
            }
        }
        _completedChecks.Add($"CompactHandEnd:{nativeMode}:Native3Roots6Branches:BlockedPartialFatal:StatusDraw:ThreeAndFourBurns:EtherealFirst:FullStateAndPlayPile:AllHistory:AllRng:AllSnapshotProperties:Frozen8Workers:ReplayAfterNativeDeath");
    }
}
