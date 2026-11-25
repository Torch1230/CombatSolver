use crate::continuation::ChoiceRequest;
use crate::error::KernelError;
use crate::state::{BattleState, CardHandle, CreatureHandle, PowerInst};
use smallvec::SmallVec;
use sts2_types::{
    AfflictionId, CardId, CardKeywords, CardMeta, CardPilePosition, CardStaticFlags, CardType,
    CombatSide, Dec, DominionId, EnchantmentId, HookMask, HookSlot, MonsterId, MonsterMeta, OrbId,
    OrbMeta, PileType, PotionId, PotionMeta, PowerId, PowerMeta, RelicId, RelicMeta, RunModelId,
    TargetType, ValueProp,
};

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct CategoryHookMasks {
    pub all_cards: HookMask,
    pub deck_cards: HookMask,
    pub all_enchantments: HookMask,
    pub all_afflictions: HookMask,
    pub all_potions: HookMask,
    pub all_orbs: HookMask,
    pub all_relics: HookMask,
    pub all_powers: HookMask,
    pub all_monsters: HookMask,
}

#[derive(Clone, Copy, Debug)]
pub struct HookMaskTableView {
    pub categories: CategoryHookMasks,
    pub cards: &'static [HookMask],
    pub powers: &'static [HookMask],
    pub relics: &'static [HookMask],
    pub potions: &'static [HookMask],
    pub monsters: &'static [HookMask],
    pub orbs: &'static [HookMask],
    pub enchantments: &'static [HookMask],
    pub afflictions: &'static [HookMask],
    pub card_metas: &'static [CardMeta],
    pub power_metas: &'static [PowerMeta],
    pub card_rollout_profiles: &'static [CardRolloutProfile],
    pub card_has_hook_bits: &'static [u64],
    pub power_has_hook_bits: &'static [u64],
    pub relic_has_hook_bits: &'static [u64],
    pub monster_has_hook_bits: &'static [u64],
    pub card_has_custom_playability_bits: &'static [u64],
    pub card_is_status_or_curse_bits: &'static [u64],
    pub card_generic_choice_quality: &'static [GenericChoiceQualityEntry],
    pub afflictions_self_card_entered_only: bool,
    pub keywords_in_combat_known_only: bool,
    pub unpowered_damage_no_card_surrounded_only: bool,
    pub unpowered_block_no_card_shadowmeld_only: bool,
    pub card_dynamic_type_mad_science_only: bool,
    pub card_custom_result_location_known_only: bool,
    pub modify_result_location_cards_self_only: bool,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
#[repr(C)]
pub struct GenericChoiceQualityEntry {
    pub q_cost0: i16,
    pub q_cost1: i16,
    pub q_cost2p: i16,
    pub flags: u8,
    pub upg_bonus: u8,
}

const _: () = assert!(std::mem::size_of::<GenericChoiceQualityEntry>() == 8);

#[inline(always)]
const fn bitset_test(bits: &[u64], idx: usize) -> bool {
    let word = idx >> 6;
    word < bits.len() && ((bits[word] >> (idx & 63)) & 1) != 0
}

impl HookMaskTableView {
    #[inline(always)]
    pub const fn card_has_hook(&self, id: CardId) -> bool {
        bitset_test(self.card_has_hook_bits, id as usize)
    }

    #[inline(always)]
    pub const fn power_has_hook(&self, id: PowerId) -> bool {
        bitset_test(self.power_has_hook_bits, id as usize)
    }

    #[inline(always)]
    pub const fn relic_has_hook(&self, id: RelicId) -> bool {
        bitset_test(self.relic_has_hook_bits, id as usize)
    }

    #[inline(always)]
    pub const fn monster_has_hook(&self, id: MonsterId) -> bool {
        bitset_test(self.monster_has_hook_bits, id as usize)
    }

    #[inline(always)]
    pub const fn card_has_custom_playability(&self, id: CardId) -> bool {
        bitset_test(self.card_has_custom_playability_bits, id as usize)
    }

    #[inline(always)]
    pub const fn card_is_status_or_curse(&self, id: CardId) -> bool {
        bitset_test(self.card_is_status_or_curse_bits, id as usize)
    }
}

pub mod rollout_op_flags {
    pub const HAS_DRAW: u8 = 1 << 0;
    pub const HAS_ENERGY_OR_STAR_GAIN: u8 = 1 << 1;
    pub const HAS_ENGINE_OP: u8 = 1 << 2;
    pub const HAS_SELF_BUFF: u8 = 1 << 3;
    pub const HAS_ENEMY_DEBUFF: u8 = 1 << 4;
    pub const HAS_SELF_HP_LOSS: u8 = 1 << 5;
    pub const HAS_ATTACK: u8 = 1 << 6;
    pub const HAS_BLOCK: u8 = 1 << 7;
}

pub mod rollout_meta_flags {
    pub const IS_X_ENERGY_COST: u8 = 1 << 0;
    pub const IS_X_STAR_COST: u8 = 1 << 1;
    pub const MAY_HEAL: u8 = 1 << 2;
    pub const GAINS_BLOCK: u8 = 1 << 3;
    pub const HAS_CUSTOM_PLAYABILITY: u8 = 1 << 4;
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(C)]
pub struct CardRolloutProfile {
    pub base_damage: i16,
    pub upg_damage: i16,
    pub base_block: i16,
    pub upg_block: i16,
    pub hits: u8,
    pub upg_hits: u8,
    pub setup_weight: u8,
    pub op_flags: u8,
    pub card_type: CardType,
    pub target_type: TargetType,
    pub canonical_star_cost: i8,
    pub meta_flags: u8,
}

impl Default for CardRolloutProfile {
    #[inline]
    fn default() -> Self {
        Self {
            base_damage: 0,
            upg_damage: 0,
            base_block: 0,
            upg_block: 0,
            hits: 0,
            upg_hits: 0,
            setup_weight: 0,
            op_flags: 0,
            card_type: CardType::None,
            target_type: TargetType::None,
            canonical_star_cost: 0,
            meta_flags: 0,
        }
    }
}

const _: () = assert!(std::mem::size_of::<CardRolloutProfile>() == 16);

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
pub enum CachedHookListener {
    Relic(u16),
    Potion(u16),
    Power {
        creature: CreatureHandle,
        power_handle: u32,
        creature_idx: u16,
        power_idx: u16,
    },
    Monster(CreatureHandle),
    Orb(u16),
    Card(CardHandle),
    Enchantment(CardHandle),
    Affliction(CardHandle),
    RunModel(RunModelId),
    Dominion(DominionId),
}

const _: () = assert!(std::mem::size_of::<CachedHookListener>() == 16);

impl CachedHookListener {
    #[inline(always)]
    pub const fn to_hook_listener(self) -> HookListener {
        match self {
            Self::Relic(idx) => HookListener::Relic(idx as usize),
            Self::Potion(idx) => HookListener::Potion(idx as usize),
            Self::Power {
                creature,
                power_handle,
                ..
            } => HookListener::Power {
                creature,
                power_handle,
            },
            Self::Monster(h) => HookListener::Monster(h),
            Self::Orb(idx) => HookListener::Orb(idx as usize),
            Self::Card(h) => HookListener::Card(h),
            Self::Enchantment(h) => HookListener::Enchantment(h),
            Self::Affliction(h) => HookListener::Affliction(h),
            Self::RunModel(id) => HookListener::RunModel(id),
            Self::Dominion(id) => HookListener::Dominion(id),
        }
    }

    #[inline(always)]
    pub const fn from_hook_listener(listener: HookListener) -> Option<Self> {
        match listener {
            HookListener::Relic(idx) => Some(Self::Relic(if idx <= u16::MAX as usize {
                idx as u16
            } else {
                u16::MAX
            })),
            HookListener::Potion(idx) => Some(Self::Potion(if idx <= u16::MAX as usize {
                idx as u16
            } else {
                u16::MAX
            })),
            HookListener::Power {
                creature,
                power_handle,
            } => Some(Self::Power {
                creature,
                power_handle,
                creature_idx: u16::MAX,
                power_idx: u16::MAX,
            }),
            HookListener::DetachedPower { .. } => None,
            HookListener::Monster(h) => Some(Self::Monster(h)),
            HookListener::Orb(idx) => Some(Self::Orb(if idx <= u16::MAX as usize {
                idx as u16
            } else {
                u16::MAX
            })),
            HookListener::Card(h) => Some(Self::Card(h)),
            HookListener::Enchantment(h) => Some(Self::Enchantment(h)),
            HookListener::Affliction(h) => Some(Self::Affliction(h)),
            HookListener::RunModel(id) => Some(Self::RunModel(id)),
            HookListener::Dominion(id) => Some(Self::Dominion(id)),
        }
    }

    #[inline(always)]
    pub const fn card_handle(self) -> Option<CardHandle> {
        match self {
            Self::Card(h) | Self::Enchantment(h) | Self::Affliction(h) => Some(h),
            _ => None,
        }
    }
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
    pub needs_choice: Option<Box<ChoiceRequest>>,
}

const _: () = assert!(std::mem::size_of::<HookResult>() <= 56);

#[derive(Clone, Debug, Default)]
pub struct CardPlayOutcome {
    pub needs_choice: Option<Box<ChoiceRequest>>,
    pub needs_choice_step: u8,
    pub transform_into: Option<CardId>,
}

impl CardPlayOutcome {
    #[inline(always)]
    pub fn with_choice(req: Box<ChoiceRequest>, step: u8) -> Self {
        Self {
            needs_choice: Some(req),
            needs_choice_step: step,
            transform_into: None,
        }
    }
}

const _: () = assert!(std::mem::size_of::<CardPlayOutcome>() == 16);

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
            return Err(KernelError::unsupported(
                "CardTurnEndInHand",
                id.model_id_str().to_owned(),
            ));
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
        Err(KernelError::unsupported(
            "MonsterMoveChoiceResume",
            id,
        ))
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
        cards: &mut [CardHandle],
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

    fn category_hook_masks(&self) -> Option<CategoryHookMasks> {
        None
    }

    fn hook_mask_tables(&self) -> Option<&'static HookMaskTableView> {
        None
    }

    #[inline]
    fn card_rollout_profile(&self, _id: CardId) -> CardRolloutProfile {
        CardRolloutProfile::default()
    }

    #[inline]
    fn card_hook_mask(&self, id: CardId) -> Result<HookMask, KernelError> {
        Ok(self.card_meta(id)?.hook_mask)
    }

    #[inline]
    fn power_hook_mask(&self, id: PowerId) -> Result<HookMask, KernelError> {
        Ok(self.power_meta(id)?.hook_mask)
    }

    #[inline]
    fn relic_hook_mask(&self, id: RelicId) -> Result<HookMask, KernelError> {
        Ok(self.relic_meta(id)?.hook_mask)
    }

    #[inline]
    fn potion_hook_mask(&self, id: PotionId) -> Result<HookMask, KernelError> {
        Ok(self.potion_meta(id)?.hook_mask)
    }

    #[inline]
    fn monster_hook_mask(&self, id: MonsterId) -> Result<HookMask, KernelError> {
        Ok(self.monster_meta(id)?.hook_mask)
    }

    #[inline]
    fn orb_hook_mask(&self, id: OrbId) -> Result<HookMask, KernelError> {
        Ok(self.orb_meta(id)?.hook_mask)
    }

    #[inline]
    fn enchantment_hook_mask_for_card(
        &self,
        state: &BattleState,
        card: CardHandle,
        _id: EnchantmentId,
    ) -> Result<HookMask, KernelError> {
        self.listener_hook_mask(state, HookListener::Enchantment(card))
    }

    #[inline]
    fn affliction_hook_mask_for_card(
        &self,
        state: &BattleState,
        card: CardHandle,
        _id: AfflictionId,
    ) -> Result<HookMask, KernelError> {
        self.listener_hook_mask(state, HookListener::Affliction(card))
    }

    fn listener_hook_mask(
        &self,
        state: &BattleState,
        listener: HookListener,
    ) -> Result<HookMask, KernelError>;

    #[inline]
    fn dispatch_power_hook_resolved(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
        _power_idx: usize,
        power: PowerInst,
        _owner_side: CombatSide,
        slot: HookSlot,
        args: &HookArgs<'_>,
    ) -> Result<HookResult, KernelError> {
        self.dispatch_hook(
            state,
            HookListener::Power {
                creature,
                power_handle: power.handle,
            },
            slot,
            args,
        )
    }

    fn dispatch_hook(
        &self,
        state: &mut BattleState,
        listener: HookListener,
        slot: HookSlot,
        args: &HookArgs<'_>,
    ) -> Result<HookResult, KernelError>;

    #[inline]
    fn check_should_allow_hitting_cached(
        &self,
        _state: &mut BattleState,
        _target: CreatureHandle,
        _bits: u64,
        _base: usize,
    ) -> Result<Option<bool>, KernelError> {
        Ok(None)
    }

    #[inline]
    fn check_should_creature_be_removed_after_death_cached(
        &self,
        _state: &mut BattleState,
        _target: CreatureHandle,
        _bits: u64,
        _base: usize,
    ) -> Result<Option<bool>, KernelError> {
        Ok(None)
    }

    #[inline]
    #[allow(clippy::too_many_arguments)]
    fn apply_modify_damage_slots_cached(
        &self,
        _state: &mut BattleState,
        _target: Option<CreatureHandle>,
        _dealer: Option<CreatureHandle>,
        _damage: Dec,
        _props: ValueProp,
        _card_source: Option<CardHandle>,
        _card_play: Option<CardPlayContext>,
        _hook_type: crate::hooks::ModifyDamageHookType,
    ) -> Result<Option<Dec>, KernelError> {
        Ok(None)
    }

    #[inline]
    fn dispatch_void_flat_bits_cached(
        &self,
        _state: &mut BattleState,
        _bits: u64,
        _slot: HookSlot,
        _args: &HookArgs<'_>,
    ) -> Result<bool, KernelError> {
        Ok(false)
    }

    #[inline]
    fn dispatch_void_flat_multiword_cached(
        &self,
        _state: &mut BattleState,
        _slot: HookSlot,
        _args: &HookArgs<'_>,
    ) -> Result<bool, KernelError> {
        Ok(false)
    }

    #[inline]
    #[allow(clippy::too_many_arguments)]
    fn apply_modify_hp_lost_slots_cached(
        &self,
        _state: &mut BattleState,
        _target: CreatureHandle,
        _amount: Dec,
        _props: ValueProp,
        _dealer: Option<CreatureHandle>,
        _card_source: Option<CardHandle>,
        _phase: crate::hooks::HpLossPhase,
    ) -> Result<Option<(Dec, SmallVec<[HookListener; 4]>)>, KernelError> {
        Ok(None)
    }

    #[inline]
    #[allow(clippy::too_many_arguments)]
    fn apply_modify_block_slots_cached(
        &self,
        _state: &mut BattleState,
        _target: CreatureHandle,
        _block: Dec,
        _props: ValueProp,
        _card_source: Option<CardHandle>,
        _card_play: Option<CardPlayContext>,
    ) -> Result<Option<(Dec, SmallVec<[HookListener; 4]>)>, KernelError> {
        Ok(None)
    }
}
