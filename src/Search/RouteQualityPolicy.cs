namespace CombatSolver;

internal enum RouteQualityProjection
{
    Primary,
    Interim,
    PotionPolicy,
    RetentionCost,
}

internal static class RouteQualityPolicy
{
    public static int Compare(
        in RouteQuality candidate,
        in RouteQuality current,
        RouteQualityProjection projection,
        SolverTheftPolicy? theftPolicy = null)
    {
        if (projection == RouteQualityProjection.RetentionCost)
        {
            int risk = candidate.RetentionHealthRisk.CompareTo(current.RetentionHealthRisk);
            return risk != 0 ? risk : candidate.PotionStrategicCost.CompareTo(current.PotionStrategicCost);
        }
        if (projection == RouteQualityProjection.Primary)
            return ComparePrimary(candidate, current);

        int comparison = current.Won.CompareTo(candidate.Won);
        if (comparison != 0)
            return comparison;
        comparison = current.Survives.CompareTo(candidate.Survives);
        if (comparison != 0)
            return comparison;
        comparison = candidate.DeathSaveUseCount.CompareTo(current.DeathSaveUseCount);
        if (comparison != 0)
            return comparison;

        if (projection == RouteQualityProjection.Interim)
        {
            if (candidate.TheftPolicy == SolverTheftPolicy.PreserveResources
                && candidate.OutstandingStolenResource != current.OutstandingStolenResource)
                return candidate.OutstandingStolenResource.CompareTo(current.OutstandingStolenResource);
            if (SolverInterimResultOrdering.IsResourceTradeImprovement(
                    candidate.StrategicHpDeficit, candidate.PotionStrategicCost,
                    current.StrategicHpDeficit, current.PotionStrategicCost))
                return -1;
            if (SolverInterimResultOrdering.IsResourceTradeImprovement(
                    current.StrategicHpDeficit, current.PotionStrategicCost,
                    candidate.StrategicHpDeficit, candidate.PotionStrategicCost))
                return 1;
            comparison = current.GrowthHpCredit.CompareTo(candidate.GrowthHpCredit);
            if (comparison != 0)
                return comparison;
            comparison = current.GrowthRewardCount.CompareTo(candidate.GrowthRewardCount);
            if (comparison != 0)
                return comparison;
            if (candidate.StrategicHpDeficit == current.StrategicHpDeficit
                && candidate.PotionStrategicCost == current.PotionStrategicCost
                && candidate.ProjectedBattlePotionCount == current.ProjectedBattlePotionCount
                && candidate.ProjectedBattleHpLost != current.ProjectedBattleHpLost)
                return candidate.ProjectedBattleHpLost.CompareTo(current.ProjectedBattleHpLost);
            comparison = (candidate.CombatEndedTurn ?? int.MaxValue)
                .CompareTo(current.CombatEndedTurn ?? int.MaxValue);
            if (comparison != 0)
                return comparison;
            comparison = candidate.ProjectedBattlePotionCount.CompareTo(current.ProjectedBattlePotionCount);
            if (comparison != 0)
                return comparison;
            comparison = candidate.EnemyHp.CompareTo(current.EnemyHp);
            return comparison != 0 ? comparison : CompareScore(candidate.Score, current.Score);
        }

        comparison = TheftEncounterStrategy.CompareRecovery(theftPolicy,
            candidate.Won, candidate.OutstandingStolenResource,
            current.Won, current.OutstandingStolenResource);
        if (comparison != 0)
            return comparison;

        if (projection == RouteQualityProjection.PotionPolicy)
        {
            long candidateCost = (long)candidate.StrategicHpDeficit + candidate.PotionStrategicCost;
            long currentCost = (long)current.StrategicHpDeficit + current.PotionStrategicCost;
            comparison = candidateCost.CompareTo(currentCost);
            if (comparison != 0)
                return comparison;
        }

        if (projection == RouteQualityProjection.PotionPolicy
            && candidate.Won && current.Won
            && candidate.StrategicHpDeficit == current.StrategicHpDeficit
            && candidate.PotionStrategicCost == current.PotionStrategicCost
            && candidate.ProjectedBattlePotionCount == current.ProjectedBattlePotionCount
            && candidate.GrowthHpCredit == current.GrowthHpCredit
            && candidate.GrowthRewardCount == current.GrowthRewardCount
            && candidate.ProjectedBattleHpLost != current.ProjectedBattleHpLost)
            return candidate.ProjectedBattleHpLost.CompareTo(current.ProjectedBattleHpLost);

        comparison = ComparePrimary(candidate, current);
        if (comparison != 0)
            return comparison;
        if (theftPolicy == SolverTheftPolicy.PreserveResources
            && candidate.OutstandingStolenResource != current.OutstandingStolenResource)
            return candidate.OutstandingStolenResource.CompareTo(current.OutstandingStolenResource);
        comparison = candidate.ProjectedBattlePotionCount.CompareTo(current.ProjectedBattlePotionCount);
        return comparison != 0 ? comparison : CompareScore(candidate.Score, current.Score);
    }

    private static int ComparePrimary(in RouteQuality candidate, in RouteQuality current)
    {
        int comparison = current.Won.CompareTo(candidate.Won);
        if (comparison != 0)
            return comparison;
        comparison = candidate.DeathSaveUseCount.CompareTo(current.DeathSaveUseCount);
        if (comparison != 0)
            return comparison;
        comparison = candidate.StrategicHpDeficit.CompareTo(current.StrategicHpDeficit);
        if (comparison != 0)
            return comparison;
        comparison = current.GrowthHpCredit.CompareTo(candidate.GrowthHpCredit);
        if (comparison != 0)
            return comparison;
        comparison = current.GrowthRewardCount.CompareTo(candidate.GrowthRewardCount);
        return comparison != 0 ? comparison : (candidate.CombatEndedTurn ?? int.MaxValue)
            .CompareTo(current.CombatEndedTurn ?? int.MaxValue);
    }

    private static int CompareScore(double candidate, double current)
        => candidate > current ? -1 : current > candidate ? 1 : 0;
}
