use crate::content_dispatch::ContentDispatcher;
use crate::error::KernelError;
use crate::hooks;
use crate::state::{BattleState, CreatureHandle, OrbInst};
use sts2_types::OrbId;

pub const MAX_ORB_CAPACITY: i32 = 10;

pub fn add_slots(state: &mut BattleState, amount: i32) {
    if hooks::is_over_or_ending(state) {
        return;
    }
    let add = amount.min(MAX_ORB_CAPACITY - state.player.orb_capacity);
    if add > 0 {
        state.player.orb_capacity += add;
    }
}

pub fn remove_slots(state: &mut BattleState, amount: i32) {
    if hooks::is_over_or_ending(state) {
        return;
    }
    state.player.orb_capacity = (state.player.orb_capacity - amount).max(0);
    while (state.player.orbs.len() as i32) > state.player.orb_capacity {
        state.player.orbs.pop();
        state.invalidate_orb_hooks();
    }
}

pub fn channel(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    orb_id: OrbId,
) -> Result<(), KernelError> {
    if hooks::is_over_or_ending_checked(state, content)? {
        return Ok(());
    }
    let _ = content.orb_meta(orb_id)?;
    if state.player.base_orb_slot_count == 0 && state.player.orb_capacity == 0 {
        add_slots(state, 1);
    }
    if (state.player.orbs.len() as i32) >= state.player.orb_capacity && state.player.orb_capacity > 0 {
        evoke_next(state, content, true)?;
    }
    if (state.player.orbs.len() as i32) < state.player.orb_capacity {
        state.player.orbs.push(OrbInst {
            id: orb_id,
            raw_evoke_value: None,
            raw_passive_value: None,
        });
        state.invalidate_orb_hooks();
        state.record_orb_channel(orb_id);
        hooks::after_orb_channeled(state, content, orb_id)?;
    }
    Ok(())
}

pub fn evoke_next(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    dequeue: bool,
) -> Result<(), KernelError> {
    if state.player.orbs.is_empty() {
        return Ok(());
    }
    evoke_at(state, content, 0, dequeue)
}

pub fn evoke_last(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    dequeue: bool,
) -> Result<(), KernelError> {
    if state.player.orbs.is_empty() {
        return Ok(());
    }
    let idx = state.player.orbs.len() - 1;
    evoke_at(state, content, idx, dequeue)
}

fn evoke_at(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    idx: usize,
    dequeue: bool,
) -> Result<(), KernelError> {
    if hooks::is_over_or_ending_checked(state, content)? || idx >= state.player.orbs.len() {
        return Ok(());
    }
    let orb_id = state.player.orbs[idx].id;
    let targets = content.orb_evoke(state, idx)?;
    if dequeue && idx < state.player.orbs.len() {
        state.player.orbs.remove(idx);
        state.invalidate_orb_hooks();
    }
    hooks::after_orb_evoked(state, content, orb_id, &targets)?;
    Ok(())
}

pub fn passive(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    orb_idx: usize,
    target: Option<CreatureHandle>,
    count_affected_by_hooks: bool,
) -> Result<(), KernelError> {
    if hooks::is_over_or_ending_checked(state, content)? || orb_idx >= state.player.orbs.len() {
        return Ok(());
    }
    let orb_id = state.player.orbs[orb_idx].id;
    if count_affected_by_hooks {
        let (trigger_count, modifiers) =
            hooks::modify_orb_passive_trigger_count(state, content, orb_id, orb_idx, 1)?;
        hooks::after_modifying_orb_passive_trigger_count(state, content, &modifiers)?;
        for _ in 0..trigger_count {
            if hooks::is_over_or_ending_checked(state, content)?
                || orb_idx >= state.player.orbs.len()
            {
                break;
            }
            let _ = content.orb_passive(state, orb_idx, target)?;
        }
    } else {
        let _ = content.orb_passive(state, orb_idx, target)?;
    }
    Ok(())
}

pub fn before_turn_end(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    let count = state.player.orbs.len();
    for idx in 0..count {
        if !state.is_in_progress || idx >= state.player.orbs.len() {
            break;
        }
        content.orb_before_turn_end(state, idx)?;
    }
    Ok(())
}

pub fn after_turn_start(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    let count = state.player.orbs.len();
    for idx in 0..count {
        if !state.is_in_progress || idx >= state.player.orbs.len() {
            break;
        }
        content.orb_after_turn_start(state, idx)?;
    }
    Ok(())
}
