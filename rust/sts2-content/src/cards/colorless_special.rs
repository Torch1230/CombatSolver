use smallvec::SmallVec;
use std::borrow::Cow;
use std::sync::LazyLock;
use sts2_kernel::commands::card_select::SelectResult;
use sts2_kernel::commands::{
    arrow, autoplay, block, card as card_cmd, card_factory, card_pile, card_select,
    creature as creature_cmd, damage, damage::AttackBuilder, damage::AttackTargetSpec, gold, orb,
    power,
};
use sts2_kernel::content_dispatch::{AttackContextInfo, CardPlayContext, CardPlayOutcome};
use sts2_kernel::{
    BattleState, CardHandle, CardInst, CardTempFlags, ChoiceTiming, ContentDispatcher,
    CreatureHandle, DamageResult, FieldValue, HookArgs, HookResult, KernelError,
    LocalEnergyModifier, OptCardHandle, OptI16, PowerExtension, hooks, ordering,
};
use sts2_types::generated_ids::{CARD_META_TABLE, generated_card_meta};
use sts2_types::{
    CardId, CardKeywords, CardMeta, CardPartition, CardPilePosition, CardRarity, CardStaticFlags,
    CardType, CombatSide, Dec, HookSlot, LocalEnergyModifierKind, PileType, PotionId, PowerId,
    PowerStaticFlags, PowerType, RunRngType, TargetType, ValueProp, card_partition,
};

const STEP_DISCOVERY_SELECT: u8 = 1;
const STEP_SPLASH_SELECT: u8 = 2;
const STEP_DUAL_WIELD_SELECT: u8 = 3;
const STEP_PURITY_SELECT: u8 = 4;
const STEP_SECRET_TECHNIQUE_SELECT: u8 = 5;
const STEP_SECRET_WEAPON_SELECT: u8 = 6;
const STEP_SEEKER_STRIKE_SELECT: u8 = 7;
const STEP_NEOWS_FURY_SELECT: u8 = 8;
const STEP_THE_HERMIT_SELECT: u8 = 9;
const STEP_THINKING_AHEAD_SELECT: u8 = 10;
const STEP_WISH_SELECT: u8 = 11;

pub const COLORLESS_SPECIAL_IDS: &[CardId] = &[
    CardId::Abundance,
    CardId::Alchemize,
    CardId::Amnesia,
    CardId::Anointed,
    CardId::Apotheosis,
    CardId::Apparition,
    CardId::AscendersBane,
    CardId::Automation,
    CardId::BadLuck,
    CardId::BeaconOfHope,
    CardId::BeatDown,
    CardId::Beckon,
    CardId::BelieveInYou,
    CardId::Bolas,
    CardId::BrightestFlame,
    CardId::Burn,
    CardId::ByrdonisEgg,
    CardId::ByrdSwoop,
    CardId::Calamity,
    CardId::Caltrops,
    CardId::Catastrophe,
    CardId::Clash,
    CardId::Clumsy,
    CardId::Coordinate,
    CardId::CurseOfTheBell,
    CardId::DarkShackles,
    CardId::Dazed,
    CardId::Death,
    CardId::Debris,
    CardId::Debt,
    CardId::Decay,
    CardId::DingyDagger,
    CardId::Discovery,
    CardId::Disintegration,
    CardId::Distraction,
    CardId::Doubt,
    CardId::Dowsing,
    CardId::DramaticEntrance,
    CardId::DualWield,
    CardId::Enlightenment,
    CardId::Enthralled,
    CardId::Entrench,
    CardId::Entropy,
    CardId::Equilibrium,
    CardId::EternalArmor,
    CardId::Exterminate,
    CardId::Fasten,
    CardId::FeedingFrenzy,
    CardId::Finesse,
    CardId::Fisticuffs,
    CardId::FlashOfSteel,
    CardId::Folly,
    CardId::FranticEscape,
    CardId::Fuel,
    CardId::GangUp,
    CardId::GiantRock,
    CardId::GodByrd,
    CardId::GoldAxe,
    CardId::Greed,
    CardId::Guilty,
    CardId::HandOfGreed,
    CardId::HelloWorld,
    CardId::HiddenGem,
    CardId::HuddleUp,
    CardId::Impatience,
    CardId::Infection,
    CardId::Injury,
    CardId::Intercept,
    CardId::Jackpot,
    CardId::JackOfAllTrades,
    CardId::Joy,
    CardId::Judgement,
    CardId::Justice,
    CardId::Knockdown,
    CardId::LanternKey,
    CardId::Lift,
    CardId::Luminesce,
    CardId::MadScience,
    CardId::MasterOfStrategy,
    CardId::Maul,
    CardId::Mayhem,
    CardId::Metamorphosis,
    CardId::MightyShield,
    CardId::MightyWeapon,
    CardId::Mimic,
    CardId::MindBlast,
    CardId::MindRot,
    CardId::MinionDiveBomb,
    CardId::MinionSacrifice,
    CardId::MinionStrike,
    CardId::MyShinies,
    CardId::NeowsFury,
    CardId::Normality,
    CardId::Nostalgia,
    CardId::Omnislice,
    CardId::Outmaneuver,
    CardId::Panache,
    CardId::PanicButton,
    CardId::Peck,
    CardId::PoorSleep,
    CardId::PrepTime,
    CardId::Production,
    CardId::Prolong,
    CardId::Prowess,
    CardId::Purity,
    CardId::Rally,
    CardId::Rebound,
    CardId::Regret,
    CardId::Relax,
    CardId::Rend,
    CardId::Restlessness,
    CardId::RipAndTear,
    CardId::RollingBoulder,
    CardId::Sadness,
    CardId::Salvo,
    CardId::Scrawl,
    CardId::SecretTechnique,
    CardId::SecretWeapon,
    CardId::SeekerStrike,
    CardId::Shame,
    CardId::Shiv,
    CardId::Shockwave,
    CardId::Slimed,
    CardId::Sloth,
    CardId::Soot,
    CardId::Soul,
    CardId::SovereignBlade,
    CardId::Splash,
    CardId::SpoilsMap,
    CardId::SporeMind,
    CardId::Squash,
    CardId::Stack,
    CardId::Stratagem,
    CardId::Strength,
    CardId::SweepingGaze,
    CardId::TagTeam,
    CardId::Temperance,
    CardId::TheBall,
    CardId::TheBomb,
    CardId::TheChariot,
    CardId::TheDevil,
    CardId::TheEmperor,
    CardId::TheEmpress,
    CardId::TheFool,
    CardId::TheGambit,
    CardId::TheHangedMan,
    CardId::TheHermit,
    CardId::TheHierophant,
    CardId::TheHighPriestess,
    CardId::TheLovers,
    CardId::TheMagician,
    CardId::TheMoon,
    CardId::TheShot,
    CardId::TheStar,
    CardId::TheSun,
    CardId::TheTower,
    CardId::TheWorld,
    CardId::ThinkingAhead,
    CardId::ThrummingHatchet,
    CardId::ToricToughness,
    CardId::Toxic,
    CardId::UltimateDefend,
    CardId::UltimateStrike,
    CardId::Void,
    CardId::Volley,
    CardId::WasteAway,
    CardId::WheelOfFortune,
    CardId::Whistle,
    CardId::Wish,
    CardId::Wither,
    CardId::Wound,
    CardId::Writhe,
];

static COLORLESS_SPECIAL_META: LazyLock<[CardMeta; CardId::COUNT]> = LazyLock::new(|| {
    let mut table = CARD_META_TABLE;
    for &id in COLORLESS_SPECIAL_IDS {
        let meta = &mut table[id as usize];
        if is_turn_end_in_hand_card(id) {
            meta.flags |= CardStaticFlags::HAS_TURN_END_IN_HAND;
        }
        if is_growth_card(id) {
            meta.flags |= CardStaticFlags::HAS_GROWTH;
        }
        if is_rng_card(id) {
            meta.flags |= CardStaticFlags::USES_RNG;
        }
        if is_order_sensitive_card(id) {
            meta.flags |= CardStaticFlags::ORDER_SENSITIVE;
        }
    }
    table
});

const fn is_turn_end_in_hand_card(id: CardId) -> bool {
    matches!(
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
    )
}

const fn is_growth_card(id: CardId) -> bool {
    matches!(
        id,
        CardId::HandOfGreed | CardId::Alchemize | CardId::BrightestFlame
    )
}

const fn is_rng_card(id: CardId) -> bool {
    matches!(
        id,
        CardId::Abundance
            | CardId::Alchemize
            | CardId::Anointed
            | CardId::BeatDown
            | CardId::Catastrophe
            | CardId::Discovery
            | CardId::Distraction
            | CardId::HiddenGem
            | CardId::JackOfAllTrades
            | CardId::Jackpot
            | CardId::MadScience
            | CardId::Metamorphosis
            | CardId::MightyShield
            | CardId::MightyWeapon
            | CardId::RipAndTear
            | CardId::SeekerStrike
            | CardId::Splash
            | CardId::SweepingGaze
            | CardId::TheBall
            | CardId::TheFool
            | CardId::TheHierophant
            | CardId::TheLovers
            | CardId::TheMagician
            | CardId::TheWorld
            | CardId::Volley
    )
}

const fn is_order_sensitive_card(id: CardId) -> bool {
    matches!(
        id,
        CardId::Anointed
            | CardId::Apotheosis
            | CardId::Automation
            | CardId::Calamity
            | CardId::Clash
            | CardId::DualWield
            | CardId::Enlightenment
            | CardId::Entrench
            | CardId::Entropy
            | CardId::GoldAxe
            | CardId::HelloWorld
            | CardId::Impatience
            | CardId::Mayhem
            | CardId::Maul
            | CardId::MindBlast
            | CardId::Nostalgia
            | CardId::Panache
            | CardId::PrepTime
            | CardId::Prolong
            | CardId::Purity
            | CardId::Restlessness
            | CardId::RollingBoulder
            | CardId::Scrawl
            | CardId::Stack
            | CardId::Stratagem
            | CardId::TheDevil
            | CardId::TheLovers
            | CardId::TheMoon
            | CardId::ThinkingAhead
            | CardId::WheelOfFortune
    )
}

pub fn card_meta(id: CardId) -> Result<&'static CardMeta, KernelError> {
    if card_partition(id) == CardPartition::ColorlessSpecial {
        Ok(&COLORLESS_SPECIAL_META[id as usize])
    } else {
        Err(KernelError::unsupported("Card", id.model_id_str().to_owned()))
    }
}

pub fn is_playable(state: &BattleState, card: CardHandle) -> Result<bool, KernelError> {
    let ci = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?;
    let _ = card_meta(ci.id)?;
    match ci.id {
        CardId::Clash => Ok(state.player.hand.iter().all(|&h| {
            state
                .card(h)
                .is_some_and(|c| resolved_card_type(state, c) == CardType::Attack)
        })),
        _ => Ok(true),
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MadScienceRider {
    None,
    Sapping,
    Violence,
    Choking,
    Energized,
    Wisdom,
    Chaos,
    Expertise,
    Curious,
    Improvement,
}

impl MadScienceRider {
    fn from_str(s: &str) -> Self {
        match s {
            "Sapping" => Self::Sapping,
            "Violence" => Self::Violence,
            "Choking" => Self::Choking,
            "Energized" => Self::Energized,
            "Wisdom" => Self::Wisdom,
            "Chaos" => Self::Chaos,
            "Expertise" => Self::Expertise,
            "Curious" => Self::Curious,
            "Improvement" => Self::Improvement,
            _ => Self::None,
        }
    }
}

/// Pure resolved `CardType` helper for `MadScience` (awaiting `ContentDispatcher::card_resolved_type`).
pub fn mad_science_resolved_type(state: &BattleState, card: CardHandle) -> CardType {
    let Some(ci) = state.card(card) else {
        return CardType::Attack;
    };
    if let Some(ext) = state.card_ext(ci.ext_index) {
        if let Some(FieldValue::String(s)) = ext.private_fields.get("MadScience._type") {
            return match s.as_str() {
                "Skill" => CardType::Skill,
                "Power" => CardType::Power,
                _ => CardType::Attack,
            };
        }
        if let Some(FieldValue::Int(i)) = ext.private_fields.get("MadScience._type") {
            return match *i {
                2 => CardType::Skill,
                3 => CardType::Power,
                _ => CardType::Attack,
            };
        }
    }
    CardType::Attack
}

/// Pure resolved `TargetType` helper for `MadScience` (awaiting `ContentDispatcher::card_resolved_target_type`).
pub fn mad_science_resolved_target_type(state: &BattleState, card: CardHandle) -> TargetType {
    if mad_science_resolved_type(state, card) == CardType::Attack {
        TargetType::AnyEnemy
    } else {
        TargetType::SelfTarget
    }
}

fn resolved_card_type(state: &BattleState, card: &CardInst) -> CardType {
    if card.id == CardId::MadScience {
        mad_science_resolved_type(state, card.handle)
    } else {
        generated_card_meta(card.id).card_type
    }
}

/// Pure `TheTower` damage helper (awaiting `RootStaticContext.total_floor`).
pub const fn the_tower_damage(upg: bool, total_floor: i32) -> Dec {
    let per_floor = if upg { 2 } else { 1 };
    Dec::from_i32(per_floor * total_floor)
}

/// Pure `Omnislice` splash amount helper (`TotalDamage + OverkillDamage`).
pub fn omnislice_splash_amount(primary_results: &[DamageResult]) -> Dec {
    let Some(r) = primary_results.first() else {
        return Dec::ZERO;
    };
    Dec::from_i32(r.blocked_damage + r.unblocked_damage + r.overkill_damage)
}

/// Pure `TheDevil` draw-pile autoplay loop with optional per-card upgrade (awaiting `AutoPlayCmd::from_top_of_draw_pile_with_upgrade`).
pub fn the_devil_play_top_of_draw_with_upgrade(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    count: usize,
    upgrade_each: bool,
    choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
) -> Result<(), KernelError> {
    if !upgrade_each {
        let _ = autoplay::from_top_of_draw_pile(state, content, count, false, choice_queue)?;
        return Ok(());
    }
    for _ in 0..count {
        if hooks::is_over_or_ending(state) || state.player_creature().is_none_or(|c| c.is_dead()) {
            break;
        }
        card_pile::shuffle_if_necessary(state, content)?;
        if state.pending.is_some() {
            return Err(KernelError::unsupported(
                "KernelHook",
                "AutoPlayCmd::from_top_of_draw_pile_with_upgrade".to_owned(),
            ));
        }
        let Some(&top) = state.player.draw.first() else {
            break;
        };
        card_cmd::upgrade(state, content, top)?;
        let _ = autoplay::from_cards(state, content, &[top], None, false)?;
    }
    Ok(())
}

/// Pure `OnTurnEndInHand` semantic executor for all 11 turn-end-in-hand ColorlessSpecial cards
/// (awaiting `ContentDispatcher::card_on_turn_end_in_hand` / `CombatEngine::do_turn_end` integration).
pub fn on_turn_end_in_hand(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    card: CardHandle,
) -> Result<(), KernelError> {
    let ci = *state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing turn-end-in-hand card".to_owned()))?;
    let owner = state.player.creature_handle;
    match ci.id {
        CardId::BadLuck => {
            damage::damage_targets(
                state,
                content,
                &[owner],
                Dec::from_i32(13),
                ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED | ValueProp::MOVE,
                None,
                Some(card),
                None,
            )?;
        }
        CardId::Beckon => {
            damage::damage_targets(
                state,
                content,
                &[owner],
                Dec::from_i32(6),
                ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED | ValueProp::MOVE,
                None,
                Some(card),
                None,
            )?;
        }
        CardId::Burn | CardId::Decay => {
            damage::damage_targets(
                state,
                content,
                &[owner],
                Dec::from_i32(2),
                ValueProp::UNPOWERED | ValueProp::MOVE,
                None,
                Some(card),
                None,
            )?;
        }
        CardId::Debt => {
            let lose = state.player.gold.min(10).max(0);
            gold::lose_gold(state, Dec::from_i32(lose))?;
        }
        CardId::Doubt => {
            apply_turn_end_debuff_skip_tick(state, content, owner, card, PowerId::WeakPower)?;
        }
        CardId::Infection => {
            damage::damage_targets(
                state,
                content,
                &[owner],
                Dec::from_i32(3),
                ValueProp::UNPOWERED | ValueProp::MOVE,
                None,
                Some(card),
                None,
            )?;
        }
        CardId::Regret => {
            let cards_in_hand = get_card_int_field(state, card, "Regret._cardsInHand", 0);
            damage::damage_targets(
                state,
                content,
                &[owner],
                Dec::from_i32(cards_in_hand),
                ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED | ValueProp::MOVE,
                None,
                Some(card),
                None,
            )?;
            set_card_int_field(state, card, "Regret._cardsInHand", 0);
        }
        CardId::Shame => {
            apply_turn_end_debuff_skip_tick(state, content, owner, card, PowerId::FrailPower)?;
        }
        CardId::Toxic => {
            damage::damage_targets(
                state,
                content,
                &[owner],
                Dec::from_i32(5),
                ValueProp::UNPOWERED | ValueProp::MOVE,
                None,
                Some(card),
                None,
            )?;
        }
        CardId::Wither => {
            let fake_upg = get_card_int_field(state, card, "Wither._fakeUpgradeLevel", 0);
            let dmg = 3 + 3 * fake_upg;
            damage::damage_targets(
                state,
                content,
                &[owner],
                Dec::from_i32(dmg),
                ValueProp::UNPOWERED | ValueProp::MOVE,
                None,
                Some(card),
                None,
            )?;
        }
        _ => {
            return Err(KernelError::unsupported(
                "CardTurnEndInHand",
                ci.id.model_id_str().to_owned(),
            ));
        }
    }
    Ok(())
}

fn apply_turn_end_debuff_skip_tick(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    owner: CreatureHandle,
    card: CardHandle,
    power_id: PowerId,
) -> Result<(), KernelError> {
    let already_has = state
        .creature(owner)
        .is_some_and(|c| c.powers.iter().any(|p| p.id == power_id));
    if let Some(pidx) = power::apply(state, content, power_id, owner, Dec::ONE, None, Some(card))? {
        if !already_has {
            if let Some(p) = state
                .creature_mut(owner)
                .and_then(|c| c.powers.get_mut(pidx))
            {
                p.skip_next_duration_tick = true;
            }
        }
    }
    Ok(())
}

pub fn on_play(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    ctx: &CardPlayContext,
    choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
    _resume_step: u8,
) -> Result<CardPlayOutcome, KernelError> {
    let card = *state
        .card(ctx.card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?;
    let upg = card.upgrade_level > 0;
    let owner = state.player.creature_handle;
    let _ = card_meta(card.id)?;

    match card.id {
        CardId::Abundance => {
            let pool = card_factory::character_combat_pool(state);
            let chosen_ids = card_factory::get_distinct_for_combat_filtered(
                state,
                &pool,
                3,
                RunRngType::CombatCardGeneration,
                |cid| generated_card_meta(cid).card_type == CardType::Power,
            );
            let mut gen_handles = Vec::with_capacity(chosen_ids.len());
            for (idx, cid) in chosen_ids.into_iter().enumerate() {
                let h = card_pile::create_card_in_combat(state, content, cid, 1)?;
                gen_handles.push(h.0 as i32);
                if idx == 0 {
                    set_to_free_this_turn(state, h);
                    generate_existing_card_into_hand(content, state, h)?;
                } else if let Some(c) = state.card_mut(h) {
                    c.removed_from_state = true;
                    state.invalidate_hook_cache();
                }
            }
            if let Some(ext) = state.card_ext_mut(ctx.card) {
                ext.private_fields.insert(
                    Cow::Borrowed("Abundance._generatedCandidates"),
                    FieldValue::CardHandleArray(gen_handles),
                );
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Alchemize => {
            if let Some(pid) = roll_alchemize_potion(state) {
                if sts2_kernel::commands::potion::try_to_procure(state, content, pid)? {
                    state
                        .semantic_history
                        .record_growth(sts2_kernel::GrowthSource::Alchemize);
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Anointed => {
            let space = card_pile::MAX_CARDS_IN_HAND.saturating_sub(state.player.hand.len());
            let mut rare_cards: Vec<CardHandle> = state
                .player
                .draw
                .iter()
                .copied()
                .filter(|&h| {
                    state
                        .card(h)
                        .is_some_and(|c| generated_card_meta(c.id).rarity == CardRarity::Rare)
                })
                .collect();
            state
                .run_rngs
                .get_mut(RunRngType::CombatCardSelection)
                .unstable_shuffle(&mut rare_cards);
            rare_cards.truncate(space);
            for h in rare_cards {
                card_pile::add_sync(state, h, PileType::Hand, CardPilePosition::Bottom);
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Apotheosis => {
            let all_cards: Vec<CardHandle> = state
                .player
                .hand
                .iter()
                .chain(state.player.draw.iter())
                .chain(state.player.discard.iter())
                .chain(state.player.exhaust.iter())
                .chain(state.player.play.iter())
                .copied()
                .filter(|&h| h != ctx.card)
                .collect();
            for h in all_cards {
                let can_upg = state.card(h).is_some_and(|c| {
                    c.upgrade_level < content.card_meta(c.id).map_or(0, |m| m.max_upgrade_level)
                });
                if can_upg {
                    card_cmd::upgrade(state, content, h)?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Apparition => {
            power::apply(
                state,
                content,
                PowerId::IntangiblePower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Automation => {
            power::apply(
                state,
                content,
                PowerId::AutomationPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::BeaconOfHope => {
            power::apply(
                state,
                content,
                PowerId::BeaconOfHopePower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::BeatDown => {
            let count = if upg { 4 } else { 3 };
            let mut candidates: SmallVec<[CardHandle; 64]> = SmallVec::new();
            let discard: SmallVec<[CardHandle; 64]> = SmallVec::from_slice(&state.player.discard);
            for h in discard {
                if let Some(c) = state.card(h) {
                    if resolved_card_type(state, c) == CardType::Attack
                        && !card_pile::card_has_keyword(state, content, h, CardKeywords::UNPLAYABLE)?
                    {
                        candidates.push(h);
                    }
                }
            }
            let mut sel_rng = *state.run_rngs.get(RunRngType::CombatCardSelection);
            ordering::stable_shuffle_cards(state, content, &mut candidates, &mut sel_rng)?;
            *state.run_rngs.get_mut(RunRngType::CombatCardSelection) = sel_rng;

            for ch in candidates.into_iter().take(count) {
                if hooks::is_over_or_ending(state) {
                    break;
                }
                let target_type = state
                    .card(ch)
                    .map(|c| content.card_meta(c.id).map(|m| m.target_type))
                    .transpose()?
                    .unwrap_or(TargetType::None);
                let mut explicit_target = None;
                if target_type == TargetType::AnyEnemy {
                    let hittable = collect_hittable_enemies(state, content)?;
                    explicit_target = state
                        .run_rngs
                        .get_mut(RunRngType::CombatTargets)
                        .next_item(&hittable)
                        .copied();
                }
                if state.player_creature().is_none_or(|c| c.is_dead()) {
                    continue;
                }
                let blocked = card_pile::card_has_keyword(state, content, ch, CardKeywords::UNPLAYABLE)?
                    || !hooks::should_play(state, content, ch)?
                    || (target_type == TargetType::AnyEnemy && explicit_target.is_none());
                if blocked {
                    move_card_to_result_pile_without_playing_fallback(state, content, ch)?;
                    continue;
                }
                let _ = autoplay::from_cards(state, content, &[ch], explicit_target, false)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::BelieveInYou => {
            let target = ctx.target.unwrap_or(owner);
            if state.creature(target).is_some_and(|c| c.is_player) {
                gold::gain_energy(state, content, Dec::from_i32(if upg { 3 } else { 2 }))?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Bolas => {
            let target = require_target(ctx, "Bolas")?;
            let dmg = get_card_dec_field(
                state,
                ctx.card,
                "Bolas._damage",
                Dec::from_i32(if upg { 4 } else { 3 }),
            );
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let turn = state.player.turn_number;
            if let Some(ext) = state.card_ext_mut(ctx.card) {
                ext.private_fields.insert(
                    Cow::Borrowed("Bolas._playedOnTurnNumber"),
                    FieldValue::NullableInt(Some(turn)),
                );
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::BrightestFlame => {
            let amt = if upg { 3 } else { 2 };
            gold::gain_energy(state, content, Dec::from_i32(amt))?;
            let _ = card_pile::draw(state, content, amt, false)?;
            lose_max_hp(state, content, owner, Dec::from_i32(2))?;
            state.semantic_history.record_brightest_flame_max_hp_loss(2);
            Ok(CardPlayOutcome::default())
        }
        CardId::ByrdSwoop => {
            let target = require_target(ctx, "ByrdSwoop")?;
            let dmg = Dec::from_i32(if upg { 18 } else { 14 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Calamity => {
            power::apply(
                state,
                content,
                PowerId::CalamityPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Caltrops => {
            power::apply(
                state,
                content,
                PowerId::ThornsPower,
                owner,
                Dec::from_i32(if upg { 5 } else { 3 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Catastrophe => {
            let count = if upg { 3 } else { 2 };
            for _ in 0..count {
                let mut candidates: SmallVec<[CardHandle; 64]> = SmallVec::new();
                let draw: SmallVec<[CardHandle; 64]> = SmallVec::from_slice(&state.player.draw);
                for h in draw {
                    if !card_pile::card_has_keyword(state, content, h, CardKeywords::UNPLAYABLE)? {
                        candidates.push(h);
                    }
                }
                let mut sel_rng = *state.run_rngs.get(RunRngType::CombatCardSelection);
                ordering::stable_shuffle_cards(state, content, &mut candidates, &mut sel_rng)?;
                let picked = if let Some(&first) = candidates.first() {
                    Some(first)
                } else {
                    let mut fallback: SmallVec<[CardHandle; 64]> =
                        SmallVec::from_slice(&state.player.draw);
                    ordering::stable_shuffle_cards(state, content, &mut fallback, &mut sel_rng)?;
                    fallback.first().copied()
                };
                *state.run_rngs.get_mut(RunRngType::CombatCardSelection) = sel_rng;
                let Some(picked_h) = picked else {
                    continue;
                };
                if hooks::is_over_or_ending(state)
                    || state.player_creature().is_none_or(|c| c.is_dead())
                {
                    continue;
                }
                let _ = autoplay::from_cards(state, content, &[picked_h], None, false)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Clash => {
            let target = require_target(ctx, "Clash")?;
            let dmg = Dec::from_i32(if upg { 18 } else { 14 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Coordinate => {
            let target = ctx.target.unwrap_or(owner);
            power::apply(
                state,
                content,
                PowerId::CoordinatePower,
                target,
                Dec::from_i32(if upg { 8 } else { 5 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DarkShackles => {
            let target = require_target(ctx, "DarkShackles")?;
            power::apply(
                state,
                content,
                PowerId::DarkShacklesPower,
                target,
                Dec::from_i32(if upg { 15 } else { 9 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Death => {
            let target = require_target(ctx, "Death")?;
            power::apply(
                state,
                content,
                PowerId::DoomPower,
                target,
                Dec::from_i32(if upg { 81 } else { 66 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DingyDagger => {
            let target = require_target(ctx, "DingyDagger")?;
            let dmg = Dec::from_i32(dingy_dagger_damage(card.upgrade_level));
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Discovery => {
            let pool = card_factory::character_combat_pool(state);
            let chosen_ids = card_factory::get_distinct_for_combat(
                state,
                &pool,
                3,
                RunRngType::CombatCardGeneration,
            );
            let cands: SmallVec<[CardHandle; 16]> = chosen_ids
                .into_iter()
                .map(|cid| create_offer_card_instance(state, cid, 0, CardTempFlags::DISCOVERY_OFFER))
                .collect();
            match card_select::select_cards(
                state,
                &cands,
                0,
                1,
                ChoiceTiming::Action,
                "CARD.DISCOVERY",
                PileType::None,
                true,
                choice_queue,
            )? {
                SelectResult::Selected(sel) => {
                    finish_offer_choice(content, state, &cands, &sel, CardTempFlags::DISCOVERY_OFFER)?;
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => {
                    Ok(CardPlayOutcome::with_choice(req, STEP_DISCOVERY_SELECT))
                }
            }
        }
        CardId::Distraction => {
            let pool = card_factory::character_combat_pool(state);
            let chosen = card_factory::get_distinct_for_combat_filtered(
                state,
                &pool,
                1,
                RunRngType::CombatCardGeneration,
                |cid| generated_card_meta(cid).card_type == CardType::Skill,
            );
            if let Some(&cid) = chosen.first() {
                let h = card_pile::create_card_in_combat(state, content, cid, 0)?;
                set_to_free_this_turn(state, h);
                generate_existing_card_into_hand(content, state, h)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::DramaticEntrance => {
            let dmg = Dec::from_i32(if upg { 15 } else { 11 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DualWield => {
            if hooks::is_over_or_ending(state) || state.player_creature().is_none_or(|c| c.is_dead())
            {
                return Ok(CardPlayOutcome::default());
            }
            let cands: SmallVec<[CardHandle; 16]> = state
                .player
                .hand
                .iter()
                .copied()
                .filter(|&h| {
                    state.card(h).is_some_and(|c| {
                        matches!(resolved_card_type(state, c), CardType::Attack | CardType::Power)
                    })
                })
                .collect();
            match card_select::select_cards(
                state,
                &cands,
                1,
                1,
                ChoiceTiming::Action,
                "CARD.DUAL_WIELD",
                PileType::Hand,
                false,
                choice_queue,
            )? {
                SelectResult::Selected(sel) => {
                    apply_dual_wield(content, state, &sel, upg)?;
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => {
                    Ok(CardPlayOutcome::with_choice(req, STEP_DUAL_WIELD_SELECT))
                }
            }
        }
        CardId::Enlightenment => {
            let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
            for h in hand {
                let cost = ordering::resolve_energy_cost(state, content, h)?;
                if cost > 1 {
                    if upg {
                        set_temporary_cost_override_this_combat(state, h, 1);
                    } else {
                        set_temporary_cost_override_this_turn_or_until_played(state, h, 1);
                    }
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Entrench => {
            let cur_block = state.player_creature().map_or(0, |c| c.block);
            block::gain_block(
                state,
                content,
                owner,
                Dec::from_i32(cur_block),
                ValueProp::UNPOWERED | ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Entropy => {
            power::apply(
                state,
                content,
                PowerId::EntropyPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Equilibrium => {
            let blk = Dec::from_i32(if upg { 16 } else { 13 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            power::apply(
                state,
                content,
                PowerId::RetainHandPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::EternalArmor => {
            power::apply(
                state,
                content,
                PowerId::PlatingPower,
                owner,
                Dec::from_i32(if upg { 12 } else { 9 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Exterminate => {
            let dmg = Dec::from_i32(if upg { 4 } else { 3 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
                .with_hit_count(4)
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Fasten => {
            power::apply(
                state,
                content,
                PowerId::FastenPower,
                owner,
                Dec::from_i32(if upg { 6 } else { 4 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::FeedingFrenzy => {
            power::apply(
                state,
                content,
                PowerId::FeedingFrenzyPower,
                owner,
                Dec::from_i32(if upg { 7 } else { 5 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Finesse => {
            let blk = Dec::from_i32(if upg { 7 } else { 4 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            let _ = card_pile::draw(state, content, 1, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Fisticuffs => {
            let target = require_target(ctx, "Fisticuffs")?;
            let dmg = Dec::from_i32(if upg { 9 } else { 7 });
            let results = AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                dmg,
                AttackTargetSpec::Single(target),
            )
            .execute(state, content)?;
            let blk_sum: i32 = results
                .iter()
                .map(|r| r.blocked_damage + r.unblocked_damage + r.overkill_damage)
                .sum();
            block::gain_block(
                state,
                content,
                owner,
                Dec::from_i32(blk_sum),
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::FlashOfSteel => {
            let target = require_target(ctx, "FlashOfSteel")?;
            let dmg = Dec::from_i32(if upg { 8 } else { 5 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let _ = card_pile::draw(state, content, 1, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::FranticEscape => {
            let sandpit_owner = state.enemy_handles.iter().copied().find_map(|eh| {
                let c = state.creature(eh)?;
                c.powers
                    .iter()
                    .find(|p| p.id == PowerId::SandpitPower)
                    .filter(|p| {
                        p.ext_index
                            .and_then(|ei| state.power_extensions.get(ei as usize))
                            .and_then(|ext| {
                                ext.private_fields
                                    .get("SandpitPower.<Target>k__BackingField")
                            })
                            .is_none_or(|v| matches!(v, FieldValue::CreatureHandle(h) | FieldValue::Int(h) if *h == owner.0 as i32))
                    })
                    .map(|p| (eh, p.handle))
            });
            if let Some((enemy_h, phandle)) = sandpit_owner {
                if let Some(pidx) = state
                    .creature(enemy_h)
                    .and_then(|c| c.powers.iter().position(|p| p.handle == phandle))
                {
                    let _ = power::modify_amount(
                        state,
                        content,
                        enemy_h,
                        pidx,
                        Dec::ONE,
                        Some(owner),
                        Some(ctx.card),
                    )?;
                }
            }
            if let Some(ext) = state.card_ext_mut(ctx.card) {
                ext.push_local_energy_modifier(LocalEnergyModifier {
                    kind: LocalEnergyModifierKind::AddThisCombat,
                    amount: 1,
                    is_reduce_only: false,
                });
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Fuel => {
            gold::gain_energy(state, content, Dec::from_i32(if upg { 2 } else { 1 }))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::GangUp => {
            let target = require_target(ctx, "GangUp")?;
            let hits = state
                .damage_history
                .iter()
                .filter(|e| {
                    e.round_number == state.round_number
                        && e.current_side == state.current_side
                        && e.receiver_creature_handle == target
                        && e.dealer_creature_handle.is_some_and(|d| {
                            d != owner
                                && state
                                    .creature(d)
                                    .is_some_and(|c| c.side == CombatSide::Player)
                        })
                        && e.props.is_powered_attack()
                })
                .count() as i32;
            let extra = if upg { 7 } else { 5 };
            let dmg = Dec::from_i32(5 + extra * hits);
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::GiantRock => {
            let target = require_target(ctx, "GiantRock")?;
            let dmg = Dec::from_i32(if upg { 24 } else { 20 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::GodByrd => {
            power::apply(
                state,
                content,
                PowerId::GodByrdPower,
                owner,
                Dec::from_i32(if upg { 40 } else { 30 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::GoldAxe => {
            let target = require_target(ctx, "GoldAxe")?;
            let dmg = Dec::from_i32(state.player.counters.cards_played_this_combat);
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::HandOfGreed => {
            let target = require_target(ctx, "HandOfGreed")?;
            let should_trigger_fatal = hooks::should_owner_death_trigger_fatal(state, content, target)?;
            let dmg = Dec::from_i32(if upg { 25 } else { 20 });
            let results = AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                dmg,
                AttackTargetSpec::Single(target),
            )
            .execute(state, content)?;
            if should_trigger_fatal && results.iter().any(|r| r.was_target_killed) {
                gold::gain_gold(state, content, Dec::from_i32(if upg { 25 } else { 20 }))?;
                state
                    .semantic_history
                    .record_growth(sts2_kernel::GrowthSource::HandOfGreed);
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::HelloWorld => {
            power::apply(
                state,
                content,
                PowerId::HelloWorldPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::HiddenGem => {
            let mut eligible: Vec<CardHandle> = Vec::new();
            let draw: SmallVec<[CardHandle; 32]> = SmallVec::from_slice(&state.player.draw);
            for h in draw {
                if card_pile::card_has_keyword(state, content, h, CardKeywords::UNPLAYABLE)? {
                    continue;
                }
                if let Some(c) = state.card(h) {
                    let ct = resolved_card_type(state, c);
                    let base_replay = c.base_replay_count as i32;
                    let has_ench = state
                        .card_ext(c.ext_index)
                        .and_then(|e| e.enchantment.as_ref())
                        .is_some();
                    let enchanted_replay = if has_ench {
                        content.enchantment_play_count(state, h, base_replay)?
                    } else {
                        base_replay
                    };
                    if !matches!(ct, CardType::Curse | CardType::Quest) && enchanted_replay < 1 {
                        eligible.push(h);
                    }
                }
            }
            if eligible.is_empty() {
                return Ok(CardPlayOutcome::default());
            }
            let preferred: Vec<CardHandle> = eligible
                .iter()
                .copied()
                .filter(|&h| {
                    state.card(h).is_some_and(|c| {
                        matches!(
                            resolved_card_type(state, c),
                            CardType::Attack | CardType::Skill | CardType::Power
                        )
                    })
                })
                .collect();
            let cands = if preferred.is_empty() {
                eligible
            } else {
                preferred
            };
            if let Some(&picked) = state
                .run_rngs
                .get_mut(RunRngType::CombatCardSelection)
                .next_item(&cands)
            {
                if let Some(c) = state.card_mut(picked) {
                    c.base_replay_count += if upg { 3 } else { 2 };
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::HuddleUp => {
            let amt = if upg { 3 } else { 2 };
            let _ = card_pile::draw(state, content, amt, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Impatience => {
            let has_attack = state.player.hand.iter().any(|&h| {
                state
                    .card(h)
                    .is_some_and(|c| resolved_card_type(state, c) == CardType::Attack)
            });
            if !has_attack {
                let _ = card_pile::draw(state, content, if upg { 3 } else { 2 }, false)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Intercept => {
            let target = ctx.target.unwrap_or(owner);
            let blk = Dec::from_i32(if upg { 13 } else { 9 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            power::apply(
                state,
                content,
                PowerId::CoveredPower,
                target,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::JackOfAllTrades => {
            let count = if upg { 2 } else { 1 };
            let pool = card_factory::colorless_card_pool_candidates(state);
            let chosen = card_factory::get_distinct_for_combat_filtered(
                state,
                &pool,
                count,
                RunRngType::CombatCardGeneration,
                |cid| cid != CardId::JackOfAllTrades,
            );
            for cid in chosen {
                let _ = card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    cid,
                    PileType::Hand,
                    CardPilePosition::Bottom,
                    0,
                    Some(owner),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Jackpot => {
            let target = require_target(ctx, "Jackpot")?;
            let dmg = Dec::from_i32(if upg { 30 } else { 25 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            if !hooks::is_over_or_ending(state) {
                let pool: SmallVec<[CardId; 64]> = card_factory::character_combat_pool(state)
                    .into_iter()
                    .filter(|&cid| {
                        let meta = generated_card_meta(cid);
                        !meta.flags.contains(CardStaticFlags::IS_COLORLESS)
                            && meta.canonical_energy_cost == 0
                            && !meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST)
                    })
                    .collect();
                if !pool.is_empty() {
                    let mut rolled: SmallVec<[CardId; 3]> = SmallVec::new();
                    {
                        let rng = state.run_rngs.get_mut(RunRngType::CombatCardGeneration);
                        for _ in 0..3 {
                            if let Some(&picked) = rng.next_item(&pool) {
                                rolled.push(picked);
                            }
                        }
                    }
                    let gen_upg = if upg { 1 } else { 0 };
                    for picked in rolled {
                        if hooks::is_over_or_ending(state) {
                            break;
                        }
                        let _ = card_pile::add_generated_card_to_combat(
                            state,
                            content,
                            picked,
                            PileType::Hand,
                            CardPilePosition::Bottom,
                            gen_upg,
                            Some(owner),
                        )?;
                    }
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Joy => {
            gold::gain_energy(state, content, Dec::from_i32(if upg { 2 } else { 1 }))?;
            let _ = card_pile::draw(state, content, 1, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Judgement => {
            let target = require_target(ctx, "Judgement")?;
            let threshold = if upg { 40 } else { 30 };
            if state
                .creature(target)
                .is_some_and(|c| c.is_alive() && c.current_hp <= threshold)
            {
                creature_cmd::set_current_hp(state, content, target, Dec::ZERO)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Justice => {
            let target = require_target(ctx, "Justice")?;
            let dmg = Dec::from_i32(if upg { 35 } else { 30 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Knockdown => {
            let target = require_target(ctx, "Knockdown")?;
            let dmg = Dec::from_i32(if upg { 14 } else { 10 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(
                state,
                content,
                PowerId::KnockdownPower,
                target,
                Dec::from_i32(if upg { 3 } else { 2 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Lift => {
            let target = ctx.target.unwrap_or(owner);
            let blk = Dec::from_i32(if upg { 16 } else { 11 });
            block::gain_block(
                state,
                content,
                target,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Luminesce => {
            gold::gain_energy(state, content, Dec::from_i32(if upg { 3 } else { 2 }))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::MadScience => {
            mad_science_on_play(content, state, ctx)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::MasterOfStrategy => {
            let _ = card_pile::draw(state, content, if upg { 4 } else { 3 }, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Maul => {
            let target = require_target(ctx, "Maul")?;
            let dmg = get_card_dec_field(
                state,
                ctx.card,
                "Maul._damage",
                Dec::from_i32(if upg { 6 } else { 5 }),
            );
            let inc = get_card_dec_field(
                state,
                ctx.card,
                "Maul._increase",
                Dec::from_i32(if upg { 3 } else { 2 }),
            );
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .with_hit_count(2)
                .execute(state, content)?;
            let all_mauls: Vec<CardHandle> = state
                .player
                .hand
                .iter()
                .chain(state.player.draw.iter())
                .chain(state.player.discard.iter())
                .chain(state.player.exhaust.iter())
                .chain(state.player.play.iter())
                .copied()
                .filter(|&h| state.card(h).is_some_and(|c| c.id == CardId::Maul))
                .collect();
            for mh in all_mauls {
                let m_upg = state.card(mh).is_some_and(|c| c.upgrade_level > 0);
                let cur_dmg = get_card_dec_field(
                    state,
                    mh,
                    "Maul._damage",
                    Dec::from_i32(if m_upg { 6 } else { 5 }),
                );
                let cur_extra = get_card_dec_field(
                    state,
                    mh,
                    "Maul._extraDamageFromMaulPlays",
                    Dec::ZERO,
                );
                let next_dmg = cur_dmg.checked_add(inc)?;
                let next_extra = cur_extra.checked_add(inc)?;
                if let Some(ext) = state.card_ext_mut(mh) {
                    ext.private_fields
                        .insert(Cow::Borrowed("Maul._damage"), FieldValue::Decimal(next_dmg));
                    ext.private_fields.insert(
                        Cow::Borrowed("Maul._extraDamageFromMaulPlays"),
                        FieldValue::Decimal(next_extra),
                    );
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Mayhem => {
            power::apply(
                state,
                content,
                PowerId::MayhemPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Metamorphosis => {
            let pool = card_factory::character_combat_pool(state);
            let chosen = card_factory::get_for_combat_filtered(
                state,
                &pool,
                if upg { 5 } else { 3 },
                RunRngType::CombatCardGeneration,
                |cid| generated_card_meta(cid).card_type == CardType::Attack,
            );
            for cid in chosen {
                let h = card_pile::create_card_in_combat(state, content, cid, 0)?;
                make_free_this_combat(state, h);
                card_pile::enter_combat(
                    state,
                    content,
                    h,
                    PileType::Draw,
                    CardPilePosition::Random,
                    true,
                )?;
                hooks::after_card_generated(state, content, h, Some(owner))?;
                hooks::after_card_generated_for_combat(state, content, h, Some(owner))?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::MightyShield => {
            let blk = Dec::from_i32(if upg { 25 } else { 20 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            for _ in 0..3 {
                let _ = card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    CardId::Dazed,
                    PileType::Draw,
                    CardPilePosition::Random,
                    0,
                    Some(owner),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::MightyWeapon => {
            let dmg = Dec::from_i32(if upg { 30 } else { 25 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
                .execute(state, content)?;
            for _ in 0..2 {
                let _ = card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    CardId::Dazed,
                    PileType::Draw,
                    CardPilePosition::Random,
                    0,
                    Some(owner),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Mimic => {
            let target = ctx.target.unwrap_or(owner);
            let t_blk = state.creature(target).map_or(0, |c| c.block);
            block::gain_block(
                state,
                content,
                owner,
                Dec::from_i32(t_blk),
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::MindBlast => {
            let target = require_target(ctx, "MindBlast")?;
            let dmg = Dec::from_i32(state.player.draw.len() as i32);
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::MinionDiveBomb => {
            let target = require_target(ctx, "MinionDiveBomb")?;
            let dmg = Dec::from_i32(if upg { 13 } else { 11 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::MinionSacrifice => {
            let blk = Dec::from_i32(if upg { 10 } else { 7 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::MinionStrike => {
            let target = require_target(ctx, "MinionStrike")?;
            let dmg = Dec::from_i32(if upg { 9 } else { 6 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let _ = card_pile::draw(state, content, 1, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::MyShinies => {
            let target = require_target(ctx, "MyShinies")?;
            let hits = 1 + state.player.gold / 25;
            let dmg = Dec::from_i32(if upg { 3 } else { 2 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .with_hit_count(hits)
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::NeowsFury => {
            let target = require_target(ctx, "NeowsFury")?;
            let dmg = Dec::from_i32(if upg { 14 } else { 10 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let retrieve = if upg { 3 } else { 2 };
            let count = retrieve.min(card_pile::MAX_CARDS_IN_HAND.saturating_sub(state.player.hand.len()));
            if count == 0
                || hooks::is_ending(state)
                || state.player_creature().is_none_or(|c| c.is_dead())
            {
                return Ok(CardPlayOutcome::default());
            }
            let cands: SmallVec<[CardHandle; 16]> = state.player.discard.iter().copied().collect();
            match card_select::select_cards(
                state,
                &cands,
                0,
                count,
                ChoiceTiming::Action,
                "CARD.NEOWS_FURY",
                PileType::Discard,
                false,
                choice_queue,
            )? {
                SelectResult::Selected(sel) => {
                    move_selected_to_hand(state, &sel);
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => {
                    Ok(CardPlayOutcome::with_choice(req, STEP_NEOWS_FURY_SELECT))
                }
            }
        }
        CardId::Nostalgia => {
            power::apply(
                state,
                content,
                PowerId::NostalgiaPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Omnislice => {
            let target = require_target(ctx, "Omnislice")?;
            let dmg = Dec::from_i32(if upg { 11 } else { 8 });
            let attack_id = state.next_attack_id;
            state.next_attack_id += 1;
            let mut info = AttackContextInfo {
                attack_id,
                attacker: owner,
                target_side: CombatSide::Enemy,
                card_source: Some(ctx.card),
                damage_props: ValueProp::MOVE,
                hit_count: 0,
            };
            hooks::before_attack(state, content, info)?;
            let primary_results = damage::damage_targets(
                state,
                content,
                &[target],
                dmg,
                ValueProp::MOVE,
                Some(owner),
                Some(ctx.card),
                Some(*ctx),
            )?;
            info.hit_count += 1;
            let mut all_results = primary_results.clone();
            if let Some(first) = primary_results.first() {
                let target_side = state
                    .creature(first.receiver)
                    .map_or(CombatSide::Enemy, |c| c.side);
                let mut teammates: SmallVec<[CreatureHandle; 8]> = SmallVec::new();
                let candidates: SmallVec<[CreatureHandle; 8]> = if target_side == CombatSide::Enemy {
                    SmallVec::from_slice(&state.enemy_handles)
                } else {
                    SmallVec::from_slice(&state.ally_handles)
                };
                for ch in candidates {
                    if ch != target
                        && state.creature(ch).is_some_and(|c| c.is_alive())
                        && hooks::should_allow_hitting(state, content, ch)?
                    {
                        teammates.push(ch);
                    }
                }
                if !teammates.is_empty() {
                    let splash = omnislice_splash_amount(&primary_results);
                    let splash_results = damage::damage_targets_with_hook_mode(
                        state,
                        content,
                        &teammates,
                        splash,
                        ValueProp::MOVE,
                        Some(owner),
                        Some(ctx.card),
                        Some(*ctx),
                        damage::ModifyDamageHookType::CAP,
                    )?;
                    info.hit_count += 1;
                    all_results.extend(splash_results);
                }
            }
            hooks::after_attack(state, content, info, &all_results)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Outmaneuver => {
            power::apply(
                state,
                content,
                PowerId::EnergyNextTurnPower,
                owner,
                Dec::from_i32(if upg { 3 } else { 2 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Panache => {
            power::apply(
                state,
                content,
                PowerId::PanachePower,
                owner,
                Dec::from_i32(if upg { 14 } else { 10 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::PanicButton => {
            let blk = Dec::from_i32(if upg { 40 } else { 30 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            power::apply(
                state,
                content,
                PowerId::NoBlockPower,
                owner,
                Dec::from_i32(2),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Peck => {
            let target = require_target(ctx, "Peck")?;
            let hits = if upg { 4 } else { 3 };
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(2),
                AttackTargetSpec::Single(target),
            )
            .with_hit_count(hits)
            .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::PrepTime => {
            power::apply(
                state,
                content,
                PowerId::PrepTimePower,
                owner,
                Dec::from_i32(if upg { 6 } else { 4 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Production => {
            gold::gain_energy(state, content, Dec::from_i32(if upg { 3 } else { 2 }))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Prolong => {
            let cur_block = state.player_creature().map_or(0, |c| c.block);
            power::apply(
                state,
                content,
                PowerId::BlockNextTurnPower,
                owner,
                Dec::from_i32(cur_block),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Prowess => {
            let amt = Dec::from_i32(if upg { 2 } else { 1 });
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                owner,
                amt,
                Some(owner),
                Some(ctx.card),
            )?;
            power::apply(
                state,
                content,
                PowerId::DexterityPower,
                owner,
                amt,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Purity => {
            if hooks::is_over_or_ending(state) || state.player_creature().is_none_or(|c| c.is_dead())
            {
                return Ok(CardPlayOutcome::default());
            }
            let count = if upg { 5 } else { 3 };
            let cands: SmallVec<[CardHandle; 16]> = state
                .player
                .hand
                .iter()
                .copied()
                .filter(|&h| h != ctx.card)
                .collect();
            match card_select::select_cards(
                state,
                &cands,
                0,
                count,
                ChoiceTiming::Action,
                "CARD.PURITY",
                PileType::Hand,
                false,
                choice_queue,
            )? {
                SelectResult::Selected(sel) => {
                    for h in sel {
                        card_pile::exhaust(state, content, h, false)?;
                    }
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => {
                    Ok(CardPlayOutcome::with_choice(req, STEP_PURITY_SELECT))
                }
            }
        }
        CardId::Rally => {
            let blk = Dec::from_i32(if upg { 17 } else { 12 });
            let allies: SmallVec<[CreatureHandle; 4]> = state.ally_handles.iter().copied().collect();
            for ah in allies {
                if state.creature(ah).is_some_and(|c| c.is_alive() && c.is_player) {
                    block::gain_block(
                        state,
                        content,
                        ah,
                        blk,
                        ValueProp::MOVE,
                        Some(ctx.card),
                        Some(*ctx),
                    )?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Rebound => {
            let target = require_target(ctx, "Rebound")?;
            let dmg = Dec::from_i32(if upg { 12 } else { 9 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(
                state,
                content,
                PowerId::ReboundPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Relax => {
            let blk = Dec::from_i32(if upg { 18 } else { 16 });
            let amt = Dec::from_i32(if upg { 3 } else { 2 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            power::apply(
                state,
                content,
                PowerId::DrawCardsNextTurnPower,
                owner,
                amt,
                Some(owner),
                Some(ctx.card),
            )?;
            power::apply(
                state,
                content,
                PowerId::EnergyNextTurnPower,
                owner,
                amt,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Rend => {
            let target = require_target(ctx, "Rend")?;
            let debuff_count = count_non_temporary_debuffs(state, content, target)?;
            let base = if upg { 12 } else { 10 };
            let per_debuff = if upg { 8 } else { 5 };
            let dmg = Dec::from_i32(base + per_debuff * debuff_count);
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Restlessness => {
            if state.player.hand.is_empty() {
                let draw_count = if upg { 3 } else { 2 };
                for _ in 0..draw_count {
                    let _ = card_pile::draw(state, content, 1, false)?;
                }
                gold::gain_energy(state, content, Dec::from_i32(if upg { 3 } else { 2 }))?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::RipAndTear => {
            let dmg = Dec::from_i32(if upg { 9 } else { 7 });
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                dmg,
                AttackTargetSpec::RandomOpponents {
                    allow_duplicates: true,
                },
            )
            .with_hit_count(2)
            .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::RollingBoulder => {
            power::apply(
                state,
                content,
                PowerId::RollingBoulderPower,
                owner,
                Dec::from_i32(if upg { 10 } else { 5 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Salvo => {
            let target = require_target(ctx, "Salvo")?;
            let dmg = Dec::from_i32(if upg { 14 } else { 10 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(
                state,
                content,
                PowerId::RetainHandPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Scrawl => {
            let count = card_pile::MAX_CARDS_IN_HAND.saturating_sub(state.player.hand.len());
            let _ = card_pile::draw(state, content, count as i32, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SecretTechnique => select_from_draw_pile(
            state,
            content,
            "CARD.SECRET_TECHNIQUE",
            STEP_SECRET_TECHNIQUE_SELECT,
            choice_queue,
            |s, c| resolved_card_type(s, c) == CardType::Skill,
        ),
        CardId::SecretWeapon => select_from_draw_pile(
            state,
            content,
            "CARD.SECRET_WEAPON",
            STEP_SECRET_WEAPON_SELECT,
            choice_queue,
            |s, c| resolved_card_type(s, c) == CardType::Attack,
        ),
        CardId::SeekerStrike => {
            let target = require_target(ctx, "SeekerStrike")?;
            let dmg = Dec::from_i32(if upg { 12 } else { 9 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            if hooks::is_ending(state) || state.player_creature().is_none_or(|c| c.is_dead()) {
                return Ok(CardPlayOutcome::default());
            }
            let mut draw_shuffled: Vec<CardHandle> = state.player.draw.to_vec();
            let mut sel_rng = *state.run_rngs.get(RunRngType::CombatCardSelection);
            ordering::stable_shuffle_cards(state, content, &mut draw_shuffled, &mut sel_rng)?;
            *state.run_rngs.get_mut(RunRngType::CombatCardSelection) = sel_rng;
            draw_shuffled.truncate(3);

            let mut options: SmallVec<[CardHandle; 16]> = state
                .player
                .draw
                .iter()
                .copied()
                .filter(|h| draw_shuffled.contains(h))
                .collect();
            if options.len() > 1 {
                sort_draw_candidates(state, &mut options)?;
            }
            match card_select::select_cards(
                state,
                &options,
                1,
                1,
                ChoiceTiming::Action,
                "CARD.SEEKER_STRIKE",
                PileType::Draw,
                false,
                choice_queue,
            )? {
                SelectResult::Selected(sel) => {
                    move_selected_to_hand(state, &sel);
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => {
                    Ok(CardPlayOutcome::with_choice(req, STEP_SEEKER_STRIKE_SELECT))
                }
            }
        }
        CardId::Shiv => {
            let dmg = Dec::from_i32(if upg { 6 } else { 4 });
            let has_fan = state
                .player_creature()
                .is_some_and(|c| c.powers.iter().any(|p| p.id == PowerId::FanOfKnivesPower));
            if has_fan {
                AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
                    .execute(state, content)?;
            } else {
                let target = require_target(ctx, "Shiv")?;
                AttackBuilder::from_card(
                    owner,
                    ctx.card,
                    *ctx,
                    dmg,
                    AttackTargetSpec::Single(target),
                )
                .execute(state, content)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Shockwave => {
            let amt = Dec::from_i32(if upg { 5 } else { 3 });
            let targets = collect_hittable_enemies(state, content)?;
            for t in targets {
                power::apply(
                    state,
                    content,
                    PowerId::WeakPower,
                    t,
                    amt,
                    Some(owner),
                    Some(ctx.card),
                )?;
                power::apply(
                    state,
                    content,
                    PowerId::VulnerablePower,
                    t,
                    amt,
                    Some(owner),
                    Some(ctx.card),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Slimed => {
            let _ = card_pile::draw(state, content, 1, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Soul => {
            let _ = card_pile::draw(state, content, if upg { 3 } else { 2 }, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SovereignBlade => {
            let dmg = get_card_dec_field(
                state,
                ctx.card,
                "SovereignBlade._damage",
                Dec::from_i32(10),
            );
            let seeking_edge = state
                .player_creature()
                .is_some_and(|c| c.powers.iter().any(|p| p.id == PowerId::SeekingEdgePower));
            if seeking_edge {
                AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
                    .execute(state, content)?;
            } else {
                let target = require_target(ctx, "SovereignBlade")?;
                AttackBuilder::from_card(
                    owner,
                    ctx.card,
                    *ctx,
                    dmg,
                    AttackTargetSpec::Single(target),
                )
                .execute(state, content)?;
            }
            let parry_block: i32 = state
                .player_creature()
                .map(|c| {
                    c.powers
                        .iter()
                        .filter(|p| p.id == PowerId::ParryPower)
                        .map(|p| p.amount)
                        .sum()
                })
                .unwrap_or(0);
            if parry_block > 0 {
                block::gain_block(
                    state,
                    content,
                    owner,
                    Dec::from_i32(parry_block),
                    ValueProp::MOVE,
                    Some(ctx.card),
                    Some(*ctx),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Splash => {
            let pool = other_character_combat_pool(state);
            let chosen_ids = card_factory::get_distinct_for_combat_filtered(
                state,
                &pool,
                3,
                RunRngType::CombatCardGeneration,
                |cid| generated_card_meta(cid).card_type == CardType::Attack,
            );
            let offer_upg = if upg { 1 } else { 0 };
            let cands: SmallVec<[CardHandle; 16]> = chosen_ids
                .into_iter()
                .map(|cid| create_offer_card_instance(state, cid, offer_upg, CardTempFlags::SPLASH_OFFER))
                .collect();
            match card_select::select_cards(
                state,
                &cands,
                0,
                1,
                ChoiceTiming::Action,
                "CARD.SPLASH",
                PileType::None,
                true,
                choice_queue,
            )? {
                SelectResult::Selected(sel) => {
                    finish_offer_choice(content, state, &cands, &sel, CardTempFlags::SPLASH_OFFER)?;
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => {
                    Ok(CardPlayOutcome::with_choice(req, STEP_SPLASH_SELECT))
                }
            }
        }
        CardId::Squash => {
            let target = require_target(ctx, "Squash")?;
            let dmg = Dec::from_i32(if upg { 12 } else { 10 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(
                state,
                content,
                PowerId::VulnerablePower,
                target,
                Dec::from_i32(if upg { 3 } else { 2 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Stack => {
            let blk = Dec::from_i32(state.player.discard.len() as i32 + if upg { 3 } else { 0 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Stratagem => {
            power::apply(
                state,
                content,
                PowerId::StratagemPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Strength => {
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                owner,
                Dec::from_i32(if upg { 5 } else { 4 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SweepingGaze => {
            if let Some(osty_h) = state.osty_handle() {
                if state.creature(osty_h).is_some_and(|c| c.is_alive()) {
                    let dmg = Dec::from_i32(if upg { 15 } else { 10 });
                    AttackBuilder::from_card(
                        osty_h,
                        ctx.card,
                        *ctx,
                        dmg,
                        AttackTargetSpec::RandomOpponents {
                            allow_duplicates: true,
                        },
                    )
                    .execute(state, content)?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::TagTeam => {
            let target = require_target(ctx, "TagTeam")?;
            let dmg = Dec::from_i32(if upg { 15 } else { 11 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(
                state,
                content,
                PowerId::TagTeamPower,
                target,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Temperance => {
            let blk = Dec::from_i32(if upg { 25 } else { 20 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::TheBall => {
            let target = require_target(ctx, "TheBall")?;
            let dmg = get_card_dec_field(state, ctx.card, "TheBall._damage", Dec::from_i32(10));
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let next_dmg = dmg.checked_add(Dec::from_i32(if upg { 15 } else { 10 }))?;
            if let Some(ext) = state.card_ext_mut(ctx.card) {
                ext.private_fields
                    .insert(Cow::Borrowed("TheBall._damage"), FieldValue::Decimal(next_dmg));
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::TheBomb => {
            let bomb_dmg = Dec::from_i32(if upg { 50 } else { 40 });
            if let Some(pidx) = power::apply(
                state,
                content,
                PowerId::TheBombPower,
                owner,
                Dec::from_i32(3),
                Some(owner),
                Some(ctx.card),
            )? {
                set_power_dec_field(state, owner, pidx, "TheBombPower._damage", bomb_dmg);
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::TheChariot => {
            let dmg = Dec::from_i32(if upg { 30 } else { 25 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::TheDevil => {
            let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
            let count = hand.len();
            for h in hand {
                card_pile::exhaust(state, content, h, false)?;
            }
            let _ = autoplay::from_top_of_draw_pile_with_upgrade(
                state,
                content,
                count,
                false,
                upg,
                choice_queue,
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::TheEmperor => {
            gold::gain_energy(state, content, Dec::from_i32(if upg { 4 } else { 3 }))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::TheEmpress => {
            let target = require_target(ctx, "TheEmpress")?;
            let amt = Dec::from_i32(if upg { 4 } else { 3 });
            power::apply(
                state,
                content,
                PowerId::WeakPower,
                target,
                amt,
                Some(owner),
                Some(ctx.card),
            )?;
            power::apply(
                state,
                content,
                PowerId::VulnerablePower,
                target,
                amt,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::TheFool => {
            let count = if upg { 3 } else { 2 };
            for _ in 0..count {
                let mut candidates: Vec<CardHandle> = Vec::new();
                let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
                for h in hand {
                    if !card_pile::card_has_keyword(state, content, h, CardKeywords::UNPLAYABLE)? {
                        candidates.push(h);
                    }
                }
                let mut sel_rng = *state.run_rngs.get(RunRngType::CombatCardSelection);
                ordering::stable_shuffle_cards(state, content, &mut candidates, &mut sel_rng)?;
                *state.run_rngs.get_mut(RunRngType::CombatCardSelection) = sel_rng;
                if let Some(&picked_h) = candidates.first() {
                    if !hooks::is_over_or_ending(state)
                        && state.player_creature().is_some_and(|c| c.is_alive())
                    {
                        let _ = autoplay::from_cards(state, content, &[picked_h], None, false)?;
                    }
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::TheGambit => {
            let blk = Dec::from_i32(if upg { 75 } else { 50 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            power::apply(
                state,
                content,
                PowerId::TheGambitPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::TheHangedMan => {
            let loss = Dec::from_i32(if upg { 3 } else { 6 });
            damage::damage_targets(
                state,
                content,
                &[owner],
                loss,
                ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED | ValueProp::MOVE,
                Some(owner),
                Some(ctx.card),
                Some(*ctx),
            )?;
            power::apply(
                state,
                content,
                PowerId::ExtraTurnPower,
                owner,
                Dec::ONE,
                Some(owner),
                None,
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::TheHermit => {
            match card_select::from_hand_for_discard(state, 1, "CARD.THE_HERMIT", choice_queue)? {
                SelectResult::Selected(sel) => {
                    apply_the_hermit(content, state, &sel, upg)?;
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => {
                    Ok(CardPlayOutcome::with_choice(req, STEP_THE_HERMIT_SELECT))
                }
            }
        }
        CardId::TheHierophant => {
            let count = if upg { 4 } else { 3 };
            let pool = card_factory::colorless_card_pool_candidates(state);
            let chosen = card_factory::get_distinct_for_combat_filtered(
                state,
                &pool,
                count,
                RunRngType::CombatCardGeneration,
                |_| true,
            );
            for cid in chosen {
                let _ = card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    cid,
                    PileType::Hand,
                    CardPilePosition::Bottom,
                    0,
                    Some(owner),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::TheHighPriestess => {
            let _ = card_pile::draw(state, content, if upg { 5 } else { 4 }, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::TheLovers => {
            let pool = other_character_combat_pool(state);
            let count = card_pile::MAX_CARDS_IN_HAND.saturating_sub(state.player.hand.len());
            let chosen = card_factory::get_for_combat(
                state,
                &pool,
                count,
                RunRngType::CombatCardGeneration,
            );
            let gen_upg = if upg { 1 } else { 0 };
            for cid in chosen {
                let _ = card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    cid,
                    PileType::Hand,
                    CardPilePosition::Bottom,
                    gen_upg,
                    Some(owner),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::TheMagician => {
            let repeat = if upg { 4 } else { 3 };
            orb::add_slots(state, repeat);
            const ORBS: [sts2_types::OrbId; 5] = [
                sts2_types::OrbId::LightningOrb,
                sts2_types::OrbId::FrostOrb,
                sts2_types::OrbId::DarkOrb,
                sts2_types::OrbId::PlasmaOrb,
                sts2_types::OrbId::GlassOrb,
            ];
            for _ in 0..repeat {
                if let Some(&oid) = state
                    .run_rngs
                    .get_mut(RunRngType::CombatOrbs)
                    .next_item(&ORBS)
                {
                    orb::channel(state, content, oid)?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::TheMoon => {
            let count = card_pile::MAX_CARDS_IN_HAND.saturating_sub(state.player.hand.len());
            let gen_upg = if upg { 1 } else { 0 };
            for _ in 0..count {
                let _ = card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    CardId::Shiv,
                    PileType::Hand,
                    CardPilePosition::Bottom,
                    gen_upg,
                    Some(owner),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::TheShot => {
            let target = require_target(ctx, "TheShot")?;
            let dmg = Dec::from_i32(if upg { 25 } else { 20 });
            let cmd =
                AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                    .from_arrow();
            let _ = arrow::shoot_arrow_at_target(state, content, target, cmd)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::TheStar => {
            let amt = Dec::from_i32(if upg { 4 } else { 3 });
            power::apply(
                state,
                content,
                PowerId::EnergyNextTurnPower,
                owner,
                amt,
                Some(owner),
                Some(ctx.card),
            )?;
            power::apply(
                state,
                content,
                PowerId::DrawCardsNextTurnPower,
                owner,
                amt,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::TheSun => {
            let amt = if upg { 3 } else { 2 };
            let _ = card_pile::draw(state, content, amt, false)?;
            gold::gain_energy(state, content, Dec::from_i32(amt))?;
            for _ in 0..2 {
                if hooks::is_over_or_ending(state) {
                    break;
                }
                let _ = card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    CardId::Soot,
                    PileType::Draw,
                    CardPilePosition::Random,
                    0,
                    Some(owner),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::TheTower => {
            let target = require_target(ctx, "TheTower")?;
            let dmg = the_tower_damage(upg, state.static_ctx.total_floor);
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::TheWorld => {
            let space = card_pile::MAX_CARDS_IN_HAND.saturating_sub(state.player.hand.len());
            let count = (if upg { 7 } else { 5 }).min(space);
            let mut picked = state.player.discard.to_vec();
            state
                .run_rngs
                .get_mut(RunRngType::CombatCardSelection)
                .unstable_shuffle(&mut picked);
            picked.truncate(count);
            card_pile::add_and_notify(
                state,
                content,
                &picked,
                PileType::Hand,
                CardPilePosition::Bottom,
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::ThinkingAhead => {
            let _ = card_pile::draw(state, content, 2, false)?;
            if hooks::is_over_or_ending(state) || state.player_creature().is_none_or(|c| c.is_dead())
            {
                return Ok(CardPlayOutcome::default());
            }
            let cands: SmallVec<[CardHandle; 16]> = state.player.hand.iter().copied().collect();
            match card_select::select_cards(
                state,
                &cands,
                1,
                1,
                ChoiceTiming::Action,
                "CARD.THINKING_AHEAD",
                PileType::Hand,
                false,
                choice_queue,
            )? {
                SelectResult::Selected(sel) => {
                    if let Some(&h) = sel.first() {
                        card_pile::add_sync(state, h, PileType::Draw, CardPilePosition::Top);
                    }
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => {
                    Ok(CardPlayOutcome::with_choice(req, STEP_THINKING_AHEAD_SELECT))
                }
            }
        }
        CardId::ThrummingHatchet => {
            let target = require_target(ctx, "ThrummingHatchet")?;
            let dmg = get_card_dec_field(
                state,
                ctx.card,
                "ThrummingHatchet._damage",
                Dec::from_i32(if upg { 14 } else { 11 }),
            );
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let turn = state.player.turn_number;
            if let Some(ext) = state.card_ext_mut(ctx.card) {
                ext.private_fields.insert(
                    Cow::Borrowed("ThrummingHatchet._playedOnTurnNumber"),
                    FieldValue::NullableInt(Some(turn)),
                );
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::ToricToughness => {
            let blk = Dec::from_i32(if upg { 7 } else { 5 });
            let actual = block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            if let Some(pidx) = power::apply(
                state,
                content,
                PowerId::ToricToughnessPower,
                owner,
                Dec::from_i32(2),
                Some(owner),
                Some(ctx.card),
            )? {
                set_power_dec_field(
                    state,
                    owner,
                    pidx,
                    "ToricToughnessPower.<StoredBlock>k__BackingField",
                    actual,
                );
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::UltimateDefend => {
            let blk = Dec::from_i32(if upg { 15 } else { 11 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::UltimateStrike => {
            let target = require_target(ctx, "UltimateStrike")?;
            let dmg = Dec::from_i32(if upg { 20 } else { 14 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Volley => {
            let dmg = Dec::from_i32(if upg { 14 } else { 10 });
            let hits = hooks::modify_x_value(state, content, ctx.card, ctx.energy_value)?.max(0);
            if hits > 0 {
                AttackBuilder::from_card(
                    owner,
                    ctx.card,
                    *ctx,
                    dmg,
                    AttackTargetSpec::RandomOpponents {
                        allow_duplicates: true,
                    },
                )
                .with_hit_count(hits)
                .execute(state, content)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::WheelOfFortune => {
            let draw_count = if upg { 9 } else { 7 };
            if !state.player.hand.is_empty() {
                let hand_snapshot: SmallVec<[CardHandle; 16]> =
                    SmallVec::from_slice(&state.player.hand);
                discard_and_draw(state, content, &hand_snapshot, draw_count)?;
            } else {
                let _ = card_pile::draw(state, content, draw_count, false)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Whistle => {
            let target = require_target(ctx, "Whistle")?;
            let dmg = Dec::from_i32(if upg { 44 } else { 33 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            creature_cmd::stun(state, target, None)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Wish => select_from_draw_pile(
            state,
            content,
            "CARD.WISH",
            STEP_WISH_SELECT,
            choice_queue,
            |_, _| true,
        ),
        // Passive / unplayable / no-op cards:
        CardId::Amnesia
        | CardId::AscendersBane
        | CardId::BadLuck
        | CardId::Beckon
        | CardId::Burn
        | CardId::ByrdonisEgg
        | CardId::Clumsy
        | CardId::CurseOfTheBell
        | CardId::Dazed
        | CardId::Debris
        | CardId::Debt
        | CardId::Decay
        | CardId::Disintegration
        | CardId::Doubt
        | CardId::Dowsing
        | CardId::Enthralled
        | CardId::Folly
        | CardId::Greed
        | CardId::Guilty
        | CardId::Infection
        | CardId::Injury
        | CardId::LanternKey
        | CardId::MindRot
        | CardId::Normality
        | CardId::PoorSleep
        | CardId::Regret
        | CardId::Sadness
        | CardId::Shame
        | CardId::Sloth
        | CardId::Soot
        | CardId::SpoilsMap
        | CardId::SporeMind
        | CardId::Toxic
        | CardId::Void
        | CardId::WasteAway
        | CardId::Wither
        | CardId::Wound
        | CardId::Writhe => Ok(CardPlayOutcome::default()),
        _ => Err(KernelError::unsupported(
            "Card",
            card.id.model_id_str().to_owned(),
        )),
    }
}

pub fn on_play_resume(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    ctx: &CardPlayContext,
    selected: &[CardHandle],
    resume_step: u8,
) -> Result<CardPlayOutcome, KernelError> {
    let card = *state
        .card(ctx.card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?;
    let upg = card.upgrade_level > 0;
    match (card.id, resume_step) {
        (CardId::Discovery, STEP_DISCOVERY_SELECT) => {
            let cands = collect_marked_offer_cards(state, CardTempFlags::DISCOVERY_OFFER);
            finish_offer_choice(content, state, &cands, selected, CardTempFlags::DISCOVERY_OFFER)?;
            Ok(CardPlayOutcome::default())
        }
        (CardId::Splash, STEP_SPLASH_SELECT) => {
            let cands = collect_marked_offer_cards(state, CardTempFlags::SPLASH_OFFER);
            finish_offer_choice(content, state, &cands, selected, CardTempFlags::SPLASH_OFFER)?;
            Ok(CardPlayOutcome::default())
        }
        (CardId::DualWield, STEP_DUAL_WIELD_SELECT) => {
            apply_dual_wield(content, state, selected, upg)?;
            Ok(CardPlayOutcome::default())
        }
        (CardId::Purity, STEP_PURITY_SELECT) => {
            for &h in selected {
                card_pile::exhaust(state, content, h, false)?;
            }
            Ok(CardPlayOutcome::default())
        }
        (
            CardId::SecretTechnique | CardId::SecretWeapon | CardId::SeekerStrike | CardId::Wish,
            STEP_SECRET_TECHNIQUE_SELECT
            | STEP_SECRET_WEAPON_SELECT
            | STEP_SEEKER_STRIKE_SELECT
            | STEP_WISH_SELECT,
        )
        | (CardId::NeowsFury, STEP_NEOWS_FURY_SELECT) => {
            move_selected_to_hand(state, selected);
            Ok(CardPlayOutcome::default())
        }
        (CardId::TheHermit, STEP_THE_HERMIT_SELECT) => {
            apply_the_hermit(content, state, selected, upg)?;
            Ok(CardPlayOutcome::default())
        }
        (CardId::ThinkingAhead, STEP_THINKING_AHEAD_SELECT) => {
            if let Some(&h) = selected.first() {
                card_pile::add_sync(state, h, PileType::Draw, CardPilePosition::Top);
            }
            Ok(CardPlayOutcome::default())
        }
        _ => Err(KernelError::unsupported(
            "CardResume",
            card.id.model_id_str().to_owned(),
        )),
    }
}

pub fn on_upgrade(state: &mut BattleState, card: CardHandle) -> Result<(), KernelError> {
    let ci = *state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?;
    let _ = card_meta(ci.id)?;

    match ci.id {
        CardId::Abundance
        | CardId::Alchemize
        | CardId::Apotheosis
        | CardId::Automation
        | CardId::Calamity
        | CardId::Distraction
        | CardId::Entrench
        | CardId::Mayhem
        | CardId::MindBlast
        | CardId::Nostalgia
        | CardId::SovereignBlade
        | CardId::Stratagem => {
            if let Some(c) = state.card_mut(card) {
                c.base_energy_cost = (c.base_energy_cost - 1).max(0);
            }
        }
        CardId::Anointed | CardId::GoldAxe | CardId::Scrawl | CardId::Wish => {
            if let Some(c) = state.card_mut(card) {
                c.local_keywords.insert(CardKeywords::RETAIN);
            }
        }
        CardId::BeaconOfHope | CardId::Entropy | CardId::HelloWorld | CardId::MadScience => {
            if let Some(c) = state.card_mut(card) {
                c.local_keywords.insert(CardKeywords::INNATE);
            }
        }
        CardId::Apparition => {
            if let Some(c) = state.card_mut(card) {
                c.local_keywords.remove(CardKeywords::ETHEREAL);
            }
        }
        CardId::Discovery
        | CardId::Mimic
        | CardId::Prolong
        | CardId::SecretTechnique
        | CardId::SecretWeapon
        | CardId::ThinkingAhead => {
            if let Some(c) = state.card_mut(card) {
                c.local_keywords.remove(CardKeywords::EXHAUST);
            }
        }
        CardId::Bolas => {
            if let Some(ext) = state.card_ext_mut(card) {
                if let Some(FieldValue::Decimal(d)) = ext.private_fields.get_mut("Bolas._damage") {
                    *d = d.checked_add(Dec::ONE)?;
                }
            }
        }
        CardId::ThrummingHatchet => {
            if let Some(ext) = state.card_ext_mut(card) {
                if let Some(FieldValue::Decimal(d)) =
                    ext.private_fields.get_mut("ThrummingHatchet._damage")
                {
                    *d = d.checked_add(Dec::from_i32(3))?;
                }
            }
        }
        CardId::Maul => {
            if let Some(ext) = state.card_ext_mut(card) {
                if let Some(FieldValue::Decimal(d)) = ext.private_fields.get_mut("Maul._damage") {
                    *d = d.checked_add(Dec::ONE)?;
                }
                if let Some(FieldValue::Decimal(i)) = ext.private_fields.get_mut("Maul._increase") {
                    *i = i.checked_add(Dec::ONE)?;
                }
            }
        }
        CardId::TheShot => {
            if let Some(ext) = state.card_ext_mut(card) {
                if let Some(FieldValue::Decimal(d)) =
                    ext.private_fields.get_mut("TheShot._extraDamage")
                {
                    *d = d.checked_add(Dec::from_i32(3))?;
                }
            }
        }
        _ => {}
    }
    Ok(())
}

pub fn comparison_base_values(
    state: &BattleState,
    card: CardHandle,
) -> Result<SmallVec<[(&'static str, Dec); 4]>, KernelError> {
    let ci = *state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?;
    let _ = card_meta(ci.id)?;
    let mut out = SmallVec::new();
    match ci.id {
        CardId::Disintegration => {
            let amt = get_card_int_field(
                state,
                card,
                "Disintegration.<PowerAmount>k__BackingField",
                6,
            );
            out.push(("DisintegrationPower", Dec::from_i32(amt)));
        }
        CardId::Dowsing => {
            let entered = get_card_int_field(state, card, "Dowsing._unknownRoomsEntered", 0);
            out.push(("Rooms", Dec::from_i32((4 - entered).max(1))));
        }
        CardId::Guilty => {
            let completed = get_card_int_field(state, card, "Guilty._combatsCompleted", 0);
            out.push(("Combats", Dec::from_i32(5 - completed)));
        }
        CardId::Maul => {
            let default_dmg = if ci.upgrade_level > 0 { 6 } else { 5 };
            let dmg = get_card_dec_field(state, card, "Maul._damage", Dec::from_i32(default_dmg));
            out.push(("Damage", dmg));
        }
        CardId::SovereignBlade => {
            let dmg = get_card_dec_field(state, card, "SovereignBlade._damage", Dec::from_i32(10));
            out.push(("Damage", dmg));
        }
        CardId::TheBall => {
            let dmg = get_card_dec_field(state, card, "TheBall._damage", Dec::from_i32(10));
            out.push(("Damage", dmg));
        }
        CardId::Wither => {
            let fake_upg = get_card_int_field(state, card, "Wither._fakeUpgradeLevel", 0);
            out.push(("Damage", Dec::from_i32(3 + 3 * fake_upg)));
        }
        _ => {}
    }
    Ok(out)
}

pub fn dispatch_hook(
    _content: &dyn ContentDispatcher,
    state: &mut BattleState,
    card: CardHandle,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    let ci = *state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?;
    let _ = card_meta(ci.id)?;

    match ci.id {
        CardId::Bolas | CardId::ThrummingHatchet => {
            if slot == HookSlot::BeforeHandDraw {
                let key = if ci.id == CardId::Bolas {
                    "Bolas._playedOnTurnNumber"
                } else {
                    "ThrummingHatchet._playedOnTurnNumber"
                };
                if let Some(played_turn) = get_card_nullable_int_field(state, card, key) {
                    if state.player.turn_number == played_turn + 1 && ci.pile != PileType::Hand {
                        card_pile::add_sync(state, card, PileType::Hand, CardPilePosition::Bottom);
                    }
                }
            }
            Ok(HookResult::default())
        }
        CardId::Enthralled => {
            if slot == HookSlot::ShouldPlay {
                if ci.pile != PileType::Hand {
                    return Ok(bool_result(true));
                }
                if let HookArgs::CardOnly(target_card) = args {
                    if *target_card == card {
                        return Ok(bool_result(true));
                    }
                    let is_autoplay = state
                        .card(*target_card)
                        .is_some_and(|c| c.pile == PileType::Play);
                    return Ok(bool_result(is_autoplay));
                }
            }
            Ok(HookResult::default())
        }
        CardId::Guilty => {
            if slot == HookSlot::AfterCombatEnd {
                let next = get_card_int_field(state, card, "Guilty._combatsCompleted", 0) + 1;
                set_card_int_field(state, card, "Guilty._combatsCompleted", next);
                if next >= 5 && ci.pile == PileType::Deck {
                    card_pile::remove(state, card);
                }
            }
            Ok(HookResult::default())
        }
        CardId::Normality => {
            if slot == HookSlot::ShouldPlay {
                if ci.pile != PileType::Hand {
                    return Ok(bool_result(true));
                }
                return Ok(bool_result(
                    state.player.counters.card_plays_started_this_turn < 3,
                ));
            }
            Ok(HookResult::default())
        }
        CardId::Regret => {
            if slot == HookSlot::BeforeSideTurnEnd {
                if let HookArgs::SideTurn { side, participants } = args {
                    let owner = state.player.creature_handle;
                    if *side == CombatSide::Player
                        && participants.contains(&owner)
                        && ci.pile == PileType::Hand
                    {
                        let count = state.player.hand.len() as i32;
                        set_card_int_field(state, card, "Regret._cardsInHand", count);
                    }
                }
            }
            Ok(HookResult::default())
        }
        CardId::SovereignBlade | CardId::SpoilsMap => Ok(HookResult::default()),
        CardId::Void => {
            if slot == HookSlot::AfterCardDrawn {
                if let HookArgs::CardDrawn { card: drawn, .. } = args {
                    if *drawn == card {
                        gold::lose_energy(state, Dec::ONE)?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        _ => Err(KernelError::unsupported(
            "CardHook",
            ci.id.model_id_str().to_owned(),
        )),
    }
}

/// Pure `MadScience` execution helper supporting Attack/Skill/Power and all 9 `MadScienceRider` variants.
pub fn mad_science_on_play(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    ctx: &CardPlayContext,
) -> Result<(), KernelError> {
    let rtype = mad_science_resolved_type(state, ctx.card);
    let rider = state
        .card(ctx.card)
        .and_then(|c| state.card_ext(c.ext_index))
        .and_then(|e| e.private_fields.get("MadScience.<Rider>k__BackingField"))
        .and_then(|v| match v {
            FieldValue::String(s) => Some(MadScienceRider::from_str(s)),
            FieldValue::Int(i) => Some(match *i {
                1 => MadScienceRider::Sapping,
                2 => MadScienceRider::Violence,
                3 => MadScienceRider::Choking,
                4 => MadScienceRider::Energized,
                5 => MadScienceRider::Wisdom,
                6 => MadScienceRider::Chaos,
                7 => MadScienceRider::Expertise,
                8 => MadScienceRider::Curious,
                9 => MadScienceRider::Improvement,
                _ => MadScienceRider::None,
            }),
            _ => None,
        })
        .unwrap_or(MadScienceRider::None);
    let owner = state.player.creature_handle;

    match rtype {
        CardType::Attack => {
            let target = require_target(ctx, "MadScience")?;
            let hits = if rider == MadScienceRider::Violence { 3 } else { 1 };
            for _ in 0..hits {
                AttackBuilder::from_card(
                    owner,
                    ctx.card,
                    *ctx,
                    Dec::from_i32(12),
                    AttackTargetSpec::Single(target),
                )
                .execute(state, content)?;
            }
            if rider == MadScienceRider::Sapping {
                power::apply(
                    state,
                    content,
                    PowerId::WeakPower,
                    target,
                    Dec::from_i32(2),
                    Some(owner),
                    Some(ctx.card),
                )?;
                power::apply(
                    state,
                    content,
                    PowerId::VulnerablePower,
                    target,
                    Dec::from_i32(2),
                    Some(owner),
                    Some(ctx.card),
                )?;
            } else if rider == MadScienceRider::Choking {
                power::apply(
                    state,
                    content,
                    PowerId::StranglePower,
                    target,
                    Dec::from_i32(6),
                    Some(owner),
                    Some(ctx.card),
                )?;
            }
        }
        CardType::Skill => {
            block::gain_block(
                state,
                content,
                owner,
                Dec::from_i32(8),
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            match rider {
                MadScienceRider::Energized => gold::gain_energy(state, content, Dec::from_i32(2))?,
                MadScienceRider::Wisdom => {
                    let _ = card_pile::draw(state, content, 3, false)?;
                }
                MadScienceRider::Chaos => {
                    let pool = card_factory::character_combat_pool(state);
                    let chosen = card_factory::get_distinct_for_combat(
                        state,
                        &pool,
                        1,
                        RunRngType::CombatCardGeneration,
                    );
                    if let Some(&cid) = chosen.first() {
                        let h = card_pile::create_card_in_combat(state, content, cid, 0)?;
                        set_to_free_this_turn(state, h);
                        generate_existing_card_into_hand(content, state, h)?;
                    }
                }
                _ => {}
            }
        }
        CardType::Power => match rider {
            MadScienceRider::Expertise => {
                power::apply(
                    state,
                    content,
                    PowerId::StrengthPower,
                    owner,
                    Dec::from_i32(2),
                    Some(owner),
                    Some(ctx.card),
                )?;
                power::apply(
                    state,
                    content,
                    PowerId::DexterityPower,
                    owner,
                    Dec::from_i32(2),
                    Some(owner),
                    Some(ctx.card),
                )?;
            }
            MadScienceRider::Curious => {
                power::apply(
                    state,
                    content,
                    PowerId::CuriousPower,
                    owner,
                    Dec::ONE,
                    Some(owner),
                    Some(ctx.card),
                )?;
            }
            MadScienceRider::Improvement => {
                power::apply(
                    state,
                    content,
                    PowerId::ImprovementPower,
                    owner,
                    Dec::ONE,
                    Some(owner),
                    Some(ctx.card),
                )?;
                state
                    .semantic_history
                    .record_growth(sts2_kernel::GrowthSource::MadScience);
            }
            _ => {}
        },
        _ => {}
    }
    Ok(())
}

fn require_target(ctx: &CardPlayContext, name: &str) -> Result<CreatureHandle, KernelError> {
    ctx.target
        .ok_or_else(|| KernelError::InvalidAction(format!("{name} requires target")))
}

const fn bool_result(v: bool) -> HookResult {
    HookResult {
        bool_val: Some(v),
        dec_val: None,
        int_val: None,
        creature_val: None,
        location_val: None,
        modified: false,
        keywords: None,
        needs_choice: None,
    }
}

const fn dingy_dagger_damage(upgrade_level: i32) -> i32 {
    if upgrade_level <= 0 {
        7
    } else {
        7 + 3 * upgrade_level + upgrade_level * (upgrade_level + 1) / 2
    }
}

fn get_card_int_field(state: &BattleState, card: CardHandle, key: &str, default: i32) -> i32 {
    state
        .card(card)
        .and_then(|c| state.card_ext(c.ext_index))
        .and_then(|e| e.private_fields.get(key))
        .and_then(|v| match v {
            FieldValue::Int(i) => Some(*i),
            _ => None,
        })
        .unwrap_or(default)
}

fn get_card_nullable_int_field(state: &BattleState, card: CardHandle, key: &str) -> Option<i32> {
    state
        .card(card)
        .and_then(|c| state.card_ext(c.ext_index))
        .and_then(|e| e.private_fields.get(key))
        .and_then(|v| match v {
            FieldValue::NullableInt(o) => *o,
            FieldValue::Int(i) => Some(*i),
            _ => None,
        })
}

fn set_card_int_field(state: &mut BattleState, card: CardHandle, key: &'static str, val: i32) {
    if let Some(ext) = state.card_ext_mut(card) {
        ext.private_fields
            .insert(Cow::Borrowed(key), FieldValue::Int(val));
    }
}

fn get_card_dec_field(state: &BattleState, card: CardHandle, key: &str, default: Dec) -> Dec {
    state
        .card(card)
        .and_then(|c| state.card_ext(c.ext_index))
        .and_then(|e| e.private_fields.get(key))
        .and_then(|v| match v {
            FieldValue::Decimal(d) => Some(*d),
            FieldValue::Int(i) => Some(Dec::from_i32(*i)),
            _ => None,
        })
        .unwrap_or(default)
}

fn set_power_dec_field(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &'static str,
    val: Dec,
) {
    let ext_idx = state
        .creature(creature)
        .and_then(|c| c.powers.get(power_idx))
        .map(|p| p.ext_index);
    let Some(existing_ext) = ext_idx else {
        return;
    };
    let idx = match existing_ext {
        Some(i) => i as usize,
        None => {
            let i = state.power_extensions.len();
            state.power_extensions.push(PowerExtension::default());
            if let Some(p) = state
                .creature_mut(creature)
                .and_then(|c| c.powers.get_mut(power_idx))
            {
                p.ext_index = Some(i as u32);
            }
            i
        }
    };
    if let Some(ext) = state.power_extensions.get_mut(idx) {
        ext.private_fields
            .insert(Cow::Borrowed(key), FieldValue::Decimal(val));
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

fn set_to_free_this_turn(state: &mut BattleState, handle: CardHandle) {
    let Some(card) = state.card(handle) else {
        return;
    };
    let meta = generated_card_meta(card.id);
    if !meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST) {
        if let Some(c) = state.card_mut(handle) {
            c.temp_cost_override_this_turn_or_until_played = OptI16::some(0);
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
            c.temp_star_cost_override_this_turn = OptI16::some(0);
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

fn set_temporary_cost_override_this_turn_or_until_played(
    state: &mut BattleState,
    handle: CardHandle,
    cost: i32,
) {
    if let Some(c) = state.card_mut(handle) {
        c.temp_cost_override_this_turn_or_until_played = OptI16::some_clamped(cost);
    }
    if let Some(ext) = state.card_ext_mut(handle) {
        ext.push_local_energy_modifier(LocalEnergyModifier {
            kind: LocalEnergyModifierKind::SetThisTurnOrUntilPlayed,
            amount: cost,
            is_reduce_only: false,
        });
    }
}

fn set_temporary_cost_override_this_combat(
    state: &mut BattleState,
    handle: CardHandle,
    cost: i32,
) {
    if let Some(c) = state.card_mut(handle) {
        c.temp_cost_override_this_combat = OptI16::some_clamped(cost);
    }
    if let Some(ext) = state.card_ext_mut(handle) {
        ext.push_local_energy_modifier(LocalEnergyModifier {
            kind: LocalEnergyModifierKind::SetThisCombat,
            amount: cost,
            is_reduce_only: false,
        });
    }
}

fn generate_existing_card_into_hand(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    handle: CardHandle,
) -> Result<(), KernelError> {
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

fn create_offer_card_instance(
    state: &mut BattleState,
    card_id: CardId,
    upgrade_level: i32,
    marker_flag: CardTempFlags,
) -> CardHandle {
    let meta = generated_card_meta(card_id);
    let handle = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    let mut temp_flags = marker_flag;
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
        floor_added_to_deck: OptI16::NONE,
        deck_card_handle: OptCardHandle::NONE,
        clone_of_handle: OptCardHandle::NONE,
        temp_star_cost_override_this_turn: OptI16::NONE,
        temp_cost_override_this_turn: OptI16::NONE,
        temp_cost_override_this_turn_or_until_played: OptI16::NONE,
        temp_cost_override_this_combat: OptI16::NONE,
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
    if upgrade_level > 0 && meta.max_upgrade_level > 0 {
        if let Some(c) = state.card_mut(handle) {
            c.upgrade_level = 1;
        }
        let _ = crate::cards::card_on_upgrade(state, handle);
    }
    handle
}

fn collect_marked_offer_cards(
    state: &BattleState,
    marker_flag: CardTempFlags,
) -> SmallVec<[CardHandle; 16]> {
    state
        .cards
        .iter()
        .filter(|c| {
            !c.removed_from_state
                && c.pile == PileType::None
                && c.temp_flags.contains(marker_flag)
        })
        .map(|c| c.handle)
        .collect()
}

fn finish_offer_choice(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    all_candidates: &[CardHandle],
    selected: &[CardHandle],
    marker_flag: CardTempFlags,
) -> Result<(), KernelError> {
    let chosen = selected.first().copied();
    for &cand in all_candidates {
        let mut removed_pile = PileType::None;
        if let Some(c) = state.card_mut(cand) {
            c.temp_flags.remove(marker_flag);
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

fn other_character_combat_pool(state: &BattleState) -> SmallVec<[CardId; 128]> {
    card_factory::other_character_combat_pool(state)
}

fn clone_card_for_combat(state: &mut BattleState, original: CardHandle) -> Result<CardHandle, KernelError> {
    let mut src = *state
        .card(original)
        .ok_or_else(|| KernelError::InvalidState("missing card to clone".to_owned()))?;
    let source_handle = if src.temp_flags.contains(CardTempFlags::IS_DUPE) {
        if let Some(co) = src.clone_of_handle.get() {
            if let Some(orig_card) = state.card(co) {
                src = *orig_card;
                co
            } else {
                original
            }
        } else {
            original
        }
    } else {
        original
    };

    let new_handle = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    let new_ext_idx = if let Some(mut cloned_ext) = state.card_ext(src.ext_index).cloned() {
        cloned_ext.strip_non_cloned_private_fields();
        let i = state.card_extensions.len();
        state.card_extensions.push(cloned_ext);
        Some(i as u32)
    } else {
        None
    };

    let mut copy = src;
    copy.handle = new_handle;
    copy.temp_flags
        .remove(CardTempFlags::EXHAUST_ON_NEXT_PLAY | CardTempFlags::IS_DUPE);
    copy.deck_card_handle = OptCardHandle::NONE;
    copy.clone_of_handle = OptCardHandle::some(source_handle);
    copy.ext_index = new_ext_idx;
    copy.pile = PileType::None;
    copy.removed_from_state = false;
    copy.transform_into_after_play = None;
    state.push_card(copy);
    Ok(new_handle)
}

fn apply_dual_wield(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    selected: &[CardHandle],
    upg: bool,
) -> Result<(), KernelError> {
    let Some(&original) = selected.first() else {
        return Ok(());
    };
    let copies = if upg { 2 } else { 1 };
    for _ in 0..copies {
        let cloned = clone_card_for_combat(state, original)?;
        generate_existing_card_into_hand(content, state, cloned)?;
    }
    Ok(())
}

fn apply_the_hermit(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    selected: &[CardHandle],
    upg: bool,
) -> Result<(), KernelError> {
    for &h in selected {
        let is_curse = state
            .card(h)
            .is_some_and(|c| resolved_card_type(state, c) == CardType::Curse);
        if is_curse {
            card_pile::exhaust(state, content, h, false)?;
        } else {
            card_cmd::discard_single(state, content, h)?;
        }
    }
    let _ = card_pile::draw(state, content, if upg { 4 } else { 3 }, false)?;
    Ok(())
}

fn move_selected_to_hand(state: &mut BattleState, selected: &[CardHandle]) {
    for &h in selected {
        card_pile::add_sync(state, h, PileType::Hand, CardPilePosition::Bottom);
    }
}

fn sort_draw_candidates(
    state: &BattleState,
    options: &mut [CardHandle],
) -> Result<(), KernelError> {
    if options.len() <= u16::MAX as usize {
        let mut pairs: SmallVec<[(u64, CardHandle); 64]> = SmallVec::with_capacity(options.len());
        for (orig_idx, &h) in options.iter().enumerate() {
            let cid = state
                .card(h)
                .ok_or_else(|| KernelError::InvalidState("missing draw card".to_owned()))?
                .id;
            let key = (u64::from(crate::card_draw_sort_key(cid)) << 16) | (orig_idx as u64);
            pairs.push((key, h));
        }
        pairs.sort_unstable_by_key(|&(k, _)| k);
        for (i, &(_, h)) in pairs.iter().enumerate() {
            options[i] = h;
        }
    } else {
        let mut pairs: SmallVec<[(u32, CardHandle); 64]> = SmallVec::with_capacity(options.len());
        for &h in options.iter() {
            let cid = state
                .card(h)
                .ok_or_else(|| KernelError::InvalidState("missing draw card".to_owned()))?
                .id;
            pairs.push((crate::card_draw_sort_key(cid), h));
        }
        pairs.sort_by_key(|&(k, _)| k);
        for (i, &(_, h)) in pairs.iter().enumerate() {
            options[i] = h;
        }
    }
    Ok(())
}

fn select_from_draw_pile<F>(
    state: &mut BattleState,
    _content: &dyn ContentDispatcher,
    source_id: &'static str,
    resume_step: u8,
    choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
    mut filter: F,
) -> Result<CardPlayOutcome, KernelError>
where
    F: FnMut(&BattleState, &CardInst) -> bool,
{
    if hooks::is_ending(state) || state.player_creature().is_none_or(|c| c.is_dead()) {
        return Ok(CardPlayOutcome::default());
    }
    let draw = &state.player.draw;
    let cards = &state.cards;
    let options: SmallVec<[CardHandle; 64]> = if draw.len() <= u16::MAX as usize {
        let mut pairs: SmallVec<[(u64, CardHandle); 64]> = SmallVec::new();
        for (i, &h) in draw.iter().enumerate() {
            #[cfg(target_arch = "x86_64")]
            if i + 4 < draw.len() {
                let ahead = unsafe { *draw.get_unchecked(i + 4) }.0.wrapping_sub(1) as usize;
                if ahead < cards.len() {
                    unsafe {
                        core::arch::x86_64::_mm_prefetch::<{ core::arch::x86_64::_MM_HINT_T0 }>(
                            cards.as_ptr().add(ahead).cast::<i8>(),
                        );
                    }
                }
            }
            if let Some(c) = state.card(h) {
                if filter(state, c) {
                    let key = (u64::from(crate::card_draw_sort_key(c.id)) << 16) | (i as u64);
                    pairs.push((key, h));
                }
            }
        }
        if pairs.len() > 1 {
            pairs.sort_unstable_by_key(|&(k, _)| k);
        }
        pairs.into_iter().map(|(_, h)| h).collect()
    } else {
        let mut opts: SmallVec<[CardHandle; 64]> = draw
            .iter()
            .copied()
            .filter(|&h| state.card(h).is_some_and(|c| filter(state, c)))
            .collect();
        if opts.len() > 1 {
            sort_draw_candidates(state, &mut opts)?;
        }
        opts
    };
    match card_select::select_cards(
        state,
        &options,
        1,
        1,
        ChoiceTiming::Action,
        source_id,
        PileType::Draw,
        false,
        choice_queue,
    )? {
        SelectResult::Selected(sel) => {
            move_selected_to_hand(state, &sel);
            Ok(CardPlayOutcome::default())
        }
        SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome::with_choice(req, resume_step)),
    }
}

fn move_card_to_result_pile_without_playing_fallback(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_handle: CardHandle,
) -> Result<(), KernelError> {
    let (is_dupe, exhaust_next, pile) = match state.card(card_handle) {
        Some(c) => (
            c.temp_flags.contains(CardTempFlags::IS_DUPE),
            c.temp_flags.contains(CardTempFlags::EXHAUST_ON_NEXT_PLAY),
            c.pile,
        ),
        None => return Ok(()),
    };
    if is_dupe {
        card_pile::remove(state, card_handle);
        return Ok(());
    }
    if exhaust_next || card_pile::card_has_keyword(state, content, card_handle, CardKeywords::EXHAUST)?
    {
        card_pile::exhaust(state, content, card_handle, false)?;
        return Ok(());
    }
    let departed_hand = pile == PileType::Hand;
    card_pile::add_sync(state, card_handle, PileType::Discard, CardPilePosition::Bottom);
    card_pile::notify_hand_departure(state, content, departed_hand)
}

fn count_non_temporary_debuffs(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
) -> Result<i32, KernelError> {
    let Some(c) = state.creature(target) else {
        return Ok(0);
    };
    let mut count = 0;
    for p in &c.powers {
        let meta = content.power_meta(p.id)?;
        if meta.flags.contains(PowerStaticFlags::IS_TEMPORARY) {
            continue;
        }
        let is_debuff = if meta.flags.contains(PowerStaticFlags::ALLOW_NEGATIVE) {
            p.amount < 0
        } else {
            meta.power_type == PowerType::Debuff
        };
        if is_debuff {
            count += 1;
        }
    }
    Ok(count)
}

fn discard_and_draw(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    cards: &[CardHandle],
    draw_count: i32,
) -> Result<(), KernelError> {
    if cards.is_empty() || hooks::is_over_or_ending(state) {
        return Ok(());
    }
    let departed_hand = cards
        .iter()
        .any(|&h| state.card(h).is_some_and(|c| c.pile == PileType::Hand));
    let mut sly_cards = SmallVec::<[CardHandle; 8]>::new();
    for &h in cards {
        if state.card(h).is_some_and(|c| c.pile == PileType::Hand)
            && card_pile::card_has_keyword(state, content, h, CardKeywords::SLY)?
        {
            sly_cards.push(h);
        }
    }
    for &h in cards {
        card_pile::add_sync(state, h, PileType::Discard, CardPilePosition::Bottom);
        state.player.counters.cards_discarded_this_turn += 1;
        hooks::after_card_discarded(state, content, h)?;
    }
    if draw_count > 0 {
        let _ = card_pile::draw(state, content, draw_count, false)?;
    }
    card_pile::notify_hand_departure(state, content, departed_hand)?;
    if !sly_cards.is_empty() {
        let _ = autoplay::from_cards(state, content, &sly_cards, None, false)?;
    }
    Ok(())
}

fn roll_alchemize_potion(state: &mut BattleState) -> Option<PotionId> {
    crate::potions::alchemize_roll_in_combat_potion(state)
}

pub fn resolved_type(state: &BattleState, card: CardHandle) -> Result<sts2_types::CardType, KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    if id == CardId::MadScience {
        return Ok(mad_science_resolved_type(state, card));
    }
    Ok(card_meta(id)?.card_type)
}

pub fn resolved_target_type(
    state: &BattleState,
    card: CardHandle,
) -> Result<sts2_types::TargetType, KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    match id {
        CardId::Shiv => {
            let has_fan = state
                .player_creature()
                .is_some_and(|c| c.powers.iter().any(|p| p.id == PowerId::FanOfKnivesPower && p.amount > 0));
            Ok(if has_fan {
                TargetType::AllEnemies
            } else {
                TargetType::AnyEnemy
            })
        }
        CardId::SovereignBlade => {
            let seeking_edge = state
                .player_creature()
                .is_some_and(|c| c.powers.iter().any(|p| p.id == PowerId::SeekingEdgePower && p.amount > 0));
            Ok(if seeking_edge {
                TargetType::AllEnemies
            } else {
                TargetType::AnyEnemy
            })
        }
        CardId::MadScience => Ok(mad_science_resolved_target_type(state, card)),
        _ => Ok(card_meta(id)?.target_type),
    }
}

pub fn thrash_damage_variable(state: &BattleState, card: CardHandle) -> Result<Dec, KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    let _ = card_meta(id)?;
    Ok(Dec::ZERO)
}

pub fn default_result_location(
    state: &BattleState,
    card: CardHandle,
    _is_auto_play: bool,
) -> Result<Option<(sts2_types::PileType, sts2_types::CardPilePosition)>, KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    let _ = card_meta(id)?;
    Ok(None)
}
