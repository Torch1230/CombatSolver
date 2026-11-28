use crate::arena::{MainStateArena, NodeArena, NodeId};
use crate::coordinator::clone_battle_state_into;
use crate::eval::{
    compute_beam_rank_score_with_centi, evaluate_fast, evaluate_full_details_cached,
    is_creature_reviving, BeamWeightTerm, EvalMode, FastEvalSummary, FullEvalDetails,
    IntentHitsCache, PotionUsePolicy, RootEvalBaseline, SolverPotionPolicy,
};
use crate::hash::StateHash;
use crate::packer::{empty_scratch_state, hash_canonical_key, unpack_state};
use crate::portfolio::SolverSearchProfile;
use crate::SearchPolicy;
use smallvec::SmallVec;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::Arc;
use std::time::Instant;
use sts2_kernel::ordering::{
    resolve_local_energy_cost_card, resolve_local_star_cost_card,
};
use sts2_kernel::content_dispatch::{
    CachedHookListener, CardRolloutProfile, GenericChoiceQualityEntry,
};
use sts2_kernel::state::{
    hot_keys, BattleState, CardHandle, CreatureHandle, RootStaticContext,
};
use sts2_kernel::{
    rollout_meta_flags as mf, rollout_op_flags as op, CombatEngine, ContentDispatcher, StepOutcome,
};
use sts2_types::{
    AfflictionId, CardId, CardKeywords, CardRarity, CardStaticFlags, CardTags, CardType, Dec,
    HookSlot, MonsterId, OrbId, PileType, PlayerTurnPhase, PowerId, PowerType, TargetType,
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
    pub steps_meta: SmallVec<[(u32, u16); 4]>,
    pub indices: SmallVec<[u16; 8]>,
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
    pub steps: SmallVec<[CompactRolloutStep; 32]>,
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

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
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

    #[inline(always)]
    const fn try_fast_u64(
        card_id: CardId,
        upgrade_level: i32,
        energy_cost: i32,
        star_cost: i32,
        has_ext: bool,
        aff_id: Option<sts2_types::AfflictionId>,
    ) -> Option<u64> {
        let cid = card_id as u32;
        let upg = upgrade_level as u32;
        let e = (energy_cost.wrapping_add(1)) as u32;
        let s = (star_cost.wrapping_add(1)) as u32;
        let aff_ok = match aff_id {
            None => true,
            Some(a) => (a as u32) < 63,
        };
        if cid < 2048 && upg < 65536 && e < 32768 && s < 32768 && aff_ok {
            let aff_bits = match aff_id {
                None => 0u64,
                Some(a) => (a as u64) + 1,
            };
            Some(
                (cid as u64)
                    | ((has_ext as u64) << 11)
                    | (aff_bits << 12)
                    | ((upg as u64) << 18)
                    | ((e as u64) << 34)
                    | ((s as u64) << 49),
            )
        } else {
            None
        }
    }

    #[inline(always)]
    fn from_fast_u64(raw: u64) -> Self {
        let cid = raw & 0x7FF;
        let has_ext = (raw >> 11) & 1;
        let aff_bits = (raw >> 12) & 0x3F;
        let upg = (raw >> 18) & 0xFFFF;
        let e = (((raw >> 34) & 0x7FFF) as u32).wrapping_sub(1);
        let s = (((raw >> 49) & 0x7FFF) as u32).wrapping_sub(1);
        Self(
            u128::from(cid)
                | (u128::from(has_ext) << 16)
                | (u128::from(aff_bits) << 17)
                | (u128::from(upg) << 32)
                | (u128::from(e) << 64)
                | (u128::from(s) << 96),
        )
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
const CHOICE_PROBE_SETS_PER_SHARD: usize = 1024;
const CHOICE_PROBE_WAYS: usize = 4;
const CHOICE_PROBE_SLOTS_PER_SHARD: usize = CHOICE_PROBE_SETS_PER_SHARD * CHOICE_PROBE_WAYS;
const CHOICE_PROBE_L1_SETS: usize = 512;
const CHOICE_PROBE_L1_WAYS: usize = 2;
const CHOICE_PROBE_L1_SLOTS: usize = CHOICE_PROBE_L1_SETS * CHOICE_PROBE_L1_WAYS;

#[inline(always)]
fn choice_probe_slot_tag(exact_state_hash: StateHash, cfg_hash: u64, epoch: u32) -> u64 {
    let fp32 = ((exact_state_hash.0 >> 64) as u32)
        ^ (cfg_hash as u32)
        ^ ((cfg_hash >> 32) as u32);
    (fp32 as u64) | ((epoch as u64) << 32)
}
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
        | (u64::from(profile.base_score_only) << 12);
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

struct ChoiceProbeShardTable {
    tags: Box<[[u64; CHOICE_PROBE_WAYS]; CHOICE_PROBE_SETS_PER_SHARD]>,
    slots: Box<[ChoiceProbeSlot; CHOICE_PROBE_SLOTS_PER_SHARD]>,
    victim_cursor: Box<[u8; CHOICE_PROBE_SETS_PER_SHARD]>,
}

impl ChoiceProbeShardTable {
    #[cold]
    #[inline(never)]
    fn alloc() -> Self {
        Self {
            tags: vec![[0u64; CHOICE_PROBE_WAYS]; CHOICE_PROBE_SETS_PER_SHARD]
                .into_boxed_slice()
                .try_into()
                .unwrap_or_else(|_| unreachable!()),
            slots: vec![ChoiceProbeSlot::EMPTY; CHOICE_PROBE_SLOTS_PER_SHARD]
                .into_boxed_slice()
                .try_into()
                .unwrap_or_else(|_| unreachable!()),
            victim_cursor: vec![0u8; CHOICE_PROBE_SETS_PER_SHARD]
                .into_boxed_slice()
                .try_into()
                .unwrap_or_else(|_| unreachable!()),
        }
    }
}

#[derive(Default)]
struct ChoiceProbeShard {
    table: Option<ChoiceProbeShardTable>,
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
                if let Some(tbl) = s.table.as_mut() {
                    tbl.tags.fill([0u64; CHOICE_PROBE_WAYS]);
                    for slot in tbl.slots.iter_mut() {
                        slot.flags = 0;
                        slot.epoch = 0;
                    }
                    tbl.victim_cursor.fill(0);
                }
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

    #[inline(always)]
    fn table_get(
        tbl: &ChoiceProbeShardTable,
        set_idx: usize,
        want_tag: u64,
        exact_state_hash: StateHash,
        cfg_hash: u64,
        epoch: u32,
    ) -> Option<Option<CachedProbeEntry>> {
        let tag_row = tbl.tags[set_idx];
        let base = set_idx * CHOICE_PROBE_WAYS;
        for w in 0..CHOICE_PROBE_WAYS {
            if tag_row[w] == want_tag {
                let s = &tbl.slots[base + w];
                if s.state_hash == exact_state_hash
                    && s.epoch == epoch
                    && s.flags != 0
                    && s.cfg_hash == cfg_hash
                {
                    if (s.flags & CHOICE_PROBE_FLAG_NONE) != 0 {
                        return Some(None);
                    }
                    return Some(Some(s.into_entry()));
                }
            }
        }
        None
    }

    #[inline(always)]
    fn table_insert(
        tbl: &mut ChoiceProbeShardTable,
        set_idx: usize,
        want_tag: u64,
        exact_state_hash: StateHash,
        cfg_hash: u64,
        epoch: u32,
        slot: ChoiceProbeSlot,
    ) {
        debug_assert_ne!(slot.flags, 0);
        let base = set_idx * CHOICE_PROBE_WAYS;
        let tag_row = &mut tbl.tags[set_idx];
        let slots = &mut tbl.slots;
        let mut empty_way: Option<usize> = None;
        for w in 0..CHOICE_PROBE_WAYS {
            let t = tag_row[w];
            if t == want_tag {
                let cur = &slots[base + w];
                if cur.epoch == epoch
                    && cur.flags != 0
                    && cur.cfg_hash == cfg_hash
                    && cur.state_hash == exact_state_hash
                {
                    slots[base + w] = slot;
                    return;
                }
            } else if empty_way.is_none() && (t >> 32) as u32 != epoch {
                empty_way = Some(w);
            }
        }
        let way = if let Some(w) = empty_way {
            w
        } else {
            let w = usize::from(tbl.victim_cursor[set_idx]) & (CHOICE_PROBE_WAYS - 1);
            tbl.victim_cursor[set_idx] = tbl.victim_cursor[set_idx].wrapping_add(1);
            w
        };
        tag_row[way] = want_tag;
        slots[base + way] = slot;
    }

    #[inline]
    pub(crate) fn get(
        &self,
        exact_state_hash: StateHash,
        cfg_hash: u64,
    ) -> Option<Option<CachedProbeEntry>> {
        let epoch = self.epoch.load(Ordering::Relaxed);
        let (shard_idx, set_idx) = Self::slot_indices(exact_state_hash, cfg_hash);
        let want_tag = choice_probe_slot_tag(exact_state_hash, cfg_hash, epoch);
        let shard = self.shards[shard_idx].lock();
        let tbl = shard.table.as_ref()?;
        Self::table_get(tbl, set_idx, want_tag, exact_state_hash, cfg_hash, epoch)
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
        let want_tag = choice_probe_slot_tag(exact_state_hash, cfg_hash, epoch);
        let slot = Self::build_slot(exact_state_hash, cfg_hash, epoch, res);
        let shard = &mut *self.shards[shard_idx].lock();
        let tbl = shard.table.get_or_insert_with(ChoiceProbeShardTable::alloc);
        Self::table_insert(tbl, set_idx, want_tag, exact_state_hash, cfg_hash, epoch, slot);
    }

    #[inline]
    pub(crate) fn get_single_thread(
        &mut self,
        exact_state_hash: StateHash,
        cfg_hash: u64,
    ) -> Option<Option<CachedProbeEntry>> {
        let epoch = *self.epoch.get_mut();
        let (shard_idx, set_idx) = Self::slot_indices(exact_state_hash, cfg_hash);
        let want_tag = choice_probe_slot_tag(exact_state_hash, cfg_hash, epoch);
        let shard = self.shards[shard_idx].get_mut();
        let tbl = shard.table.as_ref()?;
        Self::table_get(tbl, set_idx, want_tag, exact_state_hash, cfg_hash, epoch)
    }

    #[inline]
    pub(crate) fn insert_single_thread(
        &mut self,
        exact_state_hash: StateHash,
        cfg_hash: u64,
        res: Option<CachedProbeEntry>,
    ) {
        let epoch = *self.epoch.get_mut();
        let (shard_idx, set_idx) = Self::slot_indices(exact_state_hash, cfg_hash);
        let want_tag = choice_probe_slot_tag(exact_state_hash, cfg_hash, epoch);
        let slot = Self::build_slot(exact_state_hash, cfg_hash, epoch, res);
        let shard = self.shards[shard_idx].get_mut();
        let tbl = shard.table.get_or_insert_with(ChoiceProbeShardTable::alloc);
        Self::table_insert(tbl, set_idx, want_tag, exact_state_hash, cfg_hash, epoch, slot);
    }

    #[inline(always)]
    fn build_slot(
        exact_state_hash: StateHash,
        cfg_hash: u64,
        epoch: u32,
        res: Option<CachedProbeEntry>,
    ) -> ChoiceProbeSlot {
        match res {
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
        }
    }

    #[inline(always)]
    fn l1_set_index(exact_state_hash: StateHash, cfg_hash: u64) -> usize {
        let lo = exact_state_hash.0 as usize;
        (lo ^ (lo >> 16) ^ (cfg_hash as usize)) & (CHOICE_PROBE_L1_SETS - 1)
    }

    #[inline(always)]
    fn write_l1(
        l1: &mut ChoiceProbeL1,
        epoch: u32,
        exact_state_hash: StateHash,
        cfg_hash: u64,
        res: Option<CachedProbeEntry>,
    ) {
        let tbl = l1.table.get_or_insert_with(ChoiceProbeL1Table::alloc);
        if l1.epoch != epoch {
            if epoch < l1.epoch {
                tbl.tags.fill([0u64; CHOICE_PROBE_L1_WAYS]);
                for slot in tbl.slots.iter_mut() {
                    slot.flags = 0;
                    slot.epoch = 0;
                }
                tbl.cursor.fill(0);
            }
            l1.epoch = epoch;
        }
        let set_idx = Self::l1_set_index(exact_state_hash, cfg_hash);
        let want_tag = choice_probe_slot_tag(exact_state_hash, cfg_hash, epoch);
        let base = set_idx * CHOICE_PROBE_L1_WAYS;
        let slot = Self::build_slot(exact_state_hash, cfg_hash, epoch, res);
        debug_assert_ne!(slot.flags, 0);
        let tag_row = &mut tbl.tags[set_idx];
        let l1_slots = &mut tbl.slots;
        let mut empty_way: Option<usize> = None;
        for w in 0..CHOICE_PROBE_L1_WAYS {
            let t = tag_row[w];
            if t == want_tag {
                let cur = &l1_slots[base + w];
                if cur.epoch == epoch
                    && cur.flags != 0
                    && cur.cfg_hash == cfg_hash
                    && cur.state_hash == exact_state_hash
                {
                    l1_slots[base + w] = slot;
                    return;
                }
            } else if empty_way.is_none() && (t >> 32) as u32 != epoch {
                empty_way = Some(w);
            }
        }
        let way = if let Some(w) = empty_way {
            w
        } else {
            let w = usize::from(tbl.cursor[set_idx]) & (CHOICE_PROBE_L1_WAYS - 1);
            tbl.cursor[set_idx] = tbl.cursor[set_idx].wrapping_add(1);
            w
        };
        tag_row[way] = want_tag;
        l1_slots[base + way] = slot;
    }

    #[inline]
    pub(crate) fn get_with_l1(
        &self,
        l1: &mut ChoiceProbeL1,
        exact_state_hash: StateHash,
        cfg_hash: u64,
    ) -> Option<Option<CachedProbeEntry>> {
        if !l1.use_l1 {
            return self.get(exact_state_hash, cfg_hash);
        }
        let epoch = self.epoch.load(Ordering::Relaxed);
        if l1.epoch == epoch {
            if let Some(tbl) = l1.table.as_ref() {
                let set_idx = Self::l1_set_index(exact_state_hash, cfg_hash);
                let want_tag = choice_probe_slot_tag(exact_state_hash, cfg_hash, epoch);
                let tag_row = tbl.tags[set_idx];
                let base = set_idx * CHOICE_PROBE_L1_WAYS;
                for w in 0..CHOICE_PROBE_L1_WAYS {
                    if tag_row[w] == want_tag {
                        let s = &tbl.slots[base + w];
                        if s.state_hash == exact_state_hash
                            && s.epoch == epoch
                            && s.flags != 0
                            && s.cfg_hash == cfg_hash
                        {
                            if (s.flags & CHOICE_PROBE_FLAG_NONE) != 0 {
                                return Some(None);
                            }
                            return Some(Some(s.into_entry()));
                        }
                    }
                }
            }
        }
        let res = self.get(exact_state_hash, cfg_hash)?;
        Self::write_l1(l1, epoch, exact_state_hash, cfg_hash, res);
        Some(res)
    }

    #[inline]
    pub(crate) fn insert_with_l1(
        &self,
        l1: &mut ChoiceProbeL1,
        exact_state_hash: StateHash,
        cfg_hash: u64,
        res: Option<CachedProbeEntry>,
    ) {
        if !l1.use_l1 {
            self.insert(exact_state_hash, cfg_hash, res);
            return;
        }
        let epoch = self.epoch.load(Ordering::Relaxed);
        Self::write_l1(l1, epoch, exact_state_hash, cfg_hash, res);
        self.insert(exact_state_hash, cfg_hash, res);
    }
}

pub(crate) enum ProbeCacheAccess<'a> {
    SingleThread(&'a mut ChoiceProbeCache),
    Shared(&'a ChoiceProbeCache),
}

impl<'a> ProbeCacheAccess<'a> {
    #[inline(always)]
    pub(crate) fn get_with_l1(
        &mut self,
        l1: &mut ChoiceProbeL1,
        exact_state_hash: StateHash,
        cfg_hash: u64,
    ) -> Option<Option<CachedProbeEntry>> {
        match self {
            Self::SingleThread(cache) => cache.get_single_thread(exact_state_hash, cfg_hash),
            Self::Shared(cache) => cache.get_with_l1(l1, exact_state_hash, cfg_hash),
        }
    }

    #[inline(always)]
    pub(crate) fn insert_with_l1(
        &mut self,
        l1: &mut ChoiceProbeL1,
        exact_state_hash: StateHash,
        cfg_hash: u64,
        res: Option<CachedProbeEntry>,
    ) {
        match self {
            Self::SingleThread(cache) => {
                cache.insert_single_thread(exact_state_hash, cfg_hash, res);
            }
            Self::Shared(cache) => {
                cache.insert_with_l1(l1, exact_state_hash, cfg_hash, res);
            }
        }
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
        | (u64::from(profile.base_score_only) << 16);
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
        let linear_adj = !(profile.base_score_only || is_defeat || fast.is_event_defeat);
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

#[derive(Clone, Copy)]
pub(crate) struct PlayableRolloutCard {
    handle: CardHandle,
    card_id: CardId,
    energy_cost: i32,
    star_cost: i32,
    upg: bool,
    exhausts: bool,
    is_retained: bool,
}

struct ChoiceProbeL1Table {
    tags: Box<[[u64; CHOICE_PROBE_L1_WAYS]; CHOICE_PROBE_L1_SETS]>,
    slots: Box<[ChoiceProbeSlot; CHOICE_PROBE_L1_SLOTS]>,
    cursor: Box<[u8; CHOICE_PROBE_L1_SETS]>,
}

impl ChoiceProbeL1Table {
    #[cold]
    #[inline(never)]
    fn alloc() -> Self {
        Self {
            tags: vec![[0u64; CHOICE_PROBE_L1_WAYS]; CHOICE_PROBE_L1_SETS]
                .into_boxed_slice()
                .try_into()
                .unwrap_or_else(|_| unreachable!()),
            slots: vec![ChoiceProbeSlot::EMPTY; CHOICE_PROBE_L1_SLOTS]
                .into_boxed_slice()
                .try_into()
                .unwrap_or_else(|_| unreachable!()),
            cursor: vec![0u8; CHOICE_PROBE_L1_SETS]
                .into_boxed_slice()
                .try_into()
                .unwrap_or_else(|_| unreachable!()),
        }
    }
}

pub(crate) struct ChoiceProbeL1 {
    pub(crate) use_l1: bool,
    table: Option<ChoiceProbeL1Table>,
    epoch: u32,
}

impl ChoiceProbeL1 {
    pub(crate) fn new() -> Self {
        Self {
            use_l1: false,
            table: None,
            epoch: 0,
        }
    }
}

pub(crate) struct ChoiceProbeWorkspace {
    pub(crate) probe_state: BattleState,
    pub(crate) compact_steps: Vec<CompactRolloutStep>,
    pub(crate) choice_slab: RolloutChoiceSlab,
    pub(crate) sel_buf: SmallVec<[usize; 16]>,
    pub(crate) intent_cache: IntentHitsCache,
    pub(crate) blocked_buf: SmallVec<[CardHandle; 8]>,
    pub(crate) empty_q: SmallVec<[Vec<usize>; 2]>,
    pub(crate) playable_buf: Vec<PlayableRolloutCard>,
    pub(crate) hand_buf: Vec<CardHandle>,
    pub(crate) seen_keys: Vec<RolloutDedupKey>,
    pub(crate) choice_scored_buf: Vec<(usize, i32, u32)>,
    pub(crate) l1: ChoiceProbeL1,
}

impl ChoiceProbeWorkspace {
    pub(crate) fn new(static_ctx: Arc<RootStaticContext>) -> Self {
        Self {
            probe_state: empty_scratch_state(static_ctx),
            compact_steps: Vec::with_capacity(32),
            choice_slab: RolloutChoiceSlab::default(),
            sel_buf: SmallVec::new(),
            intent_cache: IntentHitsCache::new(),
            blocked_buf: SmallVec::new(),
            empty_q: SmallVec::new(),
            playable_buf: Vec::with_capacity(16),
            hand_buf: Vec::with_capacity(16),
            seen_keys: Vec::with_capacity(16),
            choice_scored_buf: Vec::with_capacity(32),
            l1: ChoiceProbeL1::new(),
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
    pub(crate) intent_cache: IntentHitsCache,
    pub(crate) blocked_buf: SmallVec<[CardHandle; 8]>,
    pub(crate) empty_q: SmallVec<[Vec<usize>; 2]>,
    pub(crate) playable_buf: Vec<PlayableRolloutCard>,
    pub(crate) hand_buf: Vec<CardHandle>,
    pub(crate) seen_keys: Vec<RolloutDedupKey>,
    pub(crate) choice_scored_buf: Vec<(usize, i32, u32)>,
    pub(crate) results_buf: Vec<(usize, Option<RolloutResult>)>,
}

impl RolloutWorkspace {
    pub(crate) fn new(static_ctx: Arc<RootStaticContext>) -> Self {
        Self {
            base_state: empty_scratch_state(static_ctx.clone()),
            pilot_state: empty_scratch_state(static_ctx),
            steps_a: Vec::with_capacity(32),
            choices_a: RolloutChoiceSlab {
                steps_meta: SmallVec::with_capacity(32),
                indices: SmallVec::with_capacity(64),
            },
            steps_b: Vec::with_capacity(32),
            choices_b: RolloutChoiceSlab {
                steps_meta: SmallVec::with_capacity(32),
                indices: SmallVec::with_capacity(64),
            },
            sel_buf: SmallVec::new(),
            intent_cache: IntentHitsCache::new(),
            blocked_buf: SmallVec::new(),
            empty_q: SmallVec::new(),
            playable_buf: Vec::with_capacity(16),
            hand_buf: Vec::with_capacity(16),
            seen_keys: Vec::with_capacity(16),
            choice_scored_buf: Vec::with_capacity(32),
            results_buf: Vec::with_capacity(32),
        }
    }
}

#[derive(Clone, Copy)]
struct FastEnemySlot {
    handle: CreatureHandle,
    hp: i32,
    eff_hp: i32,
    incoming: i32,
    target_bonus: i32,
    vuln: bool,
}

struct FastTurnContext {
    player_hp: i32,
    missing_hp: i32,
    player_energy: i32,
    player_str: i32,
    player_dex: i32,
    player_weak: bool,
    player_frail: bool,
    player_has_fan_of_knives: bool,
    player_has_seeking_edge: bool,
    has_active_scaling: bool,
    any_enemy_vuln: bool,
    unblocked_incoming: i32,
    alive_enemies: i32,
    reviving_enemies: i32,
    enemy_count: usize,
    best_non_attack_enemy_slot: Option<usize>,
    best_debuff_enemy_slot: Option<usize>,
    enemies: [std::mem::MaybeUninit<FastEnemySlot>; MAX_FAST_ENEMIES],
}

impl FastTurnContext {
    #[inline(always)]
    fn enemy_slot(&self, slot: usize) -> FastEnemySlot {
        debug_assert!(slot < self.enemy_count);
        unsafe { self.enemies.get_unchecked(slot).assume_init() }
    }

    #[inline(always)]
    fn enemy_handle(&self, slot: usize) -> CreatureHandle {
        self.enemy_slot(slot).handle
    }

    #[inline(always)]
    fn enemy_hp(&self, slot: usize) -> i32 {
        self.enemy_slot(slot).hp
    }

    #[inline(always)]
    fn enemy_incoming(&self, slot: usize) -> i32 {
        self.enemy_slot(slot).incoming
    }

    #[inline(always)]
    fn enemy_target_bonus(&self, slot: usize) -> i32 {
        self.enemy_slot(slot).target_bonus
    }

    #[inline(always)]
    fn enemy_vuln(&self, slot: usize) -> bool {
        self.enemy_slot(slot).vuln
    }
}

#[inline(always)]
fn compute_fast_turn_context<const IS_SETUP: bool>(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    root: &RootEvalBaseline,
    intent_cache: &mut IntentHitsCache,
) -> FastTurnContext {
    let mut out = std::mem::MaybeUninit::<FastTurnContext>::uninit();
    compute_fast_turn_context_into::<IS_SETUP>(state, content, root, intent_cache, &mut out);
    unsafe { out.assume_init() }
}

fn compute_fast_turn_context_into<'a, const IS_SETUP: bool>(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    root: &RootEvalBaseline,
    intent_cache: &mut IntentHitsCache,
    out: &'a mut std::mem::MaybeUninit<FastTurnContext>,
) -> &'a FastTurnContext {
    let creatures_slice = state.creatures.as_slice();
    let creatures_contiguous = state.handle_layout.creatures;
    let get_creature = |h: CreatureHandle| -> Option<&sts2_kernel::state::CreatureInst> {
        let idx = h.0.wrapping_sub(1) as usize;
        if creatures_contiguous {
            if idx < creatures_slice.len() {
                let c = unsafe { creatures_slice.get_unchecked(idx) };
                #[cfg(debug_assertions)]
                debug_assert_eq!(c.handle, h);
                Some(c)
            } else {
                None
            }
        } else {
            state.creature(h)
        }
    };
    let pc = get_creature(state.player.creature_handle);
    let player_hp = pc.map_or(state.player.current_hp, |c| c.current_hp);
    let player_max_hp = pc.map_or(state.player.max_hp, |c| c.max_hp);
    let player_block = pc.map_or(state.player.block, |c| c.block).max(0);

    let mut player_str = 0i32;
    let mut player_dex = 0i32;
    let mut player_focus = 0i32;
    let mut player_vuln = 0i32;
    let mut player_weak = false;
    let mut player_frail = false;
    let mut player_has_fan_of_knives = false;
    let mut player_has_seeking_edge = false;
    let mut player_intangible = false;
    let mut player_accelerant = 0i32;
    let mut player_end_turn_block = 0i32;
    let mut has_active_scaling = false;

    if let Some(pcr) = pc {
        let tables = if IS_SETUP {
            state.hook_cache.cached_tables
        } else {
            None
        };
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
                PowerId::FanOfKnivesPower if p.amount > 0 => player_has_fan_of_knives = true,
                PowerId::SeekingEdgePower if p.amount > 0 => player_has_seeking_edge = true,
                PowerId::IntangiblePower if p.amount > 0 => player_intangible = true,
                PowerId::AccelerantPower => player_accelerant += p.amount,
                PowerId::PlatingPower if p.amount > 0 => player_end_turn_block += p.amount,
                _ => {}
            }
            if IS_SETUP && !has_active_scaling && p.amount > 0 {
                let pm_res = match tables.and_then(|t| t.power_metas.get(p.id as usize)) {
                    Some(pm) => Ok(pm),
                    None => content.power_meta(p.id),
                };
                if pm_res.is_ok_and(|pm| pm.power_type != PowerType::Debuff) {
                    has_active_scaling = true;
                }
            }
        }
    }

    let has_cables = root.has_gold_plated_cables;
    let mut frost_passive_block = 0i32;
    if !state.player.orbs.is_empty() {
        if IS_SETUP {
            has_active_scaling = true;
        }
        for (o_idx, orb) in state.player.orbs.iter().enumerate() {
            if orb.id == OrbId::FrostOrb {
                let single = (2 + player_focus).max(0);
                let mult = if o_idx == 0 && has_cables { 2 } else { 1 };
                frost_passive_block += single * mult;
            }
        }
    }

    let (osty_hp, wuppy_hp) = if state.pet_handles.is_empty() {
        (0, 0)
    } else {
        let osty = state
            .osty_handle()
            .and_then(get_creature)
            .filter(|c| c.is_alive())
            .map_or(0, |c| c.current_hp.max(0));
        let wuppy: i32 = state
            .pet_handles
            .iter()
            .filter_map(|&h| get_creature(h))
            .filter(|c| c.is_alive() && c.monster_id == Some(MonsterId::Wuppy))
            .map(|c| c.current_hp.max(0))
            .sum();
        (osty, wuppy)
    };

    let ptr = out.as_mut_ptr();
    let mut any_enemy_vuln = false;
    let mut alive_enemies = 0i32;
    let mut reviving_enemies = 0i32;
    let mut enemy_count = 0usize;
    let mut best_non_attack_enemy_slot: Option<usize> = None;
    let mut best_debuff_enemy_slot: Option<usize> = None;

    let mut total_incoming = 0i32;
    let mut best_non_atk_bonus = i32::MIN;
    let mut best_deb_score = i32::MIN;
    let multi_enemy_target_cache = !IS_SETUP && state.enemy_handles.len() > 1;

    for &eh in &state.enemy_handles {
        let Some(ec) = get_creature(eh) else {
            continue;
        };
        if ec.is_dead() {
            if is_creature_reviving(state, ec) {
                reviving_enemies += 1;
            }
            continue;
        }
        if !ec.is_alive() {
            continue;
        }

        alive_enemies += 1;
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
            let hits = intent_cache.lookup_i32(state, content, eh, ec);
            for &(base_dmg, count) in hits {
                let mut per_hit = (base_dmg + enemy_str).max(0);
                if enemy_weak {
                    per_hit = if root.has_paper_krane {
                        (per_hit * 60) / 100
                    } else {
                        let res = (per_hit * 3) >> 2;
                        #[cfg(debug_assertions)]
                        debug_assert_eq!(res, (per_hit * 75) / 100);
                        res
                    };
                }
                if player_vuln > 0 {
                    per_hit = if root.has_odd_mushroom {
                        let res = (per_hit * 5) >> 2;
                        #[cfg(debug_assertions)]
                        debug_assert_eq!(res, (per_hit * 125) / 100);
                        res
                    } else {
                        let res = (per_hit * 3) >> 1;
                        #[cfg(debug_assertions)]
                        debug_assert_eq!(res, (per_hit * 150) / 100);
                        res
                    };
                }
                if player_intangible && per_hit > 1 {
                    per_hit = 1;
                }
                inc += per_hit.max(0) * count.max(0);
            }
        }
        total_incoming += inc;

        if enemy_count < MAX_FAST_ENEMIES {
            let idx = enemy_count;
            let t_bonus = if cur_hp > 0 {
                (200 - cur_hp.min(200)).max(0) + if inc > 0 { 60 } else { 0 }
            } else {
                0
            };
            unsafe {
                core::ptr::addr_of_mut!((*ptr).enemies)
                    .cast::<std::mem::MaybeUninit<FastEnemySlot>>()
                    .add(idx)
                    .write(std::mem::MaybeUninit::new(FastEnemySlot {
                        handle: eh,
                        hp: cur_hp,
                        eff_hp: cur_hp + cur_block,
                        incoming: inc,
                        target_bonus: t_bonus,
                        vuln: enemy_vuln,
                    }));
            }
            any_enemy_vuln |= enemy_vuln;
            if multi_enemy_target_cache && cur_hp > 0 {
                if t_bonus > best_non_atk_bonus {
                    best_non_atk_bonus = t_bonus;
                    best_non_attack_enemy_slot = Some(idx);
                }
                let deb_sc = (if inc > 0 { 420 } else { 180 }) + t_bonus;
                if deb_sc > best_deb_score {
                    best_deb_score = deb_sc;
                    best_debuff_enemy_slot = Some(idx);
                }
            }
            enemy_count += 1;
        }
    }

    let defense = player_block + player_end_turn_block + frost_passive_block + osty_hp + wuppy_hp;
    let unblocked_incoming = (total_incoming - defense).max(0);
    unsafe {
        use core::ptr::addr_of_mut;
        addr_of_mut!((*ptr).player_hp).write(player_hp);
        addr_of_mut!((*ptr).missing_hp).write((player_max_hp - player_hp).max(0));
        addr_of_mut!((*ptr).player_energy).write(state.player.energy);
        addr_of_mut!((*ptr).player_str).write(player_str);
        addr_of_mut!((*ptr).player_dex).write(player_dex);
        addr_of_mut!((*ptr).player_weak).write(player_weak);
        addr_of_mut!((*ptr).player_frail).write(player_frail);
        addr_of_mut!((*ptr).player_has_fan_of_knives).write(player_has_fan_of_knives);
        addr_of_mut!((*ptr).player_has_seeking_edge).write(player_has_seeking_edge);
        addr_of_mut!((*ptr).has_active_scaling).write(has_active_scaling);
        addr_of_mut!((*ptr).any_enemy_vuln).write(any_enemy_vuln);
        addr_of_mut!((*ptr).unblocked_incoming).write(unblocked_incoming);
        addr_of_mut!((*ptr).alive_enemies).write(alive_enemies);
        addr_of_mut!((*ptr).reviving_enemies).write(reviving_enemies);
        addr_of_mut!((*ptr).enemy_count).write(enemy_count);
        addr_of_mut!((*ptr).best_non_attack_enemy_slot).write(best_non_attack_enemy_slot);
        addr_of_mut!((*ptr).best_debuff_enemy_slot).write(best_debuff_enemy_slot);
        out.assume_init_ref()
    }
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

#[inline(always)]
fn count_non_empty_potion_slots(state: &BattleState) -> usize {
    let mut n = 0usize;
    for s in &state.player.potion_slots {
        n += usize::from(s.is_some());
    }
    n
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum FastEnergyHook {
    None,
    BorrowedTime(i32),
    Veilpiercer,
    Corruption,
    FreeAttack,
    FreeSkill,
    FreePower,
    Fallback,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum FastShouldPlayHook {
    None,
    ChainsOfBinding { bound_played: bool },
    Fallback,
}

#[inline]
fn resolve_sole_player_power_in_slot<'a>(
    state: &'a BattleState,
    slot: HookSlot,
) -> Option<&'a sts2_kernel::state::PowerInst> {
    let words = state.hook_cache.flat_slot_words as usize;
    if words == 0 {
        return None;
    }
    let mut single_listener: Option<CachedHookListener> = None;
    for lw in 0..words {
        let bits = state.hook_cache.slot_listener_mask_word(slot as usize, lw);
        if bits == 0 {
            continue;
        }
        if (bits & (bits - 1)) != 0 || single_listener.is_some() {
            return None;
        }
        let idx = (lw << 6) + bits.trailing_zeros() as usize;
        single_listener = Some(state.hook_cache.flat_listeners[idx]);
    }
    if let Some(CachedHookListener::Power {
        creature,
        creature_idx,
        power_idx,
        ..
    }) = single_listener
    {
        if creature == state.player.creature_handle
            && creature_idx != u16::MAX
            && power_idx != u16::MAX
        {
            return state
                .creatures
                .get(creature_idx as usize)
                .and_then(|c| c.powers.get(power_idx as usize));
        }
    }
    None
}

#[inline(always)]
fn resolve_fast_query_and_hooks(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> (bool, sts2_types::HookMask, FastEnergyHook, FastShouldPlayHook) {
    let cache_ready = state.hook_cache.masks_are_valid();
    let comb = state.hook_cache.masks.combat_combined;
    let kw_ok = !comb.contains(HookSlot::TryModifyKeywordsInCombat)
        || state
            .hook_cache
            .cached_tables
            .is_some_and(|t| t.keywords_in_combat_known_only);
    if !cache_ready || !kw_ok || comb.contains(HookSlot::TryModifyStarCostInCombat) {
        return (
            false,
            comb,
            FastEnergyHook::Fallback,
            FastShouldPlayHook::Fallback,
        );
    }
    let has_should_play = comb.contains(HookSlot::ShouldPlay);
    let has_early = comb.contains(HookSlot::TryModifyEnergyCostInCombat);
    let has_late = comb.contains(HookSlot::TryModifyEnergyCostInCombatLate);
    if !has_should_play && !has_early && !has_late {
        return (
            true,
            comb,
            FastEnergyHook::None,
            FastShouldPlayHook::None,
        );
    }
    if state.hook_cache.dirty_mask != 0 {
        let _ = state.ensure_hook_cache(content);
    }
    let comb = state.hook_cache.masks.combat_combined;
    let fast_should_play = if !comb.contains(HookSlot::ShouldPlay) {
        FastShouldPlayHook::None
    } else if let Some(pw) = resolve_sole_player_power_in_slot(state, HookSlot::ShouldPlay) {
        if pw.id == PowerId::ChainsOfBindingPower {
            let bound_played = pw
                .ext_index
                .and_then(|e| state.power_extensions.get(e as usize))
                .and_then(|ext| ext.private_fields.get_bool_static(hot_keys::CHAINS_OF_BINDING_BOUND_PLAYED))
                .unwrap_or(false);
            FastShouldPlayHook::ChainsOfBinding { bound_played }
        } else {
            return (
                false,
                comb,
                FastEnergyHook::Fallback,
                FastShouldPlayHook::Fallback,
            );
        }
    } else {
        return (
            false,
            comb,
            FastEnergyHook::Fallback,
            FastShouldPlayHook::Fallback,
        );
    };

    let has_early = comb.contains(HookSlot::TryModifyEnergyCostInCombat);
    let has_late = comb.contains(HookSlot::TryModifyEnergyCostInCombatLate);
    let fast_energy_hook = if !has_early && !has_late {
        FastEnergyHook::None
    } else if has_early && !has_late {
        if let Some(pw) =
            resolve_sole_player_power_in_slot(state, HookSlot::TryModifyEnergyCostInCombat)
        {
            if pw.id == PowerId::BorrowedTimePower {
                FastEnergyHook::BorrowedTime(pw.amount)
            } else {
                FastEnergyHook::Fallback
            }
        } else {
            FastEnergyHook::Fallback
        }
    } else if !has_early && has_late {
        if let Some(pw) =
            resolve_sole_player_power_in_slot(state, HookSlot::TryModifyEnergyCostInCombatLate)
        {
            match pw.id {
                PowerId::VeilpiercerPower => FastEnergyHook::Veilpiercer,
                PowerId::CorruptionPower => FastEnergyHook::Corruption,
                PowerId::FreeAttackPower => FastEnergyHook::FreeAttack,
                PowerId::FreeSkillPower => FastEnergyHook::FreeSkill,
                PowerId::FreePowerPower => FastEnergyHook::FreePower,
                _ => FastEnergyHook::Fallback,
            }
        } else {
            FastEnergyHook::Fallback
        }
    } else {
        FastEnergyHook::Fallback
    };
    (true, comb, fast_energy_hook, fast_should_play)
}

#[inline(never)]
fn resolve_fast_energy_hook_cost(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    comb: sts2_types::HookMask,
    ch: CardHandle,
    local_e: i32,
    card_type: CardType,
    kw_base: CardKeywords,
    has_hex_power: bool,
    aff_id: Option<AfflictionId>,
    fast_energy_hook: FastEnergyHook,
) -> i32 {
    let cost = match fast_energy_hook {
        FastEnergyHook::None => local_e,
        FastEnergyHook::BorrowedTime(delta) => (local_e + delta).max(0),
        FastEnergyHook::Veilpiercer => {
            let is_ethereal = kw_base.contains(CardKeywords::ETHEREAL)
                || (has_hex_power && aff_id == Some(AfflictionId::Hexed));
            if is_ethereal { 0 } else { local_e }
        }
        FastEnergyHook::Corruption | FastEnergyHook::FreeSkill => {
            if card_type == CardType::Skill { 0 } else { local_e }
        }
        FastEnergyHook::FreeAttack => {
            if card_type == CardType::Attack { 0 } else { local_e }
        }
        FastEnergyHook::FreePower => {
            if card_type == CardType::Power { 0 } else { local_e }
        }
        FastEnergyHook::Fallback => unreachable!(),
    };
    #[cfg(debug_assertions)]
    if fast_energy_hook != FastEnergyHook::None {
        let expected = match sts2_kernel::hooks::modify_energy_cost_in_combat_cached_ready(
            state,
            content,
            ch,
            Dec::from_i32(local_e),
            comb,
        ) {
            Ok((m, true)) => m.max(Dec::ZERO).to_i32_trunc().unwrap_or(cost),
            _ => local_e,
        };
        debug_assert_eq!(cost, expected);
    }
    #[cfg(not(debug_assertions))]
    let _ = (state, content, comb, ch);
    cost
}

#[cold]
#[inline(never)]
fn has_living_other_ally(state: &BattleState) -> bool {
    state.ally_handles.iter().any(|&h| {
        h != state.player.creature_handle && state.creature(h).is_some_and(|c| c.is_alive())
    })
}

#[cold]
#[inline(never)]
fn spill_seen_fast_to_keys(
    seen_fast: &[u64; 16],
    seen_fast_len: usize,
    seen_keys: &mut Vec<RolloutDedupKey>,
) {
    seen_keys.clear();
    for &existing in &seen_fast[..seen_fast_len] {
        seen_keys.push(RolloutDedupKey::from_fast_u64(existing));
    }
}

struct ExtractedFastPlayableCard {
    handle: CardHandle,
    card_id: CardId,
    energy_cost: i32,
    star_cost: i32,
    upg: bool,
    exhausts: bool,
    is_retained: bool,
    prof: &'static CardRolloutProfile,
}

#[inline(always)]
#[allow(clippy::too_many_arguments)]
fn try_extract_fast_playable_hand_card(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    comb: sts2_types::HookMask,
    fast_energy_hook: FastEnergyHook,
    tables: &'static sts2_kernel::content_dispatch::HookMaskTableView,
    full_profiles: bool,
    cards_contiguous: bool,
    player_energy: i32,
    player_stars: i32,
    has_hex_power: bool,
    bound_blocked: bool,
    ch: CardHandle,
    seen_fast: &mut [u64; 16],
    seen_fast_len: &mut usize,
    use_seen_keys: &mut bool,
    seen_keys: &mut Vec<RolloutDedupKey>,
) -> Option<ExtractedFastPlayableCard> {
    let card_idx = if cards_contiguous {
        let idx = ch.0.wrapping_sub(1) as usize;
        if idx < state.cards.len() {
            #[cfg(debug_assertions)]
            debug_assert_eq!(state.cards[idx].handle, ch);
            idx
        } else {
            return None;
        }
    } else {
        state.card_index(ch)?
    };
    let (
        card_id,
        upgrade_level,
        is_x_e,
        local_e,
        local_s,
        has_ext,
        aff_id,
        kw_base,
        temp_flags,
        target_type,
        has_custom_play,
        prof,
    ) = {
        let card = unsafe { state.cards.get_unchecked(card_idx) };
        if card.pile != PileType::Hand {
            return None;
        }
        let kw_base = if card.keywords_added_from_outside.is_empty()
            && card.keywords_removed_from_outside.is_empty()
        {
            card.local_keywords
        } else {
            card.effective_keywords_base()
        };
        if kw_base.contains(CardKeywords::UNPLAYABLE) {
            return None;
        }
        let card_id = card.id;
        let prof: &'static CardRolloutProfile = if full_profiles {
            unsafe { tables.card_rollout_profiles.get_unchecked(card_id as usize) }
        } else {
            tables.card_rollout_profiles.get(card_id as usize)?
        };
        let mflags = prof.meta_flags;
        let is_x_s = (mflags & mf::IS_X_STAR_COST) != 0;
        let canonical_s = prof.canonical_star_cost as i32;
        let local_s = if is_x_s {
            player_stars
        } else if card.temp_star_cost_override_this_turn.is_none() && card.temp_flags.is_empty() {
            canonical_s
        } else {
            resolve_local_star_cost_card(card, canonical_s)
        };
        if player_stars < local_s {
            return None;
        }
        let is_x_e = (mflags & mf::IS_X_ENERGY_COST) != 0;
        let local_e =
            if card.ext_index.is_none() && !is_x_e && card.canonical_base_energy_cost >= 0 {
                (card.base_energy_cost as i32).max(0)
            } else {
                resolve_local_energy_cost_card(state, card, is_x_e)
            };
        (
            card_id,
            card.upgrade_level,
            is_x_e,
            local_e,
            local_s,
            card.ext_index.is_some(),
            card.affliction.map(|a| a.id),
            kw_base,
            card.temp_flags,
            prof.target_type,
            (mflags & mf::HAS_CUSTOM_PLAYABILITY) != 0,
            prof,
        )
    };
    let card_type = prof.card_type;
    let energy_cost = if is_x_e || fast_energy_hook == FastEnergyHook::None {
        local_e
    } else {
        resolve_fast_energy_hook_cost(
            state,
            content,
            comb,
            ch,
            local_e,
            card_type,
            kw_base,
            has_hex_power,
            aff_id,
            fast_energy_hook,
        )
    };
    if player_energy < energy_cost {
        return None;
    }
    if bound_blocked && aff_id == Some(AfflictionId::Bound) {
        #[cfg(debug_assertions)]
        debug_assert!(
            !sts2_kernel::hooks::should_play_unchecked_active(state, content, ch).unwrap_or(true)
        );
        return None;
    }
    let fast_k =
        RolloutDedupKey::try_fast_u64(card_id, upgrade_level, energy_cost, local_s, has_ext, aff_id);
    #[cfg(debug_assertions)]
    if let Some(fk) = fast_k {
        debug_assert_eq!(
            RolloutDedupKey::from_fast_u64(fk),
            RolloutDedupKey::new(card_id, upgrade_level, energy_cost, local_s, has_ext, aff_id)
        );
    }
    if !*use_seen_keys {
        if let Some(fk) = fast_k {
            for idx in 0..*seen_fast_len {
                if unsafe { *seen_fast.get_unchecked(idx) } == fk {
                    return None;
                }
            }
        }
    } else {
        let key =
            RolloutDedupKey::new(card_id, upgrade_level, energy_cost, local_s, has_ext, aff_id);
        if seen_keys.contains(&key) {
            return None;
        }
    }
    if target_type == TargetType::AnyAlly && !has_living_other_ally(state) {
        return None;
    }
    if has_custom_play && !content.card_is_playable(state, ch).unwrap_or(false) {
        return None;
    }
    let exhausts = kw_base.contains(CardKeywords::EXHAUST)
        || temp_flags.contains(sts2_kernel::state::CardTempFlags::EXHAUST_ON_NEXT_PLAY);
    let is_retained = kw_base.contains(CardKeywords::RETAIN)
        || temp_flags.contains(sts2_kernel::state::CardTempFlags::TEMPORARY_RETAIN_TURN);
    let upg = upgrade_level > 0;
    if !*use_seen_keys {
        if let Some(fk) = fast_k {
            if *seen_fast_len < 16 {
                unsafe {
                    *seen_fast.get_unchecked_mut(*seen_fast_len) = fk;
                }
                *seen_fast_len += 1;
                return Some(ExtractedFastPlayableCard {
                    handle: ch,
                    card_id,
                    energy_cost,
                    star_cost: local_s,
                    upg,
                    exhausts,
                    is_retained,
                    prof,
                });
            }
        }
        spill_seen_fast_to_keys(seen_fast, *seen_fast_len, seen_keys);
        *seen_fast_len = 0;
        *use_seen_keys = true;
    }
    let key = RolloutDedupKey::new(card_id, upgrade_level, energy_cost, local_s, has_ext, aff_id);
    if seen_keys.contains(&key) {
        return None;
    }
    seen_keys.push(key);
    Some(ExtractedFastPlayableCard {
        handle: ch,
        card_id,
        energy_cost,
        star_cost: local_s,
        upg,
        exhausts,
        is_retained,
        prof,
    })
}

#[inline(always)]
fn resolve_fast_scan_meta(
    state: &BattleState,
    comb: sts2_types::HookMask,
    fast_energy_hook: FastEnergyHook,
    fast_should_play: FastShouldPlayHook,
) -> (
    &'static sts2_kernel::content_dispatch::HookMaskTableView,
    bool,
    bool,
    bool,
    bool,
) {
    let tables = state
        .hook_cache
        .cached_tables
        .expect("cache_ready implies cached_tables");
    let full_profiles = CardId::COUNT <= tables.card_rollout_profiles.len();
    let cards_contiguous = state.handle_layout.cards;
    let has_hex_power = fast_energy_hook == FastEnergyHook::Veilpiercer
        && comb.contains(HookSlot::TryModifyKeywordsInCombat)
        && state.player.is_active_for_hooks
        && state.player_creature().is_some_and(|c| {
            !c.is_detached
                && state.contains_creature(c.handle)
                && c.powers.iter().any(|p| p.id == PowerId::HexPower)
        });
    let bound_blocked = matches!(
        fast_should_play,
        FastShouldPlayHook::ChainsOfBinding { bound_played: true }
    );
    (
        tables,
        full_profiles,
        cards_contiguous,
        has_hex_power,
        bound_blocked,
    )
}

fn collect_fast_playable_hand_cards(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    comb: sts2_types::HookMask,
    fast_energy_hook: FastEnergyHook,
    fast_should_play: FastShouldPlayHook,
    hand_buf: &mut Vec<CardHandle>,
    seen_keys: &mut Vec<RolloutDedupKey>,
    playable_buf: &mut Vec<PlayableRolloutCard>,
) {
    let (tables, full_profiles, cards_contiguous, has_hex_power, bound_blocked) =
        resolve_fast_scan_meta(state, comb, fast_energy_hook, fast_should_play);
    hand_buf.clear();
    hand_buf.extend_from_slice(&state.player.hand);
    let player_energy = state.player.energy;
    let player_stars = state.player.stars;
    let mut seen_fast = [0u64; 16];
    let mut seen_fast_len = 0usize;
    let mut use_seen_keys = false;
    for i in 0..hand_buf.len() {
        let ch = hand_buf[i];
        #[cfg(debug_assertions)]
        if fast_should_play != FastShouldPlayHook::None {
            let _ = sts2_kernel::hooks::should_play_unchecked_active(state, content, ch);
        }
        if let Some(c) = try_extract_fast_playable_hand_card(
            state,
            content,
            comb,
            fast_energy_hook,
            tables,
            full_profiles,
            cards_contiguous,
            player_energy,
            player_stars,
            has_hex_power,
            bound_blocked,
            ch,
            &mut seen_fast,
            &mut seen_fast_len,
            &mut use_seen_keys,
            seen_keys,
        ) {
            playable_buf.push(PlayableRolloutCard {
                handle: c.handle,
                card_id: c.card_id,
                energy_cost: c.energy_cost,
                star_cost: c.star_cost,
                upg: c.upg,
                exhausts: c.exhausts,
                is_retained: c.is_retained,
            });
        }
    }
}

fn collect_rollout_playable_cards(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    hand_buf: &mut Vec<CardHandle>,
    seen_keys: &mut Vec<RolloutDedupKey>,
    playable_buf: &mut Vec<PlayableRolloutCard>,
) {
    playable_buf.clear();
    if state.player.hand.is_empty() || !state.is_in_progress {
        return;
    }
    if !state.hook_cache.masks_are_valid() {
        let _ = state.ensure_hook_cache(content);
    }
    if sts2_kernel::hooks::is_over_or_ending_checked(state, content).unwrap_or(true) {
        return;
    }

    let (fast_query_safe, comb, fast_energy_hook, fast_should_play) =
        resolve_fast_query_and_hooks(state, content);

    if fast_query_safe {
        let tables = state
            .hook_cache
            .cached_tables
            .expect("cache_ready implies cached_tables");
        let full_profiles = CardId::COUNT <= tables.card_rollout_profiles.len();

        if fast_energy_hook != FastEnergyHook::Fallback {
            collect_fast_playable_hand_cards(
                state,
                content,
                comb,
                fast_energy_hook,
                fast_should_play,
                hand_buf,
                seen_keys,
                playable_buf,
            );
            return;
        }
        seen_keys.clear();

        if state.hook_cache.dirty_mask != 0 {
            let _ = state.ensure_hook_cache(content);
        }
        let bound_blocked = matches!(
            fast_should_play,
            FastShouldPlayHook::ChainsOfBinding { bound_played: true }
        );
        let hand_len = state.player.hand.len();
        let mut seen_fast = [0u64; 16];
        let mut seen_fast_len = 0usize;
        for i in 0..hand_len {
            let ch = state.player.hand[i];
            let Some(card_idx) = state.card_index(ch) else {
                continue;
            };
            let (
                card_id,
                upgrade_level,
                is_x_e,
                local_e,
                local_s,
                has_ext,
                aff_id,
                target_type,
                has_custom_play,
                exhausts,
                is_retained,
            ) = {
                let card = &state.cards[card_idx];
                if card.pile != PileType::Hand {
                    continue;
                }
                let kw_base = if card.keywords_added_from_outside.is_empty()
                    && card.keywords_removed_from_outside.is_empty()
                {
                    card.local_keywords
                } else {
                    card.effective_keywords_base()
                };
                if kw_base.contains(CardKeywords::UNPLAYABLE) {
                    continue;
                }
                let card_id = card.id;
                let prof = if full_profiles {
                    unsafe { tables.card_rollout_profiles.get_unchecked(card_id as usize) }
                } else if let Some(p) = tables.card_rollout_profiles.get(card_id as usize) {
                    p
                } else {
                    continue;
                };
                let mflags = prof.meta_flags;
                let is_x_e = (mflags & mf::IS_X_ENERGY_COST) != 0;
                let local_e =
                    if card.ext_index.is_none() && !is_x_e && card.canonical_base_energy_cost >= 0 {
                        (card.base_energy_cost as i32).max(0)
                    } else {
                        resolve_local_energy_cost_card(state, card, is_x_e)
                    };
                let is_x_s = (mflags & mf::IS_X_STAR_COST) != 0;
                let canonical_s = prof.canonical_star_cost as i32;
                let local_s = if is_x_s {
                    state.player.stars
                } else if card.temp_star_cost_override_this_turn.is_none()
                    && card.temp_flags.is_empty()
                {
                    canonical_s
                } else {
                    resolve_local_star_cost_card(card, canonical_s)
                };
                let exhausts = kw_base.contains(CardKeywords::EXHAUST)
                    || card
                        .temp_flags
                        .contains(sts2_kernel::state::CardTempFlags::EXHAUST_ON_NEXT_PLAY);
                let is_retained = kw_base.contains(CardKeywords::RETAIN)
                    || card
                        .temp_flags
                        .contains(sts2_kernel::state::CardTempFlags::TEMPORARY_RETAIN_TURN);
                (
                    card_id,
                    card.upgrade_level,
                    is_x_e,
                    local_e,
                    local_s,
                    card.ext_index.is_some(),
                    card.affliction.map(|a| a.id),
                    prof.target_type,
                    (mflags & mf::HAS_CUSTOM_PLAYABILITY) != 0,
                    exhausts,
                    is_retained,
                )
            };
            if state.player.stars < local_s {
                continue;
            }
            let energy_cost = if is_x_e {
                local_e
            } else {
                match sts2_kernel::hooks::modify_energy_cost_in_combat_cached_ready(
                    state,
                    content,
                    ch,
                    Dec::from_i32(local_e),
                    comb,
                ) {
                    Ok((modified, true)) => {
                        let Ok(v) = modified.max(Dec::ZERO).to_i32_trunc() else {
                            continue;
                        };
                        v
                    }
                    Ok((_, false)) => local_e,
                    Err(_) => continue,
                }
            };
            if state.player.energy < energy_cost {
                continue;
            }
            if bound_blocked && aff_id == Some(AfflictionId::Bound) {
                continue;
            }
            let fast_k = RolloutDedupKey::try_fast_u64(
                card_id,
                upgrade_level,
                energy_cost,
                local_s,
                has_ext,
                aff_id,
            );
            if !seen_keys.is_empty() {
                let key = RolloutDedupKey::new(
                    card_id,
                    upgrade_level,
                    energy_cost,
                    local_s,
                    has_ext,
                    aff_id,
                );
                if seen_keys.contains(&key) {
                    continue;
                }
            } else if let Some(fk) = fast_k {
                if seen_fast[..seen_fast_len].contains(&fk) {
                    continue;
                }
            }
            if target_type == TargetType::AnyAlly {
                let has_ally = state.ally_handles.iter().any(|&h| {
                    h != state.player.creature_handle
                        && state.creature(h).is_some_and(|c| c.is_alive())
                });
                if !has_ally {
                    continue;
                }
            }
            if !has_custom_play
                || content.card_is_playable(state, ch).unwrap_or(false)
            {
                let entry = PlayableRolloutCard {
                    handle: ch,
                    card_id,
                    energy_cost,
                    star_cost: local_s,
                    upg: upgrade_level > 0,
                    exhausts,
                    is_retained,
                };
                if seen_keys.is_empty() {
                    if let Some(fk) = fast_k {
                        if seen_fast_len < 16 {
                            seen_fast[seen_fast_len] = fk;
                            seen_fast_len += 1;
                            playable_buf.push(entry);
                            continue;
                        }
                    }
                    for &existing in &seen_fast[..seen_fast_len] {
                        seen_keys.push(RolloutDedupKey::from_fast_u64(existing));
                    }
                    seen_fast_len = 0;
                }
                let key = RolloutDedupKey::new(
                    card_id,
                    upgrade_level,
                    energy_cost,
                    local_s,
                    has_ext,
                    aff_id,
                );
                if !seen_keys.contains(&key) {
                    seen_keys.push(key);
                    playable_buf.push(entry);
                }
            }
        }
        return;
    }

    seen_keys.clear();
    let mut seen_fast = [0u64; 16];
    let mut seen_fast_len = 0usize;
    hand_buf.clear();
    hand_buf.extend_from_slice(&state.player.hand);
    for i in 0..hand_buf.len() {
        let ch = hand_buf[i];
        let Some(card_idx) = state.card_index(ch) else {
            continue;
        };
        let card = state.cards[card_idx];
        if card.pile != PileType::Hand {
            continue;
        }
        let kw = card.effective_keywords_base();
        if kw.contains(CardKeywords::UNPLAYABLE) {
            continue;
        }
        let Ok(Some((energy_cost, star_cost))) =
            CombatEngine::can_play_card_with_costs_in_progress_at(state, content, ch, card_idx)
        else {
            continue;
        };
        let exhausts = kw.contains(CardKeywords::EXHAUST)
            || card
                .temp_flags
                .contains(sts2_kernel::state::CardTempFlags::EXHAUST_ON_NEXT_PLAY);
        let is_retained = kw.contains(CardKeywords::RETAIN)
            || card
                .temp_flags
                .contains(sts2_kernel::state::CardTempFlags::TEMPORARY_RETAIN_TURN);
        let entry = PlayableRolloutCard {
            handle: ch,
            card_id: card.id,
            energy_cost,
            star_cost,
            upg: card.upgrade_level > 0,
            exhausts,
            is_retained,
        };
        let has_ext = card.ext_index.is_some();
        let aff_id = card.affliction.map(|a| a.id);
        let fast_k = RolloutDedupKey::try_fast_u64(
            card.id,
            card.upgrade_level,
            energy_cost,
            star_cost,
            has_ext,
            aff_id,
        );
        if seen_keys.is_empty() {
            if let Some(fk) = fast_k {
                let mut dup = false;
                for &existing in &seen_fast[..seen_fast_len] {
                    if existing == fk {
                        dup = true;
                        break;
                    }
                }
                if dup {
                    continue;
                }
                if seen_fast_len < 16 {
                    seen_fast[seen_fast_len] = fk;
                    seen_fast_len += 1;
                    playable_buf.push(entry);
                    continue;
                }
            }
            for &existing in &seen_fast[..seen_fast_len] {
                seen_keys.push(RolloutDedupKey::from_fast_u64(existing));
            }
            seen_fast_len = 0;
        }
        let key = RolloutDedupKey::new(
            card.id,
            card.upgrade_level,
            energy_cost,
            star_cost,
            has_ext,
            aff_id,
        );
        if seen_keys.contains(&key) {
            continue;
        }
        seen_keys.push(key);
        playable_buf.push(entry);
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
        && sts2_kernel::ordering::resolve_local_energy_cost_card(state, card, false) == 0
    {
        quality += 30;
    }
    quality
}

#[inline(always)]
fn fast_generic_choice_card_quality(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    cards: &[sts2_kernel::state::CardInst],
    cards_contiguous: bool,
    qtable: Option<&[GenericChoiceQualityEntry]>,
    root_choice_q: &[i16],
    ch: CardHandle,
) -> i32 {
    if cards_contiguous && !root_choice_q.is_empty() {
        let idx = ch.0.wrapping_sub(1) as usize;
        if idx < root_choice_q.len() && !state.is_card_meta_dirty(idx) {
            let q = unsafe { *root_choice_q.get_unchecked(idx) };
            if q != i16::MIN {
                #[cfg(debug_assertions)]
                debug_assert_eq!(
                    q as i32,
                    compute_generic_choice_card_quality(state, content, ch)
                );
                return q as i32;
            }
        }
    }
    if let Some(qt) = qtable {
        let idx = ch.0.wrapping_sub(1) as usize;
        let card = if cards_contiguous {
            if idx < cards.len() {
                let c = unsafe { cards.get_unchecked(idx) };
                #[cfg(debug_assertions)]
                debug_assert_eq!(c.handle, ch);
                Some(c)
            } else {
                None
            }
        } else if idx < cards.len() && cards[idx].handle == ch {
            Some(&cards[idx])
        } else {
            state.card(ch)
        };
        let Some(card) = card else {
            return 0;
        };
        if card.has_local_keyword(CardKeywords::UNPLAYABLE) {
            return -450;
        }
        let cid_idx = card.id as usize;
        if cid_idx < qt.len() {
            let e = unsafe { qt.get_unchecked(cid_idx) };
            if (e.flags & 1) != 0 {
                return -400;
            }
            let quality = if card.ext_index.is_none()
                && card.canonical_base_energy_cost >= 0
                && card.base_energy_cost >= 0
            {
                let base_q = match card.base_energy_cost {
                    0 => e.q_cost0,
                    1 => e.q_cost1,
                    _ => e.q_cost2p,
                } as i32;
                base_q + if card.upgrade_level > 0 {
                    e.upg_bonus as i32
                } else {
                    0
                }
            } else {
                let mut q = e.q_cost2p as i32;
                if (e.flags & 2) == 0 {
                    let base_cost = card.base_energy_cost;
                    if base_cost == 0 {
                        q += 40;
                    } else if base_cost == 1 {
                        q += 15;
                    }
                }
                if card.upgrade_level > 0 {
                    q += 35;
                }
                if (e.flags & 4) == 0 {
                    let cost_is_zero =
                        if card.ext_index.is_none() && card.canonical_base_energy_cost >= 0 {
                            card.base_energy_cost <= 0
                        } else {
                            resolve_local_energy_cost_card(state, card, false) == 0
                        };
                    if cost_is_zero {
                        q += 30;
                    }
                }
                q
            };
            #[cfg(debug_assertions)]
            debug_assert_eq!(
                quality,
                compute_generic_choice_card_quality(state, content, ch)
            );
            return quality;
        }
    }
    compute_generic_choice_card_quality(state, content, ch)
}

#[inline(always)]
const fn choice_min_key_lowest(q: i32, h: u32) -> u64 {
    (((q as i64 - i32::MIN as i64) as u64) << 32) | (h as u64)
}

#[inline(always)]
const fn choice_min_key_highest(q: i32, h: u32) -> u64 {
    (((i32::MAX as i64 - q as i64) as u64) << 32) | (h as u64)
}

fn pick_rollout_choice(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    root_choice_q: &[i16],
    req: &sts2_kernel::continuation::ChoiceRequest,
    scored: &mut Vec<(usize, i32, u32)>,
    sel_out: &mut SmallVec<[usize; 16]>,
) -> bool {
    sel_out.clear();
    if req.candidates.is_empty() {
        return req.min_count == 0;
    }

    let qtable = state
        .hook_cache
        .cached_tables
        .map(|t| t.card_generic_choice_quality);
    let cards = &state.cards;
    let cards_contiguous = state.handle_layout.cards;
    let prefer_lowest = req.source_pile == PileType::Hand;
    if req.min_count <= 1 {
        let ch0 = req.candidates[0];
        let q0 = fast_generic_choice_card_quality(
            state,
            content,
            cards,
            cards_contiguous,
            qtable,
            root_choice_q,
            ch0,
        );
        let do_prefetch = !cards_contiguous || root_choice_q.is_empty();
        let (mut best_idx, mut best_q) = (0usize, q0);
        if prefer_lowest {
            let mut best_key = choice_min_key_lowest(q0, ch0.0);
            for idx in 1..req.candidates.len() {
                #[cfg(target_arch = "x86_64")]
                if do_prefetch && idx + 4 < req.candidates.len() {
                    let ahead = unsafe { *req.candidates.get_unchecked(idx + 4) }
                        .0
                        .wrapping_sub(1) as usize;
                    if ahead < cards.len() {
                        unsafe {
                            core::arch::x86_64::_mm_prefetch::<{ core::arch::x86_64::_MM_HINT_T0 }>(
                                cards.as_ptr().add(ahead).cast::<i8>(),
                            );
                        }
                    }
                }
                let ch = unsafe { *req.candidates.get_unchecked(idx) };
                let q = fast_generic_choice_card_quality(
                    state,
                    content,
                    cards,
                    cards_contiguous,
                    qtable,
                    root_choice_q,
                    ch,
                );
                let key = choice_min_key_lowest(q, ch.0);
                if key < best_key {
                    best_key = key;
                    best_idx = idx;
                    best_q = q;
                }
            }
        } else {
            let mut best_key = choice_min_key_highest(q0, ch0.0);
            for idx in 1..req.candidates.len() {
                #[cfg(target_arch = "x86_64")]
                if do_prefetch && idx + 4 < req.candidates.len() {
                    let ahead = unsafe { *req.candidates.get_unchecked(idx + 4) }
                        .0
                        .wrapping_sub(1) as usize;
                    if ahead < cards.len() {
                        unsafe {
                            core::arch::x86_64::_mm_prefetch::<{ core::arch::x86_64::_MM_HINT_T0 }>(
                                cards.as_ptr().add(ahead).cast::<i8>(),
                            );
                        }
                    }
                }
                let ch = unsafe { *req.candidates.get_unchecked(idx) };
                let q = fast_generic_choice_card_quality(
                    state,
                    content,
                    cards,
                    cards_contiguous,
                    qtable,
                    root_choice_q,
                    ch,
                );
                let key = choice_min_key_highest(q, ch.0);
                if key < best_key {
                    best_key = key;
                    best_idx = idx;
                    best_q = q;
                }
            }
        }
        #[cfg(debug_assertions)]
        {
            let mut scored_dbg: SmallVec<[(usize, i32, u32); 32]> = req
                .candidates
                .iter()
                .enumerate()
                .map(|(idx, &ch)| (idx, compute_generic_choice_card_quality(state, content, ch), ch.0))
                .collect();
            scored_dbg.sort_unstable_by(|a, b| {
                let q_cmp = if prefer_lowest { a.1.cmp(&b.1) } else { b.1.cmp(&a.1) };
                q_cmp.then_with(|| a.2.cmp(&b.2)).then_with(|| a.0.cmp(&b.0))
            });
            debug_assert_eq!(best_idx, scored_dbg[0].0);
            debug_assert_eq!(best_q, scored_dbg[0].1);
        }
        if req.min_count == 0 {
            let pick_one = if prefer_lowest { best_q < 0 } else { best_q >= 0 };
            if pick_one {
                sel_out.push(best_idx);
            }
        } else {
            sel_out.push(best_idx);
        }
        return true;
    }

    let do_prefetch = !cards_contiguous || root_choice_q.is_empty();
    scored.clear();
    scored.reserve(req.candidates.len().saturating_sub(scored.capacity()));
    for (idx, &ch) in req.candidates.iter().enumerate() {
        #[cfg(target_arch = "x86_64")]
        if do_prefetch && idx + 4 < req.candidates.len() {
            let ahead = unsafe { *req.candidates.get_unchecked(idx + 4) }
                .0
                .wrapping_sub(1) as usize;
            if ahead < cards.len() {
                unsafe {
                    core::arch::x86_64::_mm_prefetch::<{ core::arch::x86_64::_MM_HINT_T0 }>(
                        cards.as_ptr().add(ahead).cast::<i8>(),
                    );
                }
            }
        }
        scored.push((
            idx,
            fast_generic_choice_card_quality(
                state,
                content,
                cards,
                cards_contiguous,
                qtable,
                root_choice_q,
                ch,
            ),
            ch.0,
        ));
    }
    let cmp = |a: &(usize, i32, u32), b: &(usize, i32, u32)| {
        let q_cmp = if prefer_lowest {
            a.1.cmp(&b.1)
        } else {
            b.1.cmp(&a.1)
        };
        q_cmp.then_with(|| a.2.cmp(&b.2)).then_with(|| a.0.cmp(&b.0))
    };
    let take_n = req.min_count.min(scored.len());
    if take_n < scored.len() {
        scored.select_nth_unstable_by(take_n - 1, cmp);
        scored[..take_n].sort_unstable_by(cmp);
    } else {
        scored.sort_unstable_by(cmp);
    }
    sel_out.extend(scored[..take_n].iter().map(|x| x.0));
    true
}

fn resolve_step_choices(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    root_choice_q: &[i16],
    mut outcome: StepOutcome,
    choice_slab: &mut RolloutChoiceSlab,
    scored_buf: &mut Vec<(usize, i32, u32)>,
    sel_buf: &mut SmallVec<[usize; 16]>,
) -> Option<(u32, u16)> {
    let meta_start = choice_slab.steps_meta.len();
    let idx_start = choice_slab.indices.len();
    let mut depth = 0usize;
    while let StepOutcome::NeedsChoice(ref req) = outcome {
        if depth >= 4
            || !pick_rollout_choice(state, content, root_choice_q, req, scored_buf, sel_buf)
        {
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
    playable: &[PlayableRolloutCard],
    blocked: &[CardHandle],
    ctx: &FastTurnContext,
    pilot: PilotKind,
) -> Option<(CardHandle, Option<CreatureHandle>)> {
    match pilot {
        PilotKind::DefensiveTempo => {
            select_best_pilot_move_impl::<false>(state, content, playable, blocked, ctx)
        }
        PilotKind::SetupFirst => {
            select_best_pilot_move_impl::<true>(state, content, playable, blocked, ctx)
        }
    }
}

#[inline(always)]
#[allow(clippy::too_many_arguments)]
fn pilot_finish_score<const IS_SETUP: bool, const IS_ANY_ENEMY: bool>(
    ctx: &FastTurnContext,
    pre: &MoveScorePrecomp,
    energy_cost: i32,
    has_self_hp_loss: bool,
    common_prio: i32,
    has_enemy_debuff: bool,
    eff_target: TargetType,
    retained_penalty_applies: bool,
    has_attack_op: bool,
    est_total_dmg: i32,
    kills_count: i32,
    killed_incoming: i32,
    target_slot: Option<usize>,
) -> i32 {
    if kills_count > 0 && kills_count >= ctx.alive_enemies && ctx.reviving_enemies == 0 {
        return 1_000_000 + est_total_dmg * 10 - energy_cost;
    }
    if has_self_hp_loss && pre.low_hp {
        return -500;
    }

    let mut prio = common_prio;
    if kills_count > 0 {
        prio += kills_count * 1_600;
        if killed_incoming > 0 {
            prio += killed_incoming.min(pre.unblocked_plus_10) * pre.kill_inc_mult;
        }
    }

    if !IS_SETUP {
        if pre.has_unblocked && has_enemy_debuff {
            let targets_attacker =
                IS_ANY_ENEMY && target_slot.is_some_and(|s| ctx.enemy_incoming(s) > 0);
            prio += if targets_attacker || (!IS_ANY_ENEMY && eff_target == TargetType::AllEnemies)
            {
                420
            } else {
                180
            };
        }
        if est_total_dmg > 0 {
            prio += est_total_dmg * 45;
        }
        if IS_ANY_ENEMY {
            if let Some(slot) = target_slot {
                prio += ctx.enemy_target_bonus(slot);
            }
        }
    } else {
        if est_total_dmg > 0 {
            prio += est_total_dmg * 32;
            if retained_penalty_applies && kills_count == 0 {
                prio -= 420;
            }
        }
        if IS_ANY_ENEMY {
            if let Some(slot) = target_slot {
                let hp = ctx.enemy_hp(slot);
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
    prio
}

#[derive(Clone, Copy)]
struct MoveScorePrecomp {
    unblocked: i32,
    unblocked_plus_10: i32,
    kill_inc_mult: i32,
    has_unblocked: bool,
    lethal_threat: bool,
    low_hp: bool,
    block_mult: i32,
    heal_bonus: i32,
    power_non_setup_base: i32,
    setup_retained_check: bool,
}

impl MoveScorePrecomp {
    #[inline(always)]
    fn new<const IS_SETUP: bool>(ctx: &FastTurnContext, has_non_retained: bool) -> Self {
        let unblocked = ctx.unblocked_incoming;
        let has_unblocked = unblocked > 0;
        let lethal_threat = unblocked >= ctx.player_hp;
        Self {
            unblocked,
            unblocked_plus_10: unblocked + 10,
            kill_inc_mult: if lethal_threat { 260 } else { 140 },
            has_unblocked,
            lethal_threat,
            low_hp: ctx.player_hp <= 3,
            block_mult: if lethal_threat { 260 } else { 135 },
            heal_bonus: if ctx.missing_hp > 0 {
                ctx.missing_hp.min(20) * 35
            } else {
                0
            },
            power_non_setup_base: if !has_unblocked {
                520
            } else if lethal_threat {
                100
            } else {
                240
            },
            setup_retained_check: IS_SETUP && (has_non_retained || ctx.has_active_scaling),
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct BestPilotMove {
    score: i32,
    card: CardHandle,
    target: u32,
}

impl BestPilotMove {
    const EMPTY: Self = Self {
        score: 0,
        card: CardHandle(0),
        target: u32::MAX,
    };

    #[inline(always)]
    fn to_option(self) -> Option<(CardHandle, Option<CreatureHandle>)> {
        if self.score > 0 {
            Some((
                self.card,
                if self.target == u32::MAX {
                    None
                } else {
                    Some(CreatureHandle(self.target))
                },
            ))
        } else {
            None
        }
    }
}

#[inline(always)]
#[allow(clippy::too_many_arguments)]
fn score_any_enemy_slot<const IS_SETUP: bool>(
    ctx: &FastTurnContext,
    pre: &MoveScorePrecomp,
    energy_cost: i32,
    has_self_hp_loss: bool,
    common_prio: i32,
    has_enemy_debuff: bool,
    eff_target: TargetType,
    retained_penalty_applies: bool,
    has_attack_op: bool,
    base_dmg: i32,
    single_dmg: i32,
    slot: usize,
    ch: CardHandle,
    best: &mut BestPilotMove,
) {
    let es = ctx.enemy_slot(slot);
    if es.hp <= 0 {
        return;
    }
    let (est_total_dmg, kills_count, killed_incoming) = if has_attack_op {
        if base_dmg > 0 {
            let mut kills = 0i32;
            let mut kill_inc = 0i32;
            if single_dmg >= es.eff_hp {
                kills = 1;
                kill_inc = es.incoming;
            }
            (single_dmg, kills, kill_inc)
        } else {
            (6, 0, 0)
        }
    } else {
        (0, 0, 0)
    };
    let sc = pilot_finish_score::<IS_SETUP, true>(
        ctx,
        pre,
        energy_cost,
        has_self_hp_loss,
        common_prio,
        has_enemy_debuff,
        eff_target,
        retained_penalty_applies,
        has_attack_op,
        est_total_dmg,
        kills_count,
        killed_incoming,
        Some(slot),
    );
    if sc > best.score {
        debug_assert_ne!(es.handle.0, u32::MAX);
        *best = BestPilotMove {
            score: sc,
            card: ch,
            target: es.handle.0,
        };
    }
}

#[cold]
#[inline(never)]
#[allow(clippy::too_many_arguments)]
fn score_status_or_curse_card(
    state: &BattleState,
    ctx: &FastTurnContext,
    pre: &MoveScorePrecomp,
    ch: CardHandle,
    eff_target: TargetType,
    energy_cost: i32,
    star_cost: i32,
    exhausts: bool,
    has_self_hp_loss: bool,
    best: &mut BestPilotMove,
) {
    let status_prio = if exhausts && !has_self_hp_loss && energy_cost == 0 && star_cost == 0 {
        80
    } else if exhausts
        && !has_self_hp_loss
        && !pre.has_unblocked
        && ctx.player_energy > energy_cost
    {
        25
    } else {
        -200
    };
    if status_prio > best.score {
        match eff_target {
            TargetType::AnyEnemy => {
                if let Some(slot) = (0..ctx.enemy_count).find(|&s| ctx.enemy_hp(s) > 0) {
                    let h = ctx.enemy_handle(slot);
                    debug_assert_ne!(h.0, u32::MAX);
                    *best = BestPilotMove {
                        score: status_prio,
                        card: ch,
                        target: h.0,
                    };
                }
            }
            TargetType::AnyAlly => {
                if let Some(ah) = state.ally_handles.iter().copied().find(|&ah| {
                    ah != state.player.creature_handle
                        && state.creature(ah).is_some_and(|c| c.is_alive())
                }) {
                    debug_assert_ne!(ah.0, u32::MAX);
                    *best = BestPilotMove {
                        score: status_prio,
                        card: ch,
                        target: ah.0,
                    };
                }
            }
            TargetType::AnyPlayer => {
                if state.player_creature().is_some_and(|c| c.is_alive()) {
                    let h = state.player.creature_handle;
                    debug_assert_ne!(h.0, u32::MAX);
                    *best = BestPilotMove {
                        score: status_prio,
                        card: ch,
                        target: h.0,
                    };
                }
            }
            _ => {
                *best = BestPilotMove {
                    score: status_prio,
                    card: ch,
                    target: u32::MAX,
                };
            }
        }
    }
}

#[cold]
#[inline(never)]
fn read_genetic_algorithm_block(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    ch: CardHandle,
    upg: bool,
    profile: &CardRolloutProfile,
) -> i32 {
    read_comparison_var(state, content, ch, "Block").unwrap_or_else(|| {
        if upg {
            profile.upg_block as i32
        } else {
            profile.base_block as i32
        }
    })
}

const DEFENSIVE_FLAG_BONUS: [i32; 8] = [0, 320, 340, 660, 280, 600, 620, 940];

#[inline(always)]
#[allow(clippy::too_many_arguments)]
fn eval_and_update_best_pilot_card<const IS_SETUP: bool>(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    ctx: &FastTurnContext,
    pre: &MoveScorePrecomp,
    ch: CardHandle,
    card_id: CardId,
    energy_cost: i32,
    star_cost: i32,
    upg: bool,
    exhausts: bool,
    is_retained: bool,
    profile: &CardRolloutProfile,
    best: &mut BestPilotMove,
) {
    let flags = profile.op_flags;
    let mflags = profile.meta_flags;
    let card_type = profile.card_type;
    let base_target_type = profile.target_type;
    let has_draw = (flags & op::HAS_DRAW) != 0;
    let has_energy_or_star_gain = (flags & op::HAS_ENERGY_OR_STAR_GAIN) != 0;
    let has_engine_op = (flags & op::HAS_ENGINE_OP) != 0;
    let has_self_buff = (flags & op::HAS_SELF_BUFF) != 0;
    let has_enemy_debuff = (flags & op::HAS_ENEMY_DEBUFF) != 0;
    let has_heal = (mflags & mf::MAY_HEAL) != 0;
    let has_self_hp_loss = (flags & op::HAS_SELF_HP_LOSS) != 0;
    let has_block_op = (flags & op::HAS_BLOCK) != 0 || (mflags & mf::GAINS_BLOCK) != 0;
    let has_attack_op = (flags & op::HAS_ATTACK) != 0 || card_type == CardType::Attack;

    let eff_target = match card_id {
        CardId::Shiv => {
            let resolved = if ctx.player_has_fan_of_knives {
                TargetType::AllEnemies
            } else {
                TargetType::AnyEnemy
            };
            #[cfg(debug_assertions)]
            debug_assert_eq!(
                resolved,
                content
                    .card_resolved_target_type(state, ch)
                    .unwrap_or(base_target_type)
            );
            resolved
        }
        CardId::SovereignBlade => {
            let resolved = if ctx.player_has_seeking_edge {
                TargetType::AllEnemies
            } else {
                TargetType::AnyEnemy
            };
            #[cfg(debug_assertions)]
            debug_assert_eq!(
                resolved,
                content
                    .card_resolved_target_type(state, ch)
                    .unwrap_or(base_target_type)
            );
            resolved
        }
        CardId::MadScience => content
            .card_resolved_target_type(state, ch)
            .unwrap_or(base_target_type),
        _ => base_target_type,
    };

    // Avoid wasting energy on non-helpful Status/Curse cards.
    if matches!(card_type, CardType::Status | CardType::Curse) {
        score_status_or_curse_card(
            state,
            ctx,
            pre,
            ch,
            eff_target,
            energy_cost,
            star_cost,
            exhausts,
            has_self_hp_loss,
            best,
        );
        return;
    }

    let est_block = if has_block_op {
        let raw_block = if card_id == CardId::GeneticAlgorithm {
            read_genetic_algorithm_block(state, content, ch, upg, profile)
        } else if upg {
            profile.upg_block as i32
        } else {
            profile.base_block as i32
        };
        if raw_block > 0 {
            let mut b = (raw_block + ctx.player_dex).max(0);
            if ctx.player_frail {
                let scaled = (b * 3) >> 2;
                #[cfg(debug_assertions)]
                debug_assert_eq!(scaled, (b * 75) / 100);
                b = scaled;
            }
            b
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
            let scaled = (per_hit * 3) >> 2;
            #[cfg(debug_assertions)]
            debug_assert_eq!(scaled, (per_hit * 75) / 100);
            per_hit = scaled;
        }
        (base_dmg, hits, per_hit)
    } else {
        (0, 1, 0)
    };

    if has_self_hp_loss && pre.low_hp {
        if eff_target != TargetType::AnyEnemy && !(has_attack_op && base_dmg > 0) {
            return;
        }
    }

    let mut common_prio = 50i32 + if has_heal { pre.heal_bonus } else { 0 };
    if energy_cost == 0 && star_cost == 0 {
        if has_energy_or_star_gain {
            common_prio += 2_400;
        } else if has_draw && !has_self_hp_loss {
            common_prio += 1_800;
        } else if card_type == CardType::Power {
            common_prio += 2_200;
        } else {
            common_prio += 250;
        }
    } else if has_energy_or_star_gain && energy_cost <= 1 {
        common_prio += 1_300;
    }

    let useful_block = if pre.has_unblocked {
        est_block.min(pre.unblocked)
    } else {
        0
    };
    let retained_penalty_applies = pre.setup_retained_check && is_retained;
    if !IS_SETUP {
        if pre.has_unblocked {
            if useful_block > 0 {
                common_prio += useful_block * pre.block_mult + 450;
            } else if est_block > 0 {
                common_prio += est_block * 15;
            }
        } else if est_block > 0 {
            common_prio += est_block.min(15) * 6;
        }
        if card_type == CardType::Power {
            common_prio += if !pre.has_unblocked {
                pre.power_non_setup_base + (profile.setup_weight as i32) * 15
            } else {
                pre.power_non_setup_base
            };
        }
        let lut_idx = ((flags & 1) | ((flags >> 1) & 6)) as usize;
        let lut_bonus = unsafe { *DEFENSIVE_FLAG_BONUS.get_unchecked(lut_idx) };
        #[cfg(debug_assertions)]
        {
            let expected = if has_draw { 320 } else { 0 }
                + if has_engine_op { 340 } else { 0 }
                + if has_self_buff { 280 } else { 0 };
            debug_assert_eq!(lut_bonus, expected);
        }
        common_prio += lut_bonus;
        if !pre.has_unblocked && energy_cost > 0 {
            common_prio += energy_cost * 90;
        }
    } else {
        if pre.lethal_threat && useful_block > 0 {
            common_prio += useful_block * 240 + 1_200;
        }
        if card_type == CardType::Power {
            common_prio += 1_550 + (profile.setup_weight as i32).clamp(0, 30) * 25;
        }
        if has_engine_op {
            common_prio += 950;
        }
        if has_self_buff {
            common_prio += 900;
        }
        if has_draw {
            common_prio += 820;
        }
        if has_enemy_debuff {
            common_prio += 800;
        }
        if pre.has_unblocked && useful_block > 0 && !pre.lethal_threat {
            common_prio += useful_block * 70 + 220;
        }
    }
    if has_self_hp_loss && !pre.lethal_threat {
        common_prio -= 180;
    }

    match eff_target {
        TargetType::AnyEnemy => {
            if ctx.enemy_count == 1 {
                let single = if has_attack_op && base_dmg > 0 {
                    let h = if ctx.enemy_vuln(0) {
                        let vh = (per_hit * 3) >> 1;
                        #[cfg(debug_assertions)]
                        debug_assert_eq!(vh, (per_hit * 150) / 100);
                        vh
                    } else {
                        per_hit
                    };
                    h * hits
                } else {
                    0
                };
                score_any_enemy_slot::<IS_SETUP>(
                    ctx,
                    pre,
                    energy_cost,
                    has_self_hp_loss,
                    common_prio,
                    has_enemy_debuff,
                    eff_target,
                    retained_penalty_applies,
                    has_attack_op,
                    base_dmg,
                    single,
                    0,
                    ch,
                    best,
                );
            } else if !IS_SETUP && !has_attack_op {
                let best_slot = if pre.has_unblocked && has_enemy_debuff {
                    ctx.best_debuff_enemy_slot
                } else {
                    ctx.best_non_attack_enemy_slot
                };
                #[cfg(debug_assertions)]
                let mut dbg_best = *best;
                if let Some(slot) = best_slot {
                    score_any_enemy_slot::<IS_SETUP>(
                        ctx,
                        pre,
                        energy_cost,
                        has_self_hp_loss,
                        common_prio,
                        has_enemy_debuff,
                        eff_target,
                        retained_penalty_applies,
                        false,
                        0,
                        0,
                        slot,
                        ch,
                        best,
                    );
                }
                #[cfg(debug_assertions)]
                {
                    for slot in 0..ctx.enemy_count {
                        score_any_enemy_slot::<IS_SETUP>(
                            ctx,
                            pre,
                            energy_cost,
                            has_self_hp_loss,
                            common_prio,
                            has_enemy_debuff,
                            eff_target,
                            retained_penalty_applies,
                            false,
                            0,
                            0,
                            slot,
                            ch,
                            &mut dbg_best,
                        );
                    }
                    debug_assert_eq!(*best, dbg_best);
                }
            } else if !IS_SETUP && has_attack_op && base_dmg > 0 {
                if has_self_hp_loss && pre.low_hp {
                    return;
                }
                debug_assert!(ctx.alive_enemies >= 2);
                let dmg_normal = per_hit * hits;
                let dmg_vuln = if ctx.any_enemy_vuln {
                    let vuln_hit = (per_hit * 3) >> 1;
                    #[cfg(debug_assertions)]
                    debug_assert_eq!(vuln_hit, (per_hit * 150) / 100);
                    vuln_hit * hits
                } else {
                    dmg_normal
                };
                #[cfg(debug_assertions)]
                let mut dbg_best = *best;
                let debuff_unblocked = pre.has_unblocked && has_enemy_debuff;
                let mut local_best_sc = best.score;
                let mut local_best_handle: u32 = u32::MAX;
                for slot in 0..ctx.enemy_count {
                    let es = ctx.enemy_slot(slot);
                    if es.hp <= 0 {
                        continue;
                    }
                    let single = if es.vuln { dmg_vuln } else { dmg_normal };
                    let mut sc = common_prio + single * 45 + es.target_bonus;
                    if single >= es.eff_hp {
                        sc += 1_600;
                        if es.incoming > 0 {
                            sc += es.incoming.min(pre.unblocked_plus_10) * pre.kill_inc_mult;
                        }
                    }
                    if debuff_unblocked {
                        sc += if es.incoming > 0 { 420 } else { 180 };
                    }
                    if sc > local_best_sc {
                        local_best_sc = sc;
                        local_best_handle = es.handle.0;
                    }
                }
                if local_best_handle != u32::MAX {
                    *best = BestPilotMove {
                        score: local_best_sc,
                        card: ch,
                        target: local_best_handle,
                    };
                }
                #[cfg(debug_assertions)]
                {
                    for slot in 0..ctx.enemy_count {
                        let single = if ctx.enemy_vuln(slot) {
                            dmg_vuln
                        } else {
                            dmg_normal
                        };
                        score_any_enemy_slot::<IS_SETUP>(
                            ctx,
                            pre,
                            energy_cost,
                            has_self_hp_loss,
                            common_prio,
                            has_enemy_debuff,
                            eff_target,
                            retained_penalty_applies,
                            has_attack_op,
                            base_dmg,
                            single,
                            slot,
                            ch,
                            &mut dbg_best,
                        );
                    }
                    debug_assert_eq!(*best, dbg_best);
                }
            } else {
                let (dmg_normal, dmg_vuln) = if has_attack_op && base_dmg > 0 {
                    let dn = per_hit * hits;
                    let dv = if ctx.any_enemy_vuln {
                        let vuln_hit = (per_hit * 3) >> 1;
                        #[cfg(debug_assertions)]
                        debug_assert_eq!(vuln_hit, (per_hit * 150) / 100);
                        vuln_hit * hits
                    } else {
                        dn
                    };
                    (dn, dv)
                } else {
                    (0, 0)
                };
                for slot in 0..ctx.enemy_count {
                    let single = if ctx.enemy_vuln(slot) {
                        dmg_vuln
                    } else {
                        dmg_normal
                    };
                    score_any_enemy_slot::<IS_SETUP>(
                        ctx,
                        pre,
                        energy_cost,
                        has_self_hp_loss,
                        common_prio,
                        has_enemy_debuff,
                        eff_target,
                        retained_penalty_applies,
                        has_attack_op,
                        base_dmg,
                        single,
                        slot,
                        ch,
                        best,
                    );
                }
            }
        }
        _ => {
            let target_handle = match eff_target {
                TargetType::AnyAlly => {
                    let Some(ah) = state.ally_handles.iter().copied().find(|&ah| {
                        ah != state.player.creature_handle
                            && state.creature(ah).is_some_and(|c| c.is_alive())
                    }) else {
                        return;
                    };
                    ah.0
                }
                TargetType::AnyPlayer => {
                    if !state.player_creature().is_some_and(|c| c.is_alive()) {
                        return;
                    }
                    state.player.creature_handle.0
                }
                _ => u32::MAX,
            };

            let (est_total_dmg, kills_count, killed_incoming) = if has_attack_op {
                if base_dmg > 0 {
                    if eff_target == TargetType::AllEnemies {
                        let mut total = 0i32;
                        let mut kills = 0i32;
                        let mut kill_inc = 0i32;
                        let dmg_normal = per_hit * hits;
                        let dmg_vuln = if ctx.any_enemy_vuln {
                            let vuln_hit = (per_hit * 3) >> 1;
                            #[cfg(debug_assertions)]
                            debug_assert_eq!(vuln_hit, (per_hit * 150) / 100);
                            vuln_hit * hits
                        } else {
                            dmg_normal
                        };
                        for slot in 0..ctx.enemy_count {
                            let es = ctx.enemy_slot(slot);
                            if es.hp <= 0 {
                                continue;
                            }
                            let dmg = if es.vuln { dmg_vuln } else { dmg_normal };
                            total += dmg;
                            if dmg >= es.eff_hp {
                                kills += 1;
                                kill_inc += es.incoming;
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

            let sc = pilot_finish_score::<IS_SETUP, false>(
                ctx,
                pre,
                energy_cost,
                has_self_hp_loss,
                common_prio,
                has_enemy_debuff,
                eff_target,
                retained_penalty_applies,
                has_attack_op,
                est_total_dmg,
                kills_count,
                killed_incoming,
                None,
            );
            if sc > best.score {
                *best = BestPilotMove {
                    score: sc,
                    card: ch,
                    target: target_handle,
                };
            }
        }
    }
}

fn select_best_pilot_move_impl<const IS_SETUP: bool>(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    playable: &[PlayableRolloutCard],
    blocked: &[CardHandle],
    ctx: &FastTurnContext,
) -> Option<(CardHandle, Option<CreatureHandle>)> {
    let tables = state.hook_cache.cached_tables;
    let card_rollout_profiles = tables.map(|t| t.card_rollout_profiles);
    let full_tables = card_rollout_profiles.is_some_and(|rp| CardId::COUNT <= rp.len());
    let has_blocked = !blocked.is_empty();
    let use_blocked_mask = has_blocked && playable.len() <= 64;
    let blocked_mask = if use_blocked_mask {
        let mut mask = 0u64;
        for (p_idx, p) in playable.iter().enumerate() {
            if blocked.contains(&p.handle) {
                mask |= 1u64 << p_idx;
            }
        }
        mask
    } else {
        0
    };
    let mut has_non_retained = false;
    if IS_SETUP {
        for (p_idx, p) in playable.iter().enumerate() {
            if p.is_retained {
                continue;
            }
            if has_blocked {
                if use_blocked_mask {
                    if (blocked_mask & (1u64 << p_idx)) != 0 {
                        continue;
                    }
                } else if blocked.contains(&p.handle) {
                    continue;
                }
            }
            let cidx = p.card_id as usize;
            let card_type = if full_tables {
                unsafe { card_rollout_profiles.unwrap_unchecked().get_unchecked(cidx).card_type }
            } else if let Some(pr) = card_rollout_profiles.and_then(|rp| rp.get(cidx)) {
                pr.card_type
            } else if let Ok(m) = content.card_meta(p.card_id) {
                m.card_type
            } else {
                continue;
            };
            if matches!(card_type, CardType::Status | CardType::Curse) {
                continue;
            }
            has_non_retained = true;
            break;
        }
    }

    let pre = MoveScorePrecomp::new::<IS_SETUP>(ctx, has_non_retained);
    let mut best = BestPilotMove::EMPTY;

    for (p_idx, p) in playable.iter().enumerate() {
        let ch = p.handle;
        if has_blocked {
            if use_blocked_mask {
                if (blocked_mask & (1u64 << p_idx)) != 0 {
                    continue;
                }
            } else if blocked.contains(&ch) {
                continue;
            }
        }
        let card_id = p.card_id;
        let cidx = card_id as usize;
        let fallback_profile;
        let profile: &CardRolloutProfile = if full_tables {
            unsafe { card_rollout_profiles.unwrap_unchecked().get_unchecked(cidx) }
        } else if let Some(pr) = card_rollout_profiles.and_then(|rp| rp.get(cidx)) {
            pr
        } else {
            fallback_profile = content.card_rollout_profile(card_id);
            &fallback_profile
        };
        eval_and_update_best_pilot_card::<IS_SETUP>(
            state,
            content,
            ctx,
            &pre,
            ch,
            card_id,
            p.energy_cost.max(0),
            p.star_cost.max(0),
            p.upg,
            p.exhausts,
            p.is_retained,
            profile,
            &mut best,
        );
    }

    best.to_option()
}

#[derive(Clone, Copy)]
struct FirstDefensiveCand {
    handle: CardHandle,
    card_id: CardId,
    energy_cost: i32,
    star_cost: i32,
    upg: bool,
    exhausts: bool,
    is_retained: bool,
    prof: &'static CardRolloutProfile,
}

#[inline(never)]
#[allow(clippy::too_many_arguments)]
fn init_defensive_ctx_and_eval_first(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    intent_cache: &mut IntentHitsCache,
    ctx_buf: &mut std::mem::MaybeUninit<FastTurnContext>,
    pre_buf: &mut std::mem::MaybeUninit<MoveScorePrecomp>,
    first: FirstDefensiveCand,
    best: &mut BestPilotMove,
) {
    let ctx = compute_fast_turn_context_into::<false>(
        state,
        content,
        root_baseline,
        intent_cache,
        ctx_buf,
    );
    let pre = pre_buf.write(MoveScorePrecomp::new::<false>(ctx, false));
    eval_and_update_best_pilot_card::<false>(
        state,
        content,
        ctx,
        pre,
        first.handle,
        first.card_id,
        first.energy_cost.max(0),
        first.star_cost.max(0),
        first.upg,
        first.exhausts,
        first.is_retained,
        first.prof,
        best,
    );
}

#[inline]
#[allow(clippy::too_many_arguments)]
fn select_best_defensive_tempo_move_fast(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    intent_cache: &mut IntentHitsCache,
    hand_buf: &mut Vec<CardHandle>,
    seen_keys: &mut Vec<RolloutDedupKey>,
) -> Option<Option<(CardHandle, Option<CreatureHandle>)>> {
    if state.player.hand.is_empty() || !state.is_in_progress {
        return Some(None);
    }
    if !state.hook_cache.masks_are_valid() {
        let _ = state.ensure_hook_cache(content);
    }
    if sts2_kernel::hooks::is_over_or_ending_checked(state, content).unwrap_or(true) {
        return Some(None);
    }
    let (fast_query_safe, comb, fast_energy_hook, fast_should_play) =
        resolve_fast_query_and_hooks(state, content);
    if !fast_query_safe || fast_energy_hook == FastEnergyHook::Fallback {
        return None;
    }

    let (tables, full_profiles, cards_contiguous, has_hex_power, bound_blocked) =
        resolve_fast_scan_meta(state, comb, fast_energy_hook, fast_should_play);
    let player_energy = state.player.energy;
    let player_stars = state.player.stars;
    let mut seen_fast = [0u64; 16];
    let mut seen_fast_len = 0usize;
    let mut use_seen_keys = false;

    hand_buf.clear();
    hand_buf.extend_from_slice(&state.player.hand);

    let mut first_cand: Option<FirstDefensiveCand> = None;
    let mut has_ctx = false;
    let mut ctx_buf = std::mem::MaybeUninit::<FastTurnContext>::uninit();
    let mut pre_buf = std::mem::MaybeUninit::<MoveScorePrecomp>::uninit();
    let mut best = BestPilotMove::EMPTY;

    let hand_len = hand_buf.len();
    #[cfg(target_arch = "x86_64")]
    if hand_len >= 4 {
        for &ahead_ch in &hand_buf[..3] {
            let ahead = ahead_ch.0.wrapping_sub(1) as usize;
            if ahead < state.cards.len() {
                unsafe {
                    core::arch::x86_64::_mm_prefetch::<{ core::arch::x86_64::_MM_HINT_T0 }>(
                        state.cards.as_ptr().add(ahead).cast::<i8>(),
                    );
                }
            }
        }
    }

    for hi in 0..hand_len {
        let ch = hand_buf[hi];
        #[cfg(target_arch = "x86_64")]
        if hand_len >= 4 && hi + 3 < hand_len {
            let ahead = unsafe { *hand_buf.get_unchecked(hi + 3) }
                .0
                .wrapping_sub(1) as usize;
            if ahead < state.cards.len() {
                unsafe {
                    core::arch::x86_64::_mm_prefetch::<{ core::arch::x86_64::_MM_HINT_T0 }>(
                        state.cards.as_ptr().add(ahead).cast::<i8>(),
                    );
                }
            }
        }
        let Some(c) = try_extract_fast_playable_hand_card(
            state,
            content,
            comb,
            fast_energy_hook,
            tables,
            full_profiles,
            cards_contiguous,
            player_energy,
            player_stars,
            has_hex_power,
            bound_blocked,
            ch,
            &mut seen_fast,
            &mut seen_fast_len,
            &mut use_seen_keys,
            seen_keys,
        ) else {
            continue;
        };

        if !has_ctx {
            if let Some(first) = first_cand.take() {
                init_defensive_ctx_and_eval_first(
                    state,
                    content,
                    root_baseline,
                    intent_cache,
                    &mut ctx_buf,
                    &mut pre_buf,
                    first,
                    &mut best,
                );
                has_ctx = true;
            } else {
                first_cand = Some(FirstDefensiveCand {
                    handle: c.handle,
                    card_id: c.card_id,
                    energy_cost: c.energy_cost,
                    star_cost: c.star_cost,
                    upg: c.upg,
                    exhausts: c.exhausts,
                    is_retained: c.is_retained,
                    prof: c.prof,
                });
                continue;
            }
        }
        let ctx = unsafe { ctx_buf.assume_init_ref() };
        let pre = unsafe { pre_buf.assume_init_ref() };
        eval_and_update_best_pilot_card::<false>(
            state,
            content,
            ctx,
            pre,
            c.handle,
            c.card_id,
            c.energy_cost.max(0),
            c.star_cost.max(0),
            c.upg,
            c.exhausts,
            c.is_retained,
            c.prof,
            &mut best,
        );
    }

    if has_ctx {
        return Some(best.to_option());
    }
    let Some(p) = first_cand else {
        return Some(None);
    };
    if p.energy_cost < 1_000_000 {
        let profile = p.prof;
        if !matches!(profile.card_type, CardType::Status | CardType::Curse)
            && (profile.op_flags & op::HAS_SELF_HP_LOSS) == 0
        {
            let eff_target = match p.card_id {
                CardId::Shiv | CardId::SovereignBlade | CardId::MadScience => content
                    .card_resolved_target_type(state, p.handle)
                    .unwrap_or(profile.target_type),
                _ => profile.target_type,
            };
            let fast_pick = match eff_target {
                TargetType::AnyEnemy => {
                    let mut sole_enemy: Option<CreatureHandle> = None;
                    let mut living = 0usize;
                    for &eh in &state.enemy_handles {
                        if state.creature(eh).is_some_and(|c| c.is_alive()) {
                            sole_enemy = Some(eh);
                            living += 1;
                            if living > 1 {
                                break;
                            }
                        }
                    }
                    if living == 1 {
                        Some((p.handle, sole_enemy))
                    } else {
                        None
                    }
                }
                TargetType::AnyAlly => None,
                TargetType::AnyPlayer => {
                    if state.player_creature().is_some_and(|c| c.is_alive()) {
                        Some((p.handle, Some(state.player.creature_handle)))
                    } else {
                        None
                    }
                }
                _ => Some((p.handle, None)),
            };
            if let Some(pick) = fast_pick {
                return Some(Some(pick));
            }
        }
    }
    init_defensive_ctx_and_eval_first(
        state,
        content,
        root_baseline,
        intent_cache,
        &mut ctx_buf,
        &mut pre_buf,
        p,
        &mut best,
    );
    Some(best.to_option())
}

#[cold]
#[inline(never)]
#[allow(clippy::too_many_arguments)]
fn collect_and_select_pilot_move_slow(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    intent_cache: &mut IntentHitsCache,
    pilot: PilotKind,
    blocked_buf: &[CardHandle],
    playable_buf: &mut Vec<PlayableRolloutCard>,
    hand_buf: &mut Vec<CardHandle>,
    seen_keys: &mut Vec<RolloutDedupKey>,
) -> Option<(CardHandle, Option<CreatureHandle>)> {
    collect_rollout_playable_cards(state, content, hand_buf, seen_keys, playable_buf);
    if playable_buf.is_empty() {
        return None;
    }
    let fast_pick = if pilot == PilotKind::DefensiveTempo
        && playable_buf.len() == 1
        && blocked_buf.is_empty()
    {
        let p = playable_buf[0];
        if p.energy_cost < 1_000_000 {
            let profile = state.fast_card_rollout_profile(content, p.card_id);
            if !matches!(profile.card_type, CardType::Status | CardType::Curse)
                && (profile.op_flags & op::HAS_SELF_HP_LOSS) == 0
            {
                let eff_target = match p.card_id {
                    CardId::Shiv | CardId::SovereignBlade | CardId::MadScience => content
                        .card_resolved_target_type(state, p.handle)
                        .unwrap_or(profile.target_type),
                    _ => profile.target_type,
                };
                match eff_target {
                    TargetType::AnyEnemy => {
                        let mut sole_enemy: Option<CreatureHandle> = None;
                        let mut living = 0usize;
                        for &eh in &state.enemy_handles {
                            if state.creature(eh).is_some_and(|c| c.is_alive()) {
                                sole_enemy = Some(eh);
                                living += 1;
                                if living > 1 {
                                    break;
                                }
                            }
                        }
                        if living == 1 {
                            Some((p.handle, sole_enemy))
                        } else {
                            None
                        }
                    }
                    TargetType::AnyAlly => None,
                    TargetType::AnyPlayer => {
                        if state.player_creature().is_some_and(|c| c.is_alive()) {
                            Some((p.handle, Some(state.player.creature_handle)))
                        } else {
                            None
                        }
                    }
                    _ => Some((p.handle, None)),
                }
            } else {
                None
            }
        } else {
            None
        }
    } else {
        None
    };
    if let Some(pick) = fast_pick {
        #[cfg(debug_assertions)]
        {
            let dbg_ctx =
                compute_fast_turn_context::<false>(state, content, root_baseline, intent_cache);
            debug_assert_eq!(
                Some(pick),
                select_best_pilot_move_impl::<false>(
                    state,
                    content,
                    playable_buf,
                    blocked_buf,
                    &dbg_ctx,
                )
            );
        }
        return Some(pick);
    }
    let ctx = if pilot == PilotKind::DefensiveTempo {
        compute_fast_turn_context::<false>(state, content, root_baseline, intent_cache)
    } else {
        compute_fast_turn_context::<true>(state, content, root_baseline, intent_cache)
    };
    select_best_pilot_move(state, content, playable_buf, blocked_buf, &ctx, pilot)
}

struct Turn1ProbeCacheHook<'a, 'b> {
    probe_cache: &'a mut ProbeCacheAccess<'b>,
    l1: &'a mut ChoiceProbeL1,
    cfg_hash_1t: u64,
    hit: Option<(usize, CachedProbeEntry)>,
    miss_key: Option<(StateHash, usize)>,
}

#[allow(clippy::too_many_arguments)]
fn run_pilot_turns<const TRACK_AUTO_POTIONS: bool, R>(
    state: &mut BattleState,
    compact_steps: &mut Vec<CompactRolloutStep>,
    choice_slab: &mut RolloutChoiceSlab,
    choice_scored_buf: &mut Vec<(usize, i32, u32)>,
    sel_buf: &mut SmallVec<[usize; 16]>,
    intent_cache: &mut IntentHitsCache,
    mut restore_base: R,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    pilot: PilotKind,
    horizon_turns: usize,
    blocked_buf: &mut SmallVec<[CardHandle; 8]>,
    empty_q: &mut SmallVec<[Vec<usize>; 2]>,
    playable_buf: &mut Vec<PlayableRolloutCard>,
    hand_buf: &mut Vec<CardHandle>,
    seen_keys: &mut Vec<RolloutDedupKey>,
    mut turn1_hook: Option<&mut Turn1ProbeCacheHook<'_, '_>>,
) -> Option<(usize, u16)>
where
    R: FnMut(&mut BattleState) -> bool,
{
    compact_steps.clear();
    choice_slab.clear();
    let mut total_steps = 0usize;
    let mut auto_potions_used = 0u16;
    let mut cur_potions = if TRACK_AUTO_POTIONS {
        count_non_empty_potion_slots(state)
    } else {
        0
    };

    for turn in 0..horizon_turns {
        if !state.is_in_progress
            || state.won
            || state.battleworn_dummy_timed_out
            || !state.has_living_player_creature()
            || total_steps >= MAX_TOTAL_ROLLOUT_STEPS
        {
            break;
        }
        if turn == 1 {
            if let Some(hook) = turn1_hook.as_deref_mut() {
                let h1 = hash_canonical_key(state);
                if let Some(Some(entry1)) =
                    hook.probe_cache.get_with_l1(hook.l1, h1, hook.cfg_hash_1t)
                {
                    hook.hit = Some((total_steps, entry1));
                    return Some((total_steps, auto_potions_used));
                }
                hook.miss_key = Some((h1, total_steps));
            }
        }

        blocked_buf.clear();
        for _step in 0..MAX_ROLLOUT_ACTIONS_PER_TURN {
            if !state.is_in_progress
                || state.won
                || state.player.phase != PlayerTurnPhase::Play
                || total_steps >= MAX_TOTAL_ROLLOUT_STEPS
            {
                break;
            }
            let fast_mv_opt = if pilot == PilotKind::DefensiveTempo && blocked_buf.is_empty() {
                select_best_defensive_tempo_move_fast(
                    state,
                    content,
                    root_baseline,
                    intent_cache,
                    hand_buf,
                    seen_keys,
                )
            } else {
                None
            };
            let (ch, tgt) = match fast_mv_opt {
                Some(fast_mv) => {
                    #[cfg(debug_assertions)]
                    {
                        collect_rollout_playable_cards(
                            state,
                            content,
                            hand_buf,
                            seen_keys,
                            playable_buf,
                        );
                        let dbg_ctx = compute_fast_turn_context::<false>(
                            state,
                            content,
                            root_baseline,
                            intent_cache,
                        );
                        let dbg_mv = select_best_pilot_move_impl::<false>(
                            state,
                            content,
                            playable_buf,
                            blocked_buf,
                            &dbg_ctx,
                        );
                        debug_assert_eq!(fast_mv, dbg_mv);
                    }
                    let Some(pick) = fast_mv else {
                        break;
                    };
                    pick
                }
                None => {
                    let Some(pick) = collect_and_select_pilot_move_slow(
                        state,
                        content,
                        root_baseline,
                        intent_cache,
                        pilot,
                        blocked_buf,
                        playable_buf,
                        hand_buf,
                        seen_keys,
                    ) else {
                        break;
                    };
                    pick
                }
            };

            empty_q.clear();
            let resolved =
                match CombatEngine::play_card_prechecked(state, content, ch, tgt, empty_q) {
                    Ok(outcome) => resolve_step_choices(
                        state,
                        content,
                        &root_baseline.root_card_choice_quality,
                        outcome,
                        choice_slab,
                        choice_scored_buf,
                        sel_buf,
                    ),
                    Err(_) => None,
                };
            let Some((choices_offset, choices_len)) = resolved else {
                if !restore_base(state) {
                    return None;
                }
                let replayed_pots = replay_compact_steps(
                    state,
                    content,
                    compact_steps,
                    choice_slab,
                    empty_q,
                    sel_buf,
                )
                .ok()?;
                if TRACK_AUTO_POTIONS {
                    auto_potions_used = replayed_pots;
                    cur_potions = count_non_empty_potion_slots(state);
                }
                blocked_buf.push(ch);
                continue;
            };
            if TRACK_AUTO_POTIONS {
                let potions_after = count_non_empty_potion_slots(state);
                auto_potions_used += cur_potions.saturating_sub(potions_after) as u16;
                cur_potions = potions_after;
            }

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

        let Ok(end_out) = CombatEngine::end_player_turn(state, content) else {
            return None;
        };
        let Some((choices_offset, choices_len)) = resolve_step_choices(
            state,
            content,
            &root_baseline.root_card_choice_quality,
            end_out,
            choice_slab,
            choice_scored_buf,
            sel_buf,
        ) else {
            return None;
        };
        if TRACK_AUTO_POTIONS {
            let potions_after = count_non_empty_potion_slots(state);
            auto_potions_used += cur_potions.saturating_sub(potions_after) as u16;
            cur_potions = potions_after;
        }
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
fn finish_choice_probe_eval(
    eval_state: &BattleState,
    intent_cache: &mut IntentHitsCache,
    l1: &mut ChoiceProbeL1,
    exact_state_hash: StateHash,
    cfg_hash: u64,
    cfg_hash_1t: u64,
    probe_cache: &mut ProbeCacheAccess<'_>,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    profile: &SolverSearchProfile,
    eval_mode: EvalMode,
    run_opt: Option<(usize, u16)>,
    turn1_hit: Option<(usize, CachedProbeEntry)>,
    turn1_miss_key: Option<(StateHash, usize)>,
    optional_paid_potion_cost: i32,
    optional_paid_potion_count: usize,
    parent_action_count: u16,
) -> Option<ChoiceProbeResult> {
    if run_opt.is_some() {
        if let Some((turn0_steps, raw_entry1)) = turn1_hit {
            #[cfg(debug_assertions)]
            {
                let dbg_s1 = eval_state.clone();
                let mut dbg_state = dbg_s1.clone();
                let mut dbg_steps = Vec::new();
                let mut dbg_slab = RolloutChoiceSlab::default();
                let mut dbg_scored = Vec::new();
                let mut dbg_sel = SmallVec::new();
                let mut dbg_intent = IntentHitsCache::new();
                let mut dbg_blocked = SmallVec::new();
                let mut dbg_eq = SmallVec::new();
                let mut dbg_play = Vec::new();
                let mut dbg_hand = Vec::new();
                let mut dbg_keys = Vec::new();
                let dbg_turn1 = run_pilot_turns::<false, _>(
                    &mut dbg_state,
                    &mut dbg_steps,
                    &mut dbg_slab,
                    &mut dbg_scored,
                    &mut dbg_sel,
                    &mut dbg_intent,
                    |s| {
                        clone_battle_state_into(&dbg_s1, s);
                        true
                    },
                    content,
                    root_baseline,
                    PilotKind::DefensiveTempo,
                    1,
                    &mut dbg_blocked,
                    &mut dbg_eq,
                    &mut dbg_play,
                    &mut dbg_hand,
                    &mut dbg_keys,
                    None,
                );
                let dbg_steps1 = dbg_turn1.map_or(0, |(st, _)| st);
                let dbg_fast = evaluate_fast(&dbg_state, content, root_baseline, None);
                let dbg_win = dbg_fast.won;
                let dbg_loss = dbg_fast.player_dead || dbg_fast.projected_player_hp <= 0;
                let dbg_full = if dbg_win || dbg_loss || dbg_fast.is_event_defeat {
                    FullEvalDetails::default()
                } else {
                    evaluate_full_details_cached(
                        &dbg_state,
                        content,
                        &dbg_fast,
                        root_baseline,
                        Some(&mut dbg_intent),
                    )
                };
                let dbg_lin = !(profile.base_score_only || dbg_loss || dbg_fast.is_event_defeat);
                let dbg_scores1 = compute_scores_at_zero(
                    &dbg_fast,
                    &dbg_full,
                    root_baseline,
                    profile,
                    dbg_lin,
                    dbg_steps1.min(u16::MAX as usize) as u16,
                );
                debug_assert_eq!(raw_entry1.hp_lost, dbg_fast.cumulative_hp_lost);
                debug_assert_eq!(raw_entry1.board_investment, dbg_full.board_investment);
                debug_assert_eq!(raw_entry1.alchemize_count, dbg_fast.alchemize_count);
                debug_assert_eq!(raw_entry1.empty_potion_slots, dbg_fast.empty_potion_slots);
                debug_assert_eq!(raw_entry1.is_victory, dbg_win);
                debug_assert_eq!(raw_entry1.is_defeat, dbg_loss);
                debug_assert_eq!(raw_entry1.linear_adj, dbg_lin);
                for m in 0..3 {
                    debug_assert!((dbg_scores1[m] - raw_entry1.scores_at_zero[m]).abs() < 1e-6);
                }
            }
            let mut entry0 = raw_entry1;
            if entry0.linear_adj && turn0_steps > 0 {
                let act_coeff = if entry0.is_victory { 10.0 } else { 5.0 };
                let adj = (turn0_steps as f64) * act_coeff;
                for s in &mut entry0.scores_at_zero {
                    *s -= adj;
                }
            }
            probe_cache.insert_with_l1(l1, exact_state_hash, cfg_hash, Some(entry0));
            return Some(entry0.materialize_result(
                root_baseline,
                eval_mode,
                optional_paid_potion_cost,
                optional_paid_potion_count,
                parent_action_count,
            ));
        }
    }

    let total_steps = run_opt.map_or(0, |(steps, _)| steps);
    let fast = evaluate_fast(eval_state, content, root_baseline, None);
    let is_victory = fast.won;
    let is_defeat = fast.player_dead || fast.projected_player_hp <= 0;
    let terminal = is_victory || is_defeat || fast.is_event_defeat;
    let full = if terminal {
        FullEvalDetails::default()
    } else {
        evaluate_full_details_cached(
            eval_state,
            content,
            &fast,
            root_baseline,
            Some(intent_cache),
        )
    };
    let linear_adj = !(profile.base_score_only || is_defeat || fast.is_event_defeat);
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
    probe_cache.insert_with_l1(l1, exact_state_hash, cfg_hash, Some(entry));
    if run_opt.is_some() {
        if let Some((h1, turn0_steps)) = turn1_miss_key {
            let turn1_steps = total_steps.saturating_sub(turn0_steps).min(u16::MAX as usize) as u16;
            let scores1 = compute_scores_at_zero(
                &fast,
                &full,
                root_baseline,
                profile,
                linear_adj,
                turn1_steps,
            );
            let entry1 = CachedProbeEntry {
                scores_at_zero: scores1,
                ..entry
            };
            probe_cache.insert_with_l1(l1, h1, cfg_hash_1t, Some(entry1));
        }
    }
    Some(entry.materialize_result(
        root_baseline,
        eval_mode,
        optional_paid_potion_cost,
        optional_paid_potion_count,
        parent_action_count,
    ))
}

#[allow(clippy::too_many_arguments)]
fn evaluate_choice_probe_in_place_core<R>(
    ws: &mut ChoiceProbeWorkspace,
    state: &mut BattleState,
    restore_base: R,
    exact_state_hash: StateHash,
    probe_cache: &mut ProbeCacheAccess<'_>,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    profile: &SolverSearchProfile,
    eval_mode: EvalMode,
    max_turns_ahead: usize,
    optional_paid_potion_cost: i32,
    optional_paid_potion_count: usize,
    parent_action_count: u16,
) -> Option<ChoiceProbeResult>
where
    R: FnMut(&mut BattleState) -> bool,
{
    let cache_turns = max_turns_ahead.min(4) as u8;
    let cfg_hash = choice_probe_cfg_hash(profile, cache_turns);
    if let Some(cached) = probe_cache.get_with_l1(&mut ws.l1, exact_state_hash, cfg_hash) {
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
    let cfg_hash_1t = choice_probe_cfg_hash(profile, 0);
    let turn_slots = (cache_turns as usize) + 1;
    let mut turn1_hook = (cache_turns == 1).then(|| Turn1ProbeCacheHook {
        probe_cache,
        l1: &mut ws.l1,
        cfg_hash_1t,
        hit: None,
        miss_key: None,
    });
    let run_opt = run_pilot_turns::<false, _>(
        state,
        &mut ws.compact_steps,
        &mut ws.choice_slab,
        &mut ws.choice_scored_buf,
        &mut ws.sel_buf,
        &mut ws.intent_cache,
        restore_base,
        content,
        root_baseline,
        PilotKind::DefensiveTempo,
        turn_slots,
        &mut ws.blocked_buf,
        &mut ws.empty_q,
        &mut ws.playable_buf,
        &mut ws.hand_buf,
        &mut ws.seen_keys,
        turn1_hook.as_mut(),
    );
    let (turn1_hit, turn1_miss_key) = match turn1_hook {
        Some(h) => (h.hit, h.miss_key),
        None => (None, None),
    };
    finish_choice_probe_eval(
        state,
        &mut ws.intent_cache,
        &mut ws.l1,
        exact_state_hash,
        cfg_hash,
        cfg_hash_1t,
        probe_cache,
        content,
        root_baseline,
        profile,
        eval_mode,
        run_opt,
        turn1_hit,
        turn1_miss_key,
        optional_paid_potion_cost,
        optional_paid_potion_count,
        parent_action_count,
    )
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn evaluate_choice_probe_in_place(
    ws: &mut ChoiceProbeWorkspace,
    state: &mut BattleState,
    packed_fallback: &[u8],
    exact_state_hash: StateHash,
    probe_cache: &mut ProbeCacheAccess<'_>,
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
    evaluate_choice_probe_in_place_core(
        ws,
        state,
        |s| unpack_state(packed_fallback, static_ctx, s).is_ok(),
        exact_state_hash,
        probe_cache,
        content,
        root_baseline,
        profile,
        eval_mode,
        max_turns_ahead,
        optional_paid_potion_cost,
        optional_paid_potion_count,
        parent_action_count,
    )
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn evaluate_choice_probe_in_place_with_restore<R>(
    ws: &mut ChoiceProbeWorkspace,
    state: &mut BattleState,
    restore_base: R,
    exact_state_hash: StateHash,
    probe_cache: &mut ProbeCacheAccess<'_>,
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    profile: &SolverSearchProfile,
    eval_mode: EvalMode,
    max_turns_ahead: usize,
    optional_paid_potion_cost: i32,
    optional_paid_potion_count: usize,
    parent_action_count: u16,
) -> Option<ChoiceProbeResult>
where
    R: FnMut(&mut BattleState) -> bool,
{
    evaluate_choice_probe_in_place_core(
        ws,
        state,
        restore_base,
        exact_state_hash,
        probe_cache,
        content,
        root_baseline,
        profile,
        eval_mode,
        max_turns_ahead,
        optional_paid_potion_cost,
        optional_paid_potion_count,
        parent_action_count,
    )
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn evaluate_choice_probe_packed(
    ws: &mut ChoiceProbeWorkspace,
    packed_state: &[u8],
    exact_state_hash: StateHash,
    probe_cache: &mut ProbeCacheAccess<'_>,
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
    if let Some(cached) = probe_cache.get_with_l1(&mut ws.l1, exact_state_hash, cfg_hash) {
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
        probe_cache.insert_with_l1(&mut ws.l1, exact_state_hash, cfg_hash, None);
        return None;
    }
    let cfg_hash_1t = choice_probe_cfg_hash(profile, 0);
    let turn_slots = (cache_turns as usize) + 1;
    let mut turn1_hook = (cache_turns == 1).then(|| Turn1ProbeCacheHook {
        probe_cache,
        l1: &mut ws.l1,
        cfg_hash_1t,
        hit: None,
        miss_key: None,
    });
    let run_opt = run_pilot_turns::<false, _>(
        &mut ws.probe_state,
        &mut ws.compact_steps,
        &mut ws.choice_slab,
        &mut ws.choice_scored_buf,
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
        turn1_hook.as_mut(),
    );
    let (turn1_hit, turn1_miss_key) = match turn1_hook {
        Some(h) => (h.hit, h.miss_key),
        None => (None, None),
    };
    finish_choice_probe_eval(
        &ws.probe_state,
        &mut ws.intent_cache,
        &mut ws.l1,
        exact_state_hash,
        cfg_hash,
        cfg_hash_1t,
        probe_cache,
        content,
        root_baseline,
        profile,
        eval_mode,
        run_opt,
        turn1_hit,
        turn1_miss_key,
        optional_paid_potion_cost,
        optional_paid_potion_count,
        parent_action_count,
    )
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
    let (total_steps, auto_potions_used) = run_pilot_turns::<true, _>(
        &mut ws.pilot_state,
        steps_out,
        choices_out,
        &mut ws.choice_scored_buf,
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
        None,
    )?;

    let final_fast = evaluate_fast(&ws.pilot_state, content, root_baseline, None);
    let final_terminal = final_fast.won
        || final_fast.player_dead
        || final_fast.projected_player_hp <= 0
        || final_fast.is_event_defeat;
    let final_full = if final_terminal {
        crate::eval::FullEvalDetails::default()
    } else {
        evaluate_full_details_cached(
            &ws.pilot_state,
            content,
            &final_fast,
            root_baseline,
            Some(&mut ws.intent_cache),
        )
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
        let choices = if win_choices.steps_meta.is_empty() {
            RolloutChoiceSlab::default()
        } else {
            win_choices.clone()
        };
        Some(PromotedVictory {
            steps: SmallVec::from_slice(win_steps),
            choices,
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
    let next_ref = &next_idx;
    let run_worker = |ws: &mut RolloutWorkspace| loop {
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
    };
    let worker_fn = &run_worker;

    let (first_ws, rest_ws) = workspaces[..workers]
        .split_first_mut()
        .expect("workers >= 2");
    first_ws.results_buf.clear();
    for ws in rest_ws.iter_mut() {
        ws.results_buf.clear();
    }

    pool.scope(|scope| {
        for ws in rest_ws.iter_mut() {
            scope.spawn(move |_| worker_fn(ws));
        }
        worker_fn(first_ws);
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
        assert!(cache.shards.iter().all(|s| s.lock().table.is_none()));

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
                .filter(|s| s.lock().table.is_some())
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

    #[test]
    fn rollout_dedup_key_fast_u64_round_trip() {
        let card_id = CardId::StrikeIronclad;
        let samples = [(-1, -1), (0, -1), (2, 0), (3, 5)];
        for &(energy, star) in &samples {
            for &upg in &[0i32, 1i32] {
                for &has_ext in &[false, true] {
                    for aff_id in [None, Some(sts2_types::AfflictionId::Galvanized)] {
                        let expected =
                            RolloutDedupKey::new(card_id, upg, energy, star, has_ext, aff_id);
                        let fk = RolloutDedupKey::try_fast_u64(
                            card_id, upg, energy, star, has_ext, aff_id,
                        )
                        .expect("sample fits fast u64 dedup key");
                        assert_eq!(RolloutDedupKey::from_fast_u64(fk), expected);
                    }
                }
            }
        }
    }
}
