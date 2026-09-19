using System.Runtime;

namespace CombatSolver;

internal static partial class SearchGcPolicy
{
    private static long _noGcRecoveryGeneration;
    private static long _searchRecoveryBudgetCapBytes;
    private static bool IsRecoverableNoGcOutcome(NoGcRegionStartOutcome outcome)
        => outcome is NoGcRegionStartOutcome.InsufficientMemory
            or NoGcRegionStartOutcome.SystemHeadroomInsufficient
            or NoGcRegionStartOutcome.CommitWindowInsufficient
            or NoGcRegionStartOutcome.SkippedAfterUnexpectedLoss;

    private static void InstallNoGcRecoveryProbe(
        SearchMemoryPressureSignal signal, long configuredBudget, long configuredLohBudget)
    {
        NoGcRecoveryBackoff backoff = new();
        long generation = ++_noGcRecoveryGeneration;
        _searchRecoveryBudgetCapBytes = 0;
        void ObserveFallback(long completedGen2Index)
        {
            if (completedGen2Index > 0)
                backoff.ArmReclaimedFallback(Environment.TickCount64, completedGen2Index);
            else
                backoff.ArmFallback(Environment.TickCount64, CaptureCompletedGen2Index());
        }
        signal.SetNoGcRecoveryProbe((reservedBytes, cancellationToken) =>
        {
            long now = Environment.TickCount64;
            if (!backoff.ShouldObserve(now))
                return;
            GCMemoryInfo memory = GC.GetGCMemoryInfo(GCKind.Any);
            long gen2Index = CaptureCompletedGen2Index();
            if (!backoff.ObserveCompletedCollection(now, gen2Index))
                return;

            lock (Gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != _noGcRecoveryGeneration
                    || _activeSearches != 1 || _defaultGcSearches != 0 || _noGcRegionActive
                    || _reclaimActive || _reclaimRequested || _manualReclaimRequested
                    || _regionExitOnlyRequested || !_regionExitOnlyTask.IsCompleted
                    || GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
                    return;

                long systemLimit = ResolveSystemMemoryLimit(memory);
                long load = PhysicalMemoryUsage.Capture(memory).UsedBytes;
                long budget = RecoveryBudget(configuredBudget, systemLimit, load);
                long lohBudget = Math.Min(configuredLohBudget, Math.Max(1, budget / 6));
                // Fitting the next atomic operation alone can leave almost no productive
                // window before another forced collection. Keep ordinary GC in that case.
                if (!IsNoGcCommitWindowWorthEntering(budget, lohBudget, reservedBytes))
                    return;

                backoff.RecordAttempt(now, gen2Index);
                GCLatencyMode previous = GCSettings.LatencyMode;
                NoGcRegionStartOutcome outcome = TryStartNoGcRegion(budget, lohBudget, restart: true);
                if (outcome == NoGcRegionStartOutcome.Started)
                {
                    _previousMode = previous;
                    _latencyModeOwned = true;
                    _noGcRegionActive = true;
                    _configuredNoGcRegionBudgetBytes = configuredBudget;
                    _configuredNoGcRegionLohBudgetBytes = configuredLohBudget;
                    _noGcRegionBudgetBytes = budget;
                    _noGcRegionLohBudgetBytes = lohBudget;
                    _noGcRegionAllocatedBytesAtStart = GC.GetTotalAllocatedBytes(false);
                    _lastEstablishedNoGcRegionBudgetBytesForTesting = budget;
                    _searchRecoveryBudgetCapBytes = budget;
                    ConfigureSearchMemoryLimit(signal, _noGcRegionAllocatedBytesAtStart,
                        budget, budget, lohBudget, configuredBudget, configuredLohBudget);
                    backoff.RecordRecovery();
                }
                else if (!IsRecoverableNoGcOutcome(outcome))
                {
                    signal.UseDefaultGcFallback(systemHeadroomConstrained: false);
                }
                Entry.Logger.Info($"[CombatSolver/Test] GC_NO_GC_RECOVERY attempt={backoff.Attempts} " +
                    $"outcome={FormatStartOutcome(outcome)} budget={budget} loh_budget={lohBudget} " +
                    $"next_commit_reserve={reservedBytes} physical_load={load} system_limit={systemLimit} " +
                    $"completed_gen2_index={gen2Index} forced_collect=false");
            }
        }, ObserveFallback);
        // Admission can fall back before its scope probe has been installed.
        if (!signal.IsEnabled)
            ObserveFallback(0);
    }

    private static long CaptureCompletedGen2Index()
    {
        GCMemoryInfo background = GC.GetGCMemoryInfo(GCKind.Background);
        GCMemoryInfo blocking = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return Math.Max(background.Generation == GC.MaxGeneration ? background.Index : 0,
            blocking.Generation == GC.MaxGeneration ? blocking.Index : 0);
    }

    internal static long RecoveryBudget(long configured, long systemLimit, long memoryLoad)
    {
        if (systemLimit == long.MaxValue)
            return configured;
        long headroom = Math.Max(0, systemLimit - memoryLoad);
        // Hysteresis: half the current physical headroom stays outside the reservation.
        // Reclaimed Gen2 holes may be useful to normal GC but do not enlarge this allowance.
        return Math.Min(configured, headroom / 2);
    }

    internal static long CalculateSearchAllocationLimit(long remaining, long budget, long lohBudget)
        => Math.Max(1, Math.Min(Math.Max(1, budget - lohBudget) / 5 * 4,
            Math.Max(1, remaining / 4 * 3)));

    internal static bool IsNoGcCommitWindowWorthEntering(long budget, long lohBudget, long reservedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reservedBytes);
        if (budget < MinimumNoGcRegionBudgetBytes)
            return false;
        long limit = CalculateSearchAllocationLimit(budget, budget, lohBudget);
        // Match the useful-allocation floor of layer reclamation: at least one quarter
        // of the region's search allowance (and 64 MiB) must remain beyond the reserve.
        long usefulWindow = Math.Max(64L * 1024 * 1024, limit / 4);
        return reservedBytes <= limit && limit - reservedBytes >= usefulWindow;
    }

    internal sealed class NoGcRecoveryBackoff
    {
        private long _nextObservation;
        private long _lastGen2Index;
        private bool _armed;
        public int Attempts { get; private set; }

        public bool ShouldObserve(long now) => Attempts < 3 && now >= _nextObservation;

        public void ArmFallback(long now, long gen2Index)
        {
            _armed = true;
            _lastGen2Index = gen2Index;
            // A long atomic interval between fallback and the next drained boundary must
            // count toward the cooldown, without shortening a previous attempt's backoff.
            _nextObservation = Math.Max(_nextObservation, now + 2_000);
        }

        public void ArmReclaimedFallback(long now, long completedGen2Index)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(completedGen2Index, 1);
            _armed = true;
            // The checkpoint already confirmed a collection after the loss. Reuse that
            // evidence instead of allocating under ordinary GC just to wait for another one.
            _lastGen2Index = Math.Max(_lastGen2Index, completedGen2Index - 1);
            // Only the first recovery can be immediate. Later losses retain the previous
            // attempt's backoff and the same per-scope hard cap.
            _nextObservation = Math.Max(_nextObservation, now);
        }

        public bool ObserveCompletedCollection(long now, long gen2Index)
        {
            if (!ShouldObserve(now))
                return false;
            _nextObservation = now + 2_000;
            if (!_armed)
            {
                _armed = true;
                _lastGen2Index = gen2Index;
                return false;
            }
            return gen2Index > _lastGen2Index;
        }

        public void RecordAttempt(long now, long gen2Index)
        {
            Attempts++;
            _lastGen2Index = gen2Index;
            _nextObservation = now + (2_000L << Attempts);
        }

        public void RecordRecovery() => _armed = false;
    }
}
