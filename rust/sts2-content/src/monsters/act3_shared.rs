use smallvec::{SmallVec, smallvec};
use std::borrow::Cow;
use std::collections::BTreeSet;
use sts2_kernel::commands::{arrow, block, card_pile, creature as creature_cmd, damage::AttackBuilder, power};
use sts2_kernel::{
    BattleState, CardHandle, ContentDispatcher, CreatureHandle, CreatureInst, FieldValue,
    HookArgs, HookResult, KernelError, MonsterInst,
};
use sts2_types::{
    AscensionLevel, CardId, CardPilePosition, CombatSide, Dec, GameMode, HookMask, HookSlot,
    MonsterId, MonsterMeta, PileType, PowerId, Rng, RunRngType, ValueProp,
};

static EMPTY_MONSTER_META: MonsterMeta = MonsterMeta {
    hook_mask: HookMask::EMPTY,
};

static AEONGLASS_META: MonsterMeta = MonsterMeta {
    hook_mask: HookMask::EMPTY.with(HookSlot::AfterCardGeneratedForCombat),
};

static BATTLE_FRIEND_META: MonsterMeta = MonsterMeta {
    hook_mask: HookMask::EMPTY.with(HookSlot::AfterSideTurnEnd),
};

static QUEEN_META: MonsterMeta = MonsterMeta {
    hook_mask: HookMask::EMPTY.with(HookSlot::AfterDeath),
};

static TEST_SUBJECT_META: MonsterMeta = MonsterMeta {
    hook_mask: HookMask::EMPTY.with(HookSlot::ShouldDisappearFromDoom),
};

pub fn monster_meta(id: MonsterId) -> Result<&'static MonsterMeta, KernelError> {
    match id {
        MonsterId::Aeonglass => Ok(&AEONGLASS_META),
        MonsterId::BattleFriendV1 | MonsterId::BattleFriendV2 | MonsterId::BattleFriendV3 => {
            Ok(&BATTLE_FRIEND_META)
        }
        MonsterId::Queen => Ok(&QUEEN_META),
        MonsterId::TestSubject => Ok(&TEST_SUBJECT_META),
        MonsterId::ActOneGuardian
        | MonsterId::Axebot
        | MonsterId::ByrdineMonster
        | MonsterId::Byrdpip
        | MonsterId::DevotedSculptor
        | MonsterId::EyeWithTeeth
        | MonsterId::Fabricator
        | MonsterId::FakeMerchantMonster
        | MonsterId::FatGremlin
        | MonsterId::FlailKnight
        | MonsterId::FrogKnight
        | MonsterId::GasBomb
        | MonsterId::GlobeHead
        | MonsterId::Guardbot
        | MonsterId::HardyBrute
        | MonsterId::LivingShield
        | MonsterId::MagiKnight
        | MonsterId::MechaKnight
        | MonsterId::MysteriousKnight
        | MonsterId::Noisebot
        | MonsterId::Osty
        | MonsterId::OwlMagistrate
        | MonsterId::PaelsLegion
        | MonsterId::Parafright
        | MonsterId::ScrollOfBiting
        | MonsterId::SlimedBerserker
        | MonsterId::SneakyGremlin
        | MonsterId::SoulNexus
        | MonsterId::SpectralKnight
        | MonsterId::Stabbot
        | MonsterId::TheForgotten
        | MonsterId::TheLost
        | MonsterId::TorchHeadAmalgam
        | MonsterId::ToughEgg
        | MonsterId::TrainingDummy
        | MonsterId::TurretOperator
        | MonsterId::WanderingGrunt
        | MonsterId::Wriggler
        | MonsterId::Wuppy
        | MonsterId::Zapbot => Ok(&EMPTY_MONSTER_META),
        _ => Err(KernelError::Unsupported {
            kind: "Monster",
            id: id.model_id_str().to_owned(),
        }),
    }
}

#[inline]
fn asc_active(state: &BattleState, level: AscensionLevel) -> bool {
    crate::dread_blitz::is_ascension_active_in_mode(
        state.static_ctx.game_mode,
        state.static_ctx.ascension,
        level,
    )
}

#[inline]
fn asc_val(state: &BattleState, level: AscensionLevel, high: i32, low: i32) -> i32 {
    if asc_active(state, level) {
        high
    } else {
        low
    }
}

#[inline]
fn is_dread(state: &BattleState) -> bool {
    state.static_ctx.game_mode == GameMode::Dread
}

fn read_monster_bool(state: &BattleState, creature: CreatureHandle, key: &str, default: bool) -> bool {
    let Some(m) = state.creature(creature).and_then(|c| c.monster.as_ref()) else {
        return default;
    };
    if let Some(FieldValue::Bool(b)) = m.private_fields.get(key) {
        return *b;
    }
    if let Some(FieldValue::Bool(b)) = m.setup_fields.get(key) {
        return *b;
    }
    default
}

fn read_monster_int(state: &BattleState, creature: CreatureHandle, key: &str, default: i32) -> i32 {
    let Some(m) = state.creature(creature).and_then(|c| c.monster.as_ref()) else {
        return default;
    };
    if let Some(FieldValue::Int(v)) = m.private_fields.get(key) {
        return *v;
    }
    if let Some(FieldValue::Int(v)) = m.setup_fields.get(key) {
        return *v;
    }
    default
}

fn read_monster_nullable_int(state: &BattleState, creature: CreatureHandle, key: &str) -> Option<i32> {
    let m = state.creature(creature).and_then(|c| c.monster.as_ref())?;
    match m.private_fields.get(key) {
        Some(FieldValue::NullableInt(v)) => return *v,
        Some(FieldValue::Int(v)) => return Some(*v),
        _ => {}
    }
    match m.setup_fields.get(key) {
        Some(FieldValue::NullableInt(v)) => *v,
        Some(FieldValue::Int(v)) => Some(*v),
        _ => None,
    }
}

fn read_monster_string(state: &BattleState, creature: CreatureHandle, key: &str) -> Option<String> {
    let m = state.creature(creature).and_then(|c| c.monster.as_ref())?;
    if let Some(FieldValue::String(s)) = m.private_fields.get(key) {
        return Some(s.clone());
    }
    if let Some(FieldValue::String(s)) = m.setup_fields.get(key) {
        return Some(s.clone());
    }
    None
}

fn set_monster_field(
    state: &mut BattleState,
    creature: CreatureHandle,
    key: &'static str,
    val: FieldValue,
) {
    if let Some(m) = state
        .creature_mut(creature)
        .and_then(|c| c.monster.as_mut())
    {
        m.private_fields.insert(Cow::Borrowed(key), val);
    }
}

fn remove_monster_field(state: &mut BattleState, creature: CreatureHandle, key: &str) {
    if let Some(m) = state
        .creature_mut(creature)
        .and_then(|c| c.monster.as_mut())
    {
        m.private_fields.remove(key);
    }
}

fn power_amount(state: &BattleState, creature: CreatureHandle, power_id: PowerId) -> i32 {
    state
        .creature(creature)
        .and_then(|c| c.powers.iter().find(|p| p.id == power_id))
        .map_or(0, |p| p.amount)
}

#[derive(Clone, Copy)]
#[allow(dead_code)]
enum RepeatRule {
    CanRepeatForever,
    CannotRepeat,
    UseOnlyOnce,
    CanRepeatXTimes(usize),
}

#[derive(Clone, Copy)]
struct RandBranch {
    state_id: &'static str,
    repeat: RepeatRule,
    cooldown: usize,
    weight: f32,
}

fn is_move_state(mid: MonsterId, state_id: &str) -> bool {
    match mid {
        MonsterId::Fabricator => !matches!(state_id, "fabricateBranch" | "RAND"),
        MonsterId::FakeMerchantMonster => !matches!(state_id, "RAND_MOVE" | "RAND_ATTACK_MOVE"),
        MonsterId::FlailKnight | MonsterId::MysteriousKnight | MonsterId::SoulNexus | MonsterId::SpectralKnight => {
            state_id != "RAND"
        }
        MonsterId::FrogKnight => state_id != "HALF_HEALTH",
        MonsterId::LivingShield => state_id != "SHIELD_SLAM_BRANCH",
        MonsterId::Queen => {
            !matches!(state_id, "YOURE_MINE_NOW_BRANCH" | "BURN_BRIGHT_FOR_ME_BRANCH")
        }
        MonsterId::ScrollOfBiting => state_id != "rand",
        MonsterId::TestSubject => state_id != "REVIVE_BRANCH",
        MonsterId::Wriggler => state_id != "INIT_MOVE",
        _ => true,
    }
}

fn branch_weight(mid: MonsterId, state_log_ids: &[Cow<'static, str>], branch: RandBranch) -> f32 {
    let mut repeat_weight = 1.0_f32;
    match branch.repeat {
        RepeatRule::UseOnlyOnce => {
            if state_log_ids.iter().any(|id| id.as_ref() == branch.state_id) {
                repeat_weight = 0.0;
            }
        }
        RepeatRule::CanRepeatForever => {}
        RepeatRule::CannotRepeat | RepeatRule::CanRepeatXTimes(_) => {
            let repeat_limit = match branch.repeat {
                RepeatRule::CannotRepeat => 1,
                RepeatRule::CanRepeatXTimes(n) => n,
                _ => 0,
            };
            if repeat_limit > 0 {
                repeat_weight = if state_log_ids.len() < repeat_limit {
                    1.0
                } else {
                    0.0
                };
                let mut checked = 0usize;
                while state_log_ids.len() >= repeat_limit
                    && checked < repeat_limit
                    && state_log_ids.len() > checked
                {
                    if state_log_ids[state_log_ids.len() - 1 - checked].as_ref() != branch.state_id
                    {
                        repeat_weight = 1.0;
                        break;
                    }
                    checked += 1;
                }
            }
        }
    }

    if branch.cooldown > 0 {
        let on_cooldown = state_log_ids
            .iter()
            .filter(|id| is_move_state(mid, id.as_ref()))
            .rev()
            .take(branch.cooldown)
            .any(|id| id.as_ref() == branch.state_id);
        if on_cooldown {
            return 0.0;
        }
    }

    repeat_weight * branch.weight
}

fn select_random_branch(
    state: &mut BattleState,
    creature: CreatureHandle,
    mid: MonsterId,
    branches: &[RandBranch],
    branch_name: &str,
) -> Result<&'static str, KernelError> {
    let mut weights: SmallVec<[f32; 4]> = SmallVec::with_capacity(branches.len());
    let mut total_weight = 0.0_f32;
    {
        let log = &state
            .creature(creature)
            .and_then(|c| c.monster.as_ref())
            .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?
            .state_log_ids;
        for &b in branches {
            let w = branch_weight(mid, log, b);
            weights.push(w);
            total_weight += w;
        }
    }
    let mut r = state
        .run_rngs
        .get_mut(RunRngType::MonsterAi)
        .next_f32_range(0.0, total_weight)?;
    for (idx, &b) in branches.iter().enumerate() {
        r -= weights[idx];
        if r <= 0.0 {
            return Ok(b.state_id);
        }
    }
    Err(KernelError::InvalidState(format!(
        "No valid state found in RandomBranchState {branch_name}!"
    )))
}

fn escape_creature(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    if state.creature(creature).is_none_or(|c| c.is_dead())
        || !state.is_live_combat()
        || !state.contains_creature(creature)
    {
        return Ok(());
    }

    let impaled_list: SmallVec<[(CreatureHandle, usize); 2]> = state
        .creature(creature)
        .map(|c| {
            c.powers
                .iter()
                .filter(|p| p.id == PowerId::ImpaledPower)
                .filter_map(|p| Some((p.applier_handle?, p.ext_index? as usize)))
                .collect()
        })
        .unwrap_or_default();
    for (applier, ext_idx) in impaled_list {
        while !state.power_extensions[ext_idx].impaled_arrows.is_empty() {
            arrow::dislodge_first_arrow(state, content, creature, applier, None)?;
        }
    }

    if let Some(c) = state.creature_mut(creature) {
        c.powers.clear();
        c.has_minion_power = false;
        c.is_detached = true;
    }
    state.ally_handles.retain(|h| *h != creature);
    state.enemy_handles.retain(|h| *h != creature);
    if !state.escaped_handles.contains(&creature) {
        state.escaped_handles.push(creature);
    }
    state.invalidate_hook_cache();
    Ok(())
}

fn roll_unique_monster_hp(
    state: &mut BattleState,
    min_hp: i32,
    max_hp: i32,
) -> Result<i32, KernelError> {
    if min_hp > max_hp {
        return Err(KernelError::InvalidState(format!(
            "monster min HP {min_hp} greater than max {max_hp}"
        )));
    }
    let mut used = BTreeSet::new();
    for &h in &state.enemy_handles {
        if let Some(other) = state.creature(h) {
            if other.is_monster() && other.max_hp >= min_hp && other.max_hp <= max_hp {
                used.insert(other.max_hp);
            }
        }
    }
    let range_size = (max_hp as i64) - (min_hp as i64) + 1;
    let available = range_size - (used.len() as i64);
    let rng = state.run_rngs.get_mut(RunRngType::Niche);
    if available > 0 {
        let mut ordinal = rng.next_int_range(0, available as i32)? as i64;
        let mut candidate = min_hp as i64;
        for u in used {
            let gap = (u as i64) - candidate;
            if ordinal < gap {
                return Ok((candidate + ordinal) as i32);
            }
            ordinal -= gap;
            candidate = (u as i64) + 1;
        }
        Ok((candidate + ordinal) as i32)
    } else {
        Ok(rng.next_int_range(min_hp, max_hp + 1)?)
    }
}

fn spawn_enemy_with_unique_hp(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    monster_id: MonsterId,
    slot_name: Option<Cow<'static, str>>,
) -> Result<CreatureHandle, KernelError> {
    if !state.is_live_combat() {
        return Err(KernelError::InvalidAction(
            "Monsters can only be added to a live combat.".to_owned(),
        ));
    }
    let combat_id = state.next_creature_id;
    state.next_creature_id += 1;
    let handle = CreatureHandle(state.next_creature_handle);
    state.next_creature_handle += 1;

    let (min_hp, max_hp) = content.monster_initial_hp_range(state, monster_id)?;
    let rolled_hp = roll_unique_monster_hp(state, min_hp, max_hp)?;

    let monster_seed = (state.run_rngs.seed as i64)
        .wrapping_add(state.static_ctx.map_col as i64)
        .wrapping_add(state.static_ctx.map_row as i64)
        .wrapping_add(state.static_ctx.act_index as i64)
        .wrapping_add(combat_id as i64) as u64;

    let creature = CreatureInst {
        handle,
        combat_id: Some(combat_id),
        side: CombatSide::Enemy,
        slot_name: slot_name.clone(),
        is_player: false,
        pet_owner_player_index: None,
        monster_id: Some(monster_id),
        current_hp: rolled_hp,
        max_hp: rolled_hp,
        block: 0,
        cumulative_hp_lost: 0,
        is_detached: false,
        has_minion_power: false,
        monster: Some(MonsterInst {
            monster_id,
            rng: Rng::from_seed(monster_seed),
            is_performing_move: false,
            spawned_this_turn: true,
            current_state_id: Cow::Borrowed(""),
            next_move_id: None,
            next_move_can_transition_away: true,
            performed_first_move: false,
            state_log_ids: Vec::new(),
            transient_move_follow_up_state_id: None,
            setup_fields: Default::default(),
            private_fields: Default::default(),
        }),
        powers: Vec::new(),
    };

    state.creatures.push(creature);
    state.enemy_handles.push(handle);
    state.spawned_enemy_handles.push(handle);
    state.invalidate_creature_hooks();

    content.monster_setup_for_combat(state, handle)?;
    if slot_name.is_some() && !state.static_ctx.encounter_slots.is_empty() {
        let slots = state.static_ctx.encounter_slots.clone();
        let mut sorted: SmallVec<[CreatureHandle; 8]> =
            SmallVec::from_slice(&state.enemy_handles);
        sorted.sort_by_key(|&h| {
            let sname = state.creature(h).and_then(|c| c.slot_name.as_deref());
            slots
                .iter()
                .position(|x| Some(x.as_str()) == sname)
                .unwrap_or(usize::MAX)
        });
        state.enemy_handles.clear();
        state.enemy_handles.extend_from_slice(&sorted);
    }
    content.monster_after_added_to_room(state, handle)?;
    if state.current_side == CombatSide::Player {
        content.monster_roll_move(state, handle)?;
    }
    sts2_kernel::hooks::after_creature_added_to_combat(state, content, handle)?;
    Ok(handle)
}

pub fn monster_initial_hp_range(
    state: &BattleState,
    monster_id: MonsterId,
) -> Result<(i32, i32), KernelError> {
    let tough = |high: i32, low: i32| asc_val(state, AscensionLevel::ToughEnemies, high, low);
    match monster_id {
        MonsterId::ActOneGuardian => Ok((230, 250)),
        MonsterId::Aeonglass => {
            let hp = tough(535, 512);
            Ok((hp, hp))
        }
        MonsterId::Axebot => {
            let hp = tough(80, 70);
            Ok((hp, hp))
        }
        MonsterId::BattleFriendV1 => Ok((75, 75)),
        MonsterId::BattleFriendV2 => Ok((150, 150)),
        MonsterId::BattleFriendV3 => Ok((300, 300)),
        MonsterId::ByrdineMonster | MonsterId::Byrdpip | MonsterId::PaelsLegion => Ok((9999, 9999)),
        MonsterId::DevotedSculptor => {
            let hp = tough(172, 162);
            Ok((hp, hp))
        }
        MonsterId::EyeWithTeeth => Ok((6, 6)),
        MonsterId::Fabricator => {
            let hp = tough(155, 150);
            Ok((hp, hp))
        }
        MonsterId::FakeMerchantMonster => {
            let hp = tough(175, 165);
            Ok((hp, hp))
        }
        MonsterId::FatGremlin => Ok((tough(14, 13), tough(18, 17))),
        MonsterId::FlailKnight | MonsterId::MysteriousKnight => {
            let hp = tough(108, 101);
            Ok((hp, hp))
        }
        MonsterId::FrogKnight => {
            let hp = tough(199, 191);
            Ok((hp, hp))
        }
        MonsterId::GasBomb => {
            let hp = tough(8, 7);
            Ok((hp, hp))
        }
        MonsterId::GlobeHead => {
            let hp = tough(158, 148);
            Ok((hp, hp))
        }
        MonsterId::Guardbot => Ok((tough(17, 16), tough(21, 20))),
        MonsterId::HardyBrute => Ok((130, 140)),
        MonsterId::LivingShield => {
            let hp = tough(65, 55);
            Ok((hp, hp))
        }
        MonsterId::MagiKnight => {
            let hp = tough(89, 82);
            Ok((hp, hp))
        }
        MonsterId::MechaKnight => {
            let hp = tough(320, 300);
            Ok((hp, hp))
        }
        MonsterId::Noisebot | MonsterId::Stabbot | MonsterId::Zapbot => {
            Ok((tough(19, 18), tough(24, 23)))
        }
        MonsterId::Osty => Ok((1, 1)),
        MonsterId::OwlMagistrate => {
            let hp = tough(247, 231);
            Ok((hp, hp))
        }
        MonsterId::Parafright => Ok((21, 21)),
        MonsterId::Queen => {
            let hp = tough(419, 400);
            Ok((hp, hp))
        }
        MonsterId::ScrollOfBiting => Ok((tough(33, 30), tough(39, 37))),
        MonsterId::SlimedBerserker => {
            let hp = tough(281, 261);
            Ok((hp, hp))
        }
        MonsterId::SneakyGremlin => Ok((tough(11, 10), tough(15, 14))),
        MonsterId::SoulNexus => {
            let hp = tough(254, 234);
            Ok((hp, hp))
        }
        MonsterId::SpectralKnight => {
            let hp = tough(97, 93);
            Ok((hp, hp))
        }
        MonsterId::TestSubject => {
            let hp = tough(111, 100);
            Ok((hp, hp))
        }
        MonsterId::TheForgotten => {
            let hp = tough(111, 106);
            Ok((hp, hp))
        }
        MonsterId::TheLost => {
            let hp = tough(99, 93);
            Ok((hp, hp))
        }
        MonsterId::TorchHeadAmalgam => {
            let hp = tough(211, 199);
            Ok((hp, hp))
        }
        MonsterId::ToughEgg => Ok((tough(15, 14), tough(19, 18))),
        MonsterId::TrainingDummy => Ok((20, 20)),
        MonsterId::TurretOperator => {
            let hp = tough(51, 41);
            Ok((hp, hp))
        }
        MonsterId::WanderingGrunt => Ok((42, 48)),
        MonsterId::Wriggler => Ok((tough(18, 17), tough(22, 21))),
        MonsterId::Wuppy => Ok((50, 50)),
        _ => Err(KernelError::Unsupported {
            kind: "Monster",
            id: monster_id.model_id_str().to_owned(),
        }),
    }
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

    if mid == MonsterId::Axebot {
        let setup_stock = state
            .creature(creature)
            .and_then(|c| c.monster.as_ref())
            .and_then(|m| match m.setup_fields.get("Axebot._stockAmount") {
                Some(FieldValue::NullableInt(v)) => *v,
                Some(FieldValue::Int(v)) => Some(*v),
                _ => None,
            });
        let stock_opt = read_monster_nullable_int(state, creature, "Axebot._stockAmount");
        if let Some(stock) = setup_stock {
            set_monster_field(
                state,
                creature,
                "Axebot._stockAmount",
                FieldValue::NullableInt(Some(stock)),
            );
            let respawn_hp = asc_val(state, AscensionLevel::ToughEnemies, 80, 70) + (2 - stock) * 10;
            if let Some(c) = state.creature_mut(creature) {
                c.max_hp = respawn_hp;
                c.current_hp = respawn_hp;
            }
            if state.is_live_combat() {
                let _ = state
                    .run_rngs
                    .get_mut(RunRngType::Niche)
                    .next_int_range(0, 1)?;
            }
        }
        let initial = if stock_opt.is_some() {
            "BOOT_UP_MOVE"
        } else {
            "HAMMER_UPPERCUT_MOVE"
        };
        if let Some(c) = state.creature_mut(creature) {
            if let Some(m) = &mut c.monster {
                if m.current_state_id.is_empty() {
                    m.current_state_id = Cow::Borrowed(initial);
                    if m.state_log_ids.is_empty() {
                        m.state_log_ids.push(Cow::Borrowed(initial));
                    }
                }
            }
        }
        return Ok(());
    }

    let initial_state = match mid {
        MonsterId::ActOneGuardian => "CRUSH",
        MonsterId::Aeonglass => "EBB_MOVE",
        MonsterId::BattleFriendV1 | MonsterId::BattleFriendV2 | MonsterId::BattleFriendV3 => {
            "IDLE"
        }
        MonsterId::ByrdineMonster
        | MonsterId::Byrdpip
        | MonsterId::Osty
        | MonsterId::PaelsLegion
        | MonsterId::Wuppy => "NOTHING_MOVE",
        MonsterId::DevotedSculptor => "FORBIDDEN_INCANTATION_MOVE",
        MonsterId::EyeWithTeeth => "DISTRACT_MOVE",
        MonsterId::Fabricator => "fabricateBranch",
        MonsterId::FakeMerchantMonster => "SWIPE_MOVE",
        MonsterId::FatGremlin | MonsterId::SneakyGremlin => "SPAWNED_MOVE",
        MonsterId::FlailKnight | MonsterId::MysteriousKnight => "RAM_MOVE",
        MonsterId::FrogKnight => "TONGUE_LASH",
        MonsterId::GasBomb => "EXPLODE_MOVE",
        MonsterId::GlobeHead => "SHOCKING_SLAP",
        MonsterId::Guardbot => "GUARD_MOVE",
        MonsterId::HardyBrute => "SLAM",
        MonsterId::LivingShield => "SHIELD_SLAM_MOVE",
        MonsterId::MagiKnight => "POWER_SHIELD_MOVE",
        MonsterId::MechaKnight => "CHARGE_MOVE",
        MonsterId::Noisebot => "NOISE_MOVE",
        MonsterId::OwlMagistrate => "MAGISTRATE_SCRUTINY",
        MonsterId::Parafright => "SLAM_MOVE",
        MonsterId::Queen => {
            if is_dread(state) {
                "DREAD_INITIAL_MOVE"
            } else {
                "PUPPET_STRINGS_MOVE"
            }
        }
        MonsterId::ScrollOfBiting => {
            let idx = read_monster_int(state, creature, "ScrollOfBiting._starterMoveIdx", 0);
            match idx.rem_euclid(3) {
                0 => "CHOMP",
                1 => "CHEW",
                _ => "MORE_TEETH",
            }
        }
        MonsterId::SlimedBerserker => "VOMIT_ICHOR_MOVE",
        MonsterId::SoulNexus => "SOUL_BURN_MOVE",
        MonsterId::SpectralKnight => "HEX",
        MonsterId::Stabbot => "STAB_MOVE",
        MonsterId::TestSubject => "BITE_MOVE",
        MonsterId::TheForgotten => "MIASMA",
        MonsterId::TheLost => "DEBILITATING_SMOG",
        MonsterId::TorchHeadAmalgam => "STRONG_TACKLE_MOVE",
        MonsterId::ToughEgg => {
            if read_monster_string(state, creature, "ToughEgg._afterHatchedState").is_none() {
                set_monster_field(
                    state,
                    creature,
                    "ToughEgg._afterHatchedState",
                    FieldValue::String("NIBBLE_MOVE".to_owned()),
                );
            }
            "HATCH_MOVE"
        }
        MonsterId::TrainingDummy => "POKE",
        MonsterId::TurretOperator => "UNLOAD_MOVE",
        MonsterId::WanderingGrunt => "BITE",
        MonsterId::Wriggler => {
            if read_monster_bool(state, creature, "Wriggler._startStunned", false) {
                "SPAWNED_MOVE"
            } else {
                "INIT_MOVE"
            }
        }
        MonsterId::Zapbot => "ZAP",
        MonsterId::Axebot => unreachable!(),
        _ => {
            return Err(KernelError::Unsupported {
                kind: "Monster",
                id: mid.model_id_str().to_owned(),
            });
        }
    };

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
        MonsterId::Aeonglass => {
            let pc = state.player.creature_handle;
            let p_idx = power::apply(
                state,
                content,
                PowerId::WitheringPresencePower,
                creature,
                Dec::from_i32(6),
                Some(creature),
                None,
            )?;
            if let Some(idx) = p_idx {
                let ext_idx = if let Some(ei) = state
                    .creature(creature)
                    .and_then(|c| c.powers.get(idx).and_then(|p| p.ext_index))
                {
                    ei as usize
                } else {
                    let ei = state.power_extensions.len();
                    state.power_extensions.push(Default::default());
                    if let Some(p) = state
                        .creature_mut(creature)
                        .and_then(|c| c.powers.get_mut(idx))
                    {
                        p.ext_index = Some(ei as u32);
                    }
                    ei
                };
                state.power_extensions[ext_idx].private_fields.insert(
                    Cow::Borrowed("WitheringPresencePower.<Target>k__BackingField"),
                    FieldValue::CreatureHandle(pc.0 as i32),
                );
            }
            let _ = power::apply(
                state,
                content,
                PowerId::ArtifactPower,
                creature,
                Dec::from_i32(3),
                Some(creature),
                None,
            )?;
        }
        MonsterId::Axebot => {
            let stock = read_monster_nullable_int(state, creature, "Axebot._stockAmount").unwrap_or(2);
            if stock > 0 {
                let _ = power::apply(
                    state,
                    content,
                    PowerId::StockPower,
                    creature,
                    Dec::from_i32(stock),
                    None,
                    None,
                )?;
            }
        }
        MonsterId::BattleFriendV1 | MonsterId::BattleFriendV2 | MonsterId::BattleFriendV3 => {
            let _ = power::apply(
                state,
                content,
                PowerId::BattlewornDummyTimeLimitPower,
                creature,
                Dec::from_i32(3),
                None,
                None,
            )?;
        }
        MonsterId::EyeWithTeeth | MonsterId::Parafright => {
            let _ = power::apply(
                state,
                content,
                PowerId::IllusionPower,
                creature,
                Dec::ONE,
                Some(creature),
                None,
            )?;
        }
        MonsterId::FrogKnight => {
            let plating = asc_val(state, AscensionLevel::ToughEnemies, 19, 15);
            let _ = power::apply(
                state,
                content,
                PowerId::PlatingPower,
                creature,
                Dec::from_i32(plating),
                Some(creature),
                None,
            )?;
            set_monster_field(state, creature, "FrogKnight._charged", FieldValue::Bool(false));
        }
        MonsterId::GasBomb | MonsterId::TorchHeadAmalgam => {
            let _ = power::apply(
                state,
                content,
                PowerId::MinionPower,
                creature,
                Dec::ONE,
                Some(creature),
                None,
            )?;
        }
        MonsterId::GlobeHead => {
            let galvanic = asc_val(state, AscensionLevel::DeadlyEnemies, 8, 6);
            let _ = power::apply(
                state,
                content,
                PowerId::GalvanicPower,
                creature,
                Dec::from_i32(galvanic),
                Some(creature),
                None,
            )?;
        }
        MonsterId::LivingShield => {
            let _ = power::apply(
                state,
                content,
                PowerId::RampartPower,
                creature,
                Dec::from_i32(25),
                Some(creature),
                None,
            )?;
        }
        MonsterId::MechaKnight => {
            let _ = power::apply(
                state,
                content,
                PowerId::ArtifactPower,
                creature,
                Dec::from_i32(3),
                Some(creature),
                None,
            )?;
        }
        MonsterId::MysteriousKnight => {
            let _ = power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                Dec::from_i32(6),
                Some(creature),
                None,
            )?;
            let _ = power::apply(
                state,
                content,
                PowerId::PlatingPower,
                creature,
                Dec::from_i32(6),
                Some(creature),
                None,
            )?;
        }
        MonsterId::Queen => {
            let amalgam = state.enemy_handles.iter().copied().find(|&h| {
                state
                    .creature(h)
                    .is_some_and(|c| c.monster_id == Some(MonsterId::TorchHeadAmalgam))
            });
            if let Some(ah) = amalgam {
                set_monster_field(
                    state,
                    creature,
                    "Queen._amalgam",
                    FieldValue::CreatureHandle(ah.0 as i32),
                );
            }
            if !read_monster_bool(state, creature, "Queen._hasAmalgamDied", false) {
                set_monster_field(state, creature, "Queen._hasAmalgamDied", FieldValue::Bool(false));
            }
        }
        MonsterId::ScrollOfBiting => {
            let _ = power::apply(
                state,
                content,
                PowerId::PaperCutsPower,
                creature,
                Dec::from_i32(2),
                Some(creature),
                None,
            )?;
        }
        MonsterId::TestSubject => {
            let enrage = asc_val(state, AscensionLevel::DeadlyEnemies, 3, 2)
                + if is_dread(state) { 1 } else { 0 };
            let _ = power::apply(
                state,
                content,
                PowerId::AdaptablePower,
                creature,
                Dec::ONE,
                Some(creature),
                None,
            )?;
            let _ = power::apply(
                state,
                content,
                PowerId::EnragePower,
                creature,
                Dec::from_i32(enrage),
                Some(creature),
                None,
            )?;
        }
        MonsterId::TheForgotten => {
            let _ = power::apply(
                state,
                content,
                PowerId::PossessSpeedPower,
                creature,
                Dec::ONE,
                None,
                None,
            )?;
        }
        MonsterId::TheLost => {
            let _ = power::apply(
                state,
                content,
                PowerId::PossessStrengthPower,
                creature,
                Dec::ONE,
                None,
                None,
            )?;
        }
        MonsterId::ToughEgg => {
            let is_hatched = read_monster_bool(state, creature, "ToughEgg._isHatched", false);
            if !is_hatched {
                let countdown = if state.current_side == CombatSide::Enemy {
                    2
                } else {
                    1
                };
                let _ = power::apply(
                    state,
                    content,
                    PowerId::HatchPower,
                    creature,
                    Dec::from_i32(countdown),
                    Some(creature),
                    None,
                )?;
            } else {
                let min_h = asc_val(state, AscensionLevel::ToughEnemies, 20, 19);
                let max_h = asc_val(state, AscensionLevel::ToughEnemies, 23, 22);
                let hp = state
                    .run_rngs
                    .get_mut(RunRngType::Niche)
                    .next_int_range(min_h, max_h + 1)?;
                creature_cmd::set_max_and_current_hp(state, content, creature, Dec::from_i32(hp))?;
                let after_state = read_monster_string(state, creature, "ToughEgg._afterHatchedState")
                    .map_or(Cow::Borrowed("NIBBLE_MOVE"), Cow::Owned);
                if let Some(m) = state
                    .creature_mut(creature)
                    .and_then(|c| c.monster.as_mut())
                {
                    m.current_state_id = after_state;
                }
            }
        }
        MonsterId::Zapbot => {
            let _ = power::apply(
                state,
                content,
                PowerId::HighVoltagePower,
                creature,
                Dec::from_i32(2),
                Some(creature),
                None,
            )?;
        }
        _ => {}
    }
    Ok(())
}

pub fn monster_roll_move(
    state: &mut BattleState,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
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

    let (mid, can_transition, performed_first, is_revive_state, early_cur_is_move, next_is_none) = {
        let Some(c) = state.creature(creature) else {
            return Ok(());
        };
        let Some(m) = &c.monster else {
            return Ok(());
        };
        let cur_is_move = m.current_state_id != "STUNNED" && is_move_state(m.monster_id, &m.current_state_id);
        (
            m.monster_id,
            m.next_move_can_transition_away,
            m.performed_first_move,
            m.current_state_id == "REVIVE_MOVE",
            cur_is_move,
            m.next_move_id.is_none(),
        )
    };
    let _ = monster_meta(mid)?;

    if !(is_revive_state && can_transition) && (!can_transition || (!performed_first && early_cur_is_move)) {
        if early_cur_is_move && next_is_none {
            if let Some(m) = state
                .creature_mut(creature)
                .and_then(|c| c.monster.as_mut())
            {
                m.next_move_id = Some(m.current_state_id.clone());
            }
        }
        return Ok(());
    }

    if is_revive_state && can_transition {
        let follow_up = state
            .creature(creature)
            .and_then(|c| c.monster.as_ref())
            .and_then(|m| m.transient_move_follow_up_state_id.clone());
        let target_state = follow_up.unwrap_or(Cow::Borrowed(""));
        if !target_state.is_empty() {
            if let Some(m) = state
                .creature_mut(creature)
                .and_then(|c| c.monster.as_mut())
            {
                m.transient_move_follow_up_state_id = None;
                m.current_state_id = target_state.clone();
                if is_move_state(mid, &target_state) {
                    m.next_move_id = Some(target_state.clone());
                    m.state_log_ids.push(target_state);
                    return Ok(());
                } else {
                    m.next_move_id = None;
                }
            }
        }
    }

    let (cur_after_revive, next_move) = {
        let Some(c) = state.creature(creature) else {
            return Ok(());
        };
        let Some(m) = &c.monster else {
            return Ok(());
        };
        (
            InlineStateStr::from_cow(&m.current_state_id),
            m.next_move_id.as_ref().map(InlineStateStr::from_cow),
        )
    };
    let cur_is_move =
        cur_after_revive.as_str() != "STUNNED" && is_move_state(mid, cur_after_revive.as_str());

    if !can_transition || (!performed_first && cur_is_move) {
        if cur_is_move && next_move.is_none() {
            if let Some(m) = state
                .creature_mut(creature)
                .and_then(|c| c.monster.as_mut())
            {
                m.next_move_id = Some(m.current_state_id.clone());
            }
        }
        return Ok(());
    }

    let mut cur = next_move.unwrap_or(cur_after_revive);
    let mut logged: Option<&'static str> = None;

    let next_id: &'static str = loop {
        let next: &'static str = match mid {
            MonsterId::ActOneGuardian => "CRUSH",
            MonsterId::Aeonglass => match cur.as_str() {
                "EBB_MOVE" => "EYE_LASERS_MOVE",
                "EYE_LASERS_MOVE" => "INCREASING_INTENSITY_MOVE",
                _ => "EBB_MOVE",
            },
            MonsterId::Axebot => match cur.as_str() {
                "BOOT_UP_MOVE" => "HAMMER_UPPERCUT_MOVE",
                "HAMMER_UPPERCUT_MOVE" => "ONE_TWO_MOVE",
                _ => "HAMMER_UPPERCUT_MOVE",
            },
            MonsterId::BattleFriendV1 | MonsterId::BattleFriendV2 | MonsterId::BattleFriendV3 => {
                "IDLE"
            }
            MonsterId::ByrdineMonster
            | MonsterId::Byrdpip
            | MonsterId::Osty
            | MonsterId::PaelsLegion
            | MonsterId::Wuppy => "NOTHING_MOVE",
            MonsterId::DevotedSculptor => "SAVAGE_MOVE",
            MonsterId::EyeWithTeeth => "DISTRACT_MOVE",
            MonsterId::Fabricator => match cur.as_str() {
                "FABRICATE_MOVE" | "FABRICATING_STRIKE_MOVE" | "DISINTEGRATE_MOVE" => {
                    "fabricateBranch"
                }
                "fabricateBranch" => {
                    let living_enemies = state
                        .enemy_handles
                        .iter()
                        .filter(|&&h| state.creature(h).is_some_and(|c| c.is_alive()))
                        .count();
                    if living_enemies < 4 {
                        "RAND"
                    } else {
                        "DISINTEGRATE_MOVE"
                    }
                }
                "RAND" => select_random_branch(
                    state,
                    creature,
                    mid,
                    &[
                        RandBranch {
                            state_id: "FABRICATE_MOVE",
                            repeat: RepeatRule::CanRepeatForever,
                            cooldown: 0,
                            weight: 1.0,
                        },
                        RandBranch {
                            state_id: "FABRICATING_STRIKE_MOVE",
                            repeat: RepeatRule::CanRepeatForever,
                            cooldown: 0,
                            weight: 1.0,
                        },
                    ],
                    "RAND",
                )?,
                _ => "fabricateBranch",
            },
            MonsterId::FakeMerchantMonster => match cur.as_str() {
                "SWIPE_MOVE" | "SPEW_COINS_MOVE" | "ENRAGE_MOVE" => "RAND_MOVE",
                "THROW_RELIC_MOVE" => "RAND_ATTACK_MOVE",
                "RAND_MOVE" => select_random_branch(
                    state,
                    creature,
                    mid,
                    &[
                        RandBranch {
                            state_id: "SWIPE_MOVE",
                            repeat: RepeatRule::CannotRepeat,
                            cooldown: 0,
                            weight: 1.0,
                        },
                        RandBranch {
                            state_id: "SPEW_COINS_MOVE",
                            repeat: RepeatRule::CannotRepeat,
                            cooldown: 0,
                            weight: 1.0,
                        },
                        RandBranch {
                            state_id: "THROW_RELIC_MOVE",
                            repeat: RepeatRule::CannotRepeat,
                            cooldown: 0,
                            weight: 1.0,
                        },
                        RandBranch {
                            state_id: "ENRAGE_MOVE",
                            repeat: RepeatRule::CannotRepeat,
                            cooldown: 3,
                            weight: 1.0,
                        },
                    ],
                    "RAND_MOVE",
                )?,
                "RAND_ATTACK_MOVE" => select_random_branch(
                    state,
                    creature,
                    mid,
                    &[
                        RandBranch {
                            state_id: "SWIPE_MOVE",
                            repeat: RepeatRule::CannotRepeat,
                            cooldown: 0,
                            weight: 1.0,
                        },
                        RandBranch {
                            state_id: "SPEW_COINS_MOVE",
                            repeat: RepeatRule::CannotRepeat,
                            cooldown: 0,
                            weight: 1.0,
                        },
                        RandBranch {
                            state_id: "THROW_RELIC_MOVE",
                            repeat: RepeatRule::CannotRepeat,
                            cooldown: 0,
                            weight: 1.0,
                        },
                    ],
                    "RAND_ATTACK_MOVE",
                )?,
                _ => "RAND_MOVE",
            },
            MonsterId::FatGremlin => "FLEE_MOVE",
            MonsterId::FlailKnight | MonsterId::MysteriousKnight => match cur.as_str() {
                "WAR_CHANT" | "FLAIL_MOVE" | "RAM_MOVE" => "RAND",
                "RAND" => select_random_branch(
                    state,
                    creature,
                    mid,
                    &[
                        RandBranch {
                            state_id: "WAR_CHANT",
                            repeat: RepeatRule::CannotRepeat,
                            cooldown: 0,
                            weight: 1.0,
                        },
                        RandBranch {
                            state_id: "FLAIL_MOVE",
                            repeat: RepeatRule::CanRepeatXTimes(2),
                            cooldown: 0,
                            weight: 1.0,
                        },
                        RandBranch {
                            state_id: "RAM_MOVE",
                            repeat: RepeatRule::CanRepeatXTimes(2),
                            cooldown: 0,
                            weight: 1.0,
                        },
                    ],
                    "RAND",
                )?,
                _ => "RAND",
            },
            MonsterId::FrogKnight => match cur.as_str() {
                "TONGUE_LASH" => "STRIKE_DOWN_EVIL",
                "STRIKE_DOWN_EVIL" => "FOR_THE_QUEEN",
                "FOR_THE_QUEEN" => "HALF_HEALTH",
                "HALF_HEALTH" => {
                    let charged = read_monster_bool(state, creature, "FrogKnight._charged", false);
                    let (hp, max_hp) = state
                        .creature(creature)
                        .map_or((0, 0), |c| (c.current_hp, c.max_hp));
                    if charged || hp >= max_hp / 2 {
                        "TONGUE_LASH"
                    } else {
                        "BEETLE_CHARGE"
                    }
                }
                _ => "TONGUE_LASH",
            },
            MonsterId::GasBomb => "EXPLODE_MOVE",
            MonsterId::GlobeHead => match cur.as_str() {
                "SHOCKING_SLAP" => "THUNDER_STRIKE",
                "THUNDER_STRIKE" => "GALVANIC_BURST",
                _ => "SHOCKING_SLAP",
            },
            MonsterId::Guardbot => "GUARD_MOVE",
            MonsterId::HardyBrute => "SLAM",
            MonsterId::LivingShield => match cur.as_str() {
                "SHIELD_SLAM_MOVE" => "SHIELD_SLAM_BRANCH",
                "SHIELD_SLAM_BRANCH" => {
                    let has_living_ally = state
                        .enemy_handles
                        .iter()
                        .any(|&h| h != creature && state.creature(h).is_some_and(|c| c.is_alive()));
                    if has_living_ally {
                        "SHIELD_SLAM_MOVE"
                    } else {
                        "SMASH_MOVE"
                    }
                }
                _ => "SMASH_MOVE",
            },
            MonsterId::MagiKnight => match cur.as_str() {
                "POWER_SHIELD_MOVE" => "DAMPEN_MOVE",
                "DAMPEN_MOVE" => "RAM_MOVE",
                "RAM_MOVE" => "PREP_MOVE",
                "PREP_MOVE" => "MAGIC_BOMB",
                _ => "RAM_MOVE",
            },
            MonsterId::MechaKnight => match cur.as_str() {
                "CHARGE_MOVE" => "FLAMETHROWER_MOVE",
                "FLAMETHROWER_MOVE" => "WINDUP_MOVE",
                "WINDUP_MOVE" => "HEAVY_CLEAVE_MOVE",
                _ => "FLAMETHROWER_MOVE",
            },
            MonsterId::Noisebot => "NOISE_MOVE",
            MonsterId::OwlMagistrate => match cur.as_str() {
                "MAGISTRATE_SCRUTINY" => "PECK_ASSAULT",
                "PECK_ASSAULT" => "JUDICIAL_FLIGHT",
                "JUDICIAL_FLIGHT" => "VERDICT",
                _ => "MAGISTRATE_SCRUTINY",
            },
            MonsterId::Parafright => "SLAM_MOVE",
            MonsterId::Queen => {
                let amalgam_died =
                    read_monster_bool(state, creature, "Queen._hasAmalgamDied", false);
                match cur.as_str() {
                    "PUPPET_STRINGS_MOVE" => "YOU_ARE_MINE_MOVE",
                    "YOU_ARE_MINE_MOVE" | "DREAD_INITIAL_MOVE" => "YOURE_MINE_NOW_BRANCH",
                    "YOURE_MINE_NOW_BRANCH" | "BURN_BRIGHT_FOR_ME_BRANCH" => {
                        if !amalgam_died {
                            "BURN_BRIGHT_FOR_ME_MOVE"
                        } else {
                            "OFF_WITH_YOUR_HEAD_MOVE"
                        }
                    }
                    "BURN_BRIGHT_FOR_ME_MOVE" => "BURN_BRIGHT_FOR_ME_BRANCH",
                    "OFF_WITH_YOUR_HEAD_MOVE" => "EXECUTION_MOVE",
                    "EXECUTION_MOVE" => "ENRAGE_MOVE",
                    _ => "OFF_WITH_YOUR_HEAD_MOVE",
                }
            }
            MonsterId::ScrollOfBiting => match cur.as_str() {
                "CHOMP" => "MORE_TEETH",
                "MORE_TEETH" => "CHEW",
                "CHEW" => "rand",
                "rand" => select_random_branch(
                    state,
                    creature,
                    mid,
                    &[
                        RandBranch {
                            state_id: "CHOMP",
                            repeat: RepeatRule::CannotRepeat,
                            cooldown: 0,
                            weight: 1.0,
                        },
                        RandBranch {
                            state_id: "CHEW",
                            repeat: RepeatRule::CanRepeatXTimes(2),
                            cooldown: 0,
                            weight: 1.0,
                        },
                    ],
                    "rand",
                )?,
                _ => "CHOMP",
            },
            MonsterId::SlimedBerserker => match cur.as_str() {
                "VOMIT_ICHOR_MOVE" => "FURIOUS_PUMMELING_MOVE",
                "FURIOUS_PUMMELING_MOVE" => "LEECHING_HUG_MOVE",
                "LEECHING_HUG_MOVE" => "SMOTHER_MOVE",
                _ => "VOMIT_ICHOR_MOVE",
            },
            MonsterId::SneakyGremlin => "TACKLE_MOVE",
            MonsterId::SoulNexus => match cur.as_str() {
                "SOUL_BURN_MOVE" | "MAELSTROM_MOVE" | "DRAIN_LIFE_MOVE" => "RAND",
                "RAND" => select_random_branch(
                    state,
                    creature,
                    mid,
                    &[
                        RandBranch {
                            state_id: "SOUL_BURN_MOVE",
                            repeat: RepeatRule::CannotRepeat,
                            cooldown: 0,
                            weight: 1.0,
                        },
                        RandBranch {
                            state_id: "MAELSTROM_MOVE",
                            repeat: RepeatRule::CannotRepeat,
                            cooldown: 0,
                            weight: 1.0,
                        },
                        RandBranch {
                            state_id: "DRAIN_LIFE_MOVE",
                            repeat: RepeatRule::CannotRepeat,
                            cooldown: 0,
                            weight: 1.0,
                        },
                    ],
                    "RAND",
                )?,
                _ => "RAND",
            },
            MonsterId::SpectralKnight => match cur.as_str() {
                "HEX" => "SOUL_SLASH",
                "SOUL_SLASH" | "SOUL_FLAME" => "RAND",
                "RAND" => select_random_branch(
                    state,
                    creature,
                    mid,
                    &[
                        RandBranch {
                            state_id: "SOUL_SLASH",
                            repeat: RepeatRule::CanRepeatXTimes(2),
                            cooldown: 0,
                            weight: 1.0,
                        },
                        RandBranch {
                            state_id: "SOUL_FLAME",
                            repeat: RepeatRule::CannotRepeat,
                            cooldown: 0,
                            weight: 1.0,
                        },
                    ],
                    "RAND",
                )?,
                _ => "RAND",
            },
            MonsterId::Stabbot => "STAB_MOVE",
            MonsterId::TestSubject => match cur.as_str() {
                "BITE_MOVE" => "SKULL_BASH_MOVE",
                "SKULL_BASH_MOVE" => "BITE_MOVE",
                "RESPAWN_MOVE" => "REVIVE_BRANCH",
                "REVIVE_BRANCH" => {
                    let respawns = read_monster_int(state, creature, "TestSubject._respawns", 0);
                    if respawns < 2 {
                        "MULTI_CLAW_MOVE"
                    } else {
                        "PHASE3_LACERATE_MOVE"
                    }
                }
                "MULTI_CLAW_MOVE" => "MULTI_CLAW_MOVE",
                "PHASE3_LACERATE_MOVE" => "BIG_POUNCE",
                "BIG_POUNCE" => "BURNING_GROWL_MOVE",
                "BURNING_GROWL_MOVE" => "PHASE3_LACERATE_MOVE",
                _ => "BITE_MOVE",
            },
            MonsterId::TheForgotten => match cur.as_str() {
                "MIASMA" => "DREAD",
                _ => "MIASMA",
            },
            MonsterId::TheLost => match cur.as_str() {
                "DEBILITATING_SMOG" => "EYE_LASERS",
                _ => "DEBILITATING_SMOG",
            },
            MonsterId::TorchHeadAmalgam => match cur.as_str() {
                "STRONG_TACKLE_MOVE" => "TACKLE_2_MOVE",
                "TACKLE_2_MOVE" => "BEAM_MOVE",
                "BEAM_MOVE" => "TACKLE_3_MOVE",
                "TACKLE_3_MOVE" => "TACKLE_4_MOVE",
                _ => "BEAM_MOVE",
            },
            MonsterId::ToughEgg => "NIBBLE_MOVE",
            MonsterId::TrainingDummy => "POKE",
            MonsterId::TurretOperator => match cur.as_str() {
                "UNLOAD_MOVE" => "UNLOAD_MOVE_2",
                "UNLOAD_MOVE_2" => "RELOAD_MOVE",
                _ => "UNLOAD_MOVE",
            },
            MonsterId::WanderingGrunt => "BITE",
            MonsterId::Wriggler => match cur.as_str() {
                "SPAWNED_MOVE" => "INIT_MOVE",
                "INIT_MOVE" => {
                    let slot = state
                        .creature(creature)
                        .and_then(|c| c.slot_name.as_deref());
                    match slot {
                        Some("wriggler1" | "wriggler3") => "NASTY_BITE_MOVE",
                        Some("wriggler2" | "wriggler4") => "WRIGGLE_MOVE",
                        _ => {
                            return Err(KernelError::InvalidState(
                                "No branch predicate matched for ConditionalBranchState 'INIT_MOVE'."
                                    .to_owned(),
                            ));
                        }
                    }
                }
                "NASTY_BITE_MOVE" => "WRIGGLE_MOVE",
                _ => "NASTY_BITE_MOVE",
            },
            MonsterId::Zapbot => "ZAP",
            _ => {
                return Err(KernelError::Unsupported {
                    kind: "Monster",
                    id: mid.model_id_str().to_owned(),
                });
            }
        };

        let is_move = is_move_state(mid, next);
        if logged.is_none() && is_move {
            logged = Some(next);
        }
        if is_move {
            break next;
        }
        cur = InlineStateStr::Borrowed(next);
    };

    if let Some(c_mut) = state.creature_mut(creature) {
        if let Some(m_mut) = &mut c_mut.monster {
            m_mut.current_state_id = Cow::Borrowed(next_id);
            m_mut.next_move_id = Some(Cow::Borrowed(next_id));
            if let Some(log_id) = logged {
                m_mut.state_log_ids.push(Cow::Borrowed(log_id));
            }
        }
    }
    Ok(())
}

fn generate_status_to_player(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    card_id: CardId,
    pile: PileType,
    pos: CardPilePosition,
    count: i32,
) -> Result<(), KernelError> {
    for _ in 0..count {
        let _ = card_pile::add_generated_card_to_combat(
            state,
            content,
            card_id,
            pile,
            pos,
            0,
            None,
        )?;
    }
    Ok(())
}

fn fabricator_next_slot(state: &BattleState) -> Option<&'static str> {
    const SLOTS: [&str; 4] = ["bot1", "bot2", "bot3", "bot4"];
    SLOTS.into_iter().find(|&slot| {
        state.enemy_handles.iter().all(|&h| {
            state
                .creature(h)
                .is_none_or(|c| c.is_dead() || c.slot_name.as_deref() != Some(slot))
        })
    })
}

fn parse_fabricator_last_spawned(raw: Option<&str>) -> Option<MonsterId> {
    match raw {
        Some("GUARDBOT" | "MONSTER.GUARDBOT") => Some(MonsterId::Guardbot),
        Some("NOISEBOT" | "MONSTER.NOISEBOT") => Some(MonsterId::Noisebot),
        Some("ZAPBOT" | "MONSTER.ZAPBOT") => Some(MonsterId::Zapbot),
        Some("STABBOT" | "MONSTER.STABBOT") => Some(MonsterId::Stabbot),
        _ => None,
    }
}

fn fabricator_spawn(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    fabricator: CreatureHandle,
    choices: &[MonsterId],
) -> Result<(), KernelError> {
    if !state.is_live_combat() {
        return Ok(());
    }
    let living_teammates = state
        .enemy_handles
        .iter()
        .filter(|&&h| h != fabricator && state.creature(h).is_some_and(|c| c.is_alive()))
        .count();
    let Some(slot_name) = fabricator_next_slot(state) else {
        return Ok(());
    };
    if living_teammates >= 4 {
        return Ok(());
    }
    let last_raw = read_monster_string(state, fabricator, "Fabricator._lastSpawned");
    let last_id = parse_fabricator_last_spawned(last_raw.as_deref());
    let available: SmallVec<[MonsterId; 2]> = choices
        .iter()
        .copied()
        .filter(|&id| Some(id) != last_id)
        .collect();
    let choice = if !available.is_empty() {
        let idx = state
            .run_rngs
            .get_mut(RunRngType::MonsterAi)
            .next_int(available.len() as i32)? as usize;
        available[idx]
    } else {
        choices[0]
    };
    set_monster_field(
        state,
        fabricator,
        "Fabricator._lastSpawned",
        FieldValue::String(choice.model_entry().to_owned()),
    );
    let spawned = spawn_enemy_with_unique_hp(state, content, choice, Some(Cow::Borrowed(slot_name)))?;
    let _ = power::apply(
        state,
        content,
        PowerId::MinionPower,
        spawned,
        Dec::ONE,
        Some(fabricator),
        None,
    )?;
    Ok(())
}

fn apply_wither_fake_upgrade(state: &mut BattleState, card: CardHandle, amount: i32) {
    if amount <= 0 {
        return;
    }
    if let Some(ext) = state.card_ext_mut(card) {
        let cur = match ext.private_fields.get("Wither._fakeUpgradeLevel") {
            Some(FieldValue::Int(v)) => *v,
            _ => 0,
        };
        ext.private_fields
            .insert(Cow::Borrowed("Wither._fakeUpgradeLevel"), FieldValue::Int(cur + amount));
    }
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

    let pc = state.player.creature_handle;
    let is_deadly = asc_active(state, AscensionLevel::DeadlyEnemies);
    let is_tough = asc_active(state, AscensionLevel::ToughEnemies);
    let deadly = |high: i32, low: i32| if is_deadly { high } else { low };
    let tough = |high: i32, low: i32| if is_tough { high } else { low };

    match (mid, move_id.as_ref()) {
        (_, "STUNNED") => {}
        (MonsterId::ActOneGuardian, "CRUSH") => {
            AttackBuilder::from_monster(creature, Dec::from_i32(20)).execute(state, content)?;
        }
        (MonsterId::Aeonglass, "EBB_MOVE") => {
            let dmg = Dec::from_i32(deadly(26, 22));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            block::gain_block(
                state,
                content,
                creature,
                Dec::from_i32(33),
                ValueProp::MOVE,
                None,
                None,
            )?;
        }
        (MonsterId::Aeonglass, "EYE_LASERS_MOVE") => {
            let dmg = Dec::from_i32(deadly(12, 11));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
        }
        (MonsterId::Aeonglass, "INCREASING_INTENSITY_MOVE") => {
            let wither_handles: Vec<CardHandle> = state
                .player
                .hand
                .iter()
                .chain(state.player.draw.iter())
                .chain(state.player.discard.iter())
                .chain(state.player.exhaust.iter())
                .chain(state.player.play.iter())
                .copied()
                .filter(|&h| state.card(h).is_some_and(|c| c.id == CardId::Wither))
                .collect();
            for ch in wither_handles {
                apply_wither_fake_upgrade(state, ch, 1);
            }
            let upgrade_count =
                read_monster_int(state, creature, "Aeonglass._witherUpgradeCount", 0) + 1;
            set_monster_field(
                state,
                creature,
                "Aeonglass._witherUpgradeCount",
                FieldValue::Int(upgrade_count),
            );
            let dread = is_dread(state);
            let wither_count = deadly(2, 1) + if dread { 1 } else { 0 };
            let target_pile = if dread {
                PileType::Draw
            } else {
                PileType::Discard
            };
            generate_status_to_player(
                content,
                state,
                CardId::Wither,
                target_pile,
                CardPilePosition::Random,
                wither_count,
            )?;
            let add_str = read_monster_int(state, creature, "Aeonglass._additionalStrength", 0);
            let base_str = deadly(4, 3);
            let _ = power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                Dec::from_i32(base_str + add_str),
                Some(creature),
                None,
            )?;
            set_monster_field(
                state,
                creature,
                "Aeonglass._additionalStrength",
                FieldValue::Int(add_str + 1),
            );
        }
        (MonsterId::Axebot, "BOOT_UP_MOVE") => {
            let boot_block = Dec::from_i32(deadly(15, 10));
            let boot_str = deadly(4, 3);
            let stock = read_monster_nullable_int(state, creature, "Axebot._stockAmount").unwrap_or(2);
            block::gain_block(state, content, creature, boot_block, ValueProp::MOVE, None, None)?;
            let _ = power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                Dec::from_i32(boot_str * (2 - stock)),
                Some(creature),
                None,
            )?;
        }
        (MonsterId::Axebot, "ONE_TWO_MOVE") => {
            let dmg = Dec::from_i32(deadly(11, 10));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
        }
        (MonsterId::Axebot, "HAMMER_UPPERCUT_MOVE") => {
            let dmg = Dec::from_i32(deadly(18, 14));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            if state.creature(pc).is_some_and(|c| c.is_alive()) {
                let _ = power::apply(
                    state,
                    content,
                    PowerId::WeakPower,
                    pc,
                    Dec::from_i32(2),
                    Some(creature),
                    None,
                )?;
                let _ = power::apply(
                    state,
                    content,
                    PowerId::FrailPower,
                    pc,
                    Dec::from_i32(2),
                    Some(creature),
                    None,
                )?;
            }
        }
        (MonsterId::BattleFriendV1 | MonsterId::BattleFriendV2 | MonsterId::BattleFriendV3, "IDLE") => {}
        (
            MonsterId::ByrdineMonster
            | MonsterId::Byrdpip
            | MonsterId::Osty
            | MonsterId::PaelsLegion
            | MonsterId::Wuppy,
            "NOTHING_MOVE",
        ) => {}
        (MonsterId::DevotedSculptor, "FORBIDDEN_INCANTATION_MOVE") => {
            let _ = power::apply(
                state,
                content,
                PowerId::RitualPower,
                creature,
                Dec::from_i32(9),
                None,
                None,
            )?;
        }
        (MonsterId::DevotedSculptor, "SAVAGE_MOVE") => {
            let dmg = Dec::from_i32(deadly(15, 12));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::EyeWithTeeth, "DISTRACT_MOVE") => {
            generate_status_to_player(
                content,
                state,
                CardId::Dazed,
                PileType::Discard,
                CardPilePosition::Bottom,
                3,
            )?;
        }
        (MonsterId::EyeWithTeeth | MonsterId::Parafright, "REVIVE_MOVE") => {
            let illusion_ext = state.creature(creature).and_then(|c| {
                c.powers
                    .iter()
                    .find(|p| p.id == PowerId::IllusionPower)
                    .and_then(|p| p.ext_index)
            });
            if let Some(ext_idx) = illusion_ext {
                state.power_extensions[ext_idx as usize]
                    .private_fields
                    .insert(Cow::Borrowed("IllusionPower._isReviving"), FieldValue::Bool(false));
            }
            let heal_amt = state
                .creature(creature)
                .map_or(0, |c| (c.max_hp - c.current_hp).max(0));
            creature_cmd::heal(state, content, creature, Dec::from_i32(heal_amt))?;
            if let Some(m) = state
                .creature_mut(creature)
                .and_then(|c| c.monster.as_mut())
            {
                m.next_move_can_transition_away = true;
            }
        }
        (MonsterId::Fabricator, "FABRICATE_MOVE") => {
            fabricator_spawn(content, state, creature, &[MonsterId::Guardbot, MonsterId::Noisebot])?;
            fabricator_spawn(content, state, creature, &[MonsterId::Zapbot, MonsterId::Stabbot])?;
        }
        (MonsterId::Fabricator, "FABRICATING_STRIKE_MOVE") => {
            let dmg = Dec::from_i32(deadly(21, 18));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            fabricator_spawn(content, state, creature, &[MonsterId::Zapbot, MonsterId::Stabbot])?;
        }
        (MonsterId::Fabricator, "DISINTEGRATE_MOVE") => {
            let dmg = Dec::from_i32(deadly(13, 11));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::FakeMerchantMonster, "SWIPE_MOVE") => {
            let dmg = Dec::from_i32(deadly(15, 13));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::FakeMerchantMonster, "SPEW_COINS_MOVE") => {
            AttackBuilder::from_monster(creature, Dec::from_i32(2))
                .with_hit_count(8)
                .execute(state, content)?;
        }
        (MonsterId::FakeMerchantMonster, "THROW_RELIC_MOVE") => {
            let dmg = Dec::from_i32(deadly(10, 9));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            if state.creature(pc).is_some_and(|c| c.is_alive()) {
                let _ = power::apply(
                    state,
                    content,
                    PowerId::FrailPower,
                    pc,
                    Dec::ONE,
                    Some(creature),
                    None,
                )?;
            }
        }
        (MonsterId::FakeMerchantMonster, "ENRAGE_MOVE") => {
            let _ = power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                Dec::from_i32(2),
                Some(creature),
                None,
            )?;
        }
        (MonsterId::FatGremlin | MonsterId::SneakyGremlin | MonsterId::Wriggler, "SPAWNED_MOVE") => {}
        (MonsterId::FatGremlin, "FLEE_MOVE") => {
            escape_creature(content, state, creature)?;
        }
        (MonsterId::FlailKnight | MonsterId::MysteriousKnight, "WAR_CHANT") => {
            let _ = power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                Dec::from_i32(3),
                Some(creature),
                None,
            )?;
        }
        (MonsterId::FlailKnight | MonsterId::MysteriousKnight, "FLAIL_MOVE") => {
            let dmg = Dec::from_i32(deadly(10, 9));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
        }
        (MonsterId::FlailKnight | MonsterId::MysteriousKnight, "RAM_MOVE") => {
            let dmg = Dec::from_i32(deadly(17, 15));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::FrogKnight, "FOR_THE_QUEEN") => {
            let _ = power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                Dec::from_i32(5),
                Some(creature),
                None,
            )?;
        }
        (MonsterId::FrogKnight, "STRIKE_DOWN_EVIL") => {
            let dmg = Dec::from_i32(deadly(23, 21));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::FrogKnight, "TONGUE_LASH") => {
            let dmg = Dec::from_i32(deadly(14, 13));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            if state.creature(pc).is_some_and(|c| c.is_alive()) {
                let _ = power::apply(
                    state,
                    content,
                    PowerId::FrailPower,
                    pc,
                    Dec::from_i32(2),
                    Some(creature),
                    None,
                )?;
            }
        }
        (MonsterId::FrogKnight, "BEETLE_CHARGE") => {
            set_monster_field(state, creature, "FrogKnight._charged", FieldValue::Bool(true));
            let dmg = Dec::from_i32(deadly(40, 35));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::GasBomb, "EXPLODE_MOVE") => {
            set_monster_field(state, creature, "GasBomb._hasExploded", FieldValue::Bool(true));
            let dmg = Dec::from_i32(deadly(9, 8));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            if state.creature(creature).is_some_and(|c| c.is_alive()) {
                creature_cmd::kill(state, content, creature, None)?;
            }
        }
        (MonsterId::GlobeHead, "THUNDER_STRIKE") => {
            let dmg = Dec::from_i32(deadly(7, 6));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(3)
                .execute(state, content)?;
        }
        (MonsterId::GlobeHead, "SHOCKING_SLAP") => {
            let dmg = Dec::from_i32(deadly(14, 13));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            if state.creature(pc).is_some_and(|c| c.is_alive()) {
                let _ = power::apply(
                    state,
                    content,
                    PowerId::FrailPower,
                    pc,
                    Dec::from_i32(2),
                    Some(creature),
                    None,
                )?;
            }
        }
        (MonsterId::GlobeHead, "GALVANIC_BURST") => {
            let dmg = Dec::from_i32(deadly(17, 16));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            let _ = power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                Dec::from_i32(2),
                Some(creature),
                None,
            )?;
        }
        (MonsterId::Guardbot, "GUARD_MOVE") => {
            let fabricators: SmallVec<[CreatureHandle; 4]> = state
                .enemy_handles
                .iter()
                .copied()
                .filter(|&h| {
                    state
                        .creature(h)
                        .is_some_and(|c| c.monster_id == Some(MonsterId::Fabricator) && c.is_alive())
                })
                .collect();
            for ally in fabricators {
                block::gain_block(
                    state,
                    content,
                    ally,
                    Dec::from_i32(15),
                    ValueProp::UNPOWERED,
                    None,
                    None,
                )?;
            }
        }
        (MonsterId::HardyBrute, "SLAM") => {
            AttackBuilder::from_monster(creature, Dec::from_i32(14)).execute(state, content)?;
        }
        (MonsterId::LivingShield, "SHIELD_SLAM_MOVE") => {
            AttackBuilder::from_monster(creature, Dec::from_i32(6)).execute(state, content)?;
        }
        (MonsterId::LivingShield, "SMASH_MOVE") => {
            let dmg = Dec::from_i32(deadly(18, 16));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            let _ = power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                Dec::from_i32(3),
                Some(creature),
                None,
            )?;
        }
        (MonsterId::MagiKnight, "POWER_SHIELD_MOVE") => {
            let dmg = Dec::from_i32(deadly(7, 6));
            let blk = Dec::from_i32(tough(9, 5));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            block::gain_block(state, content, creature, blk, ValueProp::MOVE, None, None)?;
        }
        (MonsterId::MagiKnight, "DAMPEN_MOVE") => {
            if state.creature(pc).is_some_and(|c| c.is_alive()) {
                let existing = state
                    .creature(pc)
                    .and_then(|c| c.powers.iter().find(|p| p.id == PowerId::DampenPower))
                    .map(|p| p.handle);
                let dampen_handle = if let Some(h) = existing {
                    Some(h)
                } else {
                    let idx = power::apply(
                        state,
                        content,
                        PowerId::DampenPower,
                        pc,
                        Dec::ONE,
                        Some(creature),
                        None,
                    )?;
                    idx.and_then(|i| {
                        state
                            .creature(pc)
                            .and_then(|c| c.powers.get(i).map(|p| p.handle))
                    })
                };
                if let Some(ph) = dampen_handle {
                    crate::powers::batch_a_m::dampen_add_caster(state, pc, ph, creature);
                }
            }
        }
        (MonsterId::MagiKnight, "RAM_MOVE") => {
            let dmg = Dec::from_i32(deadly(11, 10));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::MagiKnight, "PREP_MOVE") => {
            let blk = Dec::from_i32(tough(9, 5));
            block::gain_block(state, content, creature, blk, ValueProp::MOVE, None, None)?;
        }
        (MonsterId::MagiKnight, "MAGIC_BOMB") => {
            let dmg = Dec::from_i32(deadly(40, 35));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::MechaKnight, "CHARGE_MOVE") => {
            let dmg = Dec::from_i32(deadly(30, 25));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::MechaKnight, "FLAMETHROWER_MOVE") => {
            let dmg = Dec::from_i32(deadly(12, 8));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            generate_status_to_player(
                content,
                state,
                CardId::Burn,
                PileType::Hand,
                CardPilePosition::Bottom,
                4,
            )?;
        }
        (MonsterId::MechaKnight, "WINDUP_MOVE") => {
            block::gain_block(
                state,
                content,
                creature,
                Dec::from_i32(15),
                ValueProp::MOVE,
                None,
                None,
            )?;
            let _ = power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                Dec::from_i32(5),
                Some(creature),
                None,
            )?;
        }
        (MonsterId::MechaKnight, "HEAVY_CLEAVE_MOVE") => {
            let dmg = Dec::from_i32(deadly(40, 35));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Noisebot, "NOISE_MOVE") => {
            generate_status_to_player(
                content,
                state,
                CardId::Dazed,
                PileType::Discard,
                CardPilePosition::Bottom,
                1,
            )?;
            generate_status_to_player(
                content,
                state,
                CardId::Dazed,
                PileType::Draw,
                CardPilePosition::Random,
                1,
            )?;
        }
        (MonsterId::OwlMagistrate, "MAGISTRATE_SCRUTINY") => {
            let dmg = Dec::from_i32(deadly(17, 16));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::OwlMagistrate, "PECK_ASSAULT") => {
            AttackBuilder::from_monster(creature, Dec::from_i32(4))
                .with_hit_count(6)
                .execute(state, content)?;
        }
        (MonsterId::OwlMagistrate, "JUDICIAL_FLIGHT") => {
            let _ = power::apply(
                state,
                content,
                PowerId::SoarPower,
                creature,
                Dec::ONE,
                Some(creature),
                None,
            )?;
        }
        (MonsterId::OwlMagistrate, "VERDICT") => {
            let dmg = Dec::from_i32(deadly(36, 33));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            if state.creature(pc).is_some_and(|c| c.is_alive()) {
                let _ = power::apply(
                    state,
                    content,
                    PowerId::VulnerablePower,
                    pc,
                    Dec::from_i32(4),
                    Some(creature),
                    None,
                )?;
            }
            let _ = power::remove_by_id(state, content, creature, PowerId::SoarPower)?;
        }
        (MonsterId::Parafright, "SLAM_MOVE") => {
            let dmg = Dec::from_i32(deadly(17, 16));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Queen, "PUPPET_STRINGS_MOVE") => {
            if state.creature(pc).is_some_and(|c| c.is_alive()) {
                let _ = power::apply(
                    state,
                    content,
                    PowerId::ChainsOfBindingPower,
                    pc,
                    Dec::from_i32(3),
                    Some(creature),
                    None,
                )?;
            }
        }
        (MonsterId::Queen, "YOU_ARE_MINE_MOVE") => {
            if state.creature(pc).is_some_and(|c| c.is_alive()) {
                let _ = power::apply(
                    state,
                    content,
                    PowerId::FrailPower,
                    pc,
                    Dec::from_i32(99),
                    Some(creature),
                    None,
                )?;
                let _ = power::apply(
                    state,
                    content,
                    PowerId::WeakPower,
                    pc,
                    Dec::from_i32(99),
                    Some(creature),
                    None,
                )?;
                let _ = power::apply(
                    state,
                    content,
                    PowerId::VulnerablePower,
                    pc,
                    Dec::from_i32(99),
                    Some(creature),
                    None,
                )?;
            }
        }
        (MonsterId::Queen, "DREAD_INITIAL_MOVE") => {
            if state.creature(pc).is_some_and(|c| c.is_alive()) {
                let _ = power::apply(
                    state,
                    content,
                    PowerId::ChainsOfBindingPower,
                    pc,
                    Dec::from_i32(3),
                    Some(creature),
                    None,
                )?;
                let _ = power::apply(
                    state,
                    content,
                    PowerId::FrailPower,
                    pc,
                    Dec::from_i32(99),
                    Some(creature),
                    None,
                )?;
                let _ = power::apply(
                    state,
                    content,
                    PowerId::WeakPower,
                    pc,
                    Dec::from_i32(99),
                    Some(creature),
                    None,
                )?;
                let _ = power::apply(
                    state,
                    content,
                    PowerId::VulnerablePower,
                    pc,
                    Dec::from_i32(99),
                    Some(creature),
                    None,
                )?;
            }
        }
        (MonsterId::Queen, "BURN_BRIGHT_FOR_ME_MOVE") => {
            let str_amt = Dec::from_i32(if is_dread(state) { 2 } else { 1 });
            let teammates: SmallVec<[CreatureHandle; 8]> = state
                .enemy_handles
                .iter()
                .copied()
                .filter(|&h| h != creature)
                .collect();
            for ally in teammates {
                let _ = power::apply(
                    state,
                    content,
                    PowerId::StrengthPower,
                    ally,
                    str_amt,
                    Some(creature),
                    None,
                )?;
            }
            block::gain_block(
                state,
                content,
                creature,
                Dec::from_i32(20),
                ValueProp::MOVE,
                None,
                None,
            )?;
        }
        (MonsterId::Queen, "OFF_WITH_YOUR_HEAD_MOVE") => {
            let dmg = Dec::from_i32(deadly(4, 3));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(5)
                .execute(state, content)?;
        }
        (MonsterId::Queen, "EXECUTION_MOVE") => {
            let dmg = Dec::from_i32(deadly(18, 15));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Queen, "ENRAGE_MOVE") => {
            let _ = power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                Dec::from_i32(2),
                Some(creature),
                None,
            )?;
        }
        (MonsterId::ScrollOfBiting, "CHOMP") => {
            let dmg = Dec::from_i32(deadly(16, 14));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::ScrollOfBiting, "CHEW") => {
            let dmg = Dec::from_i32(deadly(6, 5));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
        }
        (MonsterId::ScrollOfBiting, "MORE_TEETH") => {
            let _ = power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                Dec::from_i32(2),
                Some(creature),
                None,
            )?;
        }
        (MonsterId::SlimedBerserker, "VOMIT_ICHOR_MOVE") => {
            generate_status_to_player(
                content,
                state,
                CardId::Slimed,
                PileType::Discard,
                CardPilePosition::Bottom,
                10,
            )?;
        }
        (MonsterId::SlimedBerserker, "FURIOUS_PUMMELING_MOVE") => {
            let dmg = Dec::from_i32(deadly(5, 4));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(4)
                .execute(state, content)?;
        }
        (MonsterId::SlimedBerserker, "LEECHING_HUG_MOVE") => {
            if state.creature(pc).is_some_and(|c| c.is_alive()) {
                let _ = power::apply(
                    state,
                    content,
                    PowerId::WeakPower,
                    pc,
                    Dec::from_i32(3),
                    None,
                    None,
                )?;
            }
            let _ = power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                Dec::from_i32(3),
                Some(creature),
                None,
            )?;
        }
        (MonsterId::SlimedBerserker, "SMOTHER_MOVE") => {
            let dmg = Dec::from_i32(deadly(33, 30));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::SneakyGremlin, "TACKLE_MOVE") => {
            let dmg = Dec::from_i32(deadly(10, 9));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::SoulNexus, "SOUL_BURN_MOVE") => {
            let dmg = Dec::from_i32(deadly(31, 29));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::SoulNexus, "MAELSTROM_MOVE") => {
            let dmg = Dec::from_i32(deadly(7, 6));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(4)
                .execute(state, content)?;
        }
        (MonsterId::SoulNexus, "DRAIN_LIFE_MOVE") => {
            let dmg = Dec::from_i32(deadly(19, 18));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            if state.creature(pc).is_some_and(|c| c.is_alive()) {
                let _ = power::apply(
                    state,
                    content,
                    PowerId::VulnerablePower,
                    pc,
                    Dec::from_i32(2),
                    Some(creature),
                    None,
                )?;
                let _ = power::apply(
                    state,
                    content,
                    PowerId::WeakPower,
                    pc,
                    Dec::from_i32(2),
                    Some(creature),
                    None,
                )?;
            }
        }
        (MonsterId::SpectralKnight, "HEX") => {
            if state.creature(pc).is_some_and(|c| c.is_alive()) {
                let _ = power::apply(
                    state,
                    content,
                    PowerId::HexPower,
                    pc,
                    Dec::from_i32(2),
                    Some(creature),
                    None,
                )?;
            }
        }
        (MonsterId::SpectralKnight, "SOUL_SLASH") => {
            let dmg = Dec::from_i32(deadly(17, 15));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::SpectralKnight, "SOUL_FLAME") => {
            let dmg = Dec::from_i32(deadly(4, 3));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(3)
                .execute(state, content)?;
        }
        (MonsterId::Stabbot, "STAB_MOVE") => {
            let dmg = Dec::from_i32(deadly(12, 11));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            if state.creature(pc).is_some_and(|c| c.is_alive()) {
                let _ = power::apply(
                    state,
                    content,
                    PowerId::FrailPower,
                    pc,
                    Dec::ONE,
                    Some(creature),
                    None,
                )?;
            }
        }
        (MonsterId::TestSubject, "RESPAWN_MOVE") => {
            let respawns = read_monster_int(state, creature, "TestSubject._respawns", 0) + 1;
            set_monster_field(
                state,
                creature,
                "TestSubject._respawns",
                FieldValue::Int(respawns),
            );
            let adaptable_handle = state
                .creature(creature)
                .and_then(|c| c.powers.iter().find(|p| p.id == PowerId::AdaptablePower))
                .map(|p| p.handle);
            if let Some(ph) = adaptable_handle {
                crate::powers::batch_a_m::adaptable_do_revive(state, creature, ph);
            }
            match respawns {
                1 => {
                    let hp = Dec::from_i32(tough(212, 200));
                    let _ = creature_cmd::set_max_hp(state, creature, hp)?;
                    creature_cmd::heal(state, content, creature, hp)?;
                    let _ = power::apply(
                        state,
                        content,
                        PowerId::PainfulStabsPower,
                        creature,
                        Dec::ONE,
                        Some(creature),
                        None,
                    )?;
                }
                2 => {
                    let hp = Dec::from_i32(tough(313, 300));
                    let _ = creature_cmd::set_max_hp(state, creature, hp)?;
                    creature_cmd::heal(state, content, creature, hp)?;
                    let _ = power::apply(
                        state,
                        content,
                        PowerId::NemesisPower,
                        creature,
                        Dec::ONE,
                        Some(creature),
                        None,
                    )?;
                    let _ =
                        power::remove_by_id(state, content, creature, PowerId::AdaptablePower)?;
                    let _ =
                        power::remove_by_id(state, content, creature, PowerId::PainfulStabsPower)?;
                }
                _ => {}
            }
            if let Some(m) = state
                .creature_mut(creature)
                .and_then(|c| c.monster.as_mut())
            {
                m.next_move_can_transition_away = true;
            }
        }
        (MonsterId::TestSubject, "BITE_MOVE") => {
            let dmg = Dec::from_i32(deadly(22, 20));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::TestSubject, "SKULL_BASH_MOVE") => {
            let dmg = Dec::from_i32(deadly(16, 14));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            if state.creature(pc).is_some_and(|c| c.is_alive()) {
                let _ = power::apply(
                    state,
                    content,
                    PowerId::VulnerablePower,
                    pc,
                    Dec::ONE,
                    Some(creature),
                    None,
                )?;
            }
        }
        (MonsterId::TestSubject, "MULTI_CLAW_MOVE") => {
            let dmg = Dec::from_i32(deadly(11, 10));
            let base_hits = if is_dread(state) { 4 } else { 3 };
            let extra = read_monster_int(state, creature, "TestSubject._extraMultiClawCount", 0);
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(base_hits + extra)
                .execute(state, content)?;
            set_monster_field(
                state,
                creature,
                "TestSubject._extraMultiClawCount",
                FieldValue::Int(extra + 1),
            );
        }
        (MonsterId::TestSubject, "PHASE3_LACERATE_MOVE") => {
            let dmg = Dec::from_i32(deadly(11, 10));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(3)
                .execute(state, content)?;
        }
        (MonsterId::TestSubject, "BIG_POUNCE") => {
            AttackBuilder::from_monster(creature, Dec::from_i32(45)).execute(state, content)?;
        }
        (MonsterId::TestSubject, "BURNING_GROWL_MOVE") => {
            let burn_count = deadly(5, 3);
            generate_status_to_player(
                content,
                state,
                CardId::Burn,
                PileType::Discard,
                CardPilePosition::Bottom,
                burn_count,
            )?;
            let str_gain = deadly(3, 2) + if is_dread(state) { 3 } else { 0 };
            let _ = power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                Dec::from_i32(str_gain),
                Some(creature),
                None,
            )?;
        }
        (MonsterId::TheForgotten, "MIASMA") => {
            if state.creature(pc).is_some_and(|c| c.is_alive()) {
                let _ = power::apply(
                    state,
                    content,
                    PowerId::DexterityPower,
                    pc,
                    Dec::from_i32(-2),
                    Some(creature),
                    None,
                )?;
            }
            block::gain_block(
                state,
                content,
                creature,
                Dec::from_i32(8),
                ValueProp::MOVE,
                None,
                None,
            )?;
            let _ = power::apply(
                state,
                content,
                PowerId::DexterityPower,
                creature,
                Dec::from_i32(2),
                Some(creature),
                None,
            )?;
        }
        (MonsterId::TheForgotten, "DREAD") => {
            let dex = power_amount(state, creature, PowerId::DexterityPower);
            let dmg = Dec::from_i32(deadly(15, 13) + dex);
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::TheLost, "DEBILITATING_SMOG") => {
            if state.creature(pc).is_some_and(|c| c.is_alive()) {
                let _ = power::apply(
                    state,
                    content,
                    PowerId::StrengthPower,
                    pc,
                    Dec::from_i32(-2),
                    Some(creature),
                    None,
                )?;
            }
            let _ = power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                Dec::from_i32(2),
                Some(creature),
                None,
            )?;
        }
        (MonsterId::TheLost, "EYE_LASERS") => {
            let dmg = Dec::from_i32(deadly(5, 4));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
        }
        (MonsterId::TorchHeadAmalgam, "STRONG_TACKLE_MOVE") => {
            let dmg = Dec::from_i32(deadly(32, 26) + if is_dread(state) { 5 } else { 0 });
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::TorchHeadAmalgam, "TACKLE_2_MOVE") => {
            let dmg = Dec::from_i32(deadly(22, 18));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::TorchHeadAmalgam, "BEAM_MOVE") => {
            AttackBuilder::from_monster(creature, Dec::from_i32(8))
                .with_hit_count(3)
                .execute(state, content)?;
        }
        (MonsterId::TorchHeadAmalgam, "TACKLE_3_MOVE" | "TACKLE_4_MOVE") => {
            let dmg = Dec::from_i32(deadly(16, 14));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::ToughEgg, "HATCH_MOVE") => {
            set_monster_field(state, creature, "ToughEgg._isHatched", FieldValue::Bool(true));
            let _ = power::remove_by_id(state, content, creature, PowerId::HatchPower)?;
            let to_remove: SmallVec<[u32; 8]> = state
                .creature(creature)
                .map(|c| {
                    c.powers
                        .iter()
                        .filter(|p| !matches!(p.id, PowerId::MinionPower | PowerId::ImpaledPower))
                        .map(|p| p.handle)
                        .collect()
                })
                .unwrap_or_default();
            for ph in to_remove {
                let _ = power::remove_by_handle(state, content, creature, ph)?;
            }
            let min_h = tough(20, 19);
            let max_h = tough(23, 22);
            let hp = state
                .run_rngs
                .get_mut(RunRngType::Niche)
                .next_int_range(min_h, max_h + 1)?;
            creature_cmd::set_max_and_current_hp(state, content, creature, Dec::from_i32(hp))?;
        }
        (MonsterId::ToughEgg, "NIBBLE_MOVE") => {
            let dmg = Dec::from_i32(deadly(5, 4));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::TrainingDummy, "POKE") => {
            AttackBuilder::from_monster(creature, Dec::from_i32(8)).execute(state, content)?;
        }
        (MonsterId::TurretOperator, "UNLOAD_MOVE" | "UNLOAD_MOVE_2") => {
            let dmg = Dec::from_i32(deadly(4, 3));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(5)
                .execute(state, content)?;
        }
        (MonsterId::TurretOperator, "RELOAD_MOVE") => {
            let _ = power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                Dec::ONE,
                Some(creature),
                None,
            )?;
        }
        (MonsterId::WanderingGrunt, "BITE") => {
            AttackBuilder::from_monster(creature, Dec::from_i32(7)).execute(state, content)?;
        }
        (MonsterId::Wriggler, "NASTY_BITE_MOVE") => {
            let dmg = Dec::from_i32(deadly(7, 6));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Wriggler, "WRIGGLE_MOVE") => {
            generate_status_to_player(
                content,
                state,
                CardId::Infection,
                PileType::Discard,
                CardPilePosition::Bottom,
                1,
            )?;
            let _ = power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                Dec::from_i32(2),
                Some(creature),
                None,
            )?;
        }
        (MonsterId::Zapbot, "ZAP") => {
            let dmg = Dec::from_i32(deadly(15, 14));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
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
        }
    }

    if state.creature(creature).is_some_and(|c| c.is_dead())
        && state.enemy_handles.contains(&creature)
        && sts2_kernel::hooks::should_creature_be_removed_after_death(state, content, creature)?
    {
        state.enemy_handles.retain(|h| *h != creature);
        if !state.removed_handles.contains(&creature) {
            state.removed_handles.push(creature);
        }
        if let Some(c) = state.creature_mut(creature) {
            c.is_detached = true;
        }
        state.invalidate_hook_cache();
    }
    Ok(())
}

pub fn dispatch_hook(
    content: &dyn ContentDispatcher,
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
        (MonsterId::Aeonglass, HookSlot::AfterCardGeneratedForCombat) => {
            if let HookArgs::CardGenerated { card, .. } = args {
                if state.card(*card).is_some_and(|c| c.id == CardId::Wither) {
                    let count =
                        read_monster_int(state, creature, "Aeonglass._witherUpgradeCount", 0);
                    apply_wither_fake_upgrade(state, *card, count);
                }
            }
            Ok(HookResult::default())
        }
        (
            MonsterId::BattleFriendV1 | MonsterId::BattleFriendV2 | MonsterId::BattleFriendV3,
            HookSlot::AfterSideTurnEnd,
        ) => {
            if let HookArgs::SideTurn { side, .. } = args {
                let owner_side = state
                    .creature(creature)
                    .map_or(CombatSide::Enemy, |c| c.side);
                if *side == owner_side {
                    let should_escape = state
                        .creature(creature)
                        .and_then(|c| {
                            c.powers
                                .iter()
                                .find(|p| p.id == PowerId::BattlewornDummyTimeLimitPower)
                        })
                        .and_then(|p| p.ext_index)
                        .is_some_and(|ei| {
                            state.power_extensions[ei as usize]
                                .private_fields
                                .get("BattlewornDummyTimeLimitPower._shouldEscape")
                                == Some(&FieldValue::Bool(true))
                        });
                    if should_escape {
                        escape_creature(content, state, creature)?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        (MonsterId::Queen, HookSlot::AfterDeath) => {
            if let HookArgs::AfterDeath { target, .. } = args {
                let is_amalgam = state
                    .creature(*target)
                    .is_some_and(|c| c.monster_id == Some(MonsterId::TorchHeadAmalgam));
                let queen_alive = state.creature(creature).is_some_and(|c| c.is_alive());
                if is_amalgam && queen_alive {
                    set_monster_field(
                        state,
                        creature,
                        "Queen._hasAmalgamDied",
                        FieldValue::Bool(true),
                    );
                    remove_monster_field(state, creature, "Queen._amalgam");
                    if let Some(m) = state
                        .creature_mut(creature)
                        .and_then(|c| c.monster.as_mut())
                    {
                        if m.next_move_id.as_deref() == Some("BURN_BRIGHT_FOR_ME_MOVE")
                            && m.next_move_can_transition_away
                        {
                            m.current_state_id = Cow::Borrowed("ENRAGE_MOVE");
                            m.next_move_id = Some(Cow::Borrowed("ENRAGE_MOVE"));
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        (MonsterId::TestSubject, HookSlot::ShouldDisappearFromDoom) => {
            let respawns = read_monster_int(state, creature, "TestSubject._respawns", 0);
            Ok(HookResult {
                bool_val: Some(respawns >= 2),
                ..Default::default()
            })
        }
        _ => {
            let _ = monster_meta(mid)?;
            Ok(HookResult::default())
        }
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
    let _ = monster_meta(mid)?;
    let move_id = m
        .next_move_id
        .as_deref()
        .unwrap_or(m.current_state_id.as_ref());
    let deadly = |high: i32, low: i32| asc_val(state, AscensionLevel::DeadlyEnemies, high, low);

    match (mid, move_id) {
        (_, "REVIVE_MOVE") => Ok(SmallVec::new()),
        (MonsterId::ActOneGuardian, "CRUSH") => Ok(smallvec![(Dec::from_i32(20), 1)]),
        (MonsterId::Aeonglass, "EBB_MOVE") => Ok(smallvec![(Dec::from_i32(deadly(26, 22)), 1)]),
        (MonsterId::Aeonglass, "EYE_LASERS_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(12, 11)), 2)])
        }
        (MonsterId::Aeonglass, "INCREASING_INTENSITY_MOVE") => Ok(SmallVec::new()),
        (MonsterId::Axebot, "BOOT_UP_MOVE") => Ok(SmallVec::new()),
        (MonsterId::Axebot, "ONE_TWO_MOVE") => Ok(smallvec![(Dec::from_i32(deadly(11, 10)), 2)]),
        (MonsterId::Axebot, "HAMMER_UPPERCUT_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(18, 14)), 1)])
        }
        (MonsterId::BattleFriendV1 | MonsterId::BattleFriendV2 | MonsterId::BattleFriendV3, "IDLE") => {
            Ok(SmallVec::new())
        }
        (
            MonsterId::ByrdineMonster
            | MonsterId::Byrdpip
            | MonsterId::Osty
            | MonsterId::PaelsLegion
            | MonsterId::Wuppy,
            "NOTHING_MOVE",
        ) => Ok(SmallVec::new()),
        (MonsterId::DevotedSculptor, "FORBIDDEN_INCANTATION_MOVE") => Ok(SmallVec::new()),
        (MonsterId::DevotedSculptor, "SAVAGE_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(15, 12)), 1)])
        }
        (MonsterId::EyeWithTeeth, "DISTRACT_MOVE") => Ok(SmallVec::new()),
        (MonsterId::Fabricator, "FABRICATE_MOVE" | "fabricateBranch" | "RAND") => {
            Ok(SmallVec::new())
        }
        (MonsterId::Fabricator, "FABRICATING_STRIKE_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(21, 18)), 1)])
        }
        (MonsterId::Fabricator, "DISINTEGRATE_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(13, 11)), 1)])
        }
        (MonsterId::FakeMerchantMonster, "SWIPE_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(15, 13)), 1)])
        }
        (MonsterId::FakeMerchantMonster, "SPEW_COINS_MOVE") => {
            Ok(smallvec![(Dec::from_i32(2), 8)])
        }
        (MonsterId::FakeMerchantMonster, "THROW_RELIC_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(10, 9)), 1)])
        }
        (MonsterId::FakeMerchantMonster, "ENRAGE_MOVE" | "RAND_MOVE" | "RAND_ATTACK_MOVE") => {
            Ok(SmallVec::new())
        }
        (MonsterId::FatGremlin, "SPAWNED_MOVE" | "FLEE_MOVE") => Ok(SmallVec::new()),
        (MonsterId::FlailKnight | MonsterId::MysteriousKnight, "WAR_CHANT" | "RAND") => {
            Ok(SmallVec::new())
        }
        (MonsterId::FlailKnight | MonsterId::MysteriousKnight, "FLAIL_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(10, 9)), 2)])
        }
        (MonsterId::FlailKnight | MonsterId::MysteriousKnight, "RAM_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(17, 15)), 1)])
        }
        (MonsterId::FrogKnight, "FOR_THE_QUEEN" | "HALF_HEALTH") => Ok(SmallVec::new()),
        (MonsterId::FrogKnight, "STRIKE_DOWN_EVIL") => {
            Ok(smallvec![(Dec::from_i32(deadly(23, 21)), 1)])
        }
        (MonsterId::FrogKnight, "TONGUE_LASH") => {
            Ok(smallvec![(Dec::from_i32(deadly(14, 13)), 1)])
        }
        (MonsterId::FrogKnight, "BEETLE_CHARGE") => {
            Ok(smallvec![(Dec::from_i32(deadly(40, 35)), 1)])
        }
        (MonsterId::GasBomb, "EXPLODE_MOVE") => Ok(smallvec![(Dec::from_i32(deadly(9, 8)), 1)]),
        (MonsterId::GlobeHead, "THUNDER_STRIKE") => {
            Ok(smallvec![(Dec::from_i32(deadly(7, 6)), 3)])
        }
        (MonsterId::GlobeHead, "SHOCKING_SLAP") => {
            Ok(smallvec![(Dec::from_i32(deadly(14, 13)), 1)])
        }
        (MonsterId::GlobeHead, "GALVANIC_BURST") => {
            Ok(smallvec![(Dec::from_i32(deadly(17, 16)), 1)])
        }
        (MonsterId::Guardbot, "GUARD_MOVE") => Ok(SmallVec::new()),
        (MonsterId::HardyBrute, "SLAM") => Ok(smallvec![(Dec::from_i32(14), 1)]),
        (MonsterId::LivingShield, "SHIELD_SLAM_MOVE") => Ok(smallvec![(Dec::from_i32(6), 1)]),
        (MonsterId::LivingShield, "SMASH_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(18, 16)), 1)])
        }
        (MonsterId::LivingShield, "SHIELD_SLAM_BRANCH") => Ok(SmallVec::new()),
        (MonsterId::MagiKnight, "POWER_SHIELD_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(7, 6)), 1)])
        }
        (MonsterId::MagiKnight, "DAMPEN_MOVE" | "PREP_MOVE") => Ok(SmallVec::new()),
        (MonsterId::MagiKnight, "RAM_MOVE") => Ok(smallvec![(Dec::from_i32(deadly(11, 10)), 1)]),
        (MonsterId::MagiKnight, "MAGIC_BOMB") => Ok(smallvec![(Dec::from_i32(deadly(40, 35)), 1)]),
        (MonsterId::MechaKnight, "CHARGE_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(30, 25)), 1)])
        }
        (MonsterId::MechaKnight, "FLAMETHROWER_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(12, 8)), 1)])
        }
        (MonsterId::MechaKnight, "WINDUP_MOVE") => Ok(SmallVec::new()),
        (MonsterId::MechaKnight, "HEAVY_CLEAVE_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(40, 35)), 1)])
        }
        (MonsterId::Noisebot, "NOISE_MOVE") => Ok(SmallVec::new()),
        (MonsterId::OwlMagistrate, "MAGISTRATE_SCRUTINY") => {
            Ok(smallvec![(Dec::from_i32(deadly(17, 16)), 1)])
        }
        (MonsterId::OwlMagistrate, "PECK_ASSAULT") => Ok(smallvec![(Dec::from_i32(4), 6)]),
        (MonsterId::OwlMagistrate, "JUDICIAL_FLIGHT") => Ok(SmallVec::new()),
        (MonsterId::OwlMagistrate, "VERDICT") => Ok(smallvec![(Dec::from_i32(deadly(36, 33)), 1)]),
        (MonsterId::Parafright, "SLAM_MOVE") => Ok(smallvec![(Dec::from_i32(deadly(17, 16)), 1)]),
        (
            MonsterId::Queen,
            "PUPPET_STRINGS_MOVE"
            | "YOU_ARE_MINE_MOVE"
            | "DREAD_INITIAL_MOVE"
            | "BURN_BRIGHT_FOR_ME_MOVE"
            | "ENRAGE_MOVE"
            | "YOURE_MINE_NOW_BRANCH"
            | "BURN_BRIGHT_FOR_ME_BRANCH",
        ) => Ok(SmallVec::new()),
        (MonsterId::Queen, "OFF_WITH_YOUR_HEAD_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(4, 3)), 5)])
        }
        (MonsterId::Queen, "EXECUTION_MOVE") => Ok(smallvec![(Dec::from_i32(deadly(18, 15)), 1)]),
        (MonsterId::ScrollOfBiting, "CHOMP") => Ok(smallvec![(Dec::from_i32(deadly(16, 14)), 1)]),
        (MonsterId::ScrollOfBiting, "CHEW") => Ok(smallvec![(Dec::from_i32(deadly(6, 5)), 2)]),
        (MonsterId::ScrollOfBiting, "MORE_TEETH" | "rand") => Ok(SmallVec::new()),
        (MonsterId::SlimedBerserker, "VOMIT_ICHOR_MOVE" | "LEECHING_HUG_MOVE") => {
            Ok(SmallVec::new())
        }
        (MonsterId::SlimedBerserker, "FURIOUS_PUMMELING_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(5, 4)), 4)])
        }
        (MonsterId::SlimedBerserker, "SMOTHER_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(33, 30)), 1)])
        }
        (MonsterId::SneakyGremlin, "SPAWNED_MOVE") => Ok(SmallVec::new()),
        (MonsterId::SneakyGremlin, "TACKLE_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(10, 9)), 1)])
        }
        (MonsterId::SoulNexus, "SOUL_BURN_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(31, 29)), 1)])
        }
        (MonsterId::SoulNexus, "MAELSTROM_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(7, 6)), 4)])
        }
        (MonsterId::SoulNexus, "DRAIN_LIFE_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(19, 18)), 1)])
        }
        (MonsterId::SoulNexus, "RAND") => Ok(SmallVec::new()),
        (MonsterId::SpectralKnight, "HEX" | "RAND") => Ok(SmallVec::new()),
        (MonsterId::SpectralKnight, "SOUL_SLASH") => {
            Ok(smallvec![(Dec::from_i32(deadly(17, 15)), 1)])
        }
        (MonsterId::SpectralKnight, "SOUL_FLAME") => {
            Ok(smallvec![(Dec::from_i32(deadly(4, 3)), 3)])
        }
        (MonsterId::Stabbot, "STAB_MOVE") => Ok(smallvec![(Dec::from_i32(deadly(12, 11)), 1)]),
        (
            MonsterId::TestSubject,
            "RESPAWN_MOVE" | "BURNING_GROWL_MOVE" | "REVIVE_BRANCH",
        ) => Ok(SmallVec::new()),
        (MonsterId::TestSubject, "BITE_MOVE") => Ok(smallvec![(Dec::from_i32(deadly(22, 20)), 1)]),
        (MonsterId::TestSubject, "SKULL_BASH_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(16, 14)), 1)])
        }
        (MonsterId::TestSubject, "MULTI_CLAW_MOVE") => {
            let base_hits = if is_dread(state) { 4 } else { 3 };
            let extra = read_monster_int(state, creature, "TestSubject._extraMultiClawCount", 0);
            Ok(smallvec![(Dec::from_i32(deadly(11, 10)), base_hits + extra)])
        }
        (MonsterId::TestSubject, "PHASE3_LACERATE_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(11, 10)), 3)])
        }
        (MonsterId::TestSubject, "BIG_POUNCE") => Ok(smallvec![(Dec::from_i32(45), 1)]),
        (MonsterId::TheForgotten, "MIASMA") => Ok(SmallVec::new()),
        (MonsterId::TheForgotten, "DREAD") => {
            let dex = power_amount(state, creature, PowerId::DexterityPower);
            Ok(smallvec![(Dec::from_i32(deadly(15, 13) + dex), 1)])
        }
        (MonsterId::TheLost, "DEBILITATING_SMOG") => Ok(SmallVec::new()),
        (MonsterId::TheLost, "EYE_LASERS") => Ok(smallvec![(Dec::from_i32(deadly(5, 4)), 2)]),
        (MonsterId::TorchHeadAmalgam, "STRONG_TACKLE_MOVE") => {
            let dmg = deadly(32, 26) + if is_dread(state) { 5 } else { 0 };
            Ok(smallvec![(Dec::from_i32(dmg), 1)])
        }
        (MonsterId::TorchHeadAmalgam, "TACKLE_2_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(22, 18)), 1)])
        }
        (MonsterId::TorchHeadAmalgam, "BEAM_MOVE") => Ok(smallvec![(Dec::from_i32(8), 3)]),
        (MonsterId::TorchHeadAmalgam, "TACKLE_3_MOVE" | "TACKLE_4_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(16, 14)), 1)])
        }
        (MonsterId::ToughEgg, "HATCH_MOVE") => Ok(SmallVec::new()),
        (MonsterId::ToughEgg, "NIBBLE_MOVE") => Ok(smallvec![(Dec::from_i32(deadly(5, 4)), 1)]),
        (MonsterId::TrainingDummy, "POKE") => Ok(smallvec![(Dec::from_i32(8), 1)]),
        (MonsterId::TurretOperator, "UNLOAD_MOVE" | "UNLOAD_MOVE_2") => {
            Ok(smallvec![(Dec::from_i32(deadly(4, 3)), 5)])
        }
        (MonsterId::TurretOperator, "RELOAD_MOVE") => Ok(SmallVec::new()),
        (MonsterId::WanderingGrunt, "BITE") => Ok(smallvec![(Dec::from_i32(7), 1)]),
        (MonsterId::Wriggler, "SPAWNED_MOVE" | "WRIGGLE_MOVE" | "INIT_MOVE") => {
            Ok(SmallVec::new())
        }
        (MonsterId::Wriggler, "NASTY_BITE_MOVE") => {
            Ok(smallvec![(Dec::from_i32(deadly(7, 6)), 1)])
        }
        (MonsterId::Zapbot, "ZAP") => Ok(smallvec![(Dec::from_i32(deadly(15, 14)), 1)]),
        _ => Err(KernelError::Unsupported {
            kind: "MonsterIntent",
            id: format!("{}:{}", mid.model_id_str(), move_id),
        }),
    }
}
