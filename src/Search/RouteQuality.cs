namespace CombatSolver;

internal readonly record struct RouteQuality(
    bool Won,
    bool Survives,
    int DeathSaveUseCount,
    int OutstandingStolenResource,
    int StrategicHpDeficit,
    int GrowthHpCredit,
    int GrowthRewardCount,
    int? CombatEndedTurn,
    int ProjectedBattleHpLost,
    int PotionStrategicCost,
    int ProjectedBattlePotionCount,
    int EnemyHp,
    double Score,
    SolverTheftPolicy? TheftPolicy)
{
    public long RetentionHealthRisk { get; init; }
    public bool ForfeitsEncounterGold { get; init; }

    public static RouteQuality ForRetention(long healthRisk, int potionStrategicCost)
        => new() { RetentionHealthRisk = healthRisk, PotionStrategicCost = potionStrategicCost };

    public static RouteQuality FromInterim(SolverInterimResult result)
        => new(
            result.Won,
            result.Survives,
            result.DeathSaveUseCount,
            result.OutstandingStolenResource,
            result.StrategicHpDeficit,
            result.GrowthHpCredit,
            result.GrowthRewardCount,
            result.CombatEndedTurn,
            result.ProjectedBattleHpLost,
            result.PotionStrategicCost,
            result.ProjectedBattlePotionCount,
            result.EnemyHp,
            result.Score,
            result.TheftPolicy)
        {
            ForfeitsEncounterGold = result.ForfeitsEncounterGold,
        };

    public static RouteQuality Primary(
        bool won,
        int strategicHpDeficit,
        int? combatEndedTurn,
        int growthHpCredit = 0,
        int growthRewardCount = 0,
        int deathSaveUseCount = 0)
        => new(won, false, deathSaveUseCount, 0, strategicHpDeficit,
            growthHpCredit, growthRewardCount, combatEndedTurn, 0, 0, 0, 0, 0, null);
}
