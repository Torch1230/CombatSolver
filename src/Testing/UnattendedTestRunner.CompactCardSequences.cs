using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private readonly record struct CompactCardAction(CardModel? RootCard, int GeneratedOrdinal = -1, int Target = -1);

    private async Task AssertCompactCardSequencesAsync(CombatRootSnapshot captured, CombatPredictionSimulator root,
        CombatState combat, Player player, CardModel[] cards, string fixture, IReadOnlyList<CompactCardAction[]>? requestedPaths = null)
    {
        var display = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        var enemies = combat.Enemies.ToArray();
        CompactDiscardProjection adapter;
        ResumableDiscardProgram.Candidate initial;
        List<(CompactCardAction[] Path, ResumableDiscardProgram.Candidate State, SimulationSnapshot Evaluation)> samples = [];
        using (SimulationNotificationIsolation.Enter())
        {
            adapter = new(root, player, includeAttacks: true);
            var lane = adapter.Program;
            initial = lane.Freeze();
            var reader = adapter.CreateReadView();
            var uncached = adapter.CreateReadView(false);
            var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
            IEnumerable<CompactCardAction[]> paths = requestedPaths ?? cards.Select(card => new[] { new CompactCardAction(card) })
                .Concat([cards.Select(card => new CompactCardAction(card)).ToArray(), cards.Reverse().Select(card => new CompactCardAction(card)).ToArray()]).ToArray();
            foreach (var path in paths)
            {
                var mark = lane.State.Mark();
                var oracle = root.Fork();
                foreach (var action in path)
                {
                    Play(lane, action);
                    int identity = Resolve(action);
                    CardModel original = action.RootCard ?? adapter.CaptureCardIdentities(oracle).Single(pair => pair.Value == identity).Key;
                    if (!oracle.ManualPlay(oracle.State.FindCard(original)!, action.Target < 0 ? null : adapter.Creature(action.Target), out _))
                        throw new InvalidOperationException($"{fixture} oracle suspended.");
                    var projection = adapter.Materialize(lane);
                    adapter.AssertValues(lane, oracle); adapter.AssertValues(lane, projection);
                    foreach (var enemy in enemies)
                        AssertSnapshotEqual(CaptureSimulated(oracle, (SimulatedCombatState)oracle.State.CombatState, player, enemy),
                            CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState, player, enemy),
                            fixture, $"Card{identity}");
                    if (!CompactPowerValues(((SimulatedCombatState)oracle.State.CombatState).EffectivePowers())
                        .SequenceEqual(CompactPowerValues(((SimulatedCombatState)projection.State.CombatState).EffectivePowers())))
                        throw new InvalidOperationException($"{fixture} metadata/order differs.");
                    string[] expectedHistory = CompactHistory(oracle, adapter).ToArray(), actualHistory = CompactHistory(projection, adapter).ToArray();
                    if (!expectedHistory.SequenceEqual(actualHistory))
                        throw new InvalidOperationException($"{fixture} history/source differs at card {identity}:\nExpected:\n"
                            + string.Join('\n', expectedHistory) + "\nProjected:\n" + string.Join('\n', actualHistory));
                    AssertCompactRngSet(oracle.Rng, projection.Rng);
                    var expected = Release(evaluator.Evaluate(oracle));
                    AssertCompactEvaluation(expected, Release(evaluator.Evaluate(projection)));
                    reader.Read(lane); uncached.Read(lane);
                    AssertCompactEvaluation(expected, evaluator.Evaluate(reader));
                    AssertCompactEvaluation(expected, evaluator.Evaluate(uncached));
                }
                reader.Read(lane);
                samples.Add((path, lane.Freeze(), evaluator.Evaluate(reader)));
                lane.State.Rollback(mark);
                if (!lane.State.Freeze().ContentEquals(initial.Open().State.Freeze()))
                    throw new InvalidOperationException($"{fixture} rollback retained a counter or removed card.");
            }
            foreach (var sample in samples.AsEnumerable().Reverse())
            {
                sample.State.RestoreInto(lane); reader.Read(lane);
                AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader));
            }
            initial.RestoreInto(lane); reader.Read(lane);
            AssertCompactEvaluation(Release(evaluator.Evaluate(root)), evaluator.Evaluate(reader));
        }
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using var isolation = SimulationNotificationIsolation.Enter();
            var lane = initial.Open();
            var reader = adapter.CreateReadView();
            var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
            foreach (var sample in samples.AsEnumerable().Reverse())
            {
                initial.RestoreInto(lane);
                foreach (var action in sample.Path) Play(lane, action);
                if (!lane.State.Freeze().ContentEquals(sample.State.Open().State.Freeze()))
                    throw new InvalidOperationException($"{fixture} worker retained sibling values.");
                reader.Read(lane); AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader));
            }
        })));
        _completedChecks.Add($"{fixture}:{samples.Count}Branches:Frozen8Workers:AllSnapshotProperties");

        int Resolve(CompactCardAction action) => action.RootCard != null ? adapter.IndexOf(action.RootCard)
            : action.GeneratedOrdinal >= 0 ? adapter.CardCount + action.GeneratedOrdinal
            : throw new InvalidOperationException("Compact action lacks a card identity.");
        void Play(ResumableDiscardProgram lane, CompactCardAction action)
        {
            lane.Begin(Resolve(action), action.Target); lane.Run();
            if (!lane.Complete) throw new InvalidOperationException($"{fixture} compact command suspended.");
        }
    }
}
