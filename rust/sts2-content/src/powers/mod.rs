pub mod batch_a_m;
pub mod batch_n_z;

use sts2_kernel::{
    BattleState, CardHandle, ContentDispatcher, CreatureHandle, HookArgs, HookResult, KernelError,
    PowerInst,
};
use sts2_types::{CombatSide, HookSlot, PowerId, PowerMeta};

const _: () = {
    assert!(PowerId::MythicFormPower as usize == 165);
    assert!(PowerId::NecroMasteryPower as usize == 166);
};

#[inline(always)]
const fn is_batch_a_m(id: PowerId) -> bool {
    (id as usize) < 166
}

pub fn power_meta(id: PowerId) -> Result<&'static PowerMeta, KernelError> {
    if is_batch_a_m(id) {
        batch_a_m::power_meta(id)
    } else {
        batch_n_z::power_meta(id)
    }
}

#[inline]
#[allow(clippy::too_many_arguments)]
pub fn dispatch_hook_resolved(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    p: PowerInst,
    owner_side: CombatSide,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    if is_batch_a_m(p.id) {
        batch_a_m::dispatch_hook(content, state, creature, power_idx, p, owner_side, slot, args)
    } else {
        batch_n_z::dispatch_hook(content, state, creature, power_idx, p, owner_side, slot, args)
    }
}

pub fn dispatch_hook(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    let (power_idx, p, owner_side) = state
        .creature(creature)
        .and_then(|c| {
            c.powers
                .iter()
                .position(|pw| pw.handle == power_handle)
                .map(|idx| (idx, c.powers[idx], c.side))
        })
        .ok_or_else(|| KernelError::InvalidState("missing power".to_owned()))?;
    if is_batch_a_m(p.id) {
        batch_a_m::dispatch_hook(content, state, creature, power_idx, p, owner_side, slot, args)
    } else {
        batch_n_z::dispatch_hook(content, state, creature, power_idx, p, owner_side, slot, args)
    }
}

pub fn dispatch_detached_hook(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power: PowerInst,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    if is_batch_a_m(power.id) {
        batch_a_m::dispatch_detached_hook(content, state, creature, power, slot, args)
    } else {
        batch_n_z::dispatch_detached_hook(content, state, creature, power, slot, args)
    }
}

pub fn resume_hook_choice(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    slot: HookSlot,
    selected: &[CardHandle],
) -> Result<HookResult, KernelError> {
    let (power_idx, id) = state
        .creature(creature)
        .and_then(|c| {
            c.powers
                .iter()
                .position(|p| p.handle == power_handle)
                .map(|idx| (idx, c.powers[idx].id))
        })
        .ok_or_else(|| {
            KernelError::InvalidState(format!(
                "missing power handle {power_handle} on creature {:?}",
                creature
            ))
        })?;
    if is_batch_a_m(id) {
        batch_a_m::resume_hook_choice(content, state, creature, power_idx, slot, selected)
    } else {
        batch_n_z::resume_hook_choice(content, state, creature, power_idx, slot, selected)
    }
}
