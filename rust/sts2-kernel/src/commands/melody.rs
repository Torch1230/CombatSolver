use crate::state::BattleState;
use sts2_types::CardTags;

pub fn played_this_turn(state: &BattleState, tag: CardTags) -> bool {
    match tag {
        CardTags::WAH => state.player.counters.wah_finished_this_turn > 0,
        CardTags::CHO => state.player.counters.cho_finished_this_turn > 0,
        CardTags::KI => state.player.counters.ki_finished_this_turn > 0,
        _ => false,
    }
}

pub fn melodies_this_turn(state: &BattleState) -> i32 {
    (if played_this_turn(state, CardTags::WAH) { 1 } else { 0 })
        + (if played_this_turn(state, CardTags::CHO) { 1 } else { 0 })
        + (if played_this_turn(state, CardTags::KI) { 1 } else { 0 })
}

pub fn melody_cards_played_this_turn(state: &BattleState) -> i32 {
    state.player.counters.melody_cards_finished_this_turn
}
