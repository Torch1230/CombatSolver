use crate::state::BattleState;
use sts2_types::RelicId;

pub fn increment_stack(state: &mut BattleState, relic_id: RelicId) {
    if let Some(r) = state.player.relics.iter_mut().find(|r| r.id == relic_id) {
        r.stack_count += 1;
    }
}
