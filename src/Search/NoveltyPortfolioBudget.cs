namespace CombatSolver;

// The optional scout spends a bounded portion of the same request. Unspent
// allowance returns to Beam; only measured work is deducted from its budget.
internal sealed record NoveltyPortfolioBudget(
    int TimeDivisor, int MaximumMilliseconds, int MaximumExpandedNodes)
{
    public static NoveltyPortfolioBudget Default { get; } = new(2, 5_000, 2_500);

    public SolverSearchProfile? Exploration(SolverSearchProfile profile, bool actEndingBoss = false)
    {
        if (TimeDivisor < 2 || MaximumMilliseconds < 1 || MaximumExpandedNodes < 1)
            throw new ArgumentOutOfRangeException(nameof(NoveltyPortfolioBudget));
        if (profile.SoftTimeBudgetMilliseconds < 2_000 || profile.MaxExpandedNodes < 1_000)
            return null;
        return profile with
        {
            // Boss routes need a longer Beam horizon; reserve at least three quarters
            // of their time budget for the normal search and potion audits.
            SoftTimeBudgetMilliseconds = Math.Min(MaximumMilliseconds,
                profile.SoftTimeBudgetMilliseconds / (actEndingBoss ? Math.Max(4, TimeDivisor) : TimeDivisor)),
            MaxExpandedNodes = Math.Min(MaximumExpandedNodes, profile.MaxExpandedNodes / 4),
        };
    }

    public static SolverSearchProfile? Remaining(
        SolverSearchProfile profile, long elapsedMilliseconds, long expandedNodes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(expandedNodes);
        if (elapsedMilliseconds >= profile.SoftTimeBudgetMilliseconds || expandedNodes >= profile.MaxExpandedNodes)
            return null;
        return profile with
        {
            SoftTimeBudgetMilliseconds = profile.SoftTimeBudgetMilliseconds - (int)elapsedMilliseconds,
            MaxExpandedNodes = profile.MaxExpandedNodes - (int)expandedNodes,
        };
    }
}
