use crate::content_dispatch::{ContentDispatcher, HookArgs, HookListener};
use crate::error::KernelError;
use crate::state::{BattleState, CardHandle, CreatureHandle, DamageResult, PowerInst};
use bitflags::bitflags;
use smallvec::SmallVec;
use sts2_types::{
    CardKeywords, CardPilePosition, CombatSide, Dec, HookSlot, OrbId, PileType, PowerId,
    PowerStaticFlags, ValueProp,
};

bitflags! {
    #[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Default)]
    pub struct ModifyDamageHookType: u8 {
        const NONE = 0;
        const ADDITIVE = 2;
        const MULTIPLICATIVE = 4;
        const CAP = 8;
        const ALL = 0xE;
    }
}

fn collect_creature_listeners(
    state: &BattleState,
    creature_handle: CreatureHandle,
    include_player_inventory: bool,
    out: &mut Vec<HookListener>,
) {
    let Some(creature) = state.creature(creature_handle) else {
        return;
    };
    for p in &creature.powers {
        out.push(HookListener::Power {
            creature: creature_handle,
            power_handle: p.handle,
        });
    }
    if creature.is_monster() {
        out.push(HookListener::Monster(creature_handle));
        return;
    }
    if !creature.is_player || !state.player.is_active_for_hooks {
        return;
    }
    if include_player_inventory {
        for (i, relic) in state.player.relics.iter().enumerate() {
            if !relic.is_melted {
                out.push(HookListener::Relic(i));
            }
        }
        for (i, slot) in state.player.potion_slots.iter().enumerate() {
            if slot.is_some() {
                out.push(HookListener::Potion(i));
            }
        }
    }
    for i in 0..state.player.orbs.len() {
        out.push(HookListener::Orb(i));
    }
    for pile in [
        &state.player.hand[..],
        &state.player.draw[..],
        &state.player.discard[..],
        &state.player.exhaust[..],
        &state.player.play[..],
    ] {
        for &card_handle in pile {
            out.push(HookListener::Card(card_handle));
            if let Some(card) = state.card(card_handle) {
                if let Some(ext) = state.card_ext(card.ext_index) {
                    if ext.enchantment.is_some() {
                        out.push(HookListener::Enchantment(card_handle));
                    }
                }
                if card.affliction.is_some() {
                    out.push(HookListener::Affliction(card_handle));
                }
            }
        }
    }
}

/// Exact `CombatState.IterateHookListeners()` (#1082):
/// 1. Allies (Powers -> Monster, or if active player: Relics -> Potions -> Orbs -> 5 piles)
/// 2. Enemies (Powers -> Monster)
/// 3. RunModels
/// 4. Dominion (if present)
pub fn collect_combat_only_listeners(state: &BattleState) -> Vec<HookListener> {
    let mut out = Vec::with_capacity(64);
    for &ally in &state.ally_handles {
        collect_creature_listeners(state, ally, true, &mut out);
    }
    for &enemy in &state.enemy_handles {
        collect_creature_listeners(state, enemy, true, &mut out);
    }
    for &rm in &state.static_ctx.run_model_ids {
        out.push(HookListener::RunModel(rm));
    }
    if let Some(dom) = state.static_ctx.dominion_id {
        out.push(HookListener::Dominion(dom));
    }
    out
}

/// Exact `CombatState.IterateHookListeners(includePlayerInventory: true)` (same #1082 sequence).
pub fn collect_direct_combat_hook_listeners(state: &BattleState) -> Vec<HookListener> {
    collect_combat_only_listeners(state)
}

/// Exact `RunState.IterateHookListeners(combatState)` (#1072/#1082):
/// active player's Deck (+ enchantments), followed by `CombatState.IterateHookListeners()`.
pub fn collect_run_combat_listeners(state: &BattleState) -> Vec<HookListener> {
    let mut out = Vec::with_capacity(96);
    if state.player.is_active_for_hooks {
        for &card_handle in &state.player.deck {
            out.push(HookListener::Card(card_handle));
            if let Some(card) = state.card(card_handle) {
                if let Some(ext) = state.card_ext(card.ext_index) {
                    if ext.enchantment.is_some() {
                        out.push(HookListener::Enchantment(card_handle));
                    }
                }
            }
        }
    }
    out.extend(collect_combat_only_listeners(state));
    out
}

/// Matches `Hook.IterateCombatHookListeners` (#1072): checks ending guard, then iterates `CombatState.IterateHookListeners()`.
pub fn collect_combat_hook_listeners(state: &BattleState) -> Vec<HookListener> {
    if is_over_or_ending(state) && !state.is_starting {
        return Vec::new();
    }
    collect_combat_only_listeners(state)
}

pub fn collect_combat_hook_listeners_checked(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<Vec<HookListener>, KernelError> {
    if is_over_or_ending_checked(state, content)? && !state.is_starting {
        return Ok(Vec::new());
    }
    Ok(collect_combat_only_listeners(state))
}

#[inline]
pub fn is_over_or_ending(state: &BattleState) -> bool {
    is_ending(state) || !state.is_in_progress
}

#[inline]
pub fn is_ending(state: &BattleState) -> bool {
    state.is_in_progress && (has_lost(state) || has_won_raw(state))
}

#[inline]
pub fn is_over_or_ending_checked(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<bool, KernelError> {
    if !state.is_in_progress {
        return Ok(true);
    }
    is_ending_checked(state, content)
}

#[inline]
pub fn is_ending_checked(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<bool, KernelError> {
    if !state.is_in_progress {
        return Ok(false);
    }
    if has_lost(state) {
        return Ok(true);
    }
    if !has_won_raw(state) {
        return Ok(false);
    }
    Ok(!should_stop_combat_from_ending(state, content)?)
}

#[inline]
pub fn has_lost(state: &BattleState) -> bool {
    !state.has_living_player_creature()
}

#[inline]
pub fn has_won_raw(state: &BattleState) -> bool {
    !state.enemy_handles.iter().any(|&h| {
        state
            .creature(h)
            .is_some_and(|c| c.is_alive() && !c.is_secondary_enemy())
    })
}

/// Exact `Hook.IsEligibleHookListener` check before each callback dispatch.
pub fn is_eligible_hook_listener(state: &BattleState, listener: HookListener) -> bool {
    match listener {
        HookListener::Power {
            creature,
            power_handle,
        } => {
            let Some(c) = state.creature(creature) else {
                return false;
            };
            if c.is_detached || !state.contains_creature(creature) {
                return false;
            }
            if !c.powers.iter().any(|p| p.handle == power_handle) {
                return false;
            }
            if c.is_player && !state.player.is_active_for_hooks {
                return false;
            }
            true
        }
        HookListener::Monster(creature) => state
            .creature(creature)
            .is_some_and(|c| !c.is_detached && state.contains_creature(creature)),
        HookListener::Orb(orb_idx) => {
            orb_idx < state.player.orbs.len() && state.player.is_active_for_hooks
        }
        HookListener::Relic(idx) => {
            state.player.relics.get(idx).is_some_and(|r| !r.is_melted)
        }
        HookListener::Potion(idx) => state
            .player
            .potion_slots
            .get(idx)
            .is_some_and(|s| s.is_some()),
        HookListener::Card(h) => state.card(h).is_some_and(|c| !c.removed_from_state),
        HookListener::Enchantment(h) => state.card(h).is_some_and(|c| {
            !c.removed_from_state
                && state
                    .card_ext(c.ext_index)
                    .is_some_and(|e| e.enchantment.is_some())
        }),
        HookListener::Affliction(h) => state
            .card(h)
            .is_some_and(|c| !c.removed_from_state && c.affliction.is_some()),
        HookListener::DetachedPower { .. }
        | HookListener::RunModel(_)
        | HookListener::Dominion(_) => true,
    }
}

fn dispatch_if_active(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    listener: HookListener,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<Option<crate::content_dispatch::HookResult>, KernelError> {
    if !is_eligible_hook_listener(state, listener) {
        return Ok(None);
    }
    let mask = content.listener_hook_mask(state, listener)?;
    if !mask.contains(slot) {
        return Ok(None);
    }
    Ok(Some(content.dispatch_hook(state, listener, slot, args)?))
}

fn dispatch_void_list(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    listeners: &[HookListener],
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<(), KernelError> {
    for &listener in listeners {
        let _ = dispatch_if_active(state, content, listener, slot, args)?;
    }
    Ok(())
}

pub fn before_combat_start(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    let listeners = collect_run_combat_listeners(state);
    dispatch_void_list(state, content, &listeners, HookSlot::BeforeCombatStart, &HookArgs::None)?;
    let listeners_late = collect_run_combat_listeners(state);
    dispatch_void_list(
        state,
        content,
        &listeners_late,
        HookSlot::BeforeCombatStartLate,
        &HookArgs::None,
    )
}

pub fn after_room_entered(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    let listeners = collect_run_combat_listeners(state);
    dispatch_void_list(state, content, &listeners, HookSlot::AfterRoomEntered, &HookArgs::None)
}

pub fn after_combat_victory(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    let early = collect_run_combat_listeners(state);
    dispatch_void_list(
        state,
        content,
        &early,
        HookSlot::AfterCombatVictoryEarly,
        &HookArgs::None,
    )?;
    let normal = collect_run_combat_listeners(state);
    dispatch_void_list(
        state,
        content,
        &normal,
        HookSlot::AfterCombatVictory,
        &HookArgs::None,
    )
}

pub fn after_combat_end(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    let listeners = collect_run_combat_listeners(state);
    dispatch_void_list(state, content, &listeners, HookSlot::AfterCombatEnd, &HookArgs::None)
}

pub fn before_side_turn_start(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    side: CombatSide,
    participants: &[CreatureHandle],
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::BeforeSideTurnStart,
        &HookArgs::SideTurn { side, participants },
    )
}

pub fn after_player_turn_start(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<Option<(crate::continuation::ChoiceRequest, HookListener, SmallVec<[HookListener; 16]>)>, KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    continue_after_player_turn_start(state, content, &listeners)
}

pub fn continue_after_player_turn_start(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    remaining: &[HookListener],
) -> Result<Option<(crate::continuation::ChoiceRequest, HookListener, SmallVec<[HookListener; 16]>)>, KernelError> {
    for (idx, &listener) in remaining.iter().enumerate() {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::AfterPlayerTurnStart,
            &HookArgs::Player,
        )? {
            if let Some(req) = res.needs_choice {
                let rest = remaining[idx + 1..].iter().copied().collect();
                return Ok(Some((req, listener, rest)));
            }
        }
    }
    Ok(None)
}

pub fn after_player_turn_start_late(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterPlayerTurnStartLate,
        &HookArgs::Player,
    )
}

pub fn after_side_turn_start(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    side: CombatSide,
    participants: &[CreatureHandle],
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    let args = HookArgs::SideTurn { side, participants };
    dispatch_void_list(state, content, &listeners, HookSlot::AfterSideTurnStart, &args)?;
    let listeners_late = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners_late,
        HookSlot::AfterSideTurnStartLate,
        &args,
    )
}

pub fn after_auto_pre_play_phase_entered(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<bool, KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    continue_after_auto_pre_play_phase_entered(state, content, &listeners, false)
}

pub fn continue_after_auto_pre_play_phase_entered(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    remaining: &[HookListener],
    is_late: bool,
) -> Result<bool, KernelError> {
    let slot = if is_late {
        HookSlot::AfterAutoPrePlayPhaseEnteredLate
    } else {
        HookSlot::AfterAutoPrePlayPhaseEntered
    };
    for (idx, &listener) in remaining.iter().enumerate() {
        let _ = dispatch_if_active(state, content, listener, slot, &HookArgs::Player)?;
        if let Some(pending) = state.pending.as_mut() {
            pending.frames.insert(
                0,
                crate::continuation::ContinuationFrame::AfterAutoPrePlayResume {
                    remaining_listeners: remaining[idx + 1..].iter().copied().collect(),
                    is_late,
                },
            );
            return Ok(true);
        }
    }
    if !is_late {
        let listeners_late = collect_combat_hook_listeners_checked(state, content)?;
        return continue_after_auto_pre_play_phase_entered(state, content, &listeners_late, true);
    }
    Ok(false)
}

pub fn after_auto_post_play_phase_entered(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterAutoPostPlayPhaseEntered,
        &HookArgs::Player,
    )
}

pub fn before_side_turn_end_very_early(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    side: CombatSide,
    participants: &[CreatureHandle],
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::BeforeSideTurnEndVeryEarly,
        &HookArgs::SideTurn { side, participants },
    )
}

pub fn before_side_turn_end_early(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    side: CombatSide,
    participants: &[CreatureHandle],
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::BeforeSideTurnEndEarly,
        &HookArgs::SideTurn { side, participants },
    )
}

pub fn before_side_turn_end(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    side: CombatSide,
    participants: &[CreatureHandle],
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::BeforeSideTurnEnd,
        &HookArgs::SideTurn { side, participants },
    )
}

pub fn after_side_turn_end(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    side: CombatSide,
    participants: &[CreatureHandle],
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    let args = HookArgs::SideTurn { side, participants };
    dispatch_void_list(state, content, &listeners, HookSlot::AfterSideTurnEnd, &args)?;
    let listeners_late = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners_late,
        HookSlot::AfterSideTurnEndLate,
        &args,
    )
}

pub fn should_clear_block(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
) -> Result<Option<HookListener>, KernelError> {
    if is_over_or_ending_checked(state, content)? && !state.is_starting {
        return Ok(None);
    }
    let listeners = collect_direct_combat_hook_listeners(state);
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ShouldClearBlock,
            &HookArgs::Creature(creature),
        )? {
            if res.bool_val == Some(false) {
                return Ok(Some(listener));
            }
        }
    }
    Ok(None)
}

pub fn after_preventing_block_clear(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    preventer: HookListener,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    let _ = dispatch_if_active(
        state,
        content,
        preventer,
        HookSlot::AfterPreventingBlockClear,
        &HookArgs::Creature(creature),
    )?;
    Ok(())
}

pub fn after_block_cleared(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterBlockCleared,
        &HookArgs::Creature(creature),
    )
}

pub fn should_player_reset_energy(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<bool, KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ShouldPlayerResetEnergy,
            &HookArgs::Player,
        )? {
            if res.bool_val == Some(false) {
                return Ok(false);
            }
        }
    }
    Ok(true)
}

pub fn modify_x_value(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    original_value: i32,
) -> Result<i32, KernelError> {
    let mut result = original_value;
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ModifyXValue,
            &HookArgs::ModifyXValue {
                card,
                amount: result,
            },
        )? {
            if let Some(v) = res.int_val {
                result = v;
            }
        }
    }
    Ok(result)
}

pub fn modify_max_energy(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    base_max: Dec,
) -> Result<Dec, KernelError> {
    let mut amount = base_max;
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ModifyMaxEnergy,
            &HookArgs::ModifyHandDraw {
                original_count: amount,
            },
        )? {
            if let Some(v) = res.dec_val {
                amount = v;
            }
        }
    }
    Ok(amount)
}

pub fn after_energy_reset(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(state, content, &listeners, HookSlot::AfterEnergyReset, &HookArgs::Player)?;
    let listeners_late = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners_late,
        HookSlot::AfterEnergyResetLate,
        &HookArgs::Player,
    )
}

pub fn before_hand_draw(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<Option<(crate::continuation::ChoiceRequest, HookListener, SmallVec<[HookListener; 16]>)>, KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for (idx, &listener) in listeners.iter().enumerate() {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::BeforeHandDraw,
            &HookArgs::Player,
        )? {
            if let Some(req) = res.needs_choice {
                let remaining = listeners[idx + 1..].iter().copied().collect();
                return Ok(Some((req, listener, remaining)));
            }
        }
    }
    Ok(None)
}

pub fn continue_before_hand_draw(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    remaining: &[HookListener],
) -> Result<Option<(crate::continuation::ChoiceRequest, HookListener, SmallVec<[HookListener; 16]>)>, KernelError> {
    for (idx, &listener) in remaining.iter().enumerate() {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::BeforeHandDraw,
            &HookArgs::Player,
        )? {
            if let Some(req) = res.needs_choice {
                let rest = remaining[idx + 1..].iter().copied().collect();
                return Ok(Some((req, listener, rest)));
            }
        }
    }
    Ok(None)
}

pub fn modify_hand_draw(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    base_count: Dec,
) -> Result<(Dec, SmallVec<[HookListener; 4]>), KernelError> {
    let mut count = base_count;
    let mut modifiers = SmallVec::new();
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ModifyHandDraw,
            &HookArgs::ModifyHandDraw {
                original_count: count,
            },
        )? {
            if let Some(v) = res.dec_val {
                if v != count {
                    count = v;
                    modifiers.push(listener);
                }
            }
        }
    }
    Ok((count, modifiers))
}

pub fn after_modifying_hand_draw(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    modifiers: &[HookListener],
) -> Result<(), KernelError> {
    dispatch_void_list(
        state,
        content,
        modifiers,
        HookSlot::AfterModifyingHandDraw,
        &HookArgs::Player,
    )
}

pub fn should_draw(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    from_hand_draw: bool,
) -> Result<bool, KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ShouldDraw,
            &HookArgs::ShouldDraw { from_hand_draw },
        )? {
            if res.bool_val == Some(false) {
                return Ok(false);
            }
        }
    }
    Ok(true)
}

pub fn should_flush(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<bool, KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ShouldFlush,
            &HookArgs::Player,
        )? {
            if res.bool_val == Some(false) {
                return Ok(false);
            }
        }
    }
    Ok(true)
}

pub fn before_flush(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(state, content, &listeners, HookSlot::BeforeFlush, &HookArgs::Player)?;
    let listeners_late = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners_late,
        HookSlot::BeforeFlushLate,
        &HookArgs::Player,
    )
}

pub fn after_flush(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(state, content, &listeners, HookSlot::AfterFlush, &HookArgs::Player)
}

pub fn should_ethereal_trigger(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
) -> Result<bool, KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ShouldEtherealTrigger,
            &HookArgs::CardOnly(card),
        )? {
            if res.bool_val == Some(false) {
                return Ok(false);
            }
        }
    }
    Ok(true)
}

pub fn should_take_extra_turn(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<Option<HookListener>, KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in &listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            *listener,
            HookSlot::ShouldTakeExtraTurnEarly,
            &HookArgs::Player,
        )? {
            if res.bool_val == Some(true) {
                return Ok(Some(*listener));
            }
        }
    }
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ShouldTakeExtraTurn,
            &HookArgs::Player,
        )? {
            if res.bool_val == Some(true) {
                return Ok(Some(listener));
            }
        }
    }
    Ok(None)
}

pub fn after_taking_extra_turn(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    source: HookListener,
) -> Result<(), KernelError> {
    let _ = dispatch_if_active(
        state,
        content,
        source,
        HookSlot::AfterTakingExtraTurn,
        &HookArgs::Player,
    )?;
    Ok(())
}

pub fn modify_vulnerable_multiplier(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: Option<CreatureHandle>,
    dealer: Option<CreatureHandle>,
    base_multiplier: Dec,
    props: ValueProp,
    card_source: Option<CardHandle>,
) -> Result<Dec, KernelError> {
    let mut mult = base_multiplier;
    let mut ordered: SmallVec<[HookListener; 16]> = SmallVec::new();
    if let Some(d) = dealer {
        let is_player_or_pet = state
            .creature(d)
            .is_some_and(|c| c.is_player || c.is_pet());
        if is_player_or_pet && state.player.is_active_for_hooks {
            for (i, relic) in state.player.relics.iter().enumerate() {
                if !relic.is_melted {
                    ordered.push(HookListener::Relic(i));
                }
            }
        }
        if let Some(c) = state.creature(d) {
            for p in &c.powers {
                ordered.push(HookListener::Power {
                    creature: d,
                    power_handle: p.handle,
                });
            }
            if c.is_pet() && d != state.player.creature_handle {
                if let Some(pc) = state.player_creature() {
                    for p in &pc.powers {
                        ordered.push(HookListener::Power {
                            creature: state.player.creature_handle,
                            power_handle: p.handle,
                        });
                    }
                }
            }
        }
    }
    if let Some(t) = target {
        if let Some(c) = state.creature(t) {
            for p in &c.powers {
                ordered.push(HookListener::Power {
                    creature: t,
                    power_handle: p.handle,
                });
            }
        }
    }
    for listener in ordered {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ModifyVulnerableMultiplier,
            &HookArgs::ModifyDamage {
                target,
                dealer,
                amount: mult,
                props,
                card_source,
                card_play: None,
            },
        )? {
            if let Some(next) = res.dec_val {
                mult = next;
            }
        }
    }
    Ok(mult)
}

pub fn modify_weak_multiplier(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: Option<CreatureHandle>,
    dealer: Option<CreatureHandle>,
    base_multiplier: Dec,
    props: ValueProp,
    card_source: Option<CardHandle>,
) -> Result<Dec, KernelError> {
    let mut mult = base_multiplier;
    let mut ordered: SmallVec<[HookListener; 16]> = SmallVec::new();
    if let Some(t) = target {
        if state.creature(t).is_some_and(|c| c.is_player) && state.player.is_active_for_hooks {
            for (i, relic) in state.player.relics.iter().enumerate() {
                if !relic.is_melted {
                    ordered.push(HookListener::Relic(i));
                }
            }
        }
    }
    if let Some(d) = dealer {
        if let Some(c) = state.creature(d) {
            for p in &c.powers {
                ordered.push(HookListener::Power {
                    creature: d,
                    power_handle: p.handle,
                });
            }
        }
    }
    for listener in ordered {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ModifyWeakMultiplier,
            &HookArgs::ModifyDamage {
                target,
                dealer,
                amount: mult,
                props,
                card_source,
                card_play: None,
            },
        )? {
            if let Some(next) = res.dec_val {
                mult = next;
            }
        }
    }
    Ok(mult)
}

pub fn should_allow_hitting(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
) -> Result<bool, KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ShouldAllowHitting,
            &HookArgs::Creature(creature),
        )? {
            if res.bool_val == Some(false) {
                return Ok(false);
            }
        }
    }
    Ok(true)
}

pub fn modify_damage(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: Option<CreatureHandle>,
    dealer: Option<CreatureHandle>,
    damage: Dec,
    props: ValueProp,
    card_source: Option<CardHandle>,
    card_play: Option<crate::content_dispatch::CardPlayContext>,
) -> Result<Dec, KernelError> {
    modify_damage_with_type(
        state,
        content,
        target,
        dealer,
        damage,
        props,
        card_source,
        card_play,
        ModifyDamageHookType::ALL,
    )
}

#[allow(clippy::too_many_arguments)]
pub fn modify_damage_with_type(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: Option<CreatureHandle>,
    dealer: Option<CreatureHandle>,
    damage: Dec,
    props: ValueProp,
    card_source: Option<CardHandle>,
    card_play: Option<crate::content_dispatch::CardPlayContext>,
    hook_type: ModifyDamageHookType,
) -> Result<Dec, KernelError> {
    let mut result = damage;
    if let Some(card_h) = card_source {
        let has_ench = state
            .card(card_h)
            .and_then(|c| state.card_ext(c.ext_index))
            .and_then(|e| e.enchantment.as_ref())
            .is_some();
        if has_ench {
            if hook_type.contains(ModifyDamageHookType::ADDITIVE) {
                let add_delta =
                    content.enchantment_damage_additive(state, card_h, result, props)?;
                result = result.checked_add(add_delta)?;
            }
            if hook_type.contains(ModifyDamageHookType::MULTIPLICATIVE) {
                let mult_factor =
                    content.enchantment_damage_multiplicative(state, card_h, result, props)?;
                result = result.checked_mul(mult_factor)?;
            }
        }
    }
    let listeners = collect_run_combat_listeners(state);

    if hook_type.contains(ModifyDamageHookType::ADDITIVE) {
        for &listener in &listeners {
            if let Some(res) = dispatch_if_active(
                state,
                content,
                listener,
                HookSlot::ModifyDamageAdditive,
                &HookArgs::ModifyDamage {
                    target,
                    dealer,
                    amount: result,
                    props,
                    card_source,
                    card_play,
                },
            )? {
                if let Some(delta) = res.dec_val {
                    result = result.checked_add(delta)?;
                }
            }
        }
    }

    if hook_type.contains(ModifyDamageHookType::MULTIPLICATIVE) {
        for &listener in &listeners {
            if let Some(res) = dispatch_if_active(
                state,
                content,
                listener,
                HookSlot::ModifyDamageMultiplicative,
                &HookArgs::ModifyDamage {
                    target,
                    dealer,
                    amount: result,
                    props,
                    card_source,
                    card_play,
                },
            )? {
                if let Some(factor) = res.dec_val {
                    result = result.checked_mul(factor)?;
                }
            }
        }

        for &listener in &listeners {
            if let Some(res) = dispatch_if_active(
                state,
                content,
                listener,
                HookSlot::ModifyDamageMultiplicativeLate,
                &HookArgs::ModifyDamage {
                    target,
                    dealer,
                    amount: result,
                    props,
                    card_source,
                    card_play,
                },
            )? {
                if let Some(factor) = res.dec_val {
                    result = result.checked_mul(factor)?;
                }
            }
        }
    }

    if hook_type.contains(ModifyDamageHookType::CAP) {
        let mut cap = Dec::MAX_VALUE;
        for &listener in &listeners {
            if let Some(res) = dispatch_if_active(
                state,
                content,
                listener,
                HookSlot::ModifyDamageCap,
                &HookArgs::ModifyDamage {
                    target,
                    dealer,
                    amount: result,
                    props,
                    card_source,
                    card_play,
                },
            )? {
                if let Some(item_cap) = res.dec_val {
                    if item_cap < cap {
                        cap = item_cap;
                        if result > item_cap {
                            result = item_cap;
                        }
                    }
                }
            }
        }
    }

    Ok(result.max(Dec::ZERO))
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum HpLossPhase {
    BeforeOsty,
    AfterOsty,
}

pub fn modify_hp_lost(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    amount: Dec,
    props: ValueProp,
    dealer: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
    phase: HpLossPhase,
) -> Result<(Dec, SmallVec<[HookListener; 4]>), KernelError> {
    let mut result = amount;
    let mut changed = SmallVec::new();
    let listeners = collect_run_combat_listeners(state);
    let slots = match phase {
        HpLossPhase::BeforeOsty => [
            HookSlot::ModifyHpLostBeforeOsty,
            HookSlot::ModifyHpLostBeforeOstyLate,
        ],
        HpLossPhase::AfterOsty => [
            HookSlot::ModifyHpLostAfterOsty,
            HookSlot::ModifyHpLostAfterOstyLate,
        ],
    };
    for slot in slots {
        for &listener in &listeners {
            let before = result;
            if let Some(res) = dispatch_if_active(
                state,
                content,
                listener,
                slot,
                &HookArgs::ModifyHpLost {
                    target,
                    amount: result,
                    props,
                    dealer,
                    card_source,
                },
            )? {
                if let Some(next) = res.dec_val {
                    result = next;
                    if before.trunc() != result.trunc() {
                        changed.push(listener);
                    }
                }
            }
        }
    }
    Ok((result, changed))
}

pub fn after_modifying_hp_lost(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    modifiers: &[HookListener],
    phase: HpLossPhase,
) -> Result<(), KernelError> {
    let slot = match phase {
        HpLossPhase::BeforeOsty => HookSlot::AfterModifyingHpLostBeforeOsty,
        HpLossPhase::AfterOsty => HookSlot::AfterModifyingHpLostAfterOsty,
    };
    dispatch_void_list(state, content, modifiers, slot, &HookArgs::None)
}

pub fn before_damage_received(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    amount: Dec,
    props: ValueProp,
    dealer: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    let listeners = collect_run_combat_listeners(state);
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::BeforeDamageReceived,
        &HookArgs::ModifyHpLost {
            target,
            amount,
            props,
            dealer,
            card_source,
        },
    )
}

pub fn modify_pre_block_damage_target(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    original_target: CreatureHandle,
    amount: Dec,
    props: ValueProp,
    dealer: Option<CreatureHandle>,
) -> Result<Option<CreatureHandle>, KernelError> {
    let mut target = None;
    let listeners = collect_direct_combat_hook_listeners(state);
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ModifyPreBlockDamageTarget,
            &HookArgs::ModifyUnblockedTarget {
                target: original_target,
                amount,
                props,
                dealer,
            },
        )? {
            if res.creature_val.is_some() {
                target = res.creature_val;
            }
        }
    }
    Ok(target)
}

pub fn modify_unblocked_damage_target(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    original_target: CreatureHandle,
    amount: Dec,
    props: ValueProp,
    dealer: Option<CreatureHandle>,
) -> Result<CreatureHandle, KernelError> {
    let mut target = original_target;
    let listeners = collect_combat_only_listeners(state);
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ModifyUnblockedDamageTarget,
            &HookArgs::ModifyUnblockedTarget {
                target,
                amount,
                props,
                dealer,
            },
        )? {
            if let Some(next) = res.creature_val {
                target = next;
            }
        }
    }
    Ok(target)
}

pub fn after_block_broken(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    breaker: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    let listeners = collect_direct_combat_hook_listeners(state);
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterBlockBroken,
        &HookArgs::BlockBroken { target, breaker },
    )
}

pub fn after_current_hp_changed(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
    delta: i32,
) -> Result<(), KernelError> {
    let listeners = collect_run_combat_listeners(state);
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterCurrentHpChanged,
        &HookArgs::ModifyHpLost {
            target: creature,
            amount: Dec::from_i32(delta),
            props: ValueProp::empty(),
            dealer: None,
            card_source: None,
        },
    )
}

pub fn after_damage_given(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    args: &HookArgs<'_>,
) -> Result<(), KernelError> {
    let listeners = collect_direct_combat_hook_listeners(state);
    dispatch_void_list(state, content, &listeners, HookSlot::AfterDamageGiven, args)
}

pub fn after_damage_received(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    args: &HookArgs<'_>,
) -> Result<(), KernelError> {
    let listeners = collect_run_combat_listeners(state);
    dispatch_void_list(state, content, &listeners, HookSlot::AfterDamageReceived, args)
}

pub fn before_attack(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    info: crate::content_dispatch::AttackContextInfo,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::BeforeAttack,
        &HookArgs::Attack { info, results: &[] },
    )
}

pub fn modify_attack_hit_count(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    info: crate::content_dispatch::AttackContextInfo,
    original_hit_count: i32,
) -> Result<i32, KernelError> {
    let mut hit_count = original_hit_count;
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ModifyAttackHitCount,
            &HookArgs::Attack {
                info: crate::content_dispatch::AttackContextInfo {
                    hit_count,
                    ..info
                },
                results: &[],
            },
        )? {
            if let Some(v) = res.int_val {
                hit_count = v;
            }
        }
    }
    Ok(hit_count)
}

pub fn after_attack(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    info: crate::content_dispatch::AttackContextInfo,
    results: &[DamageResult],
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterAttack,
        &HookArgs::Attack { info, results },
    )
}

pub fn before_block_gained(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    amount: Dec,
    props: ValueProp,
    card_source: Option<CardHandle>,
    card_play: Option<crate::content_dispatch::CardPlayContext>,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::BeforeBlockGained,
        &HookArgs::ModifyBlock {
            target,
            amount,
            props,
            card_source,
            card_play,
        },
    )
}

pub fn modify_block(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    block: Dec,
    props: ValueProp,
    card_source: Option<CardHandle>,
    card_play: Option<crate::content_dispatch::CardPlayContext>,
) -> Result<(Dec, SmallVec<[HookListener; 4]>), KernelError> {
    let mut result = block;
    let mut changed = SmallVec::new();
    let listeners = collect_combat_hook_listeners_checked(state, content)?;

    for &listener in &listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ModifyBlockAdditive,
            &HookArgs::ModifyBlock {
                target,
                amount: result,
                props,
                card_source,
                card_play,
            },
        )? {
            if let Some(delta) = res.dec_val {
                result = result.checked_add(delta)?;
                if !delta.is_zero() {
                    changed.push(listener);
                }
            }
        }
    }

    for &listener in &listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ModifyBlockMultiplicative,
            &HookArgs::ModifyBlock {
                target,
                amount: result,
                props,
                card_source,
                card_play,
            },
        )? {
            if let Some(factor) = res.dec_val {
                result = result.checked_mul(factor)?;
                if factor != Dec::ONE {
                    changed.push(listener);
                }
            }
        }
    }

    Ok((result.max(Dec::ZERO), changed))
}

#[allow(clippy::too_many_arguments)]
pub fn after_modifying_block_amount(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    modified_amount: Dec,
    props: ValueProp,
    card_source: Option<CardHandle>,
    card_play: Option<crate::content_dispatch::CardPlayContext>,
    modifiers: &[HookListener],
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    let args = HookArgs::ModifyBlock {
        target,
        amount: modified_amount,
        props,
        card_source,
        card_play,
    };
    for listener in listeners {
        if !modifiers.contains(&listener) {
            continue;
        }
        let _ = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::AfterModifyingBlockAmount,
            &args,
        )?;
    }
    Ok(())
}

pub fn after_block_gained(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    amount: Dec,
    props: ValueProp,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterBlockGained,
        &HookArgs::ModifyBlock {
            target,
            amount,
            props,
            card_source,
            card_play: None,
        },
    )
}

pub fn before_death(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
) -> Result<(), KernelError> {
    let listeners = collect_run_combat_listeners(state);
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::BeforeDeath,
        &HookArgs::Creature(target),
    )
}

pub fn should_die(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
) -> Result<Option<HookListener>, KernelError> {
    let listeners = collect_run_combat_listeners(state);
    for &listener in &listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ShouldDie,
            &HookArgs::Creature(target),
        )? {
            if res.bool_val == Some(false) {
                return Ok(Some(listener));
            }
        }
    }
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ShouldDieLate,
            &HookArgs::Creature(target),
        )? {
            if res.bool_val == Some(false) {
                return Ok(Some(listener));
            }
        }
    }
    Ok(None)
}

pub fn after_preventing_death(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    preventer: HookListener,
    target: CreatureHandle,
) -> Result<(), KernelError> {
    let _ = dispatch_if_active(
        state,
        content,
        preventer,
        HookSlot::AfterPreventingDeath,
        &HookArgs::Creature(target),
    )?;
    Ok(())
}

pub fn after_death(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    card_source: Option<CardHandle>,
    was_removal_prevented: bool,
) -> Result<(), KernelError> {
    let listeners = collect_run_combat_listeners(state);
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterDeath,
        &HookArgs::AfterDeath {
            target,
            card_source,
            was_removal_prevented,
        },
    )
}

pub fn should_creature_be_removed_after_death(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
) -> Result<bool, KernelError> {
    let listeners = collect_combat_only_listeners(state);
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ShouldCreatureBeRemovedFromCombatAfterDeath,
            &HookArgs::Creature(creature),
        )? {
            if res.bool_val == Some(false) {
                return Ok(false);
            }
        }
    }
    Ok(true)
}

pub fn should_owner_death_trigger_fatal(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    owner: CreatureHandle,
) -> Result<bool, KernelError> {
    let listeners = collect_run_combat_listeners(state);
    for listener in listeners {
        if let HookListener::Power { creature, .. } = listener {
            if creature == owner {
                if let Some(res) = dispatch_if_active(
                    state,
                    content,
                    listener,
                    HookSlot::ShouldOwnerDeathTriggerFatal,
                    &HookArgs::Creature(owner),
                )? {
                    if res.bool_val == Some(false) {
                        return Ok(false);
                    }
                }
            }
        }
    }
    Ok(true)
}

pub fn should_power_be_removed_on_death(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    power_owner: CreatureHandle,
    power: PowerInst,
) -> Result<bool, KernelError> {
    let meta = content.power_meta(power.id)?;
    let args = HookArgs::PowerQuery {
        owner: power_owner,
        power_id: power.id,
        power_handle: power.handle,
        power_type: meta.power_type,
        is_temporary: meta.flags.contains(PowerStaticFlags::IS_TEMPORARY),
    };
    let listeners = collect_combat_only_listeners(state);
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ShouldPowerBeRemovedOnDeath,
            &args,
        )? {
            if res.bool_val == Some(false) {
                return Ok(false);
            }
        }
    }
    Ok(true)
}

pub fn should_stop_combat_from_ending(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<bool, KernelError> {
    let listeners = collect_direct_combat_hook_listeners(state);
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ShouldStopCombatFromEnding,
            &HookArgs::None,
        )? {
            if res.bool_val == Some(true) {
                return Ok(true);
            }
        }
    }
    Ok(false)
}

pub fn before_power_amount_changed(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    power_id: PowerId,
    power_handle: u32,
    owner: CreatureHandle,
    amount: Dec,
    applier: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::BeforePowerAmountChanged,
        &HookArgs::PowerChanged {
            power_id,
            power_handle,
            owner,
            amount,
            applier,
            card_source,
        },
    )
}

pub fn modify_power_amount_given(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    power_id: PowerId,
    power_handle: u32,
    owner: CreatureHandle,
    amount: Dec,
    applier: CreatureHandle,
    card_source: Option<CardHandle>,
) -> Result<Dec, KernelError> {
    let mut num = amount;
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for &listener in &listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ModifyPowerAmountGivenAdditive,
            &HookArgs::PowerChanged {
                power_id,
                power_handle,
                owner,
                amount: num,
                applier: Some(applier),
                card_source,
            },
        )? {
            if let Some(delta) = res.dec_val {
                num = num.checked_add(delta)?;
            }
        }
    }
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ModifyPowerAmountGivenMultiplicative,
            &HookArgs::PowerChanged {
                power_id,
                power_handle,
                owner,
                amount: num,
                applier: Some(applier),
                card_source,
            },
        )? {
            if let Some(factor) = res.dec_val {
                num = num.checked_mul(factor)?;
            }
        }
    }
    Ok(num)
}

pub fn modify_power_amount_received(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    power_id: PowerId,
    power_handle: u32,
    owner: CreatureHandle,
    amount: Dec,
    applier: Option<CreatureHandle>,
) -> Result<(Dec, SmallVec<[HookListener; 4]>), KernelError> {
    let mut num = amount;
    let mut modifiers = SmallVec::new();
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::TryModifyPowerAmountReceived,
            &HookArgs::PowerChanged {
                power_id,
                power_handle,
                owner,
                amount: num,
                applier,
                card_source: None,
            },
        )? {
            if res.modified {
                if let Some(next) = res.dec_val {
                    num = next;
                }
                modifiers.push(listener);
            }
        }
    }
    Ok((num, modifiers))
}

pub fn after_modifying_power_amount_received(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    modifiers: &[HookListener],
) -> Result<(), KernelError> {
    dispatch_void_list(
        state,
        content,
        modifiers,
        HookSlot::AfterModifyingPowerAmountReceived,
        &HookArgs::None,
    )
}

pub fn before_power_applied(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    power: PowerInst,
    amount: Dec,
    applier: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    let listener = HookListener::DetachedPower {
        creature: target,
        power,
    };
    let _ = dispatch_if_active(
        state,
        content,
        listener,
        HookSlot::BeforeApplied,
        &HookArgs::PowerChanged {
            power_id: power.id,
            power_handle: power.handle,
            owner: target,
            amount,
            applier,
            card_source,
        },
    )?;
    Ok(())
}

pub fn after_power_applied(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    power_id: PowerId,
    power_handle: u32,
    amount: Dec,
    applier: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    let listener = HookListener::Power {
        creature: target,
        power_handle,
    };
    let _ = dispatch_if_active(
        state,
        content,
        listener,
        HookSlot::AfterApplied,
        &HookArgs::PowerChanged {
            power_id,
            power_handle,
            owner: target,
            amount,
            applier,
            card_source,
        },
    )?;
    Ok(())
}

pub fn after_power_removed(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    old_owner: CreatureHandle,
    removed: PowerInst,
) -> Result<(), KernelError> {
    let listener = HookListener::DetachedPower {
        creature: old_owner,
        power: removed,
    };
    let _ = dispatch_if_active(
        state,
        content,
        listener,
        HookSlot::AfterRemoved,
        &HookArgs::PowerChanged {
            power_id: removed.id,
            power_handle: removed.handle,
            owner: old_owner,
            amount: Dec::from_i32(removed.amount),
            applier: removed.applier_handle,
            card_source: None,
        },
    )?;
    Ok(())
}

pub fn after_power_amount_changed(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    power_id: PowerId,
    power_handle: u32,
    owner: CreatureHandle,
    amount: Dec,
    applier: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterPowerAmountChanged,
        &HookArgs::PowerChanged {
            power_id,
            power_handle,
            owner,
            amount,
            applier,
            card_source,
        },
    )
}

pub fn should_play(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
) -> Result<bool, KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ShouldPlay,
            &HookArgs::CardOnly(card),
        )? {
            if res.bool_val == Some(false) {
                return Ok(false);
            }
        }
    }
    Ok(true)
}

pub fn before_card_played(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    ctx: &crate::content_dispatch::CardPlayContext,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::BeforeCardPlayed,
        &HookArgs::CardPlay(*ctx),
    )
}

pub fn after_card_played(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    ctx: &crate::content_dispatch::CardPlayContext,
) -> Result<(), KernelError> {
    let listeners = collect_direct_combat_hook_listeners(state);
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterCardPlayed,
        &HookArgs::CardPlay(*ctx),
    )?;
    let listeners_late = collect_direct_combat_hook_listeners(state);
    dispatch_void_list(
        state,
        content,
        &listeners_late,
        HookSlot::AfterCardPlayedLate,
        &HookArgs::CardPlay(*ctx),
    )
}

pub fn modify_card_play_count(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    target: Option<CreatureHandle>,
    play_count: i32,
) -> Result<(i32, SmallVec<[HookListener; 4]>), KernelError> {
    let mut result = play_count;
    let mut modifiers = SmallVec::new();
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ModifyCardPlayCount,
            &HookArgs::CardPlayCount {
                card,
                target,
                play_count: result,
            },
        )? {
            if let Some(next) = res.int_val {
                if next != result {
                    result = next;
                    modifiers.push(listener);
                }
            }
        }
    }
    Ok((result, modifiers))
}

pub fn after_modifying_card_play_count(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    modifiers: &[HookListener],
) -> Result<(), KernelError> {
    dispatch_void_list(
        state,
        content,
        modifiers,
        HookSlot::AfterModifyingCardPlayCount,
        &HookArgs::CardOnly(card),
    )
}

#[allow(clippy::too_many_arguments)]
pub fn modify_card_play_result_location(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    is_auto_play: bool,
    energy_value: i32,
    pile: PileType,
    position: CardPilePosition,
) -> Result<(PileType, CardPilePosition, SmallVec<[HookListener; 4]>), KernelError> {
    let mut result = (pile, position);
    let mut modifiers = SmallVec::new();
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ModifyCardPlayResultLocation,
            &HookArgs::CardPlayResultLocation {
                card,
                is_auto_play,
                energy_value,
                pile: result.0,
                position: result.1,
            },
        )? {
            if let Some(next) = res.location_val {
                if next != result {
                    result = next;
                    modifiers.push(listener);
                }
            }
        }
    }
    Ok((result.0, result.1, modifiers))
}

#[allow(clippy::too_many_arguments)]
pub fn after_modifying_card_play_result_location(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    is_auto_play: bool,
    energy_value: i32,
    pile: PileType,
    position: CardPilePosition,
    modifiers: &[HookListener],
) -> Result<(), KernelError> {
    dispatch_void_list(
        state,
        content,
        modifiers,
        HookSlot::AfterModifyingCardPlayResultLocation,
        &HookArgs::CardPlayResultLocation {
            card,
            is_auto_play,
            energy_value,
            pile,
            position,
        },
    )
}

pub fn modify_energy_cost_in_combat(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    original_cost: Dec,
) -> Result<(Dec, bool), KernelError> {
    let mut cost = original_cost;
    let mut modified = false;
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for &listener in &listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::TryModifyEnergyCostInCombat,
            &HookArgs::CardCost { card, cost },
        )? {
            if res.modified {
                modified = true;
                if let Some(next) = res.dec_val {
                    cost = next;
                }
            }
        }
    }
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::TryModifyEnergyCostInCombatLate,
            &HookArgs::CardCost { card, cost },
        )? {
            if res.modified {
                modified = true;
                if let Some(next) = res.dec_val {
                    cost = next;
                }
            }
        }
    }
    Ok((cost, modified))
}

pub fn modify_star_cost_in_combat(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    original_cost: Dec,
) -> Result<(Dec, bool), KernelError> {
    let mut cost = original_cost;
    let mut modified = false;
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::TryModifyStarCostInCombat,
            &HookArgs::CardCost { card, cost },
        )? {
            if res.modified {
                modified = true;
                if let Some(next) = res.dec_val {
                    cost = next;
                }
            }
        }
    }
    Ok((cost, modified))
}

pub fn modify_keywords_in_combat(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    keywords: CardKeywords,
) -> Result<CardKeywords, KernelError> {
    let mut kw = keywords;
    let listeners = collect_direct_combat_hook_listeners(state);
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::TryModifyKeywordsInCombat,
            &HookArgs::CardOnly(card),
        )? {
            if let Some(next) = res.keywords {
                kw |= next;
            }
        }
    }
    Ok(kw)
}

pub fn after_card_changed_piles(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    old_pile: PileType,
) -> Result<(), KernelError> {
    let listeners = collect_run_combat_listeners(state);
    let args = HookArgs::CardPileChanged { card, old_pile };
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterCardChangedPiles,
        &args,
    )?;
    let listeners_late = collect_run_combat_listeners(state);
    dispatch_void_list(
        state,
        content,
        &listeners_late,
        HookSlot::AfterCardChangedPilesLate,
        &args,
    )
}

pub fn after_card_drawn(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    from_hand_draw: bool,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterCardDrawnEarly,
        &HookArgs::CardDrawn {
            card,
            from_hand_draw,
        },
    )?;
    let listeners_normal = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners_normal,
        HookSlot::AfterCardDrawn,
        &HookArgs::CardDrawn {
            card,
            from_hand_draw,
        },
    )
}

pub fn after_card_discarded(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterCardDiscarded,
        &HookArgs::CardOnly(card),
    )
}

pub fn after_card_exhausted(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    caused_by_ethereal: bool,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterCardExhausted,
        &HookArgs::CardExhausted {
            card,
            caused_by_ethereal,
        },
    )
}

pub fn after_card_entered_combat(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterCardEnteredCombat,
        &HookArgs::CardOnly(card),
    )
}

pub fn after_card_generated(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    creator: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterCardGenerated,
        &HookArgs::CardGenerated { card, creator },
    )
}

pub fn after_card_generated_for_combat(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    creator: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterCardGeneratedForCombat,
        &HookArgs::CardGenerated { card, creator },
    )
}

pub fn after_hand_emptied(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(state, content, &listeners, HookSlot::AfterHandEmptied, &HookArgs::Player)
}

pub fn after_shuffle(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<Option<(crate::continuation::ChoiceRequest, HookListener, SmallVec<[HookListener; 16]>)>, KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    continue_after_shuffle(state, content, &listeners)
}

pub fn continue_after_shuffle(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    remaining: &[HookListener],
) -> Result<Option<(crate::continuation::ChoiceRequest, HookListener, SmallVec<[HookListener; 16]>)>, KernelError> {
    for (idx, &listener) in remaining.iter().enumerate() {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::AfterShuffle,
            &HookArgs::Player,
        )? {
            if let Some(req) = res.needs_choice {
                let rest = remaining[idx + 1..].iter().copied().collect();
                return Ok(Some((req, listener, rest)));
            }
        }
    }
    Ok(None)
}

pub fn after_energy_spent(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    amount: i32,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterEnergySpent,
        &HookArgs::CardCost {
            card,
            cost: Dec::from_i32(amount),
        },
    )
}

pub fn should_gain_stars(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<bool, KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ShouldGainStars,
            &HookArgs::Player,
        )? {
            if res.bool_val == Some(false) {
                return Ok(false);
            }
        }
    }
    Ok(true)
}

pub fn after_stars_gained(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    amount: i32,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterStarsGained,
        &HookArgs::ModifyHandDraw {
            original_count: Dec::from_i32(amount),
        },
    )
}

pub fn after_stars_spent(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    amount: i32,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterStarsSpent,
        &HookArgs::ModifyHandDraw {
            original_count: Dec::from_i32(amount),
        },
    )
}

pub fn modify_orb_value(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    orb: OrbId,
    amount: Dec,
) -> Result<Dec, KernelError> {
    let mut result = amount;
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ModifyOrbValue,
            &HookArgs::ModifyOrbValue {
                orb,
                amount: result,
            },
        )? {
            if let Some(v) = res.dec_val {
                result = v;
            }
        }
    }
    Ok(result)
}

pub fn modify_orb_passive_trigger_count(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    orb: OrbId,
    orb_idx: usize,
    trigger_count: i32,
) -> Result<(i32, SmallVec<[HookListener; 4]>), KernelError> {
    let mut result = trigger_count;
    let mut changed = SmallVec::new();
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ModifyOrbPassiveTriggerCounts,
            &HookArgs::ModifyOrbPassiveTriggerCount {
                orb,
                orb_idx,
                trigger_count: result,
            },
        )? {
            if let Some(v) = res.int_val {
                if v != result {
                    result = v;
                    changed.push(listener);
                }
            }
        }
    }
    Ok((result, changed))
}

pub fn after_modifying_orb_passive_trigger_count(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    modifiers: &[HookListener],
) -> Result<(), KernelError> {
    dispatch_void_list(
        state,
        content,
        modifiers,
        HookSlot::AfterModifyingOrbPassiveTriggerCount,
        &HookArgs::None,
    )
}

pub fn after_orb_channeled(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    orb: OrbId,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterOrbChanneled,
        &HookArgs::ModifyOrbValue {
            orb,
            amount: Dec::ZERO,
        },
    )
}

pub fn after_orb_evoked(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    orb: OrbId,
    targets: &[CreatureHandle],
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterOrbEvoked,
        &HookArgs::OrbEvoked { orb, targets },
    )
}

pub fn after_arrow_shot(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    arrow_idx: Option<usize>,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterArrowShot,
        &HookArgs::ArrowShot {
            arrow_idx,
            card_source,
        },
    )
}

pub fn after_arrow_impaled(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterArrowImpaled,
        &HookArgs::AfterDeath {
            target,
            card_source,
            was_removal_prevented: false,
        },
    )
}

pub fn after_arrow_dislodged(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterArrowDislodged,
        &HookArgs::AfterDeath {
            target,
            card_source,
            was_removal_prevented: false,
        },
    )
}

pub fn modify_summon_amount(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    amount: Dec,
) -> Result<Dec, KernelError> {
    let mut result = amount;
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ModifySummonAmount,
            &HookArgs::ModifySummonAmount { amount: result },
        )? {
            if let Some(v) = res.dec_val {
                result = v;
            }
        }
    }
    Ok(result)
}

pub fn after_summon(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    amount: Dec,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterSummon,
        &HookArgs::ModifySummonAmount { amount },
    )
}

pub fn after_osty_revived(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    osty: CreatureHandle,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterOstyRevived,
        &HookArgs::Creature(osty),
    )
}

pub fn after_creature_added_to_combat(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    let listeners = collect_direct_combat_hook_listeners(state);
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterCreatureAddedToCombat,
        &HookArgs::Creature(creature),
    )
}

pub fn after_died_to_doom(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    let listeners = collect_direct_combat_hook_listeners(state);
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterDiedToDoom,
        &HookArgs::Creature(creature),
    )
}

pub fn should_disappear_from_doom(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
) -> Result<bool, KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ShouldDisappearFromDoom,
            &HookArgs::Creature(creature),
        )? {
            if res.bool_val == Some(false) {
                return Ok(false);
            }
        }
    }
    Ok(true)
}

pub fn modify_gold_gained(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    amount: Dec,
) -> Result<Dec, KernelError> {
    let mut result = amount;
    let listeners = collect_run_combat_listeners(state);
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ModifyGoldGained,
            &HookArgs::ModifySummonAmount { amount: result },
        )? {
            if let Some(v) = res.dec_val {
                result = v;
            }
        }
    }
    Ok(result)
}

pub fn after_gold_gained(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    let listeners = collect_run_combat_listeners(state);
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterGoldGained,
        &HookArgs::Player,
    )
}

pub fn should_procure_potion(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    potion_id: sts2_types::PotionId,
) -> Result<bool, KernelError> {
    let listeners = collect_run_combat_listeners(state);
    for listener in listeners {
        if let Some(res) = dispatch_if_active(
            state,
            content,
            listener,
            HookSlot::ShouldProcurePotion,
            &HookArgs::Potion {
                potion_id,
                target: None,
            },
        )? {
            if res.bool_val == Some(false) {
                return Ok(false);
            }
        }
    }
    Ok(true)
}

pub fn before_potion_used(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    potion_id: sts2_types::PotionId,
    target: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    let listeners = collect_run_combat_listeners(state);
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::BeforePotionUsed,
        &HookArgs::Potion { potion_id, target },
    )
}

pub fn after_potion_used(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    potion_id: sts2_types::PotionId,
) -> Result<(), KernelError> {
    let listeners = collect_run_combat_listeners(state);
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterPotionUsed,
        &HookArgs::Potion {
            potion_id,
            target: None,
        },
    )
}

pub fn after_potion_procured(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    potion_id: sts2_types::PotionId,
) -> Result<(), KernelError> {
    let listeners = collect_run_combat_listeners(state);
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterPotionProcured,
        &HookArgs::Potion {
            potion_id,
            target: None,
        },
    )
}

pub fn after_potion_discarded(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    potion_id: sts2_types::PotionId,
) -> Result<(), KernelError> {
    let listeners = collect_run_combat_listeners(state);
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterPotionDiscarded,
        &HookArgs::Potion {
            potion_id,
            target: None,
        },
    )
}

pub fn after_forge(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    amount: Dec,
) -> Result<(), KernelError> {
    let listeners = collect_combat_hook_listeners_checked(state, content)?;
    dispatch_void_list(
        state,
        content,
        &listeners,
        HookSlot::AfterForge,
        &HookArgs::Forge { amount },
    )
}
