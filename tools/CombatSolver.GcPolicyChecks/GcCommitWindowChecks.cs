using System.Runtime;

namespace CombatSolver;

internal static class GcCommitWindowChecks
{
    public static void Run()
    {
        PolicyCheck.Run("queen-sized replacement region keeps ordinary GC when reserve consumes its window", () =>
        {
            UnattendedTestRunner.IsActive = true;
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
            SearchMemoryPressureSignal signal = new();
            ISearchGcScope? scope = null;
            try
            {
                SearchGcPolicy.ReclaimIfPendingAsync("commit_window_setup", true).GetAwaiter().GetResult();
                scope = SearchGcPolicy.EnterSearchScope(true, 853_962_871, signal, deadline.Token);
                PolicyCheck.Require(signal.IsEnabled, "Exercise the actual CLR region and production checkpoint.");
                signal.TryRecoverNoGc(513_894_480, deadline.Token);
                SearchGcLifecycleSnapshot before = SearchGcPolicy.CaptureLifecycle();
                signal.ReclaimAndContinue(deadline.Token, "queen_commit_window");
                SearchGcLifecycleSnapshot delta = SearchGcPolicy.CaptureLifecycle().DeltaFrom(before);
                PolicyCheck.Require(!signal.IsEnabled && signal.RemainingBytes == long.MaxValue
                    && !signal.ConservativeParallelismRequired
                    && GCSettings.LatencyMode != GCLatencyMode.NoGCRegion,
                    "A completed collection must not restart the 55 MB productive window or retain its allocation limit.");
                PolicyCheck.Require(delta.ForcedCollections >= 1 && delta.NoGcStartAttempts == 0,
                    "Reject before asking CLR to reserve another undersized region.");
                before = SearchGcPolicy.CaptureLifecycle();
                signal.TryRecoverNoGc(513_894_480, deadline.Token);
                PolicyCheck.Require(!signal.IsEnabled
                    && SearchGcPolicy.CaptureLifecycle().DeltaFrom(before).NoGcStartAttempts == 0,
                    "The recovery probe must not immediately undo the checkpoint's rejection.");
                // Rejection is conditional on useful capacity, not a permanent NoGC disable.
                Thread.Sleep(2_100);
                before = SearchGcPolicy.CaptureLifecycle();
                signal.TryRecoverNoGc(64 * 1024 * 1024, deadline.Token);
                delta = SearchGcPolicy.CaptureLifecycle().DeltaFrom(before);
                PolicyCheck.Require(signal.IsEnabled && delta.NoGcStartAttempts == 1
                    && delta.ForcedCollections == 0,
                    "A smaller next operation can recover after the observation cooldown without a new forced GC.");
                Console.WriteLine("COMMIT_WINDOW_OK queen budget=853962871 reserve=513894480 rejected; smaller reserve recovered");
            }
            finally
            {
                scope?.Dispose();
                SearchGcPolicy.ReclaimIfPendingAsync("commit_window_cleanup", true).GetAwaiter().GetResult();
                UnattendedTestRunner.IsActive = false;
            }
        });
    }
}
