using System.Text.Json;
using CombatSolver;
using MegaCrit.Sts2.Core.Entities.Players;
using Model = CombatSolver.SearchOutcomeValueModel;

namespace OfflineSearchHarness;

internal static class OutcomeRankingChecks
{
    internal static int Run()
    {
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
            a with { FeatureSchema = 0 }, a with { FeatureSchema = 5 },
        }) Reject(() => new Model().Fit([[invalid, b]]), "invalid/unknown label rejected");

        var loss = b with { Outcome = b.Outcome with { Won = false, Survives = false }, CompletedDefeat = true };
        Check(Model.CompareWitnesses(a, loss) < 0, "completed victory outranks an observed defeat");
        Check(Model.CompareWitnesses(loss, loss with { RemainingActions = 100 }) == 0, "defeat effort does not reward faster death");
        var mixed = new Model();
        mixed.Fit([[a, b, loss]]);
        Check(mixed.FittedPairs == 3, "genuine defeat witnesses add cross-outcome preferences");
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
        Reject(() => Model.Load(document with { LinearWeights = [1] }), "misaligned linear weights rejected");
        Reject(() => Model.Load(document with { LinearWeights = [0, 0, double.NaN] }), "non-finite linear weights rejected");
        Reject(() => Model.Load(document with { GameMvid = Guid.Empty }), "game version mismatch rejected");
        Reject(() => Model.Load(document with { Forest = [new(2, 0, 0)] }), "incomplete tree rejected");
        var learned = new Model();
        Check(learned.Fit([Enumerable.Range(0, 24).Select(i => Row(i, 24 - i)).ToArray()]), "small pairwise dataset fits");
        Check(learned.PredictFeaturesForTesting(new Dictionary<string, double> { ["x"] = 23 })
            > learned.PredictFeaturesForTesting(new Dictionary<string, double> { ["x"] = 0 }), "gradient learns preferred direction");
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
        string directory = Path.Combine(Path.GetTempPath(), "outcome-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string one = Path.Combine(directory, "one.json"), two = Path.Combine(directory, "two.json");
            File.WriteAllText(one, JsonSerializer.Serialize(new[] { a }));
            File.WriteAllText(two, JsonSerializer.Serialize(new[] { b }));
            string inputs = Path.Combine(directory, "inputs.json");
            File.WriteAllText(inputs, JsonSerializer.Serialize(new[] { new[] { one, two } }));
            var grouped = OutcomeValueTraining.ReadRoots(inputs);
            var groupedModel = new Model();
            Check(grouped.Count == 1 && grouped[0].Rows.Length == 2, "collection policies retain one actual root");
            Check(!groupedModel.Fit(grouped.Select(r => r.Rows).ToArray()) && groupedModel.FittedPairs == 0,
                "independent roll-in pool numbers cannot create false preferences");
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine($"Outcome ranking: {checks} assertions passed.");
        return 0;
    }
}
