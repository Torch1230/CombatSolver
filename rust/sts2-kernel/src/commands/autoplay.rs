use crate::commands::card_pile;
use crate::content_dispatch::ContentDispatcher;
use crate::continuation::{ContinuationFrame, StepOutcome};
use crate::engine::CombatEngine;
use crate::error::KernelError;
use crate::hooks;
use crate::ordering;
use crate::state::{BattleState, CardHandle, CardTempFlags, CreatureHandle};
use smallvec::SmallVec;
use sts2_types::{CardKeywords, CardPilePosition, CardStaticFlags, PileType, RunRngType, TargetType};

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct PrepaidXCapture {
    pub energy: Option<i32>,
    pub stars: Option<i32>,
}

pub fn prepay_resources_for_autoplay(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_handle: CardHandle,
) -> Result<PrepaidXCapture, KernelError> {
    let (card_id, pile) = state
        .card(card_handle)
        .map(|c| (c.id, c.pile))
        .ok_or_else(|| KernelError::InvalidState("missing prepaid card".to_owned()))?;
    if pile != PileType::Hand {
        return Err(KernelError::InvalidState(
            "Prepaid autoplay requires a card in hand.".to_owned(),
        ));
    }
    let flags = content.card_meta(card_id)?.flags;
    let is_x_energy = flags.contains(CardStaticFlags::IS_X_ENERGY_COST);
    let is_x_star = flags.contains(CardStaticFlags::IS_X_STAR_COST);

    let resolved_energy_cost = ordering::resolve_energy_cost(state, content, card_handle)?;
    let energy_to_spend = if is_x_energy {
        state.player.energy
    } else {
        resolved_energy_cost.min(state.player.energy).max(0)
    };
    let resolved_star_cost = ordering::resolve_star_cost(state, content, card_handle)?;
    let stars_to_spend = if is_x_star {
        state.player.stars
    } else {
        resolved_star_cost.min(state.player.stars).max(0)
    };

    if is_x_energy {
        if let Some(c) = state.card_mut(card_handle) {
            c.captured_energy_x_value = energy_to_spend;
        }
    }
    if energy_to_spend > 0 && state.is_live_combat() {
        state.record_energy_spent(energy_to_spend);
    }
    state.player.energy = (state.player.energy - energy_to_spend).max(0);
    hooks::after_energy_spent(state, content, card_handle, energy_to_spend)?;

    state.player.stars = (state.player.stars - stars_to_spend).max(0);
    if stars_to_spend > 0 {
        hooks::after_stars_spent(state, content, stars_to_spend)?;
    }

    Ok(PrepaidXCapture {
        energy: is_x_energy.then_some(energy_to_spend),
        stars: is_x_star.then_some(stars_to_spend),
    })
}

pub fn from_prepaid_card(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_handle: CardHandle,
    fixed_target: Option<CreatureHandle>,
    x_capture: PrepaidXCapture,
) -> Result<StepOutcome, KernelError> {
    if hooks::is_over_or_ending_checked(state, content)?
        || state.player_creature().is_none_or(|c| c.is_dead())
    {
        return Ok(StepOutcome::Completed);
    }

    if card_pile::card_has_keyword(state, content, card_handle, CardKeywords::UNPLAYABLE)?
        || !hooks::should_play(state, content, card_handle)?
    {
        card_pile::add_sync(state, card_handle, PileType::Play, CardPilePosition::Bottom);
        CombatEngine::move_to_result_pile_without_playing(state, content, card_handle)?;
        return Ok(StepOutcome::Completed);
    }

    let target_type = content.card_resolved_target_type(state, card_handle)?;
    let mut target = fixed_target;
    if target.is_none() && matches!(target_type, TargetType::AnyEnemy | TargetType::AnyAlly) {
        let candidates = match target_type {
            TargetType::AnyEnemy => {
                let mut list: SmallVec<[CreatureHandle; 8]> = SmallVec::new();
                let enemies: SmallVec<[CreatureHandle; 8]> =
                    SmallVec::from_slice(&state.enemy_handles);
                for eh in enemies {
                    if state.creature(eh).is_some_and(|c| c.is_alive())
                        && hooks::should_allow_hitting(state, content, eh)?
                    {
                        list.push(eh);
                    }
                }
                list
            }
            TargetType::AnyAlly => state
                .ally_handles
                .iter()
                .copied()
                .filter(|&ah| {
                    ah != state.player.creature_handle
                        && state.creature(ah).is_some_and(|c| c.is_alive())
                })
                .collect(),
            _ => SmallVec::new(),
        };
        target = state
            .run_rngs
            .get_mut(RunRngType::CombatTargets)
            .next_item(&candidates)
            .copied();
    }
    if target.is_none() && matches!(target_type, TargetType::AnyEnemy | TargetType::AnyAlly) {
        card_pile::add_sync(state, card_handle, PileType::Play, CardPilePosition::Bottom);
        CombatEngine::move_to_result_pile_without_playing(state, content, card_handle)?;
        return Ok(StepOutcome::Completed);
    }

    let mut choices: SmallVec<[Vec<usize>; 2]> = SmallVec::new();
    CombatEngine::autoplay_prepaid_card(
        state,
        content,
        card_handle,
        target,
        x_capture,
        &mut choices,
    )
}

pub fn prepay_and_autoplay_card(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_handle: CardHandle,
    fixed_target: Option<CreatureHandle>,
) -> Result<StepOutcome, KernelError> {
    let x_capture = prepay_resources_for_autoplay(state, content, card_handle)?;
    from_prepaid_card(state, content, card_handle, fixed_target, x_capture)
}

pub fn from_cards(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    cards: &[CardHandle],
    fixed_target: Option<CreatureHandle>,
    spend_resources: bool,
) -> Result<StepOutcome, KernelError> {
    let mut choices: SmallVec<[Vec<usize>; 2]> = SmallVec::new();
    play_cards_core(
        state,
        content,
        cards,
        fixed_target,
        spend_resources,
        None,
        true,
        &mut choices,
    )
}

pub fn from_top_of_draw_pile(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    count: usize,
    force_exhaust: bool,
    choices: &mut SmallVec<[Vec<usize>; 2]>,
) -> Result<StepOutcome, KernelError> {
    from_top_of_draw_pile_with_upgrade(state, content, count, force_exhaust, false, choices)
}

pub fn from_top_of_draw_pile_with_upgrade(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    count: usize,
    force_exhaust: bool,
    upgrade_drawn: bool,
    choices: &mut SmallVec<[Vec<usize>; 2]>,
) -> Result<StepOutcome, KernelError> {
    if hooks::is_over_or_ending_checked(state, content)? {
        return Ok(StepOutcome::Completed);
    }
    let mut preloaded: SmallVec<[CardHandle; 8]> = SmallVec::new();
    for _ in 0..count {
        card_pile::shuffle_if_necessary(state, content)?;
        if let Some(pending) = &state.pending {
            return Ok(StepOutcome::NeedsChoice(pending.request.clone()));
        }
        let Some(&top) = state.player.draw.first() else {
            break;
        };
        if upgrade_drawn {
            crate::commands::card::upgrade(state, content, top)?;
        }
        preloaded.push(top);
        card_pile::add_sync(state, top, PileType::Play, CardPilePosition::Bottom);
    }

    play_cards_core(
        state,
        content,
        &preloaded,
        None,
        false,
        Some(force_exhaust),
        false,
        choices,
    )
}

pub fn move_to_result_pile_without_playing(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_handle: CardHandle,
) -> Result<(), KernelError> {
    CombatEngine::move_to_result_pile_without_playing(state, content, card_handle)
}

#[allow(clippy::too_many_arguments)]
pub fn resume_play_cards_core(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    cards: &[CardHandle],
    fixed_target: Option<CreatureHandle>,
    spend_resources: bool,
    force_exhaust: Option<bool>,
    move_to_play: bool,
) -> Result<StepOutcome, KernelError> {
    let mut choices: SmallVec<[Vec<usize>; 2]> = SmallVec::new();
    play_cards_core(
        state,
        content,
        cards,
        fixed_target,
        spend_resources,
        force_exhaust,
        move_to_play,
        &mut choices,
    )
}

#[allow(clippy::too_many_arguments)]
fn play_cards_core(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    cards: &[CardHandle],
    fixed_target: Option<CreatureHandle>,
    spend_resources: bool,
    force_exhaust: Option<bool>,
    move_to_play: bool,
    choices: &mut SmallVec<[Vec<usize>; 2]>,
) -> Result<StepOutcome, KernelError> {
    for (idx, &card_handle) in cards.iter().enumerate() {
        if state.player_creature().is_none_or(|c| c.is_dead()) {
            break;
        }
        if let Some(force) = force_exhaust {
            if let Some(c) = state.card_mut(card_handle) {
                if force {
                    c.temp_flags.insert(CardTempFlags::EXHAUST_ON_NEXT_PLAY);
                } else {
                    c.temp_flags.remove(CardTempFlags::EXHAUST_ON_NEXT_PLAY);
                }
            }
        }
        if hooks::is_over_or_ending_checked(state, content)? {
            continue;
        }
        if move_to_play {
            card_pile::add_sync(state, card_handle, PileType::Play, CardPilePosition::Bottom);
        }

        if card_pile::card_has_keyword(state, content, card_handle, CardKeywords::UNPLAYABLE)?
            || !hooks::should_play(state, content, card_handle)?
        {
            CombatEngine::move_to_result_pile_without_playing(state, content, card_handle)?;
            continue;
        }

        let target_type = content.card_resolved_target_type(state, card_handle)?;

        let target = if matches!(target_type, TargetType::AnyEnemy | TargetType::AnyAlly)
            || (target_type == TargetType::AnyPlayer
                && spend_resources
                && fixed_target.is_some())
        {
            let mut candidates: SmallVec<[CreatureHandle; 8]> = SmallVec::new();
            match target_type {
                TargetType::AnyEnemy => {
                    let enemies: SmallVec<[CreatureHandle; 8]> =
                        SmallVec::from_slice(&state.enemy_handles);
                    for eh in enemies {
                        if state.creature(eh).is_some_and(|c| c.is_alive())
                            && hooks::should_allow_hitting(state, content, eh)?
                        {
                            candidates.push(eh);
                        }
                    }
                }
                TargetType::AnyAlly => {
                    for &ah in &state.ally_handles {
                        if ah != state.player.creature_handle
                            && state.creature(ah).is_some_and(|c| c.is_alive())
                        {
                            candidates.push(ah);
                        }
                    }
                }
                TargetType::AnyPlayer => {
                    candidates.push(state.player.creature_handle);
                }
                _ => {}
            }
            let chosen = if let Some(ft) = fixed_target {
                if candidates.contains(&ft) {
                    Some(ft)
                } else {
                    state
                        .run_rngs
                        .get_mut(RunRngType::CombatTargets)
                        .next_item(&candidates)
                        .copied()
                }
            } else {
                state
                    .run_rngs
                    .get_mut(RunRngType::CombatTargets)
                    .next_item(&candidates)
                    .copied()
            };
            let Some(resolved) = chosen else {
                CombatEngine::move_to_result_pile_without_playing(state, content, card_handle)?;
                continue;
            };
            Some(resolved)
        } else {
            None
        };

        let outcome = CombatEngine::autoplay_card(
            state,
            content,
            card_handle,
            target,
            spend_resources,
            choices,
        )?;
        if matches!(outcome, StepOutcome::NeedsChoice(_)) {
            if idx + 1 < cards.len() {
                if let Some(pending) = state.pending.as_mut() {
                    pending.frames.insert(
                        0,
                        ContinuationFrame::AutoPlayResume {
                            remaining_cards: cards[idx + 1..].iter().copied().collect(),
                            fixed_target,
                            spend_resources,
                            force_exhaust,
                            move_to_play,
                        },
                    );
                }
            }
            return Ok(outcome);
        }
        if !matches!(outcome, StepOutcome::Completed) {
            return Ok(outcome);
        }
    }

    Ok(StepOutcome::Completed)
}
