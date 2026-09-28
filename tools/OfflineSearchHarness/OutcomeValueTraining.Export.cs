using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CombatSolver;

namespace OfflineSearchHarness;

internal static partial class OutcomeValueTraining
{
    // The external fitter receives resolved float observations and exact C#
    // preference edges. It must never reconstruct gameplay labels in Python.
    internal static int Export(string pathsFile, string directory)
    {
        Stopwatch clock = Stopwatch.StartNew();
        using var specification = JsonDocument.Parse(File.ReadAllText(pathsFile));
        var (partition, pairSelection, pairWeighting) = ReadPolicy(specification.RootElement);
        var roots = ReadRoots(pathsFile);
        return ExportRanking(roots, directory, clock, partition, pairSelection, pairWeighting,
            "completed-outcome", r => SearchOutcomeValueModel.PrepareTraining(r,
                highestPolicyTierOnly: pairSelection == "highest-policy-tier", balanceTrainingTurns: pairWeighting == "turns"));
    }

    private static int ExportRanking<T>(IReadOnlyList<(string Id, T[] Rows)> roots,
        string directory, Stopwatch clock, string partition, string pairSelection, string pairWeighting,
        string trainingTarget, Func<T[][], SearchOutcomeValueModel.PreparedTraining> prepare)
        where T : class, SearchOutcomeValueModel.RankingObservation
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new InvalidDataException("Ranking export directory must be empty.");
        if (roots.Count == 0) throw new InvalidDataException("Missing ranking roots.");
        Directory.CreateDirectory(directory);
        List<object> heads = [];
        var groups = roots.Select((root, index) => new { root.Rows, Index = index,
                Character = partition == "character" ? OutcomeModelFile.CharacterOf(root.Rows) : "" })
            .GroupBy(r => r.Character, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var prepared = prepare(group.Select(r => r.Rows).ToArray());
            var (foundation, scores) = SearchOutcomeValueModel.FitLinearFoundation(prepared);
            string stem = "head-" + heads.Count;
            string matrixName = stem + ".f32", pairsName = stem + ".pairs", marginName = stem + ".f64";
            using (var writer = new BinaryWriter(File.Create(Path.Combine(directory, matrixName))))
                foreach (var row in prepared.Rows)
                    foreach (string name in prepared.FeatureNames)
                    {
                        float value = (float)row.Features.GetValueOrDefault(name);
                        if (!float.IsFinite(value)) throw new InvalidDataException("Non-finite float observation.");
                        writer.Write(value);
                    }
            using (var writer = new BinaryWriter(File.Create(Path.Combine(directory, pairsName))))
                foreach (var pair in prepared.Pairs)
                {
                    writer.Write(pair.Preferred); writer.Write(pair.Other); writer.Write(pair.Weight);
                }
            using (var writer = new BinaryWriter(File.Create(Path.Combine(directory, marginName))))
                foreach (double score in scores)
                {
                    if (!double.IsFinite(score)) throw new InvalidDataException("Non-finite linear score.");
                    writer.Write(score);
                }
            heads.Add(new { character = group.Key, rootIndices = group.Select(r => r.Index).ToArray(),
                roots = group.Count(), sampledRows = group.Sum(r => r.Rows.Length),
                participatingRoots = prepared.ParticipatingRoots, rows = prepared.Rows.Count,
                pairs = prepared.Pairs.Count, pairKinds = prepared.PairKinds,
                crossTurnPairs = prepared.CrossTurnPairs, crossTurnWeight = prepared.CrossTurnWeight,
                correctionPairs = prepared.Pairs.Count(p => prepared.Rows[p.Preferred]
                    is SearchOutcomeValueModel.JointObservation { IsCorrection: true }),
                correctionWeight = prepared.Pairs.Where(p => prepared.Rows[p.Preferred]
                    is SearchOutcomeValueModel.JointObservation { IsCorrection: true }).Sum(p => p.Weight),
                foundation, matrix = matrixName, edges = pairsName, margins = marginName,
                sha256 = new[] { matrixName, pairsName, marginName }.ToDictionary(n => n, n =>
                    Hash(Path.Combine(directory, n))) });
        }
        var result = new { exportSchema = 1, trainingTarget, partition, pairSelection, pairWeighting, heads,
            roots = roots.Count, sampledRows = roots.Sum(r => r.Rows.Length),
            matrixFormat = "row-major-little-endian-float32-explicit-zero",
            edgeFormat = "little-endian-int32-int32-float64",
            marginFormat = "little-endian-float64",
            elapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
            peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 };
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(result));
        Console.WriteLine(JsonSerializer.Serialize(new { result.roots, result.sampledRows,
            heads = heads.Count, result.elapsedMilliseconds, result.peakWorkingSetBytes }));
        return 0;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal sealed record PredictionInput(string? Character, Dictionary<string, double> Features);

    internal static int Predict(string modelPath, string inputsPath, string output)
    {
        var model = OutcomeModelFile.Read(modelPath);
        var inputs = JsonSerializer.Deserialize<PredictionInput[]>(File.ReadAllText(inputsPath))
            ?? throw new InvalidDataException("Missing prediction inputs.");
        if (inputs.Any(i => i == null || i.Features == null || i.Features.Any(p =>
            string.IsNullOrWhiteSpace(p.Key) || !float.IsFinite((float)p.Value))))
            throw new InvalidDataException("Invalid prediction observation.");
        double[] predictions = inputs.Select(i => model.Select(i.Character).PredictFeaturesForTesting(i.Features)).ToArray();
        File.WriteAllText(output, JsonSerializer.Serialize(predictions));
        Console.WriteLine($"Predicted {predictions.Length} offline observations.");
        return 0;
    }
}
