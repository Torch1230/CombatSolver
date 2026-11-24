#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd)]
#[repr(u8)]
pub enum HookSlot {
    BeforeCombatStart = 0,
    BeforeCombatStartLate = 1,
    AfterCombatVictoryEarly = 2,
    AfterCombatVictory = 3,
    AfterCombatEnd = 4,
    AfterRoomEntered = 5,

    BeforeSideTurnStart = 6,
    AfterPlayerTurnStart = 7,
    AfterSideTurnStart = 8,
    AfterSideTurnStartLate = 9,
    AfterAutoPrePlayPhaseEntered = 10,
    AfterAutoPostPlayPhaseEntered = 11,
    BeforeSideTurnEndVeryEarly = 12,
    BeforeSideTurnEndEarly = 13,
    BeforeSideTurnEnd = 14,
    AfterSideTurnEnd = 15,
    AfterSideTurnEndLate = 16,

    ShouldClearBlock = 17,
    AfterPreventingBlockClear = 18,
    AfterBlockCleared = 19,

    ShouldPlayerResetEnergy = 20,
    ModifyMaxEnergy = 21,
    AfterEnergyReset = 22,
    AfterEnergyResetLate = 23,
    BeforeHandDraw = 24,
    ModifyHandDraw = 25,
    AfterModifyingHandDraw = 26,

    ShouldDraw = 27,
    ShouldFlush = 28,
    BeforeFlush = 29,
    BeforeFlushLate = 30,
    AfterFlush = 31,
    ShouldEtherealTrigger = 32,
    ShouldTakeExtraTurnEarly = 33,
    ShouldTakeExtraTurn = 34,
    AfterTakingExtraTurn = 35,

    ModifyDamageAdditive = 36,
    ModifyDamageMultiplicative = 37,
    ModifyDamageMultiplicativeLate = 38,
    ModifyDamageCap = 39,
    ModifyHpLostBeforeOsty = 40,
    ModifyHpLostBeforeOstyLate = 41,
    ModifyHpLostAfterOsty = 42,
    ModifyHpLostAfterOstyLate = 43,
    AfterModifyingHpLostBeforeOsty = 44,
    AfterModifyingHpLostAfterOsty = 45,

    ShouldAllowHitting = 46,
    BeforeDamageReceived = 47,
    ModifyPreBlockDamageTarget = 48,
    ModifyUnblockedDamageTarget = 49,
    AfterDamageGiven = 50,
    AfterDamageReceived = 51,
    AfterCurrentHpChanged = 52,

    BeforeAttack = 53,
    ModifyAttackHitCount = 54,
    AfterAttack = 55,

    BeforeBlockGained = 56,
    ModifyBlockAdditive = 57,
    ModifyBlockMultiplicative = 58,
    AfterModifyingBlockAmount = 59,
    AfterBlockGained = 60,
    AfterBlockBroken = 61,

    BeforeDeath = 62,
    ShouldDie = 63,
    ShouldDieLate = 64,
    AfterPreventingDeath = 65,
    AfterDeath = 66,
    ShouldCreatureBeRemovedFromCombatAfterDeath = 67,
    ShouldPowerBeRemovedOnDeath = 68,
    ShouldStopCombatFromEnding = 69,
    AfterDiedToDoom = 70,
    ShouldDisappearFromDoom = 71,

    BeforePowerAmountChanged = 72,
    ModifyPowerAmountGivenAdditive = 73,
    ModifyPowerAmountGivenMultiplicative = 74,
    TryModifyPowerAmountReceived = 75,
    AfterModifyingPowerAmountReceived = 76,
    AfterPowerAmountChanged = 77,

    ShouldPlay = 78,
    BeforeCardPlayed = 79,
    ModifyCardPlayCount = 80,
    AfterModifyingCardPlayCount = 81,
    ModifyCardPlayResultLocation = 82,
    AfterModifyingCardPlayResultLocation = 83,
    AfterCardPlayed = 84,
    AfterCardPlayedLate = 85,

    TryModifyEnergyCostInCombat = 86,
    TryModifyEnergyCostInCombatLate = 87,
    TryModifyStarCostInCombat = 88,
    TryModifyKeywordsInCombat = 89,
    AfterCardChangedPiles = 90,
    AfterCardChangedPilesLate = 91,
    AfterCardDrawnEarly = 92,
    AfterCardDrawn = 93,
    AfterCardDiscarded = 94,
    AfterCardExhausted = 95,
    AfterCardGenerated = 96,
    AfterCardGeneratedForCombat = 97,
    AfterCardEnteredCombat = 98,
    AfterCardOwnerChanged = 99,
    AfterHandEmptied = 100,
    AfterShuffle = 101,

    AfterEnergySpent = 102,
    ShouldGainStars = 103,
    AfterStarsGained = 104,
    AfterStarsSpent = 105,

    ModifyOrbValue = 106,
    ModifyOrbPassiveTriggerCounts = 107,
    AfterModifyingOrbPassiveTriggerCount = 108,
    AfterOrbChanneled = 109,
    AfterOrbEvoked = 110,

    AfterArrowShot = 111,
    AfterArrowImpaled = 112,
    AfterArrowDislodged = 113,

    ModifySummonAmount = 114,
    AfterSummon = 115,
    AfterOstyRevived = 116,

    ShouldProcurePotion = 117,
    BeforePotionUsed = 118,
    AfterPotionUsed = 119,
    AfterPotionProcured = 120,
    AfterPotionDiscarded = 121,

    ModifyGoldGained = 122,
    AfterGoldGained = 123,
    AfterCreatureAddedToCombat = 124,
    AfterForge = 125,

    BeforeApplied = 126,
    AfterApplied = 127,
    AfterRemoved = 128,
    ShouldOwnerDeathTriggerFatal = 129,
    ModifyVulnerableMultiplier = 130,
    ModifyWeakMultiplier = 131,
    ModifyXValue = 132,
    AfterPlayerTurnStartLate = 133,
    AfterAutoPrePlayPhaseEnteredLate = 134,
}

/// 192-bit bitmask covering every combat hook slot for O(1) listener skipping.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Default)]
pub struct HookMask(pub [u64; 3]);

impl HookMask {
    pub const EMPTY: Self = Self([0, 0, 0]);

    #[inline(always)]
    pub const fn with(mut self, slot: HookSlot) -> Self {
        let idx = slot as usize;
        self.0[idx / 64] |= 1u64 << (idx % 64);
        self
    }

    #[inline(always)]
    pub const fn contains(self, slot: HookSlot) -> bool {
        let idx = slot as usize;
        (self.0[idx / 64] & (1u64 << (idx % 64))) != 0
    }

    #[inline(always)]
    pub const fn slot_parts(slot: HookSlot) -> (usize, u64) {
        let idx = slot as usize;
        (idx >> 6, 1u64 << (idx & 63))
    }

    #[inline(always)]
    pub const fn contains_slot_parts(self, word: usize, bit: u64) -> bool {
        (self.0[word] & bit) != 0
    }

    #[inline(always)]
    pub const fn union(self, other: Self) -> Self {
        Self([
            self.0[0] | other.0[0],
            self.0[1] | other.0[1],
            self.0[2] | other.0[2],
        ])
    }

    #[inline(always)]
    pub const fn intersects(self, other: Self) -> bool {
        ((self.0[0] & other.0[0]) | (self.0[1] & other.0[1]) | (self.0[2] & other.0[2])) != 0
    }

    #[inline(always)]
    pub const fn is_empty(self) -> bool {
        (self.0[0] | self.0[1] | self.0[2]) == 0
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hook_mask_round_trip() {
        let mask = HookMask::EMPTY
            .with(HookSlot::ModifyDamageAdditive)
            .with(HookSlot::AfterForge);
        assert!(mask.contains(HookSlot::ModifyDamageAdditive));
        assert!(mask.contains(HookSlot::AfterForge));
        assert!(!mask.contains(HookSlot::ShouldDie));
    }
}
