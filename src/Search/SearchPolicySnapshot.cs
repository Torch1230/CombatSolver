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
    SearchDiagnosticsSink Diagnostics)
{
    public bool PredictPotionReward { get; init; }
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

    /// <summary>转置支配表的合并条目上限；0 表示不设上限。</summary>
    public int TranspositionEntryLimit { get; init; } = DefaultTranspositionEntryLimit;

    internal const int DefaultTranspositionEntryLimit = 1_000_000;

    public bool StopAtAcceptableBattleHpLoss { get; init; } = true;

    public bool CanStopAtHpTarget => StopAtAcceptableBattleHpLoss
        && (!EffectiveHasGrowthTargets || GrowthOpportunityTargets.IsBounded);

    public bool GrowthTargetSatisfied(GrowthValues rewards)
        => CanStopAtHpTarget
            && (!EffectiveHasGrowthTargets || GrowthOpportunityTargets.IsSatisfiedBy(rewards));

    public int MinimumRequiredPotionUses(int alreadyUsed)
        => Math.Max(PotionStrategy.Directives.Count(d => d.Directive == SolverPotionDirective.Force),
            PotionPolicy == SolverPotionPolicy.RequireAtLeastOne && alreadyUsed == 0 ? 1 : 0);

    public bool IgnoreLongTermRewards { get; init; }
    public GrowthValues EffectiveGrowthBudgets => IgnoreLongTermRewards ? default : GrowthBudgets;
    public bool EffectiveHasGrowthTargets => !IgnoreLongTermRewards && HasGrowthTargets;
    public int TranspositionPruningDisabledMask { get; init; }
    public SearchInteractionState? Interaction { get; init; }
}
