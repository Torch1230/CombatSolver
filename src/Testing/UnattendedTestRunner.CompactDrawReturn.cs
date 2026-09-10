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
    private async Task AssertCompactDrawReturnAsync(CombatState combat, Player player)
    {
        var tokens = typeof(CardChoiceSupport).GetMethod("ToTokens", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<IReadOnlyList<PredictedCard>, IReadOnlyList<PredictedCard>, IReadOnlyList<PredictedCard>,
                Func<CardModel, string>, IReadOnlyList<PlanCardToken>>>();
        List<object> evidence = [];
        for (int mode = 0; mode < 4; mode++)
        {
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
            ClearRunDeck((RunState)combat.RunState, player);
            await ClearPlayerPilesAsync(player);
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "ESCAPE_PLAN", UpgradeLevels = 1, Pile = "Hand" });
            if (mode == 3)
                for (int index = 0; index < 9; index++)
                    await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_SILENT", Pile = "Hand" });
            if (mode != 0)
            {
                await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = "STRATAGEM_POWER", Amount = 1, Target = "Player" });
                foreach (string card in new[] { "DEFEND_SILENT", "STRIKE_SILENT" })
                    await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = card, Pile = "Discard" });
            }
            SetEnergy(player, 3); SetStars(player, 0);
            await SetBlockAsync(player.Creature, 2);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            CardModel nativeCard = player.PlayerCombatState!.Hand.Cards.Single(card => card is EscapePlan);
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            var simulator = root.ForkSimulator();
            CombatPredictionSimulator expected;
            List<PlanCardChoice> plans = [];
            using (SimulationNotificationIsolation.Enter())
            {
                var adapter = new CompactDiscardProjection(simulator, player);
                var lane = adapter.Program;
                var original = lane.Freeze();
                var mark = lane.State.Mark();
                lane.Begin(adapter.IndexOf(nativeCard)); lane.Run();
                int selected = mode == 0 ? -1 : lane.Cards(lane.ChoicePile)
                    .Single(id => adapter.Original(id).Id.Entry == (mode == 1 ? "STRIKE_SILENT" : "DEFEND_SILENT"));
                if (mode != 0)
                {
                    if (!lane.NeedsChoice || lane.ChoicePile != ResumableDiscardProgram.Pile.Draw)
                        throw new InvalidOperationException("Draw-return fixture missed its shuffle choice.");
                    var suspended = lane.Freeze();
                    lane.SupplyChoice([selected]); lane.Run();
                    var finished = lane.State.Freeze();
                    var resumed = suspended.Open();
                    resumed.SupplyChoice([selected]); resumed.Run();
                    if (!resumed.State.Freeze().ContentEquals(finished))
                        throw new InvalidOperationException("Draw return did not survive a frozen shuffle choice.");
                }
                int expectedDraws = mode is 1 or 2 ? 1 : 0;
                if (!lane.Complete || lane.Block != (mode == 1 ? 7 : 2)
                    || Enumerable.Range(0, lane.EventCount).Select(lane.EventAt).Count(e => e.Kind == ResumableDiscardProgram.EventKind.Draw) != expectedDraws)
                    throw new InvalidOperationException("Empty/full-hand draw or returned card category differs.");
                expected = simulator.Fork();
                var shadow = (SimulatedCombatState)expected.State.CombatState;
                shadow.BeginActionChoices(TurnStartChoiceCursor.ForAutomaticPolicy(request =>
                {
                    var spec = request.Spec!;
                    PredictedCard card = spec.Options.Single(card => ReferenceEquals(card.Original, adapter.Original(selected)));
                    PlanCardChoice plan = new(request.Effect, request.SourcePile,
                        tokens([card], spec.Options, spec.SourceCards, static card => card.Id.Entry), request.SourceId, request.ContextId, request.Timing);
                    plans.Add(plan); return plan;
                }));
                try
                {
                    if (!expected.ManualPlay(expected.State.FindCard(nativeCard)!, null, out _))
                        throw new InvalidOperationException("Draw-return oracle left a choice.");
                }
                finally { shadow.EndActionChoices(); }
                if (plans.Count != (mode == 0 ? 0 : 1)) throw new InvalidOperationException("Draw-return oracle choice count differs.");
                var projection = adapter.Materialize(lane);
                adapter.AssertValues(lane, expected); adapter.AssertValues(lane, projection);
                AssertSnapshotEqual(CaptureSimulated(expected, shadow, player, combat.Enemies[0]),
                    CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState, player, combat.Enemies[0]), "CompactDrawReturn", "Mode" + mode);
                if (!CompactHistory(expected, adapter).SequenceEqual(CompactHistory(projection, adapter)))
                    throw new InvalidOperationException("Draw-return history/source differs in mode " + mode);
                AssertCompactRngSet(expected.Rng, projection.Rng);
                var evaluator = new CompactEvaluationDriver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
                    SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
                var score = Release(evaluator.Evaluate(expected));
                foreach (bool cached in new[] { false, true })
                {
                    var reader = adapter.CreateReadView(cached); reader.Read(lane);
                    AssertCompactEvaluation(score, evaluator.Evaluate(reader));
                }
                evidence.Add(new { mode, actualDraws = expectedDraws, lane.Block, lane.ShuffleCount, score.StateKey });
                lane.State.Rollback(mark);
                if (!lane.State.Freeze().ContentEquals(original.Open().State.Freeze()))
                    throw new InvalidOperationException("Draw-return rollback retained a predicate or RNG value.");
            }
            var selector = new PlannedCardSelector(plans);
            using (CardSelectCmd.PushSelector(selector))
            {
                if (!nativeCard.TryManualPlay(null)) throw new InvalidOperationException("Native draw-return card rejected.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            }
            selector.AssertConsumed();
            AssertSnapshotEqual(CaptureSimulated(expected, (SimulatedCombatState)expected.State.CombatState, player, combat.Enemies[0]),
                CaptureActual(combat, player, combat.Enemies[0]), "CompactDrawReturn", "NativeMode" + mode);
        }
        _completedChecks.Add("CompactDrawReturn:Native4Roots:EmptyDraw:ShuffleRetrievedVersusDrawn:FullHandAfterRetrieve:FrozenChoice:Rollback:OriginalKeys:AllSnapshotProperties:History:AllRng");
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-draw-return.json"),
                JsonSerializer.Serialize(new { cases = evidence, productionBackendEnabled = false }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
