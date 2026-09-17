namespace CombatSolver;

internal static class PowerCommitmentPortfolioGate
{
    internal const string SkippedNoReachablePower = "NoReachableRegisteredPower";

    internal static string? Reject(
        bool hasReachablePower,
        in BeamWidthPortfolioBaseline baseline,
        int memberBeamWidth,
        long remainingNodes,
        long remainingMilliseconds,
        long timeBudgetMilliseconds,
        long remainingMemoryBytes)
    {
        if (!hasReachablePower)
            return SkippedNoReachablePower;
        return BeamWidthPortfolioGate.RejectRefinement(
            baseline,
            memberBeamWidth,
            remainingNodes,
            remainingMilliseconds,
            timeBudgetMilliseconds,
            remainingMemoryBytes);
    }
}
