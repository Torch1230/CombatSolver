using CombatSolver;
int checks = 0;
void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks++; }
SearchObjectiveOutcome Outcome(SearchObjective mode, int hp, int growth, int loss = 0) => new(
    SearchObjectivePolicy.Default with { Mode = mode }, new(0, 0, growth), 0, 0, 0, 0, 0, 0, loss, hp);
Check(SearchObjectivePolicy.Default.MinimumEndingHp == 30, "Default reserve must be 30 HP");
foreach (var mode in new[] { SearchObjective.PermanentGrowth, SearchObjective.NetResources })
{
    var safe = Outcome(mode, 30, 1) with { GoldGain = 1 };
    var risky = Outcome(mode, 29, 100) with { GoldGain = 1000 };
    Check(safe.MeetsLimits && !risky.MeetsLimits && safe.CompareTo(risky) < 0,
        "Rewards must not override the HP reserve");
    Check(!(Outcome(mode, 50, 100, 11) with { GoldGain = 1 }).MeetsLimits, "Loss cap still applies");
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
foreach (var mode in new[] { SearchObjective.PermanentGrowth, SearchObjective.NetResources })
{
    var noGain = Outcome(mode, 20, 0, 15);
    var unsafeGain = Outcome(mode, 19, 3, 16) with { GoldGain = 10 };
    var safeGain = Outcome(mode, 30, 3, 0) with { GoldGain = 10 };
    Check(!noGain.HasObjectiveGain && noGain.MeetsLimits,
        "No-gain routes must not be blocked by reward-only limits");
    Check(noGain.CompareTo(unsafeGain) < 0, "No gain must beat unsafe farming");
    Check(safeGain.CompareTo(noGain) < 0, "Safe positive gain remains preferred");
}
var zero = Outcome(SearchObjective.NetResources, 20, 0);
var spent = zero with { GoldGain = -10, EndingHp = 25 };
Check(zero.CompareTo(spent) == 0 && spent.MeetsLimits,
    "Nonpositive net returns fall back to HP and existing resource policy");
var unrelatedGold = Outcome(SearchObjective.PermanentGrowth, 20, 0) with { GoldGain = 100 };
Check(!unrelatedGold.HasObjectiveGain, "Gold must not count as permanent growth");
foreach(var mode in new[]{SearchObjective.PermanentGrowth,SearchObjective.NetResources})
{
    var policy=SearchObjectivePolicy.Default with { Mode=mode, GrowthTarget=0, NetResourceTarget=0 };
    var reached=Outcome(mode,30,3) with { Policy=policy, GoldGain=25 };
    Check(reached.CanStopSearch(true), "Legacy zero uses finite defaults");
    Check(!reached.CanStopSearch(false), "Incomplete routes must not stop search");
    Check(!(reached with { EndingHp=29 }).CanStopSearch(true), "Unsafe farming must not stop search");
    Check(!(reached with { Growth=default, GoldGain=0 }).CanStopSearch(true), "No gain is not target completion");
    var extra=reached with { Growth=new(0,0,100), GoldGain=1000 };
    Check(extra.TargetValue==reached.TargetValue, "Rewards above target must not improve objective priority");
}
Check(!Outcome(SearchObjective.Balanced,50,100).CanStopSearch(true), "Balanced retains its stopping rules");
Console.WriteLine($"SEARCH_OBJECTIVE_CHECKS_OK checks={checks}");
