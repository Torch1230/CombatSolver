use crate::content_dispatch::ContentDispatcher;
use crate::error::KernelError;
use crate::hooks;
use crate::state::BattleState;
use sts2_types::Dec;

pub fn gain_gold(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    amount: Dec,
) -> Result<(), KernelError> {
    let modified = hooks::modify_gold_gained(state, content, amount)?;
    if modified > Dec::ZERO {
        state.player.gold += modified.to_i32_trunc()?;
        hooks::after_gold_gained(state, content)?;
    }
    Ok(())
}

pub fn lose_gold(state: &mut BattleState, amount: Dec) -> Result<(), KernelError> {
    state.player.gold = (state.player.gold - amount.to_i32_trunc()?).max(0);
    Ok(())
}

pub fn gain_energy(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    amount: Dec,
) -> Result<(), KernelError> {
    if amount <= Dec::ZERO || hooks::is_ending_checked(state, content)? {
        return Ok(());
    }
    let modified = hooks::modify_energy_gain(state, content, amount)?;
    if modified <= Dec::ZERO {
        return Ok(());
    }
    let next = Dec::from_i32(state.player.energy)
        .checked_add(modified)?
        .clamp(Dec::ZERO, Dec::CLAMP_MAX_999M)
        .to_i32_trunc()?;
    state.player.energy = next;
    Ok(())
}

pub fn lose_energy(state: &mut BattleState, amount: Dec) -> Result<(), KernelError> {
    if amount <= Dec::ZERO || hooks::is_ending(state) {
        return Ok(());
    }
    let next = Dec::from_i32(state.player.energy)
        .checked_sub(amount)?
        .clamp(Dec::ZERO, Dec::CLAMP_MAX_999M)
        .to_i32_trunc()?;
    state.player.energy = next;
    Ok(())
}

pub fn gain_stars(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    amount: Dec,
) -> Result<(), KernelError> {
    if !state.is_live_combat() && !state.is_starting {
        return Ok(());
    }
    if hooks::is_over_or_ending_checked(state, content)? && !state.is_starting {
        return Ok(());
    }
    if hooks::should_gain_stars(state, content)? {
        if amount > Dec::ZERO {
            state.player.counters.stars_gained_this_turn = state
                .player
                .counters
                .stars_gained_this_turn
                .checked_add(amount)?;
        }
        let next = Dec::from_i32(state.player.stars)
            .checked_add(amount)?
            .max(Dec::ZERO)
            .to_i32_trunc()?;
        state.player.stars = next;
        hooks::after_stars_gained(state, content, amount.to_i32_trunc()?)?;
    }
    Ok(())
}

pub fn lose_stars(state: &mut BattleState, amount: Dec) -> Result<(), KernelError> {
    if hooks::is_over_or_ending(state) && !state.is_starting {
        return Ok(());
    }
    let next = Dec::from_i32(state.player.stars)
        .checked_sub(amount)?
        .max(Dec::ZERO)
        .to_i32_trunc()?;
    state.player.stars = next;
    Ok(())
}
