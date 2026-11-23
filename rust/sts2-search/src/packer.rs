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
        let len = self.var_u32()? as usize;
        let bytes = self.take(len)?;
        std::str::from_utf8(bytes)
            .map(str::to_owned)
            .map_err(|_| self.err("invalid utf8"))
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
        let len = self.var_u32()? as usize;
        let mut map = BTreeMap::new();
        for _ in 0..len {
            let k = self.str()?;
            let v = self.field_value()?;
            map.insert(k, v);
        }
        Ok(map)
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

    // PlayerCounters (26 fields)
    let c = &p.counters;
    w.dec(c.stars_gained_this_turn);
    w.i32(c.skill_cards_played_this_turn);
    w.i32(c.attack_cards_played_this_turn);
    w.i32(c.shivs_played_this_turn);
    w.i32(c.cards_discarded_this_turn);
    w.i32(c.cards_played_this_combat);
    w.i32(c.cards_generated_this_combat);
    w.i32(c.sadness_generated_this_combat);
    w.i32(c.cards_drawn_this_combat);
    w.i32(c.cards_played_this_turn);
    w.i32(c.manual_cards_played_this_turn);
    w.i32(c.card_plays_started_this_turn);
    w.i32(c.attack_or_skill_card_plays_started_this_turn);
    w.i32(c.attack_cards_started_this_turn);
    w.i32(c.zero_cost_attacks_started_this_turn);
    w.i32(c.first_in_series_card_plays_started_this_turn);
    w.i32(c.wah_started_this_turn);
    w.i32(c.cho_started_this_turn);
    w.i32(c.ki_started_this_turn);
    w.i32(c.first_wah_play_ordinal_this_turn);
    w.i32(c.first_cho_play_ordinal_this_turn);
    w.i32(c.first_ki_play_ordinal_this_turn);
    w.i32(c.wah_finished_this_turn);
    w.i32(c.cho_finished_this_turn);
    w.i32(c.ki_finished_this_turn);
    w.i32(c.melody_cards_finished_this_turn);

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

    // Cards
    w.var_u32(state.cards.len() as u32);
    for card in &state.cards {
        w.u32(card.handle.0);
        w.u16(card.id as u16);
        w.i32(card.upgrade_level);
        w.i32(card.canonical_base_energy_cost);
        w.i32(card.base_energy_cost);
        w.i32(card.captured_energy_x_value);
        w.i32(card.base_replay_count);
        w.opt_i32(card.floor_added_to_deck);
        w.opt_u32(card.deck_card_handle.map(|h| h.0));
        w.opt_u32(card.clone_of_handle.map(|h| h.0));
        w.opt_i32(card.temp_star_cost_override_this_turn);
        w.opt_i32(card.temp_cost_override_this_turn);
        w.opt_i32(card.temp_cost_override_this_turn_or_until_played);
        w.opt_i32(card.temp_cost_override_this_combat);
        w.u16(card.local_keywords.bits());
        w.u16(card.keywords_added_from_outside.bits());
        w.u16(card.keywords_removed_from_outside.bits());
        w.u16(card.temp_flags.bits());
        match card.affliction {
            Some(aff) => {
                w.u8(1);
                w.u16(aff.id as u16);
                w.i32(aff.amount);
            }
            None => w.u8(0),
        }
        w.opt_u32(card.ext_index);
        w.u8(card.pile as u8);
        w.bool(card.removed_from_state);
        match card.transform_into_after_play {
            Some(cid) => {
                w.u8(1);
                w.u16(cid as u16);
            }
            None => w.u8(0),
        }
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
        w.u32(dh.receiver_creature_handle.0);
        w.opt_u32(dh.dealer_creature_handle.map(|h| h.0));
        w.u32(dh.original_target_creature_handle.0);
        w.u8(dh.props.bits());
        w.i32(dh.intended_hp_loss);
        w.i32(dh.blocked_damage);
        w.i32(dh.unblocked_damage);
        w.i32(dh.overkill_damage);
        w.bool(dh.was_target_killed);
        w.bool(dh.was_block_broken);
        w.bool(dh.was_fully_blocked);
        w.opt_u32(dh.card_source_handle.map(|h| h.0));
        w.i32(dh.round_number);
        w.u8(dh.current_side as u8);
        w.var_u32(dh.player_turn_numbers.len() as u32);
        for &tn in &dh.player_turn_numbers {
            w.i32(tn);
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

    let run_string_seed = r.str()?;
    let run_seed = r.u64()?;
    let mut run_streams = [Rng::from_seed(0); 12];
    for s in &mut run_streams {
        *s = r.rng()?;
    }
    out.run_rngs = RunRngSet {
        string_seed: run_string_seed,
        seed: run_seed,
        streams: run_streams,
    };

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

    p.counters = PlayerCounters {
        stars_gained_this_turn: r.dec()?,
        skill_cards_played_this_turn: r.i32()?,
        attack_cards_played_this_turn: r.i32()?,
        shivs_played_this_turn: r.i32()?,
        cards_discarded_this_turn: r.i32()?,
        cards_played_this_combat: r.i32()?,
        cards_generated_this_combat: r.i32()?,
        sadness_generated_this_combat: r.i32()?,
        cards_drawn_this_combat: r.i32()?,
        cards_played_this_turn: r.i32()?,
        manual_cards_played_this_turn: r.i32()?,
        card_plays_started_this_turn: r.i32()?,
        attack_or_skill_card_plays_started_this_turn: r.i32()?,
        attack_cards_started_this_turn: r.i32()?,
        zero_cost_attacks_started_this_turn: r.i32()?,
        first_in_series_card_plays_started_this_turn: r.i32()?,
        wah_started_this_turn: r.i32()?,
        cho_started_this_turn: r.i32()?,
        ki_started_this_turn: r.i32()?,
        first_wah_play_ordinal_this_turn: r.i32()?,
        first_cho_play_ordinal_this_turn: r.i32()?,
        first_ki_play_ordinal_this_turn: r.i32()?,
        wah_finished_this_turn: r.i32()?,
        cho_finished_this_turn: r.i32()?,
        ki_finished_this_turn: r.i32()?,
        melody_cards_finished_this_turn: r.i32()?,
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
    p.relics.clear();
    p.relics.reserve(relic_len);
    for _ in 0..relic_len {
        p.relics.push(RelicInst {
            id: decode_relic_id(r.u16()?)?,
            is_wax: r.bool()?,
            is_melted: r.bool()?,
            stack_count: r.i32()?,
            floor_added_to_deck: r.opt_i32()?,
            private_fields: r.field_map()?,
        });
    }

    // Cards
    let cards_len = r.var_u32()? as usize;
    out.cards.clear();
    out.cards.reserve(cards_len);
    for _ in 0..cards_len {
        let handle = CardHandle(r.u32()?);
        let id = decode_card_id(r.u16()?)?;
        let upgrade_level = r.i32()?;
        let canonical_base_energy_cost = r.i32()?;
        let base_energy_cost = r.i32()?;
        let captured_energy_x_value = r.i32()?;
        let base_replay_count = r.i32()?;
        let floor_added_to_deck = r.opt_i32()?;
        let deck_card_handle = r.opt_u32()?.map(CardHandle);
        let clone_of_handle = r.opt_u32()?.map(CardHandle);
        let temp_star_cost_override_this_turn = r.opt_i32()?;
        let temp_cost_override_this_turn = r.opt_i32()?;
        let temp_cost_override_this_turn_or_until_played = r.opt_i32()?;
        let temp_cost_override_this_combat = r.opt_i32()?;
        let local_keywords = CardKeywords::from_bits_retain(r.u16()?);
        let keywords_added_from_outside = CardKeywords::from_bits_retain(r.u16()?);
        let keywords_removed_from_outside = CardKeywords::from_bits_retain(r.u16()?);
        let temp_flags = CardTempFlags::from_bits_retain(r.u16()?);
        let affliction = if r.u8()? == 0 {
            None
        } else {
            Some(AfflictionInst {
                id: decode_affliction_id(r.u16()?)?,
                amount: r.i32()?,
            })
        };
        let ext_index = r.opt_u32()?;
        let pile = decode_pile_type(r.u8()?)?;
        let removed_from_state = r.bool()?;
        let transform_into_after_play = if r.u8()? == 0 {
            None
        } else {
            Some(decode_card_id(r.u16()?)?)
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

    // CardExtensions
    let ext_len = r.var_u32()? as usize;
    out.card_extensions.clear();
    out.card_extensions.reserve(ext_len);
    for _ in 0..ext_len {
        let mlen = r.var_u32()? as usize;
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

    // Creatures
    let cr_len = r.var_u32()? as usize;
    out.creatures.clear();
    out.creatures.reserve(cr_len);
    for _ in 0..cr_len {
        let handle = CreatureHandle(r.u32()?);
        let combat_id = r.opt_u32()?;
        let side = decode_combat_side(r.u8()?)?;
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

    // PowerExtensions
    let pe_len = r.var_u32()? as usize;
    out.power_extensions.clear();
    out.power_extensions.reserve(pe_len);
    for _ in 0..pe_len {
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
        let receiver_creature_handle = CreatureHandle(r.u32()?);
        let dealer_creature_handle = r.opt_u32()?.map(CreatureHandle);
        let original_target_creature_handle = CreatureHandle(r.u32()?);
        let props = ValueProp::from_bits_retain(r.u8()?);
        let intended_hp_loss = r.i32()?;
        let blocked_damage = r.i32()?;
        let unblocked_damage = r.i32()?;
        let overkill_damage = r.i32()?;
        let was_target_killed = r.bool()?;
        let was_block_broken = r.bool()?;
        let was_fully_blocked = r.bool()?;
        let card_source_handle = r.opt_u32()?.map(CardHandle);
        let round_number = r.i32()?;
        let current_side = decode_combat_side(r.u8()?)?;
        let tn_len = r.var_u32()? as usize;
        let mut player_turn_numbers = SmallVec::with_capacity(tn_len);
        for _ in 0..tn_len {
            player_turn_numbers.push(r.i32()?);
        }
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
    sh.actor_events.clear();
    let ae_len = r.var_u32()? as usize;
    sh.actor_events.reserve(ae_len);
    for _ in 0..ae_len {
        sh.actor_events.push((
            r.str()?,
            read_turn_key(&mut r)?,
            r.var_u32()? as usize,
        ));
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
    w.u8(1);
    w.u16(card.id as u16);
    w.i32(card.upgrade_level);
    w.i32(card.base_energy_cost);
    w.i32(card.captured_energy_x_value);
    w.i32(card.base_replay_count);
    w.opt_i32(card.temp_star_cost_override_this_turn);
    w.opt_i32(card.temp_cost_override_this_turn);
    w.opt_i32(card.temp_cost_override_this_turn_or_until_played);
    w.opt_i32(card.temp_cost_override_this_combat);
    w.u16(card.local_keywords.bits());
    w.u16(card.keywords_added_from_outside.bits());
    w.u16(card.keywords_removed_from_outside.bits());
    w.u16(card.temp_flags.bits());
    match card.affliction {
        Some(a) => {
            w.u8(1);
            w.u16(a.id as u16);
            w.i32(a.amount);
        }
        None => w.u8(0),
    }
    match card.transform_into_after_play {
        Some(cid) => {
            w.u8(1);
            w.u16(cid as u16);
        }
        None => w.u8(0),
    }
    if let Some(ext) = state.card_ext(card.ext_index) {
        w.u8(1);
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
    } else {
        w.u8(0);
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

    let c = &p.counters;
    w.dec(c.stars_gained_this_turn);
    w.i32(c.skill_cards_played_this_turn);
    w.i32(c.attack_cards_played_this_turn);
    w.i32(c.shivs_played_this_turn);
    w.i32(c.cards_discarded_this_turn);
    w.i32(c.cards_played_this_combat);
    w.i32(c.cards_generated_this_combat);
    w.i32(c.sadness_generated_this_combat);
    w.i32(c.cards_drawn_this_combat);
    w.i32(c.cards_played_this_turn);
    w.i32(c.manual_cards_played_this_turn);
    w.i32(c.card_plays_started_this_turn);
    w.i32(c.attack_or_skill_card_plays_started_this_turn);
    w.i32(c.attack_cards_started_this_turn);
    w.i32(c.zero_cost_attacks_started_this_turn);
    w.i32(c.first_in_series_card_plays_started_this_turn);
    w.i32(c.wah_started_this_turn);
    w.i32(c.cho_started_this_turn);
    w.i32(c.ki_started_this_turn);
    w.i32(c.first_wah_play_ordinal_this_turn);
    w.i32(c.first_cho_play_ordinal_this_turn);
    w.i32(c.first_ki_play_ordinal_this_turn);
    w.i32(c.wah_finished_this_turn);
    w.i32(c.cho_finished_this_turn);
    w.i32(c.ki_finished_this_turn);
    w.i32(c.melody_cards_finished_this_turn);

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
    w.i32(state.semantic_history.brightest_flame_max_hp_spent);

    w.bool(state.pending.is_some());
}
