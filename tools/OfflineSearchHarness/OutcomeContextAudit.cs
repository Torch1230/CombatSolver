using System.Diagnostics;
using System.Text.Json;
using CombatSolver;
using Model = CombatSolver.SearchOutcomeValueModel;

namespace OfflineSearchHarness;

// Diagnostic only. These are already observed completions within one physical
// training root, not new independent examples or rollout/value guarantees.
internal static class OutcomeContextAudit
{
    private const int MaximumRows = 256;
    internal readonly record struct Comparison(int Preferred, int Other, int Kind,
        bool CrossTurn, bool SharedPool);
    internal sealed record CandidatePairs(Comparison[] Pairs, int DefeatTies, int PolicyTies);

    internal static CandidatePairs Compare(Model.TrainingRow[] rows)
    {
        if (rows.Length > MaximumRows) throw new InvalidDataException("Context audit row bound exceeded.");
        Model.ValidateTrainingRows(rows);
        int[] turns = rows.Select(Model.TrainingTurn).ToArray();
        HashSet<(int, int)> shared = [];
        foreach (var group in Enumerable.Range(0, rows.Length)
            .SelectMany(i => rows[i].Groups.Distinct().Select(g => (Group: g, Row: i))).GroupBy(p => p.Group))
        {
            int[] members = group.Select(p => p.Row).ToArray();
            for (int a = 0; a < members.Length; a++)
                for (int b = a + 1; b < members.Length; b++)
                    shared.Add((members[a], members[b]));
        }
        List<Comparison> comparisons = [];
        int defeatTies = 0, policyTies = 0;
        for (int a = 0; a < rows.Length; a++)
            for (int b = a + 1; b < rows.Length; b++)
            {
                if (!rows[a].Outcome.Won && !rows[b].Outcome.Won) { defeatTies++; continue; }
                int order = Model.CompareWitnesses(rows[a], rows[b], out int kind);
                // Remaining action count rewards descendants on an identical
                // outcome path. It cannot establish cross-context policy value.
                if (order == 0 || kind == 2) { policyTies++; continue; }
                comparisons.Add(new(order < 0 ? a : b, order < 0 ? b : a,
                    kind, turns[a] != turns[b], shared.Contains((a, b))));
            }
        return new(comparisons.ToArray(), defeatTies, policyTies);
    }

    internal static int[] Sample(int length, Random sampler)
    {
        int[] indices = Enumerable.Range(0, length).ToArray();
        sampler.Shuffle(indices); // Never inspect labels, scores, turns or pool membership.
        return indices.Take(MaximumRows).Order().ToArray();
    }

    private sealed class Counts
    {
        public int Pairs { get; set; }
        public int Correct { get; set; }
        public int Wrong { get; set; }
        public int Ties { get; set; }
        public double LossSum { get; set; }
        public double? Accuracy => Pairs == 0 ? null : (double)Correct / Pairs;
        public double? MeanLogLoss => Pairs == 0 ? null : LossSum / Pairs;
        public void Add(double margin)
        {
            if (!double.IsFinite(margin)) throw new InvalidDataException("Non-finite prediction margin.");
            Pairs++;
            if (margin > 0) Correct++; else if (margin < 0) Wrong++; else Ties++;
            LossSum += Math.Log(1 + Math.Exp(-Math.Clamp(margin, -40, 40)));
        }
    }

    internal static int Run(string pathsFile, string modelFile, string output)
    {
        Stopwatch clock = Stopwatch.StartNew();
        var file = OutcomeModelFile.Read(modelFile);
        var roots = OutcomeValueTraining.ReadRoots(pathsFile, requireTrainingTurns: true);
        Random sampler = new(0);
        Dictionary<string, Counts> totals = [];
        Dictionary<string, Dictionary<string, Counts>> roles = [];
        List<object> results = [];
        foreach (var root in roots)
        {
            int[] indices = Sample(root.Rows.Length, sampler);
            var rows = indices.Select(i => root.Rows[i]).ToArray();
            string role = rows.Length == 0 ? "empty" : OutcomeModelFile.CharacterOf(root.Rows);
            var pairs = Compare(rows);
            Dictionary<string, Counts> local = [];
            if (!roles.TryGetValue(role, out var roleCounts)) roles.Add(role, roleCounts = []);
            if (rows.Length > 0)
            {
                var model = file.Select(file.IsConditional ? role : null);
                double[] scores = rows.Select(r => model.PredictFeaturesForTesting(r.Features)).ToArray();
                foreach (var pair in pairs.Pairs)
                {
                    string key = $"{(pair.CrossTurn ? "cross-turn" : "same-turn")}/"
                        + $"{(pair.SharedPool ? "shared-pool" : "disjoint-pools")}/"
                        + (pair.Kind == 0 ? "victory-over-defeat" : "victory-policy");
                    double margin = scores[pair.Preferred] - scores[pair.Other];
                    foreach (var counts in new[] { local, roleCounts, totals })
                    {
                        if (!counts.TryGetValue(key, out var count)) counts.Add(key, count = new());
                        count.Add(margin);
                    }
                }
            }
            results.Add(new { rootIndex = results.Count, root = root.Id, character = role,
                availableRows = root.Rows.Length, sampledRows = rows.Length, sampledIndices = indices,
                pairs.DefeatTies, pairs.PolicyTies, counts = local });
        }
        var result = new { schemaVersion = 1, diagnosticOnly = true, maximumRowsPerRoot = MaximumRows,
            samplerSeed = 0, roots = results.Count, predictionModel = Path.GetFullPath(modelFile),
            scope = "Observed within-root completed outcomes; no imitation or suffix-effort comparisons; not independent evaluation.",
            sharedPoolMeaning = "Endpoints occur in an original pool; not proof this edge survived training pair sampling.",
            totals, byCharacter = roles, rootDetails = results,
            elapsedSeconds = clock.Elapsed.TotalSeconds,
            peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 };
        File.WriteAllText(output, JsonSerializer.Serialize(result));
        Console.WriteLine(JsonSerializer.Serialize(new { result.roots, result.elapsedSeconds,
            result.peakWorkingSetBytes, totals }));
        return 0;
    }

    internal static int Check()
    {
        int count = 0;
        void Verify(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Context audit: " + name);
            count++;
        }
        void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            Verify(rejected, name);
        }
        SolverInterimResult quality = new(true, 0, 2, 2, 0, 0, 0, 0, 4) { Survives = true };
        Model.TrainingRow Row(int turn, int hp, int group) => new(new() { ["battle/turn"] = turn },
            quality with { ProjectedBattleHpLost = hp, StrategicHpDeficit = hp }, 3, [group],
            FeatureSchema: Model.FeatureSchema);
        var a = Row(1, 2, 0); var b = Row(2, 3, 1);
        var comparison = Compare([a, b]);
        Verify(comparison.Pairs is [{ Preferred: 0, Other: 1, Kind: 1, CrossTurn: true, SharedPool: false }],
            "different pools and turns reuse complete policy labels in the observed direction");
        Verify(Compare([b, a]).Pairs is [{ Preferred: 1, Other: 0 }], "row order cannot change preference");
        Verify(Compare([a, b with { Groups = [0, 0] }]).Pairs is [{ SharedPool: true }],
            "duplicate pool membership does not duplicate a comparison");
        Verify(Compare([a, Row(1, 3, 1)]).Pairs is [{ CrossTurn: false }], "same turn remains a separate stratum");
        var suffix = b with { Outcome = a.Outcome, RemainingActions = 1 };
        Verify(Compare([a, suffix]) is { Pairs.Length: 0, PolicyTies: 1 },
            "shorter witnessed suffix cannot invent higher policy value across turns");
        Verify(Compare([a, a]) is { Pairs.Length: 0, PolicyTies: 1 }, "identical policy remains tied");
        var defeat = b with { Outcome = b.Outcome with { Won = false, Survives = false }, CompletedDefeat = true };
        Verify(Compare([defeat, a]).Pairs is [{ Preferred: 1, Other: 0, Kind: 0 }], "victory precedes observed defeat");
        Verify(Compare([defeat, defeat with { RemainingActions = 99 }]) is { Pairs.Length: 0, DefeatTies: 1 },
            "two defeats create no preference");
        Verify(Compare([]).Pairs.Length == 0 && Compare([a]).Pairs.Length == 0, "no edges cross separate single-row roots");
        Reject(() => Compare([a, b with { CompletedDefeat = true }]), "inconsistent witness is rejected");
        Reject(() => Compare([a, b with { Features = new() { ["battle/turn"] = 1.5 } }]), "fractional turns are rejected");
        Reject(() => Compare(Enumerable.Repeat(a, 257).ToArray()), "comparison count remains bounded");
        int[] sample = Sample(500, new(0));
        Verify(sample.Length == 256 && sample.Distinct().Count() == 256 && sample.All(i => i is >= 0 and < 500),
            "sampler is bounded and cannot duplicate rows");
        Verify(sample.SequenceEqual(Sample(500, new(0))), "fixed source order and seed are reproducible");
        Verify(Sample(3, new(0)).SequenceEqual([0, 1, 2]), "small root retains all original row indices");
        string directory = Path.Combine(Path.GetTempPath(), "outcome-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "rows.json"), inputs = Path.Combine(directory, "inputs.json");
            var raw = Enumerable.Range(1, 65).Select(i => Row(i, i, i)).ToArray();
            File.WriteAllText(source, JsonSerializer.Serialize(raw));
            File.WriteAllText(inputs, JsonSerializer.Serialize(new
                { schemaVersion = 1, maximumRowsPerRoot = 64, roots = new[] { source } }));
            var kept = OutcomeValueTraining.ReadRoots(inputs)[0].Rows;
            int omitted = Enumerable.Range(1, 65).Except(kept.Select(Model.TrainingTurn)).Single();
            raw[omitted - 1] = raw[omitted - 1] with { Features = new() { ["battle/turn"] = 1.5 } };
            File.WriteAllText(source, JsonSerializer.Serialize(raw));
            Verify(OutcomeValueTraining.ReadRoots(inputs)[0].Rows.Select(Model.TrainingTurn)
                .SequenceEqual(kept.Select(Model.TrainingTurn)), "default reader preserves its exact sampled rows");
            Reject(() => OutcomeValueTraining.ReadRoots(inputs, requireTrainingTurns: true),
                "turn validation precedes sampling, including an omitted corrupt row");
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine($"Passed {count} outcome context audit contracts.");
        return 0;
    }
}
