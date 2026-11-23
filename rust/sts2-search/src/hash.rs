use xxhash_rust::xxh64::xxh64;

const PRIME64_1: u64 = 0x9E37_79B1_85EB_CA87;
const PRIME64_2: u64 = 0xC2B2_AE3D_27D4_EB4F;
const PRIME64_3: u64 = 0x1656_67B1_9E37_79F9;
const PRIME64_4: u64 = 0x85EB_CA77_C2B2_AE63;
const PRIME64_5: u64 = 0x27D4_EB2F_1656_67C5;

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Hash, Ord, PartialOrd)]
pub struct StateHash(pub u128);

impl StateHash {
    #[inline]
    pub const fn from_parts(hi: u64, lo: u64) -> Self {
        Self(((hi as u128) << 64) | (lo as u128))
    }

    #[inline]
    pub const fn shard_index(self) -> usize {
        (self.0 >> 120) as usize
    }

    #[inline]
    pub const fn parts(self) -> (u64, u64) {
        ((self.0 >> 64) as u64, self.0 as u64)
    }
}

#[inline(always)]
fn read_u64_le(bytes: &[u8], offset: usize) -> u64 {
    let mut buf = [0u8; 8];
    buf.copy_from_slice(&bytes[offset..offset + 8]);
    u64::from_le_bytes(buf)
}

#[inline(always)]
fn round64(acc: u64, input: u64) -> u64 {
    acc.wrapping_add(input.wrapping_mul(PRIME64_2))
        .rotate_left(31)
        .wrapping_mul(PRIME64_1)
}

#[inline(always)]
fn merge_round64(mut acc: u64, val: u64) -> u64 {
    let v = round64(0, val);
    acc ^= v;
    acc.wrapping_mul(PRIME64_1).wrapping_add(PRIME64_4)
}

#[inline(always)]
fn avalanche64(mut h: u64) -> u64 {
    h ^= h >> 33;
    h = h.wrapping_mul(PRIME64_2);
    h ^= h >> 29;
    h = h.wrapping_mul(PRIME64_3);
    h ^= h >> 32;
    h
}

/// 4-lane parallel unrolled byte-level 128-bit hash with dual-seed `xxh64` finalization.
pub fn hash_bytes_128(bytes: &[u8]) -> StateHash {
    let len = bytes.len();
    let mut offset = 0usize;

    let (hi_acc, lo_acc) = if len >= 32 {
        let mut v1 = PRIME64_1.wrapping_add(PRIME64_2);
        let mut v2 = PRIME64_2;
        let mut v3 = 0u64;
        let mut v4 = 0u64.wrapping_sub(PRIME64_1);

        let limit = len - 32;
        while offset <= limit {
            let x0 = read_u64_le(bytes, offset);
            let x1 = read_u64_le(bytes, offset + 8);
            let x2 = read_u64_le(bytes, offset + 16);
            let x3 = read_u64_le(bytes, offset + 24);
            v1 = round64(v1, x0);
            v2 = round64(v2, x1);
            v3 = round64(v3, x2);
            v4 = round64(v4, x3);
            offset += 32;
        }

        let mut h1 = v1
            .rotate_left(1)
            .wrapping_add(v2.rotate_left(7))
            .wrapping_add(v3.rotate_left(12))
            .wrapping_add(v4.rotate_left(18));
        h1 = merge_round64(h1, v1);
        h1 = merge_round64(h1, v2);
        h1 = merge_round64(h1, v3);
        h1 = merge_round64(h1, v4);
        h1 = h1.wrapping_add(len as u64);

        let mut h2 = v1
            .rotate_left(13)
            .wrapping_add(v2.rotate_left(29))
            .wrapping_add(v3.rotate_left(37))
            .wrapping_add(v4.rotate_left(53));
        h2 = merge_round64(h2, v4 ^ PRIME64_3);
        h2 = merge_round64(h2, v3 ^ PRIME64_5);
        h2 = merge_round64(h2, v2 ^ PRIME64_1);
        h2 = merge_round64(h2, v1 ^ PRIME64_2);
        h2 = h2.wrapping_add((len as u64).wrapping_mul(PRIME64_5));

        let tail = &bytes[offset..];
        let t1 = xxh64(tail, h1 ^ PRIME64_1);
        let t2 = xxh64(tail, h2 ^ PRIME64_2);
        (avalanche64(h1 ^ t1), avalanche64(h2 ^ t2))
    } else {
        let h1 = xxh64(bytes, PRIME64_1);
        let h2 = xxh64(bytes, PRIME64_2);
        (h1, h2)
    };

    StateHash::from_parts(hi_acc, lo_acc)
}

#[inline]
pub fn hash_u64_pair(a: u64, b: u64) -> StateHash {
    let mut buf = [0u8; 16];
    buf[0..8].copy_from_slice(&a.to_le_bytes());
    buf[8..16].copy_from_slice(&b.to_le_bytes());
    hash_bytes_128(&buf)
}

#[inline]
pub fn hash_u64_slice(words: &[u64]) -> u64 {
    let mut h = PRIME64_5.wrapping_add((words.len() as u64).wrapping_mul(8));
    for &w in words {
        h = round64(h, w);
    }
    avalanche64(h)
}
