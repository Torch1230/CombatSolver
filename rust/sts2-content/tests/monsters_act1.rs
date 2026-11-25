use smallvec::SmallVec;
use std::borrow::Cow;
use std::sync::Arc;
use sts2_content::monsters::act1;
use sts2_content::Sts2Content;
use sts2_kernel::commands::{creature as creature_cmd, damage::AttackBuilder};
use sts2_kernel::{
    BattleState, CardHandle, CardInst, CardTempFlags, CombatEngine, ContentDispatcher,
    CreatureHandle, CreatureInst, FieldMap, FieldValue, HookArgs, HookListener, MonsterInst,
    PlayerCounters, PlayerInst, RootStaticContext, SemanticHistory, StepOutcome,
};
use sts2_types::{
    CardId, CardKeywords, CharacterId, CombatSide, Dec, GameMode, HookSlot, MonsterId, PileType,
    PlayerRngSet, PlayerTurnPhase, PowerId, Rng, RoomType, RunRngSet, ValueProp,
};

const ALL_46_ACT1_MONSTERS: [MonsterId; 46] = [
    MonsterId::AssassinRubyRaider,
    MonsterId::AxeRubyRaider,
    MonsterId::BruteRubyRaider,
    MonsterId::BygoneEffigy,
    MonsterId::Byrdonis,
    MonsterId::CalcifiedCultist,
    MonsterId::CeremonialBeast,
    MonsterId::CorpseSlug,
    MonsterId::CrossbowRubyRaider,
    MonsterId::CubexConstruct,
    MonsterId::DampCultist,
    MonsterId::Flyconid,
    MonsterId::Fogmog,
    MonsterId::FossilStalker,
    MonsterId::FuzzyWurmCrawler,
    MonsterId::GremlinMerc,
    MonsterId::HauntedShip,
    MonsterId::Inklet,
    MonsterId::KinFollower,
    MonsterId::KinPriest,
    MonsterId::LagavulinMatriarch,
    MonsterId::LeafSlimeM,
    MonsterId::LeafSlimeS,
    MonsterId::LivingFog,
    MonsterId::Mawler,
    MonsterId::Nibbit,
    MonsterId::PhantasmalGardener,
    MonsterId::PhrogParasite,
    MonsterId::PunchConstruct,
    MonsterId::Seapunk,
    MonsterId::SewerClam,
    MonsterId::ShrinkerBeetle,
    MonsterId::SkulkingColony,
    MonsterId::SlitheringStrangler,
    MonsterId::SludgeSpinner,
    MonsterId::SnappingJaxfruit,
    MonsterId::SoulFysh,
    MonsterId::TerrorEel,
    MonsterId::Toadpole,
    MonsterId::TrackerRubyRaider,
    MonsterId::TwigSlimeM,
    MonsterId::TwigSlimeS,
    MonsterId::TwoTailedRat,
    MonsterId::Vantom,
    MonsterId::VineShambler,
    MonsterId::WaterfallGiant,
];

fn build_battle(
    seed: &str,
    game_mode: GameMode,
    ascension: u8,
    enemies: &[(MonsterId, Option<&str>, FieldMap)],
) -> (BattleState, SmallVec<[CreatureHandle; 8]>) {
    let run_rngs = RunRngSet::from_string_seed(seed);
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc_handle = CreatureHandle(1);
    let character = CharacterId::Ironclad;
    let content = Sts2Content;

    let mut cards = Vec::new();
    let mut deck_handles = Vec::new();
    let deck_ids = [
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::Bash,
    ];
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

    let player_creature = CreatureInst {
        handle: pc_handle,
        combat_id: Some(0),
        side: CombatSide::Player,
        slot_name: None,
        is_player: true,
        pet_owner_player_index: None,
        monster_id: None,
        current_hp: 500,
        max_hp: 500,
        block: 0,
        cumulative_hp_lost: 0,
        is_detached: false,
        monster: None,
        has_minion_power: false,
        powers: Vec::new(),
    };

    let player = PlayerInst {
        creature_handle: pc_handle,
        current_hp: 500,
        max_hp: 500,
        block: 0,
        cumulative_hp_lost: 0,
        gold: 100,
        max_energy: 3,
        base_orb_slot_count: 0,
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
        orb_capacity: 0,
        orbs: Vec::new(),
        arrows: Vec::new(),
        relics: Vec::new(),
        card_or_potion_effect_depth: 0,
    };

    let mut state = BattleState {
        static_ctx: Arc::new(RootStaticContext {
            encounter_id: "ACT1_TEST".to_owned(),
            encounter_is_weak: false,
            room_type: RoomType::Monster,
            encounter_slots: enemies
                .iter()
                .enumerate()
                .map(|(i, (_, s, _))| s.unwrap_or(&format!("slot_{i}")).to_owned())
                .collect(),
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
        next_creature_id: 1 + enemies.len() as u32,
        initial_encounter_monster_count: enemies.len(),
        next_card_handle: (cards.len() + 1) as u32,
        next_creature_handle: (2 + enemies.len()) as u32,
        next_power_handle: 1,
        next_attack_id: 1,
        run_rngs,
        player_rngs,
        player,
        cards,
        card_extensions: Vec::new(),
        creatures: vec![player_creature],
        power_extensions: Vec::new(),
        ally_handles: vec![pc_handle],
        enemy_handles: Vec::new(),
        escaped_handles: SmallVec::new(),
        removed_handles: SmallVec::new(),
        spawned_enemy_handles: SmallVec::new(),
        pet_handles: SmallVec::new(),
        damage_history: Vec::new(),
        semantic_history: SemanticHistory::default(),
        pending: None,
        vakuu_auto_select_depth: 0,
        hook_cache: Default::default(),
        deck_dirty: Default::default(),
        relic_dirty_mask: Default::default(),
    };

    let mut enemy_handles = SmallVec::new();
    for (idx, (mid, slot, setup_fields)) in enemies.iter().enumerate() {
        let eh = CreatureHandle((2 + idx) as u32);
        let (_, max_hp) = act1::monster_initial_hp_range(&state, *mid).unwrap();
        state.creatures.push(CreatureInst {
            handle: eh,
            combat_id: Some((1 + idx) as u32),
            side: CombatSide::Enemy,
            slot_name: Some(Cow::Owned((*slot).unwrap_or("slot_0").to_owned())),
            is_player: false,
            pet_owner_player_index: None,
            monster_id: Some(*mid),
            current_hp: max_hp,
            max_hp,
            block: 0,
            cumulative_hp_lost: 0,
            is_detached: false,
            monster: Some(Box::new(MonsterInst {
                monster_id: *mid,
                rng: Rng::from_seed(state.run_rngs.seed + 1 + idx as u64),
                is_performing_move: false,
                spawned_this_turn: false,
                current_state_id: Cow::Borrowed(""),
                next_move_id: None,
                next_move_can_transition_away: true,
                performed_first_move: false,
                state_log_ids: Vec::new(),
                transient_move_follow_up_state_id: None,
                setup_fields: setup_fields.clone(),
                private_fields: FieldMap::new(),
            })),
            has_minion_power: false,
            powers: Vec::new(),
        });
        state.enemy_handles.push(eh);
        state.spawned_enemy_handles.push(eh);
        enemy_handles.push(eh);
    }

    (state, enemy_handles)
}

fn default_setup_for(mid: MonsterId) -> (Option<&'static str>, FieldMap) {
    let mut setup = FieldMap::new();
    let slot = match mid {
        MonsterId::PhantasmalGardener => Some("first"),
        _ => Some("slot_0"),
    };
    if mid == MonsterId::Nibbit {
        setup.insert("Nibbit._isAlone".to_owned(), FieldValue::Bool(true));
    }
    (slot, setup)
}

#[test]
fn all_46_act1_monsters_meta_hp_setup_and_5_turn_combat() {
    let content = Sts2Content;
    assert_eq!(ALL_46_ACT1_MONSTERS.len(), 46);

    for &mid in &ALL_46_ACT1_MONSTERS {
        let meta = Sts2Content.monster_meta(mid).expect("meta supported");
        match mid {
            MonsterId::LagavulinMatriarch => {
                assert!(meta.hook_mask.contains(HookSlot::AfterDamageReceived));
            }
            MonsterId::WaterfallGiant => {
                assert!(meta.hook_mask.contains(HookSlot::ShouldDisappearFromDoom));
            }
            _ => assert!(meta.hook_mask.is_empty(), "unexpected hook mask on {mid:?}"),
        }

        let (slot, setup) = default_setup_for(mid);
        let enemy_specs: Vec<(MonsterId, Option<&str>, FieldMap)> =
            if mid == MonsterId::KinFollower {
                vec![
                    (mid, slot, setup),
                    (MonsterId::KinPriest, Some("slot_1"), FieldMap::new()),
                ]
            } else {
                vec![(mid, slot, setup)]
            };
        let (mut state, enemies) =
            build_battle("ACT1_ALL_46", GameMode::Standard, 0, &enemy_specs);
        let eh = enemies[0];
        let (lo, hi) = Sts2Content.monster_initial_hp_range(&state, mid).unwrap();
        assert!(lo <= hi && lo > 0, "invalid hp range on {mid:?}");

        let out = CombatEngine::start_combat(&mut state, &content).unwrap();
        assert_eq!(out, StepOutcome::Completed, "start_combat on {mid:?}");

        for _turn in 0..5 {
            if !state.is_in_progress {
                break;
            }
            if state.creature(eh).is_some_and(|c| c.is_alive()) {
                let _intents = Sts2Content
                    .monster_attack_intent_hits(&state, eh)
                    .unwrap_or_else(|e| panic!("intent failed on {mid:?}: {e:?}"));
            }
            CombatEngine::end_player_turn(&mut state, &content)
                .unwrap_or_else(|e| panic!("end_player_turn failed on {mid:?}: {e:?}"));
        }
        if mid == MonsterId::VineShambler {
            let (mut vstate, venemies) = build_battle(
                "VINE_ROT",
                GameMode::Standard,
                0,
                &[(MonsterId::VineShambler, Some("slot_0"), FieldMap::new())],
            );
            let veh = venemies[0];
            CombatEngine::start_combat(&mut vstate, &content).unwrap();
            CombatEngine::end_player_turn(&mut vstate, &content).unwrap(); // SWIPE -> TENTACLES
            CombatEngine::end_player_turn(&mut vstate, &content).unwrap(); // TENTACLES -> CHOMP
            assert_eq!(
                vstate.creature(veh).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
                Some("CHOMP_MOVE")
            );
            let hp_before = vstate.player.current_hp;
            CombatEngine::end_player_turn(&mut vstate, &content).unwrap(); // CHOMP -> SWIPE
            assert_eq!(vstate.player.current_hp, hp_before - 16);
            assert_eq!(
                vstate.creature(veh).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
                Some("SWIPE_MOVE")
            );
        }
    }
}

#[test]
fn hp_ranges_ascension_and_dread_parity() {
    let (mut state_norm, _) = build_battle("HP", GameMode::Standard, 0, &[]);
    let (mut state_a10, _) = build_battle("HP", GameMode::Standard, 10, &[]);
    let (mut state_dread, _) = build_battle("HP", GameMode::Dread, 10, &[]);
    let _ = (&mut state_norm, &mut state_a10, &mut state_dread);

    assert_eq!(
        act1::monster_initial_hp_range(&state_norm, MonsterId::CeremonialBeast).unwrap(),
        (252, 252)
    );
    assert_eq!(
        act1::monster_initial_hp_range(&state_a10, MonsterId::CeremonialBeast).unwrap(),
        (262, 262)
    );
    assert_eq!(
        act1::monster_initial_hp_range(&state_dread, MonsterId::CeremonialBeast).unwrap(),
        (292, 292)
    );

    assert_eq!(
        act1::monster_initial_hp_range(&state_norm, MonsterId::LagavulinMatriarch).unwrap(),
        (222, 222)
    );
    assert_eq!(
        act1::monster_initial_hp_range(&state_a10, MonsterId::LagavulinMatriarch).unwrap(),
        (233, 233)
    );
    assert_eq!(
        act1::monster_initial_hp_range(&state_dread, MonsterId::LagavulinMatriarch).unwrap(),
        (255, 255)
    );

    assert_eq!(
        act1::monster_initial_hp_range(&state_norm, MonsterId::AssassinRubyRaider).unwrap(),
        (18, 23)
    );
    assert_eq!(
        act1::monster_initial_hp_range(&state_a10, MonsterId::AssassinRubyRaider).unwrap(),
        (19, 24)
    );
    assert_eq!(
        act1::monster_initial_hp_range(&state_norm, MonsterId::WaterfallGiant).unwrap(),
        (240, 240)
    );
    assert_eq!(
        act1::monster_initial_hp_range(&state_a10, MonsterId::WaterfallGiant).unwrap(),
        (250, 250)
    );
}

#[test]
fn waterfall_giant_full_cycle_explosion_and_doom_hook() {
    let content = Sts2Content;
    let (mut state, enemies) = build_battle(
        "WATERFALL_CYCLE",
        GameMode::Standard,
        0,
        &[(MonsterId::WaterfallGiant, Some("slot_0"), FieldMap::new())],
    );
    let wg = enemies[0];
    CombatEngine::start_combat(&mut state, &content).unwrap();

    let expected_moves = [
        "PRESSURIZE_MOVE",
        "STOMP_MOVE",
        "RAM_MOVE",
        "SIPHON_MOVE",
        "PRESSURE_GUN_MOVE",
        "PRESSURE_UP_MOVE",
        "STOMP_MOVE",
    ];
    for (turn, expected) in expected_moves.iter().enumerate() {
        assert_eq!(
            state.creature(wg).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
            Some(*expected),
            "turn {turn}"
        );
        if *expected == "PRESSURE_GUN_MOVE" {
            assert_eq!(
                act1::monster_attack_intent_hits(&state, wg).unwrap().as_slice(),
                &[(Dec::from_i32(20), 1)]
            );
        }
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
    }
    // Pressure gun damage increased from 20 -> 25; SteamEruptionPower = 15 + 6*3 = 33
    assert_eq!(
        state
            .creature(wg)
            .unwrap()
            .monster
            .as_ref()
            .unwrap()
            .private_fields
            .get("WaterfallGiant._currentPressureGunDamage"),
        Some(&FieldValue::Int(25))
    );
    let steam = state
        .creature(wg)
        .unwrap()
        .powers
        .iter()
        .find(|p| p.id == PowerId::SteamEruptionPower)
        .unwrap()
        .amount;
    assert_eq!(steam, 33);

    // ShouldDisappearFromDoom is false while SteamEruptionPower is present
    let disappear = Sts2Content
        .dispatch_hook(
            &mut state,
            HookListener::Monster(wg),
            HookSlot::ShouldDisappearFromDoom,
            &HookArgs::Creature(wg),
        )
        .unwrap()
        .bool_val;
    assert_eq!(disappear, Some(false));

    // Kill WaterfallGiant -> SteamEruptionPower triggers ABOUT_TO_BLOW_MOVE with 999_999_999 HP
    creature_cmd::kill(&mut state, &content, wg, None).unwrap();
    assert!(state.is_in_progress);
    assert_eq!(state.creature(wg).unwrap().current_hp, 999_999_999);
    assert_eq!(
        state.creature(wg).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("ABOUT_TO_BLOW_MOVE")
    );

    // End turn -> performs ABOUT_TO_BLOW_MOVE (captures 33 dmg, removes SteamEruptionPower), rolls EXPLODE_MOVE
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert!(!state.creature(wg).unwrap().powers.iter().any(|p| p.id == PowerId::SteamEruptionPower));
    assert_eq!(
        state.creature(wg).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("EXPLODE_MOVE")
    );
    assert_eq!(
        act1::monster_attack_intent_hits(&state, wg).unwrap().as_slice(),
        &[(Dec::from_i32(33), 1)]
    );

    let disappear_after = Sts2Content
        .dispatch_hook(
            &mut state,
            HookListener::Monster(wg),
            HookSlot::ShouldDisappearFromDoom,
            &HookArgs::Creature(wg),
        )
        .unwrap()
        .bool_val;
    assert_eq!(disappear_after, Some(true));

    // End turn -> EXPLODE_MOVE deals 33 dmg and kills self, winning combat
    let hp_before = state.player.current_hp;
    let res = CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.player.current_hp, hp_before - 33);
    assert_eq!(res, StepOutcome::CombatEnded { won: true });
    assert!(state.creature(wg).unwrap().is_detached);
}

#[test]
fn lagavulin_matriarch_sleep_wake_and_shell_awake_hook() {
    let content = Sts2Content;
    let (mut state, enemies) = build_battle(
        "LAGAVULIN_TEST",
        GameMode::Dread,
        10,
        &[(MonsterId::LagavulinMatriarch, Some("slot_0"), FieldMap::new())],
    );
    let lag = enemies[0];
    CombatEngine::start_combat(&mut state, &content).unwrap();

    assert_eq!(state.creature(lag).unwrap().max_hp, 255);
    assert_eq!(
        state.creature(lag).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("SLEEP_MOVE")
    );
    assert!(state.creature(lag).unwrap().powers.iter().any(|p| p.id == PowerId::PlatingPower && p.amount == 12));
    assert!(state.creature(lag).unwrap().powers.iter().any(|p| p.id == PowerId::AsleepPower && p.amount == 3));

    // Deal unblocked damage (> half HP) -> wakes up into STUNNED, removes PlatingPower & AsleepPower
    let pc = state.player.creature_handle;
    AttackBuilder {
        damage_per_hit: Dec::from_i32(150),
        hit_count: 1,
        attacker: pc,
        card_source: None,
        card_play: None,
        is_monster_source: false,
        props: ValueProp::UNBLOCKABLE | ValueProp::MOVE,
        target_spec: sts2_kernel::commands::damage::AttackTargetSpec::Single(lag),
        hook_type: sts2_kernel::commands::damage::ModifyDamageHookType::ALL,
    }
    .execute(&mut state, &content)
    .unwrap();

    let m = state.creature(lag).unwrap().monster.as_ref().unwrap();
    assert_eq!(m.next_move_id.as_deref(), Some("STUNNED"));
    assert_eq!(m.private_fields.get("LagavulinMatriarch._isAwake"), Some(&FieldValue::Bool(true)));
    assert_eq!(m.private_fields.get("LagavulinMatriarch._isShellAwake"), Some(&FieldValue::Bool(true)));
    assert!(!state.creature(lag).unwrap().powers.iter().any(|p| p.id == PowerId::PlatingPower));

    // Stunned turn -> next turn rolls SLASH_MOVE -> DISEMBOWEL_MOVE -> SLASH2_MOVE -> SOUL_SIPHON_MOVE
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    for expected in ["SLASH_MOVE", "DISEMBOWEL_MOVE", "SLASH2_MOVE", "SOUL_SIPHON_MOVE", "SLASH_MOVE"] {
        assert_eq!(
            state.creature(lag).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
            Some(expected)
        );
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
    }
}

#[test]
fn ceremonial_beast_plow_break_and_second_phase() {
    let content = Sts2Content;
    let (mut state, enemies) = build_battle(
        "BEAST_TEST",
        GameMode::Dread,
        10,
        &[(MonsterId::CeremonialBeast, Some("slot_0"), FieldMap::new())],
    );
    let beast = enemies[0];
    CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(
        state.creature(beast).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("STAMP_MOVE")
    );
    // Turn 1: STAMP_MOVE applies PlowPower(160)
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert!(state.creature(beast).unwrap().powers.iter().any(|p| p.id == PowerId::PlowPower && p.amount == 160));
    assert_eq!(
        state.creature(beast).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("PLOW_MOVE")
    );

    // Drop HP below 160 -> Plow breaks, applies Ritual(2) in Dread, stuns with BEAST_CRY_MOVE follow-up
    let pc = state.player.creature_handle;
    AttackBuilder {
        damage_per_hit: Dec::from_i32(150),
        hit_count: 1,
        attacker: pc,
        card_source: None,
        card_play: None,
        is_monster_source: false,
        props: ValueProp::MOVE,
        target_spec: sts2_kernel::commands::damage::AttackTargetSpec::Single(beast),
        hook_type: sts2_kernel::commands::damage::ModifyDamageHookType::ALL,
    }
    .execute(&mut state, &content)
    .unwrap();
    assert_eq!(
        state.creature(beast).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("STUNNED")
    );
    assert!(state.creature(beast).unwrap().powers.iter().any(|p| p.id == PowerId::RitualPower && p.amount == 2));

    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(
        state.creature(beast).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("BEAST_CRY_MOVE")
    );
    CombatEngine::end_player_turn(&mut state, &content).unwrap(); // BEAST_CRY_MOVE -> STOMP_MOVE
    assert!(state
        .player_creature()
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::RingingPower && p.amount == 1));
    assert_eq!(
        state.creature(beast).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("STOMP_MOVE")
    );
    CombatEngine::end_player_turn(&mut state, &content).unwrap(); // STOMP_MOVE -> CRUSH_MOVE
    assert_eq!(
        state.creature(beast).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("CRUSH_MOVE")
    );
    CombatEngine::end_player_turn(&mut state, &content).unwrap(); // CRUSH_MOVE -> BEAST_CRY_MOVE
    assert_eq!(
        state.creature(beast).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("BEAST_CRY_MOVE")
    );
}

#[test]
fn random_branch_state_rules_and_cooldowns() {
    let content = Sts2Content;
    // Mawler: CLAW_MOVE -> RAND (ROAR_MOVE UseOnlyOnce, RIP_AND_TEAR_MOVE CannotRepeat, CLAW_MOVE CannotRepeat)
    let (mut state, enemies) = build_battle(
        "MAWLER_RNG",
        GameMode::Standard,
        0,
        &[(MonsterId::Mawler, Some("slot_0"), FieldMap::new())],
    );
    let mawler = enemies[0];
    CombatEngine::start_combat(&mut state, &content).unwrap();
    let mut roar_count = 0;
    let mut prev = String::new();
    for _ in 0..10 {
        let mv = state
            .creature(mawler)
            .unwrap()
            .monster
            .as_ref()
            .unwrap()
            .next_move_id
            .clone()
            .unwrap();
        assert_ne!(mv, prev, "Mawler repeated {mv}");
        if mv == "ROAR_MOVE" {
            roar_count += 1;
        }
        prev = mv.into_owned();
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
    }
    assert!(roar_count <= 1, "ROAR_MOVE used {roar_count} times");

    // Flyconid: INITIAL (no FRAIL_SPORES cooldown issue initially, cannot repeat) -> RAND (VULN cd=3, FRAIL cd=2)
    let (mut fstate, fenemies) = build_battle(
        "FLYCONID_RNG",
        GameMode::Standard,
        0,
        &[(MonsterId::Flyconid, Some("slot_0"), FieldMap::new())],
    );
    let fly = fenemies[0];
    CombatEngine::start_combat(&mut fstate, &content).unwrap();
    let log0 = fstate.creature(fly).unwrap().monster.as_ref().unwrap().state_log_ids.clone();
    assert_eq!(log0.len(), 1);
    assert!(matches!(log0[0].as_ref(), "FRAIL_SPORES_MOVE" | "SMASH_MOVE"));
    for _ in 0..8 {
        CombatEngine::end_player_turn(&mut fstate, &content).unwrap();
    }
    let log = &fstate.creature(fly).unwrap().monster.as_ref().unwrap().state_log_ids;
    for (i, mv) in log.iter().enumerate() {
        if i > 0 {
            // Note: when SMASH is at i-1, FRAIL within 2, and VULN within 3, all 3 weights are 0 -> falls back to first branch (VULNERABLE_SPORES_MOVE).
            let all_zero_fallback = mv == "VULNERABLE_SPORES_MOVE"
                && log[i - 1] == "SMASH_MOVE"
                && (1..=2).any(|b| i >= b && log[i - b] == "FRAIL_SPORES_MOVE")
                && (1..=3).any(|b| i >= b && log[i - b] == "VULNERABLE_SPORES_MOVE");
            if !all_zero_fallback {
                assert_ne!(mv, &log[i - 1], "Flyconid repeated {mv} at {i}");
                if mv == "VULNERABLE_SPORES_MOVE" {
                    for back in 1..=3 {
                        if i >= back {
                            assert_ne!(&log[i - back], "VULNERABLE_SPORES_MOVE");
                        }
                    }
                }
                if mv == "FRAIL_SPORES_MOVE" {
                    for back in 1..=2 {
                        if i >= back {
                            assert_ne!(&log[i - back], "FRAIL_SPORES_MOVE");
                        }
                    }
                }
            }
        }
    }
}

#[test]
fn gremlin_merc_steal_and_blitz_and_terror_eel_shriek() {
    let content = Sts2Content;
    // Standard: GremlinMerc steals 20 gold per move and uses ToughEnemies damage
    {
        let (mut state, enemies) = build_battle(
            "GREMLIN_STD",
            GameMode::Standard,
            8, // ToughEnemies active
            &[(MonsterId::GremlinMerc, Some("slot_0"), FieldMap::new())],
        );
        let gm = enemies[0];
        CombatEngine::start_combat(&mut state, &content).unwrap();
        assert_eq!(
            act1::monster_attack_intent_hits(&state, gm).unwrap().as_slice(),
            &[(Dec::from_i32(8), 2)]
        );
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
        assert_eq!(state.player.gold, 80);
    }
    // Blitz: GremlinMerc does NOT steal gold
    {
        let (mut state, _enemies) = build_battle(
            "GREMLIN_BLITZ",
            GameMode::Blitz,
            0,
            &[(MonsterId::GremlinMerc, Some("slot_0"), FieldMap::new())],
        );
        CombatEngine::start_combat(&mut state, &content).unwrap();
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
        assert_eq!(state.player.gold, 100);
    }
    // TerrorEel: ShriekPower breaks at <= 70 HP -> STUNNED -> TERROR_MOVE (Vulnerable 99) -> CRASH_MOVE
    {
        let (mut state, enemies) = build_battle(
            "TERROR_EEL",
            GameMode::Standard,
            0,
            &[(MonsterId::TerrorEel, Some("slot_0"), FieldMap::new())],
        );
        let eel = enemies[0];
        CombatEngine::start_combat(&mut state, &content).unwrap();
        let pc = state.player.creature_handle;
        AttackBuilder {
            damage_per_hit: Dec::from_i32(75),
            hit_count: 1,
            attacker: pc,
            card_source: None,
            card_play: None,
            is_monster_source: false,
            props: ValueProp::MOVE,
            target_spec: sts2_kernel::commands::damage::AttackTargetSpec::Single(eel),
            hook_type: sts2_kernel::commands::damage::ModifyDamageHookType::ALL,
        }
        .execute(&mut state, &content)
        .unwrap();
        assert_eq!(
            state.creature(eel).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
            Some("STUNNED")
        );
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
        assert_eq!(
            state.creature(eel).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
            Some("TERROR_MOVE")
        );
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
        assert!(state
            .player_creature()
            .unwrap()
            .powers
            .iter()
            .any(|p| p.id == PowerId::VulnerablePower && p.amount == 99));
        assert_eq!(
            state.creature(eel).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
            Some("CRASH_MOVE")
        );
    }
}

#[test]
fn two_tailed_rat_summon_and_corpse_slug_ravenous_and_status_generators() {
    let content = Sts2Content;
    // TwoTailedRat: 2 rats in slots "first", "second"; force turnsUntilSummonable=0 -> CallForBackup spawns in "fifth" with unique HP & Niche RNG draw
    {
        let mut s0 = FieldMap::new();
        s0.insert("TwoTailedRat._starterMoveIndex".to_owned(), FieldValue::Int(0));
        s0.insert("TwoTailedRat._turnsUntilSummonable".to_owned(), FieldValue::Int(0));
        let mut s1 = FieldMap::new();
        s1.insert("TwoTailedRat._starterMoveIndex".to_owned(), FieldValue::Int(1));
        let (mut state, enemies) = build_battle(
            "RATS_SUMMON",
            GameMode::Standard,
            0,
            &[
                (MonsterId::TwoTailedRat, Some("first"), s0),
                (MonsterId::TwoTailedRat, Some("second"), s1),
            ],
        );
        state.static_ctx = Arc::new(RootStaticContext {
            encounter_slots: vec![
                "first".to_owned(),
                "second".to_owned(),
                "third".to_owned(),
                "fourth".to_owned(),
                "fifth".to_owned(),
            ],
            ..(*state.static_ctx).clone()
        });
        CombatEngine::start_combat(&mut state, &content).unwrap();
        let niche_before = state.run_rngs.get(sts2_types::RunRngType::Niche).counter;
        // Force rat[0] to CALL_FOR_BACKUP_MOVE to verify spawn + unique HP + Niche RNG
        state.creature_mut(enemies[0]).unwrap().monster.as_mut().unwrap().next_move_id =
            Some(Cow::Borrowed("CALL_FOR_BACKUP_MOVE"));
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
        assert_eq!(state.enemy_handles.len(), 3);
        assert!(state.run_rngs.get(sts2_types::RunRngType::Niche).counter > niche_before);
        let spawned = state
            .enemy_handles
            .iter()
            .copied()
            .find(|h| !enemies.contains(h))
            .unwrap();
        let spawned_hp = state.creature(spawned).unwrap().max_hp;
        assert!((17..=21).contains(&spawned_hp));
        assert_ne!(spawned_hp, 21); // 21 is already used by existing rats
    }

    // CorpseSlug: ally death triggers RavenousPower -> STUNNED -> end_player_turn executes RavenousStunned
    {
        let mut s0 = FieldMap::new();
        s0.insert("CorpseSlug._starterMoveIdx".to_owned(), FieldValue::Int(0));
        let mut s1 = FieldMap::new();
        s1.insert("CorpseSlug._starterMoveIdx".to_owned(), FieldValue::Int(1));
        let (mut state, enemies) = build_battle(
            "SLUGS_RAV",
            GameMode::Standard,
            0,
            &[
                (MonsterId::CorpseSlug, Some("slot_0"), s0),
                (MonsterId::CorpseSlug, Some("slot_1"), s1),
            ],
        );
        CombatEngine::start_combat(&mut state, &content).unwrap();
        creature_cmd::kill(&mut state, &content, enemies[0], None).unwrap();
        assert_eq!(
            state.creature(enemies[1]).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
            Some("STUNNED")
        );
        assert!(state
            .creature(enemies[1])
            .unwrap()
            .powers
            .iter()
            .any(|p| p.id == PowerId::StrengthPower && p.amount == 4));
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
        assert_eq!(
            state
                .creature(enemies[1])
                .unwrap()
                .monster
                .as_ref()
                .unwrap()
                .private_fields
                .get("CorpseSlug._isRavenous"),
            Some(&FieldValue::Bool(false))
        );
    }

    // SoulFysh (Dread): BECKON_MOVE adds Beckon to Draw, Hand, and Discard
    {
        let (mut state, enemies) = build_battle(
            "SOUL_FYSH_DREAD",
            GameMode::Dread,
            0,
            &[(MonsterId::SoulFysh, Some("slot_0"), FieldMap::new())],
        );
        let sf = enemies[0];
        CombatEngine::start_combat(&mut state, &content).unwrap();
        assert_eq!(
            state.creature(sf).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
            Some("BECKON_MOVE")
        );
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
        let beckon_count = state
            .cards
            .iter()
            .filter(|c| c.id == CardId::Beckon && c.pile != PileType::None)
            .count();
        assert_eq!(beckon_count, 3);
    }

    // Vantom: Dismember skips Wounds when lethal
    {
        let (mut state, enemies) = build_battle(
            "VANTOM_DREAD",
            GameMode::Dread,
            10,
            &[(MonsterId::Vantom, Some("slot_0"), FieldMap::new())],
        );
        let v = enemies[0];
        CombatEngine::start_combat(&mut state, &content).unwrap();
        assert!(state
            .creature(v)
            .unwrap()
            .powers
            .iter()
            .any(|p| p.id == PowerId::SlipperyPower && p.amount == 11));
        CombatEngine::end_player_turn(&mut state, &content).unwrap(); // INK_BLOT
        CombatEngine::end_player_turn(&mut state, &content).unwrap(); // INKY_LANCE
        CombatEngine::end_player_turn(&mut state, &content).unwrap(); // DISMEMBER
        assert_eq!(
            state.cards.iter().filter(|c| c.id == CardId::Wound).count(),
            3
        );
        CombatEngine::end_player_turn(&mut state, &content).unwrap(); // PREPARE (+3 str in Dread)
        assert!(state
            .creature(v)
            .unwrap()
            .powers
            .iter()
            .any(|p| p.id == PowerId::StrengthPower && p.amount == 3));
    }
}

#[test]
fn stunned_move_callbacks_end_to_end() {
    let content = Sts2Content;

    // LagavulinMatriarch STUNNED via CombatEngine::end_player_turn -> WakeUpMove sets _isAwake = true
    {
        let (mut state, enemies) = build_battle(
            "STUN_LAG",
            GameMode::Standard,
            0,
            &[(MonsterId::LagavulinMatriarch, Some("slot_0"), FieldMap::new())],
        );
        let lag = enemies[0];
        CombatEngine::start_combat(&mut state, &content).unwrap();
        {
            let m = state.creature_mut(lag).unwrap().monster.as_mut().unwrap();
            m.current_state_id = Cow::Borrowed("STUNNED");
            m.next_move_id = Some(Cow::Borrowed("STUNNED"));
            m.transient_move_follow_up_state_id = Some(Cow::Borrowed("SLASH_MOVE"));
            m.private_fields
                .insert("LagavulinMatriarch._isAwake".to_owned(), FieldValue::Bool(false));
        }
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
        assert_eq!(
            state
                .creature(lag)
                .unwrap()
                .monster
                .as_ref()
                .unwrap()
                .private_fields
                .get("LagavulinMatriarch._isAwake"),
            Some(&FieldValue::Bool(true))
        );
    }

    // CorpseSlug STUNNED via CombatEngine::end_player_turn -> RavenousStunned sets _isRavenous = false
    {
        let (mut state, enemies) = build_battle(
            "STUN_SLUG",
            GameMode::Standard,
            0,
            &[(MonsterId::CorpseSlug, Some("slot_0"), FieldMap::new())],
        );
        let slug = enemies[0];
        CombatEngine::start_combat(&mut state, &content).unwrap();
        {
            let m = state.creature_mut(slug).unwrap().monster.as_mut().unwrap();
            m.current_state_id = Cow::Borrowed("STUNNED");
            m.next_move_id = Some(Cow::Borrowed("STUNNED"));
            m.transient_move_follow_up_state_id = Some(Cow::Borrowed("WHIP_SLAP_MOVE"));
            m.private_fields
                .insert("CorpseSlug._isRavenous".to_owned(), FieldValue::Bool(true));
        }
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
        assert_eq!(
            state
                .creature(slug)
                .unwrap()
                .monster
                .as_ref()
                .unwrap()
                .private_fields
                .get("CorpseSlug._isRavenous"),
            Some(&FieldValue::Bool(false))
        );
    }

    // CeremonialBeast STUNNED via CombatEngine::end_player_turn -> StunnedMove sets _isStunnedByPlowRemoval = false
    {
        let (mut state, enemies) = build_battle(
            "STUN_BEAST",
            GameMode::Standard,
            0,
            &[(MonsterId::CeremonialBeast, Some("slot_0"), FieldMap::new())],
        );
        let beast = enemies[0];
        CombatEngine::start_combat(&mut state, &content).unwrap();
        {
            let m = state.creature_mut(beast).unwrap().monster.as_mut().unwrap();
            m.current_state_id = Cow::Borrowed("STUNNED");
            m.next_move_id = Some(Cow::Borrowed("STUNNED"));
            m.transient_move_follow_up_state_id = Some(Cow::Borrowed("BEAST_CRY_MOVE"));
            m.private_fields.insert(
                "CeremonialBeast._isStunnedByPlowRemoval".to_owned(),
                FieldValue::Bool(true),
            );
        }
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
        assert_eq!(
            state
                .creature(beast)
                .unwrap()
                .monster
                .as_ref()
                .unwrap()
                .private_fields
                .get("CeremonialBeast._isStunnedByPlowRemoval"),
            Some(&FieldValue::Bool(false))
        );
    }
}
