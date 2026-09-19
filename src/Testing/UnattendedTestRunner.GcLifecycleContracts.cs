using System.Runtime;
using System.Runtime.CompilerServices;

namespace CombatSolver;

// Shared unchanged CLR contracts: linked by GcPolicyChecks without loading Godot.
internal sealed partial class UnattendedTestRunner
{
    private static async Task AssertManualGcAtInSearchCheckpointAsync(
        long budgetBytes,
        CancellationToken cancellationToken)
    {
        await SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_manual_gc_checkpoint_setup",
            forceCollection: true);
        SearchGcPolicy.ResetCountersForTesting();
        SearchMemoryPressureSignal signal = new();
        IDisposable? scope = SearchGcPolicy.EnterLowLatencySearch(
            enableNoGcRegion: true,
            budgetBytes,
            signal,
            cancellationToken);
        Task? manualGc = null;
        Task? checkpoint = null;
        int generation2Before = GC.CollectionCount(GC.MaxGeneration);
        try
        {
            manualGc = SearchGcPolicy.ForceManualGc();
            if (manualGc.IsCompleted)
                throw new InvalidOperationException("活动 NoGC 搜索中的手动 GC 没有排队。");
            checkpoint = Task.Run(
                () => signal.ReclaimAndContinue(cancellationToken),
                cancellationToken);
            await Task.WhenAll(manualGc, checkpoint).WaitAsync(cancellationToken);
            if (signal.ReclaimCount != 1
                || GC.CollectionCount(GC.MaxGeneration) <= generation2Before
                || SearchGcPolicy.BackgroundReclaimStartedCountForTesting != 0
                || SearchGcPolicy.BackgroundGen2CompletedCountForTesting != 0)
            {
                throw new InvalidOperationException(
                    $"搜索内检查点没有恰好吸收手动 GC：" +
                    $"checkpoints={signal.ReclaimCount} " +
                    $"gen2_delta={GC.CollectionCount(GC.MaxGeneration) - generation2Before} " +
                    $"background_reclaims={SearchGcPolicy.BackgroundReclaimStartedCountForTesting} " +
                    $"background_gen2={SearchGcPolicy.BackgroundGen2CompletedCountForTesting}。");
            }
        }
        finally
        {
            scope?.Dispose();
            scope = null;
            if (checkpoint != null)
                await checkpoint.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (manualGc != null)
                await manualGc.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync("no_gc_disabled")
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SearchGcPolicy.ReclaimIfPendingAsync(
                    "unattended_manual_gc_checkpoint_cleanup")
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        // The assertion above pins zero post-search requests while the scope is active.
        // Windows then drains its replacement region once on normal scope exit.
        int expectedExitCollections = OperatingSystem.IsWindows() ? 1 : 0;
        if (SearchGcPolicy.BackgroundReclaimStartedCountForTesting != expectedExitCollections
            || SearchGcPolicy.BackgroundGen2CompletedCountForTesting != expectedExitCollections)
        {
            throw new InvalidOperationException(
                "检查点吸收手动 GC 后，搜索退出回收次数与平台策略不一致。");
        }
    }

    private static async Task AssertDeferredReclaimSurvivesFaultedCheckpointAsync(
        long budgetBytes,
        CancellationToken cancellationToken)
    {
        await SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_faulted_checkpoint_deferred_setup",
            forceCollection: true);
        SearchGcPolicy.ResetCountersForTesting();
        SearchMemoryPressureSignal signal = new();
        IDisposable? scope = SearchGcPolicy.EnterLowLatencySearch(
            enableNoGcRegion: true,
            budgetBytes,
            signal,
            cancellationToken);
        Task? checkpoint = null;
        Task? checkpointJoin = null;
        Task? deferredReclaim = null;
        try
        {
            Task checkpointReached = SearchGcPolicy.PauseNextInSearchCheckpointForTesting();
            checkpoint = Task.Run(
                () => signal.ReclaimAndContinue(cancellationToken),
                CancellationToken.None);
            Task firstCompleted = await Task.WhenAny(checkpointReached, checkpoint)
                .WaitAsync(cancellationToken);
            if (ReferenceEquals(firstCompleted, checkpoint))
            {
                await checkpoint;
                throw new InvalidOperationException(
                    "搜索内 checkpoint 没有停在 fault/deferred 竞态边界。");
            }
            await checkpointReached;

            checkpointJoin = SearchGcPolicy.ForceManualGc();
            deferredReclaim = SearchGcPolicy.ReclaimIfPendingAsync(
                "unattended_deferred_after_checkpoint_started",
                forceCollection: true,
                includeCombatLifecyclePressure: false);
            if (deferredReclaim.IsCompleted || ReferenceEquals(checkpointJoin, deferredReclaim))
            {
                throw new InvalidOperationException(
                    "checkpoint 加入者与 post-search deferred 请求错误共享了完成信号。");
            }

            SearchGcPolicy.FailNextInSearchCheckpointAfterTransitionForTesting();
            SearchGcPolicy.ResumeInSearchCheckpointForTesting();
            await AssertInjectedInSearchCheckpointFailureAsync(
                checkpoint,
                "checkpoint caller",
                cancellationToken);
            await AssertInjectedInSearchCheckpointFailureAsync(
                checkpointJoin,
                "checkpoint joiner",
                cancellationToken);
            if (deferredReclaim.IsCompleted)
            {
                throw new InvalidOperationException(
                    "faulted checkpoint 提前落定了独立的 post-search deferred 请求。");
            }

            scope.Dispose();
            scope = null;
            await deferredReclaim.WaitAsync(cancellationToken);
            if (SearchGcPolicy.BackgroundReclaimStartedCountForTesting != 1
                || SearchGcPolicy.BackgroundGen2CompletedCountForTesting != 1)
            {
                throw new InvalidOperationException(
                    $"faulted checkpoint 后的 deferred 请求没有恢复：" +
                    $"reclaims={SearchGcPolicy.BackgroundReclaimStartedCountForTesting} " +
                    $"gen2={SearchGcPolicy.BackgroundGen2CompletedCountForTesting}。");
            }
        }
        finally
        {
            SearchGcPolicy.ResumeInSearchCheckpointForTesting();
            if (checkpoint != null)
                await checkpoint.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (checkpointJoin != null)
                await checkpointJoin.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            scope?.Dispose();
            if (deferredReclaim != null)
            {
                await deferredReclaim
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            SearchGcPolicy.ResetCountersForTesting();
            await SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync("no_gc_disabled")
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SearchGcPolicy.ReclaimIfPendingAsync(
                    "unattended_faulted_checkpoint_deferred_cleanup",
                    forceCollection: true)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task AssertInjectedInSearchCheckpointFailureAsync(
        Task operation,
        string waiter,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation.WaitAsync(cancellationToken);
        }
        catch (InvalidOperationException ex) when (
            ex.Message == "无人测试注入的搜索内 GC checkpoint 失败。")
        {
            return;
        }
        throw new InvalidOperationException($"{waiter} 没有收到同一个 checkpoint 失败。");
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
}
