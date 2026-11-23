pub mod dec;
pub mod flags;
pub mod generated_ids;
pub mod hooks;
pub mod ids;
pub mod rng;

pub use dec::{Dec, DecError};
pub use flags::{
    CardMeta, CardStaticFlags, MonsterMeta, OrbMeta, PotionMeta, PowerMeta, PowerStaticFlags,
    RelicMeta, RelicStaticFlags,
};
pub use generated_ids::{
    AfflictionId, CardId, CardPartition, CARD_POOL_COLORLESS, CARD_POOL_CURSE, CARD_POOL_DEFECT,
    CARD_POOL_FOOL, CARD_POOL_IRONCLAD, CARD_POOL_NECROBINDER, CARD_POOL_REGENT, CARD_POOL_SILENT,
    CARD_POOL_STATUS, CharacterId, DominionId, GAME_VERSION, MonsterId, OrbId, PotionId, PowerId,
    RelicId, RunModelId, card_partition, generated_card_meta, generated_power_meta,
    generated_relic_meta,
};

pub const fn character_card_pool(character: CharacterId) -> &'static [CardId] {
    match character {
        CharacterId::Defect => CARD_POOL_DEFECT,
        CharacterId::Fool => CARD_POOL_FOOL,
        CharacterId::Ironclad => CARD_POOL_IRONCLAD,
        CharacterId::Necrobinder => CARD_POOL_NECROBINDER,
        CharacterId::Regent => CARD_POOL_REGENT,
        CharacterId::Silent => CARD_POOL_SILENT,
    }
}

pub static TAROT_CARDS: &[CardId] = &[
    CardId::Judgement,
    CardId::TheFool,
    CardId::TheHighPriestess,
    CardId::TheEmperor,
    CardId::TheEmpress,
    CardId::TheHangedMan,
    CardId::TheHierophant,
    CardId::TheChariot,
    CardId::Justice,
    CardId::TheMagician,
    CardId::TheMoon,
    CardId::TheTower,
    CardId::Strength,
    CardId::Death,
    CardId::Temperance,
    CardId::TheDevil,
    CardId::TheHermit,
    CardId::TheLovers,
    CardId::TheStar,
    CardId::TheSun,
    CardId::TheWorld,
    CardId::WheelOfFortune,
];
pub use hooks::{HookMask, HookSlot};
pub use ids::{
    ArrowStatus, AscensionLevel, CardKeywords, CardPilePosition, CardRarity, CardTags, CardType,
    CombatSide, EnchantmentId, EnchantmentStatus, GameMode, LocalEnergyModifierKind, PileType,
    PlayerTurnPhase, PowerInstanceType, PowerStackType, PowerType, RoomType, TargetType, ValueProp,
    blitz_ascension_level, is_ascension_active_in_mode,
};
pub use rng::{
    MegaRandom, PlayerRngSet, PlayerRngType, Rng, RngError, RngWords, RunRngSet, RunRngType,
    deterministic_hash_code, deterministic_hash_code_old, snake_case,
};
