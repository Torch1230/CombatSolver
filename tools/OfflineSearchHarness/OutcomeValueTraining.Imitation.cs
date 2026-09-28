using System.Diagnostics;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Players;
using Model = CombatSolver.SearchOutcomeValueModel;

namespace OfflineSearchHarness;

internal static partial class OutcomeValueTraining
{
    internal static int ExportImitation(string pathsFile, string directory)
    {
        var clock = Stopwatch.StartNew();
        var roots = ReadImitationRoots(pathsFile);
        return ExportRanking(roots, directory, clock, "character", "all", "pairs",
            "winning-route-imitation", Model.PrepareImitationTraining);
    }

    internal static List<(string Id, Model.ImitationRow[] Rows)> ReadImitationRoots(string pathsFile)
    {
        using var specification = JsonDocument.Parse(File.ReadAllText(pathsFile));
        var input = specification.RootElement;
        if (input.GetProperty("schemaVersion").GetInt32() != 1
            || input.GetProperty("trainingTarget").GetString() != "winning-route-imitation")
            throw new InvalidDataException("Expected explicit winning-route imitation inputs.");
        int maximum = input.GetProperty("maximumRowsPerRoot").GetInt32();
        if (maximum is < 64 or > 8192) throw new InvalidDataException("Invalid imitation row limit.");
        List<(string, Model.ImitationRow[])> roots = [];
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        HashSet<string> files = new(StringComparer.Ordinal);
        foreach (var entry in input.GetProperty("roots").EnumerateArray())
        {
            string file = Path.GetFullPath(entry.GetString() ?? throw new InvalidDataException("Missing imitation file."));
            if (!files.Add(file)) throw new InvalidDataException("Repeated imitation root file.");
            using var stream = File.OpenRead(file);
            var source = JsonSerializer.Deserialize<Model.ImitationDocument>(stream)
                ?? throw new InvalidDataException("Missing imitation document.");
            if (source.Schema != 1 || source.FeatureSchema != Model.FeatureSchema
                || source.GameMvid != typeof(Player).Assembly.ManifestModule.ModuleVersionId
                || source.Witness is not { Won: true, Survives: true, Score: 0 }
                || source.ActionCount < 0 || source.UnavailableReason != null || source.Rows == null)
                throw new InvalidDataException("No compatible completed winning-route teacher.");
            // Validate all observations before sampling. Missing route membership
            // must never default to a negative, and unavailable roots are explicit.
            Model.ValidateImitationRows(source.Rows);
            string character = OutcomeModelFile.CharacterOf(source.Rows);
            var sampled = SampleImitation(source.Rows, maximum);
            foreach (var row in sampled)
            {
                Dictionary<string, double> features = new(row.Features.Count, StringComparer.Ordinal);
                foreach (var (name, value) in row.Features)
                {
                    if (!names.TryGetValue(name, out string? canonical)) names.Add(name, canonical = name);
                    features.Add(canonical, value);
                }
                row.Features.Clear();
                foreach (var pair in features) row.Features.Add(pair.Key, pair.Value);
            }
            if (sampled.Length == 0 || OutcomeModelFile.CharacterOf(sampled) != character)
                throw new InvalidDataException("Imitation root has no sampled comparisons.");
            roots.Add((file, sampled));
        }
        return roots;
    }

    internal static Model.ImitationRow[] SampleImitation(Model.ImitationRow[] rows, int maximum)
    {
        // Positives are scarce route states, not independently labelled wins.
        // Retain them before drawing alternatives from their actual search pools.
        Random sampler = new(0);
        var positives = rows.Where(r => r.OnWinningRoute == true).ToArray();
        sampler.Shuffle(positives);
        positives = positives.Take(maximum / 2).ToArray();
        var groups = positives.SelectMany(r => r.Groups).ToHashSet();
        var negatives = rows.Where(r => r.OnWinningRoute == false && r.Groups.Any(groups.Contains)).ToArray();
        sampler.Shuffle(negatives);
        return positives.Concat(negatives.Take(maximum - positives.Length)).ToArray();
    }

    internal static int AuditImitation(string pathsFile, string modelPath, string output)
    {
        var clock = Stopwatch.StartNew();
        var model = OutcomeModelFile.Read(modelPath);
        List<object> results = [];
        int excluded = 0;
        List<double> accuracies = [], losses = [];
        foreach (var root in ReadImitationRoots(pathsFile))
        {
            string character = OutcomeModelFile.CharacterOf(root.Rows);
            var graph = Model.PrepareImitationTraining([root.Rows]);
            if (graph.Pairs.Count < 2)
            {
                excluded++;
                results.Add(new { root.Id, character, pairs = graph.Pairs.Count, unavailableReason = "insufficient-imitation-comparisons" });
                continue;
            }
            var head = model.Select(character);
            var scores = graph.Rows.Select(r => head.PredictFeaturesForTesting(r.Features)).ToArray();
            double accuracy = 0, loss = 0;
            foreach (var pair in graph.Pairs)
            {
                double margin = scores[pair.Preferred] - scores[pair.Other];
                accuracy += pair.Weight * (margin > 0 ? 1 : margin == 0 ? .5 : 0);
                loss += pair.Weight * (Math.Max(-margin, 0) + Math.Log(1 + Math.Exp(-Math.Abs(margin))));
            }
            accuracies.Add(accuracy); losses.Add(loss);
            results.Add(new { root.Id, character, pairs = graph.Pairs.Count, rows = graph.Rows.Count, accuracy, loss });
        }
        if (accuracies.Count == 0) throw new InvalidDataException("No auditable imitation roots.");
        var result = new { trainingTarget = "winning-route-imitation", roots = results, excludedRoots = excluded,
            participatingRoots = accuracies.Count, accuracy = accuracies.Average(), loss = losses.Average(),
            elapsedMilliseconds = clock.Elapsed.TotalMilliseconds };
        File.WriteAllText(output, JsonSerializer.Serialize(result));
        Console.WriteLine(JsonSerializer.Serialize(new { result.participatingRoots, result.excludedRoots, result.accuracy, result.loss }));
        return 0;
    }
}
