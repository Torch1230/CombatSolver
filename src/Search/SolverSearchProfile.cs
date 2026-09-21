namespace CombatSolver;

internal sealed record SolverSearchProfile(
    int BeamWidth,
    int MaxExpandedNodes,
    int MaxCardBranchesPerNode,
    int MaxPileChoiceBranchesPerAction,
    int MaxHandChoiceBranchesPerAction,
    int SoftTimeBudgetMilliseconds)
{
    /// <summary>
    /// 全局剪枝按分数填充普通席位时，不取排名前 W 位而取第 W+1 至 2W 位；必保通道、药水配额和
    /// 边界多样化不变。由普通组合与能力前缀后验的次段成员使用，默认 false，
    /// 此时保留逻辑逐位不变。
    /// </summary>
    public bool SecondRankBand { get; init; }

    // Experimental, explicitly injected frozen model; absent in normal production profiles.
    public ContextualRankingModel? ContextualRanking { get; init; }
    /// <summary>Offline experiment: continuously price lethal intent only at a living, playable new-turn node after EndTurn.</summary>
    public bool ContinuousThreatRanking { get; init; }
    /// <summary>Offline experiment: extend equal-policy tactical tie ordering to uniform-progress base-score boundaries.</summary>
    public bool BaseScoreTacticalTies { get; init; }
    /// <summary>Offline one-factor sensitivity probe; absent in production profiles.</summary>
    public BeamWeightPerturbation? BeamWeightPerturbation { get; init; }
    /// <summary>Offline portfolio experiment: replace the wide refinement with a narrow offensive member.</summary>
    public bool OffensiveRefinementPortfolio { get; init; }
    /// <summary>Offline experiment: append a narrow offensive member using at most one eighth of prior member expansions.</summary>
    public bool BoundedOffensiveRefinementPortfolio { get; init; }
    /// <summary>Honor the player's HP stopping target across portfolios, preserving audits for visible or observed healing.</summary>
    public bool StopPortfolioAtHpTarget { get; init; } = true;

    /// <summary>
    /// 中途排序只用状态基础分 <c>node.Score</c>，不加 <c>BeamRankScore</c> 的各项附加分（当前能量、
    /// 持续效果增量、铺垫潜力等）；终局排序与路线比较规则不变。由普通组合与能力前缀后验
    /// 的基础分成员使用，默认 false，此时排序逐位不变。
    /// </summary>
    public bool BaseScoreOnly { get; init; }

    /// <summary>
    /// 能力偏好完整成员使用更高的承诺席位，但不改变最终评分、状态键或终局比较。
    /// </summary>
    public bool AggressivePowerCommitment { get; init; }

    public static SolverSearchProfile Default { get; } = new(
        BeamWidth: 60,
        MaxExpandedNodes: 120_000,
        MaxCardBranchesPerNode: 32,
        MaxPileChoiceBranchesPerAction: 18,
        MaxHandChoiceBranchesPerAction: 24,
        SoftTimeBudgetMilliseconds: 120_000);
}

internal enum BeamWeightTerm { CurrentEnergy, PersistentBuffDelta, EnemyHp }

internal sealed record BeamWeightPerturbation
{
    public BeamWeightTerm Term { get; }
    public double Scale { get; }

    public BeamWeightPerturbation(BeamWeightTerm term, double scale)
    {
        if (!Enum.IsDefined(term))
            throw new ArgumentOutOfRangeException(nameof(term));
        if (!double.IsFinite(scale) || scale < 0 || scale > 2)
            throw new ArgumentOutOfRangeException(nameof(scale));
        Term = term;
        Scale = scale;
    }
}
