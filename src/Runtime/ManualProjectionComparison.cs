namespace CombatSolver;

internal sealed record ManualProjectionBaseline(
    int StartTurnNumber,
    int ProjectedBattleHpLost,
    int ProjectedBattlePotionCount,
    string StateDifference,
    string? OriginalCheckpointId = null);

internal sealed record ManualProjectionComparison(
    int OriginalTurnNumber,
    int CurrentTurnNumber,
    int PreviousProjectedBattleHpLost,
    int CurrentProjectedBattleHpLost,
    int PreviousProjectedBattlePotionCount,
    int CurrentProjectedBattlePotionCount,
    string StateDifference,
    string? OriginalCheckpointId = null,
    string? CurrentCheckpointId = null)
{
    public int Difference => CurrentProjectedBattleHpLost - PreviousProjectedBattleHpLost;
    public int AdditionalPotionCount => Math.Max(0,
        CurrentProjectedBattlePotionCount - PreviousProjectedBattlePotionCount);
    public int PotionHpCost => checked(AdditionalPotionCount * 9);
    public int PotionAdjustedHpReduction => checked(-Difference - PotionHpCost);
    public bool IsImprovement => Difference < 0 && PotionAdjustedHpReduction >= 0;
}
