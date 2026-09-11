namespace CombatSolver;

internal static class GrowthCostPolicy
{
    internal static bool AllowsBrightestFlame(int? limit, int spentAtRoot, int spentAfterAction)
        => !limit.HasValue || spentAfterAction <= Math.Max(limit.Value, spentAtRoot);
}
