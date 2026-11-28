use crate::commands::{creature, power};
use crate::content_dispatch::ContentDispatcher;
use crate::error::KernelError;
use crate::hooks;
use crate::state::{BattleState, CreatureHandle};
use sts2_types::{CombatSide, Dec, MonsterId, PowerId};

pub fn summon(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    raw_amount: Dec,
) -> Result<(Option<CreatureHandle>, Dec), KernelError> {
    if !state.is_live_combat() && !state.is_starting {
        return Err(KernelError::InvalidState(
            "Summoner must be in combat".to_owned(),
        ));
    }
    let amount = hooks::modify_summon_amount(state, content, raw_amount)?;
    if amount.is_zero() {
        return Ok((state.osty_handle(), Dec::ZERO));
    }

    let existing_osty = state
        .ally_handles
        .iter()
        .copied()
        .find(|&h| {
            state
                .creature(h)
                .is_some_and(|c| c.monster_id == Some(MonsterId::Osty) && c.is_pet())
        });

    let osty_handle = if state.is_osty_alive() {
        let h = state.osty_handle().expect("alive osty has handle");
        creature::gain_max_hp(state, content, h, amount)?;
        h
    } else if let Some(h) = existing_osty {
        if state.creature(h).is_some_and(|c| c.is_alive()) {
            return Err(KernelError::InvalidState(
                "Osty is alive but not registered as pet".to_owned(),
            ));
        }
        if !state.pet_handles.contains(&h) {
            state.pet_handles.push(h);
        }
        creature::set_max_hp(state, h, amount)?;
        creature::heal(state, content, h, amount)?;
        hooks::after_osty_revived(state, content, h)?;
        h
    } else {
        let h = creature::create_and_add_monster(
            state,
            content,
            MonsterId::Osty,
            CombatSide::Player,
            None,
        )?;
        if !state.pet_handles.contains(&h) {
            state.pet_handles.push(h);
        }
        power::apply(state, content, PowerId::DieForYouPower, h, Dec::ONE, None, None)?;
        creature::set_max_hp(state, h, amount)?;
        creature::heal(state, content, h, amount)?;
        h
    };

    hooks::after_summon(state, content, amount)?;
    Ok((Some(osty_handle), amount))
}
