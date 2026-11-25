use smallvec::SmallVec;
use std::borrow::Cow;
use std::sync::Arc;
use sts2_content::{Sts2Content, dread_blitz, orbs_enchantments_afflictions};
use sts2_kernel::{
    BattleState, CardHandle, CardInst, CardTempFlags, ContentDispatcher, CreatureHandle,
    CreatureInst, EnchantmentInst, HookArgs, HookListener, KernelError, MonsterInst,
    PlayerCounters, PlayerInst, RelicInst, RootStaticContext, SemanticHistory,
};
use sts2_types::{
    AfflictionId, CardId, CardKeywords, CardType, CharacterId, CombatSide, DominionId,
    EnchantmentId, EnchantmentStatus, GameMode, HookSlot, MonsterId, OrbId, PileType,
    PlayerRngSet, PlayerTurnPhase, PotionId, PowerId, RelicId, Rng, RoomType, RunModelId,
    RunRngSet,
};

fn build_probe_state() -> BattleState {
    let run_rngs = RunRngSet::from_string_seed("COV1562");
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc = CreatureHandle(1);
    let ec = CreatureHandle(2);
    BattleState {
        static_ctx: Arc::new(RootStaticContext {
            encounter_id: "COV".to_owned(),
            encounter_is_weak: false,
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
            total_floor: 1,
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
        next_creature_id: 3,
        initial_encounter_monster_count: 1,
        next_card_handle: 2,
        next_creature_handle: 3,
        next_power_handle: 2,
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
            base_orb_slot_count: 3,
            is_active_for_hooks: true,
            can_use_or_remove_potions: true,
            energy: 3,
            stars: 0,
            phase: PlayerTurnPhase::Play,
            turn_number: 1,
            counters: PlayerCounters::default(),
            hand: Vec::new(),
            draw: Vec::new(),
            discard: Vec::new(),
            exhaust: Vec::new(),
            play: Vec::new(),
            deck: Vec::new(),
            potion_slots: smallvec::smallvec![None, None, None],
            combat_generated_potion_mask: 0,
            orb_capacity: 3,
            orbs: Vec::new(),
            arrows: Vec::new(),
            relics: Vec::new(),
            card_or_potion_effect_depth: 0,
        },
        cards: Vec::new(),
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
                has_minion_power: false,
                powers: Vec::new(),
            },
            CreatureInst {
                handle: ec,
                combat_id: Some(1),
                side: CombatSide::Enemy,
                slot_name: Some(Cow::Borrowed("slot_0")),
                is_player: false,
                pet_owner_player_index: None,
                monster_id: Some(MonsterId::FuzzyWurmCrawler),
                current_hp: 50,
                max_hp: 50,
                block: 0,
                cumulative_hp_lost: 0,
                is_detached: false,
                monster: Some(Box::new(MonsterInst {
                    monster_id: MonsterId::FuzzyWurmCrawler,
                    rng: Rng::from_seed(42),
                    is_performing_move: false,
                    spawned_this_turn: false,
                    current_state_id: Cow::Borrowed(""),
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
            },
        ],
        power_extensions: Vec::new(),
        ally_handles: vec![pc],
        enemy_handles: vec![ec],
        escaped_handles: SmallVec::new(),
        removed_handles: SmallVec::new(),
        spawned_enemy_handles: smallvec::smallvec![ec],
        pet_handles: SmallVec::new(),
        damage_history: Vec::new(),
        semantic_history: SemanticHistory::default(),
        pending: None,
        vakuu_auto_select_depth: 0,
        hook_cache: Default::default(),
        deck_dirty: Default::default(),
        relic_dirty_mask: Default::default(),
    }
}

#[test]
fn exhaustive_1562_vanilla_entities_zero_unsupported() {
    let content = Sts2Content;
    let mut total = 0usize;

    assert_eq!(CardId::COUNT, 716);
    for idx in 0..CardId::COUNT {
        let id: CardId = unsafe { std::mem::transmute(idx as u16) };
        let meta = content
            .card_meta(id)
            .unwrap_or_else(|e| panic!("card_meta failed for {id:?}: {e:?}"));
        let mut s = build_probe_state();
        let ch = CardHandle(1);
        s.cards.push(CardInst {
            handle: ch,
            id,
            upgrade_level: 0,
            canonical_base_energy_cost: meta.canonical_energy_cost as i16,
            base_energy_cost: meta.canonical_energy_cost as i16,
            captured_energy_x_value: 0,
            base_replay_count: 0,
            floor_added_to_deck: sts2_kernel::OptI16::some(1),
            deck_card_handle: sts2_kernel::OptCardHandle::some(ch),
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
            pile: PileType::Hand,
            removed_from_state: false,
            transform_into_after_play: None,
        });
        s.player.hand.push(ch);
        let _ = content
            .card_comparison_base_values(&s, ch)
            .unwrap_or_else(|e| panic!("card_comparison_base_values failed for {id:?}: {e:?}"));
        let _ = content
            .card_is_playable(&s, ch)
            .unwrap_or_else(|e| panic!("card_is_playable failed for {id:?}: {e:?}"));
        let _ = content
            .card_on_upgrade(&mut s, ch)
            .unwrap_or_else(|e| panic!("card_on_upgrade failed for {id:?}: {e:?}"));
        total += 1;
    }

    assert_eq!(PowerId::COUNT, 301);
    for idx in 0..PowerId::COUNT {
        let id: PowerId = unsafe { std::mem::transmute(idx as u16) };
        let _ = content
            .power_meta(id)
            .unwrap_or_else(|e| panic!("power_meta failed for {id:?}: {e:?}"));
        total += 1;
    }

    assert_eq!(RelicId::COUNT, 315);
    for idx in 0..RelicId::COUNT {
        let id: RelicId = unsafe { std::mem::transmute(idx as u16) };
        let _ = content
            .relic_meta(id)
            .unwrap_or_else(|e| panic!("relic_meta failed for {id:?}: {e:?}"));
        let mut s = build_probe_state();
        s.player.relics.push(RelicInst {
            id,
            is_wax: false,
            is_melted: false,
            stack_count: 1,
            floor_added_to_deck: None,
            private_fields: Default::default(),
        });
        let res = content.dispatch_hook(&mut s, HookListener::Relic(0), HookSlot::AfterCombatEnd, &HookArgs::None);
        assert!(
            !matches!(res, Err(KernelError::Unsupported { .. })),
            "relic dispatch returned Unsupported for {id:?}: {res:?}"
        );
        total += 1;
    }

    assert_eq!(PotionId::COUNT, 67);
    for idx in 0..PotionId::COUNT {
        let id: PotionId = unsafe { std::mem::transmute(idx as u16) };
        let _ = content
            .potion_meta(id)
            .unwrap_or_else(|e| panic!("potion_meta failed for {id:?}: {e:?}"));
        total += 1;
    }

    assert_eq!(OrbId::COUNT, 5);
    for idx in 0..OrbId::COUNT {
        let id: OrbId = unsafe { std::mem::transmute(idx as u8) };
        let _ = content
            .orb_meta(id)
            .unwrap_or_else(|e| panic!("orb_meta failed for {id:?}: {e:?}"));
        total += 1;
    }

    assert_eq!(EnchantmentId::COUNT, 21);
    for idx in 0..EnchantmentId::COUNT {
        let id: EnchantmentId = unsafe { std::mem::transmute(idx as u16) };
        let _ = orbs_enchantments_afflictions::enchantment_hook_mask(id)
            .unwrap_or_else(|e| panic!("enchantment_hook_mask failed for {id:?}: {e:?}"));
        let _ = content
            .enchantment_should_start_at_bottom(id)
            .unwrap_or_else(|e| panic!("enchantment_should_start_at_bottom failed for {id:?}: {e:?}"));
        let mut s = build_probe_state();
        s.card_extensions.push(sts2_kernel::CardExtension {
            enchantment: Some(EnchantmentInst {
                id,
                amount: 1,
                status: EnchantmentStatus::Normal,
                private_fields: Default::default(),
            }),
            local_energy_modifiers: SmallVec::new(),
            private_fields: Default::default(),
        });
        s.cards.push(CardInst {
            handle: CardHandle(1),
            id: CardId::StrikeIronclad,
            upgrade_level: 0,
            canonical_base_energy_cost: 1,
            base_energy_cost: 1,
            captured_energy_x_value: 0,
            base_replay_count: 0,
            floor_added_to_deck: sts2_kernel::OptI16::some(1),
            deck_card_handle: sts2_kernel::OptCardHandle::NONE,
            clone_of_handle: sts2_kernel::OptCardHandle::NONE,
            temp_star_cost_override_this_turn: sts2_kernel::OptI16::NONE,
            temp_cost_override_this_turn: sts2_kernel::OptI16::NONE,
            temp_cost_override_this_turn_or_until_played: sts2_kernel::OptI16::NONE,
            temp_cost_override_this_combat: sts2_kernel::OptI16::NONE,
            local_keywords: CardKeywords::empty(),
            keywords_added_from_outside: CardKeywords::empty(),
            keywords_removed_from_outside: CardKeywords::empty(),
            temp_flags: CardTempFlags::empty(),
            affliction: None,
            ext_index: Some(0),
            pile: PileType::Hand,
            removed_from_state: false,
            transform_into_after_play: None,
        });
        let res = content.dispatch_hook(&mut s, HookListener::Enchantment(CardHandle(1)), HookSlot::AfterCombatEnd, &HookArgs::None);
        assert!(
            !matches!(res, Err(KernelError::Unsupported { .. })),
            "enchantment dispatch returned Unsupported for {id:?}: {res:?}"
        );
        total += 1;
    }

    assert_eq!(AfflictionId::COUNT, 7);
    for idx in 0..AfflictionId::COUNT {
        let id: AfflictionId = unsafe { std::mem::transmute(idx as u16) };
        let _ = orbs_enchantments_afflictions::affliction_hook_mask(id)
            .unwrap_or_else(|e| panic!("affliction_hook_mask failed for {id:?}: {e:?}"));
        total += 1;
    }

    assert_eq!(DominionId::COUNT, 12);
    for idx in 0..DominionId::COUNT {
        let id: DominionId = unsafe { std::mem::transmute(idx as u16) };
        let _ = dread_blitz::dominion_hook_mask(id)
            .unwrap_or_else(|e| panic!("dominion_hook_mask failed for {id:?}: {e:?}"));
        let mut s = build_probe_state();
        let res = content.dispatch_hook(&mut s, HookListener::Dominion(id), HookSlot::AfterCombatEnd, &HookArgs::None);
        assert!(
            !matches!(res, Err(KernelError::Unsupported { .. })),
            "dominion dispatch returned Unsupported for {id:?}: {res:?}"
        );
        total += 1;
    }

    assert_eq!(RunModelId::COUNT, 2);
    for idx in 0..RunModelId::COUNT {
        let id: RunModelId = unsafe { std::mem::transmute(idx as u8) };
        let _ = dread_blitz::run_model_hook_mask(id)
            .unwrap_or_else(|e| panic!("run_model_hook_mask failed for {id:?}: {e:?}"));
        let mut s = build_probe_state();
        let res = content.dispatch_hook(&mut s, HookListener::RunModel(id), HookSlot::AfterCombatEnd, &HookArgs::None);
        assert!(
            !matches!(res, Err(KernelError::Unsupported { .. })),
            "run_model dispatch returned Unsupported for {id:?}: {res:?}"
        );
        total += 1;
    }

    assert_eq!(MonsterId::COUNT, 116);
    for idx in 0..MonsterId::COUNT {
        let id: MonsterId = unsafe { std::mem::transmute(idx as u16) };
        let _ = content
            .monster_meta(id)
            .unwrap_or_else(|e| panic!("monster_meta failed for {id:?}: {e:?}"));
        let mut s = build_probe_state();
        let eh = CreatureHandle(2);
        s.creatures[1].monster_id = Some(id);
        s.creatures[1].slot_name = Some(Cow::Borrowed(match id {
            MonsterId::PhantasmalGardener => "first",
            MonsterId::Wriggler => "wriggler1",
            _ => "slot_0",
        }));
        s.creatures[1].monster.as_mut().unwrap().monster_id = id;
        if id == MonsterId::Nibbit {
            s.creatures[1]
                .monster
                .as_mut()
                .unwrap()
                .setup_fields
                .insert("Nibbit._isAlone".to_owned(), sts2_kernel::FieldValue::Bool(true));
        }
        let _ = content
            .monster_initial_hp_range(&s, id)
            .unwrap_or_else(|e| panic!("monster_initial_hp_range failed for {id:?}: {e:?}"));
        let _ = content
            .monster_setup_for_combat(&mut s, eh)
            .unwrap_or_else(|e| panic!("monster_setup_for_combat failed for {id:?}: {e:?}"));
        let _ = content
            .monster_after_added_to_room(&mut s, eh)
            .unwrap_or_else(|e| panic!("monster_after_added_to_room failed for {id:?}: {e:?}"));
        let _ = content
            .monster_roll_move(&mut s, eh)
            .unwrap_or_else(|e| panic!("monster_roll_move failed for {id:?}: {e:?}"));
        let _ = content
            .monster_attack_intent_hits(&s, eh)
            .unwrap_or_else(|e| panic!("monster_attack_intent_hits failed for {id:?}: {e:?}"));
        total += 1;
    }

    assert_eq!(total, 1562);
}

#[test]
fn hook_mask_tables_and_rollout_profiles_match_per_entity_queries() {
    use sts2_kernel::rollout_op_flags as op;
    let content = Sts2Content;
    let tables = content
        .hook_mask_tables()
        .expect("Sts2Content must expose hook_mask_tables");
    let cats = content
        .category_hook_masks()
        .expect("Sts2Content must expose category_hook_masks");
    assert_eq!(tables.categories, cats);
    assert_eq!(
        tables.categories.deck_cards,
        sts2_types::HookMask::EMPTY.with(HookSlot::AfterCombatEnd)
    );

    for idx in 0..CardId::COUNT {
        let id: CardId = unsafe { std::mem::transmute(idx as u16) };
        let mask = content.card_hook_mask(id).unwrap();
        assert_eq!(tables.cards[idx], mask);
        assert_eq!(tables.card_has_hook(id), !mask.is_empty());
        assert_eq!(
            tables.card_has_custom_playability(id),
            matches!(id, CardId::Clash | CardId::HighFive)
        );
        let meta = content.card_meta(id).unwrap();
        assert_eq!(
            tables.card_is_status_or_curse(id),
            matches!(meta.card_type, CardType::Status | CardType::Curse)
        );
    }
    for idx in 0..PowerId::COUNT {
        let id: PowerId = unsafe { std::mem::transmute(idx as u16) };
        let mask = content.power_hook_mask(id).unwrap();
        assert_eq!(tables.powers[idx], mask);
        assert_eq!(tables.power_has_hook(id), !mask.is_empty());
        assert_eq!(
            mask.contains(HookSlot::ShouldAllowHitting),
            matches!(
                id,
                PowerId::AdaptablePower
                    | PowerId::DieForYouPower
                    | PowerId::IllusionPower
                    | PowerId::ReattachPower
                    | PowerId::WuppysLoyaltyPower
            ),
            "unexpected ShouldAllowHitting mask for {id:?}"
        );
        assert_eq!(
            mask.contains(HookSlot::ShouldCreatureBeRemovedFromCombatAfterDeath),
            matches!(
                id,
                PowerId::AdaptablePower
                    | PowerId::DieForYouPower
                    | PowerId::IllusionPower
                    | PowerId::PainfulStabsPower
                    | PowerId::ReattachPower
                    | PowerId::SteamEruptionPower
                    | PowerId::WuppysLoyaltyPower
            ),
            "unexpected ShouldCreatureBeRemovedFromCombatAfterDeath mask for {id:?}"
        );
        assert_eq!(
            mask.contains(HookSlot::ShouldPowerBeRemovedOnDeath),
            matches!(id, PowerId::IllusionPower | PowerId::ReattachPower),
            "unexpected ShouldPowerBeRemovedOnDeath mask for {id:?}"
        );
        assert_eq!(
            mask.contains(HookSlot::TryModifyEnergyCostInCombat),
            matches!(
                id,
                PowerId::BorrowedTimePower | PowerId::CuriousPower | PowerId::TangledPower
            ),
            "unexpected TryModifyEnergyCostInCombat mask for {id:?}"
        );
        assert_eq!(
            mask.contains(HookSlot::TryModifyEnergyCostInCombatLate),
            matches!(
                id,
                PowerId::CorruptionPower
                    | PowerId::EnsemblePower
                    | PowerId::FreeAttackPower
                    | PowerId::FreeCardPower
                    | PowerId::FreePowerPower
                    | PowerId::FreeSkillPower
                    | PowerId::VeilpiercerPower
                    | PowerId::VoidFormPower
            ),
            "unexpected TryModifyEnergyCostInCombatLate mask for {id:?}"
        );
    }
    for idx in 0..RelicId::COUNT {
        let id: RelicId = unsafe { std::mem::transmute(idx as u16) };
        let mask = content.relic_hook_mask(id).unwrap();
        assert_eq!(tables.relics[idx], mask);
        assert_eq!(tables.relic_has_hook(id), !mask.is_empty());
    }
    for idx in 0..PotionId::COUNT {
        let id: PotionId = unsafe { std::mem::transmute(idx as u16) };
        assert_eq!(tables.potions[idx], content.potion_hook_mask(id).unwrap());
    }
    for idx in 0..MonsterId::COUNT {
        let id: MonsterId = unsafe { std::mem::transmute(idx as u16) };
        let mask = content.monster_hook_mask(id).unwrap();
        assert_eq!(tables.monsters[idx], mask);
        assert_eq!(tables.monster_has_hook(id), !mask.is_empty());
    }
    for idx in 0..OrbId::COUNT {
        let id: OrbId = unsafe { std::mem::transmute(idx as u8) };
        assert_eq!(tables.orbs[idx], content.orb_hook_mask(id).unwrap());
    }
    assert_eq!(tables.enchantments.len(), EnchantmentId::COUNT);
    for idx in 0..EnchantmentId::COUNT {
        let id: EnchantmentId = unsafe { std::mem::transmute(idx as u16) };
        assert_eq!(
            tables.enchantments[idx],
            orbs_enchantments_afflictions::enchantment_hook_mask(id).unwrap()
        );
    }
    assert_eq!(tables.afflictions.len(), AfflictionId::COUNT);
    for idx in 0..AfflictionId::COUNT {
        let id: AfflictionId = unsafe { std::mem::transmute(idx as u16) };
        assert_eq!(
            tables.afflictions[idx],
            orbs_enchantments_afflictions::affliction_hook_mask(id).unwrap()
        );
    }

    let fight_me = content.card_rollout_profile(CardId::FightMe);
    assert_eq!((fight_me.base_damage, fight_me.upg_damage, fight_me.hits, fight_me.upg_hits), (5, 6, 2, 2));
    assert_ne!(fight_me.op_flags & op::HAS_ATTACK, 0);
    assert_ne!(fight_me.op_flags & op::HAS_SELF_BUFF, 0);

    let boomerang = content.card_rollout_profile(CardId::SwordBoomerang);
    assert_eq!((boomerang.base_damage, boomerang.hits, boomerang.upg_hits), (3, 3, 4));

    let seven_stars = content.card_rollout_profile(CardId::SevenStars);
    assert_eq!((seven_stars.base_damage, seven_stars.hits, seven_stars.upg_hits), (7, 7, 7));

    let glow = content.card_rollout_profile(CardId::Glow);
    assert_ne!(glow.op_flags & op::HAS_DRAW, 0);
    assert_ne!(glow.op_flags & op::HAS_ENERGY_OR_STAR_GAIN, 0);
    assert_ne!(glow.op_flags & op::HAS_SELF_BUFF, 0);

    let havoc = content.card_rollout_profile(CardId::Havoc);
    assert_ne!(havoc.op_flags & op::HAS_DRAW, 0);

    let dash = content.card_rollout_profile(CardId::Dash);
    assert_eq!((dash.base_damage, dash.upg_damage, dash.base_block, dash.upg_block), (10, 13, 10, 13));
    assert_ne!(dash.op_flags & op::HAS_ATTACK, 0);
    assert_ne!(dash.op_flags & op::HAS_BLOCK, 0);
}

#[test]
fn dynamic_comparison_values_set_matches_all_cards() {
    let content = Sts2Content;
    for idx in 0..CardId::COUNT {
        let id: CardId = unsafe { std::mem::transmute(idx as u16) };
        let meta = content.card_meta(id).unwrap();
        let mut s = build_probe_state();
        let ch = CardHandle(1);
        s.cards.push(CardInst {
            handle: ch,
            id,
            upgrade_level: 0,
            canonical_base_energy_cost: meta.canonical_energy_cost as i16,
            base_energy_cost: meta.canonical_energy_cost as i16,
            captured_energy_x_value: 0,
            base_replay_count: 0,
            floor_added_to_deck: sts2_kernel::OptI16::NONE,
            deck_card_handle: sts2_kernel::OptCardHandle::some(ch),
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
            pile: PileType::Hand,
            removed_from_state: false,
            transform_into_after_play: None,
        });
        s.player.hand.push(ch);
        let is_dyn = sts2_kernel::ordering::card_has_dynamic_comparison_values(id);
        let vals = match sts2_types::card_partition(id) {
            sts2_types::CardPartition::Ironclad => sts2_content::cards::ironclad::comparison_base_values(&s, ch),
            sts2_types::CardPartition::Silent => sts2_content::cards::silent::comparison_base_values(&s, ch),
            sts2_types::CardPartition::Defect => sts2_content::cards::defect::comparison_base_values(&s, ch),
            sts2_types::CardPartition::Regent => sts2_content::cards::regent::comparison_base_values(&s, ch),
            sts2_types::CardPartition::Necrobinder => sts2_content::cards::necrobinder::comparison_base_values(&s, ch),
            sts2_types::CardPartition::Fool => sts2_content::cards::fool::comparison_base_values(&s, ch),
            sts2_types::CardPartition::ColorlessSpecial => {
                sts2_content::cards::colorless_special::comparison_base_values(&s, ch)
            }
        }
        .unwrap();
        if !is_dyn {
            assert!(
                vals.is_empty(),
                "card {id:?} returned non-empty comparison values {vals:?} but is missing from card_has_dynamic_comparison_values"
            );
        }
    }
}
