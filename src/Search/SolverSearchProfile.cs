namespace CombatSolver;

internal enum SolverSearchPhase
{
    Short,
    Deep,
}

internal sealed record SolverSearchProfile(
    SolverSearchPhase Phase,
    int BeamWidth,
    int MaxExpandedNodes,
    int MaxCardBranchesPerNode,
    int MaxPileChoiceBranchesPerAction,
    int MaxHandChoiceBranchesPerAction,
    int SoftTimeBudgetMilliseconds)
{
    // Internal to the existing failed-layer retry; never enabled for a normal primary solve.
    public bool RecoverDeferredTurnFrontier { get; init; }

    public static SolverSearchProfile Short { get; } = new(
        SolverSearchPhase.Short,
        BeamWidth: 24,
        MaxExpandedNodes: 2_400,
        MaxCardBranchesPerNode: 20,
        MaxPileChoiceBranchesPerAction: 10,
        MaxHandChoiceBranchesPerAction: 12,
        SoftTimeBudgetMilliseconds: 8_000);

    public static SolverSearchProfile Deep { get; } = new(
        SolverSearchPhase.Deep,
        BeamWidth: 60,
        MaxExpandedNodes: 12_000,
        MaxCardBranchesPerNode: 32,
        MaxPileChoiceBranchesPerAction: 18,
        MaxHandChoiceBranchesPerAction: 24,
        SoftTimeBudgetMilliseconds: 120_000);
}
