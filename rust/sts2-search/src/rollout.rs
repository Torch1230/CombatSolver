use crate::arena::{MainStateArena, NodeArena, NodeId};
use crate::coordinator::clone_battle_state_into;
use crate::eval::{
    compute_beam_rank_score_with_centi, evaluate_fast, evaluate_full, is_creature_reviving,
    BeamWeightTerm, EvalMode, FastEvalSummary, FullEvalDetails, PotionUsePolicy, RootEvalBaseline,
    SolverPotionPolicy,
};
use crate::hash::StateHash;
use crate::packer::{empty_scratch_state, hash_canonical_key, unpack_state};
use crate::portfolio::SolverSearchProfile;
use crate::SearchPolicy;
use smallvec::SmallVec;
use std::borrow::Cow;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::Arc;
use std::time::Instant;
use sts2_kernel::ordering::{
    resolve_local_energy_cost, resolve_local_energy_cost_card, resolve_local_star_cost_card,
};
use sts2_kernel::state::{BattleState, CardHandle, CreatureHandle, RootStaticContext};
use sts2_kernel::{rollout_op_flags as op, CombatEngine, ContentDispatcher, StepOutcome};
use sts2_types::{
    CardId, CardKeywords, CardRarity, CardStaticFlags, CardTags, CardType, Dec, HookSlot,
    MonsterId, OrbId, PileType, PlayerTurnPhase, PowerId, PowerType, TargetType,
};

const MAX_ROLLOUT_ACTIONS_PER_TURN: usize = 24;
const MAX_TOTAL_ROLLOUT_STEPS: usize = 96;
const MAX_FAST_ENEMIES: usize = 16;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum PilotKind {
    DefensiveTempo,
    SetupFirst,
}

#[derive(Clone, Copy, Debug, Default)]
pub struct CompactRolloutStep {
    pub card_handle: u32,
    pub target_handle: u32,
    pub choices_offset: u32,
    pub choices_len: u16,
    pub is_end_turn: u8,
    pub _pad: u8,
}

const _: () = assert!(std::mem::size_of::<CompactRolloutStep>() == 16);

#[derive(Clone, Debug, Default)]
pub struct RolloutChoiceSlab {
    pub steps_meta: Vec<(u32, u16)>,
    pub indices: Vec<u16>,
}

impl RolloutChoiceSlab {
    #[inline]
    pub fn clear(&mut self) {
        self.steps_meta.clear();
        self.indices.clear();
    }

    #[inline]
    pub fn truncate_to(&mut self, meta_len: usize, indices_len: usize) {
        self.steps_meta.truncate(meta_len);
        self.indices.truncate(indices_len);
    }

    #[inline]
    pub fn push_step(&mut self, sel: &[usize]) {
        let idx_off = self.indices.len() as u32;
        for &v in sel {
            self.indices.push(v as u16);
        }
        self.steps_meta.push((idx_off, sel.len() as u16));
    }

    #[inline]
    pub fn step_indices(&self, step_idx: usize) -> &[u16] {
        let (off, len) = self.steps_meta[step_idx];
        let o = off as usize;
        &self.indices[o..o + len as usize]
    }
}

#[derive(Clone, Debug)]
pub struct PromotedVictory {
    pub steps: Vec<CompactRolloutStep>,
    pub choices: RolloutChoiceSlab,
    pub final_fast: FastEvalSummary,
    pub final_turn: i32,
    pub final_state_hash: StateHash,
    pub auto_potions_used: u16,
}

#[derive(Clone, Debug)]
pub struct RolloutResult {
    pub rollout_score: f64,
    pub promoted_victory: Option<PromotedVictory>,
}

#[derive(Clone, Copy, Eq, PartialEq)]
pub(crate) struct RolloutDedupKey(u128);

impl RolloutDedupKey {
    #[inline(always)]
    fn new(
        card_id: CardId,
        upgrade_level: i32,
        energy_cost: i32,
        star_cost: i32,
        has_ext: bool,
        aff_id: Option<sts2_types::AfflictionId>,
    ) -> Self {
        let aff_bits = aff_id.map_or(0u128, |a| {
            debug_assert!((a as u32) < 0x7FFF);
            u128::from((a as u32) + 1)
        });
        let raw = u128::from(card_id as u16)
            | (u128::from(has_ext) << 16)
            | (aff_bits << 17)
            | (u128::from(upgrade_level as u32) << 32)
            | (u128::from(energy_cost as u32) << 64)
            | (u128::from(star_cost as u32) << 96);
        Self(raw)
    }
}

#[inline(always)]
const fn eval_mode_index(mode: EvalMode) -> usize {
    match mode {
        EvalMode::Balanced => 0,
        EvalMode::SetupAndSurvival => 1,
        EvalMode::Offense => 2,
    }
}

#[inline(always)]
fn compute_scores_at_zero(
    fast: &FastEvalSummary,
    full: &FullEvalDetails,
    root_baseline: &RootEvalBaseline,
    profile: &SolverSearchProfile,
    linear_adj: bool,
    action_count: u16,
) -> [f64; 3] {
    if !linear_adj || fast.won {
        let s = compute_beam_rank_score_with_centi(
            fast,
            full,
            root_baseline,
            profile.base_score_only,
            profile.boss_tempo_hp_pricing,
            profile.beam_weight_perturbation,
            EvalMode::Balanced,
            0,
            action_count,
        );
        [s, s, s]
    } else {
        [
            compute_beam_rank_score_with_centi(
                fast,
                full,
                root_baseline,
                profile.base_score_only,
                profile.boss_tempo_hp_pricing,
                profile.beam_weight_perturbation,
                EvalMode::Balanced,
                0,
                action_count,
            ),
            compute_beam_rank_score_with_centi(
                fast,
                full,
                root_baseline,
                profile.base_score_only,
                profile.boss_tempo_hp_pricing,
                profile.beam_weight_perturbation,
                EvalMode::SetupAndSurvival,
                0,
                action_count,
            ),
            compute_beam_rank_score_with_centi(
                fast,
                full,
                root_baseline,
                profile.base_score_only,
                profile.boss_tempo_hp_pricing,
                profile.beam_weight_perturbation,
                EvalMode::Offense,
                0,
                action_count,
            ),
        ]
    }
}

#[derive(Clone, Copy, Debug, Default)]
pub(crate) struct ChoiceProbeResult {
    pub score: f64,
    pub hp_lost: i32,
    pub board_investment: i32,
    pub is_victory: bool,
    pub is_defeat: bool,
}

const CHOICE_PROBE_SHARDS: usize = 256;
const CHOICE_PROBE_SETS_PER_SHARD: usize = 64;
const CHOICE_PROBE_WAYS: usize = 4;
const CHOICE_PROBE_SLOTS_PER_SHARD: usize = CHOICE_PROBE_SETS_PER_SHARD * CHOICE_PROBE_WAYS;
const CHOICE_PROBE_FLAG_SOME: u8 = 1 << 0;
const CHOICE_PROBE_FLAG_NONE: u8 = 1 << 1;
const CHOICE_PROBE_FLAG_VICTORY: u8 = 1 << 2;
const CHOICE_PROBE_FLAG_DEFEAT: u8 = 1 << 3;
const CHOICE_PROBE_FLAG_LINEAR_ADJ: u8 = 1 << 4;

#[inline]
fn mix_cfg_u64(acc: u64, v: u64) -> u64 {
    let mut x = acc.wrapping_add(v).wrapping_add(0x9E37_79B9_7F4A_7C15);
    x = (x ^ (x >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    x = (x ^ (x >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    x ^ (x >> 31)
}

#[inline]
fn choice_probe_cfg_hash(profile: &SolverSearchProfile, max_turns_ahead: u8) -> u64 {
    let flags_word = ((max_turns_ahead as u64) << 4)
        | (u64::from(profile.base_score_only) << 12)
        | (u64::from(profile.boss_tempo_hp_pricing) << 13);
    let (pert_term, pert_scale) = match profile.beam_weight_perturbation {
        None => (0u64, 0u64),
        Some(p) => {
            let term = match p.term {
                BeamWeightTerm::CurrentEnergy => 1u64,
                BeamWeightTerm::PersistentBuffDelta => 2u64,
                BeamWeightTerm::EnemyHp => 3u64,
            };
            (term, p.scale.to_bits())
        }
    };
    let h = mix_cfg_u64(0x517C_C1B7_2722_0A95, flags_word | (pert_term << 16));
    mix_cfg_u64(h, pert_scale)
}

#[derive(Clone, Copy)]
pub(crate) struct CachedProbeEntry {
    pub scores_at_zero: [f64; 3],
    pub hp_lost: i32,
    pub board_investment: i32,
    pub alchemize_count: u8,
    pub empty_potion_slots: u8,
    pub is_victory: bool,
    pub is_defeat: bool,
    pub linear_adj: bool,
}

impl CachedProbeEntry {
    #[inline(always)]
    pub(crate) fn materialize_result(
        self,
        root_baseline: &RootEvalBaseline,
        eval_mode: EvalMode,
        optional_paid_potion_cost: i32,
        optional_paid_potion_count: usize,
        parent_action_count: u16,
    ) -> ChoiceProbeResult {
        let base = self.scores_at_zero[eval_mode_index(eval_mode)];
        let score = if self.linear_adj {
            let net_potion_cost_centi = PotionUsePolicy::compute_net_potion_opportunity_cost_centi(
                optional_paid_potion_cost,
                optional_paid_potion_count as i32,
                self.alchemize_count as i32,
                self.empty_potion_slots as i32,
                root_baseline,
            );
            let act_coeff = if self.is_victory { 10.0 } else { 5.0 };
            base - (net_potion_cost_centi as f64) * 300.0 - (parent_action_count as f64) * act_coeff
        } else {
            base
        };
        ChoiceProbeResult {
            score,
            hp_lost: self.hp_lost,
            board_investment: self.board_investment,
            is_victory: self.is_victory,
            is_defeat: self.is_defeat,
        }
    }
}

#[derive(Clone, Copy)]
struct ChoiceProbeSlot {
    state_hash: StateHash,
    cfg_hash: u64,
    scores_at_zero: [f64; 3],
    hp_lost: i32,
    board_investment: i32,
    epoch: u32,
    flags: u8,
    alchemize_count: u8,
    empty_potion_slots: u8,
}

const _: () = assert!(std::mem::size_of::<ChoiceProbeSlot>() == 64);

impl ChoiceProbeSlot {
    const EMPTY: Self = Self {
        state_hash: StateHash(0),
        cfg_hash: 0,
        scores_at_zero: [0.0; 3],
        hp_lost: 0,
        board_investment: 0,
        epoch: 0,
        flags: 0,
        alchemize_count: 0,
        empty_potion_slots: 0,
    };

    #[inline(always)]
    fn into_entry(self) -> CachedProbeEntry {
        CachedProbeEntry {
            scores_at_zero: self.scores_at_zero,
            hp_lost: self.hp_lost,
            board_investment: self.board_investment,
            alchemize_count: self.alchemize_count,
            empty_potion_slots: self.empty_potion_slots,
            is_victory: (self.flags & CHOICE_PROBE_FLAG_VICTORY) != 0,
            is_defeat: (self.flags & CHOICE_PROBE_FLAG_DEFEAT) != 0,
            linear_adj: (self.flags & CHOICE_PROBE_FLAG_LINEAR_ADJ) != 0,
        }
    }
}

struct ChoiceProbeShard {
    slots: Option<Box<[ChoiceProbeSlot; CHOICE_PROBE_SLOTS_PER_SHARD]>>,
    victim_cursor: [u8; CHOICE_PROBE_SETS_PER_SHARD],
}

impl Default for ChoiceProbeShard {
    #[inline]
    fn default() -> Self {
        Self {
            slots: None,
            victim_cursor: [0; CHOICE_PROBE_SETS_PER_SHARD],
        }
    }
}

pub(crate) struct ChoiceProbeCache {
    shards: Box<[parking_lot::Mutex<ChoiceProbeShard>]>,
    epoch: AtomicU32,
}

impl Default for ChoiceProbeCache {
    fn default() -> Self {
        Self::new()
    }
}

impl ChoiceProbeCache {
    pub(crate) fn new() -> Self {
        let shards = (0..CHOICE_PROBE_SHARDS)
            .map(|_| parking_lot::Mutex::new(ChoiceProbeShard::default()))
            .collect::<Vec<_>>()
            .into_boxed_slice();
        Self {
            shards,
            epoch: AtomicU32::new(1),
        }
    }

    pub(crate) fn clear(&self) {
        let mut next = self.epoch.load(Ordering::Relaxed).wrapping_add(1);
        if next == 0 {
            next = 1;
            for shard in self.shards.iter() {
                let mut s = shard.lock();
                if let Some(slots) = s.slots.as_mut() {
                    for slot in slots.iter_mut() {
                        slot.flags = 0;
                        slot.epoch = 0;
                    }
                }
                s.victim_cursor.fill(0);
            }
        }
        self.epoch.store(next, Ordering::Relaxed);
    }

    #[inline(always)]
    fn slot_indices(exact_state_hash: StateHash, cfg_hash: u64) -> (usize, usize) {
        let shard_idx = exact_state_hash.shard_index() & (CHOICE_PROBE_SHARDS - 1);
        let lo = exact_state_hash.0 as usize;
        let set_idx =
            (lo ^ (lo >> 16) ^ (cfg_hash as usize)) & (CHOICE_PROBE_SETS_PER_SHARD - 1);
        (shard_idx, set_idx)
    }

    #[inline]
    pub(crate) fn get(
        &self,
        exact_state_hash: StateHash,
        cfg_hash: u64,
    ) -> Option<Option<CachedProbeEntry>> {
        let epoch = self.epoch.load(Ordering::Relaxed);
        let (shard_idx, set_idx) = Self::slot_indices(exact_state_hash, cfg_hash);
        let shard = self.shards[shard_idx].lock();
        let Some(slots) = shard.slots.as_ref() else {
            return None;
        };
        let base = set_idx * CHOICE_PROBE_WAYS;
        for w in 0..CHOICE_PROBE_WAYS {
            let s = &slots[base + w];
            if s.epoch == epoch
                && s.flags != 0
                && s.cfg_hash == cfg_hash
                && s.state_hash == exact_state_hash
            {
                if (s.flags & CHOICE_PROBE_FLAG_NONE) != 0 {
                    return Some(None);
                }
                return Some(Some(s.into_entry()));
            }
        }
        None
    }

    #[inline]
    pub(crate) fn insert(
        &self,
        exact_state_hash: StateHash,
        cfg_hash: u64,
        res: Option<CachedProbeEntry>,
    ) {
        let epoch = self.epoch.load(Ordering::Relaxed);
        let (shard_idx, set_idx) = Self::slot_indices(exact_state_hash, cfg_hash);
        let slot = match res {
            None => ChoiceProbeSlot {
                state_hash: exact_state_hash,
                cfg_hash,
                scores_at_zero: [0.0; 3],
                hp_lost: 0,
                board_investment: 0,
                epoch,
                flags: CHOICE_PROBE_FLAG_NONE,
                alchemize_count: 0,
                empty_potion_slots: 0,
            },
            Some(r) => {
                let mut flags = CHOICE_PROBE_FLAG_SOME;
                if r.is_victory {
                    flags |= CHOICE_PROBE_FLAG_VICTORY;
                }
                if r.is_defeat {
                    flags |= CHOICE_PROBE_FLAG_DEFEAT;
                }
                if r.linear_adj {
                    flags |= CHOICE_PROBE_FLAG_LINEAR_ADJ;
                }
                ChoiceProbeSlot {
                    state_hash: exact_state_hash,
                    cfg_hash,
                    scores_at_zero: r.scores_at_zero,
                    hp_lost: r.hp_lost,
                    board_investment: r.board_investment,
                    epoch,
                    flags,
                    alchemize_count: r.alchemize_count,
                    empty_potion_slots: r.empty_potion_slots,
                }
            }
        };
        let shard = &mut *self.shards[shard_idx].lock();
        let slots = shard
            .slots
            .get_or_insert_with(|| Box::new([ChoiceProbeSlot::EMPTY; CHOICE_PROBE_SLOTS_PER_SHARD]));
        let base = set_idx * CHOICE_PROBE_WAYS;
        for w in 0..CHOICE_PROBE_WAYS {
            let cur = &slots[base + w];
            if cur.epoch == epoch
                && cur.flags != 0
                && cur.cfg_hash == cfg_hash
                && cur.state_hash == exact_state_hash
            {
                slots[base + w] = slot;
                return;
            }
        }
        for w in 0..CHOICE_PROBE_WAYS {
            let cur = &slots[base + w];
            if cur.epoch != epoch || cur.flags == 0 {
                slots[base + w] = slot;
                return;
            }
        }
        let way = usize::from(shard.victim_cursor[set_idx]) & (CHOICE_PROBE_WAYS - 1);
        shard.victim_cursor[set_idx] = shard.victim_cursor[set_idx].wrapping_add(1);
        slots[base + way] = slot;
    }
}

const TURN_ROLLOUT_SHARDS: usize = 64;
const TURN_ROLLOUT_SETS_PER_SHARD: usize = 32;
const TURN_ROLLOUT_WAYS: usize = 2;
const TURN_ROLLOUT_SLOTS_PER_SHARD: usize = TURN_ROLLOUT_SETS_PER_SHARD * TURN_ROLLOUT_WAYS;
const TURN_ROLLOUT_SLOT_NONE: u8 = 1;
const TURN_ROLLOUT_SLOT_SOME: u8 = 2;
const ROLLOUT_PILOT_VALID: u8 = 1 << 0;
const ROLLOUT_PILOT_WON: u8 = 1 << 1;
const ROLLOUT_PILOT_LINEAR_ADJ: u8 = 1 << 2;

#[inline]
fn turn_rollout_cfg_hash(profile: &SolverSearchProfile, horizon_turns: usize) -> u64 {
    let flags_word = (horizon_turns as u64)
        | (u64::from(profile.base_score_only) << 16)
        | (u64::from(profile.boss_tempo_hp_pricing) << 17);
    let (pert_term, pert_scale) = match profile.beam_weight_perturbation {
        None => (0u64, 0u64),
        Some(p) => {
            let term = match p.term {
                BeamWeightTerm::CurrentEnergy => 1u64,
                BeamWeightTerm::PersistentBuffDelta => 2u64,
                BeamWeightTerm::EnemyHp => 3u64,
            };
            (term, p.scale.to_bits())
        }
    };
    let h = mix_cfg_u64(0x9E37_79B9_7F4A_7C15, flags_word | (pert_term << 20));
    mix_cfg_u64(h, pert_scale)
}

#[derive(Clone, Copy)]
pub(crate) struct TurnRolloutPilotEntry {
    pub scores_at_zero: [f64; 3],
    pub alchemize_count: u8,
    pub empty_potion_slots: u8,
    pub flags: u8,
}

impl TurnRolloutPilotEntry {
    const EMPTY: Self = Self {
        scores_at_zero: [0.0; 3],
        alchemize_count: 0,
        empty_potion_slots: 0,
        flags: 0,
    };

    #[inline(always)]
    pub(crate) fn from_eval(
        fast: &FastEvalSummary,
        full: &FullEvalDetails,
        root_baseline: &RootEvalBaseline,
        profile: &SolverSearchProfile,
        total_steps: usize,
    ) -> Self {
        let is_defeat = fast.player_dead || fast.projected_player_hp <= 0;
        let linear_adj = !(profile.base_score_only || is_defeat);
        let step_actions = total_steps.min(u16::MAX as usize) as u16;
        let scores_at_zero =
            compute_scores_at_zero(fast, full, root_baseline, profile, linear_adj, step_actions);
        let mut flags = ROLLOUT_PILOT_VALID;
        if fast.won {
            flags |= ROLLOUT_PILOT_WON;
        }
        if linear_adj {
            flags |= ROLLOUT_PILOT_LINEAR_ADJ;
        }
        Self {
            scores_at_zero,
            alchemize_count: fast.alchemize_count,
            empty_potion_slots: fast.empty_potion_slots,
            flags,
        }
    }

    #[inline(always)]
    pub(crate) fn is_valid(self) -> bool {
        (self.flags & ROLLOUT_PILOT_VALID) != 0
    }

    #[inline(always)]
    pub(crate) fn won(self) -> bool {
        (self.flags & ROLLOUT_PILOT_WON) != 0
    }

    #[inline(always)]
    pub(crate) fn materialize_score(
        self,
        root_baseline: &RootEvalBaseline,
        eval_mode: EvalMode,
        optional_paid_potion_cost: i32,
        optional_paid_potion_count: usize,
        parent_action_count: u16,
    ) -> f64 {
        let base = self.scores_at_zero[eval_mode_index(eval_mode)];
        if (self.flags & ROLLOUT_PILOT_LINEAR_ADJ) != 0 {
            let net_c = PotionUsePolicy::compute_net_potion_opportunity_cost_centi(
                optional_paid_potion_cost,
                optional_paid_potion_count as i32,
                self.alchemize_count as i32,
                self.empty_potion_slots as i32,
                root_baseline,
            );
            let act_coeff = if self.won() { 10.0 } else { 5.0 };
            base - (net_c as f64) * 300.0 - (parent_action_count as f64) * act_coeff
        } else {
            base
        }
    }
}

#[derive(Clone, Copy)]
pub(crate) struct TurnRolloutEntry {
    pub a: TurnRolloutPilotEntry,
    pub b: TurnRolloutPilotEntry,
}

#[derive(Clone, Copy)]
struct TurnRolloutSlot {
    state_hash: StateHash,
    cfg_hash: u64,
    a_scores_at_zero: [f64; 3],
    b_scores_at_zero: [f64; 3],
    epoch: u32,
    a_alchemize_count: u8,
    a_empty_potion_slots: u8,
    a_flags: u8,
    b_alchemize_count: u8,
    b_empty_potion_slots: u8,
    b_flags: u8,
    slot_state: u8,
}

const _: () = assert!(std::mem::size_of::<TurnRolloutSlot>() == 96);

impl TurnRolloutSlot {
    const EMPTY: Self = Self {
        state_hash: StateHash(0),
        cfg_hash: 0,
        a_scores_at_zero: [0.0; 3],
        b_scores_at_zero: [0.0; 3],
        epoch: 0,
        a_alchemize_count: 0,
        a_empty_potion_slots: 0,
        a_flags: 0,
        b_alchemize_count: 0,
        b_empty_potion_slots: 0,
        b_flags: 0,
        slot_state: 0,
    };

    #[inline(always)]
    fn into_entry(self) -> TurnRolloutEntry {
        TurnRolloutEntry {
            a: TurnRolloutPilotEntry {
                scores_at_zero: self.a_scores_at_zero,
                alchemize_count: self.a_alchemize_count,
                empty_potion_slots: self.a_empty_potion_slots,
                flags: self.a_flags,
            },
            b: TurnRolloutPilotEntry {
                scores_at_zero: self.b_scores_at_zero,
                alchemize_count: self.b_alchemize_count,
                empty_potion_slots: self.b_empty_potion_slots,
                flags: self.b_flags,
            },
        }
    }
}

struct TurnRolloutShard {
    slots: Option<Box<[TurnRolloutSlot; TURN_ROLLOUT_SLOTS_PER_SHARD]>>,
    victim_bits: u32,
}

impl Default for TurnRolloutShard {
    #[inline]
    fn default() -> Self {
        Self {
            slots: None,
            victim_bits: 0,
        }
    }
}

pub(crate) struct TurnRolloutCache {
    shards: Box<[parking_lot::Mutex<TurnRolloutShard>]>,
    epoch: AtomicU32,
}

impl Default for TurnRolloutCache {
    fn default() -> Self {
        Self::new()
    }
}

impl TurnRolloutCache {
    pub(crate) fn new() -> Self {
        let shards = (0..TURN_ROLLOUT_SHARDS)
            .map(|_| parking_lot::Mutex::new(TurnRolloutShard::default()))
            .collect::<Vec<_>>()
            .into_boxed_slice();
        Self {
            shards,
            epoch: AtomicU32::new(1),
        }
    }

    pub(crate) fn clear(&self) {
        let mut next = self.epoch.load(Ordering::Relaxed).wrapping_add(1);
        if next == 0 {
            next = 1;
            for shard in self.shards.iter() {
                let mut s = shard.lock();
                if let Some(slots) = s.slots.as_mut() {
                    for slot in slots.iter_mut() {
                        slot.slot_state = 0;
                        slot.epoch = 0;
                    }
                }
                s.victim_bits = 0;
            }
        }
        self.epoch.store(next, Ordering::Relaxed);
    }

    #[inline(always)]
    fn slot_indices(exact_state_hash: StateHash, cfg_hash: u64) -> (usize, usize) {
        let shard_idx = exact_state_hash.shard_index() & (TURN_ROLLOUT_SHARDS - 1);
        let lo = exact_state_hash.0 as usize;
        let set_idx =
            (lo ^ (lo >> 16) ^ (cfg_hash as usize)) & (TURN_ROLLOUT_SETS_PER_SHARD - 1);
        (shard_idx, set_idx)
    }

    #[inline]
    pub(crate) fn get(
        &self,
        exact_state_hash: StateHash,
        cfg_hash: u64,
    ) -> Option<Option<TurnRolloutEntry>> {
        let epoch = self.epoch.load(Ordering::Relaxed);
        let (shard_idx, set_idx) = Self::slot_indices(exact_state_hash, cfg_hash);
        let shard = self.shards[shard_idx].lock();
        let Some(slots) = shard.slots.as_ref() else {
            return None;
        };
        let base = set_idx * TURN_ROLLOUT_WAYS;
        for w in 0..TURN_ROLLOUT_WAYS {
            let s = &slots[base + w];
            if s.epoch == epoch
                && s.slot_state != 0
                && s.cfg_hash == cfg_hash
                && s.state_hash == exact_state_hash
            {
                if s.slot_state == TURN_ROLLOUT_SLOT_NONE {
                    return Some(None);
                }
                return Some(Some(s.into_entry()));
            }
        }
        None
    }

    #[inline]
    pub(crate) fn insert(
        &self,
        exact_state_hash: StateHash,
        cfg_hash: u64,
        res: Option<TurnRolloutEntry>,
    ) {
        let epoch = self.epoch.load(Ordering::Relaxed);
        let (shard_idx, set_idx) = Self::slot_indices(exact_state_hash, cfg_hash);
        let slot = match res {
            None => TurnRolloutSlot {
                state_hash: exact_state_hash,
                cfg_hash,
                a_scores_at_zero: [0.0; 3],
                b_scores_at_zero: [0.0; 3],
                epoch,
                a_alchemize_count: 0,
                a_empty_potion_slots: 0,
                a_flags: 0,
                b_alchemize_count: 0,
                b_empty_potion_slots: 0,
                b_flags: 0,
                slot_state: TURN_ROLLOUT_SLOT_NONE,
            },
            Some(e) => TurnRolloutSlot {
                state_hash: exact_state_hash,
                cfg_hash,
                a_scores_at_zero: e.a.scores_at_zero,
                b_scores_at_zero: e.b.scores_at_zero,
                epoch,
                a_alchemize_count: e.a.alchemize_count,
                a_empty_potion_slots: e.a.empty_potion_slots,
                a_flags: e.a.flags,
                b_alchemize_count: e.b.alchemize_count,
                b_empty_potion_slots: e.b.empty_potion_slots,
                b_flags: e.b.flags,
                slot_state: TURN_ROLLOUT_SLOT_SOME,
            },
        };
        let shard = &mut *self.shards[shard_idx].lock();
        let slots = shard.slots.get_or_insert_with(|| {
            Box::new([TurnRolloutSlot::EMPTY; TURN_ROLLOUT_SLOTS_PER_SHARD])
        });
        let base = set_idx * TURN_ROLLOUT_WAYS;
        for w in 0..TURN_ROLLOUT_WAYS {
            let cur = &slots[base + w];
            if cur.epoch == epoch
                && cur.slot_state != 0
                && cur.cfg_hash == cfg_hash
                && cur.state_hash == exact_state_hash
            {
                slots[base + w] = slot;
                return;
            }
        }
        for w in 0..TURN_ROLLOUT_WAYS {
            let cur = &slots[base + w];
            if cur.epoch != epoch || cur.slot_state == 0 {
                slots[base + w] = slot;
                return;
            }
        }
        let bit = 1u32 << set_idx;
        let way = usize::from((shard.victim_bits & bit) != 0);
        shard.victim_bits ^= bit;
        slots[base + way] = slot;
    }
}

struct PilotIntentEntry {
    handle: CreatureHandle,
    monster_id: MonsterId,
    move_id: Cow<'static, str>,
    hits: SmallVec<[(Dec, i32); 2]>,
}

#[derive(Default)]
pub(crate) struct PilotIntentCache {
    entries: Vec<PilotIntentEntry>,
}

impl PilotIntentCache {
    #[inline]
    fn clear(&mut self) {
        self.entries.clear();
    }
}

pub(crate) struct ChoiceProbeWorkspace {
    pub(crate) probe_state: BattleState,
    pub(crate) compact_steps: Vec<CompactRolloutStep>,
    pub(crate) choice_slab: RolloutChoiceSlab,
    pub(crate) sel_buf: SmallVec<[usize; 16]>,
    pub(crate) intent_cache: PilotIntentCache,
    pub(crate) blocked_buf: SmallVec<[CardHandle; 8]>,
    pub(crate) empty_q: SmallVec<[Vec<usize>; 2]>,
    pub(crate) playable_buf: Vec<(CardHandle, usize, i32, i32)>,
    pub(crate) hand_buf: Vec<CardHandle>,
    pub(crate) seen_keys: Vec<RolloutDedupKey>,
}

impl ChoiceProbeWorkspace {
    pub(crate) fn new(static_ctx: Arc<RootStaticContext>) -> Self {
        Self {
            probe_state: empty_scratch_state(static_ctx),
            compact_steps: Vec::with_capacity(32),
            choice_slab: RolloutChoiceSlab::default(),
            sel_buf: SmallVec::new(),
            intent_cache: PilotIntentCache {
                entries: Vec::with_capacity(16),
            },
            blocked_buf: SmallVec::new(),
            empty_q: SmallVec::new(),
            playable_buf: Vec::with_capacity(16),
            hand_buf: Vec::with_capacity(16),
            seen_keys: Vec::with_capacity(16),
        }
    }
}

pub(crate) struct RolloutWorkspace {
    pub(crate) base_state: BattleState,
    pub(crate) pilot_state: BattleState,
    pub(crate) steps_a: Vec<CompactRolloutStep>,
    pub(crate) choices_a: RolloutChoiceSlab,
    pub(crate) steps_b: Vec<CompactRolloutStep>,
    pub(crate) choices_b: RolloutChoiceSlab,
    pub(crate) sel_buf: SmallVec<[usize; 16]>,
    pub(crate) intent_cache: PilotIntentCache,
    pub(crate) blocked_buf: SmallVec<[CardHandle; 8]>,
    pub(crate) empty_q: SmallVec<[Vec<usize>; 2]>,
    pub(crate) playable_buf: Vec<(CardHandle, usize, i32, i32)>,
    pub(crate) hand_buf: Vec<CardHandle>,
    pub(crate) seen_keys: Vec<RolloutDedupKey>,
    pub(crate) results_buf: Vec<(usize, Option<RolloutResult>)>,
}

impl RolloutWorkspace {
    pub(crate) fn new(static_ctx: Arc<RootStaticContext>) -> Self {
        Self {
            base_state: empty_scratch_state(static_ctx.clone()),
            pilot_state: empty_scratch_state(static_ctx),
            steps_a: Vec::with_capacity(32),
            choices_a: RolloutChoiceSlab {
                steps_meta: Vec::with_capacity(32),
                indices: Vec::with_capacity(64),
            },
            steps_b: Vec::with_capacity(32),
            choices_b: RolloutChoiceSlab {
                steps_meta: Vec::with_capacity(32),
                indices: Vec::with_capacity(64),
            },
            sel_buf: SmallVec::new(),
            intent_cache: PilotIntentCache {
                entries: Vec::with_capacity(16),
            },
            blocked_buf: SmallVec::new(),
            empty_q: SmallVec::new(),
            playable_buf: Vec::with_capacity(16),
            hand_buf: Vec::with_capacity(16),
            seen_keys: Vec::with_capacity(16),
            results_buf: Vec::with_capacity(32),
        }
    }
}

struct FastTurnContext {
    player_hp: i32,
    missing_hp: i32,
    player_energy: i32,
    player_str: i32,
    player_dex: i32,
    player_weak: bool,
    player_frail: bool,
    has_active_scaling: bool,
    unblocked_incoming: i32,
    alive_enemies: i32,
    reviving_enemies: i32,
    enemy_count: usize,
    enemy_handles: [CreatureHandle; MAX_FAST_ENEMIES],
    enemy_hp: [i32; MAX_FAST_ENEMIES],
    enemy_block: [i32; MAX_FAST_ENEMIES],
    enemy_incoming: [i32; MAX_FAST_ENEMIES],
    enemy_vuln: [bool; MAX_FAST_ENEMIES],
}

#[inline(always)]
fn cow_str_eq(a: &Cow<'static, str>, b: &Cow<'static, str>) -> bool {
    match (a, b) {
        (Cow::Borrowed(sa), Cow::Borrowed(sb)) => {
            (sa.as_ptr() == sb.as_ptr() && sa.len() == sb.len()) || *sa == *sb
        }
        _ => a.as_ref() == b.as_ref(),
    }
}

#[inline]
fn lookup_cached_intent_hits<'a>(
    intent_cache: &'a mut PilotIntentCache,
    dynamic_hits: &'a mut SmallVec<[(Dec, i32); 2]>,
    state: &BattleState,
    content: &dyn ContentDispatcher,
    eh: CreatureHandle,
    ec: &sts2_kernel::state::CreatureInst,
) -> &'a [(Dec, i32)] {
    let Some(m) = &ec.monster else {
        return &[];
    };
    let mid = m.monster_id;
    let is_dynamic = matches!(
        mid,
        MonsterId::WaterfallGiant | MonsterId::TestSubject | MonsterId::TheForgotten
    );
    if is_dynamic {
        *dynamic_hits = content.monster_attack_intent_hits(state, eh).unwrap_or_default();
        return dynamic_hits.as_slice();
    }
    let move_cow = m.next_move_id.as_ref().unwrap_or(&m.current_state_id);
    if let Some(pos) = intent_cache.entries.iter().position(|e| e.handle == eh) {
        let matched = {
            let entry = &intent_cache.entries[pos];
            entry.monster_id == mid && cow_str_eq(&entry.move_id, move_cow)
        };
        if matched {
            let entry = &intent_cache.entries[pos];
            #[cfg(debug_assertions)]
            {
                let fresh = content
                    .monster_attack_intent_hits(state, eh)
                    .unwrap_or_default();
                debug_assert_eq!(
                    entry.hits, fresh,
                    "PilotIntentCache mismatch for {mid:?} move={}",
                    move_cow.as_ref()
                );
            }
            return entry.hits.as_slice();
        }
        let hits = content
            .monster_attack_intent_hits(state, eh)
            .unwrap_or_default();
        let entry = &mut intent_cache.entries[pos];
        entry.monster_id = mid;
        entry.move_id = move_cow.clone();
        entry.hits = hits;
        return entry.hits.as_slice();
    }
    let hits = content
        .monster_attack_intent_hits(state, eh)
        .unwrap_or_default();
    intent_cache.entries.push(PilotIntentEntry {
        handle: eh,
        monster_id: mid,
        move_id: move_cow.clone(),
        hits,
    });
    intent_cache.entries.last().unwrap().hits.as_slice()
}

fn compute_fast_turn_context(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    root: &RootEvalBaseline,
    intent_cache: &mut PilotIntentCache,
) -> FastTurnContext {
    let pc = state.player_creature();
    let player_hp = pc.map_or(state.player.current_hp, |c| c.current_hp);
    let player_max_hp = pc.map_or(state.player.max_hp, |c| c.max_hp);
    let player_block = pc.map_or(state.player.block, |c| c.block).max(0);

    let mut player_str = 0i32;
    let mut player_dex = 0i32;
    let mut player_focus = 0i32;
    let mut player_vuln = 0i32;
    let mut player_weak = false;
    let mut player_frail = false;
    let mut player_intangible = false;
    let mut player_accelerant = 0i32;
    let mut player_end_turn_block = 0i32;
    let mut has_active_scaling = false;

    if let Some(pcr) = pc {
        for p in &pcr.powers {
            if p.amount == 0 {
                continue;
            }
            match p.id {
                PowerId::StrengthPower => player_str += p.amount,
                PowerId::DexterityPower => player_dex += p.amount,
                PowerId::FocusPower => player_focus += p.amount,
                PowerId::VulnerablePower if p.amount > 0 => player_vuln += p.amount,
                PowerId::WeakPower if p.amount > 0 => player_weak = true,
                PowerId::FrailPower if p.amount > 0 => player_frail = true,
                PowerId::IntangiblePower if p.amount > 0 => player_intangible = true,
                PowerId::AccelerantPower => player_accelerant += p.amount,
                PowerId::PlatingPower if p.amount > 0 => player_end_turn_block += p.amount,
                _ => {}
            }
            if !has_active_scaling
                && p.amount > 0
                && state
                    .fast_power_meta(content, p.id)
                    .is_ok_and(|pm| pm.power_type != PowerType::Debuff)
            {
                has_active_scaling = true;
            }
        }
    }

    let has_cables = root.has_gold_plated_cables;
    let mut frost_passive_block = 0i32;
    if !state.player.orbs.is_empty() {
        has_active_scaling = true;
        for (o_idx, orb) in state.player.orbs.iter().enumerate() {
            if orb.id == OrbId::FrostOrb {
                let single = (2 + player_focus).max(0);
                let mult = if o_idx == 0 && has_cables { 2 } else { 1 };
                frost_passive_block += single * mult;
            }
        }
    }

    let osty_hp = state
        .osty_handle()
        .and_then(|h| state.creature(h))
        .filter(|c| c.is_alive())
        .map_or(0, |c| c.current_hp.max(0));
    let wuppy_hp: i32 = state
        .pet_handles
        .iter()
        .filter_map(|&h| state.creature(h))
        .filter(|c| c.is_alive() && c.monster_id == Some(MonsterId::Wuppy))
        .map(|c| c.current_hp.max(0))
        .sum();

    let mut ctx = FastTurnContext {
        player_hp,
        missing_hp: (player_max_hp - player_hp).max(0),
        player_energy: state.player.energy,
        player_str,
        player_dex,
        player_weak,
        player_frail,
        has_active_scaling,
        unblocked_incoming: 0,
        alive_enemies: 0,
        reviving_enemies: 0,
        enemy_count: 0,
        enemy_handles: [CreatureHandle(0); MAX_FAST_ENEMIES],
        enemy_hp: [0; MAX_FAST_ENEMIES],
        enemy_block: [0; MAX_FAST_ENEMIES],
        enemy_incoming: [0; MAX_FAST_ENEMIES],
        enemy_vuln: [false; MAX_FAST_ENEMIES],
    };

    let mut total_incoming = 0i32;
    let mut dynamic_hits: SmallVec<[(Dec, i32); 2]> = SmallVec::new();

    for &eh in &state.enemy_handles {
        let Some(ec) = state.creature(eh) else {
            continue;
        };
        if ec.is_dead() {
            if is_creature_reviving(state, ec) {
                ctx.reviving_enemies += 1;
            }
            continue;
        }
        if !ec.is_alive() {
            continue;
        }

        ctx.alive_enemies += 1;
        let cur_hp = ec.current_hp.max(0);
        let cur_block = ec.block.max(0);
        let mut enemy_str = 0i32;
        let mut enemy_weak = false;
        let mut enemy_poison = 0i32;
        let mut enemy_vuln = false;
        let mut has_reattach = false;
        let mut has_damage_cap = false;

        for p in &ec.powers {
            match p.id {
                PowerId::StrengthPower => enemy_str += p.amount,
                PowerId::WeakPower if p.amount > 0 => enemy_weak = true,
                PowerId::VulnerablePower if p.amount > 0 => enemy_vuln = true,
                PowerId::PoisonPower if p.amount > 0 => enemy_poison += p.amount,
                PowerId::ReattachPower if p.amount > 0 => has_reattach = true,
                PowerId::IntangiblePower
                | PowerId::BufferPower
                | PowerId::SlipperyPower
                | PowerId::HardToKillPower
                | PowerId::HardenedShellPower
                    if p.amount > 0 =>
                {
                    has_damage_cap = true;
                }
                _ => {}
            }
        }

        let ticks = enemy_poison.min(1 + player_accelerant.max(0));
        let poison_dmg = if ticks > 0 {
            ticks * (2 * enemy_poison - ticks + 1) / 2
        } else {
            0
        };
        let poison_lethal = cur_hp > 0 && poison_dmg >= cur_hp && !has_reattach && !has_damage_cap;
        let mut inc = 0i32;
        if !poison_lethal {
            let hits =
                lookup_cached_intent_hits(intent_cache, &mut dynamic_hits, state, content, eh, ec);
            for &(base_dmg, count) in hits {
                let mut per_hit =
                    (base_dmg.to_i32_trunc().unwrap_or(0) + enemy_str).max(0);
                if enemy_weak {
                    per_hit = if root.has_paper_krane {
                        (per_hit * 60) / 100
                    } else {
                        (per_hit * 75) / 100
                    };
                }
                if player_vuln > 0 {
                    per_hit = if root.has_odd_mushroom {
                        (per_hit * 125) / 100
                    } else {
                        (per_hit * 150) / 100
                    };
                }
                if player_intangible && per_hit > 1 {
                    per_hit = 1;
                }
                inc += per_hit.max(0) * count.max(0);
            }
        }
        total_incoming += inc;

        if ctx.enemy_count < MAX_FAST_ENEMIES {
            let idx = ctx.enemy_count;
            ctx.enemy_handles[idx] = eh;
            ctx.enemy_hp[idx] = cur_hp;
            ctx.enemy_block[idx] = cur_block;
            ctx.enemy_incoming[idx] = inc;
            ctx.enemy_vuln[idx] = enemy_vuln;
            ctx.enemy_count += 1;
        }
    }

    let defense = player_block + player_end_turn_block + frost_passive_block + osty_hp + wuppy_hp;
    ctx.unblocked_incoming = (total_incoming - defense).max(0);
    ctx
}

#[inline]
fn parent_satisfies_hard_potions(
    arena: &NodeArena,
    parent_id: NodeId,
    root: &RootEvalBaseline,
    policy: &SearchPolicy,
) -> bool {
    let pnode = arena.get(parent_id);
    if (pnode.forced_used_mask & root.all_forced_potion_mask) != root.all_forced_potion_mask {
        return false;
    }
    if (pnode.explicit_potion_count as usize) < root.minimum_required_potion_uses {
        return false;
    }
    if policy.potion_policy == SolverPotionPolicy::Disabled {
        let allowed_forced =
            (pnode.forced_used_mask & root.all_forced_potion_mask).count_ones() as u16;
        if u16::from(pnode.explicit_potion_count) > allowed_forced {
            return false;
        }
    }
    true
}

#[inline]
fn count_non_empty_potion_slots(state: &BattleState) -> usize {
    state
        .player
        .potion_slots
        .iter()
        .filter(|s| s.is_some())
        .count()
}

fn collect_rollout_playable_cards(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    hand_buf: &mut Vec<CardHandle>,
    seen_keys: &mut Vec<RolloutDedupKey>,
    playable_buf: &mut Vec<(CardHandle, usize, i32, i32)>,
) {
    playable_buf.clear();
    if state.player.hand.is_empty()
        || sts2_kernel::hooks::is_over_or_ending_checked(state, content).unwrap_or(true)
    {
        return;
    }
    let _ = state.ensure_hook_cache(content);
    hand_buf.clear();
    hand_buf.extend_from_slice(&state.player.hand);
    seen_keys.clear();

    let cache_ready = state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some();
    let comb = state.hook_cache.masks.combat_combined;
    let early_dedup_safe = cache_ready
        && !comb.contains(HookSlot::ShouldPlay)
        && !comb.contains(HookSlot::TryModifyKeywordsInCombat)
        && !comb.contains(HookSlot::TryModifyEnergyCostInCombat)
        && !comb.contains(HookSlot::TryModifyEnergyCostInCombatLate)
        && !comb.contains(HookSlot::TryModifyStarCostInCombat);

    for i in 0..hand_buf.len() {
        let ch = hand_buf[i];
        let Some(card_idx) = state.card_index(ch) else {
            continue;
        };
        let card = state.cards[card_idx];
        if card.pile != PileType::Hand {
            continue;
        }
        let is_unplayable = card.effective_keywords_base().contains(CardKeywords::UNPLAYABLE);
        if is_unplayable {
            continue;
        }
        let card_id = card.id;
        let upgrade_level = card.upgrade_level;
        let has_ext = card.ext_index.is_some();
        let aff_id = card.affliction.map(|a| a.id);
        if early_dedup_safe {
            if let Ok(meta) = state.fast_card_meta(content, card_id) {
                let is_x_e = meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST);
                let is_x_s = meta.flags.contains(CardStaticFlags::IS_X_STAR_COST);
                let local_e = resolve_local_energy_cost_card(state, &card, is_x_e);
                if !is_x_e && local_e > state.player.energy {
                    continue;
                }
                let local_s = if is_x_s {
                    state.player.stars
                } else {
                    resolve_local_star_cost_card(&card, meta.canonical_star_cost)
                };
                if !is_x_s && local_s > state.player.stars {
                    continue;
                }
                let key = RolloutDedupKey::new(
                    card_id,
                    upgrade_level,
                    local_e,
                    local_s,
                    has_ext,
                    aff_id,
                );
                if !seen_keys.is_empty() && seen_keys.contains(&key) {
                    continue;
                }
                if meta.target_type == TargetType::AnyAlly {
                    let has_ally = state.ally_handles.iter().any(|&h| {
                        h != state.player.creature_handle
                            && state.creature(h).is_some_and(|c| c.is_alive())
                    });
                    if !has_ally {
                        continue;
                    }
                }
                if content.card_is_playable(state, ch).unwrap_or(false) {
                    seen_keys.push(key);
                    playable_buf.push((ch, card_idx, local_e, local_s));
                }
                continue;
            }
        }
        let Ok(Some((energy_cost, star_cost))) =
            CombatEngine::can_play_card_with_costs_in_progress_at(state, content, ch, card_idx)
        else {
            continue;
        };
        let key = RolloutDedupKey::new(
            card_id,
            upgrade_level,
            energy_cost,
            star_cost,
            has_ext,
            aff_id,
        );
        if seen_keys.contains(&key) {
            continue;
        }
        seen_keys.push(key);
        playable_buf.push((ch, card_idx, energy_cost, star_cost));
    }
}

fn compute_generic_choice_card_quality(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    ch: CardHandle,
) -> i32 {
    let Some(card) = state.card(ch) else {
        return 0;
    };
    let Ok(meta) = state.fast_card_meta(content, card.id) else {
        return 0;
    };
    if card.has_local_keyword(CardKeywords::UNPLAYABLE) {
        return -450;
    }
    if matches!(meta.card_type, CardType::Curse | CardType::Status) {
        return -400;
    }
    let mut quality = if meta
        .base_tags
        .intersects(CardTags::STRIKE | CardTags::DEFEND)
    {
        60
    } else {
        let mut q = match meta.card_type {
            CardType::Power => 280,
            CardType::Skill => 200,
            CardType::Attack => 190,
            _ => 120,
        };
        q += match meta.rarity {
            CardRarity::Rare | CardRarity::Ancient => 90,
            CardRarity::Uncommon => 50,
            CardRarity::Common | CardRarity::Event => 20,
            _ => 10,
        };
        let setup_weight = state.fast_card_rollout_profile(content, card.id).setup_weight as i32;
        q += (setup_weight * 4).min(140);
        let base_cost = card.base_energy_cost;
        if base_cost == 0 {
            q += 40;
        } else if base_cost == 1 {
            q += 15;
        }
        q
    };
    if card.upgrade_level > 0 {
        quality += 35;
    }
    if !meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST)
        && resolve_local_energy_cost(state, ch, false) == 0
    {
        quality += 30;
    }
    quality
}

fn pick_rollout_choice(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    req: &sts2_kernel::continuation::ChoiceRequest,
    sel_out: &mut SmallVec<[usize; 16]>,
) -> bool {
    sel_out.clear();
    if req.candidates.is_empty() {
        return req.min_count == 0;
    }

    let prefer_lowest = req.source_pile == PileType::Hand;
    let mut scored: SmallVec<[(usize, i32, u32); 32]> = req
        .candidates
        .iter()
        .enumerate()
        .map(|(idx, &ch)| (idx, compute_generic_choice_card_quality(state, content, ch), ch.0))
        .collect();
    scored.sort_unstable_by(|a, b| {
        let q_cmp = if prefer_lowest {
            a.1.cmp(&b.1)
        } else {
            b.1.cmp(&a.1)
        };
        q_cmp.then_with(|| a.2.cmp(&b.2)).then_with(|| a.0.cmp(&b.0))
    });

    if req.min_count == 0 {
        let pick_one = if prefer_lowest {
            scored[0].1 < 0
        } else {
            scored[0].1 >= 0
        };
        if pick_one {
            sel_out.push(scored[0].0);
        }
        return true;
    }

    let take_n = req.min_count.min(scored.len());
    sel_out.extend(scored.iter().take(take_n).map(|x| x.0));
    true
}

fn resolve_step_choices(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    mut outcome: StepOutcome,
    choice_slab: &mut RolloutChoiceSlab,
    sel_buf: &mut SmallVec<[usize; 16]>,
) -> Option<(u32, u16)> {
    let meta_start = choice_slab.steps_meta.len();
    let idx_start = choice_slab.indices.len();
    let mut depth = 0usize;
    while let StepOutcome::NeedsChoice(ref req) = outcome {
        if depth >= 4 || !pick_rollout_choice(state, content, req, sel_buf) {
            choice_slab.truncate_to(meta_start, idx_start);
            return None;
        }
        let Ok(next) = CombatEngine::resume_choice(state, content, sel_buf) else {
            choice_slab.truncate_to(meta_start, idx_start);
            return None;
        };
        choice_slab.push_step(sel_buf);
        outcome = next;
        depth += 1;
    }
    Some((
        meta_start as u32,
        (choice_slab.steps_meta.len() - meta_start) as u16,
    ))
}

fn replay_compact_steps(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    steps: &[CompactRolloutStep],
    choices: &RolloutChoiceSlab,
    choice_q: &mut SmallVec<[Vec<usize>; 2]>,
    sel_buf: &mut SmallVec<[usize; 16]>,
) -> Result<u16, sts2_kernel::KernelError> {
    let mut auto_potions = 0u16;
    for step in steps {
        let potions_before = count_non_empty_potion_slots(state);
        choice_q.clear();
        if step.is_end_turn == 0 {
            let target = if step.target_handle == u32::MAX {
                None
            } else {
                Some(CreatureHandle(step.target_handle))
            };
            for c_i in 0..step.choices_len as usize {
                let s = choices.step_indices(step.choices_offset as usize + c_i);
                choice_q.push(s.iter().map(|&x| x as usize).collect());
            }
            let mut out = CombatEngine::play_card(
                state,
                content,
                CardHandle(step.card_handle),
                target,
                choice_q,
            )?;
            while matches!(out, StepOutcome::NeedsChoice(_)) && !choice_q.is_empty() {
                let sel = choice_q.remove(0);
                out = CombatEngine::resume_choice(state, content, &sel)?;
            }
            if matches!(out, StepOutcome::NeedsChoice(_)) {
                return Err(sts2_kernel::KernelError::InvalidAction(
                    "compact rollout replay exhausted queued choices".to_owned(),
                ));
            }
        } else {
            let mut out = CombatEngine::end_player_turn(state, content)?;
            let mut c_idx = 0usize;
            while matches!(out, StepOutcome::NeedsChoice(_)) {
                if c_idx >= step.choices_len as usize {
                    return Err(sts2_kernel::KernelError::InvalidAction(
                        "compact rollout end-turn replay missing choice".to_owned(),
                    ));
                }
                let s = choices.step_indices(step.choices_offset as usize + c_idx);
                sel_buf.clear();
                sel_buf.extend(s.iter().map(|&x| x as usize));
                out = CombatEngine::resume_choice(state, content, sel_buf)?;
                c_idx += 1;
            }
        }
        let potions_after = count_non_empty_potion_slots(state);
        auto_potions += potions_before.saturating_sub(potions_after) as u16;
    }
    Ok(auto_potions)
}

#[inline]
fn read_comparison_var(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    ch: CardHandle,
    name: &str,
) -> Option<i32> {
    let vals = content.card_comparison_base_values(state, ch).ok()?;
    for (k, v) in vals {
        if k == name {
            return Some(v.to_i32_trunc().unwrap_or(0).max(0));
        }
    }
    None
}

fn select_best_pilot_move(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    playable: &[(CardHandle, usize, i32, i32)],
    blocked: &[CardHandle],
    ctx: &FastTurnContext,
    pilot: PilotKind,
) -> Option<(CardHandle, Option<CreatureHandle>)> {
    let mut has_non_retained = false;
    if pilot == PilotKind::SetupFirst {
        for &(ch, card_idx, _, _) in playable {
            if !blocked.is_empty() && blocked.contains(&ch) {
                continue;
            }
            let card = &state.cards[card_idx];
            let Ok(meta) = state.fast_card_meta(content, card.id) else {
                continue;
            };
            if matches!(meta.card_type, CardType::Status | CardType::Curse) {
                continue;
            }
            let is_retained = card.has_local_keyword(CardKeywords::RETAIN)
                || card
                    .temp_flags
                    .contains(sts2_kernel::state::CardTempFlags::TEMPORARY_RETAIN_TURN);
            if !is_retained {
                has_non_retained = true;
                break;
            }
        }
    }

    let lethal_threat = ctx.unblocked_incoming >= ctx.player_hp;
    let mut best: Option<(i32, CardHandle, Option<CreatureHandle>)> = None;

    for &(ch, card_idx, raw_e_cost, raw_s_cost) in playable {
        if !blocked.is_empty() && blocked.contains(&ch) {
            continue;
        }
        let card = &state.cards[card_idx];
        let Ok(meta) = state.fast_card_meta(content, card.id) else {
            continue;
        };

        let profile = state.fast_card_rollout_profile(content, card.id);
        let energy_cost = raw_e_cost.max(0);
        let star_cost = raw_s_cost.max(0);
        let exhausts = card.has_local_keyword(CardKeywords::EXHAUST)
            || card
                .temp_flags
                .contains(sts2_kernel::state::CardTempFlags::EXHAUST_ON_NEXT_PLAY);
        let is_retained = card.has_local_keyword(CardKeywords::RETAIN)
            || card
                .temp_flags
                .contains(sts2_kernel::state::CardTempFlags::TEMPORARY_RETAIN_TURN);

        let flags = profile.op_flags;
        let has_draw = (flags & op::HAS_DRAW) != 0;
        let has_energy_or_star_gain = (flags & op::HAS_ENERGY_OR_STAR_GAIN) != 0;
        let has_engine_op = (flags & op::HAS_ENGINE_OP) != 0;
        let has_self_buff = (flags & op::HAS_SELF_BUFF) != 0;
        let has_enemy_debuff = (flags & op::HAS_ENEMY_DEBUFF) != 0;
        let has_heal = meta.flags.contains(CardStaticFlags::MAY_HEAL);
        let has_self_hp_loss = (flags & op::HAS_SELF_HP_LOSS) != 0;
        let has_block_op =
            (flags & op::HAS_BLOCK) != 0 || meta.flags.contains(CardStaticFlags::GAINS_BLOCK);
        let has_attack_op =
            (flags & op::HAS_ATTACK) != 0 || meta.card_type == CardType::Attack;

        let eff_target = state
            .fast_card_resolved_target_type(content, ch, card.id, meta)
            .unwrap_or(meta.target_type);

        // Avoid wasting energy on non-helpful Status/Curse cards.
        if matches!(meta.card_type, CardType::Status | CardType::Curse) {
            let status_prio =
                if exhausts && !has_self_hp_loss && energy_cost == 0 && star_cost == 0 {
                    80
                } else if exhausts
                    && !has_self_hp_loss
                    && ctx.unblocked_incoming == 0
                    && ctx.player_energy > energy_cost
                {
                    25
                } else {
                    -200
                };
            if status_prio > 0 && best.is_none_or(|(bsc, _, _)| status_prio > bsc) {
                match eff_target {
                    TargetType::AnyEnemy => {
                        if let Some(slot) = (0..ctx.enemy_count).find(|&s| ctx.enemy_hp[s] > 0) {
                            best = Some((status_prio, ch, Some(ctx.enemy_handles[slot])));
                        }
                    }
                    TargetType::AnyAlly => {
                        if let Some(ah) = state.ally_handles.iter().copied().find(|&ah| {
                            ah != state.player.creature_handle
                                && state.creature(ah).is_some_and(|c| c.is_alive())
                        }) {
                            best = Some((status_prio, ch, Some(ah)));
                        }
                    }
                    TargetType::AnyPlayer => {
                        if state.player_creature().is_some_and(|c| c.is_alive()) {
                            best = Some((status_prio, ch, Some(state.player.creature_handle)));
                        }
                    }
                    _ => {
                        best = Some((status_prio, ch, None));
                    }
                }
            }
            continue;
        }

        let upg = card.upgrade_level > 0;
        let est_block = if has_block_op {
            let raw_block = match card.id {
                CardId::GeneticAlgorithm => read_comparison_var(state, content, ch, "Block")
                    .unwrap_or_else(|| {
                        if upg {
                            profile.upg_block as i32
                        } else {
                            profile.base_block as i32
                        }
                    }),
                _ => {
                    if upg {
                        profile.upg_block as i32
                    } else {
                        profile.base_block as i32
                    }
                }
            };
            if raw_block > 0 {
                let mut b = raw_block + ctx.player_dex;
                if ctx.player_frail {
                    b = (b * 75) / 100;
                }
                b.max(0)
            } else {
                5
            }
        } else {
            0
        };

        let (base_dmg, hits, per_hit) = if has_attack_op {
            let base_dmg = if upg {
                profile.upg_damage as i32
            } else {
                profile.base_damage as i32
            };
            let hits = (if upg {
                profile.upg_hits
            } else {
                profile.hits
            })
            .max(1) as i32;
            let mut per_hit = (base_dmg + ctx.player_str).max(0);
            if base_dmg > 0 && ctx.player_weak {
                per_hit = (per_hit * 75) / 100;
            }
            (base_dmg, hits, per_hit)
        } else {
            (0, 1, 0)
        };

        // Precompute non-target-dependent priority components once per card.
        if has_self_hp_loss && ctx.player_hp <= 3 {
            // Check lethal for AnyEnemy below only if base_dmg > 0; otherwise -500 <= 0.
            if eff_target != TargetType::AnyEnemy && !(has_attack_op && base_dmg > 0) {
                continue;
            }
        }

        let mut common_prio = 50i32;
        if energy_cost == 0 && star_cost == 0 {
            if has_energy_or_star_gain {
                common_prio += 2_400;
            } else if has_draw && !has_self_hp_loss {
                common_prio += 1_800;
            } else if meta.card_type == CardType::Power {
                common_prio += 2_200;
            } else {
                common_prio += 250;
            }
        } else if has_energy_or_star_gain && energy_cost <= 1 {
            common_prio += 1_300;
        }
        if has_heal && ctx.missing_hp > 0 {
            common_prio += ctx.missing_hp.min(20) * 35;
        }

        let useful_block = if ctx.unblocked_incoming > 0 {
            est_block.min(ctx.unblocked_incoming)
        } else {
            0
        };

        let finish_score = |est_total_dmg: i32,
                            kills_count: i32,
                            killed_incoming: i32,
                            target_slot: Option<usize>|
         -> i32 {
            if kills_count > 0 && kills_count >= ctx.alive_enemies && ctx.reviving_enemies == 0 {
                return 1_000_000 + est_total_dmg * 10 - energy_cost;
            }
            if has_self_hp_loss && ctx.player_hp <= 3 {
                return -500;
            }

            let mut prio = common_prio;
            if kills_count > 0 {
                prio += kills_count * 1_600;
                if killed_incoming > 0 {
                    prio += killed_incoming.min(ctx.unblocked_incoming + 10)
                        * if lethal_threat { 260 } else { 140 };
                }
            }

            match pilot {
                PilotKind::DefensiveTempo => {
                    if ctx.unblocked_incoming > 0 {
                        if useful_block > 0 {
                            prio += useful_block * if lethal_threat { 260 } else { 135 } + 450;
                        } else if est_block > 0 {
                            prio += est_block * 15;
                        }
                        if has_enemy_debuff {
                            let targets_attacker =
                                target_slot.is_some_and(|s| ctx.enemy_incoming[s] > 0);
                            prio += if targets_attacker || eff_target == TargetType::AllEnemies {
                                420
                            } else {
                                180
                            };
                        }
                    } else if est_block > 0 {
                        prio += est_block.min(15) * 6;
                    }

                    if est_total_dmg > 0 {
                        prio += est_total_dmg * 45;
                    }
                    if meta.card_type == CardType::Power {
                        prio += if ctx.unblocked_incoming == 0 {
                            520 + (profile.setup_weight as i32) * 15
                        } else if lethal_threat {
                            100
                        } else {
                            240
                        };
                    }
                    if has_draw {
                        prio += 320;
                    }
                    if has_engine_op {
                        prio += 340;
                    }
                    if has_self_buff {
                        prio += 280;
                    }
                    if ctx.unblocked_incoming == 0 && energy_cost > 0 {
                        prio += energy_cost * 90;
                    }
                    if eff_target == TargetType::AnyEnemy {
                        if let Some(slot) = target_slot {
                            let hp = ctx.enemy_hp[slot];
                            if hp > 0 {
                                prio += (200 - hp.min(200)).max(0);
                                if ctx.enemy_incoming[slot] > 0 {
                                    prio += 60;
                                }
                            }
                        }
                    }
                }
                PilotKind::SetupFirst => {
                    if lethal_threat && useful_block > 0 {
                        prio += useful_block * 240 + 1_200;
                    }
                    if meta.card_type == CardType::Power {
                        prio += 1_550 + (profile.setup_weight as i32).clamp(0, 30) * 25;
                    }
                    if has_engine_op {
                        prio += 950;
                    }
                    if has_self_buff {
                        prio += 900;
                    }
                    if has_draw {
                        prio += 820;
                    }
                    if has_enemy_debuff {
                        prio += 800;
                    }
                    if ctx.unblocked_incoming > 0 && useful_block > 0 && !lethal_threat {
                        prio += useful_block * 70 + 220;
                    }
                    if est_total_dmg > 0 {
                        prio += est_total_dmg * 32;
                        if is_retained
                            && kills_count == 0
                            && (has_non_retained || ctx.has_active_scaling)
                        {
                            prio -= 420;
                        }
                    }
                    if eff_target == TargetType::AnyEnemy {
                        if let Some(slot) = target_slot {
                            let hp = ctx.enemy_hp[slot];
                            if hp > 0 {
                                if has_enemy_debuff && !has_attack_op {
                                    prio += hp.min(250);
                                } else {
                                    prio += (200 - hp.min(200)).max(0);
                                }
                            }
                        }
                    }
                }
            }

            if has_self_hp_loss && !lethal_threat {
                prio -= 180;
            }
            prio
        };

        match eff_target {
            TargetType::AnyEnemy => {
                for slot in 0..ctx.enemy_count {
                    let hp = ctx.enemy_hp[slot];
                    if hp <= 0 {
                        continue;
                    }
                    let (est_total_dmg, kills_count, killed_incoming) = if has_attack_op {
                        if base_dmg > 0 {
                            let scaled_hit = if ctx.enemy_vuln[slot] {
                                (per_hit * 150) / 100
                            } else {
                                per_hit
                            };
                            let single = scaled_hit * hits;
                            let mut kills = 0i32;
                            let mut kill_inc = 0i32;
                            if single >= hp + ctx.enemy_block[slot] {
                                kills = 1;
                                kill_inc = ctx.enemy_incoming[slot];
                            }
                            (single, kills, kill_inc)
                        } else {
                            (6, 0, 0)
                        }
                    } else {
                        (0, 0, 0)
                    };
                    let sc = finish_score(est_total_dmg, kills_count, killed_incoming, Some(slot));
                    if sc > 0 && best.is_none_or(|(bsc, _, _)| sc > bsc) {
                        best = Some((sc, ch, Some(ctx.enemy_handles[slot])));
                    }
                }
            }
            _ => {
                let target_opt = match eff_target {
                    TargetType::AnyAlly => {
                        let Some(ah) = state.ally_handles.iter().copied().find(|&ah| {
                            ah != state.player.creature_handle
                                && state.creature(ah).is_some_and(|c| c.is_alive())
                        }) else {
                            continue;
                        };
                        Some(ah)
                    }
                    TargetType::AnyPlayer => {
                        if !state.player_creature().is_some_and(|c| c.is_alive()) {
                            continue;
                        }
                        Some(state.player.creature_handle)
                    }
                    _ => None,
                };

                let (est_total_dmg, kills_count, killed_incoming) = if has_attack_op {
                    if base_dmg > 0 {
                        if eff_target == TargetType::AllEnemies {
                            let mut total = 0i32;
                            let mut kills = 0i32;
                            let mut kill_inc = 0i32;
                            for slot in 0..ctx.enemy_count {
                                let hp = ctx.enemy_hp[slot];
                                if hp <= 0 {
                                    continue;
                                }
                                let scaled_hit = if ctx.enemy_vuln[slot] {
                                    (per_hit * 150) / 100
                                } else {
                                    per_hit
                                };
                                let dmg = scaled_hit * hits;
                                total += dmg;
                                if dmg >= hp + ctx.enemy_block[slot] {
                                    kills += 1;
                                    kill_inc += ctx.enemy_incoming[slot];
                                }
                            }
                            (total, kills, kill_inc)
                        } else {
                            (per_hit * hits, 0, 0)
                        }
                    } else {
                        let total = if eff_target == TargetType::AllEnemies {
                            6 * ctx.alive_enemies.max(1)
                        } else {
                            6
                        };
                        (total, 0, 0)
                    }
                } else {
                    (0, 0, 0)
                };

                let sc = finish_score(est_total_dmg, kills_count, killed_incoming, None);
                if sc > 0 && best.is_none_or(|(bsc, _, _)| sc > bsc) {
                    best = Some((sc, ch, target_opt));
                }
            }
        }
    }

    best.map(|(_, ch, tgt)| (ch, tgt))
}

#[allow(clippy::too_many_arguments)]
fn run_pilot_turns<R>(
    state: &mut BattleState,
    compact_steps: &mut Vec<CompactRolloutStep>,
    choice_slab: &mut RolloutChoiceSlab,
    sel_buf: &mut SmallVec<[usize; 16]>,
    intent_cache: &mut PilotIntentCache,
    mut restore_base: R,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    pilot: PilotKind,
    horizon_turns: usize,
    blocked_buf: &mut SmallVec<[CardHandle; 8]>,
    empty_q: &mut SmallVec<[Vec<usize>; 2]>,
    playable_buf: &mut Vec<(CardHandle, usize, i32, i32)>,
    hand_buf: &mut Vec<CardHandle>,
    seen_keys: &mut Vec<RolloutDedupKey>,
) -> Option<(usize, u16)>
where
    R: FnMut(&mut BattleState) -> bool,
{
    compact_steps.clear();
    choice_slab.clear();
    intent_cache.clear();
    let mut total_steps = 0usize;
    let mut auto_potions_used = 0u16;

    for _turn in 0..horizon_turns {
        if !state.is_in_progress
            || state.won
            || !state.has_living_player_creature()
            || total_steps >= MAX_TOTAL_ROLLOUT_STEPS
        {
            break;
        }

        blocked_buf.clear();
        intent_cache.clear();
        for _step in 0..MAX_ROLLOUT_ACTIONS_PER_TURN {
            if !state.is_in_progress
                || state.won
                || state.player.phase != PlayerTurnPhase::Play
                || total_steps >= MAX_TOTAL_ROLLOUT_STEPS
            {
                break;
            }
            collect_rollout_playable_cards(state, content, hand_buf, seen_keys, playable_buf);
            if playable_buf.is_empty() {
                break;
            }
            let ctx = compute_fast_turn_context(state, content, root_baseline, intent_cache);
            let Some((ch, tgt)) =
                select_best_pilot_move(state, content, playable_buf, blocked_buf, &ctx, pilot)
            else {
                break;
            };

            let potions_before = count_non_empty_potion_slots(state);
            empty_q.clear();
            let resolved =
                match CombatEngine::play_card_prechecked(state, content, ch, tgt, empty_q) {
                    Ok(outcome) => resolve_step_choices(state, content, outcome, choice_slab, sel_buf),
                    Err(_) => None,
                };
            let Some((choices_offset, choices_len)) = resolved else {
                if !restore_base(state) {
                    return None;
                }
                intent_cache.clear();
                auto_potions_used = replay_compact_steps(
                    state,
                    content,
                    compact_steps,
                    choice_slab,
                    empty_q,
                    sel_buf,
                )
                .ok()?;
                blocked_buf.push(ch);
                continue;
            };
            let potions_after = count_non_empty_potion_slots(state);
            auto_potions_used += potions_before.saturating_sub(potions_after) as u16;

            total_steps += 1;
            compact_steps.push(CompactRolloutStep {
                card_handle: ch.0,
                target_handle: tgt.map_or(u32::MAX, |h| h.0),
                choices_offset,
                choices_len,
                is_end_turn: 0,
                _pad: 0,
            });
        }

        if !state.is_in_progress
            || state.won
            || !state.has_living_player_creature()
            || state.player.phase != PlayerTurnPhase::Play
            || total_steps >= MAX_TOTAL_ROLLOUT_STEPS
        {
            break;
        }

        let potions_before = count_non_empty_potion_slots(state);
        let Ok(end_out) = CombatEngine::end_player_turn(state, content) else {
            return None;
        };
        let Some((choices_offset, choices_len)) =
            resolve_step_choices(state, content, end_out, choice_slab, sel_buf)
        else {
            return None;
        };
        let potions_after = count_non_empty_potion_slots(state);
        auto_potions_used += potions_before.saturating_sub(potions_after) as u16;
        total_steps += 1;
        compact_steps.push(CompactRolloutStep {
            card_handle: 0,
            target_handle: u32::MAX,
            choices_offset,
            choices_len,
            is_end_turn: 1,
            _pad: 0,
        });
    }

    Some((total_steps, auto_potions_used))
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn evaluate_choice_probe_state(
    ws: &mut ChoiceProbeWorkspace,
    base_state: &BattleState,
    exact_state_hash: StateHash,
    probe_cache: &ChoiceProbeCache,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    profile: &SolverSearchProfile,
    eval_mode: EvalMode,
    max_turns_ahead: usize,
    optional_paid_potion_cost: i32,
    optional_paid_potion_count: usize,
    parent_action_count: u16,
) -> Option<ChoiceProbeResult> {
    let cache_turns = max_turns_ahead.min(4) as u8;
    let cfg_hash = choice_probe_cfg_hash(profile, cache_turns);
    if let Some(cached) = probe_cache.get(exact_state_hash, cfg_hash) {
        return cached.map(|e| {
            e.materialize_result(
                root_baseline,
                eval_mode,
                optional_paid_potion_cost,
                optional_paid_potion_count,
                parent_action_count,
            )
        });
    }
    clone_battle_state_into(base_state, &mut ws.probe_state);
    let turn_slots = (cache_turns as usize) + 1;
    let total_steps = run_pilot_turns(
        &mut ws.probe_state,
        &mut ws.compact_steps,
        &mut ws.choice_slab,
        &mut ws.sel_buf,
        &mut ws.intent_cache,
        |s| {
            clone_battle_state_into(base_state, s);
            true
        },
        content,
        root_baseline,
        PilotKind::DefensiveTempo,
        turn_slots,
        &mut ws.blocked_buf,
        &mut ws.empty_q,
        &mut ws.playable_buf,
        &mut ws.hand_buf,
        &mut ws.seen_keys,
    )
    .map_or(0, |(steps, _)| steps);

    let fast = evaluate_fast(&ws.probe_state, content, root_baseline, None);
    let is_victory = fast.won;
    let is_defeat = fast.player_dead || fast.projected_player_hp <= 0;
    let terminal = is_victory || is_defeat;
    let full = if terminal {
        FullEvalDetails::default()
    } else {
        evaluate_full(&ws.probe_state, content, &fast, root_baseline)
    };
    let linear_adj = !(profile.base_score_only || is_defeat);
    let probe_steps = total_steps.min(u16::MAX as usize) as u16;
    let scores_at_zero =
        compute_scores_at_zero(&fast, &full, root_baseline, profile, linear_adj, probe_steps);
    let entry = CachedProbeEntry {
        scores_at_zero,
        hp_lost: fast.cumulative_hp_lost,
        board_investment: full.board_investment,
        alchemize_count: fast.alchemize_count,
        empty_potion_slots: fast.empty_potion_slots,
        is_victory,
        is_defeat,
        linear_adj,
    };
    probe_cache.insert(exact_state_hash, cfg_hash, Some(entry));
    Some(entry.materialize_result(
        root_baseline,
        eval_mode,
        optional_paid_potion_cost,
        optional_paid_potion_count,
        parent_action_count,
    ))
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn evaluate_choice_probe_packed(
    ws: &mut ChoiceProbeWorkspace,
    packed_state: &[u8],
    exact_state_hash: StateHash,
    probe_cache: &ChoiceProbeCache,
    static_ctx: &Arc<RootStaticContext>,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    profile: &SolverSearchProfile,
    eval_mode: EvalMode,
    max_turns_ahead: usize,
    optional_paid_potion_cost: i32,
    optional_paid_potion_count: usize,
    parent_action_count: u16,
) -> Option<ChoiceProbeResult> {
    let cache_turns = max_turns_ahead.min(4) as u8;
    let cfg_hash = choice_probe_cfg_hash(profile, cache_turns);
    if let Some(cached) = probe_cache.get(exact_state_hash, cfg_hash) {
        return cached.map(|e| {
            e.materialize_result(
                root_baseline,
                eval_mode,
                optional_paid_potion_cost,
                optional_paid_potion_count,
                parent_action_count,
            )
        });
    }
    if unpack_state(packed_state, static_ctx, &mut ws.probe_state).is_err() {
        probe_cache.insert(exact_state_hash, cfg_hash, None);
        return None;
    }
    let turn_slots = (cache_turns as usize) + 1;
    let total_steps = run_pilot_turns(
        &mut ws.probe_state,
        &mut ws.compact_steps,
        &mut ws.choice_slab,
        &mut ws.sel_buf,
        &mut ws.intent_cache,
        |s| unpack_state(packed_state, static_ctx, s).is_ok(),
        content,
        root_baseline,
        PilotKind::DefensiveTempo,
        turn_slots,
        &mut ws.blocked_buf,
        &mut ws.empty_q,
        &mut ws.playable_buf,
        &mut ws.hand_buf,
        &mut ws.seen_keys,
    )
    .map_or(0, |(steps, _)| steps);

    let fast = evaluate_fast(&ws.probe_state, content, root_baseline, None);
    let is_victory = fast.won;
    let is_defeat = fast.player_dead || fast.projected_player_hp <= 0;
    let terminal = is_victory || is_defeat;
    let full = if terminal {
        FullEvalDetails::default()
    } else {
        evaluate_full(&ws.probe_state, content, &fast, root_baseline)
    };
    let linear_adj = !(profile.base_score_only || is_defeat);
    let probe_steps = total_steps.min(u16::MAX as usize) as u16;
    let scores_at_zero =
        compute_scores_at_zero(&fast, &full, root_baseline, profile, linear_adj, probe_steps);
    let entry = CachedProbeEntry {
        scores_at_zero,
        hp_lost: fast.cumulative_hp_lost,
        board_investment: full.board_investment,
        alchemize_count: fast.alchemize_count,
        empty_potion_slots: fast.empty_potion_slots,
        is_victory,
        is_defeat,
        linear_adj,
    };
    probe_cache.insert(exact_state_hash, cfg_hash, Some(entry));
    Some(entry.materialize_result(
        root_baseline,
        eval_mode,
        optional_paid_potion_cost,
        optional_paid_potion_count,
        parent_action_count,
    ))
}

#[inline]
pub(crate) fn encode_choice_probe_rank_score(probe: &ChoiceProbeResult) -> f64 {
    let s = probe
        .score
        .clamp(-1_000_000_000_000_000.0, 1_000_000_000_000_000.0);
    if probe.is_victory {
        2_000_000_000_000_000.0 + s - (probe.hp_lost as f64) * 100_000.0
    } else if probe.is_defeat {
        -2_000_000_000_000_000.0 + s
    } else {
        s
    }
}

struct PilotOutcomeMeta {
    pilot_entry: TurnRolloutPilotEntry,
    score: f64,
    won: bool,
    final_fast: FastEvalSummary,
    auto_potions_used: u16,
    won_key: Option<(StateHash, i32)>,
}

#[allow(clippy::too_many_arguments)]
fn run_pilot_from_base(
    ws: &mut RolloutWorkspace,
    use_slab_b: bool,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    profile: &SolverSearchProfile,
    eval_mode: EvalMode,
    horizon_turns: usize,
    optional_paid_potion_cost: i32,
    optional_paid_potion_count: usize,
    parent_action_count: u16,
    pilot: PilotKind,
    compute_won_key: bool,
) -> Option<PilotOutcomeMeta> {
    let base_state = &ws.base_state;
    clone_battle_state_into(base_state, &mut ws.pilot_state);
    let (steps_out, choices_out) = if use_slab_b {
        (&mut ws.steps_b, &mut ws.choices_b)
    } else {
        (&mut ws.steps_a, &mut ws.choices_a)
    };
    let (total_steps, auto_potions_used) = run_pilot_turns(
        &mut ws.pilot_state,
        steps_out,
        choices_out,
        &mut ws.sel_buf,
        &mut ws.intent_cache,
        |s| {
            clone_battle_state_into(base_state, s);
            true
        },
        content,
        root_baseline,
        pilot,
        horizon_turns,
        &mut ws.blocked_buf,
        &mut ws.empty_q,
        &mut ws.playable_buf,
        &mut ws.hand_buf,
        &mut ws.seen_keys,
    )?;

    let final_fast = evaluate_fast(&ws.pilot_state, content, root_baseline, None);
    let final_terminal =
        final_fast.won || final_fast.player_dead || final_fast.projected_player_hp <= 0;
    let final_full = if final_terminal {
        crate::eval::FullEvalDetails::default()
    } else {
        evaluate_full(&ws.pilot_state, content, &final_fast, root_baseline)
    };
    let pilot_entry = TurnRolloutPilotEntry::from_eval(
        &final_fast,
        &final_full,
        root_baseline,
        profile,
        total_steps,
    );
    let score = pilot_entry.materialize_score(
        root_baseline,
        eval_mode,
        optional_paid_potion_cost,
        optional_paid_potion_count,
        parent_action_count,
    );
    let won_key = if compute_won_key && final_fast.won {
        Some((
            hash_canonical_key(&ws.pilot_state),
            ws.pilot_state.player.turn_number,
        ))
    } else {
        None
    };
    Some(PilotOutcomeMeta {
        pilot_entry,
        score,
        won: final_fast.won,
        final_fast,
        auto_potions_used,
        won_key,
    })
}

#[allow(clippy::too_many_arguments)]
fn evaluate_single_candidate(
    ws: &mut RolloutWorkspace,
    pid: NodeId,
    state_arena: &MainStateArena,
    arena: &NodeArena,
    root_ctx: &Arc<RootStaticContext>,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    eval_mode: EvalMode,
    horizon_turns: usize,
    rollout_cache: Option<&TurnRolloutCache>,
) -> Option<RolloutResult> {
    let pnode = arena.get(pid);
    if pnode.is_terminal() || pnode.state_handle.len == 0 {
        return None;
    }
    let can_promote = parent_satisfies_hard_potions(arena, pid, root_baseline, policy);
    let opt_cost = pnode.optional_paid_potion_cost as i32;
    let opt_count = pnode.optional_paid_potion_count as usize;
    let parent_act = pnode.action_count;
    let pnode_hash = pnode.state_hash;
    let cfg_hash = turn_rollout_cfg_hash(profile, horizon_turns);

    if let Some(cache) = rollout_cache {
        if let Some(cached) = cache.get(pnode_hash, cfg_hash) {
            let Some(entry) = cached else {
                return None;
            };
            let any_won = entry.a.won() || entry.b.won();
            if !(can_promote && any_won) {
                let score_a = entry.a.is_valid().then(|| {
                    (
                        entry.a.won(),
                        entry.a.materialize_score(
                            root_baseline,
                            eval_mode,
                            opt_cost,
                            opt_count,
                            parent_act,
                        ),
                    )
                });
                let score_b = entry.b.is_valid().then(|| {
                    (
                        entry.b.won(),
                        entry.b.materialize_score(
                            root_baseline,
                            eval_mode,
                            opt_cost,
                            opt_count,
                            parent_act,
                        ),
                    )
                });
                let chosen_score = match (score_a, score_b) {
                    (Some((aw, as_)), Some((bw, bs_))) => {
                        if aw != bw {
                            if bw {
                                bs_
                            } else {
                                as_
                            }
                        } else if bs_ > as_ {
                            bs_
                        } else {
                            as_
                        }
                    }
                    (Some((_, as_)), None) => as_,
                    (None, Some((_, bs_))) => bs_,
                    (None, None) => return None,
                };
                return Some(RolloutResult {
                    rollout_score: chosen_score,
                    promoted_victory: None,
                });
            }
        }
    }

    let pbytes = state_arena.get(pnode.state_handle);
    if unpack_state(pbytes, root_ctx, &mut ws.base_state).is_err() {
        return None;
    }
    #[cfg(debug_assertions)]
    debug_assert_eq!(pnode_hash, hash_canonical_key(&ws.base_state));
    if !ws.base_state.is_in_progress
        || ws.base_state.won
        || ws.base_state.pending.is_some()
        || !ws.base_state.has_living_player_creature()
    {
        if let Some(cache) = rollout_cache {
            cache.insert(pnode_hash, cfg_hash, None);
        }
        return None;
    }

    let res_a = run_pilot_from_base(
        ws,
        false,
        content,
        root_baseline,
        profile,
        eval_mode,
        horizon_turns,
        opt_cost,
        opt_count,
        parent_act,
        PilotKind::DefensiveTempo,
        can_promote,
    );
    let res_b = run_pilot_from_base(
        ws,
        true,
        content,
        root_baseline,
        profile,
        eval_mode,
        horizon_turns,
        opt_cost,
        opt_count,
        parent_act,
        PilotKind::SetupFirst,
        can_promote,
    );

    if let Some(cache) = rollout_cache {
        let entry_opt = if res_a.is_none() && res_b.is_none() {
            None
        } else {
            Some(TurnRolloutEntry {
                a: res_a
                    .as_ref()
                    .map_or(TurnRolloutPilotEntry::EMPTY, |r| r.pilot_entry),
                b: res_b
                    .as_ref()
                    .map_or(TurnRolloutPilotEntry::EMPTY, |r| r.pilot_entry),
            })
        };
        cache.insert(pnode_hash, cfg_hash, entry_opt);
    }

    let (winner_is_b, chosen) = match (res_a, res_b) {
        (Some(a), Some(b)) => {
            if a.won != b.won {
                if b.won {
                    (true, b)
                } else {
                    (false, a)
                }
            } else if b.score > a.score {
                (true, b)
            } else {
                (false, a)
            }
        }
        (Some(a), None) => (false, a),
        (None, Some(b)) => (true, b),
        (None, None) => return None,
    };

    let (win_steps, win_choices) = if winner_is_b {
        (&ws.steps_b, &ws.choices_b)
    } else {
        (&ws.steps_a, &ws.choices_a)
    };
    let promoted_victory = if can_promote && chosen.won && !win_steps.is_empty() {
        let (final_state_hash, final_turn) = chosen.won_key?;
        Some(PromotedVictory {
            steps: win_steps.clone(),
            choices: win_choices.clone(),
            final_fast: chosen.final_fast,
            final_turn,
            final_state_hash,
            auto_potions_used: chosen.auto_potions_used,
        })
    } else {
        None
    };

    Some(RolloutResult {
        rollout_score: chosen.score,
        promoted_victory,
    })
}

#[allow(clippy::too_many_arguments)]
pub fn evaluate_turn_boundary_rollouts(
    state_arena: &MainStateArena,
    arena: &NodeArena,
    shortlist: &[NodeId],
    root_ctx: &Arc<RootStaticContext>,
    content: &(dyn ContentDispatcher + Sync),
    root_baseline: &RootEvalBaseline,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    eval_mode: EvalMode,
    horizon_turns: usize,
    deadline: Option<Instant>,
    pool: Option<&rayon::ThreadPool>,
    cancel: &AtomicBool,
) -> Vec<Option<RolloutResult>> {
    let m = shortlist.len();
    if m == 0 || horizon_turns == 0 {
        return vec![None; m];
    }
    let dop = policy
        .max_degree_of_parallelism
        .clamp(1, crate::eval::weights::MAXIMUM_SEARCH_MAX_DEGREE_OF_PARALLELISM);
    let workers = if dop <= 1 || pool.is_none() || m < 4 {
        1
    } else {
        dop.min(m)
    };
    let mut workspaces: Vec<RolloutWorkspace> = (0..workers)
        .map(|_| RolloutWorkspace::new(root_ctx.clone()))
        .collect();
    evaluate_turn_boundary_rollouts_with_workspaces(
        state_arena,
        arena,
        shortlist,
        root_ctx,
        content,
        root_baseline,
        policy,
        profile,
        eval_mode,
        horizon_turns,
        deadline,
        pool,
        cancel,
        &mut workspaces,
        None,
    )
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn evaluate_turn_boundary_rollouts_with_workspaces(
    state_arena: &MainStateArena,
    arena: &NodeArena,
    shortlist: &[NodeId],
    root_ctx: &Arc<RootStaticContext>,
    content: &(dyn ContentDispatcher + Sync),
    root_baseline: &RootEvalBaseline,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    eval_mode: EvalMode,
    horizon_turns: usize,
    deadline: Option<Instant>,
    pool: Option<&rayon::ThreadPool>,
    cancel: &AtomicBool,
    workspaces: &mut [RolloutWorkspace],
    rollout_cache: Option<&TurnRolloutCache>,
) -> Vec<Option<RolloutResult>> {
    let mut out = Vec::with_capacity(shortlist.len());
    evaluate_turn_boundary_rollouts_into(
        state_arena,
        arena,
        shortlist,
        root_ctx,
        content,
        root_baseline,
        policy,
        profile,
        eval_mode,
        horizon_turns,
        deadline,
        pool,
        cancel,
        workspaces,
        rollout_cache,
        &mut out,
    );
    out
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn evaluate_turn_boundary_rollouts_into(
    state_arena: &MainStateArena,
    arena: &NodeArena,
    shortlist: &[NodeId],
    root_ctx: &Arc<RootStaticContext>,
    content: &(dyn ContentDispatcher + Sync),
    root_baseline: &RootEvalBaseline,
    policy: &SearchPolicy,
    profile: &SolverSearchProfile,
    eval_mode: EvalMode,
    horizon_turns: usize,
    deadline: Option<Instant>,
    pool: Option<&rayon::ThreadPool>,
    cancel: &AtomicBool,
    workspaces: &mut [RolloutWorkspace],
    rollout_cache: Option<&TurnRolloutCache>,
    out: &mut Vec<Option<RolloutResult>>,
) {
    let m = shortlist.len();
    out.clear();
    if m == 0 || horizon_turns == 0 || workspaces.is_empty() {
        out.resize_with(m, || None);
        return;
    }

    let dop = policy
        .max_degree_of_parallelism
        .clamp(1, crate::eval::weights::MAXIMUM_SEARCH_MAX_DEGREE_OF_PARALLELISM);
    if dop <= 1 || pool.is_none() || m < 4 || workspaces.len() < 2 {
        let ws = &mut workspaces[0];
        out.reserve(m);
        for &pid in shortlist {
            if cancel.load(Ordering::Relaxed) || deadline.is_some_and(|d| Instant::now() >= d) {
                out.push(None);
                continue;
            }
            out.push(evaluate_single_candidate(
                ws,
                pid,
                state_arena,
                arena,
                root_ctx,
                content,
                root_baseline,
                policy,
                profile,
                eval_mode,
                horizon_turns,
                rollout_cache,
            ));
        }
        return;
    }

    let pool = pool.unwrap();
    let workers = dop.min(m).min(workspaces.len());
    let batch_size = (m / (workers * 4)).clamp(1, 4);
    out.resize_with(m, || None);
    let next_idx = std::sync::atomic::AtomicUsize::new(0);

    pool.scope(|scope| {
        for ws in workspaces[..workers].iter_mut() {
            ws.results_buf.clear();
            let next_ref = &next_idx;
            scope.spawn(move |_| loop {
                if cancel.load(Ordering::Relaxed)
                    || deadline.is_some_and(|d| Instant::now() >= d)
                {
                    break;
                }
                let start = next_ref.fetch_add(batch_size, Ordering::Relaxed);
                if start >= m {
                    break;
                }
                let end = (start + batch_size).min(m);
                for idx in start..end {
                    if cancel.load(Ordering::Relaxed)
                        || deadline.is_some_and(|d| Instant::now() >= d)
                    {
                        break;
                    }
                    let r = evaluate_single_candidate(
                        ws,
                        shortlist[idx],
                        state_arena,
                        arena,
                        root_ctx,
                        content,
                        root_baseline,
                        policy,
                        profile,
                        eval_mode,
                        horizon_turns,
                        rollout_cache,
                    );
                    ws.results_buf.push((idx, r));
                }
            });
        }
    });

    for ws in workspaces[..workers].iter_mut() {
        for (idx, res) in ws.results_buf.drain(..) {
            out[idx] = res;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::eval::{BeamWeightPerturbation, BossHpRelief, RelicCounterEvaluation};

    #[test]
    fn choice_probe_cache_lazy_shards_and_four_way_eviction() {
        let cache = ChoiceProbeCache::new();
        assert!(cache.shards.iter().all(|s| s.lock().slots.is_none()));

        let h0 = StateHash(0x0000_0000_0001_0001u128);
        let cfg_a = choice_probe_cfg_hash(&SolverSearchProfile::default(), 2);
        let cfg_b = choice_probe_cfg_hash(&SolverSearchProfile::default(), 3);
        assert_ne!(cfg_a, cfg_b);

        let r0 = CachedProbeEntry {
            scores_at_zero: [123.5, 223.5, 323.5],
            hp_lost: 4,
            board_investment: 17,
            alchemize_count: 1,
            empty_potion_slots: 2,
            is_victory: true,
            is_defeat: false,
            linear_adj: true,
        };
        let r1 = CachedProbeEntry {
            scores_at_zero: [-45.25, -45.25, -45.25],
            hp_lost: 12,
            board_investment: -3,
            alchemize_count: 0,
            empty_potion_slots: 1,
            is_victory: false,
            is_defeat: true,
            linear_adj: false,
        };

        let (sh0, set0) = ChoiceProbeCache::slot_indices(h0, cfg_a);
        let same_set_hashes: Vec<StateHash> = (1u128..1_000_000u128)
            .filter_map(|k| {
                let candidate = StateHash((k << 64) ^ (h0.0 & 0xFFFF_FFFF_FFFF_FFFF));
                if ChoiceProbeCache::slot_indices(candidate, cfg_a) == (sh0, set0)
                    && candidate != h0
                {
                    Some(candidate)
                } else {
                    None
                }
            })
            .take(4)
            .collect();
        assert_eq!(same_set_hashes.len(), 4);
        let h_a = h0;
        let h_b = same_set_hashes[0];
        let h_c = same_set_hashes[1];
        let h_d = same_set_hashes[2];
        let h_e = same_set_hashes[3];

        cache.insert(h_a, cfg_a, Some(r0));
        assert_eq!(
            cache
                .shards
                .iter()
                .filter(|s| s.lock().slots.is_some())
                .count(),
            1
        );

        cache.insert(h_b, cfg_a, Some(r1));
        cache.insert(h_c, cfg_a, Some(r0));
        cache.insert(h_d, cfg_a, Some(r1));
        cache.insert(h_a, cfg_b, None);

        let got_a = cache.get(h_a, cfg_a).flatten().unwrap();
        assert_eq!(got_a.scores_at_zero[0].to_bits(), r0.scores_at_zero[0].to_bits());
        assert_eq!(got_a.scores_at_zero[1].to_bits(), r0.scores_at_zero[1].to_bits());
        assert_eq!(got_a.scores_at_zero[2].to_bits(), r0.scores_at_zero[2].to_bits());
        assert_eq!(got_a.hp_lost, r0.hp_lost);
        assert_eq!(got_a.board_investment, r0.board_investment);
        assert_eq!(got_a.alchemize_count, r0.alchemize_count);
        assert_eq!(got_a.empty_potion_slots, r0.empty_potion_slots);
        assert_eq!(got_a.is_victory, r0.is_victory);
        assert_eq!(got_a.is_defeat, r0.is_defeat);
        assert_eq!(got_a.linear_adj, r0.linear_adj);

        assert!(cache.get(h_b, cfg_a).is_some());
        assert!(cache.get(h_c, cfg_a).is_some());
        assert!(cache.get(h_d, cfg_a).is_some());
        assert!(matches!(cache.get(h_a, cfg_b), Some(None)));

        cache.insert(h_e, cfg_a, Some(r0));
        assert!(cache.get(h_e, cfg_a).is_some());
        assert!(cache.get(h_a, cfg_a).is_none());
        assert!(cache.get(h_b, cfg_a).is_some());

        cache.clear();
        assert!(cache.get(h_e, cfg_a).is_none());
        assert!(cache.get(h_a, cfg_b).is_none());
    }

    #[test]
    fn turn_rollout_cache_cross_mode_and_linear_materialization() {
        let root = RootEvalBaseline {
            initial_player_hp: 70,
            initial_player_max_hp: 80,
            initial_enemy_count: 2,
            start_turn_number: 1,
            boss_hp_relief: BossHpRelief::ActClearHeal,
            belt_full_at_root: true,
            base_replacement_credit_centi: 900,
            ..Default::default()
        };
        let fast_ongoing = FastEvalSummary {
            base_score: 12_345.5,
            player_hp: 55,
            player_max_hp: 80,
            projected_player_hp: 40,
            cumulative_hp_lost: 15,
            energy: 2,
            stars: 1,
            alchemize_count: 1,
            empty_potion_slots: 2,
            enemy_hp: 88,
            alive_enemy_count: 2,
            ..Default::default()
        };
        let full_ongoing = FullEvalDetails {
            board_investment: 30,
            unblocked_incoming: 10,
            persistent_buff_value: 20,
            playable_card_count: 3,
            ..Default::default()
        };
        let profile = SolverSearchProfile::default();
        let entry_a = TurnRolloutPilotEntry::from_eval(&fast_ongoing, &full_ongoing, &root, &profile, 6);
        let cache = TurnRolloutCache::new();
        let hash = StateHash(0x1234_5678_9ABC_DEF0u128);
        let cfg = turn_rollout_cfg_hash(&profile, 4);
        cache.insert(
            hash,
            cfg,
            Some(TurnRolloutEntry {
                a: entry_a,
                b: TurnRolloutPilotEntry::EMPTY,
            }),
        );
        let hit = cache.get(hash, cfg).flatten().unwrap();
        for &mode in &[EvalMode::Balanced, EvalMode::SetupAndSurvival, EvalMode::Offense] {
            for &(opt_cost, opt_cnt, parent_act) in &[(0, 0, 0), (9, 1, 5), (18, 2, 11)] {
                let got = hit.a.materialize_score(&root, mode, opt_cost, opt_cnt, parent_act);
                let net_c = PotionUsePolicy::compute_net_potion_opportunity_cost_centi(
                    opt_cost,
                    opt_cnt as i32,
                    fast_ongoing.alchemize_count as i32,
                    fast_ongoing.empty_potion_slots as i32,
                    &root,
                );
                let direct = compute_beam_rank_score_with_centi(
                    &fast_ongoing,
                    &full_ongoing,
                    &root,
                    profile.base_score_only,
                    profile.boss_tempo_hp_pricing,
                    profile.beam_weight_perturbation,
                    mode,
                    net_c,
                    parent_act + 6,
                );
                assert!((got - direct).abs() <= 1e-9, "turn rollout mismatch: got={got} direct={direct}");
            }
        }
        cache.clear();
        assert!(cache.get(hash, cfg).is_none());
    }

    #[test]
    fn linear_probe_materialization_matches_direct_rank_score() {
        let root = RootEvalBaseline {
            initial_player_hp: 70,
            initial_player_max_hp: 80,
            initial_enemy_count: 2,
            start_turn_number: 1,
            boss_hp_relief: BossHpRelief::ActClearHeal,
            belt_full_at_root: true,
            base_replacement_credit_centi: 900,
            ..Default::default()
        };
        let fast_ongoing = FastEvalSummary {
            base_score: 123_456.5,
            player_hp: 58,
            player_max_hp: 80,
            projected_player_hp: 45,
            cumulative_hp_lost: 12,
            death_save_hp_restored: 0,
            strategic_hp_credit: 200,
            death_save_use_count: 0,
            combat_ended_turn: None,
            growth_reward_count: 2,
            relic_eval: RelicCounterEvaluation {
                satisfied_priority: 3,
                distance: 1,
                ..Default::default()
            },
            outstanding_stolen: 15,
            enemy_hp: 94,
            enemy_block: 6,
            alive_enemy_count: 2,
            reviving_enemy_count: 1,
            energy: 2,
            stars: 3,
            live_deck_clutter: 1,
            alchemize_count: 1,
            empty_potion_slots: 2,
            won: false,
            player_dead: false,
            forfeits_gold: true,
            ..Default::default()
        };
        let full_ongoing = FullEvalDetails {
            retained_blade_bonus_base: 18,
            board_investment: 42,
            unblocked_incoming: 13,
            end_turn_block: 20,
            incoming_damage: 15,
            next_turn_block: 8,
            next_turn_energy: 2,
            next_turn_draw: 1,
            next_turn_stars: 2,
            next_turn_summon: 3,
            doom_lethal_count: 1,
            reviving_hp_penalty: 12,
            persistent_buff_value: 25,
            enemy_debuff_value: 9,
            enemy_threat_power_penalty: 4,
            orb_capacity: 3,
            orb_score: 21,
            osty_hp: 15,
            highest_alive_enemy_hp: 60,
            focus_target_remaining_hp: 34,
            min_sandpit_turns: 3,
            ready_arrows: 2,
            melody_cards_finished: 1,
            playable_card_count: 4,
            zero_cost_playable_count: 1,
            hand_clutter: 1,
            effective_doom_value: 17,
            all_reattach_encounter: false,
            preserves_excess_block: true,
            ..Default::default()
        };
        let mut fast_won = fast_ongoing;
        fast_won.won = true;
        fast_won.enemy_hp = 0;
        fast_won.alive_enemy_count = 0;
        fast_won.reviving_enemy_count = 0;
        fast_won.combat_ended_turn = Some(4);

        let mut fast_dead = fast_ongoing;
        fast_dead.player_dead = true;
        fast_dead.projected_player_hp = 0;

        let modes = [
            EvalMode::Balanced,
            EvalMode::SetupAndSurvival,
            EvalMode::Offense,
        ];
        let perts = [
            None,
            Some(BeamWeightPerturbation {
                term: BeamWeightTerm::CurrentEnergy,
                scale: 1.25,
            }),
            Some(BeamWeightPerturbation {
                term: BeamWeightTerm::PersistentBuffDelta,
                scale: 0.75,
            }),
            Some(BeamWeightPerturbation {
                term: BeamWeightTerm::EnemyHp,
                scale: 1.4,
            }),
        ];

        for (fast, full) in [
            (&fast_ongoing, &full_ongoing),
            (&fast_won, &FullEvalDetails::default()),
            (&fast_dead, &FullEvalDetails::default()),
        ] {
            let is_victory = fast.won;
            let is_defeat = fast.player_dead || fast.projected_player_hp <= 0;
            for &base_only in &[false, true] {
                for &pert in &perts {
                    let profile = SolverSearchProfile {
                        soft_time_budget_ms: 0,
                        base_score_only: base_only,
                        beam_weight_perturbation: pert,
                        ..Default::default()
                    };
                    let probe_steps = 7u16;
                    let linear_adj = !(base_only || is_defeat);
                    let scores_at_zero = compute_scores_at_zero(
                        fast,
                        full,
                        &root,
                        &profile,
                        linear_adj,
                        probe_steps,
                    );
                    let entry = CachedProbeEntry {
                        scores_at_zero,
                        hp_lost: fast.cumulative_hp_lost,
                        board_investment: full.board_investment,
                        alchemize_count: fast.alchemize_count,
                        empty_potion_slots: fast.empty_potion_slots,
                        is_victory,
                        is_defeat,
                        linear_adj,
                    };
                    for &eval_mode in &modes {
                        for &(opt_cost, opt_cnt, p_act) in &[(0, 0, 0), (9, 1, 5), (18, 2, 12)] {
                            let mat = entry.materialize_result(
                                &root,
                                eval_mode,
                                opt_cost,
                                opt_cnt,
                                p_act,
                            );
                            let net_c = PotionUsePolicy::compute_net_potion_opportunity_cost_centi(
                                opt_cost,
                                opt_cnt as i32,
                                fast.alchemize_count as i32,
                                fast.empty_potion_slots as i32,
                                &root,
                            );
                            let direct = compute_beam_rank_score_with_centi(
                                fast,
                                full,
                                &root,
                                profile.base_score_only,
                                profile.boss_tempo_hp_pricing,
                                profile.beam_weight_perturbation,
                                eval_mode,
                                net_c,
                                p_act.saturating_add(probe_steps),
                            );
                            assert!(
                                (mat.score - direct).abs() <= 1e-9,
                                "linear mismatch: mat={} direct={}",
                                mat.score,
                                direct
                            );
                            assert_eq!(mat.hp_lost, fast.cumulative_hp_lost);
                            assert_eq!(mat.board_investment, full.board_investment);
                            assert_eq!(mat.is_victory, fast.won);
                            assert_eq!(mat.is_defeat, is_defeat);
                        }
                    }
                }
            }
        }
    }
}
