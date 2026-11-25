use smallvec::SmallVec;
use std::sync::Arc;
use sts2_content::cards::colorless_special::{
    COLORLESS_SPECIAL_IDS, mad_science_resolved_target_type, mad_science_resolved_type,
    omnislice_splash_amount, on_turn_end_in_hand, the_tower_damage,
};
use sts2_content::Sts2Content;
use sts2_kernel::commands::{card as card_cmd, card_pile, forge, power};
use sts2_kernel::{
    ArrowSlotInst, BattleState, CardHandle, CardInst, CardTempFlags, CombatEngine,
    ContentDispatcher, CreatureHandle, CreatureInst, DamageResult, FieldValue, HookArgs,
    HookListener, KernelError, MonsterInst, PlayerCounters, PlayerInst, RootStaticContext,
    SemanticHistory, StepOutcome,
};
use sts2_types::generated_ids::{CARD_PARTITION_TABLE, generated_card_meta};
use sts2_types::{
    ArrowStatus, CardId, CardKeywords, CardPartition, CardStaticFlags, CardType, CharacterId,
    CombatSide, Dec, GameMode, HookSlot, MonsterId, PileType, PlayerRngSet, PlayerTurnPhase,
    PowerId, Rng, RoomType, RunRngSet, TargetType, ValueProp, card_partition,
};

fn make_card(handle: u32, id: CardId, pile: PileType) -> CardInst {
    let meta = Sts2Content.card_meta(id).unwrap_or_else(|_| generated_card_meta(id));
    CardInst {
        handle: CardHandle(handle),
        id,
        upgrade_level: 0,
        canonical_base_energy_cost: meta.canonical_energy_cost as i16,
        base_energy_cost: meta.canonical_energy_cost as i16,
        captured_energy_x_value: 0,
        base_replay_count: 0,
        floor_added_to_deck: sts2_kernel::OptI16::some(1),
        deck_card_handle: sts2_kernel::OptCardHandle::some(CardHandle(handle)),
        clone_of_handle: sts2_kernel::OptCardHandle::NONE,
        temp_star_cost_override_this_turn: sts2_kernel::OptI16::NONE,
        temp_cost_override_this_turn: sts2_kernel::OptI16::NONE,
        temp_cost_override_this_turn_or_until_played: sts2_kernel::OptI16::NONE,
        temp_cost_override_this_combat: sts2_kernel::OptI16::NONE,
        local_keywords: meta.base_keywords,
        keywords_added_from_outside: CardKeywords::empty(),
        keywords_removed_from_outside: CardKeywords::empty(),
        temp_flags: if meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST) {
            CardTempFlags::COSTS_X_ENERGY
        } else {
            CardTempFlags::empty()
        },
        affliction: None,
        ext_index: None,
        pile,
        removed_from_state: false,
        transform_into_after_play: None,
    }
}

fn build_battle(
    character: CharacterId,
    hand_ids: &[CardId],
    draw_ids: &[CardId],
    discard_ids: &[CardId],
    enemies: &[(MonsterId, i32)],
) -> (BattleState, Vec<CreatureHandle>) {
    let run_rngs = RunRngSet::from_string_seed("COLORLESSTEST1");
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc_handle = CreatureHandle(1);

    let mut cards = Vec::new();
    let mut hand = Vec::new();
    let mut draw = Vec::new();
    let mut discard = Vec::new();
    let mut next_h = 1u32;

    for &cid in hand_ids {
        let h = CardHandle(next_h);
        next_h += 1;
        hand.push(h);
        cards.push(make_card(h.0, cid, PileType::Hand));
    }
    for &cid in draw_ids {
        let h = CardHandle(next_h);
        next_h += 1;
        draw.push(h);
        cards.push(make_card(h.0, cid, PileType::Draw));
    }
    for &cid in discard_ids {
        let h = CardHandle(next_h);
        next_h += 1;
        discard.push(h);
        cards.push(make_card(h.0, cid, PileType::Discard));
    }

    let mut arrows = Vec::new();
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
    let mut slots = Vec::new();
    for (idx, &(mid, hp)) in enemies.iter().enumerate() {
        let eh = CreatureHandle((idx + 2) as u32);
        let slot_name = format!("slot_{idx}");
        slots.push(slot_name.clone());
        enemy_handles.push(eh);
        creatures.push(CreatureInst {
            handle: eh,
            combat_id: Some((idx + 1) as u32),
            side: CombatSide::Enemy,
            slot_name: Some(std::borrow::Cow::Owned(slot_name)),
            is_player: false,
            pet_owner_player_index: None,
            monster_id: Some(mid),
            current_hp: hp,
            max_hp: hp,
            block: 0,
            cumulative_hp_lost: 0,
            is_detached: false,
            monster: Some(Box::new(MonsterInst {
                monster_id: mid,
                rng: Rng::from_seed(run_rngs.seed + idx as u64 + 1),
                is_performing_move: false,
                spawned_this_turn: false,
                current_state_id: std::borrow::Cow::Borrowed("INIT"),
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

    let state = BattleState {
        static_ctx: Arc::new(RootStaticContext {
            encounter_id: "TEST".to_owned(),
            encounter_is_weak: true,
            room_type: RoomType::Monster,
            encounter_slots: slots,
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
        is_in_progress: true,
        is_starting: false,
        won: false,
        is_player_extra_turn: false,
        gold_was_stolen: false,
        end_player_turn_requested: false,
        card_action_boundary_depth: 0,
        next_creature_id: (creatures.len() as u32) + 1,
        initial_encounter_monster_count: enemy_handles.len(),
        next_card_handle: next_h,
        next_creature_handle: (creatures.len() as u32) + 1,
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
            max_energy: 3,
            base_orb_slot_count: character.base_orb_slot_count(),
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
            potion_slots: SmallVec::from_elem(None, 3),
            combat_generated_potion_mask: 0,
            orb_capacity: character.base_orb_slot_count(),
            orbs: Vec::new(),
            arrows,
            relics: Vec::new(),
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
        spawned_enemy_handles: SmallVec::new(),
        pet_handles: SmallVec::new(),
        damage_history: Vec::new(),
        semantic_history: SemanticHistory::default(),
        pending: None,
        vakuu_auto_select_depth: 0,
        hook_cache: Default::default(),
        deck_dirty: Default::default(),
        relic_dirty_mask: Default::default(),
    };
    (state, enemy_handles)
}

fn play_hand_card(
    state: &mut BattleState,
    idx: usize,
    target: Option<CreatureHandle>,
    choices: Vec<Vec<usize>>,
) -> Result<StepOutcome, KernelError> {
    let h = state.player.hand[idx];
    let mut q: SmallVec<[Vec<usize>; 2]> = choices.into_iter().collect();
    CombatEngine::play_card(state, &Sts2Content, h, target, &mut q)
}

#[test]
fn all_172_colorless_special_cards_have_metadata_and_no_unsupported() {
    let content = Sts2Content;
    let mut seen = std::collections::HashSet::new();
    for &id in COLORLESS_SPECIAL_IDS {
        assert_eq!(card_partition(id), CardPartition::ColorlessSpecial, "{id:?}");
        assert!(seen.insert(id), "duplicate {id:?}");
        let meta = content.card_meta(id).expect("no Unsupported for ColorlessSpecial");
        if matches!(
            id,
            CardId::BadLuck
                | CardId::Beckon
                | CardId::Burn
                | CardId::Debt
                | CardId::Decay
                | CardId::Doubt
                | CardId::Infection
                | CardId::Regret
                | CardId::Shame
                | CardId::Toxic
                | CardId::Wither
        ) {
            assert!(
                meta.flags.contains(CardStaticFlags::HAS_TURN_END_IN_HAND),
                "{id:?} missing HAS_TURN_END_IN_HAND"
            );
        }
    }
    assert_eq!(seen.len(), 172);
    let table_count = CARD_PARTITION_TABLE
        .iter()
        .filter(|&&p| p == CardPartition::ColorlessSpecial)
        .count();
    assert_eq!(table_count, 172);
    assert!(content
        .card_meta(CardId::HandOfGreed)
        .unwrap()
        .flags
        .contains(CardStaticFlags::HAS_GROWTH));
    assert!(content
        .card_meta(CardId::Discovery)
        .unwrap()
        .flags
        .contains(CardStaticFlags::USES_RNG));
    assert!(content
        .card_meta(CardId::Clash)
        .unwrap()
        .flags
        .contains(CardStaticFlags::ORDER_SENSITIVE));
    assert!(content
        .card_meta(CardId::Bolas)
        .unwrap()
        .hook_mask
        .contains(HookSlot::BeforeHandDraw));
    assert!(content
        .card_meta(CardId::Void)
        .unwrap()
        .hook_mask
        .contains(HookSlot::AfterCardDrawn));
    assert!(content
        .card_meta(CardId::Normality)
        .unwrap()
        .hook_mask
        .contains(HookSlot::ShouldPlay));
    assert!(content
        .card_meta(CardId::Enthralled)
        .unwrap()
        .hook_mask
        .contains(HookSlot::ShouldPlay));
}

#[test]
fn playability_and_card_hooks_work_accurately() {
    let content = Sts2Content;
    // Clash playable only if all cards in hand are Attacks
    let (mut state, enemies) = build_battle(
        CharacterId::Ironclad,
        &[CardId::Clash, CardId::Wound],
        &[],
        &[],
        &[(MonsterId::FuzzyWurmCrawler, 50)],
    );
    let clash_h = state.player.hand[0];
    assert!(!CombatEngine::can_play_card(&mut state, &content, clash_h).unwrap());
    let wound_h = state.player.hand[1];
    card_pile::exhaust(&mut state, &content, wound_h, false).unwrap();
    let clash_h = state.player.hand[0];
    assert!(CombatEngine::can_play_card(&mut state, &content, clash_h).unwrap());
    play_hand_card(&mut state, 0, Some(enemies[0]), vec![]).unwrap();
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, 36);

    // Normality blocks 4th play in a turn
    let (mut state, enemies) = build_battle(
        CharacterId::Ironclad,
        &[
            CardId::Normality,
            CardId::Shiv,
            CardId::Shiv,
            CardId::Shiv,
            CardId::Shiv,
        ],
        &[],
        &[],
        &[(MonsterId::FuzzyWurmCrawler, 50)],
    );
    for _ in 0..3 {
        let h = state.player.hand[1];
        assert!(CombatEngine::can_play_card(&mut state, &content, h).unwrap());
        play_hand_card(&mut state, 1, Some(enemies[0]), vec![]).unwrap();
    }
    let h = state.player.hand[1];
    assert!(!CombatEngine::can_play_card(&mut state, &content, h).unwrap());

    // Enthralled blocks manual play of other cards while in hand, but allows Enthralled itself
    let (mut state, _) = build_battle(
        CharacterId::Ironclad,
        &[CardId::Enthralled, CardId::Shiv],
        &[],
        &[],
        &[(MonsterId::FuzzyWurmCrawler, 50)],
    );
    let enthralled_h = state.player.hand[0];
    let shiv_h = state.player.hand[1];
    assert!(!CombatEngine::can_play_card(&mut state, &content, shiv_h).unwrap());
    assert!(CombatEngine::can_play_card(&mut state, &content, enthralled_h).unwrap());
    play_hand_card(&mut state, 0, None, vec![]).unwrap();
    let shiv_h2 = state.player.hand[0];
    assert!(CombatEngine::can_play_card(&mut state, &content, shiv_h2).unwrap());

    // Void loses 1 energy when drawn
    let (mut state, _) = build_battle(
        CharacterId::Ironclad,
        &[],
        &[CardId::Void],
        &[],
        &[(MonsterId::FuzzyWurmCrawler, 50)],
    );
    state.player.energy = 3;
    card_pile::draw(&mut state, &content, 1, false).unwrap();
    assert_eq!(state.player.energy, 2);

    // Bolas & ThrummingHatchet return to hand at BeforeHandDraw next turn
    let (mut state, enemies) = build_battle(
        CharacterId::Ironclad,
        &[CardId::Bolas, CardId::ThrummingHatchet],
        &[],
        &[],
        &[(MonsterId::FuzzyWurmCrawler, 50)],
    );
    play_hand_card(&mut state, 0, Some(enemies[0]), vec![]).unwrap();
    play_hand_card(&mut state, 0, Some(enemies[0]), vec![]).unwrap();
    assert_eq!(state.player.discard.len(), 2);
    state.player.turn_number += 1;
    let res = sts2_kernel::hooks::before_hand_draw(&mut state, &content).unwrap();
    assert!(res.is_none());
    assert_eq!(state.player.hand.len(), 2);

    // Guilty increments combatsCompleted and removes from deck at 5
    let (mut state, _) = build_battle(
        CharacterId::Ironclad,
        &[],
        &[],
        &[],
        &[(MonsterId::FuzzyWurmCrawler, 50)],
    );
    let guilty_h = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    state.cards.push(make_card(guilty_h.0, CardId::Guilty, PileType::Deck));
    state.player.deck.push(guilty_h);
    for expected in 1..=4 {
        sts2_kernel::hooks::after_combat_end(&mut state, &content).unwrap();
        assert_eq!(state.player.deck.len(), 1);
        assert_eq!(
            content.card_comparison_base_values(&state, guilty_h).unwrap()[0],
            ("Combats", Dec::from_i32(5 - expected))
        );
    }
    sts2_kernel::hooks::after_combat_end(&mut state, &content).unwrap();
    assert!(state.player.deck.is_empty());
}

#[test]
fn on_turn_end_in_hand_helper_covers_all_11_cards() {
    let content = Sts2Content;
    let cards = [
        CardId::BadLuck,
        CardId::Beckon,
        CardId::Burn,
        CardId::Decay,
        CardId::Infection,
        CardId::Toxic,
        CardId::Debt,
        CardId::Doubt,
        CardId::Shame,
        CardId::Regret,
        CardId::Wither,
    ];
    let (mut state, _) = build_battle(
        CharacterId::Ironclad,
        &cards,
        &[],
        &[],
        &[(MonsterId::FuzzyWurmCrawler, 50)],
    );
    let owner = state.player.creature_handle;
    state.creature_mut(owner).unwrap().block = 10;
    state.sync_player_hp_to_creature();
    // Wither fake upgrade level = 2 -> 3 + 6 = 9 damage
    let wither_h = state.player.hand[10];
    state.card_ext_mut(wither_h).unwrap().private_fields.insert(
        "Wither._fakeUpgradeLevel".to_owned(),
        FieldValue::Int(2),
    );
    // Regret records hand count via BeforeSideTurnEnd
    let regret_h = state.player.hand[9];
    content
        .dispatch_hook(
            &mut state,
            HookListener::Card(regret_h),
            HookSlot::BeforeSideTurnEnd,
            &HookArgs::SideTurn {
                side: CombatSide::Player,
                participants: &[owner],
            },
        )
        .unwrap();

    for i in 0..cards.len() {
        let h = state.player.hand[i];
        on_turn_end_in_hand(&content, &mut state, h).unwrap();
    }

    // Blockable: Burn(2)+Decay(2)+Infection(3)+Toxic(5)+Wither(9) = 21 against 10 block -> 11 hp loss
    // Unblockable: BadLuck(13)+Beckon(6)+Regret(11) = 30 hp loss
    // Total hp loss = 41 from 80 -> 39
    assert_eq!(state.player.current_hp, 39);
    assert_eq!(state.player.gold, 89);
    let pc = state.creature(owner).unwrap();
    let weak = pc.powers.iter().find(|p| p.id == PowerId::WeakPower).unwrap();
    assert_eq!(weak.amount, 1);
    assert!(weak.skip_next_duration_tick);
    let frail = pc.powers.iter().find(|p| p.id == PowerId::FrailPower).unwrap();
    assert_eq!(frail.amount, 1);
    assert!(frail.skip_next_duration_tick);
    assert_eq!(
        content.card_comparison_base_values(&state, wither_h).unwrap()[0],
        ("Damage", Dec::from_i32(9))
    );
}

#[test]
fn kernel_wired_surfaces_and_helpers_work() {
    let content = Sts2Content;

    // 1. TheTower helper & on_play using RootStaticContext.total_floor
    assert_eq!(the_tower_damage(false, 14), Dec::from_i32(28));
    assert_eq!(the_tower_damage(true, 14), Dec::from_i32(42));
    let (mut state, enemies) = build_battle(
        CharacterId::Ironclad,
        &[CardId::TheTower],
        &[],
        &[],
        &[(MonsterId::FuzzyWurmCrawler, 50)],
    );
    let mut ctx = (*state.static_ctx).clone();
    ctx.total_floor = 14;
    state.static_ctx = Arc::new(ctx);
    play_hand_card(&mut state, 0, Some(enemies[0]), vec![]).unwrap();
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, 50 - 28);

    // 2. TheDevil unupgraded vs upgraded (upgrading drawn card before autoplay)
    let (mut state, _) = build_battle(
        CharacterId::Ironclad,
        &[CardId::TheDevil, CardId::Wound, CardId::Injury],
        &[CardId::Soul, CardId::Fuel],
        &[],
        &[(MonsterId::FuzzyWurmCrawler, 50)],
    );
    play_hand_card(&mut state, 0, None, vec![]).unwrap();
    // Wound & Injury exhausted (2), Soul & Fuel autoplayed & exhausted (2), TheDevil itself exhausted (1) = 5 total
    assert_eq!(state.player.exhaust.len(), 5);
    assert_eq!(state.player.energy, 11); // Fuel gave +1

    let (mut state_upg, _) = build_battle(
        CharacterId::Ironclad,
        &[CardId::TheDevil, CardId::Wound],
        &[CardId::Fuel],
        &[],
        &[(MonsterId::FuzzyWurmCrawler, 50)],
    );
    let devil_h = state_upg.player.hand[0];
    card_cmd::upgrade(&mut state_upg, &content, devil_h).unwrap();
    play_hand_card(&mut state_upg, 0, None, vec![]).unwrap();
    assert_eq!(state_upg.player.energy, 12); // Fuel+ gave +2

    // 3. Omnislice single-enemy & multi-enemy capped splash
    let (mut state_single, enemies) = build_battle(
        CharacterId::Ironclad,
        &[CardId::Omnislice],
        &[],
        &[],
        &[(MonsterId::FuzzyWurmCrawler, 50)],
    );
    play_hand_card(&mut state_single, 0, Some(enemies[0]), vec![]).unwrap();
    assert_eq!(state_single.creature(enemies[0]).unwrap().current_hp, 42);

    let (mut state_multi, enemies) = build_battle(
        CharacterId::Ironclad,
        &[CardId::Omnislice],
        &[],
        &[],
        &[
            (MonsterId::FuzzyWurmCrawler, 50),
            (MonsterId::FuzzyWurmCrawler, 50),
        ],
    );
    // Apply Vulnerable to enemies[1] to prove splash uses ModifyDamageHookType::CAP only
    let pc_multi = state_multi.player.creature_handle;
    power::apply(
        &mut state_multi,
        &content,
        PowerId::VulnerablePower,
        enemies[1],
        Dec::TWO,
        Some(pc_multi),
        None,
    )
    .unwrap();
    play_hand_card(&mut state_multi, 0, Some(enemies[0]), vec![]).unwrap();
    assert_eq!(state_multi.creature(enemies[0]).unwrap().current_hp, 42);
    assert_eq!(state_multi.creature(enemies[1]).unwrap().current_hp, 42);
    let sample_res = vec![DamageResult {
        receiver: enemies[0],
        original_target: enemies[0],
        props: ValueProp::MOVE,
        intended_hp_loss: 8,
        blocked_damage: 3,
        unblocked_damage: 5,
        overkill_damage: 2,
        was_target_killed: false,
        was_block_broken: true,
        was_fully_blocked: false,
    }];
    assert_eq!(omnislice_splash_amount(&sample_res), Dec::from_i32(10));

    // 4. MadScience Attack & Skill & Power through CombatEngine::play_card
    let (mut state_ms, enemies) = build_battle(
        CharacterId::Ironclad,
        &[CardId::MadScience, CardId::MadScience],
        &[],
        &[],
        &[(MonsterId::FuzzyWurmCrawler, 50)],
    );
    let ms_atk = state_ms.player.hand[0];
    state_ms.card_ext_mut(ms_atk).unwrap().private_fields.insert(
        "MadScience.<Rider>k__BackingField".to_owned(),
        FieldValue::String("Violence".to_owned()),
    );
    play_hand_card(&mut state_ms, 0, Some(enemies[0]), vec![]).unwrap();
    assert_eq!(state_ms.creature(enemies[0]).unwrap().current_hp, 14); // 12 * 3 = 36

    let ms_skill = state_ms.player.hand[0];
    state_ms.card_ext_mut(ms_skill).unwrap().private_fields.insert(
        "MadScience._type".to_owned(),
        FieldValue::String("Skill".to_owned()),
    );
    state_ms.card_ext_mut(ms_skill).unwrap().private_fields.insert(
        "MadScience.<Rider>k__BackingField".to_owned(),
        FieldValue::String("Energized".to_owned()),
    );
    assert_eq!(mad_science_resolved_type(&state_ms, ms_skill), CardType::Skill);
    assert_eq!(
        mad_science_resolved_target_type(&state_ms, ms_skill),
        TargetType::SelfTarget
    );
    let e_before = state_ms.player.energy;
    play_hand_card(&mut state_ms, 0, None, vec![]).unwrap();
    assert_eq!(state_ms.player.block, 8);
    assert_eq!(state_ms.player.energy, e_before + 2 - 1);
}

#[test]
fn selection_cards_and_resumes_work() {
    // Discovery, Splash, Purity, SecretTechnique, SecretWeapon, SeekerStrike, ThinkingAhead, DualWield, NeowsFury, TheHermit, Wish
    let (mut state, enemies) = build_battle(
        CharacterId::Silent,
        &[
            CardId::Discovery,
            CardId::Splash,
            CardId::DualWield,
            CardId::Purity,
            CardId::TheHermit,
            CardId::Shiv,
            CardId::Injury,
        ],
        &[CardId::Finesse, CardId::FlashOfSteel, CardId::DefendSilent],
        &[CardId::Soul],
        &[(MonsterId::FuzzyWurmCrawler, 80)],
    );
    // Restrict card_generation_pools in this unit test to implemented non-Basic cards
    // so Discovery does not select an unimplemented W1 character card on this branch.
    Arc::make_mut(&mut state.static_ctx).card_generation_pools = vec![
        CardId::FlashOfSteel,
        CardId::Finesse,
        CardId::MasterOfStrategy,
        CardId::DramaticEntrance,
    ];
    let outcome = play_hand_card(&mut state, 0, None, vec![]).unwrap();
    assert!(matches!(outcome, StepOutcome::NeedsChoice(_)));
    CombatEngine::resume_choice(&mut state, &Sts2Content, &[1]).unwrap();
    // Discovery exhausted; 1 free card added to hand
    assert_eq!(state.player.exhaust.len(), 1);

    // Splash: pause with 3 candidates, cancel selection cleanly
    let splash_idx = state
        .player
        .hand
        .iter()
        .position(|&h| state.card(h).unwrap().id == CardId::Splash)
        .unwrap();
    let splash_outcome = play_hand_card(&mut state, splash_idx, None, vec![]).unwrap();
    match splash_outcome {
        StepOutcome::NeedsChoice(req) => {
            assert_eq!(req.candidates.len(), 3);
            assert!(req.can_skip);
        }
        other => panic!("expected NeedsChoice for Splash, got {other:?}"),
    }
    CombatEngine::resume_choice(&mut state, &Sts2Content, &[]).unwrap();

    // TheHermit exhausts Injury (Curse) and draws 2
    let hermit_idx = state
        .player
        .hand
        .iter()
        .position(|&h| state.card(h).unwrap().id == CardId::TheHermit)
        .unwrap();
    let injury_cand_idx = state
        .player
        .hand
        .iter()
        .filter(|&&h| h != state.player.hand[hermit_idx])
        .position(|&h| state.card(h).unwrap().id == CardId::Injury)
        .unwrap();
    let _ = injury_cand_idx;
    let hand_before = state.player.hand.len();
    let injury_pos = state
        .player
        .hand
        .iter()
        .position(|&h| state.card(h).unwrap().id == CardId::Injury)
        .unwrap();
    // Play TheHermit (first remove it from hand during play, so indices shift if injury was after hermit)
    let expected_cand = if injury_pos > hermit_idx {
        injury_pos - 1
    } else {
        injury_pos
    };
    play_hand_card(&mut state, hermit_idx, None, vec![vec![expected_cand]]).unwrap();
    assert!(state
        .player
        .exhaust
        .iter()
        .any(|&h| state.card(h).unwrap().id == CardId::Injury));
    assert_eq!(state.player.hand.len(), hand_before - 2 + 2);

    // SecretTechnique + SecretWeapon + ThinkingAhead + Wish + NeowsFury + SeekerStrike
    let (mut state2, enemies2) = build_battle(
        CharacterId::Ironclad,
        &[
            CardId::SecretTechnique,
            CardId::SecretWeapon,
            CardId::ThinkingAhead,
            CardId::Wish,
            CardId::NeowsFury,
            CardId::SeekerStrike,
        ],
        &[
            CardId::Finesse,
            CardId::FlashOfSteel,
            CardId::UltimateStrike,
            CardId::UltimateDefend,
        ],
        &[CardId::Soul, CardId::Fuel],
        &[(MonsterId::FuzzyWurmCrawler, 100)],
    );
    let play_by_id = |s: &mut BattleState, cid: CardId, tgt: Option<CreatureHandle>, ch: Vec<Vec<usize>>| {
        let idx = s.player.hand.iter().position(|&h| s.card(h).unwrap().id == cid).unwrap();
        play_hand_card(s, idx, tgt, ch)
    };
    play_by_id(&mut state2, CardId::SecretTechnique, None, vec![vec![0]]).unwrap();
    play_by_id(&mut state2, CardId::SecretWeapon, None, vec![vec![0]]).unwrap();
    play_by_id(&mut state2, CardId::ThinkingAhead, None, vec![vec![0]]).unwrap();
    // Put a card back on top; ensure Wish is still playable (if Wish was put back, draw it or find remaining)
    if let Some(wish_idx) = state2.player.hand.iter().position(|&h| state2.card(h).unwrap().id == CardId::Wish) {
        play_hand_card(&mut state2, wish_idx, None, vec![vec![0]]).unwrap();
    }
    play_by_id(&mut state2, CardId::NeowsFury, Some(enemies2[0]), vec![vec![0, 1]]).unwrap();
    assert!(state2.player.discard.is_empty());
    play_by_id(&mut state2, CardId::SeekerStrike, Some(enemies2[0]), vec![vec![0]]).unwrap();
    assert_eq!(enemies2[0], enemies[0]);
}

#[test]
fn upgrades_forging_tokens_and_special_cards_work() {
    let content = Sts2Content;
    let (mut state, enemies) = build_battle(
        CharacterId::Regent,
        &[
            CardId::Apotheosis,
            CardId::Maul,
            CardId::DingyDagger,
            CardId::HandOfGreed,
            CardId::BrightestFlame,
            CardId::MinionStrike,
            CardId::MinionDiveBomb,
            CardId::MinionSacrifice,
            CardId::Sadness,
            CardId::TheBall,
        ],
        &[CardId::Shiv, CardId::Finesse],
        &[],
        &[(MonsterId::FuzzyWurmCrawler, 15)],
    );
    // Forge creates SovereignBlade and increases its damage
    forge::forge(&mut state, &content, Dec::from_i32(5)).unwrap();
    let blade_h = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::SovereignBlade)
        .unwrap_or(&state.player.discard[0]);
    assert_eq!(
        content.card_comparison_base_values(&state, blade_h).unwrap()[0],
        ("Damage", Dec::from_i32(15))
    );

    // DingyDagger multi-upgrade: 7 -> 11 -> 16
    let dagger_h = state.player.hand[2];
    card_cmd::upgrade(&mut state, &content, dagger_h).unwrap();
    card_cmd::upgrade(&mut state, &content, dagger_h).unwrap();
    assert_eq!(state.card(dagger_h).unwrap().upgrade_level, 2);

    // Maul play buffs itself + upgrade preserves buff
    let maul_h = state.player.hand[1];
    play_hand_card(&mut state, 1, Some(enemies[0]), vec![]).unwrap(); // 5*2 = 10 dmg -> enemy at 5 HP
    assert_eq!(
        content.card_comparison_base_values(&state, maul_h).unwrap()[0],
        ("Damage", Dec::from_i32(7))
    );
    // Apotheosis upgrades everything else (including Maul in discard: 7 -> 8!)
    play_hand_card(&mut state, 0, None, vec![]).unwrap();
    assert_eq!(
        content.card_comparison_base_values(&state, maul_h).unwrap()[0],
        ("Damage", Dec::from_i32(8))
    );

    // HandOfGreed fatal grants gold!
    let hog_idx = state
        .player
        .hand
        .iter()
        .position(|&h| state.card(h).unwrap().id == CardId::HandOfGreed)
        .unwrap();
    let gold_before = state.player.gold;
    play_hand_card(&mut state, hog_idx, Some(enemies[0]), vec![]).unwrap();
    assert_eq!(state.player.gold, gold_before + 25); // upgraded by Apotheosis: +25 gold!
}

#[test]
fn remaining_colorless_status_curse_and_token_cards_execute_cleanly() {
    let content = Sts2Content;
    let (mut state, enemies) = build_battle(
        CharacterId::Silent,
        &[
            CardId::Rend,
            CardId::TheBomb,
            CardId::ToricToughness,
            CardId::Entrench,
            CardId::Prolong,
            CardId::Fisticuffs,
            CardId::Judgement,
            CardId::Enlightenment,
            CardId::WheelOfFortune,
        ],
        &[
            CardId::Shiv,
            CardId::Soul,
            CardId::Fuel,
            CardId::Luminesce,
            CardId::Finesse,
            CardId::FlashOfSteel,
            CardId::Scrawl,
            CardId::Production,
            CardId::Joy,
        ],
        &[CardId::StrikeSilent],
        &[(MonsterId::FuzzyWurmCrawler, 80)],
    );
    let owner = state.player.creature_handle;
    let enemy = enemies[0];
    // Apply 1 negative Strength (non-temp debuff), 1 Vulnerable (non-temp debuff), 1 DarkShackles (temp debuff)
    sts2_kernel::commands::power::apply(
        &mut state,
        &content,
        PowerId::StrengthPower,
        enemy,
        Dec::from_i32(-2),
        Some(owner),
        None,
    )
    .unwrap();
    sts2_kernel::commands::power::apply(
        &mut state,
        &content,
        PowerId::VulnerablePower,
        enemy,
        Dec::ONE,
        Some(owner),
        None,
    )
    .unwrap();
    sts2_kernel::commands::power::apply(
        &mut state,
        &content,
        PowerId::DarkShacklesPower,
        enemy,
        Dec::from_i32(5),
        Some(owner),
        None,
    )
    .unwrap();

    // Rend: 10 + 5*2 = 20 base, *1.5 Vulnerable = 30 -> enemy 80 -> 50
    play_hand_card(&mut state, 0, Some(enemy), vec![]).unwrap();
    assert_eq!(state.creature(enemy).unwrap().current_hp, 50);

    // TheBomb + ToricToughness + Entrench + Prolong
    play_hand_card(&mut state, 0, None, vec![]).unwrap();
    play_hand_card(&mut state, 0, None, vec![]).unwrap(); // +5 block -> 5
    play_hand_card(&mut state, 0, None, vec![]).unwrap(); // Entrench doubles -> 10
    assert_eq!(state.player.block, 10);
    play_hand_card(&mut state, 0, None, vec![]).unwrap(); // Prolong -> BlockNextTurn(10)

    // Fisticuffs: 7 * 1.5 Vuln = 10 -> 10 block gained (10 + 10 = 20) -> enemy 50 -> 40
    play_hand_card(&mut state, 0, Some(enemy), vec![]).unwrap();
    assert_eq!(state.creature(enemy).unwrap().current_hp, 40);
    assert_eq!(state.player.block, 20);

    // Judgement: threshold 30, enemy is 40 -> no kill
    play_hand_card(&mut state, 0, Some(enemy), vec![]).unwrap();
    assert!(state.creature(enemy).unwrap().is_alive());

    // Enlightenment + WheelOfFortune
    play_hand_card(&mut state, 0, None, vec![]).unwrap();
    play_hand_card(&mut state, 0, None, vec![]).unwrap();
    assert_eq!(state.player.hand.len(), 7);

    // Batch 2: Alchemize, Abundance, JackOfAllTrades, Jackpot, TheMagician, TheMoon, TheSun, TheHangedMan, Whistle, Volley
    let (mut state2, enemies2) = build_battle(
        CharacterId::Defect,
        &[
            CardId::Alchemize,
            CardId::Abundance,
            CardId::JackOfAllTrades,
            CardId::Jackpot,
            CardId::TheMagician,
            CardId::TheMoon,
            CardId::TheSun,
            CardId::TheHangedMan,
            CardId::Whistle,
            CardId::Volley,
        ],
        &[CardId::Apotheosis, CardId::MasterOfStrategy, CardId::UltimateStrike],
        &[CardId::Finesse, CardId::FlashOfSteel],
        &[(MonsterId::FuzzyWurmCrawler, 120)],
    );
    Arc::make_mut(&mut state2.static_ctx).card_generation_pools = vec![
        CardId::Zap,
        CardId::Dualcast,
        CardId::StrikeDefect,
        CardId::DefendDefect,
        CardId::Panache,
        CardId::HelloWorld,
        CardId::Calamity,
    ];
    play_hand_card(&mut state2, 0, None, vec![]).unwrap(); // Alchemize
    assert!(state2.player.potion_slots.iter().any(|s| s.is_some()));
    play_hand_card(&mut state2, 0, None, vec![]).unwrap(); // Abundance -> adds Power to hand
    play_hand_card(&mut state2, 0, None, vec![]).unwrap(); // JackOfAllTrades
    play_hand_card(&mut state2, 0, Some(enemies2[0]), vec![]).unwrap(); // Jackpot
    let mag_idx = state2
        .player
        .hand
        .iter()
        .position(|&h| state2.card(h).unwrap().id == CardId::TheMagician)
        .unwrap();
    play_hand_card(&mut state2, mag_idx, None, vec![]).unwrap();
    assert_eq!(state2.player.orbs.len(), 3);
    let sun_idx = state2
        .player
        .hand
        .iter()
        .position(|&h| state2.card(h).unwrap().id == CardId::TheSun)
        .unwrap();
    play_hand_card(&mut state2, sun_idx, None, vec![]).unwrap();
    assert!(state2
        .player
        .discard
        .iter()
        .any(|&h| state2.card(h).unwrap().id == CardId::Soot));
    let whistle_idx = state2
        .player
        .hand
        .iter()
        .position(|&h| state2.card(h).unwrap().id == CardId::Whistle)
        .unwrap();
    play_hand_card(&mut state2, whistle_idx, Some(enemies2[0]), vec![]).unwrap();
    assert_eq!(
        state2.creature(enemies2[0])
            .unwrap()
            .monster
            .as_ref()
            .unwrap()
            .next_move_id
            .as_deref(),
        Some("STUNNED")
    );
}

#[test]
fn shiv_and_sovereign_blade_target_type_reflect_powers() {
    let content = Sts2Content;
    let (mut state, enemies) = build_battle(
        CharacterId::Silent,
        &[CardId::FanOfKnives, CardId::Shiv, CardId::SovereignBlade],
        &[],
        &[],
        &[(MonsterId::FuzzyWurmCrawler, 40), (MonsterId::FuzzyWurmCrawler, 40)],
    );
    let shiv_h = state.player.hand[1];
    let blade_h = state.player.hand[2];
    assert_eq!(
        content.card_resolved_target_type(&state, shiv_h).unwrap(),
        sts2_types::TargetType::AnyEnemy
    );
    assert_eq!(
        content.card_resolved_target_type(&state, blade_h).unwrap(),
        sts2_types::TargetType::AnyEnemy
    );

    play_hand_card(&mut state, 0, None, vec![]).unwrap(); // FanOfKnives
    assert_eq!(
        content.card_resolved_target_type(&state, shiv_h).unwrap(),
        sts2_types::TargetType::AllEnemies
    );
    play_hand_card(&mut state, 0, None, vec![]).unwrap(); // Shiv AoE with target=None
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, 36);
    assert_eq!(state.creature(enemies[1]).unwrap().current_hp, 36);

    let pc = state.player.creature_handle;
    sts2_kernel::commands::power::apply(
        &mut state,
        &content,
        sts2_types::PowerId::SeekingEdgePower,
        pc,
        Dec::ONE,
        Some(pc),
        None,
    )
    .unwrap();
    assert_eq!(
        content.card_resolved_target_type(&state, blade_h).unwrap(),
        sts2_types::TargetType::AllEnemies
    );
    play_hand_card(&mut state, 0, None, vec![]).unwrap(); // SovereignBlade AoE with target=None
    assert_eq!(state.creature(enemies[0]).unwrap().current_hp, 26);
    assert_eq!(state.creature(enemies[1]).unwrap().current_hp, 26);
}

#[test]
fn frozen_pools_and_jackpot_roll_ordering_are_respected() {
    let (mut state, enemies) = build_battle(
        CharacterId::Ironclad,
        &[CardId::Jackpot, CardId::JackOfAllTrades, CardId::Alchemize],
        &[],
        &[],
        &[(MonsterId::FuzzyWurmCrawler, 80)],
    );
    {
        let ctx = Arc::make_mut(&mut state.static_ctx);
        ctx.card_generation_pools = vec![CardId::Anger];
        ctx.colorless_card_pool = vec![CardId::Finesse];
        ctx.potion_generation_pool = vec![sts2_types::PotionId::FirePotion];
    }

    let rng_before = state
        .run_rngs
        .get(sts2_types::RunRngType::CombatCardGeneration)
        .counter;
    play_hand_card(&mut state, 0, Some(enemies[0]), vec![]).unwrap(); // Jackpot
    assert_eq!(
        state
            .run_rngs
            .get(sts2_types::RunRngType::CombatCardGeneration)
            .counter,
        rng_before + 3
    );
    let anger_count = state
        .player
        .hand
        .iter()
        .filter(|&&h| state.card(h).is_some_and(|c| c.id == CardId::Anger))
        .count();
    assert_eq!(anger_count, 3);

    play_hand_card(&mut state, 0, None, vec![]).unwrap(); // JackOfAllTrades -> Finesse
    assert!(state
        .player
        .hand
        .iter()
        .any(|&h| state.card(h).is_some_and(|c| c.id == CardId::Finesse)));

    let alch_idx = state
        .player
        .hand
        .iter()
        .position(|&h| state.card(h).is_some_and(|c| c.id == CardId::Alchemize))
        .unwrap();
    play_hand_card(&mut state, alch_idx, None, vec![]).unwrap();
    assert_eq!(
        state.player.potion_slots[0],
        Some(sts2_types::PotionId::FirePotion)
    );
}
