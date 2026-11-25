use smallvec::SmallVec;
use std::borrow::Cow;
use std::collections::BTreeSet;
use std::sync::Arc;
use sts2_content::relics::batch_n_z;
use sts2_content::Sts2Content;
use sts2_kernel::commands::{arrow, block, card_pile, creature as creature_cmd, potion, power};
use sts2_kernel::content_dispatch::{CardPlayContext, CardPlayOutcome};
use sts2_kernel::continuation::StepOutcome;
use sts2_kernel::{
    ArrowSlotInst, BattleState, CardHandle, CardInst, CardTempFlags, CombatEngine,
    ContentDispatcher, CreatureHandle, CreatureInst, FieldMap, FieldValue, HookArgs,
    HookListener, HookResult, KernelError, MonsterInst, PlayerCounters, PlayerInst, RelicInst,
    RootStaticContext, SemanticHistory,
};
use sts2_types::{
    generated_card_meta, ArrowStatus, CardId, CardKeywords, CardMeta, CardRarity, CardStaticFlags,
    CardTags, CardType, CharacterId, CombatSide, Dec, EnchantmentId, HookMask, HookSlot,
    MonsterId, MonsterMeta, OrbId, OrbMeta, PileType, PlayerRngSet, PlayerTurnPhase, PotionId,
    PotionMeta, PowerId, PowerMeta, RelicId, RelicMeta, RelicStaticFlags, Rng, RoomType,
    RunRngSet, TargetType, ValueProp,
};

fn make_card(handle: u32, id: CardId, pile: PileType) -> CardInst {
    let meta = generated_card_meta(id);
    CardInst {
        handle: CardHandle(handle),
        id,
        upgrade_level: 0,
        canonical_base_energy_cost: meta.canonical_energy_cost as i16,
        base_energy_cost: meta.canonical_energy_cost as i16,
        captured_energy_x_value: 0,
        base_replay_count: 0,
        floor_added_to_deck: sts2_kernel::OptI16::NONE,
        deck_card_handle: sts2_kernel::OptCardHandle::NONE,
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
        pile,
        removed_from_state: false,
        transform_into_after_play: None,
    }
}

fn make_relic(id: RelicId) -> RelicInst {
    RelicInst {
        id,
        is_wax: false,
        is_melted: false,
        stack_count: 1,
        floor_added_to_deck: None,
        private_fields: FieldMap::new(),
    }
}

fn make_state(relics: Vec<RelicInst>, deck_cards: Vec<CardId>) -> BattleState {
    let run_rngs = RunRngSet::from_string_seed("RELIC_NZ_SEED");
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let mut cards = Vec::new();
    let mut deck = Vec::new();
    let mut next_h = 1u32;
    for cid in deck_cards {
        let ch = CardHandle(next_h);
        next_h += 1;
        cards.push(make_card(ch.0, cid, PileType::Deck));
        deck.push(ch);
    }

    let player_h = CreatureHandle(1);
    let enemy_h = CreatureHandle(2);
    let player_creature = CreatureInst {
        handle: player_h,
        combat_id: Some(1),
        side: CombatSide::Player,
        slot_name: None,
        is_player: true,
        pet_owner_player_index: None,
        monster_id: None,
        current_hp: 70,
        max_hp: 80,
        block: 0,
        cumulative_hp_lost: 0,
        is_detached: false,
        monster: None,
        has_minion_power: false,
        powers: Vec::new(),
    };
    let enemy_creature = CreatureInst {
        handle: enemy_h,
        combat_id: Some(2),
        side: CombatSide::Enemy,
        slot_name: Some(Cow::Borrowed("monster")),
        is_player: false,
        pet_owner_player_index: None,
        monster_id: Some(MonsterId::FuzzyWurmCrawler),
        current_hp: 100,
        max_hp: 100,
        block: 0,
        cumulative_hp_lost: 0,
        is_detached: false,
        monster: Some(Box::new(MonsterInst {
            monster_id: MonsterId::FuzzyWurmCrawler,
            rng: Rng::from_seed(run_rngs.seed ^ 99),
            is_performing_move: false,
            spawned_this_turn: false,
            current_state_id: Cow::Borrowed(""),
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

    BattleState {
        static_ctx: Arc::new(RootStaticContext {
            character_id: CharacterId::Ironclad,
            room_type: RoomType::Monster,
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
        next_creature_id: 3,
        initial_encounter_monster_count: 1,
        next_card_handle: next_h,
        next_creature_handle: 3,
        next_power_handle: 1,
        next_attack_id: 1,
        run_rngs,
        player_rngs,
        player: PlayerInst {
            creature_handle: player_h,
            current_hp: 70,
            max_hp: 80,
            block: 0,
            cumulative_hp_lost: 0,
            gold: 100,
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
            deck,
            potion_slots: smallvec::smallvec![None, None, None],
            combat_generated_potion_mask: 0,
            orb_capacity: 3,
            orbs: Vec::new(),
            arrows: Vec::new(),
            relics: relics.into_iter().collect(),
            card_or_potion_effect_depth: 0,
        },
        cards,
        card_extensions: Vec::new(),
        creatures: vec![player_creature, enemy_creature],
        power_extensions: Vec::new(),
        ally_handles: vec![player_h],
        enemy_handles: vec![enemy_h],
        escaped_handles: SmallVec::new(),
        removed_handles: SmallVec::new(),
        spawned_enemy_handles: SmallVec::new(),
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
    }
}

fn push_hand_card(state: &mut BattleState, id: CardId) -> CardHandle {
    let h = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    state.cards.push(make_card(h.0, id, PileType::Hand));
    state.player.hand.push(h);
    h
}

fn push_draw_card(state: &mut BattleState, id: CardId) -> CardHandle {
    let h = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    state.cards.push(make_card(h.0, id, PileType::Draw));
    state.player.draw.push(h);
    h
}

fn power_amt(state: &BattleState, creature: CreatureHandle, pid: PowerId) -> i32 {
    state
        .creature(creature)
        .and_then(|c| c.powers.iter().find(|p| p.id == pid))
        .map_or(0, |p| p.amount)
}

const ALL_N_Z_RELICS: [RelicId; 146] = [
    RelicId::NeowsBones,
    RelicId::NeowsSacrifice,
    RelicId::NeowsTalisman,
    RelicId::NeowsTorment,
    RelicId::NewLeaf,
    RelicId::NinjaScroll,
    RelicId::Nunchaku,
    RelicId::NutritiousOyster,
    RelicId::NutritiousSoup,
    RelicId::ObsidianVice,
    RelicId::Ocarina,
    RelicId::OddlySmoothStone,
    RelicId::OldCoin,
    RelicId::OrangeDough,
    RelicId::Orichalcum,
    RelicId::OrnamentalFan,
    RelicId::Orrery,
    RelicId::PaelsBlood,
    RelicId::PaelsClaw,
    RelicId::PaelsEye,
    RelicId::PaelsFlesh,
    RelicId::PaelsGrowth,
    RelicId::PaelsHorn,
    RelicId::PaelsLegion,
    RelicId::PaelsTears,
    RelicId::PaelsTooth,
    RelicId::PaelsWing,
    RelicId::PandorasBox,
    RelicId::Pantograph,
    RelicId::PaperKrane,
    RelicId::PaperPhrog,
    RelicId::ParryingShield,
    RelicId::Pear,
    RelicId::Pendulum,
    RelicId::PenNib,
    RelicId::Permafrost,
    RelicId::PetrifiedToad,
    RelicId::PhialHolster,
    RelicId::PhilosophersStone,
    RelicId::PhylacteryUnbound,
    RelicId::Planisphere,
    RelicId::Pocketwatch,
    RelicId::PollinousCore,
    RelicId::Pomander,
    RelicId::PotionBelt,
    RelicId::PowerCell,
    RelicId::PrayerWheel,
    RelicId::PrecariousShears,
    RelicId::PreciseScissors,
    RelicId::PreservedFog,
    RelicId::PrismaticGem,
    RelicId::PumpkinCandle,
    RelicId::PunchDagger,
    RelicId::RadiantPearl,
    RelicId::RainbowRing,
    RelicId::RazorTooth,
    RelicId::RedMask,
    RelicId::RedSkull,
    RelicId::Regalite,
    RelicId::RegalPillow,
    RelicId::ReptileTrinket,
    RelicId::RingingTriangle,
    RelicId::RingOfTheDrake,
    RelicId::RingOfTheSnake,
    RelicId::RippleBasin,
    RelicId::RoyalPoison,
    RelicId::RoyalStamp,
    RelicId::RuinedHelmet,
    RelicId::RunicCapacitor,
    RelicId::RunicPyramid,
    RelicId::Sai,
    RelicId::SandCastle,
    RelicId::ScreamingFlagon,
    RelicId::ScrollBoxes,
    RelicId::SealOfGold,
    RelicId::SeaGlass,
    RelicId::SelfFormingClay,
    RelicId::SereTalon,
    RelicId::Shovel,
    RelicId::Shuriken,
    RelicId::SignetRing,
    RelicId::SilkenTress,
    RelicId::SilverCrucible,
    RelicId::SlingOfCourage,
    RelicId::SmallCapsule,
    RelicId::SneckoEye,
    RelicId::SneckoSkull,
    RelicId::SongBow,
    RelicId::Sozu,
    RelicId::SparklingRouge,
    RelicId::SpikedGauntlets,
    RelicId::StoneCalendar,
    RelicId::StoneCracker,
    RelicId::StoneHumidifier,
    RelicId::Storybook,
    RelicId::Storyteller,
    RelicId::Strawberry,
    RelicId::StrikeDummy,
    RelicId::SturdyClamp,
    RelicId::SwordOfJade,
    RelicId::SwordOfStone,
    RelicId::SymbioticVirus,
    RelicId::TanxsWhistle,
    RelicId::TeaOfDiscourtesy,
    RelicId::TheAbacus,
    RelicId::TheBoot,
    RelicId::TheCourier,
    RelicId::ThrowingAxe,
    RelicId::Thumbscrew,
    RelicId::Tingsha,
    RelicId::TinyMailbox,
    RelicId::ToastyMittens,
    RelicId::Toolbox,
    RelicId::TormentousCollar,
    RelicId::TouchOfOrobas,
    RelicId::ToughBandages,
    RelicId::ToxicEgg,
    RelicId::ToyBox,
    RelicId::TriBoomerang,
    RelicId::TungstenRod,
    RelicId::TuningFork,
    RelicId::Twine,
    RelicId::TwistedFunnel,
    RelicId::UnceasingTop,
    RelicId::UndyingSigil,
    RelicId::UnsettlingLamp,
    RelicId::Vajra,
    RelicId::Vambrace,
    RelicId::VelvetChoker,
    RelicId::VenerableTeaSet,
    RelicId::VeryHotCocoa,
    RelicId::VexingPuzzlebox,
    RelicId::VitruvianMinion,
    RelicId::WarHammer,
    RelicId::WarPaint,
    RelicId::Whetstone,
    RelicId::WhisperingEarring,
    RelicId::WhiteBeastStatue,
    RelicId::WhiteStar,
    RelicId::WhittlingKnife,
    RelicId::WingedBoots,
    RelicId::WingCharm,
    RelicId::WongosMysteryTicket,
    RelicId::WongoCustomerAppreciationBadge,
    RelicId::Wuppy,
    RelicId::YummyCookie,
];

#[test]
fn all_146_n_z_relics_have_meta_and_flags() {
    let mut seen = BTreeSet::new();
    for id in ALL_N_Z_RELICS {
        assert!(seen.insert(id as usize), "duplicate relic {id:?}");
        assert!(
            id.model_entry().as_bytes()[0] >= b'N',
            "{id:?} is not N-Z"
        );
        let meta = Sts2Content
            .relic_meta(id)
            .unwrap_or_else(|e| panic!("missing relic_meta for {id:?}: {e}"));
        let mut state = make_state(vec![make_relic(id)], vec![]);
        // Ensure passive/no-op dispatch with HookArgs::None never returns Unsupported.
        let res = batch_n_z::dispatch_hook(
            &Sts2Content,
            &mut state,
            id,
            HookSlot::AfterCombatEnd,
            &HookArgs::None,
        );
        assert!(res.is_ok(), "unexpected error on {id:?}: {res:?}");
        if matches!(
            id,
            RelicId::PenNib
                | RelicId::Pocketwatch
                | RelicId::VelvetChoker
                | RelicId::OrnamentalFan
                | RelicId::Nunchaku
                | RelicId::Shuriken
                | RelicId::Vambrace
                | RelicId::UnceasingTop
                | RelicId::UnsettlingLamp
                | RelicId::PaelsEye
                | RelicId::PaelsLegion
        ) {
            assert!(
                meta.flags.contains(RelicStaticFlags::ORDER_SENSITIVE),
                "{id:?} must be ORDER_SENSITIVE"
            );
        }
    }
    assert_eq!(seen.len(), 146);

    assert!(Sts2Content
        .relic_meta(RelicId::Pantograph)
        .unwrap()
        .flags
        .contains(RelicStaticFlags::MAY_HEAL));
    assert!(Sts2Content
        .relic_meta(RelicId::Wuppy)
        .unwrap()
        .flags
        .contains(RelicStaticFlags::ADDS_PET | RelicStaticFlags::HAS_GROWTH));
    assert!(Sts2Content
        .relic_meta(RelicId::PaelsLegion)
        .unwrap()
        .flags
        .contains(RelicStaticFlags::ADDS_PET | RelicStaticFlags::ORDER_SENSITIVE));
    assert!(Sts2Content
        .relic_meta(RelicId::PaperKrane)
        .unwrap()
        .hook_mask
        .contains(HookSlot::ModifyWeakMultiplier));
    assert!(Sts2Content
        .relic_meta(RelicId::PaperPhrog)
        .unwrap()
        .hook_mask
        .contains(HookSlot::ModifyVulnerableMultiplier));
}

#[test]
fn counter_and_order_sensitive_relics_n_z() {
    // Nunchaku: 10th attack grants +1 energy
    let mut s = make_state(vec![make_relic(RelicId::Nunchaku)], vec![]);
    let atk = push_hand_card(&mut s, CardId::StrikeIronclad);
    for i in 1..=10 {
        let cp = CardPlayContext {
            card: atk,
            target: Some(CreatureHandle(2)),
            result_pile: PileType::Discard,
            is_auto_play: false,
            play_index: 0,
            play_count: 1,
            play_ordinal: i,
            energy_spent: 1,
            energy_value: 1,
            stars_spent: 0,
            star_value: 0,
        };
        sts2_kernel::hooks::after_card_played(&mut s, &Sts2Content, &cp).unwrap();
    }
    assert_eq!(s.player.energy, 4);
    assert_eq!(
        s.player.relics[0].private_fields.get("Nunchaku._attacksPlayed"),
        Some(&FieldValue::Int(10))
    );

    // OrnamentalFan & Shuriken: 3 attacks in one turn -> +4 block, +1 Strength; reset on turn start
    let mut s = make_state(
        vec![
            make_relic(RelicId::OrnamentalFan),
            make_relic(RelicId::Shuriken),
        ],
        vec![],
    );
    let atk = push_hand_card(&mut s, CardId::StrikeIronclad);
    for i in 1..=3 {
        let cp = CardPlayContext {
            card: atk,
            target: Some(CreatureHandle(2)),
            result_pile: PileType::Discard,
            is_auto_play: false,
            play_index: 0,
            play_count: 1,
            play_ordinal: i,
            energy_spent: 1,
            energy_value: 1,
            stars_spent: 0,
            star_value: 0,
        };
        sts2_kernel::hooks::after_card_played(&mut s, &Sts2Content, &cp).unwrap();
    }
    assert_eq!(s.player_creature().unwrap().block, 4);
    assert_eq!(power_amt(&s, CreatureHandle(1), PowerId::StrengthPower), 1);
    sts2_kernel::hooks::before_side_turn_start(
        &mut s,
        &Sts2Content,
        CombatSide::Player,
        &[CreatureHandle(1)],
    )
    .unwrap();
    assert_eq!(
        s.player.relics[0].private_fields.get("OrnamentalFan._attacksThisTurn"),
        Some(&FieldValue::Int(0))
    );
    assert_eq!(
        s.player.relics[1].private_fields.get("Shuriken._attacksThisTurn"),
        Some(&FieldValue::Int(0))
    );

    // TuningFork: 10th skill -> +7 block, wraps to 0
    let mut s = make_state(vec![make_relic(RelicId::TuningFork)], vec![]);
    s.player.relics[0]
        .private_fields
        .insert("TuningFork._skillsPlayed".to_owned(), FieldValue::Int(9));
    let skl = push_hand_card(&mut s, CardId::DefendIronclad);
    let cp = CardPlayContext {
        card: skl,
        target: None,
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
    sts2_kernel::hooks::after_card_played(&mut s, &Sts2Content, &cp).unwrap();
    assert_eq!(s.player_creature().unwrap().block, 7);
    assert_eq!(
        s.player.relics[0].private_fields.get("TuningFork._skillsPlayed"),
        Some(&FieldValue::Int(0))
    );

    // PenNib: 10th attack doubles powered attack damage on that play, then clears transient fields
    let mut s = make_state(vec![make_relic(RelicId::PenNib)], vec![]);
    s.player.relics[0]
        .private_fields
        .insert("PenNib._attacksPlayed".to_owned(), FieldValue::Int(9));
    let atk = push_hand_card(&mut s, CardId::StrikeIronclad);
    let cp = CardPlayContext {
        card: atk,
        target: Some(CreatureHandle(2)),
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
    sts2_kernel::hooks::before_card_played(&mut s, &Sts2Content, &cp).unwrap();
    let dmg = sts2_kernel::hooks::modify_damage(
        &mut s,
        &Sts2Content,
        Some(CreatureHandle(2)),
        Some(CreatureHandle(1)),
        Dec::from_i32(6),
        ValueProp::MOVE,
        Some(atk),
        Some(cp),
    )
    .unwrap();
    assert_eq!(dmg, Dec::from_i32(12));
    sts2_kernel::hooks::after_card_played(&mut s, &Sts2Content, &cp).unwrap();
    assert!(!s.player.relics[0]
        .private_fields
        .contains_key("PenNib._doubleDamagePlayOrdinal"));

    // VelvetChoker: +1 max energy, stops playing 7th card
    let mut s = make_state(vec![make_relic(RelicId::VelvetChoker)], vec![]);
    let max_e = sts2_kernel::hooks::modify_max_energy(&mut s, &Sts2Content, Dec::from_i32(3)).unwrap();
    assert_eq!(max_e, Dec::from_i32(4));
    s.player.counters.card_plays_started_this_turn = 5;
    assert!(sts2_kernel::hooks::should_play(&mut s, &Sts2Content, atk).unwrap());
    s.player.counters.card_plays_started_this_turn = 6;
    assert!(!sts2_kernel::hooks::should_play(&mut s, &Sts2Content, atk).unwrap());

    // RainbowRing: Attack + Skill + Power once per turn -> +1 Str, +1 Dex
    let mut s = make_state(vec![make_relic(RelicId::RainbowRing)], vec![]);
    let a = push_hand_card(&mut s, CardId::StrikeIronclad);
    let b = push_hand_card(&mut s, CardId::DefendIronclad);
    let p = push_hand_card(&mut s, CardId::Inflame);
    for (ord, h) in [(1, a), (2, b), (3, p), (4, a)] {
        let cp = CardPlayContext {
            card: h,
            target: None,
            result_pile: PileType::Discard,
            is_auto_play: false,
            play_index: 0,
            play_count: 1,
            play_ordinal: ord,
            energy_spent: 1,
            energy_value: 1,
            stars_spent: 0,
            star_value: 0,
        };
        sts2_kernel::hooks::after_card_played(&mut s, &Sts2Content, &cp).unwrap();
    }
    assert_eq!(power_amt(&s, CreatureHandle(1), PowerId::StrengthPower), 1);
    assert_eq!(power_amt(&s, CreatureHandle(1), PowerId::DexterityPower), 1);

    // ThrowingAxe: first card play count +1 once per combat
    let mut s = make_state(vec![make_relic(RelicId::ThrowingAxe)], vec![]);
    let (cnt1, mods) =
        sts2_kernel::hooks::modify_card_play_count(&mut s, &Sts2Content, a, None, 1)
            .unwrap();
    assert_eq!(cnt1, 2);
    sts2_kernel::hooks::after_modifying_card_play_count(&mut s, &Sts2Content, a, &mods)
        .unwrap();
    let (cnt2, _) =
        sts2_kernel::hooks::modify_card_play_count(&mut s, &Sts2Content, b, None, 1)
            .unwrap();
    assert_eq!(cnt2, 1);

    // Permafrost: first Power played -> +7 block
    let mut s = make_state(vec![make_relic(RelicId::Permafrost)], vec![]);
    let pwr = push_hand_card(&mut s, CardId::Inflame);
    let cp = CardPlayContext {
        card: pwr,
        target: None,
        result_pile: PileType::Exhaust,
        is_auto_play: false,
        play_index: 0,
        play_count: 1,
        play_ordinal: 1,
        energy_spent: 1,
        energy_value: 1,
        stars_spent: 0,
        star_value: 0,
    };
    sts2_kernel::hooks::after_card_played(&mut s, &Sts2Content, &cp).unwrap();
    sts2_kernel::hooks::after_card_played(&mut s, &Sts2Content, &cp).unwrap();
    assert_eq!(s.player_creature().unwrap().block, 7);
}

#[test]
fn turn_room_and_combat_relics_n_z() {
    // Pocketwatch: turn 1 no bonus even if last turn <= 3; turn 2 draws +3 if <=3 played turn 1
    let mut s = make_state(vec![make_relic(RelicId::Pocketwatch)], vec![]);
    let (d1, _) = sts2_kernel::hooks::modify_hand_draw(&mut s, &Sts2Content, Dec::from_i32(5)).unwrap();
    assert_eq!(d1, Dec::from_i32(5));
    let c1 = push_hand_card(&mut s, CardId::StrikeIronclad);
    let cp = CardPlayContext {
        card: c1,
        target: None,
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
    sts2_kernel::hooks::after_card_played(&mut s, &Sts2Content, &cp).unwrap();
    s.player.turn_number = 2;
    sts2_kernel::hooks::before_side_turn_start(
        &mut s,
        &Sts2Content,
        CombatSide::Player,
        &[CreatureHandle(1)],
    )
    .unwrap();
    let (d2, _) = sts2_kernel::hooks::modify_hand_draw(&mut s, &Sts2Content, Dec::from_i32(5)).unwrap();
    assert_eq!(d2, Dec::from_i32(8));

    // RingOfTheSnake, RingOfTheDrake, PaelsBlood, Pendulum, PollinousCore
    let mut s = make_state(
        vec![
            make_relic(RelicId::RingOfTheSnake),
            make_relic(RelicId::RingOfTheDrake),
            make_relic(RelicId::PaelsBlood),
        ],
        vec![],
    );
    let (draw_t1, _) =
        sts2_kernel::hooks::modify_hand_draw(&mut s, &Sts2Content, Dec::from_i32(5)).unwrap();
    assert_eq!(draw_t1, Dec::from_i32(10));
    s.player.turn_number = 2;
    let (draw_t2, _) =
        sts2_kernel::hooks::modify_hand_draw(&mut s, &Sts2Content, Dec::from_i32(5)).unwrap();
    assert_eq!(draw_t2, Dec::from_i32(8));

    let mut s = make_state(
        vec![
            make_relic(RelicId::Pendulum),
            make_relic(RelicId::PollinousCore),
        ],
        vec![],
    );
    for expected in [5, 5, 6, 7] {
        assert!(sts2_kernel::hooks::before_hand_draw(&mut s, &Sts2Content).unwrap().is_none());
        let (cnt, mods) =
            sts2_kernel::hooks::modify_hand_draw(&mut s, &Sts2Content, Dec::from_i32(5)).unwrap();
        sts2_kernel::hooks::after_modifying_hand_draw(&mut s, &Sts2Content, &mods).unwrap();
        assert_eq!(cnt, Dec::from_i32(expected));
    }

    // Orichalcum + RippleBasin + ScreamingFlagon + ObsidianVice + ParryingShield + Sai
    let mut s = make_state(vec![make_relic(RelicId::Orichalcum)], vec![]);
    sts2_kernel::hooks::before_side_turn_end_very_early(
        &mut s,
        &Sts2Content,
        CombatSide::Player,
        &[CreatureHandle(1)],
    )
    .unwrap();
    // Gaining block after BeforeSideTurnEndVeryEarly does NOT cancel Orichalcum!
    block::gain_block(&mut s, &Sts2Content, CreatureHandle(1), Dec::TWO, ValueProp::UNPOWERED, None, None).unwrap();
    sts2_kernel::hooks::before_side_turn_end(
        &mut s,
        &Sts2Content,
        CombatSide::Player,
        &[CreatureHandle(1)],
    )
    .unwrap();
    assert_eq!(s.player_creature().unwrap().block, 8);

    // StoneCalendar (turn 7 -> 52 AoE) & ScreamingFlagon (empty hand -> 20 AoE)
    let mut s = make_state(
        vec![
            make_relic(RelicId::StoneCalendar),
            make_relic(RelicId::ScreamingFlagon),
        ],
        vec![],
    );
    s.player.turn_number = 7;
    sts2_kernel::hooks::after_side_turn_start(
        &mut s,
        &Sts2Content,
        CombatSide::Player,
        &[CreatureHandle(1)],
    )
    .unwrap();
    sts2_kernel::hooks::before_side_turn_end(
        &mut s,
        &Sts2Content,
        CombatSide::Player,
        &[CreatureHandle(1)],
    )
    .unwrap();
    assert_eq!(s.creature(CreatureHandle(2)).unwrap().current_hp, 28);

    // SealOfGold, VeryHotCocoa, PaelsFlesh, PaelsTears, PumpkinCandle, VenerableTeaSet
    let mut s = make_state(
        vec![
            make_relic(RelicId::SealOfGold),
            make_relic(RelicId::VeryHotCocoa),
            make_relic(RelicId::PaelsFlesh),
            make_relic(RelicId::PaelsTears),
            make_relic(RelicId::PumpkinCandle),
            make_relic(RelicId::VenerableTeaSet),
        ],
        vec![],
    );
    s.player.relics[4]
        .private_fields
        .insert("PumpkinCandle.<KindleCount>k__BackingField".to_owned(), FieldValue::Int(2));
    s.player.relics[5]
        .private_fields
        .insert("VenerableTeaSet._isArmed".to_owned(), FieldValue::Bool(true));
    s.player.turn_number = 3;
    let me = sts2_kernel::hooks::modify_max_energy(&mut s, &Sts2Content, Dec::from_i32(3)).unwrap();
    assert_eq!(me, Dec::from_i32(5)); // PaelsFlesh + PumpkinCandle
    sts2_kernel::hooks::after_energy_reset(&mut s, &Sts2Content).unwrap();
    assert_eq!(s.player.energy, 5); // 3 + 2 from VenerableTeaSet
    sts2_kernel::hooks::before_side_turn_end(
        &mut s,
        &Sts2Content,
        CombatSide::Player,
        &[CreatureHandle(1)],
    )
    .unwrap();
    assert_eq!(
        s.player.relics[3].private_fields.get("PaelsTears._gainNextTurn"),
        Some(&FieldValue::Bool(true))
    );
    s.player.turn_number = 1;
    sts2_kernel::hooks::after_energy_reset(&mut s, &Sts2Content).unwrap();
    assert_eq!(s.player.energy, 5);
    sts2_kernel::hooks::after_side_turn_start(
        &mut s,
        &Sts2Content,
        CombatSide::Player,
        &[CreatureHandle(1)],
    )
    .unwrap();
    assert_eq!(s.player.energy, 12); // +2 PaelsTears + 1 SealOfGold + 4 VeryHotCocoa
    assert_eq!(s.player.gold, 97);
    sts2_kernel::hooks::after_combat_end(&mut s, &Sts2Content).unwrap();
    assert_eq!(
        s.player.relics[4]
            .private_fields
            .get("PumpkinCandle.<KindleCount>k__BackingField"),
        Some(&FieldValue::Int(1))
    );

    // Room / Start / Victory / Pet relics: Pantograph, SwordOfStone -> SwordOfJade, PhilosophersStone, PhylacteryUnbound, RunicCapacitor, SymbioticVirus, PetrifiedToad, Sozu, ReptileTrinket, SparklingRouge, RoyalPoison
    let mut s = make_state(vec![make_relic(RelicId::Pantograph)], vec![]);
    Arc::make_mut(&mut s.static_ctx).room_type = RoomType::Boss;
    sts2_kernel::hooks::before_combat_start(&mut s, &Sts2Content).unwrap();
    assert_eq!(s.player_creature().unwrap().current_hp, 80);

    let mut s = make_state(vec![make_relic(RelicId::SwordOfStone)], vec![]);
    Arc::make_mut(&mut s.static_ctx).room_type = RoomType::Elite;
    for _ in 0..5 {
        sts2_kernel::hooks::after_combat_victory(&mut s, &Sts2Content).unwrap();
    }
    assert_eq!(s.player.relics[0].id, RelicId::SwordOfJade);

    let mut s = make_state(
        vec![
            make_relic(RelicId::PhilosophersStone),
            make_relic(RelicId::OddlySmoothStone),
            make_relic(RelicId::Vajra),
            make_relic(RelicId::RingingTriangle),
            make_relic(RelicId::SongBow),
            make_relic(RelicId::Storyteller),
            make_relic(RelicId::RedMask),
            make_relic(RelicId::TwistedFunnel),
            make_relic(RelicId::PhylacteryUnbound),
            make_relic(RelicId::RunicCapacitor),
            make_relic(RelicId::SymbioticVirus),
            make_relic(RelicId::PetrifiedToad),
        ],
        vec![],
    );
    sts2_kernel::hooks::after_room_entered(&mut s, &Sts2Content).unwrap();
    sts2_kernel::hooks::before_combat_start(&mut s, &Sts2Content).unwrap();
    sts2_kernel::hooks::before_side_turn_start(
        &mut s,
        &Sts2Content,
        CombatSide::Player,
        &[CreatureHandle(1)],
    )
    .unwrap();
    sts2_kernel::hooks::after_side_turn_start(
        &mut s,
        &Sts2Content,
        CombatSide::Player,
        &[CreatureHandle(1)],
    )
    .unwrap();
    assert_eq!(power_amt(&s, CreatureHandle(2), PowerId::StrengthPower), 1);
    assert_eq!(power_amt(&s, CreatureHandle(2), PowerId::WeakPower), 1);
    assert_eq!(power_amt(&s, CreatureHandle(2), PowerId::PoisonPower), 4);
    assert_eq!(power_amt(&s, CreatureHandle(1), PowerId::DexterityPower), 1);
    assert_eq!(power_amt(&s, CreatureHandle(1), PowerId::StrengthPower), 1);
    assert_eq!(power_amt(&s, CreatureHandle(1), PowerId::RetainHandPower), 1);
    assert_eq!(power_amt(&s, CreatureHandle(1), PowerId::AimPower), 18);
    assert_eq!(s.player.orb_capacity, 6);
    assert_eq!(s.player.orbs.len(), 1);
    assert_eq!(s.player.orbs[0].id, OrbId::DarkOrb);
    assert_eq!(s.player.potion_slots[0], Some(PotionId::PotionShapedRock));
    assert!(s.is_osty_alive());

    // Sozu blocks potion procurement; ReptileTrinket grants 3 ReptileTrinketPower on potion use
    let mut s = make_state(vec![make_relic(RelicId::Sozu)], vec![]);
    assert!(!potion::try_to_procure(&mut s, &Sts2Content, PotionId::FirePotion).unwrap());
    let mut s = make_state(vec![make_relic(RelicId::ReptileTrinket)], vec![]);
    s.player.potion_slots[0] = Some(PotionId::BlockPotion);
    potion::use_potion(&mut s, &Sts2Content, 0, Some(CreatureHandle(1)), &mut SmallVec::new()).unwrap();
    assert_eq!(
        power_amt(&s, CreatureHandle(1), PowerId::ReptileTrinketPower),
        3
    );

    // SparklingRouge on turn 3 block clear & RoyalPoison on turn 1
    let mut s = make_state(
        vec![
            make_relic(RelicId::SparklingRouge),
            make_relic(RelicId::RoyalPoison),
        ],
        vec![],
    );
    let _ = sts2_kernel::hooks::after_player_turn_start(&mut s, &Sts2Content).unwrap();
    assert_eq!(s.player_creature().unwrap().current_hp, 66);
    s.player.turn_number = 3;
    sts2_kernel::hooks::after_block_cleared(&mut s, &Sts2Content, CreatureHandle(1)).unwrap();
    assert_eq!(power_amt(&s, CreatureHandle(1), PowerId::StrengthPower), 1);
    assert_eq!(power_amt(&s, CreatureHandle(1), PowerId::DexterityPower), 1);
}

#[test]
fn damage_block_power_and_card_modifiers_n_z() {
    // TungstenRod, TheBoot, StrikeDummy, PaperKrane, PaperPhrog, UndyingSigil, Thumbscrew
    let mut s = make_state(
        vec![
            make_relic(RelicId::TungstenRod),
            make_relic(RelicId::TheBoot),
            make_relic(RelicId::StrikeDummy),
            make_relic(RelicId::PaperKrane),
            make_relic(RelicId::PaperPhrog),
            make_relic(RelicId::UndyingSigil),
            make_relic(RelicId::Thumbscrew),
        ],
        vec![],
    );
    let strike = push_hand_card(&mut s, CardId::StrikeIronclad);
    let dmg_strike = sts2_kernel::hooks::modify_damage(
        &mut s,
        &Sts2Content,
        Some(CreatureHandle(2)),
        Some(CreatureHandle(1)),
        Dec::from_i32(6),
        ValueProp::MOVE,
        Some(strike),
        None,
    )
    .unwrap();
    assert_eq!(dmg_strike, Dec::from_i32(9));

    let vuln = sts2_kernel::hooks::modify_vulnerable_multiplier(
        &mut s,
        &Sts2Content,
        Some(CreatureHandle(2)),
        Some(CreatureHandle(1)),
        Dec::ONE_POINT_FIVE,
        ValueProp::MOVE,
        Some(strike),
    )
    .unwrap();
    assert_eq!(vuln, Dec::from_parts(175, 2).unwrap());

    let weak = sts2_kernel::hooks::modify_weak_multiplier(
        &mut s,
        &Sts2Content,
        Some(CreatureHandle(1)),
        Some(CreatureHandle(2)),
        Dec::from_parts(75, 2).unwrap(),
        ValueProp::MOVE,
        None,
    )
    .unwrap();
    assert_eq!(weak, Dec::from_parts(60, 2).unwrap());

    // UndyingSigil: halves enemy attack when enemy HP <= Doom
    power::apply(
        &mut s,
        &Sts2Content,
        PowerId::DoomPower,
        CreatureHandle(2),
        Dec::from_i32(100),
        Some(CreatureHandle(1)),
        None,
    )
    .unwrap();
    let halved = sts2_kernel::hooks::modify_damage(
        &mut s,
        &Sts2Content,
        Some(CreatureHandle(1)),
        Some(CreatureHandle(2)),
        Dec::from_i32(10),
        ValueProp::MOVE,
        None,
        None,
    )
    .unwrap();
    assert_eq!(halved, Dec::from_i32(5));

    // TungstenRod reduces HP loss to player by 1 (clamped >= 0)
    let (hp_loss_p, _) = sts2_kernel::hooks::modify_hp_lost(
        &mut s,
        &Sts2Content,
        CreatureHandle(1),
        Dec::from_i32(1),
        ValueProp::MOVE,
        Some(CreatureHandle(2)),
        None,
        sts2_kernel::hooks::HpLossPhase::AfterOsty,
    )
    .unwrap();
    assert_eq!(hp_loss_p, Dec::ZERO);

    // TheBoot raises 1..5 powered attack damage to 5
    let (hp_loss_e, _) = sts2_kernel::hooks::modify_hp_lost(
        &mut s,
        &Sts2Content,
        CreatureHandle(2),
        Dec::from_i32(2),
        ValueProp::MOVE,
        Some(CreatureHandle(1)),
        Some(strike),
        sts2_kernel::hooks::HpLossPhase::AfterOsty,
    )
    .unwrap();
    assert_eq!(hp_loss_e, Dec::from_i32(5));

    // Thumbscrew: multi-hit attack +1 hit, single-hit unchanged
    let info_single = sts2_kernel::content_dispatch::AttackContextInfo {
        attack_id: 1,
        attacker: CreatureHandle(1),
        target_side: CombatSide::Enemy,
        card_source: Some(strike),
        damage_props: ValueProp::MOVE,
        hit_count: 1,
    };
    assert_eq!(
        sts2_kernel::hooks::modify_attack_hit_count(&mut s, &Sts2Content, info_single, 1).unwrap(),
        1
    );
    let info_multi = sts2_kernel::content_dispatch::AttackContextInfo {
        hit_count: 2,
        ..info_single
    };
    assert_eq!(
        sts2_kernel::hooks::modify_attack_hit_count(&mut s, &Sts2Content, info_multi, 2).unwrap(),
        3
    );

    // SneckoEye, SneckoSkull, RuinedHelmet, RedSkull, SelfFormingClay, RunicPyramid, SturdyClamp, SpikedGauntlets
    let mut s = make_state(
        vec![
            make_relic(RelicId::SneckoEye),
            make_relic(RelicId::SneckoSkull),
            make_relic(RelicId::RuinedHelmet),
            make_relic(RelicId::RedSkull),
            make_relic(RelicId::SelfFormingClay),
            make_relic(RelicId::RunicPyramid),
            make_relic(RelicId::SturdyClamp),
            make_relic(RelicId::SpikedGauntlets),
        ],
        vec![],
    );
    sts2_kernel::hooks::after_room_entered(&mut s, &Sts2Content).unwrap();
    sts2_kernel::hooks::before_combat_start(&mut s, &Sts2Content).unwrap();
    assert_eq!(power_amt(&s, CreatureHandle(1), PowerId::ConfusedPower), 1);
    assert!(!sts2_kernel::hooks::should_flush(&mut s, &Sts2Content).unwrap());

    // RuinedHelmet doubles first positive Strength once
    power::apply(
        &mut s,
        &Sts2Content,
        PowerId::StrengthPower,
        CreatureHandle(1),
        Dec::TWO,
        Some(CreatureHandle(1)),
        None,
    )
    .unwrap();
    assert_eq!(power_amt(&s, CreatureHandle(1), PowerId::StrengthPower), 4);
    power::apply(
        &mut s,
        &Sts2Content,
        PowerId::StrengthPower,
        CreatureHandle(1),
        Dec::TWO,
        Some(CreatureHandle(1)),
        None,
    )
    .unwrap();
    assert_eq!(power_amt(&s, CreatureHandle(1), PowerId::StrengthPower), 6);

    // RedSkull toggles +3 when <= 50% HP and -3 when healed above 50%
    creature_cmd::set_current_hp(&mut s, &Sts2Content, CreatureHandle(1), Dec::from_i32(40)).unwrap();
    assert_eq!(power_amt(&s, CreatureHandle(1), PowerId::StrengthPower), 9);
    creature_cmd::heal(&mut s, &Sts2Content, CreatureHandle(1), Dec::from_i32(10)).unwrap();
    assert_eq!(power_amt(&s, CreatureHandle(1), PowerId::StrengthPower), 6);

    // SneckoSkull adds +1 Poison
    power::apply(
        &mut s,
        &Sts2Content,
        PowerId::PoisonPower,
        CreatureHandle(2),
        Dec::from_i32(3),
        Some(CreatureHandle(1)),
        None,
    )
    .unwrap();
    assert_eq!(power_amt(&s, CreatureHandle(2), PowerId::PoisonPower), 4);

    // SturdyClamp retains up to 10 block
    s.creature_mut(CreatureHandle(1)).unwrap().block = 15;
    s.sync_player_hp_to_creature();
    let prev = sts2_kernel::hooks::should_clear_block(&mut s, &Sts2Content, CreatureHandle(1))
        .unwrap()
        .expect("SturdyClamp prevents clear");
    sts2_kernel::hooks::after_preventing_block_clear(&mut s, &Sts2Content, prev, CreatureHandle(1))
        .unwrap();
    assert_eq!(s.player_creature().unwrap().block, 10);

    // SpikedGauntlets raises Power card cost by +1
    let pwr = push_hand_card(&mut s, CardId::Inflame);
    let cost =
        sts2_kernel::ordering::resolve_energy_cost(&mut s, &Sts2Content, pwr).unwrap();
    assert_eq!(cost, 2);

    // UnceasingTop, Tingsha, ToughBandages, TheAbacus, WhittlingKnife, Twine, RazorTooth, Regalite
    let mut s = make_state(
        vec![
            make_relic(RelicId::UnceasingTop),
            make_relic(RelicId::Tingsha),
            make_relic(RelicId::ToughBandages),
            make_relic(RelicId::TheAbacus),
            make_relic(RelicId::WhittlingKnife),
            make_relic(RelicId::Twine),
            make_relic(RelicId::RazorTooth),
            make_relic(RelicId::Regalite),
        ],
        vec![],
    );
    let _d = push_draw_card(&mut s, CardId::StrikeIronclad);
    s.player.phase = PlayerTurnPhase::Play;
    sts2_kernel::hooks::after_hand_emptied(&mut s, &Sts2Content).unwrap();
    assert_eq!(s.player.hand.len(), 1);
    let disc = s.player.hand[0];
    // Discarding the only card empties hand -> UnceasingTop draws -> shuffles Discard -> TheAbacus (+6) + WhittlingKnife (+5 Aim) + ToughBandages (+3) + Tingsha (3 dmg)
    sts2_kernel::commands::card::discard_single(&mut s, &Sts2Content, disc).unwrap();
    assert_eq!(s.player_creature().unwrap().block, 9);
    assert_eq!(s.creature(CreatureHandle(2)).unwrap().current_hp, 97);
    assert_eq!(power_amt(&s, CreatureHandle(1), PowerId::AimPower), 5);

    // Regalite: first card generated for combat per turn grants +4 block; RazorTooth upgrades played Attack/Skill
    let gen_card = card_pile::add_generated_card_to_combat(
        &mut s,
        &Sts2Content,
        CardId::StrikeIronclad,
        PileType::Hand,
        sts2_types::CardPilePosition::Bottom,
        0,
        Some(CreatureHandle(1)),
    )
    .unwrap();
    assert_eq!(s.player_creature().unwrap().block, 13);
    let cp_razor = CardPlayContext {
        card: gen_card,
        target: Some(CreatureHandle(2)),
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
    sts2_kernel::hooks::after_card_played(&mut s, &Sts2Content, &cp_razor).unwrap();
    assert_eq!(s.card(gen_card).unwrap().upgrade_level, 1);

    // Twine on arrow dislodge
    s.player.arrows.push(ArrowSlotInst {
        status: ArrowStatus::Away,
    });
    power::apply_new_with_extension(
        &mut s,
        &Sts2Content,
        PowerId::ImpaledPower,
        CreatureHandle(2),
        Dec::ONE,
        Some(CreatureHandle(1)),
        None,
        sts2_kernel::PowerExtension {
            impaled_arrows: smallvec::smallvec![0],
            ..Default::default()
        },
    )
    .unwrap();
    arrow::dislodge_first_arrow(
        &mut s,
        &Sts2Content,
        CreatureHandle(2),
        CreatureHandle(1),
        None,
    )
    .unwrap();
    assert_eq!(s.player_creature().unwrap().block, 17);

    // UnsettlingLamp: doubles first debuff card (even negative Strength) once, skips temporary internal power, skips Artifact
    let mut s = make_state(vec![make_relic(RelicId::UnsettlingLamp)], vec![]);
    sts2_kernel::hooks::before_combat_start(&mut s, &Sts2Content).unwrap();
    let debuff_card = push_hand_card(&mut s, CardId::Bash);
    let tmp_card = push_hand_card(&mut s, CardId::Neutralize);
    // Applying PiercingWailPower (ITemporaryPower -> StrengthPower): PiercingWailPower doubles (6 -> 12), internal StrengthPower does not re-double (-12 stays -12).
    power::apply(
        &mut s,
        &Sts2Content,
        PowerId::PiercingWailPower,
        CreatureHandle(2),
        Dec::from_i32(6),
        Some(CreatureHandle(1)),
        Some(tmp_card),
    )
    .unwrap();
    assert_eq!(
        power_amt(&s, CreatureHandle(2), PowerId::PiercingWailPower),
        12
    );
    assert_eq!(
        power_amt(&s, CreatureHandle(2), PowerId::StrengthPower),
        -12
    );
    let cp = CardPlayContext {
        card: tmp_card,
        target: Some(CreatureHandle(2)),
        result_pile: PileType::Exhaust,
        is_auto_play: false,
        play_index: 0,
        play_count: 1,
        play_ordinal: 1,
        energy_spent: 1,
        energy_value: 1,
        stars_spent: 0,
        star_value: 0,
    };
    sts2_kernel::hooks::after_card_played(&mut s, &Sts2Content, &cp).unwrap();
    // Subsequent debuff card is NOT doubled.
    power::apply(
        &mut s,
        &Sts2Content,
        PowerId::VulnerablePower,
        CreatureHandle(2),
        Dec::TWO,
        Some(CreatureHandle(1)),
        Some(debuff_card),
    )
    .unwrap();
    assert_eq!(power_amt(&s, CreatureHandle(2), PowerId::VulnerablePower), 2);
}

#[test]
fn card_gen_choices_extra_turn_and_merge_ready_helpers_n_z() {
    // NinjaScroll, Ocarina, OrangeDough, RadiantPearl, PowerCell, StoneCracker, VexingPuzzlebox, TeaOfDiscourtesy
    let mut s = make_state(
        vec![
            make_relic(RelicId::NinjaScroll),
            make_relic(RelicId::Ocarina),
            make_relic(RelicId::OrangeDough),
            make_relic(RelicId::RadiantPearl),
            make_relic(RelicId::PowerCell),
            make_relic(RelicId::StoneCracker),
            make_relic(RelicId::VexingPuzzlebox),
            make_relic(RelicId::TeaOfDiscourtesy),
        ],
        vec![],
    );
    push_draw_card(&mut s, CardId::Deflect);
    push_draw_card(&mut s, CardId::Slice);
    push_draw_card(&mut s, CardId::StrikeIronclad);
    sts2_kernel::hooks::after_room_entered(&mut s, &Sts2Content).unwrap();
    sts2_kernel::hooks::before_combat_start(&mut s, &Sts2Content).unwrap();
    assert_eq!(
        s.player.relics[7].private_fields.get("TeaOfDiscourtesy._combatsLeft"),
        Some(&FieldValue::Int(0))
    );
    sts2_kernel::hooks::before_side_turn_start(
        &mut s,
        &Sts2Content,
        CombatSide::Player,
        &[CreatureHandle(1)],
    )
    .unwrap();
    assert!(sts2_kernel::hooks::before_hand_draw(&mut s, &Sts2Content)
        .unwrap()
        .is_none());
    let _ = sts2_kernel::hooks::after_player_turn_start(&mut s, &Sts2Content).unwrap();
    sts2_kernel::hooks::after_side_turn_start(
        &mut s,
        &Sts2Content,
        CombatSide::Player,
        &[CreatureHandle(1)],
    )
    .unwrap();
    // 2 from PowerCell + 3 Shivs + 1 Ocarina + 1 Luminesce + 1 VexingPuzzlebox + 2 OrangeDough = 10 (hand cap)
    assert_eq!(s.player.hand.len(), 10);

    // Toolbox choice pause & resume via CombatEngine
    let mut s = make_state(
        vec![make_relic(RelicId::Toolbox)],
        vec![CardId::StrikeIronclad, CardId::DefendIronclad],
    );
    let outcome = CombatEngine::start_combat(&mut s, &Sts2Content).unwrap();
    let req = match outcome {
        StepOutcome::NeedsChoice(r) => r,
        other => panic!("expected Toolbox NeedsChoice, got {other:?}"),
    };
    assert_eq!(req.source_id, "RELIC.TOOLBOX");
    assert_eq!(req.candidates.len(), 3);
    let chosen_id = s.card(req.candidates[1]).unwrap().id;
    let res = CombatEngine::resume_choice(&mut s, &Sts2Content, &[1]).unwrap();
    assert_eq!(res, StepOutcome::Completed);
    assert!(s.player.hand.iter().any(|&h| s.card(h).unwrap().id == chosen_id));

    // ToastyMittens choice pause & resume via CombatEngine
    let mut s = make_state(
        vec![make_relic(RelicId::ToastyMittens)],
        vec![
            CardId::StrikeIronclad,
            CardId::StrikeIronclad,
            CardId::DefendIronclad,
            CardId::DefendIronclad,
            CardId::Bash,
        ],
    );
    let outcome = CombatEngine::start_combat(&mut s, &Sts2Content).unwrap();
    let req = match outcome {
        StepOutcome::NeedsChoice(r) => r,
        other => panic!("expected ToastyMittens NeedsChoice, got {other:?}"),
    };
    assert_eq!(req.source_id, "RELIC.TOASTY_MITTENS");
    let res = CombatEngine::resume_choice(&mut s, &Sts2Content, &[0]).unwrap();
    assert_eq!(res, StepOutcome::Completed);
    assert_eq!(s.player.exhaust.len(), 1);
    assert_eq!(power_amt(&s, CreatureHandle(1), PowerId::StrengthPower), 1);

    // PaelsEye extra turn when 0 manual cards played; suppressed on turn 1 by WhisperingEarring
    let mut s = make_state(
        vec![make_relic(RelicId::PaelsEye)],
        vec![CardId::StrikeIronclad, CardId::DefendIronclad],
    );
    assert_eq!(CombatEngine::start_combat(&mut s, &Sts2Content).unwrap(), StepOutcome::Completed);
    let _ = CombatEngine::end_player_turn(&mut s, &Sts2Content).unwrap();
    assert_eq!(s.player.turn_number, 2);
    assert!(s.is_player_extra_turn);
    assert_eq!(s.player.exhaust.len(), 2);

    let mut s_suppressed = make_state(
        vec![
            make_relic(RelicId::PaelsEye),
            make_relic(RelicId::WhisperingEarring),
        ],
        vec![CardId::StrikeIronclad],
    );
    assert_eq!(
        CombatEngine::start_combat(&mut s_suppressed, &Sts2Content).unwrap(),
        StepOutcome::Completed
    );
    let _ = CombatEngine::end_player_turn(&mut s_suppressed, &Sts2Content).unwrap();
    assert!(!s_suppressed.is_player_extra_turn);

    // WarHammer upgrades 4 deck cards on Elite victory; ToyBox melts wax relic every 3rd combat; Wuppy spawns pet & syncs HP; WongosMysteryTicket increments combats
    let mut s = make_state(
        vec![
            make_relic(RelicId::WarHammer),
            make_relic(RelicId::ToyBox),
            make_relic(RelicId::Wuppy),
            make_relic(RelicId::WongosMysteryTicket),
            RelicInst {
                is_wax: true,
                ..make_relic(RelicId::Vajra)
            },
        ],
        vec![
            CardId::StrikeIronclad,
            CardId::StrikeIronclad,
            CardId::DefendIronclad,
            CardId::DefendIronclad,
            CardId::Bash,
        ],
    );
    Arc::make_mut(&mut s.static_ctx).room_type = RoomType::Elite;
    s.player.relics[1]
        .private_fields
        .insert("ToyBox.<CombatsSeen>k__BackingField".to_owned(), FieldValue::Int(2));
    sts2_kernel::hooks::before_combat_start(&mut s, &Sts2Content).unwrap();
    let wuppy_h = s.pet_handles[0];
    assert_eq!(s.creature(wuppy_h).unwrap().max_hp, 60);
    creature_cmd::set_current_hp(&mut s, &Sts2Content, wuppy_h, Dec::from_i32(42))
        .unwrap();
    assert_eq!(
        s.player.relics[2].private_fields.get("Wuppy._currentHp"),
        Some(&FieldValue::Int(42))
    );
    sts2_kernel::hooks::after_combat_victory(&mut s, &Sts2Content).unwrap();
    sts2_kernel::hooks::after_combat_end(&mut s, &Sts2Content).unwrap();
    let upgraded_count = s
        .player
        .deck
        .iter()
        .filter(|&&h| s.card(h).unwrap().upgrade_level == 1)
        .count();
    assert_eq!(upgraded_count, 4);
    assert!(s.player.relics[4].is_melted);
    assert_eq!(
        s.player.relics[3]
            .private_fields
            .get("WongosMysteryTicket._combatsFinished"),
        Some(&FieldValue::Int(1))
    );

    // Vambrace & PaelsLegion direct helper tests (plus Unsupported guard when HookArgs::None arrives on e838bba3)
    let mut s = make_state(
        vec![
            make_relic(RelicId::Vambrace),
            make_relic(RelicId::PaelsLegion),
        ],
        vec![],
    );
    sts2_kernel::hooks::before_combat_start(&mut s, &Sts2Content).unwrap();
    assert_eq!(s.pet_handles.len(), 1);
    let def = push_hand_card(&mut s, CardId::DefendIronclad);
    let cp = CardPlayContext {
        card: def,
        target: None,
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
    let mult_args = HookArgs::ModifyBlock {
        target: CreatureHandle(1),
        amount: Dec::from_i32(5),
        props: ValueProp::MOVE,
        card_source: Some(def),
        card_play: Some(cp),
    };
    let v_mult = batch_n_z::dispatch_hook(
        &Sts2Content,
        &mut s,
        RelicId::Vambrace,
        HookSlot::ModifyBlockMultiplicative,
        &mult_args,
    )
    .unwrap();
    assert_eq!(v_mult.dec_val, Some(Dec::TWO));
    let p_mult = batch_n_z::dispatch_hook(
        &Sts2Content,
        &mut s,
        RelicId::PaelsLegion,
        HookSlot::ModifyBlockMultiplicative,
        &mult_args,
    )
    .unwrap();
    assert_eq!(p_mult.dec_val, Some(Dec::TWO));
    let mod_args = HookArgs::ModifyBlock {
        target: CreatureHandle(1),
        amount: Dec::from_i32(20),
        props: ValueProp::MOVE,
        card_source: Some(def),
        card_play: Some(cp),
    };
    batch_n_z::dispatch_hook(
        &Sts2Content,
        &mut s,
        RelicId::Vambrace,
        HookSlot::AfterModifyingBlockAmount,
        &mod_args,
    )
    .unwrap();
    batch_n_z::dispatch_hook(
        &Sts2Content,
        &mut s,
        RelicId::PaelsLegion,
        HookSlot::AfterModifyingBlockAmount,
        &mod_args,
    )
    .unwrap();
    sts2_kernel::hooks::after_card_played(&mut s, &Sts2Content, &cp).unwrap();
    assert_eq!(
        s.player.relics[0]
            .private_fields
            .get("Vambrace._blockGainedThisCombat"),
        Some(&FieldValue::Bool(true))
    );
    assert_eq!(
        s.player.relics[1].private_fields.get("PaelsLegion._cooldown"),
        Some(&FieldValue::Int(2))
    );

    // End-to-end CombatEngine tests for Vambrace, PaelsLegion, VitruvianMinion, WhisperingEarring
    let mut s_vam = make_state(vec![make_relic(RelicId::Vambrace)], vec![CardId::DefendIronclad, CardId::DefendIronclad]);
    CombatEngine::start_combat(&mut s_vam, &Sts2Content).unwrap();
    let d0 = s_vam.player.hand[0];
    let d1 = s_vam.player.hand[1];
    let mut empty_q = SmallVec::new();
    CombatEngine::play_card(&mut s_vam, &Sts2Content, d0, None, &mut empty_q).unwrap();
    assert_eq!(s_vam.player_creature().unwrap().block, 10);
    CombatEngine::play_card(&mut s_vam, &Sts2Content, d1, None, &mut empty_q).unwrap();
    assert_eq!(s_vam.player_creature().unwrap().block, 15);

    let mut s_leg = make_state(vec![make_relic(RelicId::PaelsLegion)], vec![CardId::DefendIronclad, CardId::DefendIronclad]);
    CombatEngine::start_combat(&mut s_leg, &Sts2Content).unwrap();
    let l0 = s_leg.player.hand[0];
    let l1 = s_leg.player.hand[1];
    CombatEngine::play_card(&mut s_leg, &Sts2Content, l0, None, &mut empty_q).unwrap();
    assert_eq!(s_leg.player_creature().unwrap().block, 10);
    CombatEngine::play_card(&mut s_leg, &Sts2Content, l1, None, &mut empty_q).unwrap();
    assert_eq!(s_leg.player_creature().unwrap().block, 15);
    assert_eq!(
        s_leg.player.relics[0].private_fields.get("PaelsLegion._cooldown"),
        Some(&FieldValue::Int(2))
    );

    // VitruvianMinion: under vanilla Sts2Content in v0.114.0, MinionStrike carries CardTags::MINION (2x -> 12),
    // while StrikeIronclad has no MINION tag (1x -> 6); when card_meta has CardTags::MINION, both ModifyDamageMultiplicative
    // and ModifyBlockMultiplicative return 2x.
    let mut s_vit = make_state(
        vec![make_relic(RelicId::VitruvianMinion)],
        vec![CardId::MinionStrike, CardId::StrikeIronclad],
    );
    CombatEngine::start_combat(&mut s_vit, &Sts2Content).unwrap();
    let minion_h = *s_vit.player.hand.iter().find(|&&h| s_vit.card(h).unwrap().id == CardId::MinionStrike).unwrap();
    let strike_h = *s_vit.player.hand.iter().find(|&&h| s_vit.card(h).unwrap().id == CardId::StrikeIronclad).unwrap();
    let enemy_h = s_vit.enemy_handles[0];
    CombatEngine::play_card(&mut s_vit, &Sts2Content, minion_h, Some(enemy_h), &mut empty_q).unwrap();
    assert_eq!(s_vit.creature(enemy_h).unwrap().current_hp, 100 - 12);
    CombatEngine::play_card(&mut s_vit, &Sts2Content, strike_h, Some(enemy_h), &mut empty_q).unwrap();
    assert_eq!(s_vit.creature(enemy_h).unwrap().current_hp, 100 - 12 - 6);

    static MINION_TAG_CARD_META: CardMeta = CardMeta {
        card_type: CardType::Attack,
        rarity: CardRarity::Token,
        target_type: TargetType::AnyEnemy,
        canonical_energy_cost: 0,
        canonical_star_cost: -1,
        max_upgrade_level: 1,
        base_keywords: CardKeywords::EXHAUST,
        base_tags: CardTags::MINION,
        flags: CardStaticFlags::empty(),
        hook_mask: HookMask::EMPTY,
    };
    struct MinionTagContent;
    impl ContentDispatcher for MinionTagContent {
        fn card_meta(&self, id: CardId) -> Result<&'static CardMeta, KernelError> {
            if id == CardId::MinionStrike {
                Ok(&MINION_TAG_CARD_META)
            } else {
                Sts2Content.card_meta(id)
            }
        }
        fn power_meta(&self, id: PowerId) -> Result<&'static PowerMeta, KernelError> { Sts2Content.power_meta(id) }
        fn relic_meta(&self, id: RelicId) -> Result<&'static RelicMeta, KernelError> { Sts2Content.relic_meta(id) }
        fn potion_meta(&self, id: PotionId) -> Result<&'static PotionMeta, KernelError> { Sts2Content.potion_meta(id) }
        fn monster_meta(&self, id: MonsterId) -> Result<&'static MonsterMeta, KernelError> { Sts2Content.monster_meta(id) }
        fn orb_meta(&self, id: OrbId) -> Result<&'static OrbMeta, KernelError> { Sts2Content.orb_meta(id) }
        fn card_is_playable(&self, state: &BattleState, card: CardHandle) -> Result<bool, KernelError> { Sts2Content.card_is_playable(state, card) }
        fn card_on_play(&self, state: &mut BattleState, ctx: &CardPlayContext, choice_queue: &mut SmallVec<[Vec<usize>; 2]>, resume_step: u8) -> Result<CardPlayOutcome, KernelError> { Sts2Content.card_on_play(state, ctx, choice_queue, resume_step) }
        fn card_on_play_resume_after_select(&self, state: &mut BattleState, ctx: &CardPlayContext, selected: &[CardHandle], resume_step: u8) -> Result<CardPlayOutcome, KernelError> { Sts2Content.card_on_play_resume_after_select(state, ctx, selected, resume_step) }
        fn card_on_upgrade(&self, state: &mut BattleState, card: CardHandle) -> Result<(), KernelError> { Sts2Content.card_on_upgrade(state, card) }
        fn card_comparison_base_values(&self, state: &BattleState, card: CardHandle) -> Result<SmallVec<[(&'static str, Dec); 4]>, KernelError> { Sts2Content.card_comparison_base_values(state, card) }
        fn monster_initial_hp_range(&self, state: &BattleState, monster_id: MonsterId) -> Result<(i32, i32), KernelError> { Sts2Content.monster_initial_hp_range(state, monster_id) }
        fn monster_setup_for_combat(&self, state: &mut BattleState, creature: CreatureHandle) -> Result<(), KernelError> { Sts2Content.monster_setup_for_combat(state, creature) }
        fn monster_after_added_to_room(&self, state: &mut BattleState, creature: CreatureHandle) -> Result<(), KernelError> { Sts2Content.monster_after_added_to_room(state, creature) }
        fn monster_roll_move(&self, state: &mut BattleState, creature: CreatureHandle) -> Result<(), KernelError> { Sts2Content.monster_roll_move(state, creature) }
        fn monster_perform_move(&self, state: &mut BattleState, creature: CreatureHandle) -> Result<(), KernelError> { Sts2Content.monster_perform_move(state, creature) }
        fn orb_passive(&self, state: &mut BattleState, orb_idx: usize, target: Option<CreatureHandle>) -> Result<SmallVec<[CreatureHandle; 4]>, KernelError> { Sts2Content.orb_passive(state, orb_idx, target) }
        fn orb_evoke(&self, state: &mut BattleState, orb_idx: usize) -> Result<SmallVec<[CreatureHandle; 4]>, KernelError> { Sts2Content.orb_evoke(state, orb_idx) }
        fn orb_before_turn_end(&self, state: &mut BattleState, orb_idx: usize) -> Result<(), KernelError> { Sts2Content.orb_before_turn_end(state, orb_idx) }
        fn orb_after_turn_start(&self, state: &mut BattleState, orb_idx: usize) -> Result<(), KernelError> { Sts2Content.orb_after_turn_start(state, orb_idx) }
        fn potion_on_use(&self, state: &mut BattleState, potion_id: PotionId, target: Option<CreatureHandle>, choice_queue: &mut SmallVec<[Vec<usize>; 2]>, resume_step: u8) -> Result<CardPlayOutcome, KernelError> { Sts2Content.potion_on_use(state, potion_id, target, choice_queue, resume_step) }
        fn potion_on_use_resume_after_select(&self, state: &mut BattleState, potion_id: PotionId, target: Option<CreatureHandle>, selected: &[CardHandle], resume_step: u8) -> Result<CardPlayOutcome, KernelError> { Sts2Content.potion_on_use_resume_after_select(state, potion_id, target, selected, resume_step) }
        fn resume_hook_choice(&self, state: &mut BattleState, listener: HookListener, slot: HookSlot, selected: &[CardHandle]) -> Result<HookResult, KernelError> { Sts2Content.resume_hook_choice(state, listener, slot, selected) }
        fn enchantment_on_play(&self, state: &mut BattleState, card: CardHandle, ctx: &CardPlayContext) -> Result<(), KernelError> { Sts2Content.enchantment_on_play(state, card, ctx) }
        fn affliction_on_play(&self, state: &mut BattleState, card: CardHandle, ctx: &CardPlayContext) -> Result<(), KernelError> { Sts2Content.affliction_on_play(state, card, ctx) }
        fn enchantment_play_count(&self, state: &BattleState, card: CardHandle, play_count: i32) -> Result<i32, KernelError> { Sts2Content.enchantment_play_count(state, card, play_count) }
        fn enchantment_damage_additive(&self, state: &BattleState, card: CardHandle, amount: Dec, props: ValueProp) -> Result<Dec, KernelError> { Sts2Content.enchantment_damage_additive(state, card, amount, props) }
        fn enchantment_damage_multiplicative(&self, state: &BattleState, card: CardHandle, amount: Dec, props: ValueProp) -> Result<Dec, KernelError> { Sts2Content.enchantment_damage_multiplicative(state, card, amount, props) }
        fn enchantment_should_start_at_bottom(&self, id: EnchantmentId) -> Result<bool, KernelError> { Sts2Content.enchantment_should_start_at_bottom(id) }
        fn enchantment_modify_shuffle_order(&self, state: &BattleState, card: CardHandle, cards: &mut [CardHandle], is_initial_shuffle: bool) -> Result<(), KernelError> { Sts2Content.enchantment_modify_shuffle_order(state, card, cards, is_initial_shuffle) }
        fn listener_hook_mask(&self, state: &BattleState, listener: HookListener) -> Result<HookMask, KernelError> { Sts2Content.listener_hook_mask(state, listener) }
        fn dispatch_hook(&self, state: &mut BattleState, listener: HookListener, slot: HookSlot, args: &HookArgs<'_>) -> Result<HookResult, KernelError> { Sts2Content.dispatch_hook(state, listener, slot, args) }
    }
    let mut s_vit_tag = make_state(vec![make_relic(RelicId::VitruvianMinion)], vec![]);
    let mh = push_hand_card(&mut s_vit_tag, CardId::MinionStrike);
    let dmg_res = batch_n_z::dispatch_hook(
        &MinionTagContent,
        &mut s_vit_tag,
        RelicId::VitruvianMinion,
        HookSlot::ModifyDamageMultiplicative,
        &HookArgs::ModifyDamage {
            target: Some(CreatureHandle(2)),
            dealer: Some(CreatureHandle(1)),
            amount: Dec::from_i32(6),
            props: ValueProp::MOVE,
            card_source: Some(mh),
            card_play: None,
        },
    )
    .unwrap();
    assert_eq!(dmg_res.dec_val, Some(Dec::TWO));
    let blk_res = batch_n_z::dispatch_hook(
        &MinionTagContent,
        &mut s_vit_tag,
        RelicId::VitruvianMinion,
        HookSlot::ModifyBlockMultiplicative,
        &HookArgs::ModifyBlock {
            target: CreatureHandle(1),
            amount: Dec::from_i32(7),
            props: ValueProp::MOVE,
            card_source: Some(mh),
            card_play: None,
        },
    )
    .unwrap();
    assert_eq!(blk_res.dec_val, Some(Dec::TWO));

    // WhisperingEarring: +1 max energy + Turn-1 autoplay + VakuuCardSelector auto-selects Survivor discard!
    let mut s_we = make_state(
        vec![
            make_relic(RelicId::WhisperingEarring),
            make_relic(RelicId::RingOfTheSnake),
        ],
        vec![CardId::Survivor; 7],
    );
    CombatEngine::start_combat(&mut s_we, &Sts2Content).unwrap();
    assert_eq!(s_we.player.max_energy, 3);
    assert_eq!(s_we.player.energy, 0);
    assert_eq!(s_we.vakuu_auto_select_depth, 0);
    assert_eq!(s_we.player.counters.cards_discarded_this_turn, 3);
    assert_eq!(s_we.player_creature().unwrap().block, 32); // 4 Survivors * 8 block
}
