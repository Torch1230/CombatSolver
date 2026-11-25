use crate::hash::WordHasher128;
use smallvec::SmallVec;
use std::borrow::Cow;
use std::sync::Arc;
use sts2_kernel::content_dispatch::HookListener;
use sts2_kernel::continuation::{
    CardPlayResources, CardSelectActionKind, ChoiceRequest, ChoiceTiming, ContinuationFrame,
    PendingContinuation,
};
use sts2_kernel::state::{
    intern_str, sym_str, AfflictionInst, ArrowSlotInst, BattleState, CardExtension,
    CardHandle, CardInst, CardTempFlags, CreatureHandle, CreatureInst, DamageHistoryEntry,
    DeckDirtyFlag, EnchantmentInst, FieldMap, FieldValue, GrowthCounters, InlineI32Vec4,
    InlineOptI32Vec4, LocalEnergyModifier, MonsterInst, OptCardHandle, OptI16, OrbInst,
    PlayerCounters, PlayerInst, PowerExtension, PowerInst, RelicDirtyMask, RelicInst,
    RootStaticContext, SemanticHistory, TurnKey, HOOK_DIRTY_ALL, HOOK_DIRTY_DECK,
    HOOK_DIRTY_DISCARD, HOOK_DIRTY_DRAW, HOOK_DIRTY_EXHAUST, HOOK_DIRTY_HAND, HOOK_DIRTY_ORBS,
    HOOK_DIRTY_PLAY, HOOK_DIRTY_POTIONS, HOOK_DIRTY_RELICS, HOOK_DIRTY_STATIC, SYM_FALLBACK_ID,
};
use sts2_kernel::KernelError;
use sts2_types::{
    AfflictionId, ArrowStatus, CardId, CardKeywords, CardPilePosition, CardType, CombatSide, Dec,
    DominionId, EnchantmentId, EnchantmentStatus, LocalEnergyModifierKind, MegaRandom, MonsterId,
    OrbId, PileType, PlayerRngSet, PlayerTurnPhase, PotionId, PowerId, RelicId, Rng, RunModelId,
    RunRngSet, ValueProp,
};

struct ByteWriter<'a> {
    buf: &'a mut Vec<u8>,
    cur: *mut u8,
    end: *mut u8,
}

impl Drop for ByteWriter<'_> {
    #[inline]
    fn drop(&mut self) {
        let len = (self.cur as usize) - (self.buf.as_ptr() as usize);
        // SAFETY: `cur` never exceeds `end == buf.as_ptr().add(buf.capacity())`.
        unsafe {
            self.buf.set_len(len);
        }
    }
}

impl<'a> ByteWriter<'a> {
    #[inline]
    fn new(buf: &'a mut Vec<u8>) -> Self {
        let len = buf.len();
        if buf.capacity() - len < 1024 {
            buf.reserve(1024);
        }
        // SAFETY: `buf.capacity() >= len + 1024`.
        unsafe {
            let base = buf.as_mut_ptr();
            Self {
                cur: base.add(len),
                end: base.add(buf.capacity()),
                buf,
            }
        }
    }

    #[cold]
    #[inline(never)]
    fn grow(&mut self, additional: usize) {
        let written = (self.cur as usize) - (self.buf.as_ptr() as usize);
        unsafe {
            self.buf.set_len(written);
        }
        self.buf.reserve(additional.max(512));
        unsafe {
            let base = self.buf.as_mut_ptr();
            self.cur = base.add(written);
            self.end = base.add(self.buf.capacity());
        }
    }

    #[inline(always)]
    fn ensure(&mut self, additional: usize) {
        if (self.end as usize) - (self.cur as usize) < additional {
            self.grow(additional);
        }
    }

    #[inline(always)]
    unsafe fn u8_unchecked(&mut self, v: u8) {
        unsafe {
            *self.cur = v;
            self.cur = self.cur.add(1);
        }
    }

    #[inline(always)]
    unsafe fn u16_unchecked(&mut self, v: u16) {
        unsafe {
            std::ptr::write_unaligned(self.cur.cast::<u16>(), v.to_le());
            self.cur = self.cur.add(2);
        }
    }

    #[inline(always)]
    unsafe fn u32_unchecked(&mut self, v: u32) {
        unsafe {
            std::ptr::write_unaligned(self.cur.cast::<u32>(), v.to_le());
            self.cur = self.cur.add(4);
        }
    }

    #[inline(always)]
    unsafe fn i32_unchecked(&mut self, v: i32) {
        unsafe {
            self.u32_unchecked(v as u32);
        }
    }

    #[inline(always)]
    unsafe fn u64_unchecked(&mut self, v: u64) {
        unsafe {
            std::ptr::write_unaligned(self.cur.cast::<u64>(), v.to_le());
            self.cur = self.cur.add(8);
        }
    }

    #[inline(always)]
    unsafe fn bytes_unchecked(&mut self, bytes: &[u8]) {
        let n = bytes.len();
        unsafe {
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), self.cur, n);
            self.cur = self.cur.add(n);
        }
    }

    #[inline(always)]
    unsafe fn opt_i32_unchecked(&mut self, v: Option<i32>) {
        match v {
            Some(x) => unsafe {
                self.u8_unchecked(1);
                self.i32_unchecked(x);
            },
            None => unsafe {
                self.u8_unchecked(0);
            },
        }
    }

    #[inline(always)]
    unsafe fn opt_u32_unchecked(&mut self, v: Option<u32>) {
        match v {
            Some(x) => unsafe {
                self.u8_unchecked(1);
                self.u32_unchecked(x);
            },
            None => unsafe {
                self.u8_unchecked(0);
            },
        }
    }

    #[cold]
    #[inline(never)]
    unsafe fn var_u32_unchecked_slow(&mut self, mut v: u32) {
        unsafe {
            while v >= 0x80 {
                *self.cur = (v as u8) | 0x80;
                self.cur = self.cur.add(1);
                v >>= 7;
            }
            *self.cur = v as u8;
            self.cur = self.cur.add(1);
        }
    }

    #[inline(always)]
    unsafe fn var_u32_unchecked(&mut self, v: u32) {
        unsafe {
            if v < 0x80 {
                *self.cur = v as u8;
                self.cur = self.cur.add(1);
            } else {
                self.var_u32_unchecked_slow(v);
            }
        }
    }

    #[inline(always)]
    unsafe fn var_i32_unchecked(&mut self, v: i32) {
        let zz = (v as u32).wrapping_shl(1) ^ ((v >> 31) as u32);
        unsafe {
            self.var_u32_unchecked(zz);
        }
    }

    #[inline(always)]
    fn write_bytes(&mut self, bytes: &[u8]) {
        self.ensure(bytes.len());
        // SAFETY: `ensure(bytes.len())` guarantees capacity and `bytes` is disjoint.
        unsafe {
            self.bytes_unchecked(bytes);
        }
    }

    #[inline(always)]
    fn u8(&mut self, v: u8) {
        self.ensure(1);
        unsafe {
            self.u8_unchecked(v);
        }
    }

    #[inline(always)]
    fn bool(&mut self, v: bool) {
        self.u8(u8::from(v));
    }

    #[inline(always)]
    fn u16(&mut self, v: u16) {
        self.ensure(2);
        unsafe {
            self.u16_unchecked(v);
        }
    }

    #[inline(always)]
    fn u32(&mut self, v: u32) {
        self.ensure(4);
        unsafe {
            self.u32_unchecked(v);
        }
    }

    #[inline(always)]
    fn i32(&mut self, v: i32) {
        self.ensure(4);
        unsafe {
            self.i32_unchecked(v);
        }
    }

    #[inline(always)]
    fn u64(&mut self, v: u64) {
        self.ensure(8);
        unsafe {
            self.u64_unchecked(v);
        }
    }

    #[inline(always)]
    fn var_u32(&mut self, v: u32) {
        if v < 0x80 {
            self.u8(v as u8);
        } else {
            self.var_u32_slow(v);
        }
    }

    #[cold]
    #[inline(never)]
    fn var_u32_slow(&mut self, v: u32) {
        self.ensure(5);
        unsafe {
            self.var_u32_unchecked(v);
        }
    }

    #[inline]
    fn dec(&mut self, v: Dec) {
        let d = v.as_decimal();
        let m = d.mantissa();
        if let Ok(m64) = i64::try_from(m) {
            self.ensure(13);
            unsafe {
                self.u8_unchecked(0);
                self.u64_unchecked(m64 as u64);
                self.u32_unchecked(d.scale());
            }
        } else {
            self.u8(1);
            self.str(&v.to_string());
        }
    }

    #[inline]
    fn str(&mut self, s: &str) {
        self.var_u32(s.len() as u32);
        self.write_bytes(s.as_bytes());
    }

    #[inline]
    fn opt_str(&mut self, s: Option<&str>) {
        match s {
            Some(v) => {
                self.u8(1);
                self.str(v);
            }
            None => self.u8(0),
        }
    }

    #[inline(always)]
    fn opt_i32(&mut self, v: Option<i32>) {
        match v {
            Some(x) => {
                self.ensure(5);
                unsafe {
                    self.u8_unchecked(1);
                    self.i32_unchecked(x);
                }
            }
            None => self.u8(0),
        }
    }

    #[inline(always)]
    fn opt_u32(&mut self, v: Option<u32>) {
        match v {
            Some(x) => {
                self.ensure(5);
                unsafe {
                    self.u8_unchecked(1);
                    self.u32_unchecked(x);
                }
            }
            None => self.u8(0),
        }
    }

    #[inline]
    fn sym_str(&mut self, s: &str) {
        let id = intern_str(s);
        self.u16(id);
        if id == SYM_FALLBACK_ID {
            self.str(s);
        }
    }

    #[cold]
    #[inline(never)]
    fn cow_str_slow_with_tail(&mut self, s: &Cow<'static, str>, tail_reserve: usize) {
        match s {
            Cow::Borrowed(st) => {
                let ptr = st.as_ptr() as usize;
                self.u8(0x80);
                #[cfg(target_pointer_width = "64")]
                self.u64(ptr as u64);
                #[cfg(not(target_pointer_width = "64"))]
                self.write_bytes(&ptr.to_le_bytes());
                self.var_u32(st.len() as u32);
            }
            Cow::Owned(st) => {
                self.u8(0x81);
                self.sym_str(st);
            }
        }
        if tail_reserve > 0 {
            self.ensure(tail_reserve);
        }
    }

    #[inline(always)]
    unsafe fn cow_str_unchecked_with_tail(&mut self, s: &Cow<'static, str>, tail_reserve: usize) {
        if let Cow::Borrowed(st) = s {
            let len = st.len();
            if len < 0x80 {
                let ptr = st.as_ptr() as usize;
                unsafe {
                    self.u8_unchecked(len as u8);
                    #[cfg(target_pointer_width = "64")]
                    self.u64_unchecked(ptr as u64);
                    #[cfg(not(target_pointer_width = "64"))]
                    self.bytes_unchecked(&ptr.to_le_bytes());
                }
                return;
            }
        }
        self.cow_str_slow_with_tail(s, tail_reserve);
    }

    #[inline(always)]
    fn cow_str(&mut self, s: &Cow<'static, str>) {
        if let Cow::Borrowed(st) = s {
            let len = st.len();
            if len < 0x80 {
                const PTR_BYTES: usize = std::mem::size_of::<usize>();
                let ptr = st.as_ptr() as usize;
                self.ensure(1 + PTR_BYTES);
                unsafe {
                    self.u8_unchecked(len as u8);
                    #[cfg(target_pointer_width = "64")]
                    self.u64_unchecked(ptr as u64);
                    #[cfg(not(target_pointer_width = "64"))]
                    self.bytes_unchecked(&ptr.to_le_bytes());
                }
                return;
            }
        }
        self.cow_str_slow_with_tail(s, 0);
    }

    #[cold]
    #[inline(never)]
    fn opt_cow_str_slow_with_tail(&mut self, s: Option<&Cow<'static, str>>, tail_reserve: usize) {
        match s {
            None => self.u8(0),
            Some(Cow::Borrowed(st)) => {
                let ptr = st.as_ptr() as usize;
                self.u8(0x80);
                #[cfg(target_pointer_width = "64")]
                self.u64(ptr as u64);
                #[cfg(not(target_pointer_width = "64"))]
                self.write_bytes(&ptr.to_le_bytes());
                self.var_u32(st.len() as u32);
            }
            Some(Cow::Owned(st)) => {
                self.u8(0x81);
                self.sym_str(st);
            }
        }
        if tail_reserve > 0 {
            self.ensure(tail_reserve);
        }
    }

    #[inline(always)]
    unsafe fn opt_cow_str_unchecked_with_tail(
        &mut self,
        s: Option<&Cow<'static, str>>,
        tail_reserve: usize,
    ) {
        match s {
            None => unsafe {
                self.u8_unchecked(0);
            },
            Some(Cow::Borrowed(st)) if st.len() < 0x7F => {
                let len = st.len();
                let ptr = st.as_ptr() as usize;
                unsafe {
                    self.u8_unchecked((len as u8) + 1);
                    #[cfg(target_pointer_width = "64")]
                    self.u64_unchecked(ptr as u64);
                    #[cfg(not(target_pointer_width = "64"))]
                    self.bytes_unchecked(&ptr.to_le_bytes());
                }
            }
            other => self.opt_cow_str_slow_with_tail(other, tail_reserve),
        }
    }

    #[inline(always)]
    fn rng(&mut self, r: &Rng) {
        self.ensure(36);
        unsafe {
            self.rng_unchecked(r);
        }
    }

    #[inline(always)]
    unsafe fn rng_unchecked(&mut self, r: &Rng) {
        unsafe {
            self.i32_unchecked(r.counter);
            self.u64_unchecked(r.random.s0);
            self.u64_unchecked(r.random.s1);
            self.u64_unchecked(r.random.s2);
            self.u64_unchecked(r.random.s3);
        }
    }

    #[cold]
    #[inline(never)]
    fn dec_overflow_mantissa_and_ensure(&mut self, v: Dec, tail_reserve: usize) {
        self.u8(1);
        self.str(&v.to_string());
        if tail_reserve > 0 {
            self.ensure(tail_reserve);
        }
    }

    #[inline(always)]
    unsafe fn dec_unchecked_with_tail(&mut self, v: Dec, tail_reserve: usize) {
        let d = v.as_decimal();
        if let Ok(m64) = i64::try_from(d.mantissa()) {
            unsafe {
                self.u8_unchecked(0);
                self.u64_unchecked(m64 as u64);
                self.u32_unchecked(d.scale());
            }
        } else {
            self.dec_overflow_mantissa_and_ensure(v, tail_reserve);
        }
    }

    fn field_map(&mut self, map: &FieldMap) {
        self.var_u32(map.len() as u32);
        for (k, v) in map {
            self.sym_str(k);
            self.field_value(v);
        }
    }

    #[inline(always)]
    fn cow_field_map(&mut self, map: &FieldMap) {
        if map.is_empty() {
            self.u8(0);
        } else {
            self.cow_field_map_nonempty(map);
        }
    }

    #[inline(never)]
    fn cow_field_map_nonempty(&mut self, map: &FieldMap) {
        self.var_u32(map.len() as u32);
        for (k, v) in map.iter_raw() {
            self.cow_str(k);
            self.field_value(v);
        }
    }

    fn field_value(&mut self, v: &FieldValue) {
        match v {
            FieldValue::Int(x) => {
                self.u8(0);
                self.i32(*x);
            }
            FieldValue::Decimal(d) => {
                self.u8(1);
                self.dec(*d);
            }
            FieldValue::Bool(b) => {
                self.u8(2);
                self.bool(*b);
            }
            FieldValue::String(s) => {
                self.u8(3);
                self.str(s);
            }
            FieldValue::NullableInt(x) => {
                self.u8(4);
                self.opt_i32(*x);
            }
            FieldValue::CardHandle(h) => {
                self.u8(5);
                self.i32(*h);
            }
            FieldValue::CreatureHandle(h) => {
                self.u8(6);
                self.i32(*h);
            }
            FieldValue::IntArray(arr) => {
                self.u8(7);
                self.var_u32(arr.len() as u32);
                for &x in arr {
                    self.i32(x);
                }
            }
            FieldValue::CardHandleArray(arr) => {
                self.u8(8);
                self.var_u32(arr.len() as u32);
                for &x in arr {
                    self.i32(x);
                }
            }
            FieldValue::CreatureHandleArray(arr) => {
                self.u8(9);
                self.var_u32(arr.len() as u32);
                for &x in arr {
                    self.i32(x);
                }
            }
            FieldValue::CardIntMap(arr) => {
                self.u8(10);
                self.var_u32(arr.len() as u32);
                for &(a, b) in arr {
                    self.i32(a);
                    self.i32(b);
                }
            }
            FieldValue::CreatureDecimalMap(arr) => {
                self.u8(11);
                self.var_u32(arr.len() as u32);
                for &(a, d) in arr {
                    self.i32(a);
                    self.dec(d);
                }
            }
            FieldValue::CardPairArray(arr) => {
                self.u8(12);
                self.var_u32(arr.len() as u32);
                for &(a, b) in arr {
                    self.i32(a);
                    self.i32(b);
                }
            }
        }
    }
}

#[inline(always)]
fn slice_u16(b: &[u8], off: usize) -> u16 {
    u16::from_le_bytes([b[off], b[off + 1]])
}

#[inline(always)]
fn slice_u32(b: &[u8], off: usize) -> u32 {
    u32::from_le_bytes([b[off], b[off + 1], b[off + 2], b[off + 3]])
}

#[inline(always)]
fn slice_i32(b: &[u8], off: usize) -> i32 {
    i32::from_le_bytes([b[off], b[off + 1], b[off + 2], b[off + 3]])
}

#[inline(always)]
fn slice_u64(b: &[u8], off: usize) -> u64 {
    u64::from_le_bytes([
        b[off],
        b[off + 1],
        b[off + 2],
        b[off + 3],
        b[off + 4],
        b[off + 5],
        b[off + 6],
        b[off + 7],
    ])
}

struct ByteReader<'a> {
    start: *const u8,
    cur: *const u8,
    end: *const u8,
    _marker: std::marker::PhantomData<&'a [u8]>,
}

impl<'a> ByteReader<'a> {
    #[inline]
    fn new(buf: &'a [u8]) -> Self {
        let range = buf.as_ptr_range();
        Self {
            start: range.start,
            cur: range.start,
            end: range.end,
            _marker: std::marker::PhantomData,
        }
    }

    #[inline(always)]
    fn pos(&self) -> usize {
        (self.cur as usize) - (self.start as usize)
    }

    #[inline(always)]
    fn remaining(&self) -> usize {
        (self.end as usize) - (self.cur as usize)
    }

    #[inline(always)]
    fn take(&mut self, n: usize) -> Result<&'a [u8], ()> {
        if self.remaining() < n {
            return Err(());
        }
        // SAFETY: `remaining() >= n` checks `[cur, cur + n) <= end`.
        unsafe {
            let s = std::slice::from_raw_parts(self.cur, n);
            self.cur = self.cur.add(n);
            Ok(s)
        }
    }

    #[inline(always)]
    fn peek_u8(&self) -> Result<u8, ()> {
        if self.cur == self.end {
            return Err(());
        }
        // SAFETY: `cur < end` within the same allocated slice.
        Ok(unsafe { *self.cur })
    }

    #[inline(always)]
    fn u8(&mut self) -> Result<u8, ()> {
        if self.cur == self.end {
            return Err(());
        }
        // SAFETY: `cur < end` within the same allocated slice.
        unsafe {
            let b = *self.cur;
            self.cur = self.cur.add(1);
            Ok(b)
        }
    }

    #[inline(always)]
    fn bool(&mut self) -> Result<bool, ()> {
        Ok(self.u8()? != 0)
    }

    #[inline(always)]
    fn u16(&mut self) -> Result<u16, ()> {
        if self.remaining() < 2 {
            return Err(());
        }
        unsafe {
            let v = u16::from_le(std::ptr::read_unaligned(self.cur.cast::<u16>()));
            self.cur = self.cur.add(2);
            Ok(v)
        }
    }

    #[inline(always)]
    fn u32(&mut self) -> Result<u32, ()> {
        if self.remaining() < 4 {
            return Err(());
        }
        unsafe {
            let v = u32::from_le(std::ptr::read_unaligned(self.cur.cast::<u32>()));
            self.cur = self.cur.add(4);
            Ok(v)
        }
    }

    #[inline(always)]
    fn i32(&mut self) -> Result<i32, ()> {
        Ok(self.u32()? as i32)
    }

    #[inline(always)]
    fn u64(&mut self) -> Result<u64, ()> {
        if self.remaining() < 8 {
            return Err(());
        }
        unsafe {
            let v = u64::from_le(std::ptr::read_unaligned(self.cur.cast::<u64>()));
            self.cur = self.cur.add(8);
            Ok(v)
        }
    }

    #[cold]
    #[inline(never)]
    fn var_u32_slow(&mut self, b0: u8) -> Result<u32, ()> {
        let mut res = (b0 & 0x7F) as u32;
        let mut shift = 7u32;
        loop {
            let byte = self.u8()?;
            res |= ((byte & 0x7F) as u32) << shift;
            if (byte & 0x80) == 0 {
                return Ok(res);
            }
            shift += 7;
            if shift >= 35 {
                return Err(());
            }
        }
    }

    #[inline(always)]
    fn var_u32(&mut self) -> Result<u32, ()> {
        let b0 = self.u8()?;
        if b0 < 0x80 {
            return Ok(b0 as u32);
        }
        self.var_u32_slow(b0)
    }

    #[inline]
    fn var_i32(&mut self) -> Result<i32, ()> {
        let zz = self.var_u32()?;
        Ok(((zz >> 1) as i32) ^ -((zz & 1) as i32))
    }

    #[inline]
    fn dec(&mut self) -> Result<Dec, ()> {
        if self.u8()? == 0 {
            let raw = self.take(12)?;
            let m = i64::from_le_bytes([
                raw[0], raw[1], raw[2], raw[3], raw[4], raw[5], raw[6], raw[7],
            ]);
            let scale = slice_u32(raw, 8);
            Dec::from_parts(m, scale).map_err(|_| ())
        } else {
            let s = self.str()?;
            serde_json::from_str(&format!("\"{s}\"")).map_err(|_| ())
        }
    }

    #[inline]
    fn str(&mut self) -> Result<String, ()> {
        let mut s = String::new();
        self.read_str_into(&mut s)?;
        Ok(s)
    }

    #[inline]
    fn read_str_into(&mut self, dst: &mut String) -> Result<(), ()> {
        let len = self.var_u32()? as usize;
        let bytes = self.take(len)?;
        let s = std::str::from_utf8(bytes).map_err(|_| ())?;
        dst.clear();
        dst.push_str(s);
        Ok(())
    }

    #[inline]
    fn read_cow_str_into(&mut self, dst: &mut Cow<'static, str>) -> Result<(), ()> {
        let len = self.var_u32()? as usize;
        let bytes = self.take(len)?;
        let s = std::str::from_utf8(bytes).map_err(|_| ())?;
        match dst {
            Cow::Owned(existing) => {
                existing.clear();
                existing.push_str(s);
            }
            Cow::Borrowed(existing) => {
                if *existing != s {
                    *dst = Cow::Owned(s.to_owned());
                }
            }
        }
        Ok(())
    }

    #[inline]
    fn sym_cow(&mut self) -> Result<Cow<'static, str>, ()> {
        let id = self.u16()?;
        if id != SYM_FALLBACK_ID {
            sym_str(id).map(Cow::Borrowed).ok_or(())
        } else {
            Ok(Cow::Owned(self.str()?))
        }
    }

    #[inline(always)]
    fn read_borrowed_static_str_len(&mut self, len: usize) -> Result<&'static str, ()> {
        #[cfg(target_pointer_width = "64")]
        let ptr_val = self.u64()? as usize;
        #[cfg(not(target_pointer_width = "64"))]
        let ptr_val = {
            const USIZE_BYTES: usize = std::mem::size_of::<usize>();
            let raw_ptr = self.take(USIZE_BYTES)?;
            usize::from_le_bytes(raw_ptr.try_into().unwrap())
        };
        if len == 0 {
            return Ok("");
        }
        if ptr_val == 0 {
            return Err(());
        }
        // SAFETY: encoded in-process from a `Cow::Borrowed(&'static str)` whose backing bytes live for `'static`.
        let slice = unsafe { std::slice::from_raw_parts(ptr_val as *const u8, len) };
        Ok(unsafe { std::str::from_utf8_unchecked(slice) })
    }

    #[inline(always)]
    fn read_borrowed_static_str(&mut self) -> Result<&'static str, ()> {
        #[cfg(target_pointer_width = "64")]
        let ptr_val = self.u64()? as usize;
        #[cfg(not(target_pointer_width = "64"))]
        let ptr_val = {
            const USIZE_BYTES: usize = std::mem::size_of::<usize>();
            let raw_ptr = self.take(USIZE_BYTES)?;
            usize::from_le_bytes(raw_ptr.try_into().unwrap())
        };
        let len = self.var_u32()? as usize;
        if len == 0 {
            return Ok("");
        }
        if ptr_val == 0 {
            return Err(());
        }
        // SAFETY: encoded in-process from a `Cow::Borrowed(&'static str)` whose backing bytes live for `'static`.
        let slice = unsafe { std::slice::from_raw_parts(ptr_val as *const u8, len) };
        Ok(unsafe { std::str::from_utf8_unchecked(slice) })
    }

    #[inline(always)]
    fn cow_str(&mut self) -> Result<Cow<'static, str>, ()> {
        let tag = self.u8()?;
        if tag < 0x80 {
            Ok(Cow::Borrowed(self.read_borrowed_static_str_len(tag as usize)?))
        } else if tag == 0x80 {
            Ok(Cow::Borrowed(self.read_borrowed_static_str()?))
        } else if tag == 0x81 {
            self.sym_cow()
        } else {
            Err(())
        }
    }

    #[inline(always)]
    fn opt_cow_str(&mut self) -> Result<Option<Cow<'static, str>>, ()> {
        let tag = self.u8()?;
        if tag == 0 {
            Ok(None)
        } else if tag < 0x80 {
            Ok(Some(Cow::Borrowed(
                self.read_borrowed_static_str_len((tag - 1) as usize)?,
            )))
        } else if tag == 0x80 {
            Ok(Some(Cow::Borrowed(self.read_borrowed_static_str()?)))
        } else if tag == 0x81 {
            Ok(Some(self.sym_cow()?))
        } else {
            Err(())
        }
    }

    #[cold]
    #[inline(never)]
    fn set_cow_from_owned_cold(dst: &mut Cow<'static, str>, s: &'static str) {
        *dst = Cow::Borrowed(s);
    }

    #[cold]
    #[inline(never)]
    fn set_opt_cow_from_owned_cold(
        dst: &mut Option<Cow<'static, str>>,
        val: Option<&'static str>,
    ) {
        *dst = val.map(Cow::Borrowed);
    }

    #[inline(always)]
    fn read_sym_cow_into(&mut self, dst: &mut Cow<'static, str>) -> Result<(), ()> {
        let id = self.u16()?;
        if id != SYM_FALLBACK_ID {
            let s = sym_str(id).ok_or(())?;
            if let Cow::Borrowed(existing) = dst {
                if existing.as_ptr() != s.as_ptr() || existing.len() != s.len() {
                    *existing = s;
                }
            } else {
                Self::set_cow_from_owned_cold(dst, s);
            }
            Ok(())
        } else {
            self.read_cow_str_into(dst)
        }
    }

    #[inline(always)]
    fn read_cow_str_encoded_into(&mut self, dst: &mut Cow<'static, str>) -> Result<(), ()> {
        let tag = self.u8()?;
        if tag < 0x80 {
            let s = self.read_borrowed_static_str_len(tag as usize)?;
            if let Cow::Borrowed(existing) = dst {
                if existing.as_ptr() != s.as_ptr() || existing.len() != s.len() {
                    *existing = s;
                }
            } else {
                Self::set_cow_from_owned_cold(dst, s);
            }
            Ok(())
        } else if tag == 0x80 {
            let s = self.read_borrowed_static_str()?;
            if let Cow::Borrowed(existing) = dst {
                if existing.as_ptr() != s.as_ptr() || existing.len() != s.len() {
                    *existing = s;
                }
            } else {
                Self::set_cow_from_owned_cold(dst, s);
            }
            Ok(())
        } else if tag == 0x81 {
            self.read_sym_cow_into(dst)
        } else {
            Err(())
        }
    }

    #[inline(always)]
    fn read_opt_cow_str_encoded_into(
        &mut self,
        dst: &mut Option<Cow<'static, str>>,
    ) -> Result<(), ()> {
        let tag = self.u8()?;
        if tag == 0 {
            match dst {
                None => {}
                // SAFETY: `Some(Cow::Borrowed(_))` has no destructor drop glue.
                Some(Cow::Borrowed(_)) => unsafe {
                    std::ptr::write(dst, None);
                },
                Some(Cow::Owned(_)) => Self::set_opt_cow_from_owned_cold(dst, None),
            }
            Ok(())
        } else if tag < 0x80 {
            let s = self.read_borrowed_static_str_len((tag - 1) as usize)?;
            match dst {
                Some(Cow::Borrowed(existing)) => {
                    if existing.as_ptr() != s.as_ptr() || existing.len() != s.len() {
                        *existing = s;
                    }
                }
                // SAFETY: `None` has no destructor drop glue.
                None => unsafe {
                    std::ptr::write(dst, Some(Cow::Borrowed(s)));
                },
                Some(Cow::Owned(_)) => Self::set_opt_cow_from_owned_cold(dst, Some(s)),
            }
            Ok(())
        } else if tag == 0x80 {
            let s = self.read_borrowed_static_str()?;
            match dst {
                Some(Cow::Borrowed(existing)) => {
                    if existing.as_ptr() != s.as_ptr() || existing.len() != s.len() {
                        *existing = s;
                    }
                }
                // SAFETY: `None` has no destructor drop glue.
                None => unsafe {
                    std::ptr::write(dst, Some(Cow::Borrowed(s)));
                },
                Some(Cow::Owned(_)) => Self::set_opt_cow_from_owned_cold(dst, Some(s)),
            }
            Ok(())
        } else if tag == 0x81 {
            let id = self.u16()?;
            if id != SYM_FALLBACK_ID {
                let s = sym_str(id).ok_or(())?;
                match dst {
                    Some(Cow::Borrowed(existing)) => {
                        if existing.as_ptr() != s.as_ptr() || existing.len() != s.len() {
                            *existing = s;
                        }
                    }
                    // SAFETY: `None` has no destructor drop glue.
                    None => unsafe {
                        std::ptr::write(dst, Some(Cow::Borrowed(s)));
                    },
                    Some(Cow::Owned(_)) => Self::set_opt_cow_from_owned_cold(dst, Some(s)),
                }
                Ok(())
            } else {
                let slot = dst.get_or_insert_with(|| Cow::Borrowed(""));
                self.read_cow_str_into(slot)
            }
        } else {
            Err(())
        }
    }

    #[inline]
    fn opt_i32(&mut self) -> Result<Option<i32>, ()> {
        if self.u8()? == 0 {
            Ok(None)
        } else {
            Ok(Some(self.i32()?))
        }
    }

    #[inline]
    fn opt_u32(&mut self) -> Result<Option<u32>, ()> {
        if self.u8()? == 0 {
            Ok(None)
        } else {
            Ok(Some(self.u32()?))
        }
    }

    #[inline]
    fn opt_u64(&mut self) -> Result<Option<u64>, ()> {
        if self.u8()? == 0 {
            Ok(None)
        } else {
            Ok(Some(self.u64()?))
        }
    }

    #[inline]
    fn rng(&mut self) -> Result<Rng, ()> {
        let raw = self.take(36)?;
        let counter = slice_i32(raw, 0);
        let s0 = slice_u64(raw, 4);
        let s1 = slice_u64(raw, 12);
        let s2 = slice_u64(raw, 20);
        let s3 = slice_u64(raw, 28);
        Ok(Rng {
            counter,
            random: MegaRandom::from_state(s0, s1, s2, s3),
        })
    }

    fn cow_field_map(&mut self) -> Result<FieldMap, ()> {
        let mut map = FieldMap::new();
        self.cow_field_map_into(&mut map)?;
        Ok(map)
    }

    fn cow_field_map_into(&mut self, map: &mut FieldMap) -> Result<(), ()> {
        let len = self.var_u32()? as usize;
        if len == 0 {
            if !map.is_empty() {
                map.clear();
            }
            return Ok(());
        }
        for i in 0..len {
            let key = self.cow_str()?;
            let v = self.field_value()?;
            map.overwrite_sorted_cow_at(i, key, v);
        }
        if map.len() > len {
            map.truncate(len);
        }
        Ok(())
    }

    fn field_value(&mut self) -> Result<FieldValue, ()> {
        match self.u8()? {
            0 => Ok(FieldValue::Int(self.i32()?)),
            1 => Ok(FieldValue::Decimal(self.dec()?)),
            2 => Ok(FieldValue::Bool(self.bool()?)),
            3 => Ok(FieldValue::String(self.str()?)),
            4 => Ok(FieldValue::NullableInt(self.opt_i32()?)),
            5 => Ok(FieldValue::CardHandle(self.i32()?)),
            6 => Ok(FieldValue::CreatureHandle(self.i32()?)),
            7 => {
                let len = self.var_u32()? as usize;
                let mut v = Vec::with_capacity(len);
                for _ in 0..len {
                    v.push(self.i32()?);
                }
                Ok(FieldValue::IntArray(v))
            }
            8 => {
                let len = self.var_u32()? as usize;
                let mut v = Vec::with_capacity(len);
                for _ in 0..len {
                    v.push(self.i32()?);
                }
                Ok(FieldValue::CardHandleArray(v))
            }
            9 => {
                let len = self.var_u32()? as usize;
                let mut v = Vec::with_capacity(len);
                for _ in 0..len {
                    v.push(self.i32()?);
                }
                Ok(FieldValue::CreatureHandleArray(v))
            }
            10 => {
                let len = self.var_u32()? as usize;
                let mut v = Vec::with_capacity(len);
                for _ in 0..len {
                    v.push((self.i32()?, self.i32()?));
                }
                Ok(FieldValue::CardIntMap(v))
            }
            11 => {
                let len = self.var_u32()? as usize;
                let mut v = Vec::with_capacity(len);
                for _ in 0..len {
                    v.push((self.i32()?, self.dec()?));
                }
                Ok(FieldValue::CreatureDecimalMap(v))
            }
            12 => {
                let len = self.var_u32()? as usize;
                let mut v = Vec::with_capacity(len);
                for _ in 0..len {
                    v.push((self.i32()?, self.i32()?));
                }
                Ok(FieldValue::CardPairArray(v))
            }
            _ => Err(()),
        }
    }
}

#[inline]
fn decode_card_id(raw: u16) -> Result<CardId, ()> {
    if (raw as usize) < CardId::COUNT {
        Ok(unsafe { core::mem::transmute::<u16, CardId>(raw) })
    } else {
        Err(())
    }
}

#[inline]
fn decode_power_id(raw: u16) -> Result<PowerId, ()> {
    if (raw as usize) < PowerId::COUNT {
        Ok(unsafe { core::mem::transmute::<u16, PowerId>(raw) })
    } else {
        Err(())
    }
}

#[inline]
fn decode_relic_id(raw: u16) -> Result<RelicId, ()> {
    if (raw as usize) < RelicId::COUNT {
        Ok(unsafe { core::mem::transmute::<u16, RelicId>(raw) })
    } else {
        Err(())
    }
}

#[inline]
fn decode_potion_id(raw: u16) -> Result<PotionId, ()> {
    if (raw as usize) < PotionId::COUNT {
        Ok(unsafe { core::mem::transmute::<u16, PotionId>(raw) })
    } else {
        Err(())
    }
}

#[inline]
fn decode_monster_id(raw: u16) -> Result<MonsterId, ()> {
    if (raw as usize) < MonsterId::COUNT {
        Ok(unsafe { core::mem::transmute::<u16, MonsterId>(raw) })
    } else {
        Err(())
    }
}

#[inline]
fn decode_orb_id(raw: u8) -> Result<OrbId, ()> {
    if (raw as usize) < OrbId::COUNT {
        Ok(unsafe { core::mem::transmute::<u8, OrbId>(raw) })
    } else {
        Err(())
    }
}

#[inline]
fn decode_enchantment_id(raw: u16) -> Result<EnchantmentId, ()> {
    if (raw as usize) < EnchantmentId::COUNT {
        Ok(unsafe { core::mem::transmute::<u16, EnchantmentId>(raw) })
    } else {
        Err(())
    }
}

#[inline]
fn decode_affliction_id(raw: u16) -> Result<AfflictionId, ()> {
    if (raw as usize) < AfflictionId::COUNT {
        Ok(unsafe { core::mem::transmute::<u16, AfflictionId>(raw) })
    } else {
        Err(())
    }
}

#[inline]
fn decode_dominion_id(raw: u16) -> Result<DominionId, ()> {
    if (raw as usize) < DominionId::COUNT {
        Ok(unsafe { core::mem::transmute::<u16, DominionId>(raw) })
    } else {
        Err(())
    }
}

#[inline]
fn decode_run_model_id(raw: u8) -> Result<RunModelId, ()> {
    if (raw as usize) < RunModelId::COUNT {
        Ok(unsafe { core::mem::transmute::<u8, RunModelId>(raw) })
    } else {
        Err(())
    }
}

#[inline]
fn decode_combat_side(raw: u8) -> Result<CombatSide, ()> {
    match raw {
        0 => Ok(CombatSide::Player),
        1 => Ok(CombatSide::Enemy),
        _ => Err(()),
    }
}

#[inline]
fn decode_player_turn_phase(raw: u8) -> Result<PlayerTurnPhase, ()> {
    match raw {
        0 => Ok(PlayerTurnPhase::None),
        1 => Ok(PlayerTurnPhase::Start),
        2 => Ok(PlayerTurnPhase::AutoPrePlay),
        3 => Ok(PlayerTurnPhase::Play),
        4 => Ok(PlayerTurnPhase::AutoPostPlay),
        5 => Ok(PlayerTurnPhase::End),
        _ => Err(()),
    }
}

#[inline]
fn decode_pile_type(raw: u8) -> Result<PileType, ()> {
    match raw {
        0 => Ok(PileType::None),
        1 => Ok(PileType::Draw),
        2 => Ok(PileType::Hand),
        3 => Ok(PileType::Discard),
        4 => Ok(PileType::Exhaust),
        5 => Ok(PileType::Play),
        6 => Ok(PileType::Deck),
        _ => Err(()),
    }
}

#[inline]
fn decode_pile_pos(raw: u8) -> Result<CardPilePosition, ()> {
    match raw {
        0 => Ok(CardPilePosition::Bottom),
        1 => Ok(CardPilePosition::Top),
        2 => Ok(CardPilePosition::Random),
        _ => Err(()),
    }
}

#[inline]
fn decode_arrow_status(raw: u8) -> Result<ArrowStatus, ()> {
    match raw {
        0 => Ok(ArrowStatus::Retrieved),
        1 => Ok(ArrowStatus::Away),
        2 => Ok(ArrowStatus::Destroyed),
        _ => Err(()),
    }
}

#[inline]
fn decode_ench_status(raw: u8) -> Result<EnchantmentStatus, ()> {
    match raw {
        0 => Ok(EnchantmentStatus::Normal),
        1 => Ok(EnchantmentStatus::Disabled),
        _ => Err(()),
    }
}

#[inline]
fn decode_card_type(raw: u8) -> Result<CardType, ()> {
    match raw {
        0 => Ok(CardType::None),
        1 => Ok(CardType::Attack),
        2 => Ok(CardType::Skill),
        3 => Ok(CardType::Power),
        4 => Ok(CardType::Status),
        5 => Ok(CardType::Curse),
        6 => Ok(CardType::Quest),
        _ => Err(()),
    }
}

#[inline]
fn decode_mod_kind(raw: u8) -> Result<LocalEnergyModifierKind, ()> {
    match raw {
        0 => Ok(LocalEnergyModifierKind::FreeThisTurn),
        1 => Ok(LocalEnergyModifierKind::FreeUntilPlayed),
        2 => Ok(LocalEnergyModifierKind::FreeThisCombat),
        3 => Ok(LocalEnergyModifierKind::SetThisTurn),
        4 => Ok(LocalEnergyModifierKind::SetUntilPlayed),
        5 => Ok(LocalEnergyModifierKind::SetThisTurnOrUntilPlayed),
        6 => Ok(LocalEnergyModifierKind::SetThisCombat),
        7 => Ok(LocalEnergyModifierKind::AddThisCombat),
        8 => Ok(LocalEnergyModifierKind::AddThisTurn),
        9 => Ok(LocalEnergyModifierKind::AddUntilPlayed),
        10 => Ok(LocalEnergyModifierKind::AddThisTurnOrUntilPlayed),
        _ => Err(()),
    }
}

struct SetLenOnDrop<'a, T: Copy> {
    vec: &'a mut Vec<T>,
    base_len: usize,
    written: usize,
}

impl<T: Copy> Drop for SetLenOnDrop<'_, T> {
    #[inline(always)]
    fn drop(&mut self) {
        // SAFETY: callers write `self.written` initialized elements at `base_len..base_len + written`.
        unsafe {
            self.vec.set_len(self.base_len + self.written);
        }
    }
}

#[inline(always)]
unsafe fn write_card_handles_unchecked(w: &mut ByteWriter<'_>, handles: &[CardHandle]) {
    if handles.is_empty() {
        unsafe {
            w.u8_unchecked(0);
        }
        return;
    }
    unsafe {
        w.var_u32_unchecked(handles.len() as u32);
        for &h in handles {
            w.u32_unchecked(h.0);
        }
    }
}

#[inline]
fn write_card_handles(w: &mut ByteWriter<'_>, handles: &[CardHandle]) {
    if handles.is_empty() {
        w.u8(0);
        return;
    }
    w.ensure(5 + handles.len() * 4);
    unsafe {
        write_card_handles_unchecked(w, handles);
    }
}

#[inline(always)]
unsafe fn write_card_handles_u16_unchecked(w: &mut ByteWriter<'_>, handles: &[CardHandle]) {
    if handles.is_empty() {
        unsafe {
            w.u8_unchecked(0);
        }
        return;
    }
    let len = handles.len();
    unsafe {
        w.var_u32_unchecked(len as u32);
        let dst = w.cur.cast::<u16>();
        for i in 0..len {
            let h = handles.get_unchecked(i).0;
            debug_assert!(h <= u16::MAX as u32);
            std::ptr::write_unaligned(dst.add(i), (h as u16).to_le());
        }
        w.cur = w.cur.add(len * 2);
    }
}

#[inline(always)]
unsafe fn write_creature_handles_unchecked(w: &mut ByteWriter<'_>, handles: &[CreatureHandle]) {
    if handles.is_empty() {
        unsafe {
            w.u8_unchecked(0);
        }
        return;
    }
    unsafe {
        w.var_u32_unchecked(handles.len() as u32);
        for &h in handles {
            w.u32_unchecked(h.0);
        }
    }
}

fn read_card_handles<const N: usize>(
    r: &mut ByteReader<'_>,
    out: &mut SmallVec<[CardHandle; N]>,
) -> Result<(), ()>
where
    [CardHandle; N]: smallvec::Array<Item = CardHandle>,
{
    let len = r.var_u32()? as usize;
    let raw = r.take(len * 4)?;
    out.clear();
    out.reserve(len);
    for chunk in raw.chunks_exact(4) {
        out.push(CardHandle(u32::from_le_bytes([
            chunk[0], chunk[1], chunk[2], chunk[3],
        ])));
    }
    Ok(())
}

fn read_card_handles_vec(
    r: &mut ByteReader<'_>,
    out: &mut Vec<CardHandle>,
) -> Result<(), ()> {
    let len = r.var_u32()? as usize;
    let raw = r.take(len * 4)?;
    out.clear();
    if len == 0 {
        return Ok(());
    }
    out.reserve(len);
    let src = raw.as_ptr().cast::<u32>();
    let dst = out.as_mut_ptr();
    unsafe {
        for i in 0..len {
            let v = u32::from_le(std::ptr::read_unaligned(src.add(i)));
            dst.add(i).write(CardHandle(v));
        }
        out.set_len(len);
    }
    Ok(())
}

fn read_card_handles_u16_vec(
    r: &mut ByteReader<'_>,
    out: &mut Vec<CardHandle>,
) -> Result<(), ()> {
    let len = r.var_u32()? as usize;
    let raw = r.take(len * 2)?;
    out.clear();
    if len == 0 {
        return Ok(());
    }
    out.reserve(len);
    let src = raw.as_ptr().cast::<u16>();
    let dst = out.as_mut_ptr();
    unsafe {
        for i in 0..len {
            let v = u16::from_le(std::ptr::read_unaligned(src.add(i)));
            dst.add(i).write(CardHandle(v as u32));
        }
        out.set_len(len);
    }
    Ok(())
}


fn read_creature_handles<const N: usize>(
    r: &mut ByteReader<'_>,
    out: &mut SmallVec<[CreatureHandle; N]>,
) -> Result<(), ()>
where
    [CreatureHandle; N]: smallvec::Array<Item = CreatureHandle>,
{
    let len = r.var_u32()? as usize;
    let raw = r.take(len * 4)?;
    out.clear();
    out.reserve(len);
    for chunk in raw.chunks_exact(4) {
        out.push(CreatureHandle(u32::from_le_bytes([
            chunk[0], chunk[1], chunk[2], chunk[3],
        ])));
    }
    Ok(())
}

fn read_creature_handles_vec(
    r: &mut ByteReader<'_>,
    out: &mut Vec<CreatureHandle>,
) -> Result<(), ()> {
    let len = r.var_u32()? as usize;
    let raw = r.take(len * 4)?;
    out.clear();
    if len == 0 {
        return Ok(());
    }
    out.reserve(len);
    let src = raw.as_ptr().cast::<u32>();
    let dst = out.as_mut_ptr();
    unsafe {
        for i in 0..len {
            let v = u32::from_le(std::ptr::read_unaligned(src.add(i)));
            dst.add(i).write(CreatureHandle(v));
        }
        out.set_len(len);
    }
    Ok(())
}

fn read_turn_key(r: &mut ByteReader<'_>) -> Result<TurnKey, ()> {
    let round_number = r.i32()?;
    let side = decode_combat_side(r.u8()?)?;
    let len = r.var_u32()? as usize;
    let mut player_turn_numbers = InlineOptI32Vec4::new();
    for _ in 0..len {
        player_turn_numbers.push(r.opt_i32()?);
    }
    Ok(TurnKey {
        round_number,
        side,
        player_turn_numbers,
    })
}

#[inline(always)]
unsafe fn write_turn_key_delta_unchecked(
    w: &mut ByteWriter<'_>,
    tk: &TurnKey,
    prev_valid: &mut bool,
    prev: &mut TurnKey,
) {
    if *prev_valid && *prev == *tk {
        unsafe {
            w.u8_unchecked(0);
        }
        return;
    }
    *prev_valid = true;
    *prev = *tk;
    if tk.side == CombatSide::Player
        && tk.round_number >= 0
        && tk.player_turn_numbers.is_single_some(tk.round_number)
    {
        unsafe {
            w.u8_unchecked(1);
            w.var_u32_unchecked(tk.round_number as u32);
        }
    } else {
        unsafe {
            w.u8_unchecked(2);
            w.i32_unchecked(tk.round_number);
            w.u8_unchecked(tk.side as u8);
            let ptn_len = tk.player_turn_numbers.len();
            debug_assert!(ptn_len <= 4);
            w.var_u32_unchecked(ptn_len as u32);
            for p in tk.player_turn_numbers.iter() {
                w.opt_i32_unchecked(p);
            }
        }
    }
}

#[inline(always)]
fn read_turn_key_delta_with_tag_into(
    tag: u8,
    r: &mut ByteReader<'_>,
    prev_valid: &mut bool,
    prev: &mut TurnKey,
) -> Result<(), ()> {
    if tag == 0 {
        if *prev_valid {
            return Ok(());
        }
        return Err(());
    }
    let tk = match tag {
        1 => {
            let round = r.var_u32()? as i32;
            TurnKey {
                round_number: round,
                side: CombatSide::Player,
                player_turn_numbers: InlineOptI32Vec4::one(Some(round)),
            }
        }
        2 => read_turn_key(r)?,
        _ => return Err(()),
    };
    *prev_valid = true;
    *prev = tk;
    Ok(())
}

#[inline(always)]
fn read_turn_key_delta_into(
    r: &mut ByteReader<'_>,
    prev_valid: &mut bool,
    prev: &mut TurnKey,
) -> Result<(), ()> {
    let tag = r.u8()?;
    read_turn_key_delta_with_tag_into(tag, r, prev_valid, prev)
}

fn write_hook_listener(w: &mut ByteWriter<'_>, l: HookListener) {
    match l {
        HookListener::Relic(idx) => {
            w.u8(0);
            w.var_u32(idx as u32);
        }
        HookListener::Potion(idx) => {
            w.u8(1);
            w.var_u32(idx as u32);
        }
        HookListener::Power {
            creature,
            power_handle,
        } => {
            w.u8(2);
            w.u32(creature.0);
            w.u32(power_handle);
        }
        HookListener::DetachedPower { creature, power } => {
            w.u8(3);
            w.u32(creature.0);
            w.u32(power.handle);
            w.u16(power.id as u16);
            w.i32(power.amount);
            w.i32(power.amount_on_turn_start);
            w.bool(power.skip_next_duration_tick);
            w.opt_u32(power.applier_handle.map(|h| h.0));
            w.opt_u32(power.ext_index);
        }
        HookListener::Monster(ch) => {
            w.u8(4);
            w.u32(ch.0);
        }
        HookListener::Orb(idx) => {
            w.u8(5);
            w.var_u32(idx as u32);
        }
        HookListener::Card(ch) => {
            w.u8(6);
            w.u32(ch.0);
        }
        HookListener::Enchantment(ch) => {
            w.u8(7);
            w.u32(ch.0);
        }
        HookListener::Affliction(ch) => {
            w.u8(8);
            w.u32(ch.0);
        }
        HookListener::RunModel(id) => {
            w.u8(9);
            w.u8(id as u8);
        }
        HookListener::Dominion(id) => {
            w.u8(10);
            w.u16(id as u16);
        }
    }
}

fn read_hook_listener(r: &mut ByteReader<'_>) -> Result<HookListener, ()> {
    match r.u8()? {
        0 => Ok(HookListener::Relic(r.var_u32()? as usize)),
        1 => Ok(HookListener::Potion(r.var_u32()? as usize)),
        2 => Ok(HookListener::Power {
            creature: CreatureHandle(r.u32()?),
            power_handle: r.u32()?,
        }),
        3 => Ok(HookListener::DetachedPower {
            creature: CreatureHandle(r.u32()?),
            power: PowerInst {
                handle: r.u32()?,
                id: decode_power_id(r.u16()?)?,
                amount: r.i32()?,
                amount_on_turn_start: r.i32()?,
                skip_next_duration_tick: r.bool()?,
                applier_handle: r.opt_u32()?.map(CreatureHandle),
                ext_index: r.opt_u32()?,
            },
        }),
        4 => Ok(HookListener::Monster(CreatureHandle(r.u32()?))),
        5 => Ok(HookListener::Orb(r.var_u32()? as usize)),
        6 => Ok(HookListener::Card(CardHandle(r.u32()?))),
        7 => Ok(HookListener::Enchantment(CardHandle(r.u32()?))),
        8 => Ok(HookListener::Affliction(CardHandle(r.u32()?))),
        9 => Ok(HookListener::RunModel(decode_run_model_id(r.u8()?)?)),
        10 => Ok(HookListener::Dominion(decode_dominion_id(r.u16()?)?)),
        _ => Err(()),
    }
}

fn write_choice_request(w: &mut ByteWriter<'_>, req: &ChoiceRequest) {
    w.u8(match req.timing {
        ChoiceTiming::Action => 0,
        ChoiceTiming::PlayerTurnEnd => 1,
        ChoiceTiming::EnemyTurn => 2,
        ChoiceTiming::PlayerTurnStart => 3,
    });
    w.cow_str(&req.source_id);
    w.u8(req.source_pile as u8);
    w.var_u32(req.min_count as u32);
    w.var_u32(req.max_count as u32);
    write_card_handles(w, &req.candidates);
    w.bool(req.can_skip);
}

fn read_choice_request(r: &mut ByteReader<'_>) -> Result<ChoiceRequest, ()> {
    let timing = match r.u8()? {
        0 => ChoiceTiming::Action,
        1 => ChoiceTiming::PlayerTurnEnd,
        2 => ChoiceTiming::EnemyTurn,
        3 => ChoiceTiming::PlayerTurnStart,
        _ => return Err(()),
    };
    let source_id = r.cow_str()?;
    let source_pile = decode_pile_type(r.u8()?)?;
    let min_count = r.var_u32()? as usize;
    let max_count = r.var_u32()? as usize;
    let mut candidates = SmallVec::new();
    read_card_handles(r, &mut candidates)?;
    let can_skip = r.bool()?;
    Ok(ChoiceRequest {
        timing,
        source_id,
        source_pile,
        min_count,
        max_count,
        candidates,
        can_skip,
    })
}

fn write_pending(w: &mut ByteWriter<'_>, pending: &Option<PendingContinuation>) {
    let Some(p) = pending else {
        w.u8(0);
        return;
    };
    w.u8(1);
    write_choice_request(w, &p.request);
    w.var_u32(p.frames.len() as u32);
    for frame in &p.frames {
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
                w.u8(0);
                w.u32(card.0);
                w.u32(original_owner_creature.0);
                w.opt_u32(target.map(|h| h.0));
                w.i32(*play_index);
                w.i32(*play_count);
                w.bool(*is_auto_play);
                w.u8(*result_pile as u8);
                w.u8(*result_position as u8);
                w.i32(resources.energy_spent);
                w.i32(resources.energy_value);
                w.i32(resources.stars_spent);
                w.i32(resources.star_value);
                w.opt_i32(resources.captured_energy_x);
                w.opt_i32(resources.captured_star_x);
                w.u8(*on_play_resume_step);
            }
            ContinuationFrame::PotionUseResume {
                potion_slot,
                potion_id,
                target,
                resume_step,
            } => {
                w.u8(1);
                w.var_u32(*potion_slot as u32);
                w.u16(*potion_id as u16);
                w.opt_u32(target.map(|h| h.0));
                w.u8(*resume_step);
            }
            ContinuationFrame::BeforeHandDrawResume {
                paused_listener,
                remaining_listeners,
                is_extra_turn,
            } => {
                w.u8(2);
                write_hook_listener(w, *paused_listener);
                w.var_u32(remaining_listeners.len() as u32);
                for &l in remaining_listeners {
                    write_hook_listener(w, l);
                }
                w.bool(*is_extra_turn);
            }
            ContinuationFrame::AfterShuffleResume {
                paused_listener,
                remaining_listeners,
                remaining_draws,
                from_hand_draw,
                resume_turn_start,
            } => {
                w.u8(3);
                write_hook_listener(w, *paused_listener);
                w.var_u32(remaining_listeners.len() as u32);
                for &l in remaining_listeners {
                    write_hook_listener(w, l);
                }
                w.i32(*remaining_draws);
                w.bool(*from_hand_draw);
                w.u8(match resume_turn_start {
                    None => 0,
                    Some(false) => 1,
                    Some(true) => 2,
                });
            }
            ContinuationFrame::AfterPlayerTurnStartResume {
                paused_listener,
                remaining_listeners,
                is_extra_turn,
            } => {
                w.u8(4);
                write_hook_listener(w, *paused_listener);
                w.var_u32(remaining_listeners.len() as u32);
                for &l in remaining_listeners {
                    write_hook_listener(w, l);
                }
                w.bool(*is_extra_turn);
            }
            ContinuationFrame::CardSelectResume {
                source_card,
                action_kind,
            } => {
                w.u8(5);
                w.u32(source_card.0);
                match action_kind {
                    CardSelectActionKind::DiscardSelected => w.u8(0),
                    CardSelectActionKind::ExhaustSelected => w.u8(1),
                    CardSelectActionKind::MoveSelectedToPile { pile, position } => {
                        w.u8(2);
                        w.u8(*pile as u8);
                        w.u8(*position as u8);
                    }
                }
            }
            ContinuationFrame::AutoPlayResume {
                remaining_cards,
                fixed_target,
                spend_resources,
                force_exhaust,
                move_to_play,
            } => {
                w.u8(6);
                write_card_handles(w, remaining_cards);
                w.opt_u32(fixed_target.map(|h| h.0));
                w.bool(*spend_resources);
                w.u8(match force_exhaust {
                    None => 0,
                    Some(false) => 1,
                    Some(true) => 2,
                });
                w.bool(*move_to_play);
            }
            ContinuationFrame::EndTurnResume { stage } => {
                w.u8(7);
                w.u8(*stage);
            }
            ContinuationFrame::DeathRevivalResume { target, recursion } => {
                w.u8(8);
                w.u32(target.0);
                w.u32(*recursion);
            }
            ContinuationFrame::EnemyTurnResume {
                current_enemy,
                remaining_enemies,
            } => {
                w.u8(9);
                w.u32(current_enemy.0);
                w.var_u32(remaining_enemies.len() as u32);
                for &eh in remaining_enemies {
                    w.u32(eh.0);
                }
            }
            ContinuationFrame::AfterAutoPrePlayResume {
                remaining_listeners,
                is_late,
            } => {
                w.u8(10);
                w.var_u32(remaining_listeners.len() as u32);
                for &l in remaining_listeners {
                    write_hook_listener(w, l);
                }
                w.bool(*is_late);
            }
        }
    }
}

fn read_pending(r: &mut ByteReader<'_>) -> Result<Option<PendingContinuation>, ()> {
    if r.u8()? == 0 {
        return Ok(None);
    }
    let request = read_choice_request(r)?;
    let frame_count = r.var_u32()? as usize;
    let mut frames = SmallVec::with_capacity(frame_count);
    for _ in 0..frame_count {
        let frame = match r.u8()? {
            0 => ContinuationFrame::CardPlayResume {
                card: CardHandle(r.u32()?),
                original_owner_creature: CreatureHandle(r.u32()?),
                target: r.opt_u32()?.map(CreatureHandle),
                play_index: r.i32()?,
                play_count: r.i32()?,
                is_auto_play: r.bool()?,
                result_pile: decode_pile_type(r.u8()?)?,
                result_position: decode_pile_pos(r.u8()?)?,
                resources: CardPlayResources {
                    energy_spent: r.i32()?,
                    energy_value: r.i32()?,
                    stars_spent: r.i32()?,
                    star_value: r.i32()?,
                    captured_energy_x: r.opt_i32()?,
                    captured_star_x: r.opt_i32()?,
                },
                on_play_resume_step: r.u8()?,
            },
            1 => ContinuationFrame::PotionUseResume {
                potion_slot: r.var_u32()? as usize,
                potion_id: decode_potion_id(r.u16()?)?,
                target: r.opt_u32()?.map(CreatureHandle),
                resume_step: r.u8()?,
            },
            2 => {
                let paused_listener = read_hook_listener(r)?;
                let len = r.var_u32()? as usize;
                let mut remaining_listeners = SmallVec::with_capacity(len);
                for _ in 0..len {
                    remaining_listeners.push(read_hook_listener(r)?);
                }
                let is_extra_turn = r.bool()?;
                ContinuationFrame::BeforeHandDrawResume {
                    paused_listener,
                    remaining_listeners,
                    is_extra_turn,
                }
            }
            3 => {
                let paused_listener = read_hook_listener(r)?;
                let len = r.var_u32()? as usize;
                let mut remaining_listeners = SmallVec::with_capacity(len);
                for _ in 0..len {
                    remaining_listeners.push(read_hook_listener(r)?);
                }
                let remaining_draws = r.i32()?;
                let from_hand_draw = r.bool()?;
                let resume_turn_start = match r.u8()? {
                    0 => None,
                    1 => Some(false),
                    2 => Some(true),
                    _ => return Err(()),
                };
                ContinuationFrame::AfterShuffleResume {
                    paused_listener,
                    remaining_listeners,
                    remaining_draws,
                    from_hand_draw,
                    resume_turn_start,
                }
            }
            4 => {
                let paused_listener = read_hook_listener(r)?;
                let len = r.var_u32()? as usize;
                let mut remaining_listeners = SmallVec::with_capacity(len);
                for _ in 0..len {
                    remaining_listeners.push(read_hook_listener(r)?);
                }
                let is_extra_turn = r.bool()?;
                ContinuationFrame::AfterPlayerTurnStartResume {
                    paused_listener,
                    remaining_listeners,
                    is_extra_turn,
                }
            }
            5 => {
                let source_card = CardHandle(r.u32()?);
                let action_kind = match r.u8()? {
                    0 => CardSelectActionKind::DiscardSelected,
                    1 => CardSelectActionKind::ExhaustSelected,
                    2 => CardSelectActionKind::MoveSelectedToPile {
                        pile: decode_pile_type(r.u8()?)?,
                        position: decode_pile_pos(r.u8()?)?,
                    },
                    _ => return Err(()),
                };
                ContinuationFrame::CardSelectResume {
                    source_card,
                    action_kind,
                }
            }
            6 => {
                let mut remaining_cards = SmallVec::new();
                read_card_handles(r, &mut remaining_cards)?;
                let fixed_target = r.opt_u32()?.map(CreatureHandle);
                let spend_resources = r.bool()?;
                let force_exhaust = match r.u8()? {
                    0 => None,
                    1 => Some(false),
                    2 => Some(true),
                    _ => return Err(()),
                };
                let move_to_play = r.bool()?;
                ContinuationFrame::AutoPlayResume {
                    remaining_cards,
                    fixed_target,
                    spend_resources,
                    force_exhaust,
                    move_to_play,
                }
            }
            7 => ContinuationFrame::EndTurnResume { stage: r.u8()? },
            8 => ContinuationFrame::DeathRevivalResume {
                target: CreatureHandle(r.u32()?),
                recursion: r.u32()?,
            },
            9 => {
                let current_enemy = CreatureHandle(r.u32()?);
                let len = r.var_u32()? as usize;
                let mut remaining_enemies = SmallVec::with_capacity(len);
                for _ in 0..len {
                    remaining_enemies.push(CreatureHandle(r.u32()?));
                }
                ContinuationFrame::EnemyTurnResume {
                    current_enemy,
                    remaining_enemies,
                }
            }
            10 => {
                let len = r.var_u32()? as usize;
                let mut remaining_listeners = SmallVec::with_capacity(len);
                for _ in 0..len {
                    remaining_listeners.push(read_hook_listener(r)?);
                }
                let is_late = r.bool()?;
                ContinuationFrame::AfterAutoPrePlayResume {
                    remaining_listeners,
                    is_late,
                }
            }
            _ => return Err(()),
        };
        frames.push(frame);
    }
    Ok(Some(PendingContinuation { request, frames }))
}

#[inline]
fn write_relic(w: &mut ByteWriter<'_>, relic: &RelicInst) {
    let pf_empty = relic.private_fields.is_empty();
    w.ensure(14);
    unsafe {
        w.u16_unchecked(relic.id as u16);
        w.u8_unchecked(u8::from(relic.is_wax));
        w.u8_unchecked(u8::from(relic.is_melted));
        w.i32_unchecked(relic.stack_count);
        w.opt_i32_unchecked(relic.floor_added_to_deck);
        if pf_empty {
            w.u8_unchecked(0);
        }
    }
    if !pf_empty {
        w.cow_field_map_nonempty(&relic.private_fields);
    }
}

#[inline]
fn read_relic_into(
    r: &mut ByteReader<'_>,
    dr: &mut RelicInst,
    relics_hook_changed: &mut bool,
) -> Result<(), ()> {
    let rel_hdr = r.take(8)?;
    let id = decode_relic_id(slice_u16(rel_hdr, 0))?;
    let is_wax = rel_hdr[2] != 0;
    let is_melted = rel_hdr[3] != 0;
    let stack_count = slice_i32(rel_hdr, 4);
    let floor_added_to_deck = r.opt_i32()?;
    if dr.id != id || dr.is_melted != is_melted {
        *relics_hook_changed = true;
    }
    dr.id = id;
    dr.is_wax = is_wax;
    dr.is_melted = is_melted;
    dr.stack_count = stack_count;
    dr.floor_added_to_deck = floor_added_to_deck;
    r.cow_field_map_into(&mut dr.private_fields)
}

#[inline(never)]
fn write_card_slow(w: &mut ByteWriter<'_>, card: &CardInst, remaining_cards: usize) {
    let opt_mask: u16 = u16::from(card.upgrade_level != 0)
        | (u16::from(card.base_energy_cost != card.canonical_base_energy_cost) << 1)
        | (u16::from(card.captured_energy_x_value != 0) << 2)
        | (u16::from(card.base_replay_count != 0) << 3)
        | (u16::from(card.floor_added_to_deck.is_some()) << 4)
        | (u16::from(card.deck_card_handle.is_some()) << 5)
        | (u16::from(card.clone_of_handle.is_some()) << 6)
        | (u16::from(card.temp_star_cost_override_this_turn.is_some()) << 7)
        | (u16::from(card.temp_cost_override_this_turn.is_some()) << 8)
        | (u16::from(card.temp_cost_override_this_turn_or_until_played.is_some()) << 9)
        | (u16::from(card.temp_cost_override_this_combat.is_some()) << 10)
        | (u16::from(card.keywords_added_from_outside.bits() != 0) << 11)
        | (u16::from(card.keywords_removed_from_outside.bits() != 0) << 12)
        | (u16::from(card.temp_flags.bits() != 0) << 13)
        | (u16::from(card.affliction.is_some()) << 14)
        | (u16::from(card.ext_index.is_some()) << 15);
    let flags_byte = ((card.pile as u8) & 0x0F)
        | (u8::from(card.removed_from_state) << 4)
        | (u8::from(card.transform_into_after_play.is_some()) << 5);

    w.ensure(98 + remaining_cards * 8);
    unsafe {
        w.u8_unchecked(0);
        w.var_u32_unchecked(card.handle.0);
        w.u16_unchecked(card.id as u16);
        w.i32_unchecked(card.canonical_base_energy_cost as i32);
        w.u16_unchecked(card.local_keywords.bits());
        w.u16_unchecked(opt_mask);
        w.u8_unchecked(flags_byte);

        if opt_mask != 0 || (flags_byte & 0x20) != 0 {
            if (opt_mask & (1 << 0)) != 0 {
                w.i32_unchecked(card.upgrade_level);
            }
            if (opt_mask & (1 << 1)) != 0 {
                w.i32_unchecked(card.base_energy_cost as i32);
            }
            if (opt_mask & (1 << 2)) != 0 {
                w.i32_unchecked(card.captured_energy_x_value as i32);
            }
            if (opt_mask & (1 << 3)) != 0 {
                w.i32_unchecked(card.base_replay_count as i32);
            }
            if let Some(v) = card.floor_added_to_deck.get() {
                w.i32_unchecked(v);
            }
            if let Some(h) = card.deck_card_handle.get() {
                w.var_u32_unchecked(h.0);
            }
            if let Some(h) = card.clone_of_handle.get() {
                w.var_u32_unchecked(h.0);
            }
            if let Some(v) = card.temp_star_cost_override_this_turn.get() {
                w.i32_unchecked(v);
            }
            if let Some(v) = card.temp_cost_override_this_turn.get() {
                w.i32_unchecked(v);
            }
            if let Some(v) = card.temp_cost_override_this_turn_or_until_played.get() {
                w.i32_unchecked(v);
            }
            if let Some(v) = card.temp_cost_override_this_combat.get() {
                w.i32_unchecked(v);
            }
            if (opt_mask & (1 << 11)) != 0 {
                w.u16_unchecked(card.keywords_added_from_outside.bits());
            }
            if (opt_mask & (1 << 12)) != 0 {
                w.u16_unchecked(card.keywords_removed_from_outside.bits());
            }
            if (opt_mask & (1 << 13)) != 0 {
                w.u16_unchecked(card.temp_flags.bits());
            }
            if let Some(aff) = card.affliction {
                w.u16_unchecked(aff.id as u16);
                w.i32_unchecked(aff.amount);
            }
            if let Some(ei) = card.ext_index {
                w.var_u32_unchecked(ei);
            }
            if let Some(cid) = card.transform_into_after_play {
                w.u16_unchecked(cid as u16);
            }
        }
    }
}

#[inline(never)]
#[allow(clippy::too_many_arguments)]
fn write_damage_history_slow(
    w: &mut ByteWriter<'_>,
    dh: &DamageHistoryEntry,
    flags: u16,
    recv: u32,
    orig: u32,
    tn_len: usize,
    tn0: i32,
    remaining_dh: usize,
) {
    unsafe {
        w.u16_unchecked(flags);
        w.u8_unchecked(dh.props.bits());
        w.u8_unchecked(dh.current_side as u8);
        w.var_u32_unchecked(recv);
        if let Some(dealer) = dh.dealer_creature_handle {
            w.var_u32_unchecked(dealer.0);
        }
        if orig != recv {
            w.var_u32_unchecked(orig);
        }
        if dh.intended_hp_loss != 0 {
            w.var_i32_unchecked(dh.intended_hp_loss);
        }
        if dh.blocked_damage != 0 {
            w.var_i32_unchecked(dh.blocked_damage);
        }
        if dh.unblocked_damage != 0 {
            w.var_i32_unchecked(dh.unblocked_damage);
        }
        if dh.overkill_damage != 0 {
            w.var_i32_unchecked(dh.overkill_damage);
        }
        if let Some(cs) = dh.card_source_handle {
            w.var_u32_unchecked(cs.0);
        }
        w.var_i32_unchecked(dh.round_number);
        if tn_len == 1 {
            w.var_i32_unchecked(tn0);
        }
    }
    if tn_len > 1 {
        w.ensure(5 + tn_len * 5 + remaining_dh * 64);
        unsafe {
            w.var_u32_unchecked(tn_len as u32);
            for &tn in &dh.player_turn_numbers {
                w.var_i32_unchecked(tn);
            }
        }
    }
}

/// Packs the entire mutable `BattleState` into a compact variable-length binary record.
/// Static run context (`static_ctx`) is shared via `Arc<RootStaticContext>` and not duplicated per node.
pub fn pack_state(state: &BattleState, out: &mut Vec<u8>) {
    out.clear();
    let sctx: &RootStaticContext = &state.static_ctx;
    let prefix_len = sctx.root_deck_prefix_len;
    let deck_unchanged =
        !state.deck_dirty.0 && prefix_len > 0 && state.cards.len() >= prefix_len;
    let card_start = if deck_unchanged { prefix_len } else { 0 };
    let cards_to_pack = state.cards.len() - card_start;
    let est_size = 512
        + cards_to_pack * 10
        + state.creatures.len() * 64
        + state.damage_history.len() * 12;
    out.reserve(est_size);
    let mut w = ByteWriter::new(out);

    let seed_matches_root = Arc::ptr_eq(&state.run_rngs.string_seed, &sctx.root_run_rng_string_seed)
        || *state.run_rngs.string_seed == *sctx.root_run_rng_string_seed;
    #[cfg(debug_assertions)]
    if deck_unchanged {
        debug_assert_eq!(
            &state.cards[..prefix_len],
            &sctx.root_deck_cards[..],
            "deck_dirty was false despite modified root deck cards"
        );
        debug_assert_eq!(
            state.player.deck.as_slice(),
            sctx.root_deck_handles.as_slice(),
            "deck_dirty was false despite modified player deck handles"
        );
    }
    let u16_handles = state.next_card_handle <= u16::MAX as u32;
    let relics_delta = sctx.has_root_relics
        && state.player.relics.len() == sctx.root_relics.len()
        && state.player.relics.len() <= 64;
    let pack_flags = u8::from(seed_matches_root)
        | (u8::from(deck_unchanged) << 1)
        | (u8::from(u16_handles) << 2)
        | (u8::from(relics_delta) << 3);

    // Header scalars (batched direct pointer writes)
    w.ensure(45);
    unsafe {
        w.u8_unchecked(state.current_side as u8);
        w.i32_unchecked(state.round_number);
        w.u8_unchecked(u8::from(state.is_in_progress));
        w.u8_unchecked(u8::from(state.is_starting));
        w.u8_unchecked(u8::from(state.won));
        w.u8_unchecked(u8::from(state.is_player_extra_turn));
        w.u8_unchecked(u8::from(state.gold_was_stolen));
        w.u8_unchecked(u8::from(state.end_player_turn_requested));
        w.i32_unchecked(state.card_action_boundary_depth);
        w.u32_unchecked(state.next_creature_id);
        w.var_u32_unchecked(state.initial_encounter_monster_count as u32);
        w.u32_unchecked(state.next_card_handle);
        w.u32_unchecked(state.next_creature_handle);
        w.u32_unchecked(state.next_power_handle);
        w.u64_unchecked(state.next_attack_id);
        w.u8_unchecked(pack_flags);
    }

    if !seed_matches_root {
        w.str(&state.run_rngs.string_seed);
    }
    let rs = &state.run_rngs.streams;
    let ps = &state.player_rngs.streams;
    let rng_delta_mask: u16 = if sctx.has_root_rngs {
        let mut m = 0u16;
        for i in 0..12 {
            if rs[i].counter != sctx.root_run_rng_counters[i] {
                m |= 1u16 << i;
            } else {
                #[cfg(debug_assertions)]
                debug_assert_eq!(
                    rs[i], sctx.root_run_rngs[i],
                    "run_rng[{i}] diverged without counter advance"
                );
            }
        }
        for i in 0..4 {
            if ps[i].counter != sctx.root_player_rng_counters[i] {
                m |= 1u16 << (12 + i);
            } else {
                #[cfg(debug_assertions)]
                debug_assert_eq!(
                    ps[i], sctx.root_player_rngs[i],
                    "player_rng[{i}] diverged without counter advance"
                );
            }
        }
        m
    } else {
        0xFFFF
    };
    let total_rngs = rng_delta_mask.count_ones() as usize;
    w.ensure(18 + total_rngs * 36);
    unsafe {
        w.u16_unchecked(rng_delta_mask);
        w.u64_unchecked(state.run_rngs.seed);
        let mut rem = rng_delta_mask & 0x0FFF;
        while rem != 0 {
            let i = rem.trailing_zeros() as usize;
            rem &= rem - 1;
            w.rng_unchecked(&rs[i]);
        }
        w.u64_unchecked(state.player_rngs.seed);
        let mut rem = rng_delta_mask >> 12;
        while rem != 0 {
            let i = rem.trailing_zeros() as usize;
            rem &= rem - 1;
            w.rng_unchecked(&ps[i]);
        }
    }

    // PlayerInst fixed scalars (51 bytes) + PlayerCounters (26 fields bitmask-packed)
    let p = &state.player;
    w.ensure(51);
    unsafe {
        w.u32_unchecked(p.creature_handle.0);
        w.i32_unchecked(p.current_hp);
        w.i32_unchecked(p.max_hp);
        w.i32_unchecked(p.block);
        w.i32_unchecked(p.cumulative_hp_lost);
        w.i32_unchecked(p.gold);
        w.i32_unchecked(p.max_energy);
        w.i32_unchecked(p.base_orb_slot_count);
        w.u8_unchecked(u8::from(p.is_active_for_hooks));
        w.u8_unchecked(u8::from(p.can_use_or_remove_potions));
        w.i32_unchecked(p.energy);
        w.i32_unchecked(p.stars);
        w.u8_unchecked(p.phase as u8);
        w.i32_unchecked(p.turn_number);
        w.i32_unchecked(p.card_or_potion_effect_depth);
    }

    write_player_counters(&mut w, &p.counters);

    if u16_handles {
        let total_handles = p.hand.len()
            + p.draw.len()
            + p.discard.len()
            + p.exhaust.len()
            + p.play.len()
            + if deck_unchanged { 0 } else { p.deck.len() };
        w.ensure(30 + total_handles * 2);
        unsafe {
            write_card_handles_u16_unchecked(&mut w, &p.hand);
            write_card_handles_u16_unchecked(&mut w, &p.draw);
            write_card_handles_u16_unchecked(&mut w, &p.discard);
            write_card_handles_u16_unchecked(&mut w, &p.exhaust);
            write_card_handles_u16_unchecked(&mut w, &p.play);
            if !deck_unchanged {
                write_card_handles_u16_unchecked(&mut w, &p.deck);
            }
        }
    } else {
        let total_handles = p.hand.len()
            + p.draw.len()
            + p.discard.len()
            + p.exhaust.len()
            + p.play.len()
            + if deck_unchanged { 0 } else { p.deck.len() };
        w.ensure(30 + total_handles * 4);
        unsafe {
            write_card_handles_unchecked(&mut w, &p.hand);
            write_card_handles_unchecked(&mut w, &p.draw);
            write_card_handles_unchecked(&mut w, &p.discard);
            write_card_handles_unchecked(&mut w, &p.exhaust);
            write_card_handles_unchecked(&mut w, &p.play);
            if !deck_unchanged {
                write_card_handles_unchecked(&mut w, &p.deck);
            }
        }
    }

    w.ensure(
        2 + 5
            + p.potion_slots.len() * 3
            + 4
            + 5
            + p.orbs.len() * 35
            + 5
            + p.arrows.len() * 2,
    );
    unsafe {
        w.u16_unchecked(p.combat_generated_potion_mask);
        w.var_u32_unchecked(p.potion_slots.len() as u32);
        for &slot in &p.potion_slots {
            match slot {
                Some(pid) => {
                    w.u8_unchecked(1);
                    w.u16_unchecked(pid as u16);
                }
                None => w.u8_unchecked(0),
            }
        }

        w.i32_unchecked(p.orb_capacity);
        w.var_u32_unchecked(p.orbs.len() as u32);
        for (orb_idx, orb) in p.orbs.iter().enumerate() {
            if orb.raw_evoke_value.is_none() && orb.raw_passive_value.is_none() {
                debug_assert!((orb.id as usize) < 0x80);
                w.u8_unchecked((orb.id as u8) | 0x80);
                continue;
            }
            let tail_after = (p.orbs.len() - orb_idx - 1) * 35 + 5 + p.arrows.len() * 2;
            w.u8_unchecked(orb.id as u8);
            match orb.raw_evoke_value {
                Some(d) => {
                    w.u8_unchecked(1);
                    w.dec_unchecked_with_tail(d, tail_after + 15);
                }
                None => w.u8_unchecked(0),
            }
            match orb.raw_passive_value {
                Some(d) => {
                    w.u8_unchecked(1);
                    w.dec_unchecked_with_tail(d, tail_after);
                }
                None => w.u8_unchecked(0),
            }
        }

        w.var_u32_unchecked(p.arrows.len() as u32);
        for arrow in &p.arrows {
            w.u8_unchecked(arrow.status as u8);
            w.u8_unchecked(u8::from(arrow.destroy_after_impaled));
        }
    }

    if relics_delta {
        debug_assert_eq!(sctx.root_relic_headers.len(), sctx.root_relics.len());
        let relic_len = p.relics.len();
        let valid_mask = if relic_len == 64 {
            u64::MAX
        } else {
            (1u64 << relic_len) - 1
        };
        #[cfg(debug_assertions)]
        for i in 0..relic_len {
            if (state.relic_dirty_mask.0 & (1u64 << i)) == 0 {
                let cur = &p.relics[i];
                let hdr = &sctx.root_relic_headers[i];
                debug_assert!(
                    hdr.matches_relic(cur)
                        && ((hdr.pf_empty && cur.private_fields.is_empty())
                            || (!hdr.pf_empty
                                && !cur.private_fields.is_empty()
                                && cur.private_fields == sctx.root_relics[i].private_fields)),
                    "relic[{i}] modified without mark_relic_dirty: cur={cur:?} root={:?}",
                    sctx.root_relics[i]
                );
            }
        }
        let mut rmask = 0u64;
        let mut cand = state.relic_dirty_mask.0 & valid_mask;
        while cand != 0 {
            let i = cand.trailing_zeros() as usize;
            cand &= cand - 1;
            let cur = &p.relics[i];
            let hdr = &sctx.root_relic_headers[i];
            let same = hdr.matches_relic(cur)
                && ((hdr.pf_empty && cur.private_fields.is_empty())
                    || (!hdr.pf_empty
                        && !cur.private_fields.is_empty()
                        && cur.private_fields == sctx.root_relics[i].private_fields));
            if !same {
                rmask |= 1u64 << i;
            }
        }
        w.u64(rmask);
        let mut rem = rmask;
        while rem != 0 {
            let i = rem.trailing_zeros() as usize;
            rem &= rem - 1;
            write_relic(&mut w, &p.relics[i]);
        }
    } else {
        w.var_u32(p.relics.len() as u32);
        for relic in &p.relics {
            write_relic(&mut w, relic);
        }
    }

    // Cards (compact 8-byte single-u64 record / 13-byte afflicted fast record / full fallback)
    let root_floors = sctx.root_card_floors.as_slice();
    let root_floors_len = root_floors.len();
    w.ensure(5 + cards_to_pack * 8);
    unsafe {
        w.var_u32_unchecked(cards_to_pack as u32);
    }
    for (idx, card) in state.cards[card_start..].iter().enumerate() {
        let card_abs_idx = card_start + idx;
        let floor_raw = card.floor_added_to_deck.raw();
        let use_root_floor = floor_raw != 0;
        let shape_hdr_fast = card.handle.0 == (card_abs_idx + 1) as u32
            && card.clone_of_handle.is_none()
            && card.transform_into_after_play.is_none()
            && ((card.upgrade_level as u32) & !1u32) == 0
            && (((card.canonical_base_energy_cost as i32 + 128) as u32) & !0xFFu32) == 0
            && (card.deck_card_handle.raw() >> 16) == 0
            && (!use_root_floor
                || (card_abs_idx < root_floors_len
                    && floor_raw == root_floors[card_abs_idx].raw()));
        if shape_hdr_fast {
            let base_flags = ((card.pile as u8) & 0x0F)
                | (u8::from(card.removed_from_state) << 4)
                | (u8::from(card.upgrade_level != 0) << 5)
                | (u8::from(use_root_floor) << 6);
            let cost_byte = card.canonical_base_energy_cost as i8 as u8;
            let deck_h = card.deck_card_handle.raw() as u16;
            let rare_clean = card.is_rare_clean();
            if card.ext_index.is_none() && rare_clean {
                if card.base_energy_cost == card.canonical_base_energy_cost {
                    if card.affliction.is_none() {
                        let word = ((0x80u8 | base_flags) as u64)
                            | ((cost_byte as u64) << 8)
                            | ((card.id as u16 as u64) << 16)
                            | ((card.local_keywords.bits() as u64) << 32)
                            | ((deck_h as u64) << 48);
                        unsafe {
                            w.u64_unchecked(word);
                        }
                        continue;
                    }
                    if let Some(aff) = card.affliction {
                        if (-32768..=32767).contains(&aff.amount) {
                            w.ensure(13 + (cards_to_pack - idx - 1) * 8);
                            let word = (base_flags as u64)
                                | ((cost_byte as u64) << 8)
                                | ((card.id as u16 as u64) << 16)
                                | ((card.local_keywords.bits() as u64) << 32)
                                | ((deck_h as u64) << 48);
                            let aff_word = (aff.id as u16 as u32)
                                | (((aff.amount as i16 as u16) as u32) << 16);
                            unsafe {
                                w.u8_unchecked(1);
                                w.u64_unchecked(word);
                                w.u32_unchecked(aff_word);
                            }
                            continue;
                        }
                    }
                } else if card.affliction.is_none()
                    && (((card.base_energy_cost as i32 + 128) as u32) & !0xFFu32) == 0
                {
                    w.ensure(10 + (cards_to_pack - idx - 1) * 8);
                    let word = (base_flags as u64)
                        | ((cost_byte as u64) << 8)
                        | ((card.id as u16 as u64) << 16)
                        | ((card.local_keywords.bits() as u64) << 32)
                        | ((deck_h as u64) << 48);
                    let actual_cost_byte = card.base_energy_cost as i8 as u8;
                    unsafe {
                        w.u8_unchecked(2);
                        w.u64_unchecked(word);
                        w.u8_unchecked(actual_cost_byte);
                    }
                    continue;
                }
            } else if rare_clean
                && card.affliction.is_none()
                && card.base_energy_cost == card.canonical_base_energy_cost
            {
                if let Some(ei) = card.ext_index {
                    if ei <= u16::MAX as u32 {
                        w.ensure(11 + (cards_to_pack - idx - 1) * 8);
                        let word = (base_flags as u64)
                            | ((cost_byte as u64) << 8)
                            | ((card.id as u16 as u64) << 16)
                            | ((card.local_keywords.bits() as u64) << 32)
                            | ((deck_h as u64) << 48);
                        unsafe {
                            w.u8_unchecked(3);
                            w.u64_unchecked(word);
                            w.u16_unchecked(ei as u16);
                        }
                        continue;
                    }
                }
            } else if card.ext_index.is_none()
                && card.affliction.is_none()
                && card.base_energy_cost == card.canonical_base_energy_cost
                && (card.keywords_added_from_outside.bits()
                    | card.keywords_removed_from_outside.bits()
                    | (card.captured_energy_x_value as u16)
                    | (card.base_replay_count as u16)
                    | card.temp_star_cost_override_this_turn.raw()
                    | card.temp_cost_override_this_turn.raw()
                    | card.temp_cost_override_this_turn_or_until_played.raw()
                    | card.temp_cost_override_this_combat.raw())
                    == 0
            {
                w.ensure(11 + (cards_to_pack - idx - 1) * 8);
                let word = (base_flags as u64)
                    | ((cost_byte as u64) << 8)
                    | ((card.id as u16 as u64) << 16)
                    | ((card.local_keywords.bits() as u64) << 32)
                    | ((deck_h as u64) << 48);
                unsafe {
                    w.u8_unchecked(4);
                    w.u64_unchecked(word);
                    w.u16_unchecked(card.temp_flags.bits());
                }
                continue;
            }
        }

        write_card_slow(&mut w, card, cards_to_pack - idx - 1);
    }

    // CardExtensions
    w.var_u32(state.card_extensions.len() as u32);
    for ext in &state.card_extensions {
        let epf_empty = ext
            .enchantment
            .as_ref()
            .is_none_or(|e| e.private_fields.is_empty());
        let cpf_empty = ext.private_fields.is_empty();
        w.ensure(
            5 + ext.local_energy_modifiers.len() * 6 + 9 + usize::from(epf_empty && cpf_empty),
        );
        unsafe {
            w.var_u32_unchecked(ext.local_energy_modifiers.len() as u32);
            for m in &ext.local_energy_modifiers {
                w.u8_unchecked(m.kind as u8);
                w.i32_unchecked(m.amount);
                w.u8_unchecked(u8::from(m.is_reduce_only));
            }
        }
        match &ext.enchantment {
            Some(e) => {
                unsafe {
                    w.u8_unchecked(1);
                    w.u16_unchecked(e.id as u16);
                    w.i32_unchecked(e.amount);
                    w.u8_unchecked(e.status as u8);
                    if epf_empty {
                        w.u8_unchecked(0);
                    }
                }
                if !epf_empty {
                    w.cow_field_map_nonempty(&e.private_fields);
                }
            }
            None => unsafe {
                w.u8_unchecked(0);
            },
        }
        if epf_empty && cpf_empty {
            unsafe {
                w.u8_unchecked(0);
            }
        } else {
            w.cow_field_map(&ext.private_fields);
        }
    }

    // Creatures
    w.var_u32(state.creatures.len() as u32);
    for cr in &state.creatures {
        const PTR_BYTES: usize = std::mem::size_of::<usize>();
        let per_str = 1 + PTR_BYTES;
        w.ensure(10 + per_str + 27);
        unsafe {
            w.u32_unchecked(cr.handle.0);
            w.opt_u32_unchecked(cr.combat_id);
            w.u8_unchecked(cr.side as u8);
            w.opt_cow_str_unchecked_with_tail(cr.slot_name.as_ref(), 27);
            w.u8_unchecked(u8::from(cr.is_player));
            w.opt_u32_unchecked(cr.pet_owner_player_index.map(|i| i as u32));
            match cr.monster_id {
                Some(mid) => {
                    w.u8_unchecked(1);
                    w.u16_unchecked(mid as u16);
                }
                None => w.u8_unchecked(0),
            }
            w.i32_unchecked(cr.current_hp);
            w.i32_unchecked(cr.max_hp);
            w.i32_unchecked(cr.block);
            w.i32_unchecked(cr.cumulative_hp_lost);
            w.u8_unchecked(u8::from(cr.is_detached));
            if cr.monster.is_none() {
                w.u8_unchecked(0);
            }
        }

        if let Some(m) = &cr.monster {
            let log_len = m.state_log_ids.len();
            let sf_empty = m.setup_fields.is_empty();
            let pf_empty = m.private_fields.is_empty();
            let after_csid = per_str + 2 + 5 + log_len * per_str + per_str + 2;
            w.ensure(41 + per_str + after_csid);
            unsafe {
                w.u8_unchecked(1);
                w.u16_unchecked(m.monster_id as u16);
                w.i32_unchecked(m.rng.counter);
                w.u64_unchecked(m.rng.random.s0);
                w.u64_unchecked(m.rng.random.s1);
                w.u64_unchecked(m.rng.random.s2);
                w.u64_unchecked(m.rng.random.s3);
                w.u8_unchecked(u8::from(m.is_performing_move));
                w.u8_unchecked(u8::from(m.spawned_this_turn));
                w.cow_str_unchecked_with_tail(&m.current_state_id, after_csid);
                w.opt_cow_str_unchecked_with_tail(
                    m.next_move_id.as_ref(),
                    2 + 5 + log_len * per_str + per_str + 2,
                );
                w.u8_unchecked(u8::from(m.next_move_can_transition_away));
                w.u8_unchecked(u8::from(m.performed_first_move));
                w.var_u32_unchecked(log_len as u32);
                for (s_idx, s) in m.state_log_ids.iter().enumerate() {
                    w.cow_str_unchecked_with_tail(
                        s,
                        (log_len - s_idx - 1) * per_str + per_str + 2,
                    );
                }
                w.opt_cow_str_unchecked_with_tail(m.transient_move_follow_up_state_id.as_ref(), 2);
                if sf_empty && pf_empty {
                    w.u16_unchecked(0);
                }
            }
            if !sf_empty || !pf_empty {
                w.cow_field_map(&m.setup_fields);
                w.cow_field_map(&m.private_fields);
            }
        }

        w.ensure(5 + cr.powers.len() * 23);
        unsafe {
            w.var_u32_unchecked(cr.powers.len() as u32);
            for pw in &cr.powers {
                w.u32_unchecked(pw.handle);
                w.u16_unchecked(pw.id as u16);
                w.i32_unchecked(pw.amount);
                w.i32_unchecked(pw.amount_on_turn_start);
                let pflags = u8::from(pw.skip_next_duration_tick)
                    | (u8::from(pw.applier_handle.is_some()) << 1)
                    | (u8::from(pw.ext_index.is_some()) << 2);
                w.u8_unchecked(pflags);
                if let Some(ah) = pw.applier_handle {
                    w.u32_unchecked(ah.0);
                }
                if let Some(ei) = pw.ext_index {
                    w.u32_unchecked(ei);
                }
            }
        }
    }

    // PowerExtensions
    w.var_u32(state.power_extensions.len() as u32);
    for pe in &state.power_extensions {
        let pe_pf_empty = pe.private_fields.is_empty();
        if pe_pf_empty {
            w.ensure(1 + 22 + pe.impaled_arrows.len() * 5);
            unsafe {
                w.u8_unchecked(0);
            }
        } else {
            w.cow_field_map_nonempty(&pe.private_fields);
            w.ensure(22 + pe.impaled_arrows.len() * 5);
        }
        unsafe {
            match pe.attack_command_id {
                Some(x) => {
                    w.u8_unchecked(1);
                    w.u64_unchecked(x);
                }
                None => w.u8_unchecked(0),
            }
            w.i32_unchecked(pe.amount_when_attack_started);
            w.var_u32_unchecked(pe.impaled_arrows.len() as u32);
            for &idx in &pe.impaled_arrows {
                w.var_u32_unchecked(idx as u32);
            }
            w.i32_unchecked(pe.hit_counter);
        }
    }

    // Handle lists
    let extra_h_mask = u8::from(!state.escaped_handles.is_empty())
        | (u8::from(!state.removed_handles.is_empty()) << 1)
        | (u8::from(!state.spawned_enemy_handles.is_empty()) << 2)
        | (u8::from(!state.pet_handles.is_empty()) << 3);
    let total_cr_handles = state.ally_handles.len()
        + state.enemy_handles.len()
        + state.escaped_handles.len()
        + state.removed_handles.len()
        + state.spawned_enemy_handles.len()
        + state.pet_handles.len();
    w.ensure(31 + total_cr_handles * 4);
    unsafe {
        write_creature_handles_unchecked(&mut w, &state.ally_handles);
        write_creature_handles_unchecked(&mut w, &state.enemy_handles);
        w.u8_unchecked(extra_h_mask);
        if (extra_h_mask & 1) != 0 {
            write_creature_handles_unchecked(&mut w, &state.escaped_handles);
        }
        if (extra_h_mask & 2) != 0 {
            write_creature_handles_unchecked(&mut w, &state.removed_handles);
        }
        if (extra_h_mask & 4) != 0 {
            write_creature_handles_unchecked(&mut w, &state.spawned_enemy_handles);
        }
        if (extra_h_mask & 8) != 0 {
            write_creature_handles_unchecked(&mut w, &state.pet_handles);
        }
    }

    // Damage history
    let dh_len = state.damage_history.len();
    w.ensure(5 + dh_len * 64);
    unsafe {
        w.var_u32_unchecked(dh_len as u32);
    }
    for (idx, dh) in state.damage_history.iter().enumerate() {
        let recv = dh.receiver_creature_handle.0;
        let orig = dh.original_target_creature_handle.0;
        let d_raw = dh.dealer_creature_handle.map_or(0, |h| h.0);
        let cs_raw = dh.card_source_handle.map_or(0, |h| h.0);
        let tn_len = dh.player_turn_numbers.len();
        let tn0 = if tn_len == 1 {
            dh.player_turn_numbers[0]
        } else {
            0
        };
        let or_i = dh.intended_hp_loss
            | dh.blocked_damage
            | dh.unblocked_damage
            | dh.overkill_damage
            | dh.round_number
            | tn0;
        let fast_small = tn_len <= 1 && ((recv | orig | d_raw | cs_raw) < 128) && or_i >= 0 && or_i < 64;
        let flags: u16 = u16::from(dh.dealer_creature_handle.is_some())
            | (u16::from(orig != recv) << 1)
            | (u16::from(dh.intended_hp_loss != 0) << 2)
            | (u16::from(dh.blocked_damage != 0) << 3)
            | (u16::from(dh.unblocked_damage != 0) << 4)
            | (u16::from(dh.overkill_damage != 0) << 5)
            | (u16::from(dh.was_target_killed) << 6)
            | (u16::from(dh.was_block_broken) << 7)
            | (u16::from(dh.was_fully_blocked) << 8)
            | (u16::from(dh.card_source_handle.is_some()) << 9)
            | (u16::from(tn_len == 1) << 10)
            | (u16::from(tn_len > 1) << 11)
            | (u16::from(fast_small) << 12);
        if fast_small {
            let hdr = (flags as u32)
                | ((dh.props.bits() as u32) << 16)
                | ((dh.current_side as u32) << 24);
            unsafe {
                let mut p = w.cur;
                std::ptr::write_unaligned(p.cast::<u32>(), hdr.to_le());
                p = p.add(4);
                *p = recv as u8;
                p = p.add(1);
                *p = d_raw as u8;
                p = p.add(usize::from(dh.dealer_creature_handle.is_some()));
                *p = orig as u8;
                p = p.add(usize::from(orig != recv));
                *p = dh.intended_hp_loss as u8;
                p = p.add(usize::from(dh.intended_hp_loss != 0));
                *p = dh.blocked_damage as u8;
                p = p.add(usize::from(dh.blocked_damage != 0));
                *p = dh.unblocked_damage as u8;
                p = p.add(usize::from(dh.unblocked_damage != 0));
                *p = dh.overkill_damage as u8;
                p = p.add(usize::from(dh.overkill_damage != 0));
                *p = cs_raw as u8;
                p = p.add(usize::from(dh.card_source_handle.is_some()));
                *p = dh.round_number as u8;
                p = p.add(1);
                *p = tn0 as u8;
                p = p.add(usize::from(tn_len == 1));
                w.cur = p;
            }
            continue;
        }
        write_damage_history_slow(&mut w, dh, flags, recv, orig, tn_len, tn0, dh_len - idx - 1);
    }

    // Semantic history
    let sh = &state.semantic_history;
    let sh_mask: u8 = u8::from(!sh.exhausts.is_empty())
        | (u8::from(!sh.card_block_gains.is_empty()) << 1)
        | (u8::from(!sh.orb_channels.is_empty()) << 2)
        | (u8::from(!sh.energy_spent.is_empty()) << 3)
        | (u8::from(!sh.card_drawn.is_empty()) << 4)
        | (u8::from(!sh.actor_events.is_empty()) << 5);
    w.u8(sh_mask);
    if sh_mask != 0 {
        let mut prev_valid = false;
        let mut prev_tk = TurnKey {
            round_number: 0,
            side: CombatSide::Player,
            player_turn_numbers: InlineOptI32Vec4::new(),
        };
        if (sh_mask & (1 << 0)) != 0 {
            w.ensure(5 + sh.exhausts.len() * 32);
            unsafe {
                w.var_u32_unchecked(sh.exhausts.len() as u32);
                for (tk, idx) in &sh.exhausts {
                    if *idx < 0x80 && prev_valid && prev_tk == *tk {
                        w.u16_unchecked((*idx as u16) << 8);
                    } else {
                        write_turn_key_delta_unchecked(&mut w, tk, &mut prev_valid, &mut prev_tk);
                        w.var_u32_unchecked(*idx as u32);
                    }
                }
            }
        }
        if (sh_mask & (1 << 1)) != 0 {
            w.ensure(5 + sh.card_block_gains.len() * 36);
            unsafe {
                w.var_u32_unchecked(sh.card_block_gains.len() as u32);
                for (tk, idx, ord) in &sh.card_block_gains {
                    if *idx < 0x80 && prev_valid && prev_tk == *tk {
                        if (-32768..=32767).contains(ord) {
                            w.u32_unchecked(
                                3u32
                                    | ((*idx as u32) << 8)
                                    | (((*ord as i16 as u16) as u32) << 16),
                            );
                            continue;
                        }
                        w.u16_unchecked((*idx as u16) << 8);
                    } else {
                        write_turn_key_delta_unchecked(&mut w, tk, &mut prev_valid, &mut prev_tk);
                        w.var_u32_unchecked(*idx as u32);
                    }
                    w.i32_unchecked(*ord);
                }
            }
        }
        if (sh_mask & (1 << 2)) != 0 {
            w.ensure(5 + sh.orb_channels.len() * 33);
            unsafe {
                w.var_u32_unchecked(sh.orb_channels.len() as u32);
                for (tk, idx, oid) in &sh.orb_channels {
                    if *idx < 0x80 && prev_valid && prev_tk == *tk {
                        w.u16_unchecked((*idx as u16) << 8);
                    } else {
                        write_turn_key_delta_unchecked(&mut w, tk, &mut prev_valid, &mut prev_tk);
                        w.var_u32_unchecked(*idx as u32);
                    }
                    w.u8_unchecked(*oid as u8);
                }
            }
        }
        if (sh_mask & (1 << 3)) != 0 {
            w.ensure(5 + sh.energy_spent.len() * 36);
            unsafe {
                w.var_u32_unchecked(sh.energy_spent.len() as u32);
                for (tk, idx, amt) in &sh.energy_spent {
                    if *idx < 0x80 && prev_valid && prev_tk == *tk {
                        if (-32768..=32767).contains(amt) {
                            w.u32_unchecked(
                                3u32
                                    | ((*idx as u32) << 8)
                                    | (((*amt as i16 as u16) as u32) << 16),
                            );
                            continue;
                        }
                        w.u16_unchecked((*idx as u16) << 8);
                    } else {
                        write_turn_key_delta_unchecked(&mut w, tk, &mut prev_valid, &mut prev_tk);
                        w.var_u32_unchecked(*idx as u32);
                    }
                    w.i32_unchecked(*amt);
                }
            }
        }
        if (sh_mask & (1 << 4)) != 0 {
            w.ensure(5 + sh.card_drawn.len() * 34);
            unsafe {
                w.var_u32_unchecked(sh.card_drawn.len() as u32);
                for (tk, idx, ct, from_hand) in &sh.card_drawn {
                    if *idx < 0x80 && prev_valid && prev_tk == *tk {
                        w.u32_unchecked(
                            ((*idx as u32) << 8)
                                | ((*ct as u32) << 16)
                                | (u32::from(*from_hand) << 24),
                        );
                    } else {
                        write_turn_key_delta_unchecked(&mut w, tk, &mut prev_valid, &mut prev_tk);
                        w.var_u32_unchecked(*idx as u32);
                        w.u8_unchecked(*ct as u8);
                        w.u8_unchecked(u8::from(*from_hand));
                    }
                }
            }
        }
        if (sh_mask & (1 << 5)) != 0 {
            const PTR_BYTES: usize = std::mem::size_of::<usize>();
            let per_ev = 1 + PTR_BYTES + 27 + 5;
            let ae_len = sh.actor_events.len();
            w.ensure(5 + ae_len * per_ev);
            unsafe {
                w.var_u32_unchecked(ae_len as u32);
                for (ev_idx, (kind, tk, idx)) in sh.actor_events.iter().enumerate() {
                    let tail = (ae_len - ev_idx - 1) * per_ev + 27 + 5;
                    w.cow_str_unchecked_with_tail(kind, tail);
                    write_turn_key_delta_unchecked(&mut w, tk, &mut prev_valid, &mut prev_tk);
                    w.var_u32_unchecked(*idx as u32);
                }
            }
        }
    }
    let gr = &sh.growth_rewards;
    if gr.is_zero() && sh.brightest_flame_max_hp_spent == 0 {
        w.ensure(16);
        unsafe {
            w.u8_unchecked(0);
            w.var_i32_unchecked(sh.prior_player_unblocked_hits);
            w.var_i32_unchecked(sh.prior_player_damaged_turn);
            w.var_i32_unchecked(sh.prior_exhaust_count);
        }
    } else {
        w.ensure(60);
        unsafe {
            w.u8_unchecked(1);
            w.i32_unchecked(gr.hand_of_greed);
            w.i32_unchecked(gr.the_hunt);
            w.i32_unchecked(gr.feed);
            w.i32_unchecked(gr.royalties);
            w.i32_unchecked(gr.alchemize);
            w.i32_unchecked(gr.genetic_algorithm);
            w.i32_unchecked(gr.the_scythe);
            w.i32_unchecked(gr.goopy);
            w.i32_unchecked(gr.forbidden_grimoire);
            w.i32_unchecked(gr.mad_science);
            w.i32_unchecked(sh.brightest_flame_max_hp_spent);
            w.var_i32_unchecked(sh.prior_player_unblocked_hits);
            w.var_i32_unchecked(sh.prior_player_damaged_turn);
            w.var_i32_unchecked(sh.prior_exhaust_count);
        }
    }

    // PendingContinuation
    if state.pending.is_none() {
        w.ensure(2);
        unsafe {
            w.u16_unchecked((state.vakuu_auto_select_depth as u16) << 8);
        }
    } else {
        write_pending(&mut w, &state.pending);
        w.u8(state.vakuu_auto_select_depth);
    }
}

#[inline]
fn read_powers_into(r: &mut ByteReader<'_>, powers: &mut Vec<PowerInst>) -> Result<bool, ()> {
    let p_len = r.var_u32()? as usize;
    powers.clear();
    let mut has_minion_power = false;
    if p_len != 0 {
        powers.reserve(p_len);
        let dst_ptr = powers.as_mut_ptr();
        let mut guard = SetLenOnDrop {
            vec: powers,
            base_len: 0,
            written: 0,
        };
        for idx in 0..p_len {
            let praw = r.take(15)?;
            let handle = slice_u32(praw, 0);
            let id = decode_power_id(slice_u16(praw, 4))?;
            if id == PowerId::MinionPower {
                has_minion_power = true;
            }
            let pflags = praw[14];
            let skip_next_duration_tick = (pflags & 1) != 0;
            let applier_handle = if (pflags & 2) != 0 {
                Some(CreatureHandle(r.u32()?))
            } else {
                None
            };
            let ext_index = if (pflags & 4) != 0 {
                Some(r.u32()?)
            } else {
                None
            };
            unsafe {
                dst_ptr.add(idx).write(PowerInst {
                    handle,
                    id,
                    amount: slice_i32(praw, 6),
                    amount_on_turn_start: slice_i32(praw, 10),
                    skip_next_duration_tick,
                    applier_handle,
                    ext_index,
                });
            }
            guard.written += 1;
        }
    }
    Ok(has_minion_power)
}

/// Unpacks a packed binary state directly into a reusable `BattleState` scratch buffer.
pub fn unpack_state(
    bytes: &[u8],
    static_ctx: &Arc<RootStaticContext>,
    out: &mut BattleState,
) -> Result<(), KernelError> {
    let mut r = ByteReader::new(bytes);
    unpack_state_inner(&mut r, static_ctx, out)
        .map_err(|()| KernelError::InvalidState(format!("state unpack error at {}", r.pos())))
}

fn unpack_state_inner(
    r: &mut ByteReader<'_>,
    static_ctx: &Arc<RootStaticContext>,
    out: &mut BattleState,
) -> Result<(), ()> {
    let same_root = Arc::ptr_eq(&out.static_ctx, static_ctx);
    let prev_deck_dirty = out.deck_dirty.0;
    let prev_dirty = out.hook_cache.dirty_mask;
    if !same_root {
        out.static_ctx = Arc::clone(static_ctx);
    }
    let sctx: &RootStaticContext = static_ctx;
    let hdr0 = r.take(19)?;
    out.current_side = decode_combat_side(hdr0[0])?;
    out.round_number = slice_i32(hdr0, 1);
    out.is_in_progress = hdr0[5] != 0;
    out.is_starting = hdr0[6] != 0;
    out.won = hdr0[7] != 0;
    out.is_player_extra_turn = hdr0[8] != 0;
    out.gold_was_stolen = hdr0[9] != 0;
    out.end_player_turn_requested = hdr0[10] != 0;
    out.card_action_boundary_depth = slice_i32(hdr0, 11);
    out.next_creature_id = slice_u32(hdr0, 15);
    out.initial_encounter_monster_count = r.var_u32()? as usize;
    let hdr1 = r.take(21)?;
    out.next_card_handle = slice_u32(hdr1, 0);
    out.next_creature_handle = slice_u32(hdr1, 4);
    out.next_power_handle = slice_u32(hdr1, 8);
    out.next_attack_id = slice_u64(hdr1, 12);

    let pack_flags = hdr1[20];
    let seed_matches_root = (pack_flags & 1) != 0;
    let deck_unchanged = (pack_flags & 2) != 0;
    let u16_handles = (pack_flags & 4) != 0;
    let relics_delta = (pack_flags & 8) != 0;
    if seed_matches_root {
        if !same_root || !Arc::ptr_eq(&out.run_rngs.string_seed, &sctx.root_run_rng_string_seed) {
            out.run_rngs.string_seed = Arc::clone(&sctx.root_run_rng_string_seed);
        }
    } else {
        let s = r.str()?;
        out.run_rngs.string_seed = Arc::from(s.as_str());
    }
    let rng_delta_mask = r.u16()?;
    out.run_rngs.seed = r.u64()?;
    for i in 0..12 {
        out.run_rngs.streams[i] = if (rng_delta_mask & (1u16 << i)) != 0 {
            r.rng()?
        } else {
            sctx.root_run_rngs[i]
        };
    }

    out.player_rngs.seed = r.u64()?;
    for i in 0..4 {
        out.player_rngs.streams[i] = if (rng_delta_mask & (1u16 << (12 + i))) != 0 {
            r.rng()?
        } else {
            sctx.root_player_rngs[i]
        };
    }

    // PlayerInst
    let p_raw = r.take(55)?;
    let p = &mut out.player;
    p.creature_handle = CreatureHandle(slice_u32(p_raw, 0));
    p.current_hp = slice_i32(p_raw, 4);
    p.max_hp = slice_i32(p_raw, 8);
    p.block = slice_i32(p_raw, 12);
    p.cumulative_hp_lost = slice_i32(p_raw, 16);
    p.gold = slice_i32(p_raw, 20);
    p.max_energy = slice_i32(p_raw, 24);
    p.base_orb_slot_count = slice_i32(p_raw, 28);
    p.is_active_for_hooks = p_raw[32] != 0;
    p.can_use_or_remove_potions = p_raw[33] != 0;
    p.energy = slice_i32(p_raw, 34);
    p.stars = slice_i32(p_raw, 38);
    p.phase = decode_player_turn_phase(p_raw[42])?;
    p.turn_number = slice_i32(p_raw, 43);
    p.card_or_potion_effect_depth = slice_i32(p_raw, 47);

    let c_mask = slice_u32(p_raw, 51);
    if c_mask == 0 {
        p.counters = PlayerCounters::default();
    } else {
        let stars_gained_this_turn = if (c_mask & 1) != 0 {
            r.dec()?
        } else {
            Dec::ZERO
        };
        let n_i32 = (c_mask & 0xFFFF_FFFE).count_ones() as usize;
        let c_raw = r.take(n_i32 * 4)?;
        let mut off = 0usize;
        macro_rules! take_bit_i32 {
            ($bit:expr) => {{
                if (c_mask & (1 << $bit)) != 0 {
                    let v = slice_i32(c_raw, off);
                    off += 4;
                    v
                } else {
                    0
                }
            }};
        }
        let mut counters = PlayerCounters {
            stars_gained_this_turn,
            skill_cards_played_this_turn: take_bit_i32!(1),
            attack_cards_played_this_turn: take_bit_i32!(2),
            shivs_played_this_turn: take_bit_i32!(3),
            cards_discarded_this_turn: take_bit_i32!(4),
            cards_played_this_combat: take_bit_i32!(5),
            cards_generated_this_combat: take_bit_i32!(6),
            sadness_generated_this_combat: take_bit_i32!(7),
            cards_drawn_this_combat: take_bit_i32!(8),
            cards_played_this_turn: take_bit_i32!(9),
            manual_cards_played_this_turn: take_bit_i32!(10),
            card_plays_started_this_turn: take_bit_i32!(11),
            attack_or_skill_card_plays_started_this_turn: take_bit_i32!(12),
            attack_cards_started_this_turn: take_bit_i32!(13),
            zero_cost_attacks_started_this_turn: take_bit_i32!(14),
            first_in_series_card_plays_started_this_turn: take_bit_i32!(15),
            ..PlayerCounters::default()
        };
        if (c_mask >> 16) != 0 {
            counters.wah_started_this_turn = take_bit_i32!(16);
            counters.cho_started_this_turn = take_bit_i32!(17);
            counters.ki_started_this_turn = take_bit_i32!(18);
            counters.first_wah_play_ordinal_this_turn = take_bit_i32!(19);
            counters.first_cho_play_ordinal_this_turn = take_bit_i32!(20);
            counters.first_ki_play_ordinal_this_turn = take_bit_i32!(21);
            counters.wah_finished_this_turn = take_bit_i32!(22);
            counters.cho_finished_this_turn = take_bit_i32!(23);
            counters.ki_finished_this_turn = take_bit_i32!(24);
            counters.melody_cards_finished_this_turn = take_bit_i32!(25);
        }
        debug_assert_eq!(off, n_i32 * 4);
        p.counters = counters;
    }

    if u16_handles {
        read_card_handles_u16_vec(r, &mut out.player.hand)?;
        read_card_handles_u16_vec(r, &mut out.player.draw)?;
        read_card_handles_u16_vec(r, &mut out.player.discard)?;
        read_card_handles_u16_vec(r, &mut out.player.exhaust)?;
        read_card_handles_u16_vec(r, &mut out.player.play)?;
    } else {
        read_card_handles_vec(r, &mut out.player.hand)?;
        read_card_handles_vec(r, &mut out.player.draw)?;
        read_card_handles_vec(r, &mut out.player.discard)?;
        read_card_handles_vec(r, &mut out.player.exhaust)?;
        read_card_handles_vec(r, &mut out.player.play)?;
    }
    if deck_unchanged {
        if same_root
            && !out.deck_dirty.0
            && out.player.deck.len() == sctx.root_deck_handles.len()
        {
            #[cfg(debug_assertions)]
            debug_assert_eq!(
                out.player.deck.as_slice(),
                sctx.root_deck_handles.as_slice(),
                "reuse invariant violated for out.player.deck"
            );
        } else {
            out.player.deck.clone_from(&sctx.root_deck_handles);
        }
    } else if u16_handles {
        read_card_handles_u16_vec(r, &mut out.player.deck)?;
    } else {
        read_card_handles_vec(r, &mut out.player.deck)?;
    }

    let p = &mut out.player;

    p.combat_generated_potion_mask = r.u16()?;
    let pot_len = r.var_u32()? as usize;
    let prev_pot_len = p.potion_slots.len();
    let mut potions_changed = pot_len != prev_pot_len;
    for idx in 0..pot_len {
        let val = if r.u8()? == 0 {
            None
        } else {
            Some(decode_potion_id(r.u16()?)?)
        };
        if idx < prev_pot_len {
            if p.potion_slots[idx] != val {
                potions_changed = true;
                p.potion_slots[idx] = val;
            }
        } else {
            p.potion_slots.push(val);
        }
    }
    if prev_pot_len > pot_len {
        p.potion_slots.truncate(pot_len);
    }

    p.orb_capacity = r.i32()?;
    let orb_len = r.var_u32()? as usize;
    let prev_orb_len = p.orbs.len();
    let mut orbs_changed = orb_len != prev_orb_len;
    if orb_len == 0 {
        if prev_orb_len != 0 {
            p.orbs.clear();
        }
    } else {
        for idx in 0..orb_len {
            let b = r.u8()?;
            let (id, raw_evoke_value, raw_passive_value) = if (b & 0x80) != 0 {
                (decode_orb_id(b & 0x7F)?, None, None)
            } else {
                let id = decode_orb_id(b)?;
                let ev = if r.u8()? == 0 { None } else { Some(r.dec()?) };
                let pv = if r.u8()? == 0 { None } else { Some(r.dec()?) };
                (id, ev, pv)
            };
            if idx < prev_orb_len {
                let o = &mut p.orbs[idx];
                if o.id != id {
                    orbs_changed = true;
                    o.id = id;
                }
                o.raw_evoke_value = raw_evoke_value;
                o.raw_passive_value = raw_passive_value;
            } else {
                p.orbs.push(OrbInst {
                    id,
                    raw_evoke_value,
                    raw_passive_value,
                });
            }
        }
        if prev_orb_len > orb_len {
            p.orbs.truncate(orb_len);
        }
    }

    let arrow_len = r.var_u32()? as usize;
    if arrow_len == 0 {
        if !p.arrows.is_empty() {
            p.arrows.clear();
        }
    } else {
        p.arrows.clear();
        p.arrows.reserve(arrow_len);
        for _ in 0..arrow_len {
            p.arrows.push(ArrowSlotInst {
                status: decode_arrow_status(r.u8()?)?,
                destroy_after_impaled: r.bool()?,
            });
        }
    }

    let old_relic_len = p.relics.len();
    let mut relics_hook_changed;
    if relics_delta {
        if !sctx.has_root_relics || sctx.root_relics.len() > 64 {
            return Err(());
        }
        debug_assert_eq!(sctx.root_relic_headers.len(), sctx.root_relics.len());
        let rmask = r.u64()?;
        let root_relics = &sctx.root_relics;
        let relic_len = root_relics.len();
        if relic_len < 64 && (rmask >> relic_len) != 0 {
            return Err(());
        }
        if same_root && old_relic_len == relic_len {
            relics_hook_changed = false;
            let valid_mask = if relic_len == 64 {
                u64::MAX
            } else {
                (1u64 << relic_len) - 1
            };
            let mut to_restore = (out.relic_dirty_mask.0 & valid_mask) & !rmask;
            while to_restore != 0 {
                let idx = to_restore.trailing_zeros() as usize;
                to_restore &= to_restore - 1;
                let dr = &mut p.relics[idx];
                let rr = &root_relics[idx];
                if dr.id != rr.id || dr.is_melted != rr.is_melted {
                    relics_hook_changed = true;
                }
                dr.id = rr.id;
                dr.is_wax = rr.is_wax;
                dr.is_melted = rr.is_melted;
                dr.stack_count = rr.stack_count;
                dr.floor_added_to_deck = rr.floor_added_to_deck;
                rr.private_fields.clone_into_map(&mut dr.private_fields);
            }
            let mut to_read = rmask;
            while to_read != 0 {
                let idx = to_read.trailing_zeros() as usize;
                to_read &= to_read - 1;
                read_relic_into(r, &mut p.relics[idx], &mut relics_hook_changed)?;
            }
            #[cfg(debug_assertions)]
            for idx in 0..relic_len {
                if (rmask & (1u64 << idx)) == 0 {
                    debug_assert_eq!(
                        p.relics[idx], root_relics[idx],
                        "clean relic[{idx}] diverged from root after unpack"
                    );
                }
            }
            out.relic_dirty_mask.0 = rmask;
        } else {
            relics_hook_changed = true;
            let keep = old_relic_len.min(relic_len);
            for (dr, rr) in p.relics[..keep].iter_mut().zip(root_relics[..keep].iter()) {
                dr.id = rr.id;
                dr.is_wax = rr.is_wax;
                dr.is_melted = rr.is_melted;
                dr.stack_count = rr.stack_count;
                dr.floor_added_to_deck = rr.floor_added_to_deck;
                rr.private_fields.clone_into_map(&mut dr.private_fields);
            }
            if relic_len > keep {
                p.relics.reserve(relic_len - keep);
                for rr in &root_relics[keep..] {
                    p.relics.push(rr.clone());
                }
            } else if old_relic_len > relic_len {
                p.relics.truncate(relic_len);
            }
            let mut rem = rmask;
            while rem != 0 {
                let idx = rem.trailing_zeros() as usize;
                rem &= rem - 1;
                read_relic_into(r, &mut p.relics[idx], &mut relics_hook_changed)?;
            }
            out.relic_dirty_mask.0 = rmask;
        }
    } else {
        out.relic_dirty_mask.0 = u64::MAX;
        let relic_len = r.var_u32()? as usize;
        relics_hook_changed = relic_len != old_relic_len;
        for idx in 0..relic_len {
            if idx < old_relic_len {
                read_relic_into(r, &mut p.relics[idx], &mut relics_hook_changed)?;
            } else {
                let rel_hdr = r.take(8)?;
                let id = decode_relic_id(slice_u16(rel_hdr, 0))?;
                let is_wax = rel_hdr[2] != 0;
                let is_melted = rel_hdr[3] != 0;
                let stack_count = slice_i32(rel_hdr, 4);
                let floor_added_to_deck = r.opt_i32()?;
                p.relics.push(RelicInst {
                    id,
                    is_wax,
                    is_melted,
                    stack_count,
                    floor_added_to_deck,
                    private_fields: r.cow_field_map()?,
                });
            }
        }
        if old_relic_len > relic_len {
            p.relics.truncate(relic_len);
        }
    }

    // Cards
    let unpack_tables = if same_root {
        out.hook_cache.cached_tables
    } else {
        None
    };
    let mut pile_has_hook_bits = 0u8;
    let prefix_len = sctx.root_deck_prefix_len;
    let cards_len = r.var_u32()? as usize;
    if deck_unchanged {
        #[cfg(debug_assertions)]
        debug_assert!(sctx.root_deck_cards.iter().all(|c| c.pile == PileType::Deck));
        if same_root && !out.deck_dirty.0 && out.cards.len() >= prefix_len {
            #[cfg(debug_assertions)]
            debug_assert_eq!(
                &out.cards[..prefix_len],
                &sctx.root_deck_cards[..],
                "reuse invariant violated for out.cards[..prefix_len]"
            );
            out.cards.truncate(prefix_len);
        } else {
            out.cards.clear();
            out.cards.reserve(prefix_len + cards_len);
            out.cards.extend_from_slice(&sctx.root_deck_cards);
        }
        out.cards.reserve(cards_len);
        out.deck_dirty.0 = false;
    } else {
        out.cards.clear();
        out.cards.reserve(cards_len);
        out.deck_dirty.0 = true;
    }
    if cards_len != 0 {
        let base_len = out.cards.len();
        let cards_ptr = out.cards.as_mut_ptr();
        let mut cards_guard = SetLenOnDrop {
            vec: &mut out.cards,
            base_len,
            written: 0,
        };
        for idx in 0..cards_len {
            let (tag, fast_word_opt) = if r.remaining() >= 8 {
                let word = u64::from_le(unsafe { std::ptr::read_unaligned(r.cur.cast::<u64>()) });
                (word as u8, Some(word))
            } else {
                (r.peek_u8()?, None)
            };
            let fast_bit = (tag & 0x80) != 0;
            let card = if fast_bit || (1..=4).contains(&tag) {
                let word = if !fast_bit {
                    let _ = r.u8()?;
                    r.u64()?
                } else if let Some(w) = fast_word_opt {
                    unsafe {
                        r.cur = r.cur.add(8);
                    }
                    w
                } else {
                    return Err(());
                };
                let tag_byte = word as u8;
                let pile = decode_pile_type(tag_byte & 0x0F)?;
                let removed_from_state = (tag_byte & 0x10) != 0;
                let upgrade_level = i32::from((tag_byte & 0x20) != 0);
                let use_root_floor = (tag_byte & 0x40) != 0;
                let canonical_base_energy_cost = ((word >> 8) as u8 as i8) as i16;
                let id = decode_card_id(((word >> 16) & 0xFFFF) as u16)?;
                let local_keywords =
                    CardKeywords::from_bits_retain(((word >> 32) & 0xFFFF) as u16);
                let dh_raw = (word >> 48) as u16;
                let deck_card_handle = OptCardHandle(dh_raw as u32);
                let (base_energy_cost, affliction, temp_flags, ext_index) = if fast_bit {
                    (
                        canonical_base_energy_cost,
                        None,
                        CardTempFlags::empty(),
                        None,
                    )
                } else {
                    match tag {
                        1 => {
                            let aff_raw = r.u32()?;
                            (
                                canonical_base_energy_cost,
                                Some(AfflictionInst {
                                    id: decode_affliction_id((aff_raw & 0xFFFF) as u16)?,
                                    amount: ((aff_raw >> 16) as u16 as i16) as i32,
                                }),
                                CardTempFlags::empty(),
                                None,
                            )
                        }
                        2 => (
                            (r.u8()? as i8) as i16,
                            None,
                            CardTempFlags::empty(),
                            None,
                        ),
                        3 => (
                            canonical_base_energy_cost,
                            None,
                            CardTempFlags::empty(),
                            Some(r.u16()? as u32),
                        ),
                        4 => (
                            canonical_base_energy_cost,
                            None,
                            CardTempFlags::from_bits_retain(r.u16()?),
                            None,
                        ),
                        _ => unreachable!(),
                    }
                };
                if let Some(t) = unpack_tables {
                    if !removed_from_state
                        && (t.card_has_hook(id)
                            || ext_index.is_some()
                            || (!t.afflictions_self_card_entered_only && affliction.is_some()))
                    {
                        pile_has_hook_bits |= 1u8 << (pile as u8);
                    }
                }
                let card_abs_idx = base_len + idx;
                let floor_added_to_deck = if use_root_floor {
                    sctx.root_card_floors
                        .get(card_abs_idx)
                        .copied()
                        .unwrap_or(OptI16::NONE)
                } else {
                    OptI16::NONE
                };
                let handle = CardHandle((card_abs_idx + 1) as u32);
                CardInst {
                    handle,
                    id,
                    upgrade_level,
                    canonical_base_energy_cost,
                    base_energy_cost,
                    captured_energy_x_value: 0,
                    base_replay_count: 0,
                    floor_added_to_deck,
                    deck_card_handle,
                    clone_of_handle: OptCardHandle::NONE,
                    temp_star_cost_override_this_turn: OptI16::NONE,
                    temp_cost_override_this_turn: OptI16::NONE,
                    temp_cost_override_this_turn_or_until_played: OptI16::NONE,
                    temp_cost_override_this_combat: OptI16::NONE,
                    local_keywords,
                    keywords_added_from_outside: CardKeywords::empty(),
                    keywords_removed_from_outside: CardKeywords::empty(),
                    temp_flags,
                    affliction,
                    ext_index,
                    pile,
                    removed_from_state,
                    transform_into_after_play: None,
                }
            } else if tag == 0 {
                let _ = r.u8()?;
                let handle = CardHandle(r.var_u32()?);
                let chdr = r.take(11)?;
                let id = decode_card_id(slice_u16(chdr, 0))?;
                let canonical_base_energy_cost =
                    slice_i32(chdr, 2).clamp(i16::MIN as i32, i16::MAX as i32) as i16;
                let local_keywords = CardKeywords::from_bits_retain(slice_u16(chdr, 6));
                let opt_mask = slice_u16(chdr, 8);
                let flags_byte = chdr[10];

                let upgrade_level = if (opt_mask & (1 << 0)) != 0 { r.i32()? } else { 0 };
                let base_energy_cost = if (opt_mask & (1 << 1)) != 0 {
                    r.i32()?.clamp(i16::MIN as i32, i16::MAX as i32) as i16
                } else {
                    canonical_base_energy_cost
                };
                let captured_energy_x_value = if (opt_mask & (1 << 2)) != 0 {
                    r.i32()?.clamp(i16::MIN as i32, i16::MAX as i32) as i16
                } else {
                    0
                };
                let base_replay_count = if (opt_mask & (1 << 3)) != 0 {
                    r.i32()?.clamp(i16::MIN as i32, i16::MAX as i32) as i16
                } else {
                    0
                };
                let floor_added_to_deck = if (opt_mask & (1 << 4)) != 0 {
                    OptI16::some_clamped(r.i32()?)
                } else {
                    OptI16::NONE
                };
                let deck_card_handle = if (opt_mask & (1 << 5)) != 0 {
                    OptCardHandle::some(CardHandle(r.var_u32()?))
                } else {
                    OptCardHandle::NONE
                };
                let clone_of_handle = if (opt_mask & (1 << 6)) != 0 {
                    OptCardHandle::some(CardHandle(r.var_u32()?))
                } else {
                    OptCardHandle::NONE
                };
                let temp_star_cost_override_this_turn = if (opt_mask & (1 << 7)) != 0 {
                    OptI16::some_clamped(r.i32()?)
                } else {
                    OptI16::NONE
                };
                let temp_cost_override_this_turn = if (opt_mask & (1 << 8)) != 0 {
                    OptI16::some_clamped(r.i32()?)
                } else {
                    OptI16::NONE
                };
                let temp_cost_override_this_turn_or_until_played = if (opt_mask & (1 << 9)) != 0 {
                    OptI16::some_clamped(r.i32()?)
                } else {
                    OptI16::NONE
                };
                let temp_cost_override_this_combat = if (opt_mask & (1 << 10)) != 0 {
                    OptI16::some_clamped(r.i32()?)
                } else {
                    OptI16::NONE
                };
                let keywords_added_from_outside = if (opt_mask & (1 << 11)) != 0 {
                    CardKeywords::from_bits_retain(r.u16()?)
                } else {
                    CardKeywords::empty()
                };
                let keywords_removed_from_outside = if (opt_mask & (1 << 12)) != 0 {
                    CardKeywords::from_bits_retain(r.u16()?)
                } else {
                    CardKeywords::empty()
                };
                let temp_flags = if (opt_mask & (1 << 13)) != 0 {
                    CardTempFlags::from_bits_retain(r.u16()?)
                } else {
                    CardTempFlags::empty()
                };
                let affliction = if (opt_mask & (1 << 14)) != 0 {
                    Some(AfflictionInst {
                        id: decode_affliction_id(r.u16()?)?,
                        amount: r.i32()?,
                    })
                } else {
                    None
                };
                let ext_index = if (opt_mask & (1 << 15)) != 0 {
                    Some(r.var_u32()?)
                } else {
                    None
                };
                let pile = decode_pile_type(flags_byte & 0x0F)?;
                let removed_from_state = (flags_byte & 0x10) != 0;
                let transform_into_after_play = if (flags_byte & 0x20) != 0 {
                    Some(decode_card_id(r.u16()?)?)
                } else {
                    None
                };
                if let Some(t) = unpack_tables {
                    if !removed_from_state
                        && (t.card_has_hook(id)
                            || ext_index.is_some()
                            || (!t.afflictions_self_card_entered_only && affliction.is_some()))
                    {
                        pile_has_hook_bits |= 1u8 << (pile as u8);
                    }
                }
                CardInst {
                    handle,
                    id,
                    upgrade_level,
                    canonical_base_energy_cost,
                    base_energy_cost,
                    captured_energy_x_value,
                    base_replay_count,
                    floor_added_to_deck,
                    deck_card_handle,
                    clone_of_handle,
                    temp_star_cost_override_this_turn,
                    temp_cost_override_this_turn,
                    temp_cost_override_this_turn_or_until_played,
                    temp_cost_override_this_combat,
                    local_keywords,
                    keywords_added_from_outside,
                    keywords_removed_from_outside,
                    temp_flags,
                    affliction,
                    ext_index,
                    pile,
                    removed_from_state,
                    transform_into_after_play,
                }
            } else {
                return Err(());
            };
            unsafe {
                cards_ptr.add(base_len + idx).write(card);
            }
            cards_guard.written += 1;
        }
    }

    // CardExtensions (reuse Vec/BTreeMap capacity in-place)
    let ext_len = r.var_u32()? as usize;
    let old_ext_len = out.card_extensions.len();
    for idx in 0..ext_len {
        let mlen = r.var_u32()? as usize;
        if idx < old_ext_len {
            let de = &mut out.card_extensions[idx];
            if mlen == 0 {
                if !de.local_energy_modifiers.is_empty() {
                    de.local_energy_modifiers.clear();
                }
            } else {
                de.local_energy_modifiers.clear();
                de.local_energy_modifiers.reserve(mlen);
                for _ in 0..mlen {
                    let mraw = r.take(6)?;
                    de.local_energy_modifiers.push(LocalEnergyModifier {
                        kind: decode_mod_kind(mraw[0])?,
                        amount: slice_i32(mraw, 1),
                        is_reduce_only: mraw[5] != 0,
                    });
                }
            }
            if r.u8()? == 0 {
                de.enchantment = None;
            } else {
                let eraw = r.take(7)?;
                let id = decode_enchantment_id(slice_u16(eraw, 0))?;
                let amount = slice_i32(eraw, 2);
                let status = decode_ench_status(eraw[6])?;
                if let Some(en) = de.enchantment.as_mut() {
                    en.id = id;
                    en.amount = amount;
                    en.status = status;
                    r.cow_field_map_into(&mut en.private_fields)?;
                } else {
                    de.enchantment = Some(EnchantmentInst {
                        id,
                        amount,
                        status,
                        private_fields: r.cow_field_map()?,
                    });
                }
            }
            r.cow_field_map_into(&mut de.private_fields)?;
        } else {
            let local_energy_modifiers = if mlen == 0 {
                SmallVec::new()
            } else {
                let mut v = SmallVec::with_capacity(mlen);
                for _ in 0..mlen {
                    let mraw = r.take(6)?;
                    v.push(LocalEnergyModifier {
                        kind: decode_mod_kind(mraw[0])?,
                        amount: slice_i32(mraw, 1),
                        is_reduce_only: mraw[5] != 0,
                    });
                }
                v
            };
            let enchantment = if r.u8()? == 0 {
                None
            } else {
                let eraw = r.take(7)?;
                Some(EnchantmentInst {
                    id: decode_enchantment_id(slice_u16(eraw, 0))?,
                    amount: slice_i32(eraw, 2),
                    status: decode_ench_status(eraw[6])?,
                    private_fields: r.cow_field_map()?,
                })
            };
            let private_fields = r.cow_field_map()?;
            out.card_extensions.push(CardExtension {
                local_energy_modifiers,
                enchantment,
                private_fields,
            });
        }
    }
    if old_ext_len > ext_len {
        out.card_extensions.truncate(ext_len);
    }

    // Creatures
    let cr_len = r.var_u32()? as usize;
    let old_cr_len = out.creatures.len();
    for cr_idx in 0..cr_len {
        let handle = CreatureHandle(r.u32()?);
        let combat_id = r.opt_u32()?;
        let side = decode_combat_side(r.u8()?)?;
        if cr_idx < old_cr_len {
            let dc = &mut out.creatures[cr_idx];
            dc.handle = handle;
            dc.combat_id = combat_id;
            dc.side = side;
            r.read_opt_cow_str_encoded_into(&mut dc.slot_name)?;
            dc.is_player = r.bool()?;
            dc.pet_owner_player_index = r.opt_u32()?.map(|x| x as usize);
            dc.monster_id = if r.u8()? == 0 {
                None
            } else {
                Some(decode_monster_id(r.u16()?)?)
            };
            let craw = r.take(17)?;
            dc.current_hp = slice_i32(craw, 0);
            dc.max_hp = slice_i32(craw, 4);
            dc.block = slice_i32(craw, 8);
            dc.cumulative_hp_lost = slice_i32(craw, 12);
            dc.is_detached = craw[16] != 0;

            if r.u8()? == 0 {
                dc.monster = None;
            } else {
                let mid = decode_monster_id(r.u16()?)?;
                let rng = r.rng()?;
                let mflags = r.take(2)?;
                let is_performing_move = mflags[0] != 0;
                let spawned_this_turn = mflags[1] != 0;
                if let Some(dm) = dc.monster.as_mut() {
                    dm.monster_id = mid;
                    dm.rng = rng;
                    dm.is_performing_move = is_performing_move;
                    dm.spawned_this_turn = spawned_this_turn;
                    r.read_cow_str_encoded_into(&mut dm.current_state_id)?;
                    r.read_opt_cow_str_encoded_into(&mut dm.next_move_id)?;
                    let mbools = r.take(2)?;
                    dm.next_move_can_transition_away = mbools[0] != 0;
                    dm.performed_first_move = mbools[1] != 0;
                    let slog_len = r.var_u32()? as usize;
                    let old_slog_len = dm.state_log_ids.len();
                    for s_idx in 0..slog_len {
                        if s_idx < old_slog_len {
                            r.read_cow_str_encoded_into(&mut dm.state_log_ids[s_idx])?;
                        } else {
                            dm.state_log_ids.push(r.cow_str()?);
                        }
                    }
                    if old_slog_len > slog_len {
                        dm.state_log_ids.truncate(slog_len);
                    }
                    r.read_opt_cow_str_encoded_into(&mut dm.transient_move_follow_up_state_id)?;
                    r.cow_field_map_into(&mut dm.setup_fields)?;
                    r.cow_field_map_into(&mut dm.private_fields)?;
                } else {
                    let current_state_id = r.cow_str()?;
                    let next_move_id = r.opt_cow_str()?;
                    let mbools = r.take(2)?;
                    let next_move_can_transition_away = mbools[0] != 0;
                    let performed_first_move = mbools[1] != 0;
                    let slog_len = r.var_u32()? as usize;
                    let mut state_log_ids = Vec::with_capacity(slog_len);
                    for _ in 0..slog_len {
                        state_log_ids.push(r.cow_str()?);
                    }
                    let transient_move_follow_up_state_id = r.opt_cow_str()?;
                    let setup_fields = r.cow_field_map()?;
                    let private_fields = r.cow_field_map()?;
                    dc.monster = Some(Box::new(MonsterInst {
                        monster_id: mid,
                        rng,
                        is_performing_move,
                        spawned_this_turn,
                        current_state_id,
                        next_move_id,
                        next_move_can_transition_away,
                        performed_first_move,
                        state_log_ids,
                        transient_move_follow_up_state_id,
                        setup_fields,
                        private_fields,
                    }));
                }
            }

            dc.has_minion_power = read_powers_into(r, &mut dc.powers)?;
        } else {
            let slot_name = r.opt_cow_str()?;
            let is_player = r.bool()?;
            let pet_owner_player_index = r.opt_u32()?.map(|x| x as usize);
            let monster_id = if r.u8()? == 0 {
                None
            } else {
                Some(decode_monster_id(r.u16()?)?)
            };
            let craw = r.take(17)?;
            let current_hp = slice_i32(craw, 0);
            let max_hp = slice_i32(craw, 4);
            let block = slice_i32(craw, 8);
            let cumulative_hp_lost = slice_i32(craw, 12);
            let is_detached = craw[16] != 0;

            let monster = if r.u8()? == 0 {
                None
            } else {
                let mid = decode_monster_id(r.u16()?)?;
                let rng = r.rng()?;
                let mflags = r.take(2)?;
                let is_performing_move = mflags[0] != 0;
                let spawned_this_turn = mflags[1] != 0;
                let current_state_id = r.cow_str()?;
                let next_move_id = r.opt_cow_str()?;
                let mbools = r.take(2)?;
                let next_move_can_transition_away = mbools[0] != 0;
                let performed_first_move = mbools[1] != 0;
                let slog_len = r.var_u32()? as usize;
                let mut state_log_ids = Vec::with_capacity(slog_len);
                for _ in 0..slog_len {
                    state_log_ids.push(r.cow_str()?);
                }
                let transient_move_follow_up_state_id = r.opt_cow_str()?;
                let setup_fields = r.cow_field_map()?;
                let private_fields = r.cow_field_map()?;
                Some(Box::new(MonsterInst {
                    monster_id: mid,
                    rng,
                    is_performing_move,
                    spawned_this_turn,
                    current_state_id,
                    next_move_id,
                    next_move_can_transition_away,
                    performed_first_move,
                    state_log_ids,
                    transient_move_follow_up_state_id,
                    setup_fields,
                    private_fields,
                }))
            };

            let mut powers = Vec::new();
            let has_minion_power = read_powers_into(r, &mut powers)?;

            out.creatures.push(CreatureInst {
                handle,
                combat_id,
                side,
                slot_name,
                is_player,
                pet_owner_player_index,
                monster_id,
                current_hp,
                max_hp,
                block,
                cumulative_hp_lost,
                is_detached,
                has_minion_power,
                monster,
                powers,
            });
        }
    }
    if old_cr_len > cr_len {
        out.creatures.truncate(cr_len);
    }

    // PowerExtensions (reuse in-place)
    let pe_len = r.var_u32()? as usize;
    let old_pe_len = out.power_extensions.len();
    for idx in 0..pe_len {
        if idx < old_pe_len {
            let dpe = &mut out.power_extensions[idx];
            r.cow_field_map_into(&mut dpe.private_fields)?;
            dpe.attack_command_id = r.opt_u64()?;
            dpe.amount_when_attack_started = r.i32()?;
            let imp_len = r.var_u32()? as usize;
            if imp_len == 0 {
                if !dpe.impaled_arrows.is_empty() {
                    dpe.impaled_arrows.clear();
                }
            } else {
                dpe.impaled_arrows.clear();
                dpe.impaled_arrows.reserve(imp_len);
                for _ in 0..imp_len {
                    dpe.impaled_arrows.push(r.var_u32()? as usize);
                }
            }
            dpe.hit_counter = r.i32()?;
        } else {
            let private_fields = r.cow_field_map()?;
            let attack_command_id = r.opt_u64()?;
            let amount_when_attack_started = r.i32()?;
            let imp_len = r.var_u32()? as usize;
            let impaled_arrows = if imp_len == 0 {
                SmallVec::new()
            } else {
                let mut v = SmallVec::with_capacity(imp_len);
                for _ in 0..imp_len {
                    v.push(r.var_u32()? as usize);
                }
                v
            };
            let hit_counter = r.i32()?;
            out.power_extensions.push(PowerExtension {
                private_fields,
                attack_command_id,
                amount_when_attack_started,
                impaled_arrows,
                hit_counter,
            });
        }
    }
    if old_pe_len > pe_len {
        out.power_extensions.truncate(pe_len);
    }

    read_creature_handles_vec(r, &mut out.ally_handles)?;
    read_creature_handles_vec(r, &mut out.enemy_handles)?;
    let extra_h_mask = r.u8()?;
    if (extra_h_mask & 1) != 0 {
        read_creature_handles(r, &mut out.escaped_handles)?;
    } else if !out.escaped_handles.is_empty() {
        out.escaped_handles.clear();
    }
    if (extra_h_mask & 2) != 0 {
        read_creature_handles(r, &mut out.removed_handles)?;
    } else if !out.removed_handles.is_empty() {
        out.removed_handles.clear();
    }
    if (extra_h_mask & 4) != 0 {
        read_creature_handles(r, &mut out.spawned_enemy_handles)?;
    } else if !out.spawned_enemy_handles.is_empty() {
        out.spawned_enemy_handles.clear();
    }
    if (extra_h_mask & 8) != 0 {
        read_creature_handles(r, &mut out.pet_handles)?;
    } else if !out.pet_handles.is_empty() {
        out.pet_handles.clear();
    }

    // Damage history
    let dh_len = r.var_u32()? as usize;
    out.damage_history.clear();
    if dh_len != 0 {
        out.damage_history.reserve(dh_len);
        let dh_ptr = out.damage_history.as_mut_ptr();
        let mut dh_guard = SetLenOnDrop {
            vec: &mut out.damage_history,
            base_len: 0,
            written: 0,
        };
        for idx in 0..dh_len {
            let dhdr = r.take(4)?;
            let flags = slice_u16(dhdr, 0);
            let props = ValueProp::from_bits_retain(dhdr[2]);
            let current_side = decode_combat_side(dhdr[3])?;
            if (flags & (1 << 12)) != 0 {
                let payload_len = 2 + (flags & 0x063F).count_ones() as usize;
                let b = r.take(payload_len)?;
                let mut pos = 0usize;
                let recv = b[pos] as u32;
                pos += 1;
                let receiver_creature_handle = CreatureHandle(recv);
                let dealer_creature_handle = if (flags & (1 << 0)) != 0 {
                    let v = b[pos] as u32;
                    pos += 1;
                    Some(CreatureHandle(v))
                } else {
                    None
                };
                let original_target_creature_handle = if (flags & (1 << 1)) != 0 {
                    let v = b[pos] as u32;
                    pos += 1;
                    CreatureHandle(v)
                } else {
                    receiver_creature_handle
                };
                let intended_hp_loss = if (flags & (1 << 2)) != 0 {
                    let v = b[pos] as i32;
                    pos += 1;
                    v
                } else {
                    0
                };
                let blocked_damage = if (flags & (1 << 3)) != 0 {
                    let v = b[pos] as i32;
                    pos += 1;
                    v
                } else {
                    0
                };
                let unblocked_damage = if (flags & (1 << 4)) != 0 {
                    let v = b[pos] as i32;
                    pos += 1;
                    v
                } else {
                    0
                };
                let overkill_damage = if (flags & (1 << 5)) != 0 {
                    let v = b[pos] as i32;
                    pos += 1;
                    v
                } else {
                    0
                };
                let was_target_killed = (flags & (1 << 6)) != 0;
                let was_block_broken = (flags & (1 << 7)) != 0;
                let was_fully_blocked = (flags & (1 << 8)) != 0;
                let card_source_handle = if (flags & (1 << 9)) != 0 {
                    let v = b[pos] as u32;
                    pos += 1;
                    Some(CardHandle(v))
                } else {
                    None
                };
                let round_number = b[pos] as i32;
                pos += 1;
                let player_turn_numbers = if (flags & (1 << 10)) != 0 {
                    let v = b[pos] as i32;
                    pos += 1;
                    InlineI32Vec4::one(v)
                } else {
                    InlineI32Vec4::new()
                };
                debug_assert_eq!(pos, payload_len);
                unsafe {
                    dh_ptr.add(idx).write(DamageHistoryEntry {
                        receiver_creature_handle,
                        dealer_creature_handle,
                        original_target_creature_handle,
                        props,
                        intended_hp_loss,
                        blocked_damage,
                        unblocked_damage,
                        overkill_damage,
                        was_target_killed,
                        was_block_broken,
                        was_fully_blocked,
                        card_source_handle,
                        round_number,
                        current_side,
                        player_turn_numbers,
                    });
                }
                dh_guard.written += 1;
                continue;
            }
            let receiver_creature_handle = CreatureHandle(r.var_u32()?);
            let dealer_creature_handle = if (flags & (1 << 0)) != 0 {
                Some(CreatureHandle(r.var_u32()?))
            } else {
                None
            };
            let original_target_creature_handle = if (flags & (1 << 1)) != 0 {
                CreatureHandle(r.var_u32()?)
            } else {
                receiver_creature_handle
            };
            let intended_hp_loss = if (flags & (1 << 2)) != 0 {
                r.var_i32()?
            } else {
                0
            };
            let blocked_damage = if (flags & (1 << 3)) != 0 {
                r.var_i32()?
            } else {
                0
            };
            let unblocked_damage = if (flags & (1 << 4)) != 0 {
                r.var_i32()?
            } else {
                0
            };
            let overkill_damage = if (flags & (1 << 5)) != 0 {
                r.var_i32()?
            } else {
                0
            };
            let was_target_killed = (flags & (1 << 6)) != 0;
            let was_block_broken = (flags & (1 << 7)) != 0;
            let was_fully_blocked = (flags & (1 << 8)) != 0;
            let card_source_handle = if (flags & (1 << 9)) != 0 {
                Some(CardHandle(r.var_u32()?))
            } else {
                None
            };
            let round_number = r.var_i32()?;
            let player_turn_numbers = if (flags & (1 << 10)) != 0 {
                InlineI32Vec4::one(r.var_i32()?)
            } else if (flags & (1 << 11)) != 0 {
                let tn_len = r.var_u32()? as usize;
                let mut v = InlineI32Vec4::new();
                for _ in 0..tn_len {
                    v.push(r.var_i32()?);
                }
                v
            } else {
                InlineI32Vec4::new()
            };
            unsafe {
                dh_ptr.add(idx).write(DamageHistoryEntry {
                    receiver_creature_handle,
                    dealer_creature_handle,
                    original_target_creature_handle,
                    props,
                    intended_hp_loss,
                    blocked_damage,
                    unblocked_damage,
                    overkill_damage,
                    was_target_killed,
                    was_block_broken,
                    was_fully_blocked,
                    card_source_handle,
                    round_number,
                    current_side,
                    player_turn_numbers,
                });
            }
            dh_guard.written += 1;
        }
    }

    // Semantic history
    let sh = &mut out.semantic_history;
    let sh_mask = r.u8()?;
    if sh_mask == 0 {
        if !sh.exhausts.is_empty() {
            sh.exhausts.clear();
        }
        if !sh.card_block_gains.is_empty() {
            sh.card_block_gains.clear();
        }
        if !sh.orb_channels.is_empty() {
            sh.orb_channels.clear();
        }
        if !sh.energy_spent.is_empty() {
            sh.energy_spent.clear();
        }
        if !sh.card_drawn.is_empty() {
            sh.card_drawn.clear();
        }
        if !sh.actor_events.is_empty() {
            sh.actor_events.clear();
        }
    } else {
        let mut prev_valid = false;
        let mut prev_tk = TurnKey {
            round_number: 0,
            side: CombatSide::Player,
            player_turn_numbers: InlineOptI32Vec4::new(),
        };
        if (sh_mask & (1 << 0)) != 0 {
            sh.exhausts.clear();
            let ex_len = r.var_u32()? as usize;
            if ex_len != 0 {
                sh.exhausts.reserve(ex_len);
                let dst_ptr = sh.exhausts.as_mut_ptr();
                let mut guard = SetLenOnDrop {
                    vec: &mut sh.exhausts,
                    base_len: 0,
                    written: 0,
                };
                for idx in 0..ex_len {
                    if prev_valid && r.remaining() >= 2 {
                        let raw = u16::from_le(unsafe {
                            std::ptr::read_unaligned(r.cur.cast::<u16>())
                        });
                        if (raw & 0x80FF) == 0 {
                            unsafe {
                                r.cur = r.cur.add(2);
                                dst_ptr.add(idx).write((prev_tk, (raw >> 8) as usize));
                            }
                            guard.written += 1;
                            continue;
                        }
                    }
                    let tag = r.u8()?;
                    if tag != 0 || !prev_valid {
                        read_turn_key_delta_with_tag_into(tag, r, &mut prev_valid, &mut prev_tk)?;
                    }
                    let val = (prev_tk, r.var_u32()? as usize);
                    unsafe {
                        dst_ptr.add(idx).write(val);
                    }
                    guard.written += 1;
                }
            }
        } else if !sh.exhausts.is_empty() {
            sh.exhausts.clear();
        }
        if (sh_mask & (1 << 1)) != 0 {
            sh.card_block_gains.clear();
            let cbg_len = r.var_u32()? as usize;
            if cbg_len != 0 {
                sh.card_block_gains.reserve(cbg_len);
                let dst_ptr = sh.card_block_gains.as_mut_ptr();
                let mut guard = SetLenOnDrop {
                    vec: &mut sh.card_block_gains,
                    base_len: 0,
                    written: 0,
                };
                for idx in 0..cbg_len {
                    if prev_valid && r.remaining() >= 4 {
                        let raw = u32::from_le(unsafe {
                            std::ptr::read_unaligned(r.cur.cast::<u32>())
                        });
                        if (raw & 0x80FF) == 3 {
                            unsafe {
                                r.cur = r.cur.add(4);
                                dst_ptr.add(idx).write((
                                    prev_tk,
                                    ((raw >> 8) & 0x7F) as usize,
                                    ((raw >> 16) as u16 as i16) as i32,
                                ));
                            }
                            guard.written += 1;
                            continue;
                        }
                    }
                    let tag = r.u8()?;
                    if tag != 0 || !prev_valid {
                        read_turn_key_delta_with_tag_into(tag, r, &mut prev_valid, &mut prev_tk)?;
                    }
                    let val = (prev_tk, r.var_u32()? as usize, r.i32()?);
                    unsafe {
                        dst_ptr.add(idx).write(val);
                    }
                    guard.written += 1;
                }
            }
        } else if !sh.card_block_gains.is_empty() {
            sh.card_block_gains.clear();
        }
        if (sh_mask & (1 << 2)) != 0 {
            sh.orb_channels.clear();
            let oc_len = r.var_u32()? as usize;
            if oc_len != 0 {
                sh.orb_channels.reserve(oc_len);
                let dst_ptr = sh.orb_channels.as_mut_ptr();
                let mut guard = SetLenOnDrop {
                    vec: &mut sh.orb_channels,
                    base_len: 0,
                    written: 0,
                };
                for idx in 0..oc_len {
                    let tag = r.u8()?;
                    if tag != 0 || !prev_valid {
                        read_turn_key_delta_with_tag_into(tag, r, &mut prev_valid, &mut prev_tk)?;
                    }
                    let val = (prev_tk, r.var_u32()? as usize, decode_orb_id(r.u8()?)?);
                    unsafe {
                        dst_ptr.add(idx).write(val);
                    }
                    guard.written += 1;
                }
            }
        } else if !sh.orb_channels.is_empty() {
            sh.orb_channels.clear();
        }
        if (sh_mask & (1 << 3)) != 0 {
            sh.energy_spent.clear();
            let es_len = r.var_u32()? as usize;
            if es_len != 0 {
                sh.energy_spent.reserve(es_len);
                let dst_ptr = sh.energy_spent.as_mut_ptr();
                let mut guard = SetLenOnDrop {
                    vec: &mut sh.energy_spent,
                    base_len: 0,
                    written: 0,
                };
                for idx in 0..es_len {
                    if prev_valid && r.remaining() >= 4 {
                        let raw = u32::from_le(unsafe {
                            std::ptr::read_unaligned(r.cur.cast::<u32>())
                        });
                        if (raw & 0x80FF) == 3 {
                            unsafe {
                                r.cur = r.cur.add(4);
                                dst_ptr.add(idx).write((
                                    prev_tk,
                                    ((raw >> 8) & 0x7F) as usize,
                                    ((raw >> 16) as u16 as i16) as i32,
                                ));
                            }
                            guard.written += 1;
                            continue;
                        }
                    }
                    let tag = r.u8()?;
                    if tag != 0 || !prev_valid {
                        read_turn_key_delta_with_tag_into(tag, r, &mut prev_valid, &mut prev_tk)?;
                    }
                    let val = (prev_tk, r.var_u32()? as usize, r.i32()?);
                    unsafe {
                        dst_ptr.add(idx).write(val);
                    }
                    guard.written += 1;
                }
            }
        } else if !sh.energy_spent.is_empty() {
            sh.energy_spent.clear();
        }
        if (sh_mask & (1 << 4)) != 0 {
            sh.card_drawn.clear();
            let cd_len = r.var_u32()? as usize;
            if cd_len != 0 {
                sh.card_drawn.reserve(cd_len);
                let dst_ptr = sh.card_drawn.as_mut_ptr();
                let mut guard = SetLenOnDrop {
                    vec: &mut sh.card_drawn,
                    base_len: 0,
                    written: 0,
                };
                for idx in 0..cd_len {
                    if prev_valid && r.remaining() >= 4 {
                        let raw = u32::from_le(unsafe {
                            std::ptr::read_unaligned(r.cur.cast::<u32>())
                        });
                        if (raw & 0x80FF) == 0 {
                            let ct = decode_card_type((raw >> 16) as u8)?;
                            unsafe {
                                r.cur = r.cur.add(4);
                                dst_ptr.add(idx).write((
                                    prev_tk,
                                    ((raw >> 8) & 0x7F) as usize,
                                    ct,
                                    (raw >> 24) != 0,
                                ));
                            }
                            guard.written += 1;
                            continue;
                        }
                    }
                    let tag = r.u8()?;
                    if tag != 0 || !prev_valid {
                        read_turn_key_delta_with_tag_into(tag, r, &mut prev_valid, &mut prev_tk)?;
                    }
                    let val = (
                        prev_tk,
                        r.var_u32()? as usize,
                        decode_card_type(r.u8()?)?,
                        r.bool()?,
                    );
                    unsafe {
                        dst_ptr.add(idx).write(val);
                    }
                    guard.written += 1;
                }
            }
        } else if !sh.card_drawn.is_empty() {
            sh.card_drawn.clear();
        }
        if (sh_mask & (1 << 5)) != 0 {
            let ae_len = r.var_u32()? as usize;
            let keep = sh.actor_events.len().min(ae_len);
            for idx in 0..keep {
                r.read_cow_str_encoded_into(&mut sh.actor_events[idx].0)?;
                read_turn_key_delta_into(r, &mut prev_valid, &mut prev_tk)?;
                sh.actor_events[idx].1 = prev_tk;
                sh.actor_events[idx].2 = r.var_u32()? as usize;
            }
            if ae_len > keep {
                sh.actor_events.reserve(ae_len - keep);
                for _ in keep..ae_len {
                    let s = r.cow_str()?;
                    read_turn_key_delta_into(r, &mut prev_valid, &mut prev_tk)?;
                    sh.actor_events.push((s, prev_tk, r.var_u32()? as usize));
                }
            } else {
                sh.actor_events.truncate(ae_len);
            }
        } else if !sh.actor_events.is_empty() {
            sh.actor_events.clear();
        }
    }
    match r.u8()? {
        0 => {
            sh.growth_rewards = GrowthCounters::default();
            sh.brightest_flame_max_hp_spent = 0;
        }
        1 => {
            let graw = r.take(44)?;
            sh.growth_rewards.hand_of_greed = slice_i32(graw, 0);
            sh.growth_rewards.the_hunt = slice_i32(graw, 4);
            sh.growth_rewards.feed = slice_i32(graw, 8);
            sh.growth_rewards.royalties = slice_i32(graw, 12);
            sh.growth_rewards.alchemize = slice_i32(graw, 16);
            sh.growth_rewards.genetic_algorithm = slice_i32(graw, 20);
            sh.growth_rewards.the_scythe = slice_i32(graw, 24);
            sh.growth_rewards.goopy = slice_i32(graw, 28);
            sh.growth_rewards.forbidden_grimoire = slice_i32(graw, 32);
            sh.growth_rewards.mad_science = slice_i32(graw, 36);
            sh.brightest_flame_max_hp_spent = slice_i32(graw, 40);
        }
        _ => return Err(()),
    }
    sh.prior_player_unblocked_hits = r.var_i32()?;
    sh.prior_player_damaged_turn = r.var_i32()?;
    sh.prior_exhaust_count = r.var_i32()?;

    out.pending = read_pending(r)?;
    out.vakuu_auto_select_depth = r.u8()?;
    if same_root && out.hook_cache.cached_tables.is_some() {
        let mut keep_clean = 0u16;
        if (pile_has_hook_bits & (1u8 << (PileType::Hand as u8))) == 0
            && out.hook_cache.listener_hand.is_empty()
            && (prev_dirty & HOOK_DIRTY_HAND) == 0
        {
            keep_clean |= HOOK_DIRTY_HAND;
        }
        if (pile_has_hook_bits & (1u8 << (PileType::Draw as u8))) == 0
            && out.hook_cache.listener_draw.is_empty()
            && (prev_dirty & HOOK_DIRTY_DRAW) == 0
        {
            keep_clean |= HOOK_DIRTY_DRAW;
        }
        if (pile_has_hook_bits & (1u8 << (PileType::Discard as u8))) == 0
            && out.hook_cache.listener_discard.is_empty()
            && (prev_dirty & HOOK_DIRTY_DISCARD) == 0
        {
            keep_clean |= HOOK_DIRTY_DISCARD;
        }
        if (pile_has_hook_bits & (1u8 << (PileType::Exhaust as u8))) == 0
            && out.hook_cache.listener_exhaust.is_empty()
            && (prev_dirty & HOOK_DIRTY_EXHAUST) == 0
        {
            keep_clean |= HOOK_DIRTY_EXHAUST;
        }
        if (pile_has_hook_bits & (1u8 << (PileType::Play as u8))) == 0
            && out.hook_cache.listener_play.is_empty()
            && (prev_dirty & HOOK_DIRTY_PLAY) == 0
        {
            keep_clean |= HOOK_DIRTY_PLAY;
        }
        if deck_unchanged && !prev_deck_dirty && (prev_dirty & HOOK_DIRTY_DECK) == 0 {
            keep_clean |= HOOK_DIRTY_DECK;
        }
        if (prev_dirty & HOOK_DIRTY_STATIC) == 0 {
            keep_clean |= HOOK_DIRTY_STATIC;
        }
        if !relics_hook_changed && (prev_dirty & HOOK_DIRTY_RELICS) == 0 {
            keep_clean |= HOOK_DIRTY_RELICS;
        }
        if !potions_changed && (prev_dirty & HOOK_DIRTY_POTIONS) == 0 {
            keep_clean |= HOOK_DIRTY_POTIONS;
        }
        if !orbs_changed && (prev_dirty & HOOK_DIRTY_ORBS) == 0 {
            keep_clean |= HOOK_DIRTY_ORBS;
        }
        if keep_clean != 0 {
            out.hook_cache.dirty_mask = HOOK_DIRTY_ALL & !keep_clean;
        } else {
            out.invalidate_hook_cache();
        }
    } else {
        out.invalidate_hook_cache();
    }
    Ok(())
}

/// Allocates a blank `BattleState` shell suitable as a reusable unpack scratch target.
pub fn empty_scratch_state(static_ctx: Arc<RootStaticContext>) -> BattleState {
    let string_seed = Arc::clone(&static_ctx.root_run_rng_string_seed);
    BattleState {
        static_ctx,
        current_side: CombatSide::Player,
        round_number: 1,
        is_in_progress: false,
        is_starting: false,
        won: false,
        is_player_extra_turn: false,
        gold_was_stolen: false,
        end_player_turn_requested: false,
        card_action_boundary_depth: 0,
        next_creature_id: 0,
        initial_encounter_monster_count: 0,
        next_card_handle: 0,
        next_creature_handle: 0,
        next_power_handle: 0,
        next_attack_id: 0,
        run_rngs: RunRngSet {
            string_seed,
            seed: 0,
            streams: [Rng::from_seed(0); 12],
        },
        player_rngs: PlayerRngSet {
            seed: 0,
            streams: [Rng::from_seed(0); 4],
        },
        player: PlayerInst {
            creature_handle: CreatureHandle(0),
            current_hp: 0,
            max_hp: 0,
            block: 0,
            cumulative_hp_lost: 0,
            gold: 0,
            max_energy: 0,
            base_orb_slot_count: 0,
            is_active_for_hooks: true,
            can_use_or_remove_potions: true,
            energy: 0,
            stars: 0,
            phase: PlayerTurnPhase::None,
            turn_number: 0,
            counters: PlayerCounters::default(),
            hand: Vec::with_capacity(12),
            draw: Vec::with_capacity(32),
            discard: Vec::with_capacity(32),
            exhaust: Vec::with_capacity(16),
            play: Vec::with_capacity(8),
            deck: Vec::with_capacity(32),
            potion_slots: SmallVec::new(),
            combat_generated_potion_mask: 0,
            orb_capacity: 0,
            orbs: Vec::new(),
            arrows: Vec::new(),
            relics: Vec::new(),
            card_or_potion_effect_depth: 0,
        },
        cards: Vec::with_capacity(64),
        card_extensions: Vec::new(),
        creatures: Vec::with_capacity(8),
        power_extensions: Vec::new(),
        ally_handles: Vec::with_capacity(4),
        enemy_handles: Vec::with_capacity(8),
        escaped_handles: SmallVec::new(),
        removed_handles: SmallVec::new(),
        spawned_enemy_handles: SmallVec::new(),
        pet_handles: SmallVec::new(),
        damage_history: Vec::new(),
        semantic_history: SemanticHistory::default(),
        pending: None,
        vakuu_auto_select_depth: 0,
        hook_cache: Default::default(),
        deck_dirty: DeckDirtyFlag(true),
        relic_dirty_mask: RelicDirtyMask(u64::MAX),
    }
}

#[inline]
fn write_canonical_card(w: &mut ByteWriter<'_>, state: &BattleState, h: CardHandle) {
    let Some(card) = state.card(h) else {
        w.u8(0);
        return;
    };
    let ext_opt = state.card_ext(card.ext_index);
    let opt_mask = u16::from(card.temp_star_cost_override_this_turn.is_some())
        | (u16::from(card.temp_cost_override_this_turn.is_some()) << 1)
        | (u16::from(card.temp_cost_override_this_turn_or_until_played.is_some()) << 2)
        | (u16::from(card.temp_cost_override_this_combat.is_some()) << 3)
        | (u16::from(card.affliction.is_some()) << 4)
        | (u16::from(card.transform_into_after_play.is_some()) << 5)
        | (u16::from(ext_opt.is_some()) << 6)
        | (u16::from(card.upgrade_level != 0) << 7)
        | (u16::from(card.captured_energy_x_value != 0) << 8)
        | (u16::from(card.base_replay_count != 0) << 9)
        | (u16::from(card.keywords_added_from_outside.bits() != 0) << 10)
        | (u16::from(card.keywords_removed_from_outside.bits() != 0) << 11)
        | (u16::from(card.temp_flags.bits() != 0) << 12);

    w.u8(1);
    w.u16(card.id as u16);
    w.i32(card.base_energy_cost as i32);
    w.u16(card.local_keywords.bits());
    w.u16(opt_mask);

    if (opt_mask & (1 << 7)) != 0 { w.i32(card.upgrade_level); }
    if (opt_mask & (1 << 8)) != 0 { w.i32(card.captured_energy_x_value as i32); }
    if (opt_mask & (1 << 9)) != 0 { w.i32(card.base_replay_count as i32); }
    if (opt_mask & (1 << 10)) != 0 { w.u16(card.keywords_added_from_outside.bits()); }
    if (opt_mask & (1 << 11)) != 0 { w.u16(card.keywords_removed_from_outside.bits()); }
    if (opt_mask & (1 << 12)) != 0 { w.u16(card.temp_flags.bits()); }
    if let Some(v) = card.temp_star_cost_override_this_turn.get() { w.i32(v); }
    if let Some(v) = card.temp_cost_override_this_turn.get() { w.i32(v); }
    if let Some(v) = card.temp_cost_override_this_turn_or_until_played.get() { w.i32(v); }
    if let Some(v) = card.temp_cost_override_this_combat.get() { w.i32(v); }
    if let Some(a) = card.affliction {
        w.u16(a.id as u16);
        w.i32(a.amount);
    }
    if let Some(cid) = card.transform_into_after_play {
        w.u16(cid as u16);
    }
    if let Some(ext) = ext_opt {
        w.var_u32(ext.local_energy_modifiers.len() as u32);
        for m in &ext.local_energy_modifiers {
            w.u8(m.kind as u8);
            w.i32(m.amount);
            w.bool(m.is_reduce_only);
        }
        match &ext.enchantment {
            Some(e) => {
                w.u8(1);
                w.u16(e.id as u16);
                w.i32(e.amount);
                w.u8(e.status as u8);
                w.field_map(&e.private_fields);
            }
            None => w.u8(0),
        }
        w.field_map(&ext.private_fields);
    }
}

#[inline]
fn write_player_counters(w: &mut ByteWriter<'_>, c: &PlayerCounters) {
    let stars_nz = !c.stars_gained_this_turn.is_zero();
    let fool_or = c.wah_started_this_turn
        | c.cho_started_this_turn
        | c.ki_started_this_turn
        | c.first_wah_play_ordinal_this_turn
        | c.first_cho_play_ordinal_this_turn
        | c.first_ki_play_ordinal_this_turn
        | c.wah_finished_this_turn
        | c.cho_finished_this_turn
        | c.ki_finished_this_turn
        | c.melody_cards_finished_this_turn;
    let mut c_mask: u32 = u32::from(stars_nz)
        | (u32::from(c.skill_cards_played_this_turn != 0) << 1)
        | (u32::from(c.attack_cards_played_this_turn != 0) << 2)
        | (u32::from(c.shivs_played_this_turn != 0) << 3)
        | (u32::from(c.cards_discarded_this_turn != 0) << 4)
        | (u32::from(c.cards_played_this_combat != 0) << 5)
        | (u32::from(c.cards_generated_this_combat != 0) << 6)
        | (u32::from(c.sadness_generated_this_combat != 0) << 7)
        | (u32::from(c.cards_drawn_this_combat != 0) << 8)
        | (u32::from(c.cards_played_this_turn != 0) << 9)
        | (u32::from(c.manual_cards_played_this_turn != 0) << 10)
        | (u32::from(c.card_plays_started_this_turn != 0) << 11)
        | (u32::from(c.attack_or_skill_card_plays_started_this_turn != 0) << 12)
        | (u32::from(c.attack_cards_started_this_turn != 0) << 13)
        | (u32::from(c.zero_cost_attacks_started_this_turn != 0) << 14)
        | (u32::from(c.first_in_series_card_plays_started_this_turn != 0) << 15);
    if fool_or != 0 {
        c_mask |= (u32::from(c.wah_started_this_turn != 0) << 16)
            | (u32::from(c.cho_started_this_turn != 0) << 17)
            | (u32::from(c.ki_started_this_turn != 0) << 18)
            | (u32::from(c.first_wah_play_ordinal_this_turn != 0) << 19)
            | (u32::from(c.first_cho_play_ordinal_this_turn != 0) << 20)
            | (u32::from(c.first_ki_play_ordinal_this_turn != 0) << 21)
            | (u32::from(c.wah_finished_this_turn != 0) << 22)
            | (u32::from(c.cho_finished_this_turn != 0) << 23)
            | (u32::from(c.ki_finished_this_turn != 0) << 24)
            | (u32::from(c.melody_cards_finished_this_turn != 0) << 25);
    }
    if c_mask == 0 {
        w.u32(0);
        return;
    }
    w.ensure(124);
    unsafe {
        w.u32_unchecked(c_mask);
        if stars_nz {
            w.dec_unchecked_with_tail(c.stars_gained_this_turn, 104);
        }
        let mut p = w.cur;
        macro_rules! write_nz_i32 {
            ($val:expr) => {{
                let v: i32 = $val;
                std::ptr::write_unaligned(p.cast::<i32>(), v.to_le());
                p = p.add(usize::from(v != 0) * 4);
            }};
        }
        write_nz_i32!(c.skill_cards_played_this_turn);
        write_nz_i32!(c.attack_cards_played_this_turn);
        write_nz_i32!(c.shivs_played_this_turn);
        write_nz_i32!(c.cards_discarded_this_turn);
        write_nz_i32!(c.cards_played_this_combat);
        write_nz_i32!(c.cards_generated_this_combat);
        write_nz_i32!(c.sadness_generated_this_combat);
        write_nz_i32!(c.cards_drawn_this_combat);
        write_nz_i32!(c.cards_played_this_turn);
        write_nz_i32!(c.manual_cards_played_this_turn);
        write_nz_i32!(c.card_plays_started_this_turn);
        write_nz_i32!(c.attack_or_skill_card_plays_started_this_turn);
        write_nz_i32!(c.attack_cards_started_this_turn);
        write_nz_i32!(c.zero_cost_attacks_started_this_turn);
        write_nz_i32!(c.first_in_series_card_plays_started_this_turn);
        if fool_or != 0 {
            write_nz_i32!(c.wah_started_this_turn);
            write_nz_i32!(c.cho_started_this_turn);
            write_nz_i32!(c.ki_started_this_turn);
            write_nz_i32!(c.first_wah_play_ordinal_this_turn);
            write_nz_i32!(c.first_cho_play_ordinal_this_turn);
            write_nz_i32!(c.first_ki_play_ordinal_this_turn);
            write_nz_i32!(c.wah_finished_this_turn);
            write_nz_i32!(c.cho_finished_this_turn);
            write_nz_i32!(c.ki_finished_this_turn);
            write_nz_i32!(c.melody_cards_finished_this_turn);
        }
        w.cur = p;
    }
}

/// Packs the canonical combat-equivalence key into `out` (strips transient raw `CardHandle`s while
/// preserving exact pile order, card properties, creature states, powers, relics, potions, orbs, and RNGs).
pub fn pack_canonical_key(state: &BattleState, out: &mut Vec<u8>) {
    out.clear();
    let mut w = ByteWriter::new(out);

    w.u8(state.current_side as u8);
    w.i32(state.round_number);
    w.bool(state.is_in_progress);
    w.bool(state.won);
    w.bool(state.is_player_extra_turn);
    w.bool(state.gold_was_stolen);
    w.bool(state.end_player_turn_requested);

    // 9 combat RNG streams + shuffle + non-combat generation
    for stream in &state.run_rngs.streams {
        w.rng(stream);
    }
    w.rng(&state.player_rngs.streams[3]); // NonCombatGeneration

    let p = &state.player;
    w.i32(p.current_hp);
    w.i32(p.max_hp);
    w.i32(p.block);
    w.i32(p.cumulative_hp_lost);
    w.i32(p.gold);
    w.i32(p.max_energy);
    w.i32(p.energy);
    w.i32(p.stars);
    w.u8(p.phase as u8);
    w.i32(p.turn_number);
    w.i32(p.orb_capacity);

    write_player_counters(&mut w, &p.counters);

    for pile in [&p.hand[..], &p.draw[..], &p.discard[..], &p.exhaust[..], &p.play[..]] {
        w.var_u32(pile.len() as u32);
        for &h in pile {
            write_canonical_card(&mut w, state, h);
        }
    }

    w.u16(p.combat_generated_potion_mask);
    w.var_u32(p.potion_slots.len() as u32);
    for &slot in &p.potion_slots {
        match slot {
            Some(pid) => {
                w.u8(1);
                w.u16(pid as u16);
            }
            None => w.u8(0),
        }
    }

    w.var_u32(p.orbs.len() as u32);
    for orb in &p.orbs {
        w.u8(orb.id as u8);
        w.opt_i32(orb.raw_evoke_value.and_then(|d| d.to_i32_trunc().ok()));
        w.opt_i32(orb.raw_passive_value.and_then(|d| d.to_i32_trunc().ok()));
    }

    w.var_u32(p.arrows.len() as u32);
    for arrow in &p.arrows {
        w.u8(arrow.status as u8);
        w.bool(arrow.destroy_after_impaled);
    }

    w.var_u32(p.relics.len() as u32);
    for relic in &p.relics {
        w.u16(relic.id as u16);
        w.bool(relic.is_wax);
        w.bool(relic.is_melted);
        w.i32(relic.stack_count);
        w.field_map(&relic.private_fields);
    }

    // Creatures (allies, enemies, pets, escaped)
    w.var_u32(state.creatures.len() as u32);
    for cr in &state.creatures {
        w.opt_u32(cr.combat_id);
        w.u8(cr.side as u8);
        w.bool(cr.is_player);
        w.bool(cr.is_detached);
        w.bool(state.contains_creature(cr.handle));
        w.i32(cr.current_hp);
        w.i32(cr.max_hp);
        w.i32(cr.block);
        w.i32(cr.cumulative_hp_lost);
        match &cr.monster {
            Some(m) => {
                w.u8(1);
                w.u16(m.monster_id as u16);
                w.rng(&m.rng);
                w.bool(m.spawned_this_turn);
                w.str(&m.current_state_id);
                w.opt_str(m.next_move_id.as_deref());
                w.bool(m.next_move_can_transition_away);
                w.bool(m.performed_first_move);
                w.var_u32(m.state_log_ids.len() as u32);
                for s in &m.state_log_ids {
                    w.str(s);
                }
                w.opt_str(m.transient_move_follow_up_state_id.as_deref());
                w.field_map(&m.private_fields);
            }
            None => w.u8(0),
        }
        w.var_u32(cr.powers.len() as u32);
        for pw in &cr.powers {
            w.u16(pw.id as u16);
            w.i32(pw.amount);
            w.i32(pw.amount_on_turn_start);
            w.bool(pw.skip_next_duration_tick);
            if let Some(ext_idx) = pw.ext_index {
                if let Some(pe) = state.power_extensions.get(ext_idx as usize) {
                    w.u8(1);
                    w.field_map(&pe.private_fields);
                    w.i32(pe.hit_counter);
                    w.var_u32(pe.impaled_arrows.len() as u32);
                    for &ia in &pe.impaled_arrows {
                        w.var_u32(ia as u32);
                    }
                } else {
                    w.u8(0);
                }
            } else {
                w.u8(0);
            }
        }
    }

    let gr = &state.semantic_history.growth_rewards;
    let g_mask: u16 = u16::from(gr.hand_of_greed != 0)
        | (u16::from(gr.the_hunt != 0) << 1)
        | (u16::from(gr.feed != 0) << 2)
        | (u16::from(gr.royalties != 0) << 3)
        | (u16::from(gr.alchemize != 0) << 4)
        | (u16::from(gr.genetic_algorithm != 0) << 5)
        | (u16::from(gr.the_scythe != 0) << 6)
        | (u16::from(gr.goopy != 0) << 7)
        | (u16::from(gr.forbidden_grimoire != 0) << 8)
        | (u16::from(gr.mad_science != 0) << 9);
    w.u16(g_mask);
    if g_mask != 0 {
        if (g_mask & (1 << 0)) != 0 { w.i32(gr.hand_of_greed); }
        if (g_mask & (1 << 1)) != 0 { w.i32(gr.the_hunt); }
        if (g_mask & (1 << 2)) != 0 { w.i32(gr.feed); }
        if (g_mask & (1 << 3)) != 0 { w.i32(gr.royalties); }
        if (g_mask & (1 << 4)) != 0 { w.i32(gr.alchemize); }
        if (g_mask & (1 << 5)) != 0 { w.i32(gr.genetic_algorithm); }
        if (g_mask & (1 << 6)) != 0 { w.i32(gr.the_scythe); }
        if (g_mask & (1 << 7)) != 0 { w.i32(gr.goopy); }
        if (g_mask & (1 << 8)) != 0 { w.i32(gr.forbidden_grimoire); }
        if (g_mask & (1 << 9)) != 0 { w.i32(gr.mad_science); }
    }
    w.i32(state.semantic_history.brightest_flame_max_hp_spent);
    w.i32(state.semantic_history.prior_player_unblocked_hits);
    w.i32(state.semantic_history.prior_player_damaged_turn);
    w.i32(state.semantic_history.prior_exhaust_count);

    w.bool(state.pending.is_some());
}

#[inline(always)]
fn hash_opt_i32(h: &mut WordHasher128, v: Option<i32>) {
    match v {
        Some(x) => h.mix_u64(1 | (((x as u32) as u64) << 1)),
        None => h.mix_u64(0),
    }
}

#[inline(always)]
fn hash_opt_str(h: &mut WordHasher128, s: Option<&str>) {
    match s {
        Some(v) => {
            h.mix_u32(1);
            h.mix_u32(v.len() as u32);
            h.mix_bytes(v.as_bytes());
        }
        None => h.mix_u32(0),
    }
}

#[inline(always)]
fn hash_rng(h: &mut WordHasher128, r: &Rng) {
    h.mix_u64x4(r.random.s0, r.random.s1, r.random.s2, r.random.s3);
    h.mix_u64(r.counter as u32 as u64);
}

#[inline(always)]
fn hash_dec(h: &mut WordHasher128, v: Dec) {
    let b = v.as_decimal().serialize();
    let lo = u64::from_le_bytes(b[0..8].try_into().unwrap());
    let hi = u64::from_le_bytes(b[8..16].try_into().unwrap());
    h.mix_u64x2(lo, hi);
}

#[inline(always)]
fn hash_field_map(h: &mut WordHasher128, map: &FieldMap) {
    if map.is_empty() {
        h.mix_u32(0);
    } else {
        hash_field_map_nonempty(h, map);
    }
}

#[inline(never)]
fn hash_field_map_nonempty(h: &mut WordHasher128, map: &FieldMap) {
    h.mix_u32(map.len() as u32);
    for (k, v) in map {
        h.mix_u32(k.len() as u32);
        h.mix_bytes(k.as_bytes());
        match v {
            FieldValue::Int(x) => h.mix_u64x2(0, *x as u32 as u64),
            FieldValue::Decimal(d) => {
                h.mix_u32(1);
                hash_dec(h, *d);
            }
            FieldValue::Bool(b) => h.mix_u64x2(2, u64::from(*b)),
            FieldValue::String(s) => {
                h.mix_u32(3);
                h.mix_u32(s.len() as u32);
                h.mix_bytes(s.as_bytes());
            }
            FieldValue::NullableInt(x) => {
                h.mix_u32(4);
                hash_opt_i32(h, *x);
            }
            FieldValue::CardHandle(ch) => h.mix_u64x2(5, *ch as u32 as u64),
            FieldValue::CreatureHandle(ch) => h.mix_u64x2(6, *ch as u32 as u64),
            FieldValue::IntArray(arr) => {
                h.mix_u64x2(7, arr.len() as u64);
                for &x in arr {
                    h.mix_i32(x);
                }
            }
            FieldValue::CardHandleArray(arr) => {
                h.mix_u64x2(8, arr.len() as u64);
                for &x in arr {
                    h.mix_i32(x);
                }
            }
            FieldValue::CreatureHandleArray(arr) => {
                h.mix_u64x2(9, arr.len() as u64);
                for &x in arr {
                    h.mix_i32(x);
                }
            }
            FieldValue::CardIntMap(arr) => {
                h.mix_u64x2(10, arr.len() as u64);
                for &(a, b) in arr {
                    h.mix_u64x2(a as u32 as u64, b as u32 as u64);
                }
            }
            FieldValue::CreatureDecimalMap(arr) => {
                h.mix_u64x2(11, arr.len() as u64);
                for &(a, d) in arr {
                    h.mix_i32(a);
                    hash_dec(h, d);
                }
            }
            FieldValue::CardPairArray(arr) => {
                h.mix_u64x2(12, arr.len() as u64);
                for &(a, b) in arr {
                    h.mix_u64x2(a as u32 as u64, b as u32 as u64);
                }
            }
        }
    }
}

#[inline(always)]
fn flush_pending_fast_cards(
    h: &mut WordHasher128,
    pending_buf: &mut [u64; 4],
    pending_len: &mut usize,
) {
    match *pending_len {
        0 => {}
        1 => h.mix_u64(pending_buf[0]),
        2 => h.mix_u64x2(pending_buf[0], pending_buf[1]),
        3 => {
            h.mix_u64x2(pending_buf[0], pending_buf[1]);
            h.mix_u64(pending_buf[2]);
        }
        _ => h.mix_u64x4(
            pending_buf[0],
            pending_buf[1],
            pending_buf[2],
            pending_buf[3],
        ),
    }
    *pending_len = 0;
}

#[inline(never)]
fn hash_canonical_card_slow(
    h: &mut WordHasher128,
    card_extensions: &[CardExtension],
    card: &CardInst,
    pending_buf: &mut [u64; 4],
    pending_len: &mut usize,
) {
    if card.is_rare_clean()
        && card.affliction.is_none()
        && card.transform_into_after_play.is_none()
    {
        if let Some(ei) = card.ext_index {
            if let Some(ext) = card_extensions.get(ei as usize) {
                flush_pending_fast_cards(h, pending_buf, pending_len);
                let slow_tag = (1u64 << 14)
                    | (card.id as u64)
                    | ((card.local_keywords.bits() as u64) << 16)
                    | ((1u64 << 6) << 32);
                let cost_upg_word = (card.base_energy_cost as u32 as u64)
                    | ((card.upgrade_level as u32 as u64) << 32);
                h.mix_u64x2(slow_tag, cost_upg_word);
                h.mix_u32(ext.local_energy_modifiers.len() as u32);
                for m in &ext.local_energy_modifiers {
                    h.mix_u64(
                        ((m.kind as u64) << 40)
                            | ((u64::from(m.is_reduce_only)) << 32)
                            | (m.amount as u32 as u64),
                    );
                }
                match &ext.enchantment {
                    Some(e) => {
                        h.mix_u64(
                            1 | ((e.id as u64) << 8)
                                | ((e.status as u64) << 24)
                                | ((e.amount as u32 as u64) << 32),
                        );
                        hash_field_map(h, &e.private_fields);
                    }
                    None => h.mix_u64(0),
                }
                hash_field_map(h, &ext.private_fields);
                return;
            }
        }
    }
    let has_ext = card
        .ext_index
        .is_some_and(|i| (i as usize) < card_extensions.len());
    let rare_mask = u16::from(card.temp_star_cost_override_this_turn.is_some())
        | (u16::from(card.temp_cost_override_this_turn.is_some()) << 1)
        | (u16::from(card.temp_cost_override_this_turn_or_until_played.is_some()) << 2)
        | (u16::from(card.temp_cost_override_this_combat.is_some()) << 3)
        | (u16::from(card.affliction.is_some()) << 4)
        | (u16::from(card.transform_into_after_play.is_some()) << 5)
        | (u16::from(has_ext) << 6)
        | (u16::from(card.captured_energy_x_value != 0) << 7)
        | (u16::from(card.base_replay_count != 0) << 8)
        | (u16::from(card.keywords_added_from_outside.bits() != 0) << 9)
        | (u16::from(card.keywords_removed_from_outside.bits() != 0) << 10)
        | (u16::from(card.temp_flags.bits() != 0) << 11);

    if rare_mask == 0
        && (-32768..=32767).contains(&card.base_energy_cost)
        && (-32768..=32767).contains(&card.upgrade_level)
    {
        debug_assert!((card.id as usize) < 1024);
        let packed = (card.id as u64)
            | ((card.local_keywords.bits() as u64) << 16)
            | (((card.base_energy_cost as i16 as u16) as u64) << 32)
            | (((card.upgrade_level as i16 as u16) as u64) << 48);
        pending_buf[*pending_len] = packed;
        *pending_len += 1;
        if *pending_len == 4 {
            h.mix_u64x4(
                pending_buf[0],
                pending_buf[1],
                pending_buf[2],
                pending_buf[3],
            );
            *pending_len = 0;
        }
        return;
    }

    flush_pending_fast_cards(h, pending_buf, pending_len);
    let slow_tag = (1u64 << 14)
        | (card.id as u64)
        | ((card.local_keywords.bits() as u64) << 16)
        | ((rare_mask as u64) << 32);
    let cost_upg_word = (card.base_energy_cost as u32 as u64) | ((card.upgrade_level as u32 as u64) << 32);
    h.mix_u64x2(slow_tag, cost_upg_word);

    if (rare_mask & (1 << 7)) != 0 {
        h.mix_i32(card.captured_energy_x_value as i32);
    }
    if (rare_mask & (1 << 8)) != 0 {
        h.mix_i32(card.base_replay_count as i32);
    }
    if (rare_mask & ((1 << 9) | (1 << 10) | (1 << 11))) != 0 {
        let kw_word = (card.keywords_added_from_outside.bits() as u64)
            | ((card.keywords_removed_from_outside.bits() as u64) << 16)
            | ((card.temp_flags.bits() as u64) << 32);
        h.mix_u64(kw_word);
    }
    if let Some(v) = card.temp_star_cost_override_this_turn.get() {
        h.mix_i32(v);
    }
    if let Some(v) = card.temp_cost_override_this_turn.get() {
        h.mix_i32(v);
    }
    if let Some(v) = card.temp_cost_override_this_turn_or_until_played.get() {
        h.mix_i32(v);
    }
    if let Some(v) = card.temp_cost_override_this_combat.get() {
        h.mix_i32(v);
    }
    if let Some(a) = card.affliction {
        h.mix_u64(((a.id as u64) << 32) | (a.amount as u32 as u64));
    }
    if let Some(cid) = card.transform_into_after_play {
        h.mix_u32(cid as u32);
    }
    if has_ext {
        let ext = &card_extensions[card.ext_index.unwrap() as usize];
        h.mix_u32(ext.local_energy_modifiers.len() as u32);
        for m in &ext.local_energy_modifiers {
            h.mix_u64(
                ((m.kind as u64) << 40)
                    | ((u64::from(m.is_reduce_only)) << 32)
                    | (m.amount as u32 as u64),
            );
        }
        match &ext.enchantment {
            Some(e) => {
                h.mix_u64(
                    1 | ((e.id as u64) << 8)
                        | ((e.status as u64) << 24)
                        | ((e.amount as u32 as u64) << 32),
                );
                hash_field_map(h, &e.private_fields);
            }
            None => h.mix_u64(0),
        }
        hash_field_map(h, &ext.private_fields);
    }
}

#[cold]
#[inline(never)]
fn find_card_hash_cold(cards: &[CardInst], ch: CardHandle) -> Option<&CardInst> {
    cards.iter().find(|c| c.handle == ch)
}

#[inline(always)]
fn hash_canonical_card(
    h: &mut WordHasher128,
    cards: &[CardInst],
    card_extensions: &[CardExtension],
    ch: CardHandle,
    pending_buf: &mut [u64; 4],
    pending_len: &mut usize,
) {
    let idx = ch.0.wrapping_sub(1) as usize;
    let card = if idx < cards.len() && cards[idx].handle == ch {
        &cards[idx]
    } else if let Some(c) = find_card_hash_cold(cards, ch) {
        c
    } else {
        flush_pending_fast_cards(h, pending_buf, pending_len);
        h.mix_u64(1u64 << 15);
        return;
    };
    if card.ext_index.is_none()
        && card.affliction.is_none()
        && card.transform_into_after_play.is_none()
        && card.is_rare_clean()
        && ((card.upgrade_level.wrapping_add(32768) as u32) & !0xFFFFu32) == 0
    {
        debug_assert!((card.id as usize) < 1024);
        let packed = (card.id as u64)
            | ((card.local_keywords.bits() as u64) << 16)
            | (((card.base_energy_cost as u16) as u64) << 32)
            | (((card.upgrade_level as i16 as u16) as u64) << 48);
        pending_buf[*pending_len] = packed;
        *pending_len += 1;
        if *pending_len == 4 {
            h.mix_u64x4(
                pending_buf[0],
                pending_buf[1],
                pending_buf[2],
                pending_buf[3],
            );
            *pending_len = 0;
        }
        return;
    }

    hash_canonical_card_slow(h, card_extensions, card, pending_buf, pending_len);
}

#[inline(always)]
fn hash_player_counters(h: &mut WordHasher128, c: &PlayerCounters) {
    let fool_or = c.wah_started_this_turn
        | c.cho_started_this_turn
        | c.ki_started_this_turn
        | c.first_wah_play_ordinal_this_turn
        | c.first_cho_play_ordinal_this_turn
        | c.first_ki_play_ordinal_this_turn
        | c.wah_finished_this_turn
        | c.cho_finished_this_turn
        | c.ki_finished_this_turn
        | c.melody_cards_finished_this_turn;
    let mut c_mask: u32 = u32::from(c.stars_gained_this_turn != Dec::ZERO)
        | (u32::from(c.skill_cards_played_this_turn != 0) << 1)
        | (u32::from(c.attack_cards_played_this_turn != 0) << 2)
        | (u32::from(c.shivs_played_this_turn != 0) << 3)
        | (u32::from(c.cards_discarded_this_turn != 0) << 4)
        | (u32::from(c.cards_played_this_combat != 0) << 5)
        | (u32::from(c.cards_generated_this_combat != 0) << 6)
        | (u32::from(c.sadness_generated_this_combat != 0) << 7)
        | (u32::from(c.cards_drawn_this_combat != 0) << 8)
        | (u32::from(c.cards_played_this_turn != 0) << 9)
        | (u32::from(c.manual_cards_played_this_turn != 0) << 10)
        | (u32::from(c.card_plays_started_this_turn != 0) << 11)
        | (u32::from(c.attack_or_skill_card_plays_started_this_turn != 0) << 12)
        | (u32::from(c.attack_cards_started_this_turn != 0) << 13)
        | (u32::from(c.zero_cost_attacks_started_this_turn != 0) << 14)
        | (u32::from(c.first_in_series_card_plays_started_this_turn != 0) << 15);
    if fool_or != 0 {
        c_mask |= (u32::from(c.wah_started_this_turn != 0) << 16)
            | (u32::from(c.cho_started_this_turn != 0) << 17)
            | (u32::from(c.ki_started_this_turn != 0) << 18)
            | (u32::from(c.first_wah_play_ordinal_this_turn != 0) << 19)
            | (u32::from(c.first_cho_play_ordinal_this_turn != 0) << 20)
            | (u32::from(c.first_ki_play_ordinal_this_turn != 0) << 21)
            | (u32::from(c.wah_finished_this_turn != 0) << 22)
            | (u32::from(c.cho_finished_this_turn != 0) << 23)
            | (u32::from(c.ki_finished_this_turn != 0) << 24)
            | (u32::from(c.melody_cards_finished_this_turn != 0) << 25);
    }
    h.mix_u32(c_mask);
    if c_mask != 0 {
        if (c_mask & (1 << 0)) != 0 { hash_dec(h, c.stars_gained_this_turn); }
        if (c_mask & (1 << 1)) != 0 { h.mix_i32(c.skill_cards_played_this_turn); }
        if (c_mask & (1 << 2)) != 0 { h.mix_i32(c.attack_cards_played_this_turn); }
        if (c_mask & (1 << 3)) != 0 { h.mix_i32(c.shivs_played_this_turn); }
        if (c_mask & (1 << 4)) != 0 { h.mix_i32(c.cards_discarded_this_turn); }
        if (c_mask & (1 << 5)) != 0 { h.mix_i32(c.cards_played_this_combat); }
        if (c_mask & (1 << 6)) != 0 { h.mix_i32(c.cards_generated_this_combat); }
        if (c_mask & (1 << 7)) != 0 { h.mix_i32(c.sadness_generated_this_combat); }
        if (c_mask & (1 << 8)) != 0 { h.mix_i32(c.cards_drawn_this_combat); }
        if (c_mask & (1 << 9)) != 0 { h.mix_i32(c.cards_played_this_turn); }
        if (c_mask & (1 << 10)) != 0 { h.mix_i32(c.manual_cards_played_this_turn); }
        if (c_mask & (1 << 11)) != 0 { h.mix_i32(c.card_plays_started_this_turn); }
        if (c_mask & (1 << 12)) != 0 { h.mix_i32(c.attack_or_skill_card_plays_started_this_turn); }
        if (c_mask & (1 << 13)) != 0 { h.mix_i32(c.attack_cards_started_this_turn); }
        if (c_mask & (1 << 14)) != 0 { h.mix_i32(c.zero_cost_attacks_started_this_turn); }
        if (c_mask & (1 << 15)) != 0 { h.mix_i32(c.first_in_series_card_plays_started_this_turn); }
        if fool_or != 0 {
            if (c_mask & (1 << 16)) != 0 { h.mix_i32(c.wah_started_this_turn); }
            if (c_mask & (1 << 17)) != 0 { h.mix_i32(c.cho_started_this_turn); }
            if (c_mask & (1 << 18)) != 0 { h.mix_i32(c.ki_started_this_turn); }
            if (c_mask & (1 << 19)) != 0 { h.mix_i32(c.first_wah_play_ordinal_this_turn); }
            if (c_mask & (1 << 20)) != 0 { h.mix_i32(c.first_cho_play_ordinal_this_turn); }
            if (c_mask & (1 << 21)) != 0 { h.mix_i32(c.first_ki_play_ordinal_this_turn); }
            if (c_mask & (1 << 22)) != 0 { h.mix_i32(c.wah_finished_this_turn); }
            if (c_mask & (1 << 23)) != 0 { h.mix_i32(c.cho_finished_this_turn); }
            if (c_mask & (1 << 24)) != 0 { h.mix_i32(c.ki_finished_this_turn); }
            if (c_mask & (1 << 25)) != 0 { h.mix_i32(c.melody_cards_finished_this_turn); }
        }
    }
}

#[inline(always)]
fn hash_canonical_pile(
    h: &mut WordHasher128,
    cards_slice: &[CardInst],
    card_extensions: &[CardExtension],
    pile: &[CardHandle],
    pending_buf: &mut [u64; 4],
    pending_len: &mut usize,
) {
    flush_pending_fast_cards(h, pending_buf, pending_len);
    h.mix_u32(pile.len() as u32);
    let len = pile.len();
    for i in 0..len {
        #[cfg(target_arch = "x86_64")]
        if i + 2 < len {
            let next_idx = pile[i + 2].0.wrapping_sub(1) as usize;
            if let Some(next_card) = cards_slice.get(next_idx) {
                unsafe {
                    std::arch::x86_64::_mm_prefetch(
                        (next_card as *const CardInst).cast::<i8>(),
                        std::arch::x86_64::_MM_HINT_T0,
                    );
                }
            }
        }
        hash_canonical_card(
            h,
            cards_slice,
            card_extensions,
            pile[i],
            pending_buf,
            pending_len,
        );
    }
}

/// Zero-allocation direct word-accumulator 128-bit canonical equivalence hash.
pub fn hash_canonical_key(state: &BattleState) -> crate::hash::StateHash {
    let mut h = WordHasher128::new();
    let sctx: &RootStaticContext = &state.static_ctx;
    let rs = &state.run_rngs.streams;
    let p3 = &state.player_rngs.streams[3];
    let rng_delta_mask: u16 = if sctx.has_root_rngs {
        let mut m = 0u16;
        for i in 0..12 {
            if rs[i].counter != sctx.root_run_rng_counters[i] {
                m |= 1u16 << i;
            } else {
                #[cfg(debug_assertions)]
                debug_assert_eq!(
                    rs[i], sctx.root_run_rngs[i],
                    "run_rng[{i}] diverged without counter advance"
                );
            }
        }
        if p3.counter != sctx.root_player_rng_counters[3] {
            m |= 1u16 << 12;
        } else {
            #[cfg(debug_assertions)]
            debug_assert_eq!(
                *p3, sctx.root_player_rngs[3],
                "player_rng[3] diverged without counter advance"
            );
        }
        m
    } else {
        0x1FFF
    };

    let gflags = (state.current_side as u64)
        | (u64::from(state.is_in_progress) << 8)
        | (u64::from(state.won) << 9)
        | (u64::from(state.is_player_extra_turn) << 10)
        | (u64::from(state.gold_was_stolen) << 11)
        | (u64::from(state.end_player_turn_requested) << 12)
        | ((rng_delta_mask as u64) << 16)
        | (u64::from(sctx.has_root_rngs) << 29)
        | ((state.round_number as u32 as u64) << 32);
    h.mix_u64(gflags);

    let mut rem = rng_delta_mask & 0x0FFF;
    while rem != 0 {
        let i = rem.trailing_zeros() as usize;
        rem &= rem - 1;
        hash_rng(&mut h, &rs[i]);
    }
    if (rng_delta_mask & (1 << 12)) != 0 {
        hash_rng(&mut h, p3);
    }

    let p = &state.player;
    h.mix_u64x4(
        (p.current_hp as u32 as u64) | ((p.max_hp as u32 as u64) << 32),
        (p.block as u32 as u64) | ((p.cumulative_hp_lost as u32 as u64) << 32),
        (p.gold as u32 as u64) | ((p.max_energy as u32 as u64) << 32),
        (p.energy as u32 as u64) | ((p.stars as u32 as u64) << 32),
    );
    h.mix_u64x2(
        (p.phase as u64) | ((p.turn_number as u32 as u64) << 32),
        p.orb_capacity as u32 as u64,
    );

    hash_player_counters(&mut h, &p.counters);

    let mut pending_buf = [0u64; 4];
    let mut pending_len = 0usize;
    let cards_slice = state.cards.as_slice();
    let card_extensions = &state.card_extensions;
    hash_canonical_pile(&mut h, cards_slice, card_extensions, &p.hand, &mut pending_buf, &mut pending_len);
    hash_canonical_pile(&mut h, cards_slice, card_extensions, &p.draw, &mut pending_buf, &mut pending_len);
    hash_canonical_pile(&mut h, cards_slice, card_extensions, &p.discard, &mut pending_buf, &mut pending_len);
    hash_canonical_pile(&mut h, cards_slice, card_extensions, &p.exhaust, &mut pending_buf, &mut pending_len);
    hash_canonical_pile(&mut h, cards_slice, card_extensions, &p.play, &mut pending_buf, &mut pending_len);
    flush_pending_fast_cards(&mut h, &mut pending_buf, &mut pending_len);

    h.mix_u64((p.combat_generated_potion_mask as u64) | ((p.potion_slots.len() as u64) << 16));
    for &slot in &p.potion_slots {
        match slot {
            Some(pid) => h.mix_u64(1 | ((pid as u64) << 8)),
            None => h.mix_u64(0),
        }
    }

    h.mix_u32(p.orbs.len() as u32);
    for orb in &p.orbs {
        h.mix_u32(orb.id as u32);
        hash_opt_i32(&mut h, orb.raw_evoke_value.and_then(|d| d.to_i32_trunc().ok()));
        hash_opt_i32(&mut h, orb.raw_passive_value.and_then(|d| d.to_i32_trunc().ok()));
    }

    h.mix_u32(p.arrows.len() as u32);
    for arrow in &p.arrows {
        h.mix_u64((arrow.status as u64) | (u64::from(arrow.destroy_after_impaled) << 8));
    }

    h.mix_u32(p.relics.len() as u32);
    for relic in &p.relics {
        h.mix_u64x2(
            (relic.id as u64)
                | (u64::from(relic.is_wax) << 16)
                | (u64::from(relic.is_melted) << 17),
            relic.stack_count as u32 as u64,
        );
        hash_field_map(&mut h, &relic.private_fields);
    }

    let (in_combat_bits, in_combat_ovf) =
        if (state.hook_cache.dirty_mask & sts2_kernel::state::HOOK_DIRTY_CREATURES) == 0
            && state.hook_cache.cached_tables.is_some()
        {
            (
                state.hook_cache.in_combat_mask,
                state.hook_cache.in_combat_overflow,
            )
        } else {
            let mut bits = 0u64;
            let mut ovf = false;
            for &h in &state.ally_handles {
                if h.0 >= 1 && h.0 <= 64 {
                    bits |= 1u64 << (h.0 - 1);
                } else {
                    ovf = true;
                }
            }
            for &h in &state.enemy_handles {
                if h.0 >= 1 && h.0 <= 64 {
                    bits |= 1u64 << (h.0 - 1);
                } else {
                    ovf = true;
                }
            }
            (bits, ovf)
        };

    h.mix_u32(state.creatures.len() as u32);
    for cr in &state.creatures {
        let combat_id_word = match cr.combat_id {
            Some(x) => 1 | ((x as u64) << 1),
            None => 0,
        };
        let in_combat = if !in_combat_ovf && cr.handle.0 >= 1 && cr.handle.0 <= 64 {
            let res = (in_combat_bits & (1u64 << (cr.handle.0 - 1))) != 0;
            #[cfg(debug_assertions)]
            debug_assert_eq!(res, state.contains_creature(cr.handle));
            res
        } else {
            state.contains_creature(cr.handle)
        };
        let cflags = (cr.side as u64)
            | (u64::from(cr.is_player) << 8)
            | (u64::from(cr.is_detached) << 9)
            | (u64::from(in_combat) << 10)
            | ((cr.current_hp as u32 as u64) << 32);
        h.mix_u64x4(
            combat_id_word,
            cflags,
            (cr.max_hp as u32 as u64) | ((cr.block as u32 as u64) << 32),
            cr.cumulative_hp_lost as u32 as u64,
        );
        match &cr.monster {
            Some(m) => {
                h.mix_u64(
                    1 | ((m.monster_id as u64) << 8)
                        | (u64::from(m.spawned_this_turn) << 24)
                        | (u64::from(m.next_move_can_transition_away) << 25)
                        | (u64::from(m.performed_first_move) << 26),
                );
                hash_rng(&mut h, &m.rng);
                let csid_bytes = m.current_state_id.as_bytes();
                h.mix_u32(csid_bytes.len() as u32);
                h.mix_bytes(csid_bytes);
                hash_opt_str(&mut h, m.next_move_id.as_deref());
                h.mix_u32(m.state_log_ids.len() as u32);
                for s in &m.state_log_ids {
                    let sb = s.as_bytes();
                    h.mix_u32(sb.len() as u32);
                    h.mix_bytes(sb);
                }
                hash_opt_str(&mut h, m.transient_move_follow_up_state_id.as_deref());
                hash_field_map(&mut h, &m.private_fields);
            }
            None => h.mix_u64(0),
        }
        h.mix_u32(cr.powers.len() as u32);
        for pw in &cr.powers {
            h.mix_u64x2(
                (pw.id as u64)
                    | (u64::from(pw.skip_next_duration_tick) << 16)
                    | ((pw.amount as u32 as u64) << 32),
                pw.amount_on_turn_start as u32 as u64,
            );
            if let Some(ext_idx) = pw.ext_index {
                if let Some(pe) = state.power_extensions.get(ext_idx as usize) {
                    h.mix_u64(
                        1 | ((pe.hit_counter as u32 as u64) << 16)
                            | ((pe.impaled_arrows.len() as u64) << 48),
                    );
                    hash_field_map(&mut h, &pe.private_fields);
                    for &ia in &pe.impaled_arrows {
                        h.mix_u32(ia as u32);
                    }
                } else {
                    h.mix_u64(0);
                }
            } else {
                h.mix_u64(0);
            }
        }
    }

    let gr = &state.semantic_history.growth_rewards;
    if gr.is_zero() {
        h.mix_u64((state.semantic_history.brightest_flame_max_hp_spent as u32 as u64) << 32);
    } else {
        let g_mask: u16 = u16::from(gr.hand_of_greed != 0)
            | (u16::from(gr.the_hunt != 0) << 1)
            | (u16::from(gr.feed != 0) << 2)
            | (u16::from(gr.royalties != 0) << 3)
            | (u16::from(gr.alchemize != 0) << 4)
            | (u16::from(gr.genetic_algorithm != 0) << 5)
            | (u16::from(gr.the_scythe != 0) << 6)
            | (u16::from(gr.goopy != 0) << 7)
            | (u16::from(gr.forbidden_grimoire != 0) << 8)
            | (u16::from(gr.mad_science != 0) << 9);
        h.mix_u64(
            (g_mask as u64)
                | ((state.semantic_history.brightest_flame_max_hp_spent as u32 as u64) << 32),
        );
        if (g_mask & (1 << 0)) != 0 { h.mix_i32(gr.hand_of_greed); }
        if (g_mask & (1 << 1)) != 0 { h.mix_i32(gr.the_hunt); }
        if (g_mask & (1 << 2)) != 0 { h.mix_i32(gr.feed); }
        if (g_mask & (1 << 3)) != 0 { h.mix_i32(gr.royalties); }
        if (g_mask & (1 << 4)) != 0 { h.mix_i32(gr.alchemize); }
        if (g_mask & (1 << 5)) != 0 { h.mix_i32(gr.genetic_algorithm); }
        if (g_mask & (1 << 6)) != 0 { h.mix_i32(gr.the_scythe); }
        if (g_mask & (1 << 7)) != 0 { h.mix_i32(gr.goopy); }
        if (g_mask & (1 << 8)) != 0 { h.mix_i32(gr.forbidden_grimoire); }
        if (g_mask & (1 << 9)) != 0 { h.mix_i32(gr.mad_science); }
    }

    let sh = &state.semantic_history;
    if sh.exhausts.is_empty()
        && sh.card_block_gains.is_empty()
        && sh.orb_channels.is_empty()
        && sh.energy_spent.is_empty()
        && sh.card_drawn.is_empty()
        && sh.actor_events.is_empty()
    {
        let total_exhausts = sh.prior_exhaust_count as u32;
        h.mix_u64x4(total_exhausts as u64, 0, 0, 0);
    } else {
        let cur_tk = state.current_turn_key();
        let ex_cur = {
            let mut n = sh.exhausts.len();
            while n > 0 && sh.exhausts[n - 1].0 == cur_tk {
                n -= 1;
            }
            sh.exhausts.len() - n
        };
        let cbg_cur = {
            let mut n = sh.card_block_gains.len();
            while n > 0 && sh.card_block_gains[n - 1].0 == cur_tk {
                n -= 1;
            }
            sh.card_block_gains.len() - n
        };
        let oc_cur = {
            let mut n = sh.orb_channels.len();
            while n > 0 && sh.orb_channels[n - 1].0 == cur_tk {
                n -= 1;
            }
            sh.orb_channels.len() - n
        };
        let cur_energy_spent: i32 = {
            let mut sum = 0i32;
            let mut n = sh.energy_spent.len();
            while n > 0 && sh.energy_spent[n - 1].0 == cur_tk {
                n -= 1;
                sum += sh.energy_spent[n].2;
            }
            sum
        };
        let cd_cur = {
            let mut n = sh.card_drawn.len();
            while n > 0 && sh.card_drawn[n - 1].0 == cur_tk {
                n -= 1;
            }
            sh.card_drawn.len() - n
        };
        let ae_start = {
            let mut n = sh.actor_events.len();
            while n > 0 && sh.actor_events[n - 1].1 == cur_tk {
                n -= 1;
            }
            n
        };
        #[cfg(debug_assertions)]
        {
            debug_assert_eq!(ex_cur, sh.exhausts.iter().filter(|(tk, _)| *tk == cur_tk).count());
            debug_assert_eq!(
                cbg_cur,
                sh.card_block_gains.iter().filter(|(tk, _, _)| *tk == cur_tk).count()
            );
            debug_assert_eq!(
                oc_cur,
                sh.orb_channels.iter().filter(|(tk, _, _)| *tk == cur_tk).count()
            );
            debug_assert_eq!(
                cur_energy_spent,
                sh.energy_spent
                    .iter()
                    .filter(|(tk, _, _)| *tk == cur_tk)
                    .map(|(_, _, amt)| *amt)
                    .sum::<i32>()
            );
            debug_assert_eq!(
                cd_cur,
                sh.card_drawn.iter().filter(|(tk, _, _, _)| *tk == cur_tk).count()
            );
            debug_assert!(sh.actor_events[..ae_start].iter().all(|(_, tk, _)| *tk != cur_tk));
        }
        let total_exhausts = (sh.prior_exhaust_count as u32).saturating_add(sh.exhausts.len() as u32);
        h.mix_u64x4(
            (total_exhausts as u64) | ((ex_cur as u64) << 32),
            ((cbg_cur as u64) << 32) | (sh.orb_channels.len() as u32 as u64),
            (oc_cur as u64) | ((cur_energy_spent as u32 as u64) << 32),
            (cd_cur as u64) | ((sh.actor_events.len() as u64) << 32),
        );
        for (kind, _, idx) in &sh.actor_events[ae_start..] {
            let kb = kind.as_bytes();
            h.mix_u32(kb.len() as u32);
            h.mix_bytes(kb);
            h.mix_u32(*idx as u32);
        }
    }

    let dh = &state.damage_history;
    let prev_turn = state.player.turn_number - 1;
    let mut prev_turn_player_damaged = sh.prior_player_damaged_turn == prev_turn;
    let (cur_player_unblocked_hits, player_turn_damage_count) = if dh.is_empty() {
        (0u32, 0u32)
    } else {
        let mut hits = 0u32;
        let mut turn_dmg = 0u32;
        let pc_handle = state.player.creature_handle;
        for e in dh {
            if e.receiver_creature_handle == pc_handle {
                if e.unblocked_damage > 0 {
                    hits += 1;
                }
                if !e.was_fully_blocked && e.player_turn_numbers.first() == Some(&prev_turn) {
                    prev_turn_player_damaged = true;
                }
            }
            if e.round_number == state.round_number && e.current_side == state.current_side {
                turn_dmg += 1;
            }
        }
        (hits, turn_dmg)
    };
    let total_player_unblocked_hits =
        (sh.prior_player_unblocked_hits as u32).saturating_add(cur_player_unblocked_hits);
    h.mix_u64x2(
        (total_player_unblocked_hits as u64) | ((player_turn_damage_count as u64) << 32),
        u64::from(prev_turn_player_damaged)
            | (u64::from(state.pending.is_some()) << 8)
            | ((sh.prior_player_damaged_turn as u32 as u64) << 32),
    );
    h.finish()
}

#[cfg(test)]
mod tests {
    use super::*;
    use sts2_types::{CharacterId, CombatSide, GameMode, RoomType, RunRngSet};

    fn dummy_ctx() -> Arc<RootStaticContext> {
        Arc::new(RootStaticContext {
            encounter_id: "PACK".to_owned(),
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
            total_floor: 1,
            ..Default::default()
        })
    }

    #[test]
    fn pack_unpack_damage_history_and_actor_events_roundtrip() {
        let mut s1 = empty_scratch_state(dummy_ctx());
        s1.run_rngs = RunRngSet::from_string_seed("PK");
        s1.damage_history.push(DamageHistoryEntry {
            receiver_creature_handle: CreatureHandle(2),
            dealer_creature_handle: Some(CreatureHandle(1)),
            original_target_creature_handle: CreatureHandle(2),
            props: ValueProp::MOVE,
            intended_hp_loss: 12,
            blocked_damage: 0,
            unblocked_damage: 12,
            overkill_damage: 0,
            was_target_killed: false,
            was_block_broken: false,
            was_fully_blocked: false,
            card_source_handle: Some(CardHandle(7)),
            round_number: 1,
            current_side: CombatSide::Player,
            player_turn_numbers: InlineI32Vec4::from_slice(&[1]),
        });
        s1.damage_history.push(DamageHistoryEntry {
            receiver_creature_handle: CreatureHandle(1),
            dealer_creature_handle: None,
            original_target_creature_handle: CreatureHandle(3),
            props: ValueProp::UNPOWERED,
            intended_hp_loss: -5,
            blocked_damage: i32::MIN,
            unblocked_damage: i32::MAX,
            overkill_damage: 4,
            was_target_killed: true,
            was_block_broken: true,
            was_fully_blocked: true,
            card_source_handle: None,
            round_number: -1,
            current_side: CombatSide::Enemy,
            player_turn_numbers: InlineI32Vec4::from_slice(&[1, 2, -3]),
        });
        s1.semantic_history.actor_events = vec![
            (
                Cow::Owned("VeryLongEventNameToEnsureCapacityReuseIsTested".to_owned()),
                TurnKey {
                    round_number: 1,
                    side: CombatSide::Player,
                    player_turn_numbers: InlineOptI32Vec4::from_slice(&[Some(1)]),
                },
                3,
            ),
            (
                Cow::Borrowed("SecondEvent"),
                TurnKey {
                    round_number: 1,
                    side: CombatSide::Enemy,
                    player_turn_numbers: InlineOptI32Vec4::new(),
                },
                9,
            ),
        ];
        s1.semantic_history.exhausts = vec![(
            TurnKey {
                round_number: 1,
                side: CombatSide::Player,
                player_turn_numbers: InlineOptI32Vec4::from_slice(&[Some(1)]),
            },
            2,
        )];
        s1.semantic_history.card_drawn = vec![
            (
                TurnKey {
                    round_number: 1,
                    side: CombatSide::Player,
                    player_turn_numbers: InlineOptI32Vec4::from_slice(&[Some(1)]),
                },
                0,
                sts2_types::CardType::Attack,
                true,
            ),
            (
                TurnKey {
                    round_number: 1,
                    side: CombatSide::Player,
                    player_turn_numbers: InlineOptI32Vec4::from_slice(&[Some(1)]),
                },
                3,
                sts2_types::CardType::Skill,
                false,
            ),
        ];
        s1.semantic_history.growth_rewards.feed = 4;
        s1.player.orbs = vec![
            OrbInst {
                id: OrbId::FrostOrb,
                raw_evoke_value: None,
                raw_passive_value: None,
            },
            OrbInst {
                id: OrbId::DarkOrb,
                raw_evoke_value: Some(Dec::from_i32(12)),
                raw_passive_value: None,
            },
        ];
        s1.pet_handles = smallvec::smallvec![CreatureHandle(9)];
        s1.semantic_history.prior_player_unblocked_hits = 2;
        s1.semantic_history.prior_player_damaged_turn = 1;
        s1.semantic_history.prior_exhaust_count = 3;

        let mut s2 = empty_scratch_state(s1.static_ctx.clone());
        s2.run_rngs = s1.run_rngs.clone();
        s2.semantic_history.actor_events = vec![(
            Cow::Borrowed("Short"),
            TurnKey {
                round_number: 1,
                side: CombatSide::Player,
                player_turn_numbers: InlineOptI32Vec4::from_slice(&[None, Some(2)]),
            },
            0,
        )];

        let mut b1 = Vec::new();
        pack_state(&s1, &mut b1);
        let mut b2 = Vec::new();
        pack_state(&s2, &mut b2);

        let mut out = empty_scratch_state(s1.static_ctx.clone());
        unpack_state(&b1, &s1.static_ctx, &mut out).unwrap();
        assert_eq!(out, s1);

        unpack_state(&b2, &s1.static_ctx, &mut out).unwrap();
        assert_eq!(out, s2);

        unpack_state(&b1, &s1.static_ctx, &mut out).unwrap();
        assert_eq!(out, s1);
    }

    #[test]
    fn hash_canonical_key_deterministic_handle_invariant_and_sensitive() {
        let mut s1 = empty_scratch_state(dummy_ctx());
        s1.run_rngs = RunRngSet::from_string_seed("HASH1");
        s1.player.current_hp = 55;
        s1.player.max_hp = 80;
        s1.player.energy = 3;
        s1.cards.push(CardInst {
            handle: CardHandle(10),
            id: CardId::StrikeIronclad,
            upgrade_level: 0,
            canonical_base_energy_cost: 1,
            base_energy_cost: 1,
            captured_energy_x_value: 0,
            base_replay_count: 0,
            floor_added_to_deck: OptI16::some(1),
            deck_card_handle: OptCardHandle::some(CardHandle(10)),
            clone_of_handle: OptCardHandle::NONE,
            temp_star_cost_override_this_turn: OptI16::NONE,
            temp_cost_override_this_turn: OptI16::NONE,
            temp_cost_override_this_turn_or_until_played: OptI16::NONE,
            temp_cost_override_this_combat: OptI16::NONE,
            local_keywords: CardKeywords::empty(),
            keywords_added_from_outside: CardKeywords::empty(),
            keywords_removed_from_outside: CardKeywords::empty(),
            temp_flags: CardTempFlags::empty(),
            affliction: None,
            ext_index: None,
            pile: PileType::Hand,
            removed_from_state: false,
            transform_into_after_play: None,
        });
        s1.player.hand.push(CardHandle(10));

        let mut s_renum = s1.clone();
        s_renum.cards[0].handle = CardHandle(99);
        s_renum.cards[0].deck_card_handle = OptCardHandle::some(CardHandle(99));
        s_renum.player.hand[0] = CardHandle(99);

        let h1 = hash_canonical_key(&s1);
        assert_eq!(h1, hash_canonical_key(&s1));
        assert_eq!(h1, hash_canonical_key(&s_renum));

        let mut s_hp = s1.clone();
        s_hp.player.current_hp = 54;
        assert_ne!(h1, hash_canonical_key(&s_hp));

        let mut s_upg = s1.clone();
        s_upg.cards[0].upgrade_level = 1;
        assert_ne!(h1, hash_canonical_key(&s_upg));

        let mut s_bound = s1.clone();
        s_bound.record_actor_event("BoundAfflicted");
        assert_ne!(h1, hash_canonical_key(&s_bound));

        let mut s_dmg = s1.clone();
        s_dmg.damage_history.push(DamageHistoryEntry {
            receiver_creature_handle: s1.player.creature_handle,
            dealer_creature_handle: Some(CreatureHandle(2)),
            original_target_creature_handle: s1.player.creature_handle,
            props: ValueProp::MOVE,
            intended_hp_loss: 5,
            blocked_damage: 0,
            unblocked_damage: 5,
            overkill_damage: 0,
            was_target_killed: false,
            was_block_broken: false,
            was_fully_blocked: false,
            card_source_handle: None,
            round_number: s1.round_number,
            current_side: s1.current_side,
            player_turn_numbers: InlineI32Vec4::one(s1.player.turn_number),
        });
        assert_ne!(h1, hash_canonical_key(&s_dmg));
    }

    #[test]
    fn pack_unpack_delta_rng_roundtrip_with_and_without_root_rngs() {
        let base_run = RunRngSet::from_string_seed("DELTA_RNG");
        let base_player = PlayerRngSet::from_seed(123456);
        let mut ctx_val = (*dummy_ctx()).clone();
        ctx_val.set_root_rngs(base_run.streams, base_player.streams);
        let ctx_with_root = Arc::new(ctx_val);

        let mut s1 = empty_scratch_state(ctx_with_root.clone());
        s1.run_rngs = base_run.clone();
        s1.player_rngs = base_player;
        let _ = s1.run_rngs.streams[2].next_int(100);
        let _ = s1.run_rngs.streams[9].next_int(50);
        let _ = s1.player_rngs.streams[3].next_int(20);

        let mut buf_delta = Vec::new();
        pack_state(&s1, &mut buf_delta);

        let mut ctx_no_root = (*dummy_ctx()).clone();
        ctx_no_root.has_root_rngs = false;
        let mut s_no_root = s1.clone();
        s_no_root.static_ctx = Arc::new(ctx_no_root);
        let mut buf_full = Vec::new();
        pack_state(&s_no_root, &mut buf_full);
        assert!(buf_delta.len() < buf_full.len());

        let mut out_delta = empty_scratch_state(ctx_with_root.clone());
        unpack_state(&buf_delta, &ctx_with_root, &mut out_delta).unwrap();
        assert_eq!(out_delta, s1);

        let mut out_full = empty_scratch_state(s_no_root.static_ctx.clone());
        unpack_state(&buf_full, &s_no_root.static_ctx, &mut out_full).unwrap();
        assert_eq!(out_full, s_no_root);

        // Root card floors + root relics delta + afflicted fast card roundtrip & reuse restore
        let mut r_ctx_val = (*dummy_ctx()).clone();
        let root_cards = vec![
            CardInst {
                handle: CardHandle(1),
                id: CardId::StrikeIronclad,
                upgrade_level: 0,
                canonical_base_energy_cost: 1,
                base_energy_cost: 1,
                captured_energy_x_value: 0,
                base_replay_count: 0,
                floor_added_to_deck: OptI16::some(5),
                deck_card_handle: OptCardHandle::some(CardHandle(1)),
                clone_of_handle: OptCardHandle::NONE,
                temp_star_cost_override_this_turn: OptI16::NONE,
                temp_cost_override_this_turn: OptI16::NONE,
                temp_cost_override_this_turn_or_until_played: OptI16::NONE,
                temp_cost_override_this_combat: OptI16::NONE,
                local_keywords: CardKeywords::empty(),
                keywords_added_from_outside: CardKeywords::empty(),
                keywords_removed_from_outside: CardKeywords::empty(),
                temp_flags: CardTempFlags::empty(),
                affliction: None,
                ext_index: None,
                pile: PileType::Hand,
                removed_from_state: false,
                transform_into_after_play: None,
            },
            CardInst {
                handle: CardHandle(2),
                id: CardId::DefendIronclad,
                upgrade_level: 1,
                canonical_base_energy_cost: 1,
                base_energy_cost: 1,
                captured_energy_x_value: 0,
                base_replay_count: 0,
                floor_added_to_deck: OptI16::some(12),
                deck_card_handle: OptCardHandle::some(CardHandle(2)),
                clone_of_handle: OptCardHandle::NONE,
                temp_star_cost_override_this_turn: OptI16::NONE,
                temp_cost_override_this_turn: OptI16::NONE,
                temp_cost_override_this_turn_or_until_played: OptI16::NONE,
                temp_cost_override_this_combat: OptI16::NONE,
                local_keywords: CardKeywords::empty(),
                keywords_added_from_outside: CardKeywords::empty(),
                keywords_removed_from_outside: CardKeywords::empty(),
                temp_flags: CardTempFlags::empty(),
                affliction: Some(AfflictionInst {
                    id: AfflictionId::Hexed,
                    amount: 2,
                }),
                ext_index: None,
                pile: PileType::Draw,
                removed_from_state: false,
                transform_into_after_play: None,
            },
        ];
        let root_relics = vec![
            RelicInst {
                id: RelicId::BurningBlood,
                is_wax: false,
                is_melted: false,
                stack_count: 0,
                floor_added_to_deck: Some(0),
                private_fields: FieldMap::new(),
            },
            RelicInst {
                id: RelicId::Nunchaku,
                is_wax: false,
                is_melted: false,
                stack_count: 4,
                floor_added_to_deck: Some(3),
                private_fields: FieldMap::new(),
            },
        ];
        r_ctx_val.set_root_card_floors(&root_cards);
        r_ctx_val.set_root_relics(&root_relics);
        let r_ctx = Arc::new(r_ctx_val);

        let mut st_clean = empty_scratch_state(r_ctx.clone());
        st_clean.run_rngs = base_run.clone();
        st_clean.cards = root_cards.clone();
        st_clean.player.hand = vec![CardHandle(1)];
        st_clean.player.draw = vec![CardHandle(2)];
        st_clean.player.relics = root_relics.clone();

        let mut st_mut = st_clean.clone();
        st_mut.player.relics[1].stack_count = 5;
        st_mut.player.relics[0].is_melted = true;

        let mut b_clean = Vec::new();
        let mut b_mut = Vec::new();
        pack_state(&st_clean, &mut b_clean);
        pack_state(&st_mut, &mut b_mut);

        let mut reuse = empty_scratch_state(r_ctx.clone());
        unpack_state(&b_mut, &r_ctx, &mut reuse).unwrap();
        assert_eq!(reuse, st_mut);
        unpack_state(&b_clean, &r_ctx, &mut reuse).unwrap();
        assert_eq!(reuse, st_clean);
    }

    #[test]
    fn relic_dirty_mask_pack_unpack_and_clone_round_trip() {
        let mut r_ctx_val = (*dummy_ctx()).clone();
        let root_relics = vec![
            RelicInst {
                id: RelicId::BurningBlood,
                is_wax: false,
                is_melted: false,
                stack_count: 0,
                floor_added_to_deck: Some(0),
                private_fields: FieldMap::new(),
            },
            RelicInst {
                id: RelicId::Nunchaku,
                is_wax: false,
                is_melted: false,
                stack_count: 4,
                floor_added_to_deck: Some(3),
                private_fields: FieldMap::new(),
            },
        ];
        r_ctx_val.set_root_relics(&root_relics);
        let r_ctx = Arc::new(r_ctx_val);

        let mut st_clean = empty_scratch_state(r_ctx.clone());
        st_clean.player.relics = root_relics.clone();
        st_clean.relic_dirty_mask = RelicDirtyMask(0);

        let mut st_mut = st_clean.clone();
        st_mut.player.relics[1].stack_count = 5;
        st_mut.mark_relic_dirty(1);
        st_mut.player.relics[0].is_melted = true;
        st_mut.mark_relic_dirty(0);

        // Reverting relic 1 back to root while its dirty bit remains set must still pack as clean.
        let mut st_reverted = st_mut.clone();
        st_reverted.player.relics[1].stack_count = 4;

        let mut b_clean = Vec::new();
        let mut b_mut = Vec::new();
        let mut b_rev = Vec::new();
        pack_state(&st_clean, &mut b_clean);
        pack_state(&st_mut, &mut b_mut);
        pack_state(&st_reverted, &mut b_rev);

        let mut reuse = empty_scratch_state(r_ctx.clone());
        reuse.player.relics = root_relics;
        reuse.relic_dirty_mask = RelicDirtyMask(0);
        unpack_state(&b_mut, &r_ctx, &mut reuse).unwrap();
        assert_eq!(reuse, st_mut);
        assert_eq!(reuse.relic_dirty_mask.0, 0b11);

        unpack_state(&b_rev, &r_ctx, &mut reuse).unwrap();
        assert_eq!(reuse, st_reverted);
        assert_eq!(reuse.relic_dirty_mask.0, 0b01);

        unpack_state(&b_clean, &r_ctx, &mut reuse).unwrap();
        assert_eq!(reuse, st_clean);
        assert_eq!(reuse.relic_dirty_mask.0, 0);

        let mut cloned = empty_scratch_state(r_ctx.clone());
        crate::coordinator::clone_battle_state_into(&st_mut, &mut cloned);
        assert_eq!(cloned, st_mut);
        assert_eq!(cloned.relic_dirty_mask.0, 0b11);
        crate::coordinator::clone_battle_state_into(&st_clean, &mut cloned);
        assert_eq!(cloned, st_clean);
        assert_eq!(cloned.relic_dirty_mask.0, 0);
    }

    #[test]
    fn hash_canonical_key_matches_wave1_reference() {
        let mut s = empty_scratch_state(dummy_ctx());
        s.run_rngs = RunRngSet::from_string_seed("WAVE1_REF");
        let _ = s.run_rngs.streams[1].next_int(17);
        let _ = s.player_rngs.streams[3].next_int(9);
        let mk_card = |id: CardId, upg: i32, cost_ovr: Option<i32>, pile: PileType, handle_id: u32| {
            let h = CardHandle(handle_id);
            CardInst {
                handle: h,
                id,
                upgrade_level: upg,
                canonical_base_energy_cost: 1,
                base_energy_cost: 1,
                captured_energy_x_value: 0,
                base_replay_count: 0,
                floor_added_to_deck: OptI16::NONE,
                deck_card_handle: OptCardHandle::some(h),
                clone_of_handle: OptCardHandle::NONE,
                temp_star_cost_override_this_turn: OptI16::NONE,
                temp_cost_override_this_turn: OptI16::from_option(cost_ovr),
                temp_cost_override_this_turn_or_until_played: OptI16::NONE,
                temp_cost_override_this_combat: OptI16::NONE,
                local_keywords: CardKeywords::empty(),
                keywords_added_from_outside: CardKeywords::empty(),
                keywords_removed_from_outside: CardKeywords::empty(),
                temp_flags: CardTempFlags::empty(),
                affliction: None,
                ext_index: None,
                pile,
                removed_from_state: false,
                transform_into_after_play: None,
            }
        };
        let p_specs: [Vec<(CardId, i32, Option<i32>)>; 5] = [
            vec![],
            vec![(CardId::StrikeIronclad, 0, None)],
            vec![
                (CardId::StrikeIronclad, 0, None),
                (CardId::DefendIronclad, 1, None),
            ],
            vec![
                (CardId::StrikeIronclad, 0, None),
                (CardId::DefendIronclad, 1, None),
                (CardId::Bash, 0, None),
            ],
            vec![
                (CardId::StrikeIronclad, 0, None),
                (CardId::DefendIronclad, 1, None),
                (CardId::Bash, 1, None),
                (CardId::DefendIronclad, 0, None),
                (CardId::StrikeIronclad, 0, Some(0)),
            ],
        ];
        s.creatures.push(CreatureInst {
            handle: CreatureHandle(1),
            combat_id: None,
            side: CombatSide::Player,
            slot_name: None,
            is_player: true,
            pet_owner_player_index: None,
            monster_id: None,
            current_hp: 70,
            max_hp: 80,
            block: 5,
            cumulative_hp_lost: 10,
            is_detached: false,
            has_minion_power: false,
            monster: None,
            powers: Vec::new(),
        });
        s.creatures.push(CreatureInst {
            handle: CreatureHandle(2),
            combat_id: Some(42),
            side: CombatSide::Enemy,
            slot_name: None,
            is_player: false,
            pet_owner_player_index: None,
            monster_id: None,
            current_hp: 50,
            max_hp: 50,
            block: 0,
            cumulative_hp_lost: 0,
            is_detached: false,
            has_minion_power: false,
            monster: None,
            powers: Vec::new(),
        });
        s.ally_handles.push(CreatureHandle(1));
        s.enemy_handles.push(CreatureHandle(2));

        let build_rotated = |state: &mut BattleState, shift: usize, handle_offset: u32| {
            state.cards.clear();
            state.player.hand.clear();
            state.player.draw.clear();
            state.player.discard.clear();
            state.player.exhaust.clear();
            state.player.play.clear();
            let mut next_h = 1 + handle_offset;
            let piles = [
                PileType::Hand,
                PileType::Draw,
                PileType::Discard,
                PileType::Exhaust,
                PileType::Play,
            ];
            for (idx, &pile) in piles.iter().enumerate() {
                let spec = &p_specs[(idx + shift) % p_specs.len()];
                for &(cid, upg, ovr) in spec {
                    let c = mk_card(cid, upg, ovr, pile, next_h);
                    next_h += 1;
                    let h = c.handle;
                    state.cards.push(c);
                    state.pile_vec_mut(pile).unwrap().push(h);
                }
                if shift == 3 && idx == 1 {
                    state.player.draw.push(CardHandle(9999));
                    let c2 = mk_card(CardId::StrikeIronclad, 1, None, pile, next_h);
                    next_h += 1;
                    let h2 = c2.handle;
                    state.cards.push(c2);
                    state.player.draw.push(h2);
                }
            }
        };

        for shift in 0..5 {
            build_rotated(&mut s, shift, 0);
            let h0 = hash_canonical_key(&s);
            build_rotated(&mut s, shift, 17);
            let h1 = hash_canonical_key(&s);
            assert_eq!(h0, h1, "hash must be handle-invariant at rotation {shift}");
        }

        // Upgraded fast-card sensitivity, rare-override sensitivity & delta-RNG sensitivity
        build_rotated(&mut s, 0, 0);
        let base_h = hash_canonical_key(&s);
        assert_eq!(s.cards[2].upgrade_level, 1);
        let mut s_unupg = s.clone();
        s_unupg.cards[2].upgrade_level = 0;
        assert_ne!(hash_canonical_key(&s_unupg), base_h);

        let mut s_ovr = s.clone();
        s_ovr.cards[0].temp_cost_override_this_turn = OptI16::some(0);
        assert_ne!(hash_canonical_key(&s_ovr), base_h);

        let mut ctx_with_roots = (*s.static_ctx).clone();
        ctx_with_roots.set_root_rngs(s.run_rngs.streams, s.player_rngs.streams);
        s.static_ctx = Arc::new(ctx_with_roots);

        let root_delta_h = hash_canonical_key(&s);
        let _ = s.run_rngs.streams[2].next_int(11);
        assert_ne!(hash_canonical_key(&s), root_delta_h);
    }

    #[test]
    fn pack_unpack_tag2_card_and_semantic_history_round_trip() {
        let mut s = empty_scratch_state(dummy_ctx());
        s.cards.push(CardInst {
            handle: CardHandle(1),
            id: CardId::StrikeIronclad,
            upgrade_level: 1,
            canonical_base_energy_cost: 1,
            base_energy_cost: 0,
            captured_energy_x_value: 0,
            base_replay_count: 0,
            floor_added_to_deck: OptI16::NONE,
            deck_card_handle: OptCardHandle::some(CardHandle(1)),
            clone_of_handle: OptCardHandle::NONE,
            temp_star_cost_override_this_turn: OptI16::NONE,
            temp_cost_override_this_turn: OptI16::NONE,
            temp_cost_override_this_turn_or_until_played: OptI16::NONE,
            temp_cost_override_this_combat: OptI16::NONE,
            local_keywords: CardKeywords::empty(),
            keywords_added_from_outside: CardKeywords::empty(),
            keywords_removed_from_outside: CardKeywords::empty(),
            temp_flags: CardTempFlags::empty(),
            affliction: None,
            ext_index: None,
            pile: PileType::Hand,
            removed_from_state: false,
            transform_into_after_play: None,
        });
        s.player.hand.push(CardHandle(1));
        let tk1 = TurnKey {
            round_number: 1,
            side: CombatSide::Player,
            player_turn_numbers: InlineOptI32Vec4::one(Some(1)),
        };
        let tk2 = TurnKey {
            round_number: 2,
            side: CombatSide::Player,
            player_turn_numbers: InlineOptI32Vec4::one(Some(2)),
        };
        s.semantic_history.exhausts.push((tk1, 0));
        s.semantic_history.exhausts.push((tk1, 5));
        s.semantic_history.exhausts.push((tk2, 200));
        s.semantic_history.card_block_gains.push((tk1, 1, 8));
        s.semantic_history.card_block_gains.push((tk1, 2, -12));
        s.semantic_history.card_block_gains.push((tk1, 3, 100_000));
        s.semantic_history.card_block_gains.push((tk2, 130, 5));
        s.semantic_history.energy_spent.push((tk1, 0, 2));
        s.semantic_history.energy_spent.push((tk1, 4, -5));
        s.semantic_history.energy_spent.push((tk1, 6, 50_000));
        s.semantic_history.energy_spent.push((tk2, 0, 1));

        let mut buf = Vec::new();
        pack_state(&s, &mut buf);
        let mut unpacked = empty_scratch_state(dummy_ctx());
        unpack_state(&buf, &dummy_ctx(), &mut unpacked).unwrap();
        assert_eq!(unpacked.cards, s.cards);
        assert_eq!(unpacked.semantic_history, s.semantic_history);
    }

    #[test]
    fn pack_unpack_card_tags_3_and_4_round_trip() {
        let mut s = empty_scratch_state(dummy_ctx());
        s.card_extensions.push(CardExtension::default());
        let mk = |handle: u32,
                  pile: PileType,
                  ext_index: Option<u32>,
                  temp_flags: CardTempFlags| CardInst {
            handle: CardHandle(handle),
            id: CardId::StrikeIronclad,
            upgrade_level: 0,
            canonical_base_energy_cost: 1,
            base_energy_cost: 1,
            captured_energy_x_value: 0,
            base_replay_count: 0,
            floor_added_to_deck: OptI16::NONE,
            deck_card_handle: OptCardHandle::NONE,
            clone_of_handle: OptCardHandle::NONE,
            temp_star_cost_override_this_turn: OptI16::NONE,
            temp_cost_override_this_turn: OptI16::NONE,
            temp_cost_override_this_turn_or_until_played: OptI16::NONE,
            temp_cost_override_this_combat: OptI16::NONE,
            local_keywords: CardKeywords::empty(),
            keywords_added_from_outside: CardKeywords::empty(),
            keywords_removed_from_outside: CardKeywords::empty(),
            temp_flags,
            affliction: None,
            ext_index,
            pile,
            removed_from_state: false,
            transform_into_after_play: None,
        };
        s.cards.push(mk(1, PileType::Hand, Some(0), CardTempFlags::empty()));
        s.cards.push(mk(
            2,
            PileType::Discard,
            None,
            CardTempFlags::EXHAUST_ON_NEXT_PLAY | CardTempFlags::TEMPORARY_RETAIN_TURN,
        ));
        s.player.hand.push(CardHandle(1));
        s.player.discard.push(CardHandle(2));
        let mut buf = Vec::new();
        pack_state(&s, &mut buf);
        let mut unpacked = empty_scratch_state(dummy_ctx());
        unpack_state(&buf, &dummy_ctx(), &mut unpacked).unwrap();
        assert_eq!(unpacked.cards, s.cards);
        assert_eq!(unpacked.card_extensions, s.card_extensions);
    }
}
