using System.Text.Json;
using CombatSolver;
using Model = CombatSolver.SearchOutcomeValueModel;

namespace OfflineSearchHarness;

internal static class OutcomeContextTrainingChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        SolverInterimResult quality = new(true, 0, 2, 2, 0, 0, 0, 0, 4) { Survives = true };
        Model.TrainingRow Row(int turn, int hp, int group, bool rare = false) => new(
            rare ? new() { ["battle/turn"] = turn, ["x"] = hp, ["rare"] = hp }
                : new() { ["battle/turn"] = turn, ["x"] = hp },
            quality with { ProjectedBattleHpLost = hp, StrategicHpDeficit = hp }, 3, [group],
            FeatureSchema: Model.FeatureSchema);
        Model.ImitationRow Imitation(bool selected, int group = 0) => new(
            new() { ["battle/turn"] = 1, ["x"] = selected ? 2 : 5 }, [group], selected, Model.FeatureSchema);
        Model.JointObservation[] Root(bool rare = false) => Model.JoinTrainingRoot(
            [Row(1, 2, 0, rare), Row(1, 5, 0), Row(2, 3, 1), Row(2, 8, 1)],
            [Imitation(true), Imitation(false)], [Row(2, 4, 0), Row(2, 6, 0)]);
        bool Context(Model.PreparedTraining graph, Model.Pair pair)
            => !graph.Rows[pair.Preferred].Groups.Intersect(graph.Rows[pair.Other].Groups).Any();
        bool Correction(Model.PreparedTraining graph, Model.Pair pair)
            => ((Model.JointObservation)graph.Rows[pair.Preferred]).IsCorrection;
        var root = Root();
        var graph = Model.PrepareContextCalibratedTraining([root]);
        check(graph.Pairs.Count == 8 && graph.CrossTurnPairs == 4,
            "four missing cross-turn outcome edges join the three ordinary and one corrective edge");
        check(Math.Abs(graph.CrossTurnWeight - 1d / 3) < 1e-12
            && Math.Abs(graph.Pairs.Where(p => Correction(graph, p)).Sum(p => p.Weight) - 1d / 3) < 1e-12
            && Math.Abs(graph.Pairs.Sum(p => p.Weight) - 1) < 1e-12,
            "all three nonempty sources share one physical root weight");
        check(graph.ParticipatingRoots == 1 && graph.Rows.Count == root.Length
            && graph.Rows.All(r => root.Any(original => ReferenceEquals(r, original))),
            "calibration references original observations without copying rows or manufacturing roots");
        var cross = graph.Pairs.Where(p => Context(graph, p)).ToArray();
        check(cross.Length == graph.CrossTurnPairs && cross.All(p =>
                graph.Rows[p.Preferred] is Model.JointObservation { Source: Model.TrainingRow, IsCorrection: false }
                && graph.Rows[p.Other] is Model.JointObservation { Source: Model.TrainingRow, IsCorrection: false }
                && Model.TrainingTurn(graph.Rows[p.Preferred]) != Model.TrainingTurn(graph.Rows[p.Other])),
            "new edges never cross targets or correction collections and always cross turns");
        check(cross.All(p => Model.CompareWitnesses(
                (Model.TrainingRow)((Model.JointObservation)graph.Rows[p.Preferred]).Source,
                (Model.TrainingRow)((Model.JointObservation)graph.Rows[p.Other]).Source, out int kind) < 0 && kind < 2),
            "context directions follow witnessed complete policy rather than an invented turn value");
        check(graph.Pairs.Select(p => (p.Preferred, p.Other)).Distinct().Count() == graph.Pairs.Count,
            "existing and context sources cannot multiply an edge");
        var repeated = Model.PrepareContextCalibratedTraining([root]);
        check(graph.Pairs.SequenceEqual(repeated.Pairs) && graph.Rows.SequenceEqual(repeated.Rows),
            "fixed input order yields the same finite calibration graph");

        var pooled = Model.JoinTrainingRoot([Row(1, 2, 0), Row(2, 3, 0), Row(3, 4, 0)],
            [Imitation(true), Imitation(false)], [Row(2, 4, 0), Row(2, 6, 0)]);
        var baseline = Model.PrepareJointTraining([pooled], balanceCorrections: true);
        var unchanged = Model.PrepareContextCalibratedTraining([pooled]);
        check(unchanged.CrossTurnPairs == 0 && unchanged.Pairs.SequenceEqual(baseline.Pairs)
            && unchanged.Rows.SequenceEqual(baseline.Rows) && unchanged.FeatureNames.SequenceEqual(baseline.FeatureNames),
            "shared original pools are not additional calibration sources and preserve existing weights");
        var local = Model.JoinTrainingRoot([Row(1, 2, 0), Row(1, 3, 1), Row(1, 4, 2)], []);
        check(Model.PrepareContextCalibratedTraining([local]).Pairs.Count == 0,
            "same-turn comparisons outside observed pools are not silently added");
        var equal = Model.JoinTrainingRoot([Row(1, 2, 0), Row(2, 2, 1) with { RemainingActions = 1 },
            Row(3, 2, 2) with { RemainingActions = 0 }], []);
        check(Model.PrepareContextCalibratedTraining([equal]).Pairs.Count == 0,
            "shorter suffixes cannot label descendants as better policy states");
        var defeated = new[] { Row(1, 2, 0), Row(2, 3, 1), Row(3, 4, 2) }.Select(r => r with
            { Outcome = r.Outcome with { Won = false, Survives = false }, CompletedDefeat = true }).ToArray();
        check(Model.PrepareContextCalibratedTraining([Model.JoinTrainingRoot(defeated, [])]).Pairs.Count == 0,
            "all-defeat roots supply neither false preferences nor training mass");
        var mixed = Model.PrepareContextCalibratedTraining([Model.JoinTrainingRoot(
            [Row(1, 2, 0), defeated[1], Row(3, 4, 2)], [])]);
        check(mixed.CrossTurnPairs == 3 && mixed.PairKinds.SequenceEqual([2, 1, 0, 0]),
            "calibration retains both completed victory-versus-defeat and victory-policy labels");
        var imitationOnly = Model.JoinTrainingRoot([], [Imitation(true, 0), Imitation(false, 1)]);
        check(Model.PrepareContextCalibratedTraining([imitationOnly]).Pairs.Count == 0,
            "local imitation membership cannot become a cross-pool outcome preference");
        check(Model.PrepareContextCalibratedTraining([Model.JoinTrainingRoot([Row(1, 2, 0)], []),
                Model.JoinTrainingRoot([Row(2, 3, 0)], [])]).Pairs.Count == 0,
            "pool IDs and final-policy labels do not join different physical roots");
        var support = Model.PrepareContextCalibratedTraining([Root(true), Root(), Root()]);
        check(support.ParticipatingRoots == 3 && !support.FeatureNames.Contains("rare")
            && Math.Abs(support.Pairs.Sum(p => p.Weight) - 3) < 1e-11,
            "additional pair coverage cannot multiply independent feature support or root weight");
        reject(() => Model.PrepareContextCalibratedTraining([Model.JoinTrainingRoot(
            [Row(1, 2, 0), Row(2, 3, 1) with { Features = new() { ["x"] = 3 } }], [])]),
            "missing observed turns cannot become calibration labels");
        reject(() => Model.PrepareContextCalibratedTraining([Model.JoinTrainingRoot(
            [Row(1, 2, 0), Row(2, 3, 1) with { Features = new() { ["battle/turn"] = 1.5 } }], [])]),
            "fractional turn values are rejected before context sampling");

        var saturated = Model.JoinTrainingRoot(Enumerable.Range(0, 101).Select(i => Row(1, i, 0))
            .Concat(Enumerable.Range(0, 120).Select(i => Row(2 + i % 2, 200 + i, 1 + i))).ToArray(), [],
            [Row(2, 4, 0), Row(2, 6, 0)]);
        string original = JsonSerializer.Serialize(saturated);
        var bounded = Model.PrepareContextCalibratedTraining([saturated]);
        check(bounded.Pairs.Count == 4096 && bounded.CrossTurnPairs == 1024
            && bounded.Pairs.Count(p => Correction(bounded, p)) == 1,
            "bounded context edges and all corrective edges survive within the unchanged root pair budget");
        check(Math.Abs(bounded.CrossTurnWeight - 1d / 3) < 1e-11
            && Math.Abs(bounded.Pairs.Sum(p => p.Weight) - 1) < 1e-11,
            "reservoir saturation cannot change physical root or source mass");
        check(JsonSerializer.Serialize(saturated) == original,
            "context row and pair sampling cannot mutate observations or source pool arrays");
        var boundedAgain = Model.PrepareContextCalibratedTraining([saturated]);
        check(bounded.Pairs.SequenceEqual(boundedAgain.Pairs), "pair reservoir is reproducible after saturation");
        check(bounded.Pairs.Select(p => (p.Preferred, p.Other)).Distinct().Count() == 4096,
            "a saturated graph still has unique preferences");
    }
}
