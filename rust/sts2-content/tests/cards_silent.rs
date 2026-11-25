use smallvec::SmallVec;
use std::sync::Arc;
use sts2_content::Sts2Content;
use sts2_kernel::commands::{card as card_cmd, card_pile, power};
use sts2_kernel::content_dispatch::ContentDispatcher;
use sts2_kernel::{
    BattleState, CardHandle, CardInst, CardTempFlags, CombatEngine, CreatureHandle, CreatureInst,
    MonsterInst, PlayerCounters, PlayerInst, RootStaticContext, StepOutcome,
};
use sts2_types::{
    generated_ids::generated_card_meta, CardId, CardKeywords, CardPartition, CardStaticFlags,
    CharacterId, CombatSide, Dec, EnchantmentId, GameMode, HookSlot, MonsterId, PileType,
    PlayerRngSet, PlayerTurnPhase, PowerId, Rng, RoomType, RunRngSet, CARD_POOL_SILENT,
    card_partition,
};

fn make_card_inst(content: &dyn ContentDispatcher, handle: CardHandle, id: CardId, pile: PileType) -> CardInst {
    let meta = content.card_meta(id).unwrap();
    let mut temp_flags = CardTempFlags::empty();
    if meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST) {
        temp_flags |= CardTempFlags::COSTS_X_ENERGY;
    }
    CardInst {
        handle,
        id,
        upgrade_level: 0,
        canonical_base_energy_cost: meta.canonical_energy_cost as i16,
        base_energy_cost: meta.canonical_energy_cost as i16,
        captured_energy_x_value: 0,
        base_replay_count: 0,
        floor_added_to_deck: sts2_kernel::OptI16::some(1),
        deck_card_handle: sts2_kernel::OptCardHandle::some(handle),
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

fn build_combat(
    hand_ids: &[CardId],
    draw_ids: &[CardId],
    enemy_hps: &[i32],
    energy: i32,
) -> (BattleState, Vec<CardHandle>, Vec<CreatureHandle>) {
    let run_rngs = RunRngSet::from_string_seed("SILENTTEST1");
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc_handle = CreatureHandle(1);

    let mut cards = Vec::new();
    let mut hand = Vec::new();
    let mut draw = Vec::new();
    let mut next_card = 1u32;

    for &cid in hand_ids {
        let h = CardHandle(next_card);
        next_card += 1;
        cards.push(make_card_inst(&Sts2Content, h, cid, PileType::Hand));
        hand.push(h);
    }
    for &cid in draw_ids {
        let h = CardHandle(next_card);
        next_card += 1;
        cards.push(make_card_inst(&Sts2Content, h, cid, PileType::Draw));
        draw.push(h);
    }

    let player_creature = CreatureInst {
        handle: pc_handle,
        combat_id: Some(0),
        side: CombatSide::Player,
        slot_name: None,
        is_player: true,
        pet_owner_player_index: None,
        monster_id: None,
        current_hp: 70,
        max_hp: 70,
        block: 0,
        cumulative_hp_lost: 0,
        is_detached: false,
        monster: None,
        has_minion_power: false,
        powers: Vec::new(),
    };

    let mut creatures = vec![player_creature];
    let mut enemy_handles = Vec::new();
    let mut slots = Vec::new();
    for (idx, &hp) in enemy_hps.iter().enumerate() {
        let eh = CreatureHandle((idx + 2) as u32);
        let slot = format!("slot_{idx}");
        slots.push(slot.clone());
        enemy_handles.push(eh);
        creatures.push(CreatureInst {
            handle: eh,
            combat_id: Some((idx + 1) as u32),
            side: CombatSide::Enemy,
            slot_name: Some(std::borrow::Cow::Owned(slot)),
            is_player: false,
            pet_owner_player_index: None,
            monster_id: Some(MonsterId::ShrinkerBeetle),
            current_hp: hp,
            max_hp: hp,
            block: 0,
            cumulative_hp_lost: 0,
            is_detached: false,
            monster: Some(Box::new(MonsterInst {
                monster_id: MonsterId::ShrinkerBeetle,
                rng: Rng::from_seed(run_rngs.seed + (idx as u64) + 1),
                is_performing_move: false,
                spawned_this_turn: false,
                current_state_id: std::borrow::Cow::Borrowed("SHRINKER_MOVE"),
                next_move_id: Some(std::borrow::Cow::Borrowed("CHOMP_MOVE")),
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
        current_hp: 70,
        max_hp: 70,
        block: 0,
        cumulative_hp_lost: 0,
        gold: 99,
        max_energy: 3,
        base_orb_slot_count: 0,
        is_active_for_hooks: true,
        can_use_or_remove_potions: true,
        energy,
        stars: 0,
        phase: PlayerTurnPhase::Play,
        turn_number: 1,
        counters: PlayerCounters::default(),
        hand: hand.clone(),
        draw,
        discard: Vec::new(),
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
    };

    let state = BattleState {
        static_ctx: Arc::new(RootStaticContext {
            encounter_id: "TEST_ENCOUNTER".to_owned(),
            encounter_is_weak: false,
            room_type: RoomType::Monster,
            encounter_slots: slots,
            game_mode: GameMode::Standard,
            ascension: 0,
            character_id: CharacterId::Silent,
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
        battleworn_dummy_timed_out: false,
        end_player_turn_requested: false,
        card_action_boundary_depth: 0,
        next_creature_id: (enemy_hps.len() + 1) as u32,
        initial_encounter_monster_count: enemy_hps.len(),
        next_card_handle: next_card,
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
        attacking_creatures: SmallVec::new(),
        deferred_death_cleanup: SmallVec::new(),
        damage_history: Vec::new(),
        semantic_history: Default::default(),
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

    (state, hand.into_iter().collect(), enemy_handles.into_iter().collect())
}

fn play(
    state: &mut BattleState,
    card: CardHandle,
    target: Option<CreatureHandle>,
    choices: &[&[usize]],
) -> StepOutcome {
    let mut cq: SmallVec<[Vec<usize>; 2]> = choices.iter().map(|s| s.to_vec()).collect();
    CombatEngine::play_card(state, &Sts2Content, card, target, &mut cq).unwrap()
}

fn upgrade_card(state: &mut BattleState, card: CardHandle) {
    card_cmd::upgrade(state, &Sts2Content, card).unwrap();
}

fn power_amt(state: &BattleState, ch: CreatureHandle, pid: PowerId) -> i32 {
    state
        .creature(ch)
        .map(|c| {
            c.powers
                .iter()
                .filter(|p| p.id == pid)
                .map(|p| p.amount)
                .sum()
        })
        .unwrap_or(0)
}

#[test]
fn all_91_silent_cards_have_metadata_upgrade_and_zero_unsupported() {
    assert_eq!(CARD_POOL_SILENT.len(), 91);
    for &cid in CARD_POOL_SILENT {
        assert_eq!(card_partition(cid), CardPartition::Silent);
        let meta = Sts2Content.card_meta(cid).unwrap();
        assert_eq!(meta.card_type, generated_card_meta(cid).card_type);
        assert_eq!(meta.rarity, generated_card_meta(cid).rarity);
        assert_eq!(meta.target_type, generated_card_meta(cid).target_type);
        assert_eq!(
            meta.canonical_energy_cost,
            generated_card_meta(cid).canonical_energy_cost
        );
        assert_eq!(meta.hook_mask, generated_card_meta(cid).hook_mask);
    }

    assert!(sts2_content::cards::silent::card_meta(CardId::StrikeIronclad).is_err());
    assert!(Sts2Content
        .card_meta(CardId::TheHunt)
        .unwrap()
        .flags
        .contains(CardStaticFlags::HAS_GROWTH));
    assert!(Sts2Content
        .card_meta(CardId::BouncingFlask)
        .unwrap()
        .flags
        .contains(CardStaticFlags::USES_RNG));
    assert!(Sts2Content
        .card_meta(CardId::Ricochet)
        .unwrap()
        .flags
        .contains(CardStaticFlags::USES_RNG));
    assert!(Sts2Content
        .card_meta(CardId::Skewer)
        .unwrap()
        .flags
        .contains(CardStaticFlags::IS_X_ENERGY_COST));
    assert!(Sts2Content
        .card_meta(CardId::Malaise)
        .unwrap()
        .flags
        .contains(CardStaticFlags::IS_X_ENERGY_COST));
    assert!(!Sts2Content
        .card_meta(CardId::GrandFinale)
        .unwrap()
        .hook_mask
        .contains(HookSlot::ShouldPlay));
    let (mut gf_state, gf_hand, _) = build_combat(&[CardId::GrandFinale, CardId::StrikeSilent], &[], &[50], 10);
    let gf_h = gf_hand[0];
    let filler_h = gf_hand[1];
    assert!(Sts2Content.card_is_playable(&gf_state, gf_h).unwrap());
    gf_state.player.draw.push(filler_h);
    assert!(!Sts2Content.card_is_playable(&gf_state, gf_h).unwrap());
    assert!(Sts2Content
        .card_meta(CardId::Pinpoint)
        .unwrap()
        .hook_mask
        .contains(HookSlot::AfterCardEnteredCombat));
    assert!(Sts2Content
        .card_meta(CardId::Pinpoint)
        .unwrap()
        .hook_mask
        .contains(HookSlot::AfterCardPlayed));

    let (mut state, hand, _) = build_combat(CARD_POOL_SILENT, &[], &[50], 10);
    for &ch in &hand {
        assert!(Sts2Content.card_is_playable(&state, ch).unwrap());
        assert!(Sts2Content
            .card_comparison_base_values(&state, ch)
            .unwrap()
            .is_empty());
        Sts2Content.card_on_upgrade(&mut state, ch).unwrap();
    }

    // Verify upgraded cost reductions & keyword mutations
    let (mut ustate, uhand, _) = build_combat(
        &[
            CardId::Afterimage,
            CardId::CalculatedGamble,
            CardId::Mirage,
            CardId::ToolsOfTheTrade,
            CardId::WellLaidPlans,
            CardId::BulletTime,
            CardId::Murder,
            CardId::Nightmare,
        ],
        &[],
        &[50],
        10,
    );
    for &h in &uhand {
        upgrade_card(&mut ustate, h);
    }
    assert!(ustate.card(uhand[0]).unwrap().local_keywords.contains(CardKeywords::INNATE));
    assert!(ustate.card(uhand[1]).unwrap().local_keywords.contains(CardKeywords::RETAIN));
    assert!(!ustate.card(uhand[2]).unwrap().local_keywords.contains(CardKeywords::EXHAUST));
    assert_eq!(ustate.card(uhand[3]).unwrap().base_energy_cost, 0);
    assert_eq!(ustate.card(uhand[4]).unwrap().base_energy_cost, 1);
    assert_eq!(ustate.card(uhand[5]).unwrap().base_energy_cost, 2);
    assert_eq!(ustate.card(uhand[6]).unwrap().base_energy_cost, 2);
    assert_eq!(ustate.card(uhand[7]).unwrap().base_energy_cost, 2);
}

#[test]
fn poison_cards_and_outbreak_and_bubble_bubble_and_mirage() {
    let (mut state, hand, enemies) = build_combat(
        &[
            CardId::DeadlyPoison,
            CardId::BubbleBubble,
            CardId::PoisonedStab,
            CardId::Snakebite,
            CardId::Haze,
            CardId::Mirage,
            CardId::Outbreak,
        ],
        &[],
        &[100, 100],
        12,
    );
    let e0 = enemies[0];
    let e1 = enemies[1];

    // BubbleBubble does nothing without poison
    assert_eq!(play(&mut state, hand[1], Some(e1), &[]), StepOutcome::Completed);
    assert_eq!(power_amt(&state, e1, PowerId::PoisonPower), 0);

    // DeadlyPoison applies 5, then BubbleBubble applies 9 -> 14
    assert_eq!(play(&mut state, hand[0], Some(e0), &[]), StepOutcome::Completed);
    assert_eq!(power_amt(&state, e0, PowerId::PoisonPower), 5);
    let (mut s2, h2, en2) = build_combat(&[CardId::DeadlyPoison, CardId::BubbleBubble], &[], &[100], 5);
    play(&mut s2, h2[0], Some(en2[0]), &[]);
    play(&mut s2, h2[1], Some(en2[0]), &[]);
    assert_eq!(power_amt(&s2, en2[0], PowerId::PoisonPower), 14);

    // PoisonedStab: 6 dmg + 3 poison
    play(&mut state, hand[2], Some(e0), &[]);
    assert_eq!(state.creature(e0).unwrap().current_hp, 94);
    assert_eq!(power_amt(&state, e0, PowerId::PoisonPower), 8);

    // Snakebite: 7 poison on e1
    play(&mut state, hand[3], Some(e1), &[]);
    assert_eq!(power_amt(&state, e1, PowerId::PoisonPower), 7);

    // Haze: +4 poison, +1 weak on all
    play(&mut state, hand[4], None, &[]);
    assert_eq!(power_amt(&state, e0, PowerId::PoisonPower), 12);
    assert_eq!(power_amt(&state, e1, PowerId::PoisonPower), 11);
    assert_eq!(power_amt(&state, e0, PowerId::WeakPower), 1);

    // Mirage: block = sum of enemy poison = 12 + 11 = 23
    play(&mut state, hand[5], None, &[]);
    assert_eq!(state.player.block, 23);

    // Outbreak: +9 poison on both (21, 20), then triggers poison immediately (21 dmg -> 20 stacks, 20 dmg -> 19 stacks)
    play(&mut state, hand[6], None, &[]);
    assert_eq!(state.creature(e0).unwrap().current_hp, 94 - 21);
    assert_eq!(power_amt(&state, e0, PowerId::PoisonPower), 20);
    assert_eq!(state.creature(e1).unwrap().current_hp, 100 - 20);
    assert_eq!(power_amt(&state, e1, PowerId::PoisonPower), 19);
}

#[test]
fn discard_sly_reflex_tactician_calculated_gamble_hand_trick() {
    let (mut state, hand, enemies) = build_combat(
        &[
            CardId::Prepared,
            CardId::Reflex,
            CardId::Tactician,
            CardId::DefendSilent,
            CardId::HandTrick,
            CardId::CalculatedGamble,
        ],
        &[CardId::Slice, CardId::Deflect, CardId::Backstab, CardId::StrikeSilent, CardId::Neutralize],
        &[60],
        3,
    );
    let _ = enemies;
    // Play Prepared (draws Slice, then pauses for discard choice)
    let out = play(&mut state, hand[0], None, &[]);
    assert!(matches!(out, StepOutcome::NeedsChoice(_)));
    // Choose Tactician (index 1 in hand after Prepared left hand: [Reflex, Tactician, DefendSilent, HandTrick, CalculatedGamble, Slice])
    let out = CombatEngine::resume_choice(&mut state, &Sts2Content, &[1]).unwrap();
    assert_eq!(out, StepOutcome::Completed);
    // Tactician had Sly -> autoplayed and gave +1 energy (3 - 0 + 1 = 4)
    assert_eq!(state.player.energy, 4);
    assert_eq!(state.player.counters.cards_discarded_this_turn, 1);

    // Play HandTrick -> +7 block, pauses to pick a non-Sly Skill in hand ([DefendSilent, CalculatedGamble, Deflect? no Slice is Attack])
    let out = play(&mut state, hand[4], None, &[]);
    assert!(matches!(out, StepOutcome::NeedsChoice(_)));
    // Choose DefendSilent (index 1: candidates are [DefendSilent, CalculatedGamble])
    let out = CombatEngine::resume_choice(&mut state, &Sts2Content, &[0]).unwrap();
    assert_eq!(out, StepOutcome::Completed);
    assert_eq!(state.player.block, 7);
    assert!(state.card(hand[3]).unwrap().temp_flags.contains(CardTempFlags::TEMPORARY_SLY_TURN));

    // Play CalculatedGamble -> discards remaining hand ([Reflex, DefendSilent(Sly), Slice]) and draws 3,
    // THEN Reflex draws 2 and DefendSilent gives +5 block!
    play(&mut state, hand[5], None, &[]);
    // Hand had 3 cards discarded, drew 3, then Reflex drew 2 -> 5 cards in hand (actually draw pile only had 4 left + discard?)
    // Draw had [Deflect, Backstab, StrikeSilent, Neutralize] (4 left):
    // CalculatedGamble drew 3 (Deflect, Backstab, StrikeSilent), Reflex drew 2 (Neutralize + shuffle 1).
    assert_eq!(state.player.block, 12);
    assert_eq!(state.player.counters.cards_discarded_this_turn, 4);
}

#[test]
fn shiv_generators_blade_of_ink_knife_trap_up_my_sleeve() {
    let (mut state, hand, enemies) = build_combat(
        &[
            CardId::BladeDance,
            CardId::CloakAndDagger,
            CardId::LeadingStrike,
            CardId::BladeOfInk,
            CardId::HiddenDaggers,
            CardId::UpMySleeve,
            CardId::DefendSilent,
        ],
        &[],
        &[100],
        10,
    );
    let e0 = enemies[0];
    upgrade_card(&mut state, hand[4]); // HiddenDaggers+

    // BladeDance -> 3 Shivs in hand
    play(&mut state, hand[0], None, &[]);
    assert_eq!(
        state.player.hand.iter().filter(|&&h| state.card(h).unwrap().id == CardId::Shiv).count(),
        3
    );

    // Play 1 Shiv -> exhausts (BladeDance also exhausted, so 2 cards in exhaust)
    let shiv0 = state.player.hand.iter().copied().find(|&h| state.card(h).unwrap().id == CardId::Shiv).unwrap();
    play(&mut state, shiv0, Some(e0), &[]);
    assert_eq!(state.creature(e0).unwrap().current_hp, 96);
    assert_eq!(state.player.exhaust.len(), 2);

    // BladeOfInk -> 2 Inky Shivs
    play(&mut state, hand[3], None, &[]);
    let inky_shiv = state
        .player
        .hand
        .iter()
        .copied()
        .find(|&h| {
            let c = state.card(h).unwrap();
            c.id == CardId::Shiv
                && state
                    .card_ext(c.ext_index)
                    .and_then(|e| e.enchantment.as_ref())
                    .is_some_and(|en| en.id == EnchantmentId::Inky)
        })
        .unwrap();
    play(&mut state, inky_shiv, Some(e0), &[]);
    assert_eq!(power_amt(&state, e0, PowerId::WeakPower), 1);

    // UpMySleeve -> 3 Shivs and cost reduced from 2 to 1
    assert_eq!(state.player.energy, 8);
    play(&mut state, hand[5], None, &[]);
    assert_eq!(state.player.energy, 6);
    assert_eq!(
        sts2_kernel::ordering::resolve_energy_cost(&mut state, &Sts2Content, hand[5]).unwrap(),
        1
    );

    // HiddenDaggers+ -> discard 2, create 2 Shiv+ (note hand cap = 10, discard frees 2 slots)
    let prev_hand_len = state.player.hand.len();
    play(&mut state, hand[4], None, &[&[0, 1]]);
    assert!(state.player.hand.iter().any(|&h| {
        let c = state.card(h).unwrap();
        c.id == CardId::Shiv && c.upgrade_level == 1
    }));
    let _ = prev_hand_len;

    // KnifeTrap+ -> replays exhausted Shivs (upgraded)
    let (mut ks, kh, ken) = build_combat(&[CardId::KnifeTrap, CardId::BladeDance], &[], &[100], 5);
    upgrade_card(&mut ks, kh[0]);
    play(&mut ks, kh[1], None, &[]);
    let shivs: Vec<CardHandle> = ks
        .player
        .hand
        .iter()
        .copied()
        .filter(|&h| ks.card(h).unwrap().id == CardId::Shiv)
        .collect();
    for s in shivs {
        play(&mut ks, s, Some(ken[0]), &[]);
    }
    assert_eq!(ks.creature(ken[0]).unwrap().current_hp, 100 - 12);
    play(&mut ks, kh[0], Some(ken[0]), &[]);
    // 3 Shiv+ = 18 dmg
    assert_eq!(ks.creature(ken[0]).unwrap().current_hp, 100 - 12 - 18);
}

#[test]
fn grand_finale_pinpoint_x_cost_scaling_and_nightmare() {
    // GrandFinale playable only when draw pile is empty
    let (mut state, hand, _) = build_combat(&[CardId::GrandFinale], &[CardId::Slice], &[100], 3);
    assert!(!CombatEngine::can_play_card(&mut state, &Sts2Content, hand[0]).unwrap());
    let _ = card_pile::draw(&mut state, &Sts2Content, 1, false).unwrap();
    assert!(CombatEngine::can_play_card(&mut state, &Sts2Content, hand[0]).unwrap());
    play(&mut state, hand[0], None, &[]);
    assert_eq!(state.creature(state.enemy_handles[0]).unwrap().current_hp, 40);

    // Pinpoint cost decreases when Skill is played, and resets at turn end
    let (mut pstate, phand, pen) = build_combat(
        &[CardId::Pinpoint, CardId::DefendSilent, CardId::Deflect],
        &[],
        &[100],
        3,
    );
    assert_eq!(
        sts2_kernel::ordering::resolve_energy_cost(&mut pstate, &Sts2Content, phand[0]).unwrap(),
        3
    );
    play(&mut pstate, phand[1], None, &[]);
    assert_eq!(
        sts2_kernel::ordering::resolve_energy_cost(&mut pstate, &Sts2Content, phand[0]).unwrap(),
        2
    );
    play(&mut pstate, phand[2], None, &[]);
    assert_eq!(
        sts2_kernel::ordering::resolve_energy_cost(&mut pstate, &Sts2Content, phand[0]).unwrap(),
        1
    );
    play(&mut pstate, phand[0], Some(pen[0]), &[]);
    assert_eq!(pstate.creature(pen[0]).unwrap().current_hp, 85);

    // Skewer + Malaise X-cost
    let (mut xstate, xhand, xen) = build_combat(&[CardId::Skewer, CardId::Malaise], &[], &[100], 3);
    play(&mut xstate, xhand[0], Some(xen[0]), &[]);
    assert_eq!(xstate.creature(xen[0]).unwrap().current_hp, 100 - 24);
    assert_eq!(xstate.player.energy, 0);
    xstate.player.energy = 2;
    upgrade_card(&mut xstate, xhand[1]);
    play(&mut xstate, xhand[1], Some(xen[0]), &[]);
    assert_eq!(power_amt(&xstate, xen[0], PowerId::StrengthPower), -3);
    assert_eq!(power_amt(&xstate, xen[0], PowerId::WeakPower), 3);

    // Nightmare clones 3 copies into hand at start of next turn
    let (mut nstate, nhand, _) = build_combat(
        &[CardId::Nightmare, CardId::Backstab],
        &[CardId::StrikeSilent, CardId::DefendSilent, CardId::Slice, CardId::Deflect, CardId::Neutralize],
        &[100],
        3,
    );
    play(&mut nstate, nhand[0], None, &[&[0]]);
    assert_eq!(power_amt(&nstate, nstate.player.creature_handle, PowerId::NightmarePower), 3);
    CombatEngine::end_player_turn(&mut nstate, &Sts2Content).unwrap();
    let backstabs_in_hand = nstate
        .player
        .hand
        .iter()
        .filter(|&&h| nstate.card(h).unwrap().id == CardId::Backstab)
        .count();
    assert_eq!(backstabs_in_hand, 3);
}

#[test]
fn echoing_slash_the_hunt_bullet_time_expose_burst_memento_precise_cut_finisher_flechettes() {
    // EchoingSlash repeats on kill
    let (mut es, eh, een) = build_combat(&[CardId::EchoingSlash], &[], &[10, 10, 25], 3);
    play(&mut es, eh[0], None, &[]);
    // Hit 1: kills een[0], een[1], een[2] goes 25->15 -> +2 attacks -> Hit 2: 15->5 -> Hit 3: 5->0
    assert!(es.creature(een[2]).unwrap().is_dead());

    // TheHunt fatal kill grants TheHuntPower (with another enemy alive so combat isn't ending)
    let (mut ths, thh, then) = build_combat(&[CardId::TheHunt], &[], &[10, 50], 3);
    play(&mut ths, thh[0], Some(then[0]), &[]);
    assert_eq!(power_amt(&ths, ths.player.creature_handle, PowerId::TheHuntPower), 1);

    // BulletTime makes hand free (except X-cost) and applies NoDrawPower
    let (mut bts, bth, _) = build_combat(
        &[CardId::BulletTime, CardId::Predator, CardId::Skewer],
        &[CardId::Slice],
        &[50],
        3,
    );
    play(&mut bts, bth[0], None, &[]);
    assert_eq!(
        sts2_kernel::ordering::resolve_energy_cost(&mut bts, &Sts2Content, bth[1]).unwrap(),
        0
    );
    assert_eq!(power_amt(&bts, bts.player.creature_handle, PowerId::NoDrawPower), 1);

    // Expose removes block + Artifact, applies Vulnerable
    let (mut exs, exh, exen) = build_combat(&[CardId::Expose], &[], &[50], 3);
    exs.creature_mut(exen[0]).unwrap().block = 12;
    power::apply(
        &mut exs,
        &Sts2Content,
        PowerId::ArtifactPower,
        exen[0],
        Dec::ONE,
        None,
        None,
    )
    .unwrap();
    play(&mut exs, exh[0], Some(exen[0]), &[]);
    assert_eq!(exs.creature(exen[0]).unwrap().block, 0);
    assert_eq!(power_amt(&exs, exen[0], PowerId::ArtifactPower), 0);
    assert_eq!(power_amt(&exs, exen[0], PowerId::VulnerablePower), 2);

    // MementoMori targets lowest-HP enemy; PreciseCut scales by remaining hand; Flechettes by Skills; Finisher by attacks played
    let (mut ms, mh, men) = build_combat(
        &[
            CardId::MementoMori,
            CardId::Slice,
            CardId::DefendSilent,
            CardId::Deflect,
            CardId::Flechettes,
            CardId::PreciseCut,
            CardId::Finisher,
        ],
        &[],
        &[80, 30],
        10,
    );
    play(&mut ms, mh[0], None, &[]);
    assert_eq!(ms.creature(men[1]).unwrap().current_hp, 11);
    play(&mut ms, mh[1], Some(men[0]), &[]); // 1 attack played
    // Hand now has: DefendSilent, Deflect, Flechettes, PreciseCut, Finisher (2 Skills)
    play(&mut ms, mh[4], Some(men[0]), &[]); // Flechettes -> 2 hits * 5 = 10 dmg
    assert_eq!(ms.creature(men[0]).unwrap().current_hp, 80 - 6 - 10);
    // Hand now has: DefendSilent, Deflect, PreciseCut, Finisher (3 other cards when PreciseCut plays)
    play(&mut ms, mh[5], Some(men[0]), &[]); // PreciseCut -> 13 - 2*3 = 7 dmg
    assert_eq!(ms.creature(men[0]).unwrap().current_hp, 80 - 6 - 10 - 7);
    // Finisher -> 3 attacks played (MementoMori, Slice, Flechettes, PreciseCut -> 4!) * 6 = 24 dmg
    play(&mut ms, mh[6], Some(men[0]), &[]);
    assert_eq!(ms.creature(men[0]).unwrap().current_hp, 80 - 6 - 10 - 7 - 24);
}
