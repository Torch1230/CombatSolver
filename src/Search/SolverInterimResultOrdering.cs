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

    /// <summary>
    /// Compares the result-quality prefix shared by in-session selection, final candidate
    /// retention, and cross-session audits. A negative value means <paramref name="candidate"/>
    /// is better. Policy/resource preferences are deliberately excluded: callers may use them
    /// only after complete victory, strategic battle loss, and combat duration are equal.
    /// </summary>
    public static int ComparePrimaryQuality(
        bool candidateCompleteVictory,
        int candidateStrategicHpDeficit,
        int? candidateCombatEndedTurn,
        bool currentCompleteVictory,
        int currentStrategicHpDeficit,
        int? currentCombatEndedTurn)
    {
        int comparison = currentCompleteVictory.CompareTo(candidateCompleteVictory);
        if (comparison != 0)
            return comparison;
        comparison = candidateStrategicHpDeficit.CompareTo(currentStrategicHpDeficit);
        if (comparison != 0)
            return comparison;
        return (candidateCombatEndedTurn ?? int.MaxValue)
            .CompareTo(currentCombatEndedTurn ?? int.MaxValue);
    }

    public static bool IsBetter(SolverInterimResult candidate, SolverInterimResult current)
    {
        int comparison = current.Won.CompareTo(candidate.Won);
        if (comparison != 0)
            return comparison < 0;
        if (candidate.OutstandingStolenResource != current.OutstandingStolenResource)
            return candidate.OutstandingStolenResource < current.OutstandingStolenResource;
        if (IsResourceTradeImprovement(candidate, current))
            return true;
        if (IsResourceTradeImprovement(current, candidate))
            return false;
        comparison = (candidate.CombatEndedTurn ?? int.MaxValue)
            .CompareTo(current.CombatEndedTurn ?? int.MaxValue);
        if (comparison != 0)
            return comparison < 0;
        if (candidate.ProjectedBattlePotionCount != current.ProjectedBattlePotionCount)
            return candidate.ProjectedBattlePotionCount < current.ProjectedBattlePotionCount;
        if (candidate.EnemyHp != current.EnemyHp)
            return candidate.EnemyHp < current.EnemyHp;
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
