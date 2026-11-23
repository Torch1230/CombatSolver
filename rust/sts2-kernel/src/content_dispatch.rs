use crate::continuation::ChoiceRequest;
use crate::error::KernelError;
use crate::state::{BattleState, CardHandle, CreatureHandle, PowerInst};
use smallvec::SmallVec;
use sts2_types::{
    CardId, CardKeywords, CardMeta, CardPilePosition, CardStaticFlags, CardType, Dec, DominionId,
    EnchantmentId, HookMask, HookSlot, MonsterId, MonsterMeta, OrbId, OrbMeta, PileType, PotionId,
    PotionMeta, PowerId, PowerMeta, RelicId, RelicMeta, RunModelId, TargetType, ValueProp,
};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum HookListener {
    Relic(usize),
    Potion(usize),
    Power {
        creature: CreatureHandle,
        power_handle: u32,
    },
    DetachedPower {
        creature: CreatureHandle,
        power: PowerInst,
    },
    Monster(CreatureHandle),
    Orb(usize),
    Card(CardHandle),
    Enchantment(CardHandle),
    Affliction(CardHandle),
    RunModel(RunModelId),
    Dominion(DominionId),
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CardPlayContext {
    pub card: CardHandle,
    pub target: Option<CreatureHandle>,
    pub result_pile: PileType,
    pub is_auto_play: bool,
    pub play_index: i32,
    pub play_count: i32,
    pub play_ordinal: i32,
    pub energy_spent: i32,
    pub energy_value: i32,
    pub stars_spent: i32,
    pub star_value: i32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct AttackContextInfo {
    pub attack_id: u64,
    pub attacker: CreatureHandle,
    pub target_side: sts2_types::CombatSide,
    pub card_source: Option<CardHandle>,
    pub damage_props: ValueProp,
    pub hit_count: i32,
}

#[derive(Clone, Debug)]
pub enum HookArgs<'a> {
    None,
    Creature(CreatureHandle),
    Player,
    ModifyDamage {
        target: Option<CreatureHandle>,
        dealer: Option<CreatureHandle>,
        amount: Dec,
        props: ValueProp,
        card_source: Option<CardHandle>,
        card_play: Option<CardPlayContext>,
    },
    ModifyHpLost {
        target: CreatureHandle,
        amount: Dec,
        props: ValueProp,
        dealer: Option<CreatureHandle>,
        card_source: Option<CardHandle>,
    },
    ModifyBlock {
        target: CreatureHandle,
        amount: Dec,
        props: ValueProp,
        card_source: Option<CardHandle>,
        card_play: Option<CardPlayContext>,
    },
    ModifyUnblockedTarget {
        target: CreatureHandle,
        amount: Dec,
        props: ValueProp,
        dealer: Option<CreatureHandle>,
    },
    ModifyHandDraw {
        original_count: Dec,
    },
    ModifyOrbValue {
        orb: OrbId,
        amount: Dec,
    },
    ModifyOrbPassiveTriggerCount {
        orb: OrbId,
        orb_idx: usize,
        trigger_count: i32,
    },
    SideTurn {
        side: sts2_types::CombatSide,
        participants: &'a [CreatureHandle],
    },
    Attack {
        info: AttackContextInfo,
        results: &'a [crate::state::DamageResult],
    },
    CardPlay(CardPlayContext),
    CardOnly(CardHandle),
    ShouldDraw {
        from_hand_draw: bool,
    },
    CardDrawn {
        card: CardHandle,
        from_hand_draw: bool,
    },
    CardExhausted {
        card: CardHandle,
        caused_by_ethereal: bool,
    },
    CardGenerated {
        card: CardHandle,
        creator: Option<CreatureHandle>,
    },
    CardPileChanged {
        card: CardHandle,
        old_pile: PileType,
    },
    CardCost {
        card: CardHandle,
        cost: Dec,
    },
    CardPlayCount {
        card: CardHandle,
        target: Option<CreatureHandle>,
        play_count: i32,
    },
    CardPlayResultLocation {
        card: CardHandle,
        is_auto_play: bool,
        energy_value: i32,
        pile: PileType,
        position: CardPilePosition,
    },
    PowerChanged {
        power_id: PowerId,
        power_handle: u32,
        owner: CreatureHandle,
        amount: Dec,
        applier: Option<CreatureHandle>,
        card_source: Option<CardHandle>,
    },
    PowerQuery {
        owner: CreatureHandle,
        power_id: PowerId,
        power_handle: u32,
        power_type: sts2_types::PowerType,
        is_temporary: bool,
    },
    DamageResult {
        target: CreatureHandle,
        dealer: Option<CreatureHandle>,
        props: ValueProp,
        blocked_damage: i32,
        unblocked_damage: i32,
        was_target_killed: bool,
        was_fully_blocked: bool,
        card_source: Option<CardHandle>,
    },
    OrbEvoked {
        orb: OrbId,
        targets: &'a [CreatureHandle],
    },
    ArrowShot {
        arrow_idx: Option<usize>,
        card_source: Option<CardHandle>,
    },
    ModifyXValue {
        card: CardHandle,
        amount: i32,
    },
    BlockBroken {
        target: CreatureHandle,
        breaker: Option<CreatureHandle>,
    },
    AfterDeath {
        target: CreatureHandle,
        card_source: Option<CardHandle>,
        was_removal_prevented: bool,
    },
    ModifySummonAmount {
        amount: Dec,
    },
    Potion {
        potion_id: PotionId,
        target: Option<CreatureHandle>,
    },
    Forge {
        amount: Dec,
    },
}

#[derive(Clone, Debug, Default)]
pub struct HookResult {
    pub bool_val: Option<bool>,
    pub dec_val: Option<Dec>,
    pub int_val: Option<i32>,
    pub creature_val: Option<CreatureHandle>,
    pub location_val: Option<(PileType, CardPilePosition)>,
    pub modified: bool,
    pub keywords: Option<CardKeywords>,
    pub needs_choice: Option<ChoiceRequest>,
}

#[derive(Clone, Debug, Default)]
pub struct CardPlayOutcome {
    pub needs_choice: Option<(ChoiceRequest, u8)>,
    pub transform_into: Option<CardId>,
}

/// Authoritative bridge from `sts2-kernel` into `sts2-content` (or any custom test dispatcher).
pub trait ContentDispatcher {
    fn card_meta(&self, id: CardId) -> Result<&'static CardMeta, KernelError>;
    fn power_meta(&self, id: PowerId) -> Result<&'static PowerMeta, KernelError>;
    fn relic_meta(&self, id: RelicId) -> Result<&'static RelicMeta, KernelError>;
    fn potion_meta(&self, id: PotionId) -> Result<&'static PotionMeta, KernelError>;
    fn monster_meta(&self, id: MonsterId) -> Result<&'static MonsterMeta, KernelError>;
    fn orb_meta(&self, id: OrbId) -> Result<&'static OrbMeta, KernelError>;

    fn card_is_playable(&self, state: &BattleState, card: CardHandle) -> Result<bool, KernelError>;

    fn card_on_play(
        &self,
        state: &mut BattleState,
        ctx: &CardPlayContext,
        choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
        resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError>;

    fn card_on_play_resume_after_select(
        &self,
        state: &mut BattleState,
        ctx: &CardPlayContext,
        selected: &[CardHandle],
        resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError>;

    fn card_on_upgrade(&self, state: &mut BattleState, card: CardHandle) -> Result<(), KernelError>;

    fn card_on_downgrade(&self, _state: &mut BattleState, _card: CardHandle) -> Result<(), KernelError> {
        Ok(())
    }

    fn card_on_turn_end_in_hand(
        &self,
        state: &mut BattleState,
        card: CardHandle,
    ) -> Result<(), KernelError> {
        let id = state
            .card(card)
            .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
            .id;
        if self
            .card_meta(id)?
            .flags
            .contains(CardStaticFlags::HAS_TURN_END_IN_HAND)
        {
            return Err(KernelError::Unsupported {
                kind: "CardTurnEndInHand",
                id: id.model_id_str().to_owned(),
            });
        }
        Ok(())
    }

    fn card_resolved_type(
        &self,
        state: &BattleState,
        card: CardHandle,
    ) -> Result<CardType, KernelError> {
        let id = state
            .card(card)
            .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
            .id;
        Ok(self.card_meta(id)?.card_type)
    }

    fn card_resolved_target_type(
        &self,
        state: &BattleState,
        card: CardHandle,
    ) -> Result<TargetType, KernelError> {
        let id = state
            .card(card)
            .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
            .id;
        Ok(self.card_meta(id)?.target_type)
    }

    fn card_default_result_location(
        &self,
        _state: &BattleState,
        _card: CardHandle,
        _is_auto_play: bool,
    ) -> Result<Option<(PileType, CardPilePosition)>, KernelError> {
        Ok(None)
    }

    fn card_thrash_damage_variable(
        &self,
        _state: &BattleState,
        _card: CardHandle,
    ) -> Result<Dec, KernelError> {
        Ok(Dec::ZERO)
    }

    fn card_comparison_base_values(
        &self,
        state: &BattleState,
        card: CardHandle,
    ) -> Result<SmallVec<[(&'static str, Dec); 4]>, KernelError>;

    fn monster_initial_hp_range(
        &self,
        state: &BattleState,
        monster_id: MonsterId,
    ) -> Result<(i32, i32), KernelError>;

    fn monster_setup_for_combat(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
    ) -> Result<(), KernelError>;

    fn monster_after_added_to_room(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
    ) -> Result<(), KernelError>;

    fn monster_roll_move(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
    ) -> Result<(), KernelError>;

    fn monster_perform_move(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
    ) -> Result<(), KernelError>;

    fn monster_resume_move_choice(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
        _selected: &[CardHandle],
    ) -> Result<(), KernelError> {
        let id = state
            .creature(creature)
            .and_then(|c| c.monster_id)
            .map(|m| m.model_id_str().to_owned())
            .unwrap_or_else(|| "unknown".to_owned());
        Err(KernelError::Unsupported {
            kind: "MonsterMoveChoiceResume",
            id,
        })
    }

    fn monster_attack_intent_hits(
        &self,
        _state: &BattleState,
        _creature: CreatureHandle,
    ) -> Result<SmallVec<[(Dec, i32); 2]>, KernelError> {
        Ok(SmallVec::new())
    }

    fn monster_intends_attack(
        &self,
        state: &BattleState,
        creature: CreatureHandle,
    ) -> Result<bool, KernelError> {
        Ok(!self.monster_attack_intent_hits(state, creature)?.is_empty())
    }

    fn orb_passive(
        &self,
        state: &mut BattleState,
        orb_idx: usize,
        target: Option<CreatureHandle>,
    ) -> Result<SmallVec<[CreatureHandle; 4]>, KernelError>;

    fn orb_evoke(
        &self,
        state: &mut BattleState,
        orb_idx: usize,
    ) -> Result<SmallVec<[CreatureHandle; 4]>, KernelError>;

    fn orb_before_turn_end(&self, state: &mut BattleState, orb_idx: usize) -> Result<(), KernelError>;

    fn orb_after_turn_start(&self, state: &mut BattleState, orb_idx: usize) -> Result<(), KernelError>;

    fn potion_on_use(
        &self,
        state: &mut BattleState,
        potion_id: PotionId,
        target: Option<CreatureHandle>,
        choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
        resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError>;

    fn potion_on_use_resume_after_select(
        &self,
        state: &mut BattleState,
        potion_id: PotionId,
        target: Option<CreatureHandle>,
        selected: &[CardHandle],
        resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError>;

    fn resume_hook_choice(
        &self,
        state: &mut BattleState,
        listener: HookListener,
        slot: HookSlot,
        selected: &[CardHandle],
    ) -> Result<HookResult, KernelError>;

    fn enchantment_on_play(
        &self,
        state: &mut BattleState,
        card: CardHandle,
        ctx: &CardPlayContext,
    ) -> Result<(), KernelError>;

    fn affliction_on_play(
        &self,
        state: &mut BattleState,
        card: CardHandle,
        ctx: &CardPlayContext,
    ) -> Result<(), KernelError>;

    fn enchantment_play_count(
        &self,
        state: &BattleState,
        card: CardHandle,
        play_count: i32,
    ) -> Result<i32, KernelError>;

    fn enchantment_damage_additive(
        &self,
        state: &BattleState,
        card: CardHandle,
        amount: Dec,
        props: ValueProp,
    ) -> Result<Dec, KernelError>;

    fn enchantment_damage_multiplicative(
        &self,
        state: &BattleState,
        card: CardHandle,
        amount: Dec,
        props: ValueProp,
    ) -> Result<Dec, KernelError>;

    fn enchantment_should_start_at_bottom(&self, id: EnchantmentId) -> Result<bool, KernelError>;

    fn enchantment_modify_shuffle_order(
        &self,
        state: &BattleState,
        card: CardHandle,
        cards: &mut Vec<CardHandle>,
        is_initial_shuffle: bool,
    ) -> Result<(), KernelError>;

    fn power_should_remove_due_to_amount(
        &self,
        power: &PowerInst,
    ) -> Result<bool, KernelError> {
        let meta = self.power_meta(power.id)?;
        let allow_neg = meta
            .flags
            .contains(sts2_types::PowerStaticFlags::ALLOW_NEGATIVE);
        Ok(if allow_neg {
            power.amount == 0
        } else {
            power.amount <= 0
        })
    }

    fn listener_hook_mask(
        &self,
        state: &BattleState,
        listener: HookListener,
    ) -> Result<HookMask, KernelError>;

    fn dispatch_hook(
        &self,
        state: &mut BattleState,
        listener: HookListener,
        slot: HookSlot,
        args: &HookArgs<'_>,
    ) -> Result<HookResult, KernelError>;
}
