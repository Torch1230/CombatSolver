namespace CombatSolver;

internal static class GcPolicyChecks
{
    private const long MiB = 1024L * 1024;

    public static void Run()
    {
        PolicyCheck.Run("cold layer remains conservative", () =>
        {
            SmartLayerMemoryDecision decision = new SmartLayerMemoryForecast().Decide(
                enabled: true, unexpectedNoGcLoss: false, allocatedBytes: MiB, remainingBytes: 8_192 * MiB);
            PolicyCheck.Require(decision.ShouldReclaim, "A cold forecast cannot justify skipping collection.");
        });
        PolicyCheck.Run("known fixed work fits remaining region", () =>
        {
            SmartLayerMemoryForecast forecast = KnownForecast();
            SmartLayerMemoryDecision decision = forecast.Decide(true, false, 100 * MiB, 4_096 * MiB);
            PolicyCheck.Require(!decision.ShouldReclaim && decision.ForecastBytes > 100 * MiB,
                "Complete fixed work should use a safety margin and permit ample headroom.");
            PolicyCheck.Require(forecast.Decide(true, false, 100 * MiB, decision.ForecastBytes - 1).ShouldReclaim,
                "Reservation must fit the actual remaining bytes.");
        });
        PolicyCheck.Run("disabled or unavailable NoGC never forces layer collection", () =>
        {
            PolicyCheck.Require(!KnownForecast().Decide(false, false, 1_024 * MiB, 0).ShouldReclaim,
                "Ordinary GC remains CLR-owned even if a forecast does not fit.");
        });
        PolicyCheck.Run("unexpected region loss overrides healthy forecast", () =>
        {
            PolicyCheck.Require(KnownForecast().Decide(true, true, 1, long.MaxValue).ShouldReclaim,
                "A lost region must pass through the existing fallback checkpoint.");
        });
        PolicyCheck.Run("fresh region is not redundantly collected", () =>
        {
            PolicyCheck.Require(!new SmartLayerMemoryForecast().Decide(true, false, 0, 4_096 * MiB).ShouldReclaim,
                "An empty region has no previous layer to reclaim.");
        });
        PolicyCheck.Run("timed or failed layer invalidates prior forecast", () =>
        {
            SmartLayerMemoryForecast forecast = KnownForecast();
            forecast.Observe(10 * MiB, 100, usableWorkSample: false);
            PolicyCheck.Require(forecast.Decide(true, false, MiB, 8_192 * MiB).ShouldReclaim,
                "Interrupted small work is not evidence of a small next layer.");
        });
        PolicyCheck.Run("zero work invalidates forecast", () =>
        {
            SmartLayerMemoryForecast forecast = KnownForecast();
            forecast.Observe(100 * MiB, 0, usableWorkSample: true);
            PolicyCheck.Require(forecast.Decide(true, false, MiB, 8_192 * MiB).ShouldReclaim,
                "A zero-transition sample has no measurable bytes/transition.");
        });
        PolicyCheck.Run("underprediction increases future reserve", () =>
        {
            SmartLayerMemoryForecast forecast = KnownForecast();
            SmartLayerMemoryDecision first = forecast.Decide(true, false, MiB, 8_192 * MiB);
            forecast.Observe(first.ForecastBytes * 2, 1_500, usableWorkSample: true);
            SmartLayerMemoryDecision next = forecast.Decide(true, false, MiB, 8_192 * MiB);
            PolicyCheck.Require(forecast.UnderpredictionHighWater >= 2 && next.ForecastBytes > first.ForecastBytes,
                "A missed allocation estimate must increase subsequent protection.");
        });
        PolicyCheck.Run("forecast saturates instead of wrapping budget", () =>
        {
            SmartLayerMemoryForecast forecast = new();
            forecast.Observe(long.MaxValue, 1, usableWorkSample: true);
            SmartLayerMemoryDecision decision = forecast.Decide(true, false, MiB, long.MaxValue);
            PolicyCheck.Require(decision.ShouldReclaim && decision.ForecastBytes == long.MaxValue,
                "Unrepresentable reservations must not appear to fit.");
        });
        PolicyCheck.Run("forecast rejects invalid measurements", () =>
        {
            SmartLayerMemoryForecast forecast = new();
            PolicyCheck.Throws<ArgumentOutOfRangeException>(() => forecast.Observe(-1, 1, true));
            PolicyCheck.Throws<ArgumentOutOfRangeException>(() => forecast.Observe(1, -1, true));
            PolicyCheck.Throws<ArgumentOutOfRangeException>(() => forecast.Decide(true, false, 1, -1));
        });
        PolicyCheck.Run("lifecycle distinguishes attempts from completed regions", () =>
        {
            SearchGcLifecycleCounters counts = new();
            SearchGcLifecycleSnapshot before = counts.Capture();
            counts.RecordStartAttempt(); // unsupported size, no successful region
            counts.RecordStartAttempt();
            counts.RecordStarted(restart: false);
            counts.RecordEndAttempt();
            counts.RecordEnded();
            counts.RecordForcedCollection();
            counts.RecordStartAttempt();
            counts.RecordStarted(restart: true);
            SearchGcLifecycleSnapshot delta = counts.Capture().DeltaFrom(before);
            PolicyCheck.Require(delta.NoGcStartAttempts == 3 && delta.NoGcStarts == 2
                && delta.NoGcEndAttempts == 1 && delta.NoGcEnds == 1
                && delta.NoGcRestarts == 1 && delta.ForcedCollections == 1,
                "API attempts, successful transitions and explicit collection calls are separate.");
        });
        PolicyCheck.Run("repeated loss probes count one loss per region", () =>
        {
            SearchGcLifecycleCounters counts = new();
            counts.RecordStarted(false);
            counts.RecordUnexpectedLoss();
            counts.RecordUnexpectedLoss();
            counts.RecordEndAttempt(); // failed EndNoGCRegion after an external collection
            counts.RecordUnexpectedLoss();
            PolicyCheck.Require(counts.Capture().NoGcLosses == 1 && counts.Capture().NoGcEnds == 0,
                "Loss probes and failed end calls must not double count region termination.");
            counts.RecordStarted(false);
            counts.RecordUnexpectedLoss();
            PolicyCheck.Require(counts.Capture().NoGcLosses == 2, "A newly lost region is a separate event.");
        });
        PolicyCheck.Run("pause maximum does not sum multiple suspensions", () =>
        {
            TimeSpan[] pauses = [TimeSpan.FromMilliseconds(7), TimeSpan.FromMilliseconds(11)];
            PolicyCheck.Require(SearchGcPauseSnapshot.MaximumNewPause(8, 9, pauses)
                == TimeSpan.FromMilliseconds(11), "Maximum pause must not become the 18 ms interval total.");
            PolicyCheck.Require(SearchGcPauseSnapshot.MaximumNewPause(9, 9, pauses) == TimeSpan.Zero,
                "The preceding collection is outside this observation interval.");
            PolicyCheck.Require(SearchGcPauseSnapshot.MaximumNewPause(9, 10, []) == TimeSpan.Zero,
                "A bookkeeping collection need not contain any suspension.");
        });
        PolicyCheck.Run("signal transports reason and observed pause", () =>
        {
            SearchMemoryPressureSignal signal = new();
            string? reasonSeen = null;
            signal.Configure(GC.GetTotalAllocatedBytes(false), 32 * MiB, 0, long.MaxValue,
                (token, reason) =>
                {
                    reasonSeen = reason;
                    signal.ObserveReclaimGcPause(TimeSpan.FromMilliseconds(11));
                    signal.ObserveReclaimGcPause(TimeSpan.FromMilliseconds(7));
                });
            signal.ReclaimAndContinue(CancellationToken.None, "smart_potion_layer");
            PolicyCheck.Require(reasonSeen == "smart_potion_layer" && signal.ReclaimCount == 1
                && signal.LastReclaimMaxObservedGcPause == TimeSpan.FromMilliseconds(11),
                "Signal must preserve the checkpoint's reason and single-pause observation.");
        });
        PolicyCheck.Run("pre-cancellation neither invokes callback nor reuses stale pause", () =>
        {
            SearchMemoryPressureSignal signal = new();
            int calls = 0;
            signal.Configure(GC.GetTotalAllocatedBytes(false), 32 * MiB, 0, long.MaxValue,
                _ => calls++);
            signal.ObserveReclaimGcPause(TimeSpan.FromMilliseconds(50));
            PolicyCheck.Throws<OperationCanceledException>(() =>
                signal.ReclaimAndContinue(new CancellationToken(canceled: true)));
            PolicyCheck.Require(calls == 0 && signal.LastReclaimMaxObservedGcPause == TimeSpan.Zero,
                "A canceled checkpoint must not reuse the previous checkpoint's maximum pause.");
        });
        PolicyCheck.Run("cancellation after collection retains completed observation", () =>
        {
            SearchMemoryPressureSignal signal = new();
            SearchGcLifecycleCounters counts = new();
            signal.SetGcLifecycleProbe(counts.Capture);
            signal.Configure(GC.GetTotalAllocatedBytes(false), 32 * MiB, 0, long.MaxValue,
                _ =>
                {
                    counts.RecordForcedCollection();
                    signal.ObserveReclaimGcPause(TimeSpan.FromMilliseconds(13));
                    signal.UseDefaultGcFallback(systemHeadroomConstrained: false);
                    throw new OperationCanceledException();
                });
            PolicyCheck.Throws<OperationCanceledException>(() => signal.ReclaimAndContinue(CancellationToken.None));
            PolicyCheck.Require(!signal.IsEnabled && signal.CaptureGcLifecycle().ForcedCollections == 1
                && signal.LastReclaimMaxObservedGcPause == TimeSpan.FromMilliseconds(13),
                "Fallback/cancellation must retain work that the collector already performed.");
        });
        PolicyCheck.Run("system and region reservations use the tighter remaining budget", () =>
        {
            SearchMemoryPressureSignal signal = new();
            signal.Configure(GC.GetTotalAllocatedBytes(false), 64 * MiB, 30 * MiB, 32 * MiB, _ => { });
            PolicyCheck.Require(signal.RemainingBytes <= 2 * MiB && !signal.CanReachCommit(3 * MiB),
                "Healthy region allocation space cannot override tighter system headroom.");
            signal.UseDefaultGcFallback(systemHeadroomConstrained: false);
            PolicyCheck.Require(!signal.IsEnabled && !signal.ConservativeParallelismRequired
                && signal.CanReachCommit(long.MaxValue),
                "Unsupported region size alone must not cap ordinary-GC parallelism.");
        });
        PolicyCheck.Run("actual disabled runtime scope changes no GC lifecycle counters", () =>
        {
            SearchGcLifecycleSnapshot before = SearchGcPolicy.CaptureLifecycle();
            SearchMemoryPressureSignal signal = new();
            IDisposable scope = SearchGcPolicy.EnterLowLatencySearch(
                enableNoGcRegion: false, noGcRegionBudgetBytes: MiB, signal, CancellationToken.None);
            scope.Dispose();
            scope.Dispose();
            PolicyCheck.Require(!signal.IsEnabled
                && SearchGcPolicy.CaptureLifecycle().DeltaFrom(before) == default,
                "A disabled scope must not start/end regions or force collections.");
        });
        PolicyCheck.Run("runtime rejects invalid budget and canceled entry", () =>
        {
            SearchMemoryPressureSignal signal = new();
            PolicyCheck.Throws<ArgumentOutOfRangeException>(() => SearchGcPolicy.EnterLowLatencySearch(
                false, 0, signal, CancellationToken.None));
            PolicyCheck.Throws<OperationCanceledException>(() => SearchGcPolicy.EnterLowLatencySearch(
                false, MiB, signal, new CancellationToken(canceled: true)));
        });
    }

    private static SmartLayerMemoryForecast KnownForecast()
    {
        SmartLayerMemoryForecast forecast = new();
        forecast.Observe(100 * MiB, 1_000, usableWorkSample: true);
        return forecast;
    }
}
