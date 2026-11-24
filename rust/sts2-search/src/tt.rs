use crate::hash::StateHash;
use rayon::prelude::*;
use smallvec::SmallVec;

pub const TT_SHARD_COUNT: usize = 256;
const INITIAL_SHARD_SLOTS: usize = 256;

/// Compact 32-byte 8-dimensional Pareto non-dominance label matching `HotTtLabel`.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TranspositionLabel {
    pub forced_used_mask: u64,
    pub score: f64,
    pub cumulative_player_hp_lost: i32,
    pub potion_strategic_cost: i16,
    pub future_sold_hp: i16,
    pub action_count: u16,
    pub potion_count: u8,
    pub death_save_use_count: u8,
}

impl TranspositionLabel {
    #[inline]
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        forced_used_mask: u64,
        death_save_use_count: i32,
        potion_count: u16,
        potion_strategic_cost: i32,
        future_sold_hp: i32,
        cumulative_player_hp_lost: i32,
        action_count: u16,
        score: f64,
    ) -> Self {
        Self {
            forced_used_mask,
            score,
            cumulative_player_hp_lost,
            potion_strategic_cost: potion_strategic_cost
                .clamp(i16::MIN as i32, i16::MAX as i32) as i16,
            future_sold_hp: future_sold_hp.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
            action_count,
            potion_count: potion_count.min(u8::MAX as u16) as u8,
            death_save_use_count: death_save_use_count.clamp(0, u8::MAX as i32) as u8,
        }
    }

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

#[derive(Clone, Copy, Debug)]
struct TtSlot {
    hash: StateHash,
    single: TranspositionLabel,
    generation: u32,
    overflow_idx: u32,
}

const _: () = assert!(std::mem::size_of::<TtSlot>() <= 64);

impl TtSlot {
    #[inline(always)]
    const fn empty() -> Self {
        Self {
            hash: StateHash(0),
            single: TranspositionLabel {
                forced_used_mask: 0,
                score: 0.0,
                cumulative_player_hp_lost: 0,
                potion_strategic_cost: 0,
                future_sold_hp: 0,
                action_count: 0,
                potion_count: 0,
                death_save_use_count: 0,
            },
            generation: 0,
            overflow_idx: u32::MAX,
        }
    }
}

#[derive(Debug)]
pub struct TtShard {
    slots: Vec<TtSlot>,
    mask: usize,
    overflow: Vec<SmallVec<[TranspositionLabel; 2]>>,
    active_entries: usize,
}

impl Default for TtShard {
    fn default() -> Self {
        Self {
            slots: vec![TtSlot::empty(); INITIAL_SHARD_SLOTS],
            mask: INITIAL_SHARD_SLOTS - 1,
            overflow: Vec::new(),
            active_entries: 0,
        }
    }
}

impl TtShard {
    fn clear_all(&mut self, max_kept_slots: usize) {
        self.active_entries = 0;
        self.overflow.clear();
        if self.slots.len() > max_kept_slots {
            self.slots.resize(INITIAL_SHARD_SLOTS, TtSlot::empty());
            self.slots.shrink_to(INITIAL_SHARD_SLOTS);
            self.mask = INITIAL_SHARD_SLOTS - 1;
        }
        for slot in &mut self.slots {
            slot.generation = 0;
        }
    }

    #[cold]
    #[inline(never)]
    fn grow(&mut self, generation: u32) {
        let new_len = (self.slots.len() * 2).max(INITIAL_SHARD_SLOTS);
        let new_mask = new_len - 1;
        let mut new_slots = vec![TtSlot::empty(); new_len];
        for &slot in &self.slots {
            if slot.generation != generation {
                continue;
            }
            let mut idx = (slot.hash.0 as usize) & new_mask;
            while new_slots[idx].generation != 0 {
                idx = (idx + 1) & new_mask;
            }
            new_slots[idx] = slot;
        }
        self.slots = new_slots;
        self.mask = new_mask;
    }

    #[inline]
    pub fn probe_dominated(
        &self,
        generation: u32,
        hash: StateHash,
        label: &TranspositionLabel,
    ) -> bool {
        let mut idx = (hash.0 as usize) & self.mask;
        loop {
            let slot = &self.slots[idx];
            if slot.generation != generation {
                return false;
            }
            if slot.hash == hash {
                return if slot.overflow_idx == u32::MAX {
                    slot.single.dominates(label)
                } else {
                    self.overflow[slot.overflow_idx as usize]
                        .iter()
                        .any(|cur| cur.dominates(label))
                };
            }
            idx = (idx + 1) & self.mask;
        }
    }

    pub fn try_insert(
        &mut self,
        generation: u32,
        entry_limit_per_shard: usize,
        hash: StateHash,
        label: TranspositionLabel,
    ) -> bool {
        let mut idx = (hash.0 as usize) & self.mask;
        loop {
            let slot = &mut self.slots[idx];
            if slot.generation != generation {
                break;
            }
            if slot.hash == hash {
                if slot.overflow_idx == u32::MAX {
                    if slot.single.dominates(&label) {
                        return false;
                    }
                    if label.dominates(&slot.single) {
                        slot.single = label;
                        return true;
                    }
                    let ov_idx = self.overflow.len() as u32;
                    self.overflow.push(smallvec::smallvec![slot.single, label]);
                    slot.overflow_idx = ov_idx;
                    return true;
                }
                let tail = &mut self.overflow[slot.overflow_idx as usize];
                for cur in tail.iter() {
                    if cur.dominates(&label) {
                        return false;
                    }
                }
                tail.retain(|cur| !label.dominates(cur));
                tail.push(label);
                if tail.len() == 1 {
                    slot.single = tail[0];
                    slot.overflow_idx = u32::MAX;
                    tail.clear();
                }
                return true;
            }
            idx = (idx + 1) & self.mask;
        }

        if self.active_entries >= entry_limit_per_shard {
            return true;
        }

        if (self.active_entries + 1) * 4 >= self.slots.len() * 3 {
            self.grow(generation);
            idx = (hash.0 as usize) & self.mask;
            while self.slots[idx].generation == generation {
                idx = (idx + 1) & self.mask;
            }
        }

        self.slots[idx] = TtSlot {
            hash,
            single: label,
            generation,
            overflow_idx: u32::MAX,
        };
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
    pub fn set_entry_limit(&mut self, total_entry_limit: usize) {
        self.entry_limit_per_shard = (total_entry_limit / TT_SHARD_COUNT).max(64);
    }

    #[inline]
    pub fn generation(&self) -> u32 {
        self.generation
    }

    /// O(1) pass reset via generation bump (shrinks oversized tables back to initial size).
    pub fn reset_generation(&mut self) {
        let max_kept_slots = (self.entry_limit_per_shard * 4)
            .next_power_of_two()
            .max(INITIAL_SHARD_SLOTS);
        if self.generation == u32::MAX {
            self.generation = 1;
            for shard in &mut self.shards {
                shard.clear_all(max_kept_slots);
            }
            return;
        }
        self.generation += 1;
        for shard in &mut self.shards {
            shard.active_entries = 0;
            shard.overflow.clear();
            if shard.slots.len() > max_kept_slots {
                shard.slots.resize(INITIAL_SHARD_SLOTS, TtSlot::empty());
                shard.slots.shrink_to(INITIAL_SHARD_SLOTS);
                shard.mask = INITIAL_SHARD_SLOTS - 1;
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

    /// Deterministically inserts shard-bucketed candidates across all 256 shards and writes
    /// acceptance decisions into `accepted_flags[sort_idx]`, draining `shard_buckets` in-place.
    /// Each shard's items MUST already be sorted by `(parent_rank, action_ord, choice_ord)`!
    pub fn insert_sharded_batch_flags(
        &mut self,
        shard_buckets: &mut [Vec<(usize, StateHash, TranspositionLabel)>],
        shard_accepted: &mut [Vec<usize>],
        is_parallel: bool,
        accepted_flags: &mut [bool],
    ) {
        let generation = self.generation;
        let cap = self.entry_limit_per_shard;

        accepted_flags.fill(false);
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
                    for &(sort_idx, hash, label) in items.iter() {
                        if shard.try_insert(generation, cap, hash, label) {
                            acc.push(sort_idx);
                        } else {
                            pruned += 1;
                        }
                    }
                    items.clear();
                    pruned
                })
                .sum();
            self.branches_pruned += pruned_total;
            for acc in shard_accepted.iter_mut() {
                for sort_idx in acc.drain(..) {
                    accepted_flags[sort_idx] = true;
                }
            }
        } else {
            for (shard, items) in self.shards.iter_mut().zip(shard_buckets.iter_mut()) {
                for (sort_idx, hash, label) in items.drain(..) {
                    if shard.try_insert(generation, cap, hash, label) {
                        accepted_flags[sort_idx] = true;
                    } else {
                        self.branches_pruned += 1;
                    }
                }
            }
        }
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

#[cfg(test)]
mod tests {
    use super::*;

    fn mk_hash(tag: u64) -> StateHash {
        let mut z = tag.wrapping_add(0x9E37_79B9_7F4A_7C15);
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        let lo = z ^ (z >> 31);
        let hi = lo.wrapping_mul(0x9E37_79B9_7F4A_7C15).rotate_left(17);
        StateHash::from_parts(hi, lo)
    }

    #[test]
    fn open_addressed_generation_reset_and_grow_preserve_parity() {
        let mut tt = ShardedParetoTT::new(65_536);
        let base_label = TranspositionLabel::new(0, 0, 0, 0, 0, 5, 1, 100.0);
        let worse_label = TranspositionLabel::new(0, 0, 0, 0, 0, 10, 2, 50.0);
        let better_label = TranspositionLabel::new(0, 0, 0, 0, 0, 2, 1, 150.0);
        let trade_label = TranspositionLabel::new(0, 0, 1, 5, 0, 0, 2, 200.0);

        let mut inserted_hashes = Vec::new();
        for i in 0..4000u64 {
            let h = mk_hash(i);
            assert!(tt.try_insert_single(h, base_label));
            assert!(tt.probe_dominated(h, &worse_label));
            inserted_hashes.push(h);
        }

        // Non-dominated tradeoff enters overflow, dominated is rejected
        let sample = inserted_hashes[17];
        assert!(!tt.try_insert_single(sample, worse_label));
        assert!(tt.try_insert_single(sample, trade_label));
        assert!(tt.probe_dominated(sample, &trade_label));
        assert!(tt.try_insert_single(sample, better_label));

        // After generation reset, stale entries behave empty on probe and accept fresh inserts
        tt.reset_generation();
        for &h in &inserted_hashes[..32] {
            assert!(!tt.probe_dominated(h, &worse_label));
            assert!(tt.try_insert_single(h, worse_label));
            assert!(tt.probe_dominated(h, &worse_label));
            assert!(!tt.probe_dominated(h, &better_label));
        }
    }

    #[test]
    fn insert_sharded_batch_flags_matches_sequential_order() {
        let mut tt_seq = ShardedParetoTT::new(65_536);
        let mut tt_par = ShardedParetoTT::new(65_536);

        let mut items = Vec::new();
        for i in 0..512usize {
            let h = mk_hash((i as u64) % 90);
            let hp_loss = (i % 7) as i32;
            let score = ((i * 19) % 100) as f64;
            let pot = ((i / 13) % 2) as u16;
            let label = TranspositionLabel::new(0, 0, pot, pot as i32 * 5, 0, hp_loss, 1, score);
            items.push((i, h, label));
        }

        let seq_flags: Vec<bool> = items
            .iter()
            .map(|&(_, h, l)| tt_seq.try_insert_single(h, l))
            .collect();

        let mut buckets: Vec<Vec<(usize, StateHash, TranspositionLabel)>> =
            (0..TT_SHARD_COUNT).map(|_| Vec::new()).collect();
        let mut accepted_bufs: Vec<Vec<usize>> =
            (0..TT_SHARD_COUNT).map(|_| Vec::new()).collect();
        for &(sort_idx, h, l) in &items {
            buckets[h.shard_index()].push((sort_idx, h, l));
        }
        let mut par_flags = vec![false; items.len()];
        tt_par.insert_sharded_batch_flags(&mut buckets, &mut accepted_bufs, true, &mut par_flags);

        assert_eq!(seq_flags, par_flags);
        assert_eq!(tt_seq.branches_pruned, tt_par.branches_pruned);
    }
}
