pub mod cards;
pub mod dread_blitz;
pub mod monsters;
pub mod orbs_enchantments_afflictions;
pub mod potions;
pub mod powers;
pub mod relics;

use smallvec::SmallVec;
use sts2_kernel::{
    BattleState, CardHandle, ContentDispatcher, CreatureHandle, HookArgs, HookListener, HookResult,
    KernelError,
};
use sts2_kernel::content_dispatch::{CardPlayContext, CardPlayOutcome};
use sts2_types::{
    CardId, CardMeta, CardPilePosition, CardType, Dec, EnchantmentId, HookMask, HookSlot,
    MonsterId, MonsterMeta, OrbId, OrbMeta, PileType, PotionId, PotionMeta, PowerId, PowerMeta,
    RelicId, RelicMeta, TargetType, ValueProp,
};

/// Authoritative Wave 0 content dispatcher; Wave 1/2 modules plug directly into the per-category routers.
#[derive(Clone, Copy, Debug, Default)]
pub struct Sts2Content;

impl ContentDispatcher for Sts2Content {
    fn card_meta(&self, id: CardId) -> Result<&'static CardMeta, KernelError> {
        cards::card_meta(id)
    }

    fn power_meta(&self, id: PowerId) -> Result<&'static PowerMeta, KernelError> {
        powers::power_meta(id)
    }

    fn relic_meta(&self, id: RelicId) -> Result<&'static RelicMeta, KernelError> {
        relics::relic_meta(id)
    }

    fn potion_meta(&self, id: PotionId) -> Result<&'static PotionMeta, KernelError> {
        potions::potion_meta(id)
    }

    fn monster_meta(&self, id: MonsterId) -> Result<&'static MonsterMeta, KernelError> {
        monsters::monster_meta(id)
    }

    fn orb_meta(&self, id: OrbId) -> Result<&'static OrbMeta, KernelError> {
        orbs_enchantments_afflictions::orb_meta(id)
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

    fn card_resolved_type(
        &self,
        state: &BattleState,
        card: CardHandle,
    ) -> Result<CardType, KernelError> {
        cards::card_resolved_type(state, card)
    }

    fn card_resolved_target_type(
        &self,
        state: &BattleState,
        card: CardHandle,
    ) -> Result<TargetType, KernelError> {
        cards::card_resolved_target_type(state, card)
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
        cards: &mut Vec<CardHandle>,
        is_initial_shuffle: bool,
    ) -> Result<(), KernelError> {
        orbs_enchantments_afflictions::enchantment_modify_shuffle_order(
            state,
            card,
            cards,
            is_initial_shuffle,
        )
    }

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
                Ok(relics::relic_meta(id)?.hook_mask)
            }
            HookListener::Potion(idx) => {
                let id = state
                    .player
                    .potion_slots
                    .get(idx)
                    .and_then(|s| *s)
                    .ok_or_else(|| KernelError::InvalidState("missing potion".to_owned()))?;
                Ok(potions::potion_meta(id)?.hook_mask)
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
                Ok(powers::power_meta(id)?.hook_mask)
            }
            HookListener::DetachedPower { power, .. } => Ok(powers::power_meta(power.id)?.hook_mask),
            HookListener::Monster(ch) => {
                let id = state
                    .creature(ch)
                    .and_then(|c| c.monster_id)
                    .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?;
                Ok(monsters::monster_meta(id)?.hook_mask)
            }
            HookListener::Orb(idx) => {
                let id = state
                    .player
                    .orbs
                    .get(idx)
                    .ok_or_else(|| KernelError::InvalidState("missing orb".to_owned()))?
                    .id;
                Ok(orbs_enchantments_afflictions::orb_meta(id)?.hook_mask)
            }
            HookListener::Card(h) => {
                let id = state
                    .card(h)
                    .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
                    .id;
                Ok(cards::card_meta(id)?.hook_mask)
            }
            HookListener::Enchantment(h) => {
                let id = state
                    .card(h)
                    .and_then(|c| state.card_ext(c.ext_index))
                    .and_then(|e| e.enchantment.as_ref().map(|x| x.id))
                    .ok_or_else(|| KernelError::InvalidState("missing enchantment".to_owned()))?;
                orbs_enchantments_afflictions::enchantment_hook_mask(id)
            }
            HookListener::Affliction(h) => {
                let id = state
                    .card(h)
                    .and_then(|c| c.affliction.map(|a| a.id))
                    .ok_or_else(|| KernelError::InvalidState("missing affliction".to_owned()))?;
                orbs_enchantments_afflictions::affliction_hook_mask(id)
            }
            HookListener::RunModel(id) => dread_blitz::run_model_hook_mask(id),
            HookListener::Dominion(id) => dread_blitz::dominion_hook_mask(id),
        }
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
