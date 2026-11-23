use crate::arena::{
    CompactAction, CompactActionKind, LocalStateSlab, MainStateArena, NodeArena, NodeId,
    PackedHandle, SearchNode,
};
use crate::boss_tempo::{
    retain_boss_tempo_prefixes, BossTempoPendingItem, BossTempoPrefix, BossTempoSearchOptions,
    BossTempoTranspositions,
};
use crate::early_turn::{
    interleave_potion_frontiers, select_early_turn_continuation_incumbent,
    select_early_turn_frontier, EARLY_TURN_EXPLORATION_INITIAL_RANK_LIMIT,
    EARLY_TURN_EXPLORATION_MAX_RANK_LIMIT, EARLY_TURN_EXPLORATION_NODE_BUDGET,
};
use crate::eval::{
    allows_brightest_flame, compute_beam_rank_score, evaluate_fast, evaluate_full,
    select_final_plan, FastEvalSummary, PotionValuationRegistry, PowerCardValuationRegistry,
    RelicCounterEvaluation, RootEvalBaseline, StandPatCache,
};
use crate::hash::{hash_bytes_128, StateHash};
use crate::novelty::{
    capture_novelty_facts, BfwsBoundedOpen, BfwsEscapeBudget, BfwsFact, BfwsPackedNovelty,
    NoveltyOpenNode, NoveltyPortfolioBudget, NoveltySearchOptions,
};
use crate::packer::{empty_scratch_state, pack_canonical_key, pack_state, unpack_state};
use crate::por::{
    classify_card_for_por, dedup_and_filter_potions, dedup_playable_hand_cards,
    enumerate_canonical_choices, por_should_skip, CombatOrderSensitivity, PorActionClass,
};
use crate::portfolio::{
    BeamWidthPortfolio, BeamWidthPortfolioBaseline, BeamWidthPortfolioGate,
    BeamWidthPortfolioRun, PortfolioSchedulingMode, PowerCommitmentPortfolioGate,
    SolverSearchProfile,
};
use crate::pruning::{
    is_pruned_by_certified_incumbent, BeamRetentionPolicy, ClosureCertificate,
    PrimaryIncumbentBucket, PrimaryIncumbentTable, PrimarySearchIncumbent,
};
use crate::tt::{ShardedParetoTT, TranspositionLabel, TT_SHARD_COUNT};
use crate::{InCombatSolveResponse, PlannedAction, SearchPolicy, SearchProgress, SearchStatistics};
use crossbeam_deque::{Injector, Steal, Stealer, Worker as DequeWorker};
use rayon::prelude::*;
use smallvec::SmallVec;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Arc;
use std::time::Instant;
use sts2_kernel::state::{
    BattleState, CardHandle, CreatureHandle, GrowthCounters, RootStaticContext,
};
use sts2_kernel::{CombatEngine, ContentDispatcher, KernelError, StepOutcome};
use sts2_types::{PlayerTurnPhase, PotionId, RoomType, TargetType};

#[derive(Clone, Copy, Debug)]
enum ActionSpec {
    PlayCard {
        card_handle: CardHandle,
        target_handle: Option<CreatureHandle>,
        por_class: PorActionClass,
    },
    UsePotion {
        potion_slot: usize,
        potion_id: PotionId,
        target_handle: Option<CreatureHandle>,
    },
    EndTurn,
}

#[derive(Clone)]
struct ActionTask {
    parent_rank: u32,
    parent_id: NodeId,
    parent_state: PackedHandle,
    parent_turn: i32,
    parent_action_count: u16,
    parent_potion_count: u16,
    parent_explicit_potion_count: u16,
    parent_automatic_potion_count: u16,
    parent_potion_cost: i32,
    parent_explicit_potion_cost: i32,
    parent_ambergris_count: u16,
    parent_forced_used_mask: u64,
    parent_forced_strategic_hp_cost: i32,
    parent_forced_ambergris_count: u16,
    action_ord: u32,
    spec: ActionSpec,
}

enum WorkItem {
    Action(ActionTask),
    ChoiceRange {
        task: ActionTask,
        paused_bytes: Arc<[u8]>,
        choices: Arc<Vec<Vec<usize>>>,
        start: usize,
        end: usize,
    },
}

struct WorkerCandidate {
    sort_key: (u32, u32, u32), // (parent_rank, action_ord, choice_ord)
    parent_id: NodeId,
    action: CompactAction,
    extra_end_turn: Option<CompactAction>,
    choices: Vec<Vec<usize>>,
    state_slab_idx: u32,
    state_off: u32,
    state_len: u32,
    key_slab_idx: u32,
    key_off: u32,
    key_len: u32,
    state_hash: StateHash,
    turn: i32,
    action_count: u16,
    potion_count: u16,
    explicit_potion_count: u16,
    automatic_potion_count: u16,
    potion_strategic_cost: i32,
    explicit_potion_strategic_cost: i32,
    ambergris_count: u16,
    forced_used_mask: u64,
    forced_strategic_hp_cost: i32,
    forced_ambergris_count: u16,
    is_terminal: bool,
    fast_eval: FastEvalSummary,
    label: TranspositionLabel,
}

struct WorkerOutput {
    slab: LocalStateSlab,
    candidates: Vec<WorkerCandidate>,
    transitions: u64,
    incumbent_pruned: u64,
    tt_probe_pruned: u64,
}

struct WorkerScratch {
    state: BattleState,
    stand_pat_state: BattleState,
    pack_buf: Vec<u8>,
    key_buf: Vec<u8>,
    prefix_buf: Vec<u8>,
    stand_pat_buf: Vec<u8>,
    slab: LocalStateSlab,
    candidates: Vec<WorkerCandidate>,
    transitions: u64,
    incumbent_pruned: u64,
    tt_probe_pruned: u64,
}

impl WorkerScratch {
    fn new(static_ctx: Arc<RootStaticContext>) -> Self {
        Self {
            state: empty_scratch_state(static_ctx.clone()),
            stand_pat_state: empty_scratch_state(static_ctx),
            pack_buf: Vec::with_capacity(4096),
            key_buf: Vec::with_capacity(2048),
            prefix_buf: Vec::with_capacity(4096),
            stand_pat_buf: Vec::with_capacity(4096),
            slab: LocalStateSlab::default(),
            candidates: Vec::with_capacity(64),
            transitions: 0,
            incumbent_pruned: 0,
            tt_probe_pruned: 0,
        }
    }
}

struct BeamRunOutput {
    best_node: Option<NodeId>,
    arena: NodeArena,
    root_baseline: RootEvalBaseline,
    won: bool,
    all_forced_uses_satisfied: bool,
    strategic_hp_deficit: i32,
    strategy_goal_hp_credit: i32,
    strategy_goal_count: i32,
    growth_rewards: GrowthCounters,
    relic_eval: RelicCounterEvaluation,
    recovered_hp: i32,
    hp_lost: i32,
    potion_count: usize,
    score: i64,
    actions: Vec<PlannedAction>,
    expanded_nodes: usize,
    transition_count: u64,
    tt_pruned: u64,
    incumbent_pruned: u64,
    por_pruned: u64,
    packed_state_bytes_total: u64,
    termination: &'static str,
    terminal_cards_hint: Vec<sts2_types::CardId>,
    early_turn_frontiers: Vec<crate::early_turn::EarlyTurnFrontierCandidate>,
    boss_tempo_prefixes: Vec<BossTempoPrefix>,
}

fn target_candidates_for(
    state: &BattleState,
    target_type: TargetType,
) -> SmallVec<[Option<CreatureHandle>; 8]> {
    let mut out = SmallVec::new();
    match target_type {
        TargetType::AnyEnemy => {
            for &eh in &state.enemy_handles {
                if state.creature(eh).is_some_and(|c| c.is_alive()) && state.contains_creature(eh) {
                    out.push(Some(eh));
                }
            }
        }
        TargetType::AnyAlly => {
            for &ah in &state.ally_handles {
                if ah != state.player.creature_handle
                    && state.creature(ah).is_some_and(|c| c.is_alive())
                {
                    out.push(Some(ah));
                }
            }
        }
        _ => out.push(None),
    }
    out
}

fn enumerate_parent_tasks(
    parent_rank: u32,
    parent_id: NodeId,
    node: &SearchNode,
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    sensitivity: &CombatOrderSensitivity,
    tasks_out: &mut Vec<ActionTask>,
    por_pruned: &mut u64,
) {
    if !state.is_in_progress || state.won || state.pending.is_some() {
        return;
    }

    let prev_por = node
        .action
        .map_or(PorActionClass::Barrier, |a| a.por_class);
    let prev_turn = node.action.map_or(node.turn, |a| a.turn);
    let curr_turn = state.player.turn_number;

    let mut action_ord = 0u32;

    if state.player.phase == PlayerTurnPhase::Play {
        let mut card_branches = 0usize;
        let hand_cards = dedup_playable_hand_cards(
            state,
            content,
            policy.accelerations.enable_hand_potion_dedup,
        );
        for ch in hand_cards {
            let Ok(meta) = state.card(ch).ok_or(()).and_then(|c| content.card_meta(c.id).map_err(|_| ())) else {
                continue;
            };
            let targets = target_candidates_for(state, meta.target_type);
            for target_handle in targets {
                if card_branches >= profile.max_card_branches_per_node {
                    break;
                }
                let por_class = classify_card_for_por(state, content, ch, target_handle);
                if policy.accelerations.enable_por
                    && por_should_skip(
                        sensitivity.por_safe,
                        prev_turn,
                        curr_turn,
                        prev_por,
                        por_class,
                    )
                {
                    *por_pruned += 1;
                    continue;
                }
                tasks_out.push(ActionTask {
                    parent_rank,
                    parent_id,
                    parent_state: node.state_handle,
                    parent_turn: node.turn,
                    parent_action_count: node.action_count,
                    parent_potion_count: node.potion_count,
                    parent_explicit_potion_count: node.explicit_potion_count,
                    parent_automatic_potion_count: node.automatic_potion_count,
                    parent_potion_cost: node.potion_strategic_cost,
                    parent_explicit_potion_cost: node.explicit_potion_strategic_cost,
                    parent_ambergris_count: node.ambergris_count,
                    parent_forced_used_mask: node.forced_used_mask,
                    parent_forced_strategic_hp_cost: node.forced_strategic_hp_cost,
                    parent_forced_ambergris_count: node.forced_ambergris_count,
                    action_ord,
                    spec: ActionSpec::PlayCard {
                        card_handle: ch,
                        target_handle,
                        por_class,
                    },
                });
                action_ord += 1;
                card_branches += 1;
            }
        }

        if policy.allows_any_potion() {
            let potions = dedup_and_filter_potions(
                state,
                sensitivity,
                policy,
                policy.accelerations.enable_hand_potion_dedup,
            );
            for (slot, pid) in potions {
                let Ok(meta) = content.potion_meta(pid) else {
                    continue;
                };
                if !policy.allow_random_cards && meta.uses_rng {
                    continue;
                }
                let targets = target_candidates_for(state, meta.target_type);
                for target_handle in targets {
                    tasks_out.push(ActionTask {
                        parent_rank,
                        parent_id,
                        parent_state: node.state_handle,
                        parent_turn: node.turn,
                        parent_action_count: node.action_count,
                        parent_potion_count: node.potion_count,
                        parent_explicit_potion_count: node.explicit_potion_count,
                        parent_automatic_potion_count: node.automatic_potion_count,
                        parent_potion_cost: node.potion_strategic_cost,
                        parent_explicit_potion_cost: node.explicit_potion_strategic_cost,
                        parent_ambergris_count: node.ambergris_count,
                        parent_forced_used_mask: node.forced_used_mask,
                        parent_forced_strategic_hp_cost: node.forced_strategic_hp_cost,
                        parent_forced_ambergris_count: node.forced_ambergris_count,
                        action_ord,
                        spec: ActionSpec::UsePotion {
                            potion_slot: slot,
                            potion_id: pid,
                            target_handle,
                        },
                    });
                    action_ord += 1;
                }
            }
        }
    }

    // EndTurn action
    tasks_out.push(ActionTask {
        parent_rank,
        parent_id,
        parent_state: node.state_handle,
        parent_turn: node.turn,
        parent_action_count: node.action_count,
        parent_potion_count: node.potion_count,
        parent_explicit_potion_count: node.explicit_potion_count,
        parent_automatic_potion_count: node.automatic_potion_count,
        parent_potion_cost: node.potion_strategic_cost,
        parent_explicit_potion_cost: node.explicit_potion_strategic_cost,
        parent_ambergris_count: node.ambergris_count,
        parent_forced_used_mask: node.forced_used_mask,
        parent_forced_strategic_hp_cost: node.forced_strategic_hp_cost,
        parent_forced_ambergris_count: node.forced_ambergris_count,
        action_ord,
        spec: ActionSpec::EndTurn,
    });
}

#[allow(clippy::too_many_arguments)]
fn finalize_candidate(
    scratch: &mut WorkerScratch,
    task: &ActionTask,
    action: CompactAction,
    extra_end_turn: Option<CompactAction>,
    choices: Vec<Vec<usize>>,
    choice_ord: u32,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    policy: &SearchPolicy,
    cert: &ClosureCertificate,
    incumbent_table: &PrimaryIncumbentTable,
    tt: &ShardedParetoTT,
    stand_pat_cache: &StandPatCache,
    sensitivity: &CombatOrderSensitivity,
) {
    if !allows_brightest_flame(
        policy.brightest_flame_max_hp_loss_limit,
        root_baseline.initial_brightest_flame_max_hp_spent,
        root_baseline.brightest_flame_spent(&scratch.state),
    ) {
        return;
    }
    // Forced-End-Turn In-Place Compression: if PlayCard/UsePotion left no legal actions in Play phase,
    // compress EndTurn directly in-place inside the worker unless disabled or scout-stopping.
    let mut final_extra_end = extra_end_turn;
    if final_extra_end.is_none()
        && policy.accelerations.enable_forced_end_turn_compression
        && !matches!(task.spec, ActionSpec::EndTurn)
        && scratch.state.is_in_progress
        && !scratch.state.won
        && scratch.state.pending.is_none()
        && scratch.state.player.phase == PlayerTurnPhase::Play
        && sensitivity.por_safe
    {
        let has_card = !dedup_playable_hand_cards(&mut scratch.state, content, true).is_empty();
        let has_potion = policy.allows_any_potion()
            && !dedup_and_filter_potions(&scratch.state, sensitivity, policy, true).is_empty();
        if !has_card && !has_potion {
            let end_turn_num = scratch.state.player.turn_number;
            if let Ok(StepOutcome::Completed) | Ok(StepOutcome::CombatEnded { .. }) =
                CombatEngine::end_player_turn(&mut scratch.state, content)
            {
                if !allows_brightest_flame(
                    policy.brightest_flame_max_hp_loss_limit,
                    root_baseline.initial_brightest_flame_max_hp_spent,
                    root_baseline.brightest_flame_spent(&scratch.state),
                ) {
                    return;
                }
                final_extra_end = Some(CompactAction {
                    kind: CompactActionKind::EndTurn,
                    turn: end_turn_num,
                    por_class: PorActionClass::Barrier,
                });
            }
        }
    }

    pack_canonical_key(&scratch.state, &mut scratch.key_buf);
    let state_hash = hash_bytes_128(&scratch.key_buf);

    let round_index = scratch.state.player.turn_number - root_baseline.start_turn_number;
    let is_alive_play = scratch.state.is_in_progress
        && !scratch.state.won
        && scratch.state.pending.is_none()
        && scratch.state.player_creature().is_some_and(|c| c.is_alive());
    let stand_pat_hp = if is_alive_play {
        if policy.accelerations.enable_end_turn_prefix_reuse {
            Some(
                stand_pat_cache
                    .get_or_compute(
                        state_hash,
                        round_index,
                        &scratch.state,
                        content,
                        &mut scratch.stand_pat_state,
                        &mut scratch.stand_pat_buf,
                    )
                    .projected_hp,
            )
        } else {
            let sp = stand_pat_cache.get_or_compute(
                state_hash,
                round_index,
                &scratch.state,
                content,
                &mut scratch.stand_pat_state,
                &mut scratch.stand_pat_buf,
            );
            Some(sp.projected_hp)
        }
    } else {
        None
    };

    let fast_eval = evaluate_fast(&scratch.state, content, root_baseline, stand_pat_hp);

    let (
        potion_delta,
        explicit_delta,
        cost_delta,
        ambergris_delta,
        forced_mask_bit,
        forced_cost_delta,
        forced_amb_delta,
    ) = match task.spec {
        ActionSpec::UsePotion {
            potion_slot,
            potion_id,
            ..
        } => {
            let cost = PotionValuationRegistry::strategic_hp_cost_with_overrides(
                potion_id,
                root_baseline.renewable_potion_shaped_rock,
                &policy.potion_hp_threshold_overrides,
            );
            let amb = u16::from(potion_id == PotionId::Ambergris);
            if let Some(bit_idx) = policy.forced_bit_for(potion_slot, potion_id) {
                let bit = if bit_idx < 64 { 1u64 << bit_idx } else { 0 };
                let newly_forced = (task.parent_forced_used_mask & bit) == 0;
                (
                    1u16,
                    1u16,
                    cost,
                    amb,
                    bit,
                    if newly_forced { cost } else { 0 },
                    if newly_forced { amb } else { 0 },
                )
            } else {
                (1u16, 1u16, cost, amb, 0u64, 0i32, 0u16)
            }
        }
        _ => (0u16, 0u16, 0i32, 0u16, 0u64, 0i32, 0u16),
    };

    let potion_count = task.parent_potion_count + potion_delta;
    let explicit_potion_count = task.parent_explicit_potion_count + explicit_delta;
    let potion_strategic_cost = task.parent_potion_cost + cost_delta;
    let explicit_potion_strategic_cost = task.parent_explicit_potion_cost + cost_delta;
    let ambergris_count = task.parent_ambergris_count + ambergris_delta;
    let forced_used_mask = task.parent_forced_used_mask | forced_mask_bit;
    let forced_strategic_hp_cost = task.parent_forced_strategic_hp_cost + forced_cost_delta;
    let forced_ambergris_count = task.parent_forced_ambergris_count + forced_amb_delta;
    let action_count = task.parent_action_count + 1 + u16::from(final_extra_end.is_some());
    let turn = scratch.state.player.turn_number;
    let is_terminal = !scratch.state.is_in_progress
        || fast_eval.won
        || fast_eval.player_dead
        || turn - root_baseline.start_turn_number >= policy.max_turns;

    if !is_terminal
        && policy.accelerations.enable_closure_pruning
        && is_pruned_by_certified_incumbent(
            cert,
            root_baseline,
            incumbent_table,
            fast_eval.cumulative_hp_lost,
            fast_eval.player_hp,
            fast_eval.player_max_hp,
            root_baseline.initial_player_max_hp,
            fast_eval.death_save_hp_restored,
            fast_eval.death_save_use_count,
            potion_count,
            turn,
            root_baseline.boss_hp_relief,
        )
    {
        scratch.incumbent_pruned += 1;
        return;
    }

    let label = fast_eval.transposition_label(potion_count, potion_strategic_cost, 0, action_count);

    // Lock-free read-only TT probe against already committed waves before packing full BattleState.
    if tt.probe_dominated(state_hash, &scratch.key_buf, &label) {
        scratch.tt_probe_pruned += 1;
        return;
    }

    pack_state(&scratch.state, &mut scratch.pack_buf);
    let (state_slab_idx, state_off, state_len) = scratch.slab.alloc_slice(&scratch.pack_buf);
    let (key_slab_idx, key_off, key_len) = scratch.slab.alloc_slice(&scratch.key_buf);

    scratch.candidates.push(WorkerCandidate {
        sort_key: (task.parent_rank, task.action_ord, choice_ord),
        parent_id: task.parent_id,
        action,
        extra_end_turn: final_extra_end,
        choices,
        state_slab_idx,
        state_off,
        state_len,
        key_slab_idx,
        key_off,
        key_len,
        state_hash,
        turn,
        action_count,
        potion_count,
        explicit_potion_count,
        automatic_potion_count: task.parent_automatic_potion_count,
        potion_strategic_cost,
        explicit_potion_strategic_cost,
        ambergris_count,
        forced_used_mask,
        forced_strategic_hp_cost,
        forced_ambergris_count,
        is_terminal,
        fast_eval,
        label,
    });
}

#[allow(clippy::too_many_arguments)]
fn resolve_choices_recursive(
    scratch: &mut WorkerScratch,
    paused_bytes: &[u8],
    choices_so_far: &mut Vec<Vec<usize>>,
    choice_ord: &mut u32,
    depth: usize,
    task: &ActionTask,
    action: CompactAction,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    incumbent_table: &PrimaryIncumbentTable,
    tt: &ShardedParetoTT,
    stand_pat_cache: &StandPatCache,
    sensitivity: &CombatOrderSensitivity,
    injector: Option<(&Injector<WorkItem>, &AtomicU64)>,
) {
    if depth >= 4 {
        return;
    }
    if unpack_state(paused_bytes, scratch.state.static_ctx.clone(), &mut scratch.state).is_err() {
        return;
    }
    let Some(pending) = scratch.state.pending.as_ref() else {
        return;
    };
    let req = pending.request.clone();
    let max_branches = if req.source_pile == sts2_types::PileType::Hand {
        profile.max_hand_choice_branches_per_action
    } else {
        profile.max_pile_choice_branches_per_action
    };
    let choice_options = enumerate_canonical_choices(
        &mut scratch.state,
        content,
        &req,
        max_branches,
        policy.accelerations.enable_multiset_choice_dedup,
    );

    // Wide top-level choice trees split across the lock-free work-stealing injector when parallel.
    if depth == 0 && choice_options.len() > 4 {
        if let Some((inj, pending_items)) = injector {
            let shared_paused: Arc<[u8]> = Arc::from(paused_bytes);
            let shared_choices: Arc<Vec<Vec<usize>>> = Arc::new(choice_options);
            let total = shared_choices.len();
            let chunk = 2usize;
            let chunk_count = (total + chunk - 1) / chunk;
            pending_items.fetch_add(chunk_count as u64, Ordering::AcqRel);
            let mut s = 0usize;
            while s < total {
                let e = (s + chunk).min(total);
                inj.push(WorkItem::ChoiceRange {
                    task: task.clone(),
                    paused_bytes: shared_paused.clone(),
                    choices: shared_choices.clone(),
                    start: s,
                    end: e,
                });
                s = e;
            }
            return;
        }
    }

    for sel in choice_options {
        if unpack_state(paused_bytes, scratch.state.static_ctx.clone(), &mut scratch.state).is_err()
        {
            continue;
        }
        scratch.transitions += 1;
        let Ok(outcome) = CombatEngine::resume_choice(&mut scratch.state, content, &sel) else {
            continue;
        };
        choices_so_far.push(sel);
        match outcome {
            StepOutcome::Completed | StepOutcome::CombatEnded { .. } => {
                let ord = *choice_ord;
                *choice_ord += 1;
                finalize_candidate(
                    scratch,
                    task,
                    action,
                    None,
                    choices_so_far.clone(),
                    ord,
                    content,
                    root_baseline,
                    policy,
                    cert,
                    incumbent_table,
                    tt,
                    stand_pat_cache,
                    sensitivity,
                );
            }
            StepOutcome::NeedsChoice(_) => {
                if allows_brightest_flame(
                    policy.brightest_flame_max_hp_loss_limit,
                    root_baseline.initial_brightest_flame_max_hp_spent,
                    root_baseline.brightest_flame_spent(&scratch.state),
                ) {
                    let mut next_paused = Vec::new();
                    pack_state(&scratch.state, &mut next_paused);
                    resolve_choices_recursive(
                        scratch,
                        &next_paused,
                        choices_so_far,
                        choice_ord,
                        depth + 1,
                        task,
                        action,
                        content,
                        root_baseline,
                        policy,
                        profile,
                        cert,
                        incumbent_table,
                        tt,
                        stand_pat_cache,
                        sensitivity,
                        None,
                    );
                }
            }
        }
        choices_so_far.pop();
    }
}

#[allow(clippy::too_many_arguments)]
fn execute_action_task(
    scratch: &mut WorkerScratch,
    task: &ActionTask,
    main_arena: &MainStateArena,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    incumbent_table: &PrimaryIncumbentTable,
    tt: &ShardedParetoTT,
    stand_pat_cache: &StandPatCache,
    sensitivity: &CombatOrderSensitivity,
    injector: Option<(&Injector<WorkItem>, &AtomicU64)>,
) {
    let parent_bytes = main_arena.get(task.parent_state);
    if unpack_state(parent_bytes, scratch.state.static_ctx.clone(), &mut scratch.state).is_err() {
        return;
    }

    let action = match task.spec {
        ActionSpec::PlayCard {
            card_handle,
            target_handle,
            por_class,
        } => CompactAction {
            kind: CompactActionKind::PlayCard {
                card_handle: card_handle.0,
                target_handle: target_handle.map(|h| h.0),
            },
            turn: task.parent_turn,
            por_class,
        },
        ActionSpec::UsePotion {
            potion_slot,
            potion_id,
            target_handle,
        } => CompactAction {
            kind: CompactActionKind::UsePotion {
                potion_slot: potion_slot as u16,
                potion_id,
                target_handle: target_handle.map(|h| h.0),
            },
            turn: task.parent_turn,
            por_class: PorActionClass::Barrier,
        },
        ActionSpec::EndTurn => CompactAction {
            kind: CompactActionKind::EndTurn,
            turn: task.parent_turn,
            por_class: PorActionClass::Barrier,
        },
    };

    let mut empty_q: SmallVec<[Vec<usize>; 2]> = SmallVec::new();
    scratch.transitions += 1;
    let outcome = match task.spec {
        ActionSpec::PlayCard {
            card_handle,
            target_handle,
            ..
        } => CombatEngine::play_card(
            &mut scratch.state,
            content,
            card_handle,
            target_handle,
            &mut empty_q,
        ),
        ActionSpec::UsePotion {
            potion_slot,
            target_handle,
            ..
        } => CombatEngine::use_potion(
            &mut scratch.state,
            content,
            potion_slot,
            target_handle,
            &mut empty_q,
        ),
        ActionSpec::EndTurn => CombatEngine::end_player_turn(&mut scratch.state, content),
    };

    let Ok(outcome) = outcome else {
        return;
    };

    match outcome {
        StepOutcome::Completed | StepOutcome::CombatEnded { .. } => {
            finalize_candidate(
                scratch,
                task,
                action,
                None,
                Vec::new(),
                0,
                content,
                root_baseline,
                policy,
                cert,
                incumbent_table,
                tt,
                stand_pat_cache,
                sensitivity,
            );
        }
        StepOutcome::NeedsChoice(_) => {
            if !allows_brightest_flame(
                policy.brightest_flame_max_hp_loss_limit,
                root_baseline.initial_brightest_flame_max_hp_spent,
                root_baseline.brightest_flame_spent(&scratch.state),
            ) {
                return;
            }
            // End-turn turn-start prefix reuse & card/potion choice prefix reuse: pack paused state ONCE!
            pack_state(&scratch.state, &mut scratch.prefix_buf);
            let paused = scratch.prefix_buf.clone();
            let mut choices_so_far = Vec::new();
            let mut choice_ord = 0u32;
            resolve_choices_recursive(
                scratch,
                &paused,
                &mut choices_so_far,
                &mut choice_ord,
                0,
                task,
                action,
                content,
                root_baseline,
                policy,
                profile,
                cert,
                incumbent_table,
                tt,
                stand_pat_cache,
                sensitivity,
                injector,
            );
        }
    }
}

#[allow(clippy::too_many_arguments)]
fn execute_choice_range(
    scratch: &mut WorkerScratch,
    task: &ActionTask,
    paused_bytes: &[u8],
    choices: &[Vec<usize>],
    start: usize,
    end: usize,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    incumbent_table: &PrimaryIncumbentTable,
    tt: &ShardedParetoTT,
    stand_pat_cache: &StandPatCache,
    sensitivity: &CombatOrderSensitivity,
) {
    let action = match task.spec {
        ActionSpec::PlayCard {
            card_handle,
            target_handle,
            por_class,
        } => CompactAction {
            kind: CompactActionKind::PlayCard {
                card_handle: card_handle.0,
                target_handle: target_handle.map(|h| h.0),
            },
            turn: task.parent_turn,
            por_class,
        },
        ActionSpec::UsePotion {
            potion_slot,
            potion_id,
            target_handle,
        } => CompactAction {
            kind: CompactActionKind::UsePotion {
                potion_slot: potion_slot as u16,
                potion_id,
                target_handle: target_handle.map(|h| h.0),
            },
            turn: task.parent_turn,
            por_class: PorActionClass::Barrier,
        },
        ActionSpec::EndTurn => CompactAction {
            kind: CompactActionKind::EndTurn,
            turn: task.parent_turn,
            por_class: PorActionClass::Barrier,
        },
    };

    for idx in start..end {
        if unpack_state(paused_bytes, scratch.state.static_ctx.clone(), &mut scratch.state).is_err()
        {
            continue;
        }
        let sel = &choices[idx];
        scratch.transitions += 1;
        let Ok(outcome) = CombatEngine::resume_choice(&mut scratch.state, content, sel) else {
            continue;
        };
        let mut choices_so_far = vec![sel.clone()];
        let mut sub_ord = (idx as u32) * 1024;
        match outcome {
            StepOutcome::Completed | StepOutcome::CombatEnded { .. } => {
                finalize_candidate(
                    scratch,
                    task,
                    action,
                    None,
                    choices_so_far,
                    sub_ord,
                    content,
                    root_baseline,
                    policy,
                    cert,
                    incumbent_table,
                    tt,
                    stand_pat_cache,
                    sensitivity,
                );
            }
            StepOutcome::NeedsChoice(_) => {
                if allows_brightest_flame(
                    policy.brightest_flame_max_hp_loss_limit,
                    root_baseline.initial_brightest_flame_max_hp_spent,
                    root_baseline.brightest_flame_spent(&scratch.state),
                ) {
                    let mut next_paused = Vec::new();
                    pack_state(&scratch.state, &mut next_paused);
                    resolve_choices_recursive(
                        scratch,
                        &next_paused,
                        &mut choices_so_far,
                        &mut sub_ord,
                        1,
                        task,
                        action,
                        content,
                        root_baseline,
                        policy,
                        profile,
                        cert,
                        incumbent_table,
                        tt,
                        stand_pat_cache,
                        sensitivity,
                        None,
                    );
                }
            }
        }
    }
}

#[allow(clippy::too_many_arguments)]
fn expand_wave_tasks(
    tasks: Vec<ActionTask>,
    dop: usize,
    pool: Option<&rayon::ThreadPool>,
    static_ctx: &Arc<RootStaticContext>,
    main_arena: &MainStateArena,
    content: &(dyn ContentDispatcher + Sync),
    root_baseline: &RootEvalBaseline,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    incumbent_table: &PrimaryIncumbentTable,
    tt: &ShardedParetoTT,
    stand_pat_cache: &StandPatCache,
    sensitivity: &CombatOrderSensitivity,
    cancel: &AtomicBool,
) -> Vec<WorkerOutput> {
    if dop <= 1 || tasks.len() <= 1 || pool.is_none() {
        let mut scratch = WorkerScratch::new(static_ctx.clone());
        for task in &tasks {
            if cancel.load(Ordering::Relaxed) {
                break;
            }
            execute_action_task(
                &mut scratch,
                task,
                main_arena,
                content,
                root_baseline,
                policy,
                profile,
                cert,
                incumbent_table,
                tt,
                stand_pat_cache,
                sensitivity,
                None,
            );
        }
        return vec![WorkerOutput {
            slab: scratch.slab,
            candidates: scratch.candidates,
            transitions: scratch.transitions,
            incumbent_pruned: scratch.incumbent_pruned,
            tt_probe_pruned: scratch.tt_probe_pruned,
        }];
    }

    let pool = pool.unwrap();
    let pending_items = AtomicU64::new(tasks.len() as u64);
    let injector: Injector<WorkItem> = Injector::new();
    for t in tasks {
        injector.push(WorkItem::Action(t));
    }

    let workers: Vec<DequeWorker<WorkItem>> = (0..dop).map(|_| DequeWorker::new_fifo()).collect();
    let stealers: Vec<Stealer<WorkItem>> = workers.iter().map(|w| w.stealer()).collect();
    let outputs: Vec<parking_lot::Mutex<Option<WorkerOutput>>> =
        (0..dop).map(|_| parking_lot::Mutex::new(None)).collect();

    pool.scope(|scope| {
        let stealers_ref = &stealers;
        let injector_ref = &injector;
        let pending_ref = &pending_items;
        let outputs_ref = &outputs;

        for (worker_idx, local) in workers.into_iter().enumerate() {
            let ctx = static_ctx.clone();
            scope.spawn(move |_| {
                let mut scratch = WorkerScratch::new(ctx);

                loop {
                    if cancel.load(Ordering::Relaxed)
                        || pending_ref.load(Ordering::Acquire) == 0
                    {
                        break;
                    }
                    let next_item = local
                        .pop()
                        .or_else(|| {
                            std::iter::repeat_with(|| injector_ref.steal_batch_and_pop(&local))
                                .find(|s| !s.is_retry())
                                .and_then(|s| s.success())
                        })
                        .or_else(|| {
                            for i in 1..stealers_ref.len() {
                                let target = (worker_idx + i) % stealers_ref.len();
                                loop {
                                    match stealers_ref[target].steal_batch_and_pop(&local) {
                                        Steal::Success(item) => return Some(item),
                                        Steal::Empty => break,
                                        Steal::Retry => continue,
                                    }
                                }
                            }
                            None
                        });

                    if let Some(item) = next_item {
                        match item {
                            WorkItem::Action(task) => {
                                execute_action_task(
                                    &mut scratch,
                                    &task,
                                    main_arena,
                                    content,
                                    root_baseline,
                                    policy,
                                    profile,
                                    cert,
                                    incumbent_table,
                                    tt,
                                    stand_pat_cache,
                                    sensitivity,
                                    Some((injector_ref, pending_ref)),
                                );
                            }
                            WorkItem::ChoiceRange {
                                task,
                                paused_bytes,
                                choices,
                                start,
                                end,
                            } => {
                                execute_choice_range(
                                    &mut scratch,
                                    &task,
                                    &paused_bytes,
                                    &choices,
                                    start,
                                    end,
                                    content,
                                    root_baseline,
                                    policy,
                                    profile,
                                    cert,
                                    incumbent_table,
                                    tt,
                                    stand_pat_cache,
                                    sensitivity,
                                );
                            }
                        }
                        pending_ref.fetch_sub(1, Ordering::AcqRel);
                    } else {
                        std::thread::yield_now();
                    }
                }

                *outputs_ref[worker_idx].lock() = Some(WorkerOutput {
                    slab: scratch.slab,
                    candidates: scratch.candidates,
                    transitions: scratch.transitions,
                    incumbent_pruned: scratch.incumbent_pruned,
                    tt_probe_pruned: scratch.tt_probe_pruned,
                });
            });
        }
    });

    outputs
        .into_iter()
        .filter_map(|slot| slot.into_inner())
        .collect()
}

fn replay_fixed_prefix(
    root: &BattleState,
    prefix: &[PlannedAction],
    policy: &SearchPolicy,
    renewable_rock: bool,
    content: &dyn ContentDispatcher,
) -> Result<(BattleState, u16, i32, u16, u64, i32, u16), KernelError> {
    let mut state = root.clone();
    let mut potion_count = 0u16;
    let mut potion_cost = 0i32;
    let mut ambergris_count = 0u16;
    let mut forced_used_mask = 0u64;
    let mut forced_strategic_hp_cost = 0i32;
    let mut forced_ambergris_count = 0u16;
    for act in prefix {
        match act {
            PlannedAction::PlayCard {
                card_handle,
                target_handle,
                choices,
            } => {
                let mut q: SmallVec<[Vec<usize>; 2]> =
                    choices.clone().unwrap_or_default().into_iter().collect();
                let mut out = CombatEngine::play_card(
                    &mut state,
                    content,
                    CardHandle(*card_handle as u32),
                    target_handle.map(|h| CreatureHandle(h as u32)),
                    &mut q,
                )?;
                while matches!(out, StepOutcome::NeedsChoice(_)) && !q.is_empty() {
                    let sel = q.remove(0);
                    out = CombatEngine::resume_choice(&mut state, content, &sel)?;
                }
            }
            PlannedAction::UsePotion {
                potion_slot,
                potion_id,
                target_handle,
                choices,
            } => {
                let cost = PotionValuationRegistry::strategic_hp_cost_with_overrides(
                    *potion_id,
                    renewable_rock,
                    &policy.potion_hp_threshold_overrides,
                );
                let amb = u16::from(*potion_id == PotionId::Ambergris);
                potion_count += 1;
                potion_cost += cost;
                ambergris_count += amb;
                if let Some(bit_idx) = policy.forced_bit_for(*potion_slot, *potion_id) {
                    let bit = if bit_idx < 64 { 1u64 << bit_idx } else { 0 };
                    if (forced_used_mask & bit) == 0 {
                        forced_used_mask |= bit;
                        forced_strategic_hp_cost += cost;
                        forced_ambergris_count += amb;
                    }
                }
                let mut q: SmallVec<[Vec<usize>; 2]> =
                    choices.clone().unwrap_or_default().into_iter().collect();
                let mut out = CombatEngine::use_potion(
                    &mut state,
                    content,
                    *potion_slot,
                    target_handle.map(|h| CreatureHandle(h as u32)),
                    &mut q,
                )?;
                while matches!(out, StepOutcome::NeedsChoice(_)) && !q.is_empty() {
                    let sel = q.remove(0);
                    out = CombatEngine::resume_choice(&mut state, content, &sel)?;
                }
            }
            PlannedAction::EndTurn { choices } => {
                let mut q: SmallVec<[Vec<usize>; 2]> =
                    choices.clone().unwrap_or_default().into_iter().collect();
                let mut out = CombatEngine::end_player_turn(&mut state, content)?;
                while matches!(out, StepOutcome::NeedsChoice(_)) && !q.is_empty() {
                    let sel = q.remove(0);
                    out = CombatEngine::resume_choice(&mut state, content, &sel)?;
                }
            }
        }
    }
    Ok((
        state,
        potion_count,
        potion_cost,
        ambergris_count,
        forced_used_mask,
        forced_strategic_hp_cost,
        forced_ambergris_count,
    ))
}

#[allow(clippy::too_many_arguments)]
fn run_beam_pass(
    root: &BattleState,
    content: &(dyn ContentDispatcher + Sync),
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    incumbent_table: &PrimaryIncumbentTable,
    stand_pat_cache: &StandPatCache,
    cancel: &AtomicBool,
    progress_cb: Option<&dyn Fn(&SearchProgress)>,
    early_turn_scout_depth: i32,
    boss_tempo_scout_turns: i32,
    fixed_prefix: Option<&[PlannedAction]>,
) -> Result<BeamRunOutput, KernelError> {
    let started = Instant::now();
    let root_baseline = RootEvalBaseline::from_state_with_policy(root, content, policy);
    let sensitivity = CombatOrderSensitivity::analyze(root, content);
    let dop = policy
        .max_degree_of_parallelism
        .clamp(1, crate::eval::weights::MAXIMUM_SEARCH_MAX_DEGREE_OF_PARALLELISM);
    let pool: Option<rayon::ThreadPool> = if dop > 1 {
        Some(
            rayon::ThreadPoolBuilder::new()
                .num_threads(dop)
                .build()
                .expect("failed to build search thread pool"),
        )
    } else {
        None
    };

    let (
        start_state,
        prefix_potions,
        prefix_potion_cost,
        prefix_ambergris,
        prefix_forced_mask,
        prefix_forced_cost,
        prefix_forced_amb,
    ) = if let Some(prefix) = fixed_prefix {
        replay_fixed_prefix(
            root,
            prefix,
            policy,
            root_baseline.renewable_potion_shaped_rock,
            content,
        )?
    } else {
        (root.clone(), 0u16, 0i32, 0u16, 0u64, 0i32, 0u16)
    };

    let mut arena = NodeArena::default();
    let mut main_arena = MainStateArena::default();
    let mut tt = ShardedParetoTT::new(65_536);

    let mut pack_buf = Vec::with_capacity(4096);
    let mut key_buf = Vec::with_capacity(2048);
    pack_state(&start_state, &mut pack_buf);
    pack_canonical_key(&start_state, &mut key_buf);
    let mut packed_state_bytes_total: u64 = pack_buf.len() as u64;
    let root_hash = hash_bytes_128(&key_buf);
    let root_state_handle = main_arena.commit_slice(&pack_buf);
    let root_key_handle = main_arena.commit_slice(&key_buf);
    let root_fast = evaluate_fast(&start_state, content, &root_baseline, None);
    let root_full = evaluate_full(&start_state, content, &root_fast, &root_baseline);
    let root_full_idx = arena.full_evals.len() as u32;
    arena.full_evals.push(root_full);

    let prefix_len = fixed_prefix.map_or(0, |p| p.len() as u16);
    let root_id = arena.push_node(SearchNode {
        parent: None,
        action: None,
        extra_end_turn: None,
        choices_offset: 0,
        choices_len: 0,
        state_handle: root_state_handle,
        canonical_key_handle: root_key_handle,
        state_hash: root_hash,
        turn: start_state.player.turn_number,
        action_count: prefix_len,
        potion_count: prefix_potions,
        explicit_potion_count: prefix_potions,
        automatic_potion_count: 0,
        potion_strategic_cost: prefix_potion_cost,
        explicit_potion_strategic_cost: prefix_potion_cost,
        ambergris_count: prefix_ambergris,
        forced_used_mask: prefix_forced_mask,
        forced_strategic_hp_cost: prefix_forced_cost,
        forced_ambergris_count: prefix_forced_amb,
        future_sold_hp: 0,
        retention_rank: 0,
        is_terminal: !start_state.is_in_progress || root_fast.won || root_fast.player_dead,
        boundary_rank: 0,
        fast_eval: root_fast,
        full_eval_idx: Some(root_full_idx),
    });

    let mut frontier: Vec<NodeId> = Vec::new();
    let mut completed: Vec<NodeId> = Vec::new();
    if arena.get(root_id).is_terminal {
        completed.push(root_id);
    } else {
        frontier.push(root_id);
    }

    let mut expanded_nodes = 0usize;
    let mut transition_count = 0u64;
    let mut incumbent_pruned = 0u64;
    let mut por_pruned = 0u64;
    let mut termination: &'static str = "None";
    let mut scratch_unpack = empty_scratch_state(root.static_ctx.clone());
    let mut early_turn_frontiers = Vec::new();
    let mut boss_tempo_prefixes = Vec::new();
    let mut last_scout_turn = start_state.player.turn_number;

    while !frontier.is_empty() {
        if cancel.load(Ordering::Relaxed) {
            termination = "Cancelled";
            break;
        }
        if expanded_nodes >= profile.max_expanded_nodes {
            termination = BeamWidthPortfolio::NODE_LIMIT_TERMINATION;
            break;
        }
        if profile.soft_time_budget_ms > 0
            && (started.elapsed().as_millis() as u64) >= profile.soft_time_budget_ms
        {
            termination = "TimeLimit";
            break;
        }

        // Check early-turn frontier capture when turn advances
        if early_turn_scout_depth > 0 {
            let max_turn = frontier
                .iter()
                .map(|&nid| arena.get(nid).turn)
                .max()
                .unwrap_or(last_scout_turn);
            if max_turn > last_scout_turn {
                for t in (last_scout_turn + 1)..=max_turn {
                    let completed_turns = t - root_baseline.start_turn_number;
                    if completed_turns >= 1 && completed_turns <= early_turn_scout_depth {
                        let f = select_early_turn_frontier(
                            &arena,
                            &frontier,
                            root_baseline.start_turn_number,
                            completed_turns,
                        );
                        early_turn_frontiers.extend(f);
                    }
                }
                last_scout_turn = max_turn;
                if max_turn - root_baseline.start_turn_number >= early_turn_scout_depth {
                    termination = "ScoutDepth";
                    break;
                }
            }
        }

        if boss_tempo_scout_turns > 0 {
            for &nid in &frontier {
                let n = arena.get(nid);
                if n.turn >= root_baseline.start_turn_number + boss_tempo_scout_turns
                    && !n.fast_eval.player_dead
                {
                    let eliminated = (root_baseline.initial_enemy_count as i32)
                        - n.fast_eval.alive_enemy_count;
                    let rank = arena
                        .full_eval(nid)
                        .map_or(n.fast_eval.base_score, |e| e.beam_rank_score);
                    let mut actions = fixed_prefix.map_or_else(Vec::new, |p| p.to_vec());
                    actions.extend(arena.reconstruct_actions(nid));
                    let forced_uses_satisfied = (n.forced_used_mask & root_baseline.all_forced_potion_mask)
                        == root_baseline.all_forced_potion_mask;
                    retain_boss_tempo_prefixes(
                        &mut boss_tempo_prefixes,
                        BossTempoPrefix {
                            actions,
                            state_hash: n.state_hash,
                            forced_uses_satisfied,
                            eliminated_enemies: eliminated,
                            potion_count: n.potion_count as usize,
                            rank,
                        },
                    );
                }
            }
            frontier.retain(|&nid| {
                arena.get(nid).turn < root_baseline.start_turn_number + boss_tempo_scout_turns
            });
            if frontier.is_empty() {
                termination = "BossTempoScout";
                break;
            }
        }

        // Enumerate action tasks for every parent in this wave deterministically
        let max_parents_this_wave = profile
            .max_expanded_nodes
            .saturating_sub(expanded_nodes)
            .min(frontier.len());
        let parent_slice = &frontier[..max_parents_this_wave];
        expanded_nodes += max_parents_this_wave;

        let mut tasks = Vec::with_capacity(max_parents_this_wave * 6);
        if let Some(ref p) = pool {
            if max_parents_this_wave >= 4 {
                let per_parent: Vec<(Vec<ActionTask>, u64)> = p.install(|| {
                    parent_slice
                        .par_iter()
                        .enumerate()
                        .map_init(
                            || empty_scratch_state(root.static_ctx.clone()),
                            |scratch, (parent_rank, &pid)| {
                                let pnode = arena.get(pid);
                                let pbytes = main_arena.get(pnode.state_handle);
                                let mut local_tasks = Vec::with_capacity(8);
                                let mut local_por = 0u64;
                                if unpack_state(pbytes, root.static_ctx.clone(), scratch).is_ok() {
                                    enumerate_parent_tasks(
                                        parent_rank as u32,
                                        pid,
                                        pnode,
                                        scratch,
                                        content,
                                        policy,
                                        profile,
                                        &sensitivity,
                                        &mut local_tasks,
                                        &mut local_por,
                                    );
                                }
                                (local_tasks, local_por)
                            },
                        )
                        .collect()
                });
                for (mut lt, lpor) in per_parent {
                    por_pruned += lpor;
                    tasks.append(&mut lt);
                }
            } else {
                for (parent_rank, &pid) in parent_slice.iter().enumerate() {
                    let pnode = arena.get(pid).clone();
                    let pbytes = main_arena.get(pnode.state_handle);
                    if unpack_state(pbytes, root.static_ctx.clone(), &mut scratch_unpack).is_err() {
                        continue;
                    }
                    enumerate_parent_tasks(
                        parent_rank as u32,
                        pid,
                        &pnode,
                        &mut scratch_unpack,
                        content,
                        policy,
                        profile,
                        &sensitivity,
                        &mut tasks,
                        &mut por_pruned,
                    );
                }
            }
        } else {
            for (parent_rank, &pid) in parent_slice.iter().enumerate() {
                let pnode = arena.get(pid).clone();
                let pbytes = main_arena.get(pnode.state_handle);
                if unpack_state(pbytes, root.static_ctx.clone(), &mut scratch_unpack).is_err() {
                    continue;
                }
                enumerate_parent_tasks(
                    parent_rank as u32,
                    pid,
                    &pnode,
                    &mut scratch_unpack,
                    content,
                    policy,
                    profile,
                    &sensitivity,
                    &mut tasks,
                    &mut por_pruned,
                );
            }
        }

        if tasks.is_empty() {
            break;
        }

        let worker_outputs = expand_wave_tasks(
            tasks,
            dop,
            pool.as_ref(),
            &root.static_ctx,
            &main_arena,
            content,
            &root_baseline,
            policy,
            profile,
            cert,
            incumbent_table,
            &tt,
            stand_pat_cache,
            &sensitivity,
            cancel,
        );

        // Collect transitions, stats, and bucket candidates into 256 TT shards
        let mut shard_buckets: Vec<Vec<(usize, StateHash, &[u8], TranspositionLabel, (usize, usize))>> =
            (0..TT_SHARD_COUNT).map(|_| Vec::new()).collect();

        let mut sort_list: Vec<(usize, usize, (u32, u32, u32))> = Vec::new();
        for (w_idx, wo) in worker_outputs.iter().enumerate() {
            transition_count += wo.transitions;
            incumbent_pruned += wo.incumbent_pruned;
            tt.branches_pruned += wo.tt_probe_pruned;
            for (c_idx, cand) in wo.candidates.iter().enumerate() {
                sort_list.push((w_idx, c_idx, cand.sort_key));
            }
        }
        // Global deterministic sort guarantees identical TT insertion order across DOP=1, 4, and 16.
        sort_list.sort_unstable_by_key(|&(_, _, sk)| sk);

        for (global_ord, &(w_idx, c_idx, _)) in sort_list.iter().enumerate() {
            let wo = &worker_outputs[w_idx];
            let cand = &wo.candidates[c_idx];
            let key_slice = wo.slab.get(cand.key_slab_idx, cand.key_off, cand.key_len);
            let shard = cand.state_hash.shard_index();
            shard_buckets[shard].push((
                global_ord,
                cand.state_hash,
                key_slice,
                cand.label,
                (w_idx, c_idx),
            ));
        }

        let accepted = if let Some(ref p) = pool {
            p.install(|| tt.insert_sharded_batch(shard_buckets, true, |_| {}))
        } else {
            tt.insert_sharded_batch(shard_buckets, false, |_| {})
        };

        let mut next_frontier_candidates: Vec<NodeId> = Vec::with_capacity(accepted.len());
        let mut hp_target_reached = false;
        for (_, (w_idx, c_idx)) in accepted {
            let wo = &worker_outputs[w_idx];
            let cand = &wo.candidates[c_idx];
            let state_bytes = wo.slab.get(cand.state_slab_idx, cand.state_off, cand.state_len);
            let key_bytes = wo.slab.get(cand.key_slab_idx, cand.key_off, cand.key_len);
            packed_state_bytes_total += state_bytes.len() as u64;
            let state_handle = main_arena.commit_slice(state_bytes);
            let canonical_key_handle = main_arena.commit_slice(key_bytes);
            let (choices_offset, choices_len) = arena.choices.push_choices(&cand.choices);

            let nid = arena.push_node(SearchNode {
                parent: Some(cand.parent_id),
                action: Some(cand.action),
                extra_end_turn: cand.extra_end_turn,
                choices_offset,
                choices_len,
                state_handle,
                canonical_key_handle,
                state_hash: cand.state_hash,
                turn: cand.turn,
                action_count: cand.action_count,
                potion_count: cand.potion_count,
                explicit_potion_count: cand.explicit_potion_count,
                automatic_potion_count: cand.automatic_potion_count,
                potion_strategic_cost: cand.potion_strategic_cost,
                explicit_potion_strategic_cost: cand.explicit_potion_strategic_cost,
                ambergris_count: cand.ambergris_count,
                forced_used_mask: cand.forced_used_mask,
                forced_strategic_hp_cost: cand.forced_strategic_hp_cost,
                forced_ambergris_count: cand.forced_ambergris_count,
                future_sold_hp: 0,
                retention_rank: 0,
                is_terminal: cand.is_terminal,
                boundary_rank: 0,
                fast_eval: cand.fast_eval,
                full_eval_idx: None,
            });

            if cand.is_terminal {
                if cand.fast_eval.won {
                    let relic_heal = crate::eval::ActEndingBossPolicy::ranked_post_combat_relic_heal(
                        root_baseline.post_combat_relic_heal,
                        true,
                        cand.fast_eval.player_hp,
                        cand.fast_eval.player_max_hp,
                    );
                    let deficit = crate::eval::ActEndingBossPolicy::strategic_hp_deficit(
                        cand.fast_eval.cumulative_hp_lost,
                        (root_baseline.initial_player_max_hp - cand.fast_eval.player_max_hp).max(0),
                        cand.fast_eval.recovered_hp + relic_heal,
                        root_baseline.boss_hp_relief,
                        cand.fast_eval.death_save_hp_restored,
                    ) - cand.fast_eval.strategic_hp_credit;
                    incumbent_table.tighten(
                        PrimaryIncumbentBucket {
                            potions: cand.potion_count,
                            forfeits_gold: false,
                        },
                        PrimarySearchIncumbent {
                            strategic_hp_deficit: deficit,
                            combat_ended_turn: cand.turn,
                            potion_strategic_cost: cand.potion_strategic_cost,
                        },
                    );
                    let node_ref = arena.get(nid);
                    if early_turn_scout_depth == 0
                        && boss_tempo_scout_turns == 0
                        && has_reached_acceptable_battle_hp_loss_node(
                            &root_baseline,
                            policy,
                            node_ref,
                        )
                    {
                        hp_target_reached = true;
                    }
                }
                completed.push(nid);
            } else {
                next_frontier_candidates.push(nid);
            }
        }
        if hp_target_reached {
            termination = "HpTarget";
            break;
        }

        // Lazy Stage-2 Evaluation ONLY for surviving non-terminal candidates before beam pruning
        if let Some(ref p) = pool {
            if next_frontier_candidates.len() >= 4 {
                let evals: Vec<(NodeId, Option<crate::eval::FullEvalDetails>)> = p.install(|| {
                    next_frontier_candidates
                        .par_iter()
                        .map_init(
                            || empty_scratch_state(root.static_ctx.clone()),
                            |scratch, &nid| {
                                let n = arena.get(nid);
                                let sbytes = main_arena.get(n.state_handle);
                                if unpack_state(sbytes, root.static_ctx.clone(), scratch).is_ok() {
                                    let mut full = evaluate_full(
                                        scratch,
                                        content,
                                        &n.fast_eval,
                                        &root_baseline,
                                    );
                                    full.beam_rank_score = compute_beam_rank_score(
                                        &n.fast_eval,
                                        &full,
                                        &root_baseline,
                                        profile.base_score_only,
                                        profile.boss_tempo_hp_pricing,
                                        profile.beam_weight_perturbation,
                                    );
                                    (nid, Some(full))
                                } else {
                                    (nid, None)
                                }
                            },
                        )
                        .collect()
                });
                for (nid, maybe_full) in evals {
                    if let Some(full) = maybe_full {
                        let idx = arena.full_evals.len() as u32;
                        arena.full_evals.push(full);
                        arena.get_mut(nid).full_eval_idx = Some(idx);
                    }
                }
            } else {
                for &nid in &next_frontier_candidates {
                    let n = arena.get(nid);
                    let sbytes = main_arena.get(n.state_handle);
                    if unpack_state(sbytes, root.static_ctx.clone(), &mut scratch_unpack).is_ok() {
                        let mut full = evaluate_full(
                            &scratch_unpack,
                            content,
                            &n.fast_eval,
                            &root_baseline,
                        );
                        full.beam_rank_score = compute_beam_rank_score(
                            &n.fast_eval,
                            &full,
                            &root_baseline,
                            profile.base_score_only,
                            profile.boss_tempo_hp_pricing,
                            profile.beam_weight_perturbation,
                        );
                        let idx = arena.full_evals.len() as u32;
                        arena.full_evals.push(full);
                        arena.get_mut(nid).full_eval_idx = Some(idx);
                    }
                }
            }
        } else {
            for &nid in &next_frontier_candidates {
                let n = arena.get(nid);
                let sbytes = main_arena.get(n.state_handle);
                if unpack_state(sbytes, root.static_ctx.clone(), &mut scratch_unpack).is_ok() {
                    let mut full =
                        evaluate_full(&scratch_unpack, content, &n.fast_eval, &root_baseline);
                    full.beam_rank_score = compute_beam_rank_score(
                        &n.fast_eval,
                        &full,
                        &root_baseline,
                        profile.base_score_only,
                        profile.boss_tempo_hp_pricing,
                        profile.beam_weight_perturbation,
                    );
                    let idx = arena.full_evals.len() as u32;
                    arena.full_evals.push(full);
                    arena.get_mut(nid).full_eval_idx = Some(idx);
                }
            }
        }

        frontier = BeamRetentionPolicy::prune_wave(
            &mut arena,
            &next_frontier_candidates,
            profile.beam_width,
            profile.potion_free_beam_width,
            profile.potion_beam_width,
            &root_baseline,
            profile.second_rank_band,
            profile.aggressive_power_commitment,
        );

        if let Some(cb) = progress_cb {
            let best_won = completed.iter().any(|&id| arena.get(id).fast_eval.won);
            let best_hp_lost = completed
                .iter()
                .filter(|&&id| arena.get(id).fast_eval.won)
                .map(|&id| arena.get(id).fast_eval.cumulative_hp_lost)
                .min();
            cb(&SearchProgress {
                nodes_expanded: expanded_nodes,
                transition_count,
                current_turn: frontier
                    .first()
                    .map_or(root_baseline.start_turn_number, |&id| arena.get(id).turn),
                best_won,
                best_hp_lost,
                elapsed_ms: started.elapsed().as_millis() as u64,
            });
        }
    }

    if completed.is_empty() {
        completed.extend(frontier.iter().copied());
    }

    let selected = select_final_plan(
        &arena,
        &completed,
        &root_baseline,
        policy.potion_policy,
        Some(policy.theft_policy),
        root_baseline.potion_replacement_hp_credit,
        root_baseline.minimum_required_potion_uses,
    );

    let (
        best_node,
        won,
        all_forced_uses_satisfied,
        strategic_hp_deficit,
        strategy_goal_hp_credit,
        strategy_goal_count,
        growth_rewards,
        relic_eval,
        recovered_hp,
        hp_lost,
        potion_count,
        score,
        actions,
    ) = match selected {
        Some(sel) => {
            let mut acts = fixed_prefix.map_or_else(Vec::new, |p| p.to_vec());
            acts.extend(arena.reconstruct_actions(sel.node_id));
            (
                Some(sel.node_id),
                sel.won,
                sel.all_forced_uses_satisfied,
                sel.strategic_hp_deficit,
                sel.strategy_goal_hp_credit,
                sel.strategy_goal_count,
                sel.growth_rewards,
                sel.relic_eval,
                sel.recovered_hp,
                sel.hp_lost,
                sel.potion_count,
                sel.score,
                acts,
            )
        }
        None => (
            None,
            false,
            root_baseline.all_forced_potion_mask == 0,
            i32::MAX,
            0,
            0,
            GrowthCounters::default(),
            RelicCounterEvaluation::default(),
            0,
            0,
            0,
            0,
            Vec::new(),
        ),
    };

    let terminal_cards_hint = start_state
        .player
        .hand
        .iter()
        .filter_map(|&ch| start_state.card(ch).map(|c| c.id))
        .collect();

    Ok(BeamRunOutput {
        best_node,
        arena,
        root_baseline,
        won,
        all_forced_uses_satisfied,
        strategic_hp_deficit,
        strategy_goal_hp_credit,
        strategy_goal_count,
        growth_rewards,
        relic_eval,
        recovered_hp,
        hp_lost,
        potion_count,
        score,
        actions,
        expanded_nodes,
        transition_count,
        tt_pruned: tt.branches_pruned,
        incumbent_pruned,
        por_pruned,
        packed_state_bytes_total,
        termination,
        terminal_cards_hint,
        early_turn_frontiers,
        boss_tempo_prefixes,
    })
}

fn run_novelty_pass(
    root: &BattleState,
    content: &(dyn ContentDispatcher + Sync),
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    incumbent_table: &PrimaryIncumbentTable,
    stand_pat_cache: &StandPatCache,
    cancel: &AtomicBool,
    options: &NoveltySearchOptions,
) -> Result<BeamRunOutput, KernelError> {
    let started = Instant::now();
    let root_baseline = RootEvalBaseline::from_state_with_policy(root, content, policy);
    let sensitivity = CombatOrderSensitivity::analyze(root, content);
    let mut arena = NodeArena::default();
    let mut main_arena = MainStateArena::default();
    let mut scratch = WorkerScratch::new(root.static_ctx.clone());
    let tt = ShardedParetoTT::new(65_536);

    pack_state(root, &mut scratch.pack_buf);
    pack_canonical_key(root, &mut scratch.key_buf);
    let root_hash = hash_bytes_128(&scratch.key_buf);
    let root_fast = evaluate_fast(root, content, &root_baseline, None);
    let root_full = evaluate_full(root, content, &root_fast, &root_baseline);
    let root_full_idx = arena.full_evals.len() as u32;
    arena.full_evals.push(root_full);

    let root_id = arena.push_node(SearchNode {
        parent: None,
        action: None,
        extra_end_turn: None,
        choices_offset: 0,
        choices_len: 0,
        state_handle: main_arena.commit_slice(&scratch.pack_buf),
        canonical_key_handle: main_arena.commit_slice(&scratch.key_buf),
        state_hash: root_hash,
        turn: root.player.turn_number,
        action_count: 0,
        potion_count: 0,
        explicit_potion_count: 0,
        automatic_potion_count: 0,
        potion_strategic_cost: 0,
        explicit_potion_strategic_cost: 0,
        ambergris_count: 0,
        forced_used_mask: 0,
        forced_strategic_hp_cost: 0,
        forced_ambergris_count: 0,
        future_sold_hp: 0,
        retention_rank: 0,
        is_terminal: !root.is_in_progress || root_fast.won || root_fast.player_dead,
        boundary_rank: 0,
        fast_eval: root_fast,
        full_eval_idx: Some(root_full_idx),
    });

    let mut open = BfwsBoundedOpen::new(options.max_open);
    let mut novelty = BfwsPackedNovelty::new(options.width, options.max_novelty_entries);
    let mut escapes: Vec<BfwsEscapeBudget> = Vec::new();
    let mut facts_buf: Vec<BfwsFact> = Vec::with_capacity(64);
    let mut completed = Vec::new();
    let mut sequence = 0u64;

    capture_novelty_facts(root, &root_fast, &root_full, &mut facts_buf);
    let w0 = novelty.evaluate((root_fast.enemy_hp / 10, 0), &facts_buf);
    if arena.get(root_id).is_terminal {
        completed.push(root_id);
    } else {
        let esc_idx = if options.familiar_allowance > 0 {
            let i = escapes.len();
            escapes.push(BfwsEscapeBudget::new(options.familiar_allowance));
            Some(i)
        } else {
            None
        };
        open.enqueue(
            NoveltyOpenNode {
                node_id: root_id,
                escape_idx: esc_idx,
            },
            w0 as i32,
            root_fast.base_score,
            sequence,
        );
        sequence += 1;
    }

    let mut expanded_nodes = 0usize;
    let mut por_pruned = 0u64;
    let mut termination = "OpenExhausted";

    while let Some(item) = open.dequeue() {
        if cancel.load(Ordering::Relaxed) {
            termination = "Cancelled";
            break;
        }
        if expanded_nodes >= profile.max_expanded_nodes {
            termination = BeamWidthPortfolio::NODE_LIMIT_TERMINATION;
            break;
        }
        if profile.soft_time_budget_ms > 0
            && (started.elapsed().as_millis() as u64) >= profile.soft_time_budget_ms
        {
            termination = "TimeLimit";
            break;
        }

        let pnode = arena.get(item.node_id).clone();
        if pnode.action_count as usize >= options.max_actions
            || pnode.turn >= root_baseline.start_turn_number + options.max_turns
        {
            continue;
        }

        expanded_nodes += 1;
        let pbytes = main_arena.get(pnode.state_handle);
        if unpack_state(pbytes, root.static_ctx.clone(), &mut scratch.state).is_err() {
            continue;
        }
        let pfull = arena.full_eval(item.node_id).copied().unwrap_or_default();
        capture_novelty_facts(&scratch.state, &pnode.fast_eval, &pfull, &mut facts_buf);
        novelty.prepare_parent(
            (pnode.fast_eval.enemy_hp / 10, pnode.potion_count as i32),
            &facts_buf,
        );

        let mut tasks = Vec::new();
        enumerate_parent_tasks(
            0,
            item.node_id,
            &pnode,
            &mut scratch.state,
            content,
            policy,
            profile,
            &sensitivity,
            &mut tasks,
            &mut por_pruned,
        );

        scratch.candidates.clear();
        for task in &tasks {
            execute_action_task(
                &mut scratch,
                task,
                &main_arena,
                content,
                &root_baseline,
                policy,
                profile,
                cert,
                incumbent_table,
                &tt,
                stand_pat_cache,
                &sensitivity,
                None,
            );
        }

        let children: Vec<WorkerCandidate> = scratch.candidates.drain(..).collect();
        for cand in children {
            let state_bytes = scratch.slab.get(cand.state_slab_idx, cand.state_off, cand.state_len);
            let key_bytes = scratch.slab.get(cand.key_slab_idx, cand.key_off, cand.key_len);
            if unpack_state(state_bytes, root.static_ctx.clone(), &mut scratch.state).is_err() {
                continue;
            }
            let cfull = evaluate_full(&scratch.state, content, &cand.fast_eval, &root_baseline);
            capture_novelty_facts(&scratch.state, &cand.fast_eval, &cfull, &mut facts_buf);
            let w = novelty.evaluate(
                (cand.fast_eval.enemy_hp / 10, cand.potion_count as i32),
                &facts_buf,
            );

            let (choices_offset, choices_len) = arena.choices.push_choices(&cand.choices);
            let full_idx = arena.full_evals.len() as u32;
            arena.full_evals.push(cfull);
            let cid = arena.push_node(SearchNode {
                parent: Some(cand.parent_id),
                action: Some(cand.action),
                extra_end_turn: cand.extra_end_turn,
                choices_offset,
                choices_len,
                state_handle: main_arena.commit_slice(state_bytes),
                canonical_key_handle: main_arena.commit_slice(key_bytes),
                state_hash: cand.state_hash,
                turn: cand.turn,
                action_count: cand.action_count,
                potion_count: cand.potion_count,
                explicit_potion_count: cand.explicit_potion_count,
                automatic_potion_count: cand.automatic_potion_count,
                potion_strategic_cost: cand.potion_strategic_cost,
                explicit_potion_strategic_cost: cand.explicit_potion_strategic_cost,
                ambergris_count: cand.ambergris_count,
                forced_used_mask: cand.forced_used_mask,
                forced_strategic_hp_cost: cand.forced_strategic_hp_cost,
                forced_ambergris_count: cand.forced_ambergris_count,
                future_sold_hp: 0,
                retention_rank: 0,
                is_terminal: cand.is_terminal,
                boundary_rank: 0,
                fast_eval: cand.fast_eval,
                full_eval_idx: Some(full_idx),
            });

            if cand.is_terminal {
                completed.push(cid);
                if has_reached_acceptable_battle_hp_loss_node(
                    &root_baseline,
                    policy,
                    arena.get(cid),
                ) {
                    termination = "HpTarget";
                    break;
                }
                continue;
            }

            let mut admitted = w <= options.width;
            if !admitted {
                if let Some(ei) = item.escape_idx {
                    admitted = escapes[ei].try_admit();
                }
            }
            if !admitted {
                continue;
            }
            let child_escape = if options.familiar_allowance > 0 {
                if w <= options.width {
                    let i = escapes.len();
                    escapes.push(BfwsEscapeBudget::new(options.familiar_allowance));
                    Some(i)
                } else {
                    item.escape_idx
                }
            } else {
                None
            };
            open.enqueue(
                NoveltyOpenNode {
                    node_id: cid,
                    escape_idx: child_escape,
                },
                w as i32,
                cand.fast_eval.base_score,
                sequence,
            );
            sequence += 1;
        }
        if termination == "HpTarget" || novelty.limit_reached {
            break;
        }
    }

    let selected = select_final_plan(
        &arena,
        &completed,
        &root_baseline,
        policy.potion_policy,
        Some(policy.theft_policy),
        root_baseline.potion_replacement_hp_credit,
        root_baseline.minimum_required_potion_uses,
    );
    let (
        best_node,
        won,
        all_forced_uses_satisfied,
        strategic_hp_deficit,
        strategy_goal_hp_credit,
        strategy_goal_count,
        growth_rewards,
        relic_eval,
        recovered_hp,
        hp_lost,
        potion_count,
        score,
        actions,
    ) = match selected {
        Some(sel) => (
            Some(sel.node_id),
            sel.won,
            sel.all_forced_uses_satisfied,
            sel.strategic_hp_deficit,
            sel.strategy_goal_hp_credit,
            sel.strategy_goal_count,
            sel.growth_rewards,
            sel.relic_eval,
            sel.recovered_hp,
            sel.hp_lost,
            sel.potion_count,
            sel.score,
            arena.reconstruct_actions(sel.node_id),
        ),
        None => (
            None,
            false,
            root_baseline.all_forced_potion_mask == 0,
            i32::MAX,
            0,
            0,
            GrowthCounters::default(),
            RelicCounterEvaluation::default(),
            0,
            0,
            0,
            0,
            Vec::new(),
        ),
    };

    let total_committed = main_arena.total_bytes_committed;
    Ok(BeamRunOutput {
        best_node,
        arena,
        root_baseline,
        won,
        all_forced_uses_satisfied,
        strategic_hp_deficit,
        strategy_goal_hp_credit,
        strategy_goal_count,
        growth_rewards,
        relic_eval,
        recovered_hp,
        hp_lost,
        potion_count,
        score,
        actions,
        expanded_nodes,
        transition_count: scratch.transitions,
        tt_pruned: tt.branches_pruned,
        incumbent_pruned: scratch.incumbent_pruned,
        por_pruned,
        packed_state_bytes_total: total_committed,
        termination,
        terminal_cards_hint: Vec::new(),
        early_turn_frontiers: Vec::new(),
        boss_tempo_prefixes: Vec::new(),
    })
}

fn run_boss_tempo_depth_first(
    root: &BattleState,
    content: &(dyn ContentDispatcher + Sync),
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    incumbent_table: &PrimaryIncumbentTable,
    stand_pat_cache: &StandPatCache,
    cancel: &AtomicBool,
    options: &BossTempoSearchOptions,
) -> Result<BeamRunOutput, KernelError> {
    let started = Instant::now();
    let root_baseline = RootEvalBaseline::from_state_with_policy(root, content, policy);
    let sensitivity = CombatOrderSensitivity::analyze(root, content);
    let mut arena = NodeArena::default();
    let mut main_arena = MainStateArena::default();
    let mut scratch = WorkerScratch::new(root.static_ctx.clone());
    let tt = ShardedParetoTT::new(16_384);
    let mut tempo_tt = BossTempoTranspositions::new(32_768);

    pack_state(root, &mut scratch.pack_buf);
    pack_canonical_key(root, &mut scratch.key_buf);
    let root_hash = hash_bytes_128(&scratch.key_buf);
    let root_fast = evaluate_fast(root, content, &root_baseline, None);
    let root_full = evaluate_full(root, content, &root_fast, &root_baseline);
    let root_full_idx = arena.full_evals.len() as u32;
    arena.full_evals.push(root_full);

    let root_id = arena.push_node(SearchNode {
        parent: None,
        action: None,
        extra_end_turn: None,
        choices_offset: 0,
        choices_len: 0,
        state_handle: main_arena.commit_slice(&scratch.pack_buf),
        canonical_key_handle: main_arena.commit_slice(&scratch.key_buf),
        state_hash: root_hash,
        turn: root.player.turn_number,
        action_count: 0,
        potion_count: 0,
        explicit_potion_count: 0,
        automatic_potion_count: 0,
        potion_strategic_cost: 0,
        explicit_potion_strategic_cost: 0,
        ambergris_count: 0,
        forced_used_mask: 0,
        forced_strategic_hp_cost: 0,
        forced_ambergris_count: 0,
        future_sold_hp: 0,
        retention_rank: 0,
        is_terminal: !root.is_in_progress || root_fast.won || root_fast.player_dead,
        boundary_rank: 0,
        fast_eval: root_fast,
        full_eval_idx: Some(root_full_idx),
    });

    let mut pending = vec![BossTempoPendingItem {
        node_id: root_id,
        remaining_discrepancies: options.discrepancy_allowance,
    }];
    let mut completed = Vec::new();
    let mut expanded_nodes = 0usize;
    let mut por_pruned = 0u64;
    let mut termination = "IterationExhausted";

    while let Some(item) = pending.pop() {
        if cancel.load(Ordering::Relaxed) {
            termination = "Cancelled";
            break;
        }
        if expanded_nodes >= profile.max_expanded_nodes {
            termination = BeamWidthPortfolio::NODE_LIMIT_TERMINATION;
            break;
        }
        if profile.soft_time_budget_ms > 0
            && (started.elapsed().as_millis() as u64) >= profile.soft_time_budget_ms
        {
            termination = "TimeLimit";
            break;
        }
        let pnode = arena.get(item.node_id).clone();
        if pnode.is_terminal {
            completed.push(item.node_id);
            continue;
        }
        if !tempo_tt.try_mark(
            pnode.state_hash,
            item.remaining_discrepancies,
            pnode.fast_eval.transposition_label(
                pnode.potion_count,
                pnode.potion_strategic_cost,
                0,
                pnode.action_count,
            ),
        ) {
            continue;
        }
        expanded_nodes += 1;
        let pbytes = main_arena.get(pnode.state_handle);
        if unpack_state(pbytes, root.static_ctx.clone(), &mut scratch.state).is_err() {
            continue;
        }
        let mut tasks = Vec::new();
        enumerate_parent_tasks(
            0,
            item.node_id,
            &pnode,
            &mut scratch.state,
            content,
            policy,
            profile,
            &sensitivity,
            &mut tasks,
            &mut por_pruned,
        );
        scratch.candidates.clear();
        for task in &tasks {
            execute_action_task(
                &mut scratch,
                task,
                &main_arena,
                content,
                &root_baseline,
                policy,
                profile,
                cert,
                incumbent_table,
                &tt,
                stand_pat_cache,
                &sensitivity,
                None,
            );
        }

        let mut next_nodes = Vec::new();
        for cand in scratch.candidates.drain(..) {
            let state_bytes = scratch.slab.get(cand.state_slab_idx, cand.state_off, cand.state_len);
            let key_bytes = scratch.slab.get(cand.key_slab_idx, cand.key_off, cand.key_len);
            if unpack_state(state_bytes, root.static_ctx.clone(), &mut scratch.state).is_err() {
                continue;
            }
            let mut cfull = evaluate_full(&scratch.state, content, &cand.fast_eval, &root_baseline);
            cfull.beam_rank_score = compute_beam_rank_score(
                &cand.fast_eval,
                &cfull,
                &root_baseline,
                false,
                true,
                None,
            );
            let full_idx = arena.full_evals.len() as u32;
            arena.full_evals.push(cfull);
            let (choices_offset, choices_len) = arena.choices.push_choices(&cand.choices);
            let cid = arena.push_node(SearchNode {
                parent: Some(cand.parent_id),
                action: Some(cand.action),
                extra_end_turn: cand.extra_end_turn,
                choices_offset,
                choices_len,
                state_handle: main_arena.commit_slice(state_bytes),
                canonical_key_handle: main_arena.commit_slice(key_bytes),
                state_hash: cand.state_hash,
                turn: cand.turn,
                action_count: cand.action_count,
                potion_count: cand.potion_count,
                explicit_potion_count: cand.explicit_potion_count,
                automatic_potion_count: cand.automatic_potion_count,
                potion_strategic_cost: cand.potion_strategic_cost,
                explicit_potion_strategic_cost: cand.explicit_potion_strategic_cost,
                ambergris_count: cand.ambergris_count,
                forced_used_mask: cand.forced_used_mask,
                forced_strategic_hp_cost: cand.forced_strategic_hp_cost,
                forced_ambergris_count: cand.forced_ambergris_count,
                future_sold_hp: 0,
                retention_rank: 0,
                is_terminal: cand.is_terminal,
                boundary_rank: 0,
                fast_eval: cand.fast_eval,
                full_eval_idx: Some(full_idx),
            });
            if cand.is_terminal {
                completed.push(cid);
            } else if !cand.fast_eval.player_dead {
                next_nodes.push((
                    cid,
                    cand.turn > pnode.turn,
                    cfull.beam_rank_score,
                ));
            }
        }
        next_nodes.sort_by(|a, b| {
            a.1.cmp(&b.1)
                .then_with(|| b.2.total_cmp(&a.2))
        });
        if let Some(&(best_cid, _, _)) = next_nodes.first() {
            for &(cid, _, _) in next_nodes.iter().skip(1).rev() {
                if item.remaining_discrepancies > 0 {
                    pending.push(BossTempoPendingItem {
                        node_id: cid,
                        remaining_discrepancies: item.remaining_discrepancies - 1,
                    });
                }
            }
            pending.push(BossTempoPendingItem {
                node_id: best_cid,
                remaining_discrepancies: item.remaining_discrepancies,
            });
        }
    }

    let selected = select_final_plan(
        &arena,
        &completed,
        &root_baseline,
        policy.potion_policy,
        Some(policy.theft_policy),
        root_baseline.potion_replacement_hp_credit,
        root_baseline.minimum_required_potion_uses,
    );
    let (
        best_node,
        won,
        all_forced_uses_satisfied,
        strategic_hp_deficit,
        strategy_goal_hp_credit,
        strategy_goal_count,
        growth_rewards,
        relic_eval,
        recovered_hp,
        hp_lost,
        potion_count,
        score,
        actions,
    ) = match selected {
        Some(sel) => (
            Some(sel.node_id),
            sel.won,
            sel.all_forced_uses_satisfied,
            sel.strategic_hp_deficit,
            sel.strategy_goal_hp_credit,
            sel.strategy_goal_count,
            sel.growth_rewards,
            sel.relic_eval,
            sel.recovered_hp,
            sel.hp_lost,
            sel.potion_count,
            sel.score,
            arena.reconstruct_actions(sel.node_id),
        ),
        None => (
            None,
            false,
            root_baseline.all_forced_potion_mask == 0,
            i32::MAX,
            0,
            0,
            GrowthCounters::default(),
            RelicCounterEvaluation::default(),
            0,
            0,
            0,
            0,
            Vec::new(),
        ),
    };

    let total_committed = main_arena.total_bytes_committed;
    Ok(BeamRunOutput {
        best_node,
        arena,
        root_baseline,
        won,
        all_forced_uses_satisfied,
        strategic_hp_deficit,
        strategy_goal_hp_credit,
        strategy_goal_count,
        growth_rewards,
        relic_eval,
        recovered_hp,
        hp_lost,
        potion_count,
        score,
        actions,
        expanded_nodes,
        transition_count: scratch.transitions,
        tt_pruned: tempo_tt.branches_pruned as u64,
        incumbent_pruned: scratch.incumbent_pruned,
        por_pruned,
        packed_state_bytes_total: total_committed,
        termination,
        terminal_cards_hint: Vec::new(),
        early_turn_frontiers: Vec::new(),
        boss_tempo_prefixes: Vec::new(),
    })
}

fn has_reached_acceptable_battle_hp_loss_node(
    root: &RootEvalBaseline,
    policy: &SearchPolicy,
    node: &SearchNode,
) -> bool {
    let f = &node.fast_eval;
    let complete_victory = crate::eval::RouteQualityPolicy::is_complete_victory(
        node.action_count as usize,
        f.all_enemies_dead,
        f.player_dead,
        f.projected_player_hp,
    );
    let all_forced_ok =
        (node.forced_used_mask & root.all_forced_potion_mask) == root.all_forced_potion_mask;
    complete_victory
        && all_forced_ok
        && root.growth_target_satisfied(&f.growth_rewards)
        && root.relic_targets_satisfied(&f.relic_eval)
        && node.potion_count as usize == root.minimum_required_potion_uses
        && root.prior_battle_hp_lost + f.cumulative_hp_lost <= policy.acceptable_battle_hp_loss
}

fn has_reached_acceptable_battle_hp_loss(
    root: &RootEvalBaseline,
    policy: &SearchPolicy,
    out: &BeamRunOutput,
) -> bool {
    out.won
        && out.all_forced_uses_satisfied
        && root.growth_target_satisfied(&out.growth_rewards)
        && root.relic_targets_satisfied(&out.relic_eval)
        && out.potion_count == root.minimum_required_potion_uses
        && root.prior_battle_hp_lost + out.hp_lost <= policy.acceptable_battle_hp_loss
}

fn can_finish_target_portfolio(
    root: &RootEvalBaseline,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    out: &BeamRunOutput,
    cert: &ClosureCertificate,
) -> bool {
    profile.stop_portfolio_at_hp_target
        && cert.healing_certified
        && out.recovered_hp == 0
        && has_reached_acceptable_battle_hp_loss(root, policy, out)
}

fn is_better_run_output(cand: &BeamRunOutput, cur: &BeamRunOutput) -> bool {
    if cand.all_forced_uses_satisfied != cur.all_forced_uses_satisfied {
        return cand.all_forced_uses_satisfied;
    }
    if cand.won != cur.won {
        return cand.won;
    }
    if cand.strategic_hp_deficit != cur.strategic_hp_deficit {
        return cand.strategic_hp_deficit < cur.strategic_hp_deficit;
    }
    if cand.strategy_goal_hp_credit != cur.strategy_goal_hp_credit {
        return cand.strategy_goal_hp_credit > cur.strategy_goal_hp_credit;
    }
    if cand.strategy_goal_count != cur.strategy_goal_count {
        return cand.strategy_goal_count > cur.strategy_goal_count;
    }
    if cand.potion_count != cur.potion_count {
        return cand.potion_count < cur.potion_count;
    }
    cand.score > cur.score
}

#[allow(clippy::too_many_arguments)]
fn solve_beam_or_portfolio(
    root: &BattleState,
    content: &(dyn ContentDispatcher + Sync),
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    incumbent_table: &PrimaryIncumbentTable,
    stand_pat_cache: &StandPatCache,
    cancel: &AtomicBool,
    progress_cb: Option<&dyn Fn(&SearchProgress)>,
    total_expanded: &mut usize,
    total_transitions: &mut u64,
    total_tt_pruned: &mut u64,
    total_incumbent_pruned: &mut u64,
    total_por_pruned: &mut u64,
) -> Result<BeamRunOutput, KernelError> {
    if !policy.use_beam_width_portfolio {
        let out = run_beam_pass(
            root,
            content,
            policy,
            profile,
            cert,
            incumbent_table,
            stand_pat_cache,
            cancel,
            progress_cb,
            0,
            0,
            None,
        )?;
        *total_expanded += out.expanded_nodes;
        *total_transitions += out.transition_count;
        *total_tt_pruned += out.tt_pruned;
        *total_incumbent_pruned += out.incumbent_pruned;
        *total_por_pruned += out.por_pruned;
        return Ok(out);
    }

    let started = Instant::now();
    let has_power = PowerCardValuationRegistry::has_reachable_power(root);
    let use_reallocation = profile.reallocated_refinement_portfolio
        && policy.use_beam_width_portfolio
        && !policy.use_novelty_search
        && !policy.adaptive_novelty_refinement
        && policy
            .beam_width_portfolio_widths
            .as_ref()
            .is_none_or(|w| w.is_empty())
        && policy.beam_width_portfolio_plain_baseline_member
        && !policy.offensive_refinement_portfolio
        && !policy.bounded_offensive_refinement_portfolio;
    let specs = BeamWidthPortfolio::production_members(
        profile.beam_width,
        policy.beam_width_portfolio_widths.as_deref(),
        policy.beam_width_portfolio_plain_baseline_member && !use_reallocation,
        has_power,
        policy.offensive_refinement_portfolio,
        policy.bounded_offensive_refinement_portfolio || use_reallocation,
    );
    let baseline_info: parking_lot::Mutex<Option<BeamWidthPortfolioBaseline>> =
        parking_lot::Mutex::new(None);
    let best_incumbent_target_hit: parking_lot::Mutex<bool> = parking_lot::Mutex::new(false);
    let pass_error: parking_lot::Mutex<Option<KernelError>> = parking_lot::Mutex::new(None);

    let run_member_cb = |member_profile: &SolverSearchProfile, cb: Option<&dyn Fn(&SearchProgress)>| {
        let m_start = Instant::now();
        let res = run_beam_pass(
            root,
            content,
            policy,
            member_profile,
            cert,
            incumbent_table,
            stand_pat_cache,
            cancel,
            cb,
            0,
            0,
            None,
        );
        match res {
            Ok(out) => {
                {
                    let mut bi = baseline_info.lock();
                    if bi.is_none() {
                        *bi = Some(BeamWidthPortfolioBaseline {
                            frontier_exhausted: out.termination == "None"
                                || out.termination == "HpTarget",
                            proven_zero_damage: out.won
                                && out.hp_lost == 0
                                && out.potion_count == 0
                                && cert.healing_certified,
                            elapsed_ms: m_start.elapsed().as_millis() as u64,
                            expanded_nodes: out.expanded_nodes,
                            beam_width: member_profile.beam_width,
                        });
                    }
                }
                if can_finish_target_portfolio(&out.root_baseline, policy, profile, &out, cert) {
                    *best_incumbent_target_hit.lock() = true;
                }
                let expanded = out.expanded_nodes;
                let trans = out.transition_count;
                let term = out.termination;
                let won = out.won;
                let hp_lost = out.hp_lost;
                let potions = out.potion_count;
                BeamWidthPortfolioRun {
                    result: Some(out),
                    expanded_nodes: expanded,
                    transition_count: trans,
                    termination: term,
                    terminal: won,
                    won,
                    battle_hp_lost: Some(hp_lost),
                    potion_count: potions,
                    stop_portfolio: cancel.load(Ordering::Relaxed),
                    memory_truncated: false,
                }
            }
            Err(e) => {
                let mut pe = pass_error.lock();
                if pe.is_none() {
                    *pe = Some(e);
                }
                BeamWidthPortfolioRun {
                    result: None,
                    expanded_nodes: 0,
                    transition_count: 0,
                    termination: "Error",
                    terminal: false,
                    won: false,
                    battle_hp_lost: None,
                    potion_count: 0,
                    stop_portfolio: true,
                    memory_truncated: false,
                }
            }
        }
    };

    let reject_fn = |spec: &crate::portfolio::BeamWidthPortfolioMemberSpec| {
        let bl = *baseline_info.lock();
        let Some(bl) = bl else {
            return None;
        };
        if *best_incumbent_target_hit.lock() {
            return Some("AcceptableBattleHpLoss");
        }
        if spec.aggressive_power_commitment {
            return PowerCommitmentPortfolioGate::reject(has_power);
        }
        let remaining_ms = profile
            .soft_time_budget_ms
            .saturating_sub(started.elapsed().as_millis() as u64);
        if profile.soft_time_budget_ms == 0 {
            return None;
        }
        BeamWidthPortfolioGate::reject_refinement(
            &bl,
            spec.beam_width,
            profile.max_expanded_nodes.saturating_sub(bl.expanded_nodes),
            remaining_ms,
            profile.soft_time_budget_ms,
        )
    };

    let compare_fn = |cand: &Option<BeamRunOutput>, cur: &Option<BeamRunOutput>| match (cand, cur) {
        (Some(a), Some(b)) => is_better_run_output(a, b),
        (Some(_), None) => true,
        _ => false,
    };

    let outcome = if policy.portfolio_scheduling_mode == PortfolioSchedulingMode::Parallel
        && policy.max_degree_of_parallelism > 1
    {
        BeamWidthPortfolio::run_with_mode(
            &specs,
            profile.max_expanded_nodes,
            profile,
            policy.portfolio_scheduling_mode,
            policy.max_degree_of_parallelism,
            |member_profile| run_member_cb(member_profile, None),
            compare_fn,
            reject_fn,
        )
    } else {
        BeamWidthPortfolio::run(
            &specs,
            profile.max_expanded_nodes,
            profile,
            |member_profile| run_member_cb(member_profile, progress_cb),
            compare_fn,
            reject_fn,
        )
    };

    if let Some(err) = pass_error.into_inner() {
        return Err(err);
    }
    let out = outcome.selected.expect("portfolio produced no output");
    *total_expanded += outcome.total_expanded_nodes;
    *total_transitions += outcome.total_transition_count;
    *total_tt_pruned += out.tt_pruned;
    *total_incumbent_pruned += out.incumbent_pruned;
    *total_por_pruned += out.por_pruned;
    Ok(out)
}

pub struct CombatSearchCoordinator;

impl CombatSearchCoordinator {
    pub fn solve(
        root: &BattleState,
        policy: &SearchPolicy,
        content: &(dyn ContentDispatcher + Sync),
        cancel: &AtomicBool,
        progress_cb: Option<&dyn Fn(&SearchProgress)>,
    ) -> Result<InCombatSolveResponse, KernelError> {
        let started = Instant::now();
        let cert = ClosureCertificate::certify(root, content);
        let incumbent_table = PrimaryIncumbentTable::new();
        let stand_pat_cache = StandPatCache::new();

        let base_profile = SolverSearchProfile {
            beam_width: policy.beam_width.max(1),
            potion_free_beam_width: policy.potion_free_beam_width,
            potion_beam_width: policy.potion_beam_width,
            max_expanded_nodes: policy.max_nodes.max(1),
            max_card_branches_per_node: policy.max_card_branches_per_node.max(1),
            max_pile_choice_branches_per_action: policy.max_pile_choice_branches_per_action.max(1),
            max_hand_choice_branches_per_action: policy.max_hand_choice_branches_per_action.max(1),
            soft_time_budget_ms: policy.time_budget_ms,
            reallocated_refinement_portfolio: policy.reallocated_refinement_portfolio,
            stop_portfolio_at_hp_target: policy.stop_portfolio_at_hp_target,
            ..Default::default()
        };

        let mut total_expanded = 0usize;
        let mut total_transitions = 0u64;
        let mut total_tt_pruned = 0u64;
        let mut total_incumbent_pruned = 0u64;
        let mut total_por_pruned = 0u64;

        let act_ending_boss = root.static_ctx.room_type == RoomType::Boss;

        let mut selected = if policy.use_novelty_search && !policy.adaptive_novelty_refinement {
            if let Some(exp_profile) = policy
                .novelty_budget
                .exploration(&base_profile, act_ending_boss)
            {
                let exp_out = run_novelty_pass(
                    root,
                    content,
                    policy,
                    &exp_profile,
                    &cert,
                    &incumbent_table,
                    &stand_pat_cache,
                    cancel,
                    &policy.novelty_options,
                )?;
                total_expanded += exp_out.expanded_nodes;
                total_transitions += exp_out.transition_count;
                total_tt_pruned += exp_out.tt_pruned;
                total_incumbent_pruned += exp_out.incumbent_pruned;
                total_por_pruned += exp_out.por_pruned;

                let settled = exp_out.root_baseline.all_forced_potion_mask == 0
                    && has_reached_acceptable_battle_hp_loss(
                        &exp_out.root_baseline,
                        policy,
                        &exp_out,
                    );
                let elapsed_ms = started.elapsed().as_millis() as u64;
                let remaining = NoveltyPortfolioBudget::remaining(
                    &base_profile,
                    elapsed_ms,
                    exp_out.expanded_nodes,
                );
                if settled || remaining.is_none() || cancel.load(Ordering::Relaxed) {
                    exp_out
                } else {
                    let beam_out = solve_beam_or_portfolio(
                        root,
                        content,
                        policy,
                        &remaining.unwrap(),
                        &cert,
                        &incumbent_table,
                        &stand_pat_cache,
                        cancel,
                        progress_cb,
                        &mut total_expanded,
                        &mut total_transitions,
                        &mut total_tt_pruned,
                        &mut total_incumbent_pruned,
                        &mut total_por_pruned,
                    )?;
                    if is_better_run_output(&exp_out, &beam_out) {
                        exp_out
                    } else {
                        beam_out
                    }
                }
            } else {
                solve_beam_or_portfolio(
                    root,
                    content,
                    policy,
                    &base_profile,
                    &cert,
                    &incumbent_table,
                    &stand_pat_cache,
                    cancel,
                    progress_cb,
                    &mut total_expanded,
                    &mut total_transitions,
                    &mut total_tt_pruned,
                    &mut total_incumbent_pruned,
                    &mut total_por_pruned,
                )?
            }
        } else if policy.adaptive_novelty_refinement {
            let b_start = Instant::now();
            let baseline = solve_beam_or_portfolio(
                root,
                content,
                policy,
                &base_profile,
                &cert,
                &incumbent_table,
                &stand_pat_cache,
                cancel,
                progress_cb,
                &mut total_expanded,
                &mut total_transitions,
                &mut total_tt_pruned,
                &mut total_incumbent_pruned,
                &mut total_por_pruned,
            )?;
            let baseline_ms = b_start.elapsed().as_millis() as u64;
            let elapsed_ms = started.elapsed().as_millis() as u64;
            if cancel.load(Ordering::Relaxed)
                || can_finish_target_portfolio(
                    &baseline.root_baseline,
                    policy,
                    &base_profile,
                    &baseline,
                    &cert,
                )
            {
                baseline
            } else if let Some(ref_profile) = policy.novelty_budget.refinement_after_baseline(
                &base_profile,
                baseline_ms,
                baseline.expanded_nodes,
                elapsed_ms,
                act_ending_boss,
            ) {
                let nov_out = run_novelty_pass(
                    root,
                    content,
                    policy,
                    &ref_profile,
                    &cert,
                    &incumbent_table,
                    &stand_pat_cache,
                    cancel,
                    &policy.novelty_options,
                )?;
                total_expanded += nov_out.expanded_nodes;
                total_transitions += nov_out.transition_count;
                total_tt_pruned += nov_out.tt_pruned;
                total_incumbent_pruned += nov_out.incumbent_pruned;
                total_por_pruned += nov_out.por_pruned;
                if is_better_run_output(&nov_out, &baseline) {
                    nov_out
                } else {
                    baseline
                }
            } else {
                baseline
            }
        } else {
            solve_beam_or_portfolio(
                root,
                content,
                policy,
                &base_profile,
                &cert,
                &incumbent_table,
                &stand_pat_cache,
                cancel,
                progress_cb,
                &mut total_expanded,
                &mut total_transitions,
                &mut total_tt_pruned,
                &mut total_incumbent_pruned,
                &mut total_por_pruned,
            )?
        };

        // Optional Early-Turn Frontier Exploration
        let etc_budget_ms = if policy.early_turn_exploration_budget_ms > 0 {
            policy.early_turn_exploration_budget_ms
        } else {
            policy.time_budget_ms
        };
        let etc_remaining_ms = || {
            etc_budget_ms.saturating_sub(started.elapsed().as_millis() as u64)
        };
        if policy.early_turn_exploration_depth > 0
            && !cancel.load(Ordering::Relaxed)
            && !has_reached_acceptable_battle_hp_loss(&selected.root_baseline, policy, &selected)
            && etc_remaining_ms() > 0
        {
            let mut scout_profile = base_profile.clone();
            scout_profile.beam_width = (base_profile.beam_width * 4).clamp(64, 384);
            scout_profile.max_expanded_nodes = base_profile.max_expanded_nodes.min(150_000);
            scout_profile.soft_time_budget_ms = etc_remaining_ms().max(1);
            if let Ok(scout) = run_beam_pass(
                root,
                content,
                policy,
                &scout_profile,
                &cert,
                &incumbent_table,
                &stand_pat_cache,
                cancel,
                None,
                policy.early_turn_exploration_depth,
                0,
                None,
            ) {
                total_expanded += scout.expanded_nodes;
                total_transitions += scout.transition_count;
                if is_better_run_output(&scout, &selected) {
                    selected = scout;
                } else {
                    let ordered = interleave_potion_frontiers(scout.early_turn_frontiers);
                    let mut cont_budget = EARLY_TURN_EXPLORATION_NODE_BUDGET;
                    for (rank, cand) in ordered
                        .into_iter()
                        .take(EARLY_TURN_EXPLORATION_MAX_RANK_LIMIT)
                        .enumerate()
                    {
                        if rank >= EARLY_TURN_EXPLORATION_INITIAL_RANK_LIMIT && !selected.won {
                            break;
                        }
                        let rem_ms = etc_remaining_ms();
                        if cont_budget < 100 || rem_ms == 0 || cancel.load(Ordering::Relaxed) {
                            break;
                        }
                        let _ = select_early_turn_continuation_incumbent(
                            incumbent_table.try_get(PrimaryIncumbentBucket {
                                potions: 0,
                                forfeits_gold: false,
                            }),
                            false,
                            selected.potion_count > 0,
                            cand.actions
                                .iter()
                                .any(|a| matches!(a, PlannedAction::UsePotion { .. })),
                        );
                        let mut cont_profile = base_profile.clone();
                        cont_profile.max_expanded_nodes =
                            cont_budget.min(base_profile.max_expanded_nodes);
                        cont_profile.soft_time_budget_ms = rem_ms;
                        if let Ok(cont) = run_beam_pass(
                            root,
                            content,
                            policy,
                            &cont_profile,
                            &cert,
                            &incumbent_table,
                            &stand_pat_cache,
                            cancel,
                            None,
                            0,
                            0,
                            Some(&cand.actions),
                        ) {
                            cont_budget = cont_budget.saturating_sub(cont.expanded_nodes);
                            total_expanded += cont.expanded_nodes;
                            total_transitions += cont.transition_count;
                            if is_better_run_output(&cont, &selected) {
                                selected = cont;
                            }
                        }
                    }
                }
            }
        }

        // Optional Boss-Tempo Discrepancy Search on Boss Encounters
        if policy.use_boss_tempo_search
            && root.static_ctx.room_type == RoomType::Boss
            && !cancel.load(Ordering::Relaxed)
            && !can_finish_target_portfolio(
                &selected.root_baseline,
                policy,
                &base_profile,
                &selected,
                &cert,
            )
        {
            let tempo_budget = BossTempoSearchOptions::additional_budget(
                &base_profile,
                policy.time_budget_ms.max(1000),
            );
            if let Ok(scout) = run_beam_pass(
                root,
                content,
                policy,
                &tempo_budget,
                &cert,
                &incumbent_table,
                &stand_pat_cache,
                cancel,
                None,
                0,
                policy.boss_tempo_options.scout_turns.max(1),
                None,
            ) {
                total_expanded += scout.expanded_nodes;
                total_transitions += scout.transition_count;
                let prefixes = scout.boss_tempo_prefixes.clone();
                if is_better_run_output(&scout, &selected) {
                    selected = scout;
                }
                for prefix in prefixes.into_iter().take(2) {
                    if cancel.load(Ordering::Relaxed) {
                        break;
                    }
                    if let Ok(cont) = run_beam_pass(
                        root,
                        content,
                        policy,
                        &tempo_budget,
                        &cert,
                        &incumbent_table,
                        &stand_pat_cache,
                        cancel,
                        None,
                        0,
                        0,
                        Some(&prefix.actions),
                    ) {
                        total_expanded += cont.expanded_nodes;
                        total_transitions += cont.transition_count;
                        if is_better_run_output(&cont, &selected) {
                            selected = cont;
                        }
                    }
                }
            }
            if let Ok(tempo_out) = run_boss_tempo_depth_first(
                root,
                content,
                policy,
                &tempo_budget,
                &cert,
                &incumbent_table,
                &stand_pat_cache,
                cancel,
                &policy.boss_tempo_options,
            ) {
                total_expanded += tempo_out.expanded_nodes;
                total_transitions += tempo_out.transition_count;
                if is_better_run_output(&tempo_out, &selected) {
                    selected = tempo_out;
                }
            }
        }

        let packed_state_bytes_total = selected.packed_state_bytes_total;
        let nodes_in_arena = selected.arena.nodes.len() as u64;
        let avg_packed_bytes = if nodes_in_arena > 0 {
            packed_state_bytes_total / nodes_in_arena
        } else {
            0
        };

        let _ = selected.best_node;
        let _ = selected.root_baseline;

        Ok(InCombatSolveResponse {
            actions: selected.actions,
            score: selected.score,
            nodes_expanded: total_expanded,
            terminal_cards_hint: selected.terminal_cards_hint,
            stats: SearchStatistics {
                won: selected.won,
                strategic_hp_deficit: selected.strategic_hp_deficit,
                hp_lost: selected.hp_lost,
                potion_count: selected.potion_count,
                transition_count: total_transitions,
                tt_branches_pruned: total_tt_pruned,
                incumbent_branches_pruned: total_incumbent_pruned
                    + incumbent_table.branches_pruned.load(Ordering::Relaxed) as u64,
                por_branches_pruned: total_por_pruned,
                avg_packed_state_bytes: avg_packed_bytes,
                elapsed_ms: started.elapsed().as_millis() as u64,
                closure_healing_certified: cert.healing_certified,
                closure_growth_zero_certified: cert.growth_certified_zero,
                termination: selected.termination.to_owned(),
            },
        })
    }
}
