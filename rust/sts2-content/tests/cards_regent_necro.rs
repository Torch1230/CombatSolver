use smallvec::SmallVec;
use std::collections::BTreeMap;
use std::sync::Arc;
use sts2_content::Sts2Content;
use sts2_kernel::commands::{card_pile, creature, osty, power};
use sts2_kernel::content_dispatch::ContentDispatcher;
use sts2_kernel::{
    ordering, ArrowSlotInst, BattleState, CardHandle, CardInst, CardTempFlags, CombatEngine,
    CreatureHandle, CreatureInst, FieldValue, MonsterInst, PlayerCounters, PlayerInst, RelicInst,
    RootStaticContext, SemanticHistory, StepOutcome,
};
use sts2_types::generated_ids::{
    generated_card_meta, CARD_POOL_NECROBINDER, CARD_POOL_REGENT,
};
use sts2_types::{
    ArrowStatus, CardId, CardKeywords, CardPilePosition, CharacterId, CombatSide, Dec, GameMode,
    MonsterId, PileType, PlayerRngSet, PlayerTurnPhase, PowerId, RelicId, Rng, RoomType,
    RunRngSet,
};

fn build_battle(
    character: CharacterId,
    hand_ids: &[CardId],
    draw_ids: &[CardId],
    discard_ids: &[CardId],
    relics: &[RelicId],
    enemy_count: usize,
    enemy_hp: i32,
) -> (BattleState, Vec<CreatureHandle>) {
    let run_rngs = RunRngSet::from_string_seed("W2TESTSEED");
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc_handle = CreatureHandle(1);

    let mut cards = Vec::new();
    let mut next_h = 1u32;
    let mut hand = SmallVec::new();
    let mut draw = SmallVec::new();
    let mut discard = SmallVec::new();
    let mut deck = SmallVec::new();

    let mut push_card = |cid: CardId, pile: PileType, target_vec: &mut SmallVec<[CardHandle; 32]>| {
        let meta = generated_card_meta(cid);
        let h = CardHandle(next_h);
        next_h += 1;
        let dh = CardHandle(next_h);
        next_h += 1;
        let mut temp_flags = CardTempFlags::empty();
        if meta.flags.contains(sts2_types::CardStaticFlags::IS_X_ENERGY_COST) {
            temp_flags |= CardTempFlags::COSTS_X_ENERGY;
        }
        cards.push(CardInst {
            handle: dh,
            id: cid,
            upgrade_level: 0,
            canonical_base_energy_cost: meta.canonical_energy_cost,
            base_energy_cost: meta.canonical_energy_cost,
            captured_energy_x_value: 0,
            base_replay_count: 0,
            floor_added_to_deck: Some(1),
            deck_card_handle: Some(dh),
            clone_of_handle: None,
            temp_star_cost_override_this_turn: None,
            temp_cost_override_this_turn: None,
            temp_cost_override_this_turn_or_until_played: None,
            temp_cost_override_this_combat: None,
            local_keywords: meta.base_keywords,
            keywords_added_from_outside: CardKeywords::empty(),
            keywords_removed_from_outside: CardKeywords::empty(),
            temp_flags,
            affliction: None,
            ext_index: None,
            pile: PileType::Deck,
            removed_from_state: false,
            transform_into_after_play: None,
        });
        deck.push(dh);

        cards.push(CardInst {
            handle: h,
            id: cid,
            upgrade_level: 0,
            canonical_base_energy_cost: meta.canonical_energy_cost,
            base_energy_cost: meta.canonical_energy_cost,
            captured_energy_x_value: 0,
            base_replay_count: 0,
            floor_added_to_deck: Some(1),
            deck_card_handle: Some(dh),
            clone_of_handle: None,
            temp_star_cost_override_this_turn: None,
            temp_cost_override_this_turn: None,
            temp_cost_override_this_turn_or_until_played: None,
            temp_cost_override_this_combat: None,
            local_keywords: meta.base_keywords,
            keywords_added_from_outside: CardKeywords::empty(),
            keywords_removed_from_outside: CardKeywords::empty(),
            temp_flags,
            affliction: None,
            ext_index: None,
            pile,
            removed_from_state: false,
            transform_into_after_play: None,
        });
        target_vec.push(h);
    };

    let mut tmp_hand = SmallVec::new();
    for &cid in hand_ids {
        push_card(cid, PileType::Hand, &mut tmp_hand);
    }
    hand.extend(tmp_hand);
    for &cid in draw_ids {
        push_card(cid, PileType::Draw, &mut draw);
    }
    for &cid in discard_ids {
        push_card(cid, PileType::Discard, &mut discard);
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

    let mut creatures = vec![player_creature];
    let mut enemy_handles = SmallVec::new();
    let mut enemy_vec = Vec::new();
    for i in 0..enemy_count {
        let eh = CreatureHandle((i + 2) as u32);
        enemy_handles.push(eh);
        enemy_vec.push(eh);
        creatures.push(CreatureInst {
            handle: eh,
            combat_id: Some((i + 1) as u32),
            side: CombatSide::Enemy,
            slot_name: Some(format!("slot_{i}")),
            is_player: false,
            pet_owner_player_index: None,
            monster_id: Some(MonsterId::FuzzyWurmCrawler),
            current_hp: enemy_hp,
            max_hp: enemy_hp,
            block: 0,
            cumulative_hp_lost: 0,
            is_detached: false,
            monster: Some(MonsterInst {
                monster_id: MonsterId::FuzzyWurmCrawler,
                rng: Rng::from_seed(run_rngs.seed + 1 + i as u64),
                is_performing_move: false,
                spawned_this_turn: false,
                current_state_id: "FIRST_ACID_GOOP".to_owned(),
                next_move_id: Some("FIRST_ACID_GOOP".to_owned()),
                next_move_can_transition_away: true,
                performed_first_move: false,
                state_log_ids: SmallVec::new(),
                transient_move_follow_up_state_id: None,
                setup_fields: BTreeMap::new(),
                private_fields: BTreeMap::new(),
            }),
            powers: SmallVec::new(),
        });
    }

    let player = PlayerInst {
        creature_handle: pc_handle,
        current_hp: character.starting_hp(),
        max_hp: character.starting_hp(),
        block: 0,
        cumulative_hp_lost: 0,
        gold: 99,
        max_energy: 10,
        base_orb_slot_count: character.base_orb_slot_count(),
        is_active_for_hooks: true,
        can_use_or_remove_potions: true,
        energy: 10,
        stars: 10,
        phase: PlayerTurnPhase::Play,
        turn_number: 1,
        counters: PlayerCounters::default(),
        hand,
        draw,
        discard,
        exhaust: SmallVec::new(),
        play: SmallVec::new(),
        deck,
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
            encounter_slots: (0..enemy_count).map(|i| format!("slot_{i}")).collect(),
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
        is_in_progress: true,
        is_starting: false,
        won: false,
        is_player_extra_turn: false,
        gold_was_stolen: false,
        end_player_turn_requested: false,
        card_action_boundary_depth: 0,
        next_creature_id: (enemy_count + 1) as u32,
        initial_encounter_monster_count: enemy_count,
        next_card_handle: next_h,
        next_creature_handle: (enemy_count + 2) as u32,
        next_power_handle: 1,
        next_attack_id: 1,
        run_rngs,
        player_rngs,
        player,
        cards,
        card_extensions: Vec::new(),
        creatures,
        power_extensions: Vec::new(),
        ally_handles: smallvec::smallvec![pc_handle],
        enemy_handles,
        escaped_handles: SmallVec::new(),
        removed_handles: SmallVec::new(),
        spawned_enemy_handles: SmallVec::new(),
        pet_handles: SmallVec::new(),
        damage_history: Vec::new(),
        semantic_history: SemanticHistory::default(),
        pending: None,
        vakuu_auto_select_depth: 0,
    };
    (state, enemy_vec)
}

#[test]
fn all_182_regent_and_necrobinder_cards_are_supported_and_upgradeable() {
    let content = Sts2Content;
    assert_eq!(CARD_POOL_REGENT.len(), 91);
    assert_eq!(CARD_POOL_NECROBINDER.len(), 91);

    for &cid in CARD_POOL_REGENT.iter().chain(CARD_POOL_NECROBINDER.iter()) {
        let meta = content.card_meta(cid).unwrap();
        let (mut state, _) = build_battle(
            CharacterId::Regent,
            &[cid],
            &[],
            &[],
            &[],
            1,
            50,
        );
        let h = state.player.hand[0];
        let playable = content.card_is_playable(&state, h).unwrap();
        if cid != CardId::HighFive {
            assert!(playable, "{cid:?} should be playable");
        } else {
            assert!(!playable, "HighFive requires Osty alive");
        }
        let _ = content.card_comparison_base_values(&state, h).unwrap();
        sts2_kernel::commands::card::upgrade(&mut state, &content, h).unwrap();
        assert_eq!(state.card(h).unwrap().upgrade_level, meta.max_upgrade_level);
    }
}

#[test]
fn regent_stars_x_costs_counters_and_hooks() {
    let content = Sts2Content;

    // Alignment (0E, 2S -> +2 energy) & RoyalGamble (0E, 5S -> +9 stars)
    let (mut state, enemies) = build_battle(
        CharacterId::Regent,
        &[CardId::Alignment, CardId::RoyalGamble, CardId::Radiate],
        &[],
        &[],
        &[],
        2,
        100,
    );
    state.player.stars = 5;
    state.player.energy = 1;
    let align_h = state.player.hand[0];
    let gamble_h = state.player.hand[1];
    let radiate_h = state.player.hand[2];
    let mut choices = SmallVec::new();

    CombatEngine::play_card(&mut state, &content, align_h, None, &mut choices).unwrap();
    assert_eq!(state.player.stars, 3);
    assert_eq!(state.player.energy, 3);

    // RoyalGamble costs 5 stars, we have 3 -> not playable yet
    assert!(!CombatEngine::can_play_card(&mut state, &content, gamble_h).unwrap());
    state.player.stars = 5;
    CombatEngine::play_card(&mut state, &content, gamble_h, None, &mut choices).unwrap();
    assert_eq!(state.player.stars, 9);
    assert_eq!(state.player.counters.stars_gained_this_turn, Dec::from_i32(9));

    // Radiate hits all enemies 9 times for 3 each = 27 damage each
    CombatEngine::play_card(&mut state, &content, radiate_h, None, &mut choices).unwrap();
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, 73);
    assert_eq!(state.creature(enemies[1]).unwrap().current_hp, 73);

    // Stardust X-star + ChemicalX
    let (mut s2, e2) = build_battle(
        CharacterId::Regent,
        &[CardId::Stardust, CardId::HeavenlyDrill],
        &[],
        &[],
        &[RelicId::ChemicalX],
        1,
        200,
    );
    s2.player.stars = 3;
    s2.player.energy = 2;
    let stardust_h = s2.player.hand[0];
    let drill_h = s2.player.hand[1];
    // Stardust: 3 + 2 (ChemX) = 5 hits * 5 = 25 dmg
    CombatEngine::play_card(&mut s2, &content, stardust_h, None, &mut choices).unwrap();
    assert_eq!(s2.player.stars, 0);
    assert_eq!(s2.creature(e2[0]).unwrap().current_hp, 175);
    // HeavenlyDrill: 2 + 2 (ChemX) = 4 (>=4 -> *2 = 8 hits) * 8 = 64 dmg
    CombatEngine::play_card(&mut s2, &content, drill_h, Some(e2[0]), &mut choices).unwrap();
    assert_eq!(s2.player.energy, 0);
    assert_eq!(s2.creature(e2[0]).unwrap().current_hp, 175 - 64);
}

#[test]
fn regent_forge_sovereign_blade_and_summon_forth() {
    let content = Sts2Content;
    let (mut state, enemies) = build_battle(
        CharacterId::Regent,
        &[
            CardId::TheSmith,
            CardId::BeatIntoShape,
            CardId::SummonForth,
            CardId::SeekingEdge,
        ],
        &[],
        &[],
        &[],
        1,
        100,
    );
    let smith_h = state.player.hand[0];
    let beat_h = state.player.hand[1];
    let summon_h = state.player.hand[2];
    let edge_h = state.player.hand[3];
    let mut choices = SmallVec::new();

    // TheSmith forges 30 -> creates SovereignBlade in hand with _damage = 10 + 30 = 40
    CombatEngine::play_card(&mut state, &content, smith_h, None, &mut choices).unwrap();
    let blade_h = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::SovereignBlade)
        .unwrap();
    let blade_dmg = state
        .card_ext(state.card(blade_h).unwrap().ext_index)
        .unwrap()
        .private_fields
        .get("SovereignBlade._damage")
        .unwrap();
    assert_eq!(*blade_dmg, FieldValue::Decimal(Dec::from_i32(40)));

    // Move blade to discard, then SummonForth brings it back to hand and forges +8 -> 48
    card_pile::add_sync(&mut state, blade_h, PileType::Discard, CardPilePosition::Bottom);
    CombatEngine::play_card(&mut state, &content, summon_h, None, &mut choices).unwrap();
    assert!(state.player.hand.contains(&blade_h));
    assert_eq!(
        state
            .card_ext(state.card(blade_h).unwrap().ext_index)
            .unwrap()
            .private_fields
            .get("SovereignBlade._damage"),
        Some(&FieldValue::Decimal(Dec::from_i32(48)))
    );

    // BeatIntoShape: first attack hit on enemy -> forges 5 -> 53; play second BeatIntoShape -> 1 prior hit -> forges 5 + 5*1 = 10 -> 63
    CombatEngine::play_card(&mut state, &content, beat_h, Some(enemies[0]), &mut choices).unwrap();
    assert_eq!(
        state
            .card_ext(state.card(blade_h).unwrap().ext_index)
            .unwrap()
            .private_fields
            .get("SovereignBlade._damage"),
        Some(&FieldValue::Decimal(Dec::from_i32(53)))
    );

    CombatEngine::play_card(&mut state, &content, edge_h, None, &mut choices).unwrap();
    assert_eq!(
        state
            .card_ext(state.card(blade_h).unwrap().ext_index)
            .unwrap()
            .private_fields
            .get("SovereignBlade._damage"),
        Some(&FieldValue::Decimal(Dec::from_i32(60)))
    );
}

#[test]
fn regent_hooks_choices_void_form_and_monologue() {
    let content = Sts2Content;

    // KinglyPunch, KinglyKick, ParticleWall, ShiningStrike, MakeItSo, LunarBlast, CrescentSpear, Supermassive
    let (mut state, enemies) = build_battle(
        CharacterId::Regent,
        &[
            CardId::ParticleWall,
            CardId::ShiningStrike,
            CardId::CloakOfStars,
            CardId::LunarBlast,
            CardId::CrescentSpear,
            CardId::Monologue,
        ],
        &[CardId::KinglyKick, CardId::KinglyPunch],
        &[CardId::MakeItSo],
        &[],
        1,
        200,
    );
    let pw_h = state.player.hand[0];
    let ss_h = state.player.hand[1];
    let cloak_h = state.player.hand[2];
    let lb_h = state.player.hand[3];
    let cs_h = state.player.hand[4];
    let mono_h = state.player.hand[5];
    let kick_h = state.player.draw[0];
    let punch_h = state.player.draw[1];
    let make_h = state.player.discard[0];

    let mut choices = SmallVec::new();
    // Draw KinglyKick and KinglyPunch -> KinglyKick cost becomes 3, KinglyPunch damage becomes 12
    let _ = card_pile::draw(&mut state, &content, 2, false).unwrap();
    assert_eq!(ordering::resolve_energy_cost(&mut state, &content, kick_h).unwrap(), 3);
    assert_eq!(
        content.card_comparison_base_values(&state, punch_h).unwrap().as_slice(),
        &[("Damage", Dec::from_i32(12))]
    );

    // ParticleWall returns to hand; ShiningStrike returns to draw top; CloakOfStars is 3rd Skill -> MakeItSo returns from discard!
    CombatEngine::play_card(&mut state, &content, pw_h, None, &mut choices).unwrap();
    assert!(state.player.hand.contains(&pw_h));
    CombatEngine::play_card(&mut state, &content, cloak_h, None, &mut choices).unwrap();
    assert_eq!(state.player.counters.skill_cards_played_this_turn, 2);
    assert!(state.player.discard.contains(&make_h));
    CombatEngine::play_card(&mut state, &content, pw_h, None, &mut choices).unwrap();
    assert_eq!(state.player.counters.skill_cards_played_this_turn, 3);
    assert!(state.player.hand.contains(&make_h));

    CombatEngine::play_card(&mut state, &content, ss_h, Some(enemies[0]), &mut choices).unwrap();
    assert_eq!(state.player.draw.first().copied(), Some(ss_h));

    // LunarBlast: 3 skills played -> 3 hits * 4 = 12
    let hp_before = state.creature(enemies[0]).unwrap().current_hp;
    CombatEngine::play_card(&mut state, &content, lb_h, Some(enemies[0]), &mut choices).unwrap();
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, hp_before - 12);

    // CrescentSpear: star cards in piles = ParticleWall, CloakOfStars, CrescentSpear = 3 -> 8 + 2*3 = 14 dmg
    let hp_before = state.creature(enemies[0]).unwrap().current_hp;
    CombatEngine::play_card(&mut state, &content, cs_h, Some(enemies[0]), &mut choices).unwrap();
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, hp_before - 14);

    // Monologue + VoidForm (ends turn)
    CombatEngine::play_card(&mut state, &content, mono_h, None, &mut choices).unwrap();
    assert!(state
        .creature(state.player.creature_handle)
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::MonologuePower));

    // Choices: Glimmer, Quasar, Begone, Charge, DecisionsDecisions, IAmInvincible
    let (mut sc, ec) = build_battle(
        CharacterId::Regent,
        &[
            CardId::Begone,
            CardId::Glimmer,
            CardId::Quasar,
            CardId::DecisionsDecisions,
            CardId::VoidForm,
            CardId::DefendRegent,
        ],
        &[CardId::IAmInvincible, CardId::StrikeRegent, CardId::StrikeRegent],
        &[],
        &[],
        1,
        100,
    );
    let begone_h = sc.player.hand[0];
    let glimmer_h = sc.player.hand[1];
    let quasar_h = sc.player.hand[2];
    let dec_h = sc.player.hand[3];
    let void_h = sc.player.hand[4];
    let inv_h = sc.player.draw[0];

    // Playing Begone pauses to pick a hand card -> transforms into MinionStrike
    let out = CombatEngine::play_card(&mut sc, &content, begone_h, None, &mut choices).unwrap();
    assert!(matches!(out, StepOutcome::NeedsChoice(_)));
    CombatEngine::resume_choice(&mut sc, &content, &[4]).unwrap(); // transform DefendRegent (now index 4 after Begone moved to play)
    assert!(sc
        .player
        .hand
        .iter()
        .any(|&h| sc.card(h).unwrap().id == CardId::MinionStrike));

    // IAmInvincible is on top of draw -> Glimmer draws 3 (including IAmInvincible) and puts the last hand card back
    let out_g = CombatEngine::play_card(&mut sc, &content, glimmer_h, None, &mut choices).unwrap();
    assert!(matches!(out_g, StepOutcome::NeedsChoice(_)));
    let put_back_idx = sc.pending.as_ref().unwrap().request.candidates.len() - 1;
    CombatEngine::resume_choice(&mut sc, &content, &[put_back_idx]).unwrap();

    // Quasar: cancelable=true -> pick 0th offer
    let out_q = CombatEngine::play_card(&mut sc, &content, quasar_h, None, &mut choices).unwrap();
    assert!(matches!(out_q, StepOutcome::NeedsChoice(_)));
    CombatEngine::resume_choice(&mut sc, &content, &[0]).unwrap();

    // Put IAmInvincible back on top of draw, play VoidForm -> VoidForm ends turn and triggers IAmInvincible?
    card_pile::add_sync(&mut sc, inv_h, PileType::Draw, CardPilePosition::Top);
    CombatEngine::play_card(&mut sc, &content, void_h, None, &mut choices).unwrap();
    assert_eq!(sc.round_number, 2);

    // Verify VoidForm+ empty local_keywords survives snapshot export/import round-trip
    let vf_plus = card_pile::create_card_in_combat(&mut sc, &content, CardId::VoidForm, 1).unwrap();
    card_pile::add_sync(&mut sc, vf_plus, PileType::Hand, CardPilePosition::Bottom);
    assert_eq!(sc.card(vf_plus).unwrap().local_keywords, CardKeywords::empty());
    let snap = sts2_kernel::snapshot::export_live_state(&sc).unwrap();
    let restored = sts2_kernel::snapshot::import_live_state(&snap, &content).unwrap();
    assert_eq!(restored.card(vf_plus).unwrap().local_keywords, CardKeywords::empty());
    let _ = (dec_h, ec);
}

#[test]
fn regent_and_necro_remaining_choice_and_special_cards() {
    let content = Sts2Content;
    let mut choices = SmallVec::new();

    // Regent: Charge, CosmicIndifference, PhotonCut, HeirloomHammer, Tutor, CrashLanding, Supermassive
    {
        let (mut state, enemies) = build_battle(
            CharacterId::Regent,
            &[
                CardId::Charge,
                CardId::CosmicIndifference,
                CardId::PhotonCut,
                CardId::HeirloomHammer,
                CardId::Tutor,
                CardId::CollisionCourse,
                CardId::Supermassive,
                CardId::Shiv,
            ],
            &[CardId::StrikeRegent, CardId::DefendRegent, CardId::Venerate],
            &[CardId::Glow],
            &[],
            1,
            200,
        );
        let charge_h = state.player.hand[0];
        let ci_h = state.player.hand[1];
        let pc_h = state.player.hand[2];
        let hh_h = state.player.hand[3];
        let tutor_h = state.player.hand[4];
        let cc_h = state.player.hand[5];
        let sm_h = state.player.hand[6];

        // Charge transforms 2 draw cards into MinionDiveBomb
        let out = CombatEngine::play_card(&mut state, &content, charge_h, None, &mut choices).unwrap();
        assert!(matches!(out, StepOutcome::NeedsChoice(_)));
        CombatEngine::resume_choice(&mut state, &content, &[0, 1]).unwrap();
        assert_eq!(
            state
                .player
                .draw
                .iter()
                .filter(|&&h| state.card(h).unwrap().id == CardId::MinionDiveBomb)
                .count(),
            2
        );

        // CosmicIndifference: 6 block + move Glow (index 0) from discard to top of draw
        let out_ci = CombatEngine::play_card(&mut state, &content, ci_h, None, &mut choices).unwrap();
        assert!(matches!(out_ci, StepOutcome::NeedsChoice(_)));
        CombatEngine::resume_choice(&mut state, &content, &[0]).unwrap();
        assert_eq!(state.player.block, 6);
        assert_eq!(state.card(state.player.draw[0]).unwrap().id, CardId::Glow);

        // CollisionCourse generates Debris (visually colorless) into hand; total generated = 2 (Charge) + 1 (Debris) = 3
        CombatEngine::play_card(&mut state, &content, cc_h, Some(enemies[0]), &mut choices).unwrap();
        assert_eq!(state.player.counters.cards_generated_this_combat, 3);

        // Supermassive: 5 + 3 * 3 = 14 damage
        let hp_before = state.creature(enemies[0]).unwrap().current_hp;
        CombatEngine::play_card(&mut state, &content, sm_h, Some(enemies[0]), &mut choices).unwrap();
        assert_eq!(state.creature(enemies[0]).unwrap().current_hp, hp_before - 14);

        // HeirloomHammer clones Shiv (only visually colorless card -> auto-selected!)
        let hand_before = state.player.hand.len();
        CombatEngine::play_card(&mut state, &content, hh_h, Some(enemies[0]), &mut choices).unwrap();
        assert_eq!(state.player.hand.len(), hand_before); // -1 played +1 cloned Shiv
        assert_eq!(
            state
                .player
                .hand
                .iter()
                .filter(|&&h| state.card(h).unwrap().id == CardId::Shiv)
                .count(),
            2
        );

        // PhotonCut: 10 dmg, draw 1 (Glow), put 1 non-Tutor hand card on top of draw
        let out_pc = CombatEngine::play_card(&mut state, &content, pc_h, Some(enemies[0]), &mut choices).unwrap();
        assert!(matches!(out_pc, StepOutcome::NeedsChoice(_)));
        let pc_idx = state
            .pending
            .as_ref()
            .unwrap()
            .request
            .candidates
            .iter()
            .position(|&h| h != tutor_h)
            .unwrap();
        CombatEngine::resume_choice(&mut state, &content, &[pc_idx]).unwrap();

        // Tutor is AnyAlly (unplayable in solo without an ally, playable once an ally is present)
        assert!(!CombatEngine::can_play_card(&mut state, &content, tutor_h).unwrap());
        let ally_h = CreatureHandle(99);
        state.creatures.push(CreatureInst {
            handle: ally_h,
            combat_id: Some(99),
            side: CombatSide::Player,
            slot_name: None,
            is_player: true,
            pet_owner_player_index: None,
            monster_id: None,
            current_hp: 50,
            max_hp: 50,
            block: 0,
            cumulative_hp_lost: 0,
            is_detached: false,
            monster: None,
            powers: SmallVec::new(),
        });
        state.ally_handles.push(ally_h);
        let out_tu = CombatEngine::play_card(&mut state, &content, tutor_h, Some(ally_h), &mut choices).unwrap();
        assert!(matches!(out_tu, StepOutcome::NeedsChoice(_)));
        CombatEngine::resume_choice(&mut state, &content, &[0]).unwrap();
    }

    // Necrobinder: Cleanse, Dredge, Graveblast, SculptingStrike, Seance, Snap, Eidolon, Protector, BoneShards, CaptureSpirit, Reave, Squeeze
    {
        let (mut state, enemies) = build_battle(
            CharacterId::Necrobinder,
            &[
                CardId::Bodyguard,
                CardId::Cleanse,
                CardId::Graveblast,
                CardId::SculptingStrike,
                CardId::Seance,
                CardId::Snap,
                CardId::Dredge,
                CardId::Squeeze,
                CardId::Protector,
            ],
            &[CardId::StrikeNecrobinder, CardId::DefendNecrobinder, CardId::Poke],
            &[CardId::Defile, CardId::Wisp],
            &[],
            1,
            300,
        );
        state.player.energy = 20;
        let bg_h = state.player.hand[0];
        let cleanse_h = state.player.hand[1];
        let gb_h = state.player.hand[2];
        let ss_h = state.player.hand[3];
        let seance_h = state.player.hand[4];
        let snap_h = state.player.hand[5];
        let dredge_h = state.player.hand[6];
        let squeeze_h = state.player.hand[7];
        let prot_h = state.player.hand[8];

        CombatEngine::play_card(&mut state, &content, bg_h, None, &mut choices).unwrap();
        // Cleanse: summon +3 (Osty MaxHP=8), exhaust 1 from draw
        let out_c = CombatEngine::play_card(&mut state, &content, cleanse_h, None, &mut choices).unwrap();
        assert!(matches!(out_c, StepOutcome::NeedsChoice(_)));
        CombatEngine::resume_choice(&mut state, &content, &[0]).unwrap();
        assert_eq!(state.player.exhaust.len(), 1);

        // Seance: transform 1 from draw into Soul
        let out_se = CombatEngine::play_card(&mut state, &content, seance_h, None, &mut choices).unwrap();
        assert!(matches!(out_se, StepOutcome::NeedsChoice(_)));
        CombatEngine::resume_choice(&mut state, &content, &[0]).unwrap();
        assert!(state.player.draw.iter().any(|&h| state.card(h).unwrap().id == CardId::Soul));

        // Graveblast: 4 dmg + pick 1 from discard (Defile / Wisp)
        let out_gb = CombatEngine::play_card(&mut state, &content, gb_h, Some(enemies[0]), &mut choices).unwrap();
        assert!(matches!(out_gb, StepOutcome::NeedsChoice(_)));
        CombatEngine::resume_choice(&mut state, &content, &[0]).unwrap();

        // SculptingStrike: 9 dmg + add Ethereal to 1 hand card
        let out_ss = CombatEngine::play_card(&mut state, &content, ss_h, Some(enemies[0]), &mut choices).unwrap();
        assert!(matches!(out_ss, StepOutcome::NeedsChoice(_)));
        CombatEngine::resume_choice(&mut state, &content, &[0]).unwrap();

        // Snap: Osty 7 dmg + add Retain to 1 hand card
        let out_sn = CombatEngine::play_card(&mut state, &content, snap_h, Some(enemies[0]), &mut choices).unwrap();
        assert!(matches!(out_sn, StepOutcome::NeedsChoice(_)));
        CombatEngine::resume_choice(&mut state, &content, &[0]).unwrap();

        // Dredge: pick up to 3 from discard and move to hand
        let out_dr = CombatEngine::play_card(&mut state, &content, dredge_h, None, &mut choices).unwrap();
        if let StepOutcome::NeedsChoice(req) = out_dr {
            let pick: Vec<usize> = (0..req.min_count).collect();
            CombatEngine::resume_choice(&mut state, &content, &pick).unwrap();
        }

        // Protector: 10 + Osty MaxHP (8) = 18 dmg
        let hp_before = state.creature(enemies[0]).unwrap().current_hp;
        CombatEngine::play_card(&mut state, &content, prot_h, Some(enemies[0]), &mut choices).unwrap();
        assert_eq!(state.creature(enemies[0]).unwrap().current_hp, hp_before - 18);

        // Squeeze: 25 + 5 * (other OstyAttack cards in piles)
        let hp_before2 = state.creature(enemies[0]).unwrap().current_hp;
        CombatEngine::play_card(&mut state, &content, squeeze_h, Some(enemies[0]), &mut choices).unwrap();
        assert!(state.creature(enemies[0]).unwrap().current_hp <= hp_before2 - 25);
    }
}

#[test]
fn regent_bombardment_glitterstream_knockout_and_decisions() {
    let content = Sts2Content;
    let (mut state, enemies) = build_battle(
        CharacterId::Regent,
        &[
            CardId::Bombardment,
            CardId::Glitterstream,
            CardId::DecisionsDecisions,
            CardId::GatherLight,
            CardId::KnockoutBlow,
        ],
        &[],
        &[],
        &[],
        2,
        100,
    );
    let bomb_h = state.player.hand[0];
    let glit_h = state.player.hand[1];
    let dec_h = state.player.hand[2];
    let ko_h = state.player.hand[4];
    let mut choices = SmallVec::new();

    // Bombardment deals 18 + 18 = 36 on first play
    CombatEngine::play_card(&mut state, &content, bomb_h, Some(enemies[0]), &mut choices).unwrap();
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, 64);

    // Dexterity +2 -> Glitterstream gives 11+2=13 block and BlockNextTurnPower(5+2=7)
    let owner = state.player.creature_handle;
    power::apply(
        &mut state,
        &content,
        PowerId::DexterityPower,
        owner,
        Dec::TWO,
        Some(owner),
        None,
    )
    .unwrap();
    CombatEngine::play_card(&mut state, &content, glit_h, None, &mut choices).unwrap();
    assert_eq!(state.player.block, 13);
    let bnt = state
        .creature(owner)
        .unwrap()
        .powers
        .iter()
        .find(|p| p.id == PowerId::BlockNextTurnPower)
        .unwrap()
        .amount;
    assert_eq!(bnt, 7);

    // DecisionsDecisions repeats GatherLight 3 times -> +3 stars and 3*(8+2)=30 block
    let stars_before = state.player.stars;
    let out = CombatEngine::play_card(&mut state, &content, dec_h, None, &mut choices).unwrap();
    assert!(matches!(out, StepOutcome::NeedsChoice(_)));
    CombatEngine::resume_choice(&mut state, &content, &[0]).unwrap();
    assert_eq!(state.player.stars, stars_before - 6 + 3);
    assert_eq!(state.player.block, 13 + 30);

    // KnockoutBlow kills enemy[1] set to 20 HP -> +5 stars
    state.creature_mut(enemies[1]).unwrap().current_hp = 20;
    let s_ko = state.player.stars;
    CombatEngine::play_card(&mut state, &content, ko_h, Some(enemies[1]), &mut choices).unwrap();
    assert_eq!(state.player.stars, s_ko + 5);
}

#[test]
fn necrobinder_osty_summon_attack_flatten_fetch_rattle_right_hand() {
    let content = Sts2Content;
    let (mut state, enemies) = build_battle(
        CharacterId::Necrobinder,
        &[
            CardId::Bodyguard,
            CardId::Unleash,
            CardId::Flatten,
            CardId::Fetch,
            CardId::Rattle,
            CardId::RightHandHand,
            CardId::HighFive,
            CardId::Sacrifice,
            CardId::Spur,
        ],
        &[CardId::StrikeNecrobinder, CardId::StrikeNecrobinder],
        &[],
        &[],
        1,
        200,
    );
    let bg_h = state.player.hand[0];
    let unleash_h = state.player.hand[1];
    let flatten_h = state.player.hand[2];
    let fetch_h = state.player.hand[3];
    let rattle_h = state.player.hand[4];
    let rhh_h = state.player.hand[5];
    let hf_h = state.player.hand[6];
    let sac_h = state.player.hand[7];
    let spur_h = state.player.hand[8];
    let mut choices = SmallVec::new();

    // HighFive unplayable while Osty is missing
    assert!(!CombatEngine::can_play_card(&mut state, &content, hf_h).unwrap());

    // Bodyguard summons Osty (5 HP / 5 MaxHP) -> HighFive now playable!
    CombatEngine::play_card(&mut state, &content, bg_h, None, &mut choices).unwrap();
    let osty_h = state.osty_handle().unwrap();
    assert_eq!(state.creature(osty_h).unwrap().current_hp, 5);
    assert!(CombatEngine::can_play_card(&mut state, &content, hf_h).unwrap());

    // Flatten costs 2 before Osty attacks; after Unleash (6+5=11 dmg), Flatten costs 0!
    assert_eq!(ordering::resolve_energy_cost(&mut state, &content, flatten_h).unwrap(), 2);
    CombatEngine::play_card(&mut state, &content, unleash_h, Some(enemies[0]), &mut choices).unwrap();
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, 189);
    assert_eq!(ordering::resolve_energy_cost(&mut state, &content, flatten_h).unwrap(), 0);

    // Fetch deals 3 and draws 1 on first play this turn
    let hand_before = state.player.hand.len();
    CombatEngine::play_card(&mut state, &content, fetch_h, Some(enemies[0]), &mut choices).unwrap();
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, 186);
    assert_eq!(state.player.hand.len(), hand_before); // -1 played +1 drawn

    // Rattle hits 1 + 2 OstyAttacks = 3 times for 7 = 21 dmg
    CombatEngine::play_card(&mut state, &content, rattle_h, Some(enemies[0]), &mut choices).unwrap();
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, 186 - 21);

    // Play RightHandHand (goes to discard); then play 2-cost HighFive -> RightHandHand returns to hand!
    CombatEngine::play_card(&mut state, &content, rhh_h, Some(enemies[0]), &mut choices).unwrap();
    assert!(state.player.discard.contains(&rhh_h));
    CombatEngine::play_card(&mut state, &content, hf_h, None, &mut choices).unwrap();
    assert!(state.player.hand.contains(&rhh_h));

    // Sacrifice kills Osty (MaxHP=5 -> 15 block); Spur revives Osty with 3 HP and heals 5 (capped at MaxHP 3)
    CombatEngine::play_card(&mut state, &content, sac_h, None, &mut choices).unwrap();
    assert!(!state.is_osty_alive());
    assert_eq!(state.player.block, 15);

    CombatEngine::play_card(&mut state, &content, spur_h, None, &mut choices).unwrap();
    assert!(state.is_osty_alive());
    assert_eq!(state.creature(osty_h).unwrap().current_hp, 3);
}

#[test]
fn necrobinder_doom_scythe_banshee_melancholy_misery_and_choices() {
    let content = Sts2Content;
    let (mut state, enemies) = build_battle(
        CharacterId::Necrobinder,
        &[
            CardId::BlightStrike,
            CardId::NoEscape,
            CardId::TimesUp,
            CardId::DeathsDoor,
            CardId::EndOfDays,
            CardId::TheScythe,
            CardId::BansheesCry,
            CardId::Melancholy,
            CardId::Undeath,
        ],
        &[],
        &[],
        &[],
        2,
        100,
    );
    let blight_h = state.player.hand[0];
    let no_esc_h = state.player.hand[1];
    let times_up_h = state.player.hand[2];
    let door_h = state.player.hand[3];
    let eod_h = state.player.hand[4];
    let scythe_h = state.player.hand[5];
    let scythe_deck_h = state.card(scythe_h).unwrap().deck_card_handle.unwrap();
    let banshee_h = state.player.hand[6];
    let mel_h = state.player.hand[7];
    let undeath_h = state.player.hand[8];
    let mut choices = SmallVec::new();

    // BlightStrike: 8 dmg (enemy 100->92) + 8 Doom
    CombatEngine::play_card(&mut state, &content, blight_h, Some(enemies[0]), &mut choices).unwrap();
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, 92);

    // NoEscape: 10 + 5 * floor(8 / 10) = 10 -> total Doom = 18
    CombatEngine::play_card(&mut state, &content, no_esc_h, Some(enemies[0]), &mut choices).unwrap();
    let doom_amt = state
        .creature(enemies[0])
        .unwrap()
        .powers
        .iter()
        .find(|p| p.id == PowerId::DoomPower)
        .unwrap()
        .amount;
    assert_eq!(doom_amt, 18);

    // TimesUp deals 18 dmg (92 -> 74)
    CombatEngine::play_card(&mut state, &content, times_up_h, Some(enemies[0]), &mut choices).unwrap();
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, 74);

    // DeathsDoor: DoomApplied this turn -> 3 * 6 = 18 block
    CombatEngine::play_card(&mut state, &content, door_h, None, &mut choices).unwrap();
    assert_eq!(state.player.block, 18);

    // TheScythe deals 13 dmg, grows +5 on both combat and deck copies!
    CombatEngine::play_card(&mut state, &content, scythe_h, Some(enemies[0]), &mut choices).unwrap();
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, 61);
    assert_eq!(
        content.card_comparison_base_values(&state, scythe_h).unwrap().as_slice(),
        &[("Damage", Dec::from_i32(18))]
    );
    assert_eq!(
        content.card_comparison_base_values(&state, scythe_deck_h).unwrap().as_slice(),
        &[("Damage", Dec::from_i32(18))]
    );

    // Undeath gives 7 block and puts a clone in discard
    CombatEngine::play_card(&mut state, &content, undeath_h, None, &mut choices).unwrap();
    assert_eq!(state.player.block, 25);
    assert!(state
        .player
        .discard
        .iter()
        .any(|&h| state.card(h).unwrap().id == CardId::Undeath));

    // EndOfDays: applies 29 Doom -> enemy[0] has 18+29=47 < 61 (survives), set enemy[1] hp to 20 -> killed immediately by DoomKill!
    state.creature_mut(enemies[1]).unwrap().current_hp = 20;
    CombatEngine::play_card(&mut state, &content, eod_h, None, &mut choices).unwrap();
    assert!(state.creature(enemies[1]).unwrap().is_dead());
    // Melancholy reduced cost by 1 on death (3 -> 2)
    assert_eq!(ordering::resolve_energy_cost(&mut state, &content, mel_h).unwrap(), 2);

    // Play an Ethereal card (Defile) -> BansheesCry drops 9 -> 7
    state.player.energy += 2;
    let pc_h = state.player.creature_handle;
    let defile_h = card_pile::add_generated_card_to_combat(
        &mut state,
        &content,
        CardId::Defile,
        PileType::Hand,
        CardPilePosition::Bottom,
        0,
        Some(pc_h),
    )
    .unwrap();
    CombatEngine::play_card(&mut state, &content, defile_h, Some(enemies[0]), &mut choices).unwrap();
    assert_eq!(ordering::resolve_energy_cost(&mut state, &content, banshee_h).unwrap(), 7);
}

#[test]
fn necrobinder_misery_souls_dirge_and_choices() {
    let content = Sts2Content;
    let (mut state, enemies) = build_battle(
        CharacterId::Necrobinder,
        &[
            CardId::Misery,
            CardId::Dirge,
            CardId::Severance,
            CardId::SoulStorm,
            CardId::Transfigure,
            CardId::DefendNecrobinder,
        ],
        &[CardId::StrikeNecrobinder, CardId::Poke],
        &[CardId::DefendNecrobinder],
        &[],
        2,
        100,
    );
    let misery_h = state.player.hand[0];
    let dirge_h = state.player.hand[1];
    let sev_h = state.player.hand[2];
    let storm_h = state.player.hand[3];
    let trans_h = state.player.hand[4];
    let def_h = state.player.hand[5];
    let mut choices = SmallVec::new();

    // Apply Weak(2) + EnfeeblingTouch(5) + Strength(-5) on enemy[0]; Misery should merge EnfeeblingTouch(-5) into Strength(-5) = 0 (skipped) and copy Weak(2) + EnfeeblingTouch(5) to enemy[1]
    let owner = state.player.creature_handle;
    power::apply(
        &mut state,
        &content,
        PowerId::WeakPower,
        enemies[0],
        Dec::TWO,
        Some(owner),
        None,
    )
    .unwrap();
    power::apply(
        &mut state,
        &content,
        PowerId::EnfeeblingTouchPower,
        enemies[0],
        Dec::from_i32(5),
        Some(owner),
        None,
    )
    .unwrap();
    CombatEngine::play_card(&mut state, &content, misery_h, Some(enemies[0]), &mut choices).unwrap();
    assert!(state
        .creature(enemies[1])
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::WeakPower && p.amount == 2));
    assert!(state
        .creature(enemies[1])
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::EnfeeblingTouchPower && p.amount == 5));
    // StrengthPower should not have been double-subtracted on enemy[1]
    assert_eq!(
        state
            .creature(enemies[1])
            .unwrap()
            .powers
            .iter()
            .find(|p| p.id == PowerId::StrengthPower)
            .map_or(0, |p| p.amount),
        -5
    );

    // Transfigure DefendNecrobinder: cost +1, replay +1
    let out_t = CombatEngine::play_card(&mut state, &content, trans_h, None, &mut choices).unwrap();
    assert!(matches!(out_t, StepOutcome::NeedsChoice(_)));
    let def_idx = state
        .pending
        .as_ref()
        .unwrap()
        .request
        .candidates
        .iter()
        .position(|&h| h == def_h)
        .unwrap();
    CombatEngine::resume_choice(&mut state, &content, &[def_idx]).unwrap();
    assert_eq!(ordering::resolve_energy_cost(&mut state, &content, def_h).unwrap(), 2);
    assert_eq!(state.card(def_h).unwrap().base_replay_count, 1);

    // Severance creates 3 Souls (draw, discard, hand) -> exhaust one -> SoulStorm deals 9 + 4*1 = 13
    CombatEngine::play_card(&mut state, &content, sev_h, Some(enemies[0]), &mut choices).unwrap();
    let hand_soul = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::Soul)
        .unwrap();
    card_pile::exhaust(&mut state, &content, hand_soul, false).unwrap();
    let hp_before = state.creature(enemies[0]).unwrap().current_hp;
    CombatEngine::play_card(&mut state, &content, storm_h, Some(enemies[0]), &mut choices).unwrap();
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, hp_before - 13);

    // Dirge with energy=2 -> summons 3 twice (Osty MaxHP = 6) and generates 2 Souls in draw
    state.player.energy = 2;
    let draw_before = state.player.draw.len();
    CombatEngine::play_card(&mut state, &content, dirge_h, None, &mut choices).unwrap();
    let osty_h = state.osty_handle().unwrap();
    assert_eq!(state.creature(osty_h).unwrap().max_hp, 6);
    assert_eq!(state.player.draw.len(), draw_before + 2);
}

#[test]
fn necrobinder_death_march_pull_from_below_drain_power_and_osty_summon() {
    let content = Sts2Content;
    let (mut state, enemies) = build_battle(
        CharacterId::Necrobinder,
        &[
            CardId::DeathMarch,
            CardId::PullFromBelow,
            CardId::DrainPower,
            CardId::Defile,
        ],
        &[CardId::StrikeNecrobinder, CardId::StrikeNecrobinder],
        &[CardId::Poke, CardId::Bodyguard, CardId::DefendNecrobinder],
        &[],
        1,
        200,
    );
    let dm_h = state.player.hand[0];
    let pfb_h = state.player.hand[1];
    let drain_h = state.player.hand[2];
    let defile_h = state.player.hand[3];
    let mut choices = SmallVec::new();

    // Non-hand draw 2 -> DeathMarch deals 8 + 4*2 = 16
    let _ = card_pile::draw(&mut state, &content, 2, false).unwrap();
    CombatEngine::play_card(&mut state, &content, dm_h, Some(enemies[0]), &mut choices).unwrap();
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, 200 - 16);

    // Play Defile (Ethereal) -> PullFromBelow hits 1 time for 5
    CombatEngine::play_card(&mut state, &content, defile_h, Some(enemies[0]), &mut choices).unwrap();
    CombatEngine::play_card(&mut state, &content, pfb_h, Some(enemies[0]), &mut choices).unwrap();
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, 200 - 16 - 13 - 5);

    // DrainPower deals 10 and upgrades 2 discard cards
    CombatEngine::play_card(&mut state, &content, drain_h, Some(enemies[0]), &mut choices).unwrap();
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, 200 - 16 - 13 - 5 - 10);
    let upgraded_count = state
        .player
        .discard
        .iter()
        .filter(|&&h| state.card(h).unwrap().upgrade_level > 0)
        .count();
    assert_eq!(upgraded_count, 2);

    // Ensure creature/osty imports stay exercised
    let _ = (creature::heal, osty::summon);
}
