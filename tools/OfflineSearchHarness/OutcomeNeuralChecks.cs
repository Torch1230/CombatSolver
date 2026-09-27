using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Players;
using Model = CombatSolver.SearchOutcomeValueModel;

namespace OfflineSearchHarness;

internal static class OutcomeNeuralChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException("Outcome neural: " + message);
            checks++;
        }
        void Reject(Model.Document document)
        {
            bool rejected = false;
            try { Model.Load(document); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "invalid neural/schema contract rejected");
        }
        Model.NeuralTerm term = new([[0, 0], [1, 2], [3, 4]], [.1, -.2], [.5, -.7]);
        Model.Document document = new(Model.NeuralSchema, ["unused", "a", "b"],
            typeof(Player).Assembly.ManifestModule.ModuleVersionId, [new(-1, 0, 10)], [0, 2, 3], Neural: term);
        Dictionary<string, double> input = new() { ["a"] = .125, ["b"] = -.25 };
        double expected = 1 + 2 * .125 - 3 * .25
            + Math.Tanh(.1 + .125 - .75) * .5 + Math.Tanh(-.2 + .25 - 1) * -.7;
        Model model = Model.Load(document);
        Check(Math.Abs(model.PredictFeaturesForTesting(input) - expected) < 1e-14,
            "float observations, signed hidden inputs and residual terms compose");
        Check(model.ExportModel().FeatureNames.SequenceEqual(["a", "b"])
            && model.ExportModel().Neural!.InputWeights.Length == 2, "neural columns share compaction");
        Check(Math.Abs(model.PredictFeaturesForTesting(new Dictionary<string, double>())
            - (1 + Math.Tanh(.1) * .5 + Math.Tanh(-.2) * -.7)) < 1e-14, "absent features preserve learned biases");
        Model roundtrip = Model.Load(JsonSerializer.Deserialize<Model.Document>(JsonSerializer.Serialize(model.ExportModel()))!);
        Check(roundtrip.PredictFeaturesForTesting(input) == model.PredictFeaturesForTesting(input), "neural format roundtrips");
        Model linear = Model.Load(model.ExportLinearModel());
        Check(linear.ExportModel().Schema == Model.Schema && linear.ExportModel().Neural == null
            && linear.PredictFeaturesForTesting(input) == -.5, "linear export removes every nonlinear term");
        Check(!JsonSerializer.Serialize(linear.ExportModel()).Contains("Neural", StringComparison.Ordinal),
            "legacy serialization omits the absent optional neural field");
        term.InputWeights[1][0] = 100; term.HiddenBias[0] = 100; term.OutputWeights[0] = 100;
        Model.NeuralTerm exported = model.ExportModel().Neural!;
        exported.InputWeights[0][0] = 200; exported.HiddenBias[0] = 200; exported.OutputWeights[0] = 200;
        Check(Math.Abs(model.PredictFeaturesForTesting(input) - expected) < 1e-14,
            "input and export arrays cannot mutate the compiled predictor");
        var constant = Model.Load(document with { LinearWeights = null,
            Neural = new([[0], [0], [0]], [.25], [2]) });
        Check(constant.ExportModel().FeatureNames.Length == 0
            && Math.Abs(Model.Load(constant.ExportModel()).PredictFeaturesForTesting(input)
                - (1 + 2 * Math.Tanh(.25))) < 1e-14, "constant residual survives empty-column compaction");
        Reject(document with { Schema = Model.Schema });
        Reject(document with { Schema = Model.LegacySchema });
        Reject(document with { Neural = null });
        Reject(document with { FactorWeights = [[0], [0], [0]] });
        foreach (Model.NeuralTerm invalid in new Model.NeuralTerm[]
        {
            new(null!, [0], [1]), new([[1]], [0], [1]),
            new([[], [], []], [], []), new([[1], [1, 2], [1]], [0], [1]),
            new([[1], null!, [1]], [0], [1]), new([[1], [1], [1]], [double.NaN], [1]),
            new([[1], [1], [1]], [0], [double.PositiveInfinity]), new([[1], [double.NaN], [1]], [0], [1]),
            new([[1], [1], [1]], [0], []),
            new([new double[33], new double[33], new double[33]], new double[33], new double[33]),
        }) Reject(document with { Neural = invalid });
        return checks;
    }
}
