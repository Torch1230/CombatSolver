use smallvec::SmallVec;
use std::borrow::Cow;
use std::sync::Arc;
use sts2_content::powers::batch_n_z;
use sts2_content::Sts2Content;
use sts2_kernel::commands::damage::{AttackBuilder, AttackTargetSpec};
use sts2_kernel::commands::{arrow, block, card_pile, creature as creature_cmd, damage, orb, power};
use sts2_kernel::continuation::StepOutcome;
use sts2_kernel::{
    ArrowSlotInst, BattleState, CardHandle, CardInst, CardTempFlags, CombatEngine,
    ContentDispatcher, CreatureHandle, CreatureInst, FieldValue, HookArgs, HookListener,
    HookResult, KernelError, MonsterInst, PlayerCounters, PlayerInst, PowerExtension, PowerInst,
    RootStaticContext, SemanticHistory,
};
use sts2_kernel::content_dispatch::{CardPlayContext, CardPlayOutcome};
use sts2_types::{
    generated_card_meta, generated_power_meta, AfflictionId, ArrowStatus, CardId, CardKeywords,
    CardMeta, CardPilePosition, CharacterId, CombatSide, Dec, EnchantmentId, GameMode, HookMask,
    HookSlot, MonsterId, MonsterMeta, OrbId, OrbMeta, PileType, PlayerRngSet, PlayerTurnPhase,
    PotionId, PotionMeta, PowerId, PowerMeta, PowerStaticFlags, RelicId, RelicMeta, Rng, RoomType,
    RunRngSet, ValueProp,
};

#[derive(Clone, Copy, Default)]
struct NzTestDispatcher;

static FALLBACK_MONSTER_META: MonsterMeta = MonsterMeta {
    hook_mask: HookMask::EMPTY,
};
static FALLBACK_FROST_ORB_META: OrbMeta = OrbMeta {
    orb_id: OrbId::FrostOrb,
    hook_mask: HookMask::EMPTY,
};
static FALLBACK_DARK_ORB_META: OrbMeta = OrbMeta {
    orb_id: OrbId::DarkOrb,
    hook_mask: HookMask::EMPTY,
};
static FALLBACK_PLASMA_ORB_META: OrbMeta = OrbMeta {
    orb_id: OrbId::PlasmaOrb,
    hook_mask: HookMask::EMPTY,
};
static FALLBACK_GLASS_ORB_META: OrbMeta = OrbMeta {
    orb_id: OrbId::GlassOrb,
    hook_mask: HookMask::EMPTY,
};

impl ContentDispatcher for NzTestDispatcher {
    fn card_meta(&self, id: CardId) -> Result<&'static CardMeta, KernelError> {
        Sts2Content.card_meta(id).or_else(|_| Ok(generated_card_meta(id)))
    }
    fn power_meta(&self, id: PowerId) -> Result<&'static PowerMeta, KernelError> {
        Sts2Content.power_meta(id).or_else(|_| Ok(generated_power_meta(id)))
    }
    fn relic_meta(&self, id: RelicId) -> Result<&'static RelicMeta, KernelError> {
        Sts2Content.relic_meta(id)
    }
    fn potion_meta(&self, id: PotionId) -> Result<&'static PotionMeta, KernelError> {
        Sts2Content.potion_meta(id)
    }
    fn monster_meta(&self, id: MonsterId) -> Result<&'static MonsterMeta, KernelError> {
        Sts2Content.monster_meta(id).or(Ok(&FALLBACK_MONSTER_META))
    }
    fn orb_meta(&self, id: OrbId) -> Result<&'static OrbMeta, KernelError> {
        Sts2Content.orb_meta(id).or_else(|_| match id {
            OrbId::FrostOrb => Ok(&FALLBACK_FROST_ORB_META),
            OrbId::DarkOrb => Ok(&FALLBACK_DARK_ORB_META),
            OrbId::PlasmaOrb => Ok(&FALLBACK_PLASMA_ORB_META),
            OrbId::GlassOrb => Ok(&FALLBACK_GLASS_ORB_META),
            OrbId::LightningOrb => unreachable!(),
        })
    }
    fn card_is_playable(&self, state: &BattleState, card: CardHandle) -> Result<bool, KernelError> {
        Sts2Content.card_is_playable(state, card).or(Ok(true))
    }
    fn card_on_play(
        &self,
        state: &mut BattleState,
        ctx: &CardPlayContext,
        choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
        resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        Sts2Content.card_on_play(state, ctx, choice_queue, resume_step)
    }
    fn card_on_play_resume_after_select(
        &self,
        state: &mut BattleState,
        ctx: &CardPlayContext,
        selected: &[CardHandle],
        resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        Sts2Content.card_on_play_resume_after_select(state, ctx, selected, resume_step)
    }
    fn card_on_upgrade(&self, state: &mut BattleState, card: CardHandle) -> Result<(), KernelError> {
        Sts2Content.card_on_upgrade(state, card)
    }
    fn card_comparison_base_values(
        &self,
        state: &BattleState,
        card: CardHandle,
    ) -> Result<SmallVec<[(&'static str, Dec); 4]>, KernelError> {
        Sts2Content.card_comparison_base_values(state, card)
    }
    fn monster_initial_hp_range(
        &self,
        state: &BattleState,
        monster_id: MonsterId,
    ) -> Result<(i32, i32), KernelError> {
        Sts2Content.monster_initial_hp_range(state, monster_id).or(Ok((10, 10)))
    }
    fn monster_setup_for_combat(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        Sts2Content.monster_setup_for_combat(state, creature).or(Ok(()))
    }
    fn monster_after_added_to_room(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        Sts2Content.monster_after_added_to_room(state, creature).or(Ok(()))
    }
    fn monster_roll_move(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        Sts2Content.monster_roll_move(state, creature).or(Ok(()))
    }
    fn monster_perform_move(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        Sts2Content.monster_perform_move(state, creature).or(Ok(()))
    }
    fn orb_passive(
        &self,
        state: &mut BattleState,
        orb_idx: usize,
        target: Option<CreatureHandle>,
    ) -> Result<SmallVec<[CreatureHandle; 4]>, KernelError> {
        sts2_content::orbs_enchantments_afflictions::orb_passive(self, state, orb_idx, target)
            .or_else(|_| Ok(SmallVec::new()))
    }
    fn orb_evoke(
        &self,
        state: &mut BattleState,
        orb_idx: usize,
    ) -> Result<SmallVec<[CreatureHandle; 4]>, KernelError> {
        sts2_content::orbs_enchantments_afflictions::orb_evoke(self, state, orb_idx)
            .or_else(|_| Ok(SmallVec::new()))
    }
    fn orb_before_turn_end(&self, state: &mut BattleState, orb_idx: usize) -> Result<(), KernelError> {
        Sts2Content.orb_before_turn_end(state, orb_idx)
    }
    fn orb_after_turn_start(&self, state: &mut BattleState, orb_idx: usize) -> Result<(), KernelError> {
        Sts2Content.orb_after_turn_start(state, orb_idx)
    }
    fn potion_on_use(
        &self,
        state: &mut BattleState,
        potion_id: PotionId,
        target: Option<CreatureHandle>,
        choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
        resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        Sts2Content.potion_on_use(state, potion_id, target, choice_queue, resume_step)
    }
    fn potion_on_use_resume_after_select(
        &self,
        state: &mut BattleState,
        potion_id: PotionId,
        target: Option<CreatureHandle>,
        selected: &[CardHandle],
        resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        Sts2Content.potion_on_use_resume_after_select(state, potion_id, target, selected, resume_step)
    }
    fn resume_hook_choice(
        &self,
        state: &mut BattleState,
        listener: HookListener,
        slot: HookSlot,
        selected: &[CardHandle],
    ) -> Result<HookResult, KernelError> {
        match listener {
            HookListener::Power {
                creature,
                power_handle,
            } => {
                let (idx, id) = state
                    .creature(creature)
                    .and_then(|c| {
                        c.powers
                            .iter()
                            .enumerate()
                            .find(|(_, p)| p.handle == power_handle)
                            .map(|(i, p)| (i, p.id))
                    })
                    .ok_or_else(|| KernelError::InvalidState("missing power".to_owned()))?;
                if id.model_entry().as_bytes()[0] >= b'N' {
                    batch_n_z::resume_hook_choice(self, state, creature, idx, slot, selected)
                } else {
                    Sts2Content.resume_hook_choice(state, listener, slot, selected)
                }
            }
            _ => Sts2Content.resume_hook_choice(state, listener, slot, selected),
        }
    }
    fn enchantment_on_play(
        &self,
        state: &mut BattleState,
        card: CardHandle,
        ctx: &CardPlayContext,
    ) -> Result<(), KernelError> {
        Sts2Content.enchantment_on_play(state, card, ctx)
    }
    fn affliction_on_play(
        &self,
        state: &mut BattleState,
        card: CardHandle,
        ctx: &CardPlayContext,
    ) -> Result<(), KernelError> {
        Sts2Content.affliction_on_play(state, card, ctx)
    }
    fn enchantment_play_count(
        &self,
        state: &BattleState,
        card: CardHandle,
        play_count: i32,
    ) -> Result<i32, KernelError> {
        Sts2Content.enchantment_play_count(state, card, play_count)
    }
    fn enchantment_damage_additive(
        &self,
        state: &BattleState,
        card: CardHandle,
        amount: Dec,
        props: ValueProp,
    ) -> Result<Dec, KernelError> {
        Sts2Content.enchantment_damage_additive(state, card, amount, props)
    }
    fn enchantment_damage_multiplicative(
        &self,
        state: &BattleState,
        card: CardHandle,
        amount: Dec,
        props: ValueProp,
    ) -> Result<Dec, KernelError> {
        Sts2Content.enchantment_damage_multiplicative(state, card, amount, props)
    }
    fn enchantment_should_start_at_bottom(&self, id: EnchantmentId) -> Result<bool, KernelError> {
        Sts2Content.enchantment_should_start_at_bottom(id)
    }
    fn enchantment_modify_shuffle_order(
        &self,
        state: &BattleState,
        card: CardHandle,
        cards: &mut [CardHandle],
        is_initial_shuffle: bool,
    ) -> Result<(), KernelError> {
        Sts2Content.enchantment_modify_shuffle_order(state, card, cards, is_initial_shuffle)
    }
    fn listener_hook_mask(
        &self,
        state: &BattleState,
        listener: HookListener,
    ) -> Result<HookMask, KernelError> {
        match listener {
            HookListener::Power { creature, power_handle } => {
                let id = state
                    .creature(creature)
                    .and_then(|c| c.powers.iter().find(|p| p.handle == power_handle))
                    .ok_or_else(|| KernelError::InvalidState("missing power".to_owned()))?
                    .id;
                Ok(self.power_meta(id)?.hook_mask)
            }
            HookListener::DetachedPower { power, .. } => Ok(self.power_meta(power.id)?.hook_mask),
            HookListener::Affliction(_)
            | HookListener::Enchantment(_)
            | HookListener::Monster(_)
            | HookListener::Card(_) => Sts2Content
                .listener_hook_mask(state, listener)
                .or(Ok(HookMask::EMPTY)),
            HookListener::Orb(idx) => {
                let id = state.player.orbs[idx].id;
                Ok(self.orb_meta(id)?.hook_mask)
            }
            _ => Sts2Content.listener_hook_mask(state, listener),
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
            HookListener::Power { creature, power_handle } => {
                let (power_idx, p, owner_side) = state
                    .creature(creature)
                    .and_then(|c| {
                        c.powers
                            .iter()
                            .enumerate()
                            .find(|(_, p)| p.handle == power_handle)
                            .map(|(idx, &p)| (idx, p, c.side))
                    })
                    .ok_or_else(|| KernelError::InvalidState("missing power".to_owned()))?;
                if p.id.model_entry().as_bytes()[0] >= b'N' {
                    batch_n_z::dispatch_hook(
                        self,
                        state,
                        creature,
                        power_idx,
                        p,
                        owner_side,
                        slot,
                        args,
                    )
                } else {
                    Sts2Content
                        .dispatch_hook(state, listener, slot, args)
                        .or_else(|_| Ok(HookResult::default()))
                }
            }
            HookListener::DetachedPower { creature, power } => {
                if power.id.model_entry().as_bytes()[0] >= b'N' {
                    batch_n_z::dispatch_detached_hook(self, state, creature, power, slot, args)
                } else {
                    Sts2Content
                        .dispatch_hook(state, listener, slot, args)
                        .or_else(|_| Ok(HookResult::default()))
                }
            }
            _ => Sts2Content.dispatch_hook(state, listener, slot, args),
        }
    }
}

const ALL_NZ_POWERS: [PowerId; 135] = [
    PowerId::NecroMasteryPower,
    PowerId::NemesisPower,
    PowerId::NeurosurgePower,
    PowerId::NightmarePower,
    PowerId::NostalgiaPower,
    PowerId::NoxiousFumesPower,
    PowerId::NoBlockPower,
    PowerId::NoDrawPower,
    PowerId::OblivionPower,
    PowerId::OneForAllPower,
    PowerId::OneTwoPunchPower,
    PowerId::OrbitPower,
    PowerId::PagestormPower,
    PowerId::PainfulStabsPower,
    PowerId::PaleBlueDotPower,
    PowerId::PanachePower,
    PowerId::PaperCutsPower,
    PowerId::ParryPower,
    PowerId::PersonalHivePower,
    PowerId::PhantomBladesPower,
    PowerId::PiercingWailPower,
    PowerId::PillarOfCreationPower,
    PowerId::PlatingPower,
    PowerId::PlowPower,
    PowerId::PoisonPower,
    PowerId::PossessSpeedPower,
    PowerId::PossessStrengthPower,
    PowerId::PrepTimePower,
    PowerId::PyrePower,
    PowerId::QuietusPower,
    PowerId::RadiancePower,
    PowerId::RagePower,
    PowerId::RampartPower,
    PowerId::RavenousPower,
    PowerId::ReaperFormPower,
    PowerId::ReattachPower,
    PowerId::ReboundPower,
    PowerId::ReflectPower,
    PowerId::RegenPower,
    PowerId::ReptileTrinketPower,
    PowerId::RetainHandPower,
    PowerId::RingingPower,
    PowerId::RitualPower,
    PowerId::RollingBoulderPower,
    PowerId::RoyaltiesPower,
    PowerId::RupturePower,
    PowerId::SandpitPower,
    PowerId::SeekingEdgePower,
    PowerId::SelfFormingClayPower,
    PowerId::SentryModePower,
    PowerId::SerpentFormPower,
    PowerId::SerratedArrowsPower,
    PowerId::SetupStrikePower,
    PowerId::ShacklingPotionPower,
    PowerId::ShadowmeldPower,
    PowerId::ShadowStepPower,
    PowerId::ShowOffPower,
    PowerId::ShriekPower,
    PowerId::ShrinkPower,
    PowerId::ShroudPower,
    PowerId::SicEmPower,
    PowerId::SignalBoostPower,
    PowerId::SimpletonPower,
    PowerId::SkittishPower,
    PowerId::SleightOfFleshPower,
    PowerId::SlipperyPower,
    PowerId::SlothPower,
    PowerId::SlowPower,
    PowerId::SlumberPower,
    PowerId::SmoggyPower,
    PowerId::SmokestackPower,
    PowerId::SneakyPower,
    PowerId::SoarPower,
    PowerId::SongPathPower,
    PowerId::SoulboundPower,
    PowerId::SpectrumShiftPower,
    PowerId::SpeedsterPower,
    PowerId::SpeedPotionPower,
    PowerId::SpinnerPower,
    PowerId::SpiritOfAshPower,
    PowerId::StampedePower,
    PowerId::StarNextTurnPower,
    PowerId::SteamEruptionPower,
    PowerId::StockPower,
    PowerId::StormPower,
    PowerId::StranglePower,
    PowerId::StratagemPower,
    PowerId::StrengthPower,
    PowerId::StretchPower,
    PowerId::SubroutinePower,
    PowerId::SuckPower,
    PowerId::SummonNextTurnPower,
    PowerId::SurprisePower,
    PowerId::SurroundedPower,
    PowerId::SwipePower,
    PowerId::SwordSagePower,
    PowerId::SynchronizePower,
    PowerId::TagTeamPower,
    PowerId::TaintedPower,
    PowerId::TangledPower,
    PowerId::TankPower,
    PowerId::TemporaryDexterityPower,
    PowerId::TemporaryStrengthPower,
    PowerId::TenderPower,
    PowerId::TerritorialPower,
    PowerId::TheBombPower,
    PowerId::TheGambitPower,
    PowerId::TheHuntPower,
    PowerId::TheSealedThronePower,
    PowerId::ThieveryPower,
    PowerId::ThornsPower,
    PowerId::ThunderPower,
    PowerId::ToolsOfTheTradePower,
    PowerId::ToricToughnessPower,
    PowerId::TrackingPower,
    PowerId::TrashToTreasurePower,
    PowerId::TunePower,
    PowerId::TyrannyPower,
    PowerId::UnderworldPower,
    PowerId::UnmovablePower,
    PowerId::VeilpiercerPower,
    PowerId::ViciousPower,
    PowerId::VigorPower,
    PowerId::ViolentChordPower,
    PowerId::VitalSparkPower,
    PowerId::VoidFormPower,
    PowerId::VulnerablePower,
    PowerId::WasteAwayPower,
    PowerId::WeakPower,
    PowerId::WellLaidPlansPower,
    PowerId::WitheringPresencePower,
    PowerId::WraithFormPower,
    PowerId::WuppysLoyaltyPower,
    PowerId::YahPower,
    PowerId::YouAreMinePower,
];

fn make_card(handle: u32, id: CardId, pile: PileType) -> CardInst {
    let meta = generated_card_meta(id);
    let mut temp_flags = CardTempFlags::empty();
    if meta
        .flags
        .contains(sts2_types::CardStaticFlags::IS_X_ENERGY_COST)
    {
        temp_flags |= CardTempFlags::COSTS_X_ENERGY;
    }
    CardInst {
        handle: CardHandle(handle),
        id,
        upgrade_level: 0,
        canonical_base_energy_cost: meta.canonical_energy_cost as i16,
        base_energy_cost: meta.canonical_energy_cost as i16,
        captured_energy_x_value: 0,
        base_replay_count: 0,
        floor_added_to_deck: sts2_kernel::OptI16::some(1),
        deck_card_handle: sts2_kernel::OptCardHandle::NONE,
        clone_of_handle: sts2_kernel::OptCardHandle::NONE,
        temp_star_cost_override_this_turn: sts2_kernel::OptI16::NONE,
        temp_cost_override_this_turn: sts2_kernel::OptI16::NONE,
        temp_cost_override_this_turn_or_until_played: sts2_kernel::OptI16::NONE,
        temp_cost_override_this_combat: sts2_kernel::OptI16::NONE,
        local_keywords: meta.base_keywords,
        keywords_added_from_outside: CardKeywords::empty(),
        keywords_removed_from_outside: CardKeywords::empty(),
        temp_flags,
        affliction: None,
        ext_index: None,
        pile,
        removed_from_state: false,
        transform_into_after_play: None,
    }
}

fn build_battle(character: CharacterId, hand_ids: &[CardId], enemy_hp: i32) -> (BattleState, CreatureHandle) {
    let run_rngs = RunRngSet::from_string_seed("NZTESTSEED");
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc_handle = CreatureHandle(1);
    let enemy_handle = CreatureHandle(2);

    let mut cards = Vec::new();
    let mut hand = Vec::new();
    for (idx, &cid) in hand_ids.iter().enumerate() {
        let h = (idx + 1) as u32;
        hand.push(CardHandle(h));
        cards.push(make_card(h, cid, PileType::Hand));
    }

    let mut arrows = Vec::new();
    for _ in 0..character.base_arrow_slot_count() {
        arrows.push(ArrowSlotInst {
            status: ArrowStatus::Retrieved,
            destroy_after_impaled: false,
        });
    }

    let player_creature = CreatureInst {
        handle: pc_handle,
        combat_id: Some(0),
        side: CombatSide::Player,
        slot_name: None,
        is_player: true,
        pet_owner_player_index: None,
        monster_id: None,
        current_hp: 80,
        max_hp: 80,
        block: 0,
        cumulative_hp_lost: 0,
        is_detached: false,
        monster: None,
        has_minion_power: false,
        powers: Vec::new(),
    };

    let enemy_creature = CreatureInst {
        handle: enemy_handle,
        combat_id: Some(1),
        side: CombatSide::Enemy,
        slot_name: Some(Cow::Borrowed("slot_0")),
        is_player: false,
        pet_owner_player_index: None,
        monster_id: Some(MonsterId::FuzzyWurmCrawler),
        current_hp: enemy_hp,
        max_hp: enemy_hp,
        block: 0,
        cumulative_hp_lost: 0,
        is_detached: false,
        monster: Some(Box::new(MonsterInst {
            monster_id: MonsterId::FuzzyWurmCrawler,
            rng: Rng::from_seed(run_rngs.seed + 1),
            is_performing_move: false,
            spawned_this_turn: false,
            current_state_id: Cow::Borrowed("FIRST_ACID_GOOP"),
            next_move_id: Some(Cow::Borrowed("FIRST_ACID_GOOP")),
            next_move_can_transition_away: false,
            performed_first_move: false,
            state_log_ids: vec![Cow::Borrowed("FIRST_ACID_GOOP")],
            transient_move_follow_up_state_id: None,
            setup_fields: Default::default(),
            private_fields: Default::default(),
        })),
        has_minion_power: false,
        powers: Vec::new(),
    };

    let player = PlayerInst {
        creature_handle: pc_handle,
        current_hp: 80,
        max_hp: 80,
        block: 0,
        cumulative_hp_lost: 0,
        gold: 100,
        max_energy: 3,
        base_orb_slot_count: character.base_orb_slot_count(),
        is_active_for_hooks: true,
        can_use_or_remove_potions: true,
        energy: 3,
        stars: 0,
        phase: PlayerTurnPhase::Play,
        turn_number: 1,
        counters: PlayerCounters::default(),
        hand,
        draw: Vec::new(),
        discard: Vec::new(),
        exhaust: Vec::new(),
        play: Vec::new(),
        deck: Vec::new(),
        potion_slots: smallvec::smallvec![None, None, None],
        combat_generated_potion_mask: 0,
        orb_capacity: character.base_orb_slot_count(),
        orbs: Vec::new(),
        arrows,
        relics: Vec::new(),
        card_or_potion_effect_depth: 0,
    };

    let state = BattleState {
        static_ctx: Arc::new(RootStaticContext {
            encounter_id: "TEST_ENCOUNTER".to_owned(),
            room_type: RoomType::Monster,
            encounter_slots: vec!["slot_0".to_owned()],
            encounter_is_weak: false,
            game_mode: GameMode::Standard,
            ascension: 0,
            character_id: character,
            dominion_id: None,
            run_model_ids: Vec::new(),
            card_generation_pools: Vec::new(),
            colorless_card_pool: Vec::new(),
            curse_card_pool: Vec::new(),
            other_character_card_pool: Vec::new(),
            tarot_card_pool: Vec::new(),
            potion_generation_pool: Vec::new(),
            transformation_pools: Vec::new(),
            map_col: 0,
            map_row: 0,
            act_index: 0,
            total_floor: 0,
            ..Default::default()
        }),
        current_side: CombatSide::Player,
        round_number: 1,
        initial_encounter_monster_count: 1,
        is_in_progress: true,
        is_starting: false,
        won: false,
        is_player_extra_turn: false,
        gold_was_stolen: false,
        end_player_turn_requested: false,
        card_action_boundary_depth: 0,
        next_creature_id: 2,
        next_card_handle: (cards.len() + 1) as u32,
        next_creature_handle: 3,
        next_power_handle: 1,
        next_attack_id: 1,
        run_rngs,
        player_rngs,
        player,
        cards,
        card_extensions: Vec::new(),
        creatures: vec![player_creature, enemy_creature],
        power_extensions: Vec::new(),
        ally_handles: vec![pc_handle],
        enemy_handles: vec![enemy_handle],
        escaped_handles: SmallVec::new(),
        removed_handles: SmallVec::new(),
        spawned_enemy_handles: smallvec::smallvec![enemy_handle],
        pet_handles: SmallVec::new(),
        damage_history: Vec::new(),
        semantic_history: SemanticHistory::default(),
        pending: None,
        vakuu_auto_select_depth: 0,
        hook_cache: Default::default(),
        deck_dirty: Default::default(),
        relic_dirty_mask: Default::default(),
    };
    (state, enemy_handle)
}

fn push_power(
    state: &mut BattleState,
    creature: CreatureHandle,
    id: PowerId,
    amount: i32,
    applier: Option<CreatureHandle>,
    private_fields: Vec<(&str, FieldValue)>,
) -> u32 {
    let handle = state.next_power_handle;
    state.next_power_handle += 1;
    let ext_index = if private_fields.is_empty() {
        None
    } else {
        let mut ext = PowerExtension::default();
        for (k, v) in private_fields {
            ext.private_fields.insert(k.to_owned(), v);
        }
        let idx = state.power_extensions.len() as u32;
        state.power_extensions.push(ext);
        Some(idx)
    };
    let c = state.creature_mut(creature).unwrap();
    if id == PowerId::MinionPower {
        c.has_minion_power = true;
    }
    c.powers.push(PowerInst {
        handle,
        id,
        amount,
        amount_on_turn_start: amount,
        skip_next_duration_tick: false,
        applier_handle: applier,
        ext_index,
    });
    state.invalidate_hook_cache();
    handle
}

#[test]
fn all_135_nz_powers_have_exact_metadata() {
    let content = Sts2Content;
    let mut prev = "";
    for &id in &ALL_NZ_POWERS {
        let entry = id.model_entry();
        assert!(entry.as_bytes()[0] >= b'N', "not N-Z: {entry}");
        assert!(entry > prev, "duplicate or unsorted: {entry}");
        prev = entry;

        let meta = content.power_meta(id).expect("N-Z power_meta must exist");
        let gen_meta = generated_power_meta(id);
        assert_eq!(meta.power_type, gen_meta.power_type, "power_type mismatch on {entry}");
        assert_eq!(meta.stack_type, gen_meta.stack_type, "stack_type mismatch on {entry}");
        assert_eq!(
            meta.instance_type, gen_meta.instance_type,
            "instance_type mismatch on {entry}"
        );

        let expect_keep = matches!(
            id,
            PowerId::PainfulStabsPower | PowerId::SteamEruptionPower | PowerId::WuppysLoyaltyPower
        );
        assert_eq!(
            !meta.flags.contains(PowerStaticFlags::REMOVE_AFTER_OWNER_DEATH),
            expect_keep,
            "REMOVE_AFTER_OWNER_DEATH mismatch on {entry}"
        );

        let expect_neg = matches!(
            id,
            PowerId::ShriekPower | PowerId::ShrinkPower | PowerId::StrengthPower
        );
        assert_eq!(
            meta.flags.contains(PowerStaticFlags::ALLOW_NEGATIVE),
            expect_neg,
            "ALLOW_NEGATIVE mismatch on {entry}"
        );

        let expect_temp = matches!(
            id,
            PowerId::PiercingWailPower
                | PowerId::ReptileTrinketPower
                | PowerId::SetupStrikePower
                | PowerId::ShacklingPotionPower
                | PowerId::SpeedPotionPower
                | PowerId::SynchronizePower
                | PowerId::TemporaryDexterityPower
                | PowerId::TemporaryStrengthPower
        );
        assert_eq!(
            meta.flags.contains(PowerStaticFlags::IS_TEMPORARY),
            expect_temp,
            "IS_TEMPORARY mismatch on {entry}"
        );
        assert_eq!(
            meta.hook_mask.contains(HookSlot::BeforeApplied),
            expect_temp,
            "BeforeApplied hook mismatch on {entry}"
        );

        let expect_after_applied = matches!(
            id,
            PowerId::PlatingPower
                | PowerId::RingingPower
                | PowerId::RitualPower
                | PowerId::TangledPower
                | PowerId::TankPower
                | PowerId::VoidFormPower
        );
        assert_eq!(
            meta.hook_mask.contains(HookSlot::AfterApplied),
            expect_after_applied,
            "AfterApplied hook mismatch on {entry}"
        );

        let expect_after_removed = matches!(
            id,
            PowerId::RingingPower
                | PowerId::SandpitPower
                | PowerId::SwordSagePower
                | PowerId::TangledPower
                | PowerId::VitalSparkPower
        );
        assert_eq!(
            meta.hook_mask.contains(HookSlot::AfterRemoved),
            expect_after_removed,
            "AfterRemoved hook mismatch on {entry}"
        );

        assert_eq!(
            meta.hook_mask.contains(HookSlot::ShouldOwnerDeathTriggerFatal),
            id == PowerId::ReattachPower,
            "ShouldOwnerDeathTriggerFatal hook mismatch on {entry}"
        );

        // Verify PowerId::from_model_id_str round-trips and stays in N-Z
        assert_eq!(PowerId::from_model_id_str(id.model_id_str()), Some(id));
    }
    assert_eq!(ALL_NZ_POWERS.len(), 135);
}

#[test]
fn poison_with_accelerant_and_nox_fumes() {
    let content = NzTestDispatcher;
    let (mut state, enemy) = build_battle(CharacterId::Silent, &[], 50);
    let pc = state.player.creature_handle;
    power::apply(&mut state, &content, PowerId::NoxiousFumesPower, pc, Dec::from_i32(2), Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::AccelerantPower, pc, Dec::from_i32(2), Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::PoisonPower, enemy, Dec::from_i32(5), Some(pc), None).unwrap();

    // Enemy turn start triggers Poison 3 times: 5 + 4 + 3 = 12 dmg, Poison becomes 2
    let enemies = [enemy];
    sts2_kernel::hooks::after_side_turn_start(&mut state, &content, CombatSide::Enemy, &enemies).unwrap();
    assert_eq!(state.creature(enemy).unwrap().current_hp, 50 - 12);
    assert_eq!(
        state.creature(enemy).unwrap().powers.iter().find(|p| p.id == PowerId::PoisonPower).unwrap().amount,
        2
    );

    // Player turn start: NoxiousFumes applies +2 Poison to enemy (2 -> 4)
    let allies = [pc];
    sts2_kernel::hooks::after_side_turn_start(&mut state, &content, CombatSide::Player, &allies).unwrap();
    assert_eq!(
        state.creature(enemy).unwrap().powers.iter().find(|p| p.id == PowerId::PoisonPower).unwrap().amount,
        4
    );
}

#[test]
fn slippery_caps_hp_loss_and_decrements_per_hit() {
    let content = Sts2Content;
    let (mut state, enemy) = build_battle(CharacterId::Ironclad, &[], 30);
    let pc = state.player.creature_handle;
    power::apply(&mut state, &content, PowerId::SlipperyPower, enemy, Dec::from_i32(2), Some(enemy), None).unwrap();

    // Hit 1: 10 dmg -> capped to 1, Slippery 2 -> 1
    damage::damage_targets(&mut state, &content, &[enemy], Dec::from_i32(10), ValueProp::MOVE, Some(pc), None, None).unwrap();
    assert_eq!(state.creature(enemy).unwrap().current_hp, 29);
    assert_eq!(state.creature(enemy).unwrap().powers[0].amount, 1);

    // Hit 2: 10 dmg -> capped to 1, Slippery removed
    damage::damage_targets(&mut state, &content, &[enemy], Dec::from_i32(10), ValueProp::MOVE, Some(pc), None, None).unwrap();
    assert_eq!(state.creature(enemy).unwrap().current_hp, 28);
    assert!(state.creature(enemy).unwrap().powers.is_empty());

    // Hit 3: 10 dmg -> full 10 dmg
    damage::damage_targets(&mut state, &content, &[enemy], Dec::from_i32(10), ValueProp::MOVE, Some(pc), None, None).unwrap();
    assert_eq!(state.creature(enemy).unwrap().current_hp, 18);
}

#[test]
fn steam_eruption_waterfall_giant_death_transformation() {
    let content = NzTestDispatcher;
    let (mut state, enemy) = build_battle(CharacterId::Ironclad, &[], 20);
    {
        let e = state.creature_mut(enemy).unwrap();
        e.monster_id = Some(MonsterId::WaterfallGiant);
        e.monster.as_mut().unwrap().monster_id = MonsterId::WaterfallGiant;
    }
    power::apply(&mut state, &content, PowerId::SteamEruptionPower, enemy, Dec::from_i32(18), Some(enemy), None).unwrap();

    // Lethal damage triggers SteamEruption AfterDeath -> 999,999,999 HP + ABOUT_TO_BLOW_MOVE, combat continues
    damage::damage_targets(&mut state, &content, &[enemy], Dec::from_i32(25), ValueProp::MOVE, None, None, None).unwrap();
    let e = state.creature(enemy).unwrap();
    assert_eq!(e.current_hp, 999_999_999);
    assert_eq!(e.max_hp, 999_999_999);
    assert!(!e.is_detached);
    assert_eq!(e.monster.as_ref().unwrap().next_move_id.as_deref(), Some("ABOUT_TO_BLOW_MOVE"));
    assert!(!e.monster.as_ref().unwrap().next_move_can_transition_away);
    assert!(!CombatEngine::check_win_condition(&mut state, &content).unwrap());
}

#[test]
fn plating_regen_ritual_the_bomb_thorns_and_wraith_form() {
    let content = Sts2Content;
    let (mut state, enemy) = build_battle(CharacterId::Defect, &[], 100);
    let pc = state.player.creature_handle;

    // Plating on enemy (after_applied fires automatically; turn 1 pre-block + turn end block + turn start skip round 1)
    power::apply(&mut state, &content, PowerId::PlatingPower, enemy, Dec::from_i32(4), Some(enemy), None).unwrap();
    sts2_kernel::hooks::before_side_turn_start(&mut state, &content, CombatSide::Player, &[pc]).unwrap();
    assert_eq!(state.creature(enemy).unwrap().block, 4);

    // Regen on player
    power::apply(&mut state, &content, PowerId::RegenPower, pc, Dec::from_i32(3), Some(pc), None).unwrap();
    state.creature_mut(pc).unwrap().current_hp = 60;
    sts2_kernel::hooks::before_side_turn_end_early(&mut state, &content, CombatSide::Player, &[pc]).unwrap();
    assert_eq!(state.creature(pc).unwrap().current_hp, 63);
    assert_eq!(state.creature(pc).unwrap().powers[0].amount, 2);

    // Ritual on enemy (after_applied sets _skipFirstEnemyTurnEnd = true)
    power::apply(&mut state, &content, PowerId::RitualPower, enemy, Dec::from_i32(3), Some(enemy), None).unwrap();
    sts2_kernel::hooks::after_side_turn_end(&mut state, &content, CombatSide::Enemy, &[enemy]).unwrap();
    assert!(!state.creature(enemy).unwrap().powers.iter().any(|p| p.id == PowerId::StrengthPower));
    sts2_kernel::hooks::after_side_turn_end(&mut state, &content, CombatSide::Enemy, &[enemy]).unwrap();
    assert_eq!(
        state.creature(enemy).unwrap().powers.iter().find(|p| p.id == PowerId::StrengthPower).unwrap().amount,
        3
    );

    // TheBomb (amount 2 -> 1 -> explode for 40)
    push_power(
        &mut state,
        pc,
        PowerId::TheBombPower,
        2,
        Some(pc),
        vec![("TheBombPower._damage", FieldValue::Decimal(Dec::from_i32(40)))],
    );
    sts2_kernel::hooks::before_side_turn_end(&mut state, &content, CombatSide::Player, &[pc]).unwrap();
    assert_eq!(state.creature(enemy).unwrap().current_hp, 100);
    sts2_kernel::hooks::before_side_turn_end(&mut state, &content, CombatSide::Player, &[pc]).unwrap();
    // 40 dmg minus 4 block = 36 unblocked -> 64 HP
    assert_eq!(state.creature(enemy).unwrap().current_hp, 64);

    // Thorns
    power::apply(&mut state, &content, PowerId::ThornsPower, pc, Dec::from_i32(5), Some(pc), None).unwrap();
    damage::damage_targets(&mut state, &content, &[pc], Dec::from_i32(2), ValueProp::MOVE, Some(enemy), None, None).unwrap();
    assert_eq!(state.creature(enemy).unwrap().current_hp, 59);

    // WraithForm loses Dexterity on turn start
    power::apply(&mut state, &content, PowerId::WraithFormPower, pc, Dec::from_i32(1), Some(pc), None).unwrap();
    sts2_kernel::hooks::after_side_turn_start(&mut state, &content, CombatSide::Player, &[pc]).unwrap();
    assert_eq!(
        state.creature(pc).unwrap().powers.iter().find(|p| p.id == PowerId::DexterityPower).unwrap().amount,
        -1
    );
}

#[test]
fn void_form_veilpiercer_sloth_slow_no_block_rage_panache_orbit() {
    let content = Sts2Content;
    let (mut state, enemy) = build_battle(
        CharacterId::Ironclad,
        &[
            CardId::Bash,
            CardId::DefendIronclad,
            CardId::StrikeIronclad,
            CardId::StrikeIronclad,
            CardId::StrikeIronclad,
        ],
        100,
    );
    let pc = state.player.creature_handle;

    // VoidFormPower: after_applied hides 0-cost until next turn start
    power::apply(&mut state, &content, PowerId::VoidFormPower, pc, Dec::ONE, Some(pc), None).unwrap();
    assert_eq!(sts2_kernel::ordering::resolve_energy_cost(&mut state, &content, CardHandle(1)).unwrap(), 2);
    sts2_kernel::hooks::before_side_turn_start(&mut state, &content, CombatSide::Player, &[pc]).unwrap();
    assert_eq!(sts2_kernel::ordering::resolve_energy_cost(&mut state, &content, CardHandle(1)).unwrap(), 0);
    // Reapplying/stacking VoidFormPower fires BeforePowerAmountChanged -> hides 0-cost again until turn start
    power::apply(&mut state, &content, PowerId::VoidFormPower, pc, Dec::ONE, Some(pc), None).unwrap();
    assert_eq!(sts2_kernel::ordering::resolve_energy_cost(&mut state, &content, CardHandle(1)).unwrap(), 2);
    sts2_kernel::hooks::before_side_turn_start(&mut state, &content, CombatSide::Player, &[pc]).unwrap();
    assert_eq!(sts2_kernel::ordering::resolve_energy_cost(&mut state, &content, CardHandle(1)).unwrap(), 0);

    // SlothPower caps plays to 2, OrbitPower refunds on 4 energy, PanachePower deals 10 after 5 plays (after initial apply), SlowPower boosts damage
    power::apply(&mut state, &content, PowerId::SlothPower, pc, Dec::from_i32(2), Some(enemy), None).unwrap();
    power::apply(&mut state, &content, PowerId::SlowPower, enemy, Dec::from_i32(2), Some(pc), None).unwrap();
    let mut choices = SmallVec::new();
    CombatEngine::play_card(&mut state, &content, CardHandle(1), Some(enemy), &mut choices).unwrap();
    // Bash deals 8 base
    assert_eq!(state.creature(enemy).unwrap().current_hp, 92);
    // SlowPower now has 1 card played -> 1.1x on next hit; Strike deals 6 * 1.1 = 6.6 -> 6 (with Bash's Vulnerable 2 -> 6 * 1.5 * 1.1 = 9.9 -> 9)
    CombatEngine::play_card(&mut state, &content, CardHandle(3), Some(enemy), &mut choices).unwrap();
    assert_eq!(state.creature(enemy).unwrap().current_hp, 83);
    // 3rd card blocked by SlothPower
    assert!(!CombatEngine::can_play_card(&mut state, &content, CardHandle(4)).unwrap());

    // NoBlockPower zeroes card block and decrements on enemy turn end
    power::apply(&mut state, &content, PowerId::NoBlockPower, pc, Dec::from_i32(2), Some(enemy), None).unwrap();
    let gained = block::gain_block(&mut state, &content, pc, Dec::from_i32(5), ValueProp::MOVE, Some(CardHandle(2)), None).unwrap();
    assert_eq!(gained, Dec::ZERO);
    sts2_kernel::hooks::after_side_turn_end(&mut state, &content, CombatSide::Enemy, &[enemy]).unwrap();
    assert_eq!(state.creature(pc).unwrap().powers.iter().find(|p| p.id == PowerId::NoBlockPower).unwrap().amount, 1);
}

#[test]
fn panache_orbit_rage_veilpiercer_sealed_throne_unmovable_and_stretch() {
    let content = Sts2Content;
    let (mut state, enemy) = build_battle(
        CharacterId::Regent,
        &[
            CardId::DefendRegent,
            CardId::DefendRegent,
            CardId::StrikeRegent,
            CardId::StrikeRegent,
            CardId::StrikeRegent,
            CardId::StrikeRegent,
        ],
        100,
    );
    let pc = state.player.creature_handle;
    state.player.energy = 10;

    power::apply(&mut state, &content, PowerId::UnmovablePower, pc, Dec::from_i32(1), Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::StretchPower, pc, Dec::from_i32(3), Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::OrbitPower, pc, Dec::from_i32(2), Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::TheSealedThronePower, pc, Dec::from_i32(1), Some(pc), None).unwrap();
    push_power(
        &mut state,
        pc,
        PowerId::PanachePower,
        10,
        Some(pc),
        vec![("PanachePower._alreadyApplied", FieldValue::Bool(true))],
    );
    power::apply(&mut state, &content, PowerId::RagePower, pc, Dec::from_i32(2), Some(pc), None).unwrap();

    let mut choices = SmallVec::new();
    // Play Defend #1: (5 + 3 Stretch) * 2 Unmovable = 16 block, consumes Stretch, gains 1 star, Orbit energy=1
    CombatEngine::play_card(&mut state, &content, CardHandle(1), None, &mut choices).unwrap();
    assert_eq!(state.player.block, 16);
    assert_eq!(state.player.stars, 1);
    assert!(!state.creature(pc).unwrap().powers.iter().any(|p| p.id == PowerId::StretchPower));

    // Play Defend #2: Unmovable already has 1 block card play this turn, so only 5 block
    CombatEngine::play_card(&mut state, &content, CardHandle(2), None, &mut choices).unwrap();
    assert_eq!(state.player.block, 21);

    // Play 3 Strikes (total 5 cards since Panache alreadyApplied=true -> triggers Panache 10 dmg; total 5 energy -> Orbit +2)
    CombatEngine::play_card(&mut state, &content, CardHandle(3), Some(enemy), &mut choices).unwrap();
    CombatEngine::play_card(&mut state, &content, CardHandle(4), Some(enemy), &mut choices).unwrap();
    CombatEngine::play_card(&mut state, &content, CardHandle(5), Some(enemy), &mut choices).unwrap();
    assert_eq!(state.creature(enemy).unwrap().current_hp, 100 - 18 - 10);
    // Rage gave 2 * 3 = 6 block
    assert_eq!(state.player.block, 27);
    // Energy: 10 - 5 + 2 (Orbit at 4 spent) = 7
    assert_eq!(state.player.energy, 7);
}

#[test]
fn rebound_nostalgia_one_two_punch_signal_boost_tag_team_and_flush_powers() {
    let content = Sts2Content;
    let (mut state, enemy) = build_battle(
        CharacterId::Ironclad,
        &[CardId::StrikeIronclad, CardId::StrikeIronclad, CardId::DefendIronclad],
        80,
    );
    let pc = state.player.creature_handle;
    power::apply(&mut state, &content, PowerId::ReboundPower, pc, Dec::ONE, Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::OneTwoPunchPower, pc, Dec::ONE, Some(pc), None).unwrap();

    let mut choices = SmallVec::new();
    // Strike #1 plays twice (OneTwoPunch) and goes to top of Draw (Rebound)
    CombatEngine::play_card(&mut state, &content, CardHandle(1), Some(enemy), &mut choices).unwrap();
    assert_eq!(state.creature(enemy).unwrap().current_hp, 80 - 12);
    assert_eq!(state.player.draw.as_slice(), &[CardHandle(1)]);
    assert!(!state.creature(pc).unwrap().powers.iter().any(|p| p.id == PowerId::ReboundPower));
    assert!(!state.creature(pc).unwrap().powers.iter().any(|p| p.id == PowerId::OneTwoPunchPower));

    // Nostalgia puts Attack/Skill on top of draw
    power::apply(&mut state, &content, PowerId::NostalgiaPower, pc, Dec::from_i32(3), Some(pc), None).unwrap();
    CombatEngine::play_card(&mut state, &content, CardHandle(2), Some(enemy), &mut choices).unwrap();
    assert_eq!(state.player.draw[0], CardHandle(2));

    // WellLaidPlans prevents flush
    power::apply(&mut state, &content, PowerId::WellLaidPlansPower, pc, Dec::ONE, Some(pc), None).unwrap();
    assert!(!sts2_kernel::hooks::should_flush(&mut state, &content).unwrap());

    // TagTeam replays enemy-targeted attack
    power::apply(&mut state, &content, PowerId::TagTeamPower, enemy, Dec::ONE, Some(enemy), None).unwrap();
    let (cnt, mods) = sts2_kernel::hooks::modify_card_play_count(&mut state, &content, CardHandle(1), Some(enemy), 1).unwrap();
    assert_eq!(cnt, 2);
    sts2_kernel::hooks::after_modifying_card_play_count(&mut state, &content, CardHandle(1), &mods).unwrap();
    assert!(!state.creature(enemy).unwrap().powers.iter().any(|p| p.id == PowerId::TagTeamPower));
}

#[test]
fn temporary_powers_and_stat_interactions() {
    let content = Sts2Content;
    let (mut state, enemy) = build_battle(CharacterId::Ironclad, &[], 50);
    let pc = state.player.creature_handle;

    // TemporaryStrengthPower / PiercingWailPower / SpeedPotionPower / SynchronizePower
    for &(pid, target_stat, expected_delta) in &[
        (PowerId::TemporaryStrengthPower, PowerId::StrengthPower, 4),
        (PowerId::PiercingWailPower, PowerId::StrengthPower, -4),
        (PowerId::SpeedPotionPower, PowerId::DexterityPower, 4),
        (PowerId::SynchronizePower, PowerId::FocusPower, 4),
    ] {
        // First apply fires BeforeApplied (+3/-3)
        power::apply(&mut state, &content, pid, enemy, Dec::from_i32(3), Some(pc), None).unwrap();
        // Second apply fires AfterPowerAmountChanged with power_handle (+1/-1)
        power::apply(&mut state, &content, pid, enemy, Dec::ONE, Some(pc), None).unwrap();
        assert_eq!(
            state.creature(enemy).unwrap().powers.iter().find(|p| p.id == target_stat).unwrap().amount,
            expected_delta
        );
        sts2_kernel::hooks::after_side_turn_end(&mut state, &content, CombatSide::Enemy, &[enemy]).unwrap();
        assert!(!state.creature(enemy).unwrap().powers.iter().any(|p| p.id == pid));
        assert!(!state.creature(enemy).unwrap().powers.iter().any(|p| p.id == target_stat));
    }
}

#[test]
fn shroud_sleight_of_flesh_vicious_tender_possess_and_paper_cuts() {
    let content = Sts2Content;
    let (mut state, enemy) = build_battle(CharacterId::Necrobinder, &[CardId::StrikeNecrobinder], 60);
    let pc = state.player.creature_handle;

    power::apply(&mut state, &content, PowerId::ShroudPower, pc, Dec::from_i32(5), Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::SleightOfFleshPower, pc, Dec::from_i32(4), Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::ViciousPower, pc, Dec::from_i32(1), Some(pc), None).unwrap();

    // Applying Doom to enemy triggers Shroud (+5 block) and SleightOfFlesh (4 dmg)
    power::apply(&mut state, &content, PowerId::DoomPower, enemy, Dec::from_i32(3), Some(pc), None).unwrap();
    assert_eq!(state.player.block, 5);
    assert_eq!(state.creature(enemy).unwrap().current_hp, 56);

    // PaperCuts reduces player max_hp on unblocked attack (break Shroud's 5 block first)
    power::apply(&mut state, &content, PowerId::PaperCutsPower, enemy, Dec::from_i32(7), Some(enemy), None).unwrap();
    damage::damage_targets(&mut state, &content, &[pc], Dec::from_i32(10), ValueProp::MOVE, Some(enemy), None, None).unwrap();
    assert_eq!(state.player.max_hp, 73);

    // PossessStrength steals and restores on enemy death (with a 2nd living enemy so combat isn't ending)
    let enemy2 = CreatureHandle(3);
    let mut e2 = state.creature(enemy).unwrap().clone();
    e2.handle = enemy2;
    e2.combat_id = Some(2);
    state.creatures.push(e2);
    state.enemy_handles.push(enemy2);
    state.invalidate_creature_hooks();
    power::apply(&mut state, &content, PowerId::PossessStrengthPower, enemy, Dec::ONE, Some(enemy), None).unwrap();
    power::apply(&mut state, &content, PowerId::StrengthPower, pc, Dec::from_i32(-3), Some(enemy), None).unwrap();
    creature_cmd::kill(&mut state, &content, enemy, None).unwrap();
    assert!(!state.creature(pc).unwrap().powers.iter().any(|p| p.id == PowerId::StrengthPower));
}

#[test]
fn monster_stun_and_death_powers_plow_shriek_ravenous_slumber_reattach_stock_surprise() {
    let content = NzTestDispatcher;
    let (state, enemy) = build_battle(CharacterId::Ironclad, &[], 50);
    let pc = state.player.creature_handle;

    // PlowPower on CeremonialBeast
    {
        let mut s = state.clone();
        s.creature_mut(enemy).unwrap().monster_id = Some(MonsterId::CeremonialBeast);
        s.creature_mut(enemy).unwrap().monster.as_mut().unwrap().next_move_can_transition_away = true;
        power::apply(&mut s, &content, PowerId::StrengthPower, enemy, Dec::from_i32(5), Some(enemy), None).unwrap();
        power::apply(&mut s, &content, PowerId::PlowPower, enemy, Dec::from_i32(45), Some(enemy), None).unwrap();
        damage::damage_targets(&mut s, &content, &[enemy], Dec::from_i32(10), ValueProp::MOVE, Some(pc), None, None).unwrap();
        assert_eq!(s.creature(enemy).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("STUNNED"));
        assert!(!s.creature(enemy).unwrap().powers.iter().any(|p| p.id == PowerId::StrengthPower));
    }

    // ShriekPower on TerrorEel
    {
        let mut s = state.clone();
        s.creature_mut(enemy).unwrap().monster_id = Some(MonsterId::TerrorEel);
        s.creature_mut(enemy).unwrap().monster.as_mut().unwrap().next_move_can_transition_away = true;
        power::apply(&mut s, &content, PowerId::ShriekPower, enemy, Dec::from_i32(40), Some(enemy), None).unwrap();
        damage::damage_targets(&mut s, &content, &[enemy], Dec::from_i32(12), ValueProp::MOVE, Some(pc), None, None).unwrap();
        assert_eq!(s.creature(enemy).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("STUNNED"));
        assert_eq!(
            s.creature(enemy).unwrap().monster.as_ref().unwrap().transient_move_follow_up_state_id.as_deref(),
            Some("TERROR_MOVE")
        );
    }

    // SlumberPower on SlumberingBeetle
    {
        let mut s = state.clone();
        s.creature_mut(enemy).unwrap().monster_id = Some(MonsterId::SlumberingBeetle);
        power::apply(&mut s, &content, PowerId::PlatingPower, enemy, Dec::from_i32(10), Some(enemy), None).unwrap();
        power::apply(&mut s, &content, PowerId::SlumberPower, enemy, Dec::from_i32(1), Some(enemy), None).unwrap();
        sts2_kernel::hooks::after_side_turn_end(&mut s, &content, CombatSide::Enemy, &[enemy]).unwrap();
        assert!(!s.creature(enemy).unwrap().powers.iter().any(|p| p.id == PowerId::PlatingPower));
        assert_eq!(s.creature(enemy).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("ROLL_OUT_MOVE"));
    }

    // ReattachPower on DecimillipedeSegment with living teammate + ShowOff fatal gate
    {
        let mut s = state.clone();
        let seg2 = CreatureHandle(3);
        let mut c2 = s.creature(enemy).unwrap().clone();
        c2.handle = seg2;
        c2.combat_id = Some(2);
        c2.monster_id = Some(MonsterId::DecimillipedeSegmentMiddle);
        s.creatures.push(c2);
        s.enemy_handles.push(seg2);
        s.creature_mut(enemy).unwrap().monster_id = Some(MonsterId::DecimillipedeSegmentFront);
        power::apply(&mut s, &content, PowerId::ReattachPower, enemy, Dec::from_i32(25), Some(enemy), None).unwrap();
        let reattach_inst = *s.creature(enemy).unwrap().powers.iter().find(|p| p.id == PowerId::ReattachPower).unwrap();
        let dummy_h = push_power(&mut s, enemy, PowerId::StrengthPower, 1, Some(enemy), Vec::new());
        let dummy_inst = *s.creature(enemy).unwrap().powers.iter().find(|p| p.handle == dummy_h).unwrap();
        assert!(!sts2_kernel::hooks::should_owner_death_trigger_fatal(&mut s, &content, enemy).unwrap());
        assert!(sts2_kernel::hooks::should_power_be_removed_on_death(&mut s, &content, enemy, dummy_inst).unwrap());
        assert!(!sts2_kernel::hooks::should_power_be_removed_on_death(&mut s, &content, enemy, reattach_inst).unwrap());
        s.creature_mut(enemy).unwrap().current_hp = 0;
        sts2_kernel::hooks::after_death(&mut s, &content, enemy, None, false).unwrap();
        assert_eq!(s.creature(enemy).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("DEAD_MOVE"));
        assert!(!sts2_kernel::hooks::should_allow_hitting(&mut s, &content, enemy).unwrap());
        batch_n_z::reattach_do_reattach(&content, &mut s, enemy, reattach_inst.handle).unwrap();
        assert_eq!(s.creature(enemy).unwrap().current_hp, 25);
        assert!(sts2_kernel::hooks::should_allow_hitting(&mut s, &content, enemy).unwrap());
    }
}

#[test]
fn card_affliction_replay_generation_and_energy_powers() {
    let content = NzTestDispatcher;
    let (mut state, enemy) = build_battle(
        CharacterId::Regent,
        &[CardId::StrikeRegent, CardId::DefendRegent, CardId::SovereignBlade],
        80,
    );
    let pc = state.player.creature_handle;

    // TangledPower afflicts attacks on AfterApplied and clears Entangled via AfterRemoved
    power::apply(&mut state, &content, PowerId::TangledPower, pc, Dec::ONE, Some(enemy), None).unwrap();
    assert_eq!(state.card(CardHandle(1)).unwrap().affliction.unwrap().id, AfflictionId::Entangled);
    assert_eq!(sts2_kernel::ordering::resolve_energy_cost(&mut state, &content, CardHandle(1)).unwrap(), 2);
    sts2_kernel::hooks::after_side_turn_end(&mut state, &content, CombatSide::Player, &[pc]).unwrap();
    assert!(state.card(CardHandle(1)).unwrap().affliction.is_none());

    // SwordSagePower adds replay count to SovereignBlade and removes on AfterRemoved (via power::remove_by_handle)
    power::apply(&mut state, &content, PowerId::SwordSagePower, pc, Dec::TWO, Some(pc), None).unwrap();
    let ss_h = state.creature(pc).unwrap().powers.iter().find(|p| p.id == PowerId::SwordSagePower).unwrap().handle;
    assert_eq!(state.card(CardHandle(3)).unwrap().base_replay_count, 2);
    let _ = power::remove_by_handle(&mut state, &content, pc, ss_h).unwrap();
    assert_eq!(state.card(CardHandle(3)).unwrap().base_replay_count, 0);

    // VitalSparkPower & RingingPower lifecycle (BeforeCombatStart / AfterPowerAmountChanged / AfterApplied / AfterRemoved)
    power::apply(&mut state, &content, PowerId::VitalSparkPower, pc, Dec::TWO, Some(enemy), None).unwrap();
    let vs_h = state.creature(pc).unwrap().powers.iter().find(|p| p.id == PowerId::VitalSparkPower).unwrap().handle;
    sts2_kernel::hooks::before_combat_start(&mut state, &content).unwrap();
    assert_eq!(state.card(CardHandle(2)).unwrap().affliction.unwrap().id, AfflictionId::Tainted);
    assert_eq!(state.card(CardHandle(2)).unwrap().affliction.unwrap().amount, 2);
    power::apply(&mut state, &content, PowerId::VitalSparkPower, pc, Dec::ONE, Some(enemy), None).unwrap();
    assert_eq!(state.card(CardHandle(2)).unwrap().affliction.unwrap().amount, 3);
    let _ = power::remove_by_handle(&mut state, &content, pc, vs_h).unwrap();
    assert!(state.card(CardHandle(2)).unwrap().affliction.is_none());

    power::apply(&mut state, &content, PowerId::RingingPower, pc, Dec::ONE, Some(enemy), None).unwrap();
    assert_eq!(state.card(CardHandle(1)).unwrap().affliction.unwrap().id, AfflictionId::Ringing);
    sts2_kernel::hooks::after_side_turn_end(&mut state, &content, CombatSide::Player, &[pc]).unwrap();
    assert!(state.card(CardHandle(1)).unwrap().affliction.is_none());

    // SpectrumShiftPower generates distinct colorless cards into hand before hand draw
    power::apply(&mut state, &content, PowerId::SpectrumShiftPower, pc, Dec::TWO, Some(pc), None).unwrap();
    sts2_kernel::hooks::before_hand_draw(&mut state, &content).unwrap();
    assert_eq!(state.player.hand.len(), 5);

    // NightmarePower clones selected card on BeforeHandDraw (SpectrumShiftPower also fires +2 -> 9)
    let nm_h = push_power(&mut state, pc, PowerId::NightmarePower, 2, Some(pc), Vec::new());
    batch_n_z::nightmare_set_selected_card(&mut state, pc, nm_h, CardHandle(1)).unwrap();
    sts2_kernel::hooks::before_hand_draw(&mut state, &content).unwrap();
    assert_eq!(state.player.hand.len(), 9);
}

#[test]
fn remaining_nz_powers_and_wired_kernel_hooks() {
    let content = NzTestDispatcher;
    let (mut state, enemy) = build_battle(CharacterId::Fool, &[CardId::FoolishShot], 80);
    let pc = state.player.creature_handle;

    // Pyre & WasteAway
    power::apply(&mut state, &content, PowerId::PyrePower, pc, Dec::TWO, Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::WasteAwayPower, pc, Dec::ONE, Some(enemy), None).unwrap();
    assert_eq!(sts2_kernel::hooks::modify_max_energy(&mut state, &content, Dec::from_i32(3)).unwrap(), Dec::from_i32(4));

    // Radiance, StarNextTurn, Spinner, RollingBoulder, YouAreMine
    power::apply(&mut state, &content, PowerId::RadiancePower, pc, Dec::TWO, Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::StarNextTurnPower, pc, Dec::from_i32(3), Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::SpinnerPower, pc, Dec::ONE, Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::RollingBoulderPower, pc, Dec::from_i32(5), Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::YouAreMinePower, pc, Dec::TWO, Some(enemy), None).unwrap();
    state.player.turn_number = 2;
    sts2_kernel::hooks::after_energy_reset(&mut state, &content).unwrap();
    assert_eq!(state.player.energy, 4);
    assert_eq!(state.player.stars, 3);
    assert_eq!(state.player.orbs.len(), 1);
    assert_eq!(state.player.orbs[0].id, OrbId::GlassOrb);
    sts2_kernel::hooks::after_player_turn_start(&mut state, &content).unwrap();
    assert_eq!(state.creature(enemy).unwrap().current_hp, 75);
    assert!(state.creature(pc).unwrap().powers.iter().any(|p| p.id == PowerId::WeakPower));

    // ReflectPower wired via damage::damage_targets -> blocked attack reflects blocked damage to attacker (6 * 1.5 Vuln = 9)
    power::apply(&mut state, &content, PowerId::ReflectPower, pc, Dec::from_i32(3), Some(pc), None).unwrap();
    block::gain_block(&mut state, &content, pc, Dec::from_i32(10), ValueProp::UNPOWERED, None, None).unwrap();
    damage::damage_targets(&mut state, &content, &[pc], Dec::from_i32(6), ValueProp::MOVE, Some(enemy), None, None).unwrap();
    assert_eq!(state.creature(enemy).unwrap().current_hp, 66);

    // ReaperFormPower wired via damage::damage_targets -> blocked + unblocked damage applies Doom (4 * 0.75 Weak = 3 -> 3*2 = 6 Doom)
    power::apply(&mut state, &content, PowerId::ReaperFormPower, pc, Dec::TWO, Some(pc), None).unwrap();
    block::gain_block(&mut state, &content, enemy, Dec::from_i32(3), ValueProp::UNPOWERED, None, None).unwrap();
    damage::damage_targets(&mut state, &content, &[enemy], Dec::from_i32(4), ValueProp::MOVE, Some(pc), None, None).unwrap();
    assert_eq!(state.creature(enemy).unwrap().powers.iter().find(|p| p.id == PowerId::DoomPower).unwrap().amount, 6);
    assert_eq!(state.creature(enemy).unwrap().current_hp, 66);

    // PainfulStabsPower & SuckPower wired via AttackBuilder::execute (2 hits against unblocked player)
    power::apply(&mut state, &content, PowerId::PainfulStabsPower, enemy, Dec::ONE, Some(enemy), None).unwrap();
    power::apply(&mut state, &content, PowerId::SuckPower, enemy, Dec::from_i32(3), Some(enemy), None).unwrap();
    state.creature_mut(pc).unwrap().block = 0;
    AttackBuilder::from_monster(enemy, Dec::from_i32(4))
        .with_hit_count(2)
        .execute(&mut state, &content)
        .unwrap();
    // Suck gives +3 Strength per hit group = +6 Strength; PainfulStabs adds 2 Wounds to Discard
    assert_eq!(state.creature(enemy).unwrap().powers.iter().find(|p| p.id == PowerId::StrengthPower).unwrap().amount, 6);
    assert_eq!(state.player.discard.iter().filter(|&&h| state.card(h).unwrap().id == CardId::Wound).count(), 2);

    // SkittishPower wired via AttackBuilder::from_card -> gains block once per turn
    power::apply(&mut state, &content, PowerId::SkittishPower, enemy, Dec::from_i32(7), Some(enemy), None).unwrap();
    let cp = CardPlayContext {
        card: CardHandle(1),
        target: Some(enemy),
        result_pile: PileType::Discard,
        is_auto_play: false,
        play_index: 0,
        play_count: 1,
        play_ordinal: 1,
        energy_spent: 1,
        energy_value: 1,
        stars_spent: 0,
        star_value: 0,
    };
    AttackBuilder::from_card(pc, CardHandle(1), cp, Dec::from_i32(2), AttackTargetSpec::Single(enemy))
        .execute(&mut state, &content)
        .unwrap();
    assert_eq!(state.creature(enemy).unwrap().block, 7);

    // NoDrawPower + PagestormPower + SpeedsterPower wired via card_pile::draw
    power::apply(&mut state, &content, PowerId::NoDrawPower, pc, Dec::ONE, Some(enemy), None).unwrap();
    let d1 = make_card(state.next_card_handle, CardId::StrikeIronclad, PileType::None);
    state.next_card_handle += 1;
    let d1_h = d1.handle;
    state.cards.push(d1);
    card_pile::add_sync(&mut state, d1_h, PileType::Draw, CardPilePosition::Top);
    assert!(card_pile::draw(&mut state, &content, 1, false).unwrap().is_empty());
    assert_eq!(card_pile::draw(&mut state, &content, 1, true).unwrap().as_slice(), &[d1_h]);

    let nd_h = state.creature(pc).unwrap().powers.iter().find(|p| p.id == PowerId::NoDrawPower).unwrap().handle;
    let _ = power::remove_by_handle(&mut state, &content, pc, nd_h).unwrap();
    let mut eth_card = make_card(state.next_card_handle, CardId::Dazed, PileType::None);
    eth_card.local_keywords |= CardKeywords::ETHEREAL;
    state.next_card_handle += 1;
    let eth_h = eth_card.handle;
    state.cards.push(eth_card);
    let follow_id = state.next_card_handle;
    let follow_h = CardHandle(follow_id);
    state.cards.push(make_card(follow_id, CardId::StrikeIronclad, PileType::None));
    state.next_card_handle += 1;
    card_pile::add_sync(&mut state, eth_h, PileType::Draw, CardPilePosition::Bottom);
    card_pile::add_sync(&mut state, follow_h, PileType::Draw, CardPilePosition::Bottom);
    power::apply(&mut state, &content, PowerId::PagestormPower, pc, Dec::ONE, Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::SpeedsterPower, pc, Dec::from_i32(5), Some(pc), None).unwrap();
    let hp_before_speed = state.creature(enemy).unwrap().current_hp;
    let drawn = card_pile::draw(&mut state, &content, 1, false).unwrap();
    assert_eq!(drawn.as_slice(), &[eth_h]);
    assert!(state.player.hand.contains(&follow_h));
    // Speedster deals 5 per non-hand draw * 2 draws (eth + follow) = 10 dmg against 7 block -> 0 block, 3 HP lost
    assert_eq!(state.creature(enemy).unwrap().block, 0);
    assert_eq!(state.creature(enemy).unwrap().current_hp, hp_before_speed - 3);

    // PillarOfCreation + Smokestack + TrashToTreasure + Soulbound wired via card_pile::add_generated_card_to_combat
    state.player.block = 0;
    power::apply(&mut state, &content, PowerId::PillarOfCreationPower, pc, Dec::from_i32(3), Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::SmokestackPower, pc, Dec::from_i32(4), Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::TrashToTreasurePower, pc, Dec::ONE, Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::SoulboundPower, pc, Dec::ONE, Some(enemy), None).unwrap();
    let _ = card_pile::add_generated_card_to_combat(&mut state, &content, CardId::Dazed, PileType::Discard, CardPilePosition::Bottom, 0, Some(pc)).unwrap();
    assert_eq!(state.player.block, 3);
    // 3 (Speedster) + 4 (Smokestack) + 8 (evoking GlassOrb from Spinner slot cap 1) = 15
    assert_eq!(state.creature(enemy).unwrap().current_hp, hp_before_speed - 15);
    assert_eq!(state.player.orbs.len(), 1);
    let draw_before_soul = state.player.draw.len();
    let _ = card_pile::add_generated_card_to_combat(&mut state, &content, CardId::Soul, PileType::Hand, CardPilePosition::Bottom, 0, Some(enemy)).unwrap();
    assert_eq!(state.player.draw.len(), draw_before_soul + 1);

    // ThunderPower wired via orb::evoke_next(LightningOrb)
    state.player.orbs.clear();
    orb::add_slots(&mut state, 1);
    orb::channel(&mut state, &content, OrbId::LightningOrb).unwrap();
    power::apply(&mut state, &content, PowerId::ThunderPower, pc, Dec::from_i32(6), Some(pc), None).unwrap();
    let hp_before_thunder = state.creature(enemy).unwrap().current_hp;
    orb::evoke_next(&mut state, &content, true).unwrap();
    // Lightning evoke (8) + Thunder (6) = 14
    assert_eq!(state.creature(enemy).unwrap().current_hp, hp_before_thunder - 14);

    // QuietusPower wired via arrow::try_shoot_arrow
    power::apply(&mut state, &content, PowerId::QuietusPower, pc, Dec::TWO, Some(pc), None).unwrap();
    let shot = arrow::try_shoot_arrow(&mut state, &content, Some(CardHandle(1))).unwrap();
    assert_eq!(shot, Some(0));
    assert!(state.player.arrows[0].destroy_after_impaled);

    // SurroundedPower wired via hooks::before_potion_used
    let surr_h = push_power(&mut state, pc, PowerId::SurroundedPower, 1, Some(enemy), Vec::new());
    power::apply(&mut state, &content, PowerId::BackAttackLeftPower, enemy, Dec::ONE, Some(enemy), None).unwrap();
    let dmg_back = sts2_kernel::hooks::modify_damage(&mut state, &content, Some(pc), Some(enemy), Dec::from_i32(10), ValueProp::MOVE, None, None).unwrap();
    // (10 + 6 Str) * 1.5 Vuln * 1.5 Surrounded = 36
    assert_eq!(dmg_back, Dec::from_i32(36));
    sts2_kernel::hooks::before_potion_used(&mut state, &content, PotionId::FirePotion, Some(enemy)).unwrap();
    let dmg_after_turn = sts2_kernel::hooks::modify_damage(&mut state, &content, Some(pc), Some(enemy), Dec::from_i32(10), ValueProp::MOVE, None, None).unwrap();
    assert_eq!(dmg_after_turn, Dec::from_i32(24));
    let _ = surr_h;

    let th_h = push_power(
        &mut state,
        enemy,
        PowerId::ThieveryPower,
        15,
        Some(enemy),
        vec![("ThieveryPower.<Target>k__BackingField", FieldValue::CreatureHandle(pc.0 as i32))],
    );
    assert_eq!(batch_n_z::thievery_steal(&mut state, enemy, th_h).unwrap(), 15);
    assert_eq!(state.player.gold, 85);

    assert_eq!(batch_n_z::royalties_reward_gold(&state, pc, 25, RoomType::Monster), 25);

    // Engine NeedsChoice + resume_choice for ToolsOfTheTradePower, TyrannyPower, StratagemPower
    let (mut choice_state, ch_enemy) = build_battle(
        CharacterId::Silent,
        &[
            CardId::StrikeSilent,
            CardId::DefendSilent,
            CardId::StrikeSilent,
            CardId::DefendSilent,
            CardId::StrikeSilent,
        ],
        80,
    );
    let _ = ch_enemy;
    let c_pc = choice_state.player.creature_handle;
    let hand_cards: Vec<CardHandle> = choice_state.player.hand.iter().copied().collect();
    for h in hand_cards {
        card_pile::add_sync(&mut choice_state, h, PileType::Discard, CardPilePosition::Bottom);
    }
    choice_state.player.energy = 3;
    power::apply(&mut choice_state, &content, PowerId::StratagemPower, c_pc, Dec::ONE, Some(c_pc), None).unwrap();
    power::apply(&mut choice_state, &content, PowerId::ToolsOfTheTradePower, c_pc, Dec::ONE, Some(c_pc), None).unwrap();
    power::apply(&mut choice_state, &content, PowerId::TyrannyPower, c_pc, Dec::ONE, Some(c_pc), None).unwrap();

    // start_turn draws 7 -> shuffles Discard -> pauses for Stratagem (AfterShuffle), then pauses for ToolsOfTheTrade & Tyranny (AfterPlayerTurnStart)
    let out1 = CombatEngine::start_turn(&mut choice_state, &content, false).unwrap();
    let StepOutcome::NeedsChoice(req1) = out1 else {
        panic!("expected Stratagem choice, got {out1:?}");
    };
    assert_eq!(req1.source_id, "POWER.STRATAGEM_POWER");
    let out2 = CombatEngine::resume_choice(&mut choice_state, &content, &[0]).unwrap();
    let StepOutcome::NeedsChoice(req2) = out2 else {
        panic!("expected ToolsOfTheTrade choice, got {out2:?}");
    };
    assert_eq!(req2.source_id, "POWER.TOOLS_OF_THE_TRADE_POWER");
    let out3 = CombatEngine::resume_choice(&mut choice_state, &content, &[0]).unwrap();
    let StepOutcome::NeedsChoice(req3) = out3 else {
        panic!("expected Tyranny choice, got {out3:?}");
    };
    assert_eq!(req3.source_id, "POWER.TYRANNY_POWER");
    let out4 = CombatEngine::resume_choice(&mut choice_state, &content, &[0]).unwrap();
    assert_eq!(out4, StepOutcome::Completed);
    assert_eq!(choice_state.player.discard.len(), 1);
    assert_eq!(choice_state.player.exhaust.len(), 1);
    assert_eq!(choice_state.player.hand.len(), 3);
}

#[test]
fn remaining_triggered_nz_powers_coverage() {
    let content = NzTestDispatcher;
    let (mut state, enemy) = build_battle(
        CharacterId::Necrobinder,
        &[CardId::StrikeNecrobinder, CardId::DefendNecrobinder, CardId::StrikeNecrobinder],
        80,
    );
    let pc = state.player.creature_handle;

    // Nemesis toggles Intangible every other turn end
    power::apply(&mut state, &content, PowerId::NemesisPower, enemy, Dec::ONE, Some(enemy), None).unwrap();
    sts2_kernel::hooks::after_side_turn_end(&mut state, &content, CombatSide::Enemy, &[enemy]).unwrap();
    assert!(state.creature(enemy).unwrap().powers.iter().any(|p| p.id == PowerId::IntangiblePower));
    sts2_kernel::hooks::after_side_turn_end(&mut state, &content, CombatSide::Enemy, &[enemy]).unwrap();
    assert!(!state.creature(enemy).unwrap().powers.iter().any(|p| p.id == PowerId::IntangiblePower));

    // Neurosurge applies Doom at turn start
    power::apply(&mut state, &content, PowerId::NeurosurgePower, pc, Dec::from_i32(4), Some(enemy), None).unwrap();
    sts2_kernel::hooks::after_side_turn_start(&mut state, &content, CombatSide::Player, &[pc]).unwrap();
    assert_eq!(state.creature(pc).unwrap().powers.iter().find(|p| p.id == PowerId::DoomPower).unwrap().amount, 4);

    // Shadowmeld, Soar, Tank, Tracking, Territorial, SelfFormingClay, ToricToughness
    power::apply(&mut state, &content, PowerId::ShadowmeldPower, pc, Dec::TWO, Some(pc), None).unwrap();
    let (blk, _) = sts2_kernel::hooks::modify_block(&mut state, &content, pc, Dec::from_i32(5), ValueProp::MOVE, None, None).unwrap();
    assert_eq!(blk, Dec::from_i32(20));

    power::apply(&mut state, &content, PowerId::SoarPower, enemy, Dec::ONE, Some(enemy), None).unwrap();
    power::apply(&mut state, &content, PowerId::WeakPower, enemy, Dec::ONE, Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::TrackingPower, pc, Dec::from_i32(50), Some(pc), None).unwrap();
    let dmg = sts2_kernel::hooks::modify_damage(&mut state, &content, Some(enemy), Some(pc), Dec::from_i32(10), ValueProp::MOVE, Some(CardHandle(1)), None).unwrap();
    assert_eq!(dmg, Dec::from_i32(7).checked_add(Dec::HALF).unwrap());

    power::apply(&mut state, &content, PowerId::SelfFormingClayPower, pc, Dec::from_i32(6), Some(pc), None).unwrap();
    push_power(
        &mut state,
        pc,
        PowerId::ToricToughnessPower,
        1,
        Some(pc),
        vec![("ToricToughnessPower.<StoredBlock>k__BackingField", FieldValue::Int(4))],
    );
    sts2_kernel::hooks::after_block_cleared(&mut state, &content, pc).unwrap();
    assert_eq!(state.player.block, 40); // Shadowmeld 4x * (6 + 4) = 40

    // StockPower spawns Axebot(amount - 1) on death; SurprisePower spawns Sneaky + FatGremlin
    power::apply(&mut state, &content, PowerId::StockPower, enemy, Dec::TWO, Some(enemy), None).unwrap();
    creature_cmd::kill(&mut state, &content, enemy, None).unwrap();
    assert!(state.enemy_handles.iter().any(|&h| state.creature(h).is_some_and(|c| c.monster_id == Some(MonsterId::Axebot))));
}

#[test]
fn cruelty_debilitate_and_vulnerable_weak_cross_batch() {
    let content = Sts2Content;
    let (mut state, enemy) = build_battle(
        CharacterId::Ironclad,
        &[CardId::StrikeIronclad; 5],
        100,
    );
    let pc = state.player.creature_handle;

    // 1. VulnerablePower (N-Z) * CrueltyPower(50) (A-M) * DebilitatePower (A-M):
    // base 1.5 -> +0.50 Cruelty = 2.0 -> Debilitate (2.0 + 1.0) = 3.0 => 10 * 3 = 30
    power::apply(&mut state, &content, PowerId::VulnerablePower, enemy, Dec::TWO, Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::CrueltyPower, pc, Dec::from_i32(50), Some(pc), None).unwrap();
    power::apply(&mut state, &content, PowerId::DebilitatePower, enemy, Dec::TWO, Some(pc), None).unwrap();
    let dmg_vuln = sts2_kernel::hooks::modify_damage(
        &mut state,
        &content,
        Some(enemy),
        Some(pc),
        Dec::from_i32(10),
        ValueProp::MOVE,
        Some(CardHandle(1)),
        None,
    )
    .unwrap();
    assert_eq!(dmg_vuln, Dec::from_i32(30));

    // 2. WeakPower (N-Z) * DebilitatePower (A-M) on attacker:
    // base 0.75 -> Debilitate (0.75 - 0.25) = 0.50 => 10 * 3.0 * 0.50 = 15
    power::apply(&mut state, &content, PowerId::WeakPower, pc, Dec::TWO, Some(enemy), None).unwrap();
    power::apply(&mut state, &content, PowerId::DebilitatePower, pc, Dec::TWO, Some(enemy), None).unwrap();
    let dmg_both = sts2_kernel::hooks::modify_damage(
        &mut state,
        &content,
        Some(enemy),
        Some(pc),
        Dec::from_i32(10),
        ValueProp::MOVE,
        Some(CardHandle(1)),
        None,
    )
    .unwrap();
    assert_eq!(dmg_both, Dec::from_i32(15));
}
