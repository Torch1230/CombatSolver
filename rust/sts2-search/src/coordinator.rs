use crate::arena::{
    ChoiceArena, CompactAction, CompactActionKind, LocalStateSlab, MainStateArena, NodeArena,
    NodeId, PackedHandle, SearchNode, NODE_FLAG_HAS_EXTRA_END_TURN, NODE_FLAG_IS_TERMINAL,
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
    allows_brightest_flame, compute_beam_rank_score_with_centi, evaluate_fast, evaluate_full,
    select_final_plan, ActEndingBossPolicy, EvalMode, FastEvalSummary, FullEvalDetails,
    PotionUsePolicy, PotionValuationRegistry, PowerCardValuationRegistry, RareNodeEval,
    RelicCounterEvaluation, RootEvalBaseline, SolverPotionPolicy, SolverTheftPolicy,
    StoredFastEval, StoredNodeEval,
};
use crate::hash::StateHash;
use crate::novelty::{
    capture_novelty_facts, BfwsBoundedOpen, BfwsEscapeBudget, BfwsFact, BfwsPackedNovelty,
    NoveltyOpenNode, NoveltyPortfolioBudget, NoveltySearchOptions,
};
use crate::packer::{empty_scratch_state, hash_canonical_key, pack_state, unpack_state};
use crate::por::{
    card_equiv_key, classify_card_for_por, dedup_and_filter_potions_with_root,
    dedup_playable_hand_cards, enumerate_canonical_choices_with_scratch,
    has_any_playable_hand_card, has_any_usable_potion_with_root, por_should_skip,
    ChoiceEnumScratch, CombatOrderSensitivity, HandCardEquivKey, PorActionClass,
};
use crate::portfolio::{
    BeamWidthPortfolio, BeamWidthPortfolioBaseline, BeamWidthPortfolioGate,
    BeamWidthPortfolioRun, PortfolioSchedulingMode, PowerCommitmentPortfolioGate,
    SolverSearchProfile,
};
use crate::pruning::{
    BeamRetentionPolicy, ClosureCertificate, FrozenIncumbentBound, PrimaryIncumbentBucket,
    PrimaryIncumbentTable, PrimarySearchIncumbent, PruneScratch,
};
use crate::rollout::{
    encode_choice_probe_rank_score, evaluate_choice_probe_packed, evaluate_choice_probe_state,
    evaluate_turn_boundary_rollouts_into, ChoiceProbeCache, ChoiceProbeResult,
    ChoiceProbeWorkspace, RolloutResult, TurnRolloutCache,
};
use crate::tt::{ShardedParetoTT, TranspositionLabel, TT_SHARD_COUNT};
use crate::{InCombatSolveResponse, PlannedAction, SearchPolicy, SearchProgress, SearchStatistics};
use smallvec::SmallVec;
use std::borrow::Cow;
use std::ops::{Deref, DerefMut};
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, OnceLock};
use std::time::{Duration, Instant};
use sts2_kernel::state::{
    ActiveHookCache, BattleState, CardHandle, CreatureHandle, FieldMap, GrowthCounters,
    RootStaticContext, SemanticHistory, HOOK_DIRTY_ALL,
};
use sts2_kernel::{CombatEngine, ContentDispatcher, KernelError, StepOutcome};
use sts2_types::{PileType, PlayerTurnPhase, PotionId, RoomType, TargetType};

const CHOICE_RESOLVED_CAPACITY: usize = 160;
const CHOICE_TT_SIZE: usize = 512;
const CHOICE_TT_MASK: usize = CHOICE_TT_SIZE - 1;

#[derive(Clone, Copy, Debug)]
enum ActionSpec {
    TurnSetup,
    PlayCard {
        card_handle: CardHandle,
        target_handle: Option<CreatureHandle>,
        por_class: PorActionClass,
    },
    UsePotion {
        potion_slot: usize,
        potion_id: PotionId,
        target_handle: Option<CreatureHandle>,
        is_generated: bool,
        is_forced: bool,
        optional_paid_cost_delta: i32,
        optional_paid_count_delta: u16,
        root_potion_slot_bit: u16,
        forced_bit: Option<u32>,
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
    parent_optional_paid_potion_count: u16,
    parent_automatic_potion_count: u16,
    parent_filled_potions: u8,
    parent_used_potion_slot_mask: u16,
    parent_potion_cost: i32,
    parent_optional_paid_potion_cost: i32,
    parent_ambergris_count: u16,
    parent_forced_used_mask: u64,
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
    por_key: u64,
    has_extra_end_turn: bool,
    choices_offset: u32,
    choices_len: u8,
    state_slab_idx: u32,
    state_off: u32,
    state_len: u32,
    state_hash: StateHash,
    turn: i32,
    turn_completed: bool,
    action_count: u16,
    potion_count: u16,
    explicit_potion_count: u16,
    optional_paid_potion_count: u16,
    automatic_potion_count: u16,
    used_potion_slot_mask: u16,
    potion_strategic_cost: i32,
    optional_paid_potion_cost: i32,
    ambergris_count: u16,
    forced_used_mask: u64,
    forced_ambergris_count: u16,
    is_terminal: bool,
    base_score: f64,
    fast_eval: StoredFastEval,
    full_eval: Option<StoredNodeEval>,
    rare_eval: Option<RareNodeEval>,
    label: TranspositionLabel,
}

#[derive(Clone, Copy)]
struct ResolvedChoiceCandidate {
    choices_offset: u32,
    choices_len: u8,
    action: CompactAction,
    por_key: u64,
    has_extra_end_turn: bool,
    state_slab_idx: u32,
    state_off: u32,
    state_len: u32,
    state_hash: StateHash,
    turn: i32,
    turn_completed: bool,
    action_count: u16,
    potion_count: u16,
    explicit_potion_count: u16,
    optional_paid_potion_count: u16,
    automatic_potion_count: u16,
    used_potion_slot_mask: u16,
    potion_strategic_cost: i32,
    optional_paid_potion_cost: i32,
    ambergris_count: u16,
    forced_used_mask: u64,
    forced_ambergris_count: u16,
    is_terminal: bool,
    base_score: f64,
    fast_eval: StoredFastEval,
    full_eval: Option<StoredNodeEval>,
    rare_eval: Option<RareNodeEval>,
    label: TranspositionLabel,
    terminal_probe_score: f64,
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
    choice_enum_bufs: [Vec<SmallVec<[usize; 8]>>; 4],
    choice_enum_scratch: ChoiceEnumScratch,
    choice_slab: LocalStateSlab,
    choice_staging_choices: ChoiceArena,
    staged_choices: ChoiceArena,
    choice_tt_hashes: [StateHash; CHOICE_TT_SIZE],
    choice_tt_epochs: [u32; CHOICE_TT_SIZE],
    choice_tt_epoch: u32,
    marginal_scores: Vec<f64>,
    marginal_order: Vec<usize>,
    probed_sels: Vec<(SmallVec<[usize; 8]>, f64, u64, u64)>,
    pack_buf: Vec<u8>,
    action_buf: Vec<ActionTask>,
    slab: LocalStateSlab,
    candidates: Vec<WorkerCandidate>,
    transitions: u64,
    choice_replay_attempts: u64,
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
            choice_enum_bufs: [
                Vec::with_capacity(64),
                Vec::with_capacity(32),
                Vec::with_capacity(16),
                Vec::with_capacity(16),
            ],
            choice_enum_scratch: ChoiceEnumScratch::default(),
            choice_slab: LocalStateSlab::default(),
            choice_staging_choices: ChoiceArena::default(),
            staged_choices: ChoiceArena::default(),
            choice_tt_hashes: [StateHash::default(); CHOICE_TT_SIZE],
            choice_tt_epochs: [0u32; CHOICE_TT_SIZE],
            choice_tt_epoch: 0,
            marginal_scores: Vec::with_capacity(64),
            marginal_order: Vec::with_capacity(64),
            probed_sels: Vec::with_capacity(64),
            state: empty_scratch_state(static_ctx),
            pack_buf: Vec::with_capacity(4096),
            action_buf: Vec::with_capacity(16),
            slab: LocalStateSlab::default(),
            candidates: Vec::with_capacity(64),
            transitions: 0,
            choice_replay_attempts: 0,
            incumbent_pruned: 0,
            tt_probe_pruned: 0,
            por_pruned: 0,
        }
    }

    fn reset_for_wave(&mut self) {
        self.slab.clear();
        self.staged_choices.clear();
        self.choice_slab.clear();
        self.choice_staging_choices.clear();
        self.candidates.clear();
        self.choice_resolved.clear();
        self.transitions = 0;
        self.choice_replay_attempts = 0;
        self.incumbent_pruned = 0;
        self.tt_probe_pruned = 0;
        self.por_pruned = 0;
    }

    #[inline]
    fn begin_choice_tree(&mut self) {
        self.choice_slab.clear();
        self.choice_staging_choices.clear();
        self.choice_resolved.clear();
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

#[inline(always)]
fn pack_parent_slice(w_idx: usize, start: usize, len: usize) -> u64 {
    debug_assert!(w_idx < 256 && start < (1 << 28) && len < (1 << 28));
    ((w_idx as u64) << 56)
        | (((start as u64) & 0x0FFF_FFFF) << 28)
        | ((len as u64) & 0x0FFF_FFFF)
}

#[inline(always)]
fn unpack_parent_slice(raw: u64) -> (usize, usize, usize) {
    let w_idx = (raw >> 56) as usize;
    let start = ((raw >> 28) & 0x0FFF_FFFF) as usize;
    let len = (raw & 0x0FFF_FFFF) as usize;
    (w_idx, start, len)
}

struct PassResources {
    scratches: Vec<WorkerScratch>,
    parent_states: Vec<BattleState>,
    rollout_workspaces: Vec<crate::rollout::RolloutWorkspace>,
    node_arena: NodeArena,
    prune_scratch: PruneScratch,
    flat_tasks: Vec<(usize, ActionTask)>,
    step_arena: MainStateArena,
    next_step_arena: MainStateArena,
    turn_staging_arena: MainStateArena,
    tt: ShardedParetoTT,
    choice_probe_cache: ChoiceProbeCache,
    turn_rollout_cache: TurnRolloutCache,
    stand_pat_seen: hashbrown::HashSet<StateHash, ahash::RandomState>,
    sort_list: Vec<(usize, usize, (u32, u32, u32))>,
    parent_slices: Vec<AtomicU64>,
    tt_shard_buckets: Vec<Vec<(usize, StateHash, TranspositionLabel)>>,
    tt_shard_accepted: Vec<Vec<usize>>,
    tt_accepted_flags: Vec<bool>,
    tt_flat_refs: Vec<(usize, usize)>,
    rollout_results: Vec<Option<RolloutResult>>,
}

static SEARCH_THREAD_POOLS: [OnceLock<rayon::ThreadPool>; 17] = [const { OnceLock::new() }; 17];
static PASS_RESOURCE_POOL: parking_lot::Mutex<Vec<PassResources>> =
    parking_lot::Mutex::new(Vec::new());
static EMPTY_STATIC_CTX: OnceLock<Arc<RootStaticContext>> = OnceLock::new();

fn empty_static_ctx() -> &'static Arc<RootStaticContext> {
    EMPTY_STATIC_CTX.get_or_init(|| Arc::new(RootStaticContext::default()))
}

fn shared_search_pool(dop: usize) -> Option<&'static rayon::ThreadPool> {
    if dop <= 1 {
        return None;
    }
    let idx = dop.min(16);
    Some(SEARCH_THREAD_POOLS[idx].get_or_init(|| {
        rayon::ThreadPoolBuilder::new()
            .num_threads(idx)
            .build()
            .expect("failed to build search thread pool")
    }))
}

#[inline]
fn reset_scratch_battle_state(s: &mut BattleState, static_ctx: &Arc<RootStaticContext>) {
    s.static_ctx = Arc::clone(static_ctx);
    s.hook_cache.clear();
    s.deck_dirty.0 = true;
}

struct PooledPassResources(Option<PassResources>);

impl Deref for PooledPassResources {
    type Target = PassResources;

    #[inline(always)]
    fn deref(&self) -> &Self::Target {
        self.0.as_ref().expect("PassResources already dropped")
    }
}

impl DerefMut for PooledPassResources {
    #[inline(always)]
    fn deref_mut(&mut self) -> &mut Self::Target {
        self.0.as_mut().expect("PassResources already dropped")
    }
}

impl Drop for PooledPassResources {
    fn drop(&mut self) {
        if let Some(mut res) = self.0.take() {
            res.reset_for_new_solve(empty_static_ctx());
            let mut pool = PASS_RESOURCE_POOL.lock();
            if pool.len() < 2 {
                pool.push(res);
            }
        }
    }
}

impl PassResources {
    fn acquire(
        static_ctx: &Arc<RootStaticContext>,
        worker_count: usize,
        transposition_entry_limit: usize,
    ) -> PooledPassResources {
        let pooled = {
            let mut pool = PASS_RESOURCE_POOL.lock();
            pool.pop()
        };
        let mut res = match pooled {
            Some(mut r) => {
                while r.scratches.len() < worker_count {
                    r.scratches.push(WorkerScratch::new(static_ctx.clone()));
                }
                while r.parent_states.len() < worker_count {
                    r.parent_states.push(empty_scratch_state(static_ctx.clone()));
                }
                while r.rollout_workspaces.len() < worker_count {
                    r.rollout_workspaces
                        .push(crate::rollout::RolloutWorkspace::new(static_ctx.clone()));
                }
                r.tt.set_entry_limit(transposition_entry_limit.max(65_536));
                r.choice_probe_cache.clear();
                r.turn_rollout_cache.clear();
                r.reset_for_new_solve(static_ctx);
                r
            }
            None => Self::new(static_ctx, worker_count, transposition_entry_limit),
        };
        let _ = &mut res;
        PooledPassResources(Some(res))
    }

    fn reset_for_new_solve(&mut self, static_ctx: &Arc<RootStaticContext>) {
        self.reset_for_pass();
        for s in &mut self.scratches {
            s.reset_for_wave();
            reset_scratch_battle_state(&mut s.state, static_ctx);
            reset_scratch_battle_state(&mut s.pre_end_turn_state, static_ctx);
            for ps in &mut s.choice_paused_states {
                reset_scratch_battle_state(ps, static_ctx);
            }
            reset_scratch_battle_state(&mut s.choice_probe_ws.probe_state, static_ctx);
        }
        for ps in &mut self.parent_states {
            reset_scratch_battle_state(ps, static_ctx);
        }
        for rw in &mut self.rollout_workspaces {
            reset_scratch_battle_state(&mut rw.base_state, static_ctx);
            reset_scratch_battle_state(&mut rw.pilot_state, static_ctx);
        }
    }

    fn new(
        static_ctx: &Arc<RootStaticContext>,
        worker_count: usize,
        transposition_entry_limit: usize,
    ) -> Self {
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
            node_arena: NodeArena::with_capacity(32_768, 32_768),
            prune_scratch: PruneScratch::default(),
            flat_tasks: Vec::with_capacity(128),
            step_arena: MainStateArena::default(),
            next_step_arena: MainStateArena::default(),
            turn_staging_arena: MainStateArena::default(),
            tt: ShardedParetoTT::new(transposition_entry_limit.max(65_536)),
            choice_probe_cache: ChoiceProbeCache::new(),
            turn_rollout_cache: TurnRolloutCache::new(),
            stand_pat_seen: hashbrown::HashSet::with_hasher(ahash::RandomState::with_seeds(
                0x3141_5926_5358_9793,
                0x2718_2818_2845_9045,
                0x1618_0339_8874_9895,
                0x1414_2135_6237_3095,
            )),
            sort_list: Vec::with_capacity(2048),
            parent_slices: (0..128).map(|_| AtomicU64::new(0)).collect(),
            tt_shard_buckets: (0..TT_SHARD_COUNT).map(|_| Vec::with_capacity(8)).collect(),
            tt_shard_accepted: (0..TT_SHARD_COUNT).map(|_| Vec::with_capacity(8)).collect(),
            tt_accepted_flags: Vec::with_capacity(512),
            tt_flat_refs: Vec::with_capacity(512),
            rollout_results: Vec::with_capacity(128),
        }
    }

    fn reset_for_pass(&mut self) {
        self.node_arena.clear();
        self.flat_tasks.clear();
        self.step_arena.clear();
        self.next_step_arena.clear();
        self.turn_staging_arena.clear();
        self.tt.reset_generation();
        self.tt.branches_pruned = 0;
        self.stand_pat_seen.clear();
        self.sort_list.clear();
        for b in &mut self.tt_shard_buckets {
            b.clear();
        }
        for a in &mut self.tt_shard_accepted {
            a.clear();
        }
        self.tt_accepted_flags.clear();
        self.tt_flat_refs.clear();
        self.rollout_results.clear();
    }
}

#[inline]
fn clone_cow_str_into(src: &Cow<'static, str>, dst: &mut Cow<'static, str>) {
    match src {
        Cow::Borrowed(b) => {
            *dst = Cow::Borrowed(b);
        }
        Cow::Owned(ss) => match dst {
            Cow::Owned(ds) => {
                if ds != ss {
                    ds.clone_from(ss);
                }
            }
            Cow::Borrowed(_) => *dst = src.clone(),
        },
    }
}

#[inline]
fn clone_opt_cow_str_into(
    src: &Option<Cow<'static, str>>,
    dst: &mut Option<Cow<'static, str>>,
) {
    match (dst, src) {
        (Some(dc), Some(sc)) => clone_cow_str_into(sc, dc),
        (dst_slot, Some(sc)) => *dst_slot = Some(sc.clone()),
        (dst_slot, None) => {
            if dst_slot.is_some() {
                *dst_slot = None;
            }
        }
    }
}

#[inline]
fn clone_small_cow_strings_into(
    src: &[Cow<'static, str>],
    dst: &mut Vec<Cow<'static, str>>,
) {
    let keep = dst.len().min(src.len());
    for i in 0..keep {
        clone_cow_str_into(&src[i], &mut dst[i]);
    }
    if src.len() > keep {
        dst.reserve(src.len() - keep);
        for s in &src[keep..] {
            dst.push(s.clone());
        }
    } else if dst.len() > keep {
        dst.truncate(keep);
    }
}

#[inline]
fn clone_field_map_into(src: &FieldMap, dst: &mut FieldMap) {
    src.clone_into_map(dst);
}

#[inline]
fn clone_semantic_history_into(src: &SemanticHistory, dst: &mut SemanticHistory) {
    if src.exhausts.is_empty() {
        if !dst.exhausts.is_empty() {
            dst.exhausts.clear();
        }
    } else {
        dst.exhausts.clone_from(&src.exhausts);
    }
    if src.card_block_gains.is_empty() {
        if !dst.card_block_gains.is_empty() {
            dst.card_block_gains.clear();
        }
    } else {
        dst.card_block_gains.clone_from(&src.card_block_gains);
    }
    if src.orb_channels.is_empty() {
        if !dst.orb_channels.is_empty() {
            dst.orb_channels.clear();
        }
    } else {
        dst.orb_channels.clone_from(&src.orb_channels);
    }
    if src.energy_spent.is_empty() {
        if !dst.energy_spent.is_empty() {
            dst.energy_spent.clear();
        }
    } else {
        dst.energy_spent.clone_from(&src.energy_spent);
    }
    if src.card_drawn.is_empty() {
        if !dst.card_drawn.is_empty() {
            dst.card_drawn.clear();
        }
    } else {
        dst.card_drawn.clone_from(&src.card_drawn);
    }

    if src.actor_events.is_empty() {
        if !dst.actor_events.is_empty() {
            dst.actor_events.clear();
        }
    } else {
        let keep = dst.actor_events.len().min(src.actor_events.len());
        for i in 0..keep {
            if dst.actor_events[i].0 != src.actor_events[i].0 {
                dst.actor_events[i].0 = src.actor_events[i].0.clone();
            }
            if dst.actor_events[i].1 != src.actor_events[i].1 {
                dst.actor_events[i].1 = src.actor_events[i].1;
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
    }

    dst.growth_rewards = src.growth_rewards;
    dst.brightest_flame_max_hp_spent = src.brightest_flame_max_hp_spent;
    dst.prior_player_unblocked_hits = src.prior_player_unblocked_hits;
    dst.prior_player_damaged_turn = src.prior_player_damaged_turn;
    dst.prior_exhaust_count = src.prior_exhaust_count;
}

#[inline]
fn clone_hook_cache_into(src: &ActiveHookCache, dst: &mut ActiveHookCache) {
    dst.dirty_mask = src.dirty_mask;
    if src.dirty_mask == HOOK_DIRTY_ALL {
        return;
    }
    dst.masks = src.masks;
    dst.in_combat_mask = src.in_combat_mask;
    dst.in_combat_overflow = src.in_combat_overflow;
    dst.combat_has_player = src.combat_has_player;
    dst.cached_tables = src.cached_tables;
    dst.listener_dominion = src.listener_dominion;
    dst.listener_pre_player.clone_from(&src.listener_pre_player);
    dst.listener_post_player.clone_from(&src.listener_post_player);
    dst.listener_relics.clone_from(&src.listener_relics);
    dst.listener_potions.clone_from(&src.listener_potions);
    dst.listener_orbs.clone_from(&src.listener_orbs);
    dst.listener_hand.clone_from(&src.listener_hand);
    dst.listener_draw.clone_from(&src.listener_draw);
    dst.listener_discard.clone_from(&src.listener_discard);
    dst.listener_exhaust.clone_from(&src.listener_exhaust);
    dst.listener_play.clone_from(&src.listener_play);
    if dst.listener_deck != src.listener_deck {
        dst.listener_deck.clone_from(&src.listener_deck);
    }
    dst.listener_run_models.clone_from(&src.listener_run_models);
    dst.flat_masks_w0.clone_from(&src.flat_masks_w0);
    dst.flat_masks_w1.clone_from(&src.flat_masks_w1);
    dst.flat_masks_w2.clone_from(&src.flat_masks_w2);
    dst.flat_listeners.clone_from(&src.flat_listeners);
}

#[inline]
pub(crate) fn clone_battle_state_into(src: &BattleState, dst: &mut BattleState) {
    let same_root = Arc::ptr_eq(&dst.static_ctx, &src.static_ctx);
    let prefix_len = src.static_ctx.root_deck_prefix_len;
    let skip_immutable_deck = same_root
        && !src.deck_dirty.0
        && !dst.deck_dirty.0
        && prefix_len > 0
        && dst.cards.len() >= prefix_len
        && src.cards.len() >= prefix_len;
    if !same_root {
        dst.static_ctx = Arc::clone(&src.static_ctx);
    }
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
    if sp.exhaust.is_empty() {
        if !dp.exhaust.is_empty() {
            dp.exhaust.clear();
        }
    } else {
        dp.exhaust.clone_from(&sp.exhaust);
    }
    if sp.play.is_empty() {
        if !dp.play.is_empty() {
            dp.play.clear();
        }
    } else {
        dp.play.clone_from(&sp.play);
    }
    if skip_immutable_deck && dp.deck.len() == sp.deck.len() {
        #[cfg(debug_assertions)]
        debug_assert_eq!(dp.deck.as_slice(), sp.deck.as_slice());
    } else {
        dp.deck.clone_from(&sp.deck);
    }
    dp.potion_slots.clone_from(&sp.potion_slots);
    dp.combat_generated_potion_mask = sp.combat_generated_potion_mask;
    dp.orb_capacity = sp.orb_capacity;
    if sp.orbs.is_empty() {
        if !dp.orbs.is_empty() {
            dp.orbs.clear();
        }
    } else {
        dp.orbs.clone_from(&sp.orbs);
    }
    if sp.arrows.is_empty() {
        if !dp.arrows.is_empty() {
            dp.arrows.clear();
        }
    } else {
        dp.arrows.clone_from(&sp.arrows);
    }
    dp.card_or_potion_effect_depth = sp.card_or_potion_effect_depth;
    let relic_keep = dp.relics.len().min(sp.relics.len());
    for (dr, sr) in dp.relics[..relic_keep].iter_mut().zip(sp.relics[..relic_keep].iter()) {
        dr.id = sr.id;
        dr.is_wax = sr.is_wax;
        dr.is_melted = sr.is_melted;
        dr.stack_count = sr.stack_count;
        dr.floor_added_to_deck = sr.floor_added_to_deck;
        clone_field_map_into(&sr.private_fields, &mut dr.private_fields);
    }
    if sp.relics.len() > relic_keep {
        dp.relics.reserve(sp.relics.len() - relic_keep);
        for sr in &sp.relics[relic_keep..] {
            dp.relics.push(sr.clone());
        }
    } else if dp.relics.len() > relic_keep {
        dp.relics.truncate(sp.relics.len());
    }

    if skip_immutable_deck {
        #[cfg(debug_assertions)]
        debug_assert_eq!(&dst.cards[..prefix_len], &src.cards[..prefix_len]);
        dst.cards.truncate(prefix_len);
        dst.cards.extend_from_slice(&src.cards[prefix_len..]);
    } else {
        dst.cards.clear();
        dst.cards.extend_from_slice(&src.cards);
    }

    if src.card_extensions.is_empty() {
        dst.card_extensions.clear();
    } else {
        let ext_keep = dst.card_extensions.len().min(src.card_extensions.len());
        for (de, se) in dst.card_extensions[..ext_keep]
            .iter_mut()
            .zip(src.card_extensions[..ext_keep].iter())
        {
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
        if src.card_extensions.len() > ext_keep {
            dst.card_extensions
                .reserve(src.card_extensions.len() - ext_keep);
            for se in &src.card_extensions[ext_keep..] {
                dst.card_extensions.push(se.clone());
            }
        } else if dst.card_extensions.len() > ext_keep {
            dst.card_extensions.truncate(src.card_extensions.len());
        }
    }

    let cr_keep = dst.creatures.len().min(src.creatures.len());
    for (dc, sc) in dst.creatures[..cr_keep]
        .iter_mut()
        .zip(src.creatures[..cr_keep].iter())
    {
        dc.handle = sc.handle;
        dc.combat_id = sc.combat_id;
        dc.side = sc.side;
        clone_opt_cow_str_into(&sc.slot_name, &mut dc.slot_name);
        dc.is_player = sc.is_player;
        dc.pet_owner_player_index = sc.pet_owner_player_index;
        dc.monster_id = sc.monster_id;
        dc.current_hp = sc.current_hp;
        dc.max_hp = sc.max_hp;
        dc.block = sc.block;
        dc.cumulative_hp_lost = sc.cumulative_hp_lost;
        dc.is_detached = sc.is_detached;
        dc.has_minion_power = sc.has_minion_power;
        dc.powers.clone_from(&sc.powers);
        match (&mut dc.monster, &sc.monster) {
            (Some(dm), Some(sm)) => {
                dm.monster_id = sm.monster_id;
                dm.rng = sm.rng;
                dm.is_performing_move = sm.is_performing_move;
                dm.spawned_this_turn = sm.spawned_this_turn;
                clone_cow_str_into(&sm.current_state_id, &mut dm.current_state_id);
                clone_opt_cow_str_into(&sm.next_move_id, &mut dm.next_move_id);
                dm.next_move_can_transition_away = sm.next_move_can_transition_away;
                dm.performed_first_move = sm.performed_first_move;
                clone_small_cow_strings_into(&sm.state_log_ids, &mut dm.state_log_ids);
                clone_opt_cow_str_into(
                    &sm.transient_move_follow_up_state_id,
                    &mut dm.transient_move_follow_up_state_id,
                );
                clone_field_map_into(&sm.setup_fields, &mut dm.setup_fields);
                clone_field_map_into(&sm.private_fields, &mut dm.private_fields);
            }
            _ => dc.monster.clone_from(&sc.monster),
        }
    }
    if src.creatures.len() > cr_keep {
        dst.creatures.reserve(src.creatures.len() - cr_keep);
        for sc in &src.creatures[cr_keep..] {
            dst.creatures.push(sc.clone());
        }
    } else if dst.creatures.len() > cr_keep {
        dst.creatures.truncate(src.creatures.len());
    }

    if src.power_extensions.is_empty() {
        dst.power_extensions.clear();
    } else {
        let pe_keep = dst.power_extensions.len().min(src.power_extensions.len());
        for (dp, sp) in dst.power_extensions[..pe_keep]
            .iter_mut()
            .zip(src.power_extensions[..pe_keep].iter())
        {
            dp.attack_command_id = sp.attack_command_id;
            dp.amount_when_attack_started = sp.amount_when_attack_started;
            dp.impaled_arrows.clone_from(&sp.impaled_arrows);
            dp.hit_counter = sp.hit_counter;
            clone_field_map_into(&sp.private_fields, &mut dp.private_fields);
        }
        if src.power_extensions.len() > pe_keep {
            dst.power_extensions
                .reserve(src.power_extensions.len() - pe_keep);
            for sp in &src.power_extensions[pe_keep..] {
                dst.power_extensions.push(sp.clone());
            }
        } else if dst.power_extensions.len() > pe_keep {
            dst.power_extensions.truncate(src.power_extensions.len());
        }
    }

    dst.ally_handles.clone_from(&src.ally_handles);
    dst.enemy_handles.clone_from(&src.enemy_handles);
    if src.escaped_handles.is_empty() {
        if !dst.escaped_handles.is_empty() {
            dst.escaped_handles.clear();
        }
    } else {
        dst.escaped_handles.clone_from(&src.escaped_handles);
    }
    if src.removed_handles.is_empty() {
        if !dst.removed_handles.is_empty() {
            dst.removed_handles.clear();
        }
    } else {
        dst.removed_handles.clone_from(&src.removed_handles);
    }
    if src.spawned_enemy_handles.is_empty() {
        if !dst.spawned_enemy_handles.is_empty() {
            dst.spawned_enemy_handles.clear();
        }
    } else {
        dst.spawned_enemy_handles
            .clone_from(&src.spawned_enemy_handles);
    }
    if src.pet_handles.is_empty() {
        if !dst.pet_handles.is_empty() {
            dst.pet_handles.clear();
        }
    } else {
        dst.pet_handles.clone_from(&src.pet_handles);
    }
    if src.damage_history.is_empty() {
        if !dst.damage_history.is_empty() {
            dst.damage_history.clear();
        }
    } else {
        dst.damage_history.clone_from(&src.damage_history);
    }
    clone_semantic_history_into(&src.semantic_history, &mut dst.semantic_history);
    if src.pending.is_none() {
        if dst.pending.is_some() {
            dst.pending = None;
        }
    } else {
        dst.pending.clone_from(&src.pending);
    }
    dst.vakuu_auto_select_depth = src.vakuu_auto_select_depth;
    clone_hook_cache_into(&src.hook_cache, &mut dst.hook_cache);
    dst.deck_dirty = src.deck_dirty;
}

struct BeamRunOutput {
    best_node: Option<NodeId>,
    best_turn: i32,
    nodes_in_arena: usize,
    root_baseline: RootEvalBaseline,
    won: bool,
    survives: bool,
    death_save_use_count: i32,
    player_hp: i32,
    end_player_max_hp: i32,
    all_forced_uses_satisfied: bool,
    strategic_hp_deficit: i32,
    net_persistent_hp_delta_centi: i32,
    net_potion_opportunity_cost_centi: i32,
    strategy_goal_hp_credit: i32,
    strategy_goal_count: i32,
    growth_rewards: GrowthCounters,
    relic_eval: RelicCounterEvaluation,
    recovered_hp: i32,
    hp_lost: i32,
    potion_count: usize,
    explicit_potion_count: usize,
    optional_potion_count: usize,
    optional_paid_potion_count: usize,
    optional_potion_strategic_cost: i32,
    optional_ambergris_count: i32,
    used_potion_slot_mask: u16,
    alchemize_count: u8,
    empty_potion_slots: u8,
    outstanding_stolen: i32,
    forfeits_gold: bool,
    has_completed_turn_ongoing: bool,
    completed_ongoing_turn: i32,
    score: i64,
    actions: Vec<PlannedAction>,
    expanded_nodes: usize,
    transition_count: u64,
    choice_replay_attempts: u64,
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
    root_baseline: &RootEvalBaseline,
    allow_paid_optional_potions: bool,
    tasks_out: &mut Vec<ActionTask>,
    por_pruned: &mut u64,
) {
    if !state.is_in_progress || state.won || state.pending.is_some() {
        return;
    }

    let parent_filled_potions = state
        .player
        .potion_slots
        .iter()
        .filter(|s| s.is_some())
        .count()
        .min(u8::MAX as usize) as u8;

    let prev_por = node.por_class();
    let prev_turn = node.action_turn();
    let curr_turn = state.player.turn_number;

    let mut action_ord = 0u32;

    let make_task = |action_ord: u32,
                     spec: ActionSpec,
                     has_la: bool,
                     la_score: Option<f64>| ActionTask {
        parent_rank,
        parent_id,
        parent_turn: node.turn as i32,
        parent_action_count: node.action_count,
        parent_potion_count: node.potion_count as u16,
        parent_explicit_potion_count: node.explicit_potion_count as u16,
        parent_optional_paid_potion_count: node.optional_paid_potion_count as u16,
        parent_automatic_potion_count: node.automatic_potion_count as u16,
        parent_filled_potions,
        parent_used_potion_slot_mask: node.used_potion_slot_mask,
        parent_potion_cost: node.potion_strategic_cost as i32,
        parent_optional_paid_potion_cost: node.optional_paid_potion_cost as i32,
        parent_ambergris_count: node.ambergris_count as u16,
        parent_forced_used_mask: node.forced_used_mask,
        parent_forced_ambergris_count: node.forced_ambergris_count as u16,
        parent_has_choice_lookahead: has_la,
        parent_choice_lookahead_score: la_score,
        action_ord,
        spec,
    };

    if state.player.phase == PlayerTurnPhase::Play {
        let hand_cards = dedup_playable_hand_cards(
            state,
            content,
            policy.accelerations.enable_hand_potion_dedup,
        );
        let max_branches = if (32..64).contains(&profile.max_card_branches_per_node) {
            64
        } else {
            profile.max_card_branches_per_node
        };
        let mut play_candidates: SmallVec<[(usize, CardHandle, Option<CreatureHandle>, PorActionClass); 64]> =
            SmallVec::new();
        let mut max_target_rank = 0usize;

        for ch in hand_cards {
            let Some((card_id, meta)) = state
                .card(ch)
                .and_then(|c| state.fast_card_meta(content, c.id).ok().map(|m| (c.id, m)))
            else {
                continue;
            };
            let eff_target = state
                .fast_card_resolved_target_type(content, ch, card_id, meta)
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
                tasks_out.push(make_task(
                    action_ord,
                    ActionSpec::PlayCard {
                        card_handle,
                        target_handle,
                        por_class,
                    },
                    parent_has_choice_lookahead,
                    parent_choice_lookahead_score,
                ));
                action_ord += 1;
            }
        } else {
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
                tasks_out.push(make_task(
                    action_ord,
                    ActionSpec::PlayCard {
                        card_handle,
                        target_handle,
                        por_class,
                    },
                    parent_has_choice_lookahead,
                    parent_choice_lookahead_score,
                ));
                action_ord += 1;
            }
        }

        if policy.allows_any_potion() {
            let potions = dedup_and_filter_potions_with_root(
                state,
                sensitivity,
                policy,
                Some(root_baseline),
                policy.accelerations.enable_hand_potion_dedup,
                allow_paid_optional_potions,
                root_baseline.renewable_potion_shaped_rock,
            );
            for (slot, pid, cls) in potions {
                let Ok(meta) = content.potion_meta(pid) else {
                    continue;
                };
                if !policy.allow_random_cards && meta.uses_rng {
                    continue;
                }
                let targets = target_candidates_for(state, meta.target_type);
                for target_handle in targets {
                    tasks_out.push(make_task(
                        action_ord,
                        ActionSpec::UsePotion {
                            potion_slot: slot,
                            potion_id: pid,
                            target_handle,
                            is_generated: cls.is_generated,
                            is_forced: cls.is_forced,
                            optional_paid_cost_delta: cls.optional_paid_cost_delta,
                            optional_paid_count_delta: cls.optional_paid_count_delta,
                            root_potion_slot_bit: cls.root_potion_slot_bit,
                            forced_bit: cls.forced_bit,
                        },
                        parent_has_choice_lookahead,
                        parent_choice_lookahead_score,
                    ));
                    action_ord += 1;
                }
            }
        }
    }

    // EndTurn action
    tasks_out.push(make_task(action_ord, ActionSpec::EndTurn, false, None));
}

struct StagedCandidateFields {
    has_extra_end_turn: bool,
    state_slab_idx: u32,
    state_off: u32,
    state_len: u32,
    state_hash: StateHash,
    turn: i32,
    turn_completed: bool,
    action_count: u16,
    potion_count: u16,
    explicit_potion_count: u16,
    optional_paid_potion_count: u16,
    automatic_potion_count: u16,
    used_potion_slot_mask: u16,
    potion_strategic_cost: i32,
    optional_paid_potion_cost: i32,
    ambergris_count: u16,
    forced_used_mask: u64,
    forced_ambergris_count: u16,
    is_terminal: bool,
    fast_eval: FastEvalSummary,
    full_eval: Option<FullEvalDetails>,
    label: TranspositionLabel,
}

#[allow(clippy::too_many_arguments)]
fn prepare_staged_candidate(
    scratch: &mut WorkerScratch,
    task: &ActionTask,
    has_extra_end_turn: bool,
    check_local_choice_tt: bool,
    inherit_lookahead: bool,
    use_choice_slab: bool,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    frozen_incumbent: &FrozenIncumbentBound,
    tt: &ShardedParetoTT,
    sensitivity: &CombatOrderSensitivity,
    allow_paid_optional_potions: bool,
    eval_mode: EvalMode,
) -> Option<StagedCandidateFields> {
    if !allows_brightest_flame(
        policy.brightest_flame_max_hp_loss_limit,
        root_baseline.initial_brightest_flame_max_hp_spent,
        root_baseline.brightest_flame_spent(&scratch.state),
    ) {
        return None;
    }
    let mut final_extra_end = has_extra_end_turn;
    if !final_extra_end
        && policy.accelerations.enable_forced_end_turn_compression
        && !matches!(task.spec, ActionSpec::EndTurn | ActionSpec::TurnSetup)
        && scratch.state.is_in_progress
        && !scratch.state.won
        && scratch.state.pending.is_none()
        && scratch.state.player.phase == PlayerTurnPhase::Play
    {
        let has_card = has_any_playable_hand_card(&mut scratch.state, content);
        let has_potion = !has_card
            && policy.allows_any_potion()
            && has_any_usable_potion_with_root(
                &scratch.state,
                sensitivity,
                policy,
                Some(root_baseline),
                allow_paid_optional_potions,
                root_baseline.renewable_potion_shaped_rock,
            );
        if !has_card && !has_potion {
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
                    final_extra_end = true;
                }
                Ok(StepOutcome::NeedsChoice(_)) | Err(_) => {
                    std::mem::swap(&mut scratch.state, &mut scratch.pre_end_turn_state);
                }
            }
        }
    }

    let fast_eval = evaluate_fast(&scratch.state, content, root_baseline, None);

    let (
        explicit_delta,
        optional_paid_count_delta,
        optional_paid_cost_delta,
        slot_cost_raw,
        ambergris_delta,
        slot_mask_bit,
        forced_mask_bit,
        forced_amb_delta,
    ) = match task.spec {
        ActionSpec::UsePotion {
            potion_id,
            is_generated,
            is_forced,
            optional_paid_cost_delta,
            optional_paid_count_delta,
            root_potion_slot_bit,
            forced_bit,
            ..
        } => {
            if is_generated {
                (1u16, 0u16, 0i32, 0i32, 0u16, 0u16, 0u64, 0u16)
            } else {
                let cost = if is_forced {
                    PotionValuationRegistry::strategic_hp_cost_for_root(
                        potion_id,
                        root_baseline.renewable_potion_shaped_rock,
                        root_baseline.initial_player_max_hp,
                        &policy.potion_hp_threshold_overrides,
                    )
                } else {
                    optional_paid_cost_delta
                };
                let amb = u16::from(potion_id == PotionId::Ambergris);
                let f_bit = forced_bit.filter(|&b| b < 64).map_or(0u64, |b| 1u64 << b);
                let newly_forced = f_bit != 0 && (task.parent_forced_used_mask & f_bit) == 0;
                (
                    1u16,
                    optional_paid_count_delta,
                    optional_paid_cost_delta,
                    cost,
                    amb,
                    root_potion_slot_bit,
                    f_bit,
                    if newly_forced { amb } else { 0 },
                )
            }
        }
        _ => (0u16, 0u16, 0i32, 0i32, 0u16, 0u16, 0u64, 0u16),
    };

    let after_filled = scratch
        .state
        .player
        .potion_slots
        .iter()
        .filter(|s| s.is_some())
        .count() as i32;
    let total_consumed = (i32::from(task.parent_filled_potions) - after_filled).max(0);
    let auto_delta = (total_consumed - i32::from(explicit_delta)).max(0) as u16;

    let potion_count = task.parent_potion_count + explicit_delta + auto_delta;
    let explicit_potion_count = task.parent_explicit_potion_count + explicit_delta;
    let optional_paid_potion_count =
        task.parent_optional_paid_potion_count + optional_paid_count_delta;
    let automatic_potion_count = task.parent_automatic_potion_count + auto_delta;
    let used_potion_slot_mask = task.parent_used_potion_slot_mask | slot_mask_bit;
    let potion_strategic_cost = task.parent_potion_cost + slot_cost_raw;
    let optional_paid_potion_cost =
        task.parent_optional_paid_potion_cost + optional_paid_cost_delta;
    let ambergris_count = task.parent_ambergris_count + ambergris_delta;
    let forced_used_mask = task.parent_forced_used_mask | forced_mask_bit;
    let forced_ambergris_count = task.parent_forced_ambergris_count + forced_amb_delta;
    let action_count = task.parent_action_count + 1 + u16::from(final_extra_end);
    let turn = scratch.state.player.turn_number;
    let turn_completed = matches!(task.spec, ActionSpec::EndTurn)
        || final_extra_end
        || turn > task.parent_turn;
    let is_terminal = !scratch.state.is_in_progress
        || fast_eval.won
        || fast_eval.player_dead
        || turn - root_baseline.start_turn_number >= policy.max_turns;

    let net_potion_cost_centi = PotionUsePolicy::compute_net_potion_opportunity_cost_centi(
        optional_paid_potion_cost,
        optional_paid_potion_count as i32,
        fast_eval.alchemize_count as i32,
        fast_eval.empty_potion_slots as i32,
        root_baseline,
    );

    if !is_terminal
        && policy.accelerations.enable_closure_pruning
        && frozen_incumbent.is_pruned(
            cert,
            root_baseline,
            fast_eval.player_hp,
            fast_eval.player_max_hp,
            fast_eval.death_save_hp_restored,
            fast_eval.death_save_use_count,
            fast_eval.won,
            fast_eval.player_dead,
            turn,
            optional_paid_potion_count as usize,
            explicit_potion_count as usize,
            net_potion_cost_centi,
            policy.theft_policy == SolverTheftPolicy::PreserveResources,
        )
    {
        scratch.incumbent_pruned += 1;
        return None;
    }

    let state_hash = hash_canonical_key(&scratch.state);

    if check_local_choice_tt && !scratch.try_insert_local_choice_tt(state_hash) {
        return None;
    }
    let full_eval = if !is_terminal {
        let mut full = evaluate_full(&scratch.state, content, &fast_eval, root_baseline);
        full.beam_rank_score = compute_beam_rank_score_with_centi(
            &fast_eval,
            &full,
            root_baseline,
            profile.base_score_only,
            profile.boss_tempo_hp_pricing,
            profile.beam_weight_perturbation,
            eval_mode,
            net_potion_cost_centi,
            action_count,
        );
        if inherit_lookahead && !turn_completed && task.parent_has_choice_lookahead {
            full.has_choice_lookahead = true;
            full.choice_lookahead_score = task.parent_choice_lookahead_score;
        }
        Some(full)
    } else {
        None
    };

    let tt_score = full_eval
        .as_ref()
        .map_or(fast_eval.base_score, |f| f.beam_rank_score);
    let label = TranspositionLabel::new(
        forced_used_mask,
        fast_eval.death_save_use_count,
        potion_count,
        optional_paid_potion_cost,
        0,
        fast_eval.cumulative_hp_lost,
        action_count,
        tt_score,
    );

    if !check_local_choice_tt && !is_terminal && tt.probe_dominated(state_hash, &label) {
        scratch.tt_probe_pruned += 1;
        return None;
    }

    let (state_slab_idx, state_off, state_len) = if is_terminal {
        (0, 0, 0)
    } else {
        pack_state(&scratch.state, &mut scratch.pack_buf);
        if use_choice_slab {
            scratch.choice_slab.alloc_slice(&scratch.pack_buf)
        } else {
            scratch.slab.alloc_slice(&scratch.pack_buf)
        }
    };

    Some(StagedCandidateFields {
        has_extra_end_turn: final_extra_end,
        state_slab_idx,
        state_off,
        state_len,
        state_hash,
        turn,
        turn_completed,
        action_count,
        potion_count,
        explicit_potion_count,
        optional_paid_potion_count,
        automatic_potion_count,
        used_potion_slot_mask,
        potion_strategic_cost,
        optional_paid_potion_cost,
        ambergris_count,
        forced_used_mask,
        forced_ambergris_count,
        is_terminal,
        fast_eval,
        full_eval,
        label,
    })
}

#[inline]
fn rare_eval_opt(f: &FastEvalSummary) -> Option<RareNodeEval> {
    if f.growth_rewards.is_zero() && f.relic_eval.target_mask == 0 {
        None
    } else {
        Some(RareNodeEval {
            growth_rewards: f.growth_rewards,
            relic_eval: f.relic_eval,
        })
    }
}

#[inline]
fn choice_probe_limits(
    profile: &SolverSearchProfile,
    first_pile: PileType,
    is_end_turn: bool,
    is_turn_setup: bool,
) -> (bool, usize) {
    let max_branches = if is_turn_setup {
        profile.beam_width.max(64).min(CHOICE_RESOLVED_CAPACITY)
    } else if first_pile == PileType::Hand {
        profile.max_hand_choice_branches_per_action
    } else {
        profile.max_pile_choice_branches_per_action
    }
    .max(1);
    let is_delayed_or_end_turn = is_end_turn
        || is_turn_setup
        || matches!(
            first_pile,
            PileType::Draw | PileType::Discard | PileType::Exhaust
        );
    (is_delayed_or_end_turn, max_branches)
}

#[allow(clippy::too_many_arguments)]
fn finalize_candidate(
    scratch: &mut WorkerScratch,
    task: &ActionTask,
    action: CompactAction,
    por_key: u64,
    has_extra_end_turn: bool,
    choices: &[SmallVec<[usize; 8]>],
    choice_ord: u32,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    frozen_incumbent: &FrozenIncumbentBound,
    tt: &ShardedParetoTT,
    sensitivity: &CombatOrderSensitivity,
    allow_paid_optional_potions: bool,
    eval_mode: EvalMode,
) {
    let Some(s) = prepare_staged_candidate(
        scratch,
        task,
        has_extra_end_turn,
        false,
        choices.is_empty(),
        false,
        content,
        root_baseline,
        policy,
        profile,
        cert,
        frozen_incumbent,
        tt,
        sensitivity,
        allow_paid_optional_potions,
        eval_mode,
    ) else {
        return;
    };

    let (choices_offset, choices_len) = scratch.staged_choices.push_choices(choices);
    let base_score = s.fast_eval.base_score;
    let rare_eval = rare_eval_opt(&s.fast_eval);
    let stored_fast = StoredFastEval::from(&s.fast_eval);
    let stored_full = s.full_eval.map(|f| f.to_stored());
    scratch.candidates.push(WorkerCandidate {
        sort_key: (task.parent_rank, task.action_ord, choice_ord),
        parent_id: task.parent_id,
        action,
        por_key,
        has_extra_end_turn: s.has_extra_end_turn,
        choices_offset,
        choices_len,
        state_slab_idx: s.state_slab_idx,
        state_off: s.state_off,
        state_len: s.state_len,
        state_hash: s.state_hash,
        turn: s.turn,
        turn_completed: s.turn_completed,
        action_count: s.action_count,
        potion_count: s.potion_count,
        explicit_potion_count: s.explicit_potion_count,
        optional_paid_potion_count: s.optional_paid_potion_count,
        automatic_potion_count: s.automatic_potion_count,
        used_potion_slot_mask: s.used_potion_slot_mask,
        potion_strategic_cost: s.potion_strategic_cost,
        optional_paid_potion_cost: s.optional_paid_potion_cost,
        ambergris_count: s.ambergris_count,
        forced_used_mask: s.forced_used_mask,
        forced_ambergris_count: s.forced_ambergris_count,
        is_terminal: s.is_terminal,
        base_score,
        fast_eval: stored_fast,
        full_eval: stored_full,
        rare_eval,
        label: s.label,
    });
}

#[allow(clippy::too_many_arguments)]
fn stage_resolved_choice(
    scratch: &mut WorkerScratch,
    task: &ActionTask,
    action: CompactAction,
    por_key: u64,
    choices: &[SmallVec<[usize; 8]>],
    is_delayed_or_end_turn: bool,
    max_branches: usize,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    frozen_incumbent: &FrozenIncumbentBound,
    tt: &ShardedParetoTT,
    probe_cache: &ChoiceProbeCache,
    sensitivity: &CombatOrderSensitivity,
    allow_paid_optional_potions: bool,
    eval_mode: EvalMode,
) {
    let Some(mut s) = prepare_staged_candidate(
        scratch,
        task,
        false,
        true,
        false,
        true,
        content,
        root_baseline,
        policy,
        profile,
        cert,
        frozen_incumbent,
        tt,
        sensitivity,
        allow_paid_optional_potions,
        eval_mode,
    ) else {
        return;
    };

    let already_resolved = scratch.choice_resolved.len();
    let should_probe_in_mem = !s.is_terminal
        && ((is_delayed_or_end_turn && already_resolved >= 1)
            || (!is_delayed_or_end_turn && already_resolved >= max_branches));
    let (
        lookahead_score,
        lookahead_hp_lost,
        lookahead_board_investment,
        has_lookahead,
        lookahead_victory,
    ) = if should_probe_in_mem {
        if let Some(probe) = evaluate_choice_probe_state(
            &mut scratch.choice_probe_ws,
            &scratch.state,
            s.state_hash,
            probe_cache,
            content,
            root_baseline,
            profile,
            eval_mode,
            1,
            s.optional_paid_potion_cost,
            s.optional_paid_potion_count as usize,
            s.action_count,
        ) {
            let encoded = encode_choice_probe_rank_score(&probe);
            if !s.turn_completed {
                if let Some(fe) = s.full_eval.as_mut() {
                    fe.has_choice_lookahead = true;
                    fe.choice_lookahead_score = Some(encoded);
                }
            }
            (
                encoded,
                probe.hp_lost,
                probe.board_investment,
                true,
                probe.is_victory,
            )
        } else {
            (
                0.0,
                s.fast_eval.cumulative_hp_lost,
                s.full_eval.as_ref().map_or(0, |f| f.board_investment),
                false,
                s.fast_eval.won,
            )
        }
    } else {
        (
            0.0,
            s.fast_eval.cumulative_hp_lost,
            s.full_eval.as_ref().map_or(0, |f| f.board_investment),
            false,
            s.fast_eval.won,
        )
    };

    let terminal_probe_score = if s.is_terminal {
        let net_potion_cost_centi =
            PotionUsePolicy::compute_net_potion_opportunity_cost_centi(
                s.optional_paid_potion_cost,
                s.optional_paid_potion_count as i32,
                s.fast_eval.alchemize_count as i32,
                s.fast_eval.empty_potion_slots as i32,
                root_baseline,
            );
        compute_beam_rank_score_with_centi(
            &s.fast_eval,
            &FullEvalDetails::default(),
            root_baseline,
            profile.base_score_only,
            profile.boss_tempo_hp_pricing,
            profile.beam_weight_perturbation,
            eval_mode,
            net_potion_cost_centi,
            s.action_count,
        )
    } else {
        0.0
    };
    let base_score = s.fast_eval.base_score;
    let rare_eval = rare_eval_opt(&s.fast_eval);
    let stored_fast = StoredFastEval::from(&s.fast_eval);
    let stored_full = s.full_eval.map(|f| f.to_stored());
    let (choices_offset, choices_len) = scratch.choice_staging_choices.push_choices(choices);
    scratch.choice_resolved.push(ResolvedChoiceCandidate {
        choices_offset,
        choices_len,
        action,
        por_key,
        has_extra_end_turn: s.has_extra_end_turn,
        state_slab_idx: s.state_slab_idx,
        state_off: s.state_off,
        state_len: s.state_len,
        state_hash: s.state_hash,
        turn: s.turn,
        turn_completed: s.turn_completed,
        action_count: s.action_count,
        potion_count: s.potion_count,
        explicit_potion_count: s.explicit_potion_count,
        optional_paid_potion_count: s.optional_paid_potion_count,
        automatic_potion_count: s.automatic_potion_count,
        used_potion_slot_mask: s.used_potion_slot_mask,
        potion_strategic_cost: s.potion_strategic_cost,
        optional_paid_potion_cost: s.optional_paid_potion_cost,
        ambergris_count: s.ambergris_count,
        forced_used_mask: s.forced_used_mask,
        forced_ambergris_count: s.forced_ambergris_count,
        is_terminal: s.is_terminal,
        base_score,
        fast_eval: stored_fast,
        full_eval: stored_full,
        rare_eval,
        label: s.label,
        terminal_probe_score,
        lookahead_score,
        lookahead_hp_lost,
        lookahead_board_investment,
        has_lookahead,
        lookahead_victory,
    });
}

#[allow(clippy::too_many_arguments)]
fn probe_choice_selection_1t(
    scratch: &mut WorkerScratch,
    _static_ctx: &Arc<RootStaticContext>,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    profile: &SolverSearchProfile,
    eval_mode: EvalMode,
    probe_cache: &ChoiceProbeCache,
    task: &ActionTask,
    sel: &[usize],
) -> f64 {
    clone_battle_state_into(&scratch.choice_paused_states[0], &mut scratch.state);
    scratch.choice_replay_attempts += 1;
    scratch.transitions += 1;
    let Ok(mut out) = CombatEngine::resume_choice(&mut scratch.state, content, sel) else {
        return f64::NEG_INFINITY;
    };
    let mut step_guard = 0usize;
    while let StepOutcome::NeedsChoice(sub_req) = out {
        step_guard += 1;
        if step_guard >= 4 || sub_req.candidates.is_empty() {
            if sub_req.min_count == 0 {
                scratch.choice_replay_attempts += 1;
                if let Ok(next) = CombatEngine::resume_choice(&mut scratch.state, content, &[]) {
                    out = next;
                    continue;
                }
            }
            return f64::NEG_INFINITY;
        }
        let fallback_sel: SmallVec<[usize; 8]> = if sub_req.min_count == 0 {
            SmallVec::new()
        } else {
            let take = sub_req.min_count.min(sub_req.candidates.len());
            let start = sub_req.candidates.len() - take;
            (start..sub_req.candidates.len()).collect()
        };
        scratch.choice_replay_attempts += 1;
        let Ok(next) = CombatEngine::resume_choice(&mut scratch.state, content, &fallback_sel)
        else {
            return f64::NEG_INFINITY;
        };
        out = next;
    }

    let (opt_cost, opt_count) = match task.spec {
        ActionSpec::UsePotion {
            optional_paid_cost_delta,
            optional_paid_count_delta,
            ..
        } => (
            task.parent_optional_paid_potion_cost + optional_paid_cost_delta,
            task.parent_optional_paid_potion_count + optional_paid_count_delta,
        ),
        _ => (
            task.parent_optional_paid_potion_cost,
            task.parent_optional_paid_potion_count,
        ),
    };
    let state_hash = hash_canonical_key(&scratch.state);
    let Some(probe) = evaluate_choice_probe_state(
        &mut scratch.choice_probe_ws,
        &scratch.state,
        state_hash,
        probe_cache,
        content,
        root_baseline,
        profile,
        eval_mode,
        1,
        opt_cost,
        opt_count as usize,
        task.parent_action_count + 1,
    ) else {
        return f64::NEG_INFINITY;
    };
    encode_choice_probe_rank_score(&probe)
}

#[allow(clippy::too_many_arguments)]
fn screen_high_branching_multi_pick(
    scratch: &mut WorkerScratch,
    choices_so_far: &mut SmallVec<[SmallVec<[usize; 8]>; 2]>,
    task: &ActionTask,
    action: CompactAction,
    por_key: u64,
    is_delayed_or_end_turn: bool,
    max_branches: usize,
    static_ctx: &Arc<RootStaticContext>,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    frozen_incumbent: &FrozenIncumbentBound,
    tt: &ShardedParetoTT,
    probe_cache: &ChoiceProbeCache,
    sensitivity: &CombatOrderSensitivity,
    allow_paid_optional_potions: bool,
    eval_mode: EvalMode,
    req: &sts2_kernel::continuation::ChoiceRequest,
) -> bool {
    let n = req.candidates.len();
    let mut groups: SmallVec<[(usize, SmallVec<[usize; 4]>); 24]> = SmallVec::new();
    if policy.accelerations.enable_multiset_choice_dedup {
        let mut keys: SmallVec<[HandCardEquivKey; 24]> = SmallVec::with_capacity(n);
        clone_battle_state_into(&scratch.choice_paused_states[0], &mut scratch.state);
        for i in 0..n {
            let k = card_equiv_key(&mut scratch.state, content, req.candidates[i]);
            if let Some(pos) = keys.iter().position(|&ek| ek == k) {
                groups[pos].1.push(i);
            } else {
                keys.push(k);
                groups.push((i, smallvec::smallvec![i]));
            }
        }
    } else {
        for i in 0..n {
            groups.push((i, smallvec::smallvec![i]));
        }
    }

    let distinct_n = groups.len();
    let is_optional_multi = req.min_count == 0 && req.max_count > 1 && distinct_n > 6;
    let is_fixed_k_multi =
        req.min_count > 1 && req.max_count >= req.min_count && distinct_n > 10;
    if !is_optional_multi && !is_fixed_k_multi {
        return false;
    }

    if is_optional_multi {
        let mut scores = std::mem::take(&mut scratch.marginal_scores);
        let mut order = std::mem::take(&mut scratch.marginal_order);
        scores.clear();
        order.clear();
        scores.reserve(distinct_n);
        order.reserve(distinct_n);
        for g in 0..distinct_n {
            let rep = groups[g].0;
            let sc = probe_choice_selection_1t(
                scratch,
                static_ctx,
                content,
                root_baseline,
                profile,
                eval_mode,
                probe_cache,
                task,
                &[rep],
            );
            scores.push(sc);
            order.push(g);
        }
        order.sort_unstable_by(|&a, &b| scores[b].total_cmp(&scores[a]).then_with(|| a.cmp(&b)));

        let mut greedy_flat: SmallVec<[usize; 24]> = SmallVec::new();
        for &g in &order {
            greedy_flat.extend_from_slice(&groups[g].1);
        }
        let max_greedy = greedy_flat.len().min(req.max_count).min(15);
        for subset_size in 7..=max_greedy {
            if scratch.choice_resolved.len() >= CHOICE_RESOLVED_CAPACITY {
                break;
            }
            let mut sel: SmallVec<[usize; 8]> = greedy_flat[..subset_size].iter().copied().collect();
            sel.sort_unstable();
            clone_battle_state_into(&scratch.choice_paused_states[0], &mut scratch.state);
            scratch.choice_replay_attempts += 1;
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
                        por_key,
                        choices_so_far.as_slice(),
                        is_delayed_or_end_turn,
                        max_branches,
                        content,
                        root_baseline,
                        policy,
                        profile,
                        cert,
                        frozen_incumbent,
                        tt,
                        probe_cache,
                        sensitivity,
                        allow_paid_optional_potions,
                        eval_mode,
                    );
                }
                StepOutcome::NeedsChoice(_) => {
                    if allows_brightest_flame(
                        policy.brightest_flame_max_hp_loss_limit,
                        root_baseline.initial_brightest_flame_max_hp_spent,
                        root_baseline.brightest_flame_spent(&scratch.state),
                    ) {
                        clone_battle_state_into(
                            &scratch.state,
                            &mut scratch.choice_paused_states[1],
                        );
                        resolve_choices_recursive(
                            scratch,
                            choices_so_far,
                            1,
                            task,
                            action,
                            por_key,
                            is_delayed_or_end_turn,
                            max_branches,
                            static_ctx,
                            content,
                            root_baseline,
                            policy,
                            profile,
                            cert,
                            frozen_incumbent,
                            tt,
                            probe_cache,
                            sensitivity,
                            allow_paid_optional_potions,
                            eval_mode,
                        );
                    }
                }
            }
            choices_so_far.pop();
        }

        let keep_groups = 6.min(distinct_n);
        let mut kept_orig: SmallVec<[usize; 24]> = SmallVec::new();
        for &g in &order[..keep_groups] {
            kept_orig.extend_from_slice(&groups[g].1);
        }
        kept_orig.sort_unstable();
        kept_orig.dedup();
        scratch.marginal_scores = scores;
        scratch.marginal_order = order;

        let mut sub_req = req.clone();
        sub_req.candidates = kept_orig.iter().map(|&idx| req.candidates[idx]).collect();
        sub_req.max_count = sub_req.max_count.min(kept_orig.len());
        clone_battle_state_into(&scratch.choice_paused_states[0], &mut scratch.state);
        let mut sub_choices = std::mem::take(&mut scratch.choice_enum_bufs[0]);
        let mut enum_scratch = std::mem::take(&mut scratch.choice_enum_scratch);
        enumerate_canonical_choices_with_scratch(
            &mut scratch.state,
            content,
            &sub_req,
            CHOICE_RESOLVED_CAPACITY,
            policy.accelerations.enable_multiset_choice_dedup,
            &mut enum_scratch,
            &mut sub_choices,
        );
        scratch.choice_enum_scratch = enum_scratch;
        for v in &mut sub_choices {
            for j in v.iter_mut() {
                *j = kept_orig[*j];
            }
            v.sort_unstable();
        }
        scratch.choice_enum_bufs[0] = sub_choices;
        return true;
    }

    // Fixed-k multi-pick (distinct_n > 10)
    let k = req.min_count.min(n);
    let mut tail_anchors: SmallVec<[usize; 8]> = SmallVec::new();
    for g in (0..distinct_n).rev().take(k + 1) {
        tail_anchors.push(groups[g].0);
    }
    let mut head_anchors: SmallVec<[usize; 8]> = SmallVec::new();
    for cand_g in [
        0usize,
        distinct_n / 2,
        1usize,
        (distinct_n / 2) + 1,
        2usize,
        (distinct_n / 2) + 2,
    ] {
        if cand_g < distinct_n {
            let rep = groups[cand_g].0;
            if !head_anchors.contains(&rep) {
                head_anchors.push(rep);
            }
        }
    }

    let build_k_selection = |pivot: usize, anchors: &[usize]| -> SmallVec<[usize; 8]> {
        let mut sel: SmallVec<[usize; 8]> = SmallVec::new();
        sel.push(pivot);
        for &a in anchors {
            if sel.len() >= k {
                break;
            }
            if !sel.contains(&a) {
                sel.push(a);
            }
        }
        for idx in 0..n {
            if sel.len() >= k {
                break;
            }
            if !sel.contains(&idx) {
                sel.push(idx);
            }
        }
        sel.sort_unstable();
        sel
    };

    let mut scores = std::mem::take(&mut scratch.marginal_scores);
    let mut order = std::mem::take(&mut scratch.marginal_order);
    let mut probed_sels = std::mem::take(&mut scratch.probed_sels);
    scores.clear();
    order.clear();
    probed_sels.clear();
    scores.reserve(distinct_n);
    order.reserve(distinct_n);

    let mut probe_cached_sel =
        |scratch: &mut WorkerScratch, sel: &SmallVec<[usize; 8]>| -> f64 {
            if let Some(&(_, sc, rep_d, trans_d)) =
                probed_sels.iter().find(|(s, _, _, _)| s == sel)
            {
                scratch.choice_replay_attempts += rep_d;
                scratch.transitions += trans_d;
                return sc;
            }
            let rep_before = scratch.choice_replay_attempts;
            let trans_before = scratch.transitions;
            let sc = probe_choice_selection_1t(
                scratch,
                static_ctx,
                content,
                root_baseline,
                profile,
                eval_mode,
                probe_cache,
                task,
                sel,
            );
            probed_sels.push((
                sel.clone(),
                sc,
                scratch.choice_replay_attempts - rep_before,
                scratch.transitions - trans_before,
            ));
            sc
        };

    // Round 1: tail vs head/mid anchors across distinct equivalence reps only
    for g in 0..distinct_n {
        let rep = groups[g].0;
        let sel_tail = build_k_selection(rep, &tail_anchors);
        let s_tail = probe_cached_sel(scratch, &sel_tail);
        let sel_head = build_k_selection(rep, &head_anchors);
        let s_head = probe_cached_sel(scratch, &sel_head);
        scores.push(s_tail.max(s_head));
        order.push(g);
    }

    // Round 2: coordinate ascent against Round-1 MVP anchors (top 3 + last)
    order.sort_unstable_by(|&a, &b| scores[b].total_cmp(&scores[a]).then_with(|| a.cmp(&b)));
    let mut mvp_anchors: SmallVec<[usize; 8]> = SmallVec::new();
    for &g in order.iter().take(3) {
        mvp_anchors.push(groups[g].0);
    }
    if let Some(&last_g) = order.last() {
        let rep = groups[last_g].0;
        if !mvp_anchors.contains(&rep) {
            mvp_anchors.push(rep);
        }
    }
    for g in 0..distinct_n {
        let sel_mvp = build_k_selection(groups[g].0, &mvp_anchors);
        let s_mvp = probe_cached_sel(scratch, &sel_mvp);
        if s_mvp > scores[g] {
            scores[g] = s_mvp;
        }
    }
    order.sort_unstable_by(|&a, &b| scores[b].total_cmp(&scores[a]).then_with(|| a.cmp(&b)));

    let target_s = if k <= 2 {
        10
    } else if k == 3 {
        9
    } else {
        7
    };
    let cap_total = target_s.max(k.min(n)).min(n);
    let mut kept_orig: SmallVec<[usize; 16]> = SmallVec::new();
    for &g in order.iter().take(cap_total.min(distinct_n)) {
        kept_orig.push(groups[g].0);
    }
    if kept_orig.len() < cap_total {
        for &g in &order {
            for &idx in groups[g].1.iter().skip(1) {
                if kept_orig.len() >= cap_total {
                    break;
                }
                kept_orig.push(idx);
            }
            if kept_orig.len() >= cap_total {
                break;
            }
        }
    }
    probed_sels.clear();
    scratch.probed_sels = probed_sels;
    scratch.marginal_scores = scores;
    scratch.marginal_order = order;

    let mut sub_req = req.clone();
    sub_req.candidates = kept_orig.iter().map(|&idx| req.candidates[idx]).collect();
    sub_req.max_count = sub_req.max_count.min(kept_orig.len());
    clone_battle_state_into(&scratch.choice_paused_states[0], &mut scratch.state);
    let mut sub_choices = std::mem::take(&mut scratch.choice_enum_bufs[0]);
    let mut enum_scratch = std::mem::take(&mut scratch.choice_enum_scratch);
    enumerate_canonical_choices_with_scratch(
        &mut scratch.state,
        content,
        &sub_req,
        CHOICE_RESOLVED_CAPACITY,
        policy.accelerations.enable_multiset_choice_dedup,
        &mut enum_scratch,
        &mut sub_choices,
    );
    scratch.choice_enum_scratch = enum_scratch;
    for v in &mut sub_choices {
        for j in v.iter_mut() {
            *j = kept_orig[*j];
        }
        v.sort_unstable();
    }
    scratch.choice_enum_bufs[0] = sub_choices;
    true
}

#[allow(clippy::too_many_arguments)]
fn resolve_choices_recursive(
    scratch: &mut WorkerScratch,
    choices_so_far: &mut SmallVec<[SmallVec<[usize; 8]>; 2]>,
    depth: usize,
    task: &ActionTask,
    action: CompactAction,
    por_key: u64,
    is_delayed_or_end_turn: bool,
    max_branches: usize,
    static_ctx: &Arc<RootStaticContext>,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    cert: &ClosureCertificate,
    frozen_incumbent: &FrozenIncumbentBound,
    tt: &ShardedParetoTT,
    probe_cache: &ChoiceProbeCache,
    sensitivity: &CombatOrderSensitivity,
    allow_paid_optional_potions: bool,
    eval_mode: EvalMode,
) {
    if depth >= 4 || scratch.choice_resolved.len() >= CHOICE_RESOLVED_CAPACITY {
        return;
    }
    let need_screen = depth == 0
        && scratch.choice_paused_states[0]
            .pending
            .as_ref()
            .is_some_and(|p| {
                let req = &p.request;
                let n = req.candidates.len();
                (req.min_count == 0 && req.max_count > 1 && n > 6)
                    || (req.min_count > 1 && req.max_count >= req.min_count && n > 10)
            });
    let screened = if need_screen {
        let req = scratch.choice_paused_states[0]
            .pending
            .as_ref()
            .expect("pending checked above")
            .request
            .clone();
        screen_high_branching_multi_pick(
            scratch,
            choices_so_far,
            task,
            action,
            por_key,
            is_delayed_or_end_turn,
            max_branches,
            static_ctx,
            content,
            root_baseline,
            policy,
            profile,
            cert,
            frozen_incumbent,
            tt,
            probe_cache,
            sensitivity,
            allow_paid_optional_potions,
            eval_mode,
            &req,
        )
    } else {
        false
    };

    if !screened {
        clone_battle_state_into(&scratch.choice_paused_states[depth], &mut scratch.state);
        if scratch.choice_paused_states[depth].pending.is_none() {
            return;
        }
        let mut buf = std::mem::take(&mut scratch.choice_enum_bufs[depth]);
        let mut enum_scratch = std::mem::take(&mut scratch.choice_enum_scratch);
        {
            let paused = &scratch.choice_paused_states[depth];
            let req = &paused.pending.as_ref().expect("pending checked").request;
            enumerate_canonical_choices_with_scratch(
                &mut scratch.state,
                content,
                req,
                CHOICE_RESOLVED_CAPACITY,
                policy.accelerations.enable_multiset_choice_dedup,
                &mut enum_scratch,
                &mut buf,
            );
        }
        scratch.choice_enum_scratch = enum_scratch;
        scratch.choice_enum_bufs[depth] = buf;
    }

    let choice_options = std::mem::take(&mut scratch.choice_enum_bufs[depth]);
    for sel in &choice_options {
        if scratch.choice_resolved.len() >= CHOICE_RESOLVED_CAPACITY {
            break;
        }
        clone_battle_state_into(&scratch.choice_paused_states[depth], &mut scratch.state);
        scratch.choice_replay_attempts += 1;
        scratch.transitions += 1;
        let Ok(outcome) = CombatEngine::resume_choice(&mut scratch.state, content, sel) else {
            continue;
        };
        choices_so_far.push(sel.clone());
        match outcome {
            StepOutcome::Completed | StepOutcome::CombatEnded { .. } => {
                stage_resolved_choice(
                    scratch,
                    task,
                    action,
                    por_key,
                    choices_so_far.as_slice(),
                    is_delayed_or_end_turn,
                    max_branches,
                    content,
                    root_baseline,
                    policy,
                    profile,
                    cert,
                    frozen_incumbent,
                    tt,
                    probe_cache,
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
                    clone_battle_state_into(
                        &scratch.state,
                        &mut scratch.choice_paused_states[depth + 1],
                    );
                    resolve_choices_recursive(
                        scratch,
                        choices_so_far,
                        depth + 1,
                        task,
                        action,
                        por_key,
                        is_delayed_or_end_turn,
                        max_branches,
                        static_ctx,
                        content,
                        root_baseline,
                        policy,
                        profile,
                        cert,
                        frozen_incumbent,
                        tt,
                        probe_cache,
                        sensitivity,
                        allow_paid_optional_potions,
                        eval_mode,
                    );
                }
            }
        }
        choices_so_far.pop();
    }
    scratch.choice_enum_bufs[depth] = choice_options;
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
            .map_or(c.base_score, |f| f.beam_rank_score)
    };
    let board_of = |c: &ResolvedChoiceCandidate| -> i32 {
        c.full_eval.as_ref().map_or(0, |f| f.board_investment)
    };
    let opt_of = |c: &ResolvedChoiceCandidate| -> i32 {
        (c.fast_eval.energy as i32).max(0)
            + (c.fast_eval.stars as i32).max(0)
            + c.full_eval
                .as_ref()
                .map_or(0, |f| f.optionality_cards)
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
    is_turn_setup: bool,
    static_ctx: &Arc<RootStaticContext>,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    profile: &SolverSearchProfile,
    eval_mode: EvalMode,
    probe_cache: &ChoiceProbeCache,
) {
    let resolved_count = scratch.choice_resolved.len();
    if resolved_count == 0 {
        return;
    }

    let max_branches = if is_turn_setup {
        profile.beam_width.max(64).min(CHOICE_RESOLVED_CAPACITY)
    } else if first_pile == PileType::Hand {
        profile.max_hand_choice_branches_per_action
    } else {
        profile.max_pile_choice_branches_per_action
    }
    .max(1);

    let is_delayed_or_end_turn = is_end_turn
        || is_turn_setup
        || matches!(
            first_pile,
            PileType::Draw | PileType::Discard | PileType::Exhaust
        );
    let run_mini_rollout =
        resolved_count >= 2 && (resolved_count > max_branches || is_delayed_or_end_turn);

    if run_mini_rollout {
        for i in 0..resolved_count {
            if scratch.choice_resolved[i].has_lookahead {
                continue;
            }
            let (is_term, slab_idx, off, len, state_hash, opt_cost, opt_count, action_count) = {
                let c = &scratch.choice_resolved[i];
                (
                    c.is_terminal,
                    c.state_slab_idx,
                    c.state_off,
                    c.state_len,
                    c.state_hash,
                    c.optional_paid_potion_cost,
                    c.optional_paid_potion_count,
                    c.action_count,
                )
            };
            let probe = if is_term {
                let c = &scratch.choice_resolved[i];
                ChoiceProbeResult {
                    score: c.terminal_probe_score,
                    hp_lost: c.fast_eval.cumulative_hp_lost as i32,
                    board_investment: 0,
                    is_victory: c.fast_eval.won(),
                    is_defeat: c.fast_eval.player_dead() || c.fast_eval.projected_player_hp <= 0,
                }
            } else {
                let state_bytes = scratch.choice_slab.get(slab_idx, off, len);
                let Some(probe) = evaluate_choice_probe_packed(
                    &mut scratch.choice_probe_ws,
                    state_bytes,
                    state_hash,
                    probe_cache,
                    static_ctx,
                    content,
                    root_baseline,
                    profile,
                    eval_mode,
                    1,
                    opt_cost,
                    opt_count as usize,
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
                    fe.choice_lookahead_score = Some(encoded);
                }
            }
        }
    }

    let mut selected_indices = [0usize; CHOICE_RESOLVED_CAPACITY];
    let mut selected_len = 0usize;
    if resolved_count <= max_branches {
        for i in 0..resolved_count {
            selected_indices[selected_len] = i;
            selected_len += 1;
        }
        selected_indices[..selected_len].sort_unstable_by(|&ia, &ib| {
            compare_resolved_choice(&scratch.choice_resolved[ia], &scratch.choice_resolved[ib], 0)
        });
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
                if selected_len >= cap {
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
                selected_indices[selected_len] = bi;
                selected_len += 1;
            }
        }
        selected_indices[..selected_len].sort_unstable_by(|&ia, &ib| {
            compare_resolved_choice(&scratch.choice_resolved[ia], &scratch.choice_resolved[ib], 0)
        });
    }

    for (choice_ord, &idx) in selected_indices[..selected_len].iter().enumerate() {
        let cand = scratch.choice_resolved[idx];
        let (state_slab_idx, state_off, state_len) = if cand.state_len == 0 {
            (0, 0, 0)
        } else {
            let bytes =
                scratch
                    .choice_slab
                    .get(cand.state_slab_idx, cand.state_off, cand.state_len);
            scratch.slab.alloc_slice(bytes)
        };
        let (choices_offset, choices_len) = scratch.staged_choices.extend_from_arena_slice(
            &scratch.choice_staging_choices,
            cand.choices_offset,
            cand.choices_len,
        );
        scratch.candidates.push(WorkerCandidate {
            sort_key: (task.parent_rank, task.action_ord, choice_ord as u32),
            parent_id: task.parent_id,
            action: cand.action,
            por_key: cand.por_key,
            has_extra_end_turn: cand.has_extra_end_turn,
            choices_offset,
            choices_len,
            state_slab_idx,
            state_off,
            state_len,
            state_hash: cand.state_hash,
            turn: cand.turn,
            turn_completed: cand.turn_completed,
            action_count: cand.action_count,
            potion_count: cand.potion_count,
            explicit_potion_count: cand.explicit_potion_count,
            optional_paid_potion_count: cand.optional_paid_potion_count,
            automatic_potion_count: cand.automatic_potion_count,
            used_potion_slot_mask: cand.used_potion_slot_mask,
            potion_strategic_cost: cand.potion_strategic_cost,
            optional_paid_potion_cost: cand.optional_paid_potion_cost,
            ambergris_count: cand.ambergris_count,
            forced_used_mask: cand.forced_used_mask,
            forced_ambergris_count: cand.forced_ambergris_count,
            is_terminal: cand.is_terminal,
            base_score: cand.base_score,
            fast_eval: cand.fast_eval,
            full_eval: cand.full_eval,
            rare_eval: cand.rare_eval,
            label: cand.label,
        });
    }
    scratch.choice_resolved.clear();
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
    frozen_incumbent: &FrozenIncumbentBound,
    tt: &ShardedParetoTT,
    probe_cache: &ChoiceProbeCache,
    sensitivity: &CombatOrderSensitivity,
    allow_paid_optional_potions: bool,
    eval_mode: EvalMode,
) {
    clone_battle_state_into(parent_state, &mut scratch.state);

    let (action, por_key) = match task.spec {
        ActionSpec::TurnSetup => CompactAction::new(
            CompactActionKind::TurnSetup,
            task.parent_turn,
            PorActionClass::Barrier,
        ),
        ActionSpec::PlayCard {
            card_handle,
            target_handle,
            por_class,
        } => CompactAction::new(
            CompactActionKind::PlayCard {
                card_handle: card_handle.0,
                target_handle: target_handle.map(|h| h.0),
            },
            task.parent_turn,
            por_class,
        ),
        ActionSpec::UsePotion {
            potion_slot,
            potion_id,
            target_handle,
            ..
        } => CompactAction::new(
            CompactActionKind::UsePotion {
                potion_slot: potion_slot as u16,
                potion_id,
                target_handle: target_handle.map(|h| h.0),
            },
            task.parent_turn,
            PorActionClass::Barrier,
        ),
        ActionSpec::EndTurn => CompactAction::new(
            CompactActionKind::EndTurn,
            task.parent_turn,
            PorActionClass::Barrier,
        ),
    };

    if matches!(task.spec, ActionSpec::TurnSetup) {
        let Some(req) = scratch.state.pending.as_ref().map(|p| p.request.clone()) else {
            return;
        };
        let first_pile = req.source_pile;
        let (is_delayed_or_end_turn, max_branches) =
            choice_probe_limits(profile, first_pile, false, true);
        scratch.begin_choice_tree();
        clone_battle_state_into(&scratch.state, &mut scratch.choice_paused_states[0]);
        let mut choices_so_far: SmallVec<[SmallVec<[usize; 8]>; 2]> = SmallVec::new();
        resolve_choices_recursive(
            scratch,
            &mut choices_so_far,
            0,
            task,
            action,
            por_key,
            is_delayed_or_end_turn,
            max_branches,
            static_ctx,
            content,
            root_baseline,
            policy,
            profile,
            cert,
            frozen_incumbent,
            tt,
            probe_cache,
            sensitivity,
            allow_paid_optional_potions,
            eval_mode,
        );
        commit_resolved_choices(
            scratch,
            task,
            first_pile,
            false,
            true,
            static_ctx,
            content,
            root_baseline,
            profile,
            eval_mode,
            probe_cache,
        );
        return;
    }

    let mut empty_q: SmallVec<[Vec<usize>; 2]> = SmallVec::new();
    scratch.transitions += 1;
    let outcome = match task.spec {
        ActionSpec::TurnSetup => unreachable!(),
        ActionSpec::PlayCard {
            card_handle,
            target_handle,
            ..
        } => CombatEngine::play_card_prechecked(
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
                por_key,
                false,
                &[],
                0,
                content,
                root_baseline,
                policy,
                profile,
                cert,
                frozen_incumbent,
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
            let (is_delayed_or_end_turn, max_branches) =
                choice_probe_limits(profile, first_pile, is_end_turn, false);
            scratch.begin_choice_tree();
            clone_battle_state_into(&scratch.state, &mut scratch.choice_paused_states[0]);
            let mut choices_so_far: SmallVec<[SmallVec<[usize; 8]>; 2]> = SmallVec::new();
            resolve_choices_recursive(
                scratch,
                &mut choices_so_far,
                0,
                task,
                action,
                por_key,
                is_delayed_or_end_turn,
                max_branches,
                static_ctx,
                content,
                root_baseline,
                policy,
                profile,
                cert,
                frozen_incumbent,
                tt,
                probe_cache,
                sensitivity,
                allow_paid_optional_potions,
                eval_mode,
            );
            commit_resolved_choices(
                scratch,
                task,
                first_pile,
                is_end_turn,
                false,
                static_ctx,
                content,
                root_baseline,
                profile,
                eval_mode,
                probe_cache,
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
    frozen_incumbent: &FrozenIncumbentBound,
    tt: &ShardedParetoTT,
    probe_cache: &ChoiceProbeCache,
    sensitivity: &CombatOrderSensitivity,
    allow_paid_optional_potions: bool,
    eval_mode: EvalMode,
    cancel: &AtomicBool,
    scratches: &mut [WorkerScratch],
    parent_states: &mut [BattleState],
    flat_tasks: &mut Vec<(usize, ActionTask)>,
    parent_slices: &mut Vec<AtomicU64>,
) -> bool {
    for s in scratches.iter_mut() {
        s.reset_for_wave();
    }
    if parent_slice.is_empty() {
        return true;
    }
    if parent_slices.len() < parent_slice.len() {
        parent_slices.resize_with(parent_slice.len(), || AtomicU64::new(0));
    }

    if dop <= 1 || pool.is_none() {
        let scratch = &mut scratches[0];
        let parent_state = &mut parent_states[0];
        for (parent_rank, &pid) in parent_slice.iter().enumerate() {
            if cancel.load(Ordering::Relaxed) {
                parent_slices[parent_rank].store(0, Ordering::Relaxed);
                continue;
            }
            let pnode = arena.get(pid);
            let p_has_la = pnode.has_choice_lookahead();
            let p_la_score = pnode.choice_lookahead_score();
            let pbytes = main_arena.get(pnode.state_handle);
            if unpack_state(pbytes, static_ctx, parent_state).is_err() {
                parent_slices[parent_rank].store(0, Ordering::Relaxed);
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
                root_baseline,
                allow_paid_optional_potions,
                &mut local_tasks,
                &mut scratch.por_pruned,
            );
            let c_start = scratch.candidates.len();
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
                    frozen_incumbent,
                    tt,
                    probe_cache,
                    sensitivity,
                    allow_paid_optional_potions,
                    eval_mode,
                );
            }
            let c_len = scratch.candidates.len() - c_start;
            parent_slices[parent_rank].store(
                if c_len > 0 {
                    pack_parent_slice(0, c_start, c_len)
                } else {
                    0
                },
                Ordering::Relaxed,
            );
            scratch.action_buf = local_tasks;
        }
        return true;
    }

    let pool = pool.unwrap();
    if parent_slice.len() < dop && parent_slice.len() <= parent_states.len() {
        flat_tasks.clear();
        let mut unpacked_count = 0usize;
        for (parent_rank, &pid) in parent_slice.iter().enumerate() {
            let pnode = arena.get(pid);
            let p_has_la = pnode.has_choice_lookahead();
            let p_la_score = pnode.choice_lookahead_score();
            let pbytes = main_arena.get(pnode.state_handle);
            let pstate = &mut parent_states[unpacked_count];
            if unpack_state(pbytes, static_ctx, pstate).is_err() {
                continue;
            }
            let p_idx = unpacked_count;
            let mut local_tasks = std::mem::take(&mut scratches[0].action_buf);
            local_tasks.clear();
            enumerate_parent_tasks(
                parent_rank as u32,
                pid,
                pnode,
                p_has_la,
                p_la_score,
                pstate,
                content,
                policy,
                profile,
                sensitivity,
                root_baseline,
                allow_paid_optional_potions,
                &mut local_tasks,
                &mut scratches[0].por_pruned,
            );
            for t in local_tasks.drain(..) {
                flat_tasks.push((p_idx, t));
            }
            scratches[0].action_buf = local_tasks;
            unpacked_count += 1;
        }

        if flat_tasks.is_empty() {
            return false;
        }
        if flat_tasks.len() <= 1 || (parent_slice.len() <= 2 && flat_tasks.len() <= 4) {
            for &(p_idx, ref task) in flat_tasks.iter() {
                if cancel.load(Ordering::Relaxed) {
                    break;
                }
                execute_action_from_parent(
                    &mut scratches[0],
                    &parent_states[p_idx],
                    task,
                    static_ctx,
                    content,
                    root_baseline,
                    policy,
                    profile,
                    cert,
                    frozen_incumbent,
                    tt,
                    probe_cache,
                    sensitivity,
                    allow_paid_optional_potions,
                    eval_mode,
                );
            }
            return false;
        }

        let active = dop.min(flat_tasks.len()).min(scratches.len());
        let next_idx = AtomicUsize::new(0);
        let tasks_ref: &[(usize, ActionTask)] = flat_tasks.as_slice();
        let parents_ref = &parent_states[..unpacked_count];
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
                        frozen_incumbent,
                        tt,
                        probe_cache,
                        sensitivity,
                        allow_paid_optional_potions,
                        eval_mode,
                    );
                });
            }
        });
        return false;
    }

    for slot in &parent_slices[..parent_slice.len()] {
        slot.store(0, Ordering::Relaxed);
    }
    let active = dop.min(parent_slice.len()).min(scratches.len()).min(parent_states.len());
    let batch_size = (parent_slice.len() / (active * 4)).clamp(1, 6);
    let next_parent = AtomicUsize::new(0);
    let slices_ref = &parent_slices[..parent_slice.len()];
    pool.scope(|scope| {
        for (w_idx, (scratch, parent_state)) in scratches
            .iter_mut()
            .zip(parent_states.iter_mut())
            .take(active)
            .enumerate()
        {
            let next_ref = &next_parent;
            scope.spawn(move |_| loop {
                if cancel.load(Ordering::Relaxed) {
                    break;
                }
                let batch_start = next_ref.fetch_add(batch_size, Ordering::Relaxed);
                if batch_start >= parent_slice.len() {
                    break;
                }
                let batch_end = (batch_start + batch_size).min(parent_slice.len());
                for parent_rank in batch_start..batch_end {
                    if cancel.load(Ordering::Relaxed) {
                        break;
                    }
                    let pid = parent_slice[parent_rank];
                    let pnode = arena.get(pid);
                    let p_has_la = pnode.has_choice_lookahead();
                    let p_la_score = pnode.choice_lookahead_score();
                    let pbytes = main_arena.get(pnode.state_handle);
                    if unpack_state(pbytes, static_ctx, parent_state).is_err() {
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
                        root_baseline,
                        allow_paid_optional_potions,
                        &mut local_tasks,
                        &mut scratch.por_pruned,
                    );
                    let c_start = scratch.candidates.len();
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
                            frozen_incumbent,
                            tt,
                            probe_cache,
                            sensitivity,
                            allow_paid_optional_potions,
                            eval_mode,
                        );
                    }
                    let c_len = scratch.candidates.len() - c_start;
                    if c_len > 0 {
                        slices_ref[parent_rank]
                            .store(pack_parent_slice(w_idx, c_start, c_len), Ordering::Relaxed);
                    }
                    scratch.action_buf = local_tasks;
                }
            });
        }
    });
    true
}

fn replay_fixed_prefix(
    root: &BattleState,
    prefix: &[PlannedAction],
    policy: &SearchPolicy,
    root_baseline: &RootEvalBaseline,
    content: &dyn ContentDispatcher,
) -> Result<(BattleState, u16, u16, i32, i32, u16, u16, u64, u16), KernelError> {
    let mut state = root.clone();
    let mut potion_count = 0u16;
    let mut optional_paid_count = 0u16;
    let mut potion_cost = 0i32;
    let mut optional_paid_cost = 0i32;
    let mut ambergris_count = 0u16;
    let mut used_potion_slot_mask = 0u16;
    let mut forced_used_mask = 0u64;
    let mut forced_ambergris_count = 0u16;
    for act in prefix {
        match act {
            PlannedAction::TurnSetup { choices } => {
                let q = choices.clone().unwrap_or_default();
                for sel in q {
                    let _ = CombatEngine::resume_choice(&mut state, content, &sel)?;
                }
            }
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
                let slot_bit = if *potion_slot < 16 {
                    1u16 << *potion_slot
                } else {
                    0
                };
                let is_generated = (state.player.combat_generated_potion_mask & slot_bit) != 0;
                potion_count += 1;
                if !is_generated {
                    let cost = if *potion_slot < 16 && root_baseline.root_slot_costs[*potion_slot] >= 0
                    {
                        root_baseline.root_slot_costs[*potion_slot] as i32
                    } else {
                        PotionValuationRegistry::strategic_hp_cost_for_root(
                            *potion_id,
                            root_baseline.renewable_potion_shaped_rock,
                            root_baseline.initial_player_max_hp,
                            &policy.potion_hp_threshold_overrides,
                        )
                    };
                    let amb = u16::from(*potion_id == PotionId::Ambergris);
                    potion_cost += cost;
                    ambergris_count += amb;
                    used_potion_slot_mask |= slot_bit;
                    if let Some(bit_idx) = policy.forced_bit_for(*potion_slot, *potion_id) {
                        let bit = if bit_idx < 64 { 1u64 << bit_idx } else { 0 };
                        if (forced_used_mask & bit) == 0 {
                            forced_used_mask |= bit;
                            forced_ambergris_count += amb;
                        }
                    } else if cost > 0 {
                        optional_paid_count += 1;
                        optional_paid_cost += cost;
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
        optional_paid_count,
        potion_cost,
        optional_paid_cost,
        ambergris_count,
        used_potion_slot_mask,
        forced_used_mask,
        forced_ambergris_count,
    ))
}

fn make_stand_pat_task(
    parent_rank: u32,
    parent_id: NodeId,
    pnode: &SearchNode,
    parent_filled_potions: u8,
    action_ord: u32,
) -> ActionTask {
    ActionTask {
        parent_rank,
        parent_id,
        parent_turn: pnode.turn as i32,
        parent_action_count: pnode.action_count,
        parent_potion_count: pnode.potion_count as u16,
        parent_explicit_potion_count: pnode.explicit_potion_count as u16,
        parent_optional_paid_potion_count: pnode.optional_paid_potion_count as u16,
        parent_automatic_potion_count: pnode.automatic_potion_count as u16,
        parent_filled_potions,
        parent_used_potion_slot_mask: pnode.used_potion_slot_mask,
        parent_potion_cost: pnode.potion_strategic_cost as i32,
        parent_optional_paid_potion_cost: pnode.optional_paid_potion_cost as i32,
        parent_ambergris_count: pnode.ambergris_count as u16,
        parent_forced_used_mask: pnode.forced_used_mask,
        parent_forced_ambergris_count: pnode.forced_ambergris_count as u16,
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
    incumbent: &mut FrozenIncumbentBound,
    policy: &SearchPolicy,
    allow_paid_optional_potions: bool,
    fast_eval: &StoredFastEval,
    win_turn: i32,
    total_potions: u16,
    explicit_potion_count: u16,
    optional_paid_potion_count: u16,
    potion_strategic_cost: i32,
    optional_paid_potion_cost: i32,
    forced_used_mask: u64,
    has_victory: &mut bool,
    best_victory_turn: &mut Option<i32>,
    best_victory_key: &mut Option<(i64, i32, i32)>,
    best_victory_clean_zero_cost: &mut bool,
) {
    let end_max_hp = if fast_eval.player_max_hp > 0 {
        fast_eval.player_max_hp as i32
    } else {
        root_baseline.initial_player_max_hp.max(1)
    };
    let relic_heal = ActEndingBossPolicy::ranked_post_combat_relic_heal(
        root_baseline.post_combat_relic_heal,
        true,
        fast_eval.player_hp as i32,
        end_max_hp,
    );
    let net_hp_delta_centi = ActEndingBossPolicy::compute_net_persistent_hp_delta_centi(
        fast_eval.player_hp as i32,
        relic_heal,
        root_baseline.initial_player_hp,
        end_max_hp,
        root_baseline.initial_player_max_hp,
        fast_eval.death_save_hp_restored as i32,
        fast_eval.strategic_hp_credit as i32,
        root_baseline.boss_hp_relief,
        root_baseline.ignore_long_term_rewards,
    );
    let net_potion_cost_centi = PotionUsePolicy::compute_net_potion_opportunity_cost_centi(
        optional_paid_potion_cost,
        optional_paid_potion_count as i32,
        fast_eval.alchemize_count as i32,
        fast_eval.empty_potion_slots as i32,
        root_baseline,
    );
    let deficit = ActEndingBossPolicy::round_centi_to_hp(-net_hp_delta_centi);

    incumbent_table.tighten(
        PrimaryIncumbentBucket {
            potions: total_potions,
            forfeits_gold: fast_eval.forfeits_gold(),
        },
        PrimarySearchIncumbent {
            strategic_hp_deficit: deficit,
            combat_ended_turn: win_turn,
            potion_strategic_cost,
        },
    );

    let enforce_forced = root_baseline.all_forced_potion_mask != 0;
    let forced_ok = !enforce_forced
        || (forced_used_mask & root_baseline.all_forced_potion_mask)
            == root_baseline.all_forced_potion_mask;
    let req_ok = (explicit_potion_count as usize) >= root_baseline.minimum_required_potion_uses;
    let disabled_ok = root_baseline.effective_potion_policy != SolverPotionPolicy::Disabled
        || (optional_paid_potion_count == 0
            && (explicit_potion_count as usize) <= root_baseline.forced_directive_count);
    if !forced_ok || !req_ok || !disabled_ok {
        return;
    }

    let check_theft = policy.theft_policy == SolverTheftPolicy::PreserveResources;
    incumbent.tighten(
        root_baseline,
        true,
        fast_eval.death_save_use_count as i32,
        net_hp_delta_centi,
        net_potion_cost_centi,
        optional_paid_potion_count as usize,
        explicit_potion_count as usize,
        win_turn,
        fast_eval.outstanding_stolen as i32,
        fast_eval.forfeits_gold(),
        check_theft,
    );

    let v_centi = (net_hp_delta_centi as i64) - (net_potion_cost_centi as i64);
    let key = (-v_centi, -net_hp_delta_centi, win_turn);
    *has_victory = true;
    if best_victory_key.is_none_or(|cur| key < cur) {
        *best_victory_key = Some(key);
        *best_victory_turn = Some(win_turn);
        let can_heal_or_grow = root_baseline.has_any_heal_or_unknown_generation
            || root_baseline.effective_has_growth_targets
            || !root_baseline.active_relic_targets.is_empty()
            || !cert.healing_certified
            || fast_eval.death_save_use_count > 0
            || (check_theft && (fast_eval.outstanding_stolen > 0 || fast_eval.forfeits_gold()))
            || (allow_paid_optional_potions
                && (root_baseline.paid_heal_potion_slot_mask != 0
                    || root_baseline.paid_fruit_juice_slot_mask != 0
                    || root_baseline.paid_entropic_brew_slot_mask != 0));
        let lossless = net_hp_delta_centi >= 0
            && optional_paid_potion_count == 0
            && net_potion_cost_centi <= 0;
        *best_victory_clean_zero_cost = lossless && !can_heal_or_grow;
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
    incumbent: &mut FrozenIncumbentBound,
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
    let mut resources = PassResources::acquire(
        &root.static_ctx,
        worker_count,
        policy.transposition_entry_limit,
    );
    run_beam_pass_with_resources(
        root,
        content,
        policy,
        profile,
        cert,
        incumbent_table,
        incumbent,
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
    incumbent: &mut FrozenIncumbentBound,
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
        node_arena,
        prune_scratch,
        flat_tasks,
        step_arena,
        next_step_arena,
        turn_staging_arena,
        tt,
        choice_probe_cache,
        turn_rollout_cache,
        stand_pat_seen,
        sort_list,
        parent_slices,
        tt_shard_buckets,
        tt_shard_accepted,
        tt_accepted_flags,
        tt_flat_refs,
        rollout_results,
    } = resources;
    debug_assert!(scratches.len() >= worker_count && parent_states.len() >= worker_count);

    let (
        start_state,
        prefix_potions,
        prefix_optional_paid_potions,
        prefix_potion_cost,
        prefix_optional_paid_cost,
        prefix_ambergris,
        prefix_used_slot_mask,
        prefix_forced_mask,
        prefix_forced_amb,
    ) = if let Some(prefix) = fixed_prefix {
        replay_fixed_prefix(root, prefix, policy, &root_baseline, content)?
    } else {
        (
            root.clone(),
            0u16,
            0u16,
            0i32,
            0i32,
            0u16,
            0u16,
            0u64,
            0u16,
        )
    };

    let arena = node_arena;
    arena.clear();
    let prefix_len = fixed_prefix.map_or(0, |p| p.len() as u16);

    pack_state(&start_state, &mut scratches[0].pack_buf);
    let mut packed_state_bytes_total: u64 = scratches[0].pack_buf.len() as u64;
    let root_hash = hash_canonical_key(&start_state);
    let root_state_handle = step_arena.commit_slice(&scratches[0].pack_buf);
    let root_fast = evaluate_fast(&start_state, content, &root_baseline, None);
    let mut root_full = evaluate_full(&start_state, content, &root_fast, &root_baseline);
    let prefix_net_potion_cost_centi = PotionUsePolicy::compute_net_potion_opportunity_cost_centi(
        prefix_optional_paid_cost,
        prefix_optional_paid_potions as i32,
        root_fast.alchemize_count as i32,
        root_fast.empty_potion_slots as i32,
        &root_baseline,
    );
    root_full.beam_rank_score = compute_beam_rank_score_with_centi(
        &root_fast,
        &root_full,
        &root_baseline,
        profile.base_score_only,
        profile.boss_tempo_hp_pricing,
        profile.beam_weight_perturbation,
        options.eval_mode,
        prefix_net_potion_cost_centi,
        prefix_len,
    );
    let mut best_seen_max_dark_evoke = root_full.max_dark_evoke;
    let mut best_seen_poison_and_doom = root_full.total_poison_and_doom;
    let mut best_seen_board_investment = root_full.board_investment;
    let root_rare_idx = arena.push_rare_eval(RareNodeEval {
        growth_rewards: root_fast.growth_rewards,
        relic_eval: root_fast.relic_eval,
    });
    let root_terminal = !start_state.is_in_progress || root_fast.won || root_fast.player_dead;
    let mut root_node = SearchNode {
        state_handle: root_state_handle,
        state_hash: root_hash,
        beam_rank_score: root_fast.base_score,
        turn: start_state
            .player
            .turn_number
            .clamp(i16::MIN as i32, i16::MAX as i32) as i16,
        action_count: prefix_len,
        potion_count: prefix_potions.min(u8::MAX as u16) as u8,
        explicit_potion_count: prefix_potions.min(u8::MAX as u16) as u8,
        optional_paid_potion_count: prefix_optional_paid_potions.min(u8::MAX as u16) as u8,
        used_potion_slot_mask: prefix_used_slot_mask,
        potion_strategic_cost: prefix_potion_cost.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
        optional_paid_potion_cost: prefix_optional_paid_cost
            .clamp(i16::MIN as i32, i16::MAX as i32) as i16,
        ambergris_count: prefix_ambergris.min(u8::MAX as u16) as u8,
        forced_used_mask: prefix_forced_mask,
        forced_ambergris_count: prefix_forced_amb.min(u8::MAX as u16) as u8,
        node_flags: if root_terminal {
            NODE_FLAG_IS_TERMINAL
        } else {
            0
        },
        fast_eval: StoredFastEval::from(&root_fast),
        rare_eval_idx: root_rare_idx,
        ..Default::default()
    };
    root_node.set_full_eval(&root_full.to_stored());
    let root_id = arena.push_node(root_node);

    let mut current_turn_frontier: Vec<NodeId> = Vec::new();
    let mut completed: Vec<NodeId> = Vec::new();
    if arena.get(root_id).is_terminal() {
        completed.push(root_id);
    } else {
        current_turn_frontier.push(root_id);
    }

    let mut expanded_nodes = 0usize;
    let mut transition_count = 0u64;
    let mut choice_replay_attempts = 0u64;
    let mut incumbent_pruned = 0u64;
    let mut por_pruned = 0u64;
    let mut termination: &'static str = "None";
    let mut early_turn_frontiers = Vec::new();
    let mut boss_tempo_prefixes = Vec::new();
    let mut latest_completed_turn_nodes: Vec<NodeId> = Vec::new();
    let mut last_active_frontier: Vec<NodeId> = Vec::new();
    let mut active_frontier: Vec<NodeId> = Vec::new();
    let mut next_step_candidates: Vec<NodeId> = Vec::new();
    let mut next_turn_candidates: Vec<NodeId> = Vec::new();
    let mut turn_setup_ended_turn: Vec<NodeId> = Vec::new();
    let mut shortlist_buf: Vec<NodeId> = Vec::new();
    let mut sp_mid_unused: Vec<NodeId> = Vec::new();

    let mut has_victory = completed.iter().any(|&id| arena.get(id).fast_eval.won());
    let mut best_won_hp_lost: Option<i32> = completed
        .iter()
        .filter(|&&id| arena.get(id).fast_eval.won())
        .map(|&id| arena.get(id).fast_eval.cumulative_hp_lost as i32)
        .min();
    let mut last_progress_ms: Option<u64> = None;
    let mut best_seen_enemy_hp = root_fast.enemy_hp;
    let mut best_seen_alive_enemies = root_fast.alive_enemy_count + root_fast.reviving_enemy_count;
    let mut turns_without_progress = 0i32;
    let mut turns_since_enemy_hp_decreased = 0i32;
    let mut best_victory_turn: Option<i32> = None;
    let mut best_victory_key: Option<(i64, i32, i32)> = None;
    let mut best_victory_clean_zero_cost = false;

    const MAX_ACTIONS_PER_TURN: usize = 64;
    let max_turns = policy.max_turns.max(1);
    let beam_width = profile.beam_width.max(1);
    let check_theft = policy.theft_policy == SolverTheftPolicy::PreserveResources;

    #[inline]
    fn push_worker_candidate_node(
        arena: &mut NodeArena,
        cand: &WorkerCandidate,
        state_handle: PackedHandle,
        choices_offset: u32,
        choices_len: u8,
    ) -> NodeId {
        let mut node_flags = 0u8;
        if cand.has_extra_end_turn {
            node_flags |= NODE_FLAG_HAS_EXTRA_END_TURN;
        }
        if cand.is_terminal {
            node_flags |= NODE_FLAG_IS_TERMINAL;
        }
        let rare_eval_idx = arena.push_opt_rare_eval(cand.rare_eval);
        let mut node = SearchNode {
            parent_raw: cand.parent_id.0,
            action: cand.action,
            por_key: cand.por_key,
            choices_offset,
            choices_len,
            state_handle,
            state_hash: cand.state_hash,
            beam_rank_score: cand.base_score,
            turn: cand.turn.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
            action_count: cand.action_count,
            potion_count: cand.potion_count.min(u8::MAX as u16) as u8,
            explicit_potion_count: cand.explicit_potion_count.min(u8::MAX as u16) as u8,
            optional_paid_potion_count: cand.optional_paid_potion_count.min(u8::MAX as u16) as u8,
            automatic_potion_count: cand.automatic_potion_count.min(u8::MAX as u16) as u8,
            used_potion_slot_mask: cand.used_potion_slot_mask,
            potion_strategic_cost: cand.potion_strategic_cost.clamp(i16::MIN as i32, i16::MAX as i32)
                as i16,
            optional_paid_potion_cost: cand
                .optional_paid_potion_cost
                .clamp(i16::MIN as i32, i16::MAX as i32)
                as i16,
            ambergris_count: cand.ambergris_count.min(u8::MAX as u16) as u8,
            forced_used_mask: cand.forced_used_mask,
            forced_ambergris_count: cand.forced_ambergris_count.min(u8::MAX as u16) as u8,
            node_flags,
            fast_eval: cand.fast_eval,
            rare_eval_idx,
            ..Default::default()
        };
        if let Some(ref full) = cand.full_eval {
            node.set_full_eval(full);
        }
        arena.push_node(node)
    }

    #[inline]
    #[allow(clippy::too_many_arguments)]
    fn commit_one_accepted(
        w_idx: usize,
        c_idx: usize,
        cand: &WorkerCandidate,
        slab: &LocalStateSlab,
        staged_choices: &ChoiceArena,
        root_baseline: &RootEvalBaseline,
        policy: &SearchPolicy,
        cert: &ClosureCertificate,
        incumbent_table: &PrimaryIncumbentTable,
        incumbent: &mut FrozenIncumbentBound,
        allow_paid_optional_potions: bool,
        check_theft: bool,
        options: &PassSearchOptions,
        arena: &mut NodeArena,
        turn_arena: &mut MainStateArena,
        completed: &mut Vec<NodeId>,
        next_step_candidates: &mut Vec<NodeId>,
        next_turn_candidates: &mut Vec<NodeId>,
        packed_state_bytes_total: &mut u64,
        has_victory: &mut bool,
        best_won_hp_lost: &mut Option<i32>,
        best_victory_turn: &mut Option<i32>,
        best_victory_key: &mut Option<(i64, i32, i32)>,
        best_victory_clean_zero_cost: &mut bool,
        hp_target_reached: &mut bool,
    ) {
        let state_handle = if cand.is_terminal {
            PackedHandle::default()
        } else if cand.turn_completed {
            let state_bytes = slab.get(cand.state_slab_idx, cand.state_off, cand.state_len);
            *packed_state_bytes_total += state_bytes.len() as u64;
            turn_arena.commit_slice(state_bytes)
        } else {
            PackedHandle {
                slab: w_idx as u32,
                offset: c_idx as u32,
                len: 0,
            }
        };
        let (choices_offset, choices_len) = arena.choices.extend_from_arena_slice(
            staged_choices,
            cand.choices_offset,
            cand.choices_len,
        );
        let nid = push_worker_candidate_node(arena, cand, state_handle, choices_offset, choices_len);

        if cand.is_terminal {
            if cand.fast_eval.won() {
                *best_won_hp_lost = Some(match *best_won_hp_lost {
                    Some(cur) => cur.min(cand.fast_eval.cumulative_hp_lost as i32),
                    None => cand.fast_eval.cumulative_hp_lost as i32,
                });
                record_victory_and_tighten(
                    root_baseline,
                    cert,
                    incumbent_table,
                    incumbent,
                    policy,
                    allow_paid_optional_potions,
                    &cand.fast_eval,
                    cand.turn,
                    cand.potion_count,
                    cand.explicit_potion_count,
                    cand.optional_paid_potion_count,
                    cand.potion_strategic_cost,
                    cand.optional_paid_potion_cost,
                    cand.forced_used_mask,
                    has_victory,
                    best_victory_turn,
                    best_victory_key,
                    best_victory_clean_zero_cost,
                );
                if options.early_turn_scout_depth == 0
                    && options.boss_tempo_scout_turns == 0
                    && has_reached_acceptable_battle_hp_loss_node(root_baseline, policy, arena, nid)
                    && (!options.allow_paid_optional_potions
                        || !PotionUsePolicy::can_any_paid_potion_improve_on(
                            true,
                            true,
                            cand.fast_eval.death_save_use_count as i32,
                            cand.fast_eval.player_hp as i32,
                            cand.fast_eval.player_max_hp as i32,
                            cand.used_potion_slot_mask,
                            cand.fast_eval.outstanding_stolen as i32,
                            cand.fast_eval.forfeits_gold(),
                            check_theft,
                            root_baseline,
                        ))
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

    let mut commit_wave_candidates =
        |scratches: &mut [WorkerScratch],
         tt: &mut ShardedParetoTT,
         arena: &mut NodeArena,
         turn_arena: &mut MainStateArena,
         incumbent: &mut FrozenIncumbentBound,
         completed: &mut Vec<NodeId>,
         next_step_candidates: &mut Vec<NodeId>,
         next_turn_candidates: &mut Vec<NodeId>,
         transition_count: &mut u64,
         choice_replay_attempts: &mut u64,
         incumbent_pruned: &mut u64,
         por_pruned: &mut u64,
         packed_state_bytes_total: &mut u64,
         has_victory: &mut bool,
         best_won_hp_lost: &mut Option<i32>,
         best_victory_turn: &mut Option<i32>,
         best_victory_key: &mut Option<(i64, i32, i32)>,
         best_victory_clean_zero_cost: &mut bool,
         hp_target_reached: &mut bool,
         parent_count: usize,
         parent_slices_valid: bool,
         parent_slices: &[AtomicU64],
         sort_list: &mut Vec<(usize, usize, (u32, u32, u32))>| {
            let mut total_cands = 0usize;
            for wo in scratches.iter() {
                total_cands += wo.candidates.len();
                *transition_count += wo.transitions;
                *choice_replay_attempts += wo.choice_replay_attempts;
                *incumbent_pruned += wo.incumbent_pruned;
                tt.branches_pruned += wo.tt_probe_pruned;
                *por_pruned += wo.por_pruned;
            }
            next_step_candidates.reserve(total_cands);

            if let Some(pool) = active_pool.filter(|_| total_cands >= 1024) {
                tt_flat_refs.clear();
                tt_flat_refs.reserve(total_cands);
                for b in tt_shard_buckets.iter_mut() {
                    b.clear();
                }
                if parent_slices_valid {
                    #[cfg(debug_assertions)]
                    let mut prev_sort_key: Option<(u32, u32, u32)> = None;
                    for slice_atom in &parent_slices[..parent_count] {
                        let raw = slice_atom.load(Ordering::Relaxed);
                        if raw == 0 {
                            continue;
                        }
                        let (w_idx, start, len) = unpack_parent_slice(raw);
                        let wo = &scratches[w_idx];
                        for c_idx in start..start + len {
                            let cand = &wo.candidates[c_idx];
                            #[cfg(debug_assertions)]
                            {
                                debug_assert!(
                                    prev_sort_key.is_none_or(|p| p <= cand.sort_key),
                                    "parent_slices commit out of sort_key order: {:?} > {:?}",
                                    prev_sort_key,
                                    cand.sort_key
                                );
                                prev_sort_key = Some(cand.sort_key);
                            }
                            let sort_idx = tt_flat_refs.len();
                            tt_flat_refs.push((w_idx, c_idx));
                            if !cand.is_terminal {
                                tt_shard_buckets[cand.state_hash.shard_index()].push((
                                    sort_idx,
                                    cand.state_hash,
                                    cand.label,
                                ));
                            }
                        }
                    }
                } else {
                    sort_list.clear();
                    for (w_idx, wo) in scratches.iter().enumerate() {
                        for (c_idx, cand) in wo.candidates.iter().enumerate() {
                            sort_list.push((w_idx, c_idx, cand.sort_key));
                        }
                    }
                    sort_list.sort_unstable_by_key(|&(_, _, sk)| sk);
                    for &(w_idx, c_idx, _) in sort_list.iter() {
                        let cand = &scratches[w_idx].candidates[c_idx];
                        let sort_idx = tt_flat_refs.len();
                        tt_flat_refs.push((w_idx, c_idx));
                        if !cand.is_terminal {
                            tt_shard_buckets[cand.state_hash.shard_index()].push((
                                sort_idx,
                                cand.state_hash,
                                cand.label,
                            ));
                        }
                    }
                }
                tt_accepted_flags.resize(tt_flat_refs.len(), false);
                pool.install(|| {
                    tt.insert_sharded_batch_flags(
                        tt_shard_buckets,
                        tt_shard_accepted,
                        true,
                        tt_accepted_flags,
                    );
                });
                for (sort_idx, &(w_idx, c_idx)) in tt_flat_refs.iter().enumerate() {
                    let wo = &scratches[w_idx];
                    let cand = &wo.candidates[c_idx];
                    if cand.is_terminal || tt_accepted_flags[sort_idx] {
                        commit_one_accepted(
                            w_idx,
                            c_idx,
                            cand,
                            &wo.slab,
                            &wo.staged_choices,
                            &root_baseline,
                            policy,
                            cert,
                            incumbent_table,
                            incumbent,
                            options.allow_paid_optional_potions,
                            check_theft,
                            &options,
                            arena,
                            turn_arena,
                            completed,
                            next_step_candidates,
                            next_turn_candidates,
                            packed_state_bytes_total,
                            has_victory,
                            best_won_hp_lost,
                            best_victory_turn,
                            best_victory_key,
                            best_victory_clean_zero_cost,
                            hp_target_reached,
                        );
                    }
                }
            } else if parent_slices_valid {
                #[cfg(debug_assertions)]
                let mut prev_sort_key: Option<(u32, u32, u32)> = None;
                for slice_atom in &parent_slices[..parent_count] {
                    let raw = slice_atom.load(Ordering::Relaxed);
                    if raw == 0 {
                        continue;
                    }
                    let (w_idx, start, len) = unpack_parent_slice(raw);
                    let wo = &scratches[w_idx];
                    for c_idx in start..start + len {
                        let cand = &wo.candidates[c_idx];
                        #[cfg(debug_assertions)]
                        {
                            debug_assert!(
                                prev_sort_key.is_none_or(|p| p <= cand.sort_key),
                                "parent_slices commit out of sort_key order: {:?} > {:?}",
                                prev_sort_key,
                                cand.sort_key
                            );
                            prev_sort_key = Some(cand.sort_key);
                        }
                        if cand.is_terminal || tt.try_insert_single(cand.state_hash, cand.label) {
                            commit_one_accepted(
                                w_idx,
                                c_idx,
                                cand,
                                &wo.slab,
                                &wo.staged_choices,
                                &root_baseline,
                                policy,
                                cert,
                                incumbent_table,
                                incumbent,
                                options.allow_paid_optional_potions,
                                check_theft,
                                &options,
                                arena,
                                turn_arena,
                                completed,
                                next_step_candidates,
                                next_turn_candidates,
                                packed_state_bytes_total,
                                has_victory,
                                best_won_hp_lost,
                                best_victory_turn,
                                best_victory_key,
                                best_victory_clean_zero_cost,
                                hp_target_reached,
                            );
                        }
                    }
                }
            } else {
                sort_list.clear();
                for (w_idx, wo) in scratches.iter().enumerate() {
                    for (c_idx, cand) in wo.candidates.iter().enumerate() {
                        sort_list.push((w_idx, c_idx, cand.sort_key));
                    }
                }
                sort_list.sort_unstable_by_key(|&(_, _, sk)| sk);
                for &(w_idx, c_idx, _) in sort_list.iter() {
                    let wo = &scratches[w_idx];
                    let cand = &wo.candidates[c_idx];
                    if cand.is_terminal || tt.try_insert_single(cand.state_hash, cand.label) {
                        commit_one_accepted(
                            w_idx,
                            c_idx,
                            cand,
                            &wo.slab,
                            &wo.staged_choices,
                            &root_baseline,
                            policy,
                            cert,
                            incumbent_table,
                            incumbent,
                            options.allow_paid_optional_potions,
                            check_theft,
                            &options,
                            arena,
                            turn_arena,
                            completed,
                            next_step_candidates,
                            next_turn_candidates,
                            packed_state_bytes_total,
                            has_victory,
                            best_won_hp_lost,
                            best_victory_turn,
                            best_victory_key,
                            best_victory_clean_zero_cost,
                            hp_target_reached,
                        );
                    }
                }
            }
        };

    // Depth-0 TurnSetup expansion when root state starts paused on a pending choice
    // (e.g., GamblingChip / ToolsOfTheTrade / HistoryCourse / Dread / Entropy / Toolbox).
    if !arena.get(root_id).is_terminal() && start_state.pending.is_some() {
        for s in scratches.iter_mut() {
            s.reset_for_wave();
        }
        let filled_at_start = start_state
            .player
            .potion_slots
            .iter()
            .filter(|s| s.is_some())
            .count()
            .min(u8::MAX as usize) as u8;
        let setup_task = ActionTask {
            parent_rank: 0,
            parent_id: root_id,
            parent_turn: start_state.player.turn_number,
            parent_action_count: prefix_len,
            parent_potion_count: prefix_potions,
            parent_explicit_potion_count: prefix_potions,
            parent_optional_paid_potion_count: prefix_optional_paid_potions,
            parent_automatic_potion_count: 0,
            parent_filled_potions: filled_at_start,
            parent_used_potion_slot_mask: prefix_used_slot_mask,
            parent_potion_cost: prefix_potion_cost,
            parent_optional_paid_potion_cost: prefix_optional_paid_cost,
            parent_ambergris_count: prefix_ambergris,
            parent_forced_used_mask: prefix_forced_mask,
            parent_forced_ambergris_count: prefix_forced_amb,
            parent_has_choice_lookahead: false,
            parent_choice_lookahead_score: None,
            action_ord: 0,
            spec: ActionSpec::TurnSetup,
        };
        let frozen_setup_inc = *incumbent;
        let mut setup_hp_target = false;
        if parent_slices.is_empty() {
            parent_slices.push(AtomicU64::new(0));
        }
        execute_action_from_parent(
            &mut scratches[0],
            &start_state,
            &setup_task,
            &root.static_ctx,
            content,
            &root_baseline,
            policy,
            profile,
            cert,
            &frozen_setup_inc,
            tt,
            choice_probe_cache,
            &sensitivity,
            options.allow_paid_optional_potions,
            options.eval_mode,
        );
        let setup_len = scratches[0].candidates.len();
        parent_slices[0].store(
            if setup_len > 0 {
                pack_parent_slice(0, 0, setup_len)
            } else {
                0
            },
            Ordering::Relaxed,
        );
        next_step_candidates.clear();
        turn_setup_ended_turn.clear();
        commit_wave_candidates(
            scratches,
            tt,
            arena,
            turn_staging_arena,
            incumbent,
            &mut completed,
            &mut next_step_candidates,
            &mut turn_setup_ended_turn,
            &mut transition_count,
            &mut choice_replay_attempts,
            &mut incumbent_pruned,
            &mut por_pruned,
            &mut packed_state_bytes_total,
            &mut has_victory,
            &mut best_won_hp_lost,
            &mut best_victory_turn,
            &mut best_victory_key,
            &mut best_victory_clean_zero_cost,
            &mut setup_hp_target,
            1,
            true,
            parent_slices,
            sort_list,
        );

        current_turn_frontier.clear();
        if setup_hp_target {
            termination = "HpTarget";
        } else if !next_step_candidates.is_empty() {
            next_step_arena.clear();
            for &nid in &next_step_candidates {
                let tmp_h = arena.get(nid).state_handle;
                let wo = &scratches[tmp_h.slab as usize];
                let cand = &wo.candidates[tmp_h.offset as usize];
                let bytes = wo.slab.get(cand.state_slab_idx, cand.state_off, cand.state_len);
                packed_state_bytes_total += bytes.len() as u64;
                arena.get_mut(nid).state_handle = next_step_arena.commit_slice(bytes);
            }
            std::mem::swap(step_arena, next_step_arena);
            next_step_arena.clear();

            let mut use_setup_rollout = false;
            let has_time_for_setup_rollout = profile.soft_time_budget_ms == 0
                || (started.elapsed().as_millis() as u64) < profile.soft_time_budget_ms;
            if next_step_candidates.len() > beam_width
                && options.rollout_horizon_turns > 0
                && !cancel.load(Ordering::Relaxed)
                && has_time_for_setup_rollout
            {
                let shortlist_cap = next_step_candidates.len().min(beam_width * 2);
                if next_step_candidates.len() > shortlist_cap {
                    BeamRetentionPolicy::prune_wave_with_rollout_into(
                        arena,
                        &next_step_candidates,
                        shortlist_cap,
                        profile.potion_free_beam_width,
                        profile.potion_beam_width,
                        &root_baseline,
                        profile.second_rank_band,
                        profile.aggressive_power_commitment,
                        false,
                        false,
                        active_pool,
                        &mut shortlist_buf,
                        prune_scratch,
                    );
                } else {
                    shortlist_buf.clear();
                    shortlist_buf.extend_from_slice(&next_step_candidates);
                }
                let rollout_deadline = (profile.soft_time_budget_ms > 0)
                    .then(|| started + Duration::from_millis(profile.soft_time_budget_ms));
                evaluate_turn_boundary_rollouts_into(
                    step_arena,
                    arena,
                    &shortlist_buf,
                    &root.static_ctx,
                    content,
                    &root_baseline,
                    policy,
                    profile,
                    options.eval_mode,
                    2,
                    rollout_deadline,
                    active_pool,
                    cancel,
                    rollout_workspaces,
                    Some(turn_rollout_cache),
                    rollout_results,
                );
                for (idx, maybe_rr) in rollout_results.drain(..).enumerate() {
                    let Some(rr) = maybe_rr else { continue };
                    let pid = shortlist_buf[idx];
                    arena.get_mut(pid).set_rollout_score(rr.rollout_score);
                    if let Some(pv) = rr.promoted_victory {
                        let pnode = arena.get(pid).clone();
                        let mut cur_parent = pid;
                        let total_acts = pv.steps.len();
                        let mut cur_turn = pnode.turn as i32;
                        let mut final_nid = pid;
                        for (step_idx, step) in pv.steps.iter().enumerate() {
                            let is_last = step_idx + 1 == total_acts;
                            let (kind, step_turn) = if step.is_end_turn != 0 {
                                let t = cur_turn;
                                cur_turn += 1;
                                (CompactActionKind::EndTurn, t)
                            } else {
                                (
                                    CompactActionKind::PlayCard {
                                        card_handle: step.card_handle,
                                        target_handle: if step.target_handle == u32::MAX {
                                            None
                                        } else {
                                            Some(step.target_handle)
                                        },
                                    },
                                    cur_turn,
                                )
                            };
                            let (c_off, c_len) = arena.choices.push_raw_slab_slice(
                                &pv.choices.steps_meta,
                                &pv.choices.indices,
                                step.choices_offset,
                                step.choices_len,
                            );
                            let node_turn = if is_last { pv.final_turn } else { step_turn };
                            let rare_eval_idx = if is_last {
                                arena.push_rare_eval(RareNodeEval {
                                    growth_rewards: pv.final_fast.growth_rewards,
                                    relic_eval: pv.final_fast.relic_eval,
                                })
                            } else {
                                pnode.rare_eval_idx
                            };
                            let (step_action, step_por_key) =
                                CompactAction::new(kind, step_turn, PorActionClass::Barrier);
                            let nid = arena.push_node(SearchNode {
                                parent_raw: cur_parent.0,
                                action: step_action,
                                por_key: step_por_key,
                                choices_offset: c_off,
                                choices_len: c_len,
                                state_handle: PackedHandle::default(),
                                state_hash: if is_last {
                                    pv.final_state_hash
                                } else {
                                    pnode.state_hash
                                },
                                beam_rank_score: if is_last {
                                    pv.final_fast.base_score
                                } else {
                                    pnode.beam_rank_score
                                },
                                turn: node_turn.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
                                action_count: pnode.action_count + step_idx as u16 + 1,
                                potion_count: if is_last {
                                    (pnode.potion_count as u16 + pv.auto_potions_used)
                                        .min(u8::MAX as u16) as u8
                                } else {
                                    pnode.potion_count
                                },
                                explicit_potion_count: pnode.explicit_potion_count,
                                optional_paid_potion_count: pnode.optional_paid_potion_count,
                                automatic_potion_count: if is_last {
                                    (pnode.automatic_potion_count as u16 + pv.auto_potions_used)
                                        .min(u8::MAX as u16) as u8
                                } else {
                                    pnode.automatic_potion_count
                                },
                                used_potion_slot_mask: pnode.used_potion_slot_mask,
                                potion_strategic_cost: pnode.potion_strategic_cost,
                                optional_paid_potion_cost: pnode.optional_paid_potion_cost,
                                ambergris_count: pnode.ambergris_count,
                                forced_used_mask: pnode.forced_used_mask,
                                forced_ambergris_count: pnode.forced_ambergris_count,
                                node_flags: if is_last { NODE_FLAG_IS_TERMINAL } else { 0 },
                                fast_eval: if is_last {
                                    StoredFastEval::from(&pv.final_fast)
                                } else {
                                    pnode.fast_eval
                                },
                                rare_eval_idx,
                                ..Default::default()
                            });
                            cur_parent = nid;
                            final_nid = nid;
                        }
                        let promoted_fast = StoredFastEval::from(&pv.final_fast);
                        record_victory_and_tighten(
                            &root_baseline,
                            cert,
                            incumbent_table,
                            incumbent,
                            policy,
                            options.allow_paid_optional_potions,
                            &promoted_fast,
                            pv.final_turn,
                            pnode.potion_count as u16 + pv.auto_potions_used,
                            pnode.explicit_potion_count as u16,
                            pnode.optional_paid_potion_count as u16,
                            pnode.potion_strategic_cost as i32,
                            pnode.optional_paid_potion_cost as i32,
                            pnode.forced_used_mask,
                            &mut has_victory,
                            &mut best_victory_turn,
                            &mut best_victory_key,
                            &mut best_victory_clean_zero_cost,
                        );
                        if pv.final_fast.won {
                            best_won_hp_lost = Some(match best_won_hp_lost {
                                Some(cur) => cur.min(pv.final_fast.cumulative_hp_lost),
                                None => pv.final_fast.cumulative_hp_lost,
                            });
                        }
                        completed.push(final_nid);
                    }
                }
                std::mem::swap(&mut next_step_candidates, &mut shortlist_buf);
                shortlist_buf.clear();
                use_setup_rollout = true;
            }
            BeamRetentionPolicy::prune_wave_with_rollout_into(
                arena,
                &next_step_candidates,
                beam_width,
                profile.potion_free_beam_width,
                profile.potion_beam_width,
                &root_baseline,
                profile.second_rank_band,
                profile.aggressive_power_commitment,
                false,
                use_setup_rollout,
                active_pool,
                &mut current_turn_frontier,
                prune_scratch,
            );
        }
    }

    for turn_layer in 0..max_turns {
        if termination == "HpTarget" {
            break;
        }
        if current_turn_frontier.is_empty() && (turn_layer > 0 || turn_setup_ended_turn.is_empty()) {
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

        if turn_layer > 0 {
            turn_staging_arena.clear();
        }
        active_frontier.clear();
        active_frontier.append(&mut current_turn_frontier);
        next_turn_candidates.clear();
        if turn_layer == 0 && !turn_setup_ended_turn.is_empty() {
            next_turn_candidates.append(&mut turn_setup_ended_turn);
        }
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

            let frozen_incumbent = *incumbent;
            let parent_slices_valid = expand_wave_tasks(
                parent_slice,
                dop,
                active_pool,
                &root.static_ctx,
                arena,
                step_arena,
                content,
                &root_baseline,
                policy,
                profile,
                cert,
                &frozen_incumbent,
                tt,
                choice_probe_cache,
                &sensitivity,
                options.allow_paid_optional_potions,
                options.eval_mode,
                cancel,
                scratches,
                parent_states,
                flat_tasks,
                parent_slices,
            );

            next_step_candidates.clear();
            commit_wave_candidates(
                scratches,
                tt,
                arena,
                turn_staging_arena,
                incumbent,
                &mut completed,
                &mut next_step_candidates,
                &mut next_turn_candidates,
                &mut transition_count,
                &mut choice_replay_attempts,
                &mut incumbent_pruned,
                &mut por_pruned,
                &mut packed_state_bytes_total,
                &mut has_victory,
                &mut best_won_hp_lost,
                &mut best_victory_turn,
                &mut best_victory_key,
                &mut best_victory_clean_zero_cost,
                &mut hp_target_reached,
                parent_slice.len(),
                parent_slices_valid,
                parent_slices,
                sort_list,
            );

            if hp_target_reached {
                termination = "HpTarget";
                break;
            }

            BeamRetentionPolicy::prune_wave_with_potion_bias_into(
                arena,
                &next_step_candidates,
                beam_width,
                profile.potion_free_beam_width,
                profile.potion_beam_width,
                &root_baseline,
                profile.second_rank_band,
                profile.aggressive_power_commitment,
                options.prefer_potion_buckets,
                active_pool,
                &mut active_frontier,
                prune_scratch,
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
                        choice_replay_attempts,
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
                arena,
                16,
                options.prefer_potion_buckets,
            );
            let mut uncached_sp_ids: SmallVec<[NodeId; 16]> = SmallVec::new();
            for nid in sp_ids {
                let h = arena.get(nid).state_hash;
                if stand_pat_seen.insert(h) {
                    uncached_sp_ids.push(nid);
                }
            }
            if !uncached_sp_ids.is_empty() {
                for s in scratches.iter_mut() {
                    s.reset_for_wave();
                }
                let frozen_incumbent = *incumbent;
                let sp_slice = uncached_sp_ids.as_slice();
                if parent_slices.len() < sp_slice.len() {
                    parent_slices.resize_with(sp_slice.len(), || AtomicU64::new(0));
                }
                for slot in &parent_slices[..sp_slice.len()] {
                    slot.store(0, Ordering::Relaxed);
                }
                if let Some(pool) = active_pool.filter(|_| sp_slice.len() > 2) {
                    let workers = dop.min(sp_slice.len()).min(scratches.len()).min(parent_states.len());
                    let next_idx = AtomicUsize::new(0);
                    let arena_ref: &NodeArena = &*arena;
                    let step_arena_ref: &MainStateArena = &*step_arena;
                    let tt_ref: &ShardedParetoTT = &*tt;
                    let probe_cache_ref: &ChoiceProbeCache = &*choice_probe_cache;
                    let rb_ref: &RootEvalBaseline = &root_baseline;
                    let static_ctx_ref = &root.static_ctx;
                    let sens_ref = &sensitivity;
                    let slices_ref = &parent_slices[..sp_slice.len()];
                    pool.scope(|scope| {
                        for (w_idx, (scratch, parent_state)) in scratches
                            .iter_mut()
                            .zip(parent_states.iter_mut())
                            .take(workers)
                            .enumerate()
                        {
                            let next_ref = &next_idx;
                            scope.spawn(move |_| loop {
                                if cancel.load(Ordering::Relaxed) {
                                    break;
                                }
                                let rank = next_ref.fetch_add(1, Ordering::Relaxed);
                                if rank >= sp_slice.len() {
                                    break;
                                }
                                let nid = sp_slice[rank];
                                let pnode = arena_ref.get(nid);
                                let pbytes = step_arena_ref.get(pnode.state_handle);
                                if unpack_state(pbytes, static_ctx_ref, parent_state)
                                    .is_err()
                                {
                                    continue;
                                }
                                let filled = parent_state
                                    .player
                                    .potion_slots
                                    .iter()
                                    .filter(|s| s.is_some())
                                    .count()
                                    .min(u8::MAX as usize) as u8;
                                let task =
                                    make_stand_pat_task(rank as u32, nid, pnode, filled, 0);
                                let c_start = scratch.candidates.len();
                                execute_action_from_parent(
                                    scratch,
                                    parent_state,
                                    &task,
                                    static_ctx_ref,
                                    content,
                                    rb_ref,
                                    policy,
                                    profile,
                                    cert,
                                    &frozen_incumbent,
                                    tt_ref,
                                    probe_cache_ref,
                                    sens_ref,
                                    options.allow_paid_optional_potions,
                                    options.eval_mode,
                                );
                                let c_len = scratch.candidates.len() - c_start;
                                if c_len > 0 {
                                    slices_ref[rank].store(
                                        pack_parent_slice(w_idx, c_start, c_len),
                                        Ordering::Relaxed,
                                    );
                                }
                            });
                        }
                    });
                } else {
                    for (rank, &nid) in sp_slice.iter().enumerate() {
                        let pnode = arena.get(nid).clone();
                        let pbytes = step_arena.get(pnode.state_handle);
                        if unpack_state(pbytes, &root.static_ctx, &mut parent_states[0])
                            .is_err()
                        {
                            continue;
                        }
                        let filled = parent_states[0]
                            .player
                            .potion_slots
                            .iter()
                            .filter(|s| s.is_some())
                            .count()
                            .min(u8::MAX as usize) as u8;
                        let task = make_stand_pat_task(rank as u32, nid, &pnode, filled, 0);
                        let c_start = scratches[0].candidates.len();
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
                            &frozen_incumbent,
                            tt,
                            choice_probe_cache,
                            &sensitivity,
                            options.allow_paid_optional_potions,
                            options.eval_mode,
                        );
                        let c_len = scratches[0].candidates.len() - c_start;
                        if c_len > 0 {
                            parent_slices[rank].store(
                                pack_parent_slice(0, c_start, c_len),
                                Ordering::Relaxed,
                            );
                        }
                    }
                }
                sp_mid_unused.clear();
                commit_wave_candidates(
                    scratches,
                    tt,
                    arena,
                    turn_staging_arena,
                    incumbent,
                    &mut completed,
                    &mut sp_mid_unused,
                    &mut next_turn_candidates,
                    &mut transition_count,
                    &mut choice_replay_attempts,
                    &mut incumbent_pruned,
                    &mut por_pruned,
                    &mut packed_state_bytes_total,
                    &mut has_victory,
                    &mut best_won_hp_lost,
                    &mut best_victory_turn,
                    &mut best_victory_key,
                    &mut best_victory_clean_zero_cost,
                    &mut hp_target_reached,
                    sp_slice.len(),
                    true,
                    parent_slices,
                    sort_list,
                );
                if hp_target_reached {
                    termination = "HpTarget";
                }
            }
        }

        if !active_frontier.is_empty() {
            std::mem::swap(&mut last_active_frontier, &mut active_frontier);
            active_frontier.clear();
        }

        if !next_turn_candidates.is_empty()
            && (!aborted_limit || latest_completed_turn_nodes.is_empty())
        {
            latest_completed_turn_nodes.clear();
            latest_completed_turn_nodes.extend(
                next_turn_candidates
                    .iter()
                    .copied()
                    .filter(|&nid| {
                        let n = arena.get(nid);
                        !n.fast_eval.player_dead() && n.fast_eval.projected_player_hp > 0
                    }),
            );
        }

        let completed_turns = turn_layer + 1;
        if options.early_turn_scout_depth > 0 && completed_turns <= options.early_turn_scout_depth
        {
            let f = select_early_turn_frontier(
                arena,
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
                if !n.fast_eval.player_dead() {
                    let eliminated = (root_baseline.initial_enemy_count as i32)
                        - (n.fast_eval.alive_enemy_count as i32);
                    let rank = n.beam_rank_score_or_base();
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
            if next_turn_candidates.len() > shortlist_cap {
                BeamRetentionPolicy::prune_wave_with_rollout_into(
                    arena,
                    &next_turn_candidates,
                    shortlist_cap,
                    profile.potion_free_beam_width,
                    profile.potion_beam_width,
                    &root_baseline,
                    profile.second_rank_band,
                    profile.aggressive_power_commitment,
                    options.prefer_potion_buckets,
                    false,
                    active_pool,
                    &mut shortlist_buf,
                    prune_scratch,
                );
            } else {
                shortlist_buf.clear();
                shortlist_buf.extend_from_slice(&next_turn_candidates);
            }
            let rollout_deadline = (profile.soft_time_budget_ms > 0)
                .then(|| started + Duration::from_millis(profile.soft_time_budget_ms));
            evaluate_turn_boundary_rollouts_into(
                turn_staging_arena,
                arena,
                &shortlist_buf,
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
                Some(turn_rollout_cache),
                rollout_results,
            );
            for (idx, maybe_rr) in rollout_results.drain(..).enumerate() {
                let Some(rr) = maybe_rr else { continue };
                let pid = shortlist_buf[idx];
                arena.get_mut(pid).set_rollout_score(rr.rollout_score);
                if let Some(pv) = rr.promoted_victory {
                    let pnode = arena.get(pid).clone();
                    let mut cur_parent = pid;
                    let mut cur_turn = pnode.turn as i32;
                    let total_acts = pv.steps.len();
                    let mut final_nid = pid;
                    for (step_idx, step) in pv.steps.iter().enumerate() {
                        let is_last = step_idx + 1 == total_acts;
                        let (kind, step_turn) = if step.is_end_turn != 0 {
                            let t = cur_turn;
                            cur_turn += 1;
                            (CompactActionKind::EndTurn, t)
                        } else {
                            (
                                CompactActionKind::PlayCard {
                                    card_handle: step.card_handle,
                                    target_handle: if step.target_handle == u32::MAX {
                                        None
                                    } else {
                                        Some(step.target_handle)
                                    },
                                },
                                cur_turn,
                            )
                        };
                        let (c_off, c_len) = arena.choices.push_raw_slab_slice(
                            &pv.choices.steps_meta,
                            &pv.choices.indices,
                            step.choices_offset,
                            step.choices_len,
                        );
                        let node_turn = if is_last { pv.final_turn } else { step_turn };
                        let rare_eval_idx = if is_last {
                            arena.push_rare_eval(RareNodeEval {
                                growth_rewards: pv.final_fast.growth_rewards,
                                relic_eval: pv.final_fast.relic_eval,
                            })
                        } else {
                            pnode.rare_eval_idx
                        };
                        let (step_action, step_por_key) =
                            CompactAction::new(kind, step_turn, PorActionClass::Barrier);
                        let nid = arena.push_node(SearchNode {
                            parent_raw: cur_parent.0,
                            action: step_action,
                            por_key: step_por_key,
                            choices_offset: c_off,
                            choices_len: c_len,
                            state_handle: PackedHandle::default(),
                            state_hash: if is_last {
                                pv.final_state_hash
                            } else {
                                pnode.state_hash
                            },
                            beam_rank_score: if is_last {
                                pv.final_fast.base_score
                            } else {
                                pnode.beam_rank_score
                            },
                            turn: node_turn.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
                            action_count: pnode.action_count + step_idx as u16 + 1,
                            potion_count: if is_last {
                                (pnode.potion_count as u16 + pv.auto_potions_used)
                                    .min(u8::MAX as u16) as u8
                            } else {
                                pnode.potion_count
                            },
                            explicit_potion_count: pnode.explicit_potion_count,
                            optional_paid_potion_count: pnode.optional_paid_potion_count,
                            automatic_potion_count: if is_last {
                                (pnode.automatic_potion_count as u16 + pv.auto_potions_used)
                                    .min(u8::MAX as u16) as u8
                            } else {
                                pnode.automatic_potion_count
                            },
                            used_potion_slot_mask: pnode.used_potion_slot_mask,
                            potion_strategic_cost: pnode.potion_strategic_cost,
                            optional_paid_potion_cost: pnode.optional_paid_potion_cost,
                            ambergris_count: pnode.ambergris_count,
                            forced_used_mask: pnode.forced_used_mask,
                            forced_ambergris_count: pnode.forced_ambergris_count,
                            node_flags: if is_last { NODE_FLAG_IS_TERMINAL } else { 0 },
                            fast_eval: if is_last {
                                StoredFastEval::from(&pv.final_fast)
                            } else {
                                pnode.fast_eval
                            },
                            rare_eval_idx,
                            ..Default::default()
                        });
                        cur_parent = nid;
                        final_nid = nid;
                    }
                    let promoted_fast = StoredFastEval::from(&pv.final_fast);
                    record_victory_and_tighten(
                        &root_baseline,
                        cert,
                        incumbent_table,
                        incumbent,
                        policy,
                        options.allow_paid_optional_potions,
                        &promoted_fast,
                        pv.final_turn,
                        pnode.potion_count as u16 + pv.auto_potions_used,
                        pnode.explicit_potion_count as u16,
                        pnode.optional_paid_potion_count as u16,
                        pnode.potion_strategic_cost as i32,
                        pnode.optional_paid_potion_cost as i32,
                        pnode.forced_used_mask,
                        &mut has_victory,
                        &mut best_victory_turn,
                        &mut best_victory_key,
                        &mut best_victory_clean_zero_cost,
                    );
                    if has_reached_acceptable_battle_hp_loss_node(
                        &root_baseline,
                        policy,
                        arena,
                        final_nid,
                    ) && (!options.allow_paid_optional_potions
                        || !PotionUsePolicy::can_any_paid_potion_improve_on(
                            true,
                            true,
                            pv.final_fast.death_save_use_count,
                            pv.final_fast.player_hp,
                            pv.final_fast.player_max_hp,
                            pnode.used_potion_slot_mask,
                            pv.final_fast.outstanding_stolen,
                            pv.final_fast.forfeits_gold,
                            check_theft,
                            &root_baseline,
                        ))
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
            std::mem::swap(&mut next_turn_candidates, &mut shortlist_buf);
            shortlist_buf.clear();
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

        BeamRetentionPolicy::prune_wave_with_rollout_into(
            arena,
            &next_turn_candidates,
            beam_width,
            profile.potion_free_beam_width,
            profile.potion_beam_width,
            &root_baseline,
            profile.second_rank_band,
            profile.aggressive_power_commitment,
            options.prefer_potion_buckets,
            ran_rollout,
            active_pool,
            &mut current_turn_frontier,
            prune_scratch,
        );

        let mut layer_progressed = false;
        let mut enemy_hp_decreased = false;
        for &nid in &current_turn_frontier {
            let n = arena.get(nid);
            let f = &n.fast_eval;
            let rem_enemies = (f.alive_enemy_count as i32) + (f.reviving_enemy_count as i32);
            if f.enemy_hp < best_seen_enemy_hp {
                best_seen_enemy_hp = f.enemy_hp;
                layer_progressed = true;
                enemy_hp_decreased = true;
            }
            if rem_enemies < best_seen_alive_enemies {
                best_seen_alive_enemies = rem_enemies;
                layer_progressed = true;
            }
            if n.has_full_eval() {
                if (n.max_dark_evoke as i32) > best_seen_max_dark_evoke {
                    best_seen_max_dark_evoke = n.max_dark_evoke as i32;
                    layer_progressed = true;
                }
                if (n.total_poison_and_doom as i32) > best_seen_poison_and_doom {
                    best_seen_poison_and_doom = n.total_poison_and_doom as i32;
                    layer_progressed = true;
                }
                if (n.board_investment as i32) > best_seen_board_investment {
                    best_seen_board_investment = n.board_investment as i32;
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
        stand_pat_seen.clear();
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
        arena,
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
        end_player_max_hp,
        all_forced_uses_satisfied,
        strategic_hp_deficit,
        net_persistent_hp_delta_centi,
        net_potion_opportunity_cost_centi,
        strategy_goal_hp_credit,
        strategy_goal_count,
        growth_rewards,
        relic_eval,
        recovered_hp,
        hp_lost,
        potion_count,
        optional_potion_count,
        optional_paid_potion_count,
        optional_potion_strategic_cost,
        optional_ambergris_count,
        used_potion_slot_mask,
        alchemize_count,
        empty_potion_slots,
        outstanding_stolen,
        forfeits_gold,
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
                sel.end_player_max_hp,
                sel.all_forced_uses_satisfied,
                sel.strategic_hp_deficit,
                sel.net_persistent_hp_delta_centi,
                sel.net_potion_opportunity_cost_centi,
                sel.strategy_goal_hp_credit,
                sel.strategy_goal_count,
                sel.growth_rewards,
                sel.relic_eval,
                sel.recovered_hp,
                sel.hp_lost,
                sel.potion_count,
                sel.optional_potion_count,
                sel.optional_paid_potion_count,
                sel.optional_potion_strategic_cost,
                sel.optional_ambergris_count,
                sel.used_potion_slot_mask,
                sel.alchemize_count,
                sel.empty_potion_slots,
                sel.outstanding_stolen,
                sel.forfeits_gold,
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
            root_baseline.initial_player_max_hp,
            root_baseline.all_forced_potion_mask == 0,
            i32::MAX,
            i32::MIN / 2,
            0,
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
            0,
            0,
            root_baseline.empty_root_potion_slots,
            0,
            false,
            0,
            Vec::new(),
        ),
    };

    let (has_completed_turn_ongoing, completed_ongoing_turn) = match best_node {
        Some(nid) if !won && survives && !latest_completed_turn_nodes.is_empty() => {
            (true, arena.get(nid).turn as i32)
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
    let nodes_in_arena = arena.len();

    Ok(BeamRunOutput {
        best_node,
        best_turn,
        nodes_in_arena,
        root_baseline,
        won,
        survives,
        death_save_use_count,
        player_hp,
        end_player_max_hp,
        all_forced_uses_satisfied,
        strategic_hp_deficit,
        net_persistent_hp_delta_centi,
        net_potion_opportunity_cost_centi,
        strategy_goal_hp_credit,
        strategy_goal_count,
        growth_rewards,
        relic_eval,
        recovered_hp,
        hp_lost,
        potion_count,
        explicit_potion_count,
        optional_potion_count,
        optional_paid_potion_count,
        optional_potion_strategic_cost,
        optional_ambergris_count,
        used_potion_slot_mask,
        alchemize_count,
        empty_potion_slots,
        outstanding_stolen,
        forfeits_gold,
        has_completed_turn_ongoing,
        completed_ongoing_turn,
        score,
        actions,
        expanded_nodes,
        transition_count,
        choice_replay_attempts,
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
    _incumbent_table: &PrimaryIncumbentTable,
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
        let tt = ShardedParetoTT::new(policy.transposition_entry_limit.max(65_536));
        let probe_cache = ChoiceProbeCache::new();
    let frozen_incumbent = FrozenIncumbentBound::default();

    pack_state(root, &mut scratch.pack_buf);
    let root_hash = hash_canonical_key(root);
    let root_fast = evaluate_fast(root, content, &root_baseline, None);
    let root_full = evaluate_full(root, content, &root_fast, &root_baseline);
    let root_rare_idx = arena.push_rare_eval(RareNodeEval {
        growth_rewards: root_fast.growth_rewards,
        relic_eval: root_fast.relic_eval,
    });
    let root_terminal = !root.is_in_progress || root_fast.won || root_fast.player_dead;
    let mut root_node = SearchNode {
        state_handle: main_arena.commit_slice(&scratch.pack_buf),
        state_hash: root_hash,
        beam_rank_score: root_fast.base_score,
        turn: root
            .player
            .turn_number
            .clamp(i16::MIN as i32, i16::MAX as i32) as i16,
        node_flags: if root_terminal {
            NODE_FLAG_IS_TERMINAL
        } else {
            0
        },
        fast_eval: StoredFastEval::from(&root_fast),
        rare_eval_idx: root_rare_idx,
        ..Default::default()
    };
    root_node.set_full_eval(&root_full.to_stored());
    let root_id = arena.push_node(root_node);

    let mut open = BfwsBoundedOpen::new(options.max_open);
    let mut novelty = BfwsPackedNovelty::new(options.width, options.max_novelty_entries);
    let mut escapes: Vec<BfwsEscapeBudget> = Vec::new();
    let mut facts_buf: Vec<BfwsFact> = Vec::with_capacity(64);
    let mut completed = Vec::new();
    let mut sequence = 0u64;

    capture_novelty_facts(root, &root_fast, &root_full, &mut facts_buf);
    let w0 = novelty.evaluate((root_fast.enemy_hp / 10, 0), &facts_buf);
    if arena.get(root_id).is_terminal() {
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
            || (pnode.turn as i32) >= root_baseline.start_turn_number + options.max_turns
        {
            continue;
        }

        expanded_nodes += 1;
        let pbytes = main_arena.get(pnode.state_handle);
        if unpack_state(pbytes, &root.static_ctx, &mut parent_state).is_err() {
            continue;
        }
        let p_has_la = pnode.has_choice_lookahead();
        let p_la_score = pnode.choice_lookahead_score();
        let pfast = evaluate_fast(&parent_state, content, &root_baseline, None);
        let pfull = evaluate_full(&parent_state, content, &pfast, &root_baseline);
        capture_novelty_facts(&parent_state, &pfast, &pfull, &mut facts_buf);
        novelty.prepare_parent(
            (pnode.fast_eval.enemy_hp / 10, pnode.potion_count as i32),
            &facts_buf,
        );

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
            profile,
            &sensitivity,
            &root_baseline,
            true,
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
                &frozen_incumbent,
                &tt,
                &probe_cache,
                &sensitivity,
                true,
                EvalMode::Balanced,
            );
        }

        let children: Vec<WorkerCandidate> = scratch.candidates.drain(..).collect();
        for cand in children {
            let (choices_offset, choices_len) = arena.choices.extend_from_arena_slice(
                &scratch.staged_choices,
                cand.choices_offset,
                cand.choices_len,
            );
            if cand.is_terminal {
                let mut node_flags = NODE_FLAG_IS_TERMINAL;
                if cand.has_extra_end_turn {
                    node_flags |= NODE_FLAG_HAS_EXTRA_END_TURN;
                }
                let rare_eval_idx = arena.push_opt_rare_eval(cand.rare_eval);
                let mut node = SearchNode {
                    parent_raw: cand.parent_id.0,
                    action: cand.action,
                    por_key: cand.por_key,
                    choices_offset,
                    choices_len,
                    state_handle: PackedHandle::default(),
                    state_hash: cand.state_hash,
                    beam_rank_score: cand.base_score,
                    turn: cand.turn.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
                    action_count: cand.action_count,
                    potion_count: cand.potion_count.min(u8::MAX as u16) as u8,
                    explicit_potion_count: cand.explicit_potion_count.min(u8::MAX as u16) as u8,
                    optional_paid_potion_count: cand
                        .optional_paid_potion_count
                        .min(u8::MAX as u16) as u8,
                    automatic_potion_count: cand.automatic_potion_count.min(u8::MAX as u16)
                        as u8,
                    used_potion_slot_mask: cand.used_potion_slot_mask,
                    potion_strategic_cost: cand
                        .potion_strategic_cost
                        .clamp(i16::MIN as i32, i16::MAX as i32)
                        as i16,
                    optional_paid_potion_cost: cand
                        .optional_paid_potion_cost
                        .clamp(i16::MIN as i32, i16::MAX as i32)
                        as i16,
                    ambergris_count: cand.ambergris_count.min(u8::MAX as u16) as u8,
                    forced_used_mask: cand.forced_used_mask,
                    forced_ambergris_count: cand.forced_ambergris_count.min(u8::MAX as u16)
                        as u8,
                    node_flags,
                    fast_eval: cand.fast_eval,
                    rare_eval_idx,
                    ..Default::default()
                };
                if let Some(ref f) = cand.full_eval {
                    node.set_full_eval(f);
                }
                let cid = arena.push_node(node);
                completed.push(cid);
                if has_reached_acceptable_battle_hp_loss_node(
                    &root_baseline,
                    policy,
                    &arena,
                    cid,
                ) {
                    termination = "HpTarget";
                    break;
                }
                continue;
            }
            let state_bytes = scratch.slab.get(cand.state_slab_idx, cand.state_off, cand.state_len);
            if unpack_state(state_bytes, &root.static_ctx, &mut scratch.state).is_err() {
                continue;
            }
            let cfast = evaluate_fast(&scratch.state, content, &root_baseline, None);
            let cfull = evaluate_full(&scratch.state, content, &cfast, &root_baseline);
            capture_novelty_facts(&scratch.state, &cfast, &cfull, &mut facts_buf);
            let w = novelty.evaluate(
                (cand.fast_eval.enemy_hp / 10, cand.potion_count as i32),
                &facts_buf,
            );

            let rare_eval_idx = arena.push_opt_rare_eval(cand.rare_eval);
            let mut node_flags = 0u8;
            if cand.has_extra_end_turn {
                node_flags |= NODE_FLAG_HAS_EXTRA_END_TURN;
            }
            let mut node = SearchNode {
                parent_raw: cand.parent_id.0,
                action: cand.action,
                por_key: cand.por_key,
                choices_offset,
                choices_len,
                state_handle: main_arena.commit_slice(state_bytes),
                state_hash: cand.state_hash,
                beam_rank_score: cand.base_score,
                turn: cand.turn.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
                action_count: cand.action_count,
                potion_count: cand.potion_count.min(u8::MAX as u16) as u8,
                explicit_potion_count: cand.explicit_potion_count.min(u8::MAX as u16) as u8,
                optional_paid_potion_count: cand
                    .optional_paid_potion_count
                    .min(u8::MAX as u16) as u8,
                automatic_potion_count: cand.automatic_potion_count.min(u8::MAX as u16) as u8,
                used_potion_slot_mask: cand.used_potion_slot_mask,
                potion_strategic_cost: cand
                    .potion_strategic_cost
                    .clamp(i16::MIN as i32, i16::MAX as i32)
                    as i16,
                optional_paid_potion_cost: cand
                    .optional_paid_potion_cost
                    .clamp(i16::MIN as i32, i16::MAX as i32)
                    as i16,
                ambergris_count: cand.ambergris_count.min(u8::MAX as u16) as u8,
                forced_used_mask: cand.forced_used_mask,
                forced_ambergris_count: cand.forced_ambergris_count.min(u8::MAX as u16) as u8,
                node_flags,
                fast_eval: cand.fast_eval,
                rare_eval_idx,
                ..Default::default()
            };
            node.set_full_eval(&cfull.to_stored());
            let cid = arena.push_node(node);

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
                cand.base_score,
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
        end_player_max_hp,
        all_forced_uses_satisfied,
        strategic_hp_deficit,
        net_persistent_hp_delta_centi,
        net_potion_opportunity_cost_centi,
        strategy_goal_hp_credit,
        strategy_goal_count,
        growth_rewards,
        relic_eval,
        recovered_hp,
        hp_lost,
        potion_count,
        optional_potion_count,
        optional_paid_potion_count,
        optional_potion_strategic_cost,
        optional_ambergris_count,
        used_potion_slot_mask,
        alchemize_count,
        empty_potion_slots,
        outstanding_stolen,
        forfeits_gold,
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
            sel.end_player_max_hp,
            sel.all_forced_uses_satisfied,
            sel.strategic_hp_deficit,
            sel.net_persistent_hp_delta_centi,
            sel.net_potion_opportunity_cost_centi,
            sel.strategy_goal_hp_credit,
            sel.strategy_goal_count,
            sel.growth_rewards,
            sel.relic_eval,
            sel.recovered_hp,
            sel.hp_lost,
            sel.potion_count,
            sel.optional_potion_count,
            sel.optional_paid_potion_count,
            sel.optional_potion_strategic_cost,
            sel.optional_ambergris_count,
            sel.used_potion_slot_mask,
            sel.alchemize_count,
            sel.empty_potion_slots,
            sel.outstanding_stolen,
            sel.forfeits_gold,
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
            root_baseline.initial_player_max_hp,
            root_baseline.all_forced_potion_mask == 0,
            i32::MAX,
            i32::MIN / 2,
            0,
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
            0,
            0,
            root_baseline.empty_root_potion_slots,
            0,
            false,
            0,
            Vec::new(),
        ),
    };

    let total_committed = main_arena.total_bytes_committed;
    let explicit_potion_count =
        best_node.map_or(0, |nid| arena.get(nid).explicit_potion_count as usize);
    let nodes_in_arena = arena.len();
    Ok(BeamRunOutput {
        best_node,
        best_turn,
        nodes_in_arena,
        root_baseline,
        won,
        survives,
        death_save_use_count,
        player_hp,
        end_player_max_hp,
        all_forced_uses_satisfied,
        strategic_hp_deficit,
        net_persistent_hp_delta_centi,
        net_potion_opportunity_cost_centi,
        strategy_goal_hp_credit,
        strategy_goal_count,
        growth_rewards,
        relic_eval,
        recovered_hp,
        hp_lost,
        potion_count,
        explicit_potion_count,
        optional_potion_count,
        optional_paid_potion_count,
        optional_potion_strategic_cost,
        optional_ambergris_count,
        used_potion_slot_mask,
        alchemize_count,
        empty_potion_slots,
        outstanding_stolen,
        forfeits_gold,
        has_completed_turn_ongoing: false,
        completed_ongoing_turn: -1,
        score,
        actions,
        expanded_nodes,
        transition_count: scratch.transitions,
        choice_replay_attempts: scratch.choice_replay_attempts,
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
    _incumbent_table: &PrimaryIncumbentTable,
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
    let probe_cache = ChoiceProbeCache::new();
    let frozen_incumbent = FrozenIncumbentBound::default();
    let mut tempo_tt = BossTempoTranspositions::new(32_768);
    let mut boss_profile = profile.clone();
    boss_profile.boss_tempo_hp_pricing = true;

    pack_state(root, &mut scratch.pack_buf);
    let root_hash = hash_canonical_key(root);
    let root_fast = evaluate_fast(root, content, &root_baseline, None);
    let root_full = evaluate_full(root, content, &root_fast, &root_baseline);
    let root_rare_idx = arena.push_rare_eval(RareNodeEval {
        growth_rewards: root_fast.growth_rewards,
        relic_eval: root_fast.relic_eval,
    });
    let root_terminal = !root.is_in_progress || root_fast.won || root_fast.player_dead;
    let mut root_node = SearchNode {
        state_handle: main_arena.commit_slice(&scratch.pack_buf),
        state_hash: root_hash,
        beam_rank_score: root_fast.base_score,
        turn: root
            .player
            .turn_number
            .clamp(i16::MIN as i32, i16::MAX as i32) as i16,
        node_flags: if root_terminal {
            NODE_FLAG_IS_TERMINAL
        } else {
            0
        },
        fast_eval: StoredFastEval::from(&root_fast),
        rare_eval_idx: root_rare_idx,
        ..Default::default()
    };
    root_node.set_full_eval(&root_full.to_stored());
    let root_id = arena.push_node(root_node);

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
        if pnode.is_terminal() {
            completed.push(item.node_id);
            continue;
        }
        if !tempo_tt.try_mark(
            pnode.state_hash,
            item.remaining_discrepancies,
            pnode.fast_eval.transposition_label(
                pnode.beam_rank_score_or_base(),
                pnode.potion_count,
                pnode.potion_strategic_cost as i32,
                0,
                pnode.action_count,
                pnode.forced_used_mask,
            ),
        ) {
            continue;
        }
        expanded_nodes += 1;
        let pbytes = main_arena.get(pnode.state_handle);
        if unpack_state(pbytes, &root.static_ctx, &mut parent_state).is_err() {
            continue;
        }
        let p_has_la = pnode.has_choice_lookahead();
        let p_la_score = pnode.choice_lookahead_score();
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
            &root_baseline,
            true,
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
                &frozen_incumbent,
                &tt,
                &probe_cache,
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
            let rank_score = cand.full_eval.map_or(cand.base_score, |f| f.beam_rank_score);
            let rare_eval_idx = arena.push_opt_rare_eval(cand.rare_eval);
            let (choices_offset, choices_len) = arena.choices.extend_from_arena_slice(
                &scratch.staged_choices,
                cand.choices_offset,
                cand.choices_len,
            );
            let mut node_flags = 0u8;
            if cand.has_extra_end_turn {
                node_flags |= NODE_FLAG_HAS_EXTRA_END_TURN;
            }
            if cand.is_terminal {
                node_flags |= NODE_FLAG_IS_TERMINAL;
            }
            let mut node = SearchNode {
                parent_raw: cand.parent_id.0,
                action: cand.action,
                por_key: cand.por_key,
                choices_offset,
                choices_len,
                state_handle,
                state_hash: cand.state_hash,
                beam_rank_score: cand.base_score,
                turn: cand.turn.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
                action_count: cand.action_count,
                potion_count: cand.potion_count.min(u8::MAX as u16) as u8,
                explicit_potion_count: cand.explicit_potion_count.min(u8::MAX as u16) as u8,
                optional_paid_potion_count: cand
                    .optional_paid_potion_count
                    .min(u8::MAX as u16) as u8,
                automatic_potion_count: cand.automatic_potion_count.min(u8::MAX as u16) as u8,
                used_potion_slot_mask: cand.used_potion_slot_mask,
                potion_strategic_cost: cand
                    .potion_strategic_cost
                    .clamp(i16::MIN as i32, i16::MAX as i32)
                    as i16,
                optional_paid_potion_cost: cand
                    .optional_paid_potion_cost
                    .clamp(i16::MIN as i32, i16::MAX as i32)
                    as i16,
                ambergris_count: cand.ambergris_count.min(u8::MAX as u16) as u8,
                forced_used_mask: cand.forced_used_mask,
                forced_ambergris_count: cand.forced_ambergris_count.min(u8::MAX as u16) as u8,
                node_flags,
                fast_eval: cand.fast_eval,
                rare_eval_idx,
                ..Default::default()
            };
            if let Some(ref cfull) = cand.full_eval {
                node.set_full_eval(cfull);
            }
            let cid = arena.push_node(node);
            if cand.is_terminal {
                completed.push(cid);
            } else if !cand.fast_eval.player_dead() {
                next_nodes.push((cid, cand.turn > (pnode.turn as i32), rank_score));
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
        end_player_max_hp,
        all_forced_uses_satisfied,
        strategic_hp_deficit,
        net_persistent_hp_delta_centi,
        net_potion_opportunity_cost_centi,
        strategy_goal_hp_credit,
        strategy_goal_count,
        growth_rewards,
        relic_eval,
        recovered_hp,
        hp_lost,
        potion_count,
        optional_potion_count,
        optional_paid_potion_count,
        optional_potion_strategic_cost,
        optional_ambergris_count,
        used_potion_slot_mask,
        alchemize_count,
        empty_potion_slots,
        outstanding_stolen,
        forfeits_gold,
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
            sel.end_player_max_hp,
            sel.all_forced_uses_satisfied,
            sel.strategic_hp_deficit,
            sel.net_persistent_hp_delta_centi,
            sel.net_potion_opportunity_cost_centi,
            sel.strategy_goal_hp_credit,
            sel.strategy_goal_count,
            sel.growth_rewards,
            sel.relic_eval,
            sel.recovered_hp,
            sel.hp_lost,
            sel.potion_count,
            sel.optional_potion_count,
            sel.optional_paid_potion_count,
            sel.optional_potion_strategic_cost,
            sel.optional_ambergris_count,
            sel.used_potion_slot_mask,
            sel.alchemize_count,
            sel.empty_potion_slots,
            sel.outstanding_stolen,
            sel.forfeits_gold,
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
            root_baseline.initial_player_max_hp,
            root_baseline.all_forced_potion_mask == 0,
            i32::MAX,
            i32::MIN / 2,
            0,
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
            0,
            0,
            root_baseline.empty_root_potion_slots,
            0,
            false,
            0,
            Vec::new(),
        ),
    };

    let total_committed = main_arena.total_bytes_committed;
    let explicit_potion_count =
        best_node.map_or(0, |nid| arena.get(nid).explicit_potion_count as usize);
    let nodes_in_arena = arena.len();
    Ok(BeamRunOutput {
        best_node,
        best_turn,
        nodes_in_arena,
        root_baseline,
        won,
        survives,
        death_save_use_count,
        player_hp,
        end_player_max_hp,
        all_forced_uses_satisfied,
        strategic_hp_deficit,
        net_persistent_hp_delta_centi,
        net_potion_opportunity_cost_centi,
        strategy_goal_hp_credit,
        strategy_goal_count,
        growth_rewards,
        relic_eval,
        recovered_hp,
        hp_lost,
        potion_count,
        explicit_potion_count,
        optional_potion_count,
        optional_paid_potion_count,
        optional_potion_strategic_cost,
        optional_ambergris_count,
        used_potion_slot_mask,
        alchemize_count,
        empty_potion_slots,
        outstanding_stolen,
        forfeits_gold,
        has_completed_turn_ongoing: false,
        completed_ongoing_turn: -1,
        score,
        actions,
        expanded_nodes,
        transition_count: scratch.transitions,
        choice_replay_attempts: scratch.choice_replay_attempts,
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

fn has_reached_acceptable_battle_hp_loss_node(
    root: &RootEvalBaseline,
    policy: &SearchPolicy,
    arena: &NodeArena,
    nid: NodeId,
) -> bool {
    let node = arena.get(nid);
    let f = &node.fast_eval;
    let rare = arena.rare_eval(nid);
    let complete_victory = crate::eval::RouteQualityPolicy::is_complete_victory(
        node.action_count as usize,
        f.all_enemies_dead() || f.won(),
        f.player_dead(),
        f.projected_player_hp as i32,
    );
    let check_theft = policy.theft_policy == SolverTheftPolicy::PreserveResources;
    let all_forced_ok =
        (node.forced_used_mask & root.all_forced_potion_mask) == root.all_forced_potion_mask;
    root.can_stop_at_hp_target
        && complete_victory
        && !f.player_dead()
        && f.projected_player_hp > 0
        && f.death_save_use_count == 0
        && (!check_theft || (f.outstanding_stolen == 0 && !f.forfeits_gold()))
        && all_forced_ok
        && root.growth_target_satisfied(&rare.growth_rewards)
        && root.relic_targets_satisfied(&rare.relic_eval)
        && node.optional_paid_potion_count == 0
        && (root.effective_potion_policy != SolverPotionPolicy::RequireAtLeastOne
            || (node.explicit_potion_count as usize) >= root.minimum_required_potion_uses)
        && root.prior_battle_hp_lost + (f.cumulative_hp_lost as i32) <= policy.acceptable_battle_hp_loss
}

fn has_reached_acceptable_battle_hp_loss(
    root: &RootEvalBaseline,
    policy: &SearchPolicy,
    out: &BeamRunOutput,
) -> bool {
    let check_theft = policy.theft_policy == SolverTheftPolicy::PreserveResources;
    root.can_stop_at_hp_target
        && out.won
        && out.survives
        && out.death_save_use_count == 0
        && (!check_theft || (out.outstanding_stolen == 0 && !out.forfeits_gold))
        && out.all_forced_uses_satisfied
        && root.growth_target_satisfied(&out.growth_rewards)
        && root.relic_targets_satisfied(&out.relic_eval)
        && out.optional_paid_potion_count == 0
        && (root.effective_potion_policy != SolverPotionPolicy::RequireAtLeastOne
            || out.explicit_potion_count >= root.minimum_required_potion_uses)
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

#[inline]
fn satisfies_run_output_hard_constraints(out: &BeamRunOutput) -> bool {
    let root = &out.root_baseline;
    let policy_ok = match root.effective_potion_policy {
        SolverPotionPolicy::Disabled => {
            out.optional_paid_potion_count == 0
                && out.explicit_potion_count <= root.forced_directive_count
        }
        SolverPotionPolicy::RequireAtLeastOne => {
            out.explicit_potion_count >= root.minimum_required_potion_uses
        }
        SolverPotionPolicy::Smart => true,
    };
    out.all_forced_uses_satisfied
        && out.explicit_potion_count >= root.minimum_required_potion_uses
        && policy_ok
}

fn is_better_run_output(cand: &BeamRunOutput, cur: &BeamRunOutput) -> bool {
    is_better_run_output_with_policy(cand, cur, None, None)
}

fn is_better_run_output_with_policy(
    cand: &BeamRunOutput,
    cur: &BeamRunOutput,
    policy: Option<&SearchPolicy>,
    _zero_potion_baseline: Option<&BeamRunOutput>,
) -> bool {
    let cand_hard = satisfies_run_output_hard_constraints(cand);
    let cur_hard = satisfies_run_output_hard_constraints(cur);
    if cand_hard != cur_hard {
        return cand_hard;
    }
    if cand.all_forced_uses_satisfied != cur.all_forced_uses_satisfied {
        return cand.all_forced_uses_satisfied;
    }
    if !cand.won && !cur.won {
        if cand.has_completed_turn_ongoing != cur.has_completed_turn_ongoing {
            return cand.has_completed_turn_ongoing;
        }
        if cand.has_completed_turn_ongoing
            && cand.survives
            && cur.survives
            && cand.completed_ongoing_turn != cur.completed_ongoing_turn
        {
            if cand.completed_ongoing_turn > cur.completed_ongoing_turn && cur.termination == "None"
            {
                return true;
            }
            if cur.completed_ongoing_turn > cand.completed_ongoing_turn && cand.termination == "None"
            {
                return false;
            }
        }
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

    let check_theft = policy
        .map_or(cand.root_baseline.theft_policy, |p| p.theft_policy)
        == SolverTheftPolicy::PreserveResources;
    if check_theft {
        if cand.outstanding_stolen != cur.outstanding_stolen {
            return cand.outstanding_stolen < cur.outstanding_stolen;
        }
        if cand.forfeits_gold != cur.forfeits_gold {
            return !cand.forfeits_gold;
        }
    }

    let cand_v = (cand.net_persistent_hp_delta_centi as i64)
        - (cand.net_potion_opportunity_cost_centi as i64);
    let cur_v = (cur.net_persistent_hp_delta_centi as i64)
        - (cur.net_potion_opportunity_cost_centi as i64);
    if cand_v != cur_v {
        return cand_v > cur_v;
    }
    if cand.net_persistent_hp_delta_centi != cur.net_persistent_hp_delta_centi {
        return cand.net_persistent_hp_delta_centi > cur.net_persistent_hp_delta_centi;
    }
    if cand.strategy_goal_hp_credit != cur.strategy_goal_hp_credit {
        return cand.strategy_goal_hp_credit > cur.strategy_goal_hp_credit;
    }
    if cand.strategy_goal_count != cur.strategy_goal_count {
        return cand.strategy_goal_count > cur.strategy_goal_count;
    }
    if !cand.won && !cand.survives && !cur.won && !cur.survives {
        if cand.best_turn != cur.best_turn {
            return cand.best_turn > cur.best_turn;
        }
    } else if cand.best_turn != cur.best_turn {
        return cand.best_turn < cur.best_turn;
    }
    if cand.hp_lost != cur.hp_lost {
        return cand.hp_lost < cur.hp_lost;
    }
    if cand.potion_count != cur.potion_count {
        return cand.potion_count < cur.potion_count;
    }
    cand.score > cur.score
}

fn should_run_smart_potion_pass(
    zero_baseline: Option<&BeamRunOutput>,
    root_baseline: &RootEvalBaseline,
    check_theft: bool,
) -> bool {
    if root_baseline.effective_potion_policy != SolverPotionPolicy::Smart
        || root_baseline.paid_optional_potion_count == 0
    {
        return false;
    }
    let Some(base) = zero_baseline else {
        return true;
    };
    if !base.won || !base.survives || base.death_save_use_count > 0 {
        return true;
    }
    if check_theft && (base.outstanding_stolen > 0 || base.forfeits_gold) {
        return true;
    }
    if !base.all_forced_uses_satisfied {
        return true;
    }
    PotionUsePolicy::can_any_paid_potion_improve_on(
        base.won,
        base.survives,
        base.death_save_use_count,
        base.player_hp,
        base.end_player_max_hp,
        base.used_potion_slot_mask,
        base.outstanding_stolen,
        base.forfeits_gold,
        check_theft,
        root_baseline,
    )
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
    shared_incumbent: &mut FrozenIncumbentBound,
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
    total_choice_replays: &mut u64,
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

    let smart_mode = root_baseline_info.effective_potion_policy == SolverPotionPolicy::Smart;
    let is_disabled_mode =
        root_baseline_info.effective_potion_policy == SolverPotionPolicy::Disabled;
    let check_theft = policy.theft_policy == SolverTheftPolicy::PreserveResources;
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
        shared_incumbent,
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
    *total_choice_replays += phase_a.choice_replay_attempts;
    *total_tt_pruned += phase_a.tt_pruned;
    *total_incumbent_pruned += phase_a.incumbent_pruned;
    *total_por_pruned += phase_a.por_pruned;

    let phase_a_is_zero_baseline = phase_a.optional_paid_potion_count == 0;
    let mut take_a = match selected_best.as_ref() {
        None => true,
        Some(cur) => is_better_run_output_with_policy(
            &phase_a,
            cur,
            Some(policy),
            baseline_zero_potion.as_ref(),
        ),
    };
    let update_zero = if phase_a_is_zero_baseline {
        match baseline_zero_potion.as_ref() {
            None => true,
            Some(cur_z) => is_better_run_output_with_policy(&phase_a, cur_z, Some(policy), None),
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
            check_theft,
        )
        && !cancel.load(Ordering::Relaxed)
        && *total_expanded < stage_node_ceiling
    {
        if let Some(avail_ms_b) = rem_ms() {
            if take_a || update_zero {
                if take_a && !update_zero {
                    *selected_best = Some(phase_a);
                } else if take_a {
                    let summary_clone = BeamRunOutput {
                        best_node: phase_a.best_node,
                        best_turn: phase_a.best_turn,
                        nodes_in_arena: phase_a.nodes_in_arena,
                        root_baseline: phase_a.root_baseline.clone(),
                        won: phase_a.won,
                        survives: phase_a.survives,
                        death_save_use_count: phase_a.death_save_use_count,
                        player_hp: phase_a.player_hp,
                        end_player_max_hp: phase_a.end_player_max_hp,
                        all_forced_uses_satisfied: phase_a.all_forced_uses_satisfied,
                        strategic_hp_deficit: phase_a.strategic_hp_deficit,
                        net_persistent_hp_delta_centi: phase_a.net_persistent_hp_delta_centi,
                        net_potion_opportunity_cost_centi: phase_a
                            .net_potion_opportunity_cost_centi,
                        strategy_goal_hp_credit: phase_a.strategy_goal_hp_credit,
                        strategy_goal_count: phase_a.strategy_goal_count,
                        growth_rewards: phase_a.growth_rewards,
                        relic_eval: phase_a.relic_eval,
                        recovered_hp: phase_a.recovered_hp,
                        hp_lost: phase_a.hp_lost,
                        potion_count: phase_a.potion_count,
                        explicit_potion_count: phase_a.explicit_potion_count,
                        optional_potion_count: phase_a.optional_potion_count,
                        optional_paid_potion_count: phase_a.optional_paid_potion_count,
                        optional_potion_strategic_cost: phase_a.optional_potion_strategic_cost,
                        optional_ambergris_count: phase_a.optional_ambergris_count,
                        used_potion_slot_mask: phase_a.used_potion_slot_mask,
                        alchemize_count: phase_a.alchemize_count,
                        empty_potion_slots: phase_a.empty_potion_slots,
                        outstanding_stolen: phase_a.outstanding_stolen,
                        forfeits_gold: phase_a.forfeits_gold,
                        has_completed_turn_ongoing: phase_a.has_completed_turn_ongoing,
                        completed_ongoing_turn: phase_a.completed_ongoing_turn,
                        score: phase_a.score,
                        actions: phase_a.actions.clone(),
                        expanded_nodes: phase_a.expanded_nodes,
                        transition_count: phase_a.transition_count,
                        choice_replay_attempts: phase_a.choice_replay_attempts,
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
                shared_incumbent,
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
            *total_choice_replays += phase_b.choice_replay_attempts;
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
                if phase_b.optional_paid_potion_count == 0
                    && baseline_zero_potion.as_ref().is_none_or(|z| {
                        is_better_run_output_with_policy(&phase_b, z, Some(policy), None)
                    })
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
        if update_zero {
            let zero_clone = BeamRunOutput {
                best_node: phase_a.best_node,
                best_turn: phase_a.best_turn,
                nodes_in_arena: phase_a.nodes_in_arena,
                root_baseline: phase_a.root_baseline.clone(),
                won: phase_a.won,
                survives: phase_a.survives,
                death_save_use_count: phase_a.death_save_use_count,
                player_hp: phase_a.player_hp,
                end_player_max_hp: phase_a.end_player_max_hp,
                all_forced_uses_satisfied: phase_a.all_forced_uses_satisfied,
                strategic_hp_deficit: phase_a.strategic_hp_deficit,
                net_persistent_hp_delta_centi: phase_a.net_persistent_hp_delta_centi,
                net_potion_opportunity_cost_centi: phase_a.net_potion_opportunity_cost_centi,
                strategy_goal_hp_credit: phase_a.strategy_goal_hp_credit,
                strategy_goal_count: phase_a.strategy_goal_count,
                growth_rewards: phase_a.growth_rewards,
                relic_eval: phase_a.relic_eval,
                recovered_hp: phase_a.recovered_hp,
                hp_lost: phase_a.hp_lost,
                potion_count: phase_a.potion_count,
                explicit_potion_count: phase_a.explicit_potion_count,
                optional_potion_count: phase_a.optional_potion_count,
                optional_paid_potion_count: phase_a.optional_paid_potion_count,
                optional_potion_strategic_cost: phase_a.optional_potion_strategic_cost,
                optional_ambergris_count: phase_a.optional_ambergris_count,
                used_potion_slot_mask: phase_a.used_potion_slot_mask,
                alchemize_count: phase_a.alchemize_count,
                empty_potion_slots: phase_a.empty_potion_slots,
                outstanding_stolen: phase_a.outstanding_stolen,
                forfeits_gold: phase_a.forfeits_gold,
                has_completed_turn_ongoing: phase_a.has_completed_turn_ongoing,
                completed_ongoing_turn: phase_a.completed_ongoing_turn,
                score: phase_a.score,
                actions: phase_a.actions.clone(),
                expanded_nodes: phase_a.expanded_nodes,
                transition_count: phase_a.transition_count,
                choice_replay_attempts: phase_a.choice_replay_attempts,
                tt_pruned: phase_a.tt_pruned,
                incumbent_pruned: phase_a.incumbent_pruned,
                por_pruned: phase_a.por_pruned,
                packed_state_bytes_total: phase_a.packed_state_bytes_total,
                termination: phase_a.termination,
                terminal_cards_hint: phase_a.terminal_cards_hint.clone(),
                early_turn_frontiers: Vec::new(),
                boss_tempo_prefixes: Vec::new(),
            };
            *baseline_zero_potion = Some(zero_clone);
        }
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
    total_choice_replays: &mut u64,
    total_tt_pruned: &mut u64,
    total_incumbent_pruned: &mut u64,
    total_por_pruned: &mut u64,
) -> Result<BeamRunOutput, KernelError> {
    if !policy.use_beam_width_portfolio {
        let mut incumbent = FrozenIncumbentBound::default();
        let out = run_beam_pass(
            root,
            content,
            policy,
            profile,
            cert,
            incumbent_table,
            &mut incumbent,
            pool,
            cancel,
            progress_cb,
            PassSearchOptions::default(),
            None,
        )?;
        *total_expanded += out.expanded_nodes;
        *total_transitions += out.transition_count;
        *total_choice_replays += out.choice_replay_attempts;
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
        let smart_mode =
            root_baseline_info.effective_potion_policy == SolverPotionPolicy::Smart;
        let check_theft = policy.theft_policy == SolverTheftPolicy::PreserveResources;

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

        let mut shared_incumbent = FrozenIncumbentBound::default();
        if cancel.load(Ordering::Relaxed) {
            return run_beam_pass(
                root,
                content,
                policy,
                profile,
                cert,
                incumbent_table,
                &mut shared_incumbent,
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
        let mut resources = PassResources::acquire(
            &root.static_ctx,
            worker_count,
            policy.transposition_entry_limit,
        );
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
            &mut shared_incumbent,
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
            total_choice_replays,
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
                &mut shared_incumbent,
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
            && stage1_out.death_save_use_count == 0
            && (!check_theft
                || (stage1_out.outstanding_stolen == 0 && !stage1_out.forfeits_gold));
        let stage1_eff_loss = stage1_out.strategic_hp_deficit.max(0);
        let stage1_paid = stage1_out.optional_paid_potion_count;
        let stage1_meets_target =
            has_reached_acceptable_battle_hp_loss(&root_baseline_info, policy, stage1_out)
                && (!smart_mode
                    || !PotionUsePolicy::can_any_paid_potion_improve_on(
                        stage1_out.won,
                        stage1_out.survives,
                        stage1_out.death_save_use_count,
                        stage1_out.player_hp,
                        stage1_out.end_player_max_hp,
                        stage1_out.used_potion_slot_mask,
                        stage1_out.outstanding_stolen,
                        stage1_out.forfeits_gold,
                        check_theft,
                        &root_baseline_info,
                    ));
        let stage1_early_exit = stage1_meets_target
            || (stage1_clean && stage1_eff_loss <= 0 && stage1_paid == 0)
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
                &mut shared_incumbent,
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
                total_choice_replays,
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
                && stage2_out.death_save_use_count == 0
                && (!check_theft
                    || (stage2_out.outstanding_stolen == 0 && !stage2_out.forfeits_gold));
            let stage2_eff_loss = stage2_out.strategic_hp_deficit.max(0);
            let stage2_paid = stage2_out.optional_paid_potion_count;
            let stage2_meets_target =
                has_reached_acceptable_battle_hp_loss(&root_baseline_info, policy, stage2_out)
                    && (!smart_mode
                        || !PotionUsePolicy::can_any_paid_potion_improve_on(
                            stage2_out.won,
                            stage2_out.survives,
                            stage2_out.death_save_use_count,
                            stage2_out.player_hp,
                            stage2_out.end_player_max_hp,
                            stage2_out.used_potion_slot_mask,
                            stage2_out.outstanding_stolen,
                            stage2_out.forfeits_gold,
                            check_theft,
                            &root_baseline_info,
                        ));
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
                    &mut shared_incumbent,
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
                    total_choice_replays,
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
        let mut member_incumbent = FrozenIncumbentBound::default();
        let res = run_beam_pass(
            root,
            content,
            policy,
            member_profile,
            cert,
            incumbent_table,
            &mut member_incumbent,
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
    *total_choice_replays += out.choice_replay_attempts;
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
        let mut compacted_root;
        let root = if !root.static_ctx.compact_turn_history || !root.static_ctx.has_root_rngs {
            compacted_root = root.clone();
            let mut ctx = (*root.static_ctx).clone();
            ctx.compact_turn_history = true;
            if !ctx.has_root_rngs {
                ctx.set_root_rngs(root.run_rngs.streams, root.player_rngs.streams);
            }
            compacted_root.static_ctx = Arc::new(ctx);
            &compacted_root
        } else {
            root
        };
        let started = Instant::now();
        let cert = ClosureCertificate::certify(root, content);
        let incumbent_table = PrimaryIncumbentTable::new();

        let dop = policy
            .max_degree_of_parallelism
            .clamp(1, crate::eval::weights::MAXIMUM_SEARCH_MAX_DEGREE_OF_PARALLELISM);
        let pool_ref = shared_search_pool(dop);

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
        let mut total_choice_replays = 0u64;
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
                total_choice_replays += exp_out.choice_replay_attempts;
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
                        &mut total_choice_replays,
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
                    &mut total_choice_replays,
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
                &mut total_choice_replays,
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
                total_choice_replays += nov_out.choice_replay_attempts;
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
                &mut total_choice_replays,
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
            let mut etc_incumbent = FrozenIncumbentBound::default();
            if let Ok(scout) = run_beam_pass(
                root,
                content,
                policy,
                &scout_profile,
                &cert,
                &incumbent_table,
                &mut etc_incumbent,
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
                total_choice_replays += scout.choice_replay_attempts;
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
                            &mut etc_incumbent,
                            pool_ref,
                            cancel,
                            None,
                            PassSearchOptions::default(),
                            Some(&cand.actions),
                        ) {
                            cont_budget = cont_budget.saturating_sub(cont.expanded_nodes);
                            total_expanded += cont.expanded_nodes;
                            total_transitions += cont.transition_count;
                            total_choice_replays += cont.choice_replay_attempts;
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
            let mut tempo_incumbent = FrozenIncumbentBound::default();
            if let Ok(scout) = run_beam_pass(
                root,
                content,
                policy,
                &tempo_budget,
                &cert,
                &incumbent_table,
                &mut tempo_incumbent,
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
                total_choice_replays += scout.choice_replay_attempts;
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
                        &mut tempo_incumbent,
                        pool_ref,
                        cancel,
                        None,
                        PassSearchOptions::default(),
                        Some(&prefix.actions),
                    ) {
                        total_expanded += cont.expanded_nodes;
                        total_transitions += cont.transition_count;
                        total_choice_replays += cont.choice_replay_attempts;
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
                total_choice_replays += tempo_out.choice_replay_attempts;
                if is_better_run_output(&tempo_out, &selected) {
                    selected = tempo_out;
                }
            }
        }

        let packed_state_bytes_total = selected.packed_state_bytes_total;
        let nodes_in_arena = selected.nodes_in_arena as u64;
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
                choice_replay_attempts: total_choice_replays,
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
