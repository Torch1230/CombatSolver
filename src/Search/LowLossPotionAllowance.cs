namespace CombatSolver;

/// <summary>Value-only, request-local permission for one potion with small HP savings.</summary>
internal sealed record LowLossPotionAllowance(
    int StrategicHpDeficit,
    int LifeHpDeficit,
    int OutstandingStolenResource,
    bool PreserveResources)
{
    public const int RequiredHpSaved = 1;

    public static bool CanOffer(SearchPolicySnapshot policy, int alreadyUsed, bool won,
        int strategicHpDeficit, int lifeHpDeficit)
        => policy.LowLossPotionEnabled && policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives && alreadyUsed == 0 && won
            && strategicHpDeficit > 0
            && lifeHpDeficit > 0;

    public bool Qualifies(bool won, int explicitUses, int strategicHpDeficit,
        int lifeHpDeficit, int outstandingStolenResource)
        => won && explicitUses == 1
            && strategicHpDeficit < StrategicHpDeficit && lifeHpDeficit < LifeHpDeficit
            && (!PreserveResources || outstandingStolenResource <= OutstandingStolenResource);

    public bool AllowsCandidate(bool won, int explicitUses, int strategicHpDeficit,
        int lifeHpDeficit, int outstandingStolenResource, int strategicPotionCost)
        => won && explicitUses == 1 && (
            Qualifies(won, explicitUses, strategicHpDeficit, lifeHpDeficit, outstandingStolenResource)
            // These routes were already available before adding the low-loss layer.
            || strategicPotionCost == 0
            || PreserveResources && outstandingStolenResource < OutstandingStolenResource);
}
