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
    DeckDirtyFlag, EnchantmentInst, FieldMap, FieldValue, InlineI32Vec4, InlineOptI32Vec4,
    LocalEnergyModifier, MonsterInst, OrbInst, PlayerCounters, PlayerInst, PowerExtension,
    PowerInst, RelicInst, RootStaticContext, SemanticHistory, TurnKey, HOOK_DIRTY_ALL,
    HOOK_DIRTY_DECK, HOOK_DIRTY_STATIC, SYM_FALLBACK_ID,
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
}

impl<'a> ByteWriter<'a> {
    #[inline]
    fn new(buf: &'a mut Vec<u8>) -> Self {
        Self { buf }
    }

    #[cold]
    #[inline(never)]
    fn grow(&mut self, additional: usize) {
        self.buf.reserve(additional);
    }

    #[inline(always)]
    fn ensure(&mut self, additional: usize) {
        if self.buf.capacity() - self.buf.len() < additional {
            self.grow(additional);
        }
    }

    #[inline(always)]
    fn write_bytes(&mut self, bytes: &[u8]) {
        let n = bytes.len();
        let len = self.buf.len();
        if self.buf.capacity() - len < n {
            self.grow(n);
        }
        // SAFETY: `capacity() >= len + n` after the check/grow above, and `bytes` is disjoint from spare capacity.
        unsafe {
            let dst = self.buf.as_mut_ptr().add(len);
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), dst, n);
            self.buf.set_len(len + n);
        }
    }

    #[inline(always)]
    fn u8(&mut self, v: u8) {
        let len = self.buf.len();
        if len == self.buf.capacity() {
            self.grow(1);
        }
        // SAFETY: `capacity() >= len + 1` after the check/grow above.
        unsafe {
            *self.buf.as_mut_ptr().add(len) = v;
            self.buf.set_len(len + 1);
        }
    }

    #[inline(always)]
    fn bool(&mut self, v: bool) {
        self.u8(u8::from(v));
    }

    #[inline(always)]
    fn u16(&mut self, v: u16) {
        self.write_bytes(&v.to_le_bytes());
    }

    #[inline(always)]
    fn u32(&mut self, v: u32) {
        self.write_bytes(&v.to_le_bytes());
    }

    #[inline(always)]
    fn i32(&mut self, v: i32) {
        self.write_bytes(&v.to_le_bytes());
    }

    #[inline(always)]
    fn u64(&mut self, v: u64) {
        self.write_bytes(&v.to_le_bytes());
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
    fn var_u32_slow(&mut self, mut v: u32) {
        let mut tmp = [0u8; 5];
        let mut n = 0usize;
        while v >= 0x80 {
            tmp[n] = (v as u8) | 0x80;
            n += 1;
            v >>= 7;
        }
        tmp[n] = v as u8;
        n += 1;
        self.write_bytes(&tmp[..n]);
    }

    #[inline]
    fn var_i32(&mut self, v: i32) {
        let zz = (v as u32).wrapping_shl(1) ^ ((v >> 31) as u32);
        self.var_u32(zz);
    }

    #[inline]
    fn dec(&mut self, v: Dec) {
        let d = v.as_decimal();
        let m = d.mantissa();
        if let Ok(m64) = i64::try_from(m) {
            let mut raw = [0u8; 13];
            raw[0] = 0;
            raw[1..9].copy_from_slice(&m64.to_le_bytes());
            raw[9..13].copy_from_slice(&d.scale().to_le_bytes());
            self.write_bytes(&raw);
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
                let xb = x.to_le_bytes();
                self.write_bytes(&[1, xb[0], xb[1], xb[2], xb[3]]);
            }
            None => self.u8(0),
        }
    }

    #[inline(always)]
    fn opt_u32(&mut self, v: Option<u32>) {
        match v {
            Some(x) => {
                let xb = x.to_le_bytes();
                self.write_bytes(&[1, xb[0], xb[1], xb[2], xb[3]]);
            }
            None => self.u8(0),
        }
    }

    #[inline(always)]
    fn opt_u64(&mut self, v: Option<u64>) {
        match v {
            Some(x) => {
                let xb = x.to_le_bytes();
                self.write_bytes(&[
                    1, xb[0], xb[1], xb[2], xb[3], xb[4], xb[5], xb[6], xb[7],
                ]);
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

    #[inline(always)]
    fn cow_str(&mut self, s: &Cow<'static, str>) {
        match s {
            Cow::Borrowed(st) => {
                const PTR_BYTES: usize = std::mem::size_of::<usize>();
                let ptr = st.as_ptr() as usize;
                if st.len() < 0x80 {
                    let mut raw = [0u8; 2 + PTR_BYTES];
                    raw[0] = 0;
                    raw[1..1 + PTR_BYTES].copy_from_slice(&ptr.to_le_bytes());
                    raw[1 + PTR_BYTES] = st.len() as u8;
                    self.write_bytes(&raw);
                } else {
                    self.u8(0);
                    self.write_bytes(&ptr.to_le_bytes());
                    self.var_u32(st.len() as u32);
                }
            }
            Cow::Owned(st) => {
                self.u8(1);
                self.sym_str(st);
            }
        }
    }

    #[inline(always)]
    fn opt_cow_str(&mut self, s: Option<&Cow<'static, str>>) {
        match s {
            None => self.u8(0),
            Some(Cow::Borrowed(st)) => {
                const PTR_BYTES: usize = std::mem::size_of::<usize>();
                let ptr = st.as_ptr() as usize;
                if st.len() < 0x80 {
                    let mut raw = [0u8; 2 + PTR_BYTES];
                    raw[0] = 1;
                    raw[1..1 + PTR_BYTES].copy_from_slice(&ptr.to_le_bytes());
                    raw[1 + PTR_BYTES] = st.len() as u8;
                    self.write_bytes(&raw);
                } else {
                    self.u8(1);
                    self.write_bytes(&ptr.to_le_bytes());
                    self.var_u32(st.len() as u32);
                }
            }
            Some(Cow::Owned(st)) => {
                self.u8(2);
                self.sym_str(st);
            }
        }
    }

    #[inline(always)]
    fn rng(&mut self, r: &Rng) {
        let mut buf = [0u8; 36];
        buf[0..4].copy_from_slice(&r.counter.to_le_bytes());
        buf[4..12].copy_from_slice(&r.random.s0.to_le_bytes());
        buf[12..20].copy_from_slice(&r.random.s1.to_le_bytes());
        buf[20..28].copy_from_slice(&r.random.s2.to_le_bytes());
        buf[28..36].copy_from_slice(&r.random.s3.to_le_bytes());
        self.write_bytes(&buf);
    }

    fn field_map(&mut self, map: &FieldMap) {
        self.var_u32(map.len() as u32);
        for (k, v) in map {
            self.sym_str(k);
            self.field_value(v);
        }
    }

    fn cow_field_map(&mut self, map: &FieldMap) {
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
    buf: &'a [u8],
    pos: usize,
}

impl<'a> ByteReader<'a> {
    #[inline]
    fn new(buf: &'a [u8]) -> Self {
        Self { buf, pos: 0 }
    }

    #[inline(always)]
    fn take(&mut self, n: usize) -> Result<&'a [u8], ()> {
        let end = self.pos.checked_add(n).ok_or(())?;
        let s = self.buf.get(self.pos..end).ok_or(())?;
        self.pos = end;
        Ok(s)
    }

    #[inline(always)]
    fn u8(&mut self) -> Result<u8, ()> {
        let &b = self.buf.get(self.pos).ok_or(())?;
        self.pos += 1;
        Ok(b)
    }

    #[inline(always)]
    fn bool(&mut self) -> Result<bool, ()> {
        Ok(self.u8()? != 0)
    }

    #[inline(always)]
    fn u16(&mut self) -> Result<u16, ()> {
        let s = self.take(2)?;
        Ok(u16::from_le_bytes([s[0], s[1]]))
    }

    #[inline(always)]
    fn u32(&mut self) -> Result<u32, ()> {
        Ok(slice_u32(self.take(4)?, 0))
    }

    #[inline(always)]
    fn i32(&mut self) -> Result<i32, ()> {
        Ok(slice_i32(self.take(4)?, 0))
    }

    #[inline(always)]
    fn u64(&mut self) -> Result<u64, ()> {
        Ok(slice_u64(self.take(8)?, 0))
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
    fn read_borrowed_static_str(&mut self) -> Result<&'static str, ()> {
        const USIZE_BYTES: usize = std::mem::size_of::<usize>();
        let raw_ptr = self.take(USIZE_BYTES)?;
        let ptr_val = usize::from_le_bytes(raw_ptr.try_into().unwrap());
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
        match self.u8()? {
            0 => Ok(Cow::Borrowed(self.read_borrowed_static_str()?)),
            1 => self.sym_cow(),
            _ => Err(()),
        }
    }

    #[inline(always)]
    fn opt_cow_str(&mut self) -> Result<Option<Cow<'static, str>>, ()> {
        match self.u8()? {
            0 => Ok(None),
            1 => Ok(Some(Cow::Borrowed(self.read_borrowed_static_str()?))),
            2 => Ok(Some(self.sym_cow()?)),
            _ => Err(()),
        }
    }

    #[inline(always)]
    fn read_sym_cow_into(&mut self, dst: &mut Cow<'static, str>) -> Result<(), ()> {
        let id = self.u16()?;
        if id != SYM_FALLBACK_ID {
            let s = sym_str(id).ok_or(())?;
            *dst = Cow::Borrowed(s);
            Ok(())
        } else {
            self.read_cow_str_into(dst)
        }
    }

    #[inline(always)]
    fn read_cow_str_encoded_into(&mut self, dst: &mut Cow<'static, str>) -> Result<(), ()> {
        match self.u8()? {
            0 => {
                let s = self.read_borrowed_static_str()?;
                *dst = Cow::Borrowed(s);
                Ok(())
            }
            1 => self.read_sym_cow_into(dst),
            _ => Err(()),
        }
    }

    #[inline(always)]
    fn read_opt_cow_str_encoded_into(
        &mut self,
        dst: &mut Option<Cow<'static, str>>,
    ) -> Result<(), ()> {
        match self.u8()? {
            0 => {
                *dst = None;
                Ok(())
            }
            1 => {
                let s = self.read_borrowed_static_str()?;
                *dst = Some(Cow::Borrowed(s));
                Ok(())
            }
            2 => {
                let id = self.u16()?;
                if id != SYM_FALLBACK_ID {
                    let s = sym_str(id).ok_or(())?;
                    *dst = Some(Cow::Borrowed(s));
                    Ok(())
                } else {
                    let slot = dst.get_or_insert_with(|| Cow::Borrowed(""));
                    self.read_cow_str_into(slot)
                }
            }
            _ => Err(()),
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

#[inline]
fn write_card_handles(w: &mut ByteWriter<'_>, handles: &[CardHandle]) {
    w.var_u32(handles.len() as u32);
    if handles.is_empty() {
        return;
    }
    let mut chunk_buf = [0u8; 64];
    for chunk in handles.chunks(16) {
        let mut out_len = 0usize;
        for &h in chunk {
            chunk_buf[out_len..out_len + 4].copy_from_slice(&h.0.to_le_bytes());
            out_len += 4;
        }
        w.write_bytes(&chunk_buf[..out_len]);
    }
}

#[inline]
fn write_card_handles_u16(w: &mut ByteWriter<'_>, handles: &[CardHandle]) {
    w.var_u32(handles.len() as u32);
    if handles.is_empty() {
        return;
    }
    w.ensure(handles.len() * 2);
    let mut chunk_buf = [0u8; 64];
    for chunk in handles.chunks(32) {
        let mut out_len = 0usize;
        for &h in chunk {
            debug_assert!(h.0 <= u16::MAX as u32);
            let b = (h.0 as u16).to_le_bytes();
            chunk_buf[out_len] = b[0];
            chunk_buf[out_len + 1] = b[1];
            out_len += 2;
        }
        w.write_bytes(&chunk_buf[..out_len]);
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
    out.reserve(len);
    for chunk in raw.chunks_exact(4) {
        out.push(CardHandle(u32::from_le_bytes([
            chunk[0], chunk[1], chunk[2], chunk[3],
        ])));
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
    out.reserve(len);
    for chunk in raw.chunks_exact(2) {
        out.push(CardHandle(u16::from_le_bytes([chunk[0], chunk[1]]) as u32));
    }
    Ok(())
}

#[inline]
fn write_creature_handles(w: &mut ByteWriter<'_>, handles: &[CreatureHandle]) {
    w.var_u32(handles.len() as u32);
    if handles.is_empty() {
        return;
    }
    let mut chunk_buf = [0u8; 64];
    for chunk in handles.chunks(16) {
        let mut out_len = 0usize;
        for &h in chunk {
            chunk_buf[out_len..out_len + 4].copy_from_slice(&h.0.to_le_bytes());
            out_len += 4;
        }
        w.write_bytes(&chunk_buf[..out_len]);
    }
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
    out.reserve(len);
    for chunk in raw.chunks_exact(4) {
        out.push(CreatureHandle(u32::from_le_bytes([
            chunk[0], chunk[1], chunk[2], chunk[3],
        ])));
    }
    Ok(())
}

fn write_turn_key(w: &mut ByteWriter<'_>, tk: &TurnKey) {
    w.i32(tk.round_number);
    w.u8(tk.side as u8);
    w.var_u32(tk.player_turn_numbers.len() as u32);
    for &p in &tk.player_turn_numbers {
        w.opt_i32(p);
    }
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

#[inline]
fn write_turn_key_delta(w: &mut ByteWriter<'_>, tk: &TurnKey, prev: &mut Option<TurnKey>) {
    if prev.as_ref() == Some(tk) {
        w.u8(0);
        return;
    }
    *prev = Some(*tk);
    if tk.side == CombatSide::Player
        && tk.round_number >= 0
        && tk.player_turn_numbers.as_slice() == [Some(tk.round_number)]
    {
        if tk.round_number < 0x80 {
            w.write_bytes(&[1, tk.round_number as u8]);
        } else {
            w.u8(1);
            w.var_u32(tk.round_number as u32);
        }
    } else {
        w.u8(2);
        write_turn_key(w, tk);
    }
}

#[inline]
fn read_turn_key_delta(
    r: &mut ByteReader<'_>,
    prev: &mut Option<TurnKey>,
) -> Result<TurnKey, ()> {
    let tk = match r.u8()? {
        0 => {
            return prev.ok_or(());
        }
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
    *prev = Some(tk);
    Ok(tk)
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

const CARD_COMMON_OPT_MASK: u16 = (1 << 0) | (1 << 5);

#[inline(always)]
fn put_var_u32(dst: &mut [u8], pos: &mut usize, mut v: u32) {
    if v < 0x80 {
        dst[*pos] = v as u8;
        *pos += 1;
        return;
    }
    while v >= 0x80 {
        dst[*pos] = (v as u8) | 0x80;
        *pos += 1;
        v >>= 7;
    }
    dst[*pos] = v as u8;
    *pos += 1;
}

#[inline(always)]
fn put_var_i32(dst: &mut [u8], pos: &mut usize, v: i32) {
    let zz = (v as u32).wrapping_shl(1) ^ ((v >> 31) as u32);
    put_var_u32(dst, pos, zz);
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
        + cards_to_pack * 18
        + state.creatures.len() * 64
        + state.damage_history.len() * 12;
    out.reserve(est_size);
    let mut w = ByteWriter::new(out);

    // Header scalars (batched into [u8; 19] + var_u32 + [u8; 21])
    let mut hdr0 = [0u8; 19];
    hdr0[0] = state.current_side as u8;
    hdr0[1..5].copy_from_slice(&state.round_number.to_le_bytes());
    hdr0[5] = u8::from(state.is_in_progress);
    hdr0[6] = u8::from(state.is_starting);
    hdr0[7] = u8::from(state.won);
    hdr0[8] = u8::from(state.is_player_extra_turn);
    hdr0[9] = u8::from(state.gold_was_stolen);
    hdr0[10] = u8::from(state.end_player_turn_requested);
    hdr0[11..15].copy_from_slice(&state.card_action_boundary_depth.to_le_bytes());
    hdr0[15..19].copy_from_slice(&state.next_creature_id.to_le_bytes());
    w.write_bytes(&hdr0);
    w.var_u32(state.initial_encounter_monster_count as u32);

    // Pack flags & RNG streams
    let seed_matches_root = state.run_rngs.string_seed == sctx.root_run_rng_string_seed;
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
    let pack_flags = u8::from(seed_matches_root)
        | (u8::from(deck_unchanged) << 1)
        | (u8::from(u16_handles) << 2);
    let mut hdr1 = [0u8; 21];
    hdr1[0..4].copy_from_slice(&state.next_card_handle.to_le_bytes());
    hdr1[4..8].copy_from_slice(&state.next_creature_handle.to_le_bytes());
    hdr1[8..12].copy_from_slice(&state.next_power_handle.to_le_bytes());
    hdr1[12..20].copy_from_slice(&state.next_attack_id.to_le_bytes());
    hdr1[20] = pack_flags;
    w.write_bytes(&hdr1);

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
    w.u16(rng_delta_mask);
    w.u64(state.run_rngs.seed);
    {
        let mut rem = rng_delta_mask & 0x0FFF;
        while rem != 0 {
            let i = rem.trailing_zeros() as usize;
            rem &= rem - 1;
            w.rng(&rs[i]);
        }
    }
    w.u64(state.player_rngs.seed);
    {
        let mut rem = rng_delta_mask >> 12;
        while rem != 0 {
            let i = rem.trailing_zeros() as usize;
            rem &= rem - 1;
            w.rng(&ps[i]);
        }
    }

    // PlayerInst fixed scalars (51 bytes) — write_player_counters emits c_mask + optional values immediately after
    let p = &state.player;
    let mut praw = [0u8; 51];
    praw[0..4].copy_from_slice(&p.creature_handle.0.to_le_bytes());
    praw[4..8].copy_from_slice(&p.current_hp.to_le_bytes());
    praw[8..12].copy_from_slice(&p.max_hp.to_le_bytes());
    praw[12..16].copy_from_slice(&p.block.to_le_bytes());
    praw[16..20].copy_from_slice(&p.cumulative_hp_lost.to_le_bytes());
    praw[20..24].copy_from_slice(&p.gold.to_le_bytes());
    praw[24..28].copy_from_slice(&p.max_energy.to_le_bytes());
    praw[28..32].copy_from_slice(&p.base_orb_slot_count.to_le_bytes());
    praw[32] = u8::from(p.is_active_for_hooks);
    praw[33] = u8::from(p.can_use_or_remove_potions);
    praw[34..38].copy_from_slice(&p.energy.to_le_bytes());
    praw[38..42].copy_from_slice(&p.stars.to_le_bytes());
    praw[42] = p.phase as u8;
    praw[43..47].copy_from_slice(&p.turn_number.to_le_bytes());
    praw[47..51].copy_from_slice(&p.card_or_potion_effect_depth.to_le_bytes());
    w.write_bytes(&praw);

    // PlayerCounters (26 fields bitmask-packed)
    write_player_counters(&mut w, &p.counters);

    if u16_handles {
        write_card_handles_u16(&mut w, &p.hand);
        write_card_handles_u16(&mut w, &p.draw);
        write_card_handles_u16(&mut w, &p.discard);
        write_card_handles_u16(&mut w, &p.exhaust);
        write_card_handles_u16(&mut w, &p.play);
        if !deck_unchanged {
            write_card_handles_u16(&mut w, &p.deck);
        }
    } else {
        write_card_handles(&mut w, &p.hand);
        write_card_handles(&mut w, &p.draw);
        write_card_handles(&mut w, &p.discard);
        write_card_handles(&mut w, &p.exhaust);
        write_card_handles(&mut w, &p.play);
        if !deck_unchanged {
            write_card_handles(&mut w, &p.deck);
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

    w.i32(p.orb_capacity);
    w.var_u32(p.orbs.len() as u32);
    for orb in &p.orbs {
        w.u8(orb.id as u8);
        match orb.raw_evoke_value {
            Some(d) => {
                w.u8(1);
                w.dec(d);
            }
            None => w.u8(0),
        }
        match orb.raw_passive_value {
            Some(d) => {
                w.u8(1);
                w.dec(d);
            }
            None => w.u8(0),
        }
    }

    w.var_u32(p.arrows.len() as u32);
    for arrow in &p.arrows {
        w.u8(arrow.status as u8);
        w.bool(arrow.destroy_after_impaled);
    }

    w.var_u32(p.relics.len() as u32);
    for relic in &p.relics {
        let mut rhdr = [0u8; 8];
        rhdr[0..2].copy_from_slice(&(relic.id as u16).to_le_bytes());
        rhdr[2] = u8::from(relic.is_wax);
        rhdr[3] = u8::from(relic.is_melted);
        rhdr[4..8].copy_from_slice(&relic.stack_count.to_le_bytes());
        w.write_bytes(&rhdr);
        w.opt_i32(relic.floor_added_to_deck);
        w.cow_field_map(&relic.private_fields);
    }

    // Cards (bitmask-packed infrequent/optional fields)
    w.var_u32(cards_to_pack as u32);
    for card in &state.cards[card_start..] {
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

        let id_bytes = (card.id as u16).to_le_bytes();
        let cost_bytes = card.canonical_base_energy_cost.to_le_bytes();
        let kw_bytes = card.local_keywords.bits().to_le_bytes();
        let opt_bytes = opt_mask.to_le_bytes();
        if (opt_mask & !CARD_COMMON_OPT_MASK) == 0 && (flags_byte & 0x20) == 0 {
            let mut cbuf = [0u8; 28];
            let mut len = 0usize;
            put_var_u32(&mut cbuf, &mut len, card.handle.0);
            cbuf[len] = id_bytes[0];
            cbuf[len + 1] = id_bytes[1];
            cbuf[len + 2..len + 6].copy_from_slice(&cost_bytes);
            cbuf[len + 6] = kw_bytes[0];
            cbuf[len + 7] = kw_bytes[1];
            cbuf[len + 8] = opt_bytes[0];
            cbuf[len + 9] = opt_bytes[1];
            cbuf[len + 10] = flags_byte;
            len += 11;
            if (opt_mask & (1 << 0)) != 0 {
                cbuf[len..len + 4].copy_from_slice(&card.upgrade_level.to_le_bytes());
                len += 4;
            }
            if let Some(h) = card.deck_card_handle {
                put_var_u32(&mut cbuf, &mut len, h.0);
            }
            w.write_bytes(&cbuf[..len]);
            continue;
        }
        if card.handle.0 < 0x80 {
            w.write_bytes(&[
                card.handle.0 as u8,
                id_bytes[0],
                id_bytes[1],
                cost_bytes[0],
                cost_bytes[1],
                cost_bytes[2],
                cost_bytes[3],
                kw_bytes[0],
                kw_bytes[1],
                opt_bytes[0],
                opt_bytes[1],
                flags_byte,
            ]);
        } else {
            w.var_u32(card.handle.0);
            w.write_bytes(&[
                id_bytes[0],
                id_bytes[1],
                cost_bytes[0],
                cost_bytes[1],
                cost_bytes[2],
                cost_bytes[3],
                kw_bytes[0],
                kw_bytes[1],
                opt_bytes[0],
                opt_bytes[1],
                flags_byte,
            ]);
        }

        if opt_mask != 0 || (flags_byte & 0x20) != 0 {
            if (opt_mask & (1 << 0)) != 0 {
                w.i32(card.upgrade_level);
            }
            if (opt_mask & (1 << 1)) != 0 {
                w.i32(card.base_energy_cost);
            }
            if (opt_mask & (1 << 2)) != 0 {
                w.i32(card.captured_energy_x_value);
            }
            if (opt_mask & (1 << 3)) != 0 {
                w.i32(card.base_replay_count);
            }
            if let Some(v) = card.floor_added_to_deck {
                w.i32(v);
            }
            if let Some(h) = card.deck_card_handle {
                w.var_u32(h.0);
            }
            if let Some(h) = card.clone_of_handle {
                w.var_u32(h.0);
            }
            if let Some(v) = card.temp_star_cost_override_this_turn {
                w.i32(v);
            }
            if let Some(v) = card.temp_cost_override_this_turn {
                w.i32(v);
            }
            if let Some(v) = card.temp_cost_override_this_turn_or_until_played {
                w.i32(v);
            }
            if let Some(v) = card.temp_cost_override_this_combat {
                w.i32(v);
            }
            if (opt_mask & (1 << 11)) != 0 {
                w.u16(card.keywords_added_from_outside.bits());
            }
            if (opt_mask & (1 << 12)) != 0 {
                w.u16(card.keywords_removed_from_outside.bits());
            }
            if (opt_mask & (1 << 13)) != 0 {
                w.u16(card.temp_flags.bits());
            }
            if let Some(aff) = card.affliction {
                w.u16(aff.id as u16);
                w.i32(aff.amount);
            }
            if let Some(ei) = card.ext_index {
                w.var_u32(ei);
            }
            if let Some(cid) = card.transform_into_after_play {
                w.u16(cid as u16);
            }
        }
    }

    // CardExtensions
    w.var_u32(state.card_extensions.len() as u32);
    for ext in &state.card_extensions {
        w.var_u32(ext.local_energy_modifiers.len() as u32);
        for m in &ext.local_energy_modifiers {
            let mut mraw = [0u8; 6];
            mraw[0] = m.kind as u8;
            mraw[1..5].copy_from_slice(&m.amount.to_le_bytes());
            mraw[5] = u8::from(m.is_reduce_only);
            w.write_bytes(&mraw);
        }
        match &ext.enchantment {
            Some(e) => {
                let mut eraw = [0u8; 8];
                eraw[0] = 1;
                eraw[1..3].copy_from_slice(&(e.id as u16).to_le_bytes());
                eraw[3..7].copy_from_slice(&e.amount.to_le_bytes());
                eraw[7] = e.status as u8;
                w.write_bytes(&eraw);
                w.cow_field_map(&e.private_fields);
            }
            None => w.u8(0),
        }
        w.cow_field_map(&ext.private_fields);
    }

    // Creatures
    w.var_u32(state.creatures.len() as u32);
    for cr in &state.creatures {
        w.u32(cr.handle.0);
        w.opt_u32(cr.combat_id);
        w.u8(cr.side as u8);
        w.opt_cow_str(cr.slot_name.as_ref());

        w.bool(cr.is_player);
        w.opt_u32(cr.pet_owner_player_index.map(|i| i as u32));
        match cr.monster_id {
            Some(mid) => {
                w.u8(1);
                w.u16(mid as u16);
            }
            None => w.u8(0),
        }
        let mut craw = [0u8; 17];
        craw[0..4].copy_from_slice(&cr.current_hp.to_le_bytes());
        craw[4..8].copy_from_slice(&cr.max_hp.to_le_bytes());
        craw[8..12].copy_from_slice(&cr.block.to_le_bytes());
        craw[12..16].copy_from_slice(&cr.cumulative_hp_lost.to_le_bytes());
        craw[16] = u8::from(cr.is_detached);
        w.write_bytes(&craw);

        match &cr.monster {
            Some(m) => {
                w.u8(1);
                w.u16(m.monster_id as u16);
                w.rng(&m.rng);
                w.write_bytes(&[
                    u8::from(m.is_performing_move),
                    u8::from(m.spawned_this_turn),
                ]);
                w.cow_str(&m.current_state_id);
                w.opt_cow_str(m.next_move_id.as_ref());
                w.write_bytes(&[
                    u8::from(m.next_move_can_transition_away),
                    u8::from(m.performed_first_move),
                ]);
                w.var_u32(m.state_log_ids.len() as u32);
                for s in &m.state_log_ids {
                    w.cow_str(s);
                }
                w.opt_cow_str(m.transient_move_follow_up_state_id.as_ref());
                w.cow_field_map(&m.setup_fields);
                w.cow_field_map(&m.private_fields);
            }
            None => w.u8(0),
        }

        w.var_u32(cr.powers.len() as u32);
        for pw in &cr.powers {
            let mut praw = [0u8; 25];
            praw[0..4].copy_from_slice(&pw.handle.to_le_bytes());
            praw[4..6].copy_from_slice(&(pw.id as u16).to_le_bytes());
            praw[6..10].copy_from_slice(&pw.amount.to_le_bytes());
            praw[10..14].copy_from_slice(&pw.amount_on_turn_start.to_le_bytes());
            praw[14] = u8::from(pw.skip_next_duration_tick);
            let mut len = 15usize;
            if let Some(ah) = pw.applier_handle {
                praw[len] = 1;
                praw[len + 1..len + 5].copy_from_slice(&ah.0.to_le_bytes());
                len += 5;
            } else {
                praw[len] = 0;
                len += 1;
            }
            if let Some(ei) = pw.ext_index {
                praw[len] = 1;
                praw[len + 1..len + 5].copy_from_slice(&ei.to_le_bytes());
                len += 5;
            } else {
                praw[len] = 0;
                len += 1;
            }
            w.write_bytes(&praw[..len]);
        }
    }

    // PowerExtensions
    w.var_u32(state.power_extensions.len() as u32);
    for pe in &state.power_extensions {
        w.cow_field_map(&pe.private_fields);
        w.opt_u64(pe.attack_command_id);
        w.i32(pe.amount_when_attack_started);
        w.var_u32(pe.impaled_arrows.len() as u32);
        for &idx in &pe.impaled_arrows {
            w.var_u32(idx as u32);
        }
        w.i32(pe.hit_counter);
    }

    // Handle lists
    write_creature_handles(&mut w, &state.ally_handles);
    write_creature_handles(&mut w, &state.enemy_handles);
    write_creature_handles(&mut w, &state.escaped_handles);
    write_creature_handles(&mut w, &state.removed_handles);
    write_creature_handles(&mut w, &state.spawned_enemy_handles);
    write_creature_handles(&mut w, &state.pet_handles);

    // Damage history
    w.var_u32(state.damage_history.len() as u32);
    for dh in &state.damage_history {
        let flags: u16 = u16::from(dh.dealer_creature_handle.is_some())
            | (u16::from(dh.original_target_creature_handle != dh.receiver_creature_handle) << 1)
            | (u16::from(dh.intended_hp_loss != 0) << 2)
            | (u16::from(dh.blocked_damage != 0) << 3)
            | (u16::from(dh.unblocked_damage != 0) << 4)
            | (u16::from(dh.overkill_damage != 0) << 5)
            | (u16::from(dh.was_target_killed) << 6)
            | (u16::from(dh.was_block_broken) << 7)
            | (u16::from(dh.was_fully_blocked) << 8)
            | (u16::from(dh.card_source_handle.is_some()) << 9)
            | (u16::from(dh.player_turn_numbers.len() == 1) << 10)
            | (u16::from(dh.player_turn_numbers.len() > 1) << 11);
        let mut dbuf = [0u8; 64];
        dbuf[0..2].copy_from_slice(&flags.to_le_bytes());
        dbuf[2] = dh.props.bits();
        dbuf[3] = dh.current_side as u8;
        let mut len = 4usize;
        put_var_u32(&mut dbuf, &mut len, dh.receiver_creature_handle.0);
        if let Some(dealer) = dh.dealer_creature_handle {
            put_var_u32(&mut dbuf, &mut len, dealer.0);
        }
        if dh.original_target_creature_handle != dh.receiver_creature_handle {
            put_var_u32(&mut dbuf, &mut len, dh.original_target_creature_handle.0);
        }
        if dh.intended_hp_loss != 0 {
            put_var_i32(&mut dbuf, &mut len, dh.intended_hp_loss);
        }
        if dh.blocked_damage != 0 {
            put_var_i32(&mut dbuf, &mut len, dh.blocked_damage);
        }
        if dh.unblocked_damage != 0 {
            put_var_i32(&mut dbuf, &mut len, dh.unblocked_damage);
        }
        if dh.overkill_damage != 0 {
            put_var_i32(&mut dbuf, &mut len, dh.overkill_damage);
        }
        if let Some(cs) = dh.card_source_handle {
            put_var_u32(&mut dbuf, &mut len, cs.0);
        }
        put_var_i32(&mut dbuf, &mut len, dh.round_number);
        if dh.player_turn_numbers.len() == 1 {
            put_var_i32(&mut dbuf, &mut len, dh.player_turn_numbers[0]);
            w.write_bytes(&dbuf[..len]);
        } else {
            w.write_bytes(&dbuf[..len]);
            if dh.player_turn_numbers.len() > 1 {
                w.var_u32(dh.player_turn_numbers.len() as u32);
                for &tn in &dh.player_turn_numbers {
                    w.var_i32(tn);
                }
            }
        }
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
        let mut prev_tk: Option<TurnKey> = None;
        if (sh_mask & (1 << 0)) != 0 {
            w.var_u32(sh.exhausts.len() as u32);
            for (tk, idx) in &sh.exhausts {
                if *idx < 0x80 && prev_tk.as_ref() == Some(tk) {
                    w.write_bytes(&[0, *idx as u8]);
                } else {
                    write_turn_key_delta(&mut w, tk, &mut prev_tk);
                    w.var_u32(*idx as u32);
                }
            }
        }
        if (sh_mask & (1 << 1)) != 0 {
            w.var_u32(sh.card_block_gains.len() as u32);
            for (tk, idx, ord) in &sh.card_block_gains {
                write_turn_key_delta(&mut w, tk, &mut prev_tk);
                w.var_u32(*idx as u32);
                w.i32(*ord);
            }
        }
        if (sh_mask & (1 << 2)) != 0 {
            w.var_u32(sh.orb_channels.len() as u32);
            for (tk, idx, oid) in &sh.orb_channels {
                write_turn_key_delta(&mut w, tk, &mut prev_tk);
                w.var_u32(*idx as u32);
                w.u8(*oid as u8);
            }
        }
        if (sh_mask & (1 << 3)) != 0 {
            w.var_u32(sh.energy_spent.len() as u32);
            for (tk, idx, amt) in &sh.energy_spent {
                write_turn_key_delta(&mut w, tk, &mut prev_tk);
                w.var_u32(*idx as u32);
                w.i32(*amt);
            }
        }
        if (sh_mask & (1 << 4)) != 0 {
            w.var_u32(sh.card_drawn.len() as u32);
            for (tk, idx, ct, from_hand) in &sh.card_drawn {
                write_turn_key_delta(&mut w, tk, &mut prev_tk);
                w.var_u32(*idx as u32);
                w.u8(*ct as u8);
                w.bool(*from_hand);
            }
        }
        if (sh_mask & (1 << 5)) != 0 {
            w.var_u32(sh.actor_events.len() as u32);
            for (kind, tk, idx) in &sh.actor_events {
                w.cow_str(kind);
                write_turn_key_delta(&mut w, tk, &mut prev_tk);
                w.var_u32(*idx as u32);
            }
        }
    }
    let gr = &sh.growth_rewards;
    let mut graw = [0u8; 44];
    graw[0..4].copy_from_slice(&gr.hand_of_greed.to_le_bytes());
    graw[4..8].copy_from_slice(&gr.the_hunt.to_le_bytes());
    graw[8..12].copy_from_slice(&gr.feed.to_le_bytes());
    graw[12..16].copy_from_slice(&gr.royalties.to_le_bytes());
    graw[16..20].copy_from_slice(&gr.alchemize.to_le_bytes());
    graw[20..24].copy_from_slice(&gr.genetic_algorithm.to_le_bytes());
    graw[24..28].copy_from_slice(&gr.the_scythe.to_le_bytes());
    graw[28..32].copy_from_slice(&gr.goopy.to_le_bytes());
    graw[32..36].copy_from_slice(&gr.forbidden_grimoire.to_le_bytes());
    graw[36..40].copy_from_slice(&gr.mad_science.to_le_bytes());
    graw[40..44].copy_from_slice(&sh.brightest_flame_max_hp_spent.to_le_bytes());
    w.write_bytes(&graw);
    w.var_i32(sh.prior_player_unblocked_hits);
    w.var_i32(sh.prior_player_damaged_turn);
    w.var_i32(sh.prior_exhaust_count);

    // PendingContinuation
    write_pending(&mut w, &state.pending);
    w.u8(state.vakuu_auto_select_depth);
}

/// Unpacks a packed binary state directly into a reusable `BattleState` scratch buffer.
pub fn unpack_state(
    bytes: &[u8],
    static_ctx: &Arc<RootStaticContext>,
    out: &mut BattleState,
) -> Result<(), KernelError> {
    let mut r = ByteReader::new(bytes);
    unpack_state_inner(&mut r, static_ctx, out)
        .map_err(|()| KernelError::InvalidState(format!("state unpack error at {}", r.pos)))
}

fn unpack_state_inner(
    r: &mut ByteReader<'_>,
    static_ctx: &Arc<RootStaticContext>,
    out: &mut BattleState,
) -> Result<(), ()> {
    let same_root = Arc::ptr_eq(&out.static_ctx, static_ctx);
    let prev_deck_dirty = out.deck_dirty.0;
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
    if seed_matches_root {
        if !same_root || out.run_rngs.string_seed != sctx.root_run_rng_string_seed {
            out.run_rngs
                .string_seed
                .clone_from(&sctx.root_run_rng_string_seed);
        }
    } else {
        r.read_str_into(&mut out.run_rngs.string_seed)?;
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
        let mut counters = PlayerCounters {
            stars_gained_this_turn: if (c_mask & (1 << 0)) != 0 { r.dec()? } else { Dec::ZERO },
            skill_cards_played_this_turn: if (c_mask & (1 << 1)) != 0 { r.i32()? } else { 0 },
            attack_cards_played_this_turn: if (c_mask & (1 << 2)) != 0 { r.i32()? } else { 0 },
            shivs_played_this_turn: if (c_mask & (1 << 3)) != 0 { r.i32()? } else { 0 },
            cards_discarded_this_turn: if (c_mask & (1 << 4)) != 0 { r.i32()? } else { 0 },
            cards_played_this_combat: if (c_mask & (1 << 5)) != 0 { r.i32()? } else { 0 },
            cards_generated_this_combat: if (c_mask & (1 << 6)) != 0 { r.i32()? } else { 0 },
            sadness_generated_this_combat: if (c_mask & (1 << 7)) != 0 { r.i32()? } else { 0 },
            cards_drawn_this_combat: if (c_mask & (1 << 8)) != 0 { r.i32()? } else { 0 },
            cards_played_this_turn: if (c_mask & (1 << 9)) != 0 { r.i32()? } else { 0 },
            manual_cards_played_this_turn: if (c_mask & (1 << 10)) != 0 { r.i32()? } else { 0 },
            card_plays_started_this_turn: if (c_mask & (1 << 11)) != 0 { r.i32()? } else { 0 },
            attack_or_skill_card_plays_started_this_turn: if (c_mask & (1 << 12)) != 0 { r.i32()? } else { 0 },
            attack_cards_started_this_turn: if (c_mask & (1 << 13)) != 0 { r.i32()? } else { 0 },
            zero_cost_attacks_started_this_turn: if (c_mask & (1 << 14)) != 0 { r.i32()? } else { 0 },
            first_in_series_card_plays_started_this_turn: if (c_mask & (1 << 15)) != 0 { r.i32()? } else { 0 },
            ..PlayerCounters::default()
        };
        if (c_mask >> 16) != 0 {
            if (c_mask & (1 << 16)) != 0 { counters.wah_started_this_turn = r.i32()?; }
            if (c_mask & (1 << 17)) != 0 { counters.cho_started_this_turn = r.i32()?; }
            if (c_mask & (1 << 18)) != 0 { counters.ki_started_this_turn = r.i32()?; }
            if (c_mask & (1 << 19)) != 0 { counters.first_wah_play_ordinal_this_turn = r.i32()?; }
            if (c_mask & (1 << 20)) != 0 { counters.first_cho_play_ordinal_this_turn = r.i32()?; }
            if (c_mask & (1 << 21)) != 0 { counters.first_ki_play_ordinal_this_turn = r.i32()?; }
            if (c_mask & (1 << 22)) != 0 { counters.wah_finished_this_turn = r.i32()?; }
            if (c_mask & (1 << 23)) != 0 { counters.cho_finished_this_turn = r.i32()?; }
            if (c_mask & (1 << 24)) != 0 { counters.ki_finished_this_turn = r.i32()?; }
            if (c_mask & (1 << 25)) != 0 { counters.melody_cards_finished_this_turn = r.i32()?; }
        }
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
    p.potion_slots.clear();
    p.potion_slots.reserve(pot_len);
    for _ in 0..pot_len {
        if r.u8()? == 0 {
            p.potion_slots.push(None);
        } else {
            p.potion_slots.push(Some(decode_potion_id(r.u16()?)?));
        }
    }

    p.orb_capacity = r.i32()?;
    let orb_len = r.var_u32()? as usize;
    if orb_len == 0 {
        if !p.orbs.is_empty() {
            p.orbs.clear();
        }
    } else {
        p.orbs.clear();
        p.orbs.reserve(orb_len);
        for _ in 0..orb_len {
            let id = decode_orb_id(r.u8()?)?;
            let raw_evoke_value = if r.u8()? == 0 { None } else { Some(r.dec()?) };
            let raw_passive_value = if r.u8()? == 0 { None } else { Some(r.dec()?) };
            p.orbs.push(OrbInst {
                id,
                raw_evoke_value,
                raw_passive_value,
            });
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

    let relic_len = r.var_u32()? as usize;
    let old_relic_len = p.relics.len();
    for idx in 0..relic_len {
        let rel_hdr = r.take(8)?;
        let id = decode_relic_id(slice_u16(rel_hdr, 0))?;
        let is_wax = rel_hdr[2] != 0;
        let is_melted = rel_hdr[3] != 0;
        let stack_count = slice_i32(rel_hdr, 4);
        let floor_added_to_deck = r.opt_i32()?;
        if idx < old_relic_len {
            let dr = &mut p.relics[idx];
            dr.id = id;
            dr.is_wax = is_wax;
            dr.is_melted = is_melted;
            dr.stack_count = stack_count;
            dr.floor_added_to_deck = floor_added_to_deck;
            r.cow_field_map_into(&mut dr.private_fields)?;
        } else {
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

    // Cards
    let prefix_len = sctx.root_deck_prefix_len;
    let cards_len = r.var_u32()? as usize;
    if deck_unchanged {
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
    for _ in 0..cards_len {
        let handle = CardHandle(r.var_u32()?);
        let chdr = r.take(11)?;
        let id = decode_card_id(slice_u16(chdr, 0))?;
        let canonical_base_energy_cost = slice_i32(chdr, 2);
        let local_keywords = CardKeywords::from_bits_retain(slice_u16(chdr, 6));
        let opt_mask = slice_u16(chdr, 8);
        let flags_byte = chdr[10];

        if (opt_mask & !CARD_COMMON_OPT_MASK) == 0 && (flags_byte & 0x20) == 0 {
            let upgrade_level = if (opt_mask & (1 << 0)) != 0 {
                r.i32()?
            } else {
                0
            };
            let deck_card_handle = if (opt_mask & (1 << 5)) != 0 {
                Some(CardHandle(r.var_u32()?))
            } else {
                None
            };
            let pile = decode_pile_type(flags_byte & 0x0F)?;
            let removed_from_state = (flags_byte & 0x10) != 0;
            out.cards.push(CardInst {
                handle,
                id,
                upgrade_level,
                canonical_base_energy_cost,
                base_energy_cost: canonical_base_energy_cost,
                captured_energy_x_value: 0,
                base_replay_count: 0,
                floor_added_to_deck: None,
                deck_card_handle,
                clone_of_handle: None,
                temp_star_cost_override_this_turn: None,
                temp_cost_override_this_turn: None,
                temp_cost_override_this_turn_or_until_played: None,
                temp_cost_override_this_combat: None,
                local_keywords,
                keywords_added_from_outside: CardKeywords::empty(),
                keywords_removed_from_outside: CardKeywords::empty(),
                temp_flags: CardTempFlags::empty(),
                affliction: None,
                ext_index: None,
                pile,
                removed_from_state,
                transform_into_after_play: None,
            });
            continue;
        }

        let upgrade_level = if (opt_mask & (1 << 0)) != 0 { r.i32()? } else { 0 };
        let base_energy_cost = if (opt_mask & (1 << 1)) != 0 {
            r.i32()?
        } else {
            canonical_base_energy_cost
        };
        let captured_energy_x_value = if (opt_mask & (1 << 2)) != 0 { r.i32()? } else { 0 };
        let base_replay_count = if (opt_mask & (1 << 3)) != 0 { r.i32()? } else { 0 };
        let floor_added_to_deck = if (opt_mask & (1 << 4)) != 0 { Some(r.i32()?) } else { None };
        let deck_card_handle = if (opt_mask & (1 << 5)) != 0 {
            Some(CardHandle(r.var_u32()?))
        } else {
            None
        };
        let clone_of_handle = if (opt_mask & (1 << 6)) != 0 {
            Some(CardHandle(r.var_u32()?))
        } else {
            None
        };
        let temp_star_cost_override_this_turn = if (opt_mask & (1 << 7)) != 0 { Some(r.i32()?) } else { None };
        let temp_cost_override_this_turn = if (opt_mask & (1 << 8)) != 0 { Some(r.i32()?) } else { None };
        let temp_cost_override_this_turn_or_until_played = if (opt_mask & (1 << 9)) != 0 { Some(r.i32()?) } else { None };
        let temp_cost_override_this_combat = if (opt_mask & (1 << 10)) != 0 { Some(r.i32()?) } else { None };
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
        out.cards.push(CardInst {
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
        });
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
                    dc.monster = Some(MonsterInst {
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
                    });
                }
            }

            let p_len = r.var_u32()? as usize;
            dc.powers.clear();
            dc.powers.reserve(p_len);
            let mut has_minion_power = false;
            for _ in 0..p_len {
                let praw = r.take(15)?;
                let handle = slice_u32(praw, 0);
                let id = decode_power_id(slice_u16(praw, 4))?;
                if id == PowerId::MinionPower {
                    has_minion_power = true;
                }
                dc.powers.push(PowerInst {
                    handle,
                    id,
                    amount: slice_i32(praw, 6),
                    amount_on_turn_start: slice_i32(praw, 10),
                    skip_next_duration_tick: praw[14] != 0,
                    applier_handle: r.opt_u32()?.map(CreatureHandle),
                    ext_index: r.opt_u32()?,
                });
            }
            dc.has_minion_power = has_minion_power;
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
                Some(MonsterInst {
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
                })
            };

            let p_len = r.var_u32()? as usize;
            let mut powers = Vec::with_capacity(p_len);
            let mut has_minion_power = false;
            for _ in 0..p_len {
                let praw = r.take(15)?;
                let handle = slice_u32(praw, 0);
                let id = decode_power_id(slice_u16(praw, 4))?;
                if id == PowerId::MinionPower {
                    has_minion_power = true;
                }
                powers.push(PowerInst {
                    handle,
                    id,
                    amount: slice_i32(praw, 6),
                    amount_on_turn_start: slice_i32(praw, 10),
                    skip_next_duration_tick: praw[14] != 0,
                    applier_handle: r.opt_u32()?.map(CreatureHandle),
                    ext_index: r.opt_u32()?,
                });
            }

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
    read_creature_handles(r, &mut out.escaped_handles)?;
    read_creature_handles(r, &mut out.removed_handles)?;
    read_creature_handles(r, &mut out.spawned_enemy_handles)?;
    read_creature_handles(r, &mut out.pet_handles)?;

    // Damage history
    let dh_len = r.var_u32()? as usize;
    out.damage_history.clear();
    out.damage_history.reserve(dh_len);
    for _ in 0..dh_len {
        let dhdr = r.take(4)?;
        let flags = slice_u16(dhdr, 0);
        let props = ValueProp::from_bits_retain(dhdr[2]);
        let current_side = decode_combat_side(dhdr[3])?;
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
        out.damage_history.push(DamageHistoryEntry {
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
        let mut prev_tk: Option<TurnKey> = None;
        if (sh_mask & (1 << 0)) != 0 {
            sh.exhausts.clear();
            let ex_len = r.var_u32()? as usize;
            sh.exhausts.reserve(ex_len);
            for _ in 0..ex_len {
                sh.exhausts
                    .push((read_turn_key_delta(r, &mut prev_tk)?, r.var_u32()? as usize));
            }
        } else if !sh.exhausts.is_empty() {
            sh.exhausts.clear();
        }
        if (sh_mask & (1 << 1)) != 0 {
            sh.card_block_gains.clear();
            let cbg_len = r.var_u32()? as usize;
            sh.card_block_gains.reserve(cbg_len);
            for _ in 0..cbg_len {
                sh.card_block_gains.push((
                    read_turn_key_delta(r, &mut prev_tk)?,
                    r.var_u32()? as usize,
                    r.i32()?,
                ));
            }
        } else if !sh.card_block_gains.is_empty() {
            sh.card_block_gains.clear();
        }
        if (sh_mask & (1 << 2)) != 0 {
            sh.orb_channels.clear();
            let oc_len = r.var_u32()? as usize;
            sh.orb_channels.reserve(oc_len);
            for _ in 0..oc_len {
                sh.orb_channels.push((
                    read_turn_key_delta(r, &mut prev_tk)?,
                    r.var_u32()? as usize,
                    decode_orb_id(r.u8()?)?,
                ));
            }
        } else if !sh.orb_channels.is_empty() {
            sh.orb_channels.clear();
        }
        if (sh_mask & (1 << 3)) != 0 {
            sh.energy_spent.clear();
            let es_len = r.var_u32()? as usize;
            sh.energy_spent.reserve(es_len);
            for _ in 0..es_len {
                sh.energy_spent.push((
                    read_turn_key_delta(r, &mut prev_tk)?,
                    r.var_u32()? as usize,
                    r.i32()?,
                ));
            }
        } else if !sh.energy_spent.is_empty() {
            sh.energy_spent.clear();
        }
        if (sh_mask & (1 << 4)) != 0 {
            sh.card_drawn.clear();
            let cd_len = r.var_u32()? as usize;
            sh.card_drawn.reserve(cd_len);
            for _ in 0..cd_len {
                sh.card_drawn.push((
                    read_turn_key_delta(r, &mut prev_tk)?,
                    r.var_u32()? as usize,
                    decode_card_type(r.u8()?)?,
                    r.bool()?,
                ));
            }
        } else if !sh.card_drawn.is_empty() {
            sh.card_drawn.clear();
        }
        if (sh_mask & (1 << 5)) != 0 {
            let ae_len = r.var_u32()? as usize;
            let keep = sh.actor_events.len().min(ae_len);
            for idx in 0..keep {
                r.read_cow_str_encoded_into(&mut sh.actor_events[idx].0)?;
                sh.actor_events[idx].1 = read_turn_key_delta(r, &mut prev_tk)?;
                sh.actor_events[idx].2 = r.var_u32()? as usize;
            }
            if ae_len > keep {
                sh.actor_events.reserve(ae_len - keep);
                for _ in keep..ae_len {
                    sh.actor_events.push((
                        r.cow_str()?,
                        read_turn_key_delta(r, &mut prev_tk)?,
                        r.var_u32()? as usize,
                    ));
                }
            } else {
                sh.actor_events.truncate(ae_len);
            }
        } else if !sh.actor_events.is_empty() {
            sh.actor_events.clear();
        }
    }
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
    sh.prior_player_unblocked_hits = r.var_i32()?;
    sh.prior_player_damaged_turn = r.var_i32()?;
    sh.prior_exhaust_count = r.var_i32()?;

    out.pending = read_pending(r)?;
    out.vakuu_auto_select_depth = r.u8()?;
    if same_root
        && deck_unchanged
        && !prev_deck_dirty
        && out.hook_cache.cached_tables.is_some()
        && (out.hook_cache.dirty_mask & (HOOK_DIRTY_DECK | HOOK_DIRTY_STATIC)) == 0
    {
        out.hook_cache.dirty_mask = HOOK_DIRTY_ALL & !(HOOK_DIRTY_DECK | HOOK_DIRTY_STATIC);
    } else {
        out.invalidate_hook_cache();
    }
    Ok(())
}

/// Allocates a blank `BattleState` shell suitable as a reusable unpack scratch target.
pub fn empty_scratch_state(static_ctx: Arc<RootStaticContext>) -> BattleState {
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
            string_seed: String::new(),
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
    w.i32(card.base_energy_cost);
    w.u16(card.local_keywords.bits());
    w.u16(opt_mask);

    if (opt_mask & (1 << 7)) != 0 { w.i32(card.upgrade_level); }
    if (opt_mask & (1 << 8)) != 0 { w.i32(card.captured_energy_x_value); }
    if (opt_mask & (1 << 9)) != 0 { w.i32(card.base_replay_count); }
    if (opt_mask & (1 << 10)) != 0 { w.u16(card.keywords_added_from_outside.bits()); }
    if (opt_mask & (1 << 11)) != 0 { w.u16(card.keywords_removed_from_outside.bits()); }
    if (opt_mask & (1 << 12)) != 0 { w.u16(card.temp_flags.bits()); }
    if let Some(v) = card.temp_star_cost_override_this_turn { w.i32(v); }
    if let Some(v) = card.temp_cost_override_this_turn { w.i32(v); }
    if let Some(v) = card.temp_cost_override_this_turn_or_until_played { w.i32(v); }
    if let Some(v) = card.temp_cost_override_this_combat { w.i32(v); }
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
    let c_mask: u32 = u32::from(c.stars_gained_this_turn != Dec::ZERO)
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
        | (u32::from(c.first_in_series_card_plays_started_this_turn != 0) << 15)
        | (u32::from(c.wah_started_this_turn != 0) << 16)
        | (u32::from(c.cho_started_this_turn != 0) << 17)
        | (u32::from(c.ki_started_this_turn != 0) << 18)
        | (u32::from(c.first_wah_play_ordinal_this_turn != 0) << 19)
        | (u32::from(c.first_cho_play_ordinal_this_turn != 0) << 20)
        | (u32::from(c.first_ki_play_ordinal_this_turn != 0) << 21)
        | (u32::from(c.wah_finished_this_turn != 0) << 22)
        | (u32::from(c.cho_finished_this_turn != 0) << 23)
        | (u32::from(c.ki_finished_this_turn != 0) << 24)
        | (u32::from(c.melody_cards_finished_this_turn != 0) << 25);
    if c_mask == 0 {
        w.u32(0);
        return;
    }
    let mut len = if (c_mask & 1) == 0 {
        w.ensure(104);
        let cur_len = w.buf.len();
        // SAFETY: `ensure(104)` guarantees at least 104 writable bytes after `cur_len`.
        unsafe {
            let dst = w.buf.as_mut_ptr().add(cur_len);
            std::ptr::copy_nonoverlapping(c_mask.to_le_bytes().as_ptr(), dst, 4);
        }
        cur_len + 4
    } else {
        w.u32(c_mask);
        w.dec(c.stars_gained_this_turn);
        w.ensure(100);
        w.buf.len()
    };

    // SAFETY: `ensure` above reserved 100 writable bytes beyond `len` for up to 25 i32 fields.
    unsafe {
        let base = w.buf.as_mut_ptr();
        macro_rules! push_bit_i32 {
            ($bit:expr, $val:expr) => {
                if (c_mask & (1 << $bit)) != 0 {
                    std::ptr::copy_nonoverlapping($val.to_le_bytes().as_ptr(), base.add(len), 4);
                    len += 4;
                }
            };
        }
        push_bit_i32!(1, c.skill_cards_played_this_turn);
        push_bit_i32!(2, c.attack_cards_played_this_turn);
        push_bit_i32!(3, c.shivs_played_this_turn);
        push_bit_i32!(4, c.cards_discarded_this_turn);
        push_bit_i32!(5, c.cards_played_this_combat);
        push_bit_i32!(6, c.cards_generated_this_combat);
        push_bit_i32!(7, c.sadness_generated_this_combat);
        push_bit_i32!(8, c.cards_drawn_this_combat);
        push_bit_i32!(9, c.cards_played_this_turn);
        push_bit_i32!(10, c.manual_cards_played_this_turn);
        push_bit_i32!(11, c.card_plays_started_this_turn);
        push_bit_i32!(12, c.attack_or_skill_card_plays_started_this_turn);
        push_bit_i32!(13, c.attack_cards_started_this_turn);
        push_bit_i32!(14, c.zero_cost_attacks_started_this_turn);
        push_bit_i32!(15, c.first_in_series_card_plays_started_this_turn);
        if (c_mask >> 16) != 0 {
            push_bit_i32!(16, c.wah_started_this_turn);
            push_bit_i32!(17, c.cho_started_this_turn);
            push_bit_i32!(18, c.ki_started_this_turn);
            push_bit_i32!(19, c.first_wah_play_ordinal_this_turn);
            push_bit_i32!(20, c.first_cho_play_ordinal_this_turn);
            push_bit_i32!(21, c.first_ki_play_ordinal_this_turn);
            push_bit_i32!(22, c.wah_finished_this_turn);
            push_bit_i32!(23, c.cho_finished_this_turn);
            push_bit_i32!(24, c.ki_finished_this_turn);
            push_bit_i32!(25, c.melody_cards_finished_this_turn);
        }
        w.buf.set_len(len);
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

fn hash_field_map(h: &mut WordHasher128, map: &FieldMap) {
    h.mix_u32(map.len() as u32);
    if map.is_empty() {
        return;
    }
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
fn flush_pending_fast_card(h: &mut WordHasher128, pending_fast: &mut Option<(u64, u64)>) {
    if let Some((a, b)) = pending_fast.take() {
        h.mix_u64x2(a, b);
    }
}

#[inline(always)]
fn hash_canonical_card(
    h: &mut WordHasher128,
    cards: &[CardInst],
    card_extensions: &[CardExtension],
    ch: CardHandle,
    pending_fast: &mut Option<(u64, u64)>,
) {
    let idx = ch.0.wrapping_sub(1) as usize;
    let card = if idx < cards.len() && cards[idx].handle == ch {
        &cards[idx]
    } else if let Some(c) = cards.iter().find(|c| c.handle == ch) {
        c
    } else {
        flush_pending_fast_card(h, pending_fast);
        h.mix_u64(0);
        return;
    };
    let has_ext = card
        .ext_index
        .is_some_and(|i| (i as usize) < card_extensions.len());
    let opt_mask = u16::from(card.temp_star_cost_override_this_turn.is_some())
        | (u16::from(card.temp_cost_override_this_turn.is_some()) << 1)
        | (u16::from(card.temp_cost_override_this_turn_or_until_played.is_some()) << 2)
        | (u16::from(card.temp_cost_override_this_combat.is_some()) << 3)
        | (u16::from(card.affliction.is_some()) << 4)
        | (u16::from(card.transform_into_after_play.is_some()) << 5)
        | (u16::from(has_ext) << 6)
        | (u16::from(card.upgrade_level != 0) << 7)
        | (u16::from(card.captured_energy_x_value != 0) << 8)
        | (u16::from(card.base_replay_count != 0) << 9)
        | (u16::from(card.keywords_added_from_outside.bits() != 0) << 10)
        | (u16::from(card.keywords_removed_from_outside.bits() != 0) << 11)
        | (u16::from(card.temp_flags.bits() != 0) << 12);

    let tag_lo = 1u64
        | ((card.id as u64) << 8)
        | ((card.local_keywords.bits() as u64) << 24)
        | ((opt_mask as u64) << 40);
    let cost_word = card.base_energy_cost as u32 as u64;
    if opt_mask == 0 {
        if let Some((a, b)) = pending_fast.take() {
            h.mix_u64x4(a, b, tag_lo, cost_word);
        } else {
            *pending_fast = Some((tag_lo, cost_word));
        }
        return;
    }
    if let Some((a, b)) = pending_fast.take() {
        h.mix_u64x4(a, b, tag_lo, cost_word);
    } else {
        h.mix_u64x2(tag_lo, cost_word);
    }

    if (opt_mask & (1 << 7)) != 0 {
        h.mix_i32(card.upgrade_level);
    }
    if (opt_mask & (1 << 8)) != 0 {
        h.mix_i32(card.captured_energy_x_value);
    }
    if (opt_mask & (1 << 9)) != 0 {
        h.mix_i32(card.base_replay_count);
    }
    if (opt_mask & ((1 << 10) | (1 << 11) | (1 << 12))) != 0 {
        let kw_word = (card.keywords_added_from_outside.bits() as u64)
            | ((card.keywords_removed_from_outside.bits() as u64) << 16)
            | ((card.temp_flags.bits() as u64) << 32);
        h.mix_u64(kw_word);
    }
    if let Some(v) = card.temp_star_cost_override_this_turn {
        h.mix_i32(v);
    }
    if let Some(v) = card.temp_cost_override_this_turn {
        h.mix_i32(v);
    }
    if let Some(v) = card.temp_cost_override_this_turn_or_until_played {
        h.mix_i32(v);
    }
    if let Some(v) = card.temp_cost_override_this_combat {
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

#[inline(always)]
fn hash_player_counters(h: &mut WordHasher128, c: &PlayerCounters) {
    let c_mask: u32 = u32::from(c.stars_gained_this_turn != Dec::ZERO)
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
        | (u32::from(c.first_in_series_card_plays_started_this_turn != 0) << 15)
        | (u32::from(c.wah_started_this_turn != 0) << 16)
        | (u32::from(c.cho_started_this_turn != 0) << 17)
        | (u32::from(c.ki_started_this_turn != 0) << 18)
        | (u32::from(c.first_wah_play_ordinal_this_turn != 0) << 19)
        | (u32::from(c.first_cho_play_ordinal_this_turn != 0) << 20)
        | (u32::from(c.first_ki_play_ordinal_this_turn != 0) << 21)
        | (u32::from(c.wah_finished_this_turn != 0) << 22)
        | (u32::from(c.cho_finished_this_turn != 0) << 23)
        | (u32::from(c.ki_finished_this_turn != 0) << 24)
        | (u32::from(c.melody_cards_finished_this_turn != 0) << 25);
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
        if (c_mask >> 16) != 0 {
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

/// Zero-allocation direct word-accumulator 128-bit canonical equivalence hash.
pub fn hash_canonical_key(state: &BattleState) -> crate::hash::StateHash {
    let mut h = WordHasher128::new();

    let gflags = (state.current_side as u64)
        | (u64::from(state.is_in_progress) << 8)
        | (u64::from(state.won) << 9)
        | (u64::from(state.is_player_extra_turn) << 10)
        | (u64::from(state.gold_was_stolen) << 11)
        | (u64::from(state.end_player_turn_requested) << 12)
        | ((state.round_number as u32 as u64) << 32);
    h.mix_u64(gflags);

    for stream in &state.run_rngs.streams {
        hash_rng(&mut h, stream);
    }
    hash_rng(&mut h, &state.player_rngs.streams[3]);

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

    let mut pending_fast: Option<(u64, u64)> = None;
    for pile in [&p.hand[..], &p.draw[..], &p.discard[..], &p.exhaust[..], &p.play[..]] {
        flush_pending_fast_card(&mut h, &mut pending_fast);
        h.mix_u32(pile.len() as u32);
        for &ch in pile {
            hash_canonical_card(
                &mut h,
                &state.cards,
                &state.card_extensions,
                ch,
                &mut pending_fast,
            );
        }
    }
    flush_pending_fast_card(&mut h, &mut pending_fast);

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

    h.mix_u32(state.creatures.len() as u32);
    for cr in &state.creatures {
        let combat_id_word = match cr.combat_id {
            Some(x) => 1 | ((x as u64) << 1),
            None => 0,
        };
        let cflags = (cr.side as u64)
            | (u64::from(cr.is_player) << 8)
            | (u64::from(cr.is_detached) << 9)
            | (u64::from(state.contains_creature(cr.handle)) << 10)
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
                h.mix_u32(m.current_state_id.len() as u32);
                h.mix_bytes(m.current_state_id.as_bytes());
                hash_opt_str(&mut h, m.next_move_id.as_deref());
                h.mix_u32(m.state_log_ids.len() as u32);
                for s in &m.state_log_ids {
                    h.mix_u32(s.len() as u32);
                    h.mix_bytes(s.as_bytes());
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
    if g_mask != 0 {
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
        let ex_cur = if sh.exhausts.is_empty() {
            0
        } else {
            sh.exhausts.iter().rev().take_while(|(tk, _)| *tk == cur_tk).count()
        };
        let cbg_cur = if sh.card_block_gains.is_empty() {
            0
        } else {
            sh.card_block_gains
                .iter()
                .rev()
                .take_while(|(tk, _, _)| *tk == cur_tk)
                .count()
        };
        let oc_cur = if sh.orb_channels.is_empty() {
            0
        } else {
            sh.orb_channels
                .iter()
                .rev()
                .take_while(|(tk, _, _)| *tk == cur_tk)
                .count()
        };
        let cur_energy_spent: i32 = if sh.energy_spent.is_empty() {
            0
        } else {
            sh.energy_spent
                .iter()
                .rev()
                .take_while(|(tk, _, _)| *tk == cur_tk)
                .map(|(_, _, amt)| *amt)
                .sum()
        };
        let cd_cur = if sh.card_drawn.is_empty() {
            0
        } else {
            sh.card_drawn
                .iter()
                .rev()
                .take_while(|(tk, _, _, _)| *tk == cur_tk)
                .count()
        };
        let ae_tail = if sh.actor_events.is_empty() {
            0
        } else {
            sh.actor_events
                .iter()
                .rev()
                .take_while(|(_, tk, _)| *tk == cur_tk)
                .count()
        };
        let ae_start = sh.actor_events.len() - ae_tail;
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
            h.mix_u32(kind.len() as u32);
            h.mix_bytes(kind.as_bytes());
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
            floor_added_to_deck: Some(1),
            deck_card_handle: Some(CardHandle(10)),
            clone_of_handle: None,
            temp_star_cost_override_this_turn: None,
            temp_cost_override_this_turn: None,
            temp_cost_override_this_turn_or_until_played: None,
            temp_cost_override_this_combat: None,
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
        s_renum.cards[0].deck_card_handle = Some(CardHandle(99));
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
    }

    fn hash_canonical_key_wave1_ref(state: &BattleState) -> crate::hash::StateHash {
        fn ref_opt_u32(h: &mut WordHasher128, v: Option<u32>) {
            match v {
                Some(x) => h.mix_u64(1 | ((x as u64) << 1)),
                None => h.mix_u64(0),
            }
        }
        fn ref_card(
            h: &mut WordHasher128,
            cards: &[CardInst],
            card_extensions: &[CardExtension],
            ch: CardHandle,
        ) {
            let idx = ch.0.wrapping_sub(1) as usize;
            let card = if idx < cards.len() && cards[idx].handle == ch {
                &cards[idx]
            } else if let Some(c) = cards.iter().find(|c| c.handle == ch) {
                c
            } else {
                h.mix_u64(0);
                return;
            };
            let ext_opt = card
                .ext_index
                .and_then(|i| card_extensions.get(i as usize));
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

            let tag_lo = 1u64
                | ((card.id as u64) << 8)
                | ((card.local_keywords.bits() as u64) << 24)
                | ((opt_mask as u64) << 40);
            if opt_mask == 0 {
                h.mix_u64x2(tag_lo, card.base_energy_cost as u32 as u64);
                return;
            }
            h.mix_u64x2(tag_lo, card.base_energy_cost as u32 as u64);
            if (opt_mask & (1 << 7)) != 0 {
                h.mix_i32(card.upgrade_level);
            }
            if (opt_mask & (1 << 8)) != 0 {
                h.mix_i32(card.captured_energy_x_value);
            }
            if (opt_mask & (1 << 9)) != 0 {
                h.mix_i32(card.base_replay_count);
            }
            if (opt_mask & ((1 << 10) | (1 << 11) | (1 << 12))) != 0 {
                let kw_word = (card.keywords_added_from_outside.bits() as u64)
                    | ((card.keywords_removed_from_outside.bits() as u64) << 16)
                    | ((card.temp_flags.bits() as u64) << 32);
                h.mix_u64(kw_word);
            }
            if let Some(v) = card.temp_star_cost_override_this_turn { h.mix_i32(v); }
            if let Some(v) = card.temp_cost_override_this_turn { h.mix_i32(v); }
            if let Some(v) = card.temp_cost_override_this_turn_or_until_played { h.mix_i32(v); }
            if let Some(v) = card.temp_cost_override_this_combat { h.mix_i32(v); }
            if let Some(a) = card.affliction {
                h.mix_u64(((a.id as u64) << 32) | (a.amount as u32 as u64));
            }
            if let Some(cid) = card.transform_into_after_play {
                h.mix_u32(cid as u32);
            }
            if let Some(ext) = ext_opt {
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

        let mut h = WordHasher128::new();
        let gflags = (state.current_side as u64)
            | (u64::from(state.is_in_progress) << 8)
            | (u64::from(state.won) << 9)
            | (u64::from(state.is_player_extra_turn) << 10)
            | (u64::from(state.gold_was_stolen) << 11)
            | (u64::from(state.end_player_turn_requested) << 12)
            | ((state.round_number as u32 as u64) << 32);
        h.mix_u64(gflags);
        for stream in &state.run_rngs.streams {
            hash_rng(&mut h, stream);
        }
        hash_rng(&mut h, &state.player_rngs.streams[3]);
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
        for pile in [&p.hand[..], &p.draw[..], &p.discard[..], &p.exhaust[..], &p.play[..]] {
            h.mix_u32(pile.len() as u32);
            for &ch in pile {
                ref_card(&mut h, &state.cards, &state.card_extensions, ch);
            }
        }
        h.mix_u64((p.combat_generated_potion_mask as u64) | ((p.potion_slots.len() as u64) << 16));
        for &slot in &p.potion_slots {
            match slot {
                Some(pid) => h.mix_u64(1 | ((pid as u64) << 8)),
                None => h.mix_u64(0),
            }
        }
        h.mix_u32(p.orbs.len() as u32);
        h.mix_u32(p.arrows.len() as u32);
        h.mix_u32(p.relics.len() as u32);
        h.mix_u32(state.creatures.len() as u32);
        for cr in &state.creatures {
            ref_opt_u32(&mut h, cr.combat_id);
            let cflags = (cr.side as u64)
                | (u64::from(cr.is_player) << 8)
                | (u64::from(cr.is_detached) << 9)
                | (u64::from(state.contains_creature(cr.handle)) << 10)
                | ((cr.current_hp as u32 as u64) << 32);
            h.mix_u64x2(
                cflags,
                (cr.max_hp as u32 as u64) | ((cr.block as u32 as u64) << 32),
            );
            h.mix_i32(cr.cumulative_hp_lost);
            h.mix_u64(0);
            h.mix_u32(cr.powers.len() as u32);
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
        h.mix_u64(
            (g_mask as u64)
                | ((state.semantic_history.brightest_flame_max_hp_spent as u32 as u64) << 32),
        );
        let sh = &state.semantic_history;
        let total_exhausts = sh.prior_exhaust_count as u32;
        h.mix_u64x4(total_exhausts as u64, 0, 0, 0);
        let prev_turn_player_damaged = sh.prior_player_damaged_turn == (state.player.turn_number - 1);
        h.mix_u64x2(
            0,
            u64::from(prev_turn_player_damaged)
                | ((sh.prior_player_damaged_turn as u32 as u64) << 32),
        );
        h.finish()
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
                floor_added_to_deck: None,
                deck_card_handle: Some(h),
                clone_of_handle: None,
                temp_star_cost_override_this_turn: None,
                temp_cost_override_this_turn: cost_ovr,
                temp_cost_override_this_turn_or_until_played: None,
                temp_cost_override_this_combat: None,
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
        // Mix 0..=5 cards per pile with plain (opt_mask==0), modified, and missing handles.
        let p_specs: [Vec<(CardId, i32, Option<i32>)>; 5] = [
            vec![],
            vec![(CardId::StrikeIronclad, 0, None)],
            vec![
                (CardId::StrikeIronclad, 0, None),
                (CardId::DefendIronclad, 0, None),
            ],
            vec![
                (CardId::StrikeIronclad, 0, None),
                (CardId::DefendIronclad, 0, None),
                (CardId::Bash, 0, None),
            ],
            vec![
                (CardId::StrikeIronclad, 0, None),
                (CardId::DefendIronclad, 1, None),
                (CardId::Bash, 0, None),
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

        for shift in 0..5 {
            s.cards.clear();
            s.player.hand.clear();
            s.player.draw.clear();
            s.player.discard.clear();
            s.player.exhaust.clear();
            s.player.play.clear();
            let mut next_h = 1u32;
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
                    s.cards.push(c);
                    s.pile_vec_mut(pile).unwrap().push(h);
                }
                if shift == 3 && idx == 1 {
                    // Missing handle case mid-pile after an odd fast card
                    s.player.draw.push(CardHandle(9999));
                    let c2 = mk_card(CardId::StrikeIronclad, 0, None, pile, next_h);
                    next_h += 1;
                    let h2 = c2.handle;
                    s.cards.push(c2);
                    s.player.draw.push(h2);
                }
            }
            assert_eq!(
                hash_canonical_key(&s),
                hash_canonical_key_wave1_ref(&s),
                "StateHash mismatch at rotation {shift}"
            );
        }
    }
}
