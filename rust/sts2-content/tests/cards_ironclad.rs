use smallvec::SmallVec;
use std::sync::Arc;
use sts2_content::Sts2Content;
use sts2_content::cards::ironclad;
use sts2_kernel::commands::card_pile;
use sts2_kernel::{
    BattleState, CardHandle, CardInst, CardTempFlags, CombatEngine, ContentDispatcher,
    CreatureHandle, CreatureInst, FieldValue, MonsterInst, PlayerCounters, PlayerInst,
    PowerInst, RootStaticContext, StepOutcome, ordering,
};
use sts2_types::{
    CARD_POOL_IRONCLAD, CardId, CardKeywords, CardStaticFlags, CharacterId, CombatSide, Dec,
    GameMode, MonsterId, PileType, PlayerRngSet, PlayerTurnPhase, PowerId, Rng, RoomType,
    RunRngSet, generated_card_meta,
};

fn build_ironclad_battle(hand_ids: &[(CardId, i32)], draw_ids: &[(CardId, i32)], discard_ids: &[(CardId, i32)], enemy_hp: i32) -> (BattleState, CreatureHandle) {
    let run_rngs = RunRngSet::from_string_seed("IRONCLAD_TEST_SEED");
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc = CreatureHandle(1);
    let enemy = CreatureHandle(2);
    let content = Sts2Content;

    let mut cards = Vec::new();
    let mut next_h = 1u32;
    let mut push_card = |id: CardId, upg: i32, pile: PileType| -> CardHandle {
        let meta = content.card_meta(id).unwrap_or_else(|_| generated_card_meta(id));
        let h = CardHandle(next_h);
        next_h += 1;
        let mut temp_flags = CardTempFlags::empty();
        if meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST) {
            temp_flags |= CardTempFlags::COSTS_X_ENERGY;
        }
        cards.push(CardInst {
            handle: h,
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
        });
        let _ = upg;
        h
    };

    let mut hand = Vec::new();
    let mut draw = Vec::new();
    let mut discard = Vec::new();
    let mut upgrade_list = Vec::new();

    for &(id, upg) in hand_ids {
        let h = push_card(id, upg, PileType::Hand);
        hand.push(h);
        if upg > 0 {
            upgrade_list.push((h, upg));
        }
    }
    for &(id, upg) in draw_ids {
        let h = push_card(id, upg, PileType::Draw);
        draw.push(h);
        if upg > 0 {
            upgrade_list.push((h, upg));
        }
    }
    for &(id, upg) in discard_ids {
        let h = push_card(id, upg, PileType::Discard);
        discard.push(h);
        if upg > 0 {
            upgrade_list.push((h, upg));
        }
    }

    let player_creature = CreatureInst {
        handle: pc,
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
        handle: enemy,
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
            current_state_id: std::borrow::Cow::Borrowed("INIT_MOVE"),
            next_move_id: Some(std::borrow::Cow::Borrowed("ACID_GOOP")),
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

    let mut state = BattleState {
        static_ctx: Arc::new(RootStaticContext {
            encounter_id: "FUZZY_WURM_CRAWLER_WEAK".to_owned(),
            encounter_is_weak: true,
            room_type: RoomType::Monster,
            encounter_slots: vec!["slot_0".to_owned()],
            game_mode: GameMode::Standard,
            ascension: 0,
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
        player: PlayerInst {
            creature_handle: pc,
            current_hp: 80,
            max_hp: 80,
            block: 0,
            cumulative_hp_lost: 0,
            gold: 99,
            max_energy: 3,
            base_orb_slot_count: 0,
            is_active_for_hooks: true,
            can_use_or_remove_potions: true,
            energy: 10,
            stars: 0,
            phase: PlayerTurnPhase::Play,
            turn_number: 1,
            counters: PlayerCounters::default(),
            hand,
            draw,
            discard,
            exhaust: Vec::new(),
            play: Vec::new(),
            deck: Vec::new(),
            potion_slots: smallvec::smallvec![None, None, None],
            combat_generated_potion_mask: 0,
            orb_capacity: 0,
            orbs: Vec::new(),
            arrows: Vec::new(),
            relics: Vec::new(),
            card_or_potion_effect_depth: 0,
        },
        cards,
        card_extensions: Vec::new(),
        creatures: vec![player_creature, enemy_creature],
        power_extensions: Vec::new(),
        ally_handles: vec![pc],
        enemy_handles: vec![enemy],
        escaped_handles: SmallVec::new(),
        removed_handles: SmallVec::new(),
        spawned_enemy_handles: smallvec::smallvec![enemy],
        pet_handles: SmallVec::new(),
        damage_history: Vec::new(),
        semantic_history: Default::default(),
        pending: None,
        vakuu_auto_select_depth: 0,
        hook_cache: Default::default(),
        deck_dirty: Default::default(),
        relic_dirty_mask: Default::default(),
    };

    for (h, levels) in upgrade_list {
        for _ in 0..levels {
            sts2_kernel::commands::card::upgrade(&mut state, &content, h).unwrap();
        }
    }

    (state, enemy)
}

fn play_hand_idx(
    state: &mut BattleState,
    idx: usize,
    target: Option<CreatureHandle>,
) -> StepOutcome {
    let h = state.player.hand[idx];
    let mut choices = SmallVec::new();
    CombatEngine::play_card(state, &Sts2Content, h, target, &mut choices).unwrap()
}

fn power_amt(state: &BattleState, creature: CreatureHandle, id: PowerId) -> i32 {
    state
        .creature(creature)
        .and_then(|c| c.powers.iter().find(|p| p.id == id))
        .map_or(0, |p| p.amount)
}

#[test]
fn all_90_ironclad_cards_meta_playable_and_upgrade_valid() {
    assert_eq!(CARD_POOL_IRONCLAD.len(), 90);
    for &id in CARD_POOL_IRONCLAD {
        let meta = ironclad::card_meta(id).expect("no Unsupported for Ironclad cards");
        let gen_meta = generated_card_meta(id);
        assert_eq!(meta.card_type, gen_meta.card_type, "type mismatch {id:?}");
        assert_eq!(meta.rarity, gen_meta.rarity, "rarity mismatch {id:?}");
        assert_eq!(meta.target_type, gen_meta.target_type, "target mismatch {id:?}");
        assert_eq!(meta.canonical_energy_cost, gen_meta.canonical_energy_cost, "cost mismatch {id:?}");
        assert_eq!(meta.canonical_star_cost, gen_meta.canonical_star_cost, "star mismatch {id:?}");
        assert_eq!(meta.max_upgrade_level, gen_meta.max_upgrade_level, "max_upg mismatch {id:?}");
        assert_eq!(meta.base_keywords, gen_meta.base_keywords, "kw mismatch {id:?}");
        assert_eq!(meta.base_tags, gen_meta.base_tags, "tags mismatch {id:?}");
        assert_eq!(meta.hook_mask, gen_meta.hook_mask, "hook_mask mismatch {id:?}");
        assert!(meta.flags.contains(gen_meta.flags), "missing generated flags on {id:?}");
    }

    assert!(ironclad::card_meta(CardId::Feed).unwrap().flags.contains(CardStaticFlags::MAY_HEAL | CardStaticFlags::HAS_GROWTH));
    assert!(ironclad::card_meta(CardId::NotYet).unwrap().flags.contains(CardStaticFlags::MAY_HEAL));
    assert!(ironclad::card_meta(CardId::SwordBoomerang).unwrap().flags.contains(CardStaticFlags::USES_RNG));
    assert!(ironclad::card_meta(CardId::StrikeSilent).is_err());

    let pairs: Vec<(CardId, i32)> = CARD_POOL_IRONCLAD.iter().map(|&id| (id, 0)).collect();
    let (mut state, _) = build_ironclad_battle(&pairs, &[], &[], 100);
    for i in 0..state.player.hand.len() {
        let h = state.player.hand[i];
        assert!(ironclad::is_playable(&state, h).unwrap());
        sts2_kernel::commands::card::upgrade(&mut state, &Sts2Content, h).unwrap();
    }

    let find_card = |id: CardId| state.cards.iter().find(|c| c.id == id).unwrap();
    assert!(find_card(CardId::Aggression).local_keywords.contains(CardKeywords::INNATE));
    assert!(find_card(CardId::Juggling).local_keywords.contains(CardKeywords::INNATE));
    assert!(!find_card(CardId::DemonicShield).local_keywords.contains(CardKeywords::EXHAUST));
    assert_eq!(find_card(CardId::Barricade).base_energy_cost, 2);
    assert_eq!(find_card(CardId::BodySlam).base_energy_cost, 0);
    assert_eq!(find_card(CardId::Corruption).base_energy_cost, 2);
    assert_eq!(find_card(CardId::DarkEmbrace).base_energy_cost, 1);
    assert_eq!(find_card(CardId::Havoc).base_energy_cost, 0);
    assert_eq!(find_card(CardId::Hellraiser).base_energy_cost, 1);
    assert_eq!(find_card(CardId::InfernalBlade).base_energy_cost, 0);
    assert_eq!(find_card(CardId::Stampede).base_energy_cost, 1);
    assert_eq!(find_card(CardId::Tank).base_energy_cost, 0);
    assert_eq!(find_card(CardId::Unmovable).base_energy_cost, 1);
}

#[test]
fn ironclad_attacks_block_self_damage_and_scaling() {
    let (mut state, enemy) = build_ironclad_battle(
        &[
            (CardId::BloodWall, 0),
            (CardId::BodySlam, 1),
            (CardId::Bloodletting, 1),
            (CardId::Hemokinesis, 0),
            (CardId::Spite, 0),
            (CardId::TearAsunder, 0),
            (CardId::Offering, 1),
            (CardId::NotYet, 0),
        ],
        &[(CardId::StrikeIronclad, 0); 6],
        &[],
        200,
    );

    // BloodWall: -2 HP (unblockable), +16 block
    assert_eq!(play_hand_idx(&mut state, 0, None), StepOutcome::Completed);
    assert_eq!(state.player.current_hp, 78);
    assert_eq!(state.player.block, 16);

    // BodySlam+: deals 16 damage
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed);
    assert_eq!(state.creature(enemy).unwrap().current_hp, 184);

    // Bloodletting+: -3 HP, +3 energy
    let e_before = state.player.energy;
    assert_eq!(play_hand_idx(&mut state, 0, None), StepOutcome::Completed);
    assert_eq!(state.player.current_hp, 75);
    assert_eq!(state.player.energy, e_before + 3);

    // Hemokinesis: -2 HP, 15 dmg
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed);
    assert_eq!(state.player.current_hp, 73);
    assert_eq!(state.creature(enemy).unwrap().current_hp, 169);

    // Spite: lost HP this turn -> 2 hits of 5 = 10 dmg
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed);
    assert_eq!(state.creature(enemy).unwrap().current_hp, 159);

    // TearAsunder: 3 unblocked HP loss entries so far -> 1 + 3 = 4 hits of 5 = 20 dmg
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed);
    assert_eq!(state.creature(enemy).unwrap().current_hp, 139);

    // Offering+: -6 HP, +2 energy, draw 5, exhausts
    let e_before_off = state.player.energy;
    assert_eq!(play_hand_idx(&mut state, 0, None), StepOutcome::Completed);
    assert_eq!(state.player.current_hp, 67);
    assert_eq!(state.player.energy, e_before_off + 2);
    assert_eq!(state.player.exhaust.len(), 1);

    // NotYet: heal 10 HP, exhausts
    assert_eq!(play_hand_idx(&mut state, 0, None), StepOutcome::Completed);
    assert_eq!(state.player.current_hp, 77);
    assert_eq!(state.player.exhaust.len(), 2);
}

#[test]
fn ironclad_choices_armaments_burning_pact_true_grit_headbutt_brand() {
    // 1. Armaments pause & resume
    let (mut state, enemy) = build_ironclad_battle(
        &[
            (CardId::Armaments, 0),
            (CardId::StrikeIronclad, 0),
            (CardId::DefendIronclad, 0),
        ],
        &[(CardId::Bash, 0), (CardId::Inflame, 0)],
        &[(CardId::Bludgeon, 0)],
        100,
    );
    let out = play_hand_idx(&mut state, 0, None);
    let StepOutcome::NeedsChoice(req) = out else {
        panic!("expected NeedsChoice from Armaments");
    };
    assert_eq!(req.source_id, "CARD.ARMAMENTS");
    assert_eq!(req.candidates.len(), 2);
    let res = CombatEngine::resume_choice(&mut state, &Sts2Content, &[0]).unwrap();
    assert_eq!(res, StepOutcome::Completed);
    assert_eq!(state.player.block, 5);
    assert_eq!(state.card(state.player.hand[0]).unwrap().upgrade_level, 1);

    // 2. BurningPact pause & resume
    let (mut state, _) = build_ironclad_battle(
        &[
            (CardId::BurningPact, 0),
            (CardId::StrikeIronclad, 0),
            (CardId::DefendIronclad, 0),
        ],
        &[(CardId::Bash, 0), (CardId::Inflame, 0)],
        &[],
        100,
    );
    let StepOutcome::NeedsChoice(req) = play_hand_idx(&mut state, 0, None) else {
        panic!("expected NeedsChoice from BurningPact");
    };
    assert_eq!(req.source_pile, PileType::Hand);
    assert_eq!(CombatEngine::resume_choice(&mut state, &Sts2Content, &[1]).unwrap(), StepOutcome::Completed);
    assert_eq!(state.player.exhaust.len(), 1);
    assert_eq!(state.player.hand.len(), 3); // 1 remaining + 2 drawn

    // 3. TrueGrit+ pause & resume
    let (mut state, _) = build_ironclad_battle(
        &[
            (CardId::TrueGrit, 1),
            (CardId::StrikeIronclad, 0),
            (CardId::DefendIronclad, 0),
        ],
        &[],
        &[],
        100,
    );
    let StepOutcome::NeedsChoice(_) = play_hand_idx(&mut state, 0, None) else {
        panic!("expected NeedsChoice from TrueGrit+");
    };
    assert_eq!(CombatEngine::resume_choice(&mut state, &Sts2Content, &[0]).unwrap(), StepOutcome::Completed);
    assert_eq!(state.player.block, 9);
    assert_eq!(state.player.exhaust.len(), 1);

    // 4. Headbutt pause & resume (moves discard card to top of draw)
    let (mut state, _) = build_ironclad_battle(
        &[(CardId::Headbutt, 0)],
        &[(CardId::StrikeIronclad, 0)],
        &[(CardId::Bludgeon, 0), (CardId::DefendIronclad, 0)],
        100,
    );
    let bludgeon_h = state.player.discard[0];
    let StepOutcome::NeedsChoice(req) = play_hand_idx(&mut state, 0, Some(enemy)) else {
        panic!("expected NeedsChoice from Headbutt");
    };
    assert_eq!(req.source_pile, PileType::Discard);
    assert_eq!(CombatEngine::resume_choice(&mut state, &Sts2Content, &[0]).unwrap(), StepOutcome::Completed);
    assert_eq!(state.player.draw[0], bludgeon_h);

    // 5. Brand pause & resume (self damage -> exhaust choice -> strength)
    let (mut state, _) = build_ironclad_battle(
        &[
            (CardId::Brand, 1),
            (CardId::StrikeIronclad, 0),
            (CardId::DefendIronclad, 0),
        ],
        &[],
        &[],
        100,
    );
    let pc = state.player.creature_handle;
    let StepOutcome::NeedsChoice(_) = play_hand_idx(&mut state, 0, None) else {
        panic!("expected NeedsChoice from Brand");
    };
    assert_eq!(state.player.current_hp, 79);
    assert_eq!(CombatEngine::resume_choice(&mut state, &Sts2Content, &[0]).unwrap(), StepOutcome::Completed);
    assert_eq!(state.player.exhaust.len(), 1);
    assert_eq!(power_amt(&state, pc, PowerId::StrengthPower), 2);
}

#[test]
fn ironclad_feed_fatal_vs_minion_and_rampage_thrash_comparison() {
    // Feed kills normal enemy -> +3 max HP & +3 HP
    let (mut state, enemy) = build_ironclad_battle(&[(CardId::Feed, 0)], &[], &[], 10);
    state.player.current_hp = 50;
    state.creature_mut(state.player.creature_handle).unwrap().current_hp = 50;
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::CombatEnded { won: true });
    assert_eq!(state.player.max_hp, 83);
    assert_eq!(state.player.current_hp, 53);

    // Feed kills MinionPower enemy -> NO max HP gain
    let (mut state, enemy) = build_ironclad_battle(&[(CardId::Feed, 0)], &[], &[], 10);
    let enemy_c = state.creature_mut(enemy).unwrap();
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
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::CombatEnded { won: true });
    assert_eq!(state.player.max_hp, 80);

    // Rampage grows damage and updates comparison_base_values
    let (mut state, enemy) = build_ironclad_battle(
        &[(CardId::Rampage, 1), (CardId::Rampage, 1)],
        &[],
        &[],
        100,
    );
    let r0 = state.player.hand[0];
    let r1 = state.player.hand[1];
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed);
    assert_eq!(state.creature(enemy).unwrap().current_hp, 90);
    assert_eq!(ironclad::comparison_base_values(&state, r0).unwrap()[0].1, Dec::from_i32(20));
    assert_eq!(ironclad::comparison_base_values(&state, r1).unwrap()[0].1, Dec::from_i32(10));
    assert_eq!(
        ordering::compare_cards(&mut state, &Sts2Content, r0, r1).unwrap(),
        core::cmp::Ordering::Greater
    );

    // Thrash picks Ironclad Attack in hand, adds modified damage, exhausts picked card
    let (mut state, enemy) = build_ironclad_battle(
        &[(CardId::Thrash, 0), (CardId::Bludgeon, 0)],
        &[],
        &[],
        100,
    );
    let thrash_h = state.player.hand[0];
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed);
    // 2 hits of 4 = 8 dmg, Bludgeon (32) added -> Thrash._damage = 36, Bludgeon exhausted
    assert_eq!(state.creature(enemy).unwrap().current_hp, 92);
    assert_eq!(state.player.exhaust.len(), 1);
    assert_eq!(ironclad::comparison_base_values(&state, thrash_h).unwrap()[0].1, Dec::from_i32(36));

    // Thrash handles non-Ironclad attacks cleanly through ContentDispatcher::card_thrash_damage_variable (returns 0 unless implemented)
    let (mut state, enemy) = build_ironclad_battle(
        &[(CardId::Thrash, 0), (CardId::StrikeSilent, 0)],
        &[],
        &[],
        100,
    );
    let mut choices = SmallVec::new();
    let thrash_non_ic = state.player.hand[0];
    CombatEngine::play_card(&mut state, &Sts2Content, thrash_non_ic, Some(enemy), &mut choices).unwrap();
    assert_eq!(state.creature(enemy).unwrap().current_hp, 92);
    assert_eq!(state.player.exhaust.len(), 1);
}

#[test]
fn ironclad_hooks_drum_of_battle_howl_midnight_stomp() {
    // DrumOfBattle exhausted by BurningPact triggers AfterCardExhausted and grants 2 energy
    let (mut state, _) = build_ironclad_battle(
        &[(CardId::BurningPact, 0), (CardId::DrumOfBattle, 0)],
        &[(CardId::StrikeIronclad, 0), (CardId::DefendIronclad, 0)],
        &[],
        100,
    );
    state.player.energy = 1;
    assert_eq!(play_hand_idx(&mut state, 0, None), StepOutcome::Completed);
    // Spent 1 energy for BurningPact, DrumOfBattle exhausted -> +2 energy
    assert_eq!(state.player.energy, 2);

    // HowlFromBeyond in exhaust auto-plays at AutoPostPlayPhaseEntered (end_player_turn)
    let (mut state, enemy) = build_ironclad_battle(&[(CardId::HowlFromBeyond, 0)], &[], &[], 100);
    let howl_h = state.player.hand[0];
    card_pile::exhaust(&mut state, &Sts2Content, howl_h, false).unwrap();
    let _ = CombatEngine::end_player_turn(&mut state, &Sts2Content).unwrap();
    // Howl dealt 18 dmg to enemy during AutoPostPlay, then moved to Discard (or Draw if shuffled on turn 2)
    assert!(state.creature(enemy).unwrap().current_hp <= 82);

    // Stomp reduces cost by 1 when an Attack is played
    let (mut state, enemy) = build_ironclad_battle(
        &[(CardId::StrikeIronclad, 0), (CardId::Stomp, 0)],
        &[],
        &[],
        100,
    );
    let stomp_h = state.player.hand[1];
    assert_eq!(ordering::resolve_energy_cost(&mut state, &Sts2Content, stomp_h).unwrap(), 3);
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed);
    assert_eq!(ordering::resolve_energy_cost(&mut state, &Sts2Content, stomp_h).unwrap(), 2);

    // Midnight reduces cost by 1 when a card is exhausted
    let (mut state, _) = build_ironclad_battle(
        &[(CardId::TrueGrit, 0), (CardId::Midnight, 0)],
        &[],
        &[],
        100,
    );
    let midnight_h = state.player.hand[1];
    assert_eq!(ordering::resolve_energy_cost(&mut state, &Sts2Content, midnight_h).unwrap(), 12);
    assert_eq!(play_hand_idx(&mut state, 0, None), StepOutcome::Completed);
    if state.player.hand.contains(&midnight_h) {
        assert_eq!(ordering::resolve_energy_cost(&mut state, &Sts2Content, midnight_h).unwrap(), 11);
    }
}

#[test]
fn ironclad_anger_whirlwind_crimson_mantle_inferno_stoke_infernal_blade_and_primal_force() {
    // Anger clones self into Discard; Whirlwind hits X times
    let (mut state, enemy) = build_ironclad_battle(
        &[(CardId::Anger, 0), (CardId::Whirlwind, 0)],
        &[],
        &[],
        100,
    );
    state.player.energy = 3;
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed);
    assert_eq!(state.creature(enemy).unwrap().current_hp, 94);
    assert_eq!(state.player.discard.len(), 2); // original Anger + clone
    assert_eq!(state.player.counters.cards_generated_this_combat, 1);

    assert_eq!(play_hand_idx(&mut state, 0, None), StepOutcome::Completed);
    assert_eq!(state.player.energy, 0);
    assert_eq!(state.creature(enemy).unwrap().current_hp, 79); // 3 * 5 = 15 dmg

    // CrimsonMantle + Inferno increment _selfDamage
    let (mut state, _) = build_ironclad_battle(
        &[(CardId::CrimsonMantle, 0), (CardId::Inferno, 0)],
        &[],
        &[],
        100,
    );
    let pc = state.player.creature_handle;
    assert_eq!(play_hand_idx(&mut state, 0, None), StepOutcome::Completed);
    assert_eq!(play_hand_idx(&mut state, 0, None), StepOutcome::Completed);
    let cm_ext = state.creature(pc).unwrap().powers.iter().find(|p| p.id == PowerId::CrimsonMantlePower).unwrap().ext_index.unwrap();
    assert_eq!(state.power_extensions[cm_ext as usize].private_fields.get("CrimsonMantlePower._selfDamage"), Some(&FieldValue::Decimal(Dec::ONE)));
    let inf_ext = state.creature(pc).unwrap().powers.iter().find(|p| p.id == PowerId::InfernoPower).unwrap().ext_index.unwrap();
    assert_eq!(state.power_extensions[inf_ext as usize].private_fields.get("InfernoPower._selfDamage"), Some(&FieldValue::Decimal(Dec::ONE)));

    // InfernalBlade generates free-this-turn attack; Stoke exhausts hand and generates N cards
    let (mut state, _) = build_ironclad_battle(
        &[(CardId::InfernalBlade, 0), (CardId::Stoke, 1), (CardId::StrikeIronclad, 0)],
        &[],
        &[],
        100,
    );
    assert_eq!(play_hand_idx(&mut state, 0, None), StepOutcome::Completed);
    assert_eq!(state.player.hand.len(), 3); // Stoke+, Strike, generated Attack
    let gen_h = state.player.hand[2];
    assert_eq!(ordering::resolve_energy_cost(&mut state, &Sts2Content, gen_h).unwrap(), 0);
    assert_eq!(play_hand_idx(&mut state, 0, None), StepOutcome::Completed);
    assert_eq!(state.player.exhaust.len(), 3); // InfernalBlade + Strike + generated Attack
    assert_eq!(state.player.hand.len(), 2);
    assert!(state.player.hand.iter().all(|&h| state.card(h).unwrap().upgrade_level == 1));

    // PrimalForce and PrimalForce+ transform hand attacks into GiantRock / GiantRock+ via Sts2Content
    let (mut state, _) = build_ironclad_battle(
        &[(CardId::PrimalForce, 0), (CardId::StrikeIronclad, 0), (CardId::DefendIronclad, 0)],
        &[],
        &[],
        100,
    );
    let mut choices = SmallVec::new();
    let pf_h = state.player.hand[0];
    CombatEngine::play_card(&mut state, &Sts2Content, pf_h, None, &mut choices).unwrap();
    assert_eq!(state.card(state.player.hand[0]).unwrap().id, CardId::GiantRock);
    assert_eq!(state.card(state.player.hand[0]).unwrap().upgrade_level, 0);
    assert_eq!(state.card(state.player.hand[1]).unwrap().id, CardId::DefendIronclad);

    let (mut state_upg, _) = build_ironclad_battle(&[(CardId::PrimalForce, 1), (CardId::StrikeIronclad, 0)], &[], &[], 100);
    let pf_upg_h = state_upg.player.hand[0];
    CombatEngine::play_card(&mut state_upg, &Sts2Content, pf_upg_h, None, &mut choices).unwrap();
    assert_eq!(state_upg.card(state_upg.player.hand[0]).unwrap().id, CardId::GiantRock);
    assert_eq!(state_upg.card(state_upg.player.hand[0]).unwrap().upgrade_level, 1);
}

#[test]
fn ironclad_synergies_exhaust_vulnerable_draw_powers_and_remaining_cards() {
    // ExpectAFight + PactsEnd + EvilEye + ForgottenRitual + SecondWind + FiendFire + AshenStrike + PerfectedStrike
    let (mut state, enemy) = build_ironclad_battle(
        &[
            (CardId::ExpectAFight, 0),
            (CardId::SecondWind, 0),
            (CardId::DefendIronclad, 0),
            (CardId::EvilEye, 0),
            (CardId::ForgottenRitual, 0),
            (CardId::PactsEnd, 0),
            (CardId::AshenStrike, 0),
            (CardId::PerfectedStrike, 0),
        ],
        &[(CardId::StrikeIronclad, 0), (CardId::PommelStrike, 0)],
        &[],
        200,
    );
    state.player.energy = 10;
    // ExpectAFight: +15 block, reduces PactsEnd(0), AshenStrike(1->0), PerfectedStrike(2->1)
    assert_eq!(play_hand_idx(&mut state, 0, None), StepOutcome::Completed);
    assert_eq!(state.player.block, 15);
    let (ashen_h, perf_h) = (state.player.hand[5], state.player.hand[6]);
    assert_eq!(ordering::resolve_energy_cost(&mut state, &Sts2Content, ashen_h).unwrap(), 0);
    assert_eq!(ordering::resolve_energy_cost(&mut state, &Sts2Content, perf_h).unwrap(), 1);

    // ForgottenRitual before exhaust -> 0 energy
    let e_before = state.player.energy;
    assert_eq!(play_hand_idx(&mut state, 3, None), StepOutcome::Completed);
    assert_eq!(state.player.energy, e_before - 1);

    // SecondWind exhausts Defend + EvilEye (non-attacks) -> +10 block, 2 exhausts
    assert_eq!(play_hand_idx(&mut state, 0, None), StepOutcome::Completed);
    assert_eq!(state.player.block, 25);
    assert_eq!(state.player.exhaust.len(), 3); // ForgottenRitual + Defend + EvilEye

    // PactsEnd with >=3 exhausts -> 18 dmg
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed);
    assert_eq!(state.creature(enemy).unwrap().current_hp, 182);

    // AshenStrike: 6 + 3*3 = 15 dmg
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed);
    assert_eq!(state.creature(enemy).unwrap().current_hp, 167);

    // PerfectedStrike: 4 strikes in combat (AshenStrike in discard, PerfectedStrike, StrikeIronclad, PommelStrike) -> 6 + 2*4 = 14 dmg
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed);
    assert_eq!(state.creature(enemy).unwrap().current_hp, 153);

    // EvilEye & ForgottenRitual when exhaust happened this turn
    let (mut state, _) = build_ironclad_battle(
        &[(CardId::TrueGrit, 0), (CardId::EvilEye, 0), (CardId::ForgottenRitual, 0), (CardId::StrikeIronclad, 0)],
        &[],
        &[],
        100,
    );
    assert_eq!(play_hand_idx(&mut state, 0, None), StepOutcome::Completed);
    let blk_before = state.player.block;
    if let Some(ee_idx) = state.player.hand.iter().position(|&h| state.card(h).unwrap().id == CardId::EvilEye) {
        assert_eq!(play_hand_idx(&mut state, ee_idx, None), StepOutcome::Completed);
        assert_eq!(state.player.block, blk_before + 16); // 2 * 8
    }
    if let Some(fr_idx) = state.player.hand.iter().position(|&h| state.card(h).unwrap().id == CardId::ForgottenRitual) {
        let e_b = state.player.energy;
        assert_eq!(play_hand_idx(&mut state, fr_idx, None), StepOutcome::Completed);
        assert_eq!(state.player.energy, e_b - 1 + 3);
    }

    // Vulnerable interactions: Bash, Dominate, MoltenFist, Bully, Dismantle, Uppercut, Thunderclap, Tremble, Taunt, Break
    let (mut state, enemy) = build_ironclad_battle(
        &[
            (CardId::Tremble, 0),
            (CardId::Dominate, 0),
            (CardId::MoltenFist, 0),
            (CardId::Dismantle, 0),
            (CardId::Bully, 0),
            (CardId::Uppercut, 0),
            (CardId::Thunderclap, 0),
            (CardId::Taunt, 0),
            (CardId::Break, 0),
        ],
        &[],
        &[],
        300,
    );
    state.player.energy = 20;
    let pc = state.player.creature_handle;
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed); // Tremble -> 3 Vuln
    assert_eq!(power_amt(&state, enemy, PowerId::VulnerablePower), 3);
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed); // Dominate -> +1 Vuln = 4, +4 Str
    assert_eq!(power_amt(&state, enemy, PowerId::VulnerablePower), 4);
    assert_eq!(power_amt(&state, pc, PowerId::StrengthPower), 4);
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed); // MoltenFist -> doubles Vuln to 8
    assert_eq!(power_amt(&state, enemy, PowerId::VulnerablePower), 8);
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed); // Dismantle -> 2 hits
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed); // Bully -> 4 + 2*8 + 4 Str
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed); // Uppercut
    assert_eq!(play_hand_idx(&mut state, 0, None), StepOutcome::Completed); // Thunderclap
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed); // Taunt
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed); // Break
    assert!(state.creature(enemy).unwrap().current_hp < 300);

    // Pillage, Havoc, Cascade, FiendFire, Cinder, BattleTrance, Powers
    let (mut state, enemy) = build_ironclad_battle(
        &[
            (CardId::Pillage, 0),
            (CardId::Havoc, 0),
            (CardId::Cascade, 1),
            (CardId::FiendFire, 0),
            (CardId::StrikeIronclad, 0),
        ],
        &[
            (CardId::StrikeIronclad, 0),
            (CardId::DefendIronclad, 0),
            (CardId::IronWave, 0),
            (CardId::TwinStrike, 0),
        ],
        &[],
        200,
    );
    state.player.energy = 6;
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed); // Pillage draws Strike then Defend and stops
    assert_eq!(state.player.draw.len(), 2);
    assert_eq!(play_hand_idx(&mut state, 0, None), StepOutcome::Completed); // Havoc exhaust-plays IronWave
    assert_eq!(state.player.exhaust.len(), 1);
    state.player.energy = 0;
    assert_eq!(play_hand_idx(&mut state, 0, None), StepOutcome::Completed); // Cascade+ at X=0 -> 1 autoplay (TwinStrike)
    state.player.energy = 2;
    assert_eq!(play_hand_idx(&mut state, 0, Some(enemy)), StepOutcome::Completed); // FiendFire exhausts rest of hand

    // Remaining powers & attacks smoke check
    let (mut state, enemy) = build_ironclad_battle(
        &[
            (CardId::Aggression, 0),
            (CardId::Barricade, 0),
            (CardId::Corruption, 0),
            (CardId::Cruelty, 0),
            (CardId::DarkEmbrace, 0),
            (CardId::DemonForm, 0),
            (CardId::FeelNoPain, 0),
            (CardId::FlameBarrier, 0),
            (CardId::Hellraiser, 0),
            (CardId::Juggernaut, 0),
        ],
        &[],
        &[],
        100,
    );
    state.player.energy = 30;
    for _ in 0..10 {
        assert_eq!(play_hand_idx(&mut state, 0, None), StepOutcome::Completed);
    }

    let (mut state, enemy2) = build_ironclad_battle(
        &[
            (CardId::Juggling, 0),
            (CardId::OneTwoPunch, 0),
            (CardId::Pyre, 0),
            (CardId::Rage, 0),
            (CardId::Rupture, 0),
            (CardId::Stampede, 0),
            (CardId::StoneArmor, 0),
            (CardId::Unmovable, 0),
            (CardId::Vicious, 0),
            (CardId::Tank, 0),
        ],
        &[],
        &[],
        100,
    );
    state.player.energy = 30;
    for _ in 0..10 {
        assert_eq!(play_hand_idx(&mut state, 0, None), StepOutcome::Completed);
    }
    let _ = (enemy, enemy2);

    let (mut state, enemy) = build_ironclad_battle(
        &[
            (CardId::BattleTrance, 0),
            (CardId::Cinder, 0),
            (CardId::Colossus, 0),
            (CardId::Conflagration, 0),
            (CardId::FightMe, 0),
            (CardId::Impervious, 0),
            (CardId::Mangle, 0),
            (CardId::SwordBoomerang, 0),
            (CardId::Unrelenting, 0),
            (CardId::Outrage, 0),
        ],
        &[],
        &[],
        200,
    );
    state.player.energy = 30;
    while !state.player.hand.is_empty() {
        let h = state.player.hand[0];
        let needs_enemy = generated_card_meta(state.card(h).unwrap().id).target_type == sts2_types::TargetType::AnyEnemy;
        assert_eq!(play_hand_idx(&mut state, 0, if needs_enemy { Some(enemy) } else { None }), StepOutcome::Completed);
    }
}
