using System.Runtime;
using CollectionObservation = CombatSolver.SearchGcPolicy.BackgroundCollectionObservation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // Lifecycle contracts, not a search, a pause benchmark, or proof that the CLR always
    // selects concurrent GC. The pause hook holds confirmation after an actual GC request;
    // the native collector may already have finished while that confirmation is held.
    private async Task RunGcCheckpointBackgroundFixtureAsync()
    {
        EnsureWithinDeadline();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(
            Math.Max(0.001, _request.TimeoutSeconds - _stopwatch.Elapsed.TotalSeconds)));
        CancellationToken token = deadline.Token;
        const long budgetBytes = 1_000_000_000L;

        AssertBackgroundCollectionObservationOrder();
        _completedChecks.Add("GcCheckpointBackground:ObserveBeforeReissue:NewestCompletedKind");
        await AssertBackgroundCheckpointSynchronizationContextAsync(budgetBytes, token);
        _completedChecks.Add("GcCheckpointBackground:ConfirmedRestart:NoContextCapture");
        await AssertBackgroundCheckpointCancellationAndReleaseAsync(budgetBytes, token);
        _completedChecks.Add("GcCheckpointBackground:CanceledConfirmation:LateManual:PostReleaseEpoch");
        await AssertBackgroundCheckpointTimeoutDrainAsync(budgetBytes, token);
        _completedChecks.Add("GcCheckpointBackground:TimeoutDrained:DefaultGc:ManualCompletion");

        // These existing narrow contracts exercise the shared collector's callers, without
        // rerunning the search/strategy parts of the SearchPolicy fixture.
        await AssertManualGcAtInSearchCheckpointAsync(budgetBytes, token);
        _completedChecks.Add("GcCheckpointBackground:ExistingEarlyManualAbsorption");
        await AssertDeferredReclaimSurvivesFaultedCheckpointAsync(budgetBytes, token);
        _completedChecks.Add("GcCheckpointBackground:ExistingPreCollectionFailure");
        await AssertExhaustionReclaimReferenceCoverageTimingAsync(false, 1, token);
        _completedChecks.Add("GcCheckpointBackground:ExistingPostSearchEpochBeforeCoverageCapture");
        await AssertExhaustionReclaimReferenceCoverageTimingAsync(true, 2, token);
        _completedChecks.Add("GcCheckpointBackground:ExistingPostSearchEpochAfterCoverageCapture");
        EnsureWithinDeadline();
    }

    private static void AssertBackgroundCollectionObservationOrder()
    {
        (long BackgroundBefore, long BlockingBefore, long Background, long Blocking,
            bool SentinelAlive, CollectionObservation Expected)[] cases =
        [
            (10, 11, 10, 11, true, CollectionObservation.Waiting),
            (10, 11, 10, 11, false, CollectionObservation.Waiting),
            (10, 11, 12, 11, false, CollectionObservation.CompletedBackground),
            (10, 11, 10, 12, false, CollectionObservation.CompletedFullBlocking),
            (10, 11, 12, 13, false, CollectionObservation.CompletedFullBlocking),
            (10, 11, 13, 12, false, CollectionObservation.CompletedBackground),
            (10, 11, 12, 11, true, CollectionObservation.RequestFreshCollection),
            (10, 11, 12, 13, true, CollectionObservation.RequestFreshCollection),
        ];
        foreach (var item in cases)
        {
            CollectionObservation observed = SearchGcPolicy.ObserveBackgroundCollection(
                item.BackgroundBefore, item.BlockingBefore, item.Background, item.Blocking,
                item.SentinelAlive);
            if (observed != item.Expected)
                throw new InvalidOperationException($"Gen2 完成观察错误：{observed}，预期 {item.Expected}。");
        }
        // A completed old request with a cleared sentinel must select completion, not another
        // request. The production loop evaluates this decision before its only request branch.
    }

    private sealed class GcCheckpointTrackingContext : SynchronizationContext
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _posts);
            // Let an accidental capture finish so the assertion reports it instead of
            // permanently deadlocking this fixture's cleanup.
            base.Post(callback, state);
        }
    }

    private static void AssertBackgroundCheckpointEntered(SearchMemoryPressureSignal signal)
    {
        if (!signal.IsEnabled || GCSettings.LatencyMode != GCLatencyMode.NoGCRegion
            || SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting <= 0)
            throw new InvalidOperationException("后台 checkpoint 合同要求实际建立 NoGC 区域。");
    }

    private static async Task WaitForBackgroundCheckpointConfirmationAsync(
        Task reached, Task checkpoint, CancellationToken token)
    {
        Task completed = await Task.WhenAny(reached, checkpoint).WaitAsync(token);
        if (ReferenceEquals(completed, checkpoint))
        {
            await checkpoint;
            throw new InvalidOperationException("后台 checkpoint 没有到达 GC 请求后的确认边界。");
        }
        await reached;
        if (!SearchGcPolicy.InSearchBackgroundCollectionForTesting.ConfirmationPending
            || SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting != 0
            || GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
            throw new InvalidOperationException("GC 确认前没有独占 checkpoint 或仍持有 NoGC 区域。");
    }

    private static async Task AssertBackgroundCheckpointSynchronizationContextAsync(
        long budgetBytes, CancellationToken token)
    {
        await SearchGcPolicy.ReclaimIfPendingAsync("unattended_background_context_setup", true);
        SearchGcPolicy.ResetCountersForTesting();
        SearchMemoryPressureSignal signal = new();
        IDisposable? scope = SearchGcPolicy.EnterLowLatencySearch(budgetBytes, signal, token);
        Task? checkpoint = null;
        GcCheckpointTrackingContext context = new();
        try
        {
            AssertBackgroundCheckpointEntered(signal);
            Task reached = SearchGcPolicy.PauseNextInSearchCollectionForTesting();
            checkpoint = Task.Run(() =>
            {
                SynchronizationContext? previous = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(context);
                try
                {
                    signal.ReclaimAndContinue(token);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                }
            }, CancellationToken.None);
            await WaitForBackgroundCheckpointConfirmationAsync(reached, checkpoint, token);
            if (checkpoint.IsCompleted)
                throw new InvalidOperationException("确认暂停期间 checkpoint 提前返回。");
            SearchGcPolicy.ResumeInSearchCollectionForTesting();
            await checkpoint.WaitAsync(token);
            var state = SearchGcPolicy.InSearchBackgroundCollectionForTesting;
            if (state.ConfirmationPending || state.Completed != 1 || state.TimeoutDrains != 0
                || context.Posts != 0 || signal.ReclaimCount != 1)
                throw new InvalidOperationException("checkpoint 未确认一次完成，或同步等待捕获了调用方上下文。");
            AssertBackgroundCheckpointEntered(signal);
        }
        finally
        {
            SearchGcPolicy.ResumeInSearchCollectionForTesting();
            if (checkpoint != null)
                await checkpoint.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            scope?.Dispose();
            await SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync("no_gc_disabled")
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SearchGcPolicy.ReclaimIfPendingAsync("unattended_background_context_cleanup", true)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task AssertBackgroundCheckpointCancellationAndReleaseAsync(
        long budgetBytes, CancellationToken token)
    {
        await SearchGcPolicy.ReclaimIfPendingAsync("unattended_background_cancel_setup", true);
        SearchGcPolicy.ResetCountersForTesting();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        SearchMemoryPressureSignal signal = new();
        IDisposable? scope = SearchGcPolicy.EnterLowLatencySearch(budgetBytes, signal, token);
        TaskCompletionSource releaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (WeakReference graph, Task released) = CreateHeldGraphForGcPolicyTest(releaseGate.Task);
        Task? checkpoint = null;
        Task? earlyManual = null;
        Task? lateManual = null;
        Task? releaseReclaim = null;
        try
        {
            AssertBackgroundCheckpointEntered(signal);
            earlyManual = SearchGcPolicy.ForceManualGc();
            Task reached = SearchGcPolicy.PauseNextInSearchCollectionForTesting();
            checkpoint = Task.Run(() => signal.ReclaimAndContinue(cancellation.Token), CancellationToken.None);
            await WaitForBackgroundCheckpointConfirmationAsync(reached, checkpoint, token);
            lateManual = SearchGcPolicy.ForceManualGc();
            if (earlyManual.IsCompleted || lateManual.IsCompleted
                || ReferenceEquals(earlyManual, lateManual) || !graph.IsAlive)
                throw new InvalidOperationException("开始前/后的手动 GC 错误共用完成，或测试图提前释放。");

            long epoch = SearchGcPolicy.ReferenceReleaseEpochForTesting;
            int releasedCallbacks = 0;
            releaseReclaim = SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
                "unattended_background_cancel_post_request_release", true, false, released,
                () => Interlocked.Increment(ref releasedCallbacks));
            releaseGate.SetResult();
            while (SearchGcPolicy.ReferenceReleaseEpochForTesting == epoch)
                await Task.Delay(10, token);
            cancellation.Cancel();
            await Task.Delay(25, token);
            if (checkpoint.IsCompleted || earlyManual.IsCompleted || lateManual.IsCompleted
                || releaseReclaim.IsCompleted)
                throw new InvalidOperationException("取消/新引用释放提前落定了仍待确认的 GC 完成链。");

            SearchGcPolicy.ResumeInSearchCollectionForTesting();
            try
            {
                await checkpoint.WaitAsync(token);
                throw new InvalidOperationException("确认完成后的 checkpoint 未传播取消。");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested && !token.IsCancellationRequested)
            {
            }
            await earlyManual.WaitAsync(token);
            var state = SearchGcPolicy.InSearchBackgroundCollectionForTesting;
            if (state.ConfirmationPending || state.Completed != 1 || state.TimeoutDrains != 0
                || signal.IsEnabled || SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting != 0
                || GCSettings.LatencyMode == GCLatencyMode.NoGCRegion
                || lateManual.IsCompleted || releaseReclaim.IsCompleted
                || SearchGcPolicy.BackgroundGen2CompletedCountForTesting != 0)
                throw new InvalidOperationException("取消未发布默认 GC，或 checkpoint 吞掉了晚到的回收义务。");

            scope.Dispose();
            scope = null;
            await Task.WhenAll(lateManual, releaseReclaim).WaitAsync(token);
            if (releasedCallbacks != 1 || graph.IsAlive
                || SearchGcPolicy.ReferenceReleaseEpochForTesting != epoch + 1
                || SearchGcPolicy.BackgroundReclaimStartedCountForTesting != 1
                || SearchGcPolicy.BackgroundGen2CompletedCountForTesting != 1)
                throw new InvalidOperationException("搜索退出后未独立覆盖新引用释放与晚到手动 GC。");
        }
        finally
        {
            releaseGate.TrySetResult();
            cancellation.Cancel();
            SearchGcPolicy.ResumeInSearchCollectionForTesting();
            if (checkpoint != null)
                await checkpoint.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            scope?.Dispose();
            await Task.WhenAll(earlyManual ?? Task.CompletedTask, lateManual ?? Task.CompletedTask,
                    releaseReclaim ?? Task.CompletedTask, released)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync("no_gc_disabled")
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SearchGcPolicy.ReclaimIfPendingAsync("unattended_background_cancel_cleanup", true)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task AssertBackgroundCheckpointTimeoutDrainAsync(
        long budgetBytes, CancellationToken token)
    {
        await SearchGcPolicy.ReclaimIfPendingAsync("unattended_background_timeout_setup", true);
        SearchGcPolicy.ResetCountersForTesting();
        SearchMemoryPressureSignal signal = new();
        IDisposable? scope = SearchGcPolicy.EnterLowLatencySearch(budgetBytes, signal, token);
        Task? checkpoint = null;
        Task? manual = null;
        try
        {
            AssertBackgroundCheckpointEntered(signal);
            Task beforeCollection = SearchGcPolicy.PauseNextInSearchCheckpointForTesting();
            Task reached = SearchGcPolicy.PauseNextInSearchCollectionForTesting(timeoutOnResume: true);
            checkpoint = Task.Run(() => signal.ReclaimAndContinue(token), CancellationToken.None);
            Task first = await Task.WhenAny(beforeCollection, checkpoint).WaitAsync(token);
            if (ReferenceEquals(first, checkpoint))
            {
                await checkpoint;
                throw new InvalidOperationException("超时测试没有到达 collection 前的手动加入边界。");
            }
            await beforeCollection;
            manual = SearchGcPolicy.ForceManualGc();
            SearchGcPolicy.ResumeInSearchCheckpointForTesting();
            await WaitForBackgroundCheckpointConfirmationAsync(reached, checkpoint, token);
            if (manual.IsCompleted || checkpoint.IsCompleted)
                throw new InvalidOperationException("超时排空前提前发布了完成。");
            SearchGcPolicy.ResumeInSearchCollectionForTesting();
            try
            {
                await checkpoint.WaitAsync(token);
                throw new InvalidOperationException("注入的后台 GC 超时没有传播。");
            }
            catch (TimeoutException error) when (error.Message.Contains("已通过阻塞回收排空", StringComparison.Ordinal))
            {
            }
            await manual.WaitAsync(token);
            var state = SearchGcPolicy.InSearchBackgroundCollectionForTesting;
            if (state.ConfirmationPending || state.Completed != 1 || state.TimeoutDrains != 1
                || signal.IsEnabled || GCSettings.LatencyMode == GCLatencyMode.NoGCRegion
                || SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting != 0)
                throw new InvalidOperationException("超时排空没有完成，或错误重建了 NoGC。");
            scope.Dispose();
            scope = null;
            using IDisposable next = SearchGcPolicy.EnterLowLatencySearch(
                enableNoGcRegion: false, budgetBytes, new SearchMemoryPressureSignal(), token);
        }
        finally
        {
            SearchGcPolicy.ResumeInSearchCheckpointForTesting();
            SearchGcPolicy.ResumeInSearchCollectionForTesting();
            if (checkpoint != null)
                await checkpoint.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            scope?.Dispose();
            if (manual != null)
                await manual.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync("no_gc_disabled")
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SearchGcPolicy.ReclaimIfPendingAsync("unattended_background_timeout_cleanup", true)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
