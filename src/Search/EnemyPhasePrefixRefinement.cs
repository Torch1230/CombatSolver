namespace CombatSolver;

// Request-owned pure prefixes; continuation execution belongs to the scheduler.
// Keep the original full-root members. One existing later base-score slot may
// instead replay a discovered hand-off and apply ordinary continuation ranking.
internal sealed class EnemyPhasePrefixRefinement
{
    private readonly SortedDictionary<int, EnemyPhaseFrontierCandidate> _frontiers = [];
    internal bool Attempted { get; private set; }

    internal static bool IsEnabled(SearchPolicySnapshot policy)
        => policy.UseBeamWidthPortfolio && !policy.UseNoveltyPortfolio
            && policy.Profile.ReallocatedRefinementPortfolio
            && !policy.IncludeTurnSetup && policy.PortfolioExperiment == null
            && policy.DevelopmentStrategy == null
            && policy.BeamWidthPortfolioWidths is not { Count: > 0 }
            && !policy.Profile.AdaptiveNoveltyRefinement
            && policy.Profile.ContextualRanking == null
            && policy.Profile.BeamWeightPerturbation == null;

    internal void Observe(IReadOnlyList<EnemyPhaseFrontierCandidate> candidates)
    {
        foreach (EnemyPhaseFrontierCandidate candidate in candidates)
        {
            if (!_frontiers.TryGetValue(candidate.Turn, out var prior)
                || candidate.ProjectedPlayerHp > prior.ProjectedPlayerHp
                || candidate.ProjectedPlayerHp == prior.ProjectedPlayerHp
                    && candidate.EnemyHp < prior.EnemyHp)
                _frontiers[candidate.Turn] = candidate;
            if (_frontiers.Count > 2)
                _frontiers.Remove(_frontiers.Keys.Last());
        }
    }

    internal EnemyPhaseFrontierCandidate? TryTake(SolverSearchProfile member,
        SolverSearchProfile pass, SolverSearchProfile root, bool refinement,
        bool hasOpeningIncumbent)
    {
        if (Attempted || !refinement || hasOpeningIncumbent
            || pass.BeamWidth <= root.BeamWidth || member.BeamWidth != pass.BeamWidth
            || !member.BaseScoreOnly || member.AggressivePowerCommitment)
            return null;
        EnemyPhaseFrontierCandidate? selected = _frontiers.Values
            .OrderByDescending(candidate => candidate.ProjectedPlayerHp)
            .ThenBy(candidate => candidate.EnemyHp).FirstOrDefault();
        Attempted = selected != null;
        return selected;
    }
}
