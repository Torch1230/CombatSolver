using CombatSolver;
int checks = 0;
void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks++; }
SearchObjectiveOutcome Outcome(SearchObjective mode, int hp, int growth, int loss = 0) => new(
    SearchObjectivePolicy.Default with { Mode = mode }, new(0, 0, growth), 0, 0, 0, 0, 0, 0, loss, hp);
Check(SearchObjectivePolicy.Default.MinimumEndingHp == 30, "Default reserve must be 30 HP");
foreach (var mode in new[] { SearchObjective.PermanentGrowth, SearchObjective.NetResources })
{
    var safe = Outcome(mode, 30, 1);
    var risky = Outcome(mode, 29, 100) with { GoldGain = 1000 };
    Check(safe.MeetsLimits && !risky.MeetsLimits && safe.CompareTo(risky) < 0,
        "Rewards must not override the HP reserve");
    Check(!Outcome(mode, 50, 100, 11).MeetsLimits, "Loss cap still applies");
    Check((risky with { Policy = risky.Policy with { MinimumEndingHp = 20 } }).MeetsLimits,
        "Explicit custom reserves remain supported");
}
for (int gain = 1; gain <= 100; gain++)
{
    var safe = Outcome(SearchObjective.Balanced, 50, 0);
    var risky = Outcome(SearchObjective.Balanced, 49, gain);
    Check(SolverInterimResultOrdering.ComparePrimaryQuality(true, 0, 2, true, 1, 2,
        candidateGrowthRewardCount: 0, currentGrowthRewardCount: gain,
        candidateObjective: safe, currentObjective: risky) < 0,
        "Balanced must prefer lower strategic loss over more growth");
    Check(SolverInterimResultOrdering.ComparePrimaryQuality(true, 0, 2, true, 0, 2,
        candidateGrowthRewardCount: gain, currentGrowthRewardCount: 0,
        candidateObjective: safe, currentObjective: safe) < 0,
        "Balanced must prefer free growth on an HP tie");
}
Check(Outcome(SearchObjective.Balanced, 1, 0).MeetsLimits,
    "Reward-only reserve must not gate balanced mode");
Console.WriteLine($"SEARCH_OBJECTIVE_CHECKS_OK checks={checks}");
