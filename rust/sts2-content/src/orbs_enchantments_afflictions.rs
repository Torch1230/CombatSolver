use smallvec::SmallVec;
use sts2_kernel::commands::{autoplay, block, card as card_cmd, damage, gold, orb, power};
use sts2_kernel::content_dispatch::CardPlayContext;
use sts2_kernel::{
    BattleState, CardHandle, ContentDispatcher, CreatureHandle, FieldValue, HookArgs, HookResult,
    KernelError, LocalEnergyModifier,
};
use sts2_types::{
    AfflictionId, Dec, EnchantmentId, EnchantmentStatus, HookMask, HookSlot,
    LocalEnergyModifierKind, OrbId, OrbMeta, PileType, PowerId, RunRngType, TargetType, ValueProp,
};

static DARK_META: OrbMeta = OrbMeta {
    orb_id: OrbId::DarkOrb,
    hook_mask: HookMask::EMPTY,
};

static FROST_META: OrbMeta = OrbMeta {
    orb_id: OrbId::FrostOrb,
    hook_mask: HookMask::EMPTY,
};

static GLASS_META: OrbMeta = OrbMeta {
    orb_id: OrbId::GlassOrb,
    hook_mask: HookMask::EMPTY,
};

static LIGHTNING_META: OrbMeta = OrbMeta {
    orb_id: OrbId::LightningOrb,
    hook_mask: HookMask::EMPTY,
};

static PLASMA_META: OrbMeta = OrbMeta {
    orb_id: OrbId::PlasmaOrb,
    hook_mask: HookMask::EMPTY,
};

pub fn orb_meta(id: OrbId) -> Result<&'static OrbMeta, KernelError> {
    match id {
        OrbId::DarkOrb => Ok(&DARK_META),
        OrbId::FrostOrb => Ok(&FROST_META),
        OrbId::GlassOrb => Ok(&GLASS_META),
        OrbId::LightningOrb => Ok(&LIGHTNING_META),
        OrbId::PlasmaOrb => Ok(&PLASMA_META),
    }
}

pub fn orb_before_turn_end(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    orb_idx: usize,
) -> Result<(), KernelError> {
    let id = state
        .player
        .orbs
        .get(orb_idx)
        .ok_or_else(|| KernelError::InvalidState("missing orb".to_owned()))?
        .id;
    match id {
        OrbId::DarkOrb | OrbId::FrostOrb | OrbId::GlassOrb | OrbId::LightningOrb => {
            orb::passive(state, content, orb_idx, None, true)
        }
        OrbId::PlasmaOrb => Ok(()),
    }
}

pub fn orb_after_turn_start(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    orb_idx: usize,
) -> Result<(), KernelError> {
    let id = state
        .player
        .orbs
        .get(orb_idx)
        .ok_or_else(|| KernelError::InvalidState("missing orb".to_owned()))?
        .id;
    match id {
        OrbId::PlasmaOrb => orb::passive(state, content, orb_idx, None, true),
        OrbId::DarkOrb | OrbId::FrostOrb | OrbId::GlassOrb | OrbId::LightningOrb => Ok(()),
    }
}

pub fn orb_passive(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    orb_idx: usize,
    target: Option<CreatureHandle>,
) -> Result<SmallVec<[CreatureHandle; 4]>, KernelError> {
    let id = state
        .player
        .orbs
        .get(orb_idx)
        .ok_or_else(|| KernelError::InvalidState("missing orb".to_owned()))?
        .id;
    match id {
        OrbId::LightningOrb => {
            let val = sts2_kernel::hooks::modify_orb_value(state, content, id, Dec::from_i32(3))?;
            apply_lightning_damage(content, state, val, target)
        }
        OrbId::FrostOrb => {
            if target.is_some() {
                return Err(KernelError::InvalidAction(
                    "Frost orbs cannot target creatures.".to_owned(),
                ));
            }
            let val = sts2_kernel::hooks::modify_orb_value(state, content, id, Dec::TWO)?;
            let owner = state.player.creature_handle;
            block::gain_block(
                state,
                content,
                owner,
                val,
                ValueProp::UNPOWERED,
                None,
                None,
            )?;
            Ok(SmallVec::new())
        }
        OrbId::DarkOrb => {
            if target.is_some() {
                return Err(KernelError::InvalidAction(
                    "Dark orbs cannot target creatures.".to_owned(),
                ));
            }
            let val = sts2_kernel::hooks::modify_orb_value(state, content, id, Dec::from_i32(6))?;
            let orb_inst = state
                .player
                .orbs
                .get_mut(orb_idx)
                .ok_or_else(|| KernelError::InvalidState("missing orb".to_owned()))?;
            let current = orb_inst.raw_evoke_value.unwrap_or(Dec::from_i32(6));
            orb_inst.raw_evoke_value = Some(current.checked_add(val)?);
            Ok(SmallVec::new())
        }
        OrbId::PlasmaOrb => {
            if target.is_some() {
                return Err(KernelError::InvalidAction(
                    "Plasma orbs cannot target creatures.".to_owned(),
                ));
            }
            gold::gain_energy(state, Dec::ONE)?;
            Ok(SmallVec::new())
        }
        OrbId::GlassOrb => {
            let targets = collect_hittable_enemies(state, content)?;
            let raw_passive = state
                .player
                .orbs
                .get(orb_idx)
                .ok_or_else(|| KernelError::InvalidState("missing orb".to_owned()))?
                .raw_passive_value
                .unwrap_or(Dec::from_i32(4));
            let val = sts2_kernel::hooks::modify_orb_value(state, content, id, raw_passive)?;
            if val <= Dec::ZERO {
                return Ok(SmallVec::new());
            }
            let next_raw = raw_passive.checked_sub(Dec::ONE)?.max(Dec::ZERO);
            if let Some(orb_inst) = state.player.orbs.get_mut(orb_idx) {
                orb_inst.raw_passive_value = Some(next_raw);
            }
            let owner = state.player.creature_handle;
            damage::damage_targets(
                state,
                content,
                &targets,
                val,
                ValueProp::UNPOWERED,
                Some(owner),
                None,
                None,
            )?;
            Ok(targets.into_iter().collect())
        }
    }
}

pub fn orb_evoke(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    orb_idx: usize,
) -> Result<SmallVec<[CreatureHandle; 4]>, KernelError> {
    let id = state
        .player
        .orbs
        .get(orb_idx)
        .ok_or_else(|| KernelError::InvalidState("missing orb".to_owned()))?
        .id;
    match id {
        OrbId::LightningOrb => {
            let val = sts2_kernel::hooks::modify_orb_value(state, content, id, Dec::from_i32(8))?;
            apply_lightning_damage(content, state, val, None)
        }
        OrbId::FrostOrb => {
            let val = sts2_kernel::hooks::modify_orb_value(state, content, id, Dec::from_i32(5))?;
            let owner = state.player.creature_handle;
            block::gain_block(
                state,
                content,
                owner,
                val,
                ValueProp::UNPOWERED,
                None,
                None,
            )?;
            Ok(smallvec::smallvec![owner])
        }
        OrbId::DarkOrb => {
            let candidates = collect_hittable_enemies(state, content)?;
            let Some(weakest) = candidates
                .iter()
                .copied()
                .min_by_key(|&eh| state.creature(eh).map_or(i32::MAX, |c| c.current_hp))
            else {
                return Ok(SmallVec::new());
            };
            let evoke_val = state
                .player
                .orbs
                .get(orb_idx)
                .ok_or_else(|| KernelError::InvalidState("missing orb".to_owned()))?
                .raw_evoke_value
                .unwrap_or(Dec::from_i32(6));
            let owner = state.player.creature_handle;
            damage::damage_targets(
                state,
                content,
                &[weakest],
                evoke_val,
                ValueProp::UNPOWERED,
                Some(owner),
                None,
                None,
            )?;
            Ok(smallvec::smallvec![weakest])
        }
        OrbId::PlasmaOrb => {
            gold::gain_energy(state, Dec::TWO)?;
            Ok(smallvec::smallvec![state.player.creature_handle])
        }
        OrbId::GlassOrb => {
            let targets = collect_hittable_enemies(state, content)?;
            let raw_passive = state
                .player
                .orbs
                .get(orb_idx)
                .ok_or_else(|| KernelError::InvalidState("missing orb".to_owned()))?
                .raw_passive_value
                .unwrap_or(Dec::from_i32(4));
            let passive_val = sts2_kernel::hooks::modify_orb_value(state, content, id, raw_passive)?;
            let evoke_val = passive_val.checked_mul(Dec::TWO)?;
            if evoke_val <= Dec::ZERO {
                return Ok(SmallVec::new());
            }
            let owner = state.player.creature_handle;
            damage::damage_targets(
                state,
                content,
                &targets,
                evoke_val,
                ValueProp::UNPOWERED,
                Some(owner),
                None,
                None,
            )?;
            Ok(targets.into_iter().collect())
        }
    }
}

fn collect_hittable_enemies(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<SmallVec<[CreatureHandle; 8]>, KernelError> {
    let mut candidates: SmallVec<[CreatureHandle; 8]> = SmallVec::new();
    let enemies: SmallVec<[CreatureHandle; 8]> = SmallVec::from_slice(&state.enemy_handles);
    for eh in enemies {
        if state.creature(eh).is_some_and(|c| c.is_alive())
            && sts2_kernel::hooks::should_allow_hitting(state, content, eh)?
        {
            candidates.push(eh);
        }
    }
    Ok(candidates)
}

fn apply_lightning_damage(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    value: Dec,
    target: Option<CreatureHandle>,
) -> Result<SmallVec<[CreatureHandle; 4]>, KernelError> {
    let candidates = collect_hittable_enemies(state, content)?;
    if candidates.is_empty() {
        return Ok(SmallVec::new());
    }
    let selected = match target {
        Some(t) => t,
        None => *state
            .run_rngs
            .get_mut(RunRngType::CombatTargets)
            .next_item(&candidates)
            .expect("non-empty candidates"),
    };
    let owner = state.player.creature_handle;
    damage::damage_targets(
        state,
        content,
        &[selected],
        value,
        ValueProp::UNPOWERED,
        Some(owner),
        None,
        None,
    )?;
    Ok(smallvec::smallvec![selected])
}

pub fn dispatch_orb_hook(
    _content: &dyn ContentDispatcher,
    state: &mut BattleState,
    orb_idx: usize,
    _slot: HookSlot,
    _args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    let id = state
        .player
        .orbs
        .get(orb_idx)
        .ok_or_else(|| KernelError::InvalidState("missing orb".to_owned()))?
        .id;
    match id {
        OrbId::DarkOrb
        | OrbId::FrostOrb
        | OrbId::GlassOrb
        | OrbId::LightningOrb
        | OrbId::PlasmaOrb => Ok(HookResult::default()),
    }
}

pub fn enchantment_hook_mask(id: EnchantmentId) -> Result<HookMask, KernelError> {
    match id {
        EnchantmentId::Adroit
        | EnchantmentId::Clone
        | EnchantmentId::Corrupted
        | EnchantmentId::Inky
        | EnchantmentId::Instinct
        | EnchantmentId::Momentum
        | EnchantmentId::PerfectFit
        | EnchantmentId::RoyallyApproved
        | EnchantmentId::Sharp
        | EnchantmentId::SoulsPower
        | EnchantmentId::Sown
        | EnchantmentId::Spiral
        | EnchantmentId::Steady
        | EnchantmentId::Swift
        | EnchantmentId::TezcatarasEmber => Ok(HookMask::EMPTY),
        EnchantmentId::Glam | EnchantmentId::Vigorous => {
            Ok(HookMask::EMPTY.with(HookSlot::AfterCardPlayed))
        }
        EnchantmentId::Goopy | EnchantmentId::Nimble => {
            Ok(HookMask::EMPTY.with(HookSlot::ModifyBlockAdditive))
        }
        EnchantmentId::Imbued => Ok(HookMask::EMPTY.with(HookSlot::AfterAutoPrePlayPhaseEntered)),
        EnchantmentId::Slither => Ok(HookMask::EMPTY.with(HookSlot::AfterCardDrawn)),
    }
}

pub fn enchantment_should_start_at_bottom(id: EnchantmentId) -> Result<bool, KernelError> {
    Ok(matches!(id, EnchantmentId::Imbued))
}

pub fn enchantment_modify_shuffle_order(
    state: &BattleState,
    card: CardHandle,
    cards: &mut [CardHandle],
    is_initial_shuffle: bool,
) -> Result<(), KernelError> {
    let id = state
        .card(card)
        .and_then(|c| state.card_ext(c.ext_index))
        .and_then(|e| e.enchantment.as_ref().map(|x| x.id))
        .ok_or_else(|| KernelError::InvalidState("missing enchantment".to_owned()))?;
    match id {
        EnchantmentId::PerfectFit => {
            if !is_initial_shuffle {
                if let Some(pos) = cards.iter().position(|&h| h == card) {
                    cards[..=pos].rotate_right(1);
                }
            }
            Ok(())
        }
        EnchantmentId::Adroit
        | EnchantmentId::Clone
        | EnchantmentId::Corrupted
        | EnchantmentId::Glam
        | EnchantmentId::Goopy
        | EnchantmentId::Imbued
        | EnchantmentId::Inky
        | EnchantmentId::Instinct
        | EnchantmentId::Momentum
        | EnchantmentId::Nimble
        | EnchantmentId::RoyallyApproved
        | EnchantmentId::Sharp
        | EnchantmentId::Slither
        | EnchantmentId::SoulsPower
        | EnchantmentId::Sown
        | EnchantmentId::Spiral
        | EnchantmentId::Steady
        | EnchantmentId::Swift
        | EnchantmentId::TezcatarasEmber
        | EnchantmentId::Vigorous => Ok(()),
    }
}

pub fn enchantment_play_count(
    state: &BattleState,
    card: CardHandle,
    play_count: i32,
) -> Result<i32, KernelError> {
    let ench = state
        .card(card)
        .and_then(|c| state.card_ext(c.ext_index))
        .and_then(|e| e.enchantment.as_ref())
        .ok_or_else(|| KernelError::InvalidState("missing enchantment".to_owned()))?;
    match ench.id {
        EnchantmentId::Glam => Ok(if ench.status == EnchantmentStatus::Normal {
            play_count + 1
        } else {
            play_count
        }),
        EnchantmentId::Spiral => Ok(play_count + 1),
        EnchantmentId::Adroit
        | EnchantmentId::Clone
        | EnchantmentId::Corrupted
        | EnchantmentId::Goopy
        | EnchantmentId::Imbued
        | EnchantmentId::Inky
        | EnchantmentId::Instinct
        | EnchantmentId::Momentum
        | EnchantmentId::Nimble
        | EnchantmentId::PerfectFit
        | EnchantmentId::RoyallyApproved
        | EnchantmentId::Sharp
        | EnchantmentId::Slither
        | EnchantmentId::SoulsPower
        | EnchantmentId::Sown
        | EnchantmentId::Steady
        | EnchantmentId::Swift
        | EnchantmentId::TezcatarasEmber
        | EnchantmentId::Vigorous => Ok(play_count),
    }
}

pub fn enchantment_damage_additive(
    state: &BattleState,
    card: CardHandle,
    _amount: Dec,
    props: ValueProp,
) -> Result<Dec, KernelError> {
    let ench = state
        .card(card)
        .and_then(|c| state.card_ext(c.ext_index))
        .and_then(|e| e.enchantment.as_ref())
        .ok_or_else(|| KernelError::InvalidState("missing enchantment".to_owned()))?;
    if !props.is_powered_attack() {
        return Ok(Dec::ZERO);
    }
    match ench.id {
        EnchantmentId::Momentum => {
            let extra = match ench.private_fields.get("Momentum._extraDamage") {
                Some(FieldValue::Int(v)) => *v,
                _ => 0,
            };
            Ok(Dec::from_i32(extra))
        }
        EnchantmentId::Sharp => Ok(Dec::from_i32(ench.amount)),
        EnchantmentId::TezcatarasEmber => Ok(Dec::from_i32(3)),
        EnchantmentId::Vigorous => Ok(if ench.status == EnchantmentStatus::Normal {
            Dec::from_i32(ench.amount)
        } else {
            Dec::ZERO
        }),
        EnchantmentId::Adroit
        | EnchantmentId::Clone
        | EnchantmentId::Corrupted
        | EnchantmentId::Glam
        | EnchantmentId::Goopy
        | EnchantmentId::Imbued
        | EnchantmentId::Inky
        | EnchantmentId::Instinct
        | EnchantmentId::Nimble
        | EnchantmentId::PerfectFit
        | EnchantmentId::RoyallyApproved
        | EnchantmentId::Slither
        | EnchantmentId::SoulsPower
        | EnchantmentId::Sown
        | EnchantmentId::Spiral
        | EnchantmentId::Steady
        | EnchantmentId::Swift => Ok(Dec::ZERO),
    }
}

pub fn enchantment_damage_multiplicative(
    state: &BattleState,
    card: CardHandle,
    _amount: Dec,
    props: ValueProp,
) -> Result<Dec, KernelError> {
    let ench = state
        .card(card)
        .and_then(|c| state.card_ext(c.ext_index))
        .and_then(|e| e.enchantment.as_ref())
        .ok_or_else(|| KernelError::InvalidState("missing enchantment".to_owned()))?;
    if !props.is_powered_attack() {
        return Ok(Dec::ONE);
    }
    match ench.id {
        EnchantmentId::Corrupted => Ok(Dec::ONE_POINT_FIVE),
        EnchantmentId::Instinct => Ok(Dec::TWO),
        EnchantmentId::Adroit
        | EnchantmentId::Clone
        | EnchantmentId::Glam
        | EnchantmentId::Goopy
        | EnchantmentId::Imbued
        | EnchantmentId::Inky
        | EnchantmentId::Momentum
        | EnchantmentId::Nimble
        | EnchantmentId::PerfectFit
        | EnchantmentId::RoyallyApproved
        | EnchantmentId::Sharp
        | EnchantmentId::Slither
        | EnchantmentId::SoulsPower
        | EnchantmentId::Sown
        | EnchantmentId::Spiral
        | EnchantmentId::Steady
        | EnchantmentId::Swift
        | EnchantmentId::TezcatarasEmber
        | EnchantmentId::Vigorous => Ok(Dec::ONE),
    }
}

pub fn enchantment_on_play(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    card: CardHandle,
    ctx: &CardPlayContext,
) -> Result<(), KernelError> {
    let (ench_id, amount, status) = state
        .card(card)
        .and_then(|c| state.card_ext(c.ext_index))
        .and_then(|e| e.enchantment.as_ref())
        .map(|ench| (ench.id, ench.amount, ench.status))
        .ok_or_else(|| KernelError::InvalidState("missing enchantment".to_owned()))?;

    match ench_id {
        EnchantmentId::Adroit => {
            let owner = state.player.creature_handle;
            block::gain_block(
                state,
                content,
                owner,
                Dec::from_i32(amount),
                ValueProp::MOVE,
                Some(card),
                Some(*ctx),
            )?;
            Ok(())
        }
        EnchantmentId::Corrupted => {
            let owner = state.player.creature_handle;
            damage::damage_targets(
                state,
                content,
                &[owner],
                Dec::TWO,
                ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED | ValueProp::MOVE,
                None,
                Some(card),
                Some(*ctx),
            )?;
            Ok(())
        }
        EnchantmentId::Goopy => {
            if ctx.card != card {
                return Ok(());
            }
            if let Some(ext) = state.card_ext_mut(card) {
                if let Some(ench) = ext.enchantment.as_mut() {
                    ench.amount += 1;
                }
            }
            if let Some(deck_h) = state.card(card).and_then(|c| c.deck_card_handle.get()) {
                if let Some(deck_ext) = state.card_ext_mut(deck_h) {
                    if let Some(deck_ench) = deck_ext.enchantment.as_mut() {
                        if deck_ench.id == EnchantmentId::Goopy {
                            deck_ench.amount += 1;
                            state
                                .semantic_history
                                .record_growth(sts2_kernel::GrowthSource::Goopy);
                        }
                    }
                }
            }
            Ok(())
        }
        EnchantmentId::Inky => {
            if ctx.card != card {
                return Ok(());
            }
            let card_id = state
                .card(card)
                .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
                .id;
            let meta = content
                .card_meta(card_id)
                .unwrap_or_else(|_| sts2_types::generated_card_meta(card_id));
            let owner = state.player.creature_handle;
            let resolved_target = content
                .card_resolved_target_type(state, card)
                .unwrap_or(meta.target_type);
            if resolved_target == TargetType::AllEnemies {
                let targets = collect_hittable_enemies(state, content)?;
                for target in targets {
                    power::apply(
                        state,
                        content,
                        PowerId::WeakPower,
                        target,
                        Dec::ONE,
                        Some(owner),
                        Some(card),
                    )?;
                }
            } else if let Some(target) = ctx.target {
                power::apply(
                    state,
                    content,
                    PowerId::WeakPower,
                    target,
                    Dec::ONE,
                    Some(owner),
                    Some(card),
                )?;
            }
            Ok(())
        }
        EnchantmentId::Momentum => {
            if let Some(ext) = state.card_ext_mut(card) {
                if let Some(ench) = ext.enchantment.as_mut() {
                    let prev = match ench.private_fields.get("Momentum._extraDamage") {
                        Some(FieldValue::Int(v)) => *v,
                        _ => 0,
                    };
                    ench.private_fields.insert(
                        "Momentum._extraDamage",
                        FieldValue::Int(prev + amount),
                    );
                }
            }
            Ok(())
        }
        EnchantmentId::Sown => {
            if status != EnchantmentStatus::Normal {
                return Ok(());
            }
            if let Some(ext) = state.card_ext_mut(card) {
                if let Some(ench) = ext.enchantment.as_mut() {
                    ench.status = EnchantmentStatus::Disabled;
                }
            }
            gold::gain_energy(state, Dec::from_i32(amount))
        }
        EnchantmentId::Swift => {
            if status != EnchantmentStatus::Normal {
                return Ok(());
            }
            if let Some(ext) = state.card_ext_mut(card) {
                if let Some(ench) = ext.enchantment.as_mut() {
                    ench.status = EnchantmentStatus::Disabled;
                }
            }
            let _ = sts2_kernel::commands::card_pile::draw(state, content, amount, false)?;
            Ok(())
        }
        EnchantmentId::Clone
        | EnchantmentId::Glam
        | EnchantmentId::Imbued
        | EnchantmentId::Instinct
        | EnchantmentId::Nimble
        | EnchantmentId::PerfectFit
        | EnchantmentId::RoyallyApproved
        | EnchantmentId::Sharp
        | EnchantmentId::Slither
        | EnchantmentId::SoulsPower
        | EnchantmentId::Spiral
        | EnchantmentId::Steady
        | EnchantmentId::TezcatarasEmber
        | EnchantmentId::Vigorous => Ok(()),
    }
}

pub fn dispatch_enchantment_hook(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    card: CardHandle,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    let ench_snapshot = state
        .card(card)
        .and_then(|c| state.card_ext(c.ext_index))
        .and_then(|e| e.enchantment.as_ref())
        .map(|ench| (ench.id, ench.amount))
        .ok_or_else(|| KernelError::InvalidState("missing enchantment".to_owned()))?;

    let (ench_id, amount) = ench_snapshot;
    match ench_id {
        EnchantmentId::Glam | EnchantmentId::Vigorous => {
            if slot == HookSlot::AfterCardPlayed {
                if let HookArgs::CardPlay(cp) = args {
                    if cp.card == card {
                        if let Some(ext) = state.card_ext_mut(card) {
                            if let Some(ench) = ext.enchantment.as_mut() {
                                ench.status = EnchantmentStatus::Disabled;
                            }
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        EnchantmentId::Goopy => {
            if slot == HookSlot::ModifyBlockAdditive {
                if let HookArgs::ModifyBlock { card_source, .. } = args {
                    if *card_source == Some(card) {
                        return Ok(HookResult {
                            dec_val: Some(Dec::from_i32(amount - 1)),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }
        EnchantmentId::Imbued => {
            if slot == HookSlot::AfterAutoPrePlayPhaseEntered && state.player.turn_number <= 1 {
                let _ = autoplay::from_cards(state, content, &[card], None, false)?;
            }
            Ok(HookResult::default())
        }
        EnchantmentId::Nimble => {
            if slot == HookSlot::ModifyBlockAdditive {
                if let HookArgs::ModifyBlock { card_source, .. } = args {
                    if *card_source == Some(card) {
                        return Ok(HookResult {
                            dec_val: Some(Dec::from_i32(amount)),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }
        EnchantmentId::Slither => {
            if slot == HookSlot::AfterCardDrawn {
                if let HookArgs::CardDrawn {
                    card: drawn_card, ..
                } = args
                {
                    if *drawn_card == card
                        && state.card(card).is_some_and(|c| c.pile == PileType::Hand)
                    {
                        let cost = state
                            .run_rngs
                            .get_mut(RunRngType::CombatEnergyCosts)
                            .next_int(4)?;
                        if let Some(c) = state.card_mut(card) {
                            c.temp_cost_override_this_combat =
                                sts2_kernel::OptI16::some_clamped(cost);
                        }
                        if let Some(ext) = state.card_ext_mut(card) {
                            ext.push_local_energy_modifier(LocalEnergyModifier {
                                kind: LocalEnergyModifierKind::SetThisCombat,
                                amount: cost,
                                is_reduce_only: false,
                            });
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        EnchantmentId::Adroit
        | EnchantmentId::Clone
        | EnchantmentId::Corrupted
        | EnchantmentId::Inky
        | EnchantmentId::Instinct
        | EnchantmentId::Momentum
        | EnchantmentId::PerfectFit
        | EnchantmentId::RoyallyApproved
        | EnchantmentId::Sharp
        | EnchantmentId::SoulsPower
        | EnchantmentId::Sown
        | EnchantmentId::Spiral
        | EnchantmentId::Steady
        | EnchantmentId::Swift
        | EnchantmentId::TezcatarasEmber => Ok(HookResult::default()),
    }
}

pub fn affliction_hook_mask(id: AfflictionId) -> Result<HookMask, KernelError> {
    match id {
        AfflictionId::Hexed => Ok(HookMask::EMPTY.with(HookSlot::AfterCardEnteredCombat)),
        AfflictionId::Bound
        | AfflictionId::Entangled
        | AfflictionId::Galvanized
        | AfflictionId::Ringing
        | AfflictionId::Smog
        | AfflictionId::Tainted => Ok(HookMask::EMPTY),
    }
}

pub fn affliction_on_play(
    _content: &dyn ContentDispatcher,
    state: &mut BattleState,
    card: CardHandle,
    _ctx: &CardPlayContext,
) -> Result<(), KernelError> {
    let aff = state
        .card(card)
        .and_then(|c| c.affliction)
        .ok_or_else(|| KernelError::InvalidState("missing affliction".to_owned()))?;
    match aff.id {
        AfflictionId::Bound
        | AfflictionId::Entangled
        | AfflictionId::Galvanized
        | AfflictionId::Hexed
        | AfflictionId::Ringing
        | AfflictionId::Smog
        | AfflictionId::Tainted => Ok(()),
    }
}

pub fn dispatch_affliction_hook(
    _content: &dyn ContentDispatcher,
    state: &mut BattleState,
    card: CardHandle,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    let aid = state
        .card(card)
        .and_then(|c| c.affliction.map(|a| a.id))
        .ok_or_else(|| KernelError::InvalidState("missing affliction".to_owned()))?;
    match aid {
        AfflictionId::Hexed => {
            if slot == HookSlot::AfterCardEnteredCombat {
                if let HookArgs::CardOnly(entered) = args {
                    if *entered == card {
                        let has_hex = state
                            .player_creature()
                            .is_some_and(|c| c.powers.iter().any(|p| p.id == PowerId::HexPower));
                        if !has_hex {
                            card_cmd::clear_affliction(state, card);
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        AfflictionId::Bound
        | AfflictionId::Entangled
        | AfflictionId::Galvanized
        | AfflictionId::Ringing
        | AfflictionId::Smog
        | AfflictionId::Tainted => Ok(HookResult::default()),
    }
}
