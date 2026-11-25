use smallvec::SmallVec;
use std::borrow::Cow;
use std::sync::Arc;
use sts2_content::Sts2Content;
use sts2_content::dread_blitz::{
    apply_knowledge_demon_choice, blitz_ascension_level, dispatch_dominion_hook,
    dispatch_run_model_hook, dominion_hook_mask, filter_cards_for_mode,
    is_ascension_active_in_mode, is_card_allowed_in_mode, is_card_allowed_in_run,
    is_potion_allowed_in_mode, is_relic_allowed_in_mode, is_special_mode, is_weak_encounter,
    knowledge_demon_choice_cards, max_ascension_for_mode, resume_dominion_hook_choice,
    run_model_hook_mask, vantom_slippery_amount, waterfall_giant_amount_for_state,
    waterfall_giant_explosive_amount,
};
use sts2_kernel::commands::creature as creature_cmd;
use sts2_kernel::{
    ArrowSlotInst, BattleState, CardHandle, CardInst, CardTempFlags, ChoiceTiming, CombatEngine,
    CreatureHandle, CreatureInst, HookArgs, MonsterInst, PlayerCounters, PlayerInst, PowerInst,
    RootStaticContext, SemanticHistory, StepOutcome,
};
use sts2_types::{
    ArrowStatus, AscensionLevel, CardId, CardKeywords, CardStaticFlags, CharacterId, CombatSide,
    DominionId, GameMode, HookMask, HookSlot, MonsterId, PileType, PlayerRngSet, PlayerTurnPhase,
    PotionId, PowerId, RelicId, Rng, RoomType, RunModelId, RunRngSet, generated_card_meta,
};

fn build_battle(
    character: CharacterId,
    deck_ids: &[CardId],
    enemies: &[(MonsterId, i32)],
    game_mode: GameMode,
    dominion_id: Option<DominionId>,
    run_model_ids: Vec<RunModelId>,
    encounter_id: &str,
    room_type: RoomType,
) -> (BattleState, Vec<CreatureHandle>) {
    let run_rngs = RunRngSet::from_string_seed("DREADBLITZ1");
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc_handle = CreatureHandle(1);

    let mut cards = Vec::new();
    let mut deck_handles = Vec::new();
    let content = Sts2Content;
    for (idx, &cid) in deck_ids.iter().enumerate() {
        let meta = sts2_kernel::ContentDispatcher::card_meta(&content, cid).unwrap();
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
    for (idx, &(monster_id, hp)) in enemies.iter().enumerate() {
        let eh = CreatureHandle((idx + 2) as u32);
        let slot = format!("slot_{idx}");
        enemy_handles.push(eh);
        encounter_slots.push(slot.clone());
        creatures.push(CreatureInst {
            handle: eh,
            combat_id: Some((idx + 1) as u32),
            side: CombatSide::Enemy,
            slot_name: Some(Cow::Owned(slot)),
            is_player: false,
            pet_owner_player_index: None,
            monster_id: Some(monster_id),
            current_hp: hp,
            max_hp: hp,
            block: 0,
            cumulative_hp_lost: 0,
            is_detached: false,
            monster: Some(Box::new(MonsterInst {
                monster_id,
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
        potion_slots: smallvec::smallvec![None, None, None],
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
            encounter_id: encounter_id.to_owned(),
            encounter_is_weak: is_weak_encounter(encounter_id),
            room_type,
            encounter_slots,
            game_mode,
            ascension: 0,
            character_id: character,
            dominion_id,
            run_model_ids,
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

#[test]
fn all_12_dominions_and_2_run_models_have_zero_unsupported_masks() {
    let all_dominions = [
        (
            DominionId::AeonglassDominion,
            HookMask::EMPTY.with(HookSlot::AfterRoomEntered),
        ),
        (
            DominionId::CeremonialBeastDominion,
            HookMask::EMPTY
                .with(HookSlot::AfterRoomEntered)
                .with(HookSlot::AfterCreatureAddedToCombat),
        ),
        (
            DominionId::InsatiableDominion,
            HookMask::EMPTY.with(HookSlot::AfterRoomEntered),
        ),
        (
            DominionId::KaiserCrabDominion,
            HookMask::EMPTY
                .with(HookSlot::AfterRoomEntered)
                .with(HookSlot::AfterCreatureAddedToCombat),
        ),
        (
            DominionId::KinDominion,
            HookMask::EMPTY.with(HookSlot::AfterRoomEntered),
        ),
        (
            DominionId::KnowledgeDemonDominion,
            HookMask::EMPTY.with(HookSlot::BeforeHandDraw),
        ),
        (
            DominionId::LagavulinMatriarchDominion,
            HookMask::EMPTY.with(HookSlot::AfterRoomEntered),
        ),
        (
            DominionId::QueenDominion,
            HookMask::EMPTY.with(HookSlot::AfterRoomEntered),
        ),
        (
            DominionId::SoulFyshDominion,
            HookMask::EMPTY.with(HookSlot::BeforeCombatStart),
        ),
        (
            DominionId::TestSubjectDominion,
            HookMask::EMPTY
                .with(HookSlot::AfterRoomEntered)
                .with(HookSlot::AfterCreatureAddedToCombat),
        ),
        (
            DominionId::VantomDominion,
            HookMask::EMPTY
                .with(HookSlot::AfterRoomEntered)
                .with(HookSlot::AfterCreatureAddedToCombat),
        ),
        (
            DominionId::WaterfallGiantDominion,
            HookMask::EMPTY
                .with(HookSlot::AfterRoomEntered)
                .with(HookSlot::AfterCreatureAddedToCombat),
        ),
    ];

    assert_eq!(all_dominions.len(), DominionId::COUNT);
    for (dom, expected) in all_dominions {
        assert_eq!(dominion_hook_mask(dom).unwrap(), expected);
    }

    assert_eq!(
        run_model_hook_mask(RunModelId::BlitzRunModel).unwrap(),
        HookMask::EMPTY
    );
    assert_eq!(
        run_model_hook_mask(RunModelId::DreadRunModel).unwrap(),
        HookMask::EMPTY
    );
}

#[test]
fn kin_dominion_applies_weak_and_frail_and_affects_combat() {
    let content = Sts2Content;
    let deck = [
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
    ];
    let (mut state, enemies) = build_battle(
        CharacterId::Ironclad,
        &deck,
        &[(MonsterId::FuzzyWurmCrawler, 55)],
        GameMode::Dread,
        Some(DominionId::KinDominion),
        vec![RunModelId::DreadRunModel],
        "FUZZY_WURM_CRAWLER_WEAK",
        RoomType::Monster,
    );
    let outcome = CombatEngine::start_combat(&mut state, &content).unwrap();
    assert_eq!(outcome, StepOutcome::Completed);

    let pc = state.player_creature().unwrap();
    assert_eq!(pc.powers.len(), 2);
    assert_eq!(pc.powers[0].id, PowerId::WeakPower);
    assert_eq!(pc.powers[0].amount, 2);
    assert_eq!(pc.powers[1].id, PowerId::FrailPower);
    assert_eq!(pc.powers[1].amount, 2);

    let strike = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::StrikeIronclad)
        .unwrap();
    let defend = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::DefendIronclad)
        .unwrap();

    let mut choices = SmallVec::new();
    CombatEngine::play_card(&mut state, &content, strike, Some(enemies[0]), &mut choices).unwrap();
    // 6 * 0.75 = 4 damage
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, 55 - 4);

    CombatEngine::play_card(&mut state, &content, defend, None, &mut choices).unwrap();
    // 5 * 0.75 = 3 block
    assert_eq!(state.player.block, 3);

    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    // FuzzyWurmCrawler deals 4 damage -> 3 block absorbed -> 1 unblocked damage
    assert_eq!(state.player.current_hp, 79);
    // Weak & Frail tick down from 2 -> 1 at enemy turn end
    let pc_after = state.player_creature().unwrap();
    assert_eq!(pc_after.powers[0].amount, 1);
    assert_eq!(pc_after.powers[1].amount, 1);
}

#[test]
fn test_subject_dominion_applies_strength_on_entry_and_mid_combat_summon() {
    let content = Sts2Content;
    let deck = [
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
    ];
    let (mut state, enemies) = build_battle(
        CharacterId::Ironclad,
        &deck,
        &[
            (MonsterId::FuzzyWurmCrawler, 55),
            (MonsterId::ShrinkerBeetle, 40),
        ],
        GameMode::Dread,
        Some(DominionId::TestSubjectDominion),
        vec![RunModelId::DreadRunModel],
        "TEST_MULTI",
        RoomType::Monster,
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();

    for &eh in &enemies {
        let enemy = state.creature(eh).unwrap();
        assert_eq!(enemy.powers.len(), 1);
        assert_eq!(enemy.powers[0].id, PowerId::StrengthPower);
        assert_eq!(enemy.powers[0].amount, 3);
    }

    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    // FuzzyWurmCrawler FIRST_ACID_GOOP base 4 + 3 Strength = 7 dmg
    assert_eq!(state.player.current_hp, 80 - 7);

    // Mid-combat enemy summon receives StrengthPower(3) via AfterCreatureAddedToCombat
    let summoned_enemy = creature_cmd::create_and_add_monster(
        &mut state,
        &content,
        MonsterId::FuzzyWurmCrawler,
        CombatSide::Enemy,
        Some(Cow::Borrowed("slot_2")),
    )
    .unwrap();
    let se = state.creature(summoned_enemy).unwrap();
    assert_eq!(se.powers.len(), 1);
    assert_eq!(se.powers[0].id, PowerId::StrengthPower);
    assert_eq!(se.powers[0].amount, 3);

    // Player-side ally summon does NOT receive StrengthPower
    let ally_pet = creature_cmd::create_and_add_monster(
        &mut state,
        &content,
        MonsterId::Osty,
        CombatSide::Player,
        None,
    )
    .unwrap();
    assert!(state.creature(ally_pet).unwrap().powers.is_empty());
}

#[test]
fn knowledge_demon_dominion_before_hand_draw_suspends_and_resumes() {
    let content = Sts2Content;
    assert_eq!(
        knowledge_demon_choice_cards(),
        [CardId::Sloth, CardId::Amnesia]
    );

    let (mut state, _) = build_battle(
        CharacterId::Ironclad,
        &[CardId::StrikeIronclad; 5],
        &[(MonsterId::FuzzyWurmCrawler, 55)],
        GameMode::Dread,
        Some(DominionId::KnowledgeDemonDominion),
        vec![RunModelId::DreadRunModel],
        "FUZZY_WURM_CRAWLER_WEAK",
        RoomType::Monster,
    );

    // Turn 1 + alive pauses at BeforeHandDraw with [Sloth, Amnesia] choice
    let outcome = CombatEngine::start_combat(&mut state, &content).unwrap();
    let req = match outcome {
        StepOutcome::NeedsChoice(req) => req,
        other => panic!("expected NeedsChoice, got {other:?}"),
    };
    assert_eq!(req.timing, ChoiceTiming::PlayerTurnStart);
    assert_eq!(req.source_id, "DOMINION.KNOWLEDGE_DEMON_DOMINION");
    assert_eq!(req.source_pile, PileType::None);
    assert_eq!(req.min_count, 1);
    assert_eq!(req.max_count, 1);
    assert!(!req.can_skip);
    assert_eq!(req.candidates.len(), 2);
    assert_eq!(state.card(req.candidates[0]).unwrap().id, CardId::Sloth);
    assert_eq!(state.card(req.candidates[1]).unwrap().id, CardId::Amnesia);

    // Clone paused state and resume both choices via BeforeHandDrawResume -> resume_dominion_hook_choice
    let mut branch_sloth = state.clone();
    let mut branch_amnesia = state.clone();
    let sloth_out = CombatEngine::resume_choice(&mut branch_sloth, &content, &[0]).unwrap();
    assert_eq!(sloth_out, StepOutcome::Completed);
    assert!(branch_sloth
        .player_creature()
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::SlothPower));
    let amnesia_out =
        CombatEngine::resume_choice(&mut branch_amnesia, &content, &[1]).unwrap();
    assert_eq!(amnesia_out, StepOutcome::Completed);
    assert!(branch_amnesia
        .player_creature()
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::AmnesiaPower));

    // Turn 2 is a no-op Ok with no choice request
    state.pending = None;
    state.player.turn_number = 2;
    let res_t2 = dispatch_dominion_hook(
        &content,
        &mut state,
        DominionId::KnowledgeDemonDominion,
        HookSlot::BeforeHandDraw,
        &HookArgs::Player,
    )
    .unwrap();
    assert!(res_t2.needs_choice.is_none());

    // Turn 1 with dead player is a no-op Ok with no choice request
    state.player.turn_number = 1;
    state.player_creature_mut().unwrap().current_hp = 0;
    let res_dead = dispatch_dominion_hook(
        &content,
        &mut state,
        DominionId::KnowledgeDemonDominion,
        HookSlot::BeforeHandDraw,
        &HookArgs::Player,
    )
    .unwrap();
    assert!(res_dead.needs_choice.is_none());

    // Direct helper rejects invalid cards and resume_dominion_hook_choice never returns Unsupported
    state.player_creature_mut().unwrap().current_hp = 80;
    let invalid_err =
        apply_knowledge_demon_choice(&content, &mut state, CardId::StrikeIronclad, None)
            .unwrap_err();
    assert!(matches!(invalid_err.kind(), sts2_kernel::KernelErrorKind::InvalidAction(_)));
    let non_choice_res = resume_dominion_hook_choice(
        &content,
        &mut state,
        DominionId::KinDominion,
        HookSlot::AfterRoomEntered,
        &[],
    )
    .unwrap();
    assert!(non_choice_res.needs_choice.is_none());
}

#[test]
fn dominions_awaiting_wave1_powers_or_cards_dispatch_exact_downstream_targets() {
    let content = Sts2Content;
    let supported_wave1_dominions = [
        (
            DominionId::AeonglassDominion,
            HookSlot::AfterRoomEntered,
            true,
            PowerId::AeonglassDominionPower,
        ),
        (
            DominionId::CeremonialBeastDominion,
            HookSlot::AfterRoomEntered,
            false,
            PowerId::RitualPower,
        ),
        (
            DominionId::InsatiableDominion,
            HookSlot::AfterRoomEntered,
            true,
            PowerId::InsatiableDominionPower,
        ),
        (
            DominionId::KaiserCrabDominion,
            HookSlot::AfterRoomEntered,
            false,
            PowerId::PlatingPower,
        ),
        (
            DominionId::LagavulinMatriarchDominion,
            HookSlot::AfterRoomEntered,
            true,
            PowerId::LagavulinMatriarchDominionPower,
        ),
        (
            DominionId::QueenDominion,
            HookSlot::AfterRoomEntered,
            true,
            PowerId::YouAreMinePower,
        ),
        (
            DominionId::VantomDominion,
            HookSlot::AfterRoomEntered,
            false,
            PowerId::SlipperyPower,
        ),
        (
            DominionId::WaterfallGiantDominion,
            HookSlot::AfterRoomEntered,
            false,
            PowerId::ExplosivePower,
        ),
    ];

    for (dom, slot, on_player, expected_power) in supported_wave1_dominions {
        let (mut state, enemies) = build_battle(
            CharacterId::Ironclad,
            &[CardId::StrikeIronclad; 5],
            &[(MonsterId::FuzzyWurmCrawler, 55)],
            GameMode::Dread,
            Some(dom),
            vec![RunModelId::DreadRunModel],
            "FUZZY_WURM_CRAWLER_WEAK",
            RoomType::Monster,
        );
        state.is_in_progress = true;
        dispatch_dominion_hook(&content, &mut state, dom, slot, &HookArgs::None).unwrap();
        let target_h = if on_player {
            state.player.creature_handle
        } else {
            enemies[0]
        };
        assert!(
            state
                .creature(target_h)
                .unwrap()
                .powers
                .iter()
                .any(|p| p.id == expected_power),
            "expected {expected_power:?} on {dom:?}"
        );
    }

    let (mut state, _) = build_battle(
        CharacterId::Ironclad,
        &[CardId::StrikeIronclad; 5],
        &[(MonsterId::FuzzyWurmCrawler, 55)],
        GameMode::Dread,
        Some(DominionId::SoulFyshDominion),
        vec![RunModelId::DreadRunModel],
        "FUZZY_WURM_CRAWLER_WEAK",
        RoomType::Monster,
    );
    state.is_in_progress = true;
    dispatch_dominion_hook(
        &content,
        &mut state,
        DominionId::SoulFyshDominion,
        HookSlot::BeforeCombatStart,
        &HookArgs::None,
    )
    .unwrap();
    assert!(
        state
            .player
            .draw
            .iter()
            .any(|&h| state.card(h).is_some_and(|c| c.id == CardId::Beckon))
    );

    // Player creature added to combat must be a no-op for enemy-entry dominions
    let (mut state, _) = build_battle(
        CharacterId::Ironclad,
        &[CardId::StrikeIronclad; 5],
        &[(MonsterId::FuzzyWurmCrawler, 55)],
        GameMode::Dread,
        Some(DominionId::WaterfallGiantDominion),
        vec![RunModelId::DreadRunModel],
        "FUZZY_WURM_CRAWLER_WEAK",
        RoomType::Monster,
    );
    state.is_in_progress = true;
    let pc = state.player.creature_handle;
    for dom in [
        DominionId::CeremonialBeastDominion,
        DominionId::KaiserCrabDominion,
        DominionId::VantomDominion,
        DominionId::WaterfallGiantDominion,
    ] {
        dispatch_dominion_hook(
            &content,
            &mut state,
            dom,
            HookSlot::AfterCreatureAddedToCombat,
            &HookArgs::Creature(pc),
        )
        .unwrap();
    }

    // WaterfallGiantDominion skips enemies with MinionPower during AfterCreatureAddedToCombat
    let eh = state.enemy_handles[0];
    let enemy_c = state.creature_mut(eh).unwrap();
    enemy_c.powers.push(PowerInst {
        handle: 99,
        id: PowerId::MinionPower,
        amount: 1,
        amount_on_turn_start: 0,
        skip_next_duration_tick: false,
        applier_handle: None,
        ext_index: None,
    });
    enemy_c.has_minion_power = true;
    dispatch_dominion_hook(
        &content,
        &mut state,
        DominionId::WaterfallGiantDominion,
        HookSlot::AfterCreatureAddedToCombat,
        &HookArgs::Creature(eh),
    )
    .unwrap();
}

#[test]
fn waterfall_giant_uses_initial_monster_count_and_encounter_is_weak_from_kernel_state() {
    assert_eq!(
        waterfall_giant_explosive_amount(2, true, RoomType::Monster),
        3
    );
    assert_eq!(
        waterfall_giant_explosive_amount(3, false, RoomType::Elite),
        3
    );
    assert_eq!(
        waterfall_giant_explosive_amount(1, true, RoomType::Monster),
        4
    );
    assert_eq!(
        waterfall_giant_explosive_amount(1, false, RoomType::Elite),
        7
    );
    assert_eq!(
        waterfall_giant_explosive_amount(1, false, RoomType::Monster),
        5
    );
    assert_eq!(
        waterfall_giant_explosive_amount(1, false, RoomType::Boss),
        5
    );

    // Verify BattleState.initial_encounter_monster_count takes precedence over current enemy_handles.len()
    let (mut state, _) = build_battle(
        CharacterId::Ironclad,
        &[CardId::StrikeIronclad; 5],
        &[
            (MonsterId::FuzzyWurmCrawler, 55),
            (MonsterId::ShrinkerBeetle, 40),
        ],
        GameMode::Dread,
        Some(DominionId::WaterfallGiantDominion),
        vec![RunModelId::DreadRunModel],
        "CUSTOM_ENCOUNTER",
        RoomType::Elite,
    );
    state.initial_encounter_monster_count = 1;
    assert_eq!(waterfall_giant_amount_for_state(&state), 7);

    // Verify RootStaticContext.encounter_is_weak overrides custom encounter_id
    let mut ctx = (*state.static_ctx).clone();
    ctx.room_type = RoomType::Monster;
    ctx.encounter_is_weak = true;
    state.static_ctx = Arc::new(ctx);
    assert_eq!(waterfall_giant_amount_for_state(&state), 4);

    assert_eq!(vantom_slippery_amount(RoomType::Elite, true), 2);
    assert_eq!(vantom_slippery_amount(RoomType::Monster, true), 1);
    assert_eq!(vantom_slippery_amount(RoomType::Elite, false), 1);

    let weak_ids = [
        "BOWLBUGS_WEAK",
        "CORPSE_SLUGS_WEAK",
        "DEVOTED_SCULPTOR_WEAK",
        "EXOSKELETONS_WEAK",
        "FUZZY_WURM_CRAWLER_WEAK",
        "NIBBITS_WEAK",
        "SCROLLS_OF_BITING_WEAK",
        "SEAPUNK_WEAK",
        "SHRINKER_BEETLE_WEAK",
        "SLIMES_WEAK",
        "SLUDGE_SPINNER_WEAK",
        "THIEVING_HOPPER_WEAK",
        "TOADPOLES_WEAK",
        "TUNNELER_WEAK",
        "TURRET_OPERATOR_WEAK",
    ];
    for id in weak_ids {
        assert!(is_weak_encounter(id), "{id} should be weak");
        let prefixed = format!("ENCOUNTER.{id}");
        assert!(is_weak_encounter(&prefixed), "{prefixed} should be weak");
    }
    assert!(!is_weak_encounter("NIBBITS_NORMAL"));
    assert!(!is_weak_encounter("ENCOUNTER.SLIMES_NORMAL"));
}

#[test]
fn blitz_and_dread_mode_filters_and_ascension_mapping() {
    assert!(!is_special_mode(GameMode::Standard));
    assert!(is_special_mode(GameMode::Dread));
    assert!(is_special_mode(GameMode::Blitz));

    let banned_cards = [
        CardId::Debt,
        CardId::HandOfGreed,
        CardId::MyShinies,
        CardId::Royalties,
    ];
    for c in banned_cards {
        assert!(!generated_card_meta(c)
            .flags
            .contains(CardStaticFlags::IS_ALLOWED_IN_BLITZ));
        assert!(!is_card_allowed_in_mode(GameMode::Blitz, c));
        assert!(is_card_allowed_in_mode(GameMode::Standard, c));
        assert!(is_card_allowed_in_mode(GameMode::Dread, c));
        assert!(!is_card_allowed_in_run(GameMode::Blitz, c));
        assert!(is_card_allowed_in_run(GameMode::Standard, c));
    }

    assert!(is_card_allowed_in_mode(
        GameMode::Blitz,
        CardId::StrikeIronclad
    ));
    assert!(!is_card_allowed_in_run(
        GameMode::Standard,
        CardId::BeaconOfHope
    ));
    assert!(!is_card_allowed_in_run(
        GameMode::Blitz,
        CardId::BeaconOfHope
    ));

    let filtered = filter_cards_for_mode(
        GameMode::Blitz,
        &[
            CardId::StrikeIronclad,
            CardId::HandOfGreed,
            CardId::BeaconOfHope,
            CardId::Bash,
            CardId::MyShinies,
        ],
    );
    assert_eq!(filtered, vec![CardId::StrikeIronclad, CardId::Bash]);

    let banned_relics = [
        RelicId::AmethystAubergine,
        RelicId::BlackStar,
        RelicId::BookOfFiveRings,
        RelicId::BowlerHat,
        RelicId::CursedPearl,
        RelicId::DowsingRod,
        RelicId::DragonFruit,
        RelicId::DreamCatcher,
        RelicId::Driftwood,
        RelicId::Ectoplasm,
        RelicId::EternalFeather,
        RelicId::FishingRod,
        RelicId::FurCoat,
        RelicId::Girya,
        RelicId::GoldenCompass,
        RelicId::GoldenPearl,
        RelicId::JuzuBracelet,
        RelicId::LastingCandy,
        RelicId::LordsParasol,
        RelicId::LuckyFysh,
        RelicId::MawBank,
        RelicId::MealTicket,
        RelicId::MeatCleaver,
        RelicId::MembershipCard,
        RelicId::MiniatureTent,
        RelicId::OldCoin,
        RelicId::PaelsClaw,
        RelicId::PaelsGrowth,
        RelicId::PaelsWing,
        RelicId::Planisphere,
        RelicId::PrayerWheel,
        RelicId::PumpkinCandle,
        RelicId::RegalPillow,
        RelicId::SealOfGold,
        RelicId::Shovel,
        RelicId::SignetRing,
        RelicId::SilkenTress,
        RelicId::StoneHumidifier,
        RelicId::TheCourier,
        RelicId::TinyMailbox,
        RelicId::ToyBox,
        RelicId::VenerableTeaSet,
        RelicId::WhiteBeastStatue,
        RelicId::WingedBoots,
    ];
    assert_eq!(banned_relics.len(), 44);
    for r in banned_relics {
        assert!(!is_relic_allowed_in_mode(GameMode::Blitz, r));
        assert!(is_relic_allowed_in_mode(GameMode::Standard, r));
        assert!(is_relic_allowed_in_mode(GameMode::Dread, r));
    }
    assert!(is_relic_allowed_in_mode(
        GameMode::Blitz,
        RelicId::BurningBlood
    ));
    assert!(is_potion_allowed_in_mode(
        GameMode::Blitz,
        PotionId::FirePotion
    ));

    assert_eq!(max_ascension_for_mode(GameMode::Blitz), 5);
    assert_eq!(max_ascension_for_mode(GameMode::Dread), 10);
    assert_eq!(max_ascension_for_mode(GameMode::Standard), 10);

    assert_eq!(blitz_ascension_level(1), Some(AscensionLevel::WearyTraveler));
    assert_eq!(blitz_ascension_level(2), Some(AscensionLevel::TightBelt));
    assert_eq!(blitz_ascension_level(3), Some(AscensionLevel::ToughEnemies));
    assert_eq!(blitz_ascension_level(4), Some(AscensionLevel::DeadlyEnemies));
    assert_eq!(blitz_ascension_level(5), Some(AscensionLevel::AscendersBane));
    assert_eq!(blitz_ascension_level(6), None);

    assert!(is_ascension_active_in_mode(
        GameMode::Blitz,
        3,
        AscensionLevel::ToughEnemies
    ));
    assert!(!is_ascension_active_in_mode(
        GameMode::Blitz,
        3,
        AscensionLevel::Poverty
    ));
    assert!(is_ascension_active_in_mode(
        GameMode::Dread,
        2,
        AscensionLevel::WearyTraveler
    ));
    assert!(!is_ascension_active_in_mode(
        GameMode::Dread,
        2,
        AscensionLevel::None
    ));
}

#[test]
fn blitz_and_dread_run_models_run_combat_cleanly() {
    let content = Sts2Content;
    for (mode, rm) in [
        (GameMode::Blitz, RunModelId::BlitzRunModel),
        (GameMode::Dread, RunModelId::DreadRunModel),
    ] {
        let (mut state, enemies) = build_battle(
            CharacterId::Ironclad,
            &[
                CardId::Bash,
                CardId::StrikeIronclad,
                CardId::StrikeIronclad,
                CardId::DefendIronclad,
                CardId::DefendIronclad,
            ],
            &[(MonsterId::FuzzyWurmCrawler, 55)],
            mode,
            None,
            vec![rm],
            "FUZZY_WURM_CRAWLER_WEAK",
            RoomType::Monster,
        );
        CombatEngine::start_combat(&mut state, &content).unwrap();
        let bash = *state
            .player
            .hand
            .iter()
            .find(|&&h| state.card(h).unwrap().id == CardId::Bash)
            .unwrap();
        let mut choices = SmallVec::new();
        CombatEngine::play_card(&mut state, &content, bash, Some(enemies[0]), &mut choices)
            .unwrap();
        assert_eq!(state.creature(enemies[0]).unwrap().current_hp, 55 - 8);
        CombatEngine::end_player_turn(&mut state, &content).unwrap();
        assert_eq!(state.round_number, 2);

        let res = dispatch_run_model_hook(
            &content,
            &mut state,
            rm,
            HookSlot::AfterRoomEntered,
            &HookArgs::None,
        )
        .unwrap();
        assert_eq!(res.dec_val, None);
    }
}

#[test]
fn phrog_parasite_infested_wrigglers_ceremonial_dominion_trigger_ritual_and_slot_moves() {
    let content = Sts2Content;
    let (mut state, enemies) = build_battle(
        CharacterId::Necrobinder,
        &[CardId::StrikeNecrobinder; 5],
        &[(MonsterId::PhrogParasite, 10)],
        GameMode::Dread,
        Some(DominionId::CeremonialBeastDominion),
        vec![RunModelId::DreadRunModel],
        "PHROG_PARASITE_ELITE",
        RoomType::Elite,
    );
    let phrog = enemies[0];
    let pc = state.player.creature_handle;
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // Kill PhrogParasite during player turn -> InfestedPower spawns 4 start-stunned Wrigglers
    // and CeremonialBeastDominion grants RitualPower(1) during Player turn.
    sts2_kernel::commands::damage::damage_targets(
        &mut state,
        &content,
        &[phrog],
        sts2_types::Dec::from_i32(50),
        sts2_types::ValueProp::MOVE,
        Some(pc),
        None,
        None,
    )
    .unwrap();
    assert_eq!(state.enemy_handles.len(), 4);
    for (idx, &wh) in state.enemy_handles.iter().enumerate() {
        let m = state.creature(wh).unwrap().monster.as_ref().unwrap();
        assert_eq!(m.next_move_id.as_deref(), Some("SPAWNED_MOVE"), "wriggler {idx}");
    }

    // Enemy turn runs SPAWNED_MOVE and RitualPower triggers +1 Strength on first enemy turn end.
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert!(state.is_in_progress);
    let expected_moves = [
        ("wriggler1", "NASTY_BITE_MOVE"),
        ("wriggler2", "WRIGGLE_MOVE"),
        ("wriggler3", "NASTY_BITE_MOVE"),
        ("wriggler4", "WRIGGLE_MOVE"),
    ];
    for (idx, &wh) in state.enemy_handles.iter().enumerate() {
        let wc = state.creature(wh).unwrap();
        let m = wc.monster.as_ref().unwrap();
        assert_eq!(wc.slot_name.as_deref(), Some(expected_moves[idx].0));
        assert_eq!(m.next_move_id.as_deref(), Some(expected_moves[idx].1));
        let strength = wc
            .powers
            .iter()
            .find(|p| p.id == PowerId::StrengthPower)
            .map_or(0, |p| p.amount);
        assert_eq!(strength, 1, "wriggler {} should gain Ritual strength on first enemy turn end", idx + 1);
    }
}
