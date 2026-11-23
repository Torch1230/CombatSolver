pub mod colorless_special;
pub mod defect;
pub mod fool;
pub mod ironclad;
pub mod necrobinder;
pub mod regent;
pub mod silent;

use smallvec::SmallVec;
use sts2_kernel::content_dispatch::{CardPlayContext, CardPlayOutcome};
use sts2_kernel::{BattleState, CardHandle, ContentDispatcher, HookArgs, HookResult, KernelError};
use sts2_types::{
    CardId, CardMeta, CardPartition, CardPilePosition, CardType, Dec, HookSlot, PileType,
    TargetType, card_partition,
};

pub fn card_meta(id: CardId) -> Result<&'static CardMeta, KernelError> {
    match card_partition(id) {
        CardPartition::Ironclad => ironclad::card_meta(id),
        CardPartition::Silent => silent::card_meta(id),
        CardPartition::Defect => defect::card_meta(id),
        CardPartition::Regent => regent::card_meta(id),
        CardPartition::Necrobinder => necrobinder::card_meta(id),
        CardPartition::Fool => fool::card_meta(id),
        CardPartition::ColorlessSpecial => colorless_special::card_meta(id),
    }
}

pub fn card_is_playable(state: &BattleState, card: CardHandle) -> Result<bool, KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    match card_partition(id) {
        CardPartition::Ironclad => ironclad::is_playable(state, card),
        CardPartition::Silent => silent::is_playable(state, card),
        CardPartition::Defect => defect::is_playable(state, card),
        CardPartition::Regent => regent::is_playable(state, card),
        CardPartition::Necrobinder => necrobinder::is_playable(state, card),
        CardPartition::Fool => fool::is_playable(state, card),
        CardPartition::ColorlessSpecial => colorless_special::is_playable(state, card),
    }
}

pub fn card_on_play(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    ctx: &CardPlayContext,
    choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
    resume_step: u8,
) -> Result<CardPlayOutcome, KernelError> {
    let id = state
        .card(ctx.card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    match card_partition(id) {
        CardPartition::Ironclad => ironclad::on_play(content, state, ctx, choice_queue, resume_step),
        CardPartition::Silent => silent::on_play(content, state, ctx, choice_queue, resume_step),
        CardPartition::Defect => defect::on_play(content, state, ctx, choice_queue, resume_step),
        CardPartition::Regent => regent::on_play(content, state, ctx, choice_queue, resume_step),
        CardPartition::Necrobinder => {
            necrobinder::on_play(content, state, ctx, choice_queue, resume_step)
        }
        CardPartition::Fool => fool::on_play(content, state, ctx, choice_queue, resume_step),
        CardPartition::ColorlessSpecial => {
            colorless_special::on_play(content, state, ctx, choice_queue, resume_step)
        }
    }
}

pub fn card_on_play_resume_after_select(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    ctx: &CardPlayContext,
    selected: &[CardHandle],
    resume_step: u8,
) -> Result<CardPlayOutcome, KernelError> {
    let id = state
        .card(ctx.card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    match card_partition(id) {
        CardPartition::Ironclad => {
            ironclad::on_play_resume(content, state, ctx, selected, resume_step)
        }
        CardPartition::Silent => silent::on_play_resume(content, state, ctx, selected, resume_step),
        CardPartition::Defect => defect::on_play_resume(content, state, ctx, selected, resume_step),
        CardPartition::Regent => regent::on_play_resume(content, state, ctx, selected, resume_step),
        CardPartition::Necrobinder => {
            necrobinder::on_play_resume(content, state, ctx, selected, resume_step)
        }
        CardPartition::Fool => fool::on_play_resume(content, state, ctx, selected, resume_step),
        CardPartition::ColorlessSpecial => {
            colorless_special::on_play_resume(content, state, ctx, selected, resume_step)
        }
    }
}

pub fn card_on_upgrade(state: &mut BattleState, card: CardHandle) -> Result<(), KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    match card_partition(id) {
        CardPartition::Ironclad => ironclad::on_upgrade(state, card),
        CardPartition::Silent => silent::on_upgrade(state, card),
        CardPartition::Defect => defect::on_upgrade(state, card),
        CardPartition::Regent => regent::on_upgrade(state, card),
        CardPartition::Necrobinder => necrobinder::on_upgrade(state, card),
        CardPartition::Fool => fool::on_upgrade(state, card),
        CardPartition::ColorlessSpecial => colorless_special::on_upgrade(state, card),
    }
}

pub fn card_comparison_base_values(
    state: &BattleState,
    card: CardHandle,
) -> Result<SmallVec<[(&'static str, Dec); 4]>, KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    match card_partition(id) {
        CardPartition::Ironclad => ironclad::comparison_base_values(state, card),
        CardPartition::Silent => silent::comparison_base_values(state, card),
        CardPartition::Defect => defect::comparison_base_values(state, card),
        CardPartition::Regent => regent::comparison_base_values(state, card),
        CardPartition::Necrobinder => necrobinder::comparison_base_values(state, card),
        CardPartition::Fool => fool::comparison_base_values(state, card),
        CardPartition::ColorlessSpecial => colorless_special::comparison_base_values(state, card),
    }
}

pub fn dispatch_hook(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    card: CardHandle,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    match card_partition(id) {
        CardPartition::Ironclad => ironclad::dispatch_hook(content, state, card, slot, args),
        CardPartition::Silent => silent::dispatch_hook(content, state, card, slot, args),
        CardPartition::Defect => defect::dispatch_hook(content, state, card, slot, args),
        CardPartition::Regent => regent::dispatch_hook(content, state, card, slot, args),
        CardPartition::Necrobinder => necrobinder::dispatch_hook(content, state, card, slot, args),
        CardPartition::Fool => fool::dispatch_hook(content, state, card, slot, args),
        CardPartition::ColorlessSpecial => {
            colorless_special::dispatch_hook(content, state, card, slot, args)
        }
    }
}

pub fn card_on_turn_end_in_hand(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    card: CardHandle,
) -> Result<(), KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    match card_partition(id) {
        CardPartition::Ironclad => ironclad::on_turn_end_in_hand(content, state, card),
        CardPartition::Silent => silent::on_turn_end_in_hand(content, state, card),
        CardPartition::Defect => defect::on_turn_end_in_hand(content, state, card),
        CardPartition::Regent => regent::on_turn_end_in_hand(content, state, card),
        CardPartition::Necrobinder => necrobinder::on_turn_end_in_hand(content, state, card),
        CardPartition::Fool => fool::on_turn_end_in_hand(content, state, card),
        CardPartition::ColorlessSpecial => {
            colorless_special::on_turn_end_in_hand(content, state, card)
        }
    }
}

pub fn card_resolved_type(state: &BattleState, card: CardHandle) -> Result<CardType, KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    match card_partition(id) {
        CardPartition::Ironclad => ironclad::resolved_type(state, card),
        CardPartition::Silent => silent::resolved_type(state, card),
        CardPartition::Defect => defect::resolved_type(state, card),
        CardPartition::Regent => regent::resolved_type(state, card),
        CardPartition::Necrobinder => necrobinder::resolved_type(state, card),
        CardPartition::Fool => fool::resolved_type(state, card),
        CardPartition::ColorlessSpecial => colorless_special::resolved_type(state, card),
    }
}

pub fn card_resolved_target_type(
    state: &BattleState,
    card: CardHandle,
) -> Result<TargetType, KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    match card_partition(id) {
        CardPartition::Ironclad => ironclad::resolved_target_type(state, card),
        CardPartition::Silent => silent::resolved_target_type(state, card),
        CardPartition::Defect => defect::resolved_target_type(state, card),
        CardPartition::Regent => regent::resolved_target_type(state, card),
        CardPartition::Necrobinder => necrobinder::resolved_target_type(state, card),
        CardPartition::Fool => fool::resolved_target_type(state, card),
        CardPartition::ColorlessSpecial => colorless_special::resolved_target_type(state, card),
    }
}

pub fn card_thrash_damage_variable(
    state: &BattleState,
    card: CardHandle,
) -> Result<Dec, KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    match card_partition(id) {
        CardPartition::Ironclad => ironclad::thrash_damage_variable(state, card),
        CardPartition::Silent => silent::thrash_damage_variable(state, card),
        CardPartition::Defect => defect::thrash_damage_variable(state, card),
        CardPartition::Regent => regent::thrash_damage_variable(state, card),
        CardPartition::Necrobinder => necrobinder::thrash_damage_variable(state, card),
        CardPartition::Fool => fool::thrash_damage_variable(state, card),
        CardPartition::ColorlessSpecial => colorless_special::thrash_damage_variable(state, card),
    }
}

pub fn card_default_result_location(
    state: &BattleState,
    card: CardHandle,
    is_auto_play: bool,
) -> Result<Option<(PileType, CardPilePosition)>, KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    match card_partition(id) {
        CardPartition::Ironclad => ironclad::default_result_location(state, card, is_auto_play),
        CardPartition::Silent => silent::default_result_location(state, card, is_auto_play),
        CardPartition::Defect => defect::default_result_location(state, card, is_auto_play),
        CardPartition::Regent => regent::default_result_location(state, card, is_auto_play),
        CardPartition::Necrobinder => {
            necrobinder::default_result_location(state, card, is_auto_play)
        }
        CardPartition::Fool => fool::default_result_location(state, card, is_auto_play),
        CardPartition::ColorlessSpecial => {
            colorless_special::default_result_location(state, card, is_auto_play)
        }
    }
}
