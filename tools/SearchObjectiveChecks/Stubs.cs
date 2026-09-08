// DTO only; production comparison code is linked by the project.
namespace CombatSolver;

internal sealed record SolverInterimResult(
    bool Won,
    int OutstandingStolenResource,
    int ProjectedBattleHpLost,
    int StrategicHpDeficit,
    int PotionStrategicCost,
    int ProjectedBattlePotionCount,
    int EnemyHp,
    double Score,
    int? CombatEndedTurn = null)
{
    public SearchObjectiveOutcome Objective { get; init; }
    public int GrowthHpCredit { get; init; }
    public int GrowthRewardCount { get; init; }
}
