use crate::commands::card_pile;
use crate::content_dispatch::ContentDispatcher;
use crate::error::KernelError;
use crate::hooks;
use crate::state::{BattleState, CardHandle, FieldValue};
use smallvec::SmallVec;
use sts2_types::{CardId, CardPilePosition, Dec, PileType};

const SOVEREIGN_BLADE_DAMAGE_KEY: &str = "SovereignBlade._damage";

fn find_sovereign_blades(state: &BattleState, include_exhausted: bool) -> SmallVec<[CardHandle; 4]> {
    let mut out = SmallVec::new();
    let piles: [&[CardHandle]; 5] = [
        &state.player.hand,
        &state.player.draw,
        &state.player.discard,
        &state.player.play,
        if include_exhausted {
            &state.player.exhaust
        } else {
            &[]
        },
    ];
    for pile in piles {
        for &h in pile {
            if state.card(h).is_some_and(|c| c.id == CardId::SovereignBlade) {
                out.push(h);
            }
        }
    }
    out
}

pub fn forge(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    amount: Dec,
) -> Result<(), KernelError> {
    if hooks::is_over_or_ending_checked(state, content)? {
        return Ok(());
    }

    if find_sovereign_blades(state, false).is_empty() {
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
    }

    let blades = find_sovereign_blades(state, true);
    for h in blades {
        let ext = state
            .card_ext_mut(h)
            .ok_or_else(|| KernelError::InvalidState("missing sovereign blade".to_owned()))?;
        let cur = match ext.private_fields.get(SOVEREIGN_BLADE_DAMAGE_KEY) {
            Some(FieldValue::Decimal(d)) => *d,
            Some(FieldValue::Int(i)) => Dec::from_i32(*i),
            _ => Dec::from_i32(10),
        };
        let next = cur.checked_add(amount)?;
        ext.private_fields
            .insert(SOVEREIGN_BLADE_DAMAGE_KEY.to_owned(), FieldValue::Decimal(next));
    }

    hooks::after_forge(state, content, amount)
}
