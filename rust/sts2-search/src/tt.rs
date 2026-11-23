use crate::hash::StateHash;
use hashbrown::HashMap;
use rayon::prelude::*;
use smallvec::SmallVec;

pub const TT_SHARD_COUNT: usize = 256;

/// 6-dimensional Pareto non-dominance label matching `CombatBeamSolver.Transpositions.cs`.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TranspositionLabel {
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
        self.potion_count <= other.potion_count
            && self.potion_strategic_cost <= other.potion_strategic_cost
            && self.future_sold_hp <= other.future_sold_hp
            && self.cumulative_player_hp_lost <= other.cumulative_player_hp_lost
            && self.action_count <= other.action_count
            && self.score >= other.score
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
    key_offset: u32,
    key_len: u32,
    frontier: TranspositionFrontier,
}

#[derive(Debug)]
pub struct TtShard {
    entries: HashMap<StateHash, SmallVec<[TtBucketEntry; 1]>, ahash::RandomState>,
    key_arena: Vec<u8>,
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
            key_arena: Vec::with_capacity(4096),
            active_entries: 0,
        }
    }
}

impl TtShard {
    fn clear_all(&mut self) {
        self.entries.clear();
        self.key_arena.clear();
        self.active_entries = 0;
    }

    #[inline]
    pub fn probe_dominated(
        &self,
        generation: u32,
        hash: StateHash,
        canonical_key: &[u8],
        label: &TranspositionLabel,
    ) -> bool {
        let Some(bucket) = self.entries.get(&hash) else {
            return false;
        };
        for entry in bucket {
            if entry.generation != generation {
                continue;
            }
            let start = entry.key_offset as usize;
            let end = start + entry.key_len as usize;
            if &self.key_arena[start..end] == canonical_key {
                return entry.frontier.is_dominated_by(label);
            }
        }
        false
    }

    pub fn try_insert(
        &mut self,
        generation: u32,
        entry_limit_per_shard: usize,
        hash: StateHash,
        canonical_key: &[u8],
        label: TranspositionLabel,
    ) -> bool {
        let bucket = self.entries.entry(hash).or_default();
        for entry in bucket.iter_mut() {
            if entry.generation != generation {
                continue;
            }
            let start = entry.key_offset as usize;
            let end = start + entry.key_len as usize;
            if &self.key_arena[start..end] == canonical_key {
                return entry.frontier.try_accept(label);
            }
        }
        // Evict stale-generation slot inside this bucket if present, before appending.
        if let Some(stale) = bucket.iter_mut().find(|e| e.generation != generation) {
            let key_offset = self.key_arena.len() as u32;
            let key_len = canonical_key.len() as u32;
            self.key_arena.extend_from_slice(canonical_key);
            stale.generation = generation;
            stale.key_offset = key_offset;
            stale.key_len = key_len;
            stale.frontier = TranspositionFrontier::new(label);
            self.active_entries += 1;
            return true;
        }
        if self.active_entries >= entry_limit_per_shard {
            // Mirror C# AtTranspositionEntryLimit: allow candidate without growing table further.
            return true;
        }
        let key_offset = self.key_arena.len() as u32;
        let key_len = canonical_key.len() as u32;
        self.key_arena.extend_from_slice(canonical_key);
        bucket.push(TtBucketEntry {
            generation,
            key_offset,
            key_len,
            frontier: TranspositionFrontier::new(label),
        });
        self.active_entries += 1;
        true
    }
}

/// 256-sharded Pareto Transposition Table with O(1) generation reset and exact byte-equality verification.
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

    /// O(1) pass reset via generation bump (clears arenas only if generation wraps or key arenas grow huge).
    pub fn reset_generation(&mut self) {
        if self.generation == u32::MAX {
            self.generation = 1;
            for shard in &mut self.shards {
                shard.clear_all();
            }
            return;
        }
        self.generation += 1;
        for shard in &mut self.shards {
            shard.active_entries = 0;
            if shard.key_arena.len() > 2 * 1024 * 1024 {
                shard.clear_all();
            }
        }
    }

    /// Lock-free read-only probe against committed entries in the current generation.
    #[inline]
    pub fn probe_dominated(
        &self,
        hash: StateHash,
        canonical_key: &[u8],
        label: &TranspositionLabel,
    ) -> bool {
        let shard = &self.shards[hash.shard_index()];
        shard.probe_dominated(self.generation, hash, canonical_key, label)
    }

    /// Deterministically deduplicates and inserts shard-bucketed candidates in parallel across all 256 shards.
    /// Each shard's items MUST already be sorted by `(parent_rank, action_ord, choice_ord)`!
    pub fn insert_sharded_batch<'a, T, F>(
        &mut self,
        shard_buckets: Vec<Vec<(usize, StateHash, &'a [u8], TranspositionLabel, T)>>,
        is_parallel: bool,
        mut on_pruned: F,
    ) -> Vec<(usize, T)>
    where
        T: Send,
        F: FnMut(usize),
    {
        let generation = self.generation;
        let cap = self.entry_limit_per_shard;

        let mut shard_outputs: Vec<(Vec<(usize, T)>, u64)> = if is_parallel {
            self.shards
                .par_iter_mut()
                .zip(shard_buckets.into_par_iter())
                .map(|(shard, items)| {
                    let mut accepted = Vec::with_capacity(items.len());
                    let mut pruned = 0u64;
                    for (sort_idx, hash, key_bytes, label, payload) in items {
                        if shard.try_insert(generation, cap, hash, key_bytes, label) {
                            accepted.push((sort_idx, payload));
                        } else {
                            pruned += 1;
                        }
                    }
                    (accepted, pruned)
                })
                .collect()
        } else {
            self.shards
                .iter_mut()
                .zip(shard_buckets.into_iter())
                .map(|(shard, items)| {
                    let mut accepted = Vec::with_capacity(items.len());
                    let mut pruned = 0u64;
                    for (sort_idx, hash, key_bytes, label, payload) in items {
                        if shard.try_insert(generation, cap, hash, key_bytes, label) {
                            accepted.push((sort_idx, payload));
                        } else {
                            pruned += 1;
                        }
                    }
                    (accepted, pruned)
                })
                .collect()
        };

        let mut total_accepted = Vec::new();
        for (mut acc, pruned) in shard_outputs.drain(..) {
            self.branches_pruned += pruned;
            on_pruned(pruned as usize);
            total_accepted.append(&mut acc);
        }
        total_accepted.sort_unstable_by_key(|(idx, _)| *idx);
        total_accepted
    }
}
