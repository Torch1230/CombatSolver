use smallvec::SmallVec;
use std::borrow::Cow;
use std::sync::LazyLock;
use sts2_kernel::commands::{
    autoplay, block, card, card_factory, card_pile, creature, damage, forge, gold, melody, orb,
    osty, potion, power,
};
use sts2_kernel::{
    hooks, ordering, BattleState, CardHandle, CardInst, CardTempFlags, ChoiceRequest, ChoiceTiming,
    ContentDispatcher, CreatureHandle, FieldValue, HookArgs, HookResult, KernelError,
    LocalEnergyModifier, RelicInst,
};
use sts2_types::{
    generated_card_meta, generated_ids::RELIC_META_TABLE, CardId, CardKeywords, CardPilePosition,
    CardRarity, CardStaticFlags, CardTags, CardType, CombatSide, Dec, HookSlot,
    LocalEnergyModifierKind, MonsterId, OrbId, PileType, PowerId, RelicId, RelicMeta,
    RelicStaticFlags, RoomType, RunRngType, ValueProp,
};

const AM_RELIC_COUNT: usize = 169;
const CHOICES_PARADOX_OFFER_MARKER: &str = "__choices_paradox_offer";

const fn extra_relic_flags(id: RelicId) -> RelicStaticFlags {
    let mut flags = RelicStaticFlags::empty();
    if matches!(
        id,
        RelicId::BathRobes
            | RelicId::BathTowel
            | RelicId::BigMushroom
            | RelicId::BlackBlood
            | RelicId::BloodVial
            | RelicId::BookOfFiveRings
            | RelicId::BookRepairKnife
            | RelicId::BurningBlood
            | RelicId::ChosenCheese
            | RelicId::DarkstonePeriapt
            | RelicId::DemonTongue
            | RelicId::DragonFruit
            | RelicId::EternalFeather
            | RelicId::FakeBloodVial
            | RelicId::FakeLeesWaffle
            | RelicId::FakeMango
            | RelicId::LeesWaffle
            | RelicId::LizardTail
            | RelicId::LoomingFruit
            | RelicId::Mango
            | RelicId::MealTicket
            | RelicId::MeatOnTheBone
    ) {
        flags = flags.union(RelicStaticFlags::MAY_HEAL);
    }
    if matches!(
        id,
        RelicId::AmethystAubergine
            | RelicId::BlackStar
            | RelicId::BookOfFiveRings
            | RelicId::BowlerHat
            | RelicId::Byrdpip
            | RelicId::ChosenCheese
            | RelicId::DarkstonePeriapt
            | RelicId::DragonFruit
            | RelicId::FakeHappyFlower
            | RelicId::FishingRod
            | RelicId::Girya
            | RelicId::HappyFlower
            | RelicId::IronClub
            | RelicId::JossPaper
            | RelicId::LastingCandy
            | RelicId::LavaRock
            | RelicId::LuckyFysh
            | RelicId::MawBank
    ) {
        flags = flags.union(RelicStaticFlags::HAS_GROWTH);
    }
    if matches!(
        id,
        RelicId::Akabeko
            | RelicId::ArtOfWar
            | RelicId::BeatingRemnant
            | RelicId::Bookmark
            | RelicId::BrilliantScarf
            | RelicId::BurningSticks
            | RelicId::CentennialPuzzle
            | RelicId::ConductorsBaton
            | RelicId::DemonTongue
            | RelicId::EmotionChip
            | RelicId::FakeHappyFlower
            | RelicId::FakeOrichalcum
            | RelicId::GalacticDust
            | RelicId::HappyFlower
            | RelicId::HistoryCourse
            | RelicId::IronClub
            | RelicId::JossPaper
            | RelicId::Kunai
            | RelicId::Kusarigama
            | RelicId::LavaLamp
            | RelicId::LetterOpener
            | RelicId::Metronome
            | RelicId::MiniRegent
            | RelicId::MummifiedHand
            | RelicId::MusicBox
    ) {
        flags = flags.union(RelicStaticFlags::ORDER_SENSITIVE);
    }
    if matches!(
        id,
        RelicId::AlchemicalCoffer
            | RelicId::ArcaneScroll
            | RelicId::Astrolabe
            | RelicId::BigHat
            | RelicId::Bookmark
            | RelicId::ChoicesParadox
            | RelicId::Crossbow
            | RelicId::DelicateFrond
            | RelicId::DistinguishedCape
            | RelicId::DustyTome
            | RelicId::FishingRod
            | RelicId::ForgottenSoul
            | RelicId::FragrantMushroom
            | RelicId::FurCoat
            | RelicId::JeweledMask
            | RelicId::Kaleidoscope
            | RelicId::Kusarigama
            | RelicId::LeadPaperweight
            | RelicId::LeafyPoultice
            | RelicId::MummifiedHand
    ) {
        flags = flags.union(RelicStaticFlags::USES_RNG);
    }
    flags
}

const fn relic_id_from_am_index(idx: usize) -> Option<RelicId> {
    match idx {
        0 => Some(RelicId::Akabeko),
        1 => Some(RelicId::AlchemicalCoffer),
        2 => Some(RelicId::AmethystAubergine),
        3 => Some(RelicId::Anchor),
        4 => Some(RelicId::ArcaneScroll),
        5 => Some(RelicId::ArchaicTooth),
        6 => Some(RelicId::ArtOfWar),
        7 => Some(RelicId::Astrolabe),
        8 => Some(RelicId::BagOfMarbles),
        9 => Some(RelicId::BagOfPreparation),
        10 => Some(RelicId::BathRobes),
        11 => Some(RelicId::BathTowel),
        12 => Some(RelicId::BeatingRemnant),
        13 => Some(RelicId::BeautifulBracelet),
        14 => Some(RelicId::Bellows),
        15 => Some(RelicId::BeltBuckle),
        16 => Some(RelicId::BigHat),
        17 => Some(RelicId::BigMushroom),
        18 => Some(RelicId::BiiigHug),
        19 => Some(RelicId::BingBong),
        20 => Some(RelicId::BizarreLamp),
        21 => Some(RelicId::BlackBlood),
        22 => Some(RelicId::BlackStar),
        23 => Some(RelicId::BlessedAntler),
        24 => Some(RelicId::BloodSoakedRose),
        25 => Some(RelicId::BloodVial),
        26 => Some(RelicId::BoneFlute),
        27 => Some(RelicId::BoneTea),
        28 => Some(RelicId::Bookmark),
        29 => Some(RelicId::BookOfFiveRings),
        30 => Some(RelicId::BookRepairKnife),
        31 => Some(RelicId::BoomingConch),
        32 => Some(RelicId::BoundPhylactery),
        33 => Some(RelicId::BowlerHat),
        34 => Some(RelicId::Bread),
        35 => Some(RelicId::BrilliantScarf),
        36 => Some(RelicId::Brimstone),
        37 => Some(RelicId::BronzeScales),
        38 => Some(RelicId::BurningBlood),
        39 => Some(RelicId::BurningSticks),
        40 => Some(RelicId::Byrdine),
        41 => Some(RelicId::Byrdpip),
        42 => Some(RelicId::CallingBell),
        43 => Some(RelicId::Candelabra),
        44 => Some(RelicId::CaptainsWheel),
        45 => Some(RelicId::Cauldron),
        46 => Some(RelicId::CentennialPuzzle),
        47 => Some(RelicId::Chandelier),
        48 => Some(RelicId::CharonsAshes),
        49 => Some(RelicId::ChemicalX),
        50 => Some(RelicId::ChoicesParadox),
        51 => Some(RelicId::ChosenCheese),
        52 => Some(RelicId::Circlet),
        53 => Some(RelicId::Claws),
        54 => Some(RelicId::CloakClasp),
        55 => Some(RelicId::ConductorsBaton),
        56 => Some(RelicId::CrackedCore),
        57 => Some(RelicId::Crossbow),
        58 => Some(RelicId::CursedPearl),
        59 => Some(RelicId::DarkstonePeriapt),
        60 => Some(RelicId::DataDisk),
        61 => Some(RelicId::DaughterOfTheWind),
        62 => Some(RelicId::DelicateFrond),
        63 => Some(RelicId::DemonTongue),
        64 => Some(RelicId::DiamondDiadem),
        65 => Some(RelicId::DingyRug),
        66 => Some(RelicId::DistinguishedCape),
        67 => Some(RelicId::DivineDestiny),
        68 => Some(RelicId::DivineRight),
        69 => Some(RelicId::DollysMirror),
        70 => Some(RelicId::DowsingRod),
        71 => Some(RelicId::DragonFruit),
        72 => Some(RelicId::DreamCatcher),
        73 => Some(RelicId::Driftwood),
        74 => Some(RelicId::DustyTome),
        75 => Some(RelicId::Ectoplasm),
        76 => Some(RelicId::ElectricShrymp),
        77 => Some(RelicId::EmberTea),
        78 => Some(RelicId::EmotionChip),
        79 => Some(RelicId::EmptyCage),
        80 => Some(RelicId::EternalFeather),
        81 => Some(RelicId::ExerciseMat),
        82 => Some(RelicId::FakeAnchor),
        83 => Some(RelicId::FakeBloodVial),
        84 => Some(RelicId::FakeHappyFlower),
        85 => Some(RelicId::FakeLeesWaffle),
        86 => Some(RelicId::FakeMango),
        87 => Some(RelicId::FakeMerchantsRug),
        88 => Some(RelicId::FakeOrichalcum),
        89 => Some(RelicId::FakeSneckoEye),
        90 => Some(RelicId::FakeStrikeDummy),
        91 => Some(RelicId::FakeVenerableTeaSet),
        92 => Some(RelicId::FencingManual),
        93 => Some(RelicId::Fertilizer),
        94 => Some(RelicId::FestivePopper),
        95 => Some(RelicId::Fiddle),
        96 => Some(RelicId::FishingRod),
        97 => Some(RelicId::ForgottenSoul),
        98 => Some(RelicId::FragrantMushroom),
        99 => Some(RelicId::FresnelLens),
        100 => Some(RelicId::FrozenEgg),
        101 => Some(RelicId::FuneraryMask),
        102 => Some(RelicId::FurCoat),
        103 => Some(RelicId::GalacticDust),
        104 => Some(RelicId::GamblingChip),
        105 => Some(RelicId::GamePiece),
        106 => Some(RelicId::GhostSeed),
        107 => Some(RelicId::Girya),
        108 => Some(RelicId::GlassEye),
        109 => Some(RelicId::Glitter),
        110 => Some(RelicId::GnarledHammer),
        111 => Some(RelicId::GoldenCompass),
        112 => Some(RelicId::GoldenPearl),
        113 => Some(RelicId::GoldPlatedCables),
        114 => Some(RelicId::Gorget),
        115 => Some(RelicId::GremlinHorn),
        116 => Some(RelicId::HandDrill),
        117 => Some(RelicId::HappyFlower),
        118 => Some(RelicId::HeftyTablet),
        119 => Some(RelicId::HelicalDart),
        120 => Some(RelicId::HistoryCourse),
        121 => Some(RelicId::HornCleat),
        122 => Some(RelicId::IceCream),
        123 => Some(RelicId::InfusedCore),
        124 => Some(RelicId::IntimidatingHelmet),
        125 => Some(RelicId::IronClub),
        126 => Some(RelicId::IvoryTile),
        127 => Some(RelicId::JeweledMask),
        128 => Some(RelicId::JewelryBox),
        129 => Some(RelicId::JossPaper),
        130 => Some(RelicId::JuzuBracelet),
        131 => Some(RelicId::Kaleidoscope),
        132 => Some(RelicId::Kifuda),
        133 => Some(RelicId::Kunai),
        134 => Some(RelicId::Kusarigama),
        135 => Some(RelicId::Lantern),
        136 => Some(RelicId::LargeCapsule),
        137 => Some(RelicId::LastingCandy),
        138 => Some(RelicId::LavaLamp),
        139 => Some(RelicId::LavaRock),
        140 => Some(RelicId::LeadPaperweight),
        141 => Some(RelicId::LeafyPoultice),
        142 => Some(RelicId::LeesWaffle),
        143 => Some(RelicId::LetterOpener),
        144 => Some(RelicId::LizardTail),
        145 => Some(RelicId::LoomingFruit),
        146 => Some(RelicId::LordsParasol),
        147 => Some(RelicId::LostCoffer),
        148 => Some(RelicId::LostWisp),
        149 => Some(RelicId::LuckyFysh),
        150 => Some(RelicId::LunarPastry),
        151 => Some(RelicId::Mango),
        152 => Some(RelicId::MassiveScroll),
        153 => Some(RelicId::MawBank),
        154 => Some(RelicId::MealTicket),
        155 => Some(RelicId::MeatCleaver),
        156 => Some(RelicId::MeatOnTheBone),
        157 => Some(RelicId::MembershipCard),
        158 => Some(RelicId::MercuryHourglass),
        159 => Some(RelicId::Metronome),
        160 => Some(RelicId::MightyWings),
        161 => Some(RelicId::MiniatureCannon),
        162 => Some(RelicId::MiniatureTent),
        163 => Some(RelicId::MiniRegent),
        164 => Some(RelicId::MoltenEgg),
        165 => Some(RelicId::MrStruggles),
        166 => Some(RelicId::MummifiedHand),
        167 => Some(RelicId::MusicBox),
        168 => Some(RelicId::MysticLighter),
        _ => None,
    }
}

static AM_RELIC_META: LazyLock<[RelicMeta; AM_RELIC_COUNT]> = LazyLock::new(|| {
    std::array::from_fn(|i| {
        let mut meta = RELIC_META_TABLE[i];
        if let Some(id) = relic_id_from_am_index(i) {
            meta.flags |= extra_relic_flags(id);
        }
        meta
    })
});

#[inline(always)]
pub fn relic_meta(id: RelicId) -> Result<&'static RelicMeta, KernelError> {
    let idx = id as usize;
    if idx < AM_RELIC_COUNT {
        Ok(&AM_RELIC_META[idx])
    } else {
        Err(KernelError::Unsupported {
            kind: "Relic",
            id: id.model_id_str().to_owned(),
        })
    }
}

#[inline]
fn relic_inst(state: &BattleState, id: RelicId) -> Option<&RelicInst> {
    state.player.relics.iter().find(|r| r.id == id && !r.is_melted)
}

#[inline]
fn relic_inst_mut(state: &mut BattleState, id: RelicId) -> Option<&mut RelicInst> {
    state
        .player
        .relics
        .iter_mut()
        .find(|r| r.id == id && !r.is_melted)
}

fn get_bool(state: &BattleState, id: RelicId, key: &str, default: bool) -> bool {
    relic_inst(state, id)
        .and_then(|r| r.private_fields.get(key))
        .and_then(|v| match v {
            FieldValue::Bool(b) => Some(*b),
            _ => None,
        })
        .unwrap_or(default)
}

fn set_bool(state: &mut BattleState, id: RelicId, key: &'static str, val: bool) {
    if let Some(r) = relic_inst_mut(state, id) {
        r.private_fields
            .insert(Cow::Borrowed(key), FieldValue::Bool(val));
    }
}

fn get_i32(state: &BattleState, id: RelicId, key: &str, default: i32) -> i32 {
    relic_inst(state, id)
        .and_then(|r| r.private_fields.get(key))
        .and_then(|v| match v {
            FieldValue::Int(i) => Some(*i),
            _ => None,
        })
        .unwrap_or(default)
}

fn set_i32(state: &mut BattleState, id: RelicId, key: &'static str, val: i32) {
    if let Some(r) = relic_inst_mut(state, id) {
        r.private_fields
            .insert(Cow::Borrowed(key), FieldValue::Int(val));
    }
}

fn get_card_handle(state: &BattleState, id: RelicId, key: &str) -> Option<CardHandle> {
    relic_inst(state, id)
        .and_then(|r| r.private_fields.get(key))
        .and_then(|v| match v {
            FieldValue::CardHandle(h) | FieldValue::Int(h) | FieldValue::NullableInt(Some(h))
                if *h > 0 =>
            {
                Some(CardHandle(*h as u32))
            }
            _ => None,
        })
}

fn set_card_handle(state: &mut BattleState, id: RelicId, key: &'static str, val: Option<CardHandle>) {
    if let Some(r) = relic_inst_mut(state, id) {
        match val {
            Some(ch) => {
                r.private_fields
                    .insert(Cow::Borrowed(key), FieldValue::CardHandle(ch.0 as i32));
            }
            None => {
                r.private_fields
                    .insert(Cow::Borrowed(key), FieldValue::NullableInt(None));
            }
        }
    }
}

fn collect_hittable_enemies(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<SmallVec<[CreatureHandle; 8]>, KernelError> {
    let mut out = SmallVec::new();
    let enemies: SmallVec<[CreatureHandle; 8]> = SmallVec::from_slice(&state.enemy_handles);
    for eh in enemies {
        if state.creature(eh).is_some_and(|c| c.is_alive())
            && hooks::should_allow_hitting(state, content, eh)?
        {
            out.push(eh);
        }
    }
    Ok(out)
}

fn is_card_type(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    expected: CardType,
) -> Result<bool, KernelError> {
    let Some(ci) = state.card(card) else {
        return Ok(false);
    };
    Ok(content.card_meta(ci.id)?.card_type == expected)
}

fn card_has_tag(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    tag: CardTags,
) -> bool {
    let Some(ci) = state.card(card) else {
        return false;
    };
    content
        .card_meta(ci.id)
        .unwrap_or_else(|_| generated_card_meta(ci.id))
        .base_tags
        .contains(tag)
}

fn create_offer_card_instance(
    state: &mut BattleState,
    card_id: CardId,
    marker_key: &'static str,
) -> CardHandle {
    let meta = generated_card_meta(card_id);
    let handle = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    let mut temp_flags = CardTempFlags::empty();
    if meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST) {
        temp_flags |= CardTempFlags::COSTS_X_ENERGY;
    }
    state.cards.push(CardInst {
        handle,
        id: card_id,
        upgrade_level: 0,
        canonical_base_energy_cost: meta.canonical_energy_cost,
        base_energy_cost: meta.canonical_energy_cost,
        captured_energy_x_value: 0,
        base_replay_count: 0,
        floor_added_to_deck: None,
        deck_card_handle: None,
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
        pile: PileType::None,
        removed_from_state: false,
        transform_into_after_play: None,
    });
    if let Some(ext) = state.card_ext_mut(handle) {
        ext.private_fields
            .insert(Cow::Borrowed(marker_key), FieldValue::Bool(true));
    }
    handle
}

fn enter_generated_into_hand_with_creator(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    handle: CardHandle,
    creator: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    let cid = state
        .card(handle)
        .ok_or_else(|| KernelError::InvalidState("missing generated card".to_owned()))?
        .id;
    let _ = content.card_meta(cid)?;
    card_pile::enter_combat(
        state,
        content,
        handle,
        PileType::Hand,
        CardPilePosition::Bottom,
        creator.is_some(),
    )?;
    hooks::after_card_generated(state, content, handle, creator)?;
    hooks::after_card_generated_for_combat(state, content, handle, creator)
}

fn set_to_free_this_turn(state: &mut BattleState, handle: CardHandle) {
    let Some(card) = state.card(handle) else {
        return;
    };
    let meta = generated_card_meta(card.id);
    if !meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST) {
        if let Some(c) = state.card_mut(handle) {
            c.temp_cost_override_this_turn_or_until_played = Some(0);
        }
        if let Some(ext) = state.card_ext_mut(handle) {
            ext.local_energy_modifiers.push(LocalEnergyModifier {
                kind: LocalEnergyModifierKind::SetThisTurnOrUntilPlayed,
                amount: 0,
                is_reduce_only: false,
            });
        }
    }
    if !meta.flags.contains(CardStaticFlags::IS_X_STAR_COST) {
        if let Some(c) = state.card_mut(handle) {
            c.temp_star_cost_override_this_turn = Some(0);
        }
    }
}

fn make_free_this_combat(state: &mut BattleState, handle: CardHandle) {
    if let Some(c) = state.card_mut(handle) {
        c.temp_flags.insert(CardTempFlags::TEMPORARY_FREE_COMBAT);
    }
    if let Some(ext) = state.card_ext_mut(handle) {
        ext.local_energy_modifiers.push(LocalEnergyModifier {
            kind: LocalEnergyModifierKind::FreeThisCombat,
            amount: 0,
            is_reduce_only: false,
        });
    }
}

fn create_clone(state: &mut BattleState, orig_handle: CardHandle) -> Result<CardHandle, KernelError> {
    let orig = *state
        .card(orig_handle)
        .ok_or_else(|| KernelError::InvalidState("missing card to clone".to_owned()))?;
    if orig.pile == PileType::Deck {
        return Err(KernelError::InvalidAction(
            "Cannot create a combat clone of a persistent deck card.".to_owned(),
        ));
    }
    let source_handle = if orig.temp_flags.contains(CardTempFlags::IS_DUPE) {
        orig.clone_of_handle.unwrap_or(orig_handle)
    } else {
        orig_handle
    };
    let source = *state
        .card(source_handle)
        .ok_or_else(|| KernelError::InvalidState("missing clone source card".to_owned()))?;
    let new_handle = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    let ext_index = if let Some(ext) = state.card_ext(source.ext_index).cloned() {
        let idx = state.card_extensions.len() as u32;
        state.card_extensions.push(ext);
        Some(idx)
    } else {
        None
    };
    let mut temp_flags = source.temp_flags;
    temp_flags.remove(CardTempFlags::EXHAUST_ON_NEXT_PLAY | CardTempFlags::IS_DUPE);
    state.cards.push(CardInst {
        handle: new_handle,
        deck_card_handle: None,
        clone_of_handle: Some(source_handle),
        floor_added_to_deck: None,
        ext_index,
        temp_flags,
        pile: PileType::None,
        removed_from_state: false,
        transform_into_after_play: None,
        ..source
    });
    Ok(new_handle)
}

fn create_dupe(state: &mut BattleState, orig_handle: CardHandle) -> Result<CardHandle, KernelError> {
    let orig = *state
        .card(orig_handle)
        .ok_or_else(|| KernelError::InvalidState("missing card to dupe".to_owned()))?;
    let source_handle = if orig.temp_flags.contains(CardTempFlags::IS_DUPE) {
        orig.clone_of_handle.unwrap_or(orig_handle)
    } else {
        orig_handle
    };
    let source = *state
        .card(source_handle)
        .ok_or_else(|| KernelError::InvalidState("missing dupe source card".to_owned()))?;
    let new_handle = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    let ext_index = if let Some(ext) = state.card_ext(source.ext_index).cloned() {
        let idx = state.card_extensions.len() as u32;
        state.card_extensions.push(ext);
        Some(idx)
    } else {
        None
    };
    let mut temp_flags = source.temp_flags;
    temp_flags.remove(CardTempFlags::EXHAUST_ON_NEXT_PLAY);
    temp_flags.insert(CardTempFlags::IS_DUPE);
    let mut local_kw = source.local_keywords;
    local_kw.remove(CardKeywords::EXHAUST);
    state.cards.push(CardInst {
        handle: new_handle,
        deck_card_handle: None,
        clone_of_handle: Some(source_handle),
        floor_added_to_deck: None,
        ext_index,
        temp_flags,
        local_keywords: local_kw,
        pile: PileType::None,
        removed_from_state: false,
        transform_into_after_play: None,
        ..source
    });
    Ok(new_handle)
}

/// Core `BathTowel.AfterPlayerTurnStartLate` logic (ready for `HookSlot::AfterPlayerTurnStartLate`).
pub fn bath_towel_after_player_turn_start_late(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
) -> Result<(), KernelError> {
    if state.player.turn_number <= 1 {
        let pc = state.player.creature_handle;
        creature::heal(state, content, pc, Dec::from_i32(8))?;
    }
    Ok(())
}

/// Core `BloodVial.AfterPlayerTurnStartLate` logic (ready for `HookSlot::AfterPlayerTurnStartLate`).
pub fn blood_vial_after_player_turn_start_late(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
) -> Result<(), KernelError> {
    if state.player.turn_number == 1 {
        let pc = state.player.creature_handle;
        creature::heal(state, content, pc, Dec::TWO)?;
    }
    Ok(())
}

/// Core `FakeBloodVial.AfterPlayerTurnStartLate` logic (ready for `HookSlot::AfterPlayerTurnStartLate`).
pub fn fake_blood_vial_after_player_turn_start_late(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
) -> Result<(), KernelError> {
    if state.player.turn_number == 1 {
        let pc = state.player.creature_handle;
        creature::heal(state, content, pc, Dec::ONE)?;
    }
    Ok(())
}

/// Core `ChemicalX.ModifyXValue` logic (ready for `HookSlot::ModifyXValue`).
pub const fn chemical_x_modify_x_value(original_value: i32) -> i32 {
    original_value + 2
}

/// Core `HandDrill.AfterBlockBroken` logic (ready once `AfterBlockBroken` carries `breaker`).
pub fn hand_drill_on_block_broken(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    target: CreatureHandle,
    breaker: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    let pc = state.player.creature_handle;
    let breaker_is_owner_or_pet = breaker.is_some_and(|b| {
        b == pc
            || state
                .creature(b)
                .is_some_and(|c| c.pet_owner_player_index.is_some())
    });
    if breaker_is_owner_or_pet && state.creature(target).is_some_and(|c| !c.is_player) {
        power::apply(
            state,
            content,
            PowerId::VulnerablePower,
            target,
            Dec::TWO,
            Some(pc),
            None,
        )?;
    }
    Ok(())
}

fn gain_energy_direct(state: &mut BattleState, amount: i32) {
    if amount > 0 {
        state.player.energy = (state.player.energy.saturating_add(amount)).clamp(0, 999_999_999);
    }
}

fn reconcile_belt_buckle(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
) -> Result<(), KernelError> {
    if !state.is_live_combat() {
        return Ok(());
    }
    let has_potion = state.player.potion_slots.iter().any(|s| s.is_some());
    let applied = get_bool(state, RelicId::BeltBuckle, "BeltBuckle._dexterityApplied", false);
    let pc = state.player.creature_handle;
    if !has_potion && !applied {
        set_bool(state, RelicId::BeltBuckle, "BeltBuckle._dexterityApplied", true);
        power::apply(
            state,
            content,
            PowerId::DexterityPower,
            pc,
            Dec::TWO,
            None,
            None,
        )?;
    } else if has_potion && applied {
        set_bool(
            state,
            RelicId::BeltBuckle,
            "BeltBuckle._dexterityApplied",
            false,
        );
        power::apply(
            state,
            content,
            PowerId::DexterityPower,
            pc,
            Dec::from_i32(-2),
            None,
            None,
        )?;
    }
    Ok(())
}

fn joss_paper_draw_if_threshold_met(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
) -> Result<(), KernelError> {
    let exhausted = get_i32(state, RelicId::JossPaper, "JossPaper._cardsExhausted", 0);
    if exhausted < 5 {
        return Ok(());
    }
    let draw_count = exhausted / 5;
    set_i32(
        state,
        RelicId::JossPaper,
        "JossPaper._cardsExhausted",
        exhausted % 5,
    );
    let _ = card_pile::draw(state, content, draw_count, false)?;
    Ok(())
}

fn summon_pet_monster(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    monster_id: MonsterId,
) -> Result<(), KernelError> {
    let h = creature::create_and_add_monster(state, content, monster_id, CombatSide::Player, None)?;
    if !state.pet_handles.contains(&h) {
        state.pet_handles.push(h);
    }
    Ok(())
}

fn fur_coat_is_marked_current_point(state: &BattleState) -> bool {
    let Some(r) = relic_inst(state, RelicId::FurCoat) else {
        return false;
    };
    let Some(FieldValue::Int(act_idx)) = r.private_fields.get("FurCoat._actIndex") else {
        return false;
    };
    if *act_idx != state.static_ctx.act_index {
        return false;
    }
    let Some(FieldValue::IntArray(coords)) = r.private_fields.get("FurCoat._markedCoordinates")
    else {
        return false;
    };
    coords
        .chunks_exact(2)
        .any(|c| c[0] == state.static_ctx.map_col && c[1] == state.static_ctx.map_row)
}

fn mummified_hand_pick_card(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<Option<CardHandle>, KernelError> {
    let hand: SmallVec<[CardHandle; 16]> = state.player.hand.iter().copied().collect();
    if hand.is_empty() {
        return Ok(None);
    }
    let mut base_positive: SmallVec<[CardHandle; 16]> = SmallVec::new();
    let mut costs_now: SmallVec<[CardHandle; 16]> = SmallVec::new();
    let mut base_positive_and_costs_now: SmallVec<[CardHandle; 16]> = SmallVec::new();
    for h in &hand {
        let Some(c) = state.card(*h) else {
            continue;
        };
        let meta = content
            .card_meta(c.id)
            .unwrap_or_else(|_| generated_card_meta(c.id));
        let is_x_energy = meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST);
        let is_x_star = meta.flags.contains(CardStaticFlags::IS_X_STAR_COST);
        let has_pos_base = (!is_x_energy && c.base_energy_cost > 0) || meta.canonical_star_cost > 0;
        let resolved_e = ordering::resolve_energy_cost(state, content, *h)?;
        let resolved_s = ordering::resolve_star_cost(state, content, *h)?;
        let has_pos_now = (!is_x_energy && resolved_e > 0) || (!is_x_star && resolved_s > 0);
        if has_pos_base {
            base_positive.push(*h);
            if has_pos_now {
                base_positive_and_costs_now.push(*h);
            }
        }
        if has_pos_now {
            costs_now.push(*h);
        }
    }
    let rng = state.run_rngs.get_mut(RunRngType::CombatCardSelection);
    if !base_positive_and_costs_now.is_empty() {
        return Ok(rng.next_item(&base_positive_and_costs_now).copied());
    }
    if !costs_now.is_empty() {
        return Ok(rng.next_item(&costs_now).copied());
    }
    if !base_positive.is_empty() {
        return Ok(rng.next_item(&base_positive).copied());
    }
    Ok(rng.next_item(&hand).copied())
}

pub fn dispatch_hook(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    id: RelicId,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    let pc = state.player.creature_handle;
    match id {
        RelicId::Akabeko => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.turn_number == 1 {
                        power::apply(
                            state,
                            content,
                            PowerId::VigorPower,
                            pc,
                            Dec::from_i32(8),
                            Some(pc),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::Anchor => {
            if slot == HookSlot::BeforeCombatStart {
                block::gain_block(
                    state,
                    content,
                    pc,
                    Dec::from_i32(10),
                    ValueProp::UNPOWERED,
                    None,
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::ArtOfWar => {
            match slot {
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(ctx) = args {
                        if is_card_type(state, content, ctx.card, CardType::Attack)? {
                            set_bool(state, id, "ArtOfWar._attackPlayedThisTurn", true);
                        }
                    }
                }
                HookSlot::AfterSideTurnEnd => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            let attacked =
                                get_bool(state, id, "ArtOfWar._attackPlayedThisTurn", false);
                            set_bool(state, id, "ArtOfWar._previousTurnHadNoAttack", !attacked);
                            set_bool(state, id, "ArtOfWar._attackPlayedThisTurn", false);
                        }
                    }
                }
                HookSlot::AfterEnergyReset => {
                    if state.player.turn_number > 1
                        && get_bool(state, id, "ArtOfWar._previousTurnHadNoAttack", false)
                    {
                        gold::gain_energy(state, Dec::ONE)?;
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_bool(state, id, "ArtOfWar._attackPlayedThisTurn", false);
                    set_bool(state, id, "ArtOfWar._previousTurnHadNoAttack", false);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::BagOfMarbles => {
            if slot == HookSlot::BeforeSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.turn_number <= 1 {
                        let enemies = collect_hittable_enemies(state, content)?;
                        for enemy in enemies {
                            power::apply(
                                state,
                                content,
                                PowerId::VulnerablePower,
                                enemy,
                                Dec::ONE,
                               Some(pc),
                                None,
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::BagOfPreparation => {
            if slot == HookSlot::ModifyHandDraw && state.player.turn_number == 1 {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    return Ok(HookResult {
                        dec_val: Some(original_count.checked_add(Dec::TWO)?),
                        ..Default::default()
                    });
                }
            }
            Ok(HookResult::default())
        }

        RelicId::BathRobes => {
            if slot == HookSlot::BeforeCombatStart
                && !get_bool(state, id, "BathRobes._wasUsed", false)
                && state.player_creature().is_some_and(|c| c.is_alive())
                && state.static_ctx.room_type == RoomType::Boss
            {
                let missing = state
                    .player_creature()
                    .map_or(0, |c| (c.max_hp - c.current_hp).max(0));
                if missing > 0 {
                    creature::heal(state, content, pc, Dec::from_i32(missing))?;
                }
                set_bool(state, id, "BathRobes._wasUsed", true);
            }
            Ok(HookResult::default())
        }

        RelicId::BathTowel => {
            if slot == HookSlot::AfterPlayerTurnStartLate {
                bath_towel_after_player_turn_start_late(content, state)?;
            }
            Ok(HookResult::default())
        }
        RelicId::BloodVial => {
            if slot == HookSlot::AfterPlayerTurnStartLate {
                blood_vial_after_player_turn_start_late(content, state)?;
            }
            Ok(HookResult::default())
        }
        RelicId::FakeBloodVial => {
            if slot == HookSlot::AfterPlayerTurnStartLate {
                fake_blood_vial_after_player_turn_start_late(content, state)?;
            }
            Ok(HookResult::default())
        }

        RelicId::BeatingRemnant => {
            match slot {
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            set_i32(state, id, "BeatingRemnant._hpLostThisTurn", 0);
                        }
                    }
                }
                HookSlot::ModifyHpLostAfterOsty => {
                    if state.is_live_combat() {
                        if let HookArgs::ModifyHpLost { target, amount, .. } = args {
                            if *target == pc {
                                let lost =
                                    get_i32(state, id, "BeatingRemnant._hpLostThisTurn", 0);
                                let remaining = Dec::from_i32((20 - lost).max(0));
                                return Ok(HookResult {
                                    dec_val: Some((*amount).min(remaining)),
                                    ..Default::default()
                                });
                            }
                        }
                    }
                }
                HookSlot::AfterDamageReceived => {
                    if state.is_live_combat() {
                        if let HookArgs::DamageResult {
                            target,
                            unblocked_damage,
                            ..
                        } = args
                        {
                            if *target == pc {
                                let lost =
                                    get_i32(state, id, "BeatingRemnant._hpLostThisTurn", 0);
                                set_i32(
                                    state,
                                    id,
                                    "BeatingRemnant._hpLostThisTurn",
                                    lost + *unblocked_damage,
                                );
                            }
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::Bellows => {
            if slot == HookSlot::AfterPlayerTurnStart && state.player.turn_number <= 1 {
                let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
                for h in hand {
                    card::upgrade(state, content, h)?;
                }
            }
            Ok(HookResult::default())
        }

        RelicId::BeltBuckle => {
            match slot {
                HookSlot::BeforeCombatStart => {
                    set_bool(state, id, "BeltBuckle._dexterityApplied", false);
                    reconcile_belt_buckle(content, state)?;
                }
                HookSlot::AfterPotionUsed
                | HookSlot::AfterPotionProcured
                | HookSlot::AfterPotionDiscarded => {
                    reconcile_belt_buckle(content, state)?;
                }
                HookSlot::AfterCombatEnd => {
                    set_bool(state, id, "BeltBuckle._dexterityApplied", false);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::BigHat => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.turn_number <= 1 {
                        let pool = card_factory::character_combat_pool(state);
                        let picked = card_factory::get_distinct_for_combat_filtered(
                            state,
                            &pool,
                            2,
                            RunRngType::CombatCardGeneration,
                            |cid| {
                                generated_card_meta(cid)
                                    .base_keywords
                                    .contains(CardKeywords::ETHEREAL)
                            },
                        );
                        for cid in picked {
                            let _ = card_pile::add_generated_card_to_combat(
                                state,
                                content,
                                cid,
                                PileType::Hand,
                                CardPilePosition::Bottom,
                                0,
                                Some(pc),
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::BigMushroom => {
            if slot == HookSlot::ModifyHandDraw && state.player.turn_number == 1 {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    return Ok(HookResult {
                        dec_val: Some(original_count.checked_sub(Dec::TWO)?),
                        ..Default::default()
                    });
                }
            }
            Ok(HookResult::default())
        }

        RelicId::BiiigHug => {
            if slot == HookSlot::AfterShuffle {
                let _ = card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    CardId::Soot,
                    PileType::Draw,
                    CardPilePosition::Random,
                    0,
                    Some(pc),
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::BizarreLamp => {
            if slot == HookSlot::BeforeHandDraw && state.player.turn_number <= 1 {
                let _ = card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    CardId::Joy,
                    PileType::Hand,
                    CardPilePosition::Bottom,
                    0,
                    Some(pc),
                )?;
                let _ = card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    CardId::Sadness,
                    PileType::Hand,
                    CardPilePosition::Bottom,
                    0,
                    Some(pc),
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::BlackBlood => {
            if slot == HookSlot::AfterCombatVictory
                && state.player_creature().is_some_and(|c| c.is_alive())
            {
                creature::heal(state, content, pc, Dec::from_i32(12))?;
            }
            Ok(HookResult::default())
        }

        RelicId::BlessedAntler => {
            match slot {
                HookSlot::ModifyMaxEnergy => {
                    if let HookArgs::ModifyHandDraw { original_count } = args {
                        return Ok(HookResult {
                            dec_val: Some(original_count.checked_add(Dec::ONE)?),
                            ..Default::default()
                        });
                    }
                }
                HookSlot::BeforeHandDraw => {
                    if state.player.turn_number == 1 {
                        for _ in 0..3 {
                            let _ = card_pile::add_generated_card_to_combat(
                                state,
                                content,
                                CardId::Dazed,
                                PileType::Draw,
                                CardPilePosition::Random,
                                0,
                                Some(pc),
                            )?;
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::BloodSoakedRose => {
            if slot == HookSlot::ModifyMaxEnergy {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    return Ok(HookResult {
                        dec_val: Some(original_count.checked_add(Dec::ONE)?),
                        ..Default::default()
                    });
                }
            }
            Ok(HookResult::default())
        }

        RelicId::BoneFlute => {
            if slot == HookSlot::AfterAttack {
                if let HookArgs::Attack { info, .. } = args {
                    if state
                        .creature(info.attacker)
                        .is_some_and(|c| c.monster_id == Some(MonsterId::Osty) && c.is_pet())
                    {
                        block::gain_block(
                            state,
                            content,
                            pc,
                            Dec::TWO,
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::BoneTea => {
            if slot == HookSlot::AfterPlayerTurnStart {
                let left = get_i32(state, id, "BoneTea._combatsLeft", 1);
                if left > 0 && state.player.turn_number <= 1 {
                    let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
                    for h in hand {
                        card::upgrade(state, content, h)?;
                    }
                    set_i32(state, id, "BoneTea._combatsLeft", left - 1);
                }
            }
            Ok(HookResult::default())
        }

        RelicId::Bookmark => {
            if slot == HookSlot::AfterFlush {
                let mut candidates = Vec::new();
                for &h in &state.player.hand {
                    let Some(c) = state.card(h) else {
                        continue;
                    };
                    let is_x = content
                        .card_meta(c.id)
                        .unwrap_or_else(|_| generated_card_meta(c.id))
                        .flags
                        .contains(CardStaticFlags::IS_X_ENERGY_COST);
                    if !is_x && ordering::resolve_local_energy_cost(state, h, is_x) > 0 {
                        candidates.push(h);
                    }
                }
                if !candidates.is_empty() {
                    if let Some(&picked) = state
                        .run_rngs
                        .get_mut(RunRngType::CombatCardSelection)
                        .next_item(&candidates)
                    {
                        if let Some(ext) = state.card_ext_mut(picked) {
                            ext.local_energy_modifiers.push(LocalEnergyModifier {
                                kind: LocalEnergyModifierKind::AddUntilPlayed,
                                amount: -1,
                                is_reduce_only: false,
                            });
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::BookOfFiveRings => {
            if slot == HookSlot::AfterCardChangedPiles {
                if let HookArgs::CardPileChanged { card: ch, .. } = args {
                    if state.player_creature().is_some_and(|c| c.is_alive())
                        && state.card(*ch).is_some_and(|c| c.pile == PileType::Deck)
                    {
                        let count = get_i32(state, id, "BookOfFiveRings._cardsAdded", 0) + 1;
                        set_i32(state, id, "BookOfFiveRings._cardsAdded", count);
                        if count % 5 == 0 {
                            creature::heal(state, content, pc, Dec::from_i32(20))?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::BookRepairKnife => {
            if slot == HookSlot::AfterDiedToDoom {
                if let HookArgs::Creature(target) = args {
                    if state
                        .creature(*target)
                        .is_some_and(|c| c.side == CombatSide::Enemy)
                        && hooks::should_owner_death_trigger_fatal(state, content, *target)?
                    {
                        creature::heal(state, content, pc, Dec::from_i32(3))?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::BoomingConch => {
            match slot {
                HookSlot::ModifyHandDraw => {
                    if state.player.turn_number <= 1
                        && state.static_ctx.room_type == RoomType::Elite
                    {
                        if let HookArgs::ModifyHandDraw { original_count } = args {
                            return Ok(HookResult {
                                dec_val: Some(original_count.checked_add(Dec::TWO)?),
                                ..Default::default()
                            });
                        }
                    }
                }
                HookSlot::AfterSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc)
                            && state.player.turn_number <= 1
                            && state.static_ctx.room_type == RoomType::Elite
                        {
                            gold::gain_energy(state, Dec::ONE)?;
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::BoundPhylactery => {
            if slot == HookSlot::BeforeCombatStart
                || (slot == HookSlot::AfterEnergyResetLate && state.player.turn_number != 1)
            {
                osty::summon(state, content, Dec::ONE)?;
            }
            Ok(HookResult::default())
        }

        RelicId::BowlerHat => {
            if slot == HookSlot::ModifyGoldGained {
                if let HookArgs::ModifySummonAmount { amount } = args {
                    let mult = Dec::from_parts(125, 2)?;
                    return Ok(HookResult {
                        dec_val: Some(amount.checked_mul(mult)?),
                        ..Default::default()
                    });
                }
            }
            Ok(HookResult::default())
        }

        RelicId::Bread => {
            match slot {
                HookSlot::ModifyMaxEnergy => {
                    if state.player.turn_number > 1 {
                        if let HookArgs::ModifyHandDraw { original_count } = args {
                            return Ok(HookResult {
                                dec_val: Some(original_count.checked_add(Dec::ONE)?),
                                ..Default::default()
                            });
                        }
                    }
                }
                HookSlot::AfterSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) && state.player.turn_number == 1 {
                            gold::lose_energy(state, Dec::TWO)?;
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::BrilliantScarf => {
            match slot {
                HookSlot::TryModifyEnergyCostInCombatLate
                | HookSlot::TryModifyStarCostInCombat => {
                    if let HookArgs::CardCost { card: ch, .. } = args {
                        let eligible = state.is_live_combat()
                            && get_i32(state, id, "BrilliantScarf._cardsPlayed", 0) == 5
                            && state
                                .card(*ch)
                                .is_some_and(|c| matches!(c.pile, PileType::Hand | PileType::Play));
                        if eligible {
                            return Ok(HookResult {
                                dec_val: Some(Dec::ZERO),
                                modified: true,
                                ..Default::default()
                            });
                        }
                    }
                }
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(ctx) = args {
                        if !ctx.is_auto_play {
                            let played =
                                get_i32(state, id, "BrilliantScarf._cardsPlayed", 0) + 1;
                            set_i32(state, id, "BrilliantScarf._cardsPlayed", played);
                        }
                    }
                }
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            set_i32(state, id, "BrilliantScarf._cardsPlayed", 0);
                        }
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_i32(state, id, "BrilliantScarf._cardsPlayed", 0);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::Brimstone => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) {
                        power::apply(
                            state,
                            content,
                            PowerId::StrengthPower,
                            pc,
                            Dec::TWO,
                            Some(pc),
                            None,
                        )?;
                        let enemies: SmallVec<[CreatureHandle; 8]> = SmallVec::from_slice(&state.enemy_handles);
                        for eh in enemies {
                            if state.creature(eh).is_some_and(|c| c.is_alive()) {
                                power::apply(
                                    state,
                                    content,
                                    PowerId::StrengthPower,
                                    eh,
                                    Dec::ONE,
                                    None,
                                    None,
                                )?;
                            }
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::BronzeScales => {
            if slot == HookSlot::AfterRoomEntered && state.static_ctx.room_type.is_combat_room() {
                power::apply(
                    state,
                    content,
                    PowerId::ThornsPower,
                    pc,
                    Dec::from_i32(3),
                    Some(pc),
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::BurningBlood => {
            if slot == HookSlot::AfterCombatVictory
                && state.player_creature().is_some_and(|c| c.is_alive())
            {
                creature::heal(state, content, pc, Dec::from_i32(6))?;
            }
            Ok(HookResult::default())
        }

        RelicId::BurningSticks => {
            match slot {
                HookSlot::AfterRoomEntered => {
                    if state.static_ctx.room_type.is_combat_room() {
                        set_bool(state, id, "BurningSticks._wasUsedThisCombat", false);
                    }
                }
                HookSlot::AfterCardExhausted => {
                    if let HookArgs::CardExhausted { card: ch, .. } = args {
                        if !get_bool(state, id, "BurningSticks._wasUsedThisCombat", false)
                            && is_card_type(state, content, *ch, CardType::Skill)?
                        {
                            set_bool(state, id, "BurningSticks._wasUsedThisCombat", true);
                            let copy = create_clone(state, *ch)?;
                            enter_generated_into_hand_with_creator(content, state, copy, Some(pc))?;
                        }
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_bool(state, id, "BurningSticks._wasUsedThisCombat", false);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::Byrdine => {
            if slot == HookSlot::BeforeCombatStart {
                summon_pet_monster(content, state, MonsterId::ByrdineMonster)?;
            }
            Ok(HookResult::default())
        }

        RelicId::Byrdpip => {
            match slot {
                HookSlot::BeforeCombatStart => {
                    summon_pet_monster(content, state, MonsterId::Byrdpip)?;
                }
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(ctx) = args {
                        if state
                            .card(ctx.card)
                            .is_some_and(|c| c.id == CardId::ByrdSwoop)
                        {
                            let played = get_i32(state, id, "Byrdpip._swoopsPlayed", 0);
                            if played < 6 {
                                set_i32(state, id, "Byrdpip._swoopsPlayed", played + 1);
                            }
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::Candelabra => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.turn_number == 2 {
                        gold::gain_energy(state, Dec::TWO)?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::CaptainsWheel => {
            if slot == HookSlot::AfterBlockCleared {
                if let HookArgs::Creature(target) = args {
                    if *target == pc && state.player.turn_number == 3 {
                        block::gain_block(
                            state,
                            content,
                            pc,
                            Dec::from_i32(18),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::CentennialPuzzle => {
            match slot {
                HookSlot::AfterDamageReceived => {
                    if let HookArgs::DamageResult {
                        target,
                        unblocked_damage,
                        ..
                    } = args
                    {
                        if !get_bool(state, id, "CentennialPuzzle._triggeredThisCombat", false)
                            && *target == pc
                            && *unblocked_damage > 0
                        {
                            set_bool(state, id, "CentennialPuzzle._triggeredThisCombat", true);
                            let _ = card_pile::draw(state, content, 3, false)?;
                        }
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_bool(state, id, "CentennialPuzzle._triggeredThisCombat", false);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::Chandelier => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.turn_number == 3 {
                        gold::gain_energy(state, Dec::from_i32(3))?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::CharonsAshes => {
            if slot == HookSlot::AfterCardExhausted {
                let targets = collect_hittable_enemies(state, content)?;
                damage::damage_targets(
                    state,
                    content,
                    &targets,
                    Dec::from_i32(3),
                    ValueProp::UNPOWERED,
                    Some(pc),
                    None,
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::ChemicalX => {
            if slot == HookSlot::ModifyXValue {
                if let HookArgs::ModifyXValue { amount, .. } = args {
                    return Ok(HookResult {
                        int_val: Some(chemical_x_modify_x_value(*amount)),
                        ..Default::default()
                    });
                }
            }
            Ok(HookResult::default())
        }

        RelicId::ChoicesParadox => {
            if slot == HookSlot::AfterPlayerTurnStart && state.player.turn_number == 1 {
                let pool = card_factory::character_combat_pool(state);
                let cids = card_factory::get_distinct_for_combat(
                    state,
                    &pool,
                    5,
                    RunRngType::CombatCardGeneration,
                );
                let mut candidates = SmallVec::<[CardHandle; 32]>::new();
                for cid in cids {
                    let h = create_offer_card_instance(state, cid, CHOICES_PARADOX_OFFER_MARKER);
                    if let Some(c) = state.card_mut(h) {
                        c.local_keywords.insert(CardKeywords::RETAIN);
                    }
                    candidates.push(h);
                }
                if !candidates.is_empty() {
                    return Ok(HookResult {
                        needs_choice: Some(ChoiceRequest {
                            timing: ChoiceTiming::PlayerTurnStart,
                            source_id: id.model_id_str().into(),
                            source_pile: PileType::None,
                            min_count: 1,
                            max_count: 1,
                            candidates,
                            can_skip: false,
                        }),
                        ..Default::default()
                    });
                }
            }
            Ok(HookResult::default())
        }

        RelicId::ChosenCheese => {
            if slot == HookSlot::AfterCombatEnd {
                creature::gain_max_hp(state, content, pc, Dec::ONE)?;
            }
            Ok(HookResult::default())
        }

        RelicId::CloakClasp => {
            if slot == HookSlot::BeforeSideTurnEnd {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) {
                        let count = state.player.hand.len() as i32;
                        block::gain_block(
                            state,
                            content,
                            pc,
                            Dec::from_i32(count),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::ConductorsBaton => {
            match slot {
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            set_bool(state, id, "ConductorsBaton._triggeredThisTurn", false);
                        }
                    }
                }
                HookSlot::AfterCardPlayed => {
                    if state.is_live_combat()
                        && !get_bool(state, id, "ConductorsBaton._triggeredThisTurn", false)
                        && melody::melodies_this_turn(state) >= 3
                    {
                        set_bool(state, id, "ConductorsBaton._triggeredThisTurn", true);
                        power::apply(
                            state,
                            content,
                            PowerId::ConductorsBatonPower,
                            pc,
                            Dec::ONE,
                            Some(pc),
                            None,
                        )?;
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::CrackedCore => {
            if slot == HookSlot::BeforeSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.turn_number <= 1 {
                        orb::channel(state, content, OrbId::LightningOrb)?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::Crossbow => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) {
                        let pool = card_factory::character_combat_pool(state);
                        let picked = card_factory::get_distinct_for_combat_filtered(
                            state,
                            &pool,
                            1,
                            RunRngType::CombatCardGeneration,
                            |cid| generated_card_meta(cid).card_type == CardType::Attack,
                        );
                        if let Some(&cid) = picked.first() {
                            let h = card_pile::create_card_in_combat(state, content, cid, 0)?;
                            set_to_free_this_turn(state, h);
                            enter_generated_into_hand_with_creator(content, state, h, Some(pc))?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::DarkstonePeriapt => {
            if slot == HookSlot::AfterCardChangedPiles {
                if let HookArgs::CardPileChanged { card: ch, .. } = args {
                    if state.card(*ch).is_some_and(|c| c.pile == PileType::Deck)
                        && is_card_type(state, content, *ch, CardType::Curse)?
                    {
                        creature::gain_max_hp(state, content, pc, Dec::from_i32(6))?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::DataDisk => {
            if slot == HookSlot::AfterRoomEntered && state.static_ctx.room_type.is_combat_room() {
                power::apply(
                    state,
                    content,
                    PowerId::FocusPower,
                    pc,
                    Dec::ONE,
                    Some(pc),
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::DaughterOfTheWind => {
            if slot == HookSlot::AfterCardPlayed {
                if let HookArgs::CardPlay(ctx) = args {
                    if is_card_type(state, content, ctx.card, CardType::Attack)? {
                        block::gain_block(
                            state,
                            content,
                            pc,
                            Dec::ONE,
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::DelicateFrond => {
            if slot == HookSlot::BeforeCombatStart {
                while state.player.potion_slots.iter().any(|s| s.is_none()) {
                    let Some(pot) = crate::potions::entropic_brew_roll_out_of_combat_potion(state)
                    else {
                        break;
                    };
                    if !potion::try_to_procure(state, content, pot)? {
                        break;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::DemonTongue => {
            match slot {
                HookSlot::AfterDamageReceived => {
                    if let HookArgs::DamageResult {
                        target,
                        unblocked_damage,
                        ..
                    } = args
                    {
                        if state.current_side == CombatSide::Player
                            && *target == pc
                            && *unblocked_damage > 0
                            && !get_bool(state, id, "DemonTongue._triggeredThisTurn", false)
                        {
                            set_bool(state, id, "DemonTongue._triggeredThisTurn", true);
                            creature::heal(state, content, pc, Dec::from_i32(*unblocked_damage))?;
                        }
                    }
                }
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            set_bool(state, id, "DemonTongue._triggeredThisTurn", false);
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::DiamondDiadem => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.turn_number <= 1 {
                        block::gain_block(
                            state,
                            content,
                            pc,
                            Dec::from_i32(20),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                        power::apply(
                            state,
                            content,
                            PowerId::BlurPower,
                            pc,
                            Dec::ONE,
                            Some(pc),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::DivineDestiny => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.turn_number <= 1 {
                        gold::gain_stars(state, content, Dec::from_i32(7))?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::DivineRight => {
            if slot == HookSlot::AfterRoomEntered && state.static_ctx.room_type.is_combat_room() {
                gold::gain_stars(state, content, Dec::from_i32(3))?;
            }
            Ok(HookResult::default())
        }

        RelicId::DragonFruit => {
            if slot == HookSlot::AfterGoldGained {
                creature::gain_max_hp(state, content, pc, Dec::ONE)?;
            }
            Ok(HookResult::default())
        }

        RelicId::Ectoplasm => {
            match slot {
                HookSlot::ModifyGoldGained => {
                    return Ok(HookResult {
                        dec_val: Some(Dec::ZERO),
                        ..Default::default()
                    });
                }
                HookSlot::ModifyMaxEnergy => {
                    if let HookArgs::ModifyHandDraw { original_count } = args {
                        return Ok(HookResult {
                            dec_val: Some(original_count.checked_add(Dec::ONE)?),
                            ..Default::default()
                        });
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::EmberTea => {
            if slot == HookSlot::AfterRoomEntered && state.static_ctx.room_type.is_combat_room() {
                let left = get_i32(state, id, "EmberTea._combatsLeft", 5);
                if left > 0 {
                    power::apply(
                        state,
                        content,
                        PowerId::StrengthPower,
                        pc,
                        Dec::TWO,
                        None,
                        None,
                    )?;
                    set_i32(state, id, "EmberTea._combatsLeft", left - 1);
                }
            }
            Ok(HookResult::default())
        }

        RelicId::EmotionChip => {
            if slot == HookSlot::AfterPlayerTurnStart {
                let prev_turn = state.player.turn_number - 1;
                let lost_last_turn = state.semantic_history.prior_player_damaged_turn == prev_turn
                    || state.damage_history.iter().any(|entry| {
                        entry.receiver_creature_handle == pc
                            && !entry.was_fully_blocked
                            && entry.player_turn_numbers.first() == Some(&prev_turn)
                    });
                if lost_last_turn {
                    let orb_count = state.player.orbs.len();
                    for idx in 0..orb_count {
                        orb::passive(state, content, idx, None, true)?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::EternalFeather => {
            if slot == HookSlot::AfterRoomEntered
                && state.static_ctx.room_type == RoomType::RestSite
                && state.player_creature().is_some_and(|c| c.is_alive())
            {
                let heal_amt = 3 * (state.player.deck.len() as i32 / 5);
                if heal_amt > 0 {
                    creature::heal(state, content, pc, Dec::from_i32(heal_amt))?;
                }
            }
            Ok(HookResult::default())
        }

        RelicId::ExerciseMat => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.turn_number <= 1 {
                        power::apply(
                            state,
                            content,
                            PowerId::StretchPower,
                            pc,
                            Dec::from_i32(6),
                            Some(pc),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::FakeAnchor => {
            if slot == HookSlot::BeforeCombatStart {
                block::gain_block(
                    state,
                    content,
                    pc,
                    Dec::from_i32(4),
                    ValueProp::UNPOWERED,
                    None,
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::FakeHappyFlower => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) {
                        let turns = (get_i32(state, id, "FakeHappyFlower._turnsSeen", 0) + 1) % 5;
                        set_i32(state, id, "FakeHappyFlower._turnsSeen", turns);
                        if turns == 0 {
                            gold::gain_energy(state, Dec::ONE)?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::FakeOrichalcum => {
            match slot {
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            set_bool(state, id, "FakeOrichalcum._shouldTrigger", false);
                        }
                    }
                }
                HookSlot::BeforeSideTurnEndVeryEarly => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc)
                            && state.player_creature().is_some_and(|c| c.block == 0)
                        {
                            set_bool(state, id, "FakeOrichalcum._shouldTrigger", true);
                        }
                    }
                }
                HookSlot::BeforeSideTurnEnd => {
                    if get_bool(state, id, "FakeOrichalcum._shouldTrigger", false) {
                        set_bool(state, id, "FakeOrichalcum._shouldTrigger", false);
                        block::gain_block(
                            state,
                            content,
                            pc,
                            Dec::from_i32(3),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::FakeSneckoEye => {
            if slot == HookSlot::BeforeCombatStart {
                power::apply(
                    state,
                    content,
                    PowerId::ConfusedPower,
                    pc,
                    Dec::ONE,
                    Some(pc),
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::FakeStrikeDummy => {
            if slot == HookSlot::ModifyDamageAdditive {
                if let HookArgs::ModifyDamage {
                    dealer,
                    props,
                    card_source,
                    ..
                } = args
                {
                    if props.is_powered_attack()
                        && (*dealer == Some(pc) || card_source.is_some())
                        && card_source.is_some_and(|ch| card_has_tag(state, content, ch, CardTags::STRIKE))
                    {
                        return Ok(HookResult {
                            dec_val: Some(Dec::ONE),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::FakeVenerableTeaSet => {
            match slot {
                HookSlot::AfterRoomEntered => {
                    if state.static_ctx.room_type == RoomType::RestSite {
                        set_bool(state, id, "FakeVenerableTeaSet._isArmed", true);
                    }
                }
                HookSlot::AfterEnergyReset => {
                    if get_bool(state, id, "FakeVenerableTeaSet._isArmed", false) {
                        set_bool(state, id, "FakeVenerableTeaSet._isArmed", false);
                        gold::gain_energy(state, Dec::ONE)?;
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::FencingManual => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.turn_number == 1 {
                        forge::forge(state, content, Dec::from_i32(10))?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::Fertilizer => {
            if slot == HookSlot::AfterCardGeneratedForCombat {
                if let HookArgs::CardGenerated { card: ch, creator } = args {
                    if *creator == Some(pc)
                        && state.card(*ch).is_some_and(|c| c.id == CardId::Sadness)
                    {
                        power::apply(
                            state,
                            content,
                            PowerId::StrengthPower,
                            pc,
                            Dec::ONE,
                            Some(pc),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::FestivePopper => {
            if slot == HookSlot::AfterPlayerTurnStart && state.player.turn_number == 1 {
                let targets = collect_hittable_enemies(state, content)?;
                damage::damage_targets(
                    state,
                    content,
                    &targets,
                    Dec::from_i32(9),
                    ValueProp::UNPOWERED,
                    Some(pc),
                    None,
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::Fiddle => {
            match slot {
                HookSlot::ModifyHandDraw => {
                    if let HookArgs::ModifyHandDraw { original_count } = args {
                        return Ok(HookResult {
                            dec_val: Some(original_count.checked_add(Dec::TWO)?),
                            ..Default::default()
                        });
                    }
                }
                HookSlot::ShouldDraw => {
                    if let HookArgs::ShouldDraw { from_hand_draw } = args {
                        if !from_hand_draw && state.current_side == CombatSide::Player {
                            return Ok(HookResult {
                                bool_val: Some(false),
                                ..Default::default()
                            });
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::FishingRod => {
            if slot == HookSlot::AfterCombatEnd && state.static_ctx.room_type == RoomType::Monster {
                let seen = get_i32(state, id, "FishingRod.<CombatsSeen>k__BackingField", 0) + 1;
                set_i32(state, id, "FishingRod.<CombatsSeen>k__BackingField", seen);
                if seen % 3 == 0 {
                    let mut upgradable = Vec::new();
                    for &dh in &state.player.deck {
                        if let Some(c) = state.card(dh) {
                            let max_upg = content
                                .card_meta(c.id)
                                .unwrap_or_else(|_| generated_card_meta(c.id))
                                .max_upgrade_level;
                            if c.upgrade_level < max_upg {
                                upgradable.push(dh);
                            }
                        }
                    }
                    if let Some(&picked) = state
                        .player_rngs
                        .get_mut(sts2_types::PlayerRngType::NonCombatGeneration)
                        .next_item(&upgradable)
                    {
                        card::upgrade(state, content, picked)?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::ForgottenSoul => {
            if slot == HookSlot::AfterCardExhausted {
                let targets = collect_hittable_enemies(state, content)?;
                if let Some(&target) = state
                    .run_rngs
                    .get_mut(RunRngType::CombatTargets)
                    .next_item(&targets)
                {
                    damage::damage_targets(
                        state,
                        content,
                        &[target],
                        Dec::ONE,
                        ValueProp::UNPOWERED,
                        Some(pc),
                        None,
                        None,
                    )?;
                }
            }
            Ok(HookResult::default())
        }

        RelicId::FuneraryMask => {
            if slot == HookSlot::BeforeHandDraw && state.player.turn_number == 1 {
                for _ in 0..3 {
                    let _ = card_pile::add_generated_card_to_combat(
                        state,
                        content,
                        CardId::Soul,
                        PileType::Draw,
                        CardPilePosition::Random,
                        0,
                        Some(pc),
                    )?;
                }
            }
            Ok(HookResult::default())
        }

        RelicId::FurCoat => {
            if fur_coat_is_marked_current_point(state) {
                match slot {
                    HookSlot::BeforeCombatStart => {
                        let enemies = collect_hittable_enemies(state, content)?;
                        for eh in enemies {
                            creature::set_current_hp(state, content, eh, Dec::ONE)?;
                        }
                    }
                    HookSlot::AfterCreatureAddedToCombat => {
                        if let HookArgs::Creature(cr) = args {
                            if state
                                .creature(*cr)
                                .is_some_and(|c| c.side == CombatSide::Enemy)
                            {
                                creature::set_current_hp(state, content, *cr, Dec::ONE)?;
                            }
                        }
                    }
                    _ => {}
                }
            }
            Ok(HookResult::default())
        }

        RelicId::GalacticDust => {
            if slot == HookSlot::AfterStarsSpent {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    let spent_delta = original_count.to_i32_trunc()?;
                    let total = get_i32(state, id, "GalacticDust._starsSpent", 0) + spent_delta;
                    let triggers = total / 10;
                    set_i32(state, id, "GalacticDust._starsSpent", total % 10);
                    if triggers > 0 {
                        block::gain_block(
                            state,
                            content,
                            pc,
                            Dec::from_i32(10 * triggers),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::GamblingChip => {
            if slot == HookSlot::AfterPlayerTurnStart && state.player.turn_number <= 1 {
                let candidates: SmallVec<[CardHandle; 32]> =
                    state.player.hand.iter().copied().collect();
                if !candidates.is_empty() {
                    return Ok(HookResult {
                        needs_choice: Some(ChoiceRequest {
                            timing: ChoiceTiming::PlayerTurnStart,
                            source_id: id.model_id_str().into(),
                            source_pile: PileType::Hand,
                            min_count: 0,
                            max_count: candidates.len(),
                            candidates,
                            can_skip: false,
                        }),
                        ..Default::default()
                    });
                }
            }
            Ok(HookResult::default())
        }

        RelicId::GamePiece => {
            if slot == HookSlot::AfterCardPlayed {
                if let HookArgs::CardPlay(ctx) = args {
                    if is_card_type(state, content, ctx.card, CardType::Power)? {
                        let _ = card_pile::draw(state, content, 1, false)?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::GhostSeed => {
            if slot == HookSlot::AfterRoomEntered && state.static_ctx.room_type.is_combat_room() {
                let handles: Vec<CardHandle> = state
                    .player
                    .hand
                    .iter()
                    .chain(state.player.draw.iter())
                    .chain(state.player.discard.iter())
                    .chain(state.player.exhaust.iter())
                    .chain(state.player.play.iter())
                    .copied()
                    .collect();
                for h in handles {
                    if let Some(c) = state.card(h) {
                        let meta = content
                            .card_meta(c.id)
                            .unwrap_or_else(|_| generated_card_meta(c.id));
                        if meta.rarity == CardRarity::Basic
                            && (meta.base_tags.contains(CardTags::STRIKE)
                                || meta.base_tags.contains(CardTags::DEFEND))
                        {
                            if let Some(cm) = state.card_mut(h) {
                                cm.local_keywords.insert(CardKeywords::ETHEREAL);
                            }
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::Girya => {
            if slot == HookSlot::AfterRoomEntered && state.static_ctx.room_type.is_combat_room() {
                let lifts = get_i32(state, id, "Girya._timesLifted", 0);
                if lifts > 0 {
                    power::apply(
                        state,
                        content,
                        PowerId::StrengthPower,
                        pc,
                        Dec::from_i32(lifts),
                        Some(pc),
                        None,
                    )?;
                }
            }
            Ok(HookResult::default())
        }

        RelicId::GoldPlatedCables => {
            if slot == HookSlot::ModifyOrbPassiveTriggerCounts {
                if let HookArgs::ModifyOrbPassiveTriggerCount {
                    orb_idx,
                    trigger_count,
                    ..
                } = args
                {
                    if *orb_idx == 0 {
                        return Ok(HookResult {
                            int_val: Some(*trigger_count + 1),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::Gorget => {
            if slot == HookSlot::AfterRoomEntered && state.static_ctx.room_type.is_combat_room() {
                power::apply(
                    state,
                    content,
                    PowerId::PlatingPower,
                    pc,
                    Dec::from_i32(4),
                    Some(pc),
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::GremlinHorn => {
            if slot == HookSlot::AfterDeath {
                if let HookArgs::AfterDeath { target, .. } = args {
                    if state
                        .creature(*target)
                        .is_some_and(|c| c.side != CombatSide::Player)
                    {
                        gain_energy_direct(state, 1);
                        let _ = card_pile::draw(state, content, 1, false)?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::HandDrill => {
            if slot == HookSlot::AfterBlockBroken {
                if let HookArgs::BlockBroken { target, breaker } = args {
                    hand_drill_on_block_broken(content, state, *target, *breaker)?;
                }
            }
            Ok(HookResult::default())
        }

        RelicId::HappyFlower => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) {
                        let count = get_i32(state, id, "HappyFlower._turnCounter", 0) + 1;
                        if count < 3 {
                            set_i32(state, id, "HappyFlower._turnCounter", count);
                        } else {
                            set_i32(state, id, "HappyFlower._turnCounter", 0);
                            gold::gain_energy(state, Dec::ONE)?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::HelicalDart => {
            if slot == HookSlot::AfterCardPlayed {
                if let HookArgs::CardPlay(ctx) = args {
                    if card_has_tag(state, content, ctx.card, CardTags::SHIV) {
                        power::apply(
                            state,
                            content,
                            PowerId::HelicalDartPower,
                            pc,
                            Dec::ONE,
                            Some(pc),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::HistoryCourse => {
            match slot {
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(ctx) = args {
                        let is_dupe = state
                            .card(ctx.card)
                            .is_some_and(|c| c.temp_flags.contains(CardTempFlags::IS_DUPE));
                        if !is_dupe && is_card_type(state, content, ctx.card, CardType::Attack)? {
                            let turn = state.player.turn_number;
                            let last_turn =
                                get_i32(state, id, "HistoryCourse._lastAttackTurn", 0);
                            if last_turn != turn {
                                let last_attack =
                                    get_card_handle(state, id, "HistoryCourse._lastAttack");
                                set_card_handle(
                                    state,
                                    id,
                                    "HistoryCourse._previousAttack",
                                    last_attack,
                                );
                                set_i32(state, id, "HistoryCourse._previousAttackTurn", last_turn);
                            }
                            set_card_handle(
                                state,
                                id,
                                "HistoryCourse._lastAttack",
                                Some(ctx.card),
                            );
                            set_i32(state, id, "HistoryCourse._lastAttackTurn", turn);
                        }
                    }
                }
                HookSlot::AfterAutoPrePlayPhaseEntered => {
                    if state.player.turn_number != 1 {
                        let prev_turn = state.player.turn_number - 1;
                        let last_turn = get_i32(state, id, "HistoryCourse._lastAttackTurn", 0);
                        let prev_attack_turn =
                            get_i32(state, id, "HistoryCourse._previousAttackTurn", 0);
                        let attack = if last_turn == prev_turn {
                            get_card_handle(state, id, "HistoryCourse._lastAttack")
                        } else if prev_attack_turn == prev_turn {
                            get_card_handle(state, id, "HistoryCourse._previousAttack")
                        } else {
                            None
                        };
                        if let Some(orig) = attack {
                            let dupe = create_dupe(state, orig)?;
                            let _ = autoplay::from_cards(state, content, &[dupe], None, false)?;
                        }
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_card_handle(state, id, "HistoryCourse._lastAttack", None);
                    set_i32(state, id, "HistoryCourse._lastAttackTurn", 0);
                    set_card_handle(state, id, "HistoryCourse._previousAttack", None);
                    set_i32(state, id, "HistoryCourse._previousAttackTurn", 0);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::HornCleat => {
            if slot == HookSlot::AfterBlockCleared {
                if let HookArgs::Creature(target) = args {
                    if *target == pc && state.player.turn_number == 2 {
                        block::gain_block(
                            state,
                            content,
                            pc,
                            Dec::from_i32(14),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::IceCream => {
            if slot == HookSlot::ShouldPlayerResetEnergy {
                return Ok(HookResult {
                    bool_val: Some(state.player.turn_number == 1),
                    ..Default::default()
                });
            }
            Ok(HookResult::default())
        }

        RelicId::InfusedCore => {
            match slot {
                HookSlot::AfterSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) && state.player.turn_number <= 1 {
                            for _ in 0..3 {
                                orb::channel(state, content, OrbId::LightningOrb)?;
                            }
                        }
                    }
                }
                HookSlot::ModifyOrbValue => {
                    if let HookArgs::ModifyOrbValue { orb, amount } = args {
                        if *orb == OrbId::LightningOrb {
                            return Ok(HookResult {
                                dec_val: Some(amount.checked_add(Dec::ONE)?),
                                ..Default::default()
                            });
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::IntimidatingHelmet => {
            if slot == HookSlot::BeforeCardPlayed {
                if let HookArgs::CardPlay(ctx) = args {
                    if ctx.energy_value >= 2 {
                        block::gain_block(
                            state,
                            content,
                            pc,
                            Dec::from_i32(4),
                            ValueProp::UNPOWERED,
                            None,
                            Some(*ctx),
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::IronClub => {
            if slot == HookSlot::AfterCardPlayed {
                let played = get_i32(state, id, "IronClub._cardsPlayed", 0) + 1;
                set_i32(state, id, "IronClub._cardsPlayed", played);
                if played % 4 == 0 && state.is_live_combat() {
                    let _ = card_pile::draw(state, content, 1, false)?;
                }
            }
            Ok(HookResult::default())
        }

        RelicId::IvoryTile => {
            if slot == HookSlot::AfterCardPlayed {
                if let HookArgs::CardPlay(ctx) = args {
                    if ctx.energy_value >= 3 {
                        gold::gain_energy(state, Dec::ONE)?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::JeweledMask => {
            if slot == HookSlot::BeforeHandDraw && state.player.turn_number <= 1 {
                let mut powers = Vec::new();
                for &h in &state.player.draw {
                    if is_card_type(state, content, h, CardType::Power)? {
                        powers.push(h);
                    }
                }
                if !powers.is_empty() {
                    let mut non_innate = Vec::new();
                    for &h in &powers {
                        if !card_pile::card_has_keyword(state, content, h, CardKeywords::INNATE)? {
                            non_innate.push(h);
                        }
                    }
                    let pool = if non_innate.is_empty() {
                        &powers
                    } else {
                        &non_innate
                    };
                    if let Some(&selected) = state
                        .run_rngs
                        .get_mut(RunRngType::CombatCardSelection)
                        .next_item(pool)
                    {
                        make_free_this_combat(state, selected);
                        card_pile::add_sync(
                            state,
                            selected,
                            PileType::Hand,
                            CardPilePosition::Bottom,
                        );
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::JossPaper => {
            match slot {
                HookSlot::AfterCardExhausted => {
                    if let HookArgs::CardExhausted {
                        caused_by_ethereal, ..
                    } = args
                    {
                        if *caused_by_ethereal {
                            let eth = get_i32(state, id, "JossPaper._etherealCount", 0) + 1;
                            set_i32(state, id, "JossPaper._etherealCount", eth);
                        } else {
                            let ex = get_i32(state, id, "JossPaper._cardsExhausted", 0) + 1;
                            set_i32(state, id, "JossPaper._cardsExhausted", ex);
                            joss_paper_draw_if_threshold_met(content, state)?;
                        }
                    }
                }
                HookSlot::AfterSideTurnEnd => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            let eth = get_i32(state, id, "JossPaper._etherealCount", 0);
                            let ex = get_i32(state, id, "JossPaper._cardsExhausted", 0) + eth;
                            set_i32(state, id, "JossPaper._cardsExhausted", ex);
                            set_i32(state, id, "JossPaper._etherealCount", 0);
                            joss_paper_draw_if_threshold_met(content, state)?;
                        }
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_i32(state, id, "JossPaper._etherealCount", 0);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::Kunai => {
            match slot {
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            set_i32(state, id, "Kunai._attacksThisTurn", 0);
                        }
                    }
                }
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(ctx) = args {
                        if is_card_type(state, content, ctx.card, CardType::Attack)? {
                            let attacks = get_i32(state, id, "Kunai._attacksThisTurn", 0) + 1;
                            set_i32(state, id, "Kunai._attacksThisTurn", attacks);
                            if attacks % 3 == 0 {
                                power::apply(
                                    state,
                                    content,
                                    PowerId::DexterityPower,
                                    pc,
                                    Dec::ONE,
                                    Some(pc),
                                    None,
                                )?;
                            }
                        }
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_i32(state, id, "Kunai._attacksThisTurn", 0);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::Kusarigama => {
            match slot {
                HookSlot::BeforeCombatStart => {
                    set_i32(state, id, "Kusarigama._attacksThisTurn", 0);
                }
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(ctx) = args {
                        if is_card_type(state, content, ctx.card, CardType::Attack)? {
                            let attacks =
                                get_i32(state, id, "Kusarigama._attacksThisTurn", 0) + 1;
                            set_i32(state, id, "Kusarigama._attacksThisTurn", attacks);
                            if attacks % 3 == 0 {
                                let targets = collect_hittable_enemies(state, content)?;
                                if let Some(&target) = state
                                    .run_rngs
                                    .get_mut(RunRngType::CombatTargets)
                                    .next_item(&targets)
                                {
                                    damage::damage_targets(
                                        state,
                                        content,
                                        &[target],
                                        Dec::from_i32(6),
                                        ValueProp::UNPOWERED,
                                        Some(pc),
                                        None,
                                        Some(*ctx),
                                    )?;
                                }
                            }
                        }
                    }
                }
                HookSlot::AfterSideTurnEnd => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            set_i32(state, id, "Kusarigama._attacksThisTurn", 0);
                        }
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_i32(state, id, "Kusarigama._attacksThisTurn", 0);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::Lantern => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.turn_number == 1 {
                        gold::gain_energy(state, Dec::ONE)?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::LavaLamp => {
            match slot {
                HookSlot::AfterRoomEntered => {
                    set_bool(state, id, "LavaLamp._tookDamageThisCombat", false);
                }
                HookSlot::AfterDamageReceived => {
                    if let HookArgs::DamageResult {
                        target,
                        props,
                        unblocked_damage,
                        ..
                    } = args
                    {
                        if state.static_ctx.room_type.is_combat_room()
                            && *target == pc
                            && *unblocked_damage > 0
                            && !props.contains(ValueProp::UNBLOCKABLE)
                        {
                            set_bool(state, id, "LavaLamp._tookDamageThisCombat", true);
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::LetterOpener => {
            match slot {
                HookSlot::BeforeCombatStart => {
                    set_i32(state, id, "LetterOpener._skillsThisTurn", 0);
                }
                HookSlot::AfterSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) && state.player.turn_number != 1 {
                            set_i32(state, id, "LetterOpener._skillsThisTurn", 0);
                        }
                    }
                }
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(ctx) = args {
                        if is_card_type(state, content, ctx.card, CardType::Skill)? {
                            let skills =
                                get_i32(state, id, "LetterOpener._skillsThisTurn", 0) + 1;
                            set_i32(state, id, "LetterOpener._skillsThisTurn", skills);
                            if skills % 3 == 0 {
                                let targets = collect_hittable_enemies(state, content)?;
                                damage::damage_targets(
                                    state,
                                    content,
                                    &targets,
                                    Dec::from_i32(5),
                                    ValueProp::UNPOWERED,
                                    Some(pc),
                                    None,
                                    Some(*ctx),
                                )?;
                            }
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::LizardTail => {
            match slot {
                HookSlot::ShouldDieLate => {
                    if let HookArgs::Creature(target) = args {
                        let prevent = *target == pc
                            && !get_bool(state, id, "LizardTail._wasUsed", false);
                        return Ok(HookResult {
                            bool_val: Some(!prevent),
                            ..Default::default()
                        });
                    }
                }
                HookSlot::AfterPreventingDeath => {
                    if let HookArgs::Creature(target) = args {
                        set_bool(state, id, "LizardTail._wasUsed", true);
                        let max_hp = state.creature(*target).map_or(1, |c| c.max_hp);
                        let heal_amt = Dec::ONE.max(Dec::from_i32(max_hp).checked_mul(Dec::HALF)?);
                        creature::heal(state, content, *target, heal_amt)?;
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::LostWisp => {
            if slot == HookSlot::AfterCardPlayed {
                if let HookArgs::CardPlay(ctx) = args {
                    if is_card_type(state, content, ctx.card, CardType::Power)? {
                        let targets = collect_hittable_enemies(state, content)?;
                        damage::damage_targets(
                            state,
                            content,
                            &targets,
                            Dec::from_i32(8),
                            ValueProp::UNPOWERED,
                            Some(pc),
                            None,
                            Some(*ctx),
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::LuckyFysh => {
            if slot == HookSlot::AfterCardChangedPiles {
                if let HookArgs::CardPileChanged { card: ch, .. } = args {
                    if state.card(*ch).is_some_and(|c| c.pile == PileType::Deck) {
                        gold::gain_gold(state, content, Dec::from_i32(15))?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::LunarPastry => {
            if slot == HookSlot::AfterSideTurnEnd {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) {
                        gold::gain_stars(state, content, Dec::ONE)?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::MawBank => {
            if slot == HookSlot::AfterRoomEntered
                && !get_bool(state, id, "MawBank._hasItemBeenBought", false)
            {
                gold::gain_gold(state, content, Dec::from_i32(12))?;
            }
            Ok(HookResult::default())
        }

        RelicId::MealTicket => {
            if slot == HookSlot::AfterRoomEntered
                && state.static_ctx.room_type == RoomType::Shop
                && state.player_creature().is_some_and(|c| c.is_alive())
            {
                creature::heal(state, content, pc, Dec::from_i32(15))?;
            }
            Ok(HookResult::default())
        }

        RelicId::MeatOnTheBone => {
            if slot == HookSlot::AfterCombatVictoryEarly {
                let should_heal = state.player_creature().is_some_and(|c| {
                    c.is_alive()
                        && Dec::from_i32(c.current_hp)
                            <= Dec::from_i32(c.max_hp)
                                .checked_mul(Dec::HALF)
                                .unwrap_or(Dec::ZERO)
                });
                if should_heal {
                    creature::heal(state, content, pc, Dec::from_i32(12))?;
                }
            }
            Ok(HookResult::default())
        }

        RelicId::MercuryHourglass => {
            if slot == HookSlot::AfterPlayerTurnStart {
                let targets = collect_hittable_enemies(state, content)?;
                damage::damage_targets(
                    state,
                    content,
                    &targets,
                    Dec::from_i32(3),
                    ValueProp::UNPOWERED,
                    Some(pc),
                    None,
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::Metronome => {
            match slot {
                HookSlot::AfterRoomEntered => {
                    if state.static_ctx.room_type.is_combat_room() {
                        set_i32(state, id, "Metronome._orbsChanneled", 0);
                    }
                }
                HookSlot::AfterOrbChanneled => {
                    let count = get_i32(state, id, "Metronome._orbsChanneled", 0) + 1;
                    set_i32(state, id, "Metronome._orbsChanneled", count);
                    if count == 7 {
                        let targets = collect_hittable_enemies(state, content)?;
                        damage::damage_targets(
                            state,
                            content,
                            &targets,
                            Dec::from_i32(30),
                            ValueProp::UNPOWERED,
                            Some(pc),
                            None,
                            None,
                        )?;
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_i32(state, id, "Metronome._orbsChanneled", 0);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::MightyWings => {
            if slot == HookSlot::AfterDamageGiven {
                if let HookArgs::DamageResult {
                    dealer,
                    props,
                    blocked_damage,
                    unblocked_damage,
                    ..
                } = args
                {
                    let is_owner_or_osty =
                        dealer.is_some_and(|d| d == pc || state.osty_handle() == Some(d));
                    if props.is_powered_attack()
                        && is_owner_or_osty
                        && (*blocked_damage + *unblocked_damage) >= 30
                    {
                        power::apply(
                            state,
                            content,
                            PowerId::StrengthPower,
                            pc,
                            Dec::TWO,
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::MiniatureCannon => {
            if slot == HookSlot::ModifyDamageAdditive {
                if let HookArgs::ModifyDamage {
                    dealer,
                    props,
                    card_source,
                    ..
                } = args
                {
                    if *dealer == Some(pc)
                        && props.is_powered_attack()
                        && card_source
                            .is_some_and(|ch| state.card(ch).is_some_and(|c| c.upgrade_level > 0))
                    {
                        return Ok(HookResult {
                            dec_val: Some(Dec::from_i32(3)),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::MiniRegent => {
            match slot {
                HookSlot::AfterStarsSpent => {
                    if let HookArgs::ModifyHandDraw { original_count } = args {
                        if *original_count > Dec::ZERO
                            && !get_bool(state, id, "MiniRegent._triggeredThisTurn", false)
                        {
                            set_bool(state, id, "MiniRegent._triggeredThisTurn", true);
                            power::apply(
                                state,
                                content,
                                PowerId::StrengthPower,
                                pc,
                                Dec::ONE,
                                Some(pc),
                                None,
                            )?;
                        }
                    }
                }
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            set_bool(state, id, "MiniRegent._triggeredThisTurn", false);
                        }
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_bool(state, id, "MiniRegent._triggeredThisTurn", false);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::MrStruggles => {
            if slot == HookSlot::AfterPlayerTurnStart {
                let targets = collect_hittable_enemies(state, content)?;
                let dmg = state.player.turn_number;
                damage::damage_targets(
                    state,
                    content,
                    &targets,
                    Dec::from_i32(dmg),
                    ValueProp::UNPOWERED,
                    Some(pc),
                    None,
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::MummifiedHand => {
            if slot == HookSlot::AfterCardPlayed && state.is_live_combat() {
                if let HookArgs::CardPlay(ctx) = args {
                    if is_card_type(state, content, ctx.card, CardType::Power)? {
                        if let Some(selected) = mummified_hand_pick_card(state, content)? {
                            set_to_free_this_turn(state, selected);
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::MusicBox => {
            match slot {
                HookSlot::BeforeCardPlayed => {
                    if let HookArgs::CardPlay(ctx) = args {
                        if get_card_handle(state, id, "MusicBox._cardBeingPlayed").is_none()
                            && !get_bool(state, id, "MusicBox._wasUsedThisTurn", false)
                            && is_card_type(state, content, ctx.card, CardType::Attack)?
                        {
                            set_card_handle(
                                state,
                                id,
                                "MusicBox._cardBeingPlayed",
                                Some(ctx.card),
                            );
                        }
                    }
                }
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(ctx) = args {
                        if get_card_handle(state, id, "MusicBox._cardBeingPlayed") == Some(ctx.card)
                            && state.is_in_progress
                        {
                            let copy = create_clone(state, ctx.card)?;
                            if let Some(c) = state.card_mut(copy) {
                                c.local_keywords.insert(CardKeywords::ETHEREAL);
                            }
                            enter_generated_into_hand_with_creator(content, state, copy, Some(pc))?;
                            set_bool(state, id, "MusicBox._wasUsedThisTurn", true);
                            set_card_handle(state, id, "MusicBox._cardBeingPlayed", None);
                        }
                    }
                }
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            set_bool(state, id, "MusicBox._wasUsedThisTurn", false);
                            set_card_handle(state, id, "MusicBox._cardBeingPlayed", None);
                        }
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_bool(state, id, "MusicBox._wasUsedThisTurn", false);
                    set_card_handle(state, id, "MusicBox._cardBeingPlayed", None);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::MysticLighter => {
            if slot == HookSlot::ModifyDamageAdditive {
                if let HookArgs::ModifyDamage {
                    dealer,
                    props,
                    card_source,
                    ..
                } = args
                {
                    let has_enchantment = card_source.is_some_and(|ch| {
                        state
                            .card(ch)
                            .and_then(|c| state.card_ext(c.ext_index))
                            .and_then(|e| e.enchantment.as_ref())
                            .is_some()
                    });
                    if props.is_powered_attack() && *dealer == Some(pc) && has_enchantment {
                        return Ok(HookResult {
                            dec_val: Some(Dec::from_i32(9)),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }

        // Non-combat / out-of-combat only A-M relics:
        RelicId::AlchemicalCoffer
        | RelicId::AmethystAubergine
        | RelicId::ArcaneScroll
        | RelicId::ArchaicTooth
        | RelicId::Astrolabe
        | RelicId::BeautifulBracelet
        | RelicId::BingBong
        | RelicId::BlackStar
        | RelicId::CallingBell
        | RelicId::Cauldron
        | RelicId::Circlet
        | RelicId::Claws
        | RelicId::CursedPearl
        | RelicId::DingyRug
        | RelicId::DistinguishedCape
        | RelicId::DollysMirror
        | RelicId::DowsingRod
        | RelicId::DreamCatcher
        | RelicId::Driftwood
        | RelicId::DustyTome
        | RelicId::ElectricShrymp
        | RelicId::EmptyCage
        | RelicId::FakeLeesWaffle
        | RelicId::FakeMango
        | RelicId::FakeMerchantsRug
        | RelicId::FragrantMushroom
        | RelicId::FresnelLens
        | RelicId::FrozenEgg
        | RelicId::GlassEye
        | RelicId::Glitter
        | RelicId::GnarledHammer
        | RelicId::GoldenCompass
        | RelicId::GoldenPearl
        | RelicId::HeftyTablet
        | RelicId::JewelryBox
        | RelicId::JuzuBracelet
        | RelicId::Kaleidoscope
        | RelicId::Kifuda
        | RelicId::LargeCapsule
        | RelicId::LastingCandy
        | RelicId::LavaRock
        | RelicId::LeadPaperweight
        | RelicId::LeafyPoultice
        | RelicId::LeesWaffle
        | RelicId::LoomingFruit
        | RelicId::LordsParasol
        | RelicId::LostCoffer
        | RelicId::Mango
        | RelicId::MassiveScroll
        | RelicId::MeatCleaver
        | RelicId::MembershipCard
        | RelicId::MiniatureTent
        | RelicId::MoltenEgg => Ok(HookResult::default()),

        _ => Err(KernelError::Unsupported {
            kind: "Relic",
            id: id.model_id_str().to_owned(),
        }),
    }
}

pub fn resume_hook_choice(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    id: RelicId,
    slot: HookSlot,
    selected: &[CardHandle],
) -> Result<HookResult, KernelError> {
    match (id, slot) {
        (RelicId::GamblingChip, HookSlot::AfterPlayerTurnStart) => {
            if !selected.is_empty() && !hooks::is_over_or_ending(state) {
                card::discard_batch(state, content, selected)?;
                let _ = card_pile::draw(state, content, selected.len() as i32, false)?;
            }
            Ok(HookResult::default())
        }
        (RelicId::ChoicesParadox, HookSlot::AfterPlayerTurnStart) => {
            let offer_handles: SmallVec<[CardHandle; 8]> = state
                .cards
                .iter()
                .filter(|c| {
                    !c.removed_from_state
                        && c.pile == PileType::None
                        && state
                            .card_ext(c.ext_index)
                            .is_some_and(|e| e.private_fields.contains_key(CHOICES_PARADOX_OFFER_MARKER))
                })
                .map(|c| c.handle)
                .collect();
            let chosen = selected.first().copied();
            for &cand in &offer_handles {
                if let Some(ext) = state
                    .card(cand)
                    .and_then(|c| c.ext_index)
                    .and_then(|idx| state.card_extensions.get_mut(idx as usize))
                {
                    ext.private_fields.remove(CHOICES_PARADOX_OFFER_MARKER);
                }
                if Some(cand) != chosen {
                    if let Some(c) = state.card_mut(cand) {
                        c.removed_from_state = true;
                    }
                    state.invalidate_hook_cache();
                }
            }
            if let Some(h) = chosen {
                let pc = state.player.creature_handle;
                enter_generated_into_hand_with_creator(content, state, h, Some(pc))?;
            }
            Ok(HookResult::default())
        }
        _ => Err(KernelError::Unsupported {
            kind: "RelicHookChoiceResume",
            id: format!("{}:{slot:?}", id.model_id_str()),
        }),
    }
}
