use crate::commands::autoplay::PrepaidXCapture;
use crate::commands::{card_pile, card_select, orb, potion};
use crate::content_dispatch::{CardPlayContext, ContentDispatcher};
use crate::continuation::{
    CardPlayResources, ContinuationFrame, PendingContinuation, StepOutcome,
};
use crate::error::KernelError;
use crate::hooks;
use crate::ordering;
use crate::state::{
    BattleState, CardHandle, CardInst, CardTempFlags, CreatureHandle, OptCardHandle, OptI16,
};
use smallvec::SmallVec;
use sts2_types::{
    CardKeywords, CardPilePosition, CardStaticFlags, CardTags, CardType, CombatSide, Dec,
    HookSlot, PileType, PlayerTurnPhase, RunRngType,
};

pub const BASE_HAND_DRAW_COUNT: i32 = 5;

pub struct CombatEngine;

impl CombatEngine {
    pub fn check_win_condition(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
    ) -> Result<bool, KernelError> {
        if !state.is_in_progress {
            return Ok(true);
        }
        if hooks::has_lost(state) {
            state.is_in_progress = false;
            state.won = false;
            return Ok(true);
        }
        if !hooks::has_won_raw(state) || hooks::should_stop_combat_from_ending(state, content)? {
            return Ok(false);
        }
        state.is_in_progress = false;
        state.won = true;
        Ok(true)
    }

    pub fn start_combat(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
    ) -> Result<StepOutcome, KernelError> {
        state.is_starting = true;
        state.initial_encounter_monster_count = state.enemy_handles.len();
        state.player.cumulative_hp_lost = 0;
        if let Some(pc) = state.player_creature_mut() {
            pc.cumulative_hp_lost = 0;
        }
        state.player.hand.clear();
        state.player.draw.clear();
        state.player.discard.clear();
        state.player.exhaust.clear();
        state.player.play.clear();

        // Populate draw pile from deck
        let deck_handles = state.player.deck.clone();
        for deck_h in deck_handles {
            let deck_card = *state
                .card(deck_h)
                .ok_or_else(|| KernelError::InvalidState("missing deck card".to_owned()))?;
            let copy_handle = CardHandle(state.next_card_handle);
            state.next_card_handle += 1;
            let mut copy = CardInst {
                handle: copy_handle,
                deck_card_handle: OptCardHandle::some(deck_h),
                pile: PileType::Draw,
                ext_index: None,
                temp_star_cost_override_this_turn: OptI16::NONE,
                temp_cost_override_this_turn: OptI16::NONE,
                temp_cost_override_this_turn_or_until_played: OptI16::NONE,
                temp_cost_override_this_combat: OptI16::NONE,
                temp_flags: deck_card.temp_flags
                    & !(CardTempFlags::TEMPORARY_SLY_TURN
                        | CardTempFlags::TEMPORARY_FREE_TURN
                        | CardTempFlags::TEMPORARY_RETAIN_TURN
                        | CardTempFlags::TEMPORARY_FREE_PLAY
                        | CardTempFlags::TEMPORARY_FREE_COMBAT),
                ..deck_card
            };
            if let Some(ext) = state.card_ext(deck_card.ext_index).cloned() {
                let idx = state.card_extensions.len() as u32;
                state.card_extensions.push(ext);
                copy.ext_index = Some(idx);
            }
            state.cards.push(copy);
            state.player.draw.push(copy_handle);
        }
        // Initial shuffle uses UnstableShuffle on Shuffle RNG
        let mut shuffle_rng = *state.run_rngs.get(RunRngType::Shuffle);
        shuffle_rng.unstable_shuffle(&mut state.player.draw);
        *state.run_rngs.get_mut(RunRngType::Shuffle) = shuffle_rng;

        // Initial shuffle order hook for enchantments
        let mut initial_order: SmallVec<[CardHandle; 64]> = state.player.draw.iter().copied().collect();
        if !state.card_extensions.is_empty() {
            let enchanted_cards: SmallVec<[CardHandle; 8]> = initial_order
                .iter()
                .copied()
                .filter(|&h| {
                    state
                        .card(h)
                        .and_then(|c| state.card_ext(c.ext_index))
                        .and_then(|e| e.enchantment.as_ref())
                        .is_some()
                })
                .collect();
            if !enchanted_cards.is_empty() {
                for ch in enchanted_cards {
                    content.enchantment_modify_shuffle_order(state, ch, &mut initial_order, true)?;
                }
                state.player.draw = initial_order.into_iter().collect();
            }
        }
        state.invalidate_hook_cache();

        // Setup enemies
        let enemy_handles: SmallVec<[CreatureHandle; 8]> =
            SmallVec::from_slice(&state.enemy_handles);
        for eh in &enemy_handles {
            content.monster_setup_for_combat(state, *eh)?;
        }
        for eh in &enemy_handles {
            content.monster_after_added_to_room(state, *eh)?;
        }

        hooks::after_room_entered(state, content)?;
        state.is_in_progress = true;
        state.is_starting = false;
        hooks::before_combat_start(state, content)?;
        Self::start_turn(state, content, false)
    }

    pub fn start_turn(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        is_extra_turn: bool,
    ) -> Result<StepOutcome, KernelError> {
        state.is_player_extra_turn = is_extra_turn;
        let participants: SmallVec<[CreatureHandle; 8]> = if is_extra_turn {
            smallvec::smallvec![state.player.creature_handle]
        } else if state.current_side == CombatSide::Player {
            SmallVec::from_slice(&state.ally_handles)
        } else {
            SmallVec::from_slice(&state.enemy_handles)
        };

        for &ch in &participants {
            if let Some(c) = state.creature_mut(ch) {
                for p in &mut c.powers {
                    p.amount_on_turn_start = p.amount;
                }
            }
        }

        let current_side = state.current_side;
        hooks::before_side_turn_start(state, content, current_side, &participants)?;

        if current_side == CombatSide::Player && !is_extra_turn {
            let enemies: SmallVec<[CreatureHandle; 8]> =
                SmallVec::from_slice(&state.enemy_handles);
            for eh in enemies {
                let alive = state.creature(eh).is_some_and(|c| c.is_alive());
                let keep_dead = !hooks::should_creature_be_removed_after_death(state, content, eh)?;
                if alive || keep_dead {
                    let stunned_action = {
                        state.creature(eh).and_then(|c| c.monster.as_ref()).and_then(|m| {
                            if m.next_move_id.as_deref() == Some("STUNNED")
                                && !m.next_move_can_transition_away
                            {
                                Some(None)
                            } else if m.current_state_id == "STUNNED" && m.next_move_can_transition_away
                            {
                                Some(Some(m.transient_move_follow_up_state_id.clone()))
                            } else {
                                None
                            }
                        })
                    };
                    match stunned_action {
                        Some(None) => {}
                        Some(Some(follow_up)) => {
                            let needs_roll = if let Some(c) = state.creature_mut(eh) {
                                if let Some(m) = &mut c.monster {
                                    m.transient_move_follow_up_state_id = None;
                                    match follow_up {
                                        Some(f) if !f.is_empty() => {
                                            m.current_state_id = f.clone();
                                            if f != "INIT_MOVE"
                                                && !f.ends_with("_BRANCH")
                                                && !f.ends_with("_RAND")
                                            {
                                                m.next_move_id = Some(f.clone());
                                                m.next_move_can_transition_away = true;
                                                m.state_log_ids.push(f);
                                                false
                                            } else {
                                                m.next_move_id = None;
                                                m.next_move_can_transition_away = true;
                                                true
                                            }
                                        }
                                        _ => {
                                            m.next_move_id = None;
                                            m.next_move_can_transition_away = true;
                                            true
                                        }
                                    }
                                } else {
                                    false
                                }
                            } else {
                                false
                            };
                            if needs_roll {
                                content.monster_roll_move(state, eh)?;
                            }
                        }
                        None => content.monster_roll_move(state, eh)?,
                    }
                }
            }
        }

        for &ch in &participants {
            let is_first_player_turn =
                current_side == CombatSide::Player && state.player.turn_number == 1;
            if !is_first_player_turn {
                if let Some(preventer) = hooks::should_clear_block(state, content, ch)? {
                    hooks::after_preventing_block_clear(state, content, preventer, ch)?;
                } else if let Some(c) = state.creature_mut(ch) {
                    c.block = 0;
                    if ch == state.player.creature_handle {
                        state.player.block = 0;
                    }
                }
            }
        }

        for &ch in &participants {
            hooks::after_block_cleared(state, content, ch)?;
        }

        if current_side == CombatSide::Player {
            return Self::setup_player_turn(state, content, is_extra_turn);
        }

        // Enemy side
        hooks::after_side_turn_start(state, content, current_side, &participants)?;
        if Self::check_win_condition(state, content)? {
            return Ok(StepOutcome::CombatEnded { won: state.won });
        }
        Self::execute_enemy_turn(state, content)
    }

    fn setup_player_turn(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        is_extra_turn: bool,
    ) -> Result<StepOutcome, KernelError> {
        state.player.phase = PlayerTurnPhase::Start;
        let max_energy = hooks::modify_max_energy(
            state,
            content,
            Dec::from_i32(state.player.max_energy),
        )?
        .to_i32_trunc()?;
        if hooks::should_player_reset_energy(state, content)? {
            state.player.energy = max_energy;
        } else {
            state.player.energy += max_energy;
        }
        hooks::after_energy_reset(state, content)?;
        Self::reset_melody_turn_counters(state);
        if let Some((req, paused_listener, remaining_listeners)) =
            hooks::before_hand_draw(state, content)?
        {
            state.pending = Some(PendingContinuation {
                request: req.clone(),
                frames: smallvec::smallvec![ContinuationFrame::BeforeHandDrawResume {
                    paused_listener,
                    remaining_listeners,
                    is_extra_turn,
                }],
            });
            return Ok(StepOutcome::NeedsChoice(req));
        }

        Self::finish_player_turn_start(state, content, is_extra_turn)
    }

    fn finish_player_turn_start(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        is_extra_turn: bool,
    ) -> Result<StepOutcome, KernelError> {
        let (mut hand_draw, modifiers) = hooks::modify_hand_draw(
            state,
            content,
            Dec::from_i32(BASE_HAND_DRAW_COUNT),
        )?;
        hooks::after_modifying_hand_draw(state, content, &modifiers)?;

        if state.player.turn_number == 1 {
            let innate_count = Self::arrange_draw_pile_for_first_hand(state, content)?;
            hand_draw = hand_draw
                .max(Dec::from_i32(innate_count as i32))
                .min(Dec::from_i32(card_pile::MAX_CARDS_IN_HAND as i32));
        }
        let draw_count = hand_draw.to_i32_trunc()?;
        let _ = card_pile::draw(state, content, draw_count, true)?;
        if let Some(pending) = state.pending.as_mut() {
            if let Some(ContinuationFrame::AfterShuffleResume {
                resume_turn_start, ..
            }) = pending.frames.last_mut()
            {
                *resume_turn_start = Some(is_extra_turn);
            }
            return Ok(StepOutcome::NeedsChoice(pending.request.clone()));
        }

        Self::run_after_player_turn_start(state, content, is_extra_turn)
    }

    fn run_after_player_turn_start(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        is_extra_turn: bool,
    ) -> Result<StepOutcome, KernelError> {
        if let Some((req, paused_listener, remaining_listeners)) =
            hooks::after_player_turn_start(state, content)?
        {
            state.pending = Some(PendingContinuation {
                request: req.clone(),
                frames: smallvec::smallvec![ContinuationFrame::AfterPlayerTurnStartResume {
                    paused_listener,
                    remaining_listeners,
                    is_extra_turn,
                }],
            });
            return Ok(StepOutcome::NeedsChoice(req));
        }
        Self::finish_after_player_turn_start(state, content, is_extra_turn)
    }

    fn finish_after_player_turn_start(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        is_extra_turn: bool,
    ) -> Result<StepOutcome, KernelError> {
        let participants: SmallVec<[CreatureHandle; 8]> = if is_extra_turn {
            smallvec::smallvec![state.player.creature_handle]
        } else {
            state.ally_handles.iter().copied().collect()
        };
        hooks::after_player_turn_start_late(state, content)?;
        hooks::after_side_turn_start(state, content, CombatSide::Player, &participants)?;
        let orb_count = state.player.orbs.len();
        for i in 0..orb_count {
            if !state.is_in_progress {
                break;
            }
            content.orb_after_turn_start(state, i)?;
        }
        if state.player_creature().is_some_and(|c| c.is_alive()) {
            state.player.phase = PlayerTurnPhase::AutoPrePlay;
            card_pile::check_for_empty_hand(state, content)?;
            if hooks::after_auto_pre_play_phase_entered(state, content)? {
                let req = state
                    .pending
                    .as_ref()
                    .ok_or_else(|| {
                        KernelError::InvalidState(
                            "after_auto_pre_play_phase_entered paused without pending continuation"
                                .to_owned(),
                        )
                    })?
                    .request
                    .clone();
                return Ok(StepOutcome::NeedsChoice(req));
            }
            state.player.phase = PlayerTurnPhase::Play;
        }
        Self::finish_action_boundary(state, content)
    }

    fn arrange_draw_pile_for_first_hand(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
    ) -> Result<usize, KernelError> {
        let mut bottom: SmallVec<[CardHandle; 4]> = SmallVec::new();
        for &h in &state.player.draw {
            if let Some(c) = state.card(h) {
                if let Some(ench_id) = state
                    .card_ext(c.ext_index)
                    .and_then(|e| e.enchantment.as_ref().map(|en| en.id))
                {
                    if content.enchantment_should_start_at_bottom(ench_id)? {
                        bottom.push(h);
                    }
                }
            }
        }
        for &h in &bottom {
            state.player.draw.retain(|x| *x != h);
            state.player.draw.push(h);
        }

        let mut innate: SmallVec<[CardHandle; 8]> = SmallVec::new();
        for &h in &state.player.draw {
            if bottom.contains(&h) {
                continue;
            }
            if let Some(c) = state.card(h) {
                if c.has_local_keyword(CardKeywords::INNATE) {
                    innate.push(h);
                }
            }
        }
        for &h in &innate {
            state.player.draw.retain(|x| *x != h);
            state.player.draw.insert(0, h);
        }
        state.invalidate_hook_cache();
        Ok(innate.len())
    }

    pub fn can_play_card(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        card_handle: CardHandle,
    ) -> Result<bool, KernelError> {
        Ok(Self::can_play_card_with_costs(state, content, card_handle)?.is_some())
    }

    pub fn can_play_card_with_costs(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        card_handle: CardHandle,
    ) -> Result<Option<(i32, i32)>, KernelError> {
        let active = !hooks::is_over_or_ending_checked(state, content)?;
        Self::can_play_card_with_costs_inner(state, content, card_handle, active)
    }

    /// Fast variant for callers that already verified `!hooks::is_over_or_ending_checked(state, content)?`.
    #[inline]
    pub fn can_play_card_with_costs_in_progress(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        card_handle: CardHandle,
    ) -> Result<Option<(i32, i32)>, KernelError> {
        #[cfg(debug_assertions)]
        debug_assert!(!hooks::is_over_or_ending_checked(state, content)?);
        let Some(card) = state.card(card_handle).copied() else {
            return Ok(None);
        };
        Self::can_play_card_with_costs_card(state, content, card, true)
    }

    /// Fast variant using a pre-resolved `card_idx` into `state.cards`.
    #[inline]
    pub fn can_play_card_with_costs_in_progress_at(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        card_handle: CardHandle,
        card_idx: usize,
    ) -> Result<Option<(i32, i32)>, KernelError> {
        #[cfg(debug_assertions)]
        {
            debug_assert!(!hooks::is_over_or_ending_checked(state, content)?);
            debug_assert_eq!(state.cards[card_idx].handle, card_handle);
        }
        let _ = card_handle;
        let card = state.cards[card_idx];
        Self::can_play_card_with_costs_card(state, content, card, true)
    }

    #[inline]
    fn can_play_card_with_costs_inner(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        card_handle: CardHandle,
        active_combat: bool,
    ) -> Result<Option<(i32, i32)>, KernelError> {
        let Some(card) = state.card(card_handle).copied() else {
            return Ok(None);
        };
        Self::can_play_card_with_costs_card(state, content, card, active_combat)
    }

    #[inline]
    fn can_play_card_with_costs_card(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        card: crate::state::CardInst,
        active_combat: bool,
    ) -> Result<Option<(i32, i32)>, KernelError> {
        let card_handle = card.handle;
        let card_id = card.id;
        let pile = card.pile;
        let base_kw = card.effective_keywords_base();
        if pile != PileType::Hand {
            return Ok(None);
        }
        if base_kw.contains(CardKeywords::UNPLAYABLE) {
            return Ok(None);
        }
        state.ensure_hook_cache(content)?;
        let cache_ok =
            state.hook_cache.dirty_mask == 0 && state.hook_cache.cached_tables.is_some();
        let comb = state.hook_cache.masks.combat_combined;
        if (!cache_ok || comb.contains(HookSlot::TryModifyKeywordsInCombat))
            && hooks::modify_keywords_in_combat_unchecked_active(
                state,
                content,
                card_handle,
                base_kw,
            )?
            .contains(CardKeywords::UNPLAYABLE)
        {
            return Ok(None);
        }

        let meta = state.fast_card_meta(content, card_id)?;
        let is_x_energy = meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST);
        let local_energy = ordering::resolve_local_energy_cost_card(state, &card, is_x_energy);
        let energy_cost = if is_x_energy {
            local_energy
        } else if active_combat
            && (!cache_ok
                || comb.contains(HookSlot::TryModifyEnergyCostInCombat)
                || comb.contains(HookSlot::TryModifyEnergyCostInCombatLate))
        {
            let (modified, was_modified) = if cache_ok && state.hook_cache.dirty_mask == 0 {
                hooks::modify_energy_cost_in_combat_cached_ready(
                    state,
                    content,
                    card_handle,
                    Dec::from_i32(local_energy),
                    comb,
                )?
            } else {
                hooks::modify_energy_cost_in_combat_unchecked_active(
                    state,
                    content,
                    card_handle,
                    Dec::from_i32(local_energy),
                )?
            };
            if was_modified {
                modified.max(Dec::ZERO).to_i32_trunc()?
            } else {
                local_energy
            }
        } else {
            local_energy
        };
        if state.player.energy < energy_cost {
            return Ok(None);
        }

        let star_cost = if meta.flags.contains(CardStaticFlags::IS_X_STAR_COST) {
            state.player.stars
        } else {
            let local_star =
                ordering::resolve_local_star_cost_card(&card, meta.canonical_star_cost);
            if active_combat && (!cache_ok || comb.contains(HookSlot::TryModifyStarCostInCombat)) {
                let (modified, was_modified) = hooks::modify_star_cost_in_combat_unchecked_active(
                    state,
                    content,
                    card_handle,
                    Dec::from_i32(local_star),
                )?;
                if was_modified {
                    modified.max(Dec::ZERO).to_i32_trunc()?
                } else {
                    local_star
                }
            } else {
                local_star
            }
        };
        if state.player.stars < star_cost {
            return Ok(None);
        }

        if active_combat
            && (!cache_ok || comb.contains(HookSlot::ShouldPlay))
            && !hooks::should_play_unchecked_active(state, content, card_handle)?
        {
            return Ok(None);
        }

        if meta.target_type == sts2_types::TargetType::AnyAlly {
            let has_ally = state.ally_handles.iter().any(|&h| {
                h != state.player.creature_handle
                    && state.creature(h).is_some_and(|c| c.is_alive())
            });
            if !has_ally {
                return Ok(None);
            }
        }

        let custom_playable = match state.hook_cache.cached_tables {
            Some(tables) => tables.card_has_custom_playability(card_id),
            None => true,
        };
        if !custom_playable || content.card_is_playable(state, card_handle)? {
            Ok(Some((energy_cost, star_cost)))
        } else {
            Ok(None)
        }
    }

    pub fn play_card_prechecked(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        card_handle: CardHandle,
        target: Option<CreatureHandle>,
        choices: &mut SmallVec<[Vec<usize>; 2]>,
    ) -> Result<StepOutcome, KernelError> {
        if state.pending.is_some() {
            return Err(KernelError::InvalidAction(
                "cannot play card while a choice is pending".to_owned(),
            ));
        }
        if !state.is_in_progress {
            return Ok(StepOutcome::CombatEnded { won: state.won });
        }
        #[cfg(debug_assertions)]
        debug_assert!(
            Self::can_play_card(state, content, card_handle)?,
            "play_card_prechecked called for unplayable {:?}",
            card_handle
        );

        let card_id = state
            .card(card_handle)
            .ok_or_else(|| KernelError::InvalidAction("card does not exist in battle state".to_owned()))?
            .id;
        let base_meta = state.fast_card_meta(content, card_id)?;
        let resolved_target_type =
            state.fast_card_resolved_target_type(content, card_handle, card_id, base_meta)?;
        if matches!(resolved_target_type, sts2_types::TargetType::AnyEnemy) {
            let valid_enemy = target.is_some_and(|th| {
                state.contains_creature(th) && state.creature(th).is_some_and(|c| c.is_alive())
            });
            if !valid_enemy {
                return Err(KernelError::InvalidAction(
                    "AnyEnemy card requires a living enemy target".to_owned(),
                ));
            }
        }

        Self::play_internal(state, content, card_handle, target, false, false, choices)
    }

    pub fn play_card(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        card_handle: CardHandle,
        target: Option<CreatureHandle>,
        choices: &mut SmallVec<[Vec<usize>; 2]>,
    ) -> Result<StepOutcome, KernelError> {
        if state.pending.is_some() {
            return Err(KernelError::InvalidAction(
                "cannot play card while a choice is pending".to_owned(),
            ));
        }
        if !state.is_in_progress {
            return Ok(StepOutcome::CombatEnded { won: state.won });
        }
        if !Self::can_play_card(state, content, card_handle)? {
            return Err(KernelError::InvalidAction(format!(
                "card {:?} is not playable",
                card_handle
            )));
        }

        let card_id = state
            .card(card_handle)
            .ok_or_else(|| KernelError::InvalidAction("card does not exist in battle state".to_owned()))?
            .id;
        let base_meta = state.fast_card_meta(content, card_id)?;
        let resolved_target_type =
            state.fast_card_resolved_target_type(content, card_handle, card_id, base_meta)?;
        if matches!(resolved_target_type, sts2_types::TargetType::AnyEnemy) {
            let valid_enemy = target.is_some_and(|th| {
                state.enemy_handles.contains(&th)
                    && state.creature(th).is_some_and(|c| c.is_alive())
            });
            if !valid_enemy {
                return Err(KernelError::InvalidAction(
                    "AnyEnemy card requires a living enemy target".to_owned(),
                ));
            }
        }

        Self::play_internal(state, content, card_handle, target, false, false, choices)
    }

    pub fn autoplay_card(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        card_handle: CardHandle,
        target: Option<CreatureHandle>,
        spend_resources: bool,
        choices: &mut SmallVec<[Vec<usize>; 2]>,
    ) -> Result<StepOutcome, KernelError> {
        if !hooks::should_play(state, content, card_handle)? {
            Self::move_to_result_pile_without_playing(state, content, card_handle)?;
            return Ok(StepOutcome::Completed);
        }
        if !spend_resources {
            let is_x = {
                let c = state.card(card_handle).unwrap();
                content
                    .card_meta(c.id)?
                    .flags
                    .contains(CardStaticFlags::IS_X_ENERGY_COST)
            };
            if is_x {
                let cur_e = state.player.energy;
                if let Some(c) = state.card_mut(card_handle) {
                    c.captured_energy_x_value = cur_e.clamp(i16::MIN as i32, i16::MAX as i32) as i16;
                }
            }
        }
        Self::play_internal_with_prepaid(
            state,
            content,
            card_handle,
            target,
            true,
            spend_resources,
            None,
            choices,
        )
    }

    pub fn autoplay_prepaid_card(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        card_handle: CardHandle,
        target: Option<CreatureHandle>,
        x_capture: PrepaidXCapture,
        choices: &mut SmallVec<[Vec<usize>; 2]>,
    ) -> Result<StepOutcome, KernelError> {
        Self::play_internal_with_prepaid(
            state,
            content,
            card_handle,
            target,
            true,
            false,
            Some(x_capture),
            choices,
        )
    }

    pub fn move_to_result_pile_without_playing(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        card_handle: CardHandle,
    ) -> Result<(), KernelError> {
        let (is_dupe, exhaust_next, pile) = match state.card(card_handle) {
            Some(c) => (
                c.temp_flags.contains(CardTempFlags::IS_DUPE),
                c.temp_flags.contains(CardTempFlags::EXHAUST_ON_NEXT_PLAY),
                c.pile,
            ),
            None => return Ok(()),
        };
        if is_dupe {
            card_pile::remove(state, card_handle);
            return Ok(());
        }
        let exhaust = exhaust_next
            || card_pile::card_has_keyword(state, content, card_handle, CardKeywords::EXHAUST)?;
        if exhaust {
            card_pile::exhaust(state, content, card_handle, false)?;
            return Ok(());
        }
        let departed_hand = pile == PileType::Hand;
        card_pile::add_sync(state, card_handle, PileType::Discard, CardPilePosition::Bottom);
        card_pile::notify_hand_departure(state, content, departed_hand)
    }

    fn play_internal(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        card_handle: CardHandle,
        target: Option<CreatureHandle>,
        is_auto_play: bool,
        spend_resources: bool,
        choices: &mut SmallVec<[Vec<usize>; 2]>,
    ) -> Result<StepOutcome, KernelError> {
        Self::play_internal_with_prepaid(
            state,
            content,
            card_handle,
            target,
            is_auto_play,
            spend_resources,
            None,
            choices,
        )
    }

    #[allow(clippy::too_many_arguments)]
    fn play_internal_with_prepaid(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        card_handle: CardHandle,
        target: Option<CreatureHandle>,
        is_auto_play: bool,
        spend_resources: bool,
        prepaid_x_capture: Option<PrepaidXCapture>,
        choices: &mut SmallVec<[Vec<usize>; 2]>,
    ) -> Result<StepOutcome, KernelError> {
        let original_owner_creature = state.player.creature_handle;
        let resolved_energy_cost = ordering::resolve_energy_cost(state, content, card_handle)?;
        let resolved_star_cost = ordering::resolve_star_cost(state, content, card_handle)?;
        let meta = {
            let c = state.card(card_handle).unwrap();
            content.card_meta(c.id)?
        };
        let is_x_energy = meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST);
        let energy_value = if is_x_energy {
            state.player.energy
        } else {
            resolved_energy_cost.max(0)
        };
        let star_value = resolved_star_cost.max(0);

        card_pile::add_sync(state, card_handle, PileType::Play, CardPilePosition::Bottom);

        let (energy_spent, stars_spent) = if is_auto_play && !spend_resources {
            (0, 0)
        } else {
            let e_spend = if is_x_energy {
                state.player.energy
            } else {
                resolved_energy_cost.min(state.player.energy).max(0)
            };
            if is_x_energy {
                if let Some(c) = state.card_mut(card_handle) {
                    c.captured_energy_x_value = e_spend.clamp(i16::MIN as i32, i16::MAX as i32) as i16;
                }
            }
            if e_spend > 0 && state.is_live_combat() {
                state.record_energy_spent(e_spend);
            }
            state.player.energy = (state.player.energy - e_spend).max(0);
            let s_spend = resolved_star_cost.min(state.player.stars).max(0);
            state.player.stars = (state.player.stars - s_spend).max(0);
            (e_spend, s_spend)
        };

        if energy_spent > 0 {
            hooks::after_energy_spent(state, content, card_handle, energy_spent)?;
        }
        if stars_spent > 0 {
            hooks::after_stars_spent(state, content, stars_spent)?;
        }

        let resources = CardPlayResources {
            energy_spent,
            energy_value,
            stars_spent,
            star_value,
            captured_energy_x: prepaid_x_capture.and_then(|cap| cap.energy),
            captured_star_x: prepaid_x_capture.and_then(|cap| cap.stars),
        };

        let (default_pile, default_pos) =
            Self::default_result_location(state, content, card_handle, is_auto_play)?;
        let (result_pile, result_pos, loc_mods) = hooks::modify_card_play_result_location(
            state,
            content,
            card_handle,
            is_auto_play,
            energy_value,
            default_pile,
            default_pos,
        )?;
        hooks::after_modifying_card_play_result_location(
            state,
            content,
            card_handle,
            is_auto_play,
            energy_value,
            result_pile,
            result_pos,
            &loc_mods,
        )?;

        let base_replay = state
            .card(card_handle)
            .map_or(0, |c| c.base_replay_count as i32);
        let has_ench = state
            .card(card_handle)
            .and_then(|c| state.card_ext(c.ext_index))
            .and_then(|e| e.enchantment.as_ref())
            .is_some();
        let enchanted_play_count = if has_ench {
            content.enchantment_play_count(state, card_handle, base_replay + 1)?
        } else {
            base_replay + 1
        };
        let (play_count, count_mods) = hooks::modify_card_play_count(
            state,
            content,
            card_handle,
            target,
            enchanted_play_count,
        )?;
        hooks::after_modifying_card_play_count(state, content, card_handle, &count_mods)?;

        if state.player_creature().is_none_or(|c| c.is_dead()) {
            return Self::finish_action_boundary(state, content);
        }

        if let Some(c) = state.card_mut(card_handle) {
            c.transform_into_after_play = None;
        }
        state.player.card_or_potion_effect_depth += 1;

        Self::run_card_play_loop(
            state,
            content,
            card_handle,
            original_owner_creature,
            target,
            0,
            play_count,
            is_auto_play,
            result_pile,
            result_pos,
            resources,
            0,
            choices,
        )
    }

    fn default_result_location(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        card_handle: CardHandle,
        is_auto_play: bool,
    ) -> Result<(PileType, CardPilePosition), KernelError> {
        let card_type = content.card_resolved_type(state, card_handle)?;
        let (is_dupe, exhaust_next) = {
            let c = state.card(card_handle).unwrap();
            (
                c.temp_flags.contains(CardTempFlags::IS_DUPE),
                c.temp_flags.contains(CardTempFlags::EXHAUST_ON_NEXT_PLAY),
            )
        };
        if is_dupe || card_type == CardType::Power {
            return Ok((PileType::None, CardPilePosition::Bottom));
        }
        if exhaust_next
            || card_pile::card_has_keyword(state, content, card_handle, CardKeywords::EXHAUST)?
        {
            if let Some(c) = state.card_mut(card_handle) {
                c.temp_flags.remove(CardTempFlags::EXHAUST_ON_NEXT_PLAY);
            }
            return Ok((PileType::Exhaust, CardPilePosition::Bottom));
        }
        if let Some(custom) =
            content.card_default_result_location(state, card_handle, is_auto_play)?
        {
            return Ok(custom);
        }
        Ok((PileType::Discard, CardPilePosition::Bottom))
    }

    fn try_transform_after_play(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        card_handle: CardHandle,
    ) -> Result<Option<CardHandle>, KernelError> {
        let Some(target_id) = state
            .card(card_handle)
            .and_then(|c| c.transform_into_after_play)
        else {
            return Ok(None);
        };
        let in_pile = state
            .card(card_handle)
            .is_some_and(|c| c.pile != PileType::None && !c.removed_from_state);
        if !in_pile {
            return Err(KernelError::InvalidState(
                "Card set transform_into_after_play but is no longer in a pile.".to_owned(),
            ));
        }
        let transformed =
            crate::commands::card::transform(state, content, card_handle, target_id)?;
        if let Some(c) = state.card_mut(card_handle) {
            c.transform_into_after_play = None;
        }
        Ok(transformed)
    }

    #[allow(clippy::too_many_arguments)]
    fn run_card_play_loop(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        card_handle: CardHandle,
        original_owner_creature: CreatureHandle,
        target: Option<CreatureHandle>,
        start_play_index: i32,
        play_count: i32,
        is_auto_play: bool,
        result_pile: PileType,
        result_pos: CardPilePosition,
        resources: CardPlayResources,
        resume_step: u8,
        choices: &mut SmallVec<[Vec<usize>; 2]>,
    ) -> Result<StepOutcome, KernelError> {
        let mut step = resume_step;
        let mut result_card = card_handle;
        for play_index in start_play_index..play_count {
            if step == 0 && hooks::is_over_or_ending_checked(state, content)? {
                break;
            }
            let ctx = CardPlayContext {
                card: card_handle,
                target,
                result_pile,
                is_auto_play,
                play_index,
                play_count,
                play_ordinal: state.player.counters.card_plays_started_this_turn + 1,
                energy_spent: resources.energy_spent,
                energy_value: resources.captured_energy_x.unwrap_or(resources.energy_value),
                stars_spent: resources.stars_spent,
                star_value: resources.captured_star_x.unwrap_or(resources.star_value),
            };

            if step == 0 {
                hooks::before_card_played(state, content, &ctx)?;
                Self::record_card_play_started(state, content, &ctx)?;
                let play_outcome = content.card_on_play(state, &ctx, choices, 0)?;
                if let Some(tid) = play_outcome.transform_into {
                    if let Some(c) = state.card_mut(card_handle) {
                        c.transform_into_after_play = Some(tid);
                    }
                }
                if let Some((req, next_step)) = play_outcome.needs_choice {
                    state.pending = Some(PendingContinuation {
                        request: req.clone(),
                        frames: smallvec::smallvec![ContinuationFrame::CardPlayResume {
                            card: card_handle,
                            original_owner_creature,
                            target,
                            play_index,
                            play_count,
                            is_auto_play,
                            result_pile,
                            result_position: result_pos,
                            resources,
                            on_play_resume_step: next_step,
                        }],
                    });
                    return Ok(StepOutcome::NeedsChoice(req));
                }
                if let Some(pending) = state.pending.as_mut() {
                    pending.frames.insert(
                        0,
                        ContinuationFrame::CardPlayResume {
                            card: card_handle,
                            original_owner_creature,
                            target,
                            play_index,
                            play_count,
                            is_auto_play,
                            result_pile,
                            result_position: result_pos,
                            resources,
                            on_play_resume_step: u8::MAX,
                        },
                    );
                    return Ok(StepOutcome::NeedsChoice(pending.request.clone()));
                }
            }
            step = 0;

            if Self::finish_single_card_play_iteration(
                state,
                content,
                card_handle,
                &ctx,
                is_auto_play,
            )? {
                return Self::finish_action_boundary(state, content);
            }
            if let Some(transformed) = Self::try_transform_after_play(state, content, card_handle)?
            {
                result_card = transformed;
                break;
            }
        }

        Self::conclude_card_play(
            state,
            content,
            card_handle,
            result_card,
            original_owner_creature,
            result_pile,
            result_pos,
        )
    }

    fn finish_single_card_play_iteration(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        card_handle: CardHandle,
        ctx: &CardPlayContext,
        is_auto_play: bool,
    ) -> Result<bool, KernelError> {
        if state.player_creature().is_none_or(|c| c.is_dead()) {
            state.player.card_or_potion_effect_depth =
                (state.player.card_or_potion_effect_depth - 1).max(0);
            return Ok(true);
        }

        let has_ench = state
            .card(card_handle)
            .and_then(|c| state.card_ext(c.ext_index))
            .and_then(|e| e.enchantment.as_ref())
            .is_some();
        if has_ench {
            content.enchantment_on_play(state, card_handle, ctx)?;
            if state.player_creature().is_none_or(|c| c.is_dead()) {
                state.player.card_or_potion_effect_depth =
                    (state.player.card_or_potion_effect_depth - 1).max(0);
                return Ok(true);
            }
        }

        let has_aff = state
            .card(card_handle)
            .and_then(|c| c.affliction)
            .is_some();
        if has_aff {
            content.affliction_on_play(state, card_handle, ctx)?;
            if state.player_creature().is_none_or(|c| c.is_dead()) {
                state.player.card_or_potion_effect_depth =
                    (state.player.card_or_potion_effect_depth - 1).max(0);
                return Ok(true);
            }
        }

        Self::record_card_played(state, content, card_handle, is_auto_play)?;
        if card_pile::card_has_keyword(state, content, card_handle, CardKeywords::ETHEREAL)? {
            state.record_actor_event("EtherealPlayFinished");
        }
        hooks::after_card_played(state, content, ctx)?;
        if state.player_creature().is_none_or(|c| c.is_dead()) {
            state.player.card_or_potion_effect_depth =
                (state.player.card_or_potion_effect_depth - 1).max(0);
            return Ok(true);
        }
        Ok(false)
    }

    fn conclude_card_play(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        card_handle: CardHandle,
        result_card: CardHandle,
        _original_owner_creature: CreatureHandle,
        result_pile: PileType,
        result_pos: CardPilePosition,
    ) -> Result<StepOutcome, KernelError> {
        state.player.card_or_potion_effect_depth =
            (state.player.card_or_potion_effect_depth - 1).max(0);

        if state
            .card(result_card)
            .is_some_and(|c| c.pile == PileType::Play)
        {
            match result_pile {
                PileType::None => card_pile::remove(state, result_card),
                PileType::Exhaust => {
                    card_pile::exhaust(state, content, result_card, false)?;
                }
                dest => {
                    card_pile::add_sync(state, result_card, dest, result_pos);
                }
            }
        }

        card_pile::check_for_empty_hand(state, content)?;
        Self::clear_after_play_modifiers(state, card_handle);
        if state.player.card_or_potion_effect_depth > 0
            || state.player.phase == PlayerTurnPhase::AutoPrePlay
        {
            if Self::check_win_condition(state, content)? {
                return Ok(StepOutcome::CombatEnded { won: state.won });
            }
            return Ok(StepOutcome::Completed);
        }
        Self::finish_action_boundary(state, content)
    }

    fn clear_after_play_modifiers(state: &mut BattleState, card_handle: CardHandle) {
        if let Some(c) = state.card_mut(card_handle) {
            c.temp_flags.remove(CardTempFlags::TEMPORARY_FREE_PLAY);
            c.temp_cost_override_this_turn_or_until_played = OptI16::NONE;
            c.temp_star_cost_override_this_turn = OptI16::NONE;
            if let Some(ext_idx) = c.ext_index {
                state.card_extensions[ext_idx as usize]
                    .local_energy_modifiers
                    .retain(|m| !m.kind.clears_after_play());
            }
        }
    }

    fn record_card_play_started(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        ctx: &CardPlayContext,
    ) -> Result<(), KernelError> {
        let card_type = content.card_resolved_type(state, ctx.card)?;
        let tags = {
            let c = state.card(ctx.card).unwrap();
            content.card_meta(c.id)?.base_tags
        };
        let ctr = &mut state.player.counters;
        ctr.card_plays_started_this_turn += 1;
        if card_type == CardType::Attack {
            ctr.attack_cards_started_this_turn += 1;
            if ctx.energy_value == 0 {
                ctr.zero_cost_attacks_started_this_turn += 1;
            }
        }
        if matches!(card_type, CardType::Attack | CardType::Skill) {
            ctr.attack_or_skill_card_plays_started_this_turn += 1;
        }
        if ctx.play_index == 0 {
            ctr.first_in_series_card_plays_started_this_turn += 1;
        }
        if tags.contains(CardTags::WAH) {
            ctr.wah_started_this_turn += 1;
            if ctr.first_wah_play_ordinal_this_turn == 0 {
                ctr.first_wah_play_ordinal_this_turn = ctx.play_ordinal;
            }
        }
        if tags.contains(CardTags::CHO) {
            ctr.cho_started_this_turn += 1;
            if ctr.first_cho_play_ordinal_this_turn == 0 {
                ctr.first_cho_play_ordinal_this_turn = ctx.play_ordinal;
            }
        }
        if tags.contains(CardTags::KI) {
            ctr.ki_started_this_turn += 1;
            if ctr.first_ki_play_ordinal_this_turn == 0 {
                ctr.first_ki_play_ordinal_this_turn = ctx.play_ordinal;
            }
        }
        Ok(())
    }

    fn record_card_played(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        card_handle: CardHandle,
        is_auto_play: bool,
    ) -> Result<(), KernelError> {
        let card_type = content.card_resolved_type(state, card_handle)?;
        let tags = {
            let c = state.card(card_handle).unwrap();
            content.card_meta(c.id)?.base_tags
        };
        let ctr = &mut state.player.counters;
        ctr.cards_played_this_combat += 1;
        ctr.cards_played_this_turn += 1;
        if !is_auto_play {
            ctr.manual_cards_played_this_turn += 1;
        }
        if card_type == CardType::Skill {
            ctr.skill_cards_played_this_turn += 1;
        } else if card_type == CardType::Attack {
            ctr.attack_cards_played_this_turn += 1;
        }
        if tags.contains(CardTags::SHIV) {
            ctr.shivs_played_this_turn += 1;
        }
        let has_wah = tags.contains(CardTags::WAH);
        let has_cho = tags.contains(CardTags::CHO);
        let has_ki = tags.contains(CardTags::KI);
        if has_wah {
            ctr.wah_finished_this_turn += 1;
        }
        if has_cho {
            ctr.cho_finished_this_turn += 1;
        }
        if has_ki {
            ctr.ki_finished_this_turn += 1;
        }
        if has_wah || has_cho || has_ki {
            ctr.melody_cards_finished_this_turn += 1;
        }
        Ok(())
    }

    pub fn resume_choice(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        selected_indices: &[usize],
    ) -> Result<StepOutcome, KernelError> {
        let Some(mut pending) = state.pending.take() else {
            return Err(KernelError::InvalidAction(
                "no pending choice to resume".to_owned(),
            ));
        };
        let req = &pending.request;
        let selected = match card_select::resolve_selection_indices(
            &req.candidates,
            req.min_count,
            req.max_count,
            req.can_skip,
            selected_indices,
        )? {
            card_select::SelectResult::Selected(s) => s,
            card_select::SelectResult::NeedsChoice(_) => unreachable!(),
        };

        let Some(frame) = pending.frames.pop() else {
            return Err(KernelError::InvalidState(
                "pending choice had no continuation frame".to_owned(),
            ));
        };

        match frame {
            ContinuationFrame::CardPlayResume {
                card,
                original_owner_creature,
                target,
                play_index,
                play_count,
                is_auto_play,
                result_pile,
                result_position,
                resources,
                on_play_resume_step,
            } => {
                let ctx = CardPlayContext {
                    card,
                    target,
                    result_pile,
                    is_auto_play,
                    play_index,
                    play_count,
                    play_ordinal: state.player.counters.card_plays_started_this_turn,
                    energy_spent: resources.energy_spent,
                    energy_value: resources.captured_energy_x.unwrap_or(resources.energy_value),
                    stars_spent: resources.stars_spent,
                    star_value: resources.captured_star_x.unwrap_or(resources.star_value),
                };
                let outcome = content.card_on_play_resume_after_select(
                    state,
                    &ctx,
                    &selected,
                    on_play_resume_step,
                )?;
                if let Some(tid) = outcome.transform_into {
                    if let Some(c) = state.card_mut(card) {
                        c.transform_into_after_play = Some(tid);
                    }
                }
                if let Some((next_req, next_step)) = outcome.needs_choice {
                    pending.request = next_req.clone();
                    pending.frames.push(ContinuationFrame::CardPlayResume {
                        card,
                        original_owner_creature,
                        target,
                        play_index,
                        play_count,
                        is_auto_play,
                        result_pile,
                        result_position,
                        resources,
                        on_play_resume_step: next_step,
                    });
                    state.pending = Some(pending);
                    return Ok(StepOutcome::NeedsChoice(next_req));
                }
                if let Some(mut inner) = state.pending.take() {
                    inner.frames.insert(
                        0,
                        ContinuationFrame::CardPlayResume {
                            card,
                            original_owner_creature,
                            target,
                            play_index,
                            play_count,
                            is_auto_play,
                            result_pile,
                            result_position,
                            resources,
                            on_play_resume_step: u8::MAX,
                        },
                    );
                    Self::prepend_outer_frames(&mut inner, pending.frames);
                    let req = inner.request.clone();
                    state.pending = Some(inner);
                    return Ok(StepOutcome::NeedsChoice(req));
                }

                if Self::finish_single_card_play_iteration(
                    state,
                    content,
                    card,
                    &ctx,
                    is_auto_play,
                )? {
                    if !pending.frames.is_empty() && !Self::check_win_condition(state, content)? {
                        return Self::unwind_outer_frames(state, content, pending.frames);
                    }
                    return Self::finish_action_boundary(state, content);
                }
                if let Some(transformed) = Self::try_transform_after_play(state, content, card)? {
                    let outcome = Self::conclude_card_play(
                        state,
                        content,
                        card,
                        transformed,
                        original_owner_creature,
                        result_pile,
                        result_position,
                    )?;
                    if matches!(outcome, StepOutcome::Completed) && !pending.frames.is_empty() {
                        return Self::unwind_outer_frames(state, content, pending.frames);
                    }
                    return Ok(outcome);
                }

                let outer_frames = pending.frames;
                let mut empty_choices = SmallVec::new();
                let outcome = Self::run_card_play_loop(
                    state,
                    content,
                    card,
                    original_owner_creature,
                    target,
                    play_index + 1,
                    play_count,
                    is_auto_play,
                    result_pile,
                    result_position,
                    resources,
                    0,
                    &mut empty_choices,
                )?;
                if matches!(outcome, StepOutcome::NeedsChoice(_)) {
                    if !outer_frames.is_empty() {
                        if let Some(p) = state.pending.as_mut() {
                            Self::prepend_outer_frames(p, outer_frames);
                        }
                    }
                    return Ok(outcome);
                }
                if matches!(outcome, StepOutcome::Completed) && !outer_frames.is_empty() {
                    return Self::unwind_outer_frames(state, content, outer_frames);
                }
                Ok(outcome)
            }
            ContinuationFrame::PotionUseResume {
                potion_slot,
                potion_id,
                target,
                resume_step,
            } => {
                let outer_frames = pending.frames;
                if let Some(mut next_pending) = potion::resume_potion_use(
                    state,
                    content,
                    potion_slot,
                    potion_id,
                    target,
                    &selected,
                    resume_step,
                )? {
                    Self::prepend_outer_frames(&mut next_pending, outer_frames);
                    let req = next_pending.request.clone();
                    state.pending = Some(next_pending);
                    return Ok(StepOutcome::NeedsChoice(req));
                }
                if !outer_frames.is_empty() {
                    return Self::unwind_outer_frames(state, content, outer_frames);
                }
                Self::finish_action_boundary(state, content)
            }
            ContinuationFrame::BeforeHandDrawResume {
                paused_listener,
                remaining_listeners,
                is_extra_turn,
            } => {
                let res = content.resume_hook_choice(
                    state,
                    paused_listener,
                    HookSlot::BeforeHandDraw,
                    &selected,
                )?;
                if let Some(next_req) = res.needs_choice {
                    state.pending = Some(PendingContinuation {
                        request: next_req.clone(),
                        frames: smallvec::smallvec![ContinuationFrame::BeforeHandDrawResume {
                            paused_listener,
                            remaining_listeners,
                            is_extra_turn,
                        }],
                    });
                    return Ok(StepOutcome::NeedsChoice(next_req));
                }
                if let Some((next_req, next_paused, rest)) =
                    hooks::continue_before_hand_draw(state, content, &remaining_listeners)?
                {
                    state.pending = Some(PendingContinuation {
                        request: next_req.clone(),
                        frames: smallvec::smallvec![ContinuationFrame::BeforeHandDrawResume {
                            paused_listener: next_paused,
                            remaining_listeners: rest,
                            is_extra_turn,
                        }],
                    });
                    return Ok(StepOutcome::NeedsChoice(next_req));
                }
                Self::finish_player_turn_start(state, content, is_extra_turn)
            }
            ContinuationFrame::AfterShuffleResume {
                paused_listener,
                remaining_listeners,
                remaining_draws,
                from_hand_draw,
                resume_turn_start,
            } => {
                let res = content.resume_hook_choice(
                    state,
                    paused_listener,
                    HookSlot::AfterShuffle,
                    &selected,
                )?;
                if let Some(next_req) = res.needs_choice {
                    pending.request = next_req.clone();
                    pending.frames.push(ContinuationFrame::AfterShuffleResume {
                        paused_listener,
                        remaining_listeners,
                        remaining_draws,
                        from_hand_draw,
                        resume_turn_start,
                    });
                    state.pending = Some(pending);
                    return Ok(StepOutcome::NeedsChoice(next_req));
                }
                if let Some((next_req, next_paused, rest)) =
                    hooks::continue_after_shuffle(state, content, &remaining_listeners)?
                {
                    pending.request = next_req.clone();
                    pending.frames.push(ContinuationFrame::AfterShuffleResume {
                        paused_listener: next_paused,
                        remaining_listeners: rest,
                        remaining_draws,
                        from_hand_draw,
                        resume_turn_start,
                    });
                    state.pending = Some(pending);
                    return Ok(StepOutcome::NeedsChoice(next_req));
                }
                if remaining_draws > 0 {
                    let _ = card_pile::draw(state, content, remaining_draws, from_hand_draw)?;
                    if let Some(mut inner) = state.pending.take() {
                        if let Some(ContinuationFrame::AfterShuffleResume {
                            resume_turn_start: rts,
                            ..
                        }) = inner.frames.last_mut()
                        {
                            *rts = resume_turn_start;
                        }
                        for (i, outer) in pending.frames.into_iter().enumerate() {
                            inner.frames.insert(i, outer);
                        }
                        let req = inner.request.clone();
                        state.pending = Some(inner);
                        return Ok(StepOutcome::NeedsChoice(req));
                    }
                }
                if let Some(is_extra_turn) = resume_turn_start {
                    return Self::run_after_player_turn_start(state, content, is_extra_turn);
                }
                if let Some(ContinuationFrame::CardPlayResume {
                    card,
                    original_owner_creature,
                    target,
                    play_index,
                    play_count,
                    is_auto_play,
                    result_pile,
                    result_position,
                    resources,
                    ..
                }) = pending.frames.pop()
                {
                    let ctx = CardPlayContext {
                        card,
                        target,
                        result_pile,
                        is_auto_play,
                        play_index,
                        play_count,
                        play_ordinal: state.player.counters.card_plays_started_this_turn,
                        energy_spent: resources.energy_spent,
                        energy_value: resources.captured_energy_x.unwrap_or(resources.energy_value),
                        stars_spent: resources.stars_spent,
                        star_value: resources.captured_star_x.unwrap_or(resources.star_value),
                    };
                    if Self::finish_single_card_play_iteration(
                        state,
                        content,
                        card,
                        &ctx,
                        is_auto_play,
                    )? {
                        if !pending.frames.is_empty() && !Self::check_win_condition(state, content)?
                        {
                            return Self::unwind_outer_frames(state, content, pending.frames);
                        }
                        return Self::finish_action_boundary(state, content);
                    }
                    if let Some(transformed) =
                        Self::try_transform_after_play(state, content, card)?
                    {
                        let outcome = Self::conclude_card_play(
                            state,
                            content,
                            card,
                            transformed,
                            original_owner_creature,
                            result_pile,
                            result_position,
                        )?;
                        if matches!(outcome, StepOutcome::Completed) && !pending.frames.is_empty() {
                            return Self::unwind_outer_frames(state, content, pending.frames);
                        }
                        return Ok(outcome);
                    }
                    let outer_frames = pending.frames;
                    let mut empty_choices = SmallVec::new();
                    let outcome = Self::run_card_play_loop(
                        state,
                        content,
                        card,
                        original_owner_creature,
                        target,
                        play_index + 1,
                        play_count,
                        is_auto_play,
                        result_pile,
                        result_position,
                        resources,
                        0,
                        &mut empty_choices,
                    )?;
                    if matches!(outcome, StepOutcome::NeedsChoice(_)) {
                        if !outer_frames.is_empty() {
                            if let Some(p) = state.pending.as_mut() {
                                Self::prepend_outer_frames(p, outer_frames);
                            }
                        }
                        return Ok(outcome);
                    }
                    if matches!(outcome, StepOutcome::Completed) && !outer_frames.is_empty() {
                        return Self::unwind_outer_frames(state, content, outer_frames);
                    }
                    return Ok(outcome);
                }
                if !pending.frames.is_empty() {
                    return Self::unwind_outer_frames(state, content, pending.frames);
                }
                Self::finish_action_boundary(state, content)
            }
            ContinuationFrame::AfterPlayerTurnStartResume {
                paused_listener,
                remaining_listeners,
                is_extra_turn,
            } => {
                let res = content.resume_hook_choice(
                    state,
                    paused_listener,
                    HookSlot::AfterPlayerTurnStart,
                    &selected,
                )?;
                if let Some(next_req) = res.needs_choice {
                    state.pending = Some(PendingContinuation {
                        request: next_req.clone(),
                        frames: smallvec::smallvec![ContinuationFrame::AfterPlayerTurnStartResume {
                            paused_listener,
                            remaining_listeners,
                            is_extra_turn,
                        }],
                    });
                    return Ok(StepOutcome::NeedsChoice(next_req));
                }
                if let Some((next_req, next_paused, rest)) =
                    hooks::continue_after_player_turn_start(state, content, &remaining_listeners)?
                {
                    state.pending = Some(PendingContinuation {
                        request: next_req.clone(),
                        frames: smallvec::smallvec![ContinuationFrame::AfterPlayerTurnStartResume {
                            paused_listener: next_paused,
                            remaining_listeners: rest,
                            is_extra_turn,
                        }],
                    });
                    return Ok(StepOutcome::NeedsChoice(next_req));
                }
                Self::finish_after_player_turn_start(state, content, is_extra_turn)
            }
            ContinuationFrame::EnemyTurnResume {
                current_enemy,
                remaining_enemies,
            } => {
                content.monster_resume_move_choice(state, current_enemy, &selected)?;
                if let Some(mut inner) = state.pending.take() {
                    inner.frames.insert(
                        0,
                        ContinuationFrame::EnemyTurnResume {
                            current_enemy,
                            remaining_enemies,
                        },
                    );
                    let req = inner.request.clone();
                    state.pending = Some(inner);
                    return Ok(StepOutcome::NeedsChoice(req));
                }
                if Self::check_win_condition(state, content)? {
                    return Ok(StepOutcome::CombatEnded { won: state.won });
                }
                Self::execute_enemy_turn_from(state, content, remaining_enemies)
            }
            _ => Err(KernelError::Unsupported {
                kind: "ContinuationFrame",
                id: "unsupported_resume_frame".to_owned(),
            }),
        }
    }

    pub fn use_potion(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        potion_slot: usize,
        target: Option<CreatureHandle>,
        choices: &mut SmallVec<[Vec<usize>; 2]>,
    ) -> Result<StepOutcome, KernelError> {
        if state.pending.is_some() {
            return Err(KernelError::InvalidAction(
                "cannot use potion while a choice is pending".to_owned(),
            ));
        }
        let outcome = potion::use_potion(state, content, potion_slot, target, choices)?;
        if !matches!(outcome, StepOutcome::Completed) {
            return Ok(outcome);
        }
        Self::finish_action_boundary(state, content)
    }

    fn prepend_outer_frames(
        pending: &mut PendingContinuation,
        outer: SmallVec<[ContinuationFrame; 4]>,
    ) {
        for (i, frame) in outer.into_iter().enumerate() {
            pending.frames.insert(i, frame);
        }
    }

    fn unwind_outer_frames(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        mut outer: SmallVec<[ContinuationFrame; 4]>,
    ) -> Result<StepOutcome, KernelError> {
        while let Some(frame) = outer.pop() {
            match frame {
                ContinuationFrame::AutoPlayResume {
                    remaining_cards,
                    fixed_target,
                    spend_resources,
                    force_exhaust,
                    move_to_play,
                } => {
                    let outcome = crate::commands::autoplay::resume_play_cards_core(
                        state,
                        content,
                        &remaining_cards,
                        fixed_target,
                        spend_resources,
                        force_exhaust,
                        move_to_play,
                    )?;
                    if matches!(outcome, StepOutcome::NeedsChoice(_)) {
                        if let Some(p) = state.pending.as_mut() {
                            Self::prepend_outer_frames(p, outer);
                        }
                        return Ok(outcome);
                    }
                    if matches!(outcome, StepOutcome::CombatEnded { .. }) {
                        return Ok(outcome);
                    }
                }
                ContinuationFrame::AfterAutoPrePlayResume {
                    remaining_listeners,
                    is_late,
                } => {
                    if hooks::continue_after_auto_pre_play_phase_entered(
                        state,
                        content,
                        &remaining_listeners,
                        is_late,
                    )? {
                        let req = {
                            let p = state.pending.as_mut().ok_or_else(|| {
                                KernelError::InvalidState(
                                    "AfterAutoPrePlayResume paused without pending continuation"
                                        .to_owned(),
                                )
                            })?;
                            Self::prepend_outer_frames(p, outer);
                            p.request.clone()
                        };
                        return Ok(StepOutcome::NeedsChoice(req));
                    }
                    if state.player_creature().is_some_and(|c| c.is_alive()) {
                        state.player.phase = PlayerTurnPhase::Play;
                    }
                }
                ContinuationFrame::CardPlayResume {
                    card,
                    original_owner_creature,
                    target,
                    play_index,
                    play_count,
                    is_auto_play,
                    result_pile,
                    result_position,
                    resources,
                    on_play_resume_step,
                } if on_play_resume_step == u8::MAX => {
                    let ctx = CardPlayContext {
                        card,
                        target,
                        result_pile,
                        is_auto_play,
                        play_index,
                        play_count,
                        play_ordinal: state.player.counters.card_plays_started_this_turn,
                        energy_spent: resources.energy_spent,
                        energy_value: resources.captured_energy_x.unwrap_or(resources.energy_value),
                        stars_spent: resources.stars_spent,
                        star_value: resources.captured_star_x.unwrap_or(resources.star_value),
                    };
                    if Self::finish_single_card_play_iteration(
                        state,
                        content,
                        card,
                        &ctx,
                        is_auto_play,
                    )? {
                        if Self::check_win_condition(state, content)? {
                            return Ok(StepOutcome::CombatEnded { won: state.won });
                        }
                        continue;
                    }
                    if let Some(transformed) =
                        Self::try_transform_after_play(state, content, card)?
                    {
                        let outcome = Self::conclude_card_play(
                            state,
                            content,
                            card,
                            transformed,
                            original_owner_creature,
                            result_pile,
                            result_position,
                        )?;
                        if matches!(outcome, StepOutcome::CombatEnded { .. }) {
                            return Ok(outcome);
                        }
                        continue;
                    }
                    let mut empty_choices = SmallVec::new();
                    let outcome = Self::run_card_play_loop(
                        state,
                        content,
                        card,
                        original_owner_creature,
                        target,
                        play_index + 1,
                        play_count,
                        is_auto_play,
                        result_pile,
                        result_position,
                        resources,
                        0,
                        &mut empty_choices,
                    )?;
                    if matches!(outcome, StepOutcome::NeedsChoice(_)) {
                        if let Some(p) = state.pending.as_mut() {
                            Self::prepend_outer_frames(p, outer);
                        }
                        return Ok(outcome);
                    }
                    if matches!(outcome, StepOutcome::CombatEnded { .. }) {
                        return Ok(outcome);
                    }
                }
                _ => {
                    return Err(KernelError::Unsupported {
                        kind: "ContinuationFrame",
                        id: "unsupported_outer_unwind_frame".to_owned(),
                    });
                }
            }
        }
        Self::finish_action_boundary(state, content)
    }

    fn finish_action_boundary(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
    ) -> Result<StepOutcome, KernelError> {
        if Self::check_win_condition(state, content)? {
            if state.hook_cache.cached_tables.is_none() {
                state.ensure_hook_cache(content)?;
            }
            return Ok(StepOutcome::CombatEnded { won: state.won });
        }
        if state.end_player_turn_requested {
            state.end_player_turn_requested = false;
            return Self::end_player_turn(state, content);
        }
        if state.hook_cache.cached_tables.is_none() {
            state.ensure_hook_cache(content)?;
        }
        Ok(StepOutcome::Completed)
    }

    pub fn end_player_turn(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
    ) -> Result<StepOutcome, KernelError> {
        if state.pending.is_some() {
            return Err(KernelError::InvalidAction(
                "cannot end turn while a choice is pending".to_owned(),
            ));
        }
        if !state.is_in_progress {
            return Ok(StepOutcome::CombatEnded { won: state.won });
        }
        state.end_player_turn_requested = false;
        state.player.phase = PlayerTurnPhase::AutoPostPlay;
        hooks::after_auto_post_play_phase_entered(state, content)?;
        state.player.phase = PlayerTurnPhase::End;

        if Self::check_win_condition(state, content)? {
            return Ok(StepOutcome::CombatEnded { won: state.won });
        }

        let allies: SmallVec<[CreatureHandle; 8]> = state.ally_handles.iter().copied().collect();
        hooks::before_side_turn_end_very_early(state, content, CombatSide::Player, &allies)?;
        hooks::before_side_turn_end_early(state, content, CombatSide::Player, &allies)?;
        hooks::before_side_turn_end(state, content, CombatSide::Player, &allies)?;
        if Self::check_win_condition(state, content)? {
            return Ok(StepOutcome::CombatEnded { won: state.won });
        }

        Self::do_turn_end(state, content)?;
        if Self::check_win_condition(state, content)? {
            return Ok(StepOutcome::CombatEnded { won: state.won });
        }

        hooks::before_flush(state, content)?;
        if Self::check_win_condition(state, content)? {
            return Ok(StepOutcome::CombatEnded { won: state.won });
        }

        let retain_whole_hand = !hooks::should_flush(state, content)?;
        let hand_snapshot: SmallVec<[CardHandle; 12]> = SmallVec::from_slice(&state.player.hand);
        let mut flushed: SmallVec<[CardHandle; 12]> = SmallVec::new();
        for h in hand_snapshot {
            let has_retain = card_pile::card_has_keyword(state, content, h, CardKeywords::RETAIN)?
                || state
                    .card(h)
                    .is_some_and(|c| c.temp_flags.contains(CardTempFlags::TEMPORARY_RETAIN_TURN));
            if !has_retain && !retain_whole_hand {
                flushed.push(h);
            }
        }
        if !flushed.is_empty() {
            card_pile::add_and_notify(
                state,
                content,
                &flushed,
                PileType::Discard,
                CardPilePosition::Bottom,
            )?;
        }
        hooks::after_flush(state, content)?;
        Self::end_of_turn_cleanup(state);

        let allies_end: SmallVec<[CreatureHandle; 8]> = state.ally_handles.iter().copied().collect();
        hooks::after_side_turn_end(state, content, CombatSide::Player, &allies_end)?;

        if state.player_creature().is_some_and(|c| c.is_alive()) {
            if let Some(extra_source) = hooks::should_take_extra_turn(state, content)? {
                Self::reset_turn_counters(state);
                hooks::after_taking_extra_turn(state, content, extra_source)?;
                Self::reset_melody_turn_counters(state);
                state.current_side = CombatSide::Player;
                state.player.turn_number += 1;
                if state.static_ctx.compact_turn_history {
                    state.compact_prior_turn_history();
                }
                Self::notify_side_switch(state);
                return Self::start_turn(state, content, true);
            }
        }

        Self::reset_melody_turn_counters(state);
        state.is_player_extra_turn = false;
        Self::reset_turn_counters(state);
        state.current_side = CombatSide::Enemy;
        Self::notify_side_switch(state);
        Self::start_turn(state, content, false)
    }

    fn do_turn_end(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
    ) -> Result<(), KernelError> {
        orb::before_turn_end(state, content)?;
        if hooks::is_over_or_ending_checked(state, content)? {
            return Ok(());
        }
        let hand_snapshot: SmallVec<[CardHandle; 12]> = SmallVec::from_slice(&state.player.hand);
        let mut turn_end_cards: SmallVec<[CardHandle; 4]> = SmallVec::new();
        let mut ethereal_cards: SmallVec<[CardHandle; 8]> = SmallVec::new();
        for h in hand_snapshot {
            let has_turn_end = match state.card(h) {
                Some(c) => content
                    .card_meta(c.id)?
                    .flags
                    .contains(CardStaticFlags::HAS_TURN_END_IN_HAND),
                None => false,
            };
            if has_turn_end {
                turn_end_cards.push(h);
            } else {
                let is_ethereal =
                    card_pile::card_has_keyword(state, content, h, CardKeywords::ETHEREAL)?;
                if is_ethereal && hooks::should_ethereal_trigger(state, content, h)? {
                    ethereal_cards.push(h);
                }
            }
        }
        for h in ethereal_cards {
            card_pile::exhaust(state, content, h, true)?;
        }
        for h in turn_end_cards {
            let in_pile = state
                .card(h)
                .is_some_and(|c| c.pile != PileType::None && !c.removed_from_state);
            if !in_pile {
                continue;
            }
            card_pile::add_and_notify(state, content, &[h], PileType::Play, CardPilePosition::Bottom)?;
            content.card_on_turn_end_in_hand(state, h)?;
            let still_in_pile = state
                .card(h)
                .is_some_and(|c| c.pile != PileType::None && !c.removed_from_state);
            if still_in_pile {
                if card_pile::card_has_keyword(state, content, h, CardKeywords::ETHEREAL)? {
                    card_pile::exhaust(state, content, h, true)?;
                } else {
                    card_pile::add_and_notify(
                        state,
                        content,
                        &[h],
                        PileType::Discard,
                        CardPilePosition::Bottom,
                    )?;
                }
            }
        }
        Ok(())
    }

    fn execute_enemy_turn(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
    ) -> Result<StepOutcome, KernelError> {
        let enemies = SmallVec::from_slice(&state.enemy_handles);
        Self::execute_enemy_turn_from(state, content, enemies)
    }

    fn execute_enemy_turn_from(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
        enemies: SmallVec<[CreatureHandle; 8]>,
    ) -> Result<StepOutcome, KernelError> {
        for idx in 0..enemies.len() {
            let eh = enemies[idx];
            let alive = state.creature(eh).is_some_and(|c| c.is_alive());
            if !alive && hooks::should_creature_be_removed_after_death(state, content, eh)? {
                continue;
            }
            if state
                .creature(eh)
                .and_then(|c| c.monster.as_ref())
                .is_some_and(|m| m.spawned_this_turn)
            {
                continue;
            }
            let is_stunned = state
                .creature(eh)
                .and_then(|c| c.monster.as_ref())
                .is_some_and(|m| m.next_move_id.as_deref() == Some("STUNNED"));
            content.monster_perform_move(state, eh)?;
            if let Some(mut inner) = state.pending.take() {
                let remaining_enemies: SmallVec<[CreatureHandle; 8]> =
                    enemies[idx + 1..].iter().copied().collect();
                inner.frames.insert(
                    0,
                    ContinuationFrame::EnemyTurnResume {
                        current_enemy: eh,
                        remaining_enemies,
                    },
                );
                let req = inner.request.clone();
                state.pending = Some(inner);
                return Ok(StepOutcome::NeedsChoice(req));
            }
            if is_stunned {
                if let Some(c) = state.creature_mut(eh) {
                    if let Some(m) = &mut c.monster {
                        m.is_performing_move = false;
                        m.performed_first_move = true;
                        m.next_move_can_transition_away = true;
                    }
                }
            }
            if Self::check_win_condition(state, content)? {
                return Ok(StepOutcome::CombatEnded { won: state.won });
            }
        }
        Self::end_enemy_turn(state, content)
    }

    fn end_enemy_turn(
        state: &mut BattleState,
        content: &dyn ContentDispatcher,
    ) -> Result<StepOutcome, KernelError> {
        let enemies: SmallVec<[CreatureHandle; 8]> = SmallVec::from_slice(&state.enemy_handles);
        hooks::before_side_turn_end_very_early(state, content, CombatSide::Enemy, &enemies)?;
        hooks::before_side_turn_end_early(state, content, CombatSide::Enemy, &enemies)?;
        hooks::before_side_turn_end(state, content, CombatSide::Enemy, &enemies)?;
        Self::end_of_turn_cleanup(state);
        hooks::after_side_turn_end(state, content, CombatSide::Enemy, &enemies)?;
        if Self::check_win_condition(state, content)? {
            return Ok(StepOutcome::CombatEnded { won: state.won });
        }
        Self::reset_turn_counters(state);
        state.current_side = CombatSide::Player;
        state.round_number += 1;
        state.player.turn_number += 1;
        if state.static_ctx.compact_turn_history {
            state.compact_prior_turn_history();
        }
        Self::notify_side_switch(state);
        Self::start_turn(state, content, false)
    }

    fn end_of_turn_cleanup(state: &mut BattleState) {
        let start = if !state.deck_dirty.0 {
            state.static_ctx.root_deck_prefix_len.min(state.cards.len())
        } else {
            0
        };
        for c in &mut state.cards[start..] {
            if !matches!(
                c.pile,
                PileType::Hand
                    | PileType::Draw
                    | PileType::Discard
                    | PileType::Exhaust
                    | PileType::Play
            ) {
                continue;
            }
            c.temp_flags.remove(
                CardTempFlags::TEMPORARY_FREE_TURN
                    | CardTempFlags::TEMPORARY_RETAIN_TURN
                    | CardTempFlags::TEMPORARY_SLY_TURN
                    | CardTempFlags::EXHAUST_ON_NEXT_PLAY,
            );
            c.temp_cost_override_this_turn = OptI16::NONE;
            c.temp_cost_override_this_turn_or_until_played = OptI16::NONE;
            c.temp_star_cost_override_this_turn = OptI16::NONE;
            if let Some(ext_idx) = c.ext_index {
                state.card_extensions[ext_idx as usize]
                    .local_energy_modifiers
                    .retain(|m| !m.kind.clears_at_end_of_turn());
            }
        }
        Self::reset_turn_counters(state);
    }

    fn reset_turn_counters(state: &mut BattleState) {
        let ctr = &mut state.player.counters;
        ctr.stars_gained_this_turn = Dec::ZERO;
        ctr.skill_cards_played_this_turn = 0;
        ctr.attack_cards_played_this_turn = 0;
        ctr.shivs_played_this_turn = 0;
        ctr.cards_discarded_this_turn = 0;
        ctr.cards_played_this_turn = 0;
        ctr.manual_cards_played_this_turn = 0;
        ctr.attack_or_skill_card_plays_started_this_turn = 0;
        ctr.attack_cards_started_this_turn = 0;
        ctr.zero_cost_attacks_started_this_turn = 0;
        ctr.first_in_series_card_plays_started_this_turn = 0;
        ctr.card_plays_started_this_turn = 0;
    }

    fn reset_melody_turn_counters(state: &mut BattleState) {
        let ctr = &mut state.player.counters;
        ctr.wah_started_this_turn = 0;
        ctr.cho_started_this_turn = 0;
        ctr.ki_started_this_turn = 0;
        ctr.first_wah_play_ordinal_this_turn = 0;
        ctr.first_cho_play_ordinal_this_turn = 0;
        ctr.first_ki_play_ordinal_this_turn = 0;
        ctr.wah_finished_this_turn = 0;
        ctr.cho_finished_this_turn = 0;
        ctr.ki_finished_this_turn = 0;
        ctr.melody_cards_finished_this_turn = 0;
    }

    fn notify_side_switch(state: &mut BattleState) {
        for c in &mut state.creatures {
            if let Some(m) = &mut c.monster {
                m.spawned_this_turn = false;
            }
        }
    }
}
