use crate::commands::power;
use crate::content_dispatch::ContentDispatcher;
use crate::error::KernelError;
use crate::hooks;
use crate::state::{
    BattleState, CardHandle, CreatureHandle, CreatureInst, DamageResult, MonsterInst, PowerInst,
};
use smallvec::SmallVec;
use std::borrow::Cow;
use std::collections::BTreeSet;
use sts2_types::{CombatSide, Dec, MonsterId, Rng, RunRngType, ValueProp};

pub fn damage_block_internal(
    state: &mut BattleState,
    creature: CreatureHandle,
    amount: Dec,
    props: ValueProp,
) -> Result<Dec, KernelError> {
    let c = state
        .creature_mut(creature)
        .ok_or_else(|| KernelError::InvalidState("missing creature".to_owned()))?;
    if props.contains(ValueProp::UNBLOCKABLE) {
        return Ok(Dec::ZERO);
    }
    if amount.as_decimal().scale() == 0 && c.block >= 0 {
        if let Ok(amt_i32) = amount.to_i32_trunc() {
            if amt_i32 >= 0 {
                let blocked_i32 = c.block.min(amt_i32);
                c.block -= blocked_i32;
                let new_block = c.block;
                if creature == state.player.creature_handle {
                    state.player.block = new_block;
                }
                return Ok(Dec::from_i32(blocked_i32));
            }
        }
    }
    let blocked = Dec::from_i32(c.block).min(amount);
    // Exact port of Creature.DamageBlockInternal: subtracts (int)blocked, returns un-truncated blocked.
    c.block -= blocked.to_i32_trunc()?;
    let new_block = c.block;
    if creature == state.player.creature_handle {
        state.player.block = new_block;
    }
    Ok(blocked)
}

pub fn lose_hp_internal(
    state: &mut BattleState,
    creature: CreatureHandle,
    amount: Dec,
    props: ValueProp,
) -> Result<DamageResult, KernelError> {
    let c = state
        .creature_mut(creature)
        .ok_or_else(|| KernelError::InvalidState("missing creature".to_owned()))?;
    let hp_before = c.current_hp;
    let (will_kill, loss) = if amount.as_decimal().scale() == 0 && hp_before >= 0 {
        if let Ok(raw_i32) = amount.to_i32_trunc() {
            (hp_before > 0 && raw_i32 >= hp_before, raw_i32.clamp(0, 999_999_999))
        } else {
            (
                hp_before > 0 && amount >= Dec::from_i32(hp_before),
                amount.clamp(Dec::ZERO, Dec::CLAMP_MAX_999M).to_i32_trunc()?,
            )
        }
    } else {
        (
            hp_before > 0 && amount >= Dec::from_i32(hp_before),
            amount.clamp(Dec::ZERO, Dec::CLAMP_MAX_999M).to_i32_trunc()?,
        )
    };
    c.current_hp = (hp_before - loss).max(0);
    let unblocked = hp_before - c.current_hp;
    c.cumulative_hp_lost = c
        .cumulative_hp_lost
        .checked_add(unblocked)
        .ok_or_else(|| KernelError::InvalidState("cumulative_hp_lost overflow".to_owned()))?;
    let overkill = if will_kill { (loss - hp_before).max(0) } else { 0 };
    let (new_hp, new_cum) = (c.current_hp, c.cumulative_hp_lost);
    if creature == state.player.creature_handle {
        state.player.current_hp = new_hp;
        state.player.cumulative_hp_lost = new_cum;
    }
    Ok(DamageResult {
        receiver: creature,
        original_target: creature,
        props,
        intended_hp_loss: 0,
        blocked_damage: 0,
        unblocked_damage: unblocked,
        overkill_damage: overkill,
        was_target_killed: will_kill,
        was_block_broken: false,
        was_fully_blocked: false,
    })
}

pub fn gain_block_internal(
    state: &mut BattleState,
    creature: CreatureHandle,
    amount: Dec,
) -> Result<(), KernelError> {
    let c = state
        .creature_mut(creature)
        .ok_or_else(|| KernelError::InvalidState("missing creature".to_owned()))?;
    let next = if amount.as_decimal().scale() == 0 {
        if let Ok(amt_i32) = amount.to_i32_trunc() {
            (i64::from(c.block) + i64::from(amt_i32)).clamp(0, 999_999_999) as i32
        } else {
            Dec::from_i32(c.block)
                .checked_add(amount)?
                .clamp(Dec::ZERO, Dec::CLAMP_MAX_999M)
                .to_i32_trunc()?
        }
    } else {
        Dec::from_i32(c.block)
            .checked_add(amount)?
            .clamp(Dec::ZERO, Dec::CLAMP_MAX_999M)
            .to_i32_trunc()?
    };
    c.block = next;
    if creature == state.player.creature_handle {
        state.player.block = next;
    }
    Ok(())
}

pub fn lose_block_internal(
    state: &mut BattleState,
    creature: CreatureHandle,
    amount: Dec,
) -> Result<(), KernelError> {
    let c = state
        .creature_mut(creature)
        .ok_or_else(|| KernelError::InvalidState("missing creature".to_owned()))?;
    let next = if amount.as_decimal().scale() == 0 {
        if let Ok(amt_i32) = amount.to_i32_trunc() {
            (i64::from(c.block) - i64::from(amt_i32)).clamp(0, 999_999_999) as i32
        } else {
            Dec::from_i32(c.block)
                .checked_sub(amount)?
                .clamp(Dec::ZERO, Dec::CLAMP_MAX_999M)
                .to_i32_trunc()?
        }
    } else {
        Dec::from_i32(c.block)
            .checked_sub(amount)?
            .clamp(Dec::ZERO, Dec::CLAMP_MAX_999M)
            .to_i32_trunc()?
    };
    c.block = next;
    if creature == state.player.creature_handle {
        state.player.block = next;
    }
    Ok(())
}

pub fn heal(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
    amount: Dec,
) -> Result<(), KernelError> {
    let is_player = state.creature(creature).is_some_and(|c| c.is_player);
    if !is_player && hooks::is_ending_checked(state, content)? {
        return Ok(());
    }
    let (delta, new_hp) = {
        let c = state
            .creature_mut(creature)
            .ok_or_else(|| KernelError::InvalidState("missing creature".to_owned()))?;
        let before = c.current_hp;
        let next = Dec::from_i32(before)
            .checked_add(amount)?
            .clamp(Dec::ZERO, Dec::from_i32(c.max_hp))
            .to_i32_trunc()?;
        c.current_hp = next;
        (next - before, next)
    };
    if creature == state.player.creature_handle {
        state.player.current_hp = new_hp;
    }
    let _ = delta;
    if amount > Dec::ZERO {
        let amt_i32 = amount.to_i32_trunc()?;
        hooks::after_current_hp_changed(state, content, creature, amt_i32)?;
    }
    Ok(())
}

pub fn stun(
    state: &mut BattleState,
    creature: CreatureHandle,
    next_move_id: Option<&str>,
) -> Result<(), KernelError> {
    let in_progress = state.is_in_progress;
    let c = state
        .creature_mut(creature)
        .ok_or_else(|| KernelError::InvalidState("missing creature".to_owned()))?;
    let Some(m) = c.monster.as_mut() else {
        return Err(KernelError::InvalidAction(
            "Can't stun a player.".to_owned(),
        ));
    };
    if !in_progress || c.current_hp <= 0 {
        return Ok(());
    }
    let follow_up = match next_move_id {
        Some(id) if !id.is_empty() => Cow::Owned(id.to_owned()),
        _ => m
            .state_log_ids
            .last()
            .cloned()
            .unwrap_or_else(|| m.current_state_id.clone()),
    };
    if m.next_move_id.is_none() || m.next_move_can_transition_away {
        m.current_state_id = Cow::Borrowed("STUNNED");
        m.next_move_id = Some(Cow::Borrowed("STUNNED"));
        m.next_move_can_transition_away = false;
        m.transient_move_follow_up_state_id = Some(follow_up);
    }
    Ok(())
}

pub fn set_current_hp(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
    amount: Dec,
) -> Result<(), KernelError> {
    let (previous, next) = {
        let c = state
            .creature_mut(creature)
            .ok_or_else(|| KernelError::InvalidState("missing creature".to_owned()))?;
        let previous = c.current_hp;
        let clamped = amount
            .clamp(Dec::ZERO, Dec::from_i32(c.max_hp))
            .to_i32_trunc()?;
        c.current_hp = clamped;
        (previous, clamped)
    };
    if creature == state.player.creature_handle {
        state.player.current_hp = next;
    }
    if next != previous {
        hooks::after_current_hp_changed(state, content, creature, next - previous)?;
    }
    let is_dead_live = state
        .creature(creature)
        .is_some_and(|c| c.is_dead() && (state.is_live_combat() || c.is_player));
    if is_dead_live {
        resolve_potential_death(state, content, creature, None)?;
    }
    Ok(())
}

pub fn set_max_and_current_hp(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
    amount: Dec,
) -> Result<(), KernelError> {
    let _ = set_max_hp(state, creature, amount)?;
    set_current_hp(state, content, creature, amount)
}

pub fn set_max_hp(
    state: &mut BattleState,
    creature: CreatureHandle,
    amount: Dec,
) -> Result<i32, KernelError> {
    let (delta, new_hp, new_max) = {
        let c = state
            .creature_mut(creature)
            .ok_or_else(|| KernelError::InvalidState("missing creature".to_owned()))?;
        let old = c.max_hp;
        let next = amount
            .clamp(Dec::ONE, Dec::CLAMP_MAX_999M)
            .to_i32_trunc()?;
        c.max_hp = next;
        if c.current_hp > c.max_hp {
            c.current_hp = c.max_hp;
        }
        (c.max_hp - old, c.current_hp, c.max_hp)
    };
    if creature == state.player.creature_handle {
        state.player.current_hp = new_hp;
        state.player.max_hp = new_max;
    }
    Ok(delta)
}

pub fn gain_max_hp(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
    amount: Dec,
) -> Result<(), KernelError> {
    if amount < Dec::ZERO {
        return Err(KernelError::InvalidAction(
            "Maximum HP gain cannot be negative".to_owned(),
        ));
    }
    let current_max = state
        .creature(creature)
        .ok_or_else(|| KernelError::InvalidState("missing creature".to_owned()))?
        .max_hp;
    let gained = set_max_hp(
        state,
        creature,
        Dec::from_i32(current_max).checked_add(amount)?,
    )?;
    heal(state, content, creature, Dec::from_i32(gained))
}

pub fn kill(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    let hp = match state.creature_mut(creature) {
        Some(c) if c.is_alive() => {
            let cur = c.current_hp;
            c.current_hp = 0;
            cur
        }
        _ => return Ok(()),
    };
    if creature == state.player.creature_handle {
        state.player.current_hp = 0;
    }
    if hp > 0 {
        hooks::after_current_hp_changed(state, content, creature, -hp)?;
    }
    resolve_potential_death(state, content, creature, card_source)
}

pub fn resolve_potential_death(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    if state.creature(target).is_none_or(|c| c.is_alive()) {
        return Ok(());
    }
    for recursion in 0.. {
        hooks::before_death(state, content, target)?;
        let Some(preventer) = hooks::should_die(state, content, target)? else {
            break;
        };
        if recursion >= 10 {
            return Err(KernelError::death_prevention_loop());
        }
        hooks::after_death(state, content, target, card_source, true)?;
        hooks::after_preventing_death(state, content, preventer, target)?;
        if state.creature(target).is_some_and(|c| c.is_alive()) {
            return Ok(());
        }
    }
    resolve_confirmed_death(state, content, target, card_source)
}

fn resolve_confirmed_death(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    let (side, is_player, is_primary_enemy, is_performing_move) = {
        let c = state
            .creature(target)
            .ok_or_else(|| KernelError::InvalidState("missing dead target".to_owned()))?;
        (
            c.side,
            c.is_player,
            c.side == CombatSide::Enemy && !c.is_secondary_enemy(),
            c.monster.as_ref().is_some_and(|m| m.is_performing_move),
        )
    };
    let defer_cleanup = is_performing_move || state.is_creature_attacking(target);

    let should_remove = hooks::should_creature_be_removed_after_death(state, content, target)?;
    if !state.pet_handles.is_empty()
        && state.creature(target).is_some_and(|c| c.is_pet())
        && should_remove
    {
        state.pet_handles.retain(|h| *h != target);
    }

    hooks::after_death(state, content, target, card_source, false)?;

    let side_handles: SmallVec<[CreatureHandle; 8]> = if side == CombatSide::Player {
        state.ally_handles.iter().copied().collect()
    } else {
        state.enemy_handles.iter().copied().collect()
    };
    let living_teammates: SmallVec<[CreatureHandle; 8]> = side_handles
        .into_iter()
        .filter(|&h| h != target && state.creature(h).is_some_and(|c| c.is_alive()))
        .collect();

    if defer_cleanup {
        state.mark_deferred_death_cleanup(target);
    } else {
        cleanup_confirmed_death_creature(state, content, target, should_remove, side, is_player)?;
    }

    if side == CombatSide::Enemy
        && is_primary_enemy
        && !living_teammates.is_empty()
        && living_teammates
            .iter()
            .all(|&h| state.creature(h).is_some_and(|c| c.is_secondary_enemy()))
    {
        for teammate in living_teammates {
            if let Some(c) = state.creature(teammate) {
                if c.is_alive() {
                    let hp = c.current_hp;
                    let r = lose_hp_internal(
                        state,
                        teammate,
                        Dec::from_i32(hp),
                        ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED,
                    )?;
                    if r.unblocked_damage > 0 {
                        hooks::after_current_hp_changed(
                            state,
                            content,
                            teammate,
                            -r.unblocked_damage,
                        )?;
                    }
                    resolve_potential_death(state, content, teammate, card_source)?;
                }
            }
        }
    } else if is_player {
        if let Some(osty) = state.osty_handle() {
            if state.creature(osty).is_some_and(|c| c.is_alive()) {
                kill(state, content, osty, card_source)?;
            }
        }
    }

    let _ = power::remove_by_id;
    Ok(())
}

fn cleanup_confirmed_death_creature(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    should_remove: bool,
    side: CombatSide,
    is_player: bool,
) -> Result<(), KernelError> {
    let powers_snapshot: SmallVec<[PowerInst; 16]> = state
        .creature(target)
        .filter(|c| !c.powers.is_empty())
        .map(|c| SmallVec::from_slice(&c.powers))
        .unwrap_or_default();
    let mut powers_to_remove: SmallVec<[u32; 16]> = SmallVec::new();
    for p in powers_snapshot {
        let remove_after_owner = state
            .fast_power_meta(content, p.id)?
            .flags
            .contains(sts2_types::PowerStaticFlags::REMOVE_AFTER_OWNER_DEATH);
        if remove_after_owner && hooks::should_power_be_removed_on_death(state, content, target, p)?
        {
            powers_to_remove.push(p.handle);
        }
    }

    if should_remove && side == CombatSide::Enemy && state.enemy_handles.contains(&target) {
        state.enemy_handles.retain(|h| *h != target);
        state.removed_handles.push(target);
        if let Some(c) = state.creature_mut(target) {
            c.is_detached = true;
        }
        state.invalidate_creature_hooks();
    }

    for ph in powers_to_remove {
        let _ = power::remove_by_handle(state, content, target, ph)?;
    }

    if is_player {
        state.player.orbs.clear();
        state.player.orb_capacity = 0;
        state.player.is_active_for_hooks = false;
        state.invalidate_orb_hooks();
        state.invalidate_creature_hooks();
    }
    Ok(())
}

pub fn flush_deferred_death_cleanup(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
) -> Result<(), KernelError> {
    let pos = state
        .deferred_death_cleanup
        .iter()
        .position(|&h| h == target);
    let Some(pos) = pos else {
        return Ok(());
    };
    let Some((alive, is_performing_move, side, is_player)) = state.creature(target).map(|c| {
        (
            c.is_alive(),
            c.monster.as_ref().is_some_and(|m| m.is_performing_move),
            c.side,
            c.is_player,
        )
    }) else {
        state.deferred_death_cleanup.swap_remove(pos);
        return Ok(());
    };
    if is_performing_move || state.is_creature_attacking(target) {
        return Ok(());
    }
    state.deferred_death_cleanup.swap_remove(pos);
    if alive {
        return Ok(());
    }
    let should_remove = hooks::should_creature_be_removed_after_death(state, content, target)?;
    cleanup_confirmed_death_creature(state, content, target, should_remove, side, is_player)
}

pub fn flush_all_eligible_deferred_death_cleanups(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    if state.deferred_death_cleanup.is_empty() {
        return Ok(());
    }
    let pending: SmallVec<[CreatureHandle; 4]> = state.deferred_death_cleanup.clone();
    for h in pending {
        flush_deferred_death_cleanup(state, content, h)?;
    }
    Ok(())
}

pub fn spawn_monster_in_combat(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    monster_id: MonsterId,
    side: CombatSide,
    slot_name: Option<Cow<'static, str>>,
) -> Result<CreatureHandle, KernelError> {
    if !state.is_live_combat() {
        return Err(KernelError::InvalidAction(
            "Monsters can only be added to a live combat.".to_owned(),
        ));
    }
    create_and_add_monster(state, content, monster_id, side, slot_name)
}

fn next_hp_offset(rng: &mut Rng, upper_exclusive: i64) -> Result<i64, KernelError> {
    if upper_exclusive <= 0 {
        return Err(KernelError::InvalidState(
            "available monster HP count must be positive".to_owned(),
        ));
    }
    if upper_exclusive <= i32::MAX as i64 {
        Ok(rng.next_int_range(0, upper_exclusive as i32)? as i64)
    } else {
        Ok(rng.next_u64_range(0, upper_exclusive as u64)? as i64)
    }
}

fn select_available_hp(
    min: i32,
    max: i32,
    used_hp: &BTreeSet<i32>,
    mut selected_ordinal: i64,
) -> Result<i32, KernelError> {
    let mut candidate = min as i64;
    for &used in used_hp {
        let gap = (used as i64) - candidate;
        if selected_ordinal < gap {
            return Ok((candidate + selected_ordinal) as i32);
        }
        selected_ordinal -= gap;
        candidate = (used as i64) + 1;
    }
    let selected = candidate + selected_ordinal;
    if selected < min as i64 || selected > max as i64 {
        return Err(KernelError::InvalidState(
            "Available monster HP selection exceeded its inclusive range.".to_owned(),
        ));
    }
    Ok(selected as i32)
}

pub fn create_monster_creature(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    monster_id: MonsterId,
    side: CombatSide,
    slot_name: Option<Cow<'static, str>>,
) -> Result<CreatureHandle, KernelError> {
    let combat_id = state.next_creature_id;
    state.next_creature_id += 1;
    let handle = CreatureHandle(state.next_creature_handle);
    state.next_creature_handle += 1;

    let (min_hp, max_hp) = content.monster_initial_hp_range(state, monster_id)?;
    if min_hp > max_hp {
        return Err(KernelError::InvalidState(format!(
            "{} has min HP {min_hp} greater than its max {max_hp}!",
            monster_id.model_id_str()
        )));
    }
    let rolled_hp = if side == CombatSide::Enemy {
        let range_size = (max_hp as i64) - (min_hp as i64) + 1;
        let mut used_hp = BTreeSet::new();
        for &eh in &state.enemy_handles {
            if let Some(other) = state.creature(eh) {
                if other.is_monster() && other.max_hp >= min_hp && other.max_hp <= max_hp {
                    used_hp.insert(other.max_hp);
                }
            }
        }
        let available_count = range_size - (used_hp.len() as i64);
        let rng = state.run_rngs.get_mut(RunRngType::Niche);
        if available_count > 0 {
            let offset = next_hp_offset(rng, available_count)?;
            select_available_hp(min_hp, max_hp, &used_hp, offset)?
        } else if max_hp < i32::MAX {
            rng.next_int_range(min_hp, max_hp + 1)?
        } else {
            let offset = next_hp_offset(rng, range_size)?;
            ((min_hp as i64) + offset) as i32
        }
    } else {
        max_hp
    };

    let monster_seed = (state.run_rngs.seed as i64)
        .wrapping_add(state.static_ctx.map_col as i64)
        .wrapping_add(state.static_ctx.map_row as i64)
        .wrapping_add(state.static_ctx.act_index as i64)
        .wrapping_add(combat_id as i64) as u64;

    let creature = CreatureInst {
        handle,
        combat_id: Some(combat_id),
        side,
        slot_name,
        is_player: false,
        pet_owner_player_index: if side == CombatSide::Player { Some(0) } else { None },
        monster_id: Some(monster_id),
        current_hp: rolled_hp,
        max_hp: rolled_hp,
        block: 0,
        cumulative_hp_lost: 0,
        is_detached: false,
        has_minion_power: false,
        monster: Some(Box::new(MonsterInst {
            monster_id,
            rng: Rng::from_seed(monster_seed),
            is_performing_move: false,
            spawned_this_turn: true,
            current_state_id: Cow::Borrowed(""),
            next_move_id: None,
            next_move_can_transition_away: true,
            performed_first_move: false,
            state_log_ids: Vec::new(),
            transient_move_follow_up_state_id: None,
            setup_fields: Default::default(),
            private_fields: Default::default(),
        })),
        powers: Vec::new(),
    };

    state.push_creature(creature);
    Ok(handle)
}

pub fn add_created_monster(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    handle: CreatureHandle,
    side: CombatSide,
    slot_name: Option<Cow<'static, str>>,
) -> Result<(), KernelError> {
    if side == CombatSide::Enemy {
        state.enemy_handles.push(handle);
        state.spawned_enemy_handles.push(handle);
    } else {
        state.ally_handles.push(handle);
    }
    state.invalidate_creature_hooks();

    content.monster_setup_for_combat(state, handle)?;
    if slot_name.is_some() && !state.static_ctx.encounter_slots.is_empty() {
        let slots = state.static_ctx.encounter_slots.clone();
        let mut sorted: SmallVec<[CreatureHandle; 8]> =
            SmallVec::from_slice(&state.enemy_handles);
        sorted.sort_by_key(|&h| {
            let sname = state.creature(h).and_then(|c| c.slot_name.as_deref());
            slots
                .iter()
                .position(|x| Some(x.as_str()) == sname)
                .unwrap_or(usize::MAX)
        });
        state.enemy_handles.clear();
        state.enemy_handles.extend_from_slice(&sorted);
    }
    if side == CombatSide::Enemy {
        content.monster_after_added_to_room(state, handle)?;
        if state.current_side == CombatSide::Player {
            content.monster_roll_move(state, handle)?;
        }
    }
    hooks::after_creature_added_to_combat(state, content, handle)
}

pub fn create_and_add_monster(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    monster_id: MonsterId,
    side: CombatSide,
    slot_name: Option<Cow<'static, str>>,
) -> Result<CreatureHandle, KernelError> {
    let handle = create_monster_creature(state, content, monster_id, side, slot_name.clone())?;
    add_created_monster(state, content, handle, side, slot_name)?;
    Ok(handle)
}
