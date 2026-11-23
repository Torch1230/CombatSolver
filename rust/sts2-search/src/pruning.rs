use crate::arena::{NodeArena, NodeId};
use crate::eval::{
    ActEndingBossPolicy, BossHpRelief, PostCombatRelicHealProfile, PowerRoutePriority,
    RelicCounterId, RootEvalBaseline,
};
use crate::portfolio::BeamWidthPortfolio;
use hashbrown::HashMap;
use parking_lot::RwLock;
use std::sync::atomic::{AtomicI32, Ordering};
use sts2_kernel::state::BattleState;
use sts2_kernel::ContentDispatcher;
use sts2_types::{
    CardId, CardStaticFlags, PileType, PotionId, PowerStaticFlags, RelicId, RelicStaticFlags,
};

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
                if meta.may_heal {
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
        if candidates.is_empty() || beam_width == 0 {
            return Vec::new();
        }
        let has_explicit_split = potion_free_beam_width.is_some() || potion_beam_width.is_some();
        if candidates.len() <= beam_width && !has_explicit_split {
            let mut out = candidates.to_vec();
            out.sort_by(|&a, &b| {
                let sa = arena
                    .full_eval(a)
                    .map_or(arena.get(a).fast_eval.base_score, |e| e.beam_rank_score);
                let sb = arena
                    .full_eval(b)
                    .map_or(arena.get(b).fast_eval.base_score, |e| e.beam_rank_score);
                sb.total_cmp(&sa)
                    .then_with(|| arena.get(a).state_hash.cmp(&arena.get(b).state_hash))
            });
            for (rank, &nid) in out.iter().enumerate() {
                arena.get_mut(nid).retention_rank = rank as u32;
            }
            return out;
        }

        // 1. Multi-objective dominance filter inside identical (enemy_dist, unordered_pile) groups
        let mut nondominated: Vec<NodeId> = Vec::with_capacity(candidates.len());
        'outer: for &cand in candidates {
            let nc = arena.get(cand);
            let Some(fc) = arena.full_eval(cand) else {
                nondominated.push(cand);
                continue;
            };
            for &other in candidates {
                if cand == other {
                    continue;
                }
                let no = arena.get(other);
                let Some(fo) = arena.full_eval(other) else {
                    continue;
                };
                if fo.enemy_combat_dist_hash != fc.enemy_combat_dist_hash
                    || fo.unordered_pile_hash != fc.unordered_pile_hash
                {
                    continue;
                }
                if multi_objective_dominates(no, fo, nc, fc) {
                    continue 'outer;
                }
            }
            nondominated.push(cand);
        }

        // 2. Sort ranked pool descending by beam_rank_score (tie-break deterministically by state_hash)
        let mut ranked = nondominated;
        ranked.sort_by(|&a, &b| {
            let sa = arena
                .full_eval(a)
                .map_or(arena.get(a).fast_eval.base_score, |e| e.beam_rank_score);
            let sb = arena
                .full_eval(b)
                .map_or(arena.get(b).fast_eval.base_score, |e| e.beam_rank_score);
            sb.total_cmp(&sa)
                .then_with(|| arena.get(a).state_hash.cmp(&arena.get(b).state_hash))
        });

        if has_explicit_split {
            let free_quota = potion_free_beam_width.unwrap_or(beam_width).min(beam_width);
            let used_quota = potion_beam_width.unwrap_or(beam_width).min(beam_width);
            let mut selected: Vec<NodeId> = Vec::with_capacity(beam_width);
            let mut free_taken = 0usize;
            let mut used_taken = 0usize;
            for &nid in &ranked {
                if selected.len() >= beam_width {
                    break;
                }
                if arena.get(nid).explicit_potion_count == 0 {
                    if free_taken < free_quota {
                        selected.push(nid);
                        free_taken += 1;
                    }
                } else if used_taken < used_quota {
                    selected.push(nid);
                    used_taken += 1;
                }
            }
            for (rank, &nid) in selected.iter().enumerate() {
                arena.get_mut(nid).retention_rank = rank as u32;
            }
            return selected;
        }

        // 3. Mandatory specialist leaders per beam lane (defensive, offensive, setup, control, resource, power)
        let mut selected: Vec<NodeId> = Vec::with_capacity(beam_width);
        fn push_unique(selected: &mut Vec<NodeId>, beam_width: usize, cand: Option<NodeId>) {
            if let Some(id) = cand {
                if selected.len() < beam_width && !selected.contains(&id) {
                    selected.push(id);
                }
            }
        }

        // Best forced potion commitment (when Force directives are active)
        if root.all_forced_potion_mask != 0 {
            push_unique(
                &mut selected,
                beam_width,
                ranked.iter().copied().max_by(|&a, &b| {
                    let na = arena.get(a);
                    let nb = arena.get(b);
                    let fa = (na.forced_used_mask & root.all_forced_potion_mask).count_ones();
                    let fb = (nb.forced_used_mask & root.all_forced_potion_mask).count_ones();
                    fa.cmp(&fb)
                        .then_with(|| na.fast_eval.base_score.total_cmp(&nb.fast_eval.base_score))
                }),
            );
        }

        // Best long-term growth / relic strategy leader
        if !root.ignore_long_term_rewards
            && (!root.effective_growth_budgets.is_zero() || !root.active_relic_targets.is_empty())
        {
            push_unique(
                &mut selected,
                beam_width,
                ranked.iter().copied().max_by(|&a, &b| {
                    let fa = &arena.get(a).fast_eval;
                    let fb = &arena.get(b).fast_eval;
                    fa.strategic_hp_credit
                        .cmp(&fb.strategic_hp_credit)
                        .then_with(|| fa.strategy_goal_count.cmp(&fb.strategy_goal_count))
                        .then_with(|| fa.base_score.total_cmp(&fb.base_score))
                }),
            );
        }

        // Best defensive
        push_unique(
            &mut selected,
            beam_width,
            ranked.iter().copied().max_by(|&a, &b| {
                let fa = &arena.get(a).fast_eval;
                let fb = &arena.get(b).fast_eval;
                fa.projected_player_hp
                    .cmp(&fb.projected_player_hp)
                    .then_with(|| fa.player_block.cmp(&fb.player_block))
                    .then_with(|| fa.base_score.total_cmp(&fb.base_score))
            }),
        );
        // Best offensive
        push_unique(
            &mut selected,
            beam_width,
            ranked.iter().copied().max_by(|&a, &b| {
                let fa = &arena.get(a).fast_eval;
                let fb = &arena.get(b).fast_eval;
                fb.alive_enemy_count
                    .cmp(&fa.alive_enemy_count)
                    .then_with(|| fb.raw_enemy_hp.cmp(&fa.raw_enemy_hp))
                    .then_with(|| fb.enemy_hp.cmp(&fa.enemy_hp))
                    .then_with(|| fa.projected_player_hp.cmp(&fb.projected_player_hp))
            }),
        );
        // Best setup / power commitment
        let power_seats = if aggressive_power_commitment {
            (beam_width / 4).max(2)
        } else {
            1
        };
        let mut power_added = 0usize;
        for &nid in &ranked {
            if power_added >= power_seats || selected.len() >= beam_width {
                break;
            }
            if let Some(full) = arena.full_eval(nid) {
                if full.best_power_priority >= PowerRoutePriority::Normal
                    || full.persistent_buff_value > 0
                {
                    if !selected.contains(&nid) {
                        selected.push(nid);
                        power_added += 1;
                    }
                }
            }
        }
        // Best control (weak / vulnerable / poison / strength suppression)
        push_unique(
            &mut selected,
            beam_width,
            ranked.iter().copied().max_by_key(|&nid| {
                arena.full_eval(nid).map_or(0, |f| {
                    f.enemy_strength_suppression * 32
                        + f.enemy_weak_turns * 8
                        + f.enemy_vulnerable_turns * 4
                        + f.delayed_damage_value
                })
            }),
        );

        // 4. Potion quota reservation when both potion & no-potion routes exist
        let has_no_potion = ranked.iter().any(|&id| arena.get(id).potion_count == 0);
        let has_potion = ranked.iter().any(|&id| arena.get(id).potion_count > 0);
        if has_no_potion && has_potion {
            let (used_quota, unused_quota) = Self::feasible_potion_use_quotas(beam_width);
            let mut cur_used = selected.iter().filter(|&&id| arena.get(id).potion_count > 0).count();
            let mut cur_unused = selected.len() - cur_used;
            for &nid in &ranked {
                if cur_unused >= unused_quota || selected.len() >= beam_width {
                    break;
                }
                if arena.get(nid).potion_count == 0 && !selected.contains(&nid) {
                    selected.push(nid);
                    cur_unused += 1;
                }
            }
            for &nid in &ranked {
                if cur_used >= used_quota || selected.len() >= beam_width {
                    break;
                }
                if arena.get(nid).potion_count > 0 && !selected.contains(&nid) {
                    selected.push(nid);
                    cur_used += 1;
                }
            }
        }

        // 5. Ordinary seats (with SecondRankBand support from BeamWidthPortfolio.cs)
        let mut ordinary: Vec<NodeId> = ranked
            .iter()
            .copied()
            .filter(|id| !selected.contains(id))
            .collect();
        if second_rank_band {
            BeamWidthPortfolio::move_leading_band_to_tail(&mut ordinary, beam_width);
        }
        for nid in ordinary {
            if selected.len() >= beam_width {
                break;
            }
            selected.push(nid);
        }

        for (rank, &nid) in selected.iter().enumerate() {
            arena.get_mut(nid).retention_rank = rank as u32;
        }
        selected
    }
}

#[inline]
fn multi_objective_dominates(
    left_node: &crate::arena::SearchNode,
    left: &crate::eval::FullEvalDetails,
    right_node: &crate::arena::SearchNode,
    right: &crate::eval::FullEvalDetails,
) -> bool {
    let lf = &left_node.fast_eval;
    let rf = &right_node.fast_eval;
    let no_worse = lf.projected_player_hp >= rf.projected_player_hp
        && lf.player_max_hp >= rf.player_max_hp
        && lf.cumulative_hp_lost <= rf.cumulative_hp_lost
        && lf.strategic_hp_credit >= rf.strategic_hp_credit
        && lf.strategy_goal_count >= rf.strategy_goal_count
        && lf.raw_growth_reward_count >= rf.raw_growth_reward_count
        && (left_node.forced_used_mask & right_node.forced_used_mask)
            == right_node.forced_used_mask
        && lf.alive_enemy_count <= rf.alive_enemy_count
        && lf.enemy_hp <= rf.enemy_hp
        && lf.raw_enemy_hp <= rf.raw_enemy_hp
        && left.persistent_buff_value >= right.persistent_buff_value
        && left.latent_setup_value >= right.latent_setup_value
        && left.delayed_damage_value >= right.delayed_damage_value
        && left.enemy_strength_suppression >= right.enemy_strength_suppression
        && left.enemy_weak_turns >= right.enemy_weak_turns
        && left.enemy_vulnerable_turns >= right.enemy_vulnerable_turns
        && lf.energy >= rf.energy
        && lf.stars >= rf.stars
        && left.future_resource_value >= right.future_resource_value
        && left.osty_hp >= right.osty_hp
        && left.osty_max_hp >= right.osty_max_hp
        && left.retained_attack_value >= right.retained_attack_value
        && left.replay_potential_value >= right.replay_potential_value
        && lf.live_deck_clutter <= rf.live_deck_clutter
        && lf.live_deck_size <= rf.live_deck_size
        && left_node.potion_count <= right_node.potion_count
        && left_node.potion_strategic_cost <= right_node.potion_strategic_cost
        && left_node.future_sold_hp <= right_node.future_sold_hp
        && left_node.action_count <= right_node.action_count;

    if !no_worse {
        return false;
    }

    lf.projected_player_hp > rf.projected_player_hp
        || lf.player_max_hp > rf.player_max_hp
        || lf.cumulative_hp_lost < rf.cumulative_hp_lost
        || lf.strategic_hp_credit > rf.strategic_hp_credit
        || lf.strategy_goal_count > rf.strategy_goal_count
        || lf.raw_growth_reward_count > rf.raw_growth_reward_count
        || lf.alive_enemy_count < rf.alive_enemy_count
        || lf.enemy_hp < rf.enemy_hp
        || lf.raw_enemy_hp < rf.raw_enemy_hp
        || left.persistent_buff_value > right.persistent_buff_value
        || left.latent_setup_value > right.latent_setup_value
        || left.delayed_damage_value > right.delayed_damage_value
        || left.enemy_strength_suppression > right.enemy_strength_suppression
        || left.enemy_weak_turns > right.enemy_weak_turns
        || left.enemy_vulnerable_turns > right.enemy_vulnerable_turns
        || lf.energy > rf.energy
        || lf.stars > rf.stars
        || left.future_resource_value > right.future_resource_value
        || left.osty_hp > right.osty_hp
        || left.osty_max_hp > right.osty_max_hp
        || left.retained_attack_value > right.retained_attack_value
        || left.replay_potential_value > right.replay_potential_value
        || lf.live_deck_clutter < rf.live_deck_clutter
        || lf.live_deck_size < rf.live_deck_size
        || left_node.potion_count < right_node.potion_count
        || left_node.potion_strategic_cost < right_node.potion_strategic_cost
        || left_node.future_sold_hp < right_node.future_sold_hp
        || left_node.action_count < right_node.action_count
}
