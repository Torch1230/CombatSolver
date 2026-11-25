use crate::eval::{PowerRoutePriority, RareNodeEval, StoredFastEval, StoredNodeEval};
use crate::hash::StateHash;
use crate::por::PorActionClass;
use crate::PlannedAction;
use sts2_types::PotionId;

pub const DEFAULT_SLAB_BYTES: usize = 1024 * 1024;
const NODE_CHUNK_SHIFT: usize = 13;
const NODE_CHUNK_SIZE: usize = 1 << NODE_CHUNK_SHIFT; // 8,192 elements per chunk
const NODE_CHUNK_MASK: usize = NODE_CHUNK_SIZE - 1;

pub const COMPACT_ACTION_TAG_NONE: u8 = 0;
pub const COMPACT_ACTION_TAG_TURN_SETUP: u8 = 1;
pub const COMPACT_ACTION_TAG_PLAY_CARD: u8 = 2;
pub const COMPACT_ACTION_TAG_USE_POTION: u8 = 3;
pub const COMPACT_ACTION_TAG_END_TURN: u8 = 4;

pub const NODE_FLAG_HAS_FULL_EVAL: u8 = 1 << 0;
pub const NODE_FLAG_HAS_ROLLOUT_SCORE: u8 = 1 << 1;
pub const NODE_FLAG_HAS_CHOICE_LOOKAHEAD: u8 = 1 << 2;
pub const NODE_FLAG_HAS_EXTRA_END_TURN: u8 = 1 << 3;
pub const NODE_FLAG_IS_TERMINAL: u8 = 1 << 4;

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Hash)]
pub struct PackedHandle {
    pub slab: u32,
    pub offset: u32,
    pub len: u32,
}

/// Thread-local bump slab for zero-contention child state packing during expansion.
/// Records larger than `DEFAULT_SLAB_BYTES` (e.g., 2,305+ card decks) automatically allocate
/// a dedicated spillover slab so there is never a fixed capacity drop.
#[derive(Debug)]
pub struct LocalStateSlab {
    slabs: Vec<Vec<u8>>,
    slab_size: usize,
    active_slab: usize,
}

impl Default for LocalStateSlab {
    fn default() -> Self {
        Self::new(DEFAULT_SLAB_BYTES)
    }
}

impl LocalStateSlab {
    pub fn new(slab_size: usize) -> Self {
        let cap = slab_size.max(4096);
        Self {
            slabs: vec![Vec::with_capacity(cap)],
            slab_size: cap,
            active_slab: 0,
        }
    }

    pub fn clear(&mut self) {
        for s in &mut self.slabs {
            s.clear();
        }
        // Keep enough slabs for reuse across waves while trimming excessive spillover slabs.
        if self.slabs.len() > 64 {
            self.slabs.truncate(64);
        }
        self.active_slab = 0;
    }

    #[inline]
    pub fn alloc_slice(&mut self, bytes: &[u8]) -> (u32, u32, u32) {
        let len = bytes.len();
        while self.active_slab < self.slabs.len() {
            let cur = &mut self.slabs[self.active_slab];
            let cur_len = cur.len();
            if cur_len + len <= cur.capacity() {
                unsafe {
                    std::ptr::copy_nonoverlapping(bytes.as_ptr(), cur.as_mut_ptr().add(cur_len), len);
                    cur.set_len(cur_len + len);
                }
                return (self.active_slab as u32, cur_len as u32, len as u32);
            }
            self.active_slab += 1;
        }
        let alloc_cap = len.max(self.slab_size);
        let mut new_slab = Vec::with_capacity(alloc_cap);
        unsafe {
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), new_slab.as_mut_ptr(), len);
            new_slab.set_len(len);
        }
        let slab = self.slabs.len();
        self.slabs.push(new_slab);
        self.active_slab = slab;
        (slab as u32, 0, len as u32)
    }

    #[inline]
    pub fn get(&self, slab: u32, offset: u32, len: u32) -> &[u8] {
        let s = &self.slabs[slab as usize];
        let o = offset as usize;
        &s[o..o + len as usize]
    }
}

/// Shared read-only-during-expansion packed state arena.
#[derive(Debug)]
pub struct MainStateArena {
    slabs: Vec<Vec<u8>>,
    slab_size: usize,
    active_slab: usize,
    pub total_bytes_committed: u64,
}

impl Default for MainStateArena {
    fn default() -> Self {
        Self::new(1024 * 1024)
    }
}

impl MainStateArena {
    pub fn new(slab_size: usize) -> Self {
        let cap = slab_size.max(8192);
        Self {
            slabs: vec![Vec::with_capacity(cap)],
            slab_size: cap,
            active_slab: 0,
            total_bytes_committed: 0,
        }
    }

    #[inline]
    pub fn commit_slice(&mut self, bytes: &[u8]) -> PackedHandle {
        let len = bytes.len();
        self.total_bytes_committed += len as u64;
        while self.active_slab < self.slabs.len() {
            let cur = &mut self.slabs[self.active_slab];
            let cur_len = cur.len();
            if cur_len + len <= cur.capacity() {
                unsafe {
                    std::ptr::copy_nonoverlapping(bytes.as_ptr(), cur.as_mut_ptr().add(cur_len), len);
                    cur.set_len(cur_len + len);
                }
                return PackedHandle {
                    slab: self.active_slab as u32,
                    offset: cur_len as u32,
                    len: len as u32,
                };
            }
            self.active_slab += 1;
        }
        let alloc_cap = len.max(self.slab_size);
        let mut new_slab = Vec::with_capacity(alloc_cap);
        unsafe {
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), new_slab.as_mut_ptr(), len);
            new_slab.set_len(len);
        }
        let slab = self.slabs.len();
        self.slabs.push(new_slab);
        self.active_slab = slab;
        PackedHandle {
            slab: slab as u32,
            offset: 0,
            len: len as u32,
        }
    }

    #[inline]
    pub fn get(&self, handle: PackedHandle) -> &[u8] {
        let s = &self.slabs[handle.slab as usize];
        let o = handle.offset as usize;
        &s[o..o + handle.len as usize]
    }

    pub fn clear(&mut self) {
        for s in &mut self.slabs {
            s.clear();
        }
        if self.slabs.len() > 64 {
            self.slabs.truncate(64);
        }
        self.active_slab = 0;
    }

    #[inline]
    pub fn reset_pass(&mut self) {
        self.total_bytes_committed = 0;
        self.clear();
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CompactActionKind {
    TurnSetup,
    PlayCard {
        card_handle: u32,
        target_handle: Option<u32>,
    },
    UsePotion {
        potion_slot: u16,
        potion_id: PotionId,
        target_handle: Option<u32>,
    },
    EndTurn,
}

/// 12-byte packed action representation (`por_key` stored separately in `SearchNode` for tight alignment).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CompactAction {
    pub primary: u32,
    pub target: u32,
    pub turn: i16,
    pub tag: u8,
    pub por_tag: u8,
}

const _: () = assert!(std::mem::size_of::<CompactAction>() == 12);

impl Default for CompactAction {
    #[inline(always)]
    fn default() -> Self {
        Self::NONE
    }
}

impl CompactAction {
    pub const NONE: Self = Self {
        primary: 0,
        target: u32::MAX,
        turn: 0,
        tag: COMPACT_ACTION_TAG_NONE,
        por_tag: 0,
    };

    #[inline]
    pub fn new(kind: CompactActionKind, turn: i32, por_class: PorActionClass) -> (Self, u64) {
        let (por_tag, por_key) = match por_class {
            PorActionClass::Barrier => (0u8, 0u64),
            PorActionClass::PureBlock(k) => (1u8, k),
            PorActionClass::PureAttack(k) => (2u8, k),
        };
        let (tag, primary, target) = match kind {
            CompactActionKind::TurnSetup => (COMPACT_ACTION_TAG_TURN_SETUP, 0, u32::MAX),
            CompactActionKind::PlayCard {
                card_handle,
                target_handle,
            } => {
                debug_assert_ne!(target_handle, Some(u32::MAX));
                (
                    COMPACT_ACTION_TAG_PLAY_CARD,
                    card_handle,
                    target_handle.unwrap_or(u32::MAX),
                )
            }
            CompactActionKind::UsePotion {
                potion_slot,
                potion_id,
                target_handle,
            } => {
                debug_assert_ne!(target_handle, Some(u32::MAX));
                (
                    COMPACT_ACTION_TAG_USE_POTION,
                    ((potion_slot as u32) << 16) | (potion_id as u16 as u32),
                    target_handle.unwrap_or(u32::MAX),
                )
            }
            CompactActionKind::EndTurn => (COMPACT_ACTION_TAG_END_TURN, 0, u32::MAX),
        };
        (
            Self {
                primary,
                target,
                turn: turn.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
                tag,
                por_tag,
            },
            por_key,
        )
    }

    #[inline(always)]
    pub const fn is_some(&self) -> bool {
        self.tag != COMPACT_ACTION_TAG_NONE
    }

    #[inline(always)]
    pub const fn kind(&self) -> Option<CompactActionKind> {
        let target_handle = if self.target == u32::MAX {
            None
        } else {
            Some(self.target)
        };
        match self.tag {
            COMPACT_ACTION_TAG_TURN_SETUP => Some(CompactActionKind::TurnSetup),
            COMPACT_ACTION_TAG_PLAY_CARD => Some(CompactActionKind::PlayCard {
                card_handle: self.primary,
                target_handle,
            }),
            COMPACT_ACTION_TAG_USE_POTION => {
                let raw = (self.primary & 0xFFFF) as u16;
                let potion_id = if (raw as usize) < PotionId::COUNT {
                    // SAFETY: `PotionId` is `#[repr(u16)]` with contiguous discriminants `0..PotionId::COUNT`.
                    unsafe { core::mem::transmute::<u16, PotionId>(raw) }
                } else {
                    PotionId::BlockPotion
                };
                Some(CompactActionKind::UsePotion {
                    potion_slot: (self.primary >> 16) as u16,
                    potion_id,
                    target_handle,
                })
            }
            COMPACT_ACTION_TAG_END_TURN => Some(CompactActionKind::EndTurn),
            _ => None,
        }
    }

    #[inline(always)]
    pub const fn por_class(&self, por_key: u64) -> PorActionClass {
        match self.por_tag {
            1 => PorActionClass::PureBlock(por_key),
            2 => PorActionClass::PureAttack(por_key),
            _ => PorActionClass::Barrier,
        }
    }
}

/// Flat arena storing multi-step choice index vectors (`Vec<Vec<usize>>`) without per-node heap objects.
#[derive(Debug, Default)]
pub struct ChoiceArena {
    steps_meta: Vec<(u32, u16)>, // (indices_offset, indices_len) per choice step
    indices: Vec<u16>,
}

impl ChoiceArena {
    #[inline]
    pub fn clear(&mut self) {
        self.steps_meta.clear();
        self.indices.clear();
    }

    pub fn push_choices<T: AsRef<[usize]>>(&mut self, choices: &[T]) -> (u32, u8) {
        if choices.is_empty() {
            return (0, 0);
        }
        debug_assert!(choices.len() <= u8::MAX as usize);
        let step_offset = self.steps_meta.len() as u32;
        for step in choices {
            let step_slice = step.as_ref();
            let idx_offset = self.indices.len() as u32;
            for &idx in step_slice {
                self.indices.push(idx as u16);
            }
            self.steps_meta.push((idx_offset, step_slice.len() as u16));
        }
        (step_offset, choices.len().min(u8::MAX as usize) as u8)
    }

    pub fn extend_from_arena_slice(
        &mut self,
        src: &ChoiceArena,
        offset: u32,
        len: u8,
    ) -> (u32, u8) {
        if len == 0 {
            return (0, 0);
        }
        let start = offset as usize;
        let end = start + len as usize;
        let slice_meta = &src.steps_meta[start..end];
        let dst_step_offset = self.steps_meta.len() as u32;
        self.steps_meta.reserve(slice_meta.len());
        let first_idx = slice_meta[0].0 as usize;
        let last = slice_meta[slice_meta.len() - 1];
        let last_end = last.0 as usize + last.1 as usize;
        debug_assert!(slice_meta
            .windows(2)
            .all(|w| w[0].0 as usize + w[0].1 as usize == w[1].0 as usize));
        let base_idx = self.indices.len() as u32;
        self.indices
            .extend_from_slice(&src.indices[first_idx..last_end]);
        let mut cur_off = base_idx;
        for &(_, idx_len) in slice_meta {
            self.steps_meta.push((cur_off, idx_len));
            cur_off += idx_len as u32;
        }
        (dst_step_offset, len)
    }

    pub fn push_raw_slab_slice(
        &mut self,
        src_meta: &[(u32, u16)],
        src_indices: &[u16],
        offset: u32,
        len: u16,
    ) -> (u32, u8) {
        if len == 0 {
            return (0, 0);
        }
        debug_assert!((len as usize) <= u8::MAX as usize);
        let clamped_len = (len as usize).min(u8::MAX as usize);
        let start = offset as usize;
        let end = start + clamped_len;
        let slice_meta = &src_meta[start..end];
        let dst_step_offset = self.steps_meta.len() as u32;
        self.steps_meta.reserve(slice_meta.len());
        let first_idx = slice_meta[0].0 as usize;
        let last = slice_meta[slice_meta.len() - 1];
        let last_end = last.0 as usize + last.1 as usize;
        let base_idx = self.indices.len() as u32;
        self.indices
            .extend_from_slice(&src_indices[first_idx..last_end]);
        let mut cur_off = base_idx;
        for &(_, idx_len) in slice_meta {
            self.steps_meta.push((cur_off, idx_len));
            cur_off += idx_len as u32;
        }
        (dst_step_offset, clamped_len as u8)
    }

    #[inline]
    pub fn copy_step_indices(
        &self,
        offset: u32,
        step_idx: usize,
        out: &mut smallvec::SmallVec<[usize; 16]>,
    ) {
        let (idx_off, idx_len) = self.steps_meta[offset as usize + step_idx];
        let o = idx_off as usize;
        let l = idx_len as usize;
        out.clear();
        out.reserve(l);
        for &v in &self.indices[o..o + l] {
            out.push(v as usize);
        }
    }

    pub fn materialize(&self, offset: u32, len: u8) -> Option<Vec<Vec<usize>>> {
        if len == 0 {
            return None;
        }
        let start = offset as usize;
        let end = start + len as usize;
        let mut out = Vec::with_capacity(len as usize);
        for &(idx_off, idx_len) in &self.steps_meta[start..end] {
            let i0 = idx_off as usize;
            let i1 = i0 + idx_len as usize;
            out.push(self.indices[i0..i1].iter().map(|&x| x as usize).collect());
        }
        Some(out)
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Hash, Ord, PartialOrd)]
pub struct NodeId(pub u32);

/// Flat 160-byte search node with inline `StoredFastEval` stored contiguously in `NodeArena`.
#[derive(Clone, Debug)]
pub struct SearchNode {
    pub state_hash: StateHash,
    pub forced_used_mask: u64,
    pub beam_rank_score: f64,
    pub rollout_score: f64,
    pub por_key: u64,
    pub fast_eval: StoredFastEval,
    pub state_handle: PackedHandle,
    pub action: CompactAction,
    pub parent_raw: u32,
    pub choices_offset: u32,
    pub rare_eval_idx: u32,
    pub effective_defense: i16,
    pub board_investment: i16,
    pub persistent_buff_value: i16,
    pub max_dark_evoke: i16,
    pub total_poison_and_doom: i16,
    pub turn: i16,
    pub action_count: u16,
    pub used_potion_slot_mask: u16,
    pub potion_strategic_cost: i16,
    pub optional_paid_potion_cost: i16,
    pub setup_score: i16,
    pub reachable_hand_value: i16,
    pub optionality_cards: i16,
    pub choices_len: u8,
    pub potion_count: u8,
    pub explicit_potion_count: u8,
    pub optional_paid_potion_count: u8,
    pub automatic_potion_count: u8,
    pub ambergris_count: u8,
    pub forced_ambergris_count: u8,
    pub boundary_rank: u8,
    pub best_power_priority: PowerRoutePriority,
    pub node_flags: u8,
}

const _: () = assert!(std::mem::size_of::<SearchNode>() <= 160);

impl Default for SearchNode {
    #[inline]
    fn default() -> Self {
        Self {
            state_hash: StateHash::default(),
            forced_used_mask: 0,
            beam_rank_score: 0.0,
            rollout_score: 0.0,
            por_key: 0,
            fast_eval: StoredFastEval::default(),
            state_handle: PackedHandle::default(),
            action: CompactAction::NONE,
            parent_raw: u32::MAX,
            choices_offset: 0,
            rare_eval_idx: u32::MAX,
            effective_defense: 0,
            board_investment: 0,
            persistent_buff_value: 0,
            max_dark_evoke: 0,
            total_poison_and_doom: 0,
            turn: 0,
            action_count: 0,
            used_potion_slot_mask: 0,
            potion_strategic_cost: 0,
            optional_paid_potion_cost: 0,
            setup_score: 0,
            reachable_hand_value: 0,
            optionality_cards: 0,
            choices_len: 0,
            potion_count: 0,
            explicit_potion_count: 0,
            optional_paid_potion_count: 0,
            automatic_potion_count: 0,
            ambergris_count: 0,
            forced_ambergris_count: 0,
            boundary_rank: 0,
            best_power_priority: PowerRoutePriority::Low,
            node_flags: 0,
        }
    }
}

impl SearchNode {
    #[inline(always)]
    pub const fn parent_id(&self) -> Option<NodeId> {
        if self.parent_raw == u32::MAX {
            None
        } else {
            Some(NodeId(self.parent_raw))
        }
    }

    #[inline(always)]
    pub const fn is_terminal(&self) -> bool {
        (self.node_flags & NODE_FLAG_IS_TERMINAL) != 0
    }

    #[inline(always)]
    pub const fn has_extra_end_turn(&self) -> bool {
        (self.node_flags & NODE_FLAG_HAS_EXTRA_END_TURN) != 0
    }

    #[inline(always)]
    pub const fn has_full_eval(&self) -> bool {
        (self.node_flags & NODE_FLAG_HAS_FULL_EVAL) != 0
    }

    #[inline(always)]
    pub const fn has_rollout_score(&self) -> bool {
        (self.node_flags & NODE_FLAG_HAS_ROLLOUT_SCORE) != 0
    }

    #[inline(always)]
    pub const fn has_choice_lookahead(&self) -> bool {
        (self.node_flags & NODE_FLAG_HAS_CHOICE_LOOKAHEAD) != 0
    }

    #[inline(always)]
    pub const fn rollout_score(&self) -> Option<f64> {
        if self.has_rollout_score() {
            Some(self.rollout_score)
        } else {
            None
        }
    }

    #[inline(always)]
    pub const fn choice_lookahead_score(&self) -> Option<f64> {
        if self.has_choice_lookahead() {
            Some(self.rollout_score)
        } else {
            None
        }
    }

    #[inline(always)]
    pub const fn beam_rank_score_or_base(&self) -> f64 {
        self.beam_rank_score
    }

    #[inline(always)]
    pub fn effective_turn_boundary_score(&self) -> f64 {
        if self.has_full_eval() && self.has_rollout_score() {
            0.35 * self.beam_rank_score + 0.65 * self.rollout_score
        } else {
            self.beam_rank_score
        }
    }

    #[inline(always)]
    pub fn por_class(&self) -> PorActionClass {
        self.action.por_class(self.por_key)
    }

    #[inline(always)]
    pub const fn action_turn(&self) -> i32 {
        if self.action.is_some() {
            self.action.turn as i32
        } else {
            self.turn as i32
        }
    }

    #[inline]
    pub fn set_full_eval(&mut self, eval: &StoredNodeEval) {
        self.beam_rank_score = eval.beam_rank_score;
        self.effective_defense = eval
            .effective_defense
            .clamp(i16::MIN as i32, i16::MAX as i32) as i16;
        self.board_investment = eval
            .board_investment
            .clamp(i16::MIN as i32, i16::MAX as i32) as i16;
        self.setup_score = eval.setup_score.clamp(i16::MIN as i32, i16::MAX as i32) as i16;
        self.optionality_cards =
            eval.optionality_cards.clamp(i16::MIN as i32, i16::MAX as i32) as i16;
        self.persistent_buff_value = eval
            .persistent_buff_value
            .clamp(i16::MIN as i32, i16::MAX as i32) as i16;
        self.reachable_hand_value =
            eval.reachable_hand_value.clamp(i16::MIN as i32, i16::MAX as i32) as i16;
        self.max_dark_evoke = eval
            .max_dark_evoke
            .clamp(i16::MIN as i32, i16::MAX as i32) as i16;
        self.total_poison_and_doom = eval
            .total_poison_and_doom
            .clamp(i16::MIN as i32, i16::MAX as i32) as i16;
        self.best_power_priority = eval.best_power_priority;
        self.node_flags |= NODE_FLAG_HAS_FULL_EVAL;
        if let Some(rs) = eval.rollout_score {
            self.rollout_score = rs;
            self.node_flags |= NODE_FLAG_HAS_ROLLOUT_SCORE;
        } else {
            self.rollout_score = eval.choice_lookahead_score.unwrap_or(0.0);
            self.node_flags &= !NODE_FLAG_HAS_ROLLOUT_SCORE;
        }
        if eval.has_choice_lookahead {
            self.node_flags |= NODE_FLAG_HAS_CHOICE_LOOKAHEAD;
        } else {
            self.node_flags &= !NODE_FLAG_HAS_CHOICE_LOOKAHEAD;
        }
    }

    #[inline(always)]
    pub fn set_rollout_score(&mut self, rs: f64) {
        self.rollout_score = rs;
        self.node_flags |= NODE_FLAG_HAS_ROLLOUT_SCORE;
        self.node_flags &= !NODE_FLAG_HAS_CHOICE_LOOKAHEAD;
    }
}

#[derive(Debug)]
pub struct NodeArena {
    node_chunks: Vec<Vec<SearchNode>>,
    node_len: usize,
    pub choices: ChoiceArena,
    pub rare_evals: Vec<RareNodeEval>,
}

impl Default for NodeArena {
    fn default() -> Self {
        Self::with_capacity(NODE_CHUNK_SIZE, 0)
    }
}

impl NodeArena {
    pub fn with_capacity(_nodes_cap: usize, _full_evals_cap: usize) -> Self {
        Self {
            node_chunks: vec![Vec::with_capacity(NODE_CHUNK_SIZE)],
            node_len: 0,
            choices: ChoiceArena::default(),
            rare_evals: Vec::with_capacity(256),
        }
    }

    #[inline]
    pub fn len(&self) -> usize {
        self.node_len
    }

    #[inline]
    pub fn is_empty(&self) -> bool {
        self.node_len == 0
    }

    #[inline]
    pub fn clear(&mut self) {
        for chunk in &mut self.node_chunks {
            chunk.clear();
        }
        if self.node_chunks.len() > 64 {
            self.node_chunks.truncate(64);
        }
        self.node_len = 0;
        self.choices.clear();
        self.rare_evals.clear();
    }

    #[inline]
    pub fn push_rare_eval(&mut self, rare: RareNodeEval) -> u32 {
        if rare.is_empty() {
            u32::MAX
        } else {
            let idx = self.rare_evals.len() as u32;
            self.rare_evals.push(rare);
            idx
        }
    }

    #[inline]
    pub fn push_opt_rare_eval(&mut self, rare: Option<RareNodeEval>) -> u32 {
        match rare {
            Some(r) if !r.is_empty() => {
                let idx = self.rare_evals.len() as u32;
                self.rare_evals.push(r);
                idx
            }
            _ => u32::MAX,
        }
    }

    #[inline]
    pub fn rare_eval(&self, id: NodeId) -> RareNodeEval {
        let idx = self.get(id).rare_eval_idx;
        if idx == u32::MAX {
            RareNodeEval::default()
        } else {
            self.rare_evals.get(idx as usize).copied().unwrap_or_default()
        }
    }

    #[cold]
    #[inline(never)]
    fn push_chunk(&mut self) {
        self.node_chunks.push(Vec::with_capacity(NODE_CHUNK_SIZE));
    }

    #[inline]
    pub fn push_node(&mut self, node: SearchNode) -> NodeId {
        let id = self.node_len;
        let chunk_idx = id >> NODE_CHUNK_SHIFT;
        let local_idx = id & NODE_CHUNK_MASK;
        if chunk_idx >= self.node_chunks.len() {
            self.push_chunk();
        }
        let chunk = &mut self.node_chunks[chunk_idx];
        debug_assert_eq!(chunk.len(), local_idx);
        debug_assert!(local_idx < chunk.capacity());
        // SAFETY: Every chunk is created with `NODE_CHUNK_SIZE` capacity and `local_idx == chunk.len() < NODE_CHUNK_SIZE`.
        unsafe {
            chunk.as_mut_ptr().add(local_idx).write(node);
            chunk.set_len(local_idx + 1);
        }
        self.node_len = id + 1;
        NodeId(id as u32)
    }

    #[inline]
    pub fn get(&self, id: NodeId) -> &SearchNode {
        let raw = id.0 as usize;
        &self.node_chunks[raw >> NODE_CHUNK_SHIFT][raw & NODE_CHUNK_MASK]
    }

    #[inline]
    pub fn get_mut(&mut self, id: NodeId) -> &mut SearchNode {
        let raw = id.0 as usize;
        &mut self.node_chunks[raw >> NODE_CHUNK_SHIFT][raw & NODE_CHUNK_MASK]
    }

    pub fn reconstruct_actions(&self, mut id: NodeId) -> Vec<PlannedAction> {
        let mut chain = Vec::new();
        loop {
            let n = self.get(id);
            if n.has_extra_end_turn() {
                chain.push((CompactActionKind::EndTurn, 0u32, 0u8));
            }
            if let Some(kind) = n.action.kind() {
                chain.push((kind, n.choices_offset, n.choices_len));
            }
            match n.parent_id() {
                Some(p) => id = p,
                None => break,
            }
        }
        chain.reverse();
        chain
            .into_iter()
            .map(|(kind, c_off, c_len)| {
                let choices = self.choices.materialize(c_off, c_len);
                match kind {
                    CompactActionKind::TurnSetup => PlannedAction::TurnSetup { choices },
                    CompactActionKind::PlayCard {
                        card_handle,
                        target_handle,
                    } => PlannedAction::PlayCard {
                        card_handle: card_handle as i32,
                        target_handle: target_handle.map(|h| h as i32),
                        choices,
                    },
                    CompactActionKind::UsePotion {
                        potion_slot,
                        potion_id,
                        target_handle,
                    } => PlannedAction::UsePotion {
                        potion_slot: potion_slot as usize,
                        potion_id,
                        target_handle: target_handle.map(|h| h as i32),
                        choices,
                    },
                    CompactActionKind::EndTurn => PlannedAction::EndTurn { choices },
                }
            })
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn slabs_reuse_existing_buffers_after_clear() {
        let mut local = LocalStateSlab::new(4096);
        let chunk = vec![7u8; 3000];
        let (s0, _, _) = local.alloc_slice(&chunk);
        let (s1, _, _) = local.alloc_slice(&chunk);
        let (s2, _, _) = local.alloc_slice(&chunk);
        assert_eq!((s0, s1, s2), (0, 1, 2));
        local.clear();
        let (r0, off0, _) = local.alloc_slice(&chunk);
        let (r1, _, _) = local.alloc_slice(&chunk);
        let (r2, _, _) = local.alloc_slice(&chunk);
        assert_eq!((r0, off0, r1, r2), (0, 0, 1, 2));
        assert_eq!(local.slabs.len(), 3);

        let mut main = MainStateArena::new(8192);
        let big = vec![9u8; 6000];
        let h0 = main.commit_slice(&big);
        let h1 = main.commit_slice(&big);
        let h2 = main.commit_slice(&big);
        assert_eq!((h0.slab, h1.slab, h2.slab), (0, 1, 2));
        main.clear();
        let m0 = main.commit_slice(&big);
        let m1 = main.commit_slice(&big);
        let m2 = main.commit_slice(&big);
        assert_eq!((m0.slab, m0.offset, m1.slab, m2.slab), (0, 0, 1, 2));
        assert_eq!(main.slabs.len(), 3);
    }
}
