use crate::content_dispatch::ContentDispatcher;
use crate::error::KernelError;
use crate::hooks;
use crate::state::{BattleState, CardHandle, CreatureHandle, PowerExtension, PowerInst};
use sts2_types::{CombatSide, Dec, PowerId, PowerInstanceType, PowerType};

pub fn find_existing_instance_for_stacking(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    power_id: PowerId,
    target: CreatureHandle,
    applier: Option<CreatureHandle>,
) -> Result<Option<usize>, KernelError> {
    let meta = content.power_meta(power_id)?;
    let Some(creature) = state.creature(target) else {
        return Ok(None);
    };
    let found = match meta.instance_type {
        PowerInstanceType::Instanced => None,
        PowerInstanceType::InstancedPerApplier => creature
            .powers
            .iter()
            .position(|p| p.id == power_id && p.applier_handle == applier),
        PowerInstanceType::None => creature.powers.iter().position(|p| p.id == power_id),
    };
    Ok(found)
}

pub fn apply(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    power_id: PowerId,
    target: CreatureHandle,
    amount: Dec,
    applier: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
) -> Result<Option<usize>, KernelError> {
    if hooks::is_ending_checked(state, content)? {
        return Ok(None);
    }
    if state
        .creature(target)
        .is_none_or(|c| c.is_detached || !state.contains_creature(target))
    {
        return Ok(None);
    }
    if !hooks::should_allow_hitting(state, content, target)? {
        return Ok(None);
    }

    if let Some(existing_idx) =
        find_existing_instance_for_stacking(state, content, power_id, target, applier)?
    {
        modify_amount(state, content, target, existing_idx, amount, applier, card_source)?;
        return Ok(Some(existing_idx));
    }

    if amount.is_zero() {
        return Ok(None);
    }

    apply_new(state, content, power_id, target, amount, applier, card_source, None)
}

pub fn apply_new_with_extension(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    power_id: PowerId,
    target: CreatureHandle,
    amount: Dec,
    applier: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
    extension: PowerExtension,
) -> Result<Option<usize>, KernelError> {
    let ext_idx = state.power_extensions.len() as u32;
    state.power_extensions.push(extension);
    apply_new(
        state,
        content,
        power_id,
        target,
        amount,
        applier,
        card_source,
        Some(ext_idx),
    )
}

#[allow(clippy::too_many_arguments)]
fn apply_new(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    power_id: PowerId,
    target: CreatureHandle,
    amount: Dec,
    applier: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
    ext_index: Option<u32>,
) -> Result<Option<usize>, KernelError> {
    let meta = content.power_meta(power_id)?;
    let power_handle = state.next_power_handle;
    state.next_power_handle += 1;

    hooks::before_power_amount_changed(
        state,
        content,
        power_id,
        power_handle,
        target,
        amount,
        applier,
        card_source,
    )?;

    let mut modified_amount = amount;
    if let Some(giver) = applier {
        modified_amount = hooks::modify_power_amount_given(
            state,
            content,
            power_id,
            power_handle,
            target,
            modified_amount,
            giver,
            card_source,
        )?;
    }

    let (received_amount, received_modifiers) = hooks::modify_power_amount_received(
        state,
        content,
        power_id,
        power_handle,
        target,
        modified_amount,
        applier,
    )?;
    modified_amount = received_amount;

    let amt_i32 = modified_amount.to_i32_trunc()?;
    let target_side = state.creature(target).map_or(CombatSide::Player, |c| c.side);
    let skip_tick = target_side == CombatSide::Player
        && state.current_side == CombatSide::Enemy
        && meta.power_type == PowerType::Debuff;

    let pending_power = PowerInst {
        handle: power_handle,
        id: power_id,
        amount: amt_i32,
        amount_on_turn_start: 0,
        skip_next_duration_tick: skip_tick,
        applier_handle: applier,
        ext_index,
    };
    hooks::before_power_applied(
        state,
        content,
        target,
        pending_power,
        modified_amount,
        applier,
        card_source,
    )?;

    let mut applied_idx: Option<usize> = None;
    if !modified_amount.is_zero() {
        let c = state
            .creature_mut(target)
            .ok_or_else(|| KernelError::InvalidState("missing power target".to_owned()))?;
        let idx = c.powers.len();
        c.powers.push(pending_power);
        applied_idx = Some(idx);

        if power_id == PowerId::DoomPower {
            if applier.is_some_and(|a| state.creature(a).is_some_and(|cr| cr.is_player)) {
                state.record_actor_event("DoomApplied");
            }
        }
    }

    hooks::after_modifying_power_amount_received(state, content, &received_modifiers)?;

    if !modified_amount.is_zero() {
        hooks::after_power_applied(
            state,
            content,
            target,
            power_id,
            power_handle,
            modified_amount,
            applier,
            card_source,
        )?;
        hooks::after_power_amount_changed(
            state,
            content,
            power_id,
            power_handle,
            target,
            modified_amount,
            applier,
            card_source,
        )?;
    }
    Ok(applied_idx)
}

pub fn modify_amount(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    owner: CreatureHandle,
    power_idx: usize,
    offset: Dec,
    applier: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
) -> Result<i32, KernelError> {
    if hooks::is_ending_checked(state, content)? {
        return Ok(0);
    }
    let Some((power_id, power_handle)) = state
        .creature(owner)
        .and_then(|c| c.powers.get(power_idx).map(|p| (p.id, p.handle)))
    else {
        return Ok(0);
    };

    hooks::before_power_amount_changed(
        state,
        content,
        power_id,
        power_handle,
        owner,
        offset,
        applier,
        card_source,
    )?;

    let mut modified_offset = offset;
    if let Some(giver) = applier {
        modified_offset = hooks::modify_power_amount_given(
            state,
            content,
            power_id,
            power_handle,
            owner,
            modified_offset,
            giver,
            card_source,
        )?;
    }
    let (received_offset, received_modifiers) = hooks::modify_power_amount_received(
        state,
        content,
        power_id,
        power_handle,
        owner,
        modified_offset,
        applier,
    )?;
    modified_offset = received_offset;

    if power_id == PowerId::DoomPower
        && applier.is_some_and(|a| state.creature(a).is_some_and(|cr| cr.is_player))
    {
        state.record_actor_event("DoomApplied");
    }

    let offset_i32 = modified_offset.to_i32_trunc()?;
    let (new_amount, should_remove) = {
        let c = state
            .creature_mut(owner)
            .ok_or_else(|| KernelError::InvalidState("missing power owner".to_owned()))?;
        let idx = c
            .powers
            .iter()
            .position(|p| p.handle == power_handle)
            .ok_or_else(|| KernelError::InvalidState("missing power handle".to_owned()))?;
        let p = &mut c.powers[idx];
        p.amount += offset_i32;
        let remove = content.power_should_remove_due_to_amount(p)?;
        (p.amount, remove)
    };

    hooks::after_modifying_power_amount_received(state, content, &received_modifiers)?;

    if !modified_offset.is_zero() {
        hooks::after_power_amount_changed(
            state,
            content,
            power_id,
            power_handle,
            owner,
            modified_offset,
            applier,
            card_source,
        )?;
    }

    if should_remove {
        remove_by_handle(state, content, owner, power_handle)?;
    }

    Ok(new_amount)
}

pub fn remove_by_index(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    owner: CreatureHandle,
    power_idx: usize,
) -> Result<Option<PowerInst>, KernelError> {
    let removed = state.creature_mut(owner).and_then(|c| {
        if power_idx < c.powers.len() {
            Some(c.powers.remove(power_idx))
        } else {
            None
        }
    });
    if let Some(p) = removed {
        hooks::after_power_removed(state, content, owner, p)?;
    }
    Ok(removed)
}

pub fn remove_by_id(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    owner: CreatureHandle,
    power_id: PowerId,
) -> Result<Option<PowerInst>, KernelError> {
    let removed = state.creature_mut(owner).and_then(|c| {
        c.powers
            .iter()
            .position(|p| p.id == power_id)
            .map(|idx| c.powers.remove(idx))
    });
    if let Some(p) = removed {
        hooks::after_power_removed(state, content, owner, p)?;
    }
    Ok(removed)
}

pub fn remove_by_handle(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    owner: CreatureHandle,
    power_handle: u32,
) -> Result<Option<PowerInst>, KernelError> {
    let removed = state.creature_mut(owner).and_then(|c| {
        c.powers
            .iter()
            .position(|p| p.handle == power_handle)
            .map(|idx| c.powers.remove(idx))
    });
    if let Some(p) = removed {
        hooks::after_power_removed(state, content, owner, p)?;
    }
    Ok(removed)
}

pub fn modify_amount_by_handle(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    owner: CreatureHandle,
    power_handle: u32,
    offset: Dec,
    applier: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
) -> Result<i32, KernelError> {
    let Some(idx) = state
        .creature(owner)
        .and_then(|c| c.powers.iter().position(|p| p.handle == power_handle))
    else {
        return Ok(0);
    };
    modify_amount(state, content, owner, idx, offset, applier, card_source)
}

pub fn tick_down_duration(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    owner: CreatureHandle,
    power_idx: usize,
) -> Result<(), KernelError> {
    if let Some(c) = state.creature_mut(owner) {
        if let Some(p) = c.powers.get_mut(power_idx) {
            if p.skip_next_duration_tick {
                p.skip_next_duration_tick = false;
                return Ok(());
            }
        } else {
            return Ok(());
        }
    }
    let _ = modify_amount(state, content, owner, power_idx, Dec::from_i32(-1), None, None)?;
    Ok(())
}

pub fn tick_down_duration_by_handle(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    owner: CreatureHandle,
    power_handle: u32,
) -> Result<(), KernelError> {
    let Some(idx) = state
        .creature(owner)
        .and_then(|c| c.powers.iter().position(|p| p.handle == power_handle))
    else {
        return Ok(());
    };
    tick_down_duration(state, content, owner, idx)
}
