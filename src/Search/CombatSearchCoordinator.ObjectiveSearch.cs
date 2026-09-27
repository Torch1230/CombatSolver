using System.Diagnostics;

namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    private static SolverResult RunObjectiveSearch(CombatRootSnapshot root,
        SolverDisplayNames names, BattleDamageSnapshot damage, SearchPolicySnapshot policy,
        SolverSearchProfile profile, Stopwatch clock, SolverPotionPolicy? potionOverride,
        CancellationToken cancellation, Action<SolverProgress>? progress, Action<SolverResult>? publish)
    {
        if (policy.ObjectiveValueModel is not { IsFitted: true } || policy.MaxDegreeOfParallelism != 1)
            throw new InvalidOperationException("Outcome valuation research requires a fitted model and DOP 1.");
        return RunPretrainedObjectiveSearch(root, names, damage, policy, profile, clock,
            potionOverride, cancellation, progress, publish);
    }
    private static SolverResult RunPretrainedObjectiveSearch(CombatRootSnapshot root,
        SolverDisplayNames names, BattleDamageSnapshot damage, SearchPolicySnapshot policy,
        SolverSearchProfile profile, Stopwatch clock, SolverPotionPolicy? potionOverride,
        CancellationToken cancellation, Action<SolverProgress>? progress, Action<SolverResult>? publish)
    {
        SearchRequestWorkTotals totals = policy.RequestWorkTotals!;
        long start = totals.Snapshot().ExpandedNodes;
        SolverResult? best = null;
        for (int width = profile.BeamWidth; ; width = Math.Min(512, width * 2))
        {
            cancellation.ThrowIfCancellationRequested();
            var remaining = AutomaticSearchBudget.Remaining(profile, clock.ElapsedMilliseconds,
                totals.Snapshot().ExpandedNodes - start);
            if (remaining == null) break;
            var member = remaining with { BeamWidth = width, AggressivePowerCommitment = false,
                BaseScoreOnly = false, SecondRankBand = false };
            SolverResult candidate = new CombatBeamSolver(root, names, damage,
                policy, cancellation, progress, member,
                potionPolicyOverride: potionOverride).Solve();
            if (candidate.ResultScope != SolverResultScope.SearchCompletion) return candidate;
            if (best == null || IsBetterPotionPolicyResult(root, policy, candidate, best))
            {
                best = candidate;
                publish?.Invoke(best);
            }
            policy.Diagnostics.Info($"[CombatSolver/Test] FITTED_SEARCH width={width} "
                + $"nodes={candidate.ExpandedNodes} hp_lost={candidate.ProjectedBattleHpLost}");
            if (width >= 512 || CanFinishTargetPortfolio(root, policy, profile, best)) break;
        }
        return best ?? throw new PotionPolicyUnsatisfiedException("Fitted search exhausted its request budget.");
    }

}
