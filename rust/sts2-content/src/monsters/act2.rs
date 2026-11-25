use smallvec::{SmallVec, smallvec};
use std::borrow::Cow;
use sts2_kernel::commands::{arrow, block, card_pile, creature as creature_cmd, damage::AttackBuilder, power};
use sts2_kernel::{
    BattleState, CardHandle, CardInst, CardTempFlags, ChoiceRequest, ChoiceTiming,
    ContentDispatcher, CreatureHandle, FieldValue, HookArgs, HookResult, KernelError,
    PendingContinuation, PowerExtension,
};
use sts2_types::{
    AscensionLevel, CardId, CardKeywords, CardPilePosition, CardRarity, CardStaticFlags,
    CombatSide, Dec, EnchantmentId, GameMode, HookMask, HookSlot, MonsterId, MonsterMeta,
    PileType, PowerId, RunRngType, ValueProp, generated_card_meta,
};

static DEFAULT_MONSTER_META: MonsterMeta = MonsterMeta {
    hook_mask: HookMask::EMPTY,
};

#[inline]
fn is_dread(state: &BattleState) -> bool {
    state.static_ctx.game_mode == GameMode::Dread
}

fn read_monster_bool(state: &BattleState, creature: CreatureHandle, key1: &str, key2: &str) -> bool {
    let Some(m) = state.creature(creature).and_then(|c| c.monster.as_ref()) else {
        return false;
    };
    if let Some(FieldValue::Bool(b)) = m.private_fields.get(key1) {
        return *b;
    }
    if let Some(FieldValue::Bool(b)) = m.setup_fields.get(key1) {
        return *b;
    }
    if let Some(FieldValue::Bool(b)) = m.setup_fields.get(key2) {
        return *b;
    }
    false
}

fn read_monster_int(state: &BattleState, creature: CreatureHandle, key1: &str, key2: &str) -> i32 {
    let Some(m) = state.creature(creature).and_then(|c| c.monster.as_ref()) else {
        return 0;
    };
    if let Some(FieldValue::Int(v)) = m.private_fields.get(key1) {
        return *v;
    }
    if let Some(FieldValue::Int(v)) = m.setup_fields.get(key1) {
        return *v;
    }
    if let Some(FieldValue::Int(v)) = m.setup_fields.get(key2) {
        return *v;
    }
    0
}

fn set_monster_bool(state: &mut BattleState, creature: CreatureHandle, key: &'static str, val: bool) {
    if let Some(c) = state.creature_mut(creature) {
        if let Some(m) = &mut c.monster {
            m.private_fields
                .insert(Cow::Borrowed(key), FieldValue::Bool(val));
        }
    }
}

fn set_monster_int(state: &mut BattleState, creature: CreatureHandle, key: &'static str, val: i32) {
    if let Some(c) = state.creature_mut(creature) {
        if let Some(m) = &mut c.monster {
            m.private_fields
                .insert(Cow::Borrowed(key), FieldValue::Int(val));
        }
    }
}

fn set_power_field(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &'static str,
    val: FieldValue,
) {
    let existing_ext = state
        .creature(creature)
        .and_then(|c| c.powers.get(power_idx))
        .and_then(|p| p.ext_index);
    let ext_idx = match existing_ext {
        Some(idx) => idx as usize,
        None => {
            let idx = state.power_extensions.len();
            state.power_extensions.push(PowerExtension::default());
            if let Some(c) = state.creature_mut(creature) {
                if let Some(p) = c.powers.get_mut(power_idx) {
                    p.ext_index = Some(idx as u32);
                }
            }
            idx
        }
    };
    if let Some(ext) = state.power_extensions.get_mut(ext_idx) {
        ext.private_fields.insert(Cow::Borrowed(key), val);
    }
}

#[derive(Clone, Copy)]
enum RepeatRule {
    CannotRepeat,
    CanRepeatXTimes(usize),
}

fn branch_weight(
    state_log_ids: &[Cow<'static, str>],
    state_id: &str,
    rule: RepeatRule,
    weight: f32,
) -> f32 {
    let limit = match rule {
        RepeatRule::CannotRepeat => 1,
        RepeatRule::CanRepeatXTimes(n) => n,
    };
    if limit > 0 && state_log_ids.len() >= limit {
        let tail = &state_log_ids[state_log_ids.len() - limit..];
        if tail.iter().all(|id| id.as_ref() == state_id) {
            return 0.0_f32;
        }
    }
    weight
}

fn pick_random_branch(
    state: &mut BattleState,
    creature: CreatureHandle,
    branches: &[(&'static str, RepeatRule, f32)],
) -> Result<&'static str, KernelError> {
    let mut weights: SmallVec<[f32; 4]> = SmallVec::with_capacity(branches.len());
    let mut total_weight = 0.0_f32;
    {
        let log = &state
            .creature(creature)
            .and_then(|c| c.monster.as_ref())
            .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?
            .state_log_ids;
        for &(id, rule, base_weight) in branches {
            let w = branch_weight(log, id, rule, base_weight);
            weights.push(w);
            total_weight += w;
        }
    }
    let mut random_weight = state
        .run_rngs
        .get_mut(RunRngType::MonsterAi)
        .next_f32_range(0.0_f32, total_weight)
        .map_err(|e| KernelError::InvalidState(format!("MonsterAi rng error: {e}")))?;
    for (idx, &(id, _, _)) in branches.iter().enumerate() {
        random_weight -= weights[idx];
        if random_weight <= 0.0_f32 {
            return Ok(id);
        }
    }
    Err(KernelError::InvalidState(
        "No valid state found in RandomBranchState".to_owned(),
    ))
}

fn living_player_creature(state: &BattleState) -> Option<CreatureHandle> {
    let pc = state.player.creature_handle;
    state
        .creature(pc)
        .is_some_and(|c| c.is_alive() && c.is_player)
        .then_some(pc)
}

fn create_ephemeral_choice_card(state: &mut BattleState, card_id: CardId) -> CardHandle {
    let meta = generated_card_meta(card_id);
    let handle = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    let mut temp_flags = CardTempFlags::empty();
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

fn escape_creature(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    let alive = state.creature(creature).is_some_and(|c| c.is_alive());
    if !alive || !state.is_live_combat() || !state.contains_creature(creature) {
        return Ok(());
    }

    loop {
        let next_impaled = state.creature(creature).and_then(|c| {
            c.powers.iter().find_map(|p| {
                if p.id != PowerId::ImpaledPower {
                    return None;
                }
                let ext_idx = p.ext_index? as usize;
                let has_arrows = !state.power_extensions.get(ext_idx)?.impaled_arrows.is_empty();
                if has_arrows {
                    p.applier_handle
                } else {
                    None
                }
            })
        });
        let Some(applier) = next_impaled else {
            break;
        };
        arrow::dislodge_first_arrow(state, content, creature, applier, None)?;
    }

    if let Some(c) = state.creature_mut(creature) {
        c.powers.clear();
        c.has_minion_power = false;
        c.is_detached = true;
    }
    state.ally_handles.retain(|h| *h != creature);
    state.enemy_handles.retain(|h| *h != creature);
    state.escaped_handles.push(creature);
    state.invalidate_hook_cache();
    Ok(())
}

fn thieving_hopper_priority(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    card_handle: CardHandle,
) -> Result<i32, KernelError> {
    let card = state
        .card(card_handle)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?;
    let rarity = content.card_meta(card.id)?.rarity;
    let is_imbued = state
        .card_ext(card.ext_index)
        .and_then(|ext| ext.enchantment.as_ref())
        .is_some_and(|ench| ench.id == EnchantmentId::Imbued);
    if rarity == CardRarity::Ancient || is_imbued {
        return Ok(3);
    }
    Ok(match rarity {
        CardRarity::Uncommon => 0,
        CardRarity::Common | CardRarity::Rare | CardRarity::Event => 1,
        CardRarity::Basic => 2,
        _ => 4,
    })
}

pub fn monster_meta(id: MonsterId) -> Result<&'static MonsterMeta, KernelError> {
    match id {
        MonsterId::BowlbugEgg
        | MonsterId::BowlbugNectar
        | MonsterId::BowlbugRock
        | MonsterId::BowlbugSilk
        | MonsterId::Chomper
        | MonsterId::Crusher
        | MonsterId::DecimillipedeSegmentBack
        | MonsterId::DecimillipedeSegmentFront
        | MonsterId::DecimillipedeSegmentMiddle
        | MonsterId::Entomancer
        | MonsterId::Exoskeleton
        | MonsterId::HunterKiller
        | MonsterId::InfestedPrism
        | MonsterId::KnowledgeDemon
        | MonsterId::LouseProgenitor
        | MonsterId::Myte
        | MonsterId::Ovicopter
        | MonsterId::Rocket
        | MonsterId::SlumberingBeetle
        | MonsterId::SpinyToad
        | MonsterId::TheInsatiable
        | MonsterId::TheObscura
        | MonsterId::ThievingHopper
        | MonsterId::Tunneler => Ok(&DEFAULT_MONSTER_META),
        _ => Err(KernelError::unsupported("Monster", id.model_id_str().to_owned())),
    }
}

pub fn monster_initial_hp_range(
    state: &BattleState,
    monster_id: MonsterId,
) -> Result<(i32, i32), KernelError> {
    match monster_id {
        MonsterId::BowlbugEgg => Ok((
            state.ascension_value(AscensionLevel::ToughEnemies, 23, 21),
            state.ascension_value(AscensionLevel::ToughEnemies, 24, 22),
        )),
        MonsterId::BowlbugNectar => Ok((
            state.ascension_value(AscensionLevel::ToughEnemies, 36, 35),
            state.ascension_value(AscensionLevel::ToughEnemies, 39, 38),
        )),
        MonsterId::BowlbugRock => Ok((
            state.ascension_value(AscensionLevel::ToughEnemies, 46, 45),
            state.ascension_value(AscensionLevel::ToughEnemies, 49, 48),
        )),
        MonsterId::BowlbugSilk => Ok((
            state.ascension_value(AscensionLevel::ToughEnemies, 41, 40),
            state.ascension_value(AscensionLevel::ToughEnemies, 44, 43),
        )),
        MonsterId::Chomper => Ok((
            state.ascension_value(AscensionLevel::ToughEnemies, 63, 60),
            state.ascension_value(AscensionLevel::ToughEnemies, 67, 64),
        )),
        MonsterId::Crusher => {
            let hp = state.ascension_value(AscensionLevel::ToughEnemies, 219, 209);
            Ok((hp, hp))
        }
        MonsterId::DecimillipedeSegmentBack
        | MonsterId::DecimillipedeSegmentFront
        | MonsterId::DecimillipedeSegmentMiddle => Ok((
            state.ascension_value(AscensionLevel::ToughEnemies, 46, 40),
            state.ascension_value(AscensionLevel::ToughEnemies, 52, 46),
        )),
        MonsterId::Entomancer => {
            let hp = state.ascension_value(AscensionLevel::ToughEnemies, 165, 145);
            Ok((hp, hp))
        }
        MonsterId::Exoskeleton => Ok((
            state.ascension_value(AscensionLevel::ToughEnemies, 26, 24),
            state.ascension_value(AscensionLevel::ToughEnemies, 30, 28),
        )),
        MonsterId::HunterKiller => {
            let hp = state.ascension_value(AscensionLevel::ToughEnemies, 126, 121);
            Ok((hp, hp))
        }
        MonsterId::InfestedPrism => {
            let hp = state.ascension_value(AscensionLevel::ToughEnemies, 171, 161);
            Ok((hp, hp))
        }
        MonsterId::KnowledgeDemon => {
            let hp = state.ascension_value(AscensionLevel::ToughEnemies, 399, 379);
            Ok((hp, hp))
        }
        MonsterId::LouseProgenitor => Ok((
            state.ascension_value(AscensionLevel::ToughEnemies, 138, 134),
            state.ascension_value(AscensionLevel::ToughEnemies, 141, 136),
        )),
        MonsterId::Myte => Ok((
            state.ascension_value(AscensionLevel::ToughEnemies, 64, 61),
            state.ascension_value(AscensionLevel::ToughEnemies, 69, 67),
        )),
        MonsterId::Ovicopter => Ok((
            state.ascension_value(AscensionLevel::ToughEnemies, 126, 124),
            state.ascension_value(AscensionLevel::ToughEnemies, 132, 130),
        )),
        MonsterId::Rocket => {
            let hp = state.ascension_value(AscensionLevel::ToughEnemies, 209, 199);
            Ok((hp, hp))
        }
        MonsterId::SlumberingBeetle => {
            let hp = state.ascension_value(AscensionLevel::ToughEnemies, 89, 86);
            Ok((hp, hp))
        }
        MonsterId::SpinyToad => Ok((
            state.ascension_value(AscensionLevel::ToughEnemies, 121, 116),
            state.ascension_value(AscensionLevel::ToughEnemies, 124, 119),
        )),
        MonsterId::TheInsatiable => {
            let base = state.ascension_value(AscensionLevel::ToughEnemies, 341, 321);
            let hp = base + if is_dread(state) { 20 } else { 0 };
            Ok((hp, hp))
        }
        MonsterId::TheObscura => {
            let hp = state.ascension_value(AscensionLevel::ToughEnemies, 129, 123);
            Ok((hp, hp))
        }
        MonsterId::ThievingHopper => {
            let hp = state.ascension_value(AscensionLevel::ToughEnemies, 84, 79);
            Ok((hp, hp))
        }
        MonsterId::Tunneler => {
            let hp = state.ascension_value(AscensionLevel::ToughEnemies, 92, 87);
            Ok((hp, hp))
        }
        _ => Err(KernelError::unsupported(
            "Monster",
            monster_id.model_id_str().to_owned(),
        )),
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

    let scream_first = read_monster_bool(state, creature, "Chomper._screamFirst", "ScreamFirst");
    let starter_idx = read_monster_int(
        state,
        creature,
        "DecimillipedeSegment._starterMoveIdx",
        "StarterMoveIdx",
    );

    let (initial_state, initial_logged) = match mid {
        MonsterId::BowlbugEgg => ("BITE_MOVE", true),
        MonsterId::BowlbugNectar => ("THRASH_MOVE", true),
        MonsterId::BowlbugRock => ("HEADBUTT_MOVE", true),
        MonsterId::BowlbugSilk => ("TOXIC_SPIT_MOVE", true),
        MonsterId::Chomper => (
            if scream_first {
                "SCREECH_MOVE"
            } else {
                "CLAMP_MOVE"
            },
            true,
        ),
        MonsterId::Crusher => ("THRASH_MOVE", true),
        MonsterId::DecimillipedeSegmentBack
        | MonsterId::DecimillipedeSegmentFront
        | MonsterId::DecimillipedeSegmentMiddle => {
            let s = match starter_idx.rem_euclid(3) {
                0 => "WRITHE_MOVE",
                1 => "BULK_MOVE",
                _ => "CONSTRICT_MOVE",
            };
            (s, true)
        }
        MonsterId::Entomancer => ("BEES_MOVE", true),
        MonsterId::Exoskeleton => ("INIT_MOVE", false),
        MonsterId::HunterKiller => ("TENDERIZING_GOOP_MOVE", true),
        MonsterId::InfestedPrism => ("JAB_MOVE", true),
        MonsterId::KnowledgeDemon => ("CURSE_OF_KNOWLEDGE_MOVE", true),
        MonsterId::LouseProgenitor => ("WEB_CANNON_MOVE", true),
        MonsterId::Myte => ("INIT_MOVE", false),
        MonsterId::Ovicopter => ("LAY_EGGS_MOVE", true),
        MonsterId::Rocket => ("CALCULATING_MOVE", true),
        MonsterId::SlumberingBeetle => ("SLEEP_CHECK", false),
        MonsterId::SpinyToad => ("PROTRUDING_SPIKES_MOVE", true),
        MonsterId::TheInsatiable => ("LIQUIFY_GROUND_MOVE", true),
        MonsterId::TheObscura => ("ILLUSION_MOVE", true),
        MonsterId::ThievingHopper => ("THIEVERY_MOVE", true),
        MonsterId::Tunneler => ("BITE_MOVE", true),
        _ => {
            return Err(KernelError::unsupported(
                "Monster",
                mid.model_id_str().to_owned(),
            ));
        }
    };

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
    let (mid, side) = state
        .creature(creature)
        .and_then(|c| c.monster_id.map(|id| (id, c.side)))
        .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?;
    let _ = monster_meta(mid)?;

    match mid {
        MonsterId::BowlbugRock => {
            power::apply(
                state,
                content,
                PowerId::ImbalancedPower,
                creature,
                Dec::ONE,
                Some(creature),
                None,
            )?;
        }
        MonsterId::Chomper => {
            power::apply(
                state,
                content,
                PowerId::ArtifactPower,
                creature,
                Dec::from_i32(2),
                Some(creature),
                None,
            )?;
        }
        MonsterId::Crusher => {
            power::apply(
                state,
                content,
                PowerId::BackAttackLeftPower,
                creature,
                Dec::ONE,
                Some(creature),
                None,
            )?;
            power::apply(
                state,
                content,
                PowerId::CrabRagePower,
                creature,
                Dec::ONE,
                Some(creature),
                None,
            )?;
            if is_dread(state) {
                power::apply(
                    state,
                    content,
                    PowerId::PlatingPower,
                    creature,
                    Dec::from_i32(14),
                    Some(creature),
                    None,
                )?;
            }
        }
        MonsterId::DecimillipedeSegmentBack
        | MonsterId::DecimillipedeSegmentFront
        | MonsterId::DecimillipedeSegmentMiddle => {
            let (min_hp, max_init_hp) = monster_initial_hp_range(state, mid)?;
            let mut max_hp = state.creature(creature).map_or(min_hp, |c| c.max_hp);
            if max_hp % 2 != 0 {
                max_hp += 1;
            }
            let side_handles: &[CreatureHandle] = if side == CombatSide::Enemy {
                &state.enemy_handles
            } else {
                &state.ally_handles
            };
            let teammate_max_hps: SmallVec<[i32; 8]> = side_handles
                .iter()
                .copied()
                .filter(|&h| h != creature)
                .filter_map(|h| state.creature(h).map(|c| c.max_hp))
                .collect();
            let mut steps = 0;
            while teammate_max_hps.contains(&max_hp) && steps < 16 {
                max_hp += 2;
                if max_hp > max_init_hp {
                    max_hp = min_hp;
                }
                steps += 1;
            }
            let _ = creature_cmd::set_max_hp(state, creature, Dec::from_i32(max_hp))?;
            creature_cmd::heal(state, content, creature, Dec::from_i32(max_hp))?;
            power::apply(
                state,
                content,
                PowerId::ReattachPower,
                creature,
                Dec::from_i32(25),
                Some(creature),
                None,
            )?;
        }
        MonsterId::Entomancer => {
            power::apply(
                state,
                content,
                PowerId::PersonalHivePower,
                creature,
                Dec::ONE,
                Some(creature),
                None,
            )?;
        }
        MonsterId::Exoskeleton => {
            power::apply(
                state,
                content,
                PowerId::HardToKillPower,
                creature,
                Dec::from_i32(9),
                Some(creature),
                None,
            )?;
        }
        MonsterId::InfestedPrism => {
            let amt = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 3, 2));
            power::apply(
                state,
                content,
                PowerId::VitalSparkPower,
                creature,
                amt,
                Some(creature),
                None,
            )?;
        }
        MonsterId::LouseProgenitor => {
            let block = Dec::from_i32(state.ascension_value(AscensionLevel::ToughEnemies, 18, 14));
            power::apply(
                state,
                content,
                PowerId::CurlUpPower,
                creature,
                block,
                Some(creature),
                None,
            )?;
        }
        MonsterId::Rocket => {
            let allies: SmallVec<[CreatureHandle; 8]> = SmallVec::from_slice(&state.ally_handles);
            for ally in allies {
                power::apply(
                    state,
                    content,
                    PowerId::SurroundedPower,
                    ally,
                    Dec::ONE,
                    Some(creature),
                    None,
                )?;
            }
            power::apply(
                state,
                content,
                PowerId::BackAttackRightPower,
                creature,
                Dec::ONE,
                Some(creature),
                None,
            )?;
            power::apply(
                state,
                content,
                PowerId::CrabRagePower,
                creature,
                Dec::ONE,
                Some(creature),
                None,
            )?;
            if is_dread(state) {
                power::apply(
                    state,
                    content,
                    PowerId::PlatingPower,
                    creature,
                    Dec::from_i32(14),
                    Some(creature),
                    None,
                )?;
            }
        }
        MonsterId::SlumberingBeetle => {
            let plating =
                Dec::from_i32(state.ascension_value(AscensionLevel::ToughEnemies, 18, 15));
            power::apply(
                state,
                content,
                PowerId::PlatingPower,
                creature,
                plating,
                Some(creature),
                None,
            )?;
            power::apply(
                state,
                content,
                PowerId::SlumberPower,
                creature,
                Dec::from_i32(3),
                Some(creature),
                None,
            )?;
        }
        MonsterId::ThievingHopper => {
            power::apply(
                state,
                content,
                PowerId::EscapeArtistPower,
                creature,
                Dec::from_i32(5),
                Some(creature),
                None,
            )?;
        }
        MonsterId::BowlbugEgg
        | MonsterId::BowlbugNectar
        | MonsterId::BowlbugSilk
        | MonsterId::HunterKiller
        | MonsterId::KnowledgeDemon
        | MonsterId::Myte
        | MonsterId::Ovicopter
        | MonsterId::SpinyToad
        | MonsterId::TheInsatiable
        | MonsterId::TheObscura
        | MonsterId::Tunneler => {}
        _ => unreachable!(),
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

    let (mid, can_transition_away, performed_first_move, cur_is_move, next_is_none) = {
        let Some(c) = state.creature(creature) else {
            return Ok(());
        };
        let Some(m) = &c.monster else {
            return Ok(());
        };
        let cur = m.next_move_id.as_deref().unwrap_or(m.current_state_id.as_ref());
        let cur_is_move = cur != "INIT_MOVE" && cur != "SLEEP_CHECK";
        let effective_can_transition_away = m.next_move_can_transition_away || cur == "DEAD_MOVE";
        (
            m.monster_id,
            effective_can_transition_away,
            m.performed_first_move,
            cur_is_move,
            m.next_move_id.is_none(),
        )
    };

    if !can_transition_away || (!performed_first_move && cur_is_move) {
        if cur_is_move && next_is_none {
            if let Some(m_mut) = state.creature_mut(creature).and_then(|c| c.monster.as_mut()) {
                m_mut.next_move_id = Some(m_mut.current_state_id.clone());
            }
        }
        return Ok(());
    }

    let is_off_balance =
        read_monster_bool(state, creature, "BowlbugRock._isOffBalance", "IsOffBalance");
    let curse_counter =
        read_monster_int(state, creature, "KnowledgeDemon._curseCounter", "CurseCounter");

    let (cur_inline, slot_tag, side, has_slumber) = {
        let Some(c) = state.creature(creature) else {
            return Ok(());
        };
        let Some(m) = &c.monster else {
            return Ok(());
        };
        let cur_cow = m.next_move_id.as_ref().unwrap_or(&m.current_state_id);
        let slot_tag = match c.slot_name.as_deref() {
            Some("first") => 1u8,
            Some("second") => 2u8,
            Some("third") => 3u8,
            _ => 0u8,
        };
        (
            InlineStateStr::from_cow(cur_cow),
            slot_tag,
            c.side,
            c.powers.iter().any(|p| p.id == PowerId::SlumberPower),
        )
    };

    let cur = cur_inline.as_str();

    let next_id: &'static str = match mid {
        MonsterId::BowlbugEgg => "BITE_MOVE",
        MonsterId::BowlbugNectar => match cur {
            "THRASH_MOVE" => "BUFF_MOVE",
            "BUFF_MOVE" => "THRASH2_MOVE",
            _ => "THRASH2_MOVE",
        },
        MonsterId::BowlbugRock => match cur {
            "HEADBUTT_MOVE" => {
                if is_off_balance {
                    "DIZZY_MOVE"
                } else {
                    "HEADBUTT_MOVE"
                }
            }
            _ => "HEADBUTT_MOVE",
        },
        MonsterId::BowlbugSilk => match cur {
            "TOXIC_SPIT_MOVE" => "THRASH_MOVE",
            _ => "TOXIC_SPIT_MOVE",
        },
        MonsterId::Chomper => match cur {
            "CLAMP_MOVE" => "SCREECH_MOVE",
            _ => "CLAMP_MOVE",
        },
        MonsterId::Crusher => match cur {
            "THRASH_MOVE" => "ENLARGING_STRIKE_MOVE",
            "ENLARGING_STRIKE_MOVE" => "BUG_STING_MOVE",
            "BUG_STING_MOVE" => "ADAPT_MOVE",
            "ADAPT_MOVE" => "GUARDED_STRIKE_MOVE",
            _ => "THRASH_MOVE",
        },
        MonsterId::DecimillipedeSegmentBack
        | MonsterId::DecimillipedeSegmentFront
        | MonsterId::DecimillipedeSegmentMiddle => match cur {
            "WRITHE_MOVE" => "CONSTRICT_MOVE",
            "CONSTRICT_MOVE" => "BULK_MOVE",
            "BULK_MOVE" => "WRITHE_MOVE",
            "DEAD_MOVE" => "REATTACH_MOVE",
            "REATTACH_MOVE" => pick_random_branch(
                state,
                creature,
                &[
                    ("WRITHE_MOVE", RepeatRule::CannotRepeat, 1.0_f32),
                    ("BULK_MOVE", RepeatRule::CannotRepeat, 1.0_f32),
                    ("CONSTRICT_MOVE", RepeatRule::CannotRepeat, 1.0_f32),
                ],
            )?,
            _ => "WRITHE_MOVE",
        },
        MonsterId::Entomancer => match cur {
            "BEES_MOVE" => "SPEAR_MOVE",
            "SPEAR_MOVE" => "PHEROMONE_SPIT_MOVE",
            _ => "BEES_MOVE",
        },
        MonsterId::Exoskeleton => {
            let need_rand = match cur {
                "INIT_MOVE" => match slot_tag {
                    1 => Some("SKITTER_MOVE"),
                    2 => Some("MANDIBLES_MOVE"),
                    3 => Some("ENRAGE_MOVE"),
                    _ => None,
                },
                "MANDIBLES_MOVE" => Some("ENRAGE_MOVE"),
                _ => None,
            };
            match need_rand {
                Some(fixed) => fixed,
                None => pick_random_branch(
                    state,
                    creature,
                    &[
                        ("SKITTER_MOVE", RepeatRule::CannotRepeat, 1.0_f32),
                        ("MANDIBLES_MOVE", RepeatRule::CannotRepeat, 1.0_f32),
                    ],
                )?,
            }
        }
        MonsterId::HunterKiller => pick_random_branch(
            state,
            creature,
            &[
                ("BITE_MOVE", RepeatRule::CannotRepeat, 1.0_f32),
                ("PUNCTURE_MOVE", RepeatRule::CanRepeatXTimes(2), 1.0_f32),
            ],
        )?,
        MonsterId::InfestedPrism => match cur {
            "JAB_MOVE" => "JAB_MOVE_2",
            "JAB_MOVE_2" => "WHIRLWIND_MOVE",
            "WHIRLWIND_MOVE" => "PULSATE_MOVE",
            _ => "JAB_MOVE",
        },
        MonsterId::KnowledgeDemon => match cur {
            "CURSE_OF_KNOWLEDGE_MOVE" => "SLAP_MOVE",
            "SLAP_MOVE" => "KNOWLEDGE_OVERWHELMING_MOVE",
            "KNOWLEDGE_OVERWHELMING_MOVE" => "PONDER_MOVE",
            "PONDER_MOVE" => {
                if curse_counter < 3 {
                    "CURSE_OF_KNOWLEDGE_MOVE"
                } else {
                    "SLAP_MOVE"
                }
            }
            _ => "CURSE_OF_KNOWLEDGE_MOVE",
        },
        MonsterId::LouseProgenitor => match cur {
            "WEB_CANNON_MOVE" => "CURL_AND_GROW_MOVE",
            "CURL_AND_GROW_MOVE" => "POUNCE_MOVE",
            _ => "WEB_CANNON_MOVE",
        },
        MonsterId::Myte => match cur {
            "INIT_MOVE" => {
                if slot_tag == 2 {
                    "SUCK_MOVE"
                } else {
                    "TOXIC_MOVE"
                }
            }
            "TOXIC_MOVE" => "BITE_MOVE",
            "BITE_MOVE" => "SUCK_MOVE",
            _ => "TOXIC_MOVE",
        },
        MonsterId::Ovicopter => match cur {
            "LAY_EGGS_MOVE" | "NUTRITIONAL_PASTE_MOVE" => "SMASH_MOVE",
            "SMASH_MOVE" => "TENDERIZER_MOVE",
            "TENDERIZER_MOVE" => {
                let side_handles: &[CreatureHandle] = if side == CombatSide::Enemy {
                    &state.enemy_handles
                } else {
                    &state.ally_handles
                };
                let living_count = side_handles
                    .iter()
                    .filter(|&&h| state.creature(h).is_some_and(|c| c.is_alive()))
                    .count();
                if living_count <= 3 {
                    "LAY_EGGS_MOVE"
                } else {
                    "NUTRITIONAL_PASTE_MOVE"
                }
            }
            _ => "LAY_EGGS_MOVE",
        },
        MonsterId::Rocket => match cur {
            "CALCULATING_MOVE" => "PRECISE_SMASH_MOVE",
            "PRECISE_SMASH_MOVE" | "PRECISE_SMASH_MOVE_2" => "CHARGE_UP_MOVE",
            "CHARGE_UP_MOVE" => "ROCKET_CLAW_MOVE",
            "ROCKET_CLAW_MOVE" => "RECHARGE_MOVE",
            _ => "CALCULATING_MOVE",
        },
        MonsterId::SlumberingBeetle => match cur {
            "SLEEP_CHECK" | "SNORE_MOVE" => {
                if has_slumber {
                    "SNORE_MOVE"
                } else {
                    "ROLL_OUT_MOVE"
                }
            }
            _ => "ROLL_OUT_MOVE",
        },
        MonsterId::SpinyToad => match cur {
            "PROTRUDING_SPIKES_MOVE" => "SPIKE_EXPLOSION_MOVE",
            "SPIKE_EXPLOSION_MOVE" => "TONGUE_LASH_MOVE",
            _ => "PROTRUDING_SPIKES_MOVE",
        },
        MonsterId::TheInsatiable => match cur {
            "LIQUIFY_GROUND_MOVE" | "THRASH_MOVE_2" => "THRASH_MOVE",
            "THRASH_MOVE" => "LUNGING_BITE_MOVE",
            "LUNGING_BITE_MOVE" => "SALIVATE_MOVE",
            "SALIVATE_MOVE" => "THRASH_MOVE_2",
            _ => "LIQUIFY_GROUND_MOVE",
        },
        MonsterId::TheObscura => pick_random_branch(
            state,
            creature,
            &[
                ("PIERCING_GAZE_MOVE", RepeatRule::CannotRepeat, 1.0_f32),
                ("SAIL_MOVE", RepeatRule::CannotRepeat, 1.0_f32),
                ("HARDENING_STRIKE_MOVE", RepeatRule::CannotRepeat, 1.0_f32),
            ],
        )?,
        MonsterId::ThievingHopper => match cur {
            "THIEVERY_MOVE" => "FLUTTER_MOVE",
            "FLUTTER_MOVE" => "HAT_TRICK_MOVE",
            "HAT_TRICK_MOVE" => "NAB_MOVE",
            _ => "ESCAPE_MOVE",
        },
        MonsterId::Tunneler => match cur {
            "BITE_MOVE" => "BURROW_MOVE",
            _ => "FROM_BELOW_MOVE",
        },
        _ => {
            return Err(KernelError::unsupported(
                "Monster",
                mid.model_id_str().to_owned(),
            ));
        }
    };

    let can_transition = next_id != "REATTACH_MOVE";
    if let Some(c_mut) = state.creature_mut(creature) {
        if let Some(m_mut) = &mut c_mut.monster {
            m_mut.current_state_id = Cow::Borrowed(next_id);
            m_mut.next_move_id = Some(Cow::Borrowed(next_id));
            m_mut.next_move_can_transition_away = can_transition;
            m_mut.state_log_ids.push(Cow::Borrowed(next_id));
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
    let side = state
        .creature(creature)
        .map_or(CombatSide::Enemy, |c| c.side);
    let _ = side;

    if move_id == "STUNNED" {
        match mid {
            MonsterId::SlumberingBeetle => {
                let _ = power::remove_by_id(state, content, creature, PowerId::PlatingPower)?;
            }
            MonsterId::BowlbugRock => {
                set_monster_bool(state, creature, "BowlbugRock._isOffBalance", false);
            }
            MonsterId::Tunneler => {
                set_monster_bool(state, creature, "Tunneler._isStunned", false);
            }
            _ => {
                let _ = monster_meta(mid)?;
            }
        }
        if let Some(c) = state.creature_mut(creature) {
            if let Some(m) = &mut c.monster {
                m.performed_first_move = true;
                m.is_performing_move = false;
                m.next_move_can_transition_away = true;
            }
        }
        return Ok(());
    }

    match (mid, move_id.as_ref()) {
        (MonsterId::BowlbugEgg, "BITE_MOVE") => {
            let amt = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 8, 7));
            AttackBuilder::from_monster(creature, amt).execute(state, content)?;
            block::gain_block(state, content, creature, amt, ValueProp::MOVE, None, None)?;
        }
        (MonsterId::BowlbugNectar, "THRASH_MOVE" | "THRASH2_MOVE") => {
            AttackBuilder::from_monster(creature, Dec::from_i32(3)).execute(state, content)?;
        }
        (MonsterId::BowlbugNectar, "BUFF_MOVE") => {
            let amt = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 16, 15));
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                amt,
                Some(creature),
                None,
            )?;
        }
        (MonsterId::BowlbugRock, "HEADBUTT_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 16, 15));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            if read_monster_bool(state, creature, "BowlbugRock._isOffBalance", "IsOffBalance") {
                creature_cmd::stun(state, creature, None)?;
            }
        }
        (MonsterId::BowlbugRock, "DIZZY_MOVE") => {
            set_monster_bool(state, creature, "BowlbugRock._isOffBalance", false);
        }
        (MonsterId::BowlbugSilk, "THRASH_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 5, 4));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
        }
        (MonsterId::BowlbugSilk, "TOXIC_SPIT_MOVE") => {
            if let Some(pc) = living_player_creature(state) {
                power::apply(
                    state,
                    content,
                    PowerId::WeakPower,
                    pc,
                    Dec::ONE,
                    Some(creature),
                    None,
                )?;
            }
        }
        (MonsterId::Chomper, "CLAMP_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 9, 8));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
        }
        (MonsterId::Chomper, "SCREECH_MOVE") => {
            if state.player_creature().is_some_and(|c| c.is_player) {
                for _ in 0..3 {
                    let _ = card_pile::add_generated_card_to_combat(
                        state,
                        content,
                        CardId::Dazed,
                        PileType::Discard,
                        CardPilePosition::Bottom,
                        0,
                        None,
                    )?;
                }
            }
        }
        (MonsterId::Crusher, "THRASH_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 14, 12));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Crusher, "ENLARGING_STRIKE_MOVE") => {
            AttackBuilder::from_monster(creature, Dec::from_i32(4)).execute(state, content)?;
        }
        (MonsterId::Crusher, "BUG_STING_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 7, 6));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
            if let Some(pc) = living_player_creature(state) {
                power::apply(
                    state,
                    content,
                    PowerId::WeakPower,
                    pc,
                    Dec::from_i32(2),
                    Some(creature),
                    None,
                )?;
                power::apply(
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
        (MonsterId::Crusher, "ADAPT_MOVE") => {
            let amt = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 3, 2));
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                amt,
                Some(creature),
                None,
            )?;
        }
        (MonsterId::Crusher, "GUARDED_STRIKE_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 14, 12));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            block::gain_block(
                state,
                content,
                creature,
                Dec::from_i32(18),
                ValueProp::MOVE,
                None,
                None,
            )?;
        }
        (
            MonsterId::DecimillipedeSegmentBack
            | MonsterId::DecimillipedeSegmentFront
            | MonsterId::DecimillipedeSegmentMiddle,
            "WRITHE_MOVE",
        ) => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 6, 5));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
        }
        (
            MonsterId::DecimillipedeSegmentBack
            | MonsterId::DecimillipedeSegmentFront
            | MonsterId::DecimillipedeSegmentMiddle,
            "BULK_MOVE",
        ) => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 7, 6));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                Dec::from_i32(2),
                Some(creature),
                None,
            )?;
        }
        (
            MonsterId::DecimillipedeSegmentBack
            | MonsterId::DecimillipedeSegmentFront
            | MonsterId::DecimillipedeSegmentMiddle,
            "CONSTRICT_MOVE",
        ) => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 9, 8));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            if let Some(pc) = living_player_creature(state) {
                power::apply(
                    state,
                    content,
                    PowerId::WeakPower,
                    pc,
                    Dec::ONE,
                    Some(creature),
                    None,
                )?;
            }
        }
        (
            MonsterId::DecimillipedeSegmentBack
            | MonsterId::DecimillipedeSegmentFront
            | MonsterId::DecimillipedeSegmentMiddle,
            "DEAD_MOVE",
        ) => {
            if let Some(c) = state.creature_mut(creature) {
                if let Some(m) = &mut c.monster {
                    m.next_move_can_transition_away = true;
                }
            }
        }
        (
            MonsterId::DecimillipedeSegmentBack
            | MonsterId::DecimillipedeSegmentFront
            | MonsterId::DecimillipedeSegmentMiddle,
            "REATTACH_MOVE",
        ) => {
            let ph = state
                .creature(creature)
                .and_then(|c| c.powers.iter().find(|p| p.id == PowerId::ReattachPower))
                .map(|p| p.handle)
                .ok_or_else(|| KernelError::InvalidState("missing ReattachPower".to_owned()))?;
            crate::powers::batch_n_z::reattach_do_reattach(content, state, creature, ph)?;
            if let Some(c) = state.creature_mut(creature) {
                if let Some(m) = &mut c.monster {
                    m.next_move_can_transition_away = true;
                }
            }
        }
        (MonsterId::Entomancer, "PHEROMONE_SPIT_MOVE") => {
            let hive_amt = state
                .creature(creature)
                .and_then(|c| c.powers.iter().find(|p| p.id == PowerId::PersonalHivePower))
                .map(|p| p.amount);
            match hive_amt {
                Some(amt) if amt < 3 => {
                    power::apply(
                        state,
                        content,
                        PowerId::PersonalHivePower,
                        creature,
                        Dec::ONE,
                        Some(creature),
                        None,
                    )?;
                    power::apply(
                        state,
                        content,
                        PowerId::StrengthPower,
                        creature,
                        Dec::ONE,
                        Some(creature),
                        None,
                    )?;
                }
                _ => {
                    power::apply(
                        state,
                        content,
                        PowerId::StrengthPower,
                        creature,
                        Dec::from_i32(2),
                        Some(creature),
                        None,
                    )?;
                }
            }
        }
        (MonsterId::Entomancer, "BEES_MOVE") => {
            let repeats = state.ascension_value(AscensionLevel::DeadlyEnemies, 8, 7);
            AttackBuilder::from_monster(creature, Dec::from_i32(3))
                .with_hit_count(repeats)
                .execute(state, content)?;
        }
        (MonsterId::Entomancer, "SPEAR_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 20, 18));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Exoskeleton, "SKITTER_MOVE") => {
            let repeats = state.ascension_value(AscensionLevel::DeadlyEnemies, 4, 3);
            AttackBuilder::from_monster(creature, Dec::ONE)
                .with_hit_count(repeats)
                .execute(state, content)?;
        }
        (MonsterId::Exoskeleton, "MANDIBLES_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 9, 8));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Exoskeleton, "ENRAGE_MOVE") => {
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                Dec::from_i32(2),
                Some(creature),
                None,
            )?;
        }
        (MonsterId::HunterKiller, "TENDERIZING_GOOP_MOVE") => {
            if let Some(pc) = living_player_creature(state) {
                power::apply(
                    state,
                    content,
                    PowerId::TenderPower,
                    pc,
                    Dec::ONE,
                    Some(creature),
                    None,
                )?;
            }
        }
        (MonsterId::HunterKiller, "BITE_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 19, 17));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::HunterKiller, "PUNCTURE_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 8, 7));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(3)
                .execute(state, content)?;
        }
        (MonsterId::InfestedPrism, "JAB_MOVE" | "JAB_MOVE_2") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 17, 15));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::InfestedPrism, "WHIRLWIND_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 6, 5));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(3)
                .execute(state, content)?;
        }
        (MonsterId::InfestedPrism, "PULSATE_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 10, 8));
            let spark = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 3, 2));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            if let Some(pc) = living_player_creature(state) {
                power::apply(
                    state,
                    content,
                    PowerId::VulnerablePower,
                    pc,
                    Dec::ONE,
                    Some(creature),
                    None,
                )?;
            }
            power::apply(
                state,
                content,
                PowerId::VitalSparkPower,
                creature,
                spark,
                Some(creature),
                None,
            )?;
        }
        (MonsterId::KnowledgeDemon, "CURSE_OF_KNOWLEDGE_MOVE") => {
            let curse_counter =
                read_monster_int(state, creature, "KnowledgeDemon._curseCounter", "CurseCounter");
            if state.player_creature().is_some_and(|c| c.is_player) {
                let dis_amt = (if is_dread(state) { 8 } else { 6 }) + curse_counter;
                let dis_h = create_ephemeral_choice_card(state, CardId::Disintegration);
                if let Some(ext) = state.card_ext_mut(dis_h) {
                    ext.private_fields.insert(
                        Cow::Borrowed("Disintegration.<PowerAmount>k__BackingField"),
                        FieldValue::Int(dis_amt),
                    );
                }
                let alt_id = match curse_counter {
                    0 => CardId::MindRot,
                    1 => CardId::Sloth,
                    _ => CardId::WasteAway,
                };
                let alt_h = create_ephemeral_choice_card(state, alt_id);
                state.pending = Some(PendingContinuation {
                    request: ChoiceRequest {
                        timing: ChoiceTiming::EnemyTurn,
                        source_id: MonsterId::KnowledgeDemon.model_id_str().into(),
                        source_pile: PileType::None,
                        min_count: 1,
                        max_count: 1,
                        candidates: smallvec![dis_h, alt_h],
                        can_skip: false,
                    },
                    frames: SmallVec::new(),
                });
                return Ok(());
            }
            set_monster_int(
                state,
                creature,
                "KnowledgeDemon._curseCounter",
                curse_counter + 1,
            );
        }
        (MonsterId::KnowledgeDemon, "SLAP_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 18, 17));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::KnowledgeDemon, "KNOWLEDGE_OVERWHELMING_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 9, 8));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(3)
                .execute(state, content)?;
        }
        (MonsterId::KnowledgeDemon, "PONDER_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 13, 11));
            let str_gain =
                Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 3, 2));
            let target_count = state
                .ally_handles
                .iter()
                .filter(|&&h| {
                    state
                        .creature(h)
                        .is_some_and(|c| c.is_player || (!c.is_monster() && !c.is_pet()))
                })
                .count() as i32;
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            creature_cmd::heal(state, content, creature, Dec::from_i32(30 * target_count))?;
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                str_gain,
                Some(creature),
                None,
            )?;
        }
        (MonsterId::LouseProgenitor, "WEB_CANNON_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 10, 9));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            if let Some(pc) = living_player_creature(state) {
                power::apply(
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
        (MonsterId::LouseProgenitor, "CURL_AND_GROW_MOVE") => {
            let blk = Dec::from_i32(state.ascension_value(AscensionLevel::ToughEnemies, 18, 14));
            let str_gain =
                Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 7, 5));
            block::gain_block(state, content, creature, blk, ValueProp::MOVE, None, None)?;
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                str_gain,
                Some(creature),
                None,
            )?;
            set_monster_bool(state, creature, "LouseProgenitor._curled", true);
        }
        (MonsterId::LouseProgenitor, "POUNCE_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 16, 14));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Myte, "TOXIC_MOVE") => {
            if state.player_creature().is_some_and(|c| c.is_player) {
                for _ in 0..2 {
                    let _ = card_pile::add_generated_card_to_combat(
                        state,
                        content,
                        CardId::Toxic,
                        PileType::Hand,
                        CardPilePosition::Bottom,
                        0,
                        None,
                    )?;
                }
            }
        }
        (MonsterId::Myte, "BITE_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 15, 13));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Myte, "SUCK_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 6, 4));
            let str_gain =
                Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 3, 2));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                str_gain,
                Some(creature),
                None,
            )?;
        }
        (MonsterId::Ovicopter, "LAY_EGGS_MOVE") => {
            const SLOTS: [&str; 5] = ["egg5", "egg4", "egg3", "egg2", "egg1"];
            for _ in 0..3 {
                let free_slot = SLOTS.iter().copied().find(|&slot| {
                    state.enemy_handles.iter().all(|&eh| {
                        state
                            .creature(eh)
                            .is_none_or(|c| c.is_dead() || c.slot_name.as_deref() != Some(slot))
                    })
                });
                let Some(slot) = free_slot else {
                    break;
                };
                let egg = creature_cmd::spawn_monster_in_combat(
                    state,
                    content,
                    MonsterId::ToughEgg,
                    side,
                    Some(Cow::Borrowed(slot)),
                )?;
                power::apply(
                    state,
                    content,
                    PowerId::MinionPower,
                    egg,
                    Dec::ONE,
                    Some(creature),
                    None,
                )?;
            }
        }
        (MonsterId::Ovicopter, "SMASH_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 17, 16));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Ovicopter, "TENDERIZER_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 8, 7));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            if let Some(pc) = living_player_creature(state) {
                power::apply(
                    state,
                    content,
                    PowerId::VulnerablePower,
                    pc,
                    Dec::from_i32(2),
                    Some(creature),
                    None,
                )?;
            }
        }
        (MonsterId::Ovicopter, "NUTRITIONAL_PASTE_MOVE") => {
            let str_gain =
                Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 4, 3));
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                str_gain,
                Some(creature),
                None,
            )?;
        }
        (MonsterId::Rocket, "CALCULATING_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 4, 3));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Rocket, "PRECISE_SMASH_MOVE" | "PRECISE_SMASH_MOVE_2") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 20, 18));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Rocket, "CHARGE_UP_MOVE") => {
            let str_gain =
                Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 3, 2));
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                str_gain,
                Some(creature),
                None,
            )?;
        }
        (MonsterId::Rocket, "ROCKET_CLAW_MOVE") => {
            let base = state.ascension_value(AscensionLevel::DeadlyEnemies, 35, 31);
            let dmg = Dec::from_i32(base + if is_dread(state) { 5 } else { 0 });
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Rocket, "RECHARGE_MOVE") => {}
        (MonsterId::SlumberingBeetle, "SNORE_MOVE") => {}
        (MonsterId::SlumberingBeetle, "ROLL_OUT_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 18, 16));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                Dec::from_i32(2),
                Some(creature),
                None,
            )?;
        }
        (MonsterId::SpinyToad, "PROTRUDING_SPIKES_MOVE") => {
            power::apply(
                state,
                content,
                PowerId::ThornsPower,
                creature,
                Dec::from_i32(5),
                Some(creature),
                None,
            )?;
        }
        (MonsterId::SpinyToad, "SPIKE_EXPLOSION_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 25, 23));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
            if let Some(thorns_handle) = state
                .creature(creature)
                .and_then(|c| c.powers.iter().find(|p| p.id == PowerId::ThornsPower))
                .map(|p| p.handle)
            {
                let _ = power::modify_amount_by_handle(
                    state,
                    content,
                    creature,
                    thorns_handle,
                    Dec::from_i32(-5),
                    Some(creature),
                    None,
                )?;
            }
        }
        (MonsterId::SpinyToad, "TONGUE_LASH_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 19, 17));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::TheInsatiable, "LIQUIFY_GROUND_MOVE") => {
            if state.player_creature().is_some_and(|c| c.is_player) {
                let pc = state.player.creature_handle;
                let sandpit_amt = Dec::from_i32(if is_dread(state) { 3 } else { 4 });
                let pidx = power::apply(
                    state,
                    content,
                    PowerId::SandpitPower,
                    creature,
                    sandpit_amt,
                    Some(creature),
                    None,
                )?
                .ok_or_else(|| {
                    KernelError::InvalidState("Liquify must create a Sandpit instance.".to_owned())
                })?;
                set_power_field(
                    state,
                    creature,
                    pidx,
                    "SandpitPower.<Target>k__BackingField",
                    FieldValue::CreatureHandle(pc.0 as i32),
                );
                for i in 0..6 {
                    let pile = if i < 3 {
                        PileType::Draw
                    } else {
                        PileType::Discard
                    };
                    let _ = card_pile::add_generated_card_to_combat(
                        state,
                        content,
                        CardId::FranticEscape,
                        pile,
                        CardPilePosition::Random,
                        0,
                        None,
                    )?;
                }
            }
        }
        (MonsterId::TheInsatiable, "THRASH_MOVE" | "THRASH_MOVE_2") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 9, 8));
            AttackBuilder::from_monster(creature, dmg)
                .with_hit_count(2)
                .execute(state, content)?;
        }
        (MonsterId::TheInsatiable, "LUNGING_BITE_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 31, 28));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::TheInsatiable, "SALIVATE_MOVE") => {
            let str_gain =
                Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 3, 2));
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                creature,
                str_gain,
                Some(creature),
                None,
            )?;
        }
        (MonsterId::TheObscura, "ILLUSION_MOVE") => {
            let _ = creature_cmd::spawn_monster_in_combat(
                state,
                content,
                MonsterId::Parafright,
                side,
                Some(Cow::Borrowed("illusion")),
            )?;
        }
        (MonsterId::TheObscura, "PIERCING_GAZE_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 11, 10));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::TheObscura, "SAIL_MOVE") => {
            let teammates: SmallVec<[CreatureHandle; 8]> = if side == CombatSide::Enemy {
                SmallVec::from_slice(&state.enemy_handles)
            } else {
                SmallVec::from_slice(&state.ally_handles)
            };
            for teammate in teammates {
                if state.creature(teammate).is_some_and(|c| c.is_alive()) {
                    power::apply(
                        state,
                        content,
                        PowerId::StrengthPower,
                        teammate,
                        Dec::from_i32(3),
                        Some(creature),
                        None,
                    )?;
                }
            }
        }
        (MonsterId::TheObscura, "HARDENING_STRIKE_MOVE") => {
            let amt = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 7, 6));
            AttackBuilder::from_monster(creature, amt).execute(state, content)?;
            block::gain_block(state, content, creature, amt, ValueProp::MOVE, None, None)?;
        }
        (MonsterId::ThievingHopper, "THIEVERY_MOVE") => {
            if living_player_creature(state).is_some() {
                let candidates: Vec<CardHandle> = state
                    .player
                    .draw
                    .iter()
                    .chain(state.player.discard.iter())
                    .copied()
                    .collect();
                let mut eligible: Vec<(CardHandle, i32)> = Vec::new();
                for ch in candidates {
                    let Some(c) = state.card(ch) else {
                        continue;
                    };
                    if c.deck_card_handle.is_none() {
                        continue;
                    }
                    if content.card_meta(c.id)?.rarity == CardRarity::Quest {
                        continue;
                    }
                    let prio = thieving_hopper_priority(state, content, ch)?;
                    eligible.push((ch, prio));
                }
                if let Some(min_prio) = eligible.iter().map(|&(_, p)| p).min() {
                    let top: Vec<CardHandle> = eligible
                        .into_iter()
                        .filter_map(|(ch, p)| (p == min_prio).then_some(ch))
                        .collect();
                    if let Some(&chosen_combat_card) = state
                        .run_rngs
                        .get_mut(RunRngType::CombatCardSelection)
                        .next_item(&top)
                    {
                        let deck_h = state
                            .card(chosen_combat_card)
                            .and_then(|c| c.deck_card_handle.get())
                            .ok_or_else(|| {
                                KernelError::InvalidState(
                                    "Only a persistent-deck combat copy can be stolen.".to_owned(),
                                )
                            })?;
                        card_pile::remove(state, chosen_combat_card);
                        let pidx = power::apply(
                            state,
                            content,
                            PowerId::SwipePower,
                            creature,
                            Dec::ONE,
                            Some(creature),
                            None,
                        )?
                        .ok_or_else(|| {
                            KernelError::InvalidState(
                                "Thievery must create SwipePower.".to_owned(),
                            )
                        })?;
                        set_power_field(
                            state,
                            creature,
                            pidx,
                            "SwipePower.<StolenCard>k__BackingField",
                            FieldValue::CardHandle(deck_h.0 as i32),
                        );
                        state.deck_dirty.0 = true;
                        state.player.deck.retain(|h| *h != deck_h);
                        if let Some(dc) = state.card_mut(deck_h) {
                            dc.pile = PileType::None;
                        }
                        state.invalidate_hook_cache();
                    }
                }
            }
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 19, 17));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::ThievingHopper, "FLUTTER_MOVE") => {
            set_monster_bool(state, creature, "ThievingHopper._isHovering", true);
            power::apply(
                state,
                content,
                PowerId::FlutterPower,
                creature,
                Dec::from_i32(5),
                Some(creature),
                None,
            )?;
        }
        (MonsterId::ThievingHopper, "HAT_TRICK_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 23, 21));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::ThievingHopper, "NAB_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 16, 14));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::ThievingHopper, "ESCAPE_MOVE") => {
            set_monster_bool(state, creature, "ThievingHopper._isHovering", false);
            escape_creature(state, content, creature)?;
        }
        (MonsterId::Tunneler, "BITE_MOVE") => {
            set_monster_bool(state, creature, "Tunneler._isStunned", false);
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 15, 13));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        (MonsterId::Tunneler, "BURROW_MOVE") => {
            set_monster_bool(state, creature, "Tunneler._isStunned", false);
            let blk = Dec::from_i32(state.ascension_value(AscensionLevel::ToughEnemies, 37, 32));
            power::apply(
                state,
                content,
                PowerId::BurrowedPower,
                creature,
                Dec::ONE,
                Some(creature),
                None,
            )?;
            block::gain_block(state, content, creature, blk, ValueProp::MOVE, None, None)?;
        }
        (MonsterId::Tunneler, "FROM_BELOW_MOVE") => {
            set_monster_bool(state, creature, "Tunneler._isStunned", false);
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 28, 24));
            AttackBuilder::from_monster(creature, dmg).execute(state, content)?;
        }
        _ => {
            return Err(KernelError::unsupported(
                "MonsterMove",
                format!("{}:{}", mid.model_id_str(), move_id),
            ));
        }
    }

    if let Some(c) = state.creature_mut(creature) {
        if let Some(m) = &mut c.monster {
            m.performed_first_move = true;
            m.is_performing_move = false;
        }
    }
    Ok(())
}

pub fn monster_resume_move_choice(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    selected: &[CardHandle],
) -> Result<(), KernelError> {
    let mid = state
        .creature(creature)
        .and_then(|c| c.monster_id)
        .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?;
    if mid != MonsterId::KnowledgeDemon {
        return Err(KernelError::unsupported(
            "MonsterMoveChoiceResume",
            mid.model_id_str().to_owned(),
        ));
    }

    let mut chosen_power: Option<(CardHandle, PowerId, i32)> = None;
    if let Some(&chosen_handle) = selected.first() {
        let card = state
            .card(chosen_handle)
            .ok_or_else(|| {
                KernelError::InvalidState("missing KnowledgeDemon choice card".to_owned())
            })?;
        let (power_id, amt) = match card.id {
            CardId::Disintegration => {
                let amt = state
                    .card_ext(card.ext_index)
                    .and_then(|ext| {
                        ext.private_fields
                            .get("Disintegration.<PowerAmount>k__BackingField")
                    })
                    .and_then(|fv| match fv {
                        FieldValue::Int(v) => Some(*v),
                        _ => None,
                    })
                    .unwrap_or(6);
                (PowerId::DisintegrationPower, amt)
            }
            CardId::MindRot => (PowerId::MindRotPower, 1),
            CardId::Sloth => (PowerId::SlothPower, 3),
            CardId::WasteAway => (PowerId::WasteAwayPower, 1),
            other => {
                return Err(KernelError::InvalidAction(format!(
                    "invalid KnowledgeDemon choice card: {}",
                    other.model_id_str()
                )));
            }
        };
        chosen_power = Some((chosen_handle, power_id, amt));
    }

    for c in &mut state.cards {
        if c.pile == PileType::None
            && matches!(
                c.id,
                CardId::Disintegration | CardId::MindRot | CardId::Sloth | CardId::WasteAway
            )
        {
            c.removed_from_state = true;
        }
    }
    state.invalidate_hook_cache();

    if let Some((chosen_handle, power_id, amt)) = chosen_power {
        let pc = state.player.creature_handle;
        let _ = power::apply(
            state,
            content,
            power_id,
            pc,
            Dec::from_i32(amt),
            None,
            Some(chosen_handle),
        )?;
    }

    let curse_counter =
        read_monster_int(state, creature, "KnowledgeDemon._curseCounter", "CurseCounter");
    set_monster_int(
        state,
        creature,
        "KnowledgeDemon._curseCounter",
        curse_counter + 1,
    );

    if let Some(c) = state.creature_mut(creature) {
        if let Some(m) = &mut c.monster {
            m.performed_first_move = true;
            m.is_performing_move = false;
        }
    }
    Ok(())
}

pub fn dispatch_hook(
    _content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    _slot: HookSlot,
    _args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    let id = state
        .creature(creature)
        .and_then(|c| c.monster_id)
        .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?;
    Err(KernelError::unsupported(
        "MonsterHook",
        id.model_id_str().to_owned(),
    ))
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
    Err(KernelError::unsupported(
        "MonsterHookChoiceResume",
        format!("{}:{slot:?}", id.model_id_str()),
    ))
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
    if move_id == "STUNNED" {
        return Ok(SmallVec::new());
    }

    match (mid, move_id) {
        (MonsterId::BowlbugEgg, "BITE_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 8, 7));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::BowlbugNectar, "THRASH_MOVE" | "THRASH2_MOVE") => {
            Ok(smallvec![(Dec::from_i32(3), 1)])
        }
        (MonsterId::BowlbugNectar, "BUFF_MOVE") => Ok(SmallVec::new()),
        (MonsterId::BowlbugRock, "HEADBUTT_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 16, 15));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::BowlbugRock, "DIZZY_MOVE") => Ok(SmallVec::new()),
        (MonsterId::BowlbugSilk, "THRASH_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 5, 4));
            Ok(smallvec![(dmg, 2)])
        }
        (MonsterId::BowlbugSilk, "TOXIC_SPIT_MOVE") => Ok(SmallVec::new()),
        (MonsterId::Chomper, "CLAMP_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 9, 8));
            Ok(smallvec![(dmg, 2)])
        }
        (MonsterId::Chomper, "SCREECH_MOVE") => Ok(SmallVec::new()),
        (MonsterId::Crusher, "THRASH_MOVE" | "GUARDED_STRIKE_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 14, 12));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::Crusher, "ENLARGING_STRIKE_MOVE") => Ok(smallvec![(Dec::from_i32(4), 1)]),
        (MonsterId::Crusher, "BUG_STING_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 7, 6));
            Ok(smallvec![(dmg, 2)])
        }
        (MonsterId::Crusher, "ADAPT_MOVE") => Ok(SmallVec::new()),
        (
            MonsterId::DecimillipedeSegmentBack
            | MonsterId::DecimillipedeSegmentFront
            | MonsterId::DecimillipedeSegmentMiddle,
            "WRITHE_MOVE",
        ) => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 6, 5));
            Ok(smallvec![(dmg, 2)])
        }
        (
            MonsterId::DecimillipedeSegmentBack
            | MonsterId::DecimillipedeSegmentFront
            | MonsterId::DecimillipedeSegmentMiddle,
            "BULK_MOVE",
        ) => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 7, 6));
            Ok(smallvec![(dmg, 1)])
        }
        (
            MonsterId::DecimillipedeSegmentBack
            | MonsterId::DecimillipedeSegmentFront
            | MonsterId::DecimillipedeSegmentMiddle,
            "CONSTRICT_MOVE",
        ) => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 9, 8));
            Ok(smallvec![(dmg, 1)])
        }
        (
            MonsterId::DecimillipedeSegmentBack
            | MonsterId::DecimillipedeSegmentFront
            | MonsterId::DecimillipedeSegmentMiddle,
            "DEAD_MOVE" | "REATTACH_MOVE",
        ) => Ok(SmallVec::new()),
        (MonsterId::Entomancer, "BEES_MOVE") => {
            let repeats = state.ascension_value(AscensionLevel::DeadlyEnemies, 8, 7);
            Ok(smallvec![(Dec::from_i32(3), repeats)])
        }
        (MonsterId::Entomancer, "SPEAR_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 20, 18));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::Entomancer, "PHEROMONE_SPIT_MOVE") => Ok(SmallVec::new()),
        (MonsterId::Exoskeleton, "SKITTER_MOVE") => {
            let repeats = state.ascension_value(AscensionLevel::DeadlyEnemies, 4, 3);
            Ok(smallvec![(Dec::ONE, repeats)])
        }
        (MonsterId::Exoskeleton, "MANDIBLES_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 9, 8));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::Exoskeleton, "ENRAGE_MOVE" | "INIT_MOVE") => Ok(SmallVec::new()),
        (MonsterId::HunterKiller, "BITE_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 19, 17));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::HunterKiller, "PUNCTURE_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 8, 7));
            Ok(smallvec![(dmg, 3)])
        }
        (MonsterId::HunterKiller, "TENDERIZING_GOOP_MOVE") => Ok(SmallVec::new()),
        (MonsterId::InfestedPrism, "JAB_MOVE" | "JAB_MOVE_2") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 17, 15));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::InfestedPrism, "WHIRLWIND_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 6, 5));
            Ok(smallvec![(dmg, 3)])
        }
        (MonsterId::InfestedPrism, "PULSATE_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 10, 8));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::KnowledgeDemon, "CURSE_OF_KNOWLEDGE_MOVE") => Ok(SmallVec::new()),
        (MonsterId::KnowledgeDemon, "SLAP_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 18, 17));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::KnowledgeDemon, "KNOWLEDGE_OVERWHELMING_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 9, 8));
            Ok(smallvec![(dmg, 3)])
        }
        (MonsterId::KnowledgeDemon, "PONDER_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 13, 11));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::LouseProgenitor, "WEB_CANNON_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 10, 9));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::LouseProgenitor, "POUNCE_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 16, 14));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::LouseProgenitor, "CURL_AND_GROW_MOVE") => Ok(SmallVec::new()),
        (MonsterId::Myte, "BITE_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 15, 13));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::Myte, "SUCK_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 6, 4));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::Myte, "TOXIC_MOVE" | "INIT_MOVE") => Ok(SmallVec::new()),
        (MonsterId::Ovicopter, "SMASH_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 17, 16));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::Ovicopter, "TENDERIZER_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 8, 7));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::Ovicopter, "LAY_EGGS_MOVE" | "NUTRITIONAL_PASTE_MOVE") => Ok(SmallVec::new()),
        (MonsterId::Rocket, "CALCULATING_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 4, 3));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::Rocket, "PRECISE_SMASH_MOVE" | "PRECISE_SMASH_MOVE_2") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 20, 18));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::Rocket, "ROCKET_CLAW_MOVE") => {
            let base = state.ascension_value(AscensionLevel::DeadlyEnemies, 35, 31);
            let dmg = Dec::from_i32(base + if is_dread(state) { 5 } else { 0 });
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::Rocket, "CHARGE_UP_MOVE" | "RECHARGE_MOVE") => Ok(SmallVec::new()),
        (MonsterId::SlumberingBeetle, "ROLL_OUT_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 18, 16));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::SlumberingBeetle, "SNORE_MOVE" | "SLEEP_CHECK") => Ok(SmallVec::new()),
        (MonsterId::SpinyToad, "SPIKE_EXPLOSION_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 25, 23));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::SpinyToad, "TONGUE_LASH_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 19, 17));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::SpinyToad, "PROTRUDING_SPIKES_MOVE") => Ok(SmallVec::new()),
        (MonsterId::TheInsatiable, "THRASH_MOVE" | "THRASH_MOVE_2") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 9, 8));
            Ok(smallvec![(dmg, 2)])
        }
        (MonsterId::TheInsatiable, "LUNGING_BITE_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 31, 28));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::TheInsatiable, "LIQUIFY_GROUND_MOVE" | "SALIVATE_MOVE") => Ok(SmallVec::new()),
        (MonsterId::TheObscura, "PIERCING_GAZE_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 11, 10));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::TheObscura, "HARDENING_STRIKE_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 7, 6));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::TheObscura, "ILLUSION_MOVE" | "SAIL_MOVE") => Ok(SmallVec::new()),
        (MonsterId::ThievingHopper, "THIEVERY_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 19, 17));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::ThievingHopper, "HAT_TRICK_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 23, 21));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::ThievingHopper, "NAB_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 16, 14));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::ThievingHopper, "FLUTTER_MOVE" | "ESCAPE_MOVE") => Ok(SmallVec::new()),
        (MonsterId::Tunneler, "BITE_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 15, 13));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::Tunneler, "FROM_BELOW_MOVE") => {
            let dmg = Dec::from_i32(state.ascension_value(AscensionLevel::DeadlyEnemies, 28, 24));
            Ok(smallvec![(dmg, 1)])
        }
        (MonsterId::Tunneler, "BURROW_MOVE") => Ok(SmallVec::new()),
        _ => Err(KernelError::unsupported(
            "MonsterIntent",
            format!("{}:{}", mid.model_id_str(), move_id),
        )),
    }
}
