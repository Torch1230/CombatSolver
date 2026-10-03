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
        PolicyCheck.Require(scope.IsLifecycleCompleted && scope.Lifecycle.ForcedCollections >= 2,
            "Finished scope must count both requested collections.");
    }
}
