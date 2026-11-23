use crate::hash::StateHash;
use hashbrown::HashMap;
use rayon::prelude::*;
use smallvec::SmallVec;

pub const TT_SHARD_COUNT: usize = 256;

/// 8-dimensional Pareto non-dominance label matching `HotTranspositionTable.cs`.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TranspositionLabel {
    pub forced_used_mask: u64,
    pub death_save_use_count: i32,
    pub potion_count: i32,
    pub potion_strategic_cost: i32,
    pub future_sold_hp: i32,
    pub cumulative_player_hp_lost: i32,
    pub action_count: i32,
    pub score: f64,
}

impl TranspositionLabel {
    #[inline]
    pub fn dominates(&self, other: &Self) -> bool {
        (self.forced_used_mask & other.forced_used_mask) == other.forced_used_mask
            && self.death_save_use_count <= other.death_save_use_count
            && self.potion_count <= other.potion_count
            && self.potion_strategic_cost <= other.potion_strategic_cost
            && self.future_sold_hp <= other.future_sold_hp
            && self.cumulative_player_hp_lost <= other.cumulative_player_hp_lost
            && self.score >= other.score
            && (self.action_count <= other.action_count || self.score > other.score)
    }
}

/// Inline single-label Pareto frontier with `SmallVec` tail for rare multi-label states.
#[derive(Clone, Debug)]
pub struct TranspositionFrontier {
    single: TranspositionLabel,
    tail: SmallVec<[TranspositionLabel; 2]>,
    has_tail: bool,
}

impl TranspositionFrontier {
    #[inline]
    pub fn new(first: TranspositionLabel) -> Self {
        Self {
            single: first,
            tail: SmallVec::new(),
            has_tail: false,
        }
    }

    #[inline]
    pub fn is_dominated_by(&self, next: &TranspositionLabel) -> bool {
        if !self.has_tail {
            return self.single.dominates(next);
        }
        self.tail.iter().any(|cur| cur.dominates(next))
    }

    #[inline]
    pub fn try_accept(&mut self, next: TranspositionLabel) -> bool {
        if !self.has_tail {
            if self.single.dominates(&next) {
                return false;
            }
            if next.dominates(&self.single) {
                self.single = next;
                return true;
            }
            self.tail.push(self.single);
            self.tail.push(next);
            self.has_tail = true;
            return true;
        }

        for cur in &self.tail {
            if cur.dominates(&next) {
                return false;
            }
        }
        self.tail.retain(|cur| !next.dominates(cur));
        self.tail.push(next);
        if self.tail.len() == 1 {
            self.single = self.tail[0];
            self.tail.clear();
            self.has_tail = false;
        }
        true
    }
}

#[derive(Clone, Debug)]
struct TtBucketEntry {
    generation: u32,
    frontier: TranspositionFrontier,
}

#[derive(Debug)]
pub struct TtShard {
    entries: HashMap<StateHash, TtBucketEntry, ahash::RandomState>,
    active_entries: usize,
}

impl Default for TtShard {
    fn default() -> Self {
        Self {
            entries: HashMap::with_hasher(ahash::RandomState::with_seeds(
                0x517c_c1b7_2722_0a95,
                0x9e37_79b9_7f4a_7c15,
                0xbf58_476d_1ce4_e5b9,
                0x94d0_49bb_1331_11eb,
            )),
            active_entries: 0,
        }
    }
}

impl TtShard {
    fn clear_all(&mut self) {
        self.entries.clear();
        self.active_entries = 0;
    }

    #[inline]
    pub fn probe_dominated(
        &self,
        generation: u32,
        hash: StateHash,
        label: &TranspositionLabel,
    ) -> bool {
        let Some(entry) = self.entries.get(&hash) else {
            return false;
        };
        if entry.generation != generation {
            return false;
        }
        entry.frontier.is_dominated_by(label)
    }

    pub fn try_insert(
        &mut self,
        generation: u32,
        entry_limit_per_shard: usize,
        hash: StateHash,
        label: TranspositionLabel,
    ) -> bool {
        if let Some(entry) = self.entries.get_mut(&hash) {
            if entry.generation == generation {
                return entry.frontier.try_accept(label);
            }
            if self.active_entries >= entry_limit_per_shard {
                return true;
            }
            entry.generation = generation;
            entry.frontier = TranspositionFrontier::new(label);
            self.active_entries += 1;
            return true;
        }
        if self.active_entries >= entry_limit_per_shard {
            return true;
        }
        self.entries.insert(
            hash,
            TtBucketEntry {
                generation,
                frontier: TranspositionFrontier::new(label),
            },
        );
        self.active_entries += 1;
        true
    }
}

/// 256-sharded Pareto Transposition Table with O(1) generation reset keyed by 128-bit `StateHash`.
#[derive(Debug)]
pub struct ShardedParetoTT {
    shards: Vec<TtShard>,
    generation: u32,
    entry_limit_per_shard: usize,
    pub branches_pruned: u64,
}

impl ShardedParetoTT {
    pub fn new(total_entry_limit: usize) -> Self {
        let mut shards = Vec::with_capacity(TT_SHARD_COUNT);
        for _ in 0..TT_SHARD_COUNT {
            shards.push(TtShard::default());
        }
        let per_shard = (total_entry_limit / TT_SHARD_COUNT).max(64);
        Self {
            shards,
            generation: 1,
            entry_limit_per_shard: per_shard,
            branches_pruned: 0,
        }
    }

    #[inline]
    pub fn generation(&self) -> u32 {
        self.generation
    }

    /// O(1) pass reset via generation bump (clears tables only when generation wraps or tables grow large).
    pub fn reset_generation(&mut self) {
        if self.generation == u32::MAX {
            self.generation = 1;
            for shard in &mut self.shards {
                shard.clear_all();
            }
            return;
        }
        self.generation += 1;
        let cap4 = self.entry_limit_per_shard * 4;
        for shard in &mut self.shards {
            shard.active_entries = 0;
            if shard.entries.len() > cap4 {
                shard.clear_all();
            }
        }
    }

    /// Lock-free read-only probe against committed entries in the current generation.
    #[inline]
    pub fn probe_dominated(&self, hash: StateHash, label: &TranspositionLabel) -> bool {
        let shard = &self.shards[hash.shard_index()];
        shard.probe_dominated(self.generation, hash, label)
    }

    /// Inserts a single candidate sequentially into the shard for `hash`.
    #[inline]
    pub fn try_insert_single(&mut self, hash: StateHash, label: TranspositionLabel) -> bool {
        let shard = &mut self.shards[hash.shard_index()];
        let accepted = shard.try_insert(
            self.generation,
            self.entry_limit_per_shard,
            hash,
            label,
        );
        if !accepted {
            self.branches_pruned += 1;
        }
        accepted
    }

    /// Zero-allocation in-place variant of `insert_sharded_batch_reused` reusing `shard_accepted`
    /// scratch buffers across waves and passes.
    /// Each shard's items MUST already be sorted by `(parent_rank, action_ord, choice_ord)`!
    pub fn insert_sharded_batch_in_place<T: Send>(
        &mut self,
        shard_buckets: &mut [Vec<(usize, StateHash, TranspositionLabel, T)>],
        shard_accepted: &mut [Vec<(usize, T)>],
        is_parallel: bool,
        out: &mut Vec<(usize, T)>,
    ) {
        let generation = self.generation;
        let cap = self.entry_limit_per_shard;

        out.clear();
        if is_parallel {
            let pruned_total: u64 = self
                .shards
                .par_iter_mut()
                .zip(shard_buckets.par_iter_mut())
                .zip(shard_accepted.par_iter_mut())
                .map(|((shard, items), acc)| {
                    acc.clear();
                    if items.is_empty() {
                        return 0u64;
                    }
                    let mut pruned = 0u64;
                    for (sort_idx, hash, label, payload) in items.drain(..) {
                        if shard.try_insert(generation, cap, hash, label) {
                            acc.push((sort_idx, payload));
                        } else {
                            pruned += 1;
                        }
                    }
                    pruned
                })
                .sum();
            self.branches_pruned += pruned_total;
            for acc in shard_accepted.iter_mut() {
                out.extend(acc.drain(..));
            }
        } else {
            for (shard, items) in self.shards.iter_mut().zip(shard_buckets.iter_mut()) {
                for (sort_idx, hash, label, payload) in items.drain(..) {
                    if shard.try_insert(generation, cap, hash, label) {
                        out.push((sort_idx, payload));
                    } else {
                        self.branches_pruned += 1;
                    }
                }
            }
        }
        out.sort_unstable_by_key(|(idx, _)| *idx);
    }

    /// Deterministically deduplicates and inserts shard-bucketed candidates in parallel across all 256 shards,
    /// draining `shard_buckets` in-place and appending accepted `(sort_idx, T)` items into `out` (then sorting `out`).
    /// Each shard's items MUST already be sorted by `(parent_rank, action_ord, choice_ord)`!
    pub fn insert_sharded_batch_reused<T>(
        &mut self,
        shard_buckets: &mut [Vec<(usize, StateHash, TranspositionLabel, T)>],
        is_parallel: bool,
        out: &mut Vec<(usize, T)>,
    ) where
        T: Send,
    {
        let generation = self.generation;
        let cap = self.entry_limit_per_shard;

        out.clear();
        if is_parallel {
            let shard_outputs: Vec<(Vec<(usize, T)>, u64)> = self
                .shards
                .par_iter_mut()
                .zip(shard_buckets.par_iter_mut())
                .map(|(shard, items)| {
                    if items.is_empty() {
                        return (Vec::new(), 0u64);
                    }
                    let mut accepted = Vec::with_capacity(items.len());
                    let mut pruned = 0u64;
                    for (sort_idx, hash, label, payload) in items.drain(..) {
                        if shard.try_insert(generation, cap, hash, label) {
                            accepted.push((sort_idx, payload));
                        } else {
                            pruned += 1;
                        }
                    }
                    (accepted, pruned)
                })
                .collect();
            for (mut acc, pruned) in shard_outputs {
                self.branches_pruned += pruned;
                out.append(&mut acc);
            }
        } else {
            for (shard, items) in self.shards.iter_mut().zip(shard_buckets.iter_mut()) {
                for (sort_idx, hash, label, payload) in items.drain(..) {
                    if shard.try_insert(generation, cap, hash, label) {
                        out.push((sort_idx, payload));
                    } else {
                        self.branches_pruned += 1;
                    }
                }
            }
        }
        out.sort_unstable_by_key(|(idx, _)| *idx);
    }

    /// Deterministically deduplicates and inserts shard-bucketed candidates in parallel across all 256 shards.
    /// Each shard's items MUST already be sorted by `(parent_rank, action_ord, choice_ord)`!
    pub fn insert_sharded_batch<T, F>(
        &mut self,
        mut shard_buckets: Vec<Vec<(usize, StateHash, TranspositionLabel, T)>>,
        is_parallel: bool,
        mut on_pruned: F,
    ) -> Vec<(usize, T)>
    where
        T: Send,
        F: FnMut(usize),
    {
        let before = self.branches_pruned;
        let mut total_accepted = Vec::new();
        self.insert_sharded_batch_reused(&mut shard_buckets, is_parallel, &mut total_accepted);
        let pruned_delta = (self.branches_pruned - before) as usize;
        if pruned_delta > 0 {
            on_pruned(pruned_delta);
        }
        total_accepted
    }
}
