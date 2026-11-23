use crate::arena::{NodeArena, NodeId};
use crate::eval::{
    ActEndingBossPolicy, BossHpRelief, PostCombatRelicHealProfile, RelicCounterId, RootEvalBaseline,
};
use crate::portfolio::BeamWidthPortfolio;
use hashbrown::HashMap;
use parking_lot::RwLock;
use smallvec::SmallVec;
use std::sync::atomic::{AtomicI32, Ordering};
use sts2_kernel::state::BattleState;
use sts2_kernel::ContentDispatcher;
use sts2_types::{
    CardId, CardStaticFlags, PileType, PotionId, PowerStaticFlags, RelicId, RelicStaticFlags,
};

const PER_NICHE_PARETO_TOP_K: usize = 32;
const NICHE_SLOT_COUNT: usize = 6; // 0=Primary, 1=Survival, 2=Tempo, 3=BoardInvestment, 4=Optionality, 5=PureRollout

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
        if candidates.is_empty() || beam_width == 0 {
            return Vec::new();
        }
        let has_explicit_split = potion_free_beam_width.is_some() || potion_beam_width.is_some();
        if candidates.len() <= beam_width && !has_explicit_split {
            let mut out = candidates.to_vec();
            out.sort_unstable_by(|&a, &b| cmp_primary_desc(arena, a, b, use_rollout_scores));
            for (rank, &nid) in out.iter().enumerate() {
                arena.get_mut(nid).retention_rank = rank as u32;
            }
            return out;
        }

        if has_explicit_split {
            let mut ranked = candidates.to_vec();
            ranked.sort_unstable_by(|&a, &b| cmp_primary_desc(arena, a, b, use_rollout_scores));
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

        // Single-pass 4-bucket partitioning by potion count BEFORE per-bucket sorting.
        let mut counts = [0usize; 4];
        for &nid in candidates {
            counts[(arena.get(nid).potion_count as usize).min(3)] += 1;
        }
        let mut buckets: [Vec<NodeId>; 4] = [
            Vec::with_capacity(counts[0]),
            Vec::with_capacity(counts[1]),
            Vec::with_capacity(counts[2]),
            Vec::with_capacity(counts[3]),
        ];
        for &nid in candidates {
            let b = (arena.get(nid).potion_count as usize).min(3);
            buckets[b].push(nid);
        }
        let bucket_quotas =
            allocate_potion_bucket_quotas(counts, beam_width, prefer_potion_buckets);

        let mut selected: Vec<NodeId> = Vec::with_capacity(beam_width);
        for b in 0..4 {
            if buckets[b].is_empty() || bucket_quotas[b] == 0 {
                continue;
            }
            select_from_potion_bucket(
                arena,
                &mut buckets[b],
                bucket_quotas[b],
                b,
                root,
                second_rank_band,
                aggressive_power_commitment,
                prefer_potion_buckets,
                is_turn_boundary,
                use_rollout_scores,
                &mut selected,
            );
        }

        // Deterministic canonical order across buckets so subsequent waves are DOP-invariant.
        selected.sort_unstable_by(|&a, &b| cmp_primary_desc(arena, a, b, use_rollout_scores));
        for (rank, &nid) in selected.iter().enumerate() {
            arena.get_mut(nid).retention_rank = rank as u32;
        }
        selected
    }
}

#[inline]
fn node_beam_score(arena: &NodeArena, id: NodeId, use_rollout_scores: bool) -> f64 {
    arena.full_eval(id).map_or(arena.get(id).fast_eval.base_score, |e| {
        if use_rollout_scores {
            e.effective_turn_boundary_score()
        } else {
            e.beam_rank_score
        }
    })
}

#[inline]
fn effective_net_hp_loss(arena: &NodeArena, id: NodeId, root: &RootEvalBaseline) -> i32 {
    let f = &arena.get(id).fast_eval;
    let max_hp_delta = root.initial_player_max_hp - f.player_max_hp;
    f.cumulative_hp_lost
        - f.recovered_hp
        + if max_hp_delta < 0 {
            max_hp_delta * 2
        } else {
            max_hp_delta
        }
}

#[inline]
fn node_optionality_score(arena: &NodeArena, id: NodeId) -> i32 {
    let f = &arena.get(id).fast_eval;
    let full_term = arena
        .full_eval(id)
        .map_or(0, |d| d.playable_card_count + d.next_turn_draw);
    f.energy.max(0) + f.stars.max(0) + full_term
}

#[inline]
fn node_setup_score(arena: &NodeArena, id: NodeId) -> i32 {
    arena.full_eval(id).map_or(0, |d| {
        (d.persistent_buff_value + d.enemy_debuff_value - d.enemy_threat_power_penalty)
            .clamp(-200, 2000)
    })
}

#[inline]
fn tie_break_deterministic(arena: &NodeArena, a: NodeId, b: NodeId) -> std::cmp::Ordering {
    let na = arena.get(a);
    let nb = arena.get(b);
    na.fast_eval
        .cumulative_hp_lost
        .cmp(&nb.fast_eval.cumulative_hp_lost)
        .then_with(|| na.fast_eval.enemy_hp.cmp(&nb.fast_eval.enemy_hp))
        .then_with(|| {
            let fa = arena.full_eval(a);
            let fb = arena.full_eval(b);
            let ha = fa.is_some_and(|f| f.has_choice_lookahead);
            let hb = fb.is_some_and(|f| f.has_choice_lookahead);
            hb.cmp(&ha).then_with(|| {
                if ha && hb {
                    let ra = fa
                        .and_then(|f| f.rollout_score)
                        .unwrap_or(f64::NEG_INFINITY);
                    let rb = fb
                        .and_then(|f| f.rollout_score)
                        .unwrap_or(f64::NEG_INFINITY);
                    rb.total_cmp(&ra)
                } else {
                    std::cmp::Ordering::Equal
                }
            })
        })
        .then_with(|| na.state_hash.cmp(&nb.state_hash))
        .then_with(|| a.0.cmp(&b.0))
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
fn cmp_primary_desc(
    arena: &NodeArena,
    a: NodeId,
    b: NodeId,
    use_rollout_scores: bool,
) -> std::cmp::Ordering {
    let sa = node_beam_score(arena, a, use_rollout_scores);
    let sb = node_beam_score(arena, b, use_rollout_scores);
    sb.total_cmp(&sa)
        .then_with(|| tie_break_deterministic(arena, a, b))
}

#[inline]
fn cmp_primary_max(
    arena: &NodeArena,
    a: NodeId,
    b: NodeId,
    use_rollout_scores: bool,
) -> std::cmp::Ordering {
    let sa = node_beam_score(arena, a, use_rollout_scores);
    let sb = node_beam_score(arena, b, use_rollout_scores);
    sa.total_cmp(&sb)
        .then_with(|| tie_break_deterministic(arena, b, a))
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
fn select_from_potion_bucket(
    arena: &NodeArena,
    bucket_nodes: &mut [NodeId],
    quota: usize,
    bucket_index: usize,
    root: &RootEvalBaseline,
    second_rank_band: bool,
    aggressive_power_commitment: bool,
    prefer_potion_buckets: bool,
    is_turn_boundary: bool,
    use_rollout_scores: bool,
    out: &mut Vec<NodeId>,
) {
    let bucket_out_start = out.len();
    let count = bucket_nodes.len();
    if count <= quota {
        out.extend_from_slice(bucket_nodes);
        return;
    }

    bucket_nodes.sort_unstable_by(|&a, &b| cmp_primary_desc(arena, a, b, use_rollout_scores));

    // 0 = unselected, 1 = selected into `out`, 2 = queued in full-pool Pareto candidate set
    let mut marks: SmallVec<[u8; 256]> = smallvec::smallvec![0u8; count];
    let mut selected_in_bucket = 0usize;

    // 0a. Forced potion leader
    if root.all_forced_potion_mask != 0 && selected_in_bucket < quota {
        if let Some((idx, &nid)) = bucket_nodes.iter().enumerate().max_by(|&(ia, &a), &(ib, &b)| {
            let na = arena.get(a);
            let nb = arena.get(b);
            let fa = (na.forced_used_mask & root.all_forced_potion_mask).count_ones();
            let fb = (nb.forced_used_mask & root.all_forced_potion_mask).count_ones();
            fa.cmp(&fb)
                .then_with(|| cmp_primary_max(arena, a, b, use_rollout_scores))
                .then_with(|| ib.cmp(&ia))
        }) {
            marks[idx] = 1;
            out.push(nid);
            selected_in_bucket += 1;
        }
    }

    // 0b. Long-term growth / relic strategy leader
    if !root.ignore_long_term_rewards
        && (!root.effective_growth_budgets.is_zero() || !root.active_relic_targets.is_empty())
        && selected_in_bucket < quota
    {
        if let Some((idx, &nid)) = bucket_nodes
            .iter()
            .enumerate()
            .filter(|&(i, _)| marks[i] == 0)
            .max_by(|&(ia, &a), &(ib, &b)| {
                let fa = &arena.get(a).fast_eval;
                let fb = &arena.get(b).fast_eval;
                fa.strategic_hp_credit
                    .cmp(&fb.strategic_hp_credit)
                    .then_with(|| fa.strategy_goal_count.cmp(&fb.strategy_goal_count))
                    .then_with(|| cmp_primary_max(arena, a, b, use_rollout_scores))
                    .then_with(|| ib.cmp(&ia))
            })
        {
            marks[idx] = 1;
            out.push(nid);
            selected_in_bucket += 1;
        }
    }

    // 0c. Non-starving potion-signature diversity inside potion buckets (b >= 1)
    if bucket_index >= 1 && quota >= 6 && selected_in_bucket < quota {
        let pct_cap = if prefer_potion_buckets {
            (quota * 25) / 100
        } else {
            (quota * 15) / 100
        };
        let diversity_cap = 16usize
            .min(pct_cap.min(quota.saturating_sub(5)).max(1))
            .min(quota - selected_in_bucket);

        let mut seen_sigs: SmallVec<[(u16, i32, u16, u64); 16]> = SmallVec::new();
        let mut div_added = 0usize;
        for (i, &nid) in bucket_nodes.iter().enumerate() {
            if div_added >= diversity_cap || seen_sigs.len() >= 16 {
                break;
            }
            if marks[i] != 0 {
                continue;
            }
            let n = arena.get(nid);
            let sig = (
                n.used_potion_slot_mask,
                n.potion_strategic_cost,
                n.explicit_potion_count,
                n.forced_used_mask,
            );
            if !seen_sigs.contains(&sig) {
                seen_sigs.push(sig);
                marks[i] = 1;
                out.push(nid);
                selected_in_bucket += 1;
                div_added += 1;
            }
        }
        if quota >= 16 && seen_sigs.len() > 1 && div_added < diversity_cap {
            for target_sig in seen_sigs {
                if div_added >= diversity_cap || selected_in_bucket >= quota {
                    break;
                }
                for (i, &nid) in bucket_nodes.iter().enumerate() {
                    if marks[i] != 0 {
                        continue;
                    }
                    let n = arena.get(nid);
                    let sig = (
                        n.used_potion_slot_mask,
                        n.potion_strategic_cost,
                        n.explicit_potion_count,
                        n.forced_used_mask,
                    );
                    if sig == target_sig {
                        marks[i] = 1;
                        out.push(nid);
                        selected_in_bucket += 1;
                        div_added += 1;
                        break;
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

    // O(N log K) bounded heap top-K selection inside a behavioral niche plus top-32 runners-up for full-pool Pareto
    #[allow(clippy::too_many_arguments)]
    fn pick_top_k_niche<F>(
        arena: &NodeArena,
        bucket_nodes: &[NodeId],
        marks: &mut [u8],
        take: usize,
        pareto_extra: usize,
        out: &mut Vec<NodeId>,
        pareto_out: &mut [usize; PER_NICHE_PARETO_TOP_K],
        pareto_count_out: &mut usize,
        cmp_better: F,
    ) -> usize
    where
        F: Fn(&NodeArena, NodeId, NodeId) -> std::cmp::Ordering,
    {
        *pareto_count_out = 0;
        let total_k = take + pareto_extra;
        if total_k == 0 {
            return 0;
        }
        let mut heap: SmallVec<[usize; 64]> = SmallVec::with_capacity(total_k);
        for (i, &nid) in bucket_nodes.iter().enumerate() {
            if marks[i] != 0 {
                continue;
            }
            if heap.len() < total_k {
                let mut pos = heap.len();
                heap.push(i);
                while pos > 0 {
                    let parent = (pos - 1) >> 1;
                    if cmp_better(arena, bucket_nodes[heap[parent]], bucket_nodes[heap[pos]])
                        .is_lt()
                    {
                        heap.swap(parent, pos);
                        pos = parent;
                    } else {
                        break;
                    }
                }
            } else if cmp_better(arena, nid, bucket_nodes[heap[0]]).is_lt() {
                heap[0] = i;
                let mut pos = 0;
                let len = heap.len();
                loop {
                    let left = (pos << 1) + 1;
                    if left >= len {
                        break;
                    }
                    let mut worst = pos;
                    if cmp_better(arena, bucket_nodes[heap[worst]], bucket_nodes[heap[left]])
                        .is_lt()
                    {
                        worst = left;
                    }
                    let right = left + 1;
                    if right < len
                        && cmp_better(arena, bucket_nodes[heap[worst]], bucket_nodes[heap[right]])
                            .is_lt()
                    {
                        worst = right;
                    }
                    if worst == pos {
                        break;
                    }
                    heap.swap(pos, worst);
                    pos = worst;
                }
            }
        }
        if heap.is_empty() {
            return 0;
        }
        heap.sort_unstable_by(|&a, &b| cmp_better(arena, bucket_nodes[a], bucket_nodes[b]));
        let added = take.min(heap.len());
        for &idx in heap.iter().take(added) {
            if marks[idx] == 0 {
                marks[idx] = 1;
                out.push(bucket_nodes[idx]);
            }
        }
        let extra = pareto_extra.min(heap.len().saturating_sub(added));
        for (k, &idx) in heap[added..added + extra].iter().enumerate() {
            pareto_out[k] = idx;
        }
        *pareto_count_out = extra;
        added
    }

    // Primary niche (reserve ~30% pure rollout slice on turn boundaries or ~20% choice-lookahead mid-turn)
    if primary_q > 0 || pareto_q > 0 {
        let has_rollout = use_rollout_scores
            && bucket_nodes
                .iter()
                .enumerate()
                .any(|(i, &nid)| marks[i] == 0 && arena.full_eval(nid).is_some_and(|f| f.rollout_score.is_some()));
        let has_choice_lookahead = !is_turn_boundary
            && !has_rollout
            && bucket_nodes
                .iter()
                .enumerate()
                .any(|(i, &nid)| marks[i] == 0 && arena.full_eval(nid).is_some_and(|f| f.has_choice_lookahead));
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
            let mut unmarked: Vec<usize> =
                (0..bucket_nodes.len()).filter(|&i| marks[i] == 0).collect();
            BeamWidthPortfolio::move_leading_band_to_tail(&mut unmarked, blended_q);
            for idx in unmarked {
                if taken_blended < take_blended {
                    marks[idx] = 1;
                    out.push(bucket_nodes[idx]);
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
            for idx in 0..bucket_nodes.len() {
                if marks[idx] != 0 {
                    continue;
                }
                if taken_blended < take_blended {
                    marks[idx] = 1;
                    out.push(bucket_nodes[idx]);
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
            selected_in_bucket += pick_top_k_niche(
                arena,
                bucket_nodes,
                &mut marks,
                take_roll,
                if pareto_q > 0 { PER_NICHE_PARETO_TOP_K } else { 0 },
                out,
                &mut niche_pareto[5],
                &mut niche_pareto_counts[5],
                |arena, a, b| {
                    let ra = arena
                        .full_eval(a)
                        .and_then(|f| f.rollout_score)
                        .unwrap_or(f64::NEG_INFINITY);
                    let rb = arena
                        .full_eval(b)
                        .and_then(|f| f.rollout_score)
                        .unwrap_or(f64::NEG_INFINITY);
                    rb.total_cmp(&ra)
                        .then_with(|| cmp_primary_desc(arena, a, b, true))
                },
            );
        } else if has_choice_lookahead
            && (choice_lookahead_q > 0 || pareto_q > 0)
            && selected_in_bucket < quota
        {
            let take_la = choice_lookahead_q.min(quota - selected_in_bucket);
            selected_in_bucket += pick_top_k_niche(
                arena,
                bucket_nodes,
                &mut marks,
                take_la,
                if pareto_q > 0 { PER_NICHE_PARETO_TOP_K } else { 0 },
                out,
                &mut niche_pareto[5],
                &mut niche_pareto_counts[5],
                |arena, a, b| {
                    let fa = arena.full_eval(a);
                    let fb = arena.full_eval(b);
                    let ha = fa.is_some_and(|f| f.has_choice_lookahead);
                    let hb = fb.is_some_and(|f| f.has_choice_lookahead);
                    hb.cmp(&ha)
                        .then_with(|| {
                            if ha && hb {
                                let ra = fa
                                    .and_then(|f| f.rollout_score)
                                    .unwrap_or(f64::NEG_INFINITY);
                                let rb = fb
                                    .and_then(|f| f.rollout_score)
                                    .unwrap_or(f64::NEG_INFINITY);
                                rb.total_cmp(&ra)
                            } else {
                                std::cmp::Ordering::Equal
                            }
                        })
                        .then_with(|| cmp_primary_desc(arena, a, b, use_rollout_scores))
                },
            );
        }
    }

    // Niche 2 — Survival / HP Preservation
    if (survival_q > 0 || pareto_q > 0) && selected_in_bucket < quota {
        let take_survival = survival_q.min(quota - selected_in_bucket);
        selected_in_bucket += pick_top_k_niche(
            arena,
            bucket_nodes,
            &mut marks,
            take_survival,
            if pareto_q > 0 { PER_NICHE_PARETO_TOP_K } else { 0 },
            out,
            &mut niche_pareto[1],
            &mut niche_pareto_counts[1],
            |arena, a, b| {
                let fa = &arena.get(a).fast_eval;
                let fb = &arena.get(b).fast_eval;
                let da = fa.death_save_use_count;
                let db = fb.death_save_use_count;
                let la = effective_net_hp_loss(arena, a, root);
                let lb = effective_net_hp_loss(arena, b, root);
                let hp_a = fa.player_hp.max(fa.projected_player_hp);
                let hp_b = fb.player_hp.max(fb.projected_player_hp);
                let ba = hp_a + arena.full_eval(a).map_or(fa.player_block, |f| f.effective_defense);
                let bb = hp_b + arena.full_eval(b).map_or(fb.player_block, |f| f.effective_defense);
                da.cmp(&db)
                    .then_with(|| la.cmp(&lb))
                    .then_with(|| bb.cmp(&ba))
                    .then_with(|| hp_b.cmp(&hp_a))
                    .then_with(|| fa.enemy_hp.cmp(&fb.enemy_hp))
                    .then_with(|| cmp_primary_desc(arena, a, b, use_rollout_scores))
            },
        );
    }

    // Niche 3 — Tempo / Kill Speed
    if (tempo_q > 0 || pareto_q > 0) && selected_in_bucket < quota {
        let take_tempo = tempo_q.min(quota - selected_in_bucket);
        selected_in_bucket += pick_top_k_niche(
            arena,
            bucket_nodes,
            &mut marks,
            take_tempo,
            if pareto_q > 0 { PER_NICHE_PARETO_TOP_K } else { 0 },
            out,
            &mut niche_pareto[2],
            &mut niche_pareto_counts[2],
            |arena, a, b| {
                let fa = &arena.get(a).fast_eval;
                let fb = &arena.get(b).fast_eval;
                let ra = fa.alive_enemy_count + fa.reviving_enemy_count;
                let rb = fb.alive_enemy_count + fb.reviving_enemy_count;
                let la = effective_net_hp_loss(arena, a, root);
                let lb = effective_net_hp_loss(arena, b, root);
                ra.cmp(&rb)
                    .then_with(|| fa.enemy_hp.cmp(&fb.enemy_hp))
                    .then_with(|| la.cmp(&lb))
                    .then_with(|| cmp_primary_desc(arena, a, b, use_rollout_scores))
            },
        );
    }

    // Niche 4 — Persistent Board Investment
    if (setup_q > 0 || pareto_q > 0) && selected_in_bucket < quota {
        let take_setup = setup_q.min(quota - selected_in_bucket);
        selected_in_bucket += pick_top_k_niche(
            arena,
            bucket_nodes,
            &mut marks,
            take_setup,
            if pareto_q > 0 { PER_NICHE_PARETO_TOP_K } else { 0 },
            out,
            &mut niche_pareto[3],
            &mut niche_pareto_counts[3],
            |arena, a, b| {
                let ba = arena.full_eval(a).map_or(0, |f| f.board_investment);
                let bb = arena.full_eval(b).map_or(0, |f| f.board_investment);
                let la = effective_net_hp_loss(arena, a, root);
                let lb = effective_net_hp_loss(arena, b, root);
                let sa = node_setup_score(arena, a);
                let sb = node_setup_score(arena, b);
                bb.cmp(&ba)
                    .then_with(|| la.cmp(&lb))
                    .then_with(|| sb.cmp(&sa))
                    .then_with(|| cmp_primary_desc(arena, a, b, use_rollout_scores))
            },
        );
    }

    // Niche 5a — Optionality / Control
    if (optionality_q > 0 || pareto_q > 0) && selected_in_bucket < quota {
        let take_opt = optionality_q.min(quota - selected_in_bucket);
        selected_in_bucket += pick_top_k_niche(
            arena,
            bucket_nodes,
            &mut marks,
            take_opt,
            if pareto_q > 0 { PER_NICHE_PARETO_TOP_K } else { 0 },
            out,
            &mut niche_pareto[4],
            &mut niche_pareto_counts[4],
            |arena, a, b| {
                let oa = node_optionality_score(arena, a);
                let ob = node_optionality_score(arena, b);
                let ba = arena.full_eval(a).map_or(0, |f| f.board_investment);
                let bb = arena.full_eval(b).map_or(0, |f| f.board_investment);
                let la = effective_net_hp_loss(arena, a, root);
                let lb = effective_net_hp_loss(arena, b, root);
                ob.cmp(&oa)
                    .then_with(|| bb.cmp(&ba))
                    .then_with(|| la.cmp(&lb))
                    .then_with(|| cmp_primary_desc(arena, a, b, use_rollout_scores))
            },
        );
    }

    // Niche 5b — True Full-Pool Pareto scanning top-32 runners-up from EACH niche via pre-extracted rows
    if pareto_q > 0 && selected_in_bucket < quota {
        #[derive(Clone, Copy, Default)]
        struct ParetoRow {
            loss: i32,
            enemy_hp: i32,
            rem: i32,
            board: i32,
            opt: i32,
            potion_cost: i32,
            primary: f64,
        }
        #[inline]
        fn extract_row(
            arena: &NodeArena,
            nid: NodeId,
            root: &RootEvalBaseline,
            use_rollout_scores: bool,
        ) -> ParetoRow {
            let n = arena.get(nid);
            let f = &n.fast_eval;
            let full = arena.full_eval(nid);
            ParetoRow {
                loss: effective_net_hp_loss(arena, nid, root),
                enemy_hp: f.enemy_hp,
                rem: f.alive_enemy_count + f.reviving_enemy_count,
                board: full.map_or(0, |d| d.board_investment),
                opt: f.energy.max(0)
                    + f.stars.max(0)
                    + full.map_or(0, |d| d.playable_card_count + d.next_turn_draw),
                potion_cost: n.potion_strategic_cost,
                primary: node_beam_score(arena, nid, use_rollout_scores),
            }
        }
        #[inline]
        fn row_dominates(other: &ParetoRow, cand: &ParetoRow) -> bool {
            if other.loss > cand.loss
                || other.enemy_hp > cand.enemy_hp
                || other.rem > cand.rem
                || other.board < cand.board
                || other.opt < cand.opt
                || other.potion_cost > cand.potion_cost
                || other.primary < cand.primary
            {
                return false;
            }
            other.loss < cand.loss
                || other.enemy_hp < cand.enemy_hp
                || other.rem < cand.rem
                || other.board > cand.board
                || other.opt > cand.opt
                || other.potion_cost < cand.potion_cost
                || other.primary > cand.primary
        }

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
                pool_rows[pareto_pool_count] =
                    extract_row(arena, bucket_nodes[idx], root, use_rollout_scores);
                pareto_pool_count += 1;
            }
        }

        let selected_rows: SmallVec<[ParetoRow; 128]> = out[bucket_out_start..]
            .iter()
            .map(|&id| extract_row(arena, id, root, use_rollout_scores))
            .collect();

        let pareto_budget = pareto_q.min(quota - selected_in_bucket);
        let mut pareto_added = 0usize;
        for p in 0..pareto_pool_count {
            if pareto_added >= pareto_budget {
                break;
            }
            let cand_row = &pool_rows[p];
            let dominated = selected_rows.iter().any(|r| row_dominates(r, cand_row))
                || pool_rows[..pareto_pool_count]
                    .iter()
                    .enumerate()
                    .any(|(j, r)| j != p && row_dominates(r, cand_row));
            if dominated {
                continue;
            }

            let cand_idx = pareto_pool[p];
            marks[cand_idx] = 1;
            out.push(bucket_nodes[cand_idx]);
            selected_in_bucket += 1;
            pareto_added += 1;
        }
    }

    // Fill any remaining seats in primary order
    for (i, &nid) in bucket_nodes.iter().enumerate() {
        if selected_in_bucket >= quota {
            break;
        }
        if marks[i] != 1 {
            marks[i] = 1;
            out.push(nid);
            selected_in_bucket += 1;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::arena::{PackedHandle, SearchNode};
    use crate::eval::{FastEvalSummary, FullEvalDetails};
    use crate::hash::StateHash;

    fn make_tied_node(
        arena: &mut NodeArena,
        hash_lo: u64,
        has_choice_lookahead: bool,
        rollout_score: Option<f64>,
    ) -> NodeId {
        let mut full = FullEvalDetails::default();
        full.beam_rank_score = 1234.0;
        full.has_choice_lookahead = has_choice_lookahead;
        full.rollout_score = rollout_score;
        let full_idx = arena.full_evals.len() as u32;
        arena.full_evals.push(full);

        let mut fast = FastEvalSummary::default();
        fast.cumulative_hp_lost = 5;
        fast.enemy_hp = 100;
        fast.alive_enemy_count = 1;
        fast.base_score = 1234.0;

        arena.push_node(SearchNode {
            parent: None,
            action: None,
            extra_end_turn: None,
            choices_offset: 0,
            choices_len: 0,
            state_handle: PackedHandle::default(),
            canonical_key_handle: PackedHandle::default(),
            state_hash: StateHash::from_parts(0, hash_lo),
            turn: 1,
            action_count: 1,
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
            is_terminal: false,
            boundary_rank: 0,
            fast_eval: fast,
            full_eval_idx: Some(full_idx),
        })
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
}
