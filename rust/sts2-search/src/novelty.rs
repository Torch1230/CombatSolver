use crate::arena::NodeId;
use crate::eval::{FastEvalSummary, FullEvalDetails};
use hashbrown::{HashMap, HashSet};
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;
use sts2_kernel::state::BattleState;
use sts2_types::{CardId, PowerId};

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd)]
pub enum BfwsFactKind {
    Scalar = 0,
    Energy = 1,
    Block = 2,
    Hp = 3,
    Zone = 4,
    Draw = 5,
    Power = 6,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd)]
pub struct BfwsFact {
    pub kind: BfwsFactKind,
    pub slot: i32,
    pub key: u32,
    pub value: i32,
    pub detail: i32,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NoveltySearchOptions {
    pub width: usize,
    pub max_open: usize,
    pub max_novelty_entries: usize,
    pub max_actions: usize,
    pub max_turns: i32,
    pub familiar_allowance: usize,
}

impl Default for NoveltySearchOptions {
    fn default() -> Self {
        Self {
            width: 2,
            max_open: 2048,
            max_novelty_entries: 65_536,
            max_actions: 48,
            max_turns: 16,
            familiar_allowance: 2,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NoveltyPortfolioBudget {
    pub time_divisor: i32,
    pub maximum_milliseconds: u64,
    pub maximum_expanded_nodes: usize,
}

impl Default for NoveltyPortfolioBudget {
    fn default() -> Self {
        Self {
            time_divisor: 2,
            maximum_milliseconds: 5_000,
            maximum_expanded_nodes: 2_500,
        }
    }
}

impl NoveltyPortfolioBudget {
    pub fn exploration(
        &self,
        profile: &crate::portfolio::SolverSearchProfile,
        act_ending_boss: bool,
    ) -> Option<crate::portfolio::SolverSearchProfile> {
        if self.time_divisor < 2 || self.maximum_milliseconds < 1 || self.maximum_expanded_nodes < 1
        {
            return None;
        }
        if profile.soft_time_budget_ms < 2_000 || profile.max_expanded_nodes < 1_000 {
            return None;
        }
        let divisor = if act_ending_boss {
            self.time_divisor.max(4) as u64
        } else {
            self.time_divisor as u64
        };
        let mut p = profile.clone();
        p.soft_time_budget_ms = self
            .maximum_milliseconds
            .min(profile.soft_time_budget_ms / divisor);
        p.max_expanded_nodes = self
            .maximum_expanded_nodes
            .min(profile.max_expanded_nodes / 4);
        Some(p)
    }

    pub fn remaining(
        profile: &crate::portfolio::SolverSearchProfile,
        elapsed_ms: u64,
        expanded_nodes: usize,
    ) -> Option<crate::portfolio::SolverSearchProfile> {
        if elapsed_ms >= profile.soft_time_budget_ms || expanded_nodes >= profile.max_expanded_nodes
        {
            return None;
        }
        let mut p = profile.clone();
        p.soft_time_budget_ms = profile.soft_time_budget_ms - elapsed_ms;
        p.max_expanded_nodes = profile.max_expanded_nodes - expanded_nodes;
        Some(p)
    }

    pub fn refinement_after_baseline(
        &self,
        profile: &crate::portfolio::SolverSearchProfile,
        baseline_ms: u64,
        baseline_expanded: usize,
        elapsed_ms: u64,
        act_ending_boss: bool,
    ) -> Option<crate::portfolio::SolverSearchProfile> {
        let maximum = self.exploration(profile, act_ending_boss)?;
        let remaining = Self::remaining(profile, elapsed_ms, baseline_expanded)?;
        let nodes = (baseline_expanded / 8)
            .min(maximum.max_expanded_nodes)
            .min(remaining.max_expanded_nodes);
        let ms = (baseline_ms / 8)
            .min(maximum.soft_time_budget_ms)
            .min(remaining.soft_time_budget_ms);
        if nodes < 1 || ms < 1 {
            return None;
        }
        let mut p = profile.clone();
        p.max_expanded_nodes = nodes;
        p.soft_time_budget_ms = ms;
        Some(p)
    }
}

/// Shared escape budget for familiar descendants of a novel ancestor (`BfwsEscapeBudget.cs`).
#[derive(Clone, Debug)]
pub struct BfwsEscapeBudget {
    maximum: usize,
    admitted: usize,
}

impl BfwsEscapeBudget {
    pub fn new(maximum: usize) -> Self {
        Self {
            maximum,
            admitted: 0,
        }
    }

    pub fn try_admit(&mut self) -> bool {
        if self.admitted >= self.maximum {
            return false;
        }
        self.admitted += 1;
        true
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
struct OpenPriority {
    width: i32,
    neg_score: f64,
    sequence: u64,
}

impl Eq for OpenPriority {}

impl PartialOrd for OpenPriority {
    fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
        Some(self.cmp(other))
    }
}

impl Ord for OpenPriority {
    fn cmp(&self, other: &Self) -> std::cmp::Ordering {
        self.width
            .cmp(&other.width)
            .then_with(|| self.neg_score.total_cmp(&other.neg_score))
            .then_with(|| self.sequence.cmp(&other.sequence))
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct OpenEntry<T> {
    priority: OpenPriority,
    value: T,
}

impl<T: Eq> PartialOrd for OpenEntry<T> {
    fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
        Some(self.cmp(other))
    }
}

impl<T: Eq> Ord for OpenEntry<T> {
    fn cmp(&self, other: &Self) -> std::cmp::Ordering {
        self.priority.cmp(&other.priority)
    }
}

/// Bounded double-ended priority queue matching `BfwsBoundedOpen.cs`.
pub struct BfwsBoundedOpen<T: Eq> {
    capacity: usize,
    entries: BTreeSet<OpenEntry<T>>,
}

impl<T: Eq> BfwsBoundedOpen<T> {
    pub fn new(capacity: usize) -> Self {
        Self {
            capacity: capacity.max(1),
            entries: BTreeSet::new(),
        }
    }

    #[inline]
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    #[inline]
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    pub fn enqueue(&mut self, value: T, width: i32, score: f64, sequence: u64) -> Option<T> {
        let entry = OpenEntry {
            priority: OpenPriority {
                width,
                neg_score: -score,
                sequence,
            },
            value,
        };
        self.entries.insert(entry);
        if self.entries.len() <= self.capacity {
            return None;
        }
        self.entries.pop_last().map(|worst| worst.value)
    }

    pub fn dequeue(&mut self) -> Option<T> {
        self.entries.pop_first().map(|best| best.value)
    }
}

/// 1:1 port of `BfwsPackedNovelty.cs` with parent-fact delta acceleration.
pub struct BfwsPackedNovelty {
    width: usize,
    maximum_entries: usize,
    atoms: HashMap<BfwsFact, u32, ahash::RandomState>,
    tables: HashMap<(i32, i32), HashSet<u64, ahash::RandomState>, ahash::RandomState>,
    facts: Vec<u32>,
    changed: Vec<usize>,
    parent_facts: HashSet<u32, ahash::RandomState>,
    parent_partition: Option<(i32, i32)>,
    parent_complete: bool,
    pub entries: usize,
    pub limit_reached: bool,
    pub unary: u64,
    pub binary: u64,
    pub familiar: u64,
}

impl BfwsPackedNovelty {
    pub fn new(width: usize, maximum_entries: usize) -> Self {
        Self {
            width: width.clamp(1, 2),
            maximum_entries: maximum_entries.max(64),
            atoms: HashMap::with_hasher(ahash::RandomState::with_seeds(1, 2, 3, 4)),
            tables: HashMap::with_hasher(ahash::RandomState::with_seeds(5, 6, 7, 8)),
            facts: Vec::with_capacity(64),
            changed: Vec::with_capacity(32),
            parent_facts: HashSet::with_hasher(ahash::RandomState::with_seeds(9, 10, 11, 12)),
            parent_partition: None,
            parent_complete: false,
            entries: 0,
            limit_reached: false,
            unary: 0,
            binary: 0,
            familiar: 0,
        }
    }

    pub fn prepare_parent(&mut self, partition: (i32, i32), facts: &[BfwsFact]) {
        self.parent_partition = Some(partition);
        self.parent_complete = !self.limit_reached;
        self.parent_facts.clear();
        for &f in facts {
            let next_id = self.atoms.len() as u32 + 1;
            let id = *self.atoms.entry(f).or_insert(next_id);
            self.parent_facts.insert(id);
        }
    }

    pub fn evaluate(&mut self, partition: (i32, i32), features: &[BfwsFact]) -> usize {
        self.facts.clear();
        for &f in features {
            let next_id = self.atoms.len() as u32 + 1;
            let id = *self.atoms.entry(f).or_insert(next_id);
            self.facts.push(id);
        }
        self.facts.sort_unstable();
        self.facts.dedup();

        let reuse_parent = self.parent_complete && self.parent_partition == Some(partition);
        self.changed.clear();
        let mut novelty = self.width + 1;

        for (i, &atom_id) in self.facts.iter().enumerate() {
            if reuse_parent && self.parent_facts.contains(&atom_id) {
                continue;
            }
            self.changed.push(i);
            if Self::record_tuple(
                atom_id as u64,
                partition,
                &mut self.tables,
                &mut self.entries,
                self.maximum_entries,
                &mut self.limit_reached,
            ) {
                novelty = 1;
            }
        }

        if self.width == 2 {
            let mut changed_cursor = 0usize;
            for i in 0..self.facts.len() {
                let is_changed =
                    changed_cursor < self.changed.len() && self.changed[changed_cursor] == i;
                if is_changed {
                    changed_cursor += 1;
                    for j in (i + 1)..self.facts.len() {
                        let tuple = ((self.facts[i] as u64) << 32) | (self.facts[j] as u64);
                        if Self::record_tuple(
                            tuple,
                            partition,
                            &mut self.tables,
                            &mut self.entries,
                            self.maximum_entries,
                            &mut self.limit_reached,
                        ) && novelty > 1
                        {
                            novelty = 2;
                        }
                    }
                } else {
                    for &c_idx in &self.changed[changed_cursor..] {
                        let tuple = ((self.facts[i] as u64) << 32) | (self.facts[c_idx] as u64);
                        if Self::record_tuple(
                            tuple,
                            partition,
                            &mut self.tables,
                            &mut self.entries,
                            self.maximum_entries,
                            &mut self.limit_reached,
                        ) && novelty > 1
                        {
                            novelty = 2;
                        }
                    }
                }
            }
        }

        if novelty == 1 {
            self.unary += 1;
        } else if novelty == 2 && self.width == 2 {
            self.binary += 1;
        } else {
            self.familiar += 1;
        }
        novelty
    }

    fn record_tuple(
        tuple: u64,
        partition: (i32, i32),
        tables: &mut HashMap<(i32, i32), HashSet<u64, ahash::RandomState>, ahash::RandomState>,
        entries: &mut usize,
        maximum_entries: usize,
        limit_reached: &mut bool,
    ) -> bool {
        let seen = tables.entry(partition).or_insert_with(|| {
            HashSet::with_hasher(ahash::RandomState::with_seeds(13, 14, 15, 16))
        });
        if seen.contains(&tuple) {
            return false;
        }
        if *entries >= maximum_entries {
            *limit_reached = true;
            return true;
        }
        seen.insert(tuple);
        *entries += 1;
        true
    }
}

pub fn capture_novelty_facts(
    state: &BattleState,
    fast: &FastEvalSummary,
    full: &FullEvalDetails,
    out: &mut Vec<BfwsFact>,
) {
    out.clear();
    let scalars = [
        fast.energy,
        fast.stars,
        fast.hand_count,
        full.playable_attack_count,
        full.reachable_hand_value,
        full.persistent_buff_value,
        full.latent_setup_value,
        full.future_resource_value,
        full.retained_attack_value,
        full.replay_potential_value,
        full.delayed_damage_value,
        full.reactive_damage_value,
        full.enemy_strength_suppression,
        full.enemy_weak_turns,
        full.enemy_vulnerable_turns,
    ];
    for (i, &val) in scalars.iter().enumerate() {
        if val > 0 {
            let bucket = if i < 4 || i >= 14 {
                val.min(7)
            } else {
                (1 + (val as u32).ilog2() as i32).min(7)
            };
            out.push(BfwsFact {
                kind: BfwsFactKind::Scalar,
                slot: i as i32,
                key: 0,
                value: bucket,
                detail: 0,
            });
        }
    }
    out.push(BfwsFact {
        kind: BfwsFactKind::Energy,
        slot: 0,
        key: 0,
        value: fast.energy,
        detail: 0,
    });
    out.push(BfwsFact {
        kind: BfwsFactKind::Block,
        slot: 0,
        key: 0,
        value: fast.player_block / 5,
        detail: 0,
    });
    out.push(BfwsFact {
        kind: BfwsFactKind::Hp,
        slot: 0,
        key: 0,
        value: fast.player_hp / 5,
        detail: 0,
    });

    let zones = [
        &state.player.hand[..],
        &state.player.draw[..],
        &state.player.discard[..],
        &state.player.exhaust[..],
    ];
    let mut zone_counts: HashMap<(CardId, i32), i32, ahash::RandomState> =
        HashMap::with_hasher(ahash::RandomState::with_seeds(21, 22, 23, 24));
    for (zone_idx, cards) in zones.iter().enumerate() {
        zone_counts.clear();
        for &ch in *cards {
            if let Some(c) = state.card(ch) {
                *zone_counts.entry((c.id, c.upgrade_level)).or_insert(0) += 1;
            }
        }
        for (&(cid, upg), &cnt) in &zone_counts {
            out.push(BfwsFact {
                kind: BfwsFactKind::Zone,
                slot: zone_idx as i32,
                key: cid as u32,
                value: upg,
                detail: cnt.min(3),
            });
        }
    }

    for (i, &ch) in state.player.draw.iter().take(3).enumerate() {
        if let Some(c) = state.card(ch) {
            out.push(BfwsFact {
                kind: BfwsFactKind::Draw,
                slot: i as i32,
                key: c.id as u32,
                value: 0,
                detail: 0,
            });
        }
    }

    for cr in &state.creatures {
        let owner_slot = if cr.handle == state.player.creature_handle {
            0
        } else {
            1
        };
        for pw in &cr.powers {
            let pkey: PowerId = pw.id;
            out.push(BfwsFact {
                kind: BfwsFactKind::Power,
                slot: owner_slot,
                key: pkey as u32,
                value: pw.amount,
                detail: 0,
            });
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct NoveltyOpenNode {
    pub node_id: NodeId,
    pub escape_idx: Option<usize>,
}
