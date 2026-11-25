use crate::state::BattleState;
use sts2_types::RelicId;

pub fn increment_stack(state: &mut BattleState, relic_id: RelicId) {
    if let Some((idx, r)) = state
        .player
        .relics
        .iter_mut()
        .enumerate()
        .find(|(_, r)| r.id == relic_id)
    {
        r.stack_count += 1;
        state.mark_relic_dirty(idx);
    }
}
