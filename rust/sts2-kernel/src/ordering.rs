use crate::content_dispatch::ContentDispatcher;
use crate::error::KernelError;
use crate::hooks;
use crate::state::{BattleState, CardHandle, CardInst};
use core::cmp::Ordering;
use smallvec::SmallVec;
use sts2_types::{AfflictionId, CardId, Dec, EnchantmentId, Rng};

#[inline]
pub fn resolve_local_energy_cost_card(state: &BattleState, card: &CardInst, is_x_energy: bool) -> i32 {
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

#[inline]
pub fn resolve_local_energy_cost(state: &BattleState, card_handle: CardHandle, is_x_energy: bool) -> i32 {
    let card = state.card(card_handle).expect("card handle exists");
    resolve_local_energy_cost_card(state, card, is_x_energy)
}

pub fn resolve_energy_cost(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_handle: CardHandle,
) -> Result<i32, KernelError> {
    let card = state
        .card(card_handle)
        .ok_or_else(|| KernelError::InvalidState("missing card handle".to_owned()))?;
    let meta = state.fast_card_meta(content, card.id)?;
    let is_x = meta
        .flags
        .contains(sts2_types::CardStaticFlags::IS_X_ENERGY_COST);
    let local_cost = resolve_local_energy_cost_card(state, card, is_x);
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

#[inline]
pub fn resolve_local_star_cost_card(card: &CardInst, canonical_star_cost: i32) -> i32 {
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

#[inline]
pub fn resolve_local_star_cost(state: &BattleState, card_handle: CardHandle, canonical_star_cost: i32) -> i32 {
    let card = state.card(card_handle).expect("card handle exists");
    resolve_local_star_cost_card(card, canonical_star_cost)
}

pub fn resolve_star_cost(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_handle: CardHandle,
) -> Result<i32, KernelError> {
    let card = state
        .card(card_handle)
        .ok_or_else(|| KernelError::InvalidState("missing card handle".to_owned()))?;
    let meta = state.fast_card_meta(content, card.id)?;
    if meta
        .flags
        .contains(sts2_types::CardStaticFlags::IS_X_STAR_COST)
    {
        return Ok(state.player.stars);
    }
    let local_cost = resolve_local_star_cost_card(card, meta.canonical_star_cost);
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
    let meta = state.fast_card_meta(content, card.id)?;
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

#[derive(Clone, Copy)]
struct ShuffleCardKey {
    handle: CardHandle,
    id: CardId,
    upgrade_level: i32,
    ench: Option<EnchantmentId>,
    aff: Option<AfflictionId>,
    base_replay_count: i32,
    orig_idx: usize,
}

#[inline]
fn compare_opt_ench(a: Option<EnchantmentId>, b: Option<EnchantmentId>) -> Ordering {
    match (a, b) {
        (None, None) => Ordering::Equal,
        (None, Some(_)) => Ordering::Less,
        (Some(_), None) => Ordering::Greater,
        (Some(x), Some(y)) => x.compare_model_id(y),
    }
}

#[inline]
fn compare_opt_aff(a: Option<AfflictionId>, b: Option<AfflictionId>) -> Ordering {
    match (a, b) {
        (None, None) => Ordering::Equal,
        (None, Some(_)) => Ordering::Less,
        (Some(_), None) => Ordering::Greater,
        (Some(x), Some(y)) => x.compare_model_id(y),
    }
}

#[inline]
fn compare_shuffle_keys(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    a: &ShuffleCardKey,
    b: &ShuffleCardKey,
    cost_cache: &mut [i32],
) -> Result<Ordering, KernelError> {
    if a.handle == b.handle {
        return Ok(Ordering::Equal);
    }
    let by_id = a.id.compare_model_id(b.id);
    if by_id != Ordering::Equal {
        return Ok(by_id);
    }
    let by_upgrade = a.upgrade_level.cmp(&b.upgrade_level);
    if by_upgrade != Ordering::Equal {
        return Ok(by_upgrade);
    }
    let by_ench = compare_opt_ench(a.ench, b.ench);
    if by_ench != Ordering::Equal {
        return Ok(by_ench);
    }
    let by_aff = compare_opt_aff(a.aff, b.aff);
    if by_aff != Ordering::Equal {
        return Ok(by_aff);
    }

    let mut cost_a = cost_cache[a.orig_idx];
    if cost_a == i32::MIN {
        cost_a = resolved_energy_cost_for_comparison(state, content, a.handle)?;
        cost_cache[a.orig_idx] = cost_a;
    }
    let mut cost_b = cost_cache[b.orig_idx];
    if cost_b == i32::MIN {
        cost_b = resolved_energy_cost_for_comparison(state, content, b.handle)?;
        cost_cache[b.orig_idx] = cost_b;
    }
    let by_cost = cost_a.cmp(&cost_b);
    if by_cost != Ordering::Equal {
        return Ok(by_cost);
    }

    let by_replay = a.base_replay_count.cmp(&b.base_replay_count);
    if by_replay != Ordering::Equal {
        return Ok(by_replay);
    }

    let vars_a = content.card_comparison_base_values(state, a.handle)?;
    let vars_b = content.card_comparison_base_values(state, b.handle)?;
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
    if handles.len() <= 1 {
        rng.unstable_shuffle(handles);
        return Ok(());
    }
    let n = handles.len();
    let mut keys: SmallVec<[ShuffleCardKey; 64]> = SmallVec::with_capacity(n);
    for (orig_idx, &h) in handles.iter().enumerate() {
        let c = state
            .card(h)
            .ok_or_else(|| KernelError::InvalidState("missing card in shuffle".to_owned()))?;
        let ench = state
            .card_ext(c.ext_index)
            .and_then(|e| e.enchantment.as_ref().map(|x| x.id));
        keys.push(ShuffleCardKey {
            handle: h,
            id: c.id,
            upgrade_level: c.upgrade_level,
            ench,
            aff: c.affliction.map(|x| x.id),
            base_replay_count: c.base_replay_count,
            orig_idx,
        });
    }
    let mut cost_cache: SmallVec<[i32; 64]> = smallvec::smallvec![i32::MIN; n];
    for i in 1..n {
        let mut j = i;
        while j > 0 {
            if compare_shuffle_keys(state, content, &keys[j - 1], &keys[j], &mut cost_cache)?
                == Ordering::Greater
            {
                keys.swap(j - 1, j);
                j -= 1;
            } else {
                break;
            }
        }
    }
    for (dst, k) in handles.iter_mut().zip(keys.iter()) {
        *dst = k.handle;
    }
    rng.unstable_shuffle(handles);
    Ok(())
}
