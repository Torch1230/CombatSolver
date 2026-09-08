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
        SearchObjectiveOutcome candidateObjective = default,
        SearchObjectiveOutcome currentObjective = default)
    {
        int comparison = currentCompleteVictory.CompareTo(candidateCompleteVictory);
        if (comparison != 0)
            return comparison;
        comparison = candidateObjective.CompareTo(currentObjective);
        if (comparison != 0) return comparison;
        comparison = candidateStrategicHpDeficit.CompareTo(currentStrategicHpDeficit);
        if (comparison != 0)
            return comparison;
        comparison = currentGrowthHpCredit.CompareTo(candidateGrowthHpCredit);
        if (comparison != 0)
            return comparison;
        comparison = currentGrowthRewardCount.CompareTo(candidateGrowthRewardCount);
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
        comparison = candidate.Objective.CompareTo(current.Objective);
        if (comparison != 0) return comparison < 0;
        if (candidate.Objective.TargetReached && current.Objective.TargetReached)
        {
            comparison = ComparePrimaryQuality(candidate.Won, candidate.StrategicHpDeficit, candidate.CombatEndedTurn,
                current.Won, current.StrategicHpDeficit, current.CombatEndedTurn);
            if (comparison != 0) return comparison < 0;
        }
        if (candidate.OutstandingStolenResource != current.OutstandingStolenResource)
            return candidate.OutstandingStolenResource < current.OutstandingStolenResource;
        if (IsResourceTradeImprovement(candidate, current))
            return true;
        if (IsResourceTradeImprovement(current, candidate))
            return false;
        if (candidate.GrowthHpCredit != current.GrowthHpCredit)
            return candidate.GrowthHpCredit > current.GrowthHpCredit;
        if (candidate.GrowthRewardCount != current.GrowthRewardCount)
            return candidate.GrowthRewardCount > current.GrowthRewardCount;
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
        => (candidate.Objective.Policy.IsRewardObjective || !candidate.Won
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

    private static bool IsResourceTradeImprovement(
        SolverInterimResult candidate,
        SolverInterimResult current)
        => IsResourceTradeImprovement(
            candidate.StrategicHpDeficit,
            candidate.PotionStrategicCost,
            current.StrategicHpDeficit,
            current.PotionStrategicCost);
}
