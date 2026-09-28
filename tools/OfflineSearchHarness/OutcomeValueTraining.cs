using System.Diagnostics;
using System.Text.Json;
using CombatSolver;

namespace OfflineSearchHarness;

internal static partial class OutcomeValueTraining
{
    internal static int Audit(string pathsFile, string modelFile, string output)
    {
        var file = OutcomeModelFile.Read(modelFile);
        List<object> results = [];
        foreach (var root in ReadRoots(pathsFile))
        {
            var rows = root.Rows;
            int[]? turns = rows.Length > 0 && rows.All(r => r.Features.ContainsKey("battle/turn"))
                ? rows.Select(SearchOutcomeValueModel.TrainingTurn).ToArray() : null;
            Dictionary<int, (int Pairs, int Correct, int Wrong, int Ties, double Loss)> turnMetrics = [];
            var model = file.Select(file.IsConditional ? OutcomeModelFile.CharacterOf(rows) : null);
            double[] scores = rows.Select(r => model.PredictFeaturesForTesting(r.Features)).ToArray();
            HashSet<(int, int)> seen = [];
            int correct = 0, wrong = 0, ties = 0, indistinguishable = 0;
            double loss = 0;
            string[] kindNames = ["victory-over-defeat", "victory-policy", "suffix-effort"];
            int[] kindPairs = new int[3], kindCorrect = new int[3], kindWrong = new int[3], kindTies = new int[3];
            double[] kindLoss = new double[3];
            foreach (var group in Enumerable.Range(0, rows.Length)
                .SelectMany(i => rows[i].Groups.Select(g => (Group: g, Row: i))).GroupBy(p => p.Group))
            {
                int[] members = group.Select(p => p.Row).Distinct().ToArray();
                for (int a = 0; a < members.Length; a++)
                    for (int b = a + 1; b < members.Length; b++)
                    {
                        int left = members[a], right = members[b];
                        if (!seen.Add((Math.Min(left, right), Math.Max(left, right)))) continue;
                        int preference = SearchOutcomeValueModel.CompareWitnesses(rows[left], rows[right], out int kind);
                        if (preference == 0) continue;
                        double margin = (scores[right] - scores[left]) * Math.Sign(preference);
                        kindPairs[kind]++;
                        if (margin > 0) kindCorrect[kind]++; else if (margin < 0) kindWrong[kind]++; else kindTies[kind]++;
                        double pairLoss = Math.Log(1 + Math.Exp(-Math.Clamp(margin, -40, 40)));
                        if (turns != null)
                        {
                            int turn = Math.Max(turns[left], turns[right]);
                            var prior = turnMetrics.GetValueOrDefault(turn);
                            turnMetrics[turn] = (prior.Pairs + 1, prior.Correct + (margin > 0 ? 1 : 0),
                                prior.Wrong + (margin < 0 ? 1 : 0), prior.Ties + (margin == 0 ? 1 : 0), prior.Loss + pairLoss);
                        }
                        kindLoss[kind] += pairLoss;
                        if (margin > 0) correct++; else if (margin < 0) wrong++; else ties++;
                        loss += pairLoss;
                        if (rows[left].Features.Count == rows[right].Features.Count
                            && rows[left].Features.All(p => rows[right].Features.TryGetValue(p.Key, out var v) && v == p.Value))
                            indistinguishable++;
                    }
            }
            int pairs = correct + wrong + ties;
            // Directory labels can repeat (for example, every root's "search"
            // folder). Join diagnostics to the ordered input by this index.
            results.Add(new { rootIndex = results.Count, root = root.Id, rows = rows.Length,
                pairs, correct, wrong, ties, indistinguishable, logLoss = pairs == 0 ? 0 : loss / pairs,
                turns = turns == null ? null : turnMetrics.OrderBy(p => p.Key).Select(p => new
                    { turn = p.Key, pairs = p.Value.Pairs, correct = p.Value.Correct, wrong = p.Value.Wrong,
                        ties = p.Value.Ties, logLoss = p.Value.Loss / p.Value.Pairs }).ToArray(),
                kinds = Enumerable.Range(0, kindNames.Length).Select(k => new { kind = kindNames[k],
                    pairs = kindPairs[k], correct = kindCorrect[k], wrong = kindWrong[k], ties = kindTies[k],
                    logLoss = kindPairs[k] == 0 ? 0 : kindLoss[k] / kindPairs[k] }).ToArray() });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(results));
        Console.WriteLine($"Audited {results.Count} supplied roots; ranking diagnostics do not measure search decision quality.");
        return 0;
    }

    internal static int Run(string pathsFile, string output)
    {
        Stopwatch clock = Stopwatch.StartNew();
        using var specification = JsonDocument.Parse(File.ReadAllText(pathsFile));
        var (partition, pairSelection, pairWeighting) = ReadPolicy(specification.RootElement);
        string[] excludedFeaturePrefixes = ReadExcludedFeaturePrefixes(specification.RootElement);
        // Sample once in the original global root order. Partitioning never
        // restarts the row sampler or resamples a character's observations.
        var inputs = ReadRoots(pathsFile);
        var roots = inputs.Select(r => r.Rows).ToArray();
        int trainingParallelism = Math.Min(4, Environment.ProcessorCount);
        var groups = roots.Select((rows, index) => new { Rows = rows, Index = index,
                Character = partition == "character" ? OutcomeModelFile.CharacterOf(rows) : "" })
            .GroupBy(r => r.Character, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal);
        Dictionary<string, SearchOutcomeValueModel> models = new(StringComparer.Ordinal);
        List<object> heads = [];
        foreach (var group in groups)
        {
            Stopwatch headClock = Stopwatch.StartNew();
            SearchOutcomeValueModel model = new();
            if (!model.Fit(group.Select(r => r.Rows).ToArray(), trainingParallelism,
                highestPolicyTierOnly: pairSelection == "highest-policy-tier", balanceTrainingTurns: pairWeighting == "turns"))
                throw new InvalidOperationException("Insufficient witnessed outcomes for partition: " + group.Key);
            models.Add(group.Key, model);
            heads.Add(new { character = group.Key, rootIndices = group.Select(r => r.Index).ToArray(),
                roots = group.Count(), rows = group.Sum(r => r.Rows.Length), pairs = model.FittedPairs,
                pairKinds = model.FittedPairKinds,
                participatingRoots = model.FittedRoots, participatingRows = model.FittedRows,
                features = model.ExportModel().FeatureNames.Length, fitMilliseconds = headClock.Elapsed.TotalMilliseconds });
        }
        object Export(bool linear)
        {
            var documents = models.ToDictionary(p => p.Key,
                p => linear ? p.Value.ExportLinearModel() : p.Value.ExportModel(), StringComparer.Ordinal);
            return partition == "shared" ? documents[""] : new OutcomeModelFile.CharacterDocument(1, documents);
        }
        File.WriteAllText(output, JsonSerializer.Serialize(Export(linear: false)));
        string linearOutput = Path.ChangeExtension(output, "linear.json");
        File.WriteAllText(linearOutput, JsonSerializer.Serialize(Export(linear: true)));
        Console.WriteLine(JsonSerializer.Serialize(new { roots = roots.Length, rows = roots.Sum(r => r.Length),
            pairs = models.Values.Sum(m => m.FittedPairs), partition, pairSelection, pairWeighting, excludedFeaturePrefixes, heads,
            pairKinds = Enumerable.Range(0, 3).Select(k => models.Values.Sum(m => m.FittedPairKinds[k])).ToArray(),
            participatingRoots = models.Values.Sum(m => m.FittedRoots), participatingRows = models.Values.Sum(m => m.FittedRows), trainingParallelism,
            features = models.Values.Sum(m => m.ExportModel().FeatureNames.Length),
            maximumHeadFeatures = models.Values.Max(m => m.ExportModel().FeatureNames.Length),
            eligibleFeatures = models.Values.Sum(m => m.EligibleFeatures),
            linearTerms = models.Values.Sum(m => m.ExportLinearModel().FeatureNames.Length), linearBytes = new FileInfo(linearOutput).Length,
            fitMilliseconds = clock.Elapsed.TotalMilliseconds, bytes = new FileInfo(output).Length,
            peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 }));
        return 0;
    }
    // An entry is one actual root: either one observation file or several policies'
    // files for that same root. Query IDs never join across independent roll-ins.
    internal static List<(string Id, SearchOutcomeValueModel.TrainingRow[] Rows)> ReadRoots(string pathsFile,
        bool requireTrainingTurns = false)
    {
        using var input = JsonDocument.Parse(File.ReadAllText(pathsFile));
        JsonElement entries = input.RootElement;
        bool balanceTrainingTurns = ReadPairWeighting(entries) == "turns";
        string[] excludedFeaturePrefixes = ReadExcludedFeaturePrefixes(entries);
        int maximumRowsPerRoot = 2048;
        string sampling = "rows";
        if (entries.ValueKind == JsonValueKind.Object)
        {
            if (entries.GetProperty("schemaVersion").GetInt32() != 1)
                throw new InvalidDataException("Unsupported training input schema.");
            maximumRowsPerRoot = entries.GetProperty("maximumRowsPerRoot").GetInt32();
            if (maximumRowsPerRoot is < 64 or > 8192)
                throw new InvalidDataException("Training row limit must be between 64 and 8192.");
            if (entries.TryGetProperty("sampling", out var method))
                sampling = method.GetString() ?? throw new InvalidDataException("Missing training sampling method.");
            if (sampling is not ("rows" or "pools"))
                throw new InvalidDataException("Unsupported training sampling method.");
            if (entries.TryGetProperty("featureUpgrade", out _))
                throw new InvalidDataException("Resource-query observations require recollection; legacy feature upgrades are unsupported.");
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
                if (balanceTrainingTurns || requireTrainingTurns)
                    foreach (var row in source) _ = SearchOutcomeValueModel.TrainingTurn(row);
                Dictionary<int, int> groups = [];
                foreach (var row in Sample(source, maximumRowsPerRoot / files.Length, sampler, sampling))
                {
                    int Remap(int group)
                    {
                        if (!groups.TryGetValue(group, out int id)) groups.Add(group, id = nextGroup++);
                        return id;
                    }
                    Dictionary<string, double> features = new(row.Features.Count, StringComparer.Ordinal);
                    foreach (var (name, value) in row.Features)
                    {
                        // Explicit offline column ablation, after validating all
                        // raw rows. Sampling, labels and group remapping stay fixed.
                        bool excluded = false;
                        foreach (string prefix in excludedFeaturePrefixes)
                            if (name.StartsWith(prefix, StringComparison.Ordinal)) { excluded = true; break; }
                        if (excluded) continue;
                        if (!featureNames.TryGetValue(name, out string? canonical))
                            featureNames.Add(name, canonical = name);
                        features.Add(canonical, value);
                    }
                    if (features.Count == 0) throw new InvalidDataException("Feature selection removed every observation column.");
                    rows.Add(row with { Features = features, Groups = row.Groups.Select(Remap).ToArray() });
                }
            }
            roots.Add((Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(files[0])))!, rows.ToArray()));
        }
        return roots;
    }

    private static (string Partition, string PairSelection, string PairWeighting) ReadPolicy(JsonElement input)
    {
        string partition = input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("partition", out var part)
            ? part.GetString() ?? throw new InvalidDataException("Missing training partition.") : "shared";
        if (partition is not ("shared" or "character")) throw new InvalidDataException("Unknown training partition.");
        string pairSelection = input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("pairSelection", out var selection)
            ? selection.GetString() ?? throw new InvalidDataException("Missing pair selection.") : "all";
        if (pairSelection is not ("all" or "highest-policy-tier"))
            throw new InvalidDataException("Unknown training pair selection.");
        return (partition, pairSelection, ReadPairWeighting(input));
    }

    private static string ReadPairWeighting(JsonElement input)
    {
        string value = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("pairWeighting", out var field)
            ? field.ValueKind == JsonValueKind.String ? field.GetString()!
                : throw new InvalidDataException("Pair weighting must be a string.") : "pairs";
        if (value is not ("pairs" or "turns")) throw new InvalidDataException("Unknown training pair weighting.");
        return value;
    }

    private static string[] ReadExcludedFeaturePrefixes(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("excludedFeaturePrefixes", out var field)) return [];
        if (field.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Excluded feature prefixes must be an array.");
        string[] prefixes = field.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String
            ? p.GetString()! : throw new InvalidDataException("Excluded feature prefixes must be strings.")).ToArray();
        if (prefixes.Any(string.IsNullOrWhiteSpace) || prefixes.Distinct(StringComparer.Ordinal).Count() != prefixes.Length)
            throw new InvalidDataException("Excluded feature prefixes must be nonempty and unique.");
        return prefixes;
    }

    private static IEnumerable<SearchOutcomeValueModel.TrainingRow> Sample(
        SearchOutcomeValueModel.TrainingRow[] source, int maximum, Random sampler, string method)
    {
        if (method == "rows")
        {
            sampler.Shuffle(source);
            return source.Take(maximum);
        }
        // Sample actual observation pools, without looking at labels or values.
        // Independent row thinning often keeps only one side of a comparison.
        // Only witnessed members exist here; unknown/pruned outcomes stay absent.
        Dictionary<int, List<int>> groups = [];
        for (int i = 0; i < source.Length; i++)
            foreach (int group in source[i].Groups.Distinct())
            {
                if (!groups.TryGetValue(group, out var members)) groups.Add(group, members = []);
                members.Add(i);
            }
        var pools = groups.Values.Where(members => members.Count >= 2).ToArray();
        if (pools.Any(members => members.Count > maximum))
            throw new InvalidDataException("A witnessed comparison pool exceeds the per-roll-in row budget.");
        sampler.Shuffle(pools);
        bool[] included = new bool[source.Length];
        List<SearchOutcomeValueModel.TrainingRow> selected = [];
        foreach (var pool in pools)
        {
            int additional = pool.Count(i => !included[i]);
            if (selected.Count + additional > maximum) continue;
            foreach (int i in pool)
            {
                if (included[i]) continue;
                included[i] = true;
                selected.Add(source[i]);
            }
            if (selected.Count == maximum) break;
        }
        return selected;
    }
}
