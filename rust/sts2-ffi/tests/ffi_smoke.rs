use std::collections::BTreeMap;
use std::ffi::c_void;
use std::ptr;
use std::slice;
use std::sync::Arc;
use sts2_combat_rs::host_wire::{HostProgressDto, HostSolveResponse};
use sts2_combat_rs::{
    ABI_VERSION, OracleActionJson, OracleStepResponse, SolveInCombatJsonResponse,
    sts2_rs_abi_version, sts2_rs_cancel_token_cancel, sts2_rs_cancel_token_create,
    sts2_rs_cancel_token_free, sts2_rs_capabilities, sts2_rs_free_buffer,
    sts2_rs_solve_in_combat, sts2_rs_solve_in_combat_host_v1,
    sts2_rs_solve_in_combat_with_controls, sts2_rs_step_oracle,
};
use sts2_content::Sts2Content;
use sts2_kernel::snapshot::export_live_state;
use sts2_kernel::{
    BattleState, CardHandle, CardInst, CardTempFlags, CombatEngine, ContentDispatcher,
    CreatureHandle, CreatureInst, MonsterInst, PlayerCounters, PlayerInst, RelicInst,
    RootStaticContext, SemanticHistory,
};
use sts2_search::{InCombatSolveRequest, SearchPolicy, SearchProgress};
use sts2_types::{
    CardId, CardKeywords, CharacterId, CombatSide, GameMode, MonsterId, PileType, PlayerRngSet,
    PlayerTurnPhase, RelicId, Rng, RoomType, RunRngSet,
};

#[test]
fn ffi_abi_and_capabilities_smoke() {
    assert_eq!(sts2_rs_abi_version(), ABI_VERSION);
    let mut ptr: *mut u8 = ptr::null_mut();
    let mut len: usize = 0;
    let rc = sts2_rs_capabilities(&mut ptr, &mut len);
    assert_eq!(rc, 0);
    assert!(!ptr.is_null());
    assert!(len > 0);
    let bytes = unsafe { slice::from_raw_parts(ptr, len) };
    let val: serde_json::Value = serde_json::from_slice(bytes).unwrap();
    assert_eq!(val["abiVersion"], 1);
    assert_eq!(val["engineId"], "sts2-combat-rs");
    sts2_rs_free_buffer(ptr, len);
}

#[test]
fn ffi_step_oracle_handles_invalid_and_unsupported() {
    let mut ptr: *mut u8 = ptr::null_mut();
    let mut len: usize = 0;
    let bad = b"{not json";
    let rc = sts2_rs_step_oracle(bad.as_ptr(), bad.len(), &mut ptr, &mut len);
    assert_eq!(rc, 0);
    let bytes = unsafe { slice::from_raw_parts(ptr, len) };
    let resp: OracleStepResponse = serde_json::from_slice(bytes).unwrap();
    assert_eq!(resp.outcome, "Error");
    assert_eq!(resp.error_code.as_deref(), Some("InvalidRequestJson"));
    sts2_rs_free_buffer(ptr, len);

    // Verify EndTurn works both with and without choices
    let legacy_end_turn: OracleActionJson =
        serde_json::from_str(r#"{"kind":"EndTurn"}"#).unwrap();
    assert!(matches!(
        legacy_end_turn,
        OracleActionJson::EndTurn { choices: None }
    ));
    let choice_end_turn: OracleActionJson =
        serde_json::from_str(r#"{"kind":"EndTurn","choices":[[0]]}"#).unwrap();
    assert!(matches!(
        choice_end_turn,
        OracleActionJson::EndTurn { choices: Some(_) }
    ));

    // Verify sts2_rs_solve_in_combat scaffold returns Unsupported cleanly
    let solve_req = br#"{"policy":{"beamWidth":32,"maxNodes":1000,"timeBudgetMs":100,"allowPotions":true,"allowRandomCards":false},"rootStateJson":null,"traceTag":null}"#;
    let mut s_ptr: *mut u8 = ptr::null_mut();
    let mut s_len: usize = 0;
    let src = sts2_rs_solve_in_combat(solve_req.as_ptr(), solve_req.len(), &mut s_ptr, &mut s_len);
    assert_eq!(src, 0);
    let s_bytes = unsafe { slice::from_raw_parts(s_ptr, s_len) };
    let s_resp: SolveInCombatJsonResponse = serde_json::from_slice(s_bytes).unwrap();
    assert_eq!(s_resp.outcome, "Unsupported");
    assert_eq!(s_resp.error_code.as_deref(), Some("Unsupported"));
    sts2_rs_free_buffer(s_ptr, s_len);
}

fn build_smoke_live_state_json() -> sts2_kernel::snapshot::LiveStateJson {
    let content = Sts2Content;
    let run_rngs = RunRngSet::from_string_seed("FFI_SMOKE_1");
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc = CreatureHandle(1);
    let ec = CreatureHandle(2);
    let deck_ids = [
        CardId::Bash,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
    ];
    let mut cards = Vec::new();
    let mut deck = smallvec::SmallVec::new();
    for (i, &cid) in deck_ids.iter().enumerate() {
        let meta = content.card_meta(cid).unwrap();
        let h = CardHandle((i + 1) as u32);
        deck.push(h);
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
            transformation_pools: Vec::new(),
            map_col: 0,
            map_row: 0,
            act_index: 0,
            total_floor: 1,
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
            energy: 0,
            stars: 0,
            phase: PlayerTurnPhase::None,
            turn_number: 1,
            counters: PlayerCounters::default(),
            hand: smallvec::SmallVec::new(),
            draw: smallvec::SmallVec::new(),
            discard: smallvec::SmallVec::new(),
            exhaust: smallvec::SmallVec::new(),
            play: smallvec::SmallVec::new(),
            deck,
            potion_slots: smallvec::smallvec![None, None, None],
            orb_capacity: 0,
            orbs: smallvec::SmallVec::new(),
            arrows: smallvec::SmallVec::new(),
            relics: smallvec::smallvec![RelicInst {
                id: RelicId::BurningBlood,
                is_wax: false,
                is_melted: false,
                stack_count: 1,
                floor_added_to_deck: None,
                private_fields: BTreeMap::new(),
            }],
            card_or_potion_effect_depth: 0,
        },
        cards,
        card_extensions: Vec::new(),
        creatures: vec![
            CreatureInst {
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
                powers: smallvec::SmallVec::new(),
            },
            CreatureInst {
                handle: ec,
                combat_id: Some(1),
                side: CombatSide::Enemy,
                slot_name: Some("slot_0".to_owned()),
                is_player: false,
                pet_owner_player_index: None,
                monster_id: Some(MonsterId::FuzzyWurmCrawler),
                current_hp: 20,
                max_hp: 20,
                block: 0,
                cumulative_hp_lost: 0,
                is_detached: false,
                monster: Some(MonsterInst {
                    monster_id: MonsterId::FuzzyWurmCrawler,
                    rng: Rng::from_seed(77),
                    is_performing_move: false,
                    spawned_this_turn: false,
                    current_state_id: String::new(),
                    next_move_id: None,
                    next_move_can_transition_away: true,
                    performed_first_move: false,
                    state_log_ids: smallvec::SmallVec::new(),
                    transient_move_follow_up_state_id: None,
                    setup_fields: BTreeMap::new(),
                    private_fields: BTreeMap::new(),
                }),
                powers: smallvec::SmallVec::new(),
            },
        ],
        power_extensions: Vec::new(),
        ally_handles: smallvec::smallvec![pc],
        enemy_handles: smallvec::smallvec![ec],
        escaped_handles: smallvec::SmallVec::new(),
        removed_handles: smallvec::SmallVec::new(),
        spawned_enemy_handles: smallvec::smallvec![ec],
        pet_handles: smallvec::SmallVec::new(),
        damage_history: Vec::new(),
        semantic_history: SemanticHistory::default(),
        pending: None,
        vakuu_auto_select_depth: 0,
    };
    CombatEngine::start_combat(&mut state, &content).unwrap();
    export_live_state(&state).unwrap()
}

extern "C" fn collect_progress_cb(ptr: *const u8, len: usize, user_data: *mut c_void) {
    let bytes = unsafe { slice::from_raw_parts(ptr, len) };
    let prog: SearchProgress = serde_json::from_slice(bytes).unwrap();
    let vec = unsafe { &mut *(user_data as *mut Vec<SearchProgress>) };
    vec.push(prog);
}

#[test]
fn ffi_solve_with_controls_progress_and_cancel_token() {
    let live = build_smoke_live_state_json();
    let req = InCombatSolveRequest {
        policy: SearchPolicy {
            beam_width: 16,
            max_nodes: 500,
            time_budget_ms: 2000,
            allow_potions: true,
            allow_random_cards: false,
            ..Default::default()
        },
        root_state_json: Some(live),
        trace_tag: Some("ffi-controls".to_owned()),
    };
    let req_bytes = serde_json::to_vec(&req).unwrap();

    // 1. Run with live token + progress callback
    let token = sts2_rs_cancel_token_create();
    assert!(!token.is_null());
    let mut progress_events: Vec<SearchProgress> = Vec::new();
    let mut out_ptr: *mut u8 = ptr::null_mut();
    let mut out_len: usize = 0;
    let rc = sts2_rs_solve_in_combat_with_controls(
        req_bytes.as_ptr(),
        req_bytes.len(),
        token,
        Some(collect_progress_cb),
        (&mut progress_events as *mut Vec<SearchProgress>) as *mut c_void,
        &mut out_ptr,
        &mut out_len,
    );
    assert_eq!(rc, 0);
    let resp_bytes = unsafe { slice::from_raw_parts(out_ptr, out_len) };
    let resp: SolveInCombatJsonResponse = serde_json::from_slice(resp_bytes).unwrap();
    sts2_rs_free_buffer(out_ptr, out_len);
    assert_eq!(resp.outcome, "Ok");
    let sol = resp.solution.unwrap();
    assert!(sol.stats.won);
    assert!(!sol.actions.is_empty());
    assert!(!progress_events.is_empty());

    // 2. Pre-cancel via sts2_rs_cancel_token_cancel and confirm search terminates immediately as Cancelled
    sts2_rs_cancel_token_cancel(token);
    let mut c_ptr: *mut u8 = ptr::null_mut();
    let mut c_len: usize = 0;
    let crc = sts2_rs_solve_in_combat_with_controls(
        req_bytes.as_ptr(),
        req_bytes.len(),
        token,
        None,
        ptr::null_mut(),
        &mut c_ptr,
        &mut c_len,
    );
    assert_eq!(crc, 0);
    let c_bytes = unsafe { slice::from_raw_parts(c_ptr, c_len) };
    let c_resp: SolveInCombatJsonResponse = serde_json::from_slice(c_bytes).unwrap();
    sts2_rs_free_buffer(c_ptr, c_len);
    assert_eq!(c_resp.outcome, "Ok");
    let c_sol = c_resp.solution.unwrap();
    assert_eq!(c_sol.stats.termination, "Cancelled");
    assert_eq!(c_sol.nodes_expanded, 0);

    sts2_rs_cancel_token_free(token);
}

extern "C" fn collect_host_progress_cb(ptr: *const u8, len: usize, user_data: *mut c_void) {
    let bytes = unsafe { slice::from_raw_parts(ptr, len) };
    let prog: HostProgressDto = serde_json::from_slice(bytes).unwrap();
    let vec = unsafe { &mut *(user_data as *mut Vec<HostProgressDto>) };
    vec.push(prog);
}

fn build_host_request_json_with_policy_override(
    live_value: serde_json::Value,
    potion_policy: &str,
    dop: i32,
    override_policy: Option<serde_json::Value>,
) -> Vec<u8> {
    let growth = serde_json::json!({
        "handOfGreed": 0, "theHunt": 0, "feed": 0, "royalties": 0,
        "alchemize": 0, "geneticAlgorithm": 0, "theScythe": 0,
        "goopy": 0, "forbiddenGrimoire": 0, "madScience": 0, "showOff": 0,
        "thirdParty": null
    });
    let policy = override_policy.unwrap_or_else(|| {
        serde_json::json!({
            "potionPolicy": potion_policy,
            "potionDirectives": [],
            "predictPotionReward": false,
            "relicStrategyEnabled": true,
            "relicCounterRules": [],
            "growthBudgets": growth,
            "brightestFlameMaxHpLossLimit": null,
            "ignoreLongTermRewards": false,
            "actTransitionBossHpStrategy": "ProgressionFirst",
            "finalBossHpStrategy": "ProgressionFirst",
            "acceptableBattleHpLoss": 0,
            "stopAtAcceptableBattleHpLoss": true,
            "useBeamWidthPortfolio": true,
            "useNoveltyPortfolio": false,
            "useEarlyTurnExploration": false,
            "beamWidth": 16,
            "potionFreeBeamWidth": null,
            "potionBeamWidth": null,
            "maxExpandedNodes": 500,
            "maxCardBranchesPerNode": 32,
            "maxPileChoiceBranchesPerAction": 18,
            "maxHandChoiceBranchesPerAction": 24,
            "softTimeBudgetMilliseconds": 2000,
            "theftPolicy": "Ignore"
        })
    });
    let req = serde_json::json!({
        "abiVersion": 1,
        "requestId": "host-smoke-1",
        "liveState": live_value,
        "characterId": "IRONCLAD",
        "ascension": 0,
        "gameMode": "Standard",
        "dominionId": null,
        "runModelIds": ["RUN.MULTIPLAYER_SCALING_MODEL"],
        "encounterIsWeak": true,
        "initialEncounterMonsterCount": 1,
        "totalFloor": 1,
        "actIndex": 0,
        "mapCol": 0,
        "mapRow": 0,
        "priorBattleHpLost": 0,
        "searchBudgetMilliseconds": 2000,
        "overallTimeoutMilliseconds": 5000,
        "maxDegreeOfParallelism": dop,
        "policy": policy
    });
    serde_json::to_vec(&req).unwrap()
}

fn build_host_request_json(
    live_value: serde_json::Value,
    potion_policy: &str,
    dop: i32,
) -> Vec<u8> {
    build_host_request_json_with_policy_override(live_value, potion_policy, dop, None)
}

#[test]
fn ffi_solve_in_combat_host_v1_round_trip_cancel_and_unsupported() {
    let live = build_smoke_live_state_json();
    let mut live_val = serde_json::to_value(&live).unwrap();
    // Host InCombatLiveStateDto carries an extra "verification" object ignored by Rust LiveStateJson.
    live_val
        .as_object_mut()
        .unwrap()
        .insert("verification".to_owned(), serde_json::json!({ "playerHp": 80 }));

    let req_bytes = build_host_request_json(live_val.clone(), "Smart", 4);
    let token = sts2_rs_cancel_token_create();
    let mut progress_events: Vec<HostProgressDto> = Vec::new();
    let mut out_ptr: *mut u8 = ptr::null_mut();
    let mut out_len: usize = 0;
    let rc = sts2_rs_solve_in_combat_host_v1(
        req_bytes.as_ptr(),
        req_bytes.len(),
        token,
        Some(collect_host_progress_cb),
        (&mut progress_events as *mut Vec<HostProgressDto>) as *mut c_void,
        &mut out_ptr,
        &mut out_len,
    );
    assert_eq!(rc, 0);
    let bytes = unsafe { slice::from_raw_parts(out_ptr, out_len) };
    let resp: HostSolveResponse = serde_json::from_slice(bytes).unwrap();
    sts2_rs_free_buffer(out_ptr, out_len);

    assert_eq!(resp.abi_version, 1);
    assert_eq!(resp.outcome, "Succeeded");
    assert!(resp.effective_degree_of_parallelism >= 1);
    let res = resp.result.expect("expected result");
    assert!(!res.actions.is_empty());
    assert!(res.actions.iter().all(|a| !a.card_id.contains('.')));
    assert!(res.terminal.all_enemies_dead);
    assert!(res.terminal.combat_ended_turn.is_some());
    assert_eq!(res.terminal.search_boundary, "None");
    assert_eq!(res.terminal.confidence, "Complete");
    let action_turns: std::collections::BTreeSet<i32> = res.actions.iter().map(|a| a.turn).collect();
    let outcome_turns: std::collections::BTreeSet<i32> =
        res.turn_outcomes.iter().map(|t| t.turn).collect();
    assert_eq!(action_turns, outcome_turns);
    assert!(res.turn_outcomes.last().unwrap().combat_ended);
    assert!(!progress_events.is_empty());
    assert_eq!(
        progress_events[0].active_phase_label.as_deref(),
        Some("RustKernel")
    );

    // Pre-cancelled token returns Cancelled cleanly
    sts2_rs_cancel_token_cancel(token);
    let mut c_ptr: *mut u8 = ptr::null_mut();
    let mut c_len: usize = 0;
    let crc = sts2_rs_solve_in_combat_host_v1(
        req_bytes.as_ptr(),
        req_bytes.len(),
        token,
        None,
        ptr::null_mut(),
        &mut c_ptr,
        &mut c_len,
    );
    assert_eq!(crc, 0);
    let c_bytes = unsafe { slice::from_raw_parts(c_ptr, c_len) };
    let c_resp: HostSolveResponse = serde_json::from_slice(c_bytes).unwrap();
    sts2_rs_free_buffer(c_ptr, c_len);
    sts2_rs_cancel_token_free(token);
    assert_eq!(c_resp.outcome, "Cancelled");
    assert!(c_resp.result.is_none());

    // Unsupported card ID maps cleanly to CapabilityMiss("model_unmapped")
    let mut bad_live = live_val;
    bad_live["cards"][0]["cardId"] = serde_json::json!("CARD.NONEXISTENT_MOD_CARD");
    let bad_bytes = build_host_request_json(bad_live, "Smart", 1);
    let mut u_ptr: *mut u8 = ptr::null_mut();
    let mut u_len: usize = 0;
    let urc = sts2_rs_solve_in_combat_host_v1(
        bad_bytes.as_ptr(),
        bad_bytes.len(),
        ptr::null(),
        None,
        ptr::null_mut(),
        &mut u_ptr,
        &mut u_len,
    );
    assert_eq!(urc, 0);
    let u_bytes = unsafe { slice::from_raw_parts(u_ptr, u_len) };
    let u_resp: HostSolveResponse = serde_json::from_slice(u_bytes).unwrap();
    sts2_rs_free_buffer(u_ptr, u_len);
    assert_eq!(u_resp.outcome, "CapabilityMiss");
    assert_eq!(u_resp.failure_code.as_deref(), Some("model_unmapped"));
}

#[test]
fn ffi_solve_in_combat_host_v1_potion_trace() {
    let mut live = build_smoke_live_state_json();
    live.player.potion_slots = vec![Some("POTION.FIRE_POTION".to_owned()), None, None];
    live.creatures[1].current_hp = 26;
    live.creatures[1].max_hp = 26;
    let live_val = serde_json::to_value(&live).unwrap();

    let req_bytes = build_host_request_json(live_val, "RequireAtLeastOne", 2);
    let mut out_ptr: *mut u8 = ptr::null_mut();
    let mut out_len: usize = 0;
    let rc = sts2_rs_solve_in_combat_host_v1(
        req_bytes.as_ptr(),
        req_bytes.len(),
        ptr::null(),
        None,
        ptr::null_mut(),
        &mut out_ptr,
        &mut out_len,
    );
    assert_eq!(rc, 0);
    let bytes = unsafe { slice::from_raw_parts(out_ptr, out_len) };
    let resp: HostSolveResponse = serde_json::from_slice(bytes).unwrap();
    sts2_rs_free_buffer(out_ptr, out_len);
    assert_eq!(resp.outcome, "Succeeded");
    let res = resp.result.unwrap();
    assert!(!res.potion_uses.is_empty());
    assert_eq!(res.potion_uses[0].id, "FIRE_POTION");
    assert_eq!(res.potion_uses[0].slot, Some(0));
    assert!(res.actions.iter().any(|a| a.kind == "UsePotion" && a.potion_id == "FIRE_POTION"));
}

fn call_host_v1(req_bytes: &[u8]) -> HostSolveResponse {
    let mut out_ptr: *mut u8 = ptr::null_mut();
    let mut out_len: usize = 0;
    let rc = sts2_rs_solve_in_combat_host_v1(
        req_bytes.as_ptr(),
        req_bytes.len(),
        ptr::null(),
        None,
        ptr::null_mut(),
        &mut out_ptr,
        &mut out_len,
    );
    assert_eq!(rc, 0);
    let bytes = unsafe { slice::from_raw_parts(out_ptr, out_len) };
    let resp: HostSolveResponse = serde_json::from_slice(bytes).unwrap();
    sts2_rs_free_buffer(out_ptr, out_len);
    resp
}

#[test]
fn ffi_host_v1_full_policy_v2_and_fail_closed_validation() {
    let mut live = build_smoke_live_state_json();
    live.player.potion_slots = vec![Some("POTION.FIRE_POTION".to_owned()), None, None];
    let live_val = serde_json::to_value(&live).unwrap();

    // 1. Full 37-field v2 HostPolicyDto succeeds cleanly and honors Force directive.
    let full_policy = serde_json::json!({
        "potionPolicy": "Disabled",
        "potionDirectives": [
            { "slot": 0, "potionId": "FIRE_POTION", "directive": "Force" }
        ],
        "potionHpThresholdOverrides": { "FIRE_POTION": 3 },
        "predictPotionReward": true,
        "relicStrategyEnabled": true,
        "relicCounterRules": [
            { "id": "HappyFlower", "enabled": true, "minimum": 0, "maximum": 2, "hpAllowance": 0, "priority": 1 }
        ],
        "growthBudgets": {
            "handOfGreed": 10, "theHunt": 0, "feed": 0, "royalties": 0,
            "alchemize": 0, "geneticAlgorithm": 0, "theScythe": 0,
            "goopy": 0, "forbiddenGrimoire": 0, "madScience": 0, "showOff": 0,
            "thirdParty": null
        },
        "brightestFlameMaxHpLossLimit": 4,
        "ignoreLongTermRewards": false,
        "actTransitionBossHpStrategy": "MinimizeHpLoss",
        "finalBossHpStrategy": "MinimizeHpLoss",
        "acceptableBattleHpLoss": 0,
        "stopAtAcceptableBattleHpLoss": true,
        "useBeamWidthPortfolio": true,
        "useNoveltyPortfolio": false,
        "useEarlyTurnExploration": true,
        "beamWidth": 16,
        "potionFreeBeamWidth": 10,
        "potionBeamWidth": 6,
        "maxExpandedNodes": 600,
        "maxCardBranchesPerNode": 32,
        "maxPileChoiceBranchesPerAction": 12,
        "maxHandChoiceBranchesPerAction": 18,
        "softTimeBudgetMilliseconds": 2000,
        "theftPolicy": "Ignore",
        "portfolioOptionsVersion": 2,
        "beamWidthPortfolioWidths": [16, 12],
        "beamWidthPortfolioPlainBaselineMember": true,
        "reallocatedRefinementPortfolio": true,
        "offensiveRefinementPortfolio": false,
        "boundedOffensiveRefinementPortfolio": true,
        "stopPortfolioAtHpTarget": true,
        "adaptiveNoveltyRefinement": true,
        "noveltySearch": {
            "width": 2, "familiarAllowance": 2, "maxOpen": 512,
            "maxNoveltyEntries": 4096, "maxActions": 32, "maxTurns": 12
        },
        "noveltyBudget": {
            "timeDivisor": 2, "maximumMilliseconds": 1500, "maximumExpandedNodes": 500
        },
        "earlyTurnExplorationDepth": 1,
        "earlyTurnExplorationBudgetMilliseconds": 2000,
        "portfolioSchedulingMode": "Parallel"
    });
    let full_req =
        build_host_request_json_with_policy_override(live_val.clone(), "Disabled", 2, Some(full_policy.clone()));
    let full_resp = call_host_v1(&full_req);
    assert_eq!(full_resp.outcome, "Succeeded");
    let res = full_resp.result.unwrap();
    assert!(res.terminal.all_enemies_dead);
    assert!(res.actions.iter().any(|a| a.kind == "UsePotion" && a.potion_id == "FIRE_POTION"));

    // 2. Unknown field on HostPolicyDto triggers CapabilityMiss("policy_unsupported") via deny_unknown_fields.
    let mut bad_unknown_field = full_policy.clone();
    bad_unknown_field.as_object_mut().unwrap().insert(
        "experimentalUnknownToggle".to_owned(),
        serde_json::json!(true),
    );
    let resp_unknown = call_host_v1(&build_host_request_json_with_policy_override(
        live_val.clone(),
        "Smart",
        1,
        Some(bad_unknown_field),
    ));
    assert_eq!(resp_unknown.outcome, "CapabilityMiss");
    assert_eq!(resp_unknown.failure_code.as_deref(), Some("policy_unsupported"));

    // 3. Unknown relicCounterRules ID triggers CapabilityMiss("policy_unsupported").
    let mut bad_relic = full_policy.clone();
    bad_relic["relicCounterRules"] = serde_json::json!([
        { "id": "ModdedUnknownRelicCounter", "enabled": true, "minimum": 0, "maximum": 1, "hpAllowance": 10, "priority": 1 }
    ]);
    let resp_relic = call_host_v1(&build_host_request_json_with_policy_override(
        live_val.clone(),
        "Smart",
        1,
        Some(bad_relic),
    ));
    assert_eq!(resp_relic.outcome, "CapabilityMiss");
    assert_eq!(resp_relic.failure_code.as_deref(), Some("policy_unsupported"));

    // 4. Non-zero showOff budget triggers CapabilityMiss("policy_unsupported").
    let mut bad_showoff = full_policy.clone();
    bad_showoff["growthBudgets"]["showOff"] = serde_json::json!(15);
    let resp_showoff = call_host_v1(&build_host_request_json_with_policy_override(
        live_val.clone(),
        "Smart",
        1,
        Some(bad_showoff),
    ));
    assert_eq!(resp_showoff.outcome, "CapabilityMiss");
    assert_eq!(resp_showoff.failure_code.as_deref(), Some("policy_unsupported"));

    // 5. Non-zero thirdParty budget triggers CapabilityMiss("policy_unsupported").
    let mut bad_tp = full_policy.clone();
    bad_tp["growthBudgets"]["thirdParty"] = serde_json::json!({ "MY_MOD_GROWTH": 10 });
    let resp_tp = call_host_v1(&build_host_request_json_with_policy_override(
        live_val.clone(),
        "Smart",
        1,
        Some(bad_tp),
    ));
    assert_eq!(resp_tp.outcome, "CapabilityMiss");
    assert_eq!(resp_tp.failure_code.as_deref(), Some("policy_unsupported"));

    // 6. Unknown potion directive triggers CapabilityMiss("policy_unsupported").
    let mut bad_dir = full_policy.clone();
    bad_dir["potionDirectives"] = serde_json::json!([
        { "slot": 0, "potionId": "FIRE_POTION", "directive": "Avoid" }
    ]);
    let resp_dir = call_host_v1(&build_host_request_json_with_policy_override(
        live_val.clone(),
        "Smart",
        1,
        Some(bad_dir),
    ));
    assert_eq!(resp_dir.outcome, "CapabilityMiss");
    assert_eq!(resp_dir.failure_code.as_deref(), Some("policy_unsupported"));

    // 7. Conflicting offensive refinement flags trigger CapabilityMiss("policy_unsupported").
    let mut bad_off = full_policy.clone();
    bad_off["offensiveRefinementPortfolio"] = serde_json::json!(true);
    let resp_off = call_host_v1(&build_host_request_json_with_policy_override(
        live_val.clone(),
        "Smart",
        1,
        Some(bad_off),
    ));
    assert_eq!(resp_off.outcome, "CapabilityMiss");
    assert_eq!(resp_off.failure_code.as_deref(), Some("policy_unsupported"));

    // 8. "Forbid" alias works identically to "Disabled" in potionDirectives.
    let mut forbid_policy = full_policy.clone();
    forbid_policy["potionPolicy"] = serde_json::json!("RequireAtLeastOne");
    forbid_policy["potionDirectives"] = serde_json::json!([
        { "slot": 0, "potionId": "FIRE_POTION", "directive": "Forbid" }
    ]);
    let resp_forbid = call_host_v1(&build_host_request_json_with_policy_override(
        live_val.clone(),
        "RequireAtLeastOne",
        1,
        Some(forbid_policy),
    ));
    assert_eq!(resp_forbid.outcome, "Succeeded");
    assert!(resp_forbid.result.unwrap().potion_uses.is_empty());

    // 9. Out-of-range or unknown potionHpThresholdOverrides fails closed with CapabilityMiss("policy_unsupported").
    let mut bad_override_range = full_policy.clone();
    bad_override_range["potionHpThresholdOverrides"] = serde_json::json!({ "FIRE_POTION": 101 });
    let resp_range = call_host_v1(&build_host_request_json_with_policy_override(
        live_val.clone(),
        "Smart",
        1,
        Some(bad_override_range),
    ));
    assert_eq!(resp_range.outcome, "CapabilityMiss");
    assert_eq!(resp_range.failure_code.as_deref(), Some("policy_unsupported"));

    let mut bad_override_id = full_policy;
    bad_override_id["potionHpThresholdOverrides"] = serde_json::json!({ "MOD_UNKNOWN_POTION": 10 });
    let resp_id = call_host_v1(&build_host_request_json_with_policy_override(
        live_val,
        "Smart",
        1,
        Some(bad_override_id),
    ));
    assert_eq!(resp_id.outcome, "CapabilityMiss");
    assert_eq!(resp_id.failure_code.as_deref(), Some("policy_unsupported"));
}

#[test]
fn ffi_host_v1_any_player_potions_and_exhaustive_death_boundary() {
    // 1. TargetType::AnyPlayer potions round-trip via FFI with player target encoding.
    let mut live_potions = build_smoke_live_state_json();
    live_potions.player.potion_slots = vec![
        Some("POTION.BLOCK_POTION".to_owned()),
        Some("POTION.SHIP_IN_A_BOTTLE".to_owned()),
        Some("POTION.ATTACK_POTION".to_owned()),
    ];
    live_potions.creatures[1].current_hp = 26;
    live_potions.creatures[1].max_hp = 26;
    let growth = serde_json::json!({
        "handOfGreed": 0, "theHunt": 0, "feed": 0, "royalties": 0,
        "alchemize": 0, "geneticAlgorithm": 0, "theScythe": 0,
        "goopy": 0, "forbiddenGrimoire": 0, "madScience": 0, "showOff": 0,
        "thirdParty": null
    });
    let potion_policy = serde_json::json!({
        "potionPolicy": "Smart",
        "potionDirectives": [
            { "slot": 0, "potionId": "BLOCK_POTION", "directive": "Force" },
            { "slot": 1, "potionId": "SHIP_IN_A_BOTTLE", "directive": "Force" },
            { "slot": 2, "potionId": "ATTACK_POTION", "directive": "Force" }
        ],
        "predictPotionReward": false,
        "relicStrategyEnabled": true,
        "relicCounterRules": [],
        "growthBudgets": growth,
        "brightestFlameMaxHpLossLimit": null,
        "ignoreLongTermRewards": false,
        "actTransitionBossHpStrategy": "ProgressionFirst",
        "finalBossHpStrategy": "ProgressionFirst",
        "acceptableBattleHpLoss": 0,
        "stopAtAcceptableBattleHpLoss": true,
        "useBeamWidthPortfolio": false,
        "useNoveltyPortfolio": false,
        "useEarlyTurnExploration": false,
        "beamWidth": 32,
        "potionFreeBeamWidth": null,
        "potionBeamWidth": null,
        "maxExpandedNodes": 3000,
        "maxCardBranchesPerNode": 32,
        "maxPileChoiceBranchesPerAction": 18,
        "maxHandChoiceBranchesPerAction": 24,
        "softTimeBudgetMilliseconds": 5000,
        "theftPolicy": "Ignore"
    });
    let potion_req = build_host_request_json_with_policy_override(
        serde_json::to_value(&live_potions).unwrap(),
        "Smart",
        2,
        Some(potion_policy),
    );
    let potion_resp = call_host_v1(&potion_req);
    assert_eq!(potion_resp.outcome, "Succeeded");
    let potion_res = potion_resp.result.unwrap();
    assert!(potion_res.terminal.all_enemies_dead);
    let mut potion_ids: Vec<&str> = Vec::new();
    for a in &potion_res.actions {
        if a.kind == "UsePotion" {
            assert_eq!(a.target_index, -2);
            assert_eq!(a.target_combat_id, Some(0));
            potion_ids.push(a.potion_id.as_str());
        }
    }
    assert!(potion_ids.contains(&"BLOCK_POTION"));
    assert!(potion_ids.contains(&"SHIP_IN_A_BOTTLE"));
    assert!(potion_ids.contains(&"ATTACK_POTION"));

    // 2. Clean exhaustive death combat sets onlyDeathRoutes=true and searchBoundary="None".
    let content = Sts2Content;
    let run_rngs = RunRngSet::from_string_seed("FFI_DEATH_ONLY");
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc = CreatureHandle(1);
    let ec = CreatureHandle(2);
    let defend_meta = content.card_meta(CardId::DefendIronclad).unwrap();
    let mut cards = Vec::new();
    let mut deck = smallvec::SmallVec::new();
    for i in 0..5 {
        let h = CardHandle((i + 1) as u32);
        deck.push(h);
        cards.push(CardInst {
            handle: h,
            id: CardId::DefendIronclad,
            upgrade_level: 0,
            canonical_base_energy_cost: defend_meta.canonical_energy_cost,
            base_energy_cost: defend_meta.canonical_energy_cost,
            captured_energy_x_value: 0,
            base_replay_count: 0,
            floor_added_to_deck: Some(1),
            deck_card_handle: Some(h),
            clone_of_handle: None,
            temp_star_cost_override_this_turn: None,
            temp_cost_override_this_turn: None,
            temp_cost_override_this_turn_or_until_played: None,
            temp_cost_override_this_combat: None,
            local_keywords: defend_meta.base_keywords,
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
    let mut death_state = BattleState {
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
            transformation_pools: Vec::new(),
            map_col: 0,
            map_row: 0,
            act_index: 0,
            total_floor: 1,
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
        next_card_handle: 6,
        next_creature_handle: 3,
        next_power_handle: 1,
        next_attack_id: 1,
        run_rngs,
        player_rngs,
        player: PlayerInst {
            creature_handle: pc,
            current_hp: 5,
            max_hp: 80,
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
            hand: smallvec::SmallVec::new(),
            draw: smallvec::SmallVec::new(),
            discard: smallvec::SmallVec::new(),
            exhaust: smallvec::SmallVec::new(),
            play: smallvec::SmallVec::new(),
            deck,
            potion_slots: smallvec::smallvec![None, None, None],
            orb_capacity: 0,
            orbs: smallvec::SmallVec::new(),
            arrows: smallvec::SmallVec::new(),
            relics: smallvec::SmallVec::new(),
            card_or_potion_effect_depth: 0,
        },
        cards,
        card_extensions: Vec::new(),
        creatures: vec![
            CreatureInst {
                handle: pc,
                combat_id: Some(0),
                side: CombatSide::Player,
                slot_name: None,
                is_player: true,
                pet_owner_player_index: None,
                monster_id: None,
                current_hp: 5,
                max_hp: 80,
                block: 0,
                cumulative_hp_lost: 0,
                is_detached: false,
                monster: None,
                powers: smallvec::SmallVec::new(),
            },
            CreatureInst {
                handle: ec,
                combat_id: Some(1),
                side: CombatSide::Enemy,
                slot_name: Some("slot_0".to_owned()),
                is_player: false,
                pet_owner_player_index: None,
                monster_id: Some(MonsterId::FuzzyWurmCrawler),
                current_hp: 200,
                max_hp: 200,
                block: 0,
                cumulative_hp_lost: 0,
                is_detached: false,
                monster: Some(MonsterInst {
                    monster_id: MonsterId::FuzzyWurmCrawler,
                    rng: Rng::from_seed(77),
                    is_performing_move: false,
                    spawned_this_turn: false,
                    current_state_id: String::new(),
                    next_move_id: None,
                    next_move_can_transition_away: true,
                    performed_first_move: false,
                    state_log_ids: smallvec::SmallVec::new(),
                    transient_move_follow_up_state_id: None,
                    setup_fields: BTreeMap::new(),
                    private_fields: BTreeMap::new(),
                }),
                powers: smallvec::SmallVec::new(),
            },
        ],
        power_extensions: Vec::new(),
        ally_handles: smallvec::smallvec![pc],
        enemy_handles: smallvec::smallvec![ec],
        escaped_handles: smallvec::SmallVec::new(),
        removed_handles: smallvec::SmallVec::new(),
        spawned_enemy_handles: smallvec::smallvec![ec],
        pet_handles: smallvec::SmallVec::new(),
        damage_history: Vec::new(),
        semantic_history: SemanticHistory::default(),
        pending: None,
        vakuu_auto_select_depth: 0,
    };
    CombatEngine::start_combat(&mut death_state, &content).unwrap();
    let death_live = export_live_state(&death_state).unwrap();
    let death_policy = serde_json::json!({
        "potionPolicy": "Disabled",
        "potionDirectives": [],
        "predictPotionReward": false,
        "relicStrategyEnabled": true,
        "relicCounterRules": [],
        "growthBudgets": growth,
        "brightestFlameMaxHpLossLimit": null,
        "ignoreLongTermRewards": false,
        "actTransitionBossHpStrategy": "ProgressionFirst",
        "finalBossHpStrategy": "ProgressionFirst",
        "acceptableBattleHpLoss": 0,
        "stopAtAcceptableBattleHpLoss": true,
        "useBeamWidthPortfolio": false,
        "useNoveltyPortfolio": false,
        "useEarlyTurnExploration": false,
        "beamWidth": 32,
        "potionFreeBeamWidth": null,
        "potionBeamWidth": null,
        "maxExpandedNodes": 10000,
        "maxCardBranchesPerNode": 32,
        "maxPileChoiceBranchesPerAction": 18,
        "maxHandChoiceBranchesPerAction": 24,
        "softTimeBudgetMilliseconds": 5000,
        "theftPolicy": "Ignore"
    });
    let death_req = build_host_request_json_with_policy_override(
        serde_json::to_value(&death_live).unwrap(),
        "Disabled",
        1,
        Some(death_policy),
    );
    let death_resp = call_host_v1(&death_req);
    assert_eq!(death_resp.outcome, "Succeeded");
    let death_res = death_resp.result.unwrap();
    assert!(!death_res.terminal.all_enemies_dead);
    assert!(death_res.terminal.player_dead);
    assert!(death_res.terminal.only_death_routes);
    assert_eq!(death_res.terminal.search_boundary, "None");
    assert_eq!(death_res.terminal.confidence, "DeathOnly");
}
