use smallvec::{SmallVec, smallvec};
use std::borrow::Cow;
use std::sync::Arc;
use sts2_content::Sts2Content;
use sts2_content::monsters::act2;
use sts2_content::powers::batch_n_z;
use sts2_kernel::commands::{arrow, block, damage};
use sts2_kernel::{
    ArrowSlotInst, BattleState, CardHandle, CardInst, CardTempFlags, ChoiceTiming, CombatEngine,
    ContentDispatcher, CreatureHandle, CreatureInst, EnchantmentInst, FieldValue, HookArgs,
    MonsterInst, PlayerCounters, PlayerInst, RootStaticContext, SemanticHistory, StepOutcome,
};
use sts2_types::{
    ArrowStatus, CardId, CardKeywords, CharacterId, CombatSide, Dec,
    EnchantmentId, EnchantmentStatus, GameMode, HookMask, HookSlot, MonsterId, PileType,
    PlayerRngSet, PlayerTurnPhase, PowerId, Rng, RoomType, RunRngSet, ValueProp,
};

const ALL_24_ACT2: [MonsterId; 24] = [
    MonsterId::BowlbugEgg,
    MonsterId::BowlbugNectar,
    MonsterId::BowlbugRock,
    MonsterId::BowlbugSilk,
    MonsterId::Chomper,
    MonsterId::Crusher,
    MonsterId::DecimillipedeSegmentBack,
    MonsterId::DecimillipedeSegmentFront,
    MonsterId::DecimillipedeSegmentMiddle,
    MonsterId::Entomancer,
    MonsterId::Exoskeleton,
    MonsterId::HunterKiller,
    MonsterId::InfestedPrism,
    MonsterId::KnowledgeDemon,
    MonsterId::LouseProgenitor,
    MonsterId::Myte,
    MonsterId::Ovicopter,
    MonsterId::Rocket,
    MonsterId::SlumberingBeetle,
    MonsterId::SpinyToad,
    MonsterId::TheInsatiable,
    MonsterId::TheObscura,
    MonsterId::ThievingHopper,
    MonsterId::Tunneler,
];

fn build_battle_with(
    character: CharacterId,
    deck_ids: &[CardId],
    enemies: &[(MonsterId, i32, Option<&str>)],
    ascension: u8,
    game_mode: GameMode,
    seed: &str,
) -> (BattleState, Vec<CreatureHandle>) {
    let run_rngs = RunRngSet::from_string_seed(seed);
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc_handle = CreatureHandle(1);
    let content = Sts2Content;

    let mut cards = Vec::new();
    let mut deck_handles = Vec::new();
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

    let mut creatures = vec![player_creature];
    let mut enemy_handles = Vec::new();
    let mut encounter_slots = Vec::new();
    for (idx, &(mid, hp, slot)) in enemies.iter().enumerate() {
        let eh = CreatureHandle((idx + 2) as u32);
        let slot_str = slot.map(str::to_owned).unwrap_or_else(|| format!("slot_{idx}"));
        enemy_handles.push(eh);
        encounter_slots.push(slot_str.clone());
        creatures.push(CreatureInst {
            handle: eh,
            combat_id: Some((idx + 1) as u32),
            side: CombatSide::Enemy,
            slot_name: Some(Cow::Owned(slot_str)),
            is_player: false,
            pet_owner_player_index: None,
            monster_id: Some(mid),
            current_hp: hp,
            max_hp: hp,
            block: 0,
            cumulative_hp_lost: 0,
            is_detached: false,
            monster: Some(Box::new(MonsterInst {
                monster_id: mid,
                rng: Rng::from_seed(run_rngs.seed + (idx as u64) + 1),
                is_performing_move: false,
                spawned_this_turn: false,
                current_state_id: Cow::Borrowed(""),
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
        });
    }

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
        potion_slots: smallvec![None, None, None],
        combat_generated_potion_mask: 0,
        orb_capacity: character.base_orb_slot_count(),
        orbs: Vec::new(),
        arrows,
        relics: Vec::new(),
        card_or_potion_effect_depth: 0,
    };

    let ret_enemies = enemy_handles.to_vec();
    let state = BattleState {
        static_ctx: Arc::new(RootStaticContext {
            encounter_id: "TEST_ACT2".to_owned(),
            encounter_is_weak: false,
            room_type: RoomType::Monster,
            encounter_slots,
            game_mode,
            ascension,
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
            act_index: 1,
            total_floor: 20,
            ..Default::default()
        }),
        current_side: CombatSide::Player,
        round_number: 1,
        is_in_progress: false,
        is_starting: false,
        won: false,
        is_player_extra_turn: false,
        gold_was_stolen: false,
        battleworn_dummy_timed_out: false,
        end_player_turn_requested: false,
        card_action_boundary_depth: 0,
        next_creature_id: (enemies.len() + 1) as u32,
        initial_encounter_monster_count: enemies.len(),
        next_card_handle: (cards.len() + 1) as u32,
        next_creature_handle: (enemies.len() + 2) as u32,
        next_power_handle: 1,
        next_attack_id: 1,
        run_rngs,
        player_rngs,
        player,
        cards,
        card_extensions: Vec::new(),
        creatures,
        power_extensions: Vec::new(),
        ally_handles: vec![pc_handle],
        enemy_handles: enemy_handles.clone(),
        escaped_handles: SmallVec::new(),
        removed_handles: SmallVec::new(),
        spawned_enemy_handles: SmallVec::from_slice(&enemy_handles),
        pet_handles: SmallVec::new(),
        attacking_creatures: SmallVec::new(),
        deferred_death_cleanup: SmallVec::new(),
        damage_history: Vec::new(),
        semantic_history: SemanticHistory::default(),
        pending: None,
        vakuu_auto_select_depth: 0,
        hook_cache: Default::default(),
        deck_dirty: Default::default(),
        relic_dirty_mask: Default::default(),
        handle_layout: Default::default(),
        card_meta_dirty: Default::default(),
        extra_clutter_hint: Default::default(),
        eot_card_cleanup_dirty: Default::default(),
    };
    (state, ret_enemies)
}

fn single_enemy_battle(
    id: MonsterId,
    hp: i32,
    ascension: u8,
    game_mode: GameMode,
    seed: &str,
) -> (BattleState, CreatureHandle) {
    let deck = [
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::StrikeIronclad,
    ];
    let (state, enemies) = build_battle_with(
        CharacterId::Ironclad,
        &deck,
        &[(id, hp, Some("slot_0"))],
        ascension,
        game_mode,
        seed,
    );
    (state, enemies[0])
}

#[test]
fn all_24_act2_meta_hp_ranges_and_unsupported_hooks() {
    let (base_state, enemy) = single_enemy_battle(MonsterId::BowlbugEgg, 22, 0, GameMode::Standard, "HP0");
    let (tough_state, _) = single_enemy_battle(MonsterId::BowlbugEgg, 24, 8, GameMode::Standard, "HP8");
    let (dread_state, _) = single_enemy_battle(MonsterId::TheInsatiable, 361, 8, GameMode::Dread, "HPD");

    let expected_hp: [(MonsterId, (i32, i32), (i32, i32)); 24] = [
        (MonsterId::BowlbugEgg, (21, 22), (23, 24)),
        (MonsterId::BowlbugNectar, (35, 38), (36, 39)),
        (MonsterId::BowlbugRock, (45, 48), (46, 49)),
        (MonsterId::BowlbugSilk, (40, 43), (41, 44)),
        (MonsterId::Chomper, (60, 64), (63, 67)),
        (MonsterId::Crusher, (209, 209), (219, 219)),
        (MonsterId::DecimillipedeSegmentBack, (40, 46), (46, 52)),
        (MonsterId::DecimillipedeSegmentFront, (40, 46), (46, 52)),
        (MonsterId::DecimillipedeSegmentMiddle, (40, 46), (46, 52)),
        (MonsterId::Entomancer, (145, 145), (165, 165)),
        (MonsterId::Exoskeleton, (24, 28), (26, 30)),
        (MonsterId::HunterKiller, (121, 121), (126, 126)),
        (MonsterId::InfestedPrism, (161, 161), (171, 171)),
        (MonsterId::KnowledgeDemon, (379, 379), (399, 399)),
        (MonsterId::LouseProgenitor, (134, 136), (138, 141)),
        (MonsterId::Myte, (61, 67), (64, 69)),
        (MonsterId::Ovicopter, (124, 130), (126, 132)),
        (MonsterId::Rocket, (199, 199), (209, 209)),
        (MonsterId::SlumberingBeetle, (86, 86), (89, 89)),
        (MonsterId::SpinyToad, (116, 119), (121, 124)),
        (MonsterId::TheInsatiable, (321, 321), (341, 341)),
        (MonsterId::TheObscura, (123, 123), (129, 129)),
        (MonsterId::ThievingHopper, (79, 79), (84, 84)),
        (MonsterId::Tunneler, (87, 87), (92, 92)),
    ];

    assert_eq!(ALL_24_ACT2.len(), 24);
    for id in ALL_24_ACT2 {
        assert!(expected_hp.iter().any(|&(eid, _, _)| eid == id));
    }
    for (id, low, high) in expected_hp {
        let meta = act2::monster_meta(id).unwrap();
        assert_eq!(meta.hook_mask, HookMask::EMPTY);
        assert_eq!(act2::monster_initial_hp_range(&base_state, id).unwrap(), low);
        assert_eq!(act2::monster_initial_hp_range(&tough_state, id).unwrap(), high);
    }
    assert_eq!(
        act2::monster_initial_hp_range(&dread_state, MonsterId::TheInsatiable).unwrap(),
        (361, 361)
    );

    let mut s = base_state;
    assert!(matches!(
        act2::dispatch_hook(&Sts2Content, &mut s, enemy, HookSlot::AfterDeath, &HookArgs::None)
            .as_ref()
            .map_err(|e| e.kind()),
        Err(sts2_kernel::KernelErrorKind::Unsupported { kind: "MonsterHook", .. })
    ));
    assert!(matches!(
        act2::resume_hook_choice(&Sts2Content, &mut s, enemy, HookSlot::AfterDeath, &[])
            .as_ref()
            .map_err(|e| e.kind()),
        Err(sts2_kernel::KernelErrorKind::Unsupported { kind: "MonsterHookChoiceResume", .. })
    ));
}

#[test]
fn bowlbug_egg_nectar_silk_cycles_and_intents() {
    let content = Sts2Content;

    // BowlbugEgg: BITE_MOVE repeats forever, 7 dmg + 7 block (A9: 8/8)
    let (mut state, egg) = single_enemy_battle(MonsterId::BowlbugEgg, 22, 9, GameMode::Standard, "EGG");
    assert_eq!(CombatEngine::start_combat(&mut state, &content).unwrap(), StepOutcome::Completed);
    assert_eq!(act2::monster_attack_intent_hits(&state, egg).unwrap().as_slice(), &[(Dec::from_i32(8), 1)]);
    let hp_before = state.player.current_hp;
    assert_eq!(CombatEngine::end_player_turn(&mut state, &content).unwrap(), StepOutcome::Completed);
    assert_eq!(state.player.current_hp, hp_before - 8);
    assert_eq!(state.creature(egg).unwrap().block, 8);
    assert_eq!(state.creature(egg).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("BITE_MOVE"));

    // BowlbugNectar: THRASH_MOVE -> BUFF_MOVE (+15 Str, A9 +16) -> THRASH2_MOVE -> THRASH2_MOVE
    let (mut state, nectar) = single_enemy_battle(MonsterId::BowlbugNectar, 38, 0, GameMode::Standard, "NEC");
    CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(act2::monster_attack_intent_hits(&state, nectar).unwrap().as_slice(), &[(Dec::from_i32(3), 1)]);
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.player.current_hp, 77);
    assert_eq!(state.creature(nectar).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("BUFF_MOVE"));
    assert!(act2::monster_attack_intent_hits(&state, nectar).unwrap().is_empty());
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.creature(nectar).unwrap().powers[0].id, PowerId::StrengthPower);
    assert_eq!(state.creature(nectar).unwrap().powers[0].amount, 15);
    assert_eq!(state.creature(nectar).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("THRASH2_MOVE"));
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.player.current_hp, 77 - 18);
    assert_eq!(state.creature(nectar).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("THRASH2_MOVE"));

    // BowlbugSilk: TOXIC_SPIT_MOVE (Weak 1) <-> THRASH_MOVE (4x2, A9 5x2)
    let (mut state, silk) = single_enemy_battle(MonsterId::BowlbugSilk, 43, 9, GameMode::Standard, "SLK");
    CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(state.creature(silk).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("TOXIC_SPIT_MOVE"));
    assert!(act2::monster_attack_intent_hits(&state, silk).unwrap().is_empty());
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    let pc = state.player.creature_handle;
    assert_eq!(state.creature(pc).unwrap().powers.iter().find(|p| p.id == PowerId::WeakPower).unwrap().amount, 1);
    assert_eq!(state.creature(silk).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("THRASH_MOVE"));
    assert_eq!(act2::monster_attack_intent_hits(&state, silk).unwrap().as_slice(), &[(Dec::from_i32(5), 2)]);
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.player.current_hp, 70);
    assert_eq!(state.creature(silk).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("TOXIC_SPIT_MOVE"));
}

#[test]
fn bowlbug_rock_imbalanced_stun_and_dizzy_callbacks() {
    let content = Sts2Content;
    let (mut state, rock) = single_enemy_battle(MonsterId::BowlbugRock, 48, 0, GameMode::Standard, "ROCK");
    CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(state.creature(rock).unwrap().powers[0].id, PowerId::ImbalancedPower);
    assert_eq!(act2::monster_attack_intent_hits(&state, rock).unwrap().as_slice(), &[(Dec::from_i32(15), 1)]);

    // Fully block HEADBUTT_MOVE -> Imbalanced sets _isOffBalance = true -> HEADBUTT_MOVE stuns self!
    let pc = state.player.creature_handle;
    block::gain_block(&mut state, &content, pc, Dec::from_i32(20), ValueProp::MOVE, None, None).unwrap();
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.player.current_hp, 80);
    let m = state.creature(rock).unwrap().monster.as_ref().unwrap();
    assert_eq!(m.private_fields.get("BowlbugRock._isOffBalance"), Some(&FieldValue::Bool(true)));
    assert_eq!(m.next_move_id.as_deref(), Some("STUNNED"));
    assert!(!m.next_move_can_transition_away);
    assert!(act2::monster_attack_intent_hits(&state, rock).unwrap().is_empty());

    // End-to-end STUNNED via CombatEngine::end_player_turn resets _isOffBalance = false and rolls HEADBUTT_MOVE
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    let m = state.creature(rock).unwrap().monster.as_ref().unwrap();
    assert_eq!(m.private_fields.get("BowlbugRock._isOffBalance"), Some(&FieldValue::Bool(false)));
    assert_eq!(m.next_move_id.as_deref(), Some("HEADBUTT_MOVE"));
    assert!(m.next_move_can_transition_away);

    // Also verify DIZZY_MOVE branch directly
    let (mut state2, rock2) = single_enemy_battle(MonsterId::BowlbugRock, 48, 0, GameMode::Standard, "ROCK2");
    CombatEngine::start_combat(&mut state2, &content).unwrap();
    {
        let m2 = state2.creature_mut(rock2).unwrap().monster.as_mut().unwrap();
        m2.performed_first_move = true;
        m2.private_fields.insert("BowlbugRock._isOffBalance".to_owned(), FieldValue::Bool(true));
    }
    act2::monster_roll_move(&mut state2, rock2).unwrap();
    assert_eq!(state2.creature(rock2).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("DIZZY_MOVE"));
    act2::monster_perform_move(&content, &mut state2, rock2).unwrap();
    assert_eq!(
        state2.creature(rock2).unwrap().monster.as_ref().unwrap().private_fields.get("BowlbugRock._isOffBalance"),
        Some(&FieldValue::Bool(false))
    );
    act2::monster_roll_move(&mut state2, rock2).unwrap();
    assert_eq!(state2.creature(rock2).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("HEADBUTT_MOVE"));
}

#[test]
fn chomper_artifact_scream_first_and_dazed() {
    let content = Sts2Content;
    let (mut state, enemies) = build_battle_with(
        CharacterId::Ironclad,
        &[CardId::StrikeIronclad; 5],
        &[
            (MonsterId::Chomper, 64, Some("first")),
            (MonsterId::Chomper, 64, Some("second")),
        ],
        0,
        GameMode::Standard,
        "CHOMP",
    );
    let c1 = enemies[0];
    let c2 = enemies[1];
    state.creature_mut(c2).unwrap().monster.as_mut().unwrap().setup_fields.insert(
        "Chomper._screamFirst".to_owned(),
        FieldValue::Bool(true),
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(state.creature(c1).unwrap().powers[0].id, PowerId::ArtifactPower);
    assert_eq!(state.creature(c1).unwrap().powers[0].amount, 2);
    assert_eq!(state.creature(c1).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("CLAMP_MOVE"));
    assert_eq!(state.creature(c2).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("SCREECH_MOVE"));
    assert_eq!(act2::monster_attack_intent_hits(&state, c1).unwrap().as_slice(), &[(Dec::from_i32(8), 2)]);
    assert!(act2::monster_attack_intent_hits(&state, c2).unwrap().is_empty());

    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.player.current_hp, 80 - 16);
    // 3 generated Dazed total across hand/draw/discard (Turn 2 draw shuffled discard!)
    let dazed_count = state
        .player
        .hand
        .iter()
        .chain(state.player.draw.iter())
        .chain(state.player.discard.iter())
        .filter(|&&h| state.card(h).unwrap().id == CardId::Dazed)
        .count();
    assert_eq!(dazed_count, 3);
    assert_eq!(state.creature(c1).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("SCREECH_MOVE"));
    assert_eq!(state.creature(c2).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("CLAMP_MOVE"));
}

#[test]
fn crusher_and_rocket_standard_vs_dread_and_cycles() {
    let content = Sts2Content;
    let (mut state, enemies) = build_battle_with(
        CharacterId::Ironclad,
        &[CardId::StrikeIronclad; 5],
        &[
            (MonsterId::Crusher, 219, Some("crusher")),
            (MonsterId::Rocket, 209, Some("rocket")),
        ],
        9,
        GameMode::Dread,
        "CRAB_DREAD",
    );
    let crusher = enemies[0];
    let rocket = enemies[1];
    state.player.max_hp = 500;
    state.player.current_hp = 500;
    if let Some(pc_c) = state.player_creature_mut() {
        pc_c.max_hp = 500;
        pc_c.current_hp = 500;
    }
    CombatEngine::start_combat(&mut state, &content).unwrap();

    let pc = state.player.creature_handle;
    assert!(state.creature(pc).unwrap().powers.iter().any(|p| p.id == PowerId::SurroundedPower));
    assert!(state.creature(crusher).unwrap().powers.iter().any(|p| p.id == PowerId::BackAttackLeftPower));
    assert!(state.creature(crusher).unwrap().powers.iter().any(|p| p.id == PowerId::CrabRagePower));
    assert_eq!(
        state.creature(crusher).unwrap().powers.iter().find(|p| p.id == PowerId::PlatingPower).unwrap().amount,
        14
    );
    assert!(state.creature(rocket).unwrap().powers.iter().any(|p| p.id == PowerId::BackAttackRightPower));
    assert_eq!(
        state.creature(rocket).unwrap().powers.iter().find(|p| p.id == PowerId::PlatingPower).unwrap().amount,
        14
    );

    // Turn 1: Crusher THRASH_MOVE (14 * 1.5 = 21 with BackAttackLeftPower), Rocket CALCULATING_MOVE (4)
    assert_eq!(act2::monster_attack_intent_hits(&state, crusher).unwrap().as_slice(), &[(Dec::from_i32(14), 1)]);
    assert_eq!(act2::monster_attack_intent_hits(&state, rocket).unwrap().as_slice(), &[(Dec::from_i32(4), 1)]);
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.player.current_hp, 500 - 21 - 4);

    // Turn 2: Crusher ENLARGING_STRIKE_MOVE (4), Rocket PRECISE_SMASH_MOVE (20)
    assert_eq!(act2::monster_attack_intent_hits(&state, crusher).unwrap().as_slice(), &[(Dec::from_i32(4), 1)]);
    assert_eq!(act2::monster_attack_intent_hits(&state, rocket).unwrap().as_slice(), &[(Dec::from_i32(20), 1)]);
    CombatEngine::end_player_turn(&mut state, &content).unwrap();

    // Turn 3: Crusher BUG_STING_MOVE (7x2 + Weak 2 + Frail 2), Rocket CHARGE_UP_MOVE (+3 Str)
    assert_eq!(act2::monster_attack_intent_hits(&state, crusher).unwrap().as_slice(), &[(Dec::from_i32(7), 2)]);
    assert!(act2::monster_attack_intent_hits(&state, rocket).unwrap().is_empty());
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.creature(pc).unwrap().powers.iter().find(|p| p.id == PowerId::WeakPower).unwrap().amount, 2);
    assert_eq!(state.creature(pc).unwrap().powers.iter().find(|p| p.id == PowerId::FrailPower).unwrap().amount, 2);
    assert_eq!(state.creature(rocket).unwrap().powers.iter().find(|p| p.id == PowerId::StrengthPower).unwrap().amount, 3);

    // Turn 4: Crusher ADAPT_MOVE (+3 Str), Rocket ROCKET_CLAW_MOVE (35 + 5 Dread = 40 base)
    assert_eq!(act2::monster_attack_intent_hits(&state, rocket).unwrap().as_slice(), &[(Dec::from_i32(40), 1)]);
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.creature(crusher).unwrap().powers.iter().find(|p| p.id == PowerId::StrengthPower).unwrap().amount, 3);

    // Turn 5: Crusher GUARDED_STRIKE_MOVE (14 dmg + 18 block), Rocket RECHARGE_MOVE
    assert_eq!(act2::monster_attack_intent_hits(&state, crusher).unwrap().as_slice(), &[(Dec::from_i32(14), 1)]);
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert!(state.creature(crusher).unwrap().block >= 18);
    assert_eq!(state.creature(crusher).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("THRASH_MOVE"));
    assert_eq!(state.creature(rocket).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("CALCULATING_MOVE"));

    // Standard mode has NO PlatingPower and RocketClaw is 31
    let (mut std_state, rocket_std) = single_enemy_battle(MonsterId::Rocket, 199, 0, GameMode::Standard, "RCK_STD");
    CombatEngine::start_combat(&mut std_state, &content).unwrap();
    assert!(!std_state.creature(rocket_std).unwrap().powers.iter().any(|p| p.id == PowerId::PlatingPower));
    std_state.creature_mut(rocket_std).unwrap().monster.as_mut().unwrap().next_move_id = Some(Cow::Borrowed("ROCKET_CLAW_MOVE"));
    assert_eq!(act2::monster_attack_intent_hits(&std_state, rocket_std).unwrap().as_slice(), &[(Dec::from_i32(31), 1)]);
}

#[test]
fn decimillipede_hp_dedup_starter_moves_death_and_reattach() {
    let content = Sts2Content;
    let (mut state, segs) = build_battle_with(
        CharacterId::Ironclad,
        &[CardId::StrikeIronclad; 5],
        &[
            (MonsterId::DecimillipedeSegmentFront, 41, Some("segment1")),
            (MonsterId::DecimillipedeSegmentMiddle, 41, Some("segment2")),
            (MonsterId::DecimillipedeSegmentBack, 41, Some("segment3")),
        ],
        0,
        GameMode::Standard,
        "DECI",
    );
    for (idx, &seg) in segs.iter().enumerate() {
        state.creature_mut(seg).unwrap().monster.as_mut().unwrap().setup_fields.insert(
            "DecimillipedeSegment._starterMoveIdx".to_owned(),
            FieldValue::Int(idx as i32),
        );
    }
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // 41 -> 42, 44, 46 unique even HPs, all healed to max, all have ReattachPower(25)
    assert_eq!(state.creature(segs[0]).unwrap().max_hp, 42);
    assert_eq!(state.creature(segs[1]).unwrap().max_hp, 44);
    assert_eq!(state.creature(segs[2]).unwrap().max_hp, 46);
    for &seg in &segs {
        assert_eq!(state.creature(seg).unwrap().current_hp, state.creature(seg).unwrap().max_hp);
        assert_eq!(state.creature(seg).unwrap().powers.iter().find(|p| p.id == PowerId::ReattachPower).unwrap().amount, 25);
    }
    assert_eq!(state.creature(segs[0]).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("WRITHE_MOVE"));
    assert_eq!(state.creature(segs[1]).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("BULK_MOVE"));
    assert_eq!(state.creature(segs[2]).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("CONSTRICT_MOVE"));

    // Kill segment 0 on Turn 1 -> ReattachPower sets DEAD_MOVE & _isReviving = true
    let pc = state.player.creature_handle;
    damage::damage_targets(
        &mut state,
        &content,
        &[segs[0]],
        Dec::from_i32(100),
        ValueProp::MOVE,
        Some(pc),
        None,
        None,
    )
    .unwrap();
    assert!(state.creature(segs[0]).unwrap().is_dead());
    assert_eq!(state.creature(segs[0]).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("DEAD_MOVE"));

    // End player turn -> Enemy Turn 1 performs DEAD_MOVE -> Player Turn 2 rolls REATTACH_MOVE
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    let m0 = state.creature(segs[0]).unwrap().monster.as_ref().unwrap();
    assert_eq!(m0.next_move_id.as_deref(), Some("REATTACH_MOVE"));
    assert!(!m0.next_move_can_transition_away);

    // End player turn -> Enemy Turn 2 performs REATTACH_MOVE -> heals 25, _isReviving = false -> Player Turn 3 rolls RAND (CannotRepeat)
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.creature(segs[0]).unwrap().current_hp, 25);
    let next = state.creature(segs[0]).unwrap().monster.as_ref().unwrap().next_move_id.as_deref().unwrap();
    assert!(matches!(next, "WRITHE_MOVE" | "BULK_MOVE" | "CONSTRICT_MOVE"));
    let _ = batch_n_z::reattach_should_owner_death_trigger_fatal;
}

#[test]
fn entomancer_exoskeleton_hunter_killer_infested_prism() {
    let content = Sts2Content;

    // Entomancer: PersonalHivePower(1) -> BEES -> SPEAR -> PHEROMONE (+1 Hive, +1 Str until Hive >= 3, then +2 Str)
    let (mut state, ent) = single_enemy_battle(MonsterId::Entomancer, 145, 0, GameMode::Standard, "ENT");
    state.player.max_hp = 500;
    state.player.current_hp = 500;
    if let Some(pc) = state.player_creature_mut() {
        pc.max_hp = 500;
        pc.current_hp = 500;
    }
    CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(state.creature(ent).unwrap().powers[0].id, PowerId::PersonalHivePower);
    assert_eq!(act2::monster_attack_intent_hits(&state, ent).unwrap().as_slice(), &[(Dec::from_i32(3), 7)]);
    for _ in 0..3 {
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
    }
    assert_eq!(state.creature(ent).unwrap().powers.iter().find(|p| p.id == PowerId::PersonalHivePower).unwrap().amount, 2);
    assert_eq!(state.creature(ent).unwrap().powers.iter().find(|p| p.id == PowerId::StrengthPower).unwrap().amount, 1);

    // Run 6 more turns so PHEROMONE triggers twice more (Hive reaches 3, then +2 Str)
    for _ in 0..6 {
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
    }
    assert_eq!(state.creature(ent).unwrap().powers.iter().find(|p| p.id == PowerId::PersonalHivePower).unwrap().amount, 3);
    assert_eq!(state.creature(ent).unwrap().powers.iter().find(|p| p.id == PowerId::StrengthPower).unwrap().amount, 4);

    // Exoskeleton: HardToKill(9), slots first/second/third/fourth
    let (mut state, exos) = build_battle_with(
        CharacterId::Ironclad,
        &[CardId::StrikeIronclad; 5],
        &[
            (MonsterId::Exoskeleton, 28, Some("first")),
            (MonsterId::Exoskeleton, 28, Some("second")),
            (MonsterId::Exoskeleton, 28, Some("third")),
            (MonsterId::Exoskeleton, 28, Some("fourth")),
        ],
        9,
        GameMode::Standard,
        "EXO",
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(state.creature(exos[0]).unwrap().powers[0].id, PowerId::HardToKillPower);
    assert_eq!(state.creature(exos[0]).unwrap().powers[0].amount, 9);
    assert_eq!(state.creature(exos[0]).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("SKITTER_MOVE"));
    assert_eq!(act2::monster_attack_intent_hits(&state, exos[0]).unwrap().as_slice(), &[(Dec::ONE, 4)]);
    assert_eq!(state.creature(exos[1]).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("MANDIBLES_MOVE"));
    assert_eq!(state.creature(exos[2]).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("ENRAGE_MOVE"));
    let fourth_mv = state.creature(exos[3]).unwrap().monster.as_ref().unwrap().next_move_id.as_deref().unwrap();
    assert!(matches!(fourth_mv, "SKITTER_MOVE" | "MANDIBLES_MOVE"));

    // HunterKiller: TENDERIZING_GOOP_MOVE -> RAND (BITE CannotRepeat, PUNCTURE max 2)
    let (mut state, hk) = single_enemy_battle(MonsterId::HunterKiller, 121, 0, GameMode::Standard, "HK");
    CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(state.creature(hk).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("TENDERIZING_GOOP_MOVE"));
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert!(state.creature(state.player.creature_handle).unwrap().powers.iter().any(|p| p.id == PowerId::TenderPower));
    for _ in 0..8 {
        if !state.is_in_progress {
            break;
        }
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
    }
    let log = &state.creature(hk).unwrap().monster.as_ref().unwrap().state_log_ids;
    for w in log.windows(2) {
        if w[0] == "BITE_MOVE" {
            assert_ne!(w[1], "BITE_MOVE", "BITE_MOVE cannot repeat");
        }
    }
    for w in log.windows(3) {
        assert!(!(w[0] == "PUNCTURE_MOVE" && w[1] == "PUNCTURE_MOVE" && w[2] == "PUNCTURE_MOVE"));
    }

    // InfestedPrism: VitalSparkPower(2), JAB -> JAB_2 -> WHIRLWIND -> PULSATE
    let (mut state, prism) = single_enemy_battle(MonsterId::InfestedPrism, 161, 0, GameMode::Standard, "PRISM");
    CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(state.creature(prism).unwrap().powers.iter().find(|p| p.id == PowerId::VitalSparkPower).unwrap().amount, 2);
    assert_eq!(state.creature(prism).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("JAB_MOVE"));
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.creature(prism).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("JAB_MOVE_2"));
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.creature(prism).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("WHIRLWIND_MOVE"));
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.creature(prism).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("PULSATE_MOVE"));
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.creature(prism).unwrap().powers.iter().find(|p| p.id == PowerId::VitalSparkPower).unwrap().amount, 4);
    assert_eq!(state.creature(state.player.creature_handle).unwrap().powers.iter().find(|p| p.id == PowerId::VulnerablePower).unwrap().amount, 1);
}

#[test]
fn knowledge_demon_choice_initiation_resume_and_ponder_cycle() {
    let content = Sts2Content;
    let (mut state, kd) = single_enemy_battle(MonsterId::KnowledgeDemon, 379, 0, GameMode::Standard, "KD1");
    CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(state.creature(kd).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("CURSE_OF_KNOWLEDGE_MOVE"));

    // Counter 0 end-to-end via CombatEngine::end_player_turn -> NeedsChoice -> resume_choice: Disintegration(6) vs MindRot
    let out0 = CombatEngine::end_player_turn(&mut state, &content).unwrap();
    let StepOutcome::NeedsChoice(req0) = out0 else {
        panic!("expected NeedsChoice from KnowledgeDemon CURSE_OF_KNOWLEDGE_MOVE, got {out0:?}");
    };
    assert_eq!(req0.timing, ChoiceTiming::EnemyTurn);
    assert_eq!(req0.candidates.len(), 2);
    assert_eq!(state.card(req0.candidates[0]).unwrap().id, CardId::Disintegration);
    assert_eq!(state.card(req0.candidates[1]).unwrap().id, CardId::MindRot);
    assert_eq!(
        CombatEngine::resume_choice(&mut state, &content, &[0]).unwrap(),
        StepOutcome::Completed
    );
    let pc = state.player.creature_handle;
    assert_eq!(state.creature(pc).unwrap().powers.iter().find(|p| p.id == PowerId::DisintegrationPower).unwrap().amount, 6);
    assert_eq!(
        state.creature(kd).unwrap().monster.as_ref().unwrap().private_fields.get("KnowledgeDemon._curseCounter"),
        Some(&FieldValue::Int(1))
    );
    assert_eq!(
        state.creature(kd).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("SLAP_MOVE")
    );

    // Counter 1: Disintegration(7) vs Sloth
    state.creature_mut(kd).unwrap().monster.as_mut().unwrap().next_move_id = Some(Cow::Borrowed("CURSE_OF_KNOWLEDGE_MOVE"));
    let StepOutcome::NeedsChoice(req1) = CombatEngine::end_player_turn(&mut state, &content).unwrap() else {
        panic!("expected NeedsChoice on second CURSE_OF_KNOWLEDGE_MOVE");
    };
    assert_eq!(state.card(req1.candidates[1]).unwrap().id, CardId::Sloth);
    CombatEngine::resume_choice(&mut state, &content, &[1]).unwrap();
    assert_eq!(state.creature(pc).unwrap().powers.iter().find(|p| p.id == PowerId::SlothPower).unwrap().amount, 3);

    // Counter 2 in Dread: Disintegration(8 + 2 = 10) vs WasteAway
    let (mut dread_s, dread_kd) = single_enemy_battle(MonsterId::KnowledgeDemon, 399, 9, GameMode::Dread, "KD2");
    CombatEngine::start_combat(&mut dread_s, &content).unwrap();
    dread_s.creature_mut(dread_kd).unwrap().monster.as_mut().unwrap().private_fields.insert(
        "KnowledgeDemon._curseCounter".to_owned(),
        FieldValue::Int(2),
    );
    act2::monster_perform_move(&content, &mut dread_s, dread_kd).unwrap();
    let req2 = dread_s.pending.take().unwrap().request;
    assert_eq!(state.card(req1.candidates[0]).unwrap().id, CardId::Disintegration);
    assert_eq!(dread_s.card(req2.candidates[1]).unwrap().id, CardId::WasteAway);
    act2::monster_resume_move_choice(&content, &mut dread_s, dread_kd, &[req2.candidates[0]]).unwrap();
    assert_eq!(
        dread_s.creature(dread_s.player.creature_handle).unwrap().powers.iter().find(|p| p.id == PowerId::DisintegrationPower).unwrap().amount,
        10
    );

    // Also verify WasteAway choice and SLAP -> OVERWHELMING -> PONDER -> POST_PONDER branch
    let (mut state_w, kd_w) = single_enemy_battle(MonsterId::KnowledgeDemon, 379, 0, GameMode::Standard, "KDW");
    CombatEngine::start_combat(&mut state_w, &content).unwrap();
    state_w.creature_mut(kd_w).unwrap().monster.as_mut().unwrap().private_fields.insert(
        "KnowledgeDemon._curseCounter".to_owned(),
        FieldValue::Int(2),
    );
    act2::monster_perform_move(&content, &mut state_w, kd_w).unwrap();
    let req_w = state_w.pending.take().unwrap().request;
    act2::monster_resume_move_choice(&content, &mut state_w, kd_w, &[req_w.candidates[1]]).unwrap();
    assert_eq!(
        state_w.creature(state_w.player.creature_handle).unwrap().powers.iter().find(|p| p.id == PowerId::WasteAwayPower).unwrap().amount,
        1
    );

    // SLAP_MOVE -> KNOWLEDGE_OVERWHELMING_MOVE -> PONDER_MOVE -> SLAP_MOVE (since _curseCounter == 3)
    act2::monster_roll_move(&mut state_w, kd_w).unwrap();
    assert_eq!(state_w.creature(kd_w).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("SLAP_MOVE"));
    assert_eq!(act2::monster_attack_intent_hits(&state_w, kd_w).unwrap().as_slice(), &[(Dec::from_i32(17), 1)]);
    act2::monster_perform_move(&content, &mut state_w, kd_w).unwrap();
    act2::monster_roll_move(&mut state_w, kd_w).unwrap();
    assert_eq!(state_w.creature(kd_w).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("KNOWLEDGE_OVERWHELMING_MOVE"));
    assert_eq!(act2::monster_attack_intent_hits(&state_w, kd_w).unwrap().as_slice(), &[(Dec::from_i32(8), 3)]);
    act2::monster_perform_move(&content, &mut state_w, kd_w).unwrap();
    act2::monster_roll_move(&mut state_w, kd_w).unwrap();
    assert_eq!(state_w.creature(kd_w).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("PONDER_MOVE"));
    state_w.creature_mut(kd_w).unwrap().current_hp = 300;
    act2::monster_perform_move(&content, &mut state_w, kd_w).unwrap();
    assert_eq!(state_w.creature(kd_w).unwrap().current_hp, 330);
    assert_eq!(state_w.creature(kd_w).unwrap().powers.iter().find(|p| p.id == PowerId::StrengthPower).unwrap().amount, 2);
    act2::monster_roll_move(&mut state_w, kd_w).unwrap();
    assert_eq!(state_w.creature(kd_w).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("SLAP_MOVE"));
}

#[test]
fn louse_progenitor_myte_ovicopter_spiny_toad_and_obscura() {
    let content = Sts2Content;

    // LouseProgenitor
    let (mut state, louse) = single_enemy_battle(MonsterId::LouseProgenitor, 136, 0, GameMode::Standard, "LOUSE");
    CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(state.creature(louse).unwrap().powers[0].id, PowerId::CurlUpPower);
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.creature(state.player.creature_handle).unwrap().powers.iter().find(|p| p.id == PowerId::FrailPower).unwrap().amount, 2);
    assert_eq!(state.creature(louse).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("CURL_AND_GROW_MOVE"));
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.creature(louse).unwrap().block, 14);
    assert_eq!(state.creature(louse).unwrap().powers.iter().find(|p| p.id == PowerId::StrengthPower).unwrap().amount, 5);
    assert_eq!(state.creature(louse).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("POUNCE_MOVE"));

    // Myte: first -> TOXIC_MOVE (2 Toxic in hand), second -> SUCK_MOVE
    let (mut state, mytes) = build_battle_with(
        CharacterId::Ironclad,
        &[CardId::StrikeIronclad; 5],
        &[
            (MonsterId::Myte, 67, Some("first")),
            (MonsterId::Myte, 67, Some("second")),
        ],
        0,
        GameMode::Standard,
        "MYTE",
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(state.creature(mytes[0]).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("TOXIC_MOVE"));
    assert_eq!(state.creature(mytes[1]).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("SUCK_MOVE"));
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    let toxic_in_hand = state.player.hand.iter().filter(|&&h| state.card(h).unwrap().id == CardId::Toxic).count()
        + state.player.discard.iter().filter(|&&h| state.card(h).unwrap().id == CardId::Toxic).count();
    assert_eq!(toxic_in_hand, 2);

    // Ovicopter: LAY_EGGS_MOVE spawns 3 ToughEgg in egg5, egg4, egg3 with MinionPower, then SUMMON_BRANCH_STATE chooses NUTRITIONAL_PASTE_MOVE (>3 living)
    let (mut state, ovi_vec) = build_battle_with(
        CharacterId::Ironclad,
        &[CardId::StrikeIronclad; 5],
        &[(MonsterId::Ovicopter, 130, Some("ovicopter"))],
        0,
        GameMode::Standard,
        "OVI",
    );
    let ovi = ovi_vec[0];
    state.static_ctx = Arc::new(RootStaticContext {
        encounter_slots: vec!["egg1".into(), "egg2".into(), "egg3".into(), "egg4".into(), "egg5".into(), "ovicopter".into()],
        ..(*state.static_ctx).clone()
    });
    CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(state.creature(ovi).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("LAY_EGGS_MOVE"));
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.enemy_handles.len(), 4);
    for eh in &state.enemy_handles {
        if *eh != ovi {
            assert_eq!(state.creature(*eh).unwrap().monster_id, Some(MonsterId::ToughEgg));
            assert!(state.creature(*eh).unwrap().powers.iter().any(|p| p.id == PowerId::MinionPower));
        }
    }
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.creature(ovi).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("NUTRITIONAL_PASTE_MOVE"));

    // SpinyToad: +5 Thorns -> SPIKE_EXPLOSION_MOVE removes 5 Thorns -> TONGUE_LASH_MOVE
    let (mut state, toad) = single_enemy_battle(MonsterId::SpinyToad, 119, 0, GameMode::Standard, "TOAD");
    CombatEngine::start_combat(&mut state, &content).unwrap();
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.creature(toad).unwrap().powers.iter().find(|p| p.id == PowerId::ThornsPower).unwrap().amount, 5);
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert!(!state.creature(toad).unwrap().powers.iter().any(|p| p.id == PowerId::ThornsPower));
    assert_eq!(state.creature(toad).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("TONGUE_LASH_MOVE"));

    // TheObscura: ILLUSION_MOVE spawns Parafright, SAIL_MOVE buffs both, RANDOM_MOVE CannotRepeat
    let (mut state, obs_vec) = build_battle_with(
        CharacterId::Ironclad,
        &[CardId::StrikeIronclad; 5],
        &[(MonsterId::TheObscura, 123, Some("obscura"))],
        0,
        GameMode::Standard,
        "OBS",
    );
    let obs = obs_vec[0];
    state.static_ctx = Arc::new(RootStaticContext {
        encounter_slots: vec!["illusion".into(), "obscura".into()],
        ..(*state.static_ctx).clone()
    });
    CombatEngine::start_combat(&mut state, &content).unwrap();
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.enemy_handles.len(), 2);
    assert_eq!(state.creature(state.enemy_handles[0]).unwrap().monster_id, Some(MonsterId::Parafright));
    state.creature_mut(obs).unwrap().monster.as_mut().unwrap().next_move_id = Some(Cow::Borrowed("SAIL_MOVE"));
    act2::monster_perform_move(&content, &mut state, obs).unwrap();
    for eh in &state.enemy_handles {
        assert_eq!(state.creature(*eh).unwrap().powers.iter().find(|p| p.id == PowerId::StrengthPower).unwrap().amount, 3);
    }
}

#[test]
fn slumbering_beetle_tunneler_insatiable_and_thieving_hopper() {
    let content = Sts2Content;

    // SlumberingBeetle: starts asleep, SlumberPower breaks on damage -> STUNNED WakeUp removes PlatingPower
    let (mut state, beetle) = single_enemy_battle(MonsterId::SlumberingBeetle, 86, 0, GameMode::Standard, "BEET");
    CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(state.creature(beetle).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("SNORE_MOVE"));
    assert_eq!(state.creature(beetle).unwrap().powers.iter().find(|p| p.id == PowerId::PlatingPower).unwrap().amount, 15);
    assert_eq!(state.creature(beetle).unwrap().powers.iter().find(|p| p.id == PowerId::SlumberPower).unwrap().amount, 3);
    let pc_beet = state.player.creature_handle;
    for _ in 0..3 {
        damage::damage_targets(&mut state, &content, &[beetle], Dec::from_i32(20), ValueProp::MOVE, Some(pc_beet), None, None).unwrap();
    }
    assert_eq!(state.creature(beetle).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("STUNNED"));
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert!(!state.creature(beetle).unwrap().powers.iter().any(|p| p.id == PowerId::PlatingPower));
    assert!(state.creature(beetle).unwrap().monster.as_ref().unwrap().next_move_can_transition_away);
    assert_eq!(state.creature(beetle).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("ROLL_OUT_MOVE"));

    // Tunneler: BITE -> BURROW (BurrowedPower + 32 Block) -> break block -> STUNNED via CombatEngine::end_player_turn resets _isStunned = false
    let (mut state, tun) = single_enemy_battle(MonsterId::Tunneler, 87, 0, GameMode::Standard, "TUN");
    CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(state.creature(tun).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("BITE_MOVE"));
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.creature(tun).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("BURROW_MOVE"));
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert!(state.creature(tun).unwrap().powers.iter().any(|p| p.id == PowerId::BurrowedPower));
    assert_eq!(state.creature(tun).unwrap().block, 32);
    let pc_tun = state.player.creature_handle;
    damage::damage_targets(&mut state, &content, &[tun], Dec::from_i32(40), ValueProp::MOVE, Some(pc_tun), None, None).unwrap();
    let m = state.creature(tun).unwrap().monster.as_ref().unwrap();
    assert_eq!(m.private_fields.get("Tunneler._isStunned"), Some(&FieldValue::Bool(true)));
    assert_eq!(m.next_move_id.as_deref(), Some("STUNNED"));
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    let m = state.creature(tun).unwrap().monster.as_ref().unwrap();
    assert_eq!(m.private_fields.get("Tunneler._isStunned"), Some(&FieldValue::Bool(false)));
    assert!(m.next_move_can_transition_away);

    // TheInsatiable: Sandpit(4 std / 3 Dread) + Target + 6 FranticEscape (3 Draw, 3 Discard)
    let (mut state, ins) = single_enemy_battle(MonsterId::TheInsatiable, 321, 0, GameMode::Standard, "INS");
    CombatEngine::start_combat(&mut state, &content).unwrap();
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    let sandpit = state.creature(ins).unwrap().powers.iter().find(|p| p.id == PowerId::SandpitPower).unwrap();
    assert_eq!(sandpit.amount, 4);
    let sp_ext = state.power_extensions[sandpit.ext_index.unwrap() as usize].private_fields.get("SandpitPower.<Target>k__BackingField");
    assert_eq!(sp_ext, Some(&FieldValue::CreatureHandle(state.player.creature_handle.0 as i32)));
    // Before Turn 2 draws, verify 3 FranticEscape in draw and 3 in discard via direct move,
    // and here verify all 6 FranticEscape exist across hand/draw/discard after Turn 2 draw.
    let frantic_total = state
        .player
        .hand
        .iter()
        .chain(state.player.draw.iter())
        .chain(state.player.discard.iter())
        .filter(|&&h| state.card(h).unwrap().id == CardId::FranticEscape)
        .count();
    assert_eq!(frantic_total, 6);
    assert_eq!(state.creature(ins).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(), Some("THRASH_MOVE"));

    // Also verify Dread TheInsatiable puts Sandpit(3) and splits 3 Draw / 3 Discard before draw
    let (mut ins_dread, ins_d) = single_enemy_battle(MonsterId::TheInsatiable, 361, 8, GameMode::Dread, "INS_D");
    CombatEngine::start_combat(&mut ins_dread, &content).unwrap();
    act2::monster_perform_move(&content, &mut ins_dread, ins_d).unwrap();
    let sp_d = ins_dread.creature(ins_d).unwrap().powers.iter().find(|p| p.id == PowerId::SandpitPower).unwrap();
    assert_eq!(sp_d.amount, 3);
    assert_eq!(ins_dread.player.draw.iter().filter(|&&h| ins_dread.card(h).unwrap().id == CardId::FranticEscape).count(), 3);
    assert_eq!(ins_dread.player.discard.iter().filter(|&&h| ins_dread.card(h).unwrap().id == CardId::FranticEscape).count(), 3);

    // ThievingHopper: priority theft (Uncommon > Common > Basic > Ancient/Imbued), EscapeArtist, Flutter, Escape + arrow dislodge
    let deck = [
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::ShrugItOff, // Common
        CardId::Uppercut,   // Uncommon (should be stolen first!)
    ];
    let (mut state, enemies) = build_battle_with(
        CharacterId::Fool,
        &deck,
        &[(MonsterId::ThievingHopper, 79, Some("slot_0"))],
        0,
        GameMode::Standard,
        "HOPPER",
    );
    let hopper = enemies[0];
    // Mark ShrugItOff as Imbued so even if in draw it has priority 3, while Uppercut has priority 0
    for h in state.cards.iter().map(|c| c.handle).collect::<Vec<_>>() {
        if state.card(h).unwrap().id == CardId::ShrugItOff {
            state.card_ext_mut(h).unwrap().enchantment = Some(EnchantmentInst {
                id: EnchantmentId::Imbued,
                amount: 1,
                status: EnchantmentStatus::Normal,
                private_fields: Default::default(),
            });
        }
    }
    CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(state.creature(hopper).unwrap().powers[0].id, PowerId::EscapeArtistPower);
    assert_eq!(state.creature(hopper).unwrap().powers[0].amount, 5);
    // Impale an arrow on hopper to verify escape dislodges arrows
    arrow::shoot_arrow_at_target_with_action(&mut state, &content, hopper, None, |_, _, _| Ok(())).unwrap();
    assert_eq!(state.player.arrows[0].status, ArrowStatus::Away);

    // Run THIEVERY_MOVE -> FLUTTER_MOVE -> HAT_TRICK_MOVE -> NAB_MOVE -> ESCAPE_MOVE
    // Ensure Uppercut is in draw or discard for deterministic theft
    if let Some(pos) = state.player.hand.iter().position(|&h| state.card(h).unwrap().id == CardId::Uppercut) {
        let h = state.player.hand.remove(pos);
        state.player.draw.push(h);
        state.card_mut(h).unwrap().pile = PileType::Draw;
    }
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    let swipe = state.creature(hopper).unwrap().powers.iter().find(|p| p.id == PowerId::SwipePower).unwrap();
    let stolen_fv = state.power_extensions[swipe.ext_index.unwrap() as usize].private_fields.get("SwipePower.<StolenCard>k__BackingField").cloned();
    assert!(matches!(stolen_fv, Some(FieldValue::CardHandle(_))));
    if let Some(FieldValue::CardHandle(sh)) = stolen_fv {
        assert_eq!(state.card(CardHandle(sh as u32)).unwrap().id, CardId::Uppercut);
        assert!(!state.player.deck.contains(&CardHandle(sh as u32)));
    }
    for _ in 0..4 {
        if !state.is_in_progress {
            break;
        }
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
    }
    assert!(state.escaped_handles.contains(&hopper));
    assert!(!state.enemy_handles.contains(&hopper));
    assert!(state.won);
    assert_eq!(state.player.arrows[0].status, ArrowStatus::Retrieved);
}

#[test]
fn thieving_hopper_flutter_stun_advances_to_nab_move() {
    let content = Sts2Content;
    let deck = [CardId::StrikeIronclad; 5];
    let (mut state, enemies) = build_battle_with(
        CharacterId::Ironclad,
        &deck,
        &[(MonsterId::ThievingHopper, 200, Some("slot_0"))],
        0,
        GameMode::Standard,
        "HOPPER_FLUTTER_STUN",
    );
    let hopper = enemies[0];
    let pc = state.player.creature_handle;
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // Turn 1 THIEVERY_MOVE -> Turn 2 FLUTTER_MOVE -> Turn 3 HAT_TRICK_MOVE
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(
        state
            .creature(hopper)
            .unwrap()
            .monster
            .as_ref()
            .unwrap()
            .next_move_id
            .as_deref(),
        Some("HAT_TRICK_MOVE")
    );

    // 5 powered attacks remove FlutterPower(5), stunning ThievingHopper with follow-up NAB_MOVE.
    for _ in 0..5 {
        damage::damage_targets(
            &mut state,
            &content,
            &[hopper],
            Dec::from_i32(4),
            ValueProp::MOVE,
            Some(pc),
            None,
            None,
        )
        .unwrap();
    }
    let m_stunned = state.creature(hopper).unwrap().monster.as_ref().unwrap();
    assert_eq!(m_stunned.next_move_id.as_deref(), Some("STUNNED"));
    assert_eq!(
        m_stunned.transient_move_follow_up_state_id.as_deref(),
        Some("NAB_MOVE")
    );

    // Ending turn executes STUNNED and prepares Turn 4 with NAB_MOVE (not HAT_TRICK_MOVE).
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(
        state
            .creature(hopper)
            .unwrap()
            .monster
            .as_ref()
            .unwrap()
            .next_move_id
            .as_deref(),
        Some("NAB_MOVE")
    );
}
