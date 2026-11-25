use smallvec::{smallvec, SmallVec};
use std::borrow::Cow;
use std::sync::LazyLock;
use sts2_kernel::commands::card_select::SelectResult;
use sts2_kernel::commands::{
    autoplay, block, card as card_cmd, card_factory, card_pile, card_select,
    damage::AttackBuilder, damage::AttackTargetSpec, gold, orb, power,
};
use sts2_kernel::content_dispatch::{CardPlayContext, CardPlayOutcome};
use sts2_kernel::{
    hooks, ordering, BattleState, CardHandle, CardInst, CardTempFlags, ChoiceTiming,
    ContentDispatcher, CreatureHandle, FieldValue, HookArgs, HookResult, KernelError,
    LocalEnergyModifier, PowerExtension,
};
use sts2_types::generated_ids::generated_card_meta;
use sts2_types::{
    card_partition, CardId, CardKeywords, CardMeta, CardPartition, CardPilePosition,
    CardStaticFlags, CardType, Dec, HookSlot, LocalEnergyModifierKind, OrbId,
    PileType, PowerId, RunRngType, ValueProp,
};

const VALID_ORBS: [OrbId; 5] = [
    OrbId::LightningOrb,
    OrbId::FrostOrb,
    OrbId::DarkOrb,
    OrbId::PlasmaOrb,
    OrbId::GlassOrb,
];

static DEFECT_CARD_META: LazyLock<[CardMeta; CardId::COUNT]> = LazyLock::new(|| {
    std::array::from_fn(|idx| {
        let mut meta = sts2_types::generated_ids::CARD_META_TABLE[idx];
        if idx == CardId::GeneticAlgorithm as usize {
            meta.flags |= CardStaticFlags::HAS_GROWTH;
        }
        if idx == CardId::Chaos as usize
            || idx == CardId::FlakCannon as usize
            || idx == CardId::Reboot as usize
            || idx == CardId::Uproar as usize
            || idx == CardId::WhiteNoise as usize
        {
            meta.flags |= CardStaticFlags::USES_RNG;
        }
        if idx == CardId::AllForOne as usize
            || idx == CardId::Barrage as usize
            || idx == CardId::Claw as usize
            || idx == CardId::CompileDriver as usize
            || idx == CardId::Darkness as usize
            || idx == CardId::DoubleEnergy as usize
            || idx == CardId::Dualcast as usize
            || idx == CardId::Ftl as usize
            || idx == CardId::HelixDrill as usize
            || idx == CardId::Hologram as usize
            || idx == CardId::MultiCast as usize
            || idx == CardId::Quadcast as usize
            || idx == CardId::Reboot as usize
            || idx == CardId::Scrape as usize
            || idx == CardId::Shatter as usize
            || idx == CardId::Synchronize as usize
            || idx == CardId::TeslaCoil as usize
            || idx == CardId::Voltaic as usize
        {
            meta.flags |= CardStaticFlags::ORDER_SENSITIVE;
        }
        meta
    })
});

pub fn card_meta(id: CardId) -> Result<&'static CardMeta, KernelError> {
    if card_partition(id) == CardPartition::Defect {
        Ok(&DEFECT_CARD_META[id as usize])
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

fn require_target(ctx: &CardPlayContext, card_id: CardId) -> Result<CreatureHandle, KernelError> {
    ctx.target.ok_or_else(|| {
        KernelError::InvalidAction(format!("{} requires target", card_id.model_id_str()))
    })
}

fn target_intends_attack(
    content: &dyn ContentDispatcher,
    state: &BattleState,
    target: CreatureHandle,
) -> Result<bool, KernelError> {
    content.monster_intends_attack(state, target)
}

fn resolve_x_value(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    ctx: &CardPlayContext,
) -> Result<i32, KernelError> {
    hooks::modify_x_value(state, content, ctx.card, ctx.energy_value)
}

fn read_card_decimal(state: &BattleState, handle: CardHandle, key: &str, default: Dec) -> Dec {
    state
        .card(handle)
        .and_then(|c| state.card_ext(c.ext_index))
        .and_then(|ext| ext.private_fields.get(key))
        .and_then(|fv| match fv {
            FieldValue::Decimal(d) => Some(*d),
            FieldValue::Int(i) => Some(Dec::from_i32(*i)),
            _ => None,
        })
        .unwrap_or(default)
}

fn read_card_int(state: &BattleState, handle: CardHandle, key: &str, default: i32) -> i32 {
    state
        .card(handle)
        .and_then(|c| state.card_ext(c.ext_index))
        .and_then(|ext| ext.private_fields.get(key))
        .and_then(|fv| match fv {
            FieldValue::Int(i) => Some(*i),
            _ => None,
        })
        .unwrap_or(default)
}

fn read_claw_damage(state: &BattleState, handle: CardHandle) -> Dec {
    let upg = state.card(handle).map_or(0, |c| c.upgrade_level);
    read_card_decimal(state, handle, "Claw._damage", Dec::from_i32(3 + upg))
}

fn read_claw_increase(state: &BattleState, handle: CardHandle) -> Dec {
    let upg = state.card(handle).map_or(0, |c| c.upgrade_level);
    read_card_decimal(state, handle, "Claw._increase", Dec::from_i32(2 + upg))
}

fn read_ga_current_block(state: &BattleState, handle: CardHandle) -> i32 {
    let increased = read_card_int(state, handle, "GeneticAlgorithm._increasedBlock", 0);
    read_card_int(state, handle, "GeneticAlgorithm._currentBlock", 1 + increased)
}

fn read_ga_increase(state: &BattleState, handle: CardHandle) -> i32 {
    let upg = state.card(handle).map_or(0, |c| c.upgrade_level);
    read_card_int(state, handle, "GeneticAlgorithm._increase", 3 + upg)
}

fn buff_genetic_algorithm(state: &mut BattleState, handle: CardHandle, increase: i32) {
    let prev_increased = read_card_int(state, handle, "GeneticAlgorithm._increasedBlock", 0);
    let next_increased = prev_increased + increase;
    let next_current = 1 + next_increased;
    if let Some(ext) = state.card_ext_mut(handle) {
        ext.private_fields.insert(
            Cow::Borrowed("GeneticAlgorithm._increasedBlock"),
            FieldValue::Int(next_increased),
        );
        ext.private_fields.insert(
            Cow::Borrowed("GeneticAlgorithm._currentBlock"),
            FieldValue::Int(next_current),
        );
    }
}

fn distinct_orb_count(state: &BattleState) -> i32 {
    let mut seen: SmallVec<[OrbId; 5]> = SmallVec::new();
    for o in &state.player.orbs {
        if !seen.contains(&o.id) {
            seen.push(o.id);
        }
    }
    seen.len() as i32
}

fn set_temp_cost_this_combat(state: &mut BattleState, handle: CardHandle, cost: i32) {
    if let Some(c) = state.card_mut(handle) {
        c.temp_cost_override_this_combat = sts2_kernel::OptI16::some_clamped(cost);
    }
    if let Some(ext) = state.card_ext_mut(handle) {
        ext.push_local_energy_modifier(LocalEnergyModifier {
            kind: LocalEnergyModifierKind::SetThisCombat,
            amount: cost,
            is_reduce_only: false,
        });
    }
}

fn add_energy_cost_this_combat(state: &mut BattleState, handle: CardHandle, amount: i32) {
    if amount == 0 {
        return;
    }
    if let Some(ext) = state.card_ext_mut(handle) {
        ext.push_local_energy_modifier(LocalEnergyModifier {
            kind: LocalEnergyModifierKind::AddThisCombat,
            amount,
            is_reduce_only: false,
        });
    }
}

fn add_energy_cost_until_played(state: &mut BattleState, handle: CardHandle, amount: i32) {
    if amount == 0 {
        return;
    }
    if let Some(ext) = state.card_ext_mut(handle) {
        ext.push_local_energy_modifier(LocalEnergyModifier {
            kind: LocalEnergyModifierKind::AddUntilPlayed,
            amount,
            is_reduce_only: false,
        });
    }
}

fn set_to_free_this_turn(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    handle: CardHandle,
) -> Result<(), KernelError> {
    let Some(card) = state.card(handle) else {
        return Ok(());
    };
    let meta = content.card_meta(card.id)?;
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
    Ok(())
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
    let source_handle = if orig.temp_flags.contains(CardTempFlags::IS_DUPE) {
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
    temp_flags.remove(CardTempFlags::EXHAUST_ON_NEXT_PLAY | CardTempFlags::IS_DUPE);
    state.cards.push(CardInst {
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

fn collect_hittable_enemies(
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

fn generate_cards_in_discard(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_id: CardId,
    count: usize,
    creator: CreatureHandle,
) -> Result<(), KernelError> {
    for _ in 0..count {
        let _ = card_pile::add_generated_card_to_combat(
            state,
            content,
            card_id,
            PileType::Discard,
            CardPilePosition::Bottom,
            0,
            Some(creator),
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
        CardId::AdaptiveStrike => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 23 } else { 18 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let clone_h = clone_card_in_combat(state, ctx.card)?;
            set_temp_cost_this_combat(state, clone_h, 0);
            if !hooks::is_over_or_ending(state) {
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
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::AllForOne => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 14 } else { 10 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            if hooks::is_over_or_ending(state) {
                return Ok(CardPlayOutcome::default());
            }
            let discard: SmallVec<[CardHandle; 32]> = SmallVec::from_slice(&state.player.discard);
            let mut to_move: SmallVec<[CardHandle; 8]> = SmallVec::new();
            for ch in discard {
                let Some(ci) = state.card(ch) else {
                    continue;
                };
                let meta = content.card_meta(ci.id)?;
                if !meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST)
                    && matches!(
                        meta.card_type,
                        CardType::Attack | CardType::Skill | CardType::Power
                    )
                    && ordering::resolve_energy_cost(state, content, ch)? == 0
                {
                    to_move.push(ch);
                }
            }
            for ch in to_move {
                card_pile::add_sync(state, ch, PileType::Hand, CardPilePosition::Bottom);
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::BallLightning => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 10 } else { 7 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            orb::channel(state, content, OrbId::LightningOrb)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Barrage => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 7 } else { 5 });
            let hits = state.player.orbs.len() as i32;
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .with_hit_count(hits)
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::BeamCell => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 4 } else { 3 });
            let vuln = Dec::from_i32(if upg { 2 } else { 1 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(
                state,
                content,
                PowerId::VulnerablePower,
                target,
                vuln,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::BiasedCognition => {
            let focus = Dec::from_i32(if upg { 6 } else { 5 });
            power::apply(
                state,
                content,
                PowerId::FocusPower,
                owner,
                focus,
                Some(owner),
                Some(ctx.card),
            )?;
            power::apply(
                state,
                content,
                PowerId::BiasedCognitionPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::BoostAway => {
            let blk = Dec::from_i32(if upg { 9 } else { 6 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            generate_cards_in_discard(state, content, CardId::Dazed, 1, owner)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::BootSequence => {
            let blk = Dec::from_i32(if upg { 13 } else { 10 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Buffer => {
            let buf = Dec::from_i32(if upg { 2 } else { 1 });
            power::apply(
                state,
                content,
                PowerId::BufferPower,
                owner,
                buf,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::BulkUp => {
            let stat = Dec::from_i32(if upg { 3 } else { 2 });
            orb::remove_slots(state, 1);
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                owner,
                stat,
                Some(owner),
                Some(ctx.card),
            )?;
            power::apply(
                state,
                content,
                PowerId::DexterityPower,
                owner,
                stat,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Capacitor => {
            orb::add_slots(state, if upg { 3 } else { 2 });
            Ok(CardPlayOutcome::default())
        }

        CardId::Chaos => {
            let repeat = if upg { 2 } else { 1 };
            for _ in 0..repeat {
                if let Some(&orb_id) = state
                    .run_rngs
                    .get_mut(RunRngType::CombatOrbs)
                    .next_item(&VALID_ORBS)
                {
                    orb::channel(state, content, orb_id)?;
                }
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::ChargeBattery => {
            let blk = Dec::from_i32(if upg { 10 } else { 7 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            power::apply(
                state,
                content,
                PowerId::EnergyNextTurnPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Chill => {
            let count = collect_hittable_enemies(state, content)?.len();
            for _ in 0..count {
                orb::channel(state, content, OrbId::FrostOrb)?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::Claw => {
            let target = require_target(ctx, card.id)?;
            let dmg = read_claw_damage(state, ctx.card);
            let inc = read_claw_increase(state, ctx.card);
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let all_claws: SmallVec<[CardHandle; 8]> = state
                .player
                .hand
                .iter()
                .chain(state.player.draw.iter())
                .chain(state.player.discard.iter())
                .chain(state.player.exhaust.iter())
                .chain(state.player.play.iter())
                .copied()
                .filter(|&h| state.card(h).is_some_and(|c| c.id == CardId::Claw))
                .collect();
            for ch in all_claws {
                let next = read_claw_damage(state, ch).checked_add(inc)?;
                if let Some(ext) = state.card_ext_mut(ch) {
                    ext.private_fields
                        .insert(Cow::Borrowed("Claw._damage"), FieldValue::Decimal(next));
                }
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::ColdSnap => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 9 } else { 6 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            orb::channel(state, content, OrbId::FrostOrb)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Compact => {
            let blk = Dec::from_i32(if upg { 7 } else { 6 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            let hand_snapshot: SmallVec<[CardHandle; 16]> =
                SmallVec::from_slice(&state.player.hand);
            let mut statuses: SmallVec<[CardHandle; 8]> = SmallVec::new();
            for h in hand_snapshot {
                let Some(ci) = state.card(h) else {
                    continue;
                };
                if content.card_meta(ci.id)?.card_type == CardType::Status
                    && !card_pile::card_has_keyword(state, content, h, CardKeywords::ETERNAL)?
                {
                    statuses.push(h);
                }
            }
            for status_h in statuses {
                if let Some(rep) = card_cmd::transform(state, content, status_h, CardId::Fuel)? {
                    if upg {
                        card_cmd::upgrade(state, content, rep)?;
                    }
                }
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::CompileDriver => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 10 } else { 7 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let distinct = distinct_orb_count(state);
            let _ = card_pile::draw(state, content, distinct, false)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::ConsumingShadow => {
            let repeat = if upg { 3 } else { 2 };
            for _ in 0..repeat {
                orb::channel(state, content, OrbId::DarkOrb)?;
            }
            power::apply(
                state,
                content,
                PowerId::ConsumingShadowPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Coolant => {
            let amt = Dec::from_i32(if upg { 3 } else { 2 });
            power::apply(
                state,
                content,
                PowerId::CoolantPower,
                owner,
                amt,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Coolheaded => {
            orb::channel(state, content, OrbId::FrostOrb)?;
            let _ = card_pile::draw(state, content, if upg { 2 } else { 1 }, false)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::CreativeAi => {
            power::apply(
                state,
                content,
                PowerId::CreativeAiPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Darkness => {
            orb::channel(state, content, OrbId::DarkOrb)?;
            let trigger_count = if upg { 2 } else { 1 };
            let orb_count = state.player.orbs.len();
            for idx in 0..orb_count {
                if state.player.orbs.get(idx).is_some_and(|o| o.id == OrbId::DarkOrb) {
                    for _ in 0..trigger_count {
                        orb::passive(state, content, idx, None, false)?;
                    }
                }
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::DefendDefect => {
            let blk = Dec::from_i32(if upg { 8 } else { 5 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Defragment => {
            let focus = Dec::from_i32(if upg { 2 } else { 1 });
            power::apply(
                state,
                content,
                PowerId::FocusPower,
                owner,
                focus,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::DoubleEnergy => {
            gold::gain_energy(state, Dec::from_i32(state.player.energy))?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Dualcast => {
            if !state.player.orbs.is_empty() {
                orb::evoke_next(state, content, false)?;
                orb::evoke_next(state, content, true)?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::EchoForm => {
            power::apply(
                state,
                content,
                PowerId::EchoFormPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::EnergySurge => {
            let amt = Dec::from_i32(if upg { 3 } else { 2 });
            let has_living_player = state
                .ally_handles
                .iter()
                .any(|&h| state.creature(h).is_some_and(|c| c.is_alive() && c.is_player));
            if has_living_player {
                gold::gain_energy(state, amt)?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::Feral => {
            power::apply(
                state,
                content,
                PowerId::FeralPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::FightThrough => {
            let blk = Dec::from_i32(if upg { 17 } else { 13 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            generate_cards_in_discard(state, content, CardId::Wound, 2, owner)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::FlakCannon => {
            let dmg = Dec::from_i32(if upg { 11 } else { 8 });
            let mut statuses: SmallVec<[CardHandle; 8]> = SmallVec::new();
            for h in state
                .player
                .hand
                .iter()
                .chain(state.player.draw.iter())
                .chain(state.player.discard.iter())
                .chain(state.player.play.iter())
                .copied()
            {
                if let Some(ci) = state.card(h) {
                    if content.card_meta(ci.id)?.card_type == CardType::Status {
                        statuses.push(h);
                    }
                }
            }
            let hits = statuses.len() as i32;
            for h in statuses {
                card_pile::exhaust(state, content, h, false)?;
            }
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                dmg,
                AttackTargetSpec::RandomOpponents {
                    allow_duplicates: true,
                },
            )
            .with_hit_count(hits)
            .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::FocusedStrike => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 11 } else { 9 });
            let focus = Dec::from_i32(if upg { 2 } else { 1 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(
                state,
                content,
                PowerId::FocusedStrikePower,
                owner,
                focus,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Ftl => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 6 } else { 5 });
            let play_max = if upg { 4 } else { 3 };
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            if state.player.counters.cards_played_this_turn < play_max {
                let _ = card_pile::draw(state, content, 1, false)?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::Fusion => {
            orb::channel(state, content, OrbId::PlasmaOrb)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::GeneticAlgorithm => {
            let blk = Dec::from_i32(read_ga_current_block(state, ctx.card));
            let inc = read_ga_increase(state, ctx.card);
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            buff_genetic_algorithm(state, ctx.card, inc);
            if let Some(deck_h) = card.deck_card_handle.get() {
                if state
                    .card(deck_h)
                    .is_some_and(|c| c.id == CardId::GeneticAlgorithm)
                {
                    buff_genetic_algorithm(state, deck_h, inc);
                    state
                        .semantic_history
                        .record_growth(sts2_kernel::GrowthSource::GeneticAlgorithm);
                }
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::Glacier => {
            let blk = Dec::from_i32(if upg { 9 } else { 6 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            for _ in 0..2 {
                orb::channel(state, content, OrbId::FrostOrb)?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::Glasswork => {
            let blk = Dec::from_i32(if upg { 8 } else { 5 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            orb::channel(state, content, OrbId::GlassOrb)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::GoForTheEyes => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 4 } else { 3 });
            let weak = Dec::from_i32(if upg { 2 } else { 1 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            if target_intends_attack(content, state, target)? {
                power::apply(
                    state,
                    content,
                    PowerId::WeakPower,
                    target,
                    weak,
                    Some(owner),
                    Some(ctx.card),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::GunkUp => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 5 } else { 4 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .with_hit_count(3)
                .execute(state, content)?;
            generate_cards_in_discard(state, content, CardId::Slimed, 1, owner)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Hailstorm => {
            let amt = Dec::from_i32(if upg { 8 } else { 6 });
            power::apply(
                state,
                content,
                PowerId::HailstormPower,
                owner,
                amt,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::HelixDrill => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 5 } else { 3 });
            let cur_tk = state.current_turn_key();
            let spent_this_turn: i32 = state
                .semantic_history
                .energy_spent
                .iter()
                .filter(|(tk, _, _)| *tk == cur_tk)
                .map(|(_, _, amt)| *amt)
                .sum();
            let hits = spent_this_turn - ctx.energy_spent;
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .with_hit_count(hits)
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Hibernate => {
            power::apply(
                state,
                content,
                PowerId::HibernatePower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            let frost_count = if upg { 3 } else { 2 };
            for _ in 0..frost_count {
                orb::channel(state, content, OrbId::FrostOrb)?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::Hologram => {
            let blk = Dec::from_i32(if upg { 5 } else { 3 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            if hooks::is_ending(state) || state.player_creature().is_none_or(|c| c.is_dead()) {
                return Ok(CardPlayOutcome::default());
            }
            let candidates: SmallVec<[CardHandle; 16]> = state.player.discard.iter().copied().collect();
            match card_select::select_cards(
                state,
                &candidates,
                1,
                1,
                ChoiceTiming::Action,
                card.id.model_id_str(),
                PileType::Discard,
                false,
                choice_queue,
            )? {
                SelectResult::Selected(selected) => {
                    if let Some(&chosen) = selected.first() {
                        card_pile::add_sync(state, chosen, PileType::Hand, CardPilePosition::Bottom);
                    }
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                    needs_choice: Some((req, 1)),
                    transform_into: None,
                }),
            }
        }

        CardId::Hotfix => {
            power::apply(
                state,
                content,
                PowerId::HotfixPower,
                owner,
                Dec::TWO,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Hyperbeam => {
            let dmg = Dec::from_i32(if upg { 30 } else { 24 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
                .execute(state, content)?;
            power::apply(
                state,
                content,
                PowerId::HyperbeamFocusDownPower,
                owner,
                Dec::from_i32(3),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::IceLance => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 24 } else { 19 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            for _ in 0..3 {
                orb::channel(state, content, OrbId::FrostOrb)?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::Ignition => {
            let target = require_target(ctx, card.id)?;
            if state.creature(target).is_none_or(|c| !c.is_player) {
                return Err(KernelError::InvalidAction(
                    "Ignition target must be a player.".to_owned(),
                ));
            }
            if target != owner {
                return Err(KernelError::Unsupported {
                    kind: "MultiplayerTarget",
                    id: card.id.model_id_str().to_owned(),
                });
            }
            orb::channel(state, content, OrbId::PlasmaOrb)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::ImitationLearning => {
            let target = require_target(ctx, card.id)?;
            if state.creature(target).is_none_or(|c| !c.is_player) {
                return Err(KernelError::InvalidAction(
                    "Imitation Learning target must be a player.".to_owned(),
                ));
            }
            let amt = Dec::from_i32(if upg { 3 } else { 2 });
            let existing_idx = state.creature(owner).and_then(|c| {
                c.powers.iter().position(|p| {
                    p.id == PowerId::ImitationLearningPower
                        && p.ext_index
                            .and_then(|ei| state.power_extensions.get(ei as usize))
                            .and_then(|ext| {
                                ext.private_fields.get("ImitationLearningPower._playerTarget")
                            })
                            .is_some_and(|v| {
                                matches!(v, FieldValue::CreatureHandle(h) | FieldValue::Int(h) if *h == target.0 as i32)
                            })
                })
            });
            if let Some(idx) = existing_idx {
                if !hooks::is_ending(state)
                    && state
                        .creature(owner)
                        .is_some_and(|c| !c.is_detached && state.contains_creature(owner))
                    && hooks::should_allow_hitting(state, content, owner)?
                {
                    let _ = power::modify_amount(
                        state,
                        content,
                        owner,
                        idx,
                        amt,
                        Some(owner),
                        Some(ctx.card),
                    )?;
                }
            } else if !hooks::is_ending(state)
                && state
                    .creature(owner)
                    .is_some_and(|c| !c.is_detached && state.contains_creature(owner))
                && hooks::should_allow_hitting(state, content, owner)?
            {
                let mut ext = PowerExtension::default();
                ext.private_fields.insert(
                    Cow::Borrowed("ImitationLearningPower._playerTarget"),
                    FieldValue::CreatureHandle(target.0 as i32),
                );
                let _ = power::apply_new_with_extension(
                    state,
                    content,
                    PowerId::ImitationLearningPower,
                    owner,
                    amt,
                    Some(owner),
                    Some(ctx.card),
                    ext,
                )?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::Iteration => {
            let amt = Dec::from_i32(if upg { 3 } else { 2 });
            power::apply(
                state,
                content,
                PowerId::IterationPower,
                owner,
                amt,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Leap => {
            let blk = Dec::from_i32(if upg { 12 } else { 9 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::LightningRod => {
            let blk = Dec::from_i32(if upg { 7 } else { 4 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            power::apply(
                state,
                content,
                PowerId::LightningRodPower,
                owner,
                Dec::TWO,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Loop => {
            let amt = Dec::from_i32(if upg { 2 } else { 1 });
            power::apply(
                state,
                content,
                PowerId::LoopPower,
                owner,
                amt,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::MachineLearning => {
            power::apply(
                state,
                content,
                PowerId::MachineLearningPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::MeteorStrike => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 30 } else { 24 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            for _ in 0..3 {
                orb::channel(state, content, OrbId::PlasmaOrb)?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::Modded => {
            orb::add_slots(state, 1);
            let _ = card_pile::draw(state, content, if upg { 2 } else { 1 }, false)?;
            add_energy_cost_this_combat(state, ctx.card, 1);
            Ok(CardPlayOutcome::default())
        }

        CardId::MomentumStrike => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 15 } else { 11 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            set_temp_cost_this_combat(state, ctx.card, 0);
            Ok(CardPlayOutcome::default())
        }

        CardId::MultiCast => {
            let x_val = resolve_x_value(content, state, ctx)?;
            let evokes = x_val + if upg { 1 } else { 0 };
            for i in 0..evokes {
                orb::evoke_next(state, content, i == evokes - 1)?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::Null => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 13 } else { 10 });
            let weak = Dec::from_i32(if upg { 2 } else { 1 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(
                state,
                content,
                PowerId::WeakPower,
                target,
                weak,
                Some(owner),
                Some(ctx.card),
            )?;
            orb::channel(state, content, OrbId::DarkOrb)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::OneForAll => {
            let amt = Dec::from_i32(if upg { 4 } else { 3 });
            let allies: SmallVec<[CreatureHandle; 8]> = SmallVec::from_slice(&state.ally_handles);
            for ah in allies {
                if state.creature(ah).is_some_and(|c| c.is_player) {
                    power::apply(
                        state,
                        content,
                        PowerId::OneForAllPower,
                        ah,
                        amt,
                        Some(owner),
                        Some(ctx.card),
                    )?;
                }
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::Overclock => {
            let _ = card_pile::draw(state, content, if upg { 3 } else { 2 }, false)?;
            generate_cards_in_discard(state, content, CardId::Burn, 1, owner)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Quadcast => {
            if !state.player.orbs.is_empty() {
                for i in 0..4 {
                    orb::evoke_next(state, content, i == 3)?;
                }
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::Rainbow => {
            orb::channel(state, content, OrbId::LightningOrb)?;
            orb::channel(state, content, OrbId::FrostOrb)?;
            orb::channel(state, content, OrbId::DarkOrb)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Reboot => {
            let hand_snapshot: SmallVec<[CardHandle; 16]> =
                SmallVec::from_slice(&state.player.hand);
            for ch in hand_snapshot {
                card_pile::add_sync(state, ch, PileType::Draw, CardPilePosition::Bottom);
            }
            card_pile::shuffle(state, content)?;
            if state.pending.is_none() {
                let _ = card_pile::draw(state, content, if upg { 6 } else { 4 }, false)?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::Refract => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 13 } else { 10 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .with_hit_count(2)
                .execute(state, content)?;
            for _ in 0..2 {
                orb::channel(state, content, OrbId::GlassOrb)?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::RocketPunch => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 14 } else { 13 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let _ = card_pile::draw(state, content, if upg { 2 } else { 1 }, false)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Scavenge => {
            let energy = Dec::from_i32(if upg { 3 } else { 2 });
            if hooks::is_over_or_ending(state) || state.player_creature().is_none_or(|c| c.is_dead())
            {
                power::apply(
                    state,
                    content,
                    PowerId::EnergyNextTurnPower,
                    owner,
                    energy,
                    Some(owner),
                    Some(ctx.card),
                )?;
                return Ok(CardPlayOutcome::default());
            }
            let candidates: SmallVec<[CardHandle; 16]> = state.player.hand.iter().copied().collect();
            match card_select::select_cards(
                state,
                &candidates,
                1,
                1,
                ChoiceTiming::Action,
                card.id.model_id_str(),
                PileType::Hand,
                false,
                choice_queue,
            )? {
                SelectResult::Selected(selected) => {
                    if let Some(&chosen) = selected.first() {
                        card_pile::exhaust(state, content, chosen, false)?;
                    }
                    power::apply(
                        state,
                        content,
                        PowerId::EnergyNextTurnPower,
                        owner,
                        energy,
                        Some(owner),
                        Some(ctx.card),
                    )?;
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                    needs_choice: Some((req, 1)),
                    transform_into: None,
                }),
            }
        }

        CardId::Scrape => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 10 } else { 7 });
            let draw_count = if upg { 5 } else { 4 };
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let drawn = card_pile::draw(state, content, draw_count, false)?;
            let mut to_discard: SmallVec<[CardHandle; 8]> = SmallVec::new();
            for ch in drawn {
                let Some(ci) = state.card(ch) else {
                    continue;
                };
                let is_x = content
                    .card_meta(ci.id)?
                    .flags
                    .contains(CardStaticFlags::IS_X_ENERGY_COST);
                if is_x || ordering::resolve_energy_cost(state, content, ch)? != 0 {
                    to_discard.push(ch);
                }
            }
            if !to_discard.is_empty() {
                card_cmd::discard_batch(state, content, &to_discard)?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::ShadowShield => {
            let blk = Dec::from_i32(if upg { 15 } else { 11 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            orb::channel(state, content, OrbId::DarkOrb)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Shatter => {
            let dmg = Dec::from_i32(if upg { 11 } else { 7 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
                .execute(state, content)?;
            let orb_count = state.player.orbs.len();
            for _ in 0..orb_count {
                orb::evoke_next(state, content, false)?;
                orb::evoke_next(state, content, true)?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::SignalBoost => {
            power::apply(
                state,
                content,
                PowerId::SignalBoostPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Skim => {
            let _ = card_pile::draw(state, content, if upg { 4 } else { 3 }, false)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Smokestack => {
            let amt = Dec::from_i32(if upg { 7 } else { 5 });
            power::apply(
                state,
                content,
                PowerId::SmokestackPower,
                owner,
                amt,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Spinner => {
            if upg {
                orb::channel(state, content, OrbId::GlassOrb)?;
            }
            power::apply(
                state,
                content,
                PowerId::SpinnerPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Storm => {
            let amt = Dec::from_i32(if upg { 2 } else { 1 });
            power::apply(
                state,
                content,
                PowerId::StormPower,
                owner,
                amt,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::StrikeDefect => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 9 } else { 6 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Subroutine => {
            power::apply(
                state,
                content,
                PowerId::SubroutinePower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Sunder => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 34 } else { 26 });
            let results =
                AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                    .execute(state, content)?;
            if results.iter().any(|r| r.was_target_killed) {
                gold::gain_energy(state, Dec::from_i32(3))?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::Supercritical => {
            gold::gain_energy(state, Dec::from_i32(if upg { 6 } else { 4 }))?;
            Ok(CardPlayOutcome::default())
        }

        CardId::SweepingBeam => {
            let dmg = Dec::from_i32(if upg { 9 } else { 6 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
                .execute(state, content)?;
            let _ = card_pile::draw(state, content, 1, false)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Synchronize => {
            let distinct = distinct_orb_count(state);
            let per_orb = if upg { 2 } else { 1 };
            power::apply(
                state,
                content,
                PowerId::SynchronizePower,
                owner,
                Dec::from_i32(distinct * per_orb),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Synthesis => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 20 } else { 14 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(
                state,
                content,
                PowerId::FreePowerPower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Tempest => {
            let x_val = resolve_x_value(content, state, ctx)?;
            let count = x_val + if upg { 1 } else { 0 };
            for _ in 0..count {
                orb::channel(state, content, OrbId::LightningOrb)?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::TeslaCoil => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 4 } else { 3 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let orb_count = state.player.orbs.len();
            for idx in 0..orb_count {
                if state
                    .player
                    .orbs
                    .get(idx)
                    .is_some_and(|o| o.id == OrbId::LightningOrb)
                {
                    orb::passive(state, content, idx, Some(target), false)?;
                    if upg {
                        orb::passive(state, content, idx, Some(target), false)?;
                    }
                }
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::Thunder => {
            let amt = Dec::from_i32(if upg { 11 } else { 8 });
            power::apply(
                state,
                content,
                PowerId::ThunderPower,
                owner,
                amt,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::TrashToTreasure => {
            power::apply(
                state,
                content,
                PowerId::TrashToTreasurePower,
                owner,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Turbo => {
            gold::gain_energy(state, Dec::from_i32(if upg { 3 } else { 2 }))?;
            generate_cards_in_discard(state, content, CardId::Void, 1, owner)?;
            Ok(CardPlayOutcome::default())
        }

        CardId::Uproar => {
            let target = require_target(ctx, card.id)?;
            let dmg = Dec::from_i32(if upg { 8 } else { 6 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .with_hit_count(2)
                .execute(state, content)?;
            let draw_snapshot: SmallVec<[CardHandle; 32]> =
                SmallVec::from_slice(&state.player.draw);
            let mut playable_attacks: Vec<CardHandle> = Vec::new();
            for ch in &draw_snapshot {
                let Some(ci) = state.card(*ch) else {
                    continue;
                };
                if content.card_meta(ci.id)?.card_type == CardType::Attack
                    && !card_pile::card_has_keyword(state, content, *ch, CardKeywords::UNPLAYABLE)?
                {
                    playable_attacks.push(*ch);
                }
            }
            let mut sel_rng = *state.run_rngs.get(RunRngType::CombatCardSelection);
            ordering::stable_shuffle_cards(state, content, &mut playable_attacks, &mut sel_rng)?;
            *state.run_rngs.get_mut(RunRngType::CombatCardSelection) = sel_rng;
            let mut chosen = playable_attacks.first().copied();
            if chosen.is_none() {
                let mut all_attacks: Vec<CardHandle> = Vec::new();
                for ch in &draw_snapshot {
                    let Some(ci) = state.card(*ch) else {
                        continue;
                    };
                    if content.card_meta(ci.id)?.card_type == CardType::Attack {
                        all_attacks.push(*ch);
                    }
                }
                let mut sel_rng2 = *state.run_rngs.get(RunRngType::CombatCardSelection);
                ordering::stable_shuffle_cards(state, content, &mut all_attacks, &mut sel_rng2)?;
                *state.run_rngs.get_mut(RunRngType::CombatCardSelection) = sel_rng2;
                chosen = all_attacks.first().copied();
            }
            if let Some(chosen_h) = chosen {
                let _ = autoplay::from_cards(state, content, &[chosen_h], None, false)?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::Voltaic => {
            let count = state
                .semantic_history
                .orb_channels
                .iter()
                .filter(|(_, _, orb_id)| *orb_id == OrbId::LightningOrb)
                .count();
            for _ in 0..count {
                orb::channel(state, content, OrbId::LightningOrb)?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::WhiteNoise => {
            let pool = card_factory::character_combat_pool(state);
            let generated = card_factory::get_distinct_for_combat_filtered(
                state,
                &pool,
                1,
                RunRngType::CombatCardGeneration,
                |id| generated_card_meta(id).card_type == CardType::Power,
            );
            if let Some(&gen_id) = generated.first() {
                let gen_h = card_pile::create_card_in_combat(state, content, gen_id, 0)?;
                set_to_free_this_turn(content, state, gen_h)?;
                card_pile::enter_combat(
                    state,
                    content,
                    gen_h,
                    PileType::Hand,
                    CardPilePosition::Bottom,
                    true,
                )?;
                hooks::after_card_generated(state, content, gen_h, Some(owner))?;
                hooks::after_card_generated_for_combat(state, content, gen_h, Some(owner))?;
            }
            Ok(CardPlayOutcome::default())
        }

        CardId::Zap => {
            orb::channel(state, content, OrbId::LightningOrb)?;
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
        CardId::Hologram => {
            if let Some(&chosen) = selected.first() {
                card_pile::add_sync(state, chosen, PileType::Hand, CardPilePosition::Bottom);
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Scavenge => {
            if let Some(&chosen) = selected.first() {
                card_pile::exhaust(state, content, chosen, false)?;
            }
            let energy = Dec::from_i32(if upg { 3 } else { 2 });
            power::apply(
                state,
                content,
                PowerId::EnergyNextTurnPower,
                owner,
                energy,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        _ => Err(KernelError::Unsupported {
            kind: "CardResume",
            id: card.id.model_id_str().to_owned(),
        }),
    }
}

pub fn on_upgrade(state: &mut BattleState, card_handle: CardHandle) -> Result<(), KernelError> {
    let id = state
        .card(card_handle)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    let _ = card_meta(id)?;
    match id {
        CardId::CreativeAi
        | CardId::DoubleEnergy
        | CardId::Dualcast
        | CardId::Feral
        | CardId::Quadcast
        | CardId::SignalBoost
        | CardId::Subroutine
        | CardId::TrashToTreasure
        | CardId::WhiteNoise
        | CardId::Zap => {
            if let Some(c) = state.card_mut(card_handle) {
                c.base_energy_cost = (c.base_energy_cost - 1).max(0);
            }
        }
        CardId::Chill
        | CardId::Fusion
        | CardId::Hologram
        | CardId::Hotfix
        | CardId::Rainbow
        | CardId::Voltaic => {
            if let Some(c) = state.card_mut(card_handle) {
                c.local_keywords.remove(CardKeywords::EXHAUST);
            }
        }
        CardId::EchoForm => {
            if let Some(c) = state.card_mut(card_handle) {
                c.local_keywords.remove(CardKeywords::ETHEREAL);
            }
        }
        CardId::MachineLearning => {
            if let Some(c) = state.card_mut(card_handle) {
                c.local_keywords.insert(CardKeywords::INNATE);
            }
        }
        CardId::Claw => {
            if let Some(ext) = state.card(card_handle).and_then(|c| state.card_ext(c.ext_index)) {
                let cur_dmg = ext.private_fields.get("Claw._damage").cloned();
                let cur_inc = ext.private_fields.get("Claw._increase").cloned();
                if let Some(ext_mut) = state.card_ext_mut(card_handle) {
                    if let Some(FieldValue::Decimal(d)) = cur_dmg {
                        ext_mut.private_fields.insert(
                            Cow::Borrowed("Claw._damage"),
                            FieldValue::Decimal(d.checked_add(Dec::ONE)?),
                        );
                    }
                    if let Some(FieldValue::Decimal(i)) = cur_inc {
                        ext_mut.private_fields.insert(
                            Cow::Borrowed("Claw._increase"),
                            FieldValue::Decimal(i.checked_add(Dec::ONE)?),
                        );
                    }
                }
            }
        }
        CardId::GeneticAlgorithm => {
            if let Some(ext) = state.card(card_handle).and_then(|c| state.card_ext(c.ext_index)) {
                if let Some(&FieldValue::Int(i)) = ext.private_fields.get("GeneticAlgorithm._increase") {
                    if let Some(ext_mut) = state.card_ext_mut(card_handle) {
                        ext_mut.private_fields.insert(
                            Cow::Borrowed("GeneticAlgorithm._increase"),
                            FieldValue::Int(i + 1),
                        );
                    }
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
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    let _ = card_meta(id)?;
    match id {
        CardId::Claw => Ok(smallvec![("Damage", read_claw_damage(state, card))]),
        CardId::GeneticAlgorithm => Ok(smallvec![(
            "Block",
            Dec::from_i32(read_ga_current_block(state, card)),
        )]),
        _ => Ok(SmallVec::new()),
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
    let _ = card_meta(id)?;
    match id {
        CardId::RocketPunch => {
            if slot == HookSlot::AfterCardGeneratedForCombat {
                if let HookArgs::CardGenerated {
                    card: generated,
                    creator,
                } = args
                {
                    if *creator == Some(state.player.creature_handle) {
                        if let Some(gen_ci) = state.card(*generated) {
                            if content.card_meta(gen_ci.id)?.card_type == CardType::Status {
                                add_energy_cost_until_played(state, card, -1);
                            }
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        _ => Err(KernelError::Unsupported {
            kind: "CardHook",
            id: id.model_id_str().to_owned(),
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
    Ok(None)
}
