namespace CombatSolver;

internal sealed record BossTempoSearchOptions(int DiscrepancyAllowance)
{
    internal int ScoutTurns { get; init; }
    internal Action<BossTempoPrefix>? PrefixObserver { get; init; }
    internal static SolverSearchProfile AdditionalBudget(SolverSearchProfile configured, int milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(milliseconds);
        return configured with
        {
            MaxExpandedNodes = Math.Max(1, configured.MaxExpandedNodes / 5),
            SoftTimeBudgetMilliseconds = Math.Max(1, milliseconds / 5),
        };
    }

    internal static double RankBase(double score, int cumulativeLoss, int soldHp,
        int projectedHp, bool playerDead)
    {
        if (playerDead)
            return score;
        if (projectedHp <= 0)
            score = score - SolverWeights.DeathPenalty + projectedHp * SolverWeights.Hp;
        return score + cumulativeLoss * SolverWeights.Hp - soldHp * SolverWeights.SoldHpPenalty;
    }
}

internal sealed record BossTempoSearchTelemetry(
    string Stop, int Iterations, long Expanded, long Transitions, long ChoiceBranches,
    long ElapsedMilliseconds, bool Improved);

internal sealed record BossTempoIterationTelemetry(string Stop, int Discrepancies, int Deferred, int PeakPending);

// State=null 表示非观测合成前缀（如主搜轨迹注入），不参与去重键。
internal sealed record BossTempoPrefix(PlanAction[] Actions, StateFingerprint? State,
    int EliminatedEnemies, int PotionCount, double Rank);
