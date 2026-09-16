using CombatSolver;

int checks = 0;
void Require(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }

SolverSearchProfile baseProfile = SolverSearchProfile.Default with { BeamWidth = 24, MaxExpandedNodes = 20_000 };

static SolverInterimResult Outcome(bool won, int hpDeficit, int potions = 0, double score = 0)
    => new(won, 0, hpDeficit, hpDeficit, 0, potions, 0, score);

static Func<SolverSearchProfile, BeamWidthPortfolioRun<SolverInterimResult>> Member(
    long expanded, string termination, bool terminal, SolverInterimResult result, bool stop = false)
    => _ => new BeamWidthPortfolioRun<SolverInterimResult>(
        result, expanded, expanded * 3, termination, terminal, result.Won,
        terminal ? result.ProjectedBattleHpLost : null, result.ProjectedBattlePotionCount)
    { StopPortfolio = stop };

static Func<SolverSearchProfile, BeamWidthPortfolioRun<SolverInterimResult>> Finished(
    long expanded, SolverInterimResult result)
    => Member(expanded, "FrontierExhausted", true, result);

List<SolverSearchProfile> observed = [];
BeamWidthPortfolioOutcome<SolverInterimResult> Portfolio(
    IReadOnlyList<int> widths,
    int sharedNodes,
    IReadOnlyList<Func<SolverSearchProfile, BeamWidthPortfolioRun<SolverInterimResult>>> members,
    Func<int, string?>? reject = null)
    => PortfolioOf(
        [.. widths.Select(width => new BeamWidthPortfolioMemberSpec(width))],
        sharedNodes,
        members,
        reject == null ? null : member => reject(member.BeamWidth));

BeamWidthPortfolioOutcome<SolverInterimResult> PortfolioOf(
    IReadOnlyList<BeamWidthPortfolioMemberSpec> specs,
    int sharedNodes,
    IReadOnlyList<Func<SolverSearchProfile, BeamWidthPortfolioRun<SolverInterimResult>>> members,
    Func<BeamWidthPortfolioMemberSpec, string?>? reject = null)
{
    observed.Clear();
    int next = 0;
    return BeamWidthPortfolio.Run(
        specs,
        sharedNodes,
        baseProfile,
        profile => { observed.Add(profile); return members[next++](profile); },
        // The production rule, extracted from CombatSearchCoordinator, not a restatement of it.
        (candidate, current) => CombatSearchCoordinator.IsBetterPotionPolicyResult(null, candidate, current),
        reject);
}

static BeamWidthPortfolioMemberSpec Width(int width) => new(width);
static BeamWidthPortfolioMemberSpec Band(int width) => new(width, SecondRankBand: true);
static BeamWidthPortfolioMemberSpec Base(int width) => new(width, BaseScoreOnly: true);

// 1. Production membership: baseline first, the narrow (2/3) and wide (3/2) refinements, then the
// second-rank-band member and the base-score-only member at the baseline width.
Require(BeamWidthPortfolio.ProductionMembers(24, null).SequenceEqual([Width(24), Width(16), Width(36), Band(24), Base(24)]),
    "Production default membership changed.");
Require(BeamWidthPortfolio.ProductionMembers(45, null).SequenceEqual([Width(45), Width(30), Width(68), Band(45), Base(45)]),
    "Act-ending boss baseline membership changed.");
Require(BeamWidthPortfolio.ProductionMembers(135, null).SequenceEqual([Width(135), Width(90), Width(203), Band(135), Base(135)]),
    "VeryHigh baseline membership changed.");
Require(BeamWidthPortfolio.ProductionMembers(1, null).SequenceEqual([Width(1), Width(2), Band(1), Base(1)]),
    "Production membership repeated the baseline width or admitted a non-positive width.");
Require(BeamWidthPortfolio.ProductionMembers(24, [24, 23, 25, 96]).SequenceEqual([Width(24), Width(23), Width(25), Width(96)]),
    "A configured membership was rewritten or gained a band or base-score member.");
Require(BeamWidthPortfolio.ProductionMembers(24, [96, 0, -3, 96, 23]).SequenceEqual([Width(24), Width(96), Width(23)]),
    "Configured membership did not force the baseline first, drop duplicates and drop non-positive widths.");
Require(BeamWidthPortfolio.ProductionMembers(24, null).Count(member => member.SecondRankBand) == 1
    && BeamWidthPortfolio.ProductionMembers(24, null).Count(member => member.BaseScoreOnly) == 1
    && BeamWidthPortfolio.ProductionMembers(24, null)[0] == Width(24)
    && !BeamWidthPortfolio.ProductionMembers(24, null).Any(member => member.SecondRankBand && member.BaseScoreOnly),
    "The baseline member must be a plain width member; the default has exactly one band and one base-score member, never combined.");

// 1a. The base-score member only flips BaseScoreOnly on its member profile.
var based = PortfolioOf([Width(24), Base(24)], 1000,
    [Finished(400, Outcome(true, 50)), Finished(100, Outcome(true, 40))]);
Require(observed.Count == 2 && !observed[0].BaseScoreOnly && !observed[0].SecondRankBand,
    "Baseline member profile changed when a base-score member is configured.");
Require(observed[1] == (baseProfile with { BaseScoreOnly = true, MaxExpandedNodes = 600 }),
    "Base-score member profile differs from the base profile in more than the flag and the node budget.");
Require(based.SelectedIndex == 1 && based.Members[1] is { BeamWidth: 24, BaseScoreOnly: true, SecondRankBand: false, Ran: true, Compared: true },
    "Base-score member detail or selection is wrong.");
Require(!SolverSearchProfile.Default.BaseScoreOnly, "The default profile must not be a base-score profile.");

// 1b. The band member only flips SecondRankBand on its member profile; the baseline stays plain.
var banded = PortfolioOf([Width(24), Band(24)], 1000,
    [Finished(400, Outcome(true, 50)), Finished(100, Outcome(true, 40))]);
Require(observed.Count == 2 && !observed[0].SecondRankBand && observed[0] == (baseProfile with { MaxExpandedNodes = 1000 }),
    "Baseline member profile changed when a band member is configured.");
Require(observed[1] == (baseProfile with { SecondRankBand = true, MaxExpandedNodes = 600 }),
    "Band member profile differs from the base profile in more than the band flag and the node budget.");
Require(banded.SelectedIndex == 1 && banded.Members[1] is { BeamWidth: 24, SecondRankBand: true, Ran: true, Compared: true }
    && banded.Members[0] is { SecondRankBand: false },
    "Band member detail or selection is wrong.");
Require(!SolverSearchProfile.Default.SecondRankBand, "The default profile must not be a band profile.");

// 1c. The band rotation used by the retention policy: leading W entries go to the tail, so a cut at W
// keeps ranks W+1..2W; short pools top up from the moved entries in their original order.
List<int> pool = [.. Enumerable.Range(1, 10)];
BeamWidthPortfolio.MoveLeadingBandToTail(pool, 4);
Require(pool.SequenceEqual([5, 6, 7, 8, 9, 10, 1, 2, 3, 4]), "Band rotation did not move the leading band to the tail.");
Require(pool.Take(4).SequenceEqual([5, 6, 7, 8]), "A cut at W after the rotation did not keep ranks W+1..2W.");
List<int> shortPool = [.. Enumerable.Range(1, 6)];
BeamWidthPortfolio.MoveLeadingBandToTail(shortPool, 4);
Require(shortPool.Take(4).SequenceEqual([5, 6, 1, 2]), "A short pool did not top up from the moved band in order.");
List<int> tinyPool = [.. Enumerable.Range(1, 4)];
BeamWidthPortfolio.MoveLeadingBandToTail(tinyPool, 4);
Require(tinyPool.SequenceEqual([1, 2, 3, 4]), "A pool that is not cut was reordered.");
List<int> emptyPool = [];
BeamWidthPortfolio.MoveLeadingBandToTail(emptyPool, 4);
Require(emptyPool.Count == 0, "An empty pool was changed.");

// 2. Shared budget: the first member gets everything, later members get what is left.
var budgets = Portfolio([24, 23, 25], 1000,
    [Finished(400, Outcome(true, 50)), Finished(600, Outcome(true, 60)), Finished(1, Outcome(true, 0))]);
Require(observed.Count == 2, "Portfolio ran a member after the shared budget was gone.");
Require(observed[0].MaxExpandedNodes == 1000 && observed[0].BeamWidth == 24, "Baseline member did not get the full budget.");
Require(observed[1].MaxExpandedNodes == 600 && observed[1].BeamWidth == 23, "Second member budget was not the remainder.");
Require(budgets.Members[2] is { Ran: false, Compared: false, SkippedReason: BeamWidthPortfolio.SkippedBudgetExhausted },
    "Exhausted member was not reported.");
Require(budgets.TotalExpandedNodes == 1000 && budgets.TotalTransitionCount == 3000, "Portfolio totals are wrong.");
Require(budgets.SelectedIndex == 0 && budgets.SelectionReason == BeamWidthPortfolio.SelectionBest,
    "Better baseline was replaced.");

// Everything but width and node budget is copied from the base profile.
Require(observed[1] == (baseProfile with { BeamWidth = 23, MaxExpandedNodes = 600 }),
    "A member changed a search dimension other than beam width and node budget.");

// 3. A member that hit the node limit without reaching a terminal state never competes.
var truncated = Portfolio([24, 23], 1000,
    [Finished(300, Outcome(true, 50)), Member(700, BeamWidthPortfolio.NodeLimitTermination, false, Outcome(true, 0))]);
Require(truncated.SelectedIndex == 0, "A truncated node-limit member won the comparison.");
Require(truncated.Members[1] is { Ran: true, Compared: false, SkippedReason: BeamWidthPortfolio.SkippedNodeLimitNotTerminal },
    "Truncated member was not reported as excluded.");
Require(truncated.Members[1].ExpandedNodes == 700, "Excluded member work was dropped from the report.");
Require(truncated.TotalExpandedNodes == 1000, "Excluded member work was dropped from the totals.");

// A node-limit member that did reach a terminal state is a complete result and does compete.
var terminalAtLimit = Portfolio([24, 23], 1000,
    [Finished(300, Outcome(true, 50)), Member(700, BeamWidthPortfolio.NodeLimitTermination, true, Outcome(true, 10))]);
Require(terminalAtLimit.SelectedIndex == 1 && terminalAtLimit.Members[1].Compared,
    "A terminal node-limit member was excluded.");

// All members truncated: fall back to the baseline rather than inventing a winner.
var allTruncated = Portfolio([24, 23], 1000,
    [Member(300, BeamWidthPortfolio.NodeLimitTermination, false, Outcome(false, 70)),
     Member(300, BeamWidthPortfolio.NodeLimitTermination, false, Outcome(false, 10))]);
Require(allTruncated.SelectedIndex == 0 && allTruncated.SelectionReason == BeamWidthPortfolio.SelectionBaselineFallback,
    "No comparable member did not fall back to the baseline.");

// 4. One member is exactly one solve: same profile, same result instance.
SolverInterimResult single = Outcome(true, 42, potions: 1);
var alone = Portfolio([24], 20_000, [Finished(1234, single)]);
Require(observed.Count == 1 && observed[0] == baseProfile, "Single-member profile differs from a direct solve.");
Require(ReferenceEquals(alone.Selected, single) && alone.SelectedIndex == 0
    && alone.SelectionReason == BeamWidthPortfolio.SelectionBest, "Single-member result was not returned unchanged.");
Require(alone.Members[0] is { BeamWidth: 24, NodeBudget: 20_000, Ran: true, Compared: true, Won: true, BattleHpLost: 42, PotionCount: 1 },
    "Single-member detail does not describe the run.");

// Two members of the same width differ only by the budget the first one already spent.
var sameWidth = Portfolio([24, 24], 1000, [Finished(400, Outcome(true, 50)), Finished(100, Outcome(true, 50))]);
Require(observed[0].BeamWidth == observed[1].BeamWidth && observed[1].MaxExpandedNodes == 600,
    "Repeated width did not reuse the shared budget rule.");
Require(sameWidth.SelectedIndex == 0, "A tie did not keep the baseline member.");

// 5. Selection follows the production ordering in both directions.
Require(Portfolio([24, 23], 1000, [Finished(10, Outcome(false, 10)), Finished(10, Outcome(true, 80))]).SelectedIndex == 1,
    "Victory lost to a lower loss on a defeat.");
Require(Portfolio([24, 23], 1000, [Finished(10, Outcome(true, 80)), Finished(10, Outcome(false, 10))]).SelectedIndex == 0,
    "A defeat beat a victory.");
Require(Portfolio([24, 23], 1000, [Finished(10, Outcome(true, 50)), Finished(10, Outcome(true, 30))]).SelectedIndex == 1,
    "Lower strategic HP loss was not preferred.");
Require(Portfolio([24, 23], 1000, [Finished(10, Outcome(true, 30)), Finished(10, Outcome(true, 50))]).SelectedIndex == 0,
    "Higher strategic HP loss was preferred.");
Require(Portfolio([24, 23], 1000,
        [Finished(10, Outcome(true, 30, potions: 2)), Finished(10, Outcome(true, 30, potions: 1))]).SelectedIndex == 1,
    "Equal loss did not fall through to the potion count.");
Require(Portfolio([24, 23, 25], 1000,
        [Finished(10, Outcome(true, 30)), Finished(10, Outcome(true, 30)), Finished(10, Outcome(true, 30))]).SelectedIndex == 0,
    "A three-way tie did not keep the baseline member.");

// The comparator is the only selection authority: an independent scan must agree.
SolverInterimResult[] scripted =
[
    Outcome(true, 44, potions: 1, score: 5),
    Outcome(true, 44, potions: 1, score: 9),
    Outcome(true, 44, potions: 0, score: 1),
];
int expected = 0;
for (int index = 1; index < scripted.Length; index++)
{
    if (CombatSearchCoordinator.IsBetterPotionPolicyResult(null, scripted[index], scripted[expected]))
        expected = index;
}
Require(Portfolio([24, 23, 25], 3000, [.. scripted.Select(result => Finished(10, result))]).SelectedIndex == expected,
    "Portfolio selection disagreed with a direct scan under the same rule.");

// 6. A contract rejection skips the member without spending budget or hiding the rejection.
var rejected = Portfolio([24, 23, 25], 1000,
    [Finished(400, Outcome(true, 50)), Finished(10, Outcome(true, 0))],
    width => width < 24 ? "TeacherProfileBeamWidthFloor:24" : null);
Require(observed.Count == 2 && observed[1].BeamWidth == 25 && observed[1].MaxExpandedNodes == 600,
    "A rejected member consumed budget or shifted the remaining members.");
Require(rejected.Members[1] is { BeamWidth: 23, Ran: false, Compared: false, SkippedReason: "TeacherProfileBeamWidthFloor:24" },
    "Rejected member was not reported.");

// 7. A non-search-completion result stops the portfolio and is returned as is.
SolverInterimResult adopted = Outcome(false, 99);
var stopped = Portfolio([24, 23, 25], 1000,
    [Member(100, "FrontierExhausted", true, adopted, stop: true), Finished(10, Outcome(true, 0))]);
Require(observed.Count == 1 && ReferenceEquals(stopped.Selected, adopted)
    && stopped.SelectionReason == BeamWidthPortfolio.SelectionStopped, "Portfolio ignored a stop request.");

// 8. Input contracts.
static void Throws<TException>(Action action, string message) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new Exception(message);
}
Throws<ArgumentException>(() => Portfolio([], 1000, []), "Empty membership was accepted.");
Throws<ArgumentOutOfRangeException>(() => Portfolio([24, 0], 1000, []), "A non-positive member width was accepted.");
Throws<ArgumentOutOfRangeException>(() => Portfolio([24], 0, []), "A non-positive shared budget was accepted.");
Throws<InvalidOperationException>(
    () => Portfolio([24, 23], 1000, [], width => "rejected"), "Rejecting every member did not fail loudly.");
checks += 4;

// 9. The refinement gate. A baseline that exhausted its frontier quickly and left room admits refinement.
const long Budget = 60_000;
BeamWidthPortfolioBaseline Baseline(
    bool exhausted = true, bool provenZeroDamage = false, long elapsed = 5_000,
    long expanded = 4_000, long allocated = 400_000_000, int width = 24)
    => new(exhausted, provenZeroDamage, elapsed, expanded, allocated, width);

string? Gate(
    BeamWidthPortfolioBaseline baseline, int memberWidth = 96, long remainingNodes = 16_000,
    long remainingMilliseconds = 55_000, long remainingMemoryBytes = long.MaxValue)
    => BeamWidthPortfolioGate.RejectRefinement(
        baseline, memberWidth, remainingNodes, remainingMilliseconds, Budget, remainingMemoryBytes);

Require(Gate(Baseline()) == null, "A baseline with headroom on every axis was refused.");

// Baseline truncated by a limit: finish that width before spending the budget elsewhere.
Require(Gate(Baseline(exhausted: false)) == BeamWidthPortfolioGate.SkippedBaselineNotFrontierExhausted,
    "A truncated baseline still started a refinement.");

// A proven-optimal baseline is never refined, even when every other axis has room.
Require(Gate(Baseline(provenZeroDamage: true)) == BeamWidthPortfolioGate.SkippedBaselineProvenZeroDamage,
    "A proven zero-damage baseline still started a refinement.");
Require(Gate(Baseline(exhausted: false, provenZeroDamage: true))
        == BeamWidthPortfolioGate.SkippedBaselineProvenZeroDamage,
    "Proven zero damage did not take precedence over the truncation reason.");

// Exactly a quarter of the budget is still early; one millisecond more is not.
Require(Gate(Baseline(elapsed: Budget / 4), memberWidth: 24, remainingMilliseconds: Budget - Budget / 4) == null,
    "A baseline at exactly a quarter of the budget was refused.");
Require(Gate(Baseline(elapsed: Budget / 4 + 1), memberWidth: 24, remainingMilliseconds: Budget - Budget / 4)
        == BeamWidthPortfolioGate.SkippedBaselineTimeShareExceeded,
    "A baseline past a quarter of the budget still started a refinement.");

// Remaining nodes must still cover another baseline-sized expansion.
Require(Gate(Baseline(expanded: 4_000), remainingNodes: 4_000) == null,
    "Node headroom equal to the baseline expansion was refused.");
Require(Gate(Baseline(expanded: 4_000), remainingNodes: 3_999) == BeamWidthPortfolioGate.SkippedNodeHeadroom,
    "A refinement started with less node headroom than the baseline expansion.");

// Time estimate is baseline elapsed x width ratio x 3/2.
Require(BeamWidthPortfolioGate.EstimateMemberCost(5_000, 24, 96) == 30_000, "Time estimate arithmetic changed.");
Require(BeamWidthPortfolioGate.EstimateMemberCost(5_000, 24, 24) == 7_500, "Same-width estimate is not 1.5x.");
Require(BeamWidthPortfolioGate.EstimateMemberCost(1, 24, 25) == 2, "Estimate did not round up.");
Require(Gate(Baseline(elapsed: 5_000), memberWidth: 96, remainingMilliseconds: 30_000) == null,
    "A refinement that exactly fits the remaining time was refused.");
Require(Gate(Baseline(elapsed: 5_000), memberWidth: 96, remainingMilliseconds: 29_999)
        == BeamWidthPortfolioGate.SkippedTimeHeadroom,
    "A refinement started without enough remaining time for its estimate.");

// Memory headroom uses the same estimate; a disabled signal (long.MaxValue) never blocks.
Require(Gate(Baseline(allocated: 400_000_000), memberWidth: 96, remainingMemoryBytes: 2_400_000_000) == null,
    "A refinement that exactly fits the reported memory headroom was refused.");
Require(Gate(Baseline(allocated: 400_000_000), memberWidth: 96, remainingMemoryBytes: 2_399_999_999)
        == BeamWidthPortfolioGate.SkippedMemoryHeadroom,
    "A refinement started without enough reported memory headroom.");
Require(Gate(Baseline(allocated: long.MaxValue / 1024), remainingMemoryBytes: long.MaxValue) == null,
    "A disabled memory pressure signal blocked a refinement.");

// The gate plugs into the combinator's rejection hook: rejected members keep their line and spend nothing.
var gated = Portfolio([24, 96], 20_000,
    [Finished(4_000, Outcome(true, 50))],
    width => width == 24 ? null : Gate(Baseline(exhausted: false), width));
Require(observed.Count == 1, "A gated member still ran.");
Require(gated.Members[1] is
        { BeamWidth: 96, Ran: false, Compared: false, NodeBudget: 0,
          SkippedReason: BeamWidthPortfolioGate.SkippedBaselineNotFrontierExhausted },
    "A gated member was not reported with its reason.");
Require(gated.SelectedIndex == 0 && gated.TotalExpandedNodes == 4_000,
    "A gated member changed the selection or the totals.");

Throws<ArgumentOutOfRangeException>(
    () => Gate(Baseline(), memberWidth: 0), "A non-positive member width was accepted by the gate.");
Throws<ArgumentOutOfRangeException>(
    () => Gate(Baseline(width: 0)), "A non-positive baseline width was accepted by the gate.");
checks += 2;

Console.WriteLine($"BEAM_WIDTH_PORTFOLIO_OK checks={checks}");
