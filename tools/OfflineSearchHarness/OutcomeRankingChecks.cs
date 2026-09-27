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
            quality with { ProjectedBattleHpLost = hp, StrategicHpDeficit = hp }, 3, groups ?? [0]);
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
        }) Reject(() => new Model().Fit([[invalid, b]]), "invalid/unknown label rejected");

        Model.Tree tree = new(2, 0.5, 0, new(-1, 0, -2), new(-1, 0, 3));
        Model.Document document = new(4, ["unused", "also-unused", "counter"],
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
        Reject(() => Model.Load(document with { Schema = 3 }), "old label schema rejected");
        Reject(() => Model.Load(document with { GameMvid = Guid.Empty }), "game version mismatch rejected");
        Reject(() => Model.Load(document with { Forest = [new(2, 0, 0)] }), "incomplete tree rejected");
        var learned = new Model();
        Check(learned.Fit([Enumerable.Range(0, 24).Select(i => Row(i, 24 - i)).ToArray()]), "small pairwise dataset fits");
        Check(learned.PredictFeaturesForTesting(new Dictionary<string, double> { ["x"] = 23 })
            > learned.PredictFeaturesForTesting(new Dictionary<string, double> { ["x"] = 0 }), "gradient learns preferred direction");
        Console.WriteLine($"Outcome ranking: {checks} assertions passed.");
        return 0;
    }
}
