namespace CombatSolver;

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
    public bool UseNoveltyPortfolio { get; init; }
    public NoveltySearchOptions? NoveltySearch { get; init; }
    public NoveltyPortfolioBudget NoveltyBudget { get; init; } = NoveltyPortfolioBudget.Default;
    public bool Act3BossStrategy { get; init; }
    internal static bool IsAct3BossEncounter(int actIndex, string? encounterId)
        => actIndex == 2 && encounterId is "TEST_SUBJECT_BOSS" or "AEONGLASS_BOSS" or "QUEEN_BOSS";
    public GrowthValues GrowthBudgets { get; init; }
    public IReadOnlyList<RelicCounterTarget> RelicTargets { get; init; } = Array.Empty<RelicCounterTarget>();
    public bool RelicTargetsSatisfied(RelicCounterEvaluation value)
        => RelicTargets.All(target => (value.SatisfiedMask & (1UL << (int)target.Id)) != 0);
    public int? BrightestFlameMaxHpLossLimit { get; init; }
    public GrowthOpportunityTargets GrowthOpportunityTargets { get; init; } = GrowthOpportunityTargets.Empty;
    public bool HasGrowthTargets => GrowthOpportunityTargets.HasTargets;
    public bool StopAtAcceptableBattleHpLoss { get; init; } = true;
    public bool CanStopAtHpTarget => StopAtAcceptableBattleHpLoss
        && (!EffectiveHasGrowthTargets || GrowthOpportunityTargets.IsBounded);
    public bool GrowthTargetSatisfied(GrowthValues rewards)
        => CanStopAtHpTarget
            && (!EffectiveHasGrowthTargets || GrowthOpportunityTargets.IsSatisfiedBy(rewards));
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

    /// <summary>
    /// 主搜索改用 <see cref="BeamWidthPortfolio" />：若干个宽度或中途排序不同的成员共享同一份节点预算，
    /// 按既有比较规则取最优。默认开启；关闭时只运行基线成员。
    /// </summary>
    public bool UseBeamWidthPortfolio { get; init; }

    /// <summary>
    /// 组合成员宽度。首项由 <see cref="BeamWidthPortfolio.ProductionMembers" /> 强制成基线宽度；
    /// 为空时用默认的 [基线, 基线×2/3, 基线×3/2, 次段 基线, 基础分 基线]，显式给出时只有宽度成员。
    /// </summary>
    public IReadOnlyList<int>? BeamWidthPortfolioWidths { get; init; }

    /// <summary>
    /// 默认 true。置 false 时不再运行那个只带基线宽度、不带任何排序修饰的组合成员，
    /// 少跑一次真实搜索；候选比较因此不再保证"不差于今天的单次搜索"。
    /// 只由实验与针对性 A/B 置位，生产默认保持 true。
    /// </summary>
    public bool BeamWidthPortfolioPlainBaselineMember { get; init; } = true;

    /// <summary>
    /// 实验用：把指定牌堆在状态键里改成顺序无关（多重集）哈希，让只差这些牌堆顺序的两个状态
    /// 落进同一条转置记录。位：手牌=1，抽牌堆=2，弃牌堆=4，消耗堆=8；默认 0，生产逐位不变。
    /// 只有「本场战斗没有任何效果按位置读该牌堆」时那个位才成立：抽牌堆每次抽牌都读顶端，
    /// 手牌会被随机取牌与“第一张可打出”按位置读，所以实际可用的通常只有弃牌堆与消耗堆。
    /// </summary>
    public int PileOrderInvariantMask { get; init; }

    internal BeamPortfolioExperiment? PortfolioExperiment { get; init; }

    /// <summary>
    /// 请求级的组合诊断，由 <see cref="CombatSearchCoordinator.Solve" /> 建立并挂到返回结果上。
    /// 开关关闭时同样记录（单成员一行）。
    /// </summary>
    public BeamWidthPortfolioTelemetry? PortfolioTelemetry { get; init; }
    public SearchRequestWorkTotals? RequestWorkTotals { get; init; }
    public SearchInteractionState? Interaction { get; init; }
}
