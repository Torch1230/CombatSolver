using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using STS2RitsuLib;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

internal sealed class PlayerTurnSetupPatch : IPatchMethod
{
    private static readonly Type CombatTurnStateType = typeof(CombatManager).Assembly.GetType(
        "MegaCrit.Sts2.Core.Combat.CombatTurnState",
        throwOnError: true)!;

    public static string PatchId => "combat_solver_player_turn_setup";
    public static string Description => "首回合页面后搜索，后续回合可见重放既有选择";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(
            typeof(CombatManager),
            "SetupPlayerTurn",
            [CombatTurnStateType, typeof(Player), typeof(HookPlayerChoiceContext)]),
    ];

    [HarmonyPriority(Priority.First)]
    public static bool Prefix(
        CombatManager __instance,
        object __0,
        Player __1,
        HookPlayerChoiceContext __2,
        ref Task __result)
    {
        if (!PlayerTurnSetupCoordinator.TryInterceptSetup(__instance, __0, __1, __2, out Task? task))
            return true;
        __result = task!;
        return false;
    }
}

internal sealed class PlayerTurnAutoPrePlayPatch : IPatchMethod
{
    private static readonly Type CombatTurnStateType = typeof(CombatManager).Assembly.GetType(
        "MegaCrit.Sts2.Core.Combat.CombatTurnState",
        throwOnError: true)!;

    public static string PatchId => "combat_solver_player_turn_auto_pre_play";
    public static string Description => "回合准备自动牌通过原生页面执行计划选择";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(
            typeof(CombatManager),
            "RunAutoPrePlayPhase",
            [CombatTurnStateType, typeof(HookPlayerChoiceContext), typeof(Task), typeof(Player)]),
    ];

    [HarmonyPriority(Priority.First)]
    public static bool Prefix(
        CombatManager __instance,
        object __0,
        HookPlayerChoiceContext __1,
        Task __2,
        Player __3,
        ref Task __result)
    {
        if (!PlayerTurnSetupCoordinator.TryInterceptAutoPrePlay(
                __instance,
                __0,
                __1,
                __2,
                __3,
                out Task? task))
        {
            return true;
        }
        __result = task!;
        return false;
    }
}

internal sealed class PlayerTurnSetupSceneExitPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_player_turn_setup_scene_exit";
    public static string Description => "返回主菜单前取消仍在等待的回合开始原生选牌";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(NGame), nameof(NGame.ReturnToMainMenu), Type.EmptyTypes),
    ];

    [HarmonyPriority(Priority.First)]
    public static void Prefix()
        => PlayerTurnSetupCoordinator.PrepareForSceneExit();
}

internal static class PlayerTurnSetupCoordinator
{
    private sealed record InitialSearchContext(
        SolverDisplayNames DisplayNames,
        SolverSettingsSnapshot Settings,
        BattleDamageSnapshot BattleDamage,
        SearchPolicySnapshot SearchPolicy,
        CombatRootSnapshot RootSnapshot);

    private sealed class ActivePlan(
        CombatState combat,
        Player player,
        NativeChoiceSession choices,
        CancellationToken token,
        InitialSearchContext? initialSearch,
        IReadOnlyList<PlanCardChoice>? replayChoices)
    {

        public CombatState Combat { get; } = combat;
        public int LifecycleGeneration { get; } = SolverController.CombatLifecycleGeneration;
        public Player Player { get; } = player;
        public NativeChoiceSession Choices { get; } = choices;
        public CancellationToken Token { get; } = token;
        public InitialSearchContext? InitialSearch { get; } = initialSearch;
        public IReadOnlyList<PlanCardChoice>? ReplayChoices { get; } = replayChoices;
        public SolverResult? Result { get; set; }
        public SearchInteractionState Interaction { get; } =
            initialSearch?.SearchPolicy.Interaction ?? new SearchInteractionState();
        public SearchMemoryPressureSignal? MemoryPressureSignal =
            initialSearch?.SearchPolicy.MemoryPressureSignal;
        public int SearchState;
        public TaskCompletionSource ManualRecalculationRequested { get; set; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int ManualSearchState;
        public bool ManualRecalculated { get; set; }
        public int ManualRecalculationCompletedCount { get; set; }
        public bool ReplayDrivingStarted { get; set; }
        public bool ReplaySurfacePrepared { get; set; }
        public bool TakeoverRequested { get; set; }
        public bool DeployAfterSetup { get; set; }
        public int DisposeState;
        public IReadOnlyList<PlanCardChoice>? PlannedChoices
            => Result?.TurnSetupChoices ?? ReplayChoices;
        public TaskCompletionSource PlanReady { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly Type CombatTurnStateType = typeof(CombatManager).Assembly.GetType(
        "MegaCrit.Sts2.Core.Combat.CombatTurnState",
        throwOnError: true)!;
    private static readonly MethodInfo SetupPlayerTurnMethod = typeof(CombatManager).GetMethod(
        "SetupPlayerTurn",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        [CombatTurnStateType, typeof(Player), typeof(HookPlayerChoiceContext)],
        modifiers: null)
        ?? throw new MissingMethodException(typeof(CombatManager).FullName, "SetupPlayerTurn");
    private static readonly MethodInfo RunAutoPrePlayPhaseMethod = typeof(CombatManager).GetMethod(
        "RunAutoPrePlayPhase",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        [CombatTurnStateType, typeof(HookPlayerChoiceContext), typeof(Task), typeof(Player)],
        modifiers: null)
        ?? throw new MissingMethodException(typeof(CombatManager).FullName, "RunAutoPrePlayPhase");

    [ThreadStatic]
    private static bool _invokingOriginalSetup;
    [ThreadStatic]
    private static bool _invokingOriginalAutoPrePlay;
    private static CancellationTokenSource? _cancellation;
    private static CancellationTokenSource? _deferredSetupCancellation;
    private static ActivePlan? _active;
    private static Task _activeOperation = Task.CompletedTask;

    private static bool IsCurrentActivePlan(ActivePlan active)
        => ReferenceEquals(_active, active)
           && !active.Token.IsCancellationRequested
           && SolverController.IsCurrentCombatLifecycle(
               active.Combat,
               active.LifecycleGeneration);

    private static bool CanPublishForPlan(ActivePlan active)
        => !active.Token.IsCancellationRequested
           && SolverController.IsCurrentCombatLifecycle(
               active.Combat,
               active.LifecycleGeneration)
           && (ReferenceEquals(_active, active)
               || (_active == null && Volatile.Read(ref active.DisposeState) != 0));

    private static void RefreshSearchProgress(ActivePlan active)
    {
        if (!IsCurrentActivePlan(active) || !SolverOverlay.IsVisible
            || !active.Interaction.TryCreateDisplayProgress(
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
        active.Interaction.RenderedRouteAdoptionSeed = progress.SpeculativeRoutePreview == null
            ? null
            : progress.RouteAdoptionSeed;
        SolverOverlay.ShowProgress(
            progress,
            active.DeployAfterSetup,
            SolverController.ReviewedWorldlinesTotal,
            preview);
        SolverOverlay.RefreshControls();
    }

    public static bool TryInterceptSetup(
        CombatManager manager,
        object turnState,
        Player player,
        HookPlayerChoiceContext choiceContext,
        out Task? task)
    {
        task = null;
        if (_invokingOriginalSetup
            || !_activeOperation.IsCompleted
            || !Entry.Enabled
            || SolverController.SolverDisabled
            || !SolverController.AutomaticCalculationEnabled
            || SolverController.IsMultiplayerSession
            || SolverController.AutomaticSearchPaused
            || !ReferenceEquals(LocalContext.GetMe(manager.DebugOnlyGetState()), player)
            || player.PlayerCombatState == null
            || player.PlayerCombatState.Phase != PlayerTurnPhase.Start
            || CardSelectCmd.Selector != null)
        {
            return false;
        }

        CombatState combat = manager.DebugOnlyGetState()
            ?? throw new InvalidOperationException("回合准备选牌接管时战斗状态不存在。");
        int turn = player.PlayerCombatState.TurnNumber;
        IReadOnlyList<PlanCardChoice>? replayChoices = null;
        if (turn <= 1)
        {
            if (!RequiresSolverChoice(player))
                return false;
        }
        else if (!SolverController.TryGetPlannedTurnSetupChoices(combat, turn, out replayChoices))
        {
            return false;
        }

        Task operation = RunSetupAsync(
            manager,
            turnState,
            player,
            choiceContext,
            combat,
            replayChoices);
        _activeOperation = operation;
        task = operation;
        if (UnattendedAsyncActivityTracker.IsRequestActive)
            task = UnattendedAsyncActivityTracker.Track(task);
        return true;
    }

    public static bool TryInterceptAutoPrePlay(
        CombatManager manager,
        object turnState,
        HookPlayerChoiceContext choiceContext,
        Task setupTask,
        Player player,
        out Task? task)
    {
        task = null;
        if (_invokingOriginalAutoPrePlay
            || _active is not { } active
            || !IsCurrentActivePlan(active)
            || !ReferenceEquals(active.Player, player)
            || !ReferenceEquals(active.Combat, manager.DebugOnlyGetState()))
        {
            return false;
        }
        Task operation = RunAutoPrePlayAsync(
            manager,
            turnState,
            choiceContext,
            setupTask,
            player,
            active);
        _activeOperation = operation;
        task = operation;
        if (UnattendedAsyncActivityTracker.IsRequestActive)
            task = UnattendedAsyncActivityTracker.Track(task);
        return true;
    }

    public static bool IsStoppingSearch => _active?.Interaction.StopRequested == true;

    public static bool IsManaging(CombatState combat)
        => _active is { } active
           && IsCurrentActivePlan(active)
           && ReferenceEquals(active.Combat, combat);

    public static bool IsSearching
        => _deferredSetupCancellation != null
           || _active is { InitialSearch: not null, Result: null, SearchState: 1 }
           || _active is { ManualSearchState: 1 };

    public static SearchMemoryPressureSignal? CurrentMemoryPressureSignal
    {
        get
        {
            ActivePlan? active = Volatile.Read(ref _active);
            return active == null ? null : Volatile.Read(ref active.MemoryPressureSignal);
        }
    }

    public static bool CanApplyCurrentTurn
        => _active is { } active
           && active.Interaction.CurrentTakeoverRequest == null
           && active.Interaction.CanAcceptTakeover
           && (active.SearchState == 1 || active.ManualSearchState == 1)
           && Volatile.Read(ref active.Interaction.Progress)?.CurrentTurnPreview != null;

    public static bool IsApplyingCurrentTurn
        => _active?.Interaction.IsApplyingCurrentTurn == true;

    public static bool CanAdoptCurrentRoute
        => _active is { } active
           && (active.Interaction.StoppedResult != null
               || active.Interaction.CurrentTakeoverRequest == null
                   && active.Interaction.RenderedRouteAdoptionSeed != null
                   && active.Interaction.CanAcceptTakeover
                   && (active.SearchState == 1 || active.ManualSearchState == 1));

    public static bool IsAdoptingCurrentRoute
        => _active?.Interaction.IsAdoptingRoute == true;

    public static void InvalidateRenderedRouteAdoptionSeed()
    {
        if (_active != null)
            _active.Interaction.RenderedRouteAdoptionSeed = null;
    }

    public static void ApplyCurrentTurn()
    {
        if (!CanApplyCurrentTurn || _active is not { } active)
            return;
        active.DeployAfterSetup = true;
        active.TakeoverRequested = true;
        if (!active.Interaction.RequestApplyCurrentTurn())
            return;
        SolverOverlay.RefreshControls();
        Entry.Logger.Info("[CombatSolver/Test] UI_ACTION action=turn_setup_apply_current_turn");
    }

    public static void AdoptCurrentRoute()
    {
        if (_active is not { } active)
            return;
        SolverResult? stoppedResult = active.Interaction.TakeStoppedResult(
            LiveCombatStamp.Capture(active.Combat));
        if (stoppedResult != null)
        {
            if (NGame.Instance is { } host)
                SolverController.ShowTurnSetupResultPreview(host, stoppedResult);
            SolverOverlay.RefreshControls();
            Entry.Logger.Info("[CombatSolver/Test] UI_ACTION action=turn_setup_adopt_stopped_route");
            return;
        }
        SolverRouteAdoptionSeed? seed = active.Interaction.RenderedRouteAdoptionSeed;
        if (seed == null || !active.Interaction.RequestAdoptRoute(seed))
            return;
        SolverOverlay.RefreshControls();
        Entry.Logger.Info(
            $"[CombatSolver/Test] UI_ACTION action=turn_setup_adopt_current_route " +
            $"candidate_version={seed.CandidateVersion}");
    }

    internal static int ManualRecalculationCompletedCountForTesting
        => _active?.ManualRecalculationCompletedCount ?? 0;

    internal static bool IsInitialChoiceSearchPendingForTesting(CombatState combat)
        => _active is { Result: null, SearchState: 1 } active
           && IsCurrentActivePlan(active)
           && ReferenceEquals(active.Combat, combat)
           && active.Choices.HasVisibleRequest;

    internal static bool TakeoverRequestedForTesting
        => _active?.TakeoverRequested == true;

    public static bool HasPendingPlannedChoice(CombatState combat)
        => _active is { PlannedChoices: not null } active
           && IsCurrentActivePlan(active)
           && ReferenceEquals(active.Combat, combat)
           && active.Choices.IsVisibleChoicePending;

    public static bool CanTakeOverTurnSetup(CombatState combat)
        => _active is { } active
           && IsCurrentActivePlan(active)
           && ReferenceEquals(active.Combat, combat)
           && HasUnresolvedVisibleChoice(active);

    public static bool TryContinuePlannedChoice(
        NGame host,
        CombatState combat,
        bool deployAfterSetup)
    {
        if (_active is not { } active
            || !IsCurrentActivePlan(active)
            || !ReferenceEquals(active.Combat, combat)
            || !HasUnresolvedVisibleChoice(active))
        {
            return false;
        }

        active.DeployAfterSetup |= deployAfterSetup;
        active.TakeoverRequested = true;
        IReadOnlyList<PlanCardChoice>? plannedChoices = active.PlannedChoices;
        if (plannedChoices != null)
            StartReplayDriver(active, host);
        string takeoverMode = deployAfterSetup
            ? "single_step"
            : SolverController.FullAutoEnabled
                ? "full_auto"
                : "route_only";
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_SETUP_TAKEOVER turn={active.Player.PlayerCombatState!.TurnNumber} " +
            $"mode={takeoverMode} choices={plannedChoices?.Count ?? 0} " +
            $"queued={(plannedChoices == null).ToString().ToLowerInvariant()}");
        return true;
    }

    public static bool TryQueueManualRecalculation(CombatState combat)
    {
        if (_active is not { } active
            || !IsCurrentActivePlan(active)
            || !ReferenceEquals(active.Combat, combat))
        {
            return false;
        }
        active.DeployAfterSetup = false;
        SolverController.QueueManualSearchAfterTurnSetup();
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_SETUP_MANUAL_RECALCULATE_QUEUED " +
            $"turn={active.Player.PlayerCombatState!.TurnNumber}");
        return true;
    }

    public static bool TryRecalculatePendingChoice(NGame host, CombatState combat)
    {
        if (_active is not { InitialSearch: not null } active
            || !IsCurrentActivePlan(active)
            || !ReferenceEquals(active.Combat, combat)
            || !HasUnresolvedVisibleChoice(active)
            || active.ReplayDrivingStarted
            || active.TakeoverRequested)
        {
            return false;
        }

        active.DeployAfterSetup = false;
        active.ManualRecalculationRequested.TrySetResult();
        SolverOverlay.ShowSearching(
            host,
            active.Player.PlayerCombatState!.TurnNumber,
            deployWhenReady: false,
            SolverController.ReviewedWorldlinesTotal);
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_SETUP_MANUAL_RECALCULATE_REQUESTED " +
            $"turn={active.Player.PlayerCombatState.TurnNumber} native_choice_pending=true");
        return true;
    }

    internal static async Task SubmitEmptyChoiceForTesting(NGame host, CombatState combat)
    {
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("只有无人值守测试可以模拟回合开始选择。");
        if (_active is not { PlannedChoices: not null } active
            || !IsCurrentActivePlan(active)
            || !ReferenceEquals(active.Combat, combat)
            || !active.Choices.IsVisibleChoicePending)
        {
            throw new InvalidOperationException("回合开始选择尚未准备好，无法模拟玩家跳过。");
        }

        await active.Choices.SelectVisibleCardsForTesting(
            host,
            [],
            active.Token);
    }

    public static void PrepareForSceneExit()
    {
        if (_active == null)
            return;
        _cancellation?.Cancel();
        NativeChoiceRuntime.CancelActiveHandSelectionForSceneExit();
    }

    private static bool HasUnresolvedVisibleChoice(ActivePlan active)
        => active.Choices.IsVisibleChoicePending
           || (active.Result == null
               && active.SearchState == 1
               && active.Choices.HasVisibleRequest);

    public static Task Reset(string reason)
    {
        CancellationTokenSource? cancellation = _cancellation;
        Task operation = _activeOperation;
        cancellation?.Cancel();
        _cancellation = null;
        if (cancellation != null)
        {
            Interlocked.CompareExchange(
                ref _deferredSetupCancellation,
                null,
                cancellation);
        }
        _activeOperation = Task.CompletedTask;
        DisposeActive();
        if (SolverSettings.Current.EnableDetailedDiagnosticLogs)
            Entry.Logger.Info($"[CombatSolver/Debug] TURN_SETUP_RESET reason={reason}");
        return ReleaseCancellationAfterOperationAsync(operation, cancellation);
    }

    private static async Task ReleaseCancellationAfterOperationAsync(
        Task operation,
        CancellationTokenSource? cancellation)
    {
        await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        cancellation?.Dispose();
    }

    public static void CancelForSolverDisabled()
    {
        // Before the solver core starts, cancellation would make Harmony return from the
        // intercepted setup without ever invoking the game's original setup. Let the barrier
        // open and take the main-thread native fallback instead.
        if (!ReferenceEquals(_deferredSetupCancellation, _cancellation))
            _cancellation?.Cancel();
        _active?.Choices.ReleaseVisibleSurface();
    }

    public static bool TryStopSearchAtCurrentRoute()
    {
        if (_active is not { InitialSearch: not null, Result: null, SearchState: 1 } active
            || active.Interaction.RenderedRouteAdoptionSeed is not { } seed)
        {
            return false;
        }
        return active.Interaction.RequestAdoptRoute(seed, stopAfterResult: true);
    }

    public static bool StopSearchByUser()
    {
        if (ReferenceEquals(_deferredSetupCancellation, _cancellation)
            && _deferredSetupCancellation != null)
        {
            // SolverController has already set AutomaticSearchPaused. The deferred callback
            // observes that state and runs the native setup rather than starting a search.
            return true;
        }
        if (_active is not { InitialSearch: not null, Result: null, SearchState: 1 } active)
            return false;
        Interlocked.Exchange(ref active.SearchState, 2);
        _cancellation?.Cancel();
        active.Choices.ReleaseVisibleSurface();
        active.PlanReady.TrySetResult();
        return true;
    }

    private static async Task RunSetupAsync(
        CombatManager manager,
        object turnState,
        Player player,
        HookPlayerChoiceContext choiceContext,
        CombatState combat,
        IReadOnlyList<PlanCardChoice>? replayChoices)
    {
        if (_active != null)
            throw new InvalidOperationException("上一项回合准备选牌事务尚未结束。");

        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        CancellationTokenSource cancellation = _cancellation;
        CancellationToken token = _cancellation.Token;
        Task rootCaptureBarrier = SearchGcPolicy.CaptureRootSnapshotBarrier();
        if (!rootCaptureBarrier.IsCompleted)
        {
            NGame host = NGame.Instance
                ?? throw new InvalidOperationException("回合准备根快照等待缺少游戏节点。");
            SolverDispatcher.Ensure(host);
            Entry.Logger.Info("[CombatSolver/Test] TURN_SETUP_ROOT_CAPTURE_DEFERRED");
            _deferredSetupCancellation = cancellation;
            try
            {
                await ResumeSetupAfterRootCaptureBarrierAsync(
                    manager,
                    turnState,
                    player,
                    choiceContext,
                    combat,
                    replayChoices,
                    rootCaptureBarrier,
                    cancellation);
            }
            finally
            {
                Interlocked.CompareExchange(
                    ref _deferredSetupCancellation,
                    null,
                    cancellation);
            }
            return;
        }
        await RunSetupAfterRootCaptureBarrierAsync(
            manager,
            turnState,
            player,
            choiceContext,
            combat,
            replayChoices,
            token);
    }

    private static async Task ResumeSetupAfterRootCaptureBarrierAsync(
        CombatManager manager,
        object turnState,
        Player player,
        HookPlayerChoiceContext choiceContext,
        CombatState combat,
        IReadOnlyList<PlanCardChoice>? replayChoices,
        Task barrier,
        CancellationTokenSource cancellation)
    {
        CancellationToken token = cancellation.Token;
        try
        {
            await barrier.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }

        TaskCompletionSource<Task> dispatched = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        SolverDispatcher.Post(() =>
        {
            Interlocked.CompareExchange(
                ref _deferredSetupCancellation,
                null,
                cancellation);
            if (token.IsCancellationRequested
                || !ReferenceEquals(_cancellation, cancellation)
                || !ReferenceEquals(manager.DebugOnlyGetState(), combat))
            {
                dispatched.TrySetResult(Task.CompletedTask);
                return;
            }
            try
            {
                if (SolverController.SolverDisabled || SolverController.AutomaticSearchPaused)
                {
                    Entry.Logger.Info(
                        "[CombatSolver/Test] TURN_SETUP_ROOT_CAPTURE_FALLBACK reason=solver_inactive");
                    dispatched.TrySetResult(InvokeOriginalSetupAsync(
                        manager,
                        turnState,
                        player,
                        choiceContext));
                    return;
                }
                Entry.Logger.Info("[CombatSolver/Test] TURN_SETUP_ROOT_CAPTURE_RESUMED");
                dispatched.TrySetResult(RunSetupAfterRootCaptureBarrierAsync(
                    manager,
                    turnState,
                    player,
                    choiceContext,
                    combat,
                    replayChoices,
                    token));
            }
            catch (Exception ex)
            {
                dispatched.TrySetException(ex);
            }
        });

        // A canceled setup still waits for this queued callback to drop its captured combat
        // references. Once the callback begins the returned core task remains part of Reset's
        // release barrier until it has fully unwound.
        Task continuation = await dispatched.Task.ConfigureAwait(false);
        await continuation.ConfigureAwait(false);
    }

    private static async Task RunSetupAfterRootCaptureBarrierAsync(
        CombatManager manager,
        object turnState,
        Player player,
        HookPlayerChoiceContext choiceContext,
        CombatState combat,
        IReadOnlyList<PlanCardChoice>? replayChoices,
        CancellationToken token)
    {
        InitialSearchContext initialSearch;
        try
        {
            SolverSettingsSnapshot settings = SolverSettings.Capture();
            SolverDisplayNames displayNames = SolverDisplayNames.Capture(combat);
            BattleDamageSnapshot battleDamage = BattleDamageTracker.Observe(combat);
            SearchInteractionState interaction = new();
            SearchPolicySnapshot searchPolicy = SolverController.CaptureSearchPolicy(
                settings,
                combat,
                includeTurnSetup: true,
                theftPolicy: SolverController.ResolveTheftPolicy(combat),
                interaction: interaction);
            long rootCaptureAllocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
            CombatRootSnapshot rootSnapshot;
            try
            {
                rootSnapshot = CombatRootSnapshot.Capture(combat);
            }
            finally
            {
                SearchGcPolicy.ReportCombatLifecycleAllocation(
                    Math.Max(
                        0,
                        GC.GetTotalAllocatedBytes(precise: false) - rootCaptureAllocatedAtStart),
                    "turn_setup_root_snapshot",
                    settings.EnableNoGcRegion);
            }
            initialSearch = new InitialSearchContext(
                displayNames,
                settings,
                battleDamage,
                searchPolicy,
                rootSnapshot);
        }
        catch
        {
            SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Failed);
            throw;
        }
        NativeChoiceSession choices = NativeChoiceRuntime.Begin(
            combat,
            player,
            $"turn_setup:{player.PlayerCombatState!.TurnNumber}");
        ActivePlan active = new(
            combat,
            player,
            choices,
            token,
            initialSearch,
            replayChoices);
        _active = active;
        NGame host = NGame.Instance
            ?? throw new InvalidOperationException("回合准备选牌搜索缺少游戏节点。");
        CombatRootSnapshot capturedRoot = initialSearch.RootSnapshot;
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_SETUP_ROOT_CAPTURE turn={player.PlayerCombatState.TurnNumber} " +
            $"elapsed_ms={capturedRoot.CaptureElapsedMilliseconds:F3} " +
            $"cards={capturedRoot.CapturedCardCount} powers={capturedRoot.CapturedPowerCount} " +
            $"listeners={capturedRoot.CapturedHookListenerCount} " +
            $"run_mod_subscribers={capturedRoot.CapturedRunModSubscriberCount} " +
            $"combat_mod_subscribers={capturedRoot.CapturedCombatModSubscriberCount} " +
            $"base_lib_card_modifiers={capturedRoot.CapturedBaseLibCardModifiers}");
        if (replayChoices != null)
        {
            SolverController.ShowTurnSetupContinuationPreview(
                host,
                active.Combat,
                player.PlayerCombatState.TurnNumber);
        }
        LogSetupPowers(player, "before_original");
        Task originalSetup = InvokeOriginalSetupAsync(manager, turnState, player, choiceContext);
        bool keepActiveForAutoPrePlay = false;
        try
        {
            bool ownsVisibleChoice = replayChoices == null
                ? await TryStartSearchAfterVisibleChoiceAsync(active, host, originalSetup, "setup")
                : await PrepareReplayChoiceSurfaceAsync(active, host, originalSetup, "setup");
            if (ownsVisibleChoice)
                await AwaitPhaseWithManualRecalculationsAsync(active, host, originalSetup);
            else
                await active.Choices.AwaitPhaseAsync(originalSetup);
            if (!IsCurrentActivePlan(active))
                return;
            keepActiveForAutoPrePlay = true;
        }
        catch (OperationCanceledException) when (
            active.Token.IsCancellationRequested
            || SolverController.SolverDisabled
            || SolverController.AutomaticSearchPaused)
        {
            if (ReferenceEquals(_active, active)
                && SolverController.IsCurrentCombatLifecycle(
                    active.Combat,
                    active.LifecycleGeneration))
            {
                active.Choices.ReleaseVisibleSurface();
            }
            await originalSetup.ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext
                | ConfigureAwaitOptions.SuppressThrowing);
            DisposeActive(active);
            return;
        }
        catch (Exception ex)
        {
            if (!CanPublishForPlan(active))
            {
                await originalSetup.ConfigureAwait(
                    ConfigureAwaitOptions.ContinueOnCapturedContext
                    | ConfigureAwaitOptions.SuppressThrowing);
                DisposeActive(active);
                return;
            }
            SolverController.RecordTurnSetupFailure(
                active.Combat,
                active.LifecycleGeneration,
                ex,
                initialSearch.SearchPolicy.MaxDegreeOfParallelism > 1);
            DisposeActive(active);
            throw;
        }
        finally
        {
            await originalSetup.ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext
                | ConfigureAwaitOptions.SuppressThrowing);
            if (!keepActiveForAutoPrePlay)
                DisposeActive(active);
        }
    }

    private static async Task InvokeOriginalSetupAsync(
        CombatManager manager,
        object turnState,
        Player player,
        HookPlayerChoiceContext choiceContext)
    {
        _invokingOriginalSetup = true;
        try
        {
            Task original = (Task)(SetupPlayerTurnMethod.Invoke(
                manager,
                [turnState, player, choiceContext])
                ?? throw new InvalidOperationException("SetupPlayerTurn 没有返回任务。"));
            _invokingOriginalSetup = false;
            await original;
        }
        finally
        {
            _invokingOriginalSetup = false;
        }
    }

    private static async Task RunAutoPrePlayAsync(
        CombatManager manager,
        object turnState,
        HookPlayerChoiceContext choiceContext,
        Task setupTask,
        Player player,
        ActivePlan active)
    {
        Task original = Task.CompletedTask;
        try
        {
            _invokingOriginalAutoPrePlay = true;
            original = (Task)(RunAutoPrePlayPhaseMethod.Invoke(
                manager,
                [turnState, choiceContext, setupTask, player])
                ?? throw new InvalidOperationException("RunAutoPrePlayPhase 没有返回任务。"));
            _invokingOriginalAutoPrePlay = false;
            NGame host = NGame.Instance
                ?? throw new InvalidOperationException("回合准备自动出牌缺少游戏节点。");
            bool ownsVisibleChoice = active.ReplayChoices == null
                ? await TryStartSearchAfterVisibleChoiceAsync(active, host, original, "auto_pre_play")
                : await PrepareReplayChoiceSurfaceAsync(active, host, original, "auto_pre_play");
            if (ownsVisibleChoice)
                await AwaitPhaseWithManualRecalculationsAsync(active, host, original);
            else
                await active.Choices.AwaitPhaseAsync(original);
            if (!IsCurrentActivePlan(active))
                return;
            await active.Choices.CompleteAsync();
            if (!IsCurrentActivePlan(active))
                return;
            LogSetupPowers(player, "after_original");

            if (SolverController.ManualSearchAfterTurnSetupRequested)
            {
                Entry.Logger.Info(
                    $"[CombatSolver/Test] TURN_SETUP_MANUAL_RECALCULATE_RESUME " +
                    $"turn={player.PlayerCombatState!.TurnNumber}");
                DisposeActive(active);
                await SolverController.ResumeAfterTurnSetupAsync(
                    host,
                    active.Combat,
                    active.LifecycleGeneration,
                    player.PlayerCombatState.TurnNumber,
                    token: active.Token);
                return;
            }

            if (active.ReplayChoices != null && !active.ManualRecalculated)
            {
                bool solverDriven = active.ReplayDrivingStarted;
                bool deployAfterSetup = active.DeployAfterSetup;
                Entry.Logger.Info(solverDriven
                    ? $"[CombatSolver/Test] TURN_SETUP_PLAN_REPLAYED turn={player.PlayerCombatState!.TurnNumber} " +
                      $"choices={active.ReplayChoices.Count} search=false"
                    : $"[CombatSolver/Test] TURN_SETUP_PLAYER_CHOICE_COMPLETED turn={player.PlayerCombatState!.TurnNumber} " +
                      "search=false");
                DisposeActive(active);
                await SolverController.ResumeAfterTurnSetupAsync(
                    host,
                    active.Combat,
                    active.LifecycleGeneration,
                    player.PlayerCombatState.TurnNumber,
                    deployWhenReady: deployAfterSetup,
                    waitForDeploymentDelay: solverDriven
                        && (deployAfterSetup || SolverController.FullAutoEnabled),
                    token: active.Token);
                return;
            }

            if (active.Result == null)
            {
                Entry.Logger.Info(
                    $"[CombatSolver/Test] TURN_SETUP_NO_VISIBLE_CHOICE turn={player.PlayerCombatState!.TurnNumber}");
                DisposeActive(active);
                await SolverController.ResumeAfterTurnSetupAsync(
                    host,
                    active.Combat,
                    active.LifecycleGeneration,
                    player.PlayerCombatState.TurnNumber,
                    waitForDeploymentDelay: SolverController.FullAutoEnabled,
                    token: active.Token);
                return;
            }

            if (!IsCurrentActivePlan(active))
                return;
            ContinuationStamp actual = ContinuationStamp.CaptureLive(active.Combat);
            ContinuationStamp expected = active.Result.TurnSetupPlayState
                ?? throw new InvalidOperationException("回合准备选牌搜索缺少 Play 阶段状态戳。");
            if (expected != actual)
            {
                SolverController.RecordTurnSetupStateMismatch(
                    active.Combat,
                    active.LifecycleGeneration,
                    expected.DescribeFirstDifference(actual));
                Entry.Logger.Warn(
                    $"[CombatSolver/Test] TURN_SETUP_STATE_MISMATCH turn={player.PlayerCombatState!.TurnNumber} " +
                    expected.DescribeFirstDifference(actual));
                DisposeActive(active);
                await SolverController.ResumeAfterTurnSetupAsync(
                    host,
                    active.Combat,
                    active.LifecycleGeneration,
                    player.PlayerCombatState.TurnNumber,
                    token: active.Token);
                return;
            }
            Entry.Logger.Info(
                $"[CombatSolver/Test] TURN_SETUP_STATE_MATCH turn={player.PlayerCombatState!.TurnNumber} " +
                "validation=exact_state_text");
            bool deployInitialResult = active.DeployAfterSetup;
            DisposeActive(active);
            if (!SolverController.ActivateTurnSetupResult(
                    host,
                    active.Combat,
                    active.LifecycleGeneration,
                    active.Result))
                throw new InvalidOperationException("回合准备搜索结果在精确状态匹配后仍无法激活。");
            if (deployInitialResult && !SolverController.FullAutoEnabled)
                SolverController.StartDeploymentAfterTurnSetup(host, active.Combat, active.Result);
        }
        catch (OperationCanceledException) when (
            active.Token.IsCancellationRequested || !CanPublishForPlan(active))
        {
            return;
        }
        catch (Exception ex)
        {
            if (!CanPublishForPlan(active))
                return;
            SolverController.RecordTurnSetupFailure(
                active.Combat,
                active.LifecycleGeneration,
                ex,
                active.InitialSearch?.SearchPolicy.MaxDegreeOfParallelism > 1);
            throw;
        }
        finally
        {
            await original.ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext
                | ConfigureAwaitOptions.SuppressThrowing);
            _invokingOriginalAutoPrePlay = false;
            DisposeActive(active);
        }
    }

    private static async Task AwaitPhaseWithManualRecalculationsAsync(
        ActivePlan active,
        NGame host,
        Task phaseTask)
    {
        if (active.ReplayDrivingStarted)
        {
            await active.Choices.AwaitPhaseAsync(phaseTask);
            return;
        }

        while (!phaseTask.IsCompleted)
        {
            TaskCompletionSource requested = active.ManualRecalculationRequested;
            Task winner = await Task.WhenAny(phaseTask, requested.Task);
            if (phaseTask.IsCompleted || ReferenceEquals(winner, phaseTask))
                break;
            active.ManualRecalculationRequested = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!IsCurrentActivePlan(active))
                break;
            await RecalculatePendingChoiceAsync(active, host);
        }
        await active.Choices.AwaitPhaseAsync(phaseTask);
    }

    private static async Task RecalculatePendingChoiceAsync(ActivePlan active, NGame host)
    {
        InitialSearchContext original = active.InitialSearch
            ?? throw new InvalidOperationException("回合开始选项重算缺少选择前搜索根。");
        await active.Choices.LockVisibleSurfaceForSearchAsync(host, active.Token);
        if (!IsCurrentActivePlan(active))
            return;

        SolverSettingsSnapshot settings = SolverSettings.Capture();
        InitialSearchContext refreshed = new(
            SolverDisplayNames.Capture(active.Combat),
            settings,
            BattleDamageTracker.Observe(active.Combat),
            SolverController.CaptureSearchPolicy(
                settings,
                active.Combat,
                includeTurnSetup: true,
                theftPolicy: SolverController.ResolveTheftPolicy(active.Combat),
                interaction: active.Interaction),
            original.RootSnapshot);
        Volatile.Write(ref active.MemoryPressureSignal, refreshed.SearchPolicy.MemoryPressureSignal);
        int turn = active.Player.PlayerCombatState!.TurnNumber;
        active.Interaction.ResetForSearch();
        Interlocked.Exchange(ref active.ManualSearchState, 1);
        SolverOverlay.ShowSearching(
            host,
            turn,
            deployWhenReady: false,
            SolverController.ReviewedWorldlinesTotal);
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_SETUP_MANUAL_RECALCULATE_START turn={turn} " +
            "native_choice_pending=true");
        try
        {
            Task<SolverResult> solveTask = Task.Run(() =>
            {
                Thread worker = Thread.CurrentThread;
                ThreadPriority previousPriority = worker.Priority;
                worker.Priority = ThreadPriority.BelowNormal;
                try
                {
                    using IDisposable gcPolicy = SearchGcPolicy.EnterLowLatencySearch(
                        refreshed.Settings.EnableNoGcRegion,
                        refreshed.Settings.NoGcRegionBudgetBytes,
                        refreshed.SearchPolicy.MemoryPressureSignal,
                        active.Token);
                    SolverResult result = CombatSearchCoordinator.Solve(
                        refreshed.RootSnapshot,
                        refreshed.DisplayNames,
                        refreshed.BattleDamage,
                        refreshed.SearchPolicy,
                        active.Token,
                        active.Interaction.PublishProgress);
                    return active.Interaction.FinalizeWorkerResult(result);
                }
                finally
                {
                    worker.Priority = previousPriority;
                }
            }, active.Token);
            while (!solveTask.IsCompleted)
            {
                RefreshSearchProgress(active);
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                active.Token.ThrowIfCancellationRequested();
            }
            SolverResult result = await solveTask;
            if (!IsCurrentActivePlan(active))
                return;
            if (result.TurnSetupChoices.Count == 0)
                throw new InvalidOperationException("回合开始选项重算没有返回原生选牌计划。");

            active.Result = result;
            active.ManualRecalculated = true;
            active.ManualRecalculationCompletedCount++;
            active.Choices.RecordPlanReady();
            SolverController.ShowTurnSetupResultPreview(host, result);
            if (active.TakeoverRequested)
                StartReplayDriver(active, host);
            Entry.Logger.Info(
                $"[CombatSolver/Test] TURN_SETUP_MANUAL_RECALCULATE_RESULT turn={turn} " +
                $"choices={result.TurnSetupChoices.Count} expanded={result.ExpandedNodes} " +
                $"searched_turns={result.SearchedTurns}");
        }
        catch (OperationCanceledException) when (
            active.Token.IsCancellationRequested || !CanPublishForPlan(active))
        {
            return;
        }
        catch
        {
            if (CanPublishForPlan(active))
                SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Failed);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref active.ManualSearchState, 0);
            if (!active.ReplayDrivingStarted)
                active.Choices.ReleaseVisibleSurface();
        }
    }

    private static async Task<bool> TryStartSearchAfterVisibleChoiceAsync(
        ActivePlan active,
        NGame host,
        Task phaseTask,
        string phase)
    {
        if (active.Result != null)
            return false;
        InitialSearchContext initialSearch = active.InitialSearch
            ?? throw new InvalidOperationException("已有路线的回合准备不得启动新搜索。");
        if (!await active.Choices.WaitForFirstVisibleSurfaceAsync(host, phaseTask, active.Token))
            return false;
        if (!IsCurrentActivePlan(active))
            return false;
        if (Interlocked.CompareExchange(ref active.SearchState, 1, 0) != 0)
        {
            await active.PlanReady.Task.WaitAsync(active.Token);
            return false;
        }
        active.Interaction.ResetForSearch();
        int turn = active.Player.PlayerCombatState!.TurnNumber;
        active.Choices.RecordSearchStarted();
        SolverOverlay.ShowSearching(
            host,
            turn,
            deployWhenReady: false,
            SolverController.ReviewedWorldlinesTotal);
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_SETUP_SEARCH_START turn={turn} phase={phase} " +
            "after_native_choice_visible=true");
        SolverResult result;
        try
        {
            Task<SolverResult> solveTask = Task.Run(() =>
            {
                Thread worker = Thread.CurrentThread;
                ThreadPriority previousPriority = worker.Priority;
                worker.Priority = ThreadPriority.BelowNormal;
                try
                {
                    using IDisposable gcPolicy = SearchGcPolicy.EnterLowLatencySearch(
                        initialSearch.Settings.EnableNoGcRegion,
                        initialSearch.Settings.NoGcRegionBudgetBytes,
                        initialSearch.SearchPolicy.MemoryPressureSignal,
                        active.Token);
                    SolverResult result = CombatSearchCoordinator.Solve(
                        initialSearch.RootSnapshot,
                        initialSearch.DisplayNames,
                        initialSearch.BattleDamage,
                        initialSearch.SearchPolicy,
                        active.Token,
                        active.Interaction.PublishProgress);
                    return active.Interaction.FinalizeWorkerResult(result);
                }
                finally
                {
                    worker.Priority = previousPriority;
                }
            }, active.Token);
            while (!solveTask.IsCompleted)
            {
                RefreshSearchProgress(active);
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                active.Token.ThrowIfCancellationRequested();
            }
            result = await solveTask;
        }
        catch (OperationCanceledException)
        {
            bool ownsCurrentLifecycle = ReferenceEquals(_active, active)
                && SolverController.IsCurrentCombatLifecycle(
                    active.Combat,
                    active.LifecycleGeneration);
            if (active.SearchState != 2 && ownsCurrentLifecycle)
                SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Canceled);
            if (!ownsCurrentLifecycle)
            {
                active.PlanReady.TrySetResult();
                return false;
            }
            if (SolverController.SolverDisabled || SolverController.AutomaticSearchPaused)
            {
                active.Choices.ReleaseVisibleSurface();
                active.PlanReady.TrySetResult();
                return false;
            }
            active.PlanReady.TrySetCanceled(active.Token);
            throw;
        }
        catch (Exception ex)
        {
            if (!IsCurrentActivePlan(active))
            {
                active.PlanReady.TrySetResult();
                return false;
            }
            SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Failed);
            active.PlanReady.TrySetException(ex);
            throw;
        }

        if (!IsCurrentActivePlan(active))
        {
            active.PlanReady.TrySetResult();
            return false;
        }
        if (SolverController.SolverDisabled
            || SolverController.AutomaticSearchPaused && !active.Interaction.StopRequested)
        {
            if (active.SearchState != 2 && ReferenceEquals(_active, active))
                SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Canceled);
            active.Choices.ReleaseVisibleSurface();
            active.PlanReady.TrySetResult();
            return false;
        }
        if (result.TurnSetupChoices.Count == 0)
        {
            if (ReferenceEquals(_active, active))
                SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Failed);
            throw new InvalidOperationException("原生页面已经请求选牌，但回合准备搜索没有返回计划选择。");
        }
        active.Result = result;
        active.Choices.RecordPlanReady();
        SolverController.ShowTurnSetupResultPreview(host, result);
        if (active.Interaction.StopRequested)
        {
            active.Interaction.PreserveStoppedResult(result, LiveCombatStamp.Capture(active.Combat));
            SolverOverlay.ShowSearchStopped(host);
        }
        else if (active.TakeoverRequested)
            StartReplayDriver(active, host);
        else
            active.Choices.ReleaseVisibleSurface();
        active.PlanReady.TrySetResult();
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_SETUP_PLAN turn={turn} choices={result.TurnSetupChoices.Count} " +
            $"expanded={result.ExpandedNodes} searched_turns={result.SearchedTurns} " +
            "awaiting_user_start=true");
        return true;
    }

    private static async Task<bool> PrepareReplayChoiceSurfaceAsync(
        ActivePlan active,
        NGame host,
        Task phaseTask,
        string phase)
    {
        if (active.ReplaySurfacePrepared)
            return false;
        if (!await active.Choices.WaitForFirstVisibleSurfaceAsync(host, phaseTask, active.Token))
            return false;
        if (!IsCurrentActivePlan(active) || active.ReplaySurfacePrepared)
            return false;

        active.ReplaySurfacePrepared = true;
        int turn = active.Player.PlayerCombatState!.TurnNumber;
        if (SolverController.FullAutoEnabled)
            StartReplayDriver(active, host);
        else
            active.Choices.ReleaseVisibleSurface();
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_SETUP_PLAN_READY turn={turn} " +
            $"source=continuation choices={active.ReplayChoices!.Count} search=false " +
            $"phase={phase} visible=true " +
            $"driving={active.ReplayDrivingStarted.ToString().ToLowerInvariant()}");
        return true;
    }

    private static bool RequiresSolverChoice(Player player)
    {
        int turn = player.PlayerCombatState?.TurnNumber ?? 0;
        if (player.Relics.Any(relic => !relic.IsMelted
                && (relic is ToastyMittens
                    || turn <= 1 && relic is Toolbox or ChoicesParadox or GamblingChip)))
        {
            return true;
        }
        if (player.Creature.Powers.Any(power => power.Amount > 0
                && power is ForegoneConclusionPower
                    or EntropyPower
                    or ToolsOfTheTradePower
                    or TyrannyPower
                    or MayhemPower
                    or StratagemPower))
        {
            return true;
        }
        PlayerCombatState state = player.PlayerCombatState
            ?? throw new InvalidOperationException("回合准备选牌检测时玩家没有战斗牌堆。");
        return state.Hand.Cards
            .Concat(state.DrawPile.Cards)
            .Concat(state.DiscardPile.Cards)
            .Concat(state.ExhaustPile.Cards)
            .Concat(state.PlayPile.Cards)
            .Any(card => card.Enchantment is Imbued);
    }

    private static void LogSetupPowers(Player player, string stage)
    {
        if (!SolverSettings.Current.EnableDetailedDiagnosticLogs)
            return;
        ICombatState combat = player.Creature.CombatState
            ?? throw new InvalidOperationException("回合准备详细诊断时玩家不在战斗中。");
        decimal handDraw = Hook.ModifyHandDraw(
            combat,
            player,
            CombatManager.baseHandDrawCount,
            out IEnumerable<AbstractModel> modifiers);
        Entry.Logger.Info(
            $"[CombatSolver/Debug] TURN_SETUP_POWERS turn={player.PlayerCombatState?.TurnNumber ?? 0} " +
            $"stage={stage} max_hand={RitsuLibFramework.GetMaxHandSize(player)} " +
            $"hand_draw={handDraw:0.##} " +
            $"hand_draw_modifiers={string.Join(',', modifiers.Select(model => model.Id.Entry))} " +
            $"powers={string.Join(',', player.Creature.Powers.Select(power =>
                $"{power.Id.Entry}:{power.Amount}/{power.AmountOnTurnStart}"))}");
    }

    private static void StartReplayDriver(ActivePlan active, NGame host)
    {
        if (active.ReplayDrivingStarted)
            return;
        IReadOnlyList<PlanCardChoice> replayChoices = active.PlannedChoices
            ?? throw new InvalidOperationException("回合准备接管缺少既有路线选择。");
        active.ReplayDrivingStarted = true;
        active.Choices.SetPlanAndStartDriving(host, replayChoices, active.Token);
    }

    private static void DisposeActive(ActivePlan? expected = null)
    {
        ActivePlan? active = expected ?? _active;
        if (active == null)
            return;
        if (ReferenceEquals(_active, active))
            _active = null;
        if (Interlocked.Exchange(ref active.DisposeState, 1) == 0)
            active.Choices.Dispose();
    }
}
