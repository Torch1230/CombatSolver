using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace CombatSolver;

// Owns process-wide GC mode and combat-end reclamation; it is not part of the search algorithm.
internal static partial class SearchGcPolicy
{
    private const long BackgroundReclaimThresholdBytes = 256L * 1024 * 1024;
    private const int ReclaimReferenceReleaseDelayMilliseconds = 250;
    private const int ReclaimCompletionTimeoutMilliseconds = 30_000;
    private const int ConcurrentSearchExitPollMilliseconds = 10;
    private const int SystemMemoryPressureLimitPercent = 95;
    private const long MinimumNoGcRegionBudgetBytes = 512L * 1024 * 1024;
    /// <summary>
    /// A no-GC region only earns its teardown cost when it can absorb a meaningful share of the
    /// search's allocation. When system headroom caps the region to a small fraction of the
    /// configured budget, the search exhausts it almost immediately and every following memory
    /// checkpoint has to tear the region down, force a collection and restart it. Falling back to
    /// the default collector for that search is cheaper than pretending the reservation held.
    /// </summary>
    private const int MinimumNoGcRegionBudgetPercent = 50;
    private static readonly Lock Gate = new();
    private static readonly SearchGcLifecycleCounters Lifecycle = new();
    private static int _activeSearches;
    private static int _defaultGcSearches;
    private static bool _automaticGcLifecycleUsed;
    private static GCLatencyMode _previousMode;
    private static bool _latencyModeOwned;
    private static bool _noGcRegionActive;
    private static bool _regionExitRequired;
    private static bool _reclaimRequired;
    private static ReclaimState _reclaim;
    // A background reclaim cannot start while a search owns the process-wide GC mode. Keep
    // active-search requests on a separate completion chain so an in-search memory checkpoint
    // never waits on work which itself requires that search to exit.
    private static bool _workingSetTrimRequested;
    private static string _reclaimReason = "unspecified";
    private static string _deferredReclaimReason = "unspecified";
    private static TaskCompletionSource? _pendingDeferredReclaim;
    private static bool HasPendingDeferredReclaim => _pendingDeferredReclaim != null;
    private static Task PendingDeferredReclaimTask => _pendingDeferredReclaim?.Task ?? Task.CompletedTask;
    private static TaskCompletionSource? _pendingManualReclaim;
    private static bool HasPendingManualReclaim => _pendingManualReclaim != null;
    private static Task PendingManualReclaimTask => _pendingManualReclaim?.Task ?? Task.CompletedTask;
    private static Task _referenceReleaseBarrier = Task.CompletedTask;
    private static long _referenceReleaseEpoch;
    private static long _requiredReferenceReleaseCollectionEpoch;
    private static int _generation2CoveragePauseStageForTesting;
    private static TaskCompletionSource? _generation2CoverageReachedForTesting;
    private static TaskCompletionSource? _generation2CoverageResumeForTesting;
    private static bool _inSearchCheckpointPauseRequestedForTesting;
    private static TaskCompletionSource? _inSearchCheckpointReachedForTesting;
    private static TaskCompletionSource? _inSearchCheckpointResumeForTesting;
    private static bool _inSearchCollectionPauseRequestedForTesting;
    private static bool _inSearchCollectionTimeoutOnResumeForTesting;
    private static TaskCompletionSource? _inSearchCollectionReachedForTesting;
    private static TaskCompletionSource? _inSearchCollectionResumeForTesting;
    private static int _inSearchBackgroundGen2CompletedCountForTesting;
    private static int _inSearchBackgroundGen2TimeoutDrainCountForTesting;
    private static bool _failNextInSearchCheckpointAfterTransitionForTesting;
    private static bool _failNextRegionExitAfterTransitionForTesting;
    private static bool _regionExitOnlyRequested;
    private static string _regionExitOnlyReason = "unspecified";
    private static TaskCompletionSource? _regionExitOnlyCompletion;
    private static Task _regionExitOnlyTask = Task.CompletedTask;
    private static long _noGcRegionAllocatedBytesAtStart;
    private static long _noGcRegionBudgetBytes;
    private static long _noGcRegionLohBudgetBytes;
    private static long _configuredNoGcRegionBudgetBytes;
    private static long _configuredNoGcRegionLohBudgetBytes;
    private static long _largestSearchAllocatedBytes;
    private static long _combatLifecycleAllocatedBytes;
    private static int _rolloverCountForTesting;
    private static int _budgetChangeRebuildCountForTesting;
    private static int _budgetChangeWaitCountForTesting;
    private static long _lastEstablishedNoGcRegionBudgetBytesForTesting;
    private static int _backgroundReclaimStartedCountForTesting;
    private static int _backgroundGen2CompletedCountForTesting;
    private static int _backgroundReclaimJoinCountForTesting;
    private static int _noGcRegionExitWithoutCollectionCountForTesting;
    private static long _lastBackgroundReclaimManagedLiveBeforeForTesting;
    private static long _lastBackgroundReclaimManagedLiveAfterForTesting;
    private static long _nextReclaimSequence;
    private static long _activeReclaimSequence;

    internal static SearchGcLifecycleSnapshot CaptureLifecycle() => Lifecycle.Capture();
    internal static int RolloverCountForTesting
    {
        get
        {
            lock (Gate)
                return _rolloverCountForTesting;
        }
    }
    internal static int BudgetChangeRebuildCountForTesting
    {
        get
        {
            lock (Gate)
                return _budgetChangeRebuildCountForTesting;
        }
    }
    internal static int BudgetChangeWaitCountForTesting
    {
        get
        {
            lock (Gate)
                return _budgetChangeWaitCountForTesting;
        }
    }
    internal static long CurrentNoGcRegionBudgetBytesForTesting
    {
        get
        {
            lock (Gate)
                return _noGcRegionBudgetBytes;
        }
    }
    internal static long LastEstablishedNoGcRegionBudgetBytesForTesting
    {
        get
        {
            lock (Gate)
                return _lastEstablishedNoGcRegionBudgetBytesForTesting;
        }
    }
    internal static int BackgroundReclaimStartedCountForTesting
    {
        get
        {
            lock (Gate)
                return _backgroundReclaimStartedCountForTesting;
        }
    }
    internal static int BackgroundGen2CompletedCountForTesting
    {
        get
        {
            lock (Gate)
                return _backgroundGen2CompletedCountForTesting;
        }
    }
    internal static int BackgroundReclaimJoinCountForTesting
    {
        get
        {
            lock (Gate)
                return _backgroundReclaimJoinCountForTesting;
        }
    }
    internal static int NoGcRegionExitWithoutCollectionCountForTesting
    {
        get
        {
            lock (Gate)
                return _noGcRegionExitWithoutCollectionCountForTesting;
        }
    }
    internal static long LastBackgroundReclaimManagedLiveBeforeForTesting
    {
        get
        {
            lock (Gate)
                return _lastBackgroundReclaimManagedLiveBeforeForTesting;
        }
    }
    internal static long LastBackgroundReclaimManagedLiveAfterForTesting
    {
        get
        {
            lock (Gate)
                return _lastBackgroundReclaimManagedLiveAfterForTesting;
        }
    }
    internal static long ReferenceReleaseEpochForTesting
    {
        get
        {
            lock (Gate)
                return _referenceReleaseEpoch;
        }
    }
    internal static bool AutomaticGcLifecycleUsed
    {
        get
        {
            lock (Gate)
                return _automaticGcLifecycleUsed;
        }
    }

    internal static bool IsBackgroundReclaiming
    {
        get
        {
            lock (Gate)
                return _reclaim.IsRunning && _activeSearches == 0;
        }
    }

    internal static (bool ConfirmationPending, int Completed, int TimeoutDrains)
        InSearchBackgroundCollectionForTesting
    {
        get
        {
            lock (Gate)
                return (_activeSearches > 0 && _reclaim.IsRunning
                    && _reclaim.CollectionStarted,
                    _inSearchBackgroundGen2CompletedCountForTesting,
                    _inSearchBackgroundGen2TimeoutDrainCountForTesting);
        }
    }

    private enum NoGcRegionStartOutcome
    {
        Started,
        SkippedAfterUnexpectedLoss,
        DefaultGcRequested,
        InsufficientMemory,
        RegionSizeUnsupported,
        PlatformUnsupported,
        BackgroundUnavailable,
        SystemHeadroomInsufficient,
        CommitWindowInsufficient,
    }

    private readonly record struct EffectiveNoGcRegionBudget(
        long TotalBytes,
        long LohBytes,
        long MemoryLoadBytes,
        long SystemMemoryLimitBytes,
        bool Capped)
    {
        public bool CanStart => TotalBytes >= MinimumNoGcRegionBudgetBytes;
    }

    private readonly record struct BackgroundGen2Completion(
        string Kind,
        long Index,
        int Requests,
        bool Concurrent = false,
        bool TimedOut = false);
    internal readonly record struct CombatLifecyclePressure(
        long AllocatedBytes,
        bool RequiresCollection);

    // Mobile .NET runtimes (Android/iOS) do not support GC.TryStartNoGCRegion; skip straight to the
    // normal CLR fallback instead of calling into it.
    internal static readonly bool NoGcRegionSupported =
        !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    internal static void ResetCountersForTesting()
    {
        lock (Gate)
        {
            _rolloverCountForTesting = 0;
            _budgetChangeRebuildCountForTesting = 0;
            _budgetChangeWaitCountForTesting = 0;
            _lastEstablishedNoGcRegionBudgetBytesForTesting = 0;
            _backgroundReclaimStartedCountForTesting = 0;
            _backgroundGen2CompletedCountForTesting = 0;
            _backgroundReclaimJoinCountForTesting = 0;
            _noGcRegionExitWithoutCollectionCountForTesting = 0;
            _lastBackgroundReclaimManagedLiveBeforeForTesting = 0;
            _lastBackgroundReclaimManagedLiveAfterForTesting = 0;
            _failNextInSearchCheckpointAfterTransitionForTesting = false;
            _failNextRegionExitAfterTransitionForTesting = false;
            _inSearchBackgroundGen2CompletedCountForTesting = 0;
            _inSearchBackgroundGen2TimeoutDrainCountForTesting = 0;
        }
    }

    internal static void ReportCombatLifecycleAllocation(
        long allocatedBytes,
        string source,
        bool automaticGcEnabled)
    {
        if (allocatedBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(allocatedBytes));
        lock (Gate)
        {
            if (!automaticGcEnabled && !_automaticGcLifecycleUsed)
            {
                Entry.Logger.Info(
                    $"[CombatSolver/Test] GC_COMBAT_LIFECYCLE_SKIPPED " +
                    $"source={source} allocated={allocatedBytes} reason=no_gc_disabled");
                return;
            }
            _automaticGcLifecycleUsed |= automaticGcEnabled;
            long previous = _combatLifecycleAllocatedBytes;
            _combatLifecycleAllocatedBytes = checked(previous + allocatedBytes);
            if (previous < BackgroundReclaimThresholdBytes
                && _combatLifecycleAllocatedBytes >= BackgroundReclaimThresholdBytes)
            {
                Entry.Logger.Info(
                    $"[CombatSolver/Test] GC_COMBAT_LIFECYCLE_PRESSURE " +
                    $"source={source} allocated={_combatLifecycleAllocatedBytes} " +
                    $"threshold={BackgroundReclaimThresholdBytes}");
            }
        }
    }

    internal static CombatLifecyclePressure DetachCombatLifecyclePressure(string reason)
    {
        lock (Gate)
        {
            long allocatedBytes = _combatLifecycleAllocatedBytes;
            _combatLifecycleAllocatedBytes = 0;
            _automaticGcLifecycleUsed = false;
            bool requiresCollection = allocatedBytes >= BackgroundReclaimThresholdBytes;
            Entry.Logger.Info(
                $"[CombatSolver/Test] GC_COMBAT_LIFECYCLE_DETACHED reason={reason} " +
                $"allocated={allocatedBytes} requires_gen2={requiresCollection.ToString().ToLowerInvariant()}");
            return new CombatLifecyclePressure(allocatedBytes, requiresCollection);
        }
    }

    internal static Task CaptureRootSnapshotBarrier()
    {
        lock (Gate)
            return _referenceReleaseBarrier;
    }

    public static IDisposable EnterLowLatencySearch(
        long noGcRegionBudgetBytes,
        SearchMemoryPressureSignal memoryPressureSignal,
        CancellationToken cancellationToken)
        => EnterLowLatencySearch(
            enableNoGcRegion: true,
            noGcRegionBudgetBytes,
            memoryPressureSignal,
            cancellationToken);

    public static IDisposable EnterLowLatencySearch(
        bool enableNoGcRegion,
        long noGcRegionBudgetBytes,
        SearchMemoryPressureSignal memoryPressureSignal,
        CancellationToken cancellationToken)
        => EnterSearchScope(enableNoGcRegion, noGcRegionBudgetBytes, memoryPressureSignal, cancellationToken);

    public static ISearchGcScope EnterSearchScope(
        bool enableNoGcRegion,
        long noGcRegionBudgetBytes,
        SearchMemoryPressureSignal memoryPressureSignal,
        CancellationToken cancellationToken)
        => EnterSearchScopeCore(enableNoGcRegion, noGcRegionBudgetBytes,
            memoryPressureSignal, cancellationToken, NoGcRegionSupported);

    internal static ISearchGcScope EnterSearchScopeForUnsupportedPlatformTesting(
        long noGcRegionBudgetBytes,
        SearchMemoryPressureSignal memoryPressureSignal,
        CancellationToken cancellationToken)
        => EnterSearchScopeCore(true, noGcRegionBudgetBytes,
            memoryPressureSignal, cancellationToken, platformSupportsNoGc: false);

    private static ISearchGcScope EnterSearchScopeCore(
        bool enableNoGcRegion,
        long noGcRegionBudgetBytes,
        SearchMemoryPressureSignal memoryPressureSignal,
        CancellationToken cancellationToken,
        bool platformSupportsNoGc)
    {
        if (noGcRegionBudgetBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(noGcRegionBudgetBytes));
        ArgumentNullException.ThrowIfNull(memoryPressureSignal);
        memoryPressureSignal.SetGcLifecycleProbe(CaptureLifecycle);
        if (!enableNoGcRegion || !platformSupportsNoGc)
            return EnterDefaultGcSearch(memoryPressureSignal, cancellationToken);
        lock (Gate)
            _automaticGcLifecycleUsed = true;
        long noGcRegionLohBudgetBytes = Math.Max(
            256L * 1024 * 1024,
            noGcRegionBudgetBytes / 6);
        bool budgetChangeLogged = false;
        bool restartRequested = false;
        while (true)
        {
            Task? reclaimTask = null;
            bool waitForActiveSearchExit = false;
            bool waitForDefaultGcSearchExit = false;
            bool requestedBudgetDiffers = false;
            lock (Gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_regionExitOnlyTask.IsCompleted)
                {
                    reclaimTask = _regionExitOnlyTask;
                }
                else if (!_referenceReleaseBarrier.IsCompleted)
                {
                    reclaimTask = _referenceReleaseBarrier;
                }
                else if (_reclaim.IsRunning || _reclaim.IsPending)
                {
                    reclaimTask = _reclaim.Task;
                }
                else if (HasPendingManualReclaim)
                {
                    reclaimTask = PendingManualReclaimTask;
                }
                else if (_defaultGcSearches > 0)
                {
                    waitForActiveSearchExit = true;
                    waitForDefaultGcSearchExit = true;
                }
                else
                {
                    long allocatedBytesAtEntry = GC.GetTotalAllocatedBytes(precise: false);
                    requestedBudgetDiffers = (_noGcRegionActive || _activeSearches > 0)
                        && (_configuredNoGcRegionBudgetBytes != noGcRegionBudgetBytes
                            || _configuredNoGcRegionLohBudgetBytes != noGcRegionLohBudgetBytes);
                    if (_activeSearches > 0)
                    {
                        // In-search checkpoints temporarily end the process-wide No-GC region. Sharing
                        // it between searches would make both checkpoint callers wait for the other to
                        // leave. Serialize these scopes; expansion lanes within one search remain parallel.
                        waitForActiveSearchExit = true;
                    }
                    else if (_activeSearches == 0)
                    {
                        if (_noGcRegionActive)
                        {
                            if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
                            {
                                if (requestedBudgetDiffers)
                                {
                                    restartRequested = true;
                                    _budgetChangeRebuildCountForTesting++;
                                    _reclaimRequired = true;
                                    Entry.Logger.Info(
                                        $"[CombatSolver/Test] GC_NO_GC_REGION_BUDGET_CHANGED " +
                                        $"previous_configured={_configuredNoGcRegionBudgetBytes} " +
                                        $"previous_effective={_noGcRegionBudgetBytes} " +
                                        $"requested={noGcRegionBudgetBytes} " +
                                        "reclaim=background_non_compacting");
                                    reclaimTask = RequestReclaimLocked("no_gc_region_budget_changed");
                                }
                                else
                                {
                                    long allocated = Math.Max(
                                        0,
                                        allocatedBytesAtEntry - _noGcRegionAllocatedBytesAtStart);
                                    long remaining = Math.Max(0, _noGcRegionBudgetBytes - allocated);
                                    long required = checked(
                                        _largestSearchAllocatedBytes + _largestSearchAllocatedBytes / 4);
                                    if (_largestSearchAllocatedBytes > 0 && remaining < required)
                                    {
                                        restartRequested = true;
                                        _rolloverCountForTesting++;
                                        _reclaimRequired = true;
                                        Entry.Logger.Info(
                                            $"[CombatSolver/Test] GC_NO_GC_REGION_ROLLOVER " +
                                            $"allocated={allocated} remaining={remaining} required={required} " +
                                            "reclaim=background_non_compacting");
                                        reclaimTask = RequestReclaimLocked("no_gc_region_rollover");
                                    }
                                    else
                                    {
                                        SearchGcLifecycleSnapshot lifecycleAtEntry = CaptureLifecycle();
                                        ConfigureSearchMemoryLimit(
                                            memoryPressureSignal,
                                            allocatedBytesAtEntry,
                                            remaining,
                                            _noGcRegionBudgetBytes,
                                            _noGcRegionLohBudgetBytes,
                                            _configuredNoGcRegionBudgetBytes,
                                            _configuredNoGcRegionLohBudgetBytes);
                                        _lastEstablishedNoGcRegionBudgetBytesForTesting =
                                            _noGcRegionBudgetBytes;
                                        _activeSearches++;
                                        Entry.Logger.Info(
                                            "[CombatSolver/Test] GC_LATENCY policy=combat_scoped_no_gc_region_reuse");
                                        return CreateExclusiveSearchScope(
                                            allocatedBytesAtEntry, memoryPressureSignal, lifecycleAtEntry,
                                            noGcRegionBudgetBytes, noGcRegionLohBudgetBytes);
                                    }
                                }
                            }
                            else
                            {
                                Lifecycle.RecordUnexpectedLoss();
                                restartRequested = true;
                                _noGcRegionActive = false;
                                _reclaimRequired = true;
                                RequireCollectionAfterNextReferenceReleaseLocked();
                                RestoreLatencyModeLocked();
                                Entry.Logger.Warn(
                                    "[CombatSolver/Test] GC_LATENCY no_gc_region_lost=true " +
                                    "reason=latency_mode_changed " +
                                    "reclaim=background_non_compacting");
                                reclaimTask = RequestReclaimLocked("no_gc_region_exhausted");
                            }
                        }
                        else if (_reclaimRequired)
                        {
                            reclaimTask = RequestReclaimLocked("before_next_search");
                        }
                        else
                        {
                            // All admission waits have completed and this gate still excludes
                            // the next request. Include this scope's own region-start attempts.
                            bool backgroundReady = noGcRegionBudgetBytes < MinimumNoGcRegionBudgetBytes
                                || !OperatingSystem.IsWindows() || PrepareWindowsBackgroundCollection();
                            cancellationToken.ThrowIfCancellationRequested();
                            SearchGcLifecycleSnapshot lifecycleAtEntry = CaptureLifecycle();
                            _previousMode = GCSettings.LatencyMode;
                            _latencyModeOwned = true;
                            EffectiveNoGcRegionBudget effectiveBudget = ResolveEffectiveNoGcRegionBudget(
                                noGcRegionBudgetBytes,
                                noGcRegionLohBudgetBytes);
                            // Captured before the size-fallback loop mutates it: only headroom
                            // clamping, never a platform reservation ceiling, makes a smaller
                            // region pointless to keep.
                            bool headroomLimitedBudget = effectiveBudget.Capped;
                            NoGcRegionStartOutcome startOutcome = !backgroundReady
                                ? NoGcRegionStartOutcome.BackgroundUnavailable
                                : effectiveBudget.CanStart
                                    ? TryStartNoGcRegionWithSizeFallback(ref effectiveBudget, restartRequested)
                                    : NoGcRegionStartOutcome.SystemHeadroomInsufficient;
                            if (startOutcome == NoGcRegionStartOutcome.Started
                                && headroomLimitedBudget
                                && !IsNoGcRegionBudgetWorthEntering(
                                    noGcRegionBudgetBytes, effectiveBudget.TotalBytes))
                            {
                                // The runtime could only reserve a fraction of the configured budget.
                                // Such a region cannot absorb a meaningful share of the search's
                                // allocation, so the search exhausts it immediately and every later
                                // memory checkpoint pays a full region teardown plus a forced
                                // collection before restarting it. Declining the region also disables
                                // the pressure signal, so the search runs to completion under the
                                // default collector instead of thrashing against this loop.
                                EndNoGcRegion();
                                Entry.Logger.Info(
                                    $"[CombatSolver/Test] GC_NO_GC_REGION_DECLINED " +
                                    $"configured_budget={noGcRegionBudgetBytes} " +
                                    $"achieved_budget={effectiveBudget.TotalBytes} " +
                                    $"percent_of_configured={effectiveBudget.TotalBytes * 100 / noGcRegionBudgetBytes} " +
                                    $"system_memory_load={effectiveBudget.MemoryLoadBytes} " +
                                    $"system_memory_limit={effectiveBudget.SystemMemoryLimitBytes}");
                                startOutcome = NoGcRegionStartOutcome.SystemHeadroomInsufficient;
                            }
                            _noGcRegionActive = startOutcome == NoGcRegionStartOutcome.Started;
                            if (_noGcRegionActive)
                            {
                                _configuredNoGcRegionBudgetBytes = noGcRegionBudgetBytes;
                                _configuredNoGcRegionLohBudgetBytes = noGcRegionLohBudgetBytes;
                                _noGcRegionBudgetBytes = effectiveBudget.TotalBytes;
                                _noGcRegionLohBudgetBytes = effectiveBudget.LohBytes;
                                _noGcRegionAllocatedBytesAtStart = GC.GetTotalAllocatedBytes(precise: false);
                                _lastEstablishedNoGcRegionBudgetBytesForTesting =
                                    effectiveBudget.TotalBytes;
                                Entry.Logger.Info(
                                    $"[CombatSolver/Test] GC_LATENCY policy=combat_scoped_no_gc_region " +
                                    $"configured_budget={noGcRegionBudgetBytes} " +
                                    $"effective_budget={effectiveBudget.TotalBytes} " +
                                    $"effective_loh_budget={effectiveBudget.LohBytes} " +
                                    $"system_memory_load={effectiveBudget.MemoryLoadBytes} " +
                                    $"system_memory_limit={effectiveBudget.SystemMemoryLimitBytes} " +
                                    $"capped={effectiveBudget.Capped.ToString().ToLowerInvariant()} " +
                                    $"current={GCSettings.LatencyMode}");
                            }
                            else
                            {
                                _configuredNoGcRegionBudgetBytes = 0;
                                _configuredNoGcRegionLohBudgetBytes = 0;
                                _noGcRegionBudgetBytes = 0;
                                _noGcRegionLohBudgetBytes = 0;
                                RestoreLatencyModeLocked();
                                Entry.Logger.Info(
                                    $"[CombatSolver/Test] GC_LATENCY policy=no_gc_region_unavailable " +
                                    $"reason={FormatStartOutcome(startOutcome)} " +
                                    $"configured_budget={noGcRegionBudgetBytes} " +
                                    $"effective_budget={effectiveBudget.TotalBytes} " +
                                    $"system_memory_load={effectiveBudget.MemoryLoadBytes} " +
                                    $"system_memory_limit={effectiveBudget.SystemMemoryLimitBytes} " +
                                    $"fallback={GCSettings.LatencyMode}");
                            }
                            if (_noGcRegionActive)
                            {
                                ConfigureSearchMemoryLimit(
                                    memoryPressureSignal,
                                    allocatedBytesAtEntry,
                                    effectiveBudget.TotalBytes,
                                    effectiveBudget.TotalBytes,
                                    effectiveBudget.LohBytes,
                                    noGcRegionBudgetBytes,
                                    noGcRegionLohBudgetBytes);
                            }
                            else
                            {
                                memoryPressureSignal.UseDefaultGcFallback(
                                    IsSystemHeadroomOutcome(startOutcome),
                                    allowNoGcRecovery: IsRecoverableNoGcOutcome(startOutcome));
                            }
                            _activeSearches++;
                            return CreateExclusiveSearchScope(
                                allocatedBytesAtEntry, memoryPressureSignal, lifecycleAtEntry,
                                noGcRegionBudgetBytes, noGcRegionLohBudgetBytes);
                        }
                    }
                }
            }

            if (waitForActiveSearchExit)
            {
                if (waitForDefaultGcSearchExit)
                {
                    if (!budgetChangeLogged)
                    {
                        Entry.Logger.Info(
                            "[CombatSolver/Test] GC_MODE_WAIT requested=no_gc " +
                            "reason=default_gc_search_active");
                        budgetChangeLogged = true;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    Thread.Sleep(ConcurrentSearchExitPollMilliseconds);
                    continue;
                }
                if (!budgetChangeLogged)
                {
                    if (requestedBudgetDiffers)
                    {
                        lock (Gate)
                            _budgetChangeWaitCountForTesting++;
                        Entry.Logger.Info(
                            $"[CombatSolver/Test] GC_NO_GC_REGION_BUDGET_WAIT " +
                            $"requested={noGcRegionBudgetBytes} reason=active_search");
                    }
                    else
                    {
                        Entry.Logger.Info(
                            "[CombatSolver/Test] GC_MODE_WAIT requested=no_gc " +
                            "reason=no_gc_search_active");
                    }
                    budgetChangeLogged = true;
                }
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(ConcurrentSearchExitPollMilliseconds);
                continue;
            }

            (reclaimTask ?? throw new InvalidOperationException("GC 回收状态缺少完成任务。"))
                .WaitAsync(cancellationToken)
                .GetAwaiter()
                .GetResult();
        }
    }

    private static ISearchGcScope EnterDefaultGcSearch(
        SearchMemoryPressureSignal memoryPressureSignal,
        CancellationToken cancellationToken)
    {
        bool waitLogged = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExitNoGcRegionWhenSearchesIdleAsync("no_gc_disabled")
                .WaitAsync(cancellationToken)
                .GetAwaiter()
                .GetResult();
            lock (Gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool onlyDefaultGcSearchesActive = _activeSearches == _defaultGcSearches;
                if (onlyDefaultGcSearchesActive
                    && !_noGcRegionActive
                    && !_latencyModeOwned
                    && _regionExitOnlyTask.IsCompleted
                    && _referenceReleaseBarrier.IsCompleted
                    && !_reclaim.IsRunning
                    && !_reclaim.IsPending
                    && !HasPendingDeferredReclaim
                    && !HasPendingManualReclaim)
                {
                    SearchGcLifecycleSnapshot lifecycleAtEntry = CaptureLifecycle();
                    _activeSearches++;
                    _defaultGcSearches++;
                    memoryPressureSignal.Disable();
                    Entry.Logger.Info(
                        "[CombatSolver/Test] GC_LATENCY policy=clr_default no_gc_enabled=false");
                    return new DefaultGcSearchScope(lifecycleAtEntry);
                }
            }
            if (!waitLogged)
            {
                Entry.Logger.Info(
                    "[CombatSolver/Test] GC_MODE_WAIT requested=default_gc " +
                    "reason=no_gc_search_active");
                waitLogged = true;
            }
            Thread.Sleep(ConcurrentSearchExitPollMilliseconds);
        }
    }

    internal static Task ExitNoGcRegionWhenSearchesIdleAsync(string reason)
    {
        lock (Gate)
        {
            // A pending recovery must not restart a region after a request to leave NoGC,
            // including when the current search has already fallen back to ordinary GC.
            _exclusiveSearchScope?.DisableRecovery();
            if (!_regionExitOnlyTask.IsCompleted)
                return _regionExitOnlyTask;
            if (!_noGcRegionActive && !_latencyModeOwned)
                return Task.CompletedTask;
            _regionExitOnlyRequested = true;
            _regionExitOnlyReason = reason;
            _regionExitOnlyCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _regionExitOnlyTask = _regionExitOnlyCompletion.Task;
            if (_activeSearches == 0)
                StartRegionExitOnlyLocked(reason);
            return _regionExitOnlyTask;
        }
    }

    private static void StartRegionExitOnlyLocked(string reason)
    {
        if (!_regionExitOnlyRequested || _activeSearches != 0)
            throw new InvalidOperationException("No-GC 区域只能在搜索线程退出后结束。");
        TaskCompletionSource completion = _regionExitOnlyCompletion
            ?? throw new InvalidOperationException("No-GC 区域退出请求缺少完成信号。");
        bool endNoGcRegion = _noGcRegionActive
            && GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
        if (_noGcRegionActive && !endNoGcRegion)
            Lifecycle.RecordUnexpectedLoss();
        bool restoreLatencyMode = _latencyModeOwned;
        GCLatencyMode previousMode = _previousMode;
        _regionExitOnlyRequested = false;
        _noGcRegionExitWithoutCollectionCountForTesting++;

        bool isCombatEnd = reason is not ("no_gc_region_rollover"
            or "no_gc_region_exhausted"
            or "before_next_search"
            or "no_gc_disabled");
        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            TaskCompletionSource? failedManualCompletion = null;
            TaskCompletionSource? failedDeferredCompletion = null;
            TaskCompletionSource? failedRequestedCompletion = null;
            int gen2Before = GC.CollectionCount(GC.MaxGeneration);
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                if (isCombatEnd)
                    await Task.Delay(System.Random.Shared.Next(3_000, 5_001));
                // 把 No-GC 区域结束推迟到击杀后 3-5s（奖励环节），让用户体感没有卡顿。
                if (endNoGcRegion)
                    EndNoGcRegion();
                if (restoreLatencyMode)
                    GCSettings.LatencyMode = previousMode;
                lock (Gate)
                    ReconcileRegionOwnershipAfterTransitionLocked(
                        previousMode,
                        restoreLatencyMode);
                ThrowInjectedRegionExitFailureForTesting();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                try
                {
                    stopwatch.Stop();
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] HEAP_REGION_EXIT reason={reason} " +
                        $"no_gc_region_ended={endNoGcRegion} forced_gen2=false " +
                        $"gen2_delta={GC.CollectionCount(GC.MaxGeneration) - gen2Before} " +
                        $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1} " +
                        $"managed_live_bytes={GC.GetTotalMemory(forceFullCollection: false)}");
                }
                catch (Exception diagnosticFailure)
                {
                    failure = CombineGcFailures(failure, diagnosticFailure);
                }
                lock (Gate)
                {
                    _regionExitOnlyCompletion = null;
                    try
                    {
                        if (failure != null)
                            ReconcileRegionOwnershipAfterTransitionLocked(previousMode, restoreLatencyMode);
                    }
                    catch (Exception finalizationFailure)
                    {
                        failure = CombineGcFailures(failure, finalizationFailure);
                    }
                    // The completion uses RunContinuationsAsynchronously, so closing it while
                    // holding Gate cannot re-enter the policy. Closing it before promotion also
                    // removes the narrow window where a new request could observe an unfinished
                    // region-exit task, enqueue _reclaim.IsPending, and then never be started.
                    if (failure == null)
                        completion.TrySetResult();
                    else
                        completion.TrySetException(failure);
                    try
                    {
                        if (failure == null && _activeSearches == 0)
                        {
                            PromoteDeferredReclaimLocked();
                            if (HasPendingManualReclaim && !_reclaim.IsPending)
                                RequestReclaimLocked("manual_gc");
                            if (_reclaim.IsPending)
                                StartReclaimLocked();
                        }
                    }
                    catch (Exception promotionFailure)
                    {
                        failure = CombineGcFailures(failure, promotionFailure);
                    }
                    if (failure != null)
                    {
                        // An exit failure settles every request which was waiting on this
                        // transition. Keep collection pressure for a later explicit policy entry,
                        // but do not leave a working-set release permanently joined to the old,
                        // faulted reclaim task.
                        _failNextRegionExitAfterTransitionForTesting = false;
                        if (HasPendingManualReclaim)
                        {
                            failedManualCompletion = _pendingManualReclaim;
                            _pendingManualReclaim = null;
                        }
                        if (HasPendingDeferredReclaim)
                        {
                            failedDeferredCompletion = _pendingDeferredReclaim;
                            _deferredReclaimReason = "unspecified";
                            _pendingDeferredReclaim = null;
                        }
                        if (_reclaim.IsPending)
                        {
                            failedRequestedCompletion = _reclaim.CancelPending();
                            _activeReclaimSequence = 0;
                        }
                        _workingSetTrimRequested = false;
                    }
                }
                if (failure != null)
                {
                    failedManualCompletion?.TrySetException(failure);
                    failedDeferredCompletion?.TrySetException(failure);
                    failedRequestedCompletion?.TrySetException(failure);
                }
            }
        });
    }

    internal static Task ReclaimAfterReferenceReleaseAsync(
        string reason,
        bool forceCollection,
        bool includeCombatLifecyclePressure,
        Task referenceRelease,
        Action onReferencesReleased)
    {
        ArgumentNullException.ThrowIfNull(referenceRelease);
        ArgumentNullException.ThrowIfNull(onReferencesReleased);
        Task predecessor;
        TaskCompletionSource barrierCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            predecessor = _referenceReleaseBarrier;
            _referenceReleaseBarrier = barrierCompletion.Task;
        }
        return CompleteReferenceReleaseBarrierAsync(
            predecessor,
            barrierCompletion,
            reason,
            forceCollection,
            includeCombatLifecyclePressure,
            referenceRelease,
            onReferencesReleased);
    }

    private static async Task CompleteReferenceReleaseBarrierAsync(
        Task predecessor,
        TaskCompletionSource barrierCompletion,
        string reason,
        bool forceCollection,
        bool includeCombatLifecyclePressure,
        Task referenceRelease,
        Action onReferencesReleased)
    {
        try
        {
            await predecessor.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await referenceRelease.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            onReferencesReleased();
            await ReclaimAfterReferenceReleaseBoundaryAsync(
                    reason,
                    forceCollection,
                    includeCombatLifecyclePressure)
                .ConfigureAwait(false);
        }
        finally
        {
            // Search workers wait on this gate, not the possibly faulted operation task. A
            // reclaim diagnostic failure must not permanently disable later combat searches.
            barrierCompletion.TrySetResult();
        }
    }

    public static Task ReclaimIfPendingAsync(
        string reason,
        bool forceCollection = false,
        bool includeCombatLifecyclePressure = true)
    {
        lock (Gate)
            return ReclaimIfPendingLocked(
                reason,
                forceCollection,
                includeCombatLifecyclePressure,
                requiredCoverageEpoch: null);
    }

    // The settings action must cooperate with the same process-wide lifecycle as automatic
    // reclamation. Queueing the request avoids blocking the Godot main thread and lets an
    // active search leave its No-GC region at the existing safe search-exit boundary.
    internal static Task ForceManualGc()
    {
        Task reclaim;
        lock (Gate)
        {
            if (_activeSearches > 0 && _reclaim.IsRunning
                && !_reclaim.CollectionStarted)
            {
                // A checkpoint which has not requested collection yet can cover this request.
                // Once marking may have started, a new manual request belongs to the next safe
                // collection; joining the current completion could miss newly released objects.
                reclaim = _reclaim.InSearchManualTask;
            }
            else if (_activeSearches > 0)
            {
                if (!HasPendingManualReclaim)
                {
                    _pendingManualReclaim = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }
                reclaim = PendingManualReclaimTask;
            }
            else
            {
                reclaim = ReclaimIfPendingLocked(
                    "manual_gc",
                    forceCollection: true,
                    includeCombatLifecyclePressure: false,
                    requiredCoverageEpoch: null);
            }
        }
        _ = reclaim.ContinueWith(
            task => Entry.Logger.Error(
                $"[CombatSolver/Test] MANUAL_GC_FAILED exception={task.Exception?.GetBaseException()}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Entry.Logger.Info(
            $"[CombatSolver/Test] MANUAL_GC queued=true completed={reclaim.IsCompleted.ToString().ToLowerInvariant()}");
        return reclaim;
    }

    internal static Task ForceManualProcessMemoryRelease()
    {
        Task reclaim;
        lock (Gate)
        {
            if (_workingSetTrimRequested && HasPendingDeferredReclaim)
            {
                // The trim belongs to post-search work, not to a possibly active/failed
                // in-search checkpoint stored in _reclaim.Task.
                reclaim = PendingDeferredReclaimTask;
            }
            else if (_workingSetTrimRequested || _reclaim.TrimsWorkingSet)
            {
                reclaim = WaitForReclaimChainAsync(_reclaim.Task);
            }
            else
            {
                _workingSetTrimRequested = true;
                reclaim = ReclaimIfPendingLocked(
                    "manual_memory_release",
                    forceCollection: true,
                    includeCombatLifecyclePressure: false,
                    requiredCoverageEpoch: null);
            }
        }
        _ = reclaim.ContinueWith(
            task => Entry.Logger.Error(
                $"[CombatSolver/Test] MANUAL_PROCESS_MEMORY_RELEASE_FAILED exception={task.Exception?.GetBaseException()}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Entry.Logger.Info(
            $"[CombatSolver/Test] MANUAL_PROCESS_MEMORY_RELEASE queued=true completed={reclaim.IsCompleted.ToString().ToLowerInvariant()}");
        return reclaim;
    }

    private static Task ReclaimAfterReferenceReleaseBoundaryAsync(
        string reason,
        bool forceCollection,
        bool includeCombatLifecyclePressure)
    {
        lock (Gate)
        {
            long releaseEpoch = checked(++_referenceReleaseEpoch);
            long? requiredCoverageEpoch = null;
            if (_requiredReferenceReleaseCollectionEpoch != 0
                && releaseEpoch >= _requiredReferenceReleaseCollectionEpoch)
            {
                // Keep the newest released graph covered when an exhaustion reclaim began
                // before worker/callback/forensic references reached quiescence.
                _requiredReferenceReleaseCollectionEpoch = releaseEpoch;
                requiredCoverageEpoch = releaseEpoch;
            }
            return ReclaimIfPendingLocked(
                reason,
                forceCollection,
                includeCombatLifecyclePressure,
                requiredCoverageEpoch);
        }
    }

    private static Task ReclaimIfPendingLocked(
        string reason,
        bool forceCollection,
        bool includeCombatLifecyclePressure,
        long? requiredCoverageEpoch)
    {
        bool lifecycleCollectionRequired = includeCombatLifecyclePressure
            && _combatLifecycleAllocatedBytes >= BackgroundReclaimThresholdBytes;
        if (includeCombatLifecyclePressure)
            _combatLifecycleAllocatedBytes = 0;
        bool activeCollectionWillCoverRelease = requiredCoverageEpoch.HasValue
            && _reclaim.IsRunning
            && _reclaim.CollectsGeneration2
            && (!_reclaim.CollectionStarted
                || _reclaim.CoverageEpoch >= requiredCoverageEpoch.Value);
        bool coverageCollectionRequired = requiredCoverageEpoch.HasValue
            && !activeCollectionWillCoverRelease;
        bool requestCollection = coverageCollectionRequired
            || ((forceCollection || lifecycleCollectionRequired)
                && !activeCollectionWillCoverRelease);
        if (_reclaim.IsRunning)
        {
            if (_activeSearches > 0)
            {
                _reclaimRequired |= requestCollection;
                _reclaimReason = reason;
                // The active reclaim is an in-search checkpoint. It may fail or be cancelled,
                // and a working-set trim must not run while the search still owns its graph.
                // Register the post-search completion immediately instead of relying on the
                // checkpoint's success-only finally path to create it later.
                return RequestReclaimLocked(reason);
            }
            if (requestCollection)
            {
                _regionExitRequired = true;
                _reclaimRequired = true;
                _reclaimReason = reason;
            }
            else
            {
                // A collection whose mark starts after this release covers the graph. Joining
                // it must not enqueue an identical second Gen2 collection.
                _backgroundReclaimJoinCountForTesting++;
            }
            return WaitForReclaimChainAsync(_reclaim.Task);
        }
        if (_reclaim.IsPending)
        {
            _reclaimRequired |= requestCollection;
            return WaitForReclaimChainAsync(_reclaim.Task);
        }
        if (_activeSearches == 0
            && !_noGcRegionActive
            && !_reclaimRequired
            && !requestCollection)
            return Task.CompletedTask;
        _reclaimRequired |= requestCollection;
        return WaitForReclaimChainAsync(RequestReclaimLocked(reason));
    }

    internal static (Task Reclaim, Task CoverageBoundaryReached)
        RequestNoGcExhaustionReclaimForTesting(bool pauseAfterCoverageCapture)
    {
        if (!UnattendedTestRunner.IsActive)
        {
            throw new InvalidOperationException(
                "No-GC exhaustion 回收入口只能在无人测试中使用。");
        }
        lock (Gate)
        {
            if (_activeSearches != 0
                || _reclaim.IsRunning
                || _reclaim.IsPending
                || HasPendingDeferredReclaim)
                throw new InvalidOperationException("No-GC exhaustion 测试要求 GC policy 已静止。");
            if (_generation2CoveragePauseStageForTesting != 0)
                throw new InvalidOperationException("No-GC exhaustion 覆盖边界测试已经在运行。");
            _generation2CoveragePauseStageForTesting = pauseAfterCoverageCapture ? 2 : 1;
            _generation2CoverageReachedForTesting = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _generation2CoverageResumeForTesting = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            RequireCollectionAfterNextReferenceReleaseLocked();
            _reclaimRequired = true;
            Task reclaim = RequestReclaimLocked("unattended_no_gc_region_exhaustion");
            return (reclaim, _generation2CoverageReachedForTesting.Task);
        }
    }

    internal static void ResumeGeneration2CoverageForTesting()
    {
        TaskCompletionSource? resume;
        lock (Gate)
            resume = _generation2CoverageResumeForTesting;
        resume?.TrySetResult();
    }

    internal static Task PauseNextInSearchCheckpointForTesting()
    {
        if (!UnattendedTestRunner.IsActive)
        {
            throw new InvalidOperationException(
                "搜索内 GC checkpoint 暂停入口只能在无人测试中使用。");
        }
        lock (Gate)
        {
            if (_inSearchCheckpointPauseRequestedForTesting
                || _inSearchCheckpointReachedForTesting != null
                || _inSearchCheckpointResumeForTesting != null)
            {
                throw new InvalidOperationException("搜索内 GC checkpoint 暂停测试已经在运行。");
            }
            _inSearchCheckpointPauseRequestedForTesting = true;
            _inSearchCheckpointReachedForTesting = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inSearchCheckpointResumeForTesting = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _inSearchCheckpointReachedForTesting.Task;
        }
    }

    internal static void ResumeInSearchCheckpointForTesting()
    {
        TaskCompletionSource? reached = null;
        TaskCompletionSource? resume;
        lock (Gate)
        {
            resume = _inSearchCheckpointResumeForTesting;
            if (_inSearchCheckpointPauseRequestedForTesting)
            {
                // A setup/cancellation failure may dispose the test before the checkpoint ever
                // consumes the hook. Disarm it so a later unrelated checkpoint cannot inherit it.
                _inSearchCheckpointPauseRequestedForTesting = false;
                reached = _inSearchCheckpointReachedForTesting;
                _inSearchCheckpointReachedForTesting = null;
                _inSearchCheckpointResumeForTesting = null;
            }
        }
        resume?.TrySetResult();
        reached?.TrySetCanceled();
    }

    internal static Task PauseNextInSearchCollectionForTesting(bool timeoutOnResume = false)
    {
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("搜索内 Gen2 确认暂停只能在无人测试中使用。");
        lock (Gate)
        {
            if (_inSearchCollectionReachedForTesting != null)
                throw new InvalidOperationException("搜索内 Gen2 确认暂停测试已经在运行。");
            _inSearchCollectionPauseRequestedForTesting = true;
            _inSearchCollectionTimeoutOnResumeForTesting = timeoutOnResume;
            _inSearchCollectionReachedForTesting = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inSearchCollectionResumeForTesting = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _inSearchCollectionReachedForTesting.Task;
        }
    }

    internal static void ResumeInSearchCollectionForTesting()
    {
        TaskCompletionSource? reached = null;
        TaskCompletionSource? resume;
        lock (Gate)
        {
            resume = _inSearchCollectionResumeForTesting;
            if (_inSearchCollectionPauseRequestedForTesting)
            {
                _inSearchCollectionPauseRequestedForTesting = false;
                _inSearchCollectionTimeoutOnResumeForTesting = false;
                reached = _inSearchCollectionReachedForTesting;
                _inSearchCollectionReachedForTesting = null;
                _inSearchCollectionResumeForTesting = null;
            }
        }
        resume?.TrySetResult();
        reached?.TrySetCanceled();
    }

    private static async Task<bool> PauseInSearchCollectionForTestingAsync()
    {
        Task resume;
        bool timeoutOnResume;
        lock (Gate)
        {
            if (!_inSearchCollectionPauseRequestedForTesting)
                return false;
            _inSearchCollectionPauseRequestedForTesting = false;
            timeoutOnResume = _inSearchCollectionTimeoutOnResumeForTesting;
            _inSearchCollectionTimeoutOnResumeForTesting = false;
            resume = (_inSearchCollectionResumeForTesting
                ?? throw new InvalidOperationException("搜索内 Gen2 测试缺少恢复信号。")).Task;
            (_inSearchCollectionReachedForTesting
                ?? throw new InvalidOperationException("搜索内 Gen2 测试缺少到达信号。"))
                .TrySetResult();
        }
        try
        {
            await resume.ConfigureAwait(false);
            return timeoutOnResume;
        }
        finally
        {
            lock (Gate)
            {
                _inSearchCollectionReachedForTesting = null;
                _inSearchCollectionResumeForTesting = null;
            }
        }
    }

    internal static void FailNextRegionExitAfterTransitionForTesting()
    {
        if (!UnattendedTestRunner.IsActive)
        {
            throw new InvalidOperationException(
                "NoGC region-exit 失败注入只能在无人测试中使用。");
        }
        lock (Gate)
        {
            if (_failNextRegionExitAfterTransitionForTesting)
                throw new InvalidOperationException("NoGC region-exit 失败注入已经登记。");
            _failNextRegionExitAfterTransitionForTesting = true;
        }
    }

    internal static void FailNextInSearchCheckpointAfterTransitionForTesting()
    {
        if (!UnattendedTestRunner.IsActive)
        {
            throw new InvalidOperationException(
                "搜索内 GC checkpoint 失败注入只能在无人测试中使用。");
        }
        lock (Gate)
        {
            if (_failNextInSearchCheckpointAfterTransitionForTesting)
                throw new InvalidOperationException("搜索内 GC checkpoint 失败注入已经登记。");
            _failNextInSearchCheckpointAfterTransitionForTesting = true;
        }
    }

    private static void ThrowInjectedInSearchCheckpointFailureForTesting()
    {
        lock (Gate)
        {
            if (!_failNextInSearchCheckpointAfterTransitionForTesting)
                return;
            _failNextInSearchCheckpointAfterTransitionForTesting = false;
        }
        throw new InvalidOperationException("无人测试注入的搜索内 GC checkpoint 失败。");
    }

    private static void ThrowInjectedRegionExitFailureForTesting()
    {
        lock (Gate)
        {
            if (!_failNextRegionExitAfterTransitionForTesting)
                return;
            _failNextRegionExitAfterTransitionForTesting = false;
        }
        throw new InvalidOperationException("无人测试注入的 NoGC region-exit 完成失败。");
    }

    private static void PauseInSearchCheckpointForTesting()
    {
        TaskCompletionSource? reached;
        Task? resume;
        lock (Gate)
        {
            if (!_inSearchCheckpointPauseRequestedForTesting)
                return;
            _inSearchCheckpointPauseRequestedForTesting = false;
            reached = _inSearchCheckpointReachedForTesting
                ?? throw new InvalidOperationException("搜索内 GC checkpoint 暂停测试缺少到达信号。");
            resume = (_inSearchCheckpointResumeForTesting
                ?? throw new InvalidOperationException("搜索内 GC checkpoint 暂停测试缺少恢复信号。"))
                .Task;
            reached.TrySetResult();
        }
        try
        {
            resume.GetAwaiter().GetResult();
        }
        finally
        {
            lock (Gate)
            {
                _inSearchCheckpointReachedForTesting = null;
                _inSearchCheckpointResumeForTesting = null;
            }
        }
    }

    private static Task PauseGeneration2CoverageForTestingAsync(
        bool afterCoverageCapture)
    {
        lock (Gate)
        {
            int expectedStage = afterCoverageCapture ? 2 : 1;
            if (_generation2CoveragePauseStageForTesting != expectedStage)
                return Task.CompletedTask;
            TaskCompletionSource reached = _generation2CoverageReachedForTesting
                ?? throw new InvalidOperationException("Gen2 覆盖边界测试缺少到达信号。");
            TaskCompletionSource resume = _generation2CoverageResumeForTesting
                ?? throw new InvalidOperationException("Gen2 覆盖边界测试缺少恢复信号。");
            reached.TrySetResult();
            return resume.Task;
        }
    }

    private static async Task WaitForReclaimChainAsync(Task checkpoint)
    {
        while (true)
        {
            await checkpoint;
            lock (Gate)
            {
                if (_reclaim.IsRunning || _reclaim.IsPending)
                {
                    checkpoint = _reclaim.Task;
                    continue;
                }
                if (HasPendingDeferredReclaim)
                {
                    checkpoint = PendingDeferredReclaimTask;
                    continue;
                }
                if (!_reclaim.IsRunning && !_reclaim.IsPending)
                    return;
            }
        }
    }

    private static Task RequestReclaimLocked(string reason)
    {
        _regionExitRequired = true;
        if (_activeSearches > 0)
        {
            if (!HasPendingDeferredReclaim)
            {
                _deferredReclaimReason = reason;
                _pendingDeferredReclaim = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] MEMORY_RECLAIM stage=deferred " +
                        $"reason={reason} active_searches={_activeSearches} " +
                        $"gen2_required={_reclaimRequired.ToString().ToLowerInvariant()} " +
                        DescribeProcessMemory());
                }
                catch (Exception requestFailure)
                {
                    TaskCompletionSource failed = _pendingDeferredReclaim;
                    _pendingDeferredReclaim = null;
                    _deferredReclaimReason = "unspecified";
                    failed.SetException(requestFailure);
                    throw;
                }
            }
            return PendingDeferredReclaimTask;
        }

        PromoteDeferredReclaimLocked();
        if (!_reclaim.IsRunning && !_reclaim.IsPending)
        {
            _reclaimReason = reason;
            _activeReclaimSequence = checked(++_nextReclaimSequence);
            _reclaim.Request();
            try
            {
                Entry.Logger.Info(
                    $"[CombatSolver/Test] MEMORY_RECLAIM stage=requested " +
                    $"id={_activeReclaimSequence} reason={reason} active_searches={_activeSearches} " +
                    $"gen2_required={_reclaimRequired.ToString().ToLowerInvariant()} " +
                    DescribeProcessMemory());
            }
            catch (Exception requestFailure)
            {
                _reclaim.CancelPending().SetException(requestFailure);
                _activeReclaimSequence = 0;
                throw;
            }
        }
        if (!_reclaim.IsRunning
            && _activeSearches == 0
            && _regionExitOnlyTask.IsCompleted)
            StartReclaimLocked();
        return _reclaim.Task;
    }

    private static void PromoteDeferredReclaimLocked()
    {
        if (!HasPendingDeferredReclaim)
            return;
        if (_activeSearches != 0)
        {
            throw new InvalidOperationException(
                "活动搜索尚未退出时不能提升 deferred GC 回收请求。");
        }
        if (_reclaim.IsRunning || _reclaim.IsPending)
        {
            throw new InvalidOperationException(
                "deferred GC 回收请求不能覆盖已有回收完成信号。");
        }

        TaskCompletionSource completion = _pendingDeferredReclaim
            ?? throw new InvalidOperationException("deferred GC 回收请求缺少完成信号。");
        string reason = _deferredReclaimReason;
        _deferredReclaimReason = "unspecified";
        _pendingDeferredReclaim = null;
        _reclaimReason = reason;
        _activeReclaimSequence = checked(++_nextReclaimSequence);
        _reclaim.Request(completion);
        try
        {
            Entry.Logger.Info(
                $"[CombatSolver/Test] MEMORY_RECLAIM stage=requested " +
                $"id={_activeReclaimSequence} reason={reason} active_searches=0 " +
                $"gen2_required={_reclaimRequired.ToString().ToLowerInvariant()} " +
                "source=deferred " + DescribeProcessMemory());
        }
        catch (Exception requestFailure)
        {
            _reclaim.CancelPending().SetException(requestFailure);
            _activeReclaimSequence = 0;
            throw;
        }
    }

    private static void RequireCollectionAfterNextReferenceReleaseLocked()
    {
        long requiredEpoch = checked(_referenceReleaseEpoch + 1);
        _requiredReferenceReleaseCollectionEpoch = Math.Max(
            _requiredReferenceReleaseCollectionEpoch,
            requiredEpoch);
    }

    private static void StartReclaimLocked()
    {
        if (!_reclaim.IsPending || _activeSearches != 0)
            throw new InvalidOperationException("GC 回收只能在请求已登记且搜索线程退出后启动。");

        TaskCompletionSource completion = _reclaim.Completion
            ?? throw new InvalidOperationException("GC 回收请求缺少完成信号。");
        TaskCompletionSource? manualCompletion = null;
        if (HasPendingManualReclaim)
        {
            manualCompletion = _pendingManualReclaim
                ?? throw new InvalidOperationException("手动 GC 请求缺少完成信号。");
            _pendingManualReclaim = null;
            _reclaimRequired = true;
        }
        string reason = _reclaimReason;
        long reclaimSequence = _activeReclaimSequence;
        bool endNoGcRegion = _noGcRegionActive
            && GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
        if (_noGcRegionActive && !endNoGcRegion)
            Lifecycle.RecordUnexpectedLoss();
        bool restoreLatencyMode = _latencyModeOwned;
        GCLatencyMode previousMode = _previousMode;
        bool collectGeneration2 = _reclaimRequired;
        bool trimWorkingSet = _workingSetTrimRequested;
        long regionAllocatedBytes = _noGcRegionAllocatedBytesAtStart == 0
            ? 0
            : Math.Max(
                0,
                GC.GetTotalAllocatedBytes(precise: false) - _noGcRegionAllocatedBytesAtStart);
        long regionBudgetBytes = _noGcRegionBudgetBytes;
        long largestSearchAllocatedBytes = _largestSearchAllocatedBytes;
        _reclaim.Start(completion, collectGeneration2, trimWorkingSet);
        _regionExitRequired = false;
        _reclaimRequired = false;
        _workingSetTrimRequested = false;
        _noGcRegionActive = false;
        _latencyModeOwned = false;
        _noGcRegionAllocatedBytesAtStart = 0;
        _noGcRegionBudgetBytes = 0;
        _noGcRegionLohBudgetBytes = 0;
        _configuredNoGcRegionBudgetBytes = 0;
        _configuredNoGcRegionLohBudgetBytes = 0;
        _largestSearchAllocatedBytes = 0;
        if (collectGeneration2)
            _backgroundReclaimStartedCountForTesting++;
        else
            _noGcRegionExitWithoutCollectionCountForTesting++;

        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            SearchGcLifecycleSnapshot lifecycleBefore = CaptureLifecycle();
            try
            {
                Entry.Logger.Info(
                    $"[CombatSolver/Test] MEMORY_RECLAIM stage=started " +
                    $"id={reclaimSequence} reason={reason} gen2_required={collectGeneration2.ToString().ToLowerInvariant()} " +
                    $"end_no_gc={endNoGcRegion.ToString().ToLowerInvariant()} " +
                    $"region_allocated={regionAllocatedBytes} region_budget={regionBudgetBytes} " +
                    $"largest_search_allocated={largestSearchAllocatedBytes} " +
                    DescribeProcessMemory());
                long liveBefore = GC.GetTotalMemory(forceFullCollection: false);
                using Process processBefore = Process.GetCurrentProcess();
                long workingSetBefore = processBefore.WorkingSet64;
                long privateBefore = processBefore.PrivateMemorySize64;
                TimeSpan pauseBefore = GC.GetTotalPauseDuration();
                Stopwatch stopwatch = Stopwatch.StartNew();

                bool directBackgroundExit = OperatingSystem.IsWindows()
                    && endNoGcRegion && collectGeneration2 && !trimWorkingSet;
                if (endNoGcRegion && !directBackgroundExit)
                    EndNoGcRegion();
                if (restoreLatencyMode && !directBackgroundExit)
                    GCSettings.LatencyMode = previousMode;
                Entry.Logger.Info(
                    $"[CombatSolver/Test] MEMORY_RECLAIM stage={(directBackgroundExit ? "region_exit_deferred" : "region_exited")} " +
                    $"id={reclaimSequence} reason={reason} " +
                    DescribeProcessMemory());

                BackgroundGen2Completion completedCollection = default;
                int generation2CollectionsBefore = GC.CollectionCount(GC.MaxGeneration);
                if (collectGeneration2)
                {
                    await Task.Delay(ReclaimReferenceReleaseDelayMilliseconds);
                    await PauseGeneration2CoverageForTestingAsync(
                        afterCoverageCapture: false);
                    long collectionCoverageEpoch;
                    lock (Gate)
                    {
                        collectionCoverageEpoch = _referenceReleaseEpoch;
                        _reclaim.BeginCollection(collectionCoverageEpoch);
                    }
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] MEMORY_RECLAIM stage=gen2_started " +
                        $"id={reclaimSequence} reason={reason} coverage_epoch={collectionCoverageEpoch} " +
                        DescribeProcessMemory());
                    await PauseGeneration2CoverageForTestingAsync(
                        afterCoverageCapture: true);
                    completedCollection = trimWorkingSet
                        ? CollectGeneration2ForManualMemoryRelease()
                        : await CollectGeneration2ForAutomaticReclaimAsync(
                            exitOwnedNoGcRegion: directBackgroundExit);
                    lock (Gate)
                    {
                        _backgroundGen2CompletedCountForTesting++;
                        if (_requiredReferenceReleaseCollectionEpoch != 0
                            && collectionCoverageEpoch
                            >= _requiredReferenceReleaseCollectionEpoch)
                        {
                            _requiredReferenceReleaseCollectionEpoch = 0;
                        }
                    }
                    if (completedCollection.TimedOut)
                        throw BackgroundCollectionTimeout();
                }
                WorkingSetTrimResult workingSetTrim = default;
                if (trimWorkingSet)
                {
                    workingSetTrim = ProcessWorkingSetTrimmer.TrimCurrentProcess();
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] WORKING_SET_TRIM " +
                        $"supported={workingSetTrim.Supported.ToString().ToLowerInvariant()} " +
                        $"working_set_before={workingSetTrim.WorkingSetBeforeBytes} " +
                        $"working_set_after={workingSetTrim.WorkingSetAfterBytes}");
                }
                stopwatch.Stop();
                GCMemoryInfo memory = GC.GetGCMemoryInfo();
                using Process processAfter = Process.GetCurrentProcess();
                processAfter.Refresh();
                long managedLiveAfter = GC.GetTotalMemory(false);
                lock (Gate)
                {
                    _lastBackgroundReclaimManagedLiveBeforeForTesting = liveBefore;
                    _lastBackgroundReclaimManagedLiveAfterForTesting = managedLiveAfter;
                }
                int generation2Collections = GC.CollectionCount(GC.MaxGeneration)
                    - generation2CollectionsBefore;
                if (collectGeneration2)
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] HEAP_RECLAIM reason={reason} " +
                        $"reclaim_id={reclaimSequence} " +
                        $"mode={(trimWorkingSet ? "blocking_compacting_working_set_trim" : "background_requested_non_compacting")} " +
                        $"no_gc_region_ended={endNoGcRegion} " +
                        $"forced_gen2=true gen2_delta={generation2Collections} " +
                        $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1} " +
                        $"gc_pause_delta_ms={(GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds:F1} " +
                        $"completion_kind={completedCollection.Kind} " +
                        $"observed_concurrent={completedCollection.Concurrent.ToString().ToLowerInvariant()} " +
                        $"completion_index={completedCollection.Index} " +
                        $"collection_requests={completedCollection.Requests} " +
                        CaptureLifecycle().DeltaFrom(lifecycleBefore).ToDiagnosticString() + " " +
                        $"managed_live_before={liveBefore} managed_live_after={managedLiveAfter} " +
                        $"managed_heap_after={memory.HeapSizeBytes} fragmented_after={memory.FragmentedBytes} " +
                        $"working_set_before={workingSetBefore} working_set_after={processAfter.WorkingSet64} " +
                        $"private_before={privateBefore} private_after={processAfter.PrivateMemorySize64}");
                }
                else
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] HEAP_RECLAIM_SKIPPED reason={reason} " +
                        $"reclaim_id={reclaimSequence} " +
                        $"no_gc_region_ended={endNoGcRegion} forced_gen2=false " +
                        $"gen2_delta={generation2Collections} " +
                        $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1} " +
                        $"gc_pause_delta_ms={(GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds:F1} " +
                        $"managed_live_before={liveBefore} managed_live_after={managedLiveAfter} " +
                        $"working_set_before={workingSetBefore} working_set_after={processAfter.WorkingSet64} " +
                        $"private_before={privateBefore} private_after={processAfter.PrivateMemorySize64}");
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                try
                {
                    lock (Gate)
                    {
                        _reclaim.Finish(completion);
                        // Keep ownership coherent even if preparation or collection failed.
                        ReconcileRegionOwnershipAfterTransitionLocked(previousMode, restoreLatencyMode);
                        if (!_noGcRegionActive)
                            RestoreLatencyModeLocked();
                        if (failure != null)
                        {
                            // Callers which joined this failed chain observe its exception. Clearing a
                            // queued trim lets a later user retry create a fresh reclaim instead of
                            // rejoining the permanently faulted task.
                            _workingSetTrimRequested = false;
                        }
                        _generation2CoveragePauseStageForTesting = 0;
                        _generation2CoverageReachedForTesting = null;
                        _generation2CoverageResumeForTesting = null;
                        _activeReclaimSequence = 0;
                        if (failure != null
                            && collectGeneration2
                            && _requiredReferenceReleaseCollectionEpoch != 0)
                        {
                            // Preserve the post-release obligation for the next safe policy entry;
                            // do not spin a retry loop after a failed background collection.
                            _reclaimRequired = true;
                        }
                        if (failure == null && (_regionExitRequired || _reclaimRequired))
                            RequestReclaimLocked(_reclaimReason);
                    }
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] MEMORY_RECLAIM stage=finished " +
                        $"id={reclaimSequence} reason={reason} success={(failure == null).ToString().ToLowerInvariant()} " +
                        DescribeProcessMemory());
                }
                catch (Exception finalizationFailure)
                {
                    failure = CombineGcFailures(failure, finalizationFailure);
                }
                finally
                {
                    try
                    {
                        if (failure == null) completion.SetResult();
                        else completion.SetException(failure);
                    }
                    finally
                    {
                        if (failure == null) manualCompletion?.SetResult();
                        else manualCompletion?.SetException(failure);
                    }
                }
            }
        });
    }

    internal enum BackgroundCollectionObservation
    {
        Waiting,
        RequestFreshCollection,
        CompletedBackground,
        CompletedFullBlocking,
    }

    internal static BackgroundCollectionObservation ObserveBackgroundCollection(
        long backgroundIndexBefore,
        long fullBlockingIndexBefore,
        long backgroundIndex,
        long fullBlockingIndex,
        bool sentinelAlive)
    {
        bool backgroundAdvanced = backgroundIndex > backgroundIndexBefore;
        bool fullBlockingAdvanced = fullBlockingIndex > fullBlockingIndexBefore;
        if (backgroundAdvanced && (!fullBlockingAdvanced || backgroundIndex > fullBlockingIndex))
            return sentinelAlive
                ? BackgroundCollectionObservation.RequestFreshCollection
                : BackgroundCollectionObservation.CompletedBackground;
        if (fullBlockingAdvanced)
            return sentinelAlive
                ? BackgroundCollectionObservation.RequestFreshCollection
                : BackgroundCollectionObservation.CompletedFullBlocking;
        return BackgroundCollectionObservation.Waiting;
    }

    private static TimeoutException BackgroundCollectionTimeout()
        => new($"后台 Gen2 回收在 {ReclaimCompletionTimeoutMilliseconds} ms 内没有确认完成；" +
            "已通过阻塞回收排空，未恢复 NoGC。");

    private static long _lastConfirmedBackgroundCollectionTick = long.MinValue;

    private static bool PrepareWindowsBackgroundCollection()
    {
        // Called under admission Gate, with no active search or owned region. Workstation
        // CLR retires its idle BGC thread. Recreating it can fall back to blocking compaction
        // even after GCStart reports BackgroundGC. Confirm readiness before accumulating a
        // multi-GB young heap. A recent completed BGC already provides this evidence.
        long last = Volatile.Read(ref _lastConfirmedBackgroundCollectionTick);
        if (last != long.MinValue && Environment.TickCount64 - last < 10_000)
            return true;
        GCLatencyMode previous = GCSettings.LatencyMode;
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                BackgroundGen2Completion completion = CollectGeneration2ForAutomaticReclaimAsync()
                    .GetAwaiter().GetResult();
                Entry.Logger.Info($"[CombatSolver/Test] GC_BACKGROUND_PREPARE attempt={attempt} " +
                    $"completion_kind={completion.Kind} concurrent={completion.Concurrent} " +
                    $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1}");
                if (completion.TimedOut)
                    throw BackgroundCollectionTimeout();
                if (completion.Concurrent)
                    return true;
            }
            // Do not build a large young heap when this CLR cannot currently run BGC.
            return false;
        }
        finally
        {
            GCSettings.LatencyMode = previous;
        }
    }

    private static async Task<BackgroundGen2Completion> CollectGeneration2InBackgroundAsync(
        bool inSearchCheckpoint = false, bool exitOwnedNoGcRegion = false)
    {
        // A forced background collection can join an automatic Gen2 that was already marking.
        // Such a collection cannot reclaim allocations created after its mark began. A fresh
        // LOH sentinel distinguishes that case: only a Gen2 that began after this method's
        // reference-release boundary can clear it.
        WeakReference completionSentinel = CreateBackgroundCollectionSentinel();
        long backgroundIndexBefore = GC.GetGCMemoryInfo(GCKind.Background).Index;
        long fullBlockingIndexBefore = GC.GetGCMemoryInfo(GCKind.FullBlocking).Index;
        long deadline = Environment.TickCount64 + ReclaimCompletionTimeoutMilliseconds;
        int requests = 0;
        bool confirmedOrDrained = false;
        bool timeoutForTesting = false;
        try
        {
            while (true)
            {
                // Observe before requesting. Periodic blind re-requests can start another GC
                // immediately before observing completion of the preceding one.
                GCMemoryInfo background = GC.GetGCMemoryInfo(GCKind.Background);
                GCMemoryInfo fullBlocking = GC.GetGCMemoryInfo(GCKind.FullBlocking);
                BackgroundCollectionObservation observation = ObserveBackgroundCollection(
                    backgroundIndexBefore,
                    fullBlockingIndexBefore,
                    background.Index,
                    fullBlocking.Index,
                    completionSentinel.IsAlive);
                if (!timeoutForTesting)
                {
                    if (observation == BackgroundCollectionObservation.CompletedBackground)
                    {
                        confirmedOrDrained = true;
                        Volatile.Write(ref _lastConfirmedBackgroundCollectionTick, Environment.TickCount64);
                        return new BackgroundGen2Completion(
                            "background", background.Index, requests, background.Concurrent);
                    }
                    if (observation == BackgroundCollectionObservation.CompletedFullBlocking)
                    {
                        confirmedOrDrained = true;
                        return new BackgroundGen2Completion(
                            "full_blocking", fullBlocking.Index, requests, fullBlocking.Concurrent);
                    }
                }
                if (timeoutForTesting || Environment.TickCount64 >= deadline)
                {
                    // An induced background GC cannot be cancelled. Drain before reporting
                    // timeout, on both checkpoint and post-search paths, so neither caller's
                    // finally can release policy ownership while this request is still in flight.
                    CollectGeneration2ForSearch();
                    confirmedOrDrained = true;
                    if (completionSentinel.IsAlive)
                        throw new InvalidOperationException("阻塞 Gen2 排空后完成哨兵仍然存活。");
                    GCMemoryInfo drained = GC.GetGCMemoryInfo(GCKind.FullBlocking);
                    Entry.Logger.Warn(
                        $"[CombatSolver/Test] GC_BACKGROUND_CONFIRMATION_TIMEOUT " +
                        $"injected={timeoutForTesting.ToString().ToLowerInvariant()} " +
                        $"drained=true completion_kind=full_blocking_timeout_drain " +
                        $"completion_index={drained.Index} collection_requests={requests + 1} " +
                        $"observed_concurrent={drained.Concurrent.ToString().ToLowerInvariant()}");
                    return new BackgroundGen2Completion(
                        "full_blocking_timeout_drain", drained.Index, requests + 1,
                        drained.Concurrent, TimedOut: true);
                }
                if (requests == 0
                    || observation == BackgroundCollectionObservation.RequestFreshCollection)
                {
                    backgroundIndexBefore = background.Index;
                    fullBlockingIndexBefore = fullBlocking.Index;
                    requests++;
                    bool endingRegion = exitOwnedNoGcRegion && requests == 1;
                    if (endingRegion)
                    {
                        Lifecycle.RecordEndAttempt();
                        if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
                        {
                            Lifecycle.RecordUnexpectedLoss();
                            throw new InvalidOperationException("后台回收前当前 NoGC 区域已意外结束。");
                        }
                    }
                    // On Windows, EndNoGCRegion first restores the old allocation budgets.
                    // An allocation on another thread can then trigger a foreground collection
                    // of the large young heap before this background request gets a chance.
                    // An induced collection itself ends NoGC: request it while still protected,
                    // with the sentinel already allocated, avoiding that unprotected interval.
                    Lifecycle.RecordForcedCollection();
                    GC.Collect(
                        GC.MaxGeneration,
                        GCCollectionMode.Forced,
                        blocking: false,
                        compacting: false);
                    if (endingRegion)
                    {
                        if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
                            throw new InvalidOperationException("后台收集请求没有结束当前 NoGC 区域。");
                        Lifecycle.RecordEnded();
                        if (inSearchCheckpoint)
                        {
                            lock (Gate)
                                ReconcileRegionOwnershipAfterTransitionLocked(_previousMode, _latencyModeOwned);
                        }
                    }
                    if (inSearchCheckpoint && requests == 1)
                        timeoutForTesting = await PauseInSearchCollectionForTestingAsync()
                            .ConfigureAwait(false);
                }
                else
                {
                    // No search/deferred task and no caller cancellation token is awaited here.
                    await Task.Delay(25).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Keep unexpected observation failures explicit, but first complete an issued GC.
            // GC.Collect's blocking form also waits for an outstanding background collection.
            if (requests > 0 && !confirmedOrDrained)
                CollectGeneration2ForSearch();
        }
    }

    private static Task<BackgroundGen2Completion> CollectGeneration2ForAutomaticReclaimAsync(
        bool inSearchCheckpoint = false, bool exitOwnedNoGcRegion = false)
        => CollectGeneration2InBackgroundAsync(inSearchCheckpoint, exitOwnedNoGcRegion);

    internal static async Task<string> CollectAutomaticReclaimForTesting()
        => (await CollectGeneration2ForAutomaticReclaimAsync()).Kind;

    private static BackgroundGen2Completion CollectGeneration2ForManualMemoryRelease()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        CollectGeneration2(blocking: true, compacting: true);
        return new BackgroundGen2Completion(
            "full_blocking_compacting",
            GC.GetGCMemoryInfo(GCKind.FullBlocking).Index,
            Requests: 1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateBackgroundCollectionSentinel()
    {
        byte[] target = new byte[128 * 1024];
        WeakReference sentinel = new(target);
        GC.KeepAlive(target);
        return sentinel;
    }

    private static void ExitLowLatencySearch(
        long allocatedBytesAtEntry,
        SearchMemoryPressureSignal memoryPressureSignal,
        SearchGcScope scope)
    {
        lock (Gate)
        {
            if (scope.IsLifecycleCompleted)
                return;
            memoryPressureSignal.Disable();
            _exclusiveSearchScope?.DisableRecovery();
            _exclusiveSearchScope = null;
            // A loss discovered on exit belongs to this admitted search. Freeze immediately
            // afterward, before releasing admission or starting any deferred collector task.
            if (_noGcRegionActive && GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
                Lifecycle.RecordUnexpectedLoss();
            scope.CompleteLifecycle(CaptureLifecycle());
            long allocatedBytes = Math.Max(
                0,
                GC.GetTotalAllocatedBytes(precise: false) - allocatedBytesAtEntry);
            _largestSearchAllocatedBytes = Math.Max(_largestSearchAllocatedBytes, allocatedBytes);
            if (allocatedBytes >= BackgroundReclaimThresholdBytes)
                _reclaimRequired = true;
            if (--_activeSearches != 0)
                return;

            if (_regionExitOnlyRequested)
            {
                bool exhausted = _noGcRegionActive
                    && GCSettings.LatencyMode != GCLatencyMode.NoGCRegion;
                if (exhausted)
                {
                    Lifecycle.RecordUnexpectedLoss();
                    _reclaimRequired = true;
                    RequireCollectionAfterNextReferenceReleaseLocked();
                    Entry.Logger.Warn(
                        "[CombatSolver/Test] GC_LATENCY no_gc_region_exhausted_before_early_exit=true " +
                        "reclaim=deferred_until_reference_release");
                }
                StartRegionExitOnlyLocked(_regionExitOnlyReason);
                return;
            }

            PromoteDeferredReclaimLocked();

            bool noGcRegionExhausted = _noGcRegionActive
                && GCSettings.LatencyMode != GCLatencyMode.NoGCRegion;
            if (noGcRegionExhausted)
            {
                Lifecycle.RecordUnexpectedLoss();
                _noGcRegionActive = false;
                _reclaimRequired = true;
                RequireCollectionAfterNextReferenceReleaseLocked();
                RestoreLatencyModeLocked();
                Entry.Logger.Warn(
                    "[CombatSolver/Test] GC_LATENCY no_gc_region_exhausted_before_search_exit=true " +
                    $"process_allocated_delta={Math.Max(0, GC.GetTotalAllocatedBytes(false) - _noGcRegionAllocatedBytesAtStart)} " +
                    "reclaim=background_non_compacting");
                RequestReclaimLocked("no_gc_region_exhausted");
                return;
            }

            if (_reclaim.IsPending)
            {
                StartReclaimLocked();
                return;
            }
            if (HasPendingManualReclaim)
            {
                RequestReclaimLocked("manual_gc");
                return;
            }
            if (_noGcRegionActive)
            {
                if (OperatingSystem.IsWindows())
                {
                    // A retained region can outlive the workstation BGC thread's idle
                    // timeout. Drain while the collector is warm, rather than carrying a
                    // large young heap into a cold collector on the next player action.
                    _reclaimRequired = true;
                    RequestReclaimLocked("windows_search_complete");
                }
                else
                {
                    Entry.Logger.Info(
                        "[CombatSolver/Test] GC_LATENCY no_gc_region_retained_until_combat_reset=true");
                }
                return;
            }
            RestoreLatencyModeLocked();
            Entry.Logger.Info(
                $"[CombatSolver/Test] GC_LATENCY exit restored={GCSettings.LatencyMode} " +
                $"entry={_previousMode}");
        }
    }

    private static void ExitDefaultGcSearch(SearchGcScope scope)
    {
        lock (Gate)
        {
            if (scope.IsLifecycleCompleted)
                return;
            if (_defaultGcSearches <= 0 || _activeSearches <= 0)
                throw new InvalidOperationException("CLR 常规 GC 搜索作用域计数失衡。");
            scope.CompleteLifecycle(CaptureLifecycle());
            _defaultGcSearches--;
            if (--_activeSearches != 0)
                return;
            PromoteDeferredReclaimLocked();
            if (_reclaim.IsPending && _regionExitOnlyTask.IsCompleted)
                StartReclaimLocked();
            else if (HasPendingManualReclaim && _regionExitOnlyTask.IsCompleted)
                RequestReclaimLocked("manual_gc");
        }
    }

    private static void ConfigureSearchMemoryLimit(
        SearchMemoryPressureSignal signal,
        long allocatedBytesAtEntry,
        long remainingRegionBytes,
        long regionBudgetBytes,
        long lohBudgetBytes,
        long configuredRegionBudgetBytes,
        long configuredLohBudgetBytes)
    {
        long allocationLimitBytes = CalculateSearchAllocationLimit(
            remainingRegionBytes, regionBudgetBytes, lohBudgetBytes);
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        long systemMemoryLimitBytes = ResolveSystemMemoryLimit(memory);
        signal.Configure(
            allocatedBytesAtEntry,
            allocationLimitBytes,
            Math.Max(0, memory.MemoryLoadBytes),
            systemMemoryLimitBytes,
            (cancellationToken, reason) => ReclaimWithinSearch(
                signal,
                configuredRegionBudgetBytes,
                configuredLohBudgetBytes,
                restartNoGcRegion: true,
                cancellationToken: cancellationToken,
                reason: reason),
            cancellationToken => ReclaimWithinSearch(
                signal,
                configuredRegionBudgetBytes,
                configuredLohBudgetBytes,
                restartNoGcRegion: false,
                cancellationToken: cancellationToken,
                reason: "default_gc_fallback"),
            HasUnexpectedNoGcLoss,
            OperatingSystem.IsWindows() ? CaptureCurrentPhysicalMemoryLoad : null,
            OperatingSystem.IsWindows()
                ? CalculateReusableHeapBytes(memory.HeapSizeBytes, memory.FragmentedBytes,
                    GC.GetTotalMemory(forceFullCollection: false))
                : 0);
        Entry.Logger.Info(
            $"[CombatSolver/Test] GC_SEARCH_ALLOCATION_LIMIT limit={allocationLimitBytes} " +
            $"remaining_region={remainingRegionBytes} region_budget={regionBudgetBytes} " +
            $"loh_budget={lohBudgetBytes} configured_budget={configuredRegionBudgetBytes} " +
            $"system_memory_load={memory.MemoryLoadBytes} " +
            $"system_pressure_source={(OperatingSystem.IsWindows() ? "physical" : "allocation_projection")} " +
            $"system_memory_limit={systemMemoryLimitBytes}");
    }

    private static bool HasUnexpectedNoGcLoss()
    {
        lock (Gate)
        {
            bool lost = _noGcRegionActive
                && GCSettings.LatencyMode != GCLatencyMode.NoGCRegion;
            if (lost)
                Lifecycle.RecordUnexpectedLoss();
            return lost;
        }
    }

    private static void ReclaimWithinSearch(
        SearchMemoryPressureSignal signal,
        long configuredRegionBudgetBytes,
        long configuredLohBudgetBytes,
        bool restartNoGcRegion,
        CancellationToken cancellationToken,
        string reason)
    {
        SearchGcLifecycleSnapshot lifecycleBefore = CaptureLifecycle();
        TaskCompletionSource checkpointCompletion;
        TaskCompletionSource? manualCompletion = null;
        bool endNoGcRegion;
        bool noGcRegionLost;
        bool restoreLatencyMode;
        GCLatencyMode previousMode;
        bool fallbackSystemHeadroomConstrained = signal.SystemPressureDominates;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Gate)
            {
                if (_activeSearches > 0 && _reclaim.IsPending)
                {
                    throw new InvalidOperationException(
                        "活动搜索期间出现了只能在搜索退出后执行的后台 GC 请求。");
                }
                if (_activeSearches == 1 && !_reclaim.IsRunning && !_reclaim.IsPending)
                {
                    if (HasPendingManualReclaim)
                    {
                        manualCompletion = _pendingManualReclaim
                            ?? throw new InvalidOperationException("手动 GC 请求缺少完成信号。");
                        _pendingManualReclaim = null;
                    }
                    // A manual request before the first collection can join its completion,
                    // independently of a later search timeout after a successful drain.
                    manualCompletion ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    checkpointCompletion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _reclaim.StartCheckpoint(checkpointCompletion, manualCompletion.Task);
                    noGcRegionLost = _noGcRegionActive
                        && GCSettings.LatencyMode != GCLatencyMode.NoGCRegion;
                    if (noGcRegionLost)
                        Lifecycle.RecordUnexpectedLoss();
                    endNoGcRegion = _noGcRegionActive && !noGcRegionLost;
                    restoreLatencyMode = _latencyModeOwned;
                    previousMode = _previousMode;
                    break;
                }
            }
            Thread.Sleep(ConcurrentSearchExitPollMilliseconds);
        }

        Exception? failure = null;
        NoGcRegionStartOutcome restartOutcome = noGcRegionLost
            ? NoGcRegionStartOutcome.SkippedAfterUnexpectedLoss
            : NoGcRegionStartOutcome.InsufficientMemory;
        bool collectionCompleted = false;
        BackgroundGen2Completion completedCollection = default;
        long liveAfterCollection = 0;
        GCMemoryInfo? heapAfterCollection = null;
        long liveBefore = 0, workingSetBefore = 0, privateBefore = 0;
        TimeSpan pauseBefore = default, pauseAfterRegionExit = default, pauseAfterCollection = default;
        SearchGcPauseSnapshot pauseObservation = default;
        bool pauseObservationAvailable = false;
        double regionExitMilliseconds = 0, collectionMilliseconds = 0;
        Stopwatch stopwatch = new();
        try
        {
            // Diagnostic acquisition is inside the operation boundary too: a process-info
            // failure must not leave an already admitted checkpoint permanently active.
            liveBefore = GC.GetTotalMemory(forceFullCollection: false);
            using (Process processBefore = Process.GetCurrentProcess())
            {
                workingSetBefore = processBefore.WorkingSet64;
                privateBefore = processBefore.PrivateMemorySize64;
            }
            pauseBefore = GC.GetTotalPauseDuration();
            pauseAfterRegionExit = pauseAfterCollection = pauseBefore;
            pauseObservation = SearchGcPauseSnapshot.Capture();
            pauseObservationAvailable = true;
            stopwatch.Start();
            PauseInSearchCheckpointForTesting();
            bool directBackgroundExit = OperatingSystem.IsWindows() && endNoGcRegion;
            if (endNoGcRegion && !directBackgroundExit)
                EndNoGcRegion();
            if (restoreLatencyMode && !directBackgroundExit)
                GCSettings.LatencyMode = OperatingSystem.IsWindows() && restartNoGcRegion
                    ? GCLatencyMode.SustainedLowLatency : previousMode;
            if (!directBackgroundExit)
            {
                lock (Gate)
                    ReconcileRegionOwnershipAfterTransitionLocked(previousMode, restoreLatencyMode);
                ThrowInjectedInSearchCheckpointFailureForTesting();
            }
            regionExitMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            pauseAfterRegionExit = GC.GetTotalPauseDuration();
            lock (Gate)
            {
                // This is only the manual-request cutoff, not a claim to cover deferred
                // reference-release epochs. Those retain their post-search completion chain.
                _reclaim.BeginCollection();
            }
            completedCollection = CollectGeneration2ForAutomaticReclaimAsync(
                inSearchCheckpoint: true, exitOwnedNoGcRegion: directBackgroundExit)
                .GetAwaiter().GetResult();
            if (directBackgroundExit)
            {
                lock (Gate)
                    ReconcileRegionOwnershipAfterTransitionLocked(previousMode, restoreLatencyMode);
                ThrowInjectedInSearchCheckpointFailureForTesting();
            }
            collectionCompleted = true;
            collectionMilliseconds = stopwatch.Elapsed.TotalMilliseconds - regionExitMilliseconds;
            pauseAfterCollection = GC.GetTotalPauseDuration();
            liveAfterCollection = GC.GetTotalMemory(false);
            heapAfterCollection = GC.GetGCMemoryInfo();
            if (restartNoGcRegion)
            {
                // 有没有腾出空间只看回收后的堆：非压缩 Gen2 少回收多少活数据，重建出来的区域
                // 就还是同一份压力。回退到常规 GC 的那条路径不重建区域，因此不参与记账。
                signal.ObserveReclaimGain(Math.Max(0, liveBefore - liveAfterCollection));
            }
            // Capture the forced collection before TryStartNoGCRegion can replace the latest
            // GC info with a bookkeeping collection that has no pause of its own.
            signal.ObserveReclaimGcPause(pauseObservation.ObserveMaximumSince());

            lock (Gate)
            {
                _inSearchBackgroundGen2CompletedCountForTesting++;
                if (completedCollection.TimedOut)
                    _inSearchBackgroundGen2TimeoutDrainCountForTesting++;
                if (cancellationToken.IsCancellationRequested || !restartNoGcRegion
                    || completedCollection.TimedOut)
                {
                    // The completed collection already ended the old region. Either a deadline or a
                    // commit that cannot fit this region must publish a coherent default-GC
                    // state before the coordinator continues.
                    _configuredNoGcRegionBudgetBytes = 0;
                    _configuredNoGcRegionLohBudgetBytes = 0;
                    _noGcRegionBudgetBytes = 0;
                    _noGcRegionLohBudgetBytes = 0;
                    _noGcRegionAllocatedBytesAtStart = 0;
                    RestoreLatencyModeLocked();
                    if (!cancellationToken.IsCancellationRequested)
                        restartOutcome = NoGcRegionStartOutcome.DefaultGcRequested;
                    signal.UseDefaultGcFallback(
                        !cancellationToken.IsCancellationRequested
                        && fallbackSystemHeadroomConstrained);
                }
                else
                {
                    if (!_latencyModeOwned)
                        _previousMode = GCSettings.LatencyMode;
                    _latencyModeOwned = true;
                    // Keep the recovered reservation ceiling for this scope. Immediately
                    // growing back to the original request recreates the pressure episode.
                    long recoveryBudgetCapBytes = _exclusiveSearchScope?.RecoveryBudgetCapBytes ?? 0;
                    EffectiveNoGcRegionBudget effectiveBudget = ResolveEffectiveNoGcRegionBudget(
                        recoveryBudgetCapBytes > 0
                            ? Math.Min(configuredRegionBudgetBytes, recoveryBudgetCapBytes)
                            : configuredRegionBudgetBytes,
                        configuredLohBudgetBytes);
                    bool usefulCommitWindow = IsNoGcCommitWindowWorthEntering(
                        effectiveBudget.TotalBytes, effectiveBudget.LohBytes, signal.NextCommitReserveBytes);
                    bool restartAttempted = endNoGcRegion && effectiveBudget.CanStart && usefulCommitWindow;
                    // Captured before the size-fallback loop mutates it; see the admission path.
                    bool restartHeadroomLimitedBudget = effectiveBudget.Capped;
                    if (endNoGcRegion)
                    {
                        restartOutcome = effectiveBudget.CanStart && usefulCommitWindow
                            ? TryStartNoGcRegionWithSizeFallback(ref effectiveBudget, restart: true)
                            : effectiveBudget.CanStart
                                ? NoGcRegionStartOutcome.CommitWindowInsufficient
                                : NoGcRegionStartOutcome.SystemHeadroomInsufficient;
                        // Platform size fallback can shrink a previously useful reservation.
                        // Recheck the achieved budget before publishing its allocation limit.
                        if (restartOutcome == NoGcRegionStartOutcome.Started
                            && !IsNoGcCommitWindowWorthEntering(effectiveBudget.TotalBytes,
                                effectiveBudget.LohBytes, signal.NextCommitReserveBytes))
                        {
                            EndNoGcRegion();
                            usefulCommitWindow = false;
                            restartOutcome = NoGcRegionStartOutcome.CommitWindowInsufficient;
                        }
                        if (restartOutcome == NoGcRegionStartOutcome.CommitWindowInsufficient)
                            Entry.Logger.Info(
                                $"[CombatSolver/Test] GC_NO_GC_REGION_DECLINED stage=restart reason=commit_window " +
                                $"achieved_budget={effectiveBudget.TotalBytes} " +
                                $"next_commit_reserve={signal.NextCommitReserveBytes}");
                        if (restartOutcome == NoGcRegionStartOutcome.Started
                            && restartHeadroomLimitedBudget
                            && !IsNoGcRegionBudgetWorthEntering(
                                configuredRegionBudgetBytes, effectiveBudget.TotalBytes))
                        {
                            // Restarting into a region this far below the configured budget would
                            // resume the teardown/maintain cycle at a shorter period, which is the
                            // outcome the recovery budget ceiling exists to avoid.
                            EndNoGcRegion();
                            Entry.Logger.Info(
                                $"[CombatSolver/Test] GC_NO_GC_REGION_DECLINED stage=restart " +
                                $"configured_budget={configuredRegionBudgetBytes} " +
                                $"achieved_budget={effectiveBudget.TotalBytes} " +
                                $"percent_of_configured={effectiveBudget.TotalBytes * 100 / configuredRegionBudgetBytes} " +
                                $"system_memory_load={effectiveBudget.MemoryLoadBytes} " +
                                $"system_memory_limit={effectiveBudget.SystemMemoryLimitBytes}");
                            restartOutcome = NoGcRegionStartOutcome.SystemHeadroomInsufficient;
                        }
                    }
                    _noGcRegionActive = restartOutcome == NoGcRegionStartOutcome.Started;
                    if (_noGcRegionActive)
                        _lastEstablishedNoGcRegionBudgetBytesForTesting = effectiveBudget.TotalBytes;
                    _noGcRegionAllocatedBytesAtStart = GC.GetTotalAllocatedBytes(precise: false);
                    if (!_noGcRegionActive)
                    {
                        _configuredNoGcRegionBudgetBytes = 0;
                        _configuredNoGcRegionLohBudgetBytes = 0;
                        _noGcRegionBudgetBytes = 0;
                        _noGcRegionLohBudgetBytes = 0;
                        RestoreLatencyModeLocked();
                        // Let ordinary GC make progress first. A bounded recovery probe can
                        // retry at a later drained boundary after a new Gen2 and healthy headroom.
                        signal.UseDefaultGcFallback(IsSystemHeadroomOutcome(restartOutcome)
                            || restartOutcome == NoGcRegionStartOutcome.CommitWindowInsufficient
                                && restartHeadroomLimitedBudget,
                            allowNoGcRecovery: IsRecoverableNoGcOutcome(restartOutcome),
                            completedRecoveryGen2Index: restartAttempted ? 0 : completedCollection.Index);
                    }
                    else
                    {
                        _configuredNoGcRegionBudgetBytes = configuredRegionBudgetBytes;
                        _configuredNoGcRegionLohBudgetBytes = configuredLohBudgetBytes;
                        _noGcRegionBudgetBytes = effectiveBudget.TotalBytes;
                        _noGcRegionLohBudgetBytes = effectiveBudget.LohBytes;
                        ConfigureSearchMemoryLimit(
                            signal,
                            _noGcRegionAllocatedBytesAtStart,
                            effectiveBudget.TotalBytes,
                            effectiveBudget.TotalBytes,
                            effectiveBudget.LohBytes,
                            configuredRegionBudgetBytes,
                            configuredLohBudgetBytes);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (completedCollection.TimedOut)
                throw BackgroundCollectionTimeout();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            try
            {
                try
                {
                    stopwatch.Stop();
                    if (pauseObservationAvailable)
                        signal.ObserveReclaimGcPause(pauseObservation.ObserveMaximumSince());
                    using Process processAfter = Process.GetCurrentProcess();
                    processAfter.Refresh();
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] HEAP_RECLAIM reason=in_search_memory_checkpoint " +
                        $"trigger={reason} " +
                        $"mode=background_requested_non_compacting no_gc_region_ended={endNoGcRegion} " +
                        $"completion_kind={completedCollection.Kind ?? "none"} " +
                        $"completion_index={completedCollection.Index} " +
                        $"collection_requests={completedCollection.Requests} " +
                        $"observed_concurrent={completedCollection.Concurrent.ToString().ToLowerInvariant()} " +
                        $"collection_timed_out={completedCollection.TimedOut.ToString().ToLowerInvariant()} " +
                        $"no_gc_region_lost={noGcRegionLost.ToString().ToLowerInvariant()} " +
                        $"no_gc_region_restart={FormatStartOutcome(restartOutcome)} " +
                        $"fallback_latched={(restartOutcome != NoGcRegionStartOutcome.Started).ToString().ToLowerInvariant()} " +
                        $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1} " +
                        $"region_exit_ms={regionExitMilliseconds:F1} collection_ms={collectionMilliseconds:F1} " +
                        $"restart_ms={Math.Max(0, stopwatch.Elapsed.TotalMilliseconds - regionExitMilliseconds - collectionMilliseconds):F1} " +
                        $"region_exit_pause_ms={(pauseAfterRegionExit - pauseBefore).TotalMilliseconds:F1} " +
                        $"collection_pause_ms={(pauseAfterCollection - pauseAfterRegionExit).TotalMilliseconds:F1} " +
                        $"restart_pause_ms={(GC.GetTotalPauseDuration() - pauseAfterCollection).TotalMilliseconds:F1} " +
                        $"gc_pause_delta_ms={(GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds:F1} " +
                        $"max_observed_gc_pause_ms={signal.LastReclaimMaxObservedGcPause.TotalMilliseconds:F1} " +
                        CaptureLifecycle().DeltaFrom(lifecycleBefore).ToDiagnosticString() + " " +
                        $"collection_completed={collectionCompleted.ToString().ToLowerInvariant()} " +
                        $"managed_live_after_collect={liveAfterCollection} " +
                        $"heap_after_collect={heapAfterCollection?.HeapSizeBytes ?? 0} " +
                        $"fragmented_after_collect={heapAfterCollection?.FragmentedBytes ?? 0} " +
                        $"committed_after_collect={heapAfterCollection?.TotalCommittedBytes ?? 0} " +
                        $"managed_live_before={liveBefore} managed_live_after={GC.GetTotalMemory(false)} " +
                        $"working_set_before={workingSetBefore} working_set_after={processAfter.WorkingSet64} " +
                        $"private_before={privateBefore} private_after={processAfter.PrivateMemorySize64}");
                }
                catch (Exception diagnosticFailure)
                {
                    failure = CombineGcFailures(failure, diagnosticFailure);
                }
                lock (Gate)
                {
                    _reclaim.Finish(checkpointCompletion);
                    if (failure != null)
                    {
                        ReconcileRegionOwnershipAfterTransitionLocked(
                            previousMode,
                            restoreLatencyMode);
                    }
                    if (failure == null && (_regionExitRequired || _reclaimRequired))
                        RequestReclaimLocked(_reclaimReason);
                }
            }
            catch (Exception finalizationFailure)
            {
                failure = CombineGcFailures(failure, finalizationFailure);
            }
            finally
            {
                try
                {
                    if (failure == null || failure is OperationCanceledException)
                        checkpointCompletion.SetResult();
                    else
                        checkpointCompletion.SetException(failure);
                }
                finally
                {
                    if (failure == null || collectionCompleted)
                        manualCompletion?.SetResult();
                    else
                        manualCompletion?.SetException(failure);
                }
            }
        }

        if (failure != null)
            throw failure;
    }

    private static void CollectGeneration2ForSearch()
        => CollectGeneration2(blocking: true, compacting: false);

    private static void CollectGeneration2(bool blocking, bool compacting)
    {
        Lifecycle.RecordForcedCollection();
        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking,
            compacting);
    }

    private static void EndNoGcRegion()
    {
        Lifecycle.RecordEndAttempt();
        try
        {
            GC.EndNoGCRegion();
            Lifecycle.RecordEnded();
        }
        catch (InvalidOperationException)
        {
            if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
                Lifecycle.RecordUnexpectedLoss();
            throw;
        }
    }

    private static string DescribeProcessMemory()
    {
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return $"working_set={process.WorkingSet64} private_bytes={process.PrivateMemorySize64} " +
               $"managed_live={GC.GetTotalMemory(forceFullCollection: false)} " +
               $"managed_heap={memory.HeapSizeBytes} fragmented={memory.FragmentedBytes} managed_committed={memory.TotalCommittedBytes} " +
               $"memory_load={memory.MemoryLoadBytes} high_memory_threshold={memory.HighMemoryLoadThresholdBytes} " +
               $"total_available={memory.TotalAvailableMemoryBytes} " +
               $"gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)} " +
               $"latency={GCSettings.LatencyMode} tick_ms={Environment.TickCount64}";
    }

    private static NoGcRegionStartOutcome TryStartNoGcRegion(
        long totalSize,
        long lohSize,
        bool restart)
    {
        if (!NoGcRegionSupported)
            return NoGcRegionStartOutcome.PlatformUnsupported;
        GCLatencyMode previous = GCSettings.LatencyMode;
        bool started = false;
        try
        {
            // The induced collection restores the CLR's mode saved at region entry.
            // Keep automatic foreground Gen2 suppressed during background collection;
            // policy ownership separately preserves the caller's original mode.
            if (OperatingSystem.IsWindows())
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            Lifecycle.RecordStartAttempt();
            started = GC.TryStartNoGCRegion(
                totalSize,
                lohSize,
                disallowFullBlockingGC: true);
            if (started)
                Lifecycle.RecordStarted(restart);
            return started ? NoGcRegionStartOutcome.Started : NoGcRegionStartOutcome.InsufficientMemory;
        }
        catch (ArgumentOutOfRangeException exception) when (exception.ParamName == "totalSize")
        {
            // The maximum SOH reservation is runtime-specific and has no public query API.
            return NoGcRegionStartOutcome.RegionSizeUnsupported;
        }
        finally
        {
            if (!started && OperatingSystem.IsWindows()
                && GCSettings.LatencyMode != GCLatencyMode.NoGCRegion
                && GCSettings.LatencyMode != previous)
                GCSettings.LatencyMode = previous;
        }
    }

    /// <summary>
    /// 运行时对单个 No-GC 区域的 SOH 预留上限没有公开查询接口（macOS/regions GC 下远低于
    /// 16 GB）。首次尝试遇到 totalSize 越界时按二分逐级缩小预算，直到成功或低于最小预算。
    /// </summary>
    private static NoGcRegionStartOutcome TryStartNoGcRegionWithSizeFallback(
        ref EffectiveNoGcRegionBudget budget,
        bool restart = false)
    {
        NoGcRegionStartOutcome outcome = TryStartNoGcRegion(budget.TotalBytes, budget.LohBytes, restart);
        int attempts = 0;
        long requested = budget.TotalBytes;
        while (outcome == NoGcRegionStartOutcome.RegionSizeUnsupported && attempts < 12)
        {
            long halved = budget.TotalBytes / 2;
            if (halved < MinimumNoGcRegionBudgetBytes)
                break;
            attempts++;
            budget = budget with
            {
                TotalBytes = halved,
                LohBytes = Math.Min(budget.LohBytes, Math.Max(1, halved / 6)),
                Capped = true,
            };
            outcome = TryStartNoGcRegion(budget.TotalBytes, budget.LohBytes, restart);
        }
        if (attempts > 0)
        {
            Entry.Logger.Info(
                $"[CombatSolver/Test] GC_NO_GC_REGION_SIZE_FALLBACK attempts={attempts} " +
                $"requested={requested} final_budget={budget.TotalBytes} " +
                $"final_loh_budget={budget.LohBytes} outcome={FormatStartOutcome(outcome)}");
        }
        return outcome;
    }

    private static bool IsSystemHeadroomOutcome(NoGcRegionStartOutcome outcome)
        => outcome is NoGcRegionStartOutcome.SystemHeadroomInsufficient
            or NoGcRegionStartOutcome.InsufficientMemory;

    /// <summary>
    /// Decides whether a region that did start is worth keeping. Only system headroom can make
    /// this question interesting: a region shrunk to fit a platform reservation ceiling is still
    /// the best the platform can offer, so callers only consult this when the budget was already
    /// headroom-capped before the size-fallback loop ran.
    /// </summary>
    internal static bool IsNoGcRegionBudgetWorthEntering(
        long configuredBudgetBytes,
        long achievedBudgetBytes)
        => achievedBudgetBytes
            >= Math.Max(
                MinimumNoGcRegionBudgetBytes,
                configuredBudgetBytes / 100 * MinimumNoGcRegionBudgetPercent);

    private static string FormatStartOutcome(NoGcRegionStartOutcome outcome)
        => outcome switch
        {
            NoGcRegionStartOutcome.Started => "started",
            NoGcRegionStartOutcome.SkippedAfterUnexpectedLoss => "skipped_after_unexpected_loss",
            NoGcRegionStartOutcome.DefaultGcRequested => "default_gc_requested",
            NoGcRegionStartOutcome.InsufficientMemory => "insufficient_memory",
            NoGcRegionStartOutcome.RegionSizeUnsupported => "region_size_unsupported",
            NoGcRegionStartOutcome.PlatformUnsupported => "platform_unsupported",
            NoGcRegionStartOutcome.BackgroundUnavailable => "background_unavailable",
            NoGcRegionStartOutcome.SystemHeadroomInsufficient => "system_headroom_insufficient",
            NoGcRegionStartOutcome.CommitWindowInsufficient => "commit_window_insufficient",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };

    private static EffectiveNoGcRegionBudget ResolveEffectiveNoGcRegionBudget(
        long configuredBudgetBytes,
        long configuredLohBudgetBytes)
    {
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        long systemLimit = ResolveSystemMemoryLimit(memory);
        // FragmentedBytes describes all heap generations, not the SOH segments that
        // TryStartNoGCRegion can reserve. Do not credit those holes as physical headroom
        // for another reservation. The active search's allocation forecast is separate.
        long memoryLoad = OperatingSystem.IsWindows()
            ? PhysicalMemoryUsage.Capture(memory).UsedBytes
            : Math.Max(0, memory.MemoryLoadBytes);
        long effectiveBudget = CalculateAllocationCapacity(configuredBudgetBytes, systemLimit, memoryLoad, 0);
        Entry.Logger.Info($"[CombatSolver/Test] GC_ALLOCATION_CAPACITY physical_load={memoryLoad} " +
            $"system_limit={systemLimit} reservation_reusable_heap=0 effective_budget={effectiveBudget}");
        if (effectiveBudget < MinimumNoGcRegionBudgetBytes)
            effectiveBudget = 0;
        long effectiveLohBudget = effectiveBudget == 0
            ? 0
            : Math.Min(
                configuredLohBudgetBytes,
                Math.Max(1, effectiveBudget / 6));
        return new EffectiveNoGcRegionBudget(
            effectiveBudget,
            effectiveLohBudget,
            memoryLoad,
            systemLimit,
            effectiveBudget < configuredBudgetBytes);
    }

    private static long CaptureCurrentPhysicalMemoryLoad()
        => PhysicalMemoryUsage.Capture(GC.GetGCMemoryInfo()).UsedBytes;

    internal static long CalculateReusableHeapBytes(long heapSize, long fragmented, long currentLive)
        => Math.Max(0, Math.Min(fragmented, heapSize - Math.Max(0, currentLive)));

    internal static long CalculateAllocationCapacity(long configured, long systemLimit, long memoryLoad, long reusableHeap)
    {
        if (systemLimit == long.MaxValue) return configured;
        // Existing heap space does not grant permission to run at the physical pressure limit.
        long headroom = Math.Max(0, systemLimit - memoryLoad);
        if (headroom == 0) return 0;
        return Math.Min(configured, headroom > long.MaxValue - reusableHeap
            ? long.MaxValue : headroom + reusableHeap);
    }

    internal static long ResolveSystemMemoryLimit(GCMemoryInfo memory)
    {
        long highMemoryThreshold = memory.HighMemoryLoadThresholdBytes;
        return highMemoryThreshold <= 0
            ? long.MaxValue
            : Math.Max(
                1,
                highMemoryThreshold / 100 * SystemMemoryPressureLimitPercent);
    }

    private static void ReconcileRegionOwnershipAfterTransitionLocked(
        GCLatencyMode previousMode,
        bool restoreLatencyMode)
    {
        bool runtimeRegionActive = GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
        _noGcRegionActive = runtimeRegionActive;
        _latencyModeOwned = restoreLatencyMode
            && (runtimeRegionActive || GCSettings.LatencyMode != previousMode);
        if (runtimeRegionActive)
            return;

        _noGcRegionAllocatedBytesAtStart = 0;
        _noGcRegionBudgetBytes = 0;
        _noGcRegionLohBudgetBytes = 0;
        _configuredNoGcRegionBudgetBytes = 0;
        _configuredNoGcRegionLohBudgetBytes = 0;
        _largestSearchAllocatedBytes = 0;
    }

    private static void RestoreLatencyModeLocked()
    {
        if (!_latencyModeOwned)
            return;
        GCSettings.LatencyMode = _previousMode;
        _latencyModeOwned = false;
    }

    private sealed class DefaultGcSearchScope(SearchGcLifecycleSnapshot lifecycleAtEntry)
        : SearchGcScope(lifecycleAtEntry, SearchGcLifecycleAttribution.SharedProcessWindow)
    {
        public override void Dispose() => ExitDefaultGcSearch(this);
    }

}
