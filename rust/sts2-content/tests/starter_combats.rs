use smallvec::SmallVec;
use std::collections::BTreeMap;
use std::sync::Arc;
use sts2_content::Sts2Content;
use sts2_kernel::{
    ArrowSlotInst, BattleState, CardHandle, CardInst, CardTempFlags, CombatEngine, CreatureHandle,
    CreatureInst, KernelError, MonsterInst, PlayerCounters, PlayerInst, RelicInst,
    RootStaticContext, SemanticHistory, StepOutcome,
};
use sts2_types::{
    ArrowStatus, CardId, CardKeywords, CharacterId, CombatSide, GameMode, MonsterId,
    PileType, PlayerRngSet, PlayerTurnPhase, RelicId, Rng, RoomType, RunRngSet,
};

fn build_starter_battle(
    character: CharacterId,
    deck_ids: &[CardId],
    relics: &[RelicId],
    enemy_id: MonsterId,
    enemy_hp: i32,
) -> (BattleState, CreatureHandle) {
    let run_rngs = RunRngSet::from_string_seed("TESTSEED1");
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc_handle = CreatureHandle(1);
    let enemy_handle = CreatureHandle(2);

    let mut cards = Vec::new();
    let mut deck_handles = SmallVec::new();
    let content = Sts2Content;
    for (idx, &cid) in deck_ids.iter().enumerate() {
        let meta = sts2_kernel::ContentDispatcher::card_meta(&content, cid).unwrap();
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

    let mut arrows = SmallVec::new();
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
        powers: SmallVec::new(),
    };

    let enemy_creature = CreatureInst {
        handle: enemy_handle,
        combat_id: Some(1),
        side: CombatSide::Enemy,
        slot_name: Some("slot_0".to_owned()),
        is_player: false,
        pet_owner_player_index: None,
        monster_id: Some(enemy_id),
        current_hp: enemy_hp,
        max_hp: enemy_hp,
        block: 0,
        cumulative_hp_lost: 0,
        is_detached: false,
        monster: Some(MonsterInst {
            monster_id: enemy_id,
            rng: Rng::from_seed(run_rngs.seed + 1),
            is_performing_move: false,
            spawned_this_turn: false,
            current_state_id: String::new(),
            next_move_id: None,
            next_move_can_transition_away: true,
            performed_first_move: false,
            state_log_ids: SmallVec::new(),
            transient_move_follow_up_state_id: None,
            setup_fields: BTreeMap::new(),
            private_fields: BTreeMap::new(),
        }),
        powers: SmallVec::new(),
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
        hand: SmallVec::new(),
        draw: SmallVec::new(),
        discard: SmallVec::new(),
        exhaust: SmallVec::new(),
        play: SmallVec::new(),
        deck: deck_handles,
        potion_slots: smallvec::smallvec![None, None, None],
        orb_capacity: character.base_orb_slot_count(),
        orbs: SmallVec::new(),
        arrows,
        relics: relics
            .iter()
            .map(|&id| RelicInst {
                id,
                is_wax: false,
                is_melted: false,
                stack_count: 1,
                floor_added_to_deck: None,
                private_fields: BTreeMap::new(),
            })
            .collect(),
        card_or_potion_effect_depth: 0,
    };

    let state = BattleState {
        static_ctx: Arc::new(RootStaticContext {
            encounter_id: "TEST_ENCOUNTER".to_owned(),
            encounter_is_weak: false,
            room_type: RoomType::Monster,
            encounter_slots: vec!["slot_0".to_owned()],
            game_mode: GameMode::Standard,
            ascension: 0,
            character_id: character,
            dominion_id: None,
            run_model_ids: Vec::new(),
            card_generation_pools: Vec::new(),
            transformation_pools: Vec::new(),
            map_col: 0,
            map_row: 0,
            act_index: 0,
            total_floor: 0,
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
        ally_handles: smallvec::smallvec![pc_handle],
        enemy_handles: smallvec::smallvec![enemy_handle],
        escaped_handles: SmallVec::new(),
        removed_handles: SmallVec::new(),
        spawned_enemy_handles: smallvec::smallvec![enemy_handle],
        pet_handles: SmallVec::new(),
        damage_history: Vec::new(),
        semantic_history: SemanticHistory::default(),
        pending: None,
        vakuu_auto_select_depth: 0,
    };
    (state, enemy_handle)
}

#[test]
fn ironclad_bash_and_strike_with_vulnerable() {
    let content = Sts2Content;
    let deck = [
        CardId::Bash,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
    ];
    let (mut state, enemy) = build_starter_battle(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::BurningBlood],
        MonsterId::FuzzyWurmCrawler,
        55,
    );
    let outcome = CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(outcome, StepOutcome::Completed);
    assert_eq!(state.player.hand.len(), 5);
    assert_eq!(state.player.energy, 3);

    let bash_h = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::Bash)
        .unwrap();
    let strike_h = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::StrikeIronclad)
        .unwrap();

    let mut choices = SmallVec::new();
    CombatEngine::play_card(&mut state, &content, bash_h, Some(enemy), &mut choices).unwrap();
    assert_eq!(state.creature(enemy).unwrap().current_hp, 55 - 8);
    assert_eq!(state.creature(enemy).unwrap().powers[0].amount, 2);

    CombatEngine::play_card(&mut state, &content, strike_h, Some(enemy), &mut choices).unwrap();
    // 6 * 1.5 = 9 damage
    assert_eq!(state.creature(enemy).unwrap().current_hp, 55 - 8 - 9);
    assert_eq!(state.player.energy, 0);

    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.round_number, 2);
    // FuzzyWurmCrawler FIRST_ACID_GOOP deals 4 damage
    assert_eq!(state.player.current_hp, 80 - 4);
    // Vulnerable ticked down from 2 -> 1 at enemy turn end
    assert_eq!(state.creature(enemy).unwrap().powers[0].amount, 1);
}

#[test]
fn silent_survivor_choice_pause_clone_and_resume() {
    let content = Sts2Content;
    let deck = [
        CardId::Survivor,
        CardId::Neutralize,
        CardId::StrikeSilent,
        CardId::DefendSilent,
        CardId::DefendSilent,
        CardId::StrikeSilent,
        CardId::StrikeSilent,
    ];
    let (mut state, enemy) = build_starter_battle(
        CharacterId::Silent,
        &deck,
        &[RelicId::RingOfTheSnake],
        MonsterId::ShrinkerBeetle,
        40,
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();
    // RingOfTheSnake draws 7 on turn 1
    assert_eq!(state.player.hand.len(), 7);

    let surv_h = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::Survivor)
        .unwrap();

    let mut choices = SmallVec::new();
    let res = CombatEngine::play_card(&mut state, &content, surv_h, None, &mut choices).unwrap();
    assert!(matches!(res, StepOutcome::NeedsChoice(_)));
    assert_eq!(state.player.block, 8);

    // Clone at paused choice point and test two independent discard choices
    let mut branch_a = state.clone();
    let mut branch_b = state.clone();
    CombatEngine::resume_choice(&mut branch_a, &content, &[0]).unwrap();
    CombatEngine::resume_choice(&mut branch_b, &content, &[1]).unwrap();
    assert_ne!(branch_a.player.discard, branch_b.player.discard);
    assert_eq!(branch_a.player.hand.len(), 5);
    assert_eq!(branch_b.player.hand.len(), 5);

    let neut_h = *branch_a
        .player
        .hand
        .iter()
        .find(|&&h| branch_a.card(h).unwrap().id == CardId::Neutralize)
        .unwrap();
    CombatEngine::play_card(&mut branch_a, &content, neut_h, Some(enemy), &mut choices).unwrap();
    assert_eq!(branch_a.creature(enemy).unwrap().current_hp, 40 - 3);
}

#[test]
fn defect_cracked_core_zap_and_dualcast() {
    let content = Sts2Content;
    let deck = [
        CardId::Zap,
        CardId::Dualcast,
        CardId::StrikeDefect,
        CardId::DefendDefect,
        CardId::DefendDefect,
    ];
    let (mut state, enemy) = build_starter_battle(
        CharacterId::Defect,
        &deck,
        &[RelicId::CrackedCore],
        MonsterId::FuzzyWurmCrawler,
        55,
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();
    // CrackedCore channels 1 Lightning orb on turn 1
    assert_eq!(state.player.orbs.len(), 1);

    let dualcast_h = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::Dualcast)
        .unwrap();
    let mut choices = SmallVec::new();
    CombatEngine::play_card(&mut state, &content, dualcast_h, None, &mut choices).unwrap();
    // 8 + 8 evoke damage
    assert_eq!(state.creature(enemy).unwrap().current_hp, 55 - 16);
    assert!(state.player.orbs.is_empty());

    let zap_h = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::Zap)
        .unwrap();
    CombatEngine::play_card(&mut state, &content, zap_h, None, &mut choices).unwrap();
    assert_eq!(state.player.orbs.len(), 1);

    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    // Lightning passive 3 damage before turn end
    assert_eq!(state.creature(enemy).unwrap().current_hp, 55 - 16 - 3);
}

#[test]
fn regent_necrobinder_and_fool_starters() {
    let content = Sts2Content;
    // Regent: DivineRight (+3 stars) -> FallingStar (2 stars, 8 dmg + Weak + Vuln) -> Venerate (+2 stars)
    {
        let deck = [
            CardId::FallingStar,
            CardId::Venerate,
            CardId::StrikeRegent,
            CardId::DefendRegent,
            CardId::DefendRegent,
        ];
        let (mut state, enemy) = build_starter_battle(
            CharacterId::Regent,
            &deck,
            &[RelicId::DivineRight],
            MonsterId::FuzzyWurmCrawler,
            55,
        );
        CombatEngine::start_combat(&mut state, &content).unwrap();
        assert_eq!(state.player.stars, 3);
        let fs = *state
            .player
            .hand
            .iter()
            .find(|&&h| state.card(h).unwrap().id == CardId::FallingStar)
            .unwrap();
        let ven = *state
            .player
            .hand
            .iter()
            .find(|&&h| state.card(h).unwrap().id == CardId::Venerate)
            .unwrap();
        let mut choices = SmallVec::new();
        CombatEngine::play_card(&mut state, &content, fs, Some(enemy), &mut choices).unwrap();
        assert_eq!(state.player.stars, 1);
        assert_eq!(state.creature(enemy).unwrap().current_hp, 55 - 8);
        CombatEngine::play_card(&mut state, &content, ven, None, &mut choices).unwrap();
        assert_eq!(state.player.stars, 3);
    }

    // Necrobinder: BoundPhylactery summons 1 Osty -> Bodyguard summons +5 (Osty 6 HP) -> Unleash deals 6 + 6 = 12
    {
        let deck = [
            CardId::Bodyguard,
            CardId::Unleash,
            CardId::StrikeNecrobinder,
            CardId::DefendNecrobinder,
            CardId::DefendNecrobinder,
        ];
        let (mut state, enemy) = build_starter_battle(
            CharacterId::Necrobinder,
            &deck,
            &[RelicId::BoundPhylactery],
            MonsterId::FuzzyWurmCrawler,
            55,
        );
        CombatEngine::start_combat(&mut state, &content).unwrap();
        let osty = state.osty_handle().expect("Osty summoned");
        assert_eq!(state.creature(osty).unwrap().current_hp, 1);

        let bg = *state
            .player
            .hand
            .iter()
            .find(|&&h| state.card(h).unwrap().id == CardId::Bodyguard)
            .unwrap();
        let unleash = *state
            .player
            .hand
            .iter()
            .find(|&&h| state.card(h).unwrap().id == CardId::Unleash)
            .unwrap();
        let mut choices = SmallVec::new();
        CombatEngine::play_card(&mut state, &content, bg, None, &mut choices).unwrap();
        assert_eq!(state.creature(osty).unwrap().current_hp, 6);
        CombatEngine::play_card(&mut state, &content, unleash, Some(enemy), &mut choices).unwrap();
        assert_eq!(state.creature(enemy).unwrap().current_hp, 55 - 12);
        // End turn -> FuzzyWurmCrawler deals 4 damage (DieForYou redirects to Osty: 6 -> 2),
        // then Turn 2 starts and BoundPhylactery summons +1 (2 -> 3 HP, 7 MaxHp).
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
        assert_eq!(state.player.current_hp, 66);
        assert_eq!(state.creature(osty).unwrap().current_hp, 3);
        assert_eq!(state.creature(osty).unwrap().max_hp, 7);
    }

    // Fool: SongBow (+6 Aim) -> FoolishShot (15 + 6 = 21 dmg, consumes Aim, impales arrow)
    {
        let deck = [
            CardId::FoolishShot,
            CardId::StrikeFool,
            CardId::StrikeFool,
            CardId::DefendFool,
            CardId::DefendFool,
        ];
        let (mut state, enemy) = build_starter_battle(
            CharacterId::Fool,
            &deck,
            &[RelicId::SongBow],
            MonsterId::FuzzyWurmCrawler,
            55,
        );
        CombatEngine::start_combat(&mut state, &content).unwrap();
        let fs = *state
            .player
            .hand
            .iter()
            .find(|&&h| state.card(h).unwrap().id == CardId::FoolishShot)
            .unwrap();
        let mut choices = SmallVec::new();
        CombatEngine::play_card(&mut state, &content, fs, Some(enemy), &mut choices).unwrap();
        assert_eq!(state.creature(enemy).unwrap().current_hp, 55 - 21);
        assert_eq!(state.player.arrows[0].status, ArrowStatus::Away);
    }
}

#[test]
fn huge_deck_2305_cards_no_truncation() {
    let content = Sts2Content;
    let deck = vec![CardId::StrikeIronclad; 2305];
    let (mut state, _enemy) = build_starter_battle(
        CharacterId::Ironclad,
        &deck,
        &[],
        MonsterId::FuzzyWurmCrawler,
        55,
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(state.player.hand.len(), 5);
    assert_eq!(state.player.draw.len(), 2300);
}

#[test]
fn unsupported_entity_returns_explicit_err() {
    let content = Sts2Content;
    assert!(CardId::from_model_id_str("CARD.MODDED_UNKNOWN").is_none());
    assert!(MonsterId::from_model_id_str("MONSTER.MODDED_UNKNOWN").is_none());
    assert!(RelicId::from_model_id_str("RELIC.MODDED_UNKNOWN").is_none());

    let (mut state, _enemy) = build_starter_battle(
        CharacterId::Ironclad,
        &[CardId::StrikeIronclad],
        &[],
        MonsterId::FuzzyWurmCrawler,
        55,
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();
    let base_dto = sts2_kernel::snapshot::export_live_state(&state).unwrap();

    let mut bad_relic = base_dto.clone();
    bad_relic.relics.push(sts2_kernel::snapshot::RelicDto {
        relic_id: "RELIC.MODDED_UNKNOWN".to_owned(),
        is_wax: false,
        is_melted: false,
        stack_count: 1,
        floor_added_to_deck: None,
        private_fields: BTreeMap::new(),
    });
    let relic_err = sts2_kernel::snapshot::import_live_state(&bad_relic, &content).unwrap_err();
    assert!(matches!(relic_err, KernelError::Unsupported { kind: "Relic", .. }));

    let mut bad_card = base_dto.clone();
    bad_card.cards[0].card_id = "CARD.MODDED_UNKNOWN".to_owned();
    let card_err = sts2_kernel::snapshot::import_live_state(&bad_card, &content).unwrap_err();
    assert!(matches!(card_err, KernelError::Unsupported { kind: "Card", .. }));

    let mut bad_monster = base_dto;
    bad_monster.creatures[1].monster_id = Some("MONSTER.MODDED_UNKNOWN".to_owned());
    let monster_err = sts2_kernel::snapshot::import_live_state(&bad_monster, &content).unwrap_err();
    assert!(matches!(monster_err, KernelError::Unsupported { kind: "Monster", .. }));
}

#[test]
fn hook_order_1082_places_powers_before_relics_and_dominion_last() {
    use sts2_kernel::HookListener;
    use sts2_types::{DominionId, PowerId, RunModelId};

    let (mut state, _enemy) = build_starter_battle(
        CharacterId::Ironclad,
        &[CardId::StrikeIronclad],
        &[RelicId::BurningBlood],
        MonsterId::FuzzyWurmCrawler,
        55,
    );
    let content = Sts2Content;
    CombatEngine::start_combat(&mut state, &content).unwrap();

    let pc = state.player.creature_handle;
    sts2_kernel::commands::power::apply(
        &mut state,
        &content,
        PowerId::StrengthPower,
        pc,
        sts2_types::Dec::ONE,
        Some(pc),
        None,
    )
    .unwrap();
    let mut ctx = (*state.static_ctx).clone();
    ctx.dominion_id = Some(DominionId::AeonglassDominion);
    ctx.run_model_ids = vec![RunModelId::BlitzRunModel];
    state.static_ctx = Arc::new(ctx);

    let listeners = sts2_kernel::hooks::collect_combat_hook_listeners(&state);
    let power_pos = listeners
        .iter()
        .position(|l| matches!(l, HookListener::Power { creature, .. } if *creature == pc))
        .unwrap();
    let relic_pos = listeners
        .iter()
        .position(|l| matches!(l, HookListener::Relic(0)))
        .unwrap();
    let card_pos = listeners
        .iter()
        .position(|l| matches!(l, HookListener::Card(_)))
        .unwrap();
    let monster_pos = listeners
        .iter()
        .position(|l| matches!(l, HookListener::Monster(_)))
        .unwrap();
    let run_model_pos = listeners
        .iter()
        .position(|l| matches!(l, HookListener::RunModel(RunModelId::BlitzRunModel)))
        .unwrap();
    let dominion_pos = listeners
        .iter()
        .position(|l| matches!(l, HookListener::Dominion(DominionId::AeonglassDominion)))
        .unwrap();

    assert!(power_pos < relic_pos);
    assert!(relic_pos < card_pos);
    assert!(card_pos < monster_pos);
    assert!(monster_pos < run_model_pos);
    assert!(run_model_pos < dominion_pos);

    let intents = sts2_kernel::ContentDispatcher::monster_attack_intent_hits(&content, &state, _enemy).unwrap();
    assert_eq!(intents.as_slice(), &[(sts2_types::Dec::from_i32(4), 1)]);
    assert!(sts2_kernel::ContentDispatcher::monster_intends_attack(&content, &state, _enemy).unwrap());
}
