namespace CombatSolver;

// One free slot has diminishing inventory value: discount only one ordinary potion.
internal static class PotionInventoryValue
{
    internal static int PaidCapacity(int hpBudget, int regularCost, int firstCost)
    {
        if (regularCost <= 0 || firstCost <= 0)
            throw new ArgumentOutOfRangeException(nameof(regularCost), "Paid potion costs must be positive.");
        // Boss policies use a prohibitive sentinel when HP cannot retain value.
        if (regularCost >= int.MaxValue / 4 || firstCost >= int.MaxValue / 4 || hpBudget < firstCost)
            return 0;
        return 1 + (hpBudget - firstCost) / regularCost;
    }

    internal static int RequiredCost(int cost, bool full, bool ordinaryPotionUsed, int ordinaryCost)
        => full && ordinaryPotionUsed
            ? Math.Max(0, cost - ordinaryCost + Math.Max(1, ordinaryCost / 3))
            : cost;
}
