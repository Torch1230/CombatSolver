namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertNarrowBeamRecoveryPolicy(
        CombatRootSnapshot root,
        SearchPolicySnapshot capturedPolicy)
    {
        SolverSearchProfile wide = SolverSearchProfile.Deep with
        {
            BeamWidth = 135,
            MaxExpandedNodes = 50_000,
            MaxCardBranchesPerNode = 72,
            MaxPileChoiceBranchesPerAction = 42,
            MaxHandChoiceBranchesPerAction = 54,
            SoftTimeBudgetMilliseconds = 90_000,
        };
        SolverSearchProfile narrow = CombatSearchCoordinator.BuildNarrowBeamRecoveryProfile(
            wide,
            expandedNodes: 48_000,
            elapsedMilliseconds: 89_000)
            ?? throw new InvalidOperationException("有剩余预算的宽 Beam 没有生成恢复配置。");
        if (SolverSearchProfile.Short.RecoverDeferredTurnFrontier
            || SolverSearchProfile.Deep.RecoverDeferredTurnFrontier
            || wide.RecoverDeferredTurnFrontier
            || !narrow.RecoverDeferredTurnFrontier
            || narrow.BeamWidth != SolverSearchProfile.Deep.BeamWidth
            || narrow.MaxExpandedNodes != 2_000
            || narrow.SoftTimeBudgetMilliseconds != 1_000
            || narrow.MaxCardBranchesPerNode != SolverSearchProfile.Deep.MaxCardBranchesPerNode
            || narrow.MaxPileChoiceBranchesPerAction != SolverSearchProfile.Deep.MaxPileChoiceBranchesPerAction
            || narrow.MaxHandChoiceBranchesPerAction != SolverSearchProfile.Deep.MaxHandChoiceBranchesPerAction)
        {
            throw new InvalidOperationException("窄 Beam 恢复没有遵守标准配置与原层剩余预算。");
        }

        SolverSearchProfile custom = wide with
        {
            MaxCardBranchesPerNode = 3,
            MaxPileChoiceBranchesPerAction = 2,
            MaxHandChoiceBranchesPerAction = 1,
        };
        SolverSearchProfile capped = CombatSearchCoordinator.BuildNarrowBeamRecoveryProfile(custom, 0, 0)
            ?? throw new InvalidOperationException("自定义宽 Beam 没有生成恢复配置。");
        if (capped.MaxCardBranchesPerNode != 3
            || capped.MaxPileChoiceBranchesPerAction != 2
            || capped.MaxHandChoiceBranchesPerAction != 1
            || CombatSearchCoordinator.BuildNarrowBeamRecoveryProfile(wide, 50_000, 0) != null
            || CombatSearchCoordinator.BuildNarrowBeamRecoveryProfile(wide, 0, 90_000) != null
            || CombatSearchCoordinator.BuildNarrowBeamRecoveryProfile(SolverSearchProfile.Deep, 0, 0) != null
            || CombatSearchCoordinator.BuildNarrowBeamRecoveryProfile(SolverSearchProfile.Short, 0, 0) != null)
        {
            throw new InvalidOperationException("窄 Beam 恢复扩大了自定义预算或重复了相同配置。");
        }

        SearchRequestWorkTotals work = new();
        SearchPolicySnapshot policy = capturedPolicy with { RequestWorkTotals = work };
        PotionPolicyUnsatisfiedException originalFailure = new("narrow_recovery_original_policy_miss");
        int attempts = 0;
        try
        {
            CombatSearchCoordinator.SolveWithNarrowBeamRecovery(
                root,
                policy,
                wide with { MaxExpandedNodes = 20 },
                CancellationToken.None,
                CancellationToken.None,
                (profile, _) =>
                {
                    attempts++;
                    if (attempts == 1)
                    {
                        if (profile.RecoverDeferredTurnFrontier)
                            throw new InvalidOperationException("正常主搜索提前启用了失败落选恢复。");
                        work.Record(NarrowRecoveryWork(17));
                        throw originalFailure;
                    }
                    if (attempts != 2 || profile.MaxExpandedNodes != 3
                        || !profile.RecoverDeferredTurnFrontier)
                        throw new InvalidOperationException("失败 solver 的已耗节点没有扣除，或窄 Beam 重试了多次。");
                    work.Record(NarrowRecoveryWork(3));
                    throw new PotionPolicyUnsatisfiedException("narrow_recovery_second_policy_miss");
                });
            throw new InvalidOperationException("两次药水政策失败没有保留原始错误。");
        }
        catch (PotionPolicyUnsatisfiedException exception) when (ReferenceEquals(exception, originalFailure))
        {
        }
        SearchRequestWorkSnapshot totals = work.Snapshot();
        if (attempts != 2
            || totals.RecordedSolverCount != 2
            || totals.ExpandedNodes != 20
            || totals.TransitionCount != 40
            || totals.WorkerAllocatedBytes != 60)
        {
            throw new InvalidOperationException("窄 Beam 恢复丢失或重复记录了失败 solver 的工作量。");
        }

        AssertNarrowBeamRecoveryCancellation(root, capturedPolicy, wide, callerCancels: false);
        AssertNarrowBeamRecoveryCancellation(root, capturedPolicy, wide, callerCancels: true);
    }

    private static void AssertNarrowBeamRecoveryCancellation(
        CombatRootSnapshot root,
        SearchPolicySnapshot capturedPolicy,
        SolverSearchProfile profile,
        bool callerCancels)
    {
        SearchRequestWorkTotals work = new();
        SearchPolicySnapshot policy = capturedPolicy with { RequestWorkTotals = work };
        using CancellationTokenSource caller = new();
        using CancellationTokenSource requestDeadline =
            CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
        PotionPolicyUnsatisfiedException originalFailure = new("narrow_recovery_cancel_policy_miss");
        int attempts = 0;
        try
        {
            CombatSearchCoordinator.SolveWithNarrowBeamRecovery(
                root,
                policy,
                profile,
                requestDeadline.Token,
                caller.Token,
                (_, token) =>
                {
                    attempts++;
                    work.Record(NarrowRecoveryWork(1));
                    if (attempts == 1)
                        throw originalFailure;
                    if (callerCancels)
                        caller.Cancel();
                    else
                        requestDeadline.Cancel();
                    token.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("窄 Beam 没有继承请求取消信号。");
                });
            throw new InvalidOperationException("窄 Beam 取消没有产生预期结果。");
        }
        catch (PotionPolicyUnsatisfiedException exception)
            when (!callerCancels && ReferenceEquals(exception, originalFailure))
        {
        }
        catch (OperationCanceledException) when (callerCancels && caller.IsCancellationRequested)
        {
        }
        if (attempts != 2 || work.Snapshot().RecordedSolverCount != 2)
            throw new InvalidOperationException("窄 Beam 取消没有保留两次 solver 的工作量。");
    }

    private static SearchSolverWorkContribution NarrowRecoveryWork(int expandedNodes) => new(
        ExpandedNodes: expandedNodes,
        TransitionCount: expandedNodes * 2,
        ChoiceBranchesEvaluated: 0,
        ShortElapsed: TimeSpan.FromTicks(expandedNodes),
        DeepElapsed: TimeSpan.Zero,
        WorkerAllocatedBytes: expandedNodes * 3L,
        ShortExpandedNodes: expandedNodes,
        DeepExpandedNodes: 0,
        ShortTransitionCount: expandedNodes * 2,
        DeepTransitionCount: 0,
        Gen0Collections: 0,
        Gen1Collections: 0,
        Gen2Collections: 0,
        GcPauseDuration: TimeSpan.Zero,
        MaxObservedGcPause: TimeSpan.Zero,
        DeepSearchTriggered: false);

}
