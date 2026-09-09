namespace CombatSolver;

// One free slot has diminishing inventory value: discount only one ordinary potion.
internal static class PotionInventoryValue
{
    internal static int RequiredCost(int cost, bool full, bool ordinaryPotionUsed, int ordinaryCost)
        => full && ordinaryPotionUsed
            ? Math.Max(0, cost - ordinaryCost + Math.Max(1, ordinaryCost / 3))
            : cost;
}
