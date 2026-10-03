using System.Text.Json;
using System.Text.Json.Nodes;
using CombatSolver;

internal static class Program
{
    private const string SolverId = "11111111-1111-4111-8111-111111111111";
    private const string GameId = "22222222-2222-4222-8222-222222222222";
    private const string OtherId = "33333333-3333-4333-8333-333333333333";
    private const string Skip = "LearnedNoImprovement";
    private static readonly string[] ExpectedFeatures =
    [
        "initial_hp", "maximum_hp", "card_count", "power_count", "enemy_count", "turn",
        "searchable_potions", "boss", "beam", "node_budget", "card_branches", "pile_branches",
        "hand_branches", "baseline_won", "baseline_loss", "baseline_actions", "baseline_turns",
        "baseline_expanded", "baseline_transitions", "baseline_milliseconds",
        "incumbent_loss", "incumbent_potions", "remaining_node_fraction", "remaining_time_fraction",
        "member_width_ratio", "member_second_band", "member_base_score", "dop",
        "growth_targets", "relic_target_count", "theft_policy", "forced_potion_directives",
    ];
    private static int _assertions;

    private static int Main()
    {
        (string Name, Action Check)[] checks =
        [
            ("valid trees and every leaf", ValidTrees),
            ("float32 threshold equivalence", Float32Thresholds),
            ("independent battle evidence and skip threshold", IndependentBattles),
            ("solver and game MVID fallback", VersionFallback),
            ("missing and nonfinite feature fallback", FeatureFallback),
            ("state and configuration training bounds", TrainingBounds),
            ("observation and timing extrapolation", TimingBounds),
            ("invalid schema, metadata and feature names", InvalidSchema),
            ("cycles, links, shared subtrees and unreachable nodes", InvalidTrees),
            ("nonfinite splits and invalid leaf evidence", InvalidNumbers),
            ("parsed model isolation from caller objects", ParsedModelIsolation),
            ("public feature-name schema isolation", PublicSchemaIsolation),
        ];
        int failures = 0;
        foreach ((string name, Action check) in checks)
        {
            int before = _assertions;
            try
            {
                check();
                Console.WriteLine($"PASS: {name} ({_assertions - before} assertions)");
            }
            catch (CheckFailure failure)
            {
                failures++;
                Console.Error.WriteLine($"FAIL: {name}: {failure.Message}");
            }
        }
        Console.WriteLine($"PORTFOLIO_SELECTOR_CHECKS_{(failures == 0 ? "OK" : "FAILED")} " +
            $"groups={checks.Length} passed={checks.Length - failures} failed={failures} assertions={_assertions}");
        Console.WriteLine("Contract checks only: learning to skip predicted unhelpful Beam follow-up searches " +
            "can reduce solution quality; these checks do not establish search quality or performance benefits.");
        return failures == 0 ? 0 : 1;
    }

    private static void ValidTrees()
    {
        Equal(true, ExpectedFeatures.SequenceEqual(BeamPortfolioSelector.FeatureNames), "feature order");
        BeamPortfolioSelectorDocument document = Document(SmallTree());
        double[][] probes = LeafProbes();
        string[] expected = [Skip, "Run", Skip, "Run"];
        BeamPortfolioSelector model = Parse(document);
        for (int leaf = 0; leaf < probes.Length; leaf++)
            Equal(expected[leaf], Decide(model, probes[leaf]), $"leaf {leaf}");

        // One skipping leaf at a time makes an incorrect route observable at every leaf.
        int[] leafIndices = [2, 3, 5, 6];
        foreach (int skippingLeaf in leafIndices)
        {
            BeamPortfolioSelectorNode[] nodes = SmallTree();
            foreach (int leaf in leafIndices)
                nodes[leaf] = Leaf(rate: leaf == skippingLeaf ? 0 : 1);
            model = Parse(Document(nodes));
            for (int probe = 0; probe < probes.Length; probe++)
                Equal(leafIndices[probe] == skippingLeaf ? Skip : "Run", Decide(model, probes[probe]),
                    $"only node {skippingLeaf} skips, probe {probe}");
        }

        Equal(Skip, Decide(Parse(Document()), Values()), "single-leaf tree");
        string camelCase = JsonSerializer.Serialize(document,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Equal(Skip, Decide(BeamPortfolioSelector.Parse(camelCase), probes[0]), "camel-case JSON properties");
        BeamPortfolioSelectorNode[] maximumTree = TreeWithLeaves(128);
        Equal(255, maximumTree.Length, "maximum valid node count");
        Equal(Skip, Decide(Parse(Document(maximumTree)), Values()), "255-node tree");
        Reject(JsonSerializer.Serialize(Document(TreeWithLeaves(129))), "257-node tree exceeds limit");
    }

    private static void Float32Thresholds()
    {
        double halfUlp = Math.ScaleB(1d, -24);
        double quarterUlp = Math.ScaleB(1d, -25);
        double nextFloat = 1d + Math.ScaleB(1d, -23);
        (string Name, double Value, double Threshold, double Rounded, string Decision)[] cases =
        [
            ("equal threshold goes left", 1, 1, 1, Skip),
            ("double above threshold rounds left", 1 + quarterUlp, 1, 1, Skip),
            ("midpoint rounds to even", 1 + halfUlp, 1, 1, Skip),
            ("next float goes right", nextFloat, 1, nextFloat, "Run"),
            ("double below threshold rounds right", 1 - quarterUlp, 1 - halfUlp / 4, 1, "Run"),
            ("threshold retains double precision", nextFloat, 1 + halfUlp * 1.5, nextFloat, "Run"),
            ("non-float threshold left", 1 + halfUlp / 2, 1 + halfUlp, 1, Skip),
            ("non-float threshold right", 1 + halfUlp * 1.5, 1 + halfUlp, nextFloat, "Run"),
            ("large integer rounds left", 16_777_217, 16_777_216, 16_777_216, Skip),
            ("next large float goes right", 16_777_218, 16_777_216, 16_777_218, "Run"),
        ];
        foreach (var test in cases)
        {
            Equal(test.Rounded, (double)(float)test.Value, $"{test.Name}: fixture rounding");
            BeamPortfolioSelector model = Parse(Document(
                [Split("beam", test.Threshold, 1, 2), Leaf(), Leaf(rate: 1)]));
            double[] values = Values();
            values[Feature("beam")] = test.Value;
            Equal(test.Decision, Decide(model, values), test.Name);
        }
    }

    private static void IndependentBattles()
    {
        foreach (int samples in new[] { 2, 100_000 })
        {
            BeamPortfolioSelector model = Parse(Document([Leaf(samples: samples, battles: 2)]));
            Equal("Run", Decide(model, Values()), $"{samples} samples from only two battles");
        }
        foreach (int battles in new[] { 3, 4 })
            Equal(Skip, Decide(Parse(Document([Leaf(samples: battles, battles: battles)])), Values()),
                $"{battles} independent battles meet minimum");
        Equal("Run", Decide(Parse(Document([Leaf(battles: 3)]) with { MinimumBattles = 4 }), Values()),
            "model-specific battle minimum");
        foreach (double rate in new[] { 0d, .125, Math.BitIncrement(.125), 1d })
            Equal(rate <= .125 ? Skip : "Run", Decide(Parse(Document([Leaf(rate: rate)])), Values()),
                $"rate {rate:R} at skip threshold .125");
        foreach (double threshold in new[] { 0d, 1d })
            Equal(Skip, Decide(Parse(Document([Leaf(rate: threshold)]) with { SkipThreshold = threshold }), Values()),
                $"inclusive skip threshold {threshold}");
        Equal(Skip, Decide(Parse(Document([Leaf(samples: 2, battles: 2)]) with { MinimumBattles = 2 }), Values()),
            "minimum supported two independent battles");
    }

    private static void VersionFallback()
    {
        BeamPortfolioSelector model = Parse(Document());
        Equal(SolverId, model.SolverAssemblyId, "stored solver MVID");
        Equal(GameId, model.GameAssemblyId, "stored game MVID");
        Equal(Skip, Decide(model, Values()), "matching MVIDs");
        foreach ((string solver, string game) in new[]
        {
            (OtherId, GameId), (SolverId, OtherId), (OtherId, OtherId), ("", GameId), (SolverId, ""),
        })
            Equal("VersionMismatch", model.Decide(Values(), solver, game), $"MVIDs {solver}/{game}");
        foreach (BeamPortfolioSelectorDocument document in new[]
        {
            Document() with { SolverAssemblyId = OtherId },
            Document() with { GameAssemblyId = OtherId },
        })
            Equal("VersionMismatch", Decide(Parse(document), Values()), "model MVID differs from caller");
    }

    private static void FeatureFallback()
    {
        BeamPortfolioSelector model = Parse(Document());
        foreach (double[] values in new[] { Array.Empty<double>(), Values()[..^1], Values().Append(0d).ToArray() })
            Equal("FeatureMismatch", Decide(model, values), $"feature count {values.Length}");
        Equal("FeatureMismatch", model.Decide(default, SolverId, GameId), "default feature span");
        for (int index = 0; index < ExpectedFeatures.Length; index++)
        {
            foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                double[] values = Values();
                values[index] = invalid;
                Equal("InvalidFeature", Decide(model, values), $"{ExpectedFeatures[index]}={invalid}");
            }
        }
    }

    private static void TrainingBounds()
    {
        string[] guarded =
        [
            "initial_hp", "maximum_hp", "card_count", "power_count", "enemy_count", "turn",
            "searchable_potions", "boss", "beam", "node_budget", "card_branches", "pile_branches",
            "hand_branches", "member_width_ratio", "member_second_band", "member_base_score", "dop",
            "growth_targets", "relic_target_count", "theft_policy", "forced_potion_directives",
        ];
        BeamPortfolioSelectorDocument document = Document();
        BeamPortfolioSelector model = Parse(document);
        foreach (string name in guarded)
        {
            int index = Feature(name);
            foreach (double boundary in new[] { document.Minimum[index], document.Maximum[index] })
            {
                double[] values = Values();
                values[index] = boundary;
                Equal(Skip, Decide(model, values), $"{name}: inclusive boundary {boundary:R}");
            }
            foreach (double outside in new[]
            {
                Math.BitDecrement(document.Minimum[index]), Math.BitIncrement(document.Maximum[index]),
            })
            {
                double[] values = Values();
                values[index] = outside;
                Equal("OutsideTrainingRange", Decide(model, values), $"{name}: outside {outside:R}");
            }
        }
        int beam = Feature("beam");
        document.Minimum[beam] = document.Maximum[beam] = Values()[beam];
        model = Parse(document);
        Equal(Skip, Decide(model, Values()), "zero-width training interval");
        double[] offPoint = Values();
        offPoint[beam] = Math.BitIncrement(offPoint[beam]);
        Equal("OutsideTrainingRange", Decide(model, offPoint), "outside zero-width interval before float32 rounding");
    }

    private static void TimingBounds()
    {
        string[] observations =
        [
            "baseline_won", "baseline_loss", "baseline_actions", "baseline_turns", "baseline_expanded",
            "baseline_transitions", "baseline_milliseconds", "incumbent_loss", "incumbent_potions",
            "remaining_node_fraction", "remaining_time_fraction",
        ];
        BeamPortfolioSelectorDocument document = Document();
        BeamPortfolioSelector model = Parse(document);
        foreach (string name in observations)
        {
            foreach (double outside in new[] { -double.MaxValue, double.MaxValue })
            {
                double[] values = Values();
                values[Feature(name)] = outside;
                Equal(Skip, Decide(model, values), $"finite observation {name}={outside:R}");
            }
        }
        document = Document([Split("baseline_milliseconds", 5, 1, 2), Leaf(), Leaf(rate: 1)]);
        int elapsed = Feature("baseline_milliseconds");
        document.Minimum[elapsed] = 1;
        document.Maximum[elapsed] = 9;
        model = Parse(document);
        foreach ((double time, string expected) in new[] { (.5, Skip), (100d, "Run") })
        {
            double[] values = Values();
            values[elapsed] = time;
            Equal(expected, Decide(model, values), $"timing split extrapolates at {time}");
        }
    }

    private static void InvalidSchema()
    {
        foreach (string json in new[] { "", "null", "{}", "[]", "{", "{\"SchemaVersion\":\"one\"}" })
            Reject(json, $"invalid document {json}");
        foreach (int schema in new[] { -1, 0, 2, int.MaxValue })
            RejectMutation($"schema {schema}", d => d["SchemaVersion"] = schema);
        foreach (string field in new[]
        {
            "SchemaVersion", "SolverAssemblyId", "GameAssemblyId", "FeatureNames", "Minimum", "Maximum",
            "MinimumBattles", "Nodes",
        })
            RejectMutation($"missing {field}", d => d.Remove(field));
        foreach (string field in new[] { "FeatureNames", "Minimum", "Maximum", "Nodes" })
            RejectMutation($"null {field}", d => d[field] = null);
        foreach (string field in new[] { "SolverAssemblyId", "GameAssemblyId" })
        {
            RejectMutation($"malformed {field}", d => d[field] = "not-an-mvid");
            RejectMutation($"null {field}", d => d[field] = null);
        }
        foreach (string field in new[] { "FeatureNames", "Minimum", "Maximum" })
        {
            RejectMutation($"short {field}", d => d[field]!.AsArray().RemoveAt(0));
            RejectMutation($"long {field}", d => d[field]!.AsArray().Add(d[field]![0]!.DeepClone()));
        }
        RejectMutation("different feature name", d => d["FeatureNames"]![0] = "renamed_initial_hp");
        RejectMutation("feature name casing", d => d["FeatureNames"]![0] = "INITIAL_HP");
        RejectMutation("null feature name", d => d["FeatureNames"]![0] = null);
        RejectMutation("duplicate feature name", d => d["FeatureNames"]![1] = ExpectedFeatures[0]);
        RejectMutation("reordered features", d =>
        {
            d["FeatureNames"]![0] = ExpectedFeatures[1];
            d["FeatureNames"]![1] = ExpectedFeatures[0];
        });
        RejectMutation("inverted feature bounds", d => d["Minimum"]![0] = 100_000_001d);
        foreach (double threshold in new[] { -.001, 1.001 })
            RejectMutation($"invalid skip threshold {threshold}", d => d["SkipThreshold"] = threshold);
        foreach (int battles in new[] { -1, 0, 1 })
            RejectMutation($"invalid minimum battles {battles}", d => d["MinimumBattles"] = battles);
        RejectMutation("empty tree", d => d["Nodes"] = new JsonArray());
        RejectMutation("null node", d => d["Nodes"]![2] = null);
    }

    private static void InvalidTrees()
    {
        RejectMutation("self-cycle", d => d["Nodes"]![0]!["Left"] = 0);
        RejectMutation("ancestor cycle", d => d["Nodes"]![1]!["Left"] = 0);
        RejectMutation("deep cycle", d => d["Nodes"]![4]!["Right"] = 4);
        foreach (string child in new[] { "Left", "Right" })
        {
            foreach (int index in new[] { -1, 7, int.MaxValue })
                RejectMutation($"{child} out of bounds: {index}", d => d["Nodes"]![0]![child] = index);
            RejectMutation($"leaf has {child} child", d => d["Nodes"]![2]![child] = 3);
        }
        RejectMutation("same subtree on both edges", d => d["Nodes"]![0]!["Right"] = 1);
        RejectMutation("shared leaf with different parents", d => d["Nodes"]![4]!["Left"] = 2);
        RejectMutation("shared interior subtree", d => d["Nodes"]![4]!["Left"] = 1);
        RejectMutation("unreachable appended node", d => d["Nodes"]!.AsArray().Add(JsonSerializer.SerializeToNode(Leaf())));
        RejectMutation("unreachable interior nodes", d => d["Nodes"]![0] = JsonSerializer.SerializeToNode(Leaf()));
        foreach (int feature in new[] { -2, ExpectedFeatures.Length, int.MaxValue })
            RejectMutation($"split feature {feature} out of bounds", d => d["Nodes"]![0]!["Feature"] = feature);
    }

    private static void InvalidNumbers()
    {
        // Overflow literals reach double validation; quoted names test strict JSON number handling.
        foreach (string token in new[] { "1e999", "-1e999", "\"NaN\"", "\"Infinity\"", "\"-Infinity\"" })
        {
            RejectMutation($"nonfinite split {token}", d => d["Nodes"]![0]!["Threshold"] = JsonNode.Parse(token));
            RejectMutation($"nonfinite leaf rate {token}", d => d["Nodes"]![2]!["ImprovementRate"] = JsonNode.Parse(token));
            RejectMutation($"nonfinite skip threshold {token}", d => d["SkipThreshold"] = JsonNode.Parse(token));
            foreach (string bound in new[] { "Minimum", "Maximum" })
                RejectMutation($"nonfinite {bound} {token}", d => d[bound]![0] = JsonNode.Parse(token));
        }
        foreach (int node in new[] { 0, 2 })
        {
            foreach (double rate in new[] { -.001, 1.001 })
                RejectMutation($"node {node} invalid improvement rate {rate}", d => d["Nodes"]![node]!["ImprovementRate"] = rate);
            foreach (string count in new[] { "Samples", "Battles" })
            {
                foreach (int value in new[] { -1, 0 })
                    RejectMutation($"node {node} invalid {count} {value}", d => d["Nodes"]![node]![count] = value);
                foreach (string token in new[] { "1.5", "2147483648", "null" })
                    RejectMutation($"node {node} non-integer {count} {token}", d => d["Nodes"]![node]![count] = JsonNode.Parse(token));
            }
            RejectMutation($"node {node} battles exceed samples", d => d["Nodes"]![node]!["Battles"] = 21);
        }
    }

    private static void ParsedModelIsolation()
    {
        BeamPortfolioSelectorDocument document = Document(SmallTree());
        string json = JsonSerializer.Serialize(document);
        BeamPortfolioSelector model = BeamPortfolioSelector.Parse(json);
        string modelId = model.ModelId;
        Equal(modelId, BeamPortfolioSelector.Parse(json).ModelId, "stable model identity");
        double[][] probes = LeafProbes();
        string[] expected = [Skip, "Run", Skip, "Run"];
        for (int index = 0; index < probes.Length; index++)
            Equal(expected[index], Decide(model, probes[index]), $"before caller mutation, leaf {index}");

        document.FeatureNames[0] = "caller_modified_name";
        Array.Fill(document.Minimum, double.MaxValue);
        Array.Fill(document.Maximum, double.MinValue);
        Array.Fill(document.Nodes, Leaf(rate: 1));
        Array.Fill(probes[0], double.NaN);
        BeamPortfolioObservation observation = new(Values(), Skip, false, null, null, 0, null);
        Array.Fill(observation.Features, double.PositiveInfinity);

        probes = LeafProbes();
        for (int index = 0; index < probes.Length; index++)
            Equal(expected[index], Decide(model, probes[index]), $"after caller mutation, leaf {index}");
        Equal(modelId, model.ModelId, "model identity after caller mutation");
        Equal(SolverId, model.SolverAssemblyId, "solver MVID after caller mutation");
        Equal(GameId, model.GameAssemblyId, "game MVID after caller mutation");
        double[] outside = Values();
        outside[0] = -1;
        Equal("OutsideTrainingRange", Decide(model, outside), "private bounds survive caller mutation");
    }

    private static void PublicSchemaIsolation()
    {
        BeamPortfolioSelectorDocument document = Document();
        string originalJson = JsonSerializer.Serialize(document);
        BeamPortfolioSelector model = BeamPortfolioSelector.Parse(originalJson);
        string modelId = model.ModelId;
        IReadOnlyList<string> exposed = BeamPortfolioSelector.FeatureNames;
        string[]? mutable = exposed as string[];
        if (mutable != null)
            mutable[0] = "external_initial_hp";
        document.FeatureNames[0] = "external_initial_hp";
        bool originalAccepted = Accepts(originalJson);
        bool renamedAccepted = Accepts(JsonSerializer.Serialize(document));
        Equal(Skip, Decide(model, Values()), "parsed decision after public schema mutation");
        Equal(modelId, model.ModelId, "parsed identity after public schema mutation");
        Equal((true, false), (originalAccepted, renamedAccepted),
            "public FeatureNames mutation must preserve acceptance of canonical JSON and reject renamed JSON; " +
            "parsed-instance decision and identity remained unchanged");
        Equal(true, mutable == null, "exposed feature names must not be a mutable array instance");
    }

    private static BeamPortfolioSelectorDocument Document(BeamPortfolioSelectorNode[]? nodes = null) => new(
        1, SolverId, GameId, ExpectedFeatures.ToArray(), new double[ExpectedFeatures.Length],
        Enumerable.Repeat(100_000_000d, ExpectedFeatures.Length).ToArray(), .125, 3, nodes ?? [Leaf()]);

    private static BeamPortfolioSelectorNode Leaf(double rate = 0, int samples = 20, int battles = 3) =>
        new(-1, 0, -1, -1, rate, samples, battles);

    private static BeamPortfolioSelectorNode Split(string feature, double threshold, int left, int right) =>
        new(Feature(feature), threshold, left, right, 0, 20, 3);

    private static BeamPortfolioSelectorNode[] SmallTree() =>
    [
        Split("beam", 64, 1, 4), Split("enemy_count", 2, 2, 3), Leaf(), Leaf(rate: .75),
        Split("member_width_ratio", 1, 5, 6), Leaf(rate: .125), Leaf(battles: 2),
    ];

    private static BeamPortfolioSelectorNode[] TreeWithLeaves(int leaves)
    {
        List<BeamPortfolioSelectorNode> nodes = [];
        Add(leaves);
        return nodes.ToArray();

        int Add(int count)
        {
            int index = nodes.Count;
            nodes.Add(Leaf());
            if (count > 1)
            {
                int left = Add(count / 2);
                int right = Add(count - count / 2);
                nodes[index] = Split("beam", 64, left, right);
            }
            return index;
        }
    }

    private static double[] Values() =>
    [
        50, 80, 20, 2, 2, 3, 1, 0, 64, 10_000, 16, 16, 16,
        1, 4, 8, 3, 200, 500, 5, 4, 0, .5, .5, 1, 0, 0, 4,
        0, 0, 0, 0,
    ];

    private static double[][] LeafProbes()
    {
        double[][] values = [Values(), Values(), Values(), Values()];
        values[1][Feature("enemy_count")] = 3;
        values[2][Feature("beam")] = 128;
        values[3][Feature("beam")] = 128;
        values[3][Feature("member_width_ratio")] = 2;
        return values;
    }

    private static int Feature(string name)
    {
        int index = Array.IndexOf(ExpectedFeatures, name);
        return index >= 0 ? index : throw new CheckFailure($"Unknown fixture feature {name}.");
    }

    private static BeamPortfolioSelector Parse(BeamPortfolioSelectorDocument document) =>
        BeamPortfolioSelector.Parse(JsonSerializer.Serialize(document));

    private static string Decide(BeamPortfolioSelector model, double[] values) => model.Decide(values, SolverId, GameId);

    private static void RejectMutation(string context, Action<JsonObject> mutate)
    {
        JsonObject json = JsonSerializer.SerializeToNode(Document(SmallTree()))!.AsObject();
        mutate(json);
        Reject(json.ToJsonString(), context);
    }

    private static bool Accepts(string json)
    {
        try
        {
            _ = BeamPortfolioSelector.Parse(json);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Reject(string json, string context) => Equal(false, Accepts(json), $"must reject {context}");

    private static void Equal<T>(T expected, T actual, string context)
    {
        _assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new CheckFailure($"{context}: expected {expected}, observed {actual}.");
    }

    private sealed class CheckFailure(string message) : Exception(message);
}
