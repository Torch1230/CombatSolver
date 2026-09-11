namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    /// <summary>
    /// 一条胜利路线都没找到时的搜索面升级：倍数、上限、次数和那道「估不进剩余时间就不开始」
    /// 的保护，全部按纯函数验，不启动搜索。
    /// </summary>
    private static void AssertNoVictoryEscalationPolicy()
    {
        // 高档的深搜配置。玩家实际撞上这个问题时用的就是它。
        SolverSearchProfile high = SolverSearchProfile.Default with
        {
            BeamWidth = 90,
            MaxExpandedNodes = 25_000,
            MaxCardBranchesPerNode = 48,
            MaxPileChoiceBranchesPerAction = 28,
            MaxHandChoiceBranchesPerAction = 36,
            SoftTimeBudgetMilliseconds = 180_000,
        };

        // 第一次升级：搜索面和工作量帽同时翻倍，时间给剩下的那部分。
        SolverSearchProfile first = CombatSearchCoordinator.BuildNoVictoryEscalationProfile(
            high,
            completedEscalations: 0,
            elapsedMilliseconds: 22_000,
            lastPassMilliseconds: 22_000)
            ?? throw new InvalidOperationException("时间预算还剩八成时没有生成升级配置。");
        if (first.BeamWidth != 180
            || first.MaxExpandedNodes != 50_000
            || first.MaxCardBranchesPerNode != 96
            || first.MaxPileChoiceBranchesPerAction != 56
            || first.MaxHandChoiceBranchesPerAction != 72
            || first.SoftTimeBudgetMilliseconds != 158_000)
        {
            throw new InvalidOperationException("第一次升级没有把搜索面与节点上限同时翻倍。");
        }

        // 第二次升级以玩家配的那一份为基准算 4 倍，不是在上一轮结果上再翻。
        // 分支上限撞到 100 就停在 100，和设置校验的上限一致。
        SolverSearchProfile second = CombatSearchCoordinator.BuildNoVictoryEscalationProfile(
            high,
            completedEscalations: 1,
            elapsedMilliseconds: 46_000,
            lastPassMilliseconds: 24_000)
            ?? throw new InvalidOperationException("第二次升级在预算充足时被拒。");
        if (second.BeamWidth != 360
            || second.MaxExpandedNodes != 100_000
            || second.MaxCardBranchesPerNode != SolverWeights.MaximumEscalatedBranchesPerAction
            || second.MaxPileChoiceBranchesPerAction != SolverWeights.MaximumEscalatedBranchesPerAction
            || second.MaxHandChoiceBranchesPerAction != SolverWeights.MaximumEscalatedBranchesPerAction
            || second.SoftTimeBudgetMilliseconds != 134_000)
        {
            throw new InvalidOperationException("第二次升级没有按玩家配置的 4 倍取值或没有夹住分支上限。");
        }

        // 次数封顶。
        if (CombatSearchCoordinator.BuildNoVictoryEscalationProfile(
                high,
                completedEscalations: SolverWeights.MaximumNoVictoryEscalations,
                elapsedMilliseconds: 1_000,
                lastPassMilliseconds: 1_000) != null)
        {
            throw new InvalidOperationException("升级次数没有封顶。");
        }

        // 估不进剩余时间就不开始：下一轮按上一轮的两倍估，剩 80 秒估 100 秒，拒。
        if (CombatSearchCoordinator.BuildNoVictoryEscalationProfile(
                high,
                completedEscalations: 0,
                elapsedMilliseconds: 100_000,
                lastPassMilliseconds: 50_000) != null)
        {
            throw new InvalidOperationException("剩余时间装不下下一轮时仍然开了升级。");
        }

        // 时间预算已经用光。
        if (CombatSearchCoordinator.BuildNoVictoryEscalationProfile(
                high,
                completedEscalations: 0,
                elapsedMilliseconds: high.SoftTimeBudgetMilliseconds,
                lastPassMilliseconds: 1) != null)
        {
            throw new InvalidOperationException("时间预算用光后仍然开了升级。");
        }

        // Beam 已经在上限、节点也没法再涨时必须返回 null：搜索是确定性的，
        // 配置逐位相同再跑一遍只会拿回同一个结果。
        SolverSearchProfile saturated = high with
        {
            BeamWidth = SolverWeights.MaximumEscalatedBeamWidth,
            MaxExpandedNodes = int.MaxValue,
            MaxCardBranchesPerNode = SolverWeights.MaximumEscalatedBranchesPerAction,
            MaxPileChoiceBranchesPerAction = SolverWeights.MaximumEscalatedBranchesPerAction,
            MaxHandChoiceBranchesPerAction = SolverWeights.MaximumEscalatedBranchesPerAction,
        };
        if (CombatSearchCoordinator.BuildNoVictoryEscalationProfile(
                saturated,
                completedEscalations: 0,
                elapsedMilliseconds: 1_000,
                lastPassMilliseconds: 1_000) != null)
        {
            throw new InvalidOperationException("四项都已经在上限时仍然生成了逐位相同的升级配置。");
        }

        // 极高档：Beam 135 撞不到 512，节点从 5 万涨到 10 万——实测里正是这一步跨过了
        // 「找不到胜利」到「找到胜利」的那条线（那个 Beam 下需要 83 423 个展开节点）。
        SolverSearchProfile veryHigh = high with
        {
            BeamWidth = 135,
            MaxExpandedNodes = 50_000,
            SoftTimeBudgetMilliseconds = 300_000,
        };
        SolverSearchProfile veryHighFirst = CombatSearchCoordinator.BuildNoVictoryEscalationProfile(
            veryHigh,
            completedEscalations: 0,
            elapsedMilliseconds: 13_000,
            lastPassMilliseconds: 13_000)
            ?? throw new InvalidOperationException("极高档没有生成升级配置。");
        if (veryHighFirst.BeamWidth != 270 || veryHighFirst.MaxExpandedNodes != 100_000)
            throw new InvalidOperationException("极高档的升级取值不对。");
    }
}
