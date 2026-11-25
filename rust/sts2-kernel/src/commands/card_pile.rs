use crate::commands::card;
use crate::content_dispatch::ContentDispatcher;
use crate::continuation::{ContinuationFrame, PendingContinuation};
use crate::error::KernelError;
use crate::hooks;
use crate::ordering;
use crate::state::{
    BattleState, CardHandle, CardInst, CardTempFlags, CreatureHandle, OptCardHandle, OptI16,
};
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

fn move_card_in_combat_piles_sync(
    state: &mut BattleState,
    card: CardHandle,
    card_idx: usize,
    old_pile: PileType,
    dest_pile: PileType,
    pos: CardPilePosition,
    tables: &crate::content_dispatch::HookMaskTableView,
) {
    let rem_pos = state.remove_card_from_pile_vec_only(card, old_pile);
    let idx = get_insertion_index(state, dest_pile, pos);
    let (insert_idx, old_len) = state.insert_card_into_pile_vec_only(card, dest_pile, Some(idx));
    if card_idx < state.static_ctx.root_deck_prefix_len {
        state.deck_dirty.0 = true;
    }
    state.cards[card_idx].pile = dest_pile;
    if !state.try_update_hook_cache_for_combat_pile_move(
        card,
        card_idx,
        old_pile,
        rem_pos,
        dest_pile,
        insert_idx,
        old_len,
        tables,
    ) {
        state.invalidate_pile_hooks(
            crate::state::pile_dirty_bit(old_pile) | crate::state::pile_dirty_bit(dest_pile),
        );
    }
}

pub fn add_sync(
    state: &mut BattleState,
    card: CardHandle,
    mut pile: PileType,
    pos: CardPilePosition,
) {
    let tables = state.hook_cache.cached_tables;
    if let (Some(t), Some(card_idx)) = (tables, state.card_index(card)) {
        if state.hook_cache.dirty_mask == 0 {
            let old = state.cards[card_idx].pile;
            if crate::state::pile_dirty_bit(old) != 0
                && old != PileType::Deck
                && crate::state::pile_dirty_bit(pile) != 0
                && pile != PileType::Deck
            {
                let rem_pos = state.remove_card_from_pile_vec_only(card, old);
                if pile == PileType::Hand && state.player.hand.len() >= MAX_CARDS_IN_HAND {
                    pile = PileType::Discard;
                }
                let idx = get_insertion_index(state, pile, pos);
                let (insert_idx, old_len) =
                    state.insert_card_into_pile_vec_only(card, pile, Some(idx));
                if card_idx < state.static_ctx.root_deck_prefix_len {
                    state.deck_dirty.0 = true;
                }
                state.cards[card_idx].pile = pile;
                if !state.try_update_hook_cache_for_combat_pile_move(
                    card,
                    card_idx,
                    old,
                    rem_pos,
                    pile,
                    insert_idx,
                    old_len,
                    t,
                ) {
                    state.invalidate_pile_hooks(
                        crate::state::pile_dirty_bit(old) | crate::state::pile_dirty_bit(pile),
                    );
                }
                return;
            }
        }
    }
    state.remove_card_from_current_pile_with_tables(card, tables);
    if pile == PileType::Hand && state.player.hand.len() >= MAX_CARDS_IN_HAND {
        pile = PileType::Discard;
    }
    let idx = get_insertion_index(state, pile, pos);
    state.add_card_to_pile_at_with_tables(card, pile, Some(idx), tables);
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
    let tables = state
        .hook_cache
        .cached_tables
        .or_else(|| content.hook_mask_tables());
    let dest_is_combat = crate::state::pile_dirty_bit(pile) != 0 && pile != PileType::Deck;
    if cards.len() == 1 {
        let c = cards[0];
        let old = state.card(c).map_or(PileType::None, |ci| ci.pile);
        if matches!(old, PileType::None | PileType::Deck)
            || state.player_creature().is_none_or(|cr| cr.is_dead())
        {
            return Ok(());
        }
        let mut dest = pile;
        if dest == PileType::Hand && state.player.hand.len() >= MAX_CARDS_IN_HAND {
            dest = PileType::Discard;
        }
        if dest_is_combat && state.hook_cache.dirty_mask == 0 {
            if let (Some(t), Some(card_idx)) = (tables, state.card_index(c)) {
                move_card_in_combat_piles_sync(state, c, card_idx, old, dest, pos, t);
                if old != dest && state.card(c).is_some() {
                    hooks::after_card_changed_piles(state, content, c, old)?;
                }
                return Ok(());
            }
        }
        state.remove_card_from_current_pile_with_tables(c, tables);
        let idx = get_insertion_index(state, dest, pos);
        state.add_card_to_pile_at_with_tables(c, dest, Some(idx), tables);
        if old != dest && state.card(c).is_some() {
            hooks::after_card_changed_piles(state, content, c, old)?;
        }
        return Ok(());
    }
    let mut moved: SmallVec<[(CardHandle, PileType); 16]> = SmallVec::new();
    for &c in cards {
        let old = state.card(c).map_or(PileType::None, |ci| ci.pile);
        if matches!(old, PileType::None | PileType::Deck) || state.player_creature().is_none_or(|cr| cr.is_dead()) {
            continue;
        }
        let mut dest = pile;
        if dest == PileType::Hand && state.player.hand.len() >= MAX_CARDS_IN_HAND {
            dest = PileType::Discard;
        }
        if dest_is_combat && state.hook_cache.dirty_mask == 0 {
            if let (Some(t), Some(card_idx)) = (tables, state.card_index(c)) {
                move_card_in_combat_piles_sync(state, c, card_idx, old, dest, pos, t);
                if old != dest && state.card(c).is_some() {
                    moved.push((c, old));
                }
                continue;
            }
        }
        state.remove_card_from_current_pile_with_tables(c, tables);
        let idx = get_insertion_index(state, dest, pos);
        state.add_card_to_pile_at_with_tables(c, dest, Some(idx), tables);
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
    let tables = state.hook_cache.cached_tables;
    let has_hook = tables.is_none_or(|t| state.card_has_any_hook(t, card));
    let old = state.remove_card_from_current_pile_with_tables(card, tables);
    if let Some(c) = state.card_mut(card) {
        c.removed_from_state = true;
    }
    if old == PileType::None && has_hook {
        state.invalidate_pile_hooks(crate::state::HOOK_DIRTY_PILES);
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
        canonical_base_energy_cost: meta
            .canonical_energy_cost
            .clamp(i16::MIN as i32, i16::MAX as i32) as i16,
        base_energy_cost: meta
            .canonical_energy_cost
            .clamp(i16::MIN as i32, i16::MAX as i32) as i16,
        captured_energy_x_value: 0,
        base_replay_count: 0,
        floor_added_to_deck: OptI16::NONE,
        deck_card_handle: OptCardHandle::NONE,
        clone_of_handle: OptCardHandle::NONE,
        temp_star_cost_override_this_turn: OptI16::NONE,
        temp_cost_override_this_turn: OptI16::NONE,
        temp_cost_override_this_turn_or_until_played: OptI16::NONE,
        temp_cost_override_this_combat: OptI16::NONE,
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
    let mut combined: SmallVec<[CardHandle; 64]> = state
        .player
        .discard
        .iter()
        .copied()
        .chain(state.player.draw.iter().copied())
        .collect();
    let mut shuffle_rng = *state.run_rngs.get(sts2_types::RunRngType::Shuffle);
    ordering::stable_shuffle_cards(state, content, &mut combined, &mut shuffle_rng)?;
    *state.run_rngs.get_mut(sts2_types::RunRngType::Shuffle) = shuffle_rng;

    if !state.card_extensions.is_empty() {
        let enchanted_cards: SmallVec<[CardHandle; 8]> = combined
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
    }

    let had_hooked_cards = (state.hook_cache.dirty_mask
        & (crate::state::HOOK_DIRTY_DRAW | crate::state::HOOK_DIRTY_DISCARD))
        != 0
        || !state.hook_cache.listener_discard.is_empty()
        || !state.hook_cache.listener_draw.is_empty();
    state.player.discard.clear();
    state.player.draw.clear();
    for &c in &combined {
        state.player.draw.push(c);
        if let Some(card) = state.card_mut(c) {
            card.pile = PileType::Draw;
        }
    }
    if had_hooked_cards {
        state.invalidate_pile_hooks(
            crate::state::HOOK_DIRTY_DRAW | crate::state::HOOK_DIRTY_DISCARD,
        );
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
    let tables = state
        .hook_cache
        .cached_tables
        .or_else(|| content.hook_mask_tables());
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
        let card_idx_opt = state.card_index(card);
        state.player.draw.remove(0);
        state.player.hand.push(card);
        if let Some(ci_idx) = card_idx_opt {
            let ci = &mut state.cards[ci_idx];
            if ci_idx < state.static_ctx.root_deck_prefix_len || ci.pile == PileType::Deck {
                state.deck_dirty.0 = true;
            }
            ci.pile = PileType::Hand;
        } else if let Some(ci) = state.card_mut(card) {
            ci.pile = PileType::Hand;
        }
        state.update_drawn_card_hook_cache(card, card_idx_opt, tables);
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
    #[cfg(not(debug_assertions))]
    if state.hook_cache.masks_are_valid()
        && !state
            .hook_cache
            .masks
            .combat_combined
            .contains(sts2_types::HookSlot::TryModifyKeywordsInCombat)
    {
        return Ok(false);
    }
    let modified = hooks::modify_keywords_in_combat(state, content, card, base)?;
    Ok(modified.contains(kw))
}
