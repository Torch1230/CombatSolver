use crate::content_dispatch::{
    CachedHookListener, CardRolloutProfile, ContentDispatcher, HookListener, HookMaskTableView,
};
use crate::continuation::PendingContinuation;
use crate::error::KernelError;
use bitflags::bitflags;
use serde::de::{MapAccess, SeqAccess, Visitor};
use serde::ser::SerializeMap;
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use smallvec::SmallVec;
use std::borrow::Cow;
use std::cell::{Cell, RefCell};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, LazyLock, OnceLock};
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

pub mod hot_keys {
    pub const KUNAI_ATTACKS_THIS_TURN: &str = "Kunai._attacksThisTurn";
    pub const SHURIKEN_ATTACKS_THIS_TURN: &str = "Shuriken._attacksThisTurn";
    pub const ORNAMENTAL_FAN_ATTACKS_THIS_TURN: &str = "OrnamentalFan._attacksThisTurn";
    pub const NUNCHAKU_ATTACKS_PLAYED: &str = "Nunchaku._attacksPlayed";
    pub const LETTER_OPENER_SKILLS_THIS_TURN: &str = "LetterOpener._skillsThisTurn";
    pub const POCKETWATCH_CARDS_PLAYED_THIS_TURN: &str = "Pocketwatch._cardsPlayedThisTurn";
    pub const POCKETWATCH_CARDS_PLAYED_LAST_TURN: &str = "Pocketwatch._cardsPlayedLastTurn";
    pub const PEN_NIB_ATTACKS_PLAYED: &str = "PenNib._attacksPlayed";
    pub const PEN_NIB_DOUBLE_DAMAGE_PLAY_ORDINAL: &str = "PenNib._doubleDamagePlayOrdinal";
    pub const PEN_NIB_DOUBLE_DAMAGE_CARD: &str = "PenNib._doubleDamageCard";
    pub const RAINBOW_RING_TRIGGERED_THIS_TURN: &str = "RainbowRing._triggeredThisTurn";
    pub const WITHERING_PRESENCE_TARGET: &str = "WitheringPresencePower.<Target>k__BackingField";
    pub const WITHERING_PRESENCE_CARDS_PLAYED: &str = "WitheringPresencePower._cardsPlayed";
    pub const REATTACH_IS_REVIVING: &str = "ReattachPower._isReviving";
    pub const ADAPTABLE_IS_REVIVING: &str = "AdaptablePower._isReviving";
    pub const ILLUSION_IS_REVIVING: &str = "IllusionPower._isReviving";
    pub const CHAINS_OF_BINDING_BOUND_PLAYED: &str = "ChainsOfBindingPower._boundCardPlayedThisTurn";

    pub const ALL: &[&str] = &[
        KUNAI_ATTACKS_THIS_TURN,
        SHURIKEN_ATTACKS_THIS_TURN,
        ORNAMENTAL_FAN_ATTACKS_THIS_TURN,
        NUNCHAKU_ATTACKS_PLAYED,
        LETTER_OPENER_SKILLS_THIS_TURN,
        POCKETWATCH_CARDS_PLAYED_THIS_TURN,
        POCKETWATCH_CARDS_PLAYED_LAST_TURN,
        PEN_NIB_ATTACKS_PLAYED,
        PEN_NIB_DOUBLE_DAMAGE_PLAY_ORDINAL,
        PEN_NIB_DOUBLE_DAMAGE_CARD,
        RAINBOW_RING_TRIGGERED_THIS_TURN,
        WITHERING_PRESENCE_TARGET,
        WITHERING_PRESENCE_CARDS_PLAYED,
        REATTACH_IS_REVIVING,
        ADAPTABLE_IS_REVIVING,
        ILLUSION_IS_REVIVING,
        CHAINS_OF_BINDING_BOUND_PLAYED,
    ];
}

struct GlobalSymInner {
    by_id: Vec<&'static str>,
    map: hashbrown::HashMap<&'static str, u16, ahash::RandomState>,
}

fn insert_preinterned_static(inner: &mut GlobalSymInner, keys: &[&'static str]) {
    for &key in keys {
        if !inner.map.contains_key(key) && inner.by_id.len() < SYM_FALLBACK_ID as usize {
            let id = inner.by_id.len() as u16;
            inner.by_id.push(key);
            inner.map.insert(key, id);
        }
    }
}

static GLOBAL_SYMS: LazyLock<parking_lot::RwLock<GlobalSymInner>> = LazyLock::new(|| {
    let mut inner = GlobalSymInner {
        by_id: Vec::with_capacity(512),
        map: hashbrown::HashMap::with_capacity_and_hasher(512, sym_hasher()),
    };
    insert_preinterned_static(&mut inner, hot_keys::ALL);
    parking_lot::RwLock::new(inner)
});

/// Pre-interns `&'static str` field keys so deserialized maps share `.rodata` pointers.
pub fn preintern_static_keys(keys: &[&'static str]) {
    let mut w = GLOBAL_SYMS.write();
    insert_preinterned_static(&mut w, keys);
}

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

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Hash)]
#[repr(transparent)]
pub struct OptCardHandle(pub u32);

impl OptCardHandle {
    pub const NONE: Self = Self(0);

    #[inline(always)]
    pub const fn some(handle: CardHandle) -> Self {
        debug_assert!(handle.0 != 0);
        Self(handle.0)
    }

    #[inline(always)]
    pub const fn from_option(v: Option<CardHandle>) -> Self {
        match v {
            Some(h) => Self::some(h),
            None => Self::NONE,
        }
    }

    #[inline(always)]
    pub const fn get(self) -> Option<CardHandle> {
        if self.0 == 0 {
            None
        } else {
            Some(CardHandle(self.0))
        }
    }

    #[inline(always)]
    pub const fn is_none(self) -> bool {
        self.0 == 0
    }

    #[inline(always)]
    pub const fn is_some(self) -> bool {
        self.0 != 0
    }

    #[inline(always)]
    pub const fn raw(self) -> u32 {
        self.0
    }

    #[inline(always)]
    pub const fn unwrap_or(self, default: CardHandle) -> CardHandle {
        if self.0 == 0 {
            default
        } else {
            CardHandle(self.0)
        }
    }
}

impl From<Option<CardHandle>> for OptCardHandle {
    #[inline(always)]
    fn from(v: Option<CardHandle>) -> Self {
        Self::from_option(v)
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Hash)]
#[repr(transparent)]
pub struct OptI16(pub u16);

impl OptI16 {
    pub const NONE: Self = Self(0);

    #[inline(always)]
    pub const fn some(v: i32) -> Self {
        debug_assert!(v >= -32767 && v <= 32767);
        Self((v as i16 as u16) ^ 0x8000)
    }

    #[inline(always)]
    pub const fn some_clamped(v: i32) -> Self {
        let clamped = if v < -32767 {
            -32767
        } else if v > 32767 {
            32767
        } else {
            v
        };
        Self((clamped as i16 as u16) ^ 0x8000)
    }

    #[inline(always)]
    pub const fn from_option(v: Option<i32>) -> Self {
        match v {
            Some(x) => Self::some_clamped(x),
            None => Self::NONE,
        }
    }

    #[inline(always)]
    pub const fn get(self) -> Option<i32> {
        if self.0 == 0 {
            None
        } else {
            Some(((self.0 ^ 0x8000) as i16) as i32)
        }
    }

    #[inline(always)]
    pub const fn is_none(self) -> bool {
        self.0 == 0
    }

    #[inline(always)]
    pub const fn is_some(self) -> bool {
        self.0 != 0
    }

    #[inline(always)]
    pub const fn raw(self) -> u16 {
        self.0
    }

    #[inline(always)]
    pub const fn unwrap_or(self, default: i32) -> i32 {
        match self.get() {
            Some(v) => v,
            None => default,
        }
    }

    #[inline(always)]
    pub fn map<U>(self, f: impl FnOnce(i32) -> U) -> Option<U> {
        self.get().map(f)
    }
}

impl From<Option<i32>> for OptI16 {
    #[inline(always)]
    fn from(v: Option<i32>) -> Self {
        Self::from_option(v)
    }
}

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

#[inline(always)]
fn cow_as_str<'a>(c: &'a Cow<'static, str>) -> &'a str {
    match c {
        Cow::Borrowed(s) => s,
        Cow::Owned(s) => s.as_str(),
    }
}

#[inline(always)]
fn str_eq_ptr_first(a: &str, b: &str) -> bool {
    let ab = a.as_bytes();
    let bb = b.as_bytes();
    ab.len() == bb.len() && (ab.as_ptr() == bb.as_ptr() || ab == bb)
}

#[inline(always)]
fn cow_str_eq_static(a: &Cow<'static, str>, key: &str) -> bool {
    str_eq_ptr_first(cow_as_str(a), key)
}

#[inline(always)]
fn clone_cow_str_into(dst: &mut Cow<'static, str>, src: &Cow<'static, str>) {
    match (dst, src) {
        (Cow::Borrowed(d), Cow::Borrowed(s)) => *d = *s,
        (Cow::Owned(d), Cow::Owned(s)) => {
            d.clear();
            d.push_str(s);
        }
        (dst, src) => *dst = src.clone(),
    }
}

fn field_borrow_entry<'a>(
    entry: &'a (Cow<'static, str>, FieldValue),
) -> (&'a str, &'a FieldValue) {
    (cow_as_str(&entry.0), &entry.1)
}

fn field_borrow_entry_mut<'a>(
    entry: &'a mut (Cow<'static, str>, FieldValue),
) -> (&'a str, &'a mut FieldValue) {
    (cow_as_str(&entry.0), &mut entry.1)
}

#[inline(always)]
fn clone_field_value_into(dst: &mut FieldValue, src: &FieldValue) {
    match (dst, src) {
        (FieldValue::Int(d), FieldValue::Int(s)) => *d = *s,
        (FieldValue::Bool(d), FieldValue::Bool(s)) => *d = *s,
        (FieldValue::NullableInt(d), FieldValue::NullableInt(s)) => *d = *s,
        (FieldValue::Decimal(d), FieldValue::Decimal(s)) => *d = *s,
        (FieldValue::CardHandle(d), FieldValue::CardHandle(s)) => *d = *s,
        (FieldValue::CreatureHandle(d), FieldValue::CreatureHandle(s)) => *d = *s,
        (d, s) => {
            if d != s {
                d.clone_from(s);
            }
        }
    }
}

#[inline(always)]
fn inc_int_field_value(v: &mut FieldValue) -> i32 {
    match v {
        FieldValue::Int(i) => {
            *i += 1;
            *i
        }
        FieldValue::NullableInt(Some(i)) => {
            let next = *i + 1;
            *v = FieldValue::Int(next);
            next
        }
        _ => {
            *v = FieldValue::Int(1);
            1
        }
    }
}

#[inline(always)]
fn set_int_field_value(v: &mut FieldValue, val: i32) {
    if let FieldValue::Int(i) = v {
        *i = val;
    } else {
        *v = FieldValue::Int(val);
    }
}

/// Entry handle for `FieldMap::entry`.
pub struct FieldMapEntry<'a> {
    entries: &'a mut Vec<(Cow<'static, str>, FieldValue)>,
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

/// Sorted vec-backed field map avoiding per-element inline array waste on empty maps.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct FieldMap {
    entries: Vec<(Cow<'static, str>, FieldValue)>,
}

impl FieldMap {
    #[inline]
    pub fn new() -> Self {
        Self {
            entries: Vec::new(),
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

    #[inline(always)]
    pub fn get_bool_static(&self, key: &'static str) -> Option<bool> {
        let len = self.entries.len();
        if len == 0 {
            return None;
        }
        let kb = key.as_bytes();
        if len == 1 {
            let (k, v) = &self.entries[0];
            let eb = cow_as_str(k).as_bytes();
            if eb.len() == kb.len() && (std::ptr::eq(eb.as_ptr(), kb.as_ptr()) || eb == kb) {
                return match v {
                    FieldValue::Bool(b) => Some(*b),
                    _ => None,
                };
            }
            return None;
        }
        if len <= 4 {
            for (k, v) in &self.entries {
                let eb = cow_as_str(k).as_bytes();
                if eb.len() == kb.len() && (std::ptr::eq(eb.as_ptr(), kb.as_ptr()) || eb == kb) {
                    return match v {
                        FieldValue::Bool(b) => Some(*b),
                        _ => None,
                    };
                }
            }
            return None;
        }
        self.entries
            .binary_search_by(|(k, _)| cow_as_str(k).cmp(key))
            .ok()
            .and_then(|idx| match self.entries[idx].1 {
                FieldValue::Bool(b) => Some(b),
                _ => None,
            })
    }

    #[inline(always)]
    pub fn entries(&self) -> &[(Cow<'static, str>, FieldValue)] {
        &self.entries
    }

    #[inline(always)]
    pub fn entries_mut(&mut self) -> &mut [(Cow<'static, str>, FieldValue)] {
        &mut self.entries
    }

    #[inline]
    pub fn get(&self, key: &str) -> Option<&FieldValue> {
        let len = self.entries.len();
        if len == 0 {
            return None;
        }
        if len == 1 {
            let (k, v) = &self.entries[0];
            if cow_str_eq_static(k, key) {
                return Some(v);
            }
            return None;
        }
        if len <= 4 {
            for (k, v) in &self.entries {
                if cow_str_eq_static(k, key) {
                    return Some(v);
                }
            }
            return None;
        }
        self.entries
            .binary_search_by(|(k, _)| cow_as_str(k).cmp(key))
            .ok()
            .map(|idx| &self.entries[idx].1)
    }

    #[inline]
    pub fn get_mut(&mut self, key: &str) -> Option<&mut FieldValue> {
        let len = self.entries.len();
        if len == 0 {
            return None;
        }
        if len == 1 {
            let (k, v) = &mut self.entries[0];
            if cow_str_eq_static(k, key) {
                return Some(v);
            }
            return None;
        }
        if len <= 4 {
            for (k, v) in &mut self.entries {
                if cow_str_eq_static(k, key) {
                    return Some(v);
                }
            }
            return None;
        }
        self.entries
            .binary_search_by(|(k, _)| cow_as_str(k).cmp(key))
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
            .binary_search_by(|(k, _)| cow_as_str(k).cmp(cow_as_str(&key)));
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
        let len = self.entries.len();
        if len == 0 {
            self.entries.push((key, value));
            return None;
        }
        let kb = cow_as_str(&key).as_bytes();
        if len == 1 {
            let eb = cow_as_str(&self.entries[0].0).as_bytes();
            if eb.len() == kb.len() && (eb.as_ptr() == kb.as_ptr() || eb == kb) {
                return Some(std::mem::replace(&mut self.entries[0].1, value));
            }
            if eb < kb {
                self.entries.push((key, value));
            } else {
                self.entries.insert(0, (key, value));
            }
            return None;
        }
        if len <= 4 {
            for i in 0..len {
                let eb = cow_as_str(&self.entries[i].0).as_bytes();
                if eb.len() == kb.len() && (eb.as_ptr() == kb.as_ptr() || eb == kb) {
                    return Some(std::mem::replace(&mut self.entries[i].1, value));
                }
            }
        }
        match self
            .entries
            .binary_search_by(|(k, _)| cow_as_str(k).cmp(cow_as_str(&key)))
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
        match self.entries.binary_search_by(|(k, _)| cow_as_str(k).cmp(key)) {
            Ok(idx) => self.entries[idx].1 = value,
            Err(idx) => self.entries.insert(idx, (Cow::Owned(key.to_owned()), value)),
        }
    }

    #[inline]
    pub fn inc_int(&mut self, key: &'static str) -> i32 {
        let len = self.entries.len();
        if len == 0 {
            self.entries.push((Cow::Borrowed(key), FieldValue::Int(1)));
            return 1;
        }
        let kb = key.as_bytes();
        if len == 1 {
            let (k, v) = &mut self.entries[0];
            if let Cow::Borrowed(s) = *k {
                if std::ptr::eq(s.as_ptr(), kb.as_ptr()) && s.len() == kb.len() {
                    return inc_int_field_value(v);
                }
            }
            let eb = cow_as_str(k).as_bytes();
            if eb == kb {
                *k = Cow::Borrowed(key);
                return inc_int_field_value(v);
            }
            if eb < kb {
                self.entries.push((Cow::Borrowed(key), FieldValue::Int(1)));
            } else {
                self.entries.insert(0, (Cow::Borrowed(key), FieldValue::Int(1)));
            }
            return 1;
        }
        if len <= 4 {
            for (k, v) in &mut self.entries {
                if let Cow::Borrowed(s) = *k {
                    if std::ptr::eq(s.as_ptr(), kb.as_ptr()) && s.len() == kb.len() {
                        return inc_int_field_value(v);
                    }
                }
                if cow_as_str(k).as_bytes() == kb {
                    *k = Cow::Borrowed(key);
                    return inc_int_field_value(v);
                }
            }
        }
        match self.entries.binary_search_by(|(k, _)| cow_as_str(k).cmp(key)) {
            Ok(idx) => {
                let (k, v) = &mut self.entries[idx];
                if !matches!(*k, Cow::Borrowed(s) if std::ptr::eq(s.as_ptr(), kb.as_ptr()) && s.len() == kb.len())
                {
                    *k = Cow::Borrowed(key);
                }
                inc_int_field_value(v)
            }
            Err(idx) => {
                self.entries.insert(idx, (Cow::Borrowed(key), FieldValue::Int(1)));
                1
            }
        }
    }

    #[inline]
    pub fn set_int_inplace(&mut self, key: &'static str, new_val: i32) {
        let len = self.entries.len();
        if len == 0 {
            self.entries
                .push((Cow::Borrowed(key), FieldValue::Int(new_val)));
            return;
        }
        let kb = key.as_bytes();
        if len == 1 {
            let (k, v) = &mut self.entries[0];
            if let Cow::Borrowed(s) = *k {
                if std::ptr::eq(s.as_ptr(), kb.as_ptr()) && s.len() == kb.len() {
                    set_int_field_value(v, new_val);
                    return;
                }
            }
            let eb = cow_as_str(k).as_bytes();
            if eb == kb {
                *k = Cow::Borrowed(key);
                set_int_field_value(v, new_val);
                return;
            }
            if eb < kb {
                self.entries
                    .push((Cow::Borrowed(key), FieldValue::Int(new_val)));
            } else {
                self.entries
                    .insert(0, (Cow::Borrowed(key), FieldValue::Int(new_val)));
            }
            return;
        }
        if len <= 4 {
            for (k, v) in &mut self.entries {
                if let Cow::Borrowed(s) = *k {
                    if std::ptr::eq(s.as_ptr(), kb.as_ptr()) && s.len() == kb.len() {
                        set_int_field_value(v, new_val);
                        return;
                    }
                }
                if cow_as_str(k).as_bytes() == kb {
                    *k = Cow::Borrowed(key);
                    set_int_field_value(v, new_val);
                    return;
                }
            }
        }
        match self.entries.binary_search_by(|(k, _)| cow_as_str(k).cmp(key)) {
            Ok(idx) => {
                let (k, v) = &mut self.entries[idx];
                if !matches!(*k, Cow::Borrowed(s) if std::ptr::eq(s.as_ptr(), kb.as_ptr()) && s.len() == kb.len())
                {
                    *k = Cow::Borrowed(key);
                }
                set_int_field_value(v, new_val);
            }
            Err(idx) => {
                self.entries
                    .insert(idx, (Cow::Borrowed(key), FieldValue::Int(new_val)));
            }
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

    #[inline(always)]
    pub fn clone_into_map(&self, dst: &mut FieldMap) {
        if self.entries.is_empty() {
            if !dst.entries.is_empty() {
                dst.entries.clear();
            }
            return;
        }
        self.clone_into_map_nonempty(dst);
    }

    #[inline]
    fn clone_into_map_nonempty(&self, dst: &mut FieldMap) {
        let slen = self.entries.len();
        if slen == 1 {
            let (sk, sv) = &self.entries[0];
            if dst.entries.is_empty() {
                dst.entries.push((sk.clone(), sv.clone()));
            } else {
                if dst.entries.len() > 1 {
                    dst.entries.truncate(1);
                }
                let (dk, dv) = &mut dst.entries[0];
                clone_cow_str_into(dk, sk);
                clone_field_value_into(dv, sv);
            }
            return;
        }
        let dlen = dst.entries.len();
        let keep = dlen.min(slen);
        for ((dk, dv), (sk, sv)) in dst.entries[..keep]
            .iter_mut()
            .zip(self.entries[..keep].iter())
        {
            clone_cow_str_into(dk, sk);
            clone_field_value_into(dv, sv);
        }
        if slen > dlen {
            dst.entries.reserve(slen - dlen);
            for (k, v) in &self.entries[dlen..] {
                dst.entries.push((k.clone(), v.clone()));
            }
        } else if dlen > slen {
            dst.entries.truncate(slen);
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
    type IntoIter = std::vec::IntoIter<(Cow<'static, str>, FieldValue)>;

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
        const POTION_OFFER           = 1 << 8;
        const DISCOVERY_OFFER        = 1 << 9;
        const SPLASH_OFFER           = 1 << 10;
    }
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct CardExtension {
    pub local_energy_modifiers: Vec<LocalEnergyModifier>,
    pub enchantment: Option<EnchantmentInst>,
    pub private_fields: FieldMap,
}

impl CardExtension {
    /// Pushes a local energy modifier, coalescing consecutive non-reduce-only additive deltas of the same kind.
    #[inline]
    pub fn push_local_energy_modifier(&mut self, modifier: LocalEnergyModifier) {
        if !modifier.is_reduce_only && !modifier.kind.is_absolute() {
            if let Some(last) = self.local_energy_modifiers.last_mut() {
                if !last.is_reduce_only && last.kind == modifier.kind {
                    last.amount += modifier.amount;
                    if last.amount == 0 {
                        self.local_energy_modifiers.pop();
                    }
                    return;
                }
            }
        }
        self.local_energy_modifiers.push(modifier);
    }

    pub fn strip_non_cloned_private_fields(&mut self) {
        self.private_fields.remove("Bolas._playedOnTurnNumber");
        self.private_fields
            .remove("ThrummingHatchet._playedOnTurnNumber");
        self.private_fields.remove("Bounce._lastFinishedPlayerTurn");
        self.private_fields.remove("Fetch._finishedRound");
        self.private_fields.remove("Fetch._finishedSide");
        self.private_fields.remove("Fetch._finishedTurnNumbers");
        self.private_fields.remove("Regret._cardsInHand");
        self.private_fields
            .remove("Abundance._generatedCandidates");
    }
}

/// Compact 64-byte single-cache-line hot struct for a card instance; infrequent/variable fields spill into `card_extensions`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(C)]
pub struct CardInst {
    pub handle: CardHandle,
    pub upgrade_level: i32,
    pub ext_index: Option<u32>,
    pub affliction: Option<AfflictionInst>,
    pub deck_card_handle: OptCardHandle,
    pub clone_of_handle: OptCardHandle,
    pub id: CardId,
    pub local_keywords: CardKeywords,
    pub canonical_base_energy_cost: i16,
    pub base_energy_cost: i16,
    pub floor_added_to_deck: OptI16,
    pub pile: PileType,
    pub removed_from_state: bool,
    pub keywords_added_from_outside: CardKeywords,
    pub keywords_removed_from_outside: CardKeywords,
    pub temp_flags: CardTempFlags,
    pub captured_energy_x_value: i16,
    pub base_replay_count: i16,
    pub temp_star_cost_override_this_turn: OptI16,
    pub temp_cost_override_this_turn: OptI16,
    pub temp_cost_override_this_turn_or_until_played: OptI16,
    pub temp_cost_override_this_combat: OptI16,
    pub transform_into_after_play: Option<CardId>,
}

const _: () = {
    assert!(std::mem::size_of::<CardInst>() == 64);
    assert!(OptI16::NONE.raw() == 0);
    assert!(std::mem::offset_of!(CardInst, keywords_added_from_outside) == 44);
    assert!(std::mem::offset_of!(CardInst, keywords_removed_from_outside) == 46);
    assert!(std::mem::offset_of!(CardInst, temp_flags) == 48);
    assert!(std::mem::offset_of!(CardInst, captured_energy_x_value) == 50);
    assert!(std::mem::offset_of!(CardInst, base_replay_count) == 52);
    assert!(std::mem::offset_of!(CardInst, temp_star_cost_override_this_turn) == 54);
    assert!(std::mem::offset_of!(CardInst, temp_cost_override_this_turn) == 56);
    assert!(std::mem::offset_of!(CardInst, temp_cost_override_this_turn_or_until_played) == 58);
    assert!(std::mem::offset_of!(CardInst, temp_cost_override_this_combat) == 60);
};

impl CardInst {
    #[inline(always)]
    pub const fn clean_rare_bits(&self) -> u16 {
        self.keywords_added_from_outside.bits()
            | self.keywords_removed_from_outside.bits()
            | self.temp_flags.bits()
            | (self.captured_energy_x_value as u16)
            | (self.base_replay_count as u16)
            | self.temp_star_cost_override_this_turn.raw()
            | self.temp_cost_override_this_turn.raw()
            | self.temp_cost_override_this_turn_or_until_played.raw()
            | self.temp_cost_override_this_combat.raw()
    }

    #[inline(always)]
    pub fn is_rare_clean(&self) -> bool {
        // SAFETY: Offsets 44..60 cover two unaligned 8-byte spans inside the 64-byte `#[repr(C)] CardInst`.
        let clean = unsafe {
            let p = (self as *const Self).cast::<u8>();
            let w0 = std::ptr::read_unaligned(p.add(44).cast::<u64>());
            let w1 = std::ptr::read_unaligned(p.add(52).cast::<u64>());
            (w0 | w1 | (self.temp_cost_override_this_combat.raw() as u64)) == 0
        };
        #[cfg(debug_assertions)]
        debug_assert_eq!(clean, self.clean_rare_bits() == 0);
        clean
    }

    #[inline(always)]
    pub fn has_local_keyword(&self, kw: CardKeywords) -> bool {
        #[cfg(debug_assertions)]
        debug_assert!(kw.bits().is_power_of_two());
        let added = self.local_keywords.bits() | self.keywords_added_from_outside.bits();
        ((added & !self.keywords_removed_from_outside.bits()) & kw.bits()) != 0
    }

    #[inline(always)]
    pub fn effective_keywords_base(&self) -> CardKeywords {
        (self.local_keywords | self.keywords_added_from_outside)
            & !self.keywords_removed_from_outside
    }
}

pub const ROOT_CARD_FAST_HASH_SLOW: u64 = 1u64 << 14;

#[inline(always)]
pub fn compute_card_fast_hash_word(card: &CardInst) -> u64 {
    if card.ext_index.is_none()
        && card.affliction.is_none()
        && card.transform_into_after_play.is_none()
        && card.is_rare_clean()
        && ((card.upgrade_level.wrapping_add(32768) as u32) & !0xFFFFu32) == 0
    {
        debug_assert!((card.id as usize) < 1024);
        (card.id as u64)
            | ((card.local_keywords.bits() as u64) << 16)
            | (((card.base_energy_cost as u16) as u64) << 32)
            | (((card.upgrade_level as i16 as u16) as u64) << 48)
    } else {
        ROOT_CARD_FAST_HASH_SLOW
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

/// Compact player state keeping standard combat fields and heap vectors lean.
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
    pub orbs: Vec<OrbInst>,
    pub arrows: Vec<ArrowSlotInst>,
    pub relics: Vec<RelicInst>,
    pub card_or_potion_effect_depth: i32,
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct PowerExtension {
    pub private_fields: FieldMap,
    pub attack_command_id: Option<u64>,
    pub amount_when_attack_started: i32,
    pub impaled_arrows: SmallVec<[usize; 2]>,
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
    pub state_log_ids: Vec<Cow<'static, str>>,
    pub transient_move_follow_up_state_id: Option<Cow<'static, str>>,
    pub setup_fields: FieldMap,
    pub private_fields: FieldMap,
}

/// Compact cache-line-0 `CreatureInst` placing hot combat scalars up front and spilling `MonsterInst`.
#[derive(Clone, Debug, Eq, PartialEq)]
#[repr(C)]
pub struct CreatureInst {
    pub handle: CreatureHandle,
    pub current_hp: i32,
    pub max_hp: i32,
    pub block: i32,
    pub cumulative_hp_lost: i32,
    pub side: CombatSide,
    pub is_player: bool,
    pub is_detached: bool,
    pub has_minion_power: bool,
    pub powers: Vec<PowerInst>,
    pub monster: Option<Box<MonsterInst>>,
    pub monster_id: Option<MonsterId>,
    pub combat_id: Option<u32>,
    pub pet_owner_player_index: Option<usize>,
    pub slot_name: Option<Cow<'static, str>>,
}

const _: () = assert!(std::mem::size_of::<CreatureInst>() <= 120);

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

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
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

#[derive(Clone, Copy)]
pub struct InlineOptI32Vec4Iter {
    data: [i32; 4],
    len: u8,
    some_mask: u8,
    idx: u8,
}

impl Iterator for InlineOptI32Vec4Iter {
    type Item = Option<i32>;

    #[inline(always)]
    fn next(&mut self) -> Option<Self::Item> {
        if self.idx >= self.len || self.idx >= 4 {
            return None;
        }
        let i = self.idx as usize;
        let present = (self.some_mask & (1u8 << i)) != 0;
        let v = self.data[i];
        self.idx += 1;
        Some(if present { Some(v) } else { None })
    }

    #[inline(always)]
    fn size_hint(&self) -> (usize, Option<usize>) {
        let rem = (self.len.min(4) as usize).saturating_sub(self.idx as usize);
        (rem, Some(rem))
    }
}

impl ExactSizeIterator for InlineOptI32Vec4Iter {}

#[derive(Clone, Copy, Debug, Default)]
#[repr(C)]
pub struct InlineOptI32Vec4 {
    data: [i32; 4],
    len: u8,
    some_mask: u8,
}

const _: () = {
    assert!(std::mem::size_of::<InlineOptI32Vec4>() == 20);
};

impl InlineOptI32Vec4 {
    #[inline(always)]
    pub const fn new() -> Self {
        Self {
            data: [0; 4],
            len: 0,
            some_mask: 0,
        }
    }

    #[inline(always)]
    pub const fn one(v: Option<i32>) -> Self {
        match v {
            Some(x) => Self {
                data: [x, 0, 0, 0],
                len: 1,
                some_mask: 1,
            },
            None => Self {
                data: [0; 4],
                len: 1,
                some_mask: 0,
            },
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

    #[inline(always)]
    pub fn push(&mut self, v: Option<i32>) {
        let idx = self.len as usize;
        if idx < 4 {
            match v {
                Some(x) => {
                    self.data[idx] = x;
                    self.some_mask |= 1u8 << idx;
                }
                None => {
                    self.data[idx] = 0;
                    self.some_mask &= !(1u8 << idx);
                }
            }
            self.len += 1;
        }
    }

    #[inline(always)]
    pub fn clear(&mut self) {
        self.data = [0; 4];
        self.len = 0;
        self.some_mask = 0;
    }

    #[inline(always)]
    pub const fn len(&self) -> usize {
        (self.len as usize) & 7
    }

    #[inline(always)]
    pub const fn is_empty(&self) -> bool {
        self.len == 0
    }

    #[inline(always)]
    pub const fn get(&self, idx: usize) -> Option<Option<i32>> {
        if idx < (self.len as usize) && idx < 4 {
            if (self.some_mask & (1u8 << idx)) != 0 {
                Some(Some(self.data[idx]))
            } else {
                Some(None)
            }
        } else {
            None
        }
    }

    #[inline(always)]
    pub const fn first(&self) -> Option<Option<i32>> {
        self.get(0)
    }

    #[inline(always)]
    pub const fn is_single_some(&self, v: i32) -> bool {
        self.len == 1 && self.some_mask == 1 && self.data[0] == v
    }

    #[inline(always)]
    pub const fn iter(&self) -> InlineOptI32Vec4Iter {
        InlineOptI32Vec4Iter {
            data: self.data,
            len: self.len,
            some_mask: self.some_mask,
            idx: 0,
        }
    }
}

impl PartialEq for InlineOptI32Vec4 {
    #[inline(always)]
    fn eq(&self, other: &Self) -> bool {
        self.len == other.len
            && self.some_mask == other.some_mask
            && self.data[0] == other.data[0]
            && (self.len <= 1
                || (self.data[1] == other.data[1]
                    && self.data[2] == other.data[2]
                    && self.data[3] == other.data[3]))
    }
}

impl Eq for InlineOptI32Vec4 {}

impl std::hash::Hash for InlineOptI32Vec4 {
    #[inline]
    fn hash<H: std::hash::Hasher>(&self, state: &mut H) {
        self.len.hash(state);
        for v in self.iter() {
            v.hash(state);
        }
    }
}

impl<'a> IntoIterator for &'a InlineOptI32Vec4 {
    type Item = Option<i32>;
    type IntoIter = InlineOptI32Vec4Iter;

    #[inline(always)]
    fn into_iter(self) -> Self::IntoIter {
        self.iter()
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
        serializer.collect_seq(self.iter())
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

#[derive(Clone, Copy, Debug, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TurnKey {
    pub round_number: i32,
    pub side: CombatSide,
    pub player_turn_numbers: InlineOptI32Vec4,
}

impl PartialEq for TurnKey {
    #[inline(always)]
    fn eq(&self, other: &Self) -> bool {
        if self.round_number != other.round_number || self.side != other.side {
            return false;
        }
        let ptn_a = &self.player_turn_numbers;
        let ptn_b = &other.player_turn_numbers;
        let diff = ((ptn_a.len as i32) ^ (ptn_b.len as i32))
            | ((ptn_a.some_mask as i32) ^ (ptn_b.some_mask as i32))
            | (ptn_a.data[0] ^ ptn_b.data[0]);
        if diff != 0 {
            return false;
        }
        ptn_a.len <= 1
            || (ptn_a.data[1] == ptn_b.data[1]
                && ptn_a.data[2] == ptn_b.data[2]
                && ptn_a.data[3] == ptn_b.data[3])
    }
}

const _: () = {
    assert!(std::mem::size_of::<TurnKey>() <= 28);
};

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

    #[inline(always)]
    pub const fn is_zero(&self) -> bool {
        (self.hand_of_greed
            | self.the_hunt
            | self.feed
            | self.royalties
            | self.alchemize
            | self.genetic_algorithm
            | self.the_scythe
            | self.goopy
            | self.forbidden_grimoire
            | self.mad_science)
            == 0
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

#[derive(Clone, Copy, Debug, Eq)]
pub struct SemanticHistoryTurnCache {
    pub valid: bool,
    pub valid_for: TurnKey,
    pub ex_len: u32,
    pub ex_cur: u32,
    pub cbg_len: u32,
    pub cbg_cur: u32,
    pub oc_len: u32,
    pub oc_cur: u32,
    pub es_len: u32,
    pub es_cur_sum: i32,
    pub cd_len: u32,
    pub cd_cur: u32,
    pub ae_len: u32,
    pub ae_start: u32,
}

impl Default for SemanticHistoryTurnCache {
    #[inline(always)]
    fn default() -> Self {
        Self {
            valid: false,
            valid_for: TurnKey {
                round_number: 0,
                side: CombatSide::Player,
                player_turn_numbers: InlineOptI32Vec4::new(),
            },
            ex_len: 0,
            ex_cur: 0,
            cbg_len: 0,
            cbg_cur: 0,
            oc_len: 0,
            oc_cur: 0,
            es_len: 0,
            es_cur_sum: 0,
            cd_len: 0,
            cd_cur: 0,
            ae_len: 0,
            ae_start: 0,
        }
    }
}

impl PartialEq for SemanticHistoryTurnCache {
    #[inline(always)]
    fn eq(&self, _other: &Self) -> bool {
        true
    }
}

impl SemanticHistoryTurnCache {
    #[inline(always)]
    pub fn lengths_match(
        &self,
        ex_len: usize,
        cbg_len: usize,
        oc_len: usize,
        es_len: usize,
        cd_len: usize,
        ae_len: usize,
    ) -> bool {
        self.ex_len as usize == ex_len
            && self.cbg_len as usize == cbg_len
            && self.oc_len as usize == oc_len
            && self.es_len as usize == es_len
            && self.cd_len as usize == cd_len
            && self.ae_len as usize == ae_len
    }

    #[inline(always)]
    pub fn matches(&self, cur_tk: &TurnKey, sh: &SemanticHistory) -> bool {
        self.valid
            && self.valid_for == *cur_tk
            && self.lengths_match(
                sh.exhausts.len(),
                sh.card_block_gains.len(),
                sh.orb_channels.len(),
                sh.energy_spent.len(),
                sh.card_drawn.len(),
                sh.actor_events.len(),
            )
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
    pub turn_cache: SemanticHistoryTurnCache,
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
            turn_cache: SemanticHistoryTurnCache::default(),
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

    #[inline(always)]
    fn sync_turn_cache_for_push(&mut self, tk: TurnKey) -> bool {
        let ex_len = self.exhausts.len();
        let cbg_len = self.card_block_gains.len();
        let oc_len = self.orb_channels.len();
        let es_len = self.energy_spent.len();
        let cd_len = self.card_drawn.len();
        let ae_len = self.actor_events.len();
        let c = &mut self.turn_cache;
        if c.valid {
            if !c.lengths_match(ex_len, cbg_len, oc_len, es_len, cd_len, ae_len) {
                c.valid = false;
                return false;
            }
            if c.valid_for != tk {
                c.valid_for = tk;
                c.ex_cur = 0;
                c.cbg_cur = 0;
                c.oc_cur = 0;
                c.es_cur_sum = 0;
                c.cd_cur = 0;
                c.ae_start = ae_len as u32;
            }
            true
        } else if (ex_len | cbg_len | oc_len | es_len | cd_len | ae_len) == 0 {
            c.valid = true;
            c.valid_for = tk;
            c.ex_len = 0;
            c.ex_cur = 0;
            c.cbg_len = 0;
            c.cbg_cur = 0;
            c.oc_len = 0;
            c.oc_cur = 0;
            c.es_len = 0;
            c.es_cur_sum = 0;
            c.cd_len = 0;
            c.cd_cur = 0;
            c.ae_len = 0;
            c.ae_start = 0;
            true
        } else {
            false
        }
    }

    pub fn refresh_turn_cache(&mut self, cur_tk: &TurnKey) {
        let ex_cur = if self.exhausts.is_empty() || self.exhausts[self.exhausts.len() - 1].0 != *cur_tk {
            0
        } else if self.exhausts[0].0 == *cur_tk {
            self.exhausts.len()
        } else {
            let mut n = self.exhausts.len();
            while n > 0 && self.exhausts[n - 1].0 == *cur_tk {
                n -= 1;
            }
            self.exhausts.len() - n
        };
        let cbg_cur = if self.card_block_gains.is_empty()
            || self.card_block_gains[self.card_block_gains.len() - 1].0 != *cur_tk
        {
            0
        } else if self.card_block_gains[0].0 == *cur_tk {
            self.card_block_gains.len()
        } else {
            let mut n = self.card_block_gains.len();
            while n > 0 && self.card_block_gains[n - 1].0 == *cur_tk {
                n -= 1;
            }
            self.card_block_gains.len() - n
        };
        let oc_cur = if self.orb_channels.is_empty()
            || self.orb_channels[self.orb_channels.len() - 1].0 != *cur_tk
        {
            0
        } else if self.orb_channels[0].0 == *cur_tk {
            self.orb_channels.len()
        } else {
            let mut n = self.orb_channels.len();
            while n > 0 && self.orb_channels[n - 1].0 == *cur_tk {
                n -= 1;
            }
            self.orb_channels.len() - n
        };
        let es_cur_sum = if self.energy_spent.is_empty()
            || self.energy_spent[self.energy_spent.len() - 1].0 != *cur_tk
        {
            0
        } else if self.energy_spent[0].0 == *cur_tk {
            self.energy_spent.iter().map(|e| e.2).sum()
        } else {
            let mut sum = 0i32;
            let mut n = self.energy_spent.len();
            while n > 0 && self.energy_spent[n - 1].0 == *cur_tk {
                n -= 1;
                sum += self.energy_spent[n].2;
            }
            sum
        };
        let cd_cur = if self.card_drawn.is_empty()
            || self.card_drawn[self.card_drawn.len() - 1].0 != *cur_tk
        {
            0
        } else if self.card_drawn[0].0 == *cur_tk {
            self.card_drawn.len()
        } else {
            let mut n = self.card_drawn.len();
            while n > 0 && self.card_drawn[n - 1].0 == *cur_tk {
                n -= 1;
            }
            self.card_drawn.len() - n
        };
        let ae_start = if self.actor_events.is_empty()
            || self.actor_events[self.actor_events.len() - 1].1 != *cur_tk
        {
            self.actor_events.len()
        } else if self.actor_events[0].1 == *cur_tk {
            0
        } else {
            let mut n = self.actor_events.len();
            while n > 0 && self.actor_events[n - 1].1 == *cur_tk {
                n -= 1;
            }
            n
        };
        self.turn_cache = SemanticHistoryTurnCache {
            valid: true,
            valid_for: *cur_tk,
            ex_len: self.exhausts.len() as u32,
            ex_cur: ex_cur as u32,
            cbg_len: self.card_block_gains.len() as u32,
            cbg_cur: cbg_cur as u32,
            oc_len: self.orb_channels.len() as u32,
            oc_cur: oc_cur as u32,
            es_len: self.energy_spent.len() as u32,
            es_cur_sum,
            cd_len: self.card_drawn.len() as u32,
            cd_cur: cd_cur as u32,
            ae_len: self.actor_events.len() as u32,
            ae_start: ae_start as u32,
        };
    }

    pub fn sync_turn_cache_on_turn_change(&mut self, cur_tk: &TurnKey) {
        let ex_len = self.exhausts.len();
        let cbg_len = self.card_block_gains.len();
        let oc_len = self.orb_channels.len();
        let es_len = self.energy_spent.len();
        let cd_len = self.card_drawn.len();
        let ae_len = self.actor_events.len();
        if self.turn_cache.valid
            && self
                .turn_cache
                .lengths_match(ex_len, cbg_len, oc_len, es_len, cd_len, ae_len)
        {
            if self.turn_cache.valid_for == *cur_tk {
                return;
            }
            if (ex_len == 0 || self.exhausts[ex_len - 1].0 != *cur_tk)
                && (cbg_len == 0 || self.card_block_gains[cbg_len - 1].0 != *cur_tk)
                && (oc_len == 0 || self.orb_channels[oc_len - 1].0 != *cur_tk)
                && (es_len == 0 || self.energy_spent[es_len - 1].0 != *cur_tk)
                && (cd_len == 0 || self.card_drawn[cd_len - 1].0 != *cur_tk)
                && (ae_len == 0 || self.actor_events[ae_len - 1].1 != *cur_tk)
            {
                self.turn_cache.valid_for = *cur_tk;
                self.turn_cache.ex_cur = 0;
                self.turn_cache.cbg_cur = 0;
                self.turn_cache.oc_cur = 0;
                self.turn_cache.es_cur_sum = 0;
                self.turn_cache.cd_cur = 0;
                self.turn_cache.ae_start = ae_len as u32;
                return;
            }
        }
        self.refresh_turn_cache(cur_tk);
    }

    #[inline]
    pub fn record_exhaust_tk(&mut self, tk: TurnKey) {
        let synced = self.sync_turn_cache_for_push(tk);
        self.exhausts.push((tk, 0));
        if synced {
            self.turn_cache.ex_len = self.exhausts.len() as u32;
            self.turn_cache.ex_cur += 1;
        }
    }

    pub fn record_exhaust(&mut self, state: &BattleState) {
        self.record_exhaust_tk(Self::current_turn_key(state));
    }

    #[inline]
    pub fn record_block_gain_tk(&mut self, tk: TurnKey, props: ValueProp, play_ordinal: Option<i32>) {
        if props.is_powered_block() {
            if let Some(ordinal) = play_ordinal {
                let synced = self.sync_turn_cache_for_push(tk);
                self.card_block_gains.push((tk, 0, ordinal));
                if synced {
                    self.turn_cache.cbg_len = self.card_block_gains.len() as u32;
                    self.turn_cache.cbg_cur += 1;
                }
            }
        }
    }

    pub fn record_block_gain(&mut self, state: &BattleState, props: ValueProp, play_ordinal: Option<i32>) {
        if props.is_powered_block() {
            if let Some(ordinal) = play_ordinal {
                self.record_block_gain_tk(Self::current_turn_key(state), props, Some(ordinal));
            }
        }
    }

    #[inline]
    pub fn record_orb_channel_tk(&mut self, tk: TurnKey, orb_id: OrbId) {
        let synced = self.sync_turn_cache_for_push(tk);
        self.orb_channels.push((tk, 0, orb_id));
        if synced {
            self.turn_cache.oc_len = self.orb_channels.len() as u32;
            self.turn_cache.oc_cur += 1;
        }
    }

    pub fn record_orb_channel(&mut self, state: &BattleState, orb_id: OrbId) {
        self.record_orb_channel_tk(Self::current_turn_key(state), orb_id);
    }

    #[inline]
    pub fn record_energy_spent_tk(&mut self, tk: TurnKey, amount: i32) {
        if amount > 0 {
            let synced = self.sync_turn_cache_for_push(tk);
            self.energy_spent.push((tk, 0, amount));
            if synced {
                self.turn_cache.es_len = self.energy_spent.len() as u32;
                self.turn_cache.es_cur_sum += amount;
            }
        }
    }

    pub fn record_energy_spent(&mut self, state: &BattleState, amount: i32) {
        if amount > 0 {
            self.record_energy_spent_tk(Self::current_turn_key(state), amount);
        }
    }

    #[inline]
    pub fn record_card_drawn_tk(&mut self, tk: TurnKey, card_type: CardType, from_hand_draw: bool) {
        let synced = self.sync_turn_cache_for_push(tk);
        self.card_drawn.push((tk, 0, card_type, from_hand_draw));
        if synced {
            self.turn_cache.cd_len = self.card_drawn.len() as u32;
            self.turn_cache.cd_cur += 1;
        }
    }

    pub fn record_card_drawn(&mut self, state: &BattleState, card_type: CardType, from_hand_draw: bool) {
        self.record_card_drawn_tk(Self::current_turn_key(state), card_type, from_hand_draw);
    }

    #[inline]
    pub fn record_actor_event_tk(&mut self, tk: TurnKey, kind: impl Into<Cow<'static, str>>) {
        let synced = self.sync_turn_cache_for_push(tk);
        self.actor_events.push((kind.into(), tk, 0));
        if synced {
            self.turn_cache.ae_len = self.actor_events.len() as u32;
        }
    }

    pub fn record_actor_event(&mut self, state: &BattleState, kind: impl Into<Cow<'static, str>>) {
        self.record_actor_event_tk(Self::current_turn_key(state), kind);
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

#[derive(Clone, Copy, Debug)]
pub struct ExtraClutterHintFlag(pub bool);

impl Default for ExtraClutterHintFlag {
    #[inline(always)]
    fn default() -> Self {
        Self(true)
    }
}

impl PartialEq for ExtraClutterHintFlag {
    #[inline(always)]
    fn eq(&self, _other: &Self) -> bool {
        true
    }
}

impl Eq for ExtraClutterHintFlag {}

#[derive(Clone, Copy, Debug)]
pub struct EotCleanupDirtyFlag(pub bool);

impl Default for EotCleanupDirtyFlag {
    #[inline(always)]
    fn default() -> Self {
        Self(true)
    }
}

impl PartialEq for EotCleanupDirtyFlag {
    #[inline(always)]
    fn eq(&self, _other: &Self) -> bool {
        true
    }
}

impl Eq for EotCleanupDirtyFlag {}

#[derive(Clone, Copy, Debug)]
pub struct RelicDirtyMask(pub u64);

impl Default for RelicDirtyMask {
    #[inline(always)]
    fn default() -> Self {
        Self(u64::MAX)
    }
}

impl PartialEq for RelicDirtyMask {
    #[inline(always)]
    fn eq(&self, _other: &Self) -> bool {
        true
    }
}

impl Eq for RelicDirtyMask {}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(C)]
pub struct RootRelicHeader {
    pub stack_count: i32,
    pub floor_val: i32,
    pub id: RelicId,
    pub is_wax: bool,
    pub is_melted: bool,
    pub pf_empty: bool,
    pub has_floor: bool,
}

const _: () = assert!(std::mem::size_of::<RootRelicHeader>() == 16);

impl RootRelicHeader {
    #[inline(always)]
    pub fn from_relic(r: &RelicInst) -> Self {
        Self {
            stack_count: r.stack_count,
            floor_val: r.floor_added_to_deck.unwrap_or(0),
            id: r.id,
            is_wax: r.is_wax,
            is_melted: r.is_melted,
            pf_empty: r.private_fields.is_empty(),
            has_floor: r.floor_added_to_deck.is_some(),
        }
    }

    #[inline(always)]
    pub fn matches_relic(self, r: &RelicInst) -> bool {
        let diff = ((self.id as u32) ^ (r.id as u32))
            | ((self.stack_count as u32) ^ (r.stack_count as u32))
            | ((u8::from(self.is_wax) ^ u8::from(r.is_wax)) as u32)
            | ((u8::from(self.is_melted) ^ u8::from(r.is_melted)) as u32);
        if diff != 0 {
            return false;
        }
        if self.has_floor {
            r.floor_added_to_deck == Some(self.floor_val)
        } else {
            r.floor_added_to_deck.is_none()
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IndexedTransformPool {
    pub cards: Vec<CardId>,
    pub lookup: Box<[u16; CardId::COUNT]>,
}

impl Default for IndexedTransformPool {
    #[inline]
    fn default() -> Self {
        Self {
            cards: Vec::new(),
            lookup: vec![0u16; CardId::COUNT]
                .into_boxed_slice()
                .try_into()
                .unwrap_or_else(|_| unreachable!()),
        }
    }
}

impl IndexedTransformPool {
    #[inline]
    pub fn from_vec(cards: Vec<CardId>) -> Self {
        let mut lookup: Box<[u16; CardId::COUNT]> = vec![0u16; CardId::COUNT]
            .into_boxed_slice()
            .try_into()
            .unwrap_or_else(|_| unreachable!());
        for (idx, &cid) in cards.iter().enumerate() {
            let slot = &mut lookup[cid as usize];
            if *slot == 0 && idx + 1 < u16::MAX as usize {
                *slot = (idx + 1) as u16;
            } else {
                *slot = u16::MAX;
            }
        }
        Self { cards, lookup }
    }
}

#[derive(Clone, Debug)]
pub struct RootPoolCacheInner {
    pub card_gen_pool_bits: [u64; (CardId::COUNT + 63) / 64],
    pub custom_card_gen_transform: [[IndexedTransformPool; 2]; 2],
    pub custom_colorless_transform: [[IndexedTransformPool; 2]; 2],
    pub custom_curse_transform: [[IndexedTransformPool; 2]; 2],
    pub custom_transformation_pool: [[IndexedTransformPool; 2]; 2],
    pub character_combat_pool: Vec<CardId>,
    pub other_character_combat_pool: Vec<CardId>,
    pub colorless_combat_pool: Vec<CardId>,
}

#[derive(Debug, Default)]
pub struct RootPoolCache(pub OnceLock<Box<RootPoolCacheInner>>);

impl Clone for RootPoolCache {
    fn clone(&self) -> Self {
        match self.0.get() {
            Some(inner) => {
                let lock = OnceLock::new();
                let _ = lock.set(inner.clone());
                Self(lock)
            }
            None => Self(OnceLock::new()),
        }
    }
}

impl PartialEq for RootPoolCache {
    #[inline]
    fn eq(&self, _other: &Self) -> bool {
        true
    }
}

impl Eq for RootPoolCache {}

#[derive(Clone, Copy, Debug, Default)]
pub struct ContiguousHandlesFlag {
    pub cards: bool,
    pub creatures: bool,
}

impl PartialEq for ContiguousHandlesFlag {
    #[inline(always)]
    fn eq(&self, _other: &Self) -> bool {
        true
    }
}

impl Eq for ContiguousHandlesFlag {}

#[derive(Clone, Copy, Debug)]
pub struct CardMetaDirtyMask(pub [u64; 8]);

impl CardMetaDirtyMask {
    pub const CLEAN: Self = Self([0; 8]);
    pub const ALL_DIRTY: Self = Self([u64::MAX; 8]);
}

impl Default for CardMetaDirtyMask {
    #[inline(always)]
    fn default() -> Self {
        Self::ALL_DIRTY
    }
}

impl PartialEq for CardMetaDirtyMask {
    #[inline(always)]
    fn eq(&self, _other: &Self) -> bool {
        true
    }
}

impl Eq for CardMetaDirtyMask {}

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
    pub root_run_rng_string_seed: Arc<str>,
    pub root_deck_prefix_len: usize,
    pub root_deck_cards: Vec<CardInst>,
    pub root_deck_handles: Vec<CardHandle>,
    pub root_deck_contiguous: bool,
    pub root_card_floors: Vec<OptI16>,
    pub has_root_relics: bool,
    pub root_relics: Vec<RelicInst>,
    pub root_relic_headers: Vec<RootRelicHeader>,
    pub compact_turn_history: bool,
    pub has_root_rngs: bool,
    pub root_run_rngs: [Rng; 12],
    pub root_player_rngs: [Rng; 4],
    pub root_run_rng_counters: [i32; 12],
    pub root_player_rng_counters: [i32; 4],
    pub has_root_clutter_indices: bool,
    pub root_initial_cards_len: usize,
    pub root_combat_clutter_indices: Vec<u16>,
    pub root_all_clutter_indices: Vec<u16>,
    pub root_initial_cards: Vec<CardInst>,
    pub root_card_fast_hash: Vec<u64>,
    pub root_combat_delta_len: usize,
    pub root_clean_eot_mask: [u64; 8],
    pub has_root_card_hook_mask: bool,
    pub root_initial_card_hook_mask: [u64; 8],
    pub pool_cache: RootPoolCache,
}

impl RootStaticContext {
    #[inline]
    pub fn set_root_rngs(&mut self, run: [Rng; 12], player: [Rng; 4]) {
        self.has_root_rngs = true;
        self.root_run_rngs = run;
        self.root_player_rngs = player;
        for (i, r) in run.iter().enumerate() {
            self.root_run_rng_counters[i] = r.counter;
        }
        for (i, r) in player.iter().enumerate() {
            self.root_player_rng_counters[i] = r.counter;
        }
    }

    #[inline]
    pub fn set_root_card_floors(&mut self, cards: &[CardInst]) {
        self.root_card_floors = cards.iter().map(|c| c.floor_added_to_deck).collect();
    }

    #[inline]
    pub fn set_root_relics(&mut self, relics: &[RelicInst]) {
        self.has_root_relics = true;
        self.root_relics = relics.to_vec();
        self.root_relic_headers = relics.iter().map(RootRelicHeader::from_relic).collect();
    }

    #[inline]
    pub fn set_root_clutter_indices(
        &mut self,
        cards: &[CardInst],
        mut is_status_or_curse: impl FnMut(CardId) -> bool,
    ) {
        if cards.len() > u16::MAX as usize {
            self.has_root_clutter_indices = false;
            self.root_initial_cards_len = 0;
            self.root_combat_clutter_indices.clear();
            self.root_all_clutter_indices.clear();
            return;
        }
        self.has_root_clutter_indices = true;
        self.root_initial_cards_len = cards.len();
        self.root_all_clutter_indices.clear();
        self.root_combat_clutter_indices.clear();
        let prefix = self.root_deck_prefix_len.min(cards.len());
        for (i, c) in cards.iter().enumerate() {
            if is_status_or_curse(c.id) {
                let idx = i as u16;
                self.root_all_clutter_indices.push(idx);
                if i >= prefix {
                    self.root_combat_clutter_indices.push(idx);
                }
            }
        }
    }

    pub fn set_root_initial_cards(&mut self, cards: &[CardInst], player: &PlayerInst) {
        self.root_deck_contiguous = self
            .root_deck_cards
            .iter()
            .enumerate()
            .all(|(i, c)| c.handle.0 == (i + 1) as u32);
        self.root_initial_cards.clear();
        self.root_card_fast_hash.clear();
        self.root_combat_delta_len = 0;
        self.root_clean_eot_mask = [0; 8];
        self.has_root_card_hook_mask = false;
        self.root_initial_card_hook_mask = [0; 8];

        let len = cards.len();
        if len == 0 || len > 512 {
            return;
        }
        let prefix = self.root_deck_prefix_len;
        if prefix > len {
            return;
        }
        if !cards
            .iter()
            .enumerate()
            .all(|(i, c)| c.handle.0 == (i + 1) as u32)
        {
            return;
        }
        if !cards[..prefix].iter().all(|c| c.pile == PileType::Deck) {
            return;
        }
        let mut expected_pile = [PileType::None; 512];
        let mut ok = true;
        let mut mark_pile = |handles: &[CardHandle], pile: PileType| {
            for &ch in handles {
                let idx = ch.0.wrapping_sub(1) as usize;
                if idx < prefix || idx >= len || expected_pile[idx] != PileType::None {
                    ok = false;
                    return;
                }
                expected_pile[idx] = pile;
            }
        };
        mark_pile(&player.hand, PileType::Hand);
        mark_pile(&player.draw, PileType::Draw);
        mark_pile(&player.discard, PileType::Discard);
        mark_pile(&player.exhaust, PileType::Exhaust);
        mark_pile(&player.play, PileType::Play);
        if !ok {
            return;
        }
        for (i, c) in cards.iter().enumerate().skip(prefix) {
            if c.pile != expected_pile[i] {
                return;
            }
        }
        let cleanup_flags = CardTempFlags::TEMPORARY_FREE_TURN
            | CardTempFlags::TEMPORARY_RETAIN_TURN
            | CardTempFlags::TEMPORARY_SLY_TURN
            | CardTempFlags::EXHAUST_ON_NEXT_PLAY;
        let mut eot_mask = [0u64; 8];
        for (i, c) in cards.iter().enumerate() {
            let is_eot_clean = !c.temp_flags.intersects(cleanup_flags)
                && c.temp_cost_override_this_turn.is_none()
                && c.temp_cost_override_this_turn_or_until_played.is_none()
                && c.temp_star_cost_override_this_turn.is_none()
                && c.ext_index.is_none();
            if is_eot_clean {
                eot_mask[i >> 6] |= 1u64 << (i & 63);
            }
        }
        self.root_card_fast_hash = cards.iter().map(compute_card_fast_hash_word).collect();
        self.root_initial_cards = cards.to_vec();
        self.root_combat_delta_len = len;
        self.root_clean_eot_mask = eot_mask;
    }

    pub fn set_root_card_hook_mask(&mut self, mut may_have_hook: impl FnMut(&CardInst) -> bool) {
        self.root_initial_card_hook_mask = [0; 8];
        let len = self
            .root_combat_delta_len
            .min(self.root_initial_cards.len())
            .min(512);
        for (i, c) in self.root_initial_cards[..len].iter().enumerate() {
            if may_have_hook(c) {
                self.root_initial_card_hook_mask[i >> 6] |= 1u64 << (i & 63);
            }
        }
        self.has_root_card_hook_mask = true;
    }
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
            root_run_rng_string_seed: Arc::from(""),
            root_deck_prefix_len: 0,
            root_deck_cards: Vec::new(),
            root_deck_handles: Vec::new(),
            root_deck_contiguous: false,
            root_card_floors: Vec::new(),
            has_root_relics: false,
            root_relics: Vec::new(),
            root_relic_headers: Vec::new(),
            compact_turn_history: false,
            has_root_rngs: false,
            root_run_rngs: [Rng::from_seed(0); 12],
            root_player_rngs: [Rng::from_seed(0); 4],
            root_run_rng_counters: [0; 12],
            root_player_rng_counters: [0; 4],
            has_root_clutter_indices: false,
            root_initial_cards_len: 0,
            root_combat_clutter_indices: Vec::new(),
            root_all_clutter_indices: Vec::new(),
            root_initial_cards: Vec::new(),
            root_card_fast_hash: Vec::new(),
            root_combat_delta_len: 0,
            root_clean_eot_mask: [0; 8],
            has_root_card_hook_mask: false,
            root_initial_card_hook_mask: [0; 8],
            pool_cache: RootPoolCache::default(),
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
pub const HOOK_DIRTY_FLAT_ONLY: u16 = 1 << 11;
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

pub const HOOK_SLOT_TABLE_LEN: usize = 139;

pub const ENEMY_POWER_FLAG_GOLD_THEFT: u8 = 1 << 0;
pub const ENEMY_POWER_FLAG_REVIVING: u8 = 1 << 1;

static HOOK_CACHE_THREAD_SEED: AtomicU64 = AtomicU64::new(1);
thread_local! {
    static HOOK_CACHE_REV: Cell<u64> =
        Cell::new(HOOK_CACHE_THREAD_SEED.fetch_add(1, Ordering::Relaxed) << 40);
}

#[inline(always)]
fn next_hook_cache_revision() -> u64 {
    HOOK_CACHE_REV.with(|c| {
        let next = c.get().wrapping_add(1);
        let non_zero = if next == 0 { 1 } else { next };
        c.set(non_zero);
        non_zero
    })
}

#[derive(Clone, Debug)]
pub struct ActiveHookCache {
    pub dirty_mask: u16,
    pub masks: ActiveHookMasks,
    pub listener_pre_player: Vec<(CachedHookListener, HookMask)>,
    pub listener_post_player: Vec<(CachedHookListener, HookMask)>,
    pub in_combat_mask: u64,
    pub in_combat_overflow: bool,
    pub combat_has_player: bool,
    pub has_active_history_course_relic: bool,
    pub living_check_player_idx: u16,
    pub primary_enemy_indices: [u16; 8],
    pub primary_enemy_count: u8,
    pub enemy_power_flags: u8,
    pub flat_slot_words: u8,
    pub revision: u64,
    pub cached_tables: Option<&'static HookMaskTableView>,
    pub listener_relics: Vec<(usize, HookMask)>,
    pub listener_potions: Vec<(usize, HookMask)>,
    pub listener_orbs: Vec<(usize, HookMask)>,
    pub listener_hand: Vec<(CachedHookListener, HookMask)>,
    pub listener_draw: Vec<(CachedHookListener, HookMask)>,
    pub listener_discard: Vec<(CachedHookListener, HookMask)>,
    pub listener_exhaust: Vec<(CachedHookListener, HookMask)>,
    pub listener_play: Vec<(CachedHookListener, HookMask)>,
    pub listener_deck: Vec<(CachedHookListener, HookMask)>,
    pub listener_run_models: Vec<(RunModelId, HookMask)>,
    pub listener_dominion: Option<(DominionId, HookMask)>,
    pub flat_masks_w0: Vec<u64>,
    pub flat_masks_w1: Vec<u64>,
    pub flat_masks_w2: Vec<u64>,
    pub flat_listeners: Vec<CachedHookListener>,
    pub slot_listener_masks: Option<Box<[u64; HOOK_SLOT_TABLE_LEN]>>,
    pub slot_listener_masks_ext: Option<Box<[[u64; HOOK_SLOT_TABLE_LEN]; 3]>>,
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
            combat_has_player: false,
            has_active_history_course_relic: false,
            living_check_player_idx: u16::MAX,
            primary_enemy_indices: [0; 8],
            primary_enemy_count: u8::MAX,
            enemy_power_flags: 0,
            flat_slot_words: 0,
            revision: 0,
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
            slot_listener_masks: None,
            slot_listener_masks_ext: None,
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
    pub const fn masks_are_valid(&self) -> bool {
        (self.dirty_mask & !HOOK_DIRTY_FLAT_ONLY) == 0 && self.cached_tables.is_some()
    }

    #[inline]
    pub fn recompute_player_inv_masks(&mut self, player_is_active_for_hooks: bool) {
        let base = self
            .masks
            .powers
            .union(self.masks.monsters)
            .union(self.masks.run_and_dominion);
        if self.combat_has_player {
            let player_inv = self
                .masks
                .relics
                .union(self.masks.potions)
                .union(self.masks.orbs)
                .union(self.masks.cards_hand)
                .union(self.masks.cards_draw)
                .union(self.masks.cards_discard)
                .union(self.masks.cards_exhaust)
                .union(self.masks.cards_play);
            self.masks.player_inv = player_inv;
            self.masks.combat_combined = base.union(player_inv);
        } else {
            self.masks.player_inv = HookMask::EMPTY;
            self.masks.combat_combined = base;
        }
        self.masks.run_combat_combined = if player_is_active_for_hooks {
            self.masks.combat_combined.union(self.masks.cards_deck)
        } else {
            self.masks.combat_combined
        };
    }

    #[inline(always)]
    pub(crate) fn combat_pile_flat_base(&self, pile: PileType) -> Option<usize> {
        if !self.combat_has_player {
            return None;
        }
        let pre_inv = self.listener_pre_player.len()
            + self.listener_relics.len()
            + self.listener_potions.len()
            + self.listener_orbs.len();
        match pile {
            PileType::Hand => Some(pre_inv),
            PileType::Draw => Some(pre_inv + self.listener_hand.len()),
            PileType::Discard => Some(pre_inv + self.listener_hand.len() + self.listener_draw.len()),
            PileType::Exhaust => Some(
                pre_inv
                    + self.listener_hand.len()
                    + self.listener_draw.len()
                    + self.listener_discard.len(),
            ),
            PileType::Play => Some(
                pre_inv
                    + self.listener_hand.len()
                    + self.listener_draw.len()
                    + self.listener_discard.len()
                    + self.listener_exhaust.len(),
            ),
            _ => None,
        }
    }

    #[inline(always)]
    pub const fn flat_uses_slot_masks(&self) -> bool {
        self.flat_slot_words != 0
    }

    #[inline(always)]
    pub const fn flat_uses_single_word_masks(&self) -> bool {
        self.flat_slot_words == 1
    }

    #[inline(always)]
    pub fn slot_listener_mask(&self, slot: usize) -> u64 {
        match self.slot_listener_masks.as_deref() {
            Some(m) => m[slot],
            None => 0,
        }
    }

    #[inline(always)]
    pub fn slot_listener_mask_word(&self, slot: usize, word: usize) -> u64 {
        if word == 0 {
            self.slot_listener_mask(slot)
        } else {
            match self.slot_listener_masks_ext.as_deref() {
                Some(m) => m[word - 1][slot],
                None => 0,
            }
        }
    }

    #[inline(always)]
    pub(crate) fn combat_pile_parts_mut(
        &mut self,
        pile: PileType,
    ) -> Option<(&mut Vec<(CachedHookListener, HookMask)>, &mut HookMask)> {
        match pile {
            PileType::Hand => Some((&mut self.listener_hand, &mut self.masks.cards_hand)),
            PileType::Draw => Some((&mut self.listener_draw, &mut self.masks.cards_draw)),
            PileType::Discard => Some((&mut self.listener_discard, &mut self.masks.cards_discard)),
            PileType::Exhaust => Some((&mut self.listener_exhaust, &mut self.masks.cards_exhaust)),
            PileType::Play => Some((&mut self.listener_play, &mut self.masks.cards_play)),
            _ => None,
        }
    }

    #[inline(always)]
    pub fn flat_mask_word(&self, word: usize) -> &[u64] {
        match word {
            0 => &self.flat_masks_w0,
            1 => &self.flat_masks_w1,
            _ => &self.flat_masks_w2,
        }
    }

    #[inline(always)]
    fn shift_power_indices_from(
        &mut self,
        creature_handle: CreatureHandle,
        removed_power_idx: u16,
        in_pre: bool,
        post_idx_removed: usize,
        flat_pos: usize,
    ) -> bool {
        let mut changed = false;
        let dec = |l: &mut CachedHookListener, changed: &mut bool| {
            if let CachedHookListener::Power {
                creature,
                power_idx,
                ..
            } = l
            {
                if *creature == creature_handle
                    && *power_idx != u16::MAX
                    && *power_idx > removed_power_idx
                {
                    *power_idx -= 1;
                    *changed = true;
                }
            }
        };
        if in_pre {
            #[cfg(debug_assertions)]
            debug_assert!(
                !self.listener_pre_player[..flat_pos]
                    .iter()
                    .chain(self.listener_post_player.iter())
                    .any(|(l, _)| matches!(
                        l,
                        CachedHookListener::Power { creature, power_idx, .. }
                        if *creature == creature_handle && *power_idx != u16::MAX && *power_idx > removed_power_idx
                    ))
            );
            for (l, _) in &mut self.listener_pre_player[flat_pos..] {
                dec(l, &mut changed);
            }
        } else {
            #[cfg(debug_assertions)]
            debug_assert!(
                !self.listener_pre_player
                    .iter()
                    .chain(self.listener_post_player[..post_idx_removed].iter())
                    .any(|(l, _)| matches!(
                        l,
                        CachedHookListener::Power { creature, power_idx, .. }
                        if *creature == creature_handle && *power_idx != u16::MAX && *power_idx > removed_power_idx
                    ))
            );
            for (l, _) in &mut self.listener_post_player[post_idx_removed..] {
                dec(l, &mut changed);
            }
        }
        if changed {
            #[cfg(debug_assertions)]
            debug_assert!(!self.flat_listeners[..flat_pos].iter().any(|l| matches!(
                l,
                CachedHookListener::Power { creature, power_idx, .. }
                if *creature == creature_handle && *power_idx != u16::MAX && *power_idx > removed_power_idx
            )));
            for l in &mut self.flat_listeners[flat_pos..] {
                if let CachedHookListener::Power {
                    creature,
                    power_idx,
                    ..
                } = l
                {
                    if *creature == creature_handle
                        && *power_idx != u16::MAX
                        && *power_idx > removed_power_idx
                    {
                        *power_idx -= 1;
                    }
                }
            }
        }
        changed
    }

    #[inline(always)]
    fn shift_power_indices(&mut self, creature_handle: CreatureHandle, removed_power_idx: u16) -> bool {
        let mut changed = false;
        for (l, _) in &mut self.listener_pre_player {
            if let CachedHookListener::Power {
                creature,
                power_idx,
                ..
            } = l
            {
                if *creature == creature_handle
                    && *power_idx != u16::MAX
                    && *power_idx > removed_power_idx
                {
                    *power_idx -= 1;
                    changed = true;
                }
            }
        }
        for (l, _) in &mut self.listener_post_player {
            if let CachedHookListener::Power {
                creature,
                power_idx,
                ..
            } = l
            {
                if *creature == creature_handle
                    && *power_idx != u16::MAX
                    && *power_idx > removed_power_idx
                {
                    *power_idx -= 1;
                    changed = true;
                }
            }
        }
        if changed {
            for l in &mut self.flat_listeners {
                if let CachedHookListener::Power {
                    creature,
                    power_idx,
                    ..
                } = l
                {
                    if *creature == creature_handle
                        && *power_idx != u16::MAX
                        && *power_idx > removed_power_idx
                    {
                        *power_idx -= 1;
                    }
                }
            }
        }
        changed
    }

    #[inline]
    pub fn clear(&mut self) {
        self.dirty_mask = HOOK_DIRTY_ALL;
        self.masks = ActiveHookMasks::default();
        self.listener_pre_player.clear();
        self.listener_post_player.clear();
        self.in_combat_mask = 0;
        self.in_combat_overflow = false;
        self.combat_has_player = false;
        self.has_active_history_course_relic = false;
        self.living_check_player_idx = u16::MAX;
        self.primary_enemy_indices = [0; 8];
        self.primary_enemy_count = u8::MAX;
        self.enemy_power_flags = 0;
        self.flat_slot_words = 0;
        self.revision = 0;
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
        let slot_masks_eq = if self.flat_slot_words != other.flat_slot_words {
            false
        } else if self.flat_slot_words != 0 {
            let words = self.flat_slot_words as usize;
            let mut active_eq = true;
            for w in 0..3 {
                let mut word = self.masks.combat_combined.0[w];
                let base = w * 64;
                while word != 0 {
                    let b = word.trailing_zeros() as usize;
                    word &= word - 1;
                    let s = base + b;
                    for lw in 0..words {
                        if self.slot_listener_mask_word(s, lw)
                            != other.slot_listener_mask_word(s, lw)
                        {
                            active_eq = false;
                            break;
                        }
                    }
                    if !active_eq {
                        break;
                    }
                }
                if !active_eq {
                    break;
                }
            }
            active_eq
        } else {
            self.flat_masks_w0 == other.flat_masks_w0
                && self.flat_masks_w1 == other.flat_masks_w1
                && self.flat_masks_w2 == other.flat_masks_w2
        };
        self.dirty_mask == other.dirty_mask
            && self.masks == other.masks
            && self.listener_pre_player == other.listener_pre_player
            && self.listener_post_player == other.listener_post_player
            && self.in_combat_mask == other.in_combat_mask
            && self.in_combat_overflow == other.in_combat_overflow
            && self.combat_has_player == other.combat_has_player
            && self.has_active_history_course_relic == other.has_active_history_course_relic
            && self.living_check_player_idx == other.living_check_player_idx
            && self.primary_enemy_count == other.primary_enemy_count
            && (self.primary_enemy_count == u8::MAX
                || {
                    let cnt = self.primary_enemy_count as usize;
                    let a = &self.primary_enemy_indices[..cnt];
                    let b = &other.primary_enemy_indices[..cnt];
                    a == b || a.iter().all(|x| b.contains(x))
                })
            && self.enemy_power_flags == other.enemy_power_flags
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
            && slot_masks_eq
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
    pub battleworn_dummy_timed_out: bool,
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
    pub attacking_creatures: SmallVec<[(CreatureHandle, u16); 4]>,
    pub deferred_death_cleanup: SmallVec<[CreatureHandle; 4]>,
    pub hook_cache: ActiveHookCache,
    pub deck_dirty: DeckDirtyFlag,
    pub relic_dirty_mask: RelicDirtyMask,
    pub handle_layout: ContiguousHandlesFlag,
    pub card_meta_dirty: CardMetaDirtyMask,
    pub extra_clutter_hint: ExtraClutterHintFlag,
    pub eot_card_cleanup_dirty: EotCleanupDirtyFlag,
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
fn lookup_creature_with_idx(
    creatures: &[CreatureInst],
    handle: CreatureHandle,
) -> Option<(usize, &CreatureInst)> {
    let idx = handle.0.wrapping_sub(1) as usize;
    if idx < creatures.len() && creatures[idx].handle == handle {
        return Some((idx, &creatures[idx]));
    }
    lookup_creature_with_idx_cold(creatures, handle)
}

#[cold]
#[inline(never)]
fn lookup_creature_with_idx_cold(
    creatures: &[CreatureInst],
    handle: CreatureHandle,
) -> Option<(usize, &CreatureInst)> {
    creatures.iter().enumerate().find(|(_, c)| c.handle == handle)
}

impl BattleState {
    #[inline(always)]
    pub fn invalidate_hook_cache(&mut self) {
        self.hook_cache.dirty_mask = HOOK_DIRTY_ALL;
        self.hook_cache.has_active_history_course_relic = false;
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

    #[inline(always)]
    pub fn mark_relic_dirty(&mut self, idx: usize) {
        if idx < 64 {
            self.relic_dirty_mask.0 |= 1u64 << idx;
        } else {
            self.relic_dirty_mask.0 = u64::MAX;
        }
    }

    #[inline(always)]
    pub fn mark_all_relics_dirty(&mut self) {
        self.relic_dirty_mask.0 = u64::MAX;
    }

    #[inline(always)]
    pub fn mark_card_meta_dirty(&mut self, idx: usize) {
        if idx < 512 {
            self.card_meta_dirty.0[idx >> 6] |= 1u64 << (idx & 63);
        }
    }

    #[inline(always)]
    pub fn is_card_meta_dirty(&self, idx: usize) -> bool {
        idx >= 512 || ((self.card_meta_dirty.0[idx >> 6] >> (idx & 63)) & 1) != 0
    }

    #[inline(always)]
    pub fn mark_eot_card_cleanup_dirty(&mut self) {
        self.eot_card_cleanup_dirty.0 = true;
    }

    #[inline]
    pub fn card_has_eot_cleanup_state(card: &CardInst, exts: &[CardExtension]) -> bool {
        const CLEANUP_FLAGS: CardTempFlags = CardTempFlags::TEMPORARY_FREE_TURN
            .union(CardTempFlags::TEMPORARY_RETAIN_TURN)
            .union(CardTempFlags::TEMPORARY_SLY_TURN)
            .union(CardTempFlags::EXHAUST_ON_NEXT_PLAY);
        if card.temp_flags.intersects(CLEANUP_FLAGS)
            || card.temp_cost_override_this_turn.is_some()
            || card.temp_cost_override_this_turn_or_until_played.is_some()
            || card.temp_star_cost_override_this_turn.is_some()
        {
            return true;
        }
        if let Some(ext_idx) = card.ext_index {
            if let Some(ext) = exts.get(ext_idx as usize) {
                if ext
                    .local_energy_modifiers
                    .iter()
                    .any(|m| m.kind.clears_at_end_of_turn())
                {
                    return true;
                }
            }
        }
        false
    }

    #[inline(always)]
    pub fn mark_all_card_meta_dirty(&mut self) {
        self.card_meta_dirty = CardMetaDirtyMask::ALL_DIRTY;
    }

    #[inline]
    pub fn cards_are_contiguous(&self) -> bool {
        self.cards
            .iter()
            .enumerate()
            .all(|(i, c)| c.handle.0 == (i + 1) as u32)
    }

    #[inline]
    pub fn creatures_are_contiguous(&self) -> bool {
        self.creatures
            .iter()
            .enumerate()
            .all(|(i, c)| c.handle.0 == (i + 1) as u32)
    }

    #[inline]
    pub fn recompute_handle_layout(&mut self) {
        self.handle_layout = ContiguousHandlesFlag {
            cards: self.cards_are_contiguous(),
            creatures: self.creatures_are_contiguous(),
        };
    }

    #[inline(always)]
    pub fn push_card(&mut self, card: CardInst) {
        let idx = self.cards.len();
        let expected = (idx + 1) as u32;
        if self.handle_layout.cards && card.handle.0 != expected {
            self.handle_layout.cards = false;
        }
        if !self.extra_clutter_hint.0
            && (!self.static_ctx.has_root_clutter_indices
                || (idx >= self.static_ctx.root_initial_cards_len
                    && self
                        .hook_cache
                        .cached_tables
                        .is_none_or(|t| t.card_is_status_or_curse(card.id))))
        {
            self.extra_clutter_hint.0 = true;
        }
        if !self.eot_card_cleanup_dirty.0
            && Self::card_has_eot_cleanup_state(&card, &self.card_extensions)
        {
            self.eot_card_cleanup_dirty.0 = true;
        }
        self.cards.push(card);
    }

    #[inline(always)]
    pub fn push_creature(&mut self, creature: CreatureInst) {
        let expected = (self.creatures.len() + 1) as u32;
        if self.handle_layout.creatures && creature.handle.0 != expected {
            self.handle_layout.creatures = false;
        }
        self.creatures.push(creature);
    }

    pub fn try_add_power_to_hook_cache(
        &mut self,
        creature_handle: CreatureHandle,
        creature_idx: usize,
        power_idx: usize,
        power: PowerInst,
    ) -> bool {
        let words = self.hook_cache.flat_slot_words as usize;
        if self.hook_cache.dirty_mask != 0
            || words == 0
            || power.id == PowerId::MinionPower
            || creature_idx >= self.creatures.len()
            || creature_idx >= u16::MAX as usize
            || power_idx >= u16::MAX as usize
        {
            return false;
        }
        let Some(tables) = self.hook_cache.cached_tables else {
            return false;
        };
        let c = &self.creatures[creature_idx];
        if c.handle != creature_handle
            || c.is_detached
            || (c.is_player && !self.player.is_active_for_hooks)
            || power_idx + 1 != c.powers.len()
        {
            return false;
        }
        let is_first_ally_player =
            self.ally_handles.first() == Some(&creature_handle) && c.is_player;
        let enemy_pos = if !is_first_ally_player && !self.ally_handles.contains(&creature_handle) {
            self.enemy_handles.iter().position(|&h| h == creature_handle)
        } else {
            None
        };
        if !is_first_ally_player && enemy_pos.is_none() {
            return false;
        }
        #[cfg(debug_assertions)]
        debug_assert!(self.contains_creature(creature_handle));

        let mut rev_changed = false;
        if enemy_pos.is_some() {
            let add_flag = match power.id {
                PowerId::ThieveryPower | PowerId::SwipePower => ENEMY_POWER_FLAG_GOLD_THEFT,
                PowerId::ReattachPower | PowerId::AdaptablePower => ENEMY_POWER_FLAG_REVIVING,
                _ => 0,
            };
            if add_flag != 0 && (self.hook_cache.enemy_power_flags & add_flag) != add_flag {
                self.hook_cache.enemy_power_flags |= add_flag;
                rev_changed = true;
            }
        }

        if !tables.power_has_hook(power.id) {
            if rev_changed {
                self.hook_cache.revision = next_hook_cache_revision();
            }
            return true;
        }

        if self.hook_cache.flat_listeners.len() >= (words << 6)
            || self.hook_cache.flat_listeners.len() >= 256
        {
            return false;
        }
        let pm = mask_at(tables.powers, power.id as usize);
        let new_listener = CachedHookListener::Power {
            creature: creature_handle,
            power_handle: power.handle,
            creature_idx: creature_idx as u16,
            power_idx: power_idx as u16,
        };
        let is_active_player_hooks = self.player.is_active_for_hooks;
        let enemy_handles = &self.enemy_handles;
        let out = &mut self.hook_cache;
        let Some(slot_masks) = out.slot_listener_masks.as_deref_mut() else {
            return false;
        };
        if words >= 2 && out.slot_listener_masks_ext.is_none() {
            return false;
        }
        let old_len = out.flat_listeners.len();
        let flat_pos = if is_first_ally_player {
            let pre_idx = out.listener_pre_player.len();
            out.listener_pre_player.push((new_listener, pm));
            out.masks.pre_player = out.masks.pre_player.union(pm);
            pre_idx
        } else {
            let e_pos = enemy_pos.unwrap();
            let later_enemies = &enemy_handles[e_pos + 1..];
            let post_insert_idx = out
                .listener_post_player
                .iter()
                .position(|&(l, _)| match l {
                    CachedHookListener::Monster(ch) => {
                        ch == creature_handle || later_enemies.contains(&ch)
                    }
                    CachedHookListener::Power { creature, .. } => {
                        later_enemies.contains(&creature)
                    }
                    _ => false,
                })
                .unwrap_or(out.listener_post_player.len());
            let post_base = old_len
                - out.listener_post_player.len()
                - out.listener_run_models.len()
                - usize::from(out.listener_dominion.is_some());
            out.listener_post_player
                .insert(post_insert_idx, (new_listener, pm));
            out.masks.post_player = out.masks.post_player.union(pm);
            post_base + post_insert_idx
        };

        out.flat_listeners.insert(flat_pos, new_listener);
        let old_comb = out.masks.combat_combined;
        if words == 1 {
            let new_bit = 1u64 << flat_pos;
            if flat_pos < old_len {
                let lo_mask = new_bit - 1;
                let hi_mask = !lo_mask;
                for w in 0..3 {
                    let mut cw = old_comb.0[w];
                    let base = w * 64;
                    while cw != 0 {
                        let s = base + cw.trailing_zeros() as usize;
                        cw &= cw - 1;
                        debug_assert!(s < HOOK_SLOT_TABLE_LEN);
                        unsafe {
                            let slot = slot_masks.get_unchecked_mut(s);
                            let m = *slot;
                            if (m & hi_mask) != 0 {
                                *slot = (m & lo_mask) | ((m & hi_mask) << 1);
                            }
                        }
                    }
                }
            }
            for w in 0..3 {
                let mut pw = pm.0[w];
                let base = w * 64;
                while pw != 0 {
                    let b = pw.trailing_zeros() as usize;
                    pw &= pw - 1;
                    let s = base + b;
                    let bit = 1u64 << b;
                    debug_assert!(s < HOOK_SLOT_TABLE_LEN);
                    unsafe {
                        let slot = slot_masks.get_unchecked_mut(s);
                        *slot = if (old_comb.0[w] & bit) == 0 {
                            new_bit
                        } else {
                            *slot | new_bit
                        };
                    }
                }
            }
        } else {
            let slot_masks_ext = out.slot_listener_masks_ext.as_deref_mut().expect("checked");
            let pos_w = flat_pos >> 6;
            let pos_b = flat_pos & 63;
            let new_bit = 1u64 << pos_b;
            if flat_pos < old_len {
                let lo_mask = new_bit - 1;
                let hi_mask = !lo_mask;
                for w in 0..3 {
                    let mut cw = old_comb.0[w];
                    let base = w * 64;
                    while cw != 0 {
                        let s = base + cw.trailing_zeros() as usize;
                        cw &= cw - 1;
                        debug_assert!(s < HOOK_SLOT_TABLE_LEN);
                        unsafe {
                            let mut carry = if pos_w == 0 {
                                let m = *slot_masks.get_unchecked(s);
                                let c = m >> 63;
                                if (m & hi_mask) != 0 {
                                    *slot_masks.get_unchecked_mut(s) =
                                        (m & lo_mask) | ((m & hi_mask) << 1);
                                }
                                c
                            } else {
                                let m = *slot_masks_ext.get_unchecked(pos_w - 1).get_unchecked(s);
                                let c = m >> 63;
                                if (m & hi_mask) != 0 {
                                    *slot_masks_ext
                                        .get_unchecked_mut(pos_w - 1)
                                        .get_unchecked_mut(s) =
                                        (m & lo_mask) | ((m & hi_mask) << 1);
                                }
                                c
                            };
                            for kw in (pos_w + 1)..words {
                                let slot = slot_masks_ext.get_unchecked_mut(kw - 1).get_unchecked_mut(s);
                                let cur = *slot;
                                let next_c = cur >> 63;
                                *slot = (cur << 1) | carry;
                                carry = next_c;
                            }
                        }
                    }
                }
            }
            for w in 0..3 {
                let mut pw = pm.0[w];
                let base = w * 64;
                while pw != 0 {
                    let b = pw.trailing_zeros() as usize;
                    pw &= pw - 1;
                    let s = base + b;
                    let bit = 1u64 << b;
                    debug_assert!(s < HOOK_SLOT_TABLE_LEN);
                    let had = (old_comb.0[w] & bit) != 0;
                    unsafe {
                        if !had {
                            *slot_masks.get_unchecked_mut(s) =
                                if pos_w == 0 { new_bit } else { 0 };
                            for kw in 1..words {
                                *slot_masks_ext
                                    .get_unchecked_mut(kw - 1)
                                    .get_unchecked_mut(s) = if pos_w == kw { new_bit } else { 0 };
                            }
                        } else if pos_w == 0 {
                            *slot_masks.get_unchecked_mut(s) |= new_bit;
                        } else {
                            *slot_masks_ext
                                .get_unchecked_mut(pos_w - 1)
                                .get_unchecked_mut(s) |= new_bit;
                        }
                    }
                }
            }
        }

        out.masks.powers = out.masks.powers.union(pm);
        out.masks.combat_combined = old_comb.union(pm);
        out.masks.run_combat_combined = if is_active_player_hooks {
            out.masks.combat_combined.union(out.masks.cards_deck)
        } else {
            out.masks.combat_combined
        };
        out.revision = next_hook_cache_revision();
        true
    }

    pub fn try_remove_power_from_hook_cache(
        &mut self,
        creature_handle: CreatureHandle,
        removed_power_idx: usize,
        shifted_tail: bool,
        removed: PowerInst,
    ) -> bool {
        let words = self.hook_cache.flat_slot_words as usize;
        if self.hook_cache.dirty_mask != 0
            || words == 0
            || removed_power_idx >= u16::MAX as usize
            || matches!(
                removed.id,
                PowerId::MinionPower
                    | PowerId::ThieveryPower
                    | PowerId::SwipePower
                    | PowerId::ReattachPower
                    | PowerId::AdaptablePower
            )
        {
            return false;
        }
        let Some(tables) = self.hook_cache.cached_tables else {
            return false;
        };
        if shifted_tail
            && self
                .creature(creature_handle)
                .is_none_or(|c| c.powers.len() >= u16::MAX as usize)
        {
            return false;
        }
        if !tables.power_has_hook(removed.id) {
            if shifted_tail
                && self
                    .hook_cache
                    .shift_power_indices(creature_handle, removed_power_idx as u16)
            {
                self.hook_cache.revision = next_hook_cache_revision();
            }
            return true;
        }
        if words >= 2 {
            let new_len = self.hook_cache.flat_listeners.len().saturating_sub(1);
            let new_words = ((new_len + 63) >> 6).max(1);
            if new_words != words || self.hook_cache.slot_listener_masks_ext.is_none() {
                return false;
            }
        }

        let is_active_player_hooks = self.player.is_active_for_hooks;
        let is_player_creature = creature_handle == self.player.creature_handle;
        let removed_pm = tables
            .powers
            .get(removed.id as usize)
            .copied()
            .unwrap_or(HookMask::EMPTY);
        let out = &mut self.hook_cache;
        let match_listener = |&(l, _): &(CachedHookListener, HookMask)| {
            matches!(
                l,
                CachedHookListener::Power {
                    creature,
                    power_handle,
                    ..
                } if creature == creature_handle && power_handle == removed.handle
            )
        };
        let (in_pre, flat_pos, post_idx_removed) = if is_player_creature {
            if let Some(pre_idx) = out.listener_pre_player.iter().position(match_listener) {
                out.listener_pre_player.remove(pre_idx);
                (true, pre_idx, 0)
            } else if let Some(post_idx) = out.listener_post_player.iter().position(match_listener) {
                let post_base = out.flat_listeners.len()
                    - out.listener_post_player.len()
                    - out.listener_run_models.len()
                    - usize::from(out.listener_dominion.is_some());
                out.listener_post_player.remove(post_idx);
                (false, post_base + post_idx, post_idx)
            } else {
                return false;
            }
        } else if let Some(post_idx) = out.listener_post_player.iter().position(match_listener) {
            let post_base = out.flat_listeners.len()
                - out.listener_post_player.len()
                - out.listener_run_models.len()
                - usize::from(out.listener_dominion.is_some());
            out.listener_post_player.remove(post_idx);
            (false, post_base + post_idx, post_idx)
        } else if let Some(pre_idx) = out.listener_pre_player.iter().position(match_listener) {
            out.listener_pre_player.remove(pre_idx);
            (true, pre_idx, 0)
        } else {
            return false;
        };

        out.flat_listeners.remove(flat_pos);
        if shifted_tail {
            let _ = out.shift_power_indices_from(
                creature_handle,
                removed_power_idx as u16,
                in_pre,
                post_idx_removed,
                flat_pos,
            );
        }

        let Some(slot_masks) = out.slot_listener_masks.as_deref_mut() else {
            return false;
        };
        let old_comb = out.masks.combat_combined;
        let shift_mask = if in_pre {
            old_comb
        } else {
            let mut sm = removed_pm;
            for &(_, m) in &out.listener_post_player[post_idx_removed..] {
                sm = sm.union(m);
            }
            for &(_, m) in &out.listener_run_models {
                sm = sm.union(m);
            }
            if let Some((_, m)) = out.listener_dominion {
                sm = sm.union(m);
            }
            sm
        };
        if words == 1 {
            let lo_mask = (1u64 << flat_pos) - 1;
            let hi_mask = !lo_mask;
            #[cfg(debug_assertions)]
            if !in_pre {
                for w in 0..3 {
                    let mut cw = old_comb.0[w] & !shift_mask.0[w];
                    let base = w * 64;
                    while cw != 0 {
                        let s = base + cw.trailing_zeros() as usize;
                        cw &= cw - 1;
                        debug_assert_eq!(slot_masks[s] & hi_mask, 0);
                    }
                }
            }
            for w in 0..3 {
                let mut cw = shift_mask.0[w];
                let base = w * 64;
                while cw != 0 {
                    let s = base + cw.trailing_zeros() as usize;
                    cw &= cw - 1;
                    debug_assert!(s < HOOK_SLOT_TABLE_LEN);
                    unsafe {
                        let slot = slot_masks.get_unchecked_mut(s);
                        let m = *slot;
                        if (m & hi_mask) != 0 {
                            *slot = (m & lo_mask) | ((m >> 1) & hi_mask);
                        }
                    }
                }
            }
        } else {
            let slot_masks_ext = out.slot_listener_masks_ext.as_deref_mut().expect("checked");
            let pos_w = flat_pos >> 6;
            let pos_b = flat_pos & 63;
            let lo_mask = (1u64 << pos_b) - 1;
            let hi_mask = !lo_mask;
            #[cfg(debug_assertions)]
            if !in_pre {
                for w in 0..3 {
                    let mut cw = old_comb.0[w] & !shift_mask.0[w];
                    let base = w * 64;
                    while cw != 0 {
                        let s = base + cw.trailing_zeros() as usize;
                        cw &= cw - 1;
                        let mw = if pos_w == 0 {
                            slot_masks[s]
                        } else {
                            slot_masks_ext[pos_w - 1][s]
                        };
                        debug_assert_eq!(mw & hi_mask, 0);
                        for k in (pos_w + 1)..words {
                            debug_assert_eq!(slot_masks_ext[k - 1][s], 0);
                        }
                    }
                }
            }
            for w in 0..3 {
                let mut cw = shift_mask.0[w];
                let base = w * 64;
                while cw != 0 {
                    let s = base + cw.trailing_zeros() as usize;
                    cw &= cw - 1;
                    debug_assert!(s < HOOK_SLOT_TABLE_LEN);
                    unsafe {
                        for k in pos_w..words {
                            let next_carry = if k + 1 < words {
                                *slot_masks_ext.get_unchecked(k).get_unchecked(s) & 1
                            } else {
                                0
                            };
                            if k == pos_w {
                                if pos_w == 0 {
                                    let slot = slot_masks.get_unchecked_mut(s);
                                    let m = *slot;
                                    *slot = (m & lo_mask)
                                        | ((m >> 1) & hi_mask)
                                        | (next_carry << 63);
                                } else {
                                    let slot = slot_masks_ext
                                        .get_unchecked_mut(pos_w - 1)
                                        .get_unchecked_mut(s);
                                    let m = *slot;
                                    *slot = (m & lo_mask)
                                        | ((m >> 1) & hi_mask)
                                        | (next_carry << 63);
                                }
                            } else {
                                let slot = slot_masks_ext
                                    .get_unchecked_mut(k - 1)
                                    .get_unchecked_mut(s);
                                *slot = (*slot >> 1) | (next_carry << 63);
                            }
                        }
                    }
                }
            }
        }

        let mut powers = HookMask::EMPTY;
        if in_pre {
            let mut pre = HookMask::EMPTY;
            for &(l, m) in &out.listener_pre_player {
                pre = pre.union(m);
                if matches!(l, CachedHookListener::Power { .. }) {
                    powers = powers.union(m);
                }
            }
            out.masks.pre_player = pre;
            for &(l, m) in &out.listener_post_player {
                if matches!(l, CachedHookListener::Power { .. }) {
                    powers = powers.union(m);
                }
            }
        } else {
            for &(l, m) in &out.listener_pre_player {
                if matches!(l, CachedHookListener::Power { .. }) {
                    powers = powers.union(m);
                }
            }
            let mut post = HookMask::EMPTY;
            for &(l, m) in &out.listener_post_player {
                post = post.union(m);
                if matches!(l, CachedHookListener::Power { .. }) {
                    powers = powers.union(m);
                }
            }
            out.masks.post_player = post;
        }
        out.masks.powers = powers;
        let base = powers
            .union(out.masks.monsters)
            .union(out.masks.run_and_dominion);
        out.masks.combat_combined = if out.combat_has_player {
            base.union(out.masks.player_inv)
        } else {
            base
        };
        out.masks.run_combat_combined = if is_active_player_hooks {
            out.masks.combat_combined.union(out.masks.cards_deck)
        } else {
            out.masks.combat_combined
        };
        out.revision = next_hook_cache_revision();
        true
    }

    #[inline]
    pub fn card_has_any_hook(&self, tables: &HookMaskTableView, handle: CardHandle) -> bool {
        self.card_has_any_hook_in_pile(tables, handle, false)
    }

    #[inline(always)]
    fn card_inst_has_any_hook_in_pile(
        &self,
        tables: &HookMaskTableView,
        card: &CardInst,
        is_deck: bool,
    ) -> bool {
        if tables.card_has_hook(card.id) {
            if !is_deck {
                return true;
            }
            let raw_card = mask_at(tables.cards, card.id as usize);
            let dc = tables.categories.deck_cards.0;
            if ((raw_card.0[0] & dc[0]) | (raw_card.0[1] & dc[1]) | (raw_card.0[2] & dc[2])) != 0 {
                return true;
            }
        }
        if !is_deck && !tables.afflictions_self_card_entered_only {
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
        self.card_inst_has_any_hook_in_pile(tables, card, is_deck)
    }

    #[inline(always)]
    pub fn fast_card_meta(
        &self,
        content: &dyn ContentDispatcher,
        id: CardId,
    ) -> Result<&'static CardMeta, KernelError> {
        if let Some(t) = self.hook_cache.cached_tables {
            if let Some(meta) = t.card_metas.get(id as usize) {
                return Ok(meta);
            }
        }
        content.card_meta(id)
    }

    #[inline(always)]
    pub fn fast_card_resolved_type(
        &self,
        content: &dyn ContentDispatcher,
        card_handle: CardHandle,
        id: CardId,
    ) -> Result<CardType, KernelError> {
        if id != CardId::MadScience {
            if let Some(t) = self.hook_cache.cached_tables {
                if t.card_dynamic_type_mad_science_only {
                    if let Some(meta) = t.card_metas.get(id as usize) {
                        return Ok(meta.card_type);
                    }
                }
            }
        }
        content.card_resolved_type(self, card_handle)
    }

    #[inline(always)]
    pub fn fast_power_meta(
        &self,
        content: &dyn ContentDispatcher,
        id: PowerId,
    ) -> Result<&'static PowerMeta, KernelError> {
        if let Some(t) = self.hook_cache.cached_tables {
            if let Some(meta) = t.power_metas.get(id as usize) {
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
        if self.hook_cache.dirty_mask == 0 {
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
            #[cfg(debug_assertions)]
            let prev_masks_if_flat_only = (dirty == HOOK_DIRTY_FLAT_ONLY
                && self.hook_cache.cached_tables.is_some())
            .then_some(self.hook_cache.masks);
            self.rebuild_dirty_hook_cache(content, tables, dirty)?;
            #[cfg(debug_assertions)]
            if let Some(prev) = prev_masks_if_flat_only {
                debug_assert_eq!(
                    prev, self.hook_cache.masks,
                    "HOOK_DIRTY_FLAT_ONLY had stale masks!"
                );
            }
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
        let (static_rms, static_dom) = if (dirty & HOOK_DIRTY_STATIC) != 0 {
            Self::collect_static_hook_masks(self, content, dirty)?
        } else {
            (None, None)
        };
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

    #[inline(always)]
    fn push_diff<T: Copy + PartialEq>(
        dst: &mut Vec<T>,
        write_idx: &mut usize,
        changed: &mut bool,
        item: T,
    ) {
        if !*changed && *write_idx < dst.len() && dst[*write_idx] == item {
            *write_idx += 1;
            return;
        }
        *changed = true;
        if *write_idx < dst.len() {
            dst[*write_idx] = item;
        } else {
            dst.push(item);
        }
        *write_idx += 1;
    }

    fn scan_combat_pile_listeners(
        cards: &[CardInst],
        card_extensions: &[CardExtension],
        pile: &[CardHandle],
        tables: &HookMaskTableView,
        dst: &mut Vec<(CachedHookListener, HookMask)>,
    ) -> (HookMask, bool) {
        if pile.is_empty() {
            let changed = !dst.is_empty();
            if changed {
                dst.clear();
            }
            return (HookMask::EMPTY, changed);
        }
        let has_any_ext = !card_extensions.is_empty();
        let mut acc = HookMask::EMPTY;
        let mut write_idx = 0usize;
        let mut changed = false;
        for (i, &ch) in pile.iter().enumerate() {
            #[cfg(target_arch = "x86_64")]
            if i + 4 < pile.len() {
                let ahead = unsafe { *pile.get_unchecked(i + 4) }.0.wrapping_sub(1) as usize;
                if ahead < cards.len() {
                    unsafe {
                        core::arch::x86_64::_mm_prefetch::<{ core::arch::x86_64::_MM_HINT_T0 }>(
                            cards.as_ptr().add(ahead).cast::<i8>(),
                        );
                    }
                }
            }
            let Some(card) = lookup_card(cards, ch) else {
                continue;
            };
            let _ = i;
            if card.removed_from_state {
                continue;
            }
            if tables.card_has_hook(card.id) {
                let card_mask = mask_at(tables.cards, card.id as usize);
                acc = acc.union(card_mask);
                Self::push_diff(dst, &mut write_idx, &mut changed, (CachedHookListener::Card(ch), card_mask));
            }
            if has_any_ext {
                if let Some(ext) = card.ext_index.and_then(|i| card_extensions.get(i as usize)) {
                    if let Some(en) = ext.enchantment.as_ref() {
                        let ench_mask = mask_at(tables.enchantments, en.id as usize);
                        if !ench_mask.is_empty() {
                            acc = acc.union(ench_mask);
                            Self::push_diff(
                                dst,
                                &mut write_idx,
                                &mut changed,
                                (CachedHookListener::Enchantment(ch), ench_mask),
                            );
                        }
                    }
                }
            }
            if !tables.afflictions_self_card_entered_only {
                if let Some(af) = card.affliction {
                    let aff_mask = mask_at(tables.afflictions, af.id as usize);
                    if !aff_mask.is_empty() {
                        acc = acc.union(aff_mask);
                        Self::push_diff(
                            dst,
                            &mut write_idx,
                            &mut changed,
                            (CachedHookListener::Affliction(ch), aff_mask),
                        );
                    }
                }
            }
        }
        if write_idx != dst.len() {
            dst.truncate(write_idx);
            changed = true;
        }
        (acc, changed)
    }

    fn scan_deck_pile_listeners(
        cards: &[CardInst],
        card_extensions: &[CardExtension],
        pile: &[CardHandle],
        tables: &HookMaskTableView,
        dst: &mut Vec<(CachedHookListener, HookMask)>,
    ) -> (HookMask, bool) {
        let was_empty = dst.is_empty();
        if !was_empty {
            dst.clear();
        }
        let has_any_ext = !card_extensions.is_empty();
        let dc = tables.categories.deck_cards.0;
        let mut acc = HookMask::EMPTY;
        for &ch in pile {
            let Some(card) = lookup_card(cards, ch) else {
                continue;
            };
            if card.removed_from_state {
                continue;
            }
            if tables.card_has_hook(card.id) {
                let raw_card = mask_at(tables.cards, card.id as usize);
                let card_mask = HookMask([
                    raw_card.0[0] & dc[0],
                    raw_card.0[1] & dc[1],
                    raw_card.0[2] & dc[2],
                ]);
                if !card_mask.is_empty() {
                    acc = acc.union(card_mask);
                    dst.push((CachedHookListener::Card(ch), card_mask));
                }
            }
            if has_any_ext {
                if let Some(ext) = card.ext_index.and_then(|i| card_extensions.get(i as usize)) {
                    if let Some(en) = ext.enchantment.as_ref() {
                        let ench_mask = mask_at(tables.enchantments, en.id as usize);
                        if !ench_mask.is_empty() {
                            acc = acc.union(ench_mask);
                            dst.push((CachedHookListener::Enchantment(ch), ench_mask));
                        }
                    }
                }
            }
        }
        (acc, !(was_empty && dst.is_empty()))
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
        let was_uninit = out.cached_tables.is_none();
        out.cached_tables = Some(tables);
        let prev_combat_has_player = out.combat_has_player;
        let prev_deck_mask = out.masks.cards_deck;
        let prev_combat_combined = out.masks.combat_combined;
        let prev_run_combat_combined = out.masks.run_combat_combined;
        let mut flat_dirty = was_uninit || (dirty & (HOOK_DIRTY_STATIC | HOOK_DIRTY_FLAT_ONLY)) != 0;
        let mut body_changed = flat_dirty;

        if (dirty & HOOK_DIRTY_RELICS) != 0 {
            let mut write_idx = 0usize;
            let mut changed = false;
            let mut m_acc = HookMask::EMPTY;
            let mut has_history_course = false;
            for (i, relic) in player.relics.iter().enumerate() {
                if !relic.is_melted && relic.id == RelicId::HistoryCourse {
                    has_history_course = true;
                }
                if relic.is_melted || !tables.relic_has_hook(relic.id) {
                    continue;
                }
                let m = mask_at(tables.relics, relic.id as usize);
                m_acc = m_acc.union(m);
                Self::push_diff(&mut out.listener_relics, &mut write_idx, &mut changed, (i, m));
            }
            if write_idx != out.listener_relics.len() {
                out.listener_relics.truncate(write_idx);
                changed = true;
            }
            out.masks.relics = m_acc;
            out.has_active_history_course_relic = has_history_course;
            if changed {
                flat_dirty = true;
            }
        }

        if (dirty & HOOK_DIRTY_POTIONS) != 0 {
            let mut write_idx = 0usize;
            let mut changed = false;
            let mut m_acc = HookMask::EMPTY;
            for (i, slot) in player.potion_slots.iter().enumerate() {
                if let Some(pid) = *slot {
                    let m = mask_at(tables.potions, pid as usize);
                    if !m.is_empty() {
                        m_acc = m_acc.union(m);
                        Self::push_diff(&mut out.listener_potions, &mut write_idx, &mut changed, (i, m));
                    }
                }
            }
            if write_idx != out.listener_potions.len() {
                out.listener_potions.truncate(write_idx);
                changed = true;
            }
            out.masks.potions = m_acc;
            if changed {
                flat_dirty = true;
            }
        }

        if (dirty & HOOK_DIRTY_ORBS) != 0 {
            let mut write_idx = 0usize;
            let mut changed = false;
            let mut m_acc = HookMask::EMPTY;
            for (i, orb) in player.orbs.iter().enumerate() {
                let m = mask_at(tables.orbs, orb.id as usize);
                if !m.is_empty() {
                    m_acc = m_acc.union(m);
                    Self::push_diff(&mut out.listener_orbs, &mut write_idx, &mut changed, (i, m));
                }
            }
            if write_idx != out.listener_orbs.len() {
                out.listener_orbs.truncate(write_idx);
                changed = true;
            }
            out.masks.orbs = m_acc;
            if changed {
                flat_dirty = true;
            }
        }

        if (dirty & HOOK_DIRTY_HAND) != 0 {
            let (m, changed) = Self::scan_combat_pile_listeners(
                cards,
                card_extensions,
                &player.hand,
                tables,
                &mut out.listener_hand,
            );
            out.masks.cards_hand = m;
            if changed {
                flat_dirty = true;
            }
        }
        if (dirty & HOOK_DIRTY_DRAW) != 0 {
            let (m, changed) = Self::scan_combat_pile_listeners(
                cards,
                card_extensions,
                &player.draw,
                tables,
                &mut out.listener_draw,
            );
            out.masks.cards_draw = m;
            if changed {
                flat_dirty = true;
            }
        }
        if (dirty & HOOK_DIRTY_DISCARD) != 0 {
            let (m, changed) = Self::scan_combat_pile_listeners(
                cards,
                card_extensions,
                &player.discard,
                tables,
                &mut out.listener_discard,
            );
            out.masks.cards_discard = m;
            if changed {
                flat_dirty = true;
            }
        }
        if (dirty & HOOK_DIRTY_EXHAUST) != 0 {
            let (m, changed) = Self::scan_combat_pile_listeners(
                cards,
                card_extensions,
                &player.exhaust,
                tables,
                &mut out.listener_exhaust,
            );
            out.masks.cards_exhaust = m;
            if changed {
                flat_dirty = true;
            }
        }
        if (dirty & HOOK_DIRTY_PLAY) != 0 {
            let (m, changed) = Self::scan_combat_pile_listeners(
                cards,
                card_extensions,
                &player.play,
                tables,
                &mut out.listener_play,
            );
            out.masks.cards_play = m;
            if changed {
                flat_dirty = true;
            }
        }
        if (dirty & HOOK_DIRTY_DECK) != 0 {
            let (m, deck_changed) = Self::scan_deck_pile_listeners(
                cards,
                card_extensions,
                &player.deck,
                tables,
                &mut out.listener_deck,
            );
            out.masks.cards_deck = m;
            if deck_changed || m != prev_deck_mask {
                body_changed = true;
            }
        }

        if (dirty & HOOK_DIRTY_CREATURES) != 0 {
            let prev_in_combat_mask = out.in_combat_mask;
            let prev_in_combat_overflow = out.in_combat_overflow;
            let prev_living_idx = out.living_check_player_idx;
            let prev_primary_cnt = out.primary_enemy_count;
            let prev_primary_indices = out.primary_enemy_indices;
            let prev_enemy_power_flags = out.enemy_power_flags;
            let mut pre_idx = 0usize;
            let mut pre_changed = false;
            let mut post_idx = 0usize;
            let mut post_changed = false;
            let mut powers = HookMask::EMPTY;
            let mut monsters = HookMask::EMPTY;
            let mut pre_player = HookMask::EMPTY;
            let mut post_player = HookMask::EMPTY;
            let mut in_combat_mask = 0u64;
            let mut in_combat_overflow = false;
            let mut seen_active_player = false;
            let mut player_in_roster = false;
            let mut living_player_count = 0usize;
            let mut living_player_idx = u16::MAX;
            let mut living_player_valid = true;
            for &ch in ally_handles {
                if ch.0 >= 1 && ch.0 <= 64 {
                    in_combat_mask |= 1u64 << (ch.0 - 1);
                } else {
                    in_combat_overflow = true;
                }
                let Some((c_idx, c)) = lookup_creature_with_idx(creatures, ch) else {
                    living_player_valid = false;
                    continue;
                };
                if c.is_player || (!c.is_monster() && !c.is_pet()) {
                    living_player_count += 1;
                    if c_idx < u16::MAX as usize {
                        living_player_idx = c_idx as u16;
                    } else {
                        living_player_valid = false;
                    }
                }
                if c.is_detached {
                    continue;
                }
                let creature_idx_u16 = c_idx.min(u16::MAX as usize) as u16;
                if c.is_player && ch == player.creature_handle {
                    player_in_roster = true;
                }
                let is_active_player =
                    c.is_player && player.is_active_for_hooks && !seen_active_player;
                if !(c.is_player && !player.is_active_for_hooks) {
                    for (p_idx, p) in c.powers.iter().enumerate() {
                        if !tables.power_has_hook(p.id) {
                            continue;
                        }
                        let pm = mask_at(tables.powers, p.id as usize);
                        powers = powers.union(pm);
                        let l = CachedHookListener::Power {
                            creature: ch,
                            power_handle: p.handle,
                            creature_idx: creature_idx_u16,
                            power_idx: p_idx.min(u16::MAX as usize) as u16,
                        };
                        if !seen_active_player {
                            pre_player = pre_player.union(pm);
                            Self::push_diff(
                                &mut out.listener_pre_player,
                                &mut pre_idx,
                                &mut pre_changed,
                                (l, pm),
                            );
                        } else {
                            post_player = post_player.union(pm);
                            Self::push_diff(
                                &mut out.listener_post_player,
                                &mut post_idx,
                                &mut post_changed,
                                (l, pm),
                            );
                        }
                    }
                }
                if c.is_monster() {
                    if let Some(mid) = c.monster_id {
                        if tables.monster_has_hook(mid) {
                            let mm = mask_at(tables.monsters, mid as usize);
                            monsters = monsters.union(mm);
                            let l = CachedHookListener::Monster(ch);
                            if !seen_active_player {
                                pre_player = pre_player.union(mm);
                                Self::push_diff(
                                    &mut out.listener_pre_player,
                                    &mut pre_idx,
                                    &mut pre_changed,
                                    (l, mm),
                                );
                            } else {
                                post_player = post_player.union(mm);
                                Self::push_diff(
                                    &mut out.listener_post_player,
                                    &mut post_idx,
                                    &mut post_changed,
                                    (l, mm),
                                );
                            }
                        }
                    }
                } else if is_active_player {
                    seen_active_player = true;
                }
            }
            let mut primary_enemy_indices = [0u16; 8];
            let mut primary_enemy_cnt = 0usize;
            let mut primary_enemy_valid = true;
            let mut enemy_power_flags = 0u8;
            for &ch in enemy_handles {
                if ch.0 >= 1 && ch.0 <= 64 {
                    in_combat_mask |= 1u64 << (ch.0 - 1);
                } else {
                    in_combat_overflow = true;
                }
                let Some((c_idx, c)) = lookup_creature_with_idx(creatures, ch) else {
                    primary_enemy_valid = false;
                    continue;
                };
                if !c.is_secondary_enemy() {
                    if primary_enemy_cnt < 8 && c_idx < u16::MAX as usize {
                        primary_enemy_indices[primary_enemy_cnt] = c_idx as u16;
                        primary_enemy_cnt += 1;
                    } else {
                        primary_enemy_valid = false;
                    }
                }
                if c.is_detached {
                    continue;
                }
                let creature_idx_u16 = c_idx.min(u16::MAX as usize) as u16;
                if c.is_player && ch == player.creature_handle {
                    player_in_roster = true;
                }
                for (p_idx, p) in c.powers.iter().enumerate() {
                    match p.id {
                        PowerId::ThieveryPower | PowerId::SwipePower => {
                            enemy_power_flags |= ENEMY_POWER_FLAG_GOLD_THEFT;
                        }
                        PowerId::ReattachPower | PowerId::AdaptablePower => {
                            enemy_power_flags |= ENEMY_POWER_FLAG_REVIVING;
                        }
                        _ => {}
                    }
                    if !tables.power_has_hook(p.id) {
                        continue;
                    }
                    let pm = mask_at(tables.powers, p.id as usize);
                    powers = powers.union(pm);
                    post_player = post_player.union(pm);
                    Self::push_diff(
                        &mut out.listener_post_player,
                        &mut post_idx,
                        &mut post_changed,
                        (
                            CachedHookListener::Power {
                                creature: ch,
                                power_handle: p.handle,
                                creature_idx: creature_idx_u16,
                                power_idx: p_idx.min(u16::MAX as usize) as u16,
                            },
                            pm,
                        ),
                    );
                }
                if c.is_monster() {
                    if let Some(mid) = c.monster_id {
                        if tables.monster_has_hook(mid) {
                            let mm = mask_at(tables.monsters, mid as usize);
                            monsters = monsters.union(mm);
                            post_player = post_player.union(mm);
                            Self::push_diff(
                                &mut out.listener_post_player,
                                &mut post_idx,
                                &mut post_changed,
                                (CachedHookListener::Monster(ch), mm),
                            );
                        }
                    }
                }
            }
            if pre_idx != out.listener_pre_player.len() {
                out.listener_pre_player.truncate(pre_idx);
                pre_changed = true;
            }
            if post_idx != out.listener_post_player.len() {
                out.listener_post_player.truncate(post_idx);
                post_changed = true;
            }
            out.masks.powers = powers;
            out.masks.monsters = monsters;
            out.masks.pre_player = pre_player;
            out.masks.post_player = post_player;
            out.in_combat_mask = in_combat_mask;
            out.in_combat_overflow = in_combat_overflow;
            out.combat_has_player = player.is_active_for_hooks && player_in_roster;
            out.living_check_player_idx = if living_player_valid && living_player_count == 1 {
                living_player_idx
            } else {
                u16::MAX
            };
            out.primary_enemy_indices = primary_enemy_indices;
            out.primary_enemy_count = if primary_enemy_valid {
                primary_enemy_cnt as u8
            } else {
                u8::MAX
            };
            out.enemy_power_flags = enemy_power_flags;
            if pre_changed || post_changed {
                flat_dirty = true;
            }
            if pre_changed
                || post_changed
                || out.in_combat_mask != prev_in_combat_mask
                || out.in_combat_overflow != prev_in_combat_overflow
                || out.living_check_player_idx != prev_living_idx
                || out.primary_enemy_count != prev_primary_cnt
                || (out.primary_enemy_count != u8::MAX
                    && out.primary_enemy_indices[..out.primary_enemy_count as usize]
                        != prev_primary_indices[..out.primary_enemy_count as usize])
                || out.enemy_power_flags != prev_enemy_power_flags
            {
                body_changed = true;
            }
        } else if !player.is_active_for_hooks {
            out.combat_has_player = false;
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
        let combat_has_player = out.combat_has_player;
        if prev_combat_has_player != combat_has_player {
            flat_dirty = true;
        }
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
        if flat_dirty
            || out.masks.combat_combined != prev_combat_combined
            || out.masks.run_combat_combined != prev_run_combat_combined
        {
            body_changed = true;
        }

        if flat_dirty {
            let player_inv_len = if combat_has_player {
                out.listener_relics.len()
                    + out.listener_potions.len()
                    + out.listener_orbs.len()
                    + out.listener_hand.len()
                    + out.listener_draw.len()
                    + out.listener_discard.len()
                    + out.listener_exhaust.len()
                    + out.listener_play.len()
            } else {
                0
            };
            let total_flat = out.listener_pre_player.len()
                + player_inv_len
                + out.listener_post_player.len()
                + out.listener_run_models.len()
                + usize::from(out.listener_dominion.is_some());
            out.flat_listeners.clear();
            if total_flat > out.flat_listeners.capacity() {
                out.flat_listeners.reserve(total_flat);
            }
            if total_flat <= 64 {
                out.flat_slot_words = 1;
                if !out.flat_masks_w0.is_empty() {
                    out.flat_masks_w0.clear();
                    out.flat_masks_w1.clear();
                    out.flat_masks_w2.clear();
                }
                let slot_masks = out
                    .slot_listener_masks
                    .get_or_insert_with(|| Box::new([0u64; HOOK_SLOT_TABLE_LEN]));
                let [mut cw0, mut cw1, mut cw2] = out.masks.combat_combined.0;
                while cw0 != 0 {
                    let b = cw0.trailing_zeros() as usize;
                    cw0 &= cw0 - 1;
                    debug_assert!(b < HOOK_SLOT_TABLE_LEN);
                    unsafe {
                        *slot_masks.get_unchecked_mut(b) = 0;
                    }
                }
                while cw1 != 0 {
                    let b = cw1.trailing_zeros() as usize;
                    cw1 &= cw1 - 1;
                    let slot_idx = 64 + b;
                    debug_assert!(slot_idx < HOOK_SLOT_TABLE_LEN);
                    unsafe {
                        *slot_masks.get_unchecked_mut(slot_idx) = 0;
                    }
                }
                while cw2 != 0 {
                    let b = cw2.trailing_zeros() as usize;
                    cw2 &= cw2 - 1;
                    let slot_idx = 128 + b;
                    debug_assert!(slot_idx < HOOK_SLOT_TABLE_LEN);
                    unsafe {
                        *slot_masks.get_unchecked_mut(slot_idx) = 0;
                    }
                }
                let dst_ptr = out.flat_listeners.as_mut_ptr();
                let mut flat_len = 0usize;
                #[inline(always)]
                unsafe fn push_flat_slot_listener(
                    dst_ptr: *mut CachedHookListener,
                    flat_len: &mut usize,
                    slot_masks: &mut [u64; HOOK_SLOT_TABLE_LEN],
                    l: CachedHookListener,
                    m: HookMask,
                ) {
                    let idx = *flat_len;
                    unsafe {
                        dst_ptr.add(idx).write(l);
                    }
                    *flat_len = idx + 1;
                    let bit = 1u64 << idx;
                    let [mut w0, mut w1, mut w2] = m.0;
                    while w0 != 0 {
                        let b = w0.trailing_zeros() as usize;
                        w0 &= w0 - 1;
                        debug_assert!(b < HOOK_SLOT_TABLE_LEN);
                        unsafe {
                            *slot_masks.get_unchecked_mut(b) |= bit;
                        }
                    }
                    while w1 != 0 {
                        let b = w1.trailing_zeros() as usize;
                        w1 &= w1 - 1;
                        let slot_idx = 64 + b;
                        debug_assert!(slot_idx < HOOK_SLOT_TABLE_LEN);
                        unsafe {
                            *slot_masks.get_unchecked_mut(slot_idx) |= bit;
                        }
                    }
                    while w2 != 0 {
                        let b = w2.trailing_zeros() as usize;
                        w2 &= w2 - 1;
                        let slot_idx = 128 + b;
                        debug_assert!(slot_idx < HOOK_SLOT_TABLE_LEN);
                        unsafe {
                            *slot_masks.get_unchecked_mut(slot_idx) |= bit;
                        }
                    }
                }
                if !out.listener_pre_player.is_empty() {
                    for &(l, m) in &out.listener_pre_player {
                        unsafe {
                            push_flat_slot_listener(dst_ptr, &mut flat_len, slot_masks, l, m);
                        }
                    }
                }
                if combat_has_player {
                    if !out.listener_relics.is_empty() {
                        for &(idx, m) in &out.listener_relics {
                            unsafe {
                                push_flat_slot_listener(
                                    dst_ptr,
                                    &mut flat_len,
                                    slot_masks,
                                    CachedHookListener::Relic(idx.min(u16::MAX as usize) as u16),
                                    m,
                                );
                            }
                        }
                    }
                    if !out.listener_potions.is_empty() {
                        for &(idx, m) in &out.listener_potions {
                            unsafe {
                                push_flat_slot_listener(
                                    dst_ptr,
                                    &mut flat_len,
                                    slot_masks,
                                    CachedHookListener::Potion(idx.min(u16::MAX as usize) as u16),
                                    m,
                                );
                            }
                        }
                    }
                    if !out.listener_orbs.is_empty() {
                        for &(idx, m) in &out.listener_orbs {
                            unsafe {
                                push_flat_slot_listener(
                                    dst_ptr,
                                    &mut flat_len,
                                    slot_masks,
                                    CachedHookListener::Orb(idx.min(u16::MAX as usize) as u16),
                                    m,
                                );
                            }
                        }
                    }
                    if !out.listener_hand.is_empty() {
                        for &(l, m) in &out.listener_hand {
                            unsafe {
                                push_flat_slot_listener(dst_ptr, &mut flat_len, slot_masks, l, m);
                            }
                        }
                    }
                    if !out.listener_draw.is_empty() {
                        for &(l, m) in &out.listener_draw {
                            unsafe {
                                push_flat_slot_listener(dst_ptr, &mut flat_len, slot_masks, l, m);
                            }
                        }
                    }
                    if !out.listener_discard.is_empty() {
                        for &(l, m) in &out.listener_discard {
                            unsafe {
                                push_flat_slot_listener(dst_ptr, &mut flat_len, slot_masks, l, m);
                            }
                        }
                    }
                    if !out.listener_exhaust.is_empty() {
                        for &(l, m) in &out.listener_exhaust {
                            unsafe {
                                push_flat_slot_listener(dst_ptr, &mut flat_len, slot_masks, l, m);
                            }
                        }
                    }
                    if !out.listener_play.is_empty() {
                        for &(l, m) in &out.listener_play {
                            unsafe {
                                push_flat_slot_listener(dst_ptr, &mut flat_len, slot_masks, l, m);
                            }
                        }
                    }
                }
                if !out.listener_post_player.is_empty() {
                    for &(l, m) in &out.listener_post_player {
                        unsafe {
                            push_flat_slot_listener(dst_ptr, &mut flat_len, slot_masks, l, m);
                        }
                    }
                }
                if !out.listener_run_models.is_empty() {
                    for &(rm, m) in &out.listener_run_models {
                        unsafe {
                            push_flat_slot_listener(
                                dst_ptr,
                                &mut flat_len,
                                slot_masks,
                                CachedHookListener::RunModel(rm),
                                m,
                            );
                        }
                    }
                }
                if let Some((dom, m)) = out.listener_dominion {
                    unsafe {
                        push_flat_slot_listener(
                            dst_ptr,
                            &mut flat_len,
                            slot_masks,
                            CachedHookListener::Dominion(dom),
                            m,
                        );
                    }
                }
                debug_assert_eq!(flat_len, total_flat);
                unsafe {
                    out.flat_listeners.set_len(flat_len);
                }
            } else if total_flat <= 256 {
                let words = ((total_flat + 63) >> 6) as u8;
                out.flat_slot_words = words;
                let ext_words = (words as usize) - 1;
                if !out.flat_masks_w0.is_empty() {
                    out.flat_masks_w0.clear();
                    out.flat_masks_w1.clear();
                    out.flat_masks_w2.clear();
                }
                let slot_masks = out
                    .slot_listener_masks
                    .get_or_insert_with(|| Box::new([0u64; HOOK_SLOT_TABLE_LEN]));
                let slot_masks_ext = out
                    .slot_listener_masks_ext
                    .get_or_insert_with(|| Box::new([[0u64; HOOK_SLOT_TABLE_LEN]; 3]));
                for w in 0..3 {
                    let mut cw = out.masks.combat_combined.0[w];
                    let base = w * 64;
                    while cw != 0 {
                        let b = cw.trailing_zeros() as usize;
                        cw &= cw - 1;
                        let slot_idx = base + b;
                        debug_assert!(slot_idx < HOOK_SLOT_TABLE_LEN);
                        unsafe {
                            *slot_masks.get_unchecked_mut(slot_idx) = 0;
                            for kw in 0..ext_words {
                                *slot_masks_ext
                                    .get_unchecked_mut(kw)
                                    .get_unchecked_mut(slot_idx) = 0;
                            }
                        }
                    }
                }
                let dst_ptr = out.flat_listeners.as_mut_ptr();
                let mut flat_len = 0usize;
                let mut cur_word = 0usize;
                let mut run_mask = HookMask::EMPTY;
                let mut run_bits = 0u64;
                #[inline(always)]
                unsafe fn flush_wide_run(
                    plane: &mut [u64; HOOK_SLOT_TABLE_LEN],
                    m: HookMask,
                    bits: u64,
                ) {
                    let [mut w0, mut w1, mut w2] = m.0;
                    while w0 != 0 {
                        let b = w0.trailing_zeros() as usize;
                        w0 &= w0 - 1;
                        debug_assert!(b < HOOK_SLOT_TABLE_LEN);
                        unsafe {
                            *plane.get_unchecked_mut(b) |= bits;
                        }
                    }
                    while w1 != 0 {
                        let b = w1.trailing_zeros() as usize;
                        w1 &= w1 - 1;
                        let slot_idx = 64 + b;
                        debug_assert!(slot_idx < HOOK_SLOT_TABLE_LEN);
                        unsafe {
                            *plane.get_unchecked_mut(slot_idx) |= bits;
                        }
                    }
                    while w2 != 0 {
                        let b = w2.trailing_zeros() as usize;
                        w2 &= w2 - 1;
                        let slot_idx = 128 + b;
                        debug_assert!(slot_idx < HOOK_SLOT_TABLE_LEN);
                        unsafe {
                            *plane.get_unchecked_mut(slot_idx) |= bits;
                        }
                    }
                }
                #[inline(always)]
                #[allow(clippy::too_many_arguments)]
                unsafe fn push_flat_slot_listener_wide(
                    dst_ptr: *mut CachedHookListener,
                    flat_len: &mut usize,
                    cur_word: &mut usize,
                    run_mask: &mut HookMask,
                    run_bits: &mut u64,
                    slot_masks: &mut [u64; HOOK_SLOT_TABLE_LEN],
                    slot_masks_ext: &mut [[u64; HOOK_SLOT_TABLE_LEN]; 3],
                    l: CachedHookListener,
                    m: HookMask,
                ) {
                    let idx = *flat_len;
                    unsafe {
                        dst_ptr.add(idx).write(l);
                    }
                    *flat_len = idx + 1;
                    let lw = idx >> 6;
                    let bit = 1u64 << (idx & 63);
                    let [m0, m1, m2] = m.0;
                    let [r0, r1, r2] = run_mask.0;
                    if lw == *cur_word && *run_bits != 0 && ((m0 ^ r0) | (m1 ^ r1) | (m2 ^ r2)) == 0
                    {
                        *run_bits |= bit;
                        return;
                    }
                    if *run_bits != 0 {
                        let plane: &mut [u64; HOOK_SLOT_TABLE_LEN] = if *cur_word == 0 {
                            slot_masks
                        } else {
                            unsafe { slot_masks_ext.get_unchecked_mut(*cur_word - 1) }
                        };
                        unsafe {
                            flush_wide_run(plane, *run_mask, *run_bits);
                        }
                    }
                    *cur_word = lw;
                    *run_mask = m;
                    *run_bits = bit;
                }
                if !out.listener_pre_player.is_empty() {
                    for &(l, m) in &out.listener_pre_player {
                        unsafe {
                            push_flat_slot_listener_wide(
                                dst_ptr,
                                &mut flat_len,
                                &mut cur_word,
                                &mut run_mask,
                                &mut run_bits,
                                slot_masks,
                                slot_masks_ext,
                                l,
                                m,
                            );
                        }
                    }
                }
                if combat_has_player {
                    for &(idx, m) in &out.listener_relics {
                        unsafe {
                            push_flat_slot_listener_wide(
                                dst_ptr,
                                &mut flat_len,
                                &mut cur_word,
                                &mut run_mask,
                                &mut run_bits,
                                slot_masks,
                                slot_masks_ext,
                                CachedHookListener::Relic(idx.min(u16::MAX as usize) as u16),
                                m,
                            );
                        }
                    }
                    for &(idx, m) in &out.listener_potions {
                        unsafe {
                            push_flat_slot_listener_wide(
                                dst_ptr,
                                &mut flat_len,
                                &mut cur_word,
                                &mut run_mask,
                                &mut run_bits,
                                slot_masks,
                                slot_masks_ext,
                                CachedHookListener::Potion(idx.min(u16::MAX as usize) as u16),
                                m,
                            );
                        }
                    }
                    for &(idx, m) in &out.listener_orbs {
                        unsafe {
                            push_flat_slot_listener_wide(
                                dst_ptr,
                                &mut flat_len,
                                &mut cur_word,
                                &mut run_mask,
                                &mut run_bits,
                                slot_masks,
                                slot_masks_ext,
                                CachedHookListener::Orb(idx.min(u16::MAX as usize) as u16),
                                m,
                            );
                        }
                    }
                    for &(l, m) in &out.listener_hand {
                        unsafe {
                            push_flat_slot_listener_wide(
                                dst_ptr,
                                &mut flat_len,
                                &mut cur_word,
                                &mut run_mask,
                                &mut run_bits,
                                slot_masks,
                                slot_masks_ext,
                                l,
                                m,
                            );
                        }
                    }
                    for &(l, m) in &out.listener_draw {
                        unsafe {
                            push_flat_slot_listener_wide(
                                dst_ptr,
                                &mut flat_len,
                                &mut cur_word,
                                &mut run_mask,
                                &mut run_bits,
                                slot_masks,
                                slot_masks_ext,
                                l,
                                m,
                            );
                        }
                    }
                    for &(l, m) in &out.listener_discard {
                        unsafe {
                            push_flat_slot_listener_wide(
                                dst_ptr,
                                &mut flat_len,
                                &mut cur_word,
                                &mut run_mask,
                                &mut run_bits,
                                slot_masks,
                                slot_masks_ext,
                                l,
                                m,
                            );
                        }
                    }
                    for &(l, m) in &out.listener_exhaust {
                        unsafe {
                            push_flat_slot_listener_wide(
                                dst_ptr,
                                &mut flat_len,
                                &mut cur_word,
                                &mut run_mask,
                                &mut run_bits,
                                slot_masks,
                                slot_masks_ext,
                                l,
                                m,
                            );
                        }
                    }
                    for &(l, m) in &out.listener_play {
                        unsafe {
                            push_flat_slot_listener_wide(
                                dst_ptr,
                                &mut flat_len,
                                &mut cur_word,
                                &mut run_mask,
                                &mut run_bits,
                                slot_masks,
                                slot_masks_ext,
                                l,
                                m,
                            );
                        }
                    }
                }
                for &(l, m) in &out.listener_post_player {
                    unsafe {
                        push_flat_slot_listener_wide(
                            dst_ptr,
                            &mut flat_len,
                            &mut cur_word,
                            &mut run_mask,
                            &mut run_bits,
                            slot_masks,
                            slot_masks_ext,
                            l,
                            m,
                        );
                    }
                }
                for &(rm, m) in &out.listener_run_models {
                    unsafe {
                        push_flat_slot_listener_wide(
                            dst_ptr,
                            &mut flat_len,
                            &mut cur_word,
                            &mut run_mask,
                            &mut run_bits,
                            slot_masks,
                            slot_masks_ext,
                            CachedHookListener::RunModel(rm),
                            m,
                        );
                    }
                }
                if let Some((dom, m)) = out.listener_dominion {
                    unsafe {
                        push_flat_slot_listener_wide(
                            dst_ptr,
                            &mut flat_len,
                            &mut cur_word,
                            &mut run_mask,
                            &mut run_bits,
                            slot_masks,
                            slot_masks_ext,
                            CachedHookListener::Dominion(dom),
                            m,
                        );
                    }
                }
                if run_bits != 0 {
                    let plane: &mut [u64; HOOK_SLOT_TABLE_LEN] = if cur_word == 0 {
                        slot_masks
                    } else {
                        unsafe { slot_masks_ext.get_unchecked_mut(cur_word - 1) }
                    };
                    unsafe {
                        flush_wide_run(plane, run_mask, run_bits);
                    }
                }
                debug_assert_eq!(flat_len, total_flat);
                unsafe {
                    out.flat_listeners.set_len(flat_len);
                }
            } else {
                out.flat_slot_words = 0;
                out.flat_masks_w0.clear();
                out.flat_masks_w1.clear();
                out.flat_masks_w2.clear();
                if total_flat > out.flat_masks_w0.capacity() {
                    out.flat_masks_w0.reserve(total_flat);
                    out.flat_masks_w1.reserve(total_flat);
                    out.flat_masks_w2.reserve(total_flat);
                }
                let mut push_flat = |l: CachedHookListener, m: HookMask| {
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
                        push_flat(CachedHookListener::Relic(idx.min(u16::MAX as usize) as u16), m);
                    }
                    for &(idx, m) in &out.listener_potions {
                        push_flat(CachedHookListener::Potion(idx.min(u16::MAX as usize) as u16), m);
                    }
                    for &(idx, m) in &out.listener_orbs {
                        push_flat(CachedHookListener::Orb(idx.min(u16::MAX as usize) as u16), m);
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
                    push_flat(CachedHookListener::RunModel(rm), m);
                }
                if let Some((dom, m)) = out.listener_dominion {
                    push_flat(CachedHookListener::Dominion(dom), m);
                }
            }
        }

        if body_changed || out.revision == 0 {
            out.revision = next_hook_cache_revision();
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
    pub fn enter_attacking(&mut self, creature: CreatureHandle) {
        if let Some((_, depth)) = self
            .attacking_creatures
            .iter_mut()
            .find(|(h, _)| *h == creature)
        {
            *depth = depth.saturating_add(1);
        } else {
            self.attacking_creatures.push((creature, 1));
        }
    }

    #[inline]
    pub fn exit_attacking(&mut self, creature: CreatureHandle) {
        if let Some(pos) = self
            .attacking_creatures
            .iter()
            .position(|(h, _)| *h == creature)
        {
            if self.attacking_creatures[pos].1 <= 1 {
                self.attacking_creatures.swap_remove(pos);
            } else {
                self.attacking_creatures[pos].1 -= 1;
            }
        }
    }

    #[inline]
    pub fn is_creature_attacking(&self, creature: CreatureHandle) -> bool {
        self.attacking_creatures
            .iter()
            .any(|(h, depth)| *h == creature && *depth > 0)
    }

    #[inline]
    pub fn mark_deferred_death_cleanup(&mut self, creature: CreatureHandle) {
        if !self.deferred_death_cleanup.contains(&creature) {
            self.deferred_death_cleanup.push(creature);
        }
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
    pub fn card_index(&self, handle: CardHandle) -> Option<usize> {
        let idx = handle.0.wrapping_sub(1) as usize;
        if self.handle_layout.cards {
            if idx < self.cards.len() {
                #[cfg(debug_assertions)]
                debug_assert_eq!(self.cards[idx].handle, handle);
                return Some(idx);
            }
            return None;
        }
        if idx < self.cards.len() && self.cards[idx].handle == handle {
            return Some(idx);
        }
        Self::find_card_idx_cold(&self.cards, handle)
    }

    #[inline(always)]
    pub fn card(&self, handle: CardHandle) -> Option<&CardInst> {
        let idx = handle.0.wrapping_sub(1) as usize;
        if self.handle_layout.cards {
            if idx < self.cards.len() {
                #[cfg(debug_assertions)]
                debug_assert_eq!(self.cards[idx].handle, handle);
                return Some(&self.cards[idx]);
            }
            return None;
        }
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
        let pos = if self.handle_layout.cards {
            if idx < self.cards.len() {
                #[cfg(debug_assertions)]
                debug_assert_eq!(self.cards[idx].handle, handle);
                idx
            } else {
                return None;
            }
        } else if idx < self.cards.len() && self.cards[idx].handle == handle {
            idx
        } else {
            Self::find_card_idx_cold(&self.cards, handle)?
        };
        if pos < self.static_ctx.root_deck_prefix_len || self.cards[pos].pile == PileType::Deck {
            self.deck_dirty.0 = true;
        }
        self.mark_card_meta_dirty(pos);
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
        let idx = if self.handle_layout.cards {
            if fast_idx < self.cards.len() {
                #[cfg(debug_assertions)]
                debug_assert_eq!(self.cards[fast_idx].handle, handle);
                fast_idx
            } else {
                return None;
            }
        } else if fast_idx < self.cards.len() && self.cards[fast_idx].handle == handle {
            fast_idx
        } else {
            self.cards.iter().position(|c| c.handle == handle)?
        };
        if idx < self.static_ctx.root_deck_prefix_len || self.cards[idx].pile == PileType::Deck {
            self.deck_dirty.0 = true;
        }
        self.mark_card_meta_dirty(idx);
        self.eot_card_cleanup_dirty.0 = true;
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
    pub fn creature_index(&self, handle: CreatureHandle) -> Option<usize> {
        let idx = handle.0.wrapping_sub(1) as usize;
        if self.handle_layout.creatures {
            if idx < self.creatures.len() {
                #[cfg(debug_assertions)]
                debug_assert_eq!(self.creatures[idx].handle, handle);
                return Some(idx);
            }
            return None;
        }
        if idx < self.creatures.len() && self.creatures[idx].handle == handle {
            return Some(idx);
        }
        Self::find_creature_idx_cold(&self.creatures, handle)
    }

    #[cold]
    #[inline(never)]
    fn find_creature_idx_cold(
        creatures: &[CreatureInst],
        handle: CreatureHandle,
    ) -> Option<usize> {
        creatures.iter().position(|c| c.handle == handle)
    }

    #[inline(always)]
    pub fn creature(&self, handle: CreatureHandle) -> Option<&CreatureInst> {
        let idx = handle.0.wrapping_sub(1) as usize;
        if self.handle_layout.creatures {
            if idx < self.creatures.len() {
                #[cfg(debug_assertions)]
                debug_assert_eq!(self.creatures[idx].handle, handle);
                return Some(&self.creatures[idx]);
            }
            return None;
        }
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
        if self.handle_layout.creatures {
            if idx < self.creatures.len() {
                #[cfg(debug_assertions)]
                debug_assert_eq!(self.creatures[idx].handle, handle);
                return Some(&mut self.creatures[idx]);
            }
            return None;
        }
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

    #[inline(always)]
    pub fn osty_handle(&self) -> Option<CreatureHandle> {
        if self.pet_handles.is_empty() {
            return None;
        }
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

    #[inline]
    pub fn record_exhaust(&mut self) {
        let tk = self.current_turn_key();
        self.semantic_history.record_exhaust_tk(tk);
    }

    #[inline]
    pub fn record_block_gain(&mut self, props: ValueProp, play_ordinal: Option<i32>) {
        if props.is_powered_block() {
            if let Some(ordinal) = play_ordinal {
                let tk = self.current_turn_key();
                self.semantic_history
                    .record_block_gain_tk(tk, props, Some(ordinal));
            }
        }
    }

    #[inline]
    pub fn record_orb_channel(&mut self, orb_id: OrbId) {
        let tk = self.current_turn_key();
        self.semantic_history.record_orb_channel_tk(tk, orb_id);
    }

    #[inline]
    pub fn record_energy_spent(&mut self, amount: i32) {
        if amount > 0 {
            let tk = self.current_turn_key();
            self.semantic_history.record_energy_spent_tk(tk, amount);
        }
    }

    #[inline]
    pub fn record_card_drawn(&mut self, card_type: CardType, from_hand_draw: bool) {
        let tk = self.current_turn_key();
        self.semantic_history
            .record_card_drawn_tk(tk, card_type, from_hand_draw);
    }

    #[inline]
    pub fn record_actor_event(&mut self, kind: impl Into<Cow<'static, str>>) {
        let tk = self.current_turn_key();
        self.semantic_history.record_actor_event_tk(tk, kind);
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
            let len = self.semantic_history.exhausts.len();
            if self.semantic_history.exhausts[len - 1].0 != cur_tk {
                #[cfg(debug_assertions)]
                debug_assert!(self
                    .semantic_history
                    .exhausts
                    .iter()
                    .all(|(tk, _)| *tk != cur_tk));
                self.semantic_history.prior_exhaust_count += len as i32;
                self.semantic_history.exhausts.clear();
            } else {
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
        }
        if !self.semantic_history.card_block_gains.is_empty() {
            let len = self.semantic_history.card_block_gains.len();
            if self.semantic_history.card_block_gains[len - 1].0 != cur_tk {
                #[cfg(debug_assertions)]
                debug_assert!(self
                    .semantic_history
                    .card_block_gains
                    .iter()
                    .all(|(tk, _, _)| *tk != cur_tk));
                self.semantic_history.card_block_gains.clear();
            } else if self.semantic_history.card_block_gains[0].0 != cur_tk {
                self.semantic_history
                    .card_block_gains
                    .retain(|(tk, _, _)| *tk == cur_tk);
            }
        }
        if !self.semantic_history.energy_spent.is_empty() {
            let len = self.semantic_history.energy_spent.len();
            if self.semantic_history.energy_spent[len - 1].0 != cur_tk {
                #[cfg(debug_assertions)]
                debug_assert!(self
                    .semantic_history
                    .energy_spent
                    .iter()
                    .all(|(tk, _, _)| *tk != cur_tk));
                self.semantic_history.energy_spent.clear();
            } else if self.semantic_history.energy_spent[0].0 != cur_tk {
                self.semantic_history
                    .energy_spent
                    .retain(|(tk, _, _)| *tk == cur_tk);
            }
        }
        if !self.semantic_history.card_drawn.is_empty() {
            let len = self.semantic_history.card_drawn.len();
            if self.semantic_history.card_drawn[len - 1].0 != cur_tk {
                #[cfg(debug_assertions)]
                debug_assert!(self
                    .semantic_history
                    .card_drawn
                    .iter()
                    .all(|(tk, _, _, _)| *tk != cur_tk));
                self.semantic_history.card_drawn.clear();
            } else if self.semantic_history.card_drawn[0].0 != cur_tk {
                self.semantic_history
                    .card_drawn
                    .retain(|(tk, _, _, _)| *tk == cur_tk);
            }
        }
        self.semantic_history.refresh_turn_cache(&cur_tk);
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

    #[inline(always)]
    pub(crate) fn collect_card_hook_entries_at(
        &self,
        tables: &HookMaskTableView,
        card_idx: usize,
        handle: CardHandle,
        out: &mut [(CachedHookListener, HookMask); 3],
    ) -> (usize, HookMask) {
        let card = &self.cards[card_idx];
        let mut n = 0usize;
        let mut union_m = HookMask::EMPTY;
        if tables.card_has_hook(card.id) {
            let card_mask = mask_at(tables.cards, card.id as usize);
            out[n] = (CachedHookListener::Card(handle), card_mask);
            n += 1;
            union_m = union_m.union(card_mask);
        }
        if let Some(ext) = card
            .ext_index
            .and_then(|i| self.card_extensions.get(i as usize))
        {
            if let Some(en) = ext.enchantment.as_ref() {
                let ench_mask = mask_at(tables.enchantments, en.id as usize);
                if !ench_mask.is_empty() {
                    out[n] = (CachedHookListener::Enchantment(handle), ench_mask);
                    n += 1;
                    union_m = union_m.union(ench_mask);
                }
            }
        }
        if !tables.afflictions_self_card_entered_only {
            if let Some(af) = card.affliction {
                let aff_mask = mask_at(tables.afflictions, af.id as usize);
                if !aff_mask.is_empty() {
                    out[n] = (CachedHookListener::Affliction(handle), aff_mask);
                    n += 1;
                    union_m = union_m.union(aff_mask);
                }
            }
        }
        (n, union_m)
    }

    fn try_remove_card_from_pile_hook_cache(
        &mut self,
        pile: PileType,
        handle: CardHandle,
        pos_kind: u8,
    ) -> bool {
        let Some((vec, mask_slot)) = self.hook_cache.combat_pile_parts_mut(pile) else {
            return false;
        };
        match pos_kind {
            0 => {
                // Front
                let mut k = 0usize;
                while k < vec.len() && vec[k].0.card_handle() == Some(handle) {
                    k += 1;
                }
                if k == 0 {
                    return false;
                }
                vec.drain(0..k);
            }
            1 => {
                // Back
                let mut popped = false;
                while vec
                    .last()
                    .is_some_and(|&(l, _)| l.card_handle() == Some(handle))
                {
                    vec.pop();
                    popped = true;
                }
                if !popped {
                    return false;
                }
            }
            2 => {
                // Middle
                let Some(pos) = vec
                    .iter()
                    .position(|&(l, _)| l.card_handle() == Some(handle))
                else {
                    return false;
                };
                let mut end = pos + 1;
                while end < vec.len() && vec[end].0.card_handle() == Some(handle) {
                    end += 1;
                }
                vec.drain(pos..end);
            }
            _ => return false,
        }
        let mut acc = HookMask::EMPTY;
        for &(_, m) in vec.iter() {
            acc = acc.union(m);
        }
        *mask_slot = acc;
        true
    }

    pub fn update_drawn_card_hook_cache(
        &mut self,
        handle: CardHandle,
        card_idx_opt: Option<usize>,
        tables: Option<&HookMaskTableView>,
    ) {
        let (Some(t), Some(idx)) = (tables, card_idx_opt) else {
            self.invalidate_pile_hooks(HOOK_DIRTY_DRAW | HOOK_DIRTY_HAND);
            return;
        };
        if self.cards[idx].removed_from_state {
            return;
        }
        let mut entries = [(CachedHookListener::Card(handle), HookMask::EMPTY); 3];
        let (n, union_m) = self.collect_card_hook_entries_at(t, idx, handle, &mut entries);
        if n == 0 {
            return;
        }
        if self.hook_cache.cached_tables.is_some() {
            if self.hook_cache.dirty_mask == 0
                && self.hook_cache.listener_draw.len() >= n
                && self.hook_cache.listener_draw[..n] == entries[..n]
            {
                self.hook_cache.listener_draw.drain(0..n);
                let mut draw_acc = HookMask::EMPTY;
                for &(_, m) in &self.hook_cache.listener_draw {
                    draw_acc = draw_acc.union(m);
                }
                self.hook_cache.masks.cards_draw = draw_acc;
                self.hook_cache.listener_hand.extend_from_slice(&entries[..n]);
                self.hook_cache.masks.cards_hand =
                    self.hook_cache.masks.cards_hand.union(union_m);
                self.hook_cache.revision = next_hook_cache_revision();
                return;
            }
            if (self.hook_cache.dirty_mask & HOOK_DIRTY_DRAW) == 0 {
                if self.try_remove_card_from_pile_hook_cache(PileType::Draw, handle, 0) {
                    self.hook_cache.dirty_mask |= HOOK_DIRTY_FLAT_ONLY;
                } else {
                    self.hook_cache.dirty_mask |= HOOK_DIRTY_DRAW;
                }
            }
            if (self.hook_cache.dirty_mask & HOOK_DIRTY_HAND) == 0 {
                self.hook_cache.listener_hand.extend_from_slice(&entries[..n]);
                self.hook_cache.masks.cards_hand =
                    self.hook_cache.masks.cards_hand.union(union_m);
                self.hook_cache.dirty_mask |= HOOK_DIRTY_FLAT_ONLY;
            }
            if self.hook_cache.masks_are_valid() {
                self.hook_cache
                    .recompute_player_inv_masks(self.player.is_active_for_hooks);
            }
            self.hook_cache.revision = next_hook_cache_revision();
        } else {
            self.invalidate_pile_hooks(HOOK_DIRTY_DRAW | HOOK_DIRTY_HAND);
        }
    }

    #[inline]
    pub fn remove_card_from_pile_vec_only(&mut self, handle: CardHandle, pile: PileType) -> u8 {
        let vec = match pile {
            PileType::Hand => &mut self.player.hand,
            PileType::Draw => &mut self.player.draw,
            PileType::Discard => &mut self.player.discard,
            PileType::Exhaust => &mut self.player.exhaust,
            PileType::Play => &mut self.player.play,
            PileType::Deck => {
                self.deck_dirty.0 = true;
                &mut self.player.deck
            }
            PileType::None => return 3u8,
        };
        let raw = handle.0;
        let len = vec.len();
        let pos = if len == 0 {
            3u8
        } else if vec[len - 1].0 == raw {
            vec.pop();
            1u8
        } else if len > 1 && vec[0].0 == raw {
            vec.remove(0);
            0u8
        } else if len > 2 {
            let mut found = None;
            let mut i = 1;
            while i + 1 < len {
                if vec[i].0 == raw {
                    found = Some(i);
                    break;
                }
                i += 1;
            }
            if let Some(p) = found {
                vec.remove(p);
                2u8
            } else {
                3u8
            }
        } else {
            3u8
        };
        #[cfg(debug_assertions)]
        debug_assert!(!vec.contains(&handle), "duplicate card handle in pile");
        pos
    }

    #[inline]
    pub fn insert_card_into_pile_vec_only(
        &mut self,
        handle: CardHandle,
        pile: PileType,
        index: Option<usize>,
    ) -> (usize, usize) {
        let vec = match pile {
            PileType::Hand => &mut self.player.hand,
            PileType::Draw => &mut self.player.draw,
            PileType::Discard => &mut self.player.discard,
            PileType::Exhaust => &mut self.player.exhaust,
            PileType::Play => &mut self.player.play,
            PileType::Deck => {
                self.deck_dirty.0 = true;
                &mut self.player.deck
            }
            PileType::None => return (0, 0),
        };
        let old_len = vec.len();
        let idx = match index {
            Some(i) if i <= old_len => i,
            _ => old_len,
        };
        if idx == old_len {
            vec.push(handle);
        } else {
            vec.insert(idx, handle);
        }
        (idx, old_len)
    }

    #[allow(clippy::too_many_arguments)]
    pub fn try_update_hook_cache_for_combat_pile_move(
        &mut self,
        handle: CardHandle,
        card_idx: usize,
        old_pile: PileType,
        rem_pos: u8,
        new_pile: PileType,
        insert_idx: usize,
        new_pile_old_len: usize,
        tables: &HookMaskTableView,
    ) -> bool {
        if self.cards[card_idx].removed_from_state {
            return true;
        }
        let words = self.hook_cache.flat_slot_words as usize;
        if self.hook_cache.dirty_mask != 0
            || self.hook_cache.cached_tables.is_none()
            || !self.hook_cache.combat_has_player
            || words == 0
        {
            return false;
        }
        let mut entries = [(CachedHookListener::Card(handle), HookMask::EMPTY); 3];
        let (n, union_m) = self.collect_card_hook_entries_at(tables, card_idx, handle, &mut entries);
        if n == 0 {
            return true;
        }
        if insert_idx != 0 && insert_idx != new_pile_old_len {
            return false;
        }
        if self.hook_cache.slot_listener_masks.is_none()
            || (words >= 2 && self.hook_cache.slot_listener_masks_ext.is_none())
        {
            return false;
        }
        let Some(old_base) = self.hook_cache.combat_pile_flat_base(old_pile) else {
            return false;
        };
        let Some((src_vec, src_mask_slot)) = self.hook_cache.combat_pile_parts_mut(old_pile) else {
            return false;
        };
        let src_seg_pos = match rem_pos {
            0 => 0usize,
            1 => match src_vec.len().checked_sub(n) {
                Some(p) => p,
                None => return false,
            },
            2 => match src_vec
                .iter()
                .position(|&(l, _)| l.card_handle() == Some(handle))
            {
                Some(p) => p,
                None => return false,
            },
            _ => return false,
        };
        if src_seg_pos + n > src_vec.len() || src_vec[src_seg_pos..src_seg_pos + n] != entries[..n]
        {
            return false;
        }
        let src_flat = old_base + src_seg_pos;
        src_vec.drain(src_seg_pos..src_seg_pos + n);
        if old_pile != new_pile {
            let mut acc = HookMask::EMPTY;
            for &(_, m) in src_vec.iter() {
                acc = acc.union(m);
            }
            *src_mask_slot = acc;
        }

        let Some(new_base) = self.hook_cache.combat_pile_flat_base(new_pile) else {
            return false;
        };
        let Some((dst_vec, dst_mask_slot)) = self.hook_cache.combat_pile_parts_mut(new_pile) else {
            return false;
        };
        let dst_seg_pos = if insert_idx == 0 { 0 } else { dst_vec.len() };
        let dst_flat_after = new_base + dst_seg_pos;
        if dst_seg_pos == dst_vec.len() {
            dst_vec.extend_from_slice(&entries[..n]);
        } else {
            dst_vec.splice(0..0, entries[..n].iter().copied());
        }
        if old_pile != new_pile {
            *dst_mask_slot = dst_mask_slot.union(union_m);
        }

        let cache = &mut self.hook_cache;
        if dst_flat_after != src_flat {
            let (lo, hi, shift, move_right, moved_lo, moved_hi, shifted_lo, shifted_hi) =
                if dst_flat_after > src_flat {
                    let lo = src_flat;
                    let hi = dst_flat_after + n;
                    cache.flat_listeners[lo..hi].rotate_left(n);
                    (
                        lo,
                        hi,
                        dst_flat_after - src_flat,
                        true,
                        src_flat,
                        src_flat + n,
                        src_flat + n,
                        hi,
                    )
                } else {
                    let lo = dst_flat_after;
                    let hi = src_flat + n;
                    cache.flat_listeners[lo..hi].rotate_right(n);
                    (
                        lo,
                        hi,
                        src_flat - dst_flat_after,
                        false,
                        src_flat,
                        src_flat + n,
                        dst_flat_after,
                        src_flat,
                    )
                };
            let comb_piles = cache
                .masks
                .cards_hand
                .union(cache.masks.cards_draw)
                .union(cache.masks.cards_discard)
                .union(cache.masks.cards_exhaust)
                .union(cache.masks.cards_play);
            let slot_masks = cache.slot_listener_masks.as_deref_mut().expect("checked");
            if words == 1 {
                let range_mask = mask_range_64(lo, hi);
                let moved_mask = mask_range_64(moved_lo, moved_hi);
                let shifted_mask = mask_range_64(shifted_lo, shifted_hi);
                for w in 0..3 {
                    let mut cw = comb_piles.0[w];
                    let base = w * 64;
                    while cw != 0 {
                        let s = base + cw.trailing_zeros() as usize;
                        cw &= cw - 1;
                        debug_assert!(s < HOOK_SLOT_TABLE_LEN);
                        unsafe {
                            let slot = slot_masks.get_unchecked_mut(s);
                            let m = *slot;
                            if (m & range_mask) != 0 {
                                let moved_part = if move_right {
                                    (m & moved_mask) << shift
                                } else {
                                    (m & moved_mask) >> shift
                                };
                                let shifted_part = if move_right {
                                    (m & shifted_mask) >> n
                                } else {
                                    (m & shifted_mask) << n
                                };
                                *slot = (m & !range_mask) | moved_part | shifted_part;
                            }
                        }
                    }
                }
            } else {
                let range_mask = mask_range_256(lo, hi);
                let moved_mask = mask_range_256(moved_lo, moved_hi);
                let shifted_mask = mask_range_256(shifted_lo, shifted_hi);
                let slot_masks_ext = cache.slot_listener_masks_ext.as_deref_mut().expect("checked");
                for w in 0..3 {
                    let mut cw = comb_piles.0[w];
                    let base = w * 64;
                    while cw != 0 {
                        let s = base + cw.trailing_zeros() as usize;
                        cw &= cw - 1;
                        debug_assert!(s < HOOK_SLOT_TABLE_LEN);
                        let mut m = [0u64; 4];
                        unsafe {
                            m[0] = *slot_masks.get_unchecked(s);
                            for kw in 1..words {
                                m[kw] = *slot_masks_ext.get_unchecked(kw - 1).get_unchecked(s);
                            }
                            let any_in_range = (m[0] & range_mask[0])
                                | (m[1] & range_mask[1])
                                | (m[2] & range_mask[2])
                                | (m[3] & range_mask[3]);
                            if any_in_range != 0 {
                                let out_m = rotate_bitmask_words(
                                    m,
                                    words,
                                    range_mask,
                                    moved_mask,
                                    shifted_mask,
                                    n,
                                    shift,
                                    move_right,
                                );
                                *slot_masks.get_unchecked_mut(s) = out_m[0];
                                for kw in 1..words {
                                    *slot_masks_ext.get_unchecked_mut(kw - 1).get_unchecked_mut(s) =
                                        out_m[kw];
                                }
                            }
                        }
                    }
                }
            }
        }
        cache.revision = next_hook_cache_revision();
        true
    }

    pub fn remove_card_from_current_pile(&mut self, handle: CardHandle) -> PileType {
        self.remove_card_from_current_pile_with_tables(handle, None)
    }

    pub fn remove_card_from_current_pile_with_tables(
        &mut self,
        handle: CardHandle,
        tables: Option<&HookMaskTableView>,
    ) -> PileType {
        let Some(card_idx) = self.card_index(handle) else {
            return PileType::None;
        };
        let tables = tables.or(self.hook_cache.cached_tables);
        let old = self.cards[card_idx].pile;
        let rem_pos = self.remove_card_from_pile_vec_only(handle, old);
        if old != PileType::None {
            let pile_bit = pile_dirty_bit(old);
            if old == PileType::Deck {
                if tables.is_none_or(|t| {
                    self.card_inst_has_any_hook_in_pile(t, &self.cards[card_idx], true)
                }) {
                    self.invalidate_pile_hooks(pile_bit);
                }
            } else if let Some(t) = tables {
                let card_ref = &self.cards[card_idx];
                if !card_ref.removed_from_state
                    && self.card_inst_has_any_hook_in_pile(t, card_ref, false)
                {
                    if (self.hook_cache.dirty_mask & pile_bit) == 0
                        && self.hook_cache.cached_tables.is_some()
                        && self.try_remove_card_from_pile_hook_cache(old, handle, rem_pos)
                    {
                        self.hook_cache.dirty_mask |= HOOK_DIRTY_FLAT_ONLY;
                        if self.hook_cache.masks_are_valid() {
                            self.hook_cache
                                .recompute_player_inv_masks(self.player.is_active_for_hooks);
                        }
                        self.hook_cache.revision = next_hook_cache_revision();
                    } else {
                        self.invalidate_pile_hooks(pile_bit);
                    }
                }
            } else {
                self.invalidate_pile_hooks(pile_bit);
            }
        }
        if card_idx < self.static_ctx.root_deck_prefix_len || old == PileType::Deck {
            self.deck_dirty.0 = true;
        }
        self.cards[card_idx].pile = PileType::None;
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
        let card_idx_opt = self.card_index(handle);
        let tables = tables.or(self.hook_cache.cached_tables);
        let combat_insert_pos = if matches!(
            pile,
            PileType::Hand | PileType::Draw | PileType::Discard | PileType::Exhaust | PileType::Play
        ) {
            Some(self.insert_card_into_pile_vec_only(handle, pile, index))
        } else {
            let _ = self.insert_card_into_pile_vec_only(handle, pile, index);
            None
        };
        if pile != PileType::None {
            let pile_bit = pile_dirty_bit(pile);
            if pile == PileType::Deck {
                let needs_inval = match (tables, card_idx_opt) {
                    (Some(t), Some(idx)) => {
                        self.card_inst_has_any_hook_in_pile(t, &self.cards[idx], true)
                    }
                    _ => true,
                };
                if needs_inval {
                    self.invalidate_pile_hooks(pile_bit);
                }
            } else if let (Some(t), Some(idx)) = (tables, card_idx_opt) {
                if !self.cards[idx].removed_from_state {
                    let mut entries = [(CachedHookListener::Card(handle), HookMask::EMPTY); 3];
                    let (n, union_m) =
                        self.collect_card_hook_entries_at(t, idx, handle, &mut entries);
                    if n > 0 {
                        if let Some((insert_idx, old_len)) = combat_insert_pos {
                            if (self.hook_cache.dirty_mask & pile_bit) == 0
                                && self.hook_cache.cached_tables.is_some()
                                && (insert_idx == old_len || insert_idx == 0)
                            {
                                if let Some((vec, mask_slot)) =
                                    self.hook_cache.combat_pile_parts_mut(pile)
                                {
                                    if insert_idx == old_len {
                                        vec.extend_from_slice(&entries[..n]);
                                    } else {
                                        vec.splice(0..0, entries[..n].iter().copied());
                                    }
                                    *mask_slot = mask_slot.union(union_m);
                                    self.hook_cache.dirty_mask |= HOOK_DIRTY_FLAT_ONLY;
                                    if self.hook_cache.masks_are_valid() {
                                        self.hook_cache
                                            .recompute_player_inv_masks(self.player.is_active_for_hooks);
                                    }
                                    self.hook_cache.revision = next_hook_cache_revision();
                                } else {
                                    self.invalidate_pile_hooks(pile_bit);
                                }
                            } else {
                                self.invalidate_pile_hooks(pile_bit);
                            }
                        } else {
                            self.invalidate_pile_hooks(pile_bit);
                        }
                    }
                }
            } else {
                self.invalidate_pile_hooks(pile_bit);
            }
        }
        if let Some(card_idx) = card_idx_opt {
            if card_idx < self.static_ctx.root_deck_prefix_len || pile == PileType::Deck {
                self.deck_dirty.0 = true;
            }
            self.cards[card_idx].pile = pile;
        }
    }
}

#[inline(always)]
fn mask_range_64(lo: usize, hi: usize) -> u64 {
    if lo >= hi {
        0
    } else {
        let a = if lo >= 64 { u64::MAX } else { (1u64 << lo) - 1 };
        let b = if hi >= 64 { u64::MAX } else { (1u64 << hi) - 1 };
        b & !a
    }
}

#[inline(always)]
fn mask_range_256(lo: usize, hi: usize) -> [u64; 4] {
    if lo >= hi {
        return [0u64; 4];
    }
    let below = |idx: usize| -> [u64; 4] {
        let mut m = [0u64; 4];
        let full_words = idx >> 6;
        let rem = idx & 63;
        let mut w = 0usize;
        while w < full_words && w < 4 {
            m[w] = u64::MAX;
            w += 1;
        }
        if rem != 0 && full_words < 4 {
            m[full_words] = (1u64 << rem) - 1;
        }
        m
    };
    let a = below(lo);
    let b = below(hi);
    [b[0] & !a[0], b[1] & !a[1], b[2] & !a[2], b[3] & !a[3]]
}

#[inline(always)]
fn shl_256(v: [u64; 4], shift: usize, words: usize) -> [u64; 4] {
    if shift == 0 {
        return v;
    }
    let word_shift = shift >> 6;
    let bit_shift = shift & 63;
    let mut out = [0u64; 4];
    if bit_shift == 0 {
        let mut w = word_shift;
        while w < words {
            out[w] = v[w - word_shift];
            w += 1;
        }
    } else {
        let inv = 64 - bit_shift;
        let mut w = word_shift;
        while w < words {
            let mut val = v[w - word_shift] << bit_shift;
            if w > word_shift {
                val |= v[w - word_shift - 1] >> inv;
            }
            out[w] = val;
            w += 1;
        }
    }
    out
}

#[inline(always)]
fn shr_256(v: [u64; 4], shift: usize, words: usize) -> [u64; 4] {
    if shift == 0 {
        return v;
    }
    let word_shift = shift >> 6;
    let bit_shift = shift & 63;
    let mut out = [0u64; 4];
    if bit_shift == 0 {
        let mut w = 0usize;
        while w + word_shift < words {
            out[w] = v[w + word_shift];
            w += 1;
        }
    } else {
        let inv = 64 - bit_shift;
        let mut w = 0usize;
        while w + word_shift < words {
            let mut val = v[w + word_shift] >> bit_shift;
            if w + word_shift + 1 < words {
                val |= v[w + word_shift + 1] << inv;
            }
            out[w] = val;
            w += 1;
        }
    }
    out
}

#[inline(always)]
#[allow(clippy::too_many_arguments)]
fn rotate_bitmask_words(
    m: [u64; 4],
    words: usize,
    range_mask: [u64; 4],
    moved_mask: [u64; 4],
    shifted_mask: [u64; 4],
    n: usize,
    shift: usize,
    move_right: bool,
) -> [u64; 4] {
    let outside = [
        m[0] & !range_mask[0],
        m[1] & !range_mask[1],
        m[2] & !range_mask[2],
        m[3] & !range_mask[3],
    ];
    let moved_in = [
        m[0] & moved_mask[0],
        m[1] & moved_mask[1],
        m[2] & moved_mask[2],
        m[3] & moved_mask[3],
    ];
    let shifted_in = [
        m[0] & shifted_mask[0],
        m[1] & shifted_mask[1],
        m[2] & shifted_mask[2],
        m[3] & shifted_mask[3],
    ];
    let (a, b) = if move_right {
        (shl_256(moved_in, shift, words), shr_256(shifted_in, n, words))
    } else {
        (shr_256(moved_in, shift, words), shl_256(shifted_in, n, words))
    };
    [
        outside[0] | a[0] | b[0],
        outside[1] | a[1] | b[1],
        outside[2] | a[2] | b[2],
        outside[3] | a[3] | b[3],
    ]
}

#[cfg(test)]
mod tests {
    use super::{mask_range_256, rotate_bitmask_words};
    use sts2_types::{PowerId, PowerStaticFlags, generated_power_meta};

    #[test]
    fn rotate_bitmask_words_matches_vec_rotation() {
        for total in [30usize, 64, 65, 128, 130, 192, 195, 256] {
            let words = (total + 63) >> 6;
            let orig: Vec<usize> = (0..total).filter(|i| (i * 7 + 3) % 5 < 2).collect();
            let mut m = [0u64; 4];
            let mut vec_bits = vec![false; total];
            for &i in &orig {
                m[i >> 6] |= 1u64 << (i & 63);
                vec_bits[i] = true;
            }
            let points = [
                0usize,
                1,
                2,
                31,
                61,
                62,
                63,
                64,
                65,
                126,
                127,
                128,
                129,
                191,
                192,
                total.saturating_sub(3),
                total.saturating_sub(1),
                total,
            ];
            for n in [1usize, 2, 3] {
                for &src in &points {
                    if src + n > total {
                        continue;
                    }
                    for &dst in &points {
                        if dst + n > total || dst == src {
                            continue;
                        }
                        let mut expected = vec_bits.clone();
                        let (lo, hi, shift, move_right, moved_lo, moved_hi, shifted_lo, shifted_hi) =
                            if dst > src {
                                let lo = src;
                                let hi = dst + n;
                                expected[lo..hi].rotate_left(n);
                                (lo, hi, dst - src, true, src, src + n, src + n, hi)
                            } else {
                                let lo = dst;
                                let hi = src + n;
                                expected[lo..hi].rotate_right(n);
                                (lo, hi, src - dst, false, src, src + n, dst, src)
                            };
                        let got = rotate_bitmask_words(
                            m,
                            words,
                            mask_range_256(lo, hi),
                            mask_range_256(moved_lo, moved_hi),
                            mask_range_256(shifted_lo, shifted_hi),
                            n,
                            shift,
                            move_right,
                        );
                        let mut exp_words = [0u64; 4];
                        for (i, &b) in expected.iter().enumerate() {
                            if b {
                                exp_words[i >> 6] |= 1u64 << (i & 63);
                            }
                        }
                        assert_eq!(
                            got[..words],
                            exp_words[..words],
                            "mismatch total={total} words={words} n={n} src={src} dst={dst}"
                        );
                    }
                }
            }
        }
    }

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

    #[test]
    fn opt_i16_opt_card_handle_and_inline_opt_vec_layout() {
        use super::{CardHandle, CardInst, InlineOptI32Vec4, OptCardHandle, OptI16, TurnKey};
        assert_eq!(core::mem::size_of::<CardInst>(), 64);
        assert_eq!(core::mem::size_of::<InlineOptI32Vec4>(), 20);
        assert!(core::mem::size_of::<TurnKey>() <= 28);

        assert_eq!(OptI16::NONE.get(), None);
        assert_eq!(OptI16::from_option(None).get(), None);
        for v in [-32767, -128, -1, 0, 1, 42, 32767] {
            let enc = OptI16::some(v);
            assert!(enc.is_some());
            assert_eq!(enc.get(), Some(v));
        }
        assert_eq!(OptI16::from_option(Some(-32768)).get(), Some(-32767));
        assert_eq!(OptI16::from_option(Some(40000)).get(), Some(32767));

        assert_eq!(OptCardHandle::NONE.get(), None);
        assert_eq!(OptCardHandle::some(CardHandle(7)).get(), Some(CardHandle(7)));

        let mut v1 = InlineOptI32Vec4::new();
        assert_eq!(v1.len(), 0);
        v1.push(Some(3));
        v1.push(None);
        v1.push(Some(-5));
        assert_eq!(v1.len(), 3);
        assert_eq!(v1.get(0), Some(Some(3)));
        assert_eq!(v1.get(1), Some(None));
        assert_eq!(v1.get(2), Some(Some(-5)));
        assert_eq!(v1.get(3), None);
        let v2 = InlineOptI32Vec4::from_slice(&[Some(3), None, Some(-5)]);
        assert_eq!(v1, v2);
        let collected: Vec<Option<i32>> = v1.iter().collect();
        assert_eq!(collected, vec![Some(3), None, Some(-5)]);
        assert!(InlineOptI32Vec4::one(Some(9)).is_single_some(9));
        assert!(!InlineOptI32Vec4::one(None).is_single_some(0));
        v1.clear();
        assert_eq!(v1, InlineOptI32Vec4::new());
    }
}
