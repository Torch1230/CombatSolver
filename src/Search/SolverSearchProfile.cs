namespace CombatSolver;

internal sealed record SolverSearchProfile(
    int BeamWidth,
    int MaxExpandedNodes,
    int MaxCardBranchesPerNode,
    int MaxPileChoiceBranchesPerAction,
    int MaxHandChoiceBranchesPerAction,
    int SoftTimeBudgetMilliseconds)
{
    public static SolverSearchProfile Default { get; } = new(
        BeamWidth: 96,
        MaxExpandedNodes: 150_000,
        MaxCardBranchesPerNode: 64,
        MaxPileChoiceBranchesPerAction: 18,
        MaxHandChoiceBranchesPerAction: 24,
        SoftTimeBudgetMilliseconds: 4_000);
}
