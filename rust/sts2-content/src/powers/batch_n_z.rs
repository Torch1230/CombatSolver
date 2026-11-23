use smallvec::SmallVec;
use sts2_kernel::commands::{
    autoplay, block, card as card_cmd, card_factory, card_pile, card_select,
    creature as creature_cmd, damage, gold, melody, orb, osty, power,
};
use sts2_kernel::continuation::{ChoiceRequest, ChoiceTiming};
use sts2_kernel::hooks;
use sts2_kernel::{
    BattleState, CardHandle, CardInst, CardTempFlags, ContentDispatcher, CreatureHandle,
    DamageResult, FieldValue, HookArgs, HookResult, KernelError, PowerExtension, PowerInst,
};
use sts2_types::{
    generated_card_meta, AfflictionId, CardId, CardKeywords, CardPilePosition, CardTags, CardType,
    CombatSide, Dec, HookMask, HookSlot, MonsterId, OrbId, PileType, PowerId, PowerInstanceType,
    PowerMeta, PowerStackType, PowerStaticFlags, PowerType, RoomType, RunRngType, TargetType,
    ValueProp, CARD_POOL_COLORLESS,
};

const fn meta_with(
    power_type: PowerType,
    stack_type: PowerStackType,
    instance_type: PowerInstanceType,
    flags: PowerStaticFlags,
    hook_mask: HookMask,
) -> PowerMeta {
    PowerMeta {
        power_type,
        stack_type,
        instance_type,
        flags,
        hook_mask,
    }
}

const REMOVE_ON_DEATH: PowerStaticFlags = PowerStaticFlags::REMOVE_AFTER_OWNER_DEATH;
const TEMP_REMOVE_ON_DEATH: PowerStaticFlags =
    PowerStaticFlags::IS_TEMPORARY.union(PowerStaticFlags::REMOVE_AFTER_OWNER_DEATH);
const ALLOW_NEG_AND_REMOVE: PowerStaticFlags =
    PowerStaticFlags::ALLOW_NEGATIVE.union(PowerStaticFlags::REMOVE_AFTER_OWNER_DEATH);

static NECRO_MASTERY_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterCurrentHpChanged),
);
static NEMESIS_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Single,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterSideTurnEnd),
);
static NEUROSURGE_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterSideTurnStart),
);
static NIGHTMARE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::Instanced,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::BeforeHandDraw),
);
static NOSTALGIA_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::ModifyCardPlayResultLocation),
);
static NOXIOUS_FUMES_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterSideTurnStart),
);
static NO_BLOCK_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::ModifyBlockMultiplicative)
        .with(HookSlot::AfterSideTurnEnd),
);
static NO_DRAW_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Single,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::ShouldDraw)
        .with(HookSlot::AfterSideTurnEnd),
);
static OBLIVION_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::InstancedPerApplier,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::BeforeCardPlayed)
        .with(HookSlot::AfterCardPlayed)
        .with(HookSlot::AfterSideTurnEnd),
);
static ONE_FOR_ALL_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::ModifyDamageAdditive),
);
static ONE_TWO_PUNCH_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::ModifyCardPlayCount)
        .with(HookSlot::AfterModifyingCardPlayCount)
        .with(HookSlot::AfterSideTurnEnd),
);
static ORBIT_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::Instanced,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterEnergySpent),
);
static PAGESTORM_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterCardDrawn),
);
static PAINFUL_STABS_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    PowerStaticFlags::empty(),
    HookMask::EMPTY
        .with(HookSlot::AfterAttack)
        .with(HookSlot::ShouldCreatureBeRemovedFromCombatAfterDeath),
);
static PALE_BLUE_DOT_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterCardPlayed)
        .with(HookSlot::AfterSideTurnEnd),
);
static PANACHE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::Instanced,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterCardPlayed)
        .with(HookSlot::BeforeSideTurnEnd),
);
static PAPER_CUTS_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterDamageGiven),
);
static PARRY_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY,
);
static PERSONAL_HIVE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterDamageReceived),
);
static PHANTOM_BLADES_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::TryModifyKeywordsInCombat)
        .with(HookSlot::ModifyDamageAdditive),
);
static PIERCING_WAIL_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    TEMP_REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::BeforeApplied)
        .with(HookSlot::AfterPowerAmountChanged)
        .with(HookSlot::AfterSideTurnEnd),
);
static PILLAR_OF_CREATION_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterCardGeneratedForCombat),
);
static PLATING_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterApplied)
        .with(HookSlot::BeforeSideTurnStart)
        .with(HookSlot::BeforeSideTurnEndEarly)
        .with(HookSlot::AfterSideTurnStart),
);
static PLOW_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterDamageReceived),
);
static POISON_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterSideTurnStart),
);
static POSSESS_SPEED_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Single,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterPowerAmountChanged)
        .with(HookSlot::AfterDeath),
);
static POSSESS_STRENGTH_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Single,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterPowerAmountChanged)
        .with(HookSlot::AfterDeath),
);
static PREP_TIME_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterSideTurnStart),
);
static PYRE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::ModifyMaxEnergy),
);
static QUIETUS_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterArrowShot)
        .with(HookSlot::BeforeAttack)
        .with(HookSlot::ModifyDamageMultiplicative)
        .with(HookSlot::AfterAttack)
        .with(HookSlot::AfterSideTurnEnd),
);
static RADIANCE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterEnergyReset),
);
static RAGE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterCardPlayed)
        .with(HookSlot::AfterSideTurnEnd),
);
static RAMPART_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterSideTurnStart),
);
static RAVENOUS_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterDeath),
);
static REAPER_FORM_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterDamageGiven),
);
static REATTACH_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Single,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterDeath)
        .with(HookSlot::ShouldAllowHitting)
        .with(HookSlot::ShouldCreatureBeRemovedFromCombatAfterDeath)
        .with(HookSlot::ShouldPowerBeRemovedOnDeath)
        .with(HookSlot::ShouldOwnerDeathTriggerFatal),
);
static REBOUND_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::ModifyCardPlayResultLocation)
        .with(HookSlot::AfterModifyingCardPlayResultLocation)
        .with(HookSlot::AfterSideTurnEnd),
);
static REFLECT_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterDamageReceived)
        .with(HookSlot::AfterSideTurnStart),
);
static REGEN_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::BeforeSideTurnEndEarly),
);
static REPTILE_TRINKET_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    TEMP_REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::BeforeApplied)
        .with(HookSlot::AfterPowerAmountChanged)
        .with(HookSlot::AfterSideTurnEnd),
);
static RETAIN_HAND_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::ShouldFlush)
        .with(HookSlot::AfterSideTurnEnd),
);
static RINGING_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Single,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterApplied)
        .with(HookSlot::AfterCardEnteredCombat)
        .with(HookSlot::ShouldPlay)
        .with(HookSlot::AfterSideTurnEnd)
        .with(HookSlot::AfterRemoved),
);
static RITUAL_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterApplied)
        .with(HookSlot::AfterSideTurnEnd),
);
static ROLLING_BOULDER_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::Instanced,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterPlayerTurnStart),
);
static ROYALTIES_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY,
);
static RUPTURE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::BeforeCardPlayed)
        .with(HookSlot::AfterDamageReceived)
        .with(HookSlot::AfterCardPlayed),
);
static SANDPIT_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::Instanced,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterSideTurnStartLate)
        .with(HookSlot::AfterRemoved),
);
static SEEKING_EDGE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY,
);
static SELF_FORMING_CLAY_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterBlockCleared),
);
static SENTRY_MODE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::BeforeHandDraw),
);
static SERPENT_FORM_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::BeforeCardPlayed)
        .with(HookSlot::AfterCardPlayed),
);
static SERRATED_ARROWS_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::ModifyDamageMultiplicative),
);
static SETUP_STRIKE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    TEMP_REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::BeforeApplied)
        .with(HookSlot::AfterPowerAmountChanged)
        .with(HookSlot::AfterSideTurnEnd),
);
static SHACKLING_POTION_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    TEMP_REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::BeforeApplied)
        .with(HookSlot::AfterPowerAmountChanged)
        .with(HookSlot::AfterSideTurnEnd),
);
static SHADOWMELD_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::ModifyBlockMultiplicative)
        .with(HookSlot::AfterSideTurnEnd),
);
static SHADOW_STEP_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterSideTurnStart),
);
static SHOW_OFF_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::Instanced,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterDeath)
        .with(HookSlot::AfterSideTurnEnd),
);
static SHRIEK_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    ALLOW_NEG_AND_REMOVE,
    HookMask::EMPTY.with(HookSlot::AfterDamageReceived),
);
static SHRINK_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    ALLOW_NEG_AND_REMOVE,
    HookMask::EMPTY
        .with(HookSlot::ModifyDamageMultiplicative)
        .with(HookSlot::AfterSideTurnEnd)
        .with(HookSlot::AfterDeath),
);
static SHROUD_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterPowerAmountChanged),
);
static SIC_EM_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterDamageGiven)
        .with(HookSlot::AfterSideTurnEnd),
);
static SIGNAL_BOOST_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::ModifyCardPlayCount)
        .with(HookSlot::AfterModifyingCardPlayCount),
);
static SIMPLETON_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterAutoPostPlayPhaseEntered),
);
static SKITTISH_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterAttack)
        .with(HookSlot::AfterSideTurnEnd),
);
static SLEIGHT_OF_FLESH_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterPowerAmountChanged),
);
static SLIPPERY_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::ModifyHpLostAfterOsty)
        .with(HookSlot::AfterDamageReceived),
);
static SLOTH_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::ShouldPlay)
        .with(HookSlot::BeforeCardPlayed)
        .with(HookSlot::BeforeSideTurnStart),
);
static SLOW_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterCardPlayed)
        .with(HookSlot::ModifyDamageMultiplicative)
        .with(HookSlot::AfterSideTurnStart),
);
static SLUMBER_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterDamageReceived)
        .with(HookSlot::AfterSideTurnEnd),
);
static SMOGGY_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Single,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::BeforeCardPlayed)
        .with(HookSlot::AfterCardEnteredCombat)
        .with(HookSlot::AfterCardPlayed)
        .with(HookSlot::AfterSideTurnEnd)
        .with(HookSlot::ShouldPlay),
);
static SMOKESTACK_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterCardGeneratedForCombat),
);
static SNEAKY_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterCardPlayed),
);
static SOAR_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Single,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::ModifyDamageMultiplicative),
);
static SONG_PATH_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterCardPlayed),
);
static SOULBOUND_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterCardGeneratedForCombat),
);
static SPECTRUM_SHIFT_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::BeforeHandDraw),
);
static SPEEDSTER_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterCardDrawn),
);
static SPEED_POTION_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    TEMP_REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::BeforeApplied)
        .with(HookSlot::AfterPowerAmountChanged)
        .with(HookSlot::AfterSideTurnEnd),
);
static SPINNER_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterEnergyReset),
);
static SPIRIT_OF_ASH_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::BeforeCardPlayed),
);
static STAMPEDE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterAutoPostPlayPhaseEntered),
);
static STAR_NEXT_TURN_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterEnergyReset),
);
static STEAM_ERUPTION_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    PowerStaticFlags::empty(),
    HookMask::EMPTY
        .with(HookSlot::AfterDeath)
        .with(HookSlot::ShouldStopCombatFromEnding)
        .with(HookSlot::ShouldCreatureBeRemovedFromCombatAfterDeath),
);
static STOCK_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterDeath)
        .with(HookSlot::ShouldStopCombatFromEnding),
);
static STORM_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::BeforeCardPlayed)
        .with(HookSlot::AfterCardPlayed),
);
static STRANGLE_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::InstancedPerApplier,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::BeforeCardPlayed)
        .with(HookSlot::AfterCardPlayed)
        .with(HookSlot::AfterSideTurnEnd),
);
static STRATAGEM_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterShuffle),
);
static STRENGTH_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    ALLOW_NEG_AND_REMOVE,
    HookMask::EMPTY.with(HookSlot::ModifyDamageAdditive),
);
static STRETCH_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::BeforeCardPlayed)
        .with(HookSlot::ModifyBlockAdditive)
        .with(HookSlot::AfterCardPlayed),
);
static SUBROUTINE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::BeforeCardPlayed)
        .with(HookSlot::AfterCardPlayed),
);
static SUCK_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterAttack),
);
static SUMMON_NEXT_TURN_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterPlayerTurnStart),
);
static SURPRISE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Single,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterDeath)
        .with(HookSlot::ShouldStopCombatFromEnding),
);
static SURROUNDED_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Single,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::ModifyDamageMultiplicative)
        .with(HookSlot::BeforeCardPlayed)
        .with(HookSlot::BeforePotionUsed)
        .with(HookSlot::AfterDeath),
);
static SWIPE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Single,
    PowerInstanceType::Instanced,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::BeforeDeath),
);
static SWORD_SAGE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterPowerAmountChanged)
        .with(HookSlot::AfterCardEnteredCombat)
        .with(HookSlot::AfterCardOwnerChanged)
        .with(HookSlot::AfterRemoved),
);
static SYNCHRONIZE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    TEMP_REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::BeforeApplied)
        .with(HookSlot::AfterPowerAmountChanged)
        .with(HookSlot::AfterSideTurnEnd),
);
static TAG_TEAM_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::Instanced,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::ModifyCardPlayCount)
        .with(HookSlot::AfterModifyingCardPlayCount),
);
static TAINTED_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::ModifyDamageAdditive)
        .with(HookSlot::AfterSideTurnEnd),
);
static TANGLED_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterApplied)
        .with(HookSlot::AfterCardEnteredCombat)
        .with(HookSlot::TryModifyEnergyCostInCombat)
        .with(HookSlot::AfterSideTurnEnd)
        .with(HookSlot::AfterRemoved),
);
static TANK_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Single,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterApplied)
        .with(HookSlot::ModifyDamageMultiplicative),
);
static TEMPORARY_DEXTERITY_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    TEMP_REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::BeforeApplied)
        .with(HookSlot::AfterPowerAmountChanged)
        .with(HookSlot::AfterSideTurnEnd),
);
static TEMPORARY_STRENGTH_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    TEMP_REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::BeforeApplied)
        .with(HookSlot::AfterPowerAmountChanged)
        .with(HookSlot::AfterSideTurnEnd),
);
static TENDER_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterCardPlayed)
        .with(HookSlot::AfterSideTurnEnd),
);
static TERRITORIAL_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterSideTurnEnd),
);
static THE_BOMB_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::Instanced,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::BeforeSideTurnEnd),
);
static THE_GAMBIT_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Single,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterDamageReceived),
);
static THE_HUNT_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY,
);
static THE_SEALED_THRONE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::BeforeCardPlayed),
);
static THIEVERY_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::Instanced,
    REMOVE_ON_DEATH,
    HookMask::EMPTY,
);
static THORNS_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::BeforeDamageReceived),
);
static THUNDER_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterOrbEvoked),
);
static TOOLS_OF_THE_TRADE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::ModifyHandDraw)
        .with(HookSlot::AfterPlayerTurnStart),
);
static TORIC_TOUGHNESS_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::Instanced,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterBlockCleared),
);
static TRACKING_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::ModifyDamageMultiplicative),
);
static TRASH_TO_TREASURE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterCardGeneratedForCombat),
);
static TUNE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterSideTurnEnd),
);
static TYRANNY_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::ModifyHandDraw)
        .with(HookSlot::AfterPlayerTurnStart),
);
static UNDERWORLD_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterDamageGiven)
        .with(HookSlot::AfterSideTurnEnd),
);
static UNMOVABLE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::ModifyBlockMultiplicative),
);
static VEILPIERCER_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::TryModifyEnergyCostInCombatLate)
        .with(HookSlot::BeforeCardPlayed),
);
static VICIOUS_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterPowerAmountChanged),
);
static VIGOR_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::BeforeAttack)
        .with(HookSlot::ModifyDamageAdditive)
        .with(HookSlot::AfterAttack),
);
static VIOLENT_CHORD_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterCardPlayed),
);
static VITAL_SPARK_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::BeforeCombatStart)
        .with(HookSlot::AfterCardEnteredCombat)
        .with(HookSlot::AfterCardPlayed)
        .with(HookSlot::AfterPowerAmountChanged)
        .with(HookSlot::AfterRemoved),
);
static VOID_FORM_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterApplied)
        .with(HookSlot::BeforePowerAmountChanged)
        .with(HookSlot::TryModifyEnergyCostInCombatLate)
        .with(HookSlot::TryModifyStarCostInCombat)
        .with(HookSlot::AfterCardPlayed)
        .with(HookSlot::BeforeSideTurnStart),
);
static VULNERABLE_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::ModifyDamageMultiplicative)
        .with(HookSlot::AfterSideTurnEnd),
);
static WASTE_AWAY_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::ModifyMaxEnergy),
);
static WEAK_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::ModifyDamageMultiplicative)
        .with(HookSlot::AfterSideTurnEnd),
);
static WELL_LAID_PLANS_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Single,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::ShouldFlush),
);
static WITHERING_PRESENCE_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::Instanced,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterCardPlayed),
);
static WRAITH_FORM_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterSideTurnStart),
);
static WUPPYS_LOYALTY_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Single,
    PowerInstanceType::None,
    PowerStaticFlags::empty(),
    HookMask::EMPTY
        .with(HookSlot::ModifyPreBlockDamageTarget)
        .with(HookSlot::ShouldAllowHitting)
        .with(HookSlot::ShouldCreatureBeRemovedFromCombatAfterDeath),
);
static YAH_META: PowerMeta = meta_with(
    PowerType::Buff,
    PowerStackType::Counter,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY
        .with(HookSlot::AfterArrowDislodged)
        .with(HookSlot::BeforeSideTurnStart),
);
static YOU_ARE_MINE_META: PowerMeta = meta_with(
    PowerType::Debuff,
    PowerStackType::Single,
    PowerInstanceType::None,
    REMOVE_ON_DEATH,
    HookMask::EMPTY.with(HookSlot::AfterPlayerTurnStart),
);

pub fn power_meta(id: PowerId) -> Result<&'static PowerMeta, KernelError> {
    match id {
        PowerId::NecroMasteryPower => Ok(&NECRO_MASTERY_META),
        PowerId::NemesisPower => Ok(&NEMESIS_META),
        PowerId::NeurosurgePower => Ok(&NEUROSURGE_META),
        PowerId::NightmarePower => Ok(&NIGHTMARE_META),
        PowerId::NostalgiaPower => Ok(&NOSTALGIA_META),
        PowerId::NoxiousFumesPower => Ok(&NOXIOUS_FUMES_META),
        PowerId::NoBlockPower => Ok(&NO_BLOCK_META),
        PowerId::NoDrawPower => Ok(&NO_DRAW_META),
        PowerId::OblivionPower => Ok(&OBLIVION_META),
        PowerId::OneForAllPower => Ok(&ONE_FOR_ALL_META),
        PowerId::OneTwoPunchPower => Ok(&ONE_TWO_PUNCH_META),
        PowerId::OrbitPower => Ok(&ORBIT_META),
        PowerId::PagestormPower => Ok(&PAGESTORM_META),
        PowerId::PainfulStabsPower => Ok(&PAINFUL_STABS_META),
        PowerId::PaleBlueDotPower => Ok(&PALE_BLUE_DOT_META),
        PowerId::PanachePower => Ok(&PANACHE_META),
        PowerId::PaperCutsPower => Ok(&PAPER_CUTS_META),
        PowerId::ParryPower => Ok(&PARRY_META),
        PowerId::PersonalHivePower => Ok(&PERSONAL_HIVE_META),
        PowerId::PhantomBladesPower => Ok(&PHANTOM_BLADES_META),
        PowerId::PiercingWailPower => Ok(&PIERCING_WAIL_META),
        PowerId::PillarOfCreationPower => Ok(&PILLAR_OF_CREATION_META),
        PowerId::PlatingPower => Ok(&PLATING_META),
        PowerId::PlowPower => Ok(&PLOW_META),
        PowerId::PoisonPower => Ok(&POISON_META),
        PowerId::PossessSpeedPower => Ok(&POSSESS_SPEED_META),
        PowerId::PossessStrengthPower => Ok(&POSSESS_STRENGTH_META),
        PowerId::PrepTimePower => Ok(&PREP_TIME_META),
        PowerId::PyrePower => Ok(&PYRE_META),
        PowerId::QuietusPower => Ok(&QUIETUS_META),
        PowerId::RadiancePower => Ok(&RADIANCE_META),
        PowerId::RagePower => Ok(&RAGE_META),
        PowerId::RampartPower => Ok(&RAMPART_META),
        PowerId::RavenousPower => Ok(&RAVENOUS_META),
        PowerId::ReaperFormPower => Ok(&REAPER_FORM_META),
        PowerId::ReattachPower => Ok(&REATTACH_META),
        PowerId::ReboundPower => Ok(&REBOUND_META),
        PowerId::ReflectPower => Ok(&REFLECT_META),
        PowerId::RegenPower => Ok(&REGEN_META),
        PowerId::ReptileTrinketPower => Ok(&REPTILE_TRINKET_META),
        PowerId::RetainHandPower => Ok(&RETAIN_HAND_META),
        PowerId::RingingPower => Ok(&RINGING_META),
        PowerId::RitualPower => Ok(&RITUAL_META),
        PowerId::RollingBoulderPower => Ok(&ROLLING_BOULDER_META),
        PowerId::RoyaltiesPower => Ok(&ROYALTIES_META),
        PowerId::RupturePower => Ok(&RUPTURE_META),
        PowerId::SandpitPower => Ok(&SANDPIT_META),
        PowerId::SeekingEdgePower => Ok(&SEEKING_EDGE_META),
        PowerId::SelfFormingClayPower => Ok(&SELF_FORMING_CLAY_META),
        PowerId::SentryModePower => Ok(&SENTRY_MODE_META),
        PowerId::SerpentFormPower => Ok(&SERPENT_FORM_META),
        PowerId::SerratedArrowsPower => Ok(&SERRATED_ARROWS_META),
        PowerId::SetupStrikePower => Ok(&SETUP_STRIKE_META),
        PowerId::ShacklingPotionPower => Ok(&SHACKLING_POTION_META),
        PowerId::ShadowmeldPower => Ok(&SHADOWMELD_META),
        PowerId::ShadowStepPower => Ok(&SHADOW_STEP_META),
        PowerId::ShowOffPower => Ok(&SHOW_OFF_META),
        PowerId::ShriekPower => Ok(&SHRIEK_META),
        PowerId::ShrinkPower => Ok(&SHRINK_META),
        PowerId::ShroudPower => Ok(&SHROUD_META),
        PowerId::SicEmPower => Ok(&SIC_EM_META),
        PowerId::SignalBoostPower => Ok(&SIGNAL_BOOST_META),
        PowerId::SimpletonPower => Ok(&SIMPLETON_META),
        PowerId::SkittishPower => Ok(&SKITTISH_META),
        PowerId::SleightOfFleshPower => Ok(&SLEIGHT_OF_FLESH_META),
        PowerId::SlipperyPower => Ok(&SLIPPERY_META),
        PowerId::SlothPower => Ok(&SLOTH_META),
        PowerId::SlowPower => Ok(&SLOW_META),
        PowerId::SlumberPower => Ok(&SLUMBER_META),
        PowerId::SmoggyPower => Ok(&SMOGGY_META),
        PowerId::SmokestackPower => Ok(&SMOKESTACK_META),
        PowerId::SneakyPower => Ok(&SNEAKY_META),
        PowerId::SoarPower => Ok(&SOAR_META),
        PowerId::SongPathPower => Ok(&SONG_PATH_META),
        PowerId::SoulboundPower => Ok(&SOULBOUND_META),
        PowerId::SpectrumShiftPower => Ok(&SPECTRUM_SHIFT_META),
        PowerId::SpeedsterPower => Ok(&SPEEDSTER_META),
        PowerId::SpeedPotionPower => Ok(&SPEED_POTION_META),
        PowerId::SpinnerPower => Ok(&SPINNER_META),
        PowerId::SpiritOfAshPower => Ok(&SPIRIT_OF_ASH_META),
        PowerId::StampedePower => Ok(&STAMPEDE_META),
        PowerId::StarNextTurnPower => Ok(&STAR_NEXT_TURN_META),
        PowerId::SteamEruptionPower => Ok(&STEAM_ERUPTION_META),
        PowerId::StockPower => Ok(&STOCK_META),
        PowerId::StormPower => Ok(&STORM_META),
        PowerId::StranglePower => Ok(&STRANGLE_META),
        PowerId::StratagemPower => Ok(&STRATAGEM_META),
        PowerId::StrengthPower => Ok(&STRENGTH_META),
        PowerId::StretchPower => Ok(&STRETCH_META),
        PowerId::SubroutinePower => Ok(&SUBROUTINE_META),
        PowerId::SuckPower => Ok(&SUCK_META),
        PowerId::SummonNextTurnPower => Ok(&SUMMON_NEXT_TURN_META),
        PowerId::SurprisePower => Ok(&SURPRISE_META),
        PowerId::SurroundedPower => Ok(&SURROUNDED_META),
        PowerId::SwipePower => Ok(&SWIPE_META),
        PowerId::SwordSagePower => Ok(&SWORD_SAGE_META),
        PowerId::SynchronizePower => Ok(&SYNCHRONIZE_META),
        PowerId::TagTeamPower => Ok(&TAG_TEAM_META),
        PowerId::TaintedPower => Ok(&TAINTED_META),
        PowerId::TangledPower => Ok(&TANGLED_META),
        PowerId::TankPower => Ok(&TANK_META),
        PowerId::TemporaryDexterityPower => Ok(&TEMPORARY_DEXTERITY_META),
        PowerId::TemporaryStrengthPower => Ok(&TEMPORARY_STRENGTH_META),
        PowerId::TenderPower => Ok(&TENDER_META),
        PowerId::TerritorialPower => Ok(&TERRITORIAL_META),
        PowerId::TheBombPower => Ok(&THE_BOMB_META),
        PowerId::TheGambitPower => Ok(&THE_GAMBIT_META),
        PowerId::TheHuntPower => Ok(&THE_HUNT_META),
        PowerId::TheSealedThronePower => Ok(&THE_SEALED_THRONE_META),
        PowerId::ThieveryPower => Ok(&THIEVERY_META),
        PowerId::ThornsPower => Ok(&THORNS_META),
        PowerId::ThunderPower => Ok(&THUNDER_META),
        PowerId::ToolsOfTheTradePower => Ok(&TOOLS_OF_THE_TRADE_META),
        PowerId::ToricToughnessPower => Ok(&TORIC_TOUGHNESS_META),
        PowerId::TrackingPower => Ok(&TRACKING_META),
        PowerId::TrashToTreasurePower => Ok(&TRASH_TO_TREASURE_META),
        PowerId::TunePower => Ok(&TUNE_META),
        PowerId::TyrannyPower => Ok(&TYRANNY_META),
        PowerId::UnderworldPower => Ok(&UNDERWORLD_META),
        PowerId::UnmovablePower => Ok(&UNMOVABLE_META),
        PowerId::VeilpiercerPower => Ok(&VEILPIERCER_META),
        PowerId::ViciousPower => Ok(&VICIOUS_META),
        PowerId::VigorPower => Ok(&VIGOR_META),
        PowerId::ViolentChordPower => Ok(&VIOLENT_CHORD_META),
        PowerId::VitalSparkPower => Ok(&VITAL_SPARK_META),
        PowerId::VoidFormPower => Ok(&VOID_FORM_META),
        PowerId::VulnerablePower => Ok(&VULNERABLE_META),
        PowerId::WasteAwayPower => Ok(&WASTE_AWAY_META),
        PowerId::WeakPower => Ok(&WEAK_META),
        PowerId::WellLaidPlansPower => Ok(&WELL_LAID_PLANS_META),
        PowerId::WitheringPresencePower => Ok(&WITHERING_PRESENCE_META),
        PowerId::WraithFormPower => Ok(&WRAITH_FORM_META),
        PowerId::WuppysLoyaltyPower => Ok(&WUPPYS_LOYALTY_META),
        PowerId::YahPower => Ok(&YAH_META),
        PowerId::YouAreMinePower => Ok(&YOU_ARE_MINE_META),
        _ => Err(KernelError::Unsupported {
            kind: "Power",
            id: id.model_id_str().to_owned(),
        }),
    }
}

fn find_power_by_handle(
    state: &BattleState,
    creature: CreatureHandle,
    power_handle: u32,
) -> Option<(usize, PowerInst)> {
    state.creature(creature).and_then(|c| {
        c.powers
            .iter()
            .enumerate()
            .find(|(_, p)| p.handle == power_handle)
            .map(|(i, p)| (i, *p))
    })
}

fn ensure_power_ext_by_handle(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
) -> Option<usize> {
    let (idx, existing) = find_power_by_handle(state, creature, power_handle)?;
    if let Some(e) = existing.ext_index {
        return Some(e as usize);
    }
    let ext_idx = state.power_extensions.len();
    state.power_extensions.push(PowerExtension::default());
    if let Some(c) = state.creature_mut(creature) {
        if let Some(p) = c.powers.get_mut(idx) {
            p.ext_index = Some(ext_idx as u32);
        }
    }
    Some(ext_idx)
}

fn get_ext<'a>(state: &'a BattleState, p: &PowerInst) -> Option<&'a PowerExtension> {
    p.ext_index
        .and_then(|idx| state.power_extensions.get(idx as usize))
}

fn get_bool_field(state: &BattleState, p: &PowerInst, key: &str, default: bool) -> bool {
    match get_ext(state, p).and_then(|e| e.private_fields.get(key)) {
        Some(FieldValue::Bool(b)) => *b,
        _ => default,
    }
}

fn set_bool_field(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    key: &str,
    val: bool,
) {
    if let Some(ext_idx) = ensure_power_ext_by_handle(state, creature, power_handle) {
        state.power_extensions[ext_idx]
            .private_fields
            .insert(key.to_owned(), FieldValue::Bool(val));
    }
}

fn get_int_field(state: &BattleState, p: &PowerInst, key: &str, default: i32) -> i32 {
    match get_ext(state, p).and_then(|e| e.private_fields.get(key)) {
        Some(FieldValue::Int(v)) => *v,
        _ => default,
    }
}

fn set_int_field(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    key: &str,
    val: i32,
) {
    if let Some(ext_idx) = ensure_power_ext_by_handle(state, creature, power_handle) {
        state.power_extensions[ext_idx]
            .private_fields
            .insert(key.to_owned(), FieldValue::Int(val));
    }
}

fn get_dec_field(state: &BattleState, p: &PowerInst, key: &str, default: Dec) -> Dec {
    match get_ext(state, p).and_then(|e| e.private_fields.get(key)) {
        Some(FieldValue::Decimal(d)) => *d,
        Some(FieldValue::Int(v)) => Dec::from_i32(*v),
        _ => default,
    }
}

fn get_card_handle_field(state: &BattleState, p: &PowerInst, key: &str) -> Option<CardHandle> {
    match get_ext(state, p).and_then(|e| e.private_fields.get(key)) {
        Some(FieldValue::CardHandle(h) | FieldValue::Int(h) | FieldValue::NullableInt(Some(h)))
            if *h > 0 =>
        {
            Some(CardHandle(*h as u32))
        }
        _ => None,
    }
}

fn set_card_handle_field(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    key: &str,
    val: Option<CardHandle>,
) {
    if let Some(ext_idx) = ensure_power_ext_by_handle(state, creature, power_handle) {
        state.power_extensions[ext_idx].private_fields.insert(
            key.to_owned(),
            FieldValue::CardHandle(val.map_or(0, |h| h.0 as i32)),
        );
    }
}

fn get_creature_handle_field(
    state: &BattleState,
    p: &PowerInst,
    key: &str,
) -> Option<CreatureHandle> {
    match get_ext(state, p).and_then(|e| e.private_fields.get(key)) {
        Some(
            FieldValue::CreatureHandle(h) | FieldValue::Int(h) | FieldValue::NullableInt(Some(h)),
        ) if *h > 0 => Some(CreatureHandle(*h as u32)),
        _ => None,
    }
}

fn card_int_map_set(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    key: &str,
    card: CardHandle,
    val: i32,
) {
    if let Some(ext_idx) = ensure_power_ext_by_handle(state, creature, power_handle) {
        let ch = card.0 as i32;
        let entry = state.power_extensions[ext_idx]
            .private_fields
            .entry(key.to_owned())
            .or_insert_with(|| FieldValue::CardIntMap(Vec::new()));
        if matches!(entry, FieldValue::IntArray(v) if v.is_empty()) {
            *entry = FieldValue::CardIntMap(Vec::new());
        }
        if let FieldValue::CardIntMap(vec) = entry {
            if let Some((_, v)) = vec.iter_mut().find(|(k, _)| *k == ch) {
                *v = val;
            } else {
                vec.push((ch, val));
            }
        }
    }
}

fn card_int_map_add(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    key: &str,
    card: CardHandle,
    delta: i32,
) {
    if let Some(ext_idx) = ensure_power_ext_by_handle(state, creature, power_handle) {
        let ch = card.0 as i32;
        let entry = state.power_extensions[ext_idx]
            .private_fields
            .entry(key.to_owned())
            .or_insert_with(|| FieldValue::CardIntMap(Vec::new()));
        if matches!(entry, FieldValue::IntArray(v) if v.is_empty()) {
            *entry = FieldValue::CardIntMap(Vec::new());
        }
        if let FieldValue::CardIntMap(vec) = entry {
            if let Some((_, v)) = vec.iter_mut().find(|(k, _)| *k == ch) {
                *v += delta;
                if *v == 0 && key == "SwordSagePower._grantedReplays" {
                    vec.retain(|(k, _)| *k != ch);
                }
            } else if delta != 0 {
                vec.push((ch, delta));
            }
        }
    }
}

fn card_int_map_contains(state: &BattleState, p: &PowerInst, key: &str, card: CardHandle) -> bool {
    let ch = card.0 as i32;
    matches!(
        get_ext(state, p).and_then(|e| e.private_fields.get(key)),
        Some(FieldValue::CardIntMap(vec)) if vec.iter().any(|(k, _)| *k == ch)
    )
}

fn card_int_map_remove(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    key: &str,
    card: CardHandle,
    remove_empty_key: bool,
) -> Option<i32> {
    let (_, p) = find_power_by_handle(state, creature, power_handle)?;
    let ext_idx = p.ext_index? as usize;
    let ch = card.0 as i32;
    let mut removed = None;
    let mut is_now_empty = false;
    if let Some(FieldValue::CardIntMap(vec)) = state.power_extensions[ext_idx].private_fields.get_mut(key) {
        if let Some(pos) = vec.iter().position(|(k, _)| *k == ch) {
            removed = Some(vec.remove(pos).1);
        }
        is_now_empty = vec.is_empty();
    }
    if remove_empty_key && is_now_empty {
        state.power_extensions[ext_idx].private_fields.remove(key);
    }
    removed
}

fn creature_dec_map_add(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    key: &str,
    target: CreatureHandle,
    delta: Dec,
) -> Result<(), KernelError> {
    if let Some(ext_idx) = ensure_power_ext_by_handle(state, creature, power_handle) {
        let th = target.0 as i32;
        let entry = state.power_extensions[ext_idx]
            .private_fields
            .entry(key.to_owned())
            .or_insert_with(|| FieldValue::CreatureDecimalMap(Vec::new()));
        if matches!(entry, FieldValue::IntArray(v) if v.is_empty()) {
            *entry = FieldValue::CreatureDecimalMap(Vec::new());
        }
        if let FieldValue::CreatureDecimalMap(vec) = entry {
            if let Some((_, v)) = vec.iter_mut().find(|(k, _)| *k == th) {
                *v = v.checked_add(delta)?;
            } else {
                vec.push((th, delta));
            }
        }
    }
    Ok(())
}

fn remove_field_key(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    key: &str,
) {
    if let Some((_, p)) = find_power_by_handle(state, creature, power_handle) {
        if let Some(ext_idx) = p.ext_index {
            if let Some(ext) = state.power_extensions.get_mut(ext_idx as usize) {
                ext.private_fields.remove(key);
            }
        }
    }
}

/// Exact port of `CardModel.CreateClone` + `ClearAffliction` + `EndOfTurnCleanup` (used by `NightmarePower`).
fn clone_detached_card_for_nightmare(state: &mut BattleState, source: CardHandle) -> Result<CardHandle, KernelError> {
    let src = *state
        .card(source)
        .ok_or_else(|| KernelError::InvalidState("missing nightmare source card".to_owned()))?;
    let src_ext = state.card_ext(src.ext_index).cloned();
    let handle = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    let ext_index = if let Some(mut ext) = src_ext {
        ext.local_energy_modifiers
            .retain(|m| !m.kind.clears_at_end_of_turn());
        let idx = state.card_extensions.len() as u32;
        state.card_extensions.push(ext);
        Some(idx)
    } else {
        None
    };
    let mut temp_flags = src.temp_flags;
    temp_flags.remove(
        CardTempFlags::TEMPORARY_FREE_TURN
            | CardTempFlags::TEMPORARY_RETAIN_TURN
            | CardTempFlags::TEMPORARY_SLY_TURN
            | CardTempFlags::EXHAUST_ON_NEXT_PLAY,
    );
    state.cards.push(CardInst {
        handle,
        clone_of_handle: Some(source),
        temp_cost_override_this_turn: None,
        temp_cost_override_this_turn_or_until_played: None,
        temp_star_cost_override_this_turn: None,
        temp_flags,
        affliction: None,
        ext_index,
        pile: PileType::None,
        removed_from_state: false,
        transform_into_after_play: None,
        ..src
    });
    Ok(handle)
}

fn clone_card_for_combat(state: &mut BattleState, source: CardHandle) -> Result<CardHandle, KernelError> {
    let src = *state
        .card(source)
        .ok_or_else(|| KernelError::InvalidState("missing clone source card".to_owned()))?;
    let src_ext = state.card_ext(src.ext_index).cloned();
    let handle = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    let ext_index = src_ext.map(|ext| {
        let idx = state.card_extensions.len() as u32;
        state.card_extensions.push(ext);
        idx
    });
    state.cards.push(CardInst {
        handle,
        clone_of_handle: Some(source),
        ext_index,
        pile: PileType::None,
        removed_from_state: false,
        transform_into_after_play: None,
        ..src
    });
    Ok(handle)
}

fn all_combat_card_handles(state: &BattleState) -> SmallVec<[CardHandle; 32]> {
    state
        .player
        .hand
        .iter()
        .chain(state.player.draw.iter())
        .chain(state.player.discard.iter())
        .chain(state.player.exhaust.iter())
        .chain(state.player.play.iter())
        .copied()
        .collect()
}

fn card_type_of(state: &BattleState, card: CardHandle) -> Option<CardType> {
    state
        .card(card)
        .map(|c| generated_card_meta(c.id).card_type)
}

fn card_tags_of(state: &BattleState, card: CardHandle) -> CardTags {
    state
        .card(card)
        .map_or(CardTags::empty(), |c| generated_card_meta(c.id).base_tags)
}

fn card_target_type_of(state: &BattleState, card: CardHandle) -> TargetType {
    state
        .card(card)
        .map_or(TargetType::None, |c| generated_card_meta(c.id).target_type)
}

fn card_gains_block(state: &BattleState, card: CardHandle) -> bool {
    let Some(c) = state.card(card) else {
        return false;
    };
    generated_card_meta(c.id)
        .flags
        .contains(sts2_types::CardStaticFlags::GAINS_BLOCK)
}

fn clear_card_affliction(state: &mut BattleState, card: CardHandle, target_affliction: Option<AfflictionId>) {
    if target_affliction.is_none_or(|id| {
        state
            .card(card)
            .is_some_and(|c| c.affliction.is_some_and(|a| a.id == id))
    }) {
        card_cmd::clear_affliction(state, card);
    }
}

fn hittable_enemies(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<SmallVec<[CreatureHandle; 8]>, KernelError> {
    let enemies = state.enemy_handles.clone();
    let mut out = SmallVec::new();
    for eh in enemies {
        if state.creature(eh).is_some_and(|c| c.is_alive())
            && hooks::should_allow_hitting(state, content, eh)?
        {
            out.push(eh);
        }
    }
    Ok(out)
}

fn opponents_of(state: &BattleState, creature: CreatureHandle) -> SmallVec<[CreatureHandle; 8]> {
    let side = state
        .creature(creature)
        .map_or(CombatSide::Player, |c| c.side);
    if side == CombatSide::Player {
        state.enemy_handles.clone()
    } else {
        state.ally_handles.iter().copied().collect()
    }
}

/// Exact port of `CreatureCmd.LoseMaxHp`.
fn lose_max_hp(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    amount: Dec,
) -> Result<(), KernelError> {
    if amount < Dec::ZERO {
        return Err(KernelError::InvalidAction(
            "Maximum HP loss cannot be negative.".to_owned(),
        ));
    }
    let Some((cur_hp, cur_max)) = state
        .creature(target)
        .map(|c| (c.current_hp, c.max_hp))
    else {
        return Ok(());
    };
    let new_max_hp = Dec::from_i32(cur_max).checked_sub(amount)?;
    if new_max_hp < Dec::from_i32(cur_hp) {
        let loss = Dec::from_i32(cur_hp).checked_sub(new_max_hp)?;
        damage::damage_targets(
            state,
            content,
            &[target],
            loss,
            ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED,
            None,
            None,
            None,
        )?;
    }
    let clamped_max = new_max_hp.max(Dec::ONE);
    let _ = creature_cmd::set_max_hp(state, target, clamped_max)?;
    Ok(())
}

/// Exact port of `CreatureCmd.Stun` + `MonsterModel.SetMoveImmediate` for stunned states.
fn stun_monster(state: &mut BattleState, creature: CreatureHandle, follow_up_override: Option<&str>) {
    if let Some(c) = state.creature_mut(creature) {
        if let Some(m) = &mut c.monster {
            let follow_up = follow_up_override
                .filter(|s| !s.is_empty())
                .map(str::to_owned)
                .or_else(|| m.state_log_ids.last().cloned())
                .unwrap_or_else(|| m.current_state_id.clone());
            m.current_state_id = "STUNNED".to_owned();
            m.next_move_id = Some("STUNNED".to_owned());
            m.next_move_can_transition_away = false;
            m.transient_move_follow_up_state_id = Some(follow_up);
        }
    }
}

fn set_monster_move_immediate(
    state: &mut BattleState,
    creature: CreatureHandle,
    move_id: &str,
    can_transition_away: bool,
    transient_follow_up: Option<&str>,
) {
    if let Some(c) = state.creature_mut(creature) {
        if let Some(m) = &mut c.monster {
            m.current_state_id = move_id.to_owned();
            m.next_move_id = Some(move_id.to_owned());
            m.next_move_can_transition_away = can_transition_away;
            m.transient_move_follow_up_state_id = transient_follow_up.map(str::to_owned);
        }
    }
}

/// Exact port of `WaterfallGiant.TriggerAboutToBlowState()`.
pub fn trigger_waterfall_giant_about_to_blow(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    creature_cmd::set_max_and_current_hp(state, content, creature, Dec::from_i32(999_999_999))?;
    set_monster_move_immediate(state, creature, "ABOUT_TO_BLOW_MOVE", false, None);
    Ok(())
}

fn is_decimillipede_segment(id: Option<MonsterId>) -> bool {
    matches!(
        id,
        Some(
            MonsterId::DecimillipedeSegmentBack
                | MonsterId::DecimillipedeSegmentFront
                | MonsterId::DecimillipedeSegmentMiddle
        )
    )
}

fn are_all_other_segments_dead(state: &BattleState, owner: CreatureHandle) -> bool {
    let owner_side = state.creature(owner).map_or(CombatSide::Enemy, |c| c.side);
    state.creatures.iter().all(|c| {
        if c.handle == owner || c.side != owner_side || !state.contains_creature(c.handle) {
            return true;
        }
        if !is_decimillipede_segment(c.monster_id) {
            return true;
        }
        c.is_dead()
    })
}

pub const fn is_temporary_strength_power(id: PowerId) -> bool {
    matches!(
        id,
        PowerId::TemporaryStrengthPower
            | PowerId::CoordinatePower
            | PowerId::CrushUnderPower
            | PowerId::DarkShacklesPower
            | PowerId::DyingStarPower
            | PowerId::EnfeeblingTouchPower
            | PowerId::FeedingFrenzyPower
            | PowerId::FlexPotionPower
            | PowerId::ManglePower
            | PowerId::MonarchsGazeStrengthDownPower
            | PowerId::PiercingWailPower
            | PowerId::ReptileTrinketPower
            | PowerId::SetupStrikePower
            | PowerId::ShacklingPotionPower
    )
}

pub const fn is_temporary_power(id: PowerId) -> bool {
    is_temporary_strength_power(id)
        || matches!(
            id,
            PowerId::TemporaryDexterityPower
                | PowerId::AnticipatePower
                | PowerId::FadePower
                | PowerId::HelicalDartPower
                | PowerId::SpeedPotionPower
                | PowerId::FocusedStrikePower
                | PowerId::HotfixPower
                | PowerId::HyperbeamFocusDownPower
                | PowerId::SynchronizePower
                | PowerId::ChoBarbsPower
        )
}

fn temporary_power_spec(id: PowerId) -> Option<(PowerId, i32)> {
    match id {
        PowerId::TemporaryStrengthPower
        | PowerId::ReptileTrinketPower
        | PowerId::SetupStrikePower => Some((PowerId::StrengthPower, 1)),
        PowerId::PiercingWailPower | PowerId::ShacklingPotionPower => {
            Some((PowerId::StrengthPower, -1))
        }
        PowerId::TemporaryDexterityPower | PowerId::SpeedPotionPower => {
            Some((PowerId::DexterityPower, 1))
        }
        PowerId::SynchronizePower => Some((PowerId::FocusPower, 1)),
        _ => None,
    }
}

fn temporary_power_after_turn_end(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    p: PowerInst,
    participants: &[CreatureHandle],
) -> Result<(), KernelError> {
    if !participants.contains(&creature) {
        return Ok(());
    }
    let Some((target_power, sign)) = temporary_power_spec(p.id) else {
        return Ok(());
    };
    let _ = power::remove_by_handle(state, content, creature, p.handle)?;
    let _ = power::apply(
        state,
        content,
        target_power,
        creature,
        Dec::from_i32(-sign * p.amount),
        Some(creature),
        None,
    )?;
    Ok(())
}

/// Lifecycle helper for `Power.BeforeApplied`.
pub fn on_before_applied(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    power_id: PowerId,
    target: CreatureHandle,
    amount: Dec,
    applier: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    if let Some((stat_power, sign)) = temporary_power_spec(power_id) {
        let signed_amount = amount.checked_mul(Dec::from_i32(sign))?;
        let _ = power::apply(
            state,
            content,
            stat_power,
            target,
            signed_amount,
            applier,
            card_source,
        )?;
    }
    Ok(())
}

/// Lifecycle helper for `Power.AfterApplied`.
pub fn on_after_applied(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
) -> Result<(), KernelError> {
    let Some((power_idx, p)) = find_power_by_handle(state, creature, power_handle) else {
        return Ok(());
    };
    match p.id {
        PowerId::PlatingPower => {
            if state.creature(creature).is_some_and(|c| c.side == CombatSide::Enemy) {
                let player_count = 1;
                let mult = (player_count - 1) * 2 + 1;
                if let Some(c) = state.creature_mut(creature) {
                    if let Some(pi) = c.powers.get_mut(power_idx) {
                        pi.amount *= mult;
                    }
                }
            }
        }
        PowerId::RingingPower => {
            for card in all_combat_card_handles(state) {
                if state.card(card).is_some_and(|c| c.affliction.is_none()) {
                    let _ = card_cmd::afflict(state, card, AfflictionId::Ringing, 1);
                }
            }
        }
        PowerId::RitualPower => {
            let skip = state.creature(creature).is_some_and(|c| c.side == CombatSide::Enemy);
            set_bool_field(
                state,
                creature,
                p.handle,
                "RitualPower._skipFirstEnemyTurnEnd",
                skip,
            );
        }
        PowerId::TangledPower => {
            for card in all_combat_card_handles(state) {
                if card_type_of(state, card) == Some(CardType::Attack)
                    && state.card(card).is_some_and(|c| c.affliction.is_none())
                {
                    let _ = card_cmd::afflict(state, card, AfflictionId::Entangled, 1);
                }
            }
        }
        PowerId::TankPower => {
            let allies = state.ally_handles.clone();
            for ally in allies {
                if ally != creature
                    && state
                        .creature(ally)
                        .is_some_and(|c| c.is_alive() && c.is_player)
                {
                    let _ = power::apply(
                        state,
                        content,
                        PowerId::GuardedPower,
                        ally,
                        Dec::from_i32(p.amount),
                        Some(creature),
                        None,
                    )?;
                }
            }
        }
        PowerId::VoidFormPower => {
            set_int_field(
                state,
                creature,
                p.handle,
                "VoidFormPower._cardsPlayedThisTurn",
                999_999_999,
            );
        }
        _ => {}
    }
    Ok(())
}

/// Lifecycle helper for `Power.AfterRemoved`.
pub fn on_after_removed(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    p: &PowerInst,
) -> Result<(), KernelError> {
    match p.id {
        PowerId::RingingPower => {
            for card in all_combat_card_handles(state) {
                clear_card_affliction(state, card, Some(AfflictionId::Ringing));
            }
        }
        PowerId::SandpitPower => {
            let old_dead = state.creature(creature).is_none_or(|c| c.is_dead());
            if !old_dead {
                if let Some(target) =
                    get_creature_handle_field(state, p, "SandpitPower.<Target>k__BackingField")
                {
                    if state.creature(target).is_some_and(|c| c.is_alive()) {
                        creature_cmd::kill(state, content, target, None)?;
                    }
                }
            }
        }
        PowerId::SwordSagePower => {
            if let Some(FieldValue::CardIntMap(vec)) =
                get_ext(state, p).and_then(|e| e.private_fields.get("SwordSagePower._grantedReplays"))
            {
                let entries: Vec<(i32, i32)> = vec.clone();
                for (ch, granted) in entries {
                    if let Some(c) = state.card_mut(CardHandle(ch as u32)) {
                        c.base_replay_count -= granted;
                    }
                }
            }
            if let Some(ext_idx) = p.ext_index {
                if let Some(ext) = state.power_extensions.get_mut(ext_idx as usize) {
                    ext.private_fields.remove("SwordSagePower._grantedReplays");
                }
            }
        }
        PowerId::TangledPower => {
            for card in all_combat_card_handles(state) {
                clear_card_affliction(state, card, Some(AfflictionId::Entangled));
            }
        }
        PowerId::VitalSparkPower => {
            for card in all_combat_card_handles(state) {
                clear_card_affliction(state, card, Some(AfflictionId::Tainted));
            }
        }
        _ => {}
    }
    Ok(())
}

/// Lifecycle helper for `CardPileCmd.EnterCombatInternal` rollback (`AfterCardEntryAborted`).
pub fn on_after_card_entry_aborted(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    card: CardHandle,
) {
    let Some((_, p)) = find_power_by_handle(state, creature, power_handle) else {
        return;
    };
    match p.id {
        PowerId::RingingPower => {
            clear_card_affliction(state, card, Some(AfflictionId::Ringing));
        }
        PowerId::TangledPower => {
            clear_card_affliction(state, card, Some(AfflictionId::Entangled));
        }
        PowerId::SwordSagePower => {
            if let Some(granted) = card_int_map_remove(
                state,
                creature,
                power_handle,
                "SwordSagePower._grantedReplays",
                card,
                false,
            ) {
                if let Some(c) = state.card_mut(card) {
                    c.base_replay_count -= granted;
                }
            }
        }
        _ => {}
    }
}

/// W0-B helper for `ReflectPower.AfterDamageReceived` (awaits `blocked_damage` on `HookArgs::DamageResult`).
pub fn apply_reflect_after_damage_received(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    target: CreatureHandle,
    blocked_damage: i32,
    props: ValueProp,
    dealer: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    if target == creature && blocked_damage > 0 && props.is_powered_attack() {
        if let Some(d) = dealer {
            damage::damage_targets(
                state,
                content,
                &[d],
                Dec::from_i32(blocked_damage),
                ValueProp::UNPOWERED,
                Some(creature),
                None,
                None,
            )?;
        }
    }
    Ok(())
}

/// W0-B helper for `ReaperFormPower.AfterDamageGiven` (awaits `blocked_damage` / `TotalDamage` on `HookArgs::DamageResult`).
pub fn apply_reaper_form_after_damage_given(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_amount: i32,
    dealer: Option<CreatureHandle>,
    total_damage: i32,
    props: ValueProp,
    target: CreatureHandle,
) -> Result<(), KernelError> {
    let Some(d) = dealer else {
        return Ok(());
    };
    let is_owner_or_pet = d == creature
        || state
            .creature(d)
            .is_some_and(|c| c.pet_owner_player_index.is_some() && creature == state.player.creature_handle);
    if is_owner_or_pet && props.is_powered_attack() && total_damage > 0 {
        let doom = Dec::from_i32(total_damage).checked_mul(Dec::from_i32(power_amount))?;
        let _ = power::apply(state, content, PowerId::DoomPower, target, doom, Some(creature), None)?;
    }
    Ok(())
}

/// W0-B helper for `UnderworldPower.AfterDamageGiven` (awaits `blocked_damage` / `TotalDamage` on `HookArgs::DamageResult`).
pub fn apply_underworld_after_damage_given(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_amount: i32,
    dealer: Option<CreatureHandle>,
    total_damage: i32,
    props: ValueProp,
    target: CreatureHandle,
) -> Result<(), KernelError> {
    let Some(d) = dealer else {
        return Ok(());
    };
    let owner_side = state
        .creature(creature)
        .map_or(CombatSide::Player, |c| c.side);
    let dealer_matches = state.creature(d).is_some_and(|dc| {
        dc.side == owner_side
            && d != creature
            && !(dc.pet_owner_player_index.is_some() && creature == state.player.creature_handle)
    });
    if dealer_matches && props.is_powered_attack() && total_damage > 0 {
        let doom = Dec::from_i32(total_damage).checked_mul(Dec::from_i32(power_amount))?;
        let _ = power::apply(state, content, PowerId::DoomPower, target, doom, Some(creature), None)?;
    }
    Ok(())
}

pub fn apply_painful_stabs_after_attack(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_amount: i32,
    attacker: CreatureHandle,
    target_side: CombatSide,
    damage_props: ValueProp,
    results: &[DamageResult],
) -> Result<(), KernelError> {
    let owner_side = state
        .creature(creature)
        .map_or(CombatSide::Enemy, |c| c.side);
    if attacker != creature || target_side == owner_side || !damage_props.is_powered_attack() {
        return Ok(());
    }
    let mut player_hit_counts: SmallVec<[(CreatureHandle, i32); 4]> = SmallVec::new();
    for r in results {
        let is_valid_player_target = state
            .creature(r.receiver)
            .is_some_and(|c| c.is_player);
        if is_valid_player_target && r.unblocked_damage > 0 {
            if let Some((_, count)) = player_hit_counts.iter_mut().find(|(h, _)| *h == r.receiver) {
                *count += 1;
            } else {
                player_hit_counts.push((r.receiver, 1));
            }
        }
    }
    for (_, group_count) in player_hit_counts {
        let wound_count = power_amount * group_count;
        for _ in 0..wound_count {
            let _ = card_pile::add_generated_card_to_combat(
                state,
                content,
                CardId::Wound,
                PileType::Discard,
                CardPilePosition::Bottom,
                0,
                None,
            )?;
        }
    }
    Ok(())
}

pub fn apply_skittish_after_attack(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    power_amount: i32,
    damage_props: ValueProp,
    has_card_source: bool,
    results: &[DamageResult],
) -> Result<(), KernelError> {
    let Some((_, p)) = find_power_by_handle(state, creature, power_handle) else {
        return Ok(());
    };
    if get_bool_field(state, &p, "SkittishPower._hasGainedBlockThisTurn", false)
        || !damage_props.contains(ValueProp::MOVE)
        || !has_card_source
    {
        return Ok(());
    }
    let Some(first) = results.iter().find(|r| r.receiver == creature) else {
        return Ok(());
    };
    if first.unblocked_damage == 0 {
        return Ok(());
    }
    set_bool_field(
        state,
        creature,
        power_handle,
        "SkittishPower._hasGainedBlockThisTurn",
        true,
    );
    let _ = block::gain_block(
        state,
        content,
        creature,
        Dec::from_i32(power_amount),
        ValueProp::UNPOWERED,
        None,
        None,
    )?;
    Ok(())
}

pub fn apply_suck_after_attack(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_amount: i32,
    attacker: CreatureHandle,
    target_side: CombatSide,
    damage_props: ValueProp,
    results: &[DamageResult],
) -> Result<(), KernelError> {
    let owner_side = state
        .creature(creature)
        .map_or(CombatSide::Enemy, |c| c.side);
    if attacker != creature || target_side == owner_side || !damage_props.is_powered_attack() {
        return Ok(());
    }
    let mut group_seen: SmallVec<[(CreatureHandle, CreatureHandle); 8]> = SmallVec::new();
    let mut orig_seen: SmallVec<[CreatureHandle; 8]> = SmallVec::new();
    let mut prev_orig: Option<CreatureHandle> = None;
    let mut group_has_unblocked = false;
    let mut hit_groups = 0i32;

    for r in results {
        let pair = (r.original_target, r.receiver);
        let starts_new_group = !group_seen.is_empty()
            && (group_seen.contains(&pair)
                || (orig_seen.contains(&r.original_target)
                    && prev_orig != Some(r.original_target)));
        if starts_new_group {
            if group_has_unblocked {
                hit_groups += 1;
            }
            group_seen.clear();
            orig_seen.clear();
            group_has_unblocked = false;
        }
        group_seen.push(pair);
        if !orig_seen.contains(&r.original_target) {
            orig_seen.push(r.original_target);
        }
        prev_orig = Some(r.original_target);
        if r.unblocked_damage > 0 {
            group_has_unblocked = true;
        }
    }
    if !group_seen.is_empty() && group_has_unblocked {
        hit_groups += 1;
    }

    if hit_groups > 0 {
        let _ = power::apply(
            state,
            content,
            PowerId::StrengthPower,
            creature,
            Dec::from_i32(power_amount * hit_groups),
            Some(creature),
            None,
        )?;
    }
    Ok(())
}

/// W0-B helper for `NoDrawPower.ShouldDraw` (awaits `from_hand_draw: bool`).
pub fn no_draw_should_draw(state: &BattleState, creature: CreatureHandle, from_hand_draw: bool) -> bool {
    from_hand_draw || creature != state.player.creature_handle
}

/// W0-B helper for `SpeedsterPower.AfterCardDrawn` (awaits `from_hand_draw: bool`).
pub fn apply_speedster_after_card_drawn(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_amount: i32,
    from_hand_draw: bool,
) -> Result<(), KernelError> {
    let owner_side = state
        .creature(creature)
        .map_or(CombatSide::Player, |c| c.side);
    if !from_hand_draw
        && creature == state.player.creature_handle
        && state.current_side == owner_side
    {
        let targets = hittable_enemies(state, content)?;
        if !targets.is_empty() {
            damage::damage_targets(
                state,
                content,
                &targets,
                Dec::from_i32(power_amount),
                ValueProp::UNPOWERED,
                Some(creature),
                None,
                None,
            )?;
        }
    }
    Ok(())
}

/// W0-B helper for `PillarOfCreationPower.AfterCardGeneratedForCombat` (awaits `creator`).
pub fn apply_pillar_of_creation_after_generated(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_amount: i32,
    creator: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    if creator == Some(creature) {
        let _ = block::gain_block(
            state,
            content,
            creature,
            Dec::from_i32(power_amount),
            ValueProp::UNPOWERED,
            None,
            None,
        )?;
    }
    Ok(())
}

/// W0-B helper for `SmokestackPower.AfterCardGeneratedForCombat` (awaits `creator`).
pub fn apply_smokestack_after_generated(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_amount: i32,
    card: CardHandle,
    creator: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    if card_type_of(state, card) == Some(CardType::Status) && creator == Some(creature) {
        let targets = hittable_enemies(state, content)?;
        if !targets.is_empty() {
            damage::damage_targets(
                state,
                content,
                &targets,
                Dec::from_i32(power_amount),
                ValueProp::UNPOWERED,
                Some(creature),
                None,
                None,
            )?;
        }
    }
    Ok(())
}

/// W0-B helper for `TrashToTreasurePower.AfterCardGeneratedForCombat` (awaits `creator`).
pub fn apply_trash_to_treasure_after_generated(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_amount: i32,
    card: CardHandle,
    creator: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    if card_type_of(state, card) != Some(CardType::Status) || creator != Some(creature) {
        return Ok(());
    }
    const VALID_ORBS: [OrbId; 5] = [
        OrbId::LightningOrb,
        OrbId::FrostOrb,
        OrbId::DarkOrb,
        OrbId::PlasmaOrb,
        OrbId::GlassOrb,
    ];
    for _ in 0..power_amount {
        if let Some(&orb_id) = state
            .run_rngs
            .get_mut(RunRngType::CombatOrbs)
            .next_item(&VALID_ORBS)
        {
            orb::channel(state, content, orb_id)?;
        }
    }
    Ok(())
}

pub fn apply_soulbound_after_generated(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    card: CardHandle,
    creator: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    let Some((_, p)) = find_power_by_handle(state, creature, power_handle) else {
        return Ok(());
    };
    let is_soul = state.card(card).is_some_and(|c| c.id == CardId::Soul);
    let is_adding = get_bool_field(state, &p, "SoulboundPower._isAddingSoul", false);
    if creator.is_none() || creator != p.applier_handle || !is_soul || is_adding {
        return Ok(());
    }
    set_bool_field(state, creature, power_handle, "SoulboundPower._isAddingSoul", true);
    let mut res = Ok(());
    for _ in 0..p.amount {
        if let Err(err) = card_pile::add_generated_card_to_combat(
            state,
            content,
            CardId::Soul,
            PileType::Draw,
            CardPilePosition::Random,
            0,
            Some(creature),
        ) {
            res = Err(err);
            break;
        }
    }
    remove_field_key(state, creature, power_handle, "SoulboundPower._isAddingSoul");
    res
}

/// W0-B helper for `ThunderPower.AfterOrbEvoked` (awaits `targets` on `HookArgs::OrbEvoked`).
pub fn apply_thunder_after_orb_evoked(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_amount: i32,
    orb_id: OrbId,
    targets: &[CreatureHandle],
) -> Result<(), KernelError> {
    if creature != state.player.creature_handle || orb_id != OrbId::LightningOrb {
        return Ok(());
    }
    let living: SmallVec<[CreatureHandle; 8]> = targets
        .iter()
        .copied()
        .filter(|&t| state.creature(t).is_some_and(|c| c.is_alive()))
        .collect();
    if !living.is_empty() {
        damage::damage_targets(
            state,
            content,
            &living,
            Dec::from_i32(power_amount),
            ValueProp::UNPOWERED,
            Some(creature),
            None,
            None,
        )?;
    }
    Ok(())
}

/// W0-B helper for `QuietusPower.AfterArrowShot` (awaits `arrow_idx` on `HookArgs`).
pub fn apply_quietus_after_arrow_shot(
    state: &mut BattleState,
    creature: CreatureHandle,
    arrow_idx: Option<usize>,
    has_source: bool,
) {
    if creature == state.player.creature_handle && has_source {
        if let Some(idx) = arrow_idx {
            if let Some(slot) = state.player.arrows.get_mut(idx) {
                slot.destroy_after_impaled = true;
            }
        }
    }
}

/// W0-B helper for `SurroundedPower.BeforePotionUsed` (awaits `HookSlot::BeforePotionUsed` dispatch).
pub fn apply_surrounded_before_potion_used(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    target: Option<CreatureHandle>,
) {
    if creature == state.player.creature_handle {
        if let Some(t) = target {
            surrounded_face(state, creature, power_handle, t);
        }
    }
}

fn surrounded_face(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    target: CreatureHandle,
) {
    let has_left = state
        .creature(target)
        .is_some_and(|c| c.powers.iter().any(|p| p.id == PowerId::BackAttackLeftPower));
    let has_right = state
        .creature(target)
        .is_some_and(|c| c.powers.iter().any(|p| p.id == PowerId::BackAttackRightPower));
    if has_left {
        set_int_field(
            state,
            creature,
            power_handle,
            "SurroundedPower.<Facing>k__BackingField",
            0,
        );
    } else if has_right {
        set_int_field(
            state,
            creature,
            power_handle,
            "SurroundedPower.<Facing>k__BackingField",
            1,
        );
    }
}

/// W0-B helper for `ReattachPower.ShouldPowerBeRemovedOnDeath` (awaits per-`power_handle` query).
pub fn reattach_should_power_be_removed_on_death(
    reattach_handle: u32,
    queried_power_handle: u32,
) -> bool {
    queried_power_handle != reattach_handle
}

/// W0-B helper for `ReattachPower.ShouldOwnerDeathTriggerFatal`.
pub fn reattach_should_owner_death_trigger_fatal(
    state: &BattleState,
    creature: CreatureHandle,
) -> bool {
    are_all_other_segments_dead(state, creature)
}

/// Monster move helper for `DecimillipedeSegment.Reattach` (`ReattachPower.DoReattach`).
pub fn reattach_do_reattach(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
) -> Result<(), KernelError> {
    if are_all_other_segments_dead(state, creature) {
        return Ok(());
    }
    let Some((_, p)) = find_power_by_handle(state, creature, power_handle) else {
        return Ok(());
    };
    set_bool_field(state, creature, power_handle, "ReattachPower._isReviving", false);
    creature_cmd::heal(state, content, creature, Dec::from_i32(p.amount))
}

/// Out-of-combat reward helper for `RoyaltiesPower.ModifyRewards`.
pub fn royalties_reward_gold(
    state: &BattleState,
    creature: CreatureHandle,
    power_amount: i32,
    room_type: RoomType,
) -> i32 {
    if creature == state.player.creature_handle && room_type.is_combat_room() {
        power_amount
    } else {
        0
    }
}

/// Direct helper for `NightmarePower.SetSelectedCard`.
pub fn nightmare_set_selected_card(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    selected_card: CardHandle,
) -> Result<CardHandle, KernelError> {
    let clone = clone_detached_card_for_nightmare(state, selected_card)?;
    set_card_handle_field(
        state,
        creature,
        power_handle,
        "NightmarePower._selectedCard",
        Some(clone),
    );
    Ok(clone)
}

/// Direct helper for `ThieveryPower.Steal`.
pub fn thievery_steal(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
) -> Result<i32, KernelError> {
    let Some((_, p)) = find_power_by_handle(state, creature, power_handle) else {
        return Ok(0);
    };
    let target = get_creature_handle_field(state, &p, "ThieveryPower.<Target>k__BackingField");
    if target != Some(state.player.creature_handle)
        || state.creature(state.player.creature_handle).is_none_or(|c| c.is_dead())
        || state.player.gold <= 0
    {
        return Ok(0);
    }
    let stolen = p.amount.min(state.player.gold);
    gold::lose_gold(state, Dec::from_i32(stolen))?;
    let prev = get_int_field(state, &p, "ThieveryPower._goldStolen", 0);
    set_int_field(
        state,
        creature,
        power_handle,
        "ThieveryPower._goldStolen",
        prev + stolen,
    );
    Ok(stolen)
}

/// Interactive choice helper for `StratagemPower.AfterShuffle` (also awaits hook `NeedsChoice` in W0-B).
pub fn resolve_stratagem_after_shuffle(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
    power_amount: i32,
    choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
) -> Result<Option<ChoiceRequest>, KernelError> {
    if creature != state.player.creature_handle {
        return Ok(None);
    }
    let candidates: SmallVec<[CardHandle; 32]> = state.player.draw.iter().copied().collect();
    let count = power_amount.max(0) as usize;
    match card_select::select_cards(
        state,
        &candidates,
        count,
        count,
        ChoiceTiming::Action,
        "POWER.STRATAGEM_POWER",
        PileType::Draw,
        false,
        choice_queue,
    )? {
        card_select::SelectResult::NeedsChoice(req) => Ok(Some(req)),
        card_select::SelectResult::Selected(selected) => {
            for card in selected {
                card_pile::add_sync(state, card, PileType::Hand, CardPilePosition::Bottom);
            }
            let _ = content;
            Ok(None)
        }
    }
}

/// Interactive choice helper for `ToolsOfTheTradePower.AfterPlayerTurnStart`.
pub fn resolve_tools_of_the_trade_after_turn_start(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
    power_amount: i32,
    choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
) -> Result<Option<ChoiceRequest>, KernelError> {
    if creature != state.player.creature_handle {
        return Ok(None);
    }
    let count = power_amount.max(0) as usize;
    match card_select::from_hand_for_discard(
        state,
        count,
        "POWER.TOOLS_OF_THE_TRADE_POWER",
        choice_queue,
    )? {
        card_select::SelectResult::NeedsChoice(req) => Ok(Some(req)),
        card_select::SelectResult::Selected(selected) => {
            card_cmd::discard_batch(state, content, &selected)?;
            Ok(None)
        }
    }
}

/// Interactive choice helper for `TyrannyPower.AfterPlayerTurnStart`.
pub fn resolve_tyranny_after_turn_start(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
    power_amount: i32,
    choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
) -> Result<Option<ChoiceRequest>, KernelError> {
    if creature != state.player.creature_handle {
        return Ok(None);
    }
    let candidates: SmallVec<[CardHandle; 12]> = state.player.hand.iter().copied().collect();
    let count = power_amount.max(0) as usize;
    match card_select::select_cards(
        state,
        &candidates,
        count,
        count,
        ChoiceTiming::Action,
        "POWER.TYRANNY_POWER",
        PileType::Hand,
        false,
        choice_queue,
    )? {
        card_select::SelectResult::NeedsChoice(req) => Ok(Some(req)),
        card_select::SelectResult::Selected(selected) => {
            for card in selected {
                card_pile::exhaust(state, content, card, false)?;
            }
            Ok(None)
        }
    }
}

fn resolved_energy_cost_for_comparison(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
) -> Result<i32, KernelError> {
    let Some(c) = state.card(card) else {
        return Ok(0);
    };
    if c.temp_flags.contains(CardTempFlags::COSTS_X_ENERGY)
        || generated_card_meta(c.id)
            .flags
            .contains(sts2_types::CardStaticFlags::IS_X_ENERGY_COST)
    {
        return Ok(c.captured_energy_x_value);
    }
    if c.canonical_base_energy_cost < 0 {
        return Ok(0);
    }
    Ok(sts2_kernel::ordering::resolve_energy_cost(state, content, card)?.max(0))
}

fn power_of_two_dec(exp: i32) -> Result<Dec, KernelError> {
    if exp <= 0 {
        return Ok(Dec::ONE);
    }
    let mut acc = Dec::ONE;
    for _ in 0..exp {
        acc = acc.checked_mul(Dec::TWO)?;
    }
    Ok(acc)
}

pub fn dispatch_detached_hook(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power: PowerInst,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    let _ = power_meta(power.id)?;
    match slot {
        HookSlot::BeforeApplied => {
            if let HookArgs::PowerChanged {
                power_id,
                owner,
                amount,
                applier,
                card_source,
                ..
            } = args
            {
                on_before_applied(
                    content,
                    state,
                    *power_id,
                    *owner,
                    *amount,
                    *applier,
                    *card_source,
                )?;
            }
            Ok(HookResult::default())
        }
        HookSlot::AfterRemoved => {
            on_after_removed(content, state, creature, &power)?;
            Ok(HookResult::default())
        }
        _ => Ok(HookResult::default()),
    }
}

pub fn resume_hook_choice(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    slot: HookSlot,
    selected: &[CardHandle],
) -> Result<HookResult, KernelError> {
    let id = state
        .creature(creature)
        .and_then(|c| c.powers.get(power_idx))
        .map(|p| p.id)
        .ok_or_else(|| KernelError::InvalidState("missing power".to_owned()))?;
    match (id, slot) {
        (PowerId::StratagemPower, HookSlot::AfterShuffle) => {
            for &card in selected {
                card_pile::add_sync(state, card, PileType::Hand, CardPilePosition::Bottom);
            }
            Ok(HookResult::default())
        }
        (PowerId::ToolsOfTheTradePower, HookSlot::AfterPlayerTurnStart) => {
            card_cmd::discard_batch(state, content, selected)?;
            Ok(HookResult::default())
        }
        (PowerId::TyrannyPower, HookSlot::AfterPlayerTurnStart) => {
            for &card in selected {
                card_pile::exhaust(state, content, card, false)?;
            }
            Ok(HookResult::default())
        }
        _ => Err(KernelError::Unsupported {
            kind: "PowerHookChoiceResume",
            id: format!("{}:{slot:?}", id.model_id_str()),
        }),
    }
}

pub fn dispatch_hook(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    let p = state
        .creature(creature)
        .and_then(|c| c.powers.get(power_idx))
        .copied()
        .ok_or_else(|| KernelError::InvalidState("missing power".to_owned()))?;

    match p.id {
        PowerId::NecroMasteryPower => {
            if slot == HookSlot::AfterCurrentHpChanged {
                if let HookArgs::ModifyHpLost { target, amount, .. } = args {
                    if *amount < Dec::ZERO
                        && state.creature(*target).is_some_and(|c| {
                            c.monster_id == Some(MonsterId::Osty)
                                && c.pet_owner_player_index.is_some()
                                && creature == state.player.creature_handle
                        })
                    {
                        let targets = hittable_enemies(state, content)?;
                        let dmg = amount.checked_neg()?.checked_mul(Dec::from_i32(p.amount))?;
                        damage::damage_targets(
                            state,
                            content,
                            &targets,
                            dmg,
                            ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED,
                            Some(creature),
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::NemesisPower => {
            if slot == HookSlot::AfterSideTurnEnd {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let next = !get_bool_field(
                            state,
                            &p,
                            "NemesisPower._shouldApplyIntangible",
                            false,
                        );
                        set_bool_field(
                            state,
                            creature,
                            p.handle,
                            "NemesisPower._shouldApplyIntangible",
                            next,
                        );
                        if next {
                            let _ = power::apply(
                                state,
                                content,
                                PowerId::IntangiblePower,
                                creature,
                                Dec::ONE,
                                Some(creature),
                                None,
                            )?;
                        } else {
                            let _ = power::remove_by_id(
                                state,
                                content,
                                creature,
                                PowerId::IntangiblePower,
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::NeurosurgePower => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::apply(
                            state,
                            content,
                            PowerId::DoomPower,
                            creature,
                            Dec::from_i32(p.amount),
                            Some(creature),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::NightmarePower => {
            if slot == HookSlot::BeforeHandDraw && creature == state.player.creature_handle {
                let selected = get_card_handle_field(state, &p, "NightmarePower._selectedCard")
                    .ok_or_else(|| {
                        KernelError::InvalidState("NightmarePower has no selected card.".to_owned())
                    })?;
                for _ in 0..p.amount {
                    let copy = clone_card_for_combat(state, selected)?;
                    card_pile::enter_combat(
                        state,
                        content,
                        copy,
                        PileType::Hand,
                        CardPilePosition::Bottom,
                        true,
                    )?;
                    hooks::after_card_generated(state, content, copy, Some(creature))?;
                    hooks::after_card_generated_for_combat(state, content, copy, Some(creature))?;
                }
                let _ = power::remove_by_handle(state, content, creature, p.handle)?;
            }
            Ok(HookResult::default())
        }
        PowerId::NostalgiaPower => {
            if slot == HookSlot::ModifyCardPlayResultLocation {
                if let HookArgs::CardPlayResultLocation {
                    card,
                    pile,
                    ..
                } = args
                {
                    if creature == state.player.creature_handle
                        && matches!(
                            card_type_of(state, *card),
                            Some(CardType::Attack | CardType::Skill)
                        )
                        && *pile == PileType::Discard
                        && state.player.counters.attack_or_skill_card_plays_started_this_turn
                            < p.amount
                    {
                        return Ok(HookResult {
                            location_val: Some((PileType::Draw, CardPilePosition::Top)),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::NoxiousFumesPower => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let enemies = hittable_enemies(state, content)?;
                        for enemy in enemies {
                            let _ = power::apply(
                                state,
                                content,
                                PowerId::PoisonPower,
                                enemy,
                                Dec::from_i32(p.amount),
                                Some(creature),
                                None,
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::NoBlockPower => match slot {
            HookSlot::ModifyBlockMultiplicative => {
                if let HookArgs::ModifyBlock {
                    target,
                    props,
                    card_source,
                    ..
                } = args
                {
                    if *target == creature
                        && !props.contains(ValueProp::UNPOWERED)
                        && card_source.is_some()
                    {
                        return Ok(HookResult {
                            dec_val: Some(Dec::ZERO),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { side, .. } = args {
                    if *side == CombatSide::Enemy {
                        let _ = power::modify_amount_by_handle(
                            state,
                            content,
                            creature,
                            p.handle,
                            Dec::from_i32(-1),
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::NoDrawPower => match slot {
            HookSlot::ShouldDraw => {
                if let HookArgs::ShouldDraw { from_hand_draw } = args {
                    if creature == state.player.creature_handle {
                        return Ok(HookResult {
                            bool_val: Some(no_draw_should_draw(state, creature, *from_hand_draw)),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::OblivionPower => match slot {
            HookSlot::BeforeCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    if p.applier_handle == Some(state.player.creature_handle) {
                        card_int_map_set(
                            state,
                            creature,
                            p.handle,
                            "OblivionPower._amountsForPlayedCards",
                            cp.card,
                            p.amount,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    if let Some(amt) = card_int_map_remove(
                        state,
                        creature,
                        p.handle,
                        "OblivionPower._amountsForPlayedCards",
                        cp.card,
                        false,
                    ) {
                        let _ = power::apply(
                            state,
                            content,
                            PowerId::DoomPower,
                            creature,
                            Dec::from_i32(amt),
                            p.applier_handle,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { side, .. } = args {
                    if *side == CombatSide::Player {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::OneForAllPower => {
            if slot == HookSlot::ModifyDamageAdditive {
                if let HookArgs::ModifyDamage {
                    props,
                    card_source,
                    card_play,
                    ..
                } = args
                {
                    if props.is_powered_attack()
                        && card_source.is_some()
                        && creature == state.player.creature_handle
                    {
                        let play_card = card_play.map_or(card_source.unwrap(), |cp| cp.card);
                        let is_x = state.card(play_card).is_some_and(|c| {
                            c.temp_flags.contains(CardTempFlags::COSTS_X_ENERGY)
                                || generated_card_meta(c.id)
                                    .flags
                                    .contains(sts2_types::CardStaticFlags::IS_X_ENERGY_COST)
                        });
                        if is_x {
                            return Ok(HookResult::default());
                        }
                        if let Some(cp) = card_play {
                            if (!cp.is_auto_play && cp.energy_spent != 0)
                                || (cp.is_auto_play && cp.energy_value != 0)
                            {
                                return Ok(HookResult::default());
                            }
                        } else if let Some(cs) = card_source {
                            let cost = sts2_kernel::ordering::resolve_energy_cost(state, content, *cs)?;
                            if cost != 0 {
                                return Ok(HookResult::default());
                            }
                        }
                        return Ok(HookResult {
                            dec_val: Some(Dec::from_i32(p.amount)),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::OneTwoPunchPower => match slot {
            HookSlot::ModifyCardPlayCount => {
                if let HookArgs::CardPlayCount {
                    card, play_count, ..
                } = args
                {
                    if creature == state.player.creature_handle
                        && card_type_of(state, *card) == Some(CardType::Attack)
                    {
                        return Ok(HookResult {
                            int_val: Some(*play_count + 1),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterModifyingCardPlayCount => {
                power::tick_down_duration_by_handle(state, content, creature, p.handle)?;
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::OrbitPower => {
            if slot == HookSlot::AfterEnergySpent {
                if let HookArgs::CardCost { cost, .. } = args {
                    if creature == state.player.creature_handle {
                        let spent_amt = cost.to_i32_trunc()?;
                        let mut energy_spent =
                            get_int_field(state, &p, "OrbitPower._energySpent", 0) + spent_amt;
                        while energy_spent >= 4 {
                            gold::gain_energy(state, Dec::from_i32(p.amount))?;
                            energy_spent -= 4;
                        }
                        set_int_field(
                            state,
                            creature,
                            p.handle,
                            "OrbitPower._energySpent",
                            energy_spent,
                        );
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::PagestormPower => {
            if slot == HookSlot::AfterCardDrawn {
                if let HookArgs::CardDrawn { card, .. } = args {
                    if creature == state.player.creature_handle
                        && card_pile::card_has_keyword(state, content, *card, CardKeywords::ETHEREAL)?
                    {
                        let _ = card_pile::draw(state, content, p.amount, false)?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::PainfulStabsPower => match slot {
            HookSlot::AfterAttack => {
                if let HookArgs::Attack { info, results } = args {
                    apply_painful_stabs_after_attack(
                        content,
                        state,
                        creature,
                        p.amount,
                        info.attacker,
                        info.target_side,
                        info.damage_props,
                        results,
                    )?;
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldCreatureBeRemovedFromCombatAfterDeath => {
                if let HookArgs::Creature(c) = args {
                    return Ok(HookResult {
                        bool_val: Some(*c != creature),
                        ..Default::default()
                    });
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::PaleBlueDotPower => match slot {
            HookSlot::AfterCardPlayed => {
                if creature == state.player.creature_handle
                    && !get_bool_field(
                        state,
                        &p,
                        "PaleBlueDotPower._alreadyActivatedThisTurn",
                        false,
                    )
                    && state.player.counters.cards_played_this_turn >= 5
                {
                    set_bool_field(
                        state,
                        creature,
                        p.handle,
                        "PaleBlueDotPower._alreadyActivatedThisTurn",
                        true,
                    );
                    let _ = power::apply(
                        state,
                        content,
                        PowerId::DrawCardsNextTurnPower,
                        creature,
                        Dec::from_i32(p.amount),
                        Some(creature),
                        None,
                    )?;
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        set_bool_field(
                            state,
                            creature,
                            p.handle,
                            "PaleBlueDotPower._alreadyActivatedThisTurn",
                            false,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::PanachePower => match slot {
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    if creature == state.player.creature_handle {
                        let already_applied =
                            get_bool_field(state, &p, "PanachePower._alreadyApplied", false);
                        if !already_applied {
                            let cards_left = get_int_field(state, &p, "PanachePower._cardsLeft", 5);
                            set_bool_field(
                                state,
                                creature,
                                p.handle,
                                "PanachePower._alreadyApplied",
                                true,
                            );
                            set_int_field(
                                state,
                                creature,
                                p.handle,
                                "PanachePower._cardsLeft",
                                cards_left,
                            );
                            return Ok(HookResult::default());
                        }
                        let left = get_int_field(state, &p, "PanachePower._cardsLeft", 5) - 1;
                        if left > 0 {
                            set_int_field(state, creature, p.handle, "PanachePower._cardsLeft", left);
                            return Ok(HookResult::default());
                        }
                        let targets = opponents_of(state, creature);
                        damage::damage_targets(
                            state,
                            content,
                            &targets,
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED,
                            Some(creature),
                            None,
                            Some(*cp),
                        )?;
                        set_int_field(state, creature, p.handle, "PanachePower._cardsLeft", 5);
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::BeforeSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        set_int_field(state, creature, p.handle, "PanachePower._cardsLeft", 5);
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::PaperCutsPower => {
            if slot == HookSlot::AfterDamageGiven {
                if let HookArgs::DamageResult {
                    target,
                    dealer,
                    props,
                    unblocked_damage,
                    ..
                } = args
                {
                    if *dealer == Some(creature)
                        && state.creature(*target).is_some_and(|c| c.is_player)
                        && props.is_powered_attack()
                        && *unblocked_damage > 0
                    {
                        lose_max_hp(state, content, *target, Dec::from_i32(p.amount))?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::ParryPower | PowerId::SeekingEdgePower | PowerId::TheHuntPower | PowerId::ThieveryPower | PowerId::RoyaltiesPower => {
            Ok(HookResult::default())
        }
        PowerId::PersonalHivePower => {
            if slot == HookSlot::AfterDamageReceived {
                if let HookArgs::DamageResult {
                    target,
                    dealer,
                    props,
                    ..
                } = args
                {
                    let resolved_dealer = match *dealer {
                        Some(d)
                            if state.creature(d).is_some_and(|c| {
                                c.monster_id == Some(MonsterId::Osty)
                                    && c.pet_owner_player_index.is_some()
                            }) =>
                        {
                            Some(state.player.creature_handle)
                        }
                        other => other,
                    };
                    if *target == creature
                        && resolved_dealer == Some(state.player.creature_handle)
                        && props.is_powered_attack()
                    {
                        for _ in 0..p.amount {
                            let _ = card_pile::add_generated_card_to_combat(
                                state,
                                content,
                                CardId::Dazed,
                                PileType::Draw,
                                CardPilePosition::Random,
                                0,
                                None,
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::PhantomBladesPower => match slot {
            HookSlot::TryModifyKeywordsInCombat => {
                if let HookArgs::CardOnly(card) = args {
                    if creature == state.player.creature_handle
                        && card_tags_of(state, *card).contains(CardTags::SHIV)
                    {
                        return Ok(HookResult {
                            keywords: Some(CardKeywords::RETAIN),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ModifyDamageAdditive => {
                if let HookArgs::ModifyDamage {
                    dealer,
                    props,
                    card_source,
                    ..
                } = args
                {
                    if *dealer == Some(creature)
                        && props.is_powered_attack()
                        && card_source.is_some_and(|cs| card_tags_of(state, cs).contains(CardTags::SHIV))
                        && state.player.counters.shivs_played_this_turn == 0
                    {
                        return Ok(HookResult {
                            dec_val: Some(Dec::from_i32(p.amount)),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::PiercingWailPower
        | PowerId::ReptileTrinketPower
        | PowerId::SetupStrikePower
        | PowerId::ShacklingPotionPower
        | PowerId::SpeedPotionPower
        | PowerId::SynchronizePower
        | PowerId::TemporaryDexterityPower
        | PowerId::TemporaryStrengthPower => match slot {
            HookSlot::AfterPowerAmountChanged => {
                if let HookArgs::PowerChanged {
                    power_handle,
                    owner,
                    amount,
                    applier,
                    card_source,
                    ..
                } = args
                {
                    if *owner == creature
                        && *power_handle == p.handle
                        && *amount != Dec::from_i32(p.amount)
                    {
                        if let Some((stat_power, sign)) = temporary_power_spec(p.id) {
                            let delta = amount.checked_mul(Dec::from_i32(sign))?;
                            let _ = power::apply(
                                state,
                                content,
                                stat_power,
                                creature,
                                delta,
                                *applier,
                                *card_source,
                            )?;
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    temporary_power_after_turn_end(content, state, creature, p, participants)?;
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::PillarOfCreationPower => {
            if slot == HookSlot::AfterCardGeneratedForCombat {
                if let HookArgs::CardGenerated { creator, .. } = args {
                    apply_pillar_of_creation_after_generated(
                        content, state, creature, p.amount, *creator,
                    )?;
                }
            }
            Ok(HookResult::default())
        }
        PowerId::SmokestackPower => {
            if slot == HookSlot::AfterCardGeneratedForCombat {
                if let HookArgs::CardGenerated { card, creator } = args {
                    apply_smokestack_after_generated(
                        content, state, creature, p.amount, *card, *creator,
                    )?;
                }
            }
            Ok(HookResult::default())
        }
        PowerId::TrashToTreasurePower => {
            if slot == HookSlot::AfterCardGeneratedForCombat {
                if let HookArgs::CardGenerated { card, creator } = args {
                    apply_trash_to_treasure_after_generated(
                        content, state, creature, p.amount, *card, *creator,
                    )?;
                }
            }
            Ok(HookResult::default())
        }
        PowerId::SoulboundPower => {
            if slot == HookSlot::AfterCardGeneratedForCombat {
                if let HookArgs::CardGenerated { card, creator } = args {
                    apply_soulbound_after_generated(
                        content, state, creature, p.handle, *card, *creator,
                    )?;
                }
            }
            Ok(HookResult::default())
        }
        PowerId::PlatingPower => match slot {
            HookSlot::AfterApplied => {
                on_after_applied(content, state, creature, p.handle)?;
                Ok(HookResult::default())
            }
            HookSlot::BeforeSideTurnStart => {
                if let HookArgs::SideTurn { side, .. } = args {
                    if *side == CombatSide::Player
                        && state.creature(creature).is_some_and(|c| c.side == CombatSide::Enemy)
                        && state.round_number <= 1
                    {
                        let _ = block::gain_block(
                            state,
                            content,
                            creature,
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::BeforeSideTurnEndEarly => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = block::gain_block(
                            state,
                            content,
                            creature,
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnStart => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let is_first_player_turn = state
                            .creature(creature)
                            .is_some_and(|c| c.is_player)
                            && state.player.turn_number == 1;
                        let is_first_enemy_turn = state
                            .creature(creature)
                            .is_some_and(|c| c.side == CombatSide::Enemy)
                            && state.round_number == 1;
                        if !is_first_player_turn && !is_first_enemy_turn {
                            let _ = power::modify_amount_by_handle(
                                state,
                                content,
                                creature,
                                p.handle,
                                Dec::from_i32(-1),
                                None,
                                None,
                            )?;
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::PlowPower => {
            if slot == HookSlot::AfterDamageReceived {
                if let HookArgs::DamageResult {
                    target,
                    unblocked_damage,
                    ..
                } = args
                {
                    let below_threshold = state
                        .creature(creature)
                        .is_some_and(|c| c.current_hp <= p.amount);
                    if *target == creature && *unblocked_damage > 0 && below_threshold {
                        let temp_handles: SmallVec<[u32; 4]> = state
                            .creature(creature)
                            .map(|c| {
                                c.powers
                                    .iter()
                                    .filter(|pw| is_temporary_strength_power(pw.id))
                                    .map(|pw| pw.handle)
                                    .collect()
                            })
                            .unwrap_or_default();
                        for th in temp_handles {
                            let _ = power::remove_by_handle(state, content, creature, th)?;
                        }
                        let _ = power::remove_by_id(
                            state,
                            content,
                            creature,
                            PowerId::StrengthPower,
                        )?;
                        if state
                            .creature(creature)
                            .is_some_and(|c| c.monster_id == Some(MonsterId::CeremonialBeast))
                        {
                            if let Some(c) = state.creature_mut(creature) {
                                if let Some(m) = &mut c.monster {
                                    m.private_fields.insert(
                                        "CeremonialBeast._isStunnedByPlowRemoval".to_owned(),
                                        FieldValue::Bool(true),
                                    );
                                    m.private_fields.insert(
                                        "CeremonialBeast._isInSecondPhase".to_owned(),
                                        FieldValue::Bool(true),
                                    );
                                }
                            }
                            if state.static_ctx.game_mode == sts2_types::GameMode::Dread {
                                let _ = power::apply(
                                    state,
                                    content,
                                    PowerId::RitualPower,
                                    creature,
                                    Dec::TWO,
                                    Some(creature),
                                    None,
                                )?;
                            }
                            creature_cmd::stun(state, creature, Some("BEAST_CRY_MOVE"))?;
                        } else {
                            creature_cmd::stun(state, creature, None)?;
                        }
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::PoisonPower => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) && p.amount > 0 {
                        let accelerant_sum: i32 = opponents_of(state, creature)
                            .into_iter()
                            .filter_map(|h| state.creature(h).filter(|c| c.is_alive()))
                            .flat_map(|c| c.powers.iter())
                            .filter(|pw| pw.id == PowerId::AccelerantPower)
                            .map(|pw| pw.amount)
                            .sum();
                        let trigger_count = p.amount.min(1 + accelerant_sum);
                        for _ in 0..trigger_count {
                            let cur_amount = find_power_by_handle(state, creature, p.handle)
                                .map_or(0, |(_, cur)| cur.amount);
                            if cur_amount <= 0 {
                                break;
                            }
                            damage::damage_targets(
                                state,
                                content,
                                &[creature],
                                Dec::from_i32(cur_amount),
                                ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED,
                                None,
                                None,
                                None,
                            )?;
                            if state.creature(creature).is_none_or(|c| !c.is_alive()) {
                                return Ok(HookResult::default());
                            }
                            let _ = power::modify_amount_by_handle(
                                state,
                                content,
                                creature,
                                p.handle,
                                Dec::from_i32(-1),
                                None,
                                None,
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::PossessSpeedPower => match slot {
            HookSlot::AfterPowerAmountChanged => {
                if let HookArgs::PowerChanged {
                    power_id,
                    owner,
                    amount,
                    applier,
                    ..
                } = args
                {
                    if *applier == Some(creature)
                        && state.creature(*owner).is_some_and(|c| c.is_player)
                        && *power_id == PowerId::DexterityPower
                        && *amount < Dec::ZERO
                    {
                        creature_dec_map_add(
                            state,
                            creature,
                            p.handle,
                            "PossessSpeedPower._stolen",
                            *owner,
                            *amount,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterDeath => {
                if let HookArgs::AfterDeath {
                    target,
                    was_removal_prevented,
                    ..
                } = args
                {
                    if !*was_removal_prevented && *target == creature {
                        if let Some(FieldValue::CreatureDecimalMap(vec)) = get_ext(state, &p)
                            .and_then(|e| e.private_fields.get("PossessSpeedPower._stolen"))
                        {
                            let entries = vec.clone();
                            for (th, amt) in entries {
                                let _ = power::apply(
                                    state,
                                    content,
                                    PowerId::DexterityPower,
                                    CreatureHandle(th as u32),
                                    amt.checked_neg()?,
                                    None,
                                    None,
                                )?;
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::PossessStrengthPower => match slot {
            HookSlot::AfterPowerAmountChanged => {
                if let HookArgs::PowerChanged {
                    power_id,
                    owner,
                    amount,
                    applier,
                    ..
                } = args
                {
                    if *applier == Some(creature)
                        && state.creature(*owner).is_some_and(|c| c.is_player)
                        && *power_id == PowerId::StrengthPower
                        && *amount < Dec::ZERO
                    {
                        creature_dec_map_add(
                            state,
                            creature,
                            p.handle,
                            "PossessStrengthPower._stolen",
                            *owner,
                            *amount,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterDeath => {
                if let HookArgs::AfterDeath {
                    target,
                    was_removal_prevented,
                    ..
                } = args
                {
                    if !*was_removal_prevented && *target == creature {
                        if let Some(FieldValue::CreatureDecimalMap(vec)) = get_ext(state, &p)
                            .and_then(|e| e.private_fields.get("PossessStrengthPower._stolen"))
                        {
                            let entries = vec.clone();
                            for (th, amt) in entries {
                                let _ = power::apply(
                                    state,
                                    content,
                                    PowerId::StrengthPower,
                                    CreatureHandle(th as u32),
                                    amt.checked_neg()?,
                                    None,
                                    None,
                                )?;
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::PrepTimePower => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::apply(
                            state,
                            content,
                            PowerId::VigorPower,
                            creature,
                            Dec::from_i32(p.amount),
                            Some(creature),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::PyrePower => {
            if slot == HookSlot::ModifyMaxEnergy {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    if creature == state.player.creature_handle {
                        return Ok(HookResult {
                            dec_val: Some(original_count.checked_add(Dec::from_i32(p.amount))?),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::QuietusPower => match slot {
            HookSlot::AfterArrowShot => {
                if let HookArgs::ArrowShot {
                    arrow_idx,
                    card_source,
                } = args
                {
                    apply_quietus_after_arrow_shot(
                        state,
                        creature,
                        *arrow_idx,
                        card_source.is_some(),
                    );
                }
                Ok(HookResult::default())
            }
            HookSlot::BeforeAttack => {
                if let HookArgs::Attack { info, .. } = args {
                    if info.attacker == creature
                        && info.damage_props.is_powered_attack()
                        && info.damage_props.contains(ValueProp::FROM_ARROW)
                        && get_ext(state, &p).is_none_or(|e| e.attack_command_id.is_none())
                    {
                        if let Some(ext_idx) = ensure_power_ext_by_handle(state, creature, p.handle) {
                            state.power_extensions[ext_idx].attack_command_id =
                                Some(info.attack_id);
                            if let Some(cs) = info.card_source {
                                state.power_extensions[ext_idx].private_fields.insert(
                                    "QuietusPower._cardSource".to_owned(),
                                    FieldValue::CardHandle(cs.0 as i32),
                                );
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ModifyDamageMultiplicative => {
                if let HookArgs::ModifyDamage {
                    dealer,
                    props,
                    card_source,
                    ..
                } = args
                {
                    if *dealer == Some(creature)
                        && props.is_powered_attack()
                        && props.contains(ValueProp::FROM_ARROW)
                    {
                        let tracked_card =
                            get_card_handle_field(state, &p, "QuietusPower._cardSource");
                        let has_cmd =
                            get_ext(state, &p).is_some_and(|e| e.attack_command_id.is_some());
                        if has_cmd && card_source.is_some() && tracked_card.is_some() && *card_source != tracked_card {
                            return Ok(HookResult::default());
                        }
                        return Ok(HookResult {
                            dec_val: Some(Dec::from_i32(p.amount)),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterAttack => {
                if let HookArgs::Attack { info, .. } = args {
                    if get_ext(state, &p)
                        .is_some_and(|e| e.attack_command_id == Some(info.attack_id))
                    {
                        if let Some(ext_idx) = p.ext_index {
                            state.power_extensions[ext_idx as usize].attack_command_id = None;
                            state.power_extensions[ext_idx as usize]
                                .private_fields
                                .remove("QuietusPower._cardSource");
                        }
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::RadiancePower => {
            if slot == HookSlot::AfterEnergyReset && creature == state.player.creature_handle {
                gold::gain_energy(state, Dec::ONE)?;
                power::tick_down_duration_by_handle(state, content, creature, p.handle)?;
            }
            Ok(HookResult::default())
        }
        PowerId::RagePower => match slot {
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    if creature == state.player.creature_handle
                        && card_type_of(state, cp.card) == Some(CardType::Attack)
                    {
                        let _ = block::gain_block(
                            state,
                            content,
                            creature,
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::RampartPower => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { side, .. } = args {
                    if *side == CombatSide::Player && !state.is_player_extra_turn {
                        let enemies = state.enemy_handles.clone();
                        for eh in enemies {
                            if state.creature(eh).is_some_and(|c| {
                                c.is_alive() && c.monster_id == Some(MonsterId::TurretOperator)
                            }) {
                                let _ = block::gain_block(
                                    state,
                                    content,
                                    eh,
                                    Dec::from_i32(p.amount),
                                    ValueProp::UNPOWERED,
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
        PowerId::RavenousPower => {
            if slot == HookSlot::AfterDeath {
                if let HookArgs::AfterDeath {
                    target,
                    was_removal_prevented,
                    ..
                } = args
                {
                    let owner_side = state
                        .creature(creature)
                        .map_or(CombatSide::Enemy, |c| c.side);
                    let same_side_other = *target != creature
                        && state.creature(*target).is_some_and(|c| c.side == owner_side);
                    let owner_alive_slug = state.creature(creature).is_some_and(|c| {
                        c.is_alive() && c.monster_id == Some(MonsterId::CorpseSlug)
                    });
                    if !*was_removal_prevented && same_side_other && owner_alive_slug {
                        if let Some(c) = state.creature_mut(creature) {
                            if let Some(m) = &mut c.monster {
                                m.private_fields.insert(
                                    "CorpseSlug._isRavenous".to_owned(),
                                    FieldValue::Bool(true),
                                );
                            }
                        }
                        stun_monster(state, creature, None);
                        let _ = power::apply(
                            state,
                            content,
                            PowerId::StrengthPower,
                            creature,
                            Dec::from_i32(p.amount),
                            Some(creature),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::ReaperFormPower | PowerId::UnderworldPower => {
            match slot {
                HookSlot::AfterDamageGiven => {
                    if let HookArgs::DamageResult {
                        target,
                        dealer,
                        props,
                        blocked_damage,
                        unblocked_damage,
                        ..
                    } = args
                    {
                        let total_damage = blocked_damage + unblocked_damage;
                        if p.id == PowerId::ReaperFormPower {
                            apply_reaper_form_after_damage_given(
                                content,
                                state,
                                creature,
                                p.amount,
                                *dealer,
                                total_damage,
                                *props,
                                *target,
                            )?;
                        } else {
                            apply_underworld_after_damage_given(
                                content,
                                state,
                                creature,
                                p.amount,
                                *dealer,
                                total_damage,
                                *props,
                                *target,
                            )?;
                        }
                    }
                    Ok(HookResult::default())
                }
                HookSlot::AfterSideTurnEnd if p.id == PowerId::UnderworldPower => {
                    if let HookArgs::SideTurn { side, .. } = args {
                        if *side == CombatSide::Enemy {
                            let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                        }
                    }
                    Ok(HookResult::default())
                }
                _ => Ok(HookResult::default()),
            }
        }
        PowerId::ReattachPower => match slot {
            HookSlot::AfterDeath => {
                if let HookArgs::AfterDeath {
                    target,
                    was_removal_prevented,
                    ..
                } = args
                {
                    let owner_dead = state.creature(creature).is_none_or(|c| c.is_dead());
                    if !*was_removal_prevented
                        && *target == creature
                        && !(are_all_other_segments_dead(state, creature) && owner_dead)
                    {
                        set_bool_field(state, creature, p.handle, "ReattachPower._isReviving", true);
                        set_monster_move_immediate(state, creature, "DEAD_MOVE", false, None);
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldAllowHitting => {
                if let HookArgs::Creature(target) = args {
                    if *target == creature
                        && get_bool_field(state, &p, "ReattachPower._isReviving", false)
                    {
                        return Ok(HookResult {
                            bool_val: Some(false),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldCreatureBeRemovedFromCombatAfterDeath => {
                if let HookArgs::Creature(target) = args {
                    return Ok(HookResult {
                        bool_val: Some(*target != creature),
                        ..Default::default()
                    });
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldPowerBeRemovedOnDeath => {
                if let HookArgs::PowerQuery {
                    owner,
                    power_handle,
                    ..
                } = args
                {
                    if *owner == creature {
                        return Ok(HookResult {
                            bool_val: Some(reattach_should_power_be_removed_on_death(
                                p.handle,
                                *power_handle,
                            )),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldOwnerDeathTriggerFatal => {
                if let HookArgs::Creature(owner) = args {
                    if *owner == creature {
                        return Ok(HookResult {
                            bool_val: Some(reattach_should_owner_death_trigger_fatal(
                                state, creature,
                            )),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::ReboundPower => match slot {
            HookSlot::ModifyCardPlayResultLocation => {
                if let HookArgs::CardPlayResultLocation { pile, .. } = args {
                    if creature == state.player.creature_handle && *pile == PileType::Discard {
                        return Ok(HookResult {
                            location_val: Some((PileType::Draw, CardPilePosition::Top)),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterModifyingCardPlayResultLocation => {
                if creature == state.player.creature_handle {
                    let _ = power::modify_amount_by_handle(
                        state,
                        content,
                        creature,
                        p.handle,
                        Dec::from_i32(-1),
                        None,
                        None,
                    )?;
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::ReflectPower => match slot {
            HookSlot::AfterDamageReceived => {
                if let HookArgs::DamageResult {
                    target,
                    dealer,
                    props,
                    blocked_damage,
                    ..
                } = args
                {
                    apply_reflect_after_damage_received(
                        content,
                        state,
                        creature,
                        *target,
                        *blocked_damage,
                        *props,
                        *dealer,
                    )?;
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnStart => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        power::tick_down_duration_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::RegenPower => {
            if slot == HookSlot::BeforeSideTurnEndEarly {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature)
                        && state.creature(creature).is_some_and(|c| c.is_alive())
                    {
                        creature_cmd::heal(state, content, creature, Dec::from_i32(p.amount))?;
                        power::tick_down_duration_by_handle(state, content, creature, p.handle)?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::RetainHandPower => match slot {
            HookSlot::ShouldFlush => {
                if creature == state.player.creature_handle {
                    return Ok(HookResult {
                        bool_val: Some(false),
                        ..Default::default()
                    });
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { side, participants } = args {
                    if *side == CombatSide::Player && participants.contains(&creature) {
                        power::tick_down_duration_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::RingingPower => match slot {
            HookSlot::AfterApplied => {
                on_after_applied(content, state, creature, p.handle)?;
                Ok(HookResult::default())
            }
            HookSlot::AfterCardEnteredCombat => {
                if let HookArgs::CardOnly(card) = args {
                    if creature == state.player.creature_handle
                        && state.card(*card).is_some_and(|c| c.affliction.is_none())
                    {
                        let _ = card_cmd::afflict(state, *card, AfflictionId::Ringing, 1);
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldPlay => {
                if let HookArgs::CardOnly(card) = args {
                    if creature == state.player.creature_handle
                        && state
                            .card(*card)
                            .is_some_and(|c| c.affliction.is_some_and(|a| a.id == AfflictionId::Ringing))
                    {
                        return Ok(HookResult {
                            bool_val: Some(state.player.counters.card_plays_started_this_turn == 0),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::RitualPower => match slot {
            HookSlot::AfterApplied => {
                on_after_applied(content, state, creature, p.handle)?;
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature)
                        && state.creature(creature).is_some_and(|c| c.is_alive())
                    {
                        if get_bool_field(state, &p, "RitualPower._skipFirstEnemyTurnEnd", false) {
                            set_bool_field(
                                state,
                                creature,
                                p.handle,
                                "RitualPower._skipFirstEnemyTurnEnd",
                                false,
                            );
                        } else {
                            let _ = power::apply(
                                state,
                                content,
                                PowerId::StrengthPower,
                                creature,
                                Dec::from_i32(p.amount),
                                Some(creature),
                                None,
                            )?;
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::RollingBoulderPower => {
            if slot == HookSlot::AfterPlayerTurnStart && creature == state.player.creature_handle {
                let targets = opponents_of(state, creature);
                damage::damage_targets(
                    state,
                    content,
                    &targets,
                    Dec::from_i32(p.amount),
                    ValueProp::UNPOWERED,
                    Some(creature),
                    None,
                    None,
                )?;
                if let Some((idx, _)) = find_power_by_handle(state, creature, p.handle) {
                    if let Some(c) = state.creature_mut(creature) {
                        if let Some(pi) = c.powers.get_mut(idx) {
                            pi.amount = (pi.amount + 5).clamp(-999_999_999, 999_999_999);
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::RupturePower => match slot {
            HookSlot::BeforeCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    let owner_side = state
                        .creature(creature)
                        .map_or(CombatSide::Player, |c| c.side);
                    if creature == state.player.creature_handle && state.current_side == owner_side {
                        card_int_map_set(
                            state,
                            creature,
                            p.handle,
                            "RupturePower._pendingStrength",
                            cp.card,
                            0,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterDamageReceived => {
                if let HookArgs::DamageResult {
                    target,
                    unblocked_damage,
                    card_source,
                    ..
                } = args
                {
                    let owner_side = state
                        .creature(creature)
                        .map_or(CombatSide::Player, |c| c.side);
                    if *target == creature
                        && *unblocked_damage > 0
                        && state.current_side == owner_side
                    {
                        if let Some(cs) = card_source {
                            if card_int_map_contains(
                                state,
                                &p,
                                "RupturePower._pendingStrength",
                                *cs,
                            ) {
                                card_int_map_add(
                                    state,
                                    creature,
                                    p.handle,
                                    "RupturePower._pendingStrength",
                                    *cs,
                                    p.amount,
                                );
                                return Ok(HookResult::default());
                            }
                        }
                        let _ = power::apply(
                            state,
                            content,
                            PowerId::StrengthPower,
                            creature,
                            Dec::from_i32(p.amount),
                            Some(creature),
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    if creature == state.player.creature_handle {
                        if let Some(strength) = card_int_map_remove(
                            state,
                            creature,
                            p.handle,
                            "RupturePower._pendingStrength",
                            cp.card,
                            false,
                        ) {
                            let _ = power::apply(
                                state,
                                content,
                                PowerId::StrengthPower,
                                creature,
                                Dec::from_i32(strength),
                                Some(creature),
                                None,
                            )?;
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::SandpitPower => {
            if slot == HookSlot::AfterSideTurnStartLate {
                if let HookArgs::SideTurn { side, .. } = args {
                    if *side == CombatSide::Enemy
                        && find_power_by_handle(state, creature, p.handle).is_some()
                    {
                        let _ = power::modify_amount_by_handle(
                            state,
                            content,
                            creature,
                            p.handle,
                            Dec::from_i32(-1),
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::SelfFormingClayPower => {
            if slot == HookSlot::AfterBlockCleared {
                if let HookArgs::Creature(target) = args {
                    if *target == creature {
                        let _ = block::gain_block(
                            state,
                            content,
                            creature,
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::SentryModePower => {
            if slot == HookSlot::BeforeHandDraw && creature == state.player.creature_handle {
                for _ in 0..p.amount {
                    let _ = card_pile::add_generated_card_to_combat(
                        state,
                        content,
                        CardId::SweepingGaze,
                        PileType::Hand,
                        CardPilePosition::Bottom,
                        0,
                        Some(creature),
                    )?;
                }
            }
            Ok(HookResult::default())
        }
        PowerId::SerpentFormPower => match slot {
            HookSlot::BeforeCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    if creature == state.player.creature_handle {
                        card_int_map_set(
                            state,
                            creature,
                            p.handle,
                            "SerpentFormPower._amountsForPlayedCards",
                            cp.card,
                            p.amount,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    if let Some(damage) = card_int_map_remove(
                        state,
                        creature,
                        p.handle,
                        "SerpentFormPower._amountsForPlayedCards",
                        cp.card,
                        true,
                    ) {
                        if damage > 0 {
                            let candidates = hittable_enemies(state, content)?;
                            if let Some(&target) = state
                                .run_rngs
                                .get_mut(RunRngType::CombatTargets)
                                .next_item(&candidates)
                            {
                                damage::damage_targets(
                                    state,
                                    content,
                                    &[target],
                                    Dec::from_i32(damage),
                                    ValueProp::UNPOWERED,
                                    Some(creature),
                                    None,
                                    None,
                                )?;
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::SerratedArrowsPower => {
            if slot == HookSlot::ModifyDamageMultiplicative {
                if let HookArgs::ModifyDamage {
                    target,
                    dealer,
                    props,
                    ..
                } = args
                {
                    let dealer_matches = dealer.is_some_and(|d| {
                        d == creature
                            || (creature == state.player.creature_handle
                                && state.pet_handles.contains(&d))
                    });
                    let has_impaled = target.is_some_and(|t| {
                        state
                            .creature(t)
                            .is_some_and(|c| c.powers.iter().any(|pw| pw.id == PowerId::ImpaledPower))
                    });
                    if props.is_powered_attack() && dealer_matches && has_impaled {
                        let bonus = Dec::from_i32(p.amount).checked_div(Dec::from_i32(100))?;
                        return Ok(HookResult {
                            dec_val: Some(Dec::ONE.checked_add(bonus)?),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::ShadowmeldPower => match slot {
            HookSlot::ModifyBlockMultiplicative => {
                if let HookArgs::ModifyBlock { target, .. } = args {
                    if *target == creature {
                        return Ok(HookResult {
                            dec_val: Some(power_of_two_dec(p.amount)?),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::ShadowStepPower => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::apply(
                            state,
                            content,
                            PowerId::DoubleDamagePower,
                            creature,
                            Dec::from_i32(p.amount),
                            Some(creature),
                            None,
                        )?;
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::ShowOffPower => match slot {
            HookSlot::AfterDeath => {
                if let HookArgs::AfterDeath { target, .. } = args {
                    let owner_side = state
                        .creature(creature)
                        .map_or(CombatSide::Player, |c| c.side);
                    let target_side = state
                        .creature(*target)
                        .map_or(CombatSide::Enemy, |c| c.side);
                    let fatal = target_side != owner_side
                        && hooks::should_owner_death_trigger_fatal(state, content, *target)?;
                    if fatal {
                        if let Some(card) =
                            get_card_handle_field(state, &p, "ShowOffPower._showOffInstance")
                        {
                            let mut cur = card;
                            loop {
                                let next_deck = if let Some(ext) = state.card_ext_mut(cur) {
                                    let inc = match ext.private_fields.get("ShowOff._increasedAim") {
                                        Some(FieldValue::Int(v)) => *v + p.amount,
                                        _ => p.amount,
                                    };
                                    ext.private_fields.insert(
                                        "ShowOff._increasedAim".to_owned(),
                                        FieldValue::Int(inc),
                                    );
                                    ext.private_fields.insert(
                                        "ShowOff._currentAim".to_owned(),
                                        FieldValue::Int(5 + inc),
                                    );
                                    state.card(cur).and_then(|c| c.deck_card_handle)
                                } else {
                                    None
                                };
                                match next_deck {
                                    Some(dh) if dh != cur => cur = dh,
                                    _ => break,
                                }
                            }
                        }
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::ShriekPower => {
            if slot == HookSlot::AfterDamageReceived {
                if let HookArgs::DamageResult {
                    target,
                    unblocked_damage,
                    ..
                } = args
                {
                    let below = state
                        .creature(creature)
                        .is_some_and(|c| c.current_hp <= p.amount && c.monster_id == Some(MonsterId::TerrorEel));
                    if *target == creature && *unblocked_damage > 0 && below {
                        creature_cmd::stun(state, creature, Some("TERROR_MOVE"))?;
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::ShrinkPower => match slot {
            HookSlot::ModifyDamageMultiplicative => {
                if let HookArgs::ModifyDamage { dealer, props, .. } = args {
                    if *dealer == Some(creature) && props.is_powered_attack() {
                        return Ok(HookResult {
                            dec_val: Some(Dec::SEVENTY_PERCENT),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if p.amount >= 0 && participants.contains(&creature) {
                        let _ = power::modify_amount_by_handle(
                            state,
                            content,
                            creature,
                            p.handle,
                            Dec::from_i32(-1),
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterDeath => {
                if let HookArgs::AfterDeath {
                    target,
                    was_removal_prevented,
                    ..
                } = args
                {
                    if !*was_removal_prevented && Some(*target) == p.applier_handle {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::ShroudPower => {
            if slot == HookSlot::AfterPowerAmountChanged {
                if let HookArgs::PowerChanged {
                    power_id, applier, ..
                } = args
                {
                    if *applier == Some(creature) && *power_id == PowerId::DoomPower {
                        let _ = block::gain_block(
                            state,
                            content,
                            creature,
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::SicEmPower => match slot {
            HookSlot::AfterDamageGiven => {
                if let HookArgs::DamageResult {
                    target, dealer, ..
                } = args
                {
                    let is_appliers_osty = dealer.is_some_and(|d| {
                        state.creature(d).is_some_and(|c| {
                            c.monster_id == Some(MonsterId::Osty)
                                && c.pet_owner_player_index.is_some()
                        }) && p.applier_handle == Some(state.player.creature_handle)
                    });
                    if is_appliers_osty && *target == creature {
                        let _ = osty::summon(state, content, Dec::from_i32(p.amount))?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::SignalBoostPower => match slot {
            HookSlot::ModifyCardPlayCount => {
                if let HookArgs::CardPlayCount {
                    card, play_count, ..
                } = args
                {
                    if creature == state.player.creature_handle
                        && card_type_of(state, *card) == Some(CardType::Power)
                    {
                        return Ok(HookResult {
                            int_val: Some(*play_count + 1),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterModifyingCardPlayCount => {
                let _ = power::modify_amount_by_handle(
                    state,
                    content,
                    creature,
                    p.handle,
                    Dec::from_i32(-1),
                    None,
                    None,
                )?;
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::SimpletonPower => {
            if slot == HookSlot::AfterAutoPostPlayPhaseEntered
                && creature == state.player.creature_handle
            {
                for _ in 0..p.amount {
                    let hand_snapshot = state.player.hand.clone();
                    let mut min_cost = i32::MAX;
                    let mut lowest_group: SmallVec<[CardHandle; 12]> = SmallVec::new();
                    for h in hand_snapshot {
                        if card_pile::card_has_keyword(state, content, h, CardKeywords::UNPLAYABLE)?
                        {
                            continue;
                        }
                        let cost = resolved_energy_cost_for_comparison(state, content, h)?;
                        if cost < min_cost {
                            min_cost = cost;
                            lowest_group.clear();
                            lowest_group.push(h);
                        } else if cost == min_cost {
                            lowest_group.push(h);
                        }
                    }
                    if lowest_group.is_empty() {
                        continue;
                    }
                    let picked = state
                        .run_rngs
                        .get_mut(RunRngType::CombatCardSelection)
                        .next_item(&lowest_group)
                        .copied();
                    if let Some(card) = picked {
                        if !hooks::is_over_or_ending(state)
                            && state.creature(creature).is_some_and(|c| c.is_alive())
                        {
                            autoplay::from_cards(state, content, &[card], None, false)?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::SkittishPower => match slot {
            HookSlot::AfterAttack => {
                if let HookArgs::Attack { info, results } = args {
                    apply_skittish_after_attack(
                        content,
                        state,
                        creature,
                        p.handle,
                        p.amount,
                        info.damage_props,
                        info.card_source.is_some(),
                        results,
                    )?;
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { side, .. } = args {
                    let owner_side = state
                        .creature(creature)
                        .map_or(CombatSide::Enemy, |c| c.side);
                    if *side != owner_side {
                        set_bool_field(
                            state,
                            creature,
                            p.handle,
                            "SkittishPower._hasGainedBlockThisTurn",
                            false,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::SleightOfFleshPower => {
            if slot == HookSlot::AfterPowerAmountChanged {
                if let HookArgs::PowerChanged {
                    power_id,
                    owner,
                    amount,
                    applier,
                    ..
                } = args
                {
                    if !amount.is_zero()
                        && *applier == Some(creature)
                        && state.creature(*owner).is_some_and(|c| c.side == CombatSide::Enemy)
                        && !is_temporary_power(*power_id)
                    {
                        let p_meta = content.power_meta(*power_id)?;
                        if p_meta.flags.contains(PowerStaticFlags::IS_TEMPORARY) {
                            return Ok(HookResult::default());
                        }
                        let effective_type = if p_meta.stack_type == PowerStackType::Counter
                            && p_meta.flags.contains(PowerStaticFlags::ALLOW_NEGATIVE)
                            && *amount < Dec::ZERO
                        {
                            PowerType::Debuff
                        } else if !p_meta.flags.contains(PowerStaticFlags::ALLOW_NEGATIVE)
                            && p_meta.power_type == PowerType::Debuff
                            && *amount < Dec::ZERO
                        {
                            PowerType::Buff
                        } else {
                            p_meta.power_type
                        };
                        if effective_type == PowerType::Debuff {
                            damage::damage_targets(
                                state,
                                content,
                                &[*owner],
                                Dec::from_i32(p.amount),
                                ValueProp::UNPOWERED,
                                Some(creature),
                                None,
                                None,
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::SlipperyPower => match slot {
            HookSlot::ModifyHpLostAfterOsty => {
                if let HookArgs::ModifyHpLost { target, amount, .. } = args {
                    if *target == creature && *amount >= Dec::ONE {
                        return Ok(HookResult {
                            dec_val: Some(Dec::ONE),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterDamageReceived => {
                if let HookArgs::DamageResult {
                    target,
                    unblocked_damage,
                    ..
                } = args
                {
                    if *target == creature && *unblocked_damage >= 1 {
                        let _ = power::modify_amount_by_handle(
                            state,
                            content,
                            creature,
                            p.handle,
                            Dec::from_i32(-1),
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::SlothPower => match slot {
            HookSlot::ShouldPlay => {
                if creature == state.player.creature_handle {
                    let played = get_int_field(state, &p, "SlothPower._cardsPlayed", 0);
                    return Ok(HookResult {
                        bool_val: Some(played < p.amount),
                        ..Default::default()
                    });
                }
                Ok(HookResult::default())
            }
            HookSlot::BeforeCardPlayed => {
                if creature == state.player.creature_handle {
                    let played = get_int_field(state, &p, "SlothPower._cardsPlayed", 0);
                    set_int_field(state, creature, p.handle, "SlothPower._cardsPlayed", played + 1);
                }
                Ok(HookResult::default())
            }
            HookSlot::BeforeSideTurnStart => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        set_int_field(state, creature, p.handle, "SlothPower._cardsPlayed", 0);
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::SlowPower => match slot {
            HookSlot::AfterCardPlayed => {
                let played =
                    get_int_field(state, &p, "SlowPower._cardsPlayedSinceOwnerTurnStart", 0);
                set_int_field(
                    state,
                    creature,
                    p.handle,
                    "SlowPower._cardsPlayedSinceOwnerTurnStart",
                    played + 1,
                );
                Ok(HookResult::default())
            }
            HookSlot::ModifyDamageMultiplicative => {
                if let HookArgs::ModifyDamage { target, props, .. } = args {
                    if *target == Some(creature) && props.is_powered_attack() {
                        let played = get_int_field(
                            state,
                            &p,
                            "SlowPower._cardsPlayedSinceOwnerTurnStart",
                            0,
                        );
                        let tenth = Dec::from_parts(1, 1).unwrap();
                        let bonus = tenth.checked_mul(Dec::from_i32(played))?;
                        return Ok(HookResult {
                            dec_val: Some(Dec::ONE.checked_add(bonus)?),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnStart => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        set_int_field(
                            state,
                            creature,
                            p.handle,
                            "SlowPower._cardsPlayedSinceOwnerTurnStart",
                            0,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::SlumberPower => match slot {
            HookSlot::AfterDamageReceived => {
                if let HookArgs::DamageResult {
                    target,
                    unblocked_damage,
                    ..
                } = args
                {
                    if *target == creature && *unblocked_damage > 0 {
                        tick_slumber(content, state, creature, p.handle, true)?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { side, participants } = args {
                    let owner_side = state
                        .creature(creature)
                        .map_or(CombatSide::Enemy, |c| c.side);
                    if *side == owner_side && participants.contains(&creature) {
                        tick_slumber(content, state, creature, p.handle, false)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::SmoggyPower => match slot {
            HookSlot::BeforeCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    if creature == state.player.creature_handle
                        && card_type_of(state, cp.card) == Some(CardType::Skill)
                    {
                        set_bool_field(
                            state,
                            creature,
                            p.handle,
                            "SmoggyPower._skillCardPlayStartedThisTurn",
                            true,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardEnteredCombat => {
                if let HookArgs::CardOnly(card) = args {
                    if creature == state.player.creature_handle
                        && card_type_of(state, *card) == Some(CardType::Skill)
                        && get_bool_field(
                            state,
                            &p,
                            "SmoggyPower._skillCardPlayStartedThisTurn",
                            false,
                        )
                        && state.card(*card).is_some_and(|c| c.affliction.is_none())
                    {
                        let _ = card_cmd::afflict(state, *card, AfflictionId::Smog, 1);
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    if creature == state.player.creature_handle
                        && card_type_of(state, cp.card) == Some(CardType::Skill)
                    {
                        for card in all_combat_card_handles(state) {
                            if card_type_of(state, card) == Some(CardType::Skill)
                                && state.card(card).is_some_and(|c| c.affliction.is_none())
                            {
                                let _ = card_cmd::afflict(state, card, AfflictionId::Smog, 1);
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) && creature == state.player.creature_handle {
                        set_bool_field(
                            state,
                            creature,
                            p.handle,
                            "SmoggyPower._skillCardPlayStartedThisTurn",
                            false,
                        );
                        for card in all_combat_card_handles(state) {
                            clear_card_affliction(state, card, Some(AfflictionId::Smog));
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldPlay => {
                if let HookArgs::CardOnly(card) = args {
                    if creature == state.player.creature_handle
                        && state
                            .card(*card)
                            .is_some_and(|c| c.affliction.is_some_and(|a| a.id == AfflictionId::Smog))
                    {
                        return Ok(HookResult {
                            bool_val: Some(false),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::SneakyPower => {
            if slot == HookSlot::AfterCardPlayed {
                if let HookArgs::CardPlay(cp) = args {
                    if creature != state.player.creature_handle
                        && card_type_of(state, cp.card) == Some(CardType::Attack)
                    {
                        let _ = block::gain_block(
                            state,
                            content,
                            creature,
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::SoarPower => {
            if slot == HookSlot::ModifyDamageMultiplicative {
                if let HookArgs::ModifyDamage { target, props, .. } = args {
                    if *target == Some(creature) && props.is_powered_attack() {
                        return Ok(HookResult {
                            dec_val: Some(Dec::HALF),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::SongPathPower => {
            if slot == HookSlot::AfterCardPlayed {
                if let HookArgs::CardPlay(cp) = args {
                    if creature == state.player.creature_handle
                        && card_tags_of(state, cp.card).contains(CardTags::CHO)
                        && state.player.counters.first_cho_play_ordinal_this_turn == cp.play_ordinal
                    {
                        let _ = block::gain_block(
                            state,
                            content,
                            creature,
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::SpectrumShiftPower => {
            if slot == HookSlot::BeforeHandDraw && creature == state.player.creature_handle {
                let count = p.amount.max(0) as usize;
                let picked = card_factory::get_distinct_for_combat(
                    state,
                    CARD_POOL_COLORLESS,
                    count,
                    RunRngType::CombatCardGeneration,
                );
                for cid in picked {
                    let _ = card_pile::add_generated_card_to_combat(
                        state,
                        content,
                        cid,
                        PileType::Hand,
                        CardPilePosition::Bottom,
                        0,
                        Some(creature),
                    )?;
                }
            }
            Ok(HookResult::default())
        }
        PowerId::SpeedsterPower => {
            if slot == HookSlot::AfterCardDrawn {
                if let HookArgs::CardDrawn { from_hand_draw, .. } = args {
                    apply_speedster_after_card_drawn(
                        content,
                        state,
                        creature,
                        p.amount,
                        *from_hand_draw,
                    )?;
                }
            }
            Ok(HookResult::default())
        }
        PowerId::SpinnerPower => {
            if slot == HookSlot::AfterEnergyReset && creature == state.player.creature_handle {
                for _ in 0..p.amount {
                    orb::channel(state, content, OrbId::GlassOrb)?;
                }
            }
            Ok(HookResult::default())
        }
        PowerId::SpiritOfAshPower => {
            if slot == HookSlot::BeforeCardPlayed {
                if let HookArgs::CardPlay(cp) = args {
                    if creature == state.player.creature_handle
                        && card_pile::card_has_keyword(
                            state,
                            content,
                            cp.card,
                            CardKeywords::ETHEREAL,
                        )?
                    {
                        let _ = block::gain_block(
                            state,
                            content,
                            creature,
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::StampedePower => {
            if slot == HookSlot::AfterAutoPostPlayPhaseEntered
                && creature == state.player.creature_handle
            {
                for _ in 0..p.amount {
                    let mut attacks: SmallVec<[CardHandle; 12]> = SmallVec::new();
                    let hand_snapshot = state.player.hand.clone();
                    for h in hand_snapshot {
                        if card_type_of(state, h) == Some(CardType::Attack)
                            && !card_pile::card_has_keyword(
                                state,
                                content,
                                h,
                                CardKeywords::UNPLAYABLE,
                            )?
                        {
                            attacks.push(h);
                        }
                    }
                    let selected = state
                        .run_rngs
                        .get_mut(RunRngType::CombatCardSelection)
                        .next_item(&attacks)
                        .copied();
                    if let Some(card) = selected {
                        if !hooks::is_over_or_ending(state)
                            && state.creature(creature).is_some_and(|c| c.is_alive())
                        {
                            autoplay::from_cards(state, content, &[card], None, false)?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::StarNextTurnPower => {
            if slot == HookSlot::AfterEnergyReset && creature == state.player.creature_handle {
                gold::gain_stars(state, content, Dec::from_i32(p.amount))?;
                let _ = power::remove_by_handle(state, content, creature, p.handle)?;
            }
            Ok(HookResult::default())
        }
        PowerId::SteamEruptionPower => match slot {
            HookSlot::AfterDeath => {
                if let HookArgs::AfterDeath {
                    target,
                    was_removal_prevented,
                    ..
                } = args
                {
                    if !*was_removal_prevented && *target == creature {
                        trigger_waterfall_giant_about_to_blow(content, state, creature)?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldStopCombatFromEnding => Ok(HookResult {
                bool_val: Some(true),
                ..Default::default()
            }),
            HookSlot::ShouldCreatureBeRemovedFromCombatAfterDeath => {
                if let HookArgs::Creature(target) = args {
                    return Ok(HookResult {
                        bool_val: Some(*target != creature),
                        ..Default::default()
                    });
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::StockPower => match slot {
            HookSlot::AfterDeath => {
                if let HookArgs::AfterDeath {
                    target,
                    was_removal_prevented,
                    ..
                } = args
                {
                    if !*was_removal_prevented
                        && *target == creature
                        && p.amount > 0
                        && state.is_live_combat()
                    {
                        let (side, slot_name) = state
                            .creature(creature)
                            .map_or((CombatSide::Enemy, None), |c| (c.side, c.slot_name.clone()));
                        let axebot = creature_cmd::create_monster_creature(
                            state,
                            content,
                            MonsterId::Axebot,
                            side,
                            slot_name.clone(),
                        )?;
                        if let Some(c) = state.creature_mut(axebot) {
                            if let Some(m) = &mut c.monster {
                                m.setup_fields.insert(
                                    "Axebot._stockAmount".to_owned(),
                                    FieldValue::NullableInt(Some(p.amount - 1)),
                                );
                            }
                        }
                        creature_cmd::add_created_monster(
                            state, content, axebot, side, slot_name,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldStopCombatFromEnding => Ok(HookResult {
                bool_val: Some(p.amount > 0),
                ..Default::default()
            }),
            _ => Ok(HookResult::default()),
        },
        PowerId::StormPower => match slot {
            HookSlot::BeforeCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    if creature == state.player.creature_handle
                        && card_type_of(state, cp.card) == Some(CardType::Power)
                    {
                        card_int_map_set(
                            state,
                            creature,
                            p.handle,
                            "StormPower._amountsForPlayedCards",
                            cp.card,
                            p.amount,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    if creature == state.player.creature_handle {
                        if let Some(lightning) = card_int_map_remove(
                            state,
                            creature,
                            p.handle,
                            "StormPower._amountsForPlayedCards",
                            cp.card,
                            true,
                        ) {
                            for _ in 0..lightning {
                                orb::channel(state, content, OrbId::LightningOrb)?;
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::StranglePower => match slot {
            HookSlot::BeforeCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    if p.applier_handle == Some(state.player.creature_handle) {
                        card_int_map_set(
                            state,
                            creature,
                            p.handle,
                            "StranglePower._amountsForPlayedCards",
                            cp.card,
                            p.amount,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    if let Some(dmg) = card_int_map_remove(
                        state,
                        creature,
                        p.handle,
                        "StranglePower._amountsForPlayedCards",
                        cp.card,
                        true,
                    ) {
                        damage::damage_targets(
                            state,
                            content,
                            &[creature],
                            Dec::from_i32(dmg),
                            ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED,
                            None,
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::StratagemPower => {
            if slot == HookSlot::AfterShuffle && creature == state.player.creature_handle {
                let mut empty = SmallVec::new();
                if let Some(req) = resolve_stratagem_after_shuffle(
                    state,
                    content,
                    creature,
                    p.amount,
                    &mut empty,
                )? {
                    return Ok(HookResult {
                        needs_choice: Some(req),
                        ..Default::default()
                    });
                }
            }
            Ok(HookResult::default())
        }
        PowerId::StrengthPower => {
            if slot == HookSlot::ModifyDamageAdditive {
                if let HookArgs::ModifyDamage { dealer, props, .. } = args {
                    if *dealer == Some(creature) && props.is_powered_attack() {
                        return Ok(HookResult {
                            dec_val: Some(Dec::from_i32(p.amount)),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::StretchPower => match slot {
            HookSlot::BeforeCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    let in_flight = get_bool_field(state, &p, "StretchPower._active", false);
                    if creature == state.player.creature_handle
                        && card_gains_block(state, cp.card)
                        && !in_flight
                    {
                        set_bool_field(state, creature, p.handle, "StretchPower._active", true);
                        set_int_field(
                            state,
                            creature,
                            p.handle,
                            "StretchPower._playOrdinal",
                            cp.play_ordinal,
                        );
                        set_int_field(
                            state,
                            creature,
                            p.handle,
                            "StretchPower._amountWhenCardStarted",
                            p.amount,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ModifyBlockAdditive => {
                if let HookArgs::ModifyBlock {
                    props,
                    card_source,
                    card_play,
                    ..
                } = args
                {
                    let Some(cs) = card_source else {
                        return Ok(HookResult::default());
                    };
                    if creature != state.player.creature_handle
                        || !props.contains(ValueProp::MOVE)
                        || props.contains(ValueProp::UNPOWERED)
                    {
                        return Ok(HookResult::default());
                    }
                    let in_flight = get_bool_field(state, &p, "StretchPower._active", false);
                    let tracked_ord = get_int_field(state, &p, "StretchPower._playOrdinal", -1);
                    if in_flight && card_play.is_some_and(|cp| cp.play_ordinal != tracked_ord) {
                        return Ok(HookResult::default());
                    }
                    if card_play.is_none() {
                        return Ok(HookResult {
                            dec_val: Some(if card_gains_block(state, *cs) {
                                Dec::from_i32(p.amount)
                            } else {
                                Dec::ZERO
                            }),
                            ..Default::default()
                        });
                    }
                    if !in_flight {
                        return Ok(HookResult::default());
                    }
                    set_bool_field(
                        state,
                        creature,
                        p.handle,
                        "StretchPower._gaveBonusBlock",
                        true,
                    );
                    return Ok(HookResult {
                        dec_val: Some(Dec::from_i32(p.amount)),
                        ..Default::default()
                    });
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    let in_flight = get_bool_field(state, &p, "StretchPower._active", false);
                    let tracked_ord = get_int_field(state, &p, "StretchPower._playOrdinal", -1);
                    if in_flight && cp.play_ordinal == tracked_ord {
                        let gave =
                            get_bool_field(state, &p, "StretchPower._gaveBonusBlock", false);
                        let started_amt =
                            get_int_field(state, &p, "StretchPower._amountWhenCardStarted", 0);
                        remove_field_key(state, creature, p.handle, "StretchPower._active");
                        remove_field_key(state, creature, p.handle, "StretchPower._playOrdinal");
                        remove_field_key(
                            state,
                            creature,
                            p.handle,
                            "StretchPower._amountWhenCardStarted",
                        );
                        remove_field_key(state, creature, p.handle, "StretchPower._gaveBonusBlock");
                        if gave {
                            let _ = power::modify_amount_by_handle(
                                state,
                                content,
                                creature,
                                p.handle,
                                Dec::from_i32(-started_amt),
                                None,
                                None,
                            )?;
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::SubroutinePower => match slot {
            HookSlot::BeforeCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    if creature == state.player.creature_handle
                        && card_type_of(state, cp.card) == Some(CardType::Power)
                    {
                        card_int_map_set(
                            state,
                            creature,
                            p.handle,
                            "SubroutinePower._amountsForPlayedCards",
                            cp.card,
                            p.amount,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    if creature == state.player.creature_handle {
                        if let Some(energy) = card_int_map_remove(
                            state,
                            creature,
                            p.handle,
                            "SubroutinePower._amountsForPlayedCards",
                            cp.card,
                            true,
                        ) {
                            for _ in 0..energy {
                                gold::gain_energy(state, Dec::ONE)?;
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::SuckPower => {
            if slot == HookSlot::AfterAttack {
                if let HookArgs::Attack { info, results } = args {
                    apply_suck_after_attack(
                        content,
                        state,
                        creature,
                        p.amount,
                        info.attacker,
                        info.target_side,
                        info.damage_props,
                        results,
                    )?;
                }
            }
            Ok(HookResult::default())
        }
        PowerId::SummonNextTurnPower => {
            if slot == HookSlot::AfterPlayerTurnStart
                && creature == state.player.creature_handle
                && p.amount_on_turn_start != 0
            {
                let _ = osty::summon(state, content, Dec::from_i32(p.amount))?;
                let _ = power::remove_by_handle(state, content, creature, p.handle)?;
            }
            Ok(HookResult::default())
        }
        PowerId::SurprisePower => match slot {
            HookSlot::AfterDeath => {
                if let HookArgs::AfterDeath {
                    target,
                    was_removal_prevented,
                    ..
                } = args
                {
                    if !*was_removal_prevented && *target == creature && state.is_live_combat() {
                        let thefts: SmallVec<[(i32, Option<CreatureHandle>); 2]> = state
                            .creature(creature)
                            .map(|c| {
                                c.powers
                                    .iter()
                                    .filter(|pw| pw.id == PowerId::ThieveryPower)
                                    .map(|pw| {
                                        (
                                            get_int_field(state, pw, "ThieveryPower._goldStolen", 0),
                                            get_creature_handle_field(
                                                state,
                                                pw,
                                                "ThieveryPower.<Target>k__BackingField",
                                            ),
                                        )
                                    })
                                    .collect()
                            })
                            .unwrap_or_default();
                        let total_stolen: i32 = thefts.iter().map(|(g, _)| *g).sum();
                        let fat = creature_cmd::create_monster_creature(
                            state,
                            content,
                            MonsterId::FatGremlin,
                            CombatSide::Enemy,
                            Some("fat".to_owned()),
                        )?;
                        if !hooks::is_ending_checked(state, content)?
                            && hooks::should_allow_hitting(state, content, fat)?
                        {
                            for (stolen, th_target) in thefts {
                                if stolen == 0 {
                                    continue;
                                }
                                let mut ext = PowerExtension::default();
                                ext.private_fields.insert(
                                    "HeistPower.<Target>k__BackingField".to_owned(),
                                    FieldValue::CreatureHandle(
                                        th_target.map_or(0, |h| h.0 as i32),
                                    ),
                                );
                                let _ = power::apply_new_with_extension(
                                    state,
                                    content,
                                    PowerId::HeistPower,
                                    fat,
                                    Dec::from_i32(stolen),
                                    Some(creature),
                                    None,
                                    ext,
                                )?;
                            }
                        }
                        let _ = creature_cmd::spawn_monster_in_combat(
                            state,
                            content,
                            MonsterId::SneakyGremlin,
                            CombatSide::Enemy,
                            Some("sneaky".to_owned()),
                        )?;
                        creature_cmd::add_created_monster(
                            state,
                            content,
                            fat,
                            CombatSide::Enemy,
                            Some("fat".to_owned()),
                        )?;
                        if total_stolen > 0
                            && (state.static_ctx.encounter_id == "GREMLIN_MERC_NORMAL"
                                || state.static_ctx.encounter_id == "GremlinMercNormal")
                        {
                            state.gold_was_stolen = true;
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldStopCombatFromEnding => Ok(HookResult {
                bool_val: Some(true),
                ..Default::default()
            }),
            _ => Ok(HookResult::default()),
        },
        PowerId::SurroundedPower => match slot {
            HookSlot::ModifyDamageMultiplicative => {
                if let HookArgs::ModifyDamage { target, dealer, .. } = args {
                    if *target == Some(creature) {
                        if let Some(d) = dealer {
                            let facing = get_int_field(
                                state,
                                &p,
                                "SurroundedPower.<Facing>k__BackingField",
                                1,
                            );
                            let back_left = state.creature(*d).is_some_and(|c| {
                                c.powers
                                    .iter()
                                    .any(|pw| pw.id == PowerId::BackAttackLeftPower)
                            });
                            let back_right = state.creature(*d).is_some_and(|c| {
                                c.powers
                                    .iter()
                                    .any(|pw| pw.id == PowerId::BackAttackRightPower)
                            });
                            let back_attack = (facing == 1 && back_left) || (facing == 0 && back_right);
                            if back_attack {
                                return Ok(HookResult {
                                    dec_val: Some(Dec::ONE_POINT_FIVE),
                                    ..Default::default()
                                });
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::BeforeCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    if creature == state.player.creature_handle {
                        if let Some(t) = cp.target {
                            surrounded_face(state, creature, p.handle, t);
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::BeforePotionUsed => {
                if let HookArgs::Potion { target, .. } = args {
                    apply_surrounded_before_potion_used(state, creature, p.handle, *target);
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterDeath => {
                if let HookArgs::AfterDeath {
                    target,
                    was_removal_prevented,
                    ..
                } = args
                {
                    let owner_side = state
                        .creature(creature)
                        .map_or(CombatSide::Player, |c| c.side);
                    let target_side = state
                        .creature(*target)
                        .map_or(CombatSide::Enemy, |c| c.side);
                    if !*was_removal_prevented && target_side != owner_side {
                        let living: SmallVec<[CreatureHandle; 8]> = state
                            .enemy_handles
                            .iter()
                            .copied()
                            .filter(|&h| state.creature(h).is_some_and(|c| c.is_alive()))
                            .collect();
                        if living.len() == 1 {
                            surrounded_face(state, creature, p.handle, living[0]);
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::SwipePower => {
            if slot == HookSlot::BeforeDeath {
                // Projections share no RunState combat room; StolenCard remains tracked in private_fields.
                let _ = args;
            }
            Ok(HookResult::default())
        }
        PowerId::SwordSagePower => match slot {
            HookSlot::AfterPowerAmountChanged => {
                if let HookArgs::PowerChanged {
                    power_handle,
                    owner,
                    amount,
                    ..
                } = args
                {
                    if *owner == creature && *power_handle == p.handle {
                        let delta = amount.to_i32_trunc()?;
                        if delta != 0 {
                            for ch in all_combat_card_handles(state) {
                                if state.card(ch).is_some_and(|c| c.id == CardId::SovereignBlade) {
                                    if let Some(c) = state.card_mut(ch) {
                                        c.base_replay_count += delta;
                                    }
                                    card_int_map_add(
                                        state,
                                        creature,
                                        p.handle,
                                        "SwordSagePower._grantedReplays",
                                        ch,
                                        delta,
                                    );
                                }
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardEnteredCombat => {
                if let HookArgs::CardOnly(card) = args {
                    if creature == state.player.creature_handle
                        && state.card(*card).is_some_and(|c| c.id == CardId::SovereignBlade)
                        && p.amount != 0
                    {
                        if let Some(c) = state.card_mut(*card) {
                            c.base_replay_count += p.amount;
                        }
                        card_int_map_add(
                            state,
                            creature,
                            p.handle,
                            "SwordSagePower._grantedReplays",
                            *card,
                            p.amount,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardOwnerChanged => {
                if let HookArgs::CardOnly(card) = args {
                    if state.card(*card).is_some_and(|c| c.id == CardId::SovereignBlade) {
                        if creature != state.player.creature_handle {
                            if let Some(granted) = card_int_map_remove(
                                state,
                                creature,
                                p.handle,
                                "SwordSagePower._grantedReplays",
                                *card,
                                false,
                            ) {
                                if let Some(c) = state.card_mut(*card) {
                                    c.base_replay_count -= granted;
                                }
                            }
                        } else if p.amount != 0 {
                            if let Some(c) = state.card_mut(*card) {
                                c.base_replay_count += p.amount;
                            }
                            card_int_map_add(
                                state,
                                creature,
                                p.handle,
                                "SwordSagePower._grantedReplays",
                                *card,
                                p.amount,
                            );
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::TagTeamPower => match slot {
            HookSlot::ModifyCardPlayCount => {
                if let HookArgs::CardPlayCount {
                    card,
                    target,
                    play_count,
                } = args
                {
                    if card_type_of(state, *card) == Some(CardType::Attack)
                        && p.applier_handle != Some(state.player.creature_handle)
                    {
                        let tt = card_target_type_of(state, *card);
                        let valid_target = match tt {
                            TargetType::AnyEnemy => *target == Some(creature),
                            TargetType::AllEnemies => true,
                            _ => false,
                        };
                        if valid_target {
                            return Ok(HookResult {
                                int_val: Some(*play_count + p.amount),
                                ..Default::default()
                            });
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterModifyingCardPlayCount => {
                let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::TaintedPower => match slot {
            HookSlot::ModifyDamageAdditive => {
                if let HookArgs::ModifyDamage { target, props, .. } = args {
                    if *target == Some(creature) && props.is_powered_attack() {
                        return Ok(HookResult {
                            dec_val: Some(Dec::from_i32(p.amount)),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { side, .. } = args {
                    if *side == CombatSide::Enemy {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::TangledPower => match slot {
            HookSlot::AfterApplied => {
                on_after_applied(content, state, creature, p.handle)?;
                Ok(HookResult::default())
            }
            HookSlot::AfterCardEnteredCombat => {
                if let HookArgs::CardOnly(card) = args {
                    if creature == state.player.creature_handle
                        && state.card(*card).is_some_and(|c| c.affliction.is_none())
                        && card_type_of(state, *card) == Some(CardType::Attack)
                    {
                        let _ = card_cmd::afflict(state, *card, AfflictionId::Entangled, 1);
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::TryModifyEnergyCostInCombat => {
                if let HookArgs::CardCost { card, cost } = args {
                    if creature == state.player.creature_handle {
                        if let Some(c) = state.card(*card) {
                            let is_x = c.temp_flags.contains(CardTempFlags::COSTS_X_ENERGY)
                                || generated_card_meta(c.id)
                                    .flags
                                    .contains(sts2_types::CardStaticFlags::IS_X_ENERGY_COST);
                            if !is_x
                                && c.affliction
                                    .is_some_and(|a| a.id == AfflictionId::Entangled)
                            {
                                return Ok(HookResult {
                                    dec_val: Some(cost.checked_add(Dec::from_i32(p.amount))?),
                                    modified: true,
                                    ..Default::default()
                                });
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::TankPower => match slot {
            HookSlot::AfterApplied => {
                on_after_applied(content, state, creature, p.handle)?;
                Ok(HookResult::default())
            }
            HookSlot::ModifyDamageMultiplicative => {
                if let HookArgs::ModifyDamage { target, props, .. } = args {
                    if *target == Some(creature) && props.is_powered_attack() {
                        return Ok(HookResult {
                            dec_val: Some(Dec::ONE_POINT_FIVE),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::TenderPower => match slot {
            HookSlot::AfterCardPlayed => {
                if creature == state.player.creature_handle {
                    let played = get_int_field(state, &p, "TenderPower._cardsPlayedThisTurn", 0);
                    set_int_field(
                        state,
                        creature,
                        p.handle,
                        "TenderPower._cardsPlayedThisTurn",
                        played + 1,
                    );
                    let _ = power::apply(
                        state,
                        content,
                        PowerId::StrengthPower,
                        creature,
                        Dec::from_i32(-1),
                        p.applier_handle,
                        None,
                    )?;
                    let _ = power::apply(
                        state,
                        content,
                        PowerId::DexterityPower,
                        creature,
                        Dec::from_i32(-1),
                        p.applier_handle,
                        None,
                    )?;
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let played =
                            get_int_field(state, &p, "TenderPower._cardsPlayedThisTurn", 0);
                        if played != 0 {
                            let _ = power::apply(
                                state,
                                content,
                                PowerId::StrengthPower,
                                creature,
                                Dec::from_i32(played),
                                p.applier_handle,
                                None,
                            )?;
                            let _ = power::apply(
                                state,
                                content,
                                PowerId::DexterityPower,
                                creature,
                                Dec::from_i32(played),
                                p.applier_handle,
                                None,
                            )?;
                            set_int_field(
                                state,
                                creature,
                                p.handle,
                                "TenderPower._cardsPlayedThisTurn",
                                0,
                            );
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::TerritorialPower => {
            if slot == HookSlot::AfterSideTurnEnd {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::apply(
                            state,
                            content,
                            PowerId::StrengthPower,
                            creature,
                            Dec::from_i32(p.amount),
                            Some(creature),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::TheBombPower => {
            if slot == HookSlot::BeforeSideTurnEnd {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        if p.amount > 1 {
                            let _ = power::modify_amount_by_handle(
                                state,
                                content,
                                creature,
                                p.handle,
                                Dec::from_i32(-1),
                                None,
                                None,
                            )?;
                        } else {
                            let bomb_dmg =
                                get_dec_field(state, &p, "TheBombPower._damage", Dec::from_i32(40));
                            let targets = hittable_enemies(state, content)?;
                            damage::damage_targets(
                                state,
                                content,
                                &targets,
                                bomb_dmg,
                                ValueProp::UNPOWERED,
                                Some(creature),
                                None,
                                None,
                            )?;
                            let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::TheGambitPower => {
            if slot == HookSlot::AfterDamageReceived {
                if let HookArgs::DamageResult {
                    target,
                    props,
                    unblocked_damage,
                    ..
                } = args
                {
                    if *target == creature && props.is_powered_attack() && *unblocked_damage > 0 {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                        creature_cmd::kill(state, content, creature, None)?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::TheSealedThronePower => {
            if slot == HookSlot::BeforeCardPlayed && creature == state.player.creature_handle {
                gold::gain_stars(state, content, Dec::from_i32(p.amount))?;
            }
            Ok(HookResult::default())
        }
        PowerId::ThornsPower => {
            if slot == HookSlot::BeforeDamageReceived {
                if let HookArgs::ModifyHpLost {
                    target,
                    props,
                    dealer,
                    ..
                } = args
                {
                    if *target == creature && props.is_powered_attack() {
                        if let Some(d) = dealer {
                            damage::damage_targets(
                                state,
                                content,
                                &[*d],
                                Dec::from_i32(p.amount),
                                ValueProp::UNPOWERED | ValueProp::SKIP_HURT_ANIM,
                                Some(creature),
                                None,
                                None,
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::ThunderPower => {
            if slot == HookSlot::AfterOrbEvoked {
                if let HookArgs::OrbEvoked { orb, targets } = args {
                    apply_thunder_after_orb_evoked(
                        content,
                        state,
                        creature,
                        p.amount,
                        *orb,
                        targets,
                    )?;
                }
            }
            Ok(HookResult::default())
        }
        PowerId::ToolsOfTheTradePower => match slot {
            HookSlot::ModifyHandDraw => {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    if creature == state.player.creature_handle {
                        return Ok(HookResult {
                            dec_val: Some(original_count.checked_add(Dec::from_i32(p.amount))?),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterPlayerTurnStart => {
                if creature == state.player.creature_handle {
                    let mut empty = SmallVec::new();
                    if let Some(req) = resolve_tools_of_the_trade_after_turn_start(
                        state, content, creature, p.amount, &mut empty,
                    )? {
                        return Ok(HookResult {
                            needs_choice: Some(req),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::ToricToughnessPower => {
            if slot == HookSlot::AfterBlockCleared {
                if let HookArgs::Creature(target) = args {
                    if *target == creature {
                        let stored = get_dec_field(
                            state,
                            &p,
                            "ToricToughnessPower.<StoredBlock>k__BackingField",
                            Dec::ZERO,
                        );
                        let _ = block::gain_block(
                            state,
                            content,
                            creature,
                            stored,
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                        let _ = power::modify_amount_by_handle(
                            state,
                            content,
                            creature,
                            p.handle,
                            Dec::from_i32(-1),
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::TrackingPower => {
            if slot == HookSlot::ModifyDamageMultiplicative {
                if let HookArgs::ModifyDamage {
                    target,
                    dealer,
                    props,
                    card_source,
                    ..
                } = args
                {
                    let has_weak = target.is_some_and(|t| {
                        state
                            .creature(t)
                            .is_some_and(|c| c.powers.iter().any(|pw| pw.id == PowerId::WeakPower))
                    });
                    if props.is_powered_attack()
                        && card_source.is_some()
                        && has_weak
                        && *dealer == Some(creature)
                    {
                        let bonus = Dec::from_i32(p.amount).checked_div(Dec::from_i32(100))?;
                        return Ok(HookResult {
                            dec_val: Some(Dec::ONE.checked_add(bonus)?),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::TunePower => {
            if slot == HookSlot::AfterSideTurnEnd {
                if let HookArgs::SideTurn { side, .. } = args {
                    if *side != CombatSide::Enemy
                        && creature == state.player.creature_handle
                        && melody::played_this_turn(state, CardTags::KI)
                    {
                        let _ = power::apply(
                            state,
                            content,
                            PowerId::FreeCardPower,
                            creature,
                            Dec::from_i32(p.amount),
                            Some(creature),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::TyrannyPower => match slot {
            HookSlot::ModifyHandDraw => {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    if creature == state.player.creature_handle {
                        return Ok(HookResult {
                            dec_val: Some(original_count.checked_add(Dec::from_i32(p.amount))?),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterPlayerTurnStart => {
                if creature == state.player.creature_handle {
                    let mut empty = SmallVec::new();
                    if let Some(req) = resolve_tyranny_after_turn_start(
                        state, content, creature, p.amount, &mut empty,
                    )? {
                        return Ok(HookResult {
                            needs_choice: Some(req),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::UnmovablePower => {
            if slot == HookSlot::ModifyBlockMultiplicative {
                if let HookArgs::ModifyBlock {
                    target,
                    props,
                    card_source,
                    card_play,
                    ..
                } = args
                {
                    let is_monster = state.creature(*target).is_some_and(|c| c.is_monster());
                    if is_monster
                        || !props.contains(ValueProp::MOVE)
                        || (card_source.is_some() && creature != state.player.creature_handle)
                    {
                        return Ok(HookResult::default());
                    }
                    let previous = if creature == state.player.creature_handle {
                        state
                            .semantic_history
                            .card_block_gains
                            .iter()
                            .filter(|(tk, actor_idx, ord)| {
                                *actor_idx == 0
                                    && tk.round_number == state.round_number
                                    && tk.side == state.current_side
                                    && tk.player_turn_numbers.first().copied().flatten()
                                        == Some(state.player.turn_number)
                                    && card_play.is_none_or(|cp| *ord != cp.play_ordinal)
                            })
                            .count() as i32
                    } else {
                        0
                    };
                    if previous < p.amount {
                        return Ok(HookResult {
                            dec_val: Some(Dec::TWO),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::VeilpiercerPower => match slot {
            HookSlot::TryModifyEnergyCostInCombatLate => {
                if let HookArgs::CardCost { card, .. } = args {
                    let pile = state.card(*card).map_or(PileType::None, |c| c.pile);
                    if creature == state.player.creature_handle
                        && matches!(pile, PileType::Hand | PileType::Play)
                        && card_pile::card_has_keyword(
                            state,
                            content,
                            *card,
                            CardKeywords::ETHEREAL,
                        )?
                    {
                        return Ok(HookResult {
                            dec_val: Some(Dec::ZERO),
                            modified: true,
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::BeforeCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    let pile = state.card(cp.card).map_or(PileType::None, |c| c.pile);
                    if creature == state.player.creature_handle
                        && matches!(pile, PileType::Hand | PileType::Play)
                        && card_pile::card_has_keyword(
                            state,
                            content,
                            cp.card,
                            CardKeywords::ETHEREAL,
                        )?
                    {
                        let _ = power::modify_amount_by_handle(
                            state,
                            content,
                            creature,
                            p.handle,
                            Dec::from_i32(-1),
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::ViciousPower => {
            if slot == HookSlot::AfterPowerAmountChanged {
                if let HookArgs::PowerChanged {
                    power_id,
                    amount,
                    applier,
                    ..
                } = args
                {
                    if *amount > Dec::ZERO
                        && *applier == Some(creature)
                        && *power_id == PowerId::VulnerablePower
                        && creature == state.player.creature_handle
                    {
                        let _ = card_pile::draw(state, content, p.amount, false)?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::VigorPower => match slot {
            HookSlot::BeforeAttack => {
                if let HookArgs::Attack { info, .. } = args {
                    if info.attacker == creature && info.damage_props.is_powered_attack() {
                        if let Some(ext_idx) = ensure_power_ext_by_handle(state, creature, p.handle) {
                            if state.power_extensions[ext_idx].attack_command_id.is_none() {
                                state.power_extensions[ext_idx].attack_command_id =
                                    Some(info.attack_id);
                                state.power_extensions[ext_idx].amount_when_attack_started =
                                    p.amount;
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ModifyDamageAdditive => {
                if let HookArgs::ModifyDamage { dealer, props, .. } = args {
                    if *dealer == Some(creature) && props.is_powered_attack() {
                        return Ok(HookResult {
                            dec_val: Some(Dec::from_i32(p.amount)),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterAttack => {
                if let HookArgs::Attack { info, .. } = args {
                    if let Some(ext_idx) = p.ext_index {
                        let ext = &mut state.power_extensions[ext_idx as usize];
                        if ext.attack_command_id == Some(info.attack_id) {
                            let amt = ext.amount_when_attack_started;
                            ext.attack_command_id = None;
                            ext.amount_when_attack_started = 0;
                            let _ = power::modify_amount_by_handle(
                                state,
                                content,
                                creature,
                                p.handle,
                                Dec::from_i32(-amt),
                                None,
                                None,
                            )?;
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::ViolentChordPower => {
            if slot == HookSlot::AfterCardPlayed {
                if let HookArgs::CardPlay(cp) = args {
                    if creature == state.player.creature_handle
                        && card_tags_of(state, cp.card).contains(CardTags::WAH)
                        && state.player.counters.first_wah_play_ordinal_this_turn == cp.play_ordinal
                    {
                        let targets = hittable_enemies(state, content)?;
                        if !targets.is_empty() {
                            damage::damage_targets(
                                state,
                                content,
                                &targets,
                                Dec::from_i32(p.amount),
                                ValueProp::UNPOWERED,
                                Some(creature),
                                None,
                                None,
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::VitalSparkPower => match slot {
            HookSlot::BeforeCombatStart => {
                for card in all_combat_card_handles(state) {
                    if card_type_of(state, card) == Some(CardType::Skill)
                        && state.card(card).is_some_and(|c| c.affliction.is_none())
                    {
                        let _ = card_cmd::afflict(state, card, AfflictionId::Tainted, p.amount);
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardEnteredCombat => {
                if let HookArgs::CardOnly(card) = args {
                    if card_type_of(state, *card) == Some(CardType::Skill)
                        && state.card(*card).is_some_and(|c| c.affliction.is_none())
                    {
                        let _ = card_cmd::afflict(state, *card, AfflictionId::Tainted, p.amount);
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    if state
                        .card(cp.card)
                        .is_some_and(|c| c.affliction.is_some_and(|a| a.id == AfflictionId::Tainted))
                    {
                        let _ = power::apply(
                            state,
                            content,
                            PowerId::TaintedPower,
                            state.player.creature_handle,
                            Dec::from_i32(p.amount),
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterPowerAmountChanged => {
                if let HookArgs::PowerChanged {
                    power_handle,
                    owner,
                    ..
                } = args
                {
                    if *owner == creature && *power_handle == p.handle {
                        for card in all_combat_card_handles(state) {
                            if card_type_of(state, card) == Some(CardType::Skill) {
                                if let Some(c) = state.card_mut(card) {
                                    if let Some(aff) = &mut c.affliction {
                                        if aff.id == AfflictionId::Tainted {
                                            aff.amount = p.amount;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::VoidFormPower => match slot {
            HookSlot::AfterApplied => {
                on_after_applied(content, state, creature, p.handle)?;
                Ok(HookResult::default())
            }
            HookSlot::BeforePowerAmountChanged => {
                if let HookArgs::PowerChanged {
                    power_handle,
                    owner,
                    ..
                } = args
                {
                    if *owner == creature && *power_handle == p.handle {
                        set_int_field(
                            state,
                            creature,
                            p.handle,
                            "VoidFormPower._cardsPlayedThisTurn",
                            999_999_999,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::TryModifyEnergyCostInCombatLate | HookSlot::TryModifyStarCostInCombat => {
                if let HookArgs::CardCost { card, .. } = args {
                    let pile = state.card(*card).map_or(PileType::None, |c| c.pile);
                    let played =
                        get_int_field(state, &p, "VoidFormPower._cardsPlayedThisTurn", 0);
                    let should_skip = creature != state.player.creature_handle
                        || !matches!(pile, PileType::Hand | PileType::Play)
                        || played >= p.amount;
                    if !should_skip {
                        return Ok(HookResult {
                            dec_val: Some(Dec::ZERO),
                            modified: true,
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    if creature == state.player.creature_handle
                        && !cp.is_auto_play
                        && cp.play_index == cp.play_count - 1
                    {
                        let played =
                            get_int_field(state, &p, "VoidFormPower._cardsPlayedThisTurn", 0);
                        set_int_field(
                            state,
                            creature,
                            p.handle,
                            "VoidFormPower._cardsPlayedThisTurn",
                            played + 1,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::BeforeSideTurnStart => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        set_int_field(
                            state,
                            creature,
                            p.handle,
                            "VoidFormPower._cardsPlayedThisTurn",
                            0,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::VulnerablePower => match slot {
            HookSlot::ModifyDamageMultiplicative => {
                if let HookArgs::ModifyDamage {
                    target,
                    dealer,
                    props,
                    card_source,
                    ..
                } = args
                {
                    if *target == Some(creature) && props.is_powered_attack() {
                        let mult = hooks::modify_vulnerable_multiplier(
                            state,
                            content,
                            *target,
                            *dealer,
                            Dec::ONE_POINT_FIVE,
                            *props,
                            *card_source,
                        )?;
                        return Ok(HookResult {
                            dec_val: Some(mult),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { side, .. } = args {
                    if *side == CombatSide::Enemy {
                        power::tick_down_duration_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::WasteAwayPower => {
            if slot == HookSlot::ModifyMaxEnergy {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    if creature == state.player.creature_handle {
                        let next = original_count
                            .checked_sub(Dec::from_i32(p.amount))?
                            .max(Dec::ZERO);
                        return Ok(HookResult {
                            dec_val: Some(next),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::WeakPower => match slot {
            HookSlot::ModifyDamageMultiplicative => {
                if let HookArgs::ModifyDamage {
                    target,
                    dealer,
                    props,
                    card_source,
                    ..
                } = args
                {
                    if *dealer == Some(creature) && props.is_powered_attack() {
                        let mult = hooks::modify_weak_multiplier(
                            state,
                            content,
                            *target,
                            *dealer,
                            Dec::THREE_QUARTERS,
                            *props,
                            *card_source,
                        )?;
                        return Ok(HookResult {
                            dec_val: Some(mult),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { side, .. } = args {
                    if *side == CombatSide::Enemy {
                        power::tick_down_duration_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::WellLaidPlansPower => {
            if slot == HookSlot::ShouldFlush && creature == state.player.creature_handle {
                return Ok(HookResult {
                    bool_val: Some(false),
                    ..Default::default()
                });
            }
            Ok(HookResult::default())
        }
        PowerId::WitheringPresencePower => {
            if slot == HookSlot::AfterCardPlayed {
                let target = get_creature_handle_field(
                    state,
                    &p,
                    "WitheringPresencePower.<Target>k__BackingField",
                );
                if target == Some(state.player.creature_handle) {
                    let played =
                        get_int_field(state, &p, "WitheringPresencePower._cardsPlayed", 0) + 1;
                    if played < p.amount {
                        set_int_field(
                            state,
                            creature,
                            p.handle,
                            "WitheringPresencePower._cardsPlayed",
                            played,
                        );
                    } else {
                        set_int_field(
                            state,
                            creature,
                            p.handle,
                            "WitheringPresencePower._cardsPlayed",
                            0,
                        );
                        let _ = card_pile::add_generated_card_to_combat(
                            state,
                            content,
                            CardId::Wither,
                            PileType::Hand,
                            CardPilePosition::Bottom,
                            0,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::WraithFormPower => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::apply(
                            state,
                            content,
                            PowerId::DexterityPower,
                            creature,
                            Dec::from_i32(-p.amount),
                            Some(creature),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        PowerId::WuppysLoyaltyPower => match slot {
            HookSlot::ModifyPreBlockDamageTarget => {
                if let HookArgs::ModifyUnblockedTarget { target, props, .. } = args {
                    let is_pet_owner = state
                        .creature(creature)
                        .is_some_and(|c| c.pet_owner_player_index.is_some() && c.is_alive())
                        && *target == state.player.creature_handle;
                    if is_pet_owner && props.is_powered_attack() {
                        return Ok(HookResult {
                            creature_val: Some(creature),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldAllowHitting => {
                if let HookArgs::Creature(target) = args {
                    let alive = state.creature(*target).is_some_and(|c| c.is_alive());
                    return Ok(HookResult {
                        bool_val: Some(alive),
                        ..Default::default()
                    });
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldCreatureBeRemovedFromCombatAfterDeath => {
                if let HookArgs::Creature(target) = args {
                    return Ok(HookResult {
                        bool_val: Some(*target != creature),
                        ..Default::default()
                    });
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::YahPower => match slot {
            HookSlot::AfterArrowDislodged => {
                if creature == state.player.creature_handle
                    && !get_bool_field(state, &p, "YahPower._triggeredThisTurn", false)
                {
                    set_bool_field(state, creature, p.handle, "YahPower._triggeredThisTurn", true);
                    gold::gain_energy(state, Dec::from_i32(p.amount))?;
                }
                Ok(HookResult::default())
            }
            HookSlot::BeforeSideTurnStart => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        set_bool_field(
                            state,
                            creature,
                            p.handle,
                            "YahPower._triggeredThisTurn",
                            false,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },
        PowerId::YouAreMinePower => {
            if slot == HookSlot::AfterPlayerTurnStart
                && creature == state.player.creature_handle
                && state.player.turn_number >= 2
            {
                let amt = Dec::from_i32(p.amount);
                let _ = power::apply(state, content, PowerId::WeakPower, creature, amt, None, None)?;
                let _ = power::apply(
                    state,
                    content,
                    PowerId::VulnerablePower,
                    creature,
                    amt,
                    None,
                    None,
                )?;
                let _ = power::apply(state, content, PowerId::FrailPower, creature, amt, None, None)?;
                let _ = power::remove_by_handle(state, content, creature, p.handle)?;
            }
            Ok(HookResult::default())
        }
        _ => Err(KernelError::Unsupported {
            kind: "Power",
            id: p.id.model_id_str().to_owned(),
        }),
    }
}

fn tick_slumber(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    stun: bool,
) -> Result<(), KernelError> {
    if find_power_by_handle(state, creature, power_handle).is_none() {
        return Ok(());
    }
    let _ = power::modify_amount_by_handle(
        state,
        content,
        creature,
        power_handle,
        Dec::from_i32(-1),
        None,
        None,
    )?;
    if find_power_by_handle(state, creature, power_handle).is_none()
        && state
            .creature(creature)
            .is_some_and(|c| c.monster_id == Some(MonsterId::SlumberingBeetle))
    {
        if stun {
            stun_monster(state, creature, Some("ROLL_OUT_MOVE"));
        } else {
            let _ = power::remove_by_id(state, content, creature, PowerId::PlatingPower)?;
            set_monster_move_immediate(state, creature, "ROLL_OUT_MOVE", true, None);
        }
    }
    Ok(())
}
