using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCompactDeferredPowerRootAsync(CombatRootSnapshot captured, CombatPredictionSimulator root,
        CombatState combat, Player player, CardModel[] cards, int mode)
    {
        var display = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        var enemies = combat.Enemies.ToArray();
        CompactDiscardProjection adapter;
        ResumableDiscardProgram.Candidate initial;
        List<(int[] Path, ResumableDiscardProgram.Candidate State, SimulationSnapshot Evaluation)> samples = [];
        using (SimulationNotificationIsolation.Enter())
        {
            adapter = new(root, player, includeAttacks: true);
            var lane = adapter.Program;
            initial = lane.Freeze();
            var reader = adapter.CreateReadView();
            var uncached = adapter.CreateReadView(false);
            var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
            var paths = Enumerable.Range(0, cards.Length).Select(index => new[] { index })
                .Concat([Enumerable.Range(0, cards.Length).ToArray(), Enumerable.Range(0, cards.Length).Reverse().ToArray()]);
            foreach (int[] path in paths)
            {
                var mark = lane.State.Mark();
                var oracle = root.Fork();
                foreach (int index in path)
                {
                    Play(lane, index);
                    if (!oracle.ManualPlay(oracle.State.FindCard(cards[index])!, null, out _))
                        throw new InvalidOperationException("Deferred Power oracle suspended.");
                    var projection = adapter.Materialize(lane);
                    adapter.AssertValues(lane, oracle); adapter.AssertValues(lane, projection);
                    foreach (var enemy in enemies)
                        AssertSnapshotEqual(CaptureSimulated(oracle, (SimulatedCombatState)oracle.State.CombatState, player, enemy),
                            CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState, player, enemy),
                            "CompactDeferredPowers", $"Mode{mode}-Card{index}");
                    if (!CompactPowerValues(((SimulatedCombatState)oracle.State.CombatState).EffectivePowers())
                        .SequenceEqual(CompactPowerValues(((SimulatedCombatState)projection.State.CombatState).EffectivePowers())))
                        throw new InvalidOperationException("Deferred Power metadata/order differs.");
                    if (!CompactHistory(oracle, adapter).SequenceEqual(CompactHistory(projection, adapter)))
                        throw new InvalidOperationException("Deferred Power history/source differs.");
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
                    throw new InvalidOperationException("Deferred Power rollback retained a counter or removed card.");
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
                foreach (int index in sample.Path) Play(lane, index);
                if (!lane.State.Freeze().ContentEquals(sample.State.Open().State.Freeze()))
                    throw new InvalidOperationException("Deferred Power worker retained sibling values.");
                reader.Read(lane); AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader));
            }
        })));
        _completedChecks.Add($"CompactDeferredPowerRoot:Mode{mode}:{samples.Count}Branches:Frozen8Workers:AllSnapshotProperties");

        void Play(ResumableDiscardProgram lane, int index)
        {
            lane.Begin(adapter.IndexOf(cards[index])); lane.Run();
            if (!lane.Complete) throw new InvalidOperationException("Deferred Power compact command suspended.");
        }
    }
}
