using CombatSolver;

namespace OfflineSearchHarness;

internal static class BossTempoChecks
{
    internal static int Run()
    {
        int assertions = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
        var profile = new SolverSearchProfile(135, 500_000, 48, 30, 40, 300_000);
        var extra = BossTempoSearchOptions.AdditionalBudget(profile, 300_000);
        Check(extra.MaxExpandedNodes == 100_000 && extra.SoftTimeBudgetMilliseconds == 60_000,
            "configured boss allowance is one fifth of the original budget");
        Check(profile.MaxExpandedNodes == 500_000 && profile.SoftTimeBudgetMilliseconds == 300_000,
            "baseline profile retains its full allowance");
        Check(BossTempoSearchOptions.AdditionalBudget(profile, 5_000).SoftTimeBudgetMilliseconds == 1_000,
            "explicit request time controls the added time allowance");
        double score = 55 * SolverWeights.Hp;
        double hurt = 54 * SolverWeights.Hp - SolverWeights.Hp;
        Check(BossTempoSearchOptions.RankBase(score, 0, 0, 55, false)
            - BossTempoSearchOptions.RankBase(hurt, 1, 0, 54, false) == SolverWeights.Hp,
            "actual loss has a single HP price");
        Check(BossTempoSearchOptions.RankBase(hurt + SolverWeights.SoldHpPenalty, 1, 1, 54, false)
            == BossTempoSearchOptions.RankBase(hurt, 1, 0, 54, false),
            "sold HP is a subset of damage rather than a second loss");
        Check(BossTempoSearchOptions.RankBase(SolverWeights.DeathPenalty, 0, 0, -7, false)
            == -7 * SolverWeights.Hp, "lethal stand-pat intent is continuously ranked while alive");
        Check(BossTempoSearchOptions.RankBase(SolverWeights.DeathPenalty, 55, 55, 0, true)
            == SolverWeights.DeathPenalty, "actual death retains its terminal cost");
        Check(!CombatBeamSolver.ShouldPruneByPrimaryIncumbent(12, 20, new(12, 12),
            allowTurnTieBound: false), "equal-loss late routes remain eligible");
        Check(CombatBeamSolver.ShouldPruneByPrimaryIncumbent(13, 2, new(12, 12),
            allowTurnTieBound: false), "strictly worse certified lower bound is rejected");
        PrimaryIncumbentTable table = new();
        table.Tighten(0, 0, new(12, 12));
        Check(!table.TryGet(0, 1, out _), "potion tiers require independent witnesses");
        Check(table.Tighten(0, 0, new(10, 15)), "better complete loss tightens the witness");
        Check(!table.Tighten(0, 0, new(11, 1)), "earlier finish cannot loosen the loss bound");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { status = "passed", assertions }));
        return 0;
    }
}
