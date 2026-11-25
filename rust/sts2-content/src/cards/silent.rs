use smallvec::SmallVec;
use std::borrow::Cow;
use std::sync::LazyLock;
use sts2_kernel::commands::card_select::SelectResult;
use sts2_kernel::commands::{
    autoplay, block, card as card_cmd, card_pile, card_select, damage,
    damage::{AttackBuilder, AttackTargetSpec},
    gold, power,
};
use sts2_kernel::content_dispatch::{AttackContextInfo, CardPlayContext, CardPlayOutcome};
use sts2_kernel::{
    hooks, BattleState, CardHandle, CardTempFlags, ChoiceTiming, ContentDispatcher, CreatureHandle,
    EnchantmentInst, FieldValue, HookArgs, HookListener, HookResult, KernelError,
    LocalEnergyModifier,
};
use sts2_types::{
    card_partition, generated_ids::CARD_META_TABLE, CardId, CardKeywords, CardMeta,
    CardPartition, CardPilePosition, CardStaticFlags, CardTags, CardType, CombatSide, Dec,
    EnchantmentId, EnchantmentStatus, HookSlot, LocalEnergyModifierKind, PileType, PowerId,
    RunRngType, ValueProp,
};

static SILENT_CARD_META: LazyLock<[CardMeta; CardId::COUNT]> = LazyLock::new(|| {
    std::array::from_fn(|i| {
        let mut meta = CARD_META_TABLE[i];
        if i == CardId::TheHunt as usize {
            meta.flags |= CardStaticFlags::HAS_GROWTH;
        }
        if i == CardId::BouncingFlask as usize || i == CardId::Ricochet as usize {
            meta.flags |= CardStaticFlags::USES_RNG;
        }
        if is_order_sensitive_index(i) {
            meta.flags |= CardStaticFlags::ORDER_SENSITIVE;
        }
        meta
    })
});

const fn is_order_sensitive_index(i: usize) -> bool {
    i == CardId::Acrobatics as usize
        || i == CardId::Adrenaline as usize
        || i == CardId::Backflip as usize
        || i == CardId::BladeDance as usize
        || i == CardId::BladeOfInk as usize
        || i == CardId::BladeSymphony as usize
        || i == CardId::BubbleBubble as usize
        || i == CardId::BulletTime as usize
        || i == CardId::Burst as usize
        || i == CardId::CalculatedGamble as usize
        || i == CardId::CloakAndDagger as usize
        || i == CardId::DaggerThrow as usize
        || i == CardId::EchoingSlash as usize
        || i == CardId::EscapePlan as usize
        || i == CardId::Expertise as usize
        || i == CardId::Expose as usize
        || i == CardId::FanOfKnives as usize
        || i == CardId::Finisher as usize
        || i == CardId::Flechettes as usize
        || i == CardId::GrandFinale as usize
        || i == CardId::HandTrick as usize
        || i == CardId::HiddenDaggers as usize
        || i == CardId::KnifeTrap as usize
        || i == CardId::LeadingStrike as usize
        || i == CardId::Mirage as usize
        || i == CardId::Murder as usize
        || i == CardId::Nightmare as usize
        || i == CardId::Outbreak as usize
        || i == CardId::Pinpoint as usize
        || i == CardId::Pounce as usize
        || i == CardId::PreciseCut as usize
        || i == CardId::Prepared as usize
        || i == CardId::Reflex as usize
        || i == CardId::ShadowStep as usize
        || i == CardId::StormOfSteel as usize
        || i == CardId::Survivor as usize
        || i == CardId::Tactician as usize
        || i == CardId::UpMySleeve as usize
}

pub fn card_meta(id: CardId) -> Result<&'static CardMeta, KernelError> {
    if card_partition(id) == CardPartition::Silent {
        Ok(&SILENT_CARD_META[id as usize])
    } else {
        Err(KernelError::unsupported("Card", id.model_id_str().to_owned()))
    }
}

pub fn is_playable(state: &BattleState, card: CardHandle) -> Result<bool, KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    let _ = card_meta(id)?;
    match id {
        CardId::GrandFinale => Ok(state.player.draw.is_empty()),
        _ => Ok(true),
    }
}

#[inline]
fn require_target(ctx: &CardPlayContext, name: &str) -> Result<CreatureHandle, KernelError> {
    ctx.target
        .ok_or_else(|| KernelError::InvalidAction(format!("{name} requires target")))
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

fn create_shivs_in_hand(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    owner: CreatureHandle,
    count: usize,
    upgrade_after: bool,
    enchant_inky: bool,
) -> Result<(), KernelError> {
    if count == 0 || hooks::is_over_or_ending(state) {
        return Ok(());
    }
    let mut created = SmallVec::<[CardHandle; 8]>::new();
    for _ in 0..count {
        let shiv_h = card_pile::add_generated_card_to_combat(
            state,
            content,
            CardId::Shiv,
            PileType::Hand,
            CardPilePosition::Bottom,
            0,
            Some(owner),
        )?;
        if enchant_inky {
            if let Some(ext) = state.card_ext_mut(shiv_h) {
                ext.enchantment = Some(EnchantmentInst {
                    id: EnchantmentId::Inky,
                    amount: 1,
                    status: EnchantmentStatus::Normal,
                    private_fields: Default::default(),
                });
                state.invalidate_hook_cache();
            }
        }
        created.push(shiv_h);
    }
    if upgrade_after {
        for shiv_h in created {
            card_cmd::upgrade(state, content, shiv_h)?;
        }
    }
    Ok(())
}

/// Exact port of `CardCmd.DiscardAndDraw` when `cards_to_draw > 0` must happen
/// after `AfterCardDiscarded` hooks and before `NotifyHandDeparture` / `AutoPlayCmd` (Sly).
fn discard_and_draw(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    cards: &[CardHandle],
    draw_count: usize,
) -> Result<(), KernelError> {
    if cards.is_empty() || hooks::is_over_or_ending(state) {
        return Ok(());
    }
    let mut departed_hand = false;
    let mut sly_cards = SmallVec::<[CardHandle; 8]>::new();
    for &h in cards {
        if state.card(h).is_some_and(|c| c.pile == PileType::Hand) {
            departed_hand = true;
            if card_pile::card_has_keyword(state, content, h, CardKeywords::SLY)? {
                sly_cards.push(h);
            }
        }
    }
    for &h in cards {
        card_pile::add_sync(state, h, PileType::Discard, CardPilePosition::Bottom);
        state.player.counters.cards_discarded_this_turn += 1;
        hooks::after_card_discarded(state, content, h)?;
    }
    if draw_count > 0 {
        let _ = card_pile::draw(state, content, draw_count as i32, false)?;
    }
    card_pile::notify_hand_departure(state, content, departed_hand)?;
    if !sly_cards.is_empty() {
        autoplay::from_cards(state, content, &sly_cards, None, false)?;
    }
    Ok(())
}

fn add_local_energy_modifier(
    state: &mut BattleState,
    card: CardHandle,
    kind: LocalEnergyModifierKind,
    amount: i32,
) {
    if amount == 0 {
        return;
    }
    if let Some(ext) = state.card_ext_mut(card) {
        ext.push_local_energy_modifier(LocalEnergyModifier {
            kind,
            amount,
            is_reduce_only: false,
        });
    }
}

fn set_to_free_this_turn(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    handle: CardHandle,
) -> Result<(), KernelError> {
    let Some(card) = state.card(handle) else {
        return Ok(());
    };
    let meta = content.card_meta(card.id)?;
    if !meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST) {
        if let Some(c) = state.card_mut(handle) {
            c.temp_cost_override_this_turn_or_until_played = sts2_kernel::OptI16::some(0);
            state.mark_eot_card_cleanup_dirty();
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
            state.mark_eot_card_cleanup_dirty();
        }
    }
    Ok(())
}

fn apply_nightmare_to_selected(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    owner: CreatureHandle,
    source_card: CardHandle,
    selected: &[CardHandle],
) -> Result<(), KernelError> {
    if let Some(&chosen) = selected.first() {
        if let Some(idx) = power::apply(
            state,
            content,
            PowerId::NightmarePower,
            owner,
            Dec::from_i32(3),
            Some(owner),
            Some(source_card),
        )? {
            let power_handle = state
                .creature(owner)
                .and_then(|c| c.powers.get(idx))
                .ok_or_else(|| KernelError::InvalidState("missing NightmarePower".to_owned()))?
                .handle;
            crate::powers::batch_n_z::nightmare_set_selected_card(
                state,
                owner,
                power_handle,
                chosen,
            )?;
        }
    }
    Ok(())
}

fn discard_from_hand_select(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    source_id: &'static str,
    count: usize,
    choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
) -> Result<CardPlayOutcome, KernelError> {
    match card_select::from_hand_for_discard(state, count, source_id, choice_queue)? {
        SelectResult::Selected(selected) => {
            if !selected.is_empty() {
                card_cmd::discard_batch(state, content, &selected)?;
            }
            Ok(CardPlayOutcome::default())
        }
        SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome::with_choice(req, 1)),
    }
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
    let src = Some(ctx.card);

    match card.id {
        CardId::Abrasive => {
            power::apply(state, content, PowerId::DexterityPower, owner, Dec::ONE, Some(owner), src)?;
            let thorns = Dec::from_i32(if upg { 6 } else { 4 });
            power::apply(state, content, PowerId::ThornsPower, owner, thorns, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Accelerant => {
            let amt = Dec::from_i32(if upg { 2 } else { 1 });
            power::apply(state, content, PowerId::AccelerantPower, owner, amt, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Accuracy => {
            let amt = Dec::from_i32(if upg { 6 } else { 4 });
            power::apply(state, content, PowerId::AccuracyPower, owner, amt, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Acrobatics => {
            let draw_count = if upg { 4 } else { 3 };
            let _ = card_pile::draw(state, content, draw_count, false)?;
            discard_from_hand_select(content, state, card.id.model_id_str(), 1, choice_queue)
        }
        CardId::Adrenaline => {
            let energy = Dec::from_i32(if upg { 2 } else { 1 });
            gold::gain_energy(state, content, energy)?;
            let _ = card_pile::draw(state, content, 2, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Afterimage => {
            power::apply(state, content, PowerId::AfterimagePower, owner, Dec::ONE, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Anticipate => {
            let amt = Dec::from_i32(if upg { 4 } else { 2 });
            power::apply(state, content, PowerId::AnticipatePower, owner, amt, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Assassinate => {
            let target = require_target(ctx, "Assassinate")?;
            let dmg = Dec::from_i32(if upg { 13 } else { 10 });
            let vuln = Dec::from_i32(if upg { 2 } else { 1 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(state, content, PowerId::VulnerablePower, target, vuln, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Backflip => {
            let blk = Dec::from_i32(if upg { 8 } else { 5 });
            block::gain_block(state, content, owner, blk, ValueProp::MOVE, src, Some(*ctx))?;
            let _ = card_pile::draw(state, content, 2, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Backstab => {
            let target = require_target(ctx, "Backstab")?;
            let dmg = Dec::from_i32(if upg { 15 } else { 11 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::BladeOfInk => {
            let count = if upg { 3 } else { 2 };
            create_shivs_in_hand(state, content, owner, count, false, true)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::BladeDance => {
            let count = if upg { 4 } else { 3 };
            create_shivs_in_hand(state, content, owner, count, false, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::BladeSymphony => Ok(CardPlayOutcome::default()),
        CardId::Blur => {
            let blk = Dec::from_i32(if upg { 8 } else { 5 });
            block::gain_block(state, content, owner, blk, ValueProp::MOVE, src, Some(*ctx))?;
            power::apply(state, content, PowerId::BlurPower, owner, Dec::ONE, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::BouncingFlask => {
            let repeats = if upg { 4 } else { 3 };
            for _ in 0..repeats {
                let enemies = collect_hittable_enemies(state, content)?;
                let chosen = state
                    .run_rngs
                    .get_mut(RunRngType::CombatTargets)
                    .next_item(&enemies)
                    .copied();
                if let Some(enemy) = chosen {
                    power::apply(
                        state,
                        content,
                        PowerId::PoisonPower,
                        enemy,
                        Dec::from_i32(3),
                        Some(owner),
                        src,
                    )?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::BubbleBubble => {
            let target = require_target(ctx, "BubbleBubble")?;
            let has_poison = state
                .creature(target)
                .is_some_and(|c| c.powers.iter().any(|p| p.id == PowerId::PoisonPower));
            if has_poison {
                let poison = Dec::from_i32(if upg { 12 } else { 9 });
                power::apply(state, content, PowerId::PoisonPower, target, poison, Some(owner), src)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::BulletTime => {
            let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
            for h in hand {
                set_to_free_this_turn(state, content, h)?;
            }
            power::apply(state, content, PowerId::NoDrawPower, owner, Dec::ONE, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Burst => {
            let amt = Dec::from_i32(if upg { 2 } else { 1 });
            power::apply(state, content, PowerId::BurstPower, owner, amt, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::CalculatedGamble => {
            let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
            discard_and_draw(state, content, &hand, hand.len())?;
            Ok(CardPlayOutcome::default())
        }
        CardId::CloakAndDagger => {
            block::gain_block(
                state,
                content,
                owner,
                Dec::from_i32(6),
                ValueProp::MOVE,
                src,
                Some(*ctx),
            )?;
            let count = if upg { 2 } else { 1 };
            create_shivs_in_hand(state, content, owner, count, false, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::CorrosiveWave => {
            let amt = Dec::from_i32(if upg { 3 } else { 2 });
            power::apply(state, content, PowerId::CorrosiveWavePower, owner, amt, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Concoct => {
            let target = require_target(ctx, "Concoct")?;
            let amt = Dec::from_i32(if upg { 4 } else { 3 });
            power::apply(state, content, PowerId::ConcoctPower, target, amt, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DaggerSpray => {
            let dmg = Dec::from_i32(if upg { 6 } else { 4 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
                .with_hit_count(2)
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DaggerThrow => {
            let target = require_target(ctx, "DaggerThrow")?;
            let dmg = Dec::from_i32(if upg { 12 } else { 9 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let _ = card_pile::draw(state, content, 1, false)?;
            discard_from_hand_select(content, state, card.id.model_id_str(), 1, choice_queue)
        }
        CardId::Dash => {
            let target = require_target(ctx, "Dash")?;
            let amt = Dec::from_i32(if upg { 13 } else { 10 });
            block::gain_block(state, content, owner, amt, ValueProp::MOVE, src, Some(*ctx))?;
            AttackBuilder::from_card(owner, ctx.card, *ctx, amt, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DeadlyPoison => {
            let target = require_target(ctx, "DeadlyPoison")?;
            let poison = Dec::from_i32(if upg { 7 } else { 5 });
            power::apply(state, content, PowerId::PoisonPower, target, poison, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DefendSilent => {
            let blk = Dec::from_i32(if upg { 8 } else { 5 });
            block::gain_block(state, content, owner, blk, ValueProp::MOVE, src, Some(*ctx))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Deflect => {
            let blk = Dec::from_i32(if upg { 7 } else { 4 });
            block::gain_block(state, content, owner, blk, ValueProp::MOVE, src, Some(*ctx))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DodgeAndRoll => {
            let blk = Dec::from_i32(if upg { 6 } else { 4 });
            let gained = block::gain_block(state, content, owner, blk, ValueProp::MOVE, src, Some(*ctx))?;
            power::apply(state, content, PowerId::BlockNextTurnPower, owner, gained, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::EchoingSlash => {
            let dmg = Dec::from_i32(if upg { 13 } else { 10 });
            let attacker_dead = state.creature(owner).is_none_or(|c| c.is_dead());
            if hooks::is_over_or_ending(state) || attacker_dead {
                return Ok(CardPlayOutcome::default());
            }
            let attack_id = state.next_attack_id;
            state.next_attack_id += 1;
            let info = AttackContextInfo {
                attack_id,
                attacker: owner,
                target_side: CombatSide::Enemy,
                card_source: src,
                damage_props: ValueProp::MOVE,
                hit_count: 1,
            };
            hooks::before_attack(state, content, info)?;
            let mut all_results = Vec::new();
            let mut attacks = 1i32;
            while attacks > 0 {
                attacks -= 1;
                if hooks::is_over_or_ending(state) || state.creature(owner).is_none_or(|c| c.is_dead()) {
                    break;
                }
                let targets = collect_hittable_enemies(state, content)?;
                let results = damage::damage_targets(
                    state,
                    content,
                    &targets,
                    dmg,
                    ValueProp::MOVE,
                    Some(owner),
                    src,
                    Some(*ctx),
                )?;
                attacks += results.iter().filter(|r| r.was_target_killed).count() as i32;
                all_results.extend(results);
            }
            hooks::after_attack(state, content, info, &all_results)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Envenom => {
            let amt = Dec::from_i32(if upg { 2 } else { 1 });
            power::apply(state, content, PowerId::EnvenomPower, owner, amt, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::EscapePlan => {
            let drawn = card_pile::draw(state, content, 1, false)?;
            if let Some(&drawn_h) = drawn.first() {
                if state.card(drawn_h).is_some()
                    && content.card_resolved_type(state, drawn_h)? == CardType::Skill
                {
                    let blk = Dec::from_i32(if upg { 5 } else { 3 });
                    block::gain_block(state, content, owner, blk, ValueProp::MOVE, src, Some(*ctx))?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Expertise => {
            let draw_count = if upg { 3 } else { 2 };
            let _ = card_pile::draw(state, content, draw_count, false)?;
            power::apply(state, content, PowerId::RetainHandPower, owner, Dec::ONE, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Expose => {
            let target = require_target(ctx, "Expose")?;
            let blk = state.creature(target).map_or(0, |c| c.block);
            if blk > 0 {
                block::lose_block(state, content, target, Dec::from_i32(blk))?;
            }
            let _ = power::remove_by_id(state, content, target, PowerId::ArtifactPower)?;
            let vuln = Dec::from_i32(if upg { 3 } else { 2 });
            power::apply(state, content, PowerId::VulnerablePower, target, vuln, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Fade => {
            let target = require_target(ctx, "Fade")?;
            let amt = Dec::from_i32(if upg { 9 } else { 6 });
            power::apply(state, content, PowerId::FadePower, target, amt, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::FanOfKnives => {
            power::apply(state, content, PowerId::FanOfKnivesPower, owner, Dec::ONE, Some(owner), src)?;
            let count = if upg { 5 } else { 4 };
            create_shivs_in_hand(state, content, owner, count, false, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Finisher => {
            let target = require_target(ctx, "Finisher")?;
            let dmg = Dec::from_i32(if upg { 8 } else { 6 });
            let hits = state.player.counters.attack_cards_played_this_turn;
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .with_hit_count(hits)
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Flanking => {
            let target = require_target(ctx, "Flanking")?;
            power::apply(state, content, PowerId::FlankingPower, target, Dec::TWO, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Flechettes => {
            let target = require_target(ctx, "Flechettes")?;
            let dmg = Dec::from_i32(if upg { 7 } else { 5 });
            let mut hits = 0i32;
            for &h in &state.player.hand {
                if state.card(h).is_some()
                    && content.card_resolved_type(state, h)? == CardType::Skill
                {
                    hits += 1;
                }
            }
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .with_hit_count(hits)
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::FlickFlack => {
            let dmg = Dec::from_i32(if upg { 9 } else { 7 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Sidestep => {
            let amt = Dec::from_i32(if upg { 2 } else { 1 });
            power::apply(state, content, PowerId::EnergyNextTurnPower, owner, amt, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Footwork => {
            let amt = Dec::from_i32(if upg { 3 } else { 2 });
            power::apply(state, content, PowerId::DexterityPower, owner, amt, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::GrandFinale => {
            let dmg = Dec::from_i32(if upg { 75 } else { 60 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::HandTrick => {
            let blk = Dec::from_i32(if upg { 10 } else { 7 });
            block::gain_block(state, content, owner, blk, ValueProp::MOVE, src, Some(*ctx))?;
            if hooks::is_over_or_ending(state) || state.player_creature().is_none_or(|c| c.is_dead()) {
                return Ok(CardPlayOutcome::default());
            }
            let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
            let mut candidates: SmallVec<[CardHandle; 16]> = SmallVec::new();
            for h in hand {
                if state.card(h).is_some()
                    && content.card_resolved_type(state, h)? == CardType::Skill
                    && !card_pile::card_has_keyword(state, content, h, CardKeywords::SLY)?
                    && !state
                        .card(h)
                        .is_some_and(|ci| ci.temp_flags.contains(CardTempFlags::TEMPORARY_SLY_TURN))
                {
                    candidates.push(h);
                }
            }
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
                        card_cmd::apply_single_turn_sly(state, chosen);
                    }
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome::with_choice(req, 1)),
            }
        }
        CardId::Haze => {
            let poison = Dec::from_i32(if upg { 6 } else { 4 });
            let weak = Dec::from_i32(if upg { 2 } else { 1 });
            let enemies_p = collect_hittable_enemies(state, content)?;
            for enemy in enemies_p {
                power::apply(state, content, PowerId::PoisonPower, enemy, poison, Some(owner), src)?;
            }
            let enemies_w = collect_hittable_enemies(state, content)?;
            for enemy in enemies_w {
                power::apply(state, content, PowerId::WeakPower, enemy, weak, Some(owner), src)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::HiddenDaggers => {
            match card_select::from_hand_for_discard(state, 2, card.id.model_id_str(), choice_queue)? {
                SelectResult::Selected(selected) => {
                    if !selected.is_empty() {
                        card_cmd::discard_batch(state, content, &selected)?;
                    }
                    create_shivs_in_hand(state, content, owner, 2, upg, false)?;
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome::with_choice(req, 1)),
            }
        }
        CardId::InfiniteBlades => {
            power::apply(state, content, PowerId::InfiniteBladesPower, owner, Dec::ONE, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::KnifeTrap => {
            let target = require_target(ctx, "KnifeTrap")?;
            let shivs: SmallVec<[CardHandle; 8]> = state
                .player
                .exhaust
                .iter()
                .copied()
                .filter(|&h| {
                    state.card(h).is_some_and(|c| {
                        content
                            .card_meta(c.id)
                            .is_ok_and(|m| m.base_tags.contains(CardTags::SHIV))
                    })
                })
                .collect();
            if upg {
                for &shiv_h in &shivs {
                    card_cmd::upgrade(state, content, shiv_h)?;
                }
            }
            if !shivs.is_empty() {
                autoplay::from_cards(state, content, &shivs, Some(target), false)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::LeadingStrike => {
            let target = require_target(ctx, "LeadingStrike")?;
            let dmg = Dec::from_i32(if upg { 6 } else { 3 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            create_shivs_in_hand(state, content, owner, 2, false, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::LegSweep => {
            let target = require_target(ctx, "LegSweep")?;
            let blk = Dec::from_i32(if upg { 14 } else { 11 });
            let weak = Dec::from_i32(if upg { 3 } else { 2 });
            block::gain_block(state, content, owner, blk, ValueProp::MOVE, src, Some(*ctx))?;
            power::apply(state, content, PowerId::WeakPower, target, weak, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Malaise => {
            let target = require_target(ctx, "Malaise")?;
            let x = hooks::modify_x_value(state, content, ctx.card, ctx.energy_value)?;
            let amt = x + if upg { 1 } else { 0 };
            power::apply(state, content, PowerId::StrengthPower, target, Dec::from_i32(-amt), Some(owner), src)?;
            power::apply(state, content, PowerId::WeakPower, target, Dec::from_i32(amt), Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::MasterPlanner => {
            power::apply(state, content, PowerId::MasterPlannerPower, owner, Dec::ONE, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::MementoMori => {
            let dmg = Dec::from_i32(if upg { 23 } else { 19 });
            let enemies = collect_hittable_enemies(state, content)?;
            let target = enemies.into_iter().min_by_key(|&h| {
                state.creature(h).map_or(i32::MAX, |c| c.current_hp)
            });
            if let Some(target) = target {
                AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                    .execute(state, content)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Mirage => {
            let total_poison: i32 = state
                .enemy_handles
                .iter()
                .filter_map(|&h| state.creature(h).filter(|c| c.is_alive()))
                .flat_map(|c| c.powers.iter())
                .filter(|p| p.id == PowerId::PoisonPower)
                .map(|p| p.amount)
                .sum();
            block::gain_block(
                state,
                content,
                owner,
                Dec::from_i32(total_poison),
                ValueProp::MOVE,
                src,
                Some(*ctx),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Murder => {
            let target = require_target(ctx, "Murder")?;
            let dmg = Dec::from_i32(1 + state.player.counters.cards_drawn_this_combat);
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Neutralize => {
            let target = require_target(ctx, "Neutralize")?;
            let dmg = Dec::from_i32(if upg { 4 } else { 3 });
            let weak = Dec::from_i32(if upg { 2 } else { 1 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(state, content, PowerId::WeakPower, target, weak, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Nightmare => {
            if hooks::is_over_or_ending(state) || state.player_creature().is_none_or(|c| c.is_dead()) {
                return Ok(CardPlayOutcome::default());
            }
            let candidates: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
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
                    apply_nightmare_to_selected(content, state, owner, ctx.card, &selected)?;
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome::with_choice(req, 1)),
            }
        }
        CardId::NoxiousFumes => {
            let amt = Dec::from_i32(if upg { 3 } else { 2 });
            power::apply(state, content, PowerId::NoxiousFumesPower, owner, amt, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Outbreak => {
            let poison = Dec::from_i32(if upg { 12 } else { 9 });
            let enemies = collect_hittable_enemies(state, content)?;
            for &enemy in &enemies {
                power::apply(state, content, PowerId::PoisonPower, enemy, poison, Some(owner), src)?;
            }
            for enemy in enemies {
                let poison_handle = state
                    .creature(enemy)
                    .and_then(|c| c.powers.iter().find(|p| p.id == PowerId::PoisonPower))
                    .map(|p| p.handle);
                if let Some(power_handle) = poison_handle {
                    content.dispatch_hook(
                        state,
                        HookListener::Power {
                            creature: enemy,
                            power_handle,
                        },
                        HookSlot::AfterSideTurnStart,
                        &HookArgs::SideTurn {
                            side: CombatSide::Enemy,
                            participants: &[enemy],
                        },
                    )?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::PhantomBlades => {
            let amt = Dec::from_i32(if upg { 11 } else { 8 });
            power::apply(state, content, PowerId::PhantomBladesPower, owner, amt, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::PiercingWail => {
            let amt = Dec::from_i32(if upg { 8 } else { 6 });
            let enemies = collect_hittable_enemies(state, content)?;
            for enemy in enemies {
                power::apply(state, content, PowerId::PiercingWailPower, enemy, amt, Some(owner), src)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Pinpoint => {
            let target = require_target(ctx, "Pinpoint")?;
            let dmg = Dec::from_i32(if upg { 19 } else { 15 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::PoisonedStab => {
            let target = require_target(ctx, "PoisonedStab")?;
            let dmg = Dec::from_i32(if upg { 8 } else { 6 });
            let poison = Dec::from_i32(if upg { 4 } else { 3 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(state, content, PowerId::PoisonPower, target, poison, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Pounce => {
            let target = require_target(ctx, "Pounce")?;
            let dmg = Dec::from_i32(if upg { 20 } else { 14 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(state, content, PowerId::FreeSkillPower, owner, Dec::ONE, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::PreciseCut => {
            let target = require_target(ctx, "PreciseCut")?;
            let base = if upg { 16 } else { 13 };
            let dmg = Dec::from_i32(base - 2 * state.player.hand.len() as i32);
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Predator => {
            let target = require_target(ctx, "Predator")?;
            let dmg = Dec::from_i32(if upg { 20 } else { 15 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(state, content, PowerId::DrawCardsNextTurnPower, owner, Dec::TWO, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Prepared => {
            let count = if upg { 2 } else { 1 };
            let _ = card_pile::draw(state, content, count as i32, false)?;
            discard_from_hand_select(content, state, card.id.model_id_str(), count, choice_queue)
        }
        CardId::Reflex => {
            let draw_count = if upg { 3 } else { 2 };
            let _ = card_pile::draw(state, content, draw_count, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Ricochet => {
            let repeats = if upg { 5 } else { 4 };
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(3),
                AttackTargetSpec::RandomOpponents {
                    allow_duplicates: true,
                },
            )
            .with_hit_count(repeats)
            .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SerpentForm => {
            let amt = Dec::from_i32(if upg { 6 } else { 4 });
            power::apply(state, content, PowerId::SerpentFormPower, owner, amt, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::ShadowStep => {
            let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
            if !hand.is_empty() {
                card_cmd::discard_batch(state, content, &hand)?;
            }
            power::apply(state, content, PowerId::ShadowStepPower, owner, Dec::ONE, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Shadowmeld => {
            power::apply(state, content, PowerId::ShadowmeldPower, owner, Dec::ONE, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Skewer => {
            let target = require_target(ctx, "Skewer")?;
            let dmg = Dec::from_i32(if upg { 11 } else { 8 });
            let hits = hooks::modify_x_value(state, content, ctx.card, ctx.energy_value)?;
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .with_hit_count(hits)
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Slice => {
            let target = require_target(ctx, "Slice")?;
            let dmg = Dec::from_i32(if upg { 9 } else { 6 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Snakebite => {
            let target = require_target(ctx, "Snakebite")?;
            let poison = Dec::from_i32(if upg { 10 } else { 7 });
            power::apply(state, content, PowerId::PoisonPower, target, poison, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Sneaky => {
            let amt = Dec::from_i32(if upg { 2 } else { 1 });
            power::apply(state, content, PowerId::SneakyPower, owner, amt, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Speedster => {
            power::apply(state, content, PowerId::SpeedsterPower, owner, Dec::from_i32(3), Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::StormOfSteel => {
            let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
            let count = hand.len();
            if !hand.is_empty() {
                card_cmd::discard_batch(state, content, &hand)?;
            }
            create_shivs_in_hand(state, content, owner, count, upg, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Strangle => {
            let target = require_target(ctx, "Strangle")?;
            let dmg = Dec::from_i32(if upg { 10 } else { 8 });
            let strangle = Dec::from_i32(if upg { 3 } else { 2 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(state, content, PowerId::StranglePower, target, strangle, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::StrikeSilent => {
            let target = require_target(ctx, "StrikeSilent")?;
            let dmg = Dec::from_i32(if upg { 9 } else { 6 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SuckerPunch => {
            let target = require_target(ctx, "SuckerPunch")?;
            let dmg = Dec::from_i32(if upg { 10 } else { 8 });
            let weak = Dec::from_i32(if upg { 2 } else { 1 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(state, content, PowerId::WeakPower, target, weak, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Suppress => {
            let target = require_target(ctx, "Suppress")?;
            let dmg = Dec::from_i32(if upg { 17 } else { 11 });
            let weak = Dec::from_i32(if upg { 5 } else { 3 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(state, content, PowerId::WeakPower, target, weak, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Survivor => {
            let blk = Dec::from_i32(if upg { 11 } else { 8 });
            block::gain_block(state, content, owner, blk, ValueProp::MOVE, src, Some(*ctx))?;
            discard_from_hand_select(content, state, card.id.model_id_str(), 1, choice_queue)
        }
        CardId::Tactician => {
            let energy = Dec::from_i32(if upg { 2 } else { 1 });
            gold::gain_energy(state, content, energy)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::TheHunt => {
            let target = require_target(ctx, "TheHunt")?;
            let dmg = Dec::from_i32(if upg { 15 } else { 10 });
            let should_trigger_fatal = hooks::should_owner_death_trigger_fatal(state, content, target)?;
            let results = AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                dmg,
                AttackTargetSpec::Single(target),
            )
            .execute(state, content)?;
            if should_trigger_fatal && results.iter().any(|r| r.was_target_killed) {
                if let Some(ext) = state.card_ext_mut(ctx.card) {
                    let prev = match ext.private_fields.get("TheHunt._pendingCardRewards") {
                        Some(FieldValue::Int(v)) => *v,
                        _ => 0,
                    };
                    ext.private_fields.insert(
                        Cow::Borrowed("TheHunt._pendingCardRewards"),
                        FieldValue::Int(prev + 1),
                    );
                }
                power::apply(state, content, PowerId::TheHuntPower, owner, Dec::ONE, Some(owner), src)?;
                state
                    .semantic_history
                    .record_growth(sts2_kernel::GrowthSource::TheHunt);
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::ToolsOfTheTrade => {
            power::apply(state, content, PowerId::ToolsOfTheTradePower, owner, Dec::ONE, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Tracking => {
            power::apply(state, content, PowerId::TrackingPower, owner, Dec::from_i32(50), Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Untouchable => {
            let blk = Dec::from_i32(if upg { 9 } else { 6 });
            block::gain_block(state, content, owner, blk, ValueProp::MOVE, src, Some(*ctx))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::UpMySleeve => {
            let count = if upg { 4 } else { 3 };
            create_shivs_in_hand(state, content, owner, count, false, false)?;
            add_local_energy_modifier(state, ctx.card, LocalEnergyModifierKind::AddThisCombat, -1);
            Ok(CardPlayOutcome::default())
        }
        CardId::WellLaidPlans => {
            power::apply(state, content, PowerId::WellLaidPlansPower, owner, Dec::ONE, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::WraithForm => {
            let intangible = Dec::from_i32(if upg { 3 } else { 2 });
            power::apply(state, content, PowerId::IntangiblePower, owner, intangible, Some(owner), src)?;
            power::apply(state, content, PowerId::WraithFormPower, owner, Dec::ONE, Some(owner), src)?;
            Ok(CardPlayOutcome::default())
        }
        _ => Err(KernelError::unsupported(
            "Card",
            card.id.model_id_str().to_owned(),
        )),
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
        CardId::Acrobatics | CardId::DaggerThrow | CardId::Prepared | CardId::Survivor => {
            if !selected.is_empty() {
                card_cmd::discard_batch(state, content, selected)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::HiddenDaggers => {
            if !selected.is_empty() {
                card_cmd::discard_batch(state, content, selected)?;
            }
            create_shivs_in_hand(state, content, owner, 2, upg, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::HandTrick => {
            if let Some(&chosen) = selected.first() {
                card_cmd::apply_single_turn_sly(state, chosen);
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Nightmare => {
            apply_nightmare_to_selected(content, state, owner, ctx.card, selected)?;
            Ok(CardPlayOutcome::default())
        }
        _ => Err(KernelError::unsupported(
            "CardResume",
            card.id.model_id_str().to_owned(),
        )),
    }
}

pub fn on_upgrade(state: &mut BattleState, card: CardHandle) -> Result<(), KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    let _ = card_meta(id)?;
    let Some(c) = state.card_mut(card) else {
        return Ok(());
    };
    match id {
        CardId::BladeSymphony
        | CardId::BulletTime
        | CardId::Flanking
        | CardId::MasterPlanner
        | CardId::Murder
        | CardId::Nightmare
        | CardId::ShadowStep
        | CardId::Shadowmeld
        | CardId::ToolsOfTheTrade
        | CardId::Tracking
        | CardId::WellLaidPlans => {
            c.base_energy_cost = (c.base_energy_cost - 1).max(0);
        }
        CardId::Afterimage | CardId::InfiniteBlades | CardId::Speedster => {
            c.local_keywords.insert(CardKeywords::INNATE);
        }
        CardId::CalculatedGamble => {
            c.local_keywords.insert(CardKeywords::RETAIN);
        }
        CardId::Mirage => {
            c.local_keywords.remove(CardKeywords::EXHAUST);
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
    Ok(SmallVec::new())
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
        CardId::Pinpoint => {
            match slot {
                HookSlot::AfterCardEnteredCombat => {
                    if let HookArgs::CardOnly(entered_card) = args {
                        if *entered_card == card
                            && state.card(card).is_some_and(|c| c.clone_of_handle.is_none())
                        {
                            let skills = state.player.counters.skill_cards_played_this_turn;
                            add_local_energy_modifier(
                                state,
                                card,
                                LocalEnergyModifierKind::AddThisTurn,
                                -skills,
                            );
                        }
                    }
                }
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(ctx) = args {
                        if state.card(ctx.card).is_some()
                            && content.card_resolved_type(state, ctx.card)? == CardType::Skill
                        {
                            add_local_energy_modifier(
                                state,
                                card,
                                LocalEnergyModifierKind::AddThisTurn,
                                -1,
                            );
                        }
                    }
                }
                _ => {}
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
        return Err(KernelError::unsupported(
            "CardTurnEndInHand",
            id.model_id_str().to_owned(),
        ));
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
