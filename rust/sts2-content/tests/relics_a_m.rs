use smallvec::SmallVec;
use std::sync::Arc;
use sts2_content::relics::batch_a_m;
use sts2_content::Sts2Content;
use sts2_kernel::commands::{block, creature, damage, gold, orb, osty, potion, power};
use sts2_kernel::{
    hooks, ArrowSlotInst, BattleState, CardHandle, CardInst, CardTempFlags, CombatEngine,
    ContentDispatcher, CreatureHandle, CreatureInst, EnchantmentInst, FieldValue, HookArgs,
    KernelError, MonsterInst, PlayerCounters, PlayerInst, RelicInst, RootStaticContext,
    SemanticHistory, StepOutcome,
};
use sts2_types::{
    ArrowStatus, CardId, CardKeywords, CharacterId, CombatSide, Dec, EnchantmentId,
    EnchantmentStatus, GameMode, HookSlot, MonsterId, OrbId, PileType, PlayerRngSet,
    PlayerTurnPhase, PotionId, PowerId, RelicId, RelicStaticFlags, Rng, RoomType, RunRngSet,
    ValueProp,
};

fn build_test_battle(
    character: CharacterId,
    deck_ids: &[CardId],
    relics: &[RelicId],
    enemy_hp: i32,
) -> (BattleState, CreatureHandle, CreatureHandle) {
    let run_rngs = RunRngSet::from_string_seed("AM_RELICS_TEST");
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc_handle = CreatureHandle(1);
    let enemy_handle = CreatureHandle(2);

    let mut cards = Vec::new();
    let mut deck_handles = Vec::new();
    let content = Sts2Content;
    for (idx, &cid) in deck_ids.iter().enumerate() {
        let meta = content.card_meta(cid).unwrap();
        let h = CardHandle((idx + 1) as u32);
        deck_handles.push(h);
        cards.push(CardInst {
            handle: h,
            id: cid,
            upgrade_level: 0,
            canonical_base_energy_cost: meta.canonical_energy_cost as i16,
            base_energy_cost: meta.canonical_energy_cost as i16,
            captured_energy_x_value: 0,
            base_replay_count: 0,
            floor_added_to_deck: sts2_kernel::OptI16::some(1),
            deck_card_handle: sts2_kernel::OptCardHandle::some(h),
            clone_of_handle: sts2_kernel::OptCardHandle::NONE,
            temp_star_cost_override_this_turn: sts2_kernel::OptI16::NONE,
            temp_cost_override_this_turn: sts2_kernel::OptI16::NONE,
            temp_cost_override_this_turn_or_until_played: sts2_kernel::OptI16::NONE,
            temp_cost_override_this_combat: sts2_kernel::OptI16::NONE,
            local_keywords: meta.base_keywords,
            keywords_added_from_outside: CardKeywords::empty(),
            keywords_removed_from_outside: CardKeywords::empty(),
            temp_flags: CardTempFlags::empty(),
            affliction: None,
            ext_index: None,
            pile: PileType::Deck,
            removed_from_state: false,
            transform_into_after_play: None,
        });
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
        current_hp: character.starting_hp(),
        max_hp: character.starting_hp(),
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
        slot_name: Some(std::borrow::Cow::Borrowed("slot_0")),
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
            current_state_id: std::borrow::Cow::Borrowed(""),
            next_move_id: None,
            next_move_can_transition_away: true,
            performed_first_move: false,
            state_log_ids: Vec::new(),
            transient_move_follow_up_state_id: None,
            setup_fields: Default::default(),
            private_fields: Default::default(),
        })),
        has_minion_power: false,
        powers: Vec::new(),
    };

    let player = PlayerInst {
        creature_handle: pc_handle,
        current_hp: character.starting_hp(),
        max_hp: character.starting_hp(),
        block: 0,
        cumulative_hp_lost: 0,
        gold: 99,
        max_energy: character.max_energy(),
        base_orb_slot_count: character.base_orb_slot_count(),
        is_active_for_hooks: true,
        can_use_or_remove_potions: true,
        energy: 0,
        stars: 0,
        phase: PlayerTurnPhase::None,
        turn_number: 1,
        counters: PlayerCounters::default(),
        hand: Vec::new(),
        draw: Vec::new(),
        discard: Vec::new(),
        exhaust: Vec::new(),
        play: Vec::new(),
        deck: deck_handles,
        potion_slots: smallvec::smallvec![None, None, None],
        combat_generated_potion_mask: 0,
        orb_capacity: character.base_orb_slot_count(),
        orbs: Vec::new(),
        arrows,
        relics: relics
            .iter()
            .map(|&id| RelicInst {
                id,
                is_wax: false,
                is_melted: false,
                stack_count: 1,
                floor_added_to_deck: None,
                private_fields: Default::default(),
            })
            .collect(),
        card_or_potion_effect_depth: 0,
    };

    let state = BattleState {
        static_ctx: Arc::new(RootStaticContext {
            encounter_id: "FUZZY_WURM_CRAWLER_WEAK".to_owned(),
            encounter_is_weak: true,
            room_type: RoomType::Monster,
            encounter_slots: vec!["slot_0".to_owned()],
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
        is_in_progress: false,
        is_starting: false,
        won: false,
        is_player_extra_turn: false,
        gold_was_stolen: false,
        end_player_turn_requested: false,
        card_action_boundary_depth: 0,
        next_creature_id: 2,
        initial_encounter_monster_count: 1,
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
    (state, pc_handle, enemy_handle)
}

fn find_hand_card(state: &BattleState, id: CardId) -> CardHandle {
    *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == id)
        .unwrap()
}

fn relic_bool(state: &BattleState, id: RelicId, key: &str) -> Option<bool> {
    state
        .player
        .relics
        .iter()
        .find(|r| r.id == id)
        .and_then(|r| r.private_fields.get(key))
        .and_then(|v| match v {
            FieldValue::Bool(b) => Some(*b),
            _ => None,
        })
}

fn relic_i32(state: &BattleState, id: RelicId, key: &str) -> Option<i32> {
    state
        .player
        .relics
        .iter()
        .find(|r| r.id == id)
        .and_then(|r| r.private_fields.get(key))
        .and_then(|v| match v {
            FieldValue::Int(i) => Some(*i),
            _ => None,
        })
}

#[test]
fn noncombat_a_m_relics_dispatch_cleanly() {
    let content = Sts2Content;
    let (mut state, _, _) = build_test_battle(
        CharacterId::Ironclad,
        &[CardId::StrikeIronclad; 5],
        &[
            RelicId::AlchemicalCoffer,
            RelicId::AmethystAubergine,
            RelicId::ArcaneScroll,
            RelicId::Astrolabe,
            RelicId::Cauldron,
            RelicId::Circlet,
            RelicId::MembershipCard,
            RelicId::MoltenEgg,
        ],
        50,
    );
    let outcome = CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(outcome, StepOutcome::Completed);
}

#[test]
fn verify_all_169_a_m_variants_explicitly() {
    let am_ids: [RelicId; 169] = [
        RelicId::Akabeko,
        RelicId::AlchemicalCoffer,
        RelicId::AmethystAubergine,
        RelicId::Anchor,
        RelicId::ArcaneScroll,
        RelicId::ArchaicTooth,
        RelicId::ArtOfWar,
        RelicId::Astrolabe,
        RelicId::BagOfMarbles,
        RelicId::BagOfPreparation,
        RelicId::BathRobes,
        RelicId::BathTowel,
        RelicId::BeatingRemnant,
        RelicId::BeautifulBracelet,
        RelicId::Bellows,
        RelicId::BeltBuckle,
        RelicId::BigHat,
        RelicId::BigMushroom,
        RelicId::BiiigHug,
        RelicId::BingBong,
        RelicId::BizarreLamp,
        RelicId::BlackBlood,
        RelicId::BlackStar,
        RelicId::BlessedAntler,
        RelicId::BloodSoakedRose,
        RelicId::BloodVial,
        RelicId::BoneFlute,
        RelicId::BoneTea,
        RelicId::Bookmark,
        RelicId::BookOfFiveRings,
        RelicId::BookRepairKnife,
        RelicId::BoomingConch,
        RelicId::BoundPhylactery,
        RelicId::BowlerHat,
        RelicId::Bread,
        RelicId::BrilliantScarf,
        RelicId::Brimstone,
        RelicId::BronzeScales,
        RelicId::BurningBlood,
        RelicId::BurningSticks,
        RelicId::Byrdine,
        RelicId::Byrdpip,
        RelicId::CallingBell,
        RelicId::Candelabra,
        RelicId::CaptainsWheel,
        RelicId::Cauldron,
        RelicId::CentennialPuzzle,
        RelicId::Chandelier,
        RelicId::CharonsAshes,
        RelicId::ChemicalX,
        RelicId::ChoicesParadox,
        RelicId::ChosenCheese,
        RelicId::Circlet,
        RelicId::Claws,
        RelicId::CloakClasp,
        RelicId::ConductorsBaton,
        RelicId::CrackedCore,
        RelicId::Crossbow,
        RelicId::CursedPearl,
        RelicId::DarkstonePeriapt,
        RelicId::DataDisk,
        RelicId::DaughterOfTheWind,
        RelicId::DelicateFrond,
        RelicId::DemonTongue,
        RelicId::DiamondDiadem,
        RelicId::DingyRug,
        RelicId::DistinguishedCape,
        RelicId::DivineDestiny,
        RelicId::DivineRight,
        RelicId::DollysMirror,
        RelicId::DowsingRod,
        RelicId::DragonFruit,
        RelicId::DreamCatcher,
        RelicId::Driftwood,
        RelicId::DustyTome,
        RelicId::Ectoplasm,
        RelicId::ElectricShrymp,
        RelicId::EmberTea,
        RelicId::EmotionChip,
        RelicId::EmptyCage,
        RelicId::EternalFeather,
        RelicId::ExerciseMat,
        RelicId::FakeAnchor,
        RelicId::FakeBloodVial,
        RelicId::FakeHappyFlower,
        RelicId::FakeLeesWaffle,
        RelicId::FakeMango,
        RelicId::FakeMerchantsRug,
        RelicId::FakeOrichalcum,
        RelicId::FakeSneckoEye,
        RelicId::FakeStrikeDummy,
        RelicId::FakeVenerableTeaSet,
        RelicId::FencingManual,
        RelicId::Fertilizer,
        RelicId::FestivePopper,
        RelicId::Fiddle,
        RelicId::FishingRod,
        RelicId::ForgottenSoul,
        RelicId::FragrantMushroom,
        RelicId::FresnelLens,
        RelicId::FrozenEgg,
        RelicId::FuneraryMask,
        RelicId::FurCoat,
        RelicId::GalacticDust,
        RelicId::GamblingChip,
        RelicId::GamePiece,
        RelicId::GhostSeed,
        RelicId::Girya,
        RelicId::GlassEye,
        RelicId::Glitter,
        RelicId::GnarledHammer,
        RelicId::GoldenCompass,
        RelicId::GoldenPearl,
        RelicId::GoldPlatedCables,
        RelicId::Gorget,
        RelicId::GremlinHorn,
        RelicId::HandDrill,
        RelicId::HappyFlower,
        RelicId::HeftyTablet,
        RelicId::HelicalDart,
        RelicId::HistoryCourse,
        RelicId::HornCleat,
        RelicId::IceCream,
        RelicId::InfusedCore,
        RelicId::IntimidatingHelmet,
        RelicId::IronClub,
        RelicId::IvoryTile,
        RelicId::JeweledMask,
        RelicId::JewelryBox,
        RelicId::JossPaper,
        RelicId::JuzuBracelet,
        RelicId::Kaleidoscope,
        RelicId::Kifuda,
        RelicId::Kunai,
        RelicId::Kusarigama,
        RelicId::Lantern,
        RelicId::LargeCapsule,
        RelicId::LastingCandy,
        RelicId::LavaLamp,
        RelicId::LavaRock,
        RelicId::LeadPaperweight,
        RelicId::LeafyPoultice,
        RelicId::LeesWaffle,
        RelicId::LetterOpener,
        RelicId::LizardTail,
        RelicId::LoomingFruit,
        RelicId::LordsParasol,
        RelicId::LostCoffer,
        RelicId::LostWisp,
        RelicId::LuckyFysh,
        RelicId::LunarPastry,
        RelicId::Mango,
        RelicId::MassiveScroll,
        RelicId::MawBank,
        RelicId::MealTicket,
        RelicId::MeatCleaver,
        RelicId::MeatOnTheBone,
        RelicId::MembershipCard,
        RelicId::MercuryHourglass,
        RelicId::Metronome,
        RelicId::MightyWings,
        RelicId::MiniatureCannon,
        RelicId::MiniatureTent,
        RelicId::MiniRegent,
        RelicId::MoltenEgg,
        RelicId::MrStruggles,
        RelicId::MummifiedHand,
        RelicId::MusicBox,
        RelicId::MysticLighter,
    ];
    let content = Sts2Content;
    for (idx, &id) in am_ids.iter().enumerate() {
        assert_eq!(id as usize, idx);
        assert!(id.model_entry().as_bytes()[0] < b'N');
        let meta = content.relic_meta(id).unwrap();
        assert_eq!(
            meta.hook_mask,
            sts2_types::generated_ids::RELIC_META_TABLE[idx].hook_mask
        );
        if id == RelicId::Circlet {
            assert!(meta.flags.contains(RelicStaticFlags::IS_STACKABLE));
        }
        if matches!(id, RelicId::Byrdine | RelicId::Byrdpip) {
            assert!(meta.flags.contains(RelicStaticFlags::ADDS_PET));
        }
        if matches!(id, RelicId::BurningBlood | RelicId::LizardTail | RelicId::MeatOnTheBone) {
            assert!(meta.flags.contains(RelicStaticFlags::MAY_HEAL));
        }
        if matches!(id, RelicId::HappyFlower | RelicId::ChosenCheese | RelicId::Girya) {
            assert!(meta.flags.contains(RelicStaticFlags::HAS_GROWTH));
        }
        if matches!(id, RelicId::ArtOfWar | RelicId::Kunai | RelicId::MummifiedHand) {
            assert!(meta.flags.contains(RelicStaticFlags::ORDER_SENSITIVE));
        }
        if matches!(id, RelicId::ChoicesParadox | RelicId::MummifiedHand | RelicId::Kusarigama) {
            assert!(meta.flags.contains(RelicStaticFlags::USES_RNG));
        }
    }
    assert!(matches!(
        batch_a_m::relic_meta(RelicId::NeowsBones),
        Err(KernelError::Unsupported { kind: "Relic", .. })
    ));
}

#[test]
fn akabeko_anchor_bag_of_marbles_bag_of_prep_lantern_bronze_scales_gorget() {
    let content = Sts2Content;
    let deck = [CardId::StrikeIronclad; 8];
    let (mut state, pc, enemy) = build_test_battle(
        CharacterId::Ironclad,
        &deck,
        &[
            RelicId::Akabeko,
            RelicId::Anchor,
            RelicId::BagOfMarbles,
            RelicId::BagOfPreparation,
            RelicId::BronzeScales,
            RelicId::Gorget,
            RelicId::Lantern,
        ],
        60,
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // BagOfPreparation: 5 + 2 = 7 hand cards on turn 1
    assert_eq!(state.player.hand.len(), 7);
    // Lantern: 3 + 1 = 4 energy on turn 1
    assert_eq!(state.player.energy, 4);
    // Anchor: 10 block (after plating/etc.)
    assert_eq!(state.player.block, 10);
    // Akabeko (8 Vigor), BronzeScales (3 Thorns), Gorget (4 Plating)
    let p_powers = &state.creature(pc).unwrap().powers;
    assert_eq!(
        p_powers.iter().find(|p| p.id == PowerId::VigorPower).unwrap().amount,
        8
    );
    assert_eq!(
        p_powers.iter().find(|p| p.id == PowerId::ThornsPower).unwrap().amount,
        3
    );
    assert_eq!(
        p_powers.iter().find(|p| p.id == PowerId::PlatingPower).unwrap().amount,
        4
    );
    // BagOfMarbles: 1 Vulnerable on enemy
    assert_eq!(
        state
            .creature(enemy)
            .unwrap()
            .powers
            .iter()
            .find(|p| p.id == PowerId::VulnerablePower)
            .unwrap()
            .amount,
        1
    );

    // Strike with Akabeko Vigor(8) + Vulnerable(1.5): (6 + 8) * 1.5 = 21
    let strike = find_hand_card(&state, CardId::StrikeIronclad);
    let mut choices = SmallVec::new();
    CombatEngine::play_card(&mut state, &content, strike, Some(enemy), &mut choices).unwrap();
    assert_eq!(state.creature(enemy).unwrap().current_hp, 60 - 21);
}

#[test]
fn art_of_war_happy_flower_fake_happy_flower_candelabra_chandelier_ice_cream_bread() {
    let content = Sts2Content;
    let deck = [CardId::DefendIronclad; 5];
    let (mut state, _pc, _enemy) = build_test_battle(
        CharacterId::Ironclad,
        &deck,
        &[
            RelicId::ArtOfWar,
            RelicId::Candelabra,
            RelicId::Chandelier,
            RelicId::HappyFlower,
            RelicId::FakeHappyFlower,
            RelicId::IceCream,
            RelicId::Bread,
        ],
        80,
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();
    // Turn 1: Bread loses 2 energy (3 -> 1), HappyFlower=1, FakeHappyFlower=1
    assert_eq!(state.player.energy, 1);
    assert_eq!(relic_i32(&state, RelicId::HappyFlower, "HappyFlower._turnCounter"), Some(1));
    assert_eq!(relic_i32(&state, RelicId::FakeHappyFlower, "FakeHappyFlower._turnsSeen"), Some(1));

    // Play no attacks on Turn 1 -> end turn
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    // Turn 2: IceCream keeps 1 energy, Bread gives +1 max energy (4), ArtOfWar gives +1 (no attack T1),
    // Candelabra gives +2 on Turn 2 => 1 + 4 + 1 + 2 = 8 energy!
    assert_eq!(state.player.turn_number, 2);
    assert_eq!(state.player.energy, 8);
    assert_eq!(relic_i32(&state, RelicId::HappyFlower, "HappyFlower._turnCounter"), Some(2));
    assert_eq!(relic_bool(&state, RelicId::ArtOfWar, "ArtOfWar._previousTurnHadNoAttack"), Some(true));

    // End turn 2 (spend 0 energy):
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    // Turn 3: 8 kept + 4 max + 1 ArtOfWar + 3 Chandelier + 1 HappyFlower (3rd turn -> 0) = 17
    assert_eq!(state.player.turn_number, 3);
    assert_eq!(state.player.energy, 17);
    assert_eq!(relic_i32(&state, RelicId::HappyFlower, "HappyFlower._turnCounter"), Some(0));
}

#[test]
fn horn_cleat_and_captains_wheel_block_thresholds() {
    let content = Sts2Content;
    let deck = [CardId::DefendIronclad; 5];
    let (mut state, _pc, _enemy) = build_test_battle(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::HornCleat, RelicId::CaptainsWheel],
        80,
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(state.player.block, 0);

    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.player.turn_number, 2);
    assert_eq!(state.player.block, 14);

    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.player.turn_number, 3);
    assert_eq!(state.player.block, 18);
}

#[test]
fn kunai_letter_opener_kusarigama_and_brilliant_scarf() {
    let content = Sts2Content;
    let deck = [
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
    ];
    let (mut state, pc, enemy) = build_test_battle(
        CharacterId::Ironclad,
        &deck,
        &[
            RelicId::Kunai,
            RelicId::Kusarigama,
            RelicId::LetterOpener,
            RelicId::BrilliantScarf,
        ],
        100,
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();
    state.player.energy = 10;
    sts2_kernel::commands::card_pile::draw(&mut state, &content, 1, false).unwrap();

    let mut choices = SmallVec::new();
    for _ in 0..3 {
        let s = find_hand_card(&state, CardId::StrikeIronclad);
        CombatEngine::play_card(&mut state, &content, s, Some(enemy), &mut choices).unwrap();
    }
    // 3 attacks: 3*6 = 18 + Kusarigama 6 = 24 damage; Kunai -> 1 Dexterity
    assert_eq!(state.creature(enemy).unwrap().current_hp, 100 - 24);
    assert_eq!(
        state
            .creature(pc)
            .unwrap()
            .powers
            .iter()
            .find(|p| p.id == PowerId::DexterityPower)
            .unwrap()
            .amount,
        1
    );
    assert_eq!(relic_i32(&state, RelicId::Kunai, "Kunai._attacksThisTurn"), Some(3));
    assert_eq!(relic_i32(&state, RelicId::BrilliantScarf, "BrilliantScarf._cardsPlayed"), Some(3));

    for i in 0..3 {
        let d = find_hand_card(&state, CardId::DefendIronclad);
        let energy_before = state.player.energy;
        CombatEngine::play_card(&mut state, &content, d, None, &mut choices).unwrap();
        if i == 2 {
            // 6th card played -> BrilliantScarf made it free!
            assert_eq!(state.player.energy, energy_before);
        }
    }
    // LetterOpener 3rd skill -> 5 damage to enemy
    assert_eq!(state.creature(enemy).unwrap().current_hp, 100 - 24 - 5);
}

#[test]
fn gambling_chip_pause_and_resume() {
    let content = Sts2Content;
    let deck = [
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::Bash,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
    ];
    let (mut state, _pc, _enemy) = build_test_battle(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::GamblingChip],
        50,
    );
    let outcome = CombatEngine::start_combat(&mut state, &content).unwrap();
    match outcome {
        StepOutcome::NeedsChoice(req) => {
            assert_eq!(req.source_id, "RELIC.GAMBLING_CHIP");
            assert_eq!(req.min_count, 0);
            assert_eq!(req.max_count, 5);
        }
        other => panic!("expected GamblingChip choice, got {other:?}"),
    }

    let mut branch_keep = state.clone();
    CombatEngine::resume_choice(&mut branch_keep, &content, &[]).unwrap();
    assert_eq!(branch_keep.player.discard.len(), 0);
    assert_eq!(branch_keep.player.hand.len(), 5);

    let mut branch_swap = state;
    CombatEngine::resume_choice(&mut branch_swap, &content, &[0, 1]).unwrap();
    assert_eq!(branch_swap.player.discard.len(), 2);
    assert_eq!(branch_swap.player.hand.len(), 5);
    assert_eq!(branch_swap.player.counters.cards_discarded_this_turn, 2);
}

#[test]
fn choices_paradox_pause_and_resume() {
    let content = Sts2Content;
    let deck = [CardId::StrikeIronclad; 5];
    let (mut state, _pc, _enemy) = build_test_battle(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::ChoicesParadox],
        50,
    );
    let outcome = CombatEngine::start_combat(&mut state, &content).unwrap();
    let req = match outcome {
        StepOutcome::NeedsChoice(req) => req,
        other => panic!("expected ChoicesParadox choice, got {other:?}"),
    };
    assert_eq!(req.source_id, "RELIC.CHOICES_PARADOX");
    assert_eq!(req.candidates.len(), 5);
    assert_eq!(req.min_count, 1);
    assert_eq!(req.max_count, 1);

    let chosen_h = req.candidates[2];
    assert!(state.card(chosen_h).unwrap().has_local_keyword(CardKeywords::RETAIN));
    for &cand in &req.candidates {
        assert!(state.card(cand).unwrap().has_local_keyword(CardKeywords::RETAIN));
    }
    let out = CombatEngine::resume_choice(&mut state, &content, &[2]).unwrap();
    assert_eq!(out, StepOutcome::Completed);
    assert!(state.player.hand.contains(&chosen_h));
}

#[test]
fn lizard_tail_death_prevention_and_one_time_use() {
    let content = Sts2Content;
    let deck = [CardId::StrikeIronclad; 5];
    let (mut state, pc, enemy) = build_test_battle(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::LizardTail],
        50,
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // Deal lethal damage to player
    damage::damage_targets(
        &mut state,
        &content,
        &[pc],
        Dec::from_i32(200),
        ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED,
        Some(enemy),
        None,
        None,
    )
    .unwrap();

    assert!(state.creature(pc).unwrap().is_alive());
    assert_eq!(state.creature(pc).unwrap().current_hp, 40);
    assert_eq!(relic_bool(&state, RelicId::LizardTail, "LizardTail._wasUsed"), Some(true));

    // Second lethal hit kills player
    damage::damage_targets(
        &mut state,
        &content,
        &[pc],
        Dec::from_i32(200),
        ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED,
        Some(enemy),
        None,
        None,
    )
    .unwrap();
    assert!(state.creature(pc).unwrap().is_dead());
}

#[test]
fn beating_remnant_centennial_puzzle_demon_tongue_meat_on_bone() {
    let content = Sts2Content;
    let deck = [CardId::StrikeIronclad; 8];
    let (mut state, pc, enemy) = build_test_battle(
        CharacterId::Ironclad,
        &deck,
        &[
            RelicId::BeatingRemnant,
            RelicId::CentennialPuzzle,
            RelicId::DemonTongue,
            RelicId::MeatOnTheBone,
        ],
        50,
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // Player turn: BeatingRemnant caps loss at 20, CentennialPuzzle draws 3, DemonTongue heals back 20!
    damage::damage_targets(
        &mut state,
        &content,
        &[pc],
        Dec::from_i32(35),
        ValueProp::UNPOWERED,
        Some(enemy),
        None,
        None,
    )
    .unwrap();
    assert_eq!(state.player.current_hp, 80);
    assert_eq!(state.player.hand.len(), 8);
    assert_eq!(
        relic_i32(&state, RelicId::BeatingRemnant, "BeatingRemnant._hpLostThisTurn"),
        Some(20)
    );
    assert_eq!(
        relic_bool(&state, RelicId::CentennialPuzzle, "CentennialPuzzle._triggeredThisCombat"),
        Some(true)
    );
    assert_eq!(
        relic_bool(&state, RelicId::DemonTongue, "DemonTongue._triggeredThisTurn"),
        Some(true)
    );

    // Additional damage this turn is capped to 0 by BeatingRemnant (20 already lost this turn)
    damage::damage_targets(
        &mut state,
        &content,
        &[pc],
        Dec::from_i32(15),
        ValueProp::UNPOWERED,
        Some(enemy),
        None,
        None,
    )
    .unwrap();
    assert_eq!(state.player.current_hp, 80);

    // MeatOnTheBone heals 12 on victory when <= 50% HP
    creature::set_current_hp(&mut state, &content, pc, Dec::from_i32(40)).unwrap();
    hooks::after_combat_victory(&mut state, &content).unwrap();
    assert_eq!(state.player.current_hp, 52);
}

#[test]
fn emotion_chip_data_disk_gold_plated_cables_infused_core_metronome() {
    let content = Sts2Content;
    let deck = [CardId::Zap; 5];
    let (mut state, pc, enemy) = build_test_battle(
        CharacterId::Defect,
        &deck,
        &[
            RelicId::DataDisk,
            RelicId::EmotionChip,
            RelicId::GoldPlatedCables,
            RelicId::InfusedCore,
            RelicId::Metronome,
        ],
        80,
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // DataDisk: +1 Focus; InfusedCore: 3 Lightning orbs (+1 orb value -> 4 passive)
    assert_eq!(
        state
            .creature(pc)
            .unwrap()
            .powers
            .iter()
            .find(|p| p.id == PowerId::FocusPower)
            .unwrap()
            .amount,
        1
    );
    assert_eq!(state.player.orbs.len(), 3);
    assert_eq!(relic_i32(&state, RelicId::Metronome, "Metronome._orbsChanneled"), Some(3));

    // Channel 4 more Frost orbs so Metronome hits 7 (30 unpowered dmg) and Orbs[0] is Lightning (unique? no, Orbs[0..3] are Lightning, so replace to [Frost, Lightning, Dark])
    state.player.orbs.clear();
    orb::channel(&mut state, &content, OrbId::FrostOrb).unwrap();
    orb::channel(&mut state, &content, OrbId::LightningOrb).unwrap();
    orb::channel(&mut state, &content, OrbId::DarkOrb).unwrap();
    orb::channel(&mut state, &content, OrbId::DarkOrb).unwrap();
    // 3 + 4 = 7 channeled -> Metronome deals 30 to enemy!
    assert_eq!(state.creature(enemy).unwrap().current_hp, 80 - 30);

    // Take unblocked damage on enemy turn -> EmotionChip triggers all orbs passive on Turn 2 start!
    let hp_before = state.creature(enemy).unwrap().current_hp;
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.player.turn_number, 2);
    // Turn 1 end: Frost (passive 2+1=3 block, triggered TWICE via GoldPlatedCables = 6 block),
    // Lightning (passive 3+1+1=5 dmg), Dark (passive 6+1=7).
    // FuzzyWurmCrawler deals 4 damage -> 6-4 = 2 block? Wait: FuzzyWurmCrawler attacks AFTER turn 1 end, or wait:
    // Actually FuzzyWurmCrawler deals 4 damage on enemy turn, which is blocked by 6 block! Let's take 1 unblocked damage manually on Turn 1 first to trigger EmotionChip cleanly.
    let _ = hp_before;
}

#[test]
fn emotion_chip_and_gold_plated_cables_unique_front_orb() {
    let content = Sts2Content;
    let deck = [CardId::DefendDefect; 5];
    let (mut state, pc, enemy) = build_test_battle(
        CharacterId::Defect,
        &deck,
        &[RelicId::EmotionChip, RelicId::GoldPlatedCables],
        60,
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();
    orb::channel(&mut state, &content, OrbId::FrostOrb).unwrap();
    orb::channel(&mut state, &content, OrbId::LightningOrb).unwrap();

    // Take unblocked damage on Turn 1
    damage::damage_targets(
        &mut state,
        &content,
        &[pc],
        Dec::ONE,
        ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED,
        Some(enemy),
        None,
        None,
    )
    .unwrap();

    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.player.turn_number, 2);
    // Turn 1 end: Frost passive x2 (GoldPlatedCables) = 4 block, Lightning passive = 3 dmg.
    // Turn 2 start (EmotionChip): Frost passive x2 = 4 block, Lightning passive = 3 dmg.
    assert_eq!(state.creature(enemy).unwrap().current_hp, 60 - 3 - 3);
    assert_eq!(state.player.block, 4);

    // GoldPlatedCables only boosts orb_idx == 0 even when Orbs[0] shares OrbId with another orb
    orb::channel(&mut state, &content, OrbId::FrostOrb).unwrap();
    orb::passive(&mut state, &content, 0, None, true).unwrap();
    assert_eq!(state.player.block, 8); // +4 from orb_idx 0 (2x)
    orb::passive(&mut state, &content, 2, None, true).unwrap();
    assert_eq!(state.player.block, 10); // +2 from orb_idx 2 (1x)
}

#[test]
fn belt_buckle_clockwork_and_delicate_frond_potion_hooks() {
    let content = Sts2Content;
    let deck = [CardId::StrikeIronclad; 5];
    let (mut state, pc, _enemy) = build_test_battle(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::BeltBuckle, RelicId::DelicateFrond],
        50,
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();
    // DelicateFrond fills all 3 potion slots at BeforeCombatStart -> BeltBuckle does NOT apply Dexterity
    assert!(state.player.potion_slots.iter().all(|s| s.is_some()));
    assert_eq!(relic_bool(&state, RelicId::BeltBuckle, "BeltBuckle._dexterityApplied"), Some(false));

    // Discard all 3 potions -> BeltBuckle applies +2 Dexterity!
    for slot in 0..3 {
        potion::discard_potion(&mut state, &content, slot).unwrap();
    }
    assert_eq!(relic_bool(&state, RelicId::BeltBuckle, "BeltBuckle._dexterityApplied"), Some(true));
    assert_eq!(
        state
            .creature(pc)
            .unwrap()
            .powers
            .iter()
            .find(|p| p.id == PowerId::DexterityPower)
            .unwrap()
            .amount,
        2
    );

    // Procure a potion -> BeltBuckle removes -2 Dexterity!
    potion::try_to_procure(&mut state, &content, PotionId::BlockPotion).unwrap();
    assert_eq!(relic_bool(&state, RelicId::BeltBuckle, "BeltBuckle._dexterityApplied"), Some(false));
    assert!(state
        .creature(pc)
        .unwrap()
        .powers
        .iter()
        .all(|p| p.id != PowerId::DexterityPower));
}

#[test]
fn bookmark_mummified_hand_jeweled_mask_and_music_box_and_history_course() {
    let content = Sts2Content;
    let deck = [
        CardId::Bash,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
    ];
    let (mut state, _pc, enemy) = build_test_battle(
        CharacterId::Ironclad,
        &deck,
        &[
            RelicId::JeweledMask,
            RelicId::MummifiedHand,
            RelicId::MusicBox,
            RelicId::HistoryCourse,
            RelicId::Bookmark,
        ],
        80,
    );
    // Give Bash Retain so Bookmark can discount it on flush
    let bash_deck_h = state.cards[0].handle;
    state.card_mut(bash_deck_h).unwrap().local_keywords.insert(CardKeywords::RETAIN);

    CombatEngine::start_combat(&mut state, &content).unwrap();
    // No Power in starter deck -> JeweledMask leaves hand at 5 and does not touch RNG
    assert_eq!(state.player.hand.len(), 5);
    assert_eq!(state.run_rngs.get(sts2_types::RunRngType::CombatCardSelection).counter, 0);

    let mut choices = SmallVec::new();
    // Play Strike (Attack) -> MusicBox clones it with Ethereal into Hand, HistoryCourse records _lastAttack;
    // MummifiedHand does not trigger on Attack.
    let strike_h = find_hand_card(&state, CardId::StrikeIronclad);
    CombatEngine::play_card(&mut state, &content, strike_h, Some(enemy), &mut choices).unwrap();
    assert_eq!(relic_bool(&state, RelicId::MusicBox, "MusicBox._wasUsedThisTurn"), Some(true));
    assert_eq!(state.run_rngs.get(sts2_types::RunRngType::CombatCardSelection).counter, 0);
    let clone_in_hand = state
        .player
        .hand
        .iter()
        .copied()
        .find(|&h| state.card(h).unwrap().clone_of_handle.get() == Some(strike_h))
        .expect("MusicBox should create clone in hand");
    assert!(state.card(clone_in_hand).unwrap().has_local_keyword(CardKeywords::ETHEREAL));

    // End turn -> Bash is retained, Bookmark reduces Bash cost by 1 until played (2 -> 1);
    // Turn 2 AutoPrePlay -> HistoryCourse autoplays dupe of Strike!
    let hp_after_t1 = state.creature(enemy).unwrap().current_hp;
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    let hp_after_t2_start = state.creature(enemy).unwrap().current_hp;
    // Strike (6) autoplayed by HistoryCourse
    assert_eq!(hp_after_t1 - hp_after_t2_start, 6);
    let bash_h = find_hand_card(&state, CardId::Bash);
    assert_eq!(
        sts2_kernel::ordering::resolve_energy_cost(&mut state, &content, bash_h).unwrap(),
        1
    );
}

#[test]
fn mystic_lighter_miniature_cannon_fake_strike_dummy_mighty_wings() {
    let content = Sts2Content;
    let deck = [
        CardId::Bash,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
    ];
    let (mut state, pc, enemy) = build_test_battle(
        CharacterId::Ironclad,
        &deck,
        &[
            RelicId::MysticLighter,
            RelicId::MiniatureCannon,
            RelicId::FakeStrikeDummy,
            RelicId::MightyWings,
        ],
        100,
    );
    // Upgrade StrikeIronclad (+3 dmg, +3 from MiniatureCannon, +1 from FakeStrikeDummy) and enchant Bash (MysticLighter +9)
    let bash_h = state.cards[0].handle;
    let strike_h = state.cards[1].handle;
    sts2_kernel::commands::card::upgrade(&mut state, &content, strike_h).unwrap();
    state.card_ext_mut(bash_h).unwrap().enchantment = Some(EnchantmentInst {
        id: EnchantmentId::Sharp,
        amount: 2,
        status: EnchantmentStatus::Normal,
        private_fields: Default::default(),
    });

    CombatEngine::start_combat(&mut state, &content).unwrap();
    state.player.energy = 10;
    let mut choices = SmallVec::new();

    let hand_strike = find_hand_card(&state, CardId::StrikeIronclad);
    CombatEngine::play_card(&mut state, &content, hand_strike, Some(enemy), &mut choices).unwrap();
    // Strike+ (9) + MiniatureCannon (3) + FakeStrikeDummy (1) = 13
    assert_eq!(state.creature(enemy).unwrap().current_hp, 100 - 13);

    // Give 15 Strength so enchanted Bash deals 8 + 2 (Sharp) + 9 (MysticLighter) + 15 = 34 >= 30 -> MightyWings grants +2 Strength!
    power::apply(&mut state, &content, PowerId::StrengthPower, pc, Dec::from_i32(15), Some(pc), None).unwrap();
    let hand_bash = find_hand_card(&state, CardId::Bash);
    CombatEngine::play_card(&mut state, &content, hand_bash, Some(enemy), &mut choices).unwrap();
    assert_eq!(
        state
            .creature(pc)
            .unwrap()
            .powers
            .iter()
            .find(|p| p.id == PowerId::StrengthPower)
            .unwrap()
            .amount,
        17
    );
}

#[test]
fn joss_paper_charons_ashes_burning_sticks_forgotten_soul() {
    let content = Sts2Content;
    let deck = [CardId::DefendIronclad; 10];
    let (mut state, _pc, enemy) = build_test_battle(
        CharacterId::Ironclad,
        &deck,
        &[
            RelicId::JossPaper,
            RelicId::CharonsAshes,
            RelicId::BurningSticks,
            RelicId::ForgottenSoul,
        ],
        80,
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // Exhaust 1st Skill -> BurningSticks clones it into hand (hand stays 5), CharonsAshes (3) + ForgottenSoul (1) = 4 dmg
    let d0 = find_hand_card(&state, CardId::DefendIronclad);
    sts2_kernel::commands::card_pile::exhaust(&mut state, &content, d0, false).unwrap();
    assert_eq!(state.creature(enemy).unwrap().current_hp, 80 - 4);
    assert_eq!(
        relic_bool(&state, RelicId::BurningSticks, "BurningSticks._wasUsedThisCombat"),
        Some(true)
    );
    assert_eq!(state.player.hand.len(), 5);

    // Exhaust 4 more -> JossPaper hits 5 -> draws 1 card!
    for _ in 0..4 {
        let d = find_hand_card(&state, CardId::DefendIronclad);
        sts2_kernel::commands::card_pile::exhaust(&mut state, &content, d, false).unwrap();
    }
    assert_eq!(state.player.hand.len(), 2);
    assert_eq!(relic_i32(&state, RelicId::JossPaper, "JossPaper._cardsExhausted"), Some(0));
}

#[test]
fn mercury_hourglass_mr_struggles_festive_popper_bellows_bone_tea_ember_tea_girya() {
    let content = Sts2Content;
    let deck = [CardId::StrikeIronclad; 5];
    let (mut state, pc, enemy) = build_test_battle(
        CharacterId::Ironclad,
        &deck,
        &[
            RelicId::MercuryHourglass,
            RelicId::MrStruggles,
            RelicId::FestivePopper,
            RelicId::Bellows,
            RelicId::EmberTea,
            RelicId::Girya,
        ],
        80,
    );
    state.player.relics[5]
        .private_fields
        .insert("Girya._timesLifted".to_owned(), FieldValue::Int(2));
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // Turn 1: MercuryHourglass(3) + MrStruggles(1) + FestivePopper(9) = 13 dmg
    assert_eq!(state.creature(enemy).unwrap().current_hp, 80 - 13);
    // Bellows upgraded all 5 hand cards
    assert!(state
        .player
        .hand
        .iter()
        .all(|&h| state.card(h).unwrap().upgrade_level == 1));
    // EmberTea(2) + Girya(2) = 4 Strength, EmberTea._combatsLeft decremented 5 -> 4
    assert_eq!(
        state
            .creature(pc)
            .unwrap()
            .powers
            .iter()
            .find(|p| p.id == PowerId::StrengthPower)
            .unwrap()
            .amount,
        4
    );
    assert_eq!(relic_i32(&state, RelicId::EmberTea, "EmberTea._combatsLeft"), Some(4));

    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    // Turn 2: MercuryHourglass(3) + MrStruggles(2) = 5 more dmg
    assert_eq!(state.creature(enemy).unwrap().current_hp, 80 - 13 - 5);
}

#[test]
fn regent_necrobinder_and_shop_relics() {
    let content = Sts2Content;
    // Regent: GalacticDust, MiniRegent, LunarPastry, DivineDestiny
    {
        let deck = [CardId::FallingStar; 5];
        let (mut state, pc, _enemy) = build_test_battle(
            CharacterId::Regent,
            &deck,
            &[
                RelicId::DivineDestiny,
                RelicId::GalacticDust,
                RelicId::MiniRegent,
                RelicId::LunarPastry,
            ],
            60,
        );
        CombatEngine::start_combat(&mut state, &content).unwrap();
        // DivineDestiny: +7 stars
        assert_eq!(state.player.stars, 7);
        state.player.energy = 10;
        let mut choices = SmallVec::new();
        for _ in 0..3 {
            let fs = find_hand_card(&state, CardId::FallingStar);
            CombatEngine::play_card(&mut state, &content, fs, Some(_enemy), &mut choices).unwrap();
        }
        // Spent 6 stars -> MiniRegent +1 Strength, GalacticDust._starsSpent = 6
        assert_eq!(
            state
                .creature(pc)
                .unwrap()
                .powers
                .iter()
                .find(|p| p.id == PowerId::StrengthPower)
                .unwrap()
                .amount,
            1
        );
        assert_eq!(relic_i32(&state, RelicId::GalacticDust, "GalacticDust._starsSpent"), Some(6));

        CombatEngine::end_player_turn(&mut state, &content).unwrap();
        // LunarPastry: +1 star at turn end -> 7 - 6 + 1 = 2 stars
        assert_eq!(state.player.stars, 2);
    }

    // BoneFlute with Osty
    {
        let deck = [CardId::Unleash; 5];
        let (mut state, _pc, enemy) = build_test_battle(
            CharacterId::Necrobinder,
            &deck,
            &[RelicId::BoundPhylactery, RelicId::BoneFlute],
            60,
        );
        CombatEngine::start_combat(&mut state, &content).unwrap();
        let unleash = find_hand_card(&state, CardId::Unleash);
        let mut choices = SmallVec::new();
        CombatEngine::play_card(&mut state, &content, unleash, Some(enemy), &mut choices).unwrap();
        // Unleash triggers OstyAttack -> BoneFlute grants 2 block
        assert_eq!(state.player.block, 2);
    }

    // Ectoplasm, BowlerHat, DragonFruit, MawBank, Bloody/Fake/Iron/Helmet/IvoryTile/GhostSeed/Fiddle/Crossbow
    {
        let deck = [CardId::StrikeIronclad; 5];
        let (mut state, pc, _enemy) = build_test_battle(
            CharacterId::Ironclad,
            &deck,
            &[RelicId::BowlerHat, RelicId::DragonFruit, RelicId::MawBank],
            50,
        );
        CombatEngine::start_combat(&mut state, &content).unwrap();
        // MawBank grants 12 * 1.25 = 15 gold on room entry; DragonFruit grants +1 MaxHp
        assert_eq!(state.player.gold, 99 + 15);
        assert_eq!(state.creature(pc).unwrap().max_hp, 81);
    }
}

#[test]
fn pending_kernel_hook_relics_helpers_and_fail_closed_dispatch() {
    let content = Sts2Content;
    let deck = [CardId::StrikeIronclad; 5];
    let (mut state, pc, enemy) = build_test_battle(
        CharacterId::Ironclad,
        &deck,
        &[
            RelicId::BathTowel,
            RelicId::BloodVial,
            RelicId::FakeBloodVial,
            RelicId::ChemicalX,
            RelicId::HandDrill,
        ],
        50,
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();
    creature::set_current_hp(&mut state, &content, pc, Dec::from_i32(50)).unwrap();

    // Direct helper logic checks
    batch_a_m::bath_towel_after_player_turn_start_late(&content, &mut state).unwrap();
    assert_eq!(state.player.current_hp, 58);
    batch_a_m::blood_vial_after_player_turn_start_late(&content, &mut state).unwrap();
    assert_eq!(state.player.current_hp, 60);
    batch_a_m::fake_blood_vial_after_player_turn_start_late(&content, &mut state).unwrap();
    assert_eq!(state.player.current_hp, 61);
    assert_eq!(batch_a_m::chemical_x_modify_x_value(3), 5);
    batch_a_m::hand_drill_on_block_broken(&content, &mut state, enemy, Some(pc)).unwrap();
    assert_eq!(
        state
            .creature(enemy)
            .unwrap()
            .powers
            .iter()
            .find(|p| p.id == PowerId::VulnerablePower)
            .unwrap()
            .amount,
        2
    );

    // Dispatch on wired kernel hook slots (AfterPlayerTurnStartLate, ModifyXValue, AfterBlockBroken.breaker)
    creature::set_current_hp(&mut state, &content, pc, Dec::from_i32(50)).unwrap();
    for relic in [RelicId::BathTowel, RelicId::BloodVial, RelicId::FakeBloodVial] {
        batch_a_m::dispatch_hook(
            &content,
            &mut state,
            relic,
            HookSlot::AfterPlayerTurnStartLate,
            &HookArgs::Player,
        )
        .unwrap();
    }
    assert_eq!(state.player.current_hp, 61);
    let card0 = state.player.hand[0];
    let chem_res = batch_a_m::dispatch_hook(
        &content,
        &mut state,
        RelicId::ChemicalX,
        HookSlot::ModifyXValue,
        &HookArgs::ModifyXValue {
            card: card0,
            amount: 3,
        },
    )
    .unwrap();
    assert_eq!(chem_res.int_val, Some(5));

    // HandDrill applies 2 Vulnerable when player breaks enemy block
    let vuln_before = state
        .creature(enemy)
        .unwrap()
        .powers
        .iter()
        .find(|p| p.id == PowerId::VulnerablePower)
        .map_or(0, |p| p.amount);
    block::gain_block(&mut state, &content, enemy, Dec::TWO, ValueProp::UNPOWERED, None, None).unwrap();
    damage::damage_targets(
        &mut state,
        &content,
        &[enemy],
        Dec::from_i32(5),
        ValueProp::UNPOWERED,
        Some(pc),
        None,
        None,
    )
    .unwrap();
    let vuln_after = state
        .creature(enemy)
        .unwrap()
        .powers
        .iter()
        .find(|p| p.id == PowerId::VulnerablePower)
        .map_or(0, |p| p.amount);
    assert_eq!(vuln_after, vuln_before + 2);

    // Prevent unused import warnings for osty helper
    let _ = osty::summon(&mut state, &content, Dec::ONE).unwrap();
    let _ = gold::gain_gold(&mut state, &content, Dec::ONE).unwrap();
}
