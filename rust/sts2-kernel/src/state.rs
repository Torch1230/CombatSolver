use crate::content_dispatch::{CardRolloutProfile, ContentDispatcher, HookListener, HookMaskTableView};
use crate::continuation::PendingContinuation;
use crate::error::KernelError;
use bitflags::bitflags;
use serde::de::{MapAccess, SeqAccess, Visitor};
use serde::ser::SerializeMap;
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use smallvec::SmallVec;
use std::borrow::Cow;
use std::cell::RefCell;
use std::sync::{Arc, LazyLock};
use sts2_types::{
    AfflictionId, ArrowStatus, AscensionLevel, CardId, CardKeywords, CardMeta, CardType,
    CharacterId, CombatSide, Dec, DominionId, EnchantmentId, EnchantmentStatus, GameMode, HookMask,
    LocalEnergyModifierKind, MonsterId, OrbId, PileType, PlayerRngSet, PlayerTurnPhase, PotionId,
    PowerId, PowerMeta, RelicId, RoomType, Rng, RunModelId, RunRngSet, TargetType, ValueProp,
    is_ascension_active_in_mode,
};

pub const SYM_FALLBACK_ID: u16 = u16::MAX;
const SYM_HASH_SEEDS: [u64; 4] = [
    0x517c_c1b7_2722_0a95,
    0x9e37_79b9_7f4a_7c15,
    0xbf58_476d_1ce4_e5b9,
    0x94d0_49bb_1331_11eb,
];

#[inline(always)]
fn sym_hasher() -> ahash::RandomState {
    ahash::RandomState::with_seeds(
        SYM_HASH_SEEDS[0],
        SYM_HASH_SEEDS[1],
        SYM_HASH_SEEDS[2],
        SYM_HASH_SEEDS[3],
    )
}

struct GlobalSymInner {
    by_id: Vec<&'static str>,
    map: hashbrown::HashMap<&'static str, u16, ahash::RandomState>,
}

static GLOBAL_SYMS: LazyLock<parking_lot::RwLock<GlobalSymInner>> = LazyLock::new(|| {
    parking_lot::RwLock::new(GlobalSymInner {
        by_id: Vec::with_capacity(512),
        map: hashbrown::HashMap::with_capacity_and_hasher(512, sym_hasher()),
    })
});

struct TlsSymCache {
    by_id: Vec<&'static str>,
    by_str: hashbrown::HashMap<&'static str, u16, ahash::RandomState>,
}

impl Default for TlsSymCache {
    fn default() -> Self {
        Self {
            by_id: Vec::with_capacity(512),
            by_str: hashbrown::HashMap::with_capacity_and_hasher(512, sym_hasher()),
        }
    }
}

thread_local! {
    static TLS_SYMS: RefCell<TlsSymCache> = RefCell::new(TlsSymCache::default());
}

#[inline]
fn sync_tls_from_global(tls: &mut TlsSymCache, g: &GlobalSymInner) {
    if tls.by_id.len() < g.by_id.len() {
        let start = tls.by_id.len();
        tls.by_id.extend_from_slice(&g.by_id[start..]);
        for (offset, &s) in g.by_id[start..].iter().enumerate() {
            tls.by_str.insert(s, (start + offset) as u16);
        }
    }
}

#[cold]
#[inline(never)]
fn intern_slow(tls: &mut TlsSymCache, s: &str, static_ref: Option<&'static str>) -> u16 {
    {
        let r = GLOBAL_SYMS.read();
        sync_tls_from_global(tls, &r);
        if let Some(&id) = r.map.get(s) {
            return id;
        }
    }
    let mut w = GLOBAL_SYMS.write();
    if let Some(&id) = w.map.get(s) {
        sync_tls_from_global(tls, &w);
        return id;
    }
    if w.by_id.len() >= SYM_FALLBACK_ID as usize {
        sync_tls_from_global(tls, &w);
        return SYM_FALLBACK_ID;
    }
    let id = w.by_id.len() as u16;
    let interned: &'static str = match static_ref {
        Some(sr) => sr,
        None => Box::leak(s.to_owned().into_boxed_str()),
    };
    w.by_id.push(interned);
    w.map.insert(interned, id);
    sync_tls_from_global(tls, &w);
    id
}

#[inline]
pub fn intern_str(s: &str) -> u16 {
    TLS_SYMS.with(|cell| {
        let mut tls = cell.borrow_mut();
        if let Some(&id) = tls.by_str.get(s) {
            return id;
        }
        intern_slow(&mut tls, s, None)
    })
}

#[inline]
pub fn intern_static(s: &'static str) -> u16 {
    TLS_SYMS.with(|cell| {
        let mut tls = cell.borrow_mut();
        if let Some(&id) = tls.by_str.get(s) {
            return id;
        }
        intern_slow(&mut tls, s, Some(s))
    })
}

#[inline]
pub fn sym_str(id: u16) -> Option<&'static str> {
    if id == SYM_FALLBACK_ID {
        return None;
    }
    TLS_SYMS.with(|cell| {
        let mut tls = cell.borrow_mut();
        let idx = id as usize;
        if idx < tls.by_id.len() {
            return Some(tls.by_id[idx]);
        }
        let r = GLOBAL_SYMS.read();
        sync_tls_from_global(&mut tls, &r);
        tls.by_id.get(idx).copied()
    })
}

#[inline]
pub fn sym_cow(s: &str) -> Cow<'static, str> {
    TLS_SYMS.with(|cell| {
        let mut tls = cell.borrow_mut();
        let id = if let Some(&id) = tls.by_str.get(s) {
            id
        } else {
            intern_slow(&mut tls, s, None)
        };
        if id != SYM_FALLBACK_ID {
            Cow::Borrowed(tls.by_id[id as usize])
        } else {
            Cow::Owned(s.to_owned())
        }
    })
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(transparent)]
pub struct CardHandle(pub u32);

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(transparent)]
pub struct CreatureHandle(pub u32);

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub enum FieldValue {
    Int(i32),
    Decimal(Dec),
    Bool(bool),
    String(String),
    NullableInt(Option<i32>),
    CardHandle(i32),
    CreatureHandle(i32),
    IntArray(Vec<i32>),
    CardHandleArray(Vec<i32>),
    CreatureHandleArray(Vec<i32>),
    CardIntMap(Vec<(i32, i32)>),
    CreatureDecimalMap(Vec<(i32, Dec)>),
    CardPairArray(Vec<(i32, i32)>),
}

fn field_borrow_entry<'a>(
    entry: &'a (Cow<'static, str>, FieldValue),
) -> (&'a str, &'a FieldValue) {
    (entry.0.as_ref(), &entry.1)
}

fn field_borrow_entry_mut<'a>(
    entry: &'a mut (Cow<'static, str>, FieldValue),
) -> (&'a str, &'a mut FieldValue) {
    (entry.0.as_ref(), &mut entry.1)
}

/// Entry handle for `FieldMap::entry`.
pub struct FieldMapEntry<'a> {
    entries: &'a mut SmallVec<[(Cow<'static, str>, FieldValue); 4]>,
    key: Cow<'static, str>,
    pos: Result<usize, usize>,
}

impl<'a> FieldMapEntry<'a> {
    #[inline]
    pub fn or_insert_with<F: FnOnce() -> FieldValue>(self, default: F) -> &'a mut FieldValue {
        match self.pos {
            Ok(idx) => &mut self.entries[idx].1,
            Err(idx) => {
                self.entries.insert(idx, (self.key, default()));
                &mut self.entries[idx].1
            }
        }
    }

    #[inline]
    pub fn or_insert(self, default: FieldValue) -> &'a mut FieldValue {
        self.or_insert_with(|| default)
    }
}

/// Inline sorted field map replacing per-instance `BTreeMap` heap allocations on hot structs.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct FieldMap {
    entries: SmallVec<[(Cow<'static, str>, FieldValue); 4]>,
}

impl FieldMap {
    #[inline]
    pub fn new() -> Self {
        Self {
            entries: SmallVec::new(),
        }
    }

    #[inline]
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    #[inline]
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    #[inline]
    pub fn clear(&mut self) {
        self.entries.clear();
    }

    #[inline]
    pub fn truncate(&mut self, len: usize) {
        self.entries.truncate(len);
    }

    #[inline]
    pub fn entry_matches_at(&self, idx: usize, key: &str) -> bool {
        self.entries.get(idx).is_some_and(|(k, _)| k.as_ref() == key)
    }

    #[inline]
    pub fn set_value_at(&mut self, idx: usize, value: FieldValue) {
        self.entries[idx].1 = value;
    }

    #[inline]
    pub fn get(&self, key: &str) -> Option<&FieldValue> {
        self.entries
            .binary_search_by(|(k, _)| k.as_ref().cmp(key))
            .ok()
            .map(|idx| &self.entries[idx].1)
    }

    #[inline]
    pub fn get_mut(&mut self, key: &str) -> Option<&mut FieldValue> {
        self.entries
            .binary_search_by(|(k, _)| k.as_ref().cmp(key))
            .ok()
            .map(|idx| &mut self.entries[idx].1)
    }

    #[inline]
    pub fn contains_key(&self, key: &str) -> bool {
        self.get(key).is_some()
    }

    #[inline]
    pub fn entry<K: Into<Cow<'static, str>>>(&mut self, key: K) -> FieldMapEntry<'_> {
        let key = key.into();
        let pos = self
            .entries
            .binary_search_by(|(k, _)| k.as_ref().cmp(key.as_ref()));
        FieldMapEntry {
            entries: &mut self.entries,
            key,
            pos,
        }
    }

    #[inline]
    pub fn insert<K: Into<Cow<'static, str>>>(
        &mut self,
        key: K,
        value: FieldValue,
    ) -> Option<FieldValue> {
        let key = key.into();
        match self
            .entries
            .binary_search_by(|(k, _)| k.as_ref().cmp(key.as_ref()))
        {
            Ok(idx) => Some(std::mem::replace(&mut self.entries[idx].1, value)),
            Err(idx) => {
                self.entries.insert(idx, (key, value));
                None
            }
        }
    }

    #[inline]
    pub fn set_str(&mut self, key: &str, value: FieldValue) {
        match self.entries.binary_search_by(|(k, _)| k.as_ref().cmp(key)) {
            Ok(idx) => self.entries[idx].1 = value,
            Err(idx) => self.entries.insert(idx, (Cow::Owned(key.to_owned()), value)),
        }
    }

    #[inline]
    pub fn overwrite_sorted_cow_at(
        &mut self,
        idx: usize,
        key: Cow<'static, str>,
        value: FieldValue,
    ) {
        debug_assert!(idx == 0 || self.entries[idx - 1].0.as_ref() < key.as_ref());
        if idx < self.entries.len() {
            let entry = &mut self.entries[idx];
            if entry.0.as_ref() != key.as_ref() {
                entry.0 = key;
            }
            entry.1 = value;
        } else {
            self.entries.push((key, value));
        }
    }

    #[inline]
    pub fn overwrite_sorted_str_at(&mut self, idx: usize, key: &str, value: FieldValue) {
        self.overwrite_sorted_cow_at(idx, sym_cow(key), value);
    }

    #[inline]
    pub fn remove(&mut self, key: &str) -> Option<FieldValue> {
        self.entries
            .binary_search_by(|(k, _)| k.as_ref().cmp(key))
            .ok()
            .map(|idx| self.entries.remove(idx).1)
    }

    #[inline]
    pub fn iter<'a>(&'a self) -> std::iter::Map<std::slice::Iter<'a, (Cow<'static, str>, FieldValue)>, fn(&'a (Cow<'static, str>, FieldValue)) -> (&'a str, &'a FieldValue)> {
        self.entries.iter().map(field_borrow_entry)
    }

    #[inline]
    pub fn iter_mut<'a>(&'a mut self) -> std::iter::Map<std::slice::IterMut<'a, (Cow<'static, str>, FieldValue)>, fn(&'a mut (Cow<'static, str>, FieldValue)) -> (&'a str, &'a mut FieldValue)> {
        self.entries.iter_mut().map(field_borrow_entry_mut)
    }

    #[inline]
    pub fn iter_raw(&self) -> std::slice::Iter<'_, (Cow<'static, str>, FieldValue)> {
        self.entries.iter()
    }

    #[inline]
    pub fn clone_into_map(&self, dst: &mut FieldMap) {
        if self.entries.is_empty() {
            if !dst.entries.is_empty() {
                dst.entries.clear();
            }
            return;
        }
        if dst.entries.len() == self.entries.len()
            && dst
                .entries
                .iter()
                .zip(self.entries.iter())
                .all(|((dk, _), (sk, _))| dk.as_ref() == sk.as_ref())
        {
            for ((_, dv), (_, sv)) in dst.entries.iter_mut().zip(self.entries.iter()) {
                if dv != sv {
                    dv.clone_from(sv);
                }
            }
        } else {
            dst.clone_from(self);
        }
    }
}

impl<'a> IntoIterator for &'a FieldMap {
    type Item = (&'a str, &'a FieldValue);
    type IntoIter = std::iter::Map<
        std::slice::Iter<'a, (Cow<'static, str>, FieldValue)>,
        fn(&'a (Cow<'static, str>, FieldValue)) -> (&'a str, &'a FieldValue),
    >;

    #[inline]
    fn into_iter(self) -> Self::IntoIter {
        self.iter()
    }
}

impl<'a> IntoIterator for &'a mut FieldMap {
    type Item = (&'a str, &'a mut FieldValue);
    type IntoIter = std::iter::Map<
        std::slice::IterMut<'a, (Cow<'static, str>, FieldValue)>,
        fn(&'a mut (Cow<'static, str>, FieldValue)) -> (&'a str, &'a mut FieldValue),
    >;

    #[inline]
    fn into_iter(self) -> Self::IntoIter {
        self.iter_mut()
    }
}

impl IntoIterator for FieldMap {
    type Item = (Cow<'static, str>, FieldValue);
    type IntoIter = smallvec::IntoIter<[(Cow<'static, str>, FieldValue); 4]>;

    #[inline]
    fn into_iter(self) -> Self::IntoIter {
        self.entries.into_iter()
    }
}

impl FromIterator<(Cow<'static, str>, FieldValue)> for FieldMap {
    fn from_iter<T: IntoIterator<Item = (Cow<'static, str>, FieldValue)>>(iter: T) -> Self {
        let mut map = Self::new();
        for (k, v) in iter {
            map.insert(k, v);
        }
        map
    }
}

impl FromIterator<(String, FieldValue)> for FieldMap {
    fn from_iter<T: IntoIterator<Item = (String, FieldValue)>>(iter: T) -> Self {
        let mut map = Self::new();
        for (k, v) in iter {
            map.insert(k, v);
        }
        map
    }
}

impl FromIterator<(&'static str, FieldValue)> for FieldMap {
    fn from_iter<T: IntoIterator<Item = (&'static str, FieldValue)>>(iter: T) -> Self {
        let mut map = Self::new();
        for (k, v) in iter {
            map.insert(k, v);
        }
        map
    }
}

impl<const N: usize> From<[(String, FieldValue); N]> for FieldMap {
    fn from(arr: [(String, FieldValue); N]) -> Self {
        arr.into_iter().collect()
    }
}

impl<const N: usize> From<[(&'static str, FieldValue); N]> for FieldMap {
    fn from(arr: [(&'static str, FieldValue); N]) -> Self {
        arr.into_iter().collect()
    }
}

impl<const N: usize> From<[(Cow<'static, str>, FieldValue); N]> for FieldMap {
    fn from(arr: [(Cow<'static, str>, FieldValue); N]) -> Self {
        arr.into_iter().collect()
    }
}

impl Serialize for FieldMap {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut map = serializer.serialize_map(Some(self.entries.len()))?;
        for (k, v) in &self.entries {
            map.serialize_entry(k.as_ref(), v)?;
        }
        map.end()
    }
}

impl<'de> Deserialize<'de> for FieldMap {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct FieldMapVisitor;
        impl<'de> Visitor<'de> for FieldMapVisitor {
            type Value = FieldMap;
            fn expecting(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                formatter.write_str("a map of field names to values")
            }
            fn visit_map<A: MapAccess<'de>>(self, mut access: A) -> Result<FieldMap, A::Error> {
                let mut out = FieldMap::new();
                while let Some((k, v)) = access.next_entry::<Cow<'de, str>, FieldValue>()? {
                    out.insert(sym_cow(k.as_ref()), v);
                }
                Ok(out)
            }
        }
        deserializer.deserialize_map(FieldMapVisitor)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct LocalEnergyModifier {
    pub kind: LocalEnergyModifierKind,
    pub amount: i32,
    pub is_reduce_only: bool,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct EnchantmentInst {
    pub id: EnchantmentId,
    pub amount: i32,
    pub status: EnchantmentStatus,
    pub private_fields: FieldMap,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct AfflictionInst {
    pub id: AfflictionId,
    pub amount: i32,
}

bitflags! {
    #[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Default)]
    pub struct CardTempFlags: u16 {
        const EXHAUST_ON_NEXT_PLAY   = 1 << 0;
        const IS_DUPE                = 1 << 1;
        const COSTS_X_ENERGY         = 1 << 2;
        const TEMPORARY_FREE_TURN    = 1 << 3;
        const TEMPORARY_FREE_PLAY    = 1 << 4;
        const TEMPORARY_FREE_COMBAT  = 1 << 5;
        const TEMPORARY_RETAIN_TURN  = 1 << 6;
        const TEMPORARY_SLY_TURN     = 1 << 7;
    }
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct CardExtension {
    pub local_energy_modifiers: SmallVec<[LocalEnergyModifier; 4]>,
    pub enchantment: Option<EnchantmentInst>,
    pub private_fields: FieldMap,
}

/// Compact hot struct for a card instance; infrequent/variable fields spill into `card_extensions`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CardInst {
    pub handle: CardHandle,
    pub id: CardId,
    pub upgrade_level: i32,
    pub canonical_base_energy_cost: i32,
    pub base_energy_cost: i32,
    pub captured_energy_x_value: i32,
    pub base_replay_count: i32,
    pub floor_added_to_deck: Option<i32>,
    pub deck_card_handle: Option<CardHandle>,
    pub clone_of_handle: Option<CardHandle>,
    pub temp_star_cost_override_this_turn: Option<i32>,
    pub temp_cost_override_this_turn: Option<i32>,
    pub temp_cost_override_this_turn_or_until_played: Option<i32>,
    pub temp_cost_override_this_combat: Option<i32>,
    pub local_keywords: CardKeywords,
    pub keywords_added_from_outside: CardKeywords,
    pub keywords_removed_from_outside: CardKeywords,
    pub temp_flags: CardTempFlags,
    pub affliction: Option<AfflictionInst>,
    pub ext_index: Option<u32>,
    pub pile: PileType,
    pub removed_from_state: bool,
    pub transform_into_after_play: Option<CardId>,
}

impl CardInst {
    pub fn has_local_keyword(&self, kw: CardKeywords) -> bool {
        if self.keywords_removed_from_outside.contains(kw) {
            return false;
        }
        self.local_keywords.contains(kw) || self.keywords_added_from_outside.contains(kw)
    }

    pub fn effective_keywords_base(&self) -> CardKeywords {
        (self.local_keywords | self.keywords_added_from_outside)
            & !self.keywords_removed_from_outside
    }
}

/// Exact 26 counters matching `InCombatPlayerCountersDto` / `LivePlayerCombatCountersSpec`.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerCounters {
    pub stars_gained_this_turn: Dec,
    pub skill_cards_played_this_turn: i32,
    pub attack_cards_played_this_turn: i32,
    pub shivs_played_this_turn: i32,
    pub cards_discarded_this_turn: i32,
    pub cards_played_this_combat: i32,
    pub cards_generated_this_combat: i32,
    pub sadness_generated_this_combat: i32,
    pub cards_drawn_this_combat: i32,
    pub cards_played_this_turn: i32,
    pub manual_cards_played_this_turn: i32,
    pub card_plays_started_this_turn: i32,
    pub attack_or_skill_card_plays_started_this_turn: i32,
    pub attack_cards_started_this_turn: i32,
    pub zero_cost_attacks_started_this_turn: i32,
    pub first_in_series_card_plays_started_this_turn: i32,
    pub wah_started_this_turn: i32,
    pub cho_started_this_turn: i32,
    pub ki_started_this_turn: i32,
    pub first_wah_play_ordinal_this_turn: i32,
    pub first_cho_play_ordinal_this_turn: i32,
    pub first_ki_play_ordinal_this_turn: i32,
    pub wah_finished_this_turn: i32,
    pub cho_finished_this_turn: i32,
    pub ki_finished_this_turn: i32,
    pub melody_cards_finished_this_turn: i32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct OrbInst {
    pub id: OrbId,
    pub raw_evoke_value: Option<Dec>,
    pub raw_passive_value: Option<Dec>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ArrowSlotInst {
    pub status: ArrowStatus,
    pub destroy_after_impaled: bool,
}

impl ArrowSlotInst {
    #[inline]
    pub const fn is_available(self) -> bool {
        matches!(self.status, ArrowStatus::Retrieved)
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RelicInst {
    pub id: RelicId,
    pub is_wax: bool,
    pub is_melted: bool,
    pub stack_count: i32,
    pub floor_added_to_deck: Option<i32>,
    pub private_fields: FieldMap,
}

/// Inline smallvec storage keeps standard runs stack/flat without capacity truncation on 2305+ card decks.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PlayerInst {
    pub creature_handle: CreatureHandle,
    pub current_hp: i32,
    pub max_hp: i32,
    pub block: i32,
    pub cumulative_hp_lost: i32,
    pub gold: i32,
    pub max_energy: i32,
    pub base_orb_slot_count: i32,
    pub is_active_for_hooks: bool,
    pub can_use_or_remove_potions: bool,
    pub energy: i32,
    pub stars: i32,
    pub phase: PlayerTurnPhase,
    pub turn_number: i32,
    pub counters: PlayerCounters,
    pub hand: Vec<CardHandle>,
    pub draw: Vec<CardHandle>,
    pub discard: Vec<CardHandle>,
    pub exhaust: Vec<CardHandle>,
    pub play: Vec<CardHandle>,
    pub deck: Vec<CardHandle>,
    pub potion_slots: SmallVec<[Option<PotionId>; 5]>,
    pub combat_generated_potion_mask: u16,
    pub orb_capacity: i32,
    pub orbs: SmallVec<[OrbInst; 10]>,
    pub arrows: SmallVec<[ArrowSlotInst; 4]>,
    pub relics: SmallVec<[RelicInst; 16]>,
    pub card_or_potion_effect_depth: i32,
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct PowerExtension {
    pub private_fields: FieldMap,
    pub attack_command_id: Option<u64>,
    pub amount_when_attack_started: i32,
    pub impaled_arrows: SmallVec<[usize; 4]>,
    pub hit_counter: i32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct PowerInst {
    pub handle: u32,
    pub id: PowerId,
    pub amount: i32,
    pub amount_on_turn_start: i32,
    pub skip_next_duration_tick: bool,
    pub applier_handle: Option<CreatureHandle>,
    pub ext_index: Option<u32>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MonsterInst {
    pub monster_id: MonsterId,
    pub rng: Rng,
    pub is_performing_move: bool,
    pub spawned_this_turn: bool,
    pub current_state_id: Cow<'static, str>,
    pub next_move_id: Option<Cow<'static, str>>,
    pub next_move_can_transition_away: bool,
    pub performed_first_move: bool,
    pub state_log_ids: SmallVec<[Cow<'static, str>; 8]>,
    pub transient_move_follow_up_state_id: Option<Cow<'static, str>>,
    pub setup_fields: FieldMap,
    pub private_fields: FieldMap,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CreatureInst {
    pub handle: CreatureHandle,
    pub combat_id: Option<u32>,
    pub side: CombatSide,
    pub slot_name: Option<Cow<'static, str>>,
    pub is_player: bool,
    pub pet_owner_player_index: Option<usize>,
    pub monster_id: Option<MonsterId>,
    pub current_hp: i32,
    pub max_hp: i32,
    pub block: i32,
    pub cumulative_hp_lost: i32,
    pub is_detached: bool,
    pub has_minion_power: bool,
    pub monster: Option<MonsterInst>,
    pub powers: Vec<PowerInst>,
}

impl CreatureInst {
    #[inline]
    pub const fn is_alive(&self) -> bool {
        self.current_hp > 0
    }

    #[inline]
    pub const fn is_dead(&self) -> bool {
        self.current_hp <= 0
    }

    #[inline]
    pub const fn is_pet(&self) -> bool {
        self.pet_owner_player_index.is_some()
    }

    #[inline]
    pub const fn is_monster(&self) -> bool {
        self.monster.is_some()
    }

    #[inline(always)]
    pub fn is_secondary_enemy(&self) -> bool {
        #[cfg(debug_assertions)]
        debug_assert_eq!(
            self.has_minion_power,
            self.powers.iter().any(|p| p.id == PowerId::MinionPower),
            "CreatureInst.has_minion_power drifted from powers vec"
        );
        self.side == CombatSide::Enemy && self.has_minion_power
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DamageResult {
    pub receiver: CreatureHandle,
    pub original_target: CreatureHandle,
    pub props: ValueProp,
    pub intended_hp_loss: i32,
    pub blocked_damage: i32,
    pub unblocked_damage: i32,
    pub overkill_damage: i32,
    pub was_target_killed: bool,
    pub was_block_broken: bool,
    pub was_fully_blocked: bool,
}

#[derive(Clone, Copy, Debug, Default)]
pub struct InlineI32Vec4 {
    data: [i32; 4],
    len: u8,
}

impl InlineI32Vec4 {
    #[inline]
    pub const fn new() -> Self {
        Self {
            data: [0; 4],
            len: 0,
        }
    }

    #[inline]
    pub const fn one(v: i32) -> Self {
        Self {
            data: [v, 0, 0, 0],
            len: 1,
        }
    }

    #[inline]
    pub fn from_slice(slice: &[i32]) -> Self {
        let mut out = Self::new();
        for &v in slice {
            out.push(v);
        }
        out
    }

    #[inline]
    pub fn push(&mut self, v: i32) {
        let idx = self.len as usize;
        if idx < 4 {
            self.data[idx] = v;
            self.len += 1;
        }
    }

    #[inline]
    pub fn clear(&mut self) {
        self.len = 0;
    }

    #[inline]
    pub fn as_slice(&self) -> &[i32] {
        &self.data[..(self.len as usize).min(4)]
    }
}

impl PartialEq for InlineI32Vec4 {
    #[inline]
    fn eq(&self, other: &Self) -> bool {
        self.as_slice() == other.as_slice()
    }
}

impl Eq for InlineI32Vec4 {}

impl std::hash::Hash for InlineI32Vec4 {
    #[inline]
    fn hash<H: std::hash::Hasher>(&self, state: &mut H) {
        self.as_slice().hash(state);
    }
}

impl std::ops::Deref for InlineI32Vec4 {
    type Target = [i32];

    #[inline]
    fn deref(&self) -> &[i32] {
        self.as_slice()
    }
}

impl<'a> IntoIterator for &'a InlineI32Vec4 {
    type Item = &'a i32;
    type IntoIter = std::slice::Iter<'a, i32>;

    #[inline]
    fn into_iter(self) -> Self::IntoIter {
        self.as_slice().iter()
    }
}

impl FromIterator<i32> for InlineI32Vec4 {
    fn from_iter<T: IntoIterator<Item = i32>>(iter: T) -> Self {
        let mut out = Self::new();
        for v in iter {
            out.push(v);
        }
        out
    }
}

impl Serialize for InlineI32Vec4 {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.collect_seq(self.as_slice())
    }
}

impl<'de> Deserialize<'de> for InlineI32Vec4 {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct SeqVisitor;
        impl<'de> Visitor<'de> for SeqVisitor {
            type Value = InlineI32Vec4;
            fn expecting(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.write_str("a sequence of i32")
            }
            fn visit_seq<A: SeqAccess<'de>>(self, mut seq: A) -> Result<Self::Value, A::Error> {
                let mut out = InlineI32Vec4::new();
                while let Some(v) = seq.next_element::<i32>()? {
                    out.push(v);
                }
                Ok(out)
            }
        }
        deserializer.deserialize_seq(SeqVisitor)
    }
}

#[derive(Clone, Copy, Debug, Default)]
pub struct InlineOptI32Vec4 {
    data: [Option<i32>; 4],
    len: u8,
}

impl InlineOptI32Vec4 {
    #[inline]
    pub const fn new() -> Self {
        Self {
            data: [None; 4],
            len: 0,
        }
    }

    #[inline]
    pub const fn one(v: Option<i32>) -> Self {
        Self {
            data: [v, None, None, None],
            len: 1,
        }
    }

    #[inline]
    pub fn from_slice(slice: &[Option<i32>]) -> Self {
        let mut out = Self::new();
        for &v in slice {
            out.push(v);
        }
        out
    }

    #[inline]
    pub fn push(&mut self, v: Option<i32>) {
        let idx = self.len as usize;
        if idx < 4 {
            self.data[idx] = v;
            self.len += 1;
        }
    }

    #[inline]
    pub fn clear(&mut self) {
        self.len = 0;
    }

    #[inline]
    pub fn as_slice(&self) -> &[Option<i32>] {
        &self.data[..(self.len as usize).min(4)]
    }
}

impl PartialEq for InlineOptI32Vec4 {
    #[inline]
    fn eq(&self, other: &Self) -> bool {
        self.as_slice() == other.as_slice()
    }
}

impl Eq for InlineOptI32Vec4 {}

impl std::hash::Hash for InlineOptI32Vec4 {
    #[inline]
    fn hash<H: std::hash::Hasher>(&self, state: &mut H) {
        self.as_slice().hash(state);
    }
}

impl std::ops::Deref for InlineOptI32Vec4 {
    type Target = [Option<i32>];

    #[inline]
    fn deref(&self) -> &[Option<i32>] {
        self.as_slice()
    }
}

impl<'a> IntoIterator for &'a InlineOptI32Vec4 {
    type Item = &'a Option<i32>;
    type IntoIter = std::slice::Iter<'a, Option<i32>>;

    #[inline]
    fn into_iter(self) -> Self::IntoIter {
        self.as_slice().iter()
    }
}

impl FromIterator<Option<i32>> for InlineOptI32Vec4 {
    fn from_iter<T: IntoIterator<Item = Option<i32>>>(iter: T) -> Self {
        let mut out = Self::new();
        for v in iter {
            out.push(v);
        }
        out
    }
}

impl Serialize for InlineOptI32Vec4 {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.collect_seq(self.as_slice())
    }
}

impl<'de> Deserialize<'de> for InlineOptI32Vec4 {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct SeqVisitor;
        impl<'de> Visitor<'de> for SeqVisitor {
            type Value = InlineOptI32Vec4;
            fn expecting(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.write_str("a sequence of Option<i32>")
            }
            fn visit_seq<A: SeqAccess<'de>>(self, mut seq: A) -> Result<Self::Value, A::Error> {
                let mut out = InlineOptI32Vec4::new();
                while let Some(v) = seq.next_element::<Option<i32>>()? {
                    out.push(v);
                }
                Ok(out)
            }
        }
        deserializer.deserialize_seq(SeqVisitor)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct DamageHistoryEntry {
    pub receiver_creature_handle: CreatureHandle,
    pub dealer_creature_handle: Option<CreatureHandle>,
    pub original_target_creature_handle: CreatureHandle,
    pub props: ValueProp,
    pub intended_hp_loss: i32,
    pub blocked_damage: i32,
    pub unblocked_damage: i32,
    pub overkill_damage: i32,
    pub was_target_killed: bool,
    pub was_block_broken: bool,
    pub was_fully_blocked: bool,
    pub card_source_handle: Option<CardHandle>,
    pub round_number: i32,
    pub current_side: CombatSide,
    pub player_turn_numbers: InlineI32Vec4,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TurnKey {
    pub round_number: i32,
    pub side: CombatSide,
    pub player_turn_numbers: InlineOptI32Vec4,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Serialize, Deserialize)]
pub enum GrowthSource {
    HandOfGreed,
    TheHunt,
    Feed,
    Royalties,
    Alchemize,
    GeneticAlgorithm,
    TheScythe,
    Goopy,
    ForbiddenGrimoire,
    MadScience,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GrowthCounters {
    pub hand_of_greed: i32,
    pub the_hunt: i32,
    pub feed: i32,
    pub royalties: i32,
    pub alchemize: i32,
    pub genetic_algorithm: i32,
    pub the_scythe: i32,
    pub goopy: i32,
    pub forbidden_grimoire: i32,
    pub mad_science: i32,
}

impl GrowthCounters {
    #[inline]
    pub fn total(&self) -> i32 {
        self.hand_of_greed
            .saturating_add(self.the_hunt)
            .saturating_add(self.feed)
            .saturating_add(self.royalties)
            .saturating_add(self.alchemize)
            .saturating_add(self.genetic_algorithm)
            .saturating_add(self.the_scythe)
            .saturating_add(self.goopy)
            .saturating_add(self.forbidden_grimoire)
            .saturating_add(self.mad_science)
    }

    #[inline]
    pub fn is_zero(&self) -> bool {
        *self == Self::default()
    }

    #[inline]
    pub fn credit(&self, rewards: &Self) -> i32 {
        self.hand_of_greed
            .saturating_mul(rewards.hand_of_greed)
            .saturating_add(self.the_hunt.saturating_mul(rewards.the_hunt))
            .saturating_add(self.feed.saturating_mul(rewards.feed))
            .saturating_add(self.royalties.saturating_mul(rewards.royalties))
            .saturating_add(self.alchemize.saturating_mul(rewards.alchemize))
            .saturating_add(self.genetic_algorithm.saturating_mul(rewards.genetic_algorithm))
            .saturating_add(self.the_scythe.saturating_mul(rewards.the_scythe))
            .saturating_add(self.goopy.saturating_mul(rewards.goopy))
            .saturating_add(self.forbidden_grimoire.saturating_mul(rewards.forbidden_grimoire))
            .saturating_add(self.mad_science.saturating_mul(rewards.mad_science))
    }

    #[inline]
    pub fn satisfies(&self, required: &Self) -> bool {
        self.hand_of_greed >= required.hand_of_greed
            && self.the_hunt >= required.the_hunt
            && self.feed >= required.feed
            && self.royalties >= required.royalties
            && self.alchemize >= required.alchemize
            && self.genetic_algorithm >= required.genetic_algorithm
            && self.the_scythe >= required.the_scythe
            && self.goopy >= required.goopy
            && self.forbidden_grimoire >= required.forbidden_grimoire
            && self.mad_science >= required.mad_science
    }

    #[inline]
    pub fn get(&self, source: GrowthSource) -> i32 {
        match source {
            GrowthSource::HandOfGreed => self.hand_of_greed,
            GrowthSource::TheHunt => self.the_hunt,
            GrowthSource::Feed => self.feed,
            GrowthSource::Royalties => self.royalties,
            GrowthSource::Alchemize => self.alchemize,
            GrowthSource::GeneticAlgorithm => self.genetic_algorithm,
            GrowthSource::TheScythe => self.the_scythe,
            GrowthSource::Goopy => self.goopy,
            GrowthSource::ForbiddenGrimoire => self.forbidden_grimoire,
            GrowthSource::MadScience => self.mad_science,
        }
    }

    #[inline]
    pub fn inc(&mut self, source: GrowthSource) {
        match source {
            GrowthSource::HandOfGreed => self.hand_of_greed = self.hand_of_greed.saturating_add(1),
            GrowthSource::TheHunt => self.the_hunt = self.the_hunt.saturating_add(1),
            GrowthSource::Feed => self.feed = self.feed.saturating_add(1),
            GrowthSource::Royalties => self.royalties = self.royalties.saturating_add(1),
            GrowthSource::Alchemize => self.alchemize = self.alchemize.saturating_add(1),
            GrowthSource::GeneticAlgorithm => {
                self.genetic_algorithm = self.genetic_algorithm.saturating_add(1)
            }
            GrowthSource::TheScythe => self.the_scythe = self.the_scythe.saturating_add(1),
            GrowthSource::Goopy => self.goopy = self.goopy.saturating_add(1),
            GrowthSource::ForbiddenGrimoire => {
                self.forbidden_grimoire = self.forbidden_grimoire.saturating_add(1)
            }
            GrowthSource::MadScience => self.mad_science = self.mad_science.saturating_add(1),
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SemanticHistory {
    pub exhausts: Vec<(TurnKey, usize)>,
    pub card_block_gains: Vec<(TurnKey, usize, i32)>,
    pub orb_channels: Vec<(TurnKey, usize, OrbId)>,
    pub energy_spent: Vec<(TurnKey, usize, i32)>,
    pub card_drawn: Vec<(TurnKey, usize, CardType, bool)>,
    pub actor_events: Vec<(Cow<'static, str>, TurnKey, usize)>,
    pub growth_rewards: GrowthCounters,
    pub brightest_flame_max_hp_spent: i32,
    pub prior_player_unblocked_hits: i32,
    pub prior_player_damaged_turn: i32,
    pub prior_exhaust_count: i32,
}

impl Default for SemanticHistory {
    #[inline]
    fn default() -> Self {
        Self {
            exhausts: Vec::new(),
            card_block_gains: Vec::new(),
            orb_channels: Vec::new(),
            energy_spent: Vec::new(),
            card_drawn: Vec::new(),
            actor_events: Vec::new(),
            growth_rewards: GrowthCounters::default(),
            brightest_flame_max_hp_spent: 0,
            prior_player_unblocked_hits: 0,
            prior_player_damaged_turn: -1,
            prior_exhaust_count: 0,
        }
    }
}

impl SemanticHistory {
    pub fn current_turn_key(state: &BattleState) -> TurnKey {
        TurnKey {
            round_number: state.round_number,
            side: state.current_side,
            player_turn_numbers: InlineOptI32Vec4::one(Some(state.player.turn_number)),
        }
    }

    pub fn record_exhaust(&mut self, state: &BattleState) {
        self.exhausts.push((Self::current_turn_key(state), 0));
    }

    pub fn record_block_gain(&mut self, state: &BattleState, props: ValueProp, play_ordinal: Option<i32>) {
        if props.is_powered_block() {
            if let Some(ordinal) = play_ordinal {
                self.card_block_gains
                    .push((Self::current_turn_key(state), 0, ordinal));
            }
        }
    }

    pub fn record_orb_channel(&mut self, state: &BattleState, orb_id: OrbId) {
        self.orb_channels
            .push((Self::current_turn_key(state), 0, orb_id));
    }

    pub fn record_energy_spent(&mut self, state: &BattleState, amount: i32) {
        if amount > 0 {
            self.energy_spent
                .push((Self::current_turn_key(state), 0, amount));
        }
    }

    pub fn record_card_drawn(&mut self, state: &BattleState, card_type: CardType, from_hand_draw: bool) {
        self.card_drawn
            .push((Self::current_turn_key(state), 0, card_type, from_hand_draw));
    }

    pub fn record_actor_event(&mut self, state: &BattleState, kind: impl Into<Cow<'static, str>>) {
        self.actor_events
            .push((kind.into(), Self::current_turn_key(state), 0));
    }

    #[inline]
    pub fn record_growth(&mut self, source: GrowthSource) {
        self.growth_rewards.inc(source);
    }

    #[inline]
    pub fn record_brightest_flame_max_hp_loss(&mut self, amount: i32) {
        self.brightest_flame_max_hp_spent = self
            .brightest_flame_max_hp_spent
            .saturating_add(amount.max(0));
    }
}

#[derive(Clone, Copy, Debug, Default)]
pub struct DeckDirtyFlag(pub bool);

impl PartialEq for DeckDirtyFlag {
    #[inline]
    fn eq(&self, _other: &Self) -> bool {
        true
    }
}

impl Eq for DeckDirtyFlag {}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RootStaticContext {
    pub encounter_id: String,
    pub encounter_is_weak: bool,
    pub room_type: RoomType,
    pub encounter_slots: Vec<String>,
    pub game_mode: GameMode,
    pub ascension: u8,
    pub character_id: CharacterId,
    pub dominion_id: Option<DominionId>,
    pub run_model_ids: Vec<RunModelId>,
    pub card_generation_pools: Vec<CardId>,
    pub colorless_card_pool: Vec<CardId>,
    pub curse_card_pool: Vec<CardId>,
    pub other_character_card_pool: Vec<CardId>,
    pub tarot_card_pool: Vec<CardId>,
    pub potion_generation_pool: Vec<PotionId>,
    pub transformation_pools: Vec<CardId>,
    pub map_col: i32,
    pub map_row: i32,
    pub act_index: i32,
    pub total_floor: i32,
    pub root_run_rng_string_seed: String,
    pub root_deck_prefix_len: usize,
    pub root_deck_cards: Vec<CardInst>,
    pub root_deck_handles: Vec<CardHandle>,
    pub compact_turn_history: bool,
    pub has_root_rngs: bool,
    pub root_run_rngs: [Rng; 12],
    pub root_player_rngs: [Rng; 4],
}

impl Default for RootStaticContext {
    fn default() -> Self {
        Self {
            encounter_id: String::new(),
            encounter_is_weak: false,
            room_type: RoomType::Monster,
            encounter_slots: Vec::new(),
            game_mode: GameMode::Standard,
            ascension: 0,
            character_id: CharacterId::Ironclad,
            dominion_id: None,
            run_model_ids: Vec::new(),
            card_generation_pools: Vec::new(),
            colorless_card_pool: Vec::new(),
            curse_card_pool: Vec::new(),
            other_character_card_pool: Vec::new(),
            tarot_card_pool: Vec::new(),
            potion_generation_pool: Vec::new(),
            transformation_pools: Vec::new(),
            map_col: 0,
            map_row: 0,
            act_index: 0,
            total_floor: 0,
            root_run_rng_string_seed: String::new(),
            root_deck_prefix_len: 0,
            root_deck_cards: Vec::new(),
            root_deck_handles: Vec::new(),
            compact_turn_history: false,
            has_root_rngs: false,
            root_run_rngs: [Rng::from_seed(0); 12],
            root_player_rngs: [Rng::from_seed(0); 4],
        }
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct ActiveHookMasks {
    pub relics: HookMask,
    pub potions: HookMask,
    pub orbs: HookMask,
    pub cards_hand: HookMask,
    pub cards_draw: HookMask,
    pub cards_discard: HookMask,
    pub cards_exhaust: HookMask,
    pub cards_play: HookMask,
    pub cards_deck: HookMask,
    pub player_inv: HookMask,
    pub powers: HookMask,
    pub monsters: HookMask,
    pub pre_player: HookMask,
    pub post_player: HookMask,
    pub run_and_dominion: HookMask,
    pub combat_combined: HookMask,
    pub run_combat_combined: HookMask,
}

pub const HOOK_DIRTY_HAND: u16 = 1 << 0;
pub const HOOK_DIRTY_DRAW: u16 = 1 << 1;
pub const HOOK_DIRTY_DISCARD: u16 = 1 << 2;
pub const HOOK_DIRTY_EXHAUST: u16 = 1 << 3;
pub const HOOK_DIRTY_PLAY: u16 = 1 << 4;
pub const HOOK_DIRTY_DECK: u16 = 1 << 5;
pub const HOOK_DIRTY_CREATURES: u16 = 1 << 6;
pub const HOOK_DIRTY_RELICS: u16 = 1 << 7;
pub const HOOK_DIRTY_POTIONS: u16 = 1 << 8;
pub const HOOK_DIRTY_ORBS: u16 = 1 << 9;
pub const HOOK_DIRTY_STATIC: u16 = 1 << 10;
pub const HOOK_DIRTY_PILES: u16 = HOOK_DIRTY_HAND
    | HOOK_DIRTY_DRAW
    | HOOK_DIRTY_DISCARD
    | HOOK_DIRTY_EXHAUST
    | HOOK_DIRTY_PLAY
    | HOOK_DIRTY_DECK;
pub const HOOK_DIRTY_ALL: u16 = (1 << 11) - 1;

#[inline(always)]
pub const fn pile_dirty_bit(pile: PileType) -> u16 {
    match pile {
        PileType::Hand => HOOK_DIRTY_HAND,
        PileType::Draw => HOOK_DIRTY_DRAW,
        PileType::Discard => HOOK_DIRTY_DISCARD,
        PileType::Exhaust => HOOK_DIRTY_EXHAUST,
        PileType::Play => HOOK_DIRTY_PLAY,
        PileType::Deck => HOOK_DIRTY_DECK,
        PileType::None => 0,
    }
}

#[inline(always)]
const fn mask_at(table: &[HookMask], idx: usize) -> HookMask {
    if idx < table.len() {
        table[idx]
    } else {
        HookMask::EMPTY
    }
}

#[derive(Clone, Debug)]
pub struct ActiveHookCache {
    pub dirty_mask: u16,
    pub masks: ActiveHookMasks,
    pub listener_pre_player: Vec<(HookListener, HookMask)>,
    pub listener_post_player: Vec<(HookListener, HookMask)>,
    pub in_combat_mask: u64,
    pub in_combat_overflow: bool,
    pub cached_tables: Option<&'static HookMaskTableView>,
    pub listener_relics: Vec<(usize, HookMask)>,
    pub listener_potions: Vec<(usize, HookMask)>,
    pub listener_orbs: Vec<(usize, HookMask)>,
    pub listener_hand: Vec<(HookListener, HookMask)>,
    pub listener_draw: Vec<(HookListener, HookMask)>,
    pub listener_discard: Vec<(HookListener, HookMask)>,
    pub listener_exhaust: Vec<(HookListener, HookMask)>,
    pub listener_play: Vec<(HookListener, HookMask)>,
    pub listener_deck: Vec<(HookListener, HookMask)>,
    pub listener_run_models: Vec<(RunModelId, HookMask)>,
    pub listener_dominion: Option<(DominionId, HookMask)>,
    pub flat_masks_w0: Vec<u64>,
    pub flat_masks_w1: Vec<u64>,
    pub flat_masks_w2: Vec<u64>,
    pub flat_listeners: Vec<HookListener>,
}

impl Default for ActiveHookCache {
    #[inline]
    fn default() -> Self {
        Self {
            dirty_mask: HOOK_DIRTY_ALL,
            masks: ActiveHookMasks::default(),
            listener_pre_player: Vec::new(),
            listener_post_player: Vec::new(),
            in_combat_mask: 0,
            in_combat_overflow: false,
            cached_tables: None,
            listener_relics: Vec::new(),
            listener_potions: Vec::new(),
            listener_orbs: Vec::new(),
            listener_hand: Vec::new(),
            listener_draw: Vec::new(),
            listener_discard: Vec::new(),
            listener_exhaust: Vec::new(),
            listener_play: Vec::new(),
            listener_deck: Vec::new(),
            listener_run_models: Vec::new(),
            listener_dominion: None,
            flat_masks_w0: Vec::new(),
            flat_masks_w1: Vec::new(),
            flat_masks_w2: Vec::new(),
            flat_listeners: Vec::new(),
        }
    }
}

impl PartialEq for ActiveHookCache {
    #[inline]
    fn eq(&self, _other: &Self) -> bool {
        true
    }
}

impl Eq for ActiveHookCache {}

impl ActiveHookCache {
    #[inline(always)]
    pub const fn is_valid(&self) -> bool {
        self.dirty_mask == 0
    }

    #[inline(always)]
    pub fn flat_mask_word(&self, word: usize) -> &[u64] {
        match word {
            0 => &self.flat_masks_w0,
            1 => &self.flat_masks_w1,
            _ => &self.flat_masks_w2,
        }
    }

    #[inline]
    pub fn clear(&mut self) {
        self.dirty_mask = HOOK_DIRTY_ALL;
        self.masks = ActiveHookMasks::default();
        self.listener_pre_player.clear();
        self.listener_post_player.clear();
        self.in_combat_mask = 0;
        self.in_combat_overflow = false;
        self.cached_tables = None;
        self.listener_relics.clear();
        self.listener_potions.clear();
        self.listener_orbs.clear();
        self.listener_hand.clear();
        self.listener_draw.clear();
        self.listener_discard.clear();
        self.listener_exhaust.clear();
        self.listener_play.clear();
        self.listener_deck.clear();
        self.listener_run_models.clear();
        self.listener_dominion = None;
        self.flat_masks_w0.clear();
        self.flat_masks_w1.clear();
        self.flat_masks_w2.clear();
        self.flat_listeners.clear();
    }

    pub fn exact_eq(&self, other: &Self) -> bool {
        self.dirty_mask == other.dirty_mask
            && self.masks == other.masks
            && self.listener_pre_player == other.listener_pre_player
            && self.listener_post_player == other.listener_post_player
            && self.in_combat_mask == other.in_combat_mask
            && self.in_combat_overflow == other.in_combat_overflow
            && self.cached_tables.map(std::ptr::from_ref)
                == other.cached_tables.map(std::ptr::from_ref)
            && self.listener_relics == other.listener_relics
            && self.listener_potions == other.listener_potions
            && self.listener_orbs == other.listener_orbs
            && self.listener_hand == other.listener_hand
            && self.listener_draw == other.listener_draw
            && self.listener_discard == other.listener_discard
            && self.listener_exhaust == other.listener_exhaust
            && self.listener_play == other.listener_play
            && self.listener_deck == other.listener_deck
            && self.listener_run_models == other.listener_run_models
            && self.listener_dominion == other.listener_dominion
            && self.flat_masks_w0 == other.flat_masks_w0
            && self.flat_masks_w1 == other.flat_masks_w1
            && self.flat_masks_w2 == other.flat_masks_w2
            && self.flat_listeners == other.flat_listeners
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BattleState {
    pub static_ctx: Arc<RootStaticContext>,
    pub current_side: CombatSide,
    pub round_number: i32,
    pub is_in_progress: bool,
    pub is_starting: bool,
    pub won: bool,
    pub is_player_extra_turn: bool,
    pub gold_was_stolen: bool,
    pub end_player_turn_requested: bool,
    pub card_action_boundary_depth: i32,
    pub next_creature_id: u32,
    pub initial_encounter_monster_count: usize,
    pub next_card_handle: u32,
    pub next_creature_handle: u32,
    pub next_power_handle: u32,
    pub next_attack_id: u64,
    pub run_rngs: RunRngSet,
    pub player_rngs: PlayerRngSet,
    pub player: PlayerInst,
    pub cards: Vec<CardInst>,
    pub card_extensions: Vec<CardExtension>,
    pub creatures: Vec<CreatureInst>,
    pub power_extensions: Vec<PowerExtension>,
    pub ally_handles: Vec<CreatureHandle>,
    pub enemy_handles: Vec<CreatureHandle>,
    pub escaped_handles: SmallVec<[CreatureHandle; 4]>,
    pub removed_handles: SmallVec<[CreatureHandle; 4]>,
    pub spawned_enemy_handles: SmallVec<[CreatureHandle; 8]>,
    pub pet_handles: SmallVec<[CreatureHandle; 2]>,
    pub damage_history: Vec<DamageHistoryEntry>,
    pub semantic_history: SemanticHistory,
    pub pending: Option<PendingContinuation>,
    pub vakuu_auto_select_depth: u8,
    pub hook_cache: ActiveHookCache,
    pub deck_dirty: DeckDirtyFlag,
}

#[inline(always)]
fn lookup_card(cards: &[CardInst], handle: CardHandle) -> Option<&CardInst> {
    let idx = handle.0.wrapping_sub(1) as usize;
    if idx < cards.len() && cards[idx].handle == handle {
        return Some(&cards[idx]);
    }
    lookup_card_cold(cards, handle)
}

#[cold]
#[inline(never)]
fn lookup_card_cold(cards: &[CardInst], handle: CardHandle) -> Option<&CardInst> {
    cards.iter().find(|c| c.handle == handle)
}

#[inline(always)]
fn lookup_creature(creatures: &[CreatureInst], handle: CreatureHandle) -> Option<&CreatureInst> {
    let idx = handle.0.wrapping_sub(1) as usize;
    if idx < creatures.len() && creatures[idx].handle == handle {
        return Some(&creatures[idx]);
    }
    lookup_creature_cold(creatures, handle)
}

#[cold]
#[inline(never)]
fn lookup_creature_cold(
    creatures: &[CreatureInst],
    handle: CreatureHandle,
) -> Option<&CreatureInst> {
    creatures.iter().find(|c| c.handle == handle)
}

impl BattleState {
    #[inline(always)]
    pub fn invalidate_hook_cache(&mut self) {
        self.hook_cache.dirty_mask = HOOK_DIRTY_ALL;
    }

    #[inline(always)]
    pub fn invalidate_pile_hooks(&mut self, bits: u16) {
        self.hook_cache.dirty_mask |= bits;
    }

    #[inline(always)]
    pub fn invalidate_creature_hooks(&mut self) {
        self.hook_cache.dirty_mask |= HOOK_DIRTY_CREATURES;
    }

    #[inline(always)]
    pub fn invalidate_relic_hooks(&mut self) {
        self.hook_cache.dirty_mask |= HOOK_DIRTY_RELICS;
    }

    #[inline(always)]
    pub fn invalidate_potion_hooks(&mut self) {
        self.hook_cache.dirty_mask |= HOOK_DIRTY_POTIONS;
    }

    #[inline(always)]
    pub fn invalidate_orb_hooks(&mut self) {
        self.hook_cache.dirty_mask |= HOOK_DIRTY_ORBS;
    }

    #[inline]
    pub fn card_has_any_hook(&self, tables: &HookMaskTableView, handle: CardHandle) -> bool {
        self.card_has_any_hook_in_pile(tables, handle, false)
    }

    #[inline]
    pub fn card_has_any_hook_in_pile(
        &self,
        tables: &HookMaskTableView,
        handle: CardHandle,
        is_deck: bool,
    ) -> bool {
        let Some(card) = self.card(handle) else {
            return true;
        };
        let raw_card = mask_at(tables.cards, card.id as usize);
        let card_mask = if is_deck {
            let dc = tables.categories.deck_cards.0;
            HookMask([
                raw_card.0[0] & dc[0],
                raw_card.0[1] & dc[1],
                raw_card.0[2] & dc[2],
            ])
        } else {
            raw_card
        };
        if !card_mask.is_empty() {
            return true;
        }
        if !is_deck {
            if let Some(af) = card.affliction {
                if !mask_at(tables.afflictions, af.id as usize).is_empty() {
                    return true;
                }
            }
        }
        if let Some(en) = self
            .card_ext(card.ext_index)
            .and_then(|e| e.enchantment.as_ref())
        {
            if !mask_at(tables.enchantments, en.id as usize).is_empty() {
                return true;
            }
        }
        false
    }

    #[inline(always)]
    pub fn fast_card_meta(
        &self,
        content: &dyn ContentDispatcher,
        id: CardId,
    ) -> Result<&'static CardMeta, KernelError> {
        if let Some(t) = self.hook_cache.cached_tables {
            if let Some(&meta) = t.card_metas.get(id as usize) {
                return Ok(meta);
            }
        }
        content.card_meta(id)
    }

    #[inline(always)]
    pub fn fast_power_meta(
        &self,
        content: &dyn ContentDispatcher,
        id: PowerId,
    ) -> Result<&'static PowerMeta, KernelError> {
        if let Some(t) = self.hook_cache.cached_tables {
            if let Some(&meta) = t.power_metas.get(id as usize) {
                return Ok(meta);
            }
        }
        content.power_meta(id)
    }

    #[inline(always)]
    pub fn fast_card_rollout_profile(
        &self,
        content: &dyn ContentDispatcher,
        id: CardId,
    ) -> CardRolloutProfile {
        if let Some(t) = self.hook_cache.cached_tables {
            if let Some(&prof) = t.card_rollout_profiles.get(id as usize) {
                return prof;
            }
        }
        content.card_rollout_profile(id)
    }

    #[inline(always)]
    pub fn fast_card_resolved_target_type(
        &self,
        content: &dyn ContentDispatcher,
        handle: CardHandle,
        id: CardId,
        base_meta: &'static CardMeta,
    ) -> Result<TargetType, KernelError> {
        match id {
            CardId::Shiv | CardId::SovereignBlade | CardId::MadScience => {
                content.card_resolved_target_type(self, handle)
            }
            _ => Ok(base_meta.target_type),
        }
    }

    #[inline(always)]
    pub fn ensure_hook_cache(
        &mut self,
        content: &dyn ContentDispatcher,
    ) -> Result<(), KernelError> {
        #[cfg(not(debug_assertions))]
        if self.hook_cache.dirty_mask == 0 && self.hook_cache.cached_tables.is_some() {
            return Ok(());
        }
        let Some(tables) = self
            .hook_cache
            .cached_tables
            .or_else(|| content.hook_mask_tables())
        else {
            return Ok(());
        };
        self.ensure_hook_cache_with_tables(content, tables)
    }

    #[inline(always)]
    pub fn ensure_hook_cache_with_tables(
        &mut self,
        content: &dyn ContentDispatcher,
        tables: &'static HookMaskTableView,
    ) -> Result<(), KernelError> {
        #[cfg(debug_assertions)]
        if self.hook_cache.dirty_mask == 0 {
            let mut fresh = ActiveHookCache::default();
            let (static_rms, static_dom) =
                Self::collect_static_hook_masks(self, content, HOOK_DIRTY_ALL)?;
            Self::compute_hook_cache_with_tables(
                &self.cards,
                &self.card_extensions,
                &self.creatures,
                &self.player,
                &self.ally_handles,
                &self.enemy_handles,
                static_rms.as_deref(),
                static_dom,
                tables,
                HOOK_DIRTY_ALL,
                &mut fresh,
            );
            debug_assert!(
                self.hook_cache.exact_eq(&fresh),
                "ActiveHookCache out of sync with BattleState!"
            );
            return Ok(());
        }
        let dirty = self.hook_cache.dirty_mask;
        if dirty != 0 {
            self.rebuild_dirty_hook_cache(content, tables, dirty)?;
        }
        Ok(())
    }

    fn collect_static_hook_masks(
        state: &BattleState,
        content: &dyn ContentDispatcher,
        dirty: u16,
    ) -> Result<(Option<SmallVec<[(RunModelId, HookMask); 4]>>, Option<(DominionId, HookMask)>), KernelError> {
        if (dirty & HOOK_DIRTY_STATIC) == 0 {
            return Ok((None, None));
        }
        let mut rms = SmallVec::new();
        for &rm in &state.static_ctx.run_model_ids {
            let m = content.listener_hook_mask(state, HookListener::RunModel(rm))?;
            if !m.is_empty() {
                rms.push((rm, m));
            }
        }
        let dom = if let Some(dom) = state.static_ctx.dominion_id {
            let m = content.listener_hook_mask(state, HookListener::Dominion(dom))?;
            if !m.is_empty() {
                Some((dom, m))
            } else {
                None
            }
        } else {
            None
        };
        Ok((Some(rms), dom))
    }

    #[inline(never)]
    fn rebuild_dirty_hook_cache(
        &mut self,
        content: &dyn ContentDispatcher,
        tables: &'static HookMaskTableView,
        dirty: u16,
    ) -> Result<(), KernelError> {
        let (static_rms, static_dom) = Self::collect_static_hook_masks(self, content, dirty)?;
        Self::compute_hook_cache_with_tables(
            &self.cards,
            &self.card_extensions,
            &self.creatures,
            &self.player,
            &self.ally_handles,
            &self.enemy_handles,
            static_rms.as_deref(),
            static_dom,
            tables,
            dirty,
            &mut self.hook_cache,
        );
        Ok(())
    }

    fn scan_pile_listeners(
        cards: &[CardInst],
        card_extensions: &[CardExtension],
        pile: &[CardHandle],
        is_deck: bool,
        tables: &HookMaskTableView,
        dst: &mut Vec<(HookListener, HookMask)>,
    ) -> HookMask {
        dst.clear();
        let mut acc = HookMask::EMPTY;
        for &ch in pile {
            let Some(card) = lookup_card(cards, ch) else {
                continue;
            };
            if card.removed_from_state {
                continue;
            }
            let raw_card = mask_at(tables.cards, card.id as usize);
            let card_mask = if is_deck {
                let dc = tables.categories.deck_cards.0;
                HookMask([
                    raw_card.0[0] & dc[0],
                    raw_card.0[1] & dc[1],
                    raw_card.0[2] & dc[2],
                ])
            } else {
                raw_card
            };
            let ench_mask =
                if let Some(ext) = card.ext_index.and_then(|i| card_extensions.get(i as usize)) {
                    if let Some(en) = ext.enchantment.as_ref() {
                        mask_at(tables.enchantments, en.id as usize)
                    } else {
                        HookMask::EMPTY
                    }
                } else {
                    HookMask::EMPTY
                };
            let aff_mask = if is_deck {
                HookMask::EMPTY
            } else if let Some(af) = card.affliction {
                mask_at(tables.afflictions, af.id as usize)
            } else {
                HookMask::EMPTY
            };
            if !card_mask.is_empty() {
                acc = acc.union(card_mask);
                dst.push((HookListener::Card(ch), card_mask));
            }
            if !ench_mask.is_empty() {
                acc = acc.union(ench_mask);
                dst.push((HookListener::Enchantment(ch), ench_mask));
            }
            if !aff_mask.is_empty() {
                acc = acc.union(aff_mask);
                dst.push((HookListener::Affliction(ch), aff_mask));
            }
        }
        acc
    }

    #[allow(clippy::too_many_arguments)]
    fn compute_hook_cache_with_tables(
        cards: &[CardInst],
        card_extensions: &[CardExtension],
        creatures: &[CreatureInst],
        player: &PlayerInst,
        ally_handles: &[CreatureHandle],
        enemy_handles: &[CreatureHandle],
        static_rms: Option<&[(RunModelId, HookMask)]>,
        static_dom: Option<(DominionId, HookMask)>,
        tables: &'static HookMaskTableView,
        dirty: u16,
        out: &mut ActiveHookCache,
    ) {
        out.cached_tables = Some(tables);
        if (dirty & HOOK_DIRTY_RELICS) != 0 {
            out.listener_relics.clear();
            let mut m_acc = HookMask::EMPTY;
            for (i, relic) in player.relics.iter().enumerate() {
                if relic.is_melted {
                    continue;
                }
                let m = mask_at(tables.relics, relic.id as usize);
                if !m.is_empty() {
                    m_acc = m_acc.union(m);
                    out.listener_relics.push((i, m));
                }
            }
            out.masks.relics = m_acc;
        }

        if (dirty & HOOK_DIRTY_POTIONS) != 0 {
            out.listener_potions.clear();
            let mut m_acc = HookMask::EMPTY;
            for (i, slot) in player.potion_slots.iter().enumerate() {
                if let Some(pid) = *slot {
                    let m = mask_at(tables.potions, pid as usize);
                    if !m.is_empty() {
                        m_acc = m_acc.union(m);
                        out.listener_potions.push((i, m));
                    }
                }
            }
            out.masks.potions = m_acc;
        }

        if (dirty & HOOK_DIRTY_ORBS) != 0 {
            out.listener_orbs.clear();
            let mut m_acc = HookMask::EMPTY;
            for (i, orb) in player.orbs.iter().enumerate() {
                let m = mask_at(tables.orbs, orb.id as usize);
                if !m.is_empty() {
                    m_acc = m_acc.union(m);
                    out.listener_orbs.push((i, m));
                }
            }
            out.masks.orbs = m_acc;
        }

        if (dirty & HOOK_DIRTY_HAND) != 0 {
            out.masks.cards_hand = Self::scan_pile_listeners(
                cards,
                card_extensions,
                &player.hand,
                false,
                tables,
                &mut out.listener_hand,
            );
        }
        if (dirty & HOOK_DIRTY_DRAW) != 0 {
            out.masks.cards_draw = Self::scan_pile_listeners(
                cards,
                card_extensions,
                &player.draw,
                false,
                tables,
                &mut out.listener_draw,
            );
        }
        if (dirty & HOOK_DIRTY_DISCARD) != 0 {
            out.masks.cards_discard = Self::scan_pile_listeners(
                cards,
                card_extensions,
                &player.discard,
                false,
                tables,
                &mut out.listener_discard,
            );
        }
        if (dirty & HOOK_DIRTY_EXHAUST) != 0 {
            out.masks.cards_exhaust = Self::scan_pile_listeners(
                cards,
                card_extensions,
                &player.exhaust,
                false,
                tables,
                &mut out.listener_exhaust,
            );
        }
        if (dirty & HOOK_DIRTY_PLAY) != 0 {
            out.masks.cards_play = Self::scan_pile_listeners(
                cards,
                card_extensions,
                &player.play,
                false,
                tables,
                &mut out.listener_play,
            );
        }
        if (dirty & HOOK_DIRTY_DECK) != 0 {
            out.masks.cards_deck = Self::scan_pile_listeners(
                cards,
                card_extensions,
                &player.deck,
                true,
                tables,
                &mut out.listener_deck,
            );
        }

        if (dirty & HOOK_DIRTY_CREATURES) != 0 {
            out.listener_pre_player.clear();
            out.listener_post_player.clear();
            let mut powers = HookMask::EMPTY;
            let mut monsters = HookMask::EMPTY;
            let mut pre_player = HookMask::EMPTY;
            let mut post_player = HookMask::EMPTY;
            let mut in_combat_mask = 0u64;
            let mut in_combat_overflow = false;
            let mut seen_active_player = false;
            for &ch in ally_handles {
                if ch.0 >= 1 && ch.0 <= 64 {
                    in_combat_mask |= 1u64 << (ch.0 - 1);
                } else {
                    in_combat_overflow = true;
                }
                let Some(c) = lookup_creature(creatures, ch) else {
                    continue;
                };
                if c.is_detached {
                    continue;
                }
                let is_active_player =
                    c.is_player && player.is_active_for_hooks && !seen_active_player;
                if !(c.is_player && !player.is_active_for_hooks) {
                    for p in &c.powers {
                        let pm = mask_at(tables.powers, p.id as usize);
                        if !pm.is_empty() {
                            powers = powers.union(pm);
                            let l = HookListener::Power {
                                creature: ch,
                                power_handle: p.handle,
                            };
                            if !seen_active_player {
                                pre_player = pre_player.union(pm);
                                out.listener_pre_player.push((l, pm));
                            } else {
                                post_player = post_player.union(pm);
                                out.listener_post_player.push((l, pm));
                            }
                        }
                    }
                }
                if c.is_monster() {
                    if let Some(mid) = c.monster_id {
                        let mm = mask_at(tables.monsters, mid as usize);
                        if !mm.is_empty() {
                            monsters = monsters.union(mm);
                            let l = HookListener::Monster(ch);
                            if !seen_active_player {
                                pre_player = pre_player.union(mm);
                                out.listener_pre_player.push((l, mm));
                            } else {
                                post_player = post_player.union(mm);
                                out.listener_post_player.push((l, mm));
                            }
                        }
                    }
                } else if is_active_player {
                    seen_active_player = true;
                }
            }
            for &ch in enemy_handles {
                if ch.0 >= 1 && ch.0 <= 64 {
                    in_combat_mask |= 1u64 << (ch.0 - 1);
                } else {
                    in_combat_overflow = true;
                }
                let Some(c) = lookup_creature(creatures, ch) else {
                    continue;
                };
                if c.is_detached {
                    continue;
                }
                for p in &c.powers {
                    let pm = mask_at(tables.powers, p.id as usize);
                    if !pm.is_empty() {
                        powers = powers.union(pm);
                        post_player = post_player.union(pm);
                        out.listener_post_player.push((
                            HookListener::Power {
                                creature: ch,
                                power_handle: p.handle,
                            },
                            pm,
                        ));
                    }
                }
                if c.is_monster() {
                    if let Some(mid) = c.monster_id {
                        let mm = mask_at(tables.monsters, mid as usize);
                        if !mm.is_empty() {
                            monsters = monsters.union(mm);
                            post_player = post_player.union(mm);
                            out.listener_post_player
                                .push((HookListener::Monster(ch), mm));
                        }
                    }
                }
            }
            out.masks.powers = powers;
            out.masks.monsters = monsters;
            out.masks.pre_player = pre_player;
            out.masks.post_player = post_player;
            out.in_combat_mask = in_combat_mask;
            out.in_combat_overflow = in_combat_overflow;
        }

        if let Some(rms) = static_rms {
            out.listener_run_models.clear();
            out.listener_dominion = None;
            let mut run_and_dominion = HookMask::EMPTY;
            for &(rm, m) in rms {
                run_and_dominion = run_and_dominion.union(m);
                out.listener_run_models.push((rm, m));
            }
            if let Some((dom, m)) = static_dom {
                run_and_dominion = run_and_dominion.union(m);
                out.listener_dominion = Some((dom, m));
            }
            out.masks.run_and_dominion = run_and_dominion;
        }

        let base = out
            .masks
            .powers
            .union(out.masks.monsters)
            .union(out.masks.run_and_dominion);
        let combat_has_player = player.is_active_for_hooks
            && (ally_handles.iter().any(|&h| {
                h == player.creature_handle
                    && lookup_creature(creatures, h).is_some_and(|c| !c.is_detached && c.is_player)
            }) || enemy_handles.iter().any(|&h| {
                h == player.creature_handle
                    && lookup_creature(creatures, h).is_some_and(|c| !c.is_detached && c.is_player)
            }));
        if combat_has_player {
            let player_inv = out
                .masks
                .relics
                .union(out.masks.potions)
                .union(out.masks.orbs)
                .union(out.masks.cards_hand)
                .union(out.masks.cards_draw)
                .union(out.masks.cards_discard)
                .union(out.masks.cards_exhaust)
                .union(out.masks.cards_play);
            out.masks.player_inv = player_inv;
            out.masks.combat_combined = base.union(player_inv);
        } else {
            out.masks.player_inv = HookMask::EMPTY;
            out.masks.combat_combined = base;
        }
        if player.is_active_for_hooks {
            out.masks.run_combat_combined = out.masks.combat_combined.union(out.masks.cards_deck);
        } else {
            out.masks.run_combat_combined = out.masks.combat_combined;
        }

        out.flat_masks_w0.clear();
        out.flat_masks_w1.clear();
        out.flat_masks_w2.clear();
        out.flat_listeners.clear();
        let mut push_flat = |l: HookListener, m: HookMask| {
            out.flat_masks_w0.push(m.0[0]);
            out.flat_masks_w1.push(m.0[1]);
            out.flat_masks_w2.push(m.0[2]);
            out.flat_listeners.push(l);
        };
        for &(l, m) in &out.listener_pre_player {
            push_flat(l, m);
        }
        if combat_has_player {
            for &(idx, m) in &out.listener_relics {
                push_flat(HookListener::Relic(idx), m);
            }
            for &(idx, m) in &out.listener_potions {
                push_flat(HookListener::Potion(idx), m);
            }
            for &(idx, m) in &out.listener_orbs {
                push_flat(HookListener::Orb(idx), m);
            }
            for &(l, m) in &out.listener_hand {
                push_flat(l, m);
            }
            for &(l, m) in &out.listener_draw {
                push_flat(l, m);
            }
            for &(l, m) in &out.listener_discard {
                push_flat(l, m);
            }
            for &(l, m) in &out.listener_exhaust {
                push_flat(l, m);
            }
            for &(l, m) in &out.listener_play {
                push_flat(l, m);
            }
        }
        for &(l, m) in &out.listener_post_player {
            push_flat(l, m);
        }
        for &(rm, m) in &out.listener_run_models {
            push_flat(HookListener::RunModel(rm), m);
        }
        if let Some((dom, m)) = out.listener_dominion {
            push_flat(HookListener::Dominion(dom), m);
        }

        out.dirty_mask = 0;
    }
    #[inline]
    pub fn is_vakuu_auto_select_active(&self) -> bool {
        self.vakuu_auto_select_depth > 0
    }

    pub fn with_vakuu_auto_select<T, F>(&mut self, f: F) -> Result<T, crate::error::KernelError>
    where
        F: FnOnce(&mut BattleState) -> Result<T, crate::error::KernelError>,
    {
        self.vakuu_auto_select_depth = self.vakuu_auto_select_depth.saturating_add(1);
        let res = f(self);
        self.vakuu_auto_select_depth = self.vakuu_auto_select_depth.saturating_sub(1);
        res
    }

    #[inline]
    pub fn ascension_active(&self, level: AscensionLevel) -> bool {
        is_ascension_active_in_mode(self.static_ctx.game_mode, self.static_ctx.ascension, level)
    }

    #[inline]
    pub fn ascension_value(&self, level: AscensionLevel, asc_val: i32, fallback: i32) -> i32 {
        if self.ascension_active(level) {
            asc_val
        } else {
            fallback
        }
    }

    #[inline(always)]
    pub fn card(&self, handle: CardHandle) -> Option<&CardInst> {
        let idx = handle.0.wrapping_sub(1) as usize;
        if idx < self.cards.len() && self.cards[idx].handle == handle {
            return Some(&self.cards[idx]);
        }
        Self::find_card_cold(&self.cards, handle)
    }

    #[cold]
    #[inline(never)]
    fn find_card_cold(cards: &[CardInst], handle: CardHandle) -> Option<&CardInst> {
        cards.iter().find(|c| c.handle == handle)
    }

    #[inline(always)]
    pub fn card_mut(&mut self, handle: CardHandle) -> Option<&mut CardInst> {
        let idx = handle.0.wrapping_sub(1) as usize;
        if idx < self.cards.len() && self.cards[idx].handle == handle {
            if idx < self.static_ctx.root_deck_prefix_len
                || self.cards[idx].pile == PileType::Deck
            {
                self.deck_dirty.0 = true;
            }
            return Some(&mut self.cards[idx]);
        }
        let pos = Self::find_card_idx_cold(&self.cards, handle)?;
        if pos < self.static_ctx.root_deck_prefix_len || self.cards[pos].pile == PileType::Deck {
            self.deck_dirty.0 = true;
        }
        Some(&mut self.cards[pos])
    }

    #[cold]
    #[inline(never)]
    fn find_card_idx_cold(cards: &[CardInst], handle: CardHandle) -> Option<usize> {
        cards.iter().position(|c| c.handle == handle)
    }

    #[inline(always)]
    pub fn card_ext(&self, ext_idx: Option<u32>) -> Option<&CardExtension> {
        match ext_idx {
            Some(i) => self.card_extensions.get(i as usize),
            None => None,
        }
    }

    pub fn card_ext_mut(&mut self, handle: CardHandle) -> Option<&mut CardExtension> {
        let fast_idx = handle.0.wrapping_sub(1) as usize;
        let idx = if fast_idx < self.cards.len() && self.cards[fast_idx].handle == handle {
            fast_idx
        } else {
            self.cards.iter().position(|c| c.handle == handle)?
        };
        if idx < self.static_ctx.root_deck_prefix_len || self.cards[idx].pile == PileType::Deck {
            self.deck_dirty.0 = true;
        }
        let ext_idx = match self.cards[idx].ext_index {
            Some(i) => i as usize,
            None => {
                let i = self.card_extensions.len();
                self.card_extensions.push(CardExtension::default());
                self.cards[idx].ext_index = Some(i as u32);
                i
            }
        };
        self.card_extensions.get_mut(ext_idx)
    }

    #[inline(always)]
    pub fn creature(&self, handle: CreatureHandle) -> Option<&CreatureInst> {
        let idx = handle.0.wrapping_sub(1) as usize;
        if idx < self.creatures.len() && self.creatures[idx].handle == handle {
            return Some(&self.creatures[idx]);
        }
        Self::find_creature_cold(&self.creatures, handle)
    }

    #[cold]
    #[inline(never)]
    fn find_creature_cold(
        creatures: &[CreatureInst],
        handle: CreatureHandle,
    ) -> Option<&CreatureInst> {
        creatures.iter().find(|c| c.handle == handle)
    }

    #[inline(always)]
    pub fn creature_mut(&mut self, handle: CreatureHandle) -> Option<&mut CreatureInst> {
        let idx = handle.0.wrapping_sub(1) as usize;
        if idx < self.creatures.len() && self.creatures[idx].handle == handle {
            return Some(&mut self.creatures[idx]);
        }
        Self::find_creature_mut_cold(&mut self.creatures, handle)
    }

    #[cold]
    #[inline(never)]
    fn find_creature_mut_cold(
        creatures: &mut [CreatureInst],
        handle: CreatureHandle,
    ) -> Option<&mut CreatureInst> {
        creatures.iter_mut().find(|c| c.handle == handle)
    }

    #[inline]
    pub fn player_creature(&self) -> Option<&CreatureInst> {
        self.creature(self.player.creature_handle)
    }

    #[inline]
    pub fn player_creature_mut(&mut self) -> Option<&mut CreatureInst> {
        let h = self.player.creature_handle;
        self.creature_mut(h)
    }

    #[inline(always)]
    pub fn has_living_player_creature(&self) -> bool {
        if self.ally_handles.first() == Some(&self.player.creature_handle) {
            if let Some(c) = self.creatures.first() {
                if c.handle == self.player.creature_handle && c.is_player && c.current_hp > 0 {
                    return true;
                }
            }
        }
        if let Some(&first) = self.ally_handles.first() {
            if let Some(c) = self.creature(first) {
                if (c.is_player || (!c.is_monster() && !c.is_pet())) && c.is_alive() {
                    return true;
                }
            }
        }
        self.ally_handles.iter().skip(1).any(|&h| {
            self.creature(h).is_some_and(|c| {
                let is_player_creature = c.is_player || (!c.is_monster() && !c.is_pet());
                is_player_creature && c.is_alive()
            })
        })
    }

    #[inline(always)]
    pub fn contains_creature(&self, handle: CreatureHandle) -> bool {
        if (self.hook_cache.dirty_mask & HOOK_DIRTY_CREATURES) == 0
            && self.hook_cache.cached_tables.is_some()
            && !self.hook_cache.in_combat_overflow
            && handle.0 >= 1
            && handle.0 <= 64
        {
            let res = (self.hook_cache.in_combat_mask & (1u64 << (handle.0 - 1))) != 0;
            #[cfg(debug_assertions)]
            debug_assert_eq!(
                res,
                self.ally_handles.contains(&handle) || self.enemy_handles.contains(&handle),
                "in_combat_mask desync for {:?}",
                handle
            );
            return res;
        }
        self.ally_handles.contains(&handle) || self.enemy_handles.contains(&handle)
    }

    #[inline]
    pub fn is_live_combat(&self) -> bool {
        self.is_in_progress
    }

    #[inline]
    pub fn osty_handle(&self) -> Option<CreatureHandle> {
        self.pet_handles.iter().copied().find(|&h| {
            self.creature(h)
                .is_some_and(|c| c.monster_id == Some(MonsterId::Osty))
        })
    }

    #[inline]
    pub fn is_osty_alive(&self) -> bool {
        self.osty_handle()
            .and_then(|h| self.creature(h))
            .is_some_and(|c| c.is_alive())
    }

    pub fn sync_player_hp_to_creature(&mut self) {
        if let Some((hp, max_hp, block, cum)) = self
            .player_creature()
            .map(|c| (c.current_hp, c.max_hp, c.block, c.cumulative_hp_lost))
        {
            self.player.current_hp = hp;
            self.player.max_hp = max_hp;
            self.player.block = block;
            self.player.cumulative_hp_lost = cum;
        }
    }

    pub fn current_turn_key(&self) -> TurnKey {
        TurnKey {
            round_number: self.round_number,
            side: self.current_side,
            player_turn_numbers: InlineOptI32Vec4::one(Some(self.player.turn_number)),
        }
    }

    pub fn record_exhaust(&mut self) {
        let tk = self.current_turn_key();
        self.semantic_history.exhausts.push((tk, 0));
    }

    pub fn record_block_gain(&mut self, props: ValueProp, play_ordinal: Option<i32>) {
        if props.is_powered_block() {
            if let Some(ordinal) = play_ordinal {
                let tk = self.current_turn_key();
                self.semantic_history.card_block_gains.push((tk, 0, ordinal));
            }
        }
    }

    pub fn record_orb_channel(&mut self, orb_id: OrbId) {
        let tk = self.current_turn_key();
        self.semantic_history.orb_channels.push((tk, 0, orb_id));
    }

    pub fn record_energy_spent(&mut self, amount: i32) {
        if amount > 0 {
            let tk = self.current_turn_key();
            self.semantic_history.energy_spent.push((tk, 0, amount));
        }
    }

    pub fn record_card_drawn(&mut self, card_type: CardType, from_hand_draw: bool) {
        let tk = self.current_turn_key();
        self.semantic_history
            .card_drawn
            .push((tk, 0, card_type, from_hand_draw));
    }

    pub fn record_actor_event(&mut self, kind: impl Into<Cow<'static, str>>) {
        let tk = self.current_turn_key();
        self.semantic_history
            .actor_events
            .push((kind.into(), tk, 0));
    }

    /// Folds prior-turn history into summary counters and retains only current-turn transient slices.
    pub fn compact_prior_turn_history(&mut self) {
        let cur_turn = self.player.turn_number;
        let cur_round = self.round_number;
        let cur_side = self.current_side;
        let cur_tk = self.current_turn_key();
        let pc = self.player.creature_handle;

        if !self.damage_history.is_empty() {
            for e in &self.damage_history {
                let keep = e.round_number == cur_round
                    && e.current_side == cur_side
                    && e.player_turn_numbers.as_slice() == [cur_turn];
                if !keep && e.receiver_creature_handle == pc {
                    if e.unblocked_damage > 0 {
                        self.semantic_history.prior_player_unblocked_hits += 1;
                    }
                    if !e.was_fully_blocked {
                        if let Some(&tn) = e.player_turn_numbers.first() {
                            if tn > self.semantic_history.prior_player_damaged_turn {
                                self.semantic_history.prior_player_damaged_turn = tn;
                            }
                        }
                    }
                }
            }
            self.damage_history.retain(|e| {
                e.round_number == cur_round
                    && e.current_side == cur_side
                    && e.player_turn_numbers.as_slice() == [cur_turn]
            });
        }

        if !self.semantic_history.exhausts.is_empty() {
            let mut prior_ex = 0i32;
            for (tk, _) in &self.semantic_history.exhausts {
                if *tk != cur_tk {
                    prior_ex += 1;
                }
            }
            if prior_ex > 0 {
                self.semantic_history.prior_exhaust_count += prior_ex;
                self.semantic_history.exhausts.retain(|(tk, _)| *tk == cur_tk);
            }
        }
        if !self.semantic_history.card_block_gains.is_empty() {
            self.semantic_history
                .card_block_gains
                .retain(|(tk, _, _)| *tk == cur_tk);
        }
        if !self.semantic_history.energy_spent.is_empty() {
            self.semantic_history
                .energy_spent
                .retain(|(tk, _, _)| *tk == cur_tk);
        }
        if !self.semantic_history.card_drawn.is_empty() {
            self.semantic_history
                .card_drawn
                .retain(|(tk, _, _, _)| *tk == cur_tk);
        }
    }

    pub fn pile_vec_mut(&mut self, pile: PileType) -> Option<&mut Vec<CardHandle>> {
        match pile {
            PileType::Hand => Some(&mut self.player.hand),
            PileType::Draw => Some(&mut self.player.draw),
            PileType::Discard => Some(&mut self.player.discard),
            PileType::Exhaust => Some(&mut self.player.exhaust),
            PileType::Play => Some(&mut self.player.play),
            PileType::Deck => {
                self.deck_dirty.0 = true;
                Some(&mut self.player.deck)
            }
            PileType::None => None,
        }
    }

    pub fn remove_card_from_current_pile(&mut self, handle: CardHandle) -> PileType {
        self.remove_card_from_current_pile_with_tables(handle, None)
    }

    pub fn remove_card_from_current_pile_with_tables(
        &mut self,
        handle: CardHandle,
        tables: Option<&HookMaskTableView>,
    ) -> PileType {
        let tables = tables.or(self.hook_cache.cached_tables);
        let old = self.card(handle).map_or(PileType::None, |c| c.pile);
        if old != PileType::None
            && tables
                .is_none_or(|t| self.card_has_any_hook_in_pile(t, handle, old == PileType::Deck))
        {
            self.invalidate_pile_hooks(pile_dirty_bit(old));
        }
        match old {
            PileType::Hand => {
                self.player.hand.retain(|h| *h != handle);
            }
            PileType::Draw => {
                self.player.draw.retain(|h| *h != handle);
            }
            PileType::Discard => {
                self.player.discard.retain(|h| *h != handle);
            }
            PileType::Exhaust => {
                self.player.exhaust.retain(|h| *h != handle);
            }
            PileType::Play => {
                self.player.play.retain(|h| *h != handle);
            }
            PileType::Deck => {
                self.deck_dirty.0 = true;
                self.player.deck.retain(|h| *h != handle);
            }
            PileType::None => {}
        }
        if let Some(card) = self.card_mut(handle) {
            card.pile = PileType::None;
        }
        old
    }

    pub fn add_card_to_pile_at(&mut self, handle: CardHandle, pile: PileType, index: Option<usize>) {
        self.add_card_to_pile_at_with_tables(handle, pile, index, None);
    }

    pub fn add_card_to_pile_at_with_tables(
        &mut self,
        handle: CardHandle,
        pile: PileType,
        index: Option<usize>,
        tables: Option<&HookMaskTableView>,
    ) {
        let tables = tables.or(self.hook_cache.cached_tables);
        if pile != PileType::None
            && tables
                .is_none_or(|t| self.card_has_any_hook_in_pile(t, handle, pile == PileType::Deck))
        {
            self.invalidate_pile_hooks(pile_dirty_bit(pile));
        }
        let insert_into = |vec_len: usize| -> usize {
            match index {
                Some(i) if i <= vec_len => i,
                _ => vec_len,
            }
        };
        match pile {
            PileType::Hand => {
                let idx = insert_into(self.player.hand.len());
                self.player.hand.insert(idx, handle);
            }
            PileType::Draw => {
                let idx = insert_into(self.player.draw.len());
                self.player.draw.insert(idx, handle);
            }
            PileType::Discard => {
                let idx = insert_into(self.player.discard.len());
                self.player.discard.insert(idx, handle);
            }
            PileType::Exhaust => {
                let idx = insert_into(self.player.exhaust.len());
                self.player.exhaust.insert(idx, handle);
            }
            PileType::Play => {
                let idx = insert_into(self.player.play.len());
                self.player.play.insert(idx, handle);
            }
            PileType::Deck => {
                self.deck_dirty.0 = true;
                let idx = insert_into(self.player.deck.len());
                self.player.deck.insert(idx, handle);
            }
            PileType::None => {}
        }
        if let Some(card) = self.card_mut(handle) {
            card.pile = pile;
        }
    }
}

#[cfg(test)]
mod tests {
    use sts2_types::{PowerId, PowerStaticFlags, generated_power_meta};

    #[test]
    fn minion_power_is_unique_secondary_enemy_power() {
        for idx in 0..PowerId::COUNT {
            let id: PowerId = unsafe { core::mem::transmute(idx as u16) };
            let is_sec = generated_power_meta(id)
                .flags
                .contains(PowerStaticFlags::OWNER_IS_SECONDARY_ENEMY);
            assert_eq!(
                is_sec,
                id == PowerId::MinionPower,
                "unexpected OWNER_IS_SECONDARY_ENEMY flag on {id:?}"
            );
        }
    }
}
