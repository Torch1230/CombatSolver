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
    /// is better. Callers pass strategic HP loss after earned growth credit; realized growth
    /// breaks ties before combat duration, including when every growth budget is zero.
    /// </summary>
    public static int ComparePrimaryQuality(
        bool candidateCompleteVictory,
        int candidateStrategicHpDeficit,
        int? candidateCombatEndedTurn,
        bool currentCompleteVictory,
        int currentStrategicHpDeficit,
        int? currentCombatEndedTurn,
        int candidateGrowthHpCredit = 0,
        int currentGrowthHpCredit = 0,
        int candidateGrowthRewardCount = 0,
        int currentGrowthRewardCount = 0,
        int candidateDeathSaveUseCount = 0,
        int currentDeathSaveUseCount = 0)
        => RouteQualityPolicy.Compare(
            RouteQuality.Primary(candidateCompleteVictory, candidateStrategicHpDeficit,
                candidateCombatEndedTurn, candidateGrowthHpCredit, candidateGrowthRewardCount,
                candidateDeathSaveUseCount),
            RouteQuality.Primary(currentCompleteVictory, currentStrategicHpDeficit,
                currentCombatEndedTurn, currentGrowthHpCredit, currentGrowthRewardCount,
                currentDeathSaveUseCount),
            RouteQualityProjection.Primary);

    public static bool IsBetter(SolverInterimResult candidate, SolverInterimResult current)
        => RouteQualityPolicy.Compare(
            RouteQuality.FromInterim(candidate), RouteQuality.FromInterim(current),
            RouteQualityProjection.Interim) < 0;

    public static bool CanPromoteDisplayedResult(
        SolverInterimResult candidate,
        SolverInterimResult current)
        => (!candidate.Won
                || candidate.Survives && !current.Survives
                || candidate.DeathSaveUseCount < current.DeathSaveUseCount
                || candidate.TheftPolicy == SolverTheftPolicy.PreserveResources
                    && TheftEncounterStrategy.CompareOutstanding(candidate.OutstandingStolenResource,
                        candidate.ForfeitsEncounterGold, current.OutstandingStolenResource,
                        current.ForfeitsEncounterGold) < 0
                || !current.Won
                || candidate.ProjectedBattleHpLost - candidate.GrowthHpCredit
                    <= current.ProjectedBattleHpLost - current.GrowthHpCredit)
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

}
