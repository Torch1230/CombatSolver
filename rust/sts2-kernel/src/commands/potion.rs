use crate::commands::card_pile;
use crate::content_dispatch::ContentDispatcher;
use crate::continuation::{ContinuationFrame, PendingContinuation, StepOutcome};
use crate::error::KernelError;
use crate::hooks;
use crate::state::{BattleState, CardHandle, CreatureHandle};
use smallvec::SmallVec;
use sts2_types::PotionId;

pub fn can_procure(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    potion_id: PotionId,
) -> Result<bool, KernelError> {
    hooks::should_procure_potion(state, content, potion_id)
}

pub fn try_to_procure(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    potion_id: PotionId,
) -> Result<bool, KernelError> {
    let _ = content.potion_meta(potion_id)?;
    if !can_procure(state, content, potion_id)? {
        return Ok(false);
    }
    let Some(empty_slot) = state.player.potion_slots.iter().position(|s| s.is_none()) else {
        return Ok(false);
    };
    state.player.potion_slots[empty_slot] = Some(potion_id);
    hooks::after_potion_procured(state, content, potion_id)?;
    Ok(true)
}

pub fn discard_potion(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    potion_slot: usize,
) -> Result<(), KernelError> {
    if !state.player.can_use_or_remove_potions {
        return Err(KernelError::InvalidAction(
            "potions are locked by the current event".to_owned(),
        ));
    }
    let Some(Some(potion_id)) = state.player.potion_slots.get(potion_slot).copied() else {
        return Err(KernelError::InvalidAction(format!(
            "potion slot {potion_slot} is empty or out of bounds"
        )));
    };
    state.player.potion_slots[potion_slot] = None;
    hooks::after_potion_discarded(state, content, potion_id)
}

pub fn use_potion(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    potion_slot: usize,
    target: Option<CreatureHandle>,
    choices: &mut SmallVec<[Vec<usize>; 2]>,
) -> Result<StepOutcome, KernelError> {
    use_internal(state, content, potion_slot, target, false, choices)
}

pub fn use_automatic(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    potion_slot: usize,
    target: Option<CreatureHandle>,
) -> Result<StepOutcome, KernelError> {
    let mut empty = SmallVec::new();
    use_internal(state, content, potion_slot, target, true, &mut empty)
}

fn use_internal(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    potion_slot: usize,
    target: Option<CreatureHandle>,
    is_automatic: bool,
    choices: &mut SmallVec<[Vec<usize>; 2]>,
) -> Result<StepOutcome, KernelError> {
    let Some(Some(potion_id)) = state.player.potion_slots.get(potion_slot).copied() else {
        return Err(KernelError::InvalidAction(format!(
            "potion slot {potion_slot} is empty or out of bounds"
        )));
    };
    if !is_automatic && !state.player.can_use_or_remove_potions {
        return Err(KernelError::InvalidAction(
            "potions are locked by the current event".to_owned(),
        ));
    }
    let _ = content.potion_meta(potion_id)?;
    state.player.potion_slots[potion_slot] = None;
    hooks::before_potion_used(state, content, potion_id, target)?;

    state.player.card_or_potion_effect_depth += 1;
    let outcome = content.potion_on_use(state, potion_id, target, choices, 0)?;
    if let Some((req, next_step)) = outcome.needs_choice {
        state.pending = Some(PendingContinuation {
            request: req.clone(),
            frames: smallvec::smallvec![ContinuationFrame::PotionUseResume {
                potion_slot,
                potion_id,
                target,
                resume_step: next_step,
            }],
        });
        return Ok(StepOutcome::NeedsChoice(req));
    }

    finish_potion_use(state, content, potion_id)?;
    Ok(StepOutcome::Completed)
}

pub fn resume_potion_use(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    potion_slot: usize,
    potion_id: PotionId,
    target: Option<CreatureHandle>,
    selected: &[CardHandle],
    resume_step: u8,
) -> Result<Option<PendingContinuation>, KernelError> {
    let outcome =
        content.potion_on_use_resume_after_select(state, potion_id, target, selected, resume_step)?;
    if let Some((next_req, next_step)) = outcome.needs_choice {
        return Ok(Some(PendingContinuation {
            request: next_req,
            frames: smallvec::smallvec![ContinuationFrame::PotionUseResume {
                potion_slot,
                potion_id,
                target,
                resume_step: next_step,
            }],
        }));
    }
    finish_potion_use(state, content, potion_id)?;
    Ok(None)
}

fn finish_potion_use(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    potion_id: PotionId,
) -> Result<(), KernelError> {
    state.player.card_or_potion_effect_depth =
        (state.player.card_or_potion_effect_depth - 1).max(0);
    if state.player_creature().is_some_and(|c| c.is_alive()) {
        hooks::after_potion_used(state, content, potion_id)?;
        card_pile::check_for_empty_hand(state, content)?;
    }
    Ok(())
}
