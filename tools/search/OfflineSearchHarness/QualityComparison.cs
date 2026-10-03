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
            // Separate policy outcomes from the final legacy-score tie breaker.
            SolverInterimResult materialCandidate = candidate with { Score = 0 };
            SolverInterimResult materialBaseline = baseline with { Score = 0 };
            bool materialBetter = CombatSearchCoordinator.IsBetterPotionPolicyResult(
                candidate.TheftPolicy, materialCandidate, materialBaseline);
            bool materialWorse = CombatSearchCoordinator.IsBetterPotionPolicyResult(
                candidate.TheftPolicy, materialBaseline, materialCandidate);
            return new { pair.Id, comparison = better ? -1 : worse ? 1 : 0,
                materialComparison = materialBetter ? -1 : materialWorse ? 1 : 0, candidate, baseline };
        }).ToArray();
        using FileStream stream = new(output, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, results, UnattendedTestFiles.JsonOptions);
        Console.WriteLine($"Compared {results.Length} saved pairs with the production coordinator.");
        return 0;
    }

    private static SolverInterimResult Read(string path)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("quality").Deserialize<SolverInterimResult>(UnattendedTestFiles.JsonOptions)
            ?? throw new InvalidDataException($"Empty quality: {path}");
    }
}
