pub mod cards;
pub mod dread_blitz;
pub mod monsters;
pub mod orbs_enchantments_afflictions;
pub mod potions;
pub mod powers;
pub mod relics;
pub mod rollout_profiles;

use smallvec::SmallVec;
use std::sync::LazyLock;
use sts2_kernel::commands::{block, damage, gold, power};
use sts2_kernel::content_dispatch::{CardPlayContext, CardPlayOutcome};
use sts2_kernel::hooks::HpLossPhase;
use sts2_kernel::{
    BattleState, CachedHookListener, CardHandle, CardRolloutProfile, CategoryHookMasks,
    ContentDispatcher, CreatureHandle, FieldValue, HookArgs, HookListener, HookMaskTableView,
    HookResult, KernelError, ModifyDamageHookType, PowerInst,
};
use sts2_types::{
    AfflictionId, CardId, CardMeta, CardPilePosition, CardRarity, CardStaticFlags, CardTags,
    CardType, CombatSide, Dec, EnchantmentId, HookMask, HookSlot, MonsterId, MonsterMeta, OrbId,
    OrbMeta, PileType, PotionId, PotionMeta, PowerId, PowerMeta, RelicId, RelicMeta, TargetType,
    ValueProp,
};

struct ContentMetaTables {
    card_metas: [CardMeta; CardId::COUNT],
    power_metas: [PowerMeta; PowerId::COUNT],
    relic_metas: [&'static RelicMeta; RelicId::COUNT],
    potion_metas: [&'static PotionMeta; PotionId::COUNT],
    monster_metas: [&'static MonsterMeta; MonsterId::COUNT],
    orb_metas: [&'static OrbMeta; OrbId::COUNT],
    cards: [HookMask; CardId::COUNT],
    powers: [HookMask; PowerId::COUNT],
    relics: [HookMask; RelicId::COUNT],
    potions: [HookMask; PotionId::COUNT],
    monsters: [HookMask; MonsterId::COUNT],
    orbs: [HookMask; OrbId::COUNT],
    enchantments: [HookMask; EnchantmentId::COUNT],
    afflictions: [HookMask; AfflictionId::COUNT],
    categories: CategoryHookMasks,
    card_rollout_profiles: [CardRolloutProfile; CardId::COUNT],
    card_has_hook_bits: [u64; (CardId::COUNT + 63) / 64],
    power_has_hook_bits: [u64; (PowerId::COUNT + 63) / 64],
    relic_has_hook_bits: [u64; (RelicId::COUNT + 63) / 64],
    monster_has_hook_bits: [u64; (MonsterId::COUNT + 63) / 64],
    card_has_custom_playability_bits: [u64; (CardId::COUNT + 63) / 64],
    card_is_status_or_curse_bits: [u64; (CardId::COUNT + 63) / 64],
    card_generic_choice_quality: [(i16, u8); CardId::COUNT],
    card_draw_sort_keys: [u32; CardId::COUNT],
}

static CONTENT_TABLES: LazyLock<ContentMetaTables> = LazyLock::new(|| {
    let card_metas: [CardMeta; CardId::COUNT] = std::array::from_fn(|idx| {
        let id: CardId = unsafe { core::mem::transmute(idx as u16) };
        *cards::card_meta(id).expect("valid card meta")
    });
    let mut cards = [HookMask::EMPTY; CardId::COUNT];
    let mut card_rollout_profiles = [CardRolloutProfile::default(); CardId::COUNT];
    let mut card_has_hook_bits = [0u64; (CardId::COUNT + 63) / 64];
    let mut card_has_custom_playability_bits = [0u64; (CardId::COUNT + 63) / 64];
    let mut card_is_status_or_curse_bits = [0u64; (CardId::COUNT + 63) / 64];
    let mut card_generic_choice_quality = [(0i16, 0u8); CardId::COUNT];
    let mut card_draw_sort_keys = [0u32; CardId::COUNT];
    let mut all_cards = HookMask::EMPTY;
    for (idx, meta) in card_metas.iter().enumerate() {
        let mask = meta.hook_mask;
        cards[idx] = mask;
        if !mask.is_empty() {
            card_has_hook_bits[idx >> 6] |= 1u64 << (idx & 63);
        }
        let id: CardId = unsafe { core::mem::transmute(idx as u16) };
        if matches!(id, CardId::Clash | CardId::GrandFinale | CardId::HighFive) {
            card_has_custom_playability_bits[idx >> 6] |= 1u64 << (idx & 63);
        }
        let is_sc = matches!(meta.card_type, CardType::Status | CardType::Curse);
        if is_sc {
            card_is_status_or_curse_bits[idx >> 6] |= 1u64 << (idx & 63);
        }
        let prof = rollout_profiles::build_card_rollout_profile(id, meta);
        card_rollout_profiles[idx] = prof;
        let is_sd = meta.base_tags.intersects(CardTags::STRIKE | CardTags::DEFEND);
        let is_x = meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST);
        let qflags = u8::from(is_sc) | (u8::from(is_sd) << 1) | (u8::from(is_x) << 2);
        let base_q = if is_sc {
            -400i16
        } else if is_sd {
            60i16
        } else {
            let type_q = match meta.card_type {
                CardType::Power => 280,
                CardType::Skill => 200,
                CardType::Attack => 190,
                _ => 120,
            };
            let rar_q = match meta.rarity {
                CardRarity::Rare | CardRarity::Ancient => 90,
                CardRarity::Uncommon => 50,
                CardRarity::Common | CardRarity::Event => 20,
                _ => 10,
            };
            (type_q + rar_q + ((prof.setup_weight as i32) * 4).min(140)) as i16
        };
        card_generic_choice_quality[idx] = (base_q, qflags);
        card_draw_sort_keys[idx] =
            ((meta.rarity as u32) << 16) | u32::from(id.model_entry_sort_rank());
        all_cards = all_cards.union(mask);
    }

    let power_metas: [PowerMeta; PowerId::COUNT] = std::array::from_fn(|idx| {
        let id: PowerId = unsafe { core::mem::transmute(idx as u16) };
        *powers::power_meta(id).expect("valid power meta")
    });
    let mut powers = [HookMask::EMPTY; PowerId::COUNT];
    let mut power_has_hook_bits = [0u64; (PowerId::COUNT + 63) / 64];
    let mut all_powers = HookMask::EMPTY;
    for (idx, meta) in power_metas.iter().enumerate() {
        let mask = meta.hook_mask;
        powers[idx] = mask;
        if !mask.is_empty() {
            power_has_hook_bits[idx >> 6] |= 1u64 << (idx & 63);
        }
        all_powers = all_powers.union(mask);
    }

    let relic_metas: [&'static RelicMeta; RelicId::COUNT] = std::array::from_fn(|idx| {
        let id: RelicId = unsafe { core::mem::transmute(idx as u16) };
        relics::relic_meta(id).expect("valid relic meta")
    });
    let mut relics = [HookMask::EMPTY; RelicId::COUNT];
    let mut relic_has_hook_bits = [0u64; (RelicId::COUNT + 63) / 64];
    let mut all_relics = HookMask::EMPTY;
    for (idx, meta) in relic_metas.iter().enumerate() {
        let mask = meta.hook_mask;
        relics[idx] = mask;
        if !mask.is_empty() {
            relic_has_hook_bits[idx >> 6] |= 1u64 << (idx & 63);
        }
        all_relics = all_relics.union(mask);
    }

    let potion_metas: [&'static PotionMeta; PotionId::COUNT] = std::array::from_fn(|idx| {
        let id: PotionId = unsafe { core::mem::transmute(idx as u16) };
        potions::potion_meta(id).expect("valid potion meta")
    });
    let mut potions = [HookMask::EMPTY; PotionId::COUNT];
    let mut all_potions = HookMask::EMPTY;
    for (idx, meta) in potion_metas.iter().enumerate() {
        let mask = meta.hook_mask;
        potions[idx] = mask;
        all_potions = all_potions.union(mask);
    }

    let monster_metas: [&'static MonsterMeta; MonsterId::COUNT] = std::array::from_fn(|idx| {
        let id: MonsterId = unsafe { core::mem::transmute(idx as u16) };
        monsters::monster_meta(id).expect("valid monster meta")
    });
    let mut monsters = [HookMask::EMPTY; MonsterId::COUNT];
    let mut monster_has_hook_bits = [0u64; (MonsterId::COUNT + 63) / 64];
    let mut all_monsters = HookMask::EMPTY;
    for (idx, meta) in monster_metas.iter().enumerate() {
        let mask = meta.hook_mask;
        monsters[idx] = mask;
        if !mask.is_empty() {
            monster_has_hook_bits[idx >> 6] |= 1u64 << (idx & 63);
        }
        all_monsters = all_monsters.union(mask);
    }

    let orb_metas: [&'static OrbMeta; OrbId::COUNT] = std::array::from_fn(|idx| {
        let id: OrbId = unsafe { core::mem::transmute(idx as u8) };
        orbs_enchantments_afflictions::orb_meta(id).expect("valid orb meta")
    });
    let mut orbs = [HookMask::EMPTY; OrbId::COUNT];
    let mut all_orbs = HookMask::EMPTY;
    for (idx, meta) in orb_metas.iter().enumerate() {
        let mask = meta.hook_mask;
        orbs[idx] = mask;
        all_orbs = all_orbs.union(mask);
    }

    let mut enchantments = [HookMask::EMPTY; EnchantmentId::COUNT];
    let mut all_enchantments = HookMask::EMPTY;
    let mut idx = 0usize;
    while idx < EnchantmentId::COUNT {
        let id: EnchantmentId = unsafe { core::mem::transmute(idx as u16) };
        let mask = orbs_enchantments_afflictions::enchantment_hook_mask(id)
            .expect("valid enchantment mask");
        enchantments[idx] = mask;
        all_enchantments = all_enchantments.union(mask);
        idx += 1;
    }

    let mut afflictions = [HookMask::EMPTY; AfflictionId::COUNT];
    let mut all_afflictions = HookMask::EMPTY;
    let mut idx = 0usize;
    while idx < AfflictionId::COUNT {
        let id: AfflictionId = unsafe { core::mem::transmute(idx as u16) };
        let mask = orbs_enchantments_afflictions::affliction_hook_mask(id)
            .expect("valid affliction mask");
        afflictions[idx] = mask;
        all_afflictions = all_afflictions.union(mask);
        idx += 1;
    }

    ContentMetaTables {
        card_metas,
        power_metas,
        relic_metas,
        potion_metas,
        monster_metas,
        orb_metas,
        cards,
        powers,
        relics,
        potions,
        monsters,
        orbs,
        enchantments,
        afflictions,
        categories: CategoryHookMasks {
            all_cards,
            deck_cards: HookMask::EMPTY.with(HookSlot::AfterCombatEnd),
            all_enchantments,
            all_afflictions,
            all_potions,
            all_orbs,
            all_relics,
            all_powers,
            all_monsters,
        },
        card_rollout_profiles,
        card_has_hook_bits,
        power_has_hook_bits,
        relic_has_hook_bits,
        monster_has_hook_bits,
        card_has_custom_playability_bits,
        card_is_status_or_curse_bits,
        card_generic_choice_quality,
        card_draw_sort_keys,
    }
});

#[inline(always)]
pub(crate) fn card_draw_sort_key(id: CardId) -> u32 {
    CONTENT_TABLES.card_draw_sort_keys[id as usize]
}

static HOOK_MASK_TABLE_VIEW: LazyLock<HookMaskTableView> = LazyLock::new(|| HookMaskTableView {
    categories: CONTENT_TABLES.categories,
    cards: &CONTENT_TABLES.cards,
    powers: &CONTENT_TABLES.powers,
    relics: &CONTENT_TABLES.relics,
    potions: &CONTENT_TABLES.potions,
    monsters: &CONTENT_TABLES.monsters,
    orbs: &CONTENT_TABLES.orbs,
    enchantments: &CONTENT_TABLES.enchantments,
    afflictions: &CONTENT_TABLES.afflictions,
    card_metas: &CONTENT_TABLES.card_metas,
    power_metas: &CONTENT_TABLES.power_metas,
    card_rollout_profiles: &CONTENT_TABLES.card_rollout_profiles,
    card_has_hook_bits: &CONTENT_TABLES.card_has_hook_bits,
    power_has_hook_bits: &CONTENT_TABLES.power_has_hook_bits,
    relic_has_hook_bits: &CONTENT_TABLES.relic_has_hook_bits,
    monster_has_hook_bits: &CONTENT_TABLES.monster_has_hook_bits,
    card_has_custom_playability_bits: &CONTENT_TABLES.card_has_custom_playability_bits,
    card_is_status_or_curse_bits: &CONTENT_TABLES.card_is_status_or_curse_bits,
    card_generic_choice_quality: &CONTENT_TABLES.card_generic_choice_quality,
    afflictions_self_card_entered_only: CONTENT_TABLES.categories.all_afflictions
        == HookMask::EMPTY.with(HookSlot::AfterCardEnteredCombat),
});

/// Authoritative Wave 0 content dispatcher; Wave 1/2 modules plug directly into the per-category routers.
#[derive(Clone, Copy, Debug, Default)]
pub struct Sts2Content;

#[cold]
#[inline(never)]
fn resolve_power_cold_ref<'a>(
    state: &'a BattleState,
    creature: CreatureHandle,
    power_handle: u32,
) -> Option<(usize, &'a PowerInst, CombatSide)> {
    let c = state.creature(creature)?;
    if c.is_detached
        || !state.contains_creature(creature)
        || (c.is_player && !state.player.is_active_for_hooks)
    {
        return None;
    }
    for (idx, p) in c.powers.iter().enumerate() {
        if p.handle == power_handle {
            return Some((idx, p, c.side));
        }
    }
    None
}

#[inline(always)]
fn resolve_cached_power_ref<'a>(
    state: &'a BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    creature_idx: u16,
    power_idx: u16,
) -> Option<(usize, &'a PowerInst, CombatSide)> {
    if creature_idx != u16::MAX && power_idx != u16::MAX {
        if let Some(c) = state.creatures.get(creature_idx as usize) {
            if let Some(power) = c.powers.get(power_idx as usize) {
                #[cfg(debug_assertions)]
                {
                    debug_assert_eq!(c.handle, creature);
                    debug_assert_eq!(power.handle, power_handle);
                    debug_assert!(!c.is_detached && state.contains_creature(creature));
                    debug_assert!(!c.is_player || state.player.is_active_for_hooks);
                }
                return Some((power_idx as usize, power, c.side));
            }
        }
    }
    resolve_power_cold_ref(state, creature, power_handle)
}

struct ContentListenerBuf {
    stack: [std::mem::MaybeUninit<CachedHookListener>; 16],
    len: usize,
    spill: Option<Vec<CachedHookListener>>,
}

impl ContentListenerBuf {
    #[inline(always)]
    fn new() -> Self {
        Self {
            stack: [const { std::mem::MaybeUninit::<CachedHookListener>::uninit() }; 16],
            len: 0,
            spill: None,
        }
    }

    #[inline(always)]
    fn push(&mut self, l: CachedHookListener) {
        if let Some(v) = self.spill.as_mut() {
            v.push(l);
        } else if self.len < 16 {
            self.stack[self.len].write(l);
            self.len += 1;
        } else {
            let mut v = Vec::with_capacity(32);
            let init_slice = unsafe {
                std::slice::from_raw_parts(self.stack.as_ptr().cast::<CachedHookListener>(), 16)
            };
            v.extend_from_slice(init_slice);
            v.push(l);
            self.spill = Some(v);
        }
    }

    #[inline(always)]
    fn as_slice(&self) -> &[CachedHookListener] {
        match &self.spill {
            Some(v) => v.as_slice(),
            None => unsafe {
                std::slice::from_raw_parts(
                    self.stack.as_ptr().cast::<CachedHookListener>(),
                    self.len,
                )
            },
        }
    }
}

impl Sts2Content {
    #[inline(always)]
    fn dispatch_after_card_played_listener_fast(
        &self,
        state: &mut BattleState,
        listener: CachedHookListener,
        cp: &CardPlayContext,
        played_card_type: Option<CardType>,
        args: &HookArgs<'_>,
    ) -> Result<(), KernelError> {
        if let CachedHookListener::Relic(ridx_u16) = listener {
            let ridx = ridx_u16 as usize;
            let relic = state
                .player
                .relics
                .get(ridx)
                .ok_or_else(|| KernelError::InvalidState("missing relic".to_owned()))?;
            if relic.is_melted {
                return Ok(());
            }
            let pc = state.player.creature_handle;
            match relic.id {
                RelicId::Kunai => {
                    if played_card_type == Some(CardType::Attack) {
                        state.mark_relic_dirty(ridx);
                        let next = state.player.relics[ridx]
                            .private_fields
                            .inc_int("Kunai._attacksThisTurn");
                        if next % 3 == 0 {
                            power::apply(
                                state,
                                self,
                                PowerId::DexterityPower,
                                pc,
                                Dec::ONE,
                                Some(pc),
                                None,
                            )?;
                        }
                    }
                    return Ok(());
                }
                RelicId::Shuriken => {
                    if played_card_type == Some(CardType::Attack) {
                        state.mark_relic_dirty(ridx);
                        let next = state.player.relics[ridx]
                            .private_fields
                            .inc_int("Shuriken._attacksThisTurn");
                        if next % 3 == 0 {
                            power::apply(
                                state,
                                self,
                                PowerId::StrengthPower,
                                pc,
                                Dec::ONE,
                                Some(pc),
                                None,
                            )?;
                        }
                    }
                    return Ok(());
                }
                RelicId::OrnamentalFan => {
                    if played_card_type == Some(CardType::Attack) {
                        state.mark_relic_dirty(ridx);
                        let next = state.player.relics[ridx]
                            .private_fields
                            .inc_int("OrnamentalFan._attacksThisTurn");
                        if next % 3 == 0 {
                            block::gain_block(
                                state,
                                self,
                                pc,
                                Dec::from_i32(4),
                                ValueProp::UNPOWERED,
                                None,
                                Some(*cp),
                            )?;
                        }
                    }
                    return Ok(());
                }
                RelicId::Nunchaku => {
                    if played_card_type == Some(CardType::Attack) {
                        state.mark_relic_dirty(ridx);
                        let next = state.player.relics[ridx]
                            .private_fields
                            .inc_int("Nunchaku._attacksPlayed");
                        if next % 10 == 0 {
                            gold::gain_energy(state, self, Dec::ONE)?;
                        }
                    }
                    return Ok(());
                }
                RelicId::LetterOpener => {
                    if played_card_type == Some(CardType::Skill) {
                        state.mark_relic_dirty(ridx);
                        let skills = state.player.relics[ridx]
                            .private_fields
                            .inc_int("LetterOpener._skillsThisTurn");
                        if skills % 3 == 0 {
                            let targets = relics::hittable_enemies_fast(state, self)?;
                            damage::damage_targets_void(
                                state,
                                self,
                                &targets,
                                Dec::from_i32(5),
                                ValueProp::UNPOWERED,
                                Some(pc),
                                None,
                                Some(*cp),
                            )?;
                        }
                    }
                    return Ok(());
                }
                RelicId::MummifiedHand => {
                    if !state.is_live_combat()
                        || played_card_type.is_some_and(|t| t != CardType::Power)
                    {
                        return Ok(());
                    }
                }
                RelicId::PenNib => {
                    if relic.private_fields.len() > 1 {
                        let tracked_ord = relic
                            .private_fields
                            .get("PenNib._doubleDamagePlayOrdinal")
                            .and_then(|v| match v {
                                FieldValue::Int(i) => Some(*i),
                                _ => None,
                            });
                        if tracked_ord == Some(cp.play_ordinal) {
                            state.mark_relic_dirty(ridx);
                            let r = &mut state.player.relics[ridx];
                            r.private_fields
                                .remove("PenNib._doubleDamagePlayOrdinal");
                            r.private_fields.remove("PenNib._doubleDamageCard");
                        }
                    }
                    return Ok(());
                }
                RelicId::Pocketwatch => {
                    state.mark_relic_dirty(ridx);
                    state.player.relics[ridx]
                        .private_fields
                        .inc_int("Pocketwatch._cardsPlayedThisTurn");
                    return Ok(());
                }
                RelicId::RainbowRing => {
                    if matches!(
                        relic.private_fields.get("RainbowRing._triggeredThisTurn"),
                        Some(FieldValue::Bool(true))
                    ) || !matches!(
                        played_card_type,
                        Some(CardType::Attack | CardType::Skill | CardType::Power)
                    ) {
                        return Ok(());
                    }
                }
                _ => {}
            }
            let _ = relics::dispatch_hook(self, state, ridx, HookSlot::AfterCardPlayed, args)?;
            return Ok(());
        }
        let _ = self.dispatch_cached_listener_monomorphic(
            state,
            listener,
            HookSlot::AfterCardPlayed,
            args,
        )?;
        Ok(())
    }

    #[inline(always)]
    fn dispatch_cached_non_power_monomorphic(
        &self,
        state: &mut BattleState,
        listener: CachedHookListener,
        slot: HookSlot,
        args: &HookArgs<'_>,
    ) -> Result<HookResult, KernelError> {
        #[cfg(debug_assertions)]
        debug_assert!(
            sts2_kernel::hooks::is_eligible_hook_listener(state, listener.to_hook_listener()),
            "cached listener {:?} ineligible on {:?}",
            listener,
            slot
        );
        match listener {
            CachedHookListener::Relic(idx) => {
                relics::dispatch_hook(self, state, idx as usize, slot, args)
            }
            CachedHookListener::Potion(idx) => {
                potions::dispatch_hook(self, state, idx as usize, slot, args)
            }
            CachedHookListener::Monster(ch) => monsters::dispatch_hook(self, state, ch, slot, args),
            CachedHookListener::Orb(idx) => {
                orbs_enchantments_afflictions::dispatch_orb_hook(self, state, idx as usize, slot, args)
            }
            CachedHookListener::Card(h) => cards::dispatch_hook(self, state, h, slot, args),
            CachedHookListener::Enchantment(h) => {
                orbs_enchantments_afflictions::dispatch_enchantment_hook(self, state, h, slot, args)
            }
            CachedHookListener::Affliction(h) => {
                orbs_enchantments_afflictions::dispatch_affliction_hook(self, state, h, slot, args)
            }
            CachedHookListener::RunModel(id) => {
                dread_blitz::dispatch_run_model_hook(self, state, id, slot, args)
            }
            CachedHookListener::Dominion(id) => {
                dread_blitz::dispatch_dominion_hook(self, state, id, slot, args)
            }
            CachedHookListener::Power { .. } => unreachable!(),
        }
    }

    #[inline(always)]
    fn dispatch_cached_listener_monomorphic(
        &self,
        state: &mut BattleState,
        listener: CachedHookListener,
        slot: HookSlot,
        args: &HookArgs<'_>,
    ) -> Result<HookResult, KernelError> {
        if let CachedHookListener::Power {
            creature,
            power_handle,
            creature_idx,
            power_idx,
        } = listener
        {
            if let Some((pi, power, owner_side)) =
                resolve_cached_power_ref(state, creature, power_handle, creature_idx, power_idx)
            {
                let p_copy = *power;
                return powers::dispatch_hook_resolved(
                    self, state, creature, pi, p_copy, owner_side, slot, args,
                );
            }
            return Ok(HookResult::default());
        }
        self.dispatch_cached_non_power_monomorphic(state, listener, slot, args)
    }

    #[inline(always)]
    fn dispatch_cached_listener_if_eligible_monomorphic(
        &self,
        state: &mut BattleState,
        listener: CachedHookListener,
        slot: HookSlot,
        args: &HookArgs<'_>,
    ) -> Result<(), KernelError> {
        match listener {
            CachedHookListener::Power {
                creature,
                power_handle,
                creature_idx,
                power_idx,
            } => {
                if creature_idx != u16::MAX {
                    if let Some(c) = state.creatures.get(creature_idx as usize) {
                        if c.handle == creature {
                            if c.is_detached
                                || !state.contains_creature(creature)
                                || (c.is_player && !state.player.is_active_for_hooks)
                            {
                                return Ok(());
                            }
                            if power_idx != u16::MAX {
                                if let Some(&power) = c.powers.get(power_idx as usize) {
                                    if power.handle == power_handle {
                                        let owner_side = c.side;
                                        let _ = powers::dispatch_hook_resolved(
                                            self,
                                            state,
                                            creature,
                                            power_idx as usize,
                                            power,
                                            owner_side,
                                            slot,
                                            args,
                                        )?;
                                        return Ok(());
                                    }
                                }
                            }
                            for (idx, &p) in c.powers.iter().enumerate() {
                                if p.handle == power_handle {
                                    let owner_side = c.side;
                                    let _ = powers::dispatch_hook_resolved(
                                        self, state, creature, idx, p, owner_side, slot, args,
                                    )?;
                                    return Ok(());
                                }
                            }
                            return Ok(());
                        }
                    }
                }
                if let Some((pi, power, owner_side)) =
                    resolve_power_cold_ref(state, creature, power_handle)
                {
                    let p_copy = *power;
                    let _ = powers::dispatch_hook_resolved(
                        self, state, creature, pi, p_copy, owner_side, slot, args,
                    )?;
                }
                Ok(())
            }
            CachedHookListener::Relic(relic_idx) => {
                if !state.player.is_active_for_hooks {
                    return Ok(());
                }
                let Some(relic) = state.player.relics.get(relic_idx as usize) else {
                    return Ok(());
                };
                if relic.is_melted {
                    return Ok(());
                }
                let _ = relics::dispatch_hook(self, state, relic_idx as usize, slot, args)?;
                Ok(())
            }
            CachedHookListener::Potion(idx) => {
                let raw = HookListener::Potion(idx as usize);
                if sts2_kernel::hooks::is_eligible_hook_listener(state, raw) {
                    let _ = potions::dispatch_hook(self, state, idx as usize, slot, args)?;
                }
                Ok(())
            }
            CachedHookListener::Monster(ch) => {
                let raw = HookListener::Monster(ch);
                if sts2_kernel::hooks::is_eligible_hook_listener(state, raw) {
                    let _ = monsters::dispatch_hook(self, state, ch, slot, args)?;
                }
                Ok(())
            }
            CachedHookListener::Orb(idx) => {
                let raw = HookListener::Orb(idx as usize);
                if sts2_kernel::hooks::is_eligible_hook_listener(state, raw) {
                    let _ = orbs_enchantments_afflictions::dispatch_orb_hook(
                        self, state, idx as usize, slot, args,
                    )?;
                }
                Ok(())
            }
            CachedHookListener::Card(h) => {
                let raw = HookListener::Card(h);
                if sts2_kernel::hooks::is_eligible_hook_listener(state, raw) {
                    let _ = cards::dispatch_hook(self, state, h, slot, args)?;
                }
                Ok(())
            }
            CachedHookListener::Enchantment(h) => {
                let raw = HookListener::Enchantment(h);
                if sts2_kernel::hooks::is_eligible_hook_listener(state, raw) {
                    let _ = orbs_enchantments_afflictions::dispatch_enchantment_hook(
                        self, state, h, slot, args,
                    )?;
                }
                Ok(())
            }
            CachedHookListener::Affliction(h) => {
                let raw = HookListener::Affliction(h);
                if sts2_kernel::hooks::is_eligible_hook_listener(state, raw) {
                    let _ = orbs_enchantments_afflictions::dispatch_affliction_hook(
                        self, state, h, slot, args,
                    )?;
                }
                Ok(())
            }
            CachedHookListener::RunModel(id) => {
                let _ = dread_blitz::dispatch_run_model_hook(self, state, id, slot, args)?;
                Ok(())
            }
            CachedHookListener::Dominion(id) => {
                let _ = dread_blitz::dispatch_dominion_hook(self, state, id, slot, args)?;
                Ok(())
            }
        }
    }
}

impl ContentDispatcher for Sts2Content {
    #[inline]
    fn card_meta(&self, id: CardId) -> Result<&'static CardMeta, KernelError> {
        Ok(&CONTENT_TABLES.card_metas[id as usize])
    }

    #[inline]
    fn power_meta(&self, id: PowerId) -> Result<&'static PowerMeta, KernelError> {
        Ok(&CONTENT_TABLES.power_metas[id as usize])
    }

    #[inline]
    fn relic_meta(&self, id: RelicId) -> Result<&'static RelicMeta, KernelError> {
        Ok(CONTENT_TABLES.relic_metas[id as usize])
    }

    #[inline]
    fn potion_meta(&self, id: PotionId) -> Result<&'static PotionMeta, KernelError> {
        Ok(CONTENT_TABLES.potion_metas[id as usize])
    }

    #[inline]
    fn monster_meta(&self, id: MonsterId) -> Result<&'static MonsterMeta, KernelError> {
        Ok(CONTENT_TABLES.monster_metas[id as usize])
    }

    #[inline]
    fn orb_meta(&self, id: OrbId) -> Result<&'static OrbMeta, KernelError> {
        Ok(CONTENT_TABLES.orb_metas[id as usize])
    }

    fn card_is_playable(&self, state: &BattleState, card: CardHandle) -> Result<bool, KernelError> {
        cards::card_is_playable(state, card)
    }

    fn card_on_play(
        &self,
        state: &mut BattleState,
        ctx: &CardPlayContext,
        choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
        resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        cards::card_on_play(self, state, ctx, choice_queue, resume_step)
    }

    fn card_on_play_resume_after_select(
        &self,
        state: &mut BattleState,
        ctx: &CardPlayContext,
        selected: &[CardHandle],
        resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        cards::card_on_play_resume_after_select(self, state, ctx, selected, resume_step)
    }

    fn card_on_upgrade(&self, state: &mut BattleState, card: CardHandle) -> Result<(), KernelError> {
        cards::card_on_upgrade(state, card)
    }

    fn card_comparison_base_values(
        &self,
        state: &BattleState,
        card: CardHandle,
    ) -> Result<SmallVec<[(&'static str, Dec); 4]>, KernelError> {
        cards::card_comparison_base_values(state, card)
    }

    fn card_on_turn_end_in_hand(
        &self,
        state: &mut BattleState,
        card: CardHandle,
    ) -> Result<(), KernelError> {
        cards::card_on_turn_end_in_hand(self, state, card)
    }

    #[inline]
    fn card_resolved_type(
        &self,
        state: &BattleState,
        card: CardHandle,
    ) -> Result<CardType, KernelError> {
        let id = state
            .card(card)
            .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
            .id;
        match id {
            CardId::MadScience => cards::colorless_special::resolved_type(state, card),
            _ => Ok(CONTENT_TABLES.card_metas[id as usize].card_type),
        }
    }

    #[inline]
    fn card_resolved_target_type(
        &self,
        state: &BattleState,
        card: CardHandle,
    ) -> Result<TargetType, KernelError> {
        let id = state
            .card(card)
            .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
            .id;
        match id {
            CardId::Shiv | CardId::SovereignBlade | CardId::MadScience => {
                cards::colorless_special::resolved_target_type(state, card)
            }
            _ => Ok(CONTENT_TABLES.card_metas[id as usize].target_type),
        }
    }

    fn card_default_result_location(
        &self,
        state: &BattleState,
        card: CardHandle,
        is_auto_play: bool,
    ) -> Result<Option<(PileType, CardPilePosition)>, KernelError> {
        cards::card_default_result_location(state, card, is_auto_play)
    }

    fn card_thrash_damage_variable(
        &self,
        state: &BattleState,
        card: CardHandle,
    ) -> Result<Dec, KernelError> {
        cards::card_thrash_damage_variable(state, card)
    }

    fn monster_initial_hp_range(
        &self,
        state: &BattleState,
        monster_id: MonsterId,
    ) -> Result<(i32, i32), KernelError> {
        monsters::monster_initial_hp_range(state, monster_id)
    }

    fn monster_setup_for_combat(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        monsters::monster_setup_for_combat(state, creature)
    }

    fn monster_after_added_to_room(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        monsters::monster_after_added_to_room(self, state, creature)
    }

    fn monster_roll_move(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        monsters::monster_roll_move(state, creature)
    }

    fn monster_perform_move(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        monsters::monster_perform_move(self, state, creature)
    }

    fn monster_resume_move_choice(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
        selected: &[CardHandle],
    ) -> Result<(), KernelError> {
        monsters::monster_resume_move_choice(self, state, creature, selected)
    }

    fn monster_attack_intent_hits(
        &self,
        state: &BattleState,
        creature: CreatureHandle,
    ) -> Result<SmallVec<[(Dec, i32); 2]>, KernelError> {
        monsters::monster_attack_intent_hits(state, creature)
    }

    fn orb_passive(
        &self,
        state: &mut BattleState,
        orb_idx: usize,
        target: Option<CreatureHandle>,
    ) -> Result<SmallVec<[CreatureHandle; 4]>, KernelError> {
        orbs_enchantments_afflictions::orb_passive(self, state, orb_idx, target)
    }

    fn orb_evoke(
        &self,
        state: &mut BattleState,
        orb_idx: usize,
    ) -> Result<SmallVec<[CreatureHandle; 4]>, KernelError> {
        orbs_enchantments_afflictions::orb_evoke(self, state, orb_idx)
    }

    fn orb_before_turn_end(&self, state: &mut BattleState, orb_idx: usize) -> Result<(), KernelError> {
        orbs_enchantments_afflictions::orb_before_turn_end(self, state, orb_idx)
    }

    fn orb_after_turn_start(&self, state: &mut BattleState, orb_idx: usize) -> Result<(), KernelError> {
        orbs_enchantments_afflictions::orb_after_turn_start(self, state, orb_idx)
    }

    fn potion_on_use(
        &self,
        state: &mut BattleState,
        potion_id: PotionId,
        target: Option<CreatureHandle>,
        choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
        resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        potions::potion_on_use(self, state, potion_id, target, choice_queue, resume_step)
    }

    fn potion_on_use_resume_after_select(
        &self,
        state: &mut BattleState,
        potion_id: PotionId,
        target: Option<CreatureHandle>,
        selected: &[CardHandle],
        resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        potions::potion_on_use_resume_after_select(
            self,
            state,
            potion_id,
            target,
            selected,
            resume_step,
        )
    }

    fn resume_hook_choice(
        &self,
        state: &mut BattleState,
        listener: HookListener,
        slot: HookSlot,
        selected: &[CardHandle],
    ) -> Result<HookResult, KernelError> {
        match listener {
            HookListener::Dominion(id) => {
                dread_blitz::resume_dominion_hook_choice(self, state, id, slot, selected)
            }
            HookListener::Power {
                creature,
                power_handle,
            } => powers::resume_hook_choice(self, state, creature, power_handle, slot, selected),
            HookListener::Relic(idx) => relics::resume_hook_choice(self, state, idx, slot, selected),
            HookListener::Monster(ch) => {
                monsters::resume_hook_choice(self, state, ch, slot, selected)
            }
            _ => Err(KernelError::Unsupported {
                kind: "HookChoiceResume",
                id: format!("{listener:?}:{slot:?}"),
            }),
        }
    }

    fn enchantment_on_play(
        &self,
        state: &mut BattleState,
        card: CardHandle,
        ctx: &CardPlayContext,
    ) -> Result<(), KernelError> {
        orbs_enchantments_afflictions::enchantment_on_play(self, state, card, ctx)
    }

    fn affliction_on_play(
        &self,
        state: &mut BattleState,
        card: CardHandle,
        ctx: &CardPlayContext,
    ) -> Result<(), KernelError> {
        orbs_enchantments_afflictions::affliction_on_play(self, state, card, ctx)
    }

    fn enchantment_play_count(
        &self,
        state: &BattleState,
        card: CardHandle,
        play_count: i32,
    ) -> Result<i32, KernelError> {
        orbs_enchantments_afflictions::enchantment_play_count(state, card, play_count)
    }

    fn enchantment_damage_additive(
        &self,
        state: &BattleState,
        card: CardHandle,
        amount: Dec,
        props: ValueProp,
    ) -> Result<Dec, KernelError> {
        orbs_enchantments_afflictions::enchantment_damage_additive(state, card, amount, props)
    }

    fn enchantment_damage_multiplicative(
        &self,
        state: &BattleState,
        card: CardHandle,
        amount: Dec,
        props: ValueProp,
    ) -> Result<Dec, KernelError> {
        orbs_enchantments_afflictions::enchantment_damage_multiplicative(
            state, card, amount, props,
        )
    }

    fn enchantment_should_start_at_bottom(&self, id: EnchantmentId) -> Result<bool, KernelError> {
        orbs_enchantments_afflictions::enchantment_should_start_at_bottom(id)
    }

    fn enchantment_modify_shuffle_order(
        &self,
        state: &BattleState,
        card: CardHandle,
        cards: &mut [CardHandle],
        is_initial_shuffle: bool,
    ) -> Result<(), KernelError> {
        orbs_enchantments_afflictions::enchantment_modify_shuffle_order(
            state,
            card,
            cards,
            is_initial_shuffle,
        )
    }

    #[inline]
    fn category_hook_masks(&self) -> Option<CategoryHookMasks> {
        Some(CONTENT_TABLES.categories)
    }

    #[inline]
    fn hook_mask_tables(&self) -> Option<&'static HookMaskTableView> {
        Some(&*HOOK_MASK_TABLE_VIEW)
    }

    #[inline]
    fn card_rollout_profile(&self, id: CardId) -> CardRolloutProfile {
        CONTENT_TABLES.card_rollout_profiles[id as usize]
    }

    #[inline]
    fn card_hook_mask(&self, id: CardId) -> Result<HookMask, KernelError> {
        Ok(CONTENT_TABLES.cards[id as usize])
    }

    #[inline]
    fn power_hook_mask(&self, id: PowerId) -> Result<HookMask, KernelError> {
        Ok(CONTENT_TABLES.powers[id as usize])
    }

    #[inline]
    fn relic_hook_mask(&self, id: RelicId) -> Result<HookMask, KernelError> {
        Ok(CONTENT_TABLES.relics[id as usize])
    }

    #[inline]
    fn potion_hook_mask(&self, id: PotionId) -> Result<HookMask, KernelError> {
        Ok(CONTENT_TABLES.potions[id as usize])
    }

    #[inline]
    fn monster_hook_mask(&self, id: MonsterId) -> Result<HookMask, KernelError> {
        Ok(CONTENT_TABLES.monsters[id as usize])
    }

    #[inline]
    fn orb_hook_mask(&self, id: OrbId) -> Result<HookMask, KernelError> {
        Ok(CONTENT_TABLES.orbs[id as usize])
    }

    #[inline]
    fn enchantment_hook_mask_for_card(
        &self,
        _state: &BattleState,
        _card: CardHandle,
        id: EnchantmentId,
    ) -> Result<HookMask, KernelError> {
        Ok(CONTENT_TABLES.enchantments[id as usize])
    }

    #[inline]
    fn affliction_hook_mask_for_card(
        &self,
        _state: &BattleState,
        _card: CardHandle,
        id: AfflictionId,
    ) -> Result<HookMask, KernelError> {
        Ok(CONTENT_TABLES.afflictions[id as usize])
    }

    #[inline]
    fn listener_hook_mask(
        &self,
        state: &BattleState,
        listener: HookListener,
    ) -> Result<HookMask, KernelError> {
        match listener {
            HookListener::Relic(idx) => {
                let id = state
                    .player
                    .relics
                    .get(idx)
                    .ok_or_else(|| KernelError::InvalidState("missing relic".to_owned()))?
                    .id;
                Ok(CONTENT_TABLES.relics[id as usize])
            }
            HookListener::Potion(idx) => {
                let id = state
                    .player
                    .potion_slots
                    .get(idx)
                    .and_then(|s| *s)
                    .ok_or_else(|| KernelError::InvalidState("missing potion".to_owned()))?;
                Ok(CONTENT_TABLES.potions[id as usize])
            }
            HookListener::Power {
                creature,
                power_handle,
            } => {
                let id = state
                    .creature(creature)
                    .and_then(|c| c.powers.iter().find(|p| p.handle == power_handle))
                    .ok_or_else(|| KernelError::InvalidState("missing power".to_owned()))?
                    .id;
                Ok(CONTENT_TABLES.powers[id as usize])
            }
            HookListener::DetachedPower { power, .. } => Ok(CONTENT_TABLES.powers[power.id as usize]),
            HookListener::Monster(ch) => {
                let id = state
                    .creature(ch)
                    .and_then(|c| c.monster_id)
                    .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?;
                Ok(CONTENT_TABLES.monsters[id as usize])
            }
            HookListener::Orb(idx) => {
                let id = state
                    .player
                    .orbs
                    .get(idx)
                    .ok_or_else(|| KernelError::InvalidState("missing orb".to_owned()))?
                    .id;
                Ok(CONTENT_TABLES.orbs[id as usize])
            }
            HookListener::Card(h) => {
                let id = state
                    .card(h)
                    .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
                    .id;
                Ok(CONTENT_TABLES.cards[id as usize])
            }
            HookListener::Enchantment(h) => {
                let id = state
                    .card(h)
                    .and_then(|c| state.card_ext(c.ext_index))
                    .and_then(|e| e.enchantment.as_ref().map(|x| x.id))
                    .ok_or_else(|| KernelError::InvalidState("missing enchantment".to_owned()))?;
                Ok(CONTENT_TABLES.enchantments[id as usize])
            }
            HookListener::Affliction(h) => {
                let id = state
                    .card(h)
                    .and_then(|c| c.affliction.map(|a| a.id))
                    .ok_or_else(|| KernelError::InvalidState("missing affliction".to_owned()))?;
                Ok(CONTENT_TABLES.afflictions[id as usize])
            }
            HookListener::RunModel(id) => dread_blitz::run_model_hook_mask(id),
            HookListener::Dominion(id) => dread_blitz::dominion_hook_mask(id),
        }
    }

    #[inline]
    fn dispatch_power_hook_resolved(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
        power_idx: usize,
        power: PowerInst,
        owner_side: CombatSide,
        slot: HookSlot,
        args: &HookArgs<'_>,
    ) -> Result<HookResult, KernelError> {
        powers::dispatch_hook_resolved(self, state, creature, power_idx, power, owner_side, slot, args)
    }

    #[inline]
    fn dispatch_hook(
        &self,
        state: &mut BattleState,
        listener: HookListener,
        slot: HookSlot,
        args: &HookArgs<'_>,
    ) -> Result<HookResult, KernelError> {
        match listener {
            HookListener::Relic(idx) => relics::dispatch_hook(self, state, idx, slot, args),
            HookListener::Potion(idx) => potions::dispatch_hook(self, state, idx, slot, args),
            HookListener::Power {
                creature,
                power_handle,
            } => powers::dispatch_hook(self, state, creature, power_handle, slot, args),
            HookListener::DetachedPower { creature, power } => {
                powers::dispatch_detached_hook(self, state, creature, power, slot, args)
            }
            HookListener::Monster(ch) => monsters::dispatch_hook(self, state, ch, slot, args),
            HookListener::Orb(idx) => {
                orbs_enchantments_afflictions::dispatch_orb_hook(self, state, idx, slot, args)
            }
            HookListener::Card(h) => cards::dispatch_hook(self, state, h, slot, args),
            HookListener::Enchantment(h) => {
                orbs_enchantments_afflictions::dispatch_enchantment_hook(self, state, h, slot, args)
            }
            HookListener::Affliction(h) => {
                orbs_enchantments_afflictions::dispatch_affliction_hook(self, state, h, slot, args)
            }
            HookListener::RunModel(id) => {
                dread_blitz::dispatch_run_model_hook(self, state, id, slot, args)
            }
            HookListener::Dominion(id) => {
                dread_blitz::dispatch_dominion_hook(self, state, id, slot, args)
            }
        }
    }

    #[inline]
    fn check_should_allow_hitting_cached(
        &self,
        state: &mut BattleState,
        target: CreatureHandle,
        mut bits: u64,
        base: usize,
    ) -> Result<Option<bool>, KernelError> {
        while bits != 0 {
            let idx = base + bits.trailing_zeros() as usize;
            bits &= bits - 1;
            let listener = state.hook_cache.flat_listeners[idx];
            let disallowed = if let CachedHookListener::Power {
                creature,
                power_handle,
                creature_idx,
                power_idx,
            } = listener
            {
                if let Some((pi, power, _)) =
                    resolve_cached_power_ref(state, creature, power_handle, creature_idx, power_idx)
                {
                    let fast = match power.id {
                        PowerId::AdaptablePower => {
                            creature == target
                                && powers::batch_a_m::get_bool_field(
                                    state,
                                    creature,
                                    pi,
                                    "AdaptablePower._isReviving",
                                    false,
                                )
                        }
                        PowerId::IllusionPower => {
                            creature == target
                                && powers::batch_a_m::get_bool_field(
                                    state,
                                    creature,
                                    pi,
                                    "IllusionPower._isReviving",
                                    false,
                                )
                        }
                        PowerId::ReattachPower => {
                            creature == target
                                && power.ext_index.is_some()
                                && powers::batch_n_z::get_bool_field(
                                    state,
                                    power,
                                    "ReattachPower._isReviving",
                                    false,
                                )
                        }
                        PowerId::DieForYouPower | PowerId::WuppysLoyaltyPower => {
                            !state.creature(target).is_some_and(|c| c.is_alive())
                        }
                        _ => false,
                    };
                    #[cfg(debug_assertions)]
                    {
                        let p_copy = *power;
                        let exp = powers::dispatch_should_allow_hitting(
                            state,
                            creature,
                            pi,
                            p_copy,
                            &HookArgs::Creature(target),
                        )?;
                        debug_assert_eq!(exp.bool_val == Some(false), fast);
                    }
                    fast
                } else {
                    false
                }
            } else {
                let res = self.dispatch_cached_non_power_monomorphic(
                    state,
                    listener,
                    HookSlot::ShouldAllowHitting,
                    &HookArgs::Creature(target),
                )?;
                res.bool_val == Some(false)
            };
            #[cfg(debug_assertions)]
            debug_assert_eq!(
                state.hook_cache.dirty_mask, 0,
                "ShouldAllowHitting mutated hook_cache during {:?}",
                listener
            );
            if disallowed {
                return Ok(Some(false));
            }
        }
        Ok(Some(true))
    }

    #[inline]
    fn check_should_creature_be_removed_after_death_cached(
        &self,
        state: &mut BattleState,
        target: CreatureHandle,
        mut bits: u64,
        base: usize,
    ) -> Result<Option<bool>, KernelError> {
        while bits != 0 {
            let idx = base + bits.trailing_zeros() as usize;
            bits &= bits - 1;
            let listener = state.hook_cache.flat_listeners[idx];
            let disallowed = if let CachedHookListener::Power {
                creature,
                power_handle,
                creature_idx,
                power_idx,
            } = listener
            {
                if creature != target {
                    #[cfg(debug_assertions)]
                    if let Some((_, power, _)) = resolve_cached_power_ref(
                        state,
                        creature,
                        power_handle,
                        creature_idx,
                        power_idx,
                    ) {
                        let exp = powers::dispatch_should_creature_removed_after_death(
                            creature,
                            power.id,
                            &HookArgs::Creature(target),
                        )?;
                        debug_assert_ne!(exp.bool_val, Some(false));
                    }
                    false
                } else if let Some((_, power, _)) =
                    resolve_cached_power_ref(state, creature, power_handle, creature_idx, power_idx)
                {
                    let fast = matches!(
                        power.id,
                        PowerId::AdaptablePower
                            | PowerId::DieForYouPower
                            | PowerId::IllusionPower
                            | PowerId::PainfulStabsPower
                            | PowerId::ReattachPower
                            | PowerId::SteamEruptionPower
                            | PowerId::WuppysLoyaltyPower
                    );
                    #[cfg(debug_assertions)]
                    {
                        let exp = powers::dispatch_should_creature_removed_after_death(
                            creature,
                            power.id,
                            &HookArgs::Creature(target),
                        )?;
                        debug_assert_eq!(exp.bool_val == Some(false), fast);
                    }
                    fast
                } else {
                    false
                }
            } else {
                let res = self.dispatch_cached_non_power_monomorphic(
                    state,
                    listener,
                    HookSlot::ShouldCreatureBeRemovedFromCombatAfterDeath,
                    &HookArgs::Creature(target),
                )?;
                res.bool_val == Some(false)
            };
            #[cfg(debug_assertions)]
            debug_assert_eq!(
                state.hook_cache.dirty_mask, 0,
                "ShouldCreatureBeRemovedFromCombatAfterDeath mutated hook_cache during {:?}",
                listener
            );
            if disallowed {
                return Ok(Some(false));
            }
        }
        Ok(Some(true))
    }

    #[inline]
    fn apply_modify_damage_slots_cached(
        &self,
        state: &mut BattleState,
        target: Option<CreatureHandle>,
        dealer: Option<CreatureHandle>,
        damage: Dec,
        props: ValueProp,
        card_source: Option<CardHandle>,
        card_play: Option<CardPlayContext>,
        hook_type: ModifyDamageHookType,
    ) -> Result<Option<Dec>, KernelError> {
        let comb = state.hook_cache.masks.combat_combined;
        let words = state.hook_cache.flat_slot_words as usize;
        let mut result = damage;

        if hook_type.contains(ModifyDamageHookType::ADDITIVE)
            && comb.contains(HookSlot::ModifyDamageAdditive)
        {
            for lw in 0..words {
                let lbase = lw << 6;
                let mut bits = if lw == 0 {
                    state
                        .hook_cache
                        .slot_listener_mask(HookSlot::ModifyDamageAdditive as usize)
                } else {
                    state
                        .hook_cache
                        .slot_listener_mask_word(HookSlot::ModifyDamageAdditive as usize, lw)
                };
                while bits != 0 {
                    let idx = lbase + bits.trailing_zeros() as usize;
                    bits &= bits - 1;
                    debug_assert!(idx < state.hook_cache.flat_listeners.len());
                    let listener = unsafe { *state.hook_cache.flat_listeners.get_unchecked(idx) };
                    if let CachedHookListener::Power {
                        creature,
                        power_handle,
                        creature_idx,
                        power_idx,
                    } = listener
                    {
                        if let Some((pi, power, owner_side)) = resolve_cached_power_ref(
                            state,
                            creature,
                            power_handle,
                            creature_idx,
                            power_idx,
                        ) {
                            if matches!(power.id, PowerId::StrengthPower | PowerId::VigorPower) {
                                let fast_delta =
                                    if dealer == Some(creature) && props.is_powered_attack() {
                                        Some(Dec::from_i32(power.amount))
                                    } else {
                                        None
                                    };
                                #[cfg(debug_assertions)]
                                {
                                    let p_copy = *power;
                                    let exp = powers::dispatch_hook_resolved(
                                        self,
                                        state,
                                        creature,
                                        pi,
                                        p_copy,
                                        owner_side,
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
                                    debug_assert_eq!(exp.dec_val, fast_delta);
                                }
                                let _ = (pi, owner_side);
                                if let Some(delta) = fast_delta {
                                    result = result.checked_add(delta)?;
                                }
                                continue;
                            }
                            let p_copy = *power;
                            let res = powers::dispatch_hook_resolved(
                                self,
                                state,
                                creature,
                                pi,
                                p_copy,
                                owner_side,
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
                            #[cfg(debug_assertions)]
                            debug_assert_eq!(
                                state.hook_cache.dirty_mask, 0,
                                "ModifyDamageAdditive mutated hook_cache during {:?}",
                                listener
                            );
                            if let Some(delta) = res.dec_val {
                                result = result.checked_add(delta)?;
                            }
                        }
                        continue;
                    }
                    let res = self.dispatch_cached_non_power_monomorphic(
                        state,
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
                    #[cfg(debug_assertions)]
                    debug_assert_eq!(
                        state.hook_cache.dirty_mask, 0,
                        "ModifyDamageAdditive mutated hook_cache during {:?}",
                        listener
                    );
                    if let Some(delta) = res.dec_val {
                        result = result.checked_add(delta)?;
                    }
                }
            }
        }

        if hook_type.contains(ModifyDamageHookType::MULTIPLICATIVE) {
            if comb.contains(HookSlot::ModifyDamageMultiplicative) {
                for lw in 0..words {
                    let lbase = lw << 6;
                    let mut bits = if lw == 0 {
                        state
                            .hook_cache
                            .slot_listener_mask(HookSlot::ModifyDamageMultiplicative as usize)
                    } else {
                        state.hook_cache.slot_listener_mask_word(
                            HookSlot::ModifyDamageMultiplicative as usize,
                            lw,
                        )
                    };
                    while bits != 0 {
                        let idx = lbase + bits.trailing_zeros() as usize;
                        bits &= bits - 1;
                        debug_assert!(idx < state.hook_cache.flat_listeners.len());
                        let listener = unsafe { *state.hook_cache.flat_listeners.get_unchecked(idx) };
                        if let CachedHookListener::Power {
                            creature,
                            power_handle,
                            creature_idx,
                            power_idx,
                        } = listener
                        {
                            if let Some((pi, power, owner_side)) = resolve_cached_power_ref(
                                state,
                                creature,
                                power_handle,
                                creature_idx,
                                power_idx,
                            ) {
                                match power.id {
                                    PowerId::VulnerablePower => {
                                        #[cfg(debug_assertions)]
                                        let p_copy = *power;
                                        let mult = if target == Some(creature)
                                            && props.is_powered_attack()
                                        {
                                            let m = if !comb
                                                .contains(HookSlot::ModifyVulnerableMultiplier)
                                            {
                                                Dec::ONE_POINT_FIVE
                                            } else {
                                                sts2_kernel::hooks::modify_vulnerable_multiplier(
                                                    state,
                                                    self,
                                                    target,
                                                    dealer,
                                                    Dec::ONE_POINT_FIVE,
                                                    props,
                                                    card_source,
                                                )?
                                            };
                                            Some(m)
                                        } else {
                                            None
                                        };
                                        #[cfg(debug_assertions)]
                                        {
                                            let exp = powers::dispatch_hook_resolved(
                                                self,
                                                state,
                                                creature,
                                                pi,
                                                p_copy,
                                                owner_side,
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
                                            debug_assert_eq!(exp.dec_val, mult);
                                        }
                                        let _ = (pi, owner_side);
                                        if let Some(factor) = mult {
                                            result = result.checked_mul(factor)?;
                                        }
                                        continue;
                                    }
                                    PowerId::WeakPower => {
                                        #[cfg(debug_assertions)]
                                        let p_copy = *power;
                                        let mult = if dealer == Some(creature)
                                            && props.is_powered_attack()
                                        {
                                            let m = if !comb.contains(HookSlot::ModifyWeakMultiplier)
                                            {
                                                Dec::THREE_QUARTERS
                                            } else {
                                                sts2_kernel::hooks::modify_weak_multiplier(
                                                    state,
                                                    self,
                                                    target,
                                                    dealer,
                                                    Dec::THREE_QUARTERS,
                                                    props,
                                                    card_source,
                                                )?
                                            };
                                            Some(m)
                                        } else {
                                            None
                                        };
                                        #[cfg(debug_assertions)]
                                        {
                                            let exp = powers::dispatch_hook_resolved(
                                                self,
                                                state,
                                                creature,
                                                pi,
                                                p_copy,
                                                owner_side,
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
                                            debug_assert_eq!(exp.dec_val, mult);
                                        }
                                        let _ = (pi, owner_side);
                                        if let Some(factor) = mult {
                                            result = result.checked_mul(factor)?;
                                        }
                                        continue;
                                    }
                                    _ => {
                                        let p_copy = *power;
                                        let res = powers::dispatch_hook_resolved(
                                            self,
                                            state,
                                            creature,
                                            pi,
                                            p_copy,
                                            owner_side,
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
                                        #[cfg(debug_assertions)]
                                        debug_assert_eq!(
                                            state.hook_cache.dirty_mask, 0,
                                            "ModifyDamageMultiplicative mutated hook_cache during {:?}",
                                            listener
                                        );
                                        if let Some(factor) = res.dec_val {
                                            result = result.checked_mul(factor)?;
                                        }
                                    }
                                }
                            }
                            continue;
                        }
                        if let CachedHookListener::Relic(ridx) = listener {
                            if let Some(relic) = state.player.relics.get(ridx as usize) {
                                if !relic.is_melted
                                    && relic.id == RelicId::PenNib
                                    && (!props.is_powered_attack()
                                        || card_play.is_none()
                                        || relic.private_fields.len() <= 1)
                                {
                                    #[cfg(debug_assertions)]
                                    {
                                        let exp = self.dispatch_cached_non_power_monomorphic(
                                            state,
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
                                        debug_assert_eq!(exp.dec_val, Some(Dec::ONE));
                                    }
                                    continue;
                                }
                            }
                        }
                        let res = self.dispatch_cached_non_power_monomorphic(
                            state,
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
                        #[cfg(debug_assertions)]
                        debug_assert_eq!(
                            state.hook_cache.dirty_mask, 0,
                            "ModifyDamageMultiplicative mutated hook_cache during {:?}",
                            listener
                        );
                        if let Some(factor) = res.dec_val {
                            result = result.checked_mul(factor)?;
                        }
                    }
                }
            }

            if comb.contains(HookSlot::ModifyDamageMultiplicativeLate) {
                for lw in 0..words {
                    let lbase = lw << 6;
                    let mut bits = if lw == 0 {
                        state
                            .hook_cache
                            .slot_listener_mask(HookSlot::ModifyDamageMultiplicativeLate as usize)
                    } else {
                        state.hook_cache.slot_listener_mask_word(
                            HookSlot::ModifyDamageMultiplicativeLate as usize,
                            lw,
                        )
                    };
                    while bits != 0 {
                        let idx = lbase + bits.trailing_zeros() as usize;
                        bits &= bits - 1;
                        debug_assert!(idx < state.hook_cache.flat_listeners.len());
                        let listener = unsafe { *state.hook_cache.flat_listeners.get_unchecked(idx) };
                        let res = self.dispatch_cached_listener_monomorphic(
                            state,
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
                        #[cfg(debug_assertions)]
                        debug_assert_eq!(
                            state.hook_cache.dirty_mask, 0,
                            "ModifyDamageMultiplicativeLate mutated hook_cache during {:?}",
                            listener
                        );
                        if let Some(factor) = res.dec_val {
                            result = result.checked_mul(factor)?;
                        }
                    }
                }
            }
        }

        if hook_type.contains(ModifyDamageHookType::CAP)
            && comb.contains(HookSlot::ModifyDamageCap)
        {
            let mut cap = Dec::MAX_VALUE;
            for lw in 0..words {
                let lbase = lw << 6;
                let mut bits = if lw == 0 {
                    state
                        .hook_cache
                        .slot_listener_mask(HookSlot::ModifyDamageCap as usize)
                } else {
                    state
                        .hook_cache
                        .slot_listener_mask_word(HookSlot::ModifyDamageCap as usize, lw)
                };
                while bits != 0 {
                    let idx = lbase + bits.trailing_zeros() as usize;
                    bits &= bits - 1;
                    debug_assert!(idx < state.hook_cache.flat_listeners.len());
                    let listener = unsafe { *state.hook_cache.flat_listeners.get_unchecked(idx) };
                    let res = self.dispatch_cached_listener_monomorphic(
                        state,
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
                    #[cfg(debug_assertions)]
                    debug_assert_eq!(
                        state.hook_cache.dirty_mask, 0,
                        "ModifyDamageCap mutated hook_cache during {:?}",
                        listener
                    );
                    if let Some(item_cap) = res.dec_val {
                        if item_cap < cap {
                            cap = item_cap;
                            if result > item_cap {
                                result = item_cap;
                            }
                        }
                    }
                }
            }
        }

        Ok(Some(result.max(Dec::ZERO)))
    }

    #[inline]
    fn dispatch_void_flat_bits_cached(
        &self,
        state: &mut BattleState,
        mut bits: u64,
        slot: HookSlot,
        args: &HookArgs<'_>,
    ) -> Result<bool, KernelError> {
        if slot == HookSlot::AfterCardPlayed {
            if let HookArgs::CardPlay(cp) = args {
                #[inline(never)]
                fn dispatch_after_card_played_bits_cached(
                    this: &Sts2Content,
                    state: &mut BattleState,
                    mut bits: u64,
                    cp: &CardPlayContext,
                    args: &HookArgs<'_>,
                ) -> Result<bool, KernelError> {
                    let rev0 = state.hook_cache.revision;
                    let played_card_type = state.card(cp.card).map(|c| {
                        let idx = c.id as usize;
                        debug_assert!(idx < CONTENT_TABLES.card_metas.len());
                        unsafe { CONTENT_TABLES.card_metas.get_unchecked(idx).card_type }
                    });
                    let idx0 = bits.trailing_zeros() as usize;
                    bits &= bits - 1;
                    debug_assert!(idx0 < state.hook_cache.flat_listeners.len());
                    let l0 = unsafe { *state.hook_cache.flat_listeners.get_unchecked(idx0) };
                    if bits == 0 {
                        this.dispatch_after_card_played_listener_fast(
                            state,
                            l0,
                            cp,
                            played_card_type,
                            args,
                        )?;
                        return Ok(true);
                    }
                    let mut buf = ContentListenerBuf::new();
                    while bits != 0 {
                        let idx = bits.trailing_zeros() as usize;
                        bits &= bits - 1;
                        debug_assert!(idx < state.hook_cache.flat_listeners.len());
                        buf.push(unsafe { *state.hook_cache.flat_listeners.get_unchecked(idx) });
                    }
                    this.dispatch_after_card_played_listener_fast(
                        state,
                        l0,
                        cp,
                        played_card_type,
                        args,
                    )?;
                    for &l in buf.as_slice() {
                        if state.hook_cache.dirty_mask == 0 && state.hook_cache.revision == rev0 {
                            #[cfg(debug_assertions)]
                            debug_assert!(sts2_kernel::hooks::is_eligible_hook_listener(
                                state,
                                l.to_hook_listener()
                            ));
                            this.dispatch_after_card_played_listener_fast(
                                state,
                                l,
                                cp,
                                played_card_type,
                                args,
                            )?;
                        } else {
                            this.dispatch_cached_listener_if_eligible_monomorphic(
                                state,
                                l,
                                HookSlot::AfterCardPlayed,
                                args,
                            )?;
                        }
                    }
                    Ok(true)
                }
                return dispatch_after_card_played_bits_cached(self, state, bits, cp, args);
            }
        }
        let rev0 = state.hook_cache.revision;
        let idx0 = bits.trailing_zeros() as usize;
        bits &= bits - 1;
        debug_assert!(idx0 < state.hook_cache.flat_listeners.len());
        let l0 = unsafe { *state.hook_cache.flat_listeners.get_unchecked(idx0) };
        if bits == 0 {
            let _ = self.dispatch_cached_listener_monomorphic(state, l0, slot, args)?;
            return Ok(true);
        }
        let idx1 = bits.trailing_zeros() as usize;
        bits &= bits - 1;
        debug_assert!(idx1 < state.hook_cache.flat_listeners.len());
        let l1 = unsafe { *state.hook_cache.flat_listeners.get_unchecked(idx1) };
        if bits == 0 {
            let _ = self.dispatch_cached_listener_monomorphic(state, l0, slot, args)?;
            if state.hook_cache.dirty_mask == 0 && state.hook_cache.revision == rev0 {
                #[cfg(debug_assertions)]
                debug_assert!(sts2_kernel::hooks::is_eligible_hook_listener(
                    state,
                    l1.to_hook_listener()
                ));
                let _ = self.dispatch_cached_listener_monomorphic(state, l1, slot, args)?;
            } else {
                self.dispatch_cached_listener_if_eligible_monomorphic(state, l1, slot, args)?;
            }
            return Ok(true);
        }
        let idx2 = bits.trailing_zeros() as usize;
        bits &= bits - 1;
        debug_assert!(idx2 < state.hook_cache.flat_listeners.len());
        let l2 = unsafe { *state.hook_cache.flat_listeners.get_unchecked(idx2) };
        if bits == 0 {
            let _ = self.dispatch_cached_listener_monomorphic(state, l0, slot, args)?;
            if state.hook_cache.dirty_mask == 0 && state.hook_cache.revision == rev0 {
                #[cfg(debug_assertions)]
                debug_assert!(sts2_kernel::hooks::is_eligible_hook_listener(
                    state,
                    l1.to_hook_listener()
                ));
                let _ = self.dispatch_cached_listener_monomorphic(state, l1, slot, args)?;
            } else {
                self.dispatch_cached_listener_if_eligible_monomorphic(state, l1, slot, args)?;
            }
            if state.hook_cache.dirty_mask == 0 && state.hook_cache.revision == rev0 {
                #[cfg(debug_assertions)]
                debug_assert!(sts2_kernel::hooks::is_eligible_hook_listener(
                    state,
                    l2.to_hook_listener()
                ));
                let _ = self.dispatch_cached_listener_monomorphic(state, l2, slot, args)?;
            } else {
                self.dispatch_cached_listener_if_eligible_monomorphic(state, l2, slot, args)?;
            }
            return Ok(true);
        }
        let mut rest = ContentListenerBuf::new();
        rest.push(l1);
        rest.push(l2);
        while bits != 0 {
            let idx = bits.trailing_zeros() as usize;
            bits &= bits - 1;
            debug_assert!(idx < state.hook_cache.flat_listeners.len());
            rest.push(unsafe { *state.hook_cache.flat_listeners.get_unchecked(idx) });
        }
        let _ = self.dispatch_cached_listener_monomorphic(state, l0, slot, args)?;
        for &l in rest.as_slice() {
            if state.hook_cache.dirty_mask == 0 && state.hook_cache.revision == rev0 {
                #[cfg(debug_assertions)]
                debug_assert!(sts2_kernel::hooks::is_eligible_hook_listener(
                    state,
                    l.to_hook_listener()
                ));
                let _ = self.dispatch_cached_listener_monomorphic(state, l, slot, args)?;
            } else {
                self.dispatch_cached_listener_if_eligible_monomorphic(state, l, slot, args)?;
            }
        }
        Ok(true)
    }

    #[inline]
    fn dispatch_void_flat_multiword_cached(
        &self,
        state: &mut BattleState,
        slot: HookSlot,
        args: &HookArgs<'_>,
    ) -> Result<bool, KernelError> {
        let words = state.hook_cache.flat_slot_words as usize;
        if words == 0 {
            return Ok(false);
        }
        if slot == HookSlot::AfterCardPlayed {
            if let HookArgs::CardPlay(cp) = args {
                #[inline(never)]
                fn dispatch_after_card_played_multiword_cached(
                    this: &Sts2Content,
                    state: &mut BattleState,
                    words: usize,
                    cp: &CardPlayContext,
                    args: &HookArgs<'_>,
                ) -> Result<bool, KernelError> {
                    let rev0 = state.hook_cache.revision;
                    let played_card_type = state.card(cp.card).map(|c| {
                        let idx = c.id as usize;
                        debug_assert!(idx < CONTENT_TABLES.card_metas.len());
                        unsafe { CONTENT_TABLES.card_metas.get_unchecked(idx).card_type }
                    });
                    let slot_idx = HookSlot::AfterCardPlayed as usize;
                    let mut count = 0usize;
                    let mut l0 = CachedHookListener::Relic(0);
                    let mut buf = ContentListenerBuf::new();
                    {
                        let cache = &state.hook_cache;
                        for lw in 0..words {
                            let mut bits = cache.slot_listener_mask_word(slot_idx, lw);
                            let base = lw << 6;
                            while bits != 0 {
                                let idx = base + bits.trailing_zeros() as usize;
                                bits &= bits - 1;
                                debug_assert!(idx < cache.flat_listeners.len());
                                let l = unsafe { *cache.flat_listeners.get_unchecked(idx) };
                                if count == 0 {
                                    l0 = l;
                                } else {
                                    buf.push(l);
                                }
                                count += 1;
                            }
                        }
                    }
                    if count == 0 {
                        return Ok(true);
                    }
                    this.dispatch_after_card_played_listener_fast(
                        state,
                        l0,
                        cp,
                        played_card_type,
                        args,
                    )?;
                    for &l in buf.as_slice() {
                        if state.hook_cache.dirty_mask == 0 && state.hook_cache.revision == rev0 {
                            #[cfg(debug_assertions)]
                            debug_assert!(sts2_kernel::hooks::is_eligible_hook_listener(
                                state,
                                l.to_hook_listener()
                            ));
                            this.dispatch_after_card_played_listener_fast(
                                state,
                                l,
                                cp,
                                played_card_type,
                                args,
                            )?;
                        } else {
                            this.dispatch_cached_listener_if_eligible_monomorphic(
                                state,
                                l,
                                HookSlot::AfterCardPlayed,
                                args,
                            )?;
                        }
                    }
                    Ok(true)
                }
                return dispatch_after_card_played_multiword_cached(self, state, words, cp, args);
            }
        }
        let rev0 = state.hook_cache.revision;
        let slot_idx = slot as usize;
        let mut count = 0usize;
        let mut l0 = CachedHookListener::Relic(0);
        let mut rest = ContentListenerBuf::new();
        {
            let cache = &state.hook_cache;
            for lw in 0..words {
                let mut bits = cache.slot_listener_mask_word(slot_idx, lw);
                let base = lw << 6;
                while bits != 0 {
                    let idx = base + bits.trailing_zeros() as usize;
                    bits &= bits - 1;
                    debug_assert!(idx < cache.flat_listeners.len());
                    let l = unsafe { *cache.flat_listeners.get_unchecked(idx) };
                    if count == 0 {
                        l0 = l;
                    } else {
                        rest.push(l);
                    }
                    count += 1;
                }
            }
        }
        if count == 0 {
            return Ok(true);
        }
        let _ = self.dispatch_cached_listener_monomorphic(state, l0, slot, args)?;
        for &l in rest.as_slice() {
            if state.hook_cache.dirty_mask == 0 && state.hook_cache.revision == rev0 {
                #[cfg(debug_assertions)]
                debug_assert!(sts2_kernel::hooks::is_eligible_hook_listener(
                    state,
                    l.to_hook_listener()
                ));
                let _ = self.dispatch_cached_listener_monomorphic(state, l, slot, args)?;
            } else {
                self.dispatch_cached_listener_if_eligible_monomorphic(state, l, slot, args)?;
            }
        }
        Ok(true)
    }

    #[inline]
    fn apply_modify_hp_lost_slots_cached(
        &self,
        state: &mut BattleState,
        target: CreatureHandle,
        amount: Dec,
        props: ValueProp,
        dealer: Option<CreatureHandle>,
        card_source: Option<CardHandle>,
        phase: HpLossPhase,
    ) -> Result<Option<(Dec, SmallVec<[HookListener; 4]>)>, KernelError> {
        let comb = state.hook_cache.masks.combat_combined;
        let words = state.hook_cache.flat_slot_words as usize;
        let mut result = amount;
        let mut changed = SmallVec::new();
        let (early_slot, late_slot) = match phase {
            HpLossPhase::BeforeOsty => (
                HookSlot::ModifyHpLostBeforeOsty,
                HookSlot::ModifyHpLostBeforeOstyLate,
            ),
            HpLossPhase::AfterOsty => (
                HookSlot::ModifyHpLostAfterOsty,
                HookSlot::ModifyHpLostAfterOstyLate,
            ),
        };
        if comb.contains(early_slot) {
            for lw in 0..words {
                let lbase = lw << 6;
                let mut bits = state.hook_cache.slot_listener_mask_word(early_slot as usize, lw);
                while bits != 0 {
                    let idx = lbase + bits.trailing_zeros() as usize;
                    bits &= bits - 1;
                    let listener = state.hook_cache.flat_listeners[idx];
                    let before = result;
                    let res = self.dispatch_cached_listener_monomorphic(
                        state,
                        listener,
                        early_slot,
                        &HookArgs::ModifyHpLost {
                            target,
                            amount: result,
                            props,
                            dealer,
                            card_source,
                        },
                    )?;
                    #[cfg(debug_assertions)]
                    debug_assert_eq!(
                        state.hook_cache.dirty_mask, 0,
                        "{:?} mutated hook_cache during {:?}",
                        early_slot, listener
                    );
                    if let Some(next) = res.dec_val {
                        result = next;
                        if before.trunc() != result.trunc() {
                            changed.push(listener.to_hook_listener());
                        }
                    }
                }
            }
        }
        if comb.contains(late_slot) {
            for lw in 0..words {
                let lbase = lw << 6;
                let mut bits = state.hook_cache.slot_listener_mask_word(late_slot as usize, lw);
                while bits != 0 {
                    let idx = lbase + bits.trailing_zeros() as usize;
                    bits &= bits - 1;
                    let listener = state.hook_cache.flat_listeners[idx];
                    let before = result;
                    let res = self.dispatch_cached_listener_monomorphic(
                        state,
                        listener,
                        late_slot,
                        &HookArgs::ModifyHpLost {
                            target,
                            amount: result,
                            props,
                            dealer,
                            card_source,
                        },
                    )?;
                    #[cfg(debug_assertions)]
                    debug_assert_eq!(
                        state.hook_cache.dirty_mask, 0,
                        "{:?} mutated hook_cache during {:?}",
                        late_slot, listener
                    );
                    if let Some(next) = res.dec_val {
                        result = next;
                        if before.trunc() != result.trunc() {
                            changed.push(listener.to_hook_listener());
                        }
                    }
                }
            }
        }
        Ok(Some((result, changed)))
    }

    #[inline]
    fn apply_modify_block_slots_cached(
        &self,
        state: &mut BattleState,
        target: CreatureHandle,
        block: Dec,
        props: ValueProp,
        card_source: Option<CardHandle>,
        card_play: Option<CardPlayContext>,
    ) -> Result<Option<(Dec, SmallVec<[HookListener; 4]>)>, KernelError> {
        let comb = state.hook_cache.masks.combat_combined;
        let words = state.hook_cache.flat_slot_words as usize;
        let mut result = block;
        let mut changed = SmallVec::new();
        if comb.contains(HookSlot::ModifyBlockAdditive) {
            for lw in 0..words {
                let lbase = lw << 6;
                let mut bits = if lw == 0 {
                    state
                        .hook_cache
                        .slot_listener_mask(HookSlot::ModifyBlockAdditive as usize)
                } else {
                    state
                        .hook_cache
                        .slot_listener_mask_word(HookSlot::ModifyBlockAdditive as usize, lw)
                };
                while bits != 0 {
                    let idx = lbase + bits.trailing_zeros() as usize;
                    bits &= bits - 1;
                    debug_assert!(idx < state.hook_cache.flat_listeners.len());
                    let listener = unsafe { *state.hook_cache.flat_listeners.get_unchecked(idx) };
                    if let CachedHookListener::Power {
                        creature,
                        power_handle,
                        creature_idx,
                        power_idx,
                    } = listener
                    {
                        if let Some((pi, power, owner_side)) = resolve_cached_power_ref(
                            state,
                            creature,
                            power_handle,
                            creature_idx,
                            power_idx,
                        ) {
                            if power.id == PowerId::DexterityPower {
                                let owned_source = if card_source.is_some() {
                                    creature == state.player.creature_handle
                                } else {
                                    target == creature
                                };
                                let fast_delta =
                                    if owned_source && !props.contains(ValueProp::UNPOWERED) {
                                        Some(Dec::from_i32(power.amount))
                                    } else {
                                        None
                                    };
                                #[cfg(debug_assertions)]
                                {
                                    let p_copy = *power;
                                    let exp = powers::dispatch_hook_resolved(
                                        self,
                                        state,
                                        creature,
                                        pi,
                                        p_copy,
                                        owner_side,
                                        HookSlot::ModifyBlockAdditive,
                                        &HookArgs::ModifyBlock {
                                            target,
                                            amount: result,
                                            props,
                                            card_source,
                                            card_play,
                                        },
                                    )?;
                                    debug_assert_eq!(exp.dec_val, fast_delta);
                                }
                                let _ = (pi, owner_side);
                                if let Some(delta) = fast_delta {
                                    result = result.checked_add(delta)?;
                                    if !delta.is_zero() {
                                        changed.push(listener.to_hook_listener());
                                    }
                                }
                                continue;
                            }
                            let p_copy = *power;
                            let res = powers::dispatch_hook_resolved(
                                self,
                                state,
                                creature,
                                pi,
                                p_copy,
                                owner_side,
                                HookSlot::ModifyBlockAdditive,
                                &HookArgs::ModifyBlock {
                                    target,
                                    amount: result,
                                    props,
                                    card_source,
                                    card_play,
                                },
                            )?;
                            #[cfg(debug_assertions)]
                            debug_assert_eq!(
                                state.hook_cache.dirty_mask, 0,
                                "ModifyBlockAdditive mutated hook_cache during {:?}",
                                listener
                            );
                            if let Some(delta) = res.dec_val {
                                result = result.checked_add(delta)?;
                                if !delta.is_zero() {
                                    changed.push(listener.to_hook_listener());
                                }
                            }
                        }
                        continue;
                    }
                    let res = self.dispatch_cached_non_power_monomorphic(
                        state,
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
                    #[cfg(debug_assertions)]
                    debug_assert_eq!(
                        state.hook_cache.dirty_mask, 0,
                        "ModifyBlockAdditive mutated hook_cache during {:?}",
                        listener
                    );
                    if let Some(delta) = res.dec_val {
                        result = result.checked_add(delta)?;
                        if !delta.is_zero() {
                            changed.push(listener.to_hook_listener());
                        }
                    }
                }
            }
        }
        if comb.contains(HookSlot::ModifyBlockMultiplicative) {
            for lw in 0..words {
                let lbase = lw << 6;
                let mut bits = if lw == 0 {
                    state
                        .hook_cache
                        .slot_listener_mask(HookSlot::ModifyBlockMultiplicative as usize)
                } else {
                    state
                        .hook_cache
                        .slot_listener_mask_word(HookSlot::ModifyBlockMultiplicative as usize, lw)
                };
                while bits != 0 {
                    let idx = lbase + bits.trailing_zeros() as usize;
                    bits &= bits - 1;
                    debug_assert!(idx < state.hook_cache.flat_listeners.len());
                    let listener = unsafe { *state.hook_cache.flat_listeners.get_unchecked(idx) };
                    if let CachedHookListener::Power {
                        creature,
                        power_handle,
                        creature_idx,
                        power_idx,
                    } = listener
                    {
                        if let Some((pi, power, owner_side)) = resolve_cached_power_ref(
                            state,
                            creature,
                            power_handle,
                            creature_idx,
                            power_idx,
                        ) {
                            if power.id == PowerId::FrailPower {
                                let fast_factor = if target == creature && props.is_powered_block()
                                {
                                    Some(Dec::THREE_QUARTERS)
                                } else {
                                    None
                                };
                                #[cfg(debug_assertions)]
                                {
                                    let p_copy = *power;
                                    let exp = powers::dispatch_hook_resolved(
                                        self,
                                        state,
                                        creature,
                                        pi,
                                        p_copy,
                                        owner_side,
                                        HookSlot::ModifyBlockMultiplicative,
                                        &HookArgs::ModifyBlock {
                                            target,
                                            amount: result,
                                            props,
                                            card_source,
                                            card_play,
                                        },
                                    )?;
                                    debug_assert_eq!(exp.dec_val, fast_factor);
                                }
                                let _ = (pi, owner_side);
                                if let Some(factor) = fast_factor {
                                    result = result.checked_mul(factor)?;
                                    changed.push(listener.to_hook_listener());
                                }
                                continue;
                            }
                            let p_copy = *power;
                            let res = powers::dispatch_hook_resolved(
                                self,
                                state,
                                creature,
                                pi,
                                p_copy,
                                owner_side,
                                HookSlot::ModifyBlockMultiplicative,
                                &HookArgs::ModifyBlock {
                                    target,
                                    amount: result,
                                    props,
                                    card_source,
                                    card_play,
                                },
                            )?;
                            #[cfg(debug_assertions)]
                            debug_assert_eq!(
                                state.hook_cache.dirty_mask, 0,
                                "ModifyBlockMultiplicative mutated hook_cache during {:?}",
                                listener
                            );
                            if let Some(factor) = res.dec_val {
                                result = result.checked_mul(factor)?;
                                if factor != Dec::ONE {
                                    changed.push(listener.to_hook_listener());
                                }
                            }
                        }
                        continue;
                    }
                    let res = self.dispatch_cached_non_power_monomorphic(
                        state,
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
                    #[cfg(debug_assertions)]
                    debug_assert_eq!(
                        state.hook_cache.dirty_mask, 0,
                        "ModifyBlockMultiplicative mutated hook_cache during {:?}",
                        listener
                    );
                    if let Some(factor) = res.dec_val {
                        result = result.checked_mul(factor)?;
                        if factor != Dec::ONE {
                            changed.push(listener.to_hook_listener());
                        }
                    }
                }
            }
        }
        Ok(Some((result.max(Dec::ZERO), changed)))
    }
}
