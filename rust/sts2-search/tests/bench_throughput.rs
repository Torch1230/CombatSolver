use smallvec::SmallVec;
use std::sync::Arc;
use std::time::Instant;
use sts2_content::Sts2Content;
use sts2_kernel::{
    BattleState, CardHandle, CardInst, CardTempFlags, CombatEngine, ContentDispatcher,
    CreatureHandle, CreatureInst, MonsterInst, PlayerCounters, PlayerInst, RelicInst,
    RootStaticContext, SemanticHistory,
};
use sts2_search::{SearchEngine, SearchPolicy};
use sts2_types::{
    CardId, CardKeywords, CharacterId, CombatSide, GameMode, MonsterId, PileType, PlayerRngSet,
    PlayerTurnPhase, PotionId, RelicId, Rng, RoomType, RunRngSet,
};

fn build_bench_combat() -> BattleState {
    let content = Sts2Content;
    let run_rngs = RunRngSet::from_string_seed("BENCH_WAVE1");
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc_handle = CreatureHandle(1);

    let deck_ids = [
        CardId::StrikeSilent,
        CardId::StrikeSilent,
        CardId::StrikeSilent,
        CardId::StrikeSilent,
        CardId::StrikeSilent,
        CardId::DefendSilent,
        CardId::DefendSilent,
        CardId::DefendSilent,
        CardId::DefendSilent,
        CardId::DefendSilent,
        CardId::Neutralize,
        CardId::Survivor,
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

    let enemies = [(MonsterId::Nibbit, 48), (MonsterId::ShrinkerBeetle, 42)];
    let mut creatures = vec![CreatureInst {
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
    }];
    let mut enemy_handles = Vec::new();
    let mut encounter_slots = Vec::new();
    for (idx, &(mid, hp)) in enemies.iter().enumerate() {
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
            monster_id: Some(mid),
            current_hp: hp,
            max_hp: hp,
            block: 0,
            cumulative_hp_lost: 0,
            is_detached: false,
            monster: Some(MonsterInst {
                monster_id: mid,
                rng: Rng::from_seed(run_rngs.seed + 1 + idx as u64),
                is_performing_move: false,
                spawned_this_turn: false,
                current_state_id: std::borrow::Cow::Borrowed(""),
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
    }

    let mut state = BattleState {
        static_ctx: Arc::new(RootStaticContext {
            encounter_id: "BENCH_ENCOUNTER".to_owned(),
            encounter_is_weak: false,
            room_type: RoomType::Monster,
            encounter_slots,
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
        is_in_progress: false,
        is_starting: false,
        won: false,
        is_player_extra_turn: false,
        gold_was_stolen: false,
        end_player_turn_requested: false,
        card_action_boundary_depth: 0,
        next_creature_id: 3,
        initial_encounter_monster_count: 2,
        next_card_handle: (cards.len() + 1) as u32,
        next_creature_handle: 4,
        next_power_handle: 1,
        next_attack_id: 1,
        run_rngs,
        player_rngs,
        player: PlayerInst {
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
            deck: deck_handles.to_vec(),
            potion_slots: smallvec::smallvec![
                Some(PotionId::FirePotion),
                Some(PotionId::EnergyPotion),
                None
            ],
            combat_generated_potion_mask: 0,
            orb_capacity: 0,
            orbs: Vec::new(),
            arrows: Vec::new(),
            relics: vec![RelicInst {
                id: RelicId::RingOfTheSnake,
                is_wax: false,
                is_melted: false,
                stack_count: 1,
                floor_added_to_deck: None,
                private_fields: Default::default(),
            }],
            card_or_potion_effect_depth: 0,
        },
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

#[test]
fn throughput_micro_benchmark_1t_vs_16t() {
    let root = build_bench_combat();
    let content = Sts2Content;

    let mut baseline_actions = None;
    let mut baseline_expanded = None;
    for dop in [1usize, 4usize, 16usize] {
        let policy = SearchPolicy {
            beam_width: 60,
            max_nodes: 25_000,
            time_budget_ms: 0,
            allow_potions: true,
            allow_random_cards: false,
            max_degree_of_parallelism: dop,
            ..Default::default()
        };
        let t0 = Instant::now();
        let resp = SearchEngine.solve_state(&root, &policy, &content).unwrap();
        let dt = t0.elapsed().as_secs_f64().max(1e-6);
        let trans_per_sec = resp.stats.transition_count as f64 / dt;
        let nodes_per_sec = resp.nodes_expanded as f64 / dt;
        println!(
            "[BENCH DOP={dop:2}] won={} hp_lost={} score={} expanded={} transitions={} wall_ms={:.2} trans/sec={:.0} nodes/sec={:.0} avg_packed_bytes={} tt_pruned={} por_pruned={} inc_pruned={}",
            resp.stats.won,
            resp.stats.hp_lost,
            resp.score,
            resp.nodes_expanded,
            resp.stats.transition_count,
            dt * 1000.0,
            trans_per_sec,
            nodes_per_sec,
            resp.stats.avg_packed_state_bytes,
            resp.stats.tt_branches_pruned,
            resp.stats.por_branches_pruned,
            resp.stats.incumbent_branches_pruned,
        );
        if let Some(ref base_acts) = baseline_actions {
            assert_eq!(resp.actions, *base_acts, "actions mismatch at DOP={dop}");
            assert_eq!(resp.nodes_expanded, baseline_expanded.unwrap(), "nodes mismatch at DOP={dop}");
        } else {
            baseline_actions = Some(resp.actions.clone());
            baseline_expanded = Some(resp.nodes_expanded);
        }
        assert!(resp.nodes_expanded > 0);
        assert!(resp.stats.transition_count > 0);
    }
}

fn build_custom_bench_combat(
    seed: &str,
    character: CharacterId,
    deck_ids: &[CardId],
    relics: &[RelicId],
    potions: [Option<PotionId>; 3],
    enemies: &[(MonsterId, i32, &str)],
) -> BattleState {
    let content = Sts2Content;
    let run_rngs = RunRngSet::from_string_seed(seed);
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc_handle = CreatureHandle(1);

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

    let mut creatures = vec![CreatureInst {
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
    }];
    let mut enemy_handles = Vec::new();
    let mut encounter_slots = Vec::new();
    for (idx, &(mid, hp, slot)) in enemies.iter().enumerate() {
        let eh = CreatureHandle((idx + 2) as u32);
        enemy_handles.push(eh);
        encounter_slots.push(slot.to_owned());
        creatures.push(CreatureInst {
            handle: eh,
            combat_id: Some((idx + 1) as u32),
            side: CombatSide::Enemy,
            slot_name: Some(std::borrow::Cow::Owned(slot.to_owned())),
            is_player: false,
            pet_owner_player_index: None,
            monster_id: Some(mid),
            current_hp: hp,
            max_hp: hp,
            block: 0,
            cumulative_hp_lost: 0,
            is_detached: false,
            monster: Some(MonsterInst {
                monster_id: mid,
                rng: Rng::from_seed(run_rngs.seed + 1 + idx as u64),
                is_performing_move: false,
                spawned_this_turn: false,
                current_state_id: std::borrow::Cow::Borrowed(""),
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
    }

    let mut state = BattleState {
        static_ctx: Arc::new(RootStaticContext {
            encounter_id: format!("BENCH_{seed}"),
            encounter_is_weak: false,
            room_type: RoomType::Monster,
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
            total_floor: 1,
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
        player: PlayerInst {
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
            deck: deck_handles.to_vec(),
            potion_slots: smallvec::smallvec![potions[0], potions[1], potions[2]],
            combat_generated_potion_mask: 0,
            orb_capacity: character.base_orb_slot_count(),
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
        },
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

fn run_dop_determinism_bench(label: &str, root: &BattleState, beam_width: usize, max_nodes: usize) {
    let content = Sts2Content;
    let mut baseline_actions = None;
    let mut baseline_expanded = None;
    for dop in [1usize, 4usize, 16usize] {
        let policy = SearchPolicy {
            beam_width,
            max_nodes,
            time_budget_ms: 0,
            allow_potions: true,
            allow_random_cards: false,
            max_degree_of_parallelism: dop,
            ..Default::default()
        };
        let t0 = Instant::now();
        let resp = SearchEngine.solve_state(root, &policy, &content).unwrap();
        let dt = t0.elapsed().as_secs_f64().max(1e-6);
        let trans_per_sec = resp.stats.transition_count as f64 / dt;
        let nodes_per_sec = resp.nodes_expanded as f64 / dt;
        let approx_arena_kb =
            (resp.nodes_expanded as u64 * resp.stats.avg_packed_state_bytes) / 1024;
        println!(
            "[{label} DOP={dop:2}] won={} hp_lost={} score={} expanded={} transitions={} wall_ms={:.2} trans/sec={:.0} nodes/sec={:.0} avg_packed_bytes={} approx_arena_kb={}",
            resp.stats.won,
            resp.stats.hp_lost,
            resp.score,
            resp.nodes_expanded,
            resp.stats.transition_count,
            dt * 1000.0,
            trans_per_sec,
            nodes_per_sec,
            resp.stats.avg_packed_state_bytes,
            approx_arena_kb,
        );
        if let Some(ref base_acts) = baseline_actions {
            assert_eq!(resp.actions, *base_acts, "{label} actions mismatch at DOP={dop}");
            assert_eq!(resp.nodes_expanded, baseline_expanded.unwrap(), "{label} nodes mismatch at DOP={dop}");
        } else {
            baseline_actions = Some(resp.actions.clone());
            baseline_expanded = Some(resp.nodes_expanded);
        }
        assert!(resp.nodes_expanded > 0);
        assert!(resp.stats.transition_count > 0);
    }
}

#[test]
fn mawler_search_determinism_and_throughput() {
    let root = build_custom_bench_combat(
        "BENCH_MAWLER",
        CharacterId::Ironclad,
        &[
            CardId::Bash,
            CardId::StrikeIronclad,
            CardId::StrikeIronclad,
            CardId::StrikeIronclad,
            CardId::StrikeIronclad,
            CardId::StrikeIronclad,
            CardId::DefendIronclad,
            CardId::DefendIronclad,
            CardId::DefendIronclad,
            CardId::DefendIronclad,
            CardId::ShrugItOff,
            CardId::Uppercut,
        ],
        &[RelicId::BurningBlood],
        [Some(PotionId::FirePotion), None, None],
        &[(MonsterId::Mawler, 72, "slot_0")],
    );
    run_dop_determinism_bench("MAWLER", &root, 60, 25_000);
}

#[test]
fn queen_search_determinism_and_throughput() {
    let root = build_custom_bench_combat(
        "BENCH_QUEEN",
        CharacterId::Silent,
        &[
            CardId::Neutralize,
            CardId::Survivor,
            CardId::BladeDance,
            CardId::DeadlyPoison,
            CardId::NoxiousFumes,
            CardId::Footwork,
            CardId::Backflip,
            CardId::StrikeSilent,
            CardId::StrikeSilent,
            CardId::DefendSilent,
            CardId::DefendSilent,
            CardId::DefendSilent,
        ],
        &[RelicId::RingOfTheSnake, RelicId::Vambrace],
        [Some(PotionId::BlockPotion), Some(PotionId::FirePotion), None],
        &[
            (MonsterId::TorchHeadAmalgam, 199, "amalgam"),
            (MonsterId::Queen, 400, "queen"),
        ],
    );
    run_dop_determinism_bench("QUEEN", &root, 60, 25_000);
}
