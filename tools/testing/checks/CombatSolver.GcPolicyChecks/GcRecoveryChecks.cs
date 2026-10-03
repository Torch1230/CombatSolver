namespace CombatSolver;

internal static class GcRecoveryChecks
{
    public static void RunLifecycle()
    {
        UnattendedTestRunner.IsActive = true;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        SearchMemoryPressureSignal signal = new();
        ISearchGcScope? scope = null;
        try
        {
            SearchGcPolicy.ReclaimIfPendingAsync("recovery_setup", true).GetAwaiter().GetResult();
            scope = SearchGcPolicy.EnterSearchScope(true, 1_000_000_000, signal, deadline.Token);
            PolicyCheck.Require(signal.IsEnabled, "Exercise a real region in the production policy.");
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            PolicyCheck.Require(signal.HasUnexpectedNoGcLoss(), "The test's external GC ends the region.");
            signal.ReclaimAndContinue(deadline.Token, "recovery_external_gc");
            PolicyCheck.Require(!signal.IsEnabled, "Unexpected loss first returns to ordinary GC.");
            SearchGcLifecycleSnapshot before = SearchGcPolicy.CaptureLifecycle();
            PolicyCheck.Throws<OperationCanceledException>(() =>
                signal.TryRecoverNoGc(64 * 1024 * 1024, new CancellationToken(true)));
            signal.TryRecoverNoGc(64 * 1024 * 1024, deadline.Token);
            SearchGcLifecycleSnapshot delta = SearchGcPolicy.CaptureLifecycle().DeltaFrom(before);
            PolicyCheck.Require(signal.IsEnabled
                && System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion,
                "The checkpoint's confirmed post-loss GC and adequate headroom recover the actual CLR region immediately.");
            PolicyCheck.Require(delta.NoGcStartAttempts == 1 && delta.NoGcRestarts == 1
                && delta.ForcedCollections == 0,
                "The recovery callback itself makes one bounded reservation and no forced collection.");
            scope.Dispose();
            signal.TryRecoverNoGc(64 * 1024 * 1024, deadline.Token);
            PolicyCheck.Require(!signal.IsEnabled && scope.IsLifecycleCompleted,
                "Disposed scope cannot be revived by a stale signal.");
            Console.WriteLine($"RECOVERY_LIFECYCLE_OK starts={delta.NoGcStarts} restarts={delta.NoGcRestarts} forced={delta.ForcedCollections}");
        }
        finally
        {
            scope?.Dispose();
            SearchGcPolicy.ReclaimIfPendingAsync("recovery_cleanup", true).GetAwaiter().GetResult();
            UnattendedTestRunner.IsActive = false;
        }
    }

    public static void RunExitGuard()
    {
        UnattendedTestRunner.IsActive = true;
        SearchMemoryPressureSignal signal = new();
        ISearchGcScope? scope = null;
        try
        {
            SearchGcPolicy.ReclaimIfPendingAsync("recovery_exit_setup", true).GetAwaiter().GetResult();
            scope = SearchGcPolicy.EnterSearchScope(true, 1_000_000_000, signal, CancellationToken.None);
            PolicyCheck.Require(signal.IsEnabled, "Exercise a real region before testing disable.");
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            signal.ReclaimAndContinue(CancellationToken.None, "recovery_exit_external_gc");
            SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync("no_gc_disabled").GetAwaiter().GetResult();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            Thread.Sleep(2_100);
            SearchGcLifecycleSnapshot before = SearchGcPolicy.CaptureLifecycle();
            signal.TryRecoverNoGc(64 * 1024 * 1024, CancellationToken.None);
            PolicyCheck.Require(!signal.IsEnabled
                && SearchGcPolicy.CaptureLifecycle().DeltaFrom(before).NoGcStartAttempts == 0,
                "A queued recovery must not revive NoGC after a request to leave that mode.");
        }
        finally
        {
            scope?.Dispose();
            SearchGcPolicy.ReclaimIfPendingAsync("recovery_exit_cleanup", true).GetAwaiter().GetResult();
            UnattendedTestRunner.IsActive = false;
        }
    }

    public static void RunExplicitDefaultExit()
    {
        UnattendedTestRunner.IsActive = true;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        SearchMemoryPressureSignal signal = new();
        ISearchGcScope? scope = null;
        try
        {
            SearchGcPolicy.ReclaimIfPendingAsync("explicit_default_setup", true).GetAwaiter().GetResult();
            scope = SearchGcPolicy.EnterSearchScope(true, 1_000_000_000, signal, deadline.Token);
            PolicyCheck.Require(signal.IsEnabled
                && System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion,
                "The fixture must begin with a real CLR NoGC region.");

            // Use the production callback for an indivisible search commit that exceeds
            // the region, including ReclaimWithinSearch with restartNoGcRegion: false.
            signal.UseDefaultGcAndContinue(deadline.Token);
            PolicyCheck.Require(!signal.IsEnabled
                && System.Runtime.GCSettings.LatencyMode != System.Runtime.GCLatencyMode.NoGCRegion,
                "Explicit fallback must enter ordinary GC.");

            // Supply both recovery prerequisites so a mistakenly retained allowance can
            // establish a real region. The recovery probe itself must collect nothing.
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            Thread.Sleep(2_100);
            SearchGcLifecycleSnapshot before = SearchGcPolicy.CaptureLifecycle();
            signal.TryRecoverNoGc(64 * 1024 * 1024, deadline.Token);
            SearchGcLifecycleSnapshot delta = SearchGcPolicy.CaptureLifecycle().DeltaFrom(before);
            PolicyCheck.Require(!signal.IsEnabled
                && System.Runtime.GCSettings.LatencyMode != System.Runtime.GCLatencyMode.NoGCRegion
                && delta.NoGcStartAttempts == 0 && delta.ForcedCollections == 0,
                "An explicit ordinary-GC exit must remain permanent after a new Gen2 and cooldown.");
            Console.WriteLine($"EXPLICIT_DEFAULT_EXIT_OK attempts={delta.NoGcStartAttempts} restarts={delta.NoGcRestarts} forced={delta.ForcedCollections}");
        }
        finally
        {
            scope?.Dispose();
            SearchGcPolicy.ReclaimIfPendingAsync("explicit_default_cleanup", true).GetAwaiter().GetResult();
            UnattendedTestRunner.IsActive = false;
        }
    }

    public static void Run()
    {
        PolicyCheck.Run("confirmed checkpoint GC can be consumed once without another collection", () =>
        {
            SearchGcPolicy.NoGcRecoveryBackoff backoff = new();
            backoff.ArmReclaimedFallback(1_000, 10);
            PolicyCheck.Require(backoff.ObserveCompletedCollection(1_000, 10),
                "A newly confirmed cleanup GC already satisfies the first recovery's collection requirement.");
            backoff.RecordAttempt(1_000, 10);
            backoff.RecordRecovery();
            backoff.ArmReclaimedFallback(2_000, 11);
            PolicyCheck.Require(!backoff.ShouldObserve(4_999), "Repeated loss still respects the previous attempt's cooldown.");
            PolicyCheck.Require(backoff.ObserveCompletedCollection(5_000, 11), "The next completed cleanup permits a later attempt.");
            backoff.RecordAttempt(5_000, 11);
            backoff.ArmReclaimedFallback(6_000, 11);
            PolicyCheck.Require(!backoff.ObserveCompletedCollection(13_000, 11),
                "A failed reservation cannot retry against the same completion certificate.");
        });
        PolicyCheck.Run("fallback starts the clock before the next drained boundary", () =>
        {
            SearchGcPolicy.NoGcRecoveryBackoff backoff = new();
            backoff.ArmFallback(1_000, 10);
            PolicyCheck.Require(!backoff.ShouldObserve(2_999), "Preserve the minimum cooldown.");
            PolicyCheck.Require(backoff.ObserveCompletedCollection(6_000, 11),
                "A late first boundary must accept an already-completed new Gen2 rather than restart the clock.");
            backoff.RecordAttempt(6_000, 11);
            backoff.RecordRecovery();
            backoff.ArmFallback(7_000, 12);
            PolicyCheck.Require(!backoff.ShouldObserve(9_999), "A new loss cannot shorten the previous attempt's backoff.");
        });
        PolicyCheck.Run("recovery waits for both cooldown and a new completed Gen2", () =>
        {
            SearchGcPolicy.NoGcRecoveryBackoff backoff = new();
            PolicyCheck.Require(!backoff.ObserveCompletedCollection(0, 10), "First fallback arms the observation.");
            PolicyCheck.Require(!backoff.ShouldObserve(1_999), "Do not poll GC at every parent.");
            PolicyCheck.Require(!backoff.ObserveCompletedCollection(2_000, 10), "Time alone is not a new GC.");
            PolicyCheck.Require(backoff.ObserveCompletedCollection(4_000, 11), "A later completed GC permits evaluation.");
            backoff.RecordAttempt(4_000, 11);
            PolicyCheck.Require(!backoff.ShouldObserve(7_999), "Failed reservations back off.");
            PolicyCheck.Require(!backoff.ObserveCompletedCollection(8_000, 11), "Do not retry the same heap after a failed reservation.");
        });
        PolicyCheck.Run("recovery attempts stay bounded in rate but never stop permanently", () =>
        {
            SearchGcPolicy.NoGcRecoveryBackoff backoff = new();
            long now = 0;
            for (int i = 0; i < 3; i++)
            {
                PolicyCheck.Require(!backoff.ObserveCompletedCollection(now, 2 * i), "Each new fallback starts a new observation window.");
                now += 2_000;
                PolicyCheck.Require(backoff.ObserveCompletedCollection(now, 2 * i + 1), "New collection permits an attempt.");
                backoff.RecordAttempt(now, 2 * i + 1);
                backoff.RecordRecovery();
                now += 2_000L << backoff.Attempts;
            }
            PolicyCheck.Require(backoff.Attempts == 3 && backoff.ShouldObserve(long.MaxValue),
                "A long encounter must keep every chance to re-establish a region instead of running the rest of the search with no allocation ceiling at all.");
            // Rate, not a hard stop, is what keeps a restart loop bounded: the delay saturates.
            long cooldownStart = 10_000_000;
            backoff.RecordAttempt(cooldownStart, 11);
            // Attempts is 4 here, so the delay is 2_000 << 4.
            const long expectedDelay = 2_000L << 4;
            PolicyCheck.Require(!backoff.ShouldObserve(cooldownStart + expectedDelay - 1)
                && backoff.ShouldObserve(cooldownStart + expectedDelay),
                "An attempt still waits out its own cooldown before the next observation.");
            for (int i = 0; i < 40; i++)
                backoff.RecordAttempt(cooldownStart, 11);
            PolicyCheck.Require(!backoff.ShouldObserve(cooldownStart + 59_999)
                && backoff.ShouldObserve(cooldownStart + 60_000),
                "The retry delay saturates at one minute, so attempts cannot spin or overflow.");
            PolicyCheck.Require(backoff.Attempts == 44,
                "Attempts keeps counting past the old cap so the delay, not a hard stop, bounds the rate.");
        });
        PolicyCheck.Run("attempts accumulate across fallback segments so a later segment keeps its ceiling", () =>
        {
            // RecordRecovery only disarms the observer; it does not reset the counter. A
            // search that falls back and recovers repeatedly must therefore keep observing
            // past the third segment. Sampled journals show six consecutive successful
            // recoveries inside one scope while physical load climbed, so a cap applied to
            // the cumulative count would have retired the probe mid-search.
            SearchGcPolicy.NoGcRecoveryBackoff backoff = new();
            long now = 0;
            long gen2 = 100;
            for (int segment = 1; segment <= 6; segment++)
            {
                // A segment begins with a fallback and waits for a completed collection.
                // The first observation of a fresh arming only records the baseline.
                backoff.ArmFallback(now, gen2);
                // Each recorded attempt lengthens the cooldown (2_000 << attempts, saturating),
                // so wait out the current delay before expecting an observation.
                now += 60_000;
                gen2++;
                PolicyCheck.Require(backoff.ObserveCompletedCollection(now, gen2),
                    $"Segment {segment} must observe once its cooldown and a newer collection are both present.");
                backoff.RecordAttempt(now, gen2);
                backoff.RecordRecovery();
            }
            PolicyCheck.Require(backoff.Attempts == 6,
                "Six fallback segments must produce six recorded attempts.");
            // The decisive part: a cumulative cap of three would leave the probe retired here,
            // so the sixth segment could never have observed at all.
            now += 60_000;
            gen2++;
            PolicyCheck.Require(backoff.ShouldObserve(now),
                "The probe must still be observing after more segments than the old cumulative cap allowed, so a later segment keeps its allocation ceiling.");
        });
        PolicyCheck.Run("only memory-driven outcomes stay recoverable", () =>
        {
            // The probe can act on a classification whose cause changes while the process
            // runs. Region sizing, platform support and an explicit request for ordinary
            // collection do not change, so retrying them only repeats the same failure.
            foreach (string recoverable in new[]
                     {
                         "InsufficientMemory",
                         "SystemHeadroomInsufficient",
                         "SkippedAfterUnexpectedLoss",
                     })
            {
                PolicyCheck.Require(
                    SearchGcPolicy.IsKnownOutcomeNameForTesting(recoverable),
                    $"{recoverable} must remain a known start outcome.");
                PolicyCheck.Require(
                    SearchGcPolicy.IsRecoverableOutcomeForTesting(recoverable),
                    $"A {recoverable} failure depends on memory, which can improve; the probe must retry it.");
            }
            foreach (string structural in new[]
                     {
                         "RegionSizeUnsupported",
                         "PlatformUnsupported",
                         "DefaultGcRequested",
                     })
            {
                PolicyCheck.Require(
                    SearchGcPolicy.IsKnownOutcomeNameForTesting(structural),
                    $"{structural} must remain a known start outcome.");
                PolicyCheck.Require(
                    !SearchGcPolicy.IsRecoverableOutcomeForTesting(structural),
                    $"A {structural} failure cannot change while the process runs, so the search must stay on ordinary collection.");
            }
        });
        PolicyCheck.Run("no-progress reclaim rule stays off at limit zero", () =>
        {
            SearchMemoryPressureSignal signal = new();
            for (int i = 0; i < 5; i++)
                signal.ObserveReclaimGain(0);
            PolicyCheck.Require(signal.ConsecutiveNoProgressReclaims == 5
                && !signal.ShouldStopForNoProgressReclaims(0)
                && !signal.ShouldStopForNoProgressReclaims(-1),
                "Limit 0 is the production default and must never stop a search, however many gainless reclaims happen.");
        });
        PolicyCheck.Run("no-progress reclaim rule fires once per cap and re-arms on progress", () =>
        {
            const int limit = 3;
            long belowThreshold = SearchMemoryPressureSignal.NoProgressReclaimThresholdBytes - 1;
            SearchMemoryPressureSignal signal = new();
            for (int i = 0; i < limit - 1; i++)
            {
                signal.ObserveReclaimGain(belowThreshold);
                PolicyCheck.Require(!signal.ShouldStopForNoProgressReclaims(limit),
                    "The rule must stay silent below the cap.");
            }
            signal.ObserveReclaimGain(belowThreshold);
            PolicyCheck.Require(signal.ConsecutiveNoProgressReclaims == limit
                && signal.LastReclaimRegainedBytes == belowThreshold
                && signal.ShouldStopForNoProgressReclaims(limit),
                "A gain just under the threshold counts as no progress, and reaching the cap stops the search.");
            signal.ObserveReclaimGain(SearchMemoryPressureSignal.NoProgressReclaimThresholdBytes);
            PolicyCheck.Require(signal.ConsecutiveNoProgressReclaims == 0
                && !signal.ShouldStopForNoProgressReclaims(limit),
                "A recovery that reaches the threshold re-arms the whole allowance.");
            for (int i = 0; i < limit - 1; i++)
                signal.ObserveReclaimGain(0);
            PolicyCheck.Require(!signal.ShouldStopForNoProgressReclaims(limit),
                "After progress the rule needs the full cap again, not a single gainless recovery.");
        });
        PolicyCheck.Run("no-progress counting resets per member search", () =>
        {
            const int limit = 2;
            SearchMemoryPressureSignal signal = new();
            signal.ObserveReclaimGain(0);
            signal.ObserveReclaimGain(0);
            PolicyCheck.Require(signal.ShouldStopForNoProgressReclaims(limit),
                "A search stops once its own allowance is spent.");
            signal.ResetNoProgressReclaimTracking();
            PolicyCheck.Require(signal.ConsecutiveNoProgressReclaims == 0
                && signal.LastReclaimRegainedBytes == 0
                && !signal.ShouldStopForNoProgressReclaims(limit),
                "The next member or request starts with the full allowance again.");
        });
        PolicyCheck.Run("recovery reservation leaves physical hysteresis and honors configuration", () =>
        {
            PolicyCheck.Require(SearchGcPolicy.RecoveryBudget(16_000_000_000, 29_228_695_790, 29_291_520_000) == 0,
                "The player's high-pressure checkpoint cannot restart immediately.");
            PolicyCheck.Require(SearchGcPolicy.RecoveryBudget(16_000_000_000, 29_228_695_790, 25_228_695_790) == 2_000_000_000,
                "Keep half the observed headroom outside the region; do not add Gen2 fragmentation.");
            PolicyCheck.Require(SearchGcPolicy.RecoveryBudget(1_000_000_000, 29_228_695_790, 20_000_000_000) == 1_000_000_000,
                "The configured budget remains a hard cap.");
        });
        PolicyCheck.Run("only recoverable fallback invokes a drained-boundary callback", () =>
        {
            SearchMemoryPressureSignal signal = new();
            int calls = 0;
            signal.SetNoGcRecoveryProbe((reserved, token) =>
            {
                PolicyCheck.Require(reserved == 123, "Transport the next indivisible work reserve.");
                calls++;
            });
            signal.UseDefaultGcFallback(true);
            signal.TryRecoverNoGc(123, CancellationToken.None);
            PolicyCheck.Require(calls == 0, "Explicit/default unsupported fallback remains CLR-owned.");
            signal.UseDefaultGcFallback(true, allowNoGcRecovery: true);
            PolicyCheck.Throws<OperationCanceledException>(() => signal.TryRecoverNoGc(123, new CancellationToken(true)));
            PolicyCheck.Require(calls == 0, "Cancellation must precede a region transition.");
            signal.TryRecoverNoGc(123, CancellationToken.None);
            PolicyCheck.Require(calls == 1, "A recoverable fallback retains its runtime callback.");
            signal.Configure(GC.GetTotalAllocatedBytes(false), 1024, 0, long.MaxValue, _ => {}, _ => {});
            signal.TryRecoverNoGc(123, CancellationToken.None);
            PolicyCheck.Require(calls == 1 && !signal.ConservativeParallelismRequired,
                "An established region disables recovery and clears the fallback parallel cap.");
            signal.Disable();
            signal.UseDefaultGcFallback(true, allowNoGcRecovery: true);
            signal.TryRecoverNoGc(123, CancellationToken.None);
            PolicyCheck.Require(calls == 1, "Disposal severs the scope callback.");
        });
    }
}
