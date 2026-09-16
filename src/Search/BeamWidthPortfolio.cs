namespace CombatSolver;

/// <summary>
/// 一个组合成员的定义：Beam 宽度，以及排序方式。次段成员的宽度与基线相同，但全局剪枝的普通席位取
/// 分数排名第 W+1 至 2W 位（见 <see cref="SolverSearchProfile.SecondRankBand" />）；基础分成员的宽度也与
/// 基线相同，但中途排序只用基础分（见 <see cref="SolverSearchProfile.BaseScoreOnly" />）。
/// </summary>
internal readonly record struct BeamWidthPortfolioMemberSpec(
    int BeamWidth,
    bool SecondRankBand = false,
    bool BaseScoreOnly = false)
{
    public override string ToString()
        => BeamWidth + (SecondRankBand ? "+band" : string.Empty) + (BaseScoreOnly ? "+base" : string.Empty);
}

/// <summary>
/// 一个组合成员跑完之后的实测结果。展开数、转移数、终止原因和终局判定都由调用方按它自己
/// 已有的口径给出：生产路径读 <c>SearchRequestWorkTotals</c> 的增量和
/// <c>SolverResult.BoundaryReason</c>。组合器不重算这些量，也不碰状态键或评分；成员之间的差别
/// 全部通过 <see cref="SolverSearchProfile" /> 表达（宽度、节点上限、次段标志）。
/// </summary>
internal readonly record struct BeamWidthPortfolioRun<TResult>(
    TResult Result,
    long ExpandedNodes,
    long TransitionCount,
    string Termination,
    bool Terminal,
    bool Won,
    int? BattleHpLost,
    int PotionCount)
{
    /// <summary>
    /// 接管、路线采纳这类不属于一次普通搜索完成的结果。置位时该成员直接成为选中结果，
    /// 后续成员不再运行，保持协调器遇到非 SearchCompletion 时立刻返回的既有规则。
    /// </summary>
    public bool StopPortfolio { get; init; }
}

/// <summary>一个成员的明细；未运行的成员也保留一行，附不运行的原因。</summary>
internal sealed record BeamWidthPortfolioMember(
    int BeamWidth,
    bool SecondRankBand,
    bool BaseScoreOnly,
    int NodeBudget,
    bool Ran,
    long ExpandedNodes,
    long TransitionCount,
    string? Termination,
    bool? Terminal,
    bool? Won,
    int? BattleHpLost,
    int? PotionCount,
    bool Compared,
    string? SkippedReason);

internal sealed record BeamWidthPortfolioOutcome<TResult>(
    TResult Selected,
    int SelectedIndex,
    string SelectionReason,
    IReadOnlyList<BeamWidthPortfolioMember> Members,
    long TotalExpandedNodes,
    long TotalTransitionCount);

/// <summary>
/// 按顺序在同一个根上跑若干个成员，共享一份节点预算，取最优结果。成员之间只有 Beam 宽度不同，
/// 或者是与基线同宽度、只改中途排序的成员：「次段」（普通席位取分数排名第 W+1 至 2W 位）或
/// 「基础分」（排序不加附加分）。
/// </summary>
/// <remarks>
/// <para>
/// 动机是已测到的噪声：同一根只改 Beam 宽度就能让结果双向变化，且没有"更宽必然更好"的方向性
/// （数据来源见 <c>docs/strategy/beam-width-portfolio.md</c>）。既然宽度差一格不是单调的，
/// 那就把若干次搜索当成若干个抽样，按既有比较规则整条选优。
/// </para>
/// <para>
/// 预算是**共享**的：第一个成员拿到全部上限，跑完按它的实际展开数扣减，后一个成员的
/// <see cref="SolverSearchProfile.MaxExpandedNodes" /> 就是剩下的那部分，扣到零即停。
/// 因此成员列表只有一项时，它的 Profile 与直接求解逐位相同。
/// </para>
/// <para>
/// 组合器本身不含任何比较规则：<c>isBetter</c> 由调用方传入既有的
/// <c>IsBetterPotionPolicyResult</c>，同分沿用"先出现者胜"，也就是列表首项（基线宽度）。
/// </para>
/// </remarks>
internal static class BeamWidthPortfolio
{
    /// <summary>预算已经扣光，该成员没有运行。</summary>
    internal const string SkippedBudgetExhausted = "BudgetExhausted";

    /// <summary>撞节点上限而且没打到终局：这一条不是完整结果，不参与比较。</summary>
    internal const string SkippedNodeLimitNotTerminal = "NodeLimitNotTerminal";

    /// <summary>与 <c>SearchBoundaryReason.NodeLimit</c> 的名称一致。</summary>
    internal const string NodeLimitTermination = "NodeLimit";

    /// <summary>没有任何成员可比时退回第一个真正跑过的成员。</summary>
    internal const string SelectionBaselineFallback = "BaselineFallback";

    internal const string SelectionBest = "Best";
    internal const string SelectionStopped = "StopPortfolio";

    /// <summary>默认成员相对基线宽度的比例：先窄后宽。</summary>
    internal const double NarrowRefinementRatio = 2d / 3d;
    internal const double WideRefinementRatio = 3d / 2d;

    /// <summary>
    /// 生产成员列表。首项强制是基线宽度（基线成员必须逐位等于今天的单次搜索），其后按给定顺序
    /// 去重追加，丢掉小于 1 的值。<paramref name="configuredWidths" /> 为空时用默认的
    /// [基线, 基线×2/3, 基线×3/2, 次段 基线, 基础分 基线]（四舍五入，例如基线 24 是
    /// [24, 16, 36, 24+band, 24+base]，基线 135 是 [135, 90, 203, 135+band, 135+base]）；
    /// 显式给出宽度列表时只有宽度成员，不追加次段与基础分成员。
    /// </summary>
    internal static IReadOnlyList<BeamWidthPortfolioMemberSpec> ProductionMembers(
        int baselineBeamWidth,
        IReadOnlyList<int>? configuredWidths)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baselineBeamWidth);
        List<BeamWidthPortfolioMemberSpec> members = [new(baselineBeamWidth)];
        if (configuredWidths is { Count: > 0 })
        {
            foreach (int width in configuredWidths)
            {
                BeamWidthPortfolioMemberSpec member = new(width);
                if (width >= 1 && !members.Contains(member))
                    members.Add(member);
            }
            return members;
        }
        foreach (int width in new[]
                 {
                     ScaledWidth(baselineBeamWidth, NarrowRefinementRatio),
                     ScaledWidth(baselineBeamWidth, WideRefinementRatio),
                 })
        {
            BeamWidthPortfolioMemberSpec member = new(width);
            if (!members.Contains(member))
                members.Add(member);
        }
        members.Add(new BeamWidthPortfolioMemberSpec(baselineBeamWidth, SecondRankBand: true));
        members.Add(new BeamWidthPortfolioMemberSpec(baselineBeamWidth, BaseScoreOnly: true));
        return members;
    }

    internal static int ScaledWidth(int baselineBeamWidth, double ratio)
        => Math.Max(1, (int)Math.Round(baselineBeamWidth * ratio, MidpointRounding.AwayFromZero));

    /// <summary>
    /// 次段成员的普通席位（由保留策略的全局剪枝调用）：把分数降序的 <paramref name="ranked" /> 前
    /// <paramref name="bandWidth" /> 位挪到队尾，随后按 <paramref name="bandWidth" /> 截断时留下的就是
    /// 第 W+1 至 2W 位；候选不足 2W 个时，挪走的那段按原顺序回填。候选不超过 W 个时本来一个不砍，保持不动。
    /// </summary>
    internal static void MoveLeadingBandToTail<T>(List<T> ranked, int bandWidth)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        if (bandWidth <= 0 || ranked.Count <= bandWidth)
            return;
        List<T> leading = ranked.GetRange(0, bandWidth);
        ranked.RemoveRange(0, bandWidth);
        ranked.AddRange(leading);
    }

    /// <param name="memberSpecs">成员定义，首项为基线宽度。</param>
    /// <param name="sharedMaxExpandedNodes">全部成员共用的节点上限。</param>
    /// <param name="baseProfile">除 Beam 宽度、排序方式标志和节点上限外，每个成员都照抄这份 Profile。</param>
    /// <param name="solve">按 Profile 求解并报告实测工作量与终止方式。</param>
    /// <param name="isBetter">既有比较规则；严格更优才换人，因此同分保留先出现的成员。</param>
    /// <param name="rejectMember">
    /// 合同拒绝钩子：返回非空即表示该成员不被允许（生产路径传的是
    /// <see cref="BeamWidthPortfolioGate.RejectRefinement" />）。被拒绝的成员不运行、不花预算，但保留明细行。
    /// </param>
    internal static BeamWidthPortfolioOutcome<TResult> Run<TResult>(
        IReadOnlyList<BeamWidthPortfolioMemberSpec> memberSpecs,
        int sharedMaxExpandedNodes,
        SolverSearchProfile baseProfile,
        Func<SolverSearchProfile, BeamWidthPortfolioRun<TResult>> solve,
        Func<TResult, TResult, bool> isBetter,
        Func<BeamWidthPortfolioMemberSpec, string?>? rejectMember = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(memberSpecs);
        ArgumentNullException.ThrowIfNull(baseProfile);
        ArgumentNullException.ThrowIfNull(solve);
        ArgumentNullException.ThrowIfNull(isBetter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sharedMaxExpandedNodes);
        if (memberSpecs.Count == 0)
            throw new ArgumentException("组合器至少需要一个成员。", nameof(memberSpecs));
        for (int index = 0; index < memberSpecs.Count; index++)
        {
            if (memberSpecs[index].BeamWidth <= 0)
                throw new ArgumentOutOfRangeException(nameof(memberSpecs), "成员 Beam 宽度必须为正。");
        }

        List<BeamWidthPortfolioMember> members = new(memberSpecs.Count);
        long remainingNodes = sharedMaxExpandedNodes;
        long totalExpanded = 0;
        long totalTransitions = 0;
        TResult? selected = default;
        int selectedIndex = -1;
        TResult? firstRan = default;
        int firstRanIndex = -1;
        bool stopped = false;

        for (int index = 0; index < memberSpecs.Count; index++)
        {
            BeamWidthPortfolioMemberSpec spec = memberSpecs[index];
            if (rejectMember?.Invoke(spec) is { } rejection)
            {
                members.Add(Skipped(spec, rejection));
                continue;
            }
            if (remainingNodes <= 0)
            {
                members.Add(Skipped(spec, SkippedBudgetExhausted));
                continue;
            }

            SolverSearchProfile memberProfile = baseProfile with
            {
                BeamWidth = spec.BeamWidth,
                SecondRankBand = spec.SecondRankBand,
                BaseScoreOnly = spec.BaseScoreOnly,
                MaxExpandedNodes = (int)remainingNodes,
            };
            BeamWidthPortfolioRun<TResult> run = solve(memberProfile);
            ArgumentOutOfRangeException.ThrowIfNegative(run.ExpandedNodes);
            ArgumentOutOfRangeException.ThrowIfNegative(run.TransitionCount);
            ArgumentNullException.ThrowIfNull(run.Termination);
            remainingNodes -= run.ExpandedNodes;
            totalExpanded += run.ExpandedNodes;
            totalTransitions += run.TransitionCount;
            if (firstRanIndex < 0)
            {
                firstRan = run.Result;
                firstRanIndex = index;
            }

            if (run.StopPortfolio)
            {
                members.Add(Ran(spec, memberProfile.MaxExpandedNodes, run, compared: true, skippedReason: null));
                selected = run.Result;
                selectedIndex = index;
                stopped = true;
                break;
            }

            bool comparable = run.Terminal
                || !string.Equals(run.Termination, NodeLimitTermination, StringComparison.Ordinal);
            if (comparable && (selectedIndex < 0 || isBetter(run.Result, selected!)))
            {
                selected = run.Result;
                selectedIndex = index;
            }
            members.Add(Ran(spec, memberProfile.MaxExpandedNodes, run,
                compared: comparable,
                skippedReason: comparable ? null : SkippedNodeLimitNotTerminal));
            // 顺序执行的代价只有在上一位成员真的放手之后才成立：明细已经记完，这里把这一轮的
            // 结果引用清掉，别让它活到下一位成员跑完。
            run = default;
            // 回退只发生在「没有任何成员可比」时；一旦选出了可比的结果，首个成员就没人再用。
            if (selectedIndex >= 0)
                firstRan = default;
        }

        string selectionReason;
        if (stopped)
        {
            selectionReason = SelectionStopped;
        }
        else if (selectedIndex < 0)
        {
            if (firstRanIndex < 0)
                throw new InvalidOperationException("组合器没有任何成员运行；预算或成员合同拒绝了全部宽度。");
            selected = firstRan;
            selectedIndex = firstRanIndex;
            selectionReason = SelectionBaselineFallback;
        }
        else
        {
            selectionReason = SelectionBest;
        }

        log?.Invoke(
            $"[CombatSolver/Test] BEAM_WIDTH_PORTFOLIO result " +
            $"members={memberSpecs.Count} ran={members.Count(member => member.Ran)} " +
            $"compared={members.Count(member => member.Compared)} " +
            $"selected_index={selectedIndex} selected_beam={members[selectedIndex].BeamWidth} " +
            $"selected_second_rank_band={members[selectedIndex].SecondRankBand} " +
            $"selected_base_score_only={members[selectedIndex].BaseScoreOnly} " +
            $"reason={selectionReason} shared_nodes={sharedMaxExpandedNodes} " +
            $"total_expanded={totalExpanded} total_transitions={totalTransitions}");
        return new BeamWidthPortfolioOutcome<TResult>(
            selected!,
            selectedIndex,
            selectionReason,
            members,
            totalExpanded,
            totalTransitions);

        static BeamWidthPortfolioMember Skipped(BeamWidthPortfolioMemberSpec spec, string reason)
            => new(spec.BeamWidth, spec.SecondRankBand, spec.BaseScoreOnly, 0, false, 0, 0, null, null, null, null, null, false, reason);

        static BeamWidthPortfolioMember Ran(
            BeamWidthPortfolioMemberSpec spec,
            int nodeBudget,
            BeamWidthPortfolioRun<TResult> run,
            bool compared,
            string? skippedReason)
            => new(spec.BeamWidth, spec.SecondRankBand, spec.BaseScoreOnly, nodeBudget, true, run.ExpandedNodes, run.TransitionCount,
                run.Termination, run.Terminal, run.Won, run.BattleHpLost, run.PotionCount,
                compared, skippedReason);
    }
}
