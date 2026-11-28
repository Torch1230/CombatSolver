use crate::commands::card_pile;
use crate::content_dispatch::ContentDispatcher;
use crate::error::KernelError;
use crate::hooks;
use crate::state::{BattleState, CardHandle, FieldValue};
use smallvec::SmallVec;
use std::borrow::Cow;
use sts2_types::{CardId, CardPilePosition, Dec, PileType};

const SOVEREIGN_BLADE_DAMAGE_KEY: &str = "SovereignBlade._damage";

fn collect_sovereign_blades_single_pass(
    state: &BattleState,
) -> (SmallVec<[CardHandle; 4]>, bool) {
    let mut blades: SmallVec<[CardHandle; 4]> = SmallVec::new();
    let mut has_active_blade = false;
    for pile in [
        &state.player.hand,
        &state.player.draw,
        &state.player.discard,
        &state.player.play,
    ] {
        for &h in pile {
            if state.card(h).is_some_and(|c| c.id == CardId::SovereignBlade) {
                has_active_blade = true;
                blades.push(h);
            }
        }
    }
    for &h in &state.player.exhaust {
        if state.card(h).is_some_and(|c| c.id == CardId::SovereignBlade) {
            blades.push(h);
        }
    }
    (blades, has_active_blade)
}

pub fn forge(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    amount: Dec,
) -> Result<(), KernelError> {
    if hooks::is_over_or_ending_checked(state, content)? {
        return Ok(());
    }

    let (mut blades, has_active_blade) = collect_sovereign_blades_single_pass(state);
    if !has_active_blade {
        let creator = Some(state.player.creature_handle);
        let _ = card_pile::add_generated_card_to_combat(
            state,
            content,
            CardId::SovereignBlade,
            PileType::Hand,
            CardPilePosition::Bottom,
            0,
            creator,
        )?;
        blades = collect_sovereign_blades_single_pass(state).0;
    }

    for h in blades {
        let ext = state
            .card_ext_mut(h)
            .ok_or_else(|| KernelError::InvalidState("missing sovereign blade".to_owned()))?;
        if let Some(slot) = ext.private_fields.get_mut(SOVEREIGN_BLADE_DAMAGE_KEY) {
            let cur = match *slot {
                FieldValue::Decimal(d) => d,
                FieldValue::Int(i) => Dec::from_i32(i),
                _ => Dec::from_i32(10),
            };
            *slot = FieldValue::Decimal(cur.checked_add(amount)?);
        } else {
            let next = Dec::from_i32(10).checked_add(amount)?;
            ext.private_fields
                .insert(Cow::Borrowed(SOVEREIGN_BLADE_DAMAGE_KEY), FieldValue::Decimal(next));
        }
    }

    hooks::after_forge(state, content, amount)
}
