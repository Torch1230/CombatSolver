namespace CombatSolver;

internal static class SilentWraithOpeningWindow
{
    internal static bool ShouldProtect(
        int remainingTurns,
        int intangibleTurns,
        int projectedHpBeforeOpening)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(remainingTurns);
        ArgumentOutOfRangeException.ThrowIfNegative(intangibleTurns);
        return projectedHpBeforeOpening <= 0
            || remainingTurns <= intangibleTurns + 1;
    }
}
