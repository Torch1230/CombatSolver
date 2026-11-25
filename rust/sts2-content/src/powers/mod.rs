pub mod batch_a_m;
pub mod batch_n_z;

use sts2_kernel::commands::{block, card_pile, power};
use sts2_kernel::hooks;
use sts2_kernel::state::CardTempFlags;
use sts2_kernel::{
    BattleState, CardHandle, ContentDispatcher, CreatureHandle, HookArgs, HookResult, KernelError,
    PowerInst,
};
use sts2_types::{
    AfflictionId, CardKeywords, CardStaticFlags, CardType, CombatSide, Dec, HookSlot, PileType,
    PowerId, PowerMeta, PowerType, ValueProp,
};

const _: () = {
    assert!(PowerId::MythicFormPower as usize == 168);
    assert!(PowerId::NecroMasteryPower as usize == 169);
};

#[inline(always)]
const fn is_batch_a_m(id: PowerId) -> bool {
    (id as usize) < 169
}

#[inline(always)]
pub fn dispatch_should_allow_hitting(
    state: &BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    p: PowerInst,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    if let HookArgs::Creature(target) = args {
        let bool_val = match p.id {
            PowerId::AdaptablePower => Some(
                *target != creature
                    || !batch_a_m::get_bool_field(
                        state,
                        creature,
                        power_idx,
                        "AdaptablePower._isReviving",
                        false,
                    ),
            ),
            PowerId::IllusionPower => Some(
                *target != creature
                    || !batch_a_m::get_bool_field(
                        state,
                        creature,
                        power_idx,
                        "IllusionPower._isReviving",
                        false,
                    ),
            ),
            PowerId::ReattachPower => {
                if *target == creature
                    && p.ext_index.is_some()
                    && batch_n_z::get_bool_field(state, &p, "ReattachPower._isReviving", false)
                {
                    Some(false)
                } else {
                    None
                }
            }
            PowerId::DieForYouPower | PowerId::WuppysLoyaltyPower => {
                Some(state.creature(*target).is_some_and(|c| c.is_alive()))
            }
            _ => None,
        };
        if bool_val.is_some() {
            return Ok(HookResult {
                bool_val,
                ..Default::default()
            });
        }
    }
    Ok(HookResult::default())
}

#[inline(always)]
pub fn dispatch_should_creature_removed_after_death(
    creature: CreatureHandle,
    id: PowerId,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    if let HookArgs::Creature(target) = args {
        if matches!(
            id,
            PowerId::AdaptablePower
                | PowerId::DieForYouPower
                | PowerId::IllusionPower
                | PowerId::ReattachPower
                | PowerId::SteamEruptionPower
                | PowerId::WuppysLoyaltyPower
        ) {
            return Ok(HookResult {
                bool_val: Some(*target != creature),
                ..Default::default()
            });
        }
    }
    Ok(HookResult::default())
}

#[inline(always)]
fn dispatch_should_power_removed_on_death(
    creature: CreatureHandle,
    p: PowerInst,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    match p.id {
        PowerId::IllusionPower => {
            if let HookArgs::PowerQuery {
                power_type,
                is_temporary,
                ..
            } = args
            {
                return Ok(HookResult {
                    bool_val: Some(*power_type == PowerType::Debuff && !*is_temporary),
                    ..Default::default()
                });
            }
        }
        PowerId::ReattachPower => {
            if let HookArgs::PowerQuery {
                owner,
                power_handle,
                ..
            } = args
            {
                if *owner == creature {
                    return Ok(HookResult {
                        bool_val: Some(batch_n_z::reattach_should_power_be_removed_on_death(
                            p.handle,
                            *power_handle,
                        )),
                        ..Default::default()
                    });
                }
            }
        }
        _ => {}
    }
    Ok(HookResult::default())
}

#[inline(always)]
fn dispatch_try_modify_energy_cost_early(
    content: &dyn ContentDispatcher,
    state: &BattleState,
    creature: CreatureHandle,
    p: PowerInst,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    if let HookArgs::CardCost { card, cost } = args {
        if creature == state.player.creature_handle {
            match p.id {
                PowerId::BorrowedTimePower => {
                    return Ok(HookResult {
                        dec_val: Some(cost.checked_add(Dec::from_i32(p.amount))?),
                        modified: true,
                        ..Default::default()
                    });
                }
                PowerId::CuriousPower => {
                    if *cost > Dec::ZERO
                        && batch_a_m::is_card_type(state, content, *card, CardType::Power)?
                    {
                        let next = cost.checked_sub(Dec::from_i32(p.amount))?.max(Dec::ZERO);
                        return Ok(HookResult {
                            dec_val: Some(next),
                            modified: true,
                            ..Default::default()
                        });
                    }
                }
                PowerId::TangledPower => {
                    if let Some(c) = state.card(*card) {
                        let is_x = c.temp_flags.contains(CardTempFlags::COSTS_X_ENERGY)
                            || state
                                .fast_card_meta(content, c.id)?
                                .flags
                                .contains(CardStaticFlags::IS_X_ENERGY_COST);
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
                _ => {}
            }
        }
    }
    Ok(HookResult::default())
}

#[inline(always)]
fn dispatch_try_modify_energy_cost_late(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    p: PowerInst,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    if let HookArgs::CardCost { card, .. } = args {
        match p.id {
            PowerId::CorruptionPower => {
                if creature == state.player.creature_handle
                    && batch_a_m::is_card_type(state, content, *card, CardType::Skill)?
                {
                    return Ok(HookResult {
                        dec_val: Some(Dec::ZERO),
                        modified: true,
                        ..Default::default()
                    });
                }
            }
            PowerId::EnsemblePower => {
                if creature != state.player.creature_handle
                    && state
                        .card(*card)
                        .is_some_and(|c| matches!(c.pile, PileType::Hand | PileType::Play))
                {
                    return Ok(HookResult {
                        dec_val: Some(Dec::ZERO),
                        modified: true,
                        ..Default::default()
                    });
                }
            }
            PowerId::FreeAttackPower
            | PowerId::FreePowerPower
            | PowerId::FreeSkillPower => {
                if creature == state.player.creature_handle
                    && state
                        .card(*card)
                        .is_some_and(|c| matches!(c.pile, PileType::Hand | PileType::Play))
                {
                    let type_ok = match p.id {
                        PowerId::FreeAttackPower => {
                            batch_a_m::is_card_type(state, content, *card, CardType::Attack)?
                        }
                        PowerId::FreePowerPower => {
                            batch_a_m::is_card_type(state, content, *card, CardType::Power)?
                        }
                        PowerId::FreeSkillPower => {
                            batch_a_m::is_card_type(state, content, *card, CardType::Skill)?
                        }
                        _ => true,
                    };
                    if type_ok {
                        return Ok(HookResult {
                            dec_val: Some(Dec::ZERO),
                            modified: true,
                            ..Default::default()
                        });
                    }
                }
            }
            PowerId::VeilpiercerPower => {
                if creature == state.player.creature_handle {
                    if let Some(ci) = state.card(*card) {
                        if matches!(ci.pile, PileType::Hand | PileType::Play) {
                            let is_eth = ci.effective_keywords_base().contains(CardKeywords::ETHEREAL)
                                || ((state.hook_cache.dirty_mask != 0
                                    || state.hook_cache.cached_tables.is_none()
                                    || state
                                        .hook_cache
                                        .masks
                                        .combat_combined
                                        .contains(HookSlot::TryModifyKeywordsInCombat))
                                    && card_pile::card_has_keyword(
                                        state,
                                        content,
                                        *card,
                                        CardKeywords::ETHEREAL,
                                    )?);
                            if is_eth {
                                return Ok(HookResult {
                                    dec_val: Some(Dec::ZERO),
                                    modified: true,
                                    ..Default::default()
                                });
                            }
                        }
                    }
                }
            }
            PowerId::VoidFormPower => {
                if creature == state.player.creature_handle
                    && state
                        .card(*card)
                        .is_some_and(|c| matches!(c.pile, PileType::Hand | PileType::Play))
                {
                    let played =
                        batch_n_z::get_int_field(state, &p, "VoidFormPower._cardsPlayedThisTurn", 0);
                    if played < p.amount {
                        return Ok(HookResult {
                            dec_val: Some(Dec::ZERO),
                            modified: true,
                            ..Default::default()
                        });
                    }
                }
            }
            _ => {}
        }
    }
    Ok(HookResult::default())
}

pub fn power_meta(id: PowerId) -> Result<&'static PowerMeta, KernelError> {
    if is_batch_a_m(id) {
        batch_a_m::power_meta(id)
    } else {
        batch_n_z::power_meta(id)
    }
}

#[inline(always)]
fn card_is_ethereal_fast(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
) -> Result<bool, KernelError> {
    let Some(ci) = state.card(card) else {
        return Ok(false);
    };
    if ci.effective_keywords_base().contains(CardKeywords::ETHEREAL) {
        return Ok(true);
    }
    if state.hook_cache.dirty_mask == 0
        && state.hook_cache.cached_tables.is_some()
        && !state
            .hook_cache
            .masks
            .combat_combined
            .contains(HookSlot::TryModifyKeywordsInCombat)
    {
        return Ok(false);
    }
    card_pile::card_has_keyword(state, content, card, CardKeywords::ETHEREAL)
}

#[inline]
#[allow(clippy::too_many_arguments)]
pub fn dispatch_hook_resolved(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    p: PowerInst,
    owner_side: CombatSide,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    match slot {
        HookSlot::ShouldAllowHitting => {
            return dispatch_should_allow_hitting(state, creature, power_idx, p, args);
        }
        HookSlot::ShouldCreatureBeRemovedFromCombatAfterDeath => {
            return dispatch_should_creature_removed_after_death(creature, p.id, args);
        }
        HookSlot::ShouldPowerBeRemovedOnDeath => {
            return dispatch_should_power_removed_on_death(creature, p, args);
        }
        HookSlot::TryModifyEnergyCostInCombat => {
            return dispatch_try_modify_energy_cost_early(content, state, creature, p, args);
        }
        HookSlot::TryModifyEnergyCostInCombatLate => {
            return dispatch_try_modify_energy_cost_late(content, state, creature, p, args);
        }
        HookSlot::BeforeCardPlayed if p.id == PowerId::SpiritOfAshPower => {
            if creature == state.player.creature_handle {
                if let HookArgs::CardPlay(cp) = args {
                    if card_is_ethereal_fast(state, content, cp.card)? {
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
            return Ok(HookResult::default());
        }
        HookSlot::AfterCardPlayed if p.id == PowerId::VitalSparkPower => {
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
            return Ok(HookResult::default());
        }
        HookSlot::AfterCardDrawn if p.id == PowerId::PagestormPower => {
            if creature == state.player.creature_handle {
                if let HookArgs::CardDrawn { card, .. } = args {
                    if card_is_ethereal_fast(state, content, *card)? {
                        let _ = card_pile::draw(state, content, p.amount, false)?;
                    }
                }
            }
            return Ok(HookResult::default());
        }
        HookSlot::ModifyDamageAdditive
            if p.id == PowerId::StrengthPower || p.id == PowerId::VigorPower =>
        {
            if let HookArgs::ModifyDamage { dealer, props, .. } = args {
                if *dealer == Some(creature) && props.is_powered_attack() {
                    return Ok(HookResult {
                        dec_val: Some(Dec::from_i32(p.amount)),
                        ..Default::default()
                    });
                }
            }
            return Ok(HookResult::default());
        }
        HookSlot::ModifyBlockAdditive if p.id == PowerId::DexterityPower => {
            if let HookArgs::ModifyBlock {
                target,
                props,
                card_source,
                ..
            } = args
            {
                let owned_source = if card_source.is_some() {
                    creature == state.player.creature_handle
                } else {
                    *target == creature
                };
                if owned_source && !props.contains(ValueProp::UNPOWERED) {
                    return Ok(HookResult {
                        dec_val: Some(Dec::from_i32(p.amount)),
                        ..Default::default()
                    });
                }
            }
            return Ok(HookResult::default());
        }
        HookSlot::ModifyDamageMultiplicative if p.id == PowerId::VulnerablePower => {
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
            return Ok(HookResult::default());
        }
        HookSlot::ModifyDamageMultiplicative if p.id == PowerId::WeakPower => {
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
            return Ok(HookResult::default());
        }
        HookSlot::ModifyBlockMultiplicative if p.id == PowerId::FrailPower => {
            if let HookArgs::ModifyBlock { target, props, .. } = args {
                if *target == creature && props.is_powered_block() {
                    return Ok(HookResult {
                        dec_val: Some(Dec::THREE_QUARTERS),
                        ..Default::default()
                    });
                }
            }
            return Ok(HookResult::default());
        }
        HookSlot::AfterSideTurnEnd
            if matches!(
                p.id,
                PowerId::VulnerablePower | PowerId::WeakPower | PowerId::FrailPower
            ) =>
        {
            if let HookArgs::SideTurn { side, .. } = args {
                if *side == CombatSide::Enemy {
                    #[cfg(debug_assertions)]
                    if let Some(c) = state.creature(creature) {
                        debug_assert_eq!(c.powers.get(power_idx).map(|pw| pw.handle), Some(p.handle));
                    }
                    power::tick_down_duration(state, content, creature, power_idx)?;
                }
            }
            return Ok(HookResult::default());
        }
        _ => {}
    }
    if is_batch_a_m(p.id) {
        batch_a_m::dispatch_hook(content, state, creature, power_idx, p, owner_side, slot, args)
    } else {
        batch_n_z::dispatch_hook(content, state, creature, power_idx, p, owner_side, slot, args)
    }
}

pub fn dispatch_hook(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    let (power_idx, p, owner_side) = state
        .creature(creature)
        .and_then(|c| {
            c.powers
                .iter()
                .position(|pw| pw.handle == power_handle)
                .map(|idx| (idx, c.powers[idx], c.side))
        })
        .ok_or_else(|| KernelError::InvalidState("missing power".to_owned()))?;
    if is_batch_a_m(p.id) {
        batch_a_m::dispatch_hook(content, state, creature, power_idx, p, owner_side, slot, args)
    } else {
        batch_n_z::dispatch_hook(content, state, creature, power_idx, p, owner_side, slot, args)
    }
}

pub fn dispatch_detached_hook(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power: PowerInst,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    if is_batch_a_m(power.id) {
        batch_a_m::dispatch_detached_hook(content, state, creature, power, slot, args)
    } else {
        batch_n_z::dispatch_detached_hook(content, state, creature, power, slot, args)
    }
}

pub fn resume_hook_choice(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    slot: HookSlot,
    selected: &[CardHandle],
) -> Result<HookResult, KernelError> {
    let (power_idx, id) = state
        .creature(creature)
        .and_then(|c| {
            c.powers
                .iter()
                .position(|p| p.handle == power_handle)
                .map(|idx| (idx, c.powers[idx].id))
        })
        .ok_or_else(|| {
            KernelError::InvalidState(format!(
                "missing power handle {power_handle} on creature {:?}",
                creature
            ))
        })?;
    if is_batch_a_m(id) {
        batch_a_m::resume_hook_choice(content, state, creature, power_idx, slot, selected)
    } else {
        batch_n_z::resume_hook_choice(content, state, creature, power_idx, slot, selected)
    }
}
