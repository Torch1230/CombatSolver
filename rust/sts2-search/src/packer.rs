use smallvec::SmallVec;
use std::collections::BTreeMap;
use std::sync::Arc;
use sts2_kernel::content_dispatch::HookListener;
use sts2_kernel::continuation::{
    CardPlayResources, CardSelectActionKind, ChoiceRequest, ChoiceTiming, ContinuationFrame,
    PendingContinuation,
};
use sts2_kernel::state::{
    AfflictionInst, ArrowSlotInst, BattleState, CardExtension, CardHandle, CardInst,
    CardTempFlags, CreatureHandle, CreatureInst, DamageHistoryEntry, EnchantmentInst, FieldValue,
    LocalEnergyModifier, MonsterInst, OrbInst, PlayerCounters, PlayerInst, PowerExtension,
    PowerInst, RelicInst, RootStaticContext, SemanticHistory, TurnKey,
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

    #[inline]
    fn u8(&mut self, v: u8) {
        self.buf.push(v);
    }

    #[inline]
    fn bool(&mut self, v: bool) {
        self.buf.push(u8::from(v));
    }

    #[inline]
    fn u16(&mut self, v: u16) {
        self.buf.extend_from_slice(&v.to_le_bytes());
    }

    #[inline]
    fn u32(&mut self, v: u32) {
        self.buf.extend_from_slice(&v.to_le_bytes());
    }

    #[inline]
    fn i32(&mut self, v: i32) {
        self.buf.extend_from_slice(&v.to_le_bytes());
    }

    #[inline]
    fn u64(&mut self, v: u64) {
        self.buf.extend_from_slice(&v.to_le_bytes());
    }

    #[inline]
    fn var_u32(&mut self, mut v: u32) {
        while v >= 0x80 {
            self.buf.push((v as u8) | 0x80);
            v >>= 7;
        }
        self.buf.push(v as u8);
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
            self.u8(0);
            self.buf.extend_from_slice(&m64.to_le_bytes());
            self.u32(d.scale());
        } else {
            self.u8(1);
            self.str(&v.to_string());
        }
    }

    #[inline]
    fn str(&mut self, s: &str) {
        self.var_u32(s.len() as u32);
        self.buf.extend_from_slice(s.as_bytes());
    }

    #[inline]
    fn opt_str(&mut self, s: &Option<String>) {
        match s {
            Some(v) => {
                self.u8(1);
                self.str(v);
            }
            None => self.u8(0),
        }
    }

    #[inline]
    fn opt_i32(&mut self, v: Option<i32>) {
        match v {
            Some(x) => {
                self.u8(1);
                self.i32(x);
            }
            None => self.u8(0),
        }
    }

    #[inline]
    fn opt_u32(&mut self, v: Option<u32>) {
        match v {
            Some(x) => {
                self.u8(1);
                self.u32(x);
            }
            None => self.u8(0),
        }
    }

    #[inline]
    fn opt_u64(&mut self, v: Option<u64>) {
        match v {
            Some(x) => {
                self.u8(1);
                self.u64(x);
            }
            None => self.u8(0),
        }
    }

    #[inline]
    fn rng(&mut self, r: &Rng) {
        self.i32(r.counter);
        self.u64(r.random.s0);
        self.u64(r.random.s1);
        self.u64(r.random.s2);
        self.u64(r.random.s3);
    }

    fn field_map(&mut self, map: &BTreeMap<String, FieldValue>) {
        self.var_u32(map.len() as u32);
        for (k, v) in map {
            self.str(k);
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

struct ByteReader<'a> {
    buf: &'a [u8],
    pos: usize,
}

impl<'a> ByteReader<'a> {
    #[inline]
    fn new(buf: &'a [u8]) -> Self {
        Self { buf, pos: 0 }
    }

    #[inline]
    fn err(&self, msg: &str) -> KernelError {
        KernelError::InvalidState(format!("state unpack error at {}: {msg}", self.pos))
    }

    #[inline]
    fn take(&mut self, n: usize) -> Result<&'a [u8], KernelError> {
        if self.pos + n > self.buf.len() {
            return Err(self.err("unexpected eof"));
        }
        let s = &self.buf[self.pos..self.pos + n];
        self.pos += n;
        Ok(s)
    }

    #[inline]
    fn u8(&mut self) -> Result<u8, KernelError> {
        Ok(self.take(1)?[0])
    }

    #[inline]
    fn bool(&mut self) -> Result<bool, KernelError> {
        Ok(self.u8()? != 0)
    }

    #[inline]
    fn u16(&mut self) -> Result<u16, KernelError> {
        let mut b = [0u8; 2];
        b.copy_from_slice(self.take(2)?);
        Ok(u16::from_le_bytes(b))
    }

    #[inline]
    fn u32(&mut self) -> Result<u32, KernelError> {
        let mut b = [0u8; 4];
        b.copy_from_slice(self.take(4)?);
        Ok(u32::from_le_bytes(b))
    }

    #[inline]
    fn i32(&mut self) -> Result<i32, KernelError> {
        let mut b = [0u8; 4];
        b.copy_from_slice(self.take(4)?);
        Ok(i32::from_le_bytes(b))
    }

    #[inline]
    fn u64(&mut self) -> Result<u64, KernelError> {
        let mut b = [0u8; 8];
        b.copy_from_slice(self.take(8)?);
        Ok(u64::from_le_bytes(b))
    }

    #[inline]
    fn var_u32(&mut self) -> Result<u32, KernelError> {
        let mut res = 0u32;
        let mut shift = 0u32;
        loop {
            let byte = self.u8()?;
            res |= ((byte & 0x7F) as u32) << shift;
            if (byte & 0x80) == 0 {
                return Ok(res);
            }
            shift += 7;
            if shift >= 35 {
                return Err(self.err("varint overflow"));
            }
        }
    }

    #[inline]
    fn var_i32(&mut self) -> Result<i32, KernelError> {
        let zz = self.var_u32()?;
        Ok(((zz >> 1) as i32) ^ -((zz & 1) as i32))
    }

    #[inline]
    fn dec(&mut self) -> Result<Dec, KernelError> {
        if self.u8()? == 0 {
            let mut b = [0u8; 8];
            b.copy_from_slice(self.take(8)?);
            let m = i64::from_le_bytes(b);
            let scale = self.u32()?;
            Dec::from_parts(m, scale).map_err(|_| self.err("decimal overflow"))
        } else {
            let s = self.str()?;
            serde_json::from_str(&format!("\"{s}\"")).map_err(|_| self.err("decimal parse"))
        }
    }

    #[inline]
    fn str(&mut self) -> Result<String, KernelError> {
        let mut s = String::new();
        self.read_str_into(&mut s)?;
        Ok(s)
    }

    #[inline]
    fn read_str_into(&mut self, dst: &mut String) -> Result<(), KernelError> {
        let len = self.var_u32()? as usize;
        let bytes = self.take(len)?;
        let s = std::str::from_utf8(bytes).map_err(|_| self.err("invalid utf8"))?;
        dst.clear();
        dst.push_str(s);
        Ok(())
    }

    #[inline]
    fn read_opt_str_into(&mut self, dst: &mut Option<String>) -> Result<(), KernelError> {
        if self.u8()? == 0 {
            *dst = None;
            Ok(())
        } else {
            let s = dst.get_or_insert_with(String::new);
            self.read_str_into(s)
        }
    }

    #[inline]
    fn opt_str(&mut self) -> Result<Option<String>, KernelError> {
        if self.u8()? == 0 {
            Ok(None)
        } else {
            Ok(Some(self.str()?))
        }
    }

    #[inline]
    fn opt_i32(&mut self) -> Result<Option<i32>, KernelError> {
        if self.u8()? == 0 {
            Ok(None)
        } else {
            Ok(Some(self.i32()?))
        }
    }

    #[inline]
    fn opt_u32(&mut self) -> Result<Option<u32>, KernelError> {
        if self.u8()? == 0 {
            Ok(None)
        } else {
            Ok(Some(self.u32()?))
        }
    }

    #[inline]
    fn opt_u64(&mut self) -> Result<Option<u64>, KernelError> {
        if self.u8()? == 0 {
            Ok(None)
        } else {
            Ok(Some(self.u64()?))
        }
    }

    #[inline]
    fn rng(&mut self) -> Result<Rng, KernelError> {
        let counter = self.i32()?;
        let s0 = self.u64()?;
        let s1 = self.u64()?;
        let s2 = self.u64()?;
        let s3 = self.u64()?;
        Ok(Rng {
            counter,
            random: MegaRandom::from_state(s0, s1, s2, s3),
        })
    }

    fn field_map(&mut self) -> Result<BTreeMap<String, FieldValue>, KernelError> {
        let mut map = BTreeMap::new();
        self.field_map_into(&mut map)?;
        Ok(map)
    }

    fn field_map_into(&mut self, map: &mut BTreeMap<String, FieldValue>) -> Result<(), KernelError> {
        let len = self.var_u32()? as usize;
        if len == 0 {
            if !map.is_empty() {
                map.clear();
            }
            return Ok(());
        }
        map.clear();
        for _ in 0..len {
            let k = self.str()?;
            let v = self.field_value()?;
            map.insert(k, v);
        }
        Ok(())
    }

    fn field_value(&mut self) -> Result<FieldValue, KernelError> {
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
            other => Err(self.err(&format!("unknown FieldValue tag {other}"))),
        }
    }
}

#[inline]
fn decode_card_id(raw: u16) -> Result<CardId, KernelError> {
    if (raw as usize) < CardId::COUNT {
        Ok(unsafe { core::mem::transmute::<u16, CardId>(raw) })
    } else {
        Err(KernelError::InvalidState(format!("invalid CardId {raw}")))
    }
}

#[inline]
fn decode_power_id(raw: u16) -> Result<PowerId, KernelError> {
    if (raw as usize) < PowerId::COUNT {
        Ok(unsafe { core::mem::transmute::<u16, PowerId>(raw) })
    } else {
        Err(KernelError::InvalidState(format!("invalid PowerId {raw}")))
    }
}

#[inline]
fn decode_relic_id(raw: u16) -> Result<RelicId, KernelError> {
    if (raw as usize) < RelicId::COUNT {
        Ok(unsafe { core::mem::transmute::<u16, RelicId>(raw) })
    } else {
        Err(KernelError::InvalidState(format!("invalid RelicId {raw}")))
    }
}

#[inline]
fn decode_potion_id(raw: u16) -> Result<PotionId, KernelError> {
    if (raw as usize) < PotionId::COUNT {
        Ok(unsafe { core::mem::transmute::<u16, PotionId>(raw) })
    } else {
        Err(KernelError::InvalidState(format!("invalid PotionId {raw}")))
    }
}

#[inline]
fn decode_monster_id(raw: u16) -> Result<MonsterId, KernelError> {
    if (raw as usize) < MonsterId::COUNT {
        Ok(unsafe { core::mem::transmute::<u16, MonsterId>(raw) })
    } else {
        Err(KernelError::InvalidState(format!("invalid MonsterId {raw}")))
    }
}

#[inline]
fn decode_orb_id(raw: u8) -> Result<OrbId, KernelError> {
    if (raw as usize) < OrbId::COUNT {
        Ok(unsafe { core::mem::transmute::<u8, OrbId>(raw) })
    } else {
        Err(KernelError::InvalidState(format!("invalid OrbId {raw}")))
    }
}

#[inline]
fn decode_enchantment_id(raw: u16) -> Result<EnchantmentId, KernelError> {
    if (raw as usize) < EnchantmentId::COUNT {
        Ok(unsafe { core::mem::transmute::<u16, EnchantmentId>(raw) })
    } else {
        Err(KernelError::InvalidState(format!(
            "invalid EnchantmentId {raw}"
        )))
    }
}

#[inline]
fn decode_affliction_id(raw: u16) -> Result<AfflictionId, KernelError> {
    if (raw as usize) < AfflictionId::COUNT {
        Ok(unsafe { core::mem::transmute::<u16, AfflictionId>(raw) })
    } else {
        Err(KernelError::InvalidState(format!(
            "invalid AfflictionId {raw}"
        )))
    }
}

#[inline]
fn decode_dominion_id(raw: u16) -> Result<DominionId, KernelError> {
    if (raw as usize) < DominionId::COUNT {
        Ok(unsafe { core::mem::transmute::<u16, DominionId>(raw) })
    } else {
        Err(KernelError::InvalidState(format!("invalid DominionId {raw}")))
    }
}

#[inline]
fn decode_run_model_id(raw: u8) -> Result<RunModelId, KernelError> {
    if (raw as usize) < RunModelId::COUNT {
        Ok(unsafe { core::mem::transmute::<u8, RunModelId>(raw) })
    } else {
        Err(KernelError::InvalidState(format!("invalid RunModelId {raw}")))
    }
}

#[inline]
fn decode_combat_side(raw: u8) -> Result<CombatSide, KernelError> {
    match raw {
        0 => Ok(CombatSide::Player),
        1 => Ok(CombatSide::Enemy),
        _ => Err(KernelError::InvalidState(format!("invalid CombatSide {raw}"))),
    }
}

#[inline]
fn decode_player_turn_phase(raw: u8) -> Result<PlayerTurnPhase, KernelError> {
    match raw {
        0 => Ok(PlayerTurnPhase::None),
        1 => Ok(PlayerTurnPhase::Start),
        2 => Ok(PlayerTurnPhase::AutoPrePlay),
        3 => Ok(PlayerTurnPhase::Play),
        4 => Ok(PlayerTurnPhase::AutoPostPlay),
        5 => Ok(PlayerTurnPhase::End),
        _ => Err(KernelError::InvalidState(format!(
            "invalid PlayerTurnPhase {raw}"
        ))),
    }
}

#[inline]
fn decode_pile_type(raw: u8) -> Result<PileType, KernelError> {
    match raw {
        0 => Ok(PileType::None),
        1 => Ok(PileType::Draw),
        2 => Ok(PileType::Hand),
        3 => Ok(PileType::Discard),
        4 => Ok(PileType::Exhaust),
        5 => Ok(PileType::Play),
        6 => Ok(PileType::Deck),
        _ => Err(KernelError::InvalidState(format!("invalid PileType {raw}"))),
    }
}

#[inline]
fn decode_pile_pos(raw: u8) -> Result<CardPilePosition, KernelError> {
    match raw {
        0 => Ok(CardPilePosition::Bottom),
        1 => Ok(CardPilePosition::Top),
        2 => Ok(CardPilePosition::Random),
        _ => Err(KernelError::InvalidState(format!(
            "invalid CardPilePosition {raw}"
        ))),
    }
}

#[inline]
fn decode_arrow_status(raw: u8) -> Result<ArrowStatus, KernelError> {
    match raw {
        0 => Ok(ArrowStatus::Retrieved),
        1 => Ok(ArrowStatus::Away),
        2 => Ok(ArrowStatus::Destroyed),
        _ => Err(KernelError::InvalidState(format!(
            "invalid ArrowStatus {raw}"
        ))),
    }
}

#[inline]
fn decode_ench_status(raw: u8) -> Result<EnchantmentStatus, KernelError> {
    match raw {
        0 => Ok(EnchantmentStatus::Normal),
        1 => Ok(EnchantmentStatus::Disabled),
        _ => Err(KernelError::InvalidState(format!(
            "invalid EnchantmentStatus {raw}"
        ))),
    }
}

#[inline]
fn decode_card_type(raw: u8) -> Result<CardType, KernelError> {
    match raw {
        0 => Ok(CardType::None),
        1 => Ok(CardType::Attack),
        2 => Ok(CardType::Skill),
        3 => Ok(CardType::Power),
        4 => Ok(CardType::Status),
        5 => Ok(CardType::Curse),
        6 => Ok(CardType::Quest),
        _ => Err(KernelError::InvalidState(format!("invalid CardType {raw}"))),
    }
}

#[inline]
fn decode_mod_kind(raw: u8) -> Result<LocalEnergyModifierKind, KernelError> {
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
        _ => Err(KernelError::InvalidState(format!(
            "invalid LocalEnergyModifierKind {raw}"
        ))),
    }
}

fn write_card_handles(w: &mut ByteWriter<'_>, handles: &[CardHandle]) {
    w.var_u32(handles.len() as u32);
    for h in handles {
        w.u32(h.0);
    }
}

fn read_card_handles<const N: usize>(
    r: &mut ByteReader<'_>,
    out: &mut SmallVec<[CardHandle; N]>,
) -> Result<(), KernelError>
where
    [CardHandle; N]: smallvec::Array<Item = CardHandle>,
{
    let len = r.var_u32()? as usize;
    out.clear();
    out.reserve(len);
    for _ in 0..len {
        out.push(CardHandle(r.u32()?));
    }
    Ok(())
}

fn write_creature_handles(w: &mut ByteWriter<'_>, handles: &[CreatureHandle]) {
    w.var_u32(handles.len() as u32);
    for h in handles {
        w.u32(h.0);
    }
}

fn read_creature_handles<const N: usize>(
    r: &mut ByteReader<'_>,
    out: &mut SmallVec<[CreatureHandle; N]>,
) -> Result<(), KernelError>
where
    [CreatureHandle; N]: smallvec::Array<Item = CreatureHandle>,
{
    let len = r.var_u32()? as usize;
    out.clear();
    out.reserve(len);
    for _ in 0..len {
        out.push(CreatureHandle(r.u32()?));
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

fn read_turn_key(r: &mut ByteReader<'_>) -> Result<TurnKey, KernelError> {
    let round_number = r.i32()?;
    let side = decode_combat_side(r.u8()?)?;
    let len = r.var_u32()? as usize;
    let mut player_turn_numbers = SmallVec::with_capacity(len);
    for _ in 0..len {
        player_turn_numbers.push(r.opt_i32()?);
    }
    Ok(TurnKey {
        round_number,
        side,
        player_turn_numbers,
    })
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

fn read_hook_listener(r: &mut ByteReader<'_>) -> Result<HookListener, KernelError> {
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
        other => Err(KernelError::InvalidState(format!(
            "invalid HookListener tag {other}"
        ))),
    }
}

fn write_choice_request(w: &mut ByteWriter<'_>, req: &ChoiceRequest) {
    w.u8(match req.timing {
        ChoiceTiming::Action => 0,
        ChoiceTiming::PlayerTurnEnd => 1,
        ChoiceTiming::EnemyTurn => 2,
        ChoiceTiming::PlayerTurnStart => 3,
    });
    w.str(&req.source_id);
    w.u8(req.source_pile as u8);
    w.var_u32(req.min_count as u32);
    w.var_u32(req.max_count as u32);
    write_card_handles(w, &req.candidates);
    w.bool(req.can_skip);
}

fn read_choice_request(r: &mut ByteReader<'_>) -> Result<ChoiceRequest, KernelError> {
    let timing = match r.u8()? {
        0 => ChoiceTiming::Action,
        1 => ChoiceTiming::PlayerTurnEnd,
        2 => ChoiceTiming::EnemyTurn,
        3 => ChoiceTiming::PlayerTurnStart,
        x => {
            return Err(KernelError::InvalidState(format!(
                "invalid ChoiceTiming {x}"
            )));
        }
    };
    let source_id = r.str()?;
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

fn read_pending(r: &mut ByteReader<'_>) -> Result<Option<PendingContinuation>, KernelError> {
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
                    x => {
                        return Err(KernelError::InvalidState(format!(
                            "invalid resume_turn_start {x}"
                        )));
                    }
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
                    x => {
                        return Err(KernelError::InvalidState(format!(
                            "invalid CardSelectActionKind {x}"
                        )));
                    }
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
                    x => {
                        return Err(KernelError::InvalidState(format!(
                            "invalid force_exhaust {x}"
                        )));
                    }
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
            other => {
                return Err(KernelError::InvalidState(format!(
                    "invalid ContinuationFrame tag {other}"
                )));
            }
        };
        frames.push(frame);
    }
    Ok(Some(PendingContinuation { request, frames }))
}

/// Packs the entire mutable `BattleState` into a compact variable-length binary record.
/// Static run context (`static_ctx`) is shared via `Arc<RootStaticContext>` and not duplicated per node.
pub fn pack_state(state: &BattleState, out: &mut Vec<u8>) {
    out.clear();
    let mut w = ByteWriter::new(out);

    // Header scalars
    w.u8(state.current_side as u8);
    w.i32(state.round_number);
    w.bool(state.is_in_progress);
    w.bool(state.is_starting);
    w.bool(state.won);
    w.bool(state.is_player_extra_turn);
    w.bool(state.gold_was_stolen);
    w.bool(state.end_player_turn_requested);
    w.i32(state.card_action_boundary_depth);
    w.u32(state.next_creature_id);
    w.var_u32(state.initial_encounter_monster_count as u32);
    w.u32(state.next_card_handle);
    w.u32(state.next_creature_handle);
    w.u32(state.next_power_handle);
    w.u64(state.next_attack_id);

    // RNG streams
    w.str(&state.run_rngs.string_seed);
    w.u64(state.run_rngs.seed);
    for stream in &state.run_rngs.streams {
        w.rng(stream);
    }
    w.u64(state.player_rngs.seed);
    for stream in &state.player_rngs.streams {
        w.rng(stream);
    }

    // PlayerInst
    let p = &state.player;
    w.u32(p.creature_handle.0);
    w.i32(p.current_hp);
    w.i32(p.max_hp);
    w.i32(p.block);
    w.i32(p.cumulative_hp_lost);
    w.i32(p.gold);
    w.i32(p.max_energy);
    w.i32(p.base_orb_slot_count);
    w.bool(p.is_active_for_hooks);
    w.bool(p.can_use_or_remove_potions);
    w.i32(p.energy);
    w.i32(p.stars);
    w.u8(p.phase as u8);
    w.i32(p.turn_number);
    w.i32(p.card_or_potion_effect_depth);

    // PlayerCounters (26 fields bitmask-packed)
    write_player_counters(&mut w, &p.counters);

    write_card_handles(&mut w, &p.hand);
    write_card_handles(&mut w, &p.draw);
    write_card_handles(&mut w, &p.discard);
    write_card_handles(&mut w, &p.exhaust);
    write_card_handles(&mut w, &p.play);
    write_card_handles(&mut w, &p.deck);

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
        w.u16(relic.id as u16);
        w.bool(relic.is_wax);
        w.bool(relic.is_melted);
        w.i32(relic.stack_count);
        w.opt_i32(relic.floor_added_to_deck);
        w.field_map(&relic.private_fields);
    }

    // Cards (bitmask-packed infrequent/optional fields)
    w.var_u32(state.cards.len() as u32);
    for card in &state.cards {
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

        w.var_u32(card.handle.0);
        w.u16(card.id as u16);
        w.i32(card.canonical_base_energy_cost);
        w.u16(card.local_keywords.bits());
        w.u16(opt_mask);
        w.u8(flags_byte);

        if (opt_mask & (1 << 0)) != 0 { w.i32(card.upgrade_level); }
        if (opt_mask & (1 << 1)) != 0 { w.i32(card.base_energy_cost); }
        if (opt_mask & (1 << 2)) != 0 { w.i32(card.captured_energy_x_value); }
        if (opt_mask & (1 << 3)) != 0 { w.i32(card.base_replay_count); }
        if let Some(v) = card.floor_added_to_deck { w.i32(v); }
        if let Some(h) = card.deck_card_handle { w.var_u32(h.0); }
        if let Some(h) = card.clone_of_handle { w.var_u32(h.0); }
        if let Some(v) = card.temp_star_cost_override_this_turn { w.i32(v); }
        if let Some(v) = card.temp_cost_override_this_turn { w.i32(v); }
        if let Some(v) = card.temp_cost_override_this_turn_or_until_played { w.i32(v); }
        if let Some(v) = card.temp_cost_override_this_combat { w.i32(v); }
        if (opt_mask & (1 << 11)) != 0 { w.u16(card.keywords_added_from_outside.bits()); }
        if (opt_mask & (1 << 12)) != 0 { w.u16(card.keywords_removed_from_outside.bits()); }
        if (opt_mask & (1 << 13)) != 0 { w.u16(card.temp_flags.bits()); }
        if let Some(aff) = card.affliction {
            w.u16(aff.id as u16);
            w.i32(aff.amount);
        }
        if let Some(ei) = card.ext_index { w.var_u32(ei); }
        if let Some(cid) = card.transform_into_after_play { w.u16(cid as u16); }
    }

    // CardExtensions
    w.var_u32(state.card_extensions.len() as u32);
    for ext in &state.card_extensions {
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

    // Creatures
    w.var_u32(state.creatures.len() as u32);
    for cr in &state.creatures {
        w.u32(cr.handle.0);
        w.opt_u32(cr.combat_id);
        w.u8(cr.side as u8);
        w.opt_str(&cr.slot_name);
        w.bool(cr.is_player);
        w.opt_u32(cr.pet_owner_player_index.map(|i| i as u32));
        match cr.monster_id {
            Some(mid) => {
                w.u8(1);
                w.u16(mid as u16);
            }
            None => w.u8(0),
        }
        w.i32(cr.current_hp);
        w.i32(cr.max_hp);
        w.i32(cr.block);
        w.i32(cr.cumulative_hp_lost);
        w.bool(cr.is_detached);

        match &cr.monster {
            Some(m) => {
                w.u8(1);
                w.u16(m.monster_id as u16);
                w.rng(&m.rng);
                w.bool(m.is_performing_move);
                w.bool(m.spawned_this_turn);
                w.str(&m.current_state_id);
                w.opt_str(&m.next_move_id);
                w.bool(m.next_move_can_transition_away);
                w.bool(m.performed_first_move);
                w.var_u32(m.state_log_ids.len() as u32);
                for s in &m.state_log_ids {
                    w.str(s);
                }
                w.opt_str(&m.transient_move_follow_up_state_id);
                w.field_map(&m.setup_fields);
                w.field_map(&m.private_fields);
            }
            None => w.u8(0),
        }

        w.var_u32(cr.powers.len() as u32);
        for pw in &cr.powers {
            w.u32(pw.handle);
            w.u16(pw.id as u16);
            w.i32(pw.amount);
            w.i32(pw.amount_on_turn_start);
            w.bool(pw.skip_next_duration_tick);
            w.opt_u32(pw.applier_handle.map(|h| h.0));
            w.opt_u32(pw.ext_index);
        }
    }

    // PowerExtensions
    w.var_u32(state.power_extensions.len() as u32);
    for pe in &state.power_extensions {
        w.field_map(&pe.private_fields);
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
        w.u16(flags);
        w.u8(dh.props.bits());
        w.u8(dh.current_side as u8);
        w.var_u32(dh.receiver_creature_handle.0);
        if let Some(dealer) = dh.dealer_creature_handle {
            w.var_u32(dealer.0);
        }
        if dh.original_target_creature_handle != dh.receiver_creature_handle {
            w.var_u32(dh.original_target_creature_handle.0);
        }
        if dh.intended_hp_loss != 0 {
            w.var_i32(dh.intended_hp_loss);
        }
        if dh.blocked_damage != 0 {
            w.var_i32(dh.blocked_damage);
        }
        if dh.unblocked_damage != 0 {
            w.var_i32(dh.unblocked_damage);
        }
        if dh.overkill_damage != 0 {
            w.var_i32(dh.overkill_damage);
        }
        if let Some(cs) = dh.card_source_handle {
            w.var_u32(cs.0);
        }
        w.var_i32(dh.round_number);
        if dh.player_turn_numbers.len() == 1 {
            w.var_i32(dh.player_turn_numbers[0]);
        } else if dh.player_turn_numbers.len() > 1 {
            w.var_u32(dh.player_turn_numbers.len() as u32);
            for &tn in &dh.player_turn_numbers {
                w.var_i32(tn);
            }
        }
    }

    // Semantic history
    let sh = &state.semantic_history;
    w.var_u32(sh.exhausts.len() as u32);
    for (tk, idx) in &sh.exhausts {
        write_turn_key(&mut w, tk);
        w.var_u32(*idx as u32);
    }
    w.var_u32(sh.card_block_gains.len() as u32);
    for (tk, idx, ord) in &sh.card_block_gains {
        write_turn_key(&mut w, tk);
        w.var_u32(*idx as u32);
        w.i32(*ord);
    }
    w.var_u32(sh.orb_channels.len() as u32);
    for (tk, idx, oid) in &sh.orb_channels {
        write_turn_key(&mut w, tk);
        w.var_u32(*idx as u32);
        w.u8(*oid as u8);
    }
    w.var_u32(sh.energy_spent.len() as u32);
    for (tk, idx, amt) in &sh.energy_spent {
        write_turn_key(&mut w, tk);
        w.var_u32(*idx as u32);
        w.i32(*amt);
    }
    w.var_u32(sh.card_drawn.len() as u32);
    for (tk, idx, ct, from_hand) in &sh.card_drawn {
        write_turn_key(&mut w, tk);
        w.var_u32(*idx as u32);
        w.u8(*ct as u8);
        w.bool(*from_hand);
    }
    w.var_u32(sh.actor_events.len() as u32);
    for (kind, tk, idx) in &sh.actor_events {
        w.str(kind);
        write_turn_key(&mut w, tk);
        w.var_u32(*idx as u32);
    }
    let gr = &sh.growth_rewards;
    w.i32(gr.hand_of_greed);
    w.i32(gr.the_hunt);
    w.i32(gr.feed);
    w.i32(gr.royalties);
    w.i32(gr.alchemize);
    w.i32(gr.genetic_algorithm);
    w.i32(gr.the_scythe);
    w.i32(gr.goopy);
    w.i32(gr.forbidden_grimoire);
    w.i32(gr.mad_science);
    w.i32(sh.brightest_flame_max_hp_spent);

    // PendingContinuation
    write_pending(&mut w, &state.pending);
    w.u8(state.vakuu_auto_select_depth);
}

/// Unpacks a packed binary state directly into a reusable `BattleState` scratch buffer.
pub fn unpack_state(
    bytes: &[u8],
    static_ctx: Arc<RootStaticContext>,
    out: &mut BattleState,
) -> Result<(), KernelError> {
    let mut r = ByteReader::new(bytes);
    out.static_ctx = static_ctx;
    out.current_side = decode_combat_side(r.u8()?)?;
    out.round_number = r.i32()?;
    out.is_in_progress = r.bool()?;
    out.is_starting = r.bool()?;
    out.won = r.bool()?;
    out.is_player_extra_turn = r.bool()?;
    out.gold_was_stolen = r.bool()?;
    out.end_player_turn_requested = r.bool()?;
    out.card_action_boundary_depth = r.i32()?;
    out.next_creature_id = r.u32()?;
    out.initial_encounter_monster_count = r.var_u32()? as usize;
    out.next_card_handle = r.u32()?;
    out.next_creature_handle = r.u32()?;
    out.next_power_handle = r.u32()?;
    out.next_attack_id = r.u64()?;

    r.read_str_into(&mut out.run_rngs.string_seed)?;
    out.run_rngs.seed = r.u64()?;
    for s in &mut out.run_rngs.streams {
        *s = r.rng()?;
    }

    let player_seed = r.u64()?;
    let mut player_streams = [Rng::from_seed(0); 4];
    for s in &mut player_streams {
        *s = r.rng()?;
    }
    out.player_rngs = PlayerRngSet {
        seed: player_seed,
        streams: player_streams,
    };

    // PlayerInst
    let p = &mut out.player;
    p.creature_handle = CreatureHandle(r.u32()?);
    p.current_hp = r.i32()?;
    p.max_hp = r.i32()?;
    p.block = r.i32()?;
    p.cumulative_hp_lost = r.i32()?;
    p.gold = r.i32()?;
    p.max_energy = r.i32()?;
    p.base_orb_slot_count = r.i32()?;
    p.is_active_for_hooks = r.bool()?;
    p.can_use_or_remove_potions = r.bool()?;
    p.energy = r.i32()?;
    p.stars = r.i32()?;
    p.phase = decode_player_turn_phase(r.u8()?)?;
    p.turn_number = r.i32()?;
    p.card_or_potion_effect_depth = r.i32()?;

    let c_mask = r.u32()?;
    p.counters = PlayerCounters {
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
        wah_started_this_turn: if (c_mask & (1 << 16)) != 0 { r.i32()? } else { 0 },
        cho_started_this_turn: if (c_mask & (1 << 17)) != 0 { r.i32()? } else { 0 },
        ki_started_this_turn: if (c_mask & (1 << 18)) != 0 { r.i32()? } else { 0 },
        first_wah_play_ordinal_this_turn: if (c_mask & (1 << 19)) != 0 { r.i32()? } else { 0 },
        first_cho_play_ordinal_this_turn: if (c_mask & (1 << 20)) != 0 { r.i32()? } else { 0 },
        first_ki_play_ordinal_this_turn: if (c_mask & (1 << 21)) != 0 { r.i32()? } else { 0 },
        wah_finished_this_turn: if (c_mask & (1 << 22)) != 0 { r.i32()? } else { 0 },
        cho_finished_this_turn: if (c_mask & (1 << 23)) != 0 { r.i32()? } else { 0 },
        ki_finished_this_turn: if (c_mask & (1 << 24)) != 0 { r.i32()? } else { 0 },
        melody_cards_finished_this_turn: if (c_mask & (1 << 25)) != 0 { r.i32()? } else { 0 },
    };

    read_card_handles(&mut r, &mut p.hand)?;
    read_card_handles(&mut r, &mut p.draw)?;
    read_card_handles(&mut r, &mut p.discard)?;
    read_card_handles(&mut r, &mut p.exhaust)?;
    read_card_handles(&mut r, &mut p.play)?;
    read_card_handles(&mut r, &mut p.deck)?;

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

    let arrow_len = r.var_u32()? as usize;
    p.arrows.clear();
    p.arrows.reserve(arrow_len);
    for _ in 0..arrow_len {
        p.arrows.push(ArrowSlotInst {
            status: decode_arrow_status(r.u8()?)?,
            destroy_after_impaled: r.bool()?,
        });
    }

    let relic_len = r.var_u32()? as usize;
    let old_relic_len = p.relics.len();
    for idx in 0..relic_len {
        let id = decode_relic_id(r.u16()?)?;
        let is_wax = r.bool()?;
        let is_melted = r.bool()?;
        let stack_count = r.i32()?;
        let floor_added_to_deck = r.opt_i32()?;
        if idx < old_relic_len {
            let dr = &mut p.relics[idx];
            dr.id = id;
            dr.is_wax = is_wax;
            dr.is_melted = is_melted;
            dr.stack_count = stack_count;
            dr.floor_added_to_deck = floor_added_to_deck;
            r.field_map_into(&mut dr.private_fields)?;
        } else {
            p.relics.push(RelicInst {
                id,
                is_wax,
                is_melted,
                stack_count,
                floor_added_to_deck,
                private_fields: r.field_map()?,
            });
        }
    }
    if old_relic_len > relic_len {
        p.relics.truncate(relic_len);
    }

    // Cards
    let cards_len = r.var_u32()? as usize;
    out.cards.clear();
    out.cards.reserve(cards_len);
    for _ in 0..cards_len {
        let handle = CardHandle(r.var_u32()?);
        let id = decode_card_id(r.u16()?)?;
        let canonical_base_energy_cost = r.i32()?;
        let local_keywords = CardKeywords::from_bits_retain(r.u16()?);
        let opt_mask = r.u16()?;
        let flags_byte = r.u8()?;

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
            de.local_energy_modifiers.clear();
            de.local_energy_modifiers.reserve(mlen);
            for _ in 0..mlen {
                de.local_energy_modifiers.push(LocalEnergyModifier {
                    kind: decode_mod_kind(r.u8()?)?,
                    amount: r.i32()?,
                    is_reduce_only: r.bool()?,
                });
            }
            if r.u8()? == 0 {
                de.enchantment = None;
            } else {
                let id = decode_enchantment_id(r.u16()?)?;
                let amount = r.i32()?;
                let status = decode_ench_status(r.u8()?)?;
                if let Some(en) = de.enchantment.as_mut() {
                    en.id = id;
                    en.amount = amount;
                    en.status = status;
                    r.field_map_into(&mut en.private_fields)?;
                } else {
                    de.enchantment = Some(EnchantmentInst {
                        id,
                        amount,
                        status,
                        private_fields: r.field_map()?,
                    });
                }
            }
            r.field_map_into(&mut de.private_fields)?;
        } else {
            let mut local_energy_modifiers = SmallVec::with_capacity(mlen);
            for _ in 0..mlen {
                local_energy_modifiers.push(LocalEnergyModifier {
                    kind: decode_mod_kind(r.u8()?)?,
                    amount: r.i32()?,
                    is_reduce_only: r.bool()?,
                });
            }
            let enchantment = if r.u8()? == 0 {
                None
            } else {
                Some(EnchantmentInst {
                    id: decode_enchantment_id(r.u16()?)?,
                    amount: r.i32()?,
                    status: decode_ench_status(r.u8()?)?,
                    private_fields: r.field_map()?,
                })
            };
            let private_fields = r.field_map()?;
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
            r.read_opt_str_into(&mut dc.slot_name)?;
            dc.is_player = r.bool()?;
            dc.pet_owner_player_index = r.opt_u32()?.map(|x| x as usize);
            dc.monster_id = if r.u8()? == 0 {
                None
            } else {
                Some(decode_monster_id(r.u16()?)?)
            };
            dc.current_hp = r.i32()?;
            dc.max_hp = r.i32()?;
            dc.block = r.i32()?;
            dc.cumulative_hp_lost = r.i32()?;
            dc.is_detached = r.bool()?;

            if r.u8()? == 0 {
                dc.monster = None;
            } else {
                let mid = decode_monster_id(r.u16()?)?;
                let rng = r.rng()?;
                let is_performing_move = r.bool()?;
                let spawned_this_turn = r.bool()?;
                if let Some(dm) = dc.monster.as_mut() {
                    dm.monster_id = mid;
                    dm.rng = rng;
                    dm.is_performing_move = is_performing_move;
                    dm.spawned_this_turn = spawned_this_turn;
                    r.read_str_into(&mut dm.current_state_id)?;
                    r.read_opt_str_into(&mut dm.next_move_id)?;
                    dm.next_move_can_transition_away = r.bool()?;
                    dm.performed_first_move = r.bool()?;
                    let slog_len = r.var_u32()? as usize;
                    let old_slog_len = dm.state_log_ids.len();
                    for s_idx in 0..slog_len {
                        if s_idx < old_slog_len {
                            r.read_str_into(&mut dm.state_log_ids[s_idx])?;
                        } else {
                            dm.state_log_ids.push(r.str()?);
                        }
                    }
                    if old_slog_len > slog_len {
                        dm.state_log_ids.truncate(slog_len);
                    }
                    r.read_opt_str_into(&mut dm.transient_move_follow_up_state_id)?;
                    r.field_map_into(&mut dm.setup_fields)?;
                    r.field_map_into(&mut dm.private_fields)?;
                } else {
                    let current_state_id = r.str()?;
                    let next_move_id = r.opt_str()?;
                    let next_move_can_transition_away = r.bool()?;
                    let performed_first_move = r.bool()?;
                    let slog_len = r.var_u32()? as usize;
                    let mut state_log_ids = SmallVec::with_capacity(slog_len);
                    for _ in 0..slog_len {
                        state_log_ids.push(r.str()?);
                    }
                    let transient_move_follow_up_state_id = r.opt_str()?;
                    let setup_fields = r.field_map()?;
                    let private_fields = r.field_map()?;
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
            for _ in 0..p_len {
                dc.powers.push(PowerInst {
                    handle: r.u32()?,
                    id: decode_power_id(r.u16()?)?,
                    amount: r.i32()?,
                    amount_on_turn_start: r.i32()?,
                    skip_next_duration_tick: r.bool()?,
                    applier_handle: r.opt_u32()?.map(CreatureHandle),
                    ext_index: r.opt_u32()?,
                });
            }
        } else {
            let slot_name = r.opt_str()?;
            let is_player = r.bool()?;
            let pet_owner_player_index = r.opt_u32()?.map(|x| x as usize);
            let monster_id = if r.u8()? == 0 {
                None
            } else {
                Some(decode_monster_id(r.u16()?)?)
            };
            let current_hp = r.i32()?;
            let max_hp = r.i32()?;
            let block = r.i32()?;
            let cumulative_hp_lost = r.i32()?;
            let is_detached = r.bool()?;

            let monster = if r.u8()? == 0 {
                None
            } else {
                let mid = decode_monster_id(r.u16()?)?;
                let rng = r.rng()?;
                let is_performing_move = r.bool()?;
                let spawned_this_turn = r.bool()?;
                let current_state_id = r.str()?;
                let next_move_id = r.opt_str()?;
                let next_move_can_transition_away = r.bool()?;
                let performed_first_move = r.bool()?;
                let slog_len = r.var_u32()? as usize;
                let mut state_log_ids = SmallVec::with_capacity(slog_len);
                for _ in 0..slog_len {
                    state_log_ids.push(r.str()?);
                }
                let transient_move_follow_up_state_id = r.opt_str()?;
                let setup_fields = r.field_map()?;
                let private_fields = r.field_map()?;
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
            let mut powers = SmallVec::with_capacity(p_len);
            for _ in 0..p_len {
                powers.push(PowerInst {
                    handle: r.u32()?,
                    id: decode_power_id(r.u16()?)?,
                    amount: r.i32()?,
                    amount_on_turn_start: r.i32()?,
                    skip_next_duration_tick: r.bool()?,
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
            r.field_map_into(&mut dpe.private_fields)?;
            dpe.attack_command_id = r.opt_u64()?;
            dpe.amount_when_attack_started = r.i32()?;
            let imp_len = r.var_u32()? as usize;
            dpe.impaled_arrows.clear();
            dpe.impaled_arrows.reserve(imp_len);
            for _ in 0..imp_len {
                dpe.impaled_arrows.push(r.var_u32()? as usize);
            }
            dpe.hit_counter = r.i32()?;
        } else {
            let private_fields = r.field_map()?;
            let attack_command_id = r.opt_u64()?;
            let amount_when_attack_started = r.i32()?;
            let imp_len = r.var_u32()? as usize;
            let mut impaled_arrows = SmallVec::with_capacity(imp_len);
            for _ in 0..imp_len {
                impaled_arrows.push(r.var_u32()? as usize);
            }
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

    read_creature_handles(&mut r, &mut out.ally_handles)?;
    read_creature_handles(&mut r, &mut out.enemy_handles)?;
    read_creature_handles(&mut r, &mut out.escaped_handles)?;
    read_creature_handles(&mut r, &mut out.removed_handles)?;
    read_creature_handles(&mut r, &mut out.spawned_enemy_handles)?;
    read_creature_handles(&mut r, &mut out.pet_handles)?;

    // Damage history
    let dh_len = r.var_u32()? as usize;
    out.damage_history.clear();
    out.damage_history.reserve(dh_len);
    for _ in 0..dh_len {
        let flags = r.u16()?;
        let props = ValueProp::from_bits_retain(r.u8()?);
        let current_side = decode_combat_side(r.u8()?)?;
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
            smallvec::smallvec![r.var_i32()?]
        } else if (flags & (1 << 11)) != 0 {
            let tn_len = r.var_u32()? as usize;
            let mut v = SmallVec::with_capacity(tn_len);
            for _ in 0..tn_len {
                v.push(r.var_i32()?);
            }
            v
        } else {
            SmallVec::new()
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
    sh.exhausts.clear();
    let ex_len = r.var_u32()? as usize;
    sh.exhausts.reserve(ex_len);
    for _ in 0..ex_len {
        sh.exhausts.push((read_turn_key(&mut r)?, r.var_u32()? as usize));
    }
    sh.card_block_gains.clear();
    let cbg_len = r.var_u32()? as usize;
    sh.card_block_gains.reserve(cbg_len);
    for _ in 0..cbg_len {
        sh.card_block_gains.push((
            read_turn_key(&mut r)?,
            r.var_u32()? as usize,
            r.i32()?,
        ));
    }
    sh.orb_channels.clear();
    let oc_len = r.var_u32()? as usize;
    sh.orb_channels.reserve(oc_len);
    for _ in 0..oc_len {
        sh.orb_channels.push((
            read_turn_key(&mut r)?,
            r.var_u32()? as usize,
            decode_orb_id(r.u8()?)?,
        ));
    }
    sh.energy_spent.clear();
    let es_len = r.var_u32()? as usize;
    sh.energy_spent.reserve(es_len);
    for _ in 0..es_len {
        sh.energy_spent.push((
            read_turn_key(&mut r)?,
            r.var_u32()? as usize,
            r.i32()?,
        ));
    }
    sh.card_drawn.clear();
    let cd_len = r.var_u32()? as usize;
    sh.card_drawn.reserve(cd_len);
    for _ in 0..cd_len {
        sh.card_drawn.push((
            read_turn_key(&mut r)?,
            r.var_u32()? as usize,
            decode_card_type(r.u8()?)?,
            r.bool()?,
        ));
    }
    let ae_len = r.var_u32()? as usize;
    let keep = sh.actor_events.len().min(ae_len);
    for idx in 0..keep {
        r.read_str_into(&mut sh.actor_events[idx].0)?;
        sh.actor_events[idx].1 = read_turn_key(&mut r)?;
        sh.actor_events[idx].2 = r.var_u32()? as usize;
    }
    if ae_len > keep {
        sh.actor_events.reserve(ae_len - keep);
        for _ in keep..ae_len {
            sh.actor_events.push((
                r.str()?,
                read_turn_key(&mut r)?,
                r.var_u32()? as usize,
            ));
        }
    } else {
        sh.actor_events.truncate(ae_len);
    }
    sh.growth_rewards.hand_of_greed = r.i32()?;
    sh.growth_rewards.the_hunt = r.i32()?;
    sh.growth_rewards.feed = r.i32()?;
    sh.growth_rewards.royalties = r.i32()?;
    sh.growth_rewards.alchemize = r.i32()?;
    sh.growth_rewards.genetic_algorithm = r.i32()?;
    sh.growth_rewards.the_scythe = r.i32()?;
    sh.growth_rewards.goopy = r.i32()?;
    sh.growth_rewards.forbidden_grimoire = r.i32()?;
    sh.growth_rewards.mad_science = r.i32()?;
    sh.brightest_flame_max_hp_spent = r.i32()?;

    out.pending = read_pending(&mut r)?;
    out.vakuu_auto_select_depth = r.u8()?;
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
            hand: SmallVec::new(),
            draw: SmallVec::new(),
            discard: SmallVec::new(),
            exhaust: SmallVec::new(),
            play: SmallVec::new(),
            deck: SmallVec::new(),
            potion_slots: SmallVec::new(),
            orb_capacity: 0,
            orbs: SmallVec::new(),
            arrows: SmallVec::new(),
            relics: SmallVec::new(),
            card_or_potion_effect_depth: 0,
        },
        cards: Vec::new(),
        card_extensions: Vec::new(),
        creatures: Vec::new(),
        power_extensions: Vec::new(),
        ally_handles: SmallVec::new(),
        enemy_handles: SmallVec::new(),
        escaped_handles: SmallVec::new(),
        removed_handles: SmallVec::new(),
        spawned_enemy_handles: SmallVec::new(),
        pet_handles: SmallVec::new(),
        damage_history: Vec::new(),
        semantic_history: SemanticHistory::default(),
        pending: None,
        vakuu_auto_select_depth: 0,
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
    w.u32(c_mask);
    if c_mask != 0 {
        if (c_mask & (1 << 0)) != 0 { w.dec(c.stars_gained_this_turn); }
        if (c_mask & (1 << 1)) != 0 { w.i32(c.skill_cards_played_this_turn); }
        if (c_mask & (1 << 2)) != 0 { w.i32(c.attack_cards_played_this_turn); }
        if (c_mask & (1 << 3)) != 0 { w.i32(c.shivs_played_this_turn); }
        if (c_mask & (1 << 4)) != 0 { w.i32(c.cards_discarded_this_turn); }
        if (c_mask & (1 << 5)) != 0 { w.i32(c.cards_played_this_combat); }
        if (c_mask & (1 << 6)) != 0 { w.i32(c.cards_generated_this_combat); }
        if (c_mask & (1 << 7)) != 0 { w.i32(c.sadness_generated_this_combat); }
        if (c_mask & (1 << 8)) != 0 { w.i32(c.cards_drawn_this_combat); }
        if (c_mask & (1 << 9)) != 0 { w.i32(c.cards_played_this_turn); }
        if (c_mask & (1 << 10)) != 0 { w.i32(c.manual_cards_played_this_turn); }
        if (c_mask & (1 << 11)) != 0 { w.i32(c.card_plays_started_this_turn); }
        if (c_mask & (1 << 12)) != 0 { w.i32(c.attack_or_skill_card_plays_started_this_turn); }
        if (c_mask & (1 << 13)) != 0 { w.i32(c.attack_cards_started_this_turn); }
        if (c_mask & (1 << 14)) != 0 { w.i32(c.zero_cost_attacks_started_this_turn); }
        if (c_mask & (1 << 15)) != 0 { w.i32(c.first_in_series_card_plays_started_this_turn); }
        if (c_mask & (1 << 16)) != 0 { w.i32(c.wah_started_this_turn); }
        if (c_mask & (1 << 17)) != 0 { w.i32(c.cho_started_this_turn); }
        if (c_mask & (1 << 18)) != 0 { w.i32(c.ki_started_this_turn); }
        if (c_mask & (1 << 19)) != 0 { w.i32(c.first_wah_play_ordinal_this_turn); }
        if (c_mask & (1 << 20)) != 0 { w.i32(c.first_cho_play_ordinal_this_turn); }
        if (c_mask & (1 << 21)) != 0 { w.i32(c.first_ki_play_ordinal_this_turn); }
        if (c_mask & (1 << 22)) != 0 { w.i32(c.wah_finished_this_turn); }
        if (c_mask & (1 << 23)) != 0 { w.i32(c.cho_finished_this_turn); }
        if (c_mask & (1 << 24)) != 0 { w.i32(c.ki_finished_this_turn); }
        if (c_mask & (1 << 25)) != 0 { w.i32(c.melody_cards_finished_this_turn); }
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
                w.opt_str(&m.next_move_id);
                w.bool(m.next_move_can_transition_away);
                w.bool(m.performed_first_move);
                w.var_u32(m.state_log_ids.len() as u32);
                for s in &m.state_log_ids {
                    w.str(s);
                }
                w.opt_str(&m.transient_move_follow_up_state_id);
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

    w.bool(state.pending.is_some());
}

#[inline]
fn hash_opt_u32(h: &mut crate::hash::StreamingHasher128, v: Option<u32>) {
    match v {
        Some(x) => {
            h.write_u8(1);
            h.write_u32(x);
        }
        None => h.write_u8(0),
    }
}

#[inline]
fn hash_opt_i32(h: &mut crate::hash::StreamingHasher128, v: Option<i32>) {
    match v {
        Some(x) => {
            h.write_u8(1);
            h.write_i32(x);
        }
        None => h.write_u8(0),
    }
}

#[inline]
fn hash_opt_str(h: &mut crate::hash::StreamingHasher128, s: &Option<String>) {
    match s {
        Some(v) => {
            h.write_u8(1);
            h.write_str(v);
        }
        None => h.write_u8(0),
    }
}

#[inline]
fn hash_rng(h: &mut crate::hash::StreamingHasher128, r: &Rng) {
    h.write_i32(r.counter);
    h.write_u64(r.random.s0);
    h.write_u64(r.random.s1);
    h.write_u64(r.random.s2);
    h.write_u64(r.random.s3);
}

#[inline]
fn hash_dec(h: &mut crate::hash::StreamingHasher128, v: Dec) {
    h.write_bytes(&v.as_decimal().serialize());
}

fn hash_field_map(h: &mut crate::hash::StreamingHasher128, map: &BTreeMap<String, FieldValue>) {
    h.write_u32(map.len() as u32);
    if map.is_empty() {
        return;
    }
    for (k, v) in map {
        h.write_str(k);
        match v {
            FieldValue::Int(x) => {
                h.write_u8(0);
                h.write_i32(*x);
            }
            FieldValue::Decimal(d) => {
                h.write_u8(1);
                hash_dec(h, *d);
            }
            FieldValue::Bool(b) => {
                h.write_u8(2);
                h.write_bool(*b);
            }
            FieldValue::String(s) => {
                h.write_u8(3);
                h.write_str(s);
            }
            FieldValue::NullableInt(x) => {
                h.write_u8(4);
                hash_opt_i32(h, *x);
            }
            FieldValue::CardHandle(ch) => {
                h.write_u8(5);
                h.write_i32(*ch);
            }
            FieldValue::CreatureHandle(ch) => {
                h.write_u8(6);
                h.write_i32(*ch);
            }
            FieldValue::IntArray(arr) => {
                h.write_u8(7);
                h.write_u32(arr.len() as u32);
                for &x in arr {
                    h.write_i32(x);
                }
            }
            FieldValue::CardHandleArray(arr) => {
                h.write_u8(8);
                h.write_u32(arr.len() as u32);
                for &x in arr {
                    h.write_i32(x);
                }
            }
            FieldValue::CreatureHandleArray(arr) => {
                h.write_u8(9);
                h.write_u32(arr.len() as u32);
                for &x in arr {
                    h.write_i32(x);
                }
            }
            FieldValue::CardIntMap(arr) => {
                h.write_u8(10);
                h.write_u32(arr.len() as u32);
                for &(a, b) in arr {
                    h.write_i32(a);
                    h.write_i32(b);
                }
            }
            FieldValue::CreatureDecimalMap(arr) => {
                h.write_u8(11);
                h.write_u32(arr.len() as u32);
                for &(a, d) in arr {
                    h.write_i32(a);
                    hash_dec(h, d);
                }
            }
            FieldValue::CardPairArray(arr) => {
                h.write_u8(12);
                h.write_u32(arr.len() as u32);
                for &(a, b) in arr {
                    h.write_i32(a);
                    h.write_i32(b);
                }
            }
        }
    }
}

#[inline]
fn hash_canonical_card(
    h: &mut crate::hash::StreamingHasher128,
    state: &BattleState,
    ch: CardHandle,
) {
    let Some(card) = state.card(ch) else {
        h.write_u8(0);
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

    h.write_u8(1);
    h.write_u16(card.id as u16);
    h.write_i32(card.base_energy_cost);
    h.write_u16(card.local_keywords.bits());
    h.write_u16(opt_mask);

    if (opt_mask & (1 << 7)) != 0 {
        h.write_i32(card.upgrade_level);
    }
    if (opt_mask & (1 << 8)) != 0 {
        h.write_i32(card.captured_energy_x_value);
    }
    if (opt_mask & (1 << 9)) != 0 {
        h.write_i32(card.base_replay_count);
    }
    if (opt_mask & (1 << 10)) != 0 {
        h.write_u16(card.keywords_added_from_outside.bits());
    }
    if (opt_mask & (1 << 11)) != 0 {
        h.write_u16(card.keywords_removed_from_outside.bits());
    }
    if (opt_mask & (1 << 12)) != 0 {
        h.write_u16(card.temp_flags.bits());
    }
    if let Some(v) = card.temp_star_cost_override_this_turn {
        h.write_i32(v);
    }
    if let Some(v) = card.temp_cost_override_this_turn {
        h.write_i32(v);
    }
    if let Some(v) = card.temp_cost_override_this_turn_or_until_played {
        h.write_i32(v);
    }
    if let Some(v) = card.temp_cost_override_this_combat {
        h.write_i32(v);
    }
    if let Some(a) = card.affliction {
        h.write_u16(a.id as u16);
        h.write_i32(a.amount);
    }
    if let Some(cid) = card.transform_into_after_play {
        h.write_u16(cid as u16);
    }
    if let Some(ext) = ext_opt {
        h.write_u32(ext.local_energy_modifiers.len() as u32);
        for m in &ext.local_energy_modifiers {
            h.write_u8(m.kind as u8);
            h.write_i32(m.amount);
            h.write_bool(m.is_reduce_only);
        }
        match &ext.enchantment {
            Some(e) => {
                h.write_u8(1);
                h.write_u16(e.id as u16);
                h.write_i32(e.amount);
                h.write_u8(e.status as u8);
                hash_field_map(h, &e.private_fields);
            }
            None => h.write_u8(0),
        }
        hash_field_map(h, &ext.private_fields);
    }
}

#[inline]
fn hash_player_counters(h: &mut crate::hash::StreamingHasher128, c: &PlayerCounters) {
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
    h.write_u32(c_mask);
    if c_mask != 0 {
        if (c_mask & (1 << 0)) != 0 { hash_dec(h, c.stars_gained_this_turn); }
        if (c_mask & (1 << 1)) != 0 { h.write_i32(c.skill_cards_played_this_turn); }
        if (c_mask & (1 << 2)) != 0 { h.write_i32(c.attack_cards_played_this_turn); }
        if (c_mask & (1 << 3)) != 0 { h.write_i32(c.shivs_played_this_turn); }
        if (c_mask & (1 << 4)) != 0 { h.write_i32(c.cards_discarded_this_turn); }
        if (c_mask & (1 << 5)) != 0 { h.write_i32(c.cards_played_this_combat); }
        if (c_mask & (1 << 6)) != 0 { h.write_i32(c.cards_generated_this_combat); }
        if (c_mask & (1 << 7)) != 0 { h.write_i32(c.sadness_generated_this_combat); }
        if (c_mask & (1 << 8)) != 0 { h.write_i32(c.cards_drawn_this_combat); }
        if (c_mask & (1 << 9)) != 0 { h.write_i32(c.cards_played_this_turn); }
        if (c_mask & (1 << 10)) != 0 { h.write_i32(c.manual_cards_played_this_turn); }
        if (c_mask & (1 << 11)) != 0 { h.write_i32(c.card_plays_started_this_turn); }
        if (c_mask & (1 << 12)) != 0 { h.write_i32(c.attack_or_skill_card_plays_started_this_turn); }
        if (c_mask & (1 << 13)) != 0 { h.write_i32(c.attack_cards_started_this_turn); }
        if (c_mask & (1 << 14)) != 0 { h.write_i32(c.zero_cost_attacks_started_this_turn); }
        if (c_mask & (1 << 15)) != 0 { h.write_i32(c.first_in_series_card_plays_started_this_turn); }
        if (c_mask & (1 << 16)) != 0 { h.write_i32(c.wah_started_this_turn); }
        if (c_mask & (1 << 17)) != 0 { h.write_i32(c.cho_started_this_turn); }
        if (c_mask & (1 << 18)) != 0 { h.write_i32(c.ki_started_this_turn); }
        if (c_mask & (1 << 19)) != 0 { h.write_i32(c.first_wah_play_ordinal_this_turn); }
        if (c_mask & (1 << 20)) != 0 { h.write_i32(c.first_cho_play_ordinal_this_turn); }
        if (c_mask & (1 << 21)) != 0 { h.write_i32(c.first_ki_play_ordinal_this_turn); }
        if (c_mask & (1 << 22)) != 0 { h.write_i32(c.wah_finished_this_turn); }
        if (c_mask & (1 << 23)) != 0 { h.write_i32(c.cho_finished_this_turn); }
        if (c_mask & (1 << 24)) != 0 { h.write_i32(c.ki_finished_this_turn); }
        if (c_mask & (1 << 25)) != 0 { h.write_i32(c.melody_cards_finished_this_turn); }
    }
}

/// Zero-allocation streaming 128-bit canonical equivalence hash matching `pack_canonical_key` semantics.
pub fn hash_canonical_key(state: &BattleState) -> crate::hash::StateHash {
    let mut h = crate::hash::StreamingHasher128::new();

    h.write_u8(state.current_side as u8);
    h.write_i32(state.round_number);
    h.write_bool(state.is_in_progress);
    h.write_bool(state.won);
    h.write_bool(state.is_player_extra_turn);
    h.write_bool(state.gold_was_stolen);
    h.write_bool(state.end_player_turn_requested);

    for stream in &state.run_rngs.streams {
        hash_rng(&mut h, stream);
    }
    hash_rng(&mut h, &state.player_rngs.streams[3]);

    let p = &state.player;
    h.write_i32(p.current_hp);
    h.write_i32(p.max_hp);
    h.write_i32(p.block);
    h.write_i32(p.cumulative_hp_lost);
    h.write_i32(p.gold);
    h.write_i32(p.max_energy);
    h.write_i32(p.energy);
    h.write_i32(p.stars);
    h.write_u8(p.phase as u8);
    h.write_i32(p.turn_number);
    h.write_i32(p.orb_capacity);

    hash_player_counters(&mut h, &p.counters);

    for pile in [&p.hand[..], &p.draw[..], &p.discard[..], &p.exhaust[..], &p.play[..]] {
        h.write_u32(pile.len() as u32);
        for &ch in pile {
            hash_canonical_card(&mut h, state, ch);
        }
    }

    h.write_u32(p.potion_slots.len() as u32);
    for &slot in &p.potion_slots {
        match slot {
            Some(pid) => {
                h.write_u8(1);
                h.write_u16(pid as u16);
            }
            None => h.write_u8(0),
        }
    }

    h.write_u32(p.orbs.len() as u32);
    for orb in &p.orbs {
        h.write_u8(orb.id as u8);
        hash_opt_i32(&mut h, orb.raw_evoke_value.and_then(|d| d.to_i32_trunc().ok()));
        hash_opt_i32(&mut h, orb.raw_passive_value.and_then(|d| d.to_i32_trunc().ok()));
    }

    h.write_u32(p.arrows.len() as u32);
    for arrow in &p.arrows {
        h.write_u8(arrow.status as u8);
        h.write_bool(arrow.destroy_after_impaled);
    }

    h.write_u32(p.relics.len() as u32);
    for relic in &p.relics {
        h.write_u16(relic.id as u16);
        h.write_bool(relic.is_wax);
        h.write_bool(relic.is_melted);
        h.write_i32(relic.stack_count);
        hash_field_map(&mut h, &relic.private_fields);
    }

    h.write_u32(state.creatures.len() as u32);
    for cr in &state.creatures {
        hash_opt_u32(&mut h, cr.combat_id);
        h.write_u8(cr.side as u8);
        h.write_bool(cr.is_player);
        h.write_bool(cr.is_detached);
        h.write_bool(state.contains_creature(cr.handle));
        h.write_i32(cr.current_hp);
        h.write_i32(cr.max_hp);
        h.write_i32(cr.block);
        h.write_i32(cr.cumulative_hp_lost);
        match &cr.monster {
            Some(m) => {
                h.write_u8(1);
                h.write_u16(m.monster_id as u16);
                hash_rng(&mut h, &m.rng);
                h.write_bool(m.spawned_this_turn);
                h.write_str(&m.current_state_id);
                hash_opt_str(&mut h, &m.next_move_id);
                h.write_bool(m.next_move_can_transition_away);
                h.write_bool(m.performed_first_move);
                h.write_u32(m.state_log_ids.len() as u32);
                for s in &m.state_log_ids {
                    h.write_str(s);
                }
                hash_opt_str(&mut h, &m.transient_move_follow_up_state_id);
                hash_field_map(&mut h, &m.private_fields);
            }
            None => h.write_u8(0),
        }
        h.write_u32(cr.powers.len() as u32);
        for pw in &cr.powers {
            h.write_u16(pw.id as u16);
            h.write_i32(pw.amount);
            h.write_i32(pw.amount_on_turn_start);
            h.write_bool(pw.skip_next_duration_tick);
            if let Some(ext_idx) = pw.ext_index {
                if let Some(pe) = state.power_extensions.get(ext_idx as usize) {
                    h.write_u8(1);
                    hash_field_map(&mut h, &pe.private_fields);
                    h.write_i32(pe.hit_counter);
                    h.write_u32(pe.impaled_arrows.len() as u32);
                    for &ia in &pe.impaled_arrows {
                        h.write_u32(ia as u32);
                    }
                } else {
                    h.write_u8(0);
                }
            } else {
                h.write_u8(0);
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
    h.write_u16(g_mask);
    if g_mask != 0 {
        if (g_mask & (1 << 0)) != 0 { h.write_i32(gr.hand_of_greed); }
        if (g_mask & (1 << 1)) != 0 { h.write_i32(gr.the_hunt); }
        if (g_mask & (1 << 2)) != 0 { h.write_i32(gr.feed); }
        if (g_mask & (1 << 3)) != 0 { h.write_i32(gr.royalties); }
        if (g_mask & (1 << 4)) != 0 { h.write_i32(gr.alchemize); }
        if (g_mask & (1 << 5)) != 0 { h.write_i32(gr.genetic_algorithm); }
        if (g_mask & (1 << 6)) != 0 { h.write_i32(gr.the_scythe); }
        if (g_mask & (1 << 7)) != 0 { h.write_i32(gr.goopy); }
        if (g_mask & (1 << 8)) != 0 { h.write_i32(gr.forbidden_grimoire); }
        if (g_mask & (1 << 9)) != 0 { h.write_i32(gr.mad_science); }
    }
    h.write_i32(state.semantic_history.brightest_flame_max_hp_spent);

    let cur_tk = state.current_turn_key();
    let sh = &state.semantic_history;
    h.write_u32(sh.exhausts.len() as u32);
    h.write_u32(sh.exhausts.iter().filter(|(tk, _)| *tk == cur_tk).count() as u32);
    h.write_u32(sh.card_block_gains.len() as u32);
    h.write_u32(
        sh.card_block_gains
            .iter()
            .filter(|(tk, _, _)| *tk == cur_tk)
            .count() as u32,
    );
    h.write_u32(sh.orb_channels.len() as u32);
    h.write_u32(
        sh.orb_channels
            .iter()
            .filter(|(tk, _, _)| *tk == cur_tk)
            .count() as u32,
    );
    h.write_u32(sh.energy_spent.len() as u32);
    let cur_energy_spent: i32 = sh
        .energy_spent
        .iter()
        .filter(|(tk, _, _)| *tk == cur_tk)
        .map(|(_, _, amt)| *amt)
        .sum();
    h.write_i32(cur_energy_spent);
    h.write_u32(sh.card_drawn.len() as u32);
    h.write_u32(
        sh.card_drawn
            .iter()
            .filter(|(tk, _, _, _)| *tk == cur_tk)
            .count() as u32,
    );
    h.write_u32(sh.actor_events.len() as u32);
    for (kind, tk, idx) in &sh.actor_events {
        if *tk == cur_tk {
            h.write_str(kind);
            h.write_u32(*idx as u32);
        }
    }

    let dh = &state.damage_history;
    let mut player_unblocked_hits = 0u32;
    let mut player_turn_damage_count = 0u32;
    let mut prev_round_player_hp_loss = false;
    let pc_handle = state.player.creature_handle;
    for e in dh {
        if e.receiver_creature_handle == pc_handle && e.unblocked_damage > 0 {
            player_unblocked_hits += 1;
            if e.round_number == state.round_number - 1 {
                prev_round_player_hp_loss = true;
            }
        }
        if e.round_number == state.round_number && e.current_side == state.current_side {
            player_turn_damage_count += 1;
        }
    }
    h.write_u32(dh.len() as u32);
    h.write_u32(player_unblocked_hits);
    h.write_u32(player_turn_damage_count);
    h.write_bool(prev_round_player_hp_loss);

    h.write_bool(state.pending.is_some());
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
            transformation_pools: Vec::new(),
            map_col: 0,
            map_row: 0,
            act_index: 0,
            total_floor: 1,
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
            player_turn_numbers: smallvec::smallvec![1],
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
            player_turn_numbers: smallvec::smallvec![1, 2, -3],
        });
        s1.semantic_history.actor_events = vec![
            (
                "VeryLongEventNameToEnsureCapacityReuseIsTested".to_owned(),
                TurnKey {
                    round_number: 1,
                    side: CombatSide::Player,
                    player_turn_numbers: smallvec::smallvec![Some(1)],
                },
                3,
            ),
            (
                "SecondEvent".to_owned(),
                TurnKey {
                    round_number: 1,
                    side: CombatSide::Enemy,
                    player_turn_numbers: SmallVec::new(),
                },
                9,
            ),
        ];

        let mut s2 = empty_scratch_state(s1.static_ctx.clone());
        s2.run_rngs = s1.run_rngs.clone();
        s2.semantic_history.actor_events = vec![(
            "Short".to_owned(),
            TurnKey {
                round_number: 1,
                side: CombatSide::Player,
                player_turn_numbers: smallvec::smallvec![None, Some(2)],
            },
            0,
        )];

        let mut b1 = Vec::new();
        pack_state(&s1, &mut b1);
        let mut b2 = Vec::new();
        pack_state(&s2, &mut b2);

        let mut out = empty_scratch_state(s1.static_ctx.clone());
        unpack_state(&b1, s1.static_ctx.clone(), &mut out).unwrap();
        assert_eq!(out, s1);

        unpack_state(&b2, s1.static_ctx.clone(), &mut out).unwrap();
        assert_eq!(out, s2);

        unpack_state(&b1, s1.static_ctx.clone(), &mut out).unwrap();
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
            player_turn_numbers: smallvec::smallvec![s1.player.turn_number],
        });
        assert_ne!(h1, hash_canonical_key(&s_dmg));
    }
}
