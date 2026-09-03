namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertRequiredPotionAuditSelectionAndTotals()
    {
        SolverInterimResult twoPotionPrimary = new(
            Won: true,
            OutstandingStolenResource: 5,
            ProjectedBattleHpLost: 0,
            StrategicHpDeficit: 0,
            PotionStrategicCost: 18,
            ProjectedBattlePotionCount: 2,
            EnemyHp: 0,
            Score: -1_000,
            CombatEndedTurn: 2);
        SolverInterimResult onePotionAudit = new(
            Won: true,
            OutstandingStolenResource: 0,
            ProjectedBattleHpLost: 5,
            StrategicHpDeficit: 5,
            PotionStrategicCost: 9,
            ProjectedBattlePotionCount: 1,
            EnemyHp: 0,
            Score: 1_000,
            CombatEndedTurn: 3);
        if (CombatSearchCoordinator.IsBetterPotionPolicyResult(
                SolverTheftPolicy.PreserveResources,
                onePotionAudit,
                twoPotionPrimary)
            || !CombatSearchCoordinator.IsBetterPotionPolicyResult(
                SolverTheftPolicy.PreserveResources,
                twoPotionPrimary,
                onePotionAudit)
            || !CombatSearchCoordinator.IsBetterPotionPolicyResult(
                SolverTheftPolicy.PreserveResources,
                onePotionAudit with
                {
                    StrategicHpDeficit = 0,
                    CombatEndedTurn = 2,
                },
                twoPotionPrimary))
        {
            throw new InvalidOperationException(
                "RequireAtLeastOne 审计没有先按胜利、战损、回合选择，再使用资源与药水尾键。");
        }

        static SearchSolverWorkContribution Work(int unit, bool deep) => new(
            ExpandedNodes: unit,
            TransitionCount: unit * 2,
            ChoiceBranchesEvaluated: unit * 3,
            ShortElapsed: TimeSpan.FromTicks(unit * 4L),
            DeepElapsed: TimeSpan.FromTicks(unit * 5L),
            WorkerAllocatedBytes: unit * 6L,
            ShortExpandedNodes: deep ? 0 : unit,
            DeepExpandedNodes: deep ? unit : 0,
            ShortTransitionCount: deep ? 0 : unit * 2,
            DeepTransitionCount: deep ? unit * 2 : 0,
            Gen0Collections: unit * 7,
            Gen1Collections: unit * 8,
            Gen2Collections: unit * 9,
            GcPauseDuration: TimeSpan.FromTicks(unit * 10L),
            MaxObservedGcPause: TimeSpan.FromTicks(unit * 11L),
            DeepSearchTriggered: deep);

        SearchRequestWorkSnapshot totals = CombatSearchCoordinator.AggregateAuditWork(
            Work(1, deep: false),
            Work(10, deep: false),
            Work(100, deep: true));
        if (totals.RecordedSolverCount != 3
            || totals.ExpandedNodes != 111
            || totals.TransitionCount != 222
            || totals.ChoiceBranchesEvaluated != 333
            || totals.ShortElapsed != TimeSpan.FromTicks(444)
            || totals.DeepElapsed != TimeSpan.FromTicks(555)
            || totals.WorkerAllocatedBytes != 666
            || totals.ShortExpandedNodes != 11
            || totals.DeepExpandedNodes != 100
            || totals.ShortTransitionCount != 22
            || totals.DeepTransitionCount != 200
            || totals.Gen0Collections != 777
            || totals.Gen1Collections != 888
            || totals.Gen2Collections != 999
            || totals.GcPauseDuration != TimeSpan.FromTicks(1_110)
            || totals.MaxObservedGcPause != TimeSpan.FromTicks(1_100)
            || !totals.DeepSearchTriggered)
        {
            throw new InvalidOperationException(
                $"RequireAtLeastOne 的 primary/potionFree/audited 工作量没有各合并一次：" +
                $"records={totals.RecordedSolverCount} expanded={totals.ExpandedNodes} " +
                $"transitions={totals.TransitionCount} choices={totals.ChoiceBranchesEvaluated}。");
        }

        SearchRequestWorkTotals requestTotals = new();
        requestTotals.Record(Work(1, deep: false));
        requestTotals.RecordCoordinatorOverhead(
            TimeSpan.FromTicks(13),
            deepPhase: true,
            allocatedBytes: 17,
            gen0Collections: 1,
            gen1Collections: 2,
            gen2Collections: 3,
            gcPauseDuration: TimeSpan.FromTicks(19),
            maxObservedGcPause: TimeSpan.FromTicks(23));
        SearchRequestWorkSnapshot withCoordinatorOverhead = requestTotals.Snapshot();
        if (withCoordinatorOverhead.RecordedSolverCount != 1
            || withCoordinatorOverhead.ExpandedNodes != 1
            || withCoordinatorOverhead.ShortElapsed != TimeSpan.FromTicks(4)
            || withCoordinatorOverhead.DeepElapsed != TimeSpan.FromTicks(18)
            || withCoordinatorOverhead.WorkerAllocatedBytes != 23
            || withCoordinatorOverhead.Gen0Collections != 8
            || withCoordinatorOverhead.Gen1Collections != 10
            || withCoordinatorOverhead.Gen2Collections != 12
            || withCoordinatorOverhead.GcPauseDuration != TimeSpan.FromTicks(29)
            || withCoordinatorOverhead.MaxObservedGcPause != TimeSpan.FromTicks(23))
        {
            throw new InvalidOperationException(
                "Smart 药水层间的 coordinator 内存整理没有计入请求总量，" +
                "或被错误计作另一个 solver。" +
                $" records={withCoordinatorOverhead.RecordedSolverCount}" +
                $" elapsed={withCoordinatorOverhead.ShortElapsed}/" +
                $"{withCoordinatorOverhead.DeepElapsed}" +
                $" allocated={withCoordinatorOverhead.WorkerAllocatedBytes}。");
        }
    }

    private static async Task AssertInProgressCanceledExactLayerWorkRecordedOnceAsync(
        CombatRootSnapshot rootSnapshot,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot capturedPolicy)
    {
        SearchRequestWorkTotals requestWorkTotals = new();
        SearchPolicySnapshot requestPolicy = capturedPolicy with
        {
            MaxDegreeOfParallelism = 1,
            RequestWorkTotals = requestWorkTotals,
        };
        SolverSearchProfile focusedProfile = capturedPolicy.ShortProfile with
        {
            BeamWidth = Math.Min(capturedPolicy.ShortProfile.BeamWidth, 8),
            MaxExpandedNodes = Math.Min(capturedPolicy.ShortProfile.MaxExpandedNodes, 32),
            SoftTimeBudgetMilliseconds = 120_000,
        };

        // Model one request with a completed potion-free layer followed by the exact-one-potion
        // Smart layer. The shared accumulator must retain the canceled layer's partial work.
        await Task.Run(() => new CombatBeamSolver(
            rootSnapshot,
            displayNames,
            battleDamage,
            requestPolicy,
            CancellationToken.None,
            progressCallback: null,
            focusedProfile,
            potionPolicyOverride: SolverPotionPolicy.Disabled).Solve());
        SearchRequestWorkSnapshot beforeCancellation = requestWorkTotals.Snapshot();
        if (beforeCancellation.RecordedSolverCount != 1)
        {
            throw new InvalidOperationException(
                $"请求工作量基线没有精确记录一个已完成搜索层：" +
                $"records={beforeCancellation.RecordedSolverCount}。");
        }

        using CancellationTokenSource cancellation = new();
        bool canceledAfterWork = false;
        try
        {
            await Task.Run(() => new CombatBeamSolver(
                rootSnapshot,
                displayNames,
                battleDamage,
                requestPolicy,
                cancellation.Token,
                progress =>
                {
                    if (progress.ExpandedNodes <= 0)
                        return;
                    canceledAfterWork = true;
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                },
                focusedProfile,
                potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                maximumPotionUses: 1,
                minimumPotionUses: 1).Solve());
            throw new InvalidOperationException("搜索中取消的 Smart 精确用药层没有抛出取消异常。");
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellation.Token)
        {
        }

        SearchRequestWorkSnapshot totals = requestWorkTotals.Snapshot();
        long canceledExpanded = totals.ExpandedNodes - beforeCancellation.ExpandedNodes;
        long canceledTransitions = totals.TransitionCount - beforeCancellation.TransitionCount;
        long canceledAllocation = totals.WorkerAllocatedBytes - beforeCancellation.WorkerAllocatedBytes;
        if (!canceledAfterWork
            || totals.RecordedSolverCount != beforeCancellation.RecordedSolverCount + 1
            || requestWorkTotals.RecordedSolverCountForTesting != totals.RecordedSolverCount
            || canceledExpanded <= 0
            || canceledTransitions <= 0
            || canceledAllocation <= 0)
        {
            throw new InvalidOperationException(
                $"搜索中取消的 Smart 精确用药层没有向请求总量精确贡献一次非零工作：" +
                $"canceled_after_work={canceledAfterWork} " +
                $"records={beforeCancellation.RecordedSolverCount}->{totals.RecordedSolverCount} " +
                $"expanded={canceledExpanded} transitions={canceledTransitions} " +
                $"allocated={canceledAllocation}。");
        }
        if (totals.ShortExpandedNodes + totals.DeepExpandedNodes != totals.ExpandedNodes
            || totals.ShortTransitionCount + totals.DeepTransitionCount != totals.TransitionCount)
        {
            throw new InvalidOperationException(
                $"取消后请求工作量的长短搜索分区没有与总量对齐：" +
                $"expanded={totals.ExpandedNodes}/" +
                $"{totals.ShortExpandedNodes + totals.DeepExpandedNodes} " +
                $"transitions={totals.TransitionCount}/" +
                $"{totals.ShortTransitionCount + totals.DeepTransitionCount}。");
        }
        Entry.Logger.Info(
            $"[CombatSolver/Test] REQUEST_WORK_IN_PROGRESS_CANCELLATION " +
            $"records={beforeCancellation.RecordedSolverCount}->{totals.RecordedSolverCount} " +
            $"expanded={canceledExpanded} transitions={canceledTransitions} " +
            $"allocated={canceledAllocation}");
    }
}
