use crate::error::KernelError;
use crate::state::{
    BattleState, CardHandle, IndexedTransformPool, RootPoolCacheInner, RootStaticContext,
};
use smallvec::SmallVec;
use std::sync::OnceLock;
use sts2_types::{
    CardId, CardMeta, CardRarity, CardStaticFlags, CardType, CharacterId, GameMode, RunRngType,
    CARD_POOL_COLORLESS, CARD_POOL_CURSE, CARD_POOL_FOOL, CARD_POOL_STATUS, TAROT_CARDS,
};

const TRANSFORM_OWNER_ORDER: [CharacterId; 6] = [
    CharacterId::Ironclad,
    CharacterId::Silent,
    CharacterId::Regent,
    CharacterId::Necrobinder,
    CharacterId::Defect,
    CharacterId::Fool,
];

static CARD_OWNER_LUT: OnceLock<[(Option<CharacterId>, u8); CardId::COUNT]> = OnceLock::new();
// [pool_idx 0..9][is_in_combat][is_blitz][allow_special_rarity]
static VANILLA_TRANSFORM_POOLS: OnceLock<[[[[IndexedTransformPool; 2]; 2]; 2]; 9]> =
    OnceLock::new();

#[inline]
fn card_owner_lut() -> &'static [(Option<CharacterId>, u8); CardId::COUNT] {
    CARD_OWNER_LUT.get_or_init(|| {
        let mut lut = [(None, 0u8); CardId::COUNT];
        for character in TRANSFORM_OWNER_ORDER {
            let bit = 1u8 << (character as u8);
            for &cid in sts2_types::character_card_pool(character) {
                let slot = &mut lut[cid as usize];
                if slot.0.is_none() {
                    slot.0 = Some(character);
                }
                slot.1 |= bit;
            }
        }
        lut
    })
}

#[inline(always)]
fn filter_transform_candidate(
    cand: CardId,
    allow_special_rarity: bool,
    is_in_combat: bool,
    is_blitz: bool,
) -> bool {
    let cmeta = sts2_types::generated_card_meta(cand);
    if !allow_special_rarity
        && !matches!(
            cmeta.rarity,
            CardRarity::Common | CardRarity::Uncommon | CardRarity::Rare
        )
    {
        return false;
    }
    if is_in_combat && !cmeta.flags.contains(CardStaticFlags::CAN_BE_GENERATED_IN_COMBAT) {
        return false;
    }
    if cmeta.flags.contains(CardStaticFlags::IS_MULTIPLAYER_ONLY) {
        return false;
    }
    if is_blitz && !cmeta.flags.contains(CardStaticFlags::IS_ALLOWED_IN_BLITZ) {
        return false;
    }
    true
}

fn vanilla_transform_pools() -> &'static [[[[IndexedTransformPool; 2]; 2]; 2]; 9] {
    VANILLA_TRANSFORM_POOLS.get_or_init(|| {
        const RAW_POOLS: [&[CardId]; 9] = [
            sts2_types::CARD_POOL_DEFECT,
            sts2_types::CARD_POOL_FOOL,
            sts2_types::CARD_POOL_IRONCLAD,
            sts2_types::CARD_POOL_NECROBINDER,
            sts2_types::CARD_POOL_REGENT,
            sts2_types::CARD_POOL_SILENT,
            CARD_POOL_COLORLESS,
            CARD_POOL_CURSE,
            CARD_POOL_STATUS,
        ];
        std::array::from_fn(|pool_idx| {
            let raw = RAW_POOLS[pool_idx];
            std::array::from_fn(|in_combat_i| {
                let in_combat = in_combat_i != 0;
                std::array::from_fn(|is_blitz_i| {
                    let is_blitz = is_blitz_i != 0;
                    std::array::from_fn(|allow_spec_i| {
                        let allow_spec = allow_spec_i != 0;
                        IndexedTransformPool::from_vec(
                            raw.iter()
                                .copied()
                                .filter(|&c| {
                                    filter_transform_candidate(c, allow_spec, in_combat, is_blitz)
                                })
                                .collect(),
                        )
                    })
                })
            })
        })
    })
}

#[inline]
fn filter_transform_pool(
    raw: &[CardId],
    is_blitz: bool,
) -> [[IndexedTransformPool; 2]; 2] {
    std::array::from_fn(|in_combat_i| {
        let in_combat = in_combat_i != 0;
        std::array::from_fn(|allow_spec_i| {
            let allow_spec = allow_spec_i != 0;
            IndexedTransformPool::from_vec(
                raw.iter()
                    .copied()
                    .filter(|&c| filter_transform_candidate(c, allow_spec, in_combat, is_blitz))
                    .collect(),
            )
        })
    })
}

#[inline]
fn ordered_combat_candidates_for_mode(game_mode: GameMode, pool: &[CardId]) -> Vec<CardId> {
    let mut seen_bits = [0u64; (CardId::COUNT + 63) / 64];
    let mut out = Vec::with_capacity(pool.len().min(128));
    let is_blitz = game_mode == GameMode::Blitz;
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
            && !meta.flags.contains(CardStaticFlags::IS_MULTIPLAYER_ONLY)
            && (!is_blitz || meta.flags.contains(CardStaticFlags::IS_ALLOWED_IN_BLITZ))
        {
            out.push(id);
        }
    }
    out
}

#[inline]
fn root_pool_cache(ctx: &RootStaticContext) -> &RootPoolCacheInner {
    ctx.pool_cache.0.get_or_init(|| {
        let is_blitz = ctx.game_mode == GameMode::Blitz;
        let mut card_gen_pool_bits = [0u64; (CardId::COUNT + 63) / 64];
        for &id in &ctx.card_generation_pools {
            let idx = id as usize;
            card_gen_pool_bits[idx >> 6] |= 1u64 << (idx & 63);
        }
        let custom_card_gen_transform = if ctx.card_generation_pools.is_empty() {
            Default::default()
        } else {
            filter_transform_pool(&ctx.card_generation_pools, is_blitz)
        };
        let custom_colorless_transform = if ctx.colorless_card_pool.is_empty() {
            Default::default()
        } else {
            filter_transform_pool(&ctx.colorless_card_pool, is_blitz)
        };
        let custom_curse_transform = if ctx.curse_card_pool.is_empty() {
            Default::default()
        } else {
            filter_transform_pool(&ctx.curse_card_pool, is_blitz)
        };
        let custom_transformation_pool = if ctx.transformation_pools.is_empty() {
            Default::default()
        } else {
            filter_transform_pool(&ctx.transformation_pools, is_blitz)
        };
        let char_raw = if !ctx.card_generation_pools.is_empty() {
            ctx.card_generation_pools.as_slice()
        } else {
            sts2_types::character_card_pool(ctx.character_id)
        };
        let character_combat_pool = ordered_combat_candidates_for_mode(ctx.game_mode, char_raw);
        let other_character_combat_pool = if !ctx.other_character_card_pool.is_empty() {
            ordered_combat_candidates_for_mode(ctx.game_mode, &ctx.other_character_card_pool)
        } else {
            const ALL_CHARS: [CharacterId; 6] = [
                CharacterId::Ironclad,
                CharacterId::Silent,
                CharacterId::Regent,
                CharacterId::Necrobinder,
                CharacterId::Defect,
                CharacterId::Fool,
            ];
            let mut raw = SmallVec::<[CardId; 256]>::new();
            for c in ALL_CHARS {
                if c != ctx.character_id {
                    raw.extend_from_slice(sts2_types::character_card_pool(c));
                }
            }
            ordered_combat_candidates_for_mode(ctx.game_mode, &raw)
        };
        let colorless_raw = if !ctx.colorless_card_pool.is_empty() {
            ctx.colorless_card_pool.as_slice()
        } else {
            CARD_POOL_COLORLESS
        };
        let colorless_combat_pool = ordered_combat_candidates_for_mode(ctx.game_mode, colorless_raw);
        Box::new(RootPoolCacheInner {
            card_gen_pool_bits,
            custom_card_gen_transform,
            custom_colorless_transform,
            custom_curse_transform,
            custom_transformation_pool,
            character_combat_pool,
            other_character_combat_pool,
            colorless_combat_pool,
        })
    })
}

#[inline]
fn resolve_filtered_transform_pool<'a>(
    ctx: &'a RootStaticContext,
    id: CardId,
    meta: &'static CardMeta,
    is_in_combat: bool,
    allow_special_rarity: bool,
) -> Result<&'a IndexedTransformPool, KernelError> {
    let in_c = usize::from(is_in_combat);
    let spec = usize::from(allow_special_rarity);
    let is_blitz = usize::from(ctx.game_mode == GameMode::Blitz);
    if meta.card_type == CardType::Quest
        || matches!(
            meta.rarity,
            CardRarity::Event | CardRarity::Ancient | CardRarity::Token
        )
        || meta.flags.contains(CardStaticFlags::IS_COLORLESS)
    {
        if ctx.colorless_card_pool.is_empty() {
            Ok(&vanilla_transform_pools()[6][in_c][is_blitz][spec])
        } else {
            Ok(&root_pool_cache(ctx).custom_colorless_transform[in_c][spec])
        }
    } else if meta.rarity == CardRarity::Curse {
        if ctx.curse_card_pool.is_empty() {
            Ok(&vanilla_transform_pools()[7][in_c][is_blitz][spec])
        } else {
            Ok(&root_pool_cache(ctx).custom_curse_transform[in_c][spec])
        }
    } else if meta.rarity == CardRarity::Status {
        Ok(&vanilla_transform_pools()[8][in_c][is_blitz][spec])
    } else {
        let owner_entry = card_owner_lut()[id as usize];
        let char_bit = 1u8 << (ctx.character_id as u8);
        if !ctx.card_generation_pools.is_empty() {
            let cache = root_pool_cache(ctx);
            let id_idx = id as usize;
            let in_gen_pool = (owner_entry.1 & char_bit) != 0
                || ((cache.card_gen_pool_bits[id_idx >> 6] >> (id_idx & 63)) & 1) != 0;
            if in_gen_pool {
                return Ok(&cache.custom_card_gen_transform[in_c][spec]);
            }
        }
        if let Some(owner_char) = owner_entry.0 {
            Ok(&vanilla_transform_pools()[owner_char as usize][in_c][is_blitz][spec])
        } else if !ctx.transformation_pools.is_empty() {
            Ok(&root_pool_cache(ctx).custom_transformation_pool[in_c][spec])
        } else {
            Err(KernelError::InvalidState(format!(
                "No original card pool is registered for {id:?}"
            )))
        }
    }
}

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

#[inline]
fn ordered_combat_candidates_slice<'a>(
    state: &'a BattleState,
    pool: &'a [CardId],
    scratch_out: &'a mut SmallVec<[CardId; 128]>,
) -> &'a [CardId] {
    let ctx = &state.static_ctx;
    let char_raw = character_card_pool_raw(state);
    if pool.as_ptr() == char_raw.as_ptr() && pool.len() == char_raw.len() {
        return &root_pool_cache(ctx).character_combat_pool;
    }
    let colorless_raw = colorless_card_pool_raw(state);
    if pool.as_ptr() == colorless_raw.as_ptr() && pool.len() == colorless_raw.len() {
        return &root_pool_cache(ctx).colorless_combat_pool;
    }
    if !ctx.other_character_card_pool.is_empty()
        && pool.as_ptr() == ctx.other_character_card_pool.as_ptr()
        && pool.len() == ctx.other_character_card_pool.len()
    {
        return &root_pool_cache(ctx).other_character_combat_pool;
    }
    if let Some(cache) = ctx.pool_cache.0.get() {
        if (pool.as_ptr() == cache.character_combat_pool.as_ptr()
            && pool.len() == cache.character_combat_pool.len())
            || (pool.as_ptr() == cache.other_character_combat_pool.as_ptr()
                && pool.len() == cache.other_character_combat_pool.len())
            || (pool.as_ptr() == cache.colorless_combat_pool.as_ptr()
                && pool.len() == cache.colorless_combat_pool.len())
        {
            return pool;
        }
    }
    *scratch_out = ordered_combat_candidates(state, pool);
    scratch_out.as_slice()
}

pub fn other_character_combat_pool(state: &BattleState) -> SmallVec<[CardId; 128]> {
    SmallVec::from_slice(&root_pool_cache(&state.static_ctx).other_character_combat_pool)
}

pub fn character_combat_pool(state: &BattleState) -> SmallVec<[CardId; 128]> {
    SmallVec::from_slice(&root_pool_cache(&state.static_ctx).character_combat_pool)
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
    let mut scratch = SmallVec::new();
    let base = ordered_combat_candidates_slice(state, pool, &mut scratch);
    let mut candidates: SmallVec<[CardId; 128]> = SmallVec::with_capacity(base.len().min(128));
    for &id in base {
        if predicate(id) {
            candidates.push(id);
        }
    }
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
    let mut scratch = SmallVec::new();
    let base = ordered_combat_candidates_slice(state, pool, &mut scratch);
    let mut candidates: SmallVec<[CardId; 128]> = SmallVec::with_capacity(base.len().min(128));
    for &id in base {
        if predicate(id) {
            candidates.push(id);
        }
    }
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
    let allow_special_rarity = matches!(meta.rarity, CardRarity::Status | CardRarity::Curse);

    let pool = resolve_filtered_transform_pool(
        &state.static_ctx,
        id,
        meta,
        is_in_combat,
        allow_special_rarity,
    )?;
    let out: Vec<CardId> = pool
        .cards
        .iter()
        .copied()
        .filter(|&cand| cand != id)
        .collect();

    if out.is_empty() {
        return Err(KernelError::InvalidState(
            "All transformation options provided are invalid.".to_owned(),
        ));
    }
    Ok(out)
}

pub fn pick_random_transformation_target(
    state: &mut BattleState,
    original: CardHandle,
    is_in_combat: bool,
    rng_type: RunRngType,
) -> Result<CardId, KernelError> {
    let card = state
        .card(original)
        .ok_or_else(|| KernelError::InvalidState("missing card to transform".to_owned()))?;
    let id = card.id;
    let meta = sts2_types::generated_card_meta(id);
    let allow_special_rarity = matches!(meta.rarity, CardRarity::Status | CardRarity::Curse);

    let pool = resolve_filtered_transform_pool(
        &state.static_ctx,
        id,
        meta,
        is_in_combat,
        allow_special_rarity,
    )?;
    let filtered = pool.cards.as_slice();
    let lut_val = pool.lookup[id as usize];
    let excluded = if lut_val == 0 {
        0
    } else if lut_val != u16::MAX {
        1
    } else {
        filtered.iter().filter(|&&c| c == id).count()
    };

    let valid_len = filtered.len() - excluded;
    if valid_len == 0 {
        return Err(KernelError::InvalidState(
            "All transformation options provided are invalid.".to_owned(),
        ));
    }

    #[cfg(debug_assertions)]
    let mut dbg_rng = *state.run_rngs.get(rng_type);

    let target_idx = state
        .run_rngs
        .get_mut(rng_type)
        .next_int_range(0, valid_len as i32)
        .map_err(|_| KernelError::InvalidState("No valid transformation target.".to_owned()))?
        as usize;

    let chosen = if excluded == 0 {
        filtered[target_idx]
    } else if excluded == 1 {
        let pos = (lut_val - 1) as usize;
        debug_assert_eq!(filtered[pos], id);
        if target_idx < pos {
            filtered[target_idx]
        } else {
            filtered[target_idx + 1]
        }
    } else {
        let mut rem = target_idx;
        let mut picked = None;
        for &c in filtered {
            if c == id {
                continue;
            }
            if rem == 0 {
                picked = Some(c);
                break;
            }
            rem -= 1;
        }
        picked.ok_or_else(|| KernelError::InvalidState("No valid transformation target.".to_owned()))?
    };

    #[cfg(debug_assertions)]
    {
        let ref_opts = default_transformation_options(state, original, is_in_combat)?;
        let expected = *dbg_rng
            .next_item(&ref_opts)
            .expect("ref_opts non-empty");
        debug_assert_eq!(chosen, expected);
    }

    Ok(chosen)
}
