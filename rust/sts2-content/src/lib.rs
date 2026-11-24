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
use sts2_kernel::{
    BattleState, CardHandle, CardRolloutProfile, CategoryHookMasks, ContentDispatcher,
    CreatureHandle, HookArgs, HookListener, HookMaskTableView, HookResult, KernelError, PowerInst,
};
use sts2_kernel::content_dispatch::{CardPlayContext, CardPlayOutcome};
use sts2_types::{
    AfflictionId, CardId, CardMeta, CardPilePosition, CardType, CombatSide, Dec, EnchantmentId,
    HookMask, HookSlot, MonsterId, MonsterMeta, OrbId, OrbMeta, PileType, PotionId, PotionMeta,
    PowerId, PowerMeta, RelicId, RelicMeta, TargetType, ValueProp,
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
}

static CONTENT_TABLES: LazyLock<ContentMetaTables> = LazyLock::new(|| {
    let card_metas: [CardMeta; CardId::COUNT] = std::array::from_fn(|idx| {
        let id: CardId = unsafe { core::mem::transmute(idx as u16) };
        *cards::card_meta(id).expect("valid card meta")
    });
    let mut cards = [HookMask::EMPTY; CardId::COUNT];
    let mut card_rollout_profiles = [CardRolloutProfile::default(); CardId::COUNT];
    let mut all_cards = HookMask::EMPTY;
    for (idx, meta) in card_metas.iter().enumerate() {
        let mask = meta.hook_mask;
        cards[idx] = mask;
        let id: CardId = unsafe { core::mem::transmute(idx as u16) };
        card_rollout_profiles[idx] = rollout_profiles::build_card_rollout_profile(id, meta);
        all_cards = all_cards.union(mask);
    }

    let power_metas: [PowerMeta; PowerId::COUNT] = std::array::from_fn(|idx| {
        let id: PowerId = unsafe { core::mem::transmute(idx as u16) };
        *powers::power_meta(id).expect("valid power meta")
    });
    let mut powers = [HookMask::EMPTY; PowerId::COUNT];
    let mut all_powers = HookMask::EMPTY;
    for (idx, meta) in power_metas.iter().enumerate() {
        let mask = meta.hook_mask;
        powers[idx] = mask;
        all_powers = all_powers.union(mask);
    }

    let relic_metas: [&'static RelicMeta; RelicId::COUNT] = std::array::from_fn(|idx| {
        let id: RelicId = unsafe { core::mem::transmute(idx as u16) };
        relics::relic_meta(id).expect("valid relic meta")
    });
    let mut relics = [HookMask::EMPTY; RelicId::COUNT];
    let mut all_relics = HookMask::EMPTY;
    for (idx, meta) in relic_metas.iter().enumerate() {
        let mask = meta.hook_mask;
        relics[idx] = mask;
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
    let mut all_monsters = HookMask::EMPTY;
    for (idx, meta) in monster_metas.iter().enumerate() {
        let mask = meta.hook_mask;
        monsters[idx] = mask;
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
    }
});

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
});

/// Authoritative Wave 0 content dispatcher; Wave 1/2 modules plug directly into the per-category routers.
#[derive(Clone, Copy, Debug, Default)]
pub struct Sts2Content;

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
}
