use smallvec::SmallVec;
use std::borrow::Cow;
use std::sync::Arc;
use sts2_content::Sts2Content;
use sts2_kernel::content_dispatch::CardPlayContext;
use sts2_kernel::ordering;
use sts2_kernel::{
    BattleState, CardHandle, CardInst, CardTempFlags, CombatEngine, ContentDispatcher,
    CreatureHandle, CreatureInst, MonsterInst, PlayerCounters, PlayerInst, RootStaticContext,
    SemanticHistory, StepOutcome,
};
use sts2_types::{
    CardId, CardKeywords, CardStaticFlags, CardType, CharacterId, CombatSide, Dec, GameMode,
    HookSlot, MonsterId, OrbId, PileType, PlayerRngSet, PlayerTurnPhase, PowerId, Rng, RoomType,
    RunRngSet, CARD_POOL_DEFECT,
};

fn make_card(content: &dyn ContentDispatcher, handle: u32, id: CardId, pile: PileType) -> CardInst {
    let meta = content.card_meta(id).unwrap();
    let mut temp_flags = CardTempFlags::empty();
    if meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST) {
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
        deck_card_handle: if pile == PileType::Deck {
            sts2_kernel::OptCardHandle::some(CardHandle(handle))
        } else {
            sts2_kernel::OptCardHandle::NONE
        },
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

fn build_defect_battle(
    hand_ids: &[CardId],
    draw_ids: &[CardId],
    enemy_hp: i32,
) -> (BattleState, CreatureHandle) {
    build_defect_battle_with(
        &Sts2Content,
        hand_ids,
        draw_ids,
        MonsterId::FuzzyWurmCrawler,
        enemy_hp,
    )
}

fn build_defect_battle_with(
    content: &dyn ContentDispatcher,
    hand_ids: &[CardId],
    draw_ids: &[CardId],
    enemy_id: MonsterId,
    enemy_hp: i32,
) -> (BattleState, CreatureHandle) {
    let run_rngs = RunRngSet::from_string_seed("DEFECT_TEST_SEED");
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc_handle = CreatureHandle(1);
    let enemy_handle = CreatureHandle(2);

    let mut cards = Vec::new();
    let mut hand = Vec::new();
    let mut draw = Vec::new();
    let mut next_h = 1u32;

    for &cid in hand_ids {
        let h = CardHandle(next_h);
        cards.push(make_card(content, next_h, cid, PileType::Hand));
        hand.push(h);
        next_h += 1;
    }
    for &cid in draw_ids {
        let h = CardHandle(next_h);
        cards.push(make_card(content, next_h, cid, PileType::Draw));
        draw.push(h);
        next_h += 1;
    }

    let player_creature = CreatureInst {
        handle: pc_handle,
        combat_id: Some(0),
        side: CombatSide::Player,
        slot_name: None,
        is_player: true,
        pet_owner_player_index: None,
        monster_id: None,
        current_hp: CharacterId::Defect.starting_hp(),
        max_hp: CharacterId::Defect.starting_hp(),
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
        monster_id: Some(enemy_id),
        current_hp: enemy_hp,
        max_hp: enemy_hp,
        block: 0,
        cumulative_hp_lost: 0,
        is_detached: false,
        monster: Some(Box::new(MonsterInst {
            monster_id: enemy_id,
            rng: Rng::from_seed(run_rngs.seed + 1),
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
    };

    let player = PlayerInst {
        creature_handle: pc_handle,
        current_hp: CharacterId::Defect.starting_hp(),
        max_hp: CharacterId::Defect.starting_hp(),
        block: 0,
        cumulative_hp_lost: 0,
        gold: 99,
        max_energy: 3,
        base_orb_slot_count: CharacterId::Defect.base_orb_slot_count(),
        is_active_for_hooks: true,
        can_use_or_remove_potions: true,
        energy: 10,
        stars: 0,
        phase: PlayerTurnPhase::Play,
        turn_number: 1,
        counters: PlayerCounters::default(),
        hand,
        draw,
        discard: Vec::new(),
        exhaust: Vec::new(),
        play: Vec::new(),
        deck: Vec::new(),
        potion_slots: smallvec::smallvec![None, None, None],
        combat_generated_potion_mask: 0,
        orb_capacity: 3,
        orbs: Vec::new(),
        arrows: Vec::new(),
        relics: Vec::new(),
        card_or_potion_effect_depth: 0,
    };

    let state = BattleState {
        static_ctx: Arc::new(RootStaticContext {
            encounter_id: "DEFECT_TEST".to_owned(),
            encounter_is_weak: false,
            room_type: RoomType::Monster,
            encounter_slots: vec!["slot_0".to_owned()],
            game_mode: GameMode::Standard,
            ascension: 0,
            character_id: CharacterId::Defect,
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
        is_in_progress: true,
        is_starting: false,
        won: false,
        is_player_extra_turn: false,
        gold_was_stolen: false,
        end_player_turn_requested: false,
        card_action_boundary_depth: 0,
        next_creature_id: 2,
        initial_encounter_monster_count: 1,
        next_card_handle: next_h,
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
        attacking_creatures: SmallVec::new(),
        deferred_death_cleanup: SmallVec::new(),
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

fn add_second_enemy(state: &mut BattleState, enemy_id: MonsterId, hp: i32) -> CreatureHandle {
    let h = CreatureHandle(state.next_creature_handle);
    state.next_creature_handle += 1;
    let cid = state.next_creature_id;
    state.next_creature_id += 1;
    state.creatures.push(CreatureInst {
        handle: h,
        combat_id: Some(cid),
        side: CombatSide::Enemy,
        slot_name: Some(Cow::Borrowed("slot_1")),
        is_player: false,
        pet_owner_player_index: None,
        monster_id: Some(enemy_id),
        current_hp: hp,
        max_hp: hp,
        block: 0,
        cumulative_hp_lost: 0,
        is_detached: false,
        monster: Some(Box::new(MonsterInst {
            monster_id: enemy_id,
            rng: Rng::from_seed(99),
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
    state.enemy_handles.push(h);
    state.spawned_enemy_handles.push(h);
    state.invalidate_creature_hooks();
    h
}

fn play_hand_idx(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    idx: usize,
    target: Option<CreatureHandle>,
) -> StepOutcome {
    let card_h = state.player.hand[idx];
    let mut choices = SmallVec::new();
    CombatEngine::play_card(state, content, card_h, target, &mut choices).unwrap()
}

fn upgrade_card(state: &mut BattleState, content: &dyn ContentDispatcher, handle: CardHandle) {
    sts2_kernel::commands::card::upgrade(state, content, handle).unwrap();
}

#[test]
fn all_91_defect_cards_have_complete_metadata_and_no_unsupported() {
    assert_eq!(CARD_POOL_DEFECT.len(), 91);
    let content = Sts2Content;
    let (mut state, _) = build_defect_battle(CARD_POOL_DEFECT, &[], 100);
    for (idx, &cid) in CARD_POOL_DEFECT.iter().enumerate() {
        let meta = content.card_meta(cid).expect("card_meta supported");
        let h = state.player.hand[idx];
        assert!(content.card_is_playable(&state, h).unwrap());
        let _ = content.card_comparison_base_values(&state, h).unwrap();
        if meta.max_upgrade_level > 0 {
            upgrade_card(&mut state, &content, h);
        }
    }
    let ga_meta = content.card_meta(CardId::GeneticAlgorithm).unwrap();
    assert!(ga_meta.flags.contains(CardStaticFlags::HAS_GROWTH));
    let (mut ig_state, _) = build_defect_battle(&[CardId::Ignition], &[], 100);
    let ig_h = ig_state.player.hand[0];
    assert!(ig_state.card(ig_h).unwrap().effective_keywords_base().contains(CardKeywords::EXHAUST));
    upgrade_card(&mut ig_state, &content, ig_h);
    assert!(!ig_state.card(ig_h).unwrap().effective_keywords_base().contains(CardKeywords::EXHAUST));
    assert!(content
        .card_meta(CardId::Chaos)
        .unwrap()
        .flags
        .contains(CardStaticFlags::USES_RNG));
    assert!(content
        .card_meta(CardId::RocketPunch)
        .unwrap()
        .hook_mask
        .contains(HookSlot::AfterCardGeneratedForCombat));
}

#[test]
fn defect_orbs_evoke_passive_and_slots() {
    let content = Sts2Content;
    let (mut state, enemy) = build_defect_battle(
        &[
            CardId::Capacitor,
            CardId::Rainbow,
            CardId::Darkness,
            CardId::Barrage,
            CardId::CompileDriver,
            CardId::MultiCast,
            CardId::Shatter,
            CardId::BulkUp,
        ],
        &[CardId::StrikeDefect, CardId::DefendDefect, CardId::Zap, CardId::Leap],
        300,
    );

    // Capacitor adds +2 orb slots (3 -> 5)
    play_hand_idx(&mut state, &content, 0, None);
    assert_eq!(state.player.orb_capacity, 5);

    // Rainbow channels Lightning, Frost, Dark
    let rainbow_h = state.player.hand[0];
    play_hand_idx(&mut state, &content, 0, None);
    assert_eq!(state.player.orbs.len(), 3);
    assert_eq!(state.player.orbs[0].id, OrbId::LightningOrb);
    assert_eq!(state.player.orbs[1].id, OrbId::FrostOrb);
    assert_eq!(state.player.orbs[2].id, OrbId::DarkOrb);
    assert_eq!(state.player.exhaust.as_slice(), &[rainbow_h]);

    // Darkness channels DarkOrb and triggers passive on both Dark orbs (6 -> 12)
    play_hand_idx(&mut state, &content, 0, None);
    assert_eq!(state.player.orbs.len(), 4);
    assert_eq!(state.player.orbs[2].raw_evoke_value, Some(Dec::from_i32(12)));
    assert_eq!(state.player.orbs[3].raw_evoke_value, Some(Dec::from_i32(12)));

    // Barrage hits 4 times (4 orbs) for 5 each = 20
    let hp_before = state.creature(enemy).unwrap().current_hp;
    play_hand_idx(&mut state, &content, 0, Some(enemy));
    assert_eq!(state.creature(enemy).unwrap().current_hp, hp_before - 20);

    // CompileDriver hits for 7 and draws 3 (Lightning, Frost, Dark distinct orbs)
    let hand_before = state.player.hand.len();
    play_hand_idx(&mut state, &content, 0, Some(enemy));
    assert_eq!(state.player.hand.len(), hand_before - 1 + 3);

    // MultiCast with energy=2 evokes LightningOrb twice and dequeues it
    state.player.energy = 2;
    let hp_before_mc = state.creature(enemy).unwrap().current_hp;
    play_hand_idx(&mut state, &content, 0, Some(enemy));
    assert_eq!(state.player.energy, 0);
    assert_eq!(state.creature(enemy).unwrap().current_hp, hp_before_mc - 16);
    assert_eq!(state.player.orbs.len(), 3);
    assert_eq!(state.player.orbs[0].id, OrbId::FrostOrb);

    // Shatter deals 7 all-enemies and double-evokes all 3 remaining orbs (Frost + 2x Dark)
    state.player.energy = 10;
    let hp_before_shatter = state.creature(enemy).unwrap().current_hp;
    play_hand_idx(&mut state, &content, 0, None);
    assert_eq!(state.player.orbs.len(), 0);
    // Block from double-evoking Frost (2 * 5 = 10)
    assert_eq!(state.player.block, 10);
    // Damage: 7 (attack) + 12*2 + 12*2 (2 Dark orbs evoked twice each) = 55
    assert_eq!(state.creature(enemy).unwrap().current_hp, hp_before_shatter - 55);

    // BulkUp removes 1 slot (5 -> 4), grants 2 Str + 2 Dex
    play_hand_idx(&mut state, &content, 0, None);
    assert_eq!(state.player.orb_capacity, 4);
    let powers = &state.player_creature().unwrap().powers;
    assert!(powers.iter().any(|p| p.id == PowerId::StrengthPower && p.amount == 2));
    assert!(powers.iter().any(|p| p.id == PowerId::DexterityPower && p.amount == 2));
}

#[test]
fn defect_claw_genetic_algorithm_and_comparison_values() {
    let content = Sts2Content;
    let (mut state, enemy) = build_defect_battle(
        &[CardId::Claw, CardId::Claw, CardId::GeneticAlgorithm],
        &[],
        100,
    );

    // Create a persistent deck copy for GeneticAlgorithm to test DeckVersion growth
    let deck_ga_h = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    let deck_ga = make_card(&content, deck_ga_h.0, CardId::GeneticAlgorithm, PileType::Deck);
    state.cards.push(deck_ga);
    state.player.deck.push(deck_ga_h);
    let combat_ga_h = state.player.hand[2];
    state.card_mut(combat_ga_h).unwrap().deck_card_handle =
        sts2_kernel::OptCardHandle::some(deck_ga_h);

    let claw1 = state.player.hand[0];
    let claw2 = state.player.hand[1];
    upgrade_card(&mut state, &content, claw2);

    // Play Claw 1 (3 dmg, +2 increase to all Claws)
    play_hand_idx(&mut state, &content, 0, Some(enemy));
    assert_eq!(state.creature(enemy).unwrap().current_hp, 97);
    assert_eq!(
        content.card_comparison_base_values(&state, claw1).unwrap().as_slice(),
        &[("Damage", Dec::from_i32(5))]
    );
    assert_eq!(
        content.card_comparison_base_values(&state, claw2).unwrap().as_slice(),
        &[("Damage", Dec::from_i32(6))]
    );

    // Play upgraded Claw 2 (6 dmg, +3 increase to all Claws)
    play_hand_idx(&mut state, &content, 0, Some(enemy));
    assert_eq!(state.creature(enemy).unwrap().current_hp, 91);
    assert_eq!(
        content.card_comparison_base_values(&state, claw1).unwrap().as_slice(),
        &[("Damage", Dec::from_i32(8))]
    );

    // Play GeneticAlgorithm: gains 1 block, buffs both combat card and DeckVersion to 4
    play_hand_idx(&mut state, &content, 0, None);
    assert_eq!(state.player.block, 1);
    assert_eq!(
        content
            .card_comparison_base_values(&state, combat_ga_h)
            .unwrap()
            .as_slice(),
        &[("Block", Dec::from_i32(4))]
    );
    assert_eq!(
        content
            .card_comparison_base_values(&state, deck_ga_h)
            .unwrap()
            .as_slice(),
        &[("Block", Dec::from_i32(4))]
    );
}

#[test]
fn defect_hologram_scavenge_all_for_one_and_adaptive_strike() {
    let content = Sts2Content;
    let (mut state, enemy) = build_defect_battle(
        &[
            CardId::Hologram,
            CardId::Scavenge,
            CardId::AdaptiveStrike,
            CardId::AllForOne,
        ],
        &[],
        200,
    );
    let d1 = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    state.cards.push(make_card(&content, d1.0, CardId::Zap, PileType::Discard));
    let d2 = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    state.cards.push(make_card(&content, d2.0, CardId::BeamCell, PileType::Discard));
    let d3 = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    state.cards.push(make_card(&content, d3.0, CardId::Tempest, PileType::Discard));
    state.player.discard.extend([d1, d2, d3]);

    // Hologram pauses for Discard selection (3 candidates)
    let out = play_hand_idx(&mut state, &content, 0, None);
    assert!(matches!(out, StepOutcome::NeedsChoice(_)));
    assert_eq!(state.player.block, 3);
    CombatEngine::resume_choice(&mut state, &content, &[0]).unwrap();
    assert!(state.player.hand.contains(&d1));

    // Scavenge pauses for Hand selection -> exhausts chosen card, grants EnergyNextTurnPower 2
    let scavenge_target = state.player.hand[3]; // Zap
    let out = play_hand_idx(&mut state, &content, 0, None);
    assert!(matches!(out, StepOutcome::NeedsChoice(_)));
    CombatEngine::resume_choice(&mut state, &content, &[2]).unwrap();
    assert!(state.player.exhaust.contains(&scavenge_target));
    assert!(state
        .player_creature()
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::EnergyNextTurnPower && p.amount == 2));

    // AdaptiveStrike creates a 0-cost clone in Discard
    let adaptive_h = state.player.hand[0];
    play_hand_idx(&mut state, &content, 0, Some(enemy));
    let clone_h = *state
        .player
        .discard
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::AdaptiveStrike)
        .unwrap();
    assert_eq!(
        state.card(clone_h).unwrap().clone_of_handle.get(),
        Some(adaptive_h)
    );
    assert_eq!(ordering::resolve_energy_cost(&mut state, &content, clone_h).unwrap(), 0);

    // AllForOne brings 0-cost non-X cards (BeamCell + 0-cost AdaptiveStrike clone) to Hand, leaving Tempest (X-cost)
    play_hand_idx(&mut state, &content, 0, Some(enemy));
    assert!(state.player.hand.contains(&d2));
    assert!(state.player.hand.contains(&clone_h));
    assert!(state.player.discard.contains(&d3));
}

#[test]
fn defect_reboot_scrape_ftl_helix_drill_sunder_and_go_for_the_eyes() {
    let content = Sts2Content;
    let (mut state, enemy) = build_defect_battle(
        &[
            CardId::GoForTheEyes,
            CardId::Ftl,
            CardId::HelixDrill,
            CardId::Scrape,
            CardId::Reboot,
            CardId::Sunder,
        ],
        &[
            CardId::Claw,
            CardId::Zap,
            CardId::Leap,
            CardId::BeamCell,
            CardId::StrikeDefect,
            CardId::DefendDefect,
        ],
        100,
    );
    // FuzzyWurmCrawler intends FIRST_ACID_GOOP (attack intent)
    if let Some(m) = state.creature_mut(enemy).and_then(|c| c.monster.as_mut()) {
        m.next_move_id = Some(Cow::Borrowed("FIRST_ACID_GOOP"));
    }

    // GoForTheEyes applies WeakPower(1) because FuzzyWurmCrawler intends FIRST_ACID_GOOP
    play_hand_idx(&mut state, &content, 0, Some(enemy));
    assert!(state
        .creature(enemy)
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::WeakPower && p.amount == 1));

    // Ftl: cards_played_this_turn == 1 < 3 -> draws Claw
    play_hand_idx(&mut state, &content, 0, Some(enemy));
    assert_eq!(state.player.hand.last().copied().map(|h| state.card(h).unwrap().id), Some(CardId::Claw));

    // Play Scrape (index 1: costs 1) -> draws Zap(1), Leap(1), BeamCell(0), Strike(1); discards non-0-cost!
    play_hand_idx(&mut state, &content, 1, Some(enemy));
    assert!(state
        .player
        .hand
        .iter()
        .any(|&h| state.card(h).unwrap().id == CardId::BeamCell));
    assert!(state
        .player
        .discard
        .iter()
        .any(|&h| state.card(h).unwrap().id == CardId::Leap));

    // HelixDrill: 1 energy spent earlier this turn (Scrape), 0 spent by HelixDrill -> 1 hit of 3
    let hp_before_hd = state.creature(enemy).unwrap().current_hp;
    play_hand_idx(&mut state, &content, 0, Some(enemy));
    assert_eq!(state.creature(enemy).unwrap().current_hp, hp_before_hd - 3);

    // Reboot: moves hand to draw, shuffles, draws 4, exhausts Reboot
    let reboot_h = state.player.hand[0];
    play_hand_idx(&mut state, &content, 0, None);
    assert!(state.player.exhaust.contains(&reboot_h));
    assert_eq!(state.player.hand.len(), 4);

    // Sunder killing a secondary/non-last enemy grants +3 energy
    let second = add_second_enemy(&mut state, MonsterId::ShrinkerBeetle, 20);
    let sunder_h = make_card(&content, state.next_card_handle, CardId::Sunder, PileType::Hand);
    state.next_card_handle += 1;
    state.cards.push(sunder_h);
    state.player.hand.push(sunder_h.handle);
    state.invalidate_pile_hooks(sts2_kernel::state::HOOK_DIRTY_HAND);
    state.player.energy = 3;
    let sunder_idx = state.player.hand.len() - 1;
    play_hand_idx(&mut state, &content, sunder_idx, Some(second));
    assert_eq!(state.player.energy, 3);
}

#[test]
fn defect_token_cards_rocket_punch_compact_and_flak_cannon() {
    let content = Sts2Content;
    let (mut state, enemy) = build_defect_battle_with(
        &content,
        &[
            CardId::RocketPunch,
            CardId::Turbo,
            CardId::Overclock,
            CardId::GunkUp,
            CardId::BoostAway,
            CardId::FightThrough,
            CardId::Compact,
            CardId::FlakCannon,
        ],
        &[CardId::Dazed, CardId::StrikeDefect, CardId::DefendDefect],
        MonsterId::FuzzyWurmCrawler,
        200,
    );

    let rp_h = state.player.hand[0];
    let compact_h = state.player.hand[6];
    upgrade_card(&mut state, &content, compact_h);

    // Play Turbo (index 1): +2 energy, generates Void in discard -> RocketPunch reduces cost 2 -> 1!
    let e_before = state.player.energy;
    play_hand_idx(&mut state, &content, 1, None);
    assert_eq!(state.player.energy, e_before + 2);
    assert_eq!(ordering::resolve_energy_cost(&mut state, &content, rp_h).unwrap(), 1);

    // Play Overclock: draws Dazed & StrikeDefect, generates Burn -> RocketPunch cost reduces 1 -> 0!
    play_hand_idx(&mut state, &content, 1, None);
    assert_eq!(ordering::resolve_energy_cost(&mut state, &content, rp_h).unwrap(), 0);
    assert!(state.player.hand.iter().any(|&h| state.card(h).unwrap().id == CardId::Dazed));

    // Play RocketPunch at 0 cost! (after play, AddUntilPlayed clears)
    let e_before_rp = state.player.energy;
    play_hand_idx(&mut state, &content, 0, Some(enemy));
    assert_eq!(state.player.energy, e_before_rp);
    assert_eq!(ordering::resolve_energy_cost(&mut state, &content, rp_h).unwrap(), 2);

    // Play Compact+ while Dazed is in Hand -> transforms Dazed into Fuel+
    play_hand_idx(&mut state, &content, 3, None);
    let fuel_h = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::Fuel)
        .unwrap();
    assert_eq!(state.card(fuel_h).unwrap().upgrade_level, 1);

    // Play GunkUp, BoostAway, FightThrough -> adds Slimed, Dazed, 2x Wound to discard (total 6 Status across piles)
    play_hand_idx(&mut state, &content, 0, Some(enemy));
    play_hand_idx(&mut state, &content, 0, None);
    play_hand_idx(&mut state, &content, 0, None);

    // Play FlakCannon -> exhausts all 6 Status cards and hits 6 times for 8 = 48
    let hp_before = state.creature(enemy).unwrap().current_hp;
    let flak_idx = state
        .player
        .hand
        .iter()
        .position(|&h| state.card(h).unwrap().id == CardId::FlakCannon)
        .unwrap();
    play_hand_idx(&mut state, &content, flak_idx, None);
    assert_eq!(state.creature(enemy).unwrap().current_hp, hp_before - 48);
}

#[test]
fn defect_chaos_white_noise_uproar_voltaic_and_tesla_coil() {
    let content = Sts2Content;
    let (mut state, enemy) = build_defect_battle(
        &[
            CardId::Chaos,
            CardId::Zap,
            CardId::Voltaic,
            CardId::TeslaCoil,
            CardId::WhiteNoise,
            CardId::Uproar,
        ],
        &[CardId::ColdSnap, CardId::DefendDefect],
        200,
    );
    let chaos_h = state.player.hand[0];
    upgrade_card(&mut state, &content, chaos_h);
    let tesla_h = state.player.hand[3];
    upgrade_card(&mut state, &content, tesla_h);

    // Chaos+ channels 2 random orbs using CombatOrbs RNG
    play_hand_idx(&mut state, &content, 0, None);
    assert_eq!(state.player.orbs.len(), 2);

    // Zap channels LightningOrb
    play_hand_idx(&mut state, &content, 0, None);
    let lightning_channeled = state
        .semantic_history
        .orb_channels
        .iter()
        .filter(|(_, _, o)| *o == OrbId::LightningOrb)
        .count();

    // Voltaic channels `lightning_channeled` LightningOrbs
    play_hand_idx(&mut state, &content, 0, None);
    let lightning_after_voltaic = state
        .semantic_history
        .orb_channels
        .iter()
        .filter(|(_, _, o)| *o == OrbId::LightningOrb)
        .count();
    assert_eq!(lightning_after_voltaic, lightning_channeled * 2);

    // TeslaCoil+ triggers passive x2 on every LightningOrb in queue targeting enemy
    let lightning_in_queue = state
        .player
        .orbs
        .iter()
        .filter(|o| o.id == OrbId::LightningOrb)
        .count() as i32;
    let hp_before = state.creature(enemy).unwrap().current_hp;
    play_hand_idx(&mut state, &content, 0, Some(enemy));
    assert_eq!(
        state.creature(enemy).unwrap().current_hp,
        hp_before - 4 - (lightning_in_queue * 2 * 3)
    );

    // WhiteNoise generates a free-this-turn Power card in Hand
    let hand_before_wn = state.player.hand.len();
    play_hand_idx(&mut state, &content, 0, None);
    assert_eq!(state.player.hand.len(), hand_before_wn); // -1 played +1 generated
    let gen_h = *state.player.hand.last().unwrap();
    assert_eq!(
        content.card_meta(state.card(gen_h).unwrap().id).unwrap().card_type,
        CardType::Power
    );
    assert_eq!(ordering::resolve_energy_cost(&mut state, &content, gen_h).unwrap(), 0);

    // Uproar hits 2x6 and autoplays ColdSnap from Draw (channeling FrostOrb)
    let hp_before_uproar = state.creature(enemy).unwrap().current_hp;
    play_hand_idx(&mut state, &content, 0, Some(enemy));
    assert!(state.creature(enemy).unwrap().current_hp <= hp_before_uproar - 18);
    assert_eq!(state.player.orbs.last().map(|o| o.id), Some(OrbId::FrostOrb));
    assert!(state.player.discard.iter().any(|&h| state.card(h).unwrap().id == CardId::ColdSnap));
}

#[test]
fn defect_powers_and_remaining_cards_smoke() {
    let content = Sts2Content;
    let (mut state, enemy) = build_defect_battle(
        &[
            CardId::BiasedCognition,
            CardId::Defragment,
            CardId::FocusedStrike,
            CardId::Hotfix,
            CardId::Hyperbeam,
            CardId::Coolant,
            CardId::CreativeAi,
            CardId::EchoForm,
            CardId::Feral,
            CardId::Hailstorm,
        ],
        &[],
        300,
    );
    state.player.energy = 20;
    for _ in 0..10 {
        let meta = content
            .card_meta(state.card(state.player.hand[0]).unwrap().id)
            .unwrap();
        let tgt = if meta.target_type == sts2_types::TargetType::AnyEnemy {
            Some(enemy)
        } else {
            None
        };
        play_hand_idx(&mut state, &content, 0, tgt);
    }

    let (mut state2, enemy2) = build_defect_battle(
        &[
            CardId::Iteration,
            CardId::Loop,
            CardId::MachineLearning,
            CardId::SignalBoost,
            CardId::Smokestack,
            CardId::Spinner,
            CardId::Storm,
            CardId::Subroutine,
            CardId::Synchronize,
            CardId::Synthesis,
        ],
        &[],
        300,
    );
    state2.player.energy = 20;
    state2.player.orbs.push(sts2_kernel::OrbInst {
        id: OrbId::FrostOrb,
        raw_evoke_value: None,
        raw_passive_value: None,
    });
    for _ in 0..10 {
        let meta = content
            .card_meta(state2.card(state2.player.hand[0]).unwrap().id)
            .unwrap();
        let tgt = if meta.target_type == sts2_types::TargetType::AnyEnemy {
            Some(enemy2)
        } else {
            None
        };
        play_hand_idx(&mut state2, &content, 0, tgt);
    }

    let (mut state3, enemy3) = build_defect_battle(
        &[
            CardId::Thunder,
            CardId::TrashToTreasure,
            CardId::BallLightning,
            CardId::BeamCell,
            CardId::BootSequence,
            CardId::Buffer,
            CardId::ChargeBattery,
            CardId::Chill,
            CardId::ColdSnap,
            CardId::ConsumingShadow,
        ],
        &[],
        300,
    );
    state.player.energy = 20;
    for _ in 0..10 {
        let meta = content
            .card_meta(state3.card(state3.player.hand[0]).unwrap().id)
            .unwrap();
        let tgt = if meta.target_type == sts2_types::TargetType::AnyEnemy {
            Some(enemy3)
        } else {
            None
        };
        play_hand_idx(&mut state3, &content, 0, tgt);
    }

    let (mut state4, enemy4) = build_defect_battle(
        &[
            CardId::Coolheaded,
            CardId::DoubleEnergy,
            CardId::Dualcast,
            CardId::Fusion,
            CardId::Glacier,
            CardId::Glasswork,
            CardId::IceLance,
            CardId::Leap,
            CardId::LightningRod,
            CardId::MeteorStrike,
        ],
        &[CardId::StrikeDefect, CardId::DefendDefect],
        1000,
    );
    state4.player.energy = 20;
    for _ in 0..10 {
        let meta = content
            .card_meta(state4.card(state4.player.hand[0]).unwrap().id)
            .unwrap();
        let tgt = if meta.target_type == sts2_types::TargetType::AnyEnemy {
            Some(enemy4)
        } else {
            None
        };
        play_hand_idx(&mut state4, &content, 0, tgt);
    }

    let (mut state5, enemy5) = build_defect_battle(
        &[
            CardId::Modded,
            CardId::MomentumStrike,
            CardId::Null,
            CardId::Quadcast,
            CardId::Refract,
            CardId::ShadowShield,
            CardId::Skim,
            CardId::StrikeDefect,
            CardId::Supercritical,
            CardId::SweepingBeam,
        ],
        &[
            CardId::StrikeDefect,
            CardId::DefendDefect,
            CardId::Zap,
            CardId::Leap,
            CardId::Claw,
        ],
        1000,
    );
    state5.player.energy = 20;
    for _ in 0..10 {
        let meta = content
            .card_meta(state5.card(state5.player.hand[0]).unwrap().id)
            .unwrap();
        let tgt = if meta.target_type == sts2_types::TargetType::AnyEnemy {
            Some(enemy5)
        } else {
            None
        };
        play_hand_idx(&mut state5, &content, 0, tgt);
    }

    // Multiplayer-only Defect cards
    let (mut state6, _) = build_defect_battle(
        &[
            CardId::EnergySurge,
            CardId::Hibernate,
            CardId::OneForAll,
            CardId::Ignition,
            CardId::ImitationLearning,
        ],
        &[],
        100,
    );
    let ally = state6.player.creature_handle;
    play_hand_idx(&mut state6, &content, 0, None);
    play_hand_idx(&mut state6, &content, 0, None);
    play_hand_idx(&mut state6, &content, 0, None);
    // AnyAlly in single-player cannot be played via play_card without an external ally, so test on_play directly
    let ig_h = state6.player.hand[0];
    let ig_ctx = CardPlayContext {
        card: ig_h,
        target: Some(ally),
        result_pile: PileType::Exhaust,
        is_auto_play: false,
        play_index: 0,
        play_count: 1,
        play_ordinal: 1,
        energy_spent: 1,
        energy_value: 1,
        stars_spent: 0,
        star_value: 0,
    };
    let mut cq = SmallVec::new();
    content.card_on_play(&mut state6, &ig_ctx, &mut cq, 0).unwrap();
    let il_h = state6.player.hand[1];
    let il_ctx = CardPlayContext {
        card: il_h,
        target: Some(ally),
        ..ig_ctx
    };
    content.card_on_play(&mut state6, &il_ctx, &mut cq, 0).unwrap();
    assert!(state6
        .player_creature()
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::ImitationLearningPower && p.amount == 2));
}
