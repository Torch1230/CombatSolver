use crate::commands::card;
use crate::content_dispatch::ContentDispatcher;
use crate::continuation::{ContinuationFrame, PendingContinuation};
use crate::error::KernelError;
use crate::hooks;
use crate::ordering;
use crate::state::{BattleState, CardHandle, CardInst, CardTempFlags, CreatureHandle};
use smallvec::SmallVec;
use sts2_types::{CardId, CardKeywords, CardPilePosition, CardStaticFlags, PileType};

pub const MAX_CARDS_IN_HAND: usize = 10;

fn get_insertion_index(state: &mut BattleState, pile: PileType, pos: CardPilePosition) -> usize {
    let len = match pile {
        PileType::Draw => state.player.draw.len(),
        PileType::Hand => state.player.hand.len(),
        PileType::Discard => state.player.discard.len(),
        PileType::Exhaust => state.player.exhaust.len(),
        PileType::Play => state.player.play.len(),
        PileType::Deck => state.player.deck.len(),
        PileType::None => 0,
    };
    match pos {
        CardPilePosition::Top => 0,
        CardPilePosition::Random => state
            .run_rngs
            .get_mut(sts2_types::RunRngType::Shuffle)
            .next_int((len + 1) as i32)
            .unwrap_or(0) as usize,
        CardPilePosition::Bottom => len,
    }
}

pub fn add_sync(
    state: &mut BattleState,
    card: CardHandle,
    mut pile: PileType,
    pos: CardPilePosition,
) {
    state.remove_card_from_current_pile(card);
    if pile == PileType::Hand && state.player.hand.len() >= MAX_CARDS_IN_HAND {
        pile = PileType::Discard;
    }
    let idx = get_insertion_index(state, pile, pos);
    state.add_card_to_pile_at(card, pile, Some(idx));
}

pub fn add_and_notify(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    cards: &[CardHandle],
    pile: PileType,
    pos: CardPilePosition,
) -> Result<(), KernelError> {
    if cards.is_empty() || hooks::is_ending_checked(state, content)? || !state.is_live_combat() {
        return Ok(());
    }
    let mut moved: SmallVec<[(CardHandle, PileType); 8]> = SmallVec::new();
    for &c in cards {
        let old = state.card(c).map_or(PileType::None, |ci| ci.pile);
        if matches!(old, PileType::None | PileType::Deck) || state.player_creature().is_none_or(|cr| cr.is_dead()) {
            continue;
        }
        let mut dest = pile;
        if dest == PileType::Hand && state.player.hand.len() >= MAX_CARDS_IN_HAND {
            dest = PileType::Discard;
        }
        state.remove_card_from_current_pile(c);
        let idx = get_insertion_index(state, dest, pos);
        state.add_card_to_pile_at(c, dest, Some(idx));
        let new_pile = state.card(c).map_or(PileType::None, |ci| ci.pile);
        if old != new_pile {
            moved.push((c, old));
        }
    }
    for (c, old) in moved {
        hooks::after_card_changed_piles(state, content, c, old)?;
    }
    Ok(())
}

pub fn remove(state: &mut BattleState, card: CardHandle) {
    state.remove_card_from_current_pile(card);
    if let Some(c) = state.card_mut(card) {
        c.removed_from_state = true;
    }
}

pub fn exhaust(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    caused_by_ethereal: bool,
) -> Result<(), KernelError> {
    if hooks::is_over_or_ending_checked(state, content)? {
        return Ok(());
    }
    let departed_hand = state.card(card).is_some_and(|c| c.pile == PileType::Hand);
    add_and_notify(state, content, &[card], PileType::Exhaust, CardPilePosition::Bottom)?;
    state.record_exhaust();
    hooks::after_card_exhausted(state, content, card, caused_by_ethereal)?;
    if !caused_by_ethereal {
        notify_hand_departure(state, content, departed_hand)?;
    }
    Ok(())
}

pub fn move_to_result_pile_without_playing(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
) -> Result<(), KernelError> {
    crate::engine::CombatEngine::move_to_result_pile_without_playing(state, content, card)
}

pub fn notify_hand_departure(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    departed_hand: bool,
) -> Result<(), KernelError> {
    if departed_hand {
        check_for_empty_hand(state, content)?;
    }
    Ok(())
}

pub fn check_for_empty_hand(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    if state.is_live_combat()
        && state.player.card_or_potion_effect_depth == 0
        && state.player.hand.is_empty()
        && state.player_creature().is_some_and(|c| c.is_alive())
    {
        hooks::after_hand_emptied(state, content)?;
    }
    Ok(())
}

pub fn shuffle_if_necessary(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    if state.player.draw.is_empty() && !state.player.discard.is_empty() {
        shuffle(state, content)?;
    }
    Ok(())
}

pub fn create_card_in_combat(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_id: CardId,
    upgrade_level: i32,
) -> Result<CardHandle, KernelError> {
    let meta = content.card_meta(card_id)?;
    let handle = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    let mut temp_flags = CardTempFlags::empty();
    if meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST) {
        temp_flags |= CardTempFlags::COSTS_X_ENERGY;
    }
    state.cards.push(CardInst {
        handle,
        id: card_id,
        upgrade_level: 0,
        canonical_base_energy_cost: meta.canonical_energy_cost,
        base_energy_cost: meta.canonical_energy_cost,
        captured_energy_x_value: 0,
        base_replay_count: 0,
        floor_added_to_deck: None,
        deck_card_handle: None,
        clone_of_handle: None,
        temp_star_cost_override_this_turn: None,
        temp_cost_override_this_turn: None,
        temp_cost_override_this_turn_or_until_played: None,
        temp_cost_override_this_combat: None,
        local_keywords: meta.base_keywords,
        keywords_added_from_outside: CardKeywords::empty(),
        keywords_removed_from_outside: CardKeywords::empty(),
        temp_flags,
        affliction: None,
        ext_index: None,
        pile: PileType::None,
        removed_from_state: false,
        transform_into_after_play: None,
    });
    for _ in 0..upgrade_level {
        card::upgrade(state, content, handle)?;
    }
    Ok(handle)
}

pub fn enter_combat(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_handle: CardHandle,
    pile: PileType,
    pos: CardPilePosition,
    record_generated: bool,
) -> Result<(), KernelError> {
    enter_combat_internal(state, content, card_handle, pile, pos, None, record_generated)
}

pub fn enter_combat_at_index(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_handle: CardHandle,
    pile: PileType,
    index: usize,
    record_generated: bool,
) -> Result<(), KernelError> {
    enter_combat_internal(
        state,
        content,
        card_handle,
        pile,
        CardPilePosition::Bottom,
        Some(index),
        record_generated,
    )
}

fn enter_combat_internal(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_handle: CardHandle,
    mut pile: PileType,
    pos: CardPilePosition,
    insertion_index: Option<usize>,
    record_generated: bool,
) -> Result<(), KernelError> {
    if !matches!(
        pile,
        PileType::Draw | PileType::Hand | PileType::Discard | PileType::Exhaust | PileType::Play
    ) {
        return Err(KernelError::InvalidAction(
            "new combat cards must enter a combat pile".to_owned(),
        ));
    }
    let is_sadness = state
        .card(card_handle)
        .is_some_and(|c| c.id == CardId::Sadness);
    if hooks::is_ending_checked(state, content)?
        || (insertion_index.is_none() && state.player_creature().is_none_or(|c| c.is_dead()))
    {
        if record_generated {
            state.player.counters.cards_generated_this_combat += 1;
            if is_sadness {
                state.player.counters.sadness_generated_this_combat += 1;
            }
        }
        return Ok(());
    }
    if let Some(idx) = insertion_index {
        state.remove_card_from_current_pile(card_handle);
        if pile == PileType::Hand && state.player.hand.len() >= MAX_CARDS_IN_HAND {
            pile = PileType::Discard;
        }
        state.add_card_to_pile_at(card_handle, pile, Some(idx));
    } else {
        add_sync(state, card_handle, pile, pos);
    }
    if record_generated {
        state.player.counters.cards_generated_this_combat += 1;
        if is_sadness {
            state.player.counters.sadness_generated_this_combat += 1;
        }
    }
    hooks::after_card_entered_combat(state, content, card_handle)
}

pub fn add_generated_card_to_combat(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_id: CardId,
    pile: PileType,
    pos: CardPilePosition,
    upgrade_level: i32,
    creator: Option<CreatureHandle>,
) -> Result<CardHandle, KernelError> {
    let card_handle = create_card_in_combat(state, content, card_id, upgrade_level)?;
    enter_combat(state, content, card_handle, pile, pos, creator.is_some())?;
    hooks::after_card_generated(state, content, card_handle, creator)?;
    hooks::after_card_generated_for_combat(state, content, card_handle, creator)?;
    Ok(card_handle)
}

pub fn shuffle(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    if hooks::is_over_or_ending_checked(state, content)? {
        return Ok(());
    }
    let mut combined: Vec<CardHandle> = state
        .player
        .discard
        .iter()
        .copied()
        .chain(state.player.draw.iter().copied())
        .collect();
    let mut shuffle_rng = *state.run_rngs.get(sts2_types::RunRngType::Shuffle);
    ordering::stable_shuffle_cards(state, content, &mut combined, &mut shuffle_rng)?;
    *state.run_rngs.get_mut(sts2_types::RunRngType::Shuffle) = shuffle_rng;

    let enchanted_cards: Vec<CardHandle> = combined
        .iter()
        .copied()
        .filter(|&h| {
            state
                .card(h)
                .and_then(|c| state.card_ext(c.ext_index))
                .and_then(|e| e.enchantment.as_ref())
                .is_some()
        })
        .collect();
    for ch in enchanted_cards {
        content.enchantment_modify_shuffle_order(state, ch, &mut combined, false)?;
    }

    state.player.discard.clear();
    state.player.draw.clear();
    for &c in &combined {
        state.player.draw.push(c);
        if let Some(card) = state.card_mut(c) {
            card.pile = PileType::Draw;
        }
    }
    if let Some((req, paused_listener, remaining_listeners)) =
        hooks::after_shuffle(state, content)?
    {
        state.pending = Some(PendingContinuation {
            request: req,
            frames: smallvec::smallvec![ContinuationFrame::AfterShuffleResume {
                paused_listener,
                remaining_listeners,
                remaining_draws: 0,
                from_hand_draw: false,
                resume_turn_start: None,
            }],
        });
    }
    Ok(())
}

pub fn draw(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    count: i32,
    from_hand_draw: bool,
) -> Result<SmallVec<[CardHandle; 10]>, KernelError> {
    if hooks::is_over_or_ending_checked(state, content)? {
        return Ok(SmallVec::new());
    }
    if !hooks::should_draw(state, content, from_hand_draw)? {
        return Ok(SmallVec::new());
    }
    let mut drawn = SmallVec::new();
    for i in 0..count {
        if state.player.hand.len() >= MAX_CARDS_IN_HAND
            || hooks::is_over_or_ending_checked(state, content)?
        {
            break;
        }
        shuffle_if_necessary(state, content)?;
        if let Some(pending) = state.pending.as_mut() {
            if let Some(ContinuationFrame::AfterShuffleResume {
                remaining_draws,
                from_hand_draw: fhd,
                ..
            }) = pending.frames.last_mut()
            {
                *remaining_draws = count - i;
                *fhd = from_hand_draw;
            }
            break;
        }
        let Some(&card) = state.player.draw.first() else {
            break;
        };
        if state.player.hand.len() >= MAX_CARDS_IN_HAND {
            break;
        }
        state.player.draw.remove(0);
        state.player.hand.push(card);
        if let Some(ci) = state.card_mut(card) {
            ci.pile = PileType::Hand;
        }
        state.player.counters.cards_drawn_this_combat += 1;
        if state.is_live_combat() {
            let card_type = content.card_resolved_type(state, card)?;
            state.record_card_drawn(card_type, from_hand_draw);
        }
        drawn.push(card);
        hooks::after_card_drawn(state, content, card, from_hand_draw)?;
    }
    Ok(drawn)
}

pub fn card_has_keyword(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    kw: CardKeywords,
) -> Result<bool, KernelError> {
    let ci = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?;
    if (kw == CardKeywords::RETAIN
        && ci.temp_flags.contains(CardTempFlags::TEMPORARY_RETAIN_TURN))
        || (kw == CardKeywords::SLY
            && ci.temp_flags.contains(CardTempFlags::TEMPORARY_SLY_TURN))
    {
        return Ok(true);
    }
    let base = ci.effective_keywords_base();
    if base.contains(kw) {
        return Ok(true);
    }
    let modified = hooks::modify_keywords_in_combat(state, content, card, base)?;
    Ok(modified.contains(kw))
}
