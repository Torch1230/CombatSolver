use crate::hash::hash_u64_slice;
use smallvec::SmallVec;
use sts2_kernel::continuation::ChoiceRequest;
use sts2_kernel::hooks::collect_combat_hook_listeners;
use sts2_kernel::ordering::{resolve_energy_cost, resolve_star_cost};
use sts2_kernel::state::{BattleState, CardHandle, CreatureHandle, FieldValue};
use sts2_kernel::ContentDispatcher;
use sts2_types::{
    CardId, CardKeywords, CardStaticFlags, HookMask, HookSlot, PotionId, PowerStaticFlags,
    RelicStaticFlags,
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

/// Deduplicates playable hand cards by exact equivalence key while preserving first-slot order.
pub fn dedup_playable_hand_cards(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    enable_dedup: bool,
) -> SmallVec<[CardHandle; 12]> {
    let hand = state.player.hand.clone();
    let mut playable = SmallVec::<[CardHandle; 12]>::new();
    let mut seen = SmallVec::<[HandCardEquivKey; 12]>::new();
    for ch in hand {
        if !sts2_kernel::CombatEngine::can_play_card(state, content, ch).unwrap_or(false) {
            continue;
        }
        if enable_dedup {
            let key = card_equiv_key(state, content, ch);
            if seen.contains(&key) {
                continue;
            }
            seen.push(key);
        }
        playable.push(ch);
    }
    playable
}

/// Deduplicates potion slots by `PotionId` and skips disallowed, passive, or provably no-op potions.
pub fn dedup_and_filter_potions(
    state: &BattleState,
    sensitivity: &CombatOrderSensitivity,
    policy: &crate::SearchPolicy,
    enable_dedup: bool,
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
        // FairyInABottle is passive-only (triggered on lethal damage).
        if pid == PotionId::FairyInABottle {
            continue;
        }
        if !policy.allows_explicit_potion_use(slot, pid, false) {
            continue;
        }
        let directive = policy.potion_directive_for(slot, pid);
        let is_forced = directive == crate::eval::SolverPotionDirective::Force;
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

    let equiv_keys: Vec<HandCardEquivKey> = if enable_multiset_dedup {
        req.candidates
            .iter()
            .map(|&ch| card_equiv_key(state, content, ch))
            .collect()
    } else {
        Vec::new()
    };

    let mut results = Vec::new();
    if req.can_skip && req.min_count == 0 {
        results.push(Vec::new());
    }

    let min_pick = req.min_count.max(if req.can_skip { 1 } else { req.min_count });
    let max_pick = req.max_count.min(n);

    fn dfs(
        start: usize,
        target_len: usize,
        n: usize,
        equiv_keys: &[HandCardEquivKey],
        enable_dedup: bool,
        cur: &mut Vec<usize>,
        out: &mut Vec<Vec<usize>>,
        cap: usize,
    ) {
        if out.len() >= cap {
            return;
        }
        if cur.len() == target_len {
            out.push(cur.clone());
            return;
        }
        let need = target_len - cur.len();
        let mut seen_at_depth: SmallVec<[HandCardEquivKey; 16]> = SmallVec::new();
        for idx in start..=n - need {
            if enable_dedup {
                let key = equiv_keys[idx];
                if seen_at_depth.contains(&key) {
                    continue;
                }
                seen_at_depth.push(key);
            }
            cur.push(idx);
            dfs(
                idx + 1,
                target_len,
                n,
                equiv_keys,
                enable_dedup,
                cur,
                out,
                cap,
            );
            cur.pop();
            if out.len() >= cap {
                return;
            }
        }
    }

    let mut cur = Vec::with_capacity(max_pick);
    for len in min_pick..=max_pick {
        if len == 0 {
            if !results.iter().any(|v| v.is_empty()) {
                results.push(Vec::new());
            }
            continue;
        }
        if len > n {
            break;
        }
        dfs(
            0,
            len,
            n,
            &equiv_keys,
            enable_multiset_dedup,
            &mut cur,
            &mut results,
            max_branches.max(1),
        );
        if results.len() >= max_branches.max(1) {
            break;
        }
    }

    if results.is_empty() {
        results.push(Vec::new());
    }
    results
}
