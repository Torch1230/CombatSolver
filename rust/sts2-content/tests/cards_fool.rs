use smallvec::SmallVec;
use std::sync::Arc;
use sts2_content::cards::fool;
use sts2_content::Sts2Content;
use sts2_kernel::commands::{arrow, card as card_cmd, card_pile, power};
use sts2_kernel::content_dispatch::CardPlayContext;
use sts2_kernel::{
    ArrowSlotInst, BattleState, CardHandle, CardInst, CardTempFlags, CombatEngine,
    ContentDispatcher, CreatureHandle, CreatureInst, FieldValue, MonsterInst, PlayerCounters,
    PlayerInst, RootStaticContext, SemanticHistory, StepOutcome,
};
use sts2_types::{
    ArrowStatus, CardId, CardKeywords, CardPilePosition, CharacterId, CombatSide, Dec, GameMode,
    MonsterId, PileType, PlayerRngSet, PlayerTurnPhase, PowerId, Rng, RoomType, RunRngSet,
    CARD_POOL_FOOL, generated_card_meta,
};

fn build_fool_battle(
    enemy_hps: &[i32],
    hand_ids: &[CardId],
    draw_ids: &[CardId],
    discard_ids: &[CardId],
) -> (BattleState, Vec<CreatureHandle>) {
    let run_rngs = RunRngSet::from_string_seed("FOOLTEST1");
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc_handle = CreatureHandle(1);

    let mut cards = Vec::new();
    let mut next_h = 1u32;
    let mut make_card = |cid: CardId, pile: PileType| -> CardHandle {
        let meta = generated_card_meta(cid);
        let h = CardHandle(next_h);
        next_h += 1;
        let mut temp_flags = CardTempFlags::empty();
        if meta.flags.contains(sts2_types::CardStaticFlags::IS_X_ENERGY_COST) {
            temp_flags |= CardTempFlags::COSTS_X_ENERGY;
        }
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
            temp_flags,
            affliction: None,
            ext_index: None,
            pile,
            removed_from_state: false,
            transform_into_after_play: None,
        });
        h
    };

    let hand: Vec<CardHandle> = hand_ids.iter().map(|&id| make_card(id, PileType::Hand)).collect();
    let draw: Vec<CardHandle> = draw_ids.iter().map(|&id| make_card(id, PileType::Draw)).collect();
    let discard: Vec<CardHandle> = discard_ids.iter().map(|&id| make_card(id, PileType::Discard)).collect();

    let mut creatures = Vec::new();
    let mut enemy_handles = Vec::new();
    let mut encounter_slots = Vec::new();
    let player_creature = CreatureInst {
        handle: pc_handle,
        combat_id: Some(0),
        side: CombatSide::Player,
        slot_name: None,
        is_player: true,
        pet_owner_player_index: None,
        monster_id: None,
        current_hp: 77,
        max_hp: 77,
        block: 0,
        cumulative_hp_lost: 0,
        is_detached: false,
        monster: None,
        has_minion_power: false,
        powers: Vec::new(),
    };
    creatures.push(player_creature);

    for (idx, &hp) in enemy_hps.iter().enumerate() {
        let eh = CreatureHandle((idx + 2) as u32);
        let slot = format!("slot_{idx}");
        encounter_slots.push(slot.clone());
        enemy_handles.push(eh);
        creatures.push(CreatureInst {
            handle: eh,
            combat_id: Some((idx + 1) as u32),
            side: CombatSide::Enemy,
            slot_name: Some(std::borrow::Cow::Owned(slot)),
            is_player: false,
            pet_owner_player_index: None,
            monster_id: Some(MonsterId::FuzzyWurmCrawler),
            current_hp: hp,
            max_hp: hp,
            block: 0,
            cumulative_hp_lost: 0,
            is_detached: false,
            monster: Some(MonsterInst {
                monster_id: MonsterId::FuzzyWurmCrawler,
                rng: Rng::from_seed(run_rngs.seed + idx as u64 + 1),
                is_performing_move: false,
                spawned_this_turn: false,
                current_state_id: std::borrow::Cow::Borrowed("ACID_GOOP"),
                next_move_id: Some(std::borrow::Cow::Borrowed("ACID_GOOP")),
                next_move_can_transition_away: true,
                performed_first_move: false,
                state_log_ids: smallvec::smallvec![std::borrow::Cow::Borrowed("ACID_GOOP")],
                transient_move_follow_up_state_id: None,
                setup_fields: Default::default(),
                private_fields: Default::default(),
            }),
            has_minion_power: false,
            powers: Vec::new(),
        });
    }

    let player = PlayerInst {
        creature_handle: pc_handle,
        current_hp: 77,
        max_hp: 77,
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
        orbs: SmallVec::new(),
        arrows: smallvec::smallvec![ArrowSlotInst {
            status: ArrowStatus::Retrieved,
            destroy_after_impaled: false,
        }],
        relics: SmallVec::new(),
        card_or_potion_effect_depth: 0,
    };

    let enemies_vec: Vec<CreatureHandle> = enemy_handles.iter().copied().collect();
    let state = BattleState {
        static_ctx: Arc::new(RootStaticContext {
            encounter_id: "TEST_ENCOUNTER".to_owned(),
            encounter_is_weak: false,
            room_type: RoomType::Monster,
            encounter_slots,
            game_mode: GameMode::Standard,
            ascension: 0,
            character_id: CharacterId::Fool,
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
        next_creature_id: (enemy_hps.len() + 1) as u32,
        initial_encounter_monster_count: enemy_hps.len(),
        next_card_handle: next_h,
        next_creature_handle: (enemy_hps.len() + 2) as u32,
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
        damage_history: Vec::new(),
        semantic_history: SemanticHistory::default(),
        pending: None,
        vakuu_auto_select_depth: 0,
        hook_cache: Default::default(),
        deck_dirty: Default::default(),
    };
    (state, enemies_vec)
}

fn add_card_to_hand(state: &mut BattleState, content: &dyn ContentDispatcher, id: CardId, upg: i32) -> CardHandle {
    let h = card_pile::create_card_in_combat(state, content, id, upg).unwrap();
    card_pile::add_sync(state, h, PileType::Hand, CardPilePosition::Bottom);
    assert_eq!(
        state.card(h).unwrap().pile,
        PileType::Hand,
        "hand full when adding {id:?}"
    );
    h
}

fn clear_hand(state: &mut BattleState) {
    state.player.hand.clear();
    state.invalidate_pile_hooks(sts2_kernel::state::HOOK_DIRTY_HAND);
}

fn play_hand_card(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    target: Option<CreatureHandle>,
) -> StepOutcome {
    let mut choices = SmallVec::new();
    CombatEngine::play_card(state, content, card, target, &mut choices)
        .unwrap_or_else(|e| panic!("play_card failed on {:?}: {e:?}", state.card(card).map(|c| c.id)))
}

fn power_amount(state: &BattleState, creature: CreatureHandle, id: PowerId) -> i32 {
    state
        .creature(creature)
        .and_then(|c| c.powers.iter().find(|p| p.id == id))
        .map_or(0, |p| p.amount)
}

#[test]
fn all_90_fool_cards_meta_playable_upgrade_and_comparison_complete() {
    assert_eq!(CARD_POOL_FOOL.len(), 90);
    let (mut state, _) = build_fool_battle(&[100], &[], &[], &[]);

    let cost_upgrades = [
        CardId::BlotOutTheSky,
        CardId::Ecstasy,
        CardId::Honing,
        CardId::LockedIn,
        CardId::LostLute,
        CardId::MelodicShot,
        CardId::Motif,
        CardId::Recall,
        CardId::Simpleton,
        CardId::Tune,
    ];

    for &cid in CARD_POOL_FOOL {
        let meta = fool::card_meta(cid).unwrap_or_else(|e| panic!("{cid:?} card_meta failed: {e:?}"));
        let h = card_pile::create_card_in_combat(&mut state, &Sts2Content, cid, 0).unwrap();
        assert!(fool::is_playable(&state, h).unwrap(), "{cid:?} should be playable");
        let cmp = fool::comparison_base_values(&state, h).unwrap();
        if cid == CardId::ShowOff {
            assert_eq!(cmp.as_slice(), &[("AimPower", Dec::from_i32(5))]);
        } else {
            assert!(cmp.is_empty(), "{cid:?} should have empty comparison_base_values");
        }

        let before_cost = state.card(h).unwrap().base_energy_cost;
        card_cmd::upgrade(&mut state, &Sts2Content, h).unwrap();
        let after = state.card(h).unwrap();
        assert_eq!(after.upgrade_level, 1);
        if cost_upgrades.contains(&cid) {
            assert_eq!(after.base_energy_cost, (before_cost - 1).max(0), "{cid:?} upgrade cost");
        } else {
            assert_eq!(after.base_energy_cost, before_cost, "{cid:?} cost unchanged");
        }
        if cid == CardId::Lullaby {
            assert!(after.has_local_keyword(CardKeywords::INNATE));
        }
        assert_eq!(after.canonical_base_energy_cost, meta.canonical_energy_cost);
    }
}

#[test]
fn arrow_shooting_heavy_aim_blossom_rapid_repeat_rain_trick_whistling() {
    let content = Sts2Content;
    let (mut state, enemies) = build_fool_battle(&[200], &[], &[], &[]);
    let enemy = enemies[0];
    let pc = state.player.creature_handle;

    // Quiver + Nock + HeavyShot (2x AimPower!)
    let quiver = add_card_to_hand(&mut state, &content, CardId::Quiver, 1);
    play_hand_card(&mut state, &content, quiver, None);
    assert_eq!(state.player.arrows.len(), 3);

    let nock = add_card_to_hand(&mut state, &content, CardId::Nock, 0);
    play_hand_card(&mut state, &content, nock, None);
    assert_eq!(power_amount(&state, pc, PowerId::AimPower), 5);

    let heavy = add_card_to_hand(&mut state, &content, CardId::HeavyShot, 0);
    play_hand_card(&mut state, &content, heavy, Some(enemy));
    // 20 + 2 * 5 = 30 damage, Aim consumed to 0, 1 arrow impaled
    assert_eq!(state.creature(enemy).unwrap().current_hp, 170);
    assert_eq!(power_amount(&state, pc, PowerId::AimPower), 0);
    assert_eq!(power_amount(&state, enemy, PowerId::ImpaledPower), 1);

    // WhistlingArrows in discard returns to hand when next arrow is shot
    let whistling = add_card_to_hand(&mut state, &content, CardId::WhistlingArrows, 0);
    play_hand_card(&mut state, &content, whistling, None);
    assert_eq!(state.player.block, 3);
    assert_eq!(state.card(whistling).unwrap().pile, PileType::Discard);

    // BlossomShot shoots arrow, deals 11, grants 11 StretchPower, impales, returns WhistlingArrows
    let blossom = add_card_to_hand(&mut state, &content, CardId::BlossomShot, 0);
    play_hand_card(&mut state, &content, blossom, Some(enemy));
    assert_eq!(state.creature(enemy).unwrap().current_hp, 159);
    assert_eq!(power_amount(&state, pc, PowerId::StretchPower), 11);
    assert_eq!(state.card(whistling).unwrap().pile, PileType::Hand);

    // RapidShot shoots 3rd arrow for 3x3=9 (Stretch doesn't affect attack, 3 hits dislodge an arrow!)
    let rapid = add_card_to_hand(&mut state, &content, CardId::RapidShot, 0);
    play_hand_card(&mut state, &content, rapid, Some(enemy));
    assert_eq!(state.creature(enemy).unwrap().current_hp, 150);
    assert!(arrow::player_has_any_arrows(&state));

    // RepeatShot with 2 energy -> 2 hits * 15 = 30
    state.player.energy = 2;
    let repeat = add_card_to_hand(&mut state, &content, CardId::RepeatShot, 0);
    play_hand_card(&mut state, &content, repeat, Some(enemy));
    assert_eq!(state.creature(enemy).unwrap().current_hp, 120);
    assert_eq!(state.player.energy, 0);

    // QuickShot + DivineShot + SkyShot + RainShot (add 2 more arrow slots so arrows remain available)
    state.player.energy = 10;
    let quiver2 = add_card_to_hand(&mut state, &content, CardId::Quiver, 1);
    play_hand_card(&mut state, &content, quiver2, None);
    let quick = add_card_to_hand(&mut state, &content, CardId::QuickShot, 1);
    play_hand_card(&mut state, &content, quick, Some(enemy));
    assert_eq!(state.creature(enemy).unwrap().current_hp, 107);

    let divine = add_card_to_hand(&mut state, &content, CardId::DivineShot, 0);
    play_hand_card(&mut state, &content, divine, Some(enemy));
    assert_eq!(state.creature(enemy).unwrap().current_hp, 67);

    let sky = add_card_to_hand(&mut state, &content, CardId::SkyShot, 1);
    play_hand_card(&mut state, &content, sky, Some(enemy));
    assert_eq!(state.creature(enemy).unwrap().current_hp, 27);
    assert_eq!(state.card(sky).unwrap().pile, PileType::Exhaust);

    // RainShot on 1 enemy removes AimPower & QuietusPower and deals 12
    let quietus = add_card_to_hand(&mut state, &content, CardId::Quietus, 0);
    play_hand_card(&mut state, &content, quietus, None);
    let rain = add_card_to_hand(&mut state, &content, CardId::RainShot, 0);
    play_hand_card(&mut state, &content, rain, Some(enemy));
    // 12 * 2 (Quietus 2) = 24
    assert_eq!(state.creature(enemy).unwrap().current_hp, 3);
    assert_eq!(power_amount(&state, pc, PowerId::QuietusPower), 0);
}

#[test]
fn rain_shot_multi_enemy_capped_splash_and_trick_shot_result_loc() {
    let content = Sts2Content;
    let (mut state, enemies) = build_fool_battle(&[100, 100], &[], &[], &[]);
    // Apply Vulnerable to enemies[1] to verify splash uses ModifyDamageHookType::CAP only (not multiplicative Vulnerable)
    let pc = state.player.creature_handle;
    power::apply(
        &mut state,
        &content,
        PowerId::VulnerablePower,
        enemies[1],
        Dec::TWO,
        Some(pc),
        None,
    )
    .unwrap();
    let rain = add_card_to_hand(&mut state, &content, CardId::RainShot, 0);
    let mut choices = SmallVec::new();
    CombatEngine::play_card(&mut state, &content, rain, Some(enemies[0]), &mut choices).unwrap();
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, 88); // 12 primary
    assert_eq!(state.creature(enemies[1]).unwrap().current_hp, 88); // 12 capped splash (not 18!)
    assert!(
        state
            .creature(enemies[0])
            .unwrap()
            .powers
            .iter()
            .any(|p| p.id == PowerId::ImpaledPower)
    );

    // TrickShot default_result_location check
    state.player.arrows[0].status = ArrowStatus::Retrieved;
    let trick = add_card_to_hand(&mut state, &content, CardId::TrickShot, 0);
    assert_eq!(
        fool::default_result_location(&state, trick, false).unwrap(),
        Some((PileType::Hand, CardPilePosition::Bottom))
    );
    state.player.arrows[0].status = ArrowStatus::Away;
    assert_eq!(
        fool::default_result_location(&state, trick, false).unwrap(),
        None
    );
}

#[test]
fn impale_dislodge_cards_and_pile_selection_contracts() {
    let content = Sts2Content;
    let (mut state, enemies) = build_fool_battle(
        &[200],
        &[],
        &[CardId::StrikeFool, CardId::MelodicShot, CardId::QuickShot],
        &[CardId::FoolishShot, CardId::QuickShot, CardId::DefendFool],
    );
    let enemy = enemies[0];

    // LockedIn sorts draw pile Shoot candidates by C# CardRarity then Id (QuickShot Common < MelodicShot Rare)
    let locked = add_card_to_hand(&mut state, &content, CardId::LockedIn, 0);
    let outcome = play_hand_card(&mut state, &content, locked, None);
    let StepOutcome::NeedsChoice(req) = outcome else {
        panic!("expected choice");
    };
    assert_eq!(req.candidates.len(), 2);
    assert_eq!(state.card(req.candidates[0]).unwrap().id, CardId::QuickShot);
    assert_eq!(state.card(req.candidates[1]).unwrap().id, CardId::MelodicShot);
    assert_eq!(CombatEngine::resume_choice(&mut state, &content, &[1]).unwrap(), StepOutcome::Completed);
    assert_eq!(state.card(req.candidates[1]).unwrap().pile, PileType::Hand);

    // Shoot enemy to impale
    let quick = add_card_to_hand(&mut state, &content, CardId::QuickShot, 0);
    play_hand_card(&mut state, &content, quick, Some(enemy));
    assert_eq!(power_amount(&state, enemy, PowerId::ImpaledPower), 1);

    // BowSmack (+1 energy), KiHop (draw 2), Nail (damage + 2 Weak + 2 Vuln), WahHammer (2x dmg)
    let e_before = state.player.energy;
    let bow = add_card_to_hand(&mut state, &content, CardId::BowSmack, 0);
    play_hand_card(&mut state, &content, bow, Some(enemy));
    assert_eq!(state.player.energy, e_before); // spent 1, gained 1

    let kihop = add_card_to_hand(&mut state, &content, CardId::KiHop, 0);
    play_hand_card(&mut state, &content, kihop, Some(enemy));

    let nail = add_card_to_hand(&mut state, &content, CardId::Nail, 0);
    play_hand_card(&mut state, &content, nail, Some(enemy));
    assert_eq!(power_amount(&state, enemy, PowerId::WeakPower), 2);
    assert_eq!(power_amount(&state, enemy, PowerId::VulnerablePower), 2);

    let hp_before = state.creature(enemy).unwrap().current_hp;
    let hammer = add_card_to_hand(&mut state, &content, CardId::WahHammer, 0);
    play_hand_card(&mut state, &content, hammer, Some(enemy));
    // Nail was the 3rd hit after impaling, so it dislodged the arrow: 9 * 1.5 = 13 damage
    assert_eq!(state.creature(enemy).unwrap().current_hp, hp_before - 13);

    // Re-impale and verify WahHammer 2x damage against an impaled enemy
    let quick_h = add_card_to_hand(&mut state, &content, CardId::QuickShot, 0);
    play_hand_card(&mut state, &content, quick_h, Some(enemy));
    let hp_before2 = state.creature(enemy).unwrap().current_hp;
    let hammer2 = add_card_to_hand(&mut state, &content, CardId::WahHammer, 0);
    play_hand_card(&mut state, &content, hammer2, Some(enemy));
    // 9 * 2 (WahHammer) * 1.5 (Vuln) = 27
    assert_eq!(state.creature(enemy).unwrap().current_hp, hp_before2 - 27);

    // Clear impaled via Wrest
    let wrest = add_card_to_hand(&mut state, &content, CardId::Wrest, 0);
    play_hand_card(&mut state, &content, wrest, Some(enemy));
    assert_eq!(power_amount(&state, enemy, PowerId::ImpaledPower), 0);
    assert!(arrow::player_has_any_arrows(&state));

    // Uproot dislodges & applies Vulnerable
    let quick3 = add_card_to_hand(&mut state, &content, CardId::QuickShot, 0);
    play_hand_card(&mut state, &content, quick3, Some(enemy));
    let uproot = add_card_to_hand(&mut state, &content, CardId::Uproot, 0);
    play_hand_card(&mut state, &content, uproot, Some(enemy));
    assert_eq!(power_amount(&state, enemy, PowerId::ImpaledPower), 0);
    assert!(power_amount(&state, enemy, PowerId::VulnerablePower) >= 3);

    // Recall dislodges & puts chosen discard card on top of draw
    let quick4 = add_card_to_hand(&mut state, &content, CardId::QuickShot, 0);
    play_hand_card(&mut state, &content, quick4, Some(enemy));
    let recall = add_card_to_hand(&mut state, &content, CardId::Recall, 0);
    let outcome = play_hand_card(&mut state, &content, recall, Some(enemy));
    let StepOutcome::NeedsChoice(req) = outcome else {
        panic!("expected Recall discard choice");
    };
    let chosen_card = req.candidates[0];
    assert_eq!(CombatEngine::resume_choice(&mut state, &content, &[0]).unwrap(), StepOutcome::Completed);
    assert_eq!(state.player.draw.first().copied(), Some(chosen_card));

    // ArrowStab chooses Shoot card from discard when arrow is available (keep hand < 10)
    clear_hand(&mut state);
    let arrow_stab = add_card_to_hand(&mut state, &content, CardId::ArrowStab, 0);
    let outcome = play_hand_card(&mut state, &content, arrow_stab, Some(enemy));
    if let StepOutcome::NeedsChoice(req) = outcome {
        for &c in &req.candidates {
            assert!(generated_card_meta(state.card(c).unwrap().id).base_tags.contains(sts2_types::CardTags::SHOOT));
        }
        assert_eq!(CombatEngine::resume_choice(&mut state, &content, &[0]).unwrap(), StepOutcome::Completed);
    }

    // EpicRetrieval: impale, hit twice to set counter=1, then EpicRetrieval triggers AfterArrowDislodged +3 energy
    clear_hand(&mut state);
    state.player.energy = 10;
    let quick5 = add_card_to_hand(&mut state, &content, CardId::QuickShot, 0);
    play_hand_card(&mut state, &content, quick5, Some(enemy));
    let strike1 = add_card_to_hand(&mut state, &content, CardId::StrikeFool, 0);
    let strike2 = add_card_to_hand(&mut state, &content, CardId::StrikeFool, 0);
    play_hand_card(&mut state, &content, strike1, Some(enemy));
    play_hand_card(&mut state, &content, strike2, Some(enemy));
    state.player.energy = 5;
    let epic = add_card_to_hand(&mut state, &content, CardId::EpicRetrieval, 0);
    play_hand_card(&mut state, &content, epic, Some(enemy));
    // Spent 2, gained 3 -> 6 energy
    assert_eq!(state.player.energy, 6);
}

#[test]
fn melodies_coda_cholimbs_kichant_chochorus_and_powers() {
    let content = Sts2Content;
    let (mut state, enemies) = build_fool_battle(
        &[300, 300],
        &[],
        &[CardId::StrikeFool, CardId::DefendFool],
        &[CardId::StrikeFool, CardId::StrikeFool, CardId::DefendFool],
    );
    let enemy = enemies[0];
    let pc = state.player.creature_handle;

    let coda = add_card_to_hand(&mut state, &content, CardId::Coda, 0);
    let cho_limbs = add_card_to_hand(&mut state, &content, CardId::ChoLimbs, 0);
    assert_eq!(sts2_kernel::ordering::resolve_energy_cost(&mut state, &content, coda).unwrap(), 6);
    assert_eq!(sts2_kernel::ordering::resolve_energy_cost(&mut state, &content, cho_limbs).unwrap(), 2);

    let lullaby = add_card_to_hand(&mut state, &content, CardId::Lullaby, 0);
    play_hand_card(&mut state, &content, lullaby, None);
    let yah = add_card_to_hand(&mut state, &content, CardId::Yah, 1);
    play_hand_card(&mut state, &content, yah, None);

    let wah_touch = add_card_to_hand(&mut state, &content, CardId::WahTouch, 0);
    play_hand_card(&mut state, &content, wah_touch, Some(enemy));
    // ChoLimbs now costs 0 (played Wah), Coda now costs 4
    assert_eq!(sts2_kernel::ordering::resolve_energy_cost(&mut state, &content, cho_limbs).unwrap(), 0);
    assert_eq!(sts2_kernel::ordering::resolve_energy_cost(&mut state, &content, coda).unwrap(), 4);

    // KiChant with 2 in draw, 3 in discard, count=3: shuffles & preserves top 2 of draw, lets user pick 1 and discards rest
    let ki_chant = add_card_to_hand(&mut state, &content, CardId::KiChant, 0);
    let top_two_before: Vec<CardHandle> = state.player.draw.iter().copied().collect();
    let outcome = play_hand_card(&mut state, &content, ki_chant, None);
    let StepOutcome::NeedsChoice(req) = outcome else {
        panic!("expected KiChant choice");
    };
    assert_eq!(req.candidates.len(), 3);
    assert!(req.candidates.contains(&top_two_before[0]));
    assert!(req.candidates.contains(&top_two_before[1]));
    assert_eq!(CombatEngine::resume_choice(&mut state, &content, &[0]).unwrap(), StepOutcome::Completed);

    // Now Wah and Ki played; play ChoFirm -> completes 3 melodies -> Lullaby (+20 block) triggers!
    let e_before = state.player.energy;
    let cho_firm = add_card_to_hand(&mut state, &content, CardId::ChoFirm, 0);
    play_hand_card(&mut state, &content, cho_firm, None);
    // ChoFirm gives 6*2=12 block + Lullaby 20 block = 32; ChoFirm costs 1 energy
    assert!(state.player.block >= 32);
    assert_eq!(state.player.energy, e_before - 1);
    assert_eq!(sts2_kernel::ordering::resolve_energy_cost(&mut state, &content, coda).unwrap(), 0);

    // Test YahPower (+2 energy once per turn when arrow dislodged)
    let quick_y = add_card_to_hand(&mut state, &content, CardId::QuickShot, 0);
    play_hand_card(&mut state, &content, quick_y, Some(enemy));
    let e_yah = state.player.energy;
    let wrest_y = add_card_to_hand(&mut state, &content, CardId::Wrest, 0);
    play_hand_card(&mut state, &content, wrest_y, Some(enemy));
    assert_eq!(state.player.energy, e_yah + 2);

    // Play remaining melody cards: HarmonicShield, RhythmStrike, SteadyCadence, MelodicShot, WahBlast, WahSweep, WahCho, WahKi, WahChoKi, WahSymphony, ChoBarbs, ChoReady, ChoKi, KiRest, KiVerse, KiWhip, ChoChorus
    for cid in [
        CardId::HarmonicShield,
        CardId::ChoBarbs,
        CardId::ChoReady,
        CardId::ChoKi,
        CardId::KiRest,
        CardId::KiVerse,
        CardId::Encore,
        CardId::Motif,
        CardId::SongPath,
        CardId::ViolentChord,
        CardId::Tune,
        CardId::LostLute,
        CardId::MythicForm,
    ] {
        clear_hand(&mut state);
        state.player.energy = 10;
        let upg = i32::from(cid == CardId::MythicForm);
        let h = add_card_to_hand(&mut state, &content, cid, upg);
        play_hand_card(&mut state, &content, h, None);
        // Clear EncorePower so we can keep testing cards in this unit test
        let _ = power::remove_by_id(&mut state, &content, pc, PowerId::EncorePower);
    }

    for cid in [
        CardId::Coda,
        CardId::RhythmStrike,
        CardId::SteadyCadence,
        CardId::MelodicShot,
        CardId::WahCho,
        CardId::WahKi,
        CardId::WahChoKi,
        CardId::KiWhip,
    ] {
        clear_hand(&mut state);
        state.player.energy = 10;
        let h = add_card_to_hand(&mut state, &content, cid, 0);
        play_hand_card(&mut state, &content, h, Some(enemy));
    }

    for cid in [CardId::WahBlast, CardId::WahSweep, CardId::WahSymphony] {
        clear_hand(&mut state);
        state.player.energy = 10;
        let h = add_card_to_hand(&mut state, &content, cid, 0);
        play_hand_card(&mut state, &content, h, None);
    }

    clear_hand(&mut state);
    let _wah_in_hand = add_card_to_hand(&mut state, &content, CardId::WahSweep, 0);
    let cho_chorus = add_card_to_hand(&mut state, &content, CardId::ChoChorus, 0);
    state.player.energy = 10;
    play_hand_card(&mut state, &content, cho_chorus, None);
}

#[test]
fn tarot_growth_showoff_bounce_florescence_mourning_and_all_remaining_cards() {
    let content = Sts2Content;
    let (mut state, enemies) = build_fool_battle(&[40, 100], &[], &[CardId::StrikeFool; 5], &[]);
    let weak_enemy = enemies[0];
    let tank_enemy = enemies[1];
    let pc = state.player.creature_handle;

    // ShowOff + fatal kill updates ShowOff._currentAim and comparison_base_values
    let show_off = add_card_to_hand(&mut state, &content, CardId::ShowOff, 0);
    play_hand_card(&mut state, &content, show_off, None);
    assert_eq!(power_amount(&state, pc, PowerId::AimPower), 5);
    assert_eq!(power_amount(&state, pc, PowerId::ShowOffPower), 2);

    // Timber kills weak_enemy (40hp -> 20 dmg, wait: let's hit it first with StrikeFool*4? Or DislikeYou + Timber)
    let timber = add_card_to_hand(&mut state, &content, CardId::Timber, 0);
    state.creatures.iter_mut().find(|c| c.handle == weak_enemy).unwrap().current_hp = 15;
    play_hand_card(&mut state, &content, timber, Some(weak_enemy));
    // weak_enemy killed -> Timber triggers 30 AoE on tank_enemy (100 -> 70), ShowOffPower increments ShowOff aim 5 -> 7!
    assert_eq!(state.creature(tank_enemy).unwrap().current_hp, 70);
    assert_eq!(
        fool::comparison_base_values(&state, show_off).unwrap().as_slice(),
        &[("AimPower", Dec::from_i32(7))]
    );

    // Bounce returns to hand before hand draw on turn 2
    let bounce = add_card_to_hand(&mut state, &content, CardId::Bounce, 0);
    play_hand_card(&mut state, &content, bounce, None);
    assert_eq!(power_amount(&state, pc, PowerId::StretchPower), 10);
    assert_eq!(state.card(bounce).unwrap().pile, PileType::Discard);
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.card(bounce).unwrap().pile, PileType::Hand);

    state.player.energy = 20;
    // Anguish, BrightenUp, Jest, PunchOfJoy, Rummage, SorrowfulStrike, Vitriol, Growth
    let jest = add_card_to_hand(&mut state, &content, CardId::Jest, 0);
    play_hand_card(&mut state, &content, jest, None);
    assert_eq!(state.player.counters.sadness_generated_this_combat, 1);

    let anguish = add_card_to_hand(&mut state, &content, CardId::Anguish, 0);
    let hp0 = state.creature(tank_enemy).unwrap().current_hp;
    play_hand_card(&mut state, &content, anguish, Some(tank_enemy));
    assert_eq!(state.creature(tank_enemy).unwrap().current_hp, hp0 - 12);

    let punch = add_card_to_hand(&mut state, &content, CardId::PunchOfJoy, 0);
    play_hand_card(&mut state, &content, punch, Some(tank_enemy));
    assert!(state.player.hand.iter().any(|&h| state.card(h).unwrap().id == CardId::Joy));

    let cards_ref = &state.cards;
    state.player.hand.retain(|h| {
        cards_ref
            .iter()
            .find(|c| c.handle == *h)
            .unwrap()
            .id
            != CardId::StrikeFool
    });
    state.invalidate_pile_hooks(sts2_kernel::state::HOOK_DIRTY_HAND);
    let brighten = add_card_to_hand(&mut state, &content, CardId::BrightenUp, 0);
    play_hand_card(&mut state, &content, brighten, None);
    let growth = add_card_to_hand(&mut state, &content, CardId::Growth, 0);
    let stretch_before = power_amount(&state, pc, PowerId::StretchPower);
    play_hand_card(&mut state, &content, growth, None);
    assert!(power_amount(&state, pc, PowerId::StretchPower) > stretch_before);

    // BlotOutTheSky generates a Shoot card set to free this turn
    clear_hand(&mut state);
    let blot = add_card_to_hand(&mut state, &content, CardId::BlotOutTheSky, 0);
    let hand_before = state.player.hand.len();
    play_hand_card(&mut state, &content, blot, None);
    assert_eq!(state.player.hand.len(), hand_before); // blot left hand, 1 shoot added
    let generated_h = *state.player.hand.last().unwrap();
    assert_eq!(
        sts2_kernel::ordering::resolve_energy_cost(&mut state, &content, generated_h).unwrap(),
        0
    );

    // Florescence exhausts all Shoot cards and buffs TheShot
    let florescence = add_card_to_hand(&mut state, &content, CardId::Florescence, 0);
    play_hand_card(&mut state, &content, florescence, None);
    let the_shot = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::TheShot)
        .unwrap();
    let count_field = state
        .card_ext(state.card(the_shot).unwrap().ext_index)
        .unwrap()
        .private_fields
        .get("TheShot._cardsExhaustedByFlorescence")
        .cloned();
    assert_eq!(count_field, Some(FieldValue::Decimal(Dec::ONE)));

    // MourningBlow sets transform_into_after_play + returns transformed Sadness to Hand via default_result_location
    let mourning = add_card_to_hand(&mut state, &content, CardId::MourningBlow, 0);
    assert_eq!(
        fool::default_result_location(&state, mourning, false).unwrap(),
        Some((PileType::Hand, CardPilePosition::Bottom))
    );
    let sadness_out = play_hand_card(&mut state, &content, mourning, Some(tank_enemy));
    assert_eq!(sadness_out, StepOutcome::Completed);
    assert!(state.card(mourning).unwrap().removed_from_state);
    assert!(
        state
            .player
            .hand
            .iter()
            .any(|&h| state.card(h).is_some_and(|c| c.id == CardId::Sadness))
    );

    // Remaining cards: AllIsWell, Archery, Brace, Compost, CoveringFire, DeepStretch, DislikeYou,
    // Ecstasy, Ennui, Ensemble, ExplosiveArrows, HiddenRoots, Honing, Rummage, SelfDeprecate,
    // SerratedArrows, Simpleton, SorrowfulStrike, Vitriol, Yoga, Enrapture
    for cid in [
        CardId::AllIsWell,
        CardId::Archery,
        CardId::Brace,
        CardId::Compost,
        CardId::CoveringFire,
        CardId::DeepStretch,
        CardId::Ecstasy,
        CardId::Ennui,
        CardId::Ensemble,
        CardId::ExplosiveArrows,
        CardId::HiddenRoots,
        CardId::Honing,
        CardId::Rummage,
        CardId::SelfDeprecate,
        CardId::SerratedArrows,
        CardId::Simpleton,
        CardId::Yoga,
    ] {
        clear_hand(&mut state);
        state.player.energy = 10;
        let h = add_card_to_hand(&mut state, &content, cid, 0);
        play_hand_card(&mut state, &content, h, None);
    }

    for cid in [CardId::DislikeYou, CardId::SorrowfulStrike, CardId::Vitriol] {
        clear_hand(&mut state);
        state.player.energy = 10;
        let h = add_card_to_hand(&mut state, &content, cid, 0);
        play_hand_card(&mut state, &content, h, Some(tank_enemy));
    }

    // Enrapture directly on player via on_play (TargetType::AnyAlly normally requires another ally)
    clear_hand(&mut state);
    let enrapture = add_card_to_hand(&mut state, &content, CardId::Enrapture, 0);
    let enr_ctx = CardPlayContext {
        card: enrapture,
        target: Some(pc),
        result_pile: PileType::Discard,
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
    fool::on_play(&content, &mut state, &enr_ctx, &mut cq, 0).unwrap();
    assert!(power_amount(&state, pc, PowerId::VigorPower) >= 6);
}
