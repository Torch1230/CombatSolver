using System.Diagnostics;
using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal static class SolverDiagnostics
{
    private const string Prefix = "[CombatSolver/Test]";

    public static string DescribeStart(
        CombatState state,
        SolverSearchProfile shortProfile,
        SolverSearchProfile deepProfile)
    {
        StringBuilder text = new();
        Player? player = LocalContext.GetMe(state);
        PlayerCombatState? pcs = player?.PlayerCombatState;
        text.Append(Prefix).Append(" START")
            .Append(" round=").Append(state.RoundNumber)
            .Append(" side=").Append(state.CurrentSide)
            .Append(" phase=").Append(pcs?.Phase.ToString() ?? "null")
            .AppendLine();

        if (player != null && pcs != null)
        {
            text.Append(Prefix).Append(" PLAYER")
                .Append(" hp=").Append(player.Creature.CurrentHp).Append('/').Append(player.Creature.MaxHp)
                .Append(" block=").Append(player.Creature.Block)
                .Append(" energy=").Append(pcs.Energy).Append('/').Append(pcs.MaxEnergy)
                .Append(" stars=").Append(pcs.Stars)
                .Append(" powers=").Append(PowerTokens(player.Creature))
                .AppendLine();
            AppendPile(text, "HAND", pcs.Hand.Cards);
            AppendPile(text, "DRAW", pcs.DrawPile.Cards);
            AppendPile(text, "DISCARD", pcs.DiscardPile.Cards);
            AppendPile(text, "EXHAUST", pcs.ExhaustPile.Cards);
        }

        for (int i = 0; i < state.Enemies.Count; i++)
        {
            var enemy = state.Enemies[i];
            text.Append(Prefix).Append(" ENEMY")
                .Append(" index=").Append(i)
                .Append(" id=").Append(enemy.Monster?.Id.Entry ?? "null")
                .Append(" hp=").Append(enemy.CurrentHp).Append('/').Append(enemy.MaxHp)
                .Append(" block=").Append(enemy.Block)
                .Append(" move=").Append(enemy.Monster?.NextMove?.Id ?? "null")
                .Append(" powers=").Append(PowerTokens(enemy))
                .AppendLine();
        }

        text.Append(Prefix).Append(" WEIGHTS")
            .Append(" horizon=time_or_node_budget")
            .Append(" predicted_shuffles=unbounded")
            .Append(" setup_value_horizon_turns=").Append(SolverWeights.SetupValueHorizonTurns)
            .Append(" short_beam=").Append(shortProfile.BeamWidth)
            .Append(" deep_beam=").Append(deepProfile.BeamWidth)
            .Append(" short_budget_ms=").Append(shortProfile.SoftTimeBudgetMilliseconds)
            .Append(" deep_total_budget_ms=").Append(deepProfile.SoftTimeBudgetMilliseconds)
            .Append(" max_actions_per_turn=unbounded")
            .Append(" short_top_queue=").Append(shortProfile.MaxCardBranchesPerNode)
            .Append(" deep_top_queue=").Append(deepProfile.MaxCardBranchesPerNode)
            .Append(" short_pile_choice_branches=").Append(shortProfile.MaxPileChoiceBranchesPerAction)
            .Append(" deep_pile_choice_branches=").Append(deepProfile.MaxPileChoiceBranchesPerAction)
            .Append(" short_hand_choice_branches=").Append(shortProfile.MaxHandChoiceBranchesPerAction)
            .Append(" deep_hand_choice_branches=").Append(deepProfile.MaxHandChoiceBranchesPerAction)
            .Append(" hp=").Append(SolverWeights.Hp)
            .Append(" enemy_hp=").Append(SolverWeights.EnemyHp)
            .Append(" vulnerable_attack_window_cap=").Append(SolverWeights.VulnerableAttackWindowCap)
            .Append(" vulnerable_attack_multiplier_value=")
            .Append(SolverWeights.VulnerableAttackMultiplierBeamValue)
            .Append(" current_energy_cap=").Append(SolverWeights.CurrentEnergyBeamCap)
            .Append(" current_energy_value=").Append(SolverWeights.CurrentEnergyBeamValue)
            .Append(" projected_shuffle_order=full")
            .Append(" exact_states_per_shuffle_order=").Append(SolverWeights.ExactStatesPerProjectedShuffleOrder)
            .Append(" pocketwatch_tactical_cohorts=score+shuffle_per_cadence")
            .Append(" ordered_pile_variants=").Append(SolverWeights.OrderedPileVariantsPerTacticalState)
            .Append(" pocketwatch_beam_cap=base+routing_choice_quota")
            .Append(" pocketwatch_pareto_candidates_per_cadence=")
            .Append(SolverWeights.PocketwatchParetoCandidatesPerCadence)
            .Append(" sold_hp_penalty=").Append(SolverWeights.SoldHpPenalty)
            .Append(" sold_hp_basis=route_loss_minus_minimum_reachable_loss")
            .Append(" sold_hp_threshold_mode=hard_cumulative_budget")
            .Append(" sold_hp_threshold_normal=").Append(SolverWeights.NormalSoldHpThreshold)
            .Append(" sold_hp_threshold_elite=").Append(SolverWeights.EliteSoldHpThreshold)
            .Append(" sold_hp_threshold_boss=").Append(SolverWeights.BossSoldHpThreshold)
            .Append(" potion_min_hp_saved=").Append(SolverWeights.PotionMinimumHpSaved)
            .Append(" search_state_key=dual_u64")
            .Append(" setup_dimensions=persistent_buff_latent_setup_threat_focus_future_resource_retained_attack_replay_poison_sandpit_deck_clutter")
            .Append(" final_policy=survival_victory_hp_potions_sold_hp")
            .Append(" beam_lanes=potion_split_defense_offense_scaling_resource_control_routing_choice");
        return text.ToString();
    }

    public static string DescribeResult(SolverResult result)
    {
        GCMemoryInfo gcMemory = GC.GetGCMemoryInfo();
        using Process process = Process.GetCurrentProcess();
        StringBuilder text = new();
        text.Append(Prefix).Append(" RESULT")
            .Append(" phase=").Append(result.SearchPhase)
            .Append(" deep_triggered=").Append(result.DeepSearchTriggered)
            .Append(" deep_improved=").Append(result.SingleSessionSearch
                ? "n/a_single_session"
                : result.DeepSearchImprovedResult.ToString())
            .Append(" reused=").Append(result.WasReused)
            .Append(" reused_from_turn=").Append(result.ReusedFromTurn?.ToString() ?? "-")
            .Append(" expanded=").Append(result.ExpandedNodes)
            .Append(" total_expanded=").Append(result.TotalExpandedNodes)
            .Append(" dominance_pruned=").Append(result.DominatedActionsPruned)
            .Append(" top_queue_dropped=").Append(result.TopQueueActionsDropped)
            .Append(" duplicate_cards_pruned=").Append(result.DuplicateCardBranchesPruned)
            .Append(" choice_branches=").Append(result.ChoiceBranchesEvaluated)
            .Append(" total_choice_branches=").Append(result.TotalChoiceBranchesEvaluated)
            .Append(" choice_replay_attempts=").Append(result.ChoiceReplayAttempts)
            .Append(" choice_replay_budget_exhaustions=")
            .Append(result.ChoiceReplayBudgetExhaustions)
            .Append(" shuffle_branches_pruned=").Append(result.ShuffleBranchesPruned)
            .Append(" sold_hp_branches_pruned=").Append(result.SoldHpBranchesPruned)
            .Append(" replays=").Append(result.ReplayCount)
            .Append(" forks=").Append(result.ForkCount)
            .Append(" transitions=").Append(result.TransitionCount)
            .Append(" total_transitions=").Append(result.TotalTransitionCount)
            .Append(" cache_hits=").Append(result.TransitionCacheHits)
            .Append(" stand_pat=").Append(result.StandPatProbes)
            .Append(" snapshot_reuses=").Append(result.ReusedNodeSnapshots)
            .Append(" transposition_pruned=").Append(result.TranspositionBranchesPruned)
            .Append(" repeatable_no_progress_pruned=").Append(result.RepeatableNoProgressBranchesPruned)
            .Append(" cycle_shapes=").Append(result.CycleShapesDetected)
            .Append(" cycle_probe_continuations=")
            .Append(result.CycleProbeContinuationsExpanded)
            .Append(" cycle_candidates_protected=").Append(result.CycleCandidatesProtected)
            .Append(" cycle_continuations_stopped=").Append(result.CycleContinuationsStopped)
            .Append(" cross_turn_candidates_protected=").Append(result.CrossTurnCandidatesProtected)
            .Append(" cross_turn_continuations_stopped=").Append(result.CrossTurnContinuationsStopped)
            .Append(" primary_incumbent_pruned=").Append(result.PrimaryIncumbentBranchesPruned)
            .Append(" primary_incumbent_updates=").Append(result.PrimaryIncumbentUpdates)
            .Append(" stand_pat_probes=").Append(result.StandPatProbes)
            .Append(" parallel_waves=").Append(result.ParallelExpansionWaves)
            .Append(" parallel_work_items=").Append(result.ParallelExpansionWorkItems)
            .Append(" parallel_max_concurrency=").Append(result.MaxParallelExpansionConcurrency)
            .Append(" parallel_action_waves=").Append(result.ParallelActionReplayWaves)
            .Append(" parallel_action_work_items=").Append(result.ParallelActionReplayWorkItems)
            .Append(" parallel_action_max_concurrency=").Append(result.MaxParallelActionReplayConcurrency)
            .Append(" deferred_round_choice_actions=").Append(result.DeferredRoundChoiceActions)
            .Append(" deferred_round_choice_width_total=").Append(result.DeferredRoundChoiceLayerWidthTotal)
            .Append(" deferred_round_choice_max_width=").Append(result.MaxDeferredRoundChoiceLayerWidth)
            .Append(" deferred_round_choice_finite_fallbacks=").Append(result.DeferredRoundChoiceFiniteQuotaFallbacks)
            .Append(" deferred_round_choice_finite_fallback_scope=pending_only")
            .Append(" deferred_round_choice_finite_primary_layers=").Append(result.DeferredRoundChoiceFinitePrimaryLayers)
            .Append(" deferred_round_choice_finite_pending_fallbacks=").Append(result.DeferredRoundChoiceFinitePendingFallbacks)
            .Append(" parallel_round_choice_waves=").Append(result.ParallelRoundChoiceReplayWaves)
            .Append(" parallel_round_choice_work_items=").Append(result.ParallelRoundChoiceReplayWorkItems)
            .Append(" parallel_round_choice_max_concurrency=").Append(result.MaxParallelRoundChoiceReplayConcurrency)
            .Append(" node_limit_snapshots_released=").Append(result.NodeLimitSnapshotsReleased)
            .Append(" transition_cache_hits=").Append(result.TransitionCacheHits)
            .Append(" worker_allocated_bytes=").Append(result.WorkerAllocatedBytes)
            .Append(" allocated_per_transition=").Append(result.TransitionCount == 0 ? 0 : result.WorkerAllocatedBytes / result.TransitionCount)
            .Append(" gc0=").Append(result.Gen0Collections)
            .Append(" gc1=").Append(result.Gen1Collections)
            .Append(" gc2=").Append(result.Gen2Collections)
            .Append(" gc_pause_ms=").Append(result.GcPauseDuration.TotalMilliseconds.ToString("F1"))
            .Append(" max_gc_pause_ms=").Append(result.MaxObservedGcPause.TotalMilliseconds.ToString("F1"))
            .Append(" worker_yields=").Append(result.WorkerYieldCount)
            .Append(" frame_recovery_waits=").Append(result.FrameRecoveryWaitCount)
            .Append(" frame_recovery_wait_ms=").Append(result.FrameRecoveryWaitDuration.TotalMilliseconds.ToString("F1"))
            .Append(" searched_turns=").Append(result.SearchedTurns)
            .Append(" boundary=").Append(result.BoundaryReason)
            .Append(" shuffles_crossed=").Append(result.Snapshot.ShufflesCrossed)
            .Append(" unavoidable_hp_lost=").Append(result.UnavoidableHpLost)
            .Append(" sold_hp=").Append(result.SoldHp)
            .Append(" future_sold_hp=").Append(result.FutureSoldHp)
            .Append(" sold_hp_threshold=").Append(result.SoldHpThreshold)
            .Append(" battle_hp_lost_so_far=").Append(result.BattleHpLostSoFar)
            .Append(" projected_battle_hp_lost=").Append(result.ProjectedBattleHpLost)
            .Append(" long_term_resource=").Append(result.Snapshot.LongTermResourceValue)
            .Append(" anger_copies=").Append(result.Snapshot.AngerCopiesGenerated)
            .Append(" battle_potions_used_so_far=").Append(result.BattlePotionsUsedSoFar)
            .Append(" potion_count=").Append(result.PotionCount)
            .Append(" potion_hp_saved=").Append(result.PotionHpSaved)
            .Append(" potion_hp_required=").Append(result.PotionHpRequired)
            .Append(" potion_branches_rejected=").Append(result.PotionBranchesRejected)
            .Append(" theft_policy=").Append(result.TheftPolicy?.ToString() ?? "-")
            .Append(" outstanding_stolen_resource=").Append(result.OutstandingStolenResource)
            .Append(" elapsed_ms=").Append(result.Elapsed.TotalMilliseconds.ToString("F0"))
            .Append(" total_elapsed_ms=").Append(result.TotalSearchElapsed.TotalMilliseconds.ToString("F0"))
            .Append(" total_worker_allocated_bytes=").Append(result.TotalWorkerAllocatedBytes)
            .Append(" total_gc0=").Append(result.TotalGen0Collections)
            .Append(" total_gc1=").Append(result.TotalGen1Collections)
            .Append(" total_gc2=").Append(result.TotalGen2Collections)
            .Append(" total_gc_pause_ms=").Append(result.TotalGcPauseDuration.TotalMilliseconds.ToString("F1"))
            .Append(" total_max_gc_pause_ms=").Append(result.TotalMaxObservedGcPause.TotalMilliseconds.ToString("F1"))
            .Append(" main_thread_frames=").Append(result.MainThreadFrameCount)
            .Append(" p95_main_thread_gap_ms=").Append(result.P95MainThreadFrameGapMilliseconds.ToString("F1"))
            .Append(" p99_main_thread_gap_ms=").Append(result.P99MainThreadFrameGapMilliseconds.ToString("F1"))
            .Append(" max_main_thread_gap_ms=").Append(result.MaxMainThreadFrameGapMilliseconds.ToString("F1"))
            .Append(" main_thread_over_33ms=").Append(result.MainThreadFramesOver33Milliseconds)
            .Append(" main_thread_over_50ms=").Append(result.MainThreadFramesOver50Milliseconds)
            .Append(" main_thread_over_100ms=").Append(result.MainThreadFramesOver100Milliseconds)
            .Append(" managed_live_bytes=").Append(GC.GetTotalMemory(forceFullCollection: false))
            .Append(" managed_heap_bytes=").Append(gcMemory.HeapSizeBytes)
            .Append(" managed_fragmented_bytes=").Append(gcMemory.FragmentedBytes)
            .Append(" process_working_set_bytes=").Append(process.WorkingSet64)
            .Append(" process_private_bytes=").Append(process.PrivateMemorySize64)
            .Append(" short_elapsed_ms=").Append(result.ShortSearchElapsed.TotalMilliseconds.ToString("F0"))
            .Append(" deep_elapsed_ms=").Append(result.DeepSearchElapsed.TotalMilliseconds.ToString("F0"))
            .Append(" score=").Append(result.BestNode.Score.ToString("F0"))
            .Append(" final_hp=").Append(result.Snapshot.PlayerHp)
            .Append(" final_block=").Append(result.Snapshot.PlayerBlock)
            .Append(" final_enemy_hp=").Append(result.Snapshot.EnemyHp)
            .Append(" combat_ended_turn=").Append(result.CombatEndedTurn?.ToString() ?? "-")
            .Append(" death_turn=").Append(result.DeathTurn?.ToString() ?? "-")
            .Append(" only_death_routes=").Append(result.OnlyDeathRoutesFound)
            .Append(" act_ending_boss=").Append(result.IsActEndingBoss)
            .Append(" boss_hp_relief=").Append(result.BossHpRelief)
            .Append(" engine_risk=").Append(result.Snapshot.HasRisk)
            .Append(" unsupported_intent=").Append(result.Forecast.HasUnsupportedIntent)
            .Append(" modeled_damage_exact=").Append(result.Forecast.IsExactForModeledDamage)
            .AppendLine();

        for (int round = 0; round < Math.Min(result.SearchedTurns, result.Forecast.Rounds.Count); round++)
        {
            foreach (ForecastMove move in result.Forecast.Rounds[round])
            {
                text.Append(Prefix).Append(" FORECAST")
                    .Append(" turn=").Append(result.StartTurnNumber + round)
                    .Append(" enemy=").Append(move.Owner.Monster?.Id.Entry ?? move.Owner.Name)
                    .Append(" move=").Append(move.Move.Id)
                    .Append(" hits=").Append(move.AttackHits.Count == 0 ? "-" : string.Join('x', move.AttackHits))
                    .AppendLine();
            }
        }

        foreach ((int turn, int hpLost) in result.HpLostByTurn.OrderBy(item => item.Key))
        {
            text.Append(Prefix).Append(" TURN_OUTCOME")
                .Append(" turn=").Append(turn)
                .Append(" hp_lost=").Append(hpLost)
                .Append(" sold_hp=").Append(result.SoldHpByTurn.GetValueOrDefault(turn))
                .Append(" max_block=").Append(result.MaxBlockByTurn.GetValueOrDefault(turn))
                .Append(" actual_block=").Append(result.ActualBlockByTurn.GetValueOrDefault(turn))
                .Append(" energy_left=").Append(result.EnergyLeftByTurn.GetValueOrDefault(turn))
                .AppendLine();
        }

        foreach (PredictionGap gap in result.Snapshot.PredictionGaps)
        {
            text.Append(Prefix).Append(" COVERAGE")
                .Append(" source=").Append(gap.SourceId)
                .Append(" method=").Append(gap.Method)
                .Append(" reason=").Append(gap.Reason)
                .Append(" compensated=").Append(gap.Compensated)
                .AppendLine();
        }
        foreach (string detail in result.Forecast.UnsupportedDetails)
            text.Append(Prefix).Append(" COVERAGE intent_unsupported=").Append(detail).AppendLine();
        foreach (string detail in result.Forecast.ApproximationDetails)
            text.Append(Prefix).Append(" COVERAGE approximation=").Append(detail).AppendLine();

        for (int actionIndex = 0; actionIndex < result.BestNode.Actions.Count; actionIndex++)
        {
            PlanAction action = result.BestNode.Actions[actionIndex];
            bool isLastActionInTurn = actionIndex == result.BestNode.Actions.Count - 1
                || result.BestNode.Actions[actionIndex + 1].Turn != action.Turn;
            text.Append(Prefix).Append(" ACTION")
                .Append(" turn=").Append(action.Turn)
                .Append(" kind=").Append(action.Kind);
            if (action.Kind == PlanActionKind.PlayCard)
            {
                text.Append(" card_id=").Append(action.CardId)
                    .Append(" occurrence=").Append(action.CardOccurrence)
                    .Append(" title=").Append(action.CardTitle)
                    .Append(" target_index=").Append(action.TargetIndex)
                    .Append(" target_combat_id=").Append(action.TargetCombatId?.ToString() ?? "-")
                    .Append(" target=").Append(string.IsNullOrEmpty(action.TargetName) ? "-" : action.TargetName)
                    .Append(" choice_effect=").Append(action.Choice?.Effect.ToString() ?? "-")
                    .Append(" choice_cards=").Append(action.Choice == null ? "-" : ChoiceTokens(action.Choice));
            }
            else if (action.Kind == PlanActionKind.UsePotion)
            {
                text.Append(" potion_id=").Append(action.PotionId)
                    .Append(" slot=").Append(action.PotionSlot)
                    .Append(" title=").Append(action.PotionTitle)
                    .Append(" target_index=").Append(action.TargetIndex)
                    .Append(" target_combat_id=").Append(action.TargetCombatId?.ToString() ?? "-")
                    .Append(" target=").Append(string.IsNullOrEmpty(action.TargetName) ? "-" : action.TargetName)
                    .Append(" choice_effect=").Append(action.Choice?.Effect.ToString() ?? "-")
                    .Append(" choice_cards=").Append(action.Choice == null ? "-" : ChoiceTokens(action.Choice));
            }
            else if (action.Kind == PlanActionKind.EndTurn && action.TurnStartChoices is { Count: > 0 })
            {
                text.Append(" turn_start_choices=").Append(string.Join(';', action.TurnStartChoices.Select(choice =>
                    $"{choice.SourceId}:{choice.Effect}:{ChoiceTokens(choice)}")));
            }
            if (action.RelicEffects is { Count: > 0 })
            {
                text.Append(" relic_effects=").Append(string.Join(';', action.RelicEffects.Select(effect =>
                    $"{effect.RelicId}:{effect.Summary}")));
            }
            if (action.IsExecutable)
                text.Append(" kills=").Append(result.KillsAfterAction.TryGetValue(actionIndex, out IReadOnlyList<string>? kills)
                    ? string.Join(',', kills)
                    : "-");
            if (isLastActionInTurn && result.CombatEndedTurn == action.Turn)
                text.Append(" combat_ended=true");
            text.AppendLine();
        }

        return text.ToString().TrimEnd();
    }

    private static string ChoiceTokens(PlanCardChoice choice)
        => string.Join(',', choice.Cards.Select(card =>
            $"{card.CardId}+{card.UpgradeLevel}#src{card.SourceOccurrence}/opt{card.OptionOccurrence}"));

    public static string DescribeSearchPhasePerformance(SolverResult result)
    {
        static string Metric(SearchPhaseMetric metric)
            => $"{metric.Elapsed.TotalMilliseconds:F1}ms/{metric.AllocatedBytes}B";
        return new StringBuilder()
            .Append(Prefix).Append(" SEARCH_PHASE")
            .Append(" phase=").Append(result.SearchPhase)
            .Append(" expanded=").Append(result.ExpandedNodes)
            .Append(" transitions=").Append(result.TransitionCount)
            .Append(" choice_replay_attempts=").Append(result.ChoiceReplayAttempts)
            .Append(" choice_replay_budget_exhaustions=")
            .Append(result.ChoiceReplayBudgetExhaustions)
            .Append(" cache_hits=").Append(result.TransitionCacheHits)
            .Append(" stand_pat=").Append(result.StandPatProbes)
            .Append(" parallel_waves=").Append(result.ParallelExpansionWaves)
            .Append(" parallel_work_items=").Append(result.ParallelExpansionWorkItems)
            .Append(" parallel_max_concurrency=").Append(result.MaxParallelExpansionConcurrency)
            .Append(" parallel_action_waves=").Append(result.ParallelActionReplayWaves)
            .Append(" parallel_action_work_items=").Append(result.ParallelActionReplayWorkItems)
            .Append(" parallel_action_max_concurrency=").Append(result.MaxParallelActionReplayConcurrency)
            .Append(" deferred_round_choice_actions=").Append(result.DeferredRoundChoiceActions)
            .Append(" deferred_round_choice_width_total=").Append(result.DeferredRoundChoiceLayerWidthTotal)
            .Append(" deferred_round_choice_max_width=").Append(result.MaxDeferredRoundChoiceLayerWidth)
            .Append(" deferred_round_choice_finite_fallbacks=").Append(result.DeferredRoundChoiceFiniteQuotaFallbacks)
            .Append(" deferred_round_choice_finite_fallback_scope=pending_only")
            .Append(" deferred_round_choice_finite_primary_layers=").Append(result.DeferredRoundChoiceFinitePrimaryLayers)
            .Append(" deferred_round_choice_finite_pending_fallbacks=").Append(result.DeferredRoundChoiceFinitePendingFallbacks)
            .Append(" parallel_round_choice_waves=").Append(result.ParallelRoundChoiceReplayWaves)
            .Append(" parallel_round_choice_work_items=").Append(result.ParallelRoundChoiceReplayWorkItems)
            .Append(" parallel_round_choice_max_concurrency=").Append(result.MaxParallelRoundChoiceReplayConcurrency)
            .Append(" node_limit_snapshots_released=").Append(result.NodeLimitSnapshotsReleased)
            .Append(" primary_incumbent_pruned=").Append(result.PrimaryIncumbentBranchesPruned)
            .Append(" primary_incumbent_updates=").Append(result.PrimaryIncumbentUpdates)
            .Append(" elapsed_ms=").Append(result.Elapsed.TotalMilliseconds.ToString("F1"))
            .Append(" allocated_bytes=").Append(result.WorkerAllocatedBytes)
            .Append(" fork=").Append(Metric(result.ForkMetric))
            .Append(" action=").Append(Metric(result.ActionMetric))
            .Append(" card_exec=").Append(Metric(result.CardExecutionMetric))
            .Append(" card_post=").Append(Metric(result.CardPostProcessingMetric))
            .Append(" potion_exec=").Append(Metric(result.PotionExecutionMetric))
            .Append(" round=").Append(Metric(result.RoundAdvanceMetric))
            .Append(" round_player_end=").Append(Metric(result.RoundPlayerEndMetric))
            .Append(" round_end_sim=").Append(Metric(result.RoundEndSimulationMetric))
            .Append(" round_flush=").Append(Metric(result.RoundFlushMetric))
            .Append(" round_player_powers=").Append(Metric(result.RoundPlayerEndPowersMetric))
            .Append(" round_enemy=").Append(Metric(result.RoundEnemyTurnMetric))
            .Append(" round_enemy_start=").Append(Metric(result.RoundEnemyStartMetric))
            .Append(" round_enemy_moves=").Append(Metric(result.RoundEnemyMovesMetric))
            .Append(" round_enemy_powers=").Append(Metric(result.RoundEnemyEndPowersMetric))
            .Append(" round_player_start=").Append(Metric(result.RoundPlayerStartMetric))
            .Append(" round_draw=").Append(Metric(result.RoundDrawMetric))
            .Append(" snapshot=").Append(Metric(result.SnapshotMetric))
            .Append(" threat=").Append(Metric(result.ThreatProjectionMetric))
            .Append(" fingerprint=").Append(Metric(result.FingerprintMetric))
            .Append(" projected_shuffle=").Append(Metric(result.ProjectedShuffleMetric))
            .Append(" pile_fingerprint=").Append(Metric(result.PileFingerprintMetric))
            .Append(" pile_miss=").Append(Metric(result.PileFingerprintMissMetric))
            .Append(" card_miss=").Append(Metric(result.CardFingerprintMissMetric))
            .Append(" combat_fingerprint=").Append(Metric(result.CombatFingerprintMetric))
            .Append(" prune=").Append(Metric(result.PruneMetric))
            .Append(" final=").Append(Metric(result.FinalSelectionMetric))
            .ToString();
    }

    private static void AppendPile(StringBuilder text, string name, IReadOnlyList<MegaCrit.Sts2.Core.Models.CardModel> cards)
    {
        text.Append(Prefix).Append(' ').Append(name)
            .Append(" count=").Append(cards.Count)
            .Append(" cards=")
            .Append(cards.Count == 0
                ? "-"
                : string.Join(',', cards.Select(CardToken)))
            .AppendLine();
    }

    private static string CardToken(MegaCrit.Sts2.Core.Models.CardModel card)
    {
        string upgrade = card.CurrentUpgradeLevel > 0 ? $"+{card.CurrentUpgradeLevel}" : string.Empty;
        return card.Id.Entry + upgrade;
    }

    private static string PowerTokens(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature)
    {
        return creature.Powers.Count == 0
            ? "-"
            : string.Join(',', creature.Powers.Select(power => $"{power.Id.Entry}:{power.Amount}"));
    }
}
