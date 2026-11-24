use smallvec::SmallVec;
use sts2_kernel::commands::{
    block, card as card_cmd, card_factory, card_pile, card_select, creature as creature_cmd,
    damage, gold, orb, osty, potion, power,
};
use sts2_kernel::continuation::ChoiceTiming;
use sts2_kernel::hooks;
use sts2_kernel::{
    BattleState, CardExtension, CardHandle, CardInst, CombatEngine, ContentDispatcher,
    CreatureHandle, FieldValue, HookArgs, HookResult, KernelError, LocalEnergyModifier, RelicInst,
};
use sts2_types::{
    generated_card_meta, CardId, CardKeywords, CardPilePosition, CardTags, CardType, CombatSide,
    Dec, HookMask, HookSlot, LocalEnergyModifierKind, MonsterId, OrbId, PileType, PlayerRngType,
    PlayerTurnPhase, PotionId, PowerId, PowerStackType, PowerStaticFlags, PowerType, RelicId,
    RelicMeta, RelicStaticFlags, RoomType, RunRngType, TargetType, ValueProp,
};

const fn meta(flags: RelicStaticFlags, hook_mask: HookMask) -> RelicMeta {
    RelicMeta { flags, hook_mask }
}

const EMPTY_FLAGS: RelicStaticFlags = RelicStaticFlags::empty();
const ORDER_FLAGS: RelicStaticFlags = RelicStaticFlags::ORDER_SENSITIVE;
const GROWTH_FLAGS: RelicStaticFlags = RelicStaticFlags::HAS_GROWTH;
const RNG_FLAGS: RelicStaticFlags = RelicStaticFlags::USES_RNG;
const HEAL_FLAGS: RelicStaticFlags = RelicStaticFlags::MAY_HEAL;
const ORDER_GROWTH: RelicStaticFlags = RelicStaticFlags::ORDER_SENSITIVE.union(RelicStaticFlags::HAS_GROWTH);
const ORDER_RNG: RelicStaticFlags = RelicStaticFlags::ORDER_SENSITIVE.union(RelicStaticFlags::USES_RNG);
const GROWTH_RNG: RelicStaticFlags = RelicStaticFlags::HAS_GROWTH.union(RelicStaticFlags::USES_RNG);
const PET_ORDER: RelicStaticFlags = RelicStaticFlags::ADDS_PET.union(RelicStaticFlags::ORDER_SENSITIVE);
const PET_GROWTH: RelicStaticFlags = RelicStaticFlags::ADDS_PET.union(RelicStaticFlags::HAS_GROWTH);

static NO_COMBAT_HOOKS_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY);
static PLANISPHERE_META: RelicMeta = meta(HEAL_FLAGS, HookMask::EMPTY);

static NINJA_SCROLL_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::BeforeHandDraw));
static NUNCHAKU_META: RelicMeta = meta(ORDER_GROWTH, HookMask::EMPTY.with(HookSlot::AfterCardPlayed));
static OBSIDIAN_VICE_META: RelicMeta = meta(ORDER_RNG, HookMask::EMPTY.with(HookSlot::BeforeSideTurnEnd));
static OCARINA_META: RelicMeta = meta(RNG_FLAGS, HookMask::EMPTY.with(HookSlot::BeforeHandDraw));
static ODDLY_SMOOTH_STONE_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::AfterRoomEntered));
static ORANGE_DOUGH_META: RelicMeta = meta(RNG_FLAGS, HookMask::EMPTY.with(HookSlot::AfterSideTurnStart));
static ORICHALCUM_META: RelicMeta = meta(
    ORDER_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::BeforeSideTurnStart)
        .with(HookSlot::BeforeSideTurnEndVeryEarly)
        .with(HookSlot::BeforeSideTurnEnd),
);
static ORNAMENTAL_FAN_META: RelicMeta = meta(
    ORDER_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::BeforeSideTurnStart)
        .with(HookSlot::AfterCardPlayed)
        .with(HookSlot::AfterCombatEnd),
);
static PAELS_BLOOD_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::ModifyHandDraw));
static PAELS_EYE_META: RelicMeta = meta(
    ORDER_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::AfterSideTurnStart)
        .with(HookSlot::BeforeSideTurnEndEarly)
        .with(HookSlot::ShouldTakeExtraTurnEarly)
        .with(HookSlot::AfterTakingExtraTurn)
        .with(HookSlot::AfterCombatEnd),
);
static PAELS_FLESH_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::ModifyMaxEnergy));
static PAELS_LEGION_META: RelicMeta = meta(
    PET_ORDER,
    HookMask::EMPTY
        .with(HookSlot::BeforeCombatStart)
        .with(HookSlot::ModifyBlockMultiplicative)
        .with(HookSlot::AfterModifyingBlockAmount)
        .with(HookSlot::AfterCardPlayed)
        .with(HookSlot::AfterSideTurnStart)
        .with(HookSlot::AfterCombatEnd),
);
static PAELS_TEARS_META: RelicMeta = meta(
    ORDER_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::BeforeSideTurnEnd)
        .with(HookSlot::AfterEnergyReset)
        .with(HookSlot::AfterCombatEnd),
);
static PAELS_TOOTH_META: RelicMeta = meta(GROWTH_RNG, HookMask::EMPTY.with(HookSlot::AfterCombatEnd));
static PANTOGRAPH_META: RelicMeta = meta(HEAL_FLAGS, HookMask::EMPTY.with(HookSlot::BeforeCombatStart));
static PAPER_KRANE_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::ModifyWeakMultiplier));
static PAPER_PHROG_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::ModifyVulnerableMultiplier));
static PARRYING_SHIELD_META: RelicMeta = meta(ORDER_RNG, HookMask::EMPTY.with(HookSlot::AfterSideTurnEnd));
static PENDULUM_META: RelicMeta = meta(
    GROWTH_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::BeforeHandDraw)
        .with(HookSlot::ModifyHandDraw),
);
static PEN_NIB_META: RelicMeta = meta(
    ORDER_GROWTH,
    HookMask::EMPTY
        .with(HookSlot::BeforeCardPlayed)
        .with(HookSlot::ModifyDamageMultiplicative)
        .with(HookSlot::AfterCardPlayed),
);
static PERMAFROST_META: RelicMeta = meta(
    ORDER_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::AfterRoomEntered)
        .with(HookSlot::AfterCardPlayed),
);
static PETRIFIED_TOAD_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::BeforeCombatStartLate));
static PHILOSOPHERS_STONE_META: RelicMeta = meta(
    EMPTY_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::ModifyMaxEnergy)
        .with(HookSlot::AfterCreatureAddedToCombat)
        .with(HookSlot::AfterRoomEntered),
);
static PHYLACTERY_UNBOUND_META: RelicMeta = meta(
    EMPTY_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::BeforeCombatStart)
        .with(HookSlot::AfterSideTurnStart),
);
static POCKETWATCH_META: RelicMeta = meta(
    ORDER_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::BeforeSideTurnStart)
        .with(HookSlot::ModifyHandDraw)
        .with(HookSlot::AfterCardPlayed)
        .with(HookSlot::AfterCombatEnd),
);
static POLLINOUS_CORE_META: RelicMeta = meta(
    GROWTH_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::BeforeHandDraw)
        .with(HookSlot::ModifyHandDraw)
        .with(HookSlot::AfterModifyingHandDraw),
);
static POWER_CELL_META: RelicMeta = meta(RNG_FLAGS, HookMask::EMPTY.with(HookSlot::BeforeSideTurnStart));
static PRISMATIC_GEM_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::ModifyMaxEnergy));
static PUMPKIN_CANDLE_META: RelicMeta = meta(
    GROWTH_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::ModifyMaxEnergy)
        .with(HookSlot::AfterCombatEnd),
);
static RADIANT_PEARL_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::BeforeHandDraw));
static RAINBOW_RING_META: RelicMeta = meta(
    ORDER_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::BeforeSideTurnStart)
        .with(HookSlot::AfterCardPlayed)
        .with(HookSlot::AfterCombatEnd),
);
static RAZOR_TOOTH_META: RelicMeta = meta(ORDER_FLAGS, HookMask::EMPTY.with(HookSlot::AfterCardPlayed));
static RED_MASK_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::BeforeSideTurnStart));
static RED_SKULL_META: RelicMeta = meta(
    ORDER_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::AfterRoomEntered)
        .with(HookSlot::AfterCurrentHpChanged)
        .with(HookSlot::AfterCombatEnd),
);
static REGALITE_META: RelicMeta = meta(
    ORDER_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::AfterCardGeneratedForCombat)
        .with(HookSlot::BeforeSideTurnStart)
        .with(HookSlot::AfterCombatEnd),
);
static REPTILE_TRINKET_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::AfterPotionUsed));
static RINGING_TRIANGLE_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::AfterRoomEntered));
static RING_OF_THE_DRAKE_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::ModifyHandDraw));
static RING_OF_THE_SNAKE_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::ModifyHandDraw));
static RIPPLE_BASIN_META: RelicMeta = meta(
    ORDER_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::BeforeSideTurnStart)
        .with(HookSlot::AfterCardPlayed)
        .with(HookSlot::BeforeSideTurnEnd)
        .with(HookSlot::AfterCombatEnd),
);
static ROYAL_POISON_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::AfterPlayerTurnStart));
static RUINED_HELMET_META: RelicMeta = meta(
    ORDER_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::TryModifyPowerAmountReceived)
        .with(HookSlot::AfterModifyingPowerAmountReceived)
        .with(HookSlot::AfterCombatEnd),
);
static RUNIC_CAPACITOR_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::AfterSideTurnStart));
static RUNIC_PYRAMID_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::ShouldFlush));
static SAI_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::AfterSideTurnStart));
static SCREAMING_FLAGON_META: RelicMeta = meta(ORDER_FLAGS, HookMask::EMPTY.with(HookSlot::BeforeSideTurnEnd));
static SEAL_OF_GOLD_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::AfterSideTurnStart));
static SELF_FORMING_CLAY_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::AfterDamageReceived));
static SHURIKEN_META: RelicMeta = meta(
    ORDER_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::BeforeSideTurnStart)
        .with(HookSlot::AfterCardPlayed)
        .with(HookSlot::AfterCombatEnd),
);
static SILVER_CRUCIBLE_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::AfterRoomEntered));
static SLING_OF_COURAGE_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::AfterRoomEntered));
static SNECKO_EYE_META: RelicMeta = meta(
    RNG_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::BeforeCombatStart)
        .with(HookSlot::ModifyHandDraw),
);
static SNECKO_SKULL_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::ModifyPowerAmountGivenAdditive));
static SONG_BOW_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::AfterRoomEntered));
static SOZU_META: RelicMeta = meta(
    EMPTY_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::ShouldProcurePotion)
        .with(HookSlot::ModifyMaxEnergy),
);
static SPARKLING_ROUGE_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::AfterBlockCleared));
static SPIKED_GAUNTLETS_META: RelicMeta = meta(
    EMPTY_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::ModifyMaxEnergy)
        .with(HookSlot::TryModifyEnergyCostInCombat),
);
static STONE_CALENDAR_META: RelicMeta = meta(
    EMPTY_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::AfterRoomEntered)
        .with(HookSlot::AfterSideTurnStart)
        .with(HookSlot::BeforeSideTurnEnd)
        .with(HookSlot::AfterCombatEnd),
);
static STONE_CRACKER_META: RelicMeta = meta(RNG_FLAGS, HookMask::EMPTY.with(HookSlot::AfterRoomEntered));
static STORYTELLER_META: RelicMeta = meta(
    EMPTY_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::AfterRoomEntered)
        .with(HookSlot::BeforeSideTurnEnd),
);
static STRIKE_DUMMY_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::ModifyDamageAdditive));
static STURDY_CLAMP_META: RelicMeta = meta(
    EMPTY_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::ShouldClearBlock)
        .with(HookSlot::AfterPreventingBlockClear),
);
static SWORD_OF_JADE_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::AfterRoomEntered));
static SWORD_OF_STONE_META: RelicMeta = meta(GROWTH_FLAGS, HookMask::EMPTY.with(HookSlot::AfterCombatVictory));
static SYMBIOTIC_VIRUS_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::AfterSideTurnStart));
static TEA_OF_DISCOURTESY_META: RelicMeta = meta(GROWTH_RNG, HookMask::EMPTY.with(HookSlot::BeforeCombatStart));
static THE_ABACUS_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::AfterShuffle));
static THE_BOOT_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::ModifyHpLostAfterOstyLate));
static THROWING_AXE_META: RelicMeta = meta(
    ORDER_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::AfterRoomEntered)
        .with(HookSlot::ModifyCardPlayCount)
        .with(HookSlot::AfterModifyingCardPlayCount)
        .with(HookSlot::AfterCombatEnd),
);
static THUMBSCREW_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::ModifyAttackHitCount));
static TINGSHA_META: RelicMeta = meta(ORDER_RNG, HookMask::EMPTY.with(HookSlot::AfterCardDiscarded));
static TOASTY_MITTENS_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::AfterPlayerTurnStart));
static TOOLBOX_META: RelicMeta = meta(
    RNG_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::BeforeCombatStart)
        .with(HookSlot::BeforeHandDraw)
        .with(HookSlot::AfterCombatEnd),
);
static TORMENTOUS_COLLAR_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::BeforeSideTurnStart));
static TOUGH_BANDAGES_META: RelicMeta = meta(ORDER_FLAGS, HookMask::EMPTY.with(HookSlot::AfterCardDiscarded));
static TOY_BOX_META: RelicMeta = meta(GROWTH_FLAGS, HookMask::EMPTY.with(HookSlot::AfterCombatEnd));
static TUNGSTEN_ROD_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::ModifyHpLostAfterOsty));
static TUNING_FORK_META: RelicMeta = meta(ORDER_GROWTH, HookMask::EMPTY.with(HookSlot::AfterCardPlayed));
static TWINE_META: RelicMeta = meta(ORDER_FLAGS, HookMask::EMPTY.with(HookSlot::AfterArrowDislodged));
static TWISTED_FUNNEL_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::BeforeSideTurnStart));
static UNCEASING_TOP_META: RelicMeta = meta(ORDER_FLAGS, HookMask::EMPTY.with(HookSlot::AfterHandEmptied));
static UNDYING_SIGIL_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::ModifyDamageMultiplicative));
static UNSETTLING_LAMP_META: RelicMeta = meta(
    ORDER_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::BeforeCombatStart)
        .with(HookSlot::BeforePowerAmountChanged)
        .with(HookSlot::ModifyPowerAmountGivenMultiplicative)
        .with(HookSlot::AfterCardPlayed)
        .with(HookSlot::AfterCombatEnd),
);
static VAJRA_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::AfterRoomEntered));
static VAMBRACE_META: RelicMeta = meta(
    ORDER_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::BeforeCombatStart)
        .with(HookSlot::ModifyBlockMultiplicative)
        .with(HookSlot::AfterModifyingBlockAmount)
        .with(HookSlot::AfterCardPlayed)
        .with(HookSlot::AfterCombatEnd),
);
static VELVET_CHOKER_META: RelicMeta = meta(
    ORDER_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::ModifyMaxEnergy)
        .with(HookSlot::ShouldPlay),
);
static VENERABLE_TEA_SET_META: RelicMeta = meta(
    EMPTY_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::AfterRoomEntered)
        .with(HookSlot::AfterEnergyReset),
);
static VERY_HOT_COCOA_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::AfterSideTurnStart));
static VEXING_PUZZLEBOX_META: RelicMeta = meta(RNG_FLAGS, HookMask::EMPTY.with(HookSlot::AfterPlayerTurnStart));
static VITRUVIAN_MINION_META: RelicMeta = meta(
    EMPTY_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::ModifyDamageMultiplicative)
        .with(HookSlot::ModifyBlockMultiplicative),
);
static WAR_HAMMER_META: RelicMeta = meta(GROWTH_RNG, HookMask::EMPTY.with(HookSlot::AfterCombatVictory));
static WHISPERING_EARRING_META: RelicMeta = meta(
    RNG_FLAGS,
    HookMask::EMPTY
        .with(HookSlot::ModifyMaxEnergy)
        .with(HookSlot::AfterAutoPrePlayPhaseEnteredLate),
);
static WHITTLING_KNIFE_META: RelicMeta = meta(EMPTY_FLAGS, HookMask::EMPTY.with(HookSlot::AfterShuffle));
static WONGOS_MYSTERY_TICKET_META: RelicMeta = meta(GROWTH_FLAGS, HookMask::EMPTY.with(HookSlot::AfterCombatEnd));
static WUPPY_META: RelicMeta = meta(
    PET_GROWTH,
    HookMask::EMPTY
        .with(HookSlot::BeforeCombatStart)
        .with(HookSlot::AfterCurrentHpChanged),
);

#[inline]
fn find_relic(state: &BattleState, id: RelicId) -> Option<&RelicInst> {
    state.player.relics.iter().find(|r| r.id == id && !r.is_melted)
}

#[inline]
fn find_relic_mut(state: &mut BattleState, id: RelicId) -> Option<&mut RelicInst> {
    state
        .player
        .relics
        .iter_mut()
        .find(|r| r.id == id && !r.is_melted)
}

#[inline]
fn get_bool(state: &BattleState, id: RelicId, key: &str, default: bool) -> bool {
    find_relic(state, id)
        .and_then(|r| r.private_fields.get(key))
        .and_then(|v| match v {
            FieldValue::Bool(b) => Some(*b),
            _ => None,
        })
        .unwrap_or(default)
}

#[inline]
fn set_bool(state: &mut BattleState, id: RelicId, key: &'static str, val: bool) {
    if let Some(r) = find_relic_mut(state, id) {
        r.private_fields
            .insert(std::borrow::Cow::Borrowed(key), FieldValue::Bool(val));
    }
}

#[inline]
fn get_int(state: &BattleState, id: RelicId, key: &str, default: i32) -> i32 {
    find_relic(state, id)
        .and_then(|r| r.private_fields.get(key))
        .and_then(|v| match v {
            FieldValue::Int(i) | FieldValue::NullableInt(Some(i)) => Some(*i),
            _ => None,
        })
        .unwrap_or(default)
}

#[inline]
fn set_int(state: &mut BattleState, id: RelicId, key: &'static str, val: i32) {
    if let Some(r) = find_relic_mut(state, id) {
        r.private_fields
            .insert(std::borrow::Cow::Borrowed(key), FieldValue::Int(val));
    }
}

#[inline]
fn get_card_handle(state: &BattleState, id: RelicId, key: &str) -> Option<CardHandle> {
    find_relic(state, id)
        .and_then(|r| r.private_fields.get(key))
        .and_then(|v| match v {
            FieldValue::CardHandle(h) | FieldValue::Int(h) | FieldValue::NullableInt(Some(h)) => {
                u32::try_from(*h).ok().map(CardHandle)
            }
            _ => None,
        })
}

#[inline]
fn set_card_handle(
    state: &mut BattleState,
    id: RelicId,
    key: &'static str,
    card: Option<CardHandle>,
) {
    if let Some(r) = find_relic_mut(state, id) {
        match card {
            Some(ch) => {
                r.private_fields.insert(
                    std::borrow::Cow::Borrowed(key),
                    FieldValue::CardHandle(ch.0 as i32),
                );
            }
            None => {
                r.private_fields.remove(key);
            }
        }
    }
}

#[inline]
fn get_string<'a>(state: &'a BattleState, id: RelicId, key: &str) -> Option<&'a str> {
    find_relic(state, id)
        .and_then(|r| r.private_fields.get(key))
        .and_then(|v| match v {
            FieldValue::String(s) => Some(s.as_str()),
            _ => None,
        })
}

#[inline]
fn set_string(state: &mut BattleState, id: RelicId, key: &'static str, val: Option<&str>) {
    if let Some(r) = find_relic_mut(state, id) {
        match val {
            Some(s) => {
                r.private_fields.insert(
                    std::borrow::Cow::Borrowed(key),
                    FieldValue::String(s.to_owned()),
                );
            }
            None => {
                r.private_fields.remove(key);
            }
        }
    }
}

#[inline]
fn remove_field(state: &mut BattleState, id: RelicId, key: &str) {
    if let Some(r) = find_relic_mut(state, id) {
        r.private_fields.remove(key);
    }
}

fn hittable_enemies(
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

fn power_type_for_amount(
    content: &dyn ContentDispatcher,
    power_id: PowerId,
    amount: Dec,
) -> Result<PowerType, KernelError> {
    let meta = content.power_meta(power_id)?;
    let allow_neg = meta.flags.contains(PowerStaticFlags::ALLOW_NEGATIVE);
    if meta.stack_type == PowerStackType::Counter && allow_neg && amount < Dec::ZERO {
        return Ok(PowerType::Debuff);
    }
    if !allow_neg && meta.power_type == PowerType::Debuff && amount < Dec::ZERO {
        return Ok(PowerType::Buff);
    }
    Ok(meta.power_type)
}

const fn temporary_internal_power(power_id: PowerId) -> Option<PowerId> {
    match power_id {
        PowerId::TemporaryStrengthPower
        | PowerId::MonarchsGazeStrengthDownPower
        | PowerId::DarkShacklesPower
        | PowerId::ShacklingPotionPower
        | PowerId::EnfeeblingTouchPower
        | PowerId::CrushUnderPower
        | PowerId::DyingStarPower
        | PowerId::PiercingWailPower
        | PowerId::FlexPotionPower
        | PowerId::CoordinatePower
        | PowerId::ReptileTrinketPower
        | PowerId::ManglePower
        | PowerId::FeedingFrenzyPower
        | PowerId::SetupStrikePower => Some(PowerId::StrengthPower),
        PowerId::TemporaryDexterityPower
        | PowerId::HelicalDartPower
        | PowerId::FadePower
        | PowerId::SpeedPotionPower
        | PowerId::AnticipatePower => Some(PowerId::DexterityPower),
        PowerId::HotfixPower
        | PowerId::SynchronizePower
        | PowerId::FocusedStrikePower
        | PowerId::HyperbeamFocusDownPower => Some(PowerId::FocusPower),
        PowerId::ChoBarbsPower => Some(PowerId::ThornsPower),
        _ => None,
    }
}

fn set_to_free_this_turn(state: &mut BattleState, handle: CardHandle) {
    let Some(card) = state.card(handle) else {
        return;
    };
    let meta = generated_card_meta(card.id);
    if !meta
        .flags
        .contains(sts2_types::CardStaticFlags::IS_X_ENERGY_COST)
    {
        if let Some(c) = state.card_mut(handle) {
            c.temp_cost_override_this_turn_or_until_played = Some(0);
        }
        if let Some(ext_idx) = state.card(handle).and_then(|c| c.ext_index) {
            state.card_extensions[ext_idx as usize]
                .local_energy_modifiers
                .push(LocalEnergyModifier {
                    kind: LocalEnergyModifierKind::SetThisTurnOrUntilPlayed,
                    amount: 0,
                    is_reduce_only: false,
                });
        } else if let Some(idx) = state.cards.iter().position(|c| c.handle == handle) {
            let ext_idx = state.card_extensions.len() as u32;
            let mut ext = CardExtension::default();
            ext.local_energy_modifiers.push(LocalEnergyModifier {
                kind: LocalEnergyModifierKind::SetThisTurnOrUntilPlayed,
                amount: 0,
                is_reduce_only: false,
            });
            state.card_extensions.push(ext);
            if idx < state.static_ctx.root_deck_prefix_len || state.cards[idx].pile == PileType::Deck {
                state.deck_dirty.0 = true;
            }
            state.cards[idx].ext_index = Some(ext_idx);
        }
    }
    if !meta
        .flags
        .contains(sts2_types::CardStaticFlags::IS_X_STAR_COST)
    {
        if let Some(c) = state.card_mut(handle) {
            c.temp_star_cost_override_this_turn = Some(0);
        }
    }
}

fn generate_existing_card_into_pile(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    card_handle: CardHandle,
    pile: PileType,
    pos: CardPilePosition,
    creator: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    card_pile::enter_combat(state, content, card_handle, pile, pos, creator.is_some())?;
    hooks::after_card_generated(state, content, card_handle, creator)?;
    hooks::after_card_generated_for_combat(state, content, card_handle, creator)
}

#[inline]
fn is_owned_minion(
    content: &dyn ContentDispatcher,
    card: Option<&CardInst>,
) -> Result<bool, KernelError> {
    let Some(c) = card else {
        return Ok(false);
    };
    Ok(content.card_meta(c.id)?.base_tags.contains(CardTags::MINION))
}

pub fn whispering_earring_after_auto_pre_play_phase_entered_late(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
) -> Result<(), KernelError> {
    if state.player.turn_number > 1 {
        return Ok(());
    }
    state.vakuu_auto_select_depth += 1;
    let res = whispering_earring_autoplay_loop(content, state);
    state.vakuu_auto_select_depth = state.vakuu_auto_select_depth.saturating_sub(1);
    res
}

fn whispering_earring_autoplay_loop(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
) -> Result<(), KernelError> {
    let start_turn = state.player.turn_number;
    let pc = state.player.creature_handle;
    for _ in 0..13 {
        if hooks::is_over_or_ending_checked(state, content)?
            || state.end_player_turn_requested
            || state.player_creature().is_none_or(|c| c.is_dead())
            || state.player.turn_number != start_turn
        {
            return Ok(());
        }
        let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
        let mut chosen: Option<CardHandle> = None;
        for ch in hand {
            if CombatEngine::can_play_card(state, content, ch)? {
                chosen = Some(ch);
                break;
            }
        }
        let Some(card) = chosen else {
            return Ok(());
        };
        let target_type = content.card_resolved_target_type(state, card)?;
        let target = match target_type {
            TargetType::AnyEnemy => hittable_enemies(state, content)?.into_iter().next(),
            TargetType::AnyAlly => {
                let mut allies: SmallVec<[CreatureHandle; 4]> = SmallVec::new();
                for &ah in &state.ally_handles {
                    if ah != pc && state.creature(ah).is_some_and(|c| c.is_alive()) {
                        allies.push(ah);
                    }
                }
                state
                    .run_rngs
                    .get_mut(RunRngType::CombatTargets)
                    .next_item(&allies)
                    .copied()
            }
            TargetType::AnyPlayer => Some(pc),
            _ => None,
        };
        let _ = sts2_kernel::commands::autoplay::prepay_and_autoplay_card(
            state,
            content,
            card,
            target,
        )?;
    }
    Ok(())
}

fn reconcile_red_skull(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
) -> Result<(), KernelError> {
    let Some(pc_inst) = state.player_creature() else {
        return Ok(());
    };
    let above_half = (pc_inst.current_hp as i64) * 2 > (pc_inst.max_hp as i64);
    let applied = get_bool(state, RelicId::RedSkull, "RedSkull._strengthApplied", false);
    let pc = state.player.creature_handle;
    if above_half && applied {
        power::apply(
            state,
            content,
            PowerId::StrengthPower,
            pc,
            Dec::from_i32(-3),
            Some(pc),
            None,
        )?;
        set_bool(state, RelicId::RedSkull, "RedSkull._strengthApplied", false);
    } else if !above_half && !applied {
        power::apply(
            state,
            content,
            PowerId::StrengthPower,
            pc,
            Dec::from_i32(3),
            Some(pc),
            None,
        )?;
        set_bool(state, RelicId::RedSkull, "RedSkull._strengthApplied", true);
    }
    Ok(())
}

pub fn relic_meta(id: RelicId) -> Result<&'static RelicMeta, KernelError> {
    match id {
        RelicId::NeowsBones
        | RelicId::NeowsSacrifice
        | RelicId::NeowsTalisman
        | RelicId::NeowsTorment
        | RelicId::NewLeaf
        | RelicId::NutritiousOyster
        | RelicId::NutritiousSoup
        | RelicId::OldCoin
        | RelicId::Orrery
        | RelicId::PaelsClaw
        | RelicId::PaelsGrowth
        | RelicId::PaelsHorn
        | RelicId::PaelsWing
        | RelicId::PandorasBox
        | RelicId::Pear
        | RelicId::PhialHolster
        | RelicId::Pomander
        | RelicId::PotionBelt
        | RelicId::PrayerWheel
        | RelicId::PrecariousShears
        | RelicId::PreciseScissors
        | RelicId::PreservedFog
        | RelicId::PunchDagger
        | RelicId::RegalPillow
        | RelicId::RoyalStamp
        | RelicId::SandCastle
        | RelicId::ScrollBoxes
        | RelicId::SeaGlass
        | RelicId::SereTalon
        | RelicId::Shovel
        | RelicId::SignetRing
        | RelicId::SilkenTress
        | RelicId::SmallCapsule
        | RelicId::StoneHumidifier
        | RelicId::Storybook
        | RelicId::Strawberry
        | RelicId::TanxsWhistle
        | RelicId::TheCourier
        | RelicId::TinyMailbox
        | RelicId::TouchOfOrobas
        | RelicId::ToxicEgg
        | RelicId::TriBoomerang
        | RelicId::WarPaint
        | RelicId::Whetstone
        | RelicId::WhiteBeastStatue
        | RelicId::WhiteStar
        | RelicId::WingCharm
        | RelicId::WingedBoots
        | RelicId::WongoCustomerAppreciationBadge
        | RelicId::YummyCookie => Ok(&NO_COMBAT_HOOKS_META),
        RelicId::Planisphere => Ok(&PLANISPHERE_META),
        RelicId::NinjaScroll => Ok(&NINJA_SCROLL_META),
        RelicId::Nunchaku => Ok(&NUNCHAKU_META),
        RelicId::ObsidianVice => Ok(&OBSIDIAN_VICE_META),
        RelicId::Ocarina => Ok(&OCARINA_META),
        RelicId::OddlySmoothStone => Ok(&ODDLY_SMOOTH_STONE_META),
        RelicId::OrangeDough => Ok(&ORANGE_DOUGH_META),
        RelicId::Orichalcum => Ok(&ORICHALCUM_META),
        RelicId::OrnamentalFan => Ok(&ORNAMENTAL_FAN_META),
        RelicId::PaelsBlood => Ok(&PAELS_BLOOD_META),
        RelicId::PaelsEye => Ok(&PAELS_EYE_META),
        RelicId::PaelsFlesh => Ok(&PAELS_FLESH_META),
        RelicId::PaelsLegion => Ok(&PAELS_LEGION_META),
        RelicId::PaelsTears => Ok(&PAELS_TEARS_META),
        RelicId::PaelsTooth => Ok(&PAELS_TOOTH_META),
        RelicId::Pantograph => Ok(&PANTOGRAPH_META),
        RelicId::PaperKrane => Ok(&PAPER_KRANE_META),
        RelicId::PaperPhrog => Ok(&PAPER_PHROG_META),
        RelicId::ParryingShield => Ok(&PARRYING_SHIELD_META),
        RelicId::Pendulum => Ok(&PENDULUM_META),
        RelicId::PenNib => Ok(&PEN_NIB_META),
        RelicId::Permafrost => Ok(&PERMAFROST_META),
        RelicId::PetrifiedToad => Ok(&PETRIFIED_TOAD_META),
        RelicId::PhilosophersStone => Ok(&PHILOSOPHERS_STONE_META),
        RelicId::PhylacteryUnbound => Ok(&PHYLACTERY_UNBOUND_META),
        RelicId::Pocketwatch => Ok(&POCKETWATCH_META),
        RelicId::PollinousCore => Ok(&POLLINOUS_CORE_META),
        RelicId::PowerCell => Ok(&POWER_CELL_META),
        RelicId::PrismaticGem => Ok(&PRISMATIC_GEM_META),
        RelicId::PumpkinCandle => Ok(&PUMPKIN_CANDLE_META),
        RelicId::RadiantPearl => Ok(&RADIANT_PEARL_META),
        RelicId::RainbowRing => Ok(&RAINBOW_RING_META),
        RelicId::RazorTooth => Ok(&RAZOR_TOOTH_META),
        RelicId::RedMask => Ok(&RED_MASK_META),
        RelicId::RedSkull => Ok(&RED_SKULL_META),
        RelicId::Regalite => Ok(&REGALITE_META),
        RelicId::ReptileTrinket => Ok(&REPTILE_TRINKET_META),
        RelicId::RingingTriangle => Ok(&RINGING_TRIANGLE_META),
        RelicId::RingOfTheDrake => Ok(&RING_OF_THE_DRAKE_META),
        RelicId::RingOfTheSnake => Ok(&RING_OF_THE_SNAKE_META),
        RelicId::RippleBasin => Ok(&RIPPLE_BASIN_META),
        RelicId::RoyalPoison => Ok(&ROYAL_POISON_META),
        RelicId::RuinedHelmet => Ok(&RUINED_HELMET_META),
        RelicId::RunicCapacitor => Ok(&RUNIC_CAPACITOR_META),
        RelicId::RunicPyramid => Ok(&RUNIC_PYRAMID_META),
        RelicId::Sai => Ok(&SAI_META),
        RelicId::ScreamingFlagon => Ok(&SCREAMING_FLAGON_META),
        RelicId::SealOfGold => Ok(&SEAL_OF_GOLD_META),
        RelicId::SelfFormingClay => Ok(&SELF_FORMING_CLAY_META),
        RelicId::Shuriken => Ok(&SHURIKEN_META),
        RelicId::SilverCrucible => Ok(&SILVER_CRUCIBLE_META),
        RelicId::SlingOfCourage => Ok(&SLING_OF_COURAGE_META),
        RelicId::SneckoEye => Ok(&SNECKO_EYE_META),
        RelicId::SneckoSkull => Ok(&SNECKO_SKULL_META),
        RelicId::SongBow => Ok(&SONG_BOW_META),
        RelicId::Sozu => Ok(&SOZU_META),
        RelicId::SparklingRouge => Ok(&SPARKLING_ROUGE_META),
        RelicId::SpikedGauntlets => Ok(&SPIKED_GAUNTLETS_META),
        RelicId::StoneCalendar => Ok(&STONE_CALENDAR_META),
        RelicId::StoneCracker => Ok(&STONE_CRACKER_META),
        RelicId::Storyteller => Ok(&STORYTELLER_META),
        RelicId::StrikeDummy => Ok(&STRIKE_DUMMY_META),
        RelicId::SturdyClamp => Ok(&STURDY_CLAMP_META),
        RelicId::SwordOfJade => Ok(&SWORD_OF_JADE_META),
        RelicId::SwordOfStone => Ok(&SWORD_OF_STONE_META),
        RelicId::SymbioticVirus => Ok(&SYMBIOTIC_VIRUS_META),
        RelicId::TeaOfDiscourtesy => Ok(&TEA_OF_DISCOURTESY_META),
        RelicId::TheAbacus => Ok(&THE_ABACUS_META),
        RelicId::TheBoot => Ok(&THE_BOOT_META),
        RelicId::ThrowingAxe => Ok(&THROWING_AXE_META),
        RelicId::Thumbscrew => Ok(&THUMBSCREW_META),
        RelicId::Tingsha => Ok(&TINGSHA_META),
        RelicId::ToastyMittens => Ok(&TOASTY_MITTENS_META),
        RelicId::Toolbox => Ok(&TOOLBOX_META),
        RelicId::TormentousCollar => Ok(&TORMENTOUS_COLLAR_META),
        RelicId::ToughBandages => Ok(&TOUGH_BANDAGES_META),
        RelicId::ToyBox => Ok(&TOY_BOX_META),
        RelicId::TungstenRod => Ok(&TUNGSTEN_ROD_META),
        RelicId::TuningFork => Ok(&TUNING_FORK_META),
        RelicId::Twine => Ok(&TWINE_META),
        RelicId::TwistedFunnel => Ok(&TWISTED_FUNNEL_META),
        RelicId::UnceasingTop => Ok(&UNCEASING_TOP_META),
        RelicId::UndyingSigil => Ok(&UNDYING_SIGIL_META),
        RelicId::UnsettlingLamp => Ok(&UNSETTLING_LAMP_META),
        RelicId::Vajra => Ok(&VAJRA_META),
        RelicId::Vambrace => Ok(&VAMBRACE_META),
        RelicId::VelvetChoker => Ok(&VELVET_CHOKER_META),
        RelicId::VenerableTeaSet => Ok(&VENERABLE_TEA_SET_META),
        RelicId::VeryHotCocoa => Ok(&VERY_HOT_COCOA_META),
        RelicId::VexingPuzzlebox => Ok(&VEXING_PUZZLEBOX_META),
        RelicId::VitruvianMinion => Ok(&VITRUVIAN_MINION_META),
        RelicId::WarHammer => Ok(&WAR_HAMMER_META),
        RelicId::WhisperingEarring => Ok(&WHISPERING_EARRING_META),
        RelicId::WhittlingKnife => Ok(&WHITTLING_KNIFE_META),
        RelicId::WongosMysteryTicket => Ok(&WONGOS_MYSTERY_TICKET_META),
        RelicId::Wuppy => Ok(&WUPPY_META),
        _ => Err(KernelError::Unsupported {
            kind: "Relic",
            id: id.model_id_str().to_owned(),
        }),
    }
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
        RelicId::NeowsBones
        | RelicId::NeowsSacrifice
        | RelicId::NeowsTalisman
        | RelicId::NeowsTorment
        | RelicId::NewLeaf
        | RelicId::NutritiousOyster
        | RelicId::NutritiousSoup
        | RelicId::OldCoin
        | RelicId::Orrery
        | RelicId::PaelsClaw
        | RelicId::PaelsGrowth
        | RelicId::PaelsHorn
        | RelicId::PaelsWing
        | RelicId::PandorasBox
        | RelicId::Pear
        | RelicId::PhialHolster
        | RelicId::Planisphere
        | RelicId::Pomander
        | RelicId::PotionBelt
        | RelicId::PrayerWheel
        | RelicId::PrecariousShears
        | RelicId::PreciseScissors
        | RelicId::PreservedFog
        | RelicId::PunchDagger
        | RelicId::RegalPillow
        | RelicId::RoyalStamp
        | RelicId::SandCastle
        | RelicId::ScrollBoxes
        | RelicId::SeaGlass
        | RelicId::SereTalon
        | RelicId::Shovel
        | RelicId::SignetRing
        | RelicId::SilkenTress
        | RelicId::SmallCapsule
        | RelicId::StoneHumidifier
        | RelicId::Storybook
        | RelicId::Strawberry
        | RelicId::TanxsWhistle
        | RelicId::TheCourier
        | RelicId::TinyMailbox
        | RelicId::TouchOfOrobas
        | RelicId::ToxicEgg
        | RelicId::TriBoomerang
        | RelicId::WarPaint
        | RelicId::Whetstone
        | RelicId::WhiteBeastStatue
        | RelicId::WhiteStar
        | RelicId::WingCharm
        | RelicId::WingedBoots
        | RelicId::WongoCustomerAppreciationBadge
        | RelicId::YummyCookie => Ok(HookResult::default()),

        RelicId::NinjaScroll => {
            if slot == HookSlot::BeforeHandDraw && state.player.turn_number <= 1 {
                for _ in 0..3 {
                    if hooks::is_over_or_ending(state) {
                        break;
                    }
                    let _ = card_pile::add_generated_card_to_combat(
                        state,
                        content,
                        CardId::Shiv,
                        PileType::Hand,
                        CardPilePosition::Bottom,
                        0,
                        Some(pc),
                    )?;
                }
            }
            Ok(HookResult::default())
        }

        RelicId::Nunchaku => {
            if slot == HookSlot::AfterCardPlayed {
                if let HookArgs::CardPlay(cp) = args {
                    if let Some(c) = state.card(cp.card) {
                        if content.card_meta(c.id)?.card_type == CardType::Attack {
                            let next = get_int(state, id, "Nunchaku._attacksPlayed", 0) + 1;
                            set_int(state, id, "Nunchaku._attacksPlayed", next);
                            if next % 10 == 0 {
                                gold::gain_energy(state, Dec::ONE)?;
                            }
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::ObsidianVice => {
            if slot == HookSlot::BeforeSideTurnEnd {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) {
                        let count = state.player.hand.len();
                        if count > 0 {
                            let enemies = hittable_enemies(state, content)?;
                            if let Some(&target) = state
                                .run_rngs
                                .get_mut(RunRngType::CombatTargets)
                                .next_item(&enemies)
                            {
                                damage::damage_targets(
                                    state,
                                    content,
                                    &[target],
                                    Dec::from_i32(count as i32),
                                    ValueProp::UNPOWERED,
                                    Some(pc),
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

        RelicId::Ocarina => {
            if slot == HookSlot::BeforeHandDraw && state.player.turn_number <= 1 {
                let pool = card_factory::melody_card_pool_candidates(state);
                let picked = card_factory::get_distinct_for_combat_filtered(
                    state,
                    &pool,
                    1,
                    RunRngType::CombatCardGeneration,
                    |cid| {
                        generated_card_meta(cid)
                            .base_tags
                            .intersects(CardTags::WAH | CardTags::CHO | CardTags::KI)
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
            Ok(HookResult::default())
        }

        RelicId::OddlySmoothStone => {
            if slot == HookSlot::AfterRoomEntered && state.static_ctx.room_type.is_combat_room() {
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
            Ok(HookResult::default())
        }

        RelicId::OrangeDough => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.turn_number == 1 {
                        let pool = card_factory::colorless_card_pool_candidates(state);
                        let picked = card_factory::get_distinct_for_combat(
                            state,
                            &pool,
                            2,
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
                                Some(pc),
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::Orichalcum => {
            match slot {
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            set_bool(state, id, "Orichalcum._shouldTrigger", false);
                        }
                    }
                }
                HookSlot::BeforeSideTurnEndVeryEarly => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc)
                            && state.player_creature().is_some_and(|c| c.block == 0)
                        {
                            set_bool(state, id, "Orichalcum._shouldTrigger", true);
                        }
                    }
                }
                HookSlot::BeforeSideTurnEnd => {
                    if get_bool(state, id, "Orichalcum._shouldTrigger", false) {
                        set_bool(state, id, "Orichalcum._shouldTrigger", false);
                        block::gain_block(
                            state,
                            content,
                            pc,
                            Dec::from_i32(6),
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

        RelicId::OrnamentalFan => {
            match slot {
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            set_int(state, id, "OrnamentalFan._attacksThisTurn", 0);
                        }
                    }
                }
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(cp) = args {
                        if let Some(c) = state.card(cp.card) {
                            if content.card_meta(c.id)?.card_type == CardType::Attack {
                                let next =
                                    get_int(state, id, "OrnamentalFan._attacksThisTurn", 0) + 1;
                                set_int(state, id, "OrnamentalFan._attacksThisTurn", next);
                                if next % 3 == 0 {
                                    block::gain_block(
                                        state,
                                        content,
                                        pc,
                                        Dec::from_i32(4),
                                        ValueProp::UNPOWERED,
                                        None,
                                        Some(*cp),
                                    )?;
                                }
                            }
                        }
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_int(state, id, "OrnamentalFan._attacksThisTurn", 0);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::PaelsBlood => {
            if slot == HookSlot::ModifyHandDraw {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    return Ok(HookResult {
                        dec_val: Some(original_count.checked_add(Dec::ONE)?),
                        ..Default::default()
                    });
                }
            }
            Ok(HookResult::default())
        }

        RelicId::PaelsEye => {
            match slot {
                HookSlot::AfterSideTurnStart => {
                    if let HookArgs::SideTurn { side, participants } = args {
                        if *side == CombatSide::Player {
                            set_bool(
                                state,
                                id,
                                "PaelsEye._wasOwnerPartOfLastPlayerTurn",
                                participants.contains(&pc),
                            );
                        }
                    }
                }
                HookSlot::BeforeSideTurnEndEarly => {
                    if let HookArgs::SideTurn { side, participants } = args {
                        let used = get_bool(state, id, "PaelsEye._usedThisCombat", false);
                        let was_part =
                            get_bool(state, id, "PaelsEye._wasOwnerPartOfLastPlayerTurn", true);
                        let suppressed_by_earring = state.player.turn_number == 1
                            && state
                                .player
                                .relics
                                .iter()
                                .any(|r| r.id == RelicId::WhisperingEarring);
                        let eligible = !used
                            && was_part
                            && *side == CombatSide::Player
                            && participants.contains(&pc)
                            && !suppressed_by_earring
                            && state.player.counters.manual_cards_played_this_turn == 0;
                        set_bool(state, id, "PaelsEye._eligibleForExtraTurn", eligible);
                        if eligible {
                            let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
                            for c in hand {
                                card_pile::exhaust(state, content, c, false)?;
                            }
                        }
                    }
                }
                HookSlot::ShouldTakeExtraTurnEarly => {
                    let used = get_bool(state, id, "PaelsEye._usedThisCombat", false);
                    let eligible = get_bool(state, id, "PaelsEye._eligibleForExtraTurn", false);
                    return Ok(HookResult {
                        bool_val: Some(!used && eligible),
                        ..Default::default()
                    });
                }
                HookSlot::AfterTakingExtraTurn => {
                    set_bool(state, id, "PaelsEye._usedThisCombat", true);
                    set_bool(state, id, "PaelsEye._eligibleForExtraTurn", false);
                }
                HookSlot::AfterCombatEnd => {
                    set_bool(state, id, "PaelsEye._usedThisCombat", false);
                    set_bool(state, id, "PaelsEye._wasOwnerPartOfLastPlayerTurn", true);
                    set_bool(state, id, "PaelsEye._eligibleForExtraTurn", false);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::PaelsFlesh => {
            if slot == HookSlot::ModifyMaxEnergy && state.player.turn_number >= 3 {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    return Ok(HookResult {
                        dec_val: Some(original_count.checked_add(Dec::ONE)?),
                        ..Default::default()
                    });
                }
            }
            Ok(HookResult::default())
        }

        RelicId::PaelsLegion => {
            match slot {
                HookSlot::BeforeCombatStart => {
                    let h = creature_cmd::create_and_add_monster(
                        state,
                        content,
                        MonsterId::PaelsLegion,
                        CombatSide::Player,
                        None,
                    )?;
                    if !state.pet_handles.contains(&h) {
                        state.pet_handles.push(h);
                    }
                }
                HookSlot::ModifyBlockMultiplicative => {
                    if let HookArgs::ModifyBlock {
                        props,
                        card_source,
                        card_play,
                        ..
                    } = args
                    {
                        let cooldown = get_int(state, id, "PaelsLegion._cooldown", 0);
                        if props.contains(ValueProp::MOVE)
                            && card_source.is_some()
                            && cooldown <= 0
                        {
                            let tracked = find_relic(state, id)
                                .and_then(|r| {
                                    r.private_fields.get("PaelsLegion._affectedCardPlayOrdinal")
                                })
                                .and_then(|v| match v {
                                    FieldValue::Int(i) => Some(*i),
                                    _ => None,
                                });
                            let matches = tracked.is_none()
                                || card_play.is_some_and(|cp| Some(cp.play_ordinal) == tracked);
                            return Ok(HookResult {
                                dec_val: Some(if matches { Dec::TWO } else { Dec::ONE }),
                                ..Default::default()
                            });
                        }
                        return Ok(HookResult {
                            dec_val: Some(Dec::ONE),
                            ..Default::default()
                        });
                    }
                }
                HookSlot::AfterModifyingBlockAmount => match args {
                    HookArgs::ModifyBlock {
                        amount, card_play, ..
                    } => {
                        if *amount > Dec::ZERO {
                            if let Some(cp) = card_play {
                                let tracked = find_relic(state, id)
                                    .and_then(|r| {
                                        r.private_fields
                                            .get("PaelsLegion._affectedCardPlayOrdinal")
                                    })
                                    .and_then(|v| match v {
                                        FieldValue::Int(i) => Some(*i),
                                        _ => None,
                                    });
                                if tracked.is_none() || tracked == Some(cp.play_ordinal) {
                                    set_int(
                                        state,
                                        id,
                                        "PaelsLegion._affectedCardPlayOrdinal",
                                        cp.play_ordinal,
                                    );
                                }
                            }
                        }
                    }
                    _ => {}
                },
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(cp) = args {
                        let tracked = find_relic(state, id)
                            .and_then(|r| {
                                r.private_fields.get("PaelsLegion._affectedCardPlayOrdinal")
                            })
                            .and_then(|v| match v {
                                FieldValue::Int(i) => Some(*i),
                                _ => None,
                            });
                        if tracked == Some(cp.play_ordinal) {
                            remove_field(state, id, "PaelsLegion._affectedCardPlayOrdinal");
                            set_int(state, id, "PaelsLegion._cooldown", 2);
                        }
                    }
                }
                HookSlot::AfterSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            let next = get_int(state, id, "PaelsLegion._cooldown", 0) - 1;
                            set_int(state, id, "PaelsLegion._cooldown", next);
                        }
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_int(state, id, "PaelsLegion._cooldown", 0);
                    remove_field(state, id, "PaelsLegion._affectedCardPlayOrdinal");
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::PaelsTears => {
            match slot {
                HookSlot::BeforeSideTurnEnd => {
                    if let HookArgs::SideTurn { side, participants } = args {
                        if *side == CombatSide::Player && participants.contains(&pc) {
                            let gain_next = state.player.energy > 0;
                            set_bool(state, id, "PaelsTears._gainNextTurn", gain_next);
                        }
                    }
                }
                HookSlot::AfterEnergyReset => {
                    if get_bool(state, id, "PaelsTears._gainNextTurn", false) {
                        gold::gain_energy(state, Dec::TWO)?;
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_bool(state, id, "PaelsTears._gainNextTurn", false);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::PaelsTooth => {
            if slot == HookSlot::AfterCombatEnd
                && state.player_creature().is_some_and(|c| c.is_alive())
            {
                let mut stored: Vec<i32> = find_relic(state, id)
                    .and_then(|r| r.private_fields.get("PaelsTooth._removedCards"))
                    .and_then(|v| match v {
                        FieldValue::CardHandleArray(arr) | FieldValue::IntArray(arr) => {
                            Some(arr.clone())
                        }
                        _ => None,
                    })
                    .unwrap_or_default();
                if !stored.is_empty() {
                    let idx = state
                        .player_rngs
                        .get_mut(PlayerRngType::Rewards)
                        .next_int(stored.len() as i32)
                        .map_err(|e| KernelError::InvalidState(e.to_string()))?
                        as usize;
                    let raw_handle = stored.remove(idx);
                    if let Ok(u) = u32::try_from(raw_handle) {
                        let ch = CardHandle(u);
                        card_cmd::upgrade(state, content, ch)?;
                        state.add_card_to_pile_at(ch, PileType::Deck, None);
                    }
                    if let Some(r) = find_relic_mut(state, id) {
                        r.private_fields.insert(
                            "PaelsTooth._removedCards",
                            FieldValue::CardHandleArray(stored),
                        );
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::Pantograph => {
            if slot == HookSlot::BeforeCombatStart
                && state.player_creature().is_some_and(|c| c.is_alive())
                && state.static_ctx.room_type == RoomType::Boss
            {
                creature_cmd::heal(state, content, pc, Dec::from_i32(25))?;
            }
            Ok(HookResult::default())
        }

        RelicId::PaperKrane => {
            if slot == HookSlot::ModifyWeakMultiplier {
                if let HookArgs::ModifyDamage {
                    target,
                    amount,
                    props,
                    ..
                } = args
                {
                    if *target == Some(pc) && props.is_powered_attack() {
                        return Ok(HookResult {
                            dec_val: Some(amount.checked_sub(Dec::from_parts(15, 2)?)?),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::PaperPhrog => {
            if slot == HookSlot::ModifyVulnerableMultiplier {
                if let HookArgs::ModifyDamage {
                    target,
                    amount,
                    props,
                    ..
                } = args
                {
                    if *target != Some(pc) && props.is_powered_attack() {
                        return Ok(HookResult {
                            dec_val: Some(amount.checked_add(Dec::from_parts(25, 2)?)?),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::ParryingShield => {
            if slot == HookSlot::AfterSideTurnEnd {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc)
                        && state.player_creature().is_some_and(|c| c.block >= 10)
                    {
                        let enemies = hittable_enemies(state, content)?;
                        if let Some(&target) = state
                            .run_rngs
                            .get_mut(RunRngType::CombatTargets)
                            .next_item(&enemies)
                        {
                            damage::damage_targets(
                                state,
                                content,
                                &[target],
                                Dec::from_i32(6),
                                ValueProp::UNPOWERED,
                                Some(pc),
                                None,
                                None,
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::Pendulum => {
            match slot {
                HookSlot::BeforeHandDraw => {
                    let next = (get_int(state, id, "Pendulum._turnCounter", 0) + 1) % 3;
                    set_int(state, id, "Pendulum._turnCounter", next);
                }
                HookSlot::ModifyHandDraw => {
                    if get_int(state, id, "Pendulum._turnCounter", 0) == 0 {
                        if let HookArgs::ModifyHandDraw { original_count } = args {
                            return Ok(HookResult {
                                dec_val: Some(original_count.checked_add(Dec::ONE)?),
                                ..Default::default()
                            });
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::PenNib => {
            match slot {
                HookSlot::BeforeCardPlayed => {
                    if let HookArgs::CardPlay(cp) = args {
                        if let Some(c) = state.card(cp.card) {
                            if content.card_meta(c.id)?.card_type == CardType::Attack {
                                let next = get_int(state, id, "PenNib._attacksPlayed", 0) + 1;
                                set_int(state, id, "PenNib._attacksPlayed", next);
                                if next % 10 == 0 {
                                    set_int(
                                        state,
                                        id,
                                        "PenNib._doubleDamagePlayOrdinal",
                                        cp.play_ordinal,
                                    );
                                    set_card_handle(
                                        state,
                                        id,
                                        "PenNib._doubleDamageCard",
                                        Some(cp.card),
                                    );
                                }
                            }
                        }
                    }
                }
                HookSlot::ModifyDamageMultiplicative => {
                    if let HookArgs::ModifyDamage {
                        props,
                        dealer,
                        card_source,
                        card_play,
                        ..
                    } = args
                    {
                        let tracked_ord = find_relic(state, id)
                            .and_then(|r| r.private_fields.get("PenNib._doubleDamagePlayOrdinal"))
                            .and_then(|v| match v {
                                FieldValue::Int(i) => Some(*i),
                                _ => None,
                            });
                        let tracked_card = get_card_handle(state, id, "PenNib._doubleDamageCard");
                        let osty = state.osty_handle();
                        let dealer_ok = *dealer == Some(pc) || (osty.is_some() && *dealer == osty);
                        let play_ok = card_play.is_some_and(|cp| {
                            Some(cp.play_ordinal) == tracked_ord
                                && Some(cp.card) == tracked_card
                                && *card_source == Some(cp.card)
                        });
                        if play_ok && dealer_ok && props.is_powered_attack() {
                            return Ok(HookResult {
                                dec_val: Some(Dec::TWO),
                                ..Default::default()
                            });
                        }
                        return Ok(HookResult {
                            dec_val: Some(Dec::ONE),
                            ..Default::default()
                        });
                    }
                }
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(cp) = args {
                        let tracked_ord = find_relic(state, id)
                            .and_then(|r| r.private_fields.get("PenNib._doubleDamagePlayOrdinal"))
                            .and_then(|v| match v {
                                FieldValue::Int(i) => Some(*i),
                                _ => None,
                            });
                        if tracked_ord == Some(cp.play_ordinal) {
                            remove_field(state, id, "PenNib._doubleDamagePlayOrdinal");
                            remove_field(state, id, "PenNib._doubleDamageCard");
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::Permafrost => {
            match slot {
                HookSlot::AfterRoomEntered => {
                    if state.static_ctx.room_type.is_combat_room() {
                        set_bool(state, id, "Permafrost._triggeredThisCombat", false);
                    }
                }
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(cp) = args {
                        if !get_bool(state, id, "Permafrost._triggeredThisCombat", false) {
                            if let Some(c) = state.card(cp.card) {
                                if content.card_meta(c.id)?.card_type == CardType::Power {
                                    set_bool(state, id, "Permafrost._triggeredThisCombat", true);
                                    block::gain_block(
                                        state,
                                        content,
                                        pc,
                                        Dec::from_i32(7),
                                        ValueProp::UNPOWERED,
                                        None,
                                        Some(*cp),
                                    )?;
                                }
                            }
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::PetrifiedToad => {
            if slot == HookSlot::BeforeCombatStartLate {
                let _ = potion::try_to_procure(state, content, PotionId::PotionShapedRock)?;
            }
            Ok(HookResult::default())
        }

        RelicId::PhilosophersStone => {
            match slot {
                HookSlot::ModifyMaxEnergy => {
                    if let HookArgs::ModifyHandDraw { original_count } = args {
                        return Ok(HookResult {
                            dec_val: Some(original_count.checked_add(Dec::ONE)?),
                            ..Default::default()
                        });
                    }
                }
                HookSlot::AfterCreatureAddedToCombat => {
                    if let HookArgs::Creature(ch) = args {
                        if state
                            .creature(*ch)
                            .is_some_and(|c| c.side != CombatSide::Player)
                        {
                            power::apply(
                                state,
                                content,
                                PowerId::StrengthPower,
                                *ch,
                                Dec::ONE,
                                None,
                                None,
                            )?;
                        }
                    }
                }
                HookSlot::AfterRoomEntered => {
                    if state.static_ctx.room_type.is_combat_room() {
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
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::PhylacteryUnbound => {
            match slot {
                HookSlot::BeforeCombatStart => {
                    let _ = osty::summon(state, content, Dec::from_i32(5))?;
                }
                HookSlot::AfterSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            let _ = osty::summon(state, content, Dec::TWO)?;
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::Pocketwatch => {
            match slot {
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            let this_turn = get_int(state, id, "Pocketwatch._cardsPlayedThisTurn", 0);
                            set_int(state, id, "Pocketwatch._cardsPlayedLastTurn", this_turn);
                            set_int(state, id, "Pocketwatch._cardsPlayedThisTurn", 0);
                        }
                    }
                }
                HookSlot::ModifyHandDraw => {
                    if state.player.turn_number != 1
                        && get_int(state, id, "Pocketwatch._cardsPlayedLastTurn", 0) <= 3
                    {
                        if let HookArgs::ModifyHandDraw { original_count } = args {
                            return Ok(HookResult {
                                dec_val: Some(original_count.checked_add(Dec::from_i32(3))?),
                                ..Default::default()
                            });
                        }
                    }
                }
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(_) = args {
                        let next = get_int(state, id, "Pocketwatch._cardsPlayedThisTurn", 0) + 1;
                        set_int(state, id, "Pocketwatch._cardsPlayedThisTurn", next);
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_int(state, id, "Pocketwatch._cardsPlayedThisTurn", 0);
                    set_int(state, id, "Pocketwatch._cardsPlayedLastTurn", 0);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::PollinousCore => {
            match slot {
                HookSlot::BeforeHandDraw => {
                    let next = get_int(state, id, "PollinousCore._handsSeen", 0) + 1;
                    set_int(state, id, "PollinousCore._handsSeen", next);
                }
                HookSlot::ModifyHandDraw => {
                    if get_int(state, id, "PollinousCore._handsSeen", 0) >= 4 {
                        if let HookArgs::ModifyHandDraw { original_count } = args {
                            return Ok(HookResult {
                                dec_val: Some(original_count.checked_add(Dec::TWO)?),
                                ..Default::default()
                            });
                        }
                    }
                }
                HookSlot::AfterModifyingHandDraw => {
                    set_int(state, id, "PollinousCore._handsSeen", 0);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::PowerCell => {
            if slot == HookSlot::BeforeSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.turn_number <= 1 {
                        let mut non_innate: Vec<CardHandle> = Vec::new();
                        let mut innate: Vec<CardHandle> = Vec::new();
                        let draw: SmallVec<[CardHandle; 32]> =
                            SmallVec::from_slice(&state.player.draw);
                        for ch in draw {
                            let Some(c) = state.card(ch) else {
                                continue;
                            };
                            let is_x = content
                                .card_meta(c.id)?
                                .flags
                                .contains(sts2_types::CardStaticFlags::IS_X_ENERGY_COST);
                            if is_x
                                || sts2_kernel::ordering::resolve_local_energy_cost(
                                    state, ch, false,
                                ) != 0
                            {
                                continue;
                            }
                            if card_pile::card_has_keyword(state, content, ch, CardKeywords::INNATE)? {
                                innate.push(ch);
                            } else {
                                non_innate.push(ch);
                            }
                        }
                        let mut rng = *state.run_rngs.get(RunRngType::CombatCardSelection);
                        sts2_kernel::ordering::stable_shuffle_cards(
                            state,
                            content,
                            &mut non_innate,
                            &mut rng,
                        )?;
                        sts2_kernel::ordering::stable_shuffle_cards(
                            state,
                            content,
                            &mut innate,
                            &mut rng,
                        )?;
                        *state.run_rngs.get_mut(RunRngType::CombatCardSelection) = rng;

                        for ch in non_innate.into_iter().chain(innate).take(2) {
                            card_pile::add_sync(state, ch, PileType::Hand, CardPilePosition::Bottom);
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::PrismaticGem => {
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

        RelicId::PumpkinCandle => {
            match slot {
                HookSlot::ModifyMaxEnergy => {
                    if get_int(state, id, "PumpkinCandle.<KindleCount>k__BackingField", 0) > 0 {
                        if let HookArgs::ModifyHandDraw { original_count } = args {
                            return Ok(HookResult {
                                dec_val: Some(original_count.checked_add(Dec::ONE)?),
                                ..Default::default()
                            });
                        }
                    }
                }
                HookSlot::AfterCombatEnd => {
                    let cur = get_int(state, id, "PumpkinCandle.<KindleCount>k__BackingField", 0);
                    if cur > 0 {
                        set_int(
                            state,
                            id,
                            "PumpkinCandle.<KindleCount>k__BackingField",
                            cur - 1,
                        );
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::RadiantPearl => {
            if slot == HookSlot::BeforeHandDraw && state.player.turn_number == 1 {
                let _ = card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    CardId::Luminesce,
                    PileType::Hand,
                    CardPilePosition::Bottom,
                    0,
                    Some(pc),
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::RainbowRing => {
            match slot {
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            set_bool(state, id, "RainbowRing._playedAttack", false);
                            set_bool(state, id, "RainbowRing._playedSkill", false);
                            set_bool(state, id, "RainbowRing._playedPower", false);
                            set_bool(state, id, "RainbowRing._triggeredThisTurn", false);
                        }
                    }
                }
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(cp) = args {
                        if !get_bool(state, id, "RainbowRing._triggeredThisTurn", false) {
                            if let Some(c) = state.card(cp.card) {
                                match content.card_meta(c.id)?.card_type {
                                    CardType::Attack => {
                                        set_bool(state, id, "RainbowRing._playedAttack", true);
                                    }
                                    CardType::Skill => {
                                        set_bool(state, id, "RainbowRing._playedSkill", true);
                                    }
                                    CardType::Power => {
                                        set_bool(state, id, "RainbowRing._playedPower", true);
                                    }
                                    _ => return Ok(HookResult::default()),
                                }
                                if get_bool(state, id, "RainbowRing._playedAttack", false)
                                    && get_bool(state, id, "RainbowRing._playedSkill", false)
                                    && get_bool(state, id, "RainbowRing._playedPower", false)
                                {
                                    set_bool(state, id, "RainbowRing._triggeredThisTurn", true);
                                    power::apply(
                                        state,
                                        content,
                                        PowerId::StrengthPower,
                                        pc,
                                        Dec::ONE,
                                        Some(pc),
                                        None,
                                    )?;
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
                }
                HookSlot::AfterCombatEnd => {
                    set_bool(state, id, "RainbowRing._playedAttack", false);
                    set_bool(state, id, "RainbowRing._playedSkill", false);
                    set_bool(state, id, "RainbowRing._playedPower", false);
                    set_bool(state, id, "RainbowRing._triggeredThisTurn", false);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::RazorTooth => {
            if slot == HookSlot::AfterCardPlayed {
                if let HookArgs::CardPlay(cp) = args {
                    if let Some(c) = state.card(cp.card) {
                        if matches!(
                            content.card_meta(c.id)?.card_type,
                            CardType::Attack | CardType::Skill
                        ) {
                            card_cmd::upgrade(state, content, cp.card)?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::RedMask => {
            if slot == HookSlot::BeforeSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.turn_number <= 1 {
                        for eh in hittable_enemies(state, content)? {
                            power::apply(
                                state,
                                content,
                                PowerId::WeakPower,
                                eh,
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

        RelicId::RedSkull => {
            match slot {
                HookSlot::AfterRoomEntered => {
                    if state.static_ctx.room_type.is_combat_room() {
                        reconcile_red_skull(content, state)?;
                    }
                }
                HookSlot::AfterCurrentHpChanged => {
                    if state.is_live_combat() {
                        reconcile_red_skull(content, state)?;
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_bool(state, id, "RedSkull._strengthApplied", false);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::Regalite => {
            match slot {
                HookSlot::AfterCardGeneratedForCombat => {
                    if let HookArgs::CardGenerated { creator, .. } = args {
                        if *creator == Some(pc)
                            && !get_bool(state, id, "Regalite._usedThisTurn", false)
                        {
                            set_bool(state, id, "Regalite._usedThisTurn", true);
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
                    }
                }
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            set_bool(state, id, "Regalite._usedThisTurn", false);
                        }
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_bool(state, id, "Regalite._usedThisTurn", false);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::ReptileTrinket => {
            if slot == HookSlot::AfterPotionUsed && state.is_live_combat() {
                power::apply(
                    state,
                    content,
                    PowerId::ReptileTrinketPower,
                    pc,
                    Dec::from_i32(3),
                    Some(pc),
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::RingingTriangle => {
            if slot == HookSlot::AfterRoomEntered && state.static_ctx.room_type.is_combat_room() {
                power::apply(
                    state,
                    content,
                    PowerId::RetainHandPower,
                    pc,
                    Dec::ONE,
                    Some(pc),
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::RingOfTheDrake => {
            if slot == HookSlot::ModifyHandDraw && state.player.turn_number <= 3 {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    return Ok(HookResult {
                        dec_val: Some(original_count.checked_add(Dec::TWO)?),
                        ..Default::default()
                    });
                }
            }
            Ok(HookResult::default())
        }

        RelicId::RingOfTheSnake => {
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

        RelicId::RippleBasin => {
            match slot {
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            set_bool(state, id, "RippleBasin._attackPlayedThisTurn", false);
                        }
                    }
                }
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(cp) = args {
                        if let Some(c) = state.card(cp.card) {
                            if content.card_meta(c.id)?.card_type == CardType::Attack {
                                set_bool(state, id, "RippleBasin._attackPlayedThisTurn", true);
                            }
                        }
                    }
                }
                HookSlot::BeforeSideTurnEnd => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc)
                            && !get_bool(state, id, "RippleBasin._attackPlayedThisTurn", false)
                        {
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
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_bool(state, id, "RippleBasin._attackPlayedThisTurn", false);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::RoyalPoison => {
            if slot == HookSlot::AfterPlayerTurnStart && state.player.turn_number == 1 {
                damage::damage_targets(
                    state,
                    content,
                    &[pc],
                    Dec::from_i32(4),
                    ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED,
                    None,
                    None,
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::RuinedHelmet => {
            match slot {
                HookSlot::TryModifyPowerAmountReceived => {
                    if let HookArgs::PowerQuery { .. } = args {
                        return Ok(HookResult::default());
                    }
                    if let HookArgs::PowerChanged {
                        power_id,
                        owner,
                        amount,
                        ..
                    } = args
                    {
                        if *power_id == PowerId::StrengthPower
                            && *owner == pc
                            && *amount > Dec::ZERO
                            && !get_bool(state, id, "RuinedHelmet._usedThisCombat", false)
                        {
                            return Ok(HookResult {
                                dec_val: Some(amount.checked_mul(Dec::TWO)?),
                                modified: true,
                                ..Default::default()
                            });
                        }
                    }
                }
                HookSlot::AfterModifyingPowerAmountReceived => {
                    set_bool(state, id, "RuinedHelmet._usedThisCombat", true);
                }
                HookSlot::AfterCombatEnd => {
                    set_bool(state, id, "RuinedHelmet._usedThisCombat", false);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::RunicCapacitor => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.turn_number == 1 {
                        orb::add_slots(state, 3);
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::RunicPyramid => {
            if slot == HookSlot::ShouldFlush {
                return Ok(HookResult {
                    bool_val: Some(false),
                    ..Default::default()
                });
            }
            Ok(HookResult::default())
        }

        RelicId::Sai => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) {
                        block::gain_block(
                            state,
                            content,
                            pc,
                            Dec::from_i32(7),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::ScreamingFlagon => {
            if slot == HookSlot::BeforeSideTurnEnd {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.hand.is_empty() {
                        let enemies = hittable_enemies(state, content)?;
                        damage::damage_targets(
                            state,
                            content,
                            &enemies,
                            Dec::from_i32(20),
                            ValueProp::UNPOWERED,
                            Some(pc),
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::SealOfGold => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.gold >= 3 {
                        gold::gain_energy(state, Dec::ONE)?;
                        gold::lose_gold(state, Dec::from_i32(3))?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::SelfFormingClay => {
            if slot == HookSlot::AfterDamageReceived {
                if let HookArgs::DamageResult {
                    target,
                    unblocked_damage,
                    ..
                } = args
                {
                    if state.is_live_combat() && *target == pc && *unblocked_damage > 0 {
                        power::apply(
                            state,
                            content,
                            PowerId::SelfFormingClayPower,
                            pc,
                            Dec::from_i32(3),
                            Some(pc),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::Shuriken => {
            match slot {
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            set_int(state, id, "Shuriken._attacksThisTurn", 0);
                        }
                    }
                }
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(cp) = args {
                        if let Some(c) = state.card(cp.card) {
                            if content.card_meta(c.id)?.card_type == CardType::Attack {
                                let next = get_int(state, id, "Shuriken._attacksThisTurn", 0) + 1;
                                set_int(state, id, "Shuriken._attacksThisTurn", next);
                                if next % 3 == 0 {
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
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_int(state, id, "Shuriken._attacksThisTurn", 0);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::SilverCrucible => {
            if slot == HookSlot::AfterRoomEntered && state.static_ctx.room_type == RoomType::Treasure
            {
                let cur = get_int(
                    state,
                    id,
                    "SilverCrucible.<TreasureRoomsEntered>k__BackingField",
                    0,
                );
                set_int(
                    state,
                    id,
                    "SilverCrucible.<TreasureRoomsEntered>k__BackingField",
                    cur + 1,
                );
            }
            Ok(HookResult::default())
        }

        RelicId::SlingOfCourage => {
            if slot == HookSlot::AfterRoomEntered && state.static_ctx.room_type == RoomType::Elite {
                power::apply(
                    state,
                    content,
                    PowerId::StrengthPower,
                    pc,
                    Dec::TWO,
                    Some(pc),
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::SneckoEye => {
            match slot {
                HookSlot::BeforeCombatStart => {
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
                HookSlot::ModifyHandDraw => {
                    if let HookArgs::ModifyHandDraw { original_count } = args {
                        return Ok(HookResult {
                            dec_val: Some(original_count.checked_add(Dec::TWO)?),
                            ..Default::default()
                        });
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::SneckoSkull => {
            if slot == HookSlot::ModifyPowerAmountGivenAdditive {
                if let HookArgs::PowerChanged {
                    power_id, applier, ..
                } = args
                {
                    let delta = if *power_id == PowerId::PoisonPower && *applier == Some(pc) {
                        Dec::ONE
                    } else {
                        Dec::ZERO
                    };
                    return Ok(HookResult {
                        dec_val: Some(delta),
                        ..Default::default()
                    });
                }
            }
            Ok(HookResult::default())
        }

        RelicId::SongBow => {
            if slot == HookSlot::AfterRoomEntered && state.static_ctx.room_type.is_combat_room() {
                power::apply(
                    state,
                    content,
                    PowerId::AimPower,
                    pc,
                    Dec::from_i32(6),
                    Some(pc),
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::Sozu => {
            match slot {
                HookSlot::ShouldProcurePotion => {
                    return Ok(HookResult {
                        bool_val: Some(false),
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

        RelicId::SparklingRouge => {
            if slot == HookSlot::AfterBlockCleared {
                if let HookArgs::Creature(ch) = args {
                    if *ch == pc && state.player.turn_number == 3 {
                        power::apply(
                            state,
                            content,
                            PowerId::StrengthPower,
                            pc,
                            Dec::ONE,
                            Some(pc),
                            None,
                        )?;
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
            Ok(HookResult::default())
        }

        RelicId::SpikedGauntlets => {
            match slot {
                HookSlot::ModifyMaxEnergy => {
                    if let HookArgs::ModifyHandDraw { original_count } = args {
                        return Ok(HookResult {
                            dec_val: Some(original_count.checked_add(Dec::ONE)?),
                            ..Default::default()
                        });
                    }
                }
                HookSlot::TryModifyEnergyCostInCombat => {
                    if let HookArgs::CardCost { card, cost } = args {
                        if let Some(c) = state.card(*card) {
                            if content.card_meta(c.id)?.card_type == CardType::Power {
                                return Ok(HookResult {
                                    dec_val: Some(cost.checked_add(Dec::ONE)?),
                                    modified: true,
                                    ..Default::default()
                                });
                            }
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::StoneCalendar => {
            match slot {
                HookSlot::AfterRoomEntered => {
                    if state.static_ctx.room_type.is_combat_room() {
                        set_bool(state, id, "StoneCalendar._shouldTriggerThisTurn", false);
                    }
                }
                HookSlot::AfterSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        let should =
                            participants.contains(&pc) && state.player.turn_number == 7;
                        set_bool(state, id, "StoneCalendar._shouldTriggerThisTurn", should);
                    }
                }
                HookSlot::BeforeSideTurnEnd => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if get_bool(state, id, "StoneCalendar._shouldTriggerThisTurn", false)
                            && participants.contains(&pc)
                        {
                            set_bool(state, id, "StoneCalendar._shouldTriggerThisTurn", false);
                            let enemies = hittable_enemies(state, content)?;
                            damage::damage_targets(
                                state,
                                content,
                                &enemies,
                                Dec::from_i32(52),
                                ValueProp::UNPOWERED,
                                Some(pc),
                                None,
                                None,
                            )?;
                        }
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_bool(state, id, "StoneCalendar._shouldTriggerThisTurn", false);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::StoneCracker => {
            if slot == HookSlot::AfterRoomEntered && state.static_ctx.room_type.is_combat_room() {
                let mut candidates: Vec<CardHandle> = Vec::new();
                for &ch in &state.player.draw {
                    if let Some(c) = state.card(ch) {
                        if c.upgrade_level < content.card_meta(c.id)?.max_upgrade_level {
                            candidates.push(ch);
                        }
                    }
                }
                let mut rng = *state.run_rngs.get(RunRngType::CombatCardSelection);
                sts2_kernel::ordering::stable_shuffle_cards(
                    state,
                    content,
                    &mut candidates,
                    &mut rng,
                )?;
                *state.run_rngs.get_mut(RunRngType::CombatCardSelection) = rng;
                for ch in candidates.into_iter().take(2) {
                    card_cmd::upgrade(state, content, ch)?;
                }
            }
            Ok(HookResult::default())
        }

        RelicId::Storyteller => {
            match slot {
                HookSlot::AfterRoomEntered => {
                    if state.static_ctx.room_type.is_combat_room() {
                        power::apply(
                            state,
                            content,
                            PowerId::AimPower,
                            pc,
                            Dec::from_i32(12),
                            Some(pc),
                            None,
                        )?;
                    }
                }
                HookSlot::BeforeSideTurnEnd => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            power::apply(
                                state,
                                content,
                                PowerId::AimPower,
                                pc,
                                Dec::from_i32(3),
                                Some(pc),
                                None,
                            )?;
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::StrikeDummy => {
            if slot == HookSlot::ModifyDamageAdditive {
                if let HookArgs::ModifyDamage {
                    props, card_source, ..
                } = args
                {
                    let is_strike = card_source
                        .and_then(|ch| state.card(ch))
                        .is_some_and(|c| {
                            generated_card_meta(c.id)
                                .base_tags
                                .contains(CardTags::STRIKE)
                        });
                    if is_strike && props.is_powered_attack() {
                        return Ok(HookResult {
                            dec_val: Some(Dec::from_i32(3)),
                            ..Default::default()
                        });
                    }
                    return Ok(HookResult {
                        dec_val: Some(Dec::ZERO),
                        ..Default::default()
                    });
                }
            }
            Ok(HookResult::default())
        }

        RelicId::SturdyClamp => {
            match slot {
                HookSlot::ShouldClearBlock => {
                    if let HookArgs::Creature(ch) = args {
                        return Ok(HookResult {
                            bool_val: Some(*ch != pc),
                            ..Default::default()
                        });
                    }
                }
                HookSlot::AfterPreventingBlockClear => {
                    if let HookArgs::Creature(ch) = args {
                        if *ch == pc {
                            let cur_block = state.creature(pc).map_or(0, |c| c.block);
                            if cur_block > 10 {
                                block::lose_block(
                                    state,
                                    content,
                                    pc,
                                    Dec::from_i32(cur_block - 10),
                                )?;
                            }
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::SwordOfJade => {
            if slot == HookSlot::AfterRoomEntered && state.static_ctx.room_type.is_combat_room() {
                power::apply(
                    state,
                    content,
                    PowerId::StrengthPower,
                    pc,
                    Dec::from_i32(3),
                    Some(pc),
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::SwordOfStone => {
            if slot == HookSlot::AfterCombatVictory
                && state.static_ctx.room_type == RoomType::Elite
            {
                let next = get_int(state, id, "SwordOfStone._eliteVictories", 0) + 1;
                set_int(state, id, "SwordOfStone._eliteVictories", next);
                if next >= 5 {
                    if let Some(r) = find_relic_mut(state, id) {
                        *r = RelicInst {
                            id: RelicId::SwordOfJade,
                            is_wax: false,
                            is_melted: false,
                            stack_count: 1,
                            floor_added_to_deck: None,
                            private_fields: Default::default(),
                        };
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::SymbioticVirus => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.turn_number == 1 {
                        orb::channel(state, content, OrbId::DarkOrb)?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::TeaOfDiscourtesy => {
            if slot == HookSlot::BeforeCombatStart {
                let left = get_int(state, id, "TeaOfDiscourtesy._combatsLeft", 1);
                if left > 0 {
                    for _ in 0..2 {
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
                    set_int(state, id, "TeaOfDiscourtesy._combatsLeft", left - 1);
                }
            }
            Ok(HookResult::default())
        }

        RelicId::TheAbacus => {
            if slot == HookSlot::AfterShuffle {
                block::gain_block(
                    state,
                    content,
                    pc,
                    Dec::from_i32(6),
                    ValueProp::UNPOWERED,
                    None,
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::TheBoot => {
            if slot == HookSlot::ModifyHpLostAfterOstyLate {
                if let HookArgs::ModifyHpLost {
                    target,
                    amount,
                    props,
                    dealer,
                    ..
                } = args
                {
                    let osty = state.osty_handle();
                    let dealer_ok = *dealer == Some(pc) || (osty.is_some() && *dealer == osty);
                    if dealer_ok
                        && *target != pc
                        && props.is_powered_attack()
                        && *amount >= Dec::ONE
                        && *amount < Dec::from_i32(5)
                    {
                        return Ok(HookResult {
                            dec_val: Some(Dec::from_i32(5)),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::ThrowingAxe => {
            match slot {
                HookSlot::AfterRoomEntered => {
                    set_bool(state, id, "ThrowingAxe._used", false);
                }
                HookSlot::ModifyCardPlayCount => {
                    if let HookArgs::CardPlayCount { play_count, .. } = args {
                        if !get_bool(state, id, "ThrowingAxe._used", false) {
                            return Ok(HookResult {
                                int_val: Some(play_count + 1),
                                ..Default::default()
                            });
                        }
                    }
                }
                HookSlot::AfterModifyingCardPlayCount => {
                    set_bool(state, id, "ThrowingAxe._used", true);
                }
                HookSlot::AfterCombatEnd => {
                    set_bool(state, id, "ThrowingAxe._used", false);
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::Thumbscrew => {
            if slot == HookSlot::ModifyAttackHitCount {
                if let HookArgs::Attack { info, .. } = args {
                    if info.hit_count > 1 {
                        if let Some(cs) = info.card_source {
                            if let Some(c) = state.card(cs) {
                                if content.card_meta(c.id)?.card_type == CardType::Attack {
                                    return Ok(HookResult {
                                        int_val: Some(info.hit_count + 1),
                                        ..Default::default()
                                    });
                                }
                            }
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::Tingsha => {
            if slot == HookSlot::AfterCardDiscarded && state.current_side == CombatSide::Player {
                let enemies = hittable_enemies(state, content)?;
                if let Some(&target) = state
                    .run_rngs
                    .get_mut(RunRngType::CombatTargets)
                    .next_item(&enemies)
                {
                    damage::damage_targets(
                        state,
                        content,
                        &[target],
                        Dec::from_i32(3),
                        ValueProp::UNPOWERED,
                        Some(pc),
                        None,
                        None,
                    )?;
                }
            }
            Ok(HookResult::default())
        }

        RelicId::ToastyMittens => {
            if slot == HookSlot::AfterPlayerTurnStart {
                let hand: SmallVec<[CardHandle; 16]> = state.player.hand.iter().copied().collect();
                let mut empty_q = SmallVec::new();
                match card_select::select_cards(
                    state,
                    &hand,
                    1,
                    1,
                    ChoiceTiming::PlayerTurnStart,
                    "RELIC.TOASTY_MITTENS",
                    PileType::Hand,
                    false,
                    &mut empty_q,
                )? {
                    card_select::SelectResult::NeedsChoice(req) => {
                        return Ok(HookResult {
                            needs_choice: Some(req),
                            ..Default::default()
                        });
                    }
                    card_select::SelectResult::Selected(selected) => {
                        for c in selected {
                            card_pile::exhaust(state, content, c, false)?;
                        }
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

        RelicId::Toolbox => {
            match slot {
                HookSlot::BeforeCombatStart => {
                    set_bool(state, id, "Toolbox._wasUsedThisCombat", false);
                    remove_field(state, id, "Toolbox._pendingCandidates");
                }
                HookSlot::BeforeHandDraw => {
                    if !get_bool(state, id, "Toolbox._wasUsedThisCombat", false)
                        && state.player.turn_number == 1
                    {
                        set_bool(state, id, "Toolbox._wasUsedThisCombat", true);
                        let pool = card_factory::colorless_card_pool_candidates(state);
                        let picked = card_factory::get_distinct_for_combat(
                            state,
                            &pool,
                            3,
                            RunRngType::CombatCardGeneration,
                        );
                        let mut handles: SmallVec<[CardHandle; 3]> = SmallVec::new();
                        for cid in picked {
                            handles.push(card_pile::create_card_in_combat(
                                state, content, cid, 0,
                            )?);
                        }
                        let mut empty_q = SmallVec::new();
                        match card_select::select_cards(
                            state,
                            &handles,
                            1,
                            1,
                            ChoiceTiming::PlayerTurnStart,
                            "RELIC.TOOLBOX",
                            PileType::None,
                            false,
                            &mut empty_q,
                        )? {
                            card_select::SelectResult::NeedsChoice(req) => {
                                let raw: Vec<i32> =
                                    handles.iter().map(|h| h.0 as i32).collect();
                                if let Some(r) = find_relic_mut(state, id) {
                                    r.private_fields.insert(
                                        "Toolbox._pendingCandidates",
                                        FieldValue::CardHandleArray(raw),
                                    );
                                }
                                return Ok(HookResult {
                                    needs_choice: Some(req),
                                    ..Default::default()
                                });
                            }
                            card_select::SelectResult::Selected(selected) => {
                                for &h in &handles {
                                    if !selected.contains(&h) {
                                        if let Some(c) = state.card_mut(h) {
                                            c.removed_from_state = true;
                                        }
                                        state.invalidate_hook_cache();
                                    }
                                }
                                for h in selected {
                                    generate_existing_card_into_pile(
                                        content,
                                        state,
                                        h,
                                        PileType::Hand,
                                        CardPilePosition::Bottom,
                                        Some(pc),
                                    )?;
                                }
                            }
                        }
                    }
                }
                HookSlot::AfterCombatEnd => {
                    set_bool(state, id, "Toolbox._wasUsedThisCombat", false);
                    remove_field(state, id, "Toolbox._pendingCandidates");
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::TormentousCollar => {
            if slot == HookSlot::BeforeSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if state.static_ctx.room_type == RoomType::Elite
                        && participants.contains(&pc)
                        && state.player.turn_number <= 1
                    {
                        for eh in hittable_enemies(state, content)? {
                            power::apply(
                                state,
                                content,
                                PowerId::WeakPower,
                                eh,
                                Dec::TWO,
                                Some(pc),
                                None,
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::ToughBandages => {
            if slot == HookSlot::AfterCardDiscarded && state.current_side == CombatSide::Player {
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
            Ok(HookResult::default())
        }

        RelicId::ToyBox => {
            if slot == HookSlot::AfterCombatEnd {
                let seen = get_int(state, id, "ToyBox.<CombatsSeen>k__BackingField", 0);
                if seen < 15 {
                    let next = seen + 1;
                    set_int(state, id, "ToyBox.<CombatsSeen>k__BackingField", next);
                    if next % 3 == 0 {
                        if let Some(wax) = state
                            .player
                            .relics
                            .iter_mut()
                            .find(|r| r.is_wax && !r.is_melted)
                        {
                            wax.is_melted = true;
                            state.invalidate_hook_cache();
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::TungstenRod => {
            if slot == HookSlot::ModifyHpLostAfterOsty {
                if let HookArgs::ModifyHpLost { target, amount, .. } = args {
                    if *target == pc {
                        return Ok(HookResult {
                            dec_val: Some(amount.checked_sub(Dec::ONE)?.max(Dec::ZERO)),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::TuningFork => {
            if slot == HookSlot::AfterCardPlayed {
                if let HookArgs::CardPlay(cp) = args {
                    if let Some(c) = state.card(cp.card) {
                        if content.card_meta(c.id)?.card_type == CardType::Skill {
                            let next = get_int(state, id, "TuningFork._skillsPlayed", 0) + 1;
                            if next < 10 {
                                set_int(state, id, "TuningFork._skillsPlayed", next);
                            } else {
                                set_int(state, id, "TuningFork._skillsPlayed", next % 10);
                                block::gain_block(
                                    state,
                                    content,
                                    pc,
                                    Dec::from_i32(7),
                                    ValueProp::UNPOWERED,
                                    None,
                                    Some(*cp),
                                )?;
                            }
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::Twine => {
            if slot == HookSlot::AfterArrowDislodged {
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

        RelicId::TwistedFunnel => {
            if slot == HookSlot::BeforeSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.turn_number <= 1 {
                        for eh in hittable_enemies(state, content)? {
                            power::apply(
                                state,
                                content,
                                PowerId::PoisonPower,
                                eh,
                                Dec::from_i32(4),
                                Some(pc),
                                None,
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::UnceasingTop => {
            if slot == HookSlot::AfterHandEmptied
                && matches!(
                    state.player.phase,
                    PlayerTurnPhase::AutoPrePlay
                        | PlayerTurnPhase::Play
                        | PlayerTurnPhase::AutoPostPlay
                )
            {
                let _ = card_pile::draw(state, content, 1, false)?;
            }
            Ok(HookResult::default())
        }

        RelicId::UndyingSigil => {
            if slot == HookSlot::ModifyDamageMultiplicative {
                if let HookArgs::ModifyDamage {
                    target,
                    props,
                    dealer,
                    ..
                } = args
                {
                    if let Some(d) = *dealer {
                        if props.is_powered_attack() && *target == Some(pc) && d != pc {
                            if let Some(dc) = state.creature(d) {
                                let doom = dc
                                    .powers
                                    .iter()
                                    .find(|p| p.id == PowerId::DoomPower)
                                    .map_or(0, |p| p.amount);
                                if dc.current_hp <= doom {
                                    return Ok(HookResult {
                                        dec_val: Some(Dec::HALF),
                                        ..Default::default()
                                    });
                                }
                            }
                        }
                    }
                    return Ok(HookResult {
                        dec_val: Some(Dec::ONE),
                        ..Default::default()
                    });
                }
            }
            Ok(HookResult::default())
        }

        RelicId::UnsettlingLamp => {
            match slot {
                HookSlot::BeforeCombatStart | HookSlot::AfterCombatEnd => {
                    set_bool(state, id, "UnsettlingLamp._finished", false);
                    remove_field(state, id, "UnsettlingLamp._triggeringCard");
                    remove_field(
                        state,
                        id,
                        "UnsettlingLamp._doubledTemporaryInternalPowerType",
                    );
                }
                HookSlot::BeforePowerAmountChanged => {
                    if let HookArgs::PowerChanged {
                        power_id,
                        owner,
                        amount,
                        applier,
                        card_source,
                        ..
                    } = args
                    {
                        let finished = get_bool(state, id, "UnsettlingLamp._finished", false);
                        let triggering =
                            get_card_handle(state, id, "UnsettlingLamp._triggeringCard");
                        if !finished
                            && triggering.is_none()
                            && card_source.is_some()
                            && *applier == Some(pc)
                            && state.creature(*owner).is_some_and(|c| {
                                c.side != CombatSide::Player
                                    && !c.powers.iter().any(|p| p.id == PowerId::ArtifactPower)
                            })
                            && power_type_for_amount(content, *power_id, *amount)?
                                == PowerType::Debuff
                        {
                            set_card_handle(
                                state,
                                id,
                                "UnsettlingLamp._triggeringCard",
                                *card_source,
                            );
                            let internal =
                                temporary_internal_power(*power_id).map(|p| p.model_id_str());
                            set_string(
                                state,
                                id,
                                "UnsettlingLamp._doubledTemporaryInternalPowerType",
                                internal,
                            );
                        }
                    }
                }
                HookSlot::ModifyPowerAmountGivenMultiplicative => {
                    if let HookArgs::PowerChanged {
                        power_id,
                        amount,
                        card_source,
                        ..
                    } = args
                    {
                        let finished = get_bool(state, id, "UnsettlingLamp._finished", false);
                        let triggering =
                            get_card_handle(state, id, "UnsettlingLamp._triggeringCard");
                        let doubled_internal = get_string(
                            state,
                            id,
                            "UnsettlingLamp._doubledTemporaryInternalPowerType",
                        );
                        let factor = if !finished
                            && triggering.is_some()
                            && triggering == *card_source
                            && doubled_internal != Some(power_id.model_id_str())
                            && power_type_for_amount(content, *power_id, *amount)?
                                == PowerType::Debuff
                        {
                            Dec::TWO
                        } else {
                            Dec::ONE
                        };
                        return Ok(HookResult {
                            dec_val: Some(factor),
                            ..Default::default()
                        });
                    }
                }
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(cp) = args {
                        let finished = get_bool(state, id, "UnsettlingLamp._finished", false);
                        let triggering =
                            get_card_handle(state, id, "UnsettlingLamp._triggeringCard");
                        if !finished && triggering == Some(cp.card) {
                            set_bool(state, id, "UnsettlingLamp._finished", true);
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::Vajra => {
            if slot == HookSlot::AfterRoomEntered && state.static_ctx.room_type.is_combat_room() {
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
            Ok(HookResult::default())
        }

        RelicId::Vambrace => {
            match slot {
                HookSlot::BeforeCombatStart | HookSlot::AfterCombatEnd => {
                    set_bool(state, id, "Vambrace._blockGainedThisCombat", false);
                    remove_field(state, id, "Vambrace._triggeringCard");
                }
                HookSlot::ModifyBlockMultiplicative => {
                    if let HookArgs::ModifyBlock {
                        props, card_source, ..
                    } = args
                    {
                        let triggering = get_card_handle(state, id, "Vambrace._triggeringCard");
                        let block_gained =
                            get_bool(state, id, "Vambrace._blockGainedThisCombat", false);
                        let factor = if props.contains(ValueProp::MOVE)
                            && card_source.is_some()
                            && (triggering.is_none() || triggering == *card_source)
                            && !block_gained
                        {
                            Dec::TWO
                        } else {
                            Dec::ONE
                        };
                        return Ok(HookResult {
                            dec_val: Some(factor),
                            ..Default::default()
                        });
                    }
                }
                HookSlot::AfterModifyingBlockAmount => match args {
                    HookArgs::ModifyBlock {
                        amount,
                        card_source,
                        ..
                    } => {
                        if *amount > Dec::ZERO {
                            if let Some(cs) = *card_source {
                                set_card_handle(
                                    state,
                                    id,
                                    "Vambrace._triggeringCard",
                                    Some(cs),
                                );
                            }
                        }
                    }
                    _ => {}
                },
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(cp) = args {
                        let triggering = get_card_handle(state, id, "Vambrace._triggeringCard");
                        let block_gained =
                            get_bool(state, id, "Vambrace._blockGainedThisCombat", false);
                        if triggering == Some(cp.card) && !block_gained {
                            set_bool(state, id, "Vambrace._blockGainedThisCombat", true);
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::VelvetChoker => {
            match slot {
                HookSlot::ModifyMaxEnergy => {
                    if let HookArgs::ModifyHandDraw { original_count } = args {
                        return Ok(HookResult {
                            dec_val: Some(original_count.checked_add(Dec::ONE)?),
                            ..Default::default()
                        });
                    }
                }
                HookSlot::ShouldPlay => {
                    return Ok(HookResult {
                        bool_val: Some(state.player.counters.card_plays_started_this_turn < 6),
                        ..Default::default()
                    });
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::VenerableTeaSet => {
            match slot {
                HookSlot::AfterRoomEntered => {
                    if state.static_ctx.room_type == RoomType::RestSite {
                        set_bool(state, id, "VenerableTeaSet._isArmed", true);
                    }
                }
                HookSlot::AfterEnergyReset => {
                    if get_bool(state, id, "VenerableTeaSet._isArmed", false) {
                        set_bool(state, id, "VenerableTeaSet._isArmed", false);
                        gold::gain_energy(state, Dec::TWO)?;
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::VeryHotCocoa => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&pc) && state.player.turn_number == 1 {
                        gold::gain_energy(state, Dec::from_i32(4))?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        RelicId::VexingPuzzlebox => {
            if slot == HookSlot::AfterPlayerTurnStart && state.player.turn_number == 1 {
                let pool = card_factory::character_combat_pool(state);
                let picked = card_factory::get_distinct_for_combat(
                    state,
                    &pool,
                    1,
                    RunRngType::CombatCardGeneration,
                );
                if let Some(&cid) = picked.first() {
                    let h = card_pile::create_card_in_combat(state, content, cid, 0)?;
                    set_to_free_this_turn(state, h);
                    generate_existing_card_into_pile(
                        content,
                        state,
                        h,
                        PileType::Hand,
                        CardPilePosition::Bottom,
                        Some(pc),
                    )?;
                }
            }
            Ok(HookResult::default())
        }

        RelicId::VitruvianMinion => {
            match slot {
                HookSlot::ModifyDamageMultiplicative => {
                    if let HookArgs::ModifyDamage { card_source, .. } = args {
                        let card = card_source.and_then(|h| state.card(h));
                        return Ok(HookResult {
                            dec_val: Some(if is_owned_minion(content, card)? {
                                Dec::TWO
                            } else {
                                Dec::ONE
                            }),
                            ..Default::default()
                        });
                    }
                }
                HookSlot::ModifyBlockMultiplicative => {
                    if let HookArgs::ModifyBlock { card_source, .. } = args {
                        let card = card_source.and_then(|h| state.card(h));
                        return Ok(HookResult {
                            dec_val: Some(if is_owned_minion(content, card)? {
                                Dec::TWO
                            } else {
                                Dec::ONE
                            }),
                            ..Default::default()
                        });
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::WarHammer => {
            if slot == HookSlot::AfterCombatVictory
                && state.static_ctx.room_type == RoomType::Elite
            {
                let mut candidates: Vec<CardHandle> = Vec::new();
                for &ch in &state.player.deck {
                    if let Some(c) = state.card(ch) {
                        if c.upgrade_level < content.card_meta(c.id)?.max_upgrade_level {
                            candidates.push(ch);
                        }
                    }
                }
                let mut rng = *state.player_rngs.get(PlayerRngType::NonCombatGeneration);
                sts2_kernel::ordering::stable_shuffle_cards(
                    state,
                    content,
                    &mut candidates,
                    &mut rng,
                )?;
                *state
                    .player_rngs
                    .get_mut(PlayerRngType::NonCombatGeneration) = rng;
                for ch in candidates.into_iter().take(4) {
                    card_cmd::upgrade(state, content, ch)?;
                }
            }
            Ok(HookResult::default())
        }

        RelicId::WhisperingEarring => {
            match slot {
                HookSlot::ModifyMaxEnergy => {
                    if let HookArgs::ModifyHandDraw { original_count } = args {
                        return Ok(HookResult {
                            dec_val: Some(original_count.checked_add(Dec::ONE)?),
                            ..Default::default()
                        });
                    }
                }
                HookSlot::AfterAutoPrePlayPhaseEnteredLate => {
                    whispering_earring_after_auto_pre_play_phase_entered_late(content, state)?;
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

        RelicId::WhittlingKnife => {
            if slot == HookSlot::AfterShuffle {
                power::apply(
                    state,
                    content,
                    PowerId::AimPower,
                    pc,
                    Dec::from_i32(5),
                    Some(pc),
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        RelicId::WongosMysteryTicket => {
            if slot == HookSlot::AfterCombatEnd
                && !get_bool(state, id, "WongosMysteryTicket._gaveRelics", false)
            {
                let cur = get_int(state, id, "WongosMysteryTicket._combatsFinished", 0);
                set_int(
                    state,
                    id,
                    "WongosMysteryTicket._combatsFinished",
                    cur + 1,
                );
            }
            Ok(HookResult::default())
        }

        RelicId::Wuppy => {
            match slot {
                HookSlot::BeforeCombatStart => {
                    let saved_max = get_int(state, id, "Wuppy._maxHp", 0);
                    let max_hp = if saved_max > 0 { saved_max } else { 60 };
                    let hp_set = get_bool(state, id, "Wuppy._currentHpSet", false);
                    let cur_hp = if hp_set {
                        get_int(state, id, "Wuppy._currentHp", max_hp)
                    } else {
                        max_hp
                    };
                    set_int(state, id, "Wuppy._maxHp", max_hp);
                    set_int(state, id, "Wuppy._currentHp", cur_hp);
                    set_bool(state, id, "Wuppy._currentHpSet", true);

                    let h = creature_cmd::create_and_add_monster(
                        state,
                        content,
                        MonsterId::Wuppy,
                        CombatSide::Player,
                        None,
                    )?;
                    if !state.pet_handles.contains(&h) {
                        state.pet_handles.push(h);
                    }
                    power::apply(
                        state,
                        content,
                        PowerId::WuppysLoyaltyPower,
                        h,
                        Dec::ONE,
                        None,
                        None,
                    )?;
                    creature_cmd::set_max_hp(state, h, Dec::from_i32(max_hp))?;
                    creature_cmd::set_current_hp(state, content, h, Dec::from_i32(cur_hp))?;
                }
                HookSlot::AfterCurrentHpChanged => {
                    if let HookArgs::ModifyHpLost { target, .. } = args {
                        if let Some(c) = state.creature(*target) {
                            if c.is_pet() && c.monster_id == Some(MonsterId::Wuppy) {
                                let hp = c.current_hp;
                                set_int(state, id, "Wuppy._currentHp", hp);
                                set_bool(state, id, "Wuppy._currentHpSet", true);
                            }
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }

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
    let pc = state.player.creature_handle;
    match (id, slot) {
        (RelicId::ToastyMittens, HookSlot::AfterPlayerTurnStart) => {
            for &c in selected {
                card_pile::exhaust(state, content, c, false)?;
            }
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                pc,
                Dec::ONE,
                Some(pc),
                None,
            )?;
            Ok(HookResult::default())
        }
        (RelicId::Toolbox, HookSlot::BeforeHandDraw) => {
            let candidates: Vec<CardHandle> = find_relic(state, id)
                .and_then(|r| r.private_fields.get("Toolbox._pendingCandidates"))
                .and_then(|v| match v {
                    FieldValue::CardHandleArray(arr) | FieldValue::IntArray(arr) => Some(
                        arr.iter()
                            .filter_map(|&raw| u32::try_from(raw).ok().map(CardHandle))
                            .collect(),
                    ),
                    _ => None,
                })
                .unwrap_or_default();
            remove_field(state, id, "Toolbox._pendingCandidates");
            for h in candidates {
                if !selected.contains(&h) {
                    if let Some(c) = state.card_mut(h) {
                        c.removed_from_state = true;
                    }
                    state.invalidate_hook_cache();
                }
            }
            for &h in selected {
                generate_existing_card_into_pile(
                    content,
                    state,
                    h,
                    PileType::Hand,
                    CardPilePosition::Bottom,
                    Some(pc),
                )?;
            }
            Ok(HookResult::default())
        }
        _ => Err(KernelError::Unsupported {
            kind: "RelicHookChoiceResume",
            id: format!("{}:{slot:?}", id.model_id_str()),
        }),
    }
}
