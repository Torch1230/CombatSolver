use smallvec::SmallVec;
use std::borrow::Cow;
use std::sync::LazyLock;
use sts2_kernel::commands::card_select::SelectResult;
use sts2_kernel::commands::{
    autoplay, block, card as card_cmd, card_factory, card_pile, card_select,
    damage::AttackBuilder, damage::AttackTargetSpec, forge, gold, power,
};
use sts2_kernel::content_dispatch::{CardPlayContext, CardPlayOutcome};
use sts2_kernel::{
    hooks, BattleState, CardHandle, CardInst, CardTempFlags, ChoiceTiming, ContentDispatcher,
    CreatureHandle, FieldValue, HookArgs, HookResult, KernelError, LocalEnergyModifier,
    PowerExtension, StepOutcome,
};
use sts2_types::generated_ids::CARD_POOL_REGENT;
use sts2_types::{
    generated_card_meta, CardId, CardKeywords, CardMeta, CardPilePosition, CardRarity,
    CardStaticFlags, CardType, Dec, HookSlot, LocalEnergyModifierKind, PileType, PowerId,
    RunRngType, TargetType, ValueProp,
};

const REGENT_CARD_COUNT: usize = 91;
const QUASAR_OFFER_MARKER: &str = "__quasar_offer";
const BOMBARDMENT_HAS_REPLAYED_KEY: &str = "Bombardment._hasReplayed";
const KINGLY_PUNCH_DAMAGE_KEY: &str = "KinglyPunch._damage";
const MONOLOGUE_STRENGTH_KEY: &str = "MonologuePower._strength";

#[inline]
const fn regent_index(id: CardId) -> Option<usize> {
    match id {
        CardId::Alignment => Some(0),
        CardId::Arsenal => Some(1),
        CardId::AstralPulse => Some(2),
        CardId::BeatIntoShape => Some(3),
        CardId::Begone => Some(4),
        CardId::BigBang => Some(5),
        CardId::BlackHole => Some(6),
        CardId::Bombardment => Some(7),
        CardId::Bulwark => Some(8),
        CardId::BundleOfJoy => Some(9),
        CardId::CelestialMight => Some(10),
        CardId::Charge => Some(11),
        CardId::ChildOfTheStars => Some(12),
        CardId::CloakOfStars => Some(13),
        CardId::CollisionCourse => Some(14),
        CardId::Comet => Some(15),
        CardId::Conqueror => Some(16),
        CardId::Constellation => Some(17),
        CardId::Convergence => Some(18),
        CardId::CosmicIndifference => Some(19),
        CardId::CrashLanding => Some(20),
        CardId::CrescentSpear => Some(21),
        CardId::CrushUnder => Some(22),
        CardId::DecisionsDecisions => Some(23),
        CardId::DefendRegent => Some(24),
        CardId::Devastate => Some(25),
        CardId::DyingStar => Some(26),
        CardId::FallingStar => Some(27),
        CardId::ForegoneConclusion => Some(28),
        CardId::Furnace => Some(29),
        CardId::GammaBlast => Some(30),
        CardId::GatherLight => Some(31),
        CardId::Genesis => Some(32),
        CardId::Glimmer => Some(33),
        CardId::Glitterstream => Some(34),
        CardId::Glow => Some(35),
        CardId::Guards => Some(36),
        CardId::GuidingStar => Some(37),
        CardId::HammerTime => Some(38),
        CardId::HeavenlyDrill => Some(39),
        CardId::Hegemony => Some(40),
        CardId::HeirloomHammer => Some(41),
        CardId::HiddenCache => Some(42),
        CardId::IAmInvincible => Some(43),
        CardId::KinglyKick => Some(44),
        CardId::KinglyPunch => Some(45),
        CardId::KnockoutBlow => Some(46),
        CardId::KnowThyPlace => Some(47),
        CardId::Largesse => Some(48),
        CardId::LunarBlast => Some(49),
        CardId::MakeItSo => Some(50),
        CardId::ManifestAuthority => Some(51),
        CardId::MeteorShower => Some(52),
        CardId::MonarchsGaze => Some(53),
        CardId::Monologue => Some(54),
        CardId::NeutronAegis => Some(55),
        CardId::Orbit => Some(56),
        CardId::PaleBlueDot => Some(57),
        CardId::Parry => Some(58),
        CardId::ParticleWall => Some(59),
        CardId::Patter => Some(60),
        CardId::PhotonCut => Some(61),
        CardId::Plot => Some(62),
        CardId::PillarOfCreation => Some(63),
        CardId::Prophesize => Some(64),
        CardId::Quasar => Some(65),
        CardId::Radiate => Some(66),
        CardId::RefineBlade => Some(67),
        CardId::Reflect => Some(68),
        CardId::Resonance => Some(69),
        CardId::RoyalGamble => Some(70),
        CardId::Royalties => Some(71),
        CardId::SeekingEdge => Some(72),
        CardId::SevenStars => Some(73),
        CardId::ShiningStrike => Some(74),
        CardId::SolarStrike => Some(75),
        CardId::SpectrumShift => Some(76),
        CardId::SpoilsOfBattle => Some(77),
        CardId::Stardust => Some(78),
        CardId::StrikeRegent => Some(79),
        CardId::SummonForth => Some(80),
        CardId::Supermassive => Some(81),
        CardId::SwordSage => Some(82),
        CardId::Terraforming => Some(83),
        CardId::TheSealedThrone => Some(84),
        CardId::TheSmith => Some(85),
        CardId::Tutor => Some(86),
        CardId::Tyranny => Some(87),
        CardId::Venerate => Some(88),
        CardId::VoidForm => Some(89),
        CardId::WroughtInWar => Some(90),
        _ => None,
    }
}

static REGENT_CARD_META: LazyLock<[CardMeta; REGENT_CARD_COUNT]> = LazyLock::new(|| {
    std::array::from_fn(|i| {
        let id = CARD_POOL_REGENT[i];
        let mut meta = *generated_card_meta(id);
        match id {
            CardId::ParticleWall | CardId::ShiningStrike => {
                meta.hook_mask = meta.hook_mask.with(HookSlot::ModifyCardPlayResultLocation);
            }
            _ => {}
        }
        if matches!(
            id,
            CardId::KinglyKick | CardId::KinglyPunch | CardId::Royalties
        ) {
            meta.flags |= CardStaticFlags::HAS_GROWTH;
        }
        if matches!(
            id,
            CardId::BundleOfJoy
                | CardId::DecisionsDecisions
                | CardId::Largesse
                | CardId::ManifestAuthority
                | CardId::Quasar
                | CardId::Stardust
        ) {
            meta.flags |= CardStaticFlags::USES_RNG;
        }
        if matches!(
            id,
            CardId::Alignment
                | CardId::BeatIntoShape
                | CardId::BigBang
                | CardId::Conqueror
                | CardId::CrescentSpear
                | CardId::DecisionsDecisions
                | CardId::Glimmer
                | CardId::HeavenlyDrill
                | CardId::IAmInvincible
                | CardId::LunarBlast
                | CardId::MakeItSo
                | CardId::Monologue
                | CardId::Orbit
                | CardId::PaleBlueDot
                | CardId::Patter
                | CardId::PhotonCut
                | CardId::PillarOfCreation
                | CardId::Radiate
                | CardId::Stardust
                | CardId::SummonForth
                | CardId::Supermassive
                | CardId::Terraforming
                | CardId::TheSealedThrone
                | CardId::VoidForm
        ) {
            meta.flags |= CardStaticFlags::ORDER_SENSITIVE;
        }
        meta
    })
});

pub fn card_meta(id: CardId) -> Result<&'static CardMeta, KernelError> {
    let Some(idx) = regent_index(id) else {
        return Err(KernelError::Unsupported {
            kind: "Card",
            id: id.model_id_str().to_owned(),
        });
    };
    Ok(&REGENT_CARD_META[idx])
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
        ext.push_local_energy_modifier(LocalEnergyModifier {
            kind: LocalEnergyModifierKind::AddThisCombat,
            amount: delta,
            is_reduce_only: false,
        });
    }
}

fn kingly_punch_damage(state: &BattleState, card: CardHandle) -> Dec {
    let upg = state.card(card).is_some_and(|c| c.upgrade_level > 0);
    let default = Dec::from_i32(if upg { 10 } else { 8 });
    state
        .card(card)
        .and_then(|c| state.card_ext(c.ext_index))
        .and_then(|ext| ext.private_fields.get(KINGLY_PUNCH_DAMAGE_KEY))
        .map_or(default, |fv| match fv {
            FieldValue::Decimal(d) => *d,
            FieldValue::Int(i) => Dec::from_i32(*i),
            _ => default,
        })
}

fn is_visually_colorless(id: CardId) -> bool {
    let meta = generated_card_meta(id);
    meta.flags.contains(CardStaticFlags::IS_COLORLESS)
        || meta.rarity == CardRarity::Token
        || matches!(
            id,
            CardId::Abundance
                | CardId::Apotheosis
                | CardId::Apparition
                | CardId::BrightestFlame
                | CardId::ByrdSwoop
                | CardId::DingyDagger
                | CardId::Enlightenment
                | CardId::Exterminate
                | CardId::FeedingFrenzy
                | CardId::GodByrd
                | CardId::MadScience
                | CardId::Maul
                | CardId::Metamorphosis
                | CardId::MightyShield
                | CardId::MightyWeapon
                | CardId::NeowsFury
                | CardId::Peck
                | CardId::Relax
                | CardId::Squash
                | CardId::ToricToughness
                | CardId::Wish
                | CardId::Whistle
        )
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
        // Fetch's per-turn finished state is not inherited by clones (AfterCloned).
        ext.private_fields.remove("Fetch._finishedRound");
        ext.private_fields.remove("Fetch._finishedSide");
        ext.private_fields.remove("Fetch._finishedTurnNumbers");
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

fn generate_existing_card_into_hand(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    handle: CardHandle,
) -> Result<(), KernelError> {
    let creator = Some(state.player.creature_handle);
    card_pile::enter_combat(
        state,
        content,
        handle,
        PileType::Hand,
        CardPilePosition::Bottom,
        true,
    )?;
    hooks::after_card_generated(state, content, handle, creator)?;
    hooks::after_card_generated_for_combat(state, content, handle, creator)
}

fn create_quasar_offer(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_id: CardId,
    upgrade: bool,
) -> Result<CardHandle, KernelError> {
    let handle = card_pile::create_card_in_combat(state, content, card_id, if upgrade { 1 } else { 0 })?;
    if let Some(ext) = state.card_ext_mut(handle) {
        ext.private_fields
            .insert(Cow::Borrowed(QUASAR_OFFER_MARKER), FieldValue::Bool(true));
    }
    Ok(handle)
}

fn finish_quasar_choice(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    all_candidates: &[CardHandle],
    selected: &[CardHandle],
) -> Result<(), KernelError> {
    let chosen = selected.first().copied();
    for &cand in all_candidates {
        if let Some(ext) = state
            .card(cand)
            .and_then(|c| c.ext_index)
            .and_then(|idx| state.card_extensions.get_mut(idx as usize))
        {
            ext.private_fields.remove(QUASAR_OFFER_MARKER);
        }
        if Some(cand) != chosen {
            if let Some(c) = state.card_mut(cand) {
                c.removed_from_state = true;
            }
            state.invalidate_hook_cache();
        }
    }
    if let Some(h) = chosen {
        generate_existing_card_into_hand(content, state, h)?;
    }
    Ok(())
}

fn sorted_draw_candidates(
    state: &BattleState,
    _content: &dyn ContentDispatcher,
    min_count: usize,
    max_count: usize,
) -> Result<SmallVec<[CardHandle; 16]>, KernelError> {
    let mut cands: SmallVec<[CardHandle; 16]> = state.player.draw.iter().copied().collect();
    if min_count != max_count || cands.len() > min_count {
        if cands.len() <= u16::MAX as usize {
            let mut pairs: SmallVec<[(u64, CardHandle); 64]> = SmallVec::with_capacity(cands.len());
            for (orig_idx, &h) in cands.iter().enumerate() {
                let cid = state
                    .card(h)
                    .ok_or_else(|| KernelError::InvalidState("missing draw card".to_owned()))?
                    .id;
                let key = (u64::from(crate::card_draw_sort_key(cid)) << 16) | (orig_idx as u64);
                pairs.push((key, h));
            }
            pairs.sort_unstable_by_key(|&(k, _)| k);
            cands.clear();
            cands.extend(pairs.into_iter().map(|(_, h)| h));
        } else {
            let mut pairs: SmallVec<[(u32, CardHandle); 64]> = SmallVec::with_capacity(cands.len());
            for &h in &cands {
                let cid = state
                    .card(h)
                    .ok_or_else(|| KernelError::InvalidState("missing draw card".to_owned()))?
                    .id;
                pairs.push((crate::card_draw_sort_key(cid), h));
            }
            pairs.sort_by_key(|&(k, _)| k);
            cands = pairs.into_iter().map(|(_, h)| h).collect();
        }
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

fn generate_distinct_colorless(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    count: usize,
    upgrade_generated: bool,
) -> Result<(), KernelError> {
    let creator = Some(state.player.creature_handle);
    let pool = card_factory::colorless_card_pool_candidates(state);
    let chosen = card_factory::get_distinct_for_combat(
        state,
        &pool,
        count,
        RunRngType::CombatCardGeneration,
    );
    let upgrade_level = if upgrade_generated { 1 } else { 0 };
    for cid in chosen {
        let _ = card_pile::add_generated_card_to_combat(
            state,
            content,
            cid,
            PileType::Hand,
            CardPilePosition::Bottom,
            upgrade_level,
            creator,
        )?;
    }
    Ok(())
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
        CardId::Alignment => {
            gold::gain_energy(state, Dec::from_i32(if upg { 3 } else { 2 }))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Arsenal => {
            apply_self_power(content, state, ctx, PowerId::ArsenalPower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::AstralPulse => {
            let dmg = Dec::from_i32(if upg { 8 } else { 6 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
                .with_hit_count(2)
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::BeatIntoShape => {
            let target = require_target(ctx, "BeatIntoShape")?;
            let forge_per_hit = Dec::from_i32(if upg { 7 } else { 5 });
            let results = AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                forge_per_hit,
                AttackTargetSpec::Single(target),
            )
            .execute(state, content)?;
            let owner_hits_this_turn = state
                .damage_history
                .iter()
                .filter(|entry| {
                    entry.round_number == state.round_number
                        && entry.current_side == state.current_side
                        && entry.player_turn_numbers.as_slice() == [state.player.turn_number]
                        && entry.receiver_creature_handle == target
                        && entry.dealer_creature_handle == Some(owner)
                        && entry.props.is_powered_attack()
                })
                .count() as i32;
            let current_attack_hits = results.len() as i32;
            let forge_amt = forge_per_hit
                .checked_add(forge_per_hit.checked_mul(Dec::from_i32(owner_hits_this_turn - current_attack_hits))?)?;
            forge::forge(state, content, forge_amt)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Begone => {
            let cands: SmallVec<[CardHandle; 16]> = state
                .player
                .hand
                .iter()
                .copied()
                .filter(|&h| h != ctx.card)
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
        CardId::BigBang => {
            let _ = card_pile::draw(state, content, 1, false)?;
            gold::gain_stars(state, content, Dec::ONE)?;
            gold::gain_energy(state, Dec::ONE)?;
            forge::forge(state, content, Dec::from_i32(5))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::BlackHole => {
            apply_self_power(content, state, ctx, PowerId::BlackHolePower, if upg { 4 } else { 3 })?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Bombardment => {
            let target = require_target(ctx, "Bombardment")?;
            let dmg = Dec::from_i32(if upg { 24 } else { 18 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let has_replayed = state
                .card(ctx.card)
                .and_then(|c| state.card_ext(c.ext_index))
                .and_then(|ext| ext.private_fields.get(BOMBARDMENT_HAS_REPLAYED_KEY))
                .is_some_and(|v| matches!(v, FieldValue::Bool(true)));
            if !has_replayed {
                if let Some(ext) = state.card_ext_mut(ctx.card) {
                    ext.private_fields
                        .insert(Cow::Borrowed(BOMBARDMENT_HAS_REPLAYED_KEY), FieldValue::Bool(true));
                }
                AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                    .execute(state, content)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Bulwark => {
            let blk = Dec::from_i32(if upg { 15 } else { 12 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            forge::forge(state, content, Dec::from_i32(if upg { 13 } else { 10 }))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::BundleOfJoy => {
            generate_distinct_colorless(content, state, if upg { 4 } else { 3 }, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::CelestialMight => {
            let target = require_target(ctx, "CelestialMight")?;
            let hits = if upg { 4 } else { 3 };
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(6),
                AttackTargetSpec::Single(target),
            )
            .with_hit_count(hits)
            .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Charge => match select_from_pile(state, content, PileType::Draw, 2, source_id, choice_queue)? {
            SelectResult::Selected(sel) => {
                on_play_resume(content, state, ctx, &sel, 1)?;
                Ok(CardPlayOutcome::default())
            }
            SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                needs_choice: Some((req, 1)),
                transform_into: None,
            }),
        },
        CardId::ChildOfTheStars => {
            apply_self_power(
                content,
                state,
                ctx,
                PowerId::ChildOfTheStarsPower,
                if upg { 3 } else { 2 },
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::CloakOfStars => {
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
            Ok(CardPlayOutcome::default())
        }
        CardId::CollisionCourse => {
            let target = require_target(ctx, "CollisionCourse")?;
            let dmg = Dec::from_i32(if upg { 14 } else { 10 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let _ = card_pile::add_generated_card_to_combat(
                state,
                content,
                CardId::Debris,
                PileType::Hand,
                CardPilePosition::Bottom,
                0,
                Some(owner),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Comet => {
            let target = require_target(ctx, "Comet")?;
            let dmg = Dec::from_i32(if upg { 44 } else { 33 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let three = Dec::from_i32(3);
            power::apply(
                state,
                content,
                PowerId::WeakPower,
                target,
                three,
                Some(owner),
                Some(ctx.card),
            )?;
            power::apply(
                state,
                content,
                PowerId::VulnerablePower,
                target,
                three,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Conqueror => {
            let target = require_target(ctx, "Conqueror")?;
            power::apply(
                state,
                content,
                PowerId::ConquerorPower,
                target,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            forge::forge(state, content, Dec::from_i32(if upg { 5 } else { 3 }))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Constellation => {
            let target = require_target(ctx, "Constellation")?;
            let _ = card_pile::draw(state, content, 1, false)?;
            gold::gain_energy(state, Dec::ONE)?;
            let blk = Dec::from_i32(if upg { 12 } else { 9 });
            block::gain_block(
                state,
                content,
                target,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Convergence => {
            apply_self_power(content, state, ctx, PowerId::RetainHandPower, 1)?;
            apply_self_power(content, state, ctx, PowerId::EnergyNextTurnPower, 1)?;
            apply_self_power(
                content,
                state,
                ctx,
                PowerId::StarNextTurnPower,
                if upg { 2 } else { 1 },
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::CosmicIndifference => {
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
        CardId::CrashLanding => {
            let dmg = Dec::from_i32(if upg { 26 } else { 21 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
                .execute(state, content)?;
            let slots_to_fill = card_pile::MAX_CARDS_IN_HAND.saturating_sub(state.player.hand.len());
            for _ in 0..slots_to_fill {
                let _ = card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    CardId::Debris,
                    PileType::Hand,
                    CardPilePosition::Bottom,
                    0,
                    Some(owner),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::CrescentSpear => {
            let target = require_target(ctx, "CrescentSpear")?;
            let star_card_count = state
                .player
                .hand
                .iter()
                .chain(state.player.draw.iter())
                .chain(state.player.discard.iter())
                .chain(state.player.exhaust.iter())
                .chain(state.player.play.iter())
                .filter(|&&h| {
                    state.card(h).is_some_and(|c| {
                        let m = generated_card_meta(c.id);
                        m.canonical_star_cost >= 0
                            || m.flags.contains(CardStaticFlags::IS_X_STAR_COST)
                    })
                })
                .count() as i32;
            let dmg = Dec::from_i32(8 + (if upg { 3 } else { 2 }) * star_card_count);
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::CrushUnder => {
            let enemies = hittable_enemies(state, content)?;
            let dmg = Dec::from_i32(if upg { 9 } else { 8 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
                .execute(state, content)?;
            let str_loss = Dec::from_i32(if upg { 2 } else { 1 });
            for enemy in enemies {
                power::apply(
                    state,
                    content,
                    PowerId::CrushUnderPower,
                    enemy,
                    str_loss,
                    Some(owner),
                    Some(ctx.card),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::DecisionsDecisions => {
            let _ = card_pile::draw(state, content, if upg { 5 } else { 3 }, false)?;
            let hand: SmallVec<[CardHandle; 16]> = SmallVec::from_slice(&state.player.hand);
            let mut cands: SmallVec<[CardHandle; 16]> = SmallVec::new();
            for h in hand {
                if h == ctx.card {
                    continue;
                }
                let Some(ci) = state.card(h) else {
                    continue;
                };
                if content.card_meta(ci.id)?.card_type == CardType::Skill
                    && !card_pile::card_has_keyword(state, content, h, CardKeywords::UNPLAYABLE)?
                {
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
        CardId::DefendRegent => {
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
        CardId::Devastate => {
            let target = require_target(ctx, "Devastate")?;
            let dmg = Dec::from_i32(if upg { 45 } else { 35 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::DyingStar => {
            let enemies = hittable_enemies(state, content)?;
            let dmg = Dec::from_i32(if upg { 11 } else { 9 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
                .execute(state, content)?;
            let str_loss = Dec::from_i32(if upg { 11 } else { 9 });
            for enemy in enemies {
                power::apply(
                    state,
                    content,
                    PowerId::DyingStarPower,
                    enemy,
                    str_loss,
                    Some(owner),
                    Some(ctx.card),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::FallingStar => {
            let target = require_target(ctx, "FallingStar")?;
            let dmg = Dec::from_i32(if upg { 12 } else { 8 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            power::apply(
                state,
                content,
                PowerId::WeakPower,
                target,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            power::apply(
                state,
                content,
                PowerId::VulnerablePower,
                target,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::ForegoneConclusion => {
            apply_self_power(
                content,
                state,
                ctx,
                PowerId::ForegoneConclusionPower,
                if upg { 3 } else { 2 },
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Furnace => {
            apply_self_power(content, state, ctx, PowerId::FurnacePower, if upg { 7 } else { 5 })?;
            Ok(CardPlayOutcome::default())
        }
        CardId::GammaBlast => {
            let target = require_target(ctx, "GammaBlast")?;
            let dmg = Dec::from_i32(if upg { 18 } else { 13 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let two = Dec::TWO;
            power::apply(
                state,
                content,
                PowerId::WeakPower,
                target,
                two,
                Some(owner),
                Some(ctx.card),
            )?;
            power::apply(
                state,
                content,
                PowerId::VulnerablePower,
                target,
                two,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::GatherLight => {
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
            gold::gain_stars(state, content, Dec::ONE)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Genesis => {
            apply_self_power(content, state, ctx, PowerId::GenesisPower, if upg { 3 } else { 2 })?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Glimmer => {
            let _ = card_pile::draw(state, content, if upg { 4 } else { 3 }, false)?;
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
        CardId::Glitterstream => {
            let raw_next = Dec::from_i32(if upg { 7 } else { 5 });
            let (next_turn_blk, _) = hooks::modify_block(
                state,
                content,
                owner,
                raw_next,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
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
            power::apply(
                state,
                content,
                PowerId::BlockNextTurnPower,
                owner,
                next_turn_blk,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Glow => {
            gold::gain_stars(state, content, Dec::from_i32(if upg { 2 } else { 1 }))?;
            let _ = card_pile::draw(state, content, 1, false)?;
            apply_self_power(content, state, ctx, PowerId::DrawCardsNextTurnPower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Guards => Ok(CardPlayOutcome::default()),
        CardId::GuidingStar => {
            let target = require_target(ctx, "GuidingStar")?;
            let dmg = Dec::from_i32(if upg { 13 } else { 12 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            apply_self_power(
                content,
                state,
                ctx,
                PowerId::DrawCardsNextTurnPower,
                if upg { 3 } else { 2 },
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::HammerTime => {
            apply_self_power(content, state, ctx, PowerId::HammerTimePower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::HeavenlyDrill => {
            let target = require_target(ctx, "HeavenlyDrill")?;
            let x = hooks::modify_x_value(state, content, ctx.card, ctx.energy_value)?;
            let hits = if x >= 4 { x * 2 } else { x };
            let dmg = Dec::from_i32(if upg { 10 } else { 8 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .with_hit_count(hits)
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Hegemony => {
            let target = require_target(ctx, "Hegemony")?;
            let dmg = Dec::from_i32(if upg { 18 } else { 15 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            apply_self_power(
                content,
                state,
                ctx,
                PowerId::EnergyNextTurnPower,
                if upg { 3 } else { 2 },
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::HeirloomHammer => {
            let target = require_target(ctx, "HeirloomHammer")?;
            let dmg = Dec::from_i32(if upg { 25 } else { 20 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let cands: SmallVec<[CardHandle; 16]> = state
                .player
                .hand
                .iter()
                .copied()
                .filter(|&h| state.card(h).is_some_and(|c| is_visually_colorless(c.id)))
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
        CardId::HiddenCache => {
            gold::gain_stars(state, content, Dec::ONE)?;
            apply_self_power(
                content,
                state,
                ctx,
                PowerId::StarNextTurnPower,
                if upg { 4 } else { 3 },
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::IAmInvincible => {
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
        CardId::KinglyKick => {
            let target = require_target(ctx, "KinglyKick")?;
            let dmg = Dec::from_i32(if upg { 35 } else { 27 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::KinglyPunch => {
            let target = require_target(ctx, "KinglyPunch")?;
            let dmg = kingly_punch_damage(state, ctx.card);
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::KnockoutBlow => {
            let target = require_target(ctx, "KnockoutBlow")?;
            let dmg = Dec::from_i32(if upg { 38 } else { 30 });
            let results =
                AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                    .execute(state, content)?;
            if results.iter().any(|r| r.was_target_killed) {
                gold::gain_stars(state, content, Dec::from_i32(5))?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::KnowThyPlace => {
            let target = require_target(ctx, "KnowThyPlace")?;
            power::apply(
                state,
                content,
                PowerId::WeakPower,
                target,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            power::apply(
                state,
                content,
                PowerId::VulnerablePower,
                target,
                Dec::ONE,
                Some(owner),
                Some(ctx.card),
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Largesse => {
            let _target = require_target(ctx, "Largesse")?;
            generate_distinct_colorless(content, state, 1, upg)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::LunarBlast => {
            let target = require_target(ctx, "LunarBlast")?;
            let hits = state.player.counters.skill_cards_played_this_turn;
            let dmg = Dec::from_i32(if upg { 5 } else { 4 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .with_hit_count(hits)
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::MakeItSo => {
            let target = require_target(ctx, "MakeItSo")?;
            let dmg = Dec::from_i32(if upg { 9 } else { 6 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::ManifestAuthority => {
            let blk = Dec::from_i32(if upg { 8 } else { 7 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            generate_distinct_colorless(content, state, 1, upg)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::MeteorShower => {
            let dmg = Dec::from_i32(if upg { 21 } else { 14 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
                .execute(state, content)?;
            let two = Dec::TWO;
            for enemy in hittable_enemies(state, content)? {
                power::apply(
                    state,
                    content,
                    PowerId::WeakPower,
                    enemy,
                    two,
                    Some(owner),
                    Some(ctx.card),
                )?;
            }
            for enemy in hittable_enemies(state, content)? {
                power::apply(
                    state,
                    content,
                    PowerId::VulnerablePower,
                    enemy,
                    two,
                    Some(owner),
                    Some(ctx.card),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::MonarchsGaze => {
            apply_self_power(content, state, ctx, PowerId::MonarchsGazePower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Monologue => {
            if let Some(p_idx) = apply_self_power(content, state, ctx, PowerId::MonologuePower, 1)? {
                let existing_ext = state
                    .creature(owner)
                    .and_then(|c| c.powers.get(p_idx))
                    .map(|p| p.ext_index);
                if let Some(opt_ext) = existing_ext {
                    let ext_idx = match opt_ext {
                        Some(i) => i as usize,
                        None => {
                            let i = state.power_extensions.len();
                            state.power_extensions.push(PowerExtension::default());
                            if let Some(c) = state.creature_mut(owner) {
                                if let Some(p) = c.powers.get_mut(p_idx) {
                                    p.ext_index = Some(i as u32);
                                }
                            }
                            i
                        }
                    };
                    state.power_extensions[ext_idx]
                        .private_fields
                        .insert(Cow::Borrowed(MONOLOGUE_STRENGTH_KEY), FieldValue::Decimal(Dec::ONE));
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::NeutronAegis => {
            apply_self_power(content, state, ctx, PowerId::PlatingPower, if upg { 11 } else { 8 })?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Orbit => {
            apply_self_power(content, state, ctx, PowerId::OrbitPower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::PaleBlueDot => {
            apply_self_power(
                content,
                state,
                ctx,
                PowerId::PaleBlueDotPower,
                if upg { 2 } else { 1 },
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Parry => {
            apply_self_power(content, state, ctx, PowerId::ParryPower, if upg { 14 } else { 10 })?;
            Ok(CardPlayOutcome::default())
        }
        CardId::ParticleWall => {
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
        CardId::Patter => {
            let blk = Dec::from_i32(if upg { 10 } else { 8 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            apply_self_power(content, state, ctx, PowerId::VigorPower, if upg { 3 } else { 2 })?;
            Ok(CardPlayOutcome::default())
        }
        CardId::PhotonCut => {
            let target = require_target(ctx, "PhotonCut")?;
            let dmg = Dec::from_i32(if upg { 13 } else { 10 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            let _ = card_pile::draw(state, content, if upg { 2 } else { 1 }, false)?;
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
        CardId::Plot => {
            let amt = Dec::from_i32(if upg { 3 } else { 2 });
            let allies: SmallVec<[CreatureHandle; 8]> = SmallVec::from_slice(&state.ally_handles);
            for ah in allies {
                if state
                    .creature(ah)
                    .is_some_and(|c| c.is_alive() && c.is_player)
                {
                    power::apply(
                        state,
                        content,
                        PowerId::DrawCardsNextTurnPower,
                        ah,
                        amt,
                        Some(owner),
                        Some(ctx.card),
                    )?;
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::PillarOfCreation => {
            apply_self_power(
                content,
                state,
                ctx,
                PowerId::PillarOfCreationPower,
                if upg { 3 } else { 2 },
            )?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Prophesize => {
            let _ = card_pile::draw(state, content, if upg { 9 } else { 6 }, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Quasar => {
            let pool = card_factory::colorless_card_pool_candidates(state);
            let chosen_ids = card_factory::get_distinct_for_combat(
                state,
                &pool,
                3,
                RunRngType::CombatCardGeneration,
            );
            let mut cands: SmallVec<[CardHandle; 4]> = SmallVec::new();
            for cid in chosen_ids {
                cands.push(create_quasar_offer(state, content, cid, upg)?);
            }
            match card_select::select_cards(
                state,
                &cands,
                0,
                1,
                ChoiceTiming::Action,
                source_id,
                PileType::None,
                true,
                choice_queue,
            )? {
                SelectResult::Selected(sel) => {
                    finish_quasar_choice(content, state, &cands, &sel)?;
                    Ok(CardPlayOutcome::default())
                }
                SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                    needs_choice: Some((req, 1)),
                    transform_into: None,
                }),
            }
        }
        CardId::Radiate => {
            let hits = state.player.counters.stars_gained_this_turn.to_i32_trunc()?;
            let dmg = Dec::from_i32(if upg { 4 } else { 3 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::AllOpponents)
                .with_hit_count(hits)
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::RefineBlade => {
            forge::forge(state, content, Dec::from_i32(if upg { 12 } else { 8 }))?;
            apply_self_power(content, state, ctx, PowerId::EnergyNextTurnPower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Reflect => {
            let blk = Dec::from_i32(if upg { 20 } else { 15 });
            block::gain_block(
                state,
                content,
                owner,
                blk,
                ValueProp::MOVE,
                Some(ctx.card),
                Some(*ctx),
            )?;
            apply_self_power(content, state, ctx, PowerId::ReflectPower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Resonance => {
            apply_self_power(content, state, ctx, PowerId::StrengthPower, if upg { 2 } else { 1 })?;
            let minus_one = Dec::from_i32(-1);
            for enemy in hittable_enemies(state, content)? {
                power::apply(
                    state,
                    content,
                    PowerId::StrengthPower,
                    enemy,
                    minus_one,
                    Some(owner),
                    Some(ctx.card),
                )?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::RoyalGamble => {
            gold::gain_stars(state, content, Dec::from_i32(9))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Royalties => {
            apply_self_power(content, state, ctx, PowerId::RoyaltiesPower, if upg { 40 } else { 30 })?;
            state
                .semantic_history
                .record_growth(sts2_kernel::GrowthSource::Royalties);
            Ok(CardPlayOutcome::default())
        }
        CardId::SeekingEdge => {
            apply_self_power(content, state, ctx, PowerId::SeekingEdgePower, if upg { 5 } else { 1 })?;
            forge::forge(state, content, Dec::from_i32(if upg { 11 } else { 7 }))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SevenStars => {
            AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(7),
                AttackTargetSpec::AllOpponents,
            )
            .with_hit_count(7)
            .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::ShiningStrike => {
            let target = require_target(ctx, "ShiningStrike")?;
            let dmg = Dec::from_i32(if upg { 11 } else { 8 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            gold::gain_stars(state, content, Dec::TWO)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SolarStrike => {
            let target = require_target(ctx, "SolarStrike")?;
            let dmg = Dec::from_i32(if upg { 10 } else { 9 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            gold::gain_stars(state, content, Dec::from_i32(if upg { 2 } else { 1 }))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SpectrumShift => {
            apply_self_power(content, state, ctx, PowerId::SpectrumShiftPower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SpoilsOfBattle => {
            forge::forge(state, content, Dec::from_i32(if upg { 9 } else { 6 }))?;
            let _ = card_pile::draw(state, content, 2, false)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Stardust => {
            let hits = hooks::modify_x_value(state, content, ctx.card, ctx.star_value)?;
            if hits > 0 {
                let dmg = Dec::from_i32(if upg { 7 } else { 5 });
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
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::StrikeRegent => {
            let target = require_target(ctx, "StrikeRegent")?;
            let dmg = Dec::from_i32(if upg { 9 } else { 6 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SummonForth => {
            let blades: SmallVec<[CardHandle; 4]> = state
                .player
                .draw
                .iter()
                .chain(state.player.discard.iter())
                .chain(state.player.exhaust.iter())
                .chain(state.player.play.iter())
                .copied()
                .filter(|&h| state.card(h).is_some_and(|c| c.id == CardId::SovereignBlade))
                .collect();
            for bh in blades {
                card_pile::add_sync(state, bh, PileType::Hand, CardPilePosition::Bottom);
            }
            forge::forge(state, content, Dec::from_i32(if upg { 11 } else { 8 }))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Supermassive => {
            let target = require_target(ctx, "Supermassive")?;
            let per_gen = if upg { 4 } else { 3 };
            let dmg = Dec::from_i32(5 + per_gen * state.player.counters.cards_generated_this_combat);
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::SwordSage => {
            apply_self_power(content, state, ctx, PowerId::SwordSagePower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Terraforming => {
            apply_self_power(content, state, ctx, PowerId::VigorPower, if upg { 10 } else { 7 })?;
            Ok(CardPlayOutcome::default())
        }
        CardId::TheSealedThrone => {
            apply_self_power(content, state, ctx, PowerId::TheSealedThronePower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::TheSmith => {
            forge::forge(state, content, Dec::from_i32(if upg { 40 } else { 30 }))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Tutor => match select_from_pile(state, content, PileType::Draw, 1, source_id, choice_queue)? {
            SelectResult::Selected(sel) => {
                on_play_resume(content, state, ctx, &sel, 1)?;
                Ok(CardPlayOutcome::default())
            }
            SelectResult::NeedsChoice(req) => Ok(CardPlayOutcome {
                needs_choice: Some((req, 1)),
                transform_into: None,
            }),
        },
        CardId::Tyranny => {
            apply_self_power(content, state, ctx, PowerId::TyrannyPower, 1)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Venerate => {
            gold::gain_stars(state, content, Dec::from_i32(if upg { 3 } else { 2 }))?;
            Ok(CardPlayOutcome::default())
        }
        CardId::VoidForm => {
            apply_self_power(content, state, ctx, PowerId::VoidFormPower, 2)?;
            state.end_player_turn_requested = true;
            Ok(CardPlayOutcome::default())
        }
        CardId::WroughtInWar => {
            let target = require_target(ctx, "WroughtInWar")?;
            let dmg = Dec::from_i32(if upg { 9 } else { 7 });
            AttackBuilder::from_card(owner, ctx.card, *ctx, dmg, AttackTargetSpec::Single(target))
                .execute(state, content)?;
            forge::forge(state, content, Dec::from_i32(if upg { 9 } else { 7 }))?;
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
    let upg = card.upgrade_level > 0;
    if resume_step != 1 {
        return Err(KernelError::Unsupported {
            kind: "CardResume",
            id: card.id.model_id_str().to_owned(),
        });
    }

    match card.id {
        CardId::Begone => {
            if let Some(&sel) = selected.first() {
                if let Some(rep) = card_cmd::transform(state, content, sel, CardId::MinionStrike)? {
                    if upg {
                        card_cmd::upgrade(state, content, rep)?;
                    }
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Charge => {
            for &sel in selected {
                if let Some(rep) = card_cmd::transform(state, content, sel, CardId::MinionDiveBomb)? {
                    if upg {
                        card_cmd::upgrade(state, content, rep)?;
                    }
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::CosmicIndifference | CardId::Glimmer | CardId::PhotonCut => {
            if let Some(&sel) = selected.first() {
                card_pile::add_sync(state, sel, PileType::Draw, CardPilePosition::Top);
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::DecisionsDecisions => {
            if let Some(&sel) = selected.first() {
                for _ in 0..3 {
                    if hooks::is_over_or_ending(state)
                        || state.player_creature().is_none_or(|c| c.is_dead())
                    {
                        continue;
                    }
                    let unplayable =
                        card_pile::card_has_keyword(state, content, sel, CardKeywords::UNPLAYABLE)?;
                    let target_type = state
                        .card(sel)
                        .ok_or_else(|| KernelError::InvalidState("missing selected card".to_owned()))
                        .and_then(|c| Ok(content.card_meta(c.id)?.target_type))?;
                    if !unplayable
                        && matches!(target_type, TargetType::AnyEnemy | TargetType::RandomEnemy)
                        && hooks::should_play(state, content, sel)?
                        && hittable_enemies(state, content)?.is_empty()
                    {
                        break;
                    }
                    let outcome = autoplay::from_cards(state, content, &[sel], None, false)?;
                    if let StepOutcome::NeedsChoice(req) = outcome {
                        return Err(KernelError::Unsupported {
                            kind: "CardAutoplayChoice",
                            id: req.source_id.into_owned(),
                        });
                    }
                }
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::HeirloomHammer => {
            if let Some(&sel) = selected.first() {
                let clone_h = clone_card_in_combat(state, sel)?;
                generate_existing_card_into_hand(content, state, clone_h)?;
            }
            Ok(CardPlayOutcome::default())
        }
        CardId::Quasar => {
            let offer_handles: SmallVec<[CardHandle; 4]> = state
                .cards
                .iter()
                .filter(|c| {
                    !c.removed_from_state
                        && c.pile == PileType::None
                        && state
                            .card_ext(c.ext_index)
                            .is_some_and(|e| e.private_fields.contains_key(QUASAR_OFFER_MARKER))
                })
                .map(|c| c.handle)
                .collect();
            finish_quasar_choice(content, state, &offer_handles, selected)?;
            Ok(CardPlayOutcome::default())
        }
        CardId::Tutor => {
            for &sel in selected {
                card_pile::add_sync(state, sel, PileType::Hand, CardPilePosition::Bottom);
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
        CardId::HammerTime
        | CardId::MonarchsGaze
        | CardId::Orbit
        | CardId::SevenStars
        | CardId::SpectrumShift
        | CardId::SwordSage
        | CardId::TheSealedThrone
        | CardId::Tutor => {
            if let Some(c) = state.card_mut(card) {
                if c.base_energy_cost > 0 {
                    c.base_energy_cost = (c.base_energy_cost - 1).max(0);
                }
            }
        }
        CardId::Arsenal | CardId::BigBang | CardId::Tyranny => {
            if let Some(c) = state.card_mut(card) {
                c.local_keywords.insert(CardKeywords::INNATE);
            }
        }
        CardId::Monologue | CardId::RoyalGamble => {
            if let Some(c) = state.card_mut(card) {
                c.local_keywords.insert(CardKeywords::RETAIN);
            }
        }
        CardId::KnowThyPlace => {
            if let Some(c) = state.card_mut(card) {
                c.local_keywords.remove(CardKeywords::EXHAUST);
            }
        }
        CardId::VoidForm => {
            if let Some(c) = state.card_mut(card) {
                c.local_keywords.remove(CardKeywords::ETHEREAL);
            }
        }
        CardId::KinglyPunch => {
            let cur = state
                .card(card)
                .and_then(|c| state.card_ext(c.ext_index))
                .and_then(|ext| ext.private_fields.get(KINGLY_PUNCH_DAMAGE_KEY))
                .map_or(Dec::from_i32(8), |fv| match fv {
                    FieldValue::Decimal(d) => *d,
                    FieldValue::Int(i) => Dec::from_i32(*i),
                    _ => Dec::from_i32(8),
                });
            let next = cur.checked_add(Dec::TWO)?;
            if let Some(ext) = state.card_ext_mut(card) {
                ext.private_fields
                    .insert(Cow::Borrowed(KINGLY_PUNCH_DAMAGE_KEY), FieldValue::Decimal(next));
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
        CardId::KinglyPunch => {
            let dmg = kingly_punch_damage(state, card).min(Dec::CLAMP_MAX_999M);
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
        CardId::IAmInvincible => {
            if slot == HookSlot::AfterAutoPostPlayPhaseEntered
                && state.player.draw.first().copied() == Some(card)
            {
                let outcome = autoplay::from_cards(state, content, &[card], None, false)?;
                if let StepOutcome::NeedsChoice(req) = outcome {
                    return Err(KernelError::Unsupported {
                        kind: "CardHookChoice",
                        id: req.source_id.into_owned(),
                    });
                }
            }
            Ok(HookResult::default())
        }
        CardId::KinglyKick => {
            if slot == HookSlot::AfterCardDrawn {
                if let HookArgs::CardDrawn { card: drawn, .. } = args {
                    if *drawn == card {
                        add_energy_cost_this_combat(state, card, -1);
                    }
                }
            }
            Ok(HookResult::default())
        }
        CardId::KinglyPunch => {
            if slot == HookSlot::AfterCardDrawn {
                if let HookArgs::CardDrawn { card: drawn, .. } = args {
                    if *drawn == card {
                        let cur = kingly_punch_damage(state, card);
                        let inc = Dec::from_i32(if ci.upgrade_level > 0 { 6 } else { 4 });
                        let next = cur.checked_add(inc)?;
                        if let Some(ext) = state.card_ext_mut(card) {
                            ext.private_fields.insert(
                                Cow::Borrowed(KINGLY_PUNCH_DAMAGE_KEY),
                                FieldValue::Decimal(next),
                            );
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        CardId::MakeItSo => {
            if slot == HookSlot::AfterCardPlayedLate {
                if let HookArgs::CardPlay(ctx) = args {
                    if let Some(played_ci) = state.card(ctx.card) {
                        let is_skill = content.card_meta(played_ci.id)?.card_type == CardType::Skill;
                        let in_non_hand_combat_pile = matches!(
                            ci.pile,
                            PileType::Draw | PileType::Discard | PileType::Exhaust
                        );
                        let count = state.player.counters.skill_cards_played_this_turn;
                        if is_skill && in_non_hand_combat_pile && count > 0 && count % 3 == 0 {
                            card_pile::add_sync(state, card, PileType::Hand, CardPilePosition::Bottom);
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }
        CardId::ParticleWall => {
            if slot == HookSlot::ModifyCardPlayResultLocation {
                if let HookArgs::CardPlayResultLocation {
                    card: hook_card,
                    pile,
                    ..
                } = args
                {
                    if *hook_card == card && *pile == PileType::Discard {
                        return Ok(HookResult {
                            location_val: Some((PileType::Hand, CardPilePosition::Bottom)),
                            modified: true,
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }
        CardId::ShiningStrike => {
            if slot == HookSlot::ModifyCardPlayResultLocation {
                if let HookArgs::CardPlayResultLocation {
                    card: hook_card,
                    pile,
                    ..
                } = args
                {
                    if *hook_card == card && *pile == PileType::Discard {
                        return Ok(HookResult {
                            location_val: Some((PileType::Draw, CardPilePosition::Top)),
                            modified: true,
                            ..Default::default()
                        });
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
