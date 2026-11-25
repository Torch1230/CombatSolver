use core::fmt;
use rust_decimal::prelude::ToPrimitive;
use rust_decimal::{Decimal, RoundingStrategy};
use serde::{Deserialize, Deserializer, Serialize, Serializer, de};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DecError {
    Overflow,
    DivideByZero,
    ParseError,
}

impl fmt::Display for DecError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Overflow => write!(f, "decimal overflow"),
            Self::DivideByZero => write!(f, "decimal division by zero"),
            Self::ParseError => write!(f, "invalid decimal representation"),
        }
    }
}

impl std::error::Error for DecError {}

/// Fixed-point 96-bit mantissa decimal matching C# `System.Decimal` semantics.
#[derive(Clone, Copy, Debug, Eq, Hash, Default)]
#[repr(transparent)]
pub struct Dec(pub Decimal);

impl PartialEq for Dec {
    #[inline(always)]
    fn eq(&self, other: &Self) -> bool {
        if self.0.scale() == other.0.scale() {
            return self.0.mantissa() == other.0.mantissa();
        }
        self.0 == other.0
    }
}

impl PartialOrd for Dec {
    #[inline(always)]
    fn partial_cmp(&self, other: &Self) -> Option<core::cmp::Ordering> {
        Some(self.cmp(other))
    }
}

impl Ord for Dec {
    #[inline(always)]
    fn cmp(&self, other: &Self) -> core::cmp::Ordering {
        if self.0.scale() == other.0.scale() {
            return self.0.mantissa().cmp(&other.0.mantissa());
        }
        self.0.cmp(&other.0)
    }
}

impl Dec {
    pub const ZERO: Self = Self(Decimal::ZERO);
    pub const ONE: Self = Self(Decimal::ONE);
    pub const TWO: Self = Self(Decimal::TWO);
    pub const HALF: Self = Self(Decimal::from_parts(5, 0, 0, false, 1));
    pub const THREE_QUARTERS: Self = Self(Decimal::from_parts(75, 0, 0, false, 2));
    pub const SEVENTY_PERCENT: Self = Self(Decimal::from_parts(70, 0, 0, false, 2));
    pub const ONE_POINT_FIVE: Self = Self(Decimal::from_parts(15, 0, 0, false, 1));
    pub const MAX_VALUE: Self = Self(Decimal::MAX);
    pub const MIN_VALUE: Self = Self(Decimal::MIN);
    pub const CLAMP_MAX_999M: Self = Self(Decimal::from_parts(999_999_999, 0, 0, false, 0));

    #[inline(always)]
    pub const fn from_i32(value: i32) -> Self {
        Self(Decimal::from_parts(value.unsigned_abs(), 0, 0, value < 0, 0))
    }

    #[inline]
    pub fn from_i64(value: i64) -> Self {
        Self(Decimal::from_i128_with_scale(value as i128, 0))
    }

    #[inline]
    pub fn from_parts(num: i64, scale: u32) -> Result<Self, DecError> {
        Decimal::try_from_i128_with_scale(num as i128, scale)
            .map(Self)
            .map_err(|_| DecError::Overflow)
    }

    #[inline]
    pub const fn from_decimal(value: Decimal) -> Self {
        Self(value)
    }

    #[inline]
    pub const fn as_decimal(self) -> Decimal {
        self.0
    }

    #[inline]
    pub fn is_zero(self) -> bool {
        self.0.is_zero()
    }

    #[inline]
    pub fn is_negative(self) -> bool {
        self.0.is_sign_negative() && !self.0.is_zero()
    }

    #[inline]
    pub fn is_positive(self) -> bool {
        self.0.is_sign_positive() && !self.0.is_zero()
    }

    #[inline(always)]
    pub fn max(self, other: Self) -> Self {
        if self >= other { self } else { other }
    }

    #[inline(always)]
    pub fn min(self, other: Self) -> Self {
        if self <= other { self } else { other }
    }

    #[inline(always)]
    pub fn clamp(self, min: Self, max: Self) -> Self {
        self.max(min).min(max)
    }

    /// C# `Math.Round(d, dp, MidpointRounding.ToEven)`.
    #[inline(always)]
    pub fn round_to_even(self, dp: u32) -> Self {
        if self.0.scale() <= dp {
            return self;
        }
        Self(self.0.round_dp_with_strategy(dp, RoundingStrategy::MidpointNearestEven))
    }

    /// C# `Math.Floor(d)`.
    #[inline(always)]
    pub fn floor(self) -> Self {
        if self.0.scale() == 0 {
            return self;
        }
        Self(self.0.floor())
    }

    /// C# `Math.Ceiling(d)`.
    #[inline(always)]
    pub fn ceil(self) -> Self {
        if self.0.scale() == 0 {
            return self;
        }
        Self(self.0.ceil())
    }

    /// C# `Math.Truncate(d)` / `decimal.Truncate(d)`.
    #[inline(always)]
    pub fn trunc(self) -> Self {
        if self.0.scale() == 0 {
            return self;
        }
        Self(self.0.trunc())
    }

    /// Exact match for C# `(int)decimal` (truncates toward zero, errors on overflow).
    #[inline(always)]
    pub fn to_i32_trunc(self) -> Result<i32, DecError> {
        let m = self.0.mantissa();
        let scale = self.0.scale();
        if scale == 0 {
            return i32::try_from(m).map_err(|_| DecError::Overflow);
        }
        if let Ok(m64) = i64::try_from(m) {
            let q = match scale {
                1 => m64 / 10,
                2 => m64 / 100,
                3 => m64 / 1000,
                4 => m64 / 10_000,
                _ => return self.0.trunc().to_i32().ok_or(DecError::Overflow),
            };
            return i32::try_from(q).map_err(|_| DecError::Overflow);
        }
        self.0.trunc().to_i32().ok_or(DecError::Overflow)
    }

    #[inline(always)]
    pub fn checked_add(self, rhs: Self) -> Result<Self, DecError> {
        if self.0.scale() == 0 && rhs.0.scale() == 0 {
            let res = self.0.mantissa().wrapping_add(rhs.0.mantissa());
            let abs = res.unsigned_abs();
            if abs <= u32::MAX as u128 {
                return Ok(Self(Decimal::from_parts(abs as u32, 0, 0, res < 0, 0)));
            }
        }
        self.0.checked_add(rhs.0).map(Self).ok_or(DecError::Overflow)
    }

    #[inline(always)]
    pub fn checked_sub(self, rhs: Self) -> Result<Self, DecError> {
        if self.0.scale() == 0 && rhs.0.scale() == 0 {
            let res = self.0.mantissa().wrapping_sub(rhs.0.mantissa());
            let abs = res.unsigned_abs();
            if abs <= u32::MAX as u128 {
                return Ok(Self(Decimal::from_parts(abs as u32, 0, 0, res < 0, 0)));
            }
        }
        self.0.checked_sub(rhs.0).map(Self).ok_or(DecError::Overflow)
    }

    #[inline(always)]
    pub fn checked_mul(self, rhs: Self) -> Result<Self, DecError> {
        let sum_scale = self.0.scale() + rhs.0.scale();
        if sum_scale <= 4 {
            if let Some(res) = self.0.mantissa().checked_mul(rhs.0.mantissa()) {
                let abs = res.unsigned_abs();
                if abs <= u32::MAX as u128 {
                    return Ok(Self(Decimal::from_parts(
                        abs as u32,
                        0,
                        0,
                        res < 0,
                        sum_scale,
                    )));
                }
            }
        }
        self.0.checked_mul(rhs.0).map(Self).ok_or(DecError::Overflow)
    }

    #[inline]
    pub fn checked_div(self, rhs: Self) -> Result<Self, DecError> {
        if rhs.is_zero() {
            return Err(DecError::DivideByZero);
        }
        self.0.checked_div(rhs.0).map(Self).ok_or(DecError::Overflow)
    }

    #[inline]
    pub fn checked_neg(self) -> Result<Self, DecError> {
        if self.0.is_zero() {
            return Ok(Self::ZERO);
        }
        let mut d = self.0;
        d.set_sign_negative(!d.is_sign_negative());
        Ok(Self(d))
    }
}

impl From<i32> for Dec {
    #[inline]
    fn from(value: i32) -> Self {
        Self::from_i32(value)
    }
}

impl fmt::Display for Dec {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.0.normalize())
    }
}

impl Serialize for Dec {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        rust_decimal::serde::float::serialize(&self.0, serializer)
    }
}

impl<'de> Deserialize<'de> for Dec {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct DecVisitor;
        impl<'de> de::Visitor<'de> for DecVisitor {
            type Value = Dec;

            fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                f.write_str("a decimal number or string")
            }

            fn visit_i64<E: de::Error>(self, v: i64) -> Result<Dec, E> {
                Ok(Dec::from_i64(v))
            }

            fn visit_u64<E: de::Error>(self, v: u64) -> Result<Dec, E> {
                Decimal::try_from(v)
                    .map(Dec)
                    .map_err(|e| E::custom(e.to_string()))
            }

            fn visit_f64<E: de::Error>(self, v: f64) -> Result<Dec, E> {
                Decimal::try_from(v)
                    .map(Dec)
                    .map_err(|e| E::custom(e.to_string()))
            }

            fn visit_str<E: de::Error>(self, v: &str) -> Result<Dec, E> {
                v.parse::<Decimal>()
                    .map(Dec)
                    .map_err(|e| E::custom(e.to_string()))
            }
        }
        deserializer.deserialize_any(DecVisitor)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rounding_modes_match_csharp_decimal() {
        let pos_half = Dec::from_parts(25, 1).unwrap(); // 2.5
        let pos_three_half = Dec::from_parts(35, 1).unwrap(); // 3.5
        let neg_half = Dec::from_parts(-25, 1).unwrap(); // -2.5
        let pos_frac = Dec::from_parts(29, 1).unwrap(); // 2.9
        let neg_frac = Dec::from_parts(-29, 1).unwrap(); // -2.9

        assert_eq!(pos_half.round_to_even(0), Dec::from_i32(2));
        assert_eq!(pos_three_half.round_to_even(0), Dec::from_i32(4));
        assert_eq!(neg_half.round_to_even(0), Dec::from_i32(-2));

        assert_eq!(pos_frac.floor(), Dec::from_i32(2));
        assert_eq!(neg_frac.floor(), Dec::from_i32(-3));
        assert_eq!(pos_frac.ceil(), Dec::from_i32(3));
        assert_eq!(neg_frac.ceil(), Dec::from_i32(-2));
        assert_eq!(pos_frac.trunc(), Dec::from_i32(2));
        assert_eq!(neg_frac.trunc(), Dec::from_i32(-2));
        assert_eq!(pos_frac.to_i32_trunc().unwrap(), 2);
        assert_eq!(neg_frac.to_i32_trunc().unwrap(), -2);
    }

    #[test]
    fn checked_overflow_and_div_zero_error() {
        assert_eq!(
            Dec::MAX_VALUE.checked_add(Dec::ONE),
            Err(DecError::Overflow)
        );
        assert_eq!(
            Dec::ONE.checked_div(Dec::ZERO),
            Err(DecError::DivideByZero)
        );
        assert_eq!(
            Dec::MAX_VALUE.to_i32_trunc(),
            Err(DecError::Overflow)
        );
        assert_eq!(Dec::from_i32(i32::MAX).to_i32_trunc(), Ok(i32::MAX));
        assert_eq!(Dec::from_i32(i32::MIN).to_i32_trunc(), Ok(i32::MIN));
        assert_eq!(
            Dec::from_i64(i32::MAX as i64 + 1).to_i32_trunc(),
            Err(DecError::Overflow)
        );
        assert_eq!(
            Dec::from_i64(i32::MIN as i64 - 1).to_i32_trunc(),
            Err(DecError::Overflow)
        );
        assert_eq!(Dec::from_i32(i32::MAX).checked_add(Dec::ONE), Ok(Dec::from_i64(i32::MAX as i64 + 1)));
        assert_eq!(Dec::from_i32(-5).checked_sub(Dec::from_i32(7)), Ok(Dec::from_i32(-12)));
        assert_eq!(Dec::from_i32(-5).checked_mul(Dec::from_i32(-7)), Ok(Dec::from_i32(35)));
        assert_eq!(Dec::from_parts(250, 2).unwrap().to_i32_trunc(), Ok(2));
        assert_eq!(Dec::from_parts(-299, 2).unwrap().to_i32_trunc(), Ok(-2));
        assert_eq!(Dec::from_i32(7).trunc(), Dec::from_i32(7));
        assert_eq!(Dec::from_i32(-7).floor(), Dec::from_i32(-7));
        assert_eq!(Dec::from_i32(-7).ceil(), Dec::from_i32(-7));
        assert_eq!(Dec::from_parts(25, 1).unwrap().round_to_even(1), Dec::from_parts(25, 1).unwrap());
    }

    #[test]
    fn damage_multiplier_truncation_matches_csharp() {
        // 6 * 1.5 = 9.0, 5 * 0.75 = 3.75 -> trunc 3
        let vuln = Dec::from_i32(6).checked_mul(Dec::ONE_POINT_FIVE).unwrap();
        assert_eq!(vuln.to_i32_trunc().unwrap(), 9);
        let frail = Dec::from_i32(5).checked_mul(Dec::THREE_QUARTERS).unwrap();
        assert_eq!(frail.to_i32_trunc().unwrap(), 3);
    }

    #[test]
    fn serde_round_trip_numbers_and_strings() {
        let d: Dec = serde_json::from_str("\"12.75\"").unwrap();
        assert_eq!(d, Dec::from_parts(1275, 2).unwrap());
        let d2: Dec = serde_json::from_str("8.5").unwrap();
        assert_eq!(d2, Dec::from_parts(85, 1).unwrap());
        let json = serde_json::to_string(&d).unwrap();
        let d3: Dec = serde_json::from_str(&json).unwrap();
        assert_eq!(d3, d);
    }

    #[test]
    fn fast_paths_match_decimal_across_scales_and_bounds() {
        let mut samples = vec![
            Dec::ZERO,
            Dec::ONE,
            Dec::TWO,
            Dec::HALF,
            Dec::THREE_QUARTERS,
            Dec::SEVENTY_PERCENT,
            Dec::ONE_POINT_FIVE,
            Dec::MAX_VALUE,
            Dec::MIN_VALUE,
            Dec::CLAMP_MAX_999M,
            Dec::from_i32(i32::MAX),
            Dec::from_i32(i32::MIN),
            Dec::from_i64(i32::MAX as i64 + 1),
            Dec::from_i64(i32::MIN as i64 - 1),
        ];
        for scale in 0..=5u32 {
            for m in [
                0i64, 1, -1, 9, -9, 10, -10, 15, -15, 99, -99, 100, -100, 199, -199, 999, -999,
                1000, -1000, 9999, -9999, 10_000, -10_000, 123_456, -123_456,
            ] {
                samples.push(Dec::from_parts(m, scale).unwrap());
            }
            samples.push(Dec::from_decimal(Decimal::from_parts(u32::MAX, 0, 0, false, scale)));
            samples.push(Dec::from_decimal(Decimal::from_parts(u32::MAX, 0, 0, true, scale)));
        }

        for &a in &samples {
            assert_eq!(
                a.to_i32_trunc().ok(),
                a.as_decimal().trunc().to_i32(),
                "to_i32_trunc mismatch on {a:?}"
            );
            for &b in &samples {
                assert_eq!(a == b, a.as_decimal() == b.as_decimal(), "eq mismatch {a:?} {b:?}");
                assert_eq!(
                    a.cmp(&b),
                    a.as_decimal().cmp(&b.as_decimal()),
                    "cmp mismatch {a:?} {b:?}"
                );
                assert_eq!(
                    a.max(b).as_decimal(),
                    a.as_decimal().max(b.as_decimal()),
                    "max mismatch {a:?} {b:?}"
                );
                assert_eq!(
                    a.min(b).as_decimal(),
                    a.as_decimal().min(b.as_decimal()),
                    "min mismatch {a:?} {b:?}"
                );
                assert_eq!(
                    a.checked_mul(b).map(|x| x.as_decimal()).ok(),
                    a.as_decimal().checked_mul(b.as_decimal()),
                    "checked_mul mismatch {a:?} * {b:?}"
                );
            }
        }
    }
}
