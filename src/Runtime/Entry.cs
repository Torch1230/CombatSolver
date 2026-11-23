using System.Reflection;
using CombatSolver.Api;
using CombatSolver.HotSearch;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.Interop;
using STS2RitsuLib.Patching.Core;
using Godot;

namespace CombatSolver;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    public const string ModId = "CombatSolver";

    public static CombatSolverLog Logger { get; private set; } = null!;
    public static bool Enabled { get; private set; } = true;

    /// <summary>
    /// 本进程是否是预战预报的隔离 worker —— 由 <see cref="PreCombatForecastWorker"/> 启动的
    /// 无头游戏进程（子进程环境变量 <c>COMBATSOLVER_PRECOMBAT_WORKER=1</c>）。
    ///
    /// 该进程只负责在隔离的跑局镜像里跑模拟：它没有可用的战斗 UI，也不允许嵌套发起预报。
    /// 因此凡是"只对真人玩家有意义"的运行时副作用（战斗覆盖层、UI 监控）都必须用这个开关短路。
    ///
    /// 环境变量由父进程在启动子进程时一次性注入、进程生命周期内不变，故读一次即可。
    /// 这里是该语义的**唯一权威实现**，不要在别处重复解析同一个环境变量。
    /// </summary>
    public static bool IsPreCombatWorker { get; } =
        string.Equals(
            // 全限定：本文件 using Godot，直接写 Environment 会撞 Godot.Environment。
            System.Environment.GetEnvironmentVariable("COMBATSOLVER_PRECOMBAT_WORKER"),
            "1",
            StringComparison.Ordinal);

    public static void Initialize()
    {
        Logger = new CombatSolverLog(Path.Combine(OS.GetUserDataDir(), "logs", "CombatSolver"));
        try
        {
            PreCombatForecastWorker.PinMainProcessModSources();
        }
        catch (Exception ex)
        {
            Logger.Warn(
                $"[CombatSolver/PreCombatApi] MOD_SOURCE_PINNING_UNAVAILABLE error={ex}");
        }
        SolverSettings.Load();
        SolverUiTokens.ConfigureTheme(SolverSettings.Current.OverlayTheme);
        SolverController.ApplyPersistentSettings(SolverSettings.Capture());
        ModTypeDiscoveryHub.RegisterModAssembly(ModId, Assembly.GetExecutingAssembly());
        RitsuLibFramework.SubscribeLifecycle<CombatStartingEvent>(evt => { RunStatistics.Battle(evt.CombatState); SolverController.BeginCombat(evt.CombatState); });
        RitsuLibFramework.SubscribeLifecycle<CombatEndedEvent>(_ => SolverController.Reset("combat_ended"));
        CombatManager.Instance.TurnStarted += OnTurnStarted;

        var patcher = RitsuLibFramework.CreatePatcher(ModId, "combat-solver", "战斗路线求解器");
        patcher.RegisterPatch<PlayerTurnSetupPatch>();
        patcher.RegisterPatch<PlayerTurnAutoPrePlayPatch>();
        patcher.RegisterPatch<PlayerTurnSetupSceneExitPatch>();
        patcher.RegisterPatch<ChooseCardObservationPatch>();
        patcher.RegisterPatch<SimpleGridObservationPatch>();
        patcher.RegisterPatch<RewardGridObservationPatch>();
        patcher.RegisterPatch<CombatPileObservationPatch>();
        patcher.RegisterPatch<HandObservationPatch>();
        patcher.RegisterPatch<HandUpgradeObservationPatch>();
        patcher.RegisterPatch<CombatInstantModePatch>();
        patcher.RegisterPatch<UnattendedTestIsolationPatch>();
        patcher.RegisterPatch<UnattendedHeadlessFtuePatch>();
        patcher.RegisterPatch<CombatReplayRecordingPatch>();
        patcher.RegisterPatch<RunStatisticsNewRunPatch>();
        patcher.RegisterPatch<RunStatisticsLaunchPatch>();
        patcher.RegisterPatch<RunStatisticsEndPatch>();
        patcher.RegisterPatch<UnattendedCombatStartReplayPatch>();
        patcher.RegisterPatch<CombatShowcaseSaveIsolationPatch>();
        patcher.RegisterPatch<CombatShowcaseCleanupPatch>();
        RitsuLibFramework.ApplyRequiredPatcher(patcher, DisableMod);

        if (Enabled)
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version
                ?? throw new InvalidOperationException("CombatSolver 程序集缺少版本号。");
            Logger.Info(
                $"[CombatSolver/Test] INIT mod={version.ToString(3)} simulation_engine=hot_kernel " +
                $"rf_dependency=false async=true auto_turn_search=true full_auto=true " +
                $"live_end_turn_risk_recheck=true default_performance_preset=medium " +
                $"search_beam={SolverSearchProfile.Default.BeamWidth} " +
                $"potion_min_hp_saved={HotSearchConstants.PotionMinimumHpSaved} " +
                $"progress_ui_ms={HotSearchConstants.ProgressUiIntervalMilliseconds} " +
                $"cached_route_rows={HotSearchConstants.SetupValueHorizonTurns}");
            Logger.Info("[CombatSolver/Test] ENGINE embedded=true hot_kernel=true rf_dependency=false");
            Logger.Info("战斗路线求解器已启用。每个玩家新回合会自动后台搜索，也可在面板中执行当前回合路线或开启全自动。");
            NGame? host = NGame.Instance;
            if (host != null)
            {
                SolverDispatcher.Ensure(host);
                host.AddChild(new CombatShowcaseUploadNode());
                host.TreeExiting += PreCombatForecastWorker.StopSessionAtProcessExit;
                host.TreeExiting += Logger.Journal.Dispose;
            }
            UnattendedTestRunner.TryStart(host);
            SolverWarmup.StartBackgroundWarmup();
        }
    }

    private static void OnTurnStarted(CombatState state)
    {
        if (!Enabled
            || state.CurrentSide != CombatSide.Player
            || NGame.Instance == null
            || SolverController.IsMultiplayerSession)
            return;
        if (SolverController.SolverDisabled)
        {
            SolverOverlay.ShowDisabled(NGame.Instance);
            return;
        }
        if (!SolverController.PrepareAutomaticSearchForTurn(NGame.Instance, state))
            return;
        if (PlayerTurnSetupCoordinator.IsManaging(state))
        {
            Logger.Info("[CombatSolver/Test] TURN_STARTED_DEFERRED_TO_SETUP reason=native_choice_pending");
            return;
        }
        if (!UnattendedTestRunner.AutomaticTurnSearchEnabled)
            return;
        int turn = LocalContext.GetMe(state)?.PlayerCombatState?.TurnNumber ?? -1;
        Logger.Info($"[CombatSolver/Test] AUTO_SEARCH_DEFERRED turn={turn} frames=3");
        Task deferredSearch = SolverController.StartCombatDeferredOperation(
            token => RequestAutoSearchAfterVisualSetup(state, turn, token));
        if (UnattendedAsyncActivityTracker.IsRequestActive)
            deferredSearch = UnattendedAsyncActivityTracker.Track(deferredSearch);
        TaskHelper.RunSafely(deferredSearch);
    }

    private static async Task RequestAutoSearchAfterVisualSetup(
        CombatState state,
        int turn,
        CancellationToken token)
    {
        NGame? host = NGame.Instance;
        if (host == null)
            return;
        for (int frame = 0; frame < 60; frame++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            token.ThrowIfCancellationRequested();
            if (frame >= 2 && LocalContext.GetMe(state)?.PlayerCombatState?.Phase == PlayerTurnPhase.Play)
                break;
        }

        await RunManager.Instance.ActionExecutor.FinishedExecutingActions().WaitAsync(token);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        token.ThrowIfCancellationRequested();
        await UnattendedTestRunner.ApplyScheduledStateDriftAsync(state, turn).WaitAsync(token);

        if (SolverController.FullAutoEnabled)
            await SolverController.WaitForTurnStartDeploymentDelayAsync(host, turn, token);

        if (!Enabled
            || SolverController.SolverDisabled
            || SolverController.IsMultiplayerSession
            || !UnattendedTestRunner.AutomaticTurnSearchEnabled
            || !SolverController.ShouldAutomaticallySearchNextTurn
            || SolverController.AutomaticSearchPaused
            || !CombatManager.Instance.IsInProgress
            || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
            || state.CurrentSide != CombatSide.Player
            || LocalContext.GetMe(state)?.PlayerCombatState?.Phase != PlayerTurnPhase.Play
            || LocalContext.GetMe(state)?.PlayerCombatState?.TurnNumber != turn)
        {
            return;
        }
        SolverController.RequestSearch(host, state, SearchReason.AutoTurnStart);
    }

    private static void DisableMod()
    {
        Enabled = false;
    }
}
