using System.Diagnostics;
using System.Runtime;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace CombatSolver;

internal enum SearchReason
{
    Manual,
    AutoTurnStart,
    Deploy,
    FullAuto,
    DeploymentDrift,
    PlanExhausted,
}

internal enum ReplanCause
{
    InitialSearch,
    StateMismatch,
    ManualDivergence,
    ContinuationMissing,
    DeploymentDrift,
    PlanExhausted,
    ExplicitRequest,
}

internal static class SolverController
{
    private static SolverCombatSession _combat = new();
    private static SolverSearchSession? _search;
    private static SolverDeploymentSession? _deployment;
    private static CombatBugReportClassificationSnapshot? _lastBugReportClassification;
    private static int _nextSearchGeneration;
    private static int _combatLifecycleGeneration;
    private static bool _solverDisabled;
    private static bool _stopFullAutoOnCombatEnd;
    private static bool _stopFullAutoOnDeathTurn = true;
    private static bool _stopFullAutoOnWorseRecalculation = true;
    private static readonly SearchFramePressureSignal FramePressureSignal = new();
    private static readonly HashSet<string> DeployedCardIdsForTesting = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DeployedPotionIdsForTesting = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<Task> PendingSearchReferenceReleases = [];
    private static readonly List<Task> PendingDeploymentReferenceReleases = [];
    private static readonly List<Task> PendingDeferredSearchReleases = [];
    private static readonly List<Task> PendingCombatDeferredOperations = [];
    private static int _searchReferenceReleaseScheduledCountForTesting;
    private static int _searchReferenceReleaseCompletedCountForTesting;
    private static int _searchCtsDisposeCountForTesting;
    private static int _deploymentReferenceReleaseScheduledCountForTesting;
    private static int _deploymentReferenceReleaseCompletedCountForTesting;
    private static int _deploymentCtsDisposeCountForTesting;
    private static CancellationTokenSource? _deferredSearchCts;
    private static int _deferredSearchId;
    private static CancellationTokenSource _combatDeferredOperationCancellation = new();

    public static bool IsSearching
        => _search != null
           || _deferredSearchCts != null
           || PlayerTurnSetupCoordinator.IsSearching;
    public static bool IsDeploying => _deployment != null;
    public static bool IsStoppingSearch
        => _search?.Interaction.StopRequested == true || PlayerTurnSetupCoordinator.IsStoppingSearch;
    public static bool SolverDisabled => _solverDisabled;
    public static bool CanApplyCurrentTurn
        => _search is { } search
               && search.Interaction.CurrentTakeoverRequest == null
               && search.Interaction.CanAcceptTakeover
               && Volatile.Read(ref search.Interaction.Progress)?.CurrentTurnPreview != null
           || PlayerTurnSetupCoordinator.CanApplyCurrentTurn;
    public static bool IsApplyingCurrentTurn
        => _search?.Interaction.IsApplyingCurrentTurn == true
           || PlayerTurnSetupCoordinator.IsApplyingCurrentTurn;
    public static bool CanAdoptCurrentRoute
        => _search is { } search
               && search.Interaction.CurrentTakeoverRequest == null
               && search.Interaction.RenderedRouteAdoptionSeed != null
               && search.Interaction.CanAcceptTakeover
           || HasCurrentStoppedRoute()
           || PlayerTurnSetupCoordinator.CanAdoptCurrentRoute;
    public static bool IsAdoptingCurrentRoute
        => _search?.Interaction.IsAdoptingRoute == true
           || PlayerTurnSetupCoordinator.IsAdoptingCurrentRoute;


    internal static void InvalidateRenderedRouteAdoptionSeed()
    {
        if (_search != null)
            _search.Interaction.RenderedRouteAdoptionSeed = null;
        PlayerTurnSetupCoordinator.InvalidateRenderedRouteAdoptionSeed();
    }

    public static bool CanExecuteCurrentTurn
    {
        get
        {
            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            if (state == null || !CombatManager.Instance.IsInProgress)
                return false;
            return _combat.LatestResult != null
                    && _combat.LatestStamp == LiveCombatStamp.Capture(state)
                || PlayerTurnSetupCoordinator.CanTakeOverTurnSetup(state);
        }
    }

    /// <summary>
    /// True whenever the current run is a networked multiplayer session (host or client).
    /// The solver must stay fully inert in this case: the game's own multiplayer turn
    /// synchronization has no concept of a client silently auto-planning another player's turn.
    /// </summary>
    public static bool IsMultiplayerSession
        => RunManager.Instance.IsInProgress && RunManager.Instance.NetService.Type.IsMultiplayer();

    public static bool FullAutoEnabled => _combat.FullAutoEnabled;
    public static bool AutomaticSearchPaused => _combat.AutomaticSearchPaused;
    public static bool AutomaticCalculationEnabled => SolverSettings.Current.AutomaticCalculationEnabled;
    public static bool HasCalculatedThisCombat => _combat.SearchesStarted > 0 || _combat.LatestResult != null;
    public static bool StopFullAutoOnCombatEnd => _stopFullAutoOnCombatEnd;
    public static bool StopFullAutoOnDeathTurn => _stopFullAutoOnDeathTurn;
    public static bool StopFullAutoOnWorseRecalculation => _stopFullAutoOnWorseRecalculation;
    public static SolverTheftPolicy? TheftPolicy => _combat.TheftPolicy;
    internal static long ReviewedWorldlinesTotal => _combat.ReviewedWorldlinesTotal;
    internal static SolverResult? CurrentResultForBugReport => _combat.LatestResult ?? _combat.ContinuationSource;
    internal static string ReplanAuditForBugReport => DescribeReplanAudit();
    internal static string BuildBugReportDescription(string playerDescription)
        => CombatBugReportDescription.AppendAutomaticClassification(
            playerDescription,
            CombatManager.Instance.IsInProgress || _lastBugReportClassification == null
                ? CaptureBugReportClassification()
                : _lastBugReportClassification);
    internal static int UnexpectedReplanCount
        => _combat.ReplanCounts.GetValueOrDefault(ReplanCause.StateMismatch)
           + _combat.ReplanCounts.GetValueOrDefault(ReplanCause.DeploymentDrift);
    internal static string ControlModeForBugReport
        => _combat.ManualControlObserved
            ? "manual_plus_solver"
            : "solver_only";
    internal static int? LastSolverDeployedTurnForBugReport => _combat.LastSolverDeployedTurn;
    internal static SolverResult? LastCompletedResultForTesting { get; private set; }
    internal static SolverResult? LastTurnSetupResultForTesting { get; private set; }
    internal static Exception? LastSearchFailureForTesting { get; private set; }
    internal static bool LastFullAutoStoppedForWorseRecalculationForTesting { get; private set; }
    internal static bool LastFullAutoStoppedAtLiveRiskForTesting { get; private set; }
    internal static int? LastReusedTurnForTesting { get; private set; }
    internal static int? LastReusedProjectedBattleHpLostForTesting { get; private set; }
    internal static int UnexpectedReplanCountForTesting
        => UnexpectedReplanCount;
    internal static int ManualDivergenceCountForTesting
        => _combat.ReplanCounts.GetValueOrDefault(ReplanCause.ManualDivergence);
    internal static bool ManualRouteImprovementDetected
        => _combat.ManualRouteImprovementDetected;
    internal static bool BugReportUploadRecommended
        => UnexpectedReplanCount > 0
           || _combat.ReplanCounts.GetValueOrDefault(ReplanCause.ContinuationMissing) > 0
           || _combat.ReplanCounts.GetValueOrDefault(ReplanCause.PlanExhausted) > 0
           || _combat.BugReportIssues.RequiresPlayerUpload;
    internal static ManualProjectionComparison? LastManualProjectionComparisonForTesting
        => _combat.LastManualProjectionComparison;
    internal static int NoGcRegionRolloverCountForTesting
        => SearchGcPolicy.RolloverCountForTesting;
    internal static SearchMemoryUsageSnapshot CaptureSearchMemoryUsage()
    {
        SearchMemoryPressureSignal? signal = _search?.MemoryPressureSignal
            ?? PlayerTurnSetupCoordinator.CurrentMemoryPressureSignal;
        SearchMemoryPressureUsage pressure = signal?.CaptureUsage()
            ?? SearchMemoryPressureUsage.Disabled;
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        PhysicalMemoryUsage physicalMemory = PhysicalMemoryUsage.Capture(memory);
        long systemMemoryLimit = pressure.SystemMemoryLimitBytes == long.MaxValue
            ? SearchGcPolicy.ResolveSystemMemoryLimit(memory)
            : pressure.SystemMemoryLimitBytes;
        if (physicalMemory.TotalBytes > 0)
            systemMemoryLimit = Math.Min(systemMemoryLimit, physicalMemory.TotalBytes);
        return new SearchMemoryUsageSnapshot(
            System.Environment.WorkingSet,
            physicalMemory.UsedBytes,
            settings.NoGcRegionBudgetBytes,
            IsSearching,
            pressure.AllocatedBytes,
            pressure.AllocationLimitBytes,
            pressure.ProjectedMemoryLoadBytes,
            systemMemoryLimit,
            pressure.Reclaiming,
            SearchGcPolicy.IsBackgroundReclaiming);
    }
    internal static void LogSearchMemoryDisplayState(
        SearchMemoryUsageSnapshot snapshot,
        string displayState,
        double displayRatio)
    {
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        Entry.Logger.Info(
            $"[CombatSolver/Test] MEMORY_MONITOR_DISPLAY state={displayState} " +
            $"display_ratio={displayRatio:F3} search_active={snapshot.SearchActive.ToString().ToLowerInvariant()} " +
            $"foreground_reclaim={snapshot.Reclaiming.ToString().ToLowerInvariant()} " +
            $"background_reclaim={snapshot.BackgroundReclaiming.ToString().ToLowerInvariant()} " +
            $"search_allocated={snapshot.SearchAllocatedBytes} search_limit={snapshot.SearchAllocationLimitBytes} " +
            $"projected_memory_load={snapshot.ProjectedSystemMemoryLoadBytes} " +
            $"system_memory_limit={snapshot.SystemMemoryLimitBytes} " +
            $"physical_memory_used={snapshot.PhysicalMemoryUsedBytes} " +
            $"system_occupied={snapshot.SystemOccupiedBytes} " +
            $"process_memory_limit={snapshot.ProcessMemoryLimitBytes} " +
            $"system_segment={snapshot.SystemSegmentRatio:F3} " +
            $"process_segment={snapshot.ProcessSegmentRatio:F3} " +
            $"process_pressure={snapshot.ProcessMemoryPressureRatio:F3} " +
            $"allocation_pressure={snapshot.AllocationPressureRatio:F3} " +
            $"system_pressure={snapshot.SystemPressureRatio:F3} " +
            $"system_pressure_dominates={snapshot.SystemPressureDominates.ToString().ToLowerInvariant()} " +
            $"configured_budget={snapshot.ConfiguredMemoryBudgetBytes} " +
            $"working_set={process.WorkingSet64} private_bytes={process.PrivateMemorySize64} " +
            $"managed_live={GC.GetTotalMemory(forceFullCollection: false)} " +
            $"managed_heap={memory.HeapSizeBytes} fragmented={memory.FragmentedBytes} " +
            $"memory_load={memory.MemoryLoadBytes} high_memory_threshold={memory.HighMemoryLoadThresholdBytes} " +
            $"total_available={memory.TotalAvailableMemoryBytes} " +
            $"gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)} " +
            $"latency={GCSettings.LatencyMode} tick_ms={System.Environment.TickCount64}");
    }
    internal static long LastDeployedActionStartedAtMillisecondsForTesting { get; private set; }
    internal static Task LastCombatReferenceReleaseForTesting { get; private set; } = Task.CompletedTask;
    internal static int CombatLifecycleGeneration
        => Volatile.Read(ref _combatLifecycleGeneration);
    internal static int SearchReferenceReleaseScheduledCountForTesting
        => Volatile.Read(ref _searchReferenceReleaseScheduledCountForTesting);
    internal static int SearchReferenceReleaseCompletedCountForTesting
        => Volatile.Read(ref _searchReferenceReleaseCompletedCountForTesting);
    internal static int SearchCtsDisposeCountForTesting
        => Volatile.Read(ref _searchCtsDisposeCountForTesting);
    internal static int PendingSearchReferenceReleaseCountForTesting
        => PendingSearchReferenceReleases.Count(static task => !task.IsCompleted);
    internal static bool WasCardDeployedForTesting(string cardId)
        => DeployedCardIdsForTesting.Contains(cardId);
    internal static bool WasPotionDeployedForTesting(string potionId)
        => DeployedPotionIdsForTesting.Contains(potionId);

    internal static void CancelSearchForTesting()
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("搜索会话取消入口只能在无人测试中使用。");
        CancelSearch();
    }


    private static bool HasCurrentStoppedRoute()
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        return state != null
            && CombatManager.Instance.IsInProgress
            && _combat.StoppedSearch is { StoppedResult: not null, StoppedStamp: { } stamp }
            && stamp == LiveCombatStamp.Capture(state);
    }

    private static bool TryAdoptStoppedRoute()
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        NGame? host = NGame.Instance;
        if (state == null || host == null || _combat.StoppedSearch is not { } stopped)
            return false;
        LiveCombatStamp stamp = LiveCombatStamp.Capture(state);
        SolverResult? result = stopped.TakeStoppedResult(stamp);
        _combat.StoppedSearch = null;
        if (result == null)
            return false;

        _combat.LatestResult = result;
        _combat.LatestStamp = stamp;
        _combat.ContinuationSource = result;
        BattleDamageTracker.RegisterPlan(state, result);
        SolverOverlaySnapshot snapshot = SolverOverlaySnapshot.CaptureWithReviewedWorldlines(
            result,
            UnexpectedReplanCount > 0,
            _combat.ReviewedWorldlinesTotal);
        SolverOverlay.ShowResult(host, MarkRouteAdopted(snapshot));
        SolverOverlay.RefreshControls();
        Entry.Logger.Info(
            $"[CombatSolver/Test] STOPPED_ROUTE_ADOPTED turn={result.StartTurnNumber} actions={result.BestNode.Actions.Count}");
        return true;
    }

    internal static Task StartCombatDeferredOperation(
        Func<CancellationToken, Task> operationFactory)
    {
        AssertMainThread();
        ArgumentNullException.ThrowIfNull(operationFactory);
        CancellationToken token = _combatDeferredOperationCancellation.Token;
        Task operation = RunCombatDeferredOperationAsync(operationFactory, token);
        PendingCombatDeferredOperations.RemoveAll(static task => task.IsCompleted);
        if (!operation.IsCompleted)
            PendingCombatDeferredOperations.Add(operation);
        return operation;
    }

    private static async Task RunCombatDeferredOperationAsync(
        Func<CancellationToken, Task> operationFactory,
        CancellationToken token)
    {
        try
        {
            await operationFactory(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Reset owns cancellation. The operation remains in the reference-release barrier
            // until its state machine has dropped every captured combat/result reference.
        }
    }

    internal static async Task<(
        bool StaleCompletionPreservedCurrentSession,
        bool BarrierWaitedForBothOperations,
        int ReleasesScheduled,
        int ReleasesCompleted,
        int CancellationsDisposed)> ExerciseDeploymentSessionLifecycleForTestingAsync()
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("部署会话生命周期测试只能在无人测试中使用。");
        PendingDeploymentReferenceReleases.RemoveAll(static task => task.IsCompleted);
        if (_deployment != null || PendingDeploymentReferenceReleases.Count != 0)
            throw new InvalidOperationException("部署会话生命周期测试要求控制器初始空闲。");

        int releasesScheduledBefore = Volatile.Read(
            ref _deploymentReferenceReleaseScheduledCountForTesting);
        int releasesCompletedBefore = Volatile.Read(
            ref _deploymentReferenceReleaseCompletedCountForTesting);
        int cancellationsDisposedBefore = Volatile.Read(
            ref _deploymentCtsDisposeCountForTesting);
        TaskCompletionSource firstCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        SolverDeploymentSession first = new() { Operation = firstCompletion.Task };
        _deployment = first;
        CancelDeployment();
        SolverDeploymentSession second = new() { Operation = secondCompletion.Task };
        _deployment = second;
        CompleteDeployment(first);
        bool staleCompletionPreservedCurrentSession = ReferenceEquals(_deployment, second);
        CancelDeployment();
        Task referenceRelease = DrainDeploymentReferenceReleases();
        bool barrierWaitedForBothOperations = !referenceRelease.IsCompleted;
        firstCompletion.TrySetResult();
        barrierWaitedForBothOperations &= !referenceRelease.IsCompleted;
        secondCompletion.TrySetResult();
        await referenceRelease.ConfigureAwait(false);

        return (
            staleCompletionPreservedCurrentSession,
            barrierWaitedForBothOperations,
            Volatile.Read(ref _deploymentReferenceReleaseScheduledCountForTesting)
                - releasesScheduledBefore,
            Volatile.Read(ref _deploymentReferenceReleaseCompletedCountForTesting)
                - releasesCompletedBefore,
            Volatile.Read(ref _deploymentCtsDisposeCountForTesting)
                - cancellationsDisposedBefore);
    }

    internal static void RecordManualProjectionComparisonForTesting(
        int previousProjectedBattleHpLost,
        int currentProjectedBattleHpLost)
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("手操战损比较入口只能在无人测试中使用。");
        RecordManualProjectionComparison(
            new ManualProjectionBaseline(1, previousProjectedBattleHpLost, "test_manual_state_change"),
            currentTurnNumber: 1,
            currentProjectedBattleHpLost);
    }

    internal static bool IsCurrentCombatLifecycle(
        CombatState combat,
        int lifecycleGeneration)
        => lifecycleGeneration == Volatile.Read(ref _combatLifecycleGeneration)
           && CombatManager.Instance.IsInProgress
           && !CombatManager.Instance.IsOverOrEnding
           && ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), combat);

    internal static bool RecordTurnSetupFailure(
        CombatState combat,
        int lifecycleGeneration,
        Exception exception,
        bool parallelSearchWasEnabled = false)
    {
        AssertMainThread();
        if (!IsCurrentCombatLifecycle(combat, lifecycleGeneration))
        {
            Entry.Logger.Info(
                $"[CombatSolver/Test] TURN_SETUP_FAILURE_IGNORED reason=stale_lifecycle " +
                $"operation_epoch={lifecycleGeneration} " +
                $"current_epoch={Volatile.Read(ref _combatLifecycleGeneration)}");
            return false;
        }
        _combat.BugReportIssues.RecordFailure(
            CombatBugReportIssueKind.TurnSetupFailure,
            exception);
        Entry.Logger.Error(
            $"[CombatSolver/Test] TURN_SETUP_FAILURE exception={exception.GetBaseException()}");
        if (NGame.Instance is { } host)
            SolverOverlay.Show(host, FormatTurnSetupFailure(exception, parallelSearchWasEnabled));
        return true;
    }

    internal static bool RecordTurnSetupStateMismatch(
        CombatState combat,
        int lifecycleGeneration,
        string difference)
    {
        AssertMainThread();
        if (!IsCurrentCombatLifecycle(combat, lifecycleGeneration))
        {
            Entry.Logger.Info(
                $"[CombatSolver/Test] TURN_SETUP_MISMATCH_IGNORED reason=stale_lifecycle " +
                $"operation_epoch={lifecycleGeneration} " +
                $"current_epoch={Volatile.Read(ref _combatLifecycleGeneration)}");
            return false;
        }
        _combat.BugReportIssues.Record(
            CombatBugReportIssueKind.TurnSetupStateMismatch,
            difference);
        SolverOverlay.RefreshControls();
        return true;
    }

    public static void ApplyPersistentSettings(SolverSettingsSnapshot settings)
    {
        _solverDisabled = settings.SolverDisabled;
        _stopFullAutoOnCombatEnd = settings.StopFullAutoOnCombatEnd;
        _stopFullAutoOnDeathTurn = settings.StopFullAutoOnDeathTurn;
        _stopFullAutoOnWorseRecalculation = settings.StopFullAutoOnWorseRecalculation;
    }

    internal static SearchPolicySnapshot CaptureSearchPolicy(
        SolverSettingsSnapshot settings,
        CombatState state,
        bool includeTurnSetup,
        SolverTheftPolicy? theftPolicy,
        SearchInteractionState? interaction = null)
    {
        FramePressureSignal.ResetPressure(
            recoveryEnabled: !string.Equals(
                DisplayServer.GetName(),
                "headless",
                StringComparison.OrdinalIgnoreCase));
        int maxDegreeOfParallelism = UnattendedTestRunner.SearchMaxDegreeOfParallelismOverride
            ?? settings.SearchMaxDegreeOfParallelism;
        if (maxDegreeOfParallelism < 1
            || maxDegreeOfParallelism > SolverWeights.MaximumSearchMaxDegreeOfParallelism)
        {
            throw new InvalidOperationException(
                $"搜索并行度必须在 1..{SolverWeights.MaximumSearchMaxDegreeOfParallelism} 之间，" +
                $"实际为 {maxDegreeOfParallelism}。");
        }
        return new SearchPolicySnapshot(
            settings.ShortProfile,
            settings.DeepProfile,
            settings.PotionPolicy,
            CapturePotionStrategy(state, settings.PotionPolicy),
            settings.EnableDetailedDiagnosticLogs,
            UnattendedTestRunner.VerifyIncrementalSearch,
            UnattendedTestRunner.ForceShortSearchOnly,
            UnattendedTestRunner.MeasureSearchPhases,
            maxDegreeOfParallelism,
            UnattendedTestRunner.ShortSearchBudgetOverrideMilliseconds,
            UnattendedTestRunner.DeepSearchBudgetOverrideMilliseconds,
            includeTurnSetup,
            theftPolicy,
            settings.ActTransitionBossHpStrategy,
            settings.FinalBossHpStrategy,
            settings.AcceptableBattleHpLoss,
            settings.PursuedLongTermGoals,
            new SearchDiagnosticsSink(
                message => Entry.Logger.Info(message),
                message => Entry.Logger.Debug(message)),
            FramePressureSignal,
            new SearchMemoryPressureSignal())
        {
            Interaction = interaction,
        };
    }

    public static void BeginCombat(ICombatState? state)
    {
        AssertMainThread();
        ResetCore("combat_starting");
        DeployedCardIdsForTesting.Clear();
        DeployedPotionIdsForTesting.Clear();
        LastDeployedActionStartedAtMillisecondsForTesting = 0;
        NativeChoiceRuntime.ResetTraceForTesting();
        LastTurnSetupResultForTesting = null;
        LastReusedTurnForTesting = null;
        LastReusedProjectedBattleHpLostForTesting = null;
        SearchGcPolicy.ResetCountersForTesting();
        BattleDamageTracker.Begin(state);
        CombatBugReportExporter.BeginCombat(state);
        _combat.TheftPolicy = state is CombatState combat && TheftEncounterStrategy.IsApplicable(combat)
            ? SolverTheftPolicy.PreserveResources
            : null;
        if (state is CombatState activeCombat)
            ReconcilePersistedPotionDirectives(activeCombat);
        Entry.Logger.Info(
            $"[CombatSolver/Test] THEFT_POLICY_INIT policy={_combat.TheftPolicy?.ToString() ?? "-"}");
    }

    private static void RecordReviewedWorldlines(SolverResult result)
    {
        if (!_combat.ReviewedWorldlineResults.Add(result))
            return;
        long reviewed = (long)result.ShortExpandedNodes + result.DeepExpandedNodes;
        _combat.ReviewedWorldlinesTotal = checked(_combat.ReviewedWorldlinesTotal + reviewed);
    }

    internal static bool ActivateTurnSetupResult(
        NGame host,
        CombatState state,
        int lifecycleGeneration,
        SolverResult result)
    {
        AssertMainThread();
        Player? player = LocalContext.GetMe(state);
        if (!IsCurrentCombatLifecycle(state, lifecycleGeneration)
            || _solverDisabled
            || _combat.AutomaticSearchPaused
            || !CombatManager.Instance.IsInProgress
            || state.Players.Count != 1
            || state.CurrentSide != CombatSide.Player
            || player?.PlayerCombatState?.Phase != PlayerTurnPhase.Play
            || result.TurnSetupPlayState is not { } expected
            || ContinuationStamp.CaptureLive(state) != expected)
        {
            Entry.Logger.Info("[CombatSolver/Test] TURN_SETUP_RESULT_REJECT reason=live_state_changed");
            return false;
        }

        LiveCombatStamp stamp = LiveCombatStamp.Capture(state);
        CancelSearch();
        RecordReviewedWorldlines(result);
        _combat.State = state;
        _combat.LatestResult = result;
        _combat.LatestStamp = stamp;
        _combat.ContinuationSource = result.ResultScope == SolverResultScope.CurrentTurnAdoption
            ? null
            : result;
        _combat.SearchesStarted++;
        _combat.ReplanCounts[ReplanCause.InitialSearch] = _combat.ReplanCounts.GetValueOrDefault(ReplanCause.InitialSearch) + 1;
        if (UnattendedTestRunner.IsActive)
        {
            LastCompletedResultForTesting = result;
            LastTurnSetupResultForTesting = result;
        }
        BattleDamageTracker.RegisterPlan(state, result);
        CombatBugReportExporter.RecordCheckpoint(
            state,
            result.ResultScope switch
            {
                SolverResultScope.CurrentTurnAdoption => "turn_setup_current_turn_adopted",
                SolverResultScope.RouteAdoption => "turn_setup_route_adopted",
                _ => "turn_setup_result",
            },
            result,
            DescribeReplanAudit());
        SolverOverlaySnapshot snapshot = result.ResultScope == SolverResultScope.CurrentTurnAdoption
            ? SolverOverlaySnapshot.CaptureCurrentTurn(SolverCurrentTurnPreview.FromResult(result))
            : SolverOverlaySnapshot.CaptureWithReviewedWorldlines(
                result,
                UnexpectedReplanCount > 0,
                _combat.ReviewedWorldlinesTotal);
        if (result.ResultScope == SolverResultScope.RouteAdoption)
            snapshot = MarkRouteAdopted(snapshot);
        SolverOverlay.ShowResult(host, snapshot);
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_SETUP_RESULT_ACCEPTED turn={player.PlayerCombatState.TurnNumber} " +
            $"scope={result.ResultScope}");
        Entry.Logger.Info(SolverDiagnostics.DescribeResult(result));
        if (_combat.FullAutoEnabled
            && result.ResultScope != SolverResultScope.CurrentTurnAdoption)
        {
            Task fullAutoTask = StartCombatDeferredOperation(
                token => StartFullAutoAfterTurnSetupAsync(host, state, result, token));
            if (UnattendedAsyncActivityTracker.IsRequestActive)
                fullAutoTask = UnattendedAsyncActivityTracker.Track(fullAutoTask);
            TaskHelper.RunSafely(fullAutoTask);
        }
        return true;
    }

    internal static void ShowTurnSetupResultPreview(NGame host, SolverResult result)
    {
        AssertMainThread();
        RecordReviewedWorldlines(result);
        SolverOverlaySnapshot snapshot = result.ResultScope == SolverResultScope.CurrentTurnAdoption
            ? SolverOverlaySnapshot.CaptureCurrentTurn(SolverCurrentTurnPreview.FromResult(result))
            : SolverOverlaySnapshot.CaptureWithReviewedWorldlines(
                result,
                UnexpectedReplanCount > 0,
                _combat.ReviewedWorldlinesTotal);
        if (result.ResultScope == SolverResultScope.RouteAdoption)
            snapshot = MarkRouteAdopted(snapshot);
        SolverOverlay.ShowResult(host, snapshot);
        SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Succeeded);
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_SETUP_RESULT_PREVIEW turn={result.StartTurnNumber} " +
            $"scope={result.ResultScope} native_choice_pending=true");
        Entry.Logger.Info(SolverDiagnostics.DescribeResult(result));
    }

    internal static void ShowTurnSetupContinuationPreview(
        NGame host,
        CombatState state,
        int turn)
    {
        AssertMainThread();
        if (!ReferenceEquals(_combat.State, state)
            || _combat.ContinuationSource is not { } source)
        {
            throw new InvalidOperationException("回合准备页面没有可显示的既有跨回合路线。");
        }
        SolverOverlay.ShowResult(
            host,
            SolverOverlaySnapshot.CapturePendingTurnSetup(
                source,
                turn,
                UnexpectedReplanCount > 0,
                _combat.ReviewedWorldlinesTotal));
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_SETUP_RESULT_PREVIEW turn={turn} " +
            "source=continuation native_choice_pending=true");
    }

    internal static void StartDeploymentAfterTurnSetup(
        NGame host,
        CombatState state,
        SolverResult result)
    {
        Task deploymentTask = StartCombatDeferredOperation(
            token => StartDeploymentAfterTurnSetupAsync(host, state, result, token));
        if (UnattendedAsyncActivityTracker.IsRequestActive)
            deploymentTask = UnattendedAsyncActivityTracker.Track(deploymentTask);
        TaskHelper.RunSafely(deploymentTask);
    }

    internal static bool TryGetPlannedTurnSetupChoices(
        CombatState state,
        int turn,
        out IReadOnlyList<PlanCardChoice>? choices)
    {
        AssertMainThread();
        choices = null;
        if (!ReferenceEquals(_combat.State, state)
            || _combat.ContinuationSource is not { } source
            || _combat.LastSolverDeployedTurn != turn - 1
            || !source.Continuations.Any(item => item.StartTurnNumber == turn))
        {
            return false;
        }

        PlanAction? previousEndTurn = source.BestNode.Actions.FirstOrDefault(action =>
            action.Turn == turn - 1
            && (action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn));
        PlanCardChoice[] planned = previousEndTurn?.TurnStartChoices?
            .Where(choice => choice.Timing == PlanChoiceTiming.PlayerTurnStart)
            .ToArray() ?? [];
        if (planned.Length == 0)
            return false;
        choices = planned;
        return true;
    }

    internal static async Task ResumeAfterTurnSetupAsync(
        NGame host,
        CombatState state,
        int lifecycleGeneration,
        int turn,
        bool deployWhenReady = false,
        bool waitForDeploymentDelay = false,
        CancellationToken token = default)
    {
        SolverCombatSession session = _combat;
        session.TurnSetupResumeState = state;
        bool searchRequested = false;
        try
        {
            token.ThrowIfCancellationRequested();
            long deadline = System.Environment.TickCount64 + 30_000;
            while (CombatManager.Instance.IsInProgress
                   && !CombatManager.Instance.IsOverOrEnding
                   && ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
                   && (LocalContext.GetMe(state)?.PlayerCombatState?.Phase != PlayerTurnPhase.Play
                       || CombatManager.Instance.PlayerActionsDisabled
                       || _deployment != null))
            {
                if (System.Environment.TickCount64 >= deadline)
                    throw new TimeoutException("回合准备页面完成后 30 秒内没有进入可复用状态。");
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                token.ThrowIfCancellationRequested();
            }

            if (!IsCurrentCombatLifecycle(state, lifecycleGeneration)
                || _solverDisabled
                || !CombatManager.Instance.IsInProgress
                || CombatManager.Instance.IsOverOrEnding
                || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
                || LocalContext.GetMe(state)?.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } playerState
                || playerState.TurnNumber != turn)
            {
                return;
            }

            if (waitForDeploymentDelay)
                await WaitForTurnStartDeploymentDelayAsync(host, turn, token);

            if (!IsCurrentCombatLifecycle(state, lifecycleGeneration)
                || _solverDisabled
                || !CombatManager.Instance.IsInProgress
                || CombatManager.Instance.IsOverOrEnding
                || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
                || LocalContext.GetMe(state)?.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } resumedState
                || resumedState.TurnNumber != turn)
            {
                return;
            }

            SearchReason resumedReason = session.ManualSearchAfterTurnSetupRequested
                ? SearchReason.Manual
                : SearchReason.AutoTurnStart;
            session.ManualSearchAfterTurnSetupRequested = false;
            session.TurnSetupResumeState = null;
            searchRequested = true;
            RequestSearch(host, state, resumedReason, deployWhenReady);
        }
        finally
        {
            if (ReferenceEquals(session.TurnSetupResumeState, state))
                session.TurnSetupResumeState = null;
            if (!searchRequested && ReferenceEquals(_combat, session))
                session.ManualSearchAfterTurnSetupRequested = false;
        }
    }

    internal static bool ManualSearchAfterTurnSetupRequested
        => _combat.ManualSearchAfterTurnSetupRequested;

    internal static void QueueManualSearchAfterTurnSetup()
    {
        AssertMainThread();
        _combat.ManualSearchAfterTurnSetupRequested = true;
    }

    private static async Task StartFullAutoAfterTurnSetupAsync(
        NGame host,
        CombatState state,
        SolverResult result,
        CancellationToken token)
    {
        long deadline = System.Environment.TickCount64 + 30_000;
        while (CombatManager.Instance.IsInProgress
               && !CombatManager.Instance.IsOverOrEnding
               && (_deployment != null || CombatManager.Instance.PlayerActionsDisabled))
        {
            if (System.Environment.TickCount64 >= deadline)
                throw new TimeoutException("回合准备完成后 30 秒内没有进入可部署状态。");
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            token.ThrowIfCancellationRequested();
        }
        await WaitForTurnStartDeploymentDelayAsync(host, result.StartTurnNumber, token);
        token.ThrowIfCancellationRequested();
        if (_combat.FullAutoEnabled
            && ReferenceEquals(_combat.LatestResult, result)
            && IsSamePlayableTurn(state, result.StartTurnNumber))
        {
            StartFullAutoDeployment(host, state, result);
        }
    }

    private static async Task StartDeploymentAfterTurnSetupAsync(
        NGame host,
        CombatState state,
        SolverResult result,
        CancellationToken token)
    {
        long deadline = System.Environment.TickCount64 + 30_000;
        while (CombatManager.Instance.IsInProgress
               && !CombatManager.Instance.IsOverOrEnding
               && (_deployment != null || CombatManager.Instance.PlayerActionsDisabled))
        {
            if (System.Environment.TickCount64 >= deadline)
                throw new TimeoutException("回合准备完成后 30 秒内没有进入可部署状态。");
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            token.ThrowIfCancellationRequested();
        }
        await WaitForTurnStartDeploymentDelayAsync(host, result.StartTurnNumber, token);
        token.ThrowIfCancellationRequested();
        if (!_combat.FullAutoEnabled
            && ReferenceEquals(_combat.LatestResult, result)
            && IsSamePlayableTurn(state, result.StartTurnNumber))
        {
            StartDeployment(host, state, result);
        }
    }

    public static void RequestSearch(NGame host, CombatState state, SearchReason reason, bool deployWhenReady = false)
    {
        AssertMainThread();
        _combat.StoppedSearch = null;
        SolverDispatcher.Ensure(host);
        int? searchTurn = LocalContext.GetMe(state)?.PlayerCombatState?.TurnNumber;
        if (_combat.DeployAfterTurnSetupTurn == searchTurn)
        {
            deployWhenReady = true;
            _combat.DeployAfterTurnSetupTurn = null;
            Entry.Logger.Info("[CombatSolver/Test] DEPLOY_RESUME reason=turn_setup_completed");
        }
        else if (_combat.DeployAfterTurnSetupTurn != null)
        {
            _combat.DeployAfterTurnSetupTurn = null;
        }
        Task rootCaptureBarrier = SearchGcPolicy.CaptureRootSnapshotBarrier();
        if (!rootCaptureBarrier.IsCompleted)
        {
            DeferSearchUntilRootCaptureBarrier(
                host,
                state,
                reason,
                deployWhenReady,
                rootCaptureBarrier);
            return;
        }
        // A direct request can arrive after the barrier opened but before the deferred
        // callback reached the dispatcher. The newest request owns the search slot.
        CancelDeferredSearch();
        if (reason == SearchReason.Manual)
        {
            if (_combat.AutomaticSearchPaused)
                Entry.Logger.Info("[CombatSolver/Test] AUTOMATIC_SEARCH_RESUMED reason=manual_recalculate");
            _combat.AutomaticSearchPaused = false;
            if (PlayerTurnSetupCoordinator.TryRecalculatePendingChoice(host, state))
            {
                _combat.PendingCompleteProjectionBaseline = null;
                _combat.PendingManualProjectionBaseline = null;
                Entry.Logger.Info(
                    "[CombatSolver/Test] SEARCH_RESTARTED reason=manual_recalculate_pending_turn_setup");
                return;
            }
            bool queuedAfterTurnSetup = PlayerTurnSetupCoordinator.TryQueueManualRecalculation(state);
            if (!queuedAfterTurnSetup && ReferenceEquals(_combat.TurnSetupResumeState, state))
            {
                QueueManualSearchAfterTurnSetup();
                queuedAfterTurnSetup = true;
                Entry.Logger.Info(
                    "[CombatSolver/Test] TURN_SETUP_MANUAL_RECALCULATE_QUEUED phase=resuming_play");
            }
            if (queuedAfterTurnSetup)
            {
                _combat.PendingCompleteProjectionBaseline = null;
                _combat.PendingManualProjectionBaseline = null;
                SolverOverlay.Show(
                    host,
                    "[b]战斗路线求解器[/b]\n等待当前回合开始选择完成后重新计算。");
                Entry.Logger.Info(
                    "[CombatSolver/Test] SEARCH_DEFERRED reason=manual_recalculate_after_turn_setup");
                return;
            }
        }
        else if (_combat.AutomaticSearchPaused)
        {
            _combat.FullAutoEnabled = false;
            Entry.Logger.Info($"[CombatSolver/Test] SEARCH_REJECT reason=user_stopped request={reason}");
            SolverOverlay.ShowSearchStopped(host);
            return;
        }
        ReplanCause replanCause = reason switch
        {
            SearchReason.AutoTurnStart => ReplanCause.InitialSearch,
            SearchReason.DeploymentDrift => ReplanCause.DeploymentDrift,
            SearchReason.PlanExhausted => ReplanCause.PlanExhausted,
            _ => ReplanCause.ExplicitRequest,
        };
        SearchBoundaryReason? previousBoundary = _combat.ContinuationSource?.BoundaryReason;
        if (reason != SearchReason.AutoTurnStart)
        {
            _combat.PendingCompleteProjectionBaseline = null;
            _combat.PendingManualProjectionBaseline = null;
        }
        if (!CanSolve(state, out string rejection))
        {
            SolverOverlay.Show(host, $"[b]战斗路线求解器[/b]\n{rejection}");
            Entry.Logger.Info($"[CombatSolver/Test] SEARCH_REJECT reason={rejection}");
            return;
        }
        CombatBugReportExporter.RecordCheckpoint(
            state,
            $"search_request_{reason}",
            CurrentResultForBugReport,
            DescribeReplanAudit());

        string setupStage = "battle_damage";
        try
        {
            BattleDamageSnapshot battleDamage = BattleDamageTracker.Observe(state);
            setupStage = "live_stamp";
            LiveCombatStamp stamp = LiveCombatStamp.Capture(state);
            if (reason != SearchReason.AutoTurnStart
                && _combat.LatestResult is { } previousResult
                && _combat.LatestStamp is { } previousStamp
                && previousStamp != stamp)
            {
                replanCause = ReplanCause.ManualDivergence;
                _combat.PendingManualProjectionBaseline = new ManualProjectionBaseline(
                    previousResult.StartTurnNumber,
                    previousResult.ProjectedBattleHpLost,
                    "field=live_combat_stamp expected={solver_result} actual={manual_state_change}");
            }
            setupStage = "continuation";
            ContinuationStamp? continuationStamp = reason == SearchReason.AutoTurnStart && _combat.ContinuationSource != null
                ? ContinuationStamp.CaptureLive(state)
                : null;
            if (continuationStamp != null
                && _combat.ContinuationSource!.TryCreateContinuation(
                    continuationStamp,
                    LocalContext.GetMe(state)!.Creature.CurrentHp,
                    battleDamage,
                    out SolverResult? reused))
            {
                CancelSearch();
                _combat.State = state;
                _combat.LatestResult = reused;
                _combat.LatestStamp = stamp;
                _combat.ContinuationsReused++;
                if (UnattendedTestRunner.IsActive)
                {
                    LastCompletedResultForTesting = reused;
                    LastReusedTurnForTesting = reused!.StartTurnNumber;
                    LastReusedProjectedBattleHpLostForTesting = reused.ProjectedBattleHpLost;
                }
                BattleDamageTracker.RegisterPlan(state, reused!);
                CombatBugReportExporter.RecordCheckpoint(
                    state,
                    "search_reused",
                    reused,
                    DescribeReplanAudit());
                SolverOverlay.ShowResult(
                    host,
                    SolverOverlaySnapshot.CaptureWithReviewedWorldlines(
                        reused!,
                        UnexpectedReplanCount > 0,
                        _combat.ReviewedWorldlinesTotal));
                Entry.Logger.Info($"[CombatSolver/Test] SEARCH_REUSED from_turn={reused!.ReusedFromTurn} turn={reused.StartTurnNumber} validation=exact_state_text remaining_turns={reused.SearchedTurns}");
                Entry.Logger.Info(SolverDiagnostics.DescribeResult(reused));
                if (_combat.FullAutoEnabled)
                    StartFullAutoDeployment(host, state, reused);
                else if (deployWhenReady)
                    StartDeployment(host, state, reused);
                return;
            }

            if (continuationStamp != null)
            {
                _combat.PendingCompleteProjectionBaseline = null;
                int currentTurn = LocalContext.GetMe(state)!.PlayerCombatState!.TurnNumber;
                CachedContinuation? expected = _combat.ContinuationSource!.Continuations
                    .FirstOrDefault(item => item.StartTurnNumber == currentTurn);
                _combat.LastContinuationDifferences = expected == null
                    ? ["field=continuation expected={cached_turn_missing} actual={live_turn_present}"]
                    : expected.ExpectedState.DescribeDifferences(continuationStamp);
                string difference = _combat.LastContinuationDifferences[0];
                bool followedBySolver = _combat.LastSolverDeployedTurn == currentTurn - 1;
                replanCause = !followedBySolver
                    ? ReplanCause.ManualDivergence
                    : expected == null
                        ? ReplanCause.ContinuationMissing
                        : ReplanCause.StateMismatch;
                if (replanCause == ReplanCause.ManualDivergence)
                {
                    _combat.PendingManualProjectionBaseline = new ManualProjectionBaseline(
                        _combat.ContinuationSource.StartTurnNumber,
                        _combat.ContinuationSource.ProjectedBattleHpLost,
                        difference);
                }
                if (followedBySolver
                    && _combat.ContinuationSource.BoundaryReason == SearchBoundaryReason.None
                    && _combat.ContinuationSource.CombatEndedTurn.HasValue)
                {
                    _combat.PendingCompleteProjectionBaseline = new CompleteProjectionBaseline(
                        _combat.ContinuationSource.StartTurnNumber,
                        _combat.ContinuationSource.ProjectedBattleHpLost,
                        difference);
                }
                Entry.Logger.Info(
                    $"[CombatSolver/Test] SEARCH_REUSE_MISS turn={currentTurn} " +
                    $"reason={CauseToken(replanCause)} cached_turns={_combat.ContinuationSource.Continuations.Count} " +
                    $"previous_boundary={_combat.ContinuationSource.BoundaryReason} diff_count={_combat.LastContinuationDifferences.Count} {difference}");
                if (SolverSettings.Current.EnableDetailedDiagnosticLogs)
                {
                    for (int index = 0; index < _combat.LastContinuationDifferences.Count; index++)
                    {
                        Entry.Logger.Info(
                            $"[CombatSolver/Debug] STATE_DIFF index={index} " +
                            _combat.LastContinuationDifferences[index]);
                    }
                }
            }

            _combat.ContinuationSource = null;
            CancelSearch();
            SolverSearchSession search = new(
                ++_nextSearchGeneration,
                state,
                stamp,
                deployWhenReady);
            _search = search;
            CancellationToken token = search.Cancellation.Token;
            int generation = search.Generation;
            _combat.SearchesStarted++;
            _combat.ReplanCounts[replanCause] = _combat.ReplanCounts.GetValueOrDefault(replanCause) + 1;
            if (replanCause == ReplanCause.ManualDivergence)
                MarkManualControlObserved("continuation_divergence");
            setupStage = "display_names";
            SolverDisplayNames displayNames = SolverDisplayNames.Capture(state);
            setupStage = "settings";
            SolverSettingsSnapshot settings = SolverSettings.Capture();
            SolverTheftPolicy? theftPolicy = ResolveTheftPolicy(state);
            SearchPolicySnapshot searchPolicy = CaptureSearchPolicy(
                settings,
                state,
                includeTurnSetup: false,
                theftPolicy: theftPolicy,
                interaction: search.Interaction);
            search.MaxDegreeOfParallelism = searchPolicy.MaxDegreeOfParallelism;
            search.MemoryPressureSignal = searchPolicy.MemoryPressureSignal;
            setupStage = "combat_root_snapshot";
            long rootCaptureAllocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
            CombatRootSnapshot rootSnapshot;
            try
            {
                rootSnapshot = CombatRootSnapshot.Capture(state);
            }
            finally
            {
                SearchGcPolicy.ReportCombatLifecycleAllocation(
                    Math.Max(
                        0,
                        GC.GetTotalAllocatedBytes(precise: false) - rootCaptureAllocatedAtStart),
                    "combat_root_snapshot",
                    settings.EnableNoGcRegion);
            }
            Entry.Logger.Info(
                $"[CombatSolver/Test] COMBAT_ROOT_CAPTURE generation={generation} " +
                $"elapsed_ms={rootSnapshot.CaptureElapsedMilliseconds:F3} " +
                $"cards={rootSnapshot.CapturedCardCount} powers={rootSnapshot.CapturedPowerCount} " +
                $"listeners={rootSnapshot.CapturedHookListenerCount} " +
                $"run_mod_subscribers={rootSnapshot.CapturedRunModSubscriberCount} " +
                $"combat_mod_subscribers={rootSnapshot.CapturedCombatModSubscriberCount} " +
                $"base_lib_card_modifiers={rootSnapshot.CapturedBaseLibCardModifiers}");
            _combat.State = state;
            _combat.LatestResult = null;
            _combat.LatestStamp = null;
            LastCompletedResultForTesting = null;
            LastSearchFailureForTesting = null;
            LastFullAutoStoppedForWorseRecalculationForTesting = false;
            LastFullAutoStoppedAtLiveRiskForTesting = false;

            Player player = LocalContext.GetMe(state)!;
            int turn = player.PlayerCombatState!.TurnNumber;
            SolverOverlay.ShowSearching(
                host,
                turn,
                deployWhenReady,
                _combat.ReviewedWorldlinesTotal);
            Entry.Logger.Info(
                $"[CombatSolver/Test] SEARCH_REQUEST generation={generation} reason={reason} " +
                $"cause={CauseToken(replanCause)} previous_boundary={previousBoundary?.ToString() ?? "-"} " +
                $"turn={turn} deploy_when_ready={deployWhenReady} " +
                $"theft_policy={theftPolicy?.ToString() ?? "-"} " +
                $"act_transition_boss_hp_strategy={searchPolicy.ActTransitionBossHpStrategy} " +
                $"final_boss_hp_strategy={searchPolicy.FinalBossHpStrategy} " +
                $"frame_baseline_samples={FramePressureSignal.BaselineSampleCount} " +
                $"frame_baseline_ms={FramePressureSignal.BaselineFrameGapMilliseconds:F1} " +
                $"frame_pressure_threshold_ms={FramePressureSignal.PressureFrameGapMilliseconds:F1} " +
                $"frame_recovery_enabled={FramePressureSignal.RecoveryEnabled} " +
                $"no_gc_enabled={settings.EnableNoGcRegion.ToString().ToLowerInvariant()} " +
                $"no_gc_budget_bytes={settings.NoGcRegionBudgetBytes} " +
                $"max_dop={searchPolicy.MaxDegreeOfParallelism}");
            Entry.Logger.Info(SolverDiagnostics.DescribeStart(
                state,
                settings.ShortProfile,
                settings.DeepProfile));

            setupStage = "worker_schedule";
            Task<SolverResult> solveTask = Task.Run(() =>
            {
                Entry.Logger.Info($"[CombatSolver/Test] SEARCH_WORKER_START generation={generation} thread={System.Environment.CurrentManagedThreadId} main_thread={NGame.IsMainThread()}");
                Thread worker = Thread.CurrentThread;
                ThreadPriority previousPriority = worker.Priority;
                worker.Priority = ThreadPriority.BelowNormal;
                try
                {
                    using IDisposable gcPolicy = SearchGcPolicy.EnterLowLatencySearch(
                        settings.EnableNoGcRegion,
                        settings.NoGcRegionBudgetBytes,
                        searchPolicy.MemoryPressureSignal,
                        token);
                    SolverResult result = CombatSearchCoordinator.Solve(
                        rootSnapshot,
                        displayNames,
                        battleDamage,
                        searchPolicy,
                        token,
                        progress => PublishSearchProgress(search, progress));
                    return search.Interaction.FinalizeWorkerResult(result);
                }
                finally
                {
                    worker.Priority = previousPriority;
                }
            }, token);
            search.WorkerCompletion = solveTask;
            if (!UnattendedAsyncActivityTracker.IsRequestActive)
            {
                // Preserve the production continuation path exactly. The extra lifecycle
                // ownership is needed only while a reusable unattended request is active.
                solveTask.ContinueWith(task =>
                {
                    SolverDispatcher.Post(() => CompleteSearch(host, search, task));
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                search.CallbackScheduled = true;
                return;
            }

            IDisposable? unattendedActivity = UnattendedAsyncActivityTracker.BeginActivity();
            solveTask.ContinueWith(task =>
            {
                try
                {
                    SolverDispatcher.Post(() =>
                    {
                        try
                        {
                            CompleteSearch(host, search, task);
                        }
                        finally
                        {
                            unattendedActivity?.Dispose();
                        }
                    });
                }
                catch
                {
                    unattendedActivity?.Dispose();
                    throw;
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            search.CallbackScheduled = true;
        }
        catch (Exception ex)
        {
            _combat.BugReportIssues.RecordFailure(CombatBugReportIssueKind.SearchSetupFailure, ex);
            CancelSearch();
            _combat.State = null;
            _combat.LatestResult = null;
            _combat.LatestStamp = null;
            _combat.ContinuationSource = null;
            _combat.PendingCompleteProjectionBaseline = null;
            _combat.PendingManualProjectionBaseline = null;
            SolverOverlay.Show(
                host,
                FormatSearchSetupFailure(ex));
            SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Failed);
            Entry.Logger.Error(
                $"[CombatSolver/Test] SEARCH_SETUP_FAILURE stage={setupStage} " +
                $"reason={reason} exception={ex}");
        }
    }

    internal static string FormatSearchSetupFailure(Exception exception)
    {
        string title = $"[color={SolverUiTokens.Palette.DangerHex}][b]搜索初始化失败[/b][/color]";
        if (exception is not IncompatibleGameplayModException incompatible)
        {
            return $"{title}\n[color={SolverUiTokens.Palette.DangerHex}]{EscapeRichText(exception.Message)}[/color]" +
                   $"\n{SolverUiTokens.BugReportUploadInstructionRichText}";
        }

        string modName = EscapeRichText(incompatible.PlayerFacingModName);
        return $"{title}\n[color={SolverUiTokens.Palette.DangerHex}]检测到不兼容的第三方 Mod：{modName}。" +
               $"建议卸载该 Mod 并重启游戏后再使用求解器。[/color]\n" +
               SolverUiTokens.BugReportUploadInstructionRichText;
    }

    internal static string FormatSearchFailureForTesting(
        Exception exception,
        bool parallelSearchWasEnabled)
        => FormatSearchFailure(exception, parallelSearchWasEnabled);

    private static string EscapeRichText(string value)
        => value.Replace('[', '［').Replace(']', '］');

    public static void RequestDeploy(NGame host, CombatState state)
    {
        AssertMainThread();
        SolverDispatcher.Ensure(host);
        if (_deployment != null)
        {
            Entry.Logger.Info("[CombatSolver/Test] DEPLOY_REJECT reason=already_deploying");
            return;
        }
        if (PlayerTurnSetupCoordinator.TryContinuePlannedChoice(
                host,
                state,
                deployAfterSetup: true))
        {
            Entry.Logger.Info("[CombatSolver/Test] DEPLOY_WAIT reason=turn_setup_choice");
            return;
        }
        Player? turnStartPlayer = LocalContext.GetMe(state);
        if (state.CurrentSide == CombatSide.Player
            && turnStartPlayer?.PlayerCombatState?.Phase == PlayerTurnPhase.Start)
        {
            _combat.DeployAfterTurnSetupTurn = turnStartPlayer.PlayerCombatState.TurnNumber;
            Entry.Logger.Info("[CombatSolver/Test] DEPLOY_WAIT reason=turn_setup_pending");
            return;
        }
        if (!CanSolve(state, out string rejection))
        {
            SolverOverlay.Show(host, $"[b]战斗路线求解器[/b]\n{rejection}");
            Entry.Logger.Info($"[CombatSolver/Test] DEPLOY_REJECT reason={rejection}");
            return;
        }

        LiveCombatStamp current = LiveCombatStamp.Capture(state);
        if (_combat.LatestResult != null && _combat.LatestStamp == current)
        {
            StartDeployment(host, state, _combat.LatestResult);
            return;
        }

        if (_search is { } search
            && ReferenceEquals(search.State, state)
            && search.Stamp == current)
        {
            search.DeployWhenReady = true;
            Player player = LocalContext.GetMe(state)!;
            SolverOverlay.ShowSearching(
                host,
                player.PlayerCombatState!.TurnNumber,
                deployWhenReady: true,
                _combat.ReviewedWorldlinesTotal);
            Entry.Logger.Info($"[CombatSolver/Test] DEPLOY_WAIT generation={search.Generation}");
            return;
        }

        if ((_combat.LatestResult != null || _search != null) && _combat.LatestStamp != current)
            MarkManualControlObserved("deploy_after_live_state_change");

        RequestSearch(host, state, SearchReason.Deploy, deployWhenReady: true);
    }

    public static void SetFullAuto(NGame host, CombatState state, bool enabled)
    {
        AssertMainThread();
        SolverDispatcher.Ensure(host);
        if (!enabled)
        {
            _combat.FullAutoEnabled = false;
            Entry.Logger.Info("[CombatSolver/Test] FULL_AUTO enabled=false reason=user");
            SolverOverlay.RefreshControls();
            return;
        }

        if (_combat.AutomaticSearchPaused)
        {
            _combat.FullAutoEnabled = false;
            Entry.Logger.Info("[CombatSolver/Test] FULL_AUTO_REJECT reason=user_stopped");
            SolverOverlay.ShowSearchStopped(host);
            return;
        }

        if (PlayerTurnSetupCoordinator.CanTakeOverTurnSetup(state))
        {
            _combat.FullAutoEnabled = true;
            Entry.Logger.Info(
                $"[CombatSolver/Test] FULL_AUTO enabled=true reason=turn_setup_takeover " +
                $"stop_on_combat_end={_stopFullAutoOnCombatEnd} " +
                $"stop_on_death_turn={_stopFullAutoOnDeathTurn} " +
                $"stop_on_worse_recalculation={_stopFullAutoOnWorseRecalculation}");
            SolverOverlay.RefreshControls();
            if (!PlayerTurnSetupCoordinator.TryContinuePlannedChoice(
                    host,
                    state,
                    deployAfterSetup: false))
            {
                InvalidOperationException failure = new("回合开始选牌页在全自动接管时失去活动状态。");
                _ = RecordTurnSetupFailure(
                    state,
                    Volatile.Read(ref _combatLifecycleGeneration),
                    failure);
                throw failure;
            }
            return;
        }

        if (!CanSolve(state, out string rejection))
        {
            SolverOverlay.Show(host, $"[b]战斗路线求解器[/b]\n{rejection}");
            Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_REJECT reason={rejection}");
            return;
        }

        int currentTurn = LocalContext.GetMe(state)?.PlayerCombatState?.TurnNumber ?? 0;
        if (currentTurn > 1 && _combat.LastSolverDeployedTurn != currentTurn - 1)
            MarkManualControlObserved("full_auto_after_manual_turn");

        _combat.FullAutoEnabled = true;
        Entry.Logger.Info(
            $"[CombatSolver/Test] FULL_AUTO enabled=true stop_on_combat_end={_stopFullAutoOnCombatEnd} " +
            $"stop_on_death_turn={_stopFullAutoOnDeathTurn} " +
            $"stop_on_worse_recalculation={_stopFullAutoOnWorseRecalculation}");
        SolverOverlay.RefreshControls();

        LiveCombatStamp current = LiveCombatStamp.Capture(state);
        if (_combat.LatestResult != null && _combat.LatestStamp == current)
        {
            StartFullAutoDeployment(host, state, _combat.LatestResult);
            return;
        }
        if (_search == null && _deployment == null)
            RequestSearch(host, state, SearchReason.FullAuto);
    }

    public static bool PrepareAutomaticSearchForTurn(NGame host, CombatState state)
    {
        AssertMainThread();
        int turn = LocalContext.GetMe(state)?.PlayerCombatState?.TurnNumber ?? -1;
        if (_combat.AutomaticSearchPaused
            && _combat.AutomaticSearchPausedTurn is { } stoppedTurn
            && stoppedTurn != turn)
        {
            _combat.AutomaticSearchPaused = false;
            _combat.AutomaticSearchPausedTurn = null;
            Entry.Logger.Info(
                $"[CombatSolver/Test] AUTOMATIC_SEARCH_STOP_CLEARED stopped_turn={stoppedTurn} current_turn={turn}");
        }
        if (!AutomaticCalculationEnabled)
        {
            SolverOverlay.ShowManualCalculationReady(host, HasCalculatedThisCombat);
            return false;
        }
        if (_combat.AutomaticSearchPaused)
        {
            SolverOverlay.ShowSearchStopped(host);
            return false;
        }
        return true;
    }

    public static void SetAutomaticCalculationEnabled(bool enabled, bool persist = true)
    {
        AssertMainThread();
        if (persist)
        {
            SolverSettings.Update(SolverSettings.Current with
            {
                AutomaticCalculationEnabled = enabled,
            });
        }
        if (!enabled)
            _combat.FullAutoEnabled = false;
        Entry.Logger.Info(
            $"[CombatSolver/Test] AUTOMATIC_CALCULATION enabled={enabled.ToString().ToLowerInvariant()}");
        SolverOverlay.RefreshControls();

        NGame? host = NGame.Instance;
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (host == null || state == null || !CombatManager.Instance.IsInProgress)
            return;
        if (!enabled)
        {
            if (!IsSearching)
                SolverOverlay.ShowManualCalculationReady(host, HasCalculatedThisCombat);
            return;
        }
        if (!_combat.AutomaticSearchPaused
            && !IsSearching
            && !IsDeploying
            && UnattendedTestRunner.AutomaticTurnSearchEnabled
            && CanSolve(state, out _))
        {
            RequestSearch(host, state, SearchReason.AutoTurnStart);
        }
    }

    public static void SetSolverDisabled(bool disabled, bool persist = true)
    {
        AssertMainThread();
        _solverDisabled = disabled;
        if (persist)
            SolverSettings.Update(SolverSettings.Current with { SolverDisabled = disabled });

        Entry.Logger.Info($"[CombatSolver/Test] SOLVER_DISABLED disabled={disabled}");
        if (disabled)
        {
            bool controllerSearchCanceled = _search != null || _deferredSearchCts != null;
            PlayerTurnSetupCoordinator.CancelForSolverDisabled();
            CancelDeferredSearch();
            CancelSearch();
            if (controllerSearchCanceled)
                SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Canceled);
            CancelDeployment();
            _combat.FullAutoEnabled = false;
            _combat.State = null;
            _combat.LatestResult = null;
            _combat.LatestStamp = null;
            _combat.ContinuationSource = null;
            _combat.PendingCompleteProjectionBaseline = null;
            if (NGame.Instance is { } host)
                SolverOverlay.ShowDisabled(host);
            else
                SolverOverlay.RefreshControls();
            return;
        }

        SolverOverlay.RefreshControls();
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        NGame? game = NGame.Instance;
        if (game != null
            && state != null
            && UnattendedTestRunner.AutomaticTurnSearchEnabled
            && AutomaticCalculationEnabled
            && CanSolve(state, out _))
        {
            RequestSearch(game, state, SearchReason.AutoTurnStart);
        }
    }

    public static void StopSearchByUser(NGame host)
    {
        AssertMainThread();
        if (!IsSearching)
            return;

        bool controllerSearchCanceled = _search != null || _deferredSearchCts != null;
        int? generation = _search?.Generation;
        _combat.FullAutoEnabled = false;
        _combat.AutomaticSearchPaused = true;
        _combat.AutomaticSearchPausedTurn = LocalContext.GetMe(
            CombatManager.Instance.DebugOnlyGetState())?.PlayerCombatState?.TurnNumber;
        CancelDeferredSearch();
        bool stoppingControllerAtCandidate = _search is { } search
            && search.Interaction.RenderedRouteAdoptionSeed is { } seed
            && search.Interaction.RequestAdoptRoute(seed, stopAfterResult: true);
        bool stoppingSetupAtCandidate = _search == null
            && PlayerTurnSetupCoordinator.TryStopSearchAtCurrentRoute();
        bool stoppingAtCandidate = stoppingControllerAtCandidate || stoppingSetupAtCandidate;
        if (!stoppingAtCandidate)
            CancelSearch();
        bool stoppedTurnSetupSearch = stoppingSetupAtCandidate
            || !stoppingAtCandidate && PlayerTurnSetupCoordinator.StopSearchByUser();
        if (!stoppingAtCandidate && (controllerSearchCanceled || stoppedTurnSetupSearch))
            SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Canceled);
        _combat.PendingCompleteProjectionBaseline = null;
        _combat.PendingManualProjectionBaseline = null;
        SolverOverlay.ShowSearchStopped(host);
        Entry.Logger.Info(
            $"[CombatSolver/Test] SEARCH_STOPPED_BY_USER generation={generation?.ToString() ?? "-"} " +
            $"turn_setup={stoppedTurnSetupSearch.ToString().ToLowerInvariant()} " +
            $"candidate_preserved={stoppingAtCandidate.ToString().ToLowerInvariant()} automatic_search_paused=true");
    }

    public static void ApplyCurrentTurn()
    {
        AssertMainThread();
        _combat.AutomaticSearchPaused = false;
        _combat.AutomaticSearchPausedTurn = null;
        _combat.FullAutoEnabled = false;
        if (_search is not { } search)
        {
            PlayerTurnSetupCoordinator.ApplyCurrentTurn();
            return;
        }
        if (search.Interaction.CurrentTakeoverRequest != null
            || Volatile.Read(ref search.Interaction.Progress)?.CurrentTurnPreview == null)
        {
            return;
        }

        search.DeployWhenReady = true;
        if (!search.Interaction.RequestApplyCurrentTurn())
            return;
        SolverOverlay.RefreshControls();
        Entry.Logger.Info("[CombatSolver/Test] UI_ACTION action=apply_current_turn");
    }

    public static void AdoptCurrentRoute()
    {
        AssertMainThread();
        _combat.AutomaticSearchPaused = false;
        _combat.AutomaticSearchPausedTurn = null;
        if (_search is not { } search)
        {
            if (TryAdoptStoppedRoute())
                return;
            PlayerTurnSetupCoordinator.AdoptCurrentRoute();
            return;
        }
        SolverRouteAdoptionSeed? seed = search.Interaction.RenderedRouteAdoptionSeed;
        if (seed == null || !search.Interaction.RequestAdoptRoute(seed))
            return;
        SolverOverlay.RefreshControls();
        Entry.Logger.Info(
            $"[CombatSolver/Test] UI_ACTION action=adopt_current_route " +
            $"candidate_version={seed.CandidateVersion}");
    }

    public static void SetStopFullAutoOnCombatEnd(bool enabled, bool persist = true)
    {
        AssertMainThread();
        _stopFullAutoOnCombatEnd = enabled;
        if (persist)
            SolverSettings.Update(SolverSettings.Current with { StopFullAutoOnCombatEnd = enabled });
        Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_COMBAT_END_STOP enabled={enabled}");
        SolverOverlay.RefreshControls();
    }

    public static void SetStopFullAutoOnDeathTurn(bool enabled, bool persist = true)
    {
        AssertMainThread();
        _stopFullAutoOnDeathTurn = enabled;
        if (persist)
            SolverSettings.Update(SolverSettings.Current with { StopFullAutoOnDeathTurn = enabled });
        Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_DEATH_TURN_STOP enabled={enabled}");
        SolverOverlay.RefreshControls();
    }

    public static void SetStopFullAutoOnWorseRecalculation(bool enabled, bool persist = true)
    {
        AssertMainThread();
        _stopFullAutoOnWorseRecalculation = enabled;
        if (persist)
        {
            SolverSettings.Update(SolverSettings.Current with
            {
                StopFullAutoOnWorseRecalculation = enabled,
            });
        }
        Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_WORSE_RECALCULATION_STOP enabled={enabled}");
        SolverOverlay.RefreshControls();
    }

    public static void SetTheftPolicy(NGame host, CombatState state, SolverTheftPolicy policy)
    {
        AssertMainThread();
        if (!TheftEncounterStrategy.IsApplicable(state))
            throw new InvalidOperationException("当前战斗不支持偷窃路线策略。");
        if (_deployment != null)
        {
            Entry.Logger.Info("[CombatSolver/Test] THEFT_POLICY_REJECT reason=deploying");
            return;
        }
        if (_combat.TheftPolicy == policy)
            return;

        SolverTheftPolicy? previous = _combat.TheftPolicy;
        _combat.TheftPolicy = policy;
        _combat.ContinuationSource = null;
        _combat.PendingCompleteProjectionBaseline = null;
        Entry.Logger.Info(
            $"[CombatSolver/Test] THEFT_POLICY_CHANGED previous={previous?.ToString() ?? "-"} current={policy}");
        SolverOverlay.RefreshControls();
        if (_combat.AutomaticSearchPaused || !AutomaticCalculationEnabled)
        {
            Entry.Logger.Info("[CombatSolver/Test] THEFT_POLICY_RECALCULATION_SKIPPED reason=automatic_calculation_inactive");
            return;
        }
        RequestSearch(host, state, SearchReason.Manual);
    }

    internal static PotionStrategySnapshot CapturePotionStrategy(
        CombatState state,
        SolverPotionPolicy defaultPolicy)
    {
        AssertMainThread();
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("当前战斗找不到本地玩家。");
        List<PotionSlotDirective> directives = [];
        foreach (PersistedPotionDirective persisted in SolverSettings.Current.PotionDirectives)
        {
            PotionModel? potion = GetPotionAtExistingSlot(player, persisted.Slot);
            if (potion != null
                && string.Equals(potion.Id.Entry, persisted.PotionId, StringComparison.Ordinal))
            {
                directives.Add(new PotionSlotDirective(
                    persisted.Slot,
                    persisted.PotionId,
                    persisted.Directive));
            }
        }
        return new PotionStrategySnapshot(defaultPolicy, directives);
    }

    internal static SolverPotionDirective ResolvePotionDirective(
        CombatState state,
        int slot,
        string potionId)
        => SolverSettings.ResolvePotionDirective(slot, potionId);

    internal static void SetPotionDirective(
        NGame host,
        CombatState state,
        int slot,
        string potionId,
        SolverPotionDirective directive)
    {
        AssertMainThread();
        if (!Enum.IsDefined(directive))
            throw new ArgumentOutOfRangeException(nameof(directive));
        if (_deployment != null)
        {
            Entry.Logger.Info("[CombatSolver/Test] POTION_DIRECTIVE_REJECT reason=deploying");
            return;
        }
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("当前战斗找不到本地玩家。");
        PotionModel potion = player.GetPotionAtSlotIndex(slot)
            ?? throw new InvalidOperationException($"药水槽位 {slot} 为空。");
        if (!string.Equals(potion.Id.Entry, potionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"药水槽位 {slot} 为 {potion.Id.Entry}，预期 {potionId}。");
        }

        if (SolverSettings.ResolvePotionDirective(slot, potionId) == directive)
            return;
        SolverSettings.Update(SolverSettings.ApplyPotionDirective(
            SolverSettings.Current,
            slot,
            potionId,
            directive));
        _combat.ContinuationSource = null;
        _combat.PendingCompleteProjectionBaseline = null;
        Entry.Logger.Info(
            $"[CombatSolver/Test] POTION_DIRECTIVE_CHANGED slot={slot} potion={potionId} directive={directive}");
        SolverOverlay.RefreshControls();
        if (_combat.AutomaticSearchPaused || !AutomaticCalculationEnabled)
        {
            Entry.Logger.Info("[CombatSolver/Test] POTION_DIRECTIVE_RECALCULATION_SKIPPED reason=automatic_calculation_inactive");
            return;
        }
        RequestSearch(host, state, SearchReason.Manual);
    }

    internal static void SetPotionDirectiveForTesting(
        CombatState state,
        int slot,
        string potionId,
        SolverPotionDirective directive)
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("药水策略测试覆盖只能在无人测试中使用。");
        PotionModel potion = LocalContext.GetMe(state)?.GetPotionAtSlotIndex(slot)
            ?? throw new InvalidOperationException($"测试药水槽位 {slot} 为空。");
        if (!string.Equals(potion.Id.Entry, potionId, StringComparison.Ordinal))
            throw new InvalidOperationException($"测试药水槽位 {slot} 与 {potionId} 不一致。");
        SolverSettings.ApplyForTesting(SolverSettings.ApplyPotionDirective(
            SolverSettings.Current,
            slot,
            potionId,
            directive));
    }

    private static void ReconcilePersistedPotionDirectives(CombatState state)
    {
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("当前战斗找不到本地玩家。");
        SolverSettingsData settings = SolverSettings.Current;
        PersistedPotionDirective[] retained = settings.PotionDirectives
            .Where(directive =>
            {
                PotionModel? potion = GetPotionAtExistingSlot(player, directive.Slot);
                return potion != null
                    && string.Equals(potion.Id.Entry, directive.PotionId, StringComparison.Ordinal);
            })
            .ToArray();
        if (retained.Length == settings.PotionDirectives.Length)
            return;
        SolverSettings.Update(settings with { PotionDirectives = retained });
        Entry.Logger.Info(
            $"[CombatSolver/Test] POTION_DIRECTIVES_RECONCILED " +
            $"previous={settings.PotionDirectives.Length} current={retained.Length}");
    }

    private static PotionModel? GetPotionAtExistingSlot(Player player, int slot)
        => slot >= 0 && slot < player.PotionSlots.Count
            ? player.PotionSlots[slot]
            : null;

    internal static SolverTheftPolicy? ResolveTheftPolicy(CombatState state)
        => TheftEncounterStrategy.IsApplicable(state)
            ? _combat.TheftPolicy ?? SolverTheftPolicy.PreserveResources
            : null;

    internal static void SetTheftPolicyForTesting(CombatState state, SolverTheftPolicy policy)
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("偷窃策略测试覆盖只能在无人测试中使用。");
        if (!TheftEncounterStrategy.IsApplicable(state))
            throw new InvalidOperationException("测试战斗不是偷窃资源战斗。");
        _combat.TheftPolicy = policy;
        Entry.Logger.Info($"[CombatSolver/Test] THEFT_POLICY_TEST_OVERRIDE policy={policy}");
        SolverOverlay.RefreshControls();
    }

    public static void Reset(string reason = "unspecified")
    {
        AssertMainThread();
        ResetCore(reason);
    }

    private static void ResetCore(string reason)
    {
        int lifecycleGeneration = Interlocked.Increment(ref _combatLifecycleGeneration);
        bool deferredSearchCanceled = _deferredSearchCts != null;
        CancelDeferredSearch();
        Task deferredSearchRelease = DrainDeferredSearchReleases();
        Task combatDeferredRelease = CancelAndDrainCombatDeferredOperations();
        bool searchCanceled = deferredSearchCanceled
            || _search != null
            || PlayerTurnSetupCoordinator.IsSearching;
        Task turnSetupRelease = PlayerTurnSetupCoordinator.Reset(reason);
        long forensicCaptureAllocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
        Task forensicCaptureRelease = CombatBugReportExporter.CompleteCombat(
            reason,
            CurrentResultForBugReport,
            DescribeReplanAudit());
        bool automaticGcLifecycleUsed = SearchGcPolicy.AutomaticGcLifecycleUsed;
        SearchGcPolicy.ReportCombatLifecycleAllocation(
            Math.Max(
                0,
                GC.GetTotalAllocatedBytes(precise: false) - forensicCaptureAllocatedAtStart),
            "combat_forensic_capture",
            automaticGcLifecycleUsed);
        SearchGcPolicy.CombatLifecyclePressure lifecyclePressure =
            SearchGcPolicy.DetachCombatLifecyclePressure(reason);
        bool hadState = _search != null
            || _deployment != null
            || _combat.State != null
            || _combat.LatestResult != null
            || SolverOverlay.IsVisible;
        if (_combat.SearchesStarted > 0 || _combat.ContinuationsReused > 0)
            Entry.Logger.Info($"[CombatSolver/Test] REPLAN_SUMMARY reason={reason} {DescribeReplanCounts()}");
        _lastBugReportClassification = CaptureBugReportClassification();
        CancelSearch();
        Task searchReferenceRelease = DrainSearchReferenceReleases();
        if (searchCanceled)
            SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Canceled);
        CancelDeployment();
        Task deploymentReferenceRelease = DrainDeploymentReferenceReleases();
        _combat = new SolverCombatSession();
        LastFullAutoStoppedForWorseRecalculationForTesting = false;
        LastFullAutoStoppedAtLiveRiskForTesting = false;
        LastSearchFailureForTesting = null;
        BattleDamageTracker.Reset();
        SolverOverlay.Hide();
        bool unattendedRequestActive = UnattendedAsyncActivityTracker.IsRequestActive;
        Task regionExit = unattendedRequestActive
            ? Task.CompletedTask
            : SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync(reason);
        // The unattended protocol performs one reclaim only after the game and every tracked
        // callback are quiescent. Starting a fire-and-forget reclaim here would make the 0.18
        // serialized reclaim chain request a second collection when the protocol joins it.
        Task referenceRelease = Task.WhenAll(
            searchReferenceRelease,
            deploymentReferenceRelease,
            deferredSearchRelease,
            combatDeferredRelease,
            turnSetupRelease,
            forensicCaptureRelease,
            regionExit);
        LastCombatReferenceReleaseForTesting = referenceRelease;
        if (unattendedRequestActive)
        {
            TaskHelper.RunSafely(UnattendedAsyncActivityTracker.Track(referenceRelease));
        }
        else if (automaticGcLifecycleUsed
                 && (hadState
                     || searchCanceled
                     || !referenceRelease.IsCompletedSuccessfully
                     || lifecyclePressure.AllocatedBytes > 0))
        {
            Stopwatch referenceReleaseStopwatch = Stopwatch.StartNew();
            TaskHelper.RunSafely(SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
                reason,
                lifecyclePressure.RequiresCollection,
                includeCombatLifecyclePressure: false,
                referenceRelease,
                () => LogCombatReferenceBarrier(
                    reason,
                    lifecycleGeneration,
                    lifecyclePressure,
                    referenceReleaseStopwatch)));
        }
        if (hadState)
            Entry.Logger.Info($"[CombatSolver/Test] RESET reason={reason}");
    }

    private static async Task ReleaseSearchSessionAsync(SolverSearchSession? search)
    {
        if (search == null)
            return;
        try
        {
            await search.WorkerCompletion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await search.CallbackCompletion.Task.ConfigureAwait(false);
            search.WorkerCompletion = Task.CompletedTask;
        }
        finally
        {
            DisposeSearchCancellationOnce(search);
            Interlocked.Increment(ref _searchReferenceReleaseCompletedCountForTesting);
        }
    }

    private static void QueueSearchReferenceRelease(SolverSearchSession search)
    {
        if (Interlocked.Exchange(ref search.ReferenceReleaseState, 1) != 0)
            return;
        PendingSearchReferenceReleases.RemoveAll(static task => task.IsCompleted);
        Interlocked.Increment(ref _searchReferenceReleaseScheduledCountForTesting);
        PendingSearchReferenceReleases.Add(ReleaseSearchSessionAsync(search));
    }

    private static Task DrainSearchReferenceReleases()
    {
        if (PendingSearchReferenceReleases.Count == 0)
            return Task.CompletedTask;
        Task[] releases = [.. PendingSearchReferenceReleases];
        PendingSearchReferenceReleases.Clear();
        return Task.WhenAll(releases);
    }

    private static void DisposeSearchCancellationOnce(SolverSearchSession search)
    {
        if (Interlocked.Exchange(ref search.CancellationDisposeState, 1) != 0)
            return;
        search.Cancellation.Dispose();
        Interlocked.Increment(ref _searchCtsDisposeCountForTesting);
    }

    private static void LogCombatReferenceBarrier(
        string reason,
        int lifecycleGeneration,
        SearchGcPolicy.CombatLifecyclePressure lifecyclePressure,
        Stopwatch stopwatch)
    {
        int currentLifecycleGeneration = Volatile.Read(ref _combatLifecycleGeneration);
        Entry.Logger.Info(
            $"[CombatSolver/Test] HEAP_REFERENCE_BARRIER reason={reason} " +
            $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1} " +
            $"lifecycle_epoch={lifecycleGeneration} " +
            $"current_lifecycle_epoch={currentLifecycleGeneration} " +
            $"crossed_combat={(currentLifecycleGeneration != lifecycleGeneration).ToString().ToLowerInvariant()} " +
            $"lifecycle_allocated_bytes={lifecyclePressure.AllocatedBytes} " +
            $"lifecycle_requires_gen2={lifecyclePressure.RequiresCollection.ToString().ToLowerInvariant()} " +
            $"managed_live_bytes={GC.GetTotalMemory(forceFullCollection: false)}");
    }

    internal static void SimulateDeploymentCompletionForTesting(NGame host, CombatState state)
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive
            || !ReferenceEquals(_combat.State, state)
            || _combat.LatestResult is not { } result
            || _combat.LatestStamp != LiveCombatStamp.Capture(state))
        {
            throw new InvalidOperationException("无人测试无法模拟消费当前就绪路线。");
        }
        int actionCount = result.BestNode.Actions.Count(action =>
            action.Turn == result.StartTurnNumber && action.IsExecutable);
        _combat.LatestResult = null;
        _combat.LatestStamp = null;
        SolverOverlay.ShowDeploymentComplete(
            host,
            result.StartTurnNumber,
            actionCount,
            endedTurn: true);
    }

    internal static void ReleaseUnattendedResultReferencesForTesting()
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive)
        {
            throw new InvalidOperationException(
                "无人测试结果引用只能在活动请求的最终清理阶段释放。");
        }
        // Executor assertions intentionally inspect the final result after combat teardown.
        // Release it once those assertions are finished and before the protocol's reuse Gen2;
        // IntentForecast can otherwise retain live creatures and move states from the old fight.
        LastCompletedResultForTesting = null;
        LastTurnSetupResultForTesting = null;
        LastSearchFailureForTesting = null;
    }

    public static void MonitorCombatPresence()
    {
        AssertMainThread();
        if (_combat.State == null && !SolverOverlay.IsVisible)
            return;

        CombatState? current = CombatManager.Instance.DebugOnlyGetState();
        if (!CombatManager.Instance.IsInProgress || current == null)
        {
            Reset("combat_inactive");
            return;
        }

        if (_combat.State != null && !ReferenceEquals(current, _combat.State))
        {
            Reset("combat_replaced");
            return;
        }
        BattleDamageTracker.Observe(current);
    }

    public static void RefreshSearchProgress()
    {
        AssertMainThread();
        if (_search is not { } search || !SolverOverlay.IsVisible
            || !search.Interaction.TryCreateDisplayProgress(
                System.Environment.TickCount64,
                out SolverProgress progress))
        {
            return;
        }
        SolverOverlaySnapshot? preview = progress.SpeculativeRoutePreview is { } speculative
            ? SolverOverlaySnapshot.CaptureSpeculativeRoute(speculative)
            : progress.CurrentTurnPreview is { } currentTurn
                ? SolverOverlaySnapshot.CaptureCurrentTurn(currentTurn)
                : null;
        search.Interaction.RenderedRouteAdoptionSeed = progress.SpeculativeRoutePreview == null
            ? null
            : progress.RouteAdoptionSeed;
        SolverOverlay.ShowProgress(
            progress,
            search.DeployWhenReady,
            _combat.ReviewedWorldlinesTotal,
            preview);
        SolverOverlay.RefreshControls();
    }

    public static void ObserveMainThreadFrameGap(TimeSpan gap)
    {
        AssertMainThread();
        double milliseconds = gap.TotalMilliseconds;
        SolverSearchSession? search = _search;
        bool frameRecoveryAllowed = !string.Equals(
                DisplayServer.GetName(),
                "headless",
                StringComparison.OrdinalIgnoreCase)
            && DisplayServer.WindowIsFocused();
        FramePressureSignal.ObserveFrame(
            milliseconds,
            searchActive: IsSearching,
            frameRecoveryAllowed);
        if (search == null)
            return;
        search.ObserveFrame(milliseconds);
        if (milliseconds >= 100d)
        {
            SolverProgress? progress = Volatile.Read(ref search.Interaction.Progress);
            Entry.Logger.Info(
                $"[CombatSolver/Test] MAIN_THREAD_LONG_FRAME gap_ms={milliseconds:F1} " +
                $"frame={search.FrameCount} expanded={progress?.ExpandedNodes ?? -1} " +
                $"process_allocated_delta={GC.GetTotalAllocatedBytes(precise: false) - search.ProcessAllocatedBytesAtStart} " +
                $"gc_pause_delta_ms={(GC.GetTotalPauseDuration() - search.ProcessGcPauseAtStart).TotalMilliseconds:F1}");
        }
    }
    private static void PublishSearchProgress(SolverSearchSession search, SolverProgress progress)
    {
        if (ReferenceEquals(_search, search))
            search.Interaction.PublishProgress(progress);
    }

    private static string DescribeReplanAudit()
    {
        string differences = _combat.LastContinuationDifferences.Count == 0
            ? "-"
            : string.Join(System.Environment.NewLine, _combat.LastContinuationDifferences);
        string manualComparison = _combat.LastManualProjectionComparison is { } comparison
            ? $"previous={comparison.PreviousProjectedBattleHpLost} current={comparison.CurrentProjectedBattleHpLost} " +
              $"difference={comparison.Difference} original_turn={comparison.OriginalTurnNumber} " +
              $"current_turn={comparison.CurrentTurnNumber}"
            : "-";
        return DescribeReplanCounts() +
               System.Environment.NewLine + "last_state_differences=" + differences +
               System.Environment.NewLine + "last_manual_projection_comparison=" + manualComparison;
    }

    private static string DescribeReplanCounts()
    {
        string counts = string.Join(' ', Enum.GetValues<ReplanCause>()
            .Select(cause => $"{CauseToken(cause)}={_combat.ReplanCounts.GetValueOrDefault(cause)}"));
        return $"searches={_combat.SearchesStarted} reused={_combat.ContinuationsReused} {counts} " +
               $"control_mode={ControlModeForBugReport} " +
               $"last_solver_deployed_turn={_combat.LastSolverDeployedTurn?.ToString() ?? "-"}";
    }

    private static void MarkManualControlObserved(string reason)
    {
        if (_combat.ManualControlObserved)
            return;
        _combat.ManualControlObserved = true;
        Entry.Logger.Info($"[CombatSolver/Test] CONTROL_MODE_CHANGED mode=manual_plus_solver reason={reason}");
    }

    private static string CauseToken(ReplanCause cause)
        => cause switch
        {
            ReplanCause.InitialSearch => "initial_search",
            ReplanCause.StateMismatch => "state_mismatch",
            ReplanCause.ManualDivergence => "manual_divergence",
            ReplanCause.ContinuationMissing => "continuation_missing",
            ReplanCause.DeploymentDrift => "deployment_drift",
            ReplanCause.PlanExhausted => "plan_exhausted",
            ReplanCause.ExplicitRequest => "explicit_request",
            _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, null),
        };

    private static void CompleteSearch(
        NGame host,
        SolverSearchSession search,
        Task<SolverResult> task)
    {
        try
        {
            CompleteSearchCore(host, search, task);
        }
        finally
        {
            search.CallbackCompletion.TrySetResult();
            DisposeSearchCancellationOnce(search);
        }
    }

    private static void CompleteSearchCore(
        NGame host,
        SolverSearchSession search,
        Task<SolverResult> task)
    {
        AssertMainThread();
        if (!ReferenceEquals(_search, search))
            return;
        _search = null;
        Volatile.Write(ref search.Interaction.Progress, null);
        int generation = search.Generation;
        Entry.Logger.Info($"[CombatSolver/Test] SEARCH_CALLBACK generation={generation} thread={System.Environment.CurrentManagedThreadId} main_thread={NGame.IsMainThread()}");
        Entry.Logger.Info(
            $"[CombatSolver/Test] MAIN_THREAD_FRAMES generation={generation} frames={search.FrameCount} " +
            $"p95_gap_ms={search.FramePercentile(0.95d):F1} p99_gap_ms={search.FramePercentile(0.99d):F1} " +
            $"max_gap_ms={search.MaxFrameGapMilliseconds:F1} over_33ms={search.FramesOver33Milliseconds} " +
            $"over_50ms={search.FramesOver50Milliseconds} over_100ms={search.FramesOver100Milliseconds}");

        if (task.IsCanceled)
        {
            _combat.PendingCompleteProjectionBaseline = null;
            _combat.PendingManualProjectionBaseline = null;
            SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Canceled);
            Entry.Logger.Info($"[CombatSolver/Test] SEARCH_CANCELED generation={generation}");
            return;
        }
        if (task.IsFaulted)
        {
            _combat.PendingCompleteProjectionBaseline = null;
            _combat.PendingManualProjectionBaseline = null;
            Exception ex = task.Exception?.GetBaseException() ?? new InvalidOperationException("后台搜索失败但没有异常对象。");
            _combat.BugReportIssues.RecordFailure(CombatBugReportIssueKind.SearchFailure, ex);
            LastSearchFailureForTesting = ex;
            if (ex is PotionPolicyUnsatisfiedException)
            {
                _combat.FullAutoEnabled = false;
                SolverOverlay.RefreshControls();
            }
            SolverOverlay.Show(
                host,
                FormatSearchFailure(ex, search.MaxDegreeOfParallelism > 1));
            SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Failed);
            Entry.Logger.Error($"[CombatSolver/Test] SEARCH_FAILURE generation={generation} exception={ex}");
            return;
        }

        CombatState searchedState = search.State;
        LiveCombatStamp searchedStamp = search.Stamp;
        CombatState? currentState = CombatManager.Instance.DebugOnlyGetState();
        if (!ReferenceEquals(currentState, searchedState)
            || !CanSolve(searchedState, out _)
            || LiveCombatStamp.Capture(searchedState) != searchedStamp)
        {
            _combat.BugReportIssues.Record(
                CombatBugReportIssueKind.SearchResultStale,
                $"第 {LocalContext.GetMe(searchedState)?.PlayerCombatState?.TurnNumber ?? 0} 回合");
            _combat.PendingCompleteProjectionBaseline = null;
            _combat.PendingManualProjectionBaseline = null;
            _combat.LatestResult = null;
            _combat.LatestStamp = null;
            SolverOverlay.Show(
                host,
                "[b]战斗路线求解器[/b]\n战斗状态在计算期间发生变化，已丢弃过期结果。\n" +
                SolverUiTokens.BugReportUploadInstructionRichText);
            SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Stale);
            Entry.Logger.Info($"[CombatSolver/Test] SEARCH_STALE generation={generation}");
            return;
        }

        SolverResult result = task.Result;
        bool stopped = search.Interaction.StopRequested;
        bool currentTurnAdopted = result.ResultScope == SolverResultScope.CurrentTurnAdoption;
        bool routeAdopted = result.ResultScope == SolverResultScope.RouteAdoption;
        if (stopped || currentTurnAdopted)
        {
            _combat.PendingCompleteProjectionBaseline = null;
            _combat.PendingManualProjectionBaseline = null;
        }
        else
        {
            ApplyProjectionBaselines(result);
        }
        ApplySearchFrameMetrics(result, search);
        RecordReviewedWorldlines(result);

        if (stopped)
        {
            result.ResultScope = SolverResultScope.RouteAdoption;
            search.Interaction.PreserveStoppedResult(result, searchedStamp);
            _combat.StoppedSearch = search.Interaction;
            SolverOverlay.ShowResult(
                host,
                SolverOverlaySnapshot.CaptureWithReviewedWorldlines(
                    result,
                    UnexpectedReplanCount > 0,
                    _combat.ReviewedWorldlinesTotal));
            SolverOverlay.ShowSearchStopped(host);
            SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Canceled);
            Entry.Logger.Info(
                $"[CombatSolver/Test] SEARCH_STOPPED_RESULT_PRESERVED generation={generation} " +
                $"actions={result.BestNode.Actions.Count}");
            return;
        }

        _combat.LatestResult = result;
        _combat.LatestStamp = searchedStamp;
        _combat.ContinuationSource = currentTurnAdopted ? null : result;
        if (UnattendedTestRunner.IsActive)
            LastCompletedResultForTesting = result;
        BattleDamageTracker.RegisterPlan(searchedState, result);
        CombatBugReportExporter.RecordCheckpoint(
            searchedState,
            currentTurnAdopted
                ? "search_current_turn_adopted"
                : routeAdopted
                    ? "search_route_adopted"
                    : "search_completed",
            result,
            DescribeReplanAudit());
        SolverOverlaySnapshot completedSnapshot = currentTurnAdopted
            ? SolverOverlaySnapshot.CaptureCurrentTurn(SolverCurrentTurnPreview.FromResult(result))
            : SolverOverlaySnapshot.CaptureWithReviewedWorldlines(
                result,
                UnexpectedReplanCount > 0,
                _combat.ReviewedWorldlinesTotal);
        SolverOverlay.ShowResult(
            host,
            routeAdopted ? MarkRouteAdopted(completedSnapshot) : completedSnapshot);
        SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Succeeded);
        if (currentTurnAdopted)
        {
            Entry.Logger.Info(
                $"[CombatSolver/Test] SEARCH_CURRENT_TURN_ADOPTED generation={generation} " +
                $"turn={result.StartTurnNumber} actions={result.BestNode.Actions.Count}");
        }
        else if (routeAdopted)
        {
            Entry.Logger.Info(
                $"[CombatSolver/Test] SEARCH_ROUTE_ADOPTED generation={generation} " +
                $"actions={result.BestNode.Actions.Count} continuations={result.Continuations.Count}");
        }
        Entry.Logger.Info(SolverDiagnostics.DescribeResult(result));
        if (currentTurnAdopted || search.DeployWhenReady)
            StartDeployment(host, searchedState, result);
        else if (_combat.FullAutoEnabled)
            StartFullAutoDeployment(host, searchedState, result);
    }

    private static void ApplyProjectionBaselines(SolverResult result)
    {
        CompleteProjectionBaseline? recalculationBaseline = _combat.PendingCompleteProjectionBaseline;
        ManualProjectionBaseline? manualBaseline = _combat.PendingManualProjectionBaseline;
        _combat.PendingCompleteProjectionBaseline = null;
        _combat.PendingManualProjectionBaseline = null;
        if (recalculationBaseline != null
            && result.ProjectedBattleHpLost > recalculationBaseline.ProjectedBattleHpLost)
        {
            result.RecalculatedAfterCompleteProjection = true;
            result.PreviousProjectedBattleHpLost = recalculationBaseline.ProjectedBattleHpLost;
            result.RecalculationStateDifference = recalculationBaseline.StateDifference;
            Entry.Logger.Warn(
                $"[CombatSolver/Test] COMPLETE_ROUTE_RECALCULATION_WORSENED " +
                $"original_turn={recalculationBaseline.StartTurnNumber} current_turn={result.StartTurnNumber} " +
                $"previous_projected_battle_hp_lost={recalculationBaseline.ProjectedBattleHpLost} " +
                $"current_projected_battle_hp_lost={result.ProjectedBattleHpLost} " +
                $"increase={result.ProjectedBattleHpLossIncrease} {recalculationBaseline.StateDifference}");
            _combat.BugReportIssues.Record(
                CombatBugReportIssueKind.RecalculationHpLossIncreased,
                $"预计战损 {recalculationBaseline.ProjectedBattleHpLost} → {result.ProjectedBattleHpLost}，" +
                $"增加 {result.ProjectedBattleHpLossIncrease} HP");
        }
        if (manualBaseline != null)
        {
            RecordManualProjectionComparison(
                manualBaseline,
                result.StartTurnNumber,
                result.ProjectedBattleHpLost);
        }
    }

    private static void ApplySearchFrameMetrics(SolverResult result, SolverSearchSession search)
    {
        result.MainThreadFrameCount = search.FrameCount;
        result.MainThreadFramesOver33Milliseconds = search.FramesOver33Milliseconds;
        result.MaxMainThreadFrameGapMilliseconds = search.MaxFrameGapMilliseconds;
        result.P95MainThreadFrameGapMilliseconds = search.FramePercentile(0.95d);
        result.P99MainThreadFrameGapMilliseconds = search.FramePercentile(0.99d);
        result.MainThreadFramesOver50Milliseconds = search.FramesOver50Milliseconds;
        result.MainThreadFramesOver100Milliseconds = search.FramesOver100Milliseconds;
    }

    private static SolverOverlaySnapshot MarkRouteAdopted(SolverOverlaySnapshot snapshot)
        => snapshot with
        {
            StatusText = "方案就绪 · 已采用当前路线",
            StatusTone = SolverOverlayTone.Success,
        };

    private static void StartFullAutoDeployment(NGame host, CombatState state, SolverResult result)
    {
        if (_stopFullAutoOnWorseRecalculation
            && !result.WasReused
            && result.ProjectedBattleHpLossIncrease > 0)
        {
            _combat.BugReportIssues.Record(
                CombatBugReportIssueKind.FullAutoStoppedAfterWorseRecalculation,
                $"第 {result.StartTurnNumber} 回合，预计战损 {result.PreviousProjectedBattleHpLost} → {result.ProjectedBattleHpLost}");
            _combat.FullAutoEnabled = false;
            LastFullAutoStoppedForWorseRecalculationForTesting = true;
            SolverOverlay.RefreshControls();
            SolverOverlay.ShowFullAutoStoppedAfterWorseRecalculation(
                result.StartTurnNumber,
                result.PreviousProjectedBattleHpLost,
                result.ProjectedBattleHpLost);
            Entry.Logger.Info(
                $"[CombatSolver/Test] FULL_AUTO_STOP reason=worse_recalculation " +
                $"turn={result.StartTurnNumber} increase={result.ProjectedBattleHpLossIncrease}");
            return;
        }
        if (_stopFullAutoOnCombatEnd && result.CombatEndedTurn == result.StartTurnNumber)
        {
            _combat.FullAutoEnabled = false;
            SolverOverlay.RefreshControls();
            SolverOverlay.ShowFullAutoStoppedAtCombatEnd(result.StartTurnNumber);
            Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_STOP reason=combat_end_turn turn={result.StartTurnNumber}");
            return;
        }
        if (_stopFullAutoOnDeathTurn && result.DeathTurn == result.StartTurnNumber)
        {
            _combat.BugReportIssues.Record(
                CombatBugReportIssueKind.FullAutoStoppedAtDeathTurn,
                $"第 {result.StartTurnNumber} 回合");
            _combat.FullAutoEnabled = false;
            SolverOverlay.RefreshControls();
            SolverOverlay.ShowFullAutoStoppedAtDeathTurn(result.StartTurnNumber);
            Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_STOP reason=death_turn turn={result.StartTurnNumber}");
            return;
        }

        Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_DEPLOY turn={result.StartTurnNumber}");
        StartDeployment(host, state, result);
    }

    private static void StartDeployment(NGame host, CombatState state, SolverResult result)
    {
        bool hasCurrentTurnPlan = result.BestNode.Actions.Any(action =>
            action.Turn == result.StartTurnNumber
            && (action.IsExecutable || action.Kind == PlanActionKind.EndTurn));
        if (!hasCurrentTurnPlan)
        {
            _combat.ContinuationSource = null;
            Entry.Logger.Warn(
                $"[CombatSolver/Test] DEPLOY_REPLAN turn={result.StartTurnNumber} reason=turn_plan_exhausted");
            RequestSearch(
                host,
                state,
                SearchReason.PlanExhausted,
                deployWhenReady: !_combat.FullAutoEnabled);
            return;
        }

        _combat.LatestResult = null;
        _combat.LatestStamp = null;
        CancelDeployment();
        SolverDeploymentSession deployment = new();
        _deployment = deployment;
        int actionCount = result.BestNode.Actions.Count(action =>
            action.Turn == result.StartTurnNumber && action.IsExecutable);
        SolverSettingsSnapshot deploymentSettings = SolverSettings.Capture();
        SolverOverlay.ShowDeploying(host, result.StartTurnNumber, actionCount);
        Task deploymentTask = DeployCurrentTurn(
            host,
            state,
            result,
            deploymentSettings,
            deployment,
            deployment.Cancellation.Token);
        deployment.Operation = deploymentTask;
        if (UnattendedAsyncActivityTracker.IsRequestActive)
            deploymentTask = UnattendedAsyncActivityTracker.Track(deploymentTask);
        TaskHelper.RunSafely(deploymentTask);
    }

    private static async Task DeployCurrentTurn(
        NGame host,
        CombatState state,
        SolverResult result,
        SolverSettingsSnapshot deploymentSettings,
        SolverDeploymentSession deployment,
        CancellationToken token)
    {
        AssertMainThread();
        bool measureDeploymentTiming = UnattendedTestRunner.IsActive;
        long deploymentStartedAt = measureDeploymentTiming
            ? Stopwatch.GetTimestamp()
            : 0;
        int turn = result.StartTurnNumber;
        List<PlanAction> actions = result.BestNode.Actions
            .Where(action => action.Turn == turn && action.IsExecutable)
            .ToList();
        PlanAction? plannedEndTurn = result.BestNode.Actions
            .FirstOrDefault(action => action.Turn == turn && action.Kind == PlanActionKind.EndTurn);
        FastModeType originalFastMode = SaveManager.Instance.PrefsSave.FastMode;
        FastModeType? overrideFastMode = ResolveDeploymentFastMode(deploymentSettings.DeploymentFastMode);
        try
        {
            if (overrideFastMode is { } requestedFastMode)
                SaveManager.Instance.PrefsSave.FastMode = requestedFastMode;
            Entry.Logger.Info(
                $"[CombatSolver/Test] DEPLOY_START turn={turn} action_count={actions.Count} " +
                $"fast_mode={deploymentSettings.DeploymentFastMode} " +
                $"inter_action_delay_seconds={deploymentSettings.DeploymentInterActionDelaySeconds:0.###}");
            for (int actionIndex = 0; actionIndex < actions.Count; actionIndex++)
            {
                PlanAction action = actions[actionIndex];
                token.ThrowIfCancellationRequested();
                if (!IsSamePlayableTurn(state, turn))
                    throw new InvalidOperationException("部署途中已不再是原玩家回合。");

                Player player = LocalContext.GetMe(state)!;
                Creature? target = state.GetCreature(action.TargetCombatId);
                SolverOverlay.ShowDeploymentStep(actionIndex, actions.Count, action.ActionTitle);
                List<PlanCardChoice> actionChoices = [.. action.GetActionChoicesInExecutionOrder()];
                // A card can advance the turn directly or through a nested auto-play, so its
                // next-turn choices belong to this native UI session.
                if (action.EndsPlayerTurn && action.TurnStartChoices is { Count: > 0 })
                    actionChoices.AddRange(action.TurnStartChoices);
                if (actionChoices.Count > 0)
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] DEPLOY_CHOICE_PLAN turn={turn} card={action.CardId} " +
                        $"count={actionChoices.Count} sources={string.Join(',', actionChoices.Select(choice =>
                            string.IsNullOrEmpty(choice.SourceId) ? choice.Effect.ToString() : choice.SourceId))} " +
                        $"cards={string.Join(';', actionChoices.Select(choice =>
                            $"{choice.Effect}:{string.Join(',', choice.Cards.Select(card =>
                                $"{card.CardId}+{card.UpgradeLevel}#src{card.SourceOccurrence}/opt{card.OptionOccurrence}"))}"))}");
                }
                using NativeChoiceSession choiceSession = NativeChoiceRuntime.Begin(
                    state,
                    player,
                    $"deployment:{turn}:{actionIndex}:{action.CardId ?? action.PotionId}");
                choiceSession.SetPlanAndStartDriving(host, actionChoices, token);
                long actionStartedAt = measureDeploymentTiming
                    ? Stopwatch.GetTimestamp()
                    : 0;
                Task actionCompletion;
                if (action.Kind == PlanActionKind.UsePotion)
                {
                    PotionModel potion = player.GetPotionAtSlotIndex(action.PotionSlot)
                        ?? throw new InvalidOperationException($"部署时药水槽位 {action.PotionSlot} 为空。");
                    if (!string.Equals(potion.Id.Entry, action.PotionId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"部署时药水槽位 {action.PotionSlot} 为 {potion.Id.Entry}，预期 {action.PotionId}。");
                    }
                    GameAction queuedAction = await EnqueueAndCaptureActionAsync(
                        candidate => candidate is UsePotionAction usePotion
                            && ReferenceEquals(usePotion.Player, player)
                            && usePotion.PotionIndex == (uint)action.PotionSlot,
                        () => potion.EnqueueManualUse(target),
                        token);
                    actionCompletion = queuedAction.CompletionTask;
                    LastDeployedActionStartedAtMillisecondsForTesting = System.Environment.TickCount64;
                    DeployedPotionIdsForTesting.Add(action.PotionId);
                    Entry.Logger.Info($"[CombatSolver/Test] DEPLOY_ACTION turn={turn} potion={action.PotionId} slot={action.PotionSlot} target_index={action.TargetIndex} target_combat_id={action.TargetCombatId?.ToString() ?? "-"}");
                }
                else
                {
                    List<CardModel> hand = player.PlayerCombatState!.Hand.Cards.ToList();
                    CardModel card = FindCardForDeployment(hand, action);
                    if (!card.CanPlayTargeting(target))
                    {
                        bool targetValid = card.IsValidTarget(target);
                        bool cardPlayable = card.CanPlay(out UnplayableReason reason, out AbstractModel? preventer);
                        Entry.Logger.Warn(
                            $"[CombatSolver/Test] DEPLOY_REPLAN turn={turn} reason=card_unplayable " +
                            $"card={action.CardId} occurrence={action.CardOccurrence} target_valid={targetValid} " +
                            $"can_play={cardPlayable} unplayable_reason={reason} " +
                            $"preventer={preventer?.Id.Entry ?? "-"} energy={player.PlayerCombatState.Energy} " +
                            $"stars={player.PlayerCombatState.Stars} energy_cost={card.EnergyCost.GetAmountToSpend()} " +
                            $"star_cost={card.GetStarCostWithModifiers()}");
                        _combat.ContinuationSource = null;
                        CompleteDeployment(deployment);
                        RequestSearch(
                            host,
                            state,
                            SearchReason.DeploymentDrift,
                            deployWhenReady: !_combat.FullAutoEnabled);
                        return;
                    }
                    GameAction queuedAction = await EnqueueAndCaptureActionAsync(
                        candidate => candidate is PlayCardAction playCard
                            && ReferenceEquals(playCard.NetCombatCard.ToCardModelOrNull(), card),
                        () =>
                        {
                            if (!card.TryManualPlay(target))
                                throw new InvalidOperationException($"部署卡牌 {action.CardId} 在入队时失去可用状态。");
                        },
                        token);
                    actionCompletion = queuedAction.CompletionTask;
                    LastDeployedActionStartedAtMillisecondsForTesting = System.Environment.TickCount64;
                    DeployedCardIdsForTesting.Add(card.Id.Entry);
                    Entry.Logger.Info($"[CombatSolver/Test] DEPLOY_ACTION turn={turn} card={action.CardId} target_index={action.TargetIndex} target_combat_id={action.TargetCombatId?.ToString() ?? "-"} choice={action.Choice?.Effect.ToString() ?? "-"}");
                }
                await choiceSession.AwaitProducerAndCompleteAsync(actionCompletion);
                if (measureDeploymentTiming)
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] DEPLOY_ACTION_COMPLETE turn={turn} action_index={actionIndex} " +
                        $"action={action.CardId ?? action.PotionId ?? action.Kind.ToString()} " +
                        $"elapsed_ms={Stopwatch.GetElapsedTime(actionStartedAt).TotalMilliseconds:F1}");
                }
                if (deploymentSettings.EnableDetailedDiagnosticLogs)
                {
                    PlayerCombatState liveState = player.PlayerCombatState!;
                    Entry.Logger.Info(
                        $"[CombatSolver/Debug] DEPLOY_STATE turn={turn} action_index={actionIndex} " +
                        $"action={action.CardId ?? action.PotionId ?? action.Kind.ToString()} " +
                        $"energy={liveState.Energy} hand={string.Join(',', liveState.Hand.Cards.Select(card => card.Id.Entry))} " +
                        $"draw={string.Join(',', liveState.DrawPile.Cards.Select(card => card.Id.Entry))} " +
                        $"discard={string.Join(',', liveState.DiscardPile.Cards.Select(card => card.Id.Entry))} " +
                        $"exhaust={string.Join(',', liveState.ExhaustPile.Cards.Select(card => card.Id.Entry))} " +
                        $"enemies={string.Join(',', state.Enemies.Select(enemy =>
                            $"{enemy.Monster?.Id.Entry ?? "null"}:{enemy.CurrentHp}/{enemy.Block}"))} " +
                        $"powers={string.Join(',', player.Creature.Powers.Select(power =>
                            $"{power.Id.Entry}:{power.Amount}/{power.AmountOnTurnStart}"))}");
                }
                SolverOverlay.ShowDeploymentStep(actionIndex + 1, actions.Count, null);
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                if (actionIndex + 1 < actions.Count
                    && deploymentSettings.DeploymentInterActionDelaySeconds > 0d)
                {
                    await WaitForDeploymentDelayAsync(
                        host,
                        deploymentSettings.DeploymentInterActionDelaySeconds,
                        token);
                }
            }

            if (CombatManager.Instance.IsInProgress && IsSamePlayableTurn(state, turn))
            {
                if (plannedEndTurn == null)
                {
                    Entry.Logger.Warn(
                        $"[CombatSolver/Test] DEPLOY_REPLAN turn={turn} reason=turn_plan_exhausted " +
                        $"executed_actions={actions.Count}");
                    _combat.ContinuationSource = null;
                    CompleteDeployment(deployment);
                    RequestSearch(
                        host,
                        state,
                        SearchReason.PlanExhausted,
                        deployWhenReady: !_combat.FullAutoEnabled);
                    return;
                }
                Player player = LocalContext.GetMe(state)!;
                if (_combat.FullAutoEnabled
                    && (_stopFullAutoOnDeathTurn || _stopFullAutoOnWorseRecalculation))
                {
                    await UnattendedTestRunner.ApplyScheduledPreEndTurnDriftAsync(state, turn);
                    LiveEndTurnRiskProjection liveRisk = LiveEndTurnRiskEvaluator.Evaluate(
                        state,
                        plannedEndTurn.TurnStartChoices);
                    int plannedHpLoss = result.HpLostByTurn.GetValueOrDefault(turn);
                    bool worsened = liveRisk.HpLost > plannedHpLoss;
                    if ((_stopFullAutoOnDeathTurn && liveRisk.PlayerDead)
                        || (_stopFullAutoOnWorseRecalculation && worsened))
                    {
                        _combat.BugReportIssues.Record(
                            liveRisk.PlayerDead
                                ? CombatBugReportIssueKind.FullAutoStoppedAtLiveRiskDeath
                                : CombatBugReportIssueKind.FullAutoStoppedAtLiveRiskWorsening,
                            $"第 {turn} 回合，路线预计 {plannedHpLoss} HP，实机复核 {liveRisk.HpLost} HP");
                        _combat.FullAutoEnabled = false;
                        LastFullAutoStoppedAtLiveRiskForTesting = true;
                        _combat.ContinuationSource = null;
                        SolverOverlay.RefreshControls();
                        SolverOverlay.ShowFullAutoStoppedAtLiveRisk(
                            turn,
                            plannedHpLoss,
                            liveRisk.HpLost,
                            liveRisk.PlayerDead);
                        Entry.Logger.Warn(
                            $"[CombatSolver/Test] FULL_AUTO_STOP reason=live_end_turn_risk turn={turn} " +
                            $"planned_hp_lost={plannedHpLoss} live_hp_lost={liveRisk.HpLost} " +
                            $"hp_before={liveRisk.HpBefore} hp_after={liveRisk.HpAfter} " +
                            $"player_dead={liveRisk.PlayerDead} moves={liveRisk.MonsterMoves}");
                        return;
                    }
                }
                _combat.LastSolverDeployedTurn = turn;
                SolverOverlay.ShowEndTurnDeploymentStep();
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                token.ThrowIfCancellationRequested();
                PlanCardChoice[] endTurnChoices = plannedEndTurn.TurnStartChoices?
                    .Where(choice => choice.Timing is PlanChoiceTiming.PlayerTurnEnd or PlanChoiceTiming.EnemyTurn)
                    .ToArray() ?? [];
                if (endTurnChoices.Length > 0)
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] DEPLOY_END_TURN_CHOICE_PLAN turn={turn} " +
                        $"count={endTurnChoices.Length} sources={string.Join(',', endTurnChoices.Select(choice => choice.SourceId))}");
                    using NativeChoiceSession choiceSession = NativeChoiceRuntime.Begin(
                        state,
                        player,
                        $"deployment_end_turn:{turn}");
                    choiceSession.SetPlanAndStartDriving(host, endTurnChoices, token);
                    CombatManager.Instance.OnEndedTurnLocally();
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
                    await choiceSession.WaitForAllPlansConsumedAsync(token);
                    await choiceSession.CompleteAndDetachAsync();
                }
                else
                {
                    CombatManager.Instance.OnEndedTurnLocally();
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
                }
                SolverOverlay.ShowDeploymentComplete(host, turn, actions.Count, endedTurn: true);
                string deploymentEndMessage =
                    $"[CombatSolver/Test] DEPLOY_END turn={turn} action_count={actions.Count} end_turn=true " +
                    $"forecast_turn_start_choices={plannedEndTurn.TurnStartChoices?.Count ?? 0} " +
                    $"end_turn_choices={endTurnChoices.Length}";
                if (measureDeploymentTiming)
                {
                    deploymentEndMessage +=
                        $" elapsed_ms={Stopwatch.GetElapsedTime(deploymentStartedAt).TotalMilliseconds:F1}";
                }
                Entry.Logger.Info(deploymentEndMessage);
                _combat.LastSolverDeployedTurn = turn;
            }
            else
            {
                SolverOverlay.ShowDeploymentComplete(host, turn, actions.Count, endedTurn: false);
                string deploymentEndMessage =
                    $"[CombatSolver/Test] DEPLOY_END turn={turn} action_count={actions.Count} " +
                    $"end_turn=false combat_or_turn_finished=true";
                if (measureDeploymentTiming)
                {
                    deploymentEndMessage +=
                        $" elapsed_ms={Stopwatch.GetElapsedTime(deploymentStartedAt).TotalMilliseconds:F1}";
                }
                Entry.Logger.Info(deploymentEndMessage);
                _combat.LastSolverDeployedTurn = turn;
            }
        }
        catch (OperationCanceledException)
        {
            Entry.Logger.Info($"[CombatSolver/Test] DEPLOY_CANCELED turn={turn}");
        }
        catch (Exception ex)
        {
            _combat.BugReportIssues.RecordFailure(CombatBugReportIssueKind.DeploymentFailure, ex);
            SolverOverlay.Show(host, FormatDeploymentFailure(ex));
            Entry.Logger.Error($"[CombatSolver/Test] DEPLOY_FAILURE turn={turn} exception={ex}");
        }
        finally
        {
            try
            {
                if (overrideFastMode.HasValue)
                {
                    SaveManager.Instance.PrefsSave.FastMode = originalFastMode;
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] DEPLOY_SPEED_RESTORED turn={turn} restored={originalFastMode}");
                }
                if (measureDeploymentTiming)
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] DEPLOY_FINISH turn={turn} " +
                        $"elapsed_ms={Stopwatch.GetElapsedTime(deploymentStartedAt).TotalMilliseconds:F1}");
                }
            }
            finally
            {
                CompleteDeployment(deployment);
                DisposeDeploymentCancellationOnce(deployment);
                SolverOverlay.RefreshControls();
            }
        }
    }

    internal static CardModel FindCardForDeployment(
        IReadOnlyList<CardModel> hand,
        PlanAction action)
    {
        if (!string.IsNullOrEmpty(action.CardStateKey))
        {
            CardModel? stateMatch = hand
                .Where(card => string.Equals(
                    CardChoiceSupport.ChoiceCardKey(card),
                    action.CardStateKey,
                    StringComparison.Ordinal))
                .Skip(action.CardStateOccurrence)
                .FirstOrDefault();
            return stateMatch ?? throw new InvalidOperationException(
                $"部署时找不到计划中的手牌状态 {action.CardId}@{action.CardStateOccurrence}；" +
                $"当前手牌={string.Join(',', hand.Select(card => CardChoiceSupport.ChoiceCardKey(card)))}。");
        }

        return hand
            .Where(card => string.Equals(card.Id.Entry, action.CardId, StringComparison.Ordinal))
            .Skip(action.CardOccurrence)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"部署时找不到手牌 {action.CardId}#{action.CardOccurrence}；" +
                $"当前手牌={string.Join(',', hand.Select(card => card.Id.Entry))}。");
    }

    private static async Task<GameAction> EnqueueAndCaptureActionAsync(
        Func<GameAction, bool> matches,
        Action enqueue,
        CancellationToken token)
    {
        TaskCompletionSource<GameAction> captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ActionExecutor executor = RunManager.Instance.ActionExecutor;
        void OnBeforeActionExecuted(GameAction action)
        {
            if (matches(action))
                captured.TrySetResult(action);
        }

        executor.BeforeActionExecuted += OnBeforeActionExecuted;
        try
        {
            enqueue();
            return await captured.Task.WaitAsync(token);
        }
        finally
        {
            executor.BeforeActionExecuted -= OnBeforeActionExecuted;
        }
    }

    internal static async Task WaitForTurnStartDeploymentDelayAsync(
        NGame host,
        int turn,
        CancellationToken token = default)
    {
        double seconds = SolverSettings.Capture().DeploymentInterActionDelaySeconds;
        if (seconds <= 0d)
            return;
        long startedAt = System.Environment.TickCount64;
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_START_DEPLOY_DELAY turn={turn} seconds={seconds:0.###}");
        await WaitForDeploymentDelayAsync(host, seconds, token);
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_START_DEPLOY_DELAY_COMPLETE turn={turn} " +
            $"elapsed_ms={System.Environment.TickCount64 - startedAt}");
    }

    private static async Task WaitForDeploymentDelayAsync(
        NGame host,
        double seconds,
        CancellationToken token)
    {
        SceneTreeTimer delay = host.GetTree().CreateTimer(
            seconds,
            processAlways: false,
            processInPhysics: false,
            ignoreTimeScale: false);
        await AwaitSceneTreeTimerAsync(host, delay).WaitAsync(token);
    }

    private static async Task AwaitSceneTreeTimerAsync(NGame host, SceneTreeTimer delay)
        => await host.ToSignal(delay, SceneTreeTimer.SignalName.Timeout);

    private static void DeferSearchUntilRootCaptureBarrier(
        NGame host,
        CombatState state,
        SearchReason reason,
        bool deployWhenReady,
        Task barrier)
    {
        CancelDeferredSearch();
        CancellationTokenSource cancellation = new();
        _deferredSearchCts = cancellation;
        int requestId = ++_deferredSearchId;
        int lifecycleGeneration = Volatile.Read(ref _combatLifecycleGeneration);
        Entry.Logger.Info(
            $"[CombatSolver/Test] SEARCH_ROOT_CAPTURE_DEFERRED reason={reason} " +
            $"lifecycle_epoch={lifecycleGeneration}");
        PendingDeferredSearchReleases.RemoveAll(static task => task.IsCompleted);
        Task operation = ResumeDeferredSearchAsync(
            host,
            state,
            reason,
            deployWhenReady,
            barrier,
            cancellation,
            requestId,
            lifecycleGeneration);
        PendingDeferredSearchReleases.Add(operation);
        TaskHelper.RunSafely(operation);
    }

    private static async Task ResumeDeferredSearchAsync(
        NGame host,
        CombatState state,
        SearchReason reason,
        bool deployWhenReady,
        Task barrier,
        CancellationTokenSource cancellation,
        int requestId,
        int lifecycleGeneration)
    {
        CancellationToken token = cancellation.Token;
        try
        {
            await barrier.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            TaskCompletionSource callbackCompleted = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            SolverDispatcher.Post(() =>
            {
                try
                {
                    bool ownsDeferredRequest = requestId == _deferredSearchId
                        && ReferenceEquals(_deferredSearchCts, cancellation);
                    if (token.IsCancellationRequested
                        || !ownsDeferredRequest
                        || Volatile.Read(ref _combatLifecycleGeneration) != lifecycleGeneration
                        || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state))
                    {
                        if (ownsDeferredRequest)
                            _deferredSearchCts = null;
                        return;
                    }
                    _deferredSearchCts = null;
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] SEARCH_ROOT_CAPTURE_RESUMED reason={reason} " +
                        $"lifecycle_epoch={lifecycleGeneration}");
                    RequestSearch(host, state, reason, deployWhenReady);
                }
                finally
                {
                    callbackCompleted.TrySetResult();
                }
            });
            // Even a canceled request waits for its queued callback. That callback owns the
            // final references to the old combat graph, so the combat-end barrier must not
            // declare quiescence merely because its token was canceled.
            await callbackCompleted.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Reset/newer request owns the deferred-search cancellation.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static void CancelDeferredSearch()
    {
        CancellationTokenSource? cancellation = _deferredSearchCts;
        _deferredSearchCts = null;
        if (cancellation == null)
            return;
        _deferredSearchId++;
        cancellation.Cancel();
    }

    private static Task DrainDeferredSearchReleases()
    {
        if (PendingDeferredSearchReleases.Count == 0)
            return Task.CompletedTask;
        Task[] releases = PendingDeferredSearchReleases.ToArray();
        PendingDeferredSearchReleases.Clear();
        return Task.WhenAll(releases);
    }

    private static Task CancelAndDrainCombatDeferredOperations()
    {
        CancellationTokenSource cancellation = _combatDeferredOperationCancellation;
        _combatDeferredOperationCancellation = new CancellationTokenSource();
        cancellation.Cancel();
        if (PendingCombatDeferredOperations.Count == 0)
        {
            cancellation.Dispose();
            return Task.CompletedTask;
        }
        Task[] operations = PendingCombatDeferredOperations.ToArray();
        PendingCombatDeferredOperations.Clear();
        return ReleaseCombatDeferredOperationsAsync(operations, cancellation);
    }

    private static async Task ReleaseCombatDeferredOperationsAsync(
        Task[] operations,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.WhenAll(operations)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static void CancelSearch()
    {
        SolverSearchSession? search = _search;
        _search = null;
        if (search == null)
            return;
        search.Cancellation.Cancel();
        if (!search.CallbackScheduled)
            search.CallbackCompletion.TrySetResult();
        QueueSearchReferenceRelease(search);
    }

    private static void RecordManualProjectionComparison(
        ManualProjectionBaseline baseline,
        int currentTurnNumber,
        int currentProjectedBattleHpLost)
    {
        ManualProjectionComparison comparison = new(
            baseline.StartTurnNumber,
            currentTurnNumber,
            baseline.ProjectedBattleHpLost,
            currentProjectedBattleHpLost,
            baseline.StateDifference);
        _combat.LastManualProjectionComparison = comparison;
        string direction;
        if (comparison.Difference < 0)
        {
            direction = "IMPROVED";
            _combat.ManualRouteImprovementDetected = true;
            _combat.BugReportIssues.Record(
                CombatBugReportIssueKind.BetterWorldline,
                $"预计战损 {comparison.PreviousProjectedBattleHpLost} → {comparison.CurrentProjectedBattleHpLost}，" +
                $"下降 {-comparison.Difference} HP");
        }
        else if (comparison.Difference > 0)
        {
            direction = "WORSENED";
            _combat.BugReportIssues.Record(
                CombatBugReportIssueKind.ManualHpLossIncreased,
                $"预计战损 {comparison.PreviousProjectedBattleHpLost} → {comparison.CurrentProjectedBattleHpLost}，" +
                $"增加 {comparison.Difference} HP");
        }
        else
        {
            direction = "UNCHANGED";
        }

        Entry.Logger.Info(
            $"[CombatSolver/Test] MANUAL_ROUTE_{direction} " +
            $"original_turn={comparison.OriginalTurnNumber} current_turn={comparison.CurrentTurnNumber} " +
            $"previous_projected_battle_hp_lost={comparison.PreviousProjectedBattleHpLost} " +
            $"current_projected_battle_hp_lost={comparison.CurrentProjectedBattleHpLost} " +
            $"difference={comparison.Difference} {comparison.StateDifference}");
    }

    private static string FormatSearchFailure(
        Exception exception,
        bool parallelSearchWasEnabled)
        => $"[color={SolverUiTokens.Palette.DangerHex}][b]计算失败[/b]\n" +
           $"{EscapeRichText(exception.Message)}[/color]\n" +
           SolverUiTokens.SearchFailureInstructionRichText(parallelSearchWasEnabled);

    private static string FormatDeploymentFailure(Exception exception)
        => $"[color={SolverUiTokens.Palette.DangerHex}][b]自动执行中止[/b]\n" +
           $"{EscapeRichText(exception.Message)}[/color]\n" +
           SolverUiTokens.BugReportUploadInstructionRichText;

    private static string FormatTurnSetupFailure(
        Exception exception,
        bool parallelSearchWasEnabled)
        => $"[color={SolverUiTokens.Palette.DangerHex}][b]回合准备选牌失败[/b]\n" +
           $"{EscapeRichText(exception.GetBaseException().Message)}[/color]\n" +
           SolverUiTokens.SearchFailureInstructionRichText(parallelSearchWasEnabled);

    private static void CancelDeployment()
    {
        SolverDeploymentSession? deployment = _deployment;
        _deployment = null;
        if (deployment == null)
            return;
        deployment.Cancellation.Cancel();
        QueueDeploymentReferenceRelease(deployment);
    }

    private static async Task ReleaseDeploymentSessionAsync(SolverDeploymentSession deployment)
    {
        try
        {
            await deployment.Operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            deployment.Operation = Task.CompletedTask;
        }
        finally
        {
            DisposeDeploymentCancellationOnce(deployment);
            Interlocked.Increment(ref _deploymentReferenceReleaseCompletedCountForTesting);
        }
    }

    private static void QueueDeploymentReferenceRelease(SolverDeploymentSession deployment)
    {
        if (Interlocked.Exchange(ref deployment.ReferenceReleaseState, 1) != 0)
            return;
        PendingDeploymentReferenceReleases.RemoveAll(static task => task.IsCompleted);
        Interlocked.Increment(ref _deploymentReferenceReleaseScheduledCountForTesting);
        PendingDeploymentReferenceReleases.Add(ReleaseDeploymentSessionAsync(deployment));
    }

    private static Task DrainDeploymentReferenceReleases()
    {
        if (PendingDeploymentReferenceReleases.Count == 0)
            return Task.CompletedTask;
        Task[] releases = [.. PendingDeploymentReferenceReleases];
        PendingDeploymentReferenceReleases.Clear();
        return Task.WhenAll(releases);
    }

    private static void DisposeDeploymentCancellationOnce(SolverDeploymentSession deployment)
    {
        if (Interlocked.Exchange(ref deployment.CancellationDisposeState, 1) != 0)
            return;
        deployment.Cancellation.Dispose();
        Interlocked.Increment(ref _deploymentCtsDisposeCountForTesting);
    }

    private static CombatBugReportClassificationSnapshot CaptureBugReportClassification()
        => new(
            _combat.ReplanCounts.GetValueOrDefault(ReplanCause.StateMismatch),
            _combat.ReplanCounts.GetValueOrDefault(ReplanCause.DeploymentDrift),
            _combat.ReplanCounts.GetValueOrDefault(ReplanCause.ContinuationMissing),
            _combat.ReplanCounts.GetValueOrDefault(ReplanCause.PlanExhausted),
            _combat.ReplanCounts.GetValueOrDefault(ReplanCause.ManualDivergence),
            _combat.BugReportIssues.Snapshot());

    private static void CompleteDeployment(SolverDeploymentSession deployment)
    {
        if (ReferenceEquals(_deployment, deployment))
            _deployment = null;
    }

    private static FastModeType? ResolveDeploymentFastMode(SolverDeploymentFastMode mode)
        => mode switch
        {
            SolverDeploymentFastMode.FollowGame => null,
            SolverDeploymentFastMode.Normal => FastModeType.Normal,
            SolverDeploymentFastMode.Fast => FastModeType.Fast,
            SolverDeploymentFastMode.Instant => FastModeType.Instant,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };

    private static bool CanSolve(CombatState state, out string rejection)
    {
        Player? player = LocalContext.GetMe(state);
        if (_solverDisabled)
            rejection = "求解器已在设置中禁用。";
        else if (!CombatManager.Instance.IsInProgress)
            rejection = "当前没有进行中的战斗。";
        else if (state.Players.Count != 1)
            rejection = "第一版只支持单人战斗。";
        else if (state.CurrentSide != CombatSide.Player || player?.PlayerCombatState?.Phase != PlayerTurnPhase.Play)
            rejection = "当前不是玩家出牌阶段。";
        else if (CombatManager.Instance.PlayerActionsDisabled)
            rejection = "玩家操作当前被游戏禁用。";
        else
        {
            rejection = string.Empty;
            return true;
        }
        return false;
    }

    private static bool IsSamePlayableTurn(CombatState state, int turn)
    {
        Player? player = LocalContext.GetMe(state);
        return ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
            && state.CurrentSide == CombatSide.Player
            && player?.PlayerCombatState?.TurnNumber == turn
            && player.PlayerCombatState.Phase == PlayerTurnPhase.Play;
    }

    private static void AssertMainThread()
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("CombatSolver 的主线程控制器被后台线程调用。");
    }
}
