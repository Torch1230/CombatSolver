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
    /// <see cref="SolverSearchProfile.MaxExpandedNodes" /> 是**工作量帽**，不是搜索地平线，
    /// 可它在长战斗里总是先到。一场 8 回合 Boss 战里量过：85 次回合层截断**全部**是
    /// <c>reason=nodes</c>，<c>reason=time</c> 一次都没有，时间预算只用掉 5%–30%。
    /// 原因是节点预算要按 <see cref="SolverWeights.BossEnemyStrengthSuppressionHorizon" />
    /// 摊到每个回合层，Boss 战摊完只剩八分之一，而时间那一侧摊完还很宽裕。往上调一档也不解决：
    /// 预设把时间和节点同比例放大，而时间本来就有九成用不掉。
    /// </para>
    /// <para>
    /// 同一个检查点上量过四组，Beam 和节点是**乘**的关系：
    /// </para>
    /// <list type="bullet">
    /// <item>Beam 90 / 25 000：输。主搜索在 2 701–5 206 个节点上就把前沿走空了。</item>
    /// <item>Beam 90 / 50 000：输，而且主搜索展开的节点数**一个不变**——花不掉。</item>
    /// <item>Beam 135 / 50 000：输。这个 Beam 下找到胜利需要 83 423 个节点。</item>
    /// <item>Beam 135 / 100 000：赢（两瓶药、第 9 回合斩杀、剩 1 血），用了 83 423 个节点。</item>
    /// <item>Beam 512 / 100 000：赢（第 8 回合斩杀、剩 3 血），只用了 26 671 个节点。</item>
    /// </list>
    /// <para>
    /// 所以两边必须一起抬：只抬节点，窄 Beam 花不掉；只抬 Beam，节点又不够。
    /// </para>
    /// <para>
    /// 只在整份请求**一条胜利路线都没有**时触发：已经找到胜利的战斗一次都不会走进来，
    /// 行为逐位不变。触发时多花的，正是玩家在档位里配了却一直没被用掉的那段时间。
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
            if (IsCompleteVictory(selected)
                || selected.ResultScope != SolverResultScope.SearchCompletion
                || stopRequested())
            {
                return selected;
            }
            SolverSearchProfile? escalated = BuildNoVictoryEscalationProfile(
                configured,
                completedEscalations,
                requestClock.ElapsedMilliseconds,
                lastPassMilliseconds);
            if (escalated == null)
                return selected;

            policy.Diagnostics.Info(
                $"[CombatSolver/Test] NO_VICTORY_ESCALATION start " +
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
            // 没变好就停：多给的预算既然没换来更好的路线，再翻一倍也只是让玩家多等。
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
        // 下一轮搜索面和节点都翻倍，耗时按同一个倍数估。估不进剩余预算就不开始——开了也只会
        // 撞死线，拿回一个更差的半成品，而玩家白等一遍。
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
