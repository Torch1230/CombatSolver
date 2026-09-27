using System.Diagnostics;
using System.Text.Json;
using CombatSolver;

namespace OfflineSearchHarness;

internal static class OutcomeValueTraining
{
    internal static int Audit(string pathsFile, string modelFile, string output)
    {
        var document = JsonSerializer.Deserialize<SearchOutcomeValueModel.Document>(File.ReadAllText(modelFile))!;
        var model = SearchOutcomeValueModel.Load(document);
        List<object> results = [];
        foreach (var root in ReadRoots(pathsFile))
        {
            var rows = root.Rows;
            double[] scores = rows.Select(r => model.PredictFeaturesForTesting(r.Features)).ToArray();
            HashSet<(int, int)> seen = [];
            int correct = 0, wrong = 0, ties = 0, indistinguishable = 0;
            double loss = 0;
            foreach (var group in Enumerable.Range(0, rows.Length)
                .SelectMany(i => rows[i].Groups.Select(g => (Group: g, Row: i))).GroupBy(p => p.Group))
            {
                int[] members = group.Select(p => p.Row).Distinct().ToArray();
                for (int a = 0; a < members.Length; a++)
                    for (int b = a + 1; b < members.Length; b++)
                    {
                        int left = members[a], right = members[b];
                        if (!seen.Add((Math.Min(left, right), Math.Max(left, right)))) continue;
                        int preference = SearchOutcomeValueModel.CompareWitnesses(rows[left], rows[right]);
                        if (preference == 0) continue;
                        double margin = (scores[right] - scores[left]) * Math.Sign(preference);
                        if (margin > 0) correct++; else if (margin < 0) wrong++; else ties++;
                        loss += Math.Log(1 + Math.Exp(-Math.Clamp(margin, -40, 40)));
                        if (rows[left].Features.Count == rows[right].Features.Count
                            && rows[left].Features.All(p => rows[right].Features.TryGetValue(p.Key, out var v) && v == p.Value))
                            indistinguishable++;
                    }
            }
            int pairs = correct + wrong + ties;
            results.Add(new { root = root.Id, rows = rows.Length,
                pairs, correct, wrong, ties, indistinguishable, logLoss = pairs == 0 ? 0 : loss / pairs });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(results));
        Console.WriteLine($"Audited {results.Count} roots; this is fitted-data diagnostics, not independent evaluation.");
        return 0;
    }

    internal static int Run(string pathsFile, string output)
    {
        Stopwatch clock = Stopwatch.StartNew();
        var inputs = ReadRoots(pathsFile);
        var roots = inputs.Select(r => r.Rows).ToArray();
        SearchOutcomeValueModel model = new();
        int trainingParallelism = Math.Min(4, Environment.ProcessorCount);
        if (!model.Fit(roots, trainingParallelism)) throw new InvalidOperationException("Insufficient witnessed outcomes.");
        File.WriteAllText(output, JsonSerializer.Serialize(model.ExportModel()));
        string linearOutput = Path.ChangeExtension(output, "linear.json");
        File.WriteAllText(linearOutput, JsonSerializer.Serialize(model.ExportLinearModel()));
        Console.WriteLine(JsonSerializer.Serialize(new { roots = roots.Length, rows = roots.Sum(r => r.Length), pairs = model.FittedPairs,
            participatingRoots = model.FittedRoots, participatingRows = model.FittedRows, trainingParallelism,
            features = model.ExportModel().FeatureNames.Length,
            eligibleFeatures = model.EligibleFeatures,
            linearTerms = model.ExportLinearModel().FeatureNames.Length, linearBytes = new FileInfo(linearOutput).Length,
            fitMilliseconds = clock.Elapsed.TotalMilliseconds, bytes = new FileInfo(output).Length,
            peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 }));
        return 0;
    }
    // An entry is one actual root: either one observation file or several policies'
    // files for that same root. Query IDs never join across independent roll-ins.
    internal static List<(string Id, SearchOutcomeValueModel.TrainingRow[] Rows)> ReadRoots(string pathsFile)
    {
        using var input = JsonDocument.Parse(File.ReadAllText(pathsFile));
        JsonElement entries = input.RootElement;
        int maximumRowsPerRoot = 2048;
        if (entries.ValueKind == JsonValueKind.Object)
        {
            if (entries.GetProperty("schemaVersion").GetInt32() != 1)
                throw new InvalidDataException("Unsupported training input schema.");
            maximumRowsPerRoot = entries.GetProperty("maximumRowsPerRoot").GetInt32();
            if (maximumRowsPerRoot is < 64 or > 8192)
                throw new InvalidDataException("Training row limit must be between 64 and 8192.");
            entries = entries.GetProperty("roots");
        }
        if (entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Expected training roots array.");
        List<(string, SearchOutcomeValueModel.TrainingRow[])> roots = [];
        // Fit-owned canonical strings, never process-global String.Intern. Each
        // observed value and row keeps its own dictionary; only immutable names
        // share storage across roots. Preserve insertion and sampling order.
        Dictionary<string, string> featureNames = new(StringComparer.Ordinal);
        Random sampler = new(0);
        foreach (var entry in entries.EnumerateArray())
        {
            string[] files = entry.ValueKind == JsonValueKind.String ? [entry.GetString()!]
                : entry.Deserialize<string[]>() ?? throw new InvalidDataException("Invalid root files.");
            if (files.Length == 0 || files.Length > 8) throw new InvalidDataException("Invalid roll-in count.");
            List<SearchOutcomeValueModel.TrainingRow> rows = [];
            int nextGroup = 0;
            foreach (string file in files)
            {
                using var stream = File.OpenRead(file);
                var source = JsonSerializer.Deserialize<SearchOutcomeValueModel.TrainingRow[]>(stream)
                    ?? throw new InvalidDataException("Missing training observations.");
                // Invalid labels must not disappear merely because the sampler
                // would omit them. Use the fitter's authoritative validator.
                SearchOutcomeValueModel.ValidateTrainingRows(source);
                sampler.Shuffle(source);
                Dictionary<int, int> groups = [];
                foreach (var row in source.Take(maximumRowsPerRoot / files.Length))
                {
                    int Remap(int group)
                    {
                        if (!groups.TryGetValue(group, out int id)) groups.Add(group, id = nextGroup++);
                        return id;
                    }
                    Dictionary<string, double> features = new(row.Features.Count, StringComparer.Ordinal);
                    foreach (var (name, value) in row.Features)
                    {
                        if (!featureNames.TryGetValue(name, out string? canonical))
                            featureNames.Add(name, canonical = name);
                        features.Add(canonical, value);
                    }
                    rows.Add(row with { Features = features, Groups = row.Groups.Select(Remap).ToArray() });
                }
            }
            roots.Add((Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(files[0])))!, rows.ToArray()));
        }
        return roots;
    }

}
