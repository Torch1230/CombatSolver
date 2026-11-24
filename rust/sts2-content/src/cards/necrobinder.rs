use smallvec::SmallVec;
use std::sync::LazyLock;
use sts2_kernel::commands::card_select::SelectResult;
use sts2_kernel::commands::{
    autoplay, block, card as card_cmd, card_pile, card_select, creature, damage,
    damage::AttackBuilder, damage::AttackTargetSpec, gold, osty, power,
};
use sts2_kernel::content_dispatch::{CardPlayContext, CardPlayOutcome};
use sts2_kernel::{
    hooks, BattleState, CardHandle, CardInst, CardTempFlags, ChoiceTiming, ContentDispatcher,
    CreatureHandle, FieldValue, HookArgs, HookResult, KernelError, LocalEnergyModifier, PowerInst,
    PowerExtension, StepOutcome,
};
use sts2_types::generated_ids::CARD_POOL_NECROBINDER;
use sts2_types::{
    generated_card_meta, CardId, CardKeywords, CardMeta, CardPilePosition, CardRarity,
    CardStaticFlags, CardTags, CombatSide, Dec, HookSlot, LocalEnergyModifierKind, PileType,
    PowerId, PowerMeta, PowerStackType, PowerStaticFlags, PowerType, RunRngType,
    ValueProp,
};

const NECRO_CARD_COUNT: usize = 91;
const SCYTHE_INCREASED_DAMAGE_KEY: &str = "TheScythe._increasedDamage";
const FETCH_FINISHED_ROUND_KEY: &str = "Fetch._finishedRound";
const FETCH_FINISHED_SIDE_KEY: &str = "Fetch._finishedSide";
const FETCH_FINISHED_TURNS_KEY: &str = "Fetch._finishedTurnNumbers";

#[inline]
const fn necro_index(id: CardId) -> Option<usize> {
    match id {
        CardId::Afterlife => Some(0),
        CardId::BansheesCry => Some(1),
        CardId::BlightStrike => Some(2),
        CardId::Bodyguard => Some(3),
        CardId::BoneShards => Some(4),
        CardId::BorrowedTime => Some(5),
        CardId::Bury => Some(6),
        CardId::Cacophony => Some(7),
        CardId::Calcify => Some(8),
        CardId::CallOfTheVoid => Some(9),
        CardId::CaptureSpirit => Some(10),
        CardId::Cleanse => Some(11),
        CardId::Countdown => Some(12),
        CardId::DanseMacabre => Some(13),
        CardId::DeathMarch => Some(14),
        CardId::Deathbringer => Some(15),
        CardId::DeathsDoor => Some(16),
        CardId::Debilitate => Some(17),
        CardId::DefendNecrobinder => Some(18),
        CardId::Defile => Some(19),
        CardId::Defy => Some(20),
        CardId::Delay => Some(21),
        CardId::Demesne => Some(22),
        CardId::DevourLife => Some(23),
        CardId::Dirge => Some(24),
        CardId::DrainPower => Some(25),
        CardId::Dredge => Some(26),
        CardId::Eidolon => Some(27),
        CardId::EndOfDays => Some(28),
        CardId::EnfeeblingTouch => Some(29),
        CardId::Eradicate => Some(30),
        CardId::Fear => Some(31),
        CardId::Fetch => Some(32),
        CardId::Flatten => Some(33),
        CardId::ForbiddenGrimoire => Some(34),
        CardId::Friendship => Some(35),
        CardId::GlimpseBeyond => Some(36),
        CardId::GraveWarden => Some(37),
        CardId::Graveblast => Some(38),
        CardId::Hang => Some(39),
        CardId::Haunt => Some(40),
        CardId::HighFive => Some(41),
        CardId::Invoke => Some(42),
        CardId::LegionOfBone => Some(43),
        CardId::Lethality => Some(44),
        CardId::Melancholy => Some(45),
        CardId::Misery => Some(46),
        CardId::NecroMastery => Some(47),
        CardId::NegativePulse => Some(48),
        CardId::Neurosurge => Some(49),
        CardId::NoEscape => Some(50),
        CardId::Oblivion => Some(51),
        CardId::Pagestorm => Some(52),
        CardId::Parse => Some(53),
        CardId::Poke => Some(54),
        CardId::Protector => Some(55),
        CardId::PullAggro => Some(56),
        CardId::PullFromBelow => Some(57),
        CardId::Putrefy => Some(58),
        CardId::Rattle => Some(59),
        CardId::Reanimate => Some(60),
        CardId::Reap => Some(61),
        CardId::ReaperForm => Some(62),
        CardId::Reave => Some(63),
        CardId::RightHandHand => Some(64),
        CardId::Sacrifice => Some(65),
        CardId::Scourge => Some(66),
        CardId::SculptingStrike => Some(67),
        CardId::Seance => Some(68),
        CardId::SentryMode => Some(69),
        CardId::Severance => Some(70),
        CardId::SharedFate => Some(71),
        CardId::Shroud => Some(72),
        CardId::SicEm => Some(73),
        CardId::SleightOfFlesh => Some(74),
        CardId::Snap => Some(75),
        CardId::SoulStorm => Some(76),
        CardId::Soulbound => Some(77),
        CardId::Sow => Some(78),
        CardId::SpiritOfAsh => Some(79),
        CardId::Spur => Some(80),
        CardId::Squeeze => Some(81),
        CardId::StrikeNecrobinder => Some(82),
        CardId::TheScythe => Some(83),
        CardId::TimesUp => Some(84),
        CardId::Transfigure => Some(85),
        CardId::Undeath => Some(86),
        CardId::Underworld => Some(87),
        CardId::Unleash => Some(88),
        CardId::Veilpiercer => Some(89),
        CardId::Wisp => Some(90),
        _ => None,
    }
}

static NECRO_CARD_META: LazyLock<[CardMeta; NECRO_CARD_COUNT]> = LazyLock::new(|| {
    std::array::from_fn(|i| {
        let id = CARD_POOL_NECROBINDER[i];
        let mut meta = *generated_card_meta(id);
        if id == CardId::Spur {
            meta.flags |= CardStaticFlags::MAY_HEAL;
        }
        if matches!(
            id,
            CardId::BansheesCry
                | CardId::ForbiddenGrimoire
                | CardId::Melancholy
                | CardId::TheScythe
        ) {
            meta.flags |= CardStaticFlags::HAS_GROWTH;
        }
        if matches!(
            id,
            CardId::Cacophony
                | CardId::CaptureSpirit
                | CardId::Countdown
                | CardId::Dirge
                | CardId::DrainPower
                | CardId::Eidolon
                | CardId::GlimpseBeyond
                | CardId::GraveWarden
                | CardId::Reave
                | CardId::Severance
        ) {
            meta.flags |= CardStaticFlags::USES_RNG;
        }
        if matches!(
            id,
            CardId::BansheesCry
                | CardId::BoneShards
                | CardId::BorrowedTime
                | CardId::Cleanse
                | CardId::DeathMarch
                | CardId::DeathsDoor
                | CardId::Debilitate
                | CardId::Dirge
                | CardId::Dredge
                | CardId::Eidolon
                | CardId::EndOfDays
                | CardId::Eradicate
                | CardId::Fetch
                | CardId::Flatten
                | CardId::Graveblast
                | CardId::Hang
                | CardId::HighFive
                | CardId::Lethality
                | CardId::Melancholy
                | CardId::Misery
                | CardId::NoEscape
                | CardId::Oblivion
                | CardId::Poke
                | CardId::Protector
                | CardId::PullFromBelow
                | CardId::Rattle
                | CardId::ReaperForm
                | CardId::RightHandHand
                | CardId::Sacrifice
                | CardId::SculptingStrike
                | CardId::Seance
                | CardId::SicEm
                | CardId::Snap
                | CardId::SoulStorm
                | CardId::Squeeze
                | CardId::TimesUp
                | CardId::Transfigure
                | CardId::Unleash
                | CardId::Veilpiercer
        ) {
            meta.flags |= CardStaticFlags::ORDER_SENSITIVE;
        }
        meta
    })
});

pub fn card_meta(id: CardId) -> Result<&'static CardMeta, KernelError> {
    let Some(idx) = necro_index(id) else {
        return Err(KernelError::Unsupported {
            kind: "Card",
            id: id.model_id_str().to_owned(),
        });
    };
    Ok(&NECRO_CARD_META[idx])
}

pub fn is_playable(state: &BattleState, card: CardHandle) -> Result<bool, KernelError> {
    let id = state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?
        .id;
    let _ = card_meta(id)?;
    match id {
        CardId::HighFive => Ok(state.is_osty_alive()),
        _ => Ok(true),
    }
}

#[inline]
fn require_target(ctx: &CardPlayContext, card_name: &str) -> Result<CreatureHandle, KernelError> {
    ctx.target
        .ok_or_else(|| KernelError::InvalidAction(format!("{card_name} requires target")))
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

fn add_energy_cost_this_combat(state: &mut BattleState, card: CardHandle, delta: i32) {
    if delta == 0 {
        return;
    }
    if let Some(ext) = state.card_ext_mut(card) {
        ext.local_energy_modifiers.push(LocalEnergyModifier {
            kind: LocalEnergyModifierKind::AddThisCombat,
            amount: delta,
            is_reduce_only: false,
        });
    }
}

fn set_temp_cost_override_this_turn(state: &mut BattleState, card: CardHandle, cost: i32) {
    if let Some(c) = state.card_mut(card) {
        c.temp_cost_override_this_turn = Some(cost);
    }
    if let Some(ext) = state.card_ext_mut(card) {
        ext.local_energy_modifiers.push(LocalEnergyModifier {
            kind: LocalEnergyModifierKind::SetThisTurn,
            amount: cost,
            is_reduce_only: false,
        });
    }
}

fn count_actor_events_this_turn(state: &BattleState, kind: &str) -> i32 {
    let cur = state.current_turn_key();
    state
        .semantic_history
        .actor_events
        .iter()
        .filter(|(k, tk, _)| k == kind && *tk == cur)
        .count() as i32
}

fn count_actor_events_this_combat(state: &BattleState, kind: &str) -> i32 {
    state
        .semantic_history
        .actor_events
        .iter()
        .filter(|(k, _, _)| k == kind)
        .count() as i32
}

fn count_non_hand_draws_this_turn(state: &BattleState) -> i32 {
    let cur = state.current_turn_key();
    state
        .semantic_history
        .card_drawn
        .iter()
        .filter(|(tk, _, _, from_hand_draw)| !*from_hand_draw && *tk == cur)
        .count() as i32
}

fn the_scythe_increased_damage(state: &BattleState, card: CardHandle) -> i32 {
    state
        .card(card)
        .and_then(|c| state.card_ext(c.ext_index))
        .and_then(|ext| ext.private_fields.get(SCYTHE_INCREASED_DAMAGE_KEY))
        .map_or(0, |fv| match fv {
            FieldValue::Int(i) => *i,
            FieldValue::Decimal(d) => d.to_i32_trunc().unwrap_or(0),
            _ => 0,
        })
}

fn add_the_scythe_increased_damage(
    state: &mut BattleState,
    card: CardHandle,
    delta: i32,
) -> Result<(), KernelError> {
    let cur = the_scythe_increased_damage(state, card);
    let next = cur
        .checked_add(delta)
        .ok_or_else(|| KernelError::InvalidState("TheScythe damage overflow".to_owned()))?;
    if let Some(ext) = state.card_ext_mut(card) {
        ext.private_fields
            .insert(SCYTHE_INCREASED_DAMAGE_KEY, FieldValue::Int(next));
    }
    Ok(())
}

fn fetch_has_been_played_this_turn(state: &BattleState, card: CardHandle) -> bool {
    let Some(ext) = state.card(card).and_then(|c| state.card_ext(c.ext_index)) else {
        return false;
    };
    let round = match ext.private_fields.get(FETCH_FINISHED_ROUND_KEY) {
        Some(FieldValue::Int(r)) => *r,
        _ => return false,
    };
    let side = match ext.private_fields.get(FETCH_FINISHED_SIDE_KEY) {
        Some(FieldValue::Int(s)) => *s,
        _ => return false,
    };
    let turns = match ext.private_fields.get(FETCH_FINISHED_TURNS_KEY) {
        Some(FieldValue::IntArray(arr)) if !arr.is_empty() => arr,
        _ => return false,
    };
    let cur_side = match state.current_side {
        CombatSide::Player => 0,
        CombatSide::Enemy => 1,
    };
    round == state.round_number
        && side == cur_side
        && turns.len() == 1
        && turns[0] == state.player.turn_number
}

fn fetch_record_play_finished(state: &mut BattleState, card: CardHandle) {
    let round = state.round_number;
    let side = match state.current_side {
        CombatSide::Player => 0,
        CombatSide::Enemy => 1,
    };
    let turn = state.player.turn_number;
    if let Some(ext) = state.card_ext_mut(card) {
        ext.private_fields
            .insert(FETCH_FINISHED_ROUND_KEY, FieldValue::Int(round));
        ext.private_fields
            .insert(FETCH_FINISHED_SIDE_KEY, FieldValue::Int(side));
        ext.private_fields
            .insert(FETCH_FINISHED_TURNS_KEY, FieldValue::IntArray(vec![turn]));
    }
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
    let ext_index = if let Some(mut ext) = state.card_ext(source.ext_index).cloned() {
        ext.private_fields.remove(FETCH_FINISHED_ROUND_KEY);
        ext.private_fields.remove(FETCH_FINISHED_SIDE_KEY);
        ext.private_fields.remove(FETCH_FINISHED_TURNS_KEY);
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
        deck_card_handle: None,
        clone_of_handle: Some(source_handle),
        floor_added_to_deck: None,
        ext_index,
        temp_flags,
        pile: PileType::None,
        removed_from_state: false,
        transform_into_after_play: None,
        ..source
    });
    Ok(new_handle)
}

fn generate_existing_card_into_pile(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    handle: CardHandle,
    pile: PileType,
    pos: CardPilePosition,
) -> Result<(), KernelError> {
    let creator = Some(state.player.creature_handle);
    card_pile::enter_combat(state, content, handle, pile, pos, true)?;
    hooks::after_card_generated(state, content, handle, creator)?;
    hooks::after_card_generated_for_combat(state, content, handle, creator)
}

fn create_and_generate_souls(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    count: usize,
    upgrade_souls: bool,
    pile: PileType,
    pos: CardPilePosition,
) -> Result<(), KernelError> {
    let mut souls: SmallVec<[CardHandle; 8]> = SmallVec::with_capacity(count);
    for _ in 0..count {
        souls.push(card_pile::create_card_in_combat(
            state,
            content,
            CardId::Soul,
            0,
        )?);
    }
    if upgrade_souls && !hooks::is_over_or_ending(state) {
        for &s in &souls {
            card_cmd::upgrade(state, content, s)?;
        }
    }
    for s in souls {
        generate_existing_card_into_pile(content, state, s, pile, pos)?;
    }
    Ok(())
}

fn temporary_power_internal_id(id: PowerId) -> Option<PowerId> {
    match id {
        PowerId::CoordinatePower
        | PowerId::CrushUnderPower
        | PowerId::DarkShacklesPower
        | PowerId::DyingStarPower
        | PowerId::EnfeeblingTouchPower
        | PowerId::FeedingFrenzyPower
        | PowerId::FlexPotionPower
        | PowerId::ManglePower
        | PowerId::MonarchsGazeStrengthDownPower
        | PowerId::PiercingWailPower
        | PowerId::ReptileTrinketPower
        | PowerId::SetupStrikePower
        | PowerId::ShacklingPotionPower => Some(PowerId::StrengthPower),
        PowerId::AnticipatePower
        | PowerId::FadePower
        | PowerId::HelicalDartPower
        | PowerId::SpeedPotionPower => Some(PowerId::DexterityPower),
        PowerId::FocusedStrikePower
        | PowerId::HotfixPower
        | PowerId::HyperbeamFocusDownPower
        | PowerId::SynchronizePower => Some(PowerId::FocusPower),
        PowerId::ChoBarbsPower => Some(PowerId::ThornsPower),
        _ => None,
    }
}

fn power_type_for_amount(meta: &PowerMeta, amount: i32) -> PowerType {
    let allow_neg = meta.flags.contains(PowerStaticFlags::ALLOW_NEGATIVE);
    if meta.stack_type == PowerStackType::Counter && allow_neg && amount < 0 {
        return PowerType::Debuff;
    }
    if !allow_neg && meta.power_type == PowerType::Debuff && amount < 0 {
        return PowerType::Buff;
    }
    meta.power_type
}

fn sorted_draw_candidates(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    min_count: usize,
    max_count: usize,
) -> Result<SmallVec<[CardHandle; 16]>, KernelError> {
    let mut cands: SmallVec<[CardHandle; 16]> = state.player.draw.iter().copied().collect();
    if min_count != max_count || cands.len() > min_count {
        let mut pairs: Vec<(CardHandle, CardRarity, CardId)> = Vec::with_capacity(cands.len());
        for &h in &cands {
            let cid = state
                .card(h)
                .ok_or_else(|| KernelError::InvalidState("missing draw card".to_owned()))?
                .id;
            let rarity = content
                .card_meta(cid)
                .unwrap_or_else(|_| generated_card_meta(cid))
                .rarity;
            pairs.push((h, rarity, cid));
        }
        pairs.sort_by(|a, b| a.1.cmp(&b.1).then_with(|| a.2.compare_model_id(b.2)));
        cands = pairs.into_iter().map(|p| p.0).collect();
    }
    Ok(cands)
}

fn select_from_hand(
    state: &BattleState,
    candidates: &[CardHandle],
    min_count: usize,
    max_count: usize,
    source_id: &'static str,
    choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
) -> Result<SelectResult, KernelError> {
    if hooks::is_over_or_ending(state) || state.player_creature().is_none_or(|c| c.is_dead()) {
        return Ok(SelectResult::Selected(SmallVec::new()));
    }
    card_select::select_cards(
        state,
        candidates,
        min_count,
        max_count,
        ChoiceTiming::Action,
        source_id,
        PileType::Hand,
        false,
        choice_queue,
    )
}

fn select_from_pile(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    pile: PileType,
    count: usize,
    source_id: &'static str,
    choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
) -> Result<SelectResult, KernelError> {
    if hooks::is_ending(state) || state.player_creature().is_none_or(|c| c.is_dead()) {
        return Ok(SelectResult::Selected(SmallVec::new()));
    }
    let cands: SmallVec<[CardHandle; 16]> = match pile {
        PileType::Draw => sorted_draw_candidates(state, content, count, count)?,
        PileType::Discard => state.player.discard.iter().copied().collect(),
        _ => {
            return Err(KernelError::InvalidAction(format!(
                "unsupported selection pile {pile:?}"
            )));
        }
    };
    card_select::select_cards(
        state,
        &cands,
        count,
        count,
        ChoiceTiming::Action,
        source_id,
        pile,
        false,
        choice_queue,
    )
}

fn apply_self_power(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    ctx: &CardPlayContext,
    power_id: PowerId,
    amount: i32,
) -> Result<Option<usize>, KernelError> {
    let owner = state.player.creature_handle;
    power::apply(
        state,
        content,
        power_id,
        owner,
        Dec::from_i32(amount),
        Some(owner),
        Some(ctx.card),
    )
}

fn get_power_amount(state: &BattleState, creature: CreatureHandle, power_id: PowerId) -> i32 {
    state
        .creature(creature)
        .and_then(|c| c.powers.iter().find(|p| p.id == power_id))
        .map_or(0, |p| p.amount)
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
    let source_id = card.id.model_id_str();

    match card.id {
        CardId::Afterlife => {
            let _ = osty::summon(state, content, Dec::from_i32(if upg { 9 } else { 6 }))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::BansheesCry => {
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(33),
                AttackTargetSpec::AllOpponents,
            )
            .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::BlightStrike => {
            let target = require_target(ctx, "BlightStrike")?;
            let dmg = Dec::from_i32(if upg { 10 } else { 8 });
            let results =
                AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                    .execute(state, content)?;
            let total_damage: i32 = results
                .iter()
                .map(|r| r.blocked_damage + r.unblocked_damage)
                .sum();
            power::apply(
                state,
                content,
                PowerId::DoomPower,
                target,
                Dec::from_i32(total_damage),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Bodyguard => {
            let _ = osty::summon(state, content, Dec::from_i32(if upg { 7 } else { 5 }))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::BoneShards => {
            if let Some(osty_h) = state.osty_handle().filter(|&h| {
                state.creature(h).is_some_and(|c| c.is_alive())
            }) {
                let dmg = Dec::from_i32(if upg { 12 } else { 9 });
                AttackBuilder::from_card(
                    osty_h,
                    ctx.card,
                    *ctx,
                    dmg,
                    AttackTargetSpec::AllOpponents,
                )
                .execute(state, content)?;
                block::gain_block(
                    state,
                    content,
                    owner,
                    dmg,
                    ValueProp::MOVE,
                    Some(ctx.card),
                    Some(*ctx),
                )?;
                if state.is_osty_alive() {
                    creature::kill(state, content, osty_h, Some(ctx.card))?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::BorrowedTime => {
            gold::gain_energy(state, Dec::from_i32(if upg { 6 } else { 4 }))?;
            apply_self_power(content, state, ctx, PowerId::BorrowedTimePower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Bury => {
            let target = require_target(ctx, "Bury")?;
            let dmg = Dec::from_i32(if upg { 63 } else { 52 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Cacophony => {
            apply_self_power(
                content,
                state,
                ctx,
                PowerId::CacophonyPower,
                if upg { 99 } else { 66 },
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Calcify => {
            apply_self_power(content, state, ctx, PowerId::CalcifyPower, if upg { 6 } else { 4 })?;
            Ok(CardPlayOutcome::default())
        }
        CardId::CallOfTheVoid => {
            apply_self_power(content, state, ctx, PowerId::CallOfTheVoidPower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::CaptureSpirit => {
            let target = require_target(ctx, "CaptureSpirit")?;
            let amt = if upg { 4 } else { 3 };
            damage::damage_targets(
                state,
                content,
                &[target],
                Dec::from_i32(amt),
                ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED | ValueProp::MOVE,
                Some(owner),
                Some(ctx.card),
                Some(*ctx),
            )?;
            create_and_generate_souls(
                content,
                state,
                amt as usize,
                false,
                PileType::Draw,
                CardPilePosition::Random,
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Cleanse => {
            let _ = osty::summon(state, content, Dec::from_i32(if upg { 5 } else { 3 }))?;
            match select_from_pile(state, content, PileType::Draw, 1, source_id, choice_queue)? {
                SelectResult::Selected(sel) => {
                    on_play_resume(content, state, ctx, &sel, 1)?;
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                    needs_choice: Some((req, 1)),
                    transform_into: None,
                }),
            }
        }
        CardId::Countdown => {
            apply_self_power(content, state, ctx, PowerId::CountdownPower, if upg { 9 } else { 6 })?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DanseMacabre => {
            apply_self_power(
                content,
                state,
                ctx,
                PowerId::DanseMacabrePower,
                if upg { 6 } else { 4 },
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DeathMarch => {
            let target = require_target(ctx, "DeathMarch")?;
            let base = if upg { 9 } else { 8 };
            let extra = if upg { 6 } else { 4 };
            let drawn = count_non_hand_draws_this_turn(state);
            let dmg = Dec::from_i32(base + extra * drawn);
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Deathbringer => {
            let doom = Dec::from_i32(if upg { 26 } else { 21 });
            for enemy in hittable_enemies(state, content)? {
                power::apply(
                    state,
                    content,
                    PowerId::DoomPower,
                    enemy,
                    doom,
                    Some(owner),
                    Some(ctx.card),
                )?;
            }
            for enemy in hittable_enemies(state, content)? {
                power::apply(
                    state,
                    content,
                    PowerId::WeakPower,
                    enemy,
                    Dec::ONE,
                    Some(owner),
                    Some(ctx.card),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::DeathsDoor => {
            let blk = Dec::from_i32(if upg { 7 } else { 6 });
            let gains = if count_actor_events_this_turn(state, "DoomApplied") > 0 {
                3
            } else {
                1
            };
            for _ in 0..gains {
                block::gain_block(
                    state,
                    content,
                    owner,
                    blk,
                    ValueProp::MOVE,
                    Some(ctx.card),
                    Some(*ctx),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Debilitate => {
            let target = require_target(ctx, "Debilitate")?;
            let dmg = Dec::from_i32(if upg { 12 } else { 10 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(
                state,
                content,
                PowerId::DebilitatePower,
                target,
                Dec::from_i32(if upg { 3 } else { 2 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DefendNecrobinder => {
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
        CardId::Defile => {
            let target = require_target(ctx, "Defile")?;
            let dmg = Dec::from_i32(if upg { 17 } else { 13 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Defy => {
            let target = require_target(ctx, "Defy")?;
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
            power::apply(
                state,
                content,
                PowerId::WeakPower,
                target,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Delay => {
            let blk = Dec::from_i32(if upg { 13 } else { 11 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            apply_self_power(
                content,
                state,
                ctx,
                PowerId::EnergyNextTurnPower,
                if upg { 2 } else { 1 },
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Demesne => {
            apply_self_power(content, state, ctx, PowerId::DemesnePower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DevourLife => {
            apply_self_power(content, state, ctx, PowerId::DevourLifePower, if upg { 2 } else { 1 })?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Dirge => {
            let x = hooks::modify_x_value(state, content, ctx.card, ctx.energy_value)?.max(0);
            let summon_amt = Dec::from_i32(if upg { 4 } else { 3 });
            for _ in 0..x {
                let _ = osty::summon(state, content, summon_amt)?;
            }
            create_and_generate_souls(
                content,
                state,
                x as usize,
                upg,
                PileType::Draw,
                CardPilePosition::Random,
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DrainPower => {
            let target = require_target(ctx, "DrainPower")?;
            let dmg = Dec::from_i32(if upg { 12 } else { 10 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let mut cands: SmallVec<[CardHandle; 32]> = SmallVec::new();
            for &h in &state.player.discard {
                if let Some(ci) = state.card(h) {
                    let max_upg = content.card_meta(ci.id)?.max_upgrade_level;
                    if ci.upgrade_level < max_upg {
                        cands.push(h);
                    }
                }
            }
            state
                .run_rngs
                .get_mut(RunRngType::CombatCardSelection)
                .unstable_shuffle(&mut cands);
            let take_count = if upg { 3 } else { 2 };
            for h in cands.into_iter().take(take_count) {
                if !hooks::is_over_or_ending(state) {
                    card_cmd::upgrade(state, content, h)?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Dredge => {
            let count = 3usize.min(card_pile::MAX_CARDS_IN_HAND.saturating_sub(state.player.hand.len()));
            if count == 0 {
                return Ok(CardPlayOutcome::default());
            }
            match select_from_pile(state, content, PileType::Discard, count, source_id, choice_queue)? {
                SelectResult::Selected(sel) => {
                    on_play_resume(content, state, ctx, &sel, 1)?;
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                    needs_choice: Some((req, 1)),
                    transform_into: None,
                }),
            }
        }
        CardId::Eidolon => {
            let exhaust = state.player.exhaust.clone();
            let mut ethereal_cards: SmallVec<[CardHandle; 8]> = SmallVec::new();
            for h in exhaust {
                let has_ethereal = card_pile::card_has_keyword(state, content, h, CardKeywords::ETHEREAL)?;
                let is_unplayable = card_pile::card_has_keyword(state, content, h, CardKeywords::UNPLAYABLE)?;
                if has_ethereal && !is_unplayable {
                    ethereal_cards.push(h);
                }
            }
            for ch in ethereal_cards {
                if hooks::is_over_or_ending(state)
                    || state.player_creature().is_none_or(|c| c.is_dead())
                {
                    continue;
                }
                let outcome = autoplay::from_cards(state, content, &[ch], None, false)?;
                if let StepOutcome::NeedsChoice(req) = outcome {
                    return Err(KernelError::Unsupported {
                        kind: "CardAutoplayChoice",
                        id: req.source_id.into_owned(),
                    });
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::EndOfDays => {
            let doom = Dec::from_i32(if upg { 37 } else { 29 });
            for enemy in hittable_enemies(state, content)? {
                power::apply(
                    state,
                    content,
                    PowerId::DoomPower,
                    enemy,
                    doom,
                    Some(owner),
                    Some(ctx.card),
                )?;
            }
            let mut doomed: SmallVec<[CreatureHandle; 8]> = SmallVec::new();
            for enemy in hittable_enemies(state, content)? {
                if let Some(c) = state.creature(enemy) {
                    let amt = get_power_amount(state, enemy, PowerId::DoomPower);
                    if amt > 0 && c.current_hp <= amt {
                        doomed.push(enemy);
                    }
                }
            }
            if !doomed.is_empty() {
                for &ch in &doomed {
                    creature::kill(state, content, ch, None)?;
                }
                for ch in doomed {
                    hooks::after_died_to_doom(state, content, ch)?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::EnfeeblingTouch => {
            let target = require_target(ctx, "EnfeeblingTouch")?;
            power::apply(
                state,
                content,
                PowerId::EnfeeblingTouchPower,
                target,
                Dec::from_i32(if upg { 11 } else { 8 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Eradicate => {
            let target = require_target(ctx, "Eradicate")?;
            let hits = hooks::modify_x_value(state, content, ctx.card, ctx.energy_value)?;
            let dmg = Dec::from_i32(if upg { 14 } else { 11 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .with_hit_count(hits)
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Fear => {
            let target = require_target(ctx, "Fear")?;
            let dmg = Dec::from_i32(if upg { 8 } else { 7 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(
                state,
                content,
                PowerId::VulnerablePower,
                target,
                Dec::from_i32(if upg { 2 } else { 1 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Fetch => {
            let target = require_target(ctx, "Fetch")?;
            let played_before = fetch_has_been_played_this_turn(state, ctx.card);
            if let Some(osty_h) = state.osty_handle().filter(|&h| {
                state.creature(h).is_some_and(|c| c.is_alive())
            }) {
                let dmg = Dec::from_i32(if upg { 6 } else { 3 });
                AttackBuilder::from_card(
                    osty_h,
                    ctx.card,
                    *ctx,
                    dmg,
                    AttackTargetSpec::Single(target),
                )
                .execute(state, content)?;
                if !played_before {
                    let _ = card_pile::draw(state, content, 1, false)?;
                }
            }
            fetch_record_play_finished(state, ctx.card);
            Ok(CardPlayOutcome::default())
        }
        CardId::Flatten => {
            let target = require_target(ctx, "Flatten")?;
            if let Some(osty_h) = state.osty_handle().filter(|&h| {
                state.creature(h).is_some_and(|c| c.is_alive())
            }) {
                let dmg = Dec::from_i32(if upg { 16 } else { 12 });
                AttackBuilder::from_card(
                    osty_h,
                    ctx.card,
                    *ctx,
                    dmg,
                    AttackTargetSpec::Single(target),
                )
                .execute(state, content)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::ForbiddenGrimoire => {
            apply_self_power(content, state, ctx, PowerId::ForbiddenGrimoirePower, 1)?;
            state
                .semantic_history
                .record_growth(sts2_kernel::GrowthSource::ForbiddenGrimoire);
            Ok(CardPlayOutcome::default())
        }
        CardId::Friendship => {
            let str_loss = Dec::from_i32(if upg { -1 } else { -2 });
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                owner,
                str_loss,
                Some(owner),
                Some(ctx.card),
            )?;
            apply_self_power(content, state, ctx, PowerId::FriendshipPower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::GlimpseBeyond => {
            let count = if upg { 4 } else { 3 };
            let allies: SmallVec<[CreatureHandle; 8]> = SmallVec::from_slice(&state.ally_handles);
            for ah in allies {
                if state
                    .creature(ah)
                    .is_some_and(|c| c.is_alive() && c.is_player)
                {
                    create_and_generate_souls(
                        content,
                        state,
                        count,
                        false,
                        PileType::Draw,
                        CardPilePosition::Random,
                    )?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::GraveWarden => {
            let blk = Dec::from_i32(if upg { 11 } else { 8 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            create_and_generate_souls(
                content,
                state,
                1,
                false,
                PileType::Draw,
                CardPilePosition::Random,
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Graveblast => {
            let target = require_target(ctx, "Graveblast")?;
            let dmg = Dec::from_i32(if upg { 6 } else { 4 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            match select_from_pile(state, content, PileType::Discard, 1, source_id, choice_queue)? {
                SelectResult::Selected(sel) => {
                    on_play_resume(content, state, ctx, &sel, 1)?;
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                    needs_choice: Some((req, 1)),
                    transform_into: None,
                }),
            }
        }
        CardId::Hang => {
            let target = require_target(ctx, "Hang")?;
            let dmg = Dec::from_i32(if upg { 13 } else { 10 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let cur = get_power_amount(state, target, PowerId::HangPower);
            let mut amt = cur.max(2);
            if cur + amt > 999_999_999 {
                amt = (999_999_999 - cur).max(0);
            }
            power::apply(
                state,
                content,
                PowerId::HangPower,
                target,
                Dec::from_i32(amt),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Haunt => {
            apply_self_power(content, state, ctx, PowerId::HauntPower, if upg { 9 } else { 7 })?;
            Ok(CardPlayOutcome::default())
        }
        CardId::HighFive => {
            if let Some(osty_h) = state.osty_handle().filter(|&h| {
                state.creature(h).is_some_and(|c| c.is_alive())
            }) {
                let dmg = Dec::from_i32(if upg { 13 } else { 11 });
                AttackBuilder::from_card(
                    osty_h,
                    ctx.card,
                    *ctx,
                    dmg,
                    AttackTargetSpec::AllOpponents,
                )
                .execute(state, content)?;
                let vuln = Dec::from_i32(if upg { 3 } else { 2 });
                for enemy in hittable_enemies(state, content)? {
                    power::apply(
                        state,
                        content,
                        PowerId::VulnerablePower,
                        enemy,
                        vuln,
                        Some(owner),
                        Some(ctx.card),
                    )?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Invoke => {
            let amt = if upg { 3 } else { 2 };
            apply_self_power(content, state, ctx, PowerId::SummonNextTurnPower, amt)?;
            apply_self_power(content, state, ctx, PowerId::EnergyNextTurnPower, amt)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::LegionOfBone => {
            let summon = Dec::from_i32(if upg { 8 } else { 6 });
            let allies: SmallVec<[CreatureHandle; 8]> = SmallVec::from_slice(&state.ally_handles);
            for ah in allies {
                if state
                    .creature(ah)
                    .is_some_and(|c| c.is_alive() && c.is_player)
                {
                    let _ = osty::summon(state, content, summon)?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Lethality => {
            apply_self_power(
                content,
                state,
                ctx,
                PowerId::LethalityPower,
                if upg { 75 } else { 50 },
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Melancholy => {
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
            Ok(CardPlayOutcome::default())
        }
        CardId::Misery => {
            let target = require_target(ctx, "Misery")?;
            let mut debuffs: Vec<(PowerInst, Option<PowerExtension>, i32)> = Vec::new();
            let target_powers = state
                .creature(target)
                .map(|c| c.powers.clone())
                .unwrap_or_default();
            for p in target_powers {
                let p_meta = content.power_meta(p.id)?;
                if power_type_for_amount(p_meta, p.amount) == PowerType::Debuff {
                    let ext = p
                        .ext_index
                        .and_then(|idx| state.power_extensions.get(idx as usize).cloned());
                    debuffs.push((p, ext, p.amount));
                }
            }
            for i in 0..debuffs.len() {
                let p_id = debuffs[i].0.id;
                let amt = debuffs[i].2;
                if let Some(internal_id) = temporary_power_internal_id(p_id) {
                    if let Some(pos) = debuffs.iter().position(|e| e.0.id == internal_id) {
                        debuffs[pos].2 += amt;
                    }
                }
            }

            let dmg = Dec::from_i32(if upg { 9 } else { 7 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;

            for enemy in hittable_enemies(state, content)? {
                if enemy == target {
                    continue;
                }
                for (p, ext, amt) in &debuffs {
                    if *amt == 0 {
                        continue;
                    }
                    if let Some(existing_idx) = power::find_existing_instance_for_stacking(
                        state,
                        content,
                        p.id,
                        enemy,
                        p.applier_handle,
                    )? {
                        power::modify_amount(
                            state,
                            content,
                            enemy,
                            existing_idx,
                            Dec::from_i32(*amt),
                            p.applier_handle,
                            Some(ctx.card),
                        )?;
                    } else if let Some(ext_val) = ext.clone() {
                        power::apply_new_with_extension(
                            state,
                            content,
                            p.id,
                            enemy,
                            Dec::from_i32(*amt),
                            p.applier_handle,
                            Some(ctx.card),
                            ext_val,
                        )?;
                    } else {
                        power::apply(
                            state,
                            content,
                            p.id,
                            enemy,
                            Dec::from_i32(*amt),
                            p.applier_handle,
                            Some(ctx.card),
                        )?;
                    }
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::NecroMastery => {
            let _ = osty::summon(state, content, Dec::from_i32(if upg { 8 } else { 5 }))?;
            apply_self_power(content, state, ctx, PowerId::NecroMasteryPower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::NegativePulse => {
            let blk = Dec::from_i32(if upg { 6 } else { 5 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            let doom = Dec::from_i32(if upg { 11 } else { 7 });
            for enemy in hittable_enemies(state, content)? {
                power::apply(
                    state,
                    content,
                    PowerId::DoomPower,
                    enemy,
                    doom,
                    Some(owner),
                    Some(ctx.card),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Neurosurge => {
            gold::gain_energy(state, Dec::from_i32(if upg { 4 } else { 3 }))?;
            let _ = card_pile::draw(state, content, 2, false)?;
            apply_self_power(content, state, ctx, PowerId::NeurosurgePower, 3)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::NoEscape => {
            let target = require_target(ctx, "NoEscape")?;
            let target_doom = get_power_amount(state, target, PowerId::DoomPower);
            let base = if upg { 15 } else { 10 };
            let doom = base + 5 * target_doom.div_euclid(10);
            power::apply(
                state,
                content,
                PowerId::DoomPower,
                target,
                Dec::from_i32(doom),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Oblivion => {
            let target = require_target(ctx, "Oblivion")?;
            power::apply(
                state,
                content,
                PowerId::OblivionPower,
                target,
                Dec::from_i32(if upg { 4 } else { 3 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Pagestorm => {
            apply_self_power(content, state, ctx, PowerId::PagestormPower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Parse => {
            let _ = card_pile::draw(state, content, if upg { 4 } else { 3 }, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Poke => {
            let target = require_target(ctx, "Poke")?;
            if let Some(osty_h) = state.osty_handle().filter(|&h| {
                state.creature(h).is_some_and(|c| c.is_alive())
            }) {
                let dmg = Dec::from_i32(if upg { 9 } else { 6 });
                AttackBuilder::from_card(
                    osty_h,
                    ctx.card,
                    *ctx,
                    dmg,
                    AttackTargetSpec::Single(target),
                )
                .execute(state, content)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Protector => {
            let target = require_target(ctx, "Protector")?;
            if let Some(osty_h) = state.osty_handle().filter(|&h| {
                state.creature(h).is_some_and(|c| c.is_alive())
            }) {
                let osty_max = state.creature(osty_h).map_or(0, |c| c.max_hp);
                let dmg = Dec::from_i32((if upg { 15 } else { 10 }) + osty_max);
                AttackBuilder::from_card(
                    osty_h,
                    ctx.card,
                    *ctx,
                    dmg,
                    AttackTargetSpec::Single(target),
                )
                .execute(state, content)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::PullAggro => {
            let _ = osty::summon(state, content, Dec::from_i32(if upg { 5 } else { 4 }))?;
            let blk = Dec::from_i32(if upg { 9 } else { 7 });
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
        CardId::PullFromBelow => {
            let target = require_target(ctx, "PullFromBelow")?;
            let hits = count_actor_events_this_combat(state, "EtherealPlayFinished");
            let dmg = Dec::from_i32(if upg { 7 } else { 5 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .with_hit_count(hits)
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Putrefy => {
            let target = require_target(ctx, "Putrefy")?;
            let amt = Dec::from_i32(if upg { 3 } else { 2 });
            power::apply(
                state,
                content,
                PowerId::WeakPower,
                target,
                amt,
                Some(owner),
                Some(ctx.card),
            )?;
            power::apply(
                state,
                content,
                PowerId::VulnerablePower,
                target,
                amt,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Rattle => {
            let target = require_target(ctx, "Rattle")?;
            if let Some(osty_h) = state.osty_handle().filter(|&h| {
                state.creature(h).is_some_and(|c| c.is_alive())
            }) {
                let hits = 1 + count_actor_events_this_turn(state, "OstyAttack");
                let dmg = Dec::from_i32(if upg { 9 } else { 7 });
                AttackBuilder::from_card(
                    osty_h,
                    ctx.card,
                    *ctx,
                    dmg,
                    AttackTargetSpec::Single(target),
                )
                .with_hit_count(hits)
                .execute(state, content)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Reanimate => {
            let _ = osty::summon(state, content, Dec::from_i32(if upg { 25 } else { 20 }))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Reap => {
            let target = require_target(ctx, "Reap")?;
            let dmg = Dec::from_i32(if upg { 33 } else { 27 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::ReaperForm => {
            apply_self_power(content, state, ctx, PowerId::ReaperFormPower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Reave => {
            let target = require_target(ctx, "Reave")?;
            let dmg = Dec::from_i32(if upg { 13 } else { 10 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            create_and_generate_souls(
                content,
                state,
                1,
                upg,
                PileType::Draw,
                CardPilePosition::Random,
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::RightHandHand => {
            let target = require_target(ctx, "RightHandHand")?;
            if let Some(osty_h) = state.osty_handle().filter(|&h| {
                state.creature(h).is_some_and(|c| c.is_alive())
            }) {
                let dmg = Dec::from_i32(if upg { 6 } else { 4 });
                AttackBuilder::from_card(
                    osty_h,
                    ctx.card,
                    *ctx,
                    dmg,
                    AttackTargetSpec::Single(target),
                )
                .execute(state, content)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Sacrifice => {
            if let Some(osty_h) = state.osty_handle().filter(|&h| {
                state.creature(h).is_some_and(|c| c.is_alive())
            }) {
                let osty_max = state.creature(osty_h).map_or(0, |c| c.max_hp);
                let blk = Dec::from_i32(osty_max * 3);
                creature::kill(state, content, osty_h, Some(ctx.card))?;
                block::gain_block(
                    state,
                    content,
                    owner,
                    blk,
                    ValueProp::MOVE,
                    Some(ctx.card),
                    Some(*ctx),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Scourge => {
            let target = require_target(ctx, "Scourge")?;
            power::apply(
                state,
                content,
                PowerId::DoomPower,
                target,
                Dec::from_i32(if upg { 16 } else { 13 }),
                Some(owner),
                Some(ctx.card),
            )?;
            let _ = card_pile::draw(state, content, if upg { 2 } else { 1 }, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SculptingStrike => {
            let target = require_target(ctx, "SculptingStrike")?;
            let dmg = Dec::from_i32(if upg { 12 } else { 9 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let cands: SmallVec<[CardHandle; 16]> = state
                .player
                .hand
                .iter()
                .copied()
                .filter(|&h| {
                    state
                        .card(h)
                        .is_some_and(|c| !c.local_keywords.contains(CardKeywords::ETHEREAL))
                })
                .collect();
            match select_from_hand(state, &cands, 1, 1, source_id, choice_queue)? {
                SelectResult::Selected(sel) => {
                    on_play_resume(content, state, ctx, &sel, 1)?;
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                    needs_choice: Some((req, 1)),
                    transform_into: None,
                }),
            }
        }
        CardId::Seance => match select_from_pile(state, content, PileType::Draw, 1, source_id, choice_queue)? {
            SelectResult::Selected(sel) => {
                on_play_resume(content, state, ctx, &sel, 1)?;
                Ok(CardPlayOutcome::default())
            }
            SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                needs_choice: Some((req, 1)),
                transform_into: None,
            }),
        },
        CardId::SentryMode => {
            apply_self_power(content, state, ctx, PowerId::SentryModePower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Severance => {
            let target = require_target(ctx, "Severance")?;
            let dmg = Dec::from_i32(if upg { 18 } else { 13 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let s0 = card_pile::create_card_in_combat(state, content, CardId::Soul, 0)?;
            let s1 = card_pile::create_card_in_combat(state, content, CardId::Soul, 0)?;
            let s2 = card_pile::create_card_in_combat(state, content, CardId::Soul, 0)?;
            generate_existing_card_into_pile(content, state, s0, PileType::Draw, CardPilePosition::Random)?;
            generate_existing_card_into_pile(content, state, s1, PileType::Discard, CardPilePosition::Bottom)?;
            generate_existing_card_into_pile(content, state, s2, PileType::Hand, CardPilePosition::Bottom)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SharedFate => {
            let target = require_target(ctx, "SharedFate")?;
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                owner,
                Dec::from_i32(-2),
                Some(owner),
                Some(ctx.card),
            )?;
            power::apply(
                state,
                content,
                PowerId::StrengthPower,
                target,
                Dec::from_i32(if upg { -3 } else { -2 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Shroud => {
            apply_self_power(content, state, ctx, PowerId::ShroudPower, if upg { 4 } else { 3 })?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SicEm => {
            let target = require_target(ctx, "SicEm")?;
            if let Some(osty_h) = state.osty_handle().filter(|&h| {
                state.creature(h).is_some_and(|c| c.is_alive())
            }) {
                let dmg = Dec::from_i32(if upg { 6 } else { 5 });
                AttackBuilder::from_card(
                    osty_h,
                    ctx.card,
                    *ctx,
                    dmg,
                    AttackTargetSpec::Single(target),
                )
                .execute(state, content)?;
            }
            power::apply(
                state,
                content,
                PowerId::SicEmPower,
                target,
                Dec::from_i32(if upg { 4 } else { 3 }),
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SleightOfFlesh => {
            apply_self_power(
                content,
                state,
                ctx,
                PowerId::SleightOfFleshPower,
                if upg { 13 } else { 9 },
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Snap => {
            let target = require_target(ctx, "Snap")?;
            if let Some(osty_h) = state.osty_handle().filter(|&h| {
                state.creature(h).is_some_and(|c| c.is_alive())
            }) {
                let dmg = Dec::from_i32(if upg { 10 } else { 7 });
                AttackBuilder::from_card(
                    osty_h,
                    ctx.card,
                    *ctx,
                    dmg,
                    AttackTargetSpec::Single(target),
                )
                .execute(state, content)?;
            }
            let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
            let mut cands: SmallVec<[CardHandle; 16]> = SmallVec::new();
            for h in hand {
                if !card_pile::card_has_keyword(state, content, h, CardKeywords::RETAIN)? {
                    cands.push(h);
                }
            }
            match select_from_hand(state, &cands, 1, 1, source_id, choice_queue)? {
                SelectResult::Selected(sel) => {
                    on_play_resume(content, state, ctx, &sel, 1)?;
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                    needs_choice: Some((req, 1)),
                    transform_into: None,
                }),
            }
        }
        CardId::SoulStorm => {
            let target = require_target(ctx, "SoulStorm")?;
            let souls = state
                .player
                .exhaust
                .iter()
                .filter(|&&h| state.card(h).is_some_and(|c| c.id == CardId::Soul))
                .count() as i32;
            let dmg = Dec::from_i32(9 + (if upg { 6 } else { 4 }) * souls);
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Soulbound => {
            let target = require_target(ctx, "Soulbound")?;
            power::apply(
                state,
                content,
                PowerId::SoulboundPower,
                target,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Sow => {
            let dmg = Dec::from_i32(if upg { 11 } else { 8 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SpiritOfAsh => {
            apply_self_power(
                content,
                state,
                ctx,
                PowerId::SpiritOfAshPower,
                if upg { 5 } else { 4 },
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Spur => {
            let _ = osty::summon(state, content, Dec::from_i32(if upg { 5 } else { 3 }))?;
            if let Some(osty_h) = state.osty_handle() {
                creature::heal(state, content, osty_h, Dec::from_i32(if upg { 7 } else { 5 }))?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Squeeze => {
            let target = require_target(ctx, "Squeeze")?;
            if let Some(osty_h) = state.osty_handle().filter(|&h| {
                state.creature(h).is_some_and(|c| c.is_alive())
            }) {
                let mut osty_attacks = 0i32;
                for &h in state
                    .player
                    .hand
                    .iter()
                    .chain(state.player.draw.iter())
                    .chain(state.player.discard.iter())
                    .chain(state.player.exhaust.iter())
                    .chain(state.player.play.iter())
                {
                    if h == ctx.card {
                        continue;
                    }
                    if let Some(ci) = state.card(h) {
                        if content
                            .card_meta(ci.id)?
                            .base_tags
                            .contains(CardTags::OSTY_ATTACK)
                        {
                            osty_attacks += 1;
                        }
                    }
                }
                let base = if upg { 30 } else { 25 };
                let extra = if upg { 6 } else { 5 };
                let dmg = Dec::from_i32(base + extra * osty_attacks);
                AttackBuilder::from_card(
                    osty_h,
                    ctx.card,
                    *ctx,
                    dmg,
                    AttackTargetSpec::Single(target),
                )
                .execute(state, content)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::StrikeNecrobinder => {
            let target = require_target(ctx, "StrikeNecrobinder")?;
            let dmg = Dec::from_i32(if upg { 9 } else { 6 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::TheScythe => {
            let target = require_target(ctx, "TheScythe")?;
            let dmg = Dec::from_i32(13 + the_scythe_increased_damage(state, ctx.card));
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let inc = if upg { 7 } else { 5 };
            add_the_scythe_increased_damage(state, ctx.card, inc)?;
            if let Some(deck_h) = card.deck_card_handle {
                if state.card(deck_h).is_some() {
                    add_the_scythe_increased_damage(state, deck_h, inc)?;
                    state
                        .semantic_history
                        .record_growth(sts2_kernel::GrowthSource::TheScythe);
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::TimesUp => {
            let target = require_target(ctx, "TimesUp")?;
            let doom = get_power_amount(state, target, PowerId::DoomPower);
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(doom),
                AttackTargetSpec::Single(target),
            )
            .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Transfigure => {
            let cands: SmallVec<[CardHandle; 16]> = state.player.hand.iter().copied().collect();
            match select_from_hand(state, &cands, 1, 1, source_id, choice_queue)? {
                SelectResult::Selected(sel) => {
                    on_play_resume(content, state, ctx, &sel, 1)?;
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                    needs_choice: Some((req, 1)),
                    transform_into: None,
                }),
            }
        }
        CardId::Undeath => {
            let blk = Dec::from_i32(if upg { 9 } else { 7 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            let clone_h = clone_card_in_combat(state, ctx.card)?;
            generate_existing_card_into_pile(
                content,
                state,
                clone_h,
                PileType::Discard,
                CardPilePosition::Bottom,
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Underworld => {
            apply_self_power(content, state, ctx, PowerId::UnderworldPower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Unleash => {
            let target = require_target(ctx, "Unleash")?;
            if let Some(osty_h) = state.osty_handle() {
                if let Some(osty_c) = state.creature(osty_h) {
                    if osty_c.is_alive() {
                        let base = if upg { 9 } else { 6 };
                        let dmg = Dec::from_i32(base + osty_c.current_hp);
                        AttackBuilder::from_card(
                            osty_h,
                            ctx.card,
                            *ctx,
                            dmg,
                            AttackTargetSpec::Single(target),
                        )
                        .execute(state, content)?;
                    }
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Veilpiercer => {
            let target = require_target(ctx, "Veilpiercer")?;
            let dmg = Dec::from_i32(if upg { 13 } else { 10 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            apply_self_power(content, state, ctx, PowerId::VeilpiercerPower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Wisp => {
            gold::gain_energy(state, Dec::ONE)?;
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
    resume_step: u8,
) -> Result<CardPlayOutcome, KernelError> {
    let card = *state
        .card(ctx.card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?;
    if resume_step != 1 {
        return Err(KernelError::Unsupported {
            kind: "CardResume",
            id: card.id.model_id_str().to_owned(),
        });
    }

    match card.id {
        CardId::Cleanse => {
            if let Some(&sel) = selected.first() {
                card_pile::exhaust(state, content, sel, false)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Dredge | CardId::Graveblast => {
            for &sel in selected {
                card_pile::add_sync(state, sel, PileType::Hand, CardPilePosition::Bottom);
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::SculptingStrike => {
            if let Some(&sel) = selected.first() {
                if let Some(c) = state.card_mut(sel) {
                    c.local_keywords.insert(CardKeywords::ETHEREAL);
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Seance => {
            for &sel in selected {
                let _ = card_cmd::transform(state, content, sel, CardId::Soul)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Snap => {
            if let Some(&sel) = selected.first() {
                if let Some(c) = state.card_mut(sel) {
                    c.local_keywords.insert(CardKeywords::RETAIN);
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Transfigure => {
            for &sel in selected {
                let Some(ci) = state.card(sel) else {
                    continue;
                };
                let is_x = content
                    .card_meta(ci.id)?
                    .flags
                    .contains(CardStaticFlags::IS_X_ENERGY_COST);
                if !is_x && ci.canonical_base_energy_cost >= 0 {
                    add_energy_cost_this_combat(state, sel, 1);
                }
                if let Some(c) = state.card_mut(sel) {
                    c.base_replay_count += 1;
                }
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
        CardId::BansheesCry => {
            if let Some(c) = state.card_mut(card) {
                if c.base_energy_cost > 0 {
                    c.base_energy_cost = (c.base_energy_cost - 2).max(0);
                }
            }
        }
        CardId::Demesne
        | CardId::Eidolon
        | CardId::ForbiddenGrimoire
        | CardId::Pagestorm
        | CardId::Protector
        | CardId::Sacrifice
        | CardId::Seance
        | CardId::SentryMode => {
            if let Some(c) = state.card_mut(card) {
                if c.base_energy_cost > 0 {
                    c.base_energy_cost = (c.base_energy_cost - 1).max(0);
                }
            }
        }
        CardId::CallOfTheVoid | CardId::Soulbound => {
            if let Some(c) = state.card_mut(card) {
                c.local_keywords.insert(CardKeywords::INNATE);
            }
        }
        CardId::Dredge | CardId::Misery | CardId::ReaperForm | CardId::TimesUp | CardId::Wisp => {
            if let Some(c) = state.card_mut(card) {
                c.local_keywords.insert(CardKeywords::RETAIN);
            }
        }
        CardId::Graveblast | CardId::Transfigure | CardId::Underworld => {
            if let Some(c) = state.card_mut(card) {
                c.local_keywords.remove(CardKeywords::EXHAUST);
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
        CardId::TheScythe => {
            let dmg = Dec::from_i32(13 + the_scythe_increased_damage(state, card))
                .min(Dec::CLAMP_MAX_999M);
            Ok(smallvec::smallvec![("Damage", dmg)])
        }
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
    let ci = *state
        .card(card)
        .ok_or_else(|| KernelError::InvalidState("missing card".to_owned()))?;
    match ci.id {
        CardId::BansheesCry => {
            match slot {
                HookSlot::AfterCardEnteredCombat => {
                    if let HookArgs::CardOnly(entered) = args {
                        if *entered == card && ci.clone_of_handle.is_none() {
                            let ethereal_plays =
                                count_actor_events_this_combat(state, "EtherealPlayFinished");
                            if ethereal_plays > 0 {
                                add_energy_cost_this_combat(state, card, -2 * ethereal_plays);
                            }
                        }
                    }
                }
                HookSlot::AfterCardPlayed => {
                    if let HookArgs::CardPlay(ctx) = args {
                        if card_pile::card_has_keyword(
                            state,
                            content,
                            ctx.card,
                            CardKeywords::ETHEREAL,
                        )? {
                            add_energy_cost_this_combat(state, card, -2);
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }
        CardId::Flatten => {
            match slot {
                HookSlot::AfterCardEnteredCombat => {
                    if let HookArgs::CardOnly(entered) = args {
                        if *entered == card && count_actor_events_this_turn(state, "OstyAttack") > 0 {
                            set_temp_cost_override_this_turn(state, card, 0);
                        }
                    }
                }
                HookSlot::AfterAttack => {
                    if let HookArgs::Attack { info, .. } = args {
                        if state.osty_handle().is_some_and(|oh| info.attacker == oh) {
                            set_temp_cost_override_this_turn(state, card, 0);
                        }
                    }
                }
                _ => {}
            }
            Ok(HookResult::default())
        }
        CardId::Melancholy => {
            if slot == HookSlot::AfterDeath {
                if let HookArgs::AfterDeath {
                    was_removal_prevented,
                    ..
                } = args
                {
                    if !*was_removal_prevented
                        && matches!(
                            ci.pile,
                            PileType::Draw
                                | PileType::Hand
                                | PileType::Discard
                                | PileType::Exhaust
                                | PileType::Play
                        )
                    {
                        add_energy_cost_this_combat(state, card, -1);
                    }
                }
            }
            Ok(HookResult::default())
        }
        CardId::RightHandHand => {
            if slot == HookSlot::AfterCardPlayedLate {
                if let HookArgs::CardPlay(ctx) = args {
                    if ctx.energy_value >= 2 && ci.pile == PileType::Discard {
                        card_pile::add_sync(state, card, PileType::Hand, CardPilePosition::Bottom);
                    }
                }
            }
            Ok(HookResult::default())
        }
        _ => {
            let _ = card_meta(ci.id)?;
            Err(KernelError::Unsupported {
                kind: "CardHook",
                id: ci.id.model_id_str().to_owned(),
            })
        }
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
