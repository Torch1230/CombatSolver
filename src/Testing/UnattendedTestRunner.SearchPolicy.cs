using System.Runtime;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Saves;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static async Task AssertSearchPolicySnapshotAsync(CombatState combat)
    {
        AssertBeamRankBlockDamagePayoffTieBreak();

        if (Environment.ProcessorCount < 2)
        {
            throw new PlatformNotSupportedException(
                "DOP1/DOP2 搜索等价测试至少需要两个可用逻辑处理器。");
        }

        await AssertNoGcBudgetTransitionAsync();

        SolverSettingsData originalSettings = SolverSettings.Current;
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        SearchPolicySnapshot capturedPolicy = SolverController.CaptureSearchPolicy(
            settings,
            combat,
            includeTurnSetup: false,
            theftPolicy: SolverController.ResolveTheftPolicy(combat)) with
        {
            ShortProfile = settings.ShortProfile with
            {
                MaxExpandedNodes = Math.Min(settings.ShortProfile.MaxExpandedNodes, 250),
                SoftTimeBudgetMilliseconds = 120_000,
            },
            ForceShortOnly = true,
            VerifyIncrementalSearch = false,
            DetailedDiagnostics = false,
            MeasurePhasePerformance = false,
            ShortBudgetOverrideMilliseconds = null,
            DeepBudgetOverrideMilliseconds = null,
        };
        SolverDisplayNames displayNames = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot battleDamage = BattleDamageTracker.Observe(combat);
        AssertFullRngStateIdentity(combat);
        CombatRootSnapshot rootSnapshot = CombatRootSnapshot.Capture(combat);

        SearchPolicySnapshot serialPolicy = capturedPolicy with { MaxDegreeOfParallelism = 1 };
        SearchPolicySnapshot parallelPolicy = capturedPolicy with { MaxDegreeOfParallelism = 2 };
        // Parallel first intentionally exercises cold static mirror caches under contention.
        SolverResult parallelResult = await Task.Run(() => CombatSearchCoordinator.Solve(
            rootSnapshot,
            displayNames,
            battleDamage,
            parallelPolicy,
            CancellationToken.None,
            progressCallback: null));
        SolverResult serialResult = await Task.Run(() => CombatSearchCoordinator.Solve(
            rootSnapshot,
            displayNames,
            battleDamage,
            serialPolicy,
            CancellationToken.None,
            progressCallback: null));
        AssertEquivalentSearchResults(serialResult, parallelResult, "DOP1/DOP2");
        if (serialResult.ParallelExpansionWaves != 0
            || serialResult.ParallelExpansionWorkItems != 0
            || serialResult.MaxParallelExpansionConcurrency != 0)
        {
            throw new InvalidOperationException(
                "DOP1 搜索意外记录了并行展开工作。");
        }
        if (parallelResult.ParallelExpansionWaves <= 0
            || parallelResult.ParallelExpansionWorkItems < 2
            || parallelResult.MaxParallelExpansionConcurrency < 2)
        {
            throw new InvalidOperationException(
                $"DOP2 搜索没有形成真实并行展开：" +
                $"waves={parallelResult.ParallelExpansionWaves} " +
                $"work_items={parallelResult.ParallelExpansionWorkItems} " +
                $"max_concurrency={parallelResult.MaxParallelExpansionConcurrency}。");
        }
        if (serialResult.NodeLimitSnapshotsReleased <= 0
            || parallelResult.NodeLimitSnapshotsReleased <= 0)
        {
            throw new InvalidOperationException(
                $"节点上限搜索没有释放被预算丢弃的模拟器快照：" +
                $"dop1={serialResult.NodeLimitSnapshotsReleased} " +
                $"dop2={parallelResult.NodeLimitSnapshotsReleased}。");
        }

        SolverPotionPolicy changedPotionPolicy = capturedPolicy.PotionPolicy == SolverPotionPolicy.Disabled
            ? SolverPotionPolicy.RequireAtLeastOne
            : SolverPotionPolicy.Disabled;
        try
        {
            SolverSettings.ApplyForTesting(originalSettings with
            {
                PotionPolicy = changedPotionPolicy,
                EnableDetailedDiagnosticLogs = !capturedPolicy.DetailedDiagnostics,
            });
            SolverResult afterMutation = await Task.Run(() => CombatSearchCoordinator.Solve(
                rootSnapshot,
                displayNames,
                battleDamage,
                serialPolicy,
                CancellationToken.None,
                progressCallback: null));
            AssertEquivalentSearchResults(serialResult, afterMutation, "captured policy/global settings mutation");
        }
        finally
        {
            SolverSettings.ApplyForTesting(originalSettings);
        }
    }

    private static void AssertBeamRankBlockDamagePayoffTieBreak()
    {
        const double tiedScore = 1234.5d;
        double nextScore = Math.BitIncrement(tiedScore);

        static void AssertEarlier(
            (double Score, int BlockDamagePayoff, int ActionCount) expected,
            (double Score, int BlockDamagePayoff, int ActionCount) other,
            string failure)
        {
            int forward = CombatBeamSolver.CompareBeamRankOrder(
                expected.Score,
                expected.BlockDamagePayoff,
                expected.ActionCount,
                other.Score,
                other.BlockDamagePayoff,
                other.ActionCount);
            int reverse = CombatBeamSolver.CompareBeamRankOrder(
                other.Score,
                other.BlockDamagePayoff,
                other.ActionCount,
                expected.Score,
                expected.BlockDamagePayoff,
                expected.ActionCount);
            if (forward >= 0 || reverse <= 0)
                throw new InvalidOperationException(failure);
        }

        AssertEarlier(
            (nextScore, BlockDamagePayoff: 0, ActionCount: 99),
            (tiedScore, BlockDamagePayoff: int.MaxValue, ActionCount: 0),
            "Beam 排序在 BeamRankScore 不相等时错误地让格挡伤害收益覆盖了原评分顺序。");
        AssertEarlier(
            (tiedScore, BlockDamagePayoff: 0, ActionCount: 3),
            (tiedScore, BlockDamagePayoff: int.MaxValue, ActionCount: 4),
            "Beam 同分时没有先保持较短路线优先。");
        AssertEarlier(
            (tiedScore, BlockDamagePayoff: 55, ActionCount: 3),
            (tiedScore, BlockDamagePayoff: 50, ActionCount: 3),
            "Beam 同分且动作数相同时没有优先保留可转化为伤害的更高格挡路线。");

        int exactTie = CombatBeamSolver.CompareBeamRankOrder(
            tiedScore,
            leftBlockDamagePayoffValue: 55,
            leftActionCount: 3,
            tiedScore,
            rightBlockDamagePayoffValue: 55,
            rightActionCount: 3);
        if (exactTie != 0)
            throw new InvalidOperationException("Beam 完全相同的排序键没有保持相等。");
    }

    private static void AssertFullRngStateIdentity(CombatState combat)
    {
        Rng rng = combat.RunState.Rng.CombatCardSelection;
        SerializableRng original = rng.ToSerializable();
        try
        {
            ContinuationStamp originalContinuation = ContinuationStamp.CaptureLive(combat);
            StateFingerprint originalFingerprint =
                CombatBeamSolver.CaptureRngStateFingerprintForTesting(rng);
            rng.LoadFromSerializable(new SerializableRng
            {
                counter = original.counter,
                state0 = original.state0 ^ 0x9E3779B97F4A7C15UL,
                state1 = original.state1,
                state2 = original.state2,
                state3 = original.state3,
            });
            ContinuationStamp changedContinuation = ContinuationStamp.CaptureLive(combat);
            StateFingerprint changedFingerprint =
                CombatBeamSolver.CaptureRngStateFingerprintForTesting(rng);
            if (originalContinuation == changedContinuation)
                throw new InvalidOperationException("续用状态没有区分计数相同但内部状态不同的 RNG。");
            if (originalFingerprint == changedFingerprint)
                throw new InvalidOperationException("搜索状态键没有区分计数相同但内部状态不同的 RNG。");
        }
        finally
        {
            rng.LoadFromSerializable(original);
        }
    }

    private static async Task AssertNoGcBudgetTransitionAsync()
    {
        const long initialBudgetBytes = 1_000_000_000L;
        const long changedBudgetBytes = 2_000_000_000L;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        using CancellationTokenSource firstSearchCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_no_gc_budget_transition_setup",
            forceCollection: true);
        await AssertInSearchReclaimAsync(deadline.Token);
        await AssertCombatEndReclaimPolicyAsync(deadline.Token);
        SearchGcPolicy.ResetCountersForTesting();

        TaskCompletionSource firstSearchEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task? firstSearchTask = null;
        IDisposable? changedScope = null;
        Task<IDisposable>? changedScopeTask = null;
        try
        {
            firstSearchTask = Task.Run(async () =>
            {
                using IDisposable scope = SearchGcPolicy.EnterLowLatencySearch(
                    initialBudgetBytes,
                    new SearchMemoryPressureSignal(),
                    firstSearchCancellation.Token);
                firstSearchEntered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, firstSearchCancellation.Token);
            });
            await Task.WhenAny(firstSearchEntered.Task, firstSearchTask).WaitAsync(deadline.Token);
            if (firstSearchTask.IsCompleted)
                await firstSearchTask;
            await firstSearchEntered.Task;
            if (SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting != initialBudgetBytes)
                throw new InvalidOperationException("No-GC 首次区域没有使用 1GB 测试预算。");
            if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
                throw new InvalidOperationException("No-GC 首次 1GB 测试区域没有由 CLR 实际建立。");

            changedScopeTask = Task.Run(() => SearchGcPolicy.EnterLowLatencySearch(
                changedBudgetBytes,
                new SearchMemoryPressureSignal(),
                deadline.Token));
            while (SearchGcPolicy.BudgetChangeWaitCountForTesting == 0)
            {
                deadline.Token.ThrowIfCancellationRequested();
                await Task.Delay(10, deadline.Token);
            }
            if (changedScopeTask.IsCompleted)
                throw new InvalidOperationException("No-GC 改预算请求没有等待仍在使用旧区域的搜索退出。");

            firstSearchCancellation.Cancel();
            try
            {
                await firstSearchTask.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (firstSearchCancellation.IsCancellationRequested)
            {
            }

            changedScope = await changedScopeTask.WaitAsync(deadline.Token);
            if (SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting != changedBudgetBytes)
            {
                throw new InvalidOperationException(
                    "No-GC 改预算后没有按 2GB 测试值建立新区域。");
            }
            if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
                throw new InvalidOperationException("No-GC 改预算后的 2GB 区域没有由 CLR 实际建立。");
            if (SearchGcPolicy.BudgetChangeRebuildCountForTesting != 1)
            {
                throw new InvalidOperationException(
                    $"No-GC 改预算后重建次数为 " +
                    $"{SearchGcPolicy.BudgetChangeRebuildCountForTesting}，预期为 1。");
            }
        }
        finally
        {
            firstSearchCancellation.Cancel();
            if (firstSearchTask != null)
            {
                try
                {
                    await firstSearchTask;
                }
                catch (OperationCanceledException) when (firstSearchCancellation.IsCancellationRequested)
                {
                }
            }
            if (changedScope == null && changedScopeTask != null)
            {
                try
                {
                    changedScope = await changedScopeTask.WaitAsync(deadline.Token);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                }
            }
            changedScope?.Dispose();
            await SearchGcPolicy.ReclaimIfPendingAsync(
                "unattended_no_gc_budget_transition_cleanup",
                forceCollection: true);
        }
    }

    private static async Task AssertInSearchReclaimAsync(CancellationToken cancellationToken)
    {
        const long budgetBytes = 1_000_000_000L;
        SearchMemoryPressureSignal signal = new();
        using (SearchGcPolicy.EnterLowLatencySearch(
                   budgetBytes,
                   signal,
                   cancellationToken))
        {
            signal.ReclaimAndContinue(cancellationToken);
            if (signal.ReclaimCount != 1)
                throw new InvalidOperationException("搜索内存检查点没有完成一次全代回收后继续。");
        }
        await SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_in_search_reclaim_cleanup",
            forceCollection: true);
    }

    private static async Task AssertCombatEndReclaimPolicyAsync(
        CancellationToken cancellationToken)
    {
        const long budgetBytes = 1_000_000_000L;
        SearchGcPolicy.ResetCountersForTesting();
        SearchGcPolicy.ReportCombatLifecycleAllocation(
            1024 * 1024,
            "unattended_low_allocation_root_snapshot");
        IDisposable lowAllocationScope = SearchGcPolicy.EnterLowLatencySearch(
            budgetBytes,
            new SearchMemoryPressureSignal(),
            cancellationToken);
        Task earlyRegionExit = SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync(
            "unattended_low_allocation_combat_end");
        if (earlyRegionExit.IsCompleted)
        {
            lowAllocationScope.Dispose();
            throw new InvalidOperationException("活跃搜索尚未退出时提前结束了 No-GC 区域。");
        }
        lowAllocationScope.Dispose();
        await earlyRegionExit.WaitAsync(cancellationToken);
        await SearchGcPolicy.ReclaimIfPendingAsync("unattended_low_allocation_combat_end");
        if (SearchGcPolicy.NoGcRegionExitWithoutCollectionCountForTesting != 1
            || SearchGcPolicy.BackgroundReclaimStartedCountForTesting != 0
            || SearchGcPolicy.BackgroundGen2CompletedCountForTesting != 0
            || GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
        {
            throw new InvalidOperationException(
                $"低分配战斗结束没有只退出 No-GC 区域：" +
                $"region_exits={SearchGcPolicy.NoGcRegionExitWithoutCollectionCountForTesting} " +
                $"reclaims={SearchGcPolicy.BackgroundReclaimStartedCountForTesting} " +
                $"gen2={SearchGcPolicy.BackgroundGen2CompletedCountForTesting} " +
                $"latency={GCSettings.LatencyMode}。");
        }
        await AssertReferenceReleaseBarrierAsync(budgetBytes, cancellationToken);

        SearchGcPolicy.ResetCountersForTesting();
        long rootAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        WeakReference transientRoot = AllocateTransientSearchGraphForGcPolicyTest();
        long rootAllocated = GC.GetTotalAllocatedBytes(precise: true) - rootAllocatedBefore;
        SearchGcPolicy.ReportCombatLifecycleAllocation(
            rootAllocated,
            "unattended_high_allocation_root_snapshot");
        if (rootAllocated < 270L * 1024 * 1024)
        {
            throw new InvalidOperationException(
                $"战斗根快照回收门禁只产生了 {rootAllocated} bytes，未越过 256 MiB 阈值。");
        }
        await SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_high_allocation_root_snapshot").WaitAsync(cancellationToken);
        long rootManagedLiveReleased =
            SearchGcPolicy.LastBackgroundReclaimManagedLiveBeforeForTesting
            - SearchGcPolicy.LastBackgroundReclaimManagedLiveAfterForTesting;
        if (SearchGcPolicy.BackgroundReclaimStartedCountForTesting != 1
            || SearchGcPolicy.BackgroundGen2CompletedCountForTesting != 1
            || transientRoot.IsAlive)
        {
            throw new InvalidOperationException(
                $"No-GC 区域外的战斗根快照压力没有完成一次有效 Gen2：" +
                $"reclaims={SearchGcPolicy.BackgroundReclaimStartedCountForTesting} " +
                $"gen2={SearchGcPolicy.BackgroundGen2CompletedCountForTesting} " +
                $"managed_live_released={rootManagedLiveReleased}。");
        }

        SearchGcPolicy.ResetCountersForTesting();
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        WeakReference transientAllocation;
        using (SearchGcPolicy.EnterLowLatencySearch(
                   budgetBytes,
                   new SearchMemoryPressureSignal(),
                   cancellationToken))
        {
            transientAllocation = AllocateTransientSearchGraphForGcPolicyTest();
        }
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        if (allocated < 270L * 1024 * 1024)
        {
            throw new InvalidOperationException(
                $"重分配回收门禁只产生了 {allocated} bytes，未越过 256 MiB 阈值。");
        }
        Task reclaim = SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_high_allocation_combat_end");
        Task joined = SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_high_allocation_combat_end_join");
        await Task.WhenAll(reclaim, joined).WaitAsync(cancellationToken);
        long managedLiveReleased =
            SearchGcPolicy.LastBackgroundReclaimManagedLiveBeforeForTesting
            - SearchGcPolicy.LastBackgroundReclaimManagedLiveAfterForTesting;
        if (SearchGcPolicy.BackgroundReclaimStartedCountForTesting != 1
            || SearchGcPolicy.BackgroundGen2CompletedCountForTesting != 1
            || SearchGcPolicy.BackgroundReclaimJoinCountForTesting != 1
            || transientAllocation.IsAlive)
        {
            throw new InvalidOperationException(
                $"重分配战斗结束没有完成恰好一次有效 Gen2：" +
                $"reclaims={SearchGcPolicy.BackgroundReclaimStartedCountForTesting} " +
                $"gen2={SearchGcPolicy.BackgroundGen2CompletedCountForTesting} " +
                $"joins={SearchGcPolicy.BackgroundReclaimJoinCountForTesting} " +
                $"managed_live_released={managedLiveReleased}。");
        }
        await AssertExhaustionReclaimReferenceCoverageAsync(cancellationToken);
    }

    private static async Task AssertExhaustionReclaimReferenceCoverageAsync(
        CancellationToken cancellationToken)
    {
        await AssertExhaustionReclaimReferenceCoverageTimingAsync(
            pauseAfterCoverageCapture: false,
            expectedGeneration2Collections: 1,
            cancellationToken);
        await AssertExhaustionReclaimReferenceCoverageTimingAsync(
            pauseAfterCoverageCapture: true,
            expectedGeneration2Collections: 2,
            cancellationToken);
    }

    private static async Task AssertExhaustionReclaimReferenceCoverageTimingAsync(
        bool pauseAfterCoverageCapture,
        int expectedGeneration2Collections,
        CancellationToken cancellationToken)
    {
        SearchGcPolicy.ResetCountersForTesting();
        TaskCompletionSource referencesReleased = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        (WeakReference graph, Task release) = CreateHeldGraphForGcPolicyTest(
            referencesReleased.Task);
        Task earlyReclaim = Task.CompletedTask;
        Task cleanup = Task.CompletedTask;
        try
        {
            long releaseEpochBefore = SearchGcPolicy.ReferenceReleaseEpochForTesting;
            (Task Reclaim, Task CoverageBoundaryReached) reclaimRequest =
                SearchGcPolicy.RequestNoGcExhaustionReclaimForTesting(
                    pauseAfterCoverageCapture);
            earlyReclaim = reclaimRequest.Reclaim;
            Task coverageBoundaryReached = reclaimRequest.CoverageBoundaryReached;
            Task firstCompleted = await Task.WhenAny(
                    coverageBoundaryReached,
                    earlyReclaim)
                .WaitAsync(cancellationToken);
            if (ReferenceEquals(firstCompleted, earlyReclaim))
            {
                await earlyReclaim;
                throw new InvalidOperationException(
                    "exhaustion 回收没有停在预期的 Gen2 覆盖边界。");
            }
            await coverageBoundaryReached;
            if (!graph.IsAlive)
            {
                throw new InvalidOperationException(
                    "exhaustion 测试图在引用释放前已不可达。");
            }

            int referenceCallbackCount = 0;
            string timing = pauseAfterCoverageCapture
                ? "after_coverage_capture"
                : "before_coverage_capture";
            cleanup = SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
                $"unattended_exhaustion_reference_coverage_{timing}",
                forceCollection: true,
                includeCombatLifecyclePressure: false,
                release,
                () => Interlocked.Increment(ref referenceCallbackCount));
            if (cleanup.IsCompleted)
                throw new InvalidOperationException("exhaustion 引用释放门没有等待测试图解绑。");
            referencesReleased.SetResult();
            while (SearchGcPolicy.ReferenceReleaseEpochForTesting == releaseEpochBefore)
                await Task.Delay(10, cancellationToken);
            if (SearchGcPolicy.ReferenceReleaseEpochForTesting
                != checked(releaseEpochBefore + 1))
            {
                throw new InvalidOperationException(
                    "exhaustion 覆盖边界测试观察到了意外的并发引用释放。");
            }
            SearchGcPolicy.ResumeGeneration2CoverageForTesting();
            await Task.WhenAll(earlyReclaim, cleanup).WaitAsync(cancellationToken);
            await SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
                    $"unattended_exhaustion_reference_coverage_{timing}_settled",
                    forceCollection: false,
                    includeCombatLifecyclePressure: false,
                    Task.CompletedTask,
                    static () => { })
                .WaitAsync(cancellationToken);
            int expectedJoinCount = pauseAfterCoverageCapture ? 0 : 1;
            if (referenceCallbackCount != 1
                || SearchGcPolicy.ReferenceReleaseEpochForTesting
                    != checked(releaseEpochBefore + 2)
                || SearchGcPolicy.BackgroundReclaimStartedCountForTesting
                    != expectedGeneration2Collections
                || SearchGcPolicy.BackgroundGen2CompletedCountForTesting
                    != expectedGeneration2Collections
                || SearchGcPolicy.BackgroundReclaimJoinCountForTesting != expectedJoinCount
                || graph.IsAlive)
            {
                throw new InvalidOperationException(
                    $"exhaustion 引用释放覆盖时序不正确：timing={timing} " +
                    $"callback={referenceCallbackCount} " +
                    $"reclaims={SearchGcPolicy.BackgroundReclaimStartedCountForTesting} " +
                    $"gen2={SearchGcPolicy.BackgroundGen2CompletedCountForTesting} " +
                    $"joins={SearchGcPolicy.BackgroundReclaimJoinCountForTesting} " +
                    $"graph_alive={graph.IsAlive}。");
            }
        }
        finally
        {
            referencesReleased.TrySetResult();
            SearchGcPolicy.ResumeGeneration2CoverageForTesting();
            await Task.WhenAll(earlyReclaim, cleanup)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Reference, Task Release) CreateHeldGraphForGcPolicyTest(
        Task releaseGate)
    {
        byte[][] graph = new byte[128][];
        for (int index = 0; index < graph.Length; index++)
        {
            graph[index] = new byte[32 * 1024];
            graph[index][0] = unchecked((byte)index);
        }
        StrongBox<object?> holder = new(graph);
        WeakReference reference = new(graph);
        Task release = ReleaseHeldGraphForGcPolicyTestAsync(holder, releaseGate);
        GC.KeepAlive(graph);
        return (reference, release);
    }

    private static async Task ReleaseHeldGraphForGcPolicyTestAsync(
        StrongBox<object?> holder,
        Task releaseGate)
    {
        await releaseGate;
        holder.Value = null;
    }

    private static async Task AssertReferenceReleaseBarrierAsync(
        long budgetBytes,
        CancellationToken cancellationToken)
    {
        IDisposable activeScope = SearchGcPolicy.EnterLowLatencySearch(
            budgetBytes,
            new SearchMemoryPressureSignal(),
            cancellationToken);
        TaskCompletionSource referencesReleased = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task regionExit = SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync(
            "unattended_reference_barrier_region_exit");
        int referenceCallbackCount = 0;
        Task cleanup = SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
            "unattended_reference_barrier",
            forceCollection: false,
            includeCombatLifecyclePressure: false,
            Task.WhenAll(regionExit, referencesReleased.Task),
            () => Interlocked.Increment(ref referenceCallbackCount));
        Task rootCaptureBarrier = SearchGcPolicy.CaptureRootSnapshotBarrier();
        if (rootCaptureBarrier.IsCompleted)
            throw new InvalidOperationException("根快照入口没有观察到旧战斗引用释放屏障。");

        TaskCompletionSource entrantStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource entrantEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task entrant = Task.Run(() =>
        {
            entrantStarted.TrySetResult();
            using IDisposable scope = SearchGcPolicy.EnterLowLatencySearch(
                budgetBytes,
                new SearchMemoryPressureSignal(),
                cancellationToken);
            entrantEntered.TrySetResult();
        }, cancellationToken);
        try
        {
            await entrantStarted.Task.WaitAsync(cancellationToken);
            await Task.Delay(50, cancellationToken);
            if (entrantEntered.Task.IsCompleted)
                throw new InvalidOperationException("新搜索在旧战斗引用释放屏障完成前进入了 GC 区域。");

            activeScope.Dispose();
            await regionExit.WaitAsync(cancellationToken);
            if (cleanup.IsCompleted)
                throw new InvalidOperationException("forensic/callback 引用尚未释放时提前完成了回收屏障。");
            referencesReleased.SetResult();
            await cleanup.WaitAsync(cancellationToken);
            await rootCaptureBarrier.WaitAsync(cancellationToken);
            await entrant.WaitAsync(cancellationToken);
            if (referenceCallbackCount != 1
                || !rootCaptureBarrier.IsCompletedSuccessfully
                || !entrantEntered.Task.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException(
                    $"引用释放屏障没有按序放行新搜索：callback={referenceCallbackCount} " +
                    $"root_capture={rootCaptureBarrier.Status} entrant={entrantEntered.Task.Status}。");
            }
        }
        finally
        {
            activeScope.Dispose();
            referencesReleased.TrySetResult();
            await cleanup.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await entrant.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync(
                    "unattended_reference_barrier_cleanup")
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        int suppressedReferenceCallbacks = 0;
        await SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
            "unattended_reference_barrier_fault",
            forceCollection: false,
            includeCombatLifecyclePressure: false,
            Task.FromException(new InvalidOperationException("expected reference fault")),
            () => Interlocked.Increment(ref suppressedReferenceCallbacks));
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
            "unattended_reference_barrier_cancel",
            forceCollection: false,
            includeCombatLifecyclePressure: false,
            Task.FromCanceled(canceled.Token),
            () => Interlocked.Increment(ref suppressedReferenceCallbacks));
        if (suppressedReferenceCallbacks != 2)
            throw new InvalidOperationException("fault/cancel 引用任务使后续 GC 屏障中毒。");

        List<int> serializedCallbacks = [];
        TaskCompletionSource firstReferences = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondReferences = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task firstCleanup = SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
            "unattended_reference_barrier_first",
            forceCollection: false,
            includeCombatLifecyclePressure: false,
            firstReferences.Task,
            () => serializedCallbacks.Add(1));
        Task secondCleanup = SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
            "unattended_reference_barrier_second",
            forceCollection: false,
            includeCombatLifecyclePressure: false,
            secondReferences.Task,
            () => serializedCallbacks.Add(2));
        try
        {
            secondReferences.SetResult();
            await Task.Delay(20, cancellationToken);
            if (secondCleanup.IsCompleted)
                throw new InvalidOperationException("后登记的跨战斗屏障越过了前一屏障。");
            firstReferences.SetResult();
            await Task.WhenAll(firstCleanup, secondCleanup).WaitAsync(cancellationToken);
            if (!serializedCallbacks.SequenceEqual([1, 2]))
                throw new InvalidOperationException("跨战斗引用释放屏障没有保持 FIFO 顺序。");
        }
        finally
        {
            firstReferences.TrySetResult();
            secondReferences.TrySetResult();
            await Task.WhenAll(firstCleanup, secondCleanup)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AllocateTransientSearchGraphForGcPolicyTest()
    {
        byte[][] graph = new byte[9_000][];
        for (int index = 0; index < graph.Length; index++)
        {
            graph[index] = new byte[32 * 1024];
            graph[index][0] = unchecked((byte)index);
        }
        WeakReference reference = new(graph);
        GC.KeepAlive(graph);
        return reference;
    }

    private static void AssertEquivalentSearchResults(
        SolverResult expected,
        SolverResult actual,
        string comparison)
    {
        List<string> mismatches = [];
        if (!ActionsEquivalent(expected.BestNode.Actions, actual.BestNode.Actions))
            mismatches.Add("best.actions");
        if (!ChoicesEquivalent(expected.TurnSetupChoices, actual.TurnSetupChoices))
            mismatches.Add("turn_setup_choices");
        if (!ContinuationEquivalent(expected.TurnSetupPlayState, actual.TurnSetupPlayState))
            mismatches.Add("turn_setup_play_state");
        if (!PredictionGapsEquivalent(
                expected.Snapshot.PredictionGaps,
                actual.Snapshot.PredictionGaps))
        {
            mismatches.Add("snapshot.prediction_gaps");
        }
        if (!IntDictionaryEquivalent(expected.SoldHpByTurn, actual.SoldHpByTurn))
            mismatches.Add("annotations.sold_hp_by_turn");
        if (!IntDictionaryEquivalent(expected.HpLostByTurn, actual.HpLostByTurn))
            mismatches.Add("annotations.hp_lost_by_turn");
        if (!IntDictionaryEquivalent(expected.MaxBlockByTurn, actual.MaxBlockByTurn))
            mismatches.Add("annotations.max_block_by_turn");
        if (!IntDictionaryEquivalent(expected.ActualBlockByTurn, actual.ActualBlockByTurn))
            mismatches.Add("annotations.actual_block_by_turn");
        if (!IntDictionaryEquivalent(expected.EnergyLeftByTurn, actual.EnergyLeftByTurn))
            mismatches.Add("annotations.energy_left_by_turn");
        if (!IntDictionaryEquivalent(expected.PotionCountByTurn, actual.PotionCountByTurn))
            mismatches.Add("annotations.potion_count_by_turn");
        if (!IntDictionaryEquivalent(
                expected.PotionStrategicCostByTurn,
                actual.PotionStrategicCostByTurn))
        {
            mismatches.Add("annotations.potion_strategic_cost_by_turn");
        }
        if (!KillsEquivalent(expected.KillsAfterAction, actual.KillsAfterAction))
            mismatches.Add("annotations.kills_after_action");
        if (!ContinuationsEquivalent(expected.Continuations, actual.Continuations))
            mismatches.Add("continuations");

        AddMismatch(mismatches, "search_phase", expected.SearchPhase, actual.SearchPhase);
        AddMismatch(mismatches, "start_turn", expected.StartTurnNumber, actual.StartTurnNumber);
        AddMismatch(mismatches, "best.action_count", expected.BestNode.ActionCount, actual.BestNode.ActionCount);
        AddMismatch(mismatches, "best.score", expected.BestNode.Score, actual.BestNode.Score);
        AddMismatch(mismatches, "expanded", expected.ExpandedNodes, actual.ExpandedNodes);
        AddMismatch(mismatches, "dominated", expected.DominatedActionsPruned, actual.DominatedActionsPruned);
        AddMismatch(mismatches, "top_queue", expected.TopQueueActionsDropped, actual.TopQueueActionsDropped);
        AddMismatch(
            mismatches,
            "action_admission_protected",
            expected.ActionAdmissionRepresentativesProtected,
            actual.ActionAdmissionRepresentativesProtected);
        AddMismatch(mismatches, "duplicate_cards", expected.DuplicateCardBranchesPruned, actual.DuplicateCardBranchesPruned);
        AddMismatch(mismatches, "transitions", expected.TransitionCount, actual.TransitionCount);
        AddMismatch(mismatches, "choices", expected.ChoiceBranchesEvaluated, actual.ChoiceBranchesEvaluated);
        AddMismatch(mismatches, "shuffles", expected.ShuffleBranchesPruned, actual.ShuffleBranchesPruned);
        AddMismatch(mismatches, "sold_hp_pruned", expected.SoldHpBranchesPruned, actual.SoldHpBranchesPruned);
        AddMismatch(
            mismatches,
            "hp_investment_protected",
            expected.HpInvestmentBranchesProtected,
            actual.HpInvestmentBranchesProtected);
        AddMismatch(mismatches, "replays", expected.ReplayCount, actual.ReplayCount);
        AddMismatch(mismatches, "forks", expected.ForkCount, actual.ForkCount);
        AddMismatch(mismatches, "reused", expected.ReusedNodeSnapshots, actual.ReusedNodeSnapshots);
        AddMismatch(mismatches, "tt_pruned", expected.TranspositionBranchesPruned, actual.TranspositionBranchesPruned);
        AddMismatch(mismatches, "repeatable", expected.RepeatableNoProgressBranchesPruned, actual.RepeatableNoProgressBranchesPruned);
        AddMismatch(mismatches, "stand_pat", expected.StandPatProbes, actual.StandPatProbes);
        AddMismatch(mismatches, "searched_turns", expected.SearchedTurns, actual.SearchedTurns);
        AddMismatch(mismatches, "boundary", expected.BoundaryReason, actual.BoundaryReason);
        AddMismatch(mismatches, "unavoidable_hp_lost", expected.UnavoidableHpLost, actual.UnavoidableHpLost);
        AddMismatch(mismatches, "sold_hp", expected.SoldHp, actual.SoldHp);
        AddMismatch(mismatches, "future_sold_hp", expected.FutureSoldHp, actual.FutureSoldHp);
        AddMismatch(mismatches, "battle_hp_lost", expected.BattleHpLostSoFar, actual.BattleHpLostSoFar);
        AddMismatch(mismatches, "projected_battle_hp_lost", expected.ProjectedBattleHpLost, actual.ProjectedBattleHpLost);
        AddMismatch(mismatches, "battle_potions_used", expected.BattlePotionsUsedSoFar, actual.BattlePotionsUsedSoFar);
        AddMismatch(mismatches, "potions", expected.PotionCount, actual.PotionCount);
        AddMismatch(mismatches, "potion_hp_saved", expected.PotionHpSaved, actual.PotionHpSaved);
        AddMismatch(mismatches, "potion_hp_required", expected.PotionHpRequired, actual.PotionHpRequired);
        AddMismatch(mismatches, "potion_branches_rejected", expected.PotionBranchesRejected, actual.PotionBranchesRejected);
        AddMismatch(mismatches, "theft_policy", expected.TheftPolicy, actual.TheftPolicy);
        AddMismatch(mismatches, "outstanding_stolen", expected.OutstandingStolenResource, actual.OutstandingStolenResource);
        AddMismatch(mismatches, "sold_hp_threshold", expected.SoldHpThreshold, actual.SoldHpThreshold);
        AddMismatch(mismatches, "combat_ended_turn", expected.CombatEndedTurn, actual.CombatEndedTurn);
        AddMismatch(mismatches, "death_turn", expected.DeathTurn, actual.DeathTurn);
        AddMismatch(mismatches, "only_death_routes", expected.OnlyDeathRoutesFound, actual.OnlyDeathRoutesFound);
        AddMismatch(mismatches, "act_ending_boss", expected.IsActEndingBoss, actual.IsActEndingBoss);

        AddMismatch(mismatches, "snapshot.risk", expected.Snapshot.HasRisk, actual.Snapshot.HasRisk);
        AddMismatch(mismatches, "snapshot.player_dead", expected.Snapshot.PlayerDead, actual.Snapshot.PlayerDead);
        AddMismatch(mismatches, "snapshot.enemies_dead", expected.Snapshot.AllEnemiesDead, actual.Snapshot.AllEnemiesDead);
        AddMismatch(mismatches, "snapshot.player_hp", expected.Snapshot.PlayerHp, actual.Snapshot.PlayerHp);
        AddMismatch(mismatches, "snapshot.player_max_hp", expected.Snapshot.PlayerMaxHp, actual.Snapshot.PlayerMaxHp);
        AddMismatch(mismatches, "snapshot.hp_lost", expected.Snapshot.CumulativePlayerHpLost, actual.Snapshot.CumulativePlayerHpLost);
        AddMismatch(mismatches, "snapshot.long_term", expected.Snapshot.LongTermResourceValue, actual.Snapshot.LongTermResourceValue);
        AddMismatch(mismatches, "snapshot.anger", expected.Snapshot.AngerCopiesGenerated, actual.Snapshot.AngerCopiesGenerated);
        AddMismatch(mismatches, "snapshot.projected_hp", expected.Snapshot.ProjectedPlayerHp, actual.Snapshot.ProjectedPlayerHp);
        AddMismatch(mismatches, "snapshot.block", expected.Snapshot.PlayerBlock, actual.Snapshot.PlayerBlock);
        AddMismatch(mismatches, "snapshot.enemy_hp", expected.Snapshot.EnemyHp, actual.Snapshot.EnemyHp);
        AddMismatch(mismatches, "snapshot.alive_enemies", expected.Snapshot.AliveEnemyCount, actual.Snapshot.AliveEnemyCount);
        AddMismatch(mismatches, "snapshot.energy", expected.Snapshot.Energy, actual.Snapshot.Energy);
        AddMismatch(mismatches, "snapshot.stars", expected.Snapshot.Stars, actual.Snapshot.Stars);
        AddMismatch(mismatches, "snapshot.hand", expected.Snapshot.HandCount, actual.Snapshot.HandCount);
        AddMismatch(mismatches, "snapshot.stolen", expected.Snapshot.OutstandingStolenResource, actual.Snapshot.OutstandingStolenResource);
        AddMismatch(mismatches, "snapshot.turn", expected.Snapshot.Turn, actual.Snapshot.Turn);
        AddMismatch(mismatches, "snapshot.shuffles", expected.Snapshot.ShufflesCrossed, actual.Snapshot.ShufflesCrossed);
        AddMismatch(mismatches, "snapshot.boundary", expected.Snapshot.BoundaryReason, actual.Snapshot.BoundaryReason);

        if (mismatches.Count == 0)
            return;
        throw new InvalidOperationException(
            $"搜索确定性比较 {comparison} 产生了不同结果：" +
            $"mismatches={string.Join(',', mismatches)} " +
            $"expected={DescribeResult(expected)} actual={DescribeResult(actual)}。");
    }

    private static void AddMismatch<T>(
        ICollection<string> mismatches,
        string name,
        T expected,
        T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            mismatches.Add(name);
    }

    private static bool ActionsEquivalent(
        IReadOnlyList<PlanAction> expected,
        IReadOnlyList<PlanAction> actual)
        => SequencesEquivalent(expected, actual, ActionEquivalent);

    private static bool ActionEquivalent(PlanAction expected, PlanAction actual)
        => expected.Kind == actual.Kind
            && expected.Turn == actual.Turn
            && string.Equals(expected.CardId, actual.CardId, StringComparison.Ordinal)
            && expected.CardOccurrence == actual.CardOccurrence
            && expected.TargetIndex == actual.TargetIndex
            && expected.TargetCombatId == actual.TargetCombatId
            && string.Equals(expected.CardTitle, actual.CardTitle, StringComparison.Ordinal)
            && string.Equals(expected.TargetName, actual.TargetName, StringComparison.Ordinal)
            && ChoiceEquivalent(expected.Choice, actual.Choice)
            && OptionalChoicesEquivalent(expected.NestedChoices, actual.NestedChoices)
            && expected.NestedChoicesBeforePrimary == actual.NestedChoicesBeforePrimary
            && expected.PotionSlot == actual.PotionSlot
            && string.Equals(expected.PotionId, actual.PotionId, StringComparison.Ordinal)
            && string.Equals(expected.PotionTitle, actual.PotionTitle, StringComparison.Ordinal)
            && OptionalChoicesEquivalent(expected.TurnStartChoices, actual.TurnStartChoices)
            && OptionalRelicEffectsEquivalent(expected.RelicEffects, actual.RelicEffects)
            && expected.ReplayCount == actual.ReplayCount;

    private static bool ChoicesEquivalent(
        IReadOnlyList<PlanCardChoice> expected,
        IReadOnlyList<PlanCardChoice> actual)
        => SequencesEquivalent(expected, actual, ChoiceEquivalent);

    private static bool OptionalChoicesEquivalent(
        IReadOnlyList<PlanCardChoice>? expected,
        IReadOnlyList<PlanCardChoice>? actual)
        => OptionalSequencesEquivalent(expected, actual, ChoiceEquivalent);

    private static bool ChoiceEquivalent(PlanCardChoice? expected, PlanCardChoice? actual)
    {
        if (expected == null || actual == null)
            return expected == null && actual == null;
        return expected.Effect == actual.Effect
            && expected.SourcePile == actual.SourcePile
            && SequencesEquivalent(expected.Cards, actual.Cards, CardTokenEquivalent)
            && string.Equals(expected.SourceId, actual.SourceId, StringComparison.Ordinal)
            && string.Equals(expected.ContextId, actual.ContextId, StringComparison.Ordinal)
            && expected.Timing == actual.Timing;
    }

    private static bool CardTokenEquivalent(PlanCardToken expected, PlanCardToken actual)
        => string.Equals(expected.CardId, actual.CardId, StringComparison.Ordinal)
            && expected.UpgradeLevel == actual.UpgradeLevel
            && string.Equals(expected.StateKey, actual.StateKey, StringComparison.Ordinal)
            && expected.SourceOccurrence == actual.SourceOccurrence
            && expected.OptionOccurrence == actual.OptionOccurrence
            && string.Equals(expected.Title, actual.Title, StringComparison.Ordinal);

    private static bool OptionalRelicEffectsEquivalent(
        IReadOnlyList<PlanRelicEffect>? expected,
        IReadOnlyList<PlanRelicEffect>? actual)
        => OptionalSequencesEquivalent(expected, actual, RelicEffectEquivalent);

    private static bool RelicEffectEquivalent(PlanRelicEffect expected, PlanRelicEffect actual)
        => string.Equals(expected.RelicId, actual.RelicId, StringComparison.Ordinal)
            && string.Equals(expected.RelicTitle, actual.RelicTitle, StringComparison.Ordinal)
            && string.Equals(expected.Summary, actual.Summary, StringComparison.Ordinal);

    private static bool PredictionGapsEquivalent(
        IReadOnlyList<PredictionGap> expected,
        IReadOnlyList<PredictionGap> actual)
        => SequencesEquivalent(expected, actual, static (left, right) =>
            string.Equals(left.SourceId, right.SourceId, StringComparison.Ordinal)
            && string.Equals(left.Method, right.Method, StringComparison.Ordinal)
            && string.Equals(left.Reason, right.Reason, StringComparison.Ordinal)
            && left.Compensated == right.Compensated);

    private static bool ContinuationEquivalent(
        ContinuationStamp? expected,
        ContinuationStamp? actual)
        => expected == null || actual == null
            ? expected == null && actual == null
            : string.Equals(expected.StateText, actual.StateText, StringComparison.Ordinal);

    private static bool ContinuationsEquivalent(
        IReadOnlyList<CachedContinuation> expected,
        IReadOnlyList<CachedContinuation> actual)
        => SequencesEquivalent(expected, actual, static (left, right) =>
            string.Equals(
                left.ExpectedState.StateText,
                right.ExpectedState.StateText,
                StringComparison.Ordinal)
            && left.StartTurnNumber == right.StartTurnNumber
            && left.ForecastOffset == right.ForecastOffset);

    private static bool IntDictionaryEquivalent(
        IReadOnlyDictionary<int, int> expected,
        IReadOnlyDictionary<int, int> actual)
        => expected.Count == actual.Count
            && expected.All(item => actual.TryGetValue(item.Key, out int value) && value == item.Value);

    private static bool KillsEquivalent(
        IReadOnlyDictionary<int, IReadOnlyList<string>> expected,
        IReadOnlyDictionary<int, IReadOnlyList<string>> actual)
        => expected.Count == actual.Count
            && expected.All(item =>
                actual.TryGetValue(item.Key, out IReadOnlyList<string>? values)
                && item.Value.SequenceEqual(values, StringComparer.Ordinal));

    private static bool OptionalSequencesEquivalent<T>(
        IReadOnlyList<T>? expected,
        IReadOnlyList<T>? actual,
        Func<T, T, bool> equivalent)
    {
        if (expected == null || actual == null)
            return expected == null && actual == null;
        return SequencesEquivalent(expected, actual, equivalent);
    }

    private static bool SequencesEquivalent<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        Func<T, T, bool> equivalent)
    {
        if (expected.Count != actual.Count)
            return false;
        for (int index = 0; index < expected.Count; index++)
        {
            if (!equivalent(expected[index], actual[index]))
                return false;
        }
        return true;
    }

    private static string DescribeResult(SolverResult result)
        => $"{{actions={DescribeActions(result)} action_count={result.BestNode.ActionCount} " +
            $"score={result.BestNode.Score:R} expanded={result.ExpandedNodes} " +
            $"dominated={result.DominatedActionsPruned} top_queue={result.TopQueueActionsDropped} " +
            $"action_admission_protected={result.ActionAdmissionRepresentativesProtected} " +
            $"duplicate_cards={result.DuplicateCardBranchesPruned} transitions={result.TransitionCount} " +
            $"choices={result.ChoiceBranchesEvaluated} shuffles={result.ShuffleBranchesPruned} " +
            $"sold_hp={result.SoldHpBranchesPruned} " +
            $"hp_investment_protected={result.HpInvestmentBranchesProtected} " +
            $"replays={result.ReplayCount} forks={result.ForkCount} " +
            $"reused={result.ReusedNodeSnapshots} tt_pruned={result.TranspositionBranchesPruned} " +
            $"repeatable={result.RepeatableNoProgressBranchesPruned} stand_pat={result.StandPatProbes} " +
            $"turns={result.SearchedTurns} potions={result.PotionCount} future_sold={result.FutureSoldHp} " +
            $"projected_battle_hp_lost={result.ProjectedBattleHpLost} boundary={result.BoundaryReason} " +
            $"turn_setup=[{string.Join(';', result.TurnSetupChoices.Select(DescribeChoice))}] " +
            $"annotations=[sold={DescribeDictionary(result.SoldHpByTurn)} " +
            $"hp={DescribeDictionary(result.HpLostByTurn)} max_block={DescribeDictionary(result.MaxBlockByTurn)} " +
            $"block={DescribeDictionary(result.ActualBlockByTurn)} energy={DescribeDictionary(result.EnergyLeftByTurn)} " +
            $"potions={DescribeDictionary(result.PotionCountByTurn)} " +
            $"potion_cost={DescribeDictionary(result.PotionStrategicCostByTurn)} " +
            $"kills={DescribeKills(result.KillsAfterAction)}] " +
            $"snapshot=[risk={result.Snapshot.HasRisk} dead={result.Snapshot.PlayerDead} " +
            $"enemies_dead={result.Snapshot.AllEnemiesDead} hp={result.Snapshot.PlayerHp} " +
            $"max_hp={result.Snapshot.PlayerMaxHp} hp_lost={result.Snapshot.CumulativePlayerHpLost} " +
            $"long_term={result.Snapshot.LongTermResourceValue} anger={result.Snapshot.AngerCopiesGenerated} " +
            $"projected_hp={result.Snapshot.ProjectedPlayerHp} block={result.Snapshot.PlayerBlock} " +
            $"enemy_hp={result.Snapshot.EnemyHp} alive={result.Snapshot.AliveEnemyCount} " +
            $"energy={result.Snapshot.Energy} stars={result.Snapshot.Stars} hand={result.Snapshot.HandCount} " +
            $"stolen={result.Snapshot.OutstandingStolenResource} turn={result.Snapshot.Turn} " +
            $"shuffles={result.Snapshot.ShufflesCrossed} boundary={result.Snapshot.BoundaryReason} " +
            $"gaps={string.Join(',', result.Snapshot.PredictionGaps)}]}}";

    private static string DescribeActions(SolverResult result)
        => string.Join(',', result.BestNode.Actions.Select(DescribeAction));

    private static string DescribeAction(PlanAction action)
    {
        string identity = action.Kind switch
        {
            PlanActionKind.PlayCard => $"card:{action.CardId}:{action.CardOccurrence}:{action.TargetCombatId}",
            PlanActionKind.UsePotion => $"potion:{action.PotionId}:{action.PotionSlot}:{action.TargetCombatId}",
            PlanActionKind.EndTurn => "end",
            _ => throw new ArgumentOutOfRangeException(nameof(action.Kind), action.Kind, null),
        };
        return $"{action.Turn}:{identity}" +
            $"[choice={DescribeChoice(action.Choice)};" +
            $"nested={DescribeOptionalChoices(action.NestedChoices)};" +
            $"nested_before={action.NestedChoicesBeforePrimary};" +
            $"turn_start={DescribeOptionalChoices(action.TurnStartChoices)};" +
            $"relics={DescribeRelicEffects(action.RelicEffects)};replay={action.ReplayCount}]";
    }

    private static string DescribeChoice(PlanCardChoice? choice)
        => choice == null
            ? "-"
            : $"{choice.Timing}:{choice.SourceId}:{choice.ContextId}:{choice.Effect}:{choice.SourcePile}:" +
                string.Join('+', choice.Cards.Select(DescribeCardToken));

    private static string DescribeCardToken(PlanCardToken token)
        => $"{token.CardId}+{token.UpgradeLevel}:{token.StateKey}:" +
            $"{token.SourceOccurrence}:{token.OptionOccurrence}:{token.Title}";

    private static string DescribeOptionalChoices(IReadOnlyList<PlanCardChoice>? choices)
        => choices == null ? "null" : string.Join('|', choices.Select(DescribeChoice));

    private static string DescribeRelicEffects(IReadOnlyList<PlanRelicEffect>? effects)
        => effects == null
            ? "null"
            : string.Join('|', effects.Select(effect =>
                $"{effect.RelicId}:{effect.RelicTitle}:{effect.Summary}"));

    private static string DescribeDictionary(IReadOnlyDictionary<int, int> values)
        => string.Join(',', values.OrderBy(item => item.Key).Select(item => $"{item.Key}:{item.Value}"));

    private static string DescribeKills(IReadOnlyDictionary<int, IReadOnlyList<string>> values)
        => string.Join(',', values
            .OrderBy(item => item.Key)
            .Select(item => $"{item.Key}:{string.Join('+', item.Value)}"));
}
