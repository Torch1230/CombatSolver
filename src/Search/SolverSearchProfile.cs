namespace CombatSolver;

internal sealed record SolverSearchProfile(
    int BeamWidth,
    int MaxExpandedNodes,
    int MaxCardBranchesPerNode,
    int MaxPileChoiceBranchesPerAction,
    int MaxHandChoiceBranchesPerAction,
    int SoftTimeBudgetMilliseconds)
{
    // Internal to the existing failed-layer retry; never enabled for a normal primary solve.
    public bool RecoverDeferredTurnFrontier { get; init; }

    public static SolverSearchProfile Default { get; } = new(
        BeamWidth: 60,
        MaxExpandedNodes: 24_000,
        MaxCardBranchesPerNode: 32,
        MaxPileChoiceBranchesPerAction: 18,
        MaxHandChoiceBranchesPerAction: 24,
        SoftTimeBudgetMilliseconds: 120_000);
}
