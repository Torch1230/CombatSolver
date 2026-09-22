using System.Diagnostics;

namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    private static SolverResult RunNoveltyPortfolioPass(
        CombatRootSnapshot root, SolverDisplayNames names, BattleDamageSnapshot damage,
        SearchPolicySnapshot policy, SolverSearchProfile profile, Stopwatch clock,
        SolverPotionPolicy? potionOverride, CancellationToken cancellation,
        Action<SolverProgress>? progress, Action<SolverResult>? publish,
        Func<SolverSearchProfile, SolverResult> solveBaseline)
    {
        if (profile.AdaptiveNoveltyRefinement)
        {
            return RunAdaptiveNoveltyRefinement(root, names, damage, policy, profile,
                clock, potionOverride, cancellation, progress, publish, solveBaseline);
        }
        SolverSearchProfile? explorationProfile = policy.NoveltyBudget.Exploration(profile, root.IsActEndingBoss);
        if (explorationProfile == null)
        {
            SolverResult unchanged = solveBaseline(profile);
            unchanged.NoveltyPortfolio = new("budget_too_small", 0, 0, null, true, "beam",
                null, false, unchanged.ProjectedBattleHpLost, IsCompleteVictory(unchanged),
                profile.MaxExpandedNodes, profile.SoftTimeBudgetMilliseconds);
            return unchanged;
        }
        SearchRequestWorkTotals totals = policy.RequestWorkTotals
            ?? throw new InvalidOperationException("Novelty portfolio requires request work totals.");
        long expandedBefore = totals.Snapshot().ExpandedNodes;
        // Missing mandatory potion routes are a defined search boundary. Beam still gets
        // the remainder; simulation errors and caller cancellation propagate normally.
        SolverResult? exploration = SolveOptionalPotionPosterior(new CombatBeamSolver(root, names, damage,
            policy with { NoveltySearch = policy.NoveltySearch ?? new() }, cancellation,
            progress, explorationProfile, potionPolicyOverride: potionOverride), policy, "novelty_exploration");
        long explorationExpanded = totals.Snapshot().ExpandedNodes - expandedBefore;
        long explorationElapsed = clock.ElapsedMilliseconds;
        if (exploration != null && IsCompleteVictory(exploration)) publish?.Invoke(exploration);
        if (exploration != null && ResolveTakeoverResult(exploration, policy.Interaction) is { } adopted)
        {
            adopted.NoveltyPortfolio = new("adopted", explorationExpanded, explorationElapsed,
                exploration.NoveltySearch, false, "adopted", exploration.ProjectedBattleHpLost,
                IsCompleteVictory(exploration), null, false, 0, 0);
            return adopted;
        }
        bool settled = exploration != null && (exploration.ResultScope != SolverResultScope.SearchCompletion
            || !policy.PotionStrategy.HasForcedDirectives && HasReachedAcceptableBattleHpLoss(policy, exploration));
        SolverSearchProfile? remaining = NoveltyPortfolioBudget.Remaining(profile,
            clock.ElapsedMilliseconds, explorationExpanded);
        if (settled || remaining == null)
        {
            if (exploration == null)
                throw new PotionPolicyUnsatisfiedException("Novelty search exhausted the request before satisfying mandatory potion use.");
            exploration.NoveltyPortfolio = Describe("exploration_finished", exploration, null, "exploration");
            return exploration;
        }
        cancellation.ThrowIfCancellationRequested();
        SolverResult baseline = solveBaseline(remaining);
        bool selectExploration = baseline.ResultScope == SolverResultScope.SearchCompletion
            && exploration != null && IsCompleteVictory(exploration)
            && IsBetterPotionPolicyResult(root, policy, exploration, baseline);
        SolverResult selected = selectExploration ? exploration! : baseline;
        selected.NoveltyPortfolio = Describe("portfolio", exploration, baseline,
            selectExploration ? "exploration" : "beam");
        return selected;

        NoveltyPortfolioTelemetry Describe(string stop, SolverResult? scout, SolverResult? beam, string selectedMethod)
            => new(stop, explorationExpanded, explorationElapsed, scout?.NoveltySearch,
                beam != null, selectedMethod, scout?.ProjectedBattleHpLost,
                scout != null && IsCompleteVictory(scout), beam?.ProjectedBattleHpLost,
                beam != null && IsCompleteVictory(beam), remaining?.MaxExpandedNodes ?? 0,
                remaining?.SoftTimeBudgetMilliseconds ?? 0);
    }

    private static SolverResult RunAdaptiveNoveltyRefinement(
        CombatRootSnapshot root, SolverDisplayNames names, BattleDamageSnapshot damage,
        SearchPolicySnapshot policy, SolverSearchProfile profile, Stopwatch clock,
        SolverPotionPolicy? potionOverride, CancellationToken cancellation,
        Action<SolverProgress>? progress, Action<SolverResult>? publish,
        Func<SolverSearchProfile, SolverResult> solveBaseline)
    {
        SearchRequestWorkTotals totals = policy.RequestWorkTotals
            ?? throw new InvalidOperationException("Adaptive novelty refinement requires request work totals.");
        long beforeExpanded = totals.Snapshot().ExpandedNodes;
        long beforeMilliseconds = clock.ElapsedMilliseconds;
        SolverResult baseline = solveBaseline(profile);
        long baselineExpanded = totals.Snapshot().ExpandedNodes - beforeExpanded;
        long baselineMilliseconds = clock.ElapsedMilliseconds - beforeMilliseconds;
        SolverResult? adopted = ResolveTakeoverResult(baseline, policy.Interaction);
        if (adopted != null)
            return adopted;
        if (baseline.ResultScope != SolverResultScope.SearchCompletion
            || CanFinishTargetPortfolio(root, policy, profile, baseline))
        {
            baseline.NoveltyPortfolio = Describe("baseline_finished", null, 0, 0, "beam");
            return baseline;
        }
        SolverSearchProfile? refinement = policy.NoveltyBudget.RefinementAfterBaseline(
            profile, baselineMilliseconds, baselineExpanded, clock.ElapsedMilliseconds, root.IsActEndingBoss);
        if (refinement == null)
        {
            baseline.NoveltyPortfolio = Describe("refinement_budget_unavailable", null, 0, 0, "beam");
            return baseline;
        }
        cancellation.ThrowIfCancellationRequested();
        SearchRequestWorkSnapshot scoutBefore = totals.Snapshot();
        long scoutBeforeMilliseconds = clock.ElapsedMilliseconds;
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] ADAPTIVE_NOVELTY_START baseline_expanded={baselineExpanded} " +
            $"baseline_ms={baselineMilliseconds} nodes={refinement.MaxExpandedNodes} " +
            $"time_ms={refinement.SoftTimeBudgetMilliseconds}");
        SolverResult? exploration = SolveOptionalPotionPosterior(new CombatBeamSolver(
            root, names, damage, policy with { NoveltySearch = policy.NoveltySearch ?? new() },
            cancellation, progress, refinement, potionPolicyOverride: potionOverride),
            policy, "adaptive_novelty_refinement");
        SearchRequestWorkSnapshot scoutAfter = totals.Snapshot();
        long expanded = scoutAfter.ExpandedNodes - scoutBefore.ExpandedNodes;
        long transitions = scoutAfter.TransitionCount - scoutBefore.TransitionCount;
        long elapsed = clock.ElapsedMilliseconds - scoutBeforeMilliseconds;
        if (exploration != null && ResolveTakeoverResult(exploration, policy.Interaction) is { } scoutAdopted)
            return scoutAdopted;
        bool improved = exploration != null && IsCompleteVictory(exploration)
            && IsBetterPotionPolicyResult(root, policy, exploration, baseline);
        SolverResult selected = improved ? exploration! : baseline;
        selected.NoveltyPortfolio = Describe("adaptive_refinement", exploration, expanded, elapsed,
            improved ? "exploration" : "beam");
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] ADAPTIVE_NOVELTY_END expanded={expanded} transitions={transitions} elapsed_ms={elapsed} " +
            $"won={exploration != null && IsCompleteVictory(exploration)} selected={improved}");
        if (improved)
            publish?.Invoke(selected);
        return selected;

        NoveltyPortfolioTelemetry Describe(string stop, SolverResult? scout, long expanded,
            long elapsed, string selectedMethod)
            => new(stop, expanded, elapsed, scout?.NoveltySearch, true, selectedMethod,
                scout?.ProjectedBattleHpLost, scout != null && IsCompleteVictory(scout),
                baseline.ProjectedBattleHpLost, IsCompleteVictory(baseline),
                (int)Math.Max(0, profile.MaxExpandedNodes - baselineExpanded - expanded),
                (int)Math.Max(0, profile.SoftTimeBudgetMilliseconds - clock.ElapsedMilliseconds));
    }
}
