use smallvec::SmallVec;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::Arc;
use sts2_content::Sts2Content;
use sts2_kernel::snapshot::export_live_state;
use sts2_kernel::{
    ArrowSlotInst, BattleState, CardHandle, CardInst, CardTempFlags, CombatEngine, ContentDispatcher,
    CreatureHandle, CreatureInst, MonsterInst, PlayerCounters, PlayerInst, RelicInst,
    RootStaticContext, SemanticHistory, StepOutcome,
};
use sts2_search::arena::{LocalStateSlab, MainStateArena, NodeArena, SearchNode};
use sts2_search::eval::{
    BossHpRelief, BossHpStrategy, FullEvalDetails, PotionSlotDirective, PotionUsePolicy, PowerCardValuationRegistry, PowerRoutePriority, RelicCounterId,
    RelicCounterRule, RootEvalBaseline, RouteQuality, RouteQualityPolicy, RouteQualityProjection,
    SolverPotionDirective, SolverPotionPolicy, StoredFastEval, StoredNodeEval,
};
use sts2_search::hash::StateHash;
use sts2_search::novelty::NoveltyPortfolioBudget;
use sts2_search::packer::{empty_scratch_state, pack_state, unpack_state};
use sts2_search::portfolio::{
    BeamWidthPortfolio, BeamWidthPortfolioBaseline, BeamWidthPortfolioGate,
    PortfolioSchedulingMode,
};
use sts2_search::pruning::BeamRetentionPolicy;
use sts2_search::{
    AccelerationFlags, InCombatSolveRequest, PlannedAction, SearchEngine, SearchPolicy,
};
use sts2_types::{
    ArrowStatus, CardId, CardKeywords, CharacterId, CombatSide, GameMode, MonsterId, PileType,
    PlayerRngSet, PlayerTurnPhase, PotionId, RelicId, Rng, RoomType, RunRngSet,
};

fn build_combat(
    character: CharacterId,
    deck_ids: &[CardId],
    relics: &[RelicId],
    potions: &[PotionId],
    enemies: &[(MonsterId, i32)],
    room_type: RoomType,
    seed: &str,
) -> BattleState {
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
        has_minion_power: false,
        powers: Vec::new(),
    };

    let mut creatures = vec![player_creature];
    let mut enemy_handles = Vec::new();
    let mut encounter_slots = Vec::new();

    for (idx, &(enemy_id, enemy_hp)) in enemies.iter().enumerate() {
        let eh = CreatureHandle((idx + 2) as u32);
        let slot = format!("slot_{idx}");
        enemy_handles.push(eh);
        encounter_slots.push(slot.clone());
        creatures.push(CreatureInst {
            handle: eh,
            combat_id: Some((idx + 1) as u32),
            side: CombatSide::Enemy,
            slot_name: Some(std::borrow::Cow::Owned(slot)),
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
                rng: Rng::from_seed(run_rngs.seed + 1 + idx as u64),
                is_performing_move: false,
                spawned_this_turn: false,
                current_state_id: std::borrow::Cow::Borrowed(""),
                next_move_id: None,
                next_move_can_transition_away: true,
                performed_first_move: false,
                state_log_ids: SmallVec::new(),
                transient_move_follow_up_state_id: None,
                setup_fields: Default::default(),
                private_fields: Default::default(),
            }),
            has_minion_power: false,
            powers: Vec::new(),
        });
    }

    let mut potion_slots: SmallVec<[Option<PotionId>; 5]> = smallvec::smallvec![None, None, None];
    for (i, &pid) in potions.iter().take(3).enumerate() {
        potion_slots[i] = Some(pid);
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
        potion_slots,
        combat_generated_potion_mask: 0,
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
                private_fields: Default::default(),
            })
            .collect(),
        card_or_potion_effect_depth: 0,
    };

    let mut state = BattleState {
        static_ctx: Arc::new(RootStaticContext {
            encounter_id: "TEST_ENCOUNTER".to_owned(),
            encounter_is_weak: false,
            room_type,
            encounter_slots,
            game_mode: GameMode::Standard,
            ascension: 0,
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
        damage_history: Vec::new(),
        semantic_history: SemanticHistory::default(),
        pending: None,
        vakuu_auto_select_depth: 0,
        hook_cache: Default::default(),
        deck_dirty: Default::default(),
    };

    CombatEngine::start_combat(&mut state, &content).unwrap();
    state
}

fn replay_actions_to_end(root: &BattleState, actions: &[PlannedAction]) -> BattleState {
    let content = Sts2Content;
    let mut s = root.clone();
    for act in actions {
        match act {
            PlannedAction::TurnSetup { choices } => {
                let mut q: SmallVec<[Vec<usize>; 2]> =
                    choices.clone().unwrap_or_default().into_iter().collect();
                while s.pending.is_some() && !q.is_empty() {
                    let sel = q.remove(0);
                    CombatEngine::resume_choice(&mut s, &content, &sel).unwrap();
                }
            }
            PlannedAction::PlayCard {
                card_handle,
                target_handle,
                choices,
            } => {
                let mut q: SmallVec<[Vec<usize>; 2]> =
                    choices.clone().unwrap_or_default().into_iter().collect();
                let mut out = CombatEngine::play_card(
                    &mut s,
                    &content,
                    CardHandle(*card_handle as u32),
                    target_handle.map(|h| CreatureHandle(h as u32)),
                    &mut q,
                )
                .unwrap();
                while matches!(out, StepOutcome::NeedsChoice(_)) && !q.is_empty() {
                    let sel = q.remove(0);
                    out = CombatEngine::resume_choice(&mut s, &content, &sel).unwrap();
                }
            }
            PlannedAction::UsePotion {
                potion_slot,
                target_handle,
                choices,
                ..
            } => {
                let mut q: SmallVec<[Vec<usize>; 2]> =
                    choices.clone().unwrap_or_default().into_iter().collect();
                let mut out = CombatEngine::use_potion(
                    &mut s,
                    &content,
                    *potion_slot,
                    target_handle.map(|h| CreatureHandle(h as u32)),
                    &mut q,
                )
                .unwrap();
                while matches!(out, StepOutcome::NeedsChoice(_)) && !q.is_empty() {
                    let sel = q.remove(0);
                    out = CombatEngine::resume_choice(&mut s, &content, &sel).unwrap();
                }
            }
            PlannedAction::EndTurn { choices } => {
                let mut q: SmallVec<[Vec<usize>; 2]> =
                    choices.clone().unwrap_or_default().into_iter().collect();
                let mut out = CombatEngine::end_player_turn(&mut s, &content).unwrap();
                while matches!(out, StepOutcome::NeedsChoice(_)) && !q.is_empty() {
                    let sel = q.remove(0);
                    out = CombatEngine::resume_choice(&mut s, &content, &sel).unwrap();
                }
            }
        }
    }
    s
}

#[test]
fn packer_round_trip_and_2305_card_spillover() {
    let mut deck = vec![
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::Bash,
    ];
    let state = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::BurningBlood],
        &[PotionId::FirePotion, PotionId::BlockPotion],
        &[(MonsterId::Nibbit, 35), (MonsterId::ShrinkerBeetle, 28)],
        RoomType::Monster,
        "PACK_TEST",
    );

    let mut buf = Vec::new();
    pack_state(&state, &mut buf);
    let mut unpacked = empty_scratch_state(state.static_ctx.clone());
    unpack_state(&buf, &state.static_ctx, &mut unpacked).unwrap();
    assert_eq!(state, unpacked);

    // 2,305-card deck spillover test across LocalStateSlab & MainStateArena
    deck = (0..2305)
        .map(|i| {
            if i % 3 == 0 {
                CardId::DefendIronclad
            } else {
                CardId::StrikeIronclad
            }
        })
        .collect();
    let huge_state = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::BurningBlood],
        &[],
        &[(MonsterId::Nibbit, 25)],
        RoomType::Monster,
        "HUGE_2305",
    );
    let mut huge_buf = Vec::new();
    pack_state(&huge_state, &mut huge_buf);
    assert!(huge_buf.len() > 64 * 1024);

    let mut local_slab = LocalStateSlab::new(32 * 1024); // smaller than record -> tests spillover!
    let (s_idx, off, len) = local_slab.alloc_slice(&huge_buf);
    let mut main_arena = MainStateArena::new(32 * 1024);
    let handle = main_arena.commit_slice(local_slab.get(s_idx, off, len));

    let mut huge_unpacked = empty_scratch_state(huge_state.static_ctx.clone());
    unpack_state(
        main_arena.get(handle),
        &huge_state.static_ctx,
        &mut huge_unpacked,
    )
    .unwrap();
    assert_eq!(huge_state.cards.len(), huge_unpacked.cards.len());
    assert_eq!(huge_state, huge_unpacked);
}

#[test]
fn determinism_dop_1_4_16_ironclad_multi_enemy() {
    let deck = vec![
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::Bash,
    ];
    let root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::BurningBlood],
        &[],
        &[(MonsterId::Nibbit, 26), (MonsterId::ShrinkerBeetle, 24)],
        RoomType::Monster,
        "DET_IRONCLAD",
    );

    let run_with_dop = |dop: usize| {
        let policy = SearchPolicy {
            beam_width: 24,
            max_nodes: 2500,
            time_budget_ms: 0,
            allow_potions: true,
            allow_random_cards: false,
            max_degree_of_parallelism: dop,
            ..Default::default()
        };
        SearchEngine.solve_state(&root, &policy, &Sts2Content).unwrap()
    };

    let r1 = run_with_dop(1);
    let r4 = run_with_dop(4);
    let r16 = run_with_dop(16);

    assert!(r1.stats.won);
    assert_eq!(r1.actions, r4.actions);
    assert_eq!(r1.actions, r16.actions);
    assert_eq!(r1.score, r4.score);
    assert_eq!(r1.score, r16.score);
    assert_eq!(r1.nodes_expanded, r4.nodes_expanded);
    assert_eq!(r1.nodes_expanded, r16.nodes_expanded);
    assert_eq!(r1.stats.transition_count, r4.stats.transition_count);
    assert_eq!(r1.stats.transition_count, r16.stats.transition_count);

    let end_state = replay_actions_to_end(&root, &r1.actions);
    assert!(end_state.won);
}

#[test]
fn determinism_dop_1_4_16_silent_choices_and_potions() {
    let deck = vec![
        CardId::StrikeSilent,
        CardId::StrikeSilent,
        CardId::StrikeSilent,
        CardId::StrikeSilent,
        CardId::StrikeSilent,
        CardId::DefendSilent,
        CardId::DefendSilent,
        CardId::DefendSilent,
        CardId::DefendSilent,
        CardId::Neutralize,
        CardId::Survivor,
    ];
    let root = build_combat(
        CharacterId::Silent,
        &deck,
        &[RelicId::RingOfTheSnake],
        &[
            PotionId::FirePotion,
            PotionId::BlockPotion,
            PotionId::EnergyPotion,
        ],
        &[(MonsterId::Nibbit, 42)],
        RoomType::Monster,
        "DET_SILENT",
    );

    let run_with_dop = |dop: usize| {
        let policy = SearchPolicy {
            beam_width: 24,
            max_nodes: 2500,
            time_budget_ms: 0,
            allow_potions: true,
            allow_random_cards: false,
            max_degree_of_parallelism: dop,
            ..Default::default()
        };
        SearchEngine.solve_state(&root, &policy, &Sts2Content).unwrap()
    };

    let r1 = run_with_dop(1);
    let r4 = run_with_dop(4);
    let r16 = run_with_dop(16);

    assert!(r1.stats.won);
    assert_eq!(r1.actions, r4.actions);
    assert_eq!(r1.actions, r16.actions);
    assert_eq!(r1.score, r4.score);
    assert_eq!(r1.score, r16.score);
    assert_eq!(r1.nodes_expanded, r4.nodes_expanded);
    assert_eq!(r1.nodes_expanded, r16.nodes_expanded);
    assert_eq!(r1.stats.transition_count, r4.stats.transition_count);
    assert_eq!(r1.stats.transition_count, r16.stats.transition_count);

    let end_state = replay_actions_to_end(&root, &r1.actions);
    assert!(end_state.won);
}

#[test]
fn lossless_ablation_preserves_quality_and_cuts_work() {
    let deck = vec![
        CardId::StrikeSilent,
        CardId::StrikeSilent,
        CardId::StrikeSilent,
        CardId::StrikeSilent,
        CardId::StrikeSilent,
        CardId::DefendSilent,
        CardId::DefendSilent,
        CardId::DefendSilent,
        CardId::DefendSilent,
        CardId::Neutralize,
        CardId::Survivor,
    ];
    let root = build_combat(
        CharacterId::Silent,
        &deck,
        &[RelicId::RingOfTheSnake],
        &[],
        &[(MonsterId::FuzzyWurmCrawler, 32)],
        RoomType::Monster,
        "ABLATION_SILENT",
    );

    let run_accel = |accelerations: AccelerationFlags| {
        let policy = SearchPolicy {
            beam_width: 32,
            max_nodes: 5000,
            time_budget_ms: 0,
            allow_potions: false,
            allow_random_cards: false,
            max_degree_of_parallelism: 4,
            accelerations,
            ..Default::default()
        };
        SearchEngine.solve_state(&root, &policy, &Sts2Content).unwrap()
    };

    let baseline = run_accel(AccelerationFlags::none());
    let with_por = run_accel(AccelerationFlags {
        enable_por: true,
        ..AccelerationFlags::none()
    });
    let with_dedup = run_accel(AccelerationFlags {
        enable_hand_potion_dedup: true,
        ..AccelerationFlags::none()
    });
    let with_multiset = run_accel(AccelerationFlags {
        enable_multiset_choice_dedup: true,
        ..AccelerationFlags::none()
    });
    let with_forced_end = run_accel(AccelerationFlags {
        enable_forced_end_turn_compression: true,
        ..AccelerationFlags::none()
    });
    let with_closure = run_accel(AccelerationFlags {
        enable_closure_pruning: true,
        ..AccelerationFlags::none()
    });
    let all_enabled = run_accel(AccelerationFlags::default());

    assert!(baseline.stats.won);
    assert!(all_enabled.stats.won);
    assert!(all_enabled.stats.closure_healing_certified);
    assert!(all_enabled.stats.closure_growth_zero_certified);

    for variant in [
        &with_por,
        &with_dedup,
        &with_multiset,
        &with_forced_end,
        &with_closure,
        &all_enabled,
    ] {
        assert!(variant.stats.won);
        assert!(
            variant.stats.strategic_hp_deficit <= baseline.stats.strategic_hp_deficit,
            "variant hp deficit {} exceeded baseline {}",
            variant.stats.strategic_hp_deficit,
            baseline.stats.strategic_hp_deficit
        );
    }

    assert!(
        all_enabled.stats.transition_count < baseline.stats.transition_count,
        "expected accelerations to reduce transitions: all={} baseline={}",
        all_enabled.stats.transition_count,
        baseline.stats.transition_count
    );
}

#[test]
fn portfolio_gate_power_valuation_and_route_quality() {
    let deck = vec![
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::Bash,
    ];
    let root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::BurningBlood],
        &[],
        &[(MonsterId::Nibbit, 30)],
        RoomType::Monster,
        "PORTFOLIO_TEST",
    );

    let policy = SearchPolicy {
        beam_width: 12,
        max_nodes: 3000,
        time_budget_ms: 10_000,
        allow_potions: false,
        allow_random_cards: false,
        max_degree_of_parallelism: 4,
        use_beam_width_portfolio: true,
        ..Default::default()
    };
    let resp = SearchEngine.solve_state(&root, &policy, &Sts2Content).unwrap();
    assert!(resp.stats.won);

    // Unit checks on PortfolioGate, Band rotation, PowerCardValuation, RouteQuality, PotionUsePolicy
    let bl = BeamWidthPortfolioBaseline {
        frontier_exhausted: true,
        proven_zero_damage: false,
        elapsed_ms: 10,
        expanded_nodes: 100,
        beam_width: 60,
    };
    assert_eq!(
        BeamWidthPortfolioGate::reject_refinement(&bl, 90, 500, 1000, 5000),
        None
    );
    assert_eq!(
        BeamWidthPortfolioGate::reject_refinement(&bl, 90, 50, 1000, 5000),
        Some(BeamWidthPortfolioGate::SKIPPED_NODE_HEADROOM)
    );

    let mut band = vec![1, 2, 3, 4, 5, 6];
    BeamWidthPortfolio::move_leading_band_to_tail(&mut band, 2);
    assert_eq!(band, vec![3, 4, 5, 6, 1, 2]);

    assert_eq!(
        PowerCardValuationRegistry::admission(CardId::EchoForm)
            .unwrap()
            .priority,
        PowerRoutePriority::Dedicated
    );
    assert!(PotionUsePolicy::is_eligible(
        SolverPotionPolicy::Smart,
        1,
        9,
        true,
        20,
        true,
        true,
        10
    ));

    let q_win = RouteQuality {
        won: true,
        strategic_hp_deficit: 4,
        ..Default::default()
    };
    let q_lose = RouteQuality {
        won: false,
        strategic_hp_deficit: 0,
        ..Default::default()
    };
    assert!(
        RouteQualityPolicy::compare(&q_win, &q_lose, RouteQualityProjection::PotionPolicy).is_lt()
    );
}

#[test]
fn bfws_novelty_early_turn_and_boss_tempo_search() {
    let deck = vec![
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::Bash,
    ];
    let root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::BurningBlood],
        &[],
        &[(MonsterId::Nibbit, 25)],
        RoomType::Boss,
        "MODES_TEST",
    );

    let novelty_policy = SearchPolicy {
        beam_width: 16,
        max_nodes: 1500,
        time_budget_ms: 5_000,
        use_novelty_search: true,
        ..Default::default()
    };
    let n_resp = SearchEngine
        .solve_state(&root, &novelty_policy, &Sts2Content)
        .unwrap();
    assert!(n_resp.stats.won);
    assert!(replay_actions_to_end(&root, &n_resp.actions).won);

    let early_policy = SearchPolicy {
        beam_width: 16,
        max_nodes: 1500,
        time_budget_ms: 5_000,
        early_turn_exploration_depth: 1,
        acceptable_battle_hp_loss: -100, // force early-turn exploration trigger
        ..Default::default()
    };
    let e_resp = SearchEngine
        .solve_state(&root, &early_policy, &Sts2Content)
        .unwrap();
    assert!(e_resp.stats.won);

    let boss_policy = SearchPolicy {
        beam_width: 16,
        max_nodes: 1500,
        time_budget_ms: 5_000,
        use_boss_tempo_search: true,
        ..Default::default()
    };
    let b_resp = SearchEngine
        .solve_state(&root, &boss_policy, &Sts2Content)
        .unwrap();
    assert!(b_resp.stats.won);
}

#[test]
fn cooperative_cancellation_and_streaming_progress() {
    let deck = vec![
        CardId::StrikeSilent,
        CardId::StrikeSilent,
        CardId::StrikeSilent,
        CardId::DefendSilent,
        CardId::DefendSilent,
        CardId::DefendSilent,
        CardId::Neutralize,
        CardId::Survivor,
    ];
    let root = build_combat(
        CharacterId::Silent,
        &deck,
        &[],
        &[],
        &[(MonsterId::Nibbit, 120)],
        RoomType::Monster,
        "CANCEL_TEST",
    );

    let cancel = AtomicBool::new(false);
    let updates = AtomicUsize::new(0);
    let policy = SearchPolicy {
        beam_width: 32,
        max_nodes: 50_000,
        time_budget_ms: 10_000,
        max_degree_of_parallelism: 4,
        ..Default::default()
    };

    let resp = SearchEngine
        .solve_state_with_controls(
            &root,
            &policy,
            &Sts2Content,
            &cancel,
            Some(&|_progress| {
                updates.fetch_add(1, Ordering::Relaxed);
                cancel.store(true, Ordering::Relaxed);
            }),
        )
        .unwrap();

    assert!(updates.load(Ordering::Relaxed) >= 1);
    assert_eq!(resp.stats.termination, "Cancelled");
}

#[test]
fn json_solve_request_end_to_end() {
    let deck = vec![
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::Bash,
    ];
    let root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::BurningBlood],
        &[],
        &[(MonsterId::Nibbit, 20)],
        RoomType::Monster,
        "JSON_TEST",
    );
    let live_json = export_live_state(&root).unwrap();
    let req = InCombatSolveRequest {
        policy: SearchPolicy {
            beam_width: 16,
            max_nodes: 1500,
            time_budget_ms: 5_000,
            max_degree_of_parallelism: 4,
            ..Default::default()
        },
        root_state_json: Some(live_json),
        trace_tag: Some("e2e".to_owned()),
    };
    let resp = SearchEngine.solve(&req, &Sts2Content).unwrap();
    assert!(resp.stats.won);
    assert!(replay_actions_to_end(&root, &resp.actions).won);
}

#[test]
fn potion_directives_force_and_disabled() {
    let deck = vec![
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
    ];
    let root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::BurningBlood],
        &[PotionId::FirePotion, PotionId::BlockPotion],
        &[(MonsterId::Nibbit, 12)],
        RoomType::Boss,
        "POTION_DIR_TEST",
    );

    // Force slot 0 FirePotion even when default policy is Disabled (including BossTempo).
    let forced_policy = SearchPolicy {
        beam_width: 16,
        max_nodes: 1500,
        time_budget_ms: 5_000,
        potion_policy: SolverPotionPolicy::Disabled,
        potion_directives: vec![PotionSlotDirective {
            slot: 0,
            potion_id: PotionId::FirePotion,
            directive: SolverPotionDirective::Force,
        }],
        use_boss_tempo_search: true,
        ..Default::default()
    };
    let forced_resp = SearchEngine
        .solve_state(&root, &forced_policy, &Sts2Content)
        .unwrap();
    assert!(forced_resp.stats.won);
    assert!(forced_resp.actions.iter().any(|a| matches!(
        a,
        PlannedAction::UsePotion {
            potion_slot: 0,
            potion_id: PotionId::FirePotion,
            ..
        }
    )));

    // Disabled slot 0 FirePotion cannot be used even under RequireAtLeastOne when it's the only slot.
    let single_pot_root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::BurningBlood],
        &[PotionId::FirePotion],
        &[(MonsterId::Nibbit, 12)],
        RoomType::Monster,
        "POTION_DISABLED_TEST",
    );
    let disabled_policy = SearchPolicy {
        beam_width: 16,
        max_nodes: 1500,
        time_budget_ms: 5_000,
        potion_policy: SolverPotionPolicy::RequireAtLeastOne,
        potion_directives: vec![PotionSlotDirective {
            slot: 0,
            potion_id: PotionId::FirePotion,
            directive: SolverPotionDirective::Disabled,
        }],
        ..Default::default()
    };
    let disabled_resp = SearchEngine
        .solve_state(&single_pot_root, &disabled_policy, &Sts2Content)
        .unwrap();
    assert!(disabled_resp.stats.won);
    assert!(!disabled_resp
        .actions
        .iter()
        .any(|a| matches!(a, PlannedAction::UsePotion { .. })));
}

#[test]
fn predict_potion_reward_credit_and_boss_hp_strategies() {
    let deck = vec![
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
    ];
    let mut root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[],
        &[PotionId::FirePotion, PotionId::BlockPotion, PotionId::BlockPotion],
        &[(MonsterId::FuzzyWurmCrawler, 24)],
        RoomType::Monster,
        "POTION_REWARD_TEST",
    );
    // FuzzyWurmCrawler FIRST_ACID_GOOP deals 4 + 1 Strength = 5 dmg on Turn 1.
    // 3 Strikes = 18 dmg cannot kill 24 HP Turn 1, so potion-free takes 5 dmg (< 9 Smart threshold).
    root.creatures[1].powers.push(sts2_kernel::PowerInst {
        handle: 99,
        id: sts2_types::PowerId::StrengthPower,
        amount: 1,
        amount_on_turn_start: 1,
        skip_next_duration_tick: false,
        applier_handle: None,
        ext_index: None,
    });
    let content = Sts2Content;
    let base_no_reward = SearchPolicy {
        beam_width: 16,
        max_nodes: 2000,
        time_budget_ms: 5_000,
        potion_policy: SolverPotionPolicy::Smart,
        predict_potion_reward: false,
        ..Default::default()
    };
    let base_with_reward = SearchPolicy {
        predict_potion_reward: true,
        potion_reward_replacement_hp_credit: Some(9),
        ..base_no_reward.clone()
    };
    let r_no = SearchEngine
        .solve_state(&root, &base_no_reward, &content)
        .unwrap();
    let r_yes = SearchEngine
        .solve_state(&root, &base_with_reward, &content)
        .unwrap();
    assert!(r_no.stats.won && r_yes.stats.won);
    // With WhiteBeastStatue + full belt (not RunEnding, not Sozu), credit 9 applies deterministically even when predict_potion_reward is false.
    let root_wb = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::WhiteBeastStatue],
        &[PotionId::FirePotion, PotionId::BlockPotion, PotionId::BlockPotion],
        &[(MonsterId::FuzzyWurmCrawler, 24)],
        RoomType::Monster,
        "POTION_REWARD_WB_TEST",
    );
    let baseline_wb = RootEvalBaseline::from_state_with_policy(&root_wb, &content, &base_no_reward);
    assert_eq!(baseline_wb.potion_replacement_hp_credit, 9);
    // With replacement credit = 9, optional potion net cost drops to floor (50 centi-HP), justifying Block Potion to save 5 HP.
    assert!(r_no.stats.hp_lost > 0 && r_no.stats.hp_lost < 9);
    assert_eq!(r_no.stats.potion_count, 0);
    assert_eq!(r_yes.stats.potion_count, 1);
    assert_eq!(r_yes.stats.hp_lost, 0);

    // Verify BossHpRelief mapping for Act 1 Boss vs Act 3 Boss.
    Arc::make_mut(&mut root.static_ctx).room_type = RoomType::Boss;
    Arc::make_mut(&mut root.static_ctx).act_index = 0;
    let b_act1_prog = RootEvalBaseline::from_state_with_policy(
        &root,
        &content,
        &SearchPolicy {
            act_transition_boss_hp_strategy: BossHpStrategy::ProgressionFirst,
            ..Default::default()
        },
    );
    let b_act1_min = RootEvalBaseline::from_state_with_policy(
        &root,
        &content,
        &SearchPolicy {
            act_transition_boss_hp_strategy: BossHpStrategy::MinimizeHpLoss,
            ..Default::default()
        },
    );
    assert_eq!(b_act1_prog.boss_hp_relief, BossHpRelief::ActClearHeal);
    assert_eq!(b_act1_min.boss_hp_relief, BossHpRelief::None);

    Arc::make_mut(&mut root.static_ctx).act_index = 2;
    let b_act3_prog = RootEvalBaseline::from_state_with_policy(
        &root,
        &content,
        &SearchPolicy {
            final_boss_hp_strategy: BossHpStrategy::ProgressionFirst,
            ..Default::default()
        },
    );
    let b_act3_min = RootEvalBaseline::from_state_with_policy(
        &root,
        &content,
        &SearchPolicy {
            final_boss_hp_strategy: BossHpStrategy::MinimizeHpLoss,
            ..Default::default()
        },
    );
    assert_eq!(b_act3_prog.boss_hp_relief, BossHpRelief::RunEnding);
    assert_eq!(b_act3_min.boss_hp_relief, BossHpRelief::None);
}

#[test]
fn relic_strategy_and_counter_rules_steer_lethal() {
    // 1 Bludgeon (32 dmg, 1 attack) + 2 StrikeIronclad (6 dmg each, 2 attacks) vs 12 HP Nibbit.
    // Nunchaku starts at 8 attacks played; killing with 2 Strikes ends at 10 % 10 == 0!
    let deck = vec![
        CardId::Bludgeon,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
    ];
    let mut root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::Nunchaku],
        &[],
        &[(MonsterId::Nibbit, 12)],
        RoomType::Monster,
        "RELIC_COUNTER_TEST",
    );
    root.player.relics[0]
        .private_fields
        .insert("Nunchaku._attacksPlayed".to_owned(), sts2_kernel::FieldValue::Int(8));

    let rule = RelicCounterRule {
        id: RelicCounterId::Nunchaku,
        enabled: true,
        minimum: 0,
        maximum: 0,
        hp_allowance: 25,
        priority: 1,
    };
    let enabled_policy = SearchPolicy {
        beam_width: 16,
        max_nodes: 1000,
        time_budget_ms: 5_000,
        relic_strategy_enabled: true,
        relic_counter_rules: vec![rule],
        ..Default::default()
    };
    let disabled_policy = SearchPolicy {
        relic_strategy_enabled: false,
        ..enabled_policy.clone()
    };
    let r_on = SearchEngine
        .solve_state(&root, &enabled_policy, &Sts2Content)
        .unwrap();
    let r_off = SearchEngine
        .solve_state(&root, &disabled_policy, &Sts2Content)
        .unwrap();
    assert!(r_on.stats.won && r_off.stats.won);
    let strike_count_on = r_on
        .actions
        .iter()
        .filter(|a| matches!(a, PlannedAction::PlayCard { card_handle, .. } if root.card(sts2_kernel::CardHandle(*card_handle as u32)).is_some_and(|c| c.id == CardId::StrikeIronclad)))
        .count();
    let strike_count_off = r_off
        .actions
        .iter()
        .filter(|a| matches!(a, PlannedAction::PlayCard { card_handle, .. } if root.card(sts2_kernel::CardHandle(*card_handle as u32)).is_some_and(|c| c.id == CardId::StrikeIronclad)))
        .count();
    assert_eq!(strike_count_on, 2);
    assert_eq!(strike_count_off, 0);
}

#[test]
fn growth_budgets_ignore_long_term_rewards_and_brightest_flame_limit() {
    // 1. Feed vs StrikeIronclad on a 10 HP enemy: Feed kills and gains max HP (feed growth +1),
    //    costs 1 energy vs Strike? Both cost 1 energy; use HandOfGreed (2 energy, 20 dmg) + 2 Strikes (1 energy each, 6 dmg) vs 12 HP Nibbit.
    //    HandOfGreed kills in 1 action (2 energy), vs 2 Strikes (2 energy). With BrightestFlame:
    let bf_deck = vec![
        CardId::BrightestFlame,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
    ];
    let mut bf_root = build_combat(
        CharacterId::Ironclad,
        &bf_deck,
        &[],
        &[],
        &[(MonsterId::FuzzyWurmCrawler, 24)],
        RoomType::Monster,
        "BRIGHTEST_FLAME_TEST",
    );
    // FuzzyWurmCrawler with +10 Strength deals 14 dmg Turn 1 if not killed; 4 Strikes = 24 dmg requires BrightestFlame (+2 energy, -2 max HP).
    bf_root.creatures[1].powers.push(sts2_kernel::PowerInst {
        handle: 99,
        id: sts2_types::PowerId::StrengthPower,
        amount: 10,
        amount_on_turn_start: 10,
        skip_next_duration_tick: false,
        applier_handle: None,
        ext_index: None,
    });
    let bf_forbid = SearchPolicy {
        beam_width: 16,
        max_nodes: 1000,
        time_budget_ms: 5_000,
        brightest_flame_max_hp_loss_limit: Some(0),
        ..Default::default()
    };
    let bf_allow = SearchPolicy {
        brightest_flame_max_hp_loss_limit: Some(2),
        ..bf_forbid.clone()
    };
    let r_forbid = SearchEngine
        .solve_state(&bf_root, &bf_forbid, &Sts2Content)
        .unwrap();
    let r_allow = SearchEngine
        .solve_state(&bf_root, &bf_allow, &Sts2Content)
        .unwrap();
    assert!(!r_forbid.actions.iter().any(|a| matches!(
        a,
        PlannedAction::PlayCard { card_handle, .. }
            if bf_root.card(sts2_kernel::CardHandle(*card_handle as u32)).is_some_and(|c| c.id == CardId::BrightestFlame)
    )));
    assert!(r_allow.actions.iter().any(|a| matches!(
        a,
        PlannedAction::PlayCard { card_handle, .. }
            if bf_root.card(sts2_kernel::CardHandle(*card_handle as u32)).is_some_and(|c| c.id == CardId::BrightestFlame)
    )));
    // Spent-at-root overage: initial_brightest_flame_max_hp_spent = 4 >= limit 2 -> no more BrightestFlame plays allowed.
    let bf_overage = SearchPolicy {
        brightest_flame_max_hp_loss_limit: Some(2),
        initial_brightest_flame_max_hp_spent: 4,
        ..bf_forbid
    };
    let r_overage = SearchEngine
        .solve_state(&bf_root, &bf_overage, &Sts2Content)
        .unwrap();
    assert!(!r_overage.actions.iter().any(|a| matches!(
        a,
        PlannedAction::PlayCard { card_handle, .. }
            if bf_root.card(sts2_kernel::CardHandle(*card_handle as u32)).is_some_and(|c| c.id == CardId::BrightestFlame)
    )));

    // 2. GrowthBudgets & IgnoreLongTermRewards:
    //    Hand has Alchemize (1 energy) + 3 StrikeIronclad (1 energy each, 6 dmg)
    //    vs FuzzyWurmCrawler (18 HP, attacking for 4 on Turn 1). Player has 3 energy, allow_potions: false.
    //    Playing Strike + Strike + Strike -> Turn 1 lethal, 0 dmg, 0 Alchemize.
    //    Playing Alchemize + Strike + Strike -> 4 dmg taken Turn 1, Turn 2 kill, +1 Alchemize (+40 budget).
    let g_deck = vec![
        CardId::Alchemize,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
    ];
    let g_root = build_combat(
        CharacterId::Ironclad,
        &g_deck,
        &[],
        &[],
        &[(MonsterId::FuzzyWurmCrawler, 18)],
        RoomType::Monster,
        "GROWTH_TEST",
    );
    let mut budgets = sts2_kernel::GrowthCounters::default();
    budgets.alchemize = 40;
    let p_growth = SearchPolicy {
        beam_width: 24,
        max_nodes: 2000,
        time_budget_ms: 5_000,
        allow_potions: false,
        growth_budgets: budgets,
        ignore_long_term_rewards: false,
        acceptable_battle_hp_loss: 0,
        stop_at_acceptable_battle_hp_loss: true,
        ..Default::default()
    };
    let p_ignore = SearchPolicy {
        ignore_long_term_rewards: true,
        ..p_growth.clone()
    };
    let r_growth = SearchEngine
        .solve_state(&g_root, &p_growth, &Sts2Content)
        .unwrap();
    let r_ignore = SearchEngine
        .solve_state(&g_root, &p_ignore, &Sts2Content)
        .unwrap();
    assert!(r_growth.stats.won && r_ignore.stats.won);
    let used_alchemize = |acts: &[PlannedAction]| {
        acts.iter().any(|a| {
            matches!(
                a,
                PlannedAction::PlayCard { card_handle, .. }
                    if g_root.card(sts2_kernel::CardHandle(*card_handle as u32)).is_some_and(|c| c.id == CardId::Alchemize)
            )
        })
    };
    assert!(used_alchemize(&r_growth.actions));
    assert!(!used_alchemize(&r_ignore.actions));
    // Verify IgnoreLongTermRewards re-enables early HP target stop (`can_stop_at_hp_target`).
    let baseline_g = RootEvalBaseline::from_state_with_policy(&g_root, &Sts2Content, &p_growth);
    let baseline_i = RootEvalBaseline::from_state_with_policy(&g_root, &Sts2Content, &p_ignore);
    assert!(baseline_g.effective_has_growth_targets);
    assert!(!baseline_i.effective_has_growth_targets);
}

#[test]
fn potion_hp_threshold_overrides_steer_smart_potion_usage() {
    let deck = vec![
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
    ];
    let mut root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[],
        &[PotionId::FirePotion],
        &[(MonsterId::FuzzyWurmCrawler, 24)],
        RoomType::Monster,
        "THRESHOLD_OVERRIDE_TEST",
    );
    // FuzzyWurmCrawler deals 4 + 1 = 5 dmg Turn 1 if not killed.
    root.creatures[1].powers.push(sts2_kernel::PowerInst {
        handle: 99,
        id: sts2_types::PowerId::StrengthPower,
        amount: 1,
        amount_on_turn_start: 1,
        skip_next_duration_tick: false,
        applier_handle: None,
        ext_index: None,
    });

    // Lowering FirePotion threshold from 9 to 3 allows spending it to save 5 HP.
    let mut low_map = std::collections::BTreeMap::new();
    low_map.insert(PotionId::FirePotion, 3);
    let low_policy = SearchPolicy {
        beam_width: 16,
        max_nodes: 1500,
        time_budget_ms: 5_000,
        potion_policy: SolverPotionPolicy::Smart,
        potion_hp_threshold_overrides: low_map,
        ..Default::default()
    };
    // Raising FirePotion threshold to 50 forbids spending it to save 5 HP.
    let mut high_map = std::collections::BTreeMap::new();
    high_map.insert(PotionId::FirePotion, 50);
    let high_policy = SearchPolicy {
        potion_hp_threshold_overrides: high_map,
        ..low_policy.clone()
    };

    let r_low = SearchEngine
        .solve_state(&root, &low_policy, &Sts2Content)
        .unwrap();
    let r_high = SearchEngine
        .solve_state(&root, &high_policy, &Sts2Content)
        .unwrap();
    assert!(r_low.stats.won && r_high.stats.won);
    assert_eq!(r_low.stats.potion_count, 1);
    assert_eq!(r_low.stats.hp_lost, 0);
    assert_eq!(r_high.stats.potion_count, 0);
    assert_eq!(r_high.stats.hp_lost, 5);
}

#[test]
fn stop_at_acceptable_hp_loss_and_split_beam_quotas_and_choice_caps() {
    // 1. StopAtAcceptableBattleHpLoss: true terminates with "HpTarget" on immediate win, false finishes exhaustively ("None").
    let deck = vec![
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
    ];
    let root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::BurningBlood],
        &[],
        &[(MonsterId::Nibbit, 12)],
        RoomType::Monster,
        "STOP_TARGET_TEST",
    );
    let stop_true = SearchPolicy {
        beam_width: 8,
        max_nodes: 500,
        time_budget_ms: 5_000,
        acceptable_battle_hp_loss: 0,
        stop_at_acceptable_battle_hp_loss: true,
        use_beam_width_portfolio: false,
        ..Default::default()
    };
    let stop_false = SearchPolicy {
        stop_at_acceptable_battle_hp_loss: false,
        ..stop_true.clone()
    };
    let r_stop = SearchEngine
        .solve_state(&root, &stop_true, &Sts2Content)
        .unwrap();
    let r_nostop = SearchEngine
        .solve_state(&root, &stop_false, &Sts2Content)
        .unwrap();
    assert_eq!(r_stop.stats.termination, "HpTarget");
    assert_eq!(r_nostop.stats.termination, "None");

    // 2. PotionFreeBeamWidth & PotionBeamWidth split quotas in BeamRetentionPolicy::prune_wave.
    let mut arena = NodeArena::default();
    let baseline = RootEvalBaseline::default();
    let dummy_node = |explicit_potions: u8, score: f64, hash_seed: u64| SearchNode {
        state_hash: StateHash(hash_seed as u128),
        beam_rank_score: score,
        turn: 1,
        action_count: 1,
        potion_count: explicit_potions,
        explicit_potion_count: explicit_potions,
        optional_paid_potion_count: explicit_potions,
        ..Default::default()
    };
    let n0 = arena.push_node(dummy_node(0, 100.0, 1));
    let n1 = arena.push_node(dummy_node(0, 90.0, 2));
    let n2 = arena.push_node(dummy_node(0, 80.0, 3));
    let p0 = arena.push_node(dummy_node(1, 95.0, 4));
    let p1 = arena.push_node(dummy_node(1, 85.0, 5));
    let kept = BeamRetentionPolicy::prune_wave(
        &mut arena,
        &[n0, n1, n2, p0, p1],
        3,
        Some(1),
        Some(2),
        &baseline,
        false,
        false,
    );
    assert_eq!(kept.len(), 3);
    assert_eq!(
        kept.iter()
            .filter(|&&id| arena.get(id).explicit_potion_count == 0)
            .count(),
        1
    );
    assert_eq!(
        kept.iter()
            .filter(|&&id| arena.get(id).explicit_potion_count > 0)
            .count(),
        2
    );

    // 3. Distinct MaxPileChoiceBranchesPerAction vs MaxHandChoiceBranchesPerAction.
    let choice_deck = vec![
        CardId::TrueGrit,
        CardId::StrikeIronclad,
        CardId::Bash,
        CardId::DefendIronclad,
    ];
    let choice_root = build_combat(
        CharacterId::Ironclad,
        &choice_deck,
        &[],
        &[],
        &[(MonsterId::Nibbit, 16)],
        RoomType::Monster,
        "HAND_CHOICE_CAP_TEST",
    );
    let cap_policy = SearchPolicy {
        beam_width: 16,
        max_nodes: 1000,
        time_budget_ms: 5_000,
        max_pile_choice_branches_per_action: 12,
        max_hand_choice_branches_per_action: 1,
        ..Default::default()
    };
    let cap_resp = SearchEngine
        .solve_state(&choice_root, &cap_policy, &Sts2Content)
        .unwrap();
    assert!(cap_resp.stats.won);
}

#[test]
fn portfolio_refinement_and_adaptive_novelty_options() {
    let deck = vec![
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::Bash,
    ];
    let root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::BurningBlood],
        &[],
        &[(MonsterId::Nibbit, 24)],
        RoomType::Monster,
        "PORTFOLIO_MODES_TEST",
    );
    let adaptive_policy = SearchPolicy {
        beam_width: 16,
        max_nodes: 2000,
        time_budget_ms: 5_000,
        max_degree_of_parallelism: 2,
        use_beam_width_portfolio: true,
        beam_width_portfolio_plain_baseline_member: false,
        reallocated_refinement_portfolio: false,
        offensive_refinement_portfolio: true,
        stop_portfolio_at_hp_target: false,
        portfolio_scheduling_mode: PortfolioSchedulingMode::Parallel,
        adaptive_novelty_refinement: true,
        novelty_budget: NoveltyPortfolioBudget {
            time_divisor: 2,
            maximum_milliseconds: 2_000,
            maximum_expanded_nodes: 1_000,
        },
        early_turn_exploration_depth: 1,
        early_turn_exploration_budget_ms: 3_000,
        ..Default::default()
    };
    let resp = SearchEngine
        .solve_state(&root, &adaptive_policy, &Sts2Content)
        .unwrap();
    assert!(resp.stats.won);
    assert!(replay_actions_to_end(&root, &resp.actions).won);
}

#[test]
fn any_player_potions_execute_and_replay_cleanly() {
    use sts2_search::eval::SolverPotionDirective;

    let deck = vec![
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
    ];
    let root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::BurningBlood],
        &[
            PotionId::BlockPotion,
            PotionId::ShipInABottle,
            PotionId::AttackPotion,
        ],
        &[(MonsterId::Nibbit, 30)],
        RoomType::Monster,
        "ANY_PLAYER_POTION_TEST",
    );
    let policy = SearchPolicy {
        beam_width: 32,
        max_nodes: 3000,
        time_budget_ms: 5_000,
        allow_potions: true,
        allow_random_cards: true,
        potion_policy: SolverPotionPolicy::Smart,
        potion_directives: vec![
            PotionSlotDirective {
                slot: 0,
                potion_id: PotionId::BlockPotion,
                directive: SolverPotionDirective::Force,
            },
            PotionSlotDirective {
                slot: 1,
                potion_id: PotionId::ShipInABottle,
                directive: SolverPotionDirective::Force,
            },
            PotionSlotDirective {
                slot: 2,
                potion_id: PotionId::AttackPotion,
                directive: SolverPotionDirective::Force,
            },
        ],
        use_beam_width_portfolio: false,
        ..Default::default()
    };

    let resp = SearchEngine
        .solve_state(&root, &policy, &Sts2Content)
        .unwrap();
    assert!(resp.stats.won);
    assert_eq!(resp.stats.potion_count, 3);
    let expected_player_handle = root.player.creature_handle.0 as i32;
    let mut used_ids = Vec::new();
    for act in &resp.actions {
        if let PlannedAction::UsePotion {
            potion_id,
            target_handle,
            ..
        } = act
        {
            assert_eq!(*target_handle, Some(expected_player_handle));
            used_ids.push(*potion_id);
        }
    }
    assert!(used_ids.contains(&PotionId::BlockPotion));
    assert!(used_ids.contains(&PotionId::ShipInABottle));
    assert!(used_ids.contains(&PotionId::AttackPotion));
    assert!(replay_actions_to_end(&root, &resp.actions).won);
}

#[test]
fn unwinnable_combat_exhausting_cleanly_reports_death_only_without_node_limit() {
    let deck = vec![
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
    ];
    let mut root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[],
        &[],
        &[(MonsterId::Nibbit, 200)],
        RoomType::Monster,
        "EXHAUSTIVE_DEATH_TEST",
    );
    root.player.current_hp = 5;
    root.creatures[0].current_hp = 5;

    let policy = SearchPolicy {
        beam_width: 32,
        max_nodes: 10_000,
        time_budget_ms: 5_000,
        allow_potions: false,
        use_beam_width_portfolio: false,
        ..Default::default()
    };
    let resp = SearchEngine
        .solve_state(&root, &policy, &Sts2Content)
        .unwrap();
    assert!(!resp.stats.won);
    assert_eq!(resp.stats.termination, "None");
    let end = replay_actions_to_end(&root, &resp.actions);
    assert!(!end.won);
    assert!(end.player.current_hp <= 0 || !end.has_living_player_creature());
}

#[test]
fn beam_retention_niche_selectors_keep_best_not_worst() {
    let mut arena = NodeArena::default();
    let baseline = RootEvalBaseline::default();
    let mut nodes = Vec::new();

    for i in 0..24u64 {
        let is_best_rollout = i == 23;
        let is_best_survival = i == 22;
        let is_best_tempo = i == 21;
        let is_best_setup = i == 20;
        let is_best_control = i == 19;

        let full = StoredNodeEval {
            beam_rank_score: (100 - i as i32) as f64,
            rollout_score: Some(if is_best_rollout {
                100_000.0
            } else {
                (i as f64) * 10.0
            }),
            board_investment: if is_best_setup { 500 } else { 1 },
            persistent_buff_value: if is_best_setup { 500 } else { 1 },
            setup_score: if is_best_setup {
                500
            } else if is_best_control {
                120
            } else {
                1
            },
            ..Default::default()
        };
        let mut node = SearchNode {
            state_hash: StateHash((i + 1) as u128),
            beam_rank_score: (100 - i as i32) as f64,
            turn: 1,
            action_count: 1,
            fast_eval: StoredFastEval {
                projected_player_hp: if is_best_survival { 80 } else { 40 },
                alive_enemy_count: if is_best_tempo { 0 } else { 2 },
                enemy_hp: if is_best_tempo { 0 } else { 100 },
                ..Default::default()
            },
            ..Default::default()
        };
        node.set_full_eval(&full);
        let nid = arena.push_node(node);
        nodes.push(nid);
    }

    let kept = BeamRetentionPolicy::prune_wave_with_rollout(
        &mut arena,
        &nodes,
        10,
        None,
        None,
        &baseline,
        false,
        false,
        false,
        true,
    );
    assert_eq!(kept.len(), 10);
    assert!(kept.contains(&nodes[23]), "pure rollout leader must be retained");
    assert!(kept.contains(&nodes[22]), "survival leader must be retained");
    assert!(kept.contains(&nodes[21]), "tempo leader must be retained");
    assert!(kept.contains(&nodes[20]), "setup leader must be retained");
    assert!(kept.contains(&nodes[19]), "control leader must be retained");
}

#[test]
fn potion_bucket_diversity_distinguishes_equal_cost_slots() {
    let mut arena = NodeArena::default();
    let baseline = RootEvalBaseline::default();
    let mut nodes = Vec::new();

    // 13 high-scoring candidates from slot 0 plus 1 lower-scoring candidate each from slot 1 and slot 2 (all cost 9).
    for i in 0..15u64 {
        let slot = match i {
            13 => 1u16,
            14 => 2u16,
            _ => 0u16,
        };
        let score = if slot == 0 {
            1_000.0 - i as f64
        } else {
            100.0 - i as f64
        };
        let full = FullEvalDetails {
            beam_rank_score: score,
            enemy_combat_dist_hash: i + 1,
            unordered_pile_hash: i + 100,
            ..Default::default()
        }
        .to_stored();
        let mut node = SearchNode {
            state_hash: StateHash((i + 1) as u128),
            beam_rank_score: score,
            turn: 1,
            action_count: 1,
            potion_count: 1,
            explicit_potion_count: 1,
            optional_paid_potion_count: 1,
            used_potion_slot_mask: 1u16 << slot,
            potion_strategic_cost: 9,
            optional_paid_potion_cost: 9,
            ..Default::default()
        };
        node.set_full_eval(&full);
        let nid = arena.push_node(node);
        nodes.push(nid);
    }

    let kept = BeamRetentionPolicy::prune_wave_with_rollout(
        &mut arena,
        &nodes,
        12,
        None,
        None,
        &baseline,
        false,
        false,
        true,
        false,
    );
    assert_eq!(kept.len(), 12);
    assert!(kept.contains(&nodes[13]), "slot 1 potion route must survive diversity");
    assert!(kept.contains(&nodes[14]), "slot 2 potion route must survive diversity");
}

#[test]
fn run_ending_boss_prefers_potion_victory_over_death() {
    let deck = vec![
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
    ];
    let mut root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[],
        &[PotionId::FirePotion],
        &[(MonsterId::Nibbit, 38)],
        RoomType::Boss,
        "RUN_ENDING_POTION_WIN_TEST",
    );
    Arc::make_mut(&mut root.static_ctx).act_index = 2;
    root.player.current_hp = 5;
    root.creatures[0].current_hp = 5;

    let policy = SearchPolicy {
        beam_width: 32,
        max_nodes: 5_000,
        time_budget_ms: 5_000,
        allow_potions: true,
        potion_policy: SolverPotionPolicy::Smart,
        use_beam_width_portfolio: true,
        final_boss_hp_strategy: BossHpStrategy::ProgressionFirst,
        is_non_final_act3_boss: false,
        ..Default::default()
    };
    let resp = SearchEngine
        .solve_state(&root, &policy, &Sts2Content)
        .unwrap();
    assert!(resp.stats.won);
    assert!(resp.stats.potion_count >= 1);
    assert!(replay_actions_to_end(&root, &resp.actions).won);
}

#[test]
fn pack_unpack_roundtrip_preserves_compact_cards_and_counters() {
    use sts2_kernel::state::AfflictionInst;
    use sts2_kernel::{
        CardExtension, CardTempFlags, EnchantmentInst, LocalEnergyModifier,
    };
    use sts2_search::packer::{empty_scratch_state, pack_state, unpack_state};
    use sts2_types::{AfflictionId, Dec, EnchantmentId, EnchantmentStatus, LocalEnergyModifierKind};

    let deck = vec![
        CardId::StrikeIronclad,
        CardId::Bash,
        CardId::DefendIronclad,
        CardId::Inflame,
    ];
    let mut root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::BurningBlood],
        &[PotionId::FirePotion],
        &[(MonsterId::Nibbit, 40)],
        RoomType::Monster,
        "PACK_UNPACK_ROUNDTRIP_TEST",
    );

    // Populate non-zero PlayerCounters across multiple fields.
    root.player.counters.stars_gained_this_turn = Dec::from_i32(3);
    root.player.counters.attack_cards_played_this_turn = 2;
    root.player.counters.cards_played_this_combat = 7;
    root.player.counters.cards_drawn_this_combat = 5;
    root.player.counters.melody_cards_finished_this_turn = 1;

    // Populate card extensions, upgrades, overrides, affliction, flags, and transform on cards.
    root.card_extensions.push(CardExtension {
        local_energy_modifiers: smallvec::smallvec![LocalEnergyModifier {
            kind: LocalEnergyModifierKind::AddThisTurn,
            amount: -1,
            is_reduce_only: true,
        }],
        enchantment: Some(EnchantmentInst {
            id: EnchantmentId::Sharp,
            amount: 3,
            status: EnchantmentStatus::Normal,
            private_fields: Default::default(),
        }),
        private_fields: Default::default(),
    });
    if let Some(c0) = root.cards.get_mut(0) {
        c0.upgrade_level = 1;
        c0.base_energy_cost = 0;
        c0.captured_energy_x_value = 2;
        c0.base_replay_count = 1;
        c0.floor_added_to_deck = Some(4);
        c0.temp_star_cost_override_this_turn = Some(1);
        c0.temp_cost_override_this_turn = Some(0);
        c0.temp_cost_override_this_turn_or_until_played = Some(2);
        c0.temp_cost_override_this_combat = Some(1);
        c0.keywords_added_from_outside = sts2_types::CardKeywords::RETAIN;
        c0.keywords_removed_from_outside = sts2_types::CardKeywords::EXHAUST;
        c0.temp_flags = CardTempFlags::EXHAUST_ON_NEXT_PLAY | CardTempFlags::TEMPORARY_RETAIN_TURN;
        c0.affliction = Some(AfflictionInst {
            id: AfflictionId::Hexed,
            amount: 2,
        });
        c0.ext_index = Some(0);
        c0.transform_into_after_play = Some(CardId::Wound);
    }
    if let Some(c1) = root.cards.get_mut(1) {
        c1.removed_from_state = true;
    }

    let mut packed = Vec::new();
    pack_state(&root, &mut packed);

    let mut restored = empty_scratch_state(root.static_ctx.clone());
    // Seed an existing card_extension entry to verify in-place reuse does not leak stale fields.
    restored.card_extensions.push(CardExtension {
        local_energy_modifiers: smallvec::smallvec![LocalEnergyModifier {
            kind: LocalEnergyModifierKind::FreeThisCombat,
            amount: 0,
            is_reduce_only: false,
        }],
        enchantment: None,
        private_fields: [(
            "stale".to_owned(),
            sts2_kernel::FieldValue::Int(99),
        )]
        .into_iter()
        .collect(),
    });
    restored.card_extensions.push(CardExtension::default());
    unpack_state(&packed, &root.static_ctx, &mut restored).unwrap();

    assert_eq!(restored, root);
}

#[test]
fn non_boss_setup_scoring_does_not_cliff_at_four_and_credits_regen() {
    use sts2_search::eval::{compute_beam_rank_score_with_mode, evaluate_fast, evaluate_full, EvalMode};

    let deck = vec![CardId::StrikeIronclad, CardId::DefendIronclad];
    let mut root = build_combat(
        CharacterId::Silent,
        &deck,
        &[],
        &[PotionId::RegenPotion],
        &[(MonsterId::Nibbit, 40)],
        RoomType::Monster,
        "NON_BOSS_SETUP_SCORING_TEST",
    );
    root.player.current_hp = 56;
    root.player.max_hp = 70;
    root.creatures[0].current_hp = 56;
    root.creatures[0].max_hp = 70;
    let baseline = RootEvalBaseline::from_state_with_policy(&root, &Sts2Content, &SearchPolicy::default());

    let mut state_small_buff = root.clone();
    state_small_buff.creatures[0].powers.push(sts2_kernel::PowerInst {
        handle: 100,
        id: sts2_types::PowerId::PlatingPower,
        amount: 2,
        amount_on_turn_start: 2,
        skip_next_duration_tick: false,
        applier_handle: None,
        ext_index: None,
    });
    let mut state_large_buff = state_small_buff.clone();
    state_large_buff.creatures[0].powers.push(sts2_kernel::PowerInst {
        handle: 101,
        id: sts2_types::PowerId::BarricadePower,
        amount: 1,
        amount_on_turn_start: 1,
        skip_next_duration_tick: false,
        applier_handle: None,
        ext_index: None,
    });
    state_large_buff.creatures[0].powers.push(sts2_kernel::PowerInst {
        handle: 102,
        id: sts2_types::PowerId::DexterityPower,
        amount: 3,
        amount_on_turn_start: 3,
        skip_next_duration_tick: false,
        applier_handle: None,
        ext_index: None,
    });

    let f_small = evaluate_fast(&state_small_buff, &Sts2Content, &baseline, None);
    let d_small = evaluate_full(&state_small_buff, &Sts2Content, &f_small, &baseline);
    let f_large = evaluate_fast(&state_large_buff, &Sts2Content, &baseline, None);
    let d_large = evaluate_full(&state_large_buff, &Sts2Content, &f_large, &baseline);
    assert!(d_small.persistent_buff_value >= 4);
    assert!(d_large.persistent_buff_value > d_small.persistent_buff_value + 10);
    assert!(
        d_large.beam_rank_score > d_small.beam_rank_score,
        "stacked non-boss powers above 4 must increase beam score"
    );

    let mut state_regen = root.clone();
    state_regen.creatures[0].powers.push(sts2_kernel::PowerInst {
        handle: 103,
        id: sts2_types::PowerId::RegenPower,
        amount: 5,
        amount_on_turn_start: 5,
        skip_next_duration_tick: false,
        applier_handle: None,
        ext_index: None,
    });
    let f_base = evaluate_fast(&root, &Sts2Content, &baseline, None);
    let d_base = evaluate_full(&root, &Sts2Content, &f_base, &baseline);
    let s_base = compute_beam_rank_score_with_mode(
        &f_base,
        &d_base,
        &baseline,
        false,
        false,
        None,
        EvalMode::Balanced,
        0,
        0,
    );
    let f_regen = evaluate_fast(&state_regen, &Sts2Content, &baseline, None);
    let d_regen = evaluate_full(&state_regen, &Sts2Content, &f_regen, &baseline);
    let s_regen = compute_beam_rank_score_with_mode(
        &f_regen,
        &d_regen,
        &baseline,
        false,
        false,
        None,
        EvalMode::Balanced,
        9,
        0,
    );
    assert!(d_regen.expected_regen_heal >= 14);
    assert!(
        s_regen > s_base,
        "RegenPotion on wounded player must outweigh its Turn-1 opportunity cost: {s_regen} vs {s_base}"
    );
}

#[test]
fn select_final_plan_credits_fruit_juice_max_hp_gain_at_two_to_one() {
    use sts2_search::arena::{NODE_FLAG_IS_TERMINAL, SearchNode};
    use sts2_search::eval::{FAST_FLAG_ALL_ENEMIES_DEAD, FAST_FLAG_WON};
    use sts2_search::eval::{select_final_plan, SolverPotionPolicy};

    let deck = vec![CardId::StrikeIronclad, CardId::DefendIronclad];
    let mut root = build_combat(
        CharacterId::Defect,
        &deck,
        &[],
        &[PotionId::FruitJuice],
        &[(MonsterId::Nibbit, 10)],
        RoomType::Elite,
        "FRUIT_JUICE_MAX_HP_CREDIT_TEST",
    );
    root.player.current_hp = 60;
    root.player.max_hp = 75;
    root.creatures[0].current_hp = 60;
    root.creatures[0].max_hp = 75;
    let baseline = RootEvalBaseline::from_state_with_policy(&root, &Sts2Content, &SearchPolicy::default());
    let mut arena = NodeArena::default();

    // 0-potion victory taking 11 HP lost
    let n_zero = arena.push_node(SearchNode {
        state_hash: StateHash(1),
        beam_rank_score: 9_999_647_630.0,
        turn: 4,
        action_count: 8,
        node_flags: NODE_FLAG_IS_TERMINAL,
        fast_eval: StoredFastEval {
            player_hp: 49,
            player_max_hp: 75,
            projected_player_hp: 49,
            cumulative_hp_lost: 11,
            recovered_hp: 0,
            flags: FAST_FLAG_ALL_ENEMIES_DEAD | FAST_FLAG_WON,
            combat_ended_turn_encoded: 4,
            ..Default::default()
        },
        ..Default::default()
    });

    // Fruit Juice victory: same 11 HP lost, heals 5 (54 HP) and +5 MaxHP (80), cost 9 -> +15 persistent HP credit!
    let n_fruit = arena.push_node(SearchNode {
        state_hash: StateHash(2),
        beam_rank_score: 9_999_830_620.0,
        turn: 4,
        action_count: 9,
        potion_count: 1,
        explicit_potion_count: 1,
        optional_paid_potion_count: 1,
        used_potion_slot_mask: 1,
        potion_strategic_cost: 9,
        optional_paid_potion_cost: 9,
        node_flags: NODE_FLAG_IS_TERMINAL,
        fast_eval: StoredFastEval {
            player_hp: 54,
            player_max_hp: 80,
            projected_player_hp: 54,
            cumulative_hp_lost: 11,
            recovered_hp: 5,
            flags: FAST_FLAG_ALL_ENEMIES_DEAD | FAST_FLAG_WON,
            combat_ended_turn_encoded: 4,
            ..Default::default()
        },
        ..Default::default()
    });

    let selected = select_final_plan(
        &arena,
        &[n_zero, n_fruit],
        &baseline,
        SolverPotionPolicy::Smart,
        None,
        0,
        0,
    )
    .unwrap();
    assert_eq!(
        selected.node_id, n_fruit,
        "FruitJuice (+5 HP heal + +5 MaxHP * 2 = +15 HP value) must beat 0-potion at cost 9"
    );
}

#[test]
fn unified_centi_hp_value_and_potion_costs_have_no_post_hoc_veto() {
    use sts2_search::eval::{select_final_plan, PotionValuationRegistry};

    assert_eq!(
        PotionValuationRegistry::strategic_hp_cost_for_root(
            PotionId::PotionShapedRock,
            false,
            80,
            &Default::default(),
        ),
        0
    );
    assert_eq!(
        PotionValuationRegistry::strategic_hp_cost_for_root(
            PotionId::Ambergris,
            false,
            80,
            &Default::default(),
        ),
        32
    );
    assert_eq!(
        PotionValuationRegistry::strategic_hp_cost_for_root(
            PotionId::Ambergris,
            false,
            30,
            &Default::default(),
        ),
        18
    );

    let deck = vec![CardId::StrikeIronclad, CardId::DefendIronclad];
    let mut root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[],
        &[PotionId::BlockPotion, PotionId::Ambergris],
        &[(MonsterId::Nibbit, 12)],
        RoomType::Elite,
        "UNIFIED_CENTI_V_TEST",
    );
    root.player.current_hp = 60;
    root.player.max_hp = 80;
    root.creatures[0].current_hp = 60;
    root.creatures[0].max_hp = 80;
    let baseline = RootEvalBaseline::from_state_with_policy(
        &root,
        &Sts2Content,
        &SearchPolicy {
            potion_policy: SolverPotionPolicy::Smart,
            ..Default::default()
        },
    );

    use sts2_search::arena::NODE_FLAG_IS_TERMINAL;
    use sts2_search::eval::{FAST_FLAG_ALL_ENEMIES_DEAD, FAST_FLAG_WON};

    let mut arena = NodeArena::default();
    let make_win = |arena: &mut NodeArena,
                    hash: u128,
                    player_hp: i32,
                    hp_lost: i32,
                    potion_count: u8,
                    opt_paid_count: u8,
                    opt_paid_cost: i16,
                    ambergris_count: u8| {
        arena.push_node(SearchNode {
            state_hash: StateHash(hash),
            beam_rank_score: 1_000_000_000.0,
            turn: 2,
            action_count: 3,
            potion_count,
            explicit_potion_count: potion_count,
            optional_paid_potion_count: opt_paid_count,
            used_potion_slot_mask: if opt_paid_count > 0 { 1 } else { 0 },
            potion_strategic_cost: opt_paid_cost,
            optional_paid_potion_cost: opt_paid_cost,
            ambergris_count,
            node_flags: NODE_FLAG_IS_TERMINAL,
            fast_eval: StoredFastEval {
                player_hp: player_hp.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
                player_max_hp: 80,
                projected_player_hp: player_hp.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
                cumulative_hp_lost: hp_lost.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
                flags: FAST_FLAG_ALL_ENEMIES_DEAD | FAST_FLAG_WON,
                combat_ended_turn_encoded: 2,
                ..Default::default()
            },
            ..Default::default()
        })
    };

    // Case 1: Tie in V = -900 centi-HP (-9 HP vs -0 HP - 9 cost) must break toward higher ending HP.
    let n_zero = make_win(&mut arena, 1, 51, 9, 0, 0, 0, 0);
    let n_tie_block = make_win(&mut arena, 2, 60, 0, 1, 1, 9, 0);
    let picked_tie = select_final_plan(
        &arena,
        &[n_zero, n_tie_block],
        &baseline,
        SolverPotionPolicy::Smart,
        None,
        0,
        0,
    )
    .unwrap();
    assert_eq!(picked_tie.node_id, n_tie_block);

    // Case 2: Ambergris saves 33 HP at cost 32 (V = +100 centi > 0): no post-hoc Smart/Ambergris veto.
    let n_zero_heavy = make_win(&mut arena, 3, 27, 33, 0, 0, 0, 0);
    let n_amb_win = make_win(&mut arena, 4, 60, 0, 1, 1, 32, 1);
    let picked_amb = select_final_plan(
        &arena,
        &[n_zero_heavy, n_amb_win],
        &baseline,
        SolverPotionPolicy::Smart,
        None,
        0,
        0,
    )
    .unwrap();
    assert_eq!(picked_amb.node_id, n_amb_win);
}

#[test]
fn combat_generated_potion_mask_roundtrip_and_policy_semantics() {
    use sts2_kernel::commands::potion as potion_cmd;
    use sts2_search::por::try_classify_potion_use_for_pass;

    let deck = vec![CardId::StrikeIronclad, CardId::DefendIronclad];
    let mut root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[],
        &[PotionId::BlockPotion],
        &[(MonsterId::Nibbit, 20)],
        RoomType::Monster,
        "COMBAT_GEN_POTION_MASK_TEST",
    );
    let content = Sts2Content;
    assert_eq!(root.player.combat_generated_potion_mask, 0);
    assert!(potion_cmd::try_to_procure(&mut root, &content, PotionId::FirePotion).unwrap());
    assert_eq!(root.player.potion_slots[1], Some(PotionId::FirePotion));
    assert_eq!(root.player.combat_generated_potion_mask, 1u16 << 1);

    // Pack/unpack preserves combat_generated_potion_mask cleanly.
    let mut buf = Vec::new();
    pack_state(&root, &mut buf);
    let mut restored = empty_scratch_state(root.static_ctx.clone());
    unpack_state(&buf, &root.static_ctx, &mut restored).unwrap();
    assert_eq!(
        restored.player.combat_generated_potion_mask,
        root.player.combat_generated_potion_mask
    );

    // Generated slot ignores root Disabled directive on slot 1 and never satisfies Force on slot 1.
    let policy_disabled_slot = SearchPolicy {
        potion_policy: SolverPotionPolicy::Smart,
        potion_directives: vec![PotionSlotDirective {
            slot: 1,
            potion_id: PotionId::FirePotion,
            directive: SolverPotionDirective::Disabled,
        }],
        ..Default::default()
    };
    let cls_disabled = try_classify_potion_use_for_pass(
        &root,
        1,
        PotionId::FirePotion,
        &policy_disabled_slot,
        None,
        false,
        false,
    )
    .expect("combat-generated potion must bypass slot Disabled even in 0-cost phase");
    assert!(cls_disabled.is_generated);
    assert_eq!(cls_disabled.optional_paid_cost_delta, 0);
    assert_eq!(cls_disabled.optional_paid_count_delta, 0);
    assert_eq!(cls_disabled.root_potion_slot_bit, 0);

    let policy_forced_slot = SearchPolicy {
        potion_policy: SolverPotionPolicy::Smart,
        potion_directives: vec![PotionSlotDirective {
            slot: 1,
            potion_id: PotionId::FirePotion,
            directive: SolverPotionDirective::Force,
        }],
        ..Default::default()
    };
    let cls_forced = try_classify_potion_use_for_pass(
        &root,
        1,
        PotionId::FirePotion,
        &policy_forced_slot,
        None,
        false,
        false,
    )
    .unwrap();
    assert!(cls_forced.is_generated);
    assert!(!cls_forced.is_forced);
    assert_eq!(cls_forced.forced_bit, None);

    // Discard clears mask bit; re-procure sets it; use clears mask bit.
    potion_cmd::discard_potion(&mut root, &content, 1).unwrap();
    assert_eq!(root.player.combat_generated_potion_mask, 0);
    assert!(potion_cmd::try_to_procure(&mut root, &content, PotionId::BlockPotion).unwrap());
    assert_eq!(root.player.combat_generated_potion_mask, 1u16 << 1);
    let pc = root.player.creature_handle;
    let mut empty_choices = SmallVec::new();
    potion_cmd::use_potion(
        &mut root,
        &content,
        1,
        Some(pc),
        &mut empty_choices,
    )
    .unwrap();
    assert_eq!(root.player.combat_generated_potion_mask, 0);
}

#[test]
fn provable_no_op_potion_use_filters_and_exemptions() {
    use sts2_search::por::is_provable_no_op_potion_use;

    let deck = vec![CardId::StrikeIronclad, CardId::DefendIronclad];
    let mut root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[],
        &[],
        &[(MonsterId::Nibbit, 20)],
        RoomType::Monster,
        "NOOP_POTION_TEST",
    );
    // Fortifier is no-op at 0 block.
    assert!(is_provable_no_op_potion_use(
        &root,
        PotionId::Fortifier,
        false,
        false,
        false,
        false,
    ));
    root.creatures[0].block = 5;
    assert!(!is_provable_no_op_potion_use(
        &root,
        PotionId::Fortifier,
        false,
        false,
        false,
        false,
    ));

    // BloodPotion is no-op at full HP unless any_heal_hook or damaged or forced/relic exempt.
    assert!(is_provable_no_op_potion_use(
        &root,
        PotionId::BloodPotion,
        false,
        false,
        false,
        false,
    ));
    assert!(!is_provable_no_op_potion_use(
        &root,
        PotionId::BloodPotion,
        false,
        false,
        false,
        true,
    ));

    // Draw / Discard / Hand empty no-ops.
    root.player.draw.clear();
    root.player.discard.clear();
    root.player.hand.clear();
    assert!(is_provable_no_op_potion_use(
        &root,
        PotionId::DropletOfPrecognition,
        false,
        false,
        false,
        false,
    ));
    assert!(is_provable_no_op_potion_use(
        &root,
        PotionId::LiquidMemories,
        false,
        false,
        false,
        false,
    ));
    assert!(is_provable_no_op_potion_use(
        &root,
        PotionId::TouchOfInsanity,
        false,
        false,
        false,
        false,
    ));
    // EntropicBrew with Sozu.
    assert!(is_provable_no_op_potion_use(
        &root,
        PotionId::EntropicBrew,
        false,
        false,
        true,
        false,
    ));
    assert!(!is_provable_no_op_potion_use(
        &root,
        PotionId::EntropicBrew,
        false,
        false,
        false,
        false,
    ));

    // Forced directive & OnPotionUsed relics exempt all provable no-op filters.
    assert!(!is_provable_no_op_potion_use(
        &root,
        PotionId::Fortifier,
        true,
        false,
        false,
        false,
    ));
    assert!(!is_provable_no_op_potion_use(
        &root,
        PotionId::EntropicBrew,
        false,
        true,
        true,
        false,
    ));
}

#[test]
fn can_any_paid_potion_improve_on_sound_bounds() {
    let deck = vec![CardId::StrikeIronclad, CardId::DefendIronclad];
    let root_fire = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[],
        &[PotionId::FirePotion],
        &[(MonsterId::Nibbit, 15)],
        RoomType::Monster,
        "PAID_IMPROVE_FIRE",
    );
    let rb_fire = RootEvalBaseline::from_state_with_policy(
        &root_fire,
        &Sts2Content,
        &SearchPolicy {
            potion_policy: SolverPotionPolicy::Smart,
            ..Default::default()
        },
    );
    assert!(
        !PotionUsePolicy::can_any_paid_potion_improve_on(
            true,
            true,
            0,
            root_fire.player.current_hp,
            root_fire.player.max_hp,
            0,
            0,
            false,
            false,
            &rb_fire,
        ),
        "lossless win with non-heal/non-FruitJuice FirePotion has zero HP gain headroom"
    );
    assert!(
        PotionUsePolicy::can_any_paid_potion_improve_on(
            true,
            true,
            0,
            root_fire.player.current_hp - 12,
            root_fire.player.max_hp,
            0,
            0,
            false,
            false,
            &rb_fire,
        ),
        "losing 12 HP >= FirePotion cost 9 must allow paid-potion improvement"
    );

    let root_fruit = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[],
        &[PotionId::FruitJuice],
        &[(MonsterId::Nibbit, 15)],
        RoomType::Monster,
        "PAID_IMPROVE_FRUIT",
    );
    let rb_fruit = RootEvalBaseline::from_state_with_policy(
        &root_fruit,
        &Sts2Content,
        &SearchPolicy {
            potion_policy: SolverPotionPolicy::Smart,
            ..Default::default()
        },
    );
    assert!(
        PotionUsePolicy::can_any_paid_potion_improve_on(
            true,
            true,
            0,
            root_fruit.player.current_hp,
            root_fruit.player.max_hp,
            0,
            0,
            false,
            false,
            &rb_fruit,
        ),
        "remaining FruitJuice adds +5 HP + +5*2 MaxHP headroom even at 0 damage taken"
    );
}

#[test]
fn frozen_incumbent_bound_prunes_strictly_worse_and_preserves_v_tie_higher_hp() {
    use sts2_search::pruning::{ClosureCertificate, FrozenIncumbentBound};

    let deck = vec![CardId::StrikeIronclad, CardId::DefendIronclad];
    let mut root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[],
        &[PotionId::BlockPotion],
        &[(MonsterId::Nibbit, 15)],
        RoomType::Monster,
        "FROZEN_INCUMBENT_TEST",
    );
    root.player.current_hp = 70;
    root.player.max_hp = 80;
    root.creatures[0].current_hp = 70;
    root.creatures[0].max_hp = 80;
    let rb = RootEvalBaseline::from_state_with_policy(&root, &Sts2Content, &SearchPolicy::default());
    let cert = ClosureCertificate {
        healing_certified: true,
        max_in_combat_heal: 0,
        growth_certified_zero: true,
        ..Default::default()
    };

    let mut inc = FrozenIncumbentBound::default();
    // Clean 0-potion victory at 60 HP (-10 HP delta -> -1000 centi-HP), turn 3.
    assert!(inc.tighten(&rb, true, 0, -1000, 0, 0, 0, 3, 0, false, false));

    // Strictly worse V (-1100 centi-HP at turn 2) must be pruned.
    assert!(inc.is_pruned(&cert, &rb, 59, 80, 0, 0, false, false, 2, 0, 0, 0, false));

    // Tying V = -1000 centi-HP with HIGHER ending HP (-100 HP delta - 900 potion cost = -1000, -100 > -1000) must NOT be pruned.
    assert!(!inc.is_pruned(&cert, &rb, 69, 80, 0, 0, false, false, 4, 1, 1, 900, false));
}

#[test]
fn eval_features_sandpit_wuppy_sovereign_blade_and_fool() {
    use sts2_kernel::state::FieldValue;
    use sts2_kernel::{CardExtension, CardInst, PowerInst};
    use sts2_search::eval::{compute_beam_rank_score_with_centi, evaluate_fast, evaluate_full, EvalMode};
    use sts2_types::PowerId;

    let deck = vec![CardId::StrikeIronclad, CardId::DefendIronclad];
    let root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::Wuppy],
        &[],
        &[(MonsterId::Nibbit, 80)],
        RoomType::Monster,
        "EVAL_FEATURES_TEST",
    );
    // 1. Wuppy pet HP adds to effective_defense in evaluate_full.
    let rb = RootEvalBaseline::from_state_with_policy(&root, &Sts2Content, &SearchPolicy::default());
    let f0 = evaluate_fast(&root, &Sts2Content, &rb, None);
    let d0 = evaluate_full(&root, &Sts2Content, &f0, &rb);
    assert!(
        d0.wuppy_hp > 0 && d0.effective_defense >= f0.player_block + d0.wuppy_hp,
        "Wuppy HP ({}) must contribute to effective_defense ({})",
        d0.wuppy_hp,
        d0.effective_defense
    );

    // 2. Sandpit countdown penalty when min_sandpit_turns <= 1 and enemy_hp > 60.
    let mut s_sand1 = root.clone();
    s_sand1.creatures[1].powers.push(PowerInst {
        handle: 500,
        id: PowerId::SandpitPower,
        amount: 1,
        amount_on_turn_start: 1,
        skip_next_duration_tick: false,
        applier_handle: None,
        ext_index: None,
    });
    let mut s_sand4 = root.clone();
    s_sand4.creatures[1].powers.push(PowerInst {
        handle: 501,
        id: PowerId::SandpitPower,
        amount: 4,
        amount_on_turn_start: 4,
        skip_next_duration_tick: false,
        applier_handle: None,
        ext_index: None,
    });
    let f_s1 = evaluate_fast(&s_sand1, &Sts2Content, &rb, None);
    let d_s1 = evaluate_full(&s_sand1, &Sts2Content, &f_s1, &rb);
    let f_s4 = evaluate_fast(&s_sand4, &Sts2Content, &rb, None);
    let d_s4 = evaluate_full(&s_sand4, &Sts2Content, &f_s4, &rb);
    assert_eq!(d_s1.min_sandpit_turns, 1);
    assert_eq!(d_s4.min_sandpit_turns, 4);
    assert!(d_s4.beam_rank_score > d_s1.beam_rank_score);

    // 3. Retained SovereignBlade + FurnacePower increases retained_blade_bonus_base and score.
    let mut s_blade = root.clone();
    s_blade.creatures[0].powers.push(PowerInst {
        handle: 502,
        id: PowerId::FurnacePower,
        amount: 6,
        amount_on_turn_start: 6,
        skip_next_duration_tick: false,
        applier_handle: None,
        ext_index: None,
    });
    let ext_idx = s_blade.card_extensions.len() as u32;
    let mut pf = sts2_kernel::state::FieldMap::default();
    pf.insert("SovereignBlade._damage", FieldValue::Int(25));
    s_blade.card_extensions.push(CardExtension {
        local_energy_modifiers: SmallVec::new(),
        enchantment: None,
        private_fields: pf,
    });
    let blade_h = CardHandle(999);
    s_blade.cards.push(CardInst {
        handle: blade_h,
        id: CardId::SovereignBlade,
        upgrade_level: 0,
        canonical_base_energy_cost: 2,
        base_energy_cost: 2,
        captured_energy_x_value: 0,
        base_replay_count: 0,
        floor_added_to_deck: Some(1),
        deck_card_handle: None,
        clone_of_handle: None,
        temp_star_cost_override_this_turn: None,
        temp_cost_override_this_turn: None,
        temp_cost_override_this_turn_or_until_played: None,
        temp_cost_override_this_combat: None,
        local_keywords: CardKeywords::RETAIN,
        keywords_added_from_outside: CardKeywords::empty(),
        keywords_removed_from_outside: CardKeywords::empty(),
        temp_flags: CardTempFlags::empty(),
        affliction: None,
        ext_index: Some(ext_idx),
        pile: PileType::Hand,
        removed_from_state: false,
        transform_into_after_play: None,
    });
    s_blade.player.hand.push(blade_h);
    let f_bl = evaluate_fast(&s_blade, &Sts2Content, &rb, None);
    let d_bl = evaluate_full(&s_blade, &Sts2Content, &f_bl, &rb);
    assert!(d_bl.retained_blade_bonus_base >= 31);
    assert!(
        compute_beam_rank_score_with_centi(
            &f_bl,
            &d_bl,
            &rb,
            false,
            false,
            None,
            EvalMode::SetupAndSurvival,
            0,
            0,
        ) > d0.beam_rank_score
    );

    // 4. Fool ready_arrows + melody_cards_finished.
    let mut fool_root = build_combat(
        CharacterId::Fool,
        &[CardId::StrikeFool, CardId::DefendFool],
        &[],
        &[],
        &[(MonsterId::Nibbit, 30)],
        RoomType::Monster,
        "FOOL_EVAL_TEST",
    );
    fool_root.player.counters.melody_cards_finished_this_turn = 2;
    let rb_fool = RootEvalBaseline::from_state_with_policy(
        &fool_root,
        &Sts2Content,
        &SearchPolicy::default(),
    );
    let ff = evaluate_fast(&fool_root, &Sts2Content, &rb_fool, None);
    let df = evaluate_full(&fool_root, &Sts2Content, &ff, &rb_fool);
    assert_eq!(df.ready_arrows, fool_root.player.arrows.len() as i32);
    assert_eq!(df.melody_cards_finished, 2);
}

#[test]
fn depth_zero_turn_setup_and_high_branching_multi_pick_choices() {
    // 1. Optional multi-pick N > 6 (BagOfPreparation + GamblingChip => 7 hand cards, min=0, max=7)
    // AND exercises Depth-0 TurnSetup when root.pending.is_some().
    let deck = vec![
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::Bash,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::StrikeIronclad,
    ];
    let opt_root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::BagOfPreparation, RelicId::GamblingChip],
        &[],
        &[(MonsterId::Nibbit, 14)],
        RoomType::Monster,
        "TURN_SETUP_GAMBLING_TEST",
    );
    assert!(
        opt_root.pending.is_some(),
        "BagOfPreparation + GamblingChip root must start paused on TurnSetup choice"
    );
    let policy = SearchPolicy {
        beam_width: 16,
        max_nodes: 2500,
        time_budget_ms: 5_000,
        use_beam_width_portfolio: false,
        ..Default::default()
    };
    let opt_resp = SearchEngine
        .solve_state(&opt_root, &policy, &Sts2Content)
        .unwrap();
    assert!(opt_resp.stats.won);
    assert!(opt_resp.stats.choice_replay_attempts > 0);
    assert!(
        matches!(opt_resp.actions.first(), Some(PlannedAction::TurnSetup { .. })),
        "first action on paused root must be TurnSetup, got {:?}",
        opt_resp.actions.first()
    );
    assert!(replay_actions_to_end(&opt_root, &opt_resp.actions).won);

    // 2. Fixed-k multi-pick N > 10: Dredge choosing k=3 from 12 discard cards.
    let mut fix_deck = vec![CardId::Dredge];
    for _ in 0..12 {
        fix_deck.push(CardId::StrikeNecrobinder);
    }
    let mut fix_root = build_combat(
        CharacterId::Necrobinder,
        &fix_deck,
        &[],
        &[],
        &[(MonsterId::Nibbit, 16)],
        RoomType::Monster,
        "FIXED_K_DREDGE_TEST",
    );
    // Move Dredge to hand and 12 Strikes into discard so Dredge immediately triggers N=12, k=3 choice.
    let mut dredge_h = None;
    let mut discard_hs = Vec::new();
    for c in &mut fix_root.cards {
        if c.id == CardId::Dredge {
            c.pile = PileType::Hand;
            dredge_h = Some(c.handle);
        } else {
            c.pile = PileType::Discard;
            discard_hs.push(c.handle);
        }
    }
    fix_root.player.draw.clear();
    fix_root.player.hand = vec![dredge_h.unwrap()];
    fix_root.player.discard = discard_hs;
    let fix_resp = SearchEngine
        .solve_state(&fix_root, &policy, &Sts2Content)
        .unwrap();
    assert!(fix_resp.stats.won);
    assert!(fix_resp.stats.choice_replay_attempts > 0);
    assert!(replay_actions_to_end(&fix_root, &fix_resp.actions).won);
}

#[test]
fn parallel_stand_pat_dop1_and_dop4_are_identical() {
    let deck = vec![
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::Bash,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::TrueGrit,
        CardId::Headbutt,
    ];
    let root = build_combat(
        CharacterId::Ironclad,
        &deck,
        &[RelicId::BurningBlood],
        &[],
        &[(MonsterId::Nibbit, 32)],
        RoomType::Monster,
        "PARALLEL_STANDPAT_DET_TEST",
    );
    let p1 = SearchPolicy {
        beam_width: 24,
        max_nodes: 3000,
        time_budget_ms: 0,
        max_degree_of_parallelism: 1,
        use_beam_width_portfolio: false,
        ..Default::default()
    };
    let p4 = SearchPolicy {
        max_degree_of_parallelism: 4,
        ..p1.clone()
    };
    let r1 = SearchEngine.solve_state(&root, &p1, &Sts2Content).unwrap();
    let r4 = SearchEngine.solve_state(&root, &p4, &Sts2Content).unwrap();
    assert_eq!(r1.stats.won, r4.stats.won);
    assert_eq!(r1.stats.hp_lost, r4.stats.hp_lost);
    assert_eq!(r1.stats.strategic_hp_deficit, r4.stats.strategic_hp_deficit);
    assert_eq!(r1.score, r4.score);
    assert_eq!(r1.actions, r4.actions);
}
