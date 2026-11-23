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
    allows_brightest_flame, compute_beam_rank_score_with_mode, evaluate_fast, evaluate_full,
    select_final_plan, ActEndingBossPolicy, EvalMode, FastEvalSummary, FullEvalDetails,
    PotionUsePolicy, PotionValuationRegistry, PowerCardValuationRegistry, RelicCounterEvaluation,
    RootEvalBaseline, SolverPotionPolicy,
};
use crate::hash::StateHash;
use crate::novelty::{
    capture_novelty_facts, BfwsBoundedOpen, BfwsEscapeBudget, BfwsFact, BfwsPackedNovelty,
    NoveltyOpenNode, NoveltyPortfolioBudget, NoveltySearchOptions,
};
use crate::packer::{empty_scratch_state, hash_canonical_key, pack_state, unpack_state};
use crate::por::{
    classify_card_for_por, dedup_and_filter_potions_for_pass, dedup_playable_hand_cards,
    enumerate_canonical_choices, has_any_playable_hand_card, has_any_usable_potion_for_pass,
    por_should_skip, CombatOrderSensitivity, PorActionClass,
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
use crate::rollout::{
    encode_choice_probe_rank_score, evaluate_choice_probe_packed, ChoiceProbeResult,
    ChoiceProbeWorkspace,
};
use crate::tt::{ShardedParetoTT, TranspositionLabel, TT_SHARD_COUNT};
use crate::{InCombatSolveResponse, PlannedAction, SearchPolicy, SearchProgress, SearchStatistics};
use smallvec::SmallVec;
use std::collections::BTreeMap;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};
use sts2_kernel::state::{
    BattleState, CardHandle, CreatureHandle, FieldValue, GrowthCounters, RootStaticContext,
    SemanticHistory,
};
use sts2_kernel::{CombatEngine, ContentDispatcher, KernelError, StepOutcome};
use sts2_types::{PileType, PlayerTurnPhase, PotionId, RoomType, TargetType};

const CHOICE_RESOLVED_CAPACITY: usize = 64;
const CHOICE_TT_SIZE: usize = 128;
const CHOICE_TT_MASK: usize = CHOICE_TT_SIZE - 1;

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
    parent_turn: i32,
    parent_action_count: u16,
    parent_potion_count: u16,
    parent_explicit_potion_count: u16,
    parent_automatic_potion_count: u16,
    parent_used_potion_slot_mask: u16,
    parent_potion_cost: i32,
    parent_explicit_potion_cost: i32,
    parent_ambergris_count: u16,
    parent_forced_used_mask: u64,
    parent_forced_strategic_hp_cost: i32,
    parent_forced_ambergris_count: u16,
    parent_has_choice_lookahead: bool,
    parent_choice_lookahead_score: Option<f64>,
    action_ord: u32,
    spec: ActionSpec,
}

#[derive(Clone, Copy, Debug)]
struct PassSearchOptions {
    allow_paid_optional_potions: bool,
    prefer_potion_buckets: bool,
    eval_mode: EvalMode,
    early_turn_scout_depth: i32,
    boss_tempo_scout_turns: i32,
    rollout_horizon_turns: usize,
}

impl Default for PassSearchOptions {
    fn default() -> Self {
        Self {
            allow_paid_optional_potions: true,
            prefer_potion_buckets: false,
            eval_mode: EvalMode::Balanced,
            early_turn_scout_depth: 0,
            boss_tempo_scout_turns: 0,
            rollout_horizon_turns: 0,
        }
    }
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
    state_hash: StateHash,
    turn: i32,
    turn_completed: bool,
    action_count: u16,
    potion_count: u16,
    explicit_potion_count: u16,
    automatic_potion_count: u16,
    used_potion_slot_mask: u16,
    potion_strategic_cost: i32,
    explicit_potion_strategic_cost: i32,
    ambergris_count: u16,
    forced_used_mask: u64,
    forced_strategic_hp_cost: i32,
    forced_ambergris_count: u16,
    is_terminal: bool,
    fast_eval: FastEvalSummary,
    full_eval: Option<FullEvalDetails>,
    label: TranspositionLabel,
}

struct ResolvedChoiceCandidate {
    choices: Vec<Vec<usize>>,
    action: CompactAction,
    extra_end_turn: Option<CompactAction>,
    state_slab_idx: u32,
    state_off: u32,
    state_len: u32,
    state_hash: StateHash,
    turn: i32,
    turn_completed: bool,
    action_count: u16,
    potion_count: u16,
    explicit_potion_count: u16,
    automatic_potion_count: u16,
    used_potion_slot_mask: u16,
    potion_strategic_cost: i32,
    explicit_potion_strategic_cost: i32,
    ambergris_count: u16,
    forced_used_mask: u64,
    forced_strategic_hp_cost: i32,
    forced_ambergris_count: u16,
    is_terminal: bool,
    fast_eval: FastEvalSummary,
    full_eval: Option<FullEvalDetails>,
    label: TranspositionLabel,
    lookahead_score: f64,
    lookahead_hp_lost: i32,
    lookahead_board_investment: i32,
    has_lookahead: bool,
    lookahead_victory: bool,
}

struct WorkerScratch {
    state: BattleState,
    pre_end_turn_state: BattleState,
    choice_paused_states: Vec<BattleState>,
    choice_probe_ws: ChoiceProbeWorkspace,
    choice_resolved: Vec<ResolvedChoiceCandidate>,
    choice_staged: Vec<Option<ResolvedChoiceCandidate>>,
    choice_tt_hashes: [StateHash; CHOICE_TT_SIZE],
    choice_tt_epochs: [u32; CHOICE_TT_SIZE],
    choice_tt_epoch: u32,
    pack_buf: Vec<u8>,
    action_buf: Vec<ActionTask>,
    slab: LocalStateSlab,
    candidates: Vec<WorkerCandidate>,
    transitions: u64,
    incumbent_pruned: u64,
    tt_probe_pruned: u64,
    por_pruned: u64,
}

impl WorkerScratch {
    fn new(static_ctx: Arc<RootStaticContext>) -> Self {
        let choice_paused_states = (0..4)
            .map(|_| empty_scratch_state(static_ctx.clone()))
            .collect();
        Self {
            pre_end_turn_state: empty_scratch_state(static_ctx.clone()),
            choice_paused_states,
            choice_probe_ws: ChoiceProbeWorkspace::new(static_ctx.clone()),
            choice_resolved: Vec::with_capacity(CHOICE_RESOLVED_CAPACITY),
            choice_staged: Vec::with_capacity(CHOICE_RESOLVED_CAPACITY),
            choice_tt_hashes: [StateHash::default(); CHOICE_TT_SIZE],
            choice_tt_epochs: [0u32; CHOICE_TT_SIZE],
            choice_tt_epoch: 0,
            state: empty_scratch_state(static_ctx),
            pack_buf: Vec::with_capacity(4096),
            action_buf: Vec::with_capacity(16),
            slab: LocalStateSlab::default(),
            candidates: Vec::with_capacity(64),
            transitions: 0,
            incumbent_pruned: 0,
            tt_probe_pruned: 0,
            por_pruned: 0,
        }
    }

    fn reset_for_wave(&mut self) {
        self.slab.clear();
        self.candidates.clear();
        self.choice_resolved.clear();
        self.choice_staged.clear();
        self.transitions = 0;
        self.incumbent_pruned = 0;
        self.tt_probe_pruned = 0;
        self.por_pruned = 0;
    }

    #[inline]
    fn begin_choice_tree(&mut self) {
        self.choice_resolved.clear();
        self.choice_staged.clear();
        self.choice_tt_epoch = self.choice_tt_epoch.wrapping_add(1);
        if self.choice_tt_epoch == 0 {
            self.choice_tt_epochs.fill(0);
            self.choice_tt_epoch = 1;
        }
    }

    #[inline]
    fn try_insert_local_choice_tt(&mut self, hash: StateHash) -> bool {
        let idx = (hash.0 as usize) & CHOICE_TT_MASK;
        if self.choice_tt_epochs[idx] == self.choice_tt_epoch && self.choice_tt_hashes[idx] == hash {
            return false;
        }
        self.choice_tt_epochs[idx] = self.choice_tt_epoch;
        self.choice_tt_hashes[idx] = hash;
        true
    }
}

struct PassResources {
    scratches: Vec<WorkerScratch>,
    parent_states: Vec<BattleState>,
    rollout_workspaces: Vec<crate::rollout::RolloutWorkspace>,
    step_arena: MainStateArena,
    next_step_arena: MainStateArena,
    turn_staging_arena: MainStateArena,
    tt: ShardedParetoTT,
    sort_list: Vec<(usize, usize, (u32, u32, u32))>,
    shard_buckets: Vec<Vec<(usize, StateHash, TranspositionLabel, (usize, usize))>>,
    shard_accepted: Vec<Vec<(usize, (usize, usize))>>,
    accepted_buf: Vec<(usize, (usize, usize))>,
}

impl PassResources {
    fn new(static_ctx: &Arc<RootStaticContext>, worker_count: usize) -> Self {
        Self {
            scratches: (0..worker_count)
                .map(|_| WorkerScratch::new(static_ctx.clone()))
                .collect(),
            parent_states: (0..worker_count)
                .map(|_| empty_scratch_state(static_ctx.clone()))
                .collect(),
            rollout_workspaces: (0..worker_count)
                .map(|_| crate::rollout::RolloutWorkspace::new(static_ctx.clone()))
                .collect(),
            step_arena: MainStateArena::default(),
            next_step_arena: MainStateArena::default(),
            turn_staging_arena: MainStateArena::default(),
            tt: ShardedParetoTT::new(65_536),
            sort_list: Vec::with_capacity(2048),
            shard_buckets: (0..TT_SHARD_COUNT).map(|_| Vec::new()).collect(),
            shard_accepted: (0..TT_SHARD_COUNT).map(|_| Vec::new()).collect(),
            accepted_buf: Vec::with_capacity(2048),
        }
    }

    fn reset_for_pass(&mut self) {
        self.step_arena.clear();
        self.next_step_arena.clear();
        self.turn_staging_arena.clear();
        self.tt.reset_generation();
        self.tt.branches_pruned = 0;
        self.sort_list.clear();
        for b in &mut self.shard_buckets {
            b.clear();
        }
        for a in &mut self.shard_accepted {
            a.clear();
        }
        self.accepted_buf.clear();
    }
}

#[inline]
fn clone_opt_string_into(src: &Option<String>, dst: &mut Option<String>) {
    match (dst, src) {
        (Some(ds), Some(ss)) => {
            if ds != ss {
                ds.clone_from(ss);
            }
        }
        (dst_slot, Some(ss)) => *dst_slot = Some(ss.clone()),
        (dst_slot, None) => {
            if dst_slot.is_some() {
                *dst_slot = None;
            }
        }
    }
}

#[inline]
fn clone_small_strings_into(src: &SmallVec<[String; 8]>, dst: &mut SmallVec<[String; 8]>) {
    let keep = dst.len().min(src.len());
    for i in 0..keep {
        if dst[i] != src[i] {
            dst[i].clone_from(&src[i]);
        }
    }
    if src.len() > keep {
        for s in &src[keep..] {
            dst.push(s.clone());
        }
    } else if dst.len() > keep {
        dst.truncate(keep);
    }
}

#[inline]
fn clone_field_map_into(src: &BTreeMap<String, FieldValue>, dst: &mut BTreeMap<String, FieldValue>) {
    if src.is_empty() {
        if !dst.is_empty() {
            dst.clear();
        }
        return;
    }
    if dst.len() == src.len() && dst.keys().zip(src.keys()).all(|(dk, sk)| dk == sk) {
        for (dv, sv) in dst.values_mut().zip(src.values()) {
            if dv != sv {
                dv.clone_from(sv);
            }
        }
    } else {
        dst.clone_from(src);
    }
}

#[inline]
fn clone_semantic_history_into(src: &SemanticHistory, dst: &mut SemanticHistory) {
    dst.exhausts.clone_from(&src.exhausts);
    dst.card_block_gains.clone_from(&src.card_block_gains);
    dst.orb_channels.clone_from(&src.orb_channels);
    dst.energy_spent.clone_from(&src.energy_spent);
    dst.card_drawn.clone_from(&src.card_drawn);

    let keep = dst.actor_events.len().min(src.actor_events.len());
    for i in 0..keep {
        if dst.actor_events[i].0 != src.actor_events[i].0 {
            dst.actor_events[i].0.clone_from(&src.actor_events[i].0);
        }
        if dst.actor_events[i].1 != src.actor_events[i].1 {
            dst.actor_events[i].1.clone_from(&src.actor_events[i].1);
        }
        dst.actor_events[i].2 = src.actor_events[i].2;
    }
    if src.actor_events.len() > keep {
        dst.actor_events.reserve(src.actor_events.len() - keep);
        for item in &src.actor_events[keep..] {
            dst.actor_events.push(item.clone());
        }
    } else if dst.actor_events.len() > keep {
        dst.actor_events.truncate(keep);
    }

    dst.growth_rewards = src.growth_rewards;
    dst.brightest_flame_max_hp_spent = src.brightest_flame_max_hp_spent;
}

#[inline]
pub(crate) fn clone_battle_state_into(src: &BattleState, dst: &mut BattleState) {
    dst.static_ctx = Arc::clone(&src.static_ctx);
    dst.current_side = src.current_side;
    dst.round_number = src.round_number;
    dst.is_in_progress = src.is_in_progress;
    dst.is_starting = src.is_starting;
    dst.won = src.won;
    dst.is_player_extra_turn = src.is_player_extra_turn;
    dst.gold_was_stolen = src.gold_was_stolen;
    dst.end_player_turn_requested = src.end_player_turn_requested;
    dst.card_action_boundary_depth = src.card_action_boundary_depth;
    dst.next_creature_id = src.next_creature_id;
    dst.initial_encounter_monster_count = src.initial_encounter_monster_count;
    dst.next_card_handle = src.next_card_handle;
    dst.next_creature_handle = src.next_creature_handle;
    dst.next_power_handle = src.next_power_handle;
    dst.next_attack_id = src.next_attack_id;
    dst.run_rngs.seed = src.run_rngs.seed;
    dst.run_rngs.streams = src.run_rngs.streams;
    if dst.run_rngs.string_seed != src.run_rngs.string_seed {
        dst.run_rngs.string_seed.clone_from(&src.run_rngs.string_seed);
    }
    dst.player_rngs = src.player_rngs;

    // Copy PlayerInst with in-place map/vec reuse
    let sp = &src.player;
    let dp = &mut dst.player;
    dp.creature_handle = sp.creature_handle;
    dp.current_hp = sp.current_hp;
    dp.max_hp = sp.max_hp;
    dp.block = sp.block;
    dp.cumulative_hp_lost = sp.cumulative_hp_lost;
    dp.gold = sp.gold;
    dp.max_energy = sp.max_energy;
    dp.base_orb_slot_count = sp.base_orb_slot_count;
    dp.is_active_for_hooks = sp.is_active_for_hooks;
    dp.can_use_or_remove_potions = sp.can_use_or_remove_potions;
    dp.energy = sp.energy;
    dp.stars = sp.stars;
    dp.phase = sp.phase;
    dp.turn_number = sp.turn_number;
    dp.counters = sp.counters;
    dp.hand.clone_from(&sp.hand);
    dp.draw.clone_from(&sp.draw);
    dp.discard.clone_from(&sp.discard);
    dp.exhaust.clone_from(&sp.exhaust);
    dp.play.clone_from(&sp.play);
    dp.deck.clone_from(&sp.deck);
    dp.potion_slots.clone_from(&sp.potion_slots);
    dp.orb_capacity = sp.orb_capacity;
    dp.orbs.clone_from(&sp.orbs);
    dp.arrows.clone_from(&sp.arrows);
    dp.card_or_potion_effect_depth = sp.card_or_potion_effect_depth;
    if dp.relics.len() == sp.relics.len() {
        for (dr, sr) in dp.relics.iter_mut().zip(sp.relics.iter()) {
            dr.id = sr.id;
            dr.is_wax = sr.is_wax;
            dr.is_melted = sr.is_melted;
            dr.stack_count = sr.stack_count;
            dr.floor_added_to_deck = sr.floor_added_to_deck;
            clone_field_map_into(&sr.private_fields, &mut dr.private_fields);
        }
    } else {
        dp.relics.clone_from(&sp.relics);
    }

    dst.cards.clear();
    dst.cards.extend_from_slice(&src.cards);

    if src.card_extensions.is_empty() {
        dst.card_extensions.clear();
    } else if dst.card_extensions.len() == src.card_extensions.len() {
        for (de, se) in dst.card_extensions.iter_mut().zip(src.card_extensions.iter()) {
            de.local_energy_modifiers.clone_from(&se.local_energy_modifiers);
            match (&mut de.enchantment, &se.enchantment) {
                (Some(de_e), Some(se_e)) => {
                    de_e.id = se_e.id;
                    de_e.amount = se_e.amount;
                    de_e.status = se_e.status;
                    clone_field_map_into(&se_e.private_fields, &mut de_e.private_fields);
                }
                _ => de.enchantment.clone_from(&se.enchantment),
            }
            clone_field_map_into(&se.private_fields, &mut de.private_fields);
        }
    } else {
        dst.card_extensions.clone_from(&src.card_extensions);
    }

    if dst.creatures.len() == src.creatures.len() {
        for (dc, sc) in dst.creatures.iter_mut().zip(src.creatures.iter()) {
            dc.handle = sc.handle;
            dc.combat_id = sc.combat_id;
            dc.side = sc.side;
            clone_opt_string_into(&sc.slot_name, &mut dc.slot_name);
            dc.is_player = sc.is_player;
            dc.pet_owner_player_index = sc.pet_owner_player_index;
            dc.monster_id = sc.monster_id;
            dc.current_hp = sc.current_hp;
            dc.max_hp = sc.max_hp;
            dc.block = sc.block;
            dc.cumulative_hp_lost = sc.cumulative_hp_lost;
            dc.is_detached = sc.is_detached;
            dc.powers.clone_from(&sc.powers);
            match (&mut dc.monster, &sc.monster) {
                (Some(dm), Some(sm)) => {
                    dm.monster_id = sm.monster_id;
                    dm.rng = sm.rng;
                    dm.is_performing_move = sm.is_performing_move;
                    dm.spawned_this_turn = sm.spawned_this_turn;
                    if dm.current_state_id != sm.current_state_id {
                        dm.current_state_id.clone_from(&sm.current_state_id);
                    }
                    clone_opt_string_into(&sm.next_move_id, &mut dm.next_move_id);
                    dm.next_move_can_transition_away = sm.next_move_can_transition_away;
                    dm.performed_first_move = sm.performed_first_move;
                    clone_small_strings_into(&sm.state_log_ids, &mut dm.state_log_ids);
                    clone_opt_string_into(
                        &sm.transient_move_follow_up_state_id,
                        &mut dm.transient_move_follow_up_state_id,
                    );
                    clone_field_map_into(&sm.setup_fields, &mut dm.setup_fields);
                    clone_field_map_into(&sm.private_fields, &mut dm.private_fields);
                }
                _ => dc.monster.clone_from(&sc.monster),
            }
        }
    } else {
        dst.creatures.clone_from(&src.creatures);
    }

    if src.power_extensions.is_empty() {
        dst.power_extensions.clear();
    } else if dst.power_extensions.len() == src.power_extensions.len() {
        for (dp, sp) in dst.power_extensions.iter_mut().zip(src.power_extensions.iter()) {
            dp.attack_command_id = sp.attack_command_id;
            dp.amount_when_attack_started = sp.amount_when_attack_started;
            dp.impaled_arrows.clone_from(&sp.impaled_arrows);
            dp.hit_counter = sp.hit_counter;
            clone_field_map_into(&sp.private_fields, &mut dp.private_fields);
        }
    } else {
        dst.power_extensions.clone_from(&src.power_extensions);
    }

    dst.ally_handles.clone_from(&src.ally_handles);
    dst.enemy_handles.clone_from(&src.enemy_handles);
    dst.escaped_handles.clone_from(&src.escaped_handles);
    dst.removed_handles.clone_from(&src.removed_handles);
    dst.spawned_enemy_handles.clone_from(&src.spawned_enemy_handles);
    dst.pet_handles.clone_from(&src.pet_handles);
    dst.damage_history.clone_from(&src.damage_history);
    clone_semantic_history_into(&src.semantic_history, &mut dst.semantic_history);
    dst.pending.clone_from(&src.pending);
    dst.vakuu_auto_select_depth = src.vakuu_auto_select_depth;
}

struct BeamRunOutput {
    best_node: Option<NodeId>,
    best_turn: i32,
    arena: NodeArena,
    root_baseline: RootEvalBaseline,
    won: bool,
    survives: bool,
    death_save_use_count: i32,
    player_hp: i32,
    all_forced_uses_satisfied: bool,
    strategic_hp_deficit: i32,
    strategy_goal_hp_credit: i32,
    strategy_goal_count: i32,
    growth_rewards: GrowthCounters,
    relic_eval: RelicCounterEvaluation,
    recovered_hp: i32,
    hp_lost: i32,
    potion_count: usize,
    explicit_potion_count: usize,
    optional_potion_count: usize,
    optional_potion_strategic_cost: i32,
    optional_ambergris_count: i32,
    has_completed_turn_ongoing: bool,
    completed_ongoing_turn: i32,
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
        TargetType::AnyPlayer => {
            if state.player_creature().is_some_and(|c| c.is_alive()) {
                out.push(Some(state.player.creature_handle));
            }
        }
        _ => out.push(None),
    }
    out
}

#[allow(clippy::too_many_arguments)]
fn enumerate_parent_tasks(
    parent_rank: u32,
    parent_id: NodeId,
    node: &SearchNode,
    parent_has_choice_lookahead: bool,
    parent_choice_lookahead_score: Option<f64>,
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    sensitivity: &CombatOrderSensitivity,
    allow_paid_optional_potions: bool,
    renewable_potion_shaped_rock: bool,
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
        let hand_cards = dedup_playable_hand_cards(
            state,
            content,
            policy.accelerations.enable_hand_potion_dedup,
        );
        let max_branches = profile.max_card_branches_per_node;
        let mut play_candidates: SmallVec<[(usize, CardHandle, Option<CreatureHandle>, PorActionClass); 64]> =
            SmallVec::new();
        let mut max_target_rank = 0usize;

        for ch in hand_cards {
            let Ok(meta) = state.card(ch).ok_or(()).and_then(|c| content.card_meta(c.id).map_err(|_| ())) else {
                continue;
            };
            let eff_target = content
                .card_resolved_target_type(state, ch)
                .unwrap_or(meta.target_type);
            let targets = target_candidates_for(state, eff_target);
            let mut rank_for_card = 0usize;
            for target_handle in targets {
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
                max_target_rank = max_target_rank.max(rank_for_card);
                play_candidates.push((rank_for_card, ch, target_handle, por_class));
                rank_for_card += 1;
            }
        }

        if play_candidates.len() <= max_branches {
            for &(_, card_handle, target_handle, por_class) in &play_candidates {
                tasks_out.push(ActionTask {
                    parent_rank,
                    parent_id,
                    parent_turn: node.turn,
                    parent_action_count: node.action_count,
                    parent_potion_count: node.potion_count,
                    parent_explicit_potion_count: node.explicit_potion_count,
                    parent_automatic_potion_count: node.automatic_potion_count,
                    parent_used_potion_slot_mask: node.used_potion_slot_mask,
                    parent_potion_cost: node.potion_strategic_cost,
                    parent_explicit_potion_cost: node.explicit_potion_strategic_cost,
                    parent_ambergris_count: node.ambergris_count,
                    parent_forced_used_mask: node.forced_used_mask,
                    parent_forced_strategic_hp_cost: node.forced_strategic_hp_cost,
                    parent_forced_ambergris_count: node.forced_ambergris_count,
                    parent_has_choice_lookahead,
                    parent_choice_lookahead_score,
                    action_ord,
                    spec: ActionSpec::PlayCard {
                        card_handle,
                        target_handle,
                        por_class,
                    },
                });
                action_ord += 1;
            }
        } else {
            // Distinct-card-first capping across target ranks (exact parity with BuildDistinctCardFirstMask).
            let mut keep_mask: SmallVec<[bool; 64]> =
                smallvec::smallvec![false; play_candidates.len()];
            let mut remaining = max_branches;
            for target_rank in 0..=max_target_rank {
                if remaining == 0 {
                    break;
                }
                for (i, &(rank, _, _, _)) in play_candidates.iter().enumerate() {
                    if remaining == 0 {
                        break;
                    }
                    if rank == target_rank {
                        keep_mask[i] = true;
                        remaining -= 1;
                    }
                }
            }
            for (i, &(_, card_handle, target_handle, por_class)) in
                play_candidates.iter().enumerate()
            {
                if !keep_mask[i] {
                    continue;
                }
                tasks_out.push(ActionTask {
                    parent_rank,
                    parent_id,
                    parent_turn: node.turn,
                    parent_action_count: node.action_count,
                    parent_potion_count: node.potion_count,
                    parent_explicit_potion_count: node.explicit_potion_count,
                    parent_automatic_potion_count: node.automatic_potion_count,
                    parent_used_potion_slot_mask: node.used_potion_slot_mask,
                    parent_potion_cost: node.potion_strategic_cost,
                    parent_explicit_potion_cost: node.explicit_potion_strategic_cost,
                    parent_ambergris_count: node.ambergris_count,
                    parent_forced_used_mask: node.forced_used_mask,
                    parent_forced_strategic_hp_cost: node.forced_strategic_hp_cost,
                    parent_forced_ambergris_count: node.forced_ambergris_count,
                    parent_has_choice_lookahead,
                    parent_choice_lookahead_score,
                    action_ord,
                    spec: ActionSpec::PlayCard {
                        card_handle,
                        target_handle,
                        por_class,
                    },
                });
                action_ord += 1;
            }
        }

        if policy.allows_any_potion() {
            let potions = dedup_and_filter_potions_for_pass(
                state,
                sensitivity,
                policy,
                policy.accelerations.enable_hand_potion_dedup,
                allow_paid_optional_potions,
                renewable_potion_shaped_rock,
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
                        parent_turn: node.turn,
                        parent_action_count: node.action_count,
                        parent_potion_count: node.potion_count,
                        parent_explicit_potion_count: node.explicit_potion_count,
                        parent_automatic_potion_count: node.automatic_potion_count,
                        parent_used_potion_slot_mask: node.used_potion_slot_mask,
                        parent_potion_cost: node.potion_strategic_cost,
                        parent_explicit_potion_cost: node.explicit_potion_strategic_cost,
                        parent_ambergris_count: node.ambergris_count,
                        parent_forced_used_mask: node.forced_used_mask,
                        parent_forced_strategic_hp_cost: node.forced_strategic_hp_cost,
                        parent_forced_ambergris_count: node.forced_ambergris_count,
                        parent_has_choice_lookahead,
                        parent_choice_lookahead_score,
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
        parent_turn: node.turn,
        parent_action_count: node.action_count,
        parent_potion_count: node.potion_count,
        parent_explicit_potion_count: node.explicit_potion_count,
        parent_automatic_potion_count: node.automatic_potion_count,
        parent_used_potion_slot_mask: node.used_potion_slot_mask,
        parent_potion_cost: node.potion_strategic_cost,
        parent_explicit_potion_cost: node.explicit_potion_strategic_cost,
        parent_ambergris_count: node.ambergris_count,
        parent_forced_used_mask: node.forced_used_mask,
        parent_forced_strategic_hp_cost: node.forced_strategic_hp_cost,
        parent_forced_ambergris_count: node.forced_ambergris_count,
        parent_has_choice_lookahead: false,
        parent_choice_lookahead_score: None,
        action_ord,
        spec: ActionSpec::EndTurn,
    });
}

#[allow(clippy::too_many_arguments)]
fn prepare_staged_candidate(
    scratch: &mut WorkerScratch,
    task: &ActionTask,
    extra_end_turn: Option<CompactAction>,
    check_local_choice_tt: bool,
    inherit_lookahead: bool,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    incumbent_table: &PrimaryIncumbentTable,
    tt: &ShardedParetoTT,
    sensitivity: &CombatOrderSensitivity,
    allow_paid_optional_potions: bool,
    eval_mode: EvalMode,
) -> Option<(
    Option<CompactAction>,
    u32,
    u32,
    u32,
    StateHash,
    i32,
    bool,
    u16,
    u16,
    u16,
    u16,
    i32,
    i32,
    u16,
    u64,
    i32,
    u16,
    bool,
    FastEvalSummary,
    Option<FullEvalDetails>,
    TranspositionLabel,
)> {
    if !allows_brightest_flame(
        policy.brightest_flame_max_hp_loss_limit,
        root_baseline.initial_brightest_flame_max_hp_spent,
        root_baseline.brightest_flame_spent(&scratch.state),
    ) {
        return None;
    }
    // Forced-End-Turn In-Place Compression: if PlayCard/UsePotion left no legal actions in Play phase,
    // compress EndTurn directly in-place inside the worker (restoring state if EndTurn triggers a Choice).
    let mut final_extra_end = extra_end_turn;
    if final_extra_end.is_none()
        && policy.accelerations.enable_forced_end_turn_compression
        && !matches!(task.spec, ActionSpec::EndTurn)
        && scratch.state.is_in_progress
        && !scratch.state.won
        && scratch.state.pending.is_none()
        && scratch.state.player.phase == PlayerTurnPhase::Play
    {
        let has_card = has_any_playable_hand_card(&mut scratch.state, content);
        let has_potion = !has_card
            && policy.allows_any_potion()
            && has_any_usable_potion_for_pass(
                &scratch.state,
                sensitivity,
                policy,
                allow_paid_optional_potions,
                root_baseline.renewable_potion_shaped_rock,
            );
        if !has_card && !has_potion {
            let end_turn_num = scratch.state.player.turn_number;
            clone_battle_state_into(&scratch.state, &mut scratch.pre_end_turn_state);
            match CombatEngine::end_player_turn(&mut scratch.state, content) {
                Ok(StepOutcome::Completed) | Ok(StepOutcome::CombatEnded { .. }) => {
                    if !allows_brightest_flame(
                        policy.brightest_flame_max_hp_loss_limit,
                        root_baseline.initial_brightest_flame_max_hp_spent,
                        root_baseline.brightest_flame_spent(&scratch.state),
                    ) {
                        return None;
                    }
                    final_extra_end = Some(CompactAction {
                        kind: CompactActionKind::EndTurn,
                        turn: end_turn_num,
                        por_class: PorActionClass::Barrier,
                    });
                }
                Ok(StepOutcome::NeedsChoice(_)) | Err(_) => {
                    std::mem::swap(&mut scratch.state, &mut scratch.pre_end_turn_state);
                }
            }
        }
    }

    let fast_eval = evaluate_fast(&scratch.state, content, root_baseline, None);

    let (
        potion_delta,
        explicit_delta,
        cost_delta,
        ambergris_delta,
        slot_mask_bit,
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
            let slot_bit = if potion_slot < 16 {
                1u16 << potion_slot
            } else {
                0
            };
            if let Some(bit_idx) = policy.forced_bit_for(potion_slot, potion_id) {
                let bit = if bit_idx < 64 { 1u64 << bit_idx } else { 0 };
                let newly_forced = (task.parent_forced_used_mask & bit) == 0;
                (
                    1u16,
                    1u16,
                    cost,
                    amb,
                    slot_bit,
                    bit,
                    if newly_forced { cost } else { 0 },
                    if newly_forced { amb } else { 0 },
                )
            } else {
                (1u16, 1u16, cost, amb, slot_bit, 0u64, 0i32, 0u16)
            }
        }
        _ => (0u16, 0u16, 0i32, 0u16, 0u16, 0u64, 0i32, 0u16),
    };

    let potion_count = task.parent_potion_count + potion_delta;
    let explicit_potion_count = task.parent_explicit_potion_count + explicit_delta;
    let _ = slot_mask_bit;
    let potion_strategic_cost = task.parent_potion_cost + cost_delta;
    let explicit_potion_strategic_cost = task.parent_explicit_potion_cost + cost_delta;
    let ambergris_count = task.parent_ambergris_count + ambergris_delta;
    let forced_used_mask = task.parent_forced_used_mask | forced_mask_bit;
    let forced_strategic_hp_cost = task.parent_forced_strategic_hp_cost + forced_cost_delta;
    let forced_ambergris_count = task.parent_forced_ambergris_count + forced_amb_delta;
    let action_count = task.parent_action_count + 1 + u16::from(final_extra_end.is_some());
    let turn = scratch.state.player.turn_number;
    let turn_completed = matches!(task.spec, ActionSpec::EndTurn)
        || final_extra_end.is_some()
        || turn > task.parent_turn;
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
        return None;
    }

    let state_hash = hash_canonical_key(&scratch.state);
    if check_local_choice_tt && !scratch.try_insert_local_choice_tt(state_hash) {
        return None;
    }
    let label = fast_eval.transposition_label(
        potion_count,
        potion_strategic_cost,
        0,
        action_count,
        forced_used_mask,
    );

    // Lock-free read-only TT probe against already committed waves before full eval or state packing.
    if !is_terminal && tt.probe_dominated(state_hash, &label) {
        scratch.tt_probe_pruned += 1;
        return None;
    }

    let full_eval = if !is_terminal {
        let mut full = evaluate_full(&scratch.state, content, &fast_eval, root_baseline);
        full.beam_rank_score = compute_beam_rank_score_with_mode(
            &fast_eval,
            &full,
            root_baseline,
            profile.base_score_only,
            profile.boss_tempo_hp_pricing,
            profile.beam_weight_perturbation,
            eval_mode,
            potion_strategic_cost,
            action_count,
        );
        if inherit_lookahead && !turn_completed && task.parent_has_choice_lookahead {
            full.has_choice_lookahead = true;
            full.rollout_score = task.parent_choice_lookahead_score;
        }
        Some(full)
    } else {
        None
    };

    let (state_slab_idx, state_off, state_len) = if is_terminal {
        (0, 0, 0)
    } else {
        pack_state(&scratch.state, &mut scratch.pack_buf);
        scratch.slab.alloc_slice(&scratch.pack_buf)
    };

    Some((
        final_extra_end,
        state_slab_idx,
        state_off,
        state_len,
        state_hash,
        turn,
        turn_completed,
        action_count,
        potion_count,
        explicit_potion_count,
        task.parent_automatic_potion_count,
        potion_strategic_cost,
        explicit_potion_strategic_cost,
        ambergris_count,
        forced_used_mask,
        forced_strategic_hp_cost,
        forced_ambergris_count,
        is_terminal,
        fast_eval,
        full_eval,
        label,
    ))
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
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    incumbent_table: &PrimaryIncumbentTable,
    tt: &ShardedParetoTT,
    sensitivity: &CombatOrderSensitivity,
    allow_paid_optional_potions: bool,
    eval_mode: EvalMode,
) {
    let Some((
        final_extra_end,
        state_slab_idx,
        state_off,
        state_len,
        state_hash,
        turn,
        turn_completed,
        action_count,
        potion_count,
        explicit_potion_count,
        automatic_potion_count,
        potion_strategic_cost,
        explicit_potion_strategic_cost,
        ambergris_count,
        forced_used_mask,
        forced_strategic_hp_cost,
        forced_ambergris_count,
        is_terminal,
        fast_eval,
        full_eval,
        label,
    )) = prepare_staged_candidate(
        scratch,
        task,
        extra_end_turn,
        false,
        choices.is_empty(),
        content,
        root_baseline,
        policy,
        profile,
        cert,
        incumbent_table,
        tt,
        sensitivity,
        allow_paid_optional_potions,
        eval_mode,
    )
    else {
        return;
    };

    let used_potion_slot_mask = task.parent_used_potion_slot_mask
        | match task.spec {
            ActionSpec::UsePotion { potion_slot, .. } if potion_slot < 16 => 1u16 << potion_slot,
            _ => 0,
        };

    scratch.candidates.push(WorkerCandidate {
        sort_key: (task.parent_rank, task.action_ord, choice_ord),
        parent_id: task.parent_id,
        action,
        extra_end_turn: final_extra_end,
        choices,
        state_slab_idx,
        state_off,
        state_len,
        state_hash,
        turn,
        turn_completed,
        action_count,
        potion_count,
        explicit_potion_count,
        automatic_potion_count,
        used_potion_slot_mask,
        potion_strategic_cost,
        explicit_potion_strategic_cost,
        ambergris_count,
        forced_used_mask,
        forced_strategic_hp_cost,
        forced_ambergris_count,
        is_terminal,
        fast_eval,
        full_eval,
        label,
    });
}

#[allow(clippy::too_many_arguments)]
fn stage_resolved_choice(
    scratch: &mut WorkerScratch,
    task: &ActionTask,
    action: CompactAction,
    choices: Vec<Vec<usize>>,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    incumbent_table: &PrimaryIncumbentTable,
    tt: &ShardedParetoTT,
    sensitivity: &CombatOrderSensitivity,
    allow_paid_optional_potions: bool,
    eval_mode: EvalMode,
) {
    let Some((
        final_extra_end,
        state_slab_idx,
        state_off,
        state_len,
        state_hash,
        turn,
        turn_completed,
        action_count,
        potion_count,
        explicit_potion_count,
        automatic_potion_count,
        potion_strategic_cost,
        explicit_potion_strategic_cost,
        ambergris_count,
        forced_used_mask,
        forced_strategic_hp_cost,
        forced_ambergris_count,
        is_terminal,
        fast_eval,
        full_eval,
        label,
    )) = prepare_staged_candidate(
        scratch,
        task,
        None,
        true,
        false,
        content,
        root_baseline,
        policy,
        profile,
        cert,
        incumbent_table,
        tt,
        sensitivity,
        allow_paid_optional_potions,
        eval_mode,
    )
    else {
        return;
    };

    let used_potion_slot_mask = task.parent_used_potion_slot_mask
        | match task.spec {
            ActionSpec::UsePotion { potion_slot, .. } if potion_slot < 16 => 1u16 << potion_slot,
            _ => 0,
        };

    scratch.choice_resolved.push(ResolvedChoiceCandidate {
        choices,
        action,
        extra_end_turn: final_extra_end,
        state_slab_idx,
        state_off,
        state_len,
        state_hash,
        turn,
        turn_completed,
        action_count,
        potion_count,
        explicit_potion_count,
        automatic_potion_count,
        used_potion_slot_mask,
        potion_strategic_cost,
        explicit_potion_strategic_cost,
        ambergris_count,
        forced_used_mask,
        forced_strategic_hp_cost,
        forced_ambergris_count,
        is_terminal,
        fast_eval,
        full_eval,
        label,
        lookahead_score: 0.0,
        lookahead_hp_lost: fast_eval.cumulative_hp_lost,
        lookahead_board_investment: full_eval.as_ref().map_or(0, |f| f.board_investment),
        has_lookahead: false,
        lookahead_victory: fast_eval.won,
    });
}

#[allow(clippy::too_many_arguments)]
fn resolve_choices_recursive(
    scratch: &mut WorkerScratch,
    choices_so_far: &mut Vec<Vec<usize>>,
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
    sensitivity: &CombatOrderSensitivity,
    allow_paid_optional_potions: bool,
    eval_mode: EvalMode,
) {
    if depth >= 4 || scratch.choice_resolved.len() >= CHOICE_RESOLVED_CAPACITY {
        return;
    }
    let choice_options = {
        let paused = &scratch.choice_paused_states[depth];
        clone_battle_state_into(paused, &mut scratch.state);
        let Some(pending) = paused.pending.as_ref() else {
            return;
        };
        let req = &pending.request;
        let max_branches = if req.source_pile == PileType::Hand {
            profile.max_hand_choice_branches_per_action
        } else {
            profile.max_pile_choice_branches_per_action
        };
        enumerate_canonical_choices(
            &mut scratch.state,
            content,
            req,
            max_branches,
            policy.accelerations.enable_multiset_choice_dedup,
        )
    };

    for sel in choice_options {
        if scratch.choice_resolved.len() >= CHOICE_RESOLVED_CAPACITY {
            break;
        }
        clone_battle_state_into(&scratch.choice_paused_states[depth], &mut scratch.state);
        scratch.transitions += 1;
        let Ok(outcome) = CombatEngine::resume_choice(&mut scratch.state, content, &sel) else {
            continue;
        };
        choices_so_far.push(sel);
        match outcome {
            StepOutcome::Completed | StepOutcome::CombatEnded { .. } => {
                stage_resolved_choice(
                    scratch,
                    task,
                    action,
                    choices_so_far.clone(),
                    content,
                    root_baseline,
                    policy,
                    profile,
                    cert,
                    incumbent_table,
                    tt,
                    sensitivity,
                    allow_paid_optional_potions,
                    eval_mode,
                );
            }
            StepOutcome::NeedsChoice(_) => {
                if depth + 1 < 4
                    && allows_brightest_flame(
                        policy.brightest_flame_max_hp_loss_limit,
                        root_baseline.initial_brightest_flame_max_hp_spent,
                        root_baseline.brightest_flame_spent(&scratch.state),
                    )
                {
                    clone_battle_state_into(&scratch.state, &mut scratch.choice_paused_states[depth + 1]);
                    resolve_choices_recursive(
                        scratch,
                        choices_so_far,
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
                        sensitivity,
                        allow_paid_optional_potions,
                        eval_mode,
                    );
                }
            }
        }
        choices_so_far.pop();
    }
}

#[inline]
fn compare_resolved_choice(
    a: &ResolvedChoiceCandidate,
    b: &ResolvedChoiceCandidate,
    criterion: usize,
) -> std::cmp::Ordering {
    let score_of = |c: &ResolvedChoiceCandidate| -> f64 {
        c.full_eval
            .as_ref()
            .map_or(c.fast_eval.base_score, |f| f.beam_rank_score)
    };
    let board_of = |c: &ResolvedChoiceCandidate| -> i32 {
        c.full_eval.as_ref().map_or(0, |f| f.board_investment)
    };
    let opt_of = |c: &ResolvedChoiceCandidate| -> i32 {
        c.fast_eval.energy.max(0)
            + c.fast_eval.stars.max(0)
            + c.full_eval
                .as_ref()
                .map_or(0, |f| f.playable_card_count + f.next_turn_draw)
    };

    match criterion {
        1 => {
            let cmp = score_of(b).total_cmp(&score_of(a));
            if !cmp.is_eq() {
                return cmp;
            }
        }
        2 => {
            let cmp = a.lookahead_hp_lost.cmp(&b.lookahead_hp_lost);
            if !cmp.is_eq() {
                return cmp;
            }
            let cmp = a
                .fast_eval
                .cumulative_hp_lost
                .cmp(&b.fast_eval.cumulative_hp_lost);
            if !cmp.is_eq() {
                return cmp;
            }
        }
        3 => {
            let cmp = b.lookahead_board_investment.cmp(&a.lookahead_board_investment);
            if !cmp.is_eq() {
                return cmp;
            }
            let cmp = board_of(b).cmp(&board_of(a));
            if !cmp.is_eq() {
                return cmp;
            }
            let cmp = opt_of(b).cmp(&opt_of(a));
            if !cmp.is_eq() {
                return cmp;
            }
        }
        _ => {}
    }

    b.lookahead_victory
        .cmp(&a.lookahead_victory)
        .then_with(|| b.has_lookahead.cmp(&a.has_lookahead))
        .then_with(|| {
            if a.has_lookahead && b.has_lookahead {
                b.lookahead_score.total_cmp(&a.lookahead_score)
            } else {
                std::cmp::Ordering::Equal
            }
        })
        .then_with(|| score_of(b).total_cmp(&score_of(a)))
        .then_with(|| a.state_hash.cmp(&b.state_hash))
}

#[allow(clippy::too_many_arguments)]
fn commit_resolved_choices(
    scratch: &mut WorkerScratch,
    task: &ActionTask,
    first_pile: PileType,
    is_end_turn: bool,
    static_ctx: &Arc<RootStaticContext>,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    profile: &SolverSearchProfile,
    eval_mode: EvalMode,
) {
    let resolved_count = scratch.choice_resolved.len();
    if resolved_count == 0 {
        return;
    }

    let max_branches = if first_pile == PileType::Hand {
        profile.max_hand_choice_branches_per_action
    } else {
        profile.max_pile_choice_branches_per_action
    }
    .max(1);

    let is_delayed_or_end_turn = is_end_turn
        || matches!(
            first_pile,
            PileType::Draw | PileType::Discard | PileType::Exhaust
        );
    let run_mini_rollout =
        resolved_count >= 2 && (resolved_count > max_branches || is_delayed_or_end_turn);

    if run_mini_rollout {
        for i in 0..resolved_count {
            let (is_term, slab_idx, off, len, potion_cost, action_count) = {
                let c = &scratch.choice_resolved[i];
                (
                    c.is_terminal,
                    c.state_slab_idx,
                    c.state_off,
                    c.state_len,
                    c.potion_strategic_cost,
                    c.action_count,
                )
            };
            let probe = if is_term {
                let c = &scratch.choice_resolved[i];
                let term_score = compute_beam_rank_score_with_mode(
                    &c.fast_eval,
                    &FullEvalDetails::default(),
                    root_baseline,
                    profile.base_score_only,
                    profile.boss_tempo_hp_pricing,
                    profile.beam_weight_perturbation,
                    eval_mode,
                    potion_cost,
                    action_count,
                );
                ChoiceProbeResult {
                    score: term_score,
                    hp_lost: c.fast_eval.cumulative_hp_lost,
                    board_investment: 0,
                    is_victory: c.fast_eval.won,
                    is_defeat: c.fast_eval.player_dead || c.fast_eval.projected_player_hp <= 0,
                }
            } else {
                let state_bytes = scratch.slab.get(slab_idx, off, len);
                let Some(probe) = evaluate_choice_probe_packed(
                    &mut scratch.choice_probe_ws,
                    state_bytes,
                    static_ctx,
                    content,
                    root_baseline,
                    profile,
                    eval_mode,
                    1,
                    potion_cost,
                    action_count,
                ) else {
                    continue;
                };
                probe
            };
            let encoded = encode_choice_probe_rank_score(&probe);
            let c = &mut scratch.choice_resolved[i];
            c.lookahead_score = encoded;
            c.lookahead_hp_lost = probe.hp_lost;
            c.lookahead_board_investment = probe.board_investment;
            c.lookahead_victory = probe.is_victory;
            c.has_lookahead = true;
            if !c.turn_completed {
                if let Some(fe) = c.full_eval.as_mut() {
                    fe.has_choice_lookahead = true;
                    fe.rollout_score = Some(encoded);
                }
            }
        }
    }

    let mut selected_indices: SmallVec<[usize; CHOICE_RESOLVED_CAPACITY]> = SmallVec::new();
    if resolved_count <= max_branches {
        for i in 0..resolved_count {
            selected_indices.push(i);
        }
        selected_indices
            .sort_unstable_by(|&ia, &ib| compare_resolved_choice(&scratch.choice_resolved[ia], &scratch.choice_resolved[ib], 0));
    } else {
        let cap = max_branches.min(resolved_count);
        let primary_q = ((cap * 55 + 50) / 100).max(1);
        let immediate_q = if cap >= 4 { ((cap * 15) / 100).max(1) } else { 0 };
        let survival_q = if cap >= 3 { ((cap * 15) / 100).max(1) } else { 0 };
        let invest_q = cap.saturating_sub(primary_q + immediate_q + survival_q);

        let mut marks = [false; CHOICE_RESOLVED_CAPACITY];
        for &(take, criterion) in &[
            (primary_q, 0),
            (immediate_q, 1),
            (survival_q, 2),
            (invest_q, 3),
            (cap, 0),
        ] {
            for _ in 0..take {
                if selected_indices.len() >= cap {
                    break;
                }
                let mut best_i: Option<usize> = None;
                for i in 0..resolved_count {
                    if marks[i] {
                        continue;
                    }
                    if best_i.is_none_or(|bi| {
                        compare_resolved_choice(
                            &scratch.choice_resolved[i],
                            &scratch.choice_resolved[bi],
                            criterion,
                        )
                        .is_lt()
                    }) {
                        best_i = Some(i);
                    }
                }
                let Some(bi) = best_i else { break };
                marks[bi] = true;
                selected_indices.push(bi);
            }
        }
        selected_indices
            .sort_unstable_by(|&ia, &ib| compare_resolved_choice(&scratch.choice_resolved[ia], &scratch.choice_resolved[ib], 0));
    }

    scratch.choice_staged.clear();
    scratch
        .choice_staged
        .extend(scratch.choice_resolved.drain(..).map(Some));
    for choice_ord in 0..selected_indices.len() {
        let idx = selected_indices[choice_ord];
        let Some(cand) = scratch.choice_staged[idx].take() else {
            continue;
        };
        scratch.candidates.push(WorkerCandidate {
            sort_key: (task.parent_rank, task.action_ord, choice_ord as u32),
            parent_id: task.parent_id,
            action: cand.action,
            extra_end_turn: cand.extra_end_turn,
            choices: cand.choices,
            state_slab_idx: cand.state_slab_idx,
            state_off: cand.state_off,
            state_len: cand.state_len,
            state_hash: cand.state_hash,
            turn: cand.turn,
            turn_completed: cand.turn_completed,
            action_count: cand.action_count,
            potion_count: cand.potion_count,
            explicit_potion_count: cand.explicit_potion_count,
            automatic_potion_count: cand.automatic_potion_count,
            used_potion_slot_mask: cand.used_potion_slot_mask,
            potion_strategic_cost: cand.potion_strategic_cost,
            explicit_potion_strategic_cost: cand.explicit_potion_strategic_cost,
            ambergris_count: cand.ambergris_count,
            forced_used_mask: cand.forced_used_mask,
            forced_strategic_hp_cost: cand.forced_strategic_hp_cost,
            forced_ambergris_count: cand.forced_ambergris_count,
            is_terminal: cand.is_terminal,
            fast_eval: cand.fast_eval,
            full_eval: cand.full_eval,
            label: cand.label,
        });
    }
}

#[allow(clippy::too_many_arguments)]
fn execute_action_from_parent(
    scratch: &mut WorkerScratch,
    parent_state: &BattleState,
    task: &ActionTask,
    static_ctx: &Arc<RootStaticContext>,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    incumbent_table: &PrimaryIncumbentTable,
    tt: &ShardedParetoTT,
    sensitivity: &CombatOrderSensitivity,
    allow_paid_optional_potions: bool,
    eval_mode: EvalMode,
) {
    clone_battle_state_into(parent_state, &mut scratch.state);

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
                profile,
                cert,
                incumbent_table,
                tt,
                sensitivity,
                allow_paid_optional_potions,
                eval_mode,
            );
        }
        StepOutcome::NeedsChoice(req) => {
            if !allows_brightest_flame(
                policy.brightest_flame_max_hp_loss_limit,
                root_baseline.initial_brightest_flame_max_hp_spent,
                root_baseline.brightest_flame_spent(&scratch.state),
            ) {
                return;
            }
            let first_pile = req.source_pile;
            let is_end_turn = matches!(task.spec, ActionSpec::EndTurn);
            scratch.begin_choice_tree();
            clone_battle_state_into(&scratch.state, &mut scratch.choice_paused_states[0]);
            let mut choices_so_far = Vec::with_capacity(4);
            resolve_choices_recursive(
                scratch,
                &mut choices_so_far,
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
                sensitivity,
                allow_paid_optional_potions,
                eval_mode,
            );
            commit_resolved_choices(
                scratch,
                task,
                first_pile,
                is_end_turn,
                static_ctx,
                content,
                root_baseline,
                profile,
                eval_mode,
            );
        }
    }
}

#[allow(clippy::too_many_arguments)]
fn expand_wave_tasks(
    parent_slice: &[NodeId],
    dop: usize,
    pool: Option<&rayon::ThreadPool>,
    static_ctx: &Arc<RootStaticContext>,
    arena: &NodeArena,
    main_arena: &MainStateArena,
    content: &(dyn ContentDispatcher + Sync),
    root_baseline: &RootEvalBaseline,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    incumbent_table: &PrimaryIncumbentTable,
    tt: &ShardedParetoTT,
    sensitivity: &CombatOrderSensitivity,
    allow_paid_optional_potions: bool,
    eval_mode: EvalMode,
    cancel: &AtomicBool,
    scratches: &mut [WorkerScratch],
    parent_states: &mut [BattleState],
) {
    for s in scratches.iter_mut() {
        s.reset_for_wave();
    }
    if parent_slice.is_empty() {
        return;
    }

    if dop <= 1 || pool.is_none() {
        let scratch = &mut scratches[0];
        let parent_state = &mut parent_states[0];
        for (parent_rank, &pid) in parent_slice.iter().enumerate() {
            if cancel.load(Ordering::Relaxed) {
                break;
            }
            let pnode = arena.get(pid);
            let pfull = arena.full_eval(pid);
            let p_has_la = pfull.is_some_and(|f| f.has_choice_lookahead);
            let p_la_score = pfull.and_then(|f| f.rollout_score);
            let pbytes = main_arena.get(pnode.state_handle);
            if unpack_state(pbytes, static_ctx.clone(), parent_state).is_err() {
                continue;
            }
            let mut local_tasks = std::mem::take(&mut scratch.action_buf);
            local_tasks.clear();
            enumerate_parent_tasks(
                parent_rank as u32,
                pid,
                pnode,
                p_has_la,
                p_la_score,
                parent_state,
                content,
                policy,
                profile,
                sensitivity,
                allow_paid_optional_potions,
                root_baseline.renewable_potion_shaped_rock,
                &mut local_tasks,
                &mut scratch.por_pruned,
            );
            for task in &local_tasks {
                if cancel.load(Ordering::Relaxed) {
                    break;
                }
                execute_action_from_parent(
                    scratch,
                    parent_state,
                    task,
                    static_ctx,
                    content,
                    root_baseline,
                    policy,
                    profile,
                    cert,
                    incumbent_table,
                    tt,
                    sensitivity,
                    allow_paid_optional_potions,
                    eval_mode,
                );
            }
            scratch.action_buf = local_tasks;
        }
        return;
    }

    let pool = pool.unwrap();
    if parent_slice.len() < dop {
        // Few parents (e.g., Turn 1 root): unpack each once and distribute action tasks across all workers.
        let mut unpacked_parents: Vec<BattleState> = Vec::with_capacity(parent_slice.len());
        let mut flat_tasks: Vec<(usize, ActionTask)> = Vec::with_capacity(parent_slice.len() * 8);
        for (parent_rank, &pid) in parent_slice.iter().enumerate() {
            let pnode = arena.get(pid);
            let pfull = arena.full_eval(pid);
            let p_has_la = pfull.is_some_and(|f| f.has_choice_lookahead);
            let p_la_score = pfull.and_then(|f| f.rollout_score);
            let pbytes = main_arena.get(pnode.state_handle);
            let mut pstate = empty_scratch_state(static_ctx.clone());
            if unpack_state(pbytes, static_ctx.clone(), &mut pstate).is_err() {
                continue;
            }
            let p_idx = unpacked_parents.len();
            let mut local_tasks = std::mem::take(&mut scratches[0].action_buf);
            local_tasks.clear();
            enumerate_parent_tasks(
                parent_rank as u32,
                pid,
                pnode,
                p_has_la,
                p_la_score,
                &mut pstate,
                content,
                policy,
                profile,
                sensitivity,
                allow_paid_optional_potions,
                root_baseline.renewable_potion_shaped_rock,
                &mut local_tasks,
                &mut scratches[0].por_pruned,
            );
            for t in local_tasks.drain(..) {
                flat_tasks.push((p_idx, t));
            }
            scratches[0].action_buf = local_tasks;
            unpacked_parents.push(pstate);
        }

        if flat_tasks.is_empty() {
            return;
        }
        if flat_tasks.len() <= 1 {
            let (p_idx, ref task) = flat_tasks[0];
            execute_action_from_parent(
                &mut scratches[0],
                &unpacked_parents[p_idx],
                task,
                static_ctx,
                content,
                root_baseline,
                policy,
                profile,
                cert,
                incumbent_table,
                tt,
                sensitivity,
                allow_paid_optional_potions,
                eval_mode,
            );
            return;
        }

        let active = dop.min(flat_tasks.len()).min(scratches.len());
        let next_idx = AtomicUsize::new(0);
        let tasks_ref = &flat_tasks;
        let parents_ref = &unpacked_parents;
        pool.scope(|scope| {
            for scratch in scratches[..active].iter_mut() {
                let next_ref = &next_idx;
                scope.spawn(move |_| loop {
                    if cancel.load(Ordering::Relaxed) {
                        break;
                    }
                    let idx = next_ref.fetch_add(1, Ordering::Relaxed);
                    if idx >= tasks_ref.len() {
                        break;
                    }
                    let (p_idx, ref task) = tasks_ref[idx];
                    execute_action_from_parent(
                        scratch,
                        &parents_ref[p_idx],
                        task,
                        static_ctx,
                        content,
                        root_baseline,
                        policy,
                        profile,
                        cert,
                        incumbent_table,
                        tt,
                        sensitivity,
                        allow_paid_optional_potions,
                        eval_mode,
                    );
                });
            }
        });
        return;
    }

    // Standard wave with parent_slice.len() >= dop: parent-grouped atomic work stealing across reused workers.
    let active = dop.min(parent_slice.len()).min(scratches.len()).min(parent_states.len());
    let next_parent = AtomicUsize::new(0);
    pool.scope(|scope| {
        for (scratch, parent_state) in scratches
            .iter_mut()
            .zip(parent_states.iter_mut())
            .take(active)
        {
            let next_ref = &next_parent;
            scope.spawn(move |_| loop {
                if cancel.load(Ordering::Relaxed) {
                    break;
                }
                let parent_rank = next_ref.fetch_add(1, Ordering::Relaxed);
                if parent_rank >= parent_slice.len() {
                    break;
                }
                let pid = parent_slice[parent_rank];
                let pnode = arena.get(pid);
                let pfull = arena.full_eval(pid);
                let p_has_la = pfull.is_some_and(|f| f.has_choice_lookahead);
                let p_la_score = pfull.and_then(|f| f.rollout_score);
                let pbytes = main_arena.get(pnode.state_handle);
                if unpack_state(pbytes, static_ctx.clone(), parent_state).is_err() {
                    continue;
                }
                let mut local_tasks = std::mem::take(&mut scratch.action_buf);
                local_tasks.clear();
                enumerate_parent_tasks(
                    parent_rank as u32,
                    pid,
                    pnode,
                    p_has_la,
                    p_la_score,
                    parent_state,
                    content,
                    policy,
                    profile,
                    sensitivity,
                    allow_paid_optional_potions,
                    root_baseline.renewable_potion_shaped_rock,
                    &mut local_tasks,
                    &mut scratch.por_pruned,
                );
                for task in &local_tasks {
                    if cancel.load(Ordering::Relaxed) {
                        break;
                    }
                    execute_action_from_parent(
                        scratch,
                        parent_state,
                        task,
                        static_ctx,
                        content,
                        root_baseline,
                        policy,
                        profile,
                        cert,
                        incumbent_table,
                        tt,
                        sensitivity,
                        allow_paid_optional_potions,
                        eval_mode,
                    );
                }
                scratch.action_buf = local_tasks;
            });
        }
    });
}

fn replay_fixed_prefix(
    root: &BattleState,
    prefix: &[PlannedAction],
    policy: &SearchPolicy,
    renewable_rock: bool,
    content: &dyn ContentDispatcher,
) -> Result<(BattleState, u16, i32, u16, u16, u64, i32, u16), KernelError> {
    let mut state = root.clone();
    let mut potion_count = 0u16;
    let mut potion_cost = 0i32;
    let mut ambergris_count = 0u16;
    let mut used_potion_slot_mask = 0u16;
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
                if *potion_slot < 16 {
                    used_potion_slot_mask |= 1u16 << *potion_slot;
                }
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
        used_potion_slot_mask,
        forced_used_mask,
        forced_strategic_hp_cost,
        forced_ambergris_count,
    ))
}

fn make_stand_pat_task(
    parent_rank: u32,
    parent_id: NodeId,
    pnode: &SearchNode,
    action_ord: u32,
) -> ActionTask {
    ActionTask {
        parent_rank,
        parent_id,
        parent_turn: pnode.turn,
        parent_action_count: pnode.action_count,
        parent_potion_count: pnode.potion_count,
        parent_explicit_potion_count: pnode.explicit_potion_count,
        parent_automatic_potion_count: pnode.automatic_potion_count,
        parent_used_potion_slot_mask: pnode.used_potion_slot_mask,
        parent_potion_cost: pnode.potion_strategic_cost,
        parent_explicit_potion_cost: pnode.explicit_potion_strategic_cost,
        parent_ambergris_count: pnode.ambergris_count,
        parent_forced_used_mask: pnode.forced_used_mask,
        parent_forced_strategic_hp_cost: pnode.forced_strategic_hp_cost,
        parent_forced_ambergris_count: pnode.forced_ambergris_count,
        parent_has_choice_lookahead: false,
        parent_choice_lookahead_score: None,
        action_ord,
        spec: ActionSpec::EndTurn,
    }
}

fn select_stand_pat_candidates(
    active_frontier: &[NodeId],
    arena: &NodeArena,
    max_total: usize,
    prefer_potion_buckets: bool,
) -> SmallVec<[NodeId; 16]> {
    let cap = max_total.min(active_frontier.len()).min(16);
    let mut out = SmallVec::new();
    if cap == 0 {
        return out;
    }

    let mut counts = [0usize; 4];
    for &id in active_frontier {
        let b = (arena.get(id).potion_count as usize).min(3);
        counts[b] += 1;
    }
    let active_buckets = counts.iter().filter(|&&c| c > 0).count();
    if active_buckets <= 1 {
        for &id in active_frontier.iter().take(cap) {
            out.push(id);
        }
        return out;
    }

    let mut quotas = [0usize; 4];
    let mut remaining = cap;
    let start_bucket = usize::from(prefer_potion_buckets);
    while remaining > 0 {
        let mut progressed = false;
        for step in 0..4 {
            if remaining == 0 {
                break;
            }
            let b = (start_bucket + step) % 4;
            if quotas[b] < counts[b] {
                quotas[b] += 1;
                remaining -= 1;
                progressed = true;
            }
        }
        if !progressed {
            break;
        }
    }

    let mut taken = [0usize; 4];
    for &id in active_frontier {
        if out.len() >= cap {
            break;
        }
        let b = (arena.get(id).potion_count as usize).min(3);
        if taken[b] >= quotas[b] {
            continue;
        }
        taken[b] += 1;
        out.push(id);
    }
    out
}

#[allow(clippy::too_many_arguments)]
fn record_victory_and_tighten(
    root_baseline: &RootEvalBaseline,
    cert: &ClosureCertificate,
    incumbent_table: &PrimaryIncumbentTable,
    fast_eval: &FastEvalSummary,
    win_turn: i32,
    total_potions: u16,
    explicit_potion_count: u16,
    potion_strategic_cost: i32,
    explicit_potion_strategic_cost: i32,
    forced_used_mask: u64,
    forced_strategic_hp_cost: i32,
    has_victory: &mut bool,
    best_victory_turn: &mut Option<i32>,
    best_victory_burden: &mut Option<(i32, i32, i32)>,
    best_victory_clean_zero_cost: &mut bool,
) {
    let relic_heal = ActEndingBossPolicy::ranked_post_combat_relic_heal(
        root_baseline.post_combat_relic_heal,
        true,
        fast_eval.player_hp,
        fast_eval.player_max_hp,
    );
    let max_hp_gain_credit = if !root_baseline.ignore_long_term_rewards
        && root_baseline.boss_hp_relief != crate::eval::BossHpRelief::RunEnding
    {
        (fast_eval.player_max_hp - root_baseline.initial_player_max_hp).max(0) * 2
    } else {
        0
    };
    let deficit = ActEndingBossPolicy::strategic_hp_deficit(
        fast_eval.cumulative_hp_lost,
        (root_baseline.initial_player_max_hp - fast_eval.player_max_hp).max(0),
        fast_eval.recovered_hp + relic_heal,
        root_baseline.boss_hp_relief,
        fast_eval.death_save_hp_restored,
    ) - fast_eval.strategic_hp_credit
        - max_hp_gain_credit;

    incumbent_table.tighten(
        PrimaryIncumbentBucket {
            potions: total_potions,
            forfeits_gold: false,
        },
        PrimarySearchIncumbent {
            strategic_hp_deficit: deficit,
            combat_ended_turn: win_turn,
            potion_strategic_cost,
        },
    );

    let enforce_forced = root_baseline.all_forced_potion_mask != 0;
    if enforce_forced
        && (forced_used_mask & root_baseline.all_forced_potion_mask)
            != root_baseline.all_forced_potion_mask
    {
        *has_victory = true;
        if best_victory_turn.is_none() {
            *best_victory_turn = Some(win_turn);
        }
        return;
    }

    let forced_uses = if enforce_forced {
        (forced_used_mask & root_baseline.all_forced_potion_mask).count_ones() as i32
    } else {
        0
    };
    let forced_cost = if enforce_forced {
        forced_strategic_hp_cost
    } else {
        0
    };
    let optional_count = (explicit_potion_count as i32 - forced_uses).max(0);
    let optional_cost = PotionUsePolicy::apply_replacement_credit(
        (explicit_potion_strategic_cost - forced_cost).max(0),
        optional_count,
        root_baseline.potion_replacement_hp_credit,
    );
    let burden = deficit.saturating_add(ActEndingBossPolicy::effective_optional_potion_burden(
        optional_count,
        optional_cost,
        root_baseline.boss_hp_relief,
    ));
    let key = (burden, deficit, win_turn);
    *has_victory = true;
    if best_victory_burden.is_none_or(|cur| key < cur) {
        *best_victory_burden = Some(key);
        *best_victory_turn = Some(win_turn);
        *best_victory_clean_zero_cost = total_potions == 0
            && deficit <= 0
            && !root_baseline.has_paid_heal_or_growth_potion
            && root_baseline.active_relic_targets.is_empty()
            && !root_baseline.effective_has_growth_targets
            && cert.healing_certified;
    }
}

#[allow(clippy::too_many_arguments)]
fn run_beam_pass(
    root: &BattleState,
    content: &(dyn ContentDispatcher + Sync),
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    incumbent_table: &PrimaryIncumbentTable,
    pool: Option<&rayon::ThreadPool>,
    cancel: &AtomicBool,
    progress_cb: Option<&dyn Fn(&SearchProgress)>,
    options: PassSearchOptions,
    fixed_prefix: Option<&[PlannedAction]>,
) -> Result<BeamRunOutput, KernelError> {
    let dop = policy
        .max_degree_of_parallelism
        .clamp(1, crate::eval::weights::MAXIMUM_SEARCH_MAX_DEGREE_OF_PARALLELISM);
    let active_pool = if dop > 1 { pool } else { None };
    let worker_count = if active_pool.is_some() { dop } else { 1 };
    let mut resources = PassResources::new(&root.static_ctx, worker_count);
    run_beam_pass_with_resources(
        root,
        content,
        policy,
        profile,
        cert,
        incumbent_table,
        pool,
        cancel,
        progress_cb,
        options,
        fixed_prefix,
        &mut resources,
    )
}

#[allow(clippy::too_many_arguments)]
fn run_beam_pass_with_resources(
    root: &BattleState,
    content: &(dyn ContentDispatcher + Sync),
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    incumbent_table: &PrimaryIncumbentTable,
    pool: Option<&rayon::ThreadPool>,
    cancel: &AtomicBool,
    progress_cb: Option<&dyn Fn(&SearchProgress)>,
    options: PassSearchOptions,
    fixed_prefix: Option<&[PlannedAction]>,
    resources: &mut PassResources,
) -> Result<BeamRunOutput, KernelError> {
    let started = Instant::now();
    let root_baseline = RootEvalBaseline::from_state_with_policy(root, content, policy);
    let sensitivity = CombatOrderSensitivity::analyze(root, content);
    let dop = policy
        .max_degree_of_parallelism
        .clamp(1, crate::eval::weights::MAXIMUM_SEARCH_MAX_DEGREE_OF_PARALLELISM);
    let active_pool = if dop > 1 { pool } else { None };
    let worker_count = if active_pool.is_some() { dop } else { 1 };

    resources.reset_for_pass();
    let PassResources {
        scratches,
        parent_states,
        rollout_workspaces,
        step_arena,
        next_step_arena,
        turn_staging_arena,
        tt,
        sort_list,
        shard_buckets,
        shard_accepted,
        accepted_buf,
    } = resources;
    debug_assert!(scratches.len() >= worker_count && parent_states.len() >= worker_count);

    let (
        start_state,
        prefix_potions,
        prefix_potion_cost,
        prefix_ambergris,
        prefix_used_slot_mask,
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
        (root.clone(), 0u16, 0i32, 0u16, 0u16, 0u64, 0i32, 0u16)
    };

    let mut arena = NodeArena::default();
    let prefix_len = fixed_prefix.map_or(0, |p| p.len() as u16);

    pack_state(&start_state, &mut scratches[0].pack_buf);
    let mut packed_state_bytes_total: u64 = scratches[0].pack_buf.len() as u64;
    let root_hash = hash_canonical_key(&start_state);
    let root_state_handle = step_arena.commit_slice(&scratches[0].pack_buf);
    let root_fast = evaluate_fast(&start_state, content, &root_baseline, None);
    let mut root_full = evaluate_full(&start_state, content, &root_fast, &root_baseline);
    root_full.beam_rank_score = compute_beam_rank_score_with_mode(
        &root_fast,
        &root_full,
        &root_baseline,
        profile.base_score_only,
        profile.boss_tempo_hp_pricing,
        profile.beam_weight_perturbation,
        options.eval_mode,
        prefix_potion_cost,
        prefix_len,
    );
    let mut best_seen_max_dark_evoke = root_full.max_dark_evoke;
    let mut best_seen_poison_and_doom = root_full.total_poison_and_doom;
    let mut best_seen_board_investment = root_full.board_investment;
    let root_full_idx = arena.full_evals.len() as u32;
    arena.full_evals.push(root_full);

    let root_id = arena.push_node(SearchNode {
        parent: None,
        action: None,
        extra_end_turn: None,
        choices_offset: 0,
        choices_len: 0,
        state_handle: root_state_handle,
        canonical_key_handle: PackedHandle::default(),
        state_hash: root_hash,
        turn: start_state.player.turn_number,
        action_count: prefix_len,
        potion_count: prefix_potions,
        explicit_potion_count: prefix_potions,
        automatic_potion_count: 0,
        used_potion_slot_mask: prefix_used_slot_mask,
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

    let mut current_turn_frontier: Vec<NodeId> = Vec::new();
    let mut completed: Vec<NodeId> = Vec::new();
    if arena.get(root_id).is_terminal {
        completed.push(root_id);
    } else {
        current_turn_frontier.push(root_id);
    }

    let mut expanded_nodes = 0usize;
    let mut transition_count = 0u64;
    let mut incumbent_pruned = 0u64;
    let mut por_pruned = 0u64;
    let mut termination: &'static str = "None";
    let mut early_turn_frontiers = Vec::new();
    let mut boss_tempo_prefixes = Vec::new();
    let mut latest_completed_turn_nodes: Vec<NodeId> = Vec::new();
    let mut last_active_frontier: Vec<NodeId> = Vec::new();

    let mut has_victory = completed.iter().any(|&id| arena.get(id).fast_eval.won);
    let mut best_won_hp_lost: Option<i32> = completed
        .iter()
        .filter(|&&id| arena.get(id).fast_eval.won)
        .map(|&id| arena.get(id).fast_eval.cumulative_hp_lost)
        .min();
    let mut last_progress_ms: Option<u64> = None;
    let mut best_seen_enemy_hp = root_fast.enemy_hp;
    let mut best_seen_alive_enemies = root_fast.alive_enemy_count + root_fast.reviving_enemy_count;
    let mut turns_without_progress = 0i32;
    let mut turns_since_enemy_hp_decreased = 0i32;
    let mut best_victory_turn: Option<i32> = None;
    let mut best_victory_burden: Option<(i32, i32, i32)> = None;
    let mut best_victory_clean_zero_cost = false;

    const MAX_ACTIONS_PER_TURN: usize = 64;
    let max_turns = policy.max_turns.max(1);
    let beam_width = profile.beam_width.max(1);

    for turn_layer in 0..max_turns {
        if current_turn_frontier.is_empty() {
            break;
        }
        if has_victory {
            if let Some(win_turn) = best_victory_turn {
                let current_turn = root_baseline.start_turn_number + turn_layer;
                let turn_cap = win_turn + if best_victory_clean_zero_cost { 1 } else { 4 };
                if current_turn > turn_cap {
                    break;
                }
            }
        }
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

        turn_staging_arena.clear();
        let mut active_frontier = std::mem::take(&mut current_turn_frontier);
        let mut next_turn_candidates: Vec<NodeId> = Vec::new();
        let remaining_pass_nodes = profile.max_expanded_nodes.saturating_sub(expanded_nodes);
        let per_turn_cap = if has_victory {
            remaining_pass_nodes
        } else {
            remaining_pass_nodes
                .min((beam_width * 40).max(profile.max_expanded_nodes / 4))
                .max(
                    crate::eval::weights::MINIMUM_TURN_LAYER_EXPANDED_NODES
                        .min(remaining_pass_nodes.max(1)),
                )
        };
        let mut expanded_this_turn = 0usize;
        let mut last_turn_hit_per_turn_cap = false;
        let mut aborted_limit = false;
        let mut hp_target_reached = false;

        let commit_wave_candidates =
            |scratches: &mut [WorkerScratch],
             tt: &mut ShardedParetoTT,
             arena: &mut NodeArena,
             turn_arena: &mut MainStateArena,
             completed: &mut Vec<NodeId>,
             next_step_candidates: &mut Vec<NodeId>,
             next_turn_candidates: &mut Vec<NodeId>,
             transition_count: &mut u64,
             incumbent_pruned: &mut u64,
             por_pruned: &mut u64,
             packed_state_bytes_total: &mut u64,
             has_victory: &mut bool,
             best_won_hp_lost: &mut Option<i32>,
             best_victory_turn: &mut Option<i32>,
             best_victory_burden: &mut Option<(i32, i32, i32)>,
             best_victory_clean_zero_cost: &mut bool,
             hp_target_reached: &mut bool,
             sort_list: &mut Vec<(usize, usize, (u32, u32, u32))>,
             shard_buckets: &mut [Vec<(usize, StateHash, TranspositionLabel, (usize, usize))>],
             shard_accepted: &mut [Vec<(usize, (usize, usize))>],
             accepted_buf: &mut Vec<(usize, (usize, usize))>| {
                sort_list.clear();
                for (w_idx, wo) in scratches.iter().enumerate() {
                    *transition_count += wo.transitions;
                    *incumbent_pruned += wo.incumbent_pruned;
                    tt.branches_pruned += wo.tt_probe_pruned;
                    *por_pruned += wo.por_pruned;
                    for (c_idx, cand) in wo.candidates.iter().enumerate() {
                        sort_list.push((w_idx, c_idx, cand.sort_key));
                    }
                }
                sort_list.sort_unstable_by_key(|&(_, _, sk)| sk);

                accepted_buf.clear();
                if sort_list.len() < 512 || active_pool.is_none() {
                    for &(w_idx, c_idx, _) in sort_list.iter() {
                        let cand = &scratches[w_idx].candidates[c_idx];
                        if cand.is_terminal || tt.try_insert_single(cand.state_hash, cand.label) {
                            accepted_buf.push((0, (w_idx, c_idx)));
                        }
                    }
                } else {
                    for b in shard_buckets.iter_mut() {
                        b.clear();
                    }
                    let mut terminal_accepted: SmallVec<[(usize, (usize, usize)); 16]> =
                        SmallVec::new();
                    for (global_ord, &(w_idx, c_idx, _)) in sort_list.iter().enumerate() {
                        let cand = &scratches[w_idx].candidates[c_idx];
                        if cand.is_terminal {
                            terminal_accepted.push((global_ord, (w_idx, c_idx)));
                            continue;
                        }
                        let shard = cand.state_hash.shard_index();
                        shard_buckets[shard].push((
                            global_ord,
                            cand.state_hash,
                            cand.label,
                            (w_idx, c_idx),
                        ));
                    }
                    if let Some(p) = active_pool {
                        p.install(|| {
                            tt.insert_sharded_batch_in_place(
                                shard_buckets,
                                shard_accepted,
                                true,
                                accepted_buf,
                            )
                        });
                    } else {
                        tt.insert_sharded_batch_in_place(
                            shard_buckets,
                            shard_accepted,
                            false,
                            accepted_buf,
                        );
                    }
                    if !terminal_accepted.is_empty() {
                        accepted_buf.extend(terminal_accepted);
                        accepted_buf.sort_unstable_by_key(|(idx, _)| *idx);
                    }
                }

                for &(_, (w_idx, c_idx)) in accepted_buf.iter() {
                let wo = &scratches[w_idx];
                let cand = &wo.candidates[c_idx];
                let state_handle = if cand.is_terminal {
                    PackedHandle::default()
                } else if cand.turn_completed {
                    let state_bytes =
                        wo.slab.get(cand.state_slab_idx, cand.state_off, cand.state_len);
                    *packed_state_bytes_total += state_bytes.len() as u64;
                    turn_arena.commit_slice(state_bytes)
                } else {
                    // Defer MainStateArena commit for mid-turn candidates until after beam pruning.
                    PackedHandle {
                        slab: w_idx as u32,
                        offset: c_idx as u32,
                        len: 0,
                    }
                };
                let (choices_offset, choices_len) = arena.choices.push_choices(&cand.choices);
                let full_eval_idx = cand.full_eval.map(|full| {
                    let idx = arena.full_evals.len() as u32;
                    arena.full_evals.push(full);
                    idx
                });

                let nid = arena.push_node(SearchNode {
                    parent: Some(cand.parent_id),
                    action: Some(cand.action),
                    extra_end_turn: cand.extra_end_turn,
                    choices_offset,
                    choices_len,
                    state_handle,
                    canonical_key_handle: PackedHandle::default(),
                    state_hash: cand.state_hash,
                    turn: cand.turn,
                    action_count: cand.action_count,
                    potion_count: cand.potion_count,
                    explicit_potion_count: cand.explicit_potion_count,
                    automatic_potion_count: cand.automatic_potion_count,
                    used_potion_slot_mask: cand.used_potion_slot_mask,
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
                    full_eval_idx,
                });

                if cand.is_terminal {
                    if cand.fast_eval.won {
                        *best_won_hp_lost = Some(match *best_won_hp_lost {
                            Some(cur) => cur.min(cand.fast_eval.cumulative_hp_lost),
                            None => cand.fast_eval.cumulative_hp_lost,
                        });
                        record_victory_and_tighten(
                            &root_baseline,
                            cert,
                            incumbent_table,
                            &cand.fast_eval,
                            cand.turn,
                            cand.potion_count,
                            cand.explicit_potion_count,
                            cand.potion_strategic_cost,
                            cand.explicit_potion_strategic_cost,
                            cand.forced_used_mask,
                            cand.forced_strategic_hp_cost,
                            has_victory,
                            best_victory_turn,
                            best_victory_burden,
                            best_victory_clean_zero_cost,
                        );
                        let node_ref = arena.get(nid);
                        if options.early_turn_scout_depth == 0
                            && options.boss_tempo_scout_turns == 0
                            && has_reached_acceptable_battle_hp_loss_node(
                                &root_baseline,
                                policy,
                                node_ref,
                            )
                            && (!options.allow_paid_optional_potions
                                || !root_baseline.has_paid_heal_or_growth_potion)
                        {
                            *hp_target_reached = true;
                        }
                    }
                    completed.push(nid);
                } else if cand.turn_completed {
                    next_turn_candidates.push(nid);
                } else {
                    next_step_candidates.push(nid);
                }
            }
        };

        for _action_depth in 0..=MAX_ACTIONS_PER_TURN {
            if active_frontier.is_empty() {
                break;
            }
            if cancel.load(Ordering::Relaxed) {
                termination = "Cancelled";
                aborted_limit = true;
                break;
            }
            if expanded_nodes >= profile.max_expanded_nodes {
                termination = BeamWidthPortfolio::NODE_LIMIT_TERMINATION;
                aborted_limit = true;
                break;
            }
            if expanded_this_turn >= per_turn_cap {
                last_turn_hit_per_turn_cap = true;
                break;
            }
            if profile.soft_time_budget_ms > 0
                && (started.elapsed().as_millis() as u64) >= profile.soft_time_budget_ms
            {
                termination = "TimeLimit";
                aborted_limit = true;
                break;
            }

            let allowed = (profile.max_expanded_nodes - expanded_nodes)
                .min(per_turn_cap - expanded_this_turn);
            let wave_count = active_frontier.len().min(allowed);
            if wave_count == 0 {
                break;
            }

            let parent_slice = &active_frontier[..wave_count];
            expanded_nodes += wave_count;
            expanded_this_turn += wave_count;

            expand_wave_tasks(
                parent_slice,
                dop,
                active_pool,
                &root.static_ctx,
                &arena,
                step_arena,
                content,
                &root_baseline,
                policy,
                profile,
                cert,
                incumbent_table,
                tt,
                &sensitivity,
                options.allow_paid_optional_potions,
                options.eval_mode,
                cancel,
                scratches,
                parent_states,
            );

            let mut next_step_candidates = Vec::new();
            commit_wave_candidates(
                scratches,
                tt,
                &mut arena,
                turn_staging_arena,
                &mut completed,
                &mut next_step_candidates,
                &mut next_turn_candidates,
                &mut transition_count,
                &mut incumbent_pruned,
                &mut por_pruned,
                &mut packed_state_bytes_total,
                &mut has_victory,
                &mut best_won_hp_lost,
                &mut best_victory_turn,
                &mut best_victory_burden,
                &mut best_victory_clean_zero_cost,
                &mut hp_target_reached,
                sort_list,
                shard_buckets,
                shard_accepted,
                accepted_buf,
            );

            if hp_target_reached {
                termination = "HpTarget";
                break;
            }

            active_frontier = BeamRetentionPolicy::prune_wave_with_potion_bias(
                &mut arena,
                &next_step_candidates,
                beam_width,
                profile.potion_free_beam_width,
                profile.potion_beam_width,
                &root_baseline,
                profile.second_rank_band,
                profile.aggressive_power_commitment,
                options.prefer_potion_buckets,
            );

            next_step_arena.clear();
            for &nid in &active_frontier {
                let tmp_h = arena.get(nid).state_handle;
                let wo = &scratches[tmp_h.slab as usize];
                let cand = &wo.candidates[tmp_h.offset as usize];
                let bytes = wo.slab.get(cand.state_slab_idx, cand.state_off, cand.state_len);
                packed_state_bytes_total += bytes.len() as u64;
                arena.get_mut(nid).state_handle = next_step_arena.commit_slice(bytes);
            }
            std::mem::swap(step_arena, next_step_arena);
            next_step_arena.clear();

            if let Some(cb) = progress_cb {
                let elapsed_ms = started.elapsed().as_millis() as u64;
                if last_progress_ms.is_none_or(|prev| elapsed_ms.saturating_sub(prev) >= 100) {
                    last_progress_ms = Some(elapsed_ms);
                    cb(&SearchProgress {
                        nodes_expanded: expanded_nodes,
                        transition_count,
                        current_turn: root_baseline.start_turn_number + turn_layer,
                        best_won: has_victory,
                        best_hp_lost: best_won_hp_lost,
                        elapsed_ms,
                    });
                }
            }
        }

        // Bounded StandPat EndTurn flush when a turn ended with mid-turn nodes still active
        let need_stand_pat = !hp_target_reached
            && !cancel.load(Ordering::Relaxed)
            && !active_frontier.is_empty()
            && ((next_turn_candidates.len() < beam_width && !aborted_limit)
                || (!has_victory
                    && latest_completed_turn_nodes.is_empty()
                    && next_turn_candidates.is_empty()));
        if need_stand_pat {
            let sp_ids = select_stand_pat_candidates(
                &active_frontier,
                &arena,
                16,
                options.prefer_potion_buckets,
            );
            if !sp_ids.is_empty() {
                for s in scratches.iter_mut() {
                    s.reset_for_wave();
                }
                for (rank, &nid) in sp_ids.iter().enumerate() {
                    let pnode = arena.get(nid).clone();
                    let pbytes = step_arena.get(pnode.state_handle);
                    if unpack_state(pbytes, root.static_ctx.clone(), &mut parent_states[0]).is_err()
                    {
                        continue;
                    }
                    let task = make_stand_pat_task(rank as u32, nid, &pnode, 0);
                    execute_action_from_parent(
                        &mut scratches[0],
                        &parent_states[0],
                        &task,
                        &root.static_ctx,
                        content,
                        &root_baseline,
                        policy,
                        profile,
                        cert,
                        incumbent_table,
                        tt,
                        &sensitivity,
                        options.allow_paid_optional_potions,
                        options.eval_mode,
                    );
                }
                let mut sp_mid_unused = Vec::new();
                commit_wave_candidates(
                    scratches,
                    tt,
                    &mut arena,
                    turn_staging_arena,
                    &mut completed,
                    &mut sp_mid_unused,
                    &mut next_turn_candidates,
                    &mut transition_count,
                    &mut incumbent_pruned,
                    &mut por_pruned,
                    &mut packed_state_bytes_total,
                    &mut has_victory,
                    &mut best_won_hp_lost,
                    &mut best_victory_turn,
                    &mut best_victory_burden,
                    &mut best_victory_clean_zero_cost,
                    &mut hp_target_reached,
                    sort_list,
                    shard_buckets,
                    shard_accepted,
                    accepted_buf,
                );
                if hp_target_reached {
                    termination = "HpTarget";
                }
            }
        }

        if !active_frontier.is_empty() {
            last_active_frontier = active_frontier;
        }

        if !next_turn_candidates.is_empty()
            && (!aborted_limit || latest_completed_turn_nodes.is_empty())
        {
            let surviving: Vec<NodeId> = next_turn_candidates
                .iter()
                .copied()
                .filter(|&nid| {
                    let n = arena.get(nid);
                    !n.fast_eval.player_dead && n.fast_eval.projected_player_hp > 0
                })
                .collect();
            if !surviving.is_empty() {
                latest_completed_turn_nodes = surviving;
            }
        }

        let completed_turns = turn_layer + 1;
        if options.early_turn_scout_depth > 0 && completed_turns <= options.early_turn_scout_depth
        {
            let f = select_early_turn_frontier(
                &arena,
                &next_turn_candidates,
                root_baseline.start_turn_number,
                completed_turns,
            );
            early_turn_frontiers.extend(f);
            if completed_turns >= options.early_turn_scout_depth {
                termination = "ScoutDepth";
                break;
            }
        }

        if options.boss_tempo_scout_turns > 0
            && completed_turns >= options.boss_tempo_scout_turns
        {
            for &nid in &next_turn_candidates {
                let n = arena.get(nid);
                if !n.fast_eval.player_dead {
                    let eliminated = (root_baseline.initial_enemy_count as i32)
                        - n.fast_eval.alive_enemy_count;
                    let rank = arena
                        .full_eval(nid)
                        .map_or(n.fast_eval.base_score, |e| e.beam_rank_score);
                    let mut actions = fixed_prefix.map_or_else(Vec::new, |p| p.to_vec());
                    actions.extend(arena.reconstruct_actions(nid));
                    let forced_uses_satisfied = (n.forced_used_mask
                        & root_baseline.all_forced_potion_mask)
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
            termination = "BossTempoScout";
            break;
        }

        if hp_target_reached || aborted_limit {
            break;
        }

        if next_turn_candidates.is_empty() {
            if last_turn_hit_per_turn_cap && !has_victory && termination == "None" {
                termination = BeamWidthPortfolio::NODE_LIMIT_TERMINATION;
            }
            break;
        }
        if has_victory {
            if let Some(win_turn) = best_victory_turn {
                let next_turn = root_baseline.start_turn_number + turn_layer + 1;
                let turn_cap = win_turn + if best_victory_clean_zero_cost { 1 } else { 4 };
                if next_turn > turn_cap {
                    break;
                }
            }
        }

        let has_time_for_rollout = profile.soft_time_budget_ms == 0
            || (started.elapsed().as_millis() as u64) < profile.soft_time_budget_ms;
        let ran_rollout = if options.rollout_horizon_turns > 0
            && !cancel.load(Ordering::Relaxed)
            && has_time_for_rollout
        {
            let shortlist_cap = (beam_width * 2).min(next_turn_candidates.len());
            let shortlist = if next_turn_candidates.len() > shortlist_cap {
                BeamRetentionPolicy::prune_wave_with_rollout(
                    &mut arena,
                    &next_turn_candidates,
                    shortlist_cap,
                    profile.potion_free_beam_width,
                    profile.potion_beam_width,
                    &root_baseline,
                    profile.second_rank_band,
                    profile.aggressive_power_commitment,
                    options.prefer_potion_buckets,
                    false,
                )
            } else {
                next_turn_candidates.clone()
            };
            let rollout_deadline = (profile.soft_time_budget_ms > 0)
                .then(|| started + Duration::from_millis(profile.soft_time_budget_ms));
            let rollouts = crate::rollout::evaluate_turn_boundary_rollouts_with_workspaces(
                turn_staging_arena,
                &arena,
                &shortlist,
                &root.static_ctx,
                content,
                &root_baseline,
                policy,
                profile,
                options.eval_mode,
                options.rollout_horizon_turns,
                rollout_deadline,
                active_pool,
                cancel,
                rollout_workspaces,
            );
            for (idx, maybe_rr) in rollouts.into_iter().enumerate() {
                let Some(rr) = maybe_rr else { continue };
                let pid = shortlist[idx];
                if let Some(full_idx) = arena.get(pid).full_eval_idx {
                    if let Some(fe) = arena.full_evals.get_mut(full_idx as usize) {
                        fe.rollout_score = Some(rr.rollout_score);
                    }
                }
                if let Some(pv) = rr.promoted_victory {
                    let pnode = arena.get(pid).clone();
                    let mut cur_parent = pid;
                    let mut cur_turn = pnode.turn;
                    let total_acts = pv.actions.len();
                    let mut final_nid = pid;
                    for (step_idx, act) in pv.actions.into_iter().enumerate() {
                        let is_last = step_idx + 1 == total_acts;
                        let (kind, ch_opt, step_turn) = match act {
                            PlannedAction::PlayCard {
                                card_handle,
                                target_handle,
                                choices,
                            } => (
                                CompactActionKind::PlayCard {
                                    card_handle: card_handle as u32,
                                    target_handle: target_handle.map(|h| h as u32),
                                },
                                choices,
                                cur_turn,
                            ),
                            PlannedAction::UsePotion {
                                potion_slot,
                                potion_id,
                                target_handle,
                                choices,
                            } => (
                                CompactActionKind::UsePotion {
                                    potion_slot: potion_slot as u16,
                                    potion_id,
                                    target_handle: target_handle.map(|h| h as u32),
                                },
                                choices,
                                cur_turn,
                            ),
                            PlannedAction::EndTurn { choices } => {
                                let t = cur_turn;
                                cur_turn += 1;
                                (CompactActionKind::EndTurn, choices, t)
                            }
                        };
                        let (c_off, c_len) = match ch_opt {
                            Some(ref c) => arena.choices.push_choices(c),
                            None => (0, 0),
                        };
                        let node_turn = if is_last { pv.final_turn } else { step_turn };
                        let nid = arena.push_node(SearchNode {
                            parent: Some(cur_parent),
                            action: Some(CompactAction {
                                kind,
                                turn: step_turn,
                                por_class: PorActionClass::Barrier,
                            }),
                            extra_end_turn: None,
                            choices_offset: c_off,
                            choices_len: c_len,
                            state_handle: PackedHandle::default(),
                            canonical_key_handle: PackedHandle::default(),
                            state_hash: if is_last {
                                pv.final_state_hash
                            } else {
                                pnode.state_hash
                            },
                            turn: node_turn,
                            action_count: pnode.action_count + step_idx as u16 + 1,
                            potion_count: if is_last {
                                pnode.potion_count + pv.auto_potions_used
                            } else {
                                pnode.potion_count
                            },
                            explicit_potion_count: pnode.explicit_potion_count,
                            automatic_potion_count: if is_last {
                                pnode.automatic_potion_count + pv.auto_potions_used
                            } else {
                                pnode.automatic_potion_count
                            },
                            used_potion_slot_mask: pnode.used_potion_slot_mask,
                            potion_strategic_cost: pnode.potion_strategic_cost,
                            explicit_potion_strategic_cost: pnode.explicit_potion_strategic_cost,
                            ambergris_count: pnode.ambergris_count,
                            forced_used_mask: pnode.forced_used_mask,
                            forced_strategic_hp_cost: pnode.forced_strategic_hp_cost,
                            forced_ambergris_count: pnode.forced_ambergris_count,
                            future_sold_hp: 0,
                            retention_rank: 0,
                            is_terminal: is_last,
                            boundary_rank: 0,
                            fast_eval: if is_last { pv.final_fast } else { pnode.fast_eval },
                            full_eval_idx: None,
                        });
                        cur_parent = nid;
                        final_nid = nid;
                    }
                    record_victory_and_tighten(
                        &root_baseline,
                        cert,
                        incumbent_table,
                        &pv.final_fast,
                        pv.final_turn,
                        pnode.potion_count + pv.auto_potions_used,
                        pnode.explicit_potion_count,
                        pnode.potion_strategic_cost,
                        pnode.explicit_potion_strategic_cost,
                        pnode.forced_used_mask,
                        pnode.forced_strategic_hp_cost,
                        &mut has_victory,
                        &mut best_victory_turn,
                        &mut best_victory_burden,
                        &mut best_victory_clean_zero_cost,
                    );
                    if has_reached_acceptable_battle_hp_loss_node(
                        &root_baseline,
                        policy,
                        arena.get(final_nid),
                    ) && (!options.allow_paid_optional_potions
                        || !root_baseline.has_paid_heal_or_growth_potion)
                    {
                        hp_target_reached = true;
                    }
                    if pv.final_fast.won {
                        best_won_hp_lost = Some(match best_won_hp_lost {
                            Some(cur) => cur.min(pv.final_fast.cumulative_hp_lost),
                            None => pv.final_fast.cumulative_hp_lost,
                        });
                    }
                    completed.push(final_nid);
                }
            }
            next_turn_candidates = shortlist;
            true
        } else {
            false
        };

        if hp_target_reached {
            termination = "HpTarget";
            break;
        }
        if has_victory {
            if let Some(win_turn) = best_victory_turn {
                let next_turn = root_baseline.start_turn_number + turn_layer + 1;
                let turn_cap = win_turn + if best_victory_clean_zero_cost { 1 } else { 4 };
                if next_turn > turn_cap {
                    break;
                }
            }
        }

        current_turn_frontier = BeamRetentionPolicy::prune_wave_with_rollout(
            &mut arena,
            &next_turn_candidates,
            beam_width,
            profile.potion_free_beam_width,
            profile.potion_beam_width,
            &root_baseline,
            profile.second_rank_band,
            profile.aggressive_power_commitment,
            options.prefer_potion_buckets,
            ran_rollout,
        );

        let mut layer_progressed = false;
        let mut enemy_hp_decreased = false;
        for &nid in &current_turn_frontier {
            let n = arena.get(nid);
            let f = &n.fast_eval;
            let rem_enemies = f.alive_enemy_count + f.reviving_enemy_count;
            if f.enemy_hp < best_seen_enemy_hp {
                best_seen_enemy_hp = f.enemy_hp;
                layer_progressed = true;
                enemy_hp_decreased = true;
            }
            if rem_enemies < best_seen_alive_enemies {
                best_seen_alive_enemies = rem_enemies;
                layer_progressed = true;
            }
            if let Some(full) = arena.full_eval(nid) {
                if full.max_dark_evoke > best_seen_max_dark_evoke {
                    best_seen_max_dark_evoke = full.max_dark_evoke;
                    layer_progressed = true;
                }
                if full.total_poison_and_doom > best_seen_poison_and_doom {
                    best_seen_poison_and_doom = full.total_poison_and_doom;
                    layer_progressed = true;
                }
                if full.board_investment > best_seen_board_investment {
                    best_seen_board_investment = full.board_investment;
                    layer_progressed = true;
                }
            }
        }
        turns_without_progress = if layer_progressed { 0 } else { turns_without_progress + 1 };
        turns_since_enemy_hp_decreased = if enemy_hp_decreased {
            0
        } else {
            turns_since_enemy_hp_decreased + 1
        };
        if turns_without_progress >= crate::eval::weights::SETUP_VALUE_HORIZON_TURNS {
            if !has_victory {
                termination = "TurnLimit";
            }
            break;
        }
        if root.static_ctx.room_type == RoomType::Boss
            && turn_layer + 1 >= 28
            && turns_since_enemy_hp_decreased > 2
        {
            if !has_victory && termination == "None" {
                termination = "TurnLimit";
            }
            break;
        }
        step_arena.clear();
        for &nid in &current_turn_frontier {
            let tmp_h = arena.get(nid).state_handle;
            if tmp_h.len > 0 {
                let bytes = turn_staging_arena.get(tmp_h);
                arena.get_mut(nid).state_handle = step_arena.commit_slice(bytes);
            }
        }
        turn_staging_arena.clear();
        tt.reset_generation();
    }

    if !has_victory && (termination != "None" || completed.is_empty()) {
        if !latest_completed_turn_nodes.is_empty() {
            completed.extend(latest_completed_turn_nodes.iter().copied());
        } else if !current_turn_frontier.is_empty() {
            completed.extend(current_turn_frontier.iter().copied());
        } else if completed.is_empty() && !last_active_frontier.is_empty() {
            completed.extend(last_active_frontier.iter().copied());
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
        best_turn,
        won,
        survives,
        death_save_use_count,
        player_hp,
        all_forced_uses_satisfied,
        strategic_hp_deficit,
        strategy_goal_hp_credit,
        strategy_goal_count,
        growth_rewards,
        relic_eval,
        recovered_hp,
        hp_lost,
        potion_count,
        optional_potion_count,
        optional_potion_strategic_cost,
        optional_ambergris_count,
        score,
        actions,
    ) = match selected {
        Some(sel) => {
            let mut acts = fixed_prefix.map_or_else(Vec::new, |p| p.to_vec());
            acts.extend(arena.reconstruct_actions(sel.node_id));
            (
                Some(sel.node_id),
                sel.turn,
                sel.won,
                sel.survives,
                sel.death_save_use_count,
                sel.player_hp,
                sel.all_forced_uses_satisfied,
                sel.strategic_hp_deficit,
                sel.strategy_goal_hp_credit,
                sel.strategy_goal_count,
                sel.growth_rewards,
                sel.relic_eval,
                sel.recovered_hp,
                sel.hp_lost,
                sel.potion_count,
                sel.optional_potion_count,
                sel.optional_potion_strategic_cost,
                sel.optional_ambergris_count,
                sel.score,
                acts,
            )
        }
        None => (
            None,
            start_state.player.turn_number,
            false,
            false,
            0,
            0,
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
            0,
            0,
            0,
            Vec::new(),
        ),
    };

    let (has_completed_turn_ongoing, completed_ongoing_turn) = match best_node {
        Some(nid) if !won && survives && !latest_completed_turn_nodes.is_empty() => {
            (true, arena.get(nid).turn)
        }
        _ => (false, -1),
    };

    let terminal_cards_hint = start_state
        .player
        .hand
        .iter()
        .filter_map(|&ch| start_state.card(ch).map(|c| c.id))
        .collect();

    let explicit_potion_count =
        best_node.map_or(0, |nid| arena.get(nid).explicit_potion_count as usize);

    Ok(BeamRunOutput {
        best_node,
        best_turn,
        arena,
        root_baseline,
        won,
        survives,
        death_save_use_count,
        player_hp,
        all_forced_uses_satisfied,
        strategic_hp_deficit,
        strategy_goal_hp_credit,
        strategy_goal_count,
        growth_rewards,
        relic_eval,
        recovered_hp,
        hp_lost,
        potion_count,
        explicit_potion_count,
        optional_potion_count,
        optional_potion_strategic_cost,
        optional_ambergris_count,
        has_completed_turn_ongoing,
        completed_ongoing_turn,
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
    cancel: &AtomicBool,
    options: &NoveltySearchOptions,
) -> Result<BeamRunOutput, KernelError> {
    let started = Instant::now();
    let root_baseline = RootEvalBaseline::from_state_with_policy(root, content, policy);
    let sensitivity = CombatOrderSensitivity::analyze(root, content);
    let mut arena = NodeArena::default();
    let mut main_arena = MainStateArena::default();
    let mut scratch = WorkerScratch::new(root.static_ctx.clone());
    let mut parent_state = empty_scratch_state(root.static_ctx.clone());
    let tt = ShardedParetoTT::new(65_536);

    pack_state(root, &mut scratch.pack_buf);
    let root_hash = hash_canonical_key(root);
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
        canonical_key_handle: PackedHandle::default(),
        state_hash: root_hash,
        turn: root.player.turn_number,
        action_count: 0,
        potion_count: 0,
        explicit_potion_count: 0,
        automatic_potion_count: 0,
        used_potion_slot_mask: 0,
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
        if unpack_state(pbytes, root.static_ctx.clone(), &mut parent_state).is_err() {
            continue;
        }
        let pfull = arena.full_eval(item.node_id).copied().unwrap_or_default();
        capture_novelty_facts(&parent_state, &pnode.fast_eval, &pfull, &mut facts_buf);
        novelty.prepare_parent(
            (pnode.fast_eval.enemy_hp / 10, pnode.potion_count as i32),
            &facts_buf,
        );

        let mut tasks = Vec::new();
        enumerate_parent_tasks(
            0,
            item.node_id,
            &pnode,
            pfull.has_choice_lookahead,
            pfull.rollout_score,
            &mut parent_state,
            content,
            policy,
            profile,
            &sensitivity,
            true,
            root_baseline.renewable_potion_shaped_rock,
            &mut tasks,
            &mut por_pruned,
        );

        scratch.candidates.clear();
        for task in &tasks {
            execute_action_from_parent(
                &mut scratch,
                &parent_state,
                task,
                &root.static_ctx,
                content,
                &root_baseline,
                policy,
                profile,
                cert,
                incumbent_table,
                &tt,
                &sensitivity,
                true,
                EvalMode::Balanced,
            );
        }

        let children: Vec<WorkerCandidate> = scratch.candidates.drain(..).collect();
        for cand in children {
            let (choices_offset, choices_len) = arena.choices.push_choices(&cand.choices);
            if cand.is_terminal {
                let full_eval_idx = cand.full_eval.map(|f| {
                    let i = arena.full_evals.len() as u32;
                    arena.full_evals.push(f);
                    i
                });
                let cid = arena.push_node(SearchNode {
                    parent: Some(cand.parent_id),
                    action: Some(cand.action),
                    extra_end_turn: cand.extra_end_turn,
                    choices_offset,
                    choices_len,
                    state_handle: PackedHandle::default(),
                    canonical_key_handle: PackedHandle::default(),
                    state_hash: cand.state_hash,
                    turn: cand.turn,
                    action_count: cand.action_count,
                    potion_count: cand.potion_count,
                    explicit_potion_count: cand.explicit_potion_count,
                    automatic_potion_count: cand.automatic_potion_count,
                    used_potion_slot_mask: cand.used_potion_slot_mask,
                    potion_strategic_cost: cand.potion_strategic_cost,
                    explicit_potion_strategic_cost: cand.explicit_potion_strategic_cost,
                    ambergris_count: cand.ambergris_count,
                    forced_used_mask: cand.forced_used_mask,
                    forced_strategic_hp_cost: cand.forced_strategic_hp_cost,
                    forced_ambergris_count: cand.forced_ambergris_count,
                    future_sold_hp: 0,
                    retention_rank: 0,
                    is_terminal: true,
                    boundary_rank: 0,
                    fast_eval: cand.fast_eval,
                    full_eval_idx,
                });
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
            let state_bytes = scratch.slab.get(cand.state_slab_idx, cand.state_off, cand.state_len);
            if unpack_state(state_bytes, root.static_ctx.clone(), &mut scratch.state).is_err() {
                continue;
            }
            let cfull = cand
                .full_eval
                .unwrap_or_else(|| evaluate_full(&scratch.state, content, &cand.fast_eval, &root_baseline));
            capture_novelty_facts(&scratch.state, &cand.fast_eval, &cfull, &mut facts_buf);
            let w = novelty.evaluate(
                (cand.fast_eval.enemy_hp / 10, cand.potion_count as i32),
                &facts_buf,
            );

            let full_idx = arena.full_evals.len() as u32;
            arena.full_evals.push(cfull);
            let cid = arena.push_node(SearchNode {
                parent: Some(cand.parent_id),
                action: Some(cand.action),
                extra_end_turn: cand.extra_end_turn,
                choices_offset,
                choices_len,
                state_handle: main_arena.commit_slice(state_bytes),
                canonical_key_handle: PackedHandle::default(),
                state_hash: cand.state_hash,
                turn: cand.turn,
                action_count: cand.action_count,
                potion_count: cand.potion_count,
                explicit_potion_count: cand.explicit_potion_count,
                automatic_potion_count: cand.automatic_potion_count,
                used_potion_slot_mask: cand.used_potion_slot_mask,
                potion_strategic_cost: cand.potion_strategic_cost,
                explicit_potion_strategic_cost: cand.explicit_potion_strategic_cost,
                ambergris_count: cand.ambergris_count,
                forced_used_mask: cand.forced_used_mask,
                forced_strategic_hp_cost: cand.forced_strategic_hp_cost,
                forced_ambergris_count: cand.forced_ambergris_count,
                future_sold_hp: 0,
                retention_rank: 0,
                is_terminal: false,
                boundary_rank: 0,
                fast_eval: cand.fast_eval,
                full_eval_idx: Some(full_idx),
            });

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
        best_turn,
        won,
        survives,
        death_save_use_count,
        player_hp,
        all_forced_uses_satisfied,
        strategic_hp_deficit,
        strategy_goal_hp_credit,
        strategy_goal_count,
        growth_rewards,
        relic_eval,
        recovered_hp,
        hp_lost,
        potion_count,
        optional_potion_count,
        optional_potion_strategic_cost,
        optional_ambergris_count,
        score,
        actions,
    ) = match selected {
        Some(sel) => (
            Some(sel.node_id),
            sel.turn,
            sel.won,
            sel.survives,
            sel.death_save_use_count,
            sel.player_hp,
            sel.all_forced_uses_satisfied,
            sel.strategic_hp_deficit,
            sel.strategy_goal_hp_credit,
            sel.strategy_goal_count,
            sel.growth_rewards,
            sel.relic_eval,
            sel.recovered_hp,
            sel.hp_lost,
            sel.potion_count,
            sel.optional_potion_count,
            sel.optional_potion_strategic_cost,
            sel.optional_ambergris_count,
            sel.score,
            arena.reconstruct_actions(sel.node_id),
        ),
        None => (
            None,
            root.player.turn_number,
            false,
            false,
            0,
            0,
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
            0,
            0,
            0,
            Vec::new(),
        ),
    };

    let total_committed = main_arena.total_bytes_committed;
    let explicit_potion_count =
        best_node.map_or(0, |nid| arena.get(nid).explicit_potion_count as usize);
    Ok(BeamRunOutput {
        best_node,
        best_turn,
        arena,
        root_baseline,
        won,
        survives,
        death_save_use_count,
        player_hp,
        all_forced_uses_satisfied,
        strategic_hp_deficit,
        strategy_goal_hp_credit,
        strategy_goal_count,
        growth_rewards,
        relic_eval,
        recovered_hp,
        hp_lost,
        potion_count,
        explicit_potion_count,
        optional_potion_count,
        optional_potion_strategic_cost,
        optional_ambergris_count,
        has_completed_turn_ongoing: false,
        completed_ongoing_turn: -1,
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
    cancel: &AtomicBool,
    options: &BossTempoSearchOptions,
) -> Result<BeamRunOutput, KernelError> {
    let started = Instant::now();
    let root_baseline = RootEvalBaseline::from_state_with_policy(root, content, policy);
    let sensitivity = CombatOrderSensitivity::analyze(root, content);
    let mut arena = NodeArena::default();
    let mut main_arena = MainStateArena::default();
    let mut scratch = WorkerScratch::new(root.static_ctx.clone());
    let mut parent_state = empty_scratch_state(root.static_ctx.clone());
    let tt = ShardedParetoTT::new(16_384);
    let mut tempo_tt = BossTempoTranspositions::new(32_768);
    let mut boss_profile = profile.clone();
    boss_profile.boss_tempo_hp_pricing = true;

    pack_state(root, &mut scratch.pack_buf);
    let root_hash = hash_canonical_key(root);
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
        canonical_key_handle: PackedHandle::default(),
        state_hash: root_hash,
        turn: root.player.turn_number,
        action_count: 0,
        potion_count: 0,
        explicit_potion_count: 0,
        automatic_potion_count: 0,
        used_potion_slot_mask: 0,
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
                pnode.forced_used_mask,
            ),
        ) {
            continue;
        }
        expanded_nodes += 1;
        let pbytes = main_arena.get(pnode.state_handle);
        if unpack_state(pbytes, root.static_ctx.clone(), &mut parent_state).is_err() {
            continue;
        }
        let pfull = arena.full_eval(item.node_id);
        let p_has_la = pfull.is_some_and(|f| f.has_choice_lookahead);
        let p_la_score = pfull.and_then(|f| f.rollout_score);
        let mut tasks = Vec::new();
        enumerate_parent_tasks(
            0,
            item.node_id,
            &pnode,
            p_has_la,
            p_la_score,
            &mut parent_state,
            content,
            policy,
            &boss_profile,
            &sensitivity,
            true,
            root_baseline.renewable_potion_shaped_rock,
            &mut tasks,
            &mut por_pruned,
        );
        scratch.candidates.clear();
        for task in &tasks {
            execute_action_from_parent(
                &mut scratch,
                &parent_state,
                task,
                &root.static_ctx,
                content,
                &root_baseline,
                policy,
                &boss_profile,
                cert,
                incumbent_table,
                &tt,
                &sensitivity,
                true,
                EvalMode::Balanced,
            );
        }

        let mut next_nodes = Vec::new();
        for cand in scratch.candidates.drain(..) {
            let state_handle = if cand.is_terminal {
                PackedHandle::default()
            } else {
                let state_bytes =
                    scratch.slab.get(cand.state_slab_idx, cand.state_off, cand.state_len);
                main_arena.commit_slice(state_bytes)
            };
            let rank_score = cand
                .full_eval
                .map_or(cand.fast_eval.base_score, |f| f.beam_rank_score);
            let full_eval_idx = cand.full_eval.map(|cfull| {
                let full_idx = arena.full_evals.len() as u32;
                arena.full_evals.push(cfull);
                full_idx
            });
            let (choices_offset, choices_len) = arena.choices.push_choices(&cand.choices);
            let cid = arena.push_node(SearchNode {
                parent: Some(cand.parent_id),
                action: Some(cand.action),
                extra_end_turn: cand.extra_end_turn,
                choices_offset,
                choices_len,
                state_handle,
                canonical_key_handle: PackedHandle::default(),
                state_hash: cand.state_hash,
                turn: cand.turn,
                action_count: cand.action_count,
                potion_count: cand.potion_count,
                explicit_potion_count: cand.explicit_potion_count,
                automatic_potion_count: cand.automatic_potion_count,
                used_potion_slot_mask: cand.used_potion_slot_mask,
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
                full_eval_idx,
            });
            if cand.is_terminal {
                completed.push(cid);
            } else if !cand.fast_eval.player_dead {
                next_nodes.push((cid, cand.turn > pnode.turn, rank_score));
            }
        }
        next_nodes.sort_by(|a, b| a.1.cmp(&b.1).then_with(|| b.2.total_cmp(&a.2)));
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
        best_turn,
        won,
        survives,
        death_save_use_count,
        player_hp,
        all_forced_uses_satisfied,
        strategic_hp_deficit,
        strategy_goal_hp_credit,
        strategy_goal_count,
        growth_rewards,
        relic_eval,
        recovered_hp,
        hp_lost,
        potion_count,
        optional_potion_count,
        optional_potion_strategic_cost,
        optional_ambergris_count,
        score,
        actions,
    ) = match selected {
        Some(sel) => (
            Some(sel.node_id),
            sel.turn,
            sel.won,
            sel.survives,
            sel.death_save_use_count,
            sel.player_hp,
            sel.all_forced_uses_satisfied,
            sel.strategic_hp_deficit,
            sel.strategy_goal_hp_credit,
            sel.strategy_goal_count,
            sel.growth_rewards,
            sel.relic_eval,
            sel.recovered_hp,
            sel.hp_lost,
            sel.potion_count,
            sel.optional_potion_count,
            sel.optional_potion_strategic_cost,
            sel.optional_ambergris_count,
            sel.score,
            arena.reconstruct_actions(sel.node_id),
        ),
        None => (
            None,
            root.player.turn_number,
            false,
            false,
            0,
            0,
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
            0,
            0,
            0,
            Vec::new(),
        ),
    };

    let total_committed = main_arena.total_bytes_committed;
    let explicit_potion_count =
        best_node.map_or(0, |nid| arena.get(nid).explicit_potion_count as usize);
    Ok(BeamRunOutput {
        best_node,
        best_turn,
        arena,
        root_baseline,
        won,
        survives,
        death_save_use_count,
        player_hp,
        all_forced_uses_satisfied,
        strategic_hp_deficit,
        strategy_goal_hp_credit,
        strategy_goal_count,
        growth_rewards,
        relic_eval,
        recovered_hp,
        hp_lost,
        potion_count,
        explicit_potion_count,
        optional_potion_count,
        optional_potion_strategic_cost,
        optional_ambergris_count,
        has_completed_turn_ongoing: false,
        completed_ongoing_turn: -1,
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
    is_better_run_output_with_policy(cand, cur, None, None)
}

fn is_better_run_output_with_policy(
    cand: &BeamRunOutput,
    cur: &BeamRunOutput,
    policy: Option<&SearchPolicy>,
    zero_potion_baseline: Option<&BeamRunOutput>,
) -> bool {
    if cand.all_forced_uses_satisfied != cur.all_forced_uses_satisfied {
        return cand.all_forced_uses_satisfied;
    }
    if cand.won != cur.won {
        return cand.won;
    }
    if cand.survives != cur.survives {
        return cand.survives;
    }
    if cand.death_save_use_count != cur.death_save_use_count {
        return cand.death_save_use_count < cur.death_save_use_count;
    }

    if let Some(p) = policy {
        if p.potion_policy == SolverPotionPolicy::Smart && cand.won {
            let zero_base = zero_potion_baseline
                .filter(|z| z.won && z.optional_potion_count == 0)
                .or_else(|| (cur.won && cur.optional_potion_count == 0).then_some(cur));
            if let Some(base) = zero_base {
                if cand.optional_potion_count > 0 {
                    let req_saved = PotionUsePolicy::smart_required_hp_saved(
                        cand.optional_potion_strategic_cost,
                        cand.root_baseline.boss_hp_relief,
                    );
                    let eligible = PotionUsePolicy::is_eligible(
                        SolverPotionPolicy::Smart,
                        cand.optional_potion_count as i32,
                        req_saved,
                        true,
                        base.strategic_hp_deficit,
                        true,
                        true,
                        cand.strategic_hp_deficit,
                    ) || cand.death_save_use_count < base.death_save_use_count;
                    let ambergris_ok = cand.death_save_use_count < base.death_save_use_count
                        || PotionUsePolicy::meets_ambergris_restriction(
                            true,
                            cand.optional_ambergris_count,
                            cand.optional_potion_strategic_cost,
                            cand.root_baseline.initial_player_max_hp,
                            base.player_hp,
                            cand.player_hp,
                        );
                    if !eligible || !ambergris_ok {
                        return false;
                    }
                }
            }
        }
    }

    let cand_burden = cand.strategic_hp_deficit.saturating_add(
        ActEndingBossPolicy::effective_optional_potion_burden(
            cand.optional_potion_count as i32,
            cand.optional_potion_strategic_cost,
            cand.root_baseline.boss_hp_relief,
        ),
    );
    let cur_burden = cur.strategic_hp_deficit.saturating_add(
        ActEndingBossPolicy::effective_optional_potion_burden(
            cur.optional_potion_count as i32,
            cur.optional_potion_strategic_cost,
            cur.root_baseline.boss_hp_relief,
        ),
    );
    if cand_burden != cur_burden {
        return cand_burden < cur_burden;
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
    if !cand.won && !cand.survives && !cur.won && !cur.survives && cand.best_turn != cur.best_turn {
        return cand.best_turn > cur.best_turn;
    }
    if cand.potion_count != cur.potion_count {
        return cand.potion_count < cur.potion_count;
    }
    cand.score > cur.score
}

fn should_run_smart_potion_pass(
    zero_baseline: Option<&BeamRunOutput>,
    root_baseline: &RootEvalBaseline,
) -> bool {
    if root_baseline.paid_optional_potion_count == 0 {
        return false;
    }
    let Some(base) = zero_baseline else {
        return true;
    };
    if !base.won || !base.survives || base.death_save_use_count > 0 {
        return true;
    }
    if root_baseline.has_paid_heal_or_growth_potion {
        return true;
    }
    let min_cost = PotionUsePolicy::apply_replacement_credit(
        root_baseline.min_paid_potion_cost,
        1,
        root_baseline.potion_replacement_hp_credit,
    );
    let min_req_saved =
        PotionUsePolicy::smart_required_hp_saved(min_cost, root_baseline.boss_hp_relief);
    base.strategic_hp_deficit >= min_req_saved
}

#[allow(clippy::too_many_arguments)]
fn run_adaptive_stage(
    root: &BattleState,
    content: &(dyn ContentDispatcher + Sync),
    policy: &SearchPolicy,
    base_profile: &SolverSearchProfile,
    root_baseline_info: &RootEvalBaseline,
    cert: &ClosureCertificate,
    incumbent_table: &PrimaryIncumbentTable,
    pool: Option<&rayon::ThreadPool>,
    cancel: &AtomicBool,
    progress_cb: Option<&dyn Fn(&SearchProgress)>,
    started: &Instant,
    stage_width: usize,
    stage_node_ceiling: usize,
    eval_mode: EvalMode,
    rollout_horizon_turns: usize,
    total_expanded: &mut usize,
    total_transitions: &mut u64,
    total_tt_pruned: &mut u64,
    total_incumbent_pruned: &mut u64,
    total_por_pruned: &mut u64,
    baseline_zero_potion: &mut Option<BeamRunOutput>,
    selected_best: &mut Option<BeamRunOutput>,
    resources: &mut PassResources,
) -> Result<(), KernelError> {
    let stage_available = stage_node_ceiling.saturating_sub(*total_expanded);
    if stage_available == 0 || cancel.load(Ordering::Relaxed) {
        return Ok(());
    }
    let rem_ms = || -> Option<u64> {
        if base_profile.soft_time_budget_ms == 0 {
            Some(0)
        } else {
            let elapsed = started.elapsed().as_millis() as u64;
            if elapsed >= base_profile.soft_time_budget_ms {
                None
            } else {
                Some((base_profile.soft_time_budget_ms - elapsed).max(1))
            }
        }
    };
    let Some(avail_ms_before_a) = rem_ms() else {
        return Ok(());
    };

    let smart_mode = policy.potion_policy == SolverPotionPolicy::Smart;
    let is_disabled_mode = policy.potion_policy == SolverPotionPolicy::Disabled;
    let reserve_for_paid_potions =
        smart_mode && root_baseline_info.paid_optional_potion_count > 0;
    let phase_a_allow_paid = !smart_mode && !is_disabled_mode;

    let phase_a_nodes = if reserve_for_paid_potions {
        (stage_available * 60 / 100)
            .max(500.min(stage_available))
            .min(stage_available)
    } else {
        stage_available
    };
    let phase_a_time_ms = if avail_ms_before_a == 0 {
        0
    } else if reserve_for_paid_potions {
        (avail_ms_before_a * 65 / 100).max(50).min(avail_ms_before_a)
    } else {
        avail_ms_before_a
    };

    let mut phase_a_profile = base_profile.clone();
    phase_a_profile.beam_width = stage_width.max(1);
    phase_a_profile.max_expanded_nodes = phase_a_nodes.max(1);
    phase_a_profile.soft_time_budget_ms = phase_a_time_ms;

    let phase_a = run_beam_pass_with_resources(
        root,
        content,
        policy,
        &phase_a_profile,
        cert,
        incumbent_table,
        pool,
        cancel,
        progress_cb,
        PassSearchOptions {
            allow_paid_optional_potions: phase_a_allow_paid,
            prefer_potion_buckets: phase_a_allow_paid
                && root_baseline_info.paid_optional_potion_count > 0,
            eval_mode,
            early_turn_scout_depth: 0,
            boss_tempo_scout_turns: 0,
            rollout_horizon_turns,
        },
        None,
        resources,
    )?;

    *total_expanded += phase_a.expanded_nodes;
    *total_transitions += phase_a.transition_count;
    *total_tt_pruned += phase_a.tt_pruned;
    *total_incumbent_pruned += phase_a.incumbent_pruned;
    *total_por_pruned += phase_a.por_pruned;

    let phase_a_is_zero_baseline = phase_a.optional_potion_count == 0;
    let mut take_a = match selected_best.as_ref() {
        None => true,
        Some(cur) => {
            is_better_run_output_with_policy(&phase_a, cur, Some(policy), baseline_zero_potion.as_ref())
        }
    };
    let update_zero = if phase_a_is_zero_baseline {
        match baseline_zero_potion.as_ref() {
            None => true,
            Some(cur_z) => is_better_run_output(&phase_a, cur_z),
        }
    } else {
        false
    };
    if reserve_for_paid_potions
        && should_run_smart_potion_pass(
            if update_zero {
                Some(&phase_a)
            } else {
                baseline_zero_potion.as_ref()
            },
            root_baseline_info,
        )
        && !cancel.load(Ordering::Relaxed)
        && *total_expanded < stage_node_ceiling
    {
        if let Some(avail_ms_b) = rem_ms() {
            // Update zero_potion_baseline & selected_best before cloning/moving
            if take_a || update_zero {
                if take_a && !update_zero {
                    *selected_best = Some(phase_a);
                } else {
                    // Retain zero baseline in baseline_zero_potion; if take_a also update selected_best carefully.
                    if take_a {
                        let summary_clone = BeamRunOutput {
                            best_node: phase_a.best_node,
                            best_turn: phase_a.best_turn,
                            arena: NodeArena::default(),
                            root_baseline: phase_a.root_baseline.clone(),
                            won: phase_a.won,
                            survives: phase_a.survives,
                            death_save_use_count: phase_a.death_save_use_count,
                            player_hp: phase_a.player_hp,
                            all_forced_uses_satisfied: phase_a.all_forced_uses_satisfied,
                            strategic_hp_deficit: phase_a.strategic_hp_deficit,
                            strategy_goal_hp_credit: phase_a.strategy_goal_hp_credit,
                            strategy_goal_count: phase_a.strategy_goal_count,
                            growth_rewards: phase_a.growth_rewards,
                            relic_eval: phase_a.relic_eval,
                            recovered_hp: phase_a.recovered_hp,
                            hp_lost: phase_a.hp_lost,
                            potion_count: phase_a.potion_count,
                            explicit_potion_count: phase_a.explicit_potion_count,
                            optional_potion_count: phase_a.optional_potion_count,
                            optional_potion_strategic_cost: phase_a.optional_potion_strategic_cost,
                            optional_ambergris_count: phase_a.optional_ambergris_count,
                            has_completed_turn_ongoing: phase_a.has_completed_turn_ongoing,
                            completed_ongoing_turn: phase_a.completed_ongoing_turn,
                            score: phase_a.score,
                            actions: phase_a.actions.clone(),
                            expanded_nodes: phase_a.expanded_nodes,
                            transition_count: phase_a.transition_count,
                            tt_pruned: phase_a.tt_pruned,
                            incumbent_pruned: phase_a.incumbent_pruned,
                            por_pruned: phase_a.por_pruned,
                            packed_state_bytes_total: phase_a.packed_state_bytes_total,
                            termination: phase_a.termination,
                            terminal_cards_hint: phase_a.terminal_cards_hint.clone(),
                            early_turn_frontiers: Vec::new(),
                            boss_tempo_prefixes: Vec::new(),
                        };
                        *selected_best = Some(phase_a);
                        *baseline_zero_potion = Some(summary_clone);
                    } else {
                        *baseline_zero_potion = Some(phase_a);
                    }
                }
            }

            let phase_b_nodes = stage_node_ceiling.saturating_sub(*total_expanded).max(1);
            let potion_width = ((stage_width * 5) / 4).max(stage_width).min(512);
            let mut phase_b_profile = base_profile.clone();
            phase_b_profile.beam_width = potion_width;
            phase_b_profile.max_expanded_nodes = phase_b_nodes;
            phase_b_profile.soft_time_budget_ms = avail_ms_b;

            let phase_b = run_beam_pass_with_resources(
                root,
                content,
                policy,
                &phase_b_profile,
                cert,
                incumbent_table,
                pool,
                cancel,
                progress_cb,
                PassSearchOptions {
                    allow_paid_optional_potions: true,
                    prefer_potion_buckets: true,
                    eval_mode,
                    early_turn_scout_depth: 0,
                    boss_tempo_scout_turns: 0,
                    rollout_horizon_turns,
                },
                None,
                resources,
            )?;

            *total_expanded += phase_b.expanded_nodes;
            *total_transitions += phase_b.transition_count;
            *total_tt_pruned += phase_b.tt_pruned;
            *total_incumbent_pruned += phase_b.incumbent_pruned;
            *total_por_pruned += phase_b.por_pruned;

            let take_b = match selected_best.as_ref() {
                None => true,
                Some(cur) => is_better_run_output_with_policy(
                    &phase_b,
                    cur,
                    Some(policy),
                    baseline_zero_potion.as_ref(),
                ),
            };
            if take_b {
                *selected_best = Some(phase_b);
            } else {
                let phase_b_term = phase_b.termination;
                if phase_b.optional_potion_count == 0
                    && baseline_zero_potion
                        .as_ref()
                        .is_none_or(|z| is_better_run_output(&phase_b, z))
                {
                    *baseline_zero_potion = Some(phase_b);
                }
                if let Some(cur) = selected_best.as_mut() {
                    if !cur.won
                        && cur.termination == "None"
                        && matches!(phase_b_term, "TimeLimit" | "NodeLimit")
                    {
                        cur.termination = phase_b_term;
                    }
                }
            }
            return Ok(());
        }
    }

    if take_a {
        *selected_best = Some(phase_a);
        take_a = false;
    } else if update_zero {
        *baseline_zero_potion = Some(phase_a);
    }
    let _ = take_a;
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn solve_beam_or_portfolio(
    root: &BattleState,
    content: &(dyn ContentDispatcher + Sync),
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    incumbent_table: &PrimaryIncumbentTable,
    pool: Option<&rayon::ThreadPool>,
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
            pool,
            cancel,
            progress_cb,
            PassSearchOptions::default(),
            None,
        )?;
        *total_expanded += out.expanded_nodes;
        *total_transitions += out.transition_count;
        *total_tt_pruned += out.tt_pruned;
        *total_incumbent_pruned += out.incumbent_pruned;
        *total_por_pruned += out.por_pruned;
        return Ok(out);
    }

    let use_adaptive_stages = !policy.use_novelty_search
        && !policy.adaptive_novelty_refinement
        && policy
            .beam_width_portfolio_widths
            .as_ref()
            .is_none_or(|w| w.is_empty())
        && policy.beam_width_portfolio_plain_baseline_member
        && !policy.offensive_refinement_portfolio
        && !policy.bounded_offensive_refinement_portfolio
        && policy.portfolio_scheduling_mode == PortfolioSchedulingMode::Sequential;

    if use_adaptive_stages {
        let started = Instant::now();
        let root_baseline_info = RootEvalBaseline::from_state_with_policy(root, content, policy);
        let is_default_adaptive_profile =
            profile.beam_width == 96 && profile.max_expanded_nodes == 150_000;
        let is_elite_or_boss =
            matches!(root.static_ctx.room_type, RoomType::Elite | RoomType::Boss);
        let smart_mode = policy.potion_policy == SolverPotionPolicy::Smart;

        let stage1_width = if is_default_adaptive_profile {
            96
        } else {
            profile.beam_width.clamp(4, 512)
        };
        let stage1_ceiling = if is_default_adaptive_profile {
            150_000
        } else {
            profile.max_expanded_nodes
        };
        let stage2_width = if is_default_adaptive_profile {
            240
        } else {
            (profile.beam_width * 2).clamp(stage1_width, 512)
        };
        let stage2_ceiling = if is_default_adaptive_profile {
            320_000
        } else {
            profile.max_expanded_nodes
        };
        let stage3_width = 320.max(stage2_width).min(512);
        let stage3_ceiling = if is_default_adaptive_profile {
            500_000
        } else {
            profile.max_expanded_nodes
        };

        if cancel.load(Ordering::Relaxed) {
            return run_beam_pass(
                root,
                content,
                policy,
                profile,
                cert,
                incumbent_table,
                pool,
                cancel,
                progress_cb,
                PassSearchOptions::default(),
                None,
            );
        }

        let dop = policy
            .max_degree_of_parallelism
            .clamp(1, crate::eval::weights::MAXIMUM_SEARCH_MAX_DEGREE_OF_PARALLELISM);
        let worker_count = if dop > 1 && pool.is_some() { dop } else { 1 };
        let mut resources = PassResources::new(&root.static_ctx, worker_count);
        let mut baseline_zero_potion: Option<BeamRunOutput> = None;
        let mut selected_best: Option<BeamRunOutput> = None;

        // Stage 1: W=96 / 150k Balanced, rolloutHorizon=4
        run_adaptive_stage(
            root,
            content,
            policy,
            profile,
            &root_baseline_info,
            cert,
            incumbent_table,
            pool,
            cancel,
            progress_cb,
            &started,
            stage1_width,
            stage1_ceiling,
            EvalMode::Balanced,
            4,
            total_expanded,
            total_transitions,
            total_tt_pruned,
            total_incumbent_pruned,
            total_por_pruned,
            &mut baseline_zero_potion,
            &mut selected_best,
            &mut resources,
        )?;

        let Some(stage1_out) = selected_best.as_ref() else {
            return run_beam_pass_with_resources(
                root,
                content,
                policy,
                profile,
                cert,
                incumbent_table,
                pool,
                cancel,
                progress_cb,
                PassSearchOptions::default(),
                None,
                &mut resources,
            );
        };
        let stage1_clean = stage1_out.won
            && stage1_out.survives
            && stage1_out.death_save_use_count == 0;
        let stage1_eff_loss = stage1_out.strategic_hp_deficit.max(0);
        let stage1_paid = stage1_out.optional_potion_count;
        let stage1_meets_target = has_reached_acceptable_battle_hp_loss(
            &root_baseline_info,
            policy,
            stage1_out,
        ) && (!smart_mode || !root_baseline_info.has_paid_heal_or_growth_potion);
        let stage1_early_exit = stage1_meets_target
            || (stage1_clean
                && stage1_eff_loss <= 0
                && stage1_paid == 0
                && !root_baseline_info.has_paid_heal_or_growth_potion)
            || (!is_elite_or_boss
                && stage1_clean
                && stage1_eff_loss <= 3
                && stage1_paid == 0
                && stage1_out.termination == "None");

        let has_time_left = profile.soft_time_budget_ms == 0
            || (started.elapsed().as_millis() as u64) < profile.soft_time_budget_ms;
        if !stage1_early_exit
            && !cancel.load(Ordering::Relaxed)
            && has_time_left
            && *total_expanded < stage2_ceiling
            && (is_default_adaptive_profile || profile.max_expanded_nodes >= 20_000)
        {
            // Stage 2: W=240 / 320k cumulative SetupAndSurvival, rolloutHorizon=6
            run_adaptive_stage(
                root,
                content,
                policy,
                profile,
                &root_baseline_info,
                cert,
                incumbent_table,
                pool,
                cancel,
                progress_cb,
                &started,
                stage2_width,
                stage2_ceiling,
                EvalMode::SetupAndSurvival,
                6,
                total_expanded,
                total_transitions,
                total_tt_pruned,
                total_incumbent_pruned,
                total_por_pruned,
                &mut baseline_zero_potion,
                &mut selected_best,
                &mut resources,
            )?;

            let stage2_out = selected_best.as_ref().expect("Stage 2 produced no output");
            let stage2_clean = stage2_out.won
                && stage2_out.survives
                && stage2_out.death_save_use_count == 0;
            let stage2_eff_loss = stage2_out.strategic_hp_deficit.max(0);
            let stage2_paid = stage2_out.optional_potion_count;
            let stage2_meets_target = has_reached_acceptable_battle_hp_loss(
                &root_baseline_info,
                policy,
                stage2_out,
            ) && (!smart_mode || !root_baseline_info.has_paid_heal_or_growth_potion);
            let root_hp = root_baseline_info.initial_player_hp.max(1);
            let has_time_for_3 = profile.soft_time_budget_ms == 0
                || (started.elapsed().as_millis() as u64) < profile.soft_time_budget_ms;
            let need_stage3 = is_default_adaptive_profile
                && !stage2_meets_target
                && !cancel.load(Ordering::Relaxed)
                && has_time_for_3
                && *total_expanded < stage3_ceiling
                && (!stage2_out.won
                    || !stage2_clean
                    || stage2_eff_loss >= 18
                    || stage2_eff_loss * 4 >= root_hp
                    || (is_elite_or_boss && (stage2_eff_loss >= 12 || stage2_paid >= 2)));

            if need_stage3 {
                let stage3_mode = if !stage2_out.won {
                    EvalMode::Offense
                } else {
                    EvalMode::Balanced
                };
                run_adaptive_stage(
                    root,
                    content,
                    policy,
                    profile,
                    &root_baseline_info,
                    cert,
                    incumbent_table,
                    pool,
                    cancel,
                    progress_cb,
                    &started,
                    stage3_width,
                    stage3_ceiling,
                    stage3_mode,
                    6,
                    total_expanded,
                    total_transitions,
                    total_tt_pruned,
                    total_incumbent_pruned,
                    total_por_pruned,
                    &mut baseline_zero_potion,
                    &mut selected_best,
                    &mut resources,
                )?;
            }
        }

        return Ok(selected_best.expect("adaptive stages produced no output"));
    }

    let started = Instant::now();
    let has_power = PowerCardValuationRegistry::has_reachable_power(root);
    let specs = BeamWidthPortfolio::production_members(
        profile.beam_width,
        policy.beam_width_portfolio_widths.as_deref(),
        policy.beam_width_portfolio_plain_baseline_member,
        has_power,
        policy.offensive_refinement_portfolio,
        policy.bounded_offensive_refinement_portfolio,
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
            pool,
            cancel,
            cb,
            PassSearchOptions::default(),
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

        let dop = policy
            .max_degree_of_parallelism
            .clamp(1, crate::eval::weights::MAXIMUM_SEARCH_MAX_DEGREE_OF_PARALLELISM);
        let shared_pool: Option<rayon::ThreadPool> = if dop > 1 {
            Some(
                rayon::ThreadPoolBuilder::new()
                    .num_threads(dop)
                    .build()
                    .expect("failed to build search thread pool"),
            )
        } else {
            None
        };
        let pool_ref = shared_pool.as_ref();

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
                        pool_ref,
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
                    pool_ref,
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
                pool_ref,
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
                pool_ref,
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
                pool_ref,
                cancel,
                None,
                PassSearchOptions {
                    early_turn_scout_depth: policy.early_turn_exploration_depth,
                    ..Default::default()
                },
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
                            pool_ref,
                            cancel,
                            None,
                            PassSearchOptions::default(),
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
                pool_ref,
                cancel,
                None,
                PassSearchOptions {
                    boss_tempo_scout_turns: policy.boss_tempo_options.scout_turns.max(1),
                    ..Default::default()
                },
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
                        pool_ref,
                        cancel,
                        None,
                        PassSearchOptions::default(),
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
