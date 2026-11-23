using System.Text.Json;
using CombatSolver;
using CombatSolver.HotSearch;

namespace OfflineSearchHarness;

/// <summary>Read saved scalar outcomes and invoke the HotSearch route quality comparator.</summary>
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
            HotRouteKey candKey = ToRouteKey(candidate);
            HotRouteKey baseKey = ToRouteKey(baseline);
            bool better = IsBetter(in candKey, in baseKey, candidate.TheftPolicy);
            bool worse = IsBetter(in baseKey, in candKey, candidate.TheftPolicy);
            if (better && worse)
                throw new InvalidOperationException($"Non-antisymmetric outcome comparison: {pair.Id}");
            HotRouteKey materialCand = candKey with { Score = 0 };
            HotRouteKey materialBase = baseKey with { Score = 0 };
            bool materialBetter = IsBetter(in materialCand, in materialBase, candidate.TheftPolicy);
            bool materialWorse = IsBetter(in materialBase, in materialCand, candidate.TheftPolicy);
            return new
            {
                pair.Id,
                comparison = better ? -1 : worse ? 1 : 0,
                materialComparison = materialBetter ? -1 : materialWorse ? 1 : 0,
                candidate,
                baseline,
            };
        }).ToArray();
        using FileStream stream = new(output, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, results, UnattendedTestFiles.JsonOptions);
        Console.WriteLine($"Compared {results.Length} saved pairs with HotRouteQuality.");
        return 0;
    }

    private static bool IsBetter(in HotRouteKey a, in HotRouteKey b, SolverTheftPolicy? theftPolicy)
    {
        HotSearchPolicy defaultPolicy = default;
        if (theftPolicy == SolverTheftPolicy.PreserveResources)
        {
            int cmp = b.Won.CompareTo(a.Won);
            if (cmp != 0)
                return cmp < 0;
            cmp = b.Survives.CompareTo(a.Survives);
            if (cmp != 0)
                return cmp < 0;
            cmp = a.DeathSaveUseCount.CompareTo(b.DeathSaveUseCount);
            if (cmp != 0)
                return cmp < 0;
            cmp = a.OutstandingStolen.CompareTo(b.OutstandingStolen);
            if (cmp != 0)
                return cmp < 0;
            cmp = a.ForfeitsGold.CompareTo(b.ForfeitsGold);
            if (cmp != 0)
                return cmp < 0;
        }
        return HotRouteQuality.IsBetter(in a, in b, in defaultPolicy);
    }

    private static HotRouteKey ToRouteKey(SolverInterimResult r) => new(
        Won: r.Won,
        Survives: r.Survives,
        DeathSaveUseCount: (byte)Math.Clamp(r.DeathSaveUseCount, 0, byte.MaxValue),
        OutstandingStolen: r.OutstandingStolenResource,
        ForfeitsGold: r.ForfeitsEncounterGold,
        StrategicHpDeficit: r.StrategicHpDeficit,
        OptionalPotionStrategicCost: r.PotionStrategicCost,
        RawPotionStrategicCost: r.PotionStrategicCost,
        GrowthHpCredit: r.GrowthHpCredit,
        GrowthRewardCount: r.GrowthRewardCount,
        CombatEndedTurn: r.CombatEndedTurn ?? int.MaxValue,
        ProjectedBattleHpLost: r.ProjectedBattleHpLost,
        ProjectedBattlePotionCount: r.ProjectedBattlePotionCount,
        ExplicitPotionCount: r.ProjectedBattlePotionCount,
        PlayerHp: 0,
        EnemyHp: r.EnemyHp,
        Score: (long)Math.Clamp(r.Score, long.MinValue, long.MaxValue),
        PotionMask: 0,
        AmbergrisCount: 0);

    private static SolverInterimResult Read(string path)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("quality").Deserialize<SolverInterimResult>(UnattendedTestFiles.JsonOptions)
            ?? throw new InvalidDataException($"Empty quality: {path}");
    }
}
