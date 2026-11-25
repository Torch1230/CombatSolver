pub mod batch_a_m;
pub mod batch_n_z;

use smallvec::SmallVec;
use std::borrow::Cow;
use sts2_kernel::commands::{block, card_pile, damage, gold, power};
use sts2_kernel::{
    hooks, BattleState, CardHandle, ContentDispatcher, CreatureHandle, FieldValue, HookArgs,
    HookResult, KernelError, RelicInst,
};
use sts2_types::{
    CardType, CombatSide, Dec, HookSlot, PlayerTurnPhase, PowerId, RelicId, RelicMeta, RunRngType,
    ValueProp,
};

const _: () = {
    assert!(RelicId::MysticLighter as usize == 168);
    assert!(RelicId::NeowsBones as usize == 169);
};

#[inline(always)]
const fn is_batch_a_m(id: RelicId) -> bool {
    (id as usize) < 169
}

#[inline]
pub(crate) fn hittable_enemies_fast(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<SmallVec<[CreatureHandle; 8]>, KernelError> {
    if state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some() {
        if !state
            .hook_cache
            .masks
            .combat_combined
            .contains(HookSlot::ShouldAllowHitting)
        {
            let mut out = SmallVec::new();
            for &eh in &state.enemy_handles {
                if state.creature(eh).is_some_and(|c| c.is_alive()) {
                    out.push(eh);
                }
            }
            #[cfg(debug_assertions)]
            for &eh in &out {
                debug_assert!(hooks::should_allow_hitting(state, content, eh)?);
            }
            return Ok(out);
        }
        if crate::CONTENT_TABLES.should_allow_hitting_known_only {
            let over = !state.is_starting && hooks::is_over_or_ending_checked(state, content)?;
            let mut out = SmallVec::new();
            for &eh in &state.enemy_handles {
                if let Some(c) = state.creature(eh) {
                    if c.is_alive() {
                        if over {
                            out.push(eh);
                        } else {
                            let mut blocked = false;
                            for p in &c.powers {
                                if p.ext_index.is_none() {
                                    continue;
                                }
                                let key = match p.id {
                                    PowerId::AdaptablePower => "AdaptablePower._isReviving",
                                    PowerId::IllusionPower => "IllusionPower._isReviving",
                                    PowerId::ReattachPower => "ReattachPower._isReviving",
                                    _ => continue,
                                };
                                if matches!(
                                    state
                                        .power_extensions
                                        .get(p.ext_index.unwrap() as usize)
                                        .and_then(|e| e.private_fields.get(key)),
                                    Some(FieldValue::Bool(true))
                                ) {
                                    blocked = true;
                                    break;
                                }
                            }
                            if !blocked {
                                out.push(eh);
                            }
                        }
                    }
                }
            }
            #[cfg(debug_assertions)]
            {
                let mut exp = SmallVec::<[CreatureHandle; 8]>::new();
                let enemies: SmallVec<[CreatureHandle; 8]> =
                    SmallVec::from_slice(&state.enemy_handles);
                for eh in enemies {
                    if state.creature(eh).is_some_and(|c| c.is_alive())
                        && hooks::should_allow_hitting(state, content, eh)?
                    {
                        exp.push(eh);
                    }
                }
                debug_assert_eq!(out, exp);
            }
            return Ok(out);
        }
    }
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

#[inline(always)]
fn relic_get_bool(r: &RelicInst, key: &str) -> bool {
    matches!(r.private_fields.get(key), Some(FieldValue::Bool(true)))
}

#[inline(always)]
fn relic_set_bool(r: &mut RelicInst, key: &'static str, val: bool) {
    r.private_fields
        .insert(Cow::Borrowed(key), FieldValue::Bool(val));
}

#[inline(always)]
fn relic_get_int(r: &RelicInst, key: &str) -> i32 {
    match r.private_fields.get(key) {
        Some(FieldValue::Int(i) | FieldValue::NullableInt(Some(i))) => *i,
        _ => 0,
    }
}

pub fn relic_meta(id: RelicId) -> Result<&'static RelicMeta, KernelError> {
    if is_batch_a_m(id) {
        batch_a_m::relic_meta(id)
    } else {
        batch_n_z::relic_meta(id)
    }
}

pub fn dispatch_hook(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    relic_idx: usize,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    let relic = state
        .player
        .relics
        .get(relic_idx)
        .ok_or_else(|| KernelError::InvalidState("missing relic".to_owned()))?;
    if relic.is_melted {
        return Ok(HookResult::default());
    }
    let id = relic.id;
    let pc = state.player.creature_handle;
    match id {
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
            return Ok(HookResult::default());
        }
        RelicId::RainbowRing => {
            match slot {
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(cp) = args {
                        if relic_get_bool(&state.player.relics[relic_idx], "RainbowRing._triggeredThisTurn")
                        {
                            return Ok(HookResult::default());
                        }
                        if state.card(cp.card).is_some() {
                            let ctype = content.card_resolved_type(state, cp.card)?;
                            let flag_key = match ctype {
                                CardType::Attack => "RainbowRing._playedAttack",
                                CardType::Skill => "RainbowRing._playedSkill",
                                CardType::Power => "RainbowRing._playedPower",
                                _ => return Ok(HookResult::default()),
                            };
                            let trigger = {
                                state.mark_relic_dirty(relic_idx);
                                let r = &mut state.player.relics[relic_idx];
                                let set_inplace = match r.private_fields.get_mut(flag_key) {
                                    Some(FieldValue::Bool(b)) => {
                                        *b = true;
                                        true
                                    }
                                    _ => false,
                                };
                                if !set_inplace {
                                    relic_set_bool(r, flag_key, true);
                                }
                                if relic_get_bool(r, "RainbowRing._playedAttack")
                                    && relic_get_bool(r, "RainbowRing._playedSkill")
                                    && relic_get_bool(r, "RainbowRing._playedPower")
                                {
                                    match r.private_fields.get_mut("RainbowRing._triggeredThisTurn")
                                    {
                                        Some(FieldValue::Bool(b)) => *b = true,
                                        _ => relic_set_bool(
                                            r,
                                            "RainbowRing._triggeredThisTurn",
                                            true,
                                        ),
                                    }
                                    true
                                } else {
                                    false
                                }
                            };
                            if trigger {
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
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            state.mark_relic_dirty(relic_idx);
                            let r = &mut state.player.relics[relic_idx];
                            if r.private_fields.len() == 4 {
                                let mut all_rainbow = true;
                                for (k, v) in r.private_fields.iter_mut() {
                                    if matches!(
                                        k,
                                        "RainbowRing._playedAttack"
                                            | "RainbowRing._playedSkill"
                                            | "RainbowRing._playedPower"
                                            | "RainbowRing._triggeredThisTurn"
                                    ) {
                                        if let FieldValue::Bool(b) = v {
                                            *b = false;
                                        } else {
                                            *v = FieldValue::Bool(false);
                                        }
                                    } else {
                                        all_rainbow = false;
                                        break;
                                    }
                                }
                                if all_rainbow {
                                    return Ok(HookResult::default());
                                }
                            }
                            relic_set_bool(r, "RainbowRing._playedAttack", false);
                            relic_set_bool(r, "RainbowRing._playedSkill", false);
                            relic_set_bool(r, "RainbowRing._playedPower", false);
                            relic_set_bool(r, "RainbowRing._triggeredThisTurn", false);
                        }
                    }
                }
                HookSlot::AfterCombatEnd => {
                    state.mark_relic_dirty(relic_idx);
                    let r = &mut state.player.relics[relic_idx];
                    if r.private_fields.len() == 4 {
                        let mut all_rainbow = true;
                        for (k, v) in r.private_fields.iter_mut() {
                            if matches!(
                                k,
                                "RainbowRing._playedAttack"
                                    | "RainbowRing._playedSkill"
                                    | "RainbowRing._playedPower"
                                    | "RainbowRing._triggeredThisTurn"
                            ) {
                                if let FieldValue::Bool(b) = v {
                                    *b = false;
                                } else {
                                    *v = FieldValue::Bool(false);
                                }
                            } else {
                                all_rainbow = false;
                                break;
                            }
                        }
                        if all_rainbow {
                            return Ok(HookResult::default());
                        }
                    }
                    relic_set_bool(r, "RainbowRing._playedAttack", false);
                    relic_set_bool(r, "RainbowRing._playedSkill", false);
                    relic_set_bool(r, "RainbowRing._playedPower", false);
                    relic_set_bool(r, "RainbowRing._triggeredThisTurn", false);
                }
                _ => {}
            }
            return Ok(HookResult::default());
        }
        RelicId::Kunai => {
            match slot {
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(cp) = args {
                        let is_attack = if state.card(cp.card).is_some() {
                            content.card_resolved_type(state, cp.card)? == CardType::Attack
                        } else {
                            false
                        };
                        if is_attack {
                            state.mark_relic_dirty(relic_idx);
                            let next = state.player.relics[relic_idx]
                                .private_fields
                                .inc_int("Kunai._attacksThisTurn");
                            if next % 3 == 0 {
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
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            state.mark_relic_dirty(relic_idx);
                            state.player.relics[relic_idx]
                                .private_fields
                                .set_int_inplace("Kunai._attacksThisTurn", 0);
                        }
                    }
                }
                HookSlot::AfterCombatEnd => {
                    state.mark_relic_dirty(relic_idx);
                    state.player.relics[relic_idx]
                        .private_fields
                        .set_int_inplace("Kunai._attacksThisTurn", 0);
                }
                _ => {}
            }
            return Ok(HookResult::default());
        }
        RelicId::Shuriken => {
            match slot {
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(cp) = args {
                        if state.card(cp.card).is_some()
                            && content.card_resolved_type(state, cp.card)? == CardType::Attack
                        {
                            state.mark_relic_dirty(relic_idx);
                            let next = state.player.relics[relic_idx]
                                .private_fields
                                .inc_int("Shuriken._attacksThisTurn");
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
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            state.mark_relic_dirty(relic_idx);
                            state.player.relics[relic_idx]
                                .private_fields
                                .set_int_inplace("Shuriken._attacksThisTurn", 0);
                        }
                    }
                }
                HookSlot::AfterCombatEnd => {
                    state.mark_relic_dirty(relic_idx);
                    state.player.relics[relic_idx]
                        .private_fields
                        .set_int_inplace("Shuriken._attacksThisTurn", 0);
                }
                _ => {}
            }
            return Ok(HookResult::default());
        }
        RelicId::OrnamentalFan => {
            match slot {
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(cp) = args {
                        if state.card(cp.card).is_some()
                            && content.card_resolved_type(state, cp.card)? == CardType::Attack
                        {
                            state.mark_relic_dirty(relic_idx);
                            let next = state.player.relics[relic_idx]
                                .private_fields
                                .inc_int("OrnamentalFan._attacksThisTurn");
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
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            state.mark_relic_dirty(relic_idx);
                            state.player.relics[relic_idx]
                                .private_fields
                                .set_int_inplace("OrnamentalFan._attacksThisTurn", 0);
                        }
                    }
                }
                HookSlot::AfterCombatEnd => {
                    state.mark_relic_dirty(relic_idx);
                    state.player.relics[relic_idx]
                        .private_fields
                        .set_int_inplace("OrnamentalFan._attacksThisTurn", 0);
                }
                _ => {}
            }
            return Ok(HookResult::default());
        }
        RelicId::PenNib => {
            match slot {
                HookSlot::BeforeCardPlayed => {
                    if let HookArgs::CardPlay(cp) = args {
                        if state.card(cp.card).is_some()
                            && content.card_resolved_type(state, cp.card)? == CardType::Attack
                        {
                            state.mark_relic_dirty(relic_idx);
                            let next = state.player.relics[relic_idx]
                                .private_fields
                                .inc_int("PenNib._attacksPlayed");
                            if next % 10 == 0 {
                                let r = &mut state.player.relics[relic_idx];
                                r.private_fields.set_int_inplace(
                                    "PenNib._doubleDamagePlayOrdinal",
                                    cp.play_ordinal,
                                );
                                r.private_fields.insert(
                                    Cow::Borrowed("PenNib._doubleDamageCard"),
                                    FieldValue::CardHandle(cp.card.0 as i32),
                                );
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
                        if !props.is_powered_attack() || card_play.is_none() {
                            return Ok(HookResult {
                                dec_val: Some(Dec::ONE),
                                ..Default::default()
                            });
                        }
                        let r = &state.player.relics[relic_idx];
                        if r.private_fields.len() > 1 {
                            let cp = card_play.expect("checked Some above");
                            let tracked_ord = r
                                .private_fields
                                .get("PenNib._doubleDamagePlayOrdinal")
                                .and_then(|v| match v {
                                    FieldValue::Int(i) => Some(*i),
                                    _ => None,
                                });
                            if tracked_ord == Some(cp.play_ordinal)
                                && *card_source == Some(cp.card)
                            {
                                let tracked_card = match r.private_fields.get("PenNib._doubleDamageCard") {
                                    Some(
                                        FieldValue::CardHandle(h)
                                        | FieldValue::Int(h)
                                        | FieldValue::NullableInt(Some(h)),
                                    ) => u32::try_from(*h).ok().map(CardHandle),
                                    _ => None,
                                };
                                let osty = state.osty_handle();
                                let dealer_ok =
                                    *dealer == Some(pc) || (osty.is_some() && *dealer == osty);
                                if dealer_ok && Some(cp.card) == tracked_card {
                                    return Ok(HookResult {
                                        dec_val: Some(Dec::TWO),
                                        ..Default::default()
                                    });
                                }
                            }
                        }
                        return Ok(HookResult {
                            dec_val: Some(Dec::ONE),
                            ..Default::default()
                        });
                    }
                }
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(cp) = args {
                        let pf = &state.player.relics[relic_idx].private_fields;
                        if pf.len() > 1 {
                            let tracked_ord = pf
                                .get("PenNib._doubleDamagePlayOrdinal")
                                .and_then(|v| match v {
                                    FieldValue::Int(i) => Some(*i),
                                    _ => None,
                                });
                            if tracked_ord == Some(cp.play_ordinal) {
                                state.mark_relic_dirty(relic_idx);
                                let r = &mut state.player.relics[relic_idx];
                                r.private_fields
                                    .remove("PenNib._doubleDamagePlayOrdinal");
                                r.private_fields.remove("PenNib._doubleDamageCard");
                            }
                        }
                    }
                }
                _ => {}
            }
            return Ok(HookResult::default());
        }
        RelicId::Nunchaku => {
            if slot == HookSlot::AfterCardPlayed {
                if let HookArgs::CardPlay(cp) = args {
                    if let Some(c) = state.card(cp.card) {
                        if state.fast_card_meta(content, c.id)?.card_type == CardType::Attack {
                            state.mark_relic_dirty(relic_idx);
                            let next = state.player.relics[relic_idx]
                                .private_fields
                                .inc_int("Nunchaku._attacksPlayed");
                            if next % 10 == 0 {
                                gold::gain_energy(state, content, Dec::ONE)?;
                            }
                        }
                    }
                }
            }
            return Ok(HookResult::default());
        }
        RelicId::Pocketwatch => {
            match slot {
                HookSlot::BeforeSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) {
                            state.mark_relic_dirty(relic_idx);
                            let r = &mut state.player.relics[relic_idx];
                            let this_turn = relic_get_int(r, "Pocketwatch._cardsPlayedThisTurn");
                            r.private_fields
                                .set_int_inplace("Pocketwatch._cardsPlayedLastTurn", this_turn);
                            r.private_fields
                                .set_int_inplace("Pocketwatch._cardsPlayedThisTurn", 0);
                        }
                    }
                }
                HookSlot::ModifyHandDraw => {
                    if state.player.turn_number != 1
                        && relic_get_int(
                            &state.player.relics[relic_idx],
                            "Pocketwatch._cardsPlayedLastTurn",
                        ) <= 3
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
                        state.mark_relic_dirty(relic_idx);
                        state.player.relics[relic_idx]
                            .private_fields
                            .inc_int("Pocketwatch._cardsPlayedThisTurn");
                    }
                }
                HookSlot::AfterCombatEnd => {
                    state.mark_relic_dirty(relic_idx);
                    let r = &mut state.player.relics[relic_idx];
                    r.private_fields
                        .set_int_inplace("Pocketwatch._cardsPlayedThisTurn", 0);
                    r.private_fields
                        .set_int_inplace("Pocketwatch._cardsPlayedLastTurn", 0);
                }
                _ => {}
            }
            return Ok(HookResult::default());
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
            return Ok(HookResult::default());
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
            return Ok(HookResult::default());
        }
        RelicId::Tingsha => {
            if slot == HookSlot::AfterCardDiscarded && state.current_side == CombatSide::Player {
                if state.enemy_handles.len() == 1
                    && state.hook_cache.dirty_mask == 0
                    && state.hook_cache.cached_tables.is_some()
                    && !state
                        .hook_cache
                        .masks
                        .combat_combined
                        .contains(HookSlot::ShouldAllowHitting)
                {
                    let eh = state.enemy_handles[0];
                    if state.creature(eh).is_some_and(|c| c.is_alive()) {
                        let single = [eh];
                        #[cfg(debug_assertions)]
                        debug_assert!(hooks::should_allow_hitting(state, content, eh)?);
                        let _ = state
                            .run_rngs
                            .get_mut(RunRngType::CombatTargets)
                            .next_item(&single);
                        damage::damage_targets_void(
                            state,
                            content,
                            &single,
                            Dec::from_i32(3),
                            ValueProp::UNPOWERED,
                            Some(pc),
                            None,
                            None,
                        )?;
                    }
                    return Ok(HookResult::default());
                }
                let enemies = hittable_enemies_fast(state, content)?;
                if let Some(&target) = state
                    .run_rngs
                    .get_mut(RunRngType::CombatTargets)
                    .next_item(&enemies)
                {
                    damage::damage_targets_void(
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
            return Ok(HookResult::default());
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
            return Ok(HookResult::default());
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
            return Ok(HookResult::default());
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
            return Ok(HookResult::default());
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
            return Ok(HookResult::default());
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
            return Ok(HookResult::default());
        }
        RelicId::LetterOpener => {
            match slot {
                HookSlot::BeforeCombatStart => {
                    state.mark_relic_dirty(relic_idx);
                    state.player.relics[relic_idx]
                        .private_fields
                        .set_int_inplace("LetterOpener._skillsThisTurn", 0);
                }
                HookSlot::AfterSideTurnStart => {
                    if let HookArgs::SideTurn { participants, .. } = args {
                        if participants.contains(&pc) && state.player.turn_number != 1 {
                            state.mark_relic_dirty(relic_idx);
                            state.player.relics[relic_idx]
                                .private_fields
                                .set_int_inplace("LetterOpener._skillsThisTurn", 0);
                        }
                    }
                }
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(ctx) = args {
                        if state.card(ctx.card).is_some()
                            && content.card_resolved_type(state, ctx.card)? == CardType::Skill
                        {
                            state.mark_relic_dirty(relic_idx);
                            let skills = state.player.relics[relic_idx]
                                .private_fields
                                .inc_int("LetterOpener._skillsThisTurn");
                            if skills % 3 == 0 {
                                let targets = hittable_enemies_fast(state, content)?;
                                damage::damage_targets_void(
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
            return Ok(HookResult::default());
        }
        RelicId::MercuryHourglass => {
            if slot == HookSlot::AfterPlayerTurnStart {
                let targets = hittable_enemies_fast(state, content)?;
                damage::damage_targets_void(
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
            return Ok(HookResult::default());
        }
        RelicId::CharonsAshes => {
            if slot == HookSlot::AfterCardExhausted {
                let targets = hittable_enemies_fast(state, content)?;
                damage::damage_targets_void(
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
            return Ok(HookResult::default());
        }
        RelicId::MummifiedHand => {
            if slot == HookSlot::AfterCardPlayed {
                if !state.is_live_combat() {
                    return Ok(HookResult::default());
                }
                if let HookArgs::CardPlay(ctx) = args {
                    if state.card(ctx.card).is_some()
                        && content.card_resolved_type(state, ctx.card)? != CardType::Power
                    {
                        return Ok(HookResult::default());
                    }
                }
            }
        }
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
            return Ok(HookResult::default());
        }
        _ => {}
    }
    if is_batch_a_m(id) {
        batch_a_m::dispatch_hook_at(content, state, id, Some(relic_idx), slot, args)
    } else {
        batch_n_z::dispatch_hook_at(content, state, id, Some(relic_idx), slot, args)
    }
}

pub fn resume_hook_choice(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    relic_idx: usize,
    slot: HookSlot,
    selected: &[CardHandle],
) -> Result<HookResult, KernelError> {
    let id = state
        .player
        .relics
        .get(relic_idx)
        .ok_or_else(|| KernelError::InvalidState("missing relic".to_owned()))?
        .id;
    if is_batch_a_m(id) {
        batch_a_m::resume_hook_choice(content, state, id, slot, selected)
    } else {
        batch_n_z::resume_hook_choice(content, state, id, slot, selected)
    }
}
