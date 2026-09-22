using System.Diagnostics;
using System.Runtime;

namespace CombatSolver;

internal static class GcBetweenSearchChecks
{
    public static void Run()
    {
        PolicyCheck.Run("between-search checkpoint skips first and unconfigured calls", () =>
        {
            SearchMemoryPressureSignal signal = new();
            int calls = 0;
            signal.SetBetweenSearchCheckpoint(_ => { calls++; return true; });
            signal.CheckpointBeforeSearch(CancellationToken.None);
            PolicyCheck.Require(calls == 0 && signal.ReclaimCount == 0,
                "The first search has no preceding drained search to reclaim.");
            signal.CheckpointBeforeSearch(CancellationToken.None);
            PolicyCheck.Require(calls == 0 && signal.ReclaimCount == 0,
                "An unconfigured allocation limit must skip the checkpoint.");
        });

        PolicyCheck.Run("small allocation and fake success/contended callbacks are bounded", () =>
        {
            SearchMemoryPressureSignal signal = new();
            signal.Configure(GC.GetTotalAllocatedBytes(false), 8 * 1024 * 1024, 0, long.MaxValue,
                (_, _) => { }, _ => { });
            bool enoughAllocation = false;
            int calls = 0;
            signal.SetBetweenSearchCheckpoint(_ =>
            {
                if (!enoughAllocation)
                    return false;
                calls++;
                return true;
            });
            signal.CheckpointBeforeSearch(CancellationToken.None);
            signal.CheckpointBeforeSearch(CancellationToken.None);
            PolicyCheck.Require(calls == 0 && signal.ReclaimCount == 0,
                "A small between-search allocation must not invoke reclaim work.");
            enoughAllocation = true;
            signal.CheckpointBeforeSearch(CancellationToken.None);
            PolicyCheck.Require(calls == 1 && signal.ReclaimCount == 1,
                "A qualifying fake checkpoint is counted once.");

            signal.Disable();
            SearchMemoryPressureSignal contended = new();
            contended.Configure(0, 8 * 1024 * 1024, 0, long.MaxValue,
                (_, _) => { }, _ => { });
            int contendedCalls = 0;
            contended.SetBetweenSearchCheckpoint(_ =>
            {
                contendedCalls++;
                return false;
            });
            contended.CheckpointBeforeSearch(CancellationToken.None);
            contended.CheckpointBeforeSearch(CancellationToken.None);
            PolicyCheck.Require(contendedCalls == 1 && contended.ReclaimCount == 0,
                "A contended/declined checkpoint returns immediately and is not counted.");
        });

        PolicyCheck.Run("cancellation and exception restore reclaiming state", () =>
        {
            foreach (bool throwCancellation in new[] { true, false })
            {
                SearchMemoryPressureSignal signal = new();
                signal.Configure(0, 8 * 1024 * 1024, 0, long.MaxValue,
                    (_, _) => { }, _ => { });
                int callbackCalls = 0;
                signal.SetBetweenSearchCheckpoint(token =>
                {
                    callbackCalls++;
                    signal.ObserveReclaimGcPause(TimeSpan.FromMilliseconds(7));
                    if (throwCancellation)
                        throw new OperationCanceledException(token);
                    throw new InvalidOperationException("between-search failure");
                });
                signal.CheckpointBeforeSearch(CancellationToken.None);
                if (throwCancellation)
                    PolicyCheck.Throws<OperationCanceledException>(() => signal.CheckpointBeforeSearch(CancellationToken.None));
                else
                    PolicyCheck.Throws<InvalidOperationException>(() => signal.CheckpointBeforeSearch(CancellationToken.None));
                PolicyCheck.Require(callbackCalls == 1 && !signal.CaptureUsage().Reclaiming
                    && signal.ReclaimCount == 0
                    && signal.LastReclaimMaxObservedGcPause == TimeSpan.FromMilliseconds(7),
                    "A callback cancellation or failure must clear the in-flight flag while retaining its observed pause.");
            }
        });

        PolicyCheck.Run("between-search no-progress suppression survives search reset", () =>
        {
            SearchMemoryPressureSignal signal = new();
            signal.Configure(0, 8 * 1024 * 1024, 0, long.MaxValue,
                (_, _) => { }, _ => { });
            int calls = 0;
            signal.SetBetweenSearchCheckpoint(_ =>
            {
                calls++;
                signal.ObserveReclaimGcPause(TimeSpan.FromMilliseconds(9));
                signal.ObserveReclaimGain(SearchMemoryPressureSignal.NoProgressReclaimThresholdBytes - 1);
                return true;
            });
            signal.CheckpointBeforeSearch(CancellationToken.None);
            signal.CheckpointBeforeSearch(CancellationToken.None);
            PolicyCheck.Require(calls == 1 && signal.ReclaimCount == 1
                && signal.LastReclaimMaxObservedGcPause == TimeSpan.FromMilliseconds(9),
                "The first qualifying checkpoint is allowed and records its pause.");
            signal.ResetNoProgressReclaimTracking();
            signal.CheckpointBeforeSearch(CancellationToken.None);
            PolicyCheck.Require(calls == 1 && signal.ReclaimCount == 1
                && signal.LastReclaimRegainedBytes == 0
                && signal.LastReclaimMaxObservedGcPause == TimeSpan.Zero,
                "Search-local no-progress reset must not re-enable a suppressed between-search reclaim or retain its pause.");
        });

        PolicyCheck.Run("disable unbinds checkpoint and a new configured scope reopens it", () =>
        {
            SearchMemoryPressureSignal signal = new();
            signal.Configure(0, 8 * 1024 * 1024, 0, long.MaxValue,
                (_, _) => { }, _ => { });
            int calls = 0;
            signal.SetBetweenSearchCheckpoint(_ =>
            {
                signal.ObserveReclaimGcPause(TimeSpan.FromMilliseconds(11));
                calls++;
                return true;
            });
            signal.CheckpointBeforeSearch(CancellationToken.None);
            signal.CheckpointBeforeSearch(CancellationToken.None);
            PolicyCheck.Require(calls == 1
                && signal.LastReclaimMaxObservedGcPause == TimeSpan.FromMilliseconds(11),
                "The first configured scope can reclaim once and records its pause.");
            signal.Disable();
            signal.CheckpointBeforeSearch(CancellationToken.None);
            signal.CheckpointBeforeSearch(CancellationToken.None);
            PolicyCheck.Require(calls == 1 && !signal.IsEnabled
                && signal.LastReclaimMaxObservedGcPause == TimeSpan.Zero,
                "Disable must unbind the callback and suppress stale checkpoints.");
            signal.Configure(0, 8 * 1024 * 1024, 0, long.MaxValue,
                (_, _) => { }, _ => { });
            signal.SetBetweenSearchCheckpoint(_ => { calls++; return true; });
            signal.CheckpointBeforeSearch(CancellationToken.None);
            PolicyCheck.Require(calls == 1 && signal.ReclaimCount == 1,
                "The first call in a newly configured scope is only the arm boundary.");
            signal.CheckpointBeforeSearch(CancellationToken.None);
            PolicyCheck.Require(calls == 2 && signal.ReclaimCount == 2,
                "A newly configured scope gets a fresh first-search skip and can reclaim again.");
        });

        PolicyCheck.Run("two independent checkpoint signals do not wait on each other", () =>
        {
            SearchMemoryPressureSignal first = new();
            SearchMemoryPressureSignal second = new();
            first.Configure(0, 8 * 1024 * 1024, 0, long.MaxValue, (_, _) => { }, _ => { });
            second.Configure(0, 8 * 1024 * 1024, 0, long.MaxValue, (_, _) => { }, _ => { });
            using ManualResetEventSlim entered = new(false);
            using ManualResetEventSlim release = new(false);
            first.SetBetweenSearchCheckpoint(_ =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(1));
                return true;
            });
            second.SetBetweenSearchCheckpoint(_ => false);
            first.CheckpointBeforeSearch(CancellationToken.None);
            second.CheckpointBeforeSearch(CancellationToken.None);
            Task firstTask = Task.Run(() => first.CheckpointBeforeSearch(CancellationToken.None));
            PolicyCheck.Require(entered.Wait(TimeSpan.FromSeconds(1)),
                "The first scope reaches its fake checkpoint.");
            Stopwatch watch = Stopwatch.StartNew();
            second.CheckpointBeforeSearch(CancellationToken.None);
            watch.Stop();
            PolicyCheck.Require(watch.Elapsed < TimeSpan.FromMilliseconds(100)
                && second.ReclaimCount == 0,
                "A declined second scope must not wait for the first scope's callback.");
            release.Set();
            firstTask.GetAwaiter().GetResult();
        });

        PolicyCheck.Run("actual NoGC scope does not wait on queued scope", () =>
        {
            UnattendedTestRunner.IsActive = true;
            ISearchGcScope? firstScope = null;
            ISearchGcScope? secondScope = null;
            Task<ISearchGcScope>? secondAdmission = null;
            bool secondWasDisposed = false;
            using ManualResetEventSlim admissionQueued = new(false);
            Action<string>? previousLogSink = Entry.Logger.InfoSink;
            try
            {
                SearchGcPolicy.ReclaimIfPendingAsync("between_search_scope_setup", true)
                    .GetAwaiter().GetResult();
                SearchMemoryPressureSignal first = new();
                firstScope = SearchGcPolicy.EnterSearchScope(
                    true, 1_000_000_000, first, CancellationToken.None);
                PolicyCheck.Require(first.IsEnabled
                    && GCSettings.LatencyMode == GCLatencyMode.NoGCRegion,
                    "The first scope must own a real NoGC region.");

                SearchMemoryPressureSignal second = new();
                Entry.Logger.InfoSink = message =>
                {
                    previousLogSink?.Invoke(message);
                    if (message.Contains("reason=no_gc_search_active", StringComparison.Ordinal))
                        admissionQueued.Set();
                };
                using CancellationTokenSource admissionDeadline = new(TimeSpan.FromSeconds(5));
                secondAdmission = Task.Run(() => SearchGcPolicy.EnterSearchScope(
                    true, 1_000_000_000, second, admissionDeadline.Token));
                PolicyCheck.Require(admissionQueued.Wait(TimeSpan.FromSeconds(5))
                    && !secondAdmission.IsCompleted,
                    "A second NoGC scope must remain queued while the first scope is active.");

                // Keep the allocation in SOH-sized chunks: this crosses the production 256MiB
                // between-search threshold without consuming the 256MiB LOH reservation.
                first.CheckpointBeforeSearch(CancellationToken.None);
                AllocateAndDropOverBackgroundThreshold();
                SearchGcLifecycleSnapshot beforeCheckpoint = SearchGcPolicy.CaptureLifecycle();
                using CancellationTokenSource checkpointDeadline = new(TimeSpan.FromSeconds(15));
                Task firstCheckpoint = Task.Run(
                    () => first.CheckpointBeforeSearch(checkpointDeadline.Token));
                firstCheckpoint
                    .WaitAsync(checkpointDeadline.Token).GetAwaiter().GetResult();
                SearchGcLifecycleSnapshot afterCheckpoint = SearchGcPolicy.CaptureLifecycle();
                PolicyCheck.Require(first.ReclaimCount == 1
                    && afterCheckpoint.ForcedCollections == beforeCheckpoint.ForcedCollections + 1
                    && afterCheckpoint.NoGcRestarts == beforeCheckpoint.NoGcRestarts + 1
                    && GCSettings.LatencyMode == GCLatencyMode.NoGCRegion,
                    "The active scope must finish its optional checkpoint without waiting for the queued scope.");

                firstScope.Dispose();
                firstScope = null;
                secondScope = secondAdmission.WaitAsync(admissionDeadline.Token)
                    .GetAwaiter().GetResult();
                PolicyCheck.Require(second.IsEnabled
                    && GCSettings.LatencyMode == GCLatencyMode.NoGCRegion,
                    "The queued second scope must enter after the first scope is released.");
            }
            finally
            {
                Entry.Logger.InfoSink = previousLogSink;
                secondScope?.Dispose();
                secondWasDisposed = secondScope != null;
                secondScope = null;
                firstScope?.Dispose();
                firstScope = null;
                if (secondAdmission is { IsCompleted: false })
                {
                    try { secondAdmission.Wait(TimeSpan.FromSeconds(2)); }
                    catch (AggregateException) { }
                }
                if (!secondWasDisposed && secondAdmission?.Status == TaskStatus.RanToCompletion)
                    secondAdmission.Result.Dispose();
                SearchGcPolicy.ReclaimIfPendingAsync("between_search_scope_cleanup", true)
                    .GetAwaiter().GetResult();
                UnattendedTestRunner.IsActive = false;
            }
        });

        Console.WriteLine("GC_BETWEEN_SEARCHES_OK");
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AllocateAndDropOverBackgroundThreshold()
    {
        const int ChunkBytes = 16 * 1024;
        const int ChunkCount = (256 * 1024 * 1024 / ChunkBytes) + 1;
        for (int index = 0; index < ChunkCount; index++)
        {
            byte[] chunk = new byte[ChunkBytes];
            GC.KeepAlive(chunk);
        }
    }
}
