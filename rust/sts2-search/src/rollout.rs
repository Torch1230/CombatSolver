use crate::arena::{MainStateArena, NodeArena, NodeId};
use crate::coordinator::clone_battle_state_into;
use crate::eval::{
    compute_beam_rank_score_with_centi, evaluate_fast, evaluate_full, is_creature_reviving,
    EvalMode, FastEvalSummary, PotionUsePolicy, RootEvalBaseline, SolverPotionPolicy,
};
use crate::hash::StateHash;
use crate::packer::{empty_scratch_state, hash_canonical_key, unpack_state};
use crate::portfolio::SolverSearchProfile;
use crate::SearchPolicy;
use smallvec::SmallVec;
use std::borrow::Cow;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Instant;
use sts2_kernel::ordering::{resolve_local_energy_cost, resolve_local_star_cost};
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

type RolloutDedupKey = (
    CardId,
    i32,
    i32,
    i32,
    bool,
    Option<sts2_types::AfflictionId>,
);

#[derive(Clone, Copy, Debug, Default)]
pub(crate) struct ChoiceProbeResult {
    pub score: f64,
    pub hp_lost: i32,
    pub board_investment: i32,
    pub is_victory: bool,
    pub is_defeat: bool,
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
    pub(crate) playable_buf: SmallVec<[(CardHandle, i32, i32); 16]>,
    pub(crate) hand_buf: SmallVec<[CardHandle; 16]>,
    pub(crate) seen_keys: SmallVec<[RolloutDedupKey; 16]>,
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
            playable_buf: SmallVec::new(),
            hand_buf: SmallVec::new(),
            seen_keys: SmallVec::new(),
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
    pub(crate) playable_buf: SmallVec<[(CardHandle, i32, i32); 16]>,
    pub(crate) hand_buf: SmallVec<[CardHandle; 16]>,
    pub(crate) seen_keys: SmallVec<[RolloutDedupKey; 16]>,
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
            playable_buf: SmallVec::new(),
            hand_buf: SmallVec::new(),
            seen_keys: SmallVec::new(),
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

#[inline]
fn lookup_cached_intent_hits(
    intent_cache: &mut PilotIntentCache,
    state: &BattleState,
    content: &dyn ContentDispatcher,
    eh: CreatureHandle,
    ec: &sts2_kernel::state::CreatureInst,
) -> SmallVec<[(Dec, i32); 2]> {
    let Some(m) = &ec.monster else {
        return SmallVec::new();
    };
    let mid = m.monster_id;
    let is_dynamic = matches!(
        mid,
        MonsterId::WaterfallGiant | MonsterId::TestSubject | MonsterId::TheForgotten
    );
    if is_dynamic {
        return content.monster_attack_intent_hits(state, eh).unwrap_or_default();
    }
    let move_cow = m.next_move_id.as_ref().unwrap_or(&m.current_state_id);
    let move_str = move_cow.as_ref();
    if let Some(entry) = intent_cache.entries.iter_mut().find(|e| e.handle == eh) {
        if entry.monster_id == mid && entry.move_id.as_ref() == move_str {
            #[cfg(debug_assertions)]
            {
                let fresh = content
                    .monster_attack_intent_hits(state, eh)
                    .unwrap_or_default();
                debug_assert_eq!(
                    entry.hits, fresh,
                    "PilotIntentCache mismatch for {mid:?} move={move_str}"
                );
            }
            return entry.hits.clone();
        }
        let hits = content
            .monster_attack_intent_hits(state, eh)
            .unwrap_or_default();
        entry.monster_id = mid;
        entry.move_id = move_cow.clone();
        entry.hits = hits.clone();
        return hits;
    }
    let hits = content
        .monster_attack_intent_hits(state, eh)
        .unwrap_or_default();
    intent_cache.entries.push(PilotIntentEntry {
        handle: eh,
        monster_id: mid,
        move_id: move_cow.clone(),
        hits: hits.clone(),
    });
    hits
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

    for &eh in &state.enemy_handles {
        let Some(ec) = state.creature(eh) else {
            continue;
        };
        if !state.contains_creature(eh) {
            continue;
        }
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
            let hits = lookup_cached_intent_hits(intent_cache, state, content, eh, ec);
            for (base_dmg, count) in hits {
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
    hand_buf: &mut SmallVec<[CardHandle; 16]>,
    seen_keys: &mut SmallVec<[RolloutDedupKey; 16]>,
    playable_buf: &mut SmallVec<[(CardHandle, i32, i32); 16]>,
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

    let early_dedup_safe = state.hook_cache.dirty_mask == 0
        && state.hook_cache.cached_tables.is_some()
        && {
            let comb = state.hook_cache.masks.combat_combined;
            !comb.contains(HookSlot::ShouldPlay)
                && !comb.contains(HookSlot::TryModifyEnergyCostInCombat)
                && !comb.contains(HookSlot::TryModifyEnergyCostInCombatLate)
                && !comb.contains(HookSlot::TryModifyStarCostInCombat)
                && !comb.contains(HookSlot::TryModifyKeywordsInCombat)
        };

    for i in 0..hand_buf.len() {
        let ch = hand_buf[i];
        if early_dedup_safe {
            if let Some(card) = state.card(ch) {
                if card.has_local_keyword(CardKeywords::UNPLAYABLE) {
                    continue;
                }
                if let Ok(meta) = state.fast_card_meta(content, card.id) {
                    let is_x_e = meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST);
                    let is_x_s = meta.flags.contains(CardStaticFlags::IS_X_STAR_COST);
                    let local_e = resolve_local_energy_cost(state, ch, is_x_e);
                    if !is_x_e && local_e > state.player.energy {
                        continue;
                    }
                    let local_s = if is_x_s {
                        state.player.stars
                    } else {
                        resolve_local_star_cost(state, ch, meta.canonical_star_cost)
                    };
                    if !is_x_s && local_s > state.player.stars {
                        continue;
                    }
                    if !seen_keys.is_empty() {
                        let key: RolloutDedupKey = (
                            card.id,
                            card.upgrade_level,
                            local_e,
                            local_s,
                            card.ext_index.is_some(),
                            card.affliction.map(|a| a.id),
                        );
                        if seen_keys.contains(&key) {
                            continue;
                        }
                    }
                }
            }
        }
        let Ok(Some((energy_cost, star_cost))) =
            CombatEngine::can_play_card_with_costs_in_progress(state, content, ch)
        else {
            continue;
        };
        let Some(card) = state.card(ch) else {
            continue;
        };
        let key: RolloutDedupKey = (
            card.id,
            card.upgrade_level,
            energy_cost,
            star_cost,
            card.ext_index.is_some(),
            card.affliction.map(|a| a.id),
        );
        if seen_keys.contains(&key) {
            continue;
        }
        seen_keys.push(key);
        playable_buf.push((ch, energy_cost, star_cost));
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
    playable: &[(CardHandle, i32, i32)],
    blocked: &[CardHandle],
    ctx: &FastTurnContext,
    pilot: PilotKind,
) -> Option<(CardHandle, Option<CreatureHandle>)> {
    let mut has_non_retained = false;
    if pilot == PilotKind::SetupFirst {
        for &(ch, _, _) in playable {
            if !blocked.is_empty() && blocked.contains(&ch) {
                continue;
            }
            let Some(card) = state.card(ch) else {
                continue;
            };
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

    for &(ch, raw_e_cost, raw_s_cost) in playable {
        if !blocked.is_empty() && blocked.contains(&ch) {
            continue;
        }
        let Some(card) = state.card(ch) else {
            continue;
        };
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
    playable_buf: &mut SmallVec<[(CardHandle, i32, i32); 16]>,
    hand_buf: &mut SmallVec<[CardHandle; 16]>,
    seen_keys: &mut SmallVec<[RolloutDedupKey; 16]>,
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
    content: &dyn ContentDispatcher,
    root_baseline: &RootEvalBaseline,
    profile: &SolverSearchProfile,
    eval_mode: EvalMode,
    max_turns_ahead: usize,
    optional_paid_potion_cost: i32,
    optional_paid_potion_count: usize,
    parent_action_count: u16,
) -> Option<ChoiceProbeResult> {
    clone_battle_state_into(base_state, &mut ws.probe_state);
    let turn_slots = max_turns_ahead.min(4) + 1;
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
    let terminal = fast.won || fast.player_dead || fast.projected_player_hp <= 0;
    let full = if terminal {
        crate::eval::FullEvalDetails::default()
    } else {
        evaluate_full(&ws.probe_state, content, &fast, root_baseline)
    };
    let net_potion_cost_centi = PotionUsePolicy::compute_net_potion_opportunity_cost_centi(
        optional_paid_potion_cost,
        optional_paid_potion_count as i32,
        fast.alchemize_count as i32,
        fast.empty_potion_slots as i32,
        root_baseline,
    );
    let action_count = parent_action_count.saturating_add(total_steps as u16);
    let score = compute_beam_rank_score_with_centi(
        &fast,
        &full,
        root_baseline,
        profile.base_score_only,
        profile.boss_tempo_hp_pricing,
        profile.beam_weight_perturbation,
        eval_mode,
        net_potion_cost_centi,
        action_count,
    );
    Some(ChoiceProbeResult {
        score,
        hp_lost: fast.cumulative_hp_lost,
        board_investment: full.board_investment,
        is_victory: fast.won,
        is_defeat: fast.player_dead || fast.projected_player_hp <= 0,
    })
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn evaluate_choice_probe_packed(
    ws: &mut ChoiceProbeWorkspace,
    packed_state: &[u8],
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
    if unpack_state(packed_state, static_ctx, &mut ws.probe_state).is_err() {
        return None;
    }
    let turn_slots = max_turns_ahead.min(4) + 1;
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
    let terminal = fast.won || fast.player_dead || fast.projected_player_hp <= 0;
    let full = if terminal {
        crate::eval::FullEvalDetails::default()
    } else {
        evaluate_full(&ws.probe_state, content, &fast, root_baseline)
    };
    let net_potion_cost_centi = PotionUsePolicy::compute_net_potion_opportunity_cost_centi(
        optional_paid_potion_cost,
        optional_paid_potion_count as i32,
        fast.alchemize_count as i32,
        fast.empty_potion_slots as i32,
        root_baseline,
    );
    let action_count = parent_action_count.saturating_add(total_steps as u16);
    let score = compute_beam_rank_score_with_centi(
        &fast,
        &full,
        root_baseline,
        profile.base_score_only,
        profile.boss_tempo_hp_pricing,
        profile.beam_weight_perturbation,
        eval_mode,
        net_potion_cost_centi,
        action_count,
    );
    Some(ChoiceProbeResult {
        score,
        hp_lost: fast.cumulative_hp_lost,
        board_investment: full.board_investment,
        is_victory: fast.won,
        is_defeat: fast.player_dead || fast.projected_player_hp <= 0,
    })
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
    let net_potion_cost_centi = PotionUsePolicy::compute_net_potion_opportunity_cost_centi(
        optional_paid_potion_cost,
        optional_paid_potion_count as i32,
        final_fast.alchemize_count as i32,
        final_fast.empty_potion_slots as i32,
        root_baseline,
    );
    let score = compute_beam_rank_score_with_centi(
        &final_fast,
        &final_full,
        root_baseline,
        profile.base_score_only,
        profile.boss_tempo_hp_pricing,
        profile.beam_weight_perturbation,
        eval_mode,
        net_potion_cost_centi,
        parent_action_count.saturating_add(total_steps as u16),
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
) -> Option<RolloutResult> {
    let pnode = arena.get(pid);
    if pnode.is_terminal() || pnode.state_handle.len == 0 {
        return None;
    }
    let pbytes = state_arena.get(pnode.state_handle);
    if unpack_state(pbytes, root_ctx, &mut ws.base_state).is_err() {
        return None;
    }
    if !ws.base_state.is_in_progress
        || ws.base_state.won
        || ws.base_state.pending.is_some()
        || !ws.base_state.has_living_player_creature()
    {
        return None;
    }

    let can_promote = parent_satisfies_hard_potions(arena, pid, root_baseline, policy);
    let res_a = run_pilot_from_base(
        ws,
        false,
        content,
        root_baseline,
        profile,
        eval_mode,
        horizon_turns,
        pnode.optional_paid_potion_cost as i32,
        pnode.optional_paid_potion_count as usize,
        pnode.action_count,
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
        pnode.optional_paid_potion_cost as i32,
        pnode.optional_paid_potion_count as usize,
        pnode.action_count,
        PilotKind::SetupFirst,
        can_promote,
    );

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
