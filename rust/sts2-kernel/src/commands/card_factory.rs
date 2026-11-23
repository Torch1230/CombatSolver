use crate::error::KernelError;
use crate::state::{BattleState, CardHandle};
use std::collections::HashSet;
use sts2_types::{
    CardId, CardRarity, CardStaticFlags, CardType, CharacterId, GameMode, RunRngType,
    CARD_POOL_COLORLESS, CARD_POOL_CURSE, CARD_POOL_STATUS, TAROT_CARDS,
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

pub fn ordered_combat_candidates(state: &BattleState, pool: &[CardId]) -> Vec<CardId> {
    let mut seen = HashSet::with_capacity(pool.len());
    let mut out = Vec::with_capacity(pool.len());
    for &id in pool {
        if !seen.insert(id) {
            continue;
        }
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

pub fn character_combat_pool(state: &BattleState) -> Vec<CardId> {
    let raw = if !state.static_ctx.card_generation_pools.is_empty() {
        state.static_ctx.card_generation_pools.as_slice()
    } else {
        sts2_types::character_card_pool(state.static_ctx.character_id)
    };
    ordered_combat_candidates(state, raw)
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
) -> Vec<CardId> {
    get_for_combat_filtered(state, pool, count, rng_type, |_| true)
}

pub fn get_for_combat_filtered<F>(
    state: &mut BattleState,
    pool: &[CardId],
    count: usize,
    rng_type: RunRngType,
    mut predicate: F,
) -> Vec<CardId>
where
    F: FnMut(CardId) -> bool,
{
    let candidates: Vec<CardId> = ordered_combat_candidates(state, pool)
        .into_iter()
        .filter(|&id| predicate(id))
        .collect();
    let mut out = Vec::with_capacity(count);
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
) -> Vec<CardId> {
    get_distinct_for_combat_filtered(state, pool, count, rng_type, |_| true)
}

pub fn get_distinct_for_combat_filtered<F>(
    state: &mut BattleState,
    pool: &[CardId],
    count: usize,
    rng_type: RunRngType,
    mut predicate: F,
) -> Vec<CardId>
where
    F: FnMut(CardId) -> bool,
{
    let mut candidates: Vec<CardId> = ordered_combat_candidates(state, pool)
        .into_iter()
        .filter(|&id| predicate(id))
        .collect();
    if candidates.is_empty() || count == 0 {
        return Vec::new();
    }
    let rng = state.run_rngs.get_mut(rng_type);
    rng.unstable_shuffle(&mut candidates);
    candidates.truncate(count);
    candidates
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
        CARD_POOL_COLORLESS
    } else if meta.rarity == CardRarity::Curse {
        CARD_POOL_CURSE
    } else if meta.rarity == CardRarity::Status {
        CARD_POOL_STATUS
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
