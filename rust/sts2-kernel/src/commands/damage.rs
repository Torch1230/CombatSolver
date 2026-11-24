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
use sts2_types::{CombatSide, Dec, Rng, ValueProp};

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

        let mut all_results: SmallVec<[DamageResult; 16]> =
            SmallVec::with_capacity(attack_count.max(0) as usize);
        for _ in 0..attack_count {
            if state.creature(self.attacker).is_none_or(|c| c.is_dead()) {
                break;
            }
            let valid_targets = collect_valid_targets(state, self);
            if valid_targets.is_empty() {
                break;
            }
            let targets_for_hit: SmallVec<[CreatureHandle; 8]> = match self.target_spec {
                AttackTargetSpec::Single(t) => smallvec::smallvec![t],
                AttackTargetSpec::AllOpponents => valid_targets,
                AttackTargetSpec::RandomOpponents { allow_duplicates } => {
                    let pool: SmallVec<[CreatureHandle; 8]> = if allow_duplicates {
                        valid_targets
                    } else {
                        valid_targets
                            .into_iter()
                            .filter(|h| !all_results.iter().any(|r: &DamageResult| r.receiver == *h))
                            .collect()
                    };
                    if pool.is_empty() {
                        return Err(KernelError::InvalidState(
                            "No valid targets for attack with duplicates disallowed".to_owned(),
                        ));
                    }
                    let chosen = state
                        .run_rngs
                        .get_mut(sts2_types::RunRngType::CombatTargets)
                        .next_item(&pool)
                        .copied()
                        .ok_or_else(|| KernelError::InvalidState("empty target pool".to_owned()))?;
                    smallvec::smallvec![chosen]
                }
            };
            let hit_results = damage_targets_with_hook_mode(
                state,
                content,
                &targets_for_hit,
                self.damage_per_hit,
                self.props,
                Some(self.attacker),
                self.card_source,
                self.card_play,
                self.hook_type,
            )?;
            all_results.extend(hit_results);
        }

        if state
            .creature(self.attacker)
            .is_some_and(|c| c.monster_id == Some(sts2_types::MonsterId::Osty) && c.is_pet())
        {
            state.record_actor_event("OstyAttack");
        }

        hooks::after_attack(state, content, info, &all_results)?;
        Ok(all_results)
    }
}

fn collect_valid_targets(state: &BattleState, builder: AttackBuilder) -> SmallVec<[CreatureHandle; 8]> {
    let raw: SmallVec<[CreatureHandle; 8]> = match builder.target_spec {
        AttackTargetSpec::Single(t) => smallvec::smallvec![t],
        AttackTargetSpec::AllOpponents | AttackTargetSpec::RandomOpponents { .. } => {
            if builder.is_monster_source {
                state
                    .ally_handles
                    .iter()
                    .copied()
                    .filter(|&h| {
                        state
                            .creature(h)
                            .is_some_and(|c| c.is_player || (!c.is_monster() && !c.is_pet()))
                    })
                    .collect()
            } else {
                let attacker_side = state
                    .creature(builder.attacker)
                    .map_or(CombatSide::Player, |c| c.side);
                if attacker_side == CombatSide::Player {
                    SmallVec::from_slice(&state.enemy_handles)
                } else {
                    SmallVec::from_slice(&state.ally_handles)
                }
            }
        }
    };
    raw.into_iter()
        .filter(|&h| state.creature(h).is_some_and(|c| c.is_alive()))
        .collect()
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
    if let Some(d) = dealer {
        if state.creature(d).is_none_or(|c| c.is_dead()) {
            return Ok(targets
                .iter()
                .map(|&t| DamageResult {
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
                })
                .collect());
        }
    }

    let mut pending: SmallVec<[(DamageResult, bool); 8]> = SmallVec::with_capacity(targets.len());
    for &target in targets {
        if state.creature(target).is_none_or(|c| c.is_dead()) {
            continue;
        }
        let singles = damage_single(
            state,
            content,
            target,
            amount,
            props,
            dealer,
            card_source,
            card_play,
            hook_type,
        )?;
        pending.extend(singles);
    }

    let mut killed: SmallVec<[DamageResult; 16]> = SmallVec::new();
    for (result, prevented) in &pending {
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
            killed.push(result.clone());
        }
    }

    for result in killed {
        resolve_potential_death(state, content, result.receiver, card_source)?;
    }

    Ok(pending.into_iter().map(|(r, _)| r).collect())
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
) -> Result<SmallVec<[(DamageResult, bool); 3]>, KernelError> {
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
        return Ok(smallvec::smallvec![(prevented, true)]);
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
    hooks::before_damage_received(state, content, target, modified_amount, props, dealer, card_source)?;

    let block_owner = state
        .creature(target)
        .and_then(|c| c.pet_owner_player_index.map(|_| state.player.creature_handle))
        .unwrap_or(target);

    let mut results: SmallVec<[(DamageResult, bool); 3]> = SmallVec::new();
    let pre_block_target = hooks::modify_pre_block_damage_target(
        state,
        content,
        target,
        amount,
        props,
        dealer,
    )?;
    let mut damage_remaining = modified_amount;

    if let Some(pbt) = pre_block_target {
        let (hp_loss, _) = hooks::modify_hp_lost(
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
    damage_remaining = damage_remaining.checked_sub(blocked_damage)?.max(Dec::ZERO);

    let (unblocked_before_osty, before_osty_mods) = hooks::modify_hp_lost(
        state,
        content,
        target,
        damage_remaining,
        props,
        dealer,
        card_source,
        HpLossPhase::BeforeOsty,
    )?;
    hooks::after_modifying_hp_lost(state, content, &before_osty_mods, HpLossPhase::BeforeOsty)?;

    let receiver = hooks::modify_unblocked_damage_target(
        state,
        content,
        target,
        unblocked_before_osty,
        props,
        dealer,
    )?;
    let (unblocked_after_osty, after_osty_mods) = hooks::modify_hp_lost(
        state,
        content,
        receiver,
        unblocked_before_osty,
        props,
        dealer,
        card_source,
        HpLossPhase::AfterOsty,
    )?;
    hooks::after_modifying_hp_lost(state, content, &after_osty_mods, HpLossPhase::AfterOsty)?;

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
        let (overflow, overflow_mods) = hooks::modify_hp_lost(
            state,
            content,
            target,
            overflow_remaining,
            props,
            dealer,
            card_source,
            HpLossPhase::AfterOsty,
        )?;
        hooks::after_modifying_hp_lost(state, content, &overflow_mods, HpLossPhase::AfterOsty)?;
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

    for (r, _) in &results {
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

    Ok(results)
}

pub fn roll_next_item<T: Copy>(rng: &mut Rng, items: &[T]) -> Option<T> {
    rng.next_item(items).copied()
}
