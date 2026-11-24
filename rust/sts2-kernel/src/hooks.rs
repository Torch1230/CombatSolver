use crate::content_dispatch::{ContentDispatcher, HookArgs, HookListener};
use crate::error::KernelError;
use crate::state::{BattleState, CardHandle, CreatureHandle, DamageResult, PowerInst};
use bitflags::bitflags;
use smallvec::SmallVec;
use sts2_types::{
    CardKeywords, CardPilePosition, CombatSide, Dec, HookMask, HookSlot, OrbId, PileType, PowerId,
    PowerStaticFlags, ValueProp,
};

bitflags! {
    #[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Default)]
    pub struct ModifyDamageHookType: u8 {
        const NONE = 0;
        const ADDITIVE = 2;
        const MULTIPLICATIVE = 4;
        const CAP = 8;
        const ALL = 0xE;
    }
}

fn collect_creature_listeners_for_mask(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    creature_handle: CreatureHandle,
    include_player_inventory: bool,
    mask: HookMask,
    out: &mut SmallVec<[HookListener; 16]>,
) -> Result<(), KernelError> {
    let Some(creature) = state.creature(creature_handle) else {
        return Ok(());
    };
    if creature.is_detached {
        return Ok(());
    }
    if let Some(tables) = content.hook_mask_tables() {
        let cats = tables.categories;
        if cats.all_powers.intersects(mask) {
            for p in &creature.powers {
                if tables
                    .powers
                    .get(p.id as usize)
                    .is_some_and(|m| m.intersects(mask))
                {
                    out.push(HookListener::Power {
                        creature: creature_handle,
                        power_handle: p.handle,
                    });
                }
            }
        }
        if creature.is_monster() {
            if cats.all_monsters.intersects(mask) {
                if let Some(mid) = creature.monster_id {
                    if tables
                        .monsters
                        .get(mid as usize)
                        .is_some_and(|m| m.intersects(mask))
                    {
                        out.push(HookListener::Monster(creature_handle));
                    }
                }
            }
            return Ok(());
        }
        if !creature.is_player || !state.player.is_active_for_hooks {
            return Ok(());
        }
        if include_player_inventory {
            if cats.all_relics.intersects(mask) {
                for (i, relic) in state.player.relics.iter().enumerate() {
                    if !relic.is_melted
                        && tables
                            .relics
                            .get(relic.id as usize)
                            .is_some_and(|m| m.intersects(mask))
                    {
                        out.push(HookListener::Relic(i));
                    }
                }
            }
            if cats.all_potions.intersects(mask) {
                for (i, slot) in state.player.potion_slots.iter().enumerate() {
                    if let Some(pid) = *slot {
                        if tables
                            .potions
                            .get(pid as usize)
                            .is_some_and(|m| m.intersects(mask))
                        {
                            out.push(HookListener::Potion(i));
                        }
                    }
                }
            }
        }
        if cats.all_orbs.intersects(mask) {
            for (i, orb) in state.player.orbs.iter().enumerate() {
                if tables
                    .orbs
                    .get(orb.id as usize)
                    .is_some_and(|m| m.intersects(mask))
                {
                    out.push(HookListener::Orb(i));
                }
            }
        }
        let check_cards = cats.all_cards.intersects(mask);
        let check_ench =
            !state.card_extensions.is_empty() && cats.all_enchantments.intersects(mask);
        let check_aff = cats.all_afflictions.intersects(mask);
        if check_cards && !check_ench && !check_aff {
            for pile in [
                &state.player.hand[..],
                &state.player.draw[..],
                &state.player.discard[..],
                &state.player.exhaust[..],
                &state.player.play[..],
            ] {
                for &card_handle in pile {
                    let Some(card) = state.card(card_handle) else {
                        continue;
                    };
                    if card.removed_from_state {
                        continue;
                    }
                    if tables
                        .cards
                        .get(card.id as usize)
                        .is_some_and(|m| m.intersects(mask))
                    {
                        out.push(HookListener::Card(card_handle));
                    }
                }
            }
        } else if check_cards || check_ench || check_aff {
            for pile in [
                &state.player.hand[..],
                &state.player.draw[..],
                &state.player.discard[..],
                &state.player.exhaust[..],
                &state.player.play[..],
            ] {
                for &card_handle in pile {
                    let Some(card) = state.card(card_handle) else {
                        continue;
                    };
                    if card.removed_from_state {
                        continue;
                    }
                    if check_cards
                        && tables
                            .cards
                            .get(card.id as usize)
                            .is_some_and(|m| m.intersects(mask))
                    {
                        out.push(HookListener::Card(card_handle));
                    }
                    if check_ench {
                        if let Some(ext) = state.card_ext(card.ext_index) {
                            if let Some(ench) = &ext.enchantment {
                                if tables
                                    .enchantments
                                    .get(ench.id as usize)
                                    .is_some_and(|m| m.intersects(mask))
                                {
                                    out.push(HookListener::Enchantment(card_handle));
                                }
                            }
                        }
                    }
                    if check_aff {
                        if let Some(aff) = card.affliction {
                            if tables
                                .afflictions
                                .get(aff.id as usize)
                                .is_some_and(|m| m.intersects(mask))
                            {
                                out.push(HookListener::Affliction(card_handle));
                            }
                        }
                    }
                }
            }
        }
        return Ok(());
    }
    if let Some(cats) = content.category_hook_masks() {
        if cats.all_powers.intersects(mask) {
            for p in &creature.powers {
                if content.power_hook_mask(p.id)?.intersects(mask) {
                    out.push(HookListener::Power {
                        creature: creature_handle,
                        power_handle: p.handle,
                    });
                }
            }
        }
        if creature.is_monster() {
            if cats.all_monsters.intersects(mask) {
                if let Some(mid) = creature.monster_id {
                    if content.monster_hook_mask(mid)?.intersects(mask) {
                        out.push(HookListener::Monster(creature_handle));
                    }
                }
            }
            return Ok(());
        }
        if !creature.is_player || !state.player.is_active_for_hooks {
            return Ok(());
        }
        if include_player_inventory {
            if cats.all_relics.intersects(mask) {
                for (i, relic) in state.player.relics.iter().enumerate() {
                    if !relic.is_melted && content.relic_hook_mask(relic.id)?.intersects(mask) {
                        out.push(HookListener::Relic(i));
                    }
                }
            }
            if cats.all_potions.intersects(mask) {
                for (i, slot) in state.player.potion_slots.iter().enumerate() {
                    if let Some(pid) = *slot {
                        if content.potion_hook_mask(pid)?.intersects(mask) {
                            out.push(HookListener::Potion(i));
                        }
                    }
                }
            }
        }
        if cats.all_orbs.intersects(mask) {
            for (i, orb) in state.player.orbs.iter().enumerate() {
                if content.orb_hook_mask(orb.id)?.intersects(mask) {
                    out.push(HookListener::Orb(i));
                }
            }
        }
        let check_cards = cats.all_cards.intersects(mask);
        let check_ench =
            !state.card_extensions.is_empty() && cats.all_enchantments.intersects(mask);
        let check_aff = cats.all_afflictions.intersects(mask);
        if check_cards || check_ench || check_aff {
            for pile in [
                &state.player.hand[..],
                &state.player.draw[..],
                &state.player.discard[..],
                &state.player.exhaust[..],
                &state.player.play[..],
            ] {
                for &card_handle in pile {
                    let Some(card) = state.card(card_handle) else {
                        continue;
                    };
                    if card.removed_from_state {
                        continue;
                    }
                    if check_cards && content.card_hook_mask(card.id)?.intersects(mask) {
                        out.push(HookListener::Card(card_handle));
                    }
                    if check_ench {
                        if let Some(ext) = state.card_ext(card.ext_index) {
                            if let Some(ench) = &ext.enchantment {
                                if content
                                    .enchantment_hook_mask_for_card(state, card_handle, ench.id)?
                                    .intersects(mask)
                                {
                                    out.push(HookListener::Enchantment(card_handle));
                                }
                            }
                        }
                    }
                    if check_aff {
                        if let Some(aff) = card.affliction {
                            if content
                                .affliction_hook_mask_for_card(state, card_handle, aff.id)?
                                .intersects(mask)
                            {
                                out.push(HookListener::Affliction(card_handle));
                            }
                        }
                    }
                }
            }
        }
        return Ok(());
    }

    for p in &creature.powers {
        let l = HookListener::Power {
            creature: creature_handle,
            power_handle: p.handle,
        };
        if content.listener_hook_mask(state, l)?.intersects(mask) {
            out.push(l);
        }
    }
    if creature.is_monster() {
        let l = HookListener::Monster(creature_handle);
        if content.listener_hook_mask(state, l)?.intersects(mask) {
            out.push(l);
        }
        return Ok(());
    }
    if !creature.is_player || !state.player.is_active_for_hooks {
        return Ok(());
    }
    if include_player_inventory {
        for (i, relic) in state.player.relics.iter().enumerate() {
            if !relic.is_melted {
                let l = HookListener::Relic(i);
                if content.listener_hook_mask(state, l)?.intersects(mask) {
                    out.push(l);
                }
            }
        }
        for (i, slot) in state.player.potion_slots.iter().enumerate() {
            if slot.is_some() {
                let l = HookListener::Potion(i);
                if content.listener_hook_mask(state, l)?.intersects(mask) {
                    out.push(l);
                }
            }
        }
    }
    for i in 0..state.player.orbs.len() {
        let l = HookListener::Orb(i);
        if content.listener_hook_mask(state, l)?.intersects(mask) {
            out.push(l);
        }
    }
    for pile in [
        &state.player.hand[..],
        &state.player.draw[..],
        &state.player.discard[..],
        &state.player.exhaust[..],
        &state.player.play[..],
    ] {
        for &card_handle in pile {
            let Some(card) = state.card(card_handle) else {
                continue;
            };
            if card.removed_from_state {
                continue;
            }
            let lc = HookListener::Card(card_handle);
            if content.listener_hook_mask(state, lc)?.intersects(mask) {
                out.push(lc);
            }
            if let Some(ext) = state.card_ext(card.ext_index) {
                if ext.enchantment.is_some() {
                    let le = HookListener::Enchantment(card_handle);
                    if content.listener_hook_mask(state, le)?.intersects(mask) {
                        out.push(le);
                    }
                }
            }
            if card.affliction.is_some() {
                let la = HookListener::Affliction(card_handle);
                if content.listener_hook_mask(state, la)?.intersects(mask) {
                    out.push(la);
                }
            }
        }
    }
    Ok(())
}

fn push_cached_piles(
    entries: &[(HookListener, HookMask)],
    mask: HookMask,
    out: &mut SmallVec<[HookListener; 16]>,
) {
    for &(listener, lm) in entries {
        if lm.intersects(mask) {
            out.push(listener);
        }
    }
}

fn collect_combat_only_listeners_from_cache(
    state: &BattleState,
    mask: HookMask,
    out: &mut SmallVec<[HookListener; 16]>,
) {
    let cache = &state.hook_cache;
    if !cache.masks.combat_combined.intersects(mask) {
        return;
    }
    let [m0, m1, m2] = mask.0;
    let len = cache.flat_listeners.len();
    for i in 0..len {
        let hit = (cache.flat_masks_w0[i] & m0)
            | (cache.flat_masks_w1[i] & m1)
            | (cache.flat_masks_w2[i] & m2);
        if hit != 0 {
            out.push(cache.flat_listeners[i]);
        }
    }
}

#[inline(always)]
fn skip_combat_hook_mask(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    mask: HookMask,
) -> Result<bool, KernelError> {
    #[cfg(not(debug_assertions))]
    if state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some() {
        return Ok(!state.hook_cache.masks.combat_combined.intersects(mask));
    }
    if let Some(tables) = state
        .hook_cache
        .cached_tables
        .or_else(|| content.hook_mask_tables())
    {
        state.ensure_hook_cache_with_tables(content, tables)?;
        return Ok(!state.hook_cache.masks.combat_combined.intersects(mask));
    }
    Ok(false)
}

#[inline(always)]
fn skip_combat_hook_mask_checked(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    mask: HookMask,
) -> Result<bool, KernelError> {
    if skip_combat_hook_mask(state, content, mask)? {
        return Ok(true);
    }
    Ok(!state.is_starting && is_over_or_ending_checked(state, content)?)
}

#[inline(always)]
fn skip_run_combat_hook_mask(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    mask: HookMask,
) -> Result<bool, KernelError> {
    #[cfg(not(debug_assertions))]
    if state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some() {
        return Ok(!state.hook_cache.masks.run_combat_combined.intersects(mask));
    }
    if let Some(tables) = state
        .hook_cache
        .cached_tables
        .or_else(|| content.hook_mask_tables())
    {
        state.ensure_hook_cache_with_tables(content, tables)?;
        return Ok(!state.hook_cache.masks.run_combat_combined.intersects(mask));
    }
    Ok(false)
}

fn collect_from_cache_slot(
    state: &BattleState,
    word: usize,
    bit: u64,
    include_deck: bool,
    stack_buf: &mut [std::mem::MaybeUninit<HookListener>; 16],
    stack_len: &mut usize,
    spill: &mut Option<Vec<HookListener>>,
) {
    macro_rules! push_l {
        ($l:expr) => {{
            let l = $l;
            if let Some(v) = spill.as_mut() {
                v.push(l);
            } else if *stack_len < 16 {
                stack_buf[*stack_len].write(l);
                *stack_len += 1;
            } else {
                let mut v = Vec::with_capacity(32);
                let init_slice = unsafe {
                    std::slice::from_raw_parts(stack_buf.as_ptr().cast::<HookListener>(), 16)
                };
                v.extend_from_slice(init_slice);
                v.push(l);
                *spill = Some(v);
            }
        }};
    }
    let cache = &state.hook_cache;
    let m = &cache.masks;
    if include_deck && state.player.is_active_for_hooks && m.cards_deck.contains_slot_parts(word, bit) {
        for &(listener, lm) in &cache.listener_deck {
            if lm.contains_slot_parts(word, bit) {
                push_l!(listener);
            }
        }
    }
    if !m.combat_combined.contains_slot_parts(word, bit) {
        return;
    }
    let mask_words = cache.flat_mask_word(word);
    let listeners = &cache.flat_listeners;
    for (i, &w) in mask_words.iter().enumerate() {
        if (w & bit) != 0 {
            push_l!(listeners[i]);
        }
    }
}

struct SlotListenerBuf {
    stack: [std::mem::MaybeUninit<HookListener>; 16],
    len: usize,
    spill: Option<Vec<HookListener>>,
}

impl SlotListenerBuf {
    #[inline]
    fn collect(state: &BattleState, slot: HookSlot, include_deck: bool) -> Self {
        let (word, bit) = HookMask::slot_parts(slot);
        let mut stack = [const { std::mem::MaybeUninit::<HookListener>::uninit() }; 16];
        let mut len = 0usize;
        let mut spill = None;
        collect_from_cache_slot(state, word, bit, include_deck, &mut stack, &mut len, &mut spill);
        Self { stack, len, spill }
    }

    #[inline]
    fn as_slice(&self) -> &[HookListener] {
        match &self.spill {
            Some(v) => v.as_slice(),
            None => unsafe {
                std::slice::from_raw_parts(self.stack.as_ptr().cast::<HookListener>(), self.len)
            },
        }
    }
}

#[inline]
fn dispatch_single_void_known_slot(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    listener: HookListener,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<(), KernelError> {
    if let HookListener::Power {
        creature,
        power_handle,
    } = listener
    {
        let resolved = state.creature(creature).and_then(|c| {
            if c.is_detached
                || !state.contains_creature(creature)
                || (c.is_player && !state.player.is_active_for_hooks)
            {
                return None;
            }
            for (idx, &p) in c.powers.iter().enumerate() {
                if p.handle == power_handle {
                    return Some((idx, p, c.side));
                }
            }
            None
        });
        #[cfg(debug_assertions)]
        debug_assert!(
            resolved.is_some(),
            "first listener {:?} ineligible before void dispatch on {:?}",
            listener,
            slot
        );
        if let Some((power_idx, power, owner_side)) = resolved {
            let _ = content.dispatch_power_hook_resolved(
                state, creature, power_idx, power, owner_side, slot, args,
            )?;
        }
    } else {
        #[cfg(debug_assertions)]
        debug_assert!(
            is_eligible_hook_listener(state, listener),
            "first listener {:?} ineligible before void dispatch on {:?}",
            listener,
            slot
        );
        let _ = content.dispatch_hook(state, listener, slot, args)?;
    }
    Ok(())
}

#[inline]
fn dispatch_void_combat_slot(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    slot: HookSlot,
    args: &HookArgs<'_>,
    check_ending: bool,
) -> Result<(), KernelError> {
    if check_ending && !state.is_in_progress && !state.is_starting {
        return Ok(());
    }
    let (word, bit) = HookMask::slot_parts(slot);
    #[cfg(not(debug_assertions))]
    let cached_hit = state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some();
    #[cfg(debug_assertions)]
    let cached_hit = {
        state.ensure_hook_cache(content)?;
        state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some()
    };
    if !cached_hit {
        if let Some(tables) = state
            .hook_cache
            .cached_tables
            .or_else(|| content.hook_mask_tables())
        {
            state.ensure_hook_cache_with_tables(content, tables)?;
        } else {
            if check_ending && is_over_or_ending_checked(state, content)? && !state.is_starting {
                return Ok(());
            }
            let listeners =
                collect_combat_only_listeners_for_mask(state, content, HookMask::EMPTY.with(slot))?;
            return dispatch_void_list_known_slot(state, content, &listeners, slot, args);
        }
    }
    if !state
        .hook_cache
        .masks
        .combat_combined
        .contains_slot_parts(word, bit)
    {
        return Ok(());
    }
    if check_ending && !state.is_starting && is_over_or_ending_checked(state, content)? {
        return Ok(());
    }
    let mut first_idx = usize::MAX;
    let mut multi = false;
    for (i, &w) in state.hook_cache.flat_mask_word(word).iter().enumerate() {
        if (w & bit) != 0 {
            if first_idx == usize::MAX {
                first_idx = i;
            } else {
                multi = true;
                break;
            }
        }
    }
    if first_idx == usize::MAX {
        return Ok(());
    }
    if !multi {
        let listener = state.hook_cache.flat_listeners[first_idx];
        return dispatch_single_void_known_slot(state, content, listener, slot, args);
    }
    let buf = SlotListenerBuf::collect(state, slot, false);
    dispatch_void_list_known_slot(state, content, buf.as_slice(), slot, args)
}

#[inline]
fn dispatch_void_run_combat_slot(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<(), KernelError> {
    let (word, bit) = HookMask::slot_parts(slot);
    #[cfg(not(debug_assertions))]
    let cached_hit = state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some();
    #[cfg(debug_assertions)]
    let cached_hit = {
        state.ensure_hook_cache(content)?;
        state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some()
    };
    if !cached_hit {
        if let Some(tables) = state
            .hook_cache
            .cached_tables
            .or_else(|| content.hook_mask_tables())
        {
            state.ensure_hook_cache_with_tables(content, tables)?;
        } else {
            let listeners =
                collect_run_combat_listeners_for_mask(state, content, HookMask::EMPTY.with(slot))?;
            return dispatch_void_list_known_slot(state, content, &listeners, slot, args);
        }
    }
    if !state
        .hook_cache
        .masks
        .run_combat_combined
        .contains_slot_parts(word, bit)
    {
        return Ok(());
    }
    let mut first: Option<HookListener> = None;
    let mut multi = false;
    {
        let cache = &state.hook_cache;
        if state.player.is_active_for_hooks && cache.masks.cards_deck.contains_slot_parts(word, bit)
        {
            for &(l, lm) in &cache.listener_deck {
                if lm.contains_slot_parts(word, bit) {
                    if first.is_none() {
                        first = Some(l);
                    } else {
                        multi = true;
                        break;
                    }
                }
            }
        }
        if !multi && cache.masks.combat_combined.contains_slot_parts(word, bit) {
            for (i, &w) in cache.flat_mask_word(word).iter().enumerate() {
                if (w & bit) != 0 {
                    if first.is_none() {
                        first = Some(cache.flat_listeners[i]);
                    } else {
                        multi = true;
                        break;
                    }
                }
            }
        }
    }
    let Some(first_listener) = first else {
        return Ok(());
    };
    if !multi {
        return dispatch_single_void_known_slot(state, content, first_listener, slot, args);
    }
    let buf = SlotListenerBuf::collect(state, slot, true);
    dispatch_void_list_known_slot(state, content, buf.as_slice(), slot, args)
}

fn collect_combat_only_listeners_for_mask(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    mask: HookMask,
) -> Result<SmallVec<[HookListener; 16]>, KernelError> {
    #[cfg(not(debug_assertions))]
    if state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some() {
        if !state.hook_cache.masks.combat_combined.intersects(mask) {
            return Ok(SmallVec::new());
        }
        let mut out = SmallVec::new();
        collect_combat_only_listeners_from_cache(state, mask, &mut out);
        return Ok(out);
    }
    if let Some(tables) = state
        .hook_cache
        .cached_tables
        .or_else(|| content.hook_mask_tables())
    {
        state.ensure_hook_cache_with_tables(content, tables)?;
        if !state.hook_cache.masks.combat_combined.intersects(mask) {
            return Ok(SmallVec::new());
        }
        let mut out = SmallVec::new();
        collect_combat_only_listeners_from_cache(state, mask, &mut out);
        return Ok(out);
    }
    let mut out = SmallVec::new();
    for &ally in &state.ally_handles {
        collect_creature_listeners_for_mask(state, content, ally, true, mask, &mut out)?;
    }
    for &enemy in &state.enemy_handles {
        collect_creature_listeners_for_mask(state, content, enemy, true, mask, &mut out)?;
    }
    for &rm in &state.static_ctx.run_model_ids {
        let l = HookListener::RunModel(rm);
        if content.listener_hook_mask(state, l)?.intersects(mask) {
            out.push(l);
        }
    }
    if let Some(dom) = state.static_ctx.dominion_id {
        let l = HookListener::Dominion(dom);
        if content.listener_hook_mask(state, l)?.intersects(mask) {
            out.push(l);
        }
    }
    Ok(out)
}

fn collect_run_combat_listeners_for_mask(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    mask: HookMask,
) -> Result<SmallVec<[HookListener; 16]>, KernelError> {
    #[cfg(not(debug_assertions))]
    if state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some() {
        if !state.hook_cache.masks.run_combat_combined.intersects(mask) {
            return Ok(SmallVec::new());
        }
        let mut out = SmallVec::new();
        if state.player.is_active_for_hooks && state.hook_cache.masks.cards_deck.intersects(mask) {
            push_cached_piles(&state.hook_cache.listener_deck, mask, &mut out);
        }
        collect_combat_only_listeners_from_cache(state, mask, &mut out);
        return Ok(out);
    }
    if let Some(tables) = state
        .hook_cache
        .cached_tables
        .or_else(|| content.hook_mask_tables())
    {
        state.ensure_hook_cache_with_tables(content, tables)?;
        if !state.hook_cache.masks.run_combat_combined.intersects(mask) {
            return Ok(SmallVec::new());
        }
        let mut out = SmallVec::new();
        if state.player.is_active_for_hooks && state.hook_cache.masks.cards_deck.intersects(mask) {
            push_cached_piles(&state.hook_cache.listener_deck, mask, &mut out);
        }
        collect_combat_only_listeners_from_cache(state, mask, &mut out);
        return Ok(out);
    }
    let mut out = SmallVec::new();
    if state.player.is_active_for_hooks {
        if let Some(tables) = content.hook_mask_tables() {
            let cats = tables.categories;
            let check_cards = cats.deck_cards.intersects(mask);
            let check_ench =
                !state.card_extensions.is_empty() && cats.all_enchantments.intersects(mask);
            if check_cards && !check_ench {
                for &card_handle in &state.player.deck {
                    let Some(card) = state.card(card_handle) else {
                        continue;
                    };
                    if card.removed_from_state {
                        continue;
                    }
                    if tables
                        .cards
                        .get(card.id as usize)
                        .is_some_and(|m| m.intersects(mask))
                    {
                        out.push(HookListener::Card(card_handle));
                    }
                }
            } else if check_cards || check_ench {
                for &card_handle in &state.player.deck {
                    let Some(card) = state.card(card_handle) else {
                        continue;
                    };
                    if card.removed_from_state {
                        continue;
                    }
                    if check_cards
                        && tables
                            .cards
                            .get(card.id as usize)
                            .is_some_and(|m| m.intersects(mask))
                    {
                        out.push(HookListener::Card(card_handle));
                    }
                    if check_ench {
                        if let Some(ext) = state.card_ext(card.ext_index) {
                            if let Some(ench) = &ext.enchantment {
                                if tables
                                    .enchantments
                                    .get(ench.id as usize)
                                    .is_some_and(|m| m.intersects(mask))
                                {
                                    out.push(HookListener::Enchantment(card_handle));
                                }
                            }
                        }
                    }
                }
            }
        } else if let Some(cats) = content.category_hook_masks() {
            let check_cards = cats.deck_cards.intersects(mask);
            let check_ench =
                !state.card_extensions.is_empty() && cats.all_enchantments.intersects(mask);
            if check_cards || check_ench {
                for &card_handle in &state.player.deck {
                    let Some(card) = state.card(card_handle) else {
                        continue;
                    };
                    if card.removed_from_state {
                        continue;
                    }
                    if check_cards && content.card_hook_mask(card.id)?.intersects(mask) {
                        out.push(HookListener::Card(card_handle));
                    }
                    if check_ench {
                        if let Some(ext) = state.card_ext(card.ext_index) {
                            if let Some(ench) = &ext.enchantment {
                                if content
                                    .enchantment_hook_mask_for_card(state, card_handle, ench.id)?
                                    .intersects(mask)
                                {
                                    out.push(HookListener::Enchantment(card_handle));
                                }
                            }
                        }
                    }
                }
            }
        } else {
            for &card_handle in &state.player.deck {
                let Some(card) = state.card(card_handle) else {
                    continue;
                };
                if card.removed_from_state {
                    continue;
                }
                let lc = HookListener::Card(card_handle);
                if content.listener_hook_mask(state, lc)?.intersects(mask) {
                    out.push(lc);
                }
                if let Some(ext) = state.card_ext(card.ext_index) {
                    if ext.enchantment.is_some() {
                        let le = HookListener::Enchantment(card_handle);
                        if content.listener_hook_mask(state, le)?.intersects(mask) {
                            out.push(le);
                        }
                    }
                }
            }
        }
    }
    for &ally in &state.ally_handles {
        collect_creature_listeners_for_mask(state, content, ally, true, mask, &mut out)?;
    }
    for &enemy in &state.enemy_handles {
        collect_creature_listeners_for_mask(state, content, enemy, true, mask, &mut out)?;
    }
    for &rm in &state.static_ctx.run_model_ids {
        let l = HookListener::RunModel(rm);
        if content.listener_hook_mask(state, l)?.intersects(mask) {
            out.push(l);
        }
    }
    if let Some(dom) = state.static_ctx.dominion_id {
        let l = HookListener::Dominion(dom);
        if content.listener_hook_mask(state, l)?.intersects(mask) {
            out.push(l);
        }
    }
    Ok(out)
}

#[inline]
fn collect_combat_hook_listeners_checked_for_mask(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    mask: HookMask,
) -> Result<SmallVec<[HookListener; 16]>, KernelError> {
    if skip_combat_hook_mask_checked(state, content, mask)? {
        return Ok(SmallVec::new());
    }
    collect_combat_only_listeners_for_mask(state, content, mask)
}

fn collect_creature_listeners(
    state: &BattleState,
    creature_handle: CreatureHandle,
    include_player_inventory: bool,
    out: &mut Vec<HookListener>,
) {
    let Some(creature) = state.creature(creature_handle) else {
        return;
    };
    for p in &creature.powers {
        out.push(HookListener::Power {
            creature: creature_handle,
            power_handle: p.handle,
        });
    }
    if creature.is_monster() {
        out.push(HookListener::Monster(creature_handle));
        return;
    }
    if !creature.is_player || !state.player.is_active_for_hooks {
        return;
    }
    if include_player_inventory {
        for (i, relic) in state.player.relics.iter().enumerate() {
            if !relic.is_melted {
                out.push(HookListener::Relic(i));
            }
        }
        for (i, slot) in state.player.potion_slots.iter().enumerate() {
            if slot.is_some() {
                out.push(HookListener::Potion(i));
            }
        }
    }
    for i in 0..state.player.orbs.len() {
        out.push(HookListener::Orb(i));
    }
    for pile in [
        &state.player.hand[..],
        &state.player.draw[..],
        &state.player.discard[..],
        &state.player.exhaust[..],
        &state.player.play[..],
    ] {
        for &card_handle in pile {
            out.push(HookListener::Card(card_handle));
            if let Some(card) = state.card(card_handle) {
                if let Some(ext) = state.card_ext(card.ext_index) {
                    if ext.enchantment.is_some() {
                        out.push(HookListener::Enchantment(card_handle));
                    }
                }
                if card.affliction.is_some() {
                    out.push(HookListener::Affliction(card_handle));
                }
            }
        }
    }
}

/// Exact `CombatState.IterateHookListeners()` (#1082):
/// 1. Allies (Powers -> Monster, or if active player: Relics -> Potions -> Orbs -> 5 piles)
/// 2. Enemies (Powers -> Monster)
/// 3. RunModels
/// 4. Dominion (if present)
pub fn collect_combat_only_listeners(state: &BattleState) -> Vec<HookListener> {
    let mut out = Vec::with_capacity(64);
    for &ally in &state.ally_handles {
        collect_creature_listeners(state, ally, true, &mut out);
    }
    for &enemy in &state.enemy_handles {
        collect_creature_listeners(state, enemy, true, &mut out);
    }
    for &rm in &state.static_ctx.run_model_ids {
        out.push(HookListener::RunModel(rm));
    }
    if let Some(dom) = state.static_ctx.dominion_id {
        out.push(HookListener::Dominion(dom));
    }
    out
}

/// Exact `CombatState.IterateHookListeners(includePlayerInventory: true)` (same #1082 sequence).
pub fn collect_direct_combat_hook_listeners(state: &BattleState) -> Vec<HookListener> {
    collect_combat_only_listeners(state)
}

/// Exact `RunState.IterateHookListeners(combatState)` (#1072/#1082):
/// active player's Deck (+ enchantments), followed by `CombatState.IterateHookListeners()`.
pub fn collect_run_combat_listeners(state: &BattleState) -> Vec<HookListener> {
    let mut out = Vec::with_capacity(96);
    if state.player.is_active_for_hooks {
        for &card_handle in &state.player.deck {
            out.push(HookListener::Card(card_handle));
            if let Some(card) = state.card(card_handle) {
                if let Some(ext) = state.card_ext(card.ext_index) {
                    if ext.enchantment.is_some() {
                        out.push(HookListener::Enchantment(card_handle));
                    }
                }
            }
        }
    }
    out.extend(collect_combat_only_listeners(state));
    out
}

/// Matches `Hook.IterateCombatHookListeners` (#1072): checks ending guard, then iterates `CombatState.IterateHookListeners()`.
pub fn collect_combat_hook_listeners(state: &BattleState) -> Vec<HookListener> {
    if is_over_or_ending(state) && !state.is_starting {
        return Vec::new();
    }
    collect_combat_only_listeners(state)
}

pub fn collect_combat_hook_listeners_checked(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<Vec<HookListener>, KernelError> {
    if is_over_or_ending_checked(state, content)? && !state.is_starting {
        return Ok(Vec::new());
    }
    Ok(collect_combat_only_listeners(state))
}

#[inline]
pub fn is_over_or_ending(state: &BattleState) -> bool {
    is_ending(state) || !state.is_in_progress
}

#[inline]
pub fn is_ending(state: &BattleState) -> bool {
    state.is_in_progress && (has_lost(state) || has_won_raw(state))
}

#[inline]
pub fn is_over_or_ending_checked(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<bool, KernelError> {
    if !state.is_in_progress {
        return Ok(true);
    }
    is_ending_checked(state, content)
}

#[inline]
pub fn is_ending_checked(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<bool, KernelError> {
    if !state.is_in_progress {
        return Ok(false);
    }
    if state.ally_handles.len() == 1 && state.player.current_hp <= 0 {
        return Ok(true);
    }
    if has_lost(state) {
        return Ok(true);
    }
    if !has_won_raw(state) {
        return Ok(false);
    }
    Ok(!should_stop_combat_from_ending(state, content)?)
}

#[inline(always)]
pub fn has_lost(state: &BattleState) -> bool {
    !state.has_living_player_creature()
}

#[inline(always)]
pub fn has_won_raw(state: &BattleState) -> bool {
    !state.enemy_handles.iter().any(|&h| {
        state
            .creature(h)
            .is_some_and(|c| c.is_alive() && !c.is_secondary_enemy())
    })
}

/// Exact `Hook.IsEligibleHookListener` check before each callback dispatch.
#[inline(always)]
pub fn is_eligible_hook_listener(state: &BattleState, listener: HookListener) -> bool {
    match listener {
        HookListener::Power {
            creature,
            power_handle,
        } => {
            let Some(c) = state.creature(creature) else {
                return false;
            };
            if c.is_detached || !state.contains_creature(creature) {
                return false;
            }
            if !c.powers.iter().any(|p| p.handle == power_handle) {
                return false;
            }
            if c.is_player && !state.player.is_active_for_hooks {
                return false;
            }
            true
        }
        HookListener::Monster(creature) => state
            .creature(creature)
            .is_some_and(|c| !c.is_detached && state.contains_creature(creature)),
        HookListener::Orb(orb_idx) => {
            orb_idx < state.player.orbs.len() && state.player.is_active_for_hooks
        }
        HookListener::Relic(idx) => {
            state.player.relics.get(idx).is_some_and(|r| !r.is_melted)
        }
        HookListener::Potion(idx) => state
            .player
            .potion_slots
            .get(idx)
            .is_some_and(|s| s.is_some()),
        HookListener::Card(h) => state.card(h).is_some_and(|c| !c.removed_from_state),
        HookListener::Enchantment(h) => state.card(h).is_some_and(|c| {
            !c.removed_from_state
                && state
                    .card_ext(c.ext_index)
                    .is_some_and(|e| e.enchantment.is_some())
        }),
        HookListener::Affliction(h) => state
            .card(h)
            .is_some_and(|c| !c.removed_from_state && c.affliction.is_some()),
        HookListener::DetachedPower { .. }
        | HookListener::RunModel(_)
        | HookListener::Dominion(_) => true,
    }
}

#[inline]
fn dispatch_if_active(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    listener: HookListener,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<Option<crate::content_dispatch::HookResult>, KernelError> {
    if !is_eligible_hook_listener(state, listener) {
        return Ok(None);
    }
    let mask = content.listener_hook_mask(state, listener)?;
    if !mask.contains(slot) {
        return Ok(None);
    }
    Ok(Some(content.dispatch_hook(state, listener, slot, args)?))
}

#[inline]
fn dispatch_if_eligible_known_slot(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    listener: HookListener,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<Option<crate::content_dispatch::HookResult>, KernelError> {
    if let HookListener::Power {
        creature,
        power_handle,
    } = listener
    {
        let resolved = state.creature(creature).and_then(|c| {
            if c.is_detached
                || !state.contains_creature(creature)
                || (c.is_player && !state.player.is_active_for_hooks)
            {
                return None;
            }
            for (idx, &p) in c.powers.iter().enumerate() {
                if p.handle == power_handle {
                    return Some((idx, p, c.side));
                }
            }
            None
        });
        if let Some((power_idx, power, owner_side)) = resolved {
            return Ok(Some(content.dispatch_power_hook_resolved(
                state, creature, power_idx, power, owner_side, slot, args,
            )?));
        }
        return Ok(None);
    }
    if !is_eligible_hook_listener(state, listener) {
        return Ok(None);
    }
    Ok(Some(content.dispatch_hook(state, listener, slot, args)?))
}

fn dispatch_void_list(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    listeners: &[HookListener],
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<(), KernelError> {
    for &listener in listeners {
        let _ = dispatch_if_active(state, content, listener, slot, args)?;
    }
    Ok(())
}

#[inline]
fn dispatch_void_list_known_slot(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    listeners: &[HookListener],
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<(), KernelError> {
    if let Some((&first, rest)) = listeners.split_first() {
        dispatch_single_void_known_slot(state, content, first, slot, args)?;
        for &listener in rest {
            let _ = dispatch_if_eligible_known_slot(state, content, listener, slot, args)?;
        }
    }
    Ok(())
}

#[inline(always)]
fn dispatch_query_known_slot(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    listener: HookListener,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<crate::content_dispatch::HookResult, KernelError> {
    #[cfg(debug_assertions)]
    debug_assert!(
        is_eligible_hook_listener(state, listener),
        "cached query listener {:?} ineligible on {:?}",
        listener,
        slot
    );
    if let HookListener::Power {
        creature,
        power_handle,
    } = listener
    {
        let Some(c) = state.creature(creature) else {
            return Ok(Default::default());
        };
        let mut found = None;
        for (idx, &p) in c.powers.iter().enumerate() {
            if p.handle == power_handle {
                found = Some((idx, p));
                break;
            }
        }
        let Some((power_idx, power)) = found else {
            return Ok(Default::default());
        };
        let owner_side = c.side;
        return content.dispatch_power_hook_resolved(
            state, creature, power_idx, power, owner_side, slot, args,
        );
    }
    content.dispatch_hook(state, listener, slot, args)
}

#[inline]
fn dispatch_cached_query_listeners_slot_ready<F>(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    word: usize,
    bit: u64,
    mut on_listener: F,
) -> Result<(), KernelError>
where
    F: FnMut(&mut BattleState, &dyn ContentDispatcher, HookListener) -> Result<bool, KernelError>,
{
    let len = state.hook_cache.flat_listeners.len();
    for i in 0..len {
        if (state.hook_cache.flat_mask_word(word)[i] & bit) != 0 {
            let listener = state.hook_cache.flat_listeners[i];
            #[cfg(debug_assertions)]
            debug_assert!(
                is_eligible_hook_listener(state, listener),
                "cached query listener {:?} ineligible",
                listener
            );
            let cont = on_listener(state, content, listener)?;
            #[cfg(debug_assertions)]
            debug_assert_eq!(
                state.hook_cache.dirty_mask, 0,
                "query hook mutated hook_cache during {:?}",
                listener
            );
            if !cont {
                return Ok(());
            }
        }
    }
    Ok(())
}

#[inline]
fn dispatch_cached_query_listeners_slot<F>(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    slot: HookSlot,
    mut on_listener: F,
) -> Result<(), KernelError>
where
    F: FnMut(&mut BattleState, &dyn ContentDispatcher, HookListener) -> Result<bool, KernelError>,
{
    let (word, bit) = HookMask::slot_parts(slot);
    #[cfg(not(debug_assertions))]
    let cached_hit = state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some();
    #[cfg(debug_assertions)]
    let cached_hit = {
        state.ensure_hook_cache(content)?;
        state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some()
    };
    if !cached_hit {
        state.ensure_hook_cache(content)?;
    }
    if state.hook_cache.cached_tables.is_none() || state.hook_cache.dirty_mask != 0 {
        let mask = HookMask::EMPTY.with(slot);
        let listeners = collect_combat_only_listeners_for_mask(state, content, mask)?;
        for listener in listeners {
            if is_eligible_hook_listener(state, listener)
                && !on_listener(state, content, listener)?
            {
                break;
            }
        }
        return Ok(());
    }

    if !state
        .hook_cache
        .masks
        .combat_combined
        .contains_slot_parts(word, bit)
    {
        return Ok(());
    }

    dispatch_cached_query_listeners_slot_ready(state, content, word, bit, on_listener)
}

#[inline]
fn dispatch_cached_run_query_listeners_slot<F>(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    slot: HookSlot,
    mut on_listener: F,
) -> Result<(), KernelError>
where
    F: FnMut(&mut BattleState, &dyn ContentDispatcher, HookListener) -> Result<bool, KernelError>,
{
    let (word, bit) = HookMask::slot_parts(slot);
    #[cfg(not(debug_assertions))]
    let cached_hit = state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some();
    #[cfg(debug_assertions)]
    let cached_hit = {
        state.ensure_hook_cache(content)?;
        state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some()
    };
    if !cached_hit {
        state.ensure_hook_cache(content)?;
    }
    if state.hook_cache.cached_tables.is_none() || state.hook_cache.dirty_mask != 0 {
        let mask = HookMask::EMPTY.with(slot);
        let listeners = collect_run_combat_listeners_for_mask(state, content, mask)?;
        for listener in listeners {
            if is_eligible_hook_listener(state, listener)
                && !on_listener(state, content, listener)?
            {
                break;
            }
        }
        return Ok(());
    }

    if !state
        .hook_cache
        .masks
        .run_combat_combined
        .contains_slot_parts(word, bit)
    {
        return Ok(());
    }

    if state.player.is_active_for_hooks
        && state
            .hook_cache
            .masks
            .cards_deck
            .contains_slot_parts(word, bit)
    {
        let deck_len = state.hook_cache.listener_deck.len();
        for i in 0..deck_len {
            let (listener, lm) = state.hook_cache.listener_deck[i];
            if lm.contains_slot_parts(word, bit) {
                #[cfg(debug_assertions)]
                debug_assert!(
                    is_eligible_hook_listener(state, listener),
                    "cached deck query listener {:?} ineligible",
                    listener
                );
                let cont = on_listener(state, content, listener)?;
                #[cfg(debug_assertions)]
                debug_assert_eq!(
                    state.hook_cache.dirty_mask, 0,
                    "deck query hook mutated hook_cache during {:?}",
                    listener
                );
                if !cont {
                    return Ok(());
                }
            }
        }
    }

    if state
        .hook_cache
        .masks
        .combat_combined
        .contains_slot_parts(word, bit)
    {
        let len = state.hook_cache.flat_listeners.len();
        for i in 0..len {
            if (state.hook_cache.flat_mask_word(word)[i] & bit) != 0 {
                let listener = state.hook_cache.flat_listeners[i];
                #[cfg(debug_assertions)]
                debug_assert!(
                    is_eligible_hook_listener(state, listener),
                    "cached query listener {:?} ineligible",
                    listener
                );
                let cont = on_listener(state, content, listener)?;
                #[cfg(debug_assertions)]
                debug_assert_eq!(
                    state.hook_cache.dirty_mask, 0,
                    "query hook mutated hook_cache during {:?}",
                    listener
                );
                if !cont {
                    return Ok(());
                }
            }
        }
    }

    Ok(())
}

pub fn before_combat_start(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    dispatch_void_run_combat_slot(state, content, HookSlot::BeforeCombatStart, &HookArgs::None)?;
    dispatch_void_run_combat_slot(
        state,
        content,
        HookSlot::BeforeCombatStartLate,
        &HookArgs::None,
    )
}

pub fn after_room_entered(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    dispatch_void_run_combat_slot(state, content, HookSlot::AfterRoomEntered, &HookArgs::None)
}

pub fn after_combat_victory(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    dispatch_void_run_combat_slot(
        state,
        content,
        HookSlot::AfterCombatVictoryEarly,
        &HookArgs::None,
    )?;
    dispatch_void_run_combat_slot(state, content, HookSlot::AfterCombatVictory, &HookArgs::None)
}

pub fn after_combat_end(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    dispatch_void_run_combat_slot(state, content, HookSlot::AfterCombatEnd, &HookArgs::None)
}

pub fn before_side_turn_start(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    side: CombatSide,
    participants: &[CreatureHandle],
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::BeforeSideTurnStart,
        &HookArgs::SideTurn { side, participants },
        true,
    )
}

pub fn after_player_turn_start(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<Option<(crate::continuation::ChoiceRequest, HookListener, SmallVec<[HookListener; 16]>)>, KernelError> {
    let listeners = collect_combat_hook_listeners_checked_for_mask(
        state,
        content,
        HookMask::EMPTY.with(HookSlot::AfterPlayerTurnStart),
    )?;
    continue_after_player_turn_start(state, content, &listeners)
}

pub fn continue_after_player_turn_start(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    remaining: &[HookListener],
) -> Result<Option<(crate::continuation::ChoiceRequest, HookListener, SmallVec<[HookListener; 16]>)>, KernelError> {
    for (idx, &listener) in remaining.iter().enumerate() {
        if let Some(res) = dispatch_if_eligible_known_slot(
            state,
            content,
            listener,
            HookSlot::AfterPlayerTurnStart,
            &HookArgs::Player,
        )? {
            if let Some(req) = res.needs_choice {
                let rest = remaining[idx + 1..].iter().copied().collect();
                return Ok(Some((req, listener, rest)));
            }
        }
    }
    Ok(None)
}

pub fn after_player_turn_start_late(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterPlayerTurnStartLate,
        &HookArgs::Player,
        true,
    )
}

pub fn after_side_turn_start(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    side: CombatSide,
    participants: &[CreatureHandle],
) -> Result<(), KernelError> {
    let args = HookArgs::SideTurn { side, participants };
    dispatch_void_combat_slot(state, content, HookSlot::AfterSideTurnStart, &args, true)?;
    dispatch_void_combat_slot(state, content, HookSlot::AfterSideTurnStartLate, &args, true)
}

pub fn after_auto_pre_play_phase_entered(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<bool, KernelError> {
    let listeners = collect_combat_hook_listeners_checked_for_mask(
        state,
        content,
        HookMask::EMPTY.with(HookSlot::AfterAutoPrePlayPhaseEntered),
    )?;
    continue_after_auto_pre_play_phase_entered(state, content, &listeners, false)
}

pub fn continue_after_auto_pre_play_phase_entered(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    remaining: &[HookListener],
    is_late: bool,
) -> Result<bool, KernelError> {
    let slot = if is_late {
        HookSlot::AfterAutoPrePlayPhaseEnteredLate
    } else {
        HookSlot::AfterAutoPrePlayPhaseEntered
    };
    for (idx, &listener) in remaining.iter().enumerate() {
        let _ = dispatch_if_eligible_known_slot(state, content, listener, slot, &HookArgs::Player)?;
        if let Some(pending) = state.pending.as_mut() {
            pending.frames.insert(
                0,
                crate::continuation::ContinuationFrame::AfterAutoPrePlayResume {
                    remaining_listeners: remaining[idx + 1..].iter().copied().collect(),
                    is_late,
                },
            );
            return Ok(true);
        }
    }
    if !is_late {
        let listeners_late = collect_combat_hook_listeners_checked_for_mask(
            state,
            content,
            HookMask::EMPTY.with(HookSlot::AfterAutoPrePlayPhaseEnteredLate),
        )?;
        return continue_after_auto_pre_play_phase_entered(state, content, &listeners_late, true);
    }
    Ok(false)
}

pub fn after_auto_post_play_phase_entered(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterAutoPostPlayPhaseEntered,
        &HookArgs::Player,
        true,
    )
}

pub fn before_side_turn_end_very_early(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    side: CombatSide,
    participants: &[CreatureHandle],
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::BeforeSideTurnEndVeryEarly,
        &HookArgs::SideTurn { side, participants },
        true,
    )
}

pub fn before_side_turn_end_early(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    side: CombatSide,
    participants: &[CreatureHandle],
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::BeforeSideTurnEndEarly,
        &HookArgs::SideTurn { side, participants },
        true,
    )
}

pub fn before_side_turn_end(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    side: CombatSide,
    participants: &[CreatureHandle],
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::BeforeSideTurnEnd,
        &HookArgs::SideTurn { side, participants },
        true,
    )
}

pub fn after_side_turn_end(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    side: CombatSide,
    participants: &[CreatureHandle],
) -> Result<(), KernelError> {
    let args = HookArgs::SideTurn { side, participants };
    dispatch_void_combat_slot(state, content, HookSlot::AfterSideTurnEnd, &args, true)?;
    dispatch_void_combat_slot(state, content, HookSlot::AfterSideTurnEndLate, &args, true)
}

pub fn should_clear_block(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
) -> Result<Option<HookListener>, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ShouldClearBlock);
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok(None);
    }
    let mut preventer = None;
    dispatch_cached_query_listeners_slot(state, content, HookSlot::ShouldClearBlock, |s, c, listener| {
        let res = dispatch_query_known_slot(s, c, listener, HookSlot::ShouldClearBlock, &HookArgs::Creature(creature))?;
        if res.bool_val == Some(false) {
            preventer = Some(listener);
            return Ok(false);
        }
        Ok(true)
    })?;
    Ok(preventer)
}

pub fn after_preventing_block_clear(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    preventer: HookListener,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    let _ = dispatch_if_active(
        state,
        content,
        preventer,
        HookSlot::AfterPreventingBlockClear,
        &HookArgs::Creature(creature),
    )?;
    Ok(())
}

pub fn after_block_cleared(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterBlockCleared,
        &HookArgs::Creature(creature),
        true,
    )
}

pub fn should_player_reset_energy(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<bool, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ShouldPlayerResetEnergy);
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok(true);
    }
    let mut should = true;
    dispatch_cached_query_listeners_slot(state, content, HookSlot::ShouldPlayerResetEnergy, |s, c, listener| {
        let res = dispatch_query_known_slot(s, c, listener, HookSlot::ShouldPlayerResetEnergy, &HookArgs::Player)?;
        if res.bool_val == Some(false) {
            should = false;
            return Ok(false);
        }
        Ok(true)
    })?;
    Ok(should)
}

pub fn modify_x_value(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    original_value: i32,
) -> Result<i32, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ModifyXValue);
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok(original_value);
    }
    let mut result = original_value;
    dispatch_cached_query_listeners_slot(state, content, HookSlot::ModifyXValue, |s, c, listener| {
        let res = dispatch_query_known_slot(
            s,
            c,
            listener,
            HookSlot::ModifyXValue,
            &HookArgs::ModifyXValue {
                card,
                amount: result,
            },
        )?;
        if let Some(v) = res.int_val {
            result = v;
        }
        Ok(true)
    })?;
    Ok(result)
}

pub fn modify_max_energy(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    base_max: Dec,
) -> Result<Dec, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ModifyMaxEnergy);
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok(base_max);
    }
    let mut amount = base_max;
    dispatch_cached_query_listeners_slot(state, content, HookSlot::ModifyMaxEnergy, |s, c, listener| {
        let res = dispatch_query_known_slot(
            s,
            c,
            listener,
            HookSlot::ModifyMaxEnergy,
            &HookArgs::ModifyHandDraw {
                original_count: amount,
            },
        )?;
        if let Some(v) = res.dec_val {
            amount = v;
        }
        Ok(true)
    })?;
    Ok(amount)
}

pub fn after_energy_reset(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterEnergyReset,
        &HookArgs::Player,
        true,
    )?;
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterEnergyResetLate,
        &HookArgs::Player,
        true,
    )
}

pub fn before_hand_draw(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<Option<(crate::continuation::ChoiceRequest, HookListener, SmallVec<[HookListener; 16]>)>, KernelError> {
    let listeners = collect_combat_hook_listeners_checked_for_mask(
        state,
        content,
        HookMask::EMPTY.with(HookSlot::BeforeHandDraw),
    )?;
    for (idx, &listener) in listeners.iter().enumerate() {
        if let Some(res) = dispatch_if_eligible_known_slot(
            state,
            content,
            listener,
            HookSlot::BeforeHandDraw,
            &HookArgs::Player,
        )? {
            if let Some(req) = res.needs_choice {
                let remaining = listeners[idx + 1..].iter().copied().collect();
                return Ok(Some((req, listener, remaining)));
            }
        }
    }
    Ok(None)
}

pub fn continue_before_hand_draw(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    remaining: &[HookListener],
) -> Result<Option<(crate::continuation::ChoiceRequest, HookListener, SmallVec<[HookListener; 16]>)>, KernelError> {
    for (idx, &listener) in remaining.iter().enumerate() {
        if let Some(res) = dispatch_if_eligible_known_slot(
            state,
            content,
            listener,
            HookSlot::BeforeHandDraw,
            &HookArgs::Player,
        )? {
            if let Some(req) = res.needs_choice {
                let rest = remaining[idx + 1..].iter().copied().collect();
                return Ok(Some((req, listener, rest)));
            }
        }
    }
    Ok(None)
}

pub fn modify_hand_draw(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    base_count: Dec,
) -> Result<(Dec, SmallVec<[HookListener; 4]>), KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ModifyHandDraw);
    let mut count = base_count;
    let mut modifiers = SmallVec::new();
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok((count, modifiers));
    }
    dispatch_cached_query_listeners_slot(state, content, HookSlot::ModifyHandDraw, |s, c, listener| {
        let res = dispatch_query_known_slot(
            s,
            c,
            listener,
            HookSlot::ModifyHandDraw,
            &HookArgs::ModifyHandDraw {
                original_count: count,
            },
        )?;
        if let Some(v) = res.dec_val {
            if v != count {
                count = v;
                modifiers.push(listener);
            }
        }
        Ok(true)
    })?;
    Ok((count, modifiers))
}

pub fn after_modifying_hand_draw(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    modifiers: &[HookListener],
) -> Result<(), KernelError> {
    dispatch_void_list(
        state,
        content,
        modifiers,
        HookSlot::AfterModifyingHandDraw,
        &HookArgs::Player,
    )
}

pub fn should_draw(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    from_hand_draw: bool,
) -> Result<bool, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ShouldDraw);
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok(true);
    }
    let mut should = true;
    dispatch_cached_query_listeners_slot(state, content, HookSlot::ShouldDraw, |s, c, listener| {
        let res = dispatch_query_known_slot(
            s,
            c,
            listener,
            HookSlot::ShouldDraw,
            &HookArgs::ShouldDraw { from_hand_draw },
        )?;
        if res.bool_val == Some(false) {
            should = false;
            return Ok(false);
        }
        Ok(true)
    })?;
    Ok(should)
}

pub fn should_flush(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<bool, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ShouldFlush);
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok(true);
    }
    let mut should = true;
    dispatch_cached_query_listeners_slot(state, content, HookSlot::ShouldFlush, |s, c, listener| {
        let res = dispatch_query_known_slot(s, c, listener, HookSlot::ShouldFlush, &HookArgs::Player)?;
        if res.bool_val == Some(false) {
            should = false;
            return Ok(false);
        }
        Ok(true)
    })?;
    Ok(should)
}

pub fn before_flush(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::BeforeFlush,
        &HookArgs::Player,
        true,
    )?;
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::BeforeFlushLate,
        &HookArgs::Player,
        true,
    )
}

pub fn after_flush(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterFlush,
        &HookArgs::Player,
        true,
    )
}

pub fn should_ethereal_trigger(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
) -> Result<bool, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ShouldEtherealTrigger);
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok(true);
    }
    let mut should = true;
    dispatch_cached_query_listeners_slot(state, content, HookSlot::ShouldEtherealTrigger, |s, c, listener| {
        let res = dispatch_query_known_slot(
            s,
            c,
            listener,
            HookSlot::ShouldEtherealTrigger,
            &HookArgs::CardOnly(card),
        )?;
        if res.bool_val == Some(false) {
            should = false;
            return Ok(false);
        }
        Ok(true)
    })?;
    Ok(should)
}

pub fn should_take_extra_turn(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<Option<HookListener>, KernelError> {
    const MASK: HookMask = HookMask::EMPTY
        .with(HookSlot::ShouldTakeExtraTurnEarly)
        .with(HookSlot::ShouldTakeExtraTurn);
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok(None);
    }
    let mut taker = None;
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::ShouldTakeExtraTurnEarly,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ShouldTakeExtraTurnEarly,
                &HookArgs::Player,
            )?;
            if res.bool_val == Some(true) {
                taker = Some(listener);
                return Ok(false);
            }
            Ok(true)
        },
    )?;
    if taker.is_some() {
        return Ok(taker);
    }
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::ShouldTakeExtraTurn,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ShouldTakeExtraTurn,
                &HookArgs::Player,
            )?;
            if res.bool_val == Some(true) {
                taker = Some(listener);
                return Ok(false);
            }
            Ok(true)
        },
    )?;
    Ok(taker)
}

pub fn after_taking_extra_turn(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    source: HookListener,
) -> Result<(), KernelError> {
    let _ = dispatch_if_active(
        state,
        content,
        source,
        HookSlot::AfterTakingExtraTurn,
        &HookArgs::Player,
    )?;
    Ok(())
}

pub fn modify_vulnerable_multiplier(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: Option<CreatureHandle>,
    dealer: Option<CreatureHandle>,
    base_multiplier: Dec,
    props: ValueProp,
    card_source: Option<CardHandle>,
) -> Result<Dec, KernelError> {
    state.ensure_hook_cache(content)?;
    let cache_valid = state.hook_cache.is_valid();
    if cache_valid
        && !state
            .hook_cache
            .masks
            .combat_combined
            .contains(HookSlot::ModifyVulnerableMultiplier)
    {
        return Ok(base_multiplier);
    }
    let mut mult = base_multiplier;
    let mut ordered: SmallVec<[HookListener; 16]> = SmallVec::new();
    let tables = if cache_valid {
        state.hook_cache.cached_tables.or_else(|| content.hook_mask_tables())
    } else {
        None
    };
    let check_relics = !cache_valid
        || state
            .hook_cache
            .masks
            .relics
            .contains(HookSlot::ModifyVulnerableMultiplier);
    let check_powers = !cache_valid
        || state
            .hook_cache
            .masks
            .powers
            .contains(HookSlot::ModifyVulnerableMultiplier);
    if let Some(d) = dealer {
        let is_player_or_pet = state
            .creature(d)
            .is_some_and(|c| c.is_player || c.is_pet());
        if check_relics && is_player_or_pet && state.player.is_active_for_hooks {
            if cache_valid {
                for &(i, r_mask) in &state.hook_cache.listener_relics {
                    if r_mask.contains(HookSlot::ModifyVulnerableMultiplier) {
                        ordered.push(HookListener::Relic(i));
                    }
                }
            } else {
                for (i, relic) in state.player.relics.iter().enumerate() {
                    if !relic.is_melted {
                        ordered.push(HookListener::Relic(i));
                    }
                }
            }
        }
        if check_powers {
            if let Some(c) = state.creature(d) {
                for p in &c.powers {
                    if tables.is_none_or(|t| {
                        t.powers
                            .get(p.id as usize)
                            .is_some_and(|m| m.contains(HookSlot::ModifyVulnerableMultiplier))
                    }) {
                        ordered.push(HookListener::Power {
                            creature: d,
                            power_handle: p.handle,
                        });
                    }
                }
                if c.is_pet() && d != state.player.creature_handle {
                    if let Some(pc) = state.player_creature() {
                        for p in &pc.powers {
                            if tables.is_none_or(|t| {
                                t.powers
                                    .get(p.id as usize)
                                    .is_some_and(|m| m.contains(HookSlot::ModifyVulnerableMultiplier))
                            }) {
                                ordered.push(HookListener::Power {
                                    creature: state.player.creature_handle,
                                    power_handle: p.handle,
                                });
                            }
                        }
                    }
                }
            }
        }
    }
    if check_powers {
        if let Some(t) = target {
            if let Some(c) = state.creature(t) {
                for p in &c.powers {
                    if tables.is_none_or(|tbl| {
                        tbl.powers
                            .get(p.id as usize)
                            .is_some_and(|m| m.contains(HookSlot::ModifyVulnerableMultiplier))
                    }) {
                        ordered.push(HookListener::Power {
                            creature: t,
                            power_handle: p.handle,
                        });
                    }
                }
            }
        }
    }
    for listener in ordered {
        let res_opt = if tables.is_some() {
            dispatch_if_eligible_known_slot(
                state,
                content,
                listener,
                HookSlot::ModifyVulnerableMultiplier,
                &HookArgs::ModifyDamage {
                    target,
                    dealer,
                    amount: mult,
                    props,
                    card_source,
                    card_play: None,
                },
            )?
        } else {
            dispatch_if_active(
                state,
                content,
                listener,
                HookSlot::ModifyVulnerableMultiplier,
                &HookArgs::ModifyDamage {
                    target,
                    dealer,
                    amount: mult,
                    props,
                    card_source,
                    card_play: None,
                },
            )?
        };
        if let Some(res) = res_opt {
            if let Some(next) = res.dec_val {
                mult = next;
            }
        }
    }
    Ok(mult)
}

pub fn modify_weak_multiplier(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: Option<CreatureHandle>,
    dealer: Option<CreatureHandle>,
    base_multiplier: Dec,
    props: ValueProp,
    card_source: Option<CardHandle>,
) -> Result<Dec, KernelError> {
    state.ensure_hook_cache(content)?;
    let cache_valid = state.hook_cache.is_valid();
    if cache_valid
        && !state
            .hook_cache
            .masks
            .combat_combined
            .contains(HookSlot::ModifyWeakMultiplier)
    {
        return Ok(base_multiplier);
    }
    let mut mult = base_multiplier;
    let mut ordered: SmallVec<[HookListener; 16]> = SmallVec::new();
    let tables = if cache_valid {
        state.hook_cache.cached_tables.or_else(|| content.hook_mask_tables())
    } else {
        None
    };
    let check_relics = !cache_valid
        || state
            .hook_cache
            .masks
            .relics
            .contains(HookSlot::ModifyWeakMultiplier);
    let check_powers = !cache_valid
        || state
            .hook_cache
            .masks
            .powers
            .contains(HookSlot::ModifyWeakMultiplier);
    if check_relics {
        if let Some(t) = target {
            if state.creature(t).is_some_and(|c| c.is_player) && state.player.is_active_for_hooks {
                if cache_valid {
                    for &(i, r_mask) in &state.hook_cache.listener_relics {
                        if r_mask.contains(HookSlot::ModifyWeakMultiplier) {
                            ordered.push(HookListener::Relic(i));
                        }
                    }
                } else {
                    for (i, relic) in state.player.relics.iter().enumerate() {
                        if !relic.is_melted {
                            ordered.push(HookListener::Relic(i));
                        }
                    }
                }
            }
        }
    }
    if check_powers {
        if let Some(d) = dealer {
            if let Some(c) = state.creature(d) {
                for p in &c.powers {
                    if tables.is_none_or(|tbl| {
                        tbl.powers
                            .get(p.id as usize)
                            .is_some_and(|m| m.contains(HookSlot::ModifyWeakMultiplier))
                    }) {
                        ordered.push(HookListener::Power {
                            creature: d,
                            power_handle: p.handle,
                        });
                    }
                }
            }
        }
    }
    for listener in ordered {
        let res_opt = if tables.is_some() {
            dispatch_if_eligible_known_slot(
                state,
                content,
                listener,
                HookSlot::ModifyWeakMultiplier,
                &HookArgs::ModifyDamage {
                    target,
                    dealer,
                    amount: mult,
                    props,
                    card_source,
                    card_play: None,
                },
            )?
        } else {
            dispatch_if_active(
                state,
                content,
                listener,
                HookSlot::ModifyWeakMultiplier,
                &HookArgs::ModifyDamage {
                    target,
                    dealer,
                    amount: mult,
                    props,
                    card_source,
                    card_play: None,
                },
            )?
        };
        if let Some(res) = res_opt {
            if let Some(next) = res.dec_val {
                mult = next;
            }
        }
    }
    Ok(mult)
}

pub fn should_allow_hitting(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
) -> Result<bool, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ShouldAllowHitting);
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok(true);
    }
    let mut allowed = true;
    dispatch_cached_query_listeners_slot(state, content, HookSlot::ShouldAllowHitting, |s, c, listener| {
        let res = dispatch_query_known_slot(s, c, listener, HookSlot::ShouldAllowHitting, &HookArgs::Creature(creature))?;
        if res.bool_val == Some(false) {
            allowed = false;
            return Ok(false);
        }
        Ok(true)
    })?;
    Ok(allowed)
}

pub fn modify_damage(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: Option<CreatureHandle>,
    dealer: Option<CreatureHandle>,
    damage: Dec,
    props: ValueProp,
    card_source: Option<CardHandle>,
    card_play: Option<crate::content_dispatch::CardPlayContext>,
) -> Result<Dec, KernelError> {
    modify_damage_with_type(
        state,
        content,
        target,
        dealer,
        damage,
        props,
        card_source,
        card_play,
        ModifyDamageHookType::ALL,
    )
}

#[allow(clippy::too_many_arguments)]
pub fn modify_damage_with_type(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: Option<CreatureHandle>,
    dealer: Option<CreatureHandle>,
    damage: Dec,
    props: ValueProp,
    card_source: Option<CardHandle>,
    card_play: Option<crate::content_dispatch::CardPlayContext>,
    hook_type: ModifyDamageHookType,
) -> Result<Dec, KernelError> {
    let mut result = damage;
    if let Some(card_h) = card_source {
        let has_ench = state
            .card(card_h)
            .and_then(|c| state.card_ext(c.ext_index))
            .and_then(|e| e.enchantment.as_ref())
            .is_some();
        if has_ench {
            if hook_type.contains(ModifyDamageHookType::ADDITIVE) {
                let add_delta =
                    content.enchantment_damage_additive(state, card_h, result, props)?;
                result = result.checked_add(add_delta)?;
            }
            if hook_type.contains(ModifyDamageHookType::MULTIPLICATIVE) {
                let mult_factor =
                    content.enchantment_damage_multiplicative(state, card_h, result, props)?;
                result = result.checked_mul(mult_factor)?;
            }
        }
    }
    let mut mask = HookMask::EMPTY;
    if hook_type.contains(ModifyDamageHookType::ADDITIVE) {
        mask = mask.with(HookSlot::ModifyDamageAdditive);
    }
    if hook_type.contains(ModifyDamageHookType::MULTIPLICATIVE) {
        mask = mask
            .with(HookSlot::ModifyDamageMultiplicative)
            .with(HookSlot::ModifyDamageMultiplicativeLate);
    }
    if hook_type.contains(ModifyDamageHookType::CAP) {
        mask = mask.with(HookSlot::ModifyDamageCap);
    }
    if mask == HookMask::EMPTY {
        return Ok(result.max(Dec::ZERO));
    }
    if skip_run_combat_hook_mask(state, content, mask)? {
        return Ok(result.max(Dec::ZERO));
    }

    if hook_type.contains(ModifyDamageHookType::ADDITIVE) {
        dispatch_cached_run_query_listeners_slot(
            state,
            content,
            HookSlot::ModifyDamageAdditive,
            |s, c, listener| {
                let res = dispatch_query_known_slot(
                    s,
                    c,
                    listener,
                    HookSlot::ModifyDamageAdditive,
                    &HookArgs::ModifyDamage {
                        target,
                        dealer,
                        amount: result,
                        props,
                        card_source,
                        card_play,
                    },
                )?;
                if let Some(delta) = res.dec_val {
                    result = result.checked_add(delta)?;
                }
                Ok(true)
            },
        )?;
    }

    if hook_type.contains(ModifyDamageHookType::MULTIPLICATIVE) {
        dispatch_cached_run_query_listeners_slot(
            state,
            content,
            HookSlot::ModifyDamageMultiplicative,
            |s, c, listener| {
                let res = dispatch_query_known_slot(
                    s,
                    c,
                    listener,
                    HookSlot::ModifyDamageMultiplicative,
                    &HookArgs::ModifyDamage {
                        target,
                        dealer,
                        amount: result,
                        props,
                        card_source,
                        card_play,
                    },
                )?;
                if let Some(factor) = res.dec_val {
                    result = result.checked_mul(factor)?;
                }
                Ok(true)
            },
        )?;

        dispatch_cached_run_query_listeners_slot(
            state,
            content,
            HookSlot::ModifyDamageMultiplicativeLate,
            |s, c, listener| {
                let res = dispatch_query_known_slot(
                    s,
                    c,
                    listener,
                    HookSlot::ModifyDamageMultiplicativeLate,
                    &HookArgs::ModifyDamage {
                        target,
                        dealer,
                        amount: result,
                        props,
                        card_source,
                        card_play,
                    },
                )?;
                if let Some(factor) = res.dec_val {
                    result = result.checked_mul(factor)?;
                }
                Ok(true)
            },
        )?;
    }

    if hook_type.contains(ModifyDamageHookType::CAP) {
        let mut cap = Dec::MAX_VALUE;
        dispatch_cached_run_query_listeners_slot(
            state,
            content,
            HookSlot::ModifyDamageCap,
            |s, c, listener| {
                let res = dispatch_query_known_slot(
                    s,
                    c,
                    listener,
                    HookSlot::ModifyDamageCap,
                    &HookArgs::ModifyDamage {
                        target,
                        dealer,
                        amount: result,
                        props,
                        card_source,
                        card_play,
                    },
                )?;
                if let Some(item_cap) = res.dec_val {
                    if item_cap < cap {
                        cap = item_cap;
                        if result > item_cap {
                            result = item_cap;
                        }
                    }
                }
                Ok(true)
            },
        )?;
    }

    Ok(result.max(Dec::ZERO))
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum HpLossPhase {
    BeforeOsty,
    AfterOsty,
}

pub fn modify_hp_lost(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    amount: Dec,
    props: ValueProp,
    dealer: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
    phase: HpLossPhase,
) -> Result<(Dec, SmallVec<[HookListener; 4]>), KernelError> {
    let mut result = amount;
    let mut changed = SmallVec::new();
    let slots = match phase {
        HpLossPhase::BeforeOsty => [
            HookSlot::ModifyHpLostBeforeOsty,
            HookSlot::ModifyHpLostBeforeOstyLate,
        ],
        HpLossPhase::AfterOsty => [
            HookSlot::ModifyHpLostAfterOsty,
            HookSlot::ModifyHpLostAfterOstyLate,
        ],
    };
    let mask = HookMask::EMPTY.with(slots[0]).with(slots[1]);
    if skip_run_combat_hook_mask(state, content, mask)? {
        return Ok((result, changed));
    }
    for slot in slots {
        dispatch_cached_run_query_listeners_slot(state, content, slot, |s, c, listener| {
            let before = result;
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                slot,
                &HookArgs::ModifyHpLost {
                    target,
                    amount: result,
                    props,
                    dealer,
                    card_source,
                },
            )?;
            if let Some(next) = res.dec_val {
                result = next;
                if before.trunc() != result.trunc() {
                    changed.push(listener);
                }
            }
            Ok(true)
        })?;
    }
    Ok((result, changed))
}

pub fn after_modifying_hp_lost(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    modifiers: &[HookListener],
    phase: HpLossPhase,
) -> Result<(), KernelError> {
    let slot = match phase {
        HpLossPhase::BeforeOsty => HookSlot::AfterModifyingHpLostBeforeOsty,
        HpLossPhase::AfterOsty => HookSlot::AfterModifyingHpLostAfterOsty,
    };
    dispatch_void_list(state, content, modifiers, slot, &HookArgs::None)
}

pub fn before_damage_received(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    amount: Dec,
    props: ValueProp,
    dealer: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    dispatch_void_run_combat_slot(
        state,
        content,
        HookSlot::BeforeDamageReceived,
        &HookArgs::ModifyHpLost {
            target,
            amount,
            props,
            dealer,
            card_source,
        },
    )
}

pub fn modify_pre_block_damage_target(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    original_target: CreatureHandle,
    amount: Dec,
    props: ValueProp,
    dealer: Option<CreatureHandle>,
) -> Result<Option<CreatureHandle>, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ModifyPreBlockDamageTarget);
    if skip_combat_hook_mask(state, content, MASK)? {
        return Ok(None);
    }
    let mut target = None;
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::ModifyPreBlockDamageTarget,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ModifyPreBlockDamageTarget,
                &HookArgs::ModifyUnblockedTarget {
                    target: original_target,
                    amount,
                    props,
                    dealer,
                },
            )?;
            if res.creature_val.is_some() {
                target = res.creature_val;
            }
            Ok(true)
        },
    )?;
    Ok(target)
}

pub fn modify_unblocked_damage_target(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    original_target: CreatureHandle,
    amount: Dec,
    props: ValueProp,
    dealer: Option<CreatureHandle>,
) -> Result<CreatureHandle, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ModifyUnblockedDamageTarget);
    if skip_combat_hook_mask(state, content, MASK)? {
        return Ok(original_target);
    }
    let mut target = original_target;
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::ModifyUnblockedDamageTarget,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ModifyUnblockedDamageTarget,
                &HookArgs::ModifyUnblockedTarget {
                    target,
                    amount,
                    props,
                    dealer,
                },
            )?;
            if let Some(next) = res.creature_val {
                target = next;
            }
            Ok(true)
        },
    )?;
    Ok(target)
}

pub fn after_block_broken(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    breaker: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterBlockBroken,
        &HookArgs::BlockBroken { target, breaker },
        false,
    )
}

pub fn after_current_hp_changed(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
    delta: i32,
) -> Result<(), KernelError> {
    dispatch_void_run_combat_slot(
        state,
        content,
        HookSlot::AfterCurrentHpChanged,
        &HookArgs::ModifyHpLost {
            target: creature,
            amount: Dec::from_i32(delta),
            props: ValueProp::empty(),
            dealer: None,
            card_source: None,
        },
    )
}

pub fn after_damage_given(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    args: &HookArgs<'_>,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(state, content, HookSlot::AfterDamageGiven, args, false)
}

pub fn after_damage_received(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    args: &HookArgs<'_>,
) -> Result<(), KernelError> {
    dispatch_void_run_combat_slot(state, content, HookSlot::AfterDamageReceived, args)
}

pub fn before_attack(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    info: crate::content_dispatch::AttackContextInfo,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::BeforeAttack,
        &HookArgs::Attack { info, results: &[] },
        true,
    )
}

pub fn modify_attack_hit_count(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    info: crate::content_dispatch::AttackContextInfo,
    original_hit_count: i32,
) -> Result<i32, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ModifyAttackHitCount);
    let mut hit_count = original_hit_count;
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok(hit_count);
    }
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::ModifyAttackHitCount,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ModifyAttackHitCount,
                &HookArgs::Attack {
                    info: crate::content_dispatch::AttackContextInfo {
                        hit_count,
                        ..info
                    },
                    results: &[],
                },
            )?;
            if let Some(v) = res.int_val {
                hit_count = v;
            }
            Ok(true)
        },
    )?;
    Ok(hit_count)
}

pub fn after_attack(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    info: crate::content_dispatch::AttackContextInfo,
    results: &[DamageResult],
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterAttack,
        &HookArgs::Attack { info, results },
        true,
    )
}

pub fn before_block_gained(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    amount: Dec,
    props: ValueProp,
    card_source: Option<CardHandle>,
    card_play: Option<crate::content_dispatch::CardPlayContext>,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::BeforeBlockGained,
        &HookArgs::ModifyBlock {
            target,
            amount,
            props,
            card_source,
            card_play,
        },
        true,
    )
}

pub fn modify_block(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    block: Dec,
    props: ValueProp,
    card_source: Option<CardHandle>,
    card_play: Option<crate::content_dispatch::CardPlayContext>,
) -> Result<(Dec, SmallVec<[HookListener; 4]>), KernelError> {
    const MASK: HookMask = HookMask::EMPTY
        .with(HookSlot::ModifyBlockAdditive)
        .with(HookSlot::ModifyBlockMultiplicative);
    let mut result = block;
    let mut changed = SmallVec::new();
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok((result.max(Dec::ZERO), changed));
    }

    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::ModifyBlockAdditive,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ModifyBlockAdditive,
                &HookArgs::ModifyBlock {
                    target,
                    amount: result,
                    props,
                    card_source,
                    card_play,
                },
            )?;
            if let Some(delta) = res.dec_val {
                result = result.checked_add(delta)?;
                if !delta.is_zero() {
                    changed.push(listener);
                }
            }
            Ok(true)
        },
    )?;

    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::ModifyBlockMultiplicative,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ModifyBlockMultiplicative,
                &HookArgs::ModifyBlock {
                    target,
                    amount: result,
                    props,
                    card_source,
                    card_play,
                },
            )?;
            if let Some(factor) = res.dec_val {
                result = result.checked_mul(factor)?;
                if factor != Dec::ONE {
                    changed.push(listener);
                }
            }
            Ok(true)
        },
    )?;

    Ok((result.max(Dec::ZERO), changed))
}

#[allow(clippy::too_many_arguments)]
pub fn after_modifying_block_amount(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    modified_amount: Dec,
    props: ValueProp,
    card_source: Option<CardHandle>,
    card_play: Option<crate::content_dispatch::CardPlayContext>,
    modifiers: &[HookListener],
) -> Result<(), KernelError> {
    if modifiers.is_empty() {
        return Ok(());
    }
    let listeners = collect_combat_hook_listeners_checked_for_mask(
        state,
        content,
        HookMask::EMPTY.with(HookSlot::AfterModifyingBlockAmount),
    )?;
    let args = HookArgs::ModifyBlock {
        target,
        amount: modified_amount,
        props,
        card_source,
        card_play,
    };
    for listener in listeners {
        if !modifiers.contains(&listener) {
            continue;
        }
        let _ = dispatch_if_eligible_known_slot(
            state,
            content,
            listener,
            HookSlot::AfterModifyingBlockAmount,
            &args,
        )?;
    }
    Ok(())
}

pub fn after_block_gained(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    amount: Dec,
    props: ValueProp,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterBlockGained,
        &HookArgs::ModifyBlock {
            target,
            amount,
            props,
            card_source,
            card_play: None,
        },
        true,
    )
}

pub fn before_death(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
) -> Result<(), KernelError> {
    dispatch_void_run_combat_slot(state, content, HookSlot::BeforeDeath, &HookArgs::Creature(target))
}

pub fn should_die(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
) -> Result<Option<HookListener>, KernelError> {
    const MASK: HookMask = HookMask::EMPTY
        .with(HookSlot::ShouldDie)
        .with(HookSlot::ShouldDieLate);
    if skip_run_combat_hook_mask(state, content, MASK)? {
        return Ok(None);
    }
    let mut preventer = None;
    dispatch_cached_run_query_listeners_slot(state, content, HookSlot::ShouldDie, |s, c, listener| {
        let res = dispatch_query_known_slot(
            s,
            c,
            listener,
            HookSlot::ShouldDie,
            &HookArgs::Creature(target),
        )?;
        if res.bool_val == Some(false) {
            preventer = Some(listener);
            return Ok(false);
        }
        Ok(true)
    })?;
    if preventer.is_some() {
        return Ok(preventer);
    }
    dispatch_cached_run_query_listeners_slot(
        state,
        content,
        HookSlot::ShouldDieLate,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ShouldDieLate,
                &HookArgs::Creature(target),
            )?;
            if res.bool_val == Some(false) {
                preventer = Some(listener);
                return Ok(false);
            }
            Ok(true)
        },
    )?;
    Ok(preventer)
}

pub fn after_preventing_death(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    preventer: HookListener,
    target: CreatureHandle,
) -> Result<(), KernelError> {
    let _ = dispatch_if_active(
        state,
        content,
        preventer,
        HookSlot::AfterPreventingDeath,
        &HookArgs::Creature(target),
    )?;
    Ok(())
}

pub fn after_death(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    card_source: Option<CardHandle>,
    was_removal_prevented: bool,
) -> Result<(), KernelError> {
    dispatch_void_run_combat_slot(
        state,
        content,
        HookSlot::AfterDeath,
        &HookArgs::AfterDeath {
            target,
            card_source,
            was_removal_prevented,
        },
    )
}

pub fn should_creature_be_removed_after_death(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
) -> Result<bool, KernelError> {
    const MASK: HookMask =
        HookMask::EMPTY.with(HookSlot::ShouldCreatureBeRemovedFromCombatAfterDeath);
    if skip_combat_hook_mask(state, content, MASK)? {
        return Ok(true);
    }
    let mut remove = true;
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::ShouldCreatureBeRemovedFromCombatAfterDeath,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ShouldCreatureBeRemovedFromCombatAfterDeath,
                &HookArgs::Creature(creature),
            )?;
            if res.bool_val == Some(false) {
                remove = false;
                return Ok(false);
            }
            Ok(true)
        },
    )?;
    Ok(remove)
}

pub fn should_owner_death_trigger_fatal(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    owner: CreatureHandle,
) -> Result<bool, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ShouldOwnerDeathTriggerFatal);
    if skip_run_combat_hook_mask(state, content, MASK)? {
        return Ok(true);
    }
    let mut should = true;
    dispatch_cached_run_query_listeners_slot(
        state,
        content,
        HookSlot::ShouldOwnerDeathTriggerFatal,
        |s, c, listener| {
            if matches!(listener, HookListener::Power { creature, .. } if creature == owner) {
                let res = dispatch_query_known_slot(
                    s,
                    c,
                    listener,
                    HookSlot::ShouldOwnerDeathTriggerFatal,
                    &HookArgs::Creature(owner),
                )?;
                if res.bool_val == Some(false) {
                    should = false;
                    return Ok(false);
                }
            }
            Ok(true)
        },
    )?;
    Ok(should)
}

pub fn should_power_be_removed_on_death(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    power_owner: CreatureHandle,
    power: PowerInst,
) -> Result<bool, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ShouldPowerBeRemovedOnDeath);
    if skip_combat_hook_mask(state, content, MASK)? {
        return Ok(true);
    }
    let meta = state.fast_power_meta(content, power.id)?;
    let args = HookArgs::PowerQuery {
        owner: power_owner,
        power_id: power.id,
        power_handle: power.handle,
        power_type: meta.power_type,
        is_temporary: meta.flags.contains(PowerStaticFlags::IS_TEMPORARY),
    };
    let mut should = true;
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::ShouldPowerBeRemovedOnDeath,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ShouldPowerBeRemovedOnDeath,
                &args,
            )?;
            if res.bool_val == Some(false) {
                should = false;
                return Ok(false);
            }
            Ok(true)
        },
    )?;
    Ok(should)
}

pub fn should_stop_combat_from_ending(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<bool, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ShouldStopCombatFromEnding);
    if skip_combat_hook_mask(state, content, MASK)? {
        return Ok(false);
    }
    let mut stop = false;
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::ShouldStopCombatFromEnding,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ShouldStopCombatFromEnding,
                &HookArgs::None,
            )?;
            if res.bool_val == Some(true) {
                stop = true;
                return Ok(false);
            }
            Ok(true)
        },
    )?;
    Ok(stop)
}

pub fn before_power_amount_changed(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    power_id: PowerId,
    power_handle: u32,
    owner: CreatureHandle,
    amount: Dec,
    applier: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::BeforePowerAmountChanged,
        &HookArgs::PowerChanged {
            power_id,
            power_handle,
            owner,
            amount,
            applier,
            card_source,
        },
        true,
    )
}

pub fn modify_power_amount_given(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    power_id: PowerId,
    power_handle: u32,
    owner: CreatureHandle,
    amount: Dec,
    applier: CreatureHandle,
    card_source: Option<CardHandle>,
) -> Result<Dec, KernelError> {
    const MASK: HookMask = HookMask::EMPTY
        .with(HookSlot::ModifyPowerAmountGivenAdditive)
        .with(HookSlot::ModifyPowerAmountGivenMultiplicative);
    let mut num = amount;
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok(num);
    }
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::ModifyPowerAmountGivenAdditive,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ModifyPowerAmountGivenAdditive,
                &HookArgs::PowerChanged {
                    power_id,
                    power_handle,
                    owner,
                    amount: num,
                    applier: Some(applier),
                    card_source,
                },
            )?;
            if let Some(delta) = res.dec_val {
                num = num.checked_add(delta)?;
            }
            Ok(true)
        },
    )?;
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::ModifyPowerAmountGivenMultiplicative,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ModifyPowerAmountGivenMultiplicative,
                &HookArgs::PowerChanged {
                    power_id,
                    power_handle,
                    owner,
                    amount: num,
                    applier: Some(applier),
                    card_source,
                },
            )?;
            if let Some(factor) = res.dec_val {
                num = num.checked_mul(factor)?;
            }
            Ok(true)
        },
    )?;
    Ok(num)
}

pub fn modify_power_amount_received(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    power_id: PowerId,
    power_handle: u32,
    owner: CreatureHandle,
    amount: Dec,
    applier: Option<CreatureHandle>,
) -> Result<(Dec, SmallVec<[HookListener; 4]>), KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::TryModifyPowerAmountReceived);
    let mut num = amount;
    let mut modifiers = SmallVec::new();
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok((num, modifiers));
    }
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::TryModifyPowerAmountReceived,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::TryModifyPowerAmountReceived,
                &HookArgs::PowerChanged {
                    power_id,
                    power_handle,
                    owner,
                    amount: num,
                    applier,
                    card_source: None,
                },
            )?;
            if res.modified {
                if let Some(next) = res.dec_val {
                    num = next;
                }
                modifiers.push(listener);
            }
            Ok(true)
        },
    )?;
    Ok((num, modifiers))
}

pub fn after_modifying_power_amount_received(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    modifiers: &[HookListener],
) -> Result<(), KernelError> {
    dispatch_void_list(
        state,
        content,
        modifiers,
        HookSlot::AfterModifyingPowerAmountReceived,
        &HookArgs::None,
    )
}

pub fn before_power_applied(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    power: PowerInst,
    amount: Dec,
    applier: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    let listener = HookListener::DetachedPower {
        creature: target,
        power,
    };
    let _ = dispatch_if_active(
        state,
        content,
        listener,
        HookSlot::BeforeApplied,
        &HookArgs::PowerChanged {
            power_id: power.id,
            power_handle: power.handle,
            owner: target,
            amount,
            applier,
            card_source,
        },
    )?;
    Ok(())
}

pub fn after_power_applied(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    power_id: PowerId,
    power_handle: u32,
    amount: Dec,
    applier: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    let listener = HookListener::Power {
        creature: target,
        power_handle,
    };
    let _ = dispatch_if_active(
        state,
        content,
        listener,
        HookSlot::AfterApplied,
        &HookArgs::PowerChanged {
            power_id,
            power_handle,
            owner: target,
            amount,
            applier,
            card_source,
        },
    )?;
    Ok(())
}

pub fn after_power_removed(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    old_owner: CreatureHandle,
    removed: PowerInst,
) -> Result<(), KernelError> {
    let listener = HookListener::DetachedPower {
        creature: old_owner,
        power: removed,
    };
    let _ = dispatch_if_active(
        state,
        content,
        listener,
        HookSlot::AfterRemoved,
        &HookArgs::PowerChanged {
            power_id: removed.id,
            power_handle: removed.handle,
            owner: old_owner,
            amount: Dec::from_i32(removed.amount),
            applier: removed.applier_handle,
            card_source: None,
        },
    )?;
    Ok(())
}

pub fn after_power_amount_changed(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    power_id: PowerId,
    power_handle: u32,
    owner: CreatureHandle,
    amount: Dec,
    applier: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterPowerAmountChanged,
        &HookArgs::PowerChanged {
            power_id,
            power_handle,
            owner,
            amount,
            applier,
            card_source,
        },
        true,
    )
}

pub fn should_play_unchecked_active(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
) -> Result<bool, KernelError> {
    let mut allowed = true;
    dispatch_cached_query_listeners_slot(state, content, HookSlot::ShouldPlay, |s, c, listener| {
        let res = dispatch_query_known_slot(
            s,
            c,
            listener,
            HookSlot::ShouldPlay,
            &HookArgs::CardOnly(card),
        )?;
        if res.bool_val == Some(false) {
            allowed = false;
            return Ok(false);
        }
        Ok(true)
    })?;
    Ok(allowed)
}

pub fn should_play(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
) -> Result<bool, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ShouldPlay);
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok(true);
    }
    should_play_unchecked_active(state, content, card)
}

pub fn before_card_played(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    ctx: &crate::content_dispatch::CardPlayContext,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::BeforeCardPlayed,
        &HookArgs::CardPlay(*ctx),
        true,
    )
}

pub fn after_card_played(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    ctx: &crate::content_dispatch::CardPlayContext,
) -> Result<(), KernelError> {
    const MASK_EARLY: HookMask = HookMask::EMPTY.with(HookSlot::AfterCardPlayed);
    const MASK_LATE: HookMask = HookMask::EMPTY.with(HookSlot::AfterCardPlayedLate);
    const MASK_BOTH: HookMask = MASK_EARLY.union(MASK_LATE);
    if skip_combat_hook_mask(state, content, MASK_BOTH)? {
        return Ok(());
    }
    let has_cache = state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some();
    let (run_early, run_late) = if has_cache {
        let comb = state.hook_cache.masks.combat_combined;
        (
            comb.contains(HookSlot::AfterCardPlayed),
            comb.contains(HookSlot::AfterCardPlayedLate),
        )
    } else {
        (true, true)
    };
    let args = HookArgs::CardPlay(*ctx);
    if run_early {
        dispatch_void_combat_slot(state, content, HookSlot::AfterCardPlayed, &args, false)?;
    }
    if run_late {
        dispatch_void_combat_slot(state, content, HookSlot::AfterCardPlayedLate, &args, false)?;
    }
    Ok(())
}

pub fn modify_card_play_count(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    target: Option<CreatureHandle>,
    play_count: i32,
) -> Result<(i32, SmallVec<[HookListener; 4]>), KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ModifyCardPlayCount);
    let mut result = play_count;
    let mut modifiers = SmallVec::new();
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok((result, modifiers));
    }
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::ModifyCardPlayCount,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ModifyCardPlayCount,
                &HookArgs::CardPlayCount {
                    card,
                    target,
                    play_count: result,
                },
            )?;
            if let Some(next) = res.int_val {
                if next != result {
                    result = next;
                    modifiers.push(listener);
                }
            }
            Ok(true)
        },
    )?;
    Ok((result, modifiers))
}

pub fn after_modifying_card_play_count(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    modifiers: &[HookListener],
) -> Result<(), KernelError> {
    dispatch_void_list(
        state,
        content,
        modifiers,
        HookSlot::AfterModifyingCardPlayCount,
        &HookArgs::CardOnly(card),
    )
}

#[allow(clippy::too_many_arguments)]
pub fn modify_card_play_result_location(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    is_auto_play: bool,
    energy_value: i32,
    pile: PileType,
    position: CardPilePosition,
) -> Result<(PileType, CardPilePosition, SmallVec<[HookListener; 4]>), KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ModifyCardPlayResultLocation);
    let mut result = (pile, position);
    let mut modifiers = SmallVec::new();
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok((result.0, result.1, modifiers));
    }
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::ModifyCardPlayResultLocation,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ModifyCardPlayResultLocation,
                &HookArgs::CardPlayResultLocation {
                    card,
                    is_auto_play,
                    energy_value,
                    pile: result.0,
                    position: result.1,
                },
            )?;
            if let Some(next) = res.location_val {
                if next != result {
                    result = next;
                    modifiers.push(listener);
                }
            }
            Ok(true)
        },
    )?;
    Ok((result.0, result.1, modifiers))
}

#[allow(clippy::too_many_arguments)]
pub fn after_modifying_card_play_result_location(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    is_auto_play: bool,
    energy_value: i32,
    pile: PileType,
    position: CardPilePosition,
    modifiers: &[HookListener],
) -> Result<(), KernelError> {
    dispatch_void_list(
        state,
        content,
        modifiers,
        HookSlot::AfterModifyingCardPlayResultLocation,
        &HookArgs::CardPlayResultLocation {
            card,
            is_auto_play,
            energy_value,
            pile,
            position,
        },
    )
}

#[inline]
pub fn modify_energy_cost_in_combat_cached_ready(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    original_cost: Dec,
    comb: HookMask,
) -> Result<(Dec, bool), KernelError> {
    #[cfg(debug_assertions)]
    {
        debug_assert_eq!(state.hook_cache.dirty_mask, 0);
        debug_assert!(state.hook_cache.cached_tables.is_some());
    }
    let mut cost = original_cost;
    let mut modified = false;
    let (w_early, b_early) = HookMask::slot_parts(HookSlot::TryModifyEnergyCostInCombat);
    let (w_late, b_late) = HookMask::slot_parts(HookSlot::TryModifyEnergyCostInCombatLate);
    if comb.contains_slot_parts(w_early, b_early) {
        dispatch_cached_query_listeners_slot_ready(
            state,
            content,
            w_early,
            b_early,
            |s, c, listener| {
                let res = dispatch_query_known_slot(
                    s,
                    c,
                    listener,
                    HookSlot::TryModifyEnergyCostInCombat,
                    &HookArgs::CardCost { card, cost },
                )?;
                if res.modified {
                    modified = true;
                    if let Some(next) = res.dec_val {
                        cost = next;
                    }
                }
                Ok(true)
            },
        )?;
    }
    if comb.contains_slot_parts(w_late, b_late) {
        dispatch_cached_query_listeners_slot_ready(
            state,
            content,
            w_late,
            b_late,
            |s, c, listener| {
                let res = dispatch_query_known_slot(
                    s,
                    c,
                    listener,
                    HookSlot::TryModifyEnergyCostInCombatLate,
                    &HookArgs::CardCost { card, cost },
                )?;
                if res.modified {
                    modified = true;
                    if let Some(next) = res.dec_val {
                        cost = next;
                    }
                }
                Ok(true)
            },
        )?;
    }
    Ok((cost, modified))
}

pub fn modify_energy_cost_in_combat_unchecked_active(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    original_cost: Dec,
) -> Result<(Dec, bool), KernelError> {
    #[cfg(not(debug_assertions))]
    let cached_hit = state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some();
    #[cfg(debug_assertions)]
    let cached_hit = {
        state.ensure_hook_cache(content)?;
        state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some()
    };
    if !cached_hit {
        state.ensure_hook_cache(content)?;
    }
    if state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some() {
        let comb = state.hook_cache.masks.combat_combined;
        return modify_energy_cost_in_combat_cached_ready(state, content, card, original_cost, comb);
    }
    let mut cost = original_cost;
    let mut modified = false;
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::TryModifyEnergyCostInCombat,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::TryModifyEnergyCostInCombat,
                &HookArgs::CardCost { card, cost },
            )?;
            if res.modified {
                modified = true;
                if let Some(next) = res.dec_val {
                    cost = next;
                }
            }
            Ok(true)
        },
    )?;
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::TryModifyEnergyCostInCombatLate,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::TryModifyEnergyCostInCombatLate,
                &HookArgs::CardCost { card, cost },
            )?;
            if res.modified {
                modified = true;
                if let Some(next) = res.dec_val {
                    cost = next;
                }
            }
            Ok(true)
        },
    )?;
    Ok((cost, modified))
}

pub fn modify_energy_cost_in_combat(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    original_cost: Dec,
) -> Result<(Dec, bool), KernelError> {
    const MASK_EARLY: HookMask = HookMask::EMPTY.with(HookSlot::TryModifyEnergyCostInCombat);
    const MASK_LATE: HookMask = HookMask::EMPTY.with(HookSlot::TryModifyEnergyCostInCombatLate);
    const MASK: HookMask = MASK_EARLY.union(MASK_LATE);
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok((original_cost, false));
    }
    modify_energy_cost_in_combat_unchecked_active(state, content, card, original_cost)
}

pub fn modify_star_cost_in_combat_unchecked_active(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    original_cost: Dec,
) -> Result<(Dec, bool), KernelError> {
    let mut cost = original_cost;
    let mut modified = false;
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::TryModifyStarCostInCombat,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::TryModifyStarCostInCombat,
                &HookArgs::CardCost { card, cost },
            )?;
            if res.modified {
                modified = true;
                if let Some(next) = res.dec_val {
                    cost = next;
                }
            }
            Ok(true)
        },
    )?;
    Ok((cost, modified))
}

pub fn modify_star_cost_in_combat(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    original_cost: Dec,
) -> Result<(Dec, bool), KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::TryModifyStarCostInCombat);
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok((original_cost, false));
    }
    modify_star_cost_in_combat_unchecked_active(state, content, card, original_cost)
}

pub fn modify_keywords_in_combat_unchecked_active(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    keywords: CardKeywords,
) -> Result<CardKeywords, KernelError> {
    let mut kw = keywords;
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::TryModifyKeywordsInCombat,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::TryModifyKeywordsInCombat,
                &HookArgs::CardOnly(card),
            )?;
            if let Some(next) = res.keywords {
                kw |= next;
            }
            Ok(true)
        },
    )?;
    Ok(kw)
}

pub fn modify_keywords_in_combat(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    keywords: CardKeywords,
) -> Result<CardKeywords, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::TryModifyKeywordsInCombat);
    if skip_combat_hook_mask(state, content, MASK)? {
        return Ok(keywords);
    }
    modify_keywords_in_combat_unchecked_active(state, content, card, keywords)
}

pub fn after_card_changed_piles(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    old_pile: PileType,
) -> Result<(), KernelError> {
    const MASK_EARLY: HookMask = HookMask::EMPTY.with(HookSlot::AfterCardChangedPiles);
    const MASK_LATE: HookMask = HookMask::EMPTY.with(HookSlot::AfterCardChangedPilesLate);
    const MASK_BOTH: HookMask = MASK_EARLY.union(MASK_LATE);
    if skip_run_combat_hook_mask(state, content, MASK_BOTH)? {
        return Ok(());
    }
    let has_cache = state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some();
    let (run_early, run_late) = if has_cache {
        let comb = state.hook_cache.masks.run_combat_combined;
        (
            comb.contains(HookSlot::AfterCardChangedPiles),
            comb.contains(HookSlot::AfterCardChangedPilesLate),
        )
    } else {
        (true, true)
    };
    let args = HookArgs::CardPileChanged { card, old_pile };
    if run_early {
        dispatch_void_run_combat_slot(state, content, HookSlot::AfterCardChangedPiles, &args)?;
    }
    if run_late {
        dispatch_void_run_combat_slot(
            state,
            content,
            HookSlot::AfterCardChangedPilesLate,
            &args,
        )?;
    }
    Ok(())
}

pub fn after_card_drawn(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    from_hand_draw: bool,
) -> Result<(), KernelError> {
    const MASK_EARLY: HookMask = HookMask::EMPTY.with(HookSlot::AfterCardDrawnEarly);
    const MASK_NORMAL: HookMask = HookMask::EMPTY.with(HookSlot::AfterCardDrawn);
    const MASK_BOTH: HookMask = MASK_EARLY.union(MASK_NORMAL);
    if skip_combat_hook_mask_checked(state, content, MASK_BOTH)? {
        return Ok(());
    }
    let has_cache = state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some();
    let (run_early, run_normal) = if has_cache {
        let comb = state.hook_cache.masks.combat_combined;
        (
            comb.contains(HookSlot::AfterCardDrawnEarly),
            comb.contains(HookSlot::AfterCardDrawn),
        )
    } else {
        (true, true)
    };
    let args = HookArgs::CardDrawn {
        card,
        from_hand_draw,
    };
    if run_early {
        dispatch_void_combat_slot(state, content, HookSlot::AfterCardDrawnEarly, &args, true)?;
    }
    if run_normal {
        dispatch_void_combat_slot(state, content, HookSlot::AfterCardDrawn, &args, true)?;
    }
    Ok(())
}

pub fn after_card_discarded(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterCardDiscarded,
        &HookArgs::CardOnly(card),
        true,
    )
}

pub fn after_card_exhausted(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    caused_by_ethereal: bool,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterCardExhausted,
        &HookArgs::CardExhausted {
            card,
            caused_by_ethereal,
        },
        true,
    )
}

pub fn after_card_entered_combat(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterCardEnteredCombat,
        &HookArgs::CardOnly(card),
        true,
    )
}

pub fn after_card_generated(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    creator: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterCardGenerated,
        &HookArgs::CardGenerated { card, creator },
        true,
    )
}

pub fn after_card_generated_for_combat(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    creator: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterCardGeneratedForCombat,
        &HookArgs::CardGenerated { card, creator },
        true,
    )
}

pub fn after_hand_emptied(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterHandEmptied,
        &HookArgs::Player,
        true,
    )
}

pub fn after_shuffle(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<Option<(crate::continuation::ChoiceRequest, HookListener, SmallVec<[HookListener; 16]>)>, KernelError> {
    let listeners = collect_combat_hook_listeners_checked_for_mask(
        state,
        content,
        HookMask::EMPTY.with(HookSlot::AfterShuffle),
    )?;
    continue_after_shuffle(state, content, &listeners)
}

pub fn continue_after_shuffle(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    remaining: &[HookListener],
) -> Result<Option<(crate::continuation::ChoiceRequest, HookListener, SmallVec<[HookListener; 16]>)>, KernelError> {
    for (idx, &listener) in remaining.iter().enumerate() {
        if let Some(res) = dispatch_if_eligible_known_slot(
            state,
            content,
            listener,
            HookSlot::AfterShuffle,
            &HookArgs::Player,
        )? {
            if let Some(req) = res.needs_choice {
                let rest = remaining[idx + 1..].iter().copied().collect();
                return Ok(Some((req, listener, rest)));
            }
        }
    }
    Ok(None)
}

pub fn after_energy_spent(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    amount: i32,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterEnergySpent,
        &HookArgs::CardCost {
            card,
            cost: Dec::from_i32(amount),
        },
        true,
    )
}

pub fn should_gain_stars(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<bool, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ShouldGainStars);
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok(true);
    }
    let mut should = true;
    dispatch_cached_query_listeners_slot(state, content, HookSlot::ShouldGainStars, |s, c, listener| {
        let res = dispatch_query_known_slot(s, c, listener, HookSlot::ShouldGainStars, &HookArgs::Player)?;
        if res.bool_val == Some(false) {
            should = false;
            return Ok(false);
        }
        Ok(true)
    })?;
    Ok(should)
}

pub fn after_stars_gained(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    amount: i32,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterStarsGained,
        &HookArgs::ModifyHandDraw {
            original_count: Dec::from_i32(amount),
        },
        true,
    )
}

pub fn after_stars_spent(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    amount: i32,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterStarsSpent,
        &HookArgs::ModifyHandDraw {
            original_count: Dec::from_i32(amount),
        },
        true,
    )
}

pub fn modify_orb_value(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    orb: OrbId,
    amount: Dec,
) -> Result<Dec, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ModifyOrbValue);
    let mut result = amount;
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok(result);
    }
    dispatch_cached_query_listeners_slot(state, content, HookSlot::ModifyOrbValue, |s, c, listener| {
        let res = dispatch_query_known_slot(
            s,
            c,
            listener,
            HookSlot::ModifyOrbValue,
            &HookArgs::ModifyOrbValue {
                orb,
                amount: result,
            },
        )?;
        if let Some(v) = res.dec_val {
            result = v;
        }
        Ok(true)
    })?;
    Ok(result)
}

pub fn modify_orb_passive_trigger_count(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    orb: OrbId,
    orb_idx: usize,
    trigger_count: i32,
) -> Result<(i32, SmallVec<[HookListener; 4]>), KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ModifyOrbPassiveTriggerCounts);
    let mut result = trigger_count;
    let mut changed = SmallVec::new();
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok((result, changed));
    }
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::ModifyOrbPassiveTriggerCounts,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ModifyOrbPassiveTriggerCounts,
                &HookArgs::ModifyOrbPassiveTriggerCount {
                    orb,
                    orb_idx,
                    trigger_count: result,
                },
            )?;
            if let Some(v) = res.int_val {
                if v != result {
                    result = v;
                    changed.push(listener);
                }
            }
            Ok(true)
        },
    )?;
    Ok((result, changed))
}

pub fn after_modifying_orb_passive_trigger_count(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    modifiers: &[HookListener],
) -> Result<(), KernelError> {
    dispatch_void_list(
        state,
        content,
        modifiers,
        HookSlot::AfterModifyingOrbPassiveTriggerCount,
        &HookArgs::None,
    )
}

pub fn after_orb_channeled(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    orb: OrbId,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterOrbChanneled,
        &HookArgs::ModifyOrbValue {
            orb,
            amount: Dec::ZERO,
        },
        true,
    )
}

pub fn after_orb_evoked(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    orb: OrbId,
    targets: &[CreatureHandle],
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterOrbEvoked,
        &HookArgs::OrbEvoked { orb, targets },
        true,
    )
}

pub fn after_arrow_shot(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    arrow_idx: Option<usize>,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterArrowShot,
        &HookArgs::ArrowShot {
            arrow_idx,
            card_source,
        },
        true,
    )
}

pub fn after_arrow_impaled(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterArrowImpaled,
        &HookArgs::AfterDeath {
            target,
            card_source,
            was_removal_prevented: false,
        },
        true,
    )
}

pub fn after_arrow_dislodged(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterArrowDislodged,
        &HookArgs::AfterDeath {
            target,
            card_source,
            was_removal_prevented: false,
        },
        true,
    )
}

pub fn modify_summon_amount(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    amount: Dec,
) -> Result<Dec, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ModifySummonAmount);
    let mut result = amount;
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok(result);
    }
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::ModifySummonAmount,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ModifySummonAmount,
                &HookArgs::ModifySummonAmount { amount: result },
            )?;
            if let Some(v) = res.dec_val {
                result = v;
            }
            Ok(true)
        },
    )?;
    Ok(result)
}

pub fn after_summon(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    amount: Dec,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterSummon,
        &HookArgs::ModifySummonAmount { amount },
        true,
    )
}

pub fn after_osty_revived(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    osty: CreatureHandle,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterOstyRevived,
        &HookArgs::Creature(osty),
        true,
    )
}

pub fn after_creature_added_to_combat(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterCreatureAddedToCombat,
        &HookArgs::Creature(creature),
        false,
    )
}

pub fn after_died_to_doom(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterDiedToDoom,
        &HookArgs::Creature(creature),
        false,
    )
}

pub fn should_disappear_from_doom(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
) -> Result<bool, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ShouldDisappearFromDoom);
    if skip_combat_hook_mask_checked(state, content, MASK)? {
        return Ok(true);
    }
    let mut should = true;
    dispatch_cached_query_listeners_slot(
        state,
        content,
        HookSlot::ShouldDisappearFromDoom,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ShouldDisappearFromDoom,
                &HookArgs::Creature(creature),
            )?;
            if res.bool_val == Some(false) {
                should = false;
                return Ok(false);
            }
            Ok(true)
        },
    )?;
    Ok(should)
}

pub fn modify_gold_gained(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    amount: Dec,
) -> Result<Dec, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ModifyGoldGained);
    let mut result = amount;
    if skip_run_combat_hook_mask(state, content, MASK)? {
        return Ok(result);
    }
    dispatch_cached_run_query_listeners_slot(
        state,
        content,
        HookSlot::ModifyGoldGained,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ModifyGoldGained,
                &HookArgs::ModifySummonAmount { amount: result },
            )?;
            if let Some(v) = res.dec_val {
                result = v;
            }
            Ok(true)
        },
    )?;
    Ok(result)
}

pub fn after_gold_gained(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<(), KernelError> {
    dispatch_void_run_combat_slot(state, content, HookSlot::AfterGoldGained, &HookArgs::Player)
}

pub fn should_procure_potion(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    potion_id: sts2_types::PotionId,
) -> Result<bool, KernelError> {
    const MASK: HookMask = HookMask::EMPTY.with(HookSlot::ShouldProcurePotion);
    if skip_run_combat_hook_mask(state, content, MASK)? {
        return Ok(true);
    }
    let mut should = true;
    dispatch_cached_run_query_listeners_slot(
        state,
        content,
        HookSlot::ShouldProcurePotion,
        |s, c, listener| {
            let res = dispatch_query_known_slot(
                s,
                c,
                listener,
                HookSlot::ShouldProcurePotion,
                &HookArgs::Potion {
                    potion_id,
                    target: None,
                },
            )?;
            if res.bool_val == Some(false) {
                should = false;
                return Ok(false);
            }
            Ok(true)
        },
    )?;
    Ok(should)
}

pub fn before_potion_used(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    potion_id: sts2_types::PotionId,
    target: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    dispatch_void_run_combat_slot(
        state,
        content,
        HookSlot::BeforePotionUsed,
        &HookArgs::Potion { potion_id, target },
    )
}

pub fn after_potion_used(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    potion_id: sts2_types::PotionId,
) -> Result<(), KernelError> {
    dispatch_void_run_combat_slot(
        state,
        content,
        HookSlot::AfterPotionUsed,
        &HookArgs::Potion {
            potion_id,
            target: None,
        },
    )
}

pub fn after_potion_procured(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    potion_id: sts2_types::PotionId,
) -> Result<(), KernelError> {
    dispatch_void_run_combat_slot(
        state,
        content,
        HookSlot::AfterPotionProcured,
        &HookArgs::Potion {
            potion_id,
            target: None,
        },
    )
}

pub fn after_potion_discarded(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    potion_id: sts2_types::PotionId,
) -> Result<(), KernelError> {
    dispatch_void_run_combat_slot(
        state,
        content,
        HookSlot::AfterPotionDiscarded,
        &HookArgs::Potion {
            potion_id,
            target: None,
        },
    )
}

pub fn after_forge(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    amount: Dec,
) -> Result<(), KernelError> {
    dispatch_void_combat_slot(
        state,
        content,
        HookSlot::AfterForge,
        &HookArgs::Forge { amount },
        true,
    )
}
