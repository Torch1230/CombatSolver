use smallvec::{smallvec, SmallVec};
use sts2_kernel::commands::{card_pile, power};
use sts2_kernel::{
    BattleState, CardHandle, CardInst, CardTempFlags, ChoiceRequest, ChoiceTiming,
    ContentDispatcher, CreatureHandle, HookArgs, HookResult, KernelError,
};
use sts2_types::{
    CardId, CardKeywords, CardPilePosition, CardStaticFlags, CombatSide, Dec, DominionId, GameMode,
    HookMask, HookSlot, PileType, PotionId, PowerId, RelicId, RoomType, RunModelId,
    generated_card_meta,
};

pub const fn is_special_mode(mode: GameMode) -> bool {
    matches!(mode, GameMode::Dread | GameMode::Blitz)
}

pub fn is_card_allowed_in_mode(mode: GameMode, id: CardId) -> bool {
    if matches!(mode, GameMode::Blitz) {
        generated_card_meta(id)
            .flags
            .contains(CardStaticFlags::IS_ALLOWED_IN_BLITZ)
    } else {
        true
    }
}

pub fn is_card_allowed_in_run(mode: GameMode, id: CardId) -> bool {
    if generated_card_meta(id)
        .flags
        .contains(CardStaticFlags::IS_MULTIPLAYER_ONLY)
    {
        return false;
    }
    is_card_allowed_in_mode(mode, id)
}

pub fn filter_cards_for_mode(mode: GameMode, pool: &[CardId]) -> Vec<CardId> {
    pool.iter()
        .copied()
        .filter(|&id| is_card_allowed_in_run(mode, id))
        .collect()
}

pub const fn is_relic_allowed_in_mode(mode: GameMode, id: RelicId) -> bool {
    if !matches!(mode, GameMode::Blitz) {
        return true;
    }
    !matches!(
        id,
        RelicId::AmethystAubergine
            | RelicId::BlackStar
            | RelicId::BookOfFiveRings
            | RelicId::BowlerHat
            | RelicId::CursedPearl
            | RelicId::DowsingRod
            | RelicId::DragonFruit
            | RelicId::DreamCatcher
            | RelicId::Driftwood
            | RelicId::Ectoplasm
            | RelicId::EternalFeather
            | RelicId::FishingRod
            | RelicId::FurCoat
            | RelicId::Girya
            | RelicId::GoldenCompass
            | RelicId::GoldenPearl
            | RelicId::JuzuBracelet
            | RelicId::LastingCandy
            | RelicId::LordsParasol
            | RelicId::LuckyFysh
            | RelicId::MawBank
            | RelicId::MealTicket
            | RelicId::MeatCleaver
            | RelicId::MembershipCard
            | RelicId::MiniatureTent
            | RelicId::OldCoin
            | RelicId::PaelsClaw
            | RelicId::PaelsGrowth
            | RelicId::PaelsWing
            | RelicId::Planisphere
            | RelicId::PrayerWheel
            | RelicId::PumpkinCandle
            | RelicId::RegalPillow
            | RelicId::SealOfGold
            | RelicId::Shovel
            | RelicId::SignetRing
            | RelicId::SilkenTress
            | RelicId::StoneHumidifier
            | RelicId::TheCourier
            | RelicId::TinyMailbox
            | RelicId::ToyBox
            | RelicId::VenerableTeaSet
            | RelicId::WhiteBeastStatue
            | RelicId::WingedBoots
    )
}

pub const fn is_potion_allowed_in_mode(_mode: GameMode, _id: PotionId) -> bool {
    true
}

pub const fn max_ascension_for_mode(mode: GameMode) -> u8 {
    match mode {
        GameMode::Blitz => 5,
        GameMode::Standard | GameMode::Dread => 10,
    }
}

pub use sts2_types::{blitz_ascension_level, is_ascension_active_in_mode};

pub fn is_weak_encounter(encounter_id: &str) -> bool {
    let entry = encounter_id.strip_prefix("ENCOUNTER.").unwrap_or(encounter_id);
    matches!(
        entry,
        "BOWLBUGS_WEAK"
            | "CORPSE_SLUGS_WEAK"
            | "DEVOTED_SCULPTOR_WEAK"
            | "EXOSKELETONS_WEAK"
            | "FUZZY_WURM_CRAWLER_WEAK"
            | "NIBBITS_WEAK"
            | "SCROLLS_OF_BITING_WEAK"
            | "SEAPUNK_WEAK"
            | "SHRINKER_BEETLE_WEAK"
            | "SLIMES_WEAK"
            | "SLUDGE_SPINNER_WEAK"
            | "THIEVING_HOPPER_WEAK"
            | "TOADPOLES_WEAK"
            | "TUNNELER_WEAK"
            | "TURRET_OPERATOR_WEAK"
    )
}

pub const fn waterfall_giant_explosive_amount(
    initial_monster_count: usize,
    is_weak: bool,
    room_type: RoomType,
) -> i32 {
    if initial_monster_count > 1 {
        3
    } else if is_weak {
        4
    } else if matches!(room_type, RoomType::Elite) {
        7
    } else {
        5
    }
}

pub fn waterfall_giant_amount_for_state(state: &BattleState) -> i32 {
    let initial_count = if state.initial_encounter_monster_count > 0 {
        state.initial_encounter_monster_count
    } else {
        state.enemy_handles.len()
    };
    let is_weak =
        state.static_ctx.encounter_is_weak || is_weak_encounter(&state.static_ctx.encounter_id);
    waterfall_giant_explosive_amount(initial_count, is_weak, state.static_ctx.room_type)
}

pub const fn vantom_slippery_amount(room_type: RoomType, is_room_entry: bool) -> i32 {
    if is_room_entry && matches!(room_type, RoomType::Elite) {
        2
    } else {
        1
    }
}

pub const fn knowledge_demon_choice_cards() -> [CardId; 2] {
    [CardId::Sloth, CardId::Amnesia]
}

pub fn apply_knowledge_demon_choice(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    chosen_card_id: CardId,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    let pc = state.player.creature_handle;
    match chosen_card_id {
        CardId::Sloth => {
            let _ = power::apply(
                state,
                content,
                PowerId::SlothPower,
                pc,
                Dec::from_i32(3),
                None,
                card_source,
            )?;
            Ok(())
        }
        CardId::Amnesia => {
            let _ = power::apply(
                state,
                content,
                PowerId::AmnesiaPower,
                pc,
                Dec::ONE,
                Some(pc),
                card_source,
            )?;
            Ok(())
        }
        _ => Err(KernelError::InvalidAction(format!(
            "invalid KnowledgeDemonDominion choice card: {}",
            chosen_card_id.model_id_str()
        ))),
    }
}

fn create_ephemeral_choice_card(state: &mut BattleState, card_id: CardId) -> CardHandle {
    let meta = generated_card_meta(card_id);
    let handle = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    let mut temp_flags = CardTempFlags::empty();
    if meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST) {
        temp_flags |= CardTempFlags::COSTS_X_ENERGY;
    }
    state.cards.push(CardInst {
        handle,
        id: card_id,
        upgrade_level: 0,
        canonical_base_energy_cost: meta
            .canonical_energy_cost
            .clamp(i16::MIN as i32, i16::MAX as i32) as i16,
        base_energy_cost: meta
            .canonical_energy_cost
            .clamp(i16::MIN as i32, i16::MAX as i32) as i16,
        captured_energy_x_value: 0,
        base_replay_count: 0,
        floor_added_to_deck: sts2_kernel::OptI16::NONE,
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
        pile: PileType::None,
        removed_from_state: false,
        transform_into_after_play: None,
    });
    handle
}

pub fn dominion_hook_mask(id: DominionId) -> Result<HookMask, KernelError> {
    match id {
        DominionId::AeonglassDominion
        | DominionId::InsatiableDominion
        | DominionId::KinDominion
        | DominionId::LagavulinMatriarchDominion
        | DominionId::QueenDominion => Ok(HookMask::EMPTY.with(HookSlot::AfterRoomEntered)),
        DominionId::CeremonialBeastDominion
        | DominionId::KaiserCrabDominion
        | DominionId::TestSubjectDominion
        | DominionId::VantomDominion
        | DominionId::WaterfallGiantDominion => Ok(HookMask::EMPTY
            .with(HookSlot::AfterRoomEntered)
            .with(HookSlot::AfterCreatureAddedToCombat)),
        DominionId::SoulFyshDominion => Ok(HookMask::EMPTY.with(HookSlot::BeforeCombatStart)),
        DominionId::KnowledgeDemonDominion => Ok(HookMask::EMPTY.with(HookSlot::BeforeHandDraw)),
    }
}

pub fn dispatch_dominion_hook(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    id: DominionId,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    match id {
        DominionId::AeonglassDominion => {
            if slot == HookSlot::AfterRoomEntered {
                let pc = state.player.creature_handle;
                let _ = power::apply(
                    state,
                    content,
                    PowerId::AeonglassDominionPower,
                    pc,
                    Dec::ONE,
                    None,
                    None,
                )?;
            }
            Ok(HookResult::default())
        }
        DominionId::CeremonialBeastDominion => match slot {
            HookSlot::AfterRoomEntered => {
                let enemies: SmallVec<[CreatureHandle; 8]> =
                    SmallVec::from_slice(&state.enemy_handles);
                for enemy in enemies {
                    let _ = power::apply(
                        state,
                        content,
                        PowerId::RitualPower,
                        enemy,
                        Dec::ONE,
                        None,
                        None,
                    )?;
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCreatureAddedToCombat => {
                if let HookArgs::Creature(ch) = args {
                    if state
                        .creature(*ch)
                        .is_some_and(|c| c.side != CombatSide::Player)
                    {
                        let _ = power::apply(
                            state,
                            content,
                            PowerId::RitualPower,
                            *ch,
                            Dec::ONE,
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        DominionId::InsatiableDominion => {
            if slot == HookSlot::AfterRoomEntered {
                let pc = state.player.creature_handle;
                let _ = power::apply(
                    state,
                    content,
                    PowerId::InsatiableDominionPower,
                    pc,
                    Dec::ONE,
                    None,
                    None,
                )?;
            }
            Ok(HookResult::default())
        }
        DominionId::KaiserCrabDominion => match slot {
            HookSlot::AfterRoomEntered => {
                let enemies: SmallVec<[CreatureHandle; 8]> =
                    SmallVec::from_slice(&state.enemy_handles);
                for enemy in enemies {
                    let _ = power::apply(
                        state,
                        content,
                        PowerId::PlatingPower,
                        enemy,
                        Dec::from_i32(7),
                        None,
                        None,
                    )?;
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCreatureAddedToCombat => {
                if let HookArgs::Creature(ch) = args {
                    if state
                        .creature(*ch)
                        .is_some_and(|c| c.side != CombatSide::Player)
                    {
                        let _ = power::apply(
                            state,
                            content,
                            PowerId::PlatingPower,
                            *ch,
                            Dec::from_i32(7),
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        DominionId::KinDominion => {
            if slot == HookSlot::AfterRoomEntered {
                let pc = state.player.creature_handle;
                let _ = power::apply(
                    state,
                    content,
                    PowerId::WeakPower,
                    pc,
                    Dec::from_i32(2),
                    None,
                    None,
                )?;
                let _ = power::apply(
                    state,
                    content,
                    PowerId::FrailPower,
                    pc,
                    Dec::from_i32(2),
                    None,
                    None,
                )?;
            }
            Ok(HookResult::default())
        }
        DominionId::KnowledgeDemonDominion => {
            if slot == HookSlot::BeforeHandDraw {
                if state.player.turn_number > 1
                    || state.player_creature().is_none_or(|c| c.is_dead())
                {
                    return Ok(HookResult::default());
                }
                let sloth_h = create_ephemeral_choice_card(state, CardId::Sloth);
                let amnesia_h = create_ephemeral_choice_card(state, CardId::Amnesia);
                return Ok(HookResult {
                    needs_choice: Some(ChoiceRequest {
                        timing: ChoiceTiming::PlayerTurnStart,
                        source_id: DominionId::KnowledgeDemonDominion.model_id_str().into(),
                        source_pile: PileType::None,
                        min_count: 1,
                        max_count: 1,
                        candidates: smallvec![sloth_h, amnesia_h],
                        can_skip: false,
                    }),
                    ..Default::default()
                });
            }
            Ok(HookResult::default())
        }
        DominionId::LagavulinMatriarchDominion => {
            if slot == HookSlot::AfterRoomEntered {
                let pc = state.player.creature_handle;
                let _ = power::apply(
                    state,
                    content,
                    PowerId::LagavulinMatriarchDominionPower,
                    pc,
                    Dec::ONE,
                    None,
                    None,
                )?;
            }
            Ok(HookResult::default())
        }
        DominionId::QueenDominion => {
            if slot == HookSlot::AfterRoomEntered {
                let pc = state.player.creature_handle;
                let _ = power::apply(
                    state,
                    content,
                    PowerId::YouAreMinePower,
                    pc,
                    Dec::from_i32(3),
                    None,
                    None,
                )?;
            }
            Ok(HookResult::default())
        }
        DominionId::SoulFyshDominion => {
            if slot == HookSlot::BeforeCombatStart {
                let pc = state.player.creature_handle;
                let _ = card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    CardId::Beckon,
                    PileType::Draw,
                    CardPilePosition::Random,
                    0,
                    Some(pc),
                )?;
            }
            Ok(HookResult::default())
        }
        DominionId::TestSubjectDominion => match slot {
            HookSlot::AfterRoomEntered => {
                let enemies: SmallVec<[CreatureHandle; 8]> =
                    SmallVec::from_slice(&state.enemy_handles);
                for enemy in enemies {
                    let _ = power::apply(
                        state,
                        content,
                        PowerId::StrengthPower,
                        enemy,
                        Dec::from_i32(3),
                        None,
                        None,
                    )?;
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCreatureAddedToCombat => {
                if let HookArgs::Creature(ch) = args {
                    if state
                        .creature(*ch)
                        .is_some_and(|c| c.side != CombatSide::Player)
                    {
                        let _ = power::apply(
                            state,
                            content,
                            PowerId::StrengthPower,
                            *ch,
                            Dec::from_i32(3),
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        DominionId::VantomDominion => match slot {
            HookSlot::AfterRoomEntered => {
                let amount = vantom_slippery_amount(state.static_ctx.room_type, true);
                let enemies: SmallVec<[CreatureHandle; 8]> =
                    SmallVec::from_slice(&state.enemy_handles);
                for enemy in enemies {
                    let _ = power::apply(
                        state,
                        content,
                        PowerId::SlipperyPower,
                        enemy,
                        Dec::from_i32(amount),
                        None,
                        None,
                    )?;
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCreatureAddedToCombat => {
                if let HookArgs::Creature(ch) = args {
                    if state
                        .creature(*ch)
                        .is_some_and(|c| c.side != CombatSide::Player)
                    {
                        let amount = vantom_slippery_amount(state.static_ctx.room_type, false);
                        let _ = power::apply(
                            state,
                            content,
                            PowerId::SlipperyPower,
                            *ch,
                            Dec::from_i32(amount),
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        DominionId::WaterfallGiantDominion => match slot {
            HookSlot::AfterRoomEntered => {
                let amount = waterfall_giant_amount_for_state(state);
                let enemies: SmallVec<[CreatureHandle; 8]> =
                    SmallVec::from_slice(&state.enemy_handles);
                for enemy in enemies {
                    let _ = power::apply(
                        state,
                        content,
                        PowerId::ExplosivePower,
                        enemy,
                        Dec::from_i32(amount),
                        None,
                        None,
                    )?;
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCreatureAddedToCombat => {
                if let HookArgs::Creature(ch) = args {
                    let eligible = state.creature(*ch).is_some_and(|c| {
                        c.side != CombatSide::Player
                            && !c.powers.iter().any(|p| p.id == PowerId::MinionPower)
                    });
                    if eligible {
                        let amount = waterfall_giant_amount_for_state(state);
                        let _ = power::apply(
                            state,
                            content,
                            PowerId::ExplosivePower,
                            *ch,
                            Dec::from_i32(amount),
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
    }
}

pub fn resume_dominion_hook_choice(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    id: DominionId,
    slot: HookSlot,
    selected: &[CardHandle],
) -> Result<HookResult, KernelError> {
    if matches!(id, DominionId::KnowledgeDemonDominion) && slot == HookSlot::BeforeHandDraw {
        for c in &mut state.cards {
            if c.pile == PileType::None && matches!(c.id, CardId::Sloth | CardId::Amnesia) {
                c.removed_from_state = true;
            }
        }
        state.invalidate_hook_cache();
        if let Some(&chosen_handle) = selected.first() {
            let chosen_id = state
                .card(chosen_handle)
                .ok_or_else(|| {
                    KernelError::InvalidState(
                        "missing KnowledgeDemonDominion choice card".to_owned(),
                    )
                })?
                .id;
            apply_knowledge_demon_choice(content, state, chosen_id, Some(chosen_handle))?;
        }
    }
    Ok(HookResult::default())
}

pub fn run_model_hook_mask(id: RunModelId) -> Result<HookMask, KernelError> {
    match id {
        RunModelId::BlitzRunModel | RunModelId::DreadRunModel => Ok(HookMask::EMPTY),
    }
}

pub fn dispatch_run_model_hook(
    _content: &dyn ContentDispatcher,
    _state: &mut BattleState,
    id: RunModelId,
    _slot: HookSlot,
    _args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    match id {
        RunModelId::BlitzRunModel | RunModelId::DreadRunModel => Ok(HookResult::default()),
    }
}
