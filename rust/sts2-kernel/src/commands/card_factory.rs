use crate::error::KernelError;
use crate::state::{BattleState, CardHandle};
use smallvec::SmallVec;
use sts2_types::{
    CardId, CardRarity, CardStaticFlags, CardType, CharacterId, GameMode, RunRngType,
    CARD_POOL_COLORLESS, CARD_POOL_CURSE, CARD_POOL_FOOL, CARD_POOL_STATUS, TAROT_CARDS,
};

#[inline]
pub fn is_card_allowed_in_run(state: &BattleState, id: CardId) -> bool {
    let meta = sts2_types::generated_card_meta(id);
    if meta.flags.contains(CardStaticFlags::IS_MULTIPLAYER_ONLY) {
        return false;
    }
    if state.static_ctx.game_mode == GameMode::Blitz
        && !meta.flags.contains(CardStaticFlags::IS_ALLOWED_IN_BLITZ)
    {
        return false;
    }
    true
}

pub fn ordered_combat_candidates(
    state: &BattleState,
    pool: &[CardId],
) -> SmallVec<[CardId; 128]> {
    let mut seen_bits = [0u64; (CardId::COUNT + 63) / 64];
    let mut out: SmallVec<[CardId; 128]> = SmallVec::with_capacity(pool.len().min(128));
    for &id in pool {
        let idx = id as usize;
        let word = &mut seen_bits[idx >> 6];
        let bit = 1u64 << (idx & 63);
        if (*word & bit) != 0 {
            continue;
        }
        *word |= bit;
        let meta = sts2_types::generated_card_meta(id);
        if meta.flags.contains(CardStaticFlags::CAN_BE_GENERATED_IN_COMBAT)
            && !matches!(
                meta.rarity,
                CardRarity::Basic | CardRarity::Ancient | CardRarity::Event
            )
            && is_card_allowed_in_run(state, id)
        {
            out.push(id);
        }
    }
    out
}

#[inline]
pub fn character_card_pool_raw(state: &BattleState) -> &[CardId] {
    if !state.static_ctx.card_generation_pools.is_empty() {
        state.static_ctx.card_generation_pools.as_slice()
    } else {
        sts2_types::character_card_pool(state.static_ctx.character_id)
    }
}

#[inline]
pub fn colorless_card_pool_raw(state: &BattleState) -> &[CardId] {
    if !state.static_ctx.colorless_card_pool.is_empty() {
        state.static_ctx.colorless_card_pool.as_slice()
    } else {
        CARD_POOL_COLORLESS
    }
}

#[inline]
pub fn colorless_card_pool_candidates(state: &BattleState) -> SmallVec<[CardId; 128]> {
    colorless_card_pool_raw(state).iter().copied().collect()
}

#[inline]
pub fn curse_card_pool_raw(state: &BattleState) -> &[CardId] {
    if !state.static_ctx.curse_card_pool.is_empty() {
        state.static_ctx.curse_card_pool.as_slice()
    } else {
        CARD_POOL_CURSE
    }
}

#[inline]
pub fn tarot_card_pool_raw(state: &BattleState) -> &[CardId] {
    if !state.static_ctx.tarot_card_pool.is_empty() {
        state.static_ctx.tarot_card_pool.as_slice()
    } else {
        TAROT_CARDS
    }
}

pub fn melody_card_pool_candidates(state: &BattleState) -> SmallVec<[CardId; 256]> {
    let mut pool = SmallVec::<[CardId; 256]>::new();
    if !state.static_ctx.card_generation_pools.is_empty()
        || !state.static_ctx.other_character_card_pool.is_empty()
    {
        pool.extend_from_slice(character_card_pool_raw(state));
        pool.extend_from_slice(&state.static_ctx.other_character_card_pool);
    } else {
        pool.extend_from_slice(CARD_POOL_FOOL);
    }
    pool
}

pub fn other_character_combat_pool(state: &BattleState) -> SmallVec<[CardId; 128]> {
    if !state.static_ctx.other_character_card_pool.is_empty() {
        return ordered_combat_candidates(state, &state.static_ctx.other_character_card_pool);
    }
    const ALL_CHARS: [CharacterId; 6] = [
        CharacterId::Ironclad,
        CharacterId::Silent,
        CharacterId::Regent,
        CharacterId::Necrobinder,
        CharacterId::Defect,
        CharacterId::Fool,
    ];
    let mut raw: SmallVec<[CardId; 256]> = SmallVec::new();
    for c in ALL_CHARS {
        if c != state.static_ctx.character_id {
            raw.extend_from_slice(sts2_types::character_card_pool(c));
        }
    }
    ordered_combat_candidates(state, &raw)
}

pub fn character_combat_pool(state: &BattleState) -> SmallVec<[CardId; 128]> {
    ordered_combat_candidates(state, character_card_pool_raw(state))
}

#[inline]
pub fn tarot_cards() -> &'static [CardId] {
    TAROT_CARDS
}

pub fn get_for_combat(
    state: &mut BattleState,
    pool: &[CardId],
    count: usize,
    rng_type: RunRngType,
) -> SmallVec<[CardId; 8]> {
    get_for_combat_filtered(state, pool, count, rng_type, |_| true)
}

pub fn get_for_combat_filtered<F>(
    state: &mut BattleState,
    pool: &[CardId],
    count: usize,
    rng_type: RunRngType,
    mut predicate: F,
) -> SmallVec<[CardId; 8]>
where
    F: FnMut(CardId) -> bool,
{
    let candidates: SmallVec<[CardId; 128]> = ordered_combat_candidates(state, pool)
        .into_iter()
        .filter(|&id| predicate(id))
        .collect();
    let mut out = SmallVec::with_capacity(count.min(16));
    if candidates.is_empty() {
        return out;
    }
    let rng = state.run_rngs.get_mut(rng_type);
    for _ in 0..count {
        if let Some(&picked) = rng.next_item(&candidates) {
            out.push(picked);
        }
    }
    out
}

pub fn get_distinct_for_combat(
    state: &mut BattleState,
    pool: &[CardId],
    count: usize,
    rng_type: RunRngType,
) -> SmallVec<[CardId; 8]> {
    get_distinct_for_combat_filtered(state, pool, count, rng_type, |_| true)
}

pub fn get_distinct_for_combat_filtered<F>(
    state: &mut BattleState,
    pool: &[CardId],
    count: usize,
    rng_type: RunRngType,
    mut predicate: F,
) -> SmallVec<[CardId; 8]>
where
    F: FnMut(CardId) -> bool,
{
    let mut candidates: SmallVec<[CardId; 128]> = ordered_combat_candidates(state, pool)
        .into_iter()
        .filter(|&id| predicate(id))
        .collect();
    if candidates.is_empty() || count == 0 {
        return SmallVec::new();
    }
    let rng = state.run_rngs.get_mut(rng_type);
    rng.unstable_shuffle(&mut candidates);
    let take = count.min(candidates.len());
    let mut out = SmallVec::with_capacity(take);
    out.extend_from_slice(&candidates[..take]);
    out
}

pub fn default_transformation_options(
    state: &BattleState,
    original: CardHandle,
    is_in_combat: bool,
) -> Result<Vec<CardId>, KernelError> {
    let card = state
        .card(original)
        .ok_or_else(|| KernelError::InvalidState("missing card to transform".to_owned()))?;
    let id = card.id;
    let meta = sts2_types::generated_card_meta(id);

    let pool: &[CardId] = if meta.card_type == CardType::Quest
        || matches!(
            meta.rarity,
            CardRarity::Event | CardRarity::Ancient | CardRarity::Token
        )
        || meta.flags.contains(CardStaticFlags::IS_COLORLESS)
    {
        colorless_card_pool_raw(state)
    } else if meta.rarity == CardRarity::Curse {
        curse_card_pool_raw(state)
    } else if meta.rarity == CardRarity::Status {
        CARD_POOL_STATUS
    } else if !state.static_ctx.card_generation_pools.is_empty()
        && (sts2_types::character_card_pool(state.static_ctx.character_id).contains(&id)
            || state.static_ctx.card_generation_pools.contains(&id))
    {
        state.static_ctx.card_generation_pools.as_slice()
    } else {
        let mut found: Option<&[CardId]> = None;
        for character in [
            CharacterId::Ironclad,
            CharacterId::Silent,
            CharacterId::Regent,
            CharacterId::Necrobinder,
            CharacterId::Defect,
            CharacterId::Fool,
        ] {
            let p = sts2_types::character_card_pool(character);
            if p.contains(&id) {
                found = Some(p);
                break;
            }
        }
        match found {
            Some(p) => p,
            None if !state.static_ctx.transformation_pools.is_empty() => {
                state.static_ctx.transformation_pools.as_slice()
            }
            None => {
                return Err(KernelError::InvalidState(format!(
                    "No original card pool is registered for {id:?}"
                )));
            }
        }
    };

    let mut out = Vec::new();
    for &cand in pool {
        if cand == id {
            continue;
        }
        let cmeta = sts2_types::generated_card_meta(cand);
        if !matches!(meta.rarity, CardRarity::Status | CardRarity::Curse)
            && !matches!(
                cmeta.rarity,
                CardRarity::Common | CardRarity::Uncommon | CardRarity::Rare
            )
        {
            continue;
        }
        if is_in_combat && !cmeta.flags.contains(CardStaticFlags::CAN_BE_GENERATED_IN_COMBAT) {
            continue;
        }
        if !is_card_allowed_in_run(state, cand) {
            continue;
        }
        out.push(cand);
    }

    if out.is_empty() {
        return Err(KernelError::InvalidState(
            "All transformation options provided are invalid.".to_owned(),
        ));
    }
    Ok(out)
}
