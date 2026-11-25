use crate::arena::{NodeArena, NodeId};
use crate::eval::{
    ActEndingBossPolicy, BossHpRelief, PostCombatRelicHealProfile, RelicCounterId, RootEvalBaseline,
};
use crate::portfolio::BeamWidthPortfolio;
use hashbrown::HashMap;
use parking_lot::RwLock;
use smallvec::SmallVec;
use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};
use sts2_kernel::state::BattleState;
use sts2_kernel::ContentDispatcher;
use sts2_types::{
    CardId, CardStaticFlags, PileType, PotionId, PowerStaticFlags, RelicId, RelicStaticFlags,
};

const PER_NICHE_PARETO_TOP_K: usize = 32;
const NICHE_SLOT_COUNT: usize = 6; // 0=Primary, 1=Survival, 2=Tempo, 3=BoardInvestment, 4=Optionality, 5=PureRollout

#[derive(Clone, Copy, Debug, Default)]
struct ParetoRow {
    loss: i32,
    enemy_hp: i32,
    rem: i32,
    board: i32,
    opt: i32,
    potion_cost: i32,
    primary: f64,
}

#[inline(always)]
fn pareto_row_dominates(other: &ParetoRow, cand: &ParetoRow) -> bool {
    let worse = u8::from(other.loss > cand.loss)
        | u8::from(other.enemy_hp > cand.enemy_hp)
        | u8::from(other.rem > cand.rem)
        | u8::from(other.board < cand.board)
        | u8::from(other.opt < cand.opt)
        | u8::from(other.potion_cost > cand.potion_cost)
        | u8::from(other.primary < cand.primary);
    if worse != 0 {
        return false;
    }
    (u8::from(other.loss < cand.loss)
        | u8::from(other.enemy_hp < cand.enemy_hp)
        | u8::from(other.rem < cand.rem)
        | u8::from(other.board > cand.board)
        | u8::from(other.opt > cand.opt)
        | u8::from(other.potion_cost < cand.potion_cost)
        | u8::from(other.primary > cand.primary))
        != 0
}

const PRUNE_FLAG_HAS_ROLLOUT: u8 = 1 << 0;
const PRUNE_FLAG_HAS_CHOICE_LA: u8 = 1 << 1;
const PRUNE_FLAG_FAST_EXACT: u8 = 1 << 2;

#[derive(Clone, Copy, Debug)]
#[repr(C)]
pub(crate) struct PruneKey {
    pub primary: f64,
    pub aux_score: f64,
    pub forced_used_mask: u64,
    pub node_id: NodeId,
    pub enemy_hp: i32,
    pub effective_hp_loss: i32,
    pub total_defense_hp: i32,
    pub optionality_score: i32,
    pub used_potion_slot_mask: u16,
    pub strategic_hp_credit: i16,
    pub potion_cost: i16,
    pub cumulative_hp_lost: i16,
    pub best_hp: i16,
    pub board_investment: i16,
    pub setup_score: i16,
    pub rem_enemies: u8,
    pub explicit_potion_count: u8,
    pub forced_match_count: u8,
    pub death_save_use_count: u8,
    pub strategy_goal_count: u8,
    pub flags: u8,
}

const _: () = {
    assert!(std::mem::size_of::<PruneKey>() == 64);
};

impl PruneKey {
    #[inline(always)]
    const fn has_rollout_score(&self) -> bool {
        (self.flags & PRUNE_FLAG_HAS_ROLLOUT) != 0
    }

    #[inline(always)]
    const fn has_choice_lookahead(&self) -> bool {
        (self.flags & PRUNE_FLAG_HAS_CHOICE_LA) != 0
    }

    #[inline(always)]
    const fn fast_keys_exact(&self) -> bool {
        (self.flags & PRUNE_FLAG_FAST_EXACT) != 0
    }

    #[inline(always)]
    #[allow(clippy::too_many_arguments)]
    pub(crate) fn from_raw_fields(
        nid: NodeId,
        root: &RootEvalBaseline,
        use_rollout_scores: bool,
        enemy_hp: i32,
        player_hp: i16,
        player_max_hp: i16,
        player_block: i16,
        projected_player_hp: i16,
        cumulative_hp_lost: i16,
        recovered_hp: i16,
        strategic_hp_credit: i16,
        alive_enemy_count: u8,
        reviving_enemy_count: u8,
        death_save_use_count: u8,
        strategy_goal_count: u8,
        feed_count: u8,
        energy: i16,
        stars: i16,
        forced_used_mask: u64,
        used_potion_slot_mask: u16,
        potion_cost: i16,
        explicit_potion_count: u8,
        has_full_eval: bool,
        beam_rank_score: f64,
        has_rollout_score: bool,
        has_choice_lookahead: bool,
        stored_aux_score: f64,
        effective_defense: i16,
        board_investment_raw: i16,
        setup_score_raw: i16,
        optionality_cards: i16,
    ) -> (Self, i64, i64, i64, i64, f64) {
        let max_hp_delta = root.initial_player_max_hp - (player_max_hp as i32);
        let effective_hp_loss = (cumulative_hp_lost as i32)
            - (recovered_hp as i32)
            + if max_hp_delta < 0 {
                let non_feed = ((-max_hp_delta) - i32::from(feed_count) * root.feed_max_hp_per_kill).max(0);
                -(non_feed * 2)
            } else {
                max_hp_delta
            };
        let best_hp = player_hp.max(projected_player_hp);
        let best_hp_i32 = best_hp as i32;
        let (
            primary,
            aux_score,
            total_defense_hp,
            board_investment,
            setup_score,
            optionality_score,
        ) = if has_full_eval {
            let primary = if use_rollout_scores && has_rollout_score {
                0.35 * beam_rank_score + 0.65 * stored_aux_score
            } else {
                beam_rank_score
            };
            let aux = if has_rollout_score || has_choice_lookahead {
                stored_aux_score
            } else {
                f64::NEG_INFINITY
            };
            (
                primary,
                aux,
                best_hp_i32 + (effective_defense as i32),
                board_investment_raw,
                setup_score_raw,
                (energy as i32).max(0) + (stars as i32).max(0) + (optionality_cards as i32),
            )
        } else {
            (
                beam_rank_score,
                f64::NEG_INFINITY,
                best_hp_i32 + (player_block as i32),
                0,
                0,
                (energy as i32).max(0) + (stars as i32).max(0),
            )
        };
        let death_save_use_count_i32 = death_save_use_count as i32;
        let rem_enemies_i32 = (alive_enemy_count as i32) + (reviving_enemy_count as i32);
        let board_i32 = board_investment as i32;
        let setup_i32 = setup_score as i32;
        let fast_keys_exact = (0..=63).contains(&death_save_use_count_i32)
            && (-4096..=12287).contains(&effective_hp_loss)
            && (-2048..=14335).contains(&total_defense_hp)
            && (-2048..=14335).contains(&best_hp_i32)
            && (0..=16383).contains(&enemy_hp)
            && (0..=63).contains(&rem_enemies_i32)
            && (-524_288..=524_287).contains(&board_i32)
            && (-524_288..=524_287).contains(&setup_i32)
            && (-524_288..=524_287).contains(&optionality_score);
        let (survival_fast, tempo_fast, setup_fast, optionality_fast) = if fast_keys_exact {
            let ds_bits = ((63 - death_save_use_count_i32) as i64) << 56;
            let ehl_surv = ((12287 - effective_hp_loss) as i64) << 42;
            let tdh_bits = ((total_defense_hp + 2048) as i64) << 28;
            let bhp_bits = ((best_hp_i32 + 2048) as i64) << 14;
            let ehp_surv = (16383 - enemy_hp) as i64;
            let s = ds_bits | ehl_surv | tdh_bits | bhp_bits | ehp_surv;

            let rem_bits = ((63 - rem_enemies_i32) as i64) << 42;
            let ehp_tempo = ((1_048_575 - enemy_hp) as i64) << 22;
            let ehl_20 = (524_287 - effective_hp_loss) as i64;
            let t = rem_bits | ehp_tempo | ehl_20;

            let bi_bits = ((board_i32 + 524_288) as i64) << 40;
            let ehl_mid = ehl_20 << 20;
            let ss_bits = (setup_i32 + 524_288) as i64;
            let su = bi_bits | ehl_mid | ss_bits;

            let os_bits = ((optionality_score + 524_288) as i64) << 40;
            let bi_mid = ((board_i32 + 524_288) as i64) << 20;
            let o = os_bits | bi_mid | ehl_20;
            (s, t, su, o)
        } else {
            (0, 0, 0, 0)
        };
        let mut flags = 0u8;
        if has_full_eval && has_rollout_score {
            flags |= PRUNE_FLAG_HAS_ROLLOUT;
        }
        if has_full_eval && has_choice_lookahead {
            flags |= PRUNE_FLAG_HAS_CHOICE_LA;
        }
        if fast_keys_exact {
            flags |= PRUNE_FLAG_FAST_EXACT;
        }
        let aux_f64 = if use_rollout_scores {
            if (flags & PRUNE_FLAG_HAS_ROLLOUT) != 0 {
                aux_score
            } else {
                f64::NEG_INFINITY
            }
        } else if (flags & PRUNE_FLAG_HAS_CHOICE_LA) != 0 {
            aux_score
        } else {
            f64::NEG_INFINITY
        };
        let key = Self {
            primary,
            aux_score,
            forced_used_mask,
            node_id: nid,
            enemy_hp,
            effective_hp_loss,
            total_defense_hp,
            optionality_score,
            used_potion_slot_mask,
            strategic_hp_credit,
            potion_cost,
            cumulative_hp_lost,
            best_hp,
            board_investment,
            setup_score,
            rem_enemies: rem_enemies_i32.clamp(0, u8::MAX as i32) as u8,
            explicit_potion_count,
            forced_match_count: (forced_used_mask & root.all_forced_potion_mask).count_ones()
                as u8,
            death_save_use_count,
            strategy_goal_count,
            flags,
        };
        (
            key,
            survival_fast,
            tempo_fast,
            setup_fast,
            optionality_fast,
            aux_f64,
        )
    }

    #[inline]
    fn from_node(
        arena: &NodeArena,
        nid: NodeId,
        root: &RootEvalBaseline,
        use_rollout_scores: bool,
    ) -> (Self, i64, i64, i64, i64, f64, crate::hash::StateHash) {
        let n = arena.get(nid);
        let f = &n.fast_eval;
        let (k, s, t, su, o, a) = Self::from_raw_fields(
            nid,
            root,
            use_rollout_scores,
            f.enemy_hp,
            f.player_hp,
            f.player_max_hp,
            f.player_block,
            f.projected_player_hp,
            f.cumulative_hp_lost,
            f.recovered_hp,
            f.strategic_hp_credit,
            f.alive_enemy_count,
            f.reviving_enemy_count,
            f.death_save_use_count,
            f.strategy_goal_count,
            f.feed_count,
            f.energy,
            f.stars,
            n.forced_used_mask,
            n.used_potion_slot_mask,
            n.potion_strategic_cost,
            n.explicit_potion_count,
            n.has_full_eval(),
            n.beam_rank_score,
            n.has_rollout_score(),
            n.has_choice_lookahead(),
            n.rollout_score,
            n.effective_defense,
            n.board_investment,
            n.setup_score,
            n.optionality_cards,
        );
        (k, s, t, su, o, a, n.state_hash)
    }

    #[inline]
    fn pareto_row(&self) -> ParetoRow {
        ParetoRow {
            loss: self.effective_hp_loss,
            enemy_hp: self.enemy_hp,
            rem: self.rem_enemies as i32,
            board: self.board_investment as i32,
            opt: self.optionality_score,
            potion_cost: self.potion_cost as i32,
            primary: self.primary,
        }
    }
}

pub(crate) trait PruneSource: Sync {
    fn build_prune_entry(
        &self,
        id: NodeId,
        root: &RootEvalBaseline,
        use_rollout_scores: bool,
    ) -> (PruneKey, i64, i64, i64, i64, f64, crate::hash::StateHash);
    fn potion_count(&self, id: NodeId) -> u8;
    fn explicit_potion_count(&self, id: NodeId) -> u8;
    fn beam_score(&self, id: NodeId, use_rollout_scores: bool) -> f64;
    fn tie_break_fields(&self, id: NodeId) -> (i16, i32, bool, f64, crate::hash::StateHash);
}

impl PruneSource for NodeArena {
    #[inline(always)]
    fn build_prune_entry(
        &self,
        id: NodeId,
        root: &RootEvalBaseline,
        use_rollout_scores: bool,
    ) -> (PruneKey, i64, i64, i64, i64, f64, crate::hash::StateHash) {
        PruneKey::from_node(self, id, root, use_rollout_scores)
    }

    #[inline(always)]
    fn potion_count(&self, id: NodeId) -> u8 {
        self.get(id).potion_count
    }

    #[inline(always)]
    fn explicit_potion_count(&self, id: NodeId) -> u8 {
        self.get(id).explicit_potion_count
    }

    #[inline(always)]
    fn beam_score(&self, id: NodeId, use_rollout_scores: bool) -> f64 {
        let n = self.get(id);
        if use_rollout_scores {
            n.effective_turn_boundary_score()
        } else {
            n.beam_rank_score_or_base()
        }
    }

    #[inline(always)]
    fn tie_break_fields(&self, id: NodeId) -> (i16, i32, bool, f64, crate::hash::StateHash) {
        let n = self.get(id);
        (
            n.fast_eval.cumulative_hp_lost,
            n.fast_eval.enemy_hp,
            n.has_choice_lookahead(),
            n.choice_lookahead_score().unwrap_or(f64::NEG_INFINITY),
            n.state_hash,
        )
    }
}

#[derive(Clone, Debug, Default)]
struct BucketPruneScratch {
    nodes: Vec<NodeId>,
    out: Vec<(f64, NodeId)>,
    marks: Vec<u8>,
    unmarked: Vec<usize>,
    heap: Vec<usize>,
    heap_fast: Vec<(i64, f64, u32)>,
    heap_f64: Vec<(f64, u32)>,
    selected_rows: Vec<ParetoRow>,
    keys: Vec<PruneKey>,
    state_hashes: Vec<crate::hash::StateHash>,
    survival_keys: Vec<i64>,
    tempo_keys: Vec<i64>,
    setup_keys: Vec<i64>,
    optionality_keys: Vec<i64>,
    aux_scores: Vec<f64>,
    order: Vec<(f64, u32)>,
}

#[derive(Clone, Debug, Default)]
pub struct PruneScratch {
    buckets: [BucketPruneScratch; 4],
    ranked: Vec<NodeId>,
    merged: Vec<(f64, NodeId)>,
}

/// Compile-time full-catalog healing/growth closure certification computed in `< 1 µs` from static metadata.
/// Certifies exact zero-heal / bounded-heal upper bounds on any vanilla combat root, powering sound
/// certified-incumbent HP pruning (`is_pruned_by_certified_incumbent`).
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct ClosureCertificate {
    pub healing_certified: bool,
    pub max_in_combat_heal: i32,
    pub post_combat_relic_heal: PostCombatRelicHealProfile,
    pub growth_certified_zero: bool,
    pub max_growth_credit: i32,
}

impl ClosureCertificate {
    pub fn certify(state: &BattleState, content: &dyn ContentDispatcher) -> Self {
        let mut healing_certified = true;
        let mut max_in_combat_heal = 0i32;
        let mut growth_certified_zero = true;

        // Check active relics
        for r in &state.player.relics {
            if r.is_melted {
                continue;
            }
            let Ok(meta) = content.relic_meta(r.id) else {
                healing_certified = false;
                growth_certified_zero = false;
                break;
            };
            if meta.flags.contains(RelicStaticFlags::HAS_GROWTH) {
                growth_certified_zero = false;
            }
            if meta.flags.contains(RelicStaticFlags::MAY_HEAL) {
                // BurningBlood, BlackBlood, MeatOnTheBone only heal post-combat;
                // other MAY_HEAL relics may heal during combat so conservatively uncertify.
                if !matches!(
                    r.id,
                    RelicId::BurningBlood | RelicId::BlackBlood | RelicId::MeatOnTheBone
                ) {
                    healing_certified = false;
                }
            }
        }

        // Check potion slots
        if healing_certified || growth_certified_zero {
            for slot in &state.player.potion_slots {
                let Some(pid) = *slot else { continue };
                let Ok(meta) = content.potion_meta(pid) else {
                    healing_certified = false;
                    growth_certified_zero = false;
                    break;
                };
                if meta.may_heal || pid == PotionId::FruitJuice {
                    healing_certified = false;
                }
                // Card/potion generators can produce healing or growth cards dynamically.
                if matches!(
                    pid,
                    PotionId::EntropicBrew
                        | PotionId::AttackPotion
                        | PotionId::ColorlessPotion
                        | PotionId::DistilledChaos
                ) {
                    healing_certified = false;
                    growth_certified_zero = false;
                }
            }
        }

        // Check active creature powers
        if healing_certified || growth_certified_zero {
            for cr in &state.creatures {
                for pw in &cr.powers {
                    let Ok(meta) = content.power_meta(pw.id) else {
                        healing_certified = false;
                        growth_certified_zero = false;
                        break;
                    };
                    if meta.flags.contains(PowerStaticFlags::MAY_HEAL) {
                        healing_certified = false;
                    }
                    if meta.flags.contains(PowerStaticFlags::HAS_GROWTH) {
                        growth_certified_zero = false;
                    }
                }
            }
        }

        // Check live cards across hand/draw/discard/play
        if healing_certified || growth_certified_zero {
            for card in &state.cards {
                if card.removed_from_state || card.pile == PileType::Exhaust {
                    continue;
                }
                let Ok(meta) = content.card_meta(card.id) else {
                    healing_certified = false;
                    growth_certified_zero = false;
                    break;
                };
                if meta.flags.contains(CardStaticFlags::MAY_HEAL) {
                    // Self-contained non-exhausting heal or unknown upper bound
                    healing_certified = false;
                }
                if meta.flags.contains(CardStaticFlags::HAS_GROWTH) {
                    growth_certified_zero = false;
                }
                // Card generation cards can generate healing/growth cards
                if matches!(
                    card.id,
                    CardId::Alchemize
                        | CardId::CreativeAi
                        | CardId::HelloWorld
                        | CardId::Discovery
                        | CardId::WhiteNoise
                        | CardId::Distraction
                        | CardId::InfernalBlade
                ) {
                    healing_certified = false;
                    growth_certified_zero = false;
                }
            }
        }

        if !healing_certified {
            max_in_combat_heal = i32::MAX;
        }

        Self {
            healing_certified,
            max_in_combat_heal,
            post_combat_relic_heal: PostCombatRelicHealProfile::from_state(state),
            growth_certified_zero,
            max_growth_credit: 0,
        }
    }

    #[inline]
    pub fn max_possible_healing_credit(&self, player_hp: i32, player_max_hp: i32) -> i32 {
        if !self.healing_certified {
            return i32::MAX;
        }
        let headroom = (player_max_hp - player_hp).max(0);
        let post_heal =
            self.post_combat_relic_heal.unconditional_heal.max(0) + self.post_combat_relic_heal.wounded_heal.max(0);
        (self.max_in_combat_heal.saturating_add(post_heal)).min(headroom + post_heal)
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Hash)]
pub struct PrimaryIncumbentBucket {
    pub potions: u16,
    pub forfeits_gold: bool,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct PrimarySearchIncumbent {
    pub strategic_hp_deficit: i32,
    pub combat_ended_turn: i32,
    pub potion_strategic_cost: i32,
}

/// Shared thread-safe incumbent table across waves and portfolio members.
/// Includes an atomic fast-path for zero-potion certified bounds.
pub struct PrimaryIncumbentTable {
    bounds: RwLock<HashMap<PrimaryIncumbentBucket, PrimarySearchIncumbent, ahash::RandomState>>,
    fast_zero_potion_deficit: AtomicI32,
    pub branches_pruned: AtomicI32,
}

impl Default for PrimaryIncumbentTable {
    fn default() -> Self {
        Self::new()
    }
}

impl PrimaryIncumbentTable {
    pub fn new() -> Self {
        Self {
            bounds: RwLock::new(HashMap::with_hasher(ahash::RandomState::with_seeds(
                11, 22, 33, 44,
            ))),
            fast_zero_potion_deficit: AtomicI32::new(i32::MAX),
            branches_pruned: AtomicI32::new(0),
        }
    }

    #[inline]
    pub fn fast_zero_potion_bound(&self) -> i32 {
        self.fast_zero_potion_deficit.load(Ordering::Relaxed)
    }

    pub fn tighten(&self, bucket: PrimaryIncumbentBucket, candidate: PrimarySearchIncumbent) -> bool {
        let mut w = self.bounds.write();
        if let Some(cur) = w.get(&bucket) {
            if candidate.strategic_hp_deficit > cur.strategic_hp_deficit
                || (candidate.strategic_hp_deficit == cur.strategic_hp_deficit
                    && candidate.combat_ended_turn >= cur.combat_ended_turn)
            {
                return false;
            }
        }
        w.insert(bucket, candidate);
        if bucket.potions == 0 && !bucket.forfeits_gold {
            self.fast_zero_potion_deficit
                .fetch_min(candidate.strategic_hp_deficit, Ordering::Relaxed);
        }
        true
    }

    pub fn try_get(&self, bucket: PrimaryIncumbentBucket) -> Option<PrimarySearchIncumbent> {
        self.bounds.read().get(&bucket).copied()
    }
}

/// Wave-frozen certified centi-HP incumbent bound matching `HotIncumbentBound` (`HotEvaluator.cs`).
/// Snapshotted once before each wave so DOP=1 and DOP=N make identical pruning decisions while
/// preserving `(V, NetPersistentHpDeltaCenti, Turn)` tie-break priority.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct FrozenIncumbentBound {
    pub has_certified_zero_potion_bound: bool,
    pub best_zero_potion_hp_delta_centi: i32,
    pub best_zero_potion_end_turn: i32,
    pub has_certified_overall_bound: bool,
    pub best_overall_value_centi: i64,
    pub best_overall_hp_delta_centi: i32,
    pub best_overall_end_turn: i32,
}

impl FrozenIncumbentBound {
    #[allow(clippy::too_many_arguments)]
    #[inline]
    pub fn tighten(
        &mut self,
        root: &RootEvalBaseline,
        won: bool,
        death_save_use_count: i32,
        net_hp_delta_centi: i32,
        net_potion_cost_centi: i32,
        optional_paid_count: usize,
        explicit_potion_count: usize,
        end_turn: i32,
        outstanding_stolen: i32,
        forfeits_gold: bool,
        theft_preserve_resources: bool,
    ) -> bool {
        if !won
            || root.has_any_heal_or_unknown_generation
            || root.effective_has_growth_targets
            || !root.effective_growth_budgets.is_zero()
            || !root.active_relic_targets.is_empty()
            || root.all_forced_potion_mask != 0
            || (theft_preserve_resources && (outstanding_stolen > 0 || forfeits_gold))
            || death_save_use_count > 0
        {
            return false;
        }
        let mut changed = false;
        if optional_paid_count == 0 && explicit_potion_count == 0 {
            if !self.has_certified_zero_potion_bound
                || net_hp_delta_centi > self.best_zero_potion_hp_delta_centi
                || (net_hp_delta_centi == self.best_zero_potion_hp_delta_centi
                    && end_turn < self.best_zero_potion_end_turn)
            {
                self.has_certified_zero_potion_bound = true;
                self.best_zero_potion_hp_delta_centi = net_hp_delta_centi;
                self.best_zero_potion_end_turn = end_turn;
                changed = true;
            }
        }
        let v_centi = (net_hp_delta_centi as i64) - (net_potion_cost_centi as i64);
        if !self.has_certified_overall_bound
            || v_centi > self.best_overall_value_centi
            || (v_centi == self.best_overall_value_centi
                && net_hp_delta_centi > self.best_overall_hp_delta_centi)
            || (v_centi == self.best_overall_value_centi
                && net_hp_delta_centi == self.best_overall_hp_delta_centi
                && end_turn < self.best_overall_end_turn)
        {
            self.has_certified_overall_bound = true;
            self.best_overall_value_centi = v_centi;
            self.best_overall_hp_delta_centi = net_hp_delta_centi;
            self.best_overall_end_turn = end_turn;
            changed = true;
        }
        changed
    }

    #[allow(clippy::too_many_arguments)]
    #[inline]
    pub fn is_pruned(
        &self,
        cert: &ClosureCertificate,
        root: &RootEvalBaseline,
        player_hp: i32,
        player_max_hp: i32,
        death_save_hp_restored: i32,
        death_save_use_count: i32,
        feed_count: u8,
        won: bool,
        player_dead: bool,
        turn: i32,
        optional_paid_count: usize,
        explicit_potion_count: usize,
        net_potion_cost_centi: i32,
        theft_preserve_resources: bool,
    ) -> bool {
        if !cert.healing_certified
            || !cert.growth_certified_zero
            || root.has_any_heal_or_unknown_generation
            || root.effective_has_growth_targets
            || !root.effective_growth_budgets.is_zero()
            || !root.active_relic_targets.is_empty()
            || root.all_forced_potion_mask != 0
            || theft_preserve_resources
            || player_dead
        {
            return false;
        }
        let max_post_combat_heal = root
            .post_combat_relic_heal
            .monotone_heal_for(player_hp, player_max_hp);
        let upper_bound_hp_delta_centi = ActEndingBossPolicy::compute_net_persistent_hp_delta_centi(
            player_hp,
            max_post_combat_heal,
            root.initial_player_hp,
            player_max_hp,
            root.initial_player_max_hp,
            death_save_hp_restored,
            0,
            root.boss_hp_relief,
            root.ignore_long_term_rewards,
            i32::from(feed_count) * root.feed_max_hp_per_kill,
        );

        if optional_paid_count == 0
            && explicit_potion_count == 0
            && self.has_certified_zero_potion_bound
        {
            if death_save_use_count > 0 {
                return true;
            }
            if upper_bound_hp_delta_centi < self.best_zero_potion_hp_delta_centi {
                return true;
            }
            if upper_bound_hp_delta_centi == self.best_zero_potion_hp_delta_centi
                && !won
                && turn > self.best_zero_potion_end_turn
            {
                return true;
            }
        }

        if self.has_certified_overall_bound {
            if death_save_use_count > 0 {
                return true;
            }
            let upper_bound_value_centi =
                (upper_bound_hp_delta_centi as i64) - (net_potion_cost_centi as i64);
            if upper_bound_value_centi < self.best_overall_value_centi {
                return true;
            }
            if upper_bound_value_centi == self.best_overall_value_centi {
                if upper_bound_hp_delta_centi < self.best_overall_hp_delta_centi {
                    return true;
                }
                if upper_bound_hp_delta_centi == self.best_overall_hp_delta_centi
                    && !won
                    && turn > self.best_overall_end_turn
                {
                    return true;
                }
            }
        }

        false
    }
}

/// Sound certified-incumbent HP lower-bound pruning.
/// Returns `true` when `node` cannot possibly beat the already witnessed victory under `ClosureCertificate`.
#[allow(clippy::too_many_arguments)]
#[inline]
pub fn is_pruned_by_certified_incumbent(
    cert: &ClosureCertificate,
    root: &RootEvalBaseline,
    table: &PrimaryIncumbentTable,
    cumulative_hp_lost: i32,
    player_hp: i32,
    player_max_hp: i32,
    initial_player_max_hp: i32,
    death_save_hp_restored: i32,
    death_save_use_count: i32,
    potions_used: u16,
    turn: i32,
    relief: BossHpRelief,
) -> bool {
    if !cert.healing_certified
        || !cert.growth_certified_zero
        || death_save_use_count > 0
        || root.all_forced_potion_mask != 0
        || root.effective_has_growth_targets
        || !root.effective_growth_budgets.is_zero()
    {
        return false;
    }
    let has_relic_targets = !root.active_relic_targets.is_empty();
    if has_relic_targets
        && root
            .active_relic_targets
            .iter()
            .any(|t| t.hp_allowance > 0 || t.id == RelicCounterId::MeatOnTheBone)
    {
        return false;
    }
    let max_heal = cert.max_possible_healing_credit(player_hp, player_max_hp);
    if max_heal == i32::MAX {
        return false;
    }
    let max_hp_deficit = (initial_player_max_hp - player_max_hp).max(0);
    let hp_lower_bound = ActEndingBossPolicy::strategic_hp_deficit(
        cumulative_hp_lost,
        max_hp_deficit,
        max_heal,
        relief,
        death_save_hp_restored,
    );

    if potions_used == 0 && !has_relic_targets {
        let bound = table.fast_zero_potion_bound();
        if bound < i32::MAX && hp_lower_bound > bound {
            table.branches_pruned.fetch_add(1, Ordering::Relaxed);
            return true;
        }
    }

    if let Some(inc) = table.try_get(PrimaryIncumbentBucket {
        potions: potions_used,
        forfeits_gold: false,
    }) {
        if hp_lower_bound > inc.strategic_hp_deficit
            || (!has_relic_targets
                && hp_lower_bound == inc.strategic_hp_deficit
                && turn > inc.combat_ended_turn)
        {
            table.branches_pruned.fetch_add(1, Ordering::Relaxed);
            return true;
        }
    }
    false
}

/// Multi-lane Beam Retention Policy matching `CombatBeamSolver.BeamRetentionPolicy.cs` + `.Ranking.cs`.
pub struct BeamRetentionPolicy;

impl BeamRetentionPolicy {
    #[inline]
    pub fn feasible_potion_use_quotas(limit: usize) -> (usize, usize) {
        if limit < 4 {
            (1, limit.saturating_sub(1))
        } else {
            let used = (limit / 3).max(2);
            (used, limit.saturating_sub(used))
        }
    }

    #[allow(clippy::too_many_arguments)]
    pub fn prune_wave(
        arena: &mut NodeArena,
        candidates: &[NodeId],
        beam_width: usize,
        potion_free_beam_width: Option<usize>,
        potion_beam_width: Option<usize>,
        root: &RootEvalBaseline,
        second_rank_band: bool,
        aggressive_power_commitment: bool,
    ) -> Vec<NodeId> {
        Self::prune_wave_with_potion_bias(
            arena,
            candidates,
            beam_width,
            potion_free_beam_width,
            potion_beam_width,
            root,
            second_rank_band,
            aggressive_power_commitment,
            false,
        )
    }

    #[allow(clippy::too_many_arguments)]
    pub fn prune_wave_with_potion_bias(
        arena: &mut NodeArena,
        candidates: &[NodeId],
        beam_width: usize,
        potion_free_beam_width: Option<usize>,
        potion_beam_width: Option<usize>,
        root: &RootEvalBaseline,
        second_rank_band: bool,
        aggressive_power_commitment: bool,
        prefer_potion_buckets: bool,
    ) -> Vec<NodeId> {
        Self::prune_wave_full(
            arena,
            candidates,
            beam_width,
            potion_free_beam_width,
            potion_beam_width,
            root,
            second_rank_band,
            aggressive_power_commitment,
            prefer_potion_buckets,
            false,
            false,
        )
    }

    #[allow(clippy::too_many_arguments)]
    pub fn prune_wave_with_potion_bias_into(
        arena: &NodeArena,
        candidates: &[NodeId],
        beam_width: usize,
        potion_free_beam_width: Option<usize>,
        potion_beam_width: Option<usize>,
        root: &RootEvalBaseline,
        second_rank_band: bool,
        aggressive_power_commitment: bool,
        prefer_potion_buckets: bool,
        pool: Option<&rayon::ThreadPool>,
        out: &mut Vec<NodeId>,
        scratch: &mut PruneScratch,
    ) {
        Self::prune_wave_full_into(
            arena,
            candidates,
            beam_width,
            potion_free_beam_width,
            potion_beam_width,
            root,
            second_rank_band,
            aggressive_power_commitment,
            prefer_potion_buckets,
            false,
            false,
            pool,
            out,
            scratch,
        );
    }

    #[allow(clippy::too_many_arguments)]
    pub fn prune_wave_with_rollout(
        arena: &mut NodeArena,
        candidates: &[NodeId],
        beam_width: usize,
        potion_free_beam_width: Option<usize>,
        potion_beam_width: Option<usize>,
        root: &RootEvalBaseline,
        second_rank_band: bool,
        aggressive_power_commitment: bool,
        prefer_potion_buckets: bool,
        use_rollout_scores: bool,
    ) -> Vec<NodeId> {
        Self::prune_wave_full(
            arena,
            candidates,
            beam_width,
            potion_free_beam_width,
            potion_beam_width,
            root,
            second_rank_band,
            aggressive_power_commitment,
            prefer_potion_buckets,
            true,
            use_rollout_scores,
        )
    }

    #[allow(clippy::too_many_arguments)]
    pub fn prune_wave_with_rollout_into(
        arena: &NodeArena,
        candidates: &[NodeId],
        beam_width: usize,
        potion_free_beam_width: Option<usize>,
        potion_beam_width: Option<usize>,
        root: &RootEvalBaseline,
        second_rank_band: bool,
        aggressive_power_commitment: bool,
        prefer_potion_buckets: bool,
        use_rollout_scores: bool,
        pool: Option<&rayon::ThreadPool>,
        out: &mut Vec<NodeId>,
        scratch: &mut PruneScratch,
    ) {
        Self::prune_wave_full_into(
            arena,
            candidates,
            beam_width,
            potion_free_beam_width,
            potion_beam_width,
            root,
            second_rank_band,
            aggressive_power_commitment,
            prefer_potion_buckets,
            true,
            use_rollout_scores,
            pool,
            out,
            scratch,
        );
    }

    #[allow(clippy::too_many_arguments)]
    pub fn prune_wave_full(
        arena: &mut NodeArena,
        candidates: &[NodeId],
        beam_width: usize,
        potion_free_beam_width: Option<usize>,
        potion_beam_width: Option<usize>,
        root: &RootEvalBaseline,
        second_rank_band: bool,
        aggressive_power_commitment: bool,
        prefer_potion_buckets: bool,
        is_turn_boundary: bool,
        use_rollout_scores: bool,
    ) -> Vec<NodeId> {
        let mut scratch = PruneScratch::default();
        let mut out = Vec::new();
        Self::prune_wave_full_into(
            arena,
            candidates,
            beam_width,
            potion_free_beam_width,
            potion_beam_width,
            root,
            second_rank_band,
            aggressive_power_commitment,
            prefer_potion_buckets,
            is_turn_boundary,
            use_rollout_scores,
            None,
            &mut out,
            &mut scratch,
        );
        out
    }

    #[allow(clippy::too_many_arguments)]
    pub fn prune_wave_full_into(
        arena: &NodeArena,
        candidates: &[NodeId],
        beam_width: usize,
        potion_free_beam_width: Option<usize>,
        potion_beam_width: Option<usize>,
        root: &RootEvalBaseline,
        second_rank_band: bool,
        aggressive_power_commitment: bool,
        prefer_potion_buckets: bool,
        is_turn_boundary: bool,
        use_rollout_scores: bool,
        pool: Option<&rayon::ThreadPool>,
        selected: &mut Vec<NodeId>,
        scratch: &mut PruneScratch,
    ) {
        Self::prune_with_source_into(
            arena,
            candidates,
            beam_width,
            potion_free_beam_width,
            potion_beam_width,
            root,
            second_rank_band,
            aggressive_power_commitment,
            prefer_potion_buckets,
            is_turn_boundary,
            use_rollout_scores,
            pool,
            selected,
            scratch,
        );
    }

    #[allow(clippy::too_many_arguments)]
    pub(crate) fn prune_with_source_into<S: PruneSource>(
        source: &S,
        candidates: &[NodeId],
        beam_width: usize,
        potion_free_beam_width: Option<usize>,
        potion_beam_width: Option<usize>,
        root: &RootEvalBaseline,
        second_rank_band: bool,
        aggressive_power_commitment: bool,
        prefer_potion_buckets: bool,
        is_turn_boundary: bool,
        use_rollout_scores: bool,
        pool: Option<&rayon::ThreadPool>,
        selected: &mut Vec<NodeId>,
        scratch: &mut PruneScratch,
    ) {
        selected.clear();
        if candidates.is_empty() || beam_width == 0 {
            return;
        }
        let has_explicit_split = potion_free_beam_width.is_some() || potion_beam_width.is_some();
        if candidates.len() <= beam_width && !has_explicit_split {
            selected.extend_from_slice(candidates);
            selected.sort_unstable_by(|&a, &b| cmp_primary_desc(source, a, b, use_rollout_scores));
            return;
        }

        let PruneScratch {
            buckets,
            ranked,
            merged,
        } = scratch;

        if has_explicit_split {
            ranked.clear();
            ranked.extend_from_slice(candidates);
            ranked.sort_unstable_by(|&a, &b| cmp_primary_desc(source, a, b, use_rollout_scores));
            let free_quota = potion_free_beam_width.unwrap_or(beam_width).min(beam_width);
            let used_quota = potion_beam_width.unwrap_or(beam_width).min(beam_width);
            selected.reserve(beam_width);
            let mut free_taken = 0usize;
            let mut used_taken = 0usize;
            for &nid in ranked.iter() {
                if selected.len() >= beam_width {
                    break;
                }
                if source.explicit_potion_count(nid) == 0 {
                    if free_taken < free_quota {
                        selected.push(nid);
                        free_taken += 1;
                    }
                } else if used_taken < used_quota {
                    selected.push(nid);
                    used_taken += 1;
                }
            }
            return;
        }

        // Single-pass 4-bucket partitioning by potion count BEFORE per-bucket sorting.
        let mut counts = [0usize; 4];
        for b in 0..4 {
            buckets[b].nodes.clear();
            buckets[b].out.clear();
        }
        for &nid in candidates {
            let b = (source.potion_count(nid) as usize).min(3);
            counts[b] += 1;
            buckets[b].nodes.push(nid);
        }
        let bucket_quotas =
            allocate_potion_bucket_quotas(counts, beam_width, prefer_potion_buckets);
        let active_buckets = (0..4)
            .filter(|&b| !buckets[b].nodes.is_empty() && bucket_quotas[b] > 0)
            .count();

        if let Some(pool) = pool.filter(|_| active_buckets >= 2 && candidates.len() >= 128) {
            let largest_b = (0..4)
                .filter(|&b| !buckets[b].nodes.is_empty() && bucket_quotas[b] > 0)
                .max_by_key(|&b| buckets[b].nodes.len())
                .unwrap_or(0);
            pool.scope(|scope| {
                let mut largest_bucket: Option<(usize, &mut BucketPruneScratch)> = None;
                for (b, bucket) in buckets.iter_mut().enumerate() {
                    let quota = bucket_quotas[b];
                    if bucket.nodes.is_empty() || quota == 0 {
                        continue;
                    }
                    if b == largest_b {
                        largest_bucket = Some((quota, bucket));
                        continue;
                    }
                    scope.spawn(move |_| {
                        select_from_potion_bucket_scratch(
                            source,
                            bucket,
                            quota,
                            b,
                            root,
                            second_rank_band,
                            aggressive_power_commitment,
                            prefer_potion_buckets,
                            is_turn_boundary,
                            use_rollout_scores,
                            Some(pool),
                        );
                    });
                }
                if let Some((quota, bucket)) = largest_bucket {
                    select_from_potion_bucket_scratch(
                        source,
                        bucket,
                        quota,
                        largest_b,
                        root,
                        second_rank_band,
                        aggressive_power_commitment,
                        prefer_potion_buckets,
                        is_turn_boundary,
                        use_rollout_scores,
                        Some(pool),
                    );
                }
            });
        } else {
            for (b, bucket) in buckets.iter_mut().enumerate() {
                let quota = bucket_quotas[b];
                if bucket.nodes.is_empty() || quota == 0 {
                    continue;
                }
                let single_pool = if active_buckets == 1 { pool } else { None };
                select_from_potion_bucket_scratch(
                    source,
                    bucket,
                    quota,
                    b,
                    root,
                    second_rank_band,
                    aggressive_power_commitment,
                    prefer_potion_buckets,
                    is_turn_boundary,
                    use_rollout_scores,
                    single_pool,
                );
            }
        }

        merged.clear();
        merged.reserve(beam_width);
        for bucket in buckets.iter() {
            merged.extend_from_slice(&bucket.out);
        }
        // Deterministic canonical order across buckets so subsequent waves are DOP-invariant.
        merged.sort_unstable_by(|&(sa, a), &(sb, b)| {
            sb.total_cmp(&sa)
                .then_with(|| tie_break_deterministic(source, a, b))
        });
        selected.reserve(merged.len());
        for &(_score, nid) in merged.iter() {
            #[cfg(debug_assertions)]
            debug_assert_eq!(
                _score.to_bits(),
                source.beam_score(nid, use_rollout_scores).to_bits()
            );
            selected.push(nid);
        }
    }
}

#[inline]
fn node_beam_score<S: PruneSource>(source: &S, id: NodeId, use_rollout_scores: bool) -> f64 {
    source.beam_score(id, use_rollout_scores)
}

#[inline]
fn tie_break_deterministic<S: PruneSource>(source: &S, a: NodeId, b: NodeId) -> std::cmp::Ordering {
    let (a_chl, a_ehp, a_hcla, a_cla, a_hash) = source.tie_break_fields(a);
    let (b_chl, b_ehp, b_hcla, b_cla, b_hash) = source.tie_break_fields(b);
    a_chl
        .cmp(&b_chl)
        .then_with(|| a_ehp.cmp(&b_ehp))
        .then_with(|| {
            b_hcla.cmp(&a_hcla).then_with(|| {
                if a_hcla && b_hcla {
                    b_cla.total_cmp(&a_cla)
                } else {
                    std::cmp::Ordering::Equal
                }
            })
        })
        .then_with(|| a_hash.cmp(&b_hash))
        .then_with(|| a.0.cmp(&b.0))
}

#[inline(always)]
fn cmp_key_tie_break(
    keys: &[PruneKey],
    state_hashes: &[crate::hash::StateHash],
    ia: usize,
    ib: usize,
) -> std::cmp::Ordering {
    let a = &keys[ia];
    let b = &keys[ib];
    a.cumulative_hp_lost
        .cmp(&b.cumulative_hp_lost)
        .then_with(|| a.enemy_hp.cmp(&b.enemy_hp))
        .then_with(|| {
            let ha = a.has_choice_lookahead();
            let hb = b.has_choice_lookahead();
            hb.cmp(&ha).then_with(|| {
                if ha && hb {
                    b.aux_score.total_cmp(&a.aux_score)
                } else {
                    std::cmp::Ordering::Equal
                }
            })
        })
        .then_with(|| state_hashes[ia].cmp(&state_hashes[ib]))
        .then_with(|| a.node_id.0.cmp(&b.node_id.0))
}

#[inline(always)]
fn cmp_key_primary_desc(
    keys: &[PruneKey],
    state_hashes: &[crate::hash::StateHash],
    ia: usize,
    ib: usize,
) -> std::cmp::Ordering {
    keys[ib]
        .primary
        .total_cmp(&keys[ia].primary)
        .then_with(|| cmp_key_tie_break(keys, state_hashes, ia, ib))
}

#[inline(always)]
fn cmp_key_primary_max(
    keys: &[PruneKey],
    state_hashes: &[crate::hash::StateHash],
    ia: usize,
    ib: usize,
) -> std::cmp::Ordering {
    keys[ia]
        .primary
        .total_cmp(&keys[ib].primary)
        .then_with(|| cmp_key_tie_break(keys, state_hashes, ib, ia))
}

#[inline]
fn compute_niche_quotas(
    rem: usize,
    is_turn_boundary: bool,
    aggressive_power_commitment: bool,
) -> (usize, usize, usize, usize, usize, usize) {
    match rem {
        0 => (0, 0, 0, 0, 0, 0),
        1 => (1, 0, 0, 0, 0, 0),
        2 => (1, 1, 0, 0, 0, 0),
        3 => (1, 1, 0, 1, 0, 0),
        4 => (1, 1, 1, 1, 0, 0),
        5 => (1, 1, 1, 1, 0, 1),
        _ => {
            let survival = ((rem * 18 + 50) / 100).max(1);
            let tempo = ((rem * 15 + 50) / 100).max(1);
            let setup_pct = if aggressive_power_commitment { 24 } else { 18 };
            let setup =
                ((rem * setup_pct + 50) / 100).max(if aggressive_power_commitment { 2 } else { 1 });
            let niche5_total = ((rem * 14 + 50) / 100).max(1);
            let optionality = if niche5_total >= 2 {
                if is_turn_boundary {
                    ((niche5_total * 35 + 50) / 100).max(1)
                } else {
                    (niche5_total / 2).max(1)
                }
            } else if is_turn_boundary {
                0
            } else {
                1
            };
            let pareto = niche5_total.saturating_sub(optionality);
            let non_primary = survival + tempo + setup + niche5_total;
            if non_primary < rem {
                (rem - non_primary, survival, tempo, setup, optionality, pareto)
            } else {
                let p = 1;
                let mut rest = rem - 1;
                let s = rest.min(survival).max(usize::from(rest > 0));
                rest -= s;
                let su = rest.min(setup);
                rest -= su;
                let t = rest.min(tempo);
                rest -= t;
                let o = rest.min(optionality);
                rest -= o;
                let pa = rest.min(pareto);
                (p, s, t, su, o, pa)
            }
        }
    }
}

#[inline]
fn cmp_primary_desc<S: PruneSource>(
    source: &S,
    a: NodeId,
    b: NodeId,
    use_rollout_scores: bool,
) -> std::cmp::Ordering {
    let sa = node_beam_score(source, a, use_rollout_scores);
    let sb = node_beam_score(source, b, use_rollout_scores);
    sb.total_cmp(&sa)
        .then_with(|| tie_break_deterministic(source, a, b))
}

fn allocate_potion_bucket_quotas(
    counts: [usize; 4],
    total_quota: usize,
    prefer_potion_buckets: bool,
) -> [usize; 4] {
    let mut quotas = [0usize; 4];
    let active_buckets = counts.iter().filter(|&&c| c > 0).count();
    if active_buckets == 0 || total_quota == 0 {
        return quotas;
    }
    if active_buckets == 1 {
        for b in 0..4 {
            if counts[b] > 0 {
                quotas[b] = counts[b].min(total_quota);
                return quotas;
            }
        }
    }

    let mut remaining = total_quota;
    if counts[0] > 0 {
        let zero_pct = if prefer_potion_buckets { 35 } else { 60 };
        let zero_target = ((total_quota * zero_pct) / 100).max(1);
        let take0 = counts[0].min(zero_target);
        quotas[0] = take0;
        remaining -= take0;
    }

    let mut other_buckets = counts[1..4].iter().filter(|&&c| c > 0).count();
    if other_buckets > 0 && remaining > 0 {
        if prefer_potion_buckets && counts[1] > 0 && other_buckets > 1 {
            let one_target = ((remaining * 55) / 100).max(1);
            let take1 = counts[1].min(one_target);
            quotas[1] = take1;
            remaining -= take1;
            other_buckets -= 1;
        }
        if other_buckets > 0 && remaining > 0 {
            let share = (remaining / other_buckets).max(1);
            for b in 1..4 {
                if counts[b] == 0 || quotas[b] > 0 || remaining == 0 {
                    continue;
                }
                let take = counts[b].min(share.min(remaining));
                quotas[b] = take;
                remaining -= take;
            }
        }
    }

    let start = if prefer_potion_buckets { 1 } else { 0 };
    while remaining > 0 {
        let mut progressed = false;
        for step in 0..4 {
            if remaining == 0 {
                break;
            }
            let b = (start + step) % 4;
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
    quotas
}

#[allow(clippy::too_many_arguments)]
fn select_from_potion_bucket_scratch<S: PruneSource>(
    source: &S,
    bucket: &mut BucketPruneScratch,
    quota: usize,
    bucket_index: usize,
    root: &RootEvalBaseline,
    second_rank_band: bool,
    aggressive_power_commitment: bool,
    prefer_potion_buckets: bool,
    is_turn_boundary: bool,
    use_rollout_scores: bool,
    pool: Option<&rayon::ThreadPool>,
) {
    let BucketPruneScratch {
        nodes: bucket_nodes,
        out,
        marks,
        unmarked,
        heap,
        heap_fast,
        heap_f64,
        selected_rows,
        keys,
        state_hashes,
        survival_keys,
        tempo_keys,
        setup_keys,
        optionality_keys,
        aux_scores,
        order,
    } = bucket;
    out.clear();
    let count = bucket_nodes.len();
    if count <= quota {
        out.reserve(count);
        for &nid in bucket_nodes.iter() {
            out.push((source.beam_score(nid, use_rollout_scores), nid));
        }
        return;
    }

    keys.clear();
    state_hashes.clear();
    survival_keys.clear();
    tempo_keys.clear();
    setup_keys.clear();
    optionality_keys.clear();
    aux_scores.clear();
    order.clear();
    keys.reserve(count);
    state_hashes.reserve(count);
    survival_keys.reserve(count);
    tempo_keys.reserve(count);
    setup_keys.reserve(count);
    optionality_keys.reserve(count);
    aux_scores.reserve(count);
    order.reserve(count);

    let check_sig = bucket_index >= 1 && quota >= 6;
    let sig0 = if check_sig {
        let (k0, ..) = source.build_prune_entry(bucket_nodes[0], root, use_rollout_scores);
        (
            k0.used_potion_slot_mask,
            k0.potion_cost,
            k0.explicit_potion_count,
            k0.forced_used_mask,
        )
    } else {
        (0, 0, 0, 0)
    };

    let (all_fast_exact, all_same_sig) = if let Some(pool) = pool.filter(|_| count >= 1024) {
        let k_uninit = &mut keys.spare_capacity_mut()[..count];
        let sh_uninit = &mut state_hashes.spare_capacity_mut()[..count];
        let s_uninit = &mut survival_keys.spare_capacity_mut()[..count];
        let t_uninit = &mut tempo_keys.spare_capacity_mut()[..count];
        let su_uninit = &mut setup_keys.spare_capacity_mut()[..count];
        let o_uninit = &mut optionality_keys.spare_capacity_mut()[..count];
        let a_uninit = &mut aux_scores.spare_capacity_mut()[..count];
        let ord_uninit = &mut order.spare_capacity_mut()[..count];
        let fast_exact_flag = AtomicBool::new(true);
        let fast_exact_ref = &fast_exact_flag;
        let same_sig_flag = AtomicBool::new(true);
        let same_sig_ref = &same_sig_flag;
        let chunk_len = ((count + 15) / 16).max(64);
        let mut chunk_iter = k_uninit
            .chunks_mut(chunk_len)
            .zip(sh_uninit.chunks_mut(chunk_len))
            .zip(
                s_uninit
                    .chunks_mut(chunk_len)
                    .zip(t_uninit.chunks_mut(chunk_len)),
            )
            .zip(
                su_uninit
                    .chunks_mut(chunk_len)
                    .zip(o_uninit.chunks_mut(chunk_len)),
            )
            .zip(
                a_uninit
                    .chunks_mut(chunk_len)
                    .zip(ord_uninit.chunks_mut(chunk_len)),
            )
            .zip(bucket_nodes.chunks(chunk_len));
        let chunk0 = chunk_iter.next();
        pool.scope(|scope| {
            let mut offset = chunk0.as_ref().map_or(0, |(_, n)| n.len());
            for (
                ((((k_chunk, sh_chunk), (s_chunk, t_chunk)), (su_chunk, o_chunk)), (a_chunk, ord_chunk)),
                n_chunk,
            ) in chunk_iter
            {
                let start = offset;
                offset += k_chunk.len();
                scope.spawn(move |_| {
                    let mut local_fast = true;
                    let mut local_same_sig = true;
                    for j in 0..k_chunk.len() {
                        let (k, s, t, su, o, a, sh) =
                            source.build_prune_entry(n_chunk[j], root, use_rollout_scores);
                        local_fast &= (k.flags & PRUNE_FLAG_FAST_EXACT) != 0;
                        if check_sig {
                            let sig = (
                                k.used_potion_slot_mask,
                                k.potion_cost,
                                k.explicit_potion_count,
                                k.forced_used_mask,
                            );
                            local_same_sig &= sig == sig0;
                        }
                        let ord_val = (k.primary, (start + j) as u32);
                        k_chunk[j].write(k);
                        sh_chunk[j].write(sh);
                        s_chunk[j].write(s);
                        t_chunk[j].write(t);
                        su_chunk[j].write(su);
                        o_chunk[j].write(o);
                        a_chunk[j].write(a);
                        ord_chunk[j].write(ord_val);
                    }
                    if !local_fast {
                        fast_exact_ref.store(false, Ordering::Relaxed);
                    }
                    if check_sig && !local_same_sig {
                        same_sig_ref.store(false, Ordering::Relaxed);
                    }
                });
            }
            if let Some((
                ((((k_chunk, sh_chunk), (s_chunk, t_chunk)), (su_chunk, o_chunk)), (a_chunk, ord_chunk)),
                n_chunk,
            )) = chunk0
            {
                let mut local_fast = true;
                let mut local_same_sig = true;
                for j in 0..k_chunk.len() {
                    let (k, s, t, su, o, a, sh) =
                        source.build_prune_entry(n_chunk[j], root, use_rollout_scores);
                    local_fast &= (k.flags & PRUNE_FLAG_FAST_EXACT) != 0;
                    if check_sig {
                        let sig = (
                            k.used_potion_slot_mask,
                            k.potion_cost,
                            k.explicit_potion_count,
                            k.forced_used_mask,
                        );
                        local_same_sig &= sig == sig0;
                    }
                    let ord_val = (k.primary, j as u32);
                    k_chunk[j].write(k);
                    sh_chunk[j].write(sh);
                    s_chunk[j].write(s);
                    t_chunk[j].write(t);
                    su_chunk[j].write(su);
                    o_chunk[j].write(o);
                    a_chunk[j].write(a);
                    ord_chunk[j].write(ord_val);
                }
                if !local_fast {
                    fast_exact_ref.store(false, Ordering::Relaxed);
                }
                if check_sig && !local_same_sig {
                    same_sig_ref.store(false, Ordering::Relaxed);
                }
            }
        });
        // SAFETY: Disjoint chunks initialized all `0..count` elements inside `pool.scope`.
        unsafe {
            keys.set_len(count);
            state_hashes.set_len(count);
            survival_keys.set_len(count);
            tempo_keys.set_len(count);
            setup_keys.set_len(count);
            optionality_keys.set_len(count);
            aux_scores.set_len(count);
            order.set_len(count);
        }
        (
            fast_exact_flag.load(Ordering::Relaxed),
            check_sig && same_sig_flag.load(Ordering::Relaxed),
        )
    } else {
        let mut all_fast = true;
        let mut same_sig = check_sig;
        for (i, &nid) in bucket_nodes.iter().enumerate() {
            let (k, s, t, su, o, a, sh) = source.build_prune_entry(nid, root, use_rollout_scores);
            all_fast &= k.fast_keys_exact();
            if same_sig {
                let sig = (
                    k.used_potion_slot_mask,
                    k.potion_cost,
                    k.explicit_potion_count,
                    k.forced_used_mask,
                );
                same_sig &= sig == sig0;
            }
            order.push((k.primary, i as u32));
            keys.push(k);
            state_hashes.push(sh);
            survival_keys.push(s);
            tempo_keys.push(t);
            setup_keys.push(su);
            optionality_keys.push(o);
            aux_scores.push(a);
        }
        (all_fast, same_sig)
    };
    #[inline(always)]
    fn cmp_order_entry(
        (pa, ia): (f64, u32),
        (pb, ib): (f64, u32),
        keys: &[PruneKey],
        state_hashes: &[crate::hash::StateHash],
    ) -> std::cmp::Ordering {
        pb.total_cmp(&pa)
            .then_with(|| cmp_key_tie_break(keys, state_hashes, ia as usize, ib as usize))
    }
    let fast_partial = (bucket_index == 0 || quota < 6 || all_same_sig)
        && root.all_forced_potion_mask == 0
        && (root.ignore_long_term_rewards
            || (root.effective_growth_budgets.is_zero() && root.active_relic_targets.is_empty()))
        && !second_rank_band;
    let prefix_len =
        (quota + PER_NICHE_PARETO_TOP_K + (quota / 8) + usize::from(check_sig)).min(count);
    if fast_partial && count > prefix_len * 2 {
        order.select_nth_unstable_by(prefix_len - 1, |&a, &b| {
            cmp_order_entry(a, b, keys, state_hashes)
        });
        order[..prefix_len].sort_unstable_by(|&a, &b| cmp_order_entry(a, b, keys, state_hashes));
    } else {
        order.sort_unstable_by(|&a, &b| cmp_order_entry(a, b, keys, state_hashes));
    }

    // 0 = unselected, 1 = selected into `out`, 2 = queued in full-pool Pareto candidate set
    marks.clear();
    marks.resize(count, 0);
    let mut selected_in_bucket = 0usize;

    // 0a. Forced potion leader
    if root.all_forced_potion_mask != 0 && selected_in_bucket < quota {
        if let Some((_, &(_, idx_u32))) = order.iter().enumerate().max_by(|&(ra, &(_, ia_u32)), &(rb, &(_, ib_u32))| {
            let ia = ia_u32 as usize;
            let ib = ib_u32 as usize;
            keys[ia]
                .forced_match_count
                .cmp(&keys[ib].forced_match_count)
                .then_with(|| cmp_key_primary_max(keys, state_hashes, ia, ib))
                .then_with(|| rb.cmp(&ra))
        }) {
            let idx = idx_u32 as usize;
            marks[idx] = 1;
            out.push((keys[idx].primary, keys[idx].node_id));
            selected_in_bucket += 1;
        }
    }

    // 0b. Long-term growth / relic strategy leader
    if !root.ignore_long_term_rewards
        && (!root.effective_growth_budgets.is_zero() || !root.active_relic_targets.is_empty())
        && selected_in_bucket < quota
    {
        if let Some((_, &(_, idx_u32))) = order
            .iter()
            .enumerate()
            .filter(|&(_, &(_, i_u32))| marks[i_u32 as usize] == 0)
            .max_by(|&(ra, &(_, ia_u32)), &(rb, &(_, ib_u32))| {
                let ia = ia_u32 as usize;
                let ib = ib_u32 as usize;
                keys[ia]
                    .strategic_hp_credit
                    .cmp(&keys[ib].strategic_hp_credit)
                    .then_with(|| {
                        keys[ia]
                            .strategy_goal_count
                            .cmp(&keys[ib].strategy_goal_count)
                    })
                    .then_with(|| cmp_key_primary_max(keys, state_hashes, ia, ib))
                    .then_with(|| rb.cmp(&ra))
            })
        {
            let idx = idx_u32 as usize;
            marks[idx] = 1;
            out.push((keys[idx].primary, keys[idx].node_id));
            selected_in_bucket += 1;
        }
    }

    // 0c. Non-starving potion-signature diversity inside potion buckets (b >= 1)
    if check_sig && selected_in_bucket < quota {
        if all_same_sig {
            for &(_, i_u32) in order.iter() {
                let i = i_u32 as usize;
                if marks[i] == 0 {
                    let k = &keys[i];
                    marks[i] = 1;
                    out.push((k.primary, k.node_id));
                    selected_in_bucket += 1;
                    break;
                }
            }
        } else {
            let pct_cap = if prefer_potion_buckets {
                (quota * 25) / 100
            } else {
                (quota * 15) / 100
            };
            let diversity_cap = 16usize
                .min(pct_cap.min(quota.saturating_sub(5)).max(1))
                .min(quota - selected_in_bucket);

            let mut seen_sigs: SmallVec<[(u16, i16, u8, u64); 16]> = SmallVec::new();
            let mut div_added = 0usize;
            for &(_, i_u32) in order.iter() {
                let i = i_u32 as usize;
                if div_added >= diversity_cap || seen_sigs.len() >= 16 {
                    break;
                }
                if marks[i] != 0 {
                    continue;
                }
                let k = &keys[i];
                let sig = (
                    k.used_potion_slot_mask,
                    k.potion_cost,
                    k.explicit_potion_count,
                    k.forced_used_mask,
                );
                if !seen_sigs.contains(&sig) {
                    seen_sigs.push(sig);
                    marks[i] = 1;
                    out.push((k.primary, k.node_id));
                    selected_in_bucket += 1;
                    div_added += 1;
                }
            }
            if quota >= 16 && seen_sigs.len() > 1 && div_added < diversity_cap {
                for target_sig in seen_sigs {
                    if div_added >= diversity_cap || selected_in_bucket >= quota {
                        break;
                    }
                    for &(_, i_u32) in order.iter() {
                        let i = i_u32 as usize;
                        if marks[i] != 0 {
                            continue;
                        }
                        let k = &keys[i];
                        let sig = (
                            k.used_potion_slot_mask,
                            k.potion_cost,
                            k.explicit_potion_count,
                            k.forced_used_mask,
                        );
                        if sig == target_sig {
                            marks[i] = 1;
                            out.push((k.primary, k.node_id));
                            selected_in_bucket += 1;
                            div_added += 1;
                            break;
                        }
                    }
                }
            }
        }
    }

    let rem = quota.saturating_sub(selected_in_bucket);
    if rem == 0 {
        return;
    }

    // 1. 5 MAP-Elites niche quotas + Niche 5b true full-pool Pareto
    let (primary_q, survival_q, tempo_q, setup_q, optionality_q, pareto_q) =
        compute_niche_quotas(rem, is_turn_boundary, aggressive_power_commitment);

    let mut niche_pareto = [[0usize; PER_NICHE_PARETO_TOP_K]; NICHE_SLOT_COUNT];
    let mut niche_pareto_counts = [0usize; NICHE_SLOT_COUNT];

    #[inline(always)]
    fn cmp_pure_rollout(
        keys: &[PruneKey],
        state_hashes: &[crate::hash::StateHash],
        ia: usize,
        ib: usize,
    ) -> std::cmp::Ordering {
        let a = &keys[ia];
        let b = &keys[ib];
        let ha = a.has_rollout_score();
        let hb = b.has_rollout_score();
        hb.cmp(&ha)
            .then_with(|| {
                if ha && hb {
                    b.aux_score.total_cmp(&a.aux_score)
                } else {
                    std::cmp::Ordering::Equal
                }
            })
            .then_with(|| cmp_key_primary_desc(keys, state_hashes, ia, ib))
    }

    #[inline(always)]
    fn cmp_choice_lookahead(
        keys: &[PruneKey],
        state_hashes: &[crate::hash::StateHash],
        ia: usize,
        ib: usize,
    ) -> std::cmp::Ordering {
        let a = &keys[ia];
        let b = &keys[ib];
        let ha = a.has_choice_lookahead();
        let hb = b.has_choice_lookahead();
        hb.cmp(&ha)
            .then_with(|| {
                if ha && hb {
                    b.aux_score.total_cmp(&a.aux_score)
                } else {
                    std::cmp::Ordering::Equal
                }
            })
            .then_with(|| cmp_key_primary_desc(keys, state_hashes, ia, ib))
    }

    #[inline(always)]
    fn cmp_survival(
        keys: &[PruneKey],
        state_hashes: &[crate::hash::StateHash],
        ia: usize,
        ib: usize,
    ) -> std::cmp::Ordering {
        let a = &keys[ia];
        let b = &keys[ib];
        a.death_save_use_count
            .cmp(&b.death_save_use_count)
            .then_with(|| a.effective_hp_loss.cmp(&b.effective_hp_loss))
            .then_with(|| b.total_defense_hp.cmp(&a.total_defense_hp))
            .then_with(|| b.best_hp.cmp(&a.best_hp))
            .then_with(|| a.enemy_hp.cmp(&b.enemy_hp))
            .then_with(|| cmp_key_primary_desc(keys, state_hashes, ia, ib))
    }

    #[inline(always)]
    fn cmp_tempo(
        keys: &[PruneKey],
        state_hashes: &[crate::hash::StateHash],
        ia: usize,
        ib: usize,
    ) -> std::cmp::Ordering {
        let a = &keys[ia];
        let b = &keys[ib];
        a.rem_enemies
            .cmp(&b.rem_enemies)
            .then_with(|| a.enemy_hp.cmp(&b.enemy_hp))
            .then_with(|| a.effective_hp_loss.cmp(&b.effective_hp_loss))
            .then_with(|| cmp_key_primary_desc(keys, state_hashes, ia, ib))
    }

    #[inline(always)]
    fn cmp_setup(
        keys: &[PruneKey],
        state_hashes: &[crate::hash::StateHash],
        ia: usize,
        ib: usize,
    ) -> std::cmp::Ordering {
        let a = &keys[ia];
        let b = &keys[ib];
        b.board_investment
            .cmp(&a.board_investment)
            .then_with(|| a.effective_hp_loss.cmp(&b.effective_hp_loss))
            .then_with(|| b.setup_score.cmp(&a.setup_score))
            .then_with(|| cmp_key_primary_desc(keys, state_hashes, ia, ib))
    }

    #[inline(always)]
    fn cmp_optionality(
        keys: &[PruneKey],
        state_hashes: &[crate::hash::StateHash],
        ia: usize,
        ib: usize,
    ) -> std::cmp::Ordering {
        let a = &keys[ia];
        let b = &keys[ib];
        b.optionality_score
            .cmp(&a.optionality_score)
            .then_with(|| b.board_investment.cmp(&a.board_investment))
            .then_with(|| a.effective_hp_loss.cmp(&b.effective_hp_loss))
            .then_with(|| cmp_key_primary_desc(keys, state_hashes, ia, ib))
    }

    #[inline(always)]
    #[allow(clippy::too_many_arguments)]
    fn record_niche_selected<I>(
        ordered_indices: I,
        total_len: usize,
        keys: &[PruneKey],
        marks: &mut [u8],
        take: usize,
        pareto_extra: usize,
        out: &mut Vec<(f64, NodeId)>,
        pareto_out: &mut [usize; PER_NICHE_PARETO_TOP_K],
        pareto_count_out: &mut usize,
    ) -> usize
    where
        I: Iterator<Item = usize>,
    {
        let added = take.min(total_len);
        let extra = pareto_extra.min(total_len.saturating_sub(added));
        let mut iter = ordered_indices;
        for idx in iter.by_ref().take(added) {
            if marks[idx] == 0 {
                marks[idx] = 1;
                out.push((keys[idx].primary, keys[idx].node_id));
            }
        }
        let mut p_cnt = 0usize;
        for idx in iter.take(extra) {
            pareto_out[p_cnt] = idx;
            p_cnt += 1;
        }
        *pareto_count_out = p_cnt;
        added
    }

    // O(N log K) bounded heap top-K selection over contiguous `PruneKey`s (fallback when !all_fast_exact)
    #[inline]
    #[allow(clippy::too_many_arguments)]
    fn pick_top_k_niche<F>(
        keys: &[PruneKey],
        state_hashes: &[crate::hash::StateHash],
        marks: &mut [u8],
        heap: &mut Vec<usize>,
        take: usize,
        pareto_extra: usize,
        out: &mut Vec<(f64, NodeId)>,
        pareto_out: &mut [usize; PER_NICHE_PARETO_TOP_K],
        pareto_count_out: &mut usize,
        cmp_better: F,
    ) -> usize
    where
        F: Fn(&[PruneKey], &[crate::hash::StateHash], usize, usize) -> std::cmp::Ordering,
    {
        *pareto_count_out = 0;
        let total_k = take + pareto_extra;
        if total_k == 0 {
            return 0;
        }
        heap.clear();
        for i in 0..keys.len() {
            if marks[i] != 0 {
                continue;
            }
            if heap.len() < total_k {
                let mut pos = heap.len();
                heap.push(i);
                while pos > 0 {
                    let parent = (pos - 1) >> 1;
                    let p_idx = heap[parent];
                    if cmp_better(keys, state_hashes, p_idx, i).is_lt() {
                        heap[pos] = p_idx;
                        pos = parent;
                    } else {
                        break;
                    }
                }
                heap[pos] = i;
            } else {
                let w_idx = heap[0];
                if cmp_better(keys, state_hashes, i, w_idx).is_lt() {
                    let mut pos = 0;
                    let len = heap.len();
                    loop {
                        let left = (pos << 1) + 1;
                        if left >= len {
                            break;
                        }
                        let right = left + 1;
                        let worst_child = if right < len
                            && cmp_better(keys, state_hashes, heap[left], heap[right]).is_lt()
                        {
                            right
                        } else {
                            left
                        };
                        let wc_idx = heap[worst_child];
                        if cmp_better(keys, state_hashes, i, wc_idx).is_lt() {
                            heap[pos] = wc_idx;
                            pos = worst_child;
                        } else {
                            break;
                        }
                    }
                    heap[pos] = i;
                }
            }
        }
        if heap.is_empty() {
            return 0;
        }
        heap.sort_unstable_by(|&a, &b| cmp_better(keys, state_hashes, a, b));
        record_niche_selected(
            heap.iter().copied(),
            heap.len(),
            keys,
            marks,
            take,
            pareto_extra,
            out,
            pareto_out,
            pareto_count_out,
        )
    }

    // Fast O(N) SoA 2-phase bounded heap top-K selection when `all_fast_exact` holds.
    #[inline]
    #[allow(clippy::too_many_arguments)]
    fn pick_top_k_niche_fast(
        keys: &[PruneKey],
        state_hashes: &[crate::hash::StateHash],
        fast_keys: &[i64],
        marks: &mut [u8],
        heap_fast: &mut Vec<(i64, f64, u32)>,
        take: usize,
        pareto_extra: usize,
        out: &mut Vec<(f64, NodeId)>,
        pareto_out: &mut [usize; PER_NICHE_PARETO_TOP_K],
        pareto_count_out: &mut usize,
    ) -> usize {
        *pareto_count_out = 0;
        let total_k = take + pareto_extra;
        if total_k == 0 {
            return 0;
        }
        #[inline(always)]
        fn cmp_fast_entry(
            fa: i64,
            pa: f64,
            ia: u32,
            fb: i64,
            pb: f64,
            ib: u32,
            keys: &[PruneKey],
            state_hashes: &[crate::hash::StateHash],
        ) -> std::cmp::Ordering {
            if fa != fb {
                fb.cmp(&fa)
            } else if pa > pb {
                std::cmp::Ordering::Less
            } else if pa < pb {
                std::cmp::Ordering::Greater
            } else {
                cmp_key_primary_desc(keys, state_hashes, ia as usize, ib as usize)
            }
        }
        heap_fast.clear();
        let n = keys.len().min(fast_keys.len());
        let mut w_fast = 0i64;
        let mut w_primary = 0.0f64;
        let mut w_idx = 0u32;
        let mut i = 0usize;
        while i < n && heap_fast.len() < total_k {
            if marks[i] == 0 {
                let k_fast = fast_keys[i];
                let k_primary = keys[i].primary;
                let i_u32 = i as u32;
                let mut pos = heap_fast.len();
                heap_fast.push((k_fast, k_primary, i_u32));
                while pos > 0 {
                    let parent = (pos - 1) >> 1;
                    let (p_fast, p_primary, p_idx) = heap_fast[parent];
                    if cmp_fast_entry(
                        p_fast,
                        p_primary,
                        p_idx,
                        k_fast,
                        k_primary,
                        i_u32,
                        keys,
                        state_hashes,
                    )
                    .is_lt()
                    {
                        heap_fast[pos] = (p_fast, p_primary, p_idx);
                        pos = parent;
                    } else {
                        break;
                    }
                }
                heap_fast[pos] = (k_fast, k_primary, i_u32);
                if heap_fast.len() == total_k {
                    (w_fast, w_primary, w_idx) = heap_fast[0];
                }
            }
            i += 1;
        }
        while i < n {
            let k_fast = fast_keys[i];
            if k_fast >= w_fast && marks[i] == 0 {
                let k = &keys[i];
                let k_primary = k.primary;
                let reject = k_fast == w_fast
                    && (k_primary < w_primary
                        || (!(k_primary > w_primary)
                            && !cmp_key_primary_desc(keys, state_hashes, i, w_idx as usize)
                                .is_lt()));
                if !reject {
                    let i_u32 = i as u32;
                    let mut pos = 0;
                    let len = heap_fast.len();
                    loop {
                        let left = (pos << 1) + 1;
                        if left >= len {
                            break;
                        }
                        let right = left + 1;
                        let (l_fast, l_primary, l_idx) = heap_fast[left];
                        let (worst_child, c_fast, c_primary, c_idx) = if right < len {
                            let (r_fast, r_primary, r_idx) = heap_fast[right];
                            if cmp_fast_entry(
                                l_fast,
                                l_primary,
                                l_idx,
                                r_fast,
                                r_primary,
                                r_idx,
                                keys,
                                state_hashes,
                            )
                            .is_lt()
                            {
                                (right, r_fast, r_primary, r_idx)
                            } else {
                                (left, l_fast, l_primary, l_idx)
                            }
                        } else {
                            (left, l_fast, l_primary, l_idx)
                        };
                        if cmp_fast_entry(
                            k_fast,
                            k_primary,
                            i_u32,
                            c_fast,
                            c_primary,
                            c_idx,
                            keys,
                            state_hashes,
                        )
                        .is_lt()
                        {
                            heap_fast[pos] = (c_fast, c_primary, c_idx);
                            pos = worst_child;
                        } else {
                            break;
                        }
                    }
                    heap_fast[pos] = (k_fast, k_primary, i_u32);
                    (w_fast, w_primary, w_idx) = heap_fast[0];
                }
            }
            i += 1;
        }
        if heap_fast.is_empty() {
            return 0;
        }
        heap_fast.sort_unstable_by(|&(fa, pa, ia), &(fb, pb, ib)| {
            cmp_fast_entry(fa, pa, ia, fb, pb, ib, keys, state_hashes)
        });
        record_niche_selected(
            heap_fast.iter().map(|&(_, _, idx)| idx as usize),
            heap_fast.len(),
            keys,
            marks,
            take,
            pareto_extra,
            out,
            pareto_out,
            pareto_count_out,
        )
    }

    // Fast 2-phase f64-keyed bounded heap top-K selection for Pure Rollout & Choice Lookahead niches.
    #[inline]
    #[allow(clippy::too_many_arguments)]
    fn pick_top_k_niche_f64<F>(
        keys: &[PruneKey],
        state_hashes: &[crate::hash::StateHash],
        aux_scores: &[f64],
        marks: &mut [u8],
        heap_f64: &mut Vec<(f64, u32)>,
        take: usize,
        pareto_extra: usize,
        out: &mut Vec<(f64, NodeId)>,
        pareto_out: &mut [usize; PER_NICHE_PARETO_TOP_K],
        pareto_count_out: &mut usize,
        cmp_better: F,
    ) -> usize
    where
        F: Fn(&[PruneKey], &[crate::hash::StateHash], usize, usize) -> std::cmp::Ordering,
    {
        *pareto_count_out = 0;
        let total_k = take + pareto_extra;
        if total_k == 0 {
            return 0;
        }
        #[inline(always)]
        fn cmp_f64_entry<
            F: Fn(&[PruneKey], &[crate::hash::StateHash], usize, usize) -> std::cmp::Ordering,
        >(
            fa: f64,
            ia: u32,
            fb: f64,
            ib: u32,
            keys: &[PruneKey],
            state_hashes: &[crate::hash::StateHash],
            cmp_better: &F,
        ) -> std::cmp::Ordering {
            if fa > fb {
                std::cmp::Ordering::Less
            } else if fa < fb {
                std::cmp::Ordering::Greater
            } else {
                cmp_better(keys, state_hashes, ia as usize, ib as usize)
            }
        }
        heap_f64.clear();
        let n = keys.len().min(aux_scores.len());
        let mut w_score = 0.0f64;
        let mut w_idx = 0u32;
        let mut i = 0usize;
        while i < n && heap_f64.len() < total_k {
            if marks[i] == 0 {
                let k_score = aux_scores[i];
                let i_u32 = i as u32;
                let mut pos = heap_f64.len();
                heap_f64.push((k_score, i_u32));
                while pos > 0 {
                    let parent = (pos - 1) >> 1;
                    let (p_score, p_idx) = heap_f64[parent];
                    if cmp_f64_entry(
                        p_score,
                        p_idx,
                        k_score,
                        i_u32,
                        keys,
                        state_hashes,
                        &cmp_better,
                    )
                    .is_lt()
                    {
                        heap_f64[pos] = (p_score, p_idx);
                        pos = parent;
                    } else {
                        break;
                    }
                }
                heap_f64[pos] = (k_score, i_u32);
                if heap_f64.len() == total_k {
                    (w_score, w_idx) = heap_f64[0];
                }
            }
            i += 1;
        }
        while i < n {
            let k_score = aux_scores[i];
            if !(k_score < w_score) && marks[i] == 0 {
                let accept = k_score > w_score
                    || cmp_better(keys, state_hashes, i, w_idx as usize).is_lt();
                if accept {
                    let i_u32 = i as u32;
                    let mut pos = 0;
                    let len = heap_f64.len();
                    loop {
                        let left = (pos << 1) + 1;
                        if left >= len {
                            break;
                        }
                        let right = left + 1;
                        let (l_score, l_idx) = heap_f64[left];
                        let (worst_child, c_score, c_idx) = if right < len {
                            let (r_score, r_idx) = heap_f64[right];
                            if cmp_f64_entry(
                                l_score,
                                l_idx,
                                r_score,
                                r_idx,
                                keys,
                                state_hashes,
                                &cmp_better,
                            )
                            .is_lt()
                            {
                                (right, r_score, r_idx)
                            } else {
                                (left, l_score, l_idx)
                            }
                        } else {
                            (left, l_score, l_idx)
                        };
                        if cmp_f64_entry(
                            k_score,
                            i_u32,
                            c_score,
                            c_idx,
                            keys,
                            state_hashes,
                            &cmp_better,
                        )
                        .is_lt()
                        {
                            heap_f64[pos] = (c_score, c_idx);
                            pos = worst_child;
                        } else {
                            break;
                        }
                    }
                    heap_f64[pos] = (k_score, i_u32);
                    (w_score, w_idx) = heap_f64[0];
                }
            }
            i += 1;
        }
        if heap_f64.is_empty() {
            return 0;
        }
        heap_f64.sort_unstable_by(|&(fa, ia), &(fb, ib)| {
            cmp_f64_entry(fa, ia, fb, ib, keys, state_hashes, &cmp_better)
        });
        record_niche_selected(
            heap_f64.iter().map(|&(_, idx)| idx as usize),
            heap_f64.len(),
            keys,
            marks,
            take,
            pareto_extra,
            out,
            pareto_out,
            pareto_count_out,
        )
    }

    // Primary niche (reserve ~30% pure rollout slice on turn boundaries or ~20% choice-lookahead mid-turn)
    if primary_q > 0 || pareto_q > 0 {
        let has_rollout = use_rollout_scores
            && keys
                .iter()
                .enumerate()
                .any(|(i, k)| marks[i] == 0 && k.has_rollout_score());
        let has_choice_lookahead = !is_turn_boundary
            && !has_rollout
            && keys
                .iter()
                .enumerate()
                .any(|(i, k)| marks[i] == 0 && k.has_choice_lookahead());
        let rollout_pure_q = if has_rollout && primary_q >= 3 {
            (primary_q * 30 / 100).max(1)
        } else {
            0
        };
        let choice_lookahead_q = if has_choice_lookahead && primary_q >= 3 {
            (primary_q * 20 / 100).max(1)
        } else {
            0
        };
        let blended_q = primary_q.saturating_sub(rollout_pure_q + choice_lookahead_q);
        let take_blended = blended_q.min(quota - selected_in_bucket);
        let mut taken_blended = 0usize;
        let mut p_cnt = 0usize;
        if second_rank_band && blended_q > 0 {
            unmarked.clear();
            unmarked.extend(
                order
                    .iter()
                    .map(|&(_, i_u32)| i_u32 as usize)
                    .filter(|&i| marks[i] == 0),
            );
            BeamWidthPortfolio::move_leading_band_to_tail(unmarked, blended_q);
            for &idx in unmarked.iter() {
                if taken_blended < take_blended {
                    marks[idx] = 1;
                    out.push((keys[idx].primary, keys[idx].node_id));
                    selected_in_bucket += 1;
                    taken_blended += 1;
                } else if pareto_q > 0 && p_cnt < PER_NICHE_PARETO_TOP_K {
                    niche_pareto[0][p_cnt] = idx;
                    p_cnt += 1;
                } else {
                    break;
                }
            }
        } else {
            for &(_, idx_u32) in order.iter() {
                let idx = idx_u32 as usize;
                if marks[idx] != 0 {
                    continue;
                }
                if taken_blended < take_blended {
                    marks[idx] = 1;
                    out.push((keys[idx].primary, keys[idx].node_id));
                    selected_in_bucket += 1;
                    taken_blended += 1;
                } else if pareto_q > 0 && p_cnt < PER_NICHE_PARETO_TOP_K {
                    niche_pareto[0][p_cnt] = idx;
                    p_cnt += 1;
                } else {
                    break;
                }
            }
        }
        niche_pareto_counts[0] = p_cnt;

        if has_rollout && (rollout_pure_q > 0 || pareto_q > 0) && selected_in_bucket < quota {
            let take_roll = rollout_pure_q.min(quota - selected_in_bucket);
            selected_in_bucket += pick_top_k_niche_f64(
                keys,
                state_hashes,
                aux_scores,
                marks,
                heap_f64,
                take_roll,
                if pareto_q > 0 { PER_NICHE_PARETO_TOP_K } else { 0 },
                out,
                &mut niche_pareto[5],
                &mut niche_pareto_counts[5],
                cmp_pure_rollout,
            );
        } else if has_choice_lookahead
            && (choice_lookahead_q > 0 || pareto_q > 0)
            && selected_in_bucket < quota
        {
            if use_rollout_scores {
                for (slot, k) in aux_scores.iter_mut().zip(keys.iter()) {
                    *slot = if k.has_choice_lookahead() {
                        k.aux_score
                    } else {
                        f64::NEG_INFINITY
                    };
                }
            }
            let take_la = choice_lookahead_q.min(quota - selected_in_bucket);
            selected_in_bucket += pick_top_k_niche_f64(
                keys,
                state_hashes,
                aux_scores,
                marks,
                heap_f64,
                take_la,
                if pareto_q > 0 { PER_NICHE_PARETO_TOP_K } else { 0 },
                out,
                &mut niche_pareto[5],
                &mut niche_pareto_counts[5],
                cmp_choice_lookahead,
            );
        }
    }

    let pareto_extra = if pareto_q > 0 { PER_NICHE_PARETO_TOP_K } else { 0 };

    // Niche 2 — Survival / HP Preservation
    if (survival_q > 0 || pareto_q > 0) && selected_in_bucket < quota {
        let take_survival = survival_q.min(quota - selected_in_bucket);
        selected_in_bucket += if all_fast_exact {
            pick_top_k_niche_fast(
                keys,
                state_hashes,
                survival_keys,
                marks,
                heap_fast,
                take_survival,
                pareto_extra,
                out,
                &mut niche_pareto[1],
                &mut niche_pareto_counts[1],
            )
        } else {
            pick_top_k_niche(
                keys,
                state_hashes,
                marks,
                heap,
                take_survival,
                pareto_extra,
                out,
                &mut niche_pareto[1],
                &mut niche_pareto_counts[1],
                cmp_survival,
            )
        };
    }

    // Niche 3 — Tempo / Kill Speed
    if (tempo_q > 0 || pareto_q > 0) && selected_in_bucket < quota {
        let take_tempo = tempo_q.min(quota - selected_in_bucket);
        selected_in_bucket += if all_fast_exact {
            pick_top_k_niche_fast(
                keys,
                state_hashes,
                tempo_keys,
                marks,
                heap_fast,
                take_tempo,
                pareto_extra,
                out,
                &mut niche_pareto[2],
                &mut niche_pareto_counts[2],
            )
        } else {
            pick_top_k_niche(
                keys,
                state_hashes,
                marks,
                heap,
                take_tempo,
                pareto_extra,
                out,
                &mut niche_pareto[2],
                &mut niche_pareto_counts[2],
                cmp_tempo,
            )
        };
    }

    // Niche 4 — Persistent Board Investment
    if (setup_q > 0 || pareto_q > 0) && selected_in_bucket < quota {
        let take_setup = setup_q.min(quota - selected_in_bucket);
        selected_in_bucket += if all_fast_exact {
            pick_top_k_niche_fast(
                keys,
                state_hashes,
                setup_keys,
                marks,
                heap_fast,
                take_setup,
                pareto_extra,
                out,
                &mut niche_pareto[3],
                &mut niche_pareto_counts[3],
            )
        } else {
            pick_top_k_niche(
                keys,
                state_hashes,
                marks,
                heap,
                take_setup,
                pareto_extra,
                out,
                &mut niche_pareto[3],
                &mut niche_pareto_counts[3],
                cmp_setup,
            )
        };
    }

    // Niche 5a — Optionality / Control
    if (optionality_q > 0 || pareto_q > 0) && selected_in_bucket < quota {
        let take_opt = optionality_q.min(quota - selected_in_bucket);
        selected_in_bucket += if all_fast_exact {
            pick_top_k_niche_fast(
                keys,
                state_hashes,
                optionality_keys,
                marks,
                heap_fast,
                take_opt,
                pareto_extra,
                out,
                &mut niche_pareto[4],
                &mut niche_pareto_counts[4],
            )
        } else {
            pick_top_k_niche(
                keys,
                state_hashes,
                marks,
                heap,
                take_opt,
                pareto_extra,
                out,
                &mut niche_pareto[4],
                &mut niche_pareto_counts[4],
                cmp_optionality,
            )
        };
    }

    // Niche 5b — True Full-Pool Pareto scanning top-32 runners-up from EACH niche via pre-extracted rows
    if pareto_q > 0 && selected_in_bucket < quota {
        const NICHE_ORDER: [usize; NICHE_SLOT_COUNT] = [3, 1, 2, 4, 5, 0];
        let mut pareto_pool = [0usize; NICHE_SLOT_COUNT * PER_NICHE_PARETO_TOP_K];
        let mut pool_rows = [ParetoRow::default(); NICHE_SLOT_COUNT * PER_NICHE_PARETO_TOP_K];
        let mut pareto_pool_count = 0usize;

        for rank in 0..PER_NICHE_PARETO_TOP_K {
            for &slot in &NICHE_ORDER {
                if rank >= niche_pareto_counts[slot] {
                    continue;
                }
                let idx = niche_pareto[slot][rank];
                if marks[idx] != 0 {
                    continue;
                }
                marks[idx] = 2;
                pareto_pool[pareto_pool_count] = idx;
                pool_rows[pareto_pool_count] = keys[idx].pareto_row();
                pareto_pool_count += 1;
            }
        }

        selected_rows.clear();
        let mut sel_min_loss = i32::MAX;
        let mut sel_min_enemy_hp = i32::MAX;
        let mut sel_min_rem = i32::MAX;
        let mut sel_max_board = i32::MIN;
        let mut sel_max_opt = i32::MIN;
        let mut sel_min_potion_cost = i32::MAX;
        let mut sel_max_primary = f64::NEG_INFINITY;
        for (idx, k) in keys.iter().enumerate() {
            if marks[idx] == 1 {
                let r = k.pareto_row();
                sel_min_loss = sel_min_loss.min(r.loss);
                sel_min_enemy_hp = sel_min_enemy_hp.min(r.enemy_hp);
                sel_min_rem = sel_min_rem.min(r.rem);
                sel_max_board = sel_max_board.max(r.board);
                sel_max_opt = sel_max_opt.max(r.opt);
                sel_min_potion_cost = sel_min_potion_cost.min(r.potion_cost);
                if r.primary > sel_max_primary {
                    sel_max_primary = r.primary;
                }
                selected_rows.push(r);
            }
        }
        selected_rows.sort_unstable_by_key(|r| (r.loss, r.enemy_hp));
        let mut selected_tail: SmallVec<[ParetoRow; 32]> = SmallVec::new();

        let pareto_budget = pareto_q.min(quota - selected_in_bucket);
        let mut pareto_added = 0usize;
        let mut pool_alive = [true; NICHE_SLOT_COUNT * PER_NICHE_PARETO_TOP_K];
        for p in 0..pareto_pool_count {
            if pareto_added >= pareto_budget {
                break;
            }
            if !pool_alive[p] {
                continue;
            }
            let cand_row = &pool_rows[p];
            let has_sel = !selected_rows.is_empty() || !selected_tail.is_empty();
            let mut dominated = false;
            if has_sel
                && !(cand_row.loss < sel_min_loss
                    || cand_row.enemy_hp < sel_min_enemy_hp
                    || cand_row.rem < sel_min_rem
                    || cand_row.board > sel_max_board
                    || cand_row.opt > sel_max_opt
                    || cand_row.potion_cost < sel_min_potion_cost
                    || cand_row.primary > sel_max_primary)
            {
                for r in selected_rows.iter() {
                    if r.loss > cand_row.loss
                        || (r.loss == cand_row.loss && r.enemy_hp > cand_row.enemy_hp)
                    {
                        break;
                    }
                    if pareto_row_dominates(r, cand_row) {
                        dominated = true;
                        break;
                    }
                }
                if !dominated {
                    for r in selected_tail.iter() {
                        if pareto_row_dominates(r, cand_row) {
                            dominated = true;
                            break;
                        }
                    }
                }
            }
            if !dominated {
                for j in 0..pareto_pool_count {
                    if j != p && pool_alive[j] && pareto_row_dominates(&pool_rows[j], cand_row) {
                        dominated = true;
                        break;
                    }
                }
            }
            if dominated {
                pool_alive[p] = false;
                continue;
            }

            let cand_idx = pareto_pool[p];
            marks[cand_idx] = 1;
            out.push((keys[cand_idx].primary, keys[cand_idx].node_id));
            sel_min_loss = sel_min_loss.min(cand_row.loss);
            sel_min_enemy_hp = sel_min_enemy_hp.min(cand_row.enemy_hp);
            sel_min_rem = sel_min_rem.min(cand_row.rem);
            sel_max_board = sel_max_board.max(cand_row.board);
            sel_max_opt = sel_max_opt.max(cand_row.opt);
            sel_min_potion_cost = sel_min_potion_cost.min(cand_row.potion_cost);
            if cand_row.primary > sel_max_primary {
                sel_max_primary = cand_row.primary;
            }
            selected_tail.push(*cand_row);
            pool_alive[p] = false;
            selected_in_bucket += 1;
            pareto_added += 1;
        }
    }

    // Fill any remaining seats in primary order
    for &(_, i_u32) in order.iter() {
        let i = i_u32 as usize;
        if selected_in_bucket >= quota {
            break;
        }
        if marks[i] != 1 {
            marks[i] = 1;
            out.push((keys[i].primary, keys[i].node_id));
            selected_in_bucket += 1;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[allow(unused_imports)]
    use crate::arena::{PackedHandle, SearchNode};
    use crate::eval::{StoredFastEval, StoredNodeEval};
    use crate::hash::StateHash;

    fn make_tied_node(
        arena: &mut NodeArena,
        hash_lo: u64,
        has_choice_lookahead: bool,
        rollout_score: Option<f64>,
    ) -> NodeId {
        let mut full = StoredNodeEval::default();
        full.beam_rank_score = 1234.0;
        full.has_choice_lookahead = has_choice_lookahead;
        full.choice_lookahead_score = if has_choice_lookahead {
            rollout_score
        } else {
            None
        };
        full.rollout_score = None;

        let mut fast = StoredFastEval::default();
        fast.cumulative_hp_lost = 5;
        fast.enemy_hp = 100;
        fast.alive_enemy_count = 1;

        let mut node = SearchNode {
            state_hash: StateHash::from_parts(0, hash_lo),
            beam_rank_score: 1234.0,
            turn: 1,
            action_count: 1,
            fast_eval: fast,
            ..Default::default()
        };
        node.set_full_eval(&full);
        arena.push_node(node)
    }

    #[test]
    fn tie_break_choice_lookahead_mixed_is_strict_total_order() {
        let mut arena = NodeArena::default();
        let mut ids = Vec::with_capacity(32);

        // Exact 3-node cycle witness that panicked without the ha vs hb guard
        ids.push(make_tied_node(&mut arena, 100, true, Some(200.0)));
        ids.push(make_tied_node(&mut arena, 50, false, None));
        ids.push(make_tied_node(&mut arena, 10, true, Some(100.0)));

        // Mix of has_choice_lookahead true/false with reversed/shuffled hashes and scores
        for i in 0..29u64 {
            let has_la = (i % 2) == 0;
            let hash_lo = ((29 - i) * 17) ^ 0x55;
            let rollout = if has_la {
                Some(((i * 37) % 13) as f64 * 10.0)
            } else {
                Some(999.0)
            };
            ids.push(make_tied_node(&mut arena, hash_lo, has_la, rollout));
        }

        for &a in &ids {
            assert_eq!(cmp_primary_desc(&arena, a, a, false), std::cmp::Ordering::Equal);
            for &b in &ids {
                let ab = cmp_primary_desc(&arena, a, b, false);
                let ba = cmp_primary_desc(&arena, b, a, false);
                assert_eq!(ab, ba.reverse(), "antisymmetry violated for {a:?} vs {b:?}");
                if !ab.is_gt() {
                    for &c in &ids {
                        let bc = cmp_primary_desc(&arena, b, c, false);
                        if !bc.is_gt() {
                            let ac = cmp_primary_desc(&arena, a, c, false);
                            assert!(
                                !ac.is_gt(),
                                "transitivity violated for {a:?} <= {b:?} <= {c:?}"
                            );
                        }
                    }
                }
            }
        }

        let root = RootEvalBaseline::default();
        let kept = BeamRetentionPolicy::prune_wave_full(
            &mut arena,
            &ids,
            16,
            None,
            None,
            &root,
            false,
            false,
            false,
            false,
            false,
        );
        assert_eq!(kept.len(), 16);
    }

    #[test]
    fn fast_partial_same_sig_potion_bucket_matches_full_sort() {
        let mut arena = NodeArena::default();
        let mut cands = Vec::with_capacity(220);
        for i in 0..220u64 {
            let mut full = StoredNodeEval::default();
            full.beam_rank_score = ((i.wrapping_mul(37)) % 97) as f64 * 13.0;
            full.effective_defense = (i % 19) as i32;
            full.board_investment = ((i * 5) % 17) as i32;
            full.setup_score = ((i * 3) % 23) as i32;
            full.optionality_cards = (i % 7) as i32;
            let mut fast = StoredFastEval::default();
            fast.cumulative_hp_lost = (i % 11) as i16;
            fast.enemy_hp = 100 - (i % 29) as i32;
            fast.alive_enemy_count = 1;
            let mut node = SearchNode {
                state_hash: StateHash::from_parts(0, i.wrapping_mul(17).wrapping_add(3)),
                beam_rank_score: full.beam_rank_score,
                turn: 1,
                action_count: 1,
                potion_count: 1,
                explicit_potion_count: 1,
                used_potion_slot_mask: 1,
                potion_strategic_cost: 9,
                fast_eval: fast,
                ..Default::default()
            };
            node.set_full_eval(&full);
            cands.push(arena.push_node(node));
        }

        let root = RootEvalBaseline {
            ignore_long_term_rewards: true,
            ..Default::default()
        };
        let mut scratch = PruneScratch::default();
        let mut actual_ids = Vec::new();
        BeamRetentionPolicy::prune_wave_full_into(
            &arena,
            &cands,
            32,
            None,
            None,
            &root,
            false,
            false,
            false,
            false,
            false,
            None,
            &mut actual_ids,
            &mut scratch,
        );

        let mut expected_scratch = PruneScratch::default();
        let bucket = &mut expected_scratch.buckets[1];
        bucket.nodes.extend_from_slice(&cands);
        for (i, &nid) in bucket.nodes.iter().enumerate() {
            let (k, s, t, su, o, a, sh) = arena.build_prune_entry(nid, &root, false);
            bucket.order.push((k.primary, i as u32));
            bucket.keys.push(k);
            bucket.state_hashes.push(sh);
            bucket.survival_keys.push(s);
            bucket.tempo_keys.push(t);
            bucket.setup_keys.push(su);
            bucket.optionality_keys.push(o);
            bucket.aux_scores.push(a);
        }
        bucket.order.sort_unstable_by(|&(pa, ia), &(pb, ib)| {
            pb.total_cmp(&pa).then_with(|| {
                cmp_key_tie_break(&bucket.keys, &bucket.state_hashes, ia as usize, ib as usize)
            })
        });
        assert_eq!(actual_ids.len(), 32);
        // Primary leader + 0c top pick guarantees top element matches `bucket.order[0]`
        assert_eq!(actual_ids[0], bucket.keys[bucket.order[0].1 as usize].node_id);

        // Compare directly against a second `prune_wave_full_into` call with mixed sigs (which takes full sort)
        let last = arena.get_mut(*cands.last().unwrap());
        last.used_potion_slot_mask = 2;
        last.beam_rank_score = -1_000_000.0;
        let mut expected_ids = Vec::new();
        BeamRetentionPolicy::prune_wave_full_into(
            &arena,
            &cands,
            32,
            None,
            None,
            &root,
            false,
            false,
            false,
            false,
            false,
            None,
            &mut expected_ids,
            &mut expected_scratch,
        );
        // Same-sig partial path picks 1 diverse slot (order[0]) then niches, while mixed path adds the worst-score slot-2 node
        assert_eq!(actual_ids[0], expected_ids[0]);
    }
}
