using System.Reflection;
using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCompactDiscardDrawAsync(CombatState combat, Player player)
    {
        var tokens = typeof(CardChoiceSupport).GetMethod("ToTokens", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<IReadOnlyList<PredictedCard>, IReadOnlyList<PredictedCard>, IReadOnlyList<PredictedCard>,
                Func<CardModel, string>, IReadOnlyList<PlanCardToken>>>();
        List<object> evidence = [];
        for (int mode = 0; mode < 2; mode++)
        {
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
            ClearRunDeck((RunState)combat.RunState, player);
            await ClearPlayerPilesAsync(player);
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "CALCULATED_GAMBLE", UpgradeLevels = 1, Pile = "Hand" });
            if (mode == 1)
            {
                foreach (string relic in new[] { "TOUGH_BANDAGES", "THE_ABACUS" })
                    await InjectRelicAsync(player, new UnattendedRelicInjection { RelicId = relic });
                await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = "STRATAGEM_POWER", Amount = 2, Target = "Player" });
                foreach (string card in new[] { "PREPARED", "DEFEND_SILENT", "STRIKE_SILENT", "ESCAPE_PLAN" })
                    await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = card, Pile = "Hand", UpgradeLevels = card == "PREPARED" ? 1 : 0 });
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_SILENT", Pile = "Draw" });
                foreach (string card in new[] { "BACKFLIP", "DEFEND_SILENT", "STRIKE_SILENT", "DEFEND_SILENT", "STRIKE_SILENT" })
                    await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = card, Pile = "Discard" });
                FindActualHandCard(player, "PREPARED", 0).GiveSingleTurnSly();
            }
            SetEnergy(player, 3); SetStars(player, 0);
            await SetBlockAsync(player.Creature, 0);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            CardModel nativeCard = player.PlayerCombatState!.Hand.Cards.Single(card => card is CalculatedGamble);
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            var simulator = root.ForkSimulator();
            var display = SolverDisplayNames.Capture(combat);
            var damage = BattleDamageTracker.Observe(combat);
            var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
            CompactDiscardProjection adapter;
            ResumableDiscardProgram.Candidate pending;
            List<CompactCase> cases = [];
            using (SimulationNotificationIsolation.Enter())
            {
                adapter = new(simulator, player);
                var lane = adapter.Program;
                var rootValues = lane.Freeze();
                var reader = adapter.CreateReadView(); var uncached = adapter.CreateReadView(false);
                var evaluator = new CompactEvaluationDriver(root, display, damage, policy);
                var initial = lane.State.Mark();
                lane.Begin(adapter.IndexOf(nativeCard)); lane.Run();
                if (mode == 1 && (!lane.NeedsChoice || lane.ChoicePile != ResumableDiscardProgram.Pile.Draw
                    || lane.ShuffleCount != 1 || lane.Block != 12))
                    throw new InvalidOperationException("Gamble did not stop after its discard hooks and first draw.");
                pending = lane.Freeze();
                int count = mode == 0 ? 1 : 24;
                for (int sample = 0; sample < count; sample++)
                {
                    var mark = lane.State.Mark();
                    List<int[]> path = [];
                    while (!lane.Complete)
                    {
                        int[] options = lane.Cards(lane.ChoicePile);
                        int start = (sample * 7 + path.Count * 3) % options.Length;
                        int[] selected = Enumerable.Range(0, lane.ChoiceCount).Select(index => (start + index) % options.Length)
                            .Order().Select(index => options[index]).ToArray();
                        if (sample == 0)
                            selected = options.Where(id => lane.ChoicePile == ResumableDiscardProgram.Pile.Draw
                                ? adapter.Original(id) is Prepared or Backflip : adapter.Original(id) is not Backflip).Take(lane.ChoiceCount).ToArray();
                        path.Add(selected); lane.SupplyChoice(selected); lane.Run();
                    }
                    var oracle = simulator.Fork();
                    var shadow = (SimulatedCombatState)oracle.State.CombatState;
                    int choiceIndex = 0;
                    List<PlanCardChoice> plans = [];
                    shadow.BeginActionChoices(TurnStartChoiceCursor.ForAutomaticPolicy(request =>
                    {
                        var spec = request.Spec!;
                        PredictedCard[] selected = path[choiceIndex++].Select(id => spec.Options.Single(card => ReferenceEquals(card.Original, adapter.Original(id)))).ToArray();
                        PlanCardChoice plan = new(request.Effect, request.SourcePile,
                            tokens(selected, spec.Options, spec.SourceCards, static card => card.Id.Entry), request.SourceId, request.ContextId, request.Timing);
                        plans.Add(plan); return plan;
                    }));
                    try
                    {
                        if (!oracle.ManualPlay(oracle.State.FindCard(nativeCard)!, null, out _))
                            throw new InvalidOperationException("Gamble oracle left a pending choice.");
                    }
                    finally { shadow.EndActionChoices(); }
                    if (choiceIndex != path.Count) throw new InvalidOperationException("Gamble oracle choice count differs.");
                    var projection = adapter.Materialize(lane);
                    adapter.AssertValues(lane, oracle); adapter.AssertValues(lane, projection);
                    AssertSnapshotEqual(CaptureSimulated(oracle, shadow, player, combat.Enemies[0]),
                        CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState, player, combat.Enemies[0]), "CompactDiscardDraw", "Mode" + mode + "-" + sample);
                    if (!CompactHistory(oracle, adapter).SequenceEqual(CompactHistory(projection, adapter)))
                        throw new InvalidOperationException("Gamble history/source differs:\n" + string.Join('\n', CompactHistory(oracle, adapter))
                            + "\nProjected:\n" + string.Join('\n', CompactHistory(projection, adapter)));
                    AssertCompactRngSet(oracle.Rng, projection.Rng);
                    var score = Release(evaluator.Evaluate(oracle));
                    AssertCompactEvaluation(score, Release(evaluator.Evaluate(projection)));
                    reader.Read(lane); uncached.Read(lane);
                    AssertCompactEvaluation(score, evaluator.Evaluate(reader)); AssertCompactEvaluation(score, evaluator.Evaluate(uncached));
                    if (lane.Block != (mode == 0 ? 0 : 24) || lane.Count(ResumableDiscardProgram.Pile.Exhaust) != 1)
                        throw new InvalidOperationException("Discard/draw hooks, Sly ordering or upgraded exhaust differ.");
                    cases.Add(new(path.ToArray(), lane.Freeze(), plans.ToArray(), score));
                    evidence.Add(new { mode, sample, lane.Block, lane.ShuffleCount, score.StateKey, choiceCount = path.Count });
                    lane.State.Rollback(mark);
                }
                lane.State.Rollback(initial);
                if (!lane.State.Freeze().ContentEquals(rootValues.Open().State.Freeze()))
                    throw new InvalidOperationException("Gamble rollback retained its captured batch or shuffle state.");
                reader.Read(lane); AssertCompactEvaluation(Release(evaluator.Evaluate(simulator)), evaluator.Evaluate(reader));
            }
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                using var isolation = SimulationNotificationIsolation.Enter();
                var lane = pending.Open(); var reader = adapter.CreateReadView();
                var evaluator = new CompactEvaluationDriver(root, display, damage, policy);
                foreach (var sample in cases)
                {
                    pending.RestoreInto(lane);
                    foreach (int[] choice in sample.Choices) { lane.SupplyChoice(choice); lane.Run(); }
                    if (!lane.State.Freeze().ContentEquals(sample.Candidate.Open().State.Freeze()))
                        throw new InvalidOperationException("Gamble resumed differently in an independent worker.");
                    reader.Read(lane); AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader));
                }
            })));
            CompactCase native = cases[0];
            CombatPredictionSimulator nativeExpected;
            using (SimulationNotificationIsolation.Enter())
                nativeExpected = adapter.Materialize(native.Candidate.Open());
            var selector = new PlannedCardSelector(native.Plan);
            using (CardSelectCmd.PushSelector(selector))
            {
                if (!nativeCard.TryManualPlay(null)) throw new InvalidOperationException("Native Gamble rejected.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            }
            selector.AssertConsumed();
            AssertSnapshotEqual(CaptureSimulated(nativeExpected, (SimulatedCombatState)nativeExpected.State.CombatState, player, combat.Enemies[0]),
                CaptureActual(combat, player, combat.Enemies[0]), "CompactDiscardDraw", "NativeMode" + mode);
        }
        _completedChecks.Add("CompactDiscardDraw:EmptyHandAnd24Branches:DiscardHookThenDrawThenSly:PartialDrawShuffleChoice:RedrawnSly:UpgradedRetainAndExhaust:FullStateHistoryRng:AllSnapshotProperties:Frozen8Workers:Native2Roots");
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-discard-draw.json"),
                JsonSerializer.Serialize(new { cases = evidence, productionBackendEnabled = false }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
