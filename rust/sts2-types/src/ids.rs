use bitflags::bitflags;
use serde::{Deserialize, Serialize};

pub use crate::generated_ids::EnchantmentId;

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(u8)]
pub enum CombatSide {
    Player = 0,
    Enemy = 1,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(u8)]
pub enum PlayerTurnPhase {
    None = 0,
    Start = 1,
    AutoPrePlay = 2,
    Play = 3,
    AutoPostPlay = 4,
    End = 5,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(u8)]
pub enum RoomType {
    Unassigned = 0,
    Monster = 1,
    Elite = 2,
    Boss = 3,
    Treasure = 4,
    Shop = 5,
    Event = 6,
    RestSite = 7,
    Map = 8,
}

impl RoomType {
    #[inline]
    pub const fn is_combat_room(self) -> bool {
        matches!(self, Self::Monster | Self::Elite | Self::Boss)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(u8)]
pub enum GameMode {
    Standard = 0,
    Dread = 1,
    Blitz = 2,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(u8)]
pub enum AscensionLevel {
    None = 0,
    SwarmingElites = 1,
    WearyTraveler = 2,
    Poverty = 3,
    TightBelt = 4,
    AscendersBane = 5,
    Inflation = 6,
    Scarcity = 7,
    ToughEnemies = 8,
    DeadlyEnemies = 9,
    DoubleBoss = 10,
}

pub const fn blitz_ascension_level(blitz_level: u8) -> Option<AscensionLevel> {
    match blitz_level {
        0 => Some(AscensionLevel::None),
        1 => Some(AscensionLevel::WearyTraveler),
        2 => Some(AscensionLevel::TightBelt),
        3 => Some(AscensionLevel::ToughEnemies),
        4 => Some(AscensionLevel::DeadlyEnemies),
        5 => Some(AscensionLevel::AscendersBane),
        _ => None,
    }
}

pub const fn is_ascension_active_in_mode(
    mode: GameMode,
    ascension: u8,
    level: AscensionLevel,
) -> bool {
    match mode {
        GameMode::Blitz => {
            if matches!(level, AscensionLevel::None) {
                return true;
            }
            let mut i = 1u8;
            while i <= ascension && i <= 5 {
                if let Some(mapped) = blitz_ascension_level(i) {
                    if mapped as u8 == level as u8 {
                        return true;
                    }
                }
                i += 1;
            }
            false
        }
        GameMode::Dread => ascension >= level as u8 && !matches!(level, AscensionLevel::None),
        GameMode::Standard => ascension >= level as u8,
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(u8)]
pub enum CardType {
    None = 0,
    Attack = 1,
    Skill = 2,
    Power = 3,
    Status = 4,
    Curse = 5,
    Quest = 6,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(u8)]
pub enum CardRarity {
    None = 0,
    Basic = 1,
    Common = 2,
    Uncommon = 3,
    Rare = 4,
    Curse = 5,
    Status = 6,
    Event = 7,
    Ancient = 8,
    Token = 9,
    Quest = 10,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(u8)]
pub enum TargetType {
    None = 0,
    SelfTarget = 1,
    AnyEnemy = 2,
    AllEnemies = 3,
    RandomEnemy = 4,
    AnyPlayer = 5,
    AnyAlly = 6,
    AllAllies = 7,
    TargetedNoCreature = 8,
    Osty = 9,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(u8)]
pub enum PileType {
    None = 0,
    Draw = 1,
    Hand = 2,
    Discard = 3,
    Exhaust = 4,
    Play = 5,
    Deck = 6,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(u8)]
pub enum CardPilePosition {
    Bottom = 0,
    Top = 1,
    Random = 2,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(u8)]
pub enum PowerType {
    None = 0,
    Buff = 1,
    Debuff = 2,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(u8)]
pub enum PowerStackType {
    None = 0,
    Counter = 1,
    Single = 2,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(u8)]
pub enum PowerInstanceType {
    None = 0,
    Instanced = 1,
    InstancedPerApplier = 2,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(u8)]
pub enum ArrowStatus {
    Retrieved = 0,
    Away = 1,
    Destroyed = 2,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(u8)]
pub enum EnchantmentStatus {
    Normal = 0,
    Disabled = 1,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(u8)]
pub enum LocalEnergyModifierKind {
    FreeThisTurn = 0,
    FreeUntilPlayed = 1,
    FreeThisCombat = 2,
    SetThisTurn = 3,
    SetUntilPlayed = 4,
    SetThisTurnOrUntilPlayed = 5,
    SetThisCombat = 6,
    AddThisCombat = 7,
    AddThisTurn = 8,
    AddUntilPlayed = 9,
    AddThisTurnOrUntilPlayed = 10,
}

impl LocalEnergyModifierKind {
    #[inline]
    pub const fn is_absolute(self) -> bool {
        matches!(
            self,
            Self::FreeThisTurn
                | Self::FreeUntilPlayed
                | Self::FreeThisCombat
                | Self::SetThisTurn
                | Self::SetUntilPlayed
                | Self::SetThisTurnOrUntilPlayed
                | Self::SetThisCombat
        )
    }

    #[inline]
    pub const fn clears_after_play(self) -> bool {
        matches!(
            self,
            Self::FreeUntilPlayed
                | Self::SetUntilPlayed
                | Self::SetThisTurnOrUntilPlayed
                | Self::AddUntilPlayed
                | Self::AddThisTurnOrUntilPlayed
        )
    }

    #[inline]
    pub const fn clears_at_end_of_turn(self) -> bool {
        matches!(
            self,
            Self::FreeThisTurn
                | Self::SetThisTurn
                | Self::SetThisTurnOrUntilPlayed
                | Self::AddThisTurn
                | Self::AddThisTurnOrUntilPlayed
        )
    }
}

bitflags! {
    #[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Default, Serialize, Deserialize)]
    pub struct CardKeywords: u16 {
        const EXHAUST    = 1 << 0;
        const ETHEREAL   = 1 << 1;
        const INNATE     = 1 << 2;
        const RETAIN     = 1 << 3;
        const SLY        = 1 << 4;
        const UNPLAYABLE = 1 << 5;
        const ETERNAL    = 1 << 6;
    }
}

impl CardKeywords {
    pub fn from_keyword_name(name: &str) -> Option<Self> {
        match name {
            "Exhaust" => Some(Self::EXHAUST),
            "Ethereal" => Some(Self::ETHEREAL),
            "Innate" => Some(Self::INNATE),
            "Retain" => Some(Self::RETAIN),
            "Sly" => Some(Self::SLY),
            "Unplayable" => Some(Self::UNPLAYABLE),
            "Eternal" => Some(Self::ETERNAL),
            _ => None,
        }
    }

    pub const fn as_name(self) -> Option<&'static str> {
        match self {
            Self::EXHAUST => Some("Exhaust"),
            Self::ETHEREAL => Some("Ethereal"),
            Self::INNATE => Some("Innate"),
            Self::RETAIN => Some("Retain"),
            Self::SLY => Some("Sly"),
            Self::UNPLAYABLE => Some("Unplayable"),
            Self::ETERNAL => Some("Eternal"),
            _ => None,
        }
    }
}

bitflags! {
    #[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Default, Serialize, Deserialize)]
    pub struct CardTags: u16 {
        const STRIKE      = 1 << 0;
        const DEFEND      = 1 << 1;
        const SHIV        = 1 << 2;
        const OSTY_ATTACK = 1 << 3;
        const SHOOT       = 1 << 4;
        const WAH         = 1 << 5;
        const CHO         = 1 << 6;
        const KI          = 1 << 7;
        const MINION      = 1 << 8;
    }
}

bitflags! {
    #[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Default, Serialize, Deserialize)]
    pub struct ValueProp: u8 {
        const UNBLOCKABLE    = 2;
        const UNPOWERED      = 4;
        const MOVE           = 8;
        const SKIP_HURT_ANIM = 0x10;
        const FROM_ARROW     = 0x20;
    }
}

impl ValueProp {
    #[inline]
    pub const fn is_powered_attack(self) -> bool {
        self.contains(Self::MOVE) && !self.contains(Self::UNPOWERED)
    }

    #[inline]
    pub const fn is_powered_block(self) -> bool {
        self.contains(Self::MOVE) && !self.contains(Self::UNPOWERED)
    }
}
