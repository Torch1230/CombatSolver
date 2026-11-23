use crate::commands::damage::AttackBuilder;
use crate::commands::power;
use crate::content_dispatch::ContentDispatcher;
use crate::error::KernelError;
use crate::hooks;
use crate::state::{ArrowSlotInst, BattleState, CardHandle, CreatureHandle, PowerExtension};
use sts2_types::{ArrowStatus, Dec, PowerId};

pub fn gain_arrow_slot(state: &mut BattleState) {
    state.player.arrows.push(ArrowSlotInst {
        status: ArrowStatus::Retrieved,
        destroy_after_impaled: false,
    });
}

pub fn player_has_any_arrows(state: &BattleState) -> bool {
    try_get_next_arrow_to_shoot(state).is_some()
}

pub fn try_get_next_arrow_to_shoot(state: &BattleState) -> Option<usize> {
    state
        .player
        .arrows
        .iter()
        .rposition(|arrow| arrow.is_available())
}

pub fn shoot_arrow_at_target(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    attack_cmd: AttackBuilder,
) -> Result<bool, KernelError> {
    if state.creature(target).is_none_or(|c| c.is_dead()) {
        return Ok(false);
    }
    let Some(arrow_idx) = try_shoot_arrow(state, content, attack_cmd.card_source)? else {
        return Ok(false);
    };
    impale_target_with_attack(state, content, target, arrow_idx, attack_cmd)?;
    Ok(true)
}

pub fn shoot_arrow_at_target_with_action<F>(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    source_card: Option<CardHandle>,
    action: F,
) -> Result<bool, KernelError>
where
    F: FnMut(&mut BattleState, &dyn ContentDispatcher, usize) -> Result<(), KernelError>,
{
    if state.creature(target).is_none_or(|c| c.is_dead()) {
        return Ok(false);
    }
    let Some(arrow_idx) = try_shoot_arrow(state, content, source_card)? else {
        return Ok(false);
    };
    impale_target_with_action(state, content, target, arrow_idx, source_card, action)?;
    Ok(true)
}

pub fn impale_target_with_action<F>(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    arrow_idx: usize,
    source_card: Option<CardHandle>,
    mut action: F,
) -> Result<(), KernelError>
where
    F: FnMut(&mut BattleState, &dyn ContentDispatcher, usize) -> Result<(), KernelError>,
{
    if state.creature(target).is_none_or(|c| c.is_dead()) {
        return Ok(());
    }
    action(state, content, arrow_idx)?;

    let player_creature = state.player.creature_handle;
    let mut existing_idx = state.creature(target).and_then(|c| {
        c.powers
            .iter()
            .position(|p| p.id == PowerId::ImpaledPower && p.applier_handle == Some(player_creature))
    });

    if existing_idx.is_none() {
        let mut ext = PowerExtension::default();
        ext.hit_counter = 3;
        existing_idx = power::apply_new_with_extension(
            state,
            content,
            PowerId::ImpaledPower,
            target,
            Dec::ONE,
            Some(player_creature),
            source_card,
            ext,
        )?;
    }

    if let Some(pidx) = existing_idx {
        if let Some(ext_idx) = state
            .creature(target)
            .and_then(|c| c.powers.get(pidx).and_then(|p| p.ext_index))
        {
            state.power_extensions[ext_idx as usize]
                .impaled_arrows
                .push(arrow_idx);
        }
    }

    hooks::after_arrow_impaled(state, content, target, source_card)?;

    if state
        .player
        .arrows
        .get(arrow_idx)
        .is_some_and(|a| a.destroy_after_impaled)
    {
        destroy_arrow(state, content, target, arrow_idx)?;
    } else if state.creature(target).is_some_and(|c| c.is_dead()) {
        let contains_arrow = state
            .creature(target)
            .and_then(|c| {
                c.powers
                    .iter()
                    .find(|p| p.id == PowerId::ImpaledPower && p.applier_handle == Some(player_creature))
                    .and_then(|p| p.ext_index)
            })
            .is_some_and(|ext_idx| {
                state.power_extensions[ext_idx as usize]
                    .impaled_arrows
                    .contains(&arrow_idx)
            });
        if contains_arrow {
            dislodge_first_arrow(state, content, target, player_creature, source_card)?;
        }
    }
    Ok(())
}

pub fn try_shoot_arrow(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    source: Option<CardHandle>,
) -> Result<Option<usize>, KernelError> {
    if !state.is_in_progress {
        return Ok(None);
    }
    let arrow_idx = try_get_next_arrow_to_shoot(state);
    if let Some(idx) = arrow_idx {
        state.player.arrows[idx].status = ArrowStatus::Away;
    }
    hooks::after_arrow_shot(state, content, arrow_idx, source)?;
    Ok(arrow_idx)
}

fn impale_target_with_attack(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    arrow_idx: usize,
    attack_cmd: AttackBuilder,
) -> Result<(), KernelError> {
    if state.creature(target).is_none_or(|c| c.is_dead()) {
        return Ok(());
    }
    let source_card = attack_cmd.card_source;
    let _ = attack_cmd.execute(state, content)?;

    let player_creature = state.player.creature_handle;
    let mut existing_idx = state.creature(target).and_then(|c| {
        c.powers
            .iter()
            .position(|p| p.id == PowerId::ImpaledPower && p.applier_handle == Some(player_creature))
    });

    if existing_idx.is_none() {
        let mut ext = PowerExtension::default();
        ext.hit_counter = 3;
        existing_idx = power::apply_new_with_extension(
            state,
            content,
            PowerId::ImpaledPower,
            target,
            Dec::ONE,
            Some(player_creature),
            source_card,
            ext,
        )?;
    }

    if let Some(pidx) = existing_idx {
        if let Some(ext_idx) = state
            .creature(target)
            .and_then(|c| c.powers.get(pidx).and_then(|p| p.ext_index))
        {
            state.power_extensions[ext_idx as usize]
                .impaled_arrows
                .push(arrow_idx);
        }
    }

    hooks::after_arrow_impaled(state, content, target, None)?;

    if state
        .player
        .arrows
        .get(arrow_idx)
        .is_some_and(|a| a.destroy_after_impaled)
    {
        destroy_arrow(state, content, target, arrow_idx)?;
    } else if state.creature(target).is_some_and(|c| c.is_dead()) {
        let contains_arrow = state
            .creature(target)
            .and_then(|c| {
                c.powers
                    .iter()
                    .find(|p| p.id == PowerId::ImpaledPower && p.applier_handle == Some(player_creature))
                    .and_then(|p| p.ext_index)
            })
            .is_some_and(|ext_idx| {
                state.power_extensions[ext_idx as usize]
                    .impaled_arrows
                    .contains(&arrow_idx)
            });
        if contains_arrow {
            dislodge_first_arrow(state, content, target, player_creature, source_card)?;
        }
    }
    Ok(())
}

pub fn dislodge_first_arrow(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    applier: CreatureHandle,
    source_card: Option<CardHandle>,
) -> Result<(), KernelError> {
    let (power_idx, ext_idx) = {
        let Some(c) = state.creature(target) else {
            return Ok(());
        };
        let Some(pidx) = c
            .powers
            .iter()
            .position(|p| p.id == PowerId::ImpaledPower && p.applier_handle == Some(applier))
        else {
            return Ok(());
        };
        let Some(eidx) = c.powers[pidx].ext_index else {
            return Err(KernelError::InvalidState(
                "ImpaledPower missing extension".to_owned(),
            ));
        };
        (pidx, eidx as usize)
    };

    if state.power_extensions[ext_idx].impaled_arrows.is_empty() {
        return Err(KernelError::InvalidState(
            "No impaled arrows to dislodge".to_owned(),
        ));
    }
    let arrow_idx = state.power_extensions[ext_idx].impaled_arrows.remove(0);
    let _ = power_idx;

    if state
        .player
        .arrows
        .get(arrow_idx)
        .is_some_and(|a| a.destroy_after_impaled)
    {
        state.player.arrows[arrow_idx].status = ArrowStatus::Destroyed;
        return Ok(());
    }

    hooks::after_arrow_dislodged(state, content, target, source_card)?;
    if let Some(slot) = state.player.arrows.get_mut(arrow_idx) {
        if slot.status == ArrowStatus::Destroyed {
            return Err(KernelError::InvalidState(
                "Destroyed arrows cannot be retrieved".to_owned(),
            ));
        }
        slot.status = ArrowStatus::Retrieved;
        slot.destroy_after_impaled = false;
    }
    Ok(())
}

fn destroy_arrow(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    arrow_idx: usize,
) -> Result<(), KernelError> {
    if state
        .player
        .arrows
        .get(arrow_idx)
        .is_some_and(|a| a.status != ArrowStatus::Destroyed)
    {
        if state.creature(target).is_some_and(|c| c.is_alive()) {
            let player_creature = state.player.creature_handle;
            let remove_power = state.creature(target).and_then(|c| {
                c.powers
                    .iter()
                    .find(|p| {
                        p.id == PowerId::ImpaledPower && p.applier_handle == Some(player_creature)
                    })
                    .and_then(|p| p.ext_index.map(|eidx| (p.handle, eidx as usize)))
            });
            if let Some((power_handle, eidx)) = remove_power {
                state.power_extensions[eidx]
                    .impaled_arrows
                    .retain(|idx| *idx != arrow_idx);
                if state.power_extensions[eidx].impaled_arrows.is_empty() {
                    let _ = power::remove_by_handle(state, content, target, power_handle)?;
                }
            }
        }
        state.player.arrows[arrow_idx].status = ArrowStatus::Destroyed;
    }
    Ok(())
}
