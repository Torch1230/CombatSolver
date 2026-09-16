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
    /// 边界多样化不变。只由 <see cref="BeamWidthPortfolio" /> 的次段成员置位，默认 false，
    /// 此时保留逻辑逐位不变。
    /// </summary>
    public bool SecondRankBand { get; init; }

    /// <summary>
    /// 中途排序只用状态基础分 <c>node.Score</c>，不加 <c>BeamRankScore</c> 的九项附加分（当前能量、
    /// 持续效果增量、铺垫潜力等）；终局排序与路线比较规则不变。只由 <see cref="BeamWidthPortfolio" />
    /// 的基础分成员置位，默认 false，此时排序逐位不变。
    /// </summary>
    public bool BaseScoreOnly { get; init; }

    public static SolverSearchProfile Default { get; } = new(
        BeamWidth: 60,
        MaxExpandedNodes: 120_000,
        MaxCardBranchesPerNode: 32,
        MaxPileChoiceBranchesPerAction: 18,
        MaxHandChoiceBranchesPerAction: 24,
        SoftTimeBudgetMilliseconds: 120_000);
}
