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
    SearchDiagnosticsSink Diagnostics,
    SearchFramePressureSignal FramePressureSignal,
    SearchMemoryPressureSignal MemoryPressureSignal)
{
    public SearchObjectivePolicy Objective { get; init; } = new(SearchObjective.Balanced, 10, 1, 0);
    public GrowthValues GrowthBudgets { get; init; }
    public bool HasGrowthTargets { get; init; }

    /// <summary>
    /// 不考虑局外收益。玩家填的额度原样留在 <see cref="GrowthBudgets"/> 里，折算只在
    /// <see cref="EffectiveGrowthBudgets"/> 和 <see cref="EffectiveHasGrowthTargets"/> 这一处做。
    /// </summary>
    public bool IgnoreLongTermRewards { get; init; }

    public SearchObjectivePolicy EffectiveObjective => IgnoreLongTermRewards && Objective.IsRewardObjective
        ? Objective with { Mode = SearchObjective.Balanced } : Objective;
    public BossHpRelief ResolveStrategicHpRelief(BossHpRelief relief)
        => EffectiveObjective.Mode == SearchObjective.Survival ? BossHpRelief.None
            : ActEndingBossPolicy.ResolveStrategicHpRelief(relief, ActTransitionBossHpStrategy, FinalBossHpStrategy);

    public GrowthValues EffectiveGrowthBudgets => IgnoreLongTermRewards
        || Objective.Mode != SearchObjective.Balanced ? default : GrowthBudgets;

    /// <summary>
    /// 搜索真正该看的「牌组里有没有成长目标」。开着「不考虑局外收益」时为假，
    /// 于是「打到可接受战损就提早收手」那条捷径会重新生效——不要收益了，就没有理由继续搜下去。
    /// </summary>
    public bool EffectiveHasGrowthTargets => EffectiveObjective.IsRewardObjective || !IgnoreLongTermRewards
        && Objective.Mode == SearchObjective.Balanced && HasGrowthTargets;
    public SearchRequestWorkTotals? RequestWorkTotals { get; init; }
    public SearchInteractionState? Interaction { get; init; }
}
