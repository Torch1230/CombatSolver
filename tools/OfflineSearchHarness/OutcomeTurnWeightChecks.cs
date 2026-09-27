using System.Text.Json;
using CombatSolver;
using Model = CombatSolver.SearchOutcomeValueModel;

namespace OfflineSearchHarness;

internal static class OutcomeTurnWeightChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Model.TrainingRow Row(int x, double turn, int[] groups) => new(
            new() { ["x"] = x, ["battle/turn"] = turn },
            new SolverInterimResult(true, 0, x, x, 0, 0, 0, 0, 12) { Survives = true },
            3, groups, FeatureSchema: Model.FeatureSchema);
        Model.TrainingRow[] root = [Row(0, 1, [0, 2]), Row(1, 1, [0]),
            Row(2, 8, [1, 2]), Row(3, 8, [1]), Row(4, 8, [1]), Row(5, 8, [1])];
        var ordinary = Model.PrepareTraining([root]);
        var balanced = Model.PrepareTraining([root], balanceTrainingTurns: true);
        check(ordinary.Pairs.Select(p => (p.Preferred, p.Other)).SequenceEqual(
                balanced.Pairs.Select(p => (p.Preferred, p.Other)))
            && ordinary.Rows.SequenceEqual(balanced.Rows) && ordinary.FeatureNames.SequenceEqual(balanced.FeatureNames)
            && ordinary.PairKinds.SequenceEqual(balanced.PairKinds),
            "turn stratification preserves sampled rows, edges, labels, support and ordering");
        check(ordinary.Pairs.Count == 8 && ordinary.Pairs.All(p => p.Weight == 1d / 8),
            "default equal-pair weighting remains exact");
        int Turn(Model.PreparedTraining graph, Model.Pair p) => Math.Max(
            Model.TrainingTurn(graph.Rows[p.Preferred]), Model.TrainingTurn(graph.Rows[p.Other]));
        check(balanced.Pairs.GroupBy(p => Turn(balanced, p)).All(g => Math.Abs(g.Sum(p => p.Weight) - .5) < 1e-12)
            && balanced.Pairs.Where(p => Turn(balanced, p) == 8).All(p => p.Weight == 1d / 14),
            "later endpoint assigns cross-turn edges and each observed turn receives half the root mass");
        var twoRoots = Model.PrepareTraining([root, [Row(10, 3, [0]), Row(11, 3, [0]), Row(12, 3, [0])]],
            balanceTrainingTurns: true);
        check(twoRoots.ParticipatingRoots == 2 && twoRoots.Pairs.GroupBy(p => twoRoots.Rows[p.Preferred].Features["x"] < 10)
            .All(g => Math.Abs(g.Sum(p => p.Weight) - 1) < 1e-12), "different root sizes retain equal total mass");
        var duplicate = Model.PrepareTraining([root.Select(r => r with
            { Groups = [.. r.Groups, .. r.Groups.Select(g => g + 10)] }).ToArray()], balanceTrainingTurns: true);
        check(duplicate.Pairs.SequenceEqual(balanced.Pairs), "repeated pools cannot multiply a turn's mass");
        Model.TrainingRow[] many = Enumerable.Range(0, 100).Select(i => Row(i, i < 10 ? 1 : 8, [0])).ToArray();
        var capped = Model.PrepareTraining([many], balanceTrainingTurns: true);
        check(capped.Pairs.Count == 4096 && capped.Pairs.Select(p => (p.Preferred, p.Other)).SequenceEqual(
            Model.PrepareTraining([many]).Pairs.Select(p => (p.Preferred, p.Other)))
            && Math.Abs(capped.Pairs.Sum(p => p.Weight) - 1) < 1e-12,
            "stratification does not change the deterministic pair cap or endpoints");
        foreach (double invalid in new[] { 0d, -1, .5, double.NaN, double.PositiveInfinity, (double)int.MaxValue + 1 })
            reject(() => Model.PrepareTraining([root, [Row(50, invalid, [9])]], balanceTrainingTurns: true),
                "unpaired observations cannot hide invalid turn metadata");
        reject(() => Model.PrepareTraining([root, [root[0] with { Features = new() { ["x"] = 0 } }]],
            balanceTrainingTurns: true), "turn stratification cannot invent missing turns");
        var fitted = new Model();
        check(fitted.Fit([root], balanceTrainingTurns: true)
            && JsonSerializer.Serialize(fitted.ExportLinearModel()) == JsonSerializer.Serialize(
                Model.Load(Model.FitLinearFoundation(balanced).Model).ExportModel()),
            "built-in fit and export share the weighted linear foundation");

        string directory = Path.Combine(Path.GetTempPath(), "outcome-turns-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rowsFile = Path.Combine(directory, "rows.json"), inputs = Path.Combine(directory, "inputs.json");
            void WriteInput(string weighting, string[]? excluded = null) => File.WriteAllText(inputs,
                JsonSerializer.Serialize(new { schemaVersion = 1, maximumRowsPerRoot = 64, pairWeighting = weighting,
                    excludedFeaturePrefixes = excluded ?? [], roots = new[] { rowsFile } }));
            File.WriteAllText(rowsFile, JsonSerializer.Serialize(root));
            WriteInput("unknown");
            reject(() => OutcomeValueTraining.ReadRoots(inputs), "unknown weighting must fail explicitly");
            WriteInput("turns");
            var hostGraph = Model.PrepareTraining(OutcomeValueTraining.ReadRoots(inputs).Select(r => r.Rows).ToArray(),
                balanceTrainingTurns: true);
            string export = Path.Combine(directory, "export");
            OutcomeValueTraining.Export(inputs, export);
            using (var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(export, "manifest.json"))))
                check(metadata.RootElement.GetProperty("pairWeighting").GetString() == "turns",
                    "binary graph declares its weighting policy");
            using (var reader = new BinaryReader(File.OpenRead(Path.Combine(export, "head-0.pairs"))))
            {
                var actual = new List<Model.Pair>();
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                    actual.Add(new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadDouble()));
                check(actual.SequenceEqual(hostGraph.Pairs), "binary edges preserve exact C# turn weights after host sampling");
            }
            string model = Path.Combine(directory, "model.json"), audit = Path.Combine(directory, "audit.json");
            File.WriteAllText(model, JsonSerializer.Serialize(fitted.ExportModel()));
            OutcomeValueTraining.Audit(inputs, model, audit);
            string weightedAudit = File.ReadAllText(audit);
            WriteInput("pairs");
            OutcomeValueTraining.Audit(inputs, model, audit);
            check(weightedAudit == File.ReadAllText(audit), "audit comparisons stay fixed regardless of training weights");
            using (var report = JsonDocument.Parse(weightedAudit))
            {
                var r = report.RootElement[0];
                var buckets = r.GetProperty("turns").EnumerateArray().ToArray();
                check(buckets.Sum(t => t.GetProperty("pairs").GetInt32()) == r.GetProperty("pairs").GetInt32()
                    && Math.Abs(buckets.Sum(t => t.GetProperty("logLoss").GetDouble() * t.GetProperty("pairs").GetInt32())
                        / r.GetProperty("pairs").GetInt32() - r.GetProperty("logLoss").GetDouble()) < 1e-12,
                    "turn diagnostics partition the original whole-root audit without changing its metric");
            }
            File.WriteAllText(rowsFile, JsonSerializer.Serialize(many));
            WriteInput("turns");
            var sampled = OutcomeValueTraining.ReadRoots(inputs)[0].Rows.Select(r => r.Features["x"]).ToHashSet();
            int omitted = Enumerable.Range(0, many.Length).First(i => !sampled.Contains(i));
            many[omitted] = Row(omitted, 0, [0]);
            File.WriteAllText(rowsFile, JsonSerializer.Serialize(many));
            reject(() => OutcomeValueTraining.ReadRoots(inputs), "raw invalid turns cannot disappear during row sampling");
            File.WriteAllText(rowsFile, JsonSerializer.Serialize(root));
            WriteInput("turns", ["battle/turn"]);
            reject(() => OutcomeValueTraining.Export(inputs, Path.Combine(directory, "excluded")),
                "turn-weighted fitting rejects ablation of its required observation");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
