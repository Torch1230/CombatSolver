use crate::commands::creature::{gain_block_internal, lose_block_internal};
use crate::content_dispatch::{CardPlayContext, ContentDispatcher};
use crate::error::KernelError;
use crate::hooks;
use crate::state::{BattleState, CardHandle, CreatureHandle};
use sts2_types::{Dec, HookMask, HookSlot, PowerId, ValueProp};

pub fn gain_block(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
    amount: Dec,
    props: ValueProp,
    card_source: Option<CardHandle>,
    card_play: Option<CardPlayContext>,
) -> Result<Dec, KernelError> {
    const ALL_BLOCK_HOOKS: HookMask = HookMask::EMPTY
        .with(HookSlot::BeforeBlockGained)
        .with(HookSlot::ModifyBlockAdditive)
        .with(HookSlot::ModifyBlockMultiplicative)
        .with(HookSlot::AfterBlockGained);
    const UNPOWERED_BLOCK_SIDE_HOOKS: HookMask = HookMask::EMPTY
        .with(HookSlot::BeforeBlockGained)
        .with(HookSlot::AfterBlockGained);

    if hooks::is_over_or_ending_checked(state, content)?
        || state.creature(creature).is_none_or(|c| c.is_dead())
    {
        return Ok(Dec::ZERO);
    }

    if state.hook_cache.masks_are_valid() {
        let comb = state.hook_cache.masks.combat_combined;
        let can_fast_noop = !comb.intersects(ALL_BLOCK_HOOKS)
            || (props.contains(ValueProp::UNPOWERED)
                && !props.contains(ValueProp::MOVE)
                && card_source.is_none()
                && !comb.intersects(UNPOWERED_BLOCK_SIDE_HOOKS)
                && state
                    .hook_cache
                    .cached_tables
                    .is_some_and(|t| t.unpowered_block_no_card_shadowmeld_only)
                && state
                    .creature(creature)
                    .is_none_or(|c| !c.powers.iter().any(|p| p.id == PowerId::ShadowmeldPower)));
        if can_fast_noop {
            let modified_amount = amount.max(Dec::ZERO);
            #[cfg(debug_assertions)]
            {
                let mut dbg_state = state.clone();
                hooks::before_block_gained(
                    &mut dbg_state,
                    content,
                    creature,
                    amount,
                    props,
                    card_source,
                    card_play,
                )?;
                let dbg_mod = hooks::modify_block_and_notify(
                    &mut dbg_state,
                    content,
                    creature,
                    amount,
                    props,
                    card_source,
                    card_play,
                )?;
                debug_assert_eq!(dbg_mod, modified_amount);
            }
            if modified_amount > Dec::ZERO {
                gain_block_internal(state, creature, modified_amount)?;
                if state.is_live_combat() {
                    state.record_block_gain(props, card_play.map(|cp| cp.play_ordinal));
                }
            }
            return Ok(modified_amount);
        }
    }

    hooks::before_block_gained(state, content, creature, amount, props, card_source, card_play)?;
    let modified_amount = hooks::modify_block_and_notify(
        state,
        content,
        creature,
        amount,
        props,
        card_source,
        card_play,
    )?;

    if modified_amount > Dec::ZERO {
        gain_block_internal(state, creature, modified_amount)?;
        if state.is_live_combat() {
            state.record_block_gain(props, card_play.map(|cp| cp.play_ordinal));
        }
    }

    hooks::after_block_gained(state, content, creature, modified_amount, props, card_source)?;
    Ok(modified_amount)
}

pub fn lose_block(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    amount: Dec,
) -> Result<(), KernelError> {
    lose_block_by(state, content, target, amount, None)
}

pub fn lose_block_by(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    amount: Dec,
    remover: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    if hooks::is_over_or_ending_checked(state, content)?
        || amount <= Dec::ZERO
        || state.creature(target).is_none_or(|c| c.is_dead())
    {
        return Ok(());
    }
    let block_before = state.creature(target).map_or(0, |c| c.block);
    lose_block_internal(state, target, amount)?;
    let block_after = state.creature(target).map_or(0, |c| c.block);
    if block_before > 0 && block_after == 0 {
        hooks::after_block_broken(state, content, target, remover)?;
    }
    Ok(())
}
