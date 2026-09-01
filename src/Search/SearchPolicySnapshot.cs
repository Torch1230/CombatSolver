namespace CombatSolver;

internal sealed record SearchPolicySnapshot(
    SolverSearchProfile ShortProfile,
    SolverSearchProfile DeepProfile,
    SolverPotionPolicy PotionPolicy,
    PotionStrategySnapshot PotionStrategy,
    bool DetailedDiagnostics,
    bool VerifyIncrementalSearch,
    bool ForceShortOnly,
    bool MeasurePhasePerformance,
    int MaxDegreeOfParallelism,
    int? ShortBudgetOverrideMilliseconds,
    int? DeepBudgetOverrideMilliseconds,
    bool IncludeTurnSetup,
    SolverTheftPolicy? TheftPolicy,
    BossHpStrategy ActTransitionBossHpStrategy,
    BossHpStrategy FinalBossHpStrategy,
    int AcceptableBattleHpLoss,
    LongTermGoals PursuedLongTermGoals,
    SearchDiagnosticsSink Diagnostics,
    SearchFramePressureSignal FramePressureSignal,
    SearchMemoryPressureSignal MemoryPressureSignal)
{
    public SearchRequestWorkTotals? RequestWorkTotals { get; init; }
    public SearchInteractionState? Interaction { get; init; }
}
