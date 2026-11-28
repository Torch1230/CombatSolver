use smallvec::SmallVec;
use sts2_kernel::commands::{
    autoplay, block, card, card_factory, card_pile, card_select, creature, damage, forge, gold,
    orb, osty, potion as potion_cmd, power,
};
use sts2_kernel::content_dispatch::CardPlayOutcome;
use sts2_kernel::{
    BattleState, CardHandle, CardInst, CardTempFlags, ChoiceTiming, ContentDispatcher,
    CreatureHandle, HookArgs, HookResult, KernelError, LocalEnergyModifier, StepOutcome, hooks,
    ordering,
};
use sts2_types::generated_ids::generated_card_meta;
use sts2_types::{
    CardId, CardKeywords, CardPilePosition, CardStaticFlags, CardTags, CardType, CharacterId, Dec,
    HookMask, HookSlot, LocalEnergyModifierKind, PileType, PlayerRngType, PotionId, PotionMeta,
    PowerId, RunRngType, TargetType, ValueProp,
};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PotionRarity {
    Common,
    Uncommon,
    Rare,
    Event,
    Token,
}

#[inline]
const fn make_meta(
    target_type: TargetType,
    uses_rng: bool,
    may_heal: bool,
    hook_mask: HookMask,
) -> PotionMeta {
    PotionMeta {
        target_type,
        uses_rng,
        may_heal,
        hook_mask,
    }
}

static AMBERGRIS_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, true, HookMask::EMPTY);
static ASHWATER_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static ATTACK_POTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, true, false, HookMask::EMPTY);
static BEETLE_JUICE_META: PotionMeta = make_meta(TargetType::AnyEnemy, false, false, HookMask::EMPTY);
static BLESSING_OF_THE_FORGE_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static BLOCK_POTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static BLOOD_POTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, true, HookMask::EMPTY);
static BONE_BREW_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static BOTTLED_POTENTIAL_META: PotionMeta = make_meta(TargetType::AnyPlayer, true, false, HookMask::EMPTY);
static BOTTLED_SONG_META: PotionMeta = make_meta(TargetType::AnyPlayer, true, false, HookMask::EMPTY);
static CLARITY_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static COLORLESS_POTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, true, false, HookMask::EMPTY);
static COSMIC_CONCOCTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, true, false, HookMask::EMPTY);
static CRYSTALLINE_EUPHORIA_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static CUNNING_POTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static CURE_ALL_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static DEXTERITY_POTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static DISTILLED_CHAOS_META: PotionMeta = make_meta(TargetType::AnyPlayer, true, false, HookMask::EMPTY);
static DROPLET_OF_PRECOGNITION_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static DUPLICATOR_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static ENERGY_POTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static ENTROPIC_BREW_META: PotionMeta = make_meta(TargetType::AnyPlayer, true, false, HookMask::EMPTY);
static ESSENCE_OF_DARKNESS_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static EXPLOSIVE_AMPOULE_META: PotionMeta = make_meta(TargetType::AllEnemies, false, false, HookMask::EMPTY);
static FAIRY_IN_A_BOTTLE_META: PotionMeta = make_meta(
    TargetType::SelfTarget,
    false,
    true,
    HookMask::EMPTY
        .with(HookSlot::ShouldDie)
        .with(HookSlot::AfterPreventingDeath),
);
static FIRE_POTION_META: PotionMeta = make_meta(TargetType::AnyEnemy, false, false, HookMask::EMPTY);
static FLEX_POTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static FOCUS_POTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static FORTIFIER_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static FOUL_POTION_META: PotionMeta = make_meta(TargetType::AllEnemies, false, false, HookMask::EMPTY);
static FRUIT_JUICE_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, true, HookMask::EMPTY);
static FYSH_OIL_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static GAMBLERS_BREW_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static GHOST_IN_A_JAR_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static GIGANTIFICATION_POTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static GLOWWATER_POTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static HEART_OF_IRON_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static KINGS_COURAGE_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static LIQUID_BRONZE_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static LIQUID_MEMORIES_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static LUCKY_TONIC_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static MAZALETHS_GIFT_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static OROBIC_ACID_META: PotionMeta = make_meta(TargetType::AnyPlayer, true, false, HookMask::EMPTY);
static POISON_POTION_META: PotionMeta = make_meta(TargetType::AnyEnemy, false, false, HookMask::EMPTY);
static POTION_OF_BINDING_META: PotionMeta = make_meta(TargetType::AllEnemies, false, false, HookMask::EMPTY);
static POTION_OF_CAPACITY_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static POTION_OF_DOOM_META: PotionMeta = make_meta(TargetType::AnyEnemy, false, false, HookMask::EMPTY);
static POTION_SHAPED_ROCK_META: PotionMeta = make_meta(TargetType::AnyEnemy, false, false, HookMask::EMPTY);
static POT_OF_GHOULS_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static POWDERED_DEMISE_META: PotionMeta = make_meta(TargetType::AnyEnemy, false, false, HookMask::EMPTY);
static POWER_POTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, true, false, HookMask::EMPTY);
static PRECISION_POTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static RADIANT_TINCTURE_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static REGEN_POTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, true, HookMask::EMPTY);
static SHACKLING_POTION_META: PotionMeta = make_meta(TargetType::AllEnemies, false, false, HookMask::EMPTY);
static SHIP_IN_A_BOTTLE_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static SKILL_POTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, true, false, HookMask::EMPTY);
static SNECKO_OIL_META: PotionMeta = make_meta(TargetType::AnyPlayer, true, false, HookMask::EMPTY);
static SOLDIERS_STEW_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static SPEED_POTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static STABLE_SERUM_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static STAR_POTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static STRENGTH_POTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static SWIFT_POTION_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static TOUCH_OF_INSANITY_META: PotionMeta = make_meta(TargetType::AnyPlayer, false, false, HookMask::EMPTY);
static VULNERABLE_POTION_META: PotionMeta = make_meta(TargetType::AnyEnemy, false, false, HookMask::EMPTY);
static WEAK_POTION_META: PotionMeta = make_meta(TargetType::AnyEnemy, false, false, HookMask::EMPTY);

pub fn potion_meta(id: PotionId) -> Result<&'static PotionMeta, KernelError> {
    match id {
        PotionId::Ambergris => Ok(&AMBERGRIS_META),
        PotionId::Ashwater => Ok(&ASHWATER_META),
        PotionId::AttackPotion => Ok(&ATTACK_POTION_META),
        PotionId::BeetleJuice => Ok(&BEETLE_JUICE_META),
        PotionId::BlessingOfTheForge => Ok(&BLESSING_OF_THE_FORGE_META),
        PotionId::BlockPotion => Ok(&BLOCK_POTION_META),
        PotionId::BloodPotion => Ok(&BLOOD_POTION_META),
        PotionId::BoneBrew => Ok(&BONE_BREW_META),
        PotionId::BottledPotential => Ok(&BOTTLED_POTENTIAL_META),
        PotionId::BottledSong => Ok(&BOTTLED_SONG_META),
        PotionId::Clarity => Ok(&CLARITY_META),
        PotionId::ColorlessPotion => Ok(&COLORLESS_POTION_META),
        PotionId::CosmicConcoction => Ok(&COSMIC_CONCOCTION_META),
        PotionId::CrystallineEuphoria => Ok(&CRYSTALLINE_EUPHORIA_META),
        PotionId::CunningPotion => Ok(&CUNNING_POTION_META),
        PotionId::CureAll => Ok(&CURE_ALL_META),
        PotionId::DexterityPotion => Ok(&DEXTERITY_POTION_META),
        PotionId::DistilledChaos => Ok(&DISTILLED_CHAOS_META),
        PotionId::DropletOfPrecognition => Ok(&DROPLET_OF_PRECOGNITION_META),
        PotionId::Duplicator => Ok(&DUPLICATOR_META),
        PotionId::EnergyPotion => Ok(&ENERGY_POTION_META),
        PotionId::EntropicBrew => Ok(&ENTROPIC_BREW_META),
        PotionId::EssenceOfDarkness => Ok(&ESSENCE_OF_DARKNESS_META),
        PotionId::ExplosiveAmpoule => Ok(&EXPLOSIVE_AMPOULE_META),
        PotionId::FairyInABottle => Ok(&FAIRY_IN_A_BOTTLE_META),
        PotionId::FirePotion => Ok(&FIRE_POTION_META),
        PotionId::FlexPotion => Ok(&FLEX_POTION_META),
        PotionId::FocusPotion => Ok(&FOCUS_POTION_META),
        PotionId::Fortifier => Ok(&FORTIFIER_META),
        PotionId::FoulPotion => Ok(&FOUL_POTION_META),
        PotionId::FruitJuice => Ok(&FRUIT_JUICE_META),
        PotionId::FyshOil => Ok(&FYSH_OIL_META),
        PotionId::GamblersBrew => Ok(&GAMBLERS_BREW_META),
        PotionId::GhostInAJar => Ok(&GHOST_IN_A_JAR_META),
        PotionId::GigantificationPotion => Ok(&GIGANTIFICATION_POTION_META),
        PotionId::GlowwaterPotion => Ok(&GLOWWATER_POTION_META),
        PotionId::HeartOfIron => Ok(&HEART_OF_IRON_META),
        PotionId::KingsCourage => Ok(&KINGS_COURAGE_META),
        PotionId::LiquidBronze => Ok(&LIQUID_BRONZE_META),
        PotionId::LiquidMemories => Ok(&LIQUID_MEMORIES_META),
        PotionId::LuckyTonic => Ok(&LUCKY_TONIC_META),
        PotionId::MazalethsGift => Ok(&MAZALETHS_GIFT_META),
        PotionId::OrobicAcid => Ok(&OROBIC_ACID_META),
        PotionId::PoisonPotion => Ok(&POISON_POTION_META),
        PotionId::PotionOfBinding => Ok(&POTION_OF_BINDING_META),
        PotionId::PotionOfCapacity => Ok(&POTION_OF_CAPACITY_META),
        PotionId::PotionOfDoom => Ok(&POTION_OF_DOOM_META),
        PotionId::PotionShapedRock => Ok(&POTION_SHAPED_ROCK_META),
        PotionId::PotOfGhouls => Ok(&POT_OF_GHOULS_META),
        PotionId::PowderedDemise => Ok(&POWDERED_DEMISE_META),
        PotionId::PowerPotion => Ok(&POWER_POTION_META),
        PotionId::PrecisionPotion => Ok(&PRECISION_POTION_META),
        PotionId::RadiantTincture => Ok(&RADIANT_TINCTURE_META),
        PotionId::RegenPotion => Ok(&REGEN_POTION_META),
        PotionId::ShacklingPotion => Ok(&SHACKLING_POTION_META),
        PotionId::ShipInABottle => Ok(&SHIP_IN_A_BOTTLE_META),
        PotionId::SkillPotion => Ok(&SKILL_POTION_META),
        PotionId::SneckoOil => Ok(&SNECKO_OIL_META),
        PotionId::SoldiersStew => Ok(&SOLDIERS_STEW_META),
        PotionId::SpeedPotion => Ok(&SPEED_POTION_META),
        PotionId::StableSerum => Ok(&STABLE_SERUM_META),
        PotionId::StarPotion => Ok(&STAR_POTION_META),
        PotionId::StrengthPotion => Ok(&STRENGTH_POTION_META),
        PotionId::SwiftPotion => Ok(&SWIFT_POTION_META),
        PotionId::TouchOfInsanity => Ok(&TOUCH_OF_INSANITY_META),
        PotionId::VulnerablePotion => Ok(&VULNERABLE_POTION_META),
        PotionId::WeakPotion => Ok(&WEAK_POTION_META),
    }
}

pub const fn potion_rarity(id: PotionId) -> PotionRarity {
    match id {
        PotionId::Ambergris | PotionId::FoulPotion | PotionId::GlowwaterPotion => {
            PotionRarity::Event
        }
        PotionId::PotionShapedRock => PotionRarity::Token,
        PotionId::AttackPotion
        | PotionId::BlockPotion
        | PotionId::BloodPotion
        | PotionId::ColorlessPotion
        | PotionId::DexterityPotion
        | PotionId::EnergyPotion
        | PotionId::ExplosiveAmpoule
        | PotionId::FirePotion
        | PotionId::FlexPotion
        | PotionId::FocusPotion
        | PotionId::PoisonPotion
        | PotionId::PotionOfDoom
        | PotionId::PowerPotion
        | PotionId::PrecisionPotion
        | PotionId::SkillPotion
        | PotionId::SpeedPotion
        | PotionId::StarPotion
        | PotionId::StrengthPotion
        | PotionId::SwiftPotion
        | PotionId::VulnerablePotion
        | PotionId::WeakPotion => PotionRarity::Common,
        PotionId::Ashwater
        | PotionId::BlessingOfTheForge
        | PotionId::BoneBrew
        | PotionId::BottledSong
        | PotionId::Clarity
        | PotionId::CunningPotion
        | PotionId::CureAll
        | PotionId::Duplicator
        | PotionId::Fortifier
        | PotionId::FyshOil
        | PotionId::GamblersBrew
        | PotionId::HeartOfIron
        | PotionId::KingsCourage
        | PotionId::LiquidBronze
        | PotionId::PotionOfBinding
        | PotionId::PotionOfCapacity
        | PotionId::PowderedDemise
        | PotionId::RadiantTincture
        | PotionId::RegenPotion
        | PotionId::StableSerum
        | PotionId::TouchOfInsanity => PotionRarity::Uncommon,
        PotionId::BeetleJuice
        | PotionId::BottledPotential
        | PotionId::CosmicConcoction
        | PotionId::CrystallineEuphoria
        | PotionId::DistilledChaos
        | PotionId::DropletOfPrecognition
        | PotionId::EntropicBrew
        | PotionId::EssenceOfDarkness
        | PotionId::FairyInABottle
        | PotionId::FruitJuice
        | PotionId::GhostInAJar
        | PotionId::GigantificationPotion
        | PotionId::LiquidMemories
        | PotionId::LuckyTonic
        | PotionId::MazalethsGift
        | PotionId::OrobicAcid
        | PotionId::PotOfGhouls
        | PotionId::ShacklingPotion
        | PotionId::ShipInABottle
        | PotionId::SneckoOil
        | PotionId::SoldiersStew => PotionRarity::Rare,
    }
}

static SHARED_POTION_POOL: &[PotionId] = &[
    PotionId::AttackPotion,
    PotionId::BeetleJuice,
    PotionId::BlessingOfTheForge,
    PotionId::BlockPotion,
    PotionId::BottledPotential,
    PotionId::Clarity,
    PotionId::ColorlessPotion,
    PotionId::CureAll,
    PotionId::DexterityPotion,
    PotionId::DistilledChaos,
    PotionId::DropletOfPrecognition,
    PotionId::Duplicator,
    PotionId::EnergyPotion,
    PotionId::EntropicBrew,
    PotionId::ExplosiveAmpoule,
    PotionId::FairyInABottle,
    PotionId::FirePotion,
    PotionId::FlexPotion,
    PotionId::Fortifier,
    PotionId::FruitJuice,
    PotionId::FyshOil,
    PotionId::GamblersBrew,
    PotionId::GigantificationPotion,
    PotionId::HeartOfIron,
    PotionId::LiquidBronze,
    PotionId::LiquidMemories,
    PotionId::LuckyTonic,
    PotionId::MazalethsGift,
    PotionId::OrobicAcid,
    PotionId::PotionOfBinding,
    PotionId::PowderedDemise,
    PotionId::PowerPotion,
    PotionId::RadiantTincture,
    PotionId::RegenPotion,
    PotionId::ShacklingPotion,
    PotionId::ShipInABottle,
    PotionId::SkillPotion,
    PotionId::SneckoOil,
    PotionId::SpeedPotion,
    PotionId::StableSerum,
    PotionId::StrengthPotion,
    PotionId::SwiftPotion,
    PotionId::TouchOfInsanity,
    PotionId::VulnerablePotion,
    PotionId::WeakPotion,
];

fn character_potion_pool(character: CharacterId) -> &'static [PotionId] {
    match character {
        CharacterId::Ironclad => &[
            PotionId::BloodPotion,
            PotionId::SoldiersStew,
            PotionId::Ashwater,
        ],
        CharacterId::Silent => &[
            PotionId::PoisonPotion,
            PotionId::GhostInAJar,
            PotionId::CunningPotion,
        ],
        CharacterId::Defect => &[
            PotionId::FocusPotion,
            PotionId::EssenceOfDarkness,
            PotionId::PotionOfCapacity,
        ],
        CharacterId::Regent => &[
            PotionId::StarPotion,
            PotionId::CosmicConcoction,
            PotionId::KingsCourage,
        ],
        CharacterId::Necrobinder => &[
            PotionId::PotionOfDoom,
            PotionId::PotOfGhouls,
            PotionId::BoneBrew,
        ],
        CharacterId::Fool => &[
            PotionId::PrecisionPotion,
            PotionId::BottledSong,
            PotionId::CrystallineEuphoria,
        ],
    }
}

fn create_offer_card_instance(state: &mut BattleState, card_id: CardId) -> CardHandle {
    let meta = generated_card_meta(card_id);
    let handle = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    let mut temp_flags = CardTempFlags::POTION_OFFER;
    if meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST) {
        temp_flags |= CardTempFlags::COSTS_X_ENERGY;
    }
    state.push_card(CardInst {
        handle,
        id: card_id,
        upgrade_level: 0,
        canonical_base_energy_cost: meta
            .canonical_energy_cost
            .clamp(i16::MIN as i32, i16::MAX as i32) as i16,
        base_energy_cost: meta
            .canonical_energy_cost
            .clamp(i16::MIN as i32, i16::MAX as i32) as i16,
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
        temp_flags,
        affliction: None,
        ext_index: None,
        pile: PileType::None,
        removed_from_state: false,
        transform_into_after_play: None,
    });
    handle
}

fn set_to_free_this_turn(state: &mut BattleState, handle: CardHandle) {
    let Some(card) = state.card(handle) else {
        return;
    };
    let meta = generated_card_meta(card.id);
    if !meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST) {
        if let Some(c) = state.card_mut(handle) {
            c.temp_cost_override_this_turn_or_until_played = sts2_kernel::OptI16::some(0);
            state.mark_eot_card_cleanup_dirty();
        }
        if let Some(ext) = state.card_ext_mut(handle) {
            ext.push_local_energy_modifier(LocalEnergyModifier {
                kind: LocalEnergyModifierKind::SetThisTurnOrUntilPlayed,
                amount: 0,
                is_reduce_only: false,
            });
        }
    }
    if !meta.flags.contains(CardStaticFlags::IS_X_STAR_COST) {
        if let Some(c) = state.card_mut(handle) {
            c.temp_star_cost_override_this_turn = sts2_kernel::OptI16::some(0);
            state.mark_eot_card_cleanup_dirty();
        }
    }
}

fn make_free_this_combat(state: &mut BattleState, handle: CardHandle) {
    if let Some(c) = state.card_mut(handle) {
        c.temp_flags.insert(CardTempFlags::TEMPORARY_FREE_COMBAT);
    }
    if let Some(ext) = state.card_ext_mut(handle) {
        ext.push_local_energy_modifier(LocalEnergyModifier {
            kind: LocalEnergyModifierKind::FreeThisCombat,
            amount: 0,
            is_reduce_only: false,
        });
    }
}

fn generate_existing_card_into_hand(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    handle: CardHandle,
) -> Result<(), KernelError> {
    if !state.is_live_combat() {
        return Err(KernelError::InvalidState(
            "Generated cards require active combat".to_owned(),
        ));
    }
    let cid = state
        .card(handle)
        .ok_or_else(|| KernelError::InvalidState("missing generated card".to_owned()))?
        .id;
    let _ = content.card_meta(cid)?;
    let creator = Some(state.player.creature_handle);
    card_pile::enter_combat(
        state,
        content,
        handle,
        PileType::Hand,
        CardPilePosition::Bottom,
        true,
    )?;
    hooks::after_card_generated(state, content, handle, creator)?;
    hooks::after_card_generated_for_combat(state, content, handle, creator)
}

fn require_player_target(
    state: &BattleState,
    target: Option<CreatureHandle>,
    potion_id: PotionId,
) -> Result<CreatureHandle, KernelError> {
    let t = target.ok_or_else(|| {
        KernelError::InvalidAction(format!(
            "potion {} requires a player target",
            potion_id.model_id_str()
        ))
    })?;
    if t != state.player.creature_handle {
        return Err(KernelError::InvalidAction(format!(
            "potion {} target {:?} is not the active player",
            potion_id.model_id_str(),
            t
        )));
    }
    Ok(t)
}

fn require_enemy_target(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: Option<CreatureHandle>,
    potion_id: PotionId,
) -> Result<CreatureHandle, KernelError> {
    let t = target.ok_or_else(|| {
        KernelError::InvalidAction(format!(
            "potion {} requires an enemy target",
            potion_id.model_id_str()
        ))
    })?;
    let valid = state.enemy_handles.contains(&t)
        && state.creature(t).is_some_and(|c| c.is_alive())
        && hooks::should_allow_hitting(state, content, t)?;
    if !valid {
        return Err(KernelError::InvalidAction(format!(
            "potion {} enemy target {:?} is invalid or dead",
            potion_id.model_id_str(),
            t
        )));
    }
    Ok(t)
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

pub fn ashwater_candidates(state: &BattleState) -> SmallVec<[CardHandle; 16]> {
    if hooks::is_ending(state) || state.player_creature().is_none_or(|c| c.is_dead()) {
        return SmallVec::new();
    }
    state.player.hand.iter().copied().collect()
}

pub fn ashwater_resume(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    selected: &[CardHandle],
) -> Result<(), KernelError> {
    for &h in selected {
        card_pile::exhaust(state, content, h, false)?;
    }
    Ok(())
}

pub fn gamblers_brew_candidates(state: &BattleState) -> SmallVec<[CardHandle; 16]> {
    if hooks::is_ending(state) || state.player_creature().is_none_or(|c| c.is_dead()) {
        return SmallVec::new();
    }
    state.player.hand.iter().copied().collect()
}

pub fn gamblers_brew_resume(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    selected: &[CardHandle],
) -> Result<(), KernelError> {
    if selected.is_empty() || hooks::is_over_or_ending_checked(state, content)? {
        return Ok(());
    }
    card::discard_batch(state, content, selected)?;
    let _ = card_pile::draw(state, content, selected.len() as i32, false)?;
    Ok(())
}

pub fn droplet_of_precognition_candidates(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<SmallVec<[CardHandle; 16]>, KernelError> {
    if hooks::is_ending_checked(state, content)?
        || state.player_creature().is_none_or(|c| c.is_dead())
    {
        return Ok(SmallVec::new());
    }
    let mut out: SmallVec<[CardHandle; 16]> = state.player.draw.iter().copied().collect();
    if out.len() > 1 {
        if out.len() <= u16::MAX as usize {
            let mut pairs: SmallVec<[(u64, CardHandle); 64]> = SmallVec::with_capacity(out.len());
            for (orig_idx, &h) in out.iter().enumerate() {
                let cid = state
                    .card(h)
                    .ok_or_else(|| KernelError::InvalidState("missing draw card".to_owned()))?
                    .id;
                let key = (u64::from(crate::card_draw_sort_key(cid)) << 16) | (orig_idx as u64);
                pairs.push((key, h));
            }
            pairs.sort_unstable_by_key(|&(k, _)| k);
            out.clear();
            out.extend(pairs.into_iter().map(|(_, h)| h));
        } else {
            let mut pairs: SmallVec<[(u32, CardHandle); 64]> = SmallVec::with_capacity(out.len());
            for &h in &out {
                let cid = state
                    .card(h)
                    .ok_or_else(|| KernelError::InvalidState("missing draw card".to_owned()))?
                    .id;
                pairs.push((crate::card_draw_sort_key(cid), h));
            }
            pairs.sort_by_key(|&(k, _)| k);
            out = pairs.into_iter().map(|(_, h)| h).collect();
        }
    }
    Ok(out)
}

pub fn droplet_of_precognition_resume(
    state: &mut BattleState,
    selected: &[CardHandle],
) -> Result<(), KernelError> {
    if let Some(&h) = selected.first() {
        card_pile::add_sync(state, h, PileType::Hand, CardPilePosition::Bottom);
    }
    Ok(())
}

pub fn liquid_memories_candidates(state: &BattleState) -> SmallVec<[CardHandle; 16]> {
    if hooks::is_ending(state) || state.player_creature().is_none_or(|c| c.is_dead()) {
        return SmallVec::new();
    }
    state.player.discard.iter().copied().collect()
}

pub fn liquid_memories_resume(
    state: &mut BattleState,
    selected: &[CardHandle],
) -> Result<(), KernelError> {
    if let Some(&h) = selected.first() {
        set_to_free_this_turn(state, h);
        card_pile::add_sync(state, h, PileType::Hand, CardPilePosition::Bottom);
    }
    Ok(())
}

pub fn touch_of_insanity_candidates(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<SmallVec<[CardHandle; 16]>, KernelError> {
    if hooks::is_ending_checked(state, content)?
        || state.player_creature().is_none_or(|c| c.is_dead())
    {
        return Ok(SmallVec::new());
    }
    let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
    let mut out = SmallVec::new();
    for h in hand {
        let cid = state
            .card(h)
            .ok_or_else(|| KernelError::InvalidState("missing hand card".to_owned()))?
            .id;
        let meta = content
            .card_meta(cid)
            .unwrap_or_else(|_| generated_card_meta(cid));
        let is_x_energy = meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST);
        let is_x_star = meta.flags.contains(CardStaticFlags::IS_X_STAR_COST);
        let local_energy = ordering::resolve_local_energy_cost(state, h, is_x_energy);
        let local_star = ordering::resolve_local_star_cost(state, h, meta.canonical_star_cost);
        let resolved_energy = ordering::resolve_energy_cost(state, content, h)?;
        let resolved_star = ordering::resolve_star_cost(state, content, h)?;
        let eligible = local_energy > 0
            || local_star > 0
            || (!is_x_energy && resolved_energy > 0)
            || (!is_x_star && resolved_star > 0);
        if eligible {
            out.push(h);
        }
    }
    Ok(out)
}

pub fn touch_of_insanity_resume(
    state: &mut BattleState,
    selected: &[CardHandle],
) -> Result<(), KernelError> {
    if let Some(&h) = selected.first() {
        make_free_this_combat(state, h);
    }
    Ok(())
}

pub fn card_choice_potion_candidates(
    state: &mut BattleState,
    potion_id: PotionId,
) -> Result<SmallVec<[CardHandle; 16]>, KernelError> {
    let colorless_pool;
    let char_pool;
    let pool: &[CardId] = match potion_id {
        PotionId::ColorlessPotion => {
            colorless_pool = card_factory::colorless_card_pool_candidates(state);
            &colorless_pool
        }
        _ => {
            char_pool = card_factory::character_combat_pool(state);
            &char_pool
        }
    };
    let chosen_ids = match potion_id {
        PotionId::AttackPotion => card_factory::get_distinct_for_combat_filtered(
            state,
            pool,
            3,
            RunRngType::CombatCardGeneration,
            |cid| generated_card_meta(cid).card_type == CardType::Attack,
        ),
        PotionId::SkillPotion => card_factory::get_distinct_for_combat_filtered(
            state,
            pool,
            3,
            RunRngType::CombatCardGeneration,
            |cid| generated_card_meta(cid).card_type == CardType::Skill,
        ),
        PotionId::PowerPotion => card_factory::get_distinct_for_combat_filtered(
            state,
            pool,
            3,
            RunRngType::CombatCardGeneration,
            |cid| generated_card_meta(cid).card_type == CardType::Power,
        ),
        PotionId::ColorlessPotion => card_factory::get_distinct_for_combat_filtered(
            state,
            pool,
            3,
            RunRngType::CombatCardGeneration,
            |cid| {
                generated_card_meta(cid)
                    .flags
                    .contains(CardStaticFlags::IS_COLORLESS)
            },
        ),
        _ => {
            return Err(KernelError::InvalidAction(format!(
                "{} is not a card-choice potion",
                potion_id.model_id_str()
            )));
        }
    };
    let mut handles = SmallVec::new();
    for cid in chosen_ids {
        handles.push(create_offer_card_instance(state, cid));
    }
    Ok(handles)
}

pub fn card_choice_potion_resume(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    all_candidates: &[CardHandle],
    selected: &[CardHandle],
) -> Result<(), KernelError> {
    let chosen = selected.first().copied();
    for &cand in all_candidates {
        let mut removed_pile = PileType::None;
        if let Some(c) = state.card_mut(cand) {
            c.temp_flags.remove(CardTempFlags::POTION_OFFER);
            if Some(cand) != chosen {
                removed_pile = c.pile;
                c.removed_from_state = true;
            }
        }
        if removed_pile != PileType::None {
            state.invalidate_pile_hooks(sts2_kernel::state::pile_dirty_bit(removed_pile));
        }
    }
    if let Some(h) = chosen {
        set_to_free_this_turn(state, h);
        generate_existing_card_into_hand(content, state, h)?;
    }
    Ok(())
}

fn card_choice_potion_resume_from_pending(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    selected: &[CardHandle],
) -> Result<(), KernelError> {
    let offer_handles: SmallVec<[CardHandle; 4]> = state
        .cards
        .iter()
        .filter(|c| {
            !c.removed_from_state
                && c.pile == PileType::None
                && c.temp_flags.contains(CardTempFlags::POTION_OFFER)
        })
        .map(|c| c.handle)
        .collect();
    card_choice_potion_resume(content, state, &offer_handles, selected)
}

pub fn potion_generation_pool_candidates(
    state: &BattleState,
    in_combat_only: bool,
) -> SmallVec<[PotionId; 64]> {
    let mut pool = SmallVec::<[PotionId; 64]>::new();
    let push_if_valid = |pool: &mut SmallVec<[PotionId; 64]>, p: PotionId| {
        if in_combat_only
            && matches!(
                p,
                PotionId::RegenPotion | PotionId::FruitJuice | PotionId::FairyInABottle
            )
        {
            return;
        }
        if !pool.contains(&p) {
            pool.push(p);
        }
    };
    if !state.static_ctx.potion_generation_pool.is_empty() {
        for &p in &state.static_ctx.potion_generation_pool {
            push_if_valid(&mut pool, p);
        }
        return pool;
    }
    for &p in character_potion_pool(state.static_ctx.character_id) {
        push_if_valid(&mut pool, p);
    }
    for &p in SHARED_POTION_POOL {
        push_if_valid(&mut pool, p);
    }
    pool
}

fn roll_potion_by_rarity(
    rng: &mut sts2_types::Rng,
    pool: &[PotionId],
) -> Option<PotionId> {
    let roll = rng
        .next_f32_range(0.0_f32, 1.0_f32)
        .expect("0..1 f32 range is valid");
    let rarity = if roll <= 0.1_f32 {
        PotionRarity::Rare
    } else if roll <= 0.35_f32 {
        PotionRarity::Uncommon
    } else {
        PotionRarity::Common
    };
    let matching: SmallVec<[PotionId; 64]> = pool
        .iter()
        .copied()
        .filter(|&p| potion_rarity(p) == rarity)
        .collect();
    if let Some(&picked) = rng.next_item(&matching) {
        return Some(picked);
    }
    rng.next_item(pool).copied()
}

pub fn alchemize_roll_in_combat_potion(state: &mut BattleState) -> Option<PotionId> {
    let pool = potion_generation_pool_candidates(state, true);
    if pool.is_empty() {
        return None;
    }
    let rng = state.run_rngs.get_mut(RunRngType::CombatPotionGeneration);
    roll_potion_by_rarity(rng, &pool)
}

pub fn entropic_brew_roll_out_of_combat_potion(state: &mut BattleState) -> Option<PotionId> {
    let pool = potion_generation_pool_candidates(state, false);
    if pool.is_empty() {
        return None;
    }
    let rng = state.player_rngs.get_mut(PlayerRngType::NonCombatGeneration);
    roll_potion_by_rarity(rng, &pool)
}

pub fn potion_on_use(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    potion_id: PotionId,
    target: Option<CreatureHandle>,
    choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
    _resume_step: u8,
) -> Result<CardPlayOutcome, KernelError> {
    let owner = state.player.creature_handle;
    match potion_id {
        PotionId::Ambergris => {
            let t = require_player_target(state, target, potion_id)?;
            let max_hp = state
                .creature(t)
                .ok_or_else(|| KernelError::InvalidState("missing target creature".to_owned()))?
                .max_hp;
            let heal_amt = Dec::from_i32(max_hp).checked_mul(Dec::HALF)?;
            creature::heal(state, content, t, heal_amt)?;
            if state.is_live_combat() && state.contains_creature(t) {
                power::apply(
                    state,
                    content,
                    PowerId::ExtraTurnPower,
                    t,
                    Dec::ONE,
                    Some(owner),
                    None,
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        PotionId::Ashwater => {
            let _ = require_player_target(state, target, potion_id)?;
            let cands = ashwater_candidates(state);
            match card_select::select_cards(
                state,
                &cands,
                0,
                usize::MAX,
                ChoiceTiming::Action,
                potion_id.model_id_str(),
                PileType::Hand,
                false,
                choice_queue,
            )? {
                card_select::SelectResult::Selected(sel) => {
                    ashwater_resume(content, state, &sel)?;
                    Ok(CardPlayOutcome::default())
                }
                card_select::SelectResult::NeedsChoice(req) => {
                    Ok(CardPlayOutcome::with_choice(req, 1))
                }
            }
        }
        PotionId::AttackPotion
        | PotionId::SkillPotion
        | PotionId::PowerPotion
        | PotionId::ColorlessPotion => {
            let _ = require_player_target(state, target, potion_id)?;
            let cands = card_choice_potion_candidates(state, potion_id)?;
            match card_select::select_cards(
                state,
                &cands,
                0,
                1,
                ChoiceTiming::Action,
                potion_id.model_id_str(),
                PileType::None,
                true,
                choice_queue,
            )? {
                card_select::SelectResult::Selected(sel) => {
                    card_choice_potion_resume(content, state, &cands, &sel)?;
                    Ok(CardPlayOutcome::default())
                }
                card_select::SelectResult::NeedsChoice(req) => {
                    Ok(CardPlayOutcome::with_choice(req, 1))
                }
            }
        }
        PotionId::BeetleJuice => {
            let t = require_enemy_target(state, content, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::ShrinkPower,
                t,
                Dec::from_i32(4),
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::BlessingOfTheForge => {
            let _ = require_player_target(state, target, potion_id)?;
            let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
            for h in hand {
                let Some(ci) = state.card(h) else {
                    continue;
                };
                let max_upg = content.card_meta(ci.id)?.max_upgrade_level;
                if ci.upgrade_level < max_upg {
                    card::upgrade(state, content, h)?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        PotionId::BlockPotion => {
            let t = require_player_target(state, target, potion_id)?;
            block::gain_block(
                state,
                content,
                t,
                Dec::from_i32(12),
                ValueProp::UNPOWERED,
                None,
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::BloodPotion => {
            let t = require_player_target(state, target, potion_id)?;
            let max_hp = state
                .creature(t)
                .ok_or_else(|| KernelError::InvalidState("missing target creature".to_owned()))?
                .max_hp;
            let amt = Dec::from_i32(max_hp)
                .checked_mul(Dec::from_i32(20))?
                .checked_div(Dec::from_i32(100))?;
            creature::heal(state, content, t, amt)?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::BoneBrew => {
            let _ = require_player_target(state, target, potion_id)?;
            let _ = osty::summon(state, content, Dec::from_i32(15))?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::BottledPotential => {
            let _ = require_player_target(state, target, potion_id)?;
            let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
            for h in hand {
                card_pile::add_sync(state, h, PileType::Draw, CardPilePosition::Bottom);
            }
            card_pile::shuffle(state, content)?;
            let _ = card_pile::draw(state, content, 5, false)?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::BottledSong => {
            let _ = require_player_target(state, target, potion_id)?;
            let pool = card_factory::melody_card_pool_candidates(state);
            let tags = [CardTags::WAH, CardTags::CHO, CardTags::KI];
            let mut generated = SmallVec::<[CardHandle; 3]>::new();
            for tag in tags {
                let picked = card_factory::get_distinct_for_combat_filtered(
                    state,
                    &pool,
                    1,
                    RunRngType::CombatCardGeneration,
                    |cid| generated_card_meta(cid).base_tags.contains(tag),
                );
                if let Some(&cid) = picked.first() {
                    let h = card_pile::create_card_in_combat(state, content, cid, 0)?;
                    set_to_free_this_turn(state, h);
                    generated.push(h);
                }
            }
            for h in generated {
                generate_existing_card_into_hand(content, state, h)?;
            }
            Ok(CardPlayOutcome::default())
        }
        PotionId::Clarity => {
            let t = require_player_target(state, target, potion_id)?;
            let _ = card_pile::draw(state, content, 1, false)?;
            power::apply(
                state,
                content,
                PowerId::ClarityPower,
                t,
                Dec::from_i32(3),
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::CosmicConcoction => {
            let _ = require_player_target(state, target, potion_id)?;
            let pool = card_factory::colorless_card_pool_candidates(state);
            let chosen = card_factory::get_distinct_for_combat_filtered(
                state,
                &pool,
                3,
                RunRngType::CombatCardGeneration,
                |_| true,
            );
            for cid in chosen {
                let h = card_pile::create_card_in_combat(state, content, cid, 1)?;
                generate_existing_card_into_hand(content, state, h)?;
            }
            Ok(CardPlayOutcome::default())
        }
        PotionId::CrystallineEuphoria => {
            let _ = require_player_target(state, target, potion_id)?;
            for _ in 0..3 {
                let _ = card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    CardId::Joy,
                    PileType::Hand,
                    CardPilePosition::Bottom,
                    0,
                    Some(owner),
                )?;
            }
            let sadness_count = card_pile::MAX_CARDS_IN_HAND.saturating_sub(state.player.hand.len());
            for _ in 0..sadness_count {
                let _ = card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    CardId::Sadness,
                    PileType::Hand,
                    CardPilePosition::Bottom,
                    0,
                    Some(owner),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        PotionId::CunningPotion => {
            let _ = require_player_target(state, target, potion_id)?;
            let mut shivs = [CardHandle(0); 3];
            for slot in &mut shivs {
                *slot = card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    CardId::Shiv,
                    PileType::Hand,
                    CardPilePosition::Bottom,
                    0,
                    Some(owner),
                )?;
            }
            for h in shivs {
                card::upgrade(state, content, h)?;
            }
            Ok(CardPlayOutcome::default())
        }
        PotionId::CureAll => {
            let _ = require_player_target(state, target, potion_id)?;
            gold::gain_energy(state, content, Dec::ONE)?;
            let _ = card_pile::draw(state, content, 2, false)?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::DexterityPotion => {
            let t = require_player_target(state, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::DexterityPower,
                t,
                Dec::TWO,
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::DistilledChaos => {
            let _ = require_player_target(state, target, potion_id)?;
            let outcome = autoplay::from_top_of_draw_pile(state, content, 3, false, choice_queue)?;
            if let StepOutcome::NeedsChoice(req) = outcome {
                return Err(KernelError::unsupported(
                    "PotionAutoplayChoice",
                    req.source_id.into_owned(),
                ));
            }
            Ok(CardPlayOutcome::default())
        }
        PotionId::DropletOfPrecognition => {
            let _ = require_player_target(state, target, potion_id)?;
            let cands = droplet_of_precognition_candidates(state, content)?;
            match card_select::select_cards(
                state,
                &cands,
                1,
                1,
                ChoiceTiming::Action,
                potion_id.model_id_str(),
                PileType::Draw,
                false,
                choice_queue,
            )? {
                card_select::SelectResult::Selected(sel) => {
                    droplet_of_precognition_resume(state, &sel)?;
                    Ok(CardPlayOutcome::default())
                }
                card_select::SelectResult::NeedsChoice(req) => {
                    Ok(CardPlayOutcome::with_choice(req, 1))
                }
            }
        }
        PotionId::Duplicator => {
            let t = require_player_target(state, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::DuplicationPower,
                t,
                Dec::ONE,
                Some(t),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::EnergyPotion => {
            let _ = require_player_target(state, target, potion_id)?;
            gold::gain_energy(state, content, Dec::TWO)?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::EntropicBrew => {
            let _ = require_player_target(state, target, potion_id)?;
            while state.player.potion_slots.iter().any(|s| s.is_none()) {
                let Some(pot) = entropic_brew_roll_out_of_combat_potion(state) else {
                    break;
                };
                if !potion_cmd::try_to_procure(state, content, pot)? {
                    break;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        PotionId::EssenceOfDarkness => {
            let _ = require_player_target(state, target, potion_id)?;
            let cap = state.player.orb_capacity;
            for _ in 0..cap {
                orb::channel(state, content, sts2_types::OrbId::DarkOrb)?;
            }
            Ok(CardPlayOutcome::default())
        }
        PotionId::ExplosiveAmpoule => {
            if target.is_some() {
                return Err(KernelError::InvalidAction(
                    "ExplosiveAmpoule does not take a single target".to_owned(),
                ));
            }
            let targets = collect_hittable_enemies(state, content)?;
            damage::damage_targets(
                state,
                content,
                &targets,
                Dec::from_i32(10),
                ValueProp::UNPOWERED,
                Some(owner),
                None,
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::FairyInABottle => {
            let t = require_player_target(state, target, potion_id)?;
            let max_hp = state
                .creature(t)
                .ok_or_else(|| KernelError::InvalidState("missing target creature".to_owned()))?
                .max_hp;
            let pct = Dec::from_parts(30, 2)?;
            let amt = Dec::ONE.max(Dec::from_i32(max_hp).checked_mul(pct)?);
            creature::heal(state, content, t, amt)?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::FirePotion => {
            let t = require_enemy_target(state, content, target, potion_id)?;
            damage::damage_targets(
                state,
                content,
                &[t],
                Dec::from_i32(20),
                ValueProp::UNPOWERED,
                Some(owner),
                None,
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::FlexPotion => {
            let t = require_player_target(state, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::FlexPotionPower,
                t,
                Dec::from_i32(5),
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::FocusPotion => {
            let t = require_player_target(state, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::FocusPower,
                t,
                Dec::TWO,
                Some(t),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::Fortifier => {
            let t = require_player_target(state, target, potion_id)?;
            let cur_block = state
                .creature(t)
                .ok_or_else(|| KernelError::InvalidState("missing target creature".to_owned()))?
                .block;
            let gain = Dec::from_i32(cur_block).checked_mul(Dec::TWO)?;
            if gain > Dec::ZERO {
                block::gain_block(
                    state,
                    content,
                    t,
                    gain,
                    ValueProp::UNPOWERED,
                    None,
                    None,
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        PotionId::FoulPotion => {
            if target.is_some() {
                return Err(KernelError::InvalidAction(
                    "FoulPotion does not take a single target".to_owned(),
                ));
            }
            let targets: SmallVec<[CreatureHandle; 8]> = state
                .ally_handles
                .iter()
                .chain(state.enemy_handles.iter())
                .copied()
                .filter(|&h| {
                    state
                        .creature(h)
                        .is_some_and(|c| !c.is_pet() && !c.is_detached && c.is_alive())
                })
                .collect();
            damage::damage_targets(
                state,
                content,
                &targets,
                Dec::from_i32(12),
                ValueProp::UNPOWERED,
                Some(owner),
                None,
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::FruitJuice => {
            let t = require_player_target(state, target, potion_id)?;
            creature::gain_max_hp(state, content, t, Dec::from_i32(5))?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::FyshOil => {
            let t = require_player_target(state, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                t,
                Dec::ONE,
                Some(owner),
                None,
            )?;
            power::apply(
                state,
                content,
                PowerId::DexterityPower,
                t,
                Dec::ONE,
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::GamblersBrew => {
            let _ = require_player_target(state, target, potion_id)?;
            let cands = gamblers_brew_candidates(state);
            match card_select::select_cards(
                state,
                &cands,
                0,
                cands.len(),
                ChoiceTiming::Action,
                potion_id.model_id_str(),
                PileType::Hand,
                false,
                choice_queue,
            )? {
                card_select::SelectResult::Selected(sel) => {
                    gamblers_brew_resume(content, state, &sel)?;
                    Ok(CardPlayOutcome::default())
                }
                card_select::SelectResult::NeedsChoice(req) => {
                    Ok(CardPlayOutcome::with_choice(req, 1))
                }
            }
        }
        PotionId::GhostInAJar => {
            let t = require_player_target(state, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::IntangiblePower,
                t,
                Dec::ONE,
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::GigantificationPotion => {
            let t = require_player_target(state, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::GigantificationPower,
                t,
                Dec::ONE,
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::GlowwaterPotion => {
            let _ = require_player_target(state, target, potion_id)?;
            let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
            for h in hand {
                card_pile::exhaust(state, content, h, false)?;
            }
            let _ = card_pile::draw(state, content, 10, false)?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::HeartOfIron => {
            let t = require_player_target(state, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::PlatingPower,
                t,
                Dec::from_i32(7),
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::KingsCourage => {
            let _ = require_player_target(state, target, potion_id)?;
            forge::forge(state, content, Dec::from_i32(15))?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::LiquidBronze => {
            let t = require_player_target(state, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::ThornsPower,
                t,
                Dec::from_i32(3),
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::LiquidMemories => {
            let _ = require_player_target(state, target, potion_id)?;
            let cands = liquid_memories_candidates(state);
            match card_select::select_cards(
                state,
                &cands,
                1,
                1,
                ChoiceTiming::Action,
                potion_id.model_id_str(),
                PileType::Discard,
                false,
                choice_queue,
            )? {
                card_select::SelectResult::Selected(sel) => {
                    liquid_memories_resume(state, &sel)?;
                    Ok(CardPlayOutcome::default())
                }
                card_select::SelectResult::NeedsChoice(req) => {
                    Ok(CardPlayOutcome::with_choice(req, 1))
                }
            }
        }
        PotionId::LuckyTonic => {
            let t = require_player_target(state, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::BufferPower,
                t,
                Dec::ONE,
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::MazalethsGift => {
            let t = require_player_target(state, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::RitualPower,
                t,
                Dec::ONE,
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::OrobicAcid => {
            let _ = require_player_target(state, target, potion_id)?;
            let pool = card_factory::character_combat_pool(state);
            let mut generated = SmallVec::<[CardHandle; 3]>::new();
            for card_type in [CardType::Attack, CardType::Skill, CardType::Power] {
                let picked = card_factory::get_distinct_for_combat_filtered(
                    state,
                    &pool,
                    1,
                    RunRngType::CombatCardGeneration,
                    |cid| generated_card_meta(cid).card_type == card_type,
                );
                if let Some(&cid) = picked.first() {
                    let h = card_pile::create_card_in_combat(state, content, cid, 0)?;
                    set_to_free_this_turn(state, h);
                    generated.push(h);
                }
            }
            for h in generated {
                generate_existing_card_into_hand(content, state, h)?;
            }
            Ok(CardPlayOutcome::default())
        }
        PotionId::PoisonPotion => {
            let t = require_enemy_target(state, content, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::PoisonPower,
                t,
                Dec::from_i32(6),
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::PotionOfBinding => {
            if target.is_some() {
                return Err(KernelError::InvalidAction(
                    "PotionOfBinding does not take a single target".to_owned(),
                ));
            }
            let targets = collect_hittable_enemies(state, content)?;
            for &eh in &targets {
                power::apply(
                    state,
                    content,
                    PowerId::WeakPower,
                    eh,
                    Dec::ONE,
                    Some(owner),
                    None,
                )?;
            }
            for &eh in &targets {
                power::apply(
                    state,
                    content,
                    PowerId::VulnerablePower,
                    eh,
                    Dec::ONE,
                    Some(owner),
                    None,
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        PotionId::PotionOfCapacity => {
            let _ = require_player_target(state, target, potion_id)?;
            orb::add_slots(state, 2);
            Ok(CardPlayOutcome::default())
        }
        PotionId::PotionOfDoom => {
            let t = require_enemy_target(state, content, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::DoomPower,
                t,
                Dec::from_i32(33),
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::PotionShapedRock => {
            let t = require_enemy_target(state, content, target, potion_id)?;
            damage::damage_targets(
                state,
                content,
                &[t],
                Dec::from_i32(15),
                ValueProp::UNPOWERED,
                Some(owner),
                None,
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::PotOfGhouls => {
            let _ = require_player_target(state, target, potion_id)?;
            for _ in 0..2 {
                let _ = card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    CardId::Soul,
                    PileType::Hand,
                    CardPilePosition::Bottom,
                    0,
                    Some(owner),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        PotionId::PowderedDemise => {
            let t = require_enemy_target(state, content, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::DemisePower,
                t,
                Dec::from_i32(9),
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::PrecisionPotion => {
            let t = require_player_target(state, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::AimPower,
                t,
                Dec::from_i32(21),
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::RadiantTincture => {
            let t = require_player_target(state, target, potion_id)?;
            gold::gain_energy(state, content, Dec::ONE)?;
            power::apply(
                state,
                content,
                PowerId::RadiancePower,
                t,
                Dec::from_i32(3),
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::RegenPotion => {
            let t = require_player_target(state, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::RegenPower,
                t,
                Dec::from_i32(5),
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::ShacklingPotion => {
            if target.is_some() {
                return Err(KernelError::InvalidAction(
                    "ShacklingPotion does not take a single target".to_owned(),
                ));
            }
            let targets = collect_hittable_enemies(state, content)?;
            for eh in targets {
                power::apply(
                    state,
                    content,
                    PowerId::ShacklingPotionPower,
                    eh,
                    Dec::from_i32(7),
                    Some(owner),
                    None,
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        PotionId::ShipInABottle => {
            let t = require_player_target(state, target, potion_id)?;
            block::gain_block(
                state,
                content,
                t,
                Dec::from_i32(10),
                ValueProp::UNPOWERED,
                None,
                None,
            )?;
            power::apply(
                state,
                content,
                PowerId::BlockNextTurnPower,
                t,
                Dec::from_i32(10),
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::SneckoOil => {
            let _ = require_player_target(state, target, potion_id)?;
            let _ = card_pile::draw(state, content, 7, false)?;
            let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
            for h in hand {
                let Some(ci) = state.card(h) else {
                    continue;
                };
                let meta = content
                    .card_meta(ci.id)
                    .unwrap_or_else(|_| generated_card_meta(ci.id));
                let is_x = meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST);
                if !is_x && ci.canonical_base_energy_cost >= 0 {
                    let cost = state
                        .run_rngs
                        .get_mut(RunRngType::CombatEnergyCosts)
                        .next_int(4)?;
                    if let Some(c) = state.card_mut(h) {
                        c.temp_cost_override_this_turn_or_until_played =
                            sts2_kernel::OptI16::some_clamped(cost);
                    }
                    if let Some(ext) = state.card_ext_mut(h) {
                        ext.push_local_energy_modifier(LocalEnergyModifier {
                            kind: LocalEnergyModifierKind::SetThisTurnOrUntilPlayed,
                            amount: cost,
                            is_reduce_only: false,
                        });
                    }
                }
            }
            Ok(CardPlayOutcome::default())
        }
        PotionId::SoldiersStew => {
            let _ = require_player_target(state, target, potion_id)?;
            let all_cards: Vec<CardHandle> = state
                .player
                .hand
                .iter()
                .chain(state.player.draw.iter())
                .chain(state.player.discard.iter())
                .chain(state.player.exhaust.iter())
                .chain(state.player.play.iter())
                .copied()
                .collect();
            for h in all_cards {
                let Some(ci) = state.card(h) else {
                    continue;
                };
                let tags = content
                    .card_meta(ci.id)
                    .unwrap_or_else(|_| generated_card_meta(ci.id))
                    .base_tags;
                if tags.contains(CardTags::STRIKE) {
                    if let Some(c) = state.card_mut(h) {
                        c.base_replay_count += 1;
                    }
                }
            }
            Ok(CardPlayOutcome::default())
        }
        PotionId::SpeedPotion => {
            let t = require_player_target(state, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::SpeedPotionPower,
                t,
                Dec::from_i32(5),
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::StableSerum => {
            let t = require_player_target(state, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::RetainHandPower,
                t,
                Dec::TWO,
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::StarPotion => {
            let _ = require_player_target(state, target, potion_id)?;
            gold::gain_stars(state, content, Dec::from_i32(3))?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::StrengthPotion => {
            let t = require_player_target(state, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                t,
                Dec::TWO,
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::SwiftPotion => {
            let _ = require_player_target(state, target, potion_id)?;
            let _ = card_pile::draw(state, content, 3, false)?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::TouchOfInsanity => {
            let _ = require_player_target(state, target, potion_id)?;
            let cands = touch_of_insanity_candidates(state, content)?;
            match card_select::select_cards(
                state,
                &cands,
                1,
                1,
                ChoiceTiming::Action,
                potion_id.model_id_str(),
                PileType::Hand,
                false,
                choice_queue,
            )? {
                card_select::SelectResult::Selected(sel) => {
                    touch_of_insanity_resume(state, &sel)?;
                    Ok(CardPlayOutcome::default())
                }
                card_select::SelectResult::NeedsChoice(req) => {
                    Ok(CardPlayOutcome::with_choice(req, 1))
                }
            }
        }
        PotionId::VulnerablePotion => {
            let t = require_enemy_target(state, content, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::VulnerablePower,
                t,
                Dec::from_i32(3),
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::WeakPotion => {
            let t = require_enemy_target(state, content, target, potion_id)?;
            power::apply(
                state,
                content,
                PowerId::WeakPower,
                t,
                Dec::from_i32(3),
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
    }
}

pub fn potion_on_use_resume_after_select(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    potion_id: PotionId,
    _target: Option<CreatureHandle>,
    selected: &[CardHandle],
    resume_step: u8,
) -> Result<CardPlayOutcome, KernelError> {
    if resume_step != 1 {
        return Err(KernelError::InvalidState(format!(
            "unexpected potion resume step {resume_step} for {}",
            potion_id.model_id_str()
        )));
    }
    match potion_id {
        PotionId::Ashwater => {
            ashwater_resume(content, state, selected)?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::GamblersBrew => {
            gamblers_brew_resume(content, state, selected)?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::DropletOfPrecognition => {
            droplet_of_precognition_resume(state, selected)?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::LiquidMemories => {
            liquid_memories_resume(state, selected)?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::TouchOfInsanity => {
            touch_of_insanity_resume(state, selected)?;
            Ok(CardPlayOutcome::default())
        }
        PotionId::AttackPotion
        | PotionId::SkillPotion
        | PotionId::PowerPotion
        | PotionId::ColorlessPotion => {
            card_choice_potion_resume_from_pending(content, state, selected)?;
            Ok(CardPlayOutcome::default())
        }
        _ => Err(KernelError::InvalidAction(format!(
            "potion {} does not support choice resume",
            potion_id.model_id_str()
        ))),
    }
}

pub fn dispatch_hook(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    potion_idx: usize,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    let id = state
        .player
        .potion_slots
        .get(potion_idx)
        .and_then(|s| *s)
        .ok_or_else(|| KernelError::InvalidState("missing potion".to_owned()))?;
    match id {
        PotionId::FairyInABottle => match slot {
            HookSlot::ShouldDie => {
                if let HookArgs::Creature(creature) = args {
                    if *creature == state.player.creature_handle {
                        return Ok(HookResult {
                            bool_val: Some(false),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterPreventingDeath => {
                if let HookArgs::Creature(creature) = args {
                    if *creature == state.player.creature_handle {
                        let _ =
                            potion_cmd::use_automatic(state, content, potion_idx, Some(*creature))?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        _ => Ok(HookResult::default()),
    }
}
