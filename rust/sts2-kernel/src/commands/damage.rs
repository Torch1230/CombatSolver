use crate::commands::creature::{
    damage_block_internal, lose_hp_internal, resolve_potential_death,
};
use crate::content_dispatch::{AttackContextInfo, CardPlayContext, ContentDispatcher, HookArgs};
use crate::error::KernelError;
use crate::hooks::{self, HpLossPhase};
pub use crate::hooks::ModifyDamageHookType;
use crate::state::{
    BattleState, CardHandle, CreatureHandle, DamageHistoryEntry, DamageResult, InlineI32Vec4,
};
use smallvec::SmallVec;
use sts2_types::{CombatSide, Dec, HookSlot, Rng, ValueProp};

#[derive(Clone, Copy, Debug)]
pub enum AttackTargetSpec {
    Single(CreatureHandle),
    AllOpponents,
    RandomOpponents { allow_duplicates: bool },
}

#[derive(Clone, Copy, Debug)]
pub struct AttackBuilder {
    pub damage_per_hit: Dec,
    pub hit_count: i32,
    pub attacker: CreatureHandle,
    pub card_source: Option<CardHandle>,
    pub card_play: Option<CardPlayContext>,
    pub is_monster_source: bool,
    pub props: ValueProp,
    pub target_spec: AttackTargetSpec,
    pub hook_type: ModifyDamageHookType,
}

impl AttackBuilder {
    pub fn from_card(
        attacker: CreatureHandle,
        card: CardHandle,
        card_play: CardPlayContext,
        damage_per_hit: Dec,
        target_spec: AttackTargetSpec,
    ) -> Self {
        Self {
            damage_per_hit,
            hit_count: 1,
            attacker,
            card_source: Some(card),
            card_play: Some(card_play),
            is_monster_source: false,
            props: ValueProp::MOVE,
            target_spec,
            hook_type: ModifyDamageHookType::ALL,
        }
    }

    pub fn from_monster(attacker: CreatureHandle, damage_per_hit: Dec) -> Self {
        Self {
            damage_per_hit,
            hit_count: 1,
            attacker,
            card_source: None,
            card_play: None,
            is_monster_source: true,
            props: ValueProp::MOVE,
            target_spec: AttackTargetSpec::AllOpponents,
            hook_type: ModifyDamageHookType::ALL,
        }
    }

    pub fn with_hit_count(mut self, hit_count: i32) -> Self {
        self.hit_count = hit_count;
        self
    }

    pub fn with_hook_type(mut self, hook_type: ModifyDamageHookType) -> Self {
        self.hook_type = hook_type;
        self
    }

    pub fn from_arrow(mut self) -> Self {
        self.props |= ValueProp::FROM_ARROW;
        self
    }

    pub fn unpowered(mut self) -> Self {
        self.props |= ValueProp::UNPOWERED;
        self
    }

    pub fn execute(
        self,
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
    ) -> Result<SmallVec<[DamageResult; 16]>, KernelError> {
        let attacker_dead = state
            .creature(self.attacker)
            .map_or(true, |c| c.is_dead());
        if hooks::is_over_or_ending_checked(state, content)? || attacker_dead {
            return Ok(SmallVec::new());
        }

        let attack_id = state.next_attack_id;
        state.next_attack_id += 1;
        let attacker_side = state
            .creature(self.attacker)
            .map_or(CombatSide::Player, |c| c.side);
        let opposite_side = match attacker_side {
            CombatSide::Player => CombatSide::Enemy,
            CombatSide::Enemy => CombatSide::Player,
        };
        let target_side = match self.target_spec {
            AttackTargetSpec::Single(t) => state.creature(t).map_or(opposite_side, |c| c.side),
            AttackTargetSpec::AllOpponents | AttackTargetSpec::RandomOpponents { .. } => {
                opposite_side
            }
        };
        let info = AttackContextInfo {
            attack_id,
            attacker: self.attacker,
            target_side,
            card_source: self.card_source,
            damage_props: self.props,
            hit_count: self.hit_count,
        };

        hooks::before_attack(state, content, info)?;
        let attack_count = hooks::modify_attack_hit_count(state, content, info, self.hit_count)?;

        state.enter_attacking(self.attacker);
        let res = (|| -> Result<SmallVec<[DamageResult; 16]>, KernelError> {
            let mut all_results: SmallVec<[DamageResult; 16]> =
                SmallVec::with_capacity(attack_count.max(0) as usize);
            match self.target_spec {
                AttackTargetSpec::Single(t) => {
                    let mut hit_results: SmallVec<[(DamageResult, bool); 8]> = SmallVec::new();
                    for _ in 0..attack_count {
                        if state.creature(self.attacker).is_none_or(|c| c.is_dead()) {
                            break;
                        }
                        if !state.creature(t).is_some_and(|c| c.is_alive()) {
                            break;
                        }
                        let targets_for_hit = [t];
                        hit_results.clear();
                        damage_targets_with_hook_mode_into(
                            state,
                            content,
                            &targets_for_hit,
                            self.damage_per_hit,
                            self.props,
                            Some(self.attacker),
                            self.card_source,
                            self.card_play,
                            self.hook_type,
                            &mut hit_results,
                        )?;
                        all_results.extend(hit_results.iter().map(|&(r, _)| r));
                    }
                }
                AttackTargetSpec::AllOpponents | AttackTargetSpec::RandomOpponents { .. } => {
                    let mut hit_results: SmallVec<[(DamageResult, bool); 8]> = SmallVec::new();
                    for _ in 0..attack_count {
                        if state.creature(self.attacker).is_none_or(|c| c.is_dead()) {
                            break;
                        }
                        let valid_targets = collect_valid_targets(state, self);
                        if valid_targets.is_empty() {
                            break;
                        }
                        let targets_for_hit: SmallVec<[CreatureHandle; 8]> = match self.target_spec {
                            AttackTargetSpec::AllOpponents => valid_targets,
                            AttackTargetSpec::RandomOpponents { allow_duplicates } => {
                                let chosen = if allow_duplicates {
                                    state
                                        .run_rngs
                                        .get_mut(sts2_types::RunRngType::CombatTargets)
                                        .next_item(&valid_targets)
                                        .copied()
                                        .ok_or_else(|| {
                                            KernelError::InvalidState("empty target pool".to_owned())
                                        })?
                                } else {
                                    let pool: SmallVec<[CreatureHandle; 8]> = valid_targets
                                        .into_iter()
                                        .filter(|h| !all_results.iter().any(|r| r.receiver == *h))
                                        .collect();
                                    if pool.is_empty() {
                                        return Err(KernelError::InvalidState(
                                            "No valid targets for attack with duplicates disallowed"
                                                .to_owned(),
                                        ));
                                    }
                                    state
                                        .run_rngs
                                        .get_mut(sts2_types::RunRngType::CombatTargets)
                                        .next_item(&pool)
                                        .copied()
                                        .ok_or_else(|| {
                                            KernelError::InvalidState("empty target pool".to_owned())
                                        })?
                                };
                                smallvec::smallvec![chosen]
                            }
                            AttackTargetSpec::Single(_) => unreachable!(),
                        };
                        hit_results.clear();
                        damage_targets_with_hook_mode_into(
                            state,
                            content,
                            &targets_for_hit,
                            self.damage_per_hit,
                            self.props,
                            Some(self.attacker),
                            self.card_source,
                            self.card_play,
                            self.hook_type,
                            &mut hit_results,
                        )?;
                        all_results.extend(hit_results.iter().map(|&(r, _)| r));
                    }
                }
            }

            if state
                .creature(self.attacker)
                .is_some_and(|c| c.monster_id == Some(sts2_types::MonsterId::Osty) && c.is_pet())
            {
                state.record_actor_event("OstyAttack");
            }

            hooks::after_attack(state, content, info, &all_results)?;
            Ok(all_results)
        })();
        state.exit_attacking(self.attacker);
        crate::commands::creature::flush_all_eligible_deferred_death_cleanups(state, content)?;
        res
    }
}

fn collect_valid_targets(state: &BattleState, builder: AttackBuilder) -> SmallVec<[CreatureHandle; 8]> {
    let mut out = SmallVec::new();
    match builder.target_spec {
        AttackTargetSpec::Single(t) => {
            if state.creature(t).is_some_and(|c| c.is_alive()) {
                out.push(t);
            }
        }
        AttackTargetSpec::AllOpponents | AttackTargetSpec::RandomOpponents { .. } => {
            if builder.is_monster_source {
                for &h in &state.ally_handles {
                    if let Some(c) = state.creature(h) {
                        if (c.is_player || (!c.is_monster() && !c.is_pet())) && c.is_alive() {
                            out.push(h);
                        }
                    }
                }
            } else {
                let attacker_side = state
                    .creature(builder.attacker)
                    .map_or(CombatSide::Player, |c| c.side);
                let handles = if attacker_side == CombatSide::Player {
                    &state.enemy_handles[..]
                } else {
                    &state.ally_handles[..]
                };
                for &h in handles {
                    if state.creature(h).is_some_and(|c| c.is_alive()) {
                        out.push(h);
                    }
                }
            }
        }
    }
    out
}

pub fn damage_targets(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    targets: &[CreatureHandle],
    amount: Dec,
    props: ValueProp,
    dealer: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
    card_play: Option<CardPlayContext>,
) -> Result<SmallVec<[DamageResult; 16]>, KernelError> {
    damage_targets_with_hook_mode(
        state,
        content,
        targets,
        amount,
        props,
        dealer,
        card_source,
        card_play,
        ModifyDamageHookType::ALL,
    )
}

pub fn damage_targets_void(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    targets: &[CreatureHandle],
    amount: Dec,
    props: ValueProp,
    dealer: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
    card_play: Option<CardPlayContext>,
) -> Result<(), KernelError> {
    let mut pending: SmallVec<[(DamageResult, bool); 8]> = SmallVec::with_capacity(targets.len());
    damage_targets_with_hook_mode_into(
        state,
        content,
        targets,
        amount,
        props,
        dealer,
        card_source,
        card_play,
        ModifyDamageHookType::ALL,
        &mut pending,
    )
}

#[allow(clippy::too_many_arguments)]
pub fn damage_targets_with_hook_mode(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    targets: &[CreatureHandle],
    amount: Dec,
    props: ValueProp,
    dealer: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
    card_play: Option<CardPlayContext>,
    hook_type: ModifyDamageHookType,
) -> Result<SmallVec<[DamageResult; 16]>, KernelError> {
    let mut pending: SmallVec<[(DamageResult, bool); 8]> = SmallVec::with_capacity(targets.len());
    damage_targets_with_hook_mode_into(
        state,
        content,
        targets,
        amount,
        props,
        dealer,
        card_source,
        card_play,
        hook_type,
        &mut pending,
    )?;
    Ok(pending.into_iter().map(|(r, _)| r).collect())
}

#[allow(clippy::too_many_arguments)]
fn damage_targets_with_hook_mode_into(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    targets: &[CreatureHandle],
    amount: Dec,
    props: ValueProp,
    dealer: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
    card_play: Option<CardPlayContext>,
    hook_type: ModifyDamageHookType,
    pending: &mut SmallVec<[(DamageResult, bool); 8]>,
) -> Result<(), KernelError> {
    if let Some(d) = dealer {
        if state.creature(d).is_none_or(|c| c.is_dead()) {
            for &t in targets {
                pending.push((
                    DamageResult {
                        receiver: t,
                        original_target: t,
                        props,
                        intended_hp_loss: 0,
                        blocked_damage: 0,
                        unblocked_damage: 0,
                        overkill_damage: 0,
                        was_target_killed: false,
                        was_block_broken: false,
                        was_fully_blocked: false,
                    },
                    false,
                ));
            }
            return Ok(());
        }
    }

    for &target in targets {
        if state.creature(target).is_none_or(|c| c.is_dead()) {
            continue;
        }
        damage_single(
            state,
            content,
            target,
            amount,
            props,
            dealer,
            card_source,
            card_play,
            hook_type,
            pending,
        )?;
    }

    let mut killed: SmallVec<[CreatureHandle; 8]> = SmallVec::new();
    for (result, prevented) in pending.iter() {
        if *prevented {
            continue;
        }
        if result.was_block_broken {
            hooks::after_block_broken(state, content, result.receiver, dealer)?;
        }
        if result.unblocked_damage > 0 {
            hooks::after_current_hp_changed(
                state,
                content,
                result.receiver,
                -result.unblocked_damage,
            )?;
        }
        let hook_args = HookArgs::DamageResult {
            target: result.receiver,
            dealer,
            props,
            blocked_damage: result.blocked_damage,
            unblocked_damage: result.unblocked_damage,
            was_target_killed: result.was_target_killed,
            was_fully_blocked: result.was_fully_blocked,
            card_source,
        };
        hooks::after_damage_given(state, content, &hook_args)?;
        let is_dead_now = state
            .creature(result.receiver)
            .is_some_and(|c| c.is_dead());
        if !result.was_target_killed || !is_dead_now {
            hooks::after_damage_received(state, content, &hook_args)?;
        } else {
            killed.push(result.receiver);
        }
    }

    for receiver in killed {
        resolve_potential_death(state, content, receiver, card_source)?;
    }

    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn damage_single(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    amount: Dec,
    props: ValueProp,
    dealer: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
    card_play: Option<CardPlayContext>,
    hook_type: ModifyDamageHookType,
    results: &mut SmallVec<[(DamageResult, bool); 8]>,
) -> Result<(), KernelError> {
    if !hooks::should_allow_hitting(state, content, target)? {
        let prevented = DamageResult {
            receiver: target,
            original_target: target,
            props,
            intended_hp_loss: 0,
            blocked_damage: 0,
            unblocked_damage: 0,
            overkill_damage: 0,
            was_target_killed: false,
            was_block_broken: false,
            was_fully_blocked: false,
        };
        results.push((prevented, true));
        return Ok(());
    }

    let modified_amount = hooks::modify_damage_with_type(
        state,
        content,
        Some(target),
        dealer,
        amount,
        props,
        card_source,
        card_play,
        hook_type,
    )?;
    let clean0 = state.hook_cache.masks_are_valid();
    if !clean0
        || state
            .hook_cache
            .masks
            .run_combat_combined
            .contains(HookSlot::BeforeDamageReceived)
    {
        hooks::before_damage_received(
            state,
            content,
            target,
            modified_amount,
            props,
            dealer,
            card_source,
        )?;
    }

    let block_owner = if state.pet_handles.is_empty() {
        #[cfg(debug_assertions)]
        if let Some(c) = state.creature(target) {
            debug_assert!(c.pet_owner_player_index.is_none());
        }
        target
    } else {
        state
            .creature(target)
            .and_then(|c| c.pet_owner_player_index.map(|_| state.player.creature_handle))
            .unwrap_or(target)
    };

    let start_len = results.len();
    let clean_pre = state.hook_cache.masks_are_valid();
    let pre_block_target = if !clean_pre
        || state
            .hook_cache
            .masks
            .combat_combined
            .contains(HookSlot::ModifyPreBlockDamageTarget)
    {
        hooks::modify_pre_block_damage_target(
            state,
            content,
            target,
            amount,
            props,
            dealer,
        )?
    } else {
        None
    };
    let mut damage_remaining = modified_amount;

    if let Some(pbt) = pre_block_target {
        let hp_loss = hooks::modify_hp_lost_no_notify(
            state,
            content,
            target,
            damage_remaining,
            props,
            dealer,
            card_source,
            HpLossPhase::BeforeOsty,
        )?;
        let mut absorbed = lose_hp_internal(state, pbt, hp_loss, props)?;
        absorbed.intended_hp_loss = damage_remaining.to_i32_trunc()?;
        absorbed.original_target = target;
        damage_remaining = Dec::from_i32(absorbed.overkill_damage);
        results.push((absorbed, false));
    }

    let blocked_damage = damage_block_internal(state, block_owner, damage_remaining, props)?;
    damage_remaining = if blocked_damage.is_zero() {
        damage_remaining.max(Dec::ZERO)
    } else {
        damage_remaining.checked_sub(blocked_damage)?.max(Dec::ZERO)
    };

    let clean_before = state.hook_cache.masks_are_valid();
    let run_comb_before = state.hook_cache.masks.run_combat_combined;
    let unblocked_before_osty = if !clean_before
        || run_comb_before.contains(HookSlot::ModifyHpLostBeforeOsty)
        || run_comb_before.contains(HookSlot::ModifyHpLostBeforeOstyLate)
    {
        hooks::modify_hp_lost_and_notify(
            state,
            content,
            target,
            damage_remaining,
            props,
            dealer,
            card_source,
            HpLossPhase::BeforeOsty,
        )?
    } else {
        damage_remaining
    };

    let clean_after = state.hook_cache.masks_are_valid();
    let receiver = if !clean_after
        || state
            .hook_cache
            .masks
            .combat_combined
            .contains(HookSlot::ModifyUnblockedDamageTarget)
    {
        hooks::modify_unblocked_damage_target(
            state,
            content,
            target,
            unblocked_before_osty,
            props,
            dealer,
        )?
    } else {
        target
    };
    let clean_osty = state.hook_cache.masks_are_valid();
    let run_comb_osty = state.hook_cache.masks.run_combat_combined;
    let unblocked_after_osty = if !clean_osty
        || run_comb_osty.contains(HookSlot::ModifyHpLostAfterOsty)
        || run_comb_osty.contains(HookSlot::ModifyHpLostAfterOstyLate)
    {
        hooks::modify_hp_lost_and_notify(
            state,
            content,
            receiver,
            unblocked_before_osty,
            props,
            dealer,
            card_source,
            HpLossPhase::AfterOsty,
        )?
    } else {
        unblocked_before_osty
    };

    let mut received = lose_hp_internal(state, receiver, unblocked_after_osty, props)?;
    received.intended_hp_loss = damage_remaining.to_i32_trunc()?;
    received.original_target = target;
    let overflow_remaining = Dec::from_i32(received.overkill_damage);

    let target_block_after = state.creature(target).map_or(0, |c| c.block);
    let block_broken = target_block_after <= 0 && blocked_damage > Dec::ZERO;
    let fully_blocked = !props.contains(ValueProp::UNBLOCKABLE)
        && (blocked_damage > Dec::ZERO || target_block_after > 0)
        && unblocked_after_osty.to_i32_trunc()? == 0;

    if receiver == target {
        received.blocked_damage = blocked_damage.to_i32_trunc()?;
        received.was_block_broken = block_broken;
        received.was_fully_blocked = fully_blocked;
        results.push((received, false));
    } else {
        results.push((received, false));
        let overflow = hooks::modify_hp_lost_and_notify(
            state,
            content,
            target,
            overflow_remaining,
            props,
            dealer,
            card_source,
            HpLossPhase::AfterOsty,
        )?;
        let mut original = if overflow > Dec::ZERO {
            lose_hp_internal(state, target, overflow, props)?
        } else {
            DamageResult {
                receiver: target,
                original_target: target,
                props,
                intended_hp_loss: 0,
                blocked_damage: 0,
                unblocked_damage: 0,
                overkill_damage: 0,
                was_target_killed: false,
                was_block_broken: false,
                was_fully_blocked: false,
            }
        };
        original.intended_hp_loss = overflow_remaining.to_i32_trunc()?;
        original.blocked_damage = blocked_damage.to_i32_trunc()?;
        original.was_block_broken = block_broken;
        original.was_fully_blocked = fully_blocked;
        results.push((original, false));
    }

    for (r, _) in &results[start_len..] {
        state.damage_history.push(DamageHistoryEntry {
            receiver_creature_handle: r.receiver,
            dealer_creature_handle: dealer,
            original_target_creature_handle: target,
            props: r.props,
            intended_hp_loss: r.intended_hp_loss,
            blocked_damage: r.blocked_damage,
            unblocked_damage: r.unblocked_damage,
            overkill_damage: r.overkill_damage,
            was_target_killed: r.was_target_killed,
            was_block_broken: r.was_block_broken,
            was_fully_blocked: r.was_fully_blocked,
            card_source_handle: card_source,
            round_number: state.round_number,
            current_side: state.current_side,
            player_turn_numbers: InlineI32Vec4::one(state.player.turn_number),
        });
    }

    Ok(())
}

pub fn roll_next_item<T: Copy>(rng: &mut Rng, items: &[T]) -> Option<T> {
    rng.next_item(items).copied()
}
