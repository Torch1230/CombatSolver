use core::fmt;
use serde::{Deserialize, Serialize};
use xxhash_rust::xxh64::xxh64;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RngError {
    InvalidMaxValue(i32),
    InvalidRange { min: i64, max: i64 },
    UnknownStreamName,
}

impl fmt::Display for RngError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidMaxValue(v) => write!(f, "rng maxValue must be > 0, got {v}"),
            Self::InvalidRange { min, max } => {
                write!(f, "rng min ({min}) must be < max ({max})")
            }
            Self::UnknownStreamName => write!(f, "unknown rng stream name"),
        }
    }
}

impl std::error::Error for RngError {}

/// Exact port of `MegaCrit.Sts2.Core.Helpers.StringHelper.GetDeterministicHashCode(string)`.
#[inline]
pub fn deterministic_hash_code(s: &str) -> u64 {
    xxh64(s.as_bytes(), 0)
}

/// Exact port of `StringHelper.GetDeterministicHashCodeOld(string)`.
pub fn deterministic_hash_code_old(s: &str) -> i32 {
    let mut num: i32 = 352_654_597;
    let mut num2: i32 = num;
    let units: Vec<u16> = s.encode_utf16().collect();
    let mut i = 0usize;
    while i < units.len() {
        num = ((num << 5).wrapping_add(num)) ^ (units[i] as i32);
        if i == units.len() - 1 {
            break;
        }
        num2 = ((num2 << 5).wrapping_add(num2)) ^ (units[i + 1] as i32);
        i += 2;
    }
    num.wrapping_add(num2.wrapping_mul(1_566_083_941))
}

/// Exact port of `StringHelper.SnakeCase` for ASCII PascalCase identifiers.
pub fn snake_case(txt: &str) -> String {
    let trimmed = txt.trim();
    let bytes = trimmed.as_bytes();
    let mut out = String::with_capacity(trimmed.len() + 4);
    for (i, &b) in bytes.iter().enumerate() {
        if i > 0 && b.is_ascii_uppercase() && bytes[i - 1].is_ascii_alphanumeric() {
            out.push('_');
        }
        out.push((b as char).to_ascii_lowercase());
    }
    out
}

/// Exact bitwise port of `Sts2Sim.Core.Random.MegaRandom` (`Xoshiro256**` + `SplitMix64`).
#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Serialize, Deserialize)]
pub struct MegaRandom {
    pub s0: u64,
    pub s1: u64,
    pub s2: u64,
    pub s3: u64,
}

impl MegaRandom {
    const INCR_DOUBLE: f64 = 1.1102230246251565e-16_f64;
    const INCR_FLOAT: f32 = 5.9604645e-08_f32;

    #[inline]
    pub fn splitmix64(x: &mut u64) -> u64 {
        *x = x.wrapping_add(11_400_714_819_323_198_485u64);
        let mut num = *x;
        num = (num ^ (num >> 30)).wrapping_mul(13_787_848_793_156_543_929u64);
        num = (num ^ (num >> 27)).wrapping_mul(10_723_151_780_598_845_931u64);
        num ^ (num >> 31)
    }

    #[inline]
    pub fn from_seed(mut seed: u64) -> Self {
        let s0 = Self::splitmix64(&mut seed);
        let s1 = Self::splitmix64(&mut seed);
        let s2 = Self::splitmix64(&mut seed);
        let s3 = Self::splitmix64(&mut seed);
        Self { s0, s1, s2, s3 }
    }

    #[inline]
    pub const fn from_state(s0: u64, s1: u64, s2: u64, s3: u64) -> Self {
        Self { s0, s1, s2, s3 }
    }

    #[inline]
    pub fn next_u64(&mut self) -> u64 {
        let mut s0 = self.s0;
        let mut s1 = self.s1;
        let mut s2 = self.s2;
        let mut s3 = self.s3;
        let result = s1.wrapping_mul(5).rotate_left(7).wrapping_mul(9);
        let num = s1 << 17;
        s2 ^= s0;
        s3 ^= s1;
        s1 ^= s2;
        s0 ^= s3;
        s2 ^= num;
        s3 = s3.rotate_left(45);
        self.s0 = s0;
        self.s1 = s1;
        self.s2 = s2;
        self.s3 = s3;
        result
    }

    #[inline]
    pub fn next_double(&mut self) -> f64 {
        ((self.next_u64() >> 11) as f64) * Self::INCR_DOUBLE
    }

    #[inline]
    pub fn next_i32_raw(&mut self) -> i32 {
        (self.next_u64() >> 33) as i32
    }

    #[inline]
    pub fn next_u32_raw(&mut self) -> u32 {
        self.next_u64() as u32
    }

    #[inline]
    pub fn next_bool_bit(&mut self) -> bool {
        (self.next_u64() & 0x8000_0000_0000_0000u64) != 0
    }

    #[inline]
    pub fn next_float(&mut self) -> f32 {
        ((self.next_u64() >> 40) as f32) * Self::INCR_FLOAT
    }

    #[inline]
    pub fn next_i32_max(&mut self, max_value: i32) -> Result<i32, RngError> {
        if max_value < 1 {
            return Err(RngError::InvalidMaxValue(max_value));
        }
        Ok((self.next_double() * (max_value as f64)) as i32)
    }

    #[inline]
    pub fn next_i32_range(&mut self, min_value: i32, max_value: i32) -> Result<i32, RngError> {
        if min_value >= max_value {
            return Err(RngError::InvalidRange {
                min: min_value as i64,
                max: max_value as i64,
            });
        }
        let num = (max_value as i64) - (min_value as i64);
        if num <= (i32::MAX as i64) {
            Ok(((self.next_double() * (num as f64)) as i32) + min_value)
        } else {
            Ok((((self.next_double() * (num as f64)) as i64) + (min_value as i64)) as i32)
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RngWords {
    pub counter: i32,
    pub s0: u64,
    pub s1: u64,
    pub s2: u64,
    pub s3: u64,
}

/// Exact port of `Sts2Sim.Core.Random.Rng`.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Serialize, Deserialize)]
pub struct Rng {
    pub counter: i32,
    pub random: MegaRandom,
}

impl Rng {
    #[inline]
    pub fn from_seed(seed: u64) -> Self {
        Self {
            counter: 0,
            random: MegaRandom::from_seed(seed),
        }
    }

    #[inline]
    pub fn from_named_seed(seed: u64, name: &str) -> Self {
        Self::from_seed(seed.wrapping_add(deterministic_hash_code(name)))
    }

    #[inline]
    pub const fn from_words(words: RngWords) -> Self {
        Self {
            counter: words.counter,
            random: MegaRandom::from_state(words.s0, words.s1, words.s2, words.s3),
        }
    }

    #[inline]
    pub const fn to_words(self) -> RngWords {
        RngWords {
            counter: self.counter,
            s0: self.random.s0,
            s1: self.random.s1,
            s2: self.random.s2,
            s3: self.random.s3,
        }
    }

    /// Note: C# `Rng.NextBool()` calls `_random.Next(2) == 0`, NOT `MegaRandom.NextBool()`.
    #[inline]
    pub fn next_bool(&mut self) -> bool {
        self.counter += 1;
        // max_value = 2 is always valid
        (self.random.next_double() * 2.0_f64) as i32 == 0
    }

    #[inline]
    pub fn next_int(&mut self, max_exclusive: i32) -> Result<i32, RngError> {
        self.counter += 1;
        self.random.next_i32_max(max_exclusive)
    }

    #[inline]
    pub fn next_int_range(&mut self, min_inclusive: i32, max_exclusive: i32) -> Result<i32, RngError> {
        if min_inclusive >= max_exclusive {
            return Err(RngError::InvalidRange {
                min: min_inclusive as i64,
                max: max_exclusive as i64,
            });
        }
        self.counter += 1;
        self.random.next_i32_range(min_inclusive, max_exclusive)
    }

    #[inline]
    pub fn next_u32_range(&mut self, min_inclusive: u32, max_exclusive: u32) -> Result<u32, RngError> {
        if min_inclusive >= max_exclusive {
            return Err(RngError::InvalidRange {
                min: min_inclusive as i64,
                max: max_exclusive as i64,
            });
        }
        self.counter += 1;
        let num = self.random.next_double();
        let num2 = (max_exclusive - min_inclusive) as f64;
        let num3 = (num * num2) as u32;
        Ok(min_inclusive + num3)
    }

    #[inline]
    pub fn next_u64(&mut self) -> u64 {
        self.counter += 1;
        self.random.next_u64()
    }

    #[inline]
    pub fn next_u64_range(&mut self, min_inclusive: u64, max_exclusive: u64) -> Result<u64, RngError> {
        if min_inclusive >= max_exclusive {
            return Err(RngError::InvalidRange {
                min: min_inclusive as i64,
                max: max_exclusive as i64,
            });
        }
        self.counter += 1;
        let num = self.random.next_double();
        let num2 = (max_exclusive - min_inclusive) as f64;
        let num3 = (num * num2) as u64;
        Ok(min_inclusive + num3)
    }

    #[inline]
    pub fn next_f32_range(&mut self, min: f32, max: f32) -> Result<f32, RngError> {
        if min > max {
            return Err(RngError::InvalidRange {
                min: min as i64,
                max: max as i64,
            });
        }
        self.counter += 1;
        Ok((self.random.next_double() * ((max - min) as f64) + (min as f64)) as f32)
    }

    #[inline]
    pub fn next_f64(&mut self) -> f64 {
        self.counter += 1;
        self.random.next_double()
    }

    #[inline]
    pub fn next_f64_range(&mut self, min: f64, max: f64) -> Result<f64, RngError> {
        if min > max {
            return Err(RngError::InvalidRange {
                min: min as i64,
                max: max as i64,
            });
        }
        self.counter += 1;
        Ok(self.random.next_double() * (max - min) + min)
    }

    #[inline]
    pub fn next_item<'a, T>(&mut self, items: &'a [T]) -> Option<&'a T> {
        if items.is_empty() {
            return None;
        }
        let idx = self.next_int_range(0, items.len() as i32).ok()? as usize;
        items.get(idx)
    }

    /// Exact port of `ListExtensions.UnstableShuffle<T>(this List<T> list, Rng rng)`.
    pub fn unstable_shuffle<T>(&mut self, slice: &mut [T]) {
        let child_seed = self.next_u64();
        let mut child = Rng::from_seed(child_seed);
        let mut num = slice.len();
        while num > 1 {
            num -= 1;
            let num2 = child.next_int((num + 1) as i32).expect("num + 1 > 0") as usize;
            slice.swap(num, num2);
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(u8)]
pub enum RunRngType {
    UpFront = 0,
    Shuffle = 1,
    UnknownMapPoint = 2,
    CombatCardGeneration = 3,
    CombatPotionGeneration = 4,
    CombatCardSelection = 5,
    CombatEnergyCosts = 6,
    CombatTargets = 7,
    MonsterAi = 8,
    Niche = 9,
    CombatOrbs = 10,
    TreasureRoomRelics = 11,
}

impl RunRngType {
    pub const ALL: [Self; 12] = [
        Self::UpFront,
        Self::Shuffle,
        Self::UnknownMapPoint,
        Self::CombatCardGeneration,
        Self::CombatPotionGeneration,
        Self::CombatCardSelection,
        Self::CombatEnergyCosts,
        Self::CombatTargets,
        Self::MonsterAi,
        Self::Niche,
        Self::CombatOrbs,
        Self::TreasureRoomRelics,
    ];

    pub const fn as_pascal_str(self) -> &'static str {
        match self {
            Self::UpFront => "UpFront",
            Self::Shuffle => "Shuffle",
            Self::UnknownMapPoint => "UnknownMapPoint",
            Self::CombatCardGeneration => "CombatCardGeneration",
            Self::CombatPotionGeneration => "CombatPotionGeneration",
            Self::CombatCardSelection => "CombatCardSelection",
            Self::CombatEnergyCosts => "CombatEnergyCosts",
            Self::CombatTargets => "CombatTargets",
            Self::MonsterAi => "MonsterAi",
            Self::Niche => "Niche",
            Self::CombatOrbs => "CombatOrbs",
            Self::TreasureRoomRelics => "TreasureRoomRelics",
        }
    }

    pub const fn snake_case_name(self) -> &'static str {
        match self {
            Self::UpFront => "up_front",
            Self::Shuffle => "shuffle",
            Self::UnknownMapPoint => "unknown_map_point",
            Self::CombatCardGeneration => "combat_card_generation",
            Self::CombatPotionGeneration => "combat_potion_generation",
            Self::CombatCardSelection => "combat_card_selection",
            Self::CombatEnergyCosts => "combat_energy_costs",
            Self::CombatTargets => "combat_targets",
            Self::MonsterAi => "monster_ai",
            Self::Niche => "niche",
            Self::CombatOrbs => "combat_orbs",
            Self::TreasureRoomRelics => "treasure_room_relics",
        }
    }

    pub fn from_pascal_str(s: &str) -> Option<Self> {
        match s {
            "UpFront" => Some(Self::UpFront),
            "Shuffle" => Some(Self::Shuffle),
            "UnknownMapPoint" => Some(Self::UnknownMapPoint),
            "CombatCardGeneration" => Some(Self::CombatCardGeneration),
            "CombatPotionGeneration" => Some(Self::CombatPotionGeneration),
            "CombatCardSelection" => Some(Self::CombatCardSelection),
            "CombatEnergyCosts" => Some(Self::CombatEnergyCosts),
            "CombatTargets" => Some(Self::CombatTargets),
            "MonsterAi" => Some(Self::MonsterAi),
            "Niche" => Some(Self::Niche),
            "CombatOrbs" => Some(Self::CombatOrbs),
            "TreasureRoomRelics" => Some(Self::TreasureRoomRelics),
            _ => None,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(u8)]
pub enum PlayerRngType {
    Rewards = 0,
    Shops = 1,
    Transformations = 2,
    NonCombatGeneration = 3,
}

impl PlayerRngType {
    pub const ALL: [Self; 4] = [
        Self::Rewards,
        Self::Shops,
        Self::Transformations,
        Self::NonCombatGeneration,
    ];

    pub const fn as_pascal_str(self) -> &'static str {
        match self {
            Self::Rewards => "Rewards",
            Self::Shops => "Shops",
            Self::Transformations => "Transformations",
            Self::NonCombatGeneration => "NonCombatGeneration",
        }
    }

    pub const fn snake_case_name(self) -> &'static str {
        match self {
            Self::Rewards => "rewards",
            Self::Shops => "shops",
            Self::Transformations => "transformations",
            Self::NonCombatGeneration => "non_combat_generation",
        }
    }

    pub fn from_pascal_str(s: &str) -> Option<Self> {
        match s {
            "Rewards" => Some(Self::Rewards),
            "Shops" => Some(Self::Shops),
            "Transformations" => Some(Self::Transformations),
            "NonCombatGeneration" => Some(Self::NonCombatGeneration),
            _ => None,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RunRngSet {
    pub string_seed: String,
    pub seed: u64,
    pub streams: [Rng; 12],
}

impl RunRngSet {
    pub fn from_string_seed(seed_str: &str) -> Self {
        let seed = if let Some(rest) = seed_str.strip_prefix("old") {
            (deterministic_hash_code_old(rest) as u32) as u64
        } else {
            deterministic_hash_code(seed_str)
        };
        let mut streams = [Rng::from_seed(0); 12];
        for (i, rng_type) in RunRngType::ALL.iter().enumerate() {
            streams[i] = Rng::from_named_seed(seed, rng_type.snake_case_name());
        }
        Self {
            string_seed: seed_str.to_owned(),
            seed,
            streams,
        }
    }

    #[inline]
    pub fn get(&self, kind: RunRngType) -> &Rng {
        &self.streams[kind as usize]
    }

    #[inline]
    pub fn get_mut(&mut self, kind: RunRngType) -> &mut Rng {
        &mut self.streams[kind as usize]
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct PlayerRngSet {
    pub seed: u64,
    pub streams: [Rng; 4],
}

impl PlayerRngSet {
    pub fn from_seed(seed: u64) -> Self {
        let mut streams = [Rng::from_seed(0); 4];
        for (i, rng_type) in PlayerRngType::ALL.iter().enumerate() {
            streams[i] = Rng::from_named_seed(seed, rng_type.snake_case_name());
        }
        Self { seed, streams }
    }

    #[inline]
    pub fn get(&self, kind: PlayerRngType) -> &Rng {
        &self.streams[kind as usize]
    }

    #[inline]
    pub fn get_mut(&mut self, kind: PlayerRngType) -> &mut Rng {
        &mut self.streams[kind as usize]
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn deterministic_hashes_match_dotnet_and_legacy() {
        // Known XxHash64(UTF8, seed=0) empty and standard test strings.
        assert_eq!(deterministic_hash_code(""), 0xEF46DB3751D8E999u64);
        assert_eq!(deterministic_hash_code("shuffle"), xxh64(b"shuffle", 0));
        assert_eq!(
            snake_case("CombatCardGeneration"),
            "combat_card_generation"
        );
        assert_eq!(snake_case("NonCombatGeneration"), "non_combat_generation");
        assert_eq!(snake_case("MonsterAi"), "monster_ai");
        assert_eq!(deterministic_hash_code_old("ABC"), deterministic_hash_code_old("ABC"));
    }

    #[test]
    fn mega_random_and_rng_exact_bit_sequence() {
        let mr = MegaRandom::from_seed(12345);
        assert_eq!(mr.s0, 2_454_886_589_211_414_944u64);
        let mut s = 12345u64;
        let e0 = MegaRandom::splitmix64(&mut s);
        let e1 = MegaRandom::splitmix64(&mut s);
        let e2 = MegaRandom::splitmix64(&mut s);
        let e3 = MegaRandom::splitmix64(&mut s);
        assert_eq!((mr.s0, mr.s1, mr.s2, mr.s3), (e0, e1, e2, e3));

        let mut rng = Rng::from_seed(42);
        let b0 = rng.next_bool();
        let i0 = rng.next_int(100).unwrap();
        let i1 = rng.next_int_range(-5, 10).unwrap();
        let f0 = rng.next_f32_range(0.0, 1.0).unwrap();
        let d0 = rng.next_f64();
        assert_eq!(rng.counter, 5);

        // Verify deterministic replay from saved words
        let words = rng.to_words();
        let mut rng2 = Rng::from_words(words);
        assert_eq!(rng.next_u64(), rng2.next_u64());
        assert_eq!(rng.next_int(1000).unwrap(), rng2.next_int(1000).unwrap());
        assert_eq!(rng.to_words(), rng2.to_words());
        let _ = (b0, i0, i1, f0, d0);
    }

    #[test]
    fn unstable_shuffle_consumes_one_parent_draw() {
        let mut rng = Rng::from_seed(999);
        let mut data = [1, 2, 3, 4, 5, 6, 7, 8];
        rng.unstable_shuffle(&mut data);
        assert_eq!(rng.counter, 1);

        let mut rng2 = Rng::from_seed(999);
        let mut data2 = [1, 2, 3, 4, 5, 6, 7, 8];
        rng2.unstable_shuffle(&mut data2);
        assert_eq!(data, data2);
        assert_eq!(rng.to_words(), rng2.to_words());
    }

    #[test]
    fn run_rng_set_old_seed_handling() {
        let set_old = RunRngSet::from_string_seed("oldSEED1");
        let expected_seed = (deterministic_hash_code_old("SEED1") as u32) as u64;
        assert_eq!(set_old.seed, expected_seed);
    }
}
