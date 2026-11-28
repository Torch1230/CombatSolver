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
    debug_assert!(offset + 8 <= bytes.len());
    // SAFETY: Caller ensures `offset + 8 <= bytes.len()`.
    unsafe { u64::from_le(std::ptr::read_unaligned(bytes.as_ptr().add(offset).cast::<u64>())) }
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

/// Zero-allocation streaming 128-bit hasher matching `hash_bytes_128` identically.
pub struct StreamingHasher128 {
    buf: [u8; 32],
    pos: usize,
    total_len: u64,
    v1: u64,
    v2: u64,
    v3: u64,
    v4: u64,
}

impl Default for StreamingHasher128 {
    fn default() -> Self {
        Self::new()
    }
}

impl StreamingHasher128 {
    #[inline]
    pub const fn new() -> Self {
        Self {
            buf: [0u8; 32],
            pos: 0,
            total_len: 0,
            v1: PRIME64_1.wrapping_add(PRIME64_2),
            v2: PRIME64_2,
            v3: 0,
            v4: 0u64.wrapping_sub(PRIME64_1),
        }
    }

    #[inline(always)]
    fn consume_block(&mut self, block: &[u8]) {
        let x0 = read_u64_le(block, 0);
        let x1 = read_u64_le(block, 8);
        let x2 = read_u64_le(block, 16);
        let x3 = read_u64_le(block, 24);
        self.v1 = round64(self.v1, x0);
        self.v2 = round64(self.v2, x1);
        self.v3 = round64(self.v3, x2);
        self.v4 = round64(self.v4, x3);
    }

    #[inline(always)]
    fn flush_full_block(&mut self) {
        let x0 = read_u64_le(&self.buf, 0);
        let x1 = read_u64_le(&self.buf, 8);
        let x2 = read_u64_le(&self.buf, 16);
        let x3 = read_u64_le(&self.buf, 24);
        self.v1 = round64(self.v1, x0);
        self.v2 = round64(self.v2, x1);
        self.v3 = round64(self.v3, x2);
        self.v4 = round64(self.v4, x3);
        self.pos = 0;
    }

    #[inline(always)]
    pub fn write_bytes(&mut self, mut data: &[u8]) {
        self.total_len = self.total_len.wrapping_add(data.len() as u64);
        if self.pos > 0 {
            let take = (32 - self.pos).min(data.len());
            self.buf[self.pos..self.pos + take].copy_from_slice(&data[..take]);
            self.pos += take;
            data = &data[take..];
            if self.pos == 32 {
                self.flush_full_block();
            }
        }
        while data.len() >= 32 {
            self.consume_block(&data[..32]);
            data = &data[32..];
        }
        if !data.is_empty() {
            self.buf[..data.len()].copy_from_slice(data);
            self.pos = data.len();
        }
    }

    #[inline(always)]
    pub fn write_u8(&mut self, v: u8) {
        self.buf[self.pos] = v;
        self.pos += 1;
        self.total_len = self.total_len.wrapping_add(1);
        if self.pos == 32 {
            self.flush_full_block();
        }
    }

    #[inline(always)]
    pub fn write_bool(&mut self, v: bool) {
        self.write_u8(u8::from(v));
    }

    #[inline(always)]
    pub fn write_u16(&mut self, v: u16) {
        let bytes = v.to_le_bytes();
        if self.pos + 2 <= 32 {
            self.buf[self.pos..self.pos + 2].copy_from_slice(&bytes);
            self.pos += 2;
            self.total_len = self.total_len.wrapping_add(2);
            if self.pos == 32 {
                self.flush_full_block();
            }
        } else {
            self.write_bytes(&bytes);
        }
    }

    #[inline(always)]
    pub fn write_u32(&mut self, v: u32) {
        let bytes = v.to_le_bytes();
        if self.pos + 4 <= 32 {
            self.buf[self.pos..self.pos + 4].copy_from_slice(&bytes);
            self.pos += 4;
            self.total_len = self.total_len.wrapping_add(4);
            if self.pos == 32 {
                self.flush_full_block();
            }
        } else {
            self.write_bytes(&bytes);
        }
    }

    #[inline(always)]
    pub fn write_i32(&mut self, v: i32) {
        self.write_u32(v as u32);
    }

    #[inline(always)]
    pub fn write_u64(&mut self, v: u64) {
        let bytes = v.to_le_bytes();
        if self.pos + 8 <= 32 {
            self.buf[self.pos..self.pos + 8].copy_from_slice(&bytes);
            self.pos += 8;
            self.total_len = self.total_len.wrapping_add(8);
            if self.pos == 32 {
                self.flush_full_block();
            }
        } else {
            self.write_bytes(&bytes);
        }
    }

    #[inline(always)]
    pub fn write_str(&mut self, s: &str) {
        self.write_u32(s.len() as u32);
        self.write_bytes(s.as_bytes());
    }

    #[inline]
    pub fn finish(self) -> StateHash {
        if self.total_len >= 32 {
            let mut h1 = self
                .v1
                .rotate_left(1)
                .wrapping_add(self.v2.rotate_left(7))
                .wrapping_add(self.v3.rotate_left(12))
                .wrapping_add(self.v4.rotate_left(18));
            h1 = merge_round64(h1, self.v1);
            h1 = merge_round64(h1, self.v2);
            h1 = merge_round64(h1, self.v3);
            h1 = merge_round64(h1, self.v4);
            h1 = h1.wrapping_add(self.total_len);

            let mut h2 = self
                .v1
                .rotate_left(13)
                .wrapping_add(self.v2.rotate_left(29))
                .wrapping_add(self.v3.rotate_left(37))
                .wrapping_add(self.v4.rotate_left(53));
            h2 = merge_round64(h2, self.v4 ^ PRIME64_3);
            h2 = merge_round64(h2, self.v3 ^ PRIME64_5);
            h2 = merge_round64(h2, self.v2 ^ PRIME64_1);
            h2 = merge_round64(h2, self.v1 ^ PRIME64_2);
            h2 = h2.wrapping_add(self.total_len.wrapping_mul(PRIME64_5));

            let tail = &self.buf[..self.pos];
            let t1 = xxh64(tail, h1 ^ PRIME64_1);
            let t2 = xxh64(tail, h2 ^ PRIME64_2);
            StateHash::from_parts(avalanche64(h1 ^ t1), avalanche64(h2 ^ t2))
        } else {
            let tail = &self.buf[..self.pos];
            StateHash::from_parts(xxh64(tail, PRIME64_1), xxh64(tail, PRIME64_2))
        }
    }
}

/// Direct word-accumulator 128-bit hasher using folded 128-bit multiplications.
#[derive(Clone, Copy)]
pub struct WordHasher128 {
    a0: u64,
    a1: u64,
    b0: u64,
    b1: u64,
    len: u64,
}

impl Default for WordHasher128 {
    fn default() -> Self {
        Self::new()
    }
}

impl WordHasher128 {
    #[inline(always)]
    pub const fn new() -> Self {
        Self {
            a0: PRIME64_1.wrapping_add(PRIME64_2),
            a1: PRIME64_2,
            b0: PRIME64_3,
            b1: 0u64.wrapping_sub(PRIME64_1),
            len: 0,
        }
    }

    #[inline(always)]
    fn absorb_u64(&mut self, w: u64) {
        let p = ((self.a0 ^ w) as u128).wrapping_mul(PRIME64_1 as u128);
        self.a0 = self.a1.wrapping_add(p as u64);
        self.a1 = ((p >> 64) as u64) ^ w;
    }

    #[inline(always)]
    fn absorb_u64x2(&mut self, x: u64, y: u64) {
        let p = ((self.a0 ^ x) as u128).wrapping_mul((self.a1 ^ y) as u128);
        self.a0 = self.a0.rotate_left(29).wrapping_add(p as u64) ^ y;
        self.a1 = self.a1.rotate_left(37).wrapping_add((p >> 64) as u64) ^ x;
    }

    #[inline(always)]
    fn absorb_u64x4(&mut self, x0: u64, x1: u64, x2: u64, x3: u64) {
        let p0 = ((self.a0 ^ x0) as u128).wrapping_mul((self.a1 ^ x1) as u128);
        let p1 = ((self.b0 ^ x2) as u128).wrapping_mul((self.b1 ^ x3) as u128);
        self.a0 = self.a0.rotate_left(29).wrapping_add(p0 as u64) ^ x1;
        self.a1 = self.a1.rotate_left(37).wrapping_add((p0 >> 64) as u64) ^ x0;
        self.b0 = self.b0.rotate_left(29).wrapping_add(p1 as u64) ^ x3;
        self.b1 = self.b1.rotate_left(37).wrapping_add((p1 >> 64) as u64) ^ x2;
    }

    #[inline(always)]
    pub fn mix_u64(&mut self, w: u64) {
        self.absorb_u64(w);
        self.len = self.len.wrapping_add(8);
    }

    #[inline(always)]
    pub fn mix_u64x2(&mut self, a: u64, b: u64) {
        self.absorb_u64x2(a, b);
        self.len = self.len.wrapping_add(16);
    }

    #[inline(always)]
    pub fn mix_u64x4(&mut self, a: u64, b: u64, c: u64, d: u64) {
        self.absorb_u64x4(a, b, c, d);
        self.len = self.len.wrapping_add(32);
    }

    #[inline(always)]
    pub fn mix_u32(&mut self, v: u32) {
        self.mix_u64(v as u64);
    }

    #[inline(always)]
    pub fn mix_i32(&mut self, v: i32) {
        self.mix_u64(v as u32 as u64);
    }

    #[inline(always)]
    pub fn mix_bool(&mut self, v: bool) {
        self.mix_u64(u64::from(v));
    }

    #[cold]
    #[inline(never)]
    fn mix_bytes_chunks32<'a>(&mut self, mut data: &'a [u8]) -> &'a [u8] {
        while data.len() >= 32 {
            let x0 = read_u64_le(data, 0);
            let x1 = read_u64_le(data, 8);
            let x2 = read_u64_le(data, 16);
            let x3 = read_u64_le(data, 24);
            self.absorb_u64x4(x0, x1, x2, x3);
            data = &data[32..];
        }
        data
    }

    #[inline(always)]
    pub fn mix_bytes(&mut self, mut data: &[u8]) {
        self.len = self.len.wrapping_add(data.len() as u64);
        if data.len() >= 8 {
            if data.len() >= 32 {
                let x0 = read_u64_le(data, 0);
                let x1 = read_u64_le(data, 8);
                let x2 = read_u64_le(data, 16);
                let x3 = read_u64_le(data, 24);
                self.absorb_u64x4(x0, x1, x2, x3);
                data = &data[32..];
                if data.len() >= 32 {
                    data = self.mix_bytes_chunks32(data);
                }
            }
            if data.len() >= 16 {
                let x0 = read_u64_le(data, 0);
                let x1 = read_u64_le(data, 8);
                self.absorb_u64x2(x0, x1);
                data = &data[16..];
            }
            if data.len() >= 8 {
                let w = read_u64_le(data, 0);
                self.absorb_u64(w);
                data = &data[8..];
            }
        }
        if !data.is_empty() {
            let len = data.len();
            let ptr = data.as_ptr();
            let mut tail = 0u64;
            let mut off = 0usize;
            if (len & 4) != 0 {
                // SAFETY: `len < 8` and `(len & 4) != 0` guarantee 4 readable bytes at `ptr`.
                tail = unsafe { u32::from_le(std::ptr::read_unaligned(ptr.cast::<u32>())) } as u64;
                off = 4;
            }
            if (len & 2) != 0 {
                // SAFETY: `off + 2 <= len` when `(len & 2) != 0`.
                tail |= (unsafe {
                    u16::from_le(std::ptr::read_unaligned(ptr.add(off).cast::<u16>()))
                } as u64)
                    << (off * 8);
                off += 2;
            }
            if (len & 1) != 0 {
                // SAFETY: `off < len` when `(len & 1) != 0`.
                tail |= (unsafe { *ptr.add(off) } as u64) << (off * 8);
            }
            let tagged = tail ^ ((len as u64) << 56) ^ PRIME64_5;
            self.absorb_u64(tagged);
        }
    }

    #[inline(always)]
    pub fn mix_str(&mut self, s: &str) {
        self.mix_bytes(s.as_bytes());
    }

    #[inline(always)]
    pub fn finish(self) -> StateHash {
        let lo_p = ((self.a0 ^ self.b0 ^ self.len) as u128)
            .wrapping_mul((self.a1 ^ self.b1 ^ PRIME64_5) as u128);
        let hi_p = ((self.a0.wrapping_add(self.b1) ^ PRIME64_3) as u128)
            .wrapping_mul(
                (self.a1.wrapping_add(self.b0) ^ self.len.wrapping_mul(PRIME64_2)) as u128,
            );
        let lo = avalanche64((lo_p as u64) ^ ((lo_p >> 64) as u64) ^ self.a0 ^ self.b1);
        let hi = avalanche64(
            (hi_p as u64) ^ ((hi_p >> 64) as u64) ^ self.a1 ^ self.b0 ^ lo.rotate_left(23),
        );
        StateHash::from_parts(hi, lo)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn word_hasher_sensitive_to_length_and_single_bit() {
        let mut a = WordHasher128::new();
        a.mix_str("abc");
        let mut b = WordHasher128::new();
        b.mix_str("abc\0");
        assert_ne!(a.finish(), b.finish());

        let mut c = WordHasher128::new();
        c.mix_u64x4(1, 2, 3, 4);
        let mut d = WordHasher128::new();
        d.mix_u64x4(1, 2, 3, 5);
        assert_ne!(c.finish(), d.finish());
    }

    #[test]
    fn streaming_hasher_matches_hash_bytes_128_across_lengths() {
        let data: Vec<u8> = (0..137u8).map(|i| i.wrapping_mul(31).wrapping_add(7)).collect();
        for len in [0, 1, 15, 31, 32, 33, 63, 64, 65, 97, 137] {
            let mut h = StreamingHasher128::new();
            let slice = &data[..len];
            let mut i = 0;
            while i < slice.len() {
                let step = (i % 7) + 1;
                let end = (i + step).min(slice.len());
                h.write_bytes(&slice[i..end]);
                i = end;
            }
            assert_eq!(h.finish(), hash_bytes_128(slice), "mismatch at len={len}");
        }
    }

    #[test]
    fn word_hasher_mix_bytes_tail_matches_reference_byte_loop() {
        let data: Vec<u8> = (0..96u8).map(|i| i.wrapping_mul(29).wrapping_add(3)).collect();
        for len in 0..=64 {
            let slice = &data[..len];
            let mut fast = WordHasher128::new();
            fast.mix_bytes(slice);

            let mut reference = WordHasher128::new();
            reference.len = reference.len.wrapping_add(len as u64);
            let mut rem = slice;
            while rem.len() >= 32 {
                reference.absorb_u64x4(
                    read_u64_le(rem, 0),
                    read_u64_le(rem, 8),
                    read_u64_le(rem, 16),
                    read_u64_le(rem, 24),
                );
                rem = &rem[32..];
            }
            if rem.len() >= 16 {
                reference.absorb_u64x2(read_u64_le(rem, 0), read_u64_le(rem, 8));
                rem = &rem[16..];
            }
            if rem.len() >= 8 {
                reference.absorb_u64(read_u64_le(rem, 0));
                rem = &rem[8..];
            }
            if !rem.is_empty() {
                let mut tail = 0u64;
                for (i, &b) in rem.iter().enumerate() {
                    tail |= (b as u64) << (i * 8);
                }
                let tagged = tail ^ ((rem.len() as u64) << 56) ^ PRIME64_5;
                reference.absorb_u64(tagged);
            }
            assert_eq!(fast.finish(), reference.finish(), "mismatch at len={len}");
        }
    }
}
