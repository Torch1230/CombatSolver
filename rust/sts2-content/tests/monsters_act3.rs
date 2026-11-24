use smallvec::SmallVec;
use std::borrow::Cow;
use std::sync::Arc;
use sts2_content::Sts2Content;
use sts2_kernel::commands::{creature, damage, power};
use sts2_kernel::{
    BattleState, CardHandle, CardInst, CardTempFlags, CombatEngine, ContentDispatcher,
    CreatureHandle, CreatureInst, FieldValue, MonsterInst, PlayerCounters, PlayerInst, RelicInst,
    RootStaticContext, SemanticHistory, StepOutcome,
};
use sts2_types::{
    CardId, CardKeywords, CharacterId, CombatSide, Dec, GameMode, HookMask, HookSlot, MonsterId,
    PileType, PlayerRngSet, PlayerTurnPhase, PowerId, RelicId, Rng, RoomType, RunRngSet,
    ValueProp,
};

const ALL_46_ACT3_SHARED: [MonsterId; 46] = [
    MonsterId::ActOneGuardian,
    MonsterId::Aeonglass,
    MonsterId::Axebot,
    MonsterId::BattleFriendV1,
    MonsterId::BattleFriendV2,
    MonsterId::BattleFriendV3,
    MonsterId::ByrdineMonster,
    MonsterId::Byrdpip,
    MonsterId::DevotedSculptor,
    MonsterId::EyeWithTeeth,
    MonsterId::Fabricator,
    MonsterId::FakeMerchantMonster,
    MonsterId::FatGremlin,
    MonsterId::FlailKnight,
    MonsterId::FrogKnight,
    MonsterId::GasBomb,
    MonsterId::GlobeHead,
    MonsterId::Guardbot,
    MonsterId::HardyBrute,
    MonsterId::LivingShield,
    MonsterId::MagiKnight,
    MonsterId::MechaKnight,
    MonsterId::MysteriousKnight,
    MonsterId::Noisebot,
    MonsterId::Osty,
    MonsterId::OwlMagistrate,
    MonsterId::PaelsLegion,
    MonsterId::Parafright,
    MonsterId::Queen,
    MonsterId::ScrollOfBiting,
    MonsterId::SlimedBerserker,
    MonsterId::SneakyGremlin,
    MonsterId::SoulNexus,
    MonsterId::SpectralKnight,
    MonsterId::Stabbot,
    MonsterId::TestSubject,
    MonsterId::TheForgotten,
    MonsterId::TheLost,
    MonsterId::TorchHeadAmalgam,
    MonsterId::ToughEgg,
    MonsterId::TrainingDummy,
    MonsterId::TurretOperator,
    MonsterId::WanderingGrunt,
    MonsterId::Wriggler,
    MonsterId::Wuppy,
    MonsterId::Zapbot,
];

fn build_battle(
    enemies: &[(MonsterId, Option<&str>)],
    ascension: u8,
    game_mode: GameMode,
    relics: &[RelicId],
    encounter_slots: &[&str],
) -> (BattleState, Vec<CreatureHandle>) {
    let content = Sts2Content;
    let run_rngs = RunRngSet::from_string_seed("ACT3TEST");
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc_handle = CreatureHandle(1);

    let deck_ids = [
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
    ];
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
            canonical_base_energy_cost: meta.canonical_energy_cost,
            base_energy_cost: meta.canonical_energy_cost,
            captured_energy_x_value: 0,
            base_replay_count: 0,
            floor_added_to_deck: Some(1),
            deck_card_handle: Some(h),
            clone_of_handle: None,
            temp_star_cost_override_this_turn: None,
            temp_cost_override_this_turn: None,
            temp_cost_override_this_turn_or_until_played: None,
            temp_cost_override_this_combat: None,
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
        current_hp: 999,
        max_hp: 999,
        block: 0,
        cumulative_hp_lost: 0,
        is_detached: false,
        monster: None,
        has_minion_power: false,
        powers: Vec::new(),
    };

    let player = PlayerInst {
        creature_handle: pc_handle,
        current_hp: 999,
        max_hp: 999,
        block: 0,
        cumulative_hp_lost: 0,
        gold: 99,
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

    let mut state = BattleState {
        static_ctx: Arc::new(RootStaticContext {
            encounter_id: "TEST_ENCOUNTER".to_owned(),
            encounter_is_weak: false,
            room_type: RoomType::Monster,
            encounter_slots: encounter_slots.iter().map(|s| (*s).to_owned()).collect(),
            game_mode,
            ascension,
            character_id: CharacterId::Ironclad,
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
            act_index: 2,
            total_floor: 40,
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
        next_creature_id: 1,
        initial_encounter_monster_count: enemies.len(),
        next_card_handle: (cards.len() + 1) as u32,
        next_creature_handle: 2,
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
    };

    let mut enemy_handles = Vec::new();
    for (idx, &(mid, slot)) in enemies.iter().enumerate() {
        let combat_id = state.next_creature_id;
        state.next_creature_id += 1;
        let handle = CreatureHandle(state.next_creature_handle);
        state.next_creature_handle += 1;
        let (_min_hp, max_hp) = content.monster_initial_hp_range(&state, mid).unwrap();
        state.creatures.push(CreatureInst {
            handle,
            combat_id: Some(combat_id),
            side: CombatSide::Enemy,
            slot_name: slot.map(|s| Cow::Owned(s.to_owned())),
            is_player: false,
            pet_owner_player_index: None,
            monster_id: Some(mid),
            current_hp: max_hp,
            max_hp,
            block: 0,
            cumulative_hp_lost: 0,
            is_detached: false,
            monster: Some(MonsterInst {
                monster_id: mid,
                rng: Rng::from_seed(state.run_rngs.seed + (idx as u64) + 1),
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
            }),
            has_minion_power: false,
            powers: Vec::new(),
        });
        state.enemy_handles.push(handle);
        state.spawned_enemy_handles.push(handle);
        enemy_handles.push(handle);
    }

    (state, enemy_handles)
}

#[test]
fn all_46_act3_shared_monsters_meta_hp_setup_roll_and_intent_work() {
    let content = Sts2Content;
    assert_eq!(ALL_46_ACT3_SHARED.len(), 46);

    for &mid in &ALL_46_ACT3_SHARED {
        let meta = content.monster_meta(mid).unwrap();
        match mid {
            MonsterId::Aeonglass => {
                assert!(meta.hook_mask.contains(HookSlot::AfterCardGeneratedForCombat));
            }
            MonsterId::BattleFriendV1 | MonsterId::BattleFriendV2 | MonsterId::BattleFriendV3 => {
                assert!(meta.hook_mask.contains(HookSlot::AfterSideTurnEnd));
            }
            MonsterId::Queen => {
                assert!(meta.hook_mask.contains(HookSlot::AfterDeath));
            }
            MonsterId::TestSubject => {
                assert!(meta.hook_mask.contains(HookSlot::ShouldDisappearFromDoom));
            }
            _ => assert_eq!(meta.hook_mask, HookMask::EMPTY),
        }

        let slot = if mid == MonsterId::Wriggler {
            Some("wriggler1")
        } else {
            Some("slot_0")
        };
        let is_secondary = matches!(
            mid,
            MonsterId::EyeWithTeeth
                | MonsterId::GasBomb
                | MonsterId::Parafright
                | MonsterId::TorchHeadAmalgam
        );
        let (mut state_a0, enemies_a0) = if is_secondary {
            build_battle(
                &[(mid, slot), (MonsterId::TrainingDummy, Some("slot_1"))],
                0,
                GameMode::Standard,
                &[],
                &["slot_0", "slot_1"],
            )
        } else {
            build_battle(&[(mid, slot)], 0, GameMode::Standard, &[], &[])
        };
        let (min0, max0) = content.monster_initial_hp_range(&state_a0, mid).unwrap();
        assert!(min0 <= max0 && min0 >= 1, "{mid:?} invalid A0 hp range");

        let (state_a8, _) = build_battle(&[(mid, slot)], 8, GameMode::Standard, &[], &[]);
        let (min8, max8) = content.monster_initial_hp_range(&state_a8, mid).unwrap();
        assert!(min8 >= min0 && max8 >= max0, "{mid:?} A8 hp should be >= A0");

        let eh = enemies_a0[0];
        let outcome = CombatEngine::start_combat(&mut state_a0, &content).unwrap();
        assert_eq!(outcome, StepOutcome::Completed, "{mid:?} start_combat");
        let _intents = content
            .monster_attack_intent_hits(&state_a0, eh)
            .unwrap();
    }
}

#[test]
fn osty_and_all_pet_monsters_parity() {
    let content = Sts2Content;
    for &(mid, expected_hp) in &[
        (MonsterId::Osty, 1),
        (MonsterId::ByrdineMonster, 9999),
        (MonsterId::Byrdpip, 9999),
        (MonsterId::PaelsLegion, 9999),
        (MonsterId::Wuppy, 50),
    ] {
        let (mut state, enemies) = build_battle(&[(mid, None)], 0, GameMode::Standard, &[], &[]);
        assert_eq!(
            content.monster_initial_hp_range(&state, mid).unwrap(),
            (expected_hp, expected_hp)
        );
        CombatEngine::start_combat(&mut state, &content).unwrap();
        let eh = enemies[0];
        assert_eq!(
            state.creature(eh).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
            Some("NOTHING_MOVE")
        );
        assert!(content.monster_attack_intent_hits(&state, eh).unwrap().is_empty());
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
        assert_eq!(state.player.current_hp, 999);
    }

    // Also verify Osty summoned as player pet via Necrobinder starter flow.
    let (mut state, _) = build_battle(
        &[(MonsterId::TrainingDummy, None)],
        0,
        GameMode::Standard,
        &[RelicId::BoundPhylactery],
        &[],
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();
    let osty = state.osty_handle().expect("Osty spawned as pet");
    let osty_c = state.creature(osty).unwrap();
    assert_eq!(osty_c.side, CombatSide::Player);
    assert!(osty_c.is_pet());
    assert!(!osty_c.is_secondary_enemy());
    assert_eq!(osty_c.current_hp, 1);
    assert_eq!(
        content.monster_meta(MonsterId::Osty).unwrap().hook_mask,
        HookMask::EMPTY
    );
    assert!(content.monster_attack_intent_hits(&state, osty).unwrap().is_empty());
}

#[test]
fn queen_and_torch_head_amalgam_lifecycle_and_dread() {
    let content = Sts2Content;
    let (mut state, enemies) = build_battle(
        &[
            (MonsterId::TorchHeadAmalgam, Some("amalgam")),
            (MonsterId::Queen, Some("queen")),
        ],
        9,
        GameMode::Standard,
        &[],
        &["amalgam", "queen"],
    );
    let amalgam = enemies[0];
    let queen = enemies[1];
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // Queen starts PUPPET_STRINGS_MOVE; Amalgam starts STRONG_TACKLE_MOVE (32 on A9) + MinionPower.
    assert_eq!(state.creature(queen).unwrap().max_hp, 419);
    assert_eq!(state.creature(amalgam).unwrap().max_hp, 211);
    assert!(state.creature(amalgam).unwrap().is_secondary_enemy());
    assert_eq!(
        state.creature(queen).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("PUPPET_STRINGS_MOVE")
    );
    assert_eq!(
        content.monster_attack_intent_hits(&state, amalgam).unwrap().as_slice(),
        &[(Dec::from_i32(32), 1)]
    );

    // Turn 1 -> Queen applies ChainsOfBinding(3); Amalgam hits 32.
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.player.current_hp, 999 - 32);
    assert!(state
        .player_creature()
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::ChainsOfBindingPower && p.amount == 3));
    assert_eq!(
        state.creature(queen).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("YOU_ARE_MINE_MOVE")
    );
    assert!(
        state
            .player
            .hand
            .iter()
            .any(|&h| state.card(h).is_some_and(|c| c.affliction.is_some()))
    );
    // Turn 2 -> Queen applies Frail/Weak/Vulnerable 99 and ChainsOfBinding clears Bound; Amalgam TACKLE_2_MOVE (22).
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.player.current_hp, 999 - 32 - 22);
    assert_eq!(
        state.creature(queen).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("BURN_BRIGHT_FOR_ME_MOVE")
    );

    // Kill Amalgam mid-player-turn while Queen intends BURN_BRIGHT_FOR_ME_MOVE -> Queen switches to ENRAGE_MOVE!
    creature::kill(&mut state, &content, amalgam, None).unwrap();
    assert_eq!(
        state.creature(queen).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("ENRAGE_MOVE")
    );

    // Turn 3 -> Queen performs ENRAGE_MOVE (+2 Str), rolls OFF_WITH_YOUR_HEAD_MOVE (4 x 5).
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert!(state
        .creature(queen)
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::StrengthPower && p.amount == 2));
    assert_eq!(
        state.creature(queen).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("OFF_WITH_YOUR_HEAD_MOVE")
    );
    assert_eq!(
        content.monster_attack_intent_hits(&state, queen).unwrap().as_slice(),
        &[(Dec::from_i32(4), 5)]
    );

    // Dread mode Queen starts in DREAD_INITIAL_MOVE and Amalgam gets +5 on STRONG_TACKLE_MOVE.
    let (mut dread_state, d_enemies) = build_battle(
        &[
            (MonsterId::TorchHeadAmalgam, Some("amalgam")),
            (MonsterId::Queen, Some("queen")),
        ],
        0,
        GameMode::Dread,
        &[],
        &["amalgam", "queen"],
    );
    CombatEngine::start_combat(&mut dread_state, &content).unwrap();
    assert_eq!(
        dread_state
            .creature(d_enemies[1])
            .unwrap()
            .monster
            .as_ref()
            .unwrap()
            .next_move_id
            .as_deref(),
        Some("DREAD_INITIAL_MOVE")
    );
    assert_eq!(
        content
            .monster_attack_intent_hits(&dread_state, d_enemies[0])
            .unwrap()
            .as_slice(),
        &[(Dec::from_i32(31), 1)]
    );
}

#[test]
fn test_subject_three_form_lifecycle_and_doom_hook() {
    let content = Sts2Content;
    let (mut state, enemies) = build_battle(
        &[(MonsterId::TestSubject, Some("slot_0"))],
        0,
        GameMode::Standard,
        &[],
        &["slot_0"],
    );
    let ts = enemies[0];
    CombatEngine::start_combat(&mut state, &content).unwrap();

    assert_eq!(state.creature(ts).unwrap().max_hp, 100);
    assert!(state
        .creature(ts)
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::AdaptablePower));
    assert!(!sts2_kernel::hooks::should_disappear_from_doom(&mut state, &content, ts).unwrap());

    // Kill Form 1 -> AdaptablePower prevents removal and queues RESPAWN_MOVE.
    creature::kill(&mut state, &content, ts, None).unwrap();
    assert_eq!(state.creature(ts).unwrap().current_hp, 0);
    assert!(state.enemy_handles.contains(&ts));
    assert_eq!(
        state.creature(ts).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("RESPAWN_MOVE")
    );
    assert!(!CombatEngine::check_win_condition(&mut state, &content).unwrap());

    // Enemy turn executes RESPAWN_MOVE -> 200 HP + PainfulStabs, then rolls MULTI_CLAW_MOVE (10x3).
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.creature(ts).unwrap().max_hp, 200);
    assert_eq!(state.creature(ts).unwrap().current_hp, 200);
    assert!(state
        .creature(ts)
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::PainfulStabsPower));
    assert_eq!(
        state.creature(ts).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("MULTI_CLAW_MOVE")
    );
    assert_eq!(
        content.monster_attack_intent_hits(&state, ts).unwrap().as_slice(),
        &[(Dec::from_i32(10), 3)]
    );

    // Execute MULTI_CLAW_MOVE -> deals 30, next intent scales to 4 hits.
    let hp_before_multi_claw = state.player.current_hp;
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(hp_before_multi_claw - state.player.current_hp, 30);
    assert_eq!(
        content.monster_attack_intent_hits(&state, ts).unwrap().as_slice(),
        &[(Dec::from_i32(10), 4)]
    );

    // Kill Form 2 -> RESPAWN_MOVE -> 300 HP + NemesisPower, removes Adaptable & PainfulStabs.
    creature::kill(&mut state, &content, ts, None).unwrap();
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.creature(ts).unwrap().max_hp, 300);
    assert_eq!(state.creature(ts).unwrap().current_hp, 300);
    assert!(state
        .creature(ts)
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::NemesisPower));
    assert!(!state
        .creature(ts)
        .unwrap()
        .powers
        .iter()
        .any(|p| matches!(p.id, PowerId::AdaptablePower | PowerId::PainfulStabsPower)));
    assert_eq!(
        state.creature(ts).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("PHASE3_LACERATE_MOVE")
    );
    assert!(sts2_kernel::hooks::should_disappear_from_doom(&mut state, &content, ts).unwrap());

    // Execute Lacerate -> Big Pounce -> Burning Growl (3 Burns into Discard + 2 Str).
    CombatEngine::end_player_turn(&mut state, &content).unwrap(); // Lacerate
    CombatEngine::end_player_turn(&mut state, &content).unwrap(); // Big Pounce (45)
    CombatEngine::end_player_turn(&mut state, &content).unwrap(); // Burning Growl
    let burn_count = state
        .cards
        .iter()
        .filter(|c| c.id == CardId::Burn && c.pile != PileType::None)
        .count();
    assert_eq!(burn_count, 3);
}

#[test]
fn fabricator_spawns_bots_and_guardbot_blocks() {
    let content = Sts2Content;
    let (mut state, enemies) = build_battle(
        &[(MonsterId::Fabricator, Some("fabricator"))],
        0,
        GameMode::Standard,
        &[],
        &["bot1", "bot2", "fabricator", "bot3", "bot4"],
    );
    let fab = enemies[0];
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // Turn 1..3: Fabricator spawns bots up to 4 living enemies total, then switches to DISINTEGRATE_MOVE.
    for _ in 0..4 {
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
    }
    assert!(state.enemy_handles.len() >= 2);
    assert!(state.enemy_handles.iter().any(|&h| {
        h != fab && state.creature(h).unwrap().is_secondary_enemy()
    }));

    // Verify Guardbot grants 15 unpowered block to Fabricator.
    let (mut gb_state, gb_enemies) = build_battle(
        &[
            (MonsterId::Fabricator, Some("fabricator")),
            (MonsterId::Guardbot, Some("bot1")),
        ],
        0,
        GameMode::Standard,
        &[],
        &["bot1", "fabricator"],
    );
    CombatEngine::start_combat(&mut gb_state, &content).unwrap();
    CombatEngine::end_player_turn(&mut gb_state, &content).unwrap();
    assert_eq!(gb_state.creature(gb_enemies[0]).unwrap().block, 15);
}

#[test]
fn aeonglass_wither_scaling_and_after_card_generated_hook() {
    let content = Sts2Content;
    let (mut state, enemies) =
        build_battle(&[(MonsterId::Aeonglass, None)], 0, GameMode::Standard, &[], &[]);
    let ag = enemies[0];
    CombatEngine::start_combat(&mut state, &content).unwrap();

    assert!(state
        .creature(ag)
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::WitheringPresencePower && p.amount == 6));
    assert!(state
        .creature(ag)
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::ArtifactPower && p.amount == 3));

    // Turn 1: EBB_MOVE (22 dmg, 33 block); Turn 2: EYE_LASERS_MOVE (11x2); Turn 3: INCREASING_INTENSITY_MOVE.
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.player.current_hp, 999 - 22);
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.player.current_hp, 999 - 22 - 22);
    CombatEngine::end_player_turn(&mut state, &content).unwrap();

    let wither_cards: Vec<_> = state
        .cards
        .iter()
        .filter(|c| c.id == CardId::Wither && c.pile != PileType::None)
        .collect();
    assert_eq!(wither_cards.len(), 1);
    let ext = state.card_ext(wither_cards[0].ext_index).unwrap();
    assert_eq!(
        ext.private_fields.get("Wither._fakeUpgradeLevel"),
        Some(&FieldValue::Int(1))
    );
    assert!(state
        .creature(ag)
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::StrengthPower && p.amount == 3));
}

#[test]
fn axebot_stock_respawn_and_battle_friend_escape_and_gas_bomb() {
    let content = Sts2Content;

    // Axebot + StockPower respawn chain
    let (mut axe_state, axe_enemies) =
        build_battle(&[(MonsterId::Axebot, Some("front"))], 0, GameMode::Standard, &[], &["front"]);
    let axe0 = axe_enemies[0];
    CombatEngine::start_combat(&mut axe_state, &content).unwrap();
    assert_eq!(
        axe_state
            .creature(axe0)
            .unwrap()
            .powers
            .iter()
            .find(|p| p.id == PowerId::StockPower)
            .unwrap()
            .amount,
        2
    );
    assert_eq!(
        axe_state.creature(axe0).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("HAMMER_UPPERCUT_MOVE")
    );
    creature::kill(&mut axe_state, &content, axe0, None).unwrap();
    assert_eq!(axe_state.enemy_handles.len(), 1);
    let axe1 = axe_state.enemy_handles[0];
    assert_ne!(axe1, axe0);
    assert_eq!(axe_state.creature(axe1).unwrap().max_hp, 80);
    assert_eq!(
        axe_state.creature(axe1).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("BOOT_UP_MOVE")
    );
    assert_eq!(
        axe_state
            .creature(axe1)
            .unwrap()
            .powers
            .iter()
            .find(|p| p.id == PowerId::StockPower)
            .unwrap()
            .amount,
        1
    );

    // BattleFriendV1 escapes after 3 turns
    let (mut bf_state, bf_enemies) =
        build_battle(&[(MonsterId::BattleFriendV1, None)], 0, GameMode::Standard, &[], &[]);
    let bf = bf_enemies[0];
    CombatEngine::start_combat(&mut bf_state, &content).unwrap();
    for _ in 0..3 {
        CombatEngine::end_player_turn(&mut bf_state, &content).unwrap();
    }
    assert!(bf_state.escaped_handles.contains(&bf));
    assert!(bf_state.won);

    // GasBomb self-destructs and removes itself on EXPLODE_MOVE
    let (mut gb_state, gb_enemies) = build_battle(
        &[
            (MonsterId::TrainingDummy, Some("slot_0")),
            (MonsterId::GasBomb, Some("slot_1")),
        ],
        0,
        GameMode::Standard,
        &[],
        &["slot_0", "slot_1"],
    );
    let gas = gb_enemies[1];
    CombatEngine::start_combat(&mut gb_state, &content).unwrap();
    CombatEngine::end_player_turn(&mut gb_state, &content).unwrap();
    assert_eq!(gb_state.player.current_hp, 999 - 8 - 8);
    assert!(gb_state.removed_handles.contains(&gas));
    assert!(!gb_state.enemy_handles.contains(&gas));
}

#[test]
fn remaining_special_mechanics_knights_lost_forgotten_egg_owl_frog_shield_wriggler() {
    let content = Sts2Content;

    // TheLost + TheForgotten dynamic Dread damage & Possess powers
    let (mut lf_state, lf_enemies) = build_battle(
        &[(MonsterId::TheLost, None), (MonsterId::TheForgotten, None)],
        0,
        GameMode::Standard,
        &[],
        &[],
    );
    CombatEngine::start_combat(&mut lf_state, &content).unwrap();
    CombatEngine::end_player_turn(&mut lf_state, &content).unwrap();
    assert_eq!(
        content
            .monster_attack_intent_hits(&lf_state, lf_enemies[1])
            .unwrap()
            .as_slice(),
        &[(Dec::from_i32(15), 1)]
    );

    // MagiKnight DampenPower & SpectralKnight HexPower
    let (mut mk_state, _mk_enemies) = build_battle(
        &[(MonsterId::MagiKnight, None), (MonsterId::SpectralKnight, None)],
        0,
        GameMode::Standard,
        &[],
        &[],
    );
    CombatEngine::start_combat(&mut mk_state, &content).unwrap();
    CombatEngine::end_player_turn(&mut mk_state, &content).unwrap(); // POWER_SHIELD + HEX
    assert!(mk_state
        .player_creature()
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::HexPower && p.amount == 2));
    CombatEngine::end_player_turn(&mut mk_state, &content).unwrap(); // DAMPEN_MOVE
    assert!(mk_state
        .player_creature()
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::DampenPower));

    // FrogKnight HALF_HEALTH -> BEETLE_CHARGE once
    let (mut fk_state, fk_enemies) =
        build_battle(&[(MonsterId::FrogKnight, None)], 0, GameMode::Standard, &[], &[]);
    let fk = fk_enemies[0];
    CombatEngine::start_combat(&mut fk_state, &content).unwrap();
    CombatEngine::end_player_turn(&mut fk_state, &content).unwrap(); // TONGUE_LASH
    CombatEngine::end_player_turn(&mut fk_state, &content).unwrap(); // STRIKE_DOWN_EVIL
    let dmg_props = ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED;
    damage::damage_targets(&mut fk_state, &content, &[fk], Dec::from_i32(120), dmg_props, None, None, None).unwrap();
    CombatEngine::end_player_turn(&mut fk_state, &content).unwrap(); // FOR_THE_QUEEN -> HALF_HEALTH -> BEETLE_CHARGE
    assert_eq!(
        fk_state.creature(fk).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("BEETLE_CHARGE")
    );

    // ToughEgg hatches and clears non-Minion/non-Impaled powers
    let (mut egg_state, egg_enemies) =
        build_battle(&[(MonsterId::ToughEgg, None)], 0, GameMode::Standard, &[], &[]);
    let egg = egg_enemies[0];
    CombatEngine::start_combat(&mut egg_state, &content).unwrap();
    power::apply(
        &mut egg_state,
        &content,
        PowerId::StrengthPower,
        egg,
        Dec::from_i32(4),
        Some(egg),
        None,
    )
    .unwrap();
    CombatEngine::end_player_turn(&mut egg_state, &content).unwrap();
    assert!(egg_state.creature(egg).unwrap().powers.is_empty());
    assert!(egg_state.creature(egg).unwrap().max_hp >= 19);
    assert_eq!(
        egg_state.creature(egg).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("NIBBLE_MOVE")
    );

    // OwlMagistrate gains SoarPower on JUDICIAL_FLIGHT and removes it on VERDICT
    let (mut owl_state, owl_enemies) =
        build_battle(&[(MonsterId::OwlMagistrate, None)], 0, GameMode::Standard, &[], &[]);
    let owl = owl_enemies[0];
    CombatEngine::start_combat(&mut owl_state, &content).unwrap();
    for _ in 0..3 {
        CombatEngine::end_player_turn(&mut owl_state, &content).unwrap();
    }
    assert!(owl_state
        .creature(owl)
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::SoarPower));
    CombatEngine::end_player_turn(&mut owl_state, &content).unwrap();
    assert!(!owl_state
        .creature(owl)
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::SoarPower));

    // LivingShield switches to SMASH_MOVE when alone
    let (mut ls_state, ls_enemies) = build_battle(
        &[(MonsterId::LivingShield, None), (MonsterId::TurretOperator, None)],
        0,
        GameMode::Standard,
        &[],
        &[],
    );
    CombatEngine::start_combat(&mut ls_state, &content).unwrap();
    creature::kill(&mut ls_state, &content, ls_enemies[1], None).unwrap();
    CombatEngine::end_player_turn(&mut ls_state, &content).unwrap();
    assert_eq!(
        ls_state
            .creature(ls_enemies[0])
            .unwrap()
            .monster
            .as_ref()
            .unwrap()
            .next_move_id
            .as_deref(),
        Some("SMASH_MOVE")
    );

    // EyeWithTeeth IllusionPower revive cycle
    let (mut eye_state, eye_enemies) = build_battle(
        &[
            (MonsterId::TrainingDummy, Some("slot_0")),
            (MonsterId::EyeWithTeeth, Some("slot_1")),
        ],
        0,
        GameMode::Standard,
        &[],
        &["slot_0", "slot_1"],
    );
    let eye = eye_enemies[1];
    CombatEngine::start_combat(&mut eye_state, &content).unwrap();
    creature::kill(&mut eye_state, &content, eye, None).unwrap();
    assert_eq!(
        eye_state.creature(eye).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("REVIVE_MOVE")
    );
    CombatEngine::end_player_turn(&mut eye_state, &content).unwrap();
    assert_eq!(eye_state.creature(eye).unwrap().current_hp, 6);
    assert_eq!(
        eye_state.creature(eye).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("DISTRACT_MOVE")
    );

    // Wriggler slot branching
    let (mut wr_state, wr_enemies) = build_battle(
        &[
            (MonsterId::Wriggler, Some("wriggler1")),
            (MonsterId::Wriggler, Some("wriggler2")),
        ],
        0,
        GameMode::Standard,
        &[],
        &["wriggler1", "wriggler2"],
    );
    CombatEngine::start_combat(&mut wr_state, &content).unwrap();
    assert_eq!(
        wr_state.creature(wr_enemies[0]).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("NASTY_BITE_MOVE")
    );
    assert_eq!(
        wr_state.creature(wr_enemies[1]).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("WRIGGLE_MOVE")
    );
}
