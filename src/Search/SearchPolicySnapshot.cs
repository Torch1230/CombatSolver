namespace CombatSolver;

internal readonly record struct FatalGrowthSearchTarget(GrowthSource Source, int KillCount);

internal sealed record SearchPolicySnapshot(
    SolverSearchProfile Profile,
    SolverPotionPolicy PotionPolicy,
    PotionStrategySnapshot PotionStrategy,
    bool DetailedDiagnostics,
    bool VerifyIncrementalSearch,
    bool FixedBudget,
    bool MeasurePhasePerformance,
    int MaxDegreeOfParallelism,
    int? BudgetOverrideMilliseconds,
    bool IncludeTurnSetup,
    SolverTheftPolicy? TheftPolicy,
    BossHpStrategy ActTransitionBossHpStrategy,
    BossHpStrategy FinalBossHpStrategy,
    int AcceptableBattleHpLoss,
    SearchDiagnosticsSink Diagnostics,
    SearchFramePressureSignal FramePressureSignal,
    SearchMemoryPressureSignal MemoryPressureSignal)
{
    public bool Act3BossStrategy { get; init; }
    internal static bool IsAct3BossEncounter(int actIndex, string? encounterId)
        => actIndex == 2 && encounterId is "TEST_SUBJECT_BOSS" or "AEONGLASS_BOSS" or "QUEEN_BOSS";
    public GrowthValues GrowthBudgets { get; init; }
    public IReadOnlyList<RelicCounterTarget> RelicTargets { get; init; } = Array.Empty<RelicCounterTarget>();
    public bool RelicTargetsSatisfied(RelicCounterEvaluation value)
        => RelicTargets.All(target => (value.SatisfiedMask & (1UL << (int)target.Id)) != 0);
    public int? BrightestFlameMaxHpLossLimit { get; init; }
    public bool HasGrowthTargets { get; init; }
    public bool StopAtAcceptableBattleHpLoss { get; init; } = true;
    public bool CanStopAtHpTarget => StopAtAcceptableBattleHpLoss && !EffectiveHasGrowthTargets;
    public FatalGrowthSearchTarget? FatalGrowthTarget { get; init; }
    public bool GrowthTargetSatisfied(GrowthValues rewards)
        => CanStopAtHpTarget || StopAtAcceptableBattleHpLoss && FatalGrowthTarget is { } target
            && rewards.Get(target.Source) >= target.KillCount;
    public int MinimumRequiredPotionUses(int alreadyUsed)
        => Math.Max(PotionStrategy.Directives.Count(d => d.Directive == SolverPotionDirective.Force),
            PotionPolicy == SolverPotionPolicy.RequireAtLeastOne && alreadyUsed == 0 ? 1 : 0);

    /// <summary>
    /// 不考虑局外收益。玩家填的额度原样留在 <see cref="GrowthBudgets"/> 里，折算只在
    /// <see cref="EffectiveGrowthBudgets"/> 和 <see cref="EffectiveHasGrowthTargets"/> 这一处做。
    /// </summary>
    public bool IgnoreLongTermRewards { get; init; }

    /// <summary>搜索真正该用的额度。开着「不考虑局外收益」时一律为零。</summary>
    public GrowthValues EffectiveGrowthBudgets => IgnoreLongTermRewards ? default : GrowthBudgets;

    /// <summary>
    /// 搜索真正该看的「牌组里有没有成长目标」。开着「不考虑局外收益」时为假，
    /// 于是「打到可接受战损就提早收手」那条捷径会重新生效——不要收益了，就没有理由继续搜下去。
    /// </summary>
    public bool EffectiveHasGrowthTargets => !IgnoreLongTermRewards && HasGrowthTargets;
    public SearchRequestWorkTotals? RequestWorkTotals { get; init; }
    public SearchInteractionState? Interaction { get; init; }
}
