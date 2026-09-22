using System.Reflection;
using System.Text.Json;
using CombatSolver;
using MegaCrit.Sts2.Core.Models;

namespace OfflineSearchHarness;

/// <summary>Pure model contracts and Python/C# feature parity; no game or search startup.</summary>
internal static class RankingChecks
{
    private sealed record Sample(JsonElement Observation, double[] Expected);
    private static ContextualRankingModel.Document Schema() => new(1,
        typeof(ContextualRankingModel).Assembly.ManifestModule.ModuleVersionId.ToString(),
        typeof(CardModel).Assembly.ManifestModule.ModuleVersionId.ToString(),
        ContextualRankingModel.FeatureNames.ToArray(), new double[28], new double[28],
        Enumerable.Repeat(16d, 28).ToArray(), 1, "contract-only");

    internal static int WriteSchema(string output)
    {
        using FileStream stream = new(output, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, Schema(), UnattendedTestFiles.JsonOptions);
        return 0;
    }

    internal static int Run(string input, string output)
    {
        Sample[] samples = JsonSerializer.Deserialize<Sample[]>(File.ReadAllText(input), UnattendedTestFiles.JsonOptions)
            ?? throw new InvalidDataException("Missing feature samples.");
        int checks = 0;
        foreach (Sample sample in samples)
        {
            SearchNode node = Node(sample.Observation);
            double[] actual = new double[28];
            ContextualRankingModel.Capture(node, actual);
            Require(sample.Expected.Length == actual.Length, "Feature dimensions");
            for (int i = 0; i < actual.Length; i++)
                Require(Math.Abs(actual[i] - sample.Expected[i]) < 1e-12, $"Feature {i}");
        }
        var schema = Schema();
        SearchNode probe = Node(samples.First().Observation);
        var zero = Parse(schema);
        Require(zero.Adjustment(probe) == 0, "Zero correction");
        double[] weights = Enumerable.Repeat(100d, 28).ToArray();
        var positive = Parse(schema with { Weights = weights });
        Require(positive.Adjustment(probe) == SolverWeights.Hp, "Positive cap");
        Require(positive.Adjustment(probe with { IsTerminal = true }) == 0, "Terminal bypass");
        var negative = Parse(schema with { Weights = weights.Select(w => -w).ToArray() });
        Require(negative.Adjustment(probe) == -SolverWeights.Hp, "Negative cap");
        var outside = Parse(schema with { Minimum = Enumerable.Repeat(8d, 28).ToArray() });
        Require(outside.Adjustment(probe) == 0, "Out-of-range fallback");
        Reject(schema with { SolverAssemblyId = "wrong" });
        Reject(schema with { GameAssemblyId = "wrong" });
        Reject(schema with { SchemaVersion = 0 });
        Reject(schema with { FeatureNames = schema.FeatureNames.Reverse().ToArray() });
        Reject(schema with { Weights = [] });
        Reject(schema with { Weights = Enumerable.Repeat(101d, 28).ToArray() });
        Reject(schema with { MaximumAdjustmentHp = 9 });
        Reject(schema with { MaximumAdjustmentHp = 0 });
        Reject(schema with { ModelId = "" });
        Reject(schema with { Maximum = Enumerable.Repeat(-1d, 28).ToArray() });
        using FileStream stream = new(output, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, new { samples = samples.Length, checks, status = "Passed" });
        Console.WriteLine($"Ranking contracts: {checks} assertions, {samples.Length} feature vectors passed.");
        return 0;

        void Require(bool ok, string name)
        {
            if (!ok) throw new InvalidOperationException("Ranking contract: " + name);
            checks++;
        }
        void Reject(ContextualRankingModel.Document document)
        {
            try { Parse(document); }
            catch (InvalidDataException) { checks++; return; }
            throw new InvalidOperationException("Invalid model was accepted.");
        }
    }

    private static ContextualRankingModel Parse(ContextualRankingModel.Document document)
        => ContextualRankingModel.Parse(JsonSerializer.Serialize(document));

    private static SearchNode Node(JsonElement observation)
    {
        // Fixture-only construction. All score inputs come from detached observations;
        // unused simulator/reference fields stay null and must never be accessed here.
        var fields = observation.GetProperty("retention").GetProperty("evaluation")
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[] { "playerHp", "playerMaxHp", "enemyHp", "turn" })
            fields[name] = observation.GetProperty(name);
        ConstructorInfo constructor = typeof(SimulationSnapshot).GetConstructors().Single();
        object?[] arguments = constructor.GetParameters().Select(p =>
            fields.TryGetValue(p.Name!, out JsonElement value) ? value.Deserialize(p.ParameterType)
                : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        var snapshot = (SimulationSnapshot)constructor.Invoke(arguments);
        return new SearchNode(null, 0, 0, 0, fields["turn"].GetInt32(), default, 0, 0,
            default, false, default, false, null, snapshot, default!);
    }
}
