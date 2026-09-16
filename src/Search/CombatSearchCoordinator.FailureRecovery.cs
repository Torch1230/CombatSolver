using System.Diagnostics;

namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    /// <summary>
    /// 整份请求一条胜利路线都没找到、而玩家配的时间预算还剩一大截时，把搜索面和工作量帽
    /// 一起翻倍再搜一轮。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 仅对没有完整胜利且未被玩家接管的请求补搜；停止请求仍由既有检查处理，搜索异常不在此吞掉。
    /// 每轮从同一根重新搜索，按原配置倍增 Beam、节点及选择分支上限，时间只取请求剩余量。
    /// 上一轮耗时乘以扩展倍数只是启动下一轮的估计门槛，不保证下一轮耗时或质量。
    /// 没有严格改善则停止；比较和选用仍沿用既有终局质量规则。
    /// </para>
    /// <para>
    /// 为什么两边一起翻（历史实测，2026-09，极高档还是 50 000 节点的时期；当前四档为
    /// 60 000 / 120 000 / 250 000 / 500 000，数字只作量级参考）：
    /// <see cref="SolverSearchProfile.MaxExpandedNodes" /> 是工作量帽，不是搜索地平线，可它在长战斗里
    /// 总是先到。一场 8 回合 Boss 战里 85 次回合层截断全部是 <c>reason=nodes</c>，<c>reason=time</c>
    /// 一次都没有，时间预算只用掉 5%–30%：节点预算要按
    /// <see cref="SolverWeights.BossEnemyStrengthSuppressionHorizon" /> 摊到每个回合层，而时间那一侧摊完
    /// 还很宽裕。同一个检查点上量过五组，Beam 和节点是乘的关系：
    /// </para>
    /// <list type="bullet">
    /// <item>Beam 90 / 25 000：输。主搜索在 2 701–5 206 个节点上就把前沿走空了。</item>
    /// <item>Beam 90 / 50 000：输，而且主搜索展开的节点数一个不变——花不掉。</item>
    /// <item>Beam 135 / 50 000：输。这个 Beam 下找到胜利需要 83 423 个节点。</item>
    /// <item>Beam 135 / 100 000：赢（两瓶药、第 9 回合斩杀、剩 1 血），用了 83 423 个节点。</item>
    /// <item>Beam 512 / 100 000：赢（第 8 回合斩杀、剩 3 血），只用了 26 671 个节点。</item>
    /// </list>
    /// <para>
    /// 所以两边必须一起抬：只抬节点，窄 Beam 花不掉；只抬 Beam，节点又不够。
    /// </para>
    /// <para>
    /// 两条触发路径：整份请求**一条胜利路线都没有**（原有行为，逐位不变），或者已经有胜利
    /// 但战略战损仍不低于 <see cref="SolverWeights.HighLossEscalationMinimumHp" /> 且超过
    /// 玩家设置的可接受战损（高损胜利重搜）。后者让大战损局面不再只在同一条分支上继续
    /// 微调，而是用更宽的搜索面去找结构不同的路线；两条路径都只在整轮严格变好时采用结果。
    /// 触发时多花的，正是玩家在档位里配了却一直没被用掉的那段时间。
    /// </para>
    /// </remarks>
    internal static SolverResult EscalateSearchWhenNoVictory(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverSearchProfile configured,
        Stopwatch requestClock,
        SolverResult primary,
        Func<SolverSearchProfile, Stopwatch, SolverResult> runPass,
        Func<bool> stopRequested)
    {
        SolverResult selected = primary;
        long lastPassMilliseconds = requestClock.ElapsedMilliseconds;
        for (int completedEscalations = 0; ; completedEscalations++)
        {
            if (selected.ResultScope != SolverResultScope.SearchCompletion
                || stopRequested())
            {
                return selected;
            }
            bool completeVictory = IsCompleteVictory(selected);
            // 无胜利路径保持原样；已有胜利但战损很大的局面也允许加宽重搜一次，
            // 让搜索有机会换一条分支，而不是在原有分支上继续做低效益微调。
            if (completeVictory && !ShouldEscalateHighLossVictory(root, policy, selected))
                return selected;
            SolverSearchProfile? escalated = BuildNoVictoryEscalationProfile(
                configured,
                completedEscalations,
                requestClock.ElapsedMilliseconds,
                lastPassMilliseconds);
            if (escalated == null)
                return selected;

            policy.Diagnostics.Info(
                $"[CombatSolver/Test] NO_VICTORY_ESCALATION start " +
                $"kind={(completeVictory ? "high_loss_victory" : "no_victory")} " +
                $"attempt={completedEscalations + 1} " +
                $"beam={configured.BeamWidth}->{escalated.BeamWidth} " +
                $"nodes={configured.MaxExpandedNodes}->{escalated.MaxExpandedNodes} " +
                $"card_branches={configured.MaxCardBranchesPerNode}->{escalated.MaxCardBranchesPerNode} " +
                $"elapsed_ms={requestClock.ElapsedMilliseconds} " +
                $"remaining_ms={escalated.SoftTimeBudgetMilliseconds} " +
                $"last_pass_ms={lastPassMilliseconds}");
            Stopwatch passClock = Stopwatch.StartNew();
            SolverResult candidate = runPass(escalated, passClock);
            lastPassMilliseconds = passClock.ElapsedMilliseconds;
            if (candidate.ResultScope != SolverResultScope.SearchCompletion)
                return candidate;
            // 当前补搜未严格改善就停止，限制继续扩预算的成本；这不代表更宽搜索一定没有更优解。
            bool improved = candidate.ResultScope == SolverResultScope.SearchCompletion
                && CompareCompletedResultPrimaryQuality(root, policy, candidate, selected) < 0;
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] NO_VICTORY_ESCALATION result " +
                $"attempt={completedEscalations + 1} " +
                $"won={IsCompleteVictory(candidate)} improved={improved} " +
                $"pass_ms={lastPassMilliseconds}");
            if (!improved)
                return selected;
            selected = candidate;
        }
    }

    /// <summary>
    /// 高损胜利是否值得加宽重搜：战损超过玩家设置的可接受值，并且不低于最小绝对值。
    /// 只有完整胜利才讨论「战损大」；没有胜利的局面本来就会走无胜利加宽。
    /// </summary>
    private static bool ShouldEscalateHighLossVictory(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult selected)
    {
        int deficit = StrategicHpDeficit(root, policy, selected);
        return deficit > policy.AcceptableBattleHpLoss
            && deficit >= SolverWeights.HighLossEscalationMinimumHp;
    }

    internal static SolverSearchProfile? BuildNoVictoryEscalationProfile(
        SolverSearchProfile configured,
        int completedEscalations,
        long elapsedMilliseconds,
        long lastPassMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(completedEscalations);
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(lastPassMilliseconds);
        if (completedEscalations >= SolverWeights.MaximumNoVictoryEscalations)
            return null;
        long remainingMilliseconds =
            configured.SoftTimeBudgetMilliseconds - elapsedMilliseconds;
        // 按扩展倍数估计下一轮耗时；估计超过剩余预算就不启动，避免追加一轮高超时风险的搜索。
        long projectedMilliseconds = Math.Max(1, lastPassMilliseconds)
            * SolverWeights.NoVictoryEscalationFactor;
        if (remainingMilliseconds <= 0 || remainingMilliseconds < projectedMilliseconds)
            return null;

        long multiple = 1;
        for (int index = 0; index <= completedEscalations; index++)
            multiple *= SolverWeights.NoVictoryEscalationFactor;
        SolverSearchProfile escalated = configured with
        {
            BeamWidth = Scale(configured.BeamWidth, multiple, SolverWeights.MaximumEscalatedBeamWidth),
            MaxExpandedNodes = Scale(configured.MaxExpandedNodes, multiple, int.MaxValue),
            MaxCardBranchesPerNode = Scale(
                configured.MaxCardBranchesPerNode,
                multiple,
                SolverWeights.MaximumEscalatedBranchesPerAction),
            MaxPileChoiceBranchesPerAction = Scale(
                configured.MaxPileChoiceBranchesPerAction,
                multiple,
                SolverWeights.MaximumEscalatedBranchesPerAction),
            MaxHandChoiceBranchesPerAction = Scale(
                configured.MaxHandChoiceBranchesPerAction,
                multiple,
                SolverWeights.MaximumEscalatedBranchesPerAction),
            SoftTimeBudgetMilliseconds = (int)remainingMilliseconds,
        };
        long previousMultiple = multiple / SolverWeights.NoVictoryEscalationFactor;
        // Compare every search dimension with the previous pass, including branch-only growth.
        return escalated.BeamWidth == Scale(configured.BeamWidth, previousMultiple, SolverWeights.MaximumEscalatedBeamWidth)
            && escalated.MaxExpandedNodes == Scale(configured.MaxExpandedNodes, previousMultiple, int.MaxValue)
            && escalated.MaxCardBranchesPerNode == Scale(configured.MaxCardBranchesPerNode, previousMultiple, SolverWeights.MaximumEscalatedBranchesPerAction)
            && escalated.MaxPileChoiceBranchesPerAction == Scale(configured.MaxPileChoiceBranchesPerAction, previousMultiple, SolverWeights.MaximumEscalatedBranchesPerAction)
            && escalated.MaxHandChoiceBranchesPerAction == Scale(configured.MaxHandChoiceBranchesPerAction, previousMultiple, SolverWeights.MaximumEscalatedBranchesPerAction)
                ? null
                : escalated;

        static int Scale(int value, long multiple, int maximum)
            => (int)Math.Max(value, Math.Min(maximum, (long)value * multiple));
    }

}
