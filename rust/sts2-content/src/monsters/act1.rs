use smallvec::{smallvec, SmallVec};
use std::borrow::Cow;
use sts2_kernel::commands::{block, card_pile, creature as creature_cmd, damage::AttackBuilder, power};
use sts2_kernel::{
    hooks, BattleState, CardHandle, ContentDispatcher, CreatureHandle, FieldValue, HookArgs,
    HookResult, KernelError, PowerExtension,
};
use sts2_types::{
    AscensionLevel, CardId, CardPilePosition, CombatSide, Dec, GameMode, HookMask, HookSlot,
    MonsterId, MonsterMeta, PileType, PowerId, RunRngType, ValueProp,
};

static DEFAULT_MONSTER_META: MonsterMeta = MonsterMeta {
    hook_mask: HookMask::EMPTY,
};

static LAGAVULIN_MATRIARCH_META: MonsterMeta = MonsterMeta {
    hook_mask: HookMask::EMPTY.with(HookSlot::AfterDamageReceived),
};

static WATERFALL_GIANT_META: MonsterMeta = MonsterMeta {
    hook_mask: HookMask::EMPTY.with(HookSlot::ShouldDisappearFromDoom),
};

pub fn monster_meta(id: MonsterId) -> Result<&'static MonsterMeta, KernelError> {
    match id {
        MonsterId::LagavulinMatriarch => Ok(&LAGAVULIN_MATRIARCH_META),
        MonsterId::WaterfallGiant => Ok(&WATERFALL_GIANT_META),
        MonsterId::AssassinRubyRaider
        | MonsterId::AxeRubyRaider
        | MonsterId::BruteRubyRaider
        | MonsterId::BygoneEffigy
        | MonsterId::Byrdonis
        | MonsterId::CalcifiedCultist
        | MonsterId::CeremonialBeast
        | MonsterId::CorpseSlug
        | MonsterId::CrossbowRubyRaider
        | MonsterId::CubexConstruct
        | MonsterId::DampCultist
        | MonsterId::Flyconid
        | MonsterId::Fogmog
        | MonsterId::FossilStalker
        | MonsterId::FuzzyWurmCrawler
        | MonsterId::GremlinMerc
        | MonsterId::HauntedShip
        | MonsterId::Inklet
        | MonsterId::KinFollower
        | MonsterId::KinPriest
        | MonsterId::LeafSlimeM
        | MonsterId::LeafSlimeS
        | MonsterId::LivingFog
        | MonsterId::Mawler
        | MonsterId::Nibbit
        | MonsterId::PhantasmalGardener
        | MonsterId::PhrogParasite
        | MonsterId::PunchConstruct
        | MonsterId::Seapunk
        | MonsterId::SewerClam
        | MonsterId::ShrinkerBeetle
        | MonsterId::SkulkingColony
        | MonsterId::SlitheringStrangler
        | MonsterId::SludgeSpinner
        | MonsterId::SnappingJaxfruit
        | MonsterId::SoulFysh
        | MonsterId::TerrorEel
        | MonsterId::Toadpole
        | MonsterId::TrackerRubyRaider
        | MonsterId::TwigSlimeM
        | MonsterId::TwigSlimeS
        | MonsterId::TwoTailedRat
        | MonsterId::Vantom
        | MonsterId::VineShambler => Ok(&DEFAULT_MONSTER_META),
        _ => Err(KernelError::Unsupported {
            kind: "Monster",
            id: id.model_id_str().to_owned(),
        }),
    }
}

#[inline]
fn is_dread(state: &BattleState) -> bool {
    state.static_ctx.game_mode == GameMode::Dread
}

#[inline]
fn asc(state: &BattleState, level: AscensionLevel, high: i32, low: i32) -> i32 {
    state.ascension_value(level, high, low)
}

#[inline]
fn dec_asc(state: &BattleState, level: AscensionLevel, high: i32, low: i32) -> Dec {
    Dec::from_i32(asc(state, level, high, low))
}

fn read_monster_bool(
    state: &BattleState,
    creature: CreatureHandle,
    priv_key: &str,
    setup_key: &str,
) -> bool {
    let Some(m) = state.creature(creature).and_then(|c| c.monster.as_ref()) else {
        return false;
    };
    if let Some(FieldValue::Bool(b)) = m.private_fields.get(priv_key) {
        return *b;
    }
    if let Some(FieldValue::Bool(b)) = m.setup_fields.get(priv_key) {
        return *b;
    }
    if let Some(FieldValue::Bool(b)) = m.setup_fields.get(setup_key) {
        return *b;
    }
    false
}

fn read_monster_int(
    state: &BattleState,
    creature: CreatureHandle,
    priv_key: &str,
    setup_key: &str,
    default: i32,
) -> i32 {
    let Some(m) = state.creature(creature).and_then(|c| c.monster.as_ref()) else {
        return default;
    };
    if let Some(FieldValue::Int(v)) = m.private_fields.get(priv_key) {
        return *v;
    }
    if let Some(FieldValue::Int(v)) = m.setup_fields.get(priv_key) {
        return *v;
    }
    if let Some(FieldValue::Int(v)) = m.setup_fields.get(setup_key) {
        return *v;
    }
    default
}

fn has_monster_field(state: &BattleState, creature: CreatureHandle, key: &str) -> bool {
    state
        .creature(creature)
        .and_then(|c| c.monster.as_ref())
        .is_some_and(|m| m.private_fields.contains_key(key))
}

fn set_monster_bool(state: &mut BattleState, creature: CreatureHandle, key: &'static str, val: bool) {
    if let Some(m) = state.creature_mut(creature).and_then(|c| c.monster.as_mut()) {
        m.private_fields
            .insert(Cow::Borrowed(key), FieldValue::Bool(val));
    }
}

fn set_monster_int(state: &mut BattleState, creature: CreatureHandle, key: &'static str, val: i32) {
    if let Some(m) = state.creature_mut(creature).and_then(|c| c.monster.as_mut()) {
        m.private_fields
            .insert(Cow::Borrowed(key), FieldValue::Int(val));
    }
}

fn set_monster_string(state: &mut BattleState, creature: CreatureHandle, key: &'static str, val: &str) {
    if let Some(m) = state.creature_mut(creature).and_then(|c| c.monster.as_mut()) {
        m.private_fields
            .insert(Cow::Borrowed(key), FieldValue::String(val.to_owned()));
    }
}

fn has_power(state: &BattleState, creature: CreatureHandle, power_id: PowerId) -> bool {
    state
        .creature(creature)
        .is_some_and(|c| c.powers.iter().any(|p| p.id == power_id))
}

fn power_amount(state: &BattleState, creature: CreatureHandle, power_id: PowerId) -> i32 {
    state
        .creature(creature)
        .and_then(|c| c.powers.iter().find(|p| p.id == power_id))
        .map_or(0, |p| p.amount)
}

fn player_targets(state: &BattleState) -> SmallVec<[CreatureHandle; 2]> {
    state
        .ally_handles
        .iter()
        .copied()
        .filter(|&h| {
            state.creature(h).is_some_and(|c| {
                c.is_alive() && (c.is_player || (!c.is_monster() && !c.is_pet()))
            })
        })
        .collect()
}

fn apply_power_to_players(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    source: CreatureHandle,
    power_id: PowerId,
    amount: i32,
) -> Result<(), KernelError> {
    let targets = player_targets(state);
    let amt = Dec::from_i32(amount);
    for t in targets {
        power::apply(state, content, power_id, t, amt, Some(source), None)?;
    }
    Ok(())
}

fn apply_power_to_self(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
    power_id: PowerId,
    amount: i32,
) -> Result<(), KernelError> {
    power::apply(
        state,
        content,
        power_id,
        creature,
        Dec::from_i32(amount),
        Some(creature),
        None,
    )?;
    Ok(())
}

fn next_free_slot(state: &BattleState, slots: &[&'static str], reverse: bool) -> Option<&'static str> {
    let slot_taken = |slot: &str| {
        state
            .enemy_handles
            .iter()
            .filter_map(|&h| state.creature(h))
            .any(|c| c.slot_name.as_deref() == Some(slot))
    };
    if reverse {
        slots.iter().copied().rev().find(|&s| !slot_taken(s))
    } else {
        slots.iter().copied().find(|&s| !slot_taken(s))
    }
}

const BOMB_SLOTS: &[&str] = &["bomb1", "bomb2", "bomb3", "bomb4", "bomb5"];
const RAT_SLOTS: &[&str] = &["first", "second", "third", "fourth", "fifth"];

pub fn monster_initial_hp_range(
    state: &BattleState,
    monster_id: MonsterId,
) -> Result<(i32, i32), KernelError> {
    let dread = is_dread(state);
    let t = |high, low| asc(state, AscensionLevel::ToughEnemies, high, low);
    match monster_id {
        MonsterId::AssassinRubyRaider => Ok((t(19, 18), t(24, 23))),
        MonsterId::AxeRubyRaider => Ok((t(21, 20), t(23, 22))),
        MonsterId::BruteRubyRaider => Ok((t(31, 30), t(34, 33))),
        MonsterId::BygoneEffigy => {
            let hp = t(132, 127);
            Ok((hp, hp))
        }
        MonsterId::Byrdonis => Ok((t(90, 81), t(90, 84))),
        MonsterId::CalcifiedCultist => Ok((t(39, 38), t(42, 41))),
        MonsterId::CeremonialBeast => {
            let hp = t(262, 252) + if dread { 30 } else { 0 };
            Ok((hp, hp))
        }
        MonsterId::CorpseSlug => Ok((t(27, 25), t(29, 27))),
        MonsterId::CrossbowRubyRaider => Ok((t(19, 18), t(22, 21))),
        MonsterId::CubexConstruct => {
            let hp = t(70, 65);
            Ok((hp, hp))
        }
        MonsterId::DampCultist => Ok((t(52, 51), t(54, 53))),
        MonsterId::Flyconid => Ok((t(51, 47), t(53, 49))),
        MonsterId::Fogmog => {
            let hp = t(78, 74);
            Ok((hp, hp))
        }
        MonsterId::FossilStalker => Ok((t(54, 51), t(56, 53))),
        MonsterId::FuzzyWurmCrawler => Ok((t(58, 55), t(59, 57))),
        MonsterId::GremlinMerc => Ok((t(51, 47), t(53, 49))),
        MonsterId::HauntedShip => {
            let hp = t(67, 63);
            Ok((hp, hp))
        }
        MonsterId::Inklet => Ok((t(12, 11), t(18, 17))),
        MonsterId::KinFollower => Ok((t(62, 58), t(63, 59))),
        MonsterId::KinPriest => {
            let hp = t(199, 190);
            Ok((hp, hp))
        }
        MonsterId::LagavulinMatriarch => {
            let hp = t(233, 222) + if dread { 22 } else { 0 };
            Ok((hp, hp))
        }
        MonsterId::LeafSlimeM => Ok((t(33, 32), t(36, 35))),
        MonsterId::LeafSlimeS => Ok((t(12, 11), t(16, 15))),
        MonsterId::LivingFog => {
            let hp = t(82, 80);
            Ok((hp, hp))
        }
        MonsterId::Mawler => {
            let hp = t(76, 72);
            Ok((hp, hp))
        }
        MonsterId::Nibbit => Ok((t(44, 42), t(48, 46))),
        MonsterId::PhantasmalGardener => Ok((t(27, 26), t(32, 31))),
        MonsterId::PhrogParasite => Ok((t(66, 61), t(68, 64))),
        MonsterId::PunchConstruct => {
            let hp = t(60, 55);
            Ok((hp, hp))
        }
        MonsterId::Seapunk => Ok((t(47, 44), t(49, 46))),
        MonsterId::SewerClam => {
            let hp = t(58, 56);
            Ok((hp, hp))
        }
        MonsterId::ShrinkerBeetle => Ok((t(40, 38), t(42, 40))),
        MonsterId::SkulkingColony => {
            let hp = t(80, 75);
            Ok((hp, hp))
        }
        MonsterId::SlitheringStrangler => Ok((t(54, 53), t(56, 55))),
        MonsterId::SludgeSpinner => Ok((t(41, 37), t(42, 39))),
        MonsterId::SnappingJaxfruit => Ok((t(34, 31), t(36, 33))),
        MonsterId::SoulFysh => {
            let hp = t(221, 211);
            Ok((hp, hp))
        }
        MonsterId::TerrorEel => {
            let hp = t(150, 140);
            Ok((hp, hp))
        }
        MonsterId::Toadpole => Ok((t(22, 21), t(26, 25))),
        MonsterId::TrackerRubyRaider => Ok((t(22, 21), t(26, 25))),
        MonsterId::TwigSlimeM => Ok((t(27, 26), t(29, 28))),
        MonsterId::TwigSlimeS => Ok((t(8, 7), t(12, 11))),
        MonsterId::TwoTailedRat => Ok((t(18, 17), t(22, 21))),
        MonsterId::Vantom => {
            let hp = t(183, 173);
            Ok((hp, hp))
        }
        MonsterId::VineShambler => {
            let hp = t(64, 61);
            Ok((hp, hp))
        }
        MonsterId::WaterfallGiant => {
            let hp = t(250, 240);
            Ok((hp, hp))
        }
        _ => Err(KernelError::Unsupported {
            kind: "Monster",
            id: monster_id.model_id_str().to_owned(),
        }),
    }
}

fn is_move_state(mid: MonsterId, state_id: &str) -> bool {
    !matches!(
        (mid, state_id),
        (
            MonsterId::Nibbit | MonsterId::PhantasmalGardener | MonsterId::Toadpole,
            "INIT_MOVE"
        ) | (MonsterId::Flyconid, "INITIAL" | "RAND")
            | (MonsterId::Fogmog, "BRANCH")
            | (
                MonsterId::FossilStalker
                    | MonsterId::Inklet
                    | MonsterId::LeafSlimeS
                    | MonsterId::Mawler
                    | MonsterId::SludgeSpinner
                    | MonsterId::TwigSlimeM
                    | MonsterId::TwoTailedRat,
                "RAND"
            )
            | (MonsterId::LagavulinMatriarch, "SLEEP_BRANCH")
            | (MonsterId::SlitheringStrangler, "rand")
    )
}

fn must_perform_once_before_transitioning(mid: MonsterId, state_id: &str) -> bool {
    matches!(
        (mid, state_id),
        (MonsterId::CeremonialBeast, "STUN_MOVE")
            | (MonsterId::WaterfallGiant, "ABOUT_TO_BLOW_MOVE")
            | (_, "STUNNED")
    )
}

pub fn monster_setup_for_combat(
    state: &mut BattleState,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    let mid = state
        .creature(creature)
        .and_then(|c| c.monster_id)
        .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?;
    let _ = monster_meta(mid)?;

    let corpse_starter_idx = read_monster_int(
        state,
        creature,
        "CorpseSlug._starterMoveIdx",
        "StarterMoveIdx",
        0,
    );
    let middle_inklet = read_monster_bool(state, creature, "Inklet._middleInklet", "MiddleInklet");
    let kin_index = if has_monster_field(state, creature, "KinFollower._index")
        || state
            .creature(creature)
            .and_then(|c| c.monster.as_ref())
            .is_some_and(|m| m.setup_fields.contains_key("KinFollower._index"))
    {
        read_monster_int(state, creature, "KinFollower._index", "Index", 1)
    } else if read_monster_bool(state, creature, "KinFollower._startsWithDance", "StartsWithDance") {
        0
    } else {
        1
    };
    let punch_fast = read_monster_bool(
        state,
        creature,
        "PunchConstruct._startsWithFastPunch",
        "StartsWithFastPunch",
    );
    let rat_starter = read_monster_int(
        state,
        creature,
        "TwoTailedRat._starterMoveIndex",
        "StarterMoveIndex",
        -1,
    );

    let initial_state: &'static str = match mid {
        MonsterId::AssassinRubyRaider => "KILLSHOT_MOVE",
        MonsterId::AxeRubyRaider => "SWING_1",
        MonsterId::BruteRubyRaider => "BEAT_MOVE",
        MonsterId::BygoneEffigy => "SLEEP_MOVE",
        MonsterId::Byrdonis => "SWOOP_MOVE",
        MonsterId::CalcifiedCultist => "INCANTATION_MOVE",
        MonsterId::CeremonialBeast => "STAMP_MOVE",
        MonsterId::CorpseSlug => match corpse_starter_idx.rem_euclid(3) {
            0 => "WHIP_SLAP_MOVE",
            1 => "GLOMP_MOVE",
            _ => "GOOP_MOVE",
        },
        MonsterId::CrossbowRubyRaider => "RELOAD_MOVE",
        MonsterId::CubexConstruct => "CHARGE_UP_MOVE",
        MonsterId::DampCultist => "INCANTATION_MOVE",
        MonsterId::Flyconid => "INITIAL",
        MonsterId::Fogmog => "ILLUSION_MOVE",
        MonsterId::FossilStalker => "LATCH_MOVE",
        MonsterId::FuzzyWurmCrawler => "FIRST_ACID_GOOP",
        MonsterId::GremlinMerc => "GIMME_MOVE",
        MonsterId::HauntedShip => "HAUNT_MOVE",
        MonsterId::Inklet => {
            if middle_inklet {
                "WHIRLWIND_MOVE"
            } else {
                "JAB_MOVE"
            }
        }
        MonsterId::KinFollower => match kin_index {
            0 => "POWER_DANCE_MOVE",
            1 => "QUICK_SLASH_MOVE",
            2 => "BOOMERANG_MOVE",
            _ => {
                return Err(KernelError::InvalidState(format!(
                    "KinFollower index out of range: {kin_index}"
                )));
            }
        },
        MonsterId::KinPriest => "ORB_OF_FRAILTY_MOVE",
        MonsterId::LagavulinMatriarch => "SLEEP_MOVE",
        MonsterId::LeafSlimeM => "STICKY_SHOT",
        MonsterId::LeafSlimeS => "RAND",
        MonsterId::LivingFog => "ADVANCED_GAS_MOVE",
        MonsterId::Mawler => "CLAW_MOVE",
        MonsterId::Nibbit => "INIT_MOVE",
        MonsterId::PhantasmalGardener => "INIT_MOVE",
        MonsterId::PhrogParasite => "INFECT_MOVE",
        MonsterId::PunchConstruct => {
            if punch_fast {
                "FAST_PUNCH_MOVE"
            } else {
                "READY_MOVE"
            }
        }
        MonsterId::Seapunk => "SEA_KICK_MOVE",
        MonsterId::SewerClam => "JET_MOVE",
        MonsterId::ShrinkerBeetle => "SHRINKER_MOVE",
        MonsterId::SkulkingColony => "ZOOM_MOVE",
        MonsterId::SlitheringStrangler => "CONSTRICT",
        MonsterId::SludgeSpinner => "OIL_SPRAY_MOVE",
        MonsterId::SnappingJaxfruit => "ENERGY_ORB_MOVE",
        MonsterId::SoulFysh => "BECKON_MOVE",
        MonsterId::TerrorEel => "CRASH_MOVE",
        MonsterId::Toadpole => "INIT_MOVE",
        MonsterId::TrackerRubyRaider => "TRACK_MOVE",
        MonsterId::TwigSlimeM => "STICKY_SHOT_MOVE",
        MonsterId::TwigSlimeS => "TACKLE_MOVE",
        MonsterId::TwoTailedRat => {
            if rat_starter == -1 {
                "RAND"
            } else {
                match rat_starter.rem_euclid(3) {
                    0 => "SCRATCH_MOVE",
                    1 => "DISEASE_BITE_MOVE",
                    _ => "SCREECH_MOVE",
                }
            }
        }
        MonsterId::Vantom => "INK_BLOT_MOVE",
        MonsterId::VineShambler => "SWIPE_MOVE",
        MonsterId::WaterfallGiant => "PRESSURIZE_MOVE",
        _ => {
            return Err(KernelError::Unsupported {
                kind: "Monster",
                id: mid.model_id_str().to_owned(),
            });
        }
    };

    match mid {
        MonsterId::CeremonialBeast => {
            if !has_monster_field(state, creature, "CeremonialBeast._beastCryState") {
                set_monster_string(state, creature, "CeremonialBeast._beastCryState", "BEAST_CRY_MOVE");
            }
        }
        MonsterId::CorpseSlug => {
            if !has_monster_field(state, creature, "CorpseSlug._isRavenous") {
                set_monster_bool(state, creature, "CorpseSlug._isRavenous", false);
            }
            if !has_monster_field(state, creature, "CorpseSlug._starterMoveIdx") {
                set_monster_int(state, creature, "CorpseSlug._starterMoveIdx", corpse_starter_idx);
            }
        }
        MonsterId::KinFollower => {
            if !has_monster_field(state, creature, "KinFollower._index") {
                set_monster_int(state, creature, "KinFollower._index", kin_index);
            }
        }
        MonsterId::LagavulinMatriarch => {
            if !has_monster_field(state, creature, "LagavulinMatriarch._isAwake") {
                set_monster_bool(state, creature, "LagavulinMatriarch._isAwake", false);
            }
            if !has_monster_field(state, creature, "LagavulinMatriarch._isShellAwake") {
                set_monster_bool(state, creature, "LagavulinMatriarch._isShellAwake", false);
            }
        }
        MonsterId::LivingFog => {
            if !has_monster_field(state, creature, "LivingFog._bloatAmount") {
                set_monster_int(state, creature, "LivingFog._bloatAmount", 1);
            }
        }
        MonsterId::PhantasmalGardener => {
            if !has_monster_field(state, creature, "PhantasmalGardener._enlargeTriggers") {
                set_monster_int(state, creature, "PhantasmalGardener._enlargeTriggers", 0);
            }
        }
        MonsterId::SoulFysh => {
            if !has_monster_field(state, creature, "SoulFysh._isInvisible") {
                set_monster_bool(state, creature, "SoulFysh._isInvisible", false);
            }
        }
        MonsterId::Toadpole => {
            if !has_monster_field(state, creature, "Toadpole._isFront") {
                let is_front = read_monster_bool(state, creature, "Toadpole._isFront", "IsFront");
                set_monster_bool(state, creature, "Toadpole._isFront", is_front);
            }
        }
        MonsterId::TwoTailedRat => {
            if !has_monster_field(state, creature, "TwoTailedRat._starterMoveIndex") {
                set_monster_int(state, creature, "TwoTailedRat._starterMoveIndex", rat_starter);
            }
            if !has_monster_field(state, creature, "TwoTailedRat._turnsUntilSummonable") {
                set_monster_int(state, creature, "TwoTailedRat._turnsUntilSummonable", 2);
            }
            if !has_monster_field(state, creature, "TwoTailedRat._callForBackupCount") {
                set_monster_int(state, creature, "TwoTailedRat._callForBackupCount", 0);
            }
        }
        MonsterId::WaterfallGiant => {
            if !has_monster_field(state, creature, "WaterfallGiant._steamEruptionDamage") {
                set_monster_int(state, creature, "WaterfallGiant._steamEruptionDamage", 0);
            }
        }
        _ => {}
    }

    let initial_logged = is_move_state(mid, initial_state);
    if let Some(c) = state.creature_mut(creature) {
        if let Some(m) = &mut c.monster {
            if m.current_state_id.is_empty() {
                m.current_state_id = Cow::Borrowed(initial_state);
                if initial_logged && m.state_log_ids.is_empty() {
                    m.state_log_ids.push(Cow::Borrowed(initial_state));
                }
            }
        }
    }
    Ok(())
}

pub fn monster_after_added_to_room(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    let mid = state
        .creature(creature)
        .and_then(|c| c.monster_id)
        .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?;
    let _ = monster_meta(mid)?;

    match mid {
        MonsterId::BygoneEffigy => {
            apply_power_to_self(state, content, creature, PowerId::SlowPower, 1)?;
        }
        MonsterId::Byrdonis => {
            apply_power_to_self(state, content, creature, PowerId::TerritorialPower, 1)?;
        }
        MonsterId::CorpseSlug => {
            let amt = asc(state, AscensionLevel::DeadlyEnemies, 5, 4);
            apply_power_to_self(state, content, creature, PowerId::RavenousPower, amt)?;
        }
        MonsterId::CubexConstruct => {
            block::gain_block(
                state,
                content,
                creature,
                Dec::from_i32(13),
                ValueProp::MOVE,
                None,
                None,
            )?;
            apply_power_to_self(state, content, creature, PowerId::ArtifactPower, 1)?;
        }
        MonsterId::FossilStalker => {
            apply_power_to_self(state, content, creature, PowerId::SuckPower, 3)?;
        }
        MonsterId::GremlinMerc => {
            apply_power_to_self(state, content, creature, PowerId::SurprisePower, 1)?;
            let players: SmallVec<[CreatureHandle; 2]> = state
                .ally_handles
                .iter()
                .copied()
                .filter(|&h| state.creature(h).is_some_and(|c| c.is_player))
                .collect();
            for p_handle in players {
                if let Some(p_idx) = power::apply(
                    state,
                    content,
                    PowerId::ThieveryPower,
                    creature,
                    Dec::from_i32(20),
                    Some(creature),
                    None,
                )? {
                    let existing_ext = state
                        .creature(creature)
                        .and_then(|c| c.powers.get(p_idx))
                        .and_then(|p| p.ext_index);
                    let ext_idx = match existing_ext {
                        Some(idx) => idx,
                        None => {
                            let idx = state.power_extensions.len() as u32;
                            state.power_extensions.push(PowerExtension::default());
                            if let Some(pw) = state
                                .creature_mut(creature)
                                .and_then(|c| c.powers.get_mut(p_idx))
                            {
                                pw.ext_index = Some(idx);
                            }
                            idx
                        }
                    };
                    state.power_extensions[ext_idx as usize]
                        .private_fields
                        .insert(
                            Cow::Borrowed("ThieveryPower.<Target>k__BackingField"),
                            FieldValue::CreatureHandle(p_handle.0 as i32),
                        );
                }
            }
        }
        MonsterId::Inklet => {
            apply_power_to_self(state, content, creature, PowerId::SlipperyPower, 1)?;
        }
        MonsterId::KinFollower => {
            apply_power_to_self(state, content, creature, PowerId::MinionPower, 1)?;
        }
        MonsterId::LagavulinMatriarch => {
            set_monster_bool(state, creature, "LagavulinMatriarch._isAwake", false);
            apply_power_to_self(state, content, creature, PowerId::PlatingPower, 12)?;
            apply_power_to_self(state, content, creature, PowerId::AsleepPower, 3)?;
        }
        MonsterId::PhantasmalGardener => {
            let skittish = asc(state, AscensionLevel::ToughEnemies, 7, 6);
            apply_power_to_self(state, content, creature, PowerId::SkittishPower, skittish)?;
        }
        MonsterId::PhrogParasite => {
            apply_power_to_self(state, content, creature, PowerId::InfestedPower, 4)?;
        }
        MonsterId::PunchConstruct => {
            apply_power_to_self(state, content, creature, PowerId::ArtifactPower, 1)?;
            let reduction = read_monster_int(
                state,
                creature,
                "PunchConstruct._startingHpReduction",
                "StartingHpReduction",
                0,
            );
            if reduction > 0 {
                if let Some(c) = state.creature_mut(creature) {
                    c.current_hp = (c.current_hp - reduction).max(1);
                }
            }
        }
        MonsterId::SewerClam => {
            let plating = asc(state, AscensionLevel::ToughEnemies, 9, 8);
            apply_power_to_self(state, content, creature, PowerId::PlatingPower, plating)?;
        }
        MonsterId::SkulkingColony => {
            apply_power_to_self(state, content, creature, PowerId::HardenedShellPower, 20)?;
        }
        MonsterId::TerrorEel => {
            let shriek = asc(state, AscensionLevel::ToughEnemies, 75, 70);
            apply_power_to_self(state, content, creature, PowerId::ShriekPower, shriek)?;
        }
        MonsterId::Vantom => {
            let slippery = asc(state, AscensionLevel::ToughEnemies, 9, 8)
                + if is_dread(state) { 2 } else { 0 };
            apply_power_to_self(state, content, creature, PowerId::SlipperyPower, slippery)?;
        }
        MonsterId::WaterfallGiant => {
            let pg = asc(state, AscensionLevel::DeadlyEnemies, 23, 20);
            set_monster_int(state, creature, "WaterfallGiant._currentPressureGunDamage", pg);
        }
        _ => {}
    }
    Ok(())
}

#[derive(Clone, Copy)]
enum RepeatRule {
    CannotRepeat,
    CanRepeatForever,
    UseOnlyOnce,
    CanRepeatXTimes(usize),
}

#[derive(Clone, Copy)]
struct RandomBranch {
    state_id: &'static str,
    repeat: RepeatRule,
    cooldown: usize,
    weight: f32,
}

fn eval_branch_weight(state_log_ids: &[Cow<'static, str>], b: RandomBranch) -> f32 {
    let mut repeat_weight = 1.0_f32;
    match b.repeat {
        RepeatRule::UseOnlyOnce => {
            if state_log_ids.iter().any(|s| s.as_ref() == b.state_id) {
                repeat_weight = 0.0;
            }
        }
        RepeatRule::CannotRepeat | RepeatRule::CanRepeatXTimes(_) => {
            let limit = match b.repeat {
                RepeatRule::CannotRepeat => 1usize,
                RepeatRule::CanRepeatXTimes(k) => k,
                _ => unreachable!(),
            };
            if state_log_ids.len() < limit {
                repeat_weight = 1.0;
            } else {
                repeat_weight = 0.0;
                for checked in 0..limit {
                    if state_log_ids[state_log_ids.len() - 1 - checked].as_ref() != b.state_id {
                        repeat_weight = 1.0;
                        break;
                    }
                }
            }
        }
        RepeatRule::CanRepeatForever => {}
    }

    if b.cooldown > 0
        && state_log_ids
            .iter()
            .rev()
            .take(b.cooldown)
            .any(|s| s.as_ref() == b.state_id)
    {
        return 0.0;
    }

    repeat_weight * b.weight
}

fn pick_random_branch(
    state: &mut BattleState,
    creature: CreatureHandle,
    branch_id: &str,
    branches: &[RandomBranch],
) -> Result<&'static str, KernelError> {
    let weights: SmallVec<[(&'static str, f32); 4]> = {
        let log = &state
            .creature(creature)
            .and_then(|c| c.monster.as_ref())
            .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?
            .state_log_ids;
        branches
            .iter()
            .map(|&b| (b.state_id, eval_branch_weight(log, b)))
            .collect()
    };

    let total_weight = weights.iter().map(|&(_, w)| w as f64).sum::<f64>() as f32;
    let mut random_weight = state
        .run_rngs
        .get_mut(RunRngType::MonsterAi)
        .next_f32_range(0.0, total_weight)
        .map_err(|e| KernelError::InvalidState(e.to_string()))?;

    for (state_id, w) in weights {
        random_weight -= w;
        if random_weight <= 0.0 {
            return Ok(state_id);
        }
    }

    Err(KernelError::InvalidState(format!(
        "No valid state found in RandomBranchState {branch_id}!"
    )))
}

fn two_tailed_rat_can_summon(state: &BattleState, creature: CreatureHandle) -> bool {
    let turns_until = read_monster_int(
        state,
        creature,
        "TwoTailedRat._turnsUntilSummonable",
        "TurnsUntilSummonable",
        2,
    );
    let call_count = read_monster_int(
        state,
        creature,
        "TwoTailedRat._callForBackupCount",
        "CallForBackupCount",
        0,
    );
    if turns_until > 0 || call_count >= 3 || next_free_slot(state, RAT_SLOTS, true).is_none() {
        return false;
    }
    state
        .enemy_handles
        .iter()
        .copied()
        .filter(|&h| h != creature)
        .filter_map(|h| state.creature(h))
        .filter(|c| c.monster_id == Some(MonsterId::TwoTailedRat))
        .all(|c| {
            c.monster
                .as_ref()
                .and_then(|m| m.next_move_id.as_deref())
                != Some("CALL_FOR_BACKUP_MOVE")
        })
}

fn next_state_step(
    state: &mut BattleState,
    creature: CreatureHandle,
    mid: MonsterId,
    cur: &str,
) -> Result<&'static str, KernelError> {
    match mid {
        MonsterId::AssassinRubyRaider => Ok("KILLSHOT_MOVE"),
        MonsterId::AxeRubyRaider => Ok(match cur {
            "SWING_1" => "SWING_2",
            "SWING_2" => "BIG_SWING",
            _ => "SWING_1",
        }),
        MonsterId::BruteRubyRaider => Ok(match cur {
            "BEAT_MOVE" => "ROAR_MOVE",
            _ => "BEAT_MOVE",
        }),
        MonsterId::BygoneEffigy => Ok(match cur {
            "SLEEP_MOVE" => "WAKE_MOVE",
            _ => "SLASHES_MOVE",
        }),
        MonsterId::Byrdonis => Ok(match cur {
            "SWOOP_MOVE" => "PECK_MOVE",
            _ => "SWOOP_MOVE",
        }),
        MonsterId::CalcifiedCultist | MonsterId::DampCultist => Ok("DARK_STRIKE_MOVE"),
        MonsterId::CeremonialBeast => Ok(match cur {
            "STAMP_MOVE" | "PLOW_MOVE" => "PLOW_MOVE",
            "STUN_MOVE" | "CRUSH_MOVE" => "BEAST_CRY_MOVE",
            "BEAST_CRY_MOVE" => "STOMP_MOVE",
            "STOMP_MOVE" => "CRUSH_MOVE",
            _ => "PLOW_MOVE",
        }),
        MonsterId::CorpseSlug => Ok(match cur {
            "WHIP_SLAP_MOVE" => "GLOMP_MOVE",
            "GLOMP_MOVE" => "GOOP_MOVE",
            _ => "WHIP_SLAP_MOVE",
        }),
        MonsterId::CrossbowRubyRaider => Ok(match cur {
            "RELOAD_MOVE" => "FIRE_MOVE",
            _ => "RELOAD_MOVE",
        }),
        MonsterId::CubexConstruct => Ok(match cur {
            "CHARGE_UP_MOVE" | "EXPEL_MOVE" => "REPEATER_BLAST_MOVE",
            "REPEATER_BLAST_MOVE" => "REPEATER_BLAST_MOVE_2",
            "REPEATER_BLAST_MOVE_2" => "EXPEL_MOVE",
            _ => "REPEATER_BLAST_MOVE",
        }),
        MonsterId::Flyconid => match cur {
            "INITIAL" => pick_random_branch(
                state,
                creature,
                "INITIAL",
                &[
                    RandomBranch {
                        state_id: "FRAIL_SPORES_MOVE",
                        repeat: RepeatRule::CannotRepeat,
                        cooldown: 2,
                        weight: 1.0,
                    },
                    RandomBranch {
                        state_id: "SMASH_MOVE",
                        repeat: RepeatRule::CannotRepeat,
                        cooldown: 0,
                        weight: 1.0,
                    },
                ],
            ),
            "RAND" => pick_random_branch(
                state,
                creature,
                "RAND",
                &[
                    RandomBranch {
                        state_id: "VULNERABLE_SPORES_MOVE",
                        repeat: RepeatRule::CannotRepeat,
                        cooldown: 3,
                        weight: 1.0,
                    },
                    RandomBranch {
                        state_id: "FRAIL_SPORES_MOVE",
                        repeat: RepeatRule::CannotRepeat,
                        cooldown: 2,
                        weight: 1.0,
                    },
                    RandomBranch {
                        state_id: "SMASH_MOVE",
                        repeat: RepeatRule::CannotRepeat,
                        cooldown: 0,
                        weight: 1.0,
                    },
                ],
            ),
            _ => Ok("RAND"),
        },
        MonsterId::Fogmog => match cur {
            "ILLUSION_MOVE" | "HEADBUTT_MOVE" => Ok("SWIPE_MOVE"),
            "SWIPE_MOVE" => Ok("BRANCH"),
            "SWIPE_RANDOM_MOVE" => Ok("HEADBUTT_MOVE"),
            "BRANCH" => pick_random_branch(
                state,
                creature,
                "BRANCH",
                &[
                    RandomBranch {
                        state_id: "SWIPE_RANDOM_MOVE",
                        repeat: RepeatRule::CannotRepeat,
                        cooldown: 0,
                        weight: 0.4,
                    },
                    RandomBranch {
                        state_id: "HEADBUTT_MOVE",
                        repeat: RepeatRule::CannotRepeat,
                        cooldown: 0,
                        weight: 0.6,
                    },
                ],
            ),
            _ => Ok("SWIPE_MOVE"),
        },
        MonsterId::FossilStalker => match cur {
            "RAND" => pick_random_branch(
                state,
                creature,
                "RAND",
                &[
                    RandomBranch {
                        state_id: "LATCH_MOVE",
                        repeat: RepeatRule::CanRepeatXTimes(2),
                        cooldown: 0,
                        weight: 1.0,
                    },
                    RandomBranch {
                        state_id: "TACKLE_MOVE",
                        repeat: RepeatRule::CanRepeatXTimes(2),
                        cooldown: 0,
                        weight: 1.0,
                    },
                    RandomBranch {
                        state_id: "LASH_MOVE",
                        repeat: RepeatRule::CanRepeatXTimes(2),
                        cooldown: 0,
                        weight: 1.0,
                    },
                ],
            ),
            _ => Ok("RAND"),
        },
        MonsterId::FuzzyWurmCrawler => Ok(match cur {
            "FIRST_ACID_GOOP" => "INHALE",
            "INHALE" => "ACID_GOOP",
            _ => "FIRST_ACID_GOOP",
        }),
        MonsterId::GremlinMerc => Ok(match cur {
            "GIMME_MOVE" => "DOUBLE_SMASH_MOVE",
            "DOUBLE_SMASH_MOVE" => "HEHE_MOVE",
            _ => "GIMME_MOVE",
        }),
        MonsterId::HauntedShip => Ok(match cur {
            "HAUNT_MOVE" | "STOMP_MOVE" => "SWIPE_MOVE",
            "SWIPE_MOVE" => "STOMP_MOVE",
            _ => "SWIPE_MOVE",
        }),
        MonsterId::Inklet => match cur {
            "JAB_MOVE" => Ok("RAND"),
            "RAND" => pick_random_branch(
                state,
                creature,
                "RAND",
                &[
                    RandomBranch {
                        state_id: "PIERCING_GAZE_MOVE",
                        repeat: RepeatRule::CannotRepeat,
                        cooldown: 0,
                        weight: 1.0,
                    },
                    RandomBranch {
                        state_id: "WHIRLWIND_MOVE",
                        repeat: RepeatRule::CannotRepeat,
                        cooldown: 0,
                        weight: 1.0,
                    },
                ],
            ),
            _ => Ok("JAB_MOVE"),
        },
        MonsterId::KinFollower => Ok(match cur {
            "QUICK_SLASH_MOVE" => "BOOMERANG_MOVE",
            "BOOMERANG_MOVE" => "POWER_DANCE_MOVE",
            _ => "QUICK_SLASH_MOVE",
        }),
        MonsterId::KinPriest => Ok(match cur {
            "ORB_OF_FRAILTY_MOVE" => "ORB_OF_WEAKNESS_MOVE",
            "ORB_OF_WEAKNESS_MOVE" => "BEAM_MOVE",
            "BEAM_MOVE" => "RITUAL_MOVE",
            _ => "ORB_OF_FRAILTY_MOVE",
        }),
        MonsterId::LagavulinMatriarch => match cur {
            "SLEEP_MOVE" => Ok("SLEEP_BRANCH"),
            "SLEEP_BRANCH" => {
                if has_power(state, creature, PowerId::AsleepPower) {
                    Ok("SLEEP_MOVE")
                } else {
                    Ok("SLASH_MOVE")
                }
            }
            "SLASH_MOVE" => Ok("DISEMBOWEL_MOVE"),
            "DISEMBOWEL_MOVE" => Ok("SLASH2_MOVE"),
            "SLASH2_MOVE" => Ok("SOUL_SIPHON_MOVE"),
            _ => Ok("SLASH_MOVE"),
        },
        MonsterId::LeafSlimeM => Ok(match cur {
            "STICKY_SHOT" => "CLUMP_SHOT",
            _ => "STICKY_SHOT",
        }),
        MonsterId::LeafSlimeS => match cur {
            "RAND" => pick_random_branch(
                state,
                creature,
                "RAND",
                &[
                    RandomBranch {
                        state_id: "TACKLE_MOVE",
                        repeat: RepeatRule::CannotRepeat,
                        cooldown: 0,
                        weight: 1.0,
                    },
                    RandomBranch {
                        state_id: "GOOP_MOVE",
                        repeat: RepeatRule::CannotRepeat,
                        cooldown: 0,
                        weight: 1.0,
                    },
                ],
            ),
            _ => Ok("RAND"),
        },
        MonsterId::LivingFog => Ok(match cur {
            "ADVANCED_GAS_MOVE" | "SUPER_GAS_BLAST_MOVE" => "BLOAT_MOVE",
            _ => "SUPER_GAS_BLAST_MOVE",
        }),
        MonsterId::Mawler => match cur {
            "RAND" => pick_random_branch(
                state,
                creature,
                "RAND",
                &[
                    RandomBranch {
                        state_id: "RIP_AND_TEAR_MOVE",
                        repeat: RepeatRule::CannotRepeat,
                        cooldown: 0,
                        weight: 1.0,
                    },
                    RandomBranch {
                        state_id: "ROAR_MOVE",
                        repeat: RepeatRule::UseOnlyOnce,
                        cooldown: 0,
                        weight: 1.0,
                    },
                    RandomBranch {
                        state_id: "CLAW_MOVE",
                        repeat: RepeatRule::CannotRepeat,
                        cooldown: 0,
                        weight: 1.0,
                    },
                ],
            ),
            _ => Ok("RAND"),
        },
        MonsterId::Nibbit => {
            let is_alone = read_monster_bool(state, creature, "Nibbit._isAlone", "IsAlone");
            let is_front = read_monster_bool(state, creature, "Nibbit._isFront", "IsFront");
            Ok(match cur {
                "INIT_MOVE" => {
                    if is_alone {
                        "BUTT_MOVE"
                    } else if is_front {
                        "SLICE_MOVE"
                    } else {
                        "HISS_MOVE"
                    }
                }
                "BUTT_MOVE" => "SLICE_MOVE",
                "SLICE_MOVE" => "HISS_MOVE",
                _ => "BUTT_MOVE",
            })
        }
        MonsterId::PhantasmalGardener => match cur {
            "INIT_MOVE" => {
                let slot = state
                    .creature(creature)
                    .and_then(|c| c.slot_name.as_deref());
                match slot {
                    Some("first") => Ok("FLAIL_MOVE"),
                    Some("second") => Ok("BITE_MOVE"),
                    Some("third") => Ok("LASH_MOVE"),
                    Some("fourth") => Ok("ENLARGE_MOVE"),
                    other => Err(KernelError::InvalidState(format!(
                        "No branch predicate matched for PhantasmalGardener INIT_MOVE slot={other:?}"
                    ))),
                }
            }
            "BITE_MOVE" => Ok("LASH_MOVE"),
            "LASH_MOVE" => Ok("FLAIL_MOVE"),
            "FLAIL_MOVE" => Ok("ENLARGE_MOVE"),
            _ => Ok("BITE_MOVE"),
        },
        MonsterId::PhrogParasite => Ok(match cur {
            "INFECT_MOVE" => "LASH_MOVE",
            _ => "INFECT_MOVE",
        }),
        MonsterId::PunchConstruct => Ok(match cur {
            "READY_MOVE" => "FAST_PUNCH_MOVE",
            "FAST_PUNCH_MOVE" => "STRONG_PUNCH_MOVE",
            _ => "READY_MOVE",
        }),
        MonsterId::Seapunk => Ok(match cur {
            "SEA_KICK_MOVE" => "SPINNING_KICK_MOVE",
            "SPINNING_KICK_MOVE" => "BUBBLE_BURP_MOVE",
            _ => "SEA_KICK_MOVE",
        }),
        MonsterId::SewerClam => Ok(match cur {
            "JET_MOVE" => "PRESSURIZE_MOVE",
            _ => "JET_MOVE",
        }),
        MonsterId::ShrinkerBeetle => Ok(match cur {
            "SHRINKER_MOVE" | "STOMP_MOVE" => "CHOMP_MOVE",
            _ => "STOMP_MOVE",
        }),
        MonsterId::SkulkingColony => Ok(match cur {
            "ZOOM_MOVE" => "ZOOM_MOVE_2",
            "ZOOM_MOVE_2" => "INERTIA_MOVE",
            "INERTIA_MOVE" => "PIERCING_STABS_MOVE",
            _ => "ZOOM_MOVE",
        }),
        MonsterId::SlitheringStrangler => match cur {
            "CONSTRICT" => Ok("rand"),
            "rand" => pick_random_branch(
                state,
                creature,
                "rand",
                &[
                    RandomBranch {
                        state_id: "THWACK",
                        repeat: RepeatRule::CanRepeatForever,
                        cooldown: 0,
                        weight: 1.0,
                    },
                    RandomBranch {
                        state_id: "LASH",
                        repeat: RepeatRule::CanRepeatForever,
                        cooldown: 0,
                        weight: 1.0,
                    },
                ],
            ),
            _ => Ok("CONSTRICT"),
        },
        MonsterId::SludgeSpinner => match cur {
            "RAND" => pick_random_branch(
                state,
                creature,
                "RAND",
                &[
                    RandomBranch {
                        state_id: "OIL_SPRAY_MOVE",
                        repeat: RepeatRule::CannotRepeat,
                        cooldown: 0,
                        weight: 1.0,
                    },
                    RandomBranch {
                        state_id: "SLAM_MOVE",
                        repeat: RepeatRule::CannotRepeat,
                        cooldown: 0,
                        weight: 1.0,
                    },
                    RandomBranch {
                        state_id: "RAGE_MOVE",
                        repeat: RepeatRule::CannotRepeat,
                        cooldown: 0,
                        weight: 1.0,
                    },
                ],
            ),
            _ => Ok("RAND"),
        },
        MonsterId::SnappingJaxfruit => Ok("ENERGY_ORB_MOVE"),
        MonsterId::SoulFysh => Ok(match cur {
            "BECKON_MOVE" => "DE_GAS_MOVE",
            "DE_GAS_MOVE" => "GAZE_MOVE",
            "GAZE_MOVE" => "FADE_MOVE",
            "FADE_MOVE" => "SCREAM_MOVE",
            _ => "BECKON_MOVE",
        }),
        MonsterId::TerrorEel => Ok(match cur {
            "CRASH_MOVE" => "THRASH_MOVE",
            "THRASH_MOVE" | "TERROR_MOVE" => "CRASH_MOVE",
            "STUN_MOVE" => "TERROR_MOVE",
            _ => "CRASH_MOVE",
        }),
        MonsterId::Toadpole => {
            let is_front = read_monster_bool(state, creature, "Toadpole._isFront", "IsFront");
            Ok(match cur {
                "INIT_MOVE" => {
                    if !is_front {
                        "WHIRL_MOVE"
                    } else {
                        "SPIKEN_MOVE"
                    }
                }
                "WHIRL_MOVE" => "SPIKEN_MOVE",
                "SPIKEN_MOVE" => "SPIKE_SPIT_MOVE",
                _ => "WHIRL_MOVE",
            })
        }
        MonsterId::TrackerRubyRaider => Ok("HOUNDS_MOVE"),
        MonsterId::TwigSlimeM => match cur {
            "RAND" => pick_random_branch(
                state,
                creature,
                "RAND",
                &[
                    RandomBranch {
                        state_id: "POKEY_POUNCE_MOVE",
                        repeat: RepeatRule::CanRepeatXTimes(2),
                        cooldown: 0,
                        weight: 1.0,
                    },
                    RandomBranch {
                        state_id: "STICKY_SHOT_MOVE",
                        repeat: RepeatRule::CannotRepeat,
                        cooldown: 0,
                        weight: 1.0,
                    },
                ],
            ),
            _ => Ok("RAND"),
        },
        MonsterId::TwigSlimeS => Ok("TACKLE_MOVE"),
        MonsterId::TwoTailedRat => match cur {
            "RAND" => {
                let can_summon = two_tailed_rat_can_summon(state, creature);
                let attack_w = if can_summon { 1.0 / 12.0 } else { 1.0 };
                let call_w = if can_summon { 0.75 } else { 0.0 };
                pick_random_branch(
                    state,
                    creature,
                    "RAND",
                    &[
                        RandomBranch {
                            state_id: "SCRATCH_MOVE",
                            repeat: RepeatRule::CannotRepeat,
                            cooldown: 0,
                            weight: attack_w,
                        },
                        RandomBranch {
                            state_id: "DISEASE_BITE_MOVE",
                            repeat: RepeatRule::CannotRepeat,
                            cooldown: 0,
                            weight: attack_w,
                        },
                        RandomBranch {
                            state_id: "SCREECH_MOVE",
                            repeat: RepeatRule::CannotRepeat,
                            cooldown: 3,
                            weight: attack_w,
                        },
                        RandomBranch {
                            state_id: "CALL_FOR_BACKUP_MOVE",
                            repeat: RepeatRule::UseOnlyOnce,
                            cooldown: 0,
                            weight: call_w,
                        },
                    ],
                )
            }
            _ => Ok("RAND"),
        },
        MonsterId::Vantom => Ok(match cur {
            "INK_BLOT_MOVE" => "INKY_LANCE_MOVE",
            "INKY_LANCE_MOVE" => "DISMEMBER_MOVE",
            "DISMEMBER_MOVE" => "PREPARE_MOVE",
            _ => "INK_BLOT_MOVE",
        }),
        MonsterId::VineShambler => Ok(match cur {
            "SWIPE_MOVE" => "GRASPING_VINES_MOVE",
            "GRASPING_VINES_MOVE" => "CHOMP_MOVE",
            _ => "SWIPE_MOVE",
        }),
        MonsterId::WaterfallGiant => Ok(match cur {
            "PRESSURIZE_MOVE" | "PRESSURE_UP_MOVE" => "STOMP_MOVE",
            "STOMP_MOVE" => "RAM_MOVE",
            "RAM_MOVE" => "SIPHON_MOVE",
            "SIPHON_MOVE" => "PRESSURE_GUN_MOVE",
            "PRESSURE_GUN_MOVE" => "PRESSURE_UP_MOVE",
            "ABOUT_TO_BLOW_MOVE" | "EXPLODE_MOVE" => "EXPLODE_MOVE",
            _ => "STOMP_MOVE",
        }),
        _ => Err(KernelError::Unsupported {
            kind: "Monster",
            id: mid.model_id_str().to_owned(),
        }),
    }
}

pub fn monster_roll_move(
    state: &mut BattleState,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    let (mid, can_transition_away, performed_first_move, cur_is_move) = {
        let Some(c) = state.creature(creature) else {
            return Ok(());
        };
        let Some(m) = &c.monster else {
            return Ok(());
        };
        (
            m.monster_id,
            m.next_move_can_transition_away,
            m.performed_first_move,
            is_move_state(m.monster_id, &m.current_state_id),
        )
    };
    let _ = monster_meta(mid)?;

    if !can_transition_away || (!performed_first_move && cur_is_move) {
        if cur_is_move {
            if let Some(m_mut) = state.creature_mut(creature).and_then(|c| c.monster.as_mut()) {
                if m_mut.next_move_id.as_deref() != Some(m_mut.current_state_id.as_ref()) {
                    m_mut.next_move_id = Some(m_mut.current_state_id.clone());
                }
            }
        }
        return Ok(());
    }

    enum InlineStateStr {
        Borrowed(&'static str),
        Stack([u8; 64], u8),
        Heap(String),
    }
    impl InlineStateStr {
        #[inline]
        fn from_cow(s: &Cow<'static, str>) -> Self {
            match s {
                Cow::Borrowed(b) => Self::Borrowed(b),
                Cow::Owned(o) if o.len() <= 64 => {
                    let mut buf = [0u8; 64];
                    buf[..o.len()].copy_from_slice(o.as_bytes());
                    Self::Stack(buf, o.len() as u8)
                }
                Cow::Owned(o) => Self::Heap(o.clone()),
            }
        }
        #[inline]
        fn as_str(&self) -> &str {
            match self {
                Self::Borrowed(s) => s,
                Self::Stack(buf, len) => {
                    std::str::from_utf8(&buf[..*len as usize]).unwrap_or("")
                }
                Self::Heap(s) => s.as_str(),
            }
        }
    }

    let mut cur = state
        .creature(creature)
        .and_then(|c| c.monster.as_ref())
        .map(|m| InlineStateStr::from_cow(&m.current_state_id))
        .unwrap_or(InlineStateStr::Borrowed(""));
    let mut logged: Option<&'static str> = None;
    let final_state: &'static str;
    loop {
        let next = next_state_step(state, creature, mid, cur.as_str())?;
        let next_is_move = is_move_state(mid, next);
        if logged.is_none() && next_is_move {
            logged = Some(next);
        }
        cur = InlineStateStr::Borrowed(next);
        if next_is_move {
            final_state = next;
            break;
        }
    }

    let can_transition_next = !must_perform_once_before_transitioning(mid, final_state);
    if let Some(m_mut) = state.creature_mut(creature).and_then(|c| c.monster.as_mut()) {
        m_mut.current_state_id = Cow::Borrowed(final_state);
        m_mut.next_move_id = Some(Cow::Borrowed(final_state));
        m_mut.next_move_can_transition_away = can_transition_next;
        if let Some(log_id) = logged {
            m_mut.state_log_ids.push(Cow::Borrowed(log_id));
        }
    }
    Ok(())
}

fn generate_status_to_players(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_id: CardId,
    pile: PileType,
    pos: CardPilePosition,
    count: usize,
) -> Result<(), KernelError> {
    let players: SmallVec<[CreatureHandle; 2]> = state
        .ally_handles
        .iter()
        .copied()
        .filter(|&h| state.creature(h).is_some_and(|c| c.is_player))
        .collect();
    for _ in players {
        for _ in 0..count {
            card_pile::add_generated_card_to_combat(state, content, card_id, pile, pos, 0, None)?;
        }
    }
    Ok(())
}

pub fn monster_perform_move(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    let (mid, move_id) = {
        let Some(c) = state.creature_mut(creature) else {
            return Ok(());
        };
        let Some(m) = &mut c.monster else {
            return Ok(());
        };
        let mid = m.monster_id;
        let mv = m
            .next_move_id
            .clone()
            .unwrap_or_else(|| m.current_state_id.clone());
        m.is_performing_move = true;
        (mid, mv)
    };
    let _ = monster_meta(mid)?;

    let dread = is_dread(state);
    match (mid, move_id.as_ref()) {
        // Explicit STUNNED move callbacks (invoked by engine when STUNNED executes)
        (MonsterId::LagavulinMatriarch, "STUNNED") => {
            if !read_monster_bool(state, creature, "LagavulinMatriarch._isAwake", "IsAwake") {
                set_monster_bool(state, creature, "LagavulinMatriarch._isAwake", true);
            }
        }
        (MonsterId::CorpseSlug, "STUNNED") => {
            set_monster_bool(state, creature, "CorpseSlug._isRavenous", false);
        }
        (MonsterId::CeremonialBeast, "STUNNED") => {
            set_monster_bool(
                state,
                creature,
                "CeremonialBeast._isStunnedByPlowRemoval",
                false,
            );
        }
        (_, "STUNNED") => {}

        // 1. AssassinRubyRaider
        (MonsterId::AssassinRubyRaider, "KILLSHOT_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 11, 10);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }

        // 2. AxeRubyRaider
        (MonsterId::AxeRubyRaider, "SWING_1" | "SWING_2") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 6, 5);
            let blk = dec_asc(state, AscensionLevel::DeadlyEnemies, 6, 5);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            block::gain_block(state, content, creature, blk, ValueProp::MOVE, None, None)?;
        }
        (MonsterId::AxeRubyRaider, "BIG_SWING") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 13, 12);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }

        // 3. BruteRubyRaider
        (MonsterId::BruteRubyRaider, "BEAT_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 8, 7);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::BruteRubyRaider, "ROAR_MOVE") => {
            apply_power_to_self(state, content, creature, PowerId::StrengthPower, 3)?;
        }

        // 4. BygoneEffigy
        (MonsterId::BygoneEffigy, "SLEEP_MOVE") => {}
        (MonsterId::BygoneEffigy, "WAKE_MOVE") => {
            apply_power_to_self(state, content, creature, PowerId::StrengthPower, 10)?;
        }
        (MonsterId::BygoneEffigy, "SLASHES_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 15, 13);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }

        // 5. Byrdonis
        (MonsterId::Byrdonis, "SWOOP_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 19, 17);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Byrdonis, "PECK_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 4, 3);
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(3)
                .execute(state, content)?;
        }

        // 6. CalcifiedCultist
        (MonsterId::CalcifiedCultist, "INCANTATION_MOVE") => {
            apply_power_to_self(state, content, creature, PowerId::RitualPower, 2)?;
        }
        (MonsterId::CalcifiedCultist, "DARK_STRIKE_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 11, 9);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }

        // 7. CeremonialBeast
        (MonsterId::CeremonialBeast, "STAMP_MOVE") => {
            let amt = asc(state, AscensionLevel::DeadlyEnemies, 160, 150);
            apply_power_to_self(state, content, creature, PowerId::PlowPower, amt)?;
        }
        (MonsterId::CeremonialBeast, "PLOW_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 20, 18);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            if has_power(state, creature, PowerId::PlowPower) {
                let str_gain = if dread { 3 } else { 2 };
                apply_power_to_self(state, content, creature, PowerId::StrengthPower, str_gain)?;
            }
        }
        (MonsterId::CeremonialBeast, "STUN_MOVE") => {
            set_monster_bool(
                state,
                creature,
                "CeremonialBeast._isStunnedByPlowRemoval",
                false,
            );
        }
        (MonsterId::CeremonialBeast, "BEAST_CRY_MOVE") => {
            apply_power_to_players(state, content, creature, PowerId::RingingPower, 1)?;
        }
        (MonsterId::CeremonialBeast, "STOMP_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 17, 15);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::CeremonialBeast, "CRUSH_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 19, 17);
            let str_gain = asc(state, AscensionLevel::DeadlyEnemies, 4, 3);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            apply_power_to_self(state, content, creature, PowerId::StrengthPower, str_gain)?;
        }

        // 8. CorpseSlug
        (MonsterId::CorpseSlug, "WHIP_SLAP_MOVE") => {
            AttackBuilder::from_monster(creature, Dec::from_i32(3))
                .with_hit_count(2)
                .execute(state, content)?;
        }
        (MonsterId::CorpseSlug, "GLOMP_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::CorpseSlug, "GOOP_MOVE") => {
            apply_power_to_players(state, content, creature, PowerId::FrailPower, 2)?;
        }

        // 9. CrossbowRubyRaider
        (MonsterId::CrossbowRubyRaider, "RELOAD_MOVE") => {
            block::gain_block(
                state,
                content,
                creature,
                Dec::from_i32(3),
                ValueProp::MOVE,
                None,
                None,
            )?;
        }
        (MonsterId::CrossbowRubyRaider, "FIRE_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 16, 14);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }

        // 10. CubexConstruct
        (MonsterId::CubexConstruct, "CHARGE_UP_MOVE") => {
            apply_power_to_self(state, content, creature, PowerId::StrengthPower, 2)?;
        }
        (MonsterId::CubexConstruct, "REPEATER_BLAST_MOVE" | "REPEATER_BLAST_MOVE_2") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 8, 7);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            apply_power_to_self(state, content, creature, PowerId::StrengthPower, 2)?;
        }
        (MonsterId::CubexConstruct, "EXPEL_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 6, 5);
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
        }

        // 11. DampCultist
        (MonsterId::DampCultist, "INCANTATION_MOVE") => {
            let ritual = asc(state, AscensionLevel::DeadlyEnemies, 6, 5);
            apply_power_to_self(state, content, creature, PowerId::RitualPower, ritual)?;
        }
        (MonsterId::DampCultist, "DARK_STRIKE_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 3, 1);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }

        // 12. Flyconid
        (MonsterId::Flyconid, "VULNERABLE_SPORES_MOVE") => {
            apply_power_to_players(state, content, creature, PowerId::VulnerablePower, 2)?;
        }
        (MonsterId::Flyconid, "FRAIL_SPORES_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            apply_power_to_players(state, content, creature, PowerId::FrailPower, 2)?;
        }
        (MonsterId::Flyconid, "SMASH_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 12, 11);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }

        // 13. Fogmog
        (MonsterId::Fogmog, "ILLUSION_MOVE") => {
            if state.is_live_combat() {
                let side = state
                    .creature(creature)
                    .map_or(CombatSide::Enemy, |c| c.side);
                let _ = creature_cmd::spawn_monster_in_combat(
                    state,
                    content,
                    MonsterId::EyeWithTeeth,
                    side,
                    Some(Cow::Borrowed("illusion")),
                )?;
            }
        }
        (MonsterId::Fogmog, "SWIPE_MOVE" | "SWIPE_RANDOM_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            apply_power_to_self(state, content, creature, PowerId::StrengthPower, 1)?;
        }
        (MonsterId::Fogmog, "HEADBUTT_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 16, 14);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }

        // 14. FossilStalker
        (MonsterId::FossilStalker, "TACKLE_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 11, 9);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            apply_power_to_players(state, content, creature, PowerId::FrailPower, 1)?;
        }
        (MonsterId::FossilStalker, "LATCH_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 14, 12);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::FossilStalker, "LASH_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 4, 3);
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
        }

        // 15. FuzzyWurmCrawler
        (MonsterId::FuzzyWurmCrawler, "FIRST_ACID_GOOP" | "ACID_GOOP") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 6, 4);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::FuzzyWurmCrawler, "INHALE") => {
            apply_power_to_self(state, content, creature, PowerId::StrengthPower, 7)?;
        }

        // 16. GremlinMerc (NOTE: ToughEnemies scales damages!)
        (MonsterId::GremlinMerc, "GIMME_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::ToughEnemies, 8, 7);
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
            gremlin_merc_steal(state, creature)?;
        }
        (MonsterId::GremlinMerc, "DOUBLE_SMASH_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::ToughEnemies, 7, 6);
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
            gremlin_merc_steal(state, creature)?;
            apply_power_to_players(state, content, creature, PowerId::WeakPower, 2)?;
        }
        (MonsterId::GremlinMerc, "HEHE_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::ToughEnemies, 9, 8);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            gremlin_merc_steal(state, creature)?;
            apply_power_to_self(state, content, creature, PowerId::StrengthPower, 2)?;
        }

        // 17. HauntedShip
        (MonsterId::HauntedShip, "HAUNT_MOVE") => {
            apply_power_to_players(state, content, creature, PowerId::WeakPower, 3)?;
            generate_status_to_players(
                state,
                content,
                CardId::Dazed,
                PileType::Discard,
                CardPilePosition::Bottom,
                5,
            )?;
        }
        (MonsterId::HauntedShip, "SWIPE_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 14, 13);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::HauntedShip, "STOMP_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 5, 4);
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(3)
                .execute(state, content)?;
        }

        // 18. Inklet
        (MonsterId::Inklet, "JAB_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 4, 3);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Inklet, "WHIRLWIND_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 3, 2);
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(3)
                .execute(state, content)?;
        }
        (MonsterId::Inklet, "PIERCING_GAZE_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 11, 10);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }

        // 19. KinFollower
        (MonsterId::KinFollower, "QUICK_SLASH_MOVE") => {
            AttackBuilder::from_monster(creature, Dec::from_i32(5)).execute(state, content)?;
        }
        (MonsterId::KinFollower, "BOOMERANG_MOVE") => {
            AttackBuilder::from_monster(creature, Dec::from_i32(2))
                .with_hit_count(2)
                .execute(state, content)?;
        }
        (MonsterId::KinFollower, "POWER_DANCE_MOVE") => {
            let str_gain = asc(state, AscensionLevel::DeadlyEnemies, 3, 2);
            apply_power_to_self(state, content, creature, PowerId::StrengthPower, str_gain)?;
        }

        // 20. KinPriest
        (MonsterId::KinPriest, "ORB_OF_FRAILTY_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            apply_power_to_players(state, content, creature, PowerId::FrailPower, 1)?;
        }
        (MonsterId::KinPriest, "ORB_OF_WEAKNESS_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            apply_power_to_players(state, content, creature, PowerId::WeakPower, 1)?;
        }
        (MonsterId::KinPriest, "BEAM_MOVE") => {
            AttackBuilder::from_monster(creature, Dec::from_i32(3))
                .with_hit_count(3)
                .execute(state, content)?;
        }
        (MonsterId::KinPriest, "RITUAL_MOVE") => {
            let str_gain = asc(state, AscensionLevel::DeadlyEnemies, 3, 2);
            apply_power_to_self(state, content, creature, PowerId::StrengthPower, str_gain)?;
        }

        // 21. LagavulinMatriarch
        (MonsterId::LagavulinMatriarch, "SLEEP_MOVE") => {}
        (MonsterId::LagavulinMatriarch, "SLASH_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 21, 19);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::LagavulinMatriarch, "SLASH2_MOVE") => {
            let bonus = if dread { 2 } else { 0 };
            let dmg = Dec::from_i32(asc(state, AscensionLevel::DeadlyEnemies, 14, 12) + bonus);
            let blk = Dec::from_i32(asc(state, AscensionLevel::ToughEnemies, 14, 12) + bonus);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            block::gain_block(state, content, creature, blk, ValueProp::MOVE, None, None)?;
        }
        (MonsterId::LagavulinMatriarch, "DISEMBOWEL_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 10, 9);
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
        }
        (MonsterId::LagavulinMatriarch, "SOUL_SIPHON_MOVE") => {
            let debuff = if dread { -3 } else { -2 };
            apply_power_to_players(state, content, creature, PowerId::StrengthPower, debuff)?;
            apply_power_to_players(state, content, creature, PowerId::DexterityPower, debuff)?;
            apply_power_to_self(state, content, creature, PowerId::StrengthPower, 2)?;
        }

        // 22. LeafSlimeM
        (MonsterId::LeafSlimeM, "CLUMP_SHOT") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::LeafSlimeM, "STICKY_SHOT") => {
            generate_status_to_players(
                state,
                content,
                CardId::Slimed,
                PileType::Discard,
                CardPilePosition::Bottom,
                2,
            )?;
        }

        // 23. LeafSlimeS
        (MonsterId::LeafSlimeS, "TACKLE_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 4, 3);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::LeafSlimeS, "GOOP_MOVE") => {
            generate_status_to_players(
                state,
                content,
                CardId::Slimed,
                PileType::Discard,
                CardPilePosition::Bottom,
                1,
            )?;
        }

        // 24. LivingFog
        (MonsterId::LivingFog, "ADVANCED_GAS_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            apply_power_to_players(state, content, creature, PowerId::SmoggyPower, 1)?;
        }
        (MonsterId::LivingFog, "BLOAT_MOVE") => {
            let bloat = read_monster_int(
                state,
                creature,
                "LivingFog._bloatAmount",
                "BloatAmount",
                1,
            );
            for _ in 0..bloat {
                let Some(slot) = next_free_slot(state, BOMB_SLOTS, false) else {
                    break;
                };
                if state.is_live_combat() {
                    let _ = creature_cmd::spawn_monster_in_combat(
                        state,
                        content,
                        MonsterId::GasBomb,
                        CombatSide::Enemy,
                        Some(Cow::Borrowed(slot)),
                    )?;
                }
            }
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 6, 5);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::LivingFog, "SUPER_GAS_BLAST_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }

        // 25. Mawler
        (MonsterId::Mawler, "RIP_AND_TEAR_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 16, 14);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Mawler, "ROAR_MOVE") => {
            apply_power_to_players(state, content, creature, PowerId::VulnerablePower, 3)?;
        }
        (MonsterId::Mawler, "CLAW_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 5, 4);
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
        }

        // 26. Nibbit
        (MonsterId::Nibbit, "BUTT_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 13, 12);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Nibbit, "SLICE_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 7, 6);
            let blk = dec_asc(state, AscensionLevel::ToughEnemies, 6, 5);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            block::gain_block(state, content, creature, blk, ValueProp::MOVE, None, None)?;
        }
        (MonsterId::Nibbit, "HISS_MOVE") => {
            let str_gain = asc(state, AscensionLevel::DeadlyEnemies, 3, 2);
            apply_power_to_self(state, content, creature, PowerId::StrengthPower, str_gain)?;
        }

        // 27. PhantasmalGardener
        (MonsterId::PhantasmalGardener, "BITE_MOVE") => {
            AttackBuilder::from_monster(creature, Dec::from_i32(5)).execute(state, content)?;
        }
        (MonsterId::PhantasmalGardener, "LASH_MOVE") => {
            AttackBuilder::from_monster(creature, Dec::from_i32(7)).execute(state, content)?;
        }
        (MonsterId::PhantasmalGardener, "FLAIL_MOVE") => {
            AttackBuilder::from_monster(creature, Dec::ONE)
                .with_hit_count(3)
                .execute(state, content)?;
        }
        (MonsterId::PhantasmalGardener, "ENLARGE_MOVE") => {
            let str_gain = asc(state, AscensionLevel::DeadlyEnemies, 3, 2);
            apply_power_to_self(state, content, creature, PowerId::StrengthPower, str_gain)?;
            let prev = read_monster_int(
                state,
                creature,
                "PhantasmalGardener._enlargeTriggers",
                "EnlargeTriggers",
                0,
            );
            set_monster_int(
                state,
                creature,
                "PhantasmalGardener._enlargeTriggers",
                prev + 1,
            );
        }

        // 28. PhrogParasite
        (MonsterId::PhrogParasite, "INFECT_MOVE") => {
            generate_status_to_players(
                state,
                content,
                CardId::Infection,
                PileType::Discard,
                CardPilePosition::Bottom,
                3,
            )?;
        }
        (MonsterId::PhrogParasite, "LASH_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 5, 4);
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(4)
                .execute(state, content)?;
        }

        // 29. PunchConstruct
        (MonsterId::PunchConstruct, "READY_MOVE") => {
            block::gain_block(
                state,
                content,
                creature,
                Dec::from_i32(10),
                ValueProp::MOVE,
                None,
                None,
            )?;
        }
        (MonsterId::PunchConstruct, "FAST_PUNCH_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 6, 5);
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
            apply_power_to_players(state, content, creature, PowerId::FrailPower, 1)?;
        }
        (MonsterId::PunchConstruct, "STRONG_PUNCH_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 16, 14);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }

        // 30. Seapunk
        (MonsterId::Seapunk, "SEA_KICK_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 13, 11);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Seapunk, "SPINNING_KICK_MOVE") => {
            AttackBuilder::from_monster(creature, Dec::from_i32(2))
                .with_hit_count(4)
                .execute(state, content)?;
        }
        (MonsterId::Seapunk, "BUBBLE_BURP_MOVE") => {
            let blk = dec_asc(state, AscensionLevel::ToughEnemies, 8, 7);
            let str_gain = asc(state, AscensionLevel::DeadlyEnemies, 2, 1);
            block::gain_block(state, content, creature, blk, ValueProp::MOVE, None, None)?;
            apply_power_to_self(state, content, creature, PowerId::StrengthPower, str_gain)?;
        }

        // 31. SewerClam
        (MonsterId::SewerClam, "JET_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 11, 10);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::SewerClam, "PRESSURIZE_MOVE") => {
            apply_power_to_self(state, content, creature, PowerId::StrengthPower, 4)?;
        }

        // 32. ShrinkerBeetle
        (MonsterId::ShrinkerBeetle, "SHRINKER_MOVE") => {
            apply_power_to_players(state, content, creature, PowerId::ShrinkPower, -1)?;
        }
        (MonsterId::ShrinkerBeetle, "CHOMP_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 8, 7);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::ShrinkerBeetle, "STOMP_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 14, 13);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }

        // 33. SkulkingColony
        (MonsterId::SkulkingColony, "ZOOM_MOVE" | "ZOOM_MOVE_2") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 16, 14);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::SkulkingColony, "INERTIA_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 11, 9);
            let str_gain = asc(state, AscensionLevel::DeadlyEnemies, 4, 2);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            apply_power_to_self(state, content, creature, PowerId::StrengthPower, str_gain)?;
        }
        (MonsterId::SkulkingColony, "PIERCING_STABS_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 8, 7);
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
        }

        // 34. SlitheringStrangler
        (MonsterId::SlitheringStrangler, "CONSTRICT") => {
            apply_power_to_players(state, content, creature, PowerId::ConstrictPower, 3)?;
        }
        (MonsterId::SlitheringStrangler, "THWACK") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 8, 7);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            block::gain_block(
                state,
                content,
                creature,
                Dec::from_i32(5),
                ValueProp::MOVE,
                None,
                None,
            )?;
        }
        (MonsterId::SlitheringStrangler, "LASH") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 13, 12);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }

        // 35. SludgeSpinner
        (MonsterId::SludgeSpinner, "OIL_SPRAY_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            apply_power_to_players(state, content, creature, PowerId::WeakPower, 1)?;
        }
        (MonsterId::SludgeSpinner, "SLAM_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 12, 11);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::SludgeSpinner, "RAGE_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 7, 6);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            apply_power_to_self(state, content, creature, PowerId::StrengthPower, 3)?;
        }

        // 36. SnappingJaxfruit
        (MonsterId::SnappingJaxfruit, "ENERGY_ORB_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 4, 3);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            apply_power_to_self(state, content, creature, PowerId::StrengthPower, 2)?;
        }

        // 37. SoulFysh
        (MonsterId::SoulFysh, "BECKON_MOVE") => {
            let players: SmallVec<[CreatureHandle; 2]> = state
                .ally_handles
                .iter()
                .copied()
                .filter(|&h| state.creature(h).is_some_and(|c| c.is_player))
                .collect();
            for _ in players {
                card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    CardId::Beckon,
                    PileType::Draw,
                    CardPilePosition::Random,
                    0,
                    None,
                )?;
                if dread {
                    card_pile::add_generated_card_to_combat(
                        state,
                        content,
                        CardId::Beckon,
                        PileType::Hand,
                        CardPilePosition::Bottom,
                        0,
                        None,
                    )?;
                }
                card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    CardId::Beckon,
                    PileType::Discard,
                    CardPilePosition::Bottom,
                    0,
                    None,
                )?;
            }
        }
        (MonsterId::SoulFysh, "DE_GAS_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 18, 16);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::SoulFysh, "GAZE_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 8, 7);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            generate_status_to_players(
                state,
                content,
                CardId::Beckon,
                PileType::Discard,
                CardPilePosition::Bottom,
                1,
            )?;
        }
        (MonsterId::SoulFysh, "FADE_MOVE") => {
            set_monster_bool(state, creature, "SoulFysh._isInvisible", true);
            apply_power_to_self(state, content, creature, PowerId::IntangiblePower, 2)?;
        }
        (MonsterId::SoulFysh, "SCREAM_MOVE") => {
            set_monster_bool(state, creature, "SoulFysh._isInvisible", false);
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 15, 13);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            apply_power_to_players(state, content, creature, PowerId::VulnerablePower, 3)?;
        }

        // 38. TerrorEel
        (MonsterId::TerrorEel, "CRASH_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 18, 16);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::TerrorEel, "THRASH_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 4, 3);
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(3)
                .execute(state, content)?;
            apply_power_to_self(state, content, creature, PowerId::VigorPower, 6)?;
        }
        (MonsterId::TerrorEel, "STUN_MOVE") => {}
        (MonsterId::TerrorEel, "TERROR_MOVE") => {
            apply_power_to_players(state, content, creature, PowerId::VulnerablePower, 99)?;
        }

        // 39. Toadpole
        (MonsterId::Toadpole, "SPIKE_SPIT_MOVE") => {
            apply_power_to_self(state, content, creature, PowerId::ThornsPower, -2)?;
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 4, 3);
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(3)
                .execute(state, content)?;
        }
        (MonsterId::Toadpole, "WHIRL_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 8, 7);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Toadpole, "SPIKEN_MOVE") => {
            apply_power_to_self(state, content, creature, PowerId::ThornsPower, 2)?;
        }

        // 40. TrackerRubyRaider
        (MonsterId::TrackerRubyRaider, "TRACK_MOVE") => {
            apply_power_to_players(state, content, creature, PowerId::FrailPower, 2)?;
        }
        (MonsterId::TrackerRubyRaider, "HOUNDS_MOVE") => {
            let repeat = asc(state, AscensionLevel::DeadlyEnemies, 9, 8);
            AttackBuilder::from_monster(creature, Dec::ONE)
                .with_hit_count(repeat)
                .execute(state, content)?;
        }

        // 41. TwigSlimeM
        (MonsterId::TwigSlimeM, "POKEY_POUNCE_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 12, 11);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::TwigSlimeM, "STICKY_SHOT_MOVE") => {
            generate_status_to_players(
                state,
                content,
                CardId::Slimed,
                PileType::Discard,
                CardPilePosition::Bottom,
                1,
            )?;
        }

        // 42. TwigSlimeS
        (MonsterId::TwigSlimeS, "TACKLE_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 5, 4);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }

        // 43. TwoTailedRat
        (MonsterId::TwoTailedRat, "SCRATCH_MOVE") => {
            let prev = read_monster_int(
                state,
                creature,
                "TwoTailedRat._turnsUntilSummonable",
                "TurnsUntilSummonable",
                2,
            );
            set_monster_int(
                state,
                creature,
                "TwoTailedRat._turnsUntilSummonable",
                prev - 1,
            );
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::TwoTailedRat, "DISEASE_BITE_MOVE") => {
            let prev = read_monster_int(
                state,
                creature,
                "TwoTailedRat._turnsUntilSummonable",
                "TurnsUntilSummonable",
                2,
            );
            set_monster_int(
                state,
                creature,
                "TwoTailedRat._turnsUntilSummonable",
                prev - 1,
            );
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 7, 6);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::TwoTailedRat, "SCREECH_MOVE") => {
            let prev = read_monster_int(
                state,
                creature,
                "TwoTailedRat._turnsUntilSummonable",
                "TurnsUntilSummonable",
                2,
            );
            set_monster_int(
                state,
                creature,
                "TwoTailedRat._turnsUntilSummonable",
                prev - 1,
            );
            apply_power_to_players(state, content, creature, PowerId::FrailPower, 1)?;
        }
        (MonsterId::TwoTailedRat, "CALL_FOR_BACKUP_MOVE") => {
            if state.is_live_combat() {
                if let Some(slot) = next_free_slot(state, RAT_SLOTS, true) {
                    let _ = creature_cmd::spawn_monster_in_combat(
                        state,
                        content,
                        MonsterId::TwoTailedRat,
                        CombatSide::Enemy,
                        Some(Cow::Borrowed(slot)),
                    )?;
                }
                let rats: SmallVec<[CreatureHandle; 8]> = state
                    .enemy_handles
                    .iter()
                    .copied()
                    .filter(|&h| {
                        state
                            .creature(h)
                            .is_some_and(|c| c.monster_id == Some(MonsterId::TwoTailedRat))
                    })
                    .collect();
                let next_count = rats
                    .iter()
                    .map(|&h| {
                        read_monster_int(
                            state,
                            h,
                            "TwoTailedRat._callForBackupCount",
                            "CallForBackupCount",
                            0,
                        ) + 1
                    })
                    .max()
                    .unwrap_or(1);
                for h in rats {
                    set_monster_int(state, h, "TwoTailedRat._callForBackupCount", next_count);
                }
            }
        }

        // 44. Vantom
        (MonsterId::Vantom, "INK_BLOT_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 8, 7);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Vantom, "INKY_LANCE_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 7, 6);
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
        }
        (MonsterId::Vantom, "DISMEMBER_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 30, 26);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            let living_players: SmallVec<[CreatureHandle; 2]> = state
                .ally_handles
                .iter()
                .copied()
                .filter(|&h| state.creature(h).is_some_and(|c| c.is_player && c.is_alive()))
                .collect();
            for _ in living_players {
                for _ in 0..3 {
                    card_pile::add_generated_card_to_combat(
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
        }
        (MonsterId::Vantom, "PREPARE_MOVE") => {
            let str_gain = if dread { 3 } else { 2 };
            apply_power_to_self(state, content, creature, PowerId::StrengthPower, str_gain)?;
        }

        // 45. VineShambler
        (MonsterId::VineShambler, "GRASPING_VINES_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            apply_power_to_players(state, content, creature, PowerId::TangledPower, 1)?;
        }
        (MonsterId::VineShambler, "SWIPE_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 7, 6);
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
        }
        (MonsterId::VineShambler, "CHOMP_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 18, 16);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }

        // 46. WaterfallGiant
        (MonsterId::WaterfallGiant, "PRESSURIZE_MOVE") => {
            let amt = asc(state, AscensionLevel::DeadlyEnemies, 20, 15);
            apply_power_to_self(state, content, creature, PowerId::SteamEruptionPower, amt)?;
        }
        (MonsterId::WaterfallGiant, "STOMP_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 16, 15);
            let boost = if dread { 4 } else { 3 };
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            apply_power_to_players(state, content, creature, PowerId::WeakPower, 1)?;
            apply_power_to_self(state, content, creature, PowerId::SteamEruptionPower, boost)?;
        }
        (MonsterId::WaterfallGiant, "RAM_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 11, 10);
            let boost = if dread { 4 } else { 3 };
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            apply_power_to_self(state, content, creature, PowerId::SteamEruptionPower, boost)?;
        }
        (MonsterId::WaterfallGiant, "SIPHON_MOVE") => {
            let per_player = asc(state, AscensionLevel::ToughEnemies, 15, 10);
            let player_count = state
                .ally_handles
                .iter()
                .filter(|&&h| state.creature(h).is_some_and(|c| c.is_player))
                .count() as i32;
            let boost = if dread { 4 } else { 3 };
            creature_cmd::heal(state, content, creature, Dec::from_i32(per_player * player_count))?;
            apply_power_to_self(state, content, creature, PowerId::SteamEruptionPower, boost)?;
        }
        (MonsterId::WaterfallGiant, "PRESSURE_GUN_MOVE") => {
            let base = asc(state, AscensionLevel::DeadlyEnemies, 23, 20);
            let cur_pg = read_monster_int(
                state,
                creature,
                "WaterfallGiant._currentPressureGunDamage",
                "CurrentPressureGunDamage",
                base,
            );
            let boost = if dread { 4 } else { 3 };
            AttackBuilder::from_monster(creature, Dec::from_i32(cur_pg)).execute(state, content)?;
            set_monster_int(
                state,
                creature,
                "WaterfallGiant._currentPressureGunDamage",
                cur_pg + 5,
            );
            apply_power_to_self(state, content, creature, PowerId::SteamEruptionPower, boost)?;
        }
        (MonsterId::WaterfallGiant, "PRESSURE_UP_MOVE") => {
            let dmg = dec_asc(state, AscensionLevel::DeadlyEnemies, 14, 13);
            let boost = if dread { 4 } else { 3 };
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            apply_power_to_self(state, content, creature, PowerId::SteamEruptionPower, boost)?;
        }
        (MonsterId::WaterfallGiant, "ABOUT_TO_BLOW_MOVE") => {
            let steam = power_amount(state, creature, PowerId::SteamEruptionPower);
            set_monster_int(state, creature, "WaterfallGiant._steamEruptionDamage", steam);
            let _ = power::remove_by_id(state, content, creature, PowerId::SteamEruptionPower)?;
        }
        (MonsterId::WaterfallGiant, "EXPLODE_MOVE") => {
            let dmg = read_monster_int(
                state,
                creature,
                "WaterfallGiant._steamEruptionDamage",
                "SteamEruptionDamage",
                0,
            );
            AttackBuilder::from_monster(creature, Dec::from_i32(dmg)).execute(state, content)?;
            creature_cmd::kill(state, content, creature, None)?;
        }

        _ => {
            return Err(KernelError::Unsupported {
                kind: "MonsterMove",
                id: format!("{}:{}", mid.model_id_str(), move_id),
            });
        }
    }

    if let Some(c) = state.creature_mut(creature) {
        if let Some(m) = &mut c.monster {
            m.performed_first_move = true;
            m.is_performing_move = false;
            m.next_move_can_transition_away = true;
        }
    }

    if state
        .creature(creature)
        .is_some_and(|c| c.is_dead() && c.side == CombatSide::Enemy)
        && state.enemy_handles.contains(&creature)
        && hooks::should_creature_be_removed_after_death(state, content, creature)?
    {
        state.enemy_handles.retain(|h| *h != creature);
        state.removed_handles.push(creature);
        if let Some(c) = state.creature_mut(creature) {
            c.is_detached = true;
        }
        state.invalidate_hook_cache();
    }
    Ok(())
}

fn gremlin_merc_steal(state: &mut BattleState, creature: CreatureHandle) -> Result<(), KernelError> {
    if state.static_ctx.game_mode == GameMode::Blitz {
        return Ok(());
    }
    let thievery_handles: SmallVec<[u32; 2]> = state
        .creature(creature)
        .map(|c| {
            c.powers
                .iter()
                .filter(|p| p.id == PowerId::ThieveryPower)
                .map(|p| p.handle)
                .collect()
        })
        .unwrap_or_default();
    for ph in thievery_handles {
        let _ = crate::powers::batch_n_z::thievery_steal(state, creature, ph)?;
    }
    Ok(())
}

pub fn dispatch_hook(
    _content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    let mid = state
        .creature(creature)
        .and_then(|c| c.monster_id)
        .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?;
    match (mid, slot) {
        (MonsterId::LagavulinMatriarch, HookSlot::AfterDamageReceived) => {
            if let HookArgs::DamageResult { target, .. } = args {
                if *target == creature {
                    let should_wake_shell = state
                        .creature(creature)
                        .is_some_and(|c| c.current_hp <= c.max_hp / 2)
                        && !read_monster_bool(
                            state,
                            creature,
                            "LagavulinMatriarch._isShellAwake",
                            "IsShellAwake",
                        );
                    if should_wake_shell {
                        set_monster_bool(state, creature, "LagavulinMatriarch._isShellAwake", true);
                    }
                }
            }
            Ok(HookResult::default())
        }
        (MonsterId::WaterfallGiant, HookSlot::ShouldDisappearFromDoom) => {
            let has_steam = has_power(state, creature, PowerId::SteamEruptionPower);
            Ok(HookResult {
                bool_val: Some(!has_steam),
                ..Default::default()
            })
        }
        _ => Err(KernelError::Unsupported {
            kind: "MonsterHook",
            id: format!("{}:{slot:?}", mid.model_id_str()),
        }),
    }
}

pub fn resume_hook_choice(
    _content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    slot: HookSlot,
    _selected: &[CardHandle],
) -> Result<HookResult, KernelError> {
    let id = state
        .creature(creature)
        .and_then(|c| c.monster_id)
        .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?;
    Err(KernelError::Unsupported {
        kind: "MonsterHookChoiceResume",
        id: format!("{}:{slot:?}", id.model_id_str()),
    })
}

pub fn monster_attack_intent_hits(
    state: &BattleState,
    creature: CreatureHandle,
) -> Result<SmallVec<[(Dec, i32); 2]>, KernelError> {
    let Some(c) = state.creature(creature) else {
        return Ok(SmallVec::new());
    };
    let Some(m) = &c.monster else {
        return Ok(SmallVec::new());
    };
    let mid = m.monster_id;
    let move_id = m
        .next_move_id
        .as_deref()
        .unwrap_or(m.current_state_id.as_ref());
    let dread = is_dread(state);

    match (mid, move_id) {
        // 1. AssassinRubyRaider
        (MonsterId::AssassinRubyRaider, "KILLSHOT_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 11, 10), 1)])
        }

        // 2. AxeRubyRaider
        (MonsterId::AxeRubyRaider, "SWING_1" | "SWING_2") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 6, 5), 1)])
        }
        (MonsterId::AxeRubyRaider, "BIG_SWING") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 13, 12), 1)])
        }

        // 3. BruteRubyRaider
        (MonsterId::BruteRubyRaider, "BEAT_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 8, 7), 1)])
        }
        (MonsterId::BruteRubyRaider, "ROAR_MOVE") => Ok(SmallVec::new()),

        // 4. BygoneEffigy
        (MonsterId::BygoneEffigy, "SLEEP_MOVE" | "WAKE_MOVE") => Ok(SmallVec::new()),
        (MonsterId::BygoneEffigy, "SLASHES_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 15, 13), 1)])
        }

        // 5. Byrdonis
        (MonsterId::Byrdonis, "SWOOP_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 19, 17), 1)])
        }
        (MonsterId::Byrdonis, "PECK_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 4, 3), 3)])
        }

        // 6. CalcifiedCultist
        (MonsterId::CalcifiedCultist, "INCANTATION_MOVE") => Ok(SmallVec::new()),
        (MonsterId::CalcifiedCultist, "DARK_STRIKE_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 11, 9), 1)])
        }

        // 7. CeremonialBeast
        (MonsterId::CeremonialBeast, "STAMP_MOVE" | "STUN_MOVE" | "BEAST_CRY_MOVE") => {
            Ok(SmallVec::new())
        }
        (MonsterId::CeremonialBeast, "PLOW_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 20, 18), 1)])
        }
        (MonsterId::CeremonialBeast, "STOMP_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 17, 15), 1)])
        }
        (MonsterId::CeremonialBeast, "CRUSH_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 19, 17), 1)])
        }

        // 8. CorpseSlug
        (MonsterId::CorpseSlug, "WHIP_SLAP_MOVE") => Ok(smallvec![(Dec::from_i32(3), 2)]),
        (MonsterId::CorpseSlug, "GLOMP_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8), 1)])
        }
        (MonsterId::CorpseSlug, "GOOP_MOVE") => Ok(SmallVec::new()),

        // 9. CrossbowRubyRaider
        (MonsterId::CrossbowRubyRaider, "RELOAD_MOVE") => Ok(SmallVec::new()),
        (MonsterId::CrossbowRubyRaider, "FIRE_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 16, 14), 1)])
        }

        // 10. CubexConstruct
        (MonsterId::CubexConstruct, "CHARGE_UP_MOVE") => Ok(SmallVec::new()),
        (MonsterId::CubexConstruct, "REPEATER_BLAST_MOVE" | "REPEATER_BLAST_MOVE_2") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 8, 7), 1)])
        }
        (MonsterId::CubexConstruct, "EXPEL_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 6, 5), 2)])
        }

        // 11. DampCultist
        (MonsterId::DampCultist, "INCANTATION_MOVE") => Ok(SmallVec::new()),
        (MonsterId::DampCultist, "DARK_STRIKE_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 3, 1), 1)])
        }

        // 12. Flyconid
        (MonsterId::Flyconid, "INITIAL" | "RAND" | "VULNERABLE_SPORES_MOVE") => Ok(SmallVec::new()),
        (MonsterId::Flyconid, "FRAIL_SPORES_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8), 1)])
        }
        (MonsterId::Flyconid, "SMASH_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 12, 11), 1)])
        }

        // 13. Fogmog
        (MonsterId::Fogmog, "ILLUSION_MOVE" | "BRANCH") => Ok(SmallVec::new()),
        (MonsterId::Fogmog, "SWIPE_MOVE" | "SWIPE_RANDOM_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8), 1)])
        }
        (MonsterId::Fogmog, "HEADBUTT_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 16, 14), 1)])
        }

        // 14. FossilStalker
        (MonsterId::FossilStalker, "RAND") => Ok(SmallVec::new()),
        (MonsterId::FossilStalker, "TACKLE_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 11, 9), 1)])
        }
        (MonsterId::FossilStalker, "LATCH_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 14, 12), 1)])
        }
        (MonsterId::FossilStalker, "LASH_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 4, 3), 2)])
        }

        // 15. FuzzyWurmCrawler
        (MonsterId::FuzzyWurmCrawler, "FIRST_ACID_GOOP" | "ACID_GOOP") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 6, 4), 1)])
        }
        (MonsterId::FuzzyWurmCrawler, "INHALE") => Ok(SmallVec::new()),

        // 16. GremlinMerc
        (MonsterId::GremlinMerc, "GIMME_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::ToughEnemies, 8, 7), 2)])
        }
        (MonsterId::GremlinMerc, "DOUBLE_SMASH_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::ToughEnemies, 7, 6), 2)])
        }
        (MonsterId::GremlinMerc, "HEHE_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::ToughEnemies, 9, 8), 1)])
        }

        // 17. HauntedShip
        (MonsterId::HauntedShip, "HAUNT_MOVE") => Ok(SmallVec::new()),
        (MonsterId::HauntedShip, "SWIPE_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 14, 13), 1)])
        }
        (MonsterId::HauntedShip, "STOMP_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 5, 4), 3)])
        }

        // 18. Inklet
        (MonsterId::Inklet, "RAND") => Ok(SmallVec::new()),
        (MonsterId::Inklet, "JAB_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 4, 3), 1)])
        }
        (MonsterId::Inklet, "WHIRLWIND_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 3, 2), 3)])
        }
        (MonsterId::Inklet, "PIERCING_GAZE_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 11, 10), 1)])
        }

        // 19. KinFollower
        (MonsterId::KinFollower, "QUICK_SLASH_MOVE") => Ok(smallvec![(Dec::from_i32(5), 1)]),
        (MonsterId::KinFollower, "BOOMERANG_MOVE") => Ok(smallvec![(Dec::from_i32(2), 2)]),
        (MonsterId::KinFollower, "POWER_DANCE_MOVE") => Ok(SmallVec::new()),

        // 20. KinPriest
        (MonsterId::KinPriest, "ORB_OF_FRAILTY_MOVE" | "ORB_OF_WEAKNESS_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8), 1)])
        }
        (MonsterId::KinPriest, "BEAM_MOVE") => Ok(smallvec![(Dec::from_i32(3), 3)]),
        (MonsterId::KinPriest, "RITUAL_MOVE") => Ok(SmallVec::new()),

        // 21. LagavulinMatriarch
        (MonsterId::LagavulinMatriarch, "SLEEP_MOVE" | "SLEEP_BRANCH" | "SOUL_SIPHON_MOVE") => {
            Ok(SmallVec::new())
        }
        (MonsterId::LagavulinMatriarch, "SLASH_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 21, 19), 1)])
        }
        (MonsterId::LagavulinMatriarch, "SLASH2_MOVE") => {
            let bonus = if dread { 2 } else { 0 };
            let dmg = Dec::from_i32(asc(state, AscensionLevel::DeadlyEnemies, 14, 12) + bonus);
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::LagavulinMatriarch, "DISEMBOWEL_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 10, 9), 2)])
        }

        // 22. LeafSlimeM
        (MonsterId::LeafSlimeM, "CLUMP_SHOT") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8), 1)])
        }
        (MonsterId::LeafSlimeM, "STICKY_SHOT") => Ok(SmallVec::new()),

        // 23. LeafSlimeS
        (MonsterId::LeafSlimeS, "RAND" | "GOOP_MOVE") => Ok(SmallVec::new()),
        (MonsterId::LeafSlimeS, "TACKLE_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 4, 3), 1)])
        }

        // 24. LivingFog
        (MonsterId::LivingFog, "ADVANCED_GAS_MOVE" | "SUPER_GAS_BLAST_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8), 1)])
        }
        (MonsterId::LivingFog, "BLOAT_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 6, 5), 1)])
        }

        // 25. Mawler
        (MonsterId::Mawler, "RAND" | "ROAR_MOVE") => Ok(SmallVec::new()),
        (MonsterId::Mawler, "RIP_AND_TEAR_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 16, 14), 1)])
        }
        (MonsterId::Mawler, "CLAW_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 5, 4), 2)])
        }

        // 26. Nibbit
        (MonsterId::Nibbit, "BUTT_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 13, 12), 1)])
        }
        (MonsterId::Nibbit, "SLICE_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 7, 6), 1)])
        }
        (MonsterId::Nibbit, "HISS_MOVE" | "INIT_MOVE") => Ok(SmallVec::new()),

        // 27. PhantasmalGardener
        (MonsterId::PhantasmalGardener, "INIT_MOVE" | "ENLARGE_MOVE") => Ok(SmallVec::new()),
        (MonsterId::PhantasmalGardener, "BITE_MOVE") => Ok(smallvec![(Dec::from_i32(5), 1)]),
        (MonsterId::PhantasmalGardener, "LASH_MOVE") => Ok(smallvec![(Dec::from_i32(7), 1)]),
        (MonsterId::PhantasmalGardener, "FLAIL_MOVE") => Ok(smallvec![(Dec::ONE, 3)]),

        // 28. PhrogParasite
        (MonsterId::PhrogParasite, "INFECT_MOVE") => Ok(SmallVec::new()),
        (MonsterId::PhrogParasite, "LASH_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 5, 4), 4)])
        }

        // 29. PunchConstruct
        (MonsterId::PunchConstruct, "READY_MOVE") => Ok(SmallVec::new()),
        (MonsterId::PunchConstruct, "FAST_PUNCH_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 6, 5), 2)])
        }
        (MonsterId::PunchConstruct, "STRONG_PUNCH_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 16, 14), 1)])
        }

        // 30. Seapunk
        (MonsterId::Seapunk, "SEA_KICK_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 13, 11), 1)])
        }
        (MonsterId::Seapunk, "SPINNING_KICK_MOVE") => Ok(smallvec![(Dec::from_i32(2), 4)]),
        (MonsterId::Seapunk, "BUBBLE_BURP_MOVE") => Ok(SmallVec::new()),

        // 31. SewerClam
        (MonsterId::SewerClam, "JET_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 11, 10), 1)])
        }
        (MonsterId::SewerClam, "PRESSURIZE_MOVE") => Ok(SmallVec::new()),

        // 32. ShrinkerBeetle
        (MonsterId::ShrinkerBeetle, "CHOMP_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 8, 7), 1)])
        }
        (MonsterId::ShrinkerBeetle, "STOMP_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 14, 13), 1)])
        }
        (MonsterId::ShrinkerBeetle, "SHRINKER_MOVE") => Ok(SmallVec::new()),

        // 33. SkulkingColony
        (MonsterId::SkulkingColony, "ZOOM_MOVE" | "ZOOM_MOVE_2") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 16, 14), 1)])
        }
        (MonsterId::SkulkingColony, "INERTIA_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 11, 9), 1)])
        }
        (MonsterId::SkulkingColony, "PIERCING_STABS_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 8, 7), 2)])
        }

        // 34. SlitheringStrangler
        (MonsterId::SlitheringStrangler, "CONSTRICT" | "rand") => Ok(SmallVec::new()),
        (MonsterId::SlitheringStrangler, "THWACK") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 8, 7), 1)])
        }
        (MonsterId::SlitheringStrangler, "LASH") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 13, 12), 1)])
        }

        // 35. SludgeSpinner
        (MonsterId::SludgeSpinner, "RAND") => Ok(SmallVec::new()),
        (MonsterId::SludgeSpinner, "OIL_SPRAY_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8), 1)])
        }
        (MonsterId::SludgeSpinner, "SLAM_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 12, 11), 1)])
        }
        (MonsterId::SludgeSpinner, "RAGE_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 7, 6), 1)])
        }

        // 36. SnappingJaxfruit
        (MonsterId::SnappingJaxfruit, "ENERGY_ORB_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 4, 3), 1)])
        }

        // 37. SoulFysh
        (MonsterId::SoulFysh, "BECKON_MOVE" | "FADE_MOVE") => Ok(SmallVec::new()),
        (MonsterId::SoulFysh, "DE_GAS_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 18, 16), 1)])
        }
        (MonsterId::SoulFysh, "GAZE_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 8, 7), 1)])
        }
        (MonsterId::SoulFysh, "SCREAM_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 15, 13), 1)])
        }

        // 38. TerrorEel
        (MonsterId::TerrorEel, "CRASH_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 18, 16), 1)])
        }
        (MonsterId::TerrorEel, "THRASH_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 4, 3), 3)])
        }
        (MonsterId::TerrorEel, "STUN_MOVE" | "TERROR_MOVE") => Ok(SmallVec::new()),

        // 39. Toadpole
        (MonsterId::Toadpole, "INIT_MOVE" | "SPIKEN_MOVE") => Ok(SmallVec::new()),
        (MonsterId::Toadpole, "SPIKE_SPIT_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 4, 3), 3)])
        }
        (MonsterId::Toadpole, "WHIRL_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 8, 7), 1)])
        }

        // 40. TrackerRubyRaider
        (MonsterId::TrackerRubyRaider, "TRACK_MOVE") => Ok(SmallVec::new()),
        (MonsterId::TrackerRubyRaider, "HOUNDS_MOVE") => {
            let repeat = asc(state, AscensionLevel::DeadlyEnemies, 9, 8);
            Ok(smallvec![(Dec::ONE, repeat)])
        }

        // 41. TwigSlimeM
        (MonsterId::TwigSlimeM, "RAND" | "STICKY_SHOT_MOVE") => Ok(SmallVec::new()),
        (MonsterId::TwigSlimeM, "POKEY_POUNCE_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 12, 11), 1)])
        }

        // 42. TwigSlimeS
        (MonsterId::TwigSlimeS, "TACKLE_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 5, 4), 1)])
        }

        // 43. TwoTailedRat
        (MonsterId::TwoTailedRat, "RAND" | "SCREECH_MOVE" | "CALL_FOR_BACKUP_MOVE") => {
            Ok(SmallVec::new())
        }
        (MonsterId::TwoTailedRat, "SCRATCH_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8), 1)])
        }
        (MonsterId::TwoTailedRat, "DISEASE_BITE_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 7, 6), 1)])
        }

        // 44. Vantom
        (MonsterId::Vantom, "INK_BLOT_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 8, 7), 1)])
        }
        (MonsterId::Vantom, "INKY_LANCE_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 7, 6), 2)])
        }
        (MonsterId::Vantom, "DISMEMBER_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 30, 26), 1)])
        }
        (MonsterId::Vantom, "PREPARE_MOVE") => Ok(SmallVec::new()),

        // 45. VineShambler
        (MonsterId::VineShambler, "GRASPING_VINES_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 9, 8), 1)])
        }
        (MonsterId::VineShambler, "SWIPE_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 7, 6), 2)])
        }
        (MonsterId::VineShambler, "CHOMP_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 18, 16), 1)])
        }

        // 46. WaterfallGiant
        (
            MonsterId::WaterfallGiant,
            "PRESSURIZE_MOVE" | "SIPHON_MOVE" | "ABOUT_TO_BLOW_MOVE",
        ) => Ok(SmallVec::new()),
        (MonsterId::WaterfallGiant, "STOMP_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 16, 15), 1)])
        }
        (MonsterId::WaterfallGiant, "RAM_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 11, 10), 1)])
        }
        (MonsterId::WaterfallGiant, "PRESSURE_GUN_MOVE") => {
            let base = asc(state, AscensionLevel::DeadlyEnemies, 23, 20);
            let cur_pg = read_monster_int(
                state,
                creature,
                "WaterfallGiant._currentPressureGunDamage",
                "CurrentPressureGunDamage",
                base,
            );
            Ok(smallvec![(Dec::from_i32(cur_pg), 1)])
        }
        (MonsterId::WaterfallGiant, "PRESSURE_UP_MOVE") => {
            Ok(smallvec![(dec_asc(state, AscensionLevel::DeadlyEnemies, 14, 13), 1)])
        }
        (MonsterId::WaterfallGiant, "EXPLODE_MOVE") => {
            let dmg = read_monster_int(
                state,
                creature,
                "WaterfallGiant._steamEruptionDamage",
                "SteamEruptionDamage",
                0,
            );
            Ok(smallvec![(Dec::from_i32(dmg), 1)])
        }

        _ => Err(KernelError::Unsupported {
            kind: "MonsterIntent",
            id: format!("{}:{}", mid.model_id_str(), move_id),
        }),
    }
}
