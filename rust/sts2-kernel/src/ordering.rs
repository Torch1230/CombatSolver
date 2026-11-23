use crate::content_dispatch::ContentDispatcher;
use crate::error::KernelError;
use crate::hooks;
use crate::state::{BattleState, CardHandle};
use core::cmp::Ordering;
use sts2_types::{Dec, Rng};

pub fn resolve_local_energy_cost(state: &BattleState, card_handle: CardHandle, is_x_energy: bool) -> i32 {
    let card = state.card(card_handle).expect("card handle exists");
    if card.canonical_base_energy_cost < 0 || is_x_energy {
        return card.canonical_base_energy_cost;
    }
    let mut cost = card.base_energy_cost.max(0);
    if let Some(ext) = state.card_ext(card.ext_index) {
        for modifier in &ext.local_energy_modifiers {
            let next = if modifier.kind.is_absolute() {
                modifier.amount
            } else {
                cost + modifier.amount
            };
            cost = if modifier.is_reduce_only {
                cost.min(next)
            } else {
                next
            };
        }
    }
    cost.max(0)
}

pub fn resolve_energy_cost(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_handle: CardHandle,
) -> Result<i32, KernelError> {
    let card = state
        .card(card_handle)
        .ok_or_else(|| KernelError::InvalidState("missing card handle".to_owned()))?;
    let meta = content.card_meta(card.id)?;
    let is_x = meta
        .flags
        .contains(sts2_types::CardStaticFlags::IS_X_ENERGY_COST);
    let local_cost = resolve_local_energy_cost(state, card_handle, is_x);
    if is_x {
        return Ok(local_cost);
    }
    let (modified, was_modified) =
        hooks::modify_energy_cost_in_combat(state, content, card_handle, Dec::from_i32(local_cost))?;
    if was_modified {
        Ok(modified.max(Dec::ZERO).to_i32_trunc()?)
    } else {
        Ok(local_cost)
    }
}

pub fn resolve_local_star_cost(state: &BattleState, card_handle: CardHandle, canonical_star_cost: i32) -> i32 {
    let card = state.card(card_handle).expect("card handle exists");
    if let Some(override_turn) = card.temp_star_cost_override_this_turn {
        return override_turn;
    }
    if card.temp_flags.intersects(
        crate::state::CardTempFlags::TEMPORARY_FREE_TURN
            | crate::state::CardTempFlags::TEMPORARY_FREE_PLAY
            | crate::state::CardTempFlags::TEMPORARY_FREE_COMBAT,
    ) {
        return 0;
    }
    canonical_star_cost
}

pub fn resolve_star_cost(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_handle: CardHandle,
) -> Result<i32, KernelError> {
    let card = state
        .card(card_handle)
        .ok_or_else(|| KernelError::InvalidState("missing card handle".to_owned()))?;
    let meta = content.card_meta(card.id)?;
    if meta
        .flags
        .contains(sts2_types::CardStaticFlags::IS_X_STAR_COST)
    {
        return Ok(state.player.stars);
    }
    let local_cost = resolve_local_star_cost(state, card_handle, meta.canonical_star_cost);
    let (modified, was_modified) =
        hooks::modify_star_cost_in_combat(state, content, card_handle, Dec::from_i32(local_cost))?;
    if was_modified {
        Ok(modified.max(Dec::ZERO).to_i32_trunc()?)
    } else {
        Ok(local_cost)
    }
}

pub fn resolved_energy_cost_for_comparison(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_handle: CardHandle,
) -> Result<i32, KernelError> {
    let card = state
        .card(card_handle)
        .ok_or_else(|| KernelError::InvalidState("missing card handle".to_owned()))?;
    let meta = content.card_meta(card.id)?;
    if meta
        .flags
        .contains(sts2_types::CardStaticFlags::IS_X_ENERGY_COST)
    {
        return Ok(card.captured_energy_x_value);
    }
    if card.canonical_base_energy_cost < 0 {
        return Ok(0);
    }
    Ok(resolve_energy_cost(state, content, card_handle)?.max(0))
}

/// Exact port of `CardModel.CompareTo(AbstractModel? other)` (v0.113.1).
pub fn compare_cards(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    a: CardHandle,
    b: CardHandle,
) -> Result<Ordering, KernelError> {
    if a == b {
        return Ok(Ordering::Equal);
    }
    let (ca, cb) = {
        let ca = state
            .card(a)
            .ok_or_else(|| KernelError::InvalidState("missing card a".to_owned()))?;
        let cb = state
            .card(b)
            .ok_or_else(|| KernelError::InvalidState("missing card b".to_owned()))?;
        (*ca, *cb)
    };

    let by_id = ca.id.compare_model_id(cb.id);
    if by_id != Ordering::Equal {
        return Ok(by_id);
    }

    let by_upgrade = ca.upgrade_level.cmp(&cb.upgrade_level);
    if by_upgrade != Ordering::Equal {
        return Ok(by_upgrade);
    }

    let ench_a = state
        .card_ext(ca.ext_index)
        .and_then(|e| e.enchantment.as_ref().map(|x| x.id));
    let ench_b = state
        .card_ext(cb.ext_index)
        .and_then(|e| e.enchantment.as_ref().map(|x| x.id));
    let by_ench = match (ench_a, ench_b) {
        (None, None) => Ordering::Equal,
        (None, Some(_)) => Ordering::Less,
        (Some(_), None) => Ordering::Greater,
        (Some(x), Some(y)) => x.compare_model_id(y),
    };
    if by_ench != Ordering::Equal {
        return Ok(by_ench);
    }

    let aff_a = ca.affliction.map(|x| x.id);
    let aff_b = cb.affliction.map(|x| x.id);
    let by_aff = match (aff_a, aff_b) {
        (None, None) => Ordering::Equal,
        (None, Some(_)) => Ordering::Less,
        (Some(_), None) => Ordering::Greater,
        (Some(x), Some(y)) => x.compare_model_id(y),
    };
    if by_aff != Ordering::Equal {
        return Ok(by_aff);
    }

    let cost_a = resolved_energy_cost_for_comparison(state, content, a)?;
    let cost_b = resolved_energy_cost_for_comparison(state, content, b)?;
    let by_cost = cost_a.cmp(&cost_b);
    if by_cost != Ordering::Equal {
        return Ok(by_cost);
    }

    let by_replay = ca.base_replay_count.cmp(&cb.base_replay_count);
    if by_replay != Ordering::Equal {
        return Ok(by_replay);
    }

    let vars_a = content.card_comparison_base_values(state, a)?;
    let vars_b = content.card_comparison_base_values(state, b)?;
    for ((_, va), (_, vb)) in vars_a.iter().zip(vars_b.iter()) {
        let ca_val = (*va).max(Dec::ZERO);
        let cb_val = (*vb).max(Dec::ZERO);
        let cmp = ca_val.cmp(&cb_val);
        if cmp != Ordering::Equal {
            return Ok(cmp);
        }
    }
    Ok(Ordering::Equal)
}

/// Exact port of `ListExtensions.StableShuffle`: sort by `CardModel.CompareTo`, then `UnstableShuffle`.
pub fn stable_shuffle_cards(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    handles: &mut [CardHandle],
    rng: &mut Rng,
) -> Result<(), KernelError> {
    // Insertion/sort using fallible comparator
    for i in 1..handles.len() {
        let mut j = i;
        while j > 0 {
            if compare_cards(state, content, handles[j - 1], handles[j])? == Ordering::Greater {
                handles.swap(j - 1, j);
                j -= 1;
            } else {
                break;
            }
        }
    }
    rng.unstable_shuffle(handles);
    Ok(())
}
