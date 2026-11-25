use smallvec::SmallVec;
use std::borrow::Cow;
use sts2_kernel::commands::card_select::SelectResult;
use sts2_kernel::commands::damage::{AttackBuilder, AttackTargetSpec};
use sts2_kernel::commands::{
    arrow, autoplay, block, card as card_cmd, card_factory, card_pile, card_select, damage, gold,
    melody, power,
};
use sts2_kernel::content_dispatch::{AttackContextInfo, CardPlayContext, CardPlayOutcome};
use sts2_kernel::continuation::ChoiceTiming;
use sts2_kernel::hooks;
use sts2_kernel::{
    BattleState, CardHandle, ContentDispatcher, CreatureHandle, DamageResult, FieldValue, HookArgs,
    HookResult, KernelError, PowerExtension,
};
use sts2_types::{
    ArrowStatus, CardId, CardKeywords, CardMeta, CardPartition, CardPilePosition, CardRarity,
    CardStaticFlags, CardTags, CardType, CombatSide, Dec, HookMask, HookSlot, PileType, PowerId,
    RunRngType, TargetType, ValueProp, card_partition, generated_card_meta,
};

static HEAVY_SHOT_META: CardMeta = CardMeta {
    card_type: CardType::Attack,
    rarity: CardRarity::Uncommon,
    target_type: TargetType::AnyEnemy,
    canonical_energy_cost: 2,
    canonical_star_cost: -1,
    max_upgrade_level: 1,
    base_keywords: CardKeywords::empty(),
    base_tags: CardTags::SHOOT,
    flags: CardStaticFlags::CAN_BE_GENERATED_IN_COMBAT
        .union(CardStaticFlags::CAN_BE_GENERATED_BY_MODS)
        .union(CardStaticFlags::IS_ALLOWED_IN_BLITZ),
    hook_mask: HookMask::EMPTY.with(HookSlot::ModifyDamageAdditive),
};

pub fn card_meta(id: CardId) -> Result<&'static CardMeta, KernelError> {
    if id == CardId::HeavyShot {
        return Ok(&HEAVY_SHOT_META);
    }
    if card_partition(id) == CardPartition::Fool {
        return Ok(generated_card_meta(id));
    }
    Err(KernelError::Unsupported {
        kind: "Card",
        id: id.model_id_str().to_owned(),
    })
}

pub fn is_playable(state: &BattleState, card: CardHandle) -> Result<bool, KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    let _ = card_meta(id)?;
    Ok(true)
}

fn require_target(ctx: &CardPlayContext, id: CardId) -> Result<CreatureHandle, KernelError> {
    ctx.target.ok_or_else(|| {
        KernelError::InvalidAction(format!("{} requires target", id.model_id_str()))
    })
}

fn resolve_x_value(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    ctx: &CardPlayContext,
) -> Result<i32, KernelError> {
    hooks::modify_x_value(state, content, ctx.card, ctx.energy_value)
}

fn creature_is_impaled_by_arrow(state: &BattleState, target: CreatureHandle) -> bool {
    state
        .creature(target)
        .is_some_and(|c| c.powers.iter().any(|p| p.id == PowerId::ImpaledPower))
}

fn creature_is_impaled_by_players_arrow(state: &BattleState, target: CreatureHandle) -> bool {
    let owner = state.player.creature_handle;
    state.creature(target).is_some_and(|c| {
        c.powers
            .iter()
            .any(|p| p.id == PowerId::ImpaledPower && p.applier_handle == Some(owner))
    })
}

fn dislodge_all_player_arrows(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    source: CardHandle,
) -> Result<(), KernelError> {
    let owner = state.player.creature_handle;
    while creature_is_impaled_by_players_arrow(state, target) {
        arrow::dislodge_first_arrow(state, content, target, owner, Some(source))?;
        let remove_handle = state.creature(target).and_then(|c| {
            c.powers
                .iter()
                .find(|p| p.id == PowerId::ImpaledPower && p.applier_handle == Some(owner))
                .and_then(|p| {
                    p.ext_index.and_then(|eidx| {
                        state.power_extensions[eidx as usize]
                            .impaled_arrows
                            .is_empty()
                            .then_some(p.handle)
                    })
                })
        });
        if let Some(power_handle) = remove_handle {
            let _ = power::remove_by_handle(state, content, target, power_handle)?;
        }
    }
    Ok(())
}

fn impale_target_after_action(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    target: CreatureHandle,
    arrow_idx: usize,
    source_card: CardHandle,
) -> Result<(), KernelError> {
    let player_creature = state.player.creature_handle;
    let mut existing_idx = state.creature(target).and_then(|c| {
        c.powers
            .iter()
            .position(|p| p.id == PowerId::ImpaledPower && p.applier_handle == Some(player_creature))
    });

    if existing_idx.is_none() {
        let mut ext = PowerExtension::default();
        ext.hit_counter = 3;
        existing_idx = power::apply_new_with_extension(
            state,
            content,
            PowerId::ImpaledPower,
            target,
            Dec::ONE,
            Some(player_creature),
            Some(source_card),
            ext,
        )?;
    }

    if let Some(pidx) = existing_idx {
        if let Some(ext_idx) = state
            .creature(target)
            .and_then(|c| c.powers.get(pidx).and_then(|p| p.ext_index))
        {
            state.power_extensions[ext_idx as usize]
                .impaled_arrows
                .push(arrow_idx);
        }
    }

    hooks::after_arrow_impaled(state, content, target, Some(source_card))?;

    if state.creature(target).is_some_and(|c| c.is_dead()) {
        let contains_arrow = state
            .creature(target)
            .and_then(|c| {
                c.powers
                    .iter()
                    .find(|p| {
                        p.id == PowerId::ImpaledPower && p.applier_handle == Some(player_creature)
                    })
                    .and_then(|p| p.ext_index)
            })
            .is_some_and(|ext_idx| {
                state.power_extensions[ext_idx as usize]
                    .impaled_arrows
                    .contains(&arrow_idx)
            });
        if contains_arrow {
            arrow::dislodge_first_arrow(state, content, target, player_creature, Some(source_card))?;
        }
    }
    Ok(())
}

/// C# `CardRarity` enum order (differs from Rust `sts2_types::CardRarity` discriminant).
const fn csharp_rarity_rank(rarity: CardRarity) -> u8 {
    match rarity {
        CardRarity::None => 0,
        CardRarity::Basic => 1,
        CardRarity::Common => 2,
        CardRarity::Uncommon => 3,
        CardRarity::Rare => 4,
        CardRarity::Ancient => 5,
        CardRarity::Event => 6,
        CardRarity::Token => 7,
        CardRarity::Status => 8,
        CardRarity::Curse => 9,
        CardRarity::Quest => 10,
    }
}

fn from_combat_pile_select<F>(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    pile: PileType,
    min_count: usize,
    max_count: usize,
    source_id: &'static str,
    mut predicate: F,
    cancelable: bool,
    choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
) -> Result<SelectResult, KernelError>
where
    F: FnMut(&BattleState, CardHandle) -> bool,
{
    if hooks::is_ending(state) || state.player_creature().is_none_or(|c| c.is_dead()) {
        return Ok(SelectResult::Selected(SmallVec::new()));
    }
    let source_cards: &[CardHandle] = match pile {
        PileType::Hand => &state.player.hand,
        PileType::Draw => &state.player.draw,
        PileType::Discard => &state.player.discard,
        PileType::Exhaust => &state.player.exhaust,
        PileType::Play => &state.player.play,
        PileType::Deck => &state.player.deck,
        PileType::None => &[],
    };
    let mut candidates: SmallVec<[CardHandle; 16]> = source_cards
        .iter()
        .copied()
        .filter(|&h| predicate(state, h))
        .collect();

    if pile == PileType::Draw && (min_count != max_count || candidates.len() > min_count) {
        let mut keyed: SmallVec<[(u32, CardHandle); 64]> = SmallVec::with_capacity(candidates.len());
        for &h in &candidates {
            let cid = state
                .card(h)
                .ok_or_else(|| KernelError::InvalidState("missing candidate card".to_owned()))?
                .id;
            let meta = content.card_meta(cid)?;
            let key = (u32::from(csharp_rarity_rank(meta.rarity)) << 16)
                | u32::from(cid.model_entry_sort_rank());
            keyed.push((key, h));
        }
        keyed.sort_by_key(|&(k, _)| k);
        candidates = keyed.into_iter().map(|(_, h)| h).collect();
    }

    card_select::select_cards(
        state,
        &candidates,
        min_count,
        max_count,
        ChoiceTiming::Action,
        source_id,
        pile,
        cancelable,
        choice_queue,
    )
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
            ext.push_local_energy_modifier(sts2_kernel::LocalEnergyModifier {
                kind: sts2_types::LocalEnergyModifierKind::SetThisTurnOrUntilPlayed,
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

fn set_power_card_handle_field(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &'static str,
    card_handle: CardHandle,
) {
    let ext_idx_opt = state
        .creature(creature)
        .and_then(|c| c.powers.get(power_idx))
        .and_then(|p| p.ext_index);
    let ext_idx = match ext_idx_opt {
        Some(i) => i as usize,
        None => {
            let i = state.power_extensions.len();
            state.power_extensions.push(PowerExtension::default());
            if let Some(c) = state.creature_mut(creature) {
                if let Some(p) = c.powers.get_mut(power_idx) {
                    p.ext_index = Some(i as u32);
                }
            }
            i
        }
    };
    state.power_extensions[ext_idx]
        .private_fields
        .insert(Cow::Borrowed(key), FieldValue::CardHandle(card_handle.0 as i32));
}

fn show_off_current_aim(state: &BattleState, card: CardHandle) -> i32 {
    let Some(ci) = state.card(card) else {
        return 5;
    };
    let Some(ext) = state.card_ext(ci.ext_index) else {
        return 5;
    };
    if let Some(FieldValue::Int(v)) = ext.private_fields.get("ShowOff._currentAim") {
        return *v;
    }
    if let Some(FieldValue::Int(inc)) = ext.private_fields.get("ShowOff._increasedAim") {
        return 5 + *inc;
    }
    5
}

fn apply_power_to(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    power_id: PowerId,
    target: CreatureHandle,
    amount: i32,
    owner: CreatureHandle,
    card: CardHandle,
) -> Result<Option<usize>, KernelError> {
    power::apply(
        state,
        content,
        power_id,
        target,
        Dec::from_i32(amount),
        Some(owner),
        Some(card),
    )
}

fn gain_player_block(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    ctx: &CardPlayContext,
    owner: CreatureHandle,
    amount: i32,
) -> Result<(), KernelError> {
    let _ = block::gain_block(
        state,
        content,
        owner,
        Dec::from_i32(amount),
        ValueProp::MOVE,
        Some(ctx.card),
        Some(*ctx),
    )?;
    Ok(())
}

fn shoot_single_target_arrow(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    ctx: &CardPlayContext,
    owner: CreatureHandle,
    target: CreatureHandle,
    damage_per_hit: i32,
    hit_count: i32,
) -> Result<bool, KernelError> {
    let cmd = AttackBuilder::from_card(
        owner,
        ctx.card,
        *ctx,
        Dec::from_i32(damage_per_hit),
        AttackTargetSpec::Single(target),
    )
    .with_hit_count(hit_count)
    .from_arrow();
    arrow::shoot_arrow_at_target(state, content, target, cmd)
}

fn generate_cards_into_hand(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_id: CardId,
    count: usize,
    creator: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    for _ in 0..count {
        let _ = card_pile::add_generated_card_to_combat(
            state,
            content,
            card_id,
            PileType::Hand,
            CardPilePosition::Bottom,
            0,
            creator,
        )?;
    }
    Ok(())
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
        CardId::AllIsWell => {
            apply_power_to(state, content, PowerId::AllIsWellPower, owner, if upg { 6 } else { 4 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Anguish => {
            let target = require_target(ctx, card.id)?;
            let per = if upg { 7 } else { 6 };
            let dmg = per + per * state.player.counters.sadness_generated_this_combat;
            AttackBuilder::from_card(owner, ctx.card, *ctx, Dec::from_i32(dmg), AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Archery => {
            apply_power_to(state, content, PowerId::ArcheryPower, owner, if upg { 8 } else { 6 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::ArrowStab => {
            let target = require_target(ctx, card.id)?;
            let dmg = if upg { 15 } else { 13 };
            AttackBuilder::from_card(owner, ctx.card, *ctx, Dec::from_i32(dmg), AttackTargetSpec::Single(target))
                .execute(state, content)?;
            apply_power_to(
                state,
                content,
                PowerId::AimPower,
                owner,
                if upg { 8 } else { 6 },
                owner,
                ctx.card,
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::BlossomShot => {
            let target = require_target(ctx, card.id)?;
            if state.creature(target).is_none_or(|c| c.is_dead()) {
                return Ok(CardPlayOutcome::default());
            }
            let Some(arrow_idx) = arrow::try_shoot_arrow(state, content, Some(ctx.card))? else {
                return Ok(CardPlayOutcome::default());
            };
            if state.creature(target).is_none_or(|c| c.is_dead()) {
                if let Some(slot) = state.player.arrows.get_mut(arrow_idx) {
                    slot.status = ArrowStatus::Retrieved;
                }
                return Ok(CardPlayOutcome::default());
            }
            let dmg = if upg { 14 } else { 11 };
            let results = AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(dmg),
                AttackTargetSpec::Single(target),
            )
            .from_arrow()
            .execute(state, content)?;
            let total: i32 = results
                .iter()
                .map(|r| r.blocked_damage + r.unblocked_damage + r.overkill_damage)
                .sum();
            apply_power_to(state, content, PowerId::StretchPower, owner, total, owner, ctx.card)?;
            impale_target_after_action(state, content, target, arrow_idx, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::BlotOutTheSky => {
            let picked = card_factory::get_distinct_for_combat_filtered(
                state,
                sts2_types::CARD_POOL_FOOL,
                1,
                RunRngType::CombatCardGeneration,
                |id| generated_card_meta(id).base_tags.contains(CardTags::SHOOT),
            );
            if let Some(&card_id) = picked.first() {
                let generated = card_pile::create_card_in_combat(state, content, card_id, 0)?;
                set_to_free_this_turn(state, generated);
                card_pile::enter_combat(
                    state,
                    content,
                    generated,
                    PileType::Hand,
                    CardPilePosition::Bottom,
                    true,
                )?;
                hooks::after_card_generated(state, content, generated, Some(owner))?;
                hooks::after_card_generated_for_combat(state, content, generated, Some(owner))?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Bounce => {
            apply_power_to(state, content, PowerId::StretchPower, owner, if upg { 11 } else { 8 }, owner, ctx.card)?;
            let turn = state.player.turn_number;
            if let Some(ext) = state.card_ext_mut(ctx.card) {
                ext.private_fields
                    .insert(Cow::Borrowed("Bounce._lastFinishedPlayerTurn"), FieldValue::Int(turn));
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::BowSmack => {
            let target = require_target(ctx, card.id)?;
            let was_impaled = creature_is_impaled_by_arrow(state, target);
            let dmg = if upg { 11 } else { 8 };
            AttackBuilder::from_card(owner, ctx.card, *ctx, Dec::from_i32(dmg), AttackTargetSpec::Single(target))
                .execute(state, content)?;
            if was_impaled {
                gold::gain_energy(state, content, Dec::ONE)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Brace => {
            let x = resolve_x_value(content, state, ctx)?;
            let aim = if upg { 11 } else { 8 };
            for _ in 0..x {
                apply_power_to(state, content, PowerId::AimPower, owner, aim, owner, ctx.card)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::BrightenUp => {
            generate_cards_into_hand(state, content, CardId::Sadness, 2, Some(owner))?;
            generate_cards_into_hand(state, content, CardId::Joy, if upg { 3 } else { 2 }, Some(owner))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::ChoBarbs => {
            gain_player_block(state, content, ctx, owner, if upg { 10 } else { 8 })?;
            apply_power_to(state, content, PowerId::ChoBarbsPower, owner, if upg { 3 } else { 2 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::ChoChorus => {
            gain_player_block(state, content, ctx, owner, if upg { 18 } else { 14 })?;
            let to_play: SmallVec<[CardHandle; 12]> = state
                .player
                .hand
                .iter()
                .copied()
                .filter(|&h| {
                    state.card(h).is_some_and(|c| {
                        generated_card_meta(c.id)
                            .base_tags
                            .intersects(CardTags::WAH | CardTags::KI)
                    })
                })
                .collect();
            for ch in to_play {
                if state.pending.is_some() {
                    break;
                }
                let _ = autoplay::from_cards(state, content, &[ch], None, false)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::ChoFirm => {
            let blk = if upg { 7 } else { 5 };
            let gains = if arrow::player_has_any_arrows(state) { 2 } else { 1 };
            for _ in 0..gains {
                gain_player_block(state, content, ctx, owner, blk)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::ChoKi => {
            let stretch = state
                .creature(owner)
                .and_then(|c| c.powers.iter().find(|p| p.id == PowerId::StretchPower))
                .map_or(0, |p| p.amount);
            gain_player_block(state, content, ctx, owner, if upg { 11 } else { 8 })?;
            if stretch > 0 {
                apply_power_to(state, content, PowerId::StretchPower, owner, stretch, owner, ctx.card)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::ChoLimbs => {
            let blk = if upg { 7 } else { 5 };
            for _ in 0..2 {
                gain_player_block(state, content, ctx, owner, blk)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::ChoReady => {
            gain_player_block(state, content, ctx, owner, if upg { 9 } else { 7 })?;
            apply_power_to(state, content, PowerId::AimPower, owner, if upg { 4 } else { 3 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Coda => {
            let target = require_target(ctx, card.id)?;
            let dmg = if upg { 55 } else { 40 };
            AttackBuilder::from_card(owner, ctx.card, *ctx, Dec::from_i32(dmg), AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Compost => {
            apply_power_to(state, content, PowerId::CompostPower, owner, if upg { 2 } else { 1 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::CoveringFire => {
            apply_power_to(state, content, PowerId::CoveringFirePower, owner, if upg { 11 } else { 8 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DeepStretch => {
            apply_power_to(state, content, PowerId::StretchPower, owner, if upg { 10 } else { 7 }, owner, ctx.card)?;
            let _ = card_pile::draw(state, content, 1, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DefendFool => {
            gain_player_block(state, content, ctx, owner, if upg { 8 } else { 5 })?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DislikeYou => {
            let target = require_target(ctx, card.id)?;
            let dmg = if upg { 9 } else { 8 };
            AttackBuilder::from_card(owner, ctx.card, *ctx, Dec::from_i32(dmg), AttackTargetSpec::Single(target))
                .execute(state, content)?;
            apply_power_to(state, content, PowerId::DislikeYouPower, target, if upg { 75 } else { 50 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DivineShot => {
            let target = require_target(ctx, card.id)?;
            let _ = shoot_single_target_arrow(state, content, ctx, owner, target, if upg { 50 } else { 40 }, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Ecstasy => {
            apply_power_to(state, content, PowerId::EcstasyPower, owner, 1, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Encore => {
            gold::gain_energy(state, content, Dec::ONE)?;
            let _ = card_pile::draw(state, content, if upg { 3 } else { 2 }, false)?;
            apply_power_to(state, content, PowerId::EncorePower, owner, 1, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Ennui => {
            if let Some(pidx) = apply_power_to(
                state,
                content,
                PowerId::EnnuiPower,
                owner,
                if upg { 3 } else { 2 },
                owner,
                ctx.card,
            )? {
                if let Some(ph) = state.creature(owner).and_then(|c| c.powers.get(pidx).map(|p| p.handle)) {
                    crate::powers::batch_a_m::ennui_increment_sadness_amount(state, owner, ph);
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Enrapture => {
            let target = require_target(ctx, card.id)?;
            let amt = if upg { 9 } else { 6 };
            apply_power_to(state, content, PowerId::VigorPower, target, amt, owner, ctx.card)?;
            apply_power_to(state, content, PowerId::StretchPower, target, amt, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Ensemble => {
            apply_power_to(state, content, PowerId::EnsemblePower, owner, if upg { 5 } else { 4 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::EpicRetrieval => {
            let target = require_target(ctx, card.id)?;
            let dmg = if upg { 20 } else { 15 };
            AttackBuilder::from_card(owner, ctx.card, *ctx, Dec::from_i32(dmg), AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::ExplosiveArrows => {
            apply_power_to(state, content, PowerId::ExplosiveArrowsPower, owner, if upg { 11 } else { 8 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Florescence => {
            if !hooks::is_over_or_ending(state) && !state.player.hand.is_empty() {
                match from_combat_pile_select(
                    state,
                    content,
                    PileType::Hand,
                    1,
                    1,
                    card.id.model_id_str(),
                    |_, _| true,
                    false,
                    choice_queue,
                )? {
                    SelectResult::Selected(selected) => {
                        if let Some(&picked) = selected.first() {
                            let _ = card_cmd::transform_with_upgrade(
                                state,
                                content,
                                picked,
                                CardId::TheShot,
                                if upg { 1 } else { 0 },
                            )?;
                        }
                        Ok(CardPlayOutcome::default())
                    }
                    SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                        needs_choice: Some((req, 1)),
                        transform_into: None,
                    }),
                }
            } else {
                Ok(CardPlayOutcome::default())
            }
        }
        CardId::FoolishShot => {
            let target = require_target(ctx, card.id)?;
            let _ = shoot_single_target_arrow(state, content, ctx, owner, target, if upg { 20 } else { 15 }, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Growth => {
            gain_player_block(state, content, ctx, owner, if upg { 11 } else { 8 })?;
            if !hooks::is_over_or_ending(state) {
                let mut sadness_cards: Vec<CardHandle> = state
                    .player
                    .hand
                    .iter()
                    .copied()
                    .filter(|&h| state.card(h).is_some_and(|c| c.id == CardId::Sadness))
                    .collect();
                if !sadness_cards.is_empty() {
                    let mut sel_rng = *state.run_rngs.get(RunRngType::CombatCardSelection);
                    sts2_kernel::ordering::stable_shuffle_cards(
                        state,
                        content,
                        &mut sadness_cards,
                        &mut sel_rng,
                    )?;
                    *state.run_rngs.get_mut(RunRngType::CombatCardSelection) = sel_rng;
                    let _ = card_cmd::transform_with_clone(state, content, sadness_cards[0], ctx.card)?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::HarmonicShield => {
            let base = if upg { 6 } else { 4 };
            let blk = base + base * melody::melodies_this_turn(state);
            gain_player_block(state, content, ctx, owner, blk)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::HeavyShot => {
            let target = require_target(ctx, card.id)?;
            let _ = shoot_single_target_arrow(state, content, ctx, owner, target, if upg { 17 } else { 16 }, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::HiddenRoots => {
            apply_power_to(state, content, PowerId::HiddenRootsPower, owner, if upg { 4 } else { 3 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::HobblingShot => {
            let target = require_target(ctx, card.id)?;
            if shoot_single_target_arrow(state, content, ctx, owner, target, if upg { 14 } else { 13 }, 1)? {
                let amt = if upg { 2 } else { 1 };
                apply_power_to(state, content, PowerId::WeakPower, target, amt, owner, ctx.card)?;
                apply_power_to(state, content, PowerId::VulnerablePower, target, amt, owner, ctx.card)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Honing => {
            apply_power_to(state, content, PowerId::HoningPower, owner, 100, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Jest => {
            gain_player_block(state, content, ctx, owner, if upg { 19 } else { 15 })?;
            generate_cards_into_hand(state, content, CardId::Sadness, 1, Some(owner))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::KiChant => {
            let count = if upg { 4 } else { 3 };
            if state.player.draw.is_empty() {
                card_pile::shuffle_if_necessary(state, content)?;
            } else if state.player.draw.len() < count && !state.player.discard.is_empty() {
                let already_in_draw: Vec<CardHandle> = state.player.draw.iter().copied().collect();
                card_pile::shuffle(state, content)?;
                if state.pending.is_some() {
                    return Ok(CardPlayOutcome::default());
                }
                for existing in already_in_draw.into_iter().rev() {
                    if state.card(existing).is_some_and(|c| c.pile == PileType::Draw) {
                        card_pile::add_sync(state, existing, PileType::Draw, CardPilePosition::Top);
                    }
                }
            }
            if state.pending.is_some() {
                return Ok(CardPlayOutcome::default());
            }
            let top_cards: SmallVec<[CardHandle; 4]> =
                state.player.draw.iter().take(count).copied().collect();
            if top_cards.is_empty() {
                return Ok(CardPlayOutcome::default());
            }
            match from_combat_pile_select(
                state,
                content,
                PileType::Draw,
                1,
                1,
                card.id.model_id_str(),
                |_, h| top_cards.contains(&h),
                false,
                choice_queue,
            )? {
                SelectResult::Selected(picked) => {
                    for &item in &picked {
                        card_pile::add_sync(state, item, PileType::Hand, CardPilePosition::Bottom);
                    }
                    let remaining: SmallVec<[CardHandle; 4]> = top_cards
                        .iter()
                        .copied()
                        .filter(|h| {
                            !picked.contains(h)
                                && state.card(*h).is_some_and(|c| c.pile == PileType::Draw)
                        })
                        .collect();
                    if !remaining.is_empty() {
                        card_cmd::discard_batch(state, content, &remaining)?;
                    }
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                    needs_choice: Some((req, 1)),
                    transform_into: None,
                }),
            }
        }
        CardId::KiHop => {
            let target = require_target(ctx, card.id)?;
            let was_impaled = creature_is_impaled_by_arrow(state, target);
            let dmg = if upg { 8 } else { 7 };
            AttackBuilder::from_card(owner, ctx.card, *ctx, Dec::from_i32(dmg), AttackTargetSpec::Single(target))
                .execute(state, content)?;
            if was_impaled {
                let _ = card_pile::draw(state, content, if upg { 3 } else { 2 }, false)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::KiRest => {
            apply_power_to(
                state,
                content,
                PowerId::EnergyNextTurnPower,
                owner,
                if upg { 5 } else { 4 },
                owner,
                ctx.card,
            )?;
            if melody::played_this_turn(state, CardTags::WAH)
                || melody::played_this_turn(state, CardTags::CHO)
            {
                apply_power_to(state, content, PowerId::RetainHandPower, owner, 1, owner, ctx.card)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::KiVerse => {
            apply_power_to(state, content, PowerId::KiVersePower, owner, if upg { 2 } else { 1 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::KiWhip => {
            let target = require_target(ctx, card.id)?;
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(if upg { 6 } else { 5 }),
                AttackTargetSpec::Single(target),
            )
            .execute(state, content)?;
            apply_power_to(
                state,
                content,
                PowerId::EnergyNextTurnPower,
                owner,
                if upg { 3 } else { 2 },
                owner,
                ctx.card,
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::LockedIn => {
            match from_combat_pile_select(
                state,
                content,
                PileType::Draw,
                1,
                1,
                card.id.model_id_str(),
                |st, h| {
                    st.card(h).is_some_and(|c| {
                        generated_card_meta(c.id).base_tags.contains(CardTags::SHOOT)
                    })
                },
                false,
                choice_queue,
            )? {
                SelectResult::Selected(picked) => {
                    if let Some(&item) = picked.first() {
                        card_pile::add_sync(state, item, PileType::Hand, CardPilePosition::Bottom);
                    }
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                    needs_choice: Some((req, 1)),
                    transform_into: None,
                }),
            }
        }
        CardId::LostLute => {
            apply_power_to(state, content, PowerId::LostLutePower, owner, 1, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Lullaby => {
            apply_power_to(state, content, PowerId::LullabyPower, owner, if upg { 16 } else { 12 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::MelodicShot => {
            let target = require_target(ctx, card.id)?;
            let hits = 1 + melody::melodies_this_turn(state);
            let _ = shoot_single_target_arrow(
                state,
                content,
                ctx,
                owner,
                target,
                if upg { 14 } else { 12 },
                hits,
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Motif => {
            apply_power_to(state, content, PowerId::MotifPower, owner, 1, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::MourningBlow => {
            let target = require_target(ctx, card.id)?;
            let dmg = if upg { 15 } else { 10 };
            AttackBuilder::from_card(owner, ctx.card, *ctx, Dec::from_i32(dmg), AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let is_last_in_series = ctx.play_index + 1 == ctx.play_count;
            if is_last_in_series {
                if let Some(c) = state.card_mut(ctx.card) {
                    c.transform_into_after_play = Some(CardId::Sadness);
                }
                return Ok(CardPlayOutcome {
                    needs_choice: None,
                    transform_into: Some(CardId::Sadness),
                });
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::MythicForm => {
            apply_power_to(
                state,
                content,
                PowerId::MythicFormPower,
                owner,
                1,
                owner,
                ctx.card,
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Nail => {
            let target = require_target(ctx, card.id)?;
            if creature_is_impaled_by_arrow(state, target) {
                let dmg = if upg { 25 } else { 20 };
                let pwr = if upg { 3 } else { 2 };
                AttackBuilder::from_card(owner, ctx.card, *ctx, Dec::from_i32(dmg), AttackTargetSpec::Single(target))
                    .execute(state, content)?;
                apply_power_to(state, content, PowerId::WeakPower, target, pwr, owner, ctx.card)?;
                apply_power_to(state, content, PowerId::VulnerablePower, target, pwr, owner, ctx.card)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Nock => {
            apply_power_to(state, content, PowerId::AimPower, owner, if upg { 7 } else { 5 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Optimism => {
            let sadness_cards: SmallVec<[CardHandle; 12]> = state
                .player
                .hand
                .iter()
                .copied()
                .filter(|&h| state.card(h).is_some_and(|c| c.id == CardId::Sadness))
                .collect();
            let count = sadness_cards.len();
            for ch in sadness_cards {
                card_pile::exhaust(state, content, ch, false)?;
            }
            if count > 0 {
                let _ = card_pile::draw(state, content, count as i32, false)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::PunchOfJoy => {
            let target = require_target(ctx, card.id)?;
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(if upg { 16 } else { 14 }),
                AttackTargetSpec::Single(target),
            )
            .execute(state, content)?;
            if !hooks::is_over_or_ending(state) {
                let mut sadness_in_hand: Vec<CardHandle> = state
                    .player
                    .hand
                    .iter()
                    .copied()
                    .filter(|&h| state.card(h).is_some_and(|c| c.id == CardId::Sadness))
                    .collect();
                if !sadness_in_hand.is_empty() {
                    let mut sel_rng = *state.run_rngs.get(RunRngType::CombatCardSelection);
                    sts2_kernel::ordering::stable_shuffle_cards(
                        state,
                        content,
                        &mut sadness_in_hand,
                        &mut sel_rng,
                    )?;
                    *state.run_rngs.get_mut(RunRngType::CombatCardSelection) = sel_rng;
                    let take_n = if upg { 2 } else { 1 };
                    for ch in sadness_in_hand.into_iter().take(take_n) {
                        let _ = card_cmd::transform(state, content, ch, CardId::Joy)?;
                    }
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::QuickShot => {
            let target = require_target(ctx, card.id)?;
            let _ = shoot_single_target_arrow(state, content, ctx, owner, target, if upg { 13 } else { 10 }, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Quietus => {
            apply_power_to(state, content, PowerId::QuietusPower, owner, if upg { 3 } else { 2 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Quiver => {
            arrow::gain_arrow_slot(state);
            Ok(CardPlayOutcome::default())
        }
        CardId::RainShot => {
            let target = require_target(ctx, card.id)?;
            if state.creature(target).is_none_or(|c| c.is_dead()) {
                return Ok(CardPlayOutcome::default());
            }
            let Some(arrow_idx) = arrow::try_shoot_arrow(state, content, Some(ctx.card))? else {
                return Ok(CardPlayOutcome::default());
            };
            if state.creature(target).is_none_or(|c| c.is_dead()) {
                if let Some(slot) = state.player.arrows.get_mut(arrow_idx) {
                    slot.status = ArrowStatus::Retrieved;
                }
                return Ok(CardPlayOutcome::default());
            }
            let attack_id = state.next_attack_id;
            state.next_attack_id += 1;
            let mut info = AttackContextInfo {
                attack_id,
                attacker: owner,
                target_side: CombatSide::Enemy,
                card_source: Some(ctx.card),
                damage_props: ValueProp::MOVE,
                hit_count: 0,
            };
            hooks::before_attack(state, content, info)?;
            let mut all_results: SmallVec<[DamageResult; 16]> = SmallVec::new();
            let dmg = Dec::from_i32(if upg { 15 } else { 12 });
            arrow::impale_target_with_action(
                state,
                content,
                target,
                arrow_idx,
                Some(ctx.card),
                |s, c, _| {
                    let results = damage::damage_targets(
                        s,
                        c,
                        &[target],
                        dmg,
                        ValueProp::MOVE | ValueProp::FROM_ARROW,
                        Some(owner),
                        Some(ctx.card),
                        Some(*ctx),
                    )?;
                    info.hit_count += 1;
                    all_results.extend(results.iter().cloned());
                    let _ = power::remove_by_id(s, c, owner, PowerId::AimPower)?;
                    let _ = power::remove_by_id(s, c, owner, PowerId::QuietusPower)?;

                    if let Some(first) = results.first() {
                        let receiver_side = s
                            .creature(first.receiver)
                            .map_or(CombatSide::Enemy, |cr| cr.side);
                        let side_handles: SmallVec<[CreatureHandle; 8]> =
                            if receiver_side == CombatSide::Enemy {
                                SmallVec::from_slice(&s.enemy_handles)
                            } else {
                                SmallVec::from_slice(&s.ally_handles)
                            };
                        let mut others: SmallVec<[CreatureHandle; 8]> = SmallVec::new();
                        for ch in side_handles {
                            if ch != target
                                && s.creature(ch).is_some_and(|cr| cr.is_alive())
                                && hooks::should_allow_hitting(s, c, ch)?
                            {
                                others.push(ch);
                            }
                        }
                        if !others.is_empty() {
                            let splash_amt = Dec::from_i32(
                                first.blocked_damage + first.unblocked_damage + first.overkill_damage,
                            );
                            let splash = damage::damage_targets_with_hook_mode(
                                s,
                                c,
                                &others,
                                splash_amt,
                                ValueProp::MOVE,
                                Some(owner),
                                Some(ctx.card),
                                Some(*ctx),
                                damage::ModifyDamageHookType::CAP,
                            )?;
                            info.hit_count += 1;
                            all_results.extend(splash);
                        }
                    }
                    Ok(())
                },
            )?;
            hooks::after_attack(state, content, info, &all_results)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::TwinShot => {
            let target = require_target(ctx, card.id)?;
            let _ = shoot_single_target_arrow(state, content, ctx, owner, target, if upg { 10 } else { 7 }, 2)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Recall => {
            let target = require_target(ctx, card.id)?;
            if creature_is_impaled_by_players_arrow(state, target) {
                dislodge_all_player_arrows(state, content, target, ctx.card)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::RepeatShot => {
            let target = require_target(ctx, card.id)?;
            let x = resolve_x_value(content, state, ctx)?;
            if x > 0 {
                let _ = shoot_single_target_arrow(
                    state,
                    content,
                    ctx,
                    owner,
                    target,
                    if upg { 16 } else { 13 },
                    x,
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::RhythmStrike => {
            let target = require_target(ctx, card.id)?;
            let hits = 1 + melody::melodies_this_turn(state);
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(if upg { 5 } else { 4 }),
                AttackTargetSpec::Single(target),
            )
            .with_hit_count(hits)
            .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Rummage => {
            let _ = card_pile::draw(state, content, if upg { 4 } else { 3 }, false)?;
            generate_cards_into_hand(state, content, CardId::Sadness, 1, Some(owner))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SelfDeprecate => {
            generate_cards_into_hand(state, content, CardId::Sadness, 1, Some(owner))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SerratedArrows => {
            apply_power_to(state, content, PowerId::SerratedArrowsPower, owner, if upg { 75 } else { 50 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::ShowOff => {
            let current_aim = show_off_current_aim(state, ctx.card);
            apply_power_to(state, content, PowerId::AimPower, owner, current_aim, owner, ctx.card)?;
            if let Some(pidx) = apply_power_to(
                state,
                content,
                PowerId::ShowOffPower,
                owner,
                if upg { 3 } else { 2 },
                owner,
                ctx.card,
            )? {
                set_power_card_handle_field(
                    state,
                    owner,
                    pidx,
                    "ShowOffPower._showOffInstance",
                    ctx.card,
                );
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Simpleton => {
            apply_power_to(state, content, PowerId::SimpletonPower, owner, if upg { 2 } else { 1 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SkyShot => {
            let target = require_target(ctx, card.id)?;
            let _ = shoot_single_target_arrow(state, content, ctx, owner, target, if upg { 40 } else { 30 }, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SongPath => {
            apply_power_to(state, content, PowerId::SongPathPower, owner, if upg { 6 } else { 4 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SorrowfulStrike => {
            let target = require_target(ctx, card.id)?;
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(if upg { 17 } else { 13 }),
                AttackTargetSpec::Single(target),
            )
            .execute(state, content)?;
            generate_cards_into_hand(state, content, CardId::Sadness, 1, Some(owner))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SteadyCadence => {
            let target = require_target(ctx, card.id)?;
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(if upg { 13 } else { 10 }),
                AttackTargetSpec::Single(target),
            )
            .execute(state, content)?;
            let cards_to_draw = melody::melodies_this_turn(state);
            if cards_to_draw > 0 {
                let _ = card_pile::draw(state, content, cards_to_draw, false)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::StrikeFool => {
            let target = require_target(ctx, card.id)?;
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(if upg { 9 } else { 6 }),
                AttackTargetSpec::Single(target),
            )
            .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Timber => {
            let target = require_target(ctx, card.id)?;
            let dead_or_ending = hooks::is_over_or_ending_checked(state, content)?
                || state.creature(owner).is_none_or(|c| c.is_dead());
            if !dead_or_ending {
                let attack_id = state.next_attack_id;
                state.next_attack_id += 1;
                let mut info = AttackContextInfo {
                    attack_id,
                    attacker: owner,
                    target_side: CombatSide::Enemy,
                    card_source: Some(ctx.card),
                    damage_props: ValueProp::MOVE,
                    hit_count: 0,
                };
                hooks::before_attack(state, content, info)?;
                let mut all_results: SmallVec<[DamageResult; 16]> = SmallVec::new();
                if state.creature(target).is_some_and(|c| c.is_alive())
                    && state.creature(owner).is_some_and(|c| c.is_alive())
                    && !hooks::is_over_or_ending(state)
                {
                    let primary = damage::damage_targets(
                        state,
                        content,
                        &[target],
                        Dec::from_i32(if upg { 25 } else { 20 }),
                        ValueProp::MOVE,
                        Some(owner),
                        Some(ctx.card),
                        Some(*ctx),
                    )?;
                    info.hit_count += 1;
                    let killed = primary.iter().any(|r| r.was_target_killed);
                    all_results.extend(primary);
                    if killed
                        && state.creature(owner).is_some_and(|c| c.is_alive())
                        && !hooks::is_over_or_ending(state)
                    {
                        let side_handles: SmallVec<[CreatureHandle; 8]> =
                            SmallVec::from_slice(&state.enemy_handles);
                        let mut targets: SmallVec<[CreatureHandle; 8]> = SmallVec::new();
                        for ch in side_handles {
                            if state.creature(ch).is_some_and(|c| c.is_alive())
                                && hooks::should_allow_hitting(state, content, ch)?
                            {
                                targets.push(ch);
                            }
                        }
                        if !targets.is_empty() {
                            let splash = damage::damage_targets(
                                state,
                                content,
                                &targets,
                                Dec::from_i32(if upg { 35 } else { 30 }),
                                ValueProp::MOVE,
                                Some(owner),
                                Some(ctx.card),
                                Some(*ctx),
                            )?;
                            info.hit_count += 1;
                            all_results.extend(splash);
                        }
                    }
                }
                hooks::after_attack(state, content, info, &all_results)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::TrickShot => {
            let target = require_target(ctx, card.id)?;
            if shoot_single_target_arrow(state, content, ctx, owner, target, if upg { 14 } else { 12 }, 1)? {
                let _ = card_pile::draw(state, content, if upg { 3 } else { 2 }, false)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Tune => {
            apply_power_to(state, content, PowerId::TunePower, owner, if upg { 2 } else { 1 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Uproot => {
            let target = require_target(ctx, card.id)?;
            if creature_is_impaled_by_players_arrow(state, target) {
                dislodge_all_player_arrows(state, content, target, ctx.card)?;
                apply_power_to(
                    state,
                    content,
                    PowerId::VulnerablePower,
                    target,
                    if upg { 4 } else { 3 },
                    owner,
                    ctx.card,
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::ViolentChord => {
            apply_power_to(state, content, PowerId::ViolentChordPower, owner, if upg { 9 } else { 7 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Vitriol => {
            let target = require_target(ctx, card.id)?;
            apply_power_to(state, content, PowerId::WeakPower, target, if upg { 3 } else { 2 }, owner, ctx.card)?;
            generate_cards_into_hand(state, content, CardId::Sadness, 2, Some(owner))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::WahBlast => {
            let hits = if melody::played_this_turn(state, CardTags::CHO)
                || melody::played_this_turn(state, CardTags::KI)
            {
                2
            } else {
                1
            };
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(if upg { 18 } else { 14 }),
                AttackTargetSpec::AllOpponents,
            )
            .with_hit_count(hits)
            .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::WahCho => {
            let target = require_target(ctx, card.id)?;
            let amt = if upg { 5 } else { 3 };
            gain_player_block(state, content, ctx, owner, amt)?;
            AttackBuilder::from_card(owner, ctx.card, *ctx, Dec::from_i32(amt), AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::WahChoKi => {
            let target = require_target(ctx, card.id)?;
            let amt = if upg { 10 } else { 8 };
            AttackBuilder::from_card(owner, ctx.card, *ctx, Dec::from_i32(amt), AttackTargetSpec::Single(target))
                .execute(state, content)?;
            gain_player_block(state, content, ctx, owner, amt)?;
            let _ = card_pile::draw(state, content, if upg { 2 } else { 1 }, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::WahHammer => {
            let target = require_target(ctx, card.id)?;
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(if upg { 11 } else { 9 }),
                AttackTargetSpec::Single(target),
            )
            .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::WahKi => {
            let target = require_target(ctx, card.id)?;
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(if upg { 4 } else { 3 }),
                AttackTargetSpec::Single(target),
            )
            .with_hit_count(2)
            .execute(state, content)?;
            if !hooks::is_over_or_ending(state) {
                match from_combat_pile_select(
                    state,
                    content,
                    PileType::Discard,
                    1,
                    1,
                    card.id.model_id_str(),
                    |_, _| true,
                    false,
                    choice_queue,
                )? {
                    SelectResult::Selected(selected) => {
                        if let Some(&picked) = selected.first() {
                            card_pile::add_sync(state, picked, PileType::Draw, CardPilePosition::Top);
                        }
                        Ok(CardPlayOutcome::default())
                    }
                    SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                        needs_choice: Some((req, 1)),
                        transform_into: None,
                    }),
                }
            } else {
                Ok(CardPlayOutcome::default())
            }
        }
        CardId::WahSymphony => {
            let melody_count = [
                &state.player.hand[..],
                &state.player.draw[..],
                &state.player.discard[..],
                &state.player.exhaust[..],
                &state.player.play[..],
            ]
            .into_iter()
            .flatten()
            .filter(|&&h| {
                state.card(h).is_some_and(|c| {
                    generated_card_meta(c.id)
                        .base_tags
                        .intersects(CardTags::CHO | CardTags::KI)
                })
            })
            .count() as i32;
            let dmg = 6 + (if upg { 6 } else { 4 }) * melody_count;
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(dmg),
                AttackTargetSpec::AllOpponents,
            )
            .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::WahSweep => {
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(if upg { 11 } else { 8 }),
                AttackTargetSpec::AllOpponents,
            )
            .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::WahTouch => {
            let target = require_target(ctx, card.id)?;
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(if upg { 8 } else { 6 }),
                AttackTargetSpec::Single(target),
            )
            .execute(state, content)?;
            apply_power_to(
                state,
                content,
                PowerId::VulnerablePower,
                target,
                if upg { 2 } else { 1 },
                owner,
                ctx.card,
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::WhistlingArrows => {
            gain_player_block(state, content, ctx, owner, if upg { 4 } else { 3 })?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Wrest => {
            let target = require_target(ctx, card.id)?;
            if creature_is_impaled_by_players_arrow(state, target) {
                dislodge_all_player_arrows(state, content, target, ctx.card)?;
                let _ = card_pile::draw(state, content, if upg { 3 } else { 2 }, false)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Yah => {
            apply_power_to(state, content, PowerId::YahPower, owner, if upg { 2 } else { 1 }, owner, ctx.card)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Yoga => {
            let stretch = if upg { 8 } else { 6 };
            let allies: SmallVec<[CreatureHandle; 4]> = state.ally_handles.iter().copied().collect();
            for ah in allies {
                if state.creature(ah).is_some_and(|c| c.is_alive() && c.is_player) {
                    apply_power_to(state, content, PowerId::StretchPower, ah, stretch, owner, ctx.card)?;
                }
            }
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
    let (id, upg) = {
        let ci = state
            .card(ctx.card)
            .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?;
        (ci.id, ci.upgrade_level > 0)
    };
    match id {
        CardId::LockedIn => {
            if let Some(&picked) = selected.first() {
                card_pile::add_sync(state, picked, PileType::Hand, CardPilePosition::Bottom);
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::WahKi => {
            if let Some(&picked) = selected.first() {
                card_pile::add_sync(state, picked, PileType::Draw, CardPilePosition::Top);
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Florescence => {
            if let Some(&picked) = selected.first() {
                let _ = card_cmd::transform_with_upgrade(
                    state,
                    content,
                    picked,
                    CardId::TheShot,
                    if upg { 1 } else { 0 },
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::KiChant => {
            let count = if upg { 4 } else { 3 };
            let top_cards: SmallVec<[CardHandle; 4]> =
                state.player.draw.iter().take(count).copied().collect();
            for &item in selected {
                card_pile::add_sync(state, item, PileType::Hand, CardPilePosition::Bottom);
            }
            let remaining: SmallVec<[CardHandle; 4]> = top_cards
                .iter()
                .copied()
                .filter(|h| {
                    !selected.contains(h)
                        && state.card(*h).is_some_and(|c| c.pile == PileType::Draw)
                })
                .collect();
            if !remaining.is_empty() {
                card_cmd::discard_batch(state, content, &remaining)?;
            }
            Ok(CardPlayOutcome::default())
        }
        _ => Err(KernelError::Unsupported {
            kind: "CardResume",
            id: id.model_id_str().to_owned(),
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
        CardId::BlotOutTheSky
        | CardId::Ecstasy
        | CardId::Honing
        | CardId::LockedIn
        | CardId::LostLute
        | CardId::Motif => {
            if let Some(c) = state.card_mut(card) {
                c.base_energy_cost = (c.base_energy_cost - 1).max(0);
            }
        }
        CardId::Quiver => {
            if let Some(c) = state.card_mut(card) {
                c.local_keywords.insert(CardKeywords::INNATE);
            }
        }
        CardId::Optimism | CardId::Recall => {
            if let Some(c) = state.card_mut(card) {
                c.local_keywords.remove(CardKeywords::EXHAUST);
            }
        }
        CardId::MythicForm => {
            if let Some(c) = state.card_mut(card) {
                c.local_keywords.remove(CardKeywords::ETHEREAL);
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
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    let _ = card_meta(id)?;
    if id == CardId::ShowOff {
        return Ok(smallvec::smallvec![(
            "AimPower",
            Dec::from_i32(show_off_current_aim(state, card))
        )]);
    }
    Ok(SmallVec::new())
}

pub fn dispatch_hook(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    card: CardHandle,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    let (id, upg, pile, ext_index) = {
        let ci = state
            .card(card)
            .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?;
        (ci.id, ci.upgrade_level > 0, ci.pile, ci.ext_index)
    };
    let _ = card_meta(id)?;
    match id {
        CardId::Bounce => {
            if slot == HookSlot::BeforeHandDraw {
                let last_turn = state
                    .card_ext(ext_index)
                    .and_then(|e| e.private_fields.get("Bounce._lastFinishedPlayerTurn"))
                    .and_then(|v| match v {
                        FieldValue::Int(t) => Some(*t),
                        _ => None,
                    })
                    .unwrap_or(-1);
                if last_turn >= 0
                    && state.player.turn_number - 1 == last_turn
                    && pile != PileType::Hand
                {
                    card_pile::add_sync(state, card, PileType::Hand, CardPilePosition::Bottom);
                }
            }
            Ok(HookResult::default())
        }
        CardId::ChoLimbs => {
            if slot == HookSlot::TryModifyEnergyCostInCombat {
                if let HookArgs::CardCost { card: target_card, cost } = args {
                    if *target_card == card
                        && *cost > Dec::ZERO
                        && (melody::played_this_turn(state, CardTags::WAH)
                            || melody::played_this_turn(state, CardTags::KI))
                    {
                        return Ok(HookResult {
                            dec_val: Some(cost.checked_sub(Dec::TWO)?.max(Dec::ZERO)),
                            modified: true,
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }
        CardId::Coda => {
            if slot == HookSlot::TryModifyEnergyCostInCombat {
                if let HookArgs::CardCost { card: target_card, cost } = args {
                    if *target_card == card && *cost > Dec::ZERO {
                        let reduction = 2 * melody::melodies_this_turn(state);
                        if reduction > 0 {
                            return Ok(HookResult {
                                dec_val: Some(
                                    cost.checked_sub(Dec::from_i32(reduction))?.max(Dec::ZERO),
                                ),
                                modified: true,
                                ..Default::default()
                            });
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        CardId::EpicRetrieval => {
            if slot == HookSlot::AfterArrowDislodged {
                if let HookArgs::AfterDeath { card_source, .. } = args {
                    if *card_source == Some(card) {
                        let energy = if upg { 4 } else { 3 };
                        gold::gain_energy(state, content, Dec::from_i32(energy))?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        CardId::HeavyShot => {
            if slot == HookSlot::ModifyDamageAdditive {
                if let HookArgs::ModifyDamage {
                    dealer,
                    props,
                    card_source,
                    ..
                } = args
                {
                    if *card_source == Some(card)
                        && *dealer == Some(state.player.creature_handle)
                        && props.is_powered_attack()
                        && props.contains(ValueProp::FROM_ARROW)
                    {
                        let aim = state
                            .player_creature()
                            .and_then(|c| c.powers.iter().find(|p| p.id == PowerId::AimPower))
                            .map_or(0, |p| p.amount);
                        if aim != 0 {
                            let mult = if upg { 3 } else { 2 };
                            return Ok(HookResult {
                                dec_val: Some(Dec::from_i32(aim * mult)),
                                ..Default::default()
                            });
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        CardId::WahHammer => {
            if slot == HookSlot::ModifyDamageMultiplicative {
                if let HookArgs::ModifyDamage {
                    target: Some(target),
                    card_source,
                    ..
                } = args
                {
                    if *card_source == Some(card) && creature_is_impaled_by_arrow(state, *target) {
                        return Ok(HookResult {
                            dec_val: Some(Dec::TWO),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }
        CardId::WhistlingArrows => {
            if slot == HookSlot::AfterArrowShot {
                if let HookArgs::ArrowShot { arrow_idx, .. } = args {
                    if arrow_idx.is_some() && pile == PileType::Discard {
                        card_pile::add_sync(state, card, PileType::Hand, CardPilePosition::Bottom);
                    }
                }
            }
            Ok(HookResult::default())
        }
        _ => Ok(HookResult::default()),
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
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    let _ = card_meta(id)?;
    Ok(Dec::ZERO)
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
    match id {
        CardId::MourningBlow => Ok(Some((PileType::Hand, CardPilePosition::Bottom))),
        _ => Ok(None),
    }
}
