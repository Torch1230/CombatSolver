using Model = CombatSolver.SearchOutcomeValueModel;

namespace OfflineSearchHarness;

internal static class OutcomeImitationChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Model.ImitationRow Row(double value, bool? selected, int[] groups) => new(
            new() { ["character/IRONCLAD"] = 1, ["x"] = value }, groups, selected, Model.FeatureSchema);
        var positive = Row(1, true, [0, 1]);
        var negative = Row(-1, false, [0, 1]);
        var other = Row(-2, false, [0]);
        var graph = Model.PrepareImitationTraining([[positive, negative, other]]);
        check(graph.Pairs.Count == 2 && graph.Pairs.All(p =>
                ((Model.ImitationRow)graph.Rows[p.Preferred]).OnWinningRoute == true
                && ((Model.ImitationRow)graph.Rows[p.Other]).OnWinningRoute == false),
            "imitation pairs select the demonstrated route without ranking unchosen alternatives");
        check(Math.Abs(graph.Pairs.Sum(p => p.Weight) - 1) < 1e-12,
            "duplicate pool membership cannot multiply an imitation root's weight");
        check(Model.PrepareImitationTraining([[positive], [negative, other]]).Pairs.Count == 0,
            "imitation group identifiers never join independent roots");
        foreach (var invalid in new[] { positive with { OnWinningRoute = null },
            positive with { FeatureSchema = 0 }, positive with { Groups = [-1] },
            positive with { Features = new() { ["x"] = double.MaxValue } } })
            reject(() => Model.PrepareImitationTraining([[invalid, negative]]),
                "missing or invalid imitation observations fail explicitly");
        var sampled = OutcomeValueTraining.SampleImitation(
            Enumerable.Range(0, 1000).Select(i => Row(i, false, [0])).Append(positive)
                .Append(Row(2000, false, [7])).ToArray(), 64);
        check(sampled.Length == 64 && sampled.Count(r => r.OnWinningRoute == true) == 1
                && sampled.All(r => !r.Groups.Contains(7)),
            "bounded sampling preserves the rare demonstrated state and its competing pool");
        var (document, _) = Model.FitLinearFoundation(graph);
        var model = Model.Load(document);
        check(model.PredictFeaturesForTesting(positive.Features) > model.PredictFeaturesForTesting(negative.Features),
            "imitation graph is consumable by the existing numerical model without fabricated outcomes");
        check(new Model().ExportImitation() is { UnavailableReason: "no-completed-winning-route", Rows.Length: 0 },
            "collection without a completed winner exports no imitation labels");
    }
}
