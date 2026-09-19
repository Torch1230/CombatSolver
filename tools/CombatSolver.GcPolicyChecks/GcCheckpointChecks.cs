using System.Runtime;
namespace CombatSolver;

internal static class GcCheckpointChecks
{
    public static void Run()
    {
        UnattendedTestRunner.IsActive = true;
        try { CheckAsync().GetAwaiter().GetResult(); }
        finally { UnattendedTestRunner.IsActive = false; }
    }
    private static async Task CheckAsync()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        await SearchGcPolicy.ReclaimIfPendingAsync("checkpoint_smoke_setup", true);
        GCLatencyMode originalMode = GCSettings.LatencyMode;
        SearchMemoryPressureSignal signal = new();
        using ISearchGcScope scope = SearchGcPolicy.EnterSearchScope(true, 1_000_000_000, signal, deadline.Token);
        PolicyCheck.Require(signal.IsEnabled && GCSettings.LatencyMode == GCLatencyMode.NoGCRegion,
            "Checkpoint smoke must exercise an actual NoGC region.");
        await Task.Run(() => signal.ReclaimAndContinue(deadline.Token, "smoke_resume")).WaitAsync(deadline.Token);
        PolicyCheck.Require(signal.ReclaimCount == 1 && signal.IsEnabled && GCSettings.LatencyMode == GCLatencyMode.NoGCRegion,
            "Completed reclaim must re-establish the region before returning.");
        using CancellationTokenSource canceled = new();
        Task reached = SearchGcPolicy.PauseNextInSearchCollectionForTesting();
        Task checkpoint = Task.Run(() => signal.ReclaimAndContinue(canceled.Token, "smoke_cancel"));
        try
        {
            await reached.WaitAsync(deadline.Token);
            canceled.Cancel();
            SearchGcPolicy.ResumeInSearchCollectionForTesting();
            bool observedCancellation = false;
            try { await checkpoint.WaitAsync(deadline.Token); }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { observedCancellation = true; }
            PolicyCheck.Require(observedCancellation && !signal.IsEnabled && GCSettings.LatencyMode != GCLatencyMode.NoGCRegion,
                "Cancellation drains its collection and leaves default GC before returning.");
        }
        finally
        {
            SearchGcPolicy.ResumeInSearchCollectionForTesting();
            await checkpoint.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            scope.Dispose();
            await SearchGcPolicy.ReclaimIfPendingAsync("checkpoint_smoke_cleanup", true);
        }
        PolicyCheck.Require(GCSettings.LatencyMode == originalMode,
            "Checkpoint restart and cancellation must restore the mode from scope admission.");
        PolicyCheck.Require(scope.Lifecycle.NoGcEnds >= 2 && scope.Lifecycle.NoGcLosses == 0,
            "Intentional background exits must be counted as ends, not unexpected losses.");
        PolicyCheck.Require(scope.IsLifecycleCompleted && scope.Lifecycle.ForcedCollections >= 2,
            "Finished scope must count both requested collections.");
        if (OperatingSystem.IsWindows())
        {
            using ISearchGcScope next = SearchGcPolicy.EnterSearchScope(true, 1_000_000_000,
                new SearchMemoryPressureSignal(), deadline.Token);
            PolicyCheck.Require(GCSettings.LatencyMode == GCLatencyMode.NoGCRegion,
                "Exercise a normal Windows scope end with an active region.");
            SearchGcLifecycleSnapshot beforeExit = SearchGcPolicy.CaptureLifecycle();
            next.Dispose();
            await SearchGcPolicy.ReclaimIfPendingAsync("windows_scope_end", false).WaitAsync(deadline.Token);
            SearchGcLifecycleSnapshot exit = SearchGcPolicy.CaptureLifecycle().DeltaFrom(beforeExit);
            PolicyCheck.Require(GCSettings.LatencyMode == originalMode
                && SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting == 0
                && exit.NoGcEnds == 1 && exit.NoGcLosses == 0 && exit.ForcedCollections >= 1,
                "Windows normal exit drains its region asynchronously and restores caller mode.");
        }
    }
}
