namespace CombatSolver;

// Shared by all familiar descendants of one nearest novel ancestor. A novel
// descendant starts its own allowance; siblings never replenish this allowance.
internal sealed class BfwsEscapeBudget(int maximum)
{
    public int Admitted { get; private set; }

    public bool TryAdmit()
    {
        if (Admitted >= maximum) return false;
        Admitted++;
        return true;
    }
}
