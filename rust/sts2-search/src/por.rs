use crate::hash::hash_u64_slice;
use smallvec::SmallVec;
use sts2_kernel::continuation::ChoiceRequest;
use sts2_kernel::hooks::collect_combat_hook_listeners;
use sts2_kernel::ordering::{resolve_energy_cost, resolve_star_cost};
use sts2_kernel::state::{BattleState, CardHandle, CreatureHandle, FieldValue};
use sts2_kernel::ContentDispatcher;
use sts2_types::{
    CardId, CardKeywords, CardRarity, CardStaticFlags, CardTags, CardType, HookMask, HookSlot,
    PileType, PotionId, PowerStaticFlags, RelicStaticFlags,
};

const ORDER_SENSITIVE_HOOKS: HookMask = HookMask::EMPTY
    .with(HookSlot::BeforeCardPlayed)
    .with(HookSlot::AfterCardPlayed)
    .with(HookSlot::AfterCardPlayedLate)
    .with(HookSlot::ModifyCardPlayCount)
    .with(HookSlot::AfterModifyingCardPlayCount)
    .with(HookSlot::TryModifyEnergyCostInCombat)
    .with(HookSlot::TryModifyEnergyCostInCombatLate)
    .with(HookSlot::TryModifyStarCostInCombat)
    .with(HookSlot::AfterEnergySpent)
    .with(HookSlot::AfterCardDiscarded)
    .with(HookSlot::AfterCardExhausted)
    .with(HookSlot::AfterHandEmptied)
    .with(HookSlot::AfterBlockGained)
    .with(HookSlot::AfterDamageReceived)
    .with(HookSlot::AfterDamageGiven)
    .with(HookSlot::BeforeDeath)
    .with(HookSlot::AfterDeath)
    .with(HookSlot::AfterAttack)
    .with(HookSlot::BeforeAttack);

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Ord, PartialOrd)]
pub enum PorActionClass {
    /// Pure block-only card (no draw, no RNG, no choice, no damage). Key orders canonically.
    PureBlock(u64),
    /// Pure single-target attack card (no draw, no RNG, no choice, no debuff). Key orders canonically.
    PureAttack(u64),
    /// Order-sensitive / drawing / debuffing / power / potion / end-turn action; resets POR window.
    #[default]
    Barrier,
}

#[derive(Clone, Copy, Debug, Default)]
pub struct CombatOrderSensitivity {
    pub active_mask: HookMask,
    pub por_safe: bool,
    pub any_heal_hook: bool,
}

impl CombatOrderSensitivity {
    /// O(1) per-listener `HookMask` union check across active combat listeners.
    pub fn analyze(state: &BattleState, content: &dyn ContentDispatcher) -> Self {
        let mut active_mask = HookMask::EMPTY;
        let mut order_sensitive_listener = false;

        let listeners = collect_combat_hook_listeners(state);
        for l in listeners {
            if let Ok(m) = content.listener_hook_mask(state, l) {
                active_mask = active_mask.union(m);
            }
        }

        for r in &state.player.relics {
            if r.is_melted {
                continue;
            }
            if let Ok(meta) = content.relic_meta(r.id) {
                if meta.flags.contains(RelicStaticFlags::ORDER_SENSITIVE) {
                    order_sensitive_listener = true;
                }
            }
        }
        for cr in &state.creatures {
            for pw in &cr.powers {
                if let Ok(meta) = content.power_meta(pw.id) {
                    if meta.flags.contains(PowerStaticFlags::ORDER_SENSITIVE) {
                        order_sensitive_listener = true;
                    }
                }
            }
        }
        // Cards whose damage/effect depends on current block, prior plays this turn, or card types played.
        for c in &state.cards {
            if matches!(
                c.id,
                CardId::BodySlam | CardId::Finisher | CardId::Flechettes | CardId::Clash
            ) {
                order_sensitive_listener = true;
            }
        }

        let por_safe =
            !order_sensitive_listener && !active_mask.intersects(ORDER_SENSITIVE_HOOKS);
        let any_heal_hook = active_mask.contains(HookSlot::AfterCurrentHpChanged);
        Self {
            active_mask,
            por_safe,
            any_heal_hook,
        }
    }
}

/// Classifies a `PlayCard` action for sound Partial-Order Reduction (POR).
pub fn classify_card_for_por(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    card_handle: CardHandle,
    target: Option<CreatureHandle>,
) -> PorActionClass {
    let Some(card) = state.card(card_handle) else {
        return PorActionClass::Barrier;
    };
    let Ok(meta) = content.card_meta(card.id) else {
        return PorActionClass::Barrier;
    };
    if meta.flags.contains(CardStaticFlags::ORDER_SENSITIVE)
        || meta.flags.contains(CardStaticFlags::USES_RNG)
        || meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST)
        || meta.flags.contains(CardStaticFlags::IS_X_STAR_COST)
        || card.affliction.is_some()
        || state
            .card_ext(card.ext_index)
            .is_some_and(|e| e.enchantment.is_some())
        || card.has_local_keyword(CardKeywords::EXHAUST)
        || card.temp_flags.intersects(
            sts2_kernel::state::CardTempFlags::EXHAUST_ON_NEXT_PLAY
                | sts2_kernel::state::CardTempFlags::IS_DUPE,
        )
    {
        return PorActionClass::Barrier;
    }

    let key = ((card.id as u64) << 40)
        | ((card.upgrade_level as u64 & 0xff) << 32)
        | ((target.map_or(0, |t| t.0) as u64) & 0xffff_ffff);

    match card.id {
        CardId::DefendIronclad
        | CardId::DefendSilent
        | CardId::DefendDefect
        | CardId::DefendRegent
        | CardId::DefendNecrobinder
        | CardId::DefendFool => PorActionClass::PureBlock(key),
        CardId::StrikeIronclad
        | CardId::StrikeSilent
        | CardId::StrikeDefect
        | CardId::StrikeRegent
        | CardId::StrikeNecrobinder
        | CardId::StrikeFool => {
            // If target has Thorns/CurlUp/reactive enemy powers, stay conservative as Barrier.
            if let Some(th) = target {
                if let Some(ec) = state.creature(th) {
                    if ec.powers.iter().any(|p| {
                        content
                            .power_meta(p.id)
                            .is_ok_and(|pm| pm.hook_mask.intersects(ORDER_SENSITIVE_HOOKS))
                    }) {
                        return PorActionClass::Barrier;
                    }
                }
            }
            PorActionClass::PureAttack(key)
        }
        _ => PorActionClass::Barrier,
    }
}

/// Returns `true` if `curr` violates the canonical monotonic order `(PureBlock -> PureAttack)`
/// inside a `por_safe` window where no order-sensitive hooks or triggers are active.
#[inline]
pub fn por_should_skip(
    por_safe: bool,
    prev_turn: i32,
    curr_turn: i32,
    prev_class: PorActionClass,
    curr_class: PorActionClass,
) -> bool {
    if !por_safe || prev_turn != curr_turn {
        return false;
    }
    match (prev_class, curr_class) {
        (PorActionClass::Barrier, _) | (_, PorActionClass::Barrier) => false,
        _ => curr_class < prev_class,
    }
}

#[inline]
fn hash_field_value(fv: &FieldValue) -> u64 {
    match fv {
        FieldValue::Int(x) => (*x as u64) ^ 0x11,
        FieldValue::Decimal(d) => {
            let b = d.as_decimal().serialize();
            hash_u64_slice(&[
                u64::from_le_bytes(b[0..8].try_into().unwrap()),
                u64::from_le_bytes(b[8..16].try_into().unwrap()),
            ])
        }
        FieldValue::Bool(b) => u64::from(*b) ^ 0x22,
        FieldValue::String(s) => xxhash_rust::xxh64::xxh64(s.as_bytes(), 0x33),
        FieldValue::NullableInt(o) => o.map_or(0x44, |x| (x as u64) ^ 0x45),
        FieldValue::CardHandle(h) => (*h as u64) ^ 0x55,
        FieldValue::CreatureHandle(h) => (*h as u64) ^ 0x66,
        _ => 0x77,
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd)]
pub struct HandCardEquivKey {
    pub card_id: CardId,
    pub upgrade_level: i32,
    pub energy_cost: i32,
    pub star_cost: i32,
    pub keywords_bits: u16,
    pub temp_flags_bits: u16,
    pub affliction_bits: u64,
    pub ext_hash: u64,
}

pub fn card_equiv_key_with_costs(
    state: &BattleState,
    handle: CardHandle,
    energy_cost: i32,
    star_cost: i32,
) -> HandCardEquivKey {
    let Some(card) = state.card(handle).copied() else {
        return HandCardEquivKey {
            card_id: CardId::StrikeIronclad,
            upgrade_level: -1,
            energy_cost: -1,
            star_cost: -1,
            keywords_bits: 0,
            temp_flags_bits: 0,
            affliction_bits: 0,
            ext_hash: 0,
        };
    };
    let affliction_bits = card
        .affliction
        .map_or(0, |a| ((a.id as u64) << 32) | (a.amount as u64 & 0xffff_ffff));
    let ext_hash = if let Some(ext) = state.card_ext(card.ext_index) {
        let mut words: SmallVec<[u64; 8]> = SmallVec::new();
        for m in &ext.local_energy_modifiers {
            words.push(((m.kind as u64) << 33) | ((m.amount as u64) << 1) | u64::from(m.is_reduce_only));
        }
        if let Some(e) = &ext.enchantment {
            words.push(((e.id as u64) << 34) | ((e.status as u64) << 32) | (e.amount as u64 & 0xffff_ffff));
        }
        for (k, v) in &ext.private_fields {
            words.push(xxhash_rust::xxh64::xxh64(k.as_bytes(), 0x99));
            words.push(hash_field_value(v));
        }
        hash_u64_slice(&words)
    } else {
        0
    };

    HandCardEquivKey {
        card_id: card.id,
        upgrade_level: card.upgrade_level,
        energy_cost,
        star_cost,
        keywords_bits: card.effective_keywords_base().bits(),
        temp_flags_bits: card.temp_flags.bits(),
        affliction_bits,
        ext_hash,
    }
}

pub fn card_equiv_key(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    handle: CardHandle,
) -> HandCardEquivKey {
    let Some(card) = state.card(handle).copied() else {
        return HandCardEquivKey {
            card_id: CardId::StrikeIronclad,
            upgrade_level: -1,
            energy_cost: -1,
            star_cost: -1,
            keywords_bits: 0,
            temp_flags_bits: 0,
            affliction_bits: 0,
            ext_hash: 0,
        };
    };
    let energy_cost = resolve_energy_cost(state, content, handle).unwrap_or(card.base_energy_cost);
    let star_cost = resolve_star_cost(state, content, handle).unwrap_or(0);
    card_equiv_key_with_costs(state, handle, energy_cost, star_cost)
}

/// Deduplicates playable hand cards by exact equivalence key while preserving first-slot order.
pub fn dedup_playable_hand_cards(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    enable_dedup: bool,
) -> SmallVec<[CardHandle; 12]> {
    let hand = SmallVec::<[CardHandle; 16]>::from_slice(&state.player.hand);
    let mut playable = SmallVec::<[CardHandle; 12]>::new();
    let mut seen = SmallVec::<[HandCardEquivKey; 12]>::new();
    for ch in hand {
        let Ok(Some((energy_cost, star_cost))) =
            sts2_kernel::CombatEngine::can_play_card_with_costs(state, content, ch)
        else {
            continue;
        };
        if enable_dedup {
            let key = card_equiv_key_with_costs(state, ch, energy_cost, star_cost);
            if seen.contains(&key) {
                continue;
            }
            seen.push(key);
        }
        playable.push(ch);
    }
    playable
}

/// Short-circuit check for whether any card in hand can currently be played.
pub fn has_any_playable_hand_card(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> bool {
    let hand = SmallVec::<[CardHandle; 16]>::from_slice(&state.player.hand);
    for ch in hand {
        if sts2_kernel::CombatEngine::can_play_card(state, content, ch).unwrap_or(false) {
            return true;
        }
    }
    false
}

#[inline]
fn is_usable_potion_slot(
    slot: usize,
    pid: PotionId,
    policy: &crate::SearchPolicy,
    allow_paid_optional: bool,
    renewable_potion_shaped_rock: bool,
) -> (bool, bool) {
    // FairyInABottle is passive-only (triggered on lethal damage).
    if pid == PotionId::FairyInABottle {
        return (false, false);
    }
    if !policy.allows_explicit_potion_use(slot, pid, false) {
        return (false, false);
    }
    let directive = policy.potion_directive_for(slot, pid);
    let is_forced = directive == crate::eval::SolverPotionDirective::Force;
    if !allow_paid_optional && !is_forced {
        let cost = crate::eval::PotionValuationRegistry::strategic_hp_cost_with_overrides(
            pid,
            renewable_potion_shaped_rock,
            &policy.potion_hp_threshold_overrides,
        );
        if cost > 0 {
            return (false, false);
        }
    }
    (true, is_forced)
}

/// Short-circuit check for whether any potion slot can currently be used under this pass policy.
pub fn has_any_usable_potion_for_pass(
    state: &BattleState,
    sensitivity: &CombatOrderSensitivity,
    policy: &crate::SearchPolicy,
    allow_paid_optional: bool,
    renewable_potion_shaped_rock: bool,
) -> bool {
    if !state.player.can_use_or_remove_potions {
        return false;
    }
    let player_hp = state
        .player_creature()
        .map_or(state.player.current_hp, |c| c.current_hp);
    let player_max_hp = state
        .player_creature()
        .map_or(state.player.max_hp, |c| c.max_hp);
    for (slot, maybe_id) in state.player.potion_slots.iter().enumerate() {
        let Some(pid) = *maybe_id else { continue };
        let (allowed, is_forced) = is_usable_potion_slot(
            slot,
            pid,
            policy,
            allow_paid_optional,
            renewable_potion_shaped_rock,
        );
        if !allowed {
            continue;
        }
        if !is_forced
            && pid == PotionId::BloodPotion
            && player_hp >= player_max_hp
            && !sensitivity.any_heal_hook
        {
            continue;
        }
        return true;
    }
    false
}

/// Deduplicates potion slots by `PotionId` and skips disallowed, passive, or provably no-op potions.
pub fn dedup_and_filter_potions(
    state: &BattleState,
    sensitivity: &CombatOrderSensitivity,
    policy: &crate::SearchPolicy,
    enable_dedup: bool,
) -> SmallVec<[(usize, PotionId); 5]> {
    dedup_and_filter_potions_for_pass(state, sensitivity, policy, enable_dedup, true, false)
}

/// Pass-aware potion deduplication filtering optional paid potions when `allow_paid_optional == false`.
pub fn dedup_and_filter_potions_for_pass(
    state: &BattleState,
    sensitivity: &CombatOrderSensitivity,
    policy: &crate::SearchPolicy,
    enable_dedup: bool,
    allow_paid_optional: bool,
    renewable_potion_shaped_rock: bool,
) -> SmallVec<[(usize, PotionId); 5]> {
    let mut out = SmallVec::<[(usize, PotionId); 5]>::new();
    if !state.player.can_use_or_remove_potions {
        return out;
    }
    let player_hp = state
        .player_creature()
        .map_or(state.player.current_hp, |c| c.current_hp);
    let player_max_hp = state
        .player_creature()
        .map_or(state.player.max_hp, |c| c.max_hp);

    let mut non_forced_seen: SmallVec<[PotionId; 5]> = SmallVec::new();
    for (slot, maybe_id) in state.player.potion_slots.iter().enumerate() {
        let Some(pid) = *maybe_id else { continue };
        let (allowed, is_forced) = is_usable_potion_slot(
            slot,
            pid,
            policy,
            allow_paid_optional,
            renewable_potion_shaped_rock,
        );
        if !allowed {
            continue;
        }
        if enable_dedup && !is_forced {
            if non_forced_seen.contains(&pid) {
                continue;
            }
            // BloodPotion / CureAll at full HP with no heal-listener hooks is a provable no-op.
            if pid == PotionId::BloodPotion
                && player_hp >= player_max_hp
                && !sensitivity.any_heal_hook
            {
                continue;
            }
            non_forced_seen.push(pid);
        }
        out.push((slot, pid));
    }
    out
}

/// Domain-agnostic card quality heuristic used to order choice candidates before bounded subset enumeration.
pub fn choice_candidate_quality(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    ch: CardHandle,
) -> i32 {
    let Some(card) = state.card(ch) else {
        return 0;
    };
    let Ok(meta) = content.card_meta(card.id) else {
        return 0;
    };
    if card.has_local_keyword(CardKeywords::UNPLAYABLE) {
        return -450;
    }
    if matches!(meta.card_type, CardType::Curse | CardType::Status) {
        return -400;
    }
    let mut quality = if meta
        .base_tags
        .intersects(CardTags::STRIKE | CardTags::DEFEND)
    {
        60
    } else {
        let mut q = match meta.card_type {
            CardType::Power => 280,
            CardType::Skill => 200,
            CardType::Attack => 190,
            _ => 120,
        };
        q += match meta.rarity {
            CardRarity::Rare | CardRarity::Ancient => 90,
            CardRarity::Uncommon => 50,
            CardRarity::Common | CardRarity::Event => 20,
            _ => 10,
        };
        if card.base_energy_cost == 0 {
            q += 40;
        } else if card.base_energy_cost == 1 {
            q += 15;
        }
        q
    };
    if meta.flags.contains(CardStaticFlags::HAS_GROWTH) {
        quality += 60;
    }
    if card.upgrade_level > 0 {
        quality += 35;
    }
    if !meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST)
        && sts2_kernel::ordering::resolve_local_energy_cost(state, ch, false) == 0
    {
        quality += 30;
    }
    quality
}

/// Enumerates canonical multiset choice index selections for a `ChoiceRequest` without duplicate
/// equivalent card combinations (e.g., picking 2 of 5 cards where 3 are identical `Strike+0`).
pub fn enumerate_canonical_choices(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    req: &ChoiceRequest,
    max_branches: usize,
    enable_multiset_dedup: bool,
) -> Vec<Vec<usize>> {
    let n = req.candidates.len();
    if n == 0 || req.max_count == 0 {
        return vec![Vec::new()];
    }

    let cap = max_branches.max(1);
    let equiv_keys: SmallVec<[HandCardEquivKey; 24]> = if enable_multiset_dedup {
        req.candidates
            .iter()
            .map(|&ch| card_equiv_key(state, content, ch))
            .collect()
    } else {
        SmallVec::new()
    };

    let ordered: SmallVec<[usize; 24]> = if n > 1 {
        let qualities: SmallVec<[i32; 24]> = req
            .candidates
            .iter()
            .map(|&ch| choice_candidate_quality(state, content, ch))
            .collect();
        let mut desc: SmallVec<[usize; 24]> = (0..n).collect();
        desc.sort_unstable_by_key(|&i| (-qualities[i], i));
        if req.source_pile == PileType::Hand {
            let mut asc: SmallVec<[usize; 24]> = (0..n).collect();
            asc.sort_unstable_by_key(|&i| (qualities[i], i));
            let mut used: SmallVec<[bool; 24]> = smallvec::smallvec![false; n];
            let mut mixed = SmallVec::with_capacity(n);
            let (mut lo_i, mut hi_i) = (0usize, 0usize);
            while mixed.len() < n {
                while lo_i < n && used[asc[lo_i]] {
                    lo_i += 1;
                }
                if lo_i < n {
                    let idx = asc[lo_i];
                    used[idx] = true;
                    mixed.push(idx);
                    lo_i += 1;
                }
                if mixed.len() >= n {
                    break;
                }
                while hi_i < n && used[desc[hi_i]] {
                    hi_i += 1;
                }
                if hi_i < n {
                    let idx = desc[hi_i];
                    used[idx] = true;
                    mixed.push(idx);
                    hi_i += 1;
                }
            }
            mixed
        } else {
            desc
        }
    } else {
        smallvec::smallvec![0]
    };

    // For canonical multiset enumeration over `ordered`, an occurrence of a key may only be chosen
    // when its immediately preceding occurrence in `ordered` is also chosen.
    let mut prev_dup: SmallVec<[Option<usize>; 24]> = SmallVec::with_capacity(n);
    if enable_multiset_dedup {
        for pos in 0..n {
            let key = equiv_keys[ordered[pos]];
            let prev = (0..pos).rev().find(|&p| equiv_keys[ordered[p]] == key);
            prev_dup.push(prev);
        }
    }

    fn push_normalized(
        ordered: &[usize],
        picked_positions: &[usize],
        out: &mut Vec<Vec<usize>>,
    ) {
        let mut sel: Vec<usize> = picked_positions.iter().map(|&p| ordered[p]).collect();
        sel.sort_unstable();
        out.push(sel);
    }

    #[allow(clippy::too_many_arguments)]
    fn dfs_prefix(
        start: usize,
        max_pos: usize,
        target_len: usize,
        ordered: &[usize],
        prev_dup: &[Option<usize>],
        enable_dedup: bool,
        picked: &mut Vec<usize>,
        out: &mut Vec<Vec<usize>>,
        len_cap: usize,
    ) {
        if out.len() >= len_cap {
            return;
        }
        if picked.len() == target_len {
            push_normalized(ordered, picked, out);
            return;
        }
        let need = target_len - picked.len();
        if max_pos.saturating_sub(start) < need {
            return;
        }
        for pos in start..=max_pos - need {
            if enable_dedup {
                if let Some(prev) = prev_dup[pos] {
                    if !picked.contains(&prev) {
                        continue;
                    }
                }
            }
            picked.push(pos);
            dfs_prefix(
                pos + 1,
                max_pos,
                target_len,
                ordered,
                prev_dup,
                enable_dedup,
                picked,
                out,
                len_cap,
            );
            picked.pop();
            if out.len() >= len_cap {
                return;
            }
        }
    }

    fn enumerate_for_len(
        len: usize,
        n: usize,
        ordered: &[usize],
        equiv_keys: &[HandCardEquivKey],
        prev_dup: &[Option<usize>],
        enable_dedup: bool,
        len_cap: usize,
    ) -> Vec<Vec<usize>> {
        let mut out = Vec::new();
        if len == 0 {
            out.push(Vec::new());
            return out;
        }
        if len == 1 {
            let mut seen: SmallVec<[HandCardEquivKey; 16]> = SmallVec::new();
            for &orig_idx in ordered {
                if enable_dedup {
                    let k = equiv_keys[orig_idx];
                    if seen.contains(&k) {
                        continue;
                    }
                    seen.push(k);
                }
                out.push(vec![orig_idx]);
                if out.len() >= len_cap {
                    break;
                }
            }
            return out;
        }

        let mut picked = Vec::with_capacity(len);
        for max_pos in (len - 1)..n {
            if out.len() >= len_cap {
                break;
            }
            if !enable_dedup {
                picked.clear();
                picked.push(max_pos);
                dfs_prefix(
                    0,
                    max_pos,
                    len,
                    ordered,
                    prev_dup,
                    false,
                    &mut picked,
                    &mut out,
                    len_cap,
                );
                continue;
            }

            let mut chain: SmallVec<[usize; 8]> = SmallVec::new();
            let mut cur_p = Some(max_pos);
            while let Some(p) = cur_p {
                chain.push(p);
                if chain.len() > len {
                    break;
                }
                cur_p = prev_dup[p];
            }
            if chain.len() > len {
                continue;
            }
            picked.clear();
            for &p in chain.iter().rev() {
                picked.push(p);
            }
            let prefix_limit = chain[chain.len() - 1];
            dfs_prefix(
                0,
                prefix_limit,
                len,
                ordered,
                prev_dup,
                true,
                &mut picked,
                &mut out,
                len_cap,
            );
        }
        out
    }

    let mut results = Vec::new();
    if req.can_skip && req.min_count == 0 {
        results.push(Vec::new());
    }

    let min_pick = req.min_count.max(usize::from(req.can_skip));
    let max_pick = req.max_count.min(n);

    if min_pick > max_pick {
        if results.is_empty() {
            results.push(Vec::new());
        }
        return results;
    }

    if min_pick == max_pick {
        let len_rem = cap.saturating_sub(results.len());
        if len_rem > 0 {
            results.extend(enumerate_for_len(
                min_pick,
                n,
                &ordered,
                &equiv_keys,
                &prev_dup,
                enable_multiset_dedup,
                len_rem,
            ));
        }
    } else {
        let mut by_len: SmallVec<[Vec<Vec<usize>>; 8]> =
            SmallVec::with_capacity(max_pick - min_pick + 1);
        let mut total = results.len();
        for len in min_pick..=max_pick {
            let group = enumerate_for_len(
                len,
                n,
                &ordered,
                &equiv_keys,
                &prev_dup,
                enable_multiset_dedup,
                cap,
            );
            total = total.saturating_add(group.len());
            by_len.push(group);
        }
        if total <= cap {
            for group in by_len {
                results.extend(group);
            }
        } else {
            let mut offsets: SmallVec<[usize; 8]> = smallvec::smallvec![0; by_len.len()];
            while results.len() < cap {
                let mut progressed = false;
                for (g_idx, group) in by_len.iter().enumerate() {
                    if results.len() >= cap {
                        break;
                    }
                    let pos = offsets[g_idx];
                    if pos < group.len() {
                        results.push(group[pos].clone());
                        offsets[g_idx] = pos + 1;
                        progressed = true;
                    }
                }
                if !progressed {
                    break;
                }
            }
        }
    }

    if results.is_empty() {
        results.push(Vec::new());
    }
    results
}
