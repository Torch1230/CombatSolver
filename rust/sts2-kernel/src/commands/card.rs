use crate::commands::{card_factory, card_pile};
use crate::content_dispatch::ContentDispatcher;
use crate::error::KernelError;
use crate::hooks;
use crate::state::{AfflictionInst, BattleState, CardHandle, CardTempFlags};
use smallvec::SmallVec;
use sts2_types::{AfflictionId, CardId, CardKeywords, CardPilePosition, PileType, RunRngType};

pub fn afflict(
    state: &mut BattleState,
    card: CardHandle,
    affliction_id: AfflictionId,
    amount: i32,
) -> bool {
    let Some((pile, is_same)) = state.card(card).map(|c| {
        (
            c.pile,
            c.affliction
                .as_ref()
                .is_some_and(|a| a.id == affliction_id),
        )
    }) else {
        return false;
    };
    if !matches!(pile, PileType::None | PileType::Deck) && hooks::is_over_or_ending(state) {
        return false;
    }
    if let Some(c) = state.card_mut(card) {
        if is_same {
            if let Some(existing) = &mut c.affliction {
                existing.amount += amount;
            }
        } else {
            c.affliction = Some(AfflictionInst {
                id: affliction_id,
                amount,
            });
        }
    }
    if !is_same
        && !state
            .hook_cache
            .cached_tables
            .is_some_and(|t| t.afflictions_self_card_entered_only)
    {
        state.invalidate_pile_hooks(crate::state::pile_dirty_bit(pile));
    }
    if affliction_id == AfflictionId::Bound && state.is_live_combat() {
        state.record_actor_event("BoundAfflicted");
    }
    true
}

pub fn clear_affliction(state: &mut BattleState, card: CardHandle) {
    let dirty = if let Some(c) = state.card_mut(card) {
        let had = c.affliction.take().is_some();
        if had {
            crate::state::pile_dirty_bit(c.pile)
        } else {
            0
        }
    } else {
        0
    };
    if dirty != 0
        && !state
            .hook_cache
            .cached_tables
            .is_some_and(|t| t.afflictions_self_card_entered_only)
    {
        state.invalidate_pile_hooks(dirty);
    }
}

pub fn downgrade(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
) -> Result<(), KernelError> {
    if hooks::is_ending_checked(state, content)? {
        return Ok(());
    }
    let Some(ci) = state.card(card) else {
        return Ok(());
    };
    if ci.upgrade_level <= 0 {
        return Err(KernelError::InvalidState(
            "Card is not upgraded.".to_owned(),
        ));
    }
    let meta = content.card_meta(ci.id)?;
    content.card_on_downgrade(state, card)?;
    if let Some(c) = state.card_mut(card) {
        c.upgrade_level = 0;
        c.base_energy_cost = c.canonical_base_energy_cost;
        c.local_keywords = meta.base_keywords;
    }
    Ok(())
}

pub fn transform(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    original: CardHandle,
    replacement_id: CardId,
) -> Result<Option<CardHandle>, KernelError> {
    transform_with_upgrade(state, content, original, replacement_id, 0)
}

pub fn transform_with_upgrade(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    original: CardHandle,
    replacement_id: CardId,
    upgrade_levels: i32,
) -> Result<Option<CardHandle>, KernelError> {
    if state.is_live_combat() && hooks::is_over_or_ending_checked(state, content)? {
        return Ok(None);
    }
    let Some(ci) = state.card(original) else {
        return Ok(None);
    };
    let pile = ci.pile;
    if pile == PileType::None {
        return Ok(None);
    }
    if ci.has_local_keyword(CardKeywords::ETERNAL) {
        return Err(KernelError::InvalidAction(
            "Eternal cards cannot be transformed.".to_owned(),
        ));
    }

    let original_index = match pile {
        PileType::Hand => state.player.hand.iter().position(|&h| h == original),
        PileType::Draw => state.player.draw.iter().position(|&h| h == original),
        PileType::Discard => state.player.discard.iter().position(|&h| h == original),
        PileType::Exhaust => state.player.exhaust.iter().position(|&h| h == original),
        PileType::Play => state.player.play.iter().position(|&h| h == original),
        PileType::Deck => state.player.deck.iter().position(|&h| h == original),
        PileType::None => None,
    }
    .ok_or_else(|| {
        KernelError::InvalidState("Original card not found in its assigned pile.".to_owned())
    })?;

    let tables = content.hook_mask_tables();
    let old_pile = state.remove_card_from_current_pile_with_tables(original, tables);
    if let Some(c) = state.card_mut(original) {
        c.removed_from_state = true;
    }
    let _ = old_pile;

    let rep = card_pile::create_card_in_combat(state, content, replacement_id, upgrade_levels)?;
    if pile != PileType::Deck && state.is_live_combat() {
        card_pile::enter_combat_at_index(state, content, rep, pile, original_index, true)?;
    } else {
        if pile == PileType::Deck {
            let floor_idx = state.static_ctx.act_index;
            if let Some(c) = state.card_mut(rep) {
                c.floor_added_to_deck = crate::state::OptI16::some_clamped(floor_idx);
            }
        }
        state.add_card_to_pile_at(rep, pile, Some(original_index));
        if pile == PileType::Deck {
            hooks::after_card_changed_piles(state, content, rep, PileType::None)?;
        }
    }

    let rep_pile = state.card(rep).map_or(PileType::None, |c| c.pile);
    if matches!(
        rep_pile,
        PileType::Draw | PileType::Hand | PileType::Discard | PileType::Exhaust | PileType::Play
    ) {
        hooks::after_card_generated_for_combat(
            state,
            content,
            rep,
            Some(state.player.creature_handle),
        )?;
    }
    Ok(Some(rep))
}

pub fn transform_to_random(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    original: CardHandle,
    rng_type: RunRngType,
) -> Result<Option<CardHandle>, KernelError> {
    if state.is_live_combat() && hooks::is_over_or_ending_checked(state, content)? {
        return Ok(None);
    }
    let chosen = card_factory::pick_random_transformation_target(
        state,
        original,
        state.is_live_combat(),
        rng_type,
    )?;
    transform(state, content, original, chosen)
}

pub fn upgrade(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
) -> Result<(), KernelError> {
    if hooks::is_ending_checked(state, content)? {
        return Ok(());
    }
    let Some(ci) = state.card(card) else {
        return Ok(());
    };
    let max_upgrade = content.card_meta(ci.id)?.max_upgrade_level;
    if ci.upgrade_level < max_upgrade {
        if let Some(c) = state.card_mut(card) {
            c.upgrade_level += 1;
        }
        content.card_on_upgrade(state, card)?;
    }
    Ok(())
}

pub fn apply_single_turn_retain(state: &mut BattleState, card: CardHandle) {
    if let Some(c) = state.card_mut(card) {
        c.temp_flags.insert(CardTempFlags::TEMPORARY_RETAIN_TURN);
    }
}

pub fn apply_single_turn_sly(state: &mut BattleState, card: CardHandle) {
    if let Some(c) = state.card_mut(card) {
        c.temp_flags.insert(CardTempFlags::TEMPORARY_SLY_TURN);
    }
}

pub fn discard_single(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
) -> Result<(), KernelError> {
    discard_batch(state, content, &[card])
}

pub fn discard_batch(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    cards: &[CardHandle],
) -> Result<(), KernelError> {
    if cards.is_empty() {
        return Ok(());
    }
    let mut departed_hand = false;
    let mut sly_cards: SmallVec<[CardHandle; 8]> = SmallVec::new();
    for &h in cards {
        if state.card(h).is_some_and(|c| c.pile == PileType::Hand) {
            departed_hand = true;
            if card_pile::card_has_keyword(state, content, h, CardKeywords::SLY)? {
                sly_cards.push(h);
            }
        }
    }
    for &h in cards {
        card_pile::add_sync(state, h, PileType::Discard, CardPilePosition::Bottom);
        state.player.counters.cards_discarded_this_turn += 1;
        hooks::after_card_discarded(state, content, h)?;
    }
    card_pile::notify_hand_departure(state, content, departed_hand)?;
    if !sly_cards.is_empty() {
        crate::commands::autoplay::from_cards(state, content, &sly_cards, None, false)?;
    }
    Ok(())
}
