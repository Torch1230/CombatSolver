using System.Runtime;

namespace CombatSolver;

internal static class GcPortableRuntimeChecks
{
    public static void Run()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        SearchGcPolicy.ReclaimIfPendingAsync("portable_setup", true).WaitAsync(deadline.Token).GetAwaiter().GetResult();
        int reads = 0;
        SearchGcRuntimeInfo.SetDetailedReaderForTesting(_ =>
        {
            reads++;
            throw new PlatformNotSupportedException("Mono detailed GC information is unavailable.");
        });
        UnattendedTestRunner.IsActive = true;
        try
        {
            PolicyCheck.Require(!SearchGcRuntimeInfo.SupportsDetailedInfo && reads == 1,
                "The unavailable API must be detected once before entering a checkpoint.");
            PolicyCheck.Run("portable default-GC checkpoint resumes and releases admission", () =>
            {
                SearchMemoryPressureSignal signal = new();
                using ISearchGcScope scope = SearchGcPolicy.EnterSearchScope(false, 1_000_000_000, signal, deadline.Token);
                signal.ReclaimAndContinue(deadline.Token, "portable_default_checkpoint");
                PolicyCheck.Require(signal.ReclaimCount == 1 && signal.AllocationLimitBytes < long.MaxValue
                    && signal.LastReclaimMaxObservedGcPause is null && SearchGcPauseSnapshot.Capture() is null,
                    "The completed checkpoint must refresh the default-GC allocation bound.");
                PolicyCheck.Require(!SearchGcPolicy.IsRecoverableOutcomeForTesting("InsufficientMemory"),
                    "NoGC recovery requires detailed completed-collection evidence.");
                signal.UseDefaultGcAndContinue(deadline.Token);
                PolicyCheck.Require(signal.AllocationLimitBytes == long.MaxValue,
                    "The indivisible commit must return allocation ownership to the CLR.");
                scope.Dispose();
                PolicyCheck.Require(scope.IsLifecycleCompleted && scope.Lifecycle.ForcedCollections == 2,
                    "Both blocking collections must finish before the scope is released.");
            });
            PolicyCheck.Run("portable cancellation retains completed collection and releases admission", () =>
            {
                using CancellationTokenSource canceled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                SearchMemoryPressureSignal signal = new();
                using ISearchGcScope scope = SearchGcPolicy.EnterSearchScope(false, 1_000_000_000, signal, canceled.Token);
                SearchGcLifecycleSnapshot before = SearchGcPolicy.CaptureLifecycle();
                Entry.Logger.InfoSink = message =>
                {
                    if (message.Contains("GC_DEFAULT_SEARCH_ALLOCATION_LIMIT", StringComparison.Ordinal))
                        canceled.Cancel();
                };
                try
                {
                    PolicyCheck.Throws<OperationCanceledException>(() => signal.ReclaimAndContinue(canceled.Token));
                    PolicyCheck.Require(SearchGcPolicy.CaptureLifecycle().DeltaFrom(before).ForcedCollections == 1
                        && signal.LastReclaimMaxObservedGcPause is null,
                        "Cancellation must retain its completed collection and unavailable pause observation.");
                }
                finally { Entry.Logger.InfoSink = null; }
                scope.Dispose();
                PolicyCheck.Require(scope.IsLifecycleCompleted, "Portable cancellation must release the completed scope.");
            });
            PolicyCheck.Run("portable automatic reclaim confirms a full collection", () =>
            {
                int before = GC.CollectionCount(GC.MaxGeneration);
                SearchGcLifecycleSnapshot lifecycle = SearchGcPolicy.CaptureLifecycle();
                string kind = SearchGcPolicy.CollectAutomaticReclaimForTesting()
                    .WaitAsync(deadline.Token).GetAwaiter().GetResult();
                PolicyCheck.Require(kind == "full_blocking_portable"
                    && GC.CollectionCount(GC.MaxGeneration) > before
                    && SearchGcPolicy.CaptureLifecycle().DeltaFrom(lifecycle).ForcedCollections == 1,
                    "Portable completion must come from one actual blocking full collection.");
            });
            PolicyCheck.Run("portable NoGC restart avoids unavailable detailed information", () =>
            {
                SearchMemoryPressureSignal signal = new();
                using ISearchGcScope scope = SearchGcPolicy.EnterSearchScope(
                    true, 1_000_000_000, signal, deadline.Token);
                PolicyCheck.Require(GCSettings.LatencyMode == GCLatencyMode.NoGCRegion,
                    "The capability fixture must exercise an actual NoGC region.");
                signal.ReclaimAndContinue(deadline.Token, "portable_no_gc_restart");
                PolicyCheck.Require(signal.ReclaimCount == 1
                    && GCSettings.LatencyMode == GCLatencyMode.NoGCRegion && reads == 1,
                    "NoGC capability must not imply support for detailed GC information.");
            });
            PolicyCheck.Run("portable manual memory release completes", () =>
            {
                string? completion = null;
                Entry.Logger.InfoSink = message =>
                {
                    if (message.Contains("completion_kind=full_blocking_portable", StringComparison.Ordinal))
                        completion = message;
                };
                try
                {
                    SearchGcPolicy.ForceManualProcessMemoryRelease()
                        .WaitAsync(deadline.Token).GetAwaiter().GetResult();
                    PolicyCheck.Require(completion is not null
                        && completion.Contains("gc_pause_delta_ms=unavailable", StringComparison.Ordinal),
                        "Manual memory release must complete through the portable collection path.");
                }
                finally { Entry.Logger.InfoSink = null; }
            });
            PolicyCheck.Require(reads == 1, "An unsupported detailed GC API was called after capability detection.");
            PolicyCheck.Run("unexpected capability probe errors remain explicit", () =>
                PolicyCheck.Throws<IOException>(() => SearchGcRuntimeInfo.SetDetailedReaderForTesting(
                    _ => throw new IOException("diagnostic reader failure"))));
        }
        finally
        {
            Entry.Logger.InfoSink = null;
            SearchGcPolicy.ReclaimIfPendingAsync("portable_cleanup", true)
                .WaitAsync(deadline.Token).GetAwaiter().GetResult();
            UnattendedTestRunner.IsActive = false;
            SearchGcRuntimeInfo.SetDetailedReaderForTesting(null);
        }
    }
}
