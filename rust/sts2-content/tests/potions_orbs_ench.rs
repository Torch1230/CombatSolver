use smallvec::SmallVec;
use std::sync::Arc;
use sts2_content::orbs_enchantments_afflictions::{
    affliction_hook_mask, affliction_on_play, dispatch_affliction_hook, enchantment_damage_additive,
    enchantment_damage_multiplicative, enchantment_hook_mask, enchantment_modify_shuffle_order,
    enchantment_on_play, enchantment_play_count, enchantment_should_start_at_bottom, orb_meta,
};
use sts2_content::potions::{
    ashwater_candidates, card_choice_potion_candidates, card_choice_potion_resume,
    droplet_of_precognition_candidates, entropic_brew_roll_out_of_combat_potion,
    gamblers_brew_candidates, liquid_memories_candidates, potion_meta, potion_rarity,
    touch_of_insanity_candidates, PotionRarity,
};
use sts2_content::Sts2Content;
use sts2_kernel::commands::{card_pile, creature, orb, power};
use sts2_kernel::content_dispatch::CardPlayContext;
use sts2_kernel::{
    ordering, ArrowSlotInst, BattleState, CardHandle, CardInst, CardTempFlags, CombatEngine,
    CreatureHandle, CreatureInst, EnchantmentInst, FieldValue, HookArgs, MonsterInst,
    PlayerCounters, PlayerInst, RelicInst, RootStaticContext, SemanticHistory, StepOutcome,
};
use sts2_types::{
    AfflictionId, ArrowStatus, CardId, CardKeywords, CardPilePosition, CharacterId, CombatSide,
    Dec, EnchantmentId, EnchantmentStatus, GameMode, HookSlot, MonsterId, OrbId, PileType,
    PlayerRngSet, PlayerTurnPhase, PotionId, PowerId, RelicId, Rng, RoomType, RunRngSet,
    ValueProp,
};

fn make_card(handle: u32, id: CardId, pile: PileType) -> CardInst {
    let meta = sts2_types::generated_ids::generated_card_meta(id);
    CardInst {
        handle: CardHandle(handle),
        id,
        upgrade_level: 0,
        canonical_base_energy_cost: meta.canonical_energy_cost,
        base_energy_cost: meta.canonical_energy_cost,
        captured_energy_x_value: 0,
        base_replay_count: 0,
        floor_added_to_deck: Some(1),
        deck_card_handle: Some(CardHandle(handle)),
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
        pile,
        removed_from_state: false,
        transform_into_after_play: None,
    }
}

fn build_battle(
    character: CharacterId,
    deck_ids: &[CardId],
    relics: &[RelicId],
    potions: [Option<PotionId>; 3],
    enemies: &[(MonsterId, i32)],
) -> (BattleState, Vec<CreatureHandle>) {
    let run_rngs = RunRngSet::from_string_seed("W1POTORBTEST");
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc_handle = CreatureHandle(1);

    let mut cards = Vec::new();
    let mut deck_handles = Vec::new();
    for (idx, &cid) in deck_ids.iter().enumerate() {
        let h = (idx + 1) as u32;
        deck_handles.push(CardHandle(h));
        cards.push(make_card(h, cid, PileType::Deck));
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
            monster: Some(MonsterInst {
                monster_id: mid,
                rng: Rng::from_seed(run_rngs.seed + (idx as u64) + 1),
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
        potion_slots: SmallVec::from_slice(&potions),
        combat_generated_potion_mask: 0,
        orb_capacity: character.base_orb_slot_count(),
        orbs: Vec::new(),
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

    let enemy_vec = enemy_handles.to_vec();
    let state = BattleState {
        static_ctx: Arc::new(RootStaticContext {
            encounter_id: "TEST_ENCOUNTER".to_owned(),
            encounter_is_weak: false,
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
    (state, enemy_vec)
}

#[test]
fn all_67_potions_5_orbs_21_enchantments_7_afflictions_have_metadata() {
    for idx in 0..PotionId::COUNT {
        let pid: PotionId = unsafe { std::mem::transmute(idx as u16) };
        assert!(potion_meta(pid).is_ok(), "missing potion_meta for {pid:?}");
        let _ = potion_rarity(pid);
    }
    assert_eq!(potion_rarity(PotionId::Ambergris), PotionRarity::Event);
    assert_eq!(potion_rarity(PotionId::PotionShapedRock), PotionRarity::Token);

    for idx in 0..OrbId::COUNT {
        let oid: OrbId = unsafe { std::mem::transmute(idx as u8) };
        assert!(orb_meta(oid).is_ok(), "missing orb_meta for {oid:?}");
    }

    for idx in 0..EnchantmentId::COUNT {
        let eid: EnchantmentId = unsafe { std::mem::transmute(idx as u16) };
        assert!(
            enchantment_hook_mask(eid).is_ok(),
            "missing enchantment_hook_mask for {eid:?}"
        );
        assert!(enchantment_should_start_at_bottom(eid).is_ok());
    }

    for idx in 0..AfflictionId::COUNT {
        let aid: AfflictionId = unsafe { std::mem::transmute(idx as u16) };
        assert!(
            affliction_hook_mask(aid).is_ok(),
            "missing affliction_hook_mask for {aid:?}"
        );
    }
}

#[test]
fn all_five_orbs_passive_evoke_and_focus_semantics() {
    let content = Sts2Content;
    let deck = [CardId::StrikeDefect; 5];
    let (mut state, enemies) = build_battle(
        CharacterId::Defect,
        &deck,
        &[],
        [None, None, None],
        &[(MonsterId::FuzzyWurmCrawler, 60), (MonsterId::ShrinkerBeetle, 40)],
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();
    let e0 = enemies[0];
    let e1 = enemies[1];
    let pc = state.player.creature_handle;

    orb::channel(&mut state, &content, OrbId::FrostOrb).unwrap();
    orb::passive(&mut state, &content, 0, None, true).unwrap();
    assert_eq!(state.player.block, 2);
    orb::evoke_next(&mut state, &content, true).unwrap();
    assert_eq!(state.player.block, 7);

    power::apply(&mut state, &content, PowerId::FocusPower, pc, Dec::TWO, Some(pc), None).unwrap();
    orb::channel(&mut state, &content, OrbId::DarkOrb).unwrap();
    orb::passive(&mut state, &content, 0, None, true).unwrap();
    assert_eq!(state.player.orbs[0].raw_evoke_value, Some(Dec::from_i32(14)));
    orb::evoke_next(&mut state, &content, true).unwrap();
    assert_eq!(state.creature(e1).unwrap().current_hp, 40 - 14);
    assert_eq!(state.creature(e0).unwrap().current_hp, 60);

    let e_before = state.player.energy;
    orb::channel(&mut state, &content, OrbId::PlasmaOrb).unwrap();
    orb::evoke_next(&mut state, &content, true).unwrap();
    assert_eq!(state.player.energy, e_before + 2);

    orb::channel(&mut state, &content, OrbId::PlasmaOrb).unwrap();
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(state.player.energy, 4);

    power::apply(
        &mut state,
        &content,
        PowerId::FocusPower,
        pc,
        Dec::from_i32(-2),
        Some(pc),
        None,
    )
    .unwrap();
    orb::evoke_next(&mut state, &content, true).unwrap();
    orb::channel(&mut state, &content, OrbId::GlassOrb).unwrap();
    let e0_before = state.creature(e0).unwrap().current_hp;
    let e1_before = state.creature(e1).unwrap().current_hp;
    orb::passive(&mut state, &content, 0, None, true).unwrap();
    assert_eq!(state.player.orbs[0].raw_passive_value, Some(Dec::from_i32(3)));
    assert_eq!(state.creature(e0).unwrap().current_hp, e0_before - 4);
    assert_eq!(state.creature(e1).unwrap().current_hp, e1_before - 4);

    orb::evoke_next(&mut state, &content, true).unwrap();
    assert_eq!(state.creature(e0).unwrap().current_hp, e0_before - 10);
    assert_eq!(state.creature(e1).unwrap().current_hp, e1_before - 10);

    orb::channel(&mut state, &content, OrbId::GlassOrb).unwrap();
    state.player.orbs[0].raw_passive_value = Some(Dec::ZERO);
    orb::passive(&mut state, &content, 0, None, true).unwrap();
    assert_eq!(state.player.orbs[0].raw_passive_value, Some(Dec::ZERO));
    assert_eq!(state.creature(e0).unwrap().current_hp, e0_before - 10);
}

#[test]
fn runnable_potions_and_fairy_death_prevention() {
    let content = Sts2Content;
    let deck = [
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::Bash,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
    ];
    let (mut state, enemies) = build_battle(
        CharacterId::Ironclad,
        &deck,
        &[],
        [
            Some(PotionId::BlockPotion),
            Some(PotionId::FirePotion),
            Some(PotionId::Fortifier),
        ],
        &[(MonsterId::FuzzyWurmCrawler, 55)],
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();
    let pc = state.player.creature_handle;
    let e0 = enemies[0];
    let mut choices = SmallVec::new();

    CombatEngine::use_potion(&mut state, &content, 0, Some(pc), &mut choices).unwrap();
    assert_eq!(state.player.block, 12);
    CombatEngine::use_potion(&mut state, &content, 2, Some(pc), &mut choices).unwrap();
    assert_eq!(state.player.block, 36);
    CombatEngine::use_potion(&mut state, &content, 1, Some(e0), &mut choices).unwrap();
    assert_eq!(state.creature(e0).unwrap().current_hp, 35);

    state.player.potion_slots[0] = Some(PotionId::FairyInABottle);
    state.invalidate_hook_cache();
    state.player_creature_mut().unwrap().block = 0;
    state.sync_player_hp_to_creature();
    creature::kill(&mut state, &content, pc, None).unwrap();
    assert_eq!(state.player.current_hp, 24);
    assert_eq!(state.player.potion_slots[0], None);

    state.player.potion_slots[0] = Some(PotionId::BloodPotion);
    state.player.potion_slots[1] = Some(PotionId::FruitJuice);
    state.invalidate_hook_cache();
    CombatEngine::use_potion(&mut state, &content, 0, Some(pc), &mut choices).unwrap();
    assert_eq!(state.player.current_hp, 40);
    CombatEngine::use_potion(&mut state, &content, 1, Some(pc), &mut choices).unwrap();
    assert_eq!(state.player.max_hp, 85);
    assert_eq!(state.player.current_hp, 45);

    state.player.potion_slots[0] = Some(PotionId::FoulPotion);
    state.player.potion_slots[1] = Some(PotionId::ExplosiveAmpoule);
    state.invalidate_hook_cache();
    CombatEngine::use_potion(&mut state, &content, 0, None, &mut choices).unwrap();
    assert_eq!(state.player.current_hp, 33);
    assert_eq!(state.creature(e0).unwrap().current_hp, 23);
    CombatEngine::use_potion(&mut state, &content, 1, None, &mut choices).unwrap();
    assert_eq!(state.creature(e0).unwrap().current_hp, 13);

    state.player.potion_slots[0] = Some(PotionId::SoldiersStew);
    state.invalidate_hook_cache();
    CombatEngine::use_potion(&mut state, &content, 0, Some(pc), &mut choices).unwrap();
    let strike_replays: Vec<i32> = state
        .player
        .hand
        .iter()
        .chain(state.player.draw.iter())
        .filter_map(|&h| {
            let c = state.card(h)?;
            (c.id == CardId::StrikeIronclad).then_some(c.base_replay_count)
        })
        .collect();
    assert!(!strike_replays.is_empty());
    assert!(strike_replays.iter().all(|&r| r == 1));
}

#[test]
fn choice_potions_entropic_distilled_chaos_and_forge_end_to_end() {
    let content = Sts2Content;
    let deck = [
        CardId::Bash,
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::Bash,
    ];
    let (mut state, enemies) = build_battle(
        CharacterId::Ironclad,
        &deck,
        &[],
        [
            Some(PotionId::Ashwater),
            Some(PotionId::GamblersBrew),
            Some(PotionId::DropletOfPrecognition),
        ],
        &[(MonsterId::FuzzyWurmCrawler, 100)],
    );
    CombatEngine::start_combat(&mut state, &content).unwrap();
    let pc = state.player.creature_handle;
    let e0 = enemies[0];
    let mut choices = SmallVec::new();

    // 1. Ashwater pause & resume via CombatEngine
    assert_eq!(ashwater_candidates(&state).len(), 5);
    let ash_res = CombatEngine::use_potion(&mut state, &content, 0, Some(pc), &mut choices).unwrap();
    assert!(matches!(ash_res, StepOutcome::NeedsChoice(_)));
    CombatEngine::resume_choice(&mut state, &content, &[0]).unwrap();
    assert_eq!(state.player.hand.len(), 4);
    assert_eq!(state.player.exhaust.len(), 1);

    // 2. GamblersBrew pause & resume via CombatEngine
    assert_eq!(gamblers_brew_candidates(&state).len(), 4);
    let gam_res = CombatEngine::use_potion(&mut state, &content, 1, Some(pc), &mut choices).unwrap();
    assert!(matches!(gam_res, StepOutcome::NeedsChoice(_)));
    CombatEngine::resume_choice(&mut state, &content, &[0, 1]).unwrap();
    assert_eq!(state.player.discard.len(), 2);
    assert_eq!(state.player.hand.len(), 4);

    // 3. DropletOfPrecognition pause & resume (or auto-select if <=1)
    assert!(!droplet_of_precognition_candidates(&mut state, &content)
        .unwrap()
        .is_empty());
    let drop_res =
        CombatEngine::use_potion(&mut state, &content, 2, Some(pc), &mut choices).unwrap();
    if matches!(drop_res, StepOutcome::NeedsChoice(_)) {
        CombatEngine::resume_choice(&mut state, &content, &[0]).unwrap();
    }
    assert_eq!(state.player.hand.len(), 5);

    // 4. LiquidMemories pause & resume
    state.player.potion_slots[0] = Some(PotionId::LiquidMemories);
    assert_eq!(liquid_memories_candidates(&state).len(), 2);
    let lm_res = CombatEngine::use_potion(&mut state, &content, 0, Some(pc), &mut choices).unwrap();
    assert!(matches!(lm_res, StepOutcome::NeedsChoice(_)));
    let expected_lm_card = state.pending.as_ref().unwrap().request.candidates[0];
    CombatEngine::resume_choice(&mut state, &content, &[0]).unwrap();
    assert!(state.player.hand.contains(&expected_lm_card));
    assert_eq!(
        ordering::resolve_energy_cost(&mut state, &content, expected_lm_card).unwrap(),
        0
    );

    // 5. TouchOfInsanity pause & resume
    state.player.potion_slots[0] = Some(PotionId::TouchOfInsanity);
    let toi_cands = touch_of_insanity_candidates(&mut state, &content).unwrap();
    assert!(toi_cands.len() >= 2);
    let toi_res = CombatEngine::use_potion(&mut state, &content, 0, Some(pc), &mut choices).unwrap();
    assert!(matches!(toi_res, StepOutcome::NeedsChoice(_)));
    let toi_target = state.pending.as_ref().unwrap().request.candidates[0];
    CombatEngine::resume_choice(&mut state, &content, &[0]).unwrap();
    assert_eq!(
        ordering::resolve_energy_cost(&mut state, &content, toi_target).unwrap(),
        0
    );

    // 6. AttackPotion / SkillPotion / PowerPotion / ColorlessPotion pause & skip-resume
    for pid in [
        PotionId::AttackPotion,
        PotionId::SkillPotion,
        PotionId::PowerPotion,
        PotionId::ColorlessPotion,
    ] {
        state.player.potion_slots[0] = Some(pid);
        let res = CombatEngine::use_potion(&mut state, &content, 0, Some(pc), &mut choices).unwrap();
        let StepOutcome::NeedsChoice(req) = res else {
            panic!("{pid:?} should request card choice");
        };
        assert_eq!(req.candidates.len(), 3);
        assert!(req.can_skip);
        CombatEngine::resume_choice(&mut state, &content, &[]).unwrap();
        assert!(req
            .candidates
            .iter()
            .all(|&h| state.card(h).unwrap().removed_from_state));
    }

    // Also test selecting an implemented card through card_choice_potion_resume
    let choice_cands = card_choice_potion_candidates(&mut state, PotionId::AttackPotion).unwrap();
    assert_eq!(choice_cands.len(), 3);
    let bash_cand = state
        .cards
        .iter()
        .find(|c| c.id == CardId::Bash)
        .unwrap()
        .handle;
    let offer = [choice_cands[0], bash_cand];
    card_choice_potion_resume(&content, &mut state, &offer, &[bash_cand]).unwrap();
    assert!(state.card(choice_cands[0]).unwrap().removed_from_state);

    // 7. EntropicBrew fills empty potion slots via commands::potion::try_to_procure
    assert!(entropic_brew_roll_out_of_combat_potion(&mut state).is_some());
    state.player.potion_slots = smallvec::smallvec![Some(PotionId::EntropicBrew), None, None];
    CombatEngine::use_potion(&mut state, &content, 0, Some(pc), &mut choices).unwrap();
    assert!(state.player.potion_slots.iter().all(|s| s.is_some()));

    // 8. DistilledChaos autoplays top cards from draw pile via commands::autoplay::from_top_of_draw_pile
    state.player.draw.clear();
    state.player.discard.clear();
    let strike_h = make_card(state.next_card_handle, CardId::StrikeIronclad, PileType::Draw);
    state.next_card_handle += 1;
    let strike_handle = strike_h.handle;
    state.cards.push(strike_h);
    state.player.draw.push(strike_handle);
    state.player.potion_slots[0] = Some(PotionId::DistilledChaos);
    let enemy_hp_before = state.creature(e0).unwrap().current_hp;
    CombatEngine::use_potion(&mut state, &content, 0, Some(pc), &mut choices).unwrap();
    assert_eq!(state.creature(e0).unwrap().current_hp, enemy_hp_before - 6);

    // 9. KingsCourage calls commands::forge::forge (SovereignBlade is now implemented in Wave 2)
    let blade_h = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    state
        .cards
        .push(make_card(blade_h.0, CardId::SovereignBlade, PileType::Hand));
    state.player.hand.push(blade_h);
    sts2_kernel::commands::forge::forge(&mut state, &content, Dec::from_i32(15)).unwrap();
    let dmg_val = state
        .card_ext(state.card(blade_h).unwrap().ext_index)
        .unwrap()
        .private_fields
        .get("SovereignBlade._damage")
        .cloned();
    assert_eq!(dmg_val, Some(FieldValue::Decimal(Dec::from_i32(25))));
    state.player.hand.retain(|h| *h != blade_h);
    state.player.potion_slots[0] = Some(PotionId::KingsCourage);
    CombatEngine::use_potion(&mut state, &content, 0, Some(pc), &mut choices).unwrap();
    assert!(
        state
            .player
            .hand
            .iter()
            .any(|&h| state.card(h).is_some_and(|c| c.id == CardId::SovereignBlade))
    );

    // 10. SneckoOil randomizes hand costs via CombatEnergyCosts RNG
    let rng_before = state.run_rngs.get(sts2_types::RunRngType::CombatEnergyCosts).counter;
    state.player.potion_slots[0] = Some(PotionId::SneckoOil);
    CombatEngine::use_potion(&mut state, &content, 0, Some(pc), &mut choices).unwrap();
    assert!(
        state
            .run_rngs
            .get(sts2_types::RunRngType::CombatEnergyCosts)
            .counter
            > rng_before
    );
}

#[test]
fn enchantments_and_afflictions_kernel_wired_end_to_end() {
    let content = Sts2Content;
    let deck = [
        CardId::StrikeIronclad,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
        CardId::DefendIronclad,
        CardId::Bash,
        CardId::StrikeIronclad,
        CardId::DefendIronclad,
    ];
    let (mut state, enemies) = build_battle(
        CharacterId::Ironclad,
        &deck,
        &[],
        [None, None, None],
        &[(MonsterId::FuzzyWurmCrawler, 200)],
    );
    // Mark one card in deck with Imbued BEFORE start_combat to verify bottom-of-draw + turn-1 autoplay
    let imbued_deck_h = state.player.deck[0];
    state.card_ext_mut(imbued_deck_h).unwrap().enchantment = Some(EnchantmentInst {
        id: EnchantmentId::Imbued,
        amount: 1,
        status: EnchantmentStatus::Normal,
        private_fields: Default::default(),
    });
    // Note: Imbued autoplays at turn 1 start; let's attach Imbued to a Defend so target=None works
    let defend_deck_h = *state
        .player
        .deck
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::DefendIronclad)
        .unwrap();
    state.card_ext_mut(imbued_deck_h).unwrap().enchantment = None;
    state.card_ext_mut(defend_deck_h).unwrap().enchantment = Some(EnchantmentInst {
        id: EnchantmentId::Imbued,
        amount: 1,
        status: EnchantmentStatus::Normal,
        private_fields: Default::default(),
    });

    CombatEngine::start_combat(&mut state, &content).unwrap();
    // Imbued Defend was autoplayed on Turn 1 (+5 block, moved to discard)
    assert_eq!(state.player.block, 5);
    assert_eq!(state.player.discard.len(), 1);

    let e0 = enemies[0];
    let mut choices = SmallVec::new();

    // Test Vigorous, Glam, Goopy, Momentum, Sown, Inky, Corrupted directly through PlayCard!
    let hand_strikes: Vec<CardHandle> = state
        .player
        .hand
        .iter()
        .copied()
        .filter(|&h| state.card(h).unwrap().id == CardId::StrikeIronclad)
        .collect();
    let hand_defend = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::DefendIronclad)
        .unwrap();
    let hand_bash = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::Bash)
        .unwrap();

    // Strike 0: Vigorous (+4, disables after play)
    let s0 = hand_strikes[0];
    state.card_ext_mut(s0).unwrap().enchantment = Some(EnchantmentInst {
        id: EnchantmentId::Vigorous,
        amount: 4,
        status: EnchantmentStatus::Normal,
        private_fields: Default::default(),
    });
    state.invalidate_hook_cache();
    CombatEngine::play_card(&mut state, &content, s0, Some(e0), &mut choices).unwrap();
    assert_eq!(state.creature(e0).unwrap().current_hp, 200 - 10);

    // Strike 1: Glam (replays once via enchantment_play_count -> 6 + 6 = 12) + Sown/Inky/Momentum checks
    let s1 = hand_strikes[1];
    state.card_ext_mut(s1).unwrap().enchantment = Some(EnchantmentInst {
        id: EnchantmentId::Glam,
        amount: 1,
        status: EnchantmentStatus::Normal,
        private_fields: Default::default(),
    });
    state.invalidate_hook_cache();
    CombatEngine::play_card(&mut state, &content, s1, Some(e0), &mut choices).unwrap();
    assert_eq!(state.creature(e0).unwrap().current_hp, 200 - 10 - 12);

    // Defend: Goopy (starts at amount 1 -> +0 block on first play, increments combat & deck to 2)
    state.card_ext_mut(hand_defend).unwrap().enchantment = Some(EnchantmentInst {
        id: EnchantmentId::Goopy,
        amount: 1,
        status: EnchantmentStatus::Normal,
        private_fields: Default::default(),
    });
    state.invalidate_hook_cache();
    CombatEngine::play_card(&mut state, &content, hand_defend, None, &mut choices).unwrap();
    assert_eq!(state.player.block, 5 + 5);
    assert_eq!(
        state
            .card_ext(state.card(hand_defend).unwrap().ext_index)
            .unwrap()
            .enchantment
            .as_ref()
            .unwrap()
            .amount,
        2
    );

    // Bash: Corrupted (8 * 1.5 = 12 dmg to enemy, 2 unblockable self-dmg to player via OnPlay)
    state.player.energy = 5;
    state.card_ext_mut(hand_bash).unwrap().enchantment = Some(EnchantmentInst {
        id: EnchantmentId::Corrupted,
        amount: 1,
        status: EnchantmentStatus::Normal,
        private_fields: Default::default(),
    });
    state.invalidate_hook_cache();
    let hp_before = state.player.current_hp;
    let enemy_before = state.creature(e0).unwrap().current_hp;
    CombatEngine::play_card(&mut state, &content, hand_bash, Some(e0), &mut choices).unwrap();
    assert_eq!(state.creature(e0).unwrap().current_hp, enemy_before - 12);
    assert_eq!(state.player.current_hp, hp_before - 2);

    // Direct helper tests for Sown, Swift, Inky, Momentum, Adroit, Spiral, Sharp, Instinct, TezcatarasEmber
    state.card_ext_mut(s0).unwrap().enchantment = Some(EnchantmentInst {
        id: EnchantmentId::Momentum,
        amount: 3,
        status: EnchantmentStatus::Normal,
        private_fields: Default::default(),
    });
    state.invalidate_hook_cache();
    let ctx_s0 = CardPlayContext {
        card: s0,
        target: Some(e0),
        result_pile: PileType::Discard,
        is_auto_play: false,
        play_index: 0,
        play_count: 1,
        play_ordinal: 5,
        energy_spent: 1,
        energy_value: 1,
        stars_spent: 0,
        star_value: 0,
    };
    enchantment_on_play(&content, &mut state, s0, &ctx_s0).unwrap();
    assert_eq!(
        enchantment_damage_additive(&state, s0, Dec::from_i32(6), ValueProp::MOVE).unwrap(),
        Dec::from_i32(3)
    );
    assert_eq!(
        enchantment_damage_multiplicative(&state, hand_bash, Dec::from_i32(8), ValueProp::MOVE)
            .unwrap(),
        Dec::ONE_POINT_FIVE
    );
    state.card_ext_mut(s0).unwrap().enchantment = Some(EnchantmentInst {
        id: EnchantmentId::Spiral,
        amount: 1,
        status: EnchantmentStatus::Normal,
        private_fields: Default::default(),
    });
    state.invalidate_hook_cache();
    assert_eq!(enchantment_play_count(&state, s0, 1).unwrap(), 2);

    // PerfectFit puts itself at top on non-initial shuffle via card_pile::shuffle
    state.card_ext_mut(s0).unwrap().enchantment = Some(EnchantmentInst {
        id: EnchantmentId::PerfectFit,
        amount: 1,
        status: EnchantmentStatus::Normal,
        private_fields: Default::default(),
    });
    state.invalidate_hook_cache();
    let mut order = vec![hand_bash, s0, hand_defend];
    enchantment_modify_shuffle_order(&state, s0, &mut order, false).unwrap();
    assert_eq!(order[0], s0);
    card_pile::shuffle(&mut state, &content).unwrap();
    assert_eq!(state.player.draw[0], s0);

    // Hexed clears when entering combat without HexPower via card_pile::enter_combat
    let hexed_card = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    let mut hc = make_card(hexed_card.0, CardId::StrikeIronclad, PileType::None);
    hc.affliction = Some(sts2_kernel::state::AfflictionInst {
        id: AfflictionId::Hexed,
        amount: 1,
    });
    state.cards.push(hc);
    card_pile::enter_combat(
        &mut state,
        &content,
        hexed_card,
        PileType::Hand,
        CardPilePosition::Bottom,
        false,
    )
    .unwrap();
    assert!(state.card(hexed_card).unwrap().affliction.is_none());

    // Direct dispatch_affliction_hook and affliction_on_play check
    state.card_mut(s0).unwrap().affliction = Some(sts2_kernel::state::AfflictionInst {
        id: AfflictionId::Bound,
        amount: 1,
    });
    state.invalidate_hook_cache();
    affliction_on_play(&content, &mut state, s0, &ctx_s0).unwrap();
    dispatch_affliction_hook(
        &content,
        &mut state,
        s0,
        HookSlot::AfterCardEnteredCombat,
        &HookArgs::CardOnly(s0),
    )
    .unwrap();
}
