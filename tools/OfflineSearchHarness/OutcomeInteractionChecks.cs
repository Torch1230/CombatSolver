using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Players;
using Model = CombatSolver.SearchOutcomeValueModel;

namespace OfflineSearchHarness;

internal static class OutcomeInteractionChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Outcome interactions: " + message);
            checks++;
        }
        void Reject(Model.Document invalid)
        {
            bool rejected = false;
            try { Model.Load(invalid); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "invalid factor/schema contract rejected");
        }
        Model.Document document = new(Model.Schema, ["unused", "a", "b"],
            typeof(Player).Assembly.ManifestModule.ModuleVersionId, [new(-1, 0, 10)],
            [0, 3, 4], [[0, 0], [1, 2], [3, 4]]);
        Dictionary<string, double> Input(double a, double b) => new() { ["a"] = a, ["b"] = b };
        var model = Model.Load(document);
        Check(model.PredictFeaturesForTesting(Input(2, 3)) == 85,
            "linear, tree and distinct-feature pair terms add exactly");
        Check(model.PredictFeaturesForTesting(Input(2, 0)) == 7, "self-square terms cancel");
        Check(model.PredictFeaturesForTesting(Input(-2, 3)) == -59, "signed observations preserve interactions");
        Check(model.PredictFeaturesForTesting(new Dictionary<string, double>()) == 1, "absent features are zeros");
        Check(model.ExportModel().FeatureNames.SequenceEqual(["a", "b"]), "all terms share compact remapping");
        var factorOnly = Model.Load(document with { LinearWeights = null });
        Check(factorOnly.ExportModel().FeatureNames.SequenceEqual(["a", "b"])
            && factorOnly.PredictFeaturesForTesting(Input(2, 3)) == 67, "factor-only columns survive compilation");
        var roundtrip = Model.Load(JsonSerializer.Deserialize<Model.Document>(JsonSerializer.Serialize(model.ExportModel()))!);
        Check(roundtrip.PredictFeaturesForTesting(Input(2, 3)) == 85, "factor model roundtrips");
        var linear = Model.Load(model.ExportLinearModel());
        Check(linear.ExportModel().FactorWeights == null
            && linear.PredictFeaturesForTesting(Input(2, 3)) == 18, "linear export removes both nonlinear terms");
        document.FactorWeights![1][0] = 100;
        model.ExportModel().FactorWeights![0][0] = 100;
        Check(model.PredictFeaturesForTesting(Input(2, 3)) == 85, "input and export factor matrices cannot mutate the model");
        var zero = Model.Load(document with { LinearWeights = null, FactorWeights = [[0], [0], [0]] });
        Check(zero.ExportModel().FeatureNames.Length == 0 && zero.ExportModel().FactorWeights == null
            && Model.Load(zero.ExportModel()).PredictFeaturesForTesting(Input(2, 3)) == 1,
            "zero factors normalize to an absent term and empty features roundtrip");
        var legacy = Model.Load(document with { Schema = Model.LegacySchema, FactorWeights = null });
        Check(legacy.PredictFeaturesForTesting(Input(2, 3)) == 19, "schema 9 linear/tree predictions are retained");
        Reject(document with { Schema = Model.LegacySchema });
        Reject(document with { Schema = 8, FactorWeights = null });
        Reject(document with { Schema = 11 });
        foreach (double[][] invalid in new double[][][]
        {
            [], [[1]], [[], [], []], [[1], [1, 2], [1]], [[1], null!, [1]],
            [[1], [double.NaN], [1]], [[1], [1], [double.PositiveInfinity]],
            [new double[17], new double[17], new double[17]],
        }) Reject(document with { FactorWeights = invalid });
        return checks;
    }
}
