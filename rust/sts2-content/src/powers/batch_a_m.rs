use smallvec::SmallVec;
use std::borrow::Cow;
use std::sync::LazyLock;
use sts2_kernel::commands::card_select::SelectResult;
use sts2_kernel::commands::{
    arrow, autoplay, block, card, card_factory, card_pile, card_select, creature as creature_cmd,
    forge, gold, orb, osty, power,
};
use sts2_kernel::{
    hooks, ordering, BattleState, CardHandle, CardInst, CardTempFlags, ChoiceTiming,
    ContentDispatcher, CreatureHandle, FieldValue, HookArgs, HookResult, KernelError, PowerInst,
};
use sts2_types::{
    AfflictionId, CardId, CardKeywords, CardPilePosition, CardRarity, CardTags, CardType,
    CombatSide, Dec, HookSlot, MonsterId, OrbId, PileType, PowerId, PowerMeta, PowerStaticFlags,
    PowerType, RunRngType, ValueProp,
};

const AM_POWER_COUNT: usize = 169;

static AM_POWER_META: LazyLock<[PowerMeta; AM_POWER_COUNT]> = LazyLock::new(|| {
    std::array::from_fn(|i| {
        let mut meta = sts2_types::generated_ids::POWER_META_TABLE[i];
        if i != PowerId::AdaptablePower as usize
            && i != PowerId::DieForYouPower as usize
            && i != PowerId::MinionPower as usize
        {
            meta.flags |= PowerStaticFlags::REMOVE_AFTER_OWNER_DEATH;
        }
        if is_temporary_power_index(i) {
            meta.flags |= PowerStaticFlags::IS_TEMPORARY;
            meta.hook_mask = meta.hook_mask.with(HookSlot::BeforeApplied);
        }
        if matches!(
            i,
            x if x == PowerId::DampenPower as usize
                || x == PowerId::FeralPower as usize
                || x == PowerId::HexPower as usize
                || x == PowerId::IllusionPower as usize
                || x == PowerId::JugglingPower as usize
                || x == PowerId::LullabyPower as usize
        ) {
            meta.hook_mask = meta.hook_mask.with(HookSlot::AfterApplied);
        }
        if i == PowerId::BurrowedPower as usize
            || i == PowerId::DampenPower as usize
            || i == PowerId::HexPower as usize
            || i == PowerId::InterceptPower as usize
        {
            meta.hook_mask = meta.hook_mask.with(HookSlot::AfterRemoved);
        }
        if i == PowerId::CrueltyPower as usize {
            meta.hook_mask = meta.hook_mask.with(HookSlot::ModifyVulnerableMultiplier);
        }
        if i == PowerId::DebilitatePower as usize {
            meta.hook_mask = meta
                .hook_mask
                .with(HookSlot::ModifyVulnerableMultiplier)
                .with(HookSlot::ModifyWeakMultiplier);
        }
        meta
    })
});

const fn is_temporary_power_index(i: usize) -> bool {
    i == PowerId::AnticipatePower as usize
        || i == PowerId::ChoBarbsPower as usize
        || i == PowerId::CoordinatePower as usize
        || i == PowerId::CrushUnderPower as usize
        || i == PowerId::DarkShacklesPower as usize
        || i == PowerId::DyingStarPower as usize
        || i == PowerId::EnfeeblingTouchPower as usize
        || i == PowerId::FadePower as usize
        || i == PowerId::FeedingFrenzyPower as usize
        || i == PowerId::FlexPotionPower as usize
        || i == PowerId::FocusedStrikePower as usize
        || i == PowerId::HelicalDartPower as usize
        || i == PowerId::HotfixPower as usize
        || i == PowerId::HyperbeamFocusDownPower as usize
        || i == PowerId::ManglePower as usize
        || i == PowerId::MonarchsGazeStrengthDownPower as usize
}

#[inline(always)]
pub fn power_meta(id: PowerId) -> Result<&'static PowerMeta, KernelError> {
    let idx = id as usize;
    if idx < AM_POWER_COUNT {
        Ok(&AM_POWER_META[idx])
    } else {
        Err(KernelError::Unsupported {
            kind: "Power",
            id: id.model_id_str().to_owned(),
        })
    }
}

pub fn dispatch_hook(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    p: PowerInst,
    owner_side: CombatSide,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    match p.id {
        PowerId::AccelerantPower
        | PowerId::BackAttackLeftPower
        | PowerId::BackAttackRightPower
        | PowerId::FanOfKnivesPower => Ok(HookResult::default()),

        PowerId::MinionPower => {
            if slot == HookSlot::ShouldOwnerDeathTriggerFatal {
                if let HookArgs::Creature(target) = args {
                    if *target == creature {
                        return Ok(bool_result(false));
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::CrueltyPower => {
            if slot == HookSlot::ModifyVulnerableMultiplier {
                if let HookArgs::ModifyDamage {
                    target,
                    amount,
                    props,
                    ..
                } = args
                {
                    if let Some(t) = *target {
                        let next = cruelty_modify_vulnerable_multiplier(
                            creature, p.amount, t, *amount, *props,
                        )?;
                        return Ok(dec_result(next));
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::AccuracyPower => {
            if slot == HookSlot::ModifyDamageAdditive {
                if let HookArgs::ModifyDamage {
                    dealer,
                    props,
                    card_source,
                    ..
                } = args
                {
                    if *dealer == Some(creature)
                        && props.is_powered_attack()
                        && is_card_with_tag(state, content, *card_source, CardTags::SHIV)?
                    {
                        return Ok(dec_result(Dec::from_i32(p.amount)));
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::AfterimagePower => match slot {
            HookSlot::BeforeCardPlayed => {
                if let HookArgs::CardPlay(ctx) = args {
                    if creature == state.player.creature_handle {
                        push_card_int_field(
                            state,
                            creature,
                            power_idx,
                            "AfterimagePower._amountsForPlayedCards",
                            ctx.card.0 as i32,
                            p.amount,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(ctx) = args {
                    if let Some(amount) = take_card_int_field(
                        state,
                        creature,
                        power_idx,
                        "AfterimagePower._amountsForPlayedCards",
                        ctx.card.0 as i32,
                    ) {
                        if amount > 0 {
                            block::gain_block(
                                state,
                                content,
                                creature,
                                Dec::from_i32(amount),
                                ValueProp::UNPOWERED,
                                None,
                                None,
                            )?;
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::ArtifactPower => match slot {
            HookSlot::TryModifyPowerAmountReceived => {
                if let HookArgs::PowerChanged {
                    power_id,
                    owner,
                    amount,
                    ..
                } = args
                {
                    if *owner == creature
                        && get_power_type_for_amount(content, *power_id, *amount)?
                            == PowerType::Debuff
                    {
                        return Ok(HookResult {
                            dec_val: Some(Dec::ZERO),
                            modified: true,
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterModifyingPowerAmountReceived => {
                power::modify_amount_by_handle(
                    state,
                    content,
                    creature,
                    p.handle,
                    Dec::from_i32(-1),
                    None,
                    None,
                )?;
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::BarricadePower => {
            if slot == HookSlot::ShouldClearBlock {
                if let HookArgs::Creature(target) = args {
                    return Ok(bool_result(*target != creature));
                }
            }
            Ok(HookResult::default())
        }

        PowerId::BlockNextTurnPower => {
            if slot == HookSlot::AfterBlockCleared {
                if let HookArgs::Creature(target) = args {
                    if *target == creature {
                        block::gain_block(
                            state,
                            content,
                            creature,
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::BlurPower => match slot {
            HookSlot::ShouldClearBlock => {
                if let HookArgs::Creature(target) = args {
                    return Ok(bool_result(*target != creature));
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnStart => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        power::tick_down_duration_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::BufferPower => match slot {
            HookSlot::ModifyHpLostAfterOstyLate => {
                if let HookArgs::ModifyHpLost {
                    target,
                    card_source,
                    ..
                } = args
                {
                    let is_brightest = card_source
                        .and_then(|h| state.card(h))
                        .is_some_and(|c| c.id == CardId::BrightestFlame);
                    if *target == creature && !is_brightest {
                        return Ok(dec_result(Dec::ZERO));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterModifyingHpLostAfterOsty => {
                power::modify_amount_by_handle(
                    state,
                    content,
                    creature,
                    p.handle,
                    Dec::from_i32(-1),
                    None,
                    None,
                )?;
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::DarkEmbracePower => match slot {
            HookSlot::AfterCardExhausted => {
                if let HookArgs::CardExhausted {
                    caused_by_ethereal, ..
                } = args
                {
                    dark_embrace_after_card_exhausted(
                        state,
                        content,
                        creature,
                        p.amount,
                        *caused_by_ethereal,
                        p.handle,
                    )?;
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) && creature == state.player.creature_handle {
                        let deferred = get_int_field(
                            state,
                            creature,
                            power_idx,
                            "DarkEmbracePower._etherealExhausts",
                            0,
                        );
                        set_int_field(
                            state,
                            creature,
                            power_idx,
                            "DarkEmbracePower._etherealExhausts",
                            0,
                        );
                        let _ = card_pile::draw(state, content, p.amount * deferred, false)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::DemonFormPower => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        power::apply(
                            state,
                            content,
                            PowerId::StrengthPower,
                            creature,
                            Dec::from_i32(p.amount),
                            Some(creature),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::DexterityPower => {
            if slot == HookSlot::ModifyBlockAdditive {
                if let HookArgs::ModifyBlock {
                    target,
                    props,
                    card_source,
                    ..
                } = args
                {
                    let owned_source = if card_source.is_some() {
                        creature == state.player.creature_handle
                    } else {
                        *target == creature
                    };
                    if owned_source && !props.contains(ValueProp::UNPOWERED) {
                        return Ok(dec_result(Dec::from_i32(p.amount)));
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::DoomPower => {
            if matches!(slot, HookSlot::BeforeSideTurnEnd | HookSlot::AfterSideTurnEnd) {
                if let HookArgs::SideTurn { side, participants } = args {
                    let trigger = (*side != CombatSide::Player
                        && slot == HookSlot::BeforeSideTurnEnd)
                        || (*side != CombatSide::Enemy && slot == HookSlot::AfterSideTurnEnd);
                    if trigger && !hooks::is_over_or_ending(state) && participants.contains(&creature)
                    {
                        let doomed = get_doomed_creatures(state, owner_side);
                        if doomed.first().copied() == Some(creature) {
                            for ch in &doomed {
                                creature_cmd::kill(state, content, *ch, None)?;
                            }
                            for ch in doomed {
                                hooks::after_died_to_doom(state, content, ch)?;
                            }
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::DrawCardsNextTurnPower => match slot {
            HookSlot::ModifyHandDraw => {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    if creature == state.player.creature_handle && p.amount_on_turn_start != 0 {
                        return Ok(dec_result(
                            original_count.checked_add(Dec::from_i32(p.amount))?,
                        ));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterModifyingHandDraw => {
                let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::EnergyNextTurnPower => {
            if slot == HookSlot::AfterEnergyReset && creature == state.player.creature_handle {
                gold::gain_energy(state, content, Dec::from_i32(p.amount))?;
                let _ = power::remove_by_handle(state, content, creature, p.handle)?;
            }
            Ok(HookResult::default())
        }

        PowerId::EnvenomPower => {
            if slot == HookSlot::AfterDamageGiven {
                if let HookArgs::DamageResult {
                    target,
                    dealer,
                    props,
                    unblocked_damage,
                    ..
                } = args
                {
                    if *dealer == Some(creature)
                        && props.is_powered_attack()
                        && *unblocked_damage > 0
                    {
                        power::apply(
                            state,
                            content,
                            PowerId::PoisonPower,
                            *target,
                            Dec::from_i32(p.amount),
                            Some(creature),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::FeelNoPainPower => {
            if slot == HookSlot::AfterCardExhausted && creature == state.player.creature_handle {
                block::gain_block(
                    state,
                    content,
                    creature,
                    Dec::from_i32(p.amount),
                    ValueProp::UNPOWERED,
                    None,
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        PowerId::FlameBarrierPower => match slot {
            HookSlot::AfterDamageReceived => {
                if let HookArgs::DamageResult {
                    target,
                    dealer,
                    props,
                    ..
                } = args
                {
                    if *target == creature && props.is_powered_attack() {
                        if let Some(attacker) = *dealer {
                            sts2_kernel::commands::damage::damage_targets(
                                state,
                                content,
                                &[attacker],
                                Dec::from_i32(p.amount),
                                ValueProp::UNPOWERED,
                                Some(creature),
                                None,
                                None,
                            )?;
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { side, .. } = args {
                    if *side != owner_side {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::FocusPower => {
            if slot == HookSlot::ModifyOrbValue {
                if let HookArgs::ModifyOrbValue { amount, .. } = args {
                    if creature == state.player.creature_handle {
                        let next = amount.checked_add(Dec::from_i32(p.amount))?.max(Dec::ZERO);
                        return Ok(dec_result(next));
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::FrailPower => match slot {
            HookSlot::ModifyBlockMultiplicative => {
                if let HookArgs::ModifyBlock { target, props, .. } = args {
                    if *target == creature && props.is_powered_block() {
                        return Ok(dec_result(Dec::THREE_QUARTERS));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { side, .. } = args {
                    if *side == CombatSide::Enemy {
                        power::tick_down_duration_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::HardenedShellPower => match slot {
            HookSlot::ModifyHpLostBeforeOstyLate => {
                if let HookArgs::ModifyHpLost { target, amount, .. } = args {
                    if *target == creature && !amount.is_zero() {
                        let dmg_received = get_decimal_field(
                            state,
                            creature,
                            power_idx,
                            "HardenedShellPower._damageReceivedThisTurn",
                            Dec::ZERO,
                        );
                        let cap = Dec::from_i32(p.amount)
                            .checked_sub(dmg_received)?
                            .max(Dec::ZERO);
                        return Ok(dec_result((*amount).min(cap)));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterDamageReceived => {
                if let HookArgs::DamageResult {
                    target,
                    unblocked_damage,
                    ..
                } = args
                {
                    if *target == creature && *unblocked_damage > 0 {
                        let cur = get_decimal_field(
                            state,
                            creature,
                            power_idx,
                            "HardenedShellPower._damageReceivedThisTurn",
                            Dec::ZERO,
                        );
                        set_decimal_field(
                            state,
                            creature,
                            power_idx,
                            "HardenedShellPower._damageReceivedThisTurn",
                            cur.checked_add(Dec::from_i32(*unblocked_damage))?,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::BeforeSideTurnStart => {
                set_decimal_field(
                    state,
                    creature,
                    power_idx,
                    "HardenedShellPower._damageReceivedThisTurn",
                    Dec::ZERO,
                );
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::IntangiblePower => match slot {
            HookSlot::ModifyHpLostAfterOsty => {
                if let HookArgs::ModifyHpLost { target, amount, .. } = args {
                    if *target == creature && *amount >= Dec::ONE {
                        return Ok(dec_result(Dec::ONE));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ModifyDamageCap => {
                if let HookArgs::ModifyDamage { target, .. } = args {
                    if *target == Some(creature) {
                        return Ok(dec_result(Dec::ONE));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { side, .. } = args {
                    if *side == CombatSide::Enemy {
                        power::tick_down_duration_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::ChainsOfBindingPower => match slot {
            HookSlot::AfterCardDrawn => {
                if let HookArgs::CardDrawn { card: card_h, .. } = args {
                    if chains_of_binding_can_afflict_on_draw(
                        state,
                        creature,
                        owner_side,
                        p.amount,
                        *card_h,
                    ) {
                        let _ = card::afflict(state, *card_h, AfflictionId::Bound, p.amount);
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::BeforeCardPlayed => {
                if let HookArgs::CardPlay(ctx) = args {
                    let is_bound_non_dupe = creature == state.player.creature_handle
                        && state.card(ctx.card).is_some_and(|c| {
                            !c.temp_flags.contains(CardTempFlags::IS_DUPE)
                                && c.affliction.is_some_and(|a| a.id == AfflictionId::Bound)
                        });
                    if is_bound_non_dupe {
                        set_bool_field(
                            state,
                            creature,
                            power_idx,
                            "ChainsOfBindingPower._boundCardPlayedThisTurn",
                            true,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldPlay => {
                if let HookArgs::CardOnly(card_h) = args {
                    if creature == state.player.creature_handle
                        && state
                            .card(*card_h)
                            .is_some_and(|c| c.affliction.is_some_and(|a| a.id == AfflictionId::Bound))
                        && get_bool_field(
                            state,
                            creature,
                            power_idx,
                            "ChainsOfBindingPower._boundCardPlayedThisTurn",
                            false,
                        )
                    {
                        return Ok(bool_result(false));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::BeforeSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        set_bool_field(
                            state,
                            creature,
                            power_idx,
                            "ChainsOfBindingPower._boundCardPlayedThisTurn",
                            false,
                        );
                        if creature == state.player.creature_handle {
                            let card_start = if !state.deck_dirty.0 {
                                state.static_ctx.root_deck_prefix_len.min(state.cards.len())
                            } else {
                                0
                            };
                            #[cfg(debug_assertions)]
                            debug_assert!(
                                !state.cards[..card_start].iter().any(|c| {
                                    c.affliction.is_some_and(|a| a.id == AfflictionId::Bound)
                                        && !c.removed_from_state
                                        && !matches!(c.pile, PileType::None | PileType::Deck)
                                }),
                                "ChainsOfBindingPower found bound combat card inside root deck prefix"
                            );
                            let bound_cards: SmallVec<[CardHandle; 16]> = state.cards[card_start..]
                                .iter()
                                .filter(|c| {
                                    c.affliction.is_some_and(|a| a.id == AfflictionId::Bound)
                                        && !c.removed_from_state
                                        && !matches!(c.pile, PileType::None | PileType::Deck)
                                })
                                .map(|c| c.handle)
                                .collect();
                            for ch in bound_cards {
                                card::clear_affliction(state, ch);
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::CorruptionPower => match slot {
            HookSlot::TryModifyEnergyCostInCombatLate => {
                if let HookArgs::CardCost { card, .. } = args {
                    if creature == state.player.creature_handle
                        && is_card_type(state, content, *card, CardType::Skill)?
                    {
                        return Ok(HookResult {
                            dec_val: Some(Dec::ZERO),
                            modified: true,
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ModifyCardPlayResultLocation => {
                if let HookArgs::CardPlayResultLocation { card, position, .. } = args {
                    if creature == state.player.creature_handle
                        && is_card_type(state, content, *card, CardType::Skill)?
                    {
                        return Ok(HookResult {
                            location_val: Some((PileType::Exhaust, *position)),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::HellraiserPower => match slot {
            HookSlot::AfterCardDrawnEarly => {
                if let HookArgs::CardDrawn { card: card_h, .. } = args {
                    if creature == state.player.creature_handle
                        && is_card_with_tag(state, content, Some(*card_h), CardTags::STRIKE)?
                    {
                        let enemies = hittable_enemies(state, content)?;
                        let infinite_enemies =
                            enemies.iter().all(|&eh| has_infinite_hp_display(state, eh));
                        if infinite_enemies {
                            let count = get_int_field(
                                state,
                                creature,
                                power_idx,
                                "HellraiserPower._infiniteAutoPlaysThisTurn",
                                0,
                            );
                            set_int_field(
                                state,
                                creature,
                                power_idx,
                                "HellraiserPower._infiniteAutoPlaysThisTurn",
                                count + 1,
                            );
                            if count >= 9 {
                                return Ok(HookResult::default());
                            }
                        } else {
                            set_int_field(
                                state,
                                creature,
                                power_idx,
                                "HellraiserPower._infiniteAutoPlaysThisTurn",
                                0,
                            );
                        }
                        let _ = autoplay::from_cards(state, content, &[*card_h], None, false)?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        set_int_field(
                            state,
                            creature,
                            power_idx,
                            "HellraiserPower._infiniteAutoPlaysThisTurn",
                            0,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::JuggernautPower => {
            if slot == HookSlot::AfterBlockGained {
                if let HookArgs::ModifyBlock { target, amount, .. } = args {
                    if *amount > Dec::ZERO && *target == creature {
                        let enemies = hittable_enemies(state, content)?;
                        if let Some(&enemy) = state
                            .run_rngs
                            .get_mut(RunRngType::CombatTargets)
                            .next_item(&enemies)
                        {
                            sts2_kernel::commands::damage::damage_targets(
                                state,
                                content,
                                &[enemy],
                                Dec::from_i32(p.amount),
                                ValueProp::UNPOWERED,
                                Some(creature),
                                None,
                                None,
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        },

        _ => dispatch_hook_cold(content, state, creature, power_idx, p, owner_side, slot, args),
    }
}

#[cold]
#[inline(never)]
fn dispatch_hook_cold(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    p: PowerInst,
    owner_side: CombatSide,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    match p.id {
        PowerId::AdaptablePower => match slot {
            HookSlot::AfterDeath => {
                if let HookArgs::AfterDeath { target, .. } = args {
                    let is_reviving = get_bool_field(
                        state,
                        creature,
                        power_idx,
                        "AdaptablePower._isReviving",
                        false,
                    );
                    let is_test_subject = state.creature(*target).and_then(|c| c.monster_id)
                        == Some(MonsterId::TestSubject);
                    if *target == creature && !is_reviving && is_test_subject {
                        set_bool_field(
                            state,
                            creature,
                            power_idx,
                            "AdaptablePower._isReviving",
                            true,
                        );
                        if let Some(c) = state.creature_mut(creature) {
                            if let Some(m) = &mut c.monster {
                            m.current_state_id = std::borrow::Cow::Borrowed("RESPAWN_MOVE");
                            m.next_move_id = Some(std::borrow::Cow::Borrowed("RESPAWN_MOVE"));
                                m.next_move_can_transition_away = false;
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldAllowHitting => {
                if let HookArgs::Creature(target) = args {
                    if *target != creature {
                        return Ok(bool_result(true));
                    }
                    let is_reviving = get_bool_field(
                        state,
                        creature,
                        power_idx,
                        "AdaptablePower._isReviving",
                        false,
                    );
                    return Ok(bool_result(!is_reviving));
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldStopCombatFromEnding => Ok(bool_result(true)),
            HookSlot::ShouldCreatureBeRemovedFromCombatAfterDeath => {
                if let HookArgs::Creature(target) = args {
                    return Ok(bool_result(*target != creature));
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::AeonglassDominionPower => {
            if slot == HookSlot::BeforeHandDraw && creature == state.player.creature_handle {
                let _ = card_pile::add_generated_card_to_combat(
                    state,
                    content,
                    CardId::Wither,
                    PileType::Draw,
                    CardPilePosition::Top,
                    0,
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        PowerId::AggressionPower => {
            if slot == HookSlot::BeforeSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) && creature == state.player.creature_handle {
                        let mut attacks: Vec<CardHandle> = state
                            .player
                            .discard
                            .iter()
                            .copied()
                            .filter(|&h| {
                                state.card(h).is_some_and(|c| {
                                    content
                                        .card_meta(c.id)
                                        .is_ok_and(|m| m.card_type == CardType::Attack)
                                })
                            })
                            .collect();
                        let mut rng = *state.run_rngs.get(RunRngType::CombatCardSelection);
                        rng.unstable_shuffle(&mut attacks);
                        *state.run_rngs.get_mut(RunRngType::CombatCardSelection) = rng;
                        for card_h in attacks.into_iter().take(p.amount.max(0) as usize) {
                            card_pile::add_sync(
                                state,
                                card_h,
                                PileType::Hand,
                                CardPilePosition::Bottom,
                            );
                            card::upgrade(state, content, card_h)?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::AimPower => match slot {
            HookSlot::BeforeAttack => {
                if let HookArgs::Attack { info, .. } = args {
                    if info.attacker == creature
                        && info.damage_props.is_powered_attack()
                        && info.damage_props.contains(ValueProp::FROM_ARROW)
                    {
                        let ext_idx = ensure_power_ext(state, creature, power_idx);
                        if state.power_extensions[ext_idx].attack_command_id.is_none() {
                            state.power_extensions[ext_idx].attack_command_id =
                                Some(info.attack_id);
                            state.power_extensions[ext_idx].amount_when_attack_started = p.amount;
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ModifyDamageAdditive => {
                if let HookArgs::ModifyDamage {
                    dealer,
                    props,
                    ..
                } = args
                {
                    if *dealer == Some(creature)
                        && props.is_powered_attack()
                        && props.contains(ValueProp::FROM_ARROW)
                    {
                        return Ok(dec_result(Dec::from_i32(p.amount)));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterAttack => {
                if let HookArgs::Attack { info, .. } = args {
                    if let Some(ext_idx) = p.ext_index {
                        let ext = &mut state.power_extensions[ext_idx as usize];
                        if ext.attack_command_id == Some(info.attack_id) {
                            let consumed = ext.amount_when_attack_started;
                            ext.attack_command_id = None;
                            ext.amount_when_attack_started = 0;
                            power::modify_amount_by_handle(
                                state,
                                content,
                                creature,
                                p.handle,
                                Dec::from_i32(-consumed),
                                None,
                                None,
                            )?;
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::AllIsWellPower => {
            if slot == HookSlot::AfterCardGeneratedForCombat {
                if let HookArgs::CardGenerated { card, creator } = args {
                    all_is_well_on_card_generated(
                        state, content, creature, p.amount, *card, *creator,
                    )?;
                }
            }
            Ok(HookResult::default())
        }

        PowerId::AmnesiaPower => match slot {
            HookSlot::AfterPlayerTurnStart => {
                if creature == state.player.creature_handle {
                    gold::lose_energy(state, Dec::ONE)?;
                }
                Ok(HookResult::default())
            }
            HookSlot::ModifyHandDraw => {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    if creature == state.player.creature_handle {
                        return Ok(dec_result(
                            original_count.checked_sub(Dec::TWO)?.max(Dec::ZERO),
                        ));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { side, .. } = args {
                    if *side != CombatSide::Enemy {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::AnticipatePower | PowerId::FadePower | PowerId::HelicalDartPower => {
            dispatch_temporary_stat_power(
                content,
                state,
                creature,
                p,
                slot,
                args,
                PowerId::DexterityPower,
                1,
            )
        }

        PowerId::ArcheryPower => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        power::apply(
                            state,
                            content,
                            PowerId::AimPower,
                            creature,
                            Dec::from_i32(p.amount),
                            Some(creature),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::ArsenalPower => {
            if slot == HookSlot::AfterCardGeneratedForCombat {
                if let HookArgs::CardGenerated { creator, .. } = args {
                    arsenal_on_card_generated(state, content, creature, p.amount, *creator)?;
                }
            }
            Ok(HookResult::default())
        }

        PowerId::AsleepPower => match slot {
            HookSlot::AfterDamageReceived => {
                if let HookArgs::DamageResult {
                    target,
                    unblocked_damage,
                    ..
                } = args
                {
                    if *target == creature && *unblocked_damage != 0 {
                        let _ = power::remove_by_id(
                            state,
                            content,
                            creature,
                            PowerId::PlatingPower,
                        )?;
                        if let Some(c) = state.creature_mut(creature) {
                            if let Some(m) = &mut c.monster {
                                m.private_fields.insert(
                                    Cow::Borrowed("LagavulinMatriarch._isAwake"),
                                    FieldValue::Bool(true),
                                );
                                m.current_state_id = Cow::Borrowed("STUNNED");
                                m.next_move_id = Some(Cow::Borrowed("STUNNED"));
                                m.next_move_can_transition_away = false;
                                m.transient_move_follow_up_state_id =
                                    Some(Cow::Borrowed("SLASH_MOVE"));
                            }
                        }
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::BeforeSideTurnEndVeryEarly => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) && p.amount <= 1 {
                        let _ = power::remove_by_id(
                            state,
                            content,
                            creature,
                            PowerId::PlatingPower,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let next = power::modify_amount_by_handle(
                            state,
                            content,
                            creature,
                            p.handle,
                            Dec::from_i32(-1),
                            None,
                            None,
                        )?;
                        if next <= 0 {
                            if let Some(c) = state.creature_mut(creature) {
                                if let Some(m) = &mut c.monster {
                                    m.private_fields.insert(
                                        Cow::Borrowed("LagavulinMatriarch._isAwake"),
                                        FieldValue::Bool(true),
                                    );
                                }
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::AutomationPower => {
            if slot == HookSlot::AfterCardDrawn && creature == state.player.creature_handle {
                let left =
                    get_int_field(state, creature, power_idx, "AutomationPower._cardsLeft", 10)
                        - 1;
                if left <= 0 {
                    gold::gain_energy(state, content, Dec::from_i32(p.amount))?;
                    set_int_field(state, creature, power_idx, "AutomationPower._cardsLeft", 10);
                } else {
                    set_int_field(
                        state,
                        creature,
                        power_idx,
                        "AutomationPower._cardsLeft",
                        left,
                    );
                }
            }
            Ok(HookResult::default())
        }

        PowerId::BattlewornDummyTimeLimitPower => {
            if slot == HookSlot::AfterSideTurnEnd {
                if let HookArgs::SideTurn { side, participants } = args {
                    let alive = state.creature(creature).is_some_and(|c| c.is_alive());
                    if *side == owner_side && participants.contains(&creature) && alive {
                        if p.amount > 1 {
                            power::modify_amount_by_handle(
                                state,
                                content,
                                creature,
                                p.handle,
                                Dec::from_i32(-1),
                                None,
                                None,
                            )?;
                        } else {
                            set_bool_field(
                                state,
                                creature,
                                power_idx,
                                "BattlewornDummyTimeLimitPower._shouldEscape",
                                true,
                            );
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::BeaconOfHopePower => {
            if slot == HookSlot::AfterBlockGained {
                if let HookArgs::ModifyBlock { target, amount, .. } = args {
                    let sharing = get_bool_field(
                        state,
                        creature,
                        power_idx,
                        "BeaconOfHopePower._isSharingBlock",
                        false,
                    );
                    if !sharing
                        && *target == creature
                        && state.current_side == owner_side
                        && *amount >= Dec::ONE
                    {
                        let share = amount.checked_mul(Dec::HALF)?;
                        if share >= Dec::ONE {
                            set_bool_field(
                                state,
                                creature,
                                power_idx,
                                "BeaconOfHopePower._isSharingBlock",
                                true,
                            );
                            let teammates: SmallVec<[CreatureHandle; 4]> = state
                                .ally_handles
                                .iter()
                                .copied()
                                .filter(|&h| {
                                    h != creature
                                        && state
                                            .creature(h)
                                            .is_some_and(|c| c.is_player && c.is_alive())
                                })
                                .collect();
                            let res: Result<(), KernelError> = (|| {
                                for teammate in teammates {
                                    block::gain_block(
                                        state,
                                        content,
                                        teammate,
                                        share,
                                        ValueProp::UNPOWERED,
                                        None,
                                        None,
                                    )?;
                                }
                                Ok(())
                            })();
                            remove_private_field(
                                state,
                                creature,
                                p.handle,
                                "BeaconOfHopePower._isSharingBlock",
                            );
                            res?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::BiasedCognitionPower => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        power::apply(
                            state,
                            content,
                            PowerId::FocusPower,
                            creature,
                            Dec::from_i32(-p.amount),
                            Some(creature),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::BlackHolePower => match slot {
            HookSlot::AfterStarsGained => {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    if *original_count > Dec::ZERO && creature == state.player.creature_handle {
                        let targets = get_opponents_of(state, owner_side);
                        sts2_kernel::commands::damage::damage_targets(
                            state,
                            content,
                            &targets,
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED,
                            Some(creature),
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(ctx) = args {
                    if creature == state.player.creature_handle
                        && ctx.stars_spent > 0
                        && ctx.play_index + 1 == ctx.play_count
                    {
                        let targets = get_opponents_of(state, owner_side);
                        sts2_kernel::commands::damage::damage_targets(
                            state,
                            content,
                            &targets,
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED,
                            Some(creature),
                            None,
                            Some(*ctx),
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::BorrowedTimePower => match slot {
            HookSlot::TryModifyEnergyCostInCombat => {
                if let HookArgs::CardCost { cost, .. } = args {
                    if creature == state.player.creature_handle {
                        return Ok(HookResult {
                            dec_val: Some(cost.checked_add(Dec::from_i32(p.amount))?),
                            modified: true,
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::BurrowedPower => match slot {
            HookSlot::ShouldClearBlock => {
                if let HookArgs::Creature(target) = args {
                    return Ok(bool_result(*target != creature));
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterBlockBroken => {
                if let HookArgs::BlockBroken { target, .. } = args {
                    let is_tunneler =
                        state.creature(creature).and_then(|c| c.monster_id) == Some(MonsterId::Tunneler);
                    if *target == creature && is_tunneler {
                        if let Some(c) = state.creature_mut(creature) {
                            if let Some(m) = &mut c.monster {
                                m.private_fields.insert(
                                    Cow::Borrowed("Tunneler._isStunned"),
                                    FieldValue::Bool(true),
                                );
                            }
                        }
                        creature_cmd::stun(state, creature, Some("BITE_MOVE"))?;
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::BurstPower => match slot {
            HookSlot::ModifyCardPlayCount => {
                if let HookArgs::CardPlayCount {
                    card, play_count, ..
                } = args
                {
                    if creature == state.player.creature_handle
                        && is_card_type(state, content, *card, CardType::Skill)?
                    {
                        return Ok(int_result(*play_count + 1));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterModifyingCardPlayCount => {
                power::tick_down_duration_by_handle(state, content, creature, p.handle)?;
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::CacophonyPower => {
            if slot == HookSlot::AfterCardDrawn {
                let left =
                    get_int_field(state, creature, power_idx, "CacophonyPower._cardsLeft", 33) - 1;
                if left <= 0 {
                    let enemies = hittable_enemies(state, content)?;
                    let chosen = state
                        .run_rngs
                        .get_mut(RunRngType::CombatTargets)
                        .next_item(&enemies)
                        .copied();
                    set_int_field(state, creature, power_idx, "CacophonyPower._cardsLeft", 33);
                    if let Some(enemy) = chosen {
                        sts2_kernel::commands::damage::damage_targets(
                            state,
                            content,
                            &[enemy],
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED,
                            Some(creature),
                            None,
                            None,
                        )?;
                    }
                } else {
                    set_int_field(
                        state,
                        creature,
                        power_idx,
                        "CacophonyPower._cardsLeft",
                        left,
                    );
                }
            }
            Ok(HookResult::default())
        }

        PowerId::CalamityPower => match slot {
            HookSlot::BeforeCardPlayed => {
                if let HookArgs::CardPlay(ctx) = args {
                    if creature == state.player.creature_handle
                        && is_card_type(state, content, ctx.card, CardType::Attack)?
                    {
                        push_card_handle_field(
                            state,
                            creature,
                            power_idx,
                            "CalamityPower._eligibleCardPlays",
                            ctx.card.0 as i32,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(ctx) = args {
                    if remove_card_handle_field(
                        state,
                        creature,
                        power_idx,
                        "CalamityPower._eligibleCardPlays",
                        ctx.card.0 as i32,
                        true,
                    ) {
                        let pool = card_factory::character_combat_pool(state);
                        let generated = card_factory::get_for_combat_filtered(
                            state,
                            &pool,
                            p.amount.max(0) as usize,
                            RunRngType::CombatCardGeneration,
                            |id| sts2_types::generated_card_meta(id).card_type == CardType::Attack,
                        );
                        for cid in generated {
                            let _ = card_pile::add_generated_card_to_combat(
                                state,
                                content,
                                cid,
                                PileType::Hand,
                                CardPilePosition::Bottom,
                                0,
                                Some(creature),
                            )?;
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::CalcifyPower => {
            if slot == HookSlot::ModifyDamageAdditive {
                if let HookArgs::ModifyDamage { dealer, props, .. } = args {
                    let is_owners_osty = dealer.is_some_and(|dh| {
                        state.creature(dh).is_some_and(|c| {
                            c.monster_id == Some(MonsterId::Osty)
                                && c.pet_owner_player_index.is_some()
                                && creature == state.player.creature_handle
                        })
                    });
                    if is_owners_osty && props.is_powered_attack() {
                        return Ok(dec_result(Dec::from_i32(p.amount)));
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::CallOfTheVoidPower => {
            if slot == HookSlot::BeforeHandDraw && creature == state.player.creature_handle {
                let pool = card_factory::character_combat_pool(state);
                if !pool.is_empty() && p.amount > 0 {
                    let mut created: SmallVec<[CardHandle; 8]> = SmallVec::new();
                    for _ in 0..p.amount {
                        let picked = card_factory::get_distinct_for_combat(
                            state,
                            &pool,
                            1,
                            RunRngType::CombatCardGeneration,
                        );
                        if let Some(&cid) = picked.first() {
                            let ch = card_pile::create_card_in_combat(state, content, cid, 0)?;
                            if let Some(c) = state.card_mut(ch) {
                                c.local_keywords.insert(CardKeywords::ETHEREAL);
                            }
                            created.push(ch);
                        }
                    }
                    for ch in created {
                        card_pile::enter_combat(
                            state,
                            content,
                            ch,
                            PileType::Hand,
                            CardPilePosition::Bottom,
                            true,
                        )?;
                        hooks::after_card_generated(state, content, ch, Some(creature))?;
                        hooks::after_card_generated_for_combat(
                            state,
                            content,
                            ch,
                            Some(creature),
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::ChildOfTheStarsPower => {
            if slot == HookSlot::AfterStarsSpent {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    if *original_count > Dec::ZERO && creature == state.player.creature_handle {
                        let block_amt = Dec::from_i32(p.amount).checked_mul(*original_count)?;
                        block::gain_block(
                            state,
                            content,
                            creature,
                            block_amt,
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::ChoBarbsPower => {
            dispatch_temporary_thorns_power(content, state, creature, owner_side, p, slot, args)
        }

        PowerId::ClarityPower => match slot {
            HookSlot::ModifyHandDraw => {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    if creature == state.player.creature_handle {
                        return Ok(dec_result(original_count.checked_add(Dec::ONE)?));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnStart => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        power::tick_down_duration_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::ColossusPower => match slot {
            HookSlot::ModifyDamageMultiplicative => {
                if let HookArgs::ModifyDamage {
                    target,
                    dealer,
                    props,
                    ..
                } = args
                {
                    let dealer_vuln = dealer.is_some_and(|dh| {
                        state
                            .creature(dh)
                            .is_some_and(|c| c.powers.iter().any(|pw| pw.id == PowerId::VulnerablePower))
                    });
                    if *target == Some(creature) && props.is_powered_attack() && dealer_vuln {
                        return Ok(dec_result(Dec::HALF));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { side, .. } = args {
                    if *side == CombatSide::Enemy {
                        power::tick_down_duration_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::CompostPower => {
            if slot == HookSlotAlias::AfterDeath {
                if let HookArgs::AfterDeath { target, .. } = args {
                    let target_side = state.creature(*target).map(|c| c.side);
                    if target_side.is_some_and(|s| s != owner_side) && owner_side == CombatSide::Player {
                        let any_alive_player = state
                            .ally_handles
                            .iter()
                            .any(|&h| state.creature(h).is_some_and(|c| c.is_player && c.is_alive()));
                        if any_alive_player {
                            gold::gain_energy(state, content, Dec::from_i32(p.amount))?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::ConcoctPower => match slot {
            HookSlot::AfterDamageGiven => {
                if let HookArgs::DamageResult {
                    target,
                    dealer,
                    props,
                    unblocked_damage,
                    ..
                } = args
                {
                    if *dealer == Some(creature) && props.is_powered_attack() && *unblocked_damage > 0 {
                        power::apply(
                            state,
                            content,
                            PowerId::PoisonPower,
                            *target,
                            Dec::from_i32(p.amount),
                            Some(creature),
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { side, .. } = args {
                    if *side != owner_side {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::ConductorsBatonPower => match slot {
            HookSlot::ModifyDamageMultiplicative => {
                if let HookArgs::ModifyDamage { target, props, .. } = args {
                    if *target == Some(creature) && props.is_powered_attack() {
                        return Ok(dec_result(Dec::THREE_QUARTERS));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnStart => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::ConfusedPower => {
            if slot == HookSlot::AfterCardDrawn {
                if let HookArgs::CardDrawn { card: card_h, .. } = args {
                    if creature == state.player.creature_handle {
                        let (canon, is_x) = match state.card(*card_h) {
                            Some(c) => (
                                c.canonical_base_energy_cost,
                                content.card_meta(c.id).is_ok_and(|m| {
                                    m.flags.contains(sts2_types::CardStaticFlags::IS_X_ENERGY_COST)
                                }),
                            ),
                            None => return Ok(HookResult::default()),
                        };
                        if canon >= 0 {
                            let randomized = state
                                .run_rngs
                                .get_mut(RunRngType::CombatEnergyCosts)
                                .next_int(4)?;
                            if !is_x {
                                if let Some(c) = state.card_mut(*card_h) {
                                    c.temp_cost_override_this_combat =
                                        sts2_kernel::OptI16::some_clamped(randomized);
                                }
                                if let Some(ext) = state.card_ext_mut(*card_h) {
                                    ext.push_local_energy_modifier(
                                        sts2_kernel::LocalEnergyModifier {
                                            kind: sts2_types::LocalEnergyModifierKind::SetThisCombat,
                                            amount: randomized,
                                            is_reduce_only: false,
                                        },
                                    );
                                }
                            }
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::ConquerorPower => match slot {
            HookSlot::ModifyDamageMultiplicative => {
                if let HookArgs::ModifyDamage {
                    target,
                    props,
                    card_source,
                    ..
                } = args
                {
                    let is_sovereign_blade = card_source
                        .and_then(|h| state.card(h))
                        .is_some_and(|c| c.id == CardId::SovereignBlade);
                    if *target == Some(creature) && props.is_powered_attack() && is_sovereign_blade
                    {
                        return Ok(dec_result(Dec::TWO));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        power::modify_amount_by_handle(
                            state,
                            content,
                            creature,
                            p.handle,
                            Dec::from_i32(-1),
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::ConstrictPower => match slot {
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        sts2_kernel::commands::damage::damage_targets(
                            state,
                            content,
                            &[creature],
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED,
                            Some(creature),
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterDeath => {
                if let HookArgs::AfterDeath { target, .. } = args {
                    if Some(*target) == p.applier_handle {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::ConsumingShadowPower => {
            if slot == HookSlot::AfterSideTurnEnd {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature)
                        && creature == state.player.creature_handle
                        && !state.player.orbs.is_empty()
                    {
                        for _ in 0..p.amount {
                            orb::evoke_last(state, content, true)?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::CoolantPower => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) && creature == state.player.creature_handle {
                        let mut distinct: SmallVec<[OrbId; 5]> = SmallVec::new();
                        for o in &state.player.orbs {
                            if !distinct.contains(&o.id) {
                                distinct.push(o.id);
                            }
                        }
                        let blk = Dec::from_i32(distinct.len() as i32 * p.amount);
                        block::gain_block(
                            state,
                            content,
                            creature,
                            blk,
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::CoordinatePower | PowerId::FlexPotionPower => dispatch_temporary_stat_power(
            content,
            state,
            creature,
            p,
            slot,
            args,
            PowerId::StrengthPower,
            1,
        ),

        PowerId::CrushUnderPower
        | PowerId::DarkShacklesPower
        | PowerId::DyingStarPower
        | PowerId::EnfeeblingTouchPower
        | PowerId::ManglePower
        | PowerId::MonarchsGazeStrengthDownPower => dispatch_temporary_stat_power(
            content,
            state,
            creature,
            p,
            slot,
            args,
            PowerId::StrengthPower,
            -1,
        ),

        PowerId::CorrosiveWavePower => match slot {
            HookSlot::AfterCardDrawn => {
                if creature == state.player.creature_handle {
                    let enemies = hittable_enemies(state, content)?;
                    for enemy in enemies {
                        power::apply(
                            state,
                            content,
                            PowerId::PoisonPower,
                            enemy,
                            Dec::from_i32(p.amount),
                            Some(creature),
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::CountdownPower => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let enemies = hittable_enemies(state, content)?;
                        let chosen = state
                            .run_rngs
                            .get_mut(RunRngType::CombatTargets)
                            .next_item(&enemies)
                            .copied();
                        if let Some(enemy) = chosen {
                            power::apply(
                                state,
                                content,
                                PowerId::DoomPower,
                                enemy,
                                Dec::from_i32(p.amount),
                                Some(creature),
                                None,
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::CoveredPower => match slot {
            HookSlot::ModifyDamageMultiplicative => {
                if let HookArgs::ModifyDamage { target, props, .. } = args {
                    if *target == Some(creature) && props.is_powered_attack() {
                        return Ok(dec_result(Dec::ZERO));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { side, .. } = args {
                    if *side == CombatSide::Enemy {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterDeath => {
                if let HookArgs::AfterDeath { target, .. } = args {
                    if Some(*target) == p.applier_handle {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::CoveringFirePower => {
            if slot == HookSlot::AfterArrowShot {
                if let HookArgs::ArrowShot { card_source, .. } = args {
                    if card_source.is_some() && creature == state.player.creature_handle {
                        let allies: SmallVec<[CreatureHandle; 4]> = state
                            .ally_handles
                            .iter()
                            .copied()
                            .filter(|&h| {
                                h != creature
                                    && state
                                        .creature(h)
                                        .is_some_and(|c| c.is_alive() && c.is_player)
                            })
                            .collect();
                        for ally in allies {
                            block::gain_block(
                                state,
                                content,
                                ally,
                                Dec::from_i32(p.amount),
                                ValueProp::UNPOWERED,
                                None,
                                None,
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::CrabRagePower => {
            if slot == HookSlot::AfterDeath {
                if let HookArgs::AfterDeath { target, .. } = args {
                    let same_side = state.creature(*target).is_some_and(|c| c.side == owner_side);
                    let owner_alive = state.creature(creature).is_some_and(|c| c.is_alive());
                    if *target != creature && same_side && owner_alive {
                        power::apply(
                            state,
                            content,
                            PowerId::StrengthPower,
                            creature,
                            Dec::from_i32(6),
                            Some(creature),
                            None,
                        )?;
                        block::gain_block(
                            state,
                            content,
                            creature,
                            Dec::from_i32(99),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::CreativeAiPower => {
            if slot == HookSlot::BeforeHandDraw && creature == state.player.creature_handle {
                let pool = card_factory::character_combat_pool(state);
                for _ in 0..p.amount {
                    let generated = card_factory::get_distinct_for_combat_filtered(
                        state,
                        &pool,
                        1,
                        RunRngType::CombatCardGeneration,
                        |id| sts2_types::generated_card_meta(id).card_type == CardType::Power,
                    );
                    if let Some(&cid) = generated.first() {
                        let _ = card_pile::add_generated_card_to_combat(
                            state,
                            content,
                            cid,
                            PileType::Hand,
                            CardPilePosition::Bottom,
                            0,
                            Some(creature),
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::CrimsonMantlePower => {
            if slot == HookSlot::AfterPlayerTurnStart && creature == state.player.creature_handle {
                let self_dmg = get_decimal_field(
                    state,
                    creature,
                    power_idx,
                    "CrimsonMantlePower._selfDamage",
                    Dec::ZERO,
                );
                sts2_kernel::commands::damage::damage_targets(
                    state,
                    content,
                    &[creature],
                    self_dmg,
                    ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED,
                    Some(creature),
                    None,
                    None,
                )?;
                block::gain_block(
                    state,
                    content,
                    creature,
                    Dec::from_i32(p.amount),
                    ValueProp::UNPOWERED,
                    None,
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        PowerId::CuriousPower => {
            if slot == HookSlot::TryModifyEnergyCostInCombat {
                if let HookArgs::CardCost { card, cost } = args {
                    if creature == state.player.creature_handle
                        && is_card_type(state, content, *card, CardType::Power)?
                        && *cost > Dec::ZERO
                    {
                        let next = cost.checked_sub(Dec::from_i32(p.amount))?.max(Dec::ZERO);
                        return Ok(HookResult {
                            dec_val: Some(next),
                            modified: true,
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::CurlUpPower => match slot {
            HookSlot::AfterDamageReceived => {
                if let HookArgs::DamageResult {
                    target,
                    props,
                    card_source,
                    ..
                } = args
                {
                    if *target == creature && props.is_powered_attack() {
                        if let Some(cs) = *card_source {
                            let cur = get_optional_card_field(
                                state,
                                creature,
                                power_idx,
                                "CurlUpPower._triggeringCard",
                            );
                            if cur.is_none() || cur == Some(cs) {
                                set_optional_card_field(
                                    state,
                                    creature,
                                    power_idx,
                                    "CurlUpPower._triggeringCard",
                                    Some(cs),
                                );
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(ctx) = args {
                    let trig = get_optional_card_field(
                        state,
                        creature,
                        power_idx,
                        "CurlUpPower._triggeringCard",
                    );
                    if trig == Some(ctx.card) {
                        block::gain_block(
                            state,
                            content,
                            creature,
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::DampenPower => match slot {
            HookSlot::AfterApplied => {
                if creature == state.player.creature_handle {
                    let upgraded: Vec<(CardHandle, i32)> = state
                        .player
                        .hand
                        .iter()
                        .chain(state.player.draw.iter())
                        .chain(state.player.discard.iter())
                        .chain(state.player.exhaust.iter())
                        .chain(state.player.play.iter())
                        .filter_map(|&ch| {
                            state
                                .card(ch)
                                .filter(|c| !c.removed_from_state && c.upgrade_level > 0)
                                .map(|c| (ch, c.upgrade_level))
                        })
                        .collect();
                    let ext_idx = ensure_power_ext(state, creature, power_idx);
                    let map_entries: Vec<(i32, i32)> =
                        upgraded.iter().map(|&(ch, lvl)| (ch.0 as i32, lvl)).collect();
                    state.power_extensions[ext_idx].private_fields.insert(
                        Cow::Borrowed("DampenPower._downgraded"),
                        FieldValue::CardIntMap(map_entries),
                    );
                    for (ch, _) in upgraded {
                        card::downgrade(state, content, ch)?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterDeath => {
                if let HookArgs::AfterDeath { target, .. } = args {
                    if remove_creature_handle_field(
                        state,
                        creature,
                        power_idx,
                        "DampenPower._casters",
                        target.0 as i32,
                    ) {
                        let remaining = get_creature_handle_array_len(
                            state,
                            creature,
                            power_idx,
                            "DampenPower._casters",
                        );
                        if remaining == 0 {
                            let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::DanseMacabrePower => {
            if slot == HookSlot::BeforeCardPlayed {
                if let HookArgs::CardPlay(ctx) = args {
                    if creature == state.player.creature_handle {
                        let is_x = state
                            .card(ctx.card)
                            .and_then(|c| content.card_meta(c.id).ok())
                            .is_some_and(|m| {
                                m.flags.contains(sts2_types::CardStaticFlags::IS_X_ENERGY_COST)
                            });
                        let resolved_cost = if is_x {
                            ctx.energy_value
                        } else {
                            ordering::resolve_energy_cost(state, content, ctx.card)?
                        };
                        if resolved_cost >= 2 {
                            block::gain_block(
                                state,
                                content,
                                creature,
                                Dec::from_i32(p.amount),
                                ValueProp::UNPOWERED,
                                None,
                                None,
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::DebilitatePower => match slot {
            HookSlot::ModifyVulnerableMultiplier => {
                if let HookArgs::ModifyDamage {
                    target,
                    amount,
                    props,
                    ..
                } = args
                {
                    if let Some(t) = *target {
                        let next = debilitate_modify_vulnerable_multiplier(
                            creature, t, *amount, *props,
                        )?;
                        return Ok(dec_result(next));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ModifyWeakMultiplier => {
                if let HookArgs::ModifyDamage {
                    dealer,
                    amount,
                    props,
                    ..
                } = args
                {
                    if let Some(d) = *dealer {
                        let next =
                            debilitate_modify_weak_multiplier(creature, d, *amount, *props)?;
                        return Ok(dec_result(next));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        power::modify_amount_by_handle(
                            state,
                            content,
                            creature,
                            p.handle,
                            Dec::from_i32(-1),
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::DemesnePower => match slot {
            HookSlot::ModifyHandDraw => {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    if creature == state.player.creature_handle {
                        return Ok(dec_result(
                            original_count.checked_add(Dec::from_i32(p.amount))?,
                        ));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ModifyMaxEnergy => {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    if creature == state.player.creature_handle {
                        return Ok(dec_result(
                            original_count.checked_add(Dec::from_i32(p.amount))?,
                        ));
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::DemisePower => {
            if slot == HookSlot::AfterSideTurnEnd {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        sts2_kernel::commands::damage::damage_targets(
                            state,
                            content,
                            &[creature],
                            Dec::from_i32(p.amount),
                            ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED,
                            None,
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::DevourLifePower => {
            if slot == HookSlot::AfterCardPlayed {
                if let HookArgs::CardPlay(ctx) = args {
                    if creature == state.player.creature_handle
                        && state.card(ctx.card).is_some_and(|c| c.id == CardId::Soul)
                    {
                        let _ = osty::summon(state, content, Dec::from_i32(p.amount))?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::DieForYouPower => match slot {
            HookSlot::ModifyUnblockedDamageTarget => {
                if let HookArgs::ModifyUnblockedTarget { target, props, .. } = args {
                    let owner_is_alive = state.creature(creature).is_some_and(|c| c.is_alive());
                    let is_pet_owner = state
                        .creature(creature)
                        .and_then(|c| c.pet_owner_player_index)
                        .is_some()
                        && *target == state.player.creature_handle;
                    if is_pet_owner && owner_is_alive && props.is_powered_attack() {
                        return Ok(HookResult {
                            creature_val: Some(creature),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldAllowHitting => {
                if let HookArgs::Creature(target) = args {
                    let alive = state.creature(*target).is_some_and(|c| c.is_alive());
                    return Ok(bool_result(alive));
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldCreatureBeRemovedFromCombatAfterDeath => {
                if let HookArgs::Creature(target) = args {
                    return Ok(bool_result(*target != creature));
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::DisintegrationPower => {
            if slot == HookSlot::AfterSideTurnEndLate {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        sts2_kernel::commands::damage::damage_targets(
                            state,
                            content,
                            &[creature],
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED,
                            Some(creature),
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::DislikeYouPower => match slot {
            HookSlot::ModifyDamageMultiplicative => {
                if let HookArgs::ModifyDamage { target, props, .. } = args {
                    if *target == Some(creature)
                        && props.is_powered_attack()
                        && props.contains(ValueProp::FROM_ARROW)
                    {
                        let mult = Dec::ONE.checked_add(Dec::from_parts(p.amount as i64, 2)?)?;
                        return Ok(dec_result(mult));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { side, .. } = args {
                    if *side == CombatSide::Player {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::DoubleDamagePower => match slot {
            HookSlot::ModifyDamageMultiplicative => {
                if let HookArgs::ModifyDamage {
                    dealer,
                    props,
                    card_source,
                    ..
                } = args
                {
                    if *dealer == Some(creature)
                        && props.is_powered_attack()
                        && card_source.is_some()
                    {
                        return Ok(dec_result(Dec::TWO));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        power::modify_amount_by_handle(
                            state,
                            content,
                            creature,
                            p.handle,
                            Dec::from_i32(-1),
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::DuplicationPower => match slot {
            HookSlot::ModifyCardPlayCount => {
                if let HookArgs::CardPlayCount { play_count, .. } = args {
                    if creature == state.player.creature_handle {
                        return Ok(int_result(*play_count + 1));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterModifyingCardPlayCount => {
                power::tick_down_duration_by_handle(state, content, creature, p.handle)?;
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::EchoFormPower => {
            if slot == HookSlot::ModifyCardPlayCount {
                if let HookArgs::CardPlayCount { play_count, .. } = args {
                    if creature == state.player.creature_handle
                        && state.player.counters.first_in_series_card_plays_started_this_turn
                            < p.amount
                    {
                        return Ok(int_result(*play_count + 1));
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::EcstasyPower => {
            if slot == HookSlot::AfterPlayerTurnStart && creature == state.player.creature_handle {
                let mut candidates: Vec<CardHandle> = state
                    .player
                    .hand
                    .iter()
                    .copied()
                    .filter(|&h| state.card(h).is_some_and(|c| c.id == CardId::Sadness))
                    .collect();
                if !candidates.is_empty() {
                    let mut rng = *state.run_rngs.get(RunRngType::CombatCardSelection);
                    ordering::stable_shuffle_cards(state, content, &mut candidates, &mut rng)?;
                    *state.run_rngs.get_mut(RunRngType::CombatCardSelection) = rng;
                    for ch in candidates.into_iter().take(p.amount.max(0) as usize) {
                        let _ = card::transform(state, content, ch, CardId::Joy)?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::EncorePower => match slot {
            HookSlot::BeforeCardPlayed => {
                if let HookArgs::CardPlay(ctx) = args {
                    if creature == state.player.creature_handle {
                        push_card_handle_field(
                            state,
                            creature,
                            power_idx,
                            "EncorePower._cardsBeingPlayed",
                            ctx.card.0 as i32,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldPlay => {
                if creature == state.player.creature_handle {
                    let played = get_int_field(
                        state,
                        creature,
                        power_idx,
                        "EncorePower._numberOfCardsPlayedSinceApplied",
                        0,
                    );
                    let in_flight = get_card_handle_array_len(
                        state,
                        creature,
                        power_idx,
                        "EncorePower._cardsBeingPlayed",
                    ) as i32;
                    return Ok(bool_result(played + in_flight < p.amount));
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(ctx) = args {
                    if creature == state.player.creature_handle
                        && remove_card_handle_field(
                            state,
                            creature,
                            power_idx,
                            "EncorePower._cardsBeingPlayed",
                            ctx.card.0 as i32,
                            false,
                        )
                    {
                        let played = get_int_field(
                            state,
                            creature,
                            power_idx,
                            "EncorePower._numberOfCardsPlayedSinceApplied",
                            0,
                        );
                        set_int_field(
                            state,
                            creature,
                            power_idx,
                            "EncorePower._numberOfCardsPlayedSinceApplied",
                            played + 1,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::EnnuiPower => match slot {
            HookSlot::BeforeHandDraw => {
                if creature == state.player.creature_handle {
                    let sadness =
                        get_int_field(state, creature, power_idx, "EnnuiPower._sadnessAmount", 0);
                    for _ in 0..sadness {
                        let _ = card_pile::add_generated_card_to_combat(
                            state,
                            content,
                            CardId::Sadness,
                            PileType::Hand,
                            CardPilePosition::Bottom,
                            0,
                            Some(creature),
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ModifyHandDraw => {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    if creature == state.player.creature_handle {
                        return Ok(dec_result(
                            original_count.checked_add(Dec::from_i32(p.amount))?,
                        ));
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::EnragePower => {
            if slot == HookSlot::AfterCardPlayed {
                if let HookArgs::CardPlay(ctx) = args {
                    if is_card_type(state, content, ctx.card, CardType::Skill)? {
                        power::apply(
                            state,
                            content,
                            PowerId::StrengthPower,
                            creature,
                            Dec::from_i32(p.amount),
                            Some(creature),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::EnsemblePower => match slot {
            HookSlot::TryModifyEnergyCostInCombatLate => {
                if let HookArgs::CardCost { card, .. } = args {
                    let in_hand_or_play = state
                        .card(*card)
                        .is_some_and(|c| matches!(c.pile, PileType::Hand | PileType::Play));
                    if creature != state.player.creature_handle && in_hand_or_play {
                        return Ok(HookResult {
                            dec_val: Some(Dec::ZERO),
                            modified: true,
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(cp) = args {
                    if creature != state.player.creature_handle
                        && cp.play_index + 1 == cp.play_count
                    {
                        power::modify_amount_by_handle(
                            state,
                            content,
                            creature,
                            p.handle,
                            Dec::from_i32(-1),
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::EntropyPower => {
            if slot == HookSlot::AfterPlayerTurnStart && creature == state.player.creature_handle {
                let count = p.amount.max(0) as usize;
                let candidates: SmallVec<[CardHandle; 16]> =
                    state.player.hand.iter().copied().collect();
                let mut empty_queue = SmallVec::new();
                match card_select::select_cards(
                    state,
                    &candidates,
                    count,
                    count,
                    ChoiceTiming::PlayerTurnStart,
                    "POWER.ENTROPY_POWER",
                    PileType::Hand,
                    false,
                    &mut empty_queue,
                )? {
                    SelectResult::Selected(sel) => {
                        for &ch in &sel {
                            let _ = card::transform_to_random(
                                state,
                                content,
                                ch,
                                RunRngType::CombatCardGeneration,
                            )?;
                        }
                    }
                    SelectResult::NeedsChoice(req) => {
                        return Ok(HookResult {
                            needs_choice: Some(req),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::EscapeArtistPower => {
            if slot == HookSlot::AfterSideTurnEnd {
                if let HookArgs::SideTurn { side, participants } = args {
                    if *side == owner_side && participants.contains(&creature) && p.amount > 1 {
                        power::modify_amount_by_handle(
                            state,
                            content,
                            creature,
                            p.handle,
                            Dec::from_i32(-1),
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::ExplosiveArrowsPower => {
            if slot == HookSlot::AfterArrowImpaled && creature == state.player.creature_handle {
                let enemies = hittable_enemies(state, content)?;
                if !enemies.is_empty() {
                    sts2_kernel::commands::damage::damage_targets(
                        state,
                        content,
                        &enemies,
                        Dec::from_i32(p.amount),
                        ValueProp::UNPOWERED,
                        Some(creature),
                        None,
                        None,
                    )?;
                }
            }
            Ok(HookResult::default())
        }

        PowerId::ExplosivePower => {
            if slot == HookSlot::AfterDeath {
                if let HookArgs::AfterDeath {
                    target,
                    was_removal_prevented,
                    ..
                } = args
                {
                    if *target == creature && !*was_removal_prevented {
                        let targets: SmallVec<[CreatureHandle; 4]> = state
                            .ally_handles
                            .iter()
                            .copied()
                            .filter(|&h| {
                                state.creature(h).is_some_and(|c| {
                                    c.is_alive() && (c.is_player || (!c.is_monster() && !c.is_pet()))
                                })
                            })
                            .collect();
                        sts2_kernel::commands::damage::damage_targets(
                            state,
                            content,
                            &targets,
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED | ValueProp::SKIP_HURT_ANIM,
                            None,
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::ExtraTurnPower => match slot {
            HookSlot::ShouldTakeExtraTurn => {
                Ok(bool_result(p.amount > 0 && creature == state.player.creature_handle))
            }
            HookSlot::AfterTakingExtraTurn => {
                if creature == state.player.creature_handle {
                    power::modify_amount_by_handle(
                        state,
                        content,
                        creature,
                        p.handle,
                        Dec::from_i32(-1),
                        None,
                        None,
                    )?;
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::FastenPower => {
            if slot == HookSlot::ModifyBlockAdditive {
                if let HookArgs::ModifyBlock {
                    target,
                    props,
                    card_source,
                    ..
                } = args
                {
                    let card_ok = match *card_source {
                        None => true,
                        Some(ch) => is_card_with_tag(state, content, Some(ch), CardTags::DEFEND)?,
                    };
                    if *target == creature && card_ok && !props.contains(ValueProp::UNPOWERED) {
                        return Ok(dec_result(Dec::from_i32(p.amount)));
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::FeedingFrenzyPower => dispatch_temporary_stat_power(
            content,
            state,
            creature,
            p,
            slot,
            args,
            PowerId::StrengthPower,
            1,
        ),

        PowerId::FeralPower => match slot {
            HookSlot::AfterApplied => {
                feral_after_applied(state, creature, p.handle);
                Ok(HookResult::default())
            }
            HookSlot::ModifyCardPlayResultLocation => {
                if let HookArgs::CardPlayResultLocation {
                    card,
                    energy_value,
                    pile,
                    position,
                    ..
                } = args
                {
                    let next = feral_modify_card_play_result_location(
                        state,
                        content,
                        creature,
                        p.handle,
                        *card,
                        *energy_value,
                        (*pile, *position),
                    )?;
                    if next != (*pile, *position) {
                        return Ok(HookResult {
                            location_val: Some(next),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterModifyingCardPlayResultLocation => {
                let played = get_int_field(
                    state,
                    creature,
                    power_idx,
                    "FeralPower._zeroCostAttacksPlayed",
                    0,
                );
                set_int_field(
                    state,
                    creature,
                    power_idx,
                    "FeralPower._zeroCostAttacksPlayed",
                    played + 1,
                );
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnStart => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        set_int_field(
                            state,
                            creature,
                            power_idx,
                            "FeralPower._zeroCostAttacksPlayed",
                            0,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::FlankingPower => match slot {
            HookSlot::ModifyDamageMultiplicative => {
                if let HookArgs::ModifyDamage {
                    target,
                    dealer,
                    props,
                    ..
                } = args
                {
                    if *target == Some(creature)
                        && props.is_powered_attack()
                        && *dealer != p.applier_handle
                    {
                        return Ok(dec_result(Dec::from_i32(p.amount)));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::FlutterPower => match slot {
            HookSlot::ModifyDamageMultiplicative => {
                if let HookArgs::ModifyDamage { target, props, .. } = args {
                    if *target == Some(creature) && props.is_powered_attack() {
                        return Ok(dec_result(Dec::HALF));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterDamageReceived => {
                if let HookArgs::DamageResult {
                    target,
                    props,
                    unblocked_damage,
                    ..
                } = args
                {
                    if *target == creature && *unblocked_damage > 0 && props.is_powered_attack() {
                        power::modify_amount_by_handle(
                            state,
                            content,
                            creature,
                            p.handle,
                            Dec::from_i32(-1),
                            None,
                            None,
                        )?;
                        let still_present = state
                            .creature(creature)
                            .is_some_and(|c| c.powers.iter().any(|pw| pw.handle == p.handle));
                        if !still_present {
                            if let Some(c) = state.creature_mut(creature) {
                                if let Some(m) = &mut c.monster {
                                    if m.monster_id == MonsterId::ThievingHopper {
                                        m.private_fields.insert(
                                            Cow::Borrowed("ThievingHopper._isHovering"),
                                            FieldValue::Bool(false),
                                        );
                                    }
                                }
                            }
                            creature_cmd::stun(state, creature, None)?;
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::FocusedStrikePower | PowerId::HotfixPower => dispatch_temporary_stat_power(
            content,
            state,
            creature,
            p,
            slot,
            args,
            PowerId::FocusPower,
            1,
        ),

        PowerId::HyperbeamFocusDownPower => dispatch_temporary_stat_power(
            content,
            state,
            creature,
            p,
            slot,
            args,
            PowerId::FocusPower,
            -1,
        ),

        PowerId::ForbiddenGrimoirePower => Ok(HookResult::default()),

        PowerId::ForegoneConclusionPower => {
            if slot == HookSlot::BeforeHandDraw && creature == state.player.creature_handle {
                card_pile::shuffle_if_necessary(state, content)?;
                let count = p.amount.max(0) as usize;
                let mut candidates: SmallVec<[CardHandle; 16]> =
                    state.player.draw.iter().copied().collect();
                if candidates.len() > count {
                    candidates.sort_by_key(|&ch| {
                        state
                            .card(ch)
                            .and_then(|ci| {
                                content
                                    .card_meta(ci.id)
                                    .ok()
                                    .map(|m| (csharp_card_rarity_rank(m.rarity), ci.id.model_entry()))
                            })
                            .unwrap_or((u8::MAX, ""))
                    });
                }
                let mut empty_queue = SmallVec::new();
                match card_select::select_cards(
                    state,
                    &candidates,
                    count,
                    count,
                    ChoiceTiming::PlayerTurnStart,
                    "POWER.FOREGONE_CONCLUSION_POWER",
                    PileType::Draw,
                    false,
                    &mut empty_queue,
                )? {
                    SelectResult::Selected(sel) => {
                        for &ch in &sel {
                            card_pile::add_sync(
                                state,
                                ch,
                                PileType::Hand,
                                CardPilePosition::Bottom,
                            );
                        }
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                    SelectResult::NeedsChoice(req) => {
                        return Ok(HookResult {
                            needs_choice: Some(req),
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::FreeAttackPower => dispatch_free_card_power(
            content,
            state,
            creature,
            p.handle,
            slot,
            args,
            Some(CardType::Attack),
        ),
        PowerId::FreePowerPower => dispatch_free_card_power(
            content,
            state,
            creature,
            p.handle,
            slot,
            args,
            Some(CardType::Power),
        ),
        PowerId::FreeSkillPower => dispatch_free_card_power(
            content,
            state,
            creature,
            p.handle,
            slot,
            args,
            Some(CardType::Skill),
        ),

        PowerId::FriendshipPower => {
            if slot == HookSlot::ModifyMaxEnergy {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    if creature == state.player.creature_handle {
                        return Ok(dec_result(
                            original_count.checked_add(Dec::from_i32(p.amount))?,
                        ));
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::FurnacePower => {
            if slot == HookSlot::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) && creature == state.player.creature_handle {
                        forge::forge(state, content, Dec::from_i32(p.amount))?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::GalvanicPower => match slot {
            HookSlot::BeforeCombatStart => {
                let to_afflict: Vec<CardHandle> = state
                    .cards
                    .iter()
                    .filter(|c| {
                        !c.removed_from_state
                            && !matches!(c.pile, PileType::None | PileType::Deck)
                            && c.affliction.is_none()
                            && content
                                .card_meta(c.id)
                                .is_ok_and(|m| m.card_type == CardType::Power)
                    })
                    .map(|c| c.handle)
                    .collect();
                for ch in to_afflict {
                    let _ = card::afflict(state, ch, AfflictionId::Galvanized, p.amount);
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardEnteredCombat => {
                if let HookArgs::CardOnly(card_h) = args {
                    let needs_afflict = state.card(*card_h).is_some_and(|c| {
                        c.affliction.is_none()
                            && content
                                .card_meta(c.id)
                                .is_ok_and(|m| m.card_type == CardType::Power)
                    });
                    if needs_afflict {
                        let _ = card::afflict(state, *card_h, AfflictionId::Galvanized, p.amount);
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(ctx) = args {
                    if state.card(ctx.card).is_some_and(|c| {
                        c.affliction
                            .is_some_and(|a| a.id == AfflictionId::Galvanized)
                    }) {
                        let card_owner = state.player.creature_handle;
                        sts2_kernel::commands::damage::damage_targets(
                            state,
                            content,
                            &[card_owner],
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED | ValueProp::MOVE,
                            None,
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::GenesisPower => {
            if slot == HookSlot::AfterEnergyReset && creature == state.player.creature_handle {
                gold::gain_stars(state, content, Dec::from_i32(p.amount))?;
            }
            Ok(HookResult::default())
        }

        PowerId::GigantificationPower => match slot {
            HookSlot::BeforeAttack => {
                if let HookArgs::Attack { info, .. } = args {
                    if info.attacker == creature && info.damage_props.is_powered_attack() {
                        if let Some(ch) = info.card_source {
                            if is_card_type(state, content, ch, CardType::Attack)? {
                                let ext_idx = ensure_power_ext(state, creature, power_idx);
                                if state.power_extensions[ext_idx].attack_command_id.is_none() {
                                    state.power_extensions[ext_idx].attack_command_id =
                                        Some(info.attack_id);
                                    state.power_extensions[ext_idx].amount_when_attack_started =
                                        ch.0 as i32;
                                }
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ModifyDamageMultiplicative => {
                if let HookArgs::ModifyDamage {
                    dealer,
                    props,
                    card_source,
                    ..
                } = args
                {
                    let claimed_card = p.ext_index.and_then(|e| {
                        let ext = &state.power_extensions[e as usize];
                        ext.attack_command_id
                            .map(|_| CardHandle(ext.amount_when_attack_started as u32))
                    });
                    if *dealer == Some(creature)
                        && props.is_powered_attack()
                        && claimed_card.is_some()
                        && *card_source == claimed_card
                    {
                        return Ok(dec_result(Dec::from_i32(3)));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterAttack => {
                if let HookArgs::Attack { info, .. } = args {
                    let claimed = p.ext_index.is_some_and(|e| {
                        state.power_extensions[e as usize].attack_command_id == Some(info.attack_id)
                    });
                    if claimed {
                        power::tick_down_duration_by_handle(state, content, creature, p.handle)?;
                        if let Some(ext_idx) = state
                            .creature(creature)
                            .and_then(|c| c.powers.iter().find(|pw| pw.handle == p.handle))
                            .and_then(|pw| pw.ext_index)
                        {
                            let ext = &mut state.power_extensions[ext_idx as usize];
                            ext.attack_command_id = None;
                            ext.amount_when_attack_started = 0;
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::GodByrdPower => {
            if slot == HookSlot::AfterPlayerTurnStart
                && creature == state.player.creature_handle
                && p.amount_on_turn_start != 0
            {
                let enemies = hittable_enemies(state, content)?;
                sts2_kernel::commands::damage::damage_targets(
                    state,
                    content,
                    &enemies,
                    Dec::from_i32(p.amount),
                    ValueProp::UNPOWERED,
                    Some(creature),
                    None,
                    None,
                )?;
                let _ = power::remove_by_handle(state, content, creature, p.handle)?;
            }
            Ok(HookResult::default())
        }

        PowerId::GravityPower => match slot {
            HookSlot::BeforeCardPlayed => {
                if let HookArgs::CardPlay(ctx) = args {
                    if creature == state.player.creature_handle {
                        push_card_int_field(
                            state,
                            creature,
                            power_idx,
                            "GravityPower._amountsForPlayedCards",
                            ctx.card.0 as i32,
                            p.amount,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(ctx) = args {
                    if let Some(amount) = take_card_int_field(
                        state,
                        creature,
                        power_idx,
                        "GravityPower._amountsForPlayedCards",
                        ctx.card.0 as i32,
                    ) {
                        if amount > 0 {
                            let enemies = hittable_enemies(state, content)?;
                            if !enemies.is_empty() {
                                sts2_kernel::commands::damage::damage_targets(
                                    state,
                                    content,
                                    &enemies,
                                    Dec::from_i32(amount),
                                    ValueProp::UNPOWERED,
                                    Some(creature),
                                    None,
                                    None,
                                )?;
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::GuardedPower => match slot {
            HookSlot::AfterDeath => {
                if let HookArgs::AfterDeath {
                    target,
                    was_removal_prevented,
                    ..
                } = args
                {
                    if !*was_removal_prevented && Some(*target) == p.applier_handle {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ModifyDamageMultiplicative => {
                if let HookArgs::ModifyDamage { target, props, .. } = args {
                    if *target == Some(creature) && props.is_powered_attack() {
                        return Ok(dec_result(Dec::HALF));
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::HailstormPower => {
            if slot == HookSlot::BeforeSideTurnEnd {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) && creature == state.player.creature_handle {
                        let has_frost = state.player.orbs.iter().any(|o| o.id == OrbId::FrostOrb);
                        if has_frost {
                            let enemies = hittable_enemies(state, content)?;
                            sts2_kernel::commands::damage::damage_targets(
                                state,
                                content,
                                &enemies,
                                Dec::from_i32(p.amount),
                                ValueProp::UNPOWERED,
                                Some(creature),
                                None,
                                None,
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::HammerTimePower => {
            // Single-player combat has no other Player teammates to propagate Forge to.
            if slot == HookSlot::AfterForge {
                if let HookArgs::Forge { .. } = args {}
            }
            Ok(HookResult::default())
        }

        PowerId::HangPower => {
            if slot == HookSlot::ModifyDamageMultiplicative {
                if let HookArgs::ModifyDamage {
                    target,
                    card_source,
                    ..
                } = args
                {
                    let is_hang = card_source
                        .and_then(|h| state.card(h))
                        .is_some_and(|c| c.id == CardId::Hang);
                    if *target == Some(creature) && is_hang {
                        return Ok(dec_result(Dec::from_i32(p.amount)));
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::HardToKillPower => {
            if slot == HookSlot::ModifyHpLostBeforeOstyLate {
                if let HookArgs::ModifyHpLost { target, amount, .. } = args {
                    if *target == creature && !amount.is_zero() {
                        return Ok(dec_result((*amount).min(Dec::from_i32(p.amount))));
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::HatchPower => {
            if slot == HookSlot::AfterSideTurnEnd {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        power::modify_amount_by_handle(
                            state,
                            content,
                            creature,
                            p.handle,
                            Dec::from_i32(-1),
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::HauntPower => {
            if slot == HookSlot::AfterCardPlayed {
                if let HookArgs::CardPlay(ctx) = args {
                    if creature == state.player.creature_handle
                        && state.card(ctx.card).is_some_and(|c| c.id == CardId::Soul)
                    {
                        let enemies = hittable_enemies(state, content)?;
                        if let Some(&target) = state
                            .run_rngs
                            .get_mut(RunRngType::CombatTargets)
                            .next_item(&enemies)
                        {
                            sts2_kernel::commands::damage::damage_targets(
                                state,
                                content,
                                &[target],
                                Dec::from_i32(p.amount),
                                ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED,
                                None,
                                None,
                                None,
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::HeistPower => Ok(HookResult::default()),

        PowerId::HelloWorldPower => {
            if slot == HookSlot::BeforeHandDraw
                && creature == state.player.creature_handle
                && p.amount_on_turn_start >= 1
            {
                let pool = card_factory::character_combat_pool(state);
                let generated = card_factory::get_distinct_for_combat_filtered(
                    state,
                    &pool,
                    p.amount_on_turn_start as usize,
                    RunRngType::CombatCardGeneration,
                    |id| sts2_types::generated_card_meta(id).rarity == CardRarity::Common,
                );
                for cid in generated {
                    let _ = card_pile::add_generated_card_to_combat(
                        state,
                        content,
                        cid,
                        PileType::Hand,
                        CardPilePosition::Bottom,
                        0,
                        Some(creature),
                    )?;
                }
            }
            Ok(HookResult::default())
        }

        PowerId::HexPower => match slot {
            HookSlot::AfterApplied => {
                if creature == state.player.creature_handle {
                    let to_afflict: Vec<CardHandle> = state
                        .cards
                        .iter()
                        .filter(|c| {
                            !c.removed_from_state
                                && !matches!(c.pile, PileType::None | PileType::Deck)
                                && c.affliction.is_none()
                        })
                        .map(|c| c.handle)
                        .collect();
                    for ch in to_afflict {
                        let _ = card::afflict(state, ch, AfflictionId::Hexed, p.amount);
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::TryModifyKeywordsInCombat => {
                if let HookArgs::CardOnly(card_h) = args {
                    if creature == state.player.creature_handle
                        && state
                            .card(*card_h)
                            .is_some_and(|c| c.affliction.is_some_and(|a| a.id == AfflictionId::Hexed))
                    {
                        return Ok(HookResult {
                            keywords: Some(CardKeywords::ETHEREAL),
                            ..Default::default()
                        });
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardEnteredCombat => {
                if let HookArgs::CardOnly(card_h) = args {
                    if creature == state.player.creature_handle
                        && state.card(*card_h).is_some_and(|c| c.affliction.is_none())
                    {
                        let _ = card::afflict(state, *card_h, AfflictionId::Hexed, p.amount);
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterDeath => {
                if let HookArgs::AfterDeath { target, .. } = args {
                    if Some(*target) == p.applier_handle {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::HibernatePower => {
            if slot == HookSlot::AfterPlayerTurnStart && creature == state.player.creature_handle {
                power::modify_amount_by_handle(
                    state,
                    content,
                    creature,
                    p.handle,
                    Dec::from_i32(-1),
                    None,
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        PowerId::HiddenRootsPower => {
            if slot == HookSlotAlias::AfterSideTurnStart {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        power::apply(
                            state,
                            content,
                            PowerId::StretchPower,
                            creature,
                            Dec::from_i32(p.amount),
                            Some(creature),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::HighVoltagePower => {
            if slot == HookSlot::AfterSideTurnEnd {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        power::apply(
                            state,
                            content,
                            PowerId::StrengthPower,
                            creature,
                            Dec::from_i32(p.amount),
                            Some(creature),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::HoningPower => {
            if slot == HookSlot::ModifyDamageMultiplicative {
                if let HookArgs::ModifyDamage { dealer, props, .. } = args {
                    if *dealer == Some(creature)
                        && props.is_powered_attack()
                        && props.contains(ValueProp::FROM_ARROW)
                    {
                        let mult = Dec::ONE.checked_add(Dec::from_parts(p.amount as i64, 2)?)?;
                        return Ok(dec_result(mult));
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::IllusionPower => match slot {
            HookSlot::AfterApplied => {
                illusion_after_applied(state, content, creature)?;
                Ok(HookResult::default())
            }
            HookSlot::ShouldPowerBeRemovedOnDeath => {
                if let HookArgs::PowerQuery {
                    power_type,
                    is_temporary,
                    ..
                } = args
                {
                    return Ok(bool_result(*power_type == PowerType::Debuff && !*is_temporary));
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterDeath => {
                if let HookArgs::AfterDeath { target, .. } = args {
                    if *target == creature {
                        set_bool_field(state, creature, power_idx, "IllusionPower._isReviving", true);
                        let follow_up = get_optional_string_field(
                            state,
                            creature,
                            power_idx,
                            "IllusionPower._followUpStateId",
                        );
                        if let Some(c) = state.creature_mut(creature) {
                            if let Some(m) = &mut c.monster {
                                let next_follow = follow_up
                                    .map(Cow::Owned)
                                    .or_else(|| m.state_log_ids.last().cloned())
                                    .unwrap_or_else(|| m.current_state_id.clone());
                                m.current_state_id = Cow::Borrowed("REVIVE_MOVE");
                                m.next_move_id = Some(Cow::Borrowed("REVIVE_MOVE"));
                                m.next_move_can_transition_away = false;
                                m.transient_move_follow_up_state_id = Some(next_follow);
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldAllowHitting => {
                if let HookArgs::Creature(target) = args {
                    if *target != creature {
                        return Ok(bool_result(true));
                    }
                    let is_reviving = get_bool_field(
                        state,
                        creature,
                        power_idx,
                        "IllusionPower._isReviving",
                        false,
                    );
                    return Ok(bool_result(!is_reviving));
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldCreatureBeRemovedFromCombatAfterDeath => {
                if let HookArgs::Creature(target) = args {
                    return Ok(bool_result(*target != creature));
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::ImbalancedPower => {
            if slot == HookSlot::AfterDamageGiven {
                if let HookArgs::DamageResult {
                    dealer,
                    was_fully_blocked,
                    ..
                } = args
                {
                    imbalanced_after_damage_given(
                        state,
                        creature,
                        *dealer,
                        *was_fully_blocked,
                    )?;
                }
            }
            Ok(HookResult::default())
        }

        PowerId::ImitationLearningPower => match slot {
            HookSlot::BeforeCardPlayed => {
                if let HookArgs::CardPlay(ctx) = args {
                    let player_target = get_optional_creature_field(
                        state,
                        creature,
                        power_idx,
                        "ImitationLearningPower._playerTarget",
                    );
                    if player_target.is_none() {
                        return Err(KernelError::InvalidState(
                            "Imitation Learning has no target player".to_owned(),
                        ));
                    }
                    if player_target == Some(state.player.creature_handle)
                        && is_card_type(state, content, ctx.card, CardType::Power)?
                        && ctx.play_index == 0
                    {
                        let clone_h = clone_card_in_combat(state, ctx.card)?;
                        push_card_pair_field(
                            state,
                            creature,
                            power_idx,
                            "ImitationLearningPower._pending",
                            ctx.card.0 as i32,
                            clone_h.0 as i32,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(ctx) = args {
                    if let Some(clone_raw) = take_card_pair_field(
                        state,
                        creature,
                        power_idx,
                        "ImitationLearningPower._pending",
                        ctx.card.0 as i32,
                    ) {
                        let clone_h = CardHandle(clone_raw as u32);
                        if hooks::should_play(state, content, clone_h)? {
                            power::modify_amount_by_handle(
                                state,
                                content,
                                creature,
                                p.handle,
                                Dec::from_i32(-1),
                                None,
                                None,
                            )?;
                        }
                        let _ = autoplay::from_cards(state, content, &[clone_h], None, false)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::ImpaledPower => match slot {
            HookSlot::AfterDamageReceived => {
                if let HookArgs::DamageResult {
                    target,
                    dealer,
                    props,
                    card_source,
                    ..
                } = args
                {
                    if *target == creature
                        && dealer.is_some()
                        && card_source.is_some()
                        && p.applier_handle == Some(state.player.creature_handle)
                        && props.is_powered_attack()
                    {
                        let ext_idx = ensure_power_ext(state, creature, power_idx);
                        state.power_extensions[ext_idx].hit_counter -= 1;
                        if state.power_extensions[ext_idx].hit_counter <= 0 {
                            state.power_extensions[ext_idx].hit_counter = 3;
                            if let Some(applier) = p.applier_handle {
                                arrow::dislodge_first_arrow(
                                    state,
                                    content,
                                    creature,
                                    applier,
                                    *card_source,
                                )?;
                            }
                            if state.power_extensions[ext_idx].impaled_arrows.is_empty() {
                                let _ =
                                    power::remove_by_handle(state, content, creature, p.handle)?;
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterDeath => {
                if let HookArgs::AfterDeath {
                    target,
                    card_source,
                    ..
                } = args
                {
                    if *target == creature {
                        if let Some(applier) = p.applier_handle {
                            if state.creature(applier).is_some_and(|c| c.is_alive()) {
                                while p.ext_index.is_some_and(|e| {
                                    !state.power_extensions[e as usize].impaled_arrows.is_empty()
                                }) {
                                    arrow::dislodge_first_arrow(
                                        state,
                                        content,
                                        creature,
                                        applier,
                                        *card_source,
                                    )?;
                                }
                                let _ =
                                    power::remove_by_handle(state, content, creature, p.handle)?;
                            }
                        }
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::ImprovementPower => {
            if slot == HookSlot::AfterCombatEnd && creature == state.player.creature_handle {
                let mut candidates: Vec<CardHandle> = Vec::new();
                for &ch in &state.player.deck {
                    if let Some(ci) = state.card(ch) {
                        if content
                            .card_meta(ci.id)
                            .is_ok_and(|m| ci.upgrade_level < m.max_upgrade_level)
                        {
                            candidates.push(ch);
                        }
                    }
                }
                for _ in 0..p.amount {
                    if candidates.is_empty() {
                        break;
                    }
                    let chosen = state
                        .run_rngs
                        .get_mut(RunRngType::CombatCardSelection)
                        .next_item(&candidates)
                        .copied();
                    if let Some(ch) = chosen {
                        candidates.retain(|&x| x != ch);
                        card::upgrade(state, content, ch)?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::InfernoPower => match slot {
            HookSlot::AfterPlayerTurnStart => {
                if creature == state.player.creature_handle {
                    let self_dmg = get_decimal_field(
                        state,
                        creature,
                        power_idx,
                        "InfernoPower._selfDamage",
                        Dec::ZERO,
                    );
                    sts2_kernel::commands::damage::damage_targets(
                        state,
                        content,
                        &[creature],
                        self_dmg,
                        ValueProp::UNBLOCKABLE | ValueProp::UNPOWERED,
                        Some(creature),
                        None,
                        None,
                    )?;
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterDamageReceived => {
                if let HookArgs::DamageResult {
                    target,
                    unblocked_damage,
                    ..
                } = args
                {
                    if *target == creature
                        && *unblocked_damage > 0
                        && state.current_side == owner_side
                    {
                        let enemies = hittable_enemies(state, content)?;
                        sts2_kernel::commands::damage::damage_targets(
                            state,
                            content,
                            &enemies,
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED,
                            Some(creature),
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::InfestedPower => match slot {
            HookSlot::AfterDeath => {
                if let HookArgs::AfterDeath { target, .. } = args {
                    if *target == creature {
                        const WRIGGLER_SLOTS: [&str; 4] =
                            ["wriggler1", "wriggler2", "wriggler3", "wriggler4"];
                        for slot in WRIGGLER_SLOTS {
                            let wh = creature_cmd::spawn_monster_in_combat(
                                state,
                                content,
                                MonsterId::Wriggler,
                                owner_side,
                                Some(Cow::Borrowed(slot)),
                            )?;
                            if let Some(c) = state.creature_mut(wh) {
                                if let Some(m) = &mut c.monster {
                                    m.setup_fields.insert(
                                        Cow::Borrowed("Wriggler._startStunned"),
                                        FieldValue::Bool(true),
                                    );
                                }
                            }
                            creature_cmd::stun(state, wh, None)?;
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::ShouldStopCombatFromEnding => Ok(bool_result(true)),
            _ => Ok(HookResult::default()),
        },

        PowerId::InfiniteBladesPower => {
            if slot == HookSlot::BeforeHandDraw && creature == state.player.creature_handle {
                for _ in 0..p.amount {
                    let _ = card_pile::add_generated_card_to_combat(
                        state,
                        content,
                        CardId::Shiv,
                        PileType::Hand,
                        CardPilePosition::Bottom,
                        0,
                        Some(creature),
                    )?;
                }
            }
            Ok(HookResult::default())
        }

        PowerId::InsatiableDominionPower => {
            if slot == HookSlot::AfterSideTurnEndLate {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let dmg = 2 + state.player.turn_number;
                        sts2_kernel::commands::damage::damage_targets(
                            state,
                            content,
                            &[creature],
                            Dec::from_i32(dmg),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::InterceptPower => match slot {
            HookSlot::ModifyDamageMultiplicative => {
                if let HookArgs::ModifyDamage { target, props, .. } = args {
                    if *target == Some(creature) && props.is_powered_attack() {
                        let covered = get_creature_handle_array_len(
                            state,
                            creature,
                            power_idx,
                            "InterceptPower._coveredCreatures",
                        ) as i32;
                        return Ok(dec_result(Dec::from_i32(covered + 1)));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { side, .. } = args {
                    if *side == CombatSide::Enemy {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::IterationPower => {
            if slot == HookSlot::AfterCardDrawn {
                if let HookArgs::CardDrawn { card: card_h, .. } = args {
                    if creature == state.player.creature_handle
                        && is_card_type(state, content, *card_h, CardType::Status)?
                    {
                        let tk = state.current_turn_key();
                        let drawn_statuses = state
                            .semantic_history
                            .card_drawn
                            .iter()
                            .filter(|(turn, _, ctype, _)| {
                                *turn == tk && *ctype == CardType::Status
                            })
                            .count();
                        if drawn_statuses <= 1 {
                            let _ = card_pile::draw(state, content, p.amount, false)?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::JugglingPower => match slot {
            HookSlot::AfterApplied => {
                juggling_after_applied(state, creature, p.handle);
                Ok(HookResult::default())
            }
            HookSlot::BeforeCardPlayed => {
                if let HookArgs::CardPlay(ctx) = args {
                    if creature == state.player.creature_handle
                        && is_card_type(state, content, ctx.card, CardType::Attack)?
                    {
                        let played = get_int_field(
                            state,
                            creature,
                            power_idx,
                            "JugglingPower._attacksPlayedThisTurn",
                            0,
                        ) + 1;
                        set_int_field(
                            state,
                            creature,
                            power_idx,
                            "JugglingPower._attacksPlayedThisTurn",
                            played,
                        );
                        if played == 3 {
                            set_optional_card_field(
                                state,
                                creature,
                                power_idx,
                                "JugglingPower._cardToClone",
                                Some(ctx.card),
                            );
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(ctx) = args {
                    let to_clone = get_optional_card_field(
                        state,
                        creature,
                        power_idx,
                        "JugglingPower._cardToClone",
                    );
                    if to_clone == Some(ctx.card) {
                        set_optional_card_field(
                            state,
                            creature,
                            power_idx,
                            "JugglingPower._cardToClone",
                            None,
                        );
                        for _ in 0..p.amount {
                            let clone_h = clone_card_in_combat(state, ctx.card)?;
                            card_pile::enter_combat(
                                state,
                                content,
                                clone_h,
                                PileType::Hand,
                                CardPilePosition::Bottom,
                                true,
                            )?;
                            hooks::after_card_generated(
                                state,
                                content,
                                clone_h,
                                Some(creature),
                            )?;
                            hooks::after_card_generated_for_combat(
                                state,
                                content,
                                clone_h,
                                Some(creature),
                            )?;
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        set_int_field(
                            state,
                            creature,
                            power_idx,
                            "JugglingPower._attacksPlayedThisTurn",
                            0,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::KiVersePower => match slot {
            HookSlot::ModifyCardPlayCount => {
                if let HookArgs::CardPlayCount {
                    card, play_count, ..
                } = args
                {
                    if creature == state.player.creature_handle {
                        let is_melody = state
                            .card(*card)
                            .and_then(|c| content.card_meta(c.id).ok())
                            .is_some_and(|m| m.base_tags.intersects(CardTags::WAH | CardTags::CHO));
                        if is_melody {
                            return Ok(int_result(*play_count + 1));
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterModifyingCardPlayCount => {
                power::modify_amount_by_handle(
                    state,
                    content,
                    creature,
                    p.handle,
                    Dec::from_i32(-1),
                    None,
                    None,
                )?;
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::KnockdownPower => match slot {
            HookSlot::ModifyDamageMultiplicative => {
                if let HookArgs::ModifyDamage {
                    target,
                    dealer,
                    props,
                    ..
                } = args
                {
                    if *target == Some(creature)
                        && props.is_powered_attack()
                        && *dealer != p.applier_handle
                    {
                        return Ok(dec_result(Dec::from_i32(p.amount)));
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::LagavulinMatriarchDominionPower => {
            if slot == HookSlot::AfterSideTurnEnd {
                if let HookArgs::SideTurn { side, participants } = args {
                    if *side == CombatSide::Player && participants.contains(&creature) {
                        power::apply(
                            state,
                            content,
                            PowerId::StrengthPower,
                            creature,
                            Dec::from_i32(-p.amount),
                            None,
                            None,
                        )?;
                        power::apply(
                            state,
                            content,
                            PowerId::DexterityPower,
                            creature,
                            Dec::from_i32(-p.amount),
                            None,
                            None,
                        )?;
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::LeadershipPower => {
            if slot == HookSlot::ModifyDamageAdditive {
                if let HookArgs::ModifyDamage { dealer, props, .. } = args {
                    if let Some(d) = *dealer {
                        if d != creature
                            && state.creature(d).is_some_and(|c| c.side == owner_side)
                            && props.is_powered_attack()
                        {
                            return Ok(dec_result(Dec::from_i32(p.amount)));
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::LethalityPower => {
            if slot == HookSlot::ModifyDamageMultiplicative {
                if let HookArgs::ModifyDamage {
                    props,
                    card_source,
                    card_play,
                    ..
                } = args
                {
                    if props.is_powered_attack() && creature == state.player.creature_handle {
                        if let Some(ch) = *card_source {
                            let in_play_pile =
                                state.card(ch).is_some_and(|c| c.pile == PileType::Play);
                            let play_index = card_play.as_ref().map_or(0, |cp| cp.play_index);
                            if !(in_play_pile && play_index > 0) {
                                let attacks_started =
                                    state.player.counters.attack_cards_started_this_turn;
                                let threshold = if in_play_pile && card_play.is_some() {
                                    1
                                } else {
                                    0
                                };
                                if attacks_started <= threshold {
                                    let mult = Dec::ONE
                                        .checked_add(Dec::from_parts(p.amount as i64, 2)?)?;
                                    return Ok(dec_result(mult));
                                }
                            }
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::LightningRodPower => {
            if slot == HookSlot::AfterEnergyReset && creature == state.player.creature_handle {
                orb::channel(state, content, OrbId::LightningOrb)?;
                power::modify_amount_by_handle(
                    state,
                    content,
                    creature,
                    p.handle,
                    Dec::from_i32(-1),
                    None,
                    None,
                )?;
            }
            Ok(HookResult::default())
        }

        PowerId::LoopPower => {
            if slot == HookSlot::AfterPlayerTurnStart
                && creature == state.player.creature_handle
                && !state.player.orbs.is_empty()
            {
                for _ in 0..p.amount {
                    if state.player.orbs.is_empty() {
                        break;
                    }
                    orb::passive(state, content, 0, None, true)?;
                }
            }
            Ok(HookResult::default())
        }

        PowerId::LostLutePower => {
            if slot == HookSlot::AfterCardPlayed {
                if let HookArgs::CardPlay(ctx) = args {
                    if creature == state.player.creature_handle {
                        let triggers = count_first_melody_triggers(state, content, ctx.card);
                        if triggers > 0 {
                            let mut choices = SmallVec::new();
                            let _ = autoplay::from_top_of_draw_pile(
                                state,
                                content,
                                (triggers * p.amount).max(0) as usize,
                                false,
                                &mut choices,
                            )?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::LullabyPower => match slot {
            HookSlot::AfterApplied => {
                lullaby_after_applied(state, creature, p.handle);
                Ok(HookResult::default())
            }
            HookSlot::BeforeSideTurnStart => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        set_bool_field(
                            state,
                            creature,
                            power_idx,
                            "LullabyPower._proccdThisTurn",
                            false,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if creature == state.player.creature_handle && state.is_live_combat() {
                    let proccd = get_bool_field(
                        state,
                        creature,
                        power_idx,
                        "LullabyPower._proccdThisTurn",
                        false,
                    );
                    if !proccd && all_three_melodies_played(state) {
                        set_bool_field(
                            state,
                            creature,
                            power_idx,
                            "LullabyPower._proccdThisTurn",
                            true,
                        );
                        block::gain_block(
                            state,
                            content,
                            creature,
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED,
                            None,
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::MachineLearningPower => {
            if slot == HookSlot::ModifyHandDraw {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    if creature == state.player.creature_handle {
                        return Ok(dec_result(
                            original_count.checked_add(Dec::from_i32(p.amount))?,
                        ));
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::MagicBombPower => match slot {
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature)
                        && p.applier_handle
                            .is_some_and(|a| state.creature(a).is_some_and(|c| c.is_alive()))
                    {
                        sts2_kernel::commands::damage::damage_targets(
                            state,
                            content,
                            &[creature],
                            Dec::from_i32(p.amount),
                            ValueProp::UNPOWERED,
                            Some(creature),
                            None,
                            None,
                        )?;
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterDeath => {
                if let HookArgs::AfterDeath {
                    target,
                    was_removal_prevented,
                    ..
                } = args
                {
                    if !*was_removal_prevented && Some(*target) == p.applier_handle {
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::MasterPlannerPower => {
            if slot == HookSlot::AfterCardPlayed {
                if let HookArgs::CardPlay(ctx) = args {
                    if creature == state.player.creature_handle
                        && is_card_type(state, content, ctx.card, CardType::Skill)?
                    {
                        if let Some(c) = state.card_mut(ctx.card) {
                            c.local_keywords.insert(CardKeywords::SLY);
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::MayhemPower => {
            if slot == HookSlot::AfterAutoPrePlayPhaseEntered
                && creature == state.player.creature_handle
            {
                let mut choices = SmallVec::new();
                let _ = autoplay::from_top_of_draw_pile(
                    state,
                    content,
                    p.amount.max(0) as usize,
                    false,
                    &mut choices,
                )?;
            }
            Ok(HookResult::default())
        }

        PowerId::MindRotPower => {
            if slot == HookSlot::ModifyHandDraw {
                if let HookArgs::ModifyHandDraw { original_count } = args {
                    if creature == state.player.creature_handle {
                        return Ok(dec_result(
                            original_count
                                .checked_sub(Dec::from_i32(p.amount))?
                                .max(Dec::ZERO),
                        ));
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::MonarchsGazePower => {
            if slot == HookSlot::AfterDamageGiven {
                if let HookArgs::DamageResult {
                    target,
                    dealer,
                    props,
                    ..
                } = args
                {
                    if *dealer == Some(creature) && props.is_powered_attack() {
                        power::apply(
                            state,
                            content,
                            PowerId::MonarchsGazeStrengthDownPower,
                            *target,
                            Dec::from_i32(p.amount),
                            Some(creature),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::MonologuePower => match slot {
            HookSlot::BeforeCardPlayed => {
                if let HookArgs::CardPlay(ctx) = args {
                    if creature == state.player.creature_handle {
                        let str_amt = get_decimal_field(
                            state,
                            creature,
                            power_idx,
                            "MonologuePower._strength",
                            Dec::ONE,
                        )
                        .to_i32_trunc()?;
                        push_card_int_field(
                            state,
                            creature,
                            power_idx,
                            "MonologuePower._amountsForPlayedCards",
                            ctx.card.0 as i32,
                            str_amt,
                        );
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterCardPlayed => {
                if let HookArgs::CardPlay(ctx) = args {
                    if creature == state.player.creature_handle {
                        if let Some(amt) = take_card_int_field(
                            state,
                            creature,
                            power_idx,
                            "MonologuePower._amountsForPlayedCards",
                            ctx.card.0 as i32,
                        ) {
                            let str_amt = get_decimal_field(
                                state,
                                creature,
                                power_idx,
                                "MonologuePower._strength",
                                Dec::ONE,
                            )
                            .to_i32_trunc()?;
                            power::apply(
                                state,
                                content,
                                PowerId::StrengthPower,
                                creature,
                                Dec::from_i32(amt),
                                Some(creature),
                                None,
                            )?;
                            let applied = get_decimal_field(
                                state,
                                creature,
                                power_idx,
                                "MonologuePower._strengthApplied",
                                Dec::ZERO,
                            );
                            let next_applied = applied
                                .checked_add(Dec::from_i32(str_amt))?
                                .min(Dec::CLAMP_MAX_999M);
                            set_decimal_field(
                                state,
                                creature,
                                power_idx,
                                "MonologuePower._strengthApplied",
                                next_applied,
                            );
                        }
                    }
                }
                Ok(HookResult::default())
            }
            HookSlot::AfterSideTurnEnd => {
                if let HookArgs::SideTurn { participants, .. } = args {
                    if participants.contains(&creature) {
                        let applied = get_decimal_field(
                            state,
                            creature,
                            power_idx,
                            "MonologuePower._strengthApplied",
                            Dec::ZERO,
                        );
                        let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                        power::apply(
                            state,
                            content,
                            PowerId::StrengthPower,
                            creature,
                            applied.checked_neg()?,
                            Some(creature),
                            None,
                        )?;
                    }
                }
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        },

        PowerId::MotifPower => {
            if slot == HookSlot::AfterCardPlayed {
                if let HookArgs::CardPlay(ctx) = args {
                    if creature == state.player.creature_handle {
                        let triggers = count_first_melody_triggers(state, content, ctx.card);
                        if triggers > 0 {
                            let _ = card_pile::draw(state, content, triggers * p.amount, false)?;
                        }
                    }
                }
            }
            Ok(HookResult::default())
        }

        PowerId::MythicFormPower => {
            if slot == HookSlot::BeforeHandDraw && creature == state.player.creature_handle {
                let tarot_pool: Vec<CardId> = card_factory::tarot_card_pool_raw(state)
                    .iter()
                    .copied()
                    .filter(|&id| card_factory::is_card_allowed_in_run(state, id))
                    .collect();
                for _ in 0..p.amount {
                    let picked = state
                        .run_rngs
                        .get_mut(RunRngType::CombatCardSelection)
                        .next_item(&tarot_pool)
                        .copied();
                    if let Some(cid) = picked {
                        let _ = card_pile::add_generated_card_to_combat(
                            state,
                            content,
                            cid,
                            PileType::Hand,
                            CardPilePosition::Bottom,
                            0,
                            Some(creature),
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }

        _ => Err(KernelError::Unsupported {
            kind: "Power",
            id: p.id.model_id_str().to_owned(),
        }),
    }
}

pub fn dispatch_detached_hook(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power: PowerInst,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    let _ = power_meta(power.id)?;
    match slot {
        HookSlot::BeforeApplied => {
            if is_temporary_power_index(power.id as usize) {
                if let HookArgs::PowerChanged {
                    amount,
                    applier,
                    card_source,
                    ..
                } = args
                {
                    temporary_power_before_applied(
                        state,
                        content,
                        power.id,
                        creature,
                        *amount,
                        *applier,
                        *card_source,
                    )?;
                }
            }
            Ok(HookResult::default())
        }
        HookSlot::AfterRemoved => {
            match power.id {
                PowerId::BurrowedPower => {
                    burrowed_after_removed(state, content, creature)?;
                }
                PowerId::DampenPower => {
                    dampen_after_removed_by_ext(state, content, power.ext_index)?;
                }
                PowerId::HexPower => {
                    hex_after_removed(state, creature);
                }
                PowerId::InterceptPower => {
                    intercept_after_removed(state, content, creature)?;
                }
                _ => {}
            }
            Ok(HookResult::default())
        }
        _ => Ok(HookResult::default()),
    }
}

pub fn resume_hook_choice(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    slot: HookSlot,
    selected: &[CardHandle],
) -> Result<HookResult, KernelError> {
    let p = state
        .creature(creature)
        .and_then(|c| c.powers.get(power_idx))
        .copied()
        .ok_or_else(|| KernelError::InvalidState("missing power".to_owned()))?;
    match (p.id, slot) {
        (PowerId::EntropyPower, HookSlot::AfterPlayerTurnStart) => {
            for &ch in selected {
                let _ = card::transform_to_random(
                    state,
                    content,
                    ch,
                    RunRngType::CombatCardGeneration,
                )?;
            }
            Ok(HookResult::default())
        }
        (PowerId::ForegoneConclusionPower, HookSlot::BeforeHandDraw) => {
            for &ch in selected {
                card_pile::add_sync(state, ch, PileType::Hand, CardPilePosition::Bottom);
            }
            let _ = power::remove_by_handle(state, content, creature, p.handle)?;
            Ok(HookResult::default())
        }
        _ => Err(KernelError::Unsupported {
            kind: "PowerHookChoiceResume",
            id: format!("{}:{slot:?}", p.id.model_id_str()),
        }),
    }
}

type HookSlotAlias = HookSlot;

#[inline]
fn csharp_card_rarity_rank(r: CardRarity) -> u8 {
    match r {
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

#[inline]
fn dec_result(v: Dec) -> HookResult {
    HookResult {
        dec_val: Some(v),
        ..Default::default()
    }
}

#[inline]
fn bool_result(v: bool) -> HookResult {
    HookResult {
        bool_val: Some(v),
        ..Default::default()
    }
}

#[inline]
fn int_result(v: i32) -> HookResult {
    HookResult {
        int_val: Some(v),
        ..Default::default()
    }
}

#[allow(clippy::too_many_arguments)]
fn dispatch_temporary_stat_power(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    p: PowerInst,
    slot: HookSlot,
    args: &HookArgs<'_>,
    stat_power: PowerId,
    sign: i32,
) -> Result<HookResult, KernelError> {
    match slot {
        HookSlot::AfterPowerAmountChanged => {
            if let HookArgs::PowerChanged {
                power_id,
                power_handle,
                owner,
                amount,
                applier,
                card_source,
            } = args
            {
                if *owner == creature
                    && *power_id == p.id
                    && *power_handle == p.handle
                    && *amount != Dec::from_i32(p.amount)
                {
                    let delta = amount.checked_mul(Dec::from_i32(sign))?;
                    power::apply(
                        state,
                        content,
                        stat_power,
                        creature,
                        delta,
                        *applier,
                        *card_source,
                    )?;
                }
            }
            Ok(HookResult::default())
        }
        HookSlot::AfterSideTurnEnd => {
            if let HookArgs::SideTurn { participants, .. } = args {
                if participants.contains(&creature) {
                    let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    let rev = Dec::from_i32(-sign * p.amount);
                    power::apply(
                        state,
                        content,
                        stat_power,
                        creature,
                        rev,
                        Some(creature),
                        None,
                    )?;
                }
            }
            Ok(HookResult::default())
        }
        _ => Ok(HookResult::default()),
    }
}

fn dispatch_temporary_thorns_power(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    owner_side: CombatSide,
    p: PowerInst,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    match slot {
        HookSlot::AfterPowerAmountChanged => {
            if let HookArgs::PowerChanged {
                power_id,
                power_handle,
                owner,
                amount,
                applier,
                card_source,
            } = args
            {
                if *owner == creature
                    && *power_id == p.id
                    && *power_handle == p.handle
                    && *amount != Dec::from_i32(p.amount)
                {
                    power::apply(
                        state,
                        content,
                        PowerId::ThornsPower,
                        creature,
                        *amount,
                        *applier,
                        *card_source,
                    )?;
                }
            }
            Ok(HookResult::default())
        }
        HookSlot::AfterSideTurnEnd => {
            if let HookArgs::SideTurn { side, .. } = args {
                if owner_side != *side {
                    let _ = power::remove_by_handle(state, content, creature, p.handle)?;
                    let thorns_handle = state.creature(creature).and_then(|c| {
                        c.powers
                            .iter()
                            .find(|pw| pw.id == PowerId::ThornsPower)
                            .map(|pw| pw.handle)
                    });
                    if let Some(th) = thorns_handle {
                        power::modify_amount_by_handle(
                            state,
                            content,
                            creature,
                            th,
                            Dec::from_i32(-p.amount),
                            Some(creature),
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        _ => Ok(HookResult::default()),
    }
}

fn dispatch_free_card_power(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    slot: HookSlot,
    args: &HookArgs<'_>,
    required_type: Option<CardType>,
) -> Result<HookResult, KernelError> {
    match slot {
        HookSlot::TryModifyEnergyCostInCombatLate => {
            if let HookArgs::CardCost { card, .. } = args {
                if creature == state.player.creature_handle {
                    let in_hand_or_play = state
                        .card(*card)
                        .is_some_and(|c| matches!(c.pile, PileType::Hand | PileType::Play));
                    let type_ok = match required_type {
                        None => true,
                        Some(t) => is_card_type(state, content, *card, t)?,
                    };
                    if in_hand_or_play && type_ok {
                        return Ok(HookResult {
                            dec_val: Some(Dec::ZERO),
                            modified: true,
                            ..Default::default()
                        });
                    }
                }
            }
            Ok(HookResult::default())
        }
        HookSlot::BeforeCardPlayed => {
            if let HookArgs::CardPlay(ctx) = args {
                if creature == state.player.creature_handle {
                    let in_hand_or_play = state
                        .card(ctx.card)
                        .is_some_and(|c| matches!(c.pile, PileType::Hand | PileType::Play));
                    let type_ok = match required_type {
                        None => true,
                        Some(t) => is_card_type(state, content, ctx.card, t)?,
                    };
                    if in_hand_or_play && type_ok {
                        power::modify_amount_by_handle(
                            state,
                            content,
                            creature,
                            power_handle,
                            Dec::from_i32(-1),
                            None,
                            None,
                        )?;
                    }
                }
            }
            Ok(HookResult::default())
        }
        _ => Ok(HookResult::default()),
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

pub(crate) fn is_card_type(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
    expected: CardType,
) -> Result<bool, KernelError> {
    let Some(ci) = state.card(card) else {
        return Ok(false);
    };
    Ok(content.card_meta(ci.id)?.card_type == expected)
}

fn is_card_with_tag(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    card: Option<CardHandle>,
    tag: CardTags,
) -> Result<bool, KernelError> {
    let Some(ch) = card else {
        return Ok(false);
    };
    let Some(ci) = state.card(ch) else {
        return Ok(false);
    };
    Ok(content.card_meta(ci.id)?.base_tags.contains(tag))
}

fn hittable_enemies(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<SmallVec<[CreatureHandle; 8]>, KernelError> {
    if state.hook_cache.dirty_mask == 0
        && state.hook_cache.cached_tables.is_some()
        && !state
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
    let candidates: SmallVec<[CreatureHandle; 8]> = SmallVec::from_slice(&state.enemy_handles);
    let mut out = SmallVec::new();
    for eh in candidates {
        if state.creature(eh).is_some_and(|c| c.is_alive())
            && hooks::should_allow_hitting(state, content, eh)?
        {
            out.push(eh);
        }
    }
    Ok(out)
}

fn get_opponents_of(state: &BattleState, side: CombatSide) -> SmallVec<[CreatureHandle; 8]> {
    if side == CombatSide::Player {
        state
            .enemy_handles
            .iter()
            .copied()
            .filter(|&h| state.creature(h).is_some_and(|c| c.is_alive()))
            .collect()
    } else {
        state
            .ally_handles
            .iter()
            .copied()
            .filter(|&h| state.creature(h).is_some_and(|c| c.is_alive()))
            .collect()
    }
}

fn get_doomed_creatures(state: &BattleState, side: CombatSide) -> SmallVec<[CreatureHandle; 8]> {
    let handles = if side == CombatSide::Player {
        &state.ally_handles[..]
    } else {
        &state.enemy_handles[..]
    };
    handles
        .iter()
        .copied()
        .filter(|&h| {
            state.creature(h).is_some_and(|c| {
                c.is_alive()
                    && c.powers
                        .iter()
                        .find(|pw| pw.id == PowerId::DoomPower)
                        .is_some_and(|pw| c.current_hp <= pw.amount)
            })
        })
        .collect()
}

fn get_power_type_for_amount(
    content: &dyn ContentDispatcher,
    power_id: PowerId,
    amount: Dec,
) -> Result<PowerType, KernelError> {
    let meta = content.power_meta(power_id)?;
    let allow_neg = meta.flags.contains(PowerStaticFlags::ALLOW_NEGATIVE);
    if meta.stack_type == sts2_types::PowerStackType::Counter && allow_neg && amount < Dec::ZERO {
        return Ok(PowerType::Debuff);
    }
    if !allow_neg && meta.power_type == PowerType::Debuff && amount < Dec::ZERO {
        return Ok(PowerType::Buff);
    }
    Ok(meta.power_type)
}

fn has_infinite_hp_display(state: &BattleState, enemy: CreatureHandle) -> bool {
    let Some(c) = state.creature(enemy) else {
        return false;
    };
    if c.monster_id == Some(MonsterId::WaterfallGiant) && c.max_hp == 999_999_999 {
        return true;
    }
    c.powers.iter().any(|pw| {
        if pw.id != PowerId::HardenedShellPower {
            return false;
        }
        let received = pw
            .ext_index
            .and_then(|e| state.power_extensions.get(e as usize))
            .and_then(|ext| ext.private_fields.get("HardenedShellPower._damageReceivedThisTurn"))
            .and_then(|fv| match fv {
                FieldValue::Decimal(d) => Some(*d),
                _ => None,
            })
            .unwrap_or(Dec::ZERO);
        (Dec::from_i32(pw.amount)
            .checked_sub(received)
            .unwrap_or(Dec::ZERO))
        .max(Dec::ZERO)
        .to_i32_trunc()
        .unwrap_or(0)
            == 0
    })
}

fn count_first_melody_triggers(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    card: CardHandle,
) -> i32 {
    let Some(ci) = state.card(card) else {
        return 0;
    };
    let Ok(meta) = content.card_meta(ci.id) else {
        return 0;
    };
    let mut triggers = 0;
    if meta.base_tags.contains(CardTags::WAH) && state.player.counters.wah_started_this_turn == 1 {
        triggers += 1;
    }
    if meta.base_tags.contains(CardTags::CHO) && state.player.counters.cho_started_this_turn == 1 {
        triggers += 1;
    }
    if meta.base_tags.contains(CardTags::KI) && state.player.counters.ki_started_this_turn == 1 {
        triggers += 1;
    }
    triggers
}

fn all_three_melodies_played(state: &BattleState) -> bool {
    sts2_kernel::commands::melody::played_this_turn(state, CardTags::CHO)
        && sts2_kernel::commands::melody::played_this_turn(state, CardTags::WAH)
        && sts2_kernel::commands::melody::played_this_turn(state, CardTags::KI)
}

fn ensure_power_ext(state: &mut BattleState, creature: CreatureHandle, power_idx: usize) -> usize {
    if let Some(e) = state
        .creature(creature)
        .and_then(|c| c.powers.get(power_idx))
        .and_then(|p| p.ext_index)
    {
        return e as usize;
    }
    let idx = state.power_extensions.len();
    state.power_extensions.push(Default::default());
    if let Some(c) = state.creature_mut(creature) {
        if let Some(p) = c.powers.get_mut(power_idx) {
            p.ext_index = Some(idx as u32);
        }
    }
    idx
}

fn ensure_power_ext_by_handle(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
) -> Option<usize> {
    let idx = state
        .creature(creature)
        .and_then(|c| c.powers.iter().position(|p| p.handle == power_handle))?;
    Some(ensure_power_ext(state, creature, idx))
}

fn get_int_field(
    state: &BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &str,
    default: i32,
) -> i32 {
    state
        .creature(creature)
        .and_then(|c| c.powers.get(power_idx))
        .and_then(|p| p.ext_index)
        .and_then(|e| state.power_extensions.get(e as usize))
        .and_then(|ext| ext.private_fields.get(key))
        .and_then(|fv| match fv {
            FieldValue::Int(v) => Some(*v),
            _ => None,
        })
        .unwrap_or(default)
}

fn set_int_field(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &'static str,
    val: i32,
) {
    let ext_idx = ensure_power_ext(state, creature, power_idx);
    state.power_extensions[ext_idx]
        .private_fields
        .insert(Cow::Borrowed(key), FieldValue::Int(val));
}

fn get_decimal_field(
    state: &BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &str,
    default: Dec,
) -> Dec {
    state
        .creature(creature)
        .and_then(|c| c.powers.get(power_idx))
        .and_then(|p| p.ext_index)
        .and_then(|e| state.power_extensions.get(e as usize))
        .and_then(|ext| ext.private_fields.get(key))
        .and_then(|fv| match fv {
            FieldValue::Decimal(d) => Some(*d),
            FieldValue::Int(v) => Some(Dec::from_i32(*v)),
            _ => None,
        })
        .unwrap_or(default)
}

fn set_decimal_field(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &'static str,
    val: Dec,
) {
    let ext_idx = ensure_power_ext(state, creature, power_idx);
    state.power_extensions[ext_idx]
        .private_fields
        .insert(Cow::Borrowed(key), FieldValue::Decimal(val));
}

pub(crate) fn get_bool_field(
    state: &BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &str,
    default: bool,
) -> bool {
    state
        .creature(creature)
        .and_then(|c| c.powers.get(power_idx))
        .and_then(|p| p.ext_index)
        .and_then(|e| state.power_extensions.get(e as usize))
        .and_then(|ext| ext.private_fields.get(key))
        .and_then(|fv| match fv {
            FieldValue::Bool(b) => Some(*b),
            _ => None,
        })
        .unwrap_or(default)
}

fn set_bool_field(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &'static str,
    val: bool,
) {
    let ext_idx = ensure_power_ext(state, creature, power_idx);
    state.power_extensions[ext_idx]
        .private_fields
        .insert(Cow::Borrowed(key), FieldValue::Bool(val));
}

fn get_optional_string_field(
    state: &BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &str,
) -> Option<String> {
    state
        .creature(creature)
        .and_then(|c| c.powers.get(power_idx))
        .and_then(|p| p.ext_index)
        .and_then(|e| state.power_extensions.get(e as usize))
        .and_then(|ext| ext.private_fields.get(key))
        .and_then(|fv| match fv {
            FieldValue::String(s) if !s.is_empty() => Some(s.clone()),
            _ => None,
        })
}

fn get_optional_card_field(
    state: &BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &str,
) -> Option<CardHandle> {
    state
        .creature(creature)
        .and_then(|c| c.powers.get(power_idx))
        .and_then(|p| p.ext_index)
        .and_then(|e| state.power_extensions.get(e as usize))
        .and_then(|ext| ext.private_fields.get(key))
        .and_then(|fv| match fv {
            FieldValue::CardHandle(h) | FieldValue::Int(h) | FieldValue::NullableInt(Some(h))
                if *h > 0 =>
            {
                Some(CardHandle(*h as u32))
            }
            _ => None,
        })
}

fn set_optional_card_field(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &'static str,
    val: Option<CardHandle>,
) {
    let ext_idx = ensure_power_ext(state, creature, power_idx);
    state.power_extensions[ext_idx].private_fields.insert(
        Cow::Borrowed(key),
        match val {
            Some(ch) => FieldValue::CardHandle(ch.0 as i32),
            None => FieldValue::NullableInt(None),
        },
    );
}

fn get_optional_creature_field(
    state: &BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &str,
) -> Option<CreatureHandle> {
    state
        .creature(creature)
        .and_then(|c| c.powers.get(power_idx))
        .and_then(|p| p.ext_index)
        .and_then(|e| state.power_extensions.get(e as usize))
        .and_then(|ext| ext.private_fields.get(key))
        .and_then(|fv| match fv {
            FieldValue::CreatureHandle(h)
            | FieldValue::Int(h)
            | FieldValue::NullableInt(Some(h))
                if *h > 0 =>
            {
                Some(CreatureHandle(*h as u32))
            }
            _ => None,
        })
}

fn remove_private_field(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    key: &str,
) {
    if let Some(ext_idx) = state
        .creature(creature)
        .and_then(|c| c.powers.iter().find(|p| p.handle == power_handle))
        .and_then(|p| p.ext_index)
    {
        state.power_extensions[ext_idx as usize]
            .private_fields
            .remove(key);
    }
}

fn push_card_int_field(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &'static str,
    card_handle: i32,
    val: i32,
) {
    let ext_idx = ensure_power_ext(state, creature, power_idx);
    let entry = state.power_extensions[ext_idx]
        .private_fields
        .entry(Cow::Borrowed(key))
        .or_insert_with(|| FieldValue::CardIntMap(Vec::new()));
    if let FieldValue::CardIntMap(map) = entry {
        if let Some(pair) = map.iter_mut().find(|(k, _)| *k == card_handle) {
            pair.1 = val;
        } else {
            map.push((card_handle, val));
        }
    }
}

fn take_card_int_field(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &str,
    card_handle: i32,
) -> Option<i32> {
    let ext_idx = state
        .creature(creature)
        .and_then(|c| c.powers.get(power_idx))
        .and_then(|p| p.ext_index)? as usize;
    let mut remove_key = false;
    let out = match state.power_extensions[ext_idx].private_fields.get_mut(key) {
        Some(FieldValue::CardIntMap(map)) => {
            let pos = map.iter().position(|(k, _)| *k == card_handle);
            let taken = pos.map(|i| map.remove(i).1);
            if map.is_empty() {
                remove_key = true;
            }
            taken
        }
        _ => None,
    };
    if remove_key {
        state.power_extensions[ext_idx].private_fields.remove(key);
    }
    out
}

fn push_card_pair_field(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &'static str,
    orig_h: i32,
    clone_h: i32,
) {
    let ext_idx = ensure_power_ext(state, creature, power_idx);
    let entry = state.power_extensions[ext_idx]
        .private_fields
        .entry(Cow::Borrowed(key))
        .or_insert_with(|| FieldValue::CardPairArray(Vec::new()));
    if let FieldValue::CardPairArray(arr) = entry {
        arr.push((orig_h, clone_h));
    }
}

fn take_card_pair_field(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &str,
    orig_h: i32,
) -> Option<i32> {
    let ext_idx = state
        .creature(creature)
        .and_then(|c| c.powers.get(power_idx))
        .and_then(|p| p.ext_index)? as usize;
    match state.power_extensions[ext_idx].private_fields.get_mut(key) {
        Some(FieldValue::CardPairArray(arr)) => arr
            .iter()
            .position(|(o, _)| *o == orig_h)
            .map(|pos| arr.remove(pos).1),
        _ => None,
    }
}

fn push_card_handle_field(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &'static str,
    card_handle: i32,
) {
    let ext_idx = ensure_power_ext(state, creature, power_idx);
    let entry = state.power_extensions[ext_idx]
        .private_fields
        .entry(Cow::Borrowed(key))
        .or_insert_with(|| FieldValue::CardHandleArray(Vec::new()));
    match entry {
        FieldValue::CardHandleArray(arr) | FieldValue::IntArray(arr) => arr.push(card_handle),
        _ => {}
    }
}

fn remove_card_handle_field(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &str,
    card_handle: i32,
    remove_if_empty: bool,
) -> bool {
    let Some(ext_idx) = state
        .creature(creature)
        .and_then(|c| c.powers.get(power_idx))
        .and_then(|p| p.ext_index)
    else {
        return false;
    };
    let mut should_remove_key = false;
    let removed = match state.power_extensions[ext_idx as usize]
        .private_fields
        .get_mut(key)
    {
        Some(FieldValue::CardHandleArray(arr) | FieldValue::IntArray(arr)) => {
            if let Some(pos) = arr.iter().position(|&h| h == card_handle) {
                arr.remove(pos);
                if remove_if_empty && arr.is_empty() {
                    should_remove_key = true;
                }
                true
            } else {
                false
            }
        }
        _ => false,
    };
    if should_remove_key {
        state.power_extensions[ext_idx as usize]
            .private_fields
            .remove(key);
    }
    removed
}

fn get_card_handle_array_len(
    state: &BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &str,
) -> usize {
    state
        .creature(creature)
        .and_then(|c| c.powers.get(power_idx))
        .and_then(|p| p.ext_index)
        .and_then(|e| state.power_extensions.get(e as usize))
        .and_then(|ext| ext.private_fields.get(key))
        .and_then(|fv| match fv {
            FieldValue::CardHandleArray(arr) | FieldValue::IntArray(arr) => Some(arr.len()),
            _ => None,
        })
        .unwrap_or(0)
}

fn remove_creature_handle_field(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &str,
    handle: i32,
) -> bool {
    let Some(ext_idx) = state
        .creature(creature)
        .and_then(|c| c.powers.get(power_idx))
        .and_then(|p| p.ext_index)
    else {
        return false;
    };
    match state.power_extensions[ext_idx as usize]
        .private_fields
        .get_mut(key)
    {
        Some(FieldValue::CreatureHandleArray(arr) | FieldValue::IntArray(arr)) => {
            if let Some(pos) = arr.iter().position(|&h| h == handle) {
                arr.remove(pos);
                true
            } else {
                false
            }
        }
        _ => false,
    }
}

fn get_creature_handle_array_len(
    state: &BattleState,
    creature: CreatureHandle,
    power_idx: usize,
    key: &str,
) -> usize {
    state
        .creature(creature)
        .and_then(|c| c.powers.get(power_idx))
        .and_then(|p| p.ext_index)
        .and_then(|e| state.power_extensions.get(e as usize))
        .and_then(|ext| ext.private_fields.get(key))
        .and_then(|fv| match fv {
            FieldValue::CreatureHandleArray(arr) | FieldValue::IntArray(arr) => Some(arr.len()),
            _ => None,
        })
        .unwrap_or(0)
}

// ============================================================================
// Pure Semantic Helpers & Public Entry Helpers
// ============================================================================

pub fn cruelty_modify_vulnerable_multiplier(
    owner: CreatureHandle,
    amount: i32,
    target: CreatureHandle,
    current_mult: Dec,
    props: ValueProp,
) -> Result<Dec, KernelError> {
    if target == owner || !props.is_powered_attack() {
        return Ok(current_mult);
    }
    Ok(current_mult.checked_add(Dec::from_parts(amount as i64, 2)?)?)
}

pub fn debilitate_modify_vulnerable_multiplier(
    owner: CreatureHandle,
    target: CreatureHandle,
    current_mult: Dec,
    props: ValueProp,
) -> Result<Dec, KernelError> {
    if target != owner || !props.is_powered_attack() {
        return Ok(current_mult);
    }
    let bonus = current_mult.checked_sub(Dec::ONE)?;
    Ok(current_mult.checked_add(bonus)?)
}

pub fn debilitate_modify_weak_multiplier(
    owner: CreatureHandle,
    dealer: CreatureHandle,
    current_mult: Dec,
    props: ValueProp,
) -> Result<Dec, KernelError> {
    if dealer != owner || !props.is_powered_attack() {
        return Ok(current_mult);
    }
    let reduction = Dec::ONE.checked_sub(current_mult)?;
    Ok(current_mult.checked_sub(reduction)?)
}

pub const fn minion_should_owner_death_trigger_fatal() -> bool {
    false
}

pub fn temporary_power_before_applied(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    power_id: PowerId,
    target: CreatureHandle,
    amount: Dec,
    applier: Option<CreatureHandle>,
    card_source: Option<CardHandle>,
) -> Result<(), KernelError> {
    let spec = match power_id {
        PowerId::AnticipatePower | PowerId::FadePower | PowerId::HelicalDartPower => {
            Some((PowerId::DexterityPower, 1))
        }
        PowerId::CoordinatePower | PowerId::FeedingFrenzyPower | PowerId::FlexPotionPower => {
            Some((PowerId::StrengthPower, 1))
        }
        PowerId::CrushUnderPower
        | PowerId::DarkShacklesPower
        | PowerId::DyingStarPower
        | PowerId::EnfeeblingTouchPower
        | PowerId::ManglePower
        | PowerId::MonarchsGazeStrengthDownPower => Some((PowerId::StrengthPower, -1)),
        PowerId::FocusedStrikePower | PowerId::HotfixPower => Some((PowerId::FocusPower, 1)),
        PowerId::HyperbeamFocusDownPower => Some((PowerId::FocusPower, -1)),
        PowerId::ChoBarbsPower => Some((PowerId::ThornsPower, 1)),
        _ => None,
    };
    if let Some((stat_power, sign)) = spec {
        let signed = amount.checked_mul(Dec::from_i32(sign))?;
        power::apply(state, content, stat_power, target, signed, applier, card_source)?;
    }
    Ok(())
}

pub fn feral_after_applied(state: &mut BattleState, creature: CreatureHandle, power_handle: u32) {
    if let Some(ext_idx) = ensure_power_ext_by_handle(state, creature, power_handle) {
        let count = state.player.counters.zero_cost_attacks_started_this_turn;
        state.power_extensions[ext_idx]
            .private_fields
            .insert(Cow::Borrowed("FeralPower._zeroCostAttacksPlayed"), FieldValue::Int(count));
    }
}

pub fn feral_modify_card_play_result_location(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
    power_handle: u32,
    card_h: CardHandle,
    energy_value: i32,
    location: (PileType, CardPilePosition),
) -> Result<(PileType, CardPilePosition), KernelError> {
    if creature != state.player.creature_handle || energy_value > 0 {
        return Ok(location);
    }
    let Some(ci) = state.card(card_h) else {
        return Ok(location);
    };
    if ci.temp_flags.contains(CardTempFlags::IS_DUPE) {
        return Ok(location);
    }
    if content.card_meta(ci.id)?.card_type != CardType::Attack {
        return Ok(location);
    }
    let Some(p) = state
        .creature(creature)
        .and_then(|c| c.powers.iter().find(|pw| pw.handle == power_handle))
    else {
        return Ok(location);
    };
    let played = p
        .ext_index
        .and_then(|e| state.power_extensions.get(e as usize))
        .and_then(|ext| ext.private_fields.get("FeralPower._zeroCostAttacksPlayed"))
        .and_then(|fv| match fv {
            FieldValue::Int(v) => Some(*v),
            _ => None,
        })
        .unwrap_or(0);
    if played >= p.amount {
        return Ok(location);
    }
    Ok((PileType::Hand, CardPilePosition::Top))
}

pub fn juggling_after_applied(state: &mut BattleState, creature: CreatureHandle, power_handle: u32) {
    if let Some(ext_idx) = ensure_power_ext_by_handle(state, creature, power_handle) {
        let count = state.player.counters.attack_cards_started_this_turn;
        state.power_extensions[ext_idx].private_fields.insert(
            Cow::Borrowed("JugglingPower._attacksPlayedThisTurn"),
            FieldValue::Int(count),
        );
    }
}

pub fn lullaby_after_applied(state: &mut BattleState, creature: CreatureHandle, power_handle: u32) {
    if creature == state.player.creature_handle && all_three_melodies_played(state) {
        if let Some(ext_idx) = ensure_power_ext_by_handle(state, creature, power_handle) {
            state.power_extensions[ext_idx]
                .private_fields
                .insert(Cow::Borrowed("LullabyPower._proccdThisTurn"), FieldValue::Bool(true));
        }
    }
}

pub fn illusion_after_applied(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    let has_minion = state
        .creature(creature)
        .is_some_and(|c| c.powers.iter().any(|pw| pw.id == PowerId::MinionPower));
    if !has_minion {
        power::apply(state, content, PowerId::MinionPower, creature, Dec::ONE, None, None)?;
    }
    Ok(())
}

pub const fn is_temporary_power(power_id: PowerId) -> bool {
    matches!(
        power_id,
        PowerId::AnticipatePower
            | PowerId::ChoBarbsPower
            | PowerId::CoordinatePower
            | PowerId::CrushUnderPower
            | PowerId::DarkShacklesPower
            | PowerId::DyingStarPower
            | PowerId::EnfeeblingTouchPower
            | PowerId::FadePower
            | PowerId::FeedingFrenzyPower
            | PowerId::FlexPotionPower
            | PowerId::FocusedStrikePower
            | PowerId::HelicalDartPower
            | PowerId::HotfixPower
            | PowerId::HyperbeamFocusDownPower
            | PowerId::ManglePower
            | PowerId::MonarchsGazeStrengthDownPower
            | PowerId::PiercingWailPower
            | PowerId::ReptileTrinketPower
            | PowerId::SetupStrikePower
            | PowerId::ShacklingPotionPower
            | PowerId::SpeedPotionPower
            | PowerId::SynchronizePower
    )
}

pub fn illusion_should_power_be_removed_on_death(
    content: &dyn ContentDispatcher,
    power_id: PowerId,
) -> Result<bool, KernelError> {
    let meta = content.power_meta(power_id)?;
    if meta.power_type == PowerType::Debuff {
        return Ok(!is_temporary_power(power_id));
    }
    Ok(false)
}

pub fn burrowed_after_removed(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    old_owner: CreatureHandle,
) -> Result<(), KernelError> {
    let Some(c) = state.creature(old_owner) else {
        return Ok(());
    };
    if c.is_dead() || !state.is_live_combat() || c.block <= 0 {
        return Ok(());
    }
    block::lose_block(state, content, old_owner, Dec::from_i32(c.block))
}

pub fn dampen_add_caster(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
    caster: CreatureHandle,
) {
    if let Some(ext_idx) = ensure_power_ext_by_handle(state, creature, power_handle) {
        let entry = state.power_extensions[ext_idx]
            .private_fields
            .entry(Cow::Borrowed("DampenPower._casters"))
            .or_insert_with(|| FieldValue::CreatureHandleArray(Vec::new()));
        if let FieldValue::CreatureHandleArray(arr) | FieldValue::IntArray(arr) = entry {
            let v = caster.0 as i32;
            if !arr.contains(&v) {
                arr.push(v);
            }
        }
    }
}

fn dampen_after_removed_by_ext(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    ext_index: Option<u32>,
) -> Result<(), KernelError> {
    let Some(ext_idx) = ext_index else {
        return Ok(());
    };
    let downgraded = match state.power_extensions[ext_idx as usize]
        .private_fields
        .get("DampenPower._downgraded")
    {
        Some(FieldValue::CardIntMap(map)) => map.clone(),
        _ => Vec::new(),
    };
    for (card_h, level) in downgraded {
        for _ in 0..level {
            card::upgrade(state, content, CardHandle(card_h as u32))?;
        }
    }
    state.power_extensions[ext_idx as usize]
        .private_fields
        .remove("DampenPower._downgraded");
    Ok(())
}

pub fn dampen_after_removed(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
    power_handle: u32,
) -> Result<(), KernelError> {
    let ext_index = state
        .creature(creature)
        .and_then(|c| c.powers.iter().find(|pw| pw.handle == power_handle))
        .and_then(|pw| pw.ext_index);
    dampen_after_removed_by_ext(state, content, ext_index)
}

pub fn hex_after_removed(state: &mut BattleState, creature: CreatureHandle) {
    if creature != state.player.creature_handle {
        return;
    }
    let afflicted: Vec<CardHandle> = state
        .cards
        .iter()
        .filter(|ci| {
            !ci.removed_from_state
                && !matches!(ci.pile, PileType::None | PileType::Deck)
                && ci.affliction.is_some_and(|a| a.id == AfflictionId::Hexed)
        })
        .map(|ci| ci.handle)
        .collect();
    for ch in afflicted {
        card::clear_affliction(state, ch);
    }
}

pub fn dark_embrace_after_card_exhausted(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
    amount: i32,
    caused_by_ethereal: bool,
    power_handle: u32,
) -> Result<(), KernelError> {
    if creature != state.player.creature_handle {
        return Ok(());
    }
    if caused_by_ethereal {
        if let Some(ext_idx) = ensure_power_ext_by_handle(state, creature, power_handle) {
            let cur = match state.power_extensions[ext_idx]
                .private_fields
                .get("DarkEmbracePower._etherealExhausts")
            {
                Some(FieldValue::Int(v)) => *v,
                _ => 0,
            };
            state.power_extensions[ext_idx]
                .private_fields
                .insert(Cow::Borrowed("DarkEmbracePower._etherealExhausts"), FieldValue::Int(cur + 1));
        }
    } else {
        let _ = card_pile::draw(state, content, amount, false)?;
    }
    Ok(())
}

pub fn imbalanced_after_damage_given(
    state: &mut BattleState,
    creature: CreatureHandle,
    dealer: Option<CreatureHandle>,
    was_fully_blocked: bool,
) -> Result<(), KernelError> {
    if dealer == Some(creature) && was_fully_blocked {
        let is_bowlbug =
            state.creature(creature).and_then(|c| c.monster_id) == Some(MonsterId::BowlbugRock);
        if is_bowlbug {
            if let Some(c) = state.creature_mut(creature) {
                if let Some(m) = &mut c.monster {
                    m.private_fields
                        .insert(Cow::Borrowed("BowlbugRock._isOffBalance"), FieldValue::Bool(true));
                }
            }
        } else {
            creature_cmd::stun(state, creature, None)?;
        }
    }
    Ok(())
}

pub fn all_is_well_on_card_generated(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
    amount: i32,
    card_h: CardHandle,
    creator: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    if creator == Some(creature) && state.card(card_h).is_some_and(|c| c.id == CardId::Sadness) {
        block::gain_block(
            state,
            content,
            creature,
            Dec::from_i32(amount),
            ValueProp::UNPOWERED,
            None,
            None,
        )?;
    }
    Ok(())
}

pub fn arsenal_on_card_generated(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    creature: CreatureHandle,
    amount: i32,
    creator: Option<CreatureHandle>,
) -> Result<(), KernelError> {
    if creator == Some(creature) {
        power::apply(
            state,
            content,
            PowerId::StrengthPower,
            creature,
            Dec::from_i32(amount),
            Some(creature),
            None,
        )?;
    }
    Ok(())
}

#[inline]
pub fn chains_of_binding_can_afflict_on_draw(
    state: &BattleState,
    creature: CreatureHandle,
    owner_side: CombatSide,
    amount: i32,
    card_h: CardHandle,
) -> bool {
    if amount <= 0 || creature != state.player.creature_handle || state.current_side != owner_side {
        return false;
    }
    if state.card(card_h).is_none_or(|c| c.affliction.is_some()) {
        return false;
    }
    let tk = state.current_turn_key();
    let mut count = 0i32;
    for (kind, turn, _) in &state.semantic_history.actor_events {
        if kind.as_ref() == "BoundAfflicted" && *turn == tk {
            count += 1;
            if count >= amount {
                return false;
            }
        }
    }
    true
}

pub fn adaptable_do_revive(state: &mut BattleState, creature: CreatureHandle, power_handle: u32) {
    if let Some(ext_idx) = ensure_power_ext_by_handle(state, creature, power_handle) {
        state.power_extensions[ext_idx]
            .private_fields
            .insert(Cow::Borrowed("AdaptablePower._isReviving"), FieldValue::Bool(false));
    }
}

pub fn crimson_mantle_increment_self_damage(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
) -> Result<(), KernelError> {
    if let Some(ext_idx) = ensure_power_ext_by_handle(state, creature, power_handle) {
        let cur = match state.power_extensions[ext_idx]
            .private_fields
            .get("CrimsonMantlePower._selfDamage")
        {
            Some(FieldValue::Decimal(d)) => *d,
            _ => Dec::ZERO,
        };
        state.power_extensions[ext_idx].private_fields.insert(
            Cow::Borrowed("CrimsonMantlePower._selfDamage"),
            FieldValue::Decimal(cur.checked_add(Dec::ONE)?),
        );
    }
    Ok(())
}

pub fn inferno_increment_self_damage(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
) -> Result<(), KernelError> {
    if let Some(ext_idx) = ensure_power_ext_by_handle(state, creature, power_handle) {
        let cur = match state.power_extensions[ext_idx]
            .private_fields
            .get("InfernoPower._selfDamage")
        {
            Some(FieldValue::Decimal(d)) => *d,
            _ => Dec::ZERO,
        };
        state.power_extensions[ext_idx].private_fields.insert(
            Cow::Borrowed("InfernoPower._selfDamage"),
            FieldValue::Decimal(cur.checked_add(Dec::ONE)?),
        );
    }
    Ok(())
}

pub fn ennui_increment_sadness_amount(
    state: &mut BattleState,
    creature: CreatureHandle,
    power_handle: u32,
) {
    if let Some(ext_idx) = ensure_power_ext_by_handle(state, creature, power_handle) {
        let cur = match state.power_extensions[ext_idx]
            .private_fields
            .get("EnnuiPower._sadnessAmount")
        {
            Some(FieldValue::Int(v)) => *v,
            _ => 0,
        };
        state.power_extensions[ext_idx]
            .private_fields
            .insert(Cow::Borrowed("EnnuiPower._sadnessAmount"), FieldValue::Int(cur + 1));
    }
}

pub fn intercept_after_removed(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    old_owner: CreatureHandle,
) -> Result<(), KernelError> {
    let teammates: SmallVec<[CreatureHandle; 8]> = state.ally_handles.iter().copied().collect();
    for ch in teammates {
        let handles: SmallVec<[u32; 4]> = state
            .creature(ch)
            .map(|c| {
                c.powers
                    .iter()
                    .filter(|pw| {
                        pw.id == PowerId::CoveredPower && pw.applier_handle == Some(old_owner)
                    })
                    .map(|pw| pw.handle)
                    .collect()
            })
            .unwrap_or_default();
        for ph in handles {
            let _ = power::remove_by_handle(state, content, ch, ph)?;
        }
    }
    Ok(())
}
