using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace CombatSolver;

// Owns process-wide GC mode and combat-end reclamation; it is not part of the search algorithm.
internal static class SearchGcPolicy
{
    private const long BackgroundReclaimThresholdBytes = 256L * 1024 * 1024;
    private const int ReclaimReferenceReleaseDelayMilliseconds = 250;
    private const int ReclaimCompletionTimeoutMilliseconds = 30_000;
    private const int ConcurrentSearchExitPollMilliseconds = 10;
    private const int SystemMemoryPressureLimitPercent = 95;
    private const long MinimumNoGcRegionBudgetBytes = 512L * 1024 * 1024;
    private static readonly Lock Gate = new();
    private static int _activeSearches;
    private static int _defaultGcSearches;
    private static bool _automaticGcLifecycleUsed;
    private static GCLatencyMode _previousMode;
    private static bool _latencyModeOwned;
    private static bool _noGcRegionActive;
    private static bool _regionExitRequired;
    private static bool _reclaimRequired;
    private static bool _reclaimRequested;
    private static bool _reclaimActive;
    private static bool _workingSetTrimRequested;
    private static bool _activeReclaimTrimsWorkingSet;
    private static bool _manualReclaimRequested;
    private static string _reclaimReason = "unspecified";
    private static TaskCompletionSource? _reclaimCompletion;
    private static Task _reclaimTask = Task.CompletedTask;
    private static TaskCompletionSource? _manualReclaimCompletion;
    private static Task _manualReclaimTask = Task.CompletedTask;
    private static Task _referenceReleaseBarrier = Task.CompletedTask;
    private static long _referenceReleaseEpoch;
    private static long _requiredReferenceReleaseCollectionEpoch;
    private static bool _activeReclaimCollectsGeneration2;
    private static bool _activeGeneration2CollectionStarted;
    private static long _activeGeneration2CoverageEpoch;
    private static int _generation2CoveragePauseStageForTesting;
    private static TaskCompletionSource? _generation2CoverageReachedForTesting;
    private static TaskCompletionSource? _generation2CoverageResumeForTesting;
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
                return _reclaimActive && _activeSearches == 0;
        }
    }

    private enum NoGcRegionStartOutcome
    {
        Started,
        SkippedAfterUnexpectedLoss,
        InsufficientMemory,
        RegionSizeUnsupported,
        PlatformUnsupported,
        SystemHeadroomInsufficient,
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
        int Requests);
    internal readonly record struct CombatLifecyclePressure(
        long AllocatedBytes,
        bool RequiresCollection);

    // Mobile .NET runtimes (Android/iOS) do not support GC.TryStartNoGCRegion; skip straight to the
    // SustainedLowLatency fallback instead of calling into it.
    private static readonly bool NoGcRegionSupported =
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
    {
        if (noGcRegionBudgetBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(noGcRegionBudgetBytes));
        ArgumentNullException.ThrowIfNull(memoryPressureSignal);
        if (!enableNoGcRegion)
            return EnterDefaultGcSearch(memoryPressureSignal, cancellationToken);
        lock (Gate)
            _automaticGcLifecycleUsed = true;
        long noGcRegionLohBudgetBytes = Math.Max(
            256L * 1024 * 1024,
            noGcRegionBudgetBytes / 6);
        bool budgetChangeLogged = false;
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
                else if (_reclaimActive || _reclaimRequested)
                {
                    reclaimTask = _reclaimTask;
                }
                else if (_manualReclaimRequested)
                {
                    reclaimTask = _manualReclaimTask;
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
                                        return new SearchScope(allocatedBytesAtEntry, memoryPressureSignal);
                                    }
                                }
                            }
                            else
                            {
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
                            _previousMode = GCSettings.LatencyMode;
                            _latencyModeOwned = true;
                            EffectiveNoGcRegionBudget effectiveBudget = ResolveEffectiveNoGcRegionBudget(
                                noGcRegionBudgetBytes,
                                noGcRegionLohBudgetBytes);
                            NoGcRegionStartOutcome startOutcome = effectiveBudget.CanStart
                                ? TryStartNoGcRegion(
                                    effectiveBudget.TotalBytes,
                                    effectiveBudget.LohBytes)
                                : NoGcRegionStartOutcome.SystemHeadroomInsufficient;
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
                                memoryPressureSignal.UseDefaultGcFallback();
                            }
                            _activeSearches++;
                            return new SearchScope(allocatedBytesAtEntry, memoryPressureSignal);
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

    private static IDisposable EnterDefaultGcSearch(
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
                    && !_reclaimActive
                    && !_reclaimRequested
                    && !_manualReclaimRequested)
                {
                    _activeSearches++;
                    _defaultGcSearches++;
                    memoryPressureSignal.Disable();
                    Entry.Logger.Info(
                        "[CombatSolver/Test] GC_LATENCY policy=clr_default no_gc_enabled=false");
                    return new DefaultGcSearchScope();
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
        bool restoreLatencyMode = _latencyModeOwned;
        GCLatencyMode previousMode = _previousMode;
        _regionExitOnlyRequested = false;
        _noGcRegionActive = false;
        _latencyModeOwned = false;
        _noGcRegionAllocatedBytesAtStart = 0;
        _noGcRegionBudgetBytes = 0;
        _noGcRegionLohBudgetBytes = 0;
        _configuredNoGcRegionBudgetBytes = 0;
        _configuredNoGcRegionLohBudgetBytes = 0;
        _largestSearchAllocatedBytes = 0;
        _noGcRegionExitWithoutCollectionCountForTesting++;

        bool isCombatEnd = reason is not ("no_gc_region_rollover"
            or "no_gc_region_exhausted"
            or "before_next_search"
            or "no_gc_disabled");
        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            TaskCompletionSource? failedManualCompletion = null;
            int gen2Before = GC.CollectionCount(GC.MaxGeneration);
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                if (isCombatEnd)
                    await Task.Delay(System.Random.Shared.Next(3_000, 5_001));
                // 把 No-GC 区域结束推迟到击杀后 3-5s（奖励环节），让用户体感没有卡顿。
                if (endNoGcRegion)
                    GC.EndNoGCRegion();
                if (restoreLatencyMode)
                    GCSettings.LatencyMode = previousMode;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                stopwatch.Stop();
                Entry.Logger.Info(
                    $"[CombatSolver/Test] HEAP_REGION_EXIT reason={reason} " +
                    $"no_gc_region_ended={endNoGcRegion} forced_gen2=false " +
                    $"gen2_delta={GC.CollectionCount(GC.MaxGeneration) - gen2Before} " +
                    $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1} " +
                    $"managed_live_bytes={GC.GetTotalMemory(forceFullCollection: false)}");
                lock (Gate)
                {
                    _regionExitOnlyCompletion = null;
                    if (failure == null && _activeSearches == 0)
                    {
                        if (_manualReclaimRequested && !_reclaimRequested)
                            RequestReclaimLocked("manual_gc");
                        if (_reclaimRequested)
                            StartReclaimLocked();
                    }
                    else if (failure != null && _manualReclaimRequested)
                    {
                        failedManualCompletion = _manualReclaimCompletion;
                        _manualReclaimRequested = false;
                        _manualReclaimCompletion = null;
                        _manualReclaimTask = Task.CompletedTask;
                    }
                }
                if (failure == null)
                    completion.SetResult();
                else
                {
                    completion.SetException(failure);
                    failedManualCompletion?.SetException(failure);
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
            if (_activeSearches > 0 && _reclaimActive)
            {
                // An active reclaim can only be an in-search memory checkpoint: background
                // reclaim never starts until every search scope has exited. Its blocking Gen2
                // therefore already satisfies this manual request without scheduling a second one.
                reclaim = _reclaimTask;
            }
            else if (_activeSearches > 0)
            {
                if (!_manualReclaimRequested)
                {
                    _manualReclaimRequested = true;
                    _manualReclaimCompletion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _manualReclaimTask = _manualReclaimCompletion.Task;
                }
                reclaim = _manualReclaimTask;
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

    internal static Task ForceManualMemoryRelease()
    {
        Task reclaim;
        lock (Gate)
        {
            if (_workingSetTrimRequested || _activeReclaimTrimsWorkingSet)
            {
                reclaim = WaitForReclaimChainAsync(_reclaimTask);
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
                $"[CombatSolver/Test] MANUAL_MEMORY_RELEASE_FAILED exception={task.Exception?.GetBaseException()}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Entry.Logger.Info(
            $"[CombatSolver/Test] MANUAL_MEMORY_RELEASE queued=true completed={reclaim.IsCompleted.ToString().ToLowerInvariant()}");
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
            && _reclaimActive
            && _activeReclaimCollectsGeneration2
            && (!_activeGeneration2CollectionStarted
                || _activeGeneration2CoverageEpoch >= requiredCoverageEpoch.Value);
        bool coverageCollectionRequired = requiredCoverageEpoch.HasValue
            && !activeCollectionWillCoverRelease;
        bool requestCollection = coverageCollectionRequired
            || ((forceCollection || lifecycleCollectionRequired)
                && !activeCollectionWillCoverRelease);
        if (_reclaimActive)
        {
            if (_activeSearches > 0 || requestCollection)
            {
                _regionExitRequired = true;
                _reclaimRequired |= requestCollection;
                _reclaimReason = reason;
            }
            else
            {
                // A collection whose mark starts after this release covers the graph. Joining
                // it must not enqueue an identical second Gen2 collection.
                _backgroundReclaimJoinCountForTesting++;
            }
            return WaitForReclaimChainAsync(_reclaimTask);
        }
        if (_reclaimRequested)
        {
            _reclaimRequired |= requestCollection;
            return WaitForReclaimChainAsync(_reclaimTask);
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
            if (_activeSearches != 0 || _reclaimActive || _reclaimRequested)
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
                if (!_reclaimActive && !_reclaimRequested)
                    return;
                checkpoint = _reclaimTask;
            }
        }
    }

    private static Task RequestReclaimLocked(string reason)
    {
        _regionExitRequired = true;
        if (!_reclaimActive && !_reclaimRequested)
        {
            _reclaimRequested = true;
            _reclaimReason = reason;
            _activeReclaimSequence = checked(++_nextReclaimSequence);
            _reclaimCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _reclaimTask = _reclaimCompletion.Task;
            Entry.Logger.Info(
                $"[CombatSolver/Test] MEMORY_RECLAIM stage=requested " +
                $"id={_activeReclaimSequence} reason={reason} active_searches={_activeSearches} " +
                $"gen2_required={_reclaimRequired.ToString().ToLowerInvariant()} " +
                DescribeProcessMemory());
        }
        if (!_reclaimActive
            && _activeSearches == 0
            && _regionExitOnlyTask.IsCompleted)
            StartReclaimLocked();
        return _reclaimTask;
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
        if (!_reclaimRequested || _activeSearches != 0)
            throw new InvalidOperationException("GC 回收只能在请求已登记且搜索线程退出后启动。");

        TaskCompletionSource completion = _reclaimCompletion
            ?? throw new InvalidOperationException("GC 回收请求缺少完成信号。");
        TaskCompletionSource? manualCompletion = null;
        if (_manualReclaimRequested)
        {
            manualCompletion = _manualReclaimCompletion
                ?? throw new InvalidOperationException("手动 GC 请求缺少完成信号。");
            _manualReclaimRequested = false;
            _manualReclaimCompletion = null;
            _manualReclaimTask = Task.CompletedTask;
            _reclaimRequired = true;
        }
        string reason = _reclaimReason;
        long reclaimSequence = _activeReclaimSequence;
        bool endNoGcRegion = _noGcRegionActive
            && GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
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
        _reclaimRequested = false;
        _reclaimActive = true;
        _regionExitRequired = false;
        _reclaimRequired = false;
        _workingSetTrimRequested = false;
        _activeReclaimTrimsWorkingSet = trimWorkingSet;
        _activeReclaimCollectsGeneration2 = collectGeneration2;
        _activeGeneration2CollectionStarted = false;
        _activeGeneration2CoverageEpoch = 0;
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
        Entry.Logger.Info(
            $"[CombatSolver/Test] MEMORY_RECLAIM stage=started " +
            $"id={reclaimSequence} reason={reason} gen2_required={collectGeneration2.ToString().ToLowerInvariant()} " +
            $"end_no_gc={endNoGcRegion.ToString().ToLowerInvariant()} " +
            $"region_allocated={regionAllocatedBytes} region_budget={regionBudgetBytes} " +
            $"largest_search_allocated={largestSearchAllocatedBytes} " +
            DescribeProcessMemory());

        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                long liveBefore = GC.GetTotalMemory(forceFullCollection: false);
                using Process processBefore = Process.GetCurrentProcess();
                long workingSetBefore = processBefore.WorkingSet64;
                long privateBefore = processBefore.PrivateMemorySize64;
                TimeSpan pauseBefore = GC.GetTotalPauseDuration();
                Stopwatch stopwatch = Stopwatch.StartNew();

                if (endNoGcRegion)
                    GC.EndNoGCRegion();
                if (restoreLatencyMode)
                    GCSettings.LatencyMode = previousMode;
                Entry.Logger.Info(
                    $"[CombatSolver/Test] MEMORY_RECLAIM stage=region_exited " +
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
                        _activeGeneration2CollectionStarted = true;
                        collectionCoverageEpoch = _referenceReleaseEpoch;
                        _activeGeneration2CoverageEpoch = collectionCoverageEpoch;
                    }
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] MEMORY_RECLAIM stage=gen2_started " +
                        $"id={reclaimSequence} reason={reason} coverage_epoch={collectionCoverageEpoch} " +
                        DescribeProcessMemory());
                    await PauseGeneration2CoverageForTestingAsync(
                        afterCoverageCapture: true);
                    completedCollection = trimWorkingSet
                        ? CollectGeneration2ForManualMemoryRelease()
                        : await CollectGeneration2InBackgroundAsync();
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
                        $"mode={(trimWorkingSet ? "blocking_compacting_working_set_trim" : "background_non_compacting")} " +
                        $"no_gc_region_ended={endNoGcRegion} " +
                        $"forced_gen2=true gen2_delta={generation2Collections} " +
                        $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1} " +
                        $"gc_pause_delta_ms={(GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds:F1} " +
                        $"completion_kind={completedCollection.Kind} " +
                        $"completion_index={completedCollection.Index} " +
                        $"collection_requests={completedCollection.Requests} " +
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
                lock (Gate)
                {
                    _reclaimActive = false;
                    _reclaimCompletion = null;
                    _activeReclaimCollectsGeneration2 = false;
                    _activeReclaimTrimsWorkingSet = false;
                    _activeGeneration2CollectionStarted = false;
                    _activeGeneration2CoverageEpoch = 0;
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
                if (failure == null)
                {
                    completion.SetResult();
                    manualCompletion?.SetResult();
                }
                else
                {
                    completion.SetException(failure);
                    manualCompletion?.SetException(failure);
                }
            }
        });
    }

    private static async Task<BackgroundGen2Completion> CollectGeneration2InBackgroundAsync()
    {
        // A forced background collection can join an automatic Gen2 that was already marking.
        // Such a collection cannot reclaim allocations created after its mark began. A fresh
        // LOH sentinel distinguishes that case: only a Gen2 that began after this method's
        // reference-release boundary can clear it.
        WeakReference completionSentinel = CreateBackgroundCollectionSentinel();
        long backgroundIndexBefore = GC.GetGCMemoryInfo(GCKind.Background).Index;
        long fullBlockingIndexBefore = GC.GetGCMemoryInfo(GCKind.FullBlocking).Index;
        long deadline = Environment.TickCount64 + ReclaimCompletionTimeoutMilliseconds;
        long nextRequestAt = 0;
        int requests = 0;
        while (true)
        {
            long now = Environment.TickCount64;
            if (now >= nextRequestAt)
            {
                GC.Collect(
                    GC.MaxGeneration,
                    GCCollectionMode.Forced,
                    blocking: false,
                    compacting: false);
                requests++;
                nextRequestAt = now + 1_000;
            }

            GCMemoryInfo background = GC.GetGCMemoryInfo(GCKind.Background);
            GCMemoryInfo fullBlocking = GC.GetGCMemoryInfo(GCKind.FullBlocking);
            if (background.Index > backgroundIndexBefore)
            {
                if (!completionSentinel.IsAlive)
                    return new BackgroundGen2Completion("background", background.Index, requests);
                backgroundIndexBefore = background.Index;
                fullBlockingIndexBefore = Math.Max(fullBlockingIndexBefore, fullBlocking.Index);
                nextRequestAt = 0;
            }
            else if (fullBlocking.Index > fullBlockingIndexBefore)
            {
                if (!completionSentinel.IsAlive)
                    return new BackgroundGen2Completion("full_blocking", fullBlocking.Index, requests);
                fullBlockingIndexBefore = fullBlocking.Index;
                backgroundIndexBefore = Math.Max(backgroundIndexBefore, background.Index);
                nextRequestAt = 0;
            }
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException(
                    $"后台 Gen2 回收在 {ReclaimCompletionTimeoutMilliseconds} ms 内没有完成。");
            }
            await Task.Delay(25);
        }
    }

    private static BackgroundGen2Completion CollectGeneration2ForManualMemoryRelease()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: true);
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

    private static void ExitLowLatencySearch(long allocatedBytesAtEntry)
    {
        lock (Gate)
        {
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
                    _reclaimRequired = true;
                    RequireCollectionAfterNextReferenceReleaseLocked();
                    Entry.Logger.Warn(
                        "[CombatSolver/Test] GC_LATENCY no_gc_region_exhausted_before_early_exit=true " +
                        "reclaim=deferred_until_reference_release");
                }
                StartRegionExitOnlyLocked(_regionExitOnlyReason);
                return;
            }

            bool noGcRegionExhausted = _noGcRegionActive
                && GCSettings.LatencyMode != GCLatencyMode.NoGCRegion;
            if (noGcRegionExhausted)
            {
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

            if (_reclaimRequested)
            {
                StartReclaimLocked();
                return;
            }
            if (_manualReclaimRequested)
            {
                RequestReclaimLocked("manual_gc");
                return;
            }
            if (_noGcRegionActive)
            {
                Entry.Logger.Info(
                    "[CombatSolver/Test] GC_LATENCY no_gc_region_retained_until_combat_reset=true");
                return;
            }
            RestoreLatencyModeLocked();
            Entry.Logger.Info(
                $"[CombatSolver/Test] GC_LATENCY exit restored={GCSettings.LatencyMode} " +
                $"entry={_previousMode}");
        }
    }

    private static void ExitDefaultGcSearch()
    {
        lock (Gate)
        {
            if (_defaultGcSearches <= 0 || _activeSearches <= 0)
                throw new InvalidOperationException("CLR 常规 GC 搜索作用域计数失衡。");
            _defaultGcSearches--;
            if (--_activeSearches != 0)
                return;
            if (_reclaimRequested && _regionExitOnlyTask.IsCompleted)
                StartReclaimLocked();
            else if (_manualReclaimRequested && _regionExitOnlyTask.IsCompleted)
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
        long smallObjectBudgetBytes = Math.Max(1, regionBudgetBytes - lohBudgetBytes);
        long smallObjectLimitBytes = smallObjectBudgetBytes / 5 * 4;
        long remainingLimitBytes = Math.Max(1, remainingRegionBytes / 4 * 3);
        long allocationLimitBytes = Math.Max(1, Math.Min(smallObjectLimitBytes, remainingLimitBytes));
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        long systemMemoryLimitBytes = ResolveSystemMemoryLimit(memory);
        signal.Configure(
            allocatedBytesAtEntry,
            allocationLimitBytes,
            Math.Max(0, memory.MemoryLoadBytes),
            systemMemoryLimitBytes,
            cancellationToken => ReclaimWithinSearch(
                signal,
                configuredRegionBudgetBytes,
                configuredLohBudgetBytes,
                cancellationToken),
            HasUnexpectedNoGcLoss);
        Entry.Logger.Info(
            $"[CombatSolver/Test] GC_SEARCH_ALLOCATION_LIMIT limit={allocationLimitBytes} " +
            $"remaining_region={remainingRegionBytes} region_budget={regionBudgetBytes} " +
            $"loh_budget={lohBudgetBytes} configured_budget={configuredRegionBudgetBytes} " +
            $"system_memory_load={memory.MemoryLoadBytes} " +
            $"system_memory_limit={systemMemoryLimitBytes}");
    }

    private static bool HasUnexpectedNoGcLoss()
    {
        lock (Gate)
        {
            return _noGcRegionActive
                && GCSettings.LatencyMode != GCLatencyMode.NoGCRegion;
        }
    }

    private static void ReclaimWithinSearch(
        SearchMemoryPressureSignal signal,
        long configuredRegionBudgetBytes,
        long configuredLohBudgetBytes,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource checkpointCompletion;
        TaskCompletionSource? manualCompletion = null;
        bool endNoGcRegion;
        bool noGcRegionLost;
        bool restoreLatencyMode;
        GCLatencyMode previousMode;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Gate)
            {
                if (_activeSearches == 1 && !_reclaimActive && !_reclaimRequested)
                {
                    if (_manualReclaimRequested)
                    {
                        manualCompletion = _manualReclaimCompletion
                            ?? throw new InvalidOperationException("手动 GC 请求缺少完成信号。");
                        _manualReclaimRequested = false;
                        _manualReclaimCompletion = null;
                        _manualReclaimTask = Task.CompletedTask;
                    }
                    checkpointCompletion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _reclaimActive = true;
                    _reclaimCompletion = checkpointCompletion;
                    _reclaimTask = checkpointCompletion.Task;
                    noGcRegionLost = _noGcRegionActive
                        && GCSettings.LatencyMode != GCLatencyMode.NoGCRegion;
                    endNoGcRegion = _noGcRegionActive && !noGcRegionLost;
                    restoreLatencyMode = _latencyModeOwned;
                    previousMode = _previousMode;
                    _noGcRegionActive = false;
                    _latencyModeOwned = false;
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
        long liveBefore = GC.GetTotalMemory(forceFullCollection: false);
        using Process processBefore = Process.GetCurrentProcess();
        long workingSetBefore = processBefore.WorkingSet64;
        long privateBefore = processBefore.PrivateMemorySize64;
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            if (endNoGcRegion)
                GC.EndNoGCRegion();
            if (restoreLatencyMode)
                GCSettings.LatencyMode = previousMode;
            CollectGeneration2ForSearch();
            collectionCompleted = true;

            lock (Gate)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // The blocking Gen2 already ended the old region. A deadline observed here
                    // must publish a coherent default-GC state before the coordinator returns
                    // its best completed Smart layer.
                    _configuredNoGcRegionBudgetBytes = 0;
                    _configuredNoGcRegionLohBudgetBytes = 0;
                    _noGcRegionBudgetBytes = 0;
                    _noGcRegionLohBudgetBytes = 0;
                    _noGcRegionAllocatedBytesAtStart = 0;
                    RestoreLatencyModeLocked();
                    signal.UseDefaultGcFallback();
                }
                else
                {
                    _previousMode = GCSettings.LatencyMode;
                    _latencyModeOwned = true;
                    EffectiveNoGcRegionBudget effectiveBudget = ResolveEffectiveNoGcRegionBudget(
                        configuredRegionBudgetBytes,
                        configuredLohBudgetBytes);
                    if (endNoGcRegion)
                    {
                        restartOutcome = effectiveBudget.CanStart
                            ? TryStartNoGcRegion(
                                effectiveBudget.TotalBytes,
                                effectiveBudget.LohBytes)
                            : NoGcRegionStartOutcome.SystemHeadroomInsufficient;
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
                        // The runtime may terminate a region for memory pressure or an external
                        // collection. Retrying the same reservation during this search recreates the
                        // failure loop, so fall back once and let the CLR collect normally.
                        signal.UseDefaultGcFallback();
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
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            stopwatch.Stop();
            using Process processAfter = Process.GetCurrentProcess();
            processAfter.Refresh();
            Entry.Logger.Info(
                $"[CombatSolver/Test] HEAP_RECLAIM reason=in_search_memory_checkpoint " +
                $"mode=blocking_non_compacting no_gc_region_ended={endNoGcRegion} " +
                $"no_gc_region_lost={noGcRegionLost.ToString().ToLowerInvariant()} " +
                $"no_gc_region_restart={FormatStartOutcome(restartOutcome)} " +
                $"fallback_latched={(restartOutcome != NoGcRegionStartOutcome.Started).ToString().ToLowerInvariant()} " +
                $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1} " +
                $"gc_pause_delta_ms={(GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds:F1} " +
                $"collection_completed={collectionCompleted.ToString().ToLowerInvariant()} " +
                $"managed_live_before={liveBefore} managed_live_after={GC.GetTotalMemory(false)} " +
                $"working_set_before={workingSetBefore} working_set_after={processAfter.WorkingSet64} " +
                $"private_before={privateBefore} private_after={processAfter.PrivateMemorySize64}");
            lock (Gate)
            {
                _reclaimActive = false;
                _reclaimCompletion = null;
                _activeReclaimCollectsGeneration2 = false;
                _activeGeneration2CollectionStarted = false;
                _activeGeneration2CoverageEpoch = 0;
                if (failure == null && (_regionExitRequired || _reclaimRequired))
                    RequestReclaimLocked(_reclaimReason);
            }
            if (failure == null || failure is OperationCanceledException)
                checkpointCompletion.SetResult();
            else
                checkpointCompletion.SetException(failure);
            if (manualCompletion != null)
            {
                if (failure == null || collectionCompleted)
                    manualCompletion.SetResult();
                else
                    manualCompletion.SetException(failure);
            }
        }

        if (failure != null)
            throw failure;
    }

    private static void CollectGeneration2ForSearch()
    {
        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: false);
    }

    private static string DescribeProcessMemory()
    {
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return $"working_set={process.WorkingSet64} private_bytes={process.PrivateMemorySize64} " +
               $"managed_live={GC.GetTotalMemory(forceFullCollection: false)} " +
               $"managed_heap={memory.HeapSizeBytes} fragmented={memory.FragmentedBytes} " +
               $"memory_load={memory.MemoryLoadBytes} high_memory_threshold={memory.HighMemoryLoadThresholdBytes} " +
               $"total_available={memory.TotalAvailableMemoryBytes} " +
               $"gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)} " +
               $"latency={GCSettings.LatencyMode} tick_ms={Environment.TickCount64}";
    }

    private static NoGcRegionStartOutcome TryStartNoGcRegion(
        long totalSize,
        long lohSize)
    {
        if (!NoGcRegionSupported)
            return NoGcRegionStartOutcome.PlatformUnsupported;
        try
        {
            return GC.TryStartNoGCRegion(
                totalSize,
                lohSize,
                disallowFullBlockingGC: true)
                ? NoGcRegionStartOutcome.Started
                : NoGcRegionStartOutcome.InsufficientMemory;
        }
        catch (ArgumentOutOfRangeException exception) when (exception.ParamName == "totalSize")
        {
            // The maximum SOH reservation is runtime-specific and has no public query API.
            return NoGcRegionStartOutcome.RegionSizeUnsupported;
        }
    }

    private static string FormatStartOutcome(NoGcRegionStartOutcome outcome)
        => outcome switch
        {
            NoGcRegionStartOutcome.Started => "started",
            NoGcRegionStartOutcome.SkippedAfterUnexpectedLoss => "skipped_after_unexpected_loss",
            NoGcRegionStartOutcome.InsufficientMemory => "insufficient_memory",
            NoGcRegionStartOutcome.RegionSizeUnsupported => "region_size_unsupported",
            NoGcRegionStartOutcome.PlatformUnsupported => "platform_unsupported",
            NoGcRegionStartOutcome.SystemHeadroomInsufficient => "system_headroom_insufficient",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };

    private static EffectiveNoGcRegionBudget ResolveEffectiveNoGcRegionBudget(
        long configuredBudgetBytes,
        long configuredLohBudgetBytes)
    {
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        long systemLimit = ResolveSystemMemoryLimit(memory);
        long memoryLoad = Math.Max(0, memory.MemoryLoadBytes);
        long headroom = systemLimit == long.MaxValue
            ? configuredBudgetBytes
            : Math.Max(0, systemLimit - memoryLoad);
        long effectiveBudget = Math.Min(configuredBudgetBytes, headroom);
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

    internal static long ResolveSystemMemoryLimit(GCMemoryInfo memory)
    {
        long highMemoryThreshold = memory.HighMemoryLoadThresholdBytes;
        return highMemoryThreshold <= 0
            ? long.MaxValue
            : Math.Max(
                1,
                highMemoryThreshold / 100 * SystemMemoryPressureLimitPercent);
    }

    private static void RestoreLatencyModeLocked()
    {
        if (!_latencyModeOwned)
            return;
        GCSettings.LatencyMode = _previousMode;
        _latencyModeOwned = false;
    }

    private sealed class DefaultGcSearchScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            ExitDefaultGcSearch();
        }
    }

    private sealed class SearchScope(
        long allocatedBytesAtEntry,
        SearchMemoryPressureSignal memoryPressureSignal) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            memoryPressureSignal.Disable();
            ExitLowLatencySearch(allocatedBytesAtEntry);
        }
    }
}
