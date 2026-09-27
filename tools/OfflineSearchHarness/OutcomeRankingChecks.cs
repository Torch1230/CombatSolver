using System.Text.Json;
using CombatSolver;
using MegaCrit.Sts2.Core.Entities.Players;
using Model = CombatSolver.SearchOutcomeValueModel;

namespace OfflineSearchHarness;

internal static class OutcomeRankingChecks
{
    internal static int Run()
    {
        // Reuse the existing production-policy contract: unfinished/forced-use
        // results cannot certify bounds, and later low-loss routes remain open.
        typeof(UnattendedTestRunner).GetMethod("AssertPrimaryIncumbentEligibility",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, null);
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Outcome ranking: " + message);
            checks++;
        }
        void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, message);
        }
        SolverInterimResult quality = new(true, 0, 3, 3, 0, 0, 0, 0, 2) { Survives = true };
        Model.TrainingRow Row(int x, int hp, int[]? groups = null) => new(new() { ["x"] = x },
            quality with { ProjectedBattleHpLost = hp, StrategicHpDeficit = hp }, 3, groups ?? [0], FeatureSchema: Model.FeatureSchema);
        var a = Row(0, 2) with { RemainingActions = 50 };
        var b = Row(1, 3) with { RemainingActions = 1 };
        Check(Model.CompareWitnesses(a, b) < 0, "final HP policy outranks suffix length");
        Check(Model.CompareWitnesses(b with { Outcome = a.Outcome }, a) < 0, "effort breaks full-policy ties");
        Check(Model.CompareWitnesses(a, a) == 0, "identical witnesses are not preferences");
        Check(Model.CompareWitnesses(a with { Outcome = a.Outcome with { DeathSaveUseCount = 1 } }, b) > 0,
            "death-save policy precedes HP");
        Check(Model.CompareWitnesses(b with { Outcome = b.Outcome with { StrategicHpDeficit = 1, GrowthHpCredit = 2 } }, a) < 0,
            "strategic reward participates in labels");
        var isolated = new Model();
        Check(!isolated.Fit([[a], [b]]) && isolated.FittedPairs == 0, "group numbers never join separate roots");
        var repeated = new Model();
        repeated.Fit([[a with { Groups = [0, 1] }, b with { Groups = [0, 1] }]]);
        Check(repeated.FittedPairs == 1, "repeated pool membership does not multiply a pair");
        foreach (var invalid in new[]
        {
            a with { Outcome = a.Outcome with { Score = 100 } },
            a with { Outcome = a.Outcome with { Won = false } },
            a with { Outcome = a.Outcome with { Survives = false } },
            a with { Groups = [] }, a with { Features = new() { ["x"] = double.NaN } },
            a with { FeatureSchema = 0 }, a with { FeatureSchema = 5 }, a with { FeatureSchema = 6 }, a with { FeatureSchema = 7 },
        }) Reject(() => new Model().Fit([[invalid, b]]), "invalid/unknown label rejected");

        var loss = b with { Outcome = b.Outcome with { Won = false, Survives = false }, CompletedDefeat = true };
        Check(Model.CompareWitnesses(a, loss) < 0, "completed victory outranks an observed defeat");
        Check(Model.CompareWitnesses(loss, loss with { RemainingActions = 100 }) == 0, "defeat effort does not reward faster death");
        var mixed = new Model();
        mixed.Fit([[a, b, loss]]);
        Check(mixed.FittedPairs == 3, "genuine defeat witnesses add cross-outcome preferences");
        Check(mixed.FittedPairKinds.SequenceEqual([2, 1, 0]), "fitted pair kinds use the final-policy comparator");
        var distinctLoss = loss with { Features = new() { ["x"] = 3 } };
        var tiered = new Model(); var explicitPairs = new Model();
        Check(tiered.Fit([[a, b, distinctLoss]], highestPolicyTierOnly: true)
            && explicitPairs.Fit([[a with { Groups = [0] }, b with { Groups = [1] }, distinctLoss with { Groups = [0, 1] }]]),
            "highest-tier fitting retains witnessed victory comparisons");
        Check(tiered.FittedPairKinds.SequenceEqual([2, 0, 0])
            && JsonSerializer.Serialize(tiered.ExportModel()) == JsonSerializer.Serialize(explicitPairs.ExportModel()),
            "tier selection equals the explicitly separated comparisons with the same root weight");
        var lowerTier = new Model();
        Check(lowerTier.Fit([[a, b, Row(2, 4)]], highestPolicyTierOnly: true)
            && lowerTier.FittedPairKinds.SequenceEqual([0, 3, 0]),
            "roots without observed defeats still learn victory-policy quality");
        Reject(() => new Model().Fit([[a, distinctLoss, b with { Outcome = b.Outcome with { Score = 1 } }]],
            highestPolicyTierOnly: true), "discarded lower-tier labels still require validation");
        Reject(() => new Model().Fit([[loss with { CompletedDefeat = false }, a]]), "unfinished loss cannot masquerade as a defeat witness");
        Model.Tree tree = new(2, 0.5, 0, new(-1, 0, -2), new(-1, 0, 3));
        Model.Document document = new(Model.Schema, ["unused", "also-unused", "counter"],
            typeof(Player).Assembly.ManifestModule.ModuleVersionId, [tree]);
        var loaded = Model.Load(document);
        Check(loaded.ExportModel().FeatureNames.SequenceEqual(["counter"]), "unused columns removed");
        Check(loaded.PredictFeaturesForTesting(new Dictionary<string, double> { ["counter"] = 1 }) == 0.1 * 3,
            "compiled split retains numeric prediction");
        Check(loaded.PredictFeaturesForTesting(new Dictionary<string, double> { ["unused"] = 100 }) == -0.2,
            "unknown/missing columns preserve zero convention");
        var roundtrip = Model.Load(JsonSerializer.Deserialize<Model.Document>(JsonSerializer.Serialize(loaded.ExportModel()))!);
        Check(roundtrip.PredictFeaturesForTesting(new Dictionary<string, double> { ["counter"] = 1 })
            == loaded.PredictFeaturesForTesting(new Dictionary<string, double> { ["counter"] = 1 }), "compact model roundtrip");
        var constant = Model.Load(document with { Forest = [new(-1, 0, 2)] });
        Check(Model.Load(constant.ExportModel()).PredictFeaturesForTesting(new Dictionary<string, double>()) == 0.2,
            "constant forest with no split columns roundtrips");
        var redundant = Model.Load(document with { Forest = [new(2, 0, 0, new(-1, 0, 2), new(-1, 0, 2))] });
        Check(redundant.ExportModel().FeatureNames.Length == 0
            && redundant.PredictFeaturesForTesting(new Dictionary<string, double>()) == 0.2,
            "neutral splits with equal leaf predictions are removed exactly");
        Reject(() => Model.Load(document with { Schema = 3 }), "old label schema rejected");
        Reject(() => Model.Load(document with { Schema = 5 }), "old power-owner schema rejected");
        Reject(() => Model.Load(document with { Schema = 6 }), "old forest-only model schema rejected");
        Reject(() => Model.Load(document with { Schema = 7 }), "old enemy-position model schema rejected");
        Reject(() => Model.Load(document with { LinearWeights = [1] }), "misaligned linear weights rejected");
        Reject(() => Model.Load(document with { LinearWeights = [0, 0, double.NaN] }), "non-finite linear weights rejected");
        Reject(() => Model.Load(document with { GameMvid = Guid.Empty }), "game version mismatch rejected");
        Reject(() => Model.Load(document with { Forest = [new(2, 0, 0)] }), "incomplete tree rejected");
        var learned = new Model();
        Check(learned.Fit([Enumerable.Range(0, 24).Select(i => Row(i, 24 - i)).ToArray()]), "small pairwise dataset fits");
        Check(learned.PredictFeaturesForTesting(new Dictionary<string, double> { ["x"] = 23 })
            > learned.PredictFeaturesForTesting(new Dictionary<string, double> { ["x"] = 0 }), "gradient learns preferred direction");
        var prepared = Model.PrepareTraining([Enumerable.Range(0, 24).Select(i => Row(i, 24 - i)).ToArray()]);
        var foundation = Model.FitLinearFoundation(prepared);
        Check(prepared.Pairs.Count == learned.FittedPairs && prepared.Rows.Count == learned.FittedRows
            && prepared.PairKinds.SequenceEqual(learned.FittedPairKinds)
            && Math.Abs(prepared.Pairs.Sum(p => p.Weight) - 1) < 1e-12,
            "shared preparation retains exact fitted pair counts and equal root weight");
        Check(JsonSerializer.Serialize(Model.Load(foundation.Model).ExportModel())
            == JsonSerializer.Serialize(learned.ExportLinearModel()), "exported foundation equals built-in linear model");
        Check(foundation.Scores.Select((v, i) => v == Model.Load(foundation.Model)
            .PredictFeaturesForTesting(prepared.Rows[i].Features)).All(v => v),
            "exported base margins use production float observations and accumulation");
        var withUnused = new Model();
        Model.TrainingRow[] useful = Enumerable.Range(0, 24).Select(i => Row(i, 24 - i)).ToArray();
        Model.TrainingRow[] onlyDefeats = Enumerable.Range(0, 300).Select(i => loss with
            { Features = new() { ["x"] = i * 1000 }, RemainingActions = i }).ToArray();
        Check(withUnused.Fit([onlyDefeats, [.. useful, Row(1000000, 0, [91])]]),
            "unpaired completed rows are valid observations");
        Check(withUnused.FittedRoots == 1 && withUnused.FittedRows == 24,
            "training storage includes only witnesses referenced by actual preference pairs");
        Check(JsonSerializer.Serialize(withUnused.ExportModel()) == JsonSerializer.Serialize(learned.ExportModel()),
            "compacting all-defeat and singleton rows preserves the exact serialized model");
        Reject(() => new Model().Fit([useful, [loss with { Outcome = loss.Outcome with { Score = 1 } }]]),
            "unpaired rows still undergo strict label validation");
        var linear = Model.Load(learned.ExportLinearModel());
        Check(linear.ExportModel().FeatureNames.SequenceEqual(["x"]), "linear-only artifact retains its active column");
        Check(linear.PredictFeaturesForTesting(new Dictionary<string, double> { ["x"] = 100 })
            > linear.PredictFeaturesForTesting(new Dictionary<string, double> { ["x"] = 24 }),
            "linear utility extrapolates beyond training thresholds");
        var rescaled = new Model();
        Check(rescaled.Fit([Enumerable.Range(0, 24).Select(i => Row(i, 24 - i) with
            { Features = new() { ["x"] = i * 1000 } }).ToArray()]), "rescaled observations fit");
        Check(Math.Abs(Model.Load(rescaled.ExportLinearModel()).PredictFeaturesForTesting(new Dictionary<string, double> { ["x"] = 17000 })
            - linear.PredictFeaturesForTesting(new Dictionary<string, double> { ["x"] = 17 })) < 1e-8,
            "pair normalization preserves utility under a change of units");
        var conditional = new Model();
        var contexts = Enumerable.Range(0, 2).Select(context => Enumerable.Range(0, 24)
            .Select(i => Row(i, context == 0 ? 24 - i : i) with
                { Features = new() { ["relic/context"] = context, ["x"] = i } }).ToArray()).ToArray();
        Check(conditional.Fit(contexts), "opposing context preferences fit");
        double Preference(int context, int x) => conditional.PredictFeaturesForTesting(
            new Dictionary<string, double> { ["relic/context"] = context, ["x"] = x });
        Check(Preference(0, 23) > Preference(0, 0) && Preference(1, 23) < Preference(1, 0),
            "identical actions reverse priority under different root-constant contexts");
        Check(!conditional.ExportLinearModel().FeatureNames.Contains("relic/context"),
            "constant-per-root identities cannot acquire marginal linear utility");
        var supported = new Model();
        var sparseRoots = Enumerable.Range(0, 3).Select(root => Enumerable.Range(0, 24)
            .Select(i => Row(i, root == 0 ? i : 24 - i) with
            { Features = root == 0 ? new() { ["x"] = i, ["one-root-identity"] = i }
                : new() { ["x"] = i } }).ToArray()).ToArray();
        Check(supported.Fit(sparseRoots) && supported.EligibleFeatures == 1,
            "correlated rows in one root do not supply independent feature support");
        Check(!supported.ExportModel().FeatureNames.Contains("one-root-identity"),
            "unsupported identity is excluded from both linear and tree inference");
        Model.TrainingRow[] parallelRows = Enumerable.Range(0, 1100).Select(i => Row(i, 1100 - i) with
        {
            Features = Enumerable.Range(0, 72).ToDictionary(j => "column/" + j, j => (double)((i * (j + 1)) % 1103)),
        }).ToArray();
        var serialFit = new Model();
        var parallelFit = new Model();
        Check(serialFit.Fit([parallelRows], maximumTrainingParallelism: 1)
            && parallelFit.Fit([parallelRows], maximumTrainingParallelism: 4), "bounded parallel histogram fitting succeeds");
        Check(JsonSerializer.Serialize(serialFit.ExportModel()) == JsonSerializer.Serialize(parallelFit.ExportModel()),
            "parallel column statistics preserve the exact serial model and tie order");
        string directory = Path.Combine(Path.GetTempPath(), "outcome-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string one = Path.Combine(directory, "one.json"), two = Path.Combine(directory, "two.json");
            string upgradedInput = Path.Combine(directory, "upgrade-input.json");
            var legacyPowerRow = a with { FeatureSchema = 7 };
            File.WriteAllText(one, JsonSerializer.Serialize(new[] { legacyPowerRow }));
            void WriteUpgrade(string method) => File.WriteAllText(upgradedInput,
                JsonSerializer.Serialize(new { schemaVersion = 1, maximumRowsPerRoot = 64,
                    roots = new[] { one }, featureUpgrade = method }));
            WriteUpgrade("enemy-power-totals-v1");
            Reject(() => OutcomeValueTraining.ReadRoots(upgradedInput),
                "old observations cannot invent unrecorded current resource queries");
            WriteUpgrade("unknown");
            Reject(() => OutcomeValueTraining.ReadRoots(upgradedInput), "unknown feature conversions are rejected");
            File.WriteAllText(upgradedInput, JsonSerializer.Serialize(new[] { one }));
            Reject(() => OutcomeValueTraining.ReadRoots(upgradedInput), "unconverted old observations require recollection");
            File.WriteAllText(one, JsonSerializer.Serialize(new[] { a }));
            File.WriteAllText(two, JsonSerializer.Serialize(new[] { b }));
            string inputs = Path.Combine(directory, "inputs.json");
            File.WriteAllText(inputs, JsonSerializer.Serialize(new[] { new[] { one, two } }));
            var grouped = OutcomeValueTraining.ReadRoots(inputs);
            var groupedModel = new Model();
            Check(grouped.Count == 1 && grouped[0].Rows.Length == 2, "collection policies retain one actual root");
            Check(!groupedModel.Fit(grouped.Select(r => r.Rows).ToArray()) && groupedModel.FittedPairs == 0,
                "independent roll-in pool numbers cannot create false preferences");
            Check(ReferenceEquals(grouped[0].Rows[0].Features.Keys.Single(), grouped[0].Rows[1].Features.Keys.Single()),
                "repeated feature names share fit-owned immutable storage across files");
            grouped[0].Rows[0].Features["x"] = 99;
            Check(grouped[0].Rows[1].Features["x"] == b.Features["x"],
                "sharing names cannot alias mutable feature values");
            var observations = Enumerable.Range(0, 2100).Select(i => Row(i, 2100 - i, [i / 8])).ToArray();
            File.WriteAllText(one, JsonSerializer.Serialize(observations));
            File.WriteAllText(inputs, JsonSerializer.Serialize(new[] { one }));
            var legacy = OutcomeValueTraining.ReadRoots(inputs);
            void WriteBudget(int maximumRowsPerRoot) => File.WriteAllText(inputs,
                JsonSerializer.Serialize(new { schemaVersion = 1, maximumRowsPerRoot, roots = new[] { one } }));
            WriteBudget(2048);
            Check(JsonSerializer.Serialize(legacy[0].Rows) == JsonSerializer.Serialize(OutcomeValueTraining.ReadRoots(inputs)[0].Rows),
                "explicit default budget preserves legacy sampling and pool remapping");
            WriteBudget(64);
            var limited = OutcomeValueTraining.ReadRoots(inputs);
            Check(limited[0].Rows.Length == 64 && limited[0].Rows.Select(r => r.Features["x"]).Distinct().Count() == 64,
                "bounded root sampling retains distinct real witnesses");
            Check(limited[0].Rows.Select(r => r.Features["x"]).SequenceEqual(legacy[0].Rows.Take(64).Select(r => r.Features["x"])),
                "changing the row budget preserves the deterministic sampling prefix");
            var retained = limited[0].Rows.Select(r => (int)r.Features["x"]).ToHashSet();
            int omitted = Enumerable.Range(0, observations.Length).First(i => !retained.Contains(i));
            File.WriteAllText(one, JsonSerializer.Serialize(observations.Select(r => r with
                { Features = new(r.Features) { ["card/id"] = 3, ["relic/context"] = 7 } })));
            void WriteExclusions(string[] prefixes) => File.WriteAllText(inputs, JsonSerializer.Serialize(new
                { schemaVersion = 1, maximumRowsPerRoot = 64, excludedFeaturePrefixes = prefixes, roots = new[] { one } }));
            WriteExclusions(["card/"]);
            var projected = OutcomeValueTraining.ReadRoots(inputs)[0].Rows;
            Check(projected.All(r => !r.Features.ContainsKey("card/id") && r.Features["relic/context"] == 7)
                && projected.Select(r => r.Features["x"]).SequenceEqual(limited[0].Rows.Select(r => r.Features["x"]))
                && projected.Select(r => JsonSerializer.Serialize(new { r.Outcome, r.RemainingActions, r.Groups }))
                    .SequenceEqual(limited[0].Rows.Select(r => JsonSerializer.Serialize(new { r.Outcome, r.RemainingActions, r.Groups }))),
                "column ablation preserves sampled witnesses, labels, groups and retained context values");
            WriteExclusions([""]);
            Reject(() => OutcomeValueTraining.ReadRoots(inputs), "an empty prefix cannot silently remove all features");
            WriteExclusions(["card/", "card/"]);
            Reject(() => OutcomeValueTraining.ReadRoots(inputs), "duplicate exclusion prefixes are rejected");
            WriteExclusions(["card/", "relic/", "x"]);
            Reject(() => OutcomeValueTraining.ReadRoots(inputs), "removing every observed feature is explicit failure");
            WriteExclusions(["card/"]);
            observations[omitted] = observations[omitted] with { Outcome = observations[omitted].Outcome with { Score = 10 } };
            File.WriteAllText(one, JsonSerializer.Serialize(observations));
            Reject(() => OutcomeValueTraining.ReadRoots(inputs), "column ablation cannot hide an invalid unsampled raw label");
            WriteBudget(64);
            Reject(() => OutcomeValueTraining.ReadRoots(inputs), "invalid raw labels cannot hide outside the sampled rows");
            WriteBudget(0);
            Reject(() => OutcomeValueTraining.ReadRoots(inputs), "invalid host row budget is rejected");
            void WritePoolBudget(string sampling = "pools") => File.WriteAllText(inputs,
                JsonSerializer.Serialize(new { schemaVersion = 1, maximumRowsPerRoot = 64, sampling, roots = new[] { one } }));
            File.WriteAllText(one, JsonSerializer.Serialize(Enumerable.Range(0, 128).Select(i => Row(i, 128 - i, [i / 2]))));
            WritePoolBudget();
            var pools = OutcomeValueTraining.ReadRoots(inputs)[0].Rows;
            Check(pools.Length == 64 && pools.SelectMany(r => r.Groups).GroupBy(g => g).All(g => g.Count() == 2),
                "pool sampling retains both witnesses of every selected disjoint comparison");
            Check(pools.Select(r => r.Features["x"]).Distinct().Count() == 64,
                "pool sampling cannot duplicate a state to inflate independent evidence");
            Check(JsonSerializer.Serialize(pools) == JsonSerializer.Serialize(OutcomeValueTraining.ReadRoots(inputs)[0].Rows),
                "pool sampling is deterministic under a frozen input");
            File.WriteAllText(one, JsonSerializer.Serialize(Enumerable.Range(0, 128).Select(i => Row(i, i, [i / 2]))));
            Check(pools.Select(r => r.Features["x"]).SequenceEqual(
                OutcomeValueTraining.ReadRoots(inputs)[0].Rows.Select(r => r.Features["x"])),
                "reversing every preference cannot influence which pools are sampled");
            File.WriteAllText(one, JsonSerializer.Serialize(Enumerable.Range(0, 100).Select(i => Row(i, 100 - i, [i / 2, i / 4]))));
            var overlapping = OutcomeValueTraining.ReadRoots(inputs)[0].Rows;
            Check(overlapping.Length <= 64 && overlapping.Select(r => r.Features["x"]).Distinct().Count() == overlapping.Length,
                "overlapping observation pools share their real rows within the budget");
            File.WriteAllText(one, JsonSerializer.Serialize(Enumerable.Range(0, 128).Select(i => Row(i, 128 - i))));
            Reject(() => OutcomeValueTraining.ReadRoots(inputs), "pool budgets cannot silently truncate a witnessed pool");
            WritePoolBudget("unknown");
            Reject(() => OutcomeValueTraining.ReadRoots(inputs), "unknown sampling cannot silently fall back to row thinning");
            File.WriteAllText(one, JsonSerializer.Serialize(Enumerable.Range(0, 128).Select(i => Row(i, 128 - i, [i / 2]) with
                { Features = new() { ["x"] = i, ["character/A"] = 1 } })));
            File.WriteAllText(two, JsonSerializer.Serialize(Enumerable.Range(0, 128).Select(i => Row(i, i, [i / 2]) with
                { Features = new() { ["x"] = i, ["character/B"] = 1 } })));
            File.WriteAllText(inputs, JsonSerializer.Serialize(new { schemaVersion = 1, maximumRowsPerRoot = 64,
                partition = "character", roots = new[] { one, two } }));
            var once = OutcomeValueTraining.ReadRoots(inputs);
            var expectedA = new Model(); var expectedB = new Model();
            Check(expectedA.Fit([once[0].Rows]) && expectedB.Fit([once[1].Rows]), "fixed character samples fit independently");
            string bundled = Path.Combine(directory, "conditional.json");
            Check(OutcomeValueTraining.Run(inputs, bundled) == 0, "one command produces a self-contained character model");
            var bank = OutcomeModelFile.Read(bundled);
            Check(bank.IsConditional && JsonSerializer.Serialize(bank.Select("A").ExportModel()) == JsonSerializer.Serialize(expectedA.ExportModel())
                && JsonSerializer.Serialize(bank.Select("B").ExportModel()) == JsonSerializer.Serialize(expectedB.ExportModel()),
                "partitioning fits the exact global row sample without restarting the sampler");
            Dictionary<string, double> high = new() { ["x"] = 100 }, low = new() { ["x"] = 0 };
            Check(bank.Select("A").PredictFeaturesForTesting(high) > bank.Select("A").PredictFeaturesForTesting(low)
                && bank.Select("B").PredictFeaturesForTesting(high) < bank.Select("B").PredictFeaturesForTesting(low),
                "native character selection preserves opposing learned preferences");
            string auditOutput = Path.Combine(directory, "audit.json");
            OutcomeValueTraining.Audit(inputs, bundled, auditOutput);
            using (var diagnostics = JsonDocument.Parse(File.ReadAllText(auditOutput)))
            {
                var records = diagnostics.RootElement.EnumerateArray().ToArray();
                Check(records.Length == 2 && records[0].GetProperty("root").GetString() == records[1].GetProperty("root").GetString()
                    && records.Select(r => r.GetProperty("rootIndex").GetInt32()).SequenceEqual([0, 1]),
                    "audit indices distinguish ordered roots even when their directory labels coincide");
                Check(records.All(r => r.GetProperty("pairs").GetInt32() > 0 && r.GetProperty("wrong").GetInt32() == 0),
                    "audit keeps each root paired with its observed character model");
            }
            Reject(() => bank.Select("missing"), "missing character head cannot silently use another character");
            var corrupt = new OutcomeModelFile.CharacterDocument(1, new()
                { ["A"] = expectedA.ExportModel(), ["B"] = expectedB.ExportModel() with { GameMvid = Guid.Empty } });
            Reject(() => OutcomeModelFile.Parse(JsonSerializer.Serialize(corrupt)), "an incompatible unused head rejects the entire bundle");
            Reject(() => OutcomeModelFile.CharacterOf([once[0].Rows[0], once[1].Rows[0]]), "one actual root cannot mix character identities");
            Reject(() => OutcomeModelFile.CharacterOf([a]), "conditional fitting requires an observed character identity");
            Reject(() => OutcomeModelFile.CharacterOf([a with { Features = new() { ["character/A"] = 1, ["character/B"] = 1 } }]),
                "ambiguous observed character identities are rejected");
            var sharedFile = OutcomeModelFile.Parse(JsonSerializer.Serialize(expectedA.ExportModel()));
            Check(!sharedFile.IsConditional && JsonSerializer.Serialize(sharedFile.Select(null).ExportModel()) == JsonSerializer.Serialize(expectedA.ExportModel()),
                "legacy shared documents remain loadable without character metadata");
            File.WriteAllText(inputs, JsonSerializer.Serialize(new { schemaVersion = 1, maximumRowsPerRoot = 64,
                partition = "unknown", roots = new[] { one, two } }));
            Reject(() => OutcomeValueTraining.Run(inputs, bundled), "unknown partitions are rejected before fitting");
            File.WriteAllText(inputs, JsonSerializer.Serialize(new { schemaVersion = 1, maximumRowsPerRoot = 64,
                pairSelection = "unknown", roots = new[] { one, two } }));
            Reject(() => OutcomeValueTraining.Run(inputs, bundled), "unknown pair selection cannot silently use all pairs");
            File.WriteAllText(one, JsonSerializer.Serialize(new[] { a, Row(1, 2) with { RemainingActions = 2 },
                Row(2, 3), loss with { Features = new() { ["x"] = 3 } } }));
            File.WriteAllText(inputs, JsonSerializer.Serialize(new[] { one }));
            File.WriteAllText(bundled, JsonSerializer.Serialize(new Model.Document(Model.Schema, ["x"],
                typeof(Player).Assembly.ManifestModule.ModuleVersionId, [new(-1, 0, 0)], [1])));
            OutcomeValueTraining.Audit(inputs, bundled, auditOutput);
            using (var diagnostics = JsonDocument.Parse(File.ReadAllText(auditOutput)))
            {
                var kinds = diagnostics.RootElement[0].GetProperty("kinds").EnumerateArray()
                    .ToDictionary(r => r.GetProperty("kind").GetString()!, r => r);
                Check(kinds["victory-over-defeat"].GetProperty("pairs").GetInt32() == 3
                    && kinds["victory-over-defeat"].GetProperty("wrong").GetInt32() == 3,
                    "audit exposes losing witnesses incorrectly ranked ahead of all three winning witnesses");
                Check(kinds["victory-policy"].GetProperty("pairs").GetInt32() == 2
                    && kinds["victory-policy"].GetProperty("wrong").GetInt32() == 2
                    && kinds["suffix-effort"].GetProperty("pairs").GetInt32() == 1
                    && kinds["suffix-effort"].GetProperty("correct").GetInt32() == 1,
                    "effort accuracy cannot disguise incorrect final-policy ordering");
            }
            string exported = Path.Combine(directory, "exported");
            OutcomeValueTraining.Export(inputs, exported);
            using var exportedManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(exported, "manifest.json")));
            var exportedHead = exportedManifest.RootElement.GetProperty("heads")[0];
            var expected = Model.PrepareTraining(OutcomeValueTraining.ReadRoots(inputs).Select(r => r.Rows).ToArray());
            Check(exportedHead.GetProperty("pairs").GetInt32() == expected.Pairs.Count
                && exportedHead.GetProperty("rows").GetInt32() == expected.Rows.Count,
                "binary export retains sampled graph shape");
            using (var edges = new BinaryReader(File.OpenRead(Path.Combine(exported,
                exportedHead.GetProperty("edges").GetString()!))))
            {
                Check(expected.Pairs.All(p => edges.ReadInt32() == p.Preferred
                    && edges.ReadInt32() == p.Other && edges.ReadDouble() == p.Weight)
                    && edges.BaseStream.Position == edges.BaseStream.Length, "binary edges preserve exact order and weights");
            }
            using (var matrix = new BinaryReader(File.OpenRead(Path.Combine(exported,
                exportedHead.GetProperty("matrix").GetString()!))))
                Check(expected.Rows.All(r => expected.FeatureNames.All(n =>
                    matrix.ReadSingle() == (float)r.Features.GetValueOrDefault(n)))
                    && matrix.BaseStream.Position == matrix.BaseStream.Length, "dense binary matrix uses explicit float zeros");
            Reject(() => OutcomeValueTraining.Export(inputs, exported), "exports cannot overwrite existing evidence");
        }
        finally { Directory.Delete(directory, recursive: true); }
        checks += OutcomeInteractionChecks.Run();
        Console.WriteLine($"Outcome ranking: {checks} assertions passed.");
        return 0;
    }
}
