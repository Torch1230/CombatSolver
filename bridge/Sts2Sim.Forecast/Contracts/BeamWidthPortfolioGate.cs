namespace CombatSolver;

internal readonly record struct BeamWidthPortfolioBaseline(
    bool FrontierExhausted,
    bool ProvenZeroDamage,
    long ElapsedMilliseconds,
    long ExpandedNodes,
    int BeamWidth);

internal static class BeamWidthPortfolioGate
{
    internal const string SkippedBaselineNotFrontierExhausted = "BaselineNotFrontierExhausted";
    internal const string SkippedBaselineProvenZeroDamage = "BaselineProvenZeroDamage";
    internal const string SkippedBaselineTimeShareExceeded = "BaselineTimeShareExceeded";
    internal const string SkippedNodeHeadroom = "NodeHeadroomInsufficient";
    internal const string SkippedTimeHeadroom = "TimeHeadroomInsufficient";

    internal const int BaselineTimeShareDivisor = 4;
    internal const int SafetyNumerator = 3;
    internal const int SafetyDenominator = 2;

    internal static long EstimateMemberCost(long baselineCost, int baselineBeamWidth, int memberBeamWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baselineCost);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baselineBeamWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memberBeamWidth);
        long numerator = baselineCost * memberBeamWidth * SafetyNumerator;
        long denominator = (long)baselineBeamWidth * SafetyDenominator;
        return (numerator + denominator - 1) / denominator;
    }

    internal static string? RejectRefinement(
        in BeamWidthPortfolioBaseline baseline,
        int memberBeamWidth,
        long remainingNodes,
        long remainingMilliseconds,
        long timeBudgetMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memberBeamWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baseline.BeamWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeBudgetMilliseconds);
        if (baseline.ProvenZeroDamage)
            return SkippedBaselineProvenZeroDamage;
        if (!baseline.FrontierExhausted)
            return SkippedBaselineNotFrontierExhausted;
        if (baseline.ElapsedMilliseconds * BaselineTimeShareDivisor > timeBudgetMilliseconds)
            return SkippedBaselineTimeShareExceeded;
        if (remainingNodes < baseline.ExpandedNodes)
            return SkippedNodeHeadroom;
        if (EstimateMemberCost(baseline.ElapsedMilliseconds, baseline.BeamWidth, memberBeamWidth)
            > remainingMilliseconds)
        {
            return SkippedTimeHeadroom;
        }
        return null;
    }
}
