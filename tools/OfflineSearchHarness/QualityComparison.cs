using System.Text.Json;
using CombatSolver;

namespace OfflineSearchHarness;

/// <summary>Read saved scalar outcomes and invoke the actual coordinator comparator.</summary>
internal static class QualityComparison
{
    private sealed record Pair(string Id, string Candidate, string Baseline);

    internal static int Run(string input, string output)
    {
        Pair[] pairs = JsonSerializer.Deserialize<Pair[]>(File.ReadAllText(input), UnattendedTestFiles.JsonOptions)
            ?? throw new InvalidDataException("Empty quality comparison batch.");
        var results = pairs.Select(pair =>
        {
            SolverInterimResult candidate = Read(pair.Candidate);
            SolverInterimResult baseline = Read(pair.Baseline);
            if (candidate.TheftPolicy != baseline.TheftPolicy)
                throw new InvalidDataException($"Different theft policies: {pair.Id}");
            bool better = CombatSearchCoordinator.IsBetterPotionPolicyResult(candidate.TheftPolicy, candidate, baseline);
            bool worse = CombatSearchCoordinator.IsBetterPotionPolicyResult(candidate.TheftPolicy, baseline, candidate);
            if (better && worse)
                throw new InvalidOperationException($"Non-antisymmetric outcome comparison: {pair.Id}");
            return new { pair.Id, comparison = better ? -1 : worse ? 1 : 0,
                materialComparison = CompareMaterial(candidate, baseline, includeEndingTurn: true),
                coreComparison = CompareMaterial(candidate, baseline, includeEndingTurn: false), candidate, baseline };
        }).ToArray();
        using FileStream stream = new(output, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, results, UnattendedTestFiles.JsonOptions);
        Console.WriteLine($"Compared {results.Length} saved pairs with the production coordinator.");
        return 0;
    }

    internal static int CompareMaterial(SolverInterimResult candidate, SolverInterimResult baseline,
        bool includeEndingTurn)
    {
        if (candidate.TheftPolicy != baseline.TheftPolicy)
            throw new InvalidDataException("Different theft policies in material comparison.");
        SolverInterimResult Material(SolverInterimResult value) => value with
        {
            Score = 0,
            // Only copies used for the supplementary metric are normalized.
            // Keep the authoritative win/resource ordering and original records.
            CombatEndedTurn = includeEndingTurn ? value.CombatEndedTurn : value.Won ? 1 : null,
        };
        var left = Material(candidate);
        var right = Material(baseline);
        bool better = CombatSearchCoordinator.IsBetterPotionPolicyResult(candidate.TheftPolicy, left, right);
        bool worse = CombatSearchCoordinator.IsBetterPotionPolicyResult(candidate.TheftPolicy, right, left);
        if (better && worse) throw new InvalidOperationException("Non-antisymmetric material comparison.");
        return better ? -1 : worse ? 1 : 0;
    }

    private static SolverInterimResult Read(string path)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("quality").Deserialize<SolverInterimResult>(UnattendedTestFiles.JsonOptions)
            ?? throw new InvalidDataException($"Empty quality: {path}");
    }
}
