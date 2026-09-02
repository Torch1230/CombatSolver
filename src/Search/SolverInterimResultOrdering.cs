namespace CombatSolver;

internal static class SolverInterimResultOrdering
{
    public static bool IsCompleteVictory(
        int actionCount,
        bool allEnemiesDead,
        bool playerDead,
        int projectedPlayerHp)
        => actionCount > 0
            && allEnemiesDead
            && !playerDead
            && projectedPlayerHp > 0;

    public static bool IsBetter(SolverInterimResult candidate, SolverInterimResult current)
    {
        if (candidate.Won != current.Won)
            return candidate.Won;
        if (candidate.OutstandingStolenResource != current.OutstandingStolenResource)
            return candidate.OutstandingStolenResource < current.OutstandingStolenResource;
        if (IsResourceTradeImprovement(candidate, current))
            return true;
        if (IsResourceTradeImprovement(current, candidate))
            return false;
        if (candidate.ProjectedBattlePotionCount != current.ProjectedBattlePotionCount)
            return candidate.ProjectedBattlePotionCount < current.ProjectedBattlePotionCount;
        if (candidate.EnemyHp != current.EnemyHp)
            return candidate.EnemyHp < current.EnemyHp;
        if (candidate.Won && candidate.CombatEndedTurn != current.CombatEndedTurn)
        {
            return (candidate.CombatEndedTurn ?? int.MaxValue)
                < (current.CombatEndedTurn ?? int.MaxValue);
        }
        return candidate.Score > current.Score;
    }

    public static bool CanPromoteDisplayedResult(
        SolverInterimResult candidate,
        SolverInterimResult current)
        => (!candidate.Won
                || !current.Won
                || candidate.ProjectedBattleHpLost <= current.ProjectedBattleHpLost)
            && IsBetter(candidate, current);

    internal static bool IsResourceTradeImprovement(
        int candidateHpDeficit,
        int candidatePotionCost,
        int currentHpDeficit,
        int currentPotionCost)
    {
        int candidateBurden = checked(candidateHpDeficit + candidatePotionCost);
        int currentBurden = checked(currentHpDeficit + currentPotionCost);
        return candidateBurden < currentBurden
            || candidateBurden == currentBurden && candidateHpDeficit < currentHpDeficit;
    }

    private static bool IsResourceTradeImprovement(
        SolverInterimResult candidate,
        SolverInterimResult current)
        => IsResourceTradeImprovement(
            candidate.StrategicHpDeficit,
            candidate.PotionStrategicCost,
            current.StrategicHpDeficit,
            current.PotionStrategicCost);
}
