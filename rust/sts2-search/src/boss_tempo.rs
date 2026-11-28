use crate::arena::NodeId;
use crate::eval::weights;
use crate::hash::StateHash;
use crate::portfolio::SolverSearchProfile;
use crate::tt::{TranspositionFrontier, TranspositionLabel};
use crate::PlannedAction;
use hashbrown::HashMap;
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BossTempoSearchOptions {
    pub discrepancy_allowance: usize,
    pub scout_turns: i32,
}

impl Default for BossTempoSearchOptions {
    fn default() -> Self {
        Self {
            discrepancy_allowance: 3,
            scout_turns: 1,
        }
    }
}

impl BossTempoSearchOptions {
    pub fn additional_budget(configured: &SolverSearchProfile, milliseconds: u64) -> SolverSearchProfile {
        let mut p = configured.clone();
        p.max_expanded_nodes = (configured.max_expanded_nodes / 5).max(1);
        p.soft_time_budget_ms = (milliseconds / 5).max(1);
        p
    }

    #[inline]
    pub fn rank_base(
        mut score: f64,
        cumulative_loss: i32,
        sold_hp: i32,
        projected_hp: i32,
        player_dead: bool,
    ) -> f64 {
        if player_dead {
            return score;
        }
        if projected_hp <= 0 {
            score = score - weights::DEATH_PENALTY + projected_hp as f64 * weights::HP;
        }
        score + cumulative_loss as f64 * weights::HP - sold_hp as f64 * weights::SOLD_HP_PENALTY
    }
}

#[derive(Clone, Debug)]
pub struct BossTempoPrefix {
    pub actions: Vec<PlannedAction>,
    pub state_hash: StateHash,
    pub forced_uses_satisfied: bool,
    pub eliminated_enemies: i32,
    pub potion_count: usize,
    pub rank: f64,
}

/// Discrepancy-indexed transposition table for BossTempo depth-first search (`CombatBeamSolver.BossTempo.cs`).
pub struct BossTempoTranspositions {
    expanded: HashMap<(StateHash, usize), TranspositionFrontier, ahash::RandomState>,
    entry_limit: usize,
    pub branches_pruned: usize,
}

impl BossTempoTranspositions {
    pub fn new(entry_limit: usize) -> Self {
        Self {
            expanded: HashMap::with_hasher(ahash::RandomState::with_seeds(41, 42, 43, 44)),
            entry_limit: entry_limit.max(256),
            branches_pruned: 0,
        }
    }

    pub fn try_mark(
        &mut self,
        state_hash: StateHash,
        remaining_discrepancies: usize,
        label: TranspositionLabel,
    ) -> bool {
        let key = (state_hash, remaining_discrepancies);
        if let Some(frontier) = self.expanded.get_mut(&key) {
            let ok = frontier.try_accept(label);
            if !ok {
                self.branches_pruned += 1;
            }
            return ok;
        }
        if self.expanded.len() >= self.entry_limit {
            return true;
        }
        self.expanded.insert(key, TranspositionFrontier::new(label));
        true
    }
}

#[derive(Clone, Copy, Debug)]
pub struct BossTempoPendingItem {
    pub node_id: NodeId,
    pub remaining_discrepancies: usize,
}

/// Retains top BossTempo scout prefixes per potion-count bucket matching `CombatSearchCoordinator.BossTempo.cs`.
pub fn retain_boss_tempo_prefixes(prefixes: &mut Vec<BossTempoPrefix>, candidate: BossTempoPrefix) {
    if let Some(idx) = prefixes
        .iter()
        .position(|p| p.state_hash == candidate.state_hash && p.potion_count == candidate.potion_count)
    {
        if candidate.rank <= prefixes[idx].rank {
            return;
        }
        prefixes.remove(idx);
    }
    prefixes.push(candidate);

    let mut buckets: [Vec<BossTempoPrefix>; 4] = [Vec::new(), Vec::new(), Vec::new(), Vec::new()];
    for p in prefixes.drain(..) {
        buckets[p.potion_count.min(3)].push(p);
    }
    for bucket in &mut buckets {
        bucket.sort_by(|a, b| {
            b.forced_uses_satisfied
                .cmp(&a.forced_uses_satisfied)
                .then_with(|| b.eliminated_enemies.cmp(&a.eliminated_enemies))
                .then_with(|| b.rank.total_cmp(&a.rank))
        });
        bucket.truncate(6);
        prefixes.extend(bucket.drain(..));
    }
}
