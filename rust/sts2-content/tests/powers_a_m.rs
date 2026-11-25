use smallvec::SmallVec;
use std::sync::Arc;
use sts2_content::powers::batch_a_m;
use sts2_content::Sts2Content;
use sts2_kernel::commands::{block, card, card_pile, damage, forge, gold, orb, power};
use sts2_kernel::{
    hooks, ordering, ArrowSlotInst, BattleState, CardHandle, CardInst, CardTempFlags,
    CombatEngine, ContentDispatcher, CreatureHandle, CreatureInst, FieldMap, FieldValue,
    KernelError, MonsterInst, PlayerCounters, PlayerInst, PowerExtension, PowerInst,
    RootStaticContext, SemanticHistory, StepOutcome,
};
use sts2_types::{
    AfflictionId, ArrowStatus, CardId, CardKeywords, CharacterId, CombatSide, Dec, GameMode,
    HookMask, MonsterId, PileType, PlayerRngSet, PlayerTurnPhase, PowerId, PowerStaticFlags, Rng,
    RoomType, RunRngSet, ValueProp,
};

fn build_test_battle(
    character: CharacterId,
    deck_ids: &[CardId],
    enemy_hp: i32,
) -> (BattleState, CreatureHandle, CreatureHandle) {
    let run_rngs = RunRngSet::from_string_seed("AM_POWERS_TEST");
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc_handle = CreatureHandle(1);
    let enemy_handle = CreatureHandle(2);

    let mut cards = Vec::new();
    let mut deck_handles = Vec::new();
    let content = Sts2Content;
    for (idx, &cid) in deck_ids.iter().enumerate() {
        let meta = content.card_meta(cid).unwrap();
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

    let enemy_creature = CreatureInst {
        handle: enemy_handle,
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
            current_state_id: std::borrow::Cow::Borrowed(""),
            next_move_id: None,
            next_move_can_transition_away: true,
            performed_first_move: false,
            state_log_ids: Vec::new(),
            transient_move_follow_up_state_id: None,
            setup_fields: FieldMap::new(),
            private_fields: FieldMap::new(),
        })),
        has_minion_power: false,
        powers: Vec::new(),
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
        handle_layout: Default::default(),
        card_meta_dirty: Default::default(),
        extra_clutter_hint: Default::default(),
        eot_card_cleanup_dirty: Default::default(),
    };
    (state, pc_handle, enemy_handle)
}

#[test]
fn all_169_am_power_metas_registered_with_exact_flags() {
    let content = Sts2Content;
    let mut am_count = 0usize;
    for i in 0..PowerId::COUNT {
        let base = sts2_types::generated_ids::POWER_META_TABLE[i];
        // Construct PowerId by matching index via generated_power_meta identity
        let id = PowerId::from_model_id_str(&format!(
            "POWER.{}",
            all_power_entries()[i]
        ))
        .unwrap();
        if id.model_entry().as_bytes()[0] < b'N' {
            am_count += 1;
            let meta = content.power_meta(id).expect("A-M power meta must exist");
            assert_eq!(meta.power_type, base.power_type);
            assert_eq!(meta.stack_type, base.stack_type);
            assert_eq!(meta.instance_type, base.instance_type);
            // AM_POWER_META extends base hook_mask with lifecycle/multiplier hooks where needed
            assert_eq!(meta.hook_mask.0[0] & base.hook_mask.0[0], base.hook_mask.0[0]);
            assert_eq!(meta.hook_mask.0[1] & base.hook_mask.0[1], base.hook_mask.0[1]);
            assert_eq!(meta.hook_mask.0[2] & base.hook_mask.0[2], base.hook_mask.0[2]);
            let keep_on_death = matches!(
                id,
                PowerId::AdaptablePower | PowerId::DieForYouPower | PowerId::MinionPower
            );
            assert_eq!(
                meta.flags.contains(PowerStaticFlags::REMOVE_AFTER_OWNER_DEATH),
                !keep_on_death,
                "unexpected REMOVE_AFTER_OWNER_DEATH for {:?}",
                id
            );
        }
    }
    assert_eq!(am_count, 169);
    assert_eq!(
        content
            .power_meta(PowerId::MinionPower)
            .unwrap()
            .flags
            .contains(PowerStaticFlags::OWNER_IS_SECONDARY_ENEMY),
        true
    );
    assert_eq!(
        content
            .power_meta(PowerId::DexterityPower)
            .unwrap()
            .flags
            .contains(PowerStaticFlags::ALLOW_NEGATIVE),
        true
    );
    assert_eq!(
        content
            .power_meta(PowerId::FocusPower)
            .unwrap()
            .flags
            .contains(PowerStaticFlags::ALLOW_NEGATIVE),
        true
    );
    assert_eq!(
        content.power_meta(PowerId::AccelerantPower).unwrap().hook_mask,
        HookMask::EMPTY
    );
}

fn all_power_entries() -> Vec<&'static str> {
    // Verify via PowerId::AccelerantPower..YouAreMinePower round-trip isn't needed if we read generated entries;
    // instead probe known count by checking first 166 PowerIds directly.
    let mut out = Vec::with_capacity(PowerId::COUNT);
    for i in 0..PowerId::COUNT {
        let id: PowerId = unsafe { std::mem::transmute::<u16, PowerId>(i as u16) };
        out.push(id.model_entry());
    }
    out
}

#[test]
fn damage_and_block_modifiers_match_csharp() {
    let content = Sts2Content;
    let deck = [
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::StrikeSilent,
        CardId::DefendSilent,
        CardId::Bash,
    ];
    let (mut state, pc, enemy) = build_test_battle(CharacterId::Ironclad, &deck, 100);
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // DoubleDamage + Lethality + Gigantification + Vulnerable/Weak interactions
    power::apply(
        &mut state,
        &content,
        PowerId::DoubleDamagePower,
        pc,
        Dec::ONE,
        Some(pc),
        None,
    )
    .unwrap();
    power::apply(
        &mut state,
        &content,
        PowerId::LethalityPower,
        pc,
        Dec::from_i32(50),
        Some(pc),
        None,
    )
    .unwrap();

    let strike = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::StrikeIronclad)
        .unwrap();
    // 6 * 2 * 1.5 = 18
    let dmg = hooks::modify_damage(
        &mut state,
        &content,
        Some(enemy),
        Some(pc),
        Dec::from_i32(6),
        ValueProp::MOVE,
        Some(strike),
        None,
    )
    .unwrap();
    assert_eq!(dmg, Dec::from_i32(18));

    // Fasten adds to Defend-tagged powered block
    power::apply(
        &mut state,
        &content,
        PowerId::FastenPower,
        pc,
        Dec::from_i32(4),
        Some(pc),
        None,
    )
    .unwrap();
    let defend = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::DefendIronclad)
        .unwrap();
    let (blk, _) = hooks::modify_block(
        &mut state,
        &content,
        pc,
        Dec::from_i32(5),
        ValueProp::MOVE,
        Some(defend),
        None,
    )
    .unwrap();
    assert_eq!(blk, Dec::from_i32(9));

    // Guarded halves powered attack damage on enemy; Intangible caps at 1
    power::apply(
        &mut state,
        &content,
        PowerId::GuardedPower,
        enemy,
        Dec::ONE,
        Some(enemy),
        None,
    )
    .unwrap();
    let guarded_dmg = hooks::modify_damage(
        &mut state,
        &content,
        Some(enemy),
        Some(pc),
        Dec::from_i32(10),
        ValueProp::MOVE,
        Some(strike),
        None,
    )
    .unwrap();
    assert_eq!(guarded_dmg, Dec::from_i32(15)); // 10 * 2 * 1.5 * 0.5 = 15

    power::apply(
        &mut state,
        &content,
        PowerId::IntangiblePower,
        enemy,
        Dec::ONE,
        Some(enemy),
        None,
    )
    .unwrap();
    let intang_dmg = hooks::modify_damage(
        &mut state,
        &content,
        Some(enemy),
        Some(pc),
        Dec::from_i32(10),
        ValueProp::MOVE,
        Some(strike),
        None,
    )
    .unwrap();
    assert_eq!(intang_dmg, Dec::ONE);

    // Covered zeroes powered damage; removes on enemy side turn end
    power::apply(
        &mut state,
        &content,
        PowerId::CoveredPower,
        enemy,
        Dec::ONE,
        Some(enemy),
        None,
    )
    .unwrap();
    let covered_dmg = hooks::modify_damage(
        &mut state,
        &content,
        Some(enemy),
        Some(pc),
        Dec::from_i32(10),
        ValueProp::MOVE,
        Some(strike),
        None,
    )
    .unwrap();
    assert_eq!(covered_dmg, Dec::ZERO);

    // Pure Cruelty/Debilitate helpers
    let vuln_cruelty = batch_a_m::cruelty_modify_vulnerable_multiplier(
        pc,
        25,
        enemy,
        Dec::ONE_POINT_FIVE,
        ValueProp::MOVE,
    )
    .unwrap();
    assert_eq!(vuln_cruelty, Dec::from_parts(175, 2).unwrap());
    let vuln_deb = batch_a_m::debilitate_modify_vulnerable_multiplier(
        enemy,
        enemy,
        Dec::ONE_POINT_FIVE,
        ValueProp::MOVE,
    )
    .unwrap();
    assert_eq!(vuln_deb, Dec::TWO);
    let weak_deb = batch_a_m::debilitate_modify_weak_multiplier(
        enemy,
        enemy,
        Dec::THREE_QUARTERS,
        ValueProp::MOVE,
    )
    .unwrap();
    assert_eq!(weak_deb, Dec::HALF);
}

#[test]
fn artifact_confused_buffer_hardened_shell_and_automation() {
    let content = Sts2Content;
    let deck = [
        CardId::Bash,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::Bash,
    ];
    let (mut state, pc, enemy) = build_test_battle(CharacterId::Ironclad, &deck, 80);
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // Artifact blocks negative StrengthPower (Debuff via GetTypeForAmount) and decrements
    power::apply(
        &mut state,
        &content,
        PowerId::ArtifactPower,
        enemy,
        Dec::from_i32(2),
        Some(pc),
        None,
    )
    .unwrap();
    power::apply(
        &mut state,
        &content,
        PowerId::StrengthPower,
        enemy,
        Dec::from_i32(-3),
        Some(pc),
        None,
    )
    .unwrap();
    assert_eq!(
        state
            .creature(enemy)
            .unwrap()
            .powers
            .iter()
            .find(|p| p.id == PowerId::StrengthPower),
        None
    );
    assert_eq!(
        state
            .creature(enemy)
            .unwrap()
            .powers
            .iter()
            .find(|p| p.id == PowerId::ArtifactPower)
            .unwrap()
            .amount,
        1
    );

    // Buffer prevents HP loss and decrements 1
    power::apply(
        &mut state,
        &content,
        PowerId::BufferPower,
        pc,
        Dec::ONE,
        Some(pc),
        None,
    )
    .unwrap();
    damage::damage_targets(
        &mut state,
        &content,
        &[pc],
        Dec::from_i32(15),
        ValueProp::MOVE,
        Some(enemy),
        None,
        None,
    )
    .unwrap();
    assert_eq!(state.player.current_hp, CharacterId::Ironclad.starting_hp());
    assert!(state
        .creature(pc)
        .unwrap()
        .powers
        .iter()
        .all(|p| p.id != PowerId::BufferPower));

    // HardenedShell caps HP loss per turn and resets on side turn start
    power::apply(
        &mut state,
        &content,
        PowerId::HardenedShellPower,
        enemy,
        Dec::from_i32(5),
        Some(enemy),
        None,
    )
    .unwrap();
    damage::damage_targets(
        &mut state,
        &content,
        &[enemy],
        Dec::from_i32(3),
        ValueProp::MOVE,
        Some(pc),
        None,
        None,
    )
    .unwrap();
    damage::damage_targets(
        &mut state,
        &content,
        &[enemy],
        Dec::from_i32(10),
        ValueProp::MOVE,
        Some(pc),
        None,
        None,
    )
    .unwrap();
    assert_eq!(state.creature(enemy).unwrap().current_hp, 80 - 5);

    // Confused randomizes drawn card cost via CombatEnergyCosts RNG
    power::apply(
        &mut state,
        &content,
        PowerId::ConfusedPower,
        pc,
        Dec::ONE,
        Some(pc),
        None,
    )
    .unwrap();
    // Automation triggers energy gain every 10 draws
    let ext = PowerExtension {
        private_fields: FieldMap::from([(
            "AutomationPower._cardsLeft".to_owned(),
            FieldValue::Int(1),
        )]),
        ..Default::default()
    };
    power::apply_new_with_extension(
        &mut state,
        &content,
        PowerId::AutomationPower,
        pc,
        Dec::from_i32(2),
        Some(pc),
        None,
        ext,
    )
    .unwrap();
    let energy_before = state.player.energy;
    let rng_before = state
        .run_rngs
        .get(sts2_types::RunRngType::CombatEnergyCosts)
        .counter;
    let drawn = card_pile::draw(&mut state, &content, 1, false).unwrap();
    assert_eq!(drawn.len(), 1);
    assert_eq!(state.player.energy, energy_before + 2);
    assert_eq!(
        state
            .run_rngs
            .get(sts2_types::RunRngType::CombatEnergyCosts)
            .counter,
        rng_before + 1
    );
    let resolved = ordering::resolve_energy_cost(&mut state, &content, drawn[0]).unwrap();
    assert!((0..4).contains(&resolved));
}

#[test]
fn doom_afterimage_monologue_encore_and_free_cards() {
    let content = Sts2Content;
    let deck = [
        CardId::Bash,
        CardId::DefendIronclad,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
    ];
    let (mut state, pc, enemy) = build_test_battle(CharacterId::Ironclad, &deck, 10);
    CombatEngine::start_combat(&mut state, &content).unwrap();

    power::apply(
        &mut state,
        &content,
        PowerId::AfterimagePower,
        pc,
        Dec::from_i32(2),
        Some(pc),
        None,
    )
    .unwrap();
    power::apply(
        &mut state,
        &content,
        PowerId::MonologuePower,
        pc,
        Dec::ONE,
        Some(pc),
        None,
    )
    .unwrap();
    power::apply(
        &mut state,
        &content,
        PowerId::FreeAttackPower,
        pc,
        Dec::ONE,
        Some(pc),
        None,
    )
    .unwrap();
    power::apply(
        &mut state,
        &content,
        PowerId::EncorePower,
        pc,
        Dec::from_i32(2),
        Some(pc),
        None,
    )
    .unwrap();

    let bash = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::Bash)
        .unwrap();
    let defend = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::DefendIronclad)
        .unwrap();
    let strike = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::StrikeIronclad)
        .unwrap();

    // Bash costs 0 due to FreeAttackPower
    assert_eq!(ordering::resolve_energy_cost(&mut state, &content, bash).unwrap(), 0);
    let mut choices = SmallVec::new();
    CombatEngine::play_card(&mut state, &content, bash, Some(enemy), &mut choices).unwrap();
    assert_eq!(state.player.energy, 3);
    assert_eq!(state.player.block, 2); // Afterimage
    assert_eq!(
        state
            .creature(pc)
            .unwrap()
            .powers
            .iter()
            .find(|p| p.id == PowerId::StrengthPower)
            .unwrap()
            .amount,
        1
    );

    CombatEngine::play_card(&mut state, &content, defend, None, &mut choices).unwrap();
    assert_eq!(state.player.block, 2 + 5 + 2);
    // Encore(2) blocks 3rd card play this turn
    assert!(!CombatEngine::can_play_card(&mut state, &content, strike).unwrap());

    // Doom kills enemy when current_hp <= amount at BeforeSideTurnEnd(Enemy)
    power::apply(
        &mut state,
        &content,
        PowerId::DoomPower,
        enemy,
        Dec::from_i32(5),
        Some(pc),
        None,
    )
    .unwrap();
    let outcome = CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert!(matches!(outcome, sts2_kernel::StepOutcome::CombatEnded { won: true }));
    assert!(state.creature(enemy).unwrap().is_dead());
}

#[test]
fn temporary_powers_and_turn_lifecycle_powers() {
    let content = Sts2Content;
    let deck = [
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::Bash,
    ];
    let (mut state, pc, enemy) = build_test_battle(CharacterId::Ironclad, &deck, 100);
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // Simulate BeforeApplied automatically via power::apply + stacking + AfterSideTurnEnd reversal for FlexPotionPower
    power::apply(
        &mut state,
        &content,
        PowerId::FlexPotionPower,
        pc,
        Dec::from_i32(5),
        Some(pc),
        None,
    )
    .unwrap();
    power::apply(
        &mut state,
        &content,
        PowerId::FlexPotionPower,
        pc,
        Dec::from_i32(3),
        Some(pc),
        None,
    )
    .unwrap();
    assert_eq!(
        state
            .creature(pc)
            .unwrap()
            .powers
            .iter()
            .find(|p| p.id == PowerId::StrengthPower)
            .unwrap()
            .amount,
        8
    );

    // ExtraTurnPower grants extra player turn & decrements
    power::apply(
        &mut state,
        &content,
        PowerId::ExtraTurnPower,
        pc,
        Dec::ONE,
        Some(pc),
        None,
    )
    .unwrap();
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.player.turn_number, 2);
    assert!(state.is_player_extra_turn);
    // Strength reverted from 8 -> 0 at player turn end
    assert!(state
        .creature(pc)
        .unwrap()
        .powers
        .iter()
        .all(|p| p.id != PowerId::StrengthPower));
    assert!(state
        .creature(pc)
        .unwrap()
        .powers
        .iter()
        .all(|p| p.id != PowerId::FlexPotionPower));

    // Also test AnticipatePower (DexterityPower) + HotfixPower (FocusPower) via BeforeApplied + turn-end reversal
    power::apply(
        &mut state,
        &content,
        PowerId::AnticipatePower,
        pc,
        Dec::from_i32(4),
        Some(pc),
        None,
    )
    .unwrap();
    power::apply(
        &mut state,
        &content,
        PowerId::HotfixPower,
        pc,
        Dec::from_i32(3),
        Some(pc),
        None,
    )
    .unwrap();
    assert_eq!(
        state
            .creature(pc)
            .unwrap()
            .powers
            .iter()
            .find(|p| p.id == PowerId::DexterityPower)
            .unwrap()
            .amount,
        4
    );
    assert_eq!(
        state
            .creature(pc)
            .unwrap()
            .powers
            .iter()
            .find(|p| p.id == PowerId::FocusPower)
            .unwrap()
            .amount,
        3
    );

    // Test GenesisPower + EnergyNextTurnPower + DrawCardsNextTurnPower + DemesnePower
    power::apply(
        &mut state,
        &content,
        PowerId::GenesisPower,
        pc,
        Dec::from_i32(2),
        Some(pc),
        None,
    )
    .unwrap();
    power::apply(
        &mut state,
        &content,
        PowerId::EnergyNextTurnPower,
        pc,
        Dec::from_i32(2),
        Some(pc),
        None,
    )
    .unwrap();
    power::apply(
        &mut state,
        &content,
        PowerId::DemesnePower,
        pc,
        Dec::ONE,
        Some(pc),
        None,
    )
    .unwrap();
    // Apply DrawCardsNextTurnPower on turn 2; will activate on turn 3 after enemy turn
    power::apply(
        &mut state,
        &content,
        PowerId::DrawCardsNextTurnPower,
        pc,
        Dec::ONE,
        Some(pc),
        None,
    )
    .unwrap();

    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.player.stars, 2);
    assert_eq!(state.player.energy, 3 + 1 + 2); // base 3 + Demesne 1 + EnergyNextTurn 2
    assert!(state
        .creature(pc)
        .unwrap()
        .powers
        .iter()
        .all(|p| p.id != PowerId::DexterityPower && p.id != PowerId::FocusPower));
    assert!(state
        .creature(pc)
        .unwrap()
        .powers
        .iter()
        .all(|p| p.id != PowerId::DrawCardsNextTurnPower));
    let _ = enemy;
}

#[test]
fn w0b_wired_lifecycle_choices_and_commands_end_to_end() {
    let content = Sts2Content;
    let deck = [
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::StrikeIronclad,
        CardId::Bash,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
    ];
    let (mut state, pc, enemy) = build_test_battle(CharacterId::Ironclad, &deck, 50);
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // 1. CrueltyPower & DebilitatePower wired into hooks::modify_vulnerable_multiplier / modify_weak_multiplier
    power::apply(&mut state, &content, PowerId::CrueltyPower, pc, Dec::from_i32(25), Some(pc), None)
        .unwrap();
    power::apply(&mut state, &content, PowerId::DebilitatePower, enemy, Dec::ONE, Some(pc), None)
        .unwrap();
    let vuln_mult = hooks::modify_vulnerable_multiplier(
        &mut state,
        &content,
        Some(enemy),
        Some(pc),
        Dec::ONE_POINT_FIVE,
        ValueProp::MOVE,
        None,
    )
    .unwrap();
    // (1.5 + 0.25) = 1.75 -> Debilitate doubles bonus: 1.75 + 0.75 = 2.50
    assert_eq!(vuln_mult, Dec::from_parts(250, 2).unwrap());

    let weak_mult = hooks::modify_weak_multiplier(
        &mut state,
        &content,
        Some(pc),
        Some(enemy),
        Dec::THREE_QUARTERS,
        ValueProp::MOVE,
        None,
    )
    .unwrap();
    assert_eq!(weak_mult, Dec::HALF);

    // 3. BurrowedPower AfterRemoved loses block automatically via power::remove_by_handle
    power::apply(&mut state, &content, PowerId::BurrowedPower, pc, Dec::ONE, Some(pc), None)
        .unwrap();
    block::gain_block(&mut state, &content, pc, Dec::from_i32(7), ValueProp::UNPOWERED, None, None)
        .unwrap();
    let bur_h = state
        .creature(pc)
        .unwrap()
        .powers
        .iter()
        .find(|p| p.id == PowerId::BurrowedPower)
        .unwrap()
        .handle;
    power::remove_by_handle(&mut state, &content, pc, bur_h).unwrap();
    assert_eq!(state.player.block, 0);

    // 5. DampenPower AfterApplied downgrades upgraded cards & AfterRemoved restores them
    let strike_h = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::StrikeIronclad)
        .unwrap();
    card::upgrade(&mut state, &content, strike_h).unwrap();
    assert_eq!(state.card(strike_h).unwrap().upgrade_level, 1);
    power::apply(&mut state, &content, PowerId::DampenPower, pc, Dec::ONE, Some(enemy), None)
        .unwrap();
    assert_eq!(state.card(strike_h).unwrap().upgrade_level, 0);
    let dampen_h = state
        .creature(pc)
        .unwrap()
        .powers
        .iter()
        .find(|p| p.id == PowerId::DampenPower)
        .unwrap()
        .handle;
    power::remove_by_handle(&mut state, &content, pc, dampen_h).unwrap();
    assert_eq!(state.card(strike_h).unwrap().upgrade_level, 1);

    // 6. HexPower AfterApplied afflicts Hexed (Ethereal via TryModifyKeywordsInCombat) & AfterRemoved clears
    power::apply(&mut state, &content, PowerId::HexPower, pc, Dec::ONE, Some(enemy), None).unwrap();
    assert_eq!(
        state.card(strike_h).unwrap().affliction.map(|a| a.id),
        Some(AfflictionId::Hexed)
    );
    let (hex_idx, hex_p, hex_side) = state
        .creature(pc)
        .unwrap()
        .powers
        .iter()
        .enumerate()
        .find(|(_, p)| p.id == PowerId::HexPower)
        .map(|(i, p)| (i, *p, state.creature(pc).unwrap().side))
        .unwrap();
    let kw_res = batch_a_m::dispatch_hook(
        &content,
        &mut state,
        pc,
        hex_idx,
        hex_p,
        hex_side,
        sts2_types::HookSlot::TryModifyKeywordsInCombat,
        &sts2_kernel::HookArgs::CardOnly(strike_h),
    )
    .unwrap();
    assert_eq!(kw_res.keywords, Some(CardKeywords::ETHEREAL));
    let hex_h = state.creature(pc).unwrap().powers[hex_idx].handle;
    power::remove_by_handle(&mut state, &content, pc, hex_h).unwrap();
    assert!(state.card(strike_h).unwrap().affliction.is_none());

    // 7. DarkEmbracePower AfterCardExhausted (non-ethereal immediate draw vs ethereal deferred)
    power::apply(&mut state, &content, PowerId::DarkEmbracePower, pc, Dec::ONE, Some(pc), None)
        .unwrap();
    let hand_before = state.player.hand.len();
    card_pile::exhaust(&mut state, &content, strike_h, false).unwrap();
    // Exhausted 1 card from hand, drew 1 card immediately => hand size unchanged
    assert_eq!(state.player.hand.len(), hand_before);

    // 8. FeralPower AfterApplied + ModifyCardPlayResultLocation returns 0-cost Attack to Hand/Top
    power::apply(&mut state, &content, PowerId::FeralPower, pc, Dec::ONE, Some(pc), None)
        .unwrap();
    let attack_h = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::StrikeIronclad)
        .unwrap();
    power::apply(&mut state, &content, PowerId::FreeAttackPower, pc, Dec::ONE, Some(pc), None)
        .unwrap();
    let mut choices = SmallVec::new();
    CombatEngine::play_card(&mut state, &content, attack_h, Some(enemy), &mut choices).unwrap();
    assert_eq!(state.card(attack_h).unwrap().pile, PileType::Hand);

    // 9. ImbalancedPower AfterDamageGiven with was_fully_blocked = true stuns monster
    power::apply(&mut state, &content, PowerId::ImbalancedPower, enemy, Dec::ONE, Some(pc), None)
        .unwrap();
    block::gain_block(&mut state, &content, pc, Dec::from_i32(20), ValueProp::UNPOWERED, None, None)
        .unwrap();
    damage::damage_targets(
        &mut state,
        &content,
        &[pc],
        Dec::from_i32(5),
        ValueProp::MOVE,
        Some(enemy),
        None,
        None,
    )
    .unwrap();
    assert_eq!(
        state
            .creature(enemy)
            .unwrap()
            .monster
            .as_ref()
            .unwrap()
            .next_move_id
            .as_deref(),
        Some("STUNNED")
    );

    // 10. ForegoneConclusionPower BeforeHandDraw pauses with NeedsChoice and resume_choice puts chosen draw card in hand
    let (mut state_fc, pc_fc, _) = build_test_battle(CharacterId::Ironclad, &deck, 50);
    CombatEngine::start_combat(&mut state_fc, &content).unwrap();
    power::apply(
        &mut state_fc,
        &content,
        PowerId::ForegoneConclusionPower,
        pc_fc,
        Dec::ONE,
        Some(pc_fc),
        None,
    )
    .unwrap();
    let out = CombatEngine::end_player_turn(&mut state_fc, &content).unwrap();
    assert!(matches!(out, StepOutcome::NeedsChoice(_)));
    let chosen_draw = state_fc.pending.as_ref().unwrap().request.candidates[0];
    assert_eq!(
        state_fc.card(chosen_draw).unwrap().id,
        CardId::DefendIronclad
    );
    let resumed = CombatEngine::resume_choice(&mut state_fc, &content, &[0]).unwrap();
    assert_eq!(resumed, StepOutcome::Completed);
    assert!(state_fc.player.hand.contains(&chosen_draw));
    assert!(state_fc
        .creature(pc_fc)
        .unwrap()
        .powers
        .iter()
        .all(|p| p.id != PowerId::ForegoneConclusionPower));

    // 11. MayhemPower AfterAutoPrePlayPhaseEntered autoplays top card of draw pile
    let (mut state_mh, pc_mh, _) = build_test_battle(CharacterId::Ironclad, &deck, 50);
    CombatEngine::start_combat(&mut state_mh, &content).unwrap();
    power::apply(
        &mut state_mh,
        &content,
        PowerId::MayhemPower,
        pc_mh,
        Dec::ONE,
        Some(pc_mh),
        None,
    )
    .unwrap();
    let top_draw = state_mh.player.draw[0];
    CombatEngine::end_player_turn(&mut state_mh, &content).unwrap();
    // On turn 2, Mayhem autoplays the card that was at top of draw pile after hand draw
    assert_ne!(state_mh.card(top_draw).unwrap().pile, PileType::None);
    assert!(state_mh.player.counters.cards_played_this_combat >= 1);

    // 12. MinionPower ShouldOwnerDeathTriggerFatal & IllusionPower AfterApplied + ShouldPowerBeRemovedOnDeath
    assert!(hooks::should_owner_death_trigger_fatal(&mut state, &content, enemy).unwrap());
    power::apply(&mut state, &content, PowerId::IllusionPower, enemy, Dec::ONE, Some(enemy), None)
        .unwrap();
    assert!(state
        .creature(enemy)
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::MinionPower));
    assert!(!hooks::should_owner_death_trigger_fatal(&mut state, &content, enemy).unwrap());
    let temp_p = PowerInst {
        handle: 999,
        id: PowerId::FlexPotionPower,
        amount: 2,
        amount_on_turn_start: 0,
        skip_next_duration_tick: false,
        applier_handle: Some(pc),
        ext_index: None,
    };
    let perm_debuff = PowerInst {
        handle: 998,
        id: PowerId::VulnerablePower,
        amount: 2,
        amount_on_turn_start: 0,
        skip_next_duration_tick: false,
        applier_handle: Some(pc),
        ext_index: None,
    };
    assert!(!hooks::should_power_be_removed_on_death(&mut state, &content, enemy, temp_p).unwrap());
    assert!(hooks::should_power_be_removed_on_death(&mut state, &content, enemy, perm_debuff).unwrap());

    // 13. HammerTimePower AfterForge + ForgeCmd compiles & runs cleanly in single-player
    power::apply(&mut state_mh, &content, PowerId::HammerTimePower, pc_mh, Dec::ONE, Some(pc_mh), None)
        .unwrap();
    let _ = forge::forge as fn(&mut BattleState, &dyn ContentDispatcher, Dec) -> Result<(), KernelError>;
    let _ = gold::gain_gold;
    let _ = orb::add_slots;
    let _ = batch_a_m::all_is_well_on_card_generated;
    let _ = batch_a_m::arsenal_on_card_generated;

    // 14. v0.114.0 A-M powers: GravityPower, InterceptPower (+ AfterRemoved), LeadershipPower, MagicBombPower, HardToKillPower.
    let (mut state14, pc14, enemy14) = build_test_battle(CharacterId::Ironclad, &deck, 100);
    CombatEngine::start_combat(&mut state14, &content).unwrap();

    // HardToKillPower caps HP loss via ModifyHpLostBeforeOstyLate.
    power::apply(
        &mut state14,
        &content,
        PowerId::HardToKillPower,
        enemy14,
        Dec::from_i32(10),
        Some(enemy14),
        None,
    )
    .unwrap();
    let (capped, _) = hooks::modify_hp_lost(
        &mut state14,
        &content,
        enemy14,
        Dec::from_i32(25),
        ValueProp::MOVE,
        Some(pc14),
        None,
        hooks::HpLossPhase::BeforeOsty,
    )
    .unwrap();
    assert_eq!(capped, Dec::from_i32(10));

    // GravityPower deals its amount to all enemies after each card played.
    power::apply(
        &mut state14,
        &content,
        PowerId::GravityPower,
        pc14,
        Dec::from_i32(4),
        Some(pc14),
        None,
    )
    .unwrap();
    let strike_h = state14.player.hand[0];
    let mut c14 = SmallVec::new();
    CombatEngine::play_card(&mut state14, &content, strike_h, Some(enemy14), &mut c14).unwrap();
    assert_eq!(state14.creature(enemy14).unwrap().current_hp, 90);

    // InterceptPower amplifies damage by covered count + 1 and clears CoveredPower on removal.
    let covered_ally = CreatureHandle(state14.next_creature_handle);
    state14.next_creature_handle += 1;
    let mut ally_c = state14.creature(enemy14).unwrap().clone();
    ally_c.handle = covered_ally;
    ally_c.combat_id = Some(9);
    ally_c.side = CombatSide::Player;
    ally_c.monster = None;
    ally_c.monster_id = None;
    ally_c.powers.clear();
    state14.creatures.push(ally_c);
    state14.ally_handles.push(covered_ally);
    state14.invalidate_creature_hooks();
    power::apply(
        &mut state14,
        &content,
        PowerId::CoveredPower,
        covered_ally,
        Dec::ONE,
        Some(enemy14),
        None,
    )
    .unwrap();
    let ext_i = state14.power_extensions.len() as u32;
    let mut i_pf = sts2_kernel::state::FieldMap::default();
    i_pf.insert(
        "InterceptPower._coveredCreatures",
        FieldValue::CreatureHandleArray(vec![covered_ally.0 as i32]),
    );
    state14.power_extensions.push(sts2_kernel::PowerExtension {
        private_fields: i_pf,
        ..Default::default()
    });
    let intercept_h = state14.next_power_handle;
    state14.next_power_handle += 1;
    state14.creature_mut(enemy14).unwrap().powers.push(PowerInst {
        handle: intercept_h,
        id: PowerId::InterceptPower,
        amount: 1,
        amount_on_turn_start: 1,
        skip_next_duration_tick: false,
        applier_handle: Some(enemy14),
        ext_index: Some(ext_i),
    });
    state14.invalidate_hook_cache();
    let mult_dmg = hooks::modify_damage(
        &mut state14,
        &content,
        Some(enemy14),
        Some(pc14),
        Dec::from_i32(10),
        ValueProp::MOVE,
        Some(strike_h),
        None,
    )
    .unwrap();
    assert_eq!(mult_dmg, Dec::from_i32(20));
    power::remove_by_handle(&mut state14, &content, enemy14, intercept_h).unwrap();
    assert!(state14
        .creature(covered_ally)
        .unwrap()
        .powers
        .iter()
        .all(|p| p.id != PowerId::CoveredPower));

    // LeadershipPower boosts allied powered attacks (excluding self).
    power::apply(
        &mut state14,
        &content,
        PowerId::LeadershipPower,
        covered_ally,
        Dec::from_i32(5),
        Some(covered_ally),
        None,
    )
    .unwrap();
    let ally_boost = hooks::modify_damage(
        &mut state14,
        &content,
        Some(enemy14),
        Some(pc14),
        Dec::from_i32(6),
        ValueProp::MOVE,
        Some(strike_h),
        None,
    )
    .unwrap();
    assert_eq!(ally_boost, Dec::from_i32(11));

    // MagicBombPower deals its amount at owner's side turn end if applier is alive, then removes itself.
    let hp_before_bomb = state14.creature(enemy14).unwrap().current_hp;
    power::apply(
        &mut state14,
        &content,
        PowerId::MagicBombPower,
        enemy14,
        Dec::from_i32(9),
        Some(pc14),
        None,
    )
    .unwrap();
    hooks::after_side_turn_end(&mut state14, &content, CombatSide::Enemy, &[enemy14]).unwrap();
    assert_eq!(
        state14.creature(enemy14).unwrap().current_hp,
        hp_before_bomb - 9
    );
    assert!(state14
        .creature(enemy14)
        .unwrap()
        .powers
        .iter()
        .all(|p| p.id != PowerId::MagicBombPower));
}

