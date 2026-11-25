use crate::eval::RootEvalBaseline;
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
    let energy_cost = resolve_energy_cost(state, content, handle).unwrap_or(card.base_energy_cost as i32);
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
    if hand.is_empty()
        || sts2_kernel::hooks::is_over_or_ending_checked(state, content).unwrap_or(true)
    {
        return playable;
    }
    let _ = state.ensure_hook_cache(content);
    let mut seen = SmallVec::<[HandCardEquivKey; 12]>::new();
    for ch in hand {
        let Ok(Some((energy_cost, star_cost))) =
            sts2_kernel::CombatEngine::can_play_card_with_costs_in_progress(state, content, ch)
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

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct PotionUseClassification {
    pub is_generated: bool,
    pub is_forced: bool,
    pub optional_paid_cost_delta: i32,
    pub optional_paid_count_delta: u16,
    pub root_potion_slot_bit: u16,
    pub forced_bit: Option<u32>,
}

/// Classifies a manual potion use at `slot` using `state.player.combat_generated_potion_mask`
/// and root-frozen slot costs (`HotPotionPolicy.TryClassifyPotionUseForPass`).
#[inline]
pub fn try_classify_potion_use_for_pass(
    state: &BattleState,
    slot: usize,
    pid: PotionId,
    policy: &crate::SearchPolicy,
    root: Option<&RootEvalBaseline>,
    allow_paid_optional: bool,
    renewable_potion_shaped_rock: bool,
) -> Option<PotionUseClassification> {
    if pid == PotionId::FairyInABottle || !state.player.can_use_or_remove_potions {
        return None;
    }
    if slot >= state.player.potion_slots.len() || slot >= 16 {
        return None;
    }
    let slot_bit = 1u16 << slot;
    let is_generated = (state.player.combat_generated_potion_mask & slot_bit) != 0;
    if is_generated {
        // Mid-combat generated potions cost 0, ignore root Disabled, and never satisfy root Force.
        if !policy.allow_potions || policy.potion_policy == crate::eval::SolverPotionPolicy::Disabled {
            return None;
        }
        return Some(PotionUseClassification {
            is_generated: true,
            is_forced: false,
            optional_paid_cost_delta: 0,
            optional_paid_count_delta: 0,
            root_potion_slot_bit: 0,
            forced_bit: None,
        });
    }

    if !policy.allows_explicit_potion_use(slot, pid, false) {
        return None;
    }
    let directive = policy.potion_directive_for(slot, pid);
    let is_forced = directive == crate::eval::SolverPotionDirective::Force;
    if is_forced {
        return Some(PotionUseClassification {
            is_generated: false,
            is_forced: true,
            optional_paid_cost_delta: 0,
            optional_paid_count_delta: 0,
            root_potion_slot_bit: slot_bit,
            forced_bit: policy.forced_bit_for(slot, pid),
        });
    }

    let slot_cost = match root {
        Some(rb) if rb.root_slot_costs[slot] >= 0 => rb.root_slot_costs[slot] as i32,
        Some(rb) => crate::eval::PotionValuationRegistry::strategic_hp_cost_for_root(
            pid,
            renewable_potion_shaped_rock,
            rb.initial_player_max_hp,
            &policy.potion_hp_threshold_overrides,
        ),
        None => crate::eval::PotionValuationRegistry::strategic_hp_cost_with_overrides(
            pid,
            renewable_potion_shaped_rock,
            &policy.potion_hp_threshold_overrides,
        ),
    };

    if slot_cost == 0 {
        return Some(PotionUseClassification {
            is_generated: false,
            is_forced: false,
            optional_paid_cost_delta: 0,
            optional_paid_count_delta: 0,
            root_potion_slot_bit: slot_bit,
            forced_bit: None,
        });
    }

    if !allow_paid_optional {
        return None;
    }

    Some(PotionUseClassification {
        is_generated: false,
        is_forced: false,
        optional_paid_cost_delta: slot_cost,
        optional_paid_count_delta: 1,
        root_potion_slot_bit: slot_bit,
        forced_bit: None,
    })
}

/// Returns `true` when using `pid` in `state` is provably a zero-effect no-op,
/// exempting forced directives and `OnPotionUsed` relics (`ReptileTrinket` / `BeltBuckle`).
#[inline]
pub fn is_provable_no_op_potion_use(
    state: &BattleState,
    pid: PotionId,
    is_forced: bool,
    has_on_potion_used_relic: bool,
    has_sozu: bool,
    any_heal_hook: bool,
) -> bool {
    if is_forced || has_on_potion_used_relic {
        return false;
    }
    let pc = state.player_creature();
    let player_hp = pc.map_or(state.player.current_hp, |c| c.current_hp);
    let player_max_hp = pc.map_or(state.player.max_hp, |c| c.max_hp);
    let player_block = pc.map_or(state.player.block, |c| c.block);
    match pid {
        PotionId::Fortifier => player_block <= 0,
        PotionId::BloodPotion => player_hp >= player_max_hp && !any_heal_hook,
        PotionId::DropletOfPrecognition => state.player.draw.is_empty(),
        PotionId::LiquidMemories => state.player.discard.is_empty(),
        PotionId::TouchOfInsanity => state.player.hand.is_empty(),
        PotionId::GamblersBrew | PotionId::Ashwater => state.player.hand.is_empty(),
        PotionId::SwiftPotion | PotionId::DistilledChaos => {
            state.player.draw.is_empty() && state.player.discard.is_empty()
        }
        PotionId::BlessingOfTheForge => state.player.hand.iter().all(|&ch| {
            state.card(ch).is_none_or(|c| {
                c.upgrade_level >= sts2_types::generated_card_meta(c.id).max_upgrade_level
            })
        }),
        PotionId::EntropicBrew => has_sozu,
        _ => false,
    }
}

/// Short-circuit check for whether any potion slot can currently be used under this pass policy.
pub fn has_any_usable_potion_for_pass(
    state: &BattleState,
    sensitivity: &CombatOrderSensitivity,
    policy: &crate::SearchPolicy,
    allow_paid_optional: bool,
    renewable_potion_shaped_rock: bool,
) -> bool {
    has_any_usable_potion_with_root(
        state,
        sensitivity,
        policy,
        None,
        allow_paid_optional,
        renewable_potion_shaped_rock,
    )
}

pub fn has_any_usable_potion_with_root(
    state: &BattleState,
    sensitivity: &CombatOrderSensitivity,
    policy: &crate::SearchPolicy,
    root: Option<&RootEvalBaseline>,
    allow_paid_optional: bool,
    renewable_potion_shaped_rock: bool,
) -> bool {
    if !state.player.can_use_or_remove_potions {
        return false;
    }
    let has_on_potion_used_relic = root.map_or_else(
        || {
            state.player.relics.iter().any(|r| {
                !r.is_melted
                    && matches!(
                        r.id,
                        sts2_types::RelicId::ReptileTrinket | sts2_types::RelicId::BeltBuckle
                    )
            })
        },
        |rb| rb.has_on_potion_used_relic,
    );
    let has_sozu = root.map_or_else(
        || {
            state
                .player
                .relics
                .iter()
                .any(|r| !r.is_melted && r.id == sts2_types::RelicId::Sozu)
        },
        |rb| rb.has_sozu,
    );
    for (slot, maybe_id) in state.player.potion_slots.iter().enumerate() {
        let Some(pid) = *maybe_id else { continue };
        let Some(cls) = try_classify_potion_use_for_pass(
            state,
            slot,
            pid,
            policy,
            root,
            allow_paid_optional,
            renewable_potion_shaped_rock,
        ) else {
            continue;
        };
        if is_provable_no_op_potion_use(
            state,
            pid,
            cls.is_forced,
            has_on_potion_used_relic,
            has_sozu,
            sensitivity.any_heal_hook,
        ) {
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
    let items = dedup_and_filter_potions_with_root(
        state,
        sensitivity,
        policy,
        None,
        enable_dedup,
        allow_paid_optional,
        renewable_potion_shaped_rock,
    );
    items.into_iter().map(|(slot, pid, _)| (slot, pid)).collect()
}

pub fn dedup_and_filter_potions_with_root(
    state: &BattleState,
    sensitivity: &CombatOrderSensitivity,
    policy: &crate::SearchPolicy,
    root: Option<&RootEvalBaseline>,
    enable_dedup: bool,
    allow_paid_optional: bool,
    renewable_potion_shaped_rock: bool,
) -> SmallVec<[(usize, PotionId, PotionUseClassification); 5]> {
    let mut out = SmallVec::<[(usize, PotionId, PotionUseClassification); 5]>::new();
    if !state.player.can_use_or_remove_potions {
        return out;
    }
    let has_on_potion_used_relic = root.map_or_else(
        || {
            state.player.relics.iter().any(|r| {
                !r.is_melted
                    && matches!(
                        r.id,
                        sts2_types::RelicId::ReptileTrinket | sts2_types::RelicId::BeltBuckle
                    )
            })
        },
        |rb| rb.has_on_potion_used_relic,
    );
    let has_sozu = root.map_or_else(
        || {
            state
                .player
                .relics
                .iter()
                .any(|r| !r.is_melted && r.id == sts2_types::RelicId::Sozu)
        },
        |rb| rb.has_sozu,
    );

    let mut seen: SmallVec<[(PotionId, bool, bool, i32); 5]> = SmallVec::new();
    for (slot, maybe_id) in state.player.potion_slots.iter().enumerate() {
        let Some(pid) = *maybe_id else { continue };
        let Some(cls) = try_classify_potion_use_for_pass(
            state,
            slot,
            pid,
            policy,
            root,
            allow_paid_optional,
            renewable_potion_shaped_rock,
        ) else {
            continue;
        };
        if is_provable_no_op_potion_use(
            state,
            pid,
            cls.is_forced,
            has_on_potion_used_relic,
            has_sozu,
            sensitivity.any_heal_hook,
        ) {
            continue;
        }
        if enable_dedup {
            let key = (pid, cls.is_generated, cls.is_forced, cls.optional_paid_cost_delta);
            if seen.contains(&key) {
                continue;
            }
            seen.push(key);
        }
        out.push((slot, pid, cls));
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
        let setup_weight = state.fast_card_rollout_profile(content, card.id).setup_weight as i32;
        q += (setup_weight * 4).min(140);
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

#[derive(Default)]
pub struct ChoiceEnumScratch {
    flat: Vec<SmallVec<[usize; 8]>>,
    ranges: SmallVec<[(usize, usize); 8]>,
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
    let mut buf = Vec::new();
    enumerate_canonical_choices_into(
        state,
        content,
        req,
        max_branches,
        enable_multiset_dedup,
        &mut buf,
    );
    buf.into_iter().map(|v| v.into_vec()).collect()
}

pub fn enumerate_canonical_choices_into(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    req: &ChoiceRequest,
    max_branches: usize,
    enable_multiset_dedup: bool,
    results: &mut Vec<SmallVec<[usize; 8]>>,
) {
    let mut scratch = ChoiceEnumScratch::default();
    enumerate_canonical_choices_with_scratch(
        state,
        content,
        req,
        max_branches,
        enable_multiset_dedup,
        &mut scratch,
        results,
    );
}

pub fn enumerate_canonical_choices_with_scratch(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    req: &ChoiceRequest,
    max_branches: usize,
    enable_multiset_dedup: bool,
    scratch: &mut ChoiceEnumScratch,
    results: &mut Vec<SmallVec<[usize; 8]>>,
) {
    results.clear();
    let n = req.candidates.len();
    if n == 0 || req.max_count == 0 {
        results.push(SmallVec::new());
        return;
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

    let mut groups: SmallVec<[(HandCardEquivKey, SmallVec<[usize; 8]>); 24]> = SmallVec::new();
    let mut group_suffix_avail: SmallVec<[usize; 25]> = SmallVec::new();
    if enable_multiset_dedup {
        for &orig_idx in &ordered {
            let key = equiv_keys[orig_idx];
            if let Some((_, list)) = groups.iter_mut().find(|(k, _)| *k == key) {
                list.push(orig_idx);
            } else {
                groups.push((key, smallvec::smallvec![orig_idx]));
            }
        }
        group_suffix_avail.resize(groups.len() + 1, 0);
        for i in (0..groups.len()).rev() {
            group_suffix_avail[i] = group_suffix_avail[i + 1] + groups[i].1.len();
        }
    }

    #[inline]
    fn push_normalized(
        ordered: &[usize],
        picked_positions: &[usize],
        out: &mut Vec<SmallVec<[usize; 8]>>,
    ) {
        let mut sel: SmallVec<[usize; 8]> =
            picked_positions.iter().map(|&p| ordered[p]).collect();
        sel.sort_unstable();
        out.push(sel);
    }

    #[allow(clippy::too_many_arguments)]
    fn dfs_prefix_plain(
        start: usize,
        max_pos: usize,
        target_len: usize,
        ordered: &[usize],
        picked: &mut SmallVec<[usize; 8]>,
        out: &mut Vec<SmallVec<[usize; 8]>>,
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
            picked.push(pos);
            dfs_prefix_plain(pos + 1, max_pos, target_len, ordered, picked, out, len_cap);
            picked.pop();
            if out.len() >= len_cap {
                return;
            }
        }
    }

    fn dfs_multiset_groups(
        g_idx: usize,
        need: usize,
        groups: &[(HandCardEquivKey, SmallVec<[usize; 8]>)],
        suffix_avail: &[usize],
        picked: &mut SmallVec<[usize; 8]>,
        out: &mut Vec<SmallVec<[usize; 8]>>,
        len_cap: usize,
    ) {
        if out.len() >= len_cap {
            return;
        }
        if need == 0 {
            let mut sel = picked.clone();
            sel.sort_unstable();
            out.push(sel);
            return;
        }
        if g_idx >= groups.len() || need > suffix_avail[g_idx] {
            return;
        }
        let avail_here = groups[g_idx].1.len();
        let avail_after = suffix_avail[g_idx + 1];
        let min_take = need.saturating_sub(avail_after);
        let max_take = need.min(avail_here);
        for take in (min_take..=max_take).rev() {
            let base_len = picked.len();
            for &orig_idx in groups[g_idx].1.iter().take(take) {
                picked.push(orig_idx);
            }
            dfs_multiset_groups(
                g_idx + 1,
                need - take,
                groups,
                suffix_avail,
                picked,
                out,
                len_cap,
            );
            picked.truncate(base_len);
            if out.len() >= len_cap {
                return;
            }
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn enumerate_for_len_into(
        len: usize,
        n: usize,
        ordered: &[usize],
        groups: &[(HandCardEquivKey, SmallVec<[usize; 8]>)],
        group_suffix_avail: &[usize],
        enable_dedup: bool,
        len_cap: usize,
        out: &mut Vec<SmallVec<[usize; 8]>>,
    ) {
        if len == 0 {
            out.push(SmallVec::new());
            return;
        }
        if len == 1 {
            if enable_dedup {
                for (_, idxs) in groups {
                    out.push(smallvec::smallvec![idxs[0]]);
                    if out.len() >= len_cap {
                        break;
                    }
                }
            } else {
                for &orig_idx in ordered {
                    out.push(smallvec::smallvec![orig_idx]);
                    if out.len() >= len_cap {
                        break;
                    }
                }
            }
            return;
        }

        let mut picked: SmallVec<[usize; 8]> = SmallVec::with_capacity(len);
        if enable_dedup {
            dfs_multiset_groups(0, len, groups, group_suffix_avail, &mut picked, out, len_cap);
            return;
        }

        for max_pos in (len - 1)..n {
            if out.len() >= len_cap {
                break;
            }
            picked.clear();
            picked.push(max_pos);
            dfs_prefix_plain(0, max_pos, len, ordered, &mut picked, out, len_cap);
        }
    }

    if req.can_skip && req.min_count == 0 {
        results.push(SmallVec::new());
    }

    let min_pick = req.min_count.max(usize::from(req.can_skip));
    let max_pick = req.max_count.min(n);

    if min_pick > max_pick {
        if results.is_empty() {
            results.push(SmallVec::new());
        }
        return;
    }

    if min_pick == max_pick {
        let rem_cap = results.len().saturating_add(cap);
        if results.len() < rem_cap {
            enumerate_for_len_into(
                min_pick,
                n,
                &ordered,
                &groups,
                &group_suffix_avail,
                enable_multiset_dedup,
                rem_cap,
                results,
            );
        }
    } else {
        scratch.flat.clear();
        scratch.ranges.clear();
        let mut total = results.len();
        for len in min_pick..=max_pick {
            let start = scratch.flat.len();
            enumerate_for_len_into(
                len,
                n,
                &ordered,
                &groups,
                &group_suffix_avail,
                enable_multiset_dedup,
                start.saturating_add(cap),
                &mut scratch.flat,
            );
            let end = scratch.flat.len();
            total = total.saturating_add(end - start);
            scratch.ranges.push((start, end));
        }
        if total <= cap {
            results.extend(scratch.flat.drain(..));
        } else {
            let mut offsets: SmallVec<[usize; 8]> =
                scratch.ranges.iter().map(|&(s, _)| s).collect();
            while results.len() < cap {
                let mut progressed = false;
                for (g_idx, &(_, end)) in scratch.ranges.iter().enumerate() {
                    if results.len() >= cap {
                        break;
                    }
                    let pos = offsets[g_idx];
                    if pos < end {
                        results.push(std::mem::take(&mut scratch.flat[pos]));
                        offsets[g_idx] = pos + 1;
                        progressed = true;
                    }
                }
                if !progressed {
                    break;
                }
            }
            scratch.flat.clear();
        }
    }

    if results.is_empty() {
        results.push(SmallVec::new());
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::packer::empty_scratch_state;
    use std::collections::BTreeSet;
    use std::sync::Arc;
    use sts2_content::Sts2Content;
    use sts2_kernel::continuation::ChoiceTiming;
    use sts2_kernel::state::{CardInst, CardTempFlags, RootStaticContext};

    fn make_card(handle: u32, id: CardId, base_cost: i32) -> CardInst {
        CardInst {
            handle: CardHandle(handle),
            id,
            upgrade_level: 0,
            canonical_base_energy_cost: base_cost as i16,
            base_energy_cost: base_cost as i16,
            captured_energy_x_value: 0,
            base_replay_count: 0,
            floor_added_to_deck: sts2_kernel::OptI16::NONE,
            deck_card_handle: sts2_kernel::OptCardHandle::NONE,
            clone_of_handle: sts2_kernel::OptCardHandle::NONE,
            temp_star_cost_override_this_turn: sts2_kernel::OptI16::NONE,
            temp_cost_override_this_turn: sts2_kernel::OptI16::NONE,
            temp_cost_override_this_turn_or_until_played: sts2_kernel::OptI16::NONE,
            temp_cost_override_this_combat: sts2_kernel::OptI16::NONE,
            local_keywords: CardKeywords::empty(),
            keywords_added_from_outside: CardKeywords::empty(),
            keywords_removed_from_outside: CardKeywords::empty(),
            temp_flags: CardTempFlags::empty(),
            affliction: None,
            ext_index: None,
            pile: PileType::Hand,
            removed_from_state: false,
            transform_into_after_play: None,
        }
    }

    #[test]
    fn multiset_choice_dedup_handles_interleaved_duplicates() {
        // [A, Dup1, B, C, Dup2] pick 3 in Hand (which interleaves low/high quality):
        // Cards A=Strike (60), Dup1=Defend (60), B=Armaments (200+), C=BattleTrance (200+), Dup2=Strike (60)
        // Expected distinct multisets of size 3 over {S(2), D(1), Arm(1), BT(1)}:
        // - {S, S, D}, {S, S, Arm}, {S, S, BT} (3)
        // - {S, D, Arm}, {S, D, BT}, {S, Arm, BT} (3)
        // - {D, Arm, BT} (1) -> total 7 distinct multisets!
        let mut state = empty_scratch_state(Arc::new(RootStaticContext::default()));
        let cards = [
            make_card(1, CardId::StrikeIronclad, 1),   // S
            make_card(2, CardId::DefendIronclad, 1),   // D
            make_card(3, CardId::Armaments, 1),        // Arm
            make_card(4, CardId::BattleTrance, 0),     // BT
            make_card(5, CardId::StrikeIronclad, 1),   // S (dup)
        ];
        state.cards.extend_from_slice(&cards);
        let handles: SmallVec<[CardHandle; 16]> = cards.iter().map(|c| c.handle).collect();
        state.player.hand = handles.to_vec();

        let req = ChoiceRequest {
            timing: ChoiceTiming::Action,
            source_id: "TEST".into(),
            source_pile: PileType::Hand,
            min_count: 3,
            max_count: 3,
            candidates: handles,
            can_skip: false,
        };
        let results = enumerate_canonical_choices(&mut state, &Sts2Content, &req, 64, true);
        let mut seen_multisets: BTreeSet<Vec<(CardId, i32)>> = BTreeSet::new();
        for sel in &results {
            assert_eq!(sel.len(), 3);
            let mut m: Vec<(CardId, i32)> = sel
                .iter()
                .map(|&idx| (cards[idx].id, cards[idx].upgrade_level))
                .collect();
            m.sort_by_key(|&(id, upg)| (id as u16, upg));
            assert!(
                seen_multisets.insert(m),
                "duplicate canonical choice multiset emitted: {sel:?}"
            );
        }
        assert_eq!(
            seen_multisets.len(),
            7,
            "dropped valid interleaved multiset combinations: {results:?}"
        );
    }
}
