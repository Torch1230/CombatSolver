use crate::continuation::{ChoiceRequest, ChoiceTiming};
use crate::error::KernelError;
use crate::hooks;
use crate::state::{BattleState, CardHandle};
use smallvec::SmallVec;
use sts2_types::PileType;

pub enum SelectResult {
    Selected(SmallVec<[CardHandle; 8]>),
    NeedsChoice(ChoiceRequest),
}

/// Exact port of `CardSelectCmd.FromHandForDiscard` + `SelectCardsAsync`:
/// - Returns empty if combat is over/ending or 0 candidates.
/// - Auto-selects all candidates if `candidates.len() <= min_count && !cancelable`.
/// - Consumes queued choice indices if provided; otherwise pauses with `NeedsChoice`.
pub fn from_hand_for_discard(
    state: &BattleState,
    count: usize,
    source_id: &str,
    choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
) -> Result<SelectResult, KernelError> {
    if hooks::is_over_or_ending(state) {
        return Ok(SelectResult::Selected(SmallVec::new()));
    }
    let candidates: SmallVec<[CardHandle; 16]> = state.player.hand.iter().copied().collect();
    select_cards(
        state,
        &candidates,
        count,
        count,
        ChoiceTiming::Action,
        source_id,
        PileType::Hand,
        false,
        choice_queue,
    )
}

#[allow(clippy::too_many_arguments)]
pub fn select_cards(
    state: &BattleState,
    candidates: &[CardHandle],
    min_count: usize,
    max_count: usize,
    timing: ChoiceTiming,
    source_id: &str,
    source_pile: PileType,
    cancelable: bool,
    choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
) -> Result<SelectResult, KernelError> {
    if max_count < min_count {
        return Err(KernelError::InvalidAction(
            "max_count cannot be less than min_count".to_owned(),
        ));
    }
    if candidates.is_empty() {
        return Ok(SelectResult::Selected(SmallVec::new()));
    }
    let effective_max = max_count.min(candidates.len());
    let effective_min = min_count.min(effective_max);

    if candidates.len() <= min_count && !cancelable {
        return Ok(SelectResult::Selected(candidates.iter().copied().collect()));
    }

    if state.is_vakuu_auto_select_active() {
        return Ok(SelectResult::Selected(
            candidates.iter().take(effective_max).copied().collect(),
        ));
    }

    if !choice_queue.is_empty() {
        let indices = choice_queue.remove(0);
        return resolve_selection_indices(candidates, effective_min, effective_max, cancelable, &indices);
    }

    Ok(SelectResult::NeedsChoice(ChoiceRequest {
        timing,
        source_id: source_id.to_owned(),
        source_pile,
        min_count: effective_min,
        max_count: effective_max,
        candidates: candidates.iter().copied().collect(),
        can_skip: cancelable,
    }))
}

pub fn resolve_selection_indices(
    candidates: &[CardHandle],
    min_count: usize,
    max_count: usize,
    cancelable: bool,
    indices: &[usize],
) -> Result<SelectResult, KernelError> {
    if (cancelable && indices.is_empty()) || (indices.len() >= min_count && indices.len() <= max_count) {
        let mut selected = SmallVec::<[CardHandle; 8]>::new();
        for &idx in indices {
            let Some(&card) = candidates.get(idx) else {
                return Err(KernelError::InvalidAction(format!(
                    "choice index {idx} out of range (candidates={})",
                    candidates.len()
                )));
            };
            if selected.contains(&card) {
                return Err(KernelError::InvalidAction(format!(
                    "duplicate choice index {idx}"
                )));
            }
            selected.push(card);
        }
        return Ok(SelectResult::Selected(selected));
    }
    Err(KernelError::InvalidAction(format!(
        "choice returned {} cards; expected {min_count}..{max_count}",
        indices.len()
    )))
}
