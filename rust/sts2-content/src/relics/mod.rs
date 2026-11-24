pub mod batch_a_m;
pub mod batch_n_z;

use sts2_kernel::{BattleState, CardHandle, ContentDispatcher, HookArgs, HookResult, KernelError};
use sts2_types::{HookSlot, RelicId, RelicMeta};

const _: () = {
    assert!(RelicId::MysticLighter as usize == 168);
    assert!(RelicId::NeowsBones as usize == 169);
};

#[inline(always)]
const fn is_batch_a_m(id: RelicId) -> bool {
    (id as usize) < 169
}

pub fn relic_meta(id: RelicId) -> Result<&'static RelicMeta, KernelError> {
    if is_batch_a_m(id) {
        batch_a_m::relic_meta(id)
    } else {
        batch_n_z::relic_meta(id)
    }
}

pub fn dispatch_hook(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    relic_idx: usize,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    let id = state
        .player
        .relics
        .get(relic_idx)
        .ok_or_else(|| KernelError::InvalidState("missing relic".to_owned()))?
        .id;
    if is_batch_a_m(id) {
        batch_a_m::dispatch_hook(content, state, id, slot, args)
    } else {
        batch_n_z::dispatch_hook(content, state, id, slot, args)
    }
}

pub fn resume_hook_choice(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    relic_idx: usize,
    slot: HookSlot,
    selected: &[CardHandle],
) -> Result<HookResult, KernelError> {
    let id = state
        .player
        .relics
        .get(relic_idx)
        .ok_or_else(|| KernelError::InvalidState("missing relic".to_owned()))?
        .id;
    if is_batch_a_m(id) {
        batch_a_m::resume_hook_choice(content, state, id, slot, selected)
    } else {
        batch_n_z::resume_hook_choice(content, state, id, slot, selected)
    }
}
