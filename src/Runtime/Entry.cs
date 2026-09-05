using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.Interop;
using STS2RitsuLib.Patching.Core;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    public const string ModId = "CombatSolver";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; private set; } = null!;
    public static bool Enabled { get; private set; } = true;

    public static void Initialize()
    {
        Logger = RitsuLibFramework.CreateLogger(ModId);
        SolverSettings.Load();
        SolverUiTokens.ConfigureTheme(SolverSettings.Current.OverlayTheme);
        SolverController.ApplyPersistentSettings(SolverSettings.Capture());
        ModTypeDiscoveryHub.RegisterModAssembly(ModId, Assembly.GetExecutingAssembly());
        RitsuLibFramework.SubscribeLifecycle<CombatStartingEvent>(evt => SolverController.BeginCombat(evt.CombatState));
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
        patcher.RegisterPatch<CombatStateTrackerIsolationPatch>();
        patcher.RegisterPatch<RitsuFreePlayVoidIsolationPatch>();
        patcher.RegisterPatch<RitsuFreePlayBoolIsolationPatch>();
        patcher.RegisterPatch<RitsuFreePlayResolveIsolationPatch>();
        patcher.RegisterPatch<RitsuDefaultCapabilityRegistrationPatch>();
        patcher.RegisterPatch<RitsuEmptyCardTypeFastPathPatch>();
        patcher.RegisterPatch<RitsuEmptyCardRarityFastPathPatch>();
        patcher.RegisterPatch<RitsuEmptyEnergyContributorFastPathPatch>();
        patcher.RegisterPatch<RitsuEmptyEnergyCostFastPathPatch>();
        patcher.RegisterPatch<RitsuEmptyStarContributorFastPathPatch>();
        patcher.RegisterPatch<RitsuEmptyStarCostFastPathPatch>();
        patcher.RegisterPatch<RitsuEmptyCanPlayFastPathPatch>();
        patcher.RegisterPatch<SimulationCardPileLookupPatch>();
        patcher.RegisterPatch<BaseLibCloneConcurrencyPatch>();
        patcher.RegisterPatch<PowerDynamicVarMaterializationGuardPatch>();
        patcher.RegisterPatch<UnattendedTestIsolationPatch>();
        patcher.RegisterPatch<UnattendedHeadlessFtuePatch>();
        patcher.RegisterPatch<UnattendedCombatStartReplayPatch>();
        RitsuLibFramework.ApplyRequiredPatcher(patcher, DisableMod);

        if (Enabled)
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version
                ?? throw new InvalidOperationException("CombatSolver 程序集缺少版本号。");
            Logger.Info($"[CombatSolver/Test] INIT mod={version.ToString(3)} simulation_engine=embedded rf_dependency=false async=true auto_turn_search=true full_auto=true live_end_turn_risk_recheck=true full_auto_combat_end_stop=true sold_hp_basis=route_loss_minus_minimum_reachable_loss sold_hp_threshold_mode=hard_cumulative_budget sold_hp_normal={SolverWeights.NormalSoldHpThreshold} sold_hp_elite={SolverWeights.EliteSoldHpThreshold} sold_hp_boss={SolverWeights.BossSoldHpThreshold} search_session=single_anytime default_performance_preset=medium presets=low_5s_60s+medium_8s_120s+high_12s_180s+very_high_20s_300s+custom short_checkpoint=8s total_deep_budget=120s deep_trigger=hp_loss_or_potion_or_unconfirmed short_beam=24 deep_beam=60 beam_partition=unified potion_beam_opportunity_cost_hp=18 potion_safety_candidate=1 beam_lanes=balanced_defense_offense_utility_pareto_delayed auto_search_min_frames=3 auto_search_wait_actions=true live_progress=true progress_ui=fixed_columns_seconds progress_ui_ms={SolverWeights.ProgressUiIntervalMilliseconds} background_slice_ms={SolverWeights.BackgroundWorkSliceMilliseconds} background_yield=adaptive_frame_recovery+thread_yield yield_check_interval={SolverWeights.BackgroundYieldCheckInterval} allocation_telemetry=true gc_latency=combat_scoped_no_gc_region_or_clr_default gc_start=upgrade_and_new_install_enabled_user_toggle gc_budget=independent_configurable_default_16gb_retained_when_disabled gc_disabled=steady_upstream_clr_no_new_automatic_policy+settle_prior_obligations gc_partition=soh_five_sixths+loh_one_sixth gc_auto_reclaim=no_gc_only_background_non_compacting gc_manual_reclaim=both_modes_lifecycle_safe frame_percentiles=true result_snapshot=scalar_only historical_simulators=released end_turn_card_cleanup=sparse_exact history_cards=compact state_store_fork=eager fork_context=pooled risk_cache=signature_interned lazy_simulation_collections=true search_state_key=dual_u64 cross_turn_reuse=exact_state_text horizon=time_or_node_budget predicted_shuffles=unbounded empty_deck_can_advance=true potion_min_hp_saved={SolverWeights.PotionMinimumHpSaved} rng_counters_in_state_key=true choices=true turn_start_choices=tools_tyranny_entropy_toasty turn_start_choice_auto_deploy=true unplanned_combat_choice=fail_fast deployment_drift=replan knowledge_demon_choice=searched_persistent_debuff_branches deployment_unplayable=live_reason_log+root_replan headbutt_pre_discard=true future_upgrade_names=true generated_card_titles=all_model_db armaments=true anger=true battle_trance=true crimson_mantle=true uppercut=true waterfall_giant_moves=true coverage_details=true core_powers=true ravenous_after_death=true ravenous_power_queue_resolved=true chains_of_binding_turn_state=true terminal_setup_zero=true turn_setup_bias_resets=true unspent_energy_penalty=false max_actions_per_turn=unbounded exact_cycle_pruning=true repeatable_no_progress_cycle_pruning=16 dominance_pruning=true short_beam_total={SolverSearchProfile.Short.BeamWidth} deep_beam_total={SolverSearchProfile.Deep.BeamWidth} short_top_queue={SolverSearchProfile.Short.MaxCardBranchesPerNode} deep_top_queue={SolverSearchProfile.Deep.MaxCardBranchesPerNode} short_pile_choice_branches={SolverSearchProfile.Short.MaxPileChoiceBranchesPerAction} deep_pile_choice_branches={SolverSearchProfile.Deep.MaxPileChoiceBranchesPerAction} short_hand_choice_branches={SolverSearchProfile.Short.MaxHandChoiceBranchesPerAction} deep_hand_choice_branches={SolverSearchProfile.Deep.MaxHandChoiceBranchesPerAction} snapshot_reuse=true duplicate_branch_pruning=true replay_count=true battle_damage_tracking=true cumulative_sold_hp=true action_badges=true badge_radius=10 inline_choice_badges=true turn_start_choice_badges=true semantic_action_pills=true three_column_routes=true full_kill_highlight=true full_target_names=true details_in_status_row=true battle_hp_in_route_heading=true sold_hp_summary=false persistent_status_card=true status_live_line=true compact_title=true compact_footer=true collapsed_action_buttons=true details_panel_on_demand=true overlay_draggable=true drag_coordinates=viewport drag_relayout=release_only visible_unwrapped_route_rows=3 cached_route_rows={SolverWeights.UiTurnRows} interactive_overlay=true responsive_layout=true route_scroll=true deployment_highlight=true deployment_speed=native_override_plus_interval relic_action_annotations=selected_route_replay only_death_routes=true duplicate_potion_ids=true implicit_choice_reconciliation=true knowledge_demon_auto_choice=true vital_spark_enemy_owner=true locale_font=true locale_font_bold=true type_scale=12_13_14_15 minimum_font_size=12 simulation_notification_isolation=true content_fit_height=true combat_exit_cleanup=true kill_annotations=true combat_end_marker=true");
            Logger.Info("[CombatSolver/Test] ENGINE embedded=true rf_dependency=false incremental_search=true");
            Logger.Info("战斗路线求解器已启用。每个玩家新回合会自动后台搜索，也可在面板中执行当前回合路线或开启全自动。");
            NGame? host = NGame.Instance;
            if (host != null)
                SolverDispatcher.Ensure(host);
            UnattendedTestRunner.TryStart(host);
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
            || !SolverController.AutomaticCalculationEnabled
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
