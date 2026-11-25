use smallvec::SmallVec;
use std::borrow::Cow;
use std::sync::LazyLock;
use sts2_kernel::commands::card_select::SelectResult;
use sts2_kernel::commands::{
    autoplay, block, card as card_cmd, card_factory, card_pile, card_select, creature,
    damage::{self, AttackBuilder, AttackTargetSpec},
    gold, power,
};
use sts2_kernel::content_dispatch::{CardPlayContext, CardPlayOutcome};
use sts2_kernel::{
    hooks, BattleState, CardHandle, ChoiceTiming, ContentDispatcher, CreatureHandle, FieldValue,
    HookArgs, HookResult, KernelError, LocalEnergyModifier,
};
use sts2_types::{
    CardId, CardKeywords, CardMeta, CardPartition, CardPilePosition, CardStaticFlags, CardTags,
    CardType, Dec, HookSlot, LocalEnergyModifierKind, PileType, PowerId, RunRngType, ValueProp,
    card_partition, generated_card_meta,
    generated_ids::CARD_META_TABLE,
};

use crate::powers::batch_a_m::{
    crimson_mantle_increment_self_damage, inferno_increment_self_damage,
};

static IRONCLAD_META_TABLE: LazyLock<[CardMeta; CardId::COUNT]> = LazyLock::new(|| {
    let mut table = CARD_META_TABLE;
    let mut i = 0usize;
    while i < CardId::COUNT {
        let id_opt = all_ironclad_extra_flags_by_index(i);
        table[i].flags |= id_opt;
        i += 1;
    }
    table
});

const fn all_ironclad_extra_flags_by_index(idx: usize) -> CardStaticFlags {
    let mut flags = CardStaticFlags::empty();
    if idx == CardId::Feed as usize {
        flags = flags.union(CardStaticFlags::MAY_HEAL).union(CardStaticFlags::HAS_GROWTH);
    } else if idx == CardId::NotYet as usize {
        flags = flags.union(CardStaticFlags::MAY_HEAL);
    }
    if idx == CardId::Cinder as usize
        || idx == CardId::InfernalBlade as usize
        || idx == CardId::Stoke as usize
        || idx == CardId::SwordBoomerang as usize
        || idx == CardId::Thrash as usize
        || idx == CardId::TrueGrit as usize
        || idx == CardId::Havoc as usize
        || idx == CardId::Cascade as usize
    {
        flags = flags.union(CardStaticFlags::USES_RNG);
    }
    if idx == CardId::Aggression as usize
        || idx == CardId::Anger as usize
        || idx == CardId::Armaments as usize
        || idx == CardId::AshenStrike as usize
        || idx == CardId::Barricade as usize
        || idx == CardId::Bash as usize
        || idx == CardId::BattleTrance as usize
        || idx == CardId::Blaze as usize
        || idx == CardId::BloodWall as usize
        || idx == CardId::Bloodletting as usize
        || idx == CardId::BodySlam as usize
        || idx == CardId::Brand as usize
        || idx == CardId::Break as usize
        || idx == CardId::Breakthrough as usize
        || idx == CardId::Bully as usize
        || idx == CardId::BurningPact as usize
        || idx == CardId::Cascade as usize
        || idx == CardId::Cinder as usize
        || idx == CardId::Colossus as usize
        || idx == CardId::Corruption as usize
        || idx == CardId::CrimsonMantle as usize
        || idx == CardId::Cruelty as usize
        || idx == CardId::DarkEmbrace as usize
        || idx == CardId::DemonForm as usize
        || idx == CardId::DemonicShield as usize
        || idx == CardId::Dismantle as usize
        || idx == CardId::Dominate as usize
        || idx == CardId::DrumOfBattle as usize
        || idx == CardId::EvilEye as usize
        || idx == CardId::ExpectAFight as usize
        || idx == CardId::Feed as usize
        || idx == CardId::FeelNoPain as usize
        || idx == CardId::FiendFire as usize
        || idx == CardId::FightMe as usize
        || idx == CardId::FlameBarrier as usize
        || idx == CardId::ForgottenRitual as usize
        || idx == CardId::Havoc as usize
        || idx == CardId::Headbutt as usize
        || idx == CardId::Hellraiser as usize
        || idx == CardId::Hemokinesis as usize
        || idx == CardId::HowlFromBeyond as usize
        || idx == CardId::InfernalBlade as usize
        || idx == CardId::Inferno as usize
        || idx == CardId::Inflame as usize
        || idx == CardId::Juggernaut as usize
        || idx == CardId::Juggling as usize
        || idx == CardId::Mangle as usize
        || idx == CardId::Midnight as usize
        || idx == CardId::MoltenFist as usize
        || idx == CardId::Offering as usize
        || idx == CardId::OneTwoPunch as usize
        || idx == CardId::Outrage as usize
        || idx == CardId::PactsEnd as usize
        || idx == CardId::PerfectedStrike as usize
        || idx == CardId::Pillage as usize
        || idx == CardId::PommelStrike as usize
        || idx == CardId::PrimalForce as usize
        || idx == CardId::Pyre as usize
        || idx == CardId::Rage as usize
        || idx == CardId::Rampage as usize
        || idx == CardId::Rupture as usize
        || idx == CardId::SecondWind as usize
        || idx == CardId::SetupStrike as usize
        || idx == CardId::ShrugItOff as usize
        || idx == CardId::Spite as usize
        || idx == CardId::Stampede as usize
        || idx == CardId::Stoke as usize
        || idx == CardId::Stomp as usize
        || idx == CardId::StoneArmor as usize
        || idx == CardId::Tank as usize
        || idx == CardId::Taunt as usize
        || idx == CardId::TearAsunder as usize
        || idx == CardId::Thrash as usize
        || idx == CardId::Thunderclap as usize
        || idx == CardId::Tremble as usize
        || idx == CardId::TrueGrit as usize
        || idx == CardId::Unmovable as usize
        || idx == CardId::Unrelenting as usize
        || idx == CardId::Uppercut as usize
        || idx == CardId::Vicious as usize
    {
        flags = flags.union(CardStaticFlags::ORDER_SENSITIVE);
    }
    flags
}

pub fn card_meta(id: CardId) -> Result<&'static CardMeta, KernelError> {
    if card_partition(id) == CardPartition::Ironclad {
        Ok(&IRONCLAD_META_TABLE[id as usize])
    } else {
        Err(KernelError::Unsupported {
            kind: "Card",
            id: id.model_id_str().to_owned(),
        })
    }
}

pub fn is_playable(state: &BattleState, card: CardHandle) -> Result<bool, KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    let _ = card_meta(id)?;
    Ok(true)
}

#[inline]
fn require_target(ctx: &CardPlayContext, id: CardId) -> Result<CreatureHandle, KernelError> {
    ctx.target.ok_or_else(|| {
        KernelError::InvalidAction(format!("{} requires target", id.model_id_str()))
    })
}

#[inline]
fn attack_single(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    owner: CreatureHandle,
    ctx: &CardPlayContext,
    target: CreatureHandle,
    dmg: Dec,
    hits: i32,
) -> Result<SmallVec<[sts2_kernel::DamageResult; 16]>, KernelError> {
    AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
        .with_hit_count(hits)
        .execute(state, content)
}

#[inline]
fn attack_all(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    owner: CreatureHandle,
    ctx: &CardPlayContext,
    dmg: Dec,
    hits: i32,
) -> Result<SmallVec<[sts2_kernel::DamageResult; 16]>, KernelError> {
    AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
        .with_hit_count(hits)
        .execute(state, content)
}

#[inline]
fn self_damage(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    owner: CreatureHandle,
    ctx: &CardPlayContext,
    amount: i32,
) -> Result<(), KernelError> {
    let _ = damage::damage_targets(
        state,
        content,
        &[owner],
        Dec::from_i32(amount),
        ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED | ValueProp::MOVE,
        Some(owner),
        Some(ctx.card),
        Some(*ctx),
    )?;
    Ok(())
}

#[inline]
fn gain_block(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    ctx: &CardPlayContext,
    amount: Dec,
) -> Result<(), KernelError> {
    let _ = block::gain_block(
        state,
        content,
        target,
        amount,
        ValueProp::MOVE,
        Some(ctx.card),
        Some(*ctx),
    )?;
    Ok(())
}

#[inline]
fn apply_power(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    power_id: PowerId,
    target: CreatureHandle,
    amount: Dec,
    owner: CreatureHandle,
    card_source: CardHandle,
) -> Result<Option<usize>, KernelError> {
    power::apply(
        state,
        content,
        power_id,
        target,
        amount,
        Some(owner),
        Some(card_source),
    )
}

#[inline]
fn power_amount(state: &BattleState, target: CreatureHandle, power_id: PowerId) -> i32 {
    state
        .creature(target)
        .and_then(|c| c.powers.iter().find(|p| p.id == power_id))
        .map_or(0, |p| p.amount)
}

fn hittable_enemies(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<SmallVec<[CreatureHandle; 8]>, KernelError> {
    let enemies: SmallVec<[CreatureHandle; 8]> = SmallVec::from_slice(&state.enemy_handles);
    let mut out = SmallVec::new();
    for eh in enemies {
        if state.creature(eh).is_some_and(|c| c.is_alive())
            && hooks::should_allow_hitting(state, content, eh)?
        {
            out.push(eh);
        }
    }
    Ok(out)
}

#[inline]
fn count_cards_exhausted_this_turn(state: &BattleState) -> usize {
    let tk = state.current_turn_key();
    state
        .semantic_history
        .exhausts
        .iter()
        .filter(|(t, _)| *t == tk)
        .count()
}

#[inline]
fn lost_hp_this_turn(state: &BattleState, owner: CreatureHandle) -> bool {
    state.damage_history.iter().any(|e| {
        e.receiver_creature_handle == owner
            && e.unblocked_damage > 0
            && e.round_number == state.round_number
            && e.current_side == state.current_side
            && e.player_turn_numbers.len() == 1
            && e.player_turn_numbers[0] == state.player.turn_number
    })
}

#[inline]
fn count_unblocked_hp_loss_hits(state: &BattleState, owner: CreatureHandle) -> i32 {
    let prior = if owner == state.player.creature_handle {
        state.semantic_history.prior_player_unblocked_hits
    } else {
        0
    };
    prior
        + state
            .damage_history
            .iter()
            .filter(|e| e.receiver_creature_handle == owner && e.unblocked_damage > 0)
            .count() as i32
}

fn count_strikes_in_combat(state: &BattleState) -> i32 {
    let mut count = 0i32;
    for pile in [
        &state.player.hand[..],
        &state.player.draw[..],
        &state.player.discard[..],
        &state.player.exhaust[..],
        &state.player.play[..],
    ] {
        for &h in pile {
            let Some(c) = state.card(h) else {
                continue;
            };
            if generated_card_meta(c.id)
                .base_tags
                .contains(CardTags::STRIKE)
            {
                count += 1;
            }
        }
    }
    count
}

fn clone_card_in_combat(
    state: &mut BattleState,
    orig_handle: CardHandle,
) -> Result<CardHandle, KernelError> {
    let orig = *state
        .card(orig_handle)
        .ok_or_else(|| KernelError::InvalidState("missing card to clone".to_owned()))?;
    if orig.pile == PileType::Deck {
        return Err(KernelError::InvalidAction(
            "Cannot create a combat clone of a persistent deck card.".to_owned(),
        ));
    }
    let source_handle = if orig
        .temp_flags
        .contains(sts2_kernel::CardTempFlags::IS_DUPE)
    {
        orig.clone_of_handle.unwrap_or(orig_handle)
    } else {
        orig_handle
    };
    let source = *state
        .card(source_handle)
        .ok_or_else(|| KernelError::InvalidState("missing clone source card".to_owned()))?;
    let new_handle = CardHandle(state.next_card_handle);
    state.next_card_handle += 1;
    let ext_index = if let Some(ext) = state.card_ext(source.ext_index).cloned() {
        let idx = state.card_extensions.len() as u32;
        state.card_extensions.push(ext);
        Some(idx)
    } else {
        None
    };
    let mut temp_flags = source.temp_flags;
    temp_flags.remove(
        sts2_kernel::CardTempFlags::EXHAUST_ON_NEXT_PLAY | sts2_kernel::CardTempFlags::IS_DUPE,
    );
    state.cards.push(sts2_kernel::CardInst {
        handle: new_handle,
        deck_card_handle: sts2_kernel::OptCardHandle::NONE,
        clone_of_handle: sts2_kernel::OptCardHandle::some(source_handle),
        floor_added_to_deck: sts2_kernel::OptI16::NONE,
        ext_index,
        temp_flags,
        pile: PileType::None,
        removed_from_state: false,
        transform_into_after_play: None,
        ..source
    });
    Ok(new_handle)
}

fn set_to_free_this_turn(state: &mut BattleState, handle: CardHandle) {
    let Some(card) = state.card(handle) else {
        return;
    };
    let meta = generated_card_meta(card.id);
    if !meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST) {
        if let Some(c) = state.card_mut(handle) {
            c.temp_cost_override_this_turn_or_until_played = sts2_kernel::OptI16::some(0);
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
            c.temp_star_cost_override_this_turn = sts2_kernel::OptI16::some(0);
        }
    }
}

fn add_local_energy_modifier(
    state: &mut BattleState,
    handle: CardHandle,
    kind: LocalEnergyModifierKind,
    amount: i32,
) {
    if amount == 0 {
        return;
    }
    if let Some(ext) = state.card_ext_mut(handle) {
        ext.push_local_energy_modifier(LocalEnergyModifier {
            kind,
            amount,
            is_reduce_only: false,
        });
    }
}

fn card_generation_pool(state: &BattleState) -> SmallVec<[CardId; 128]> {
    card_factory::character_combat_pool(state)
}

fn select_from_hand(
    state: &BattleState,
    min_count: usize,
    max_count: usize,
    source_id: &'static str,
    choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
) -> Result<SelectResult, KernelError> {
    if hooks::is_over_or_ending(state) || state.player_creature().is_none_or(|c| c.is_dead()) {
        return Ok(SelectResult::Selected(SmallVec::new()));
    }
    let candidates: SmallVec<[CardHandle; 16]> = state.player.hand.iter().copied().collect();
    card_select::select_cards(
        state,
        &candidates,
        min_count,
        max_count,
        ChoiceTiming::Action,
        source_id,
        PileType::Hand,
        false,
        choice_queue,
    )
}

fn select_from_hand_for_upgrade(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    source_id: &'static str,
    choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
) -> Result<SelectResult, KernelError> {
    if hooks::is_over_or_ending(state) {
        return Ok(SelectResult::Selected(SmallVec::new()));
    }
    let mut candidates: SmallVec<[CardHandle; 16]> = SmallVec::new();
    for &h in &state.player.hand {
        if let Some(c) = state.card(h) {
            if c.upgrade_level < content.card_meta(c.id)?.max_upgrade_level {
                candidates.push(h);
            }
        }
    }
    card_select::select_cards(
        state,
        &candidates,
        1,
        1,
        ChoiceTiming::Action,
        source_id,
        PileType::Hand,
        false,
        choice_queue,
    )
}

fn select_from_discard(
    state: &BattleState,
    min_count: usize,
    max_count: usize,
    source_id: &'static str,
    choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
) -> Result<SelectResult, KernelError> {
    if hooks::is_ending(state) || state.player_creature().is_none_or(|c| c.is_dead()) {
        return Ok(SelectResult::Selected(SmallVec::new()));
    }
    let candidates: SmallVec<[CardHandle; 32]> = state.player.discard.iter().copied().collect();
    card_select::select_cards(
        state,
        &candidates,
        min_count,
        max_count,
        ChoiceTiming::Action,
        source_id,
        PileType::Discard,
        false,
        choice_queue,
    )
}

#[inline]
fn resolve_x_value(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    ctx: &CardPlayContext,
) -> Result<i32, KernelError> {
    hooks::modify_x_value(state, content, ctx.card, ctx.energy_value)
}

#[inline]
fn rampage_damage(state: &BattleState, card: CardHandle) -> Dec {
    state
        .card(card)
        .and_then(|c| state.card_ext(c.ext_index))
        .and_then(|e| e.private_fields.get("Rampage._damage"))
        .and_then(|fv| match fv {
            FieldValue::Decimal(d) => Some(*d),
            _ => None,
        })
        .unwrap_or_else(|| Dec::from_i32(10))
}

#[inline]
fn set_rampage_damage(state: &mut BattleState, card: CardHandle, value: Dec) {
    if let Some(ext) = state.card_ext_mut(card) {
        ext.private_fields
            .insert(Cow::Borrowed("Rampage._damage"), FieldValue::Decimal(value));
    }
}

#[inline]
fn thrash_damage(state: &BattleState, card: CardHandle, upg_level: i32) -> Dec {
    state
        .card(card)
        .and_then(|c| state.card_ext(c.ext_index))
        .and_then(|e| e.private_fields.get("Thrash._damage"))
        .and_then(|fv| match fv {
            FieldValue::Decimal(d) => Some(*d),
            _ => None,
        })
        .unwrap_or_else(|| Dec::from_i32(4 + 2 * upg_level))
}

#[inline]
fn set_thrash_damage(state: &mut BattleState, card: CardHandle, value: Dec) {
    if let Some(ext) = state.card_ext_mut(card) {
        ext.private_fields
            .insert(Cow::Borrowed("Thrash._damage"), FieldValue::Decimal(value));
    }
}

fn perf_strike_count(state: &BattleState) -> i32 {
    count_strikes_in_combat(state)
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
    match card.id {
        CardId::Aggression => {
            apply_power(state, content, PowerId::AggressionPower, owner, Dec::ONE, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Anger => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 8 } else { 6 });
            attack_single(state, content, owner, ctx, target, dmg, 1)?;
            let clone_h = clone_card_in_combat(state, ctx.card)?;
            card_pile::enter_combat(
                state,
                content,
                clone_h,
                PileType::Discard,
                CardPilePosition::Bottom,
                true,
            )?;
            hooks::after_card_generated(state, content, clone_h, Some(owner))?;
            hooks::after_card_generated_for_combat(state, content, clone_h, Some(owner))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Armaments => {
            gain_block(state, content, owner, ctx, Dec::from_i32(5))?;
            if upg {
                let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
                for h in hand {
                    card_cmd::upgrade(state, content, h)?;
                }
                return Ok(CardPlayOutcome::default());
            }
            match select_from_hand_for_upgrade(
                state,
                content,
                card.id.model_id_str(),
                choice_queue,
            )? {
                SelectResult::Selected(selected) => {
                    if let Some(&h) = selected.first() {
                        card_cmd::upgrade(state, content, h)?;
                    }
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                    needs_choice: Some((req, 1)),
                    transform_into: None,
                }),
            }
        }
        CardId::AshenStrike => {
            let target = require_target(ctx, card.id)?;
            let extra = Dec::from_i32(if upg { 4 } else { 3 });
            let count = Dec::from_i32(state.player.exhaust.len() as i32);
            let dmg = Dec::from_i32(6).checked_add(extra.checked_mul(count)?)?;
            attack_single(state, content, owner, ctx, target, dmg, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Barricade => {
            apply_power(state, content, PowerId::BarricadePower, owner, Dec::ONE, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Bash => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 10 } else { 8 });
            let vuln = Dec::from_i32(if upg { 3 } else { 2 });
            attack_single(state, content, owner, ctx, target, dmg, 1)?;
            apply_power(state, content, PowerId::VulnerablePower, target, vuln, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::BattleTrance => {
            let cards = if upg { 4 } else { 3 };
            let _ = card_pile::draw(state, content, cards, false)?;
            apply_power(state, content, PowerId::NoDrawPower, owner, Dec::ONE, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Blaze => {
            let target = require_target(ctx, card.id)?;
            let str_amt = Dec::from_i32(if upg { 7 } else { 5 });
            apply_power(state, content, PowerId::StrengthPower, target, str_amt, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::BloodWall => {
            self_damage(state, content, owner, ctx, 2)?;
            let blk = Dec::from_i32(if upg { 20 } else { 16 });
            gain_block(state, content, owner, ctx, blk)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Bloodletting => {
            self_damage(state, content, owner, ctx, 3)?;
            let energy = Dec::from_i32(if upg { 3 } else { 2 });
            gold::gain_energy(state, energy)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Bludgeon => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 42 } else { 32 });
            attack_single(state, content, owner, ctx, target, dmg, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::BodySlam => {
            let target = require_target(ctx, card.id)?;
            let blk = state.creature(owner).map_or(0, |c| c.block);
            attack_single(state, content, owner, ctx, target, Dec::from_i32(blk), 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Brand => {
            self_damage(state, content, owner, ctx, 1)?;
            match select_from_hand(state, 1, 1, card.id.model_id_str(), choice_queue)? {
                SelectResult::Selected(selected) => {
                    if let Some(&h) = selected.first() {
                        card_pile::exhaust(state, content, h, false)?;
                    }
                    let str_amt = Dec::from_i32(if upg { 2 } else { 1 });
                    apply_power(state, content, PowerId::StrengthPower, owner, str_amt, owner, ctx.card)?;
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                    needs_choice: Some((req, 1)),
                    transform_into: None,
                }),
            }
        }
        CardId::Break => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 30 } else { 20 });
            let vuln = Dec::from_i32(if upg { 7 } else { 5 });
            attack_single(state, content, owner, ctx, target, dmg, 1)?;
            apply_power(state, content, PowerId::VulnerablePower, target, vuln, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Breakthrough => {
            self_damage(state, content, owner, ctx, 1)?;
            let dmg = Dec::from_i32(if upg { 13 } else { 9 });
            attack_all(state, content, owner, ctx, dmg, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Bully => {
            let target = require_target(ctx, card.id)?;
            let per_vuln = Dec::from_i32(if upg { 3 } else { 2 });
            let stacks = Dec::from_i32(power_amount(state, target, PowerId::VulnerablePower));
            let dmg = Dec::from_i32(4).checked_add(per_vuln.checked_mul(stacks)?)?;
            attack_single(state, content, owner, ctx, target, dmg, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::BurningPact => {
            match select_from_hand(state, 1, 1, card.id.model_id_str(), choice_queue)? {
                SelectResult::Selected(selected) => {
                    if let Some(&h) = selected.first() {
                        card_pile::exhaust(state, content, h, false)?;
                    }
                    let cards = if upg { 3 } else { 2 };
                    let _ = card_pile::draw(state, content, cards, false)?;
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                    needs_choice: Some((req, 1)),
                    transform_into: None,
                }),
            }
        }
        CardId::Cascade => {
            let x = resolve_x_value(content, state, ctx)?;
            let count = (x + if upg { 1 } else { 0 }).max(0) as usize;
            let _ = autoplay::from_top_of_draw_pile(state, content, count, false, choice_queue)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Cinder => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 24 } else { 18 });
            attack_single(state, content, owner, ctx, target, dmg, 1)?;
            let chosen = state
                .run_rngs
                .get_mut(RunRngType::CombatCardSelection)
                .next_item(&state.player.hand)
                .copied();
            if let Some(h) = chosen {
                card_pile::exhaust(state, content, h, false)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Colossus => {
            let blk = Dec::from_i32(if upg { 7 } else { 4 });
            gain_block(state, content, owner, ctx, blk)?;
            apply_power(state, content, PowerId::ColossusPower, owner, Dec::ONE, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Conflagration => {
            let repeats = if upg { 5 } else { 4 };
            attack_all(state, content, owner, ctx, Dec::TWO, repeats)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Corruption => {
            apply_power(state, content, PowerId::CorruptionPower, owner, Dec::ONE, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::CrimsonMantle => {
            let blk = Dec::from_i32(if upg { 10 } else { 7 });
            if let Some(idx) = apply_power(
                state,
                content,
                PowerId::CrimsonMantlePower,
                owner,
                blk,
                owner,
                ctx.card,
            )? {
                if let Some(ph) = state
                    .creature(owner)
                    .and_then(|c| c.powers.get(idx))
                    .map(|p| p.handle)
                {
                    crimson_mantle_increment_self_damage(state, owner, ph)?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Cruelty => {
            let bonus = Dec::from_i32(if upg { 50 } else { 25 });
            apply_power(state, content, PowerId::CrueltyPower, owner, bonus, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DarkEmbrace => {
            apply_power(state, content, PowerId::DarkEmbracePower, owner, Dec::ONE, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DefendIronclad => {
            let blk = Dec::from_i32(if upg { 8 } else { 5 });
            gain_block(state, content, owner, ctx, blk)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DemonForm => {
            let str_per_turn = Dec::from_i32(if upg { 4 } else { 3 });
            apply_power(
                state,
                content,
                PowerId::DemonFormPower,
                owner,
                str_per_turn,
                owner,
                ctx.card,
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DemonicShield => {
            let target = require_target(ctx, card.id)?;
            self_damage(state, content, owner, ctx, 1)?;
            let blk = Dec::from_i32(state.creature(owner).map_or(0, |c| c.block));
            gain_block(state, content, target, ctx, blk)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Dismantle => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 10 } else { 8 });
            let hits = if power_amount(state, target, PowerId::VulnerablePower) > 0 {
                2
            } else {
                1
            };
            attack_single(state, content, owner, ctx, target, dmg, hits)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Dominate => {
            let target = require_target(ctx, card.id)?;
            let vuln = Dec::from_i32(if upg { 2 } else { 1 });
            apply_power(state, content, PowerId::VulnerablePower, target, vuln, owner, ctx.card)?;
            let total_vuln = power_amount(state, target, PowerId::VulnerablePower);
            apply_power(
                state,
                content,
                PowerId::StrengthPower,
                owner,
                Dec::from_i32(total_vuln),
                owner,
                ctx.card,
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DrumOfBattle => {
            let _ = card_pile::draw(state, content, 2, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::EvilEye => {
            let blk = Dec::from_i32(if upg { 11 } else { 8 });
            let gains = if count_cards_exhausted_this_turn(state) > 0 {
                2
            } else {
                1
            };
            for _ in 0..gains {
                gain_block(state, content, owner, ctx, blk)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::ExpectAFight => {
            let blk = Dec::from_i32(if upg { 20 } else { 15 });
            gain_block(state, content, owner, ctx, blk)?;
            let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
            for h in hand {
                let Some(ci) = state.card(h) else {
                    continue;
                };
                let meta = content.card_meta(ci.id)?;
                if meta.card_type == CardType::Attack
                    && !meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST)
                {
                    add_local_energy_modifier(
                        state,
                        h,
                        LocalEnergyModifierKind::AddUntilPlayed,
                        -1,
                    );
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Feed => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 12 } else { 10 });
            let max_hp = Dec::from_i32(if upg { 4 } else { 3 });
            let can_trigger_fatal = hooks::should_owner_death_trigger_fatal(state, content, target)?;
            let results = attack_single(state, content, owner, ctx, target, dmg, 1)?;
            if can_trigger_fatal && results.iter().any(|r| r.was_target_killed) {
                creature::gain_max_hp(state, content, owner, max_hp)?;
                state
                    .semantic_history
                    .record_growth(sts2_kernel::GrowthSource::Feed);
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::FeelNoPain => {
            let amt = Dec::from_i32(if upg { 4 } else { 3 });
            apply_power(state, content, PowerId::FeelNoPainPower, owner, amt, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::FiendFire => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 10 } else { 7 });
            let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
            for &h in &hand {
                card_pile::exhaust(state, content, h, false)?;
            }
            attack_single(state, content, owner, ctx, target, dmg, hand.len() as i32)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::FightMe => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 6 } else { 5 });
            let str_amt = Dec::from_i32(if upg { 4 } else { 3 });
            attack_single(state, content, owner, ctx, target, dmg, 2)?;
            apply_power(state, content, PowerId::StrengthPower, owner, str_amt, owner, ctx.card)?;
            apply_power(state, content, PowerId::StrengthPower, target, Dec::ONE, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::FlameBarrier => {
            let blk = Dec::from_i32(if upg { 16 } else { 12 });
            let dmg_back = Dec::from_i32(if upg { 6 } else { 4 });
            gain_block(state, content, owner, ctx, blk)?;
            apply_power(
                state,
                content,
                PowerId::FlameBarrierPower,
                owner,
                dmg_back,
                owner,
                ctx.card,
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::ForgottenRitual => {
            if count_cards_exhausted_this_turn(state) > 0 {
                let energy = Dec::from_i32(if upg { 4 } else { 3 });
                gold::gain_energy(state, energy)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Havoc => {
            let _ = autoplay::from_top_of_draw_pile(state, content, 1, true, choice_queue)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Headbutt => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 12 } else { 9 });
            attack_single(state, content, owner, ctx, target, dmg, 1)?;
            match select_from_discard(state, 1, 1, card.id.model_id_str(), choice_queue)? {
                SelectResult::Selected(selected) => {
                    if let Some(&chosen) = selected.first() {
                        card_pile::add_sync(state, chosen, PileType::Draw, CardPilePosition::Top);
                    }
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                    needs_choice: Some((req, 1)),
                    transform_into: None,
                }),
            }
        }
        CardId::Hellraiser => {
            apply_power(state, content, PowerId::HellraiserPower, owner, Dec::ONE, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Hemokinesis => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 20 } else { 15 });
            self_damage(state, content, owner, ctx, 2)?;
            attack_single(state, content, owner, ctx, target, dmg, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::HowlFromBeyond => {
            let dmg = Dec::from_i32(if upg { 24 } else { 18 });
            attack_all(state, content, owner, ctx, dmg, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Impervious => {
            let blk = Dec::from_i32(if upg { 40 } else { 30 });
            gain_block(state, content, owner, ctx, blk)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::InfernalBlade => {
            let pool = card_generation_pool(state);
            let picked = card_factory::get_distinct_for_combat_filtered(
                state,
                &pool,
                1,
                RunRngType::CombatCardGeneration,
                |id| generated_card_meta(id).card_type == CardType::Attack,
            );
            if let Some(&gen_id) = picked.first() {
                let h = card_pile::create_card_in_combat(state, content, gen_id, 0)?;
                set_to_free_this_turn(state, h);
                card_pile::enter_combat(
                    state,
                    content,
                    h,
                    PileType::Hand,
                    CardPilePosition::Bottom,
                    true,
                )?;
                hooks::after_card_generated(state, content, h, None)?;
                hooks::after_card_generated_for_combat(state, content, h, Some(owner))?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Inferno => {
            let dmg = Dec::from_i32(if upg { 9 } else { 6 });
            if let Some(idx) = apply_power(
                state,
                content,
                PowerId::InfernoPower,
                owner,
                dmg,
                owner,
                ctx.card,
            )? {
                if let Some(ph) = state
                    .creature(owner)
                    .and_then(|c| c.powers.get(idx))
                    .map(|p| p.handle)
                {
                    inferno_increment_self_damage(state, owner, ph)?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Inflame => {
            let str_amt = Dec::from_i32(if upg { 3 } else { 2 });
            apply_power(state, content, PowerId::StrengthPower, owner, str_amt, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::IronWave => {
            let target = require_target(ctx, card.id)?;
            let amt = Dec::from_i32(if upg { 7 } else { 5 });
            gain_block(state, content, owner, ctx, amt)?;
            attack_single(state, content, owner, ctx, target, amt, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Juggernaut => {
            let dmg = Dec::from_i32(if upg { 8 } else { 6 });
            apply_power(state, content, PowerId::JuggernautPower, owner, dmg, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Juggling => {
            apply_power(state, content, PowerId::JugglingPower, owner, Dec::ONE, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Mangle => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 26 } else { 20 });
            let str_loss = Dec::from_i32(if upg { 15 } else { 10 });
            attack_single(state, content, owner, ctx, target, dmg, 1)?;
            apply_power(state, content, PowerId::ManglePower, target, str_loss, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Midnight => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 72 } else { 60 });
            attack_single(state, content, owner, ctx, target, dmg, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::MoltenFist => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 14 } else { 10 });
            attack_single(state, content, owner, ctx, target, dmg, 1)?;
            if state.creature(target).is_some_and(|c| c.is_alive()) {
                let vuln = power_amount(state, target, PowerId::VulnerablePower);
                if vuln > 0 {
                    apply_power(
                        state,
                        content,
                        PowerId::VulnerablePower,
                        target,
                        Dec::from_i32(vuln),
                        owner,
                        ctx.card,
                    )?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::NotYet => {
            let heal = Dec::from_i32(if upg { 13 } else { 10 });
            creature::heal(state, content, owner, heal)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Offering => {
            self_damage(state, content, owner, ctx, 6)?;
            gold::gain_energy(state, Dec::TWO)?;
            let cards = if upg { 5 } else { 3 };
            let _ = card_pile::draw(state, content, cards, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::OneTwoPunch => {
            let attacks = Dec::from_i32(if upg { 2 } else { 1 });
            apply_power(state, content, PowerId::OneTwoPunchPower, owner, attacks, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Outrage => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 13 } else { 9 });
            attack_single(state, content, owner, ctx, target, dmg, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::PactsEnd => {
            if state.player.exhaust.len() >= 3 {
                let dmg = Dec::from_i32(if upg { 24 } else { 18 });
                attack_all(state, content, owner, ctx, dmg, 1)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::PerfectedStrike => {
            let target = require_target(ctx, card.id)?;
            let extra = Dec::from_i32(if upg { 3 } else { 2 });
            let strikes = Dec::from_i32(count_strikes_in_combat(state));
            let dmg = Dec::from_i32(6).checked_add(extra.checked_mul(strikes)?)?;
            attack_single(state, content, owner, ctx, target, dmg, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Pillage => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 9 } else { 6 });
            attack_single(state, content, owner, ctx, target, dmg, 1)?;
            loop {
                let drawn = card_pile::draw(state, content, 1, false)?;
                let Some(&dh) = drawn.first() else {
                    break;
                };
                let Some(dc) = state.card(dh) else {
                    break;
                };
                if content.card_meta(dc.id)?.card_type != CardType::Attack
                    || state.player.hand.len() >= card_pile::MAX_CARDS_IN_HAND
                {
                    break;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::PommelStrike => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 10 } else { 9 });
            let cards = if upg { 2 } else { 1 };
            attack_single(state, content, owner, ctx, target, dmg, 1)?;
            let _ = card_pile::draw(state, content, cards, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::PrimalForce => {
            let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
            let upg_level = if upg { 1 } else { 0 };
            for h in hand {
                let Some(ci) = state.card(h) else {
                    continue;
                };
                if content.card_meta(ci.id)?.card_type == CardType::Attack {
                    let _ = card_cmd::transform_with_upgrade(
                        state,
                        content,
                        h,
                        CardId::GiantRock,
                        upg_level,
                    )?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Pyre => {
            let energy = Dec::from_i32(if upg { 2 } else { 1 });
            apply_power(state, content, PowerId::PyrePower, owner, energy, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Rage => {
            let blk = Dec::from_i32(if upg { 5 } else { 3 });
            apply_power(state, content, PowerId::RagePower, owner, blk, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Rampage => {
            let target = require_target(ctx, card.id)?;
            let cur_dmg = rampage_damage(state, ctx.card);
            attack_single(state, content, owner, ctx, target, cur_dmg, 1)?;
            let inc = Dec::from_i32(if upg { 10 } else { 5 });
            set_rampage_damage(state, ctx.card, cur_dmg.checked_add(inc)?);
            Ok(CardPlayOutcome::default())
        }
        CardId::Rupture => {
            let str_amt = Dec::from_i32(if upg { 2 } else { 1 });
            apply_power(state, content, PowerId::RupturePower, owner, str_amt, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SecondWind => {
            let blk = Dec::from_i32(if upg { 7 } else { 5 });
            let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
            for h in hand {
                let Some(ci) = state.card(h) else {
                    continue;
                };
                if content.card_meta(ci.id)?.card_type != CardType::Attack {
                    card_pile::exhaust(state, content, h, false)?;
                    gain_block(state, content, owner, ctx, blk)?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::SetupStrike => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 9 } else { 7 });
            let str_amt = Dec::from_i32(if upg { 4 } else { 3 });
            attack_single(state, content, owner, ctx, target, dmg, 1)?;
            apply_power(state, content, PowerId::SetupStrikePower, owner, str_amt, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::ShrugItOff => {
            let blk = Dec::from_i32(if upg { 11 } else { 8 });
            gain_block(state, content, owner, ctx, blk)?;
            let _ = card_pile::draw(state, content, 1, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Spite => {
            let target = require_target(ctx, card.id)?;
            let repeat = if upg { 3 } else { 2 };
            let hits = if lost_hp_this_turn(state, owner) {
                repeat
            } else {
                1
            };
            attack_single(state, content, owner, ctx, target, Dec::from_i32(5), hits)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Stampede => {
            apply_power(state, content, PowerId::StampedePower, owner, Dec::ONE, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Stoke => {
            let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
            for &h in &hand {
                card_pile::exhaust(state, content, h, false)?;
            }
            let pool = card_generation_pool(state);
            let generated = card_factory::get_for_combat(
                state,
                &pool,
                hand.len(),
                RunRngType::CombatCardGeneration,
            );
            let upg_level = if upg { 1 } else { 0 };
            for gen_id in generated {
                let h = card_pile::create_card_in_combat(state, content, gen_id, upg_level)?;
                card_pile::enter_combat(
                    state,
                    content,
                    h,
                    PileType::Hand,
                    CardPilePosition::Bottom,
                    true,
                )?;
                hooks::after_card_generated(state, content, h, None)?;
                hooks::after_card_generated_for_combat(state, content, h, Some(owner))?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Stomp => {
            let dmg = Dec::from_i32(if upg { 15 } else { 12 });
            attack_all(state, content, owner, ctx, dmg, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::StoneArmor => {
            let plating = Dec::from_i32(if upg { 6 } else { 4 });
            apply_power(state, content, PowerId::PlatingPower, owner, plating, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::StrikeIronclad => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 9 } else { 6 });
            attack_single(state, content, owner, ctx, target, dmg, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SwordBoomerang => {
            let repeat = if upg { 4 } else { 3 };
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(3),
                AttackTargetSpec::RandomOpponents {
                    allow_duplicates: true,
                },
            )
            .with_hit_count(repeat)
            .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Tank => {
            apply_power(state, content, PowerId::TankPower, owner, Dec::ONE, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Taunt => {
            let target = require_target(ctx, card.id)?;
            let blk = Dec::from_i32(if upg { 7 } else { 6 });
            let vuln = Dec::from_i32(if upg { 2 } else { 1 });
            gain_block(state, content, owner, ctx, blk)?;
            apply_power(state, content, PowerId::VulnerablePower, target, vuln, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::TearAsunder => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 7 } else { 5 });
            let hits = 1 + count_unblocked_hp_loss_hits(state, owner);
            attack_single(state, content, owner, ctx, target, dmg, hits)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Thrash => {
            let target = require_target(ctx, card.id)?;
            let cur_dmg = thrash_damage(state, ctx.card, card.upgrade_level);
            attack_single(state, content, owner, ctx, target, cur_dmg, 2)?;
            let mut attacks: SmallVec<[CardHandle; 10]> = SmallVec::new();
            for &h in &state.player.hand {
                if let Some(ci) = state.card(h) {
                    if content.card_meta(ci.id)?.card_type == CardType::Attack {
                        attacks.push(h);
                    }
                }
            }
            let picked = state
                .run_rngs
                .get_mut(RunRngType::CombatCardSelection)
                .next_item(&attacks)
                .copied();
            if let Some(selected) = picked {
                let base_val = content.card_thrash_damage_variable(state, selected)?;
                let mod_val = hooks::modify_damage(
                    state,
                    content,
                    None,
                    Some(owner),
                    base_val,
                    ValueProp::MOVE,
                    Some(selected),
                    None,
                )?;
                let next_dmg = cur_dmg.checked_add(mod_val)?;
                set_thrash_damage(state, ctx.card, next_dmg);
                card_pile::exhaust(state, content, selected, false)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Thunderclap => {
            let dmg = Dec::from_i32(if upg { 7 } else { 4 });
            attack_all(state, content, owner, ctx, dmg, 1)?;
            let enemies = hittable_enemies(state, content)?;
            for enemy in enemies {
                apply_power(
                    state,
                    content,
                    PowerId::VulnerablePower,
                    enemy,
                    Dec::ONE,
                    owner,
                    ctx.card,
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Tremble => {
            let target = require_target(ctx, card.id)?;
            let vuln = Dec::from_i32(if upg { 4 } else { 3 });
            apply_power(state, content, PowerId::VulnerablePower, target, vuln, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::TrueGrit => {
            let blk = Dec::from_i32(if upg { 9 } else { 7 });
            gain_block(state, content, owner, ctx, blk)?;
            if upg {
                match select_from_hand(state, 1, 1, card.id.model_id_str(), choice_queue)? {
                    SelectResult::Selected(selected) => {
                        if let Some(&h) = selected.first() {
                            card_pile::exhaust(state, content, h, false)?;
                        }
                        Ok(CardPlayOutcome::default())
                    }
                    SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                        needs_choice: Some((req, 1)),
                        transform_into: None,
                    }),
                }
            } else {
                let chosen = state
                    .run_rngs
                    .get_mut(RunRngType::CombatCardSelection)
                    .next_item(&state.player.hand)
                    .copied();
                if let Some(h) = chosen {
                    card_pile::exhaust(state, content, h, false)?;
                }
                Ok(CardPlayOutcome::default())
            }
        }
        CardId::TwinStrike => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 7 } else { 5 });
            attack_single(state, content, owner, ctx, target, dmg, 2)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Unmovable => {
            apply_power(state, content, PowerId::UnmovablePower, owner, Dec::ONE, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Unrelenting => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 20 } else { 14 });
            attack_single(state, content, owner, ctx, target, dmg, 1)?;
            apply_power(state, content, PowerId::FreeAttackPower, owner, Dec::ONE, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Uppercut => {
            let target = require_target(ctx, card.id)?;
            let amt = Dec::from_i32(if upg { 2 } else { 1 });
            attack_single(state, content, owner, ctx, target, Dec::from_i32(13), 1)?;
            apply_power(state, content, PowerId::WeakPower, target, amt, owner, ctx.card)?;
            apply_power(state, content, PowerId::VulnerablePower, target, amt, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Vicious => {
            let cards = Dec::from_i32(if upg { 2 } else { 1 });
            apply_power(state, content, PowerId::ViciousPower, owner, cards, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Whirlwind => {
            let dmg = Dec::from_i32(if upg { 8 } else { 5 });
            let hits = resolve_x_value(content, state, ctx)?;
            attack_all(state, content, owner, ctx, dmg, hits)?;
            Ok(CardPlayOutcome::default())
        }
        _ => Err(KernelError::Unsupported {
            kind: "Card",
            id: card.id.model_id_str().to_owned(),
        }),
    }
}

pub fn on_play_resume(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    ctx: &CardPlayContext,
    selected: &[CardHandle],
    _resume_step: u8,
) -> Result<CardPlayOutcome, KernelError> {
    let card = *state
        .card(ctx.card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?;
    let upg = card.upgrade_level > 0;
    let owner = state.player.creature_handle;
    match card.id {
        CardId::Armaments => {
            if let Some(&h) = selected.first() {
                card_cmd::upgrade(state, content, h)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Brand => {
            if let Some(&h) = selected.first() {
                card_pile::exhaust(state, content, h, false)?;
            }
            let str_amt = Dec::from_i32(if upg { 2 } else { 1 });
            apply_power(state, content, PowerId::StrengthPower, owner, str_amt, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::BurningPact => {
            if let Some(&h) = selected.first() {
                card_pile::exhaust(state, content, h, false)?;
            }
            let cards = if upg { 3 } else { 2 };
            let _ = card_pile::draw(state, content, cards, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Headbutt => {
            if let Some(&chosen) = selected.first() {
                card_pile::add_sync(state, chosen, PileType::Draw, CardPilePosition::Top);
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::TrueGrit => {
            if let Some(&h) = selected.first() {
                card_pile::exhaust(state, content, h, false)?;
            }
            Ok(CardPlayOutcome::default())
        }
        _ => Err(KernelError::Unsupported {
            kind: "CardResume",
            id: card.id.model_id_str().to_owned(),
        }),
    }
}

pub fn on_upgrade(state: &mut BattleState, card: CardHandle) -> Result<(), KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    let _ = card_meta(id)?;
    match id {
        CardId::Aggression | CardId::Juggling => {
            if let Some(c) = state.card_mut(card) {
                c.local_keywords.insert(CardKeywords::INNATE);
            }
        }
        CardId::DemonicShield => {
            if let Some(c) = state.card_mut(card) {
                c.local_keywords.remove(CardKeywords::EXHAUST);
            }
        }
        CardId::Barricade
        | CardId::BodySlam
        | CardId::Corruption
        | CardId::DarkEmbrace
        | CardId::Havoc
        | CardId::Hellraiser
        | CardId::InfernalBlade
        | CardId::Stampede
        | CardId::Tank
        | CardId::Unmovable => {
            if let Some(c) = state.card_mut(card) {
                c.base_energy_cost = (c.base_energy_cost - 1).max(0);
            }
        }
        CardId::Thrash => {
            let ext_idx = state.card(card).and_then(|c| c.ext_index);
            if let Some(idx) = ext_idx {
                if let Some(FieldValue::Decimal(d)) =
                    state.card_extensions[idx as usize].private_fields.get_mut("Thrash._damage")
                {
                    *d = d.checked_add(Dec::TWO)?;
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
        CardId::Rampage => out.push(("Damage", rampage_damage(state, card))),
        CardId::Thrash => out.push(("Damage", thrash_damage(state, card, ci.upgrade_level))),
        _ => {}
    }
    Ok(out)
}

pub fn dispatch_hook(
    content: &dyn ContentDispatcher,
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
        CardId::DrumOfBattle => {
            if slot == HookSlot::AfterCardExhausted {
                if let HookArgs::CardExhausted {
                    card: exhausted_card,
                    ..
                } = args
                {
                    if *exhausted_card == card && state.is_live_combat() {
                        let base_count = (ci.base_replay_count as i32) + 1;
                        let has_ench = state
                            .card_ext(ci.ext_index)
                            .and_then(|e| e.enchantment.as_ref())
                            .is_some();
                        let enchanted_count = if has_ench {
                            content.enchantment_play_count(state, card, base_count)?
                        } else {
                            base_count
                        };
                        let (play_count, modifiers) = hooks::modify_card_play_count(
                            state,
                            content,
                            card,
                            None,
                            enchanted_count,
                        )?;
                        hooks::after_modifying_card_play_count(
                            state,
                            content,
                            card,
                            &modifiers,
                        )?;
                        let energy = Dec::from_i32(if ci.upgrade_level > 0 { 3 } else { 2 });
                        for _ in 0..play_count {
                            gold::gain_energy(state, energy)?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        CardId::HowlFromBeyond => {
            if slot == HookSlot::AfterAutoPostPlayPhaseEntered
                && ci.pile == PileType::Exhaust
            {
                let _ = autoplay::from_cards(state, content, &[card], None, false)?;
            }
            Ok(HookResult::default())
        }
        CardId::Midnight => {
            match slot {
                HookSlot::AfterCardEnteredCombat => {
                    if let HookArgs::CardOnly(entered) = args {
                        if *entered == card && ci.clone_of_handle.is_none() {
                            let exhausted = state.semantic_history.prior_exhaust_count
                                + state.semantic_history.exhausts.len() as i32;
                            add_local_energy_modifier(
                                state,
                                card,
                                LocalEnergyModifierKind::AddThisCombat,
                                -exhausted,
                            );
                        }
                    }
                }
                HookSlot::AfterCardExhausted => {
                    add_local_energy_modifier(
                        state,
                        card,
                        LocalEnergyModifierKind::AddThisCombat,
                        -1,
                    );
                }
                _ => {}
            }
            Ok(HookResult::default())
        }
        CardId::Stomp => {
            match slot {
                HookSlot::AfterCardEnteredCombat => {
                    if let HookArgs::CardOnly(entered) = args {
                        if *entered == card && ci.clone_of_handle.is_none() {
                            let attacks = state.player.counters.attack_cards_played_this_turn;
                            add_local_energy_modifier(
                                state,
                                card,
                                LocalEnergyModifierKind::AddThisTurn,
                                -attacks,
                            );
                        }
                    }
                }
                HookSlot::BeforeCardPlayed => {
                    if let HookArgs::CardPlay(cp) = args {
                        if let Some(played) = state.card(cp.card) {
                            if content.card_meta(played.id)?.card_type == CardType::Attack {
                                add_local_energy_modifier(
                                    state,
                                    card,
                                    LocalEnergyModifierKind::AddThisTurn,
                                    -1,
                                );
                            }
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }
        _ => Err(KernelError::Unsupported {
            kind: "CardHook",
            id: ci.id.model_id_str().to_owned(),
        }),
    }
}

pub fn on_turn_end_in_hand(
    _content: &dyn ContentDispatcher,
    state: &mut BattleState,
    card: CardHandle,
) -> Result<(), KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    if card_meta(id)?
        .flags
        .contains(sts2_types::CardStaticFlags::HAS_TURN_END_IN_HAND)
    {
        return Err(KernelError::Unsupported {
            kind: "CardTurnEndInHand",
            id: id.model_id_str().to_owned(),
        });
    }
    Ok(())
}

pub fn resolved_type(state: &BattleState, card: CardHandle) -> Result<sts2_types::CardType, KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
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
    Ok(card_meta(id)?.target_type)
}

pub fn thrash_damage_variable(state: &BattleState, card: CardHandle) -> Result<Dec, KernelError> {
    let c = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?;
    let _ = card_meta(c.id)?;
    let upg = c.upgrade_level > 0;
    match c.id {
        CardId::Anger => Ok(Dec::from_i32(if upg { 8 } else { 6 })),
        CardId::AshenStrike => {
            let extra = Dec::from_i32(if upg { 4 } else { 3 });
            let count = Dec::from_i32(state.player.exhaust.len() as i32);
            Ok(Dec::from_i32(6).checked_add(extra.checked_mul(count)?)?)
        }
        CardId::Bash => Ok(Dec::from_i32(if upg { 10 } else { 8 })),
        CardId::Bludgeon => Ok(Dec::from_i32(if upg { 42 } else { 32 })),
        CardId::BodySlam => Ok(Dec::from_i32(
            state.player_creature().map_or(0, |cr| cr.block),
        )),
        CardId::Break => Ok(Dec::from_i32(if upg { 30 } else { 20 })),
        CardId::Breakthrough => Ok(Dec::from_i32(if upg { 13 } else { 9 })),
        CardId::Bully => Ok(Dec::from_i32(4)),
        CardId::Cinder => Ok(Dec::from_i32(if upg { 24 } else { 18 })),
        CardId::Conflagration => Ok(Dec::TWO),
        CardId::Dismantle => Ok(Dec::from_i32(if upg { 10 } else { 8 })),
        CardId::Feed => Ok(Dec::from_i32(if upg { 12 } else { 10 })),
        CardId::FiendFire => Ok(Dec::from_i32(if upg { 10 } else { 7 })),
        CardId::FightMe => Ok(Dec::from_i32(if upg { 6 } else { 5 })),
        CardId::Headbutt => Ok(Dec::from_i32(if upg { 12 } else { 9 })),
        CardId::Hemokinesis => Ok(Dec::from_i32(if upg { 20 } else { 15 })),
        CardId::HowlFromBeyond => Ok(Dec::from_i32(if upg { 24 } else { 18 })),
        CardId::IronWave => Ok(Dec::from_i32(if upg { 7 } else { 5 })),
        CardId::Mangle => Ok(Dec::from_i32(if upg { 26 } else { 20 })),
        CardId::Midnight => Ok(Dec::from_i32(if upg { 72 } else { 60 })),
        CardId::MoltenFist => Ok(Dec::from_i32(if upg { 14 } else { 10 })),
        CardId::Outrage => Ok(Dec::from_i32(if upg { 13 } else { 9 })),
        CardId::PactsEnd => Ok(Dec::from_i32(if upg { 24 } else { 18 })),
        CardId::PerfectedStrike => {
            let extra = Dec::from_i32(if upg { 3 } else { 2 });
            let strikes = Dec::from_i32(perf_strike_count(state));
            Ok(Dec::from_i32(6).checked_add(extra.checked_mul(strikes)?)?)
        }
        CardId::Pillage => Ok(Dec::from_i32(if upg { 9 } else { 6 })),
        CardId::PommelStrike => Ok(Dec::from_i32(if upg { 10 } else { 9 })),
        CardId::Rampage => Ok(rampage_damage(state, card)),
        CardId::SetupStrike => Ok(Dec::from_i32(if upg { 9 } else { 7 })),
        CardId::Spite => Ok(Dec::from_i32(5)),
        CardId::Stomp => Ok(Dec::from_i32(if upg { 15 } else { 12 })),
        CardId::StrikeIronclad => Ok(Dec::from_i32(if upg { 9 } else { 6 })),
        CardId::SwordBoomerang => Ok(Dec::from_i32(3)),
        CardId::TearAsunder => Ok(Dec::from_i32(if upg { 7 } else { 5 })),
        CardId::Thrash => Ok(thrash_damage(state, card, c.upgrade_level)),
        CardId::Thunderclap => Ok(Dec::from_i32(if upg { 7 } else { 4 })),
        CardId::TwinStrike => Ok(Dec::from_i32(if upg { 7 } else { 5 })),
        CardId::Unrelenting => Ok(Dec::from_i32(if upg { 20 } else { 14 })),
        CardId::Uppercut => Ok(Dec::from_i32(13)),
        CardId::Whirlwind => Ok(Dec::from_i32(if upg { 8 } else { 5 })),
        _ => Ok(Dec::ZERO),
    }
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
