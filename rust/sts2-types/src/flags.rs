use bitflags::bitflags;
use crate::generated_ids::OrbId;
use crate::hooks::HookMask;
use crate::ids::{
    CardKeywords, CardRarity, CardTags, CardType, PowerInstanceType, PowerStackType, PowerType,
    TargetType,
};

bitflags! {
    #[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Default)]
    pub struct CardStaticFlags: u32 {
        const MAY_HEAL                  = 1 << 0;
        const HAS_GROWTH                = 1 << 1;
        const ORDER_SENSITIVE           = 1 << 2;
        const USES_RNG                  = 1 << 3;
        const GAINS_BLOCK               = 1 << 4;
        const CAN_BE_GENERATED_IN_COMBAT = 1 << 5;
        const CAN_BE_GENERATED_BY_MODS  = 1 << 6;
        const IS_COLORLESS              = 1 << 7;
        const IS_MULTIPLAYER_ONLY       = 1 << 8;
        const IS_X_ENERGY_COST          = 1 << 9;
        const IS_X_STAR_COST            = 1 << 10;
        const HAS_TURN_END_IN_HAND      = 1 << 11;
        const IS_ALLOWED_IN_BLITZ       = 1 << 12;
    }
}

bitflags! {
    #[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Default)]
    pub struct PowerStaticFlags: u32 {
        const MAY_HEAL                   = 1 << 0;
        const HAS_GROWTH                 = 1 << 1;
        const ORDER_SENSITIVE            = 1 << 2;
        const USES_RNG                   = 1 << 3;
        const ALLOW_NEGATIVE             = 1 << 4;
        const OWNER_IS_SECONDARY_ENEMY   = 1 << 5;
        const REMOVE_AFTER_OWNER_DEATH   = 1 << 6;
        const IS_TEMPORARY               = 1 << 7;
    }
}

bitflags! {
    #[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Default)]
    pub struct RelicStaticFlags: u32 {
        const MAY_HEAL        = 1 << 0;
        const HAS_GROWTH      = 1 << 1;
        const ORDER_SENSITIVE = 1 << 2;
        const USES_RNG        = 1 << 3;
        const IS_STACKABLE    = 1 << 4;
        const ADDS_PET        = 1 << 5;
    }
}

#[derive(Clone, Copy, Debug)]
pub struct CardMeta {
    pub card_type: CardType,
    pub rarity: CardRarity,
    pub target_type: TargetType,
    pub canonical_energy_cost: i32,
    pub canonical_star_cost: i32,
    pub max_upgrade_level: i32,
    pub base_keywords: CardKeywords,
    pub base_tags: CardTags,
    pub flags: CardStaticFlags,
    pub hook_mask: HookMask,
}

#[derive(Clone, Copy, Debug)]
pub struct PowerMeta {
    pub power_type: PowerType,
    pub stack_type: PowerStackType,
    pub instance_type: PowerInstanceType,
    pub flags: PowerStaticFlags,
    pub hook_mask: HookMask,
}

#[derive(Clone, Copy, Debug)]
pub struct RelicMeta {
    pub flags: RelicStaticFlags,
    pub hook_mask: HookMask,
}

#[derive(Clone, Copy, Debug)]
pub struct PotionMeta {
    pub target_type: TargetType,
    pub uses_rng: bool,
    pub may_heal: bool,
    pub hook_mask: HookMask,
}

#[derive(Clone, Copy, Debug)]
pub struct MonsterMeta {
    pub hook_mask: HookMask,
}

#[derive(Clone, Copy, Debug)]
pub struct OrbMeta {
    pub orb_id: OrbId,
    pub hook_mask: HookMask,
}
