use crate::eval::{FastEvalSummary, FullEvalDetails};
use crate::hash::StateHash;
use crate::por::PorActionClass;
use crate::PlannedAction;
use sts2_types::PotionId;

pub const DEFAULT_SLAB_BYTES: usize = 256 * 1024;

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
        }
    }

    pub fn clear(&mut self) {
        for s in &mut self.slabs {
            s.clear();
        }
        // Keep only the first slab when shrinking spillover slabs between searches.
        if self.slabs.len() > 8 {
            self.slabs.truncate(4);
        }
    }

    #[inline]
    pub fn alloc_slice(&mut self, bytes: &[u8]) -> (u32, u32, u32) {
        let len = bytes.len();
        let slab = self.slabs.len().saturating_sub(1) as u32;
        if let Some(last) = self.slabs.last_mut() {
            if last.len() + len <= last.capacity() {
                let offset = last.len() as u32;
                last.extend_from_slice(bytes);
                return (slab, offset, len as u32);
            }
        }
        let alloc_cap = len.max(self.slab_size);
        let mut new_slab = Vec::with_capacity(alloc_cap);
        new_slab.extend_from_slice(bytes);
        let slab = self.slabs.len() as u32;
        self.slabs.push(new_slab);
        (slab, 0, len as u32)
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
    pub total_bytes_committed: u64,
}

impl Default for MainStateArena {
    fn default() -> Self {
        Self::new(DEFAULT_SLAB_BYTES * 2)
    }
}

impl MainStateArena {
    pub fn new(slab_size: usize) -> Self {
        let cap = slab_size.max(8192);
        Self {
            slabs: vec![Vec::with_capacity(cap)],
            slab_size: cap,
            total_bytes_committed: 0,
        }
    }

    #[inline]
    pub fn commit_slice(&mut self, bytes: &[u8]) -> PackedHandle {
        let len = bytes.len();
        self.total_bytes_committed += len as u64;
        let slab = self.slabs.len().saturating_sub(1) as u32;
        if let Some(last) = self.slabs.last_mut() {
            if last.len() + len <= last.capacity() {
                let offset = last.len() as u32;
                last.extend_from_slice(bytes);
                return PackedHandle {
                    slab,
                    offset,
                    len: len as u32,
                };
            }
        }
        let alloc_cap = len.max(self.slab_size);
        let mut new_slab = Vec::with_capacity(alloc_cap);
        new_slab.extend_from_slice(bytes);
        let slab = self.slabs.len() as u32;
        self.slabs.push(new_slab);
        PackedHandle {
            slab,
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
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CompactActionKind {
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

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CompactAction {
    pub kind: CompactActionKind,
    pub turn: i32,
    pub por_class: PorActionClass,
}

/// Flat arena storing multi-step choice index vectors (`Vec<Vec<usize>>`) without per-node heap objects.
#[derive(Debug, Default)]
pub struct ChoiceArena {
    steps_meta: Vec<(u32, u16)>, // (indices_offset, indices_len) per choice step
    indices: Vec<u16>,
}

impl ChoiceArena {
    pub fn push_choices(&mut self, choices: &[Vec<usize>]) -> (u32, u16) {
        if choices.is_empty() {
            return (0, 0);
        }
        let step_offset = self.steps_meta.len() as u32;
        for step in choices {
            let idx_offset = self.indices.len() as u32;
            for &idx in step {
                self.indices.push(idx as u16);
            }
            self.steps_meta.push((idx_offset, step.len() as u16));
        }
        (step_offset, choices.len() as u16)
    }

    pub fn materialize(&self, offset: u32, len: u16) -> Option<Vec<Vec<usize>>> {
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

/// Flat search node stored contiguously inside `NodeArena`.
#[derive(Clone, Debug)]
pub struct SearchNode {
    pub parent: Option<NodeId>,
    pub action: Option<CompactAction>,
    pub extra_end_turn: Option<CompactAction>,
    pub choices_offset: u32,
    pub choices_len: u16,
    pub state_handle: PackedHandle,
    pub canonical_key_handle: PackedHandle,
    pub state_hash: StateHash,
    pub turn: i32,
    pub action_count: u16,
    pub potion_count: u16,
    pub explicit_potion_count: u16,
    pub automatic_potion_count: u16,
    pub potion_strategic_cost: i32,
    pub explicit_potion_strategic_cost: i32,
    pub ambergris_count: u16,
    pub forced_used_mask: u64,
    pub forced_strategic_hp_cost: i32,
    pub forced_ambergris_count: u16,
    pub future_sold_hp: i32,
    pub retention_rank: u32,
    pub is_terminal: bool,
    pub boundary_rank: u8,
    pub fast_eval: FastEvalSummary,
    pub full_eval_idx: Option<u32>,
}

#[derive(Debug, Default)]
pub struct NodeArena {
    pub nodes: Vec<SearchNode>,
    pub choices: ChoiceArena,
    pub full_evals: Vec<FullEvalDetails>,
}

impl NodeArena {
    #[inline]
    pub fn push_node(&mut self, node: SearchNode) -> NodeId {
        let id = NodeId(self.nodes.len() as u32);
        self.nodes.push(node);
        id
    }

    #[inline]
    pub fn get(&self, id: NodeId) -> &SearchNode {
        &self.nodes[id.0 as usize]
    }

    #[inline]
    pub fn get_mut(&mut self, id: NodeId) -> &mut SearchNode {
        &mut self.nodes[id.0 as usize]
    }

    #[inline]
    pub fn full_eval(&self, id: NodeId) -> Option<&FullEvalDetails> {
        self.get(id)
            .full_eval_idx
            .and_then(|idx| self.full_evals.get(idx as usize))
    }

    pub fn reconstruct_actions(&self, mut id: NodeId) -> Vec<PlannedAction> {
        let mut chain = Vec::new();
        loop {
            let n = self.get(id);
            if let Some(extra) = n.extra_end_turn {
                chain.push((extra.kind, 0u32, 0u16));
            }
            if let Some(act) = n.action {
                chain.push((act.kind, n.choices_offset, n.choices_len));
            }
            match n.parent {
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
