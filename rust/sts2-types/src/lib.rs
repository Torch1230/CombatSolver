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

static CARD_MODEL_ENTRY_SORT_RANKS: std::sync::OnceLock<[u16; CardId::COUNT]> =
    std::sync::OnceLock::new();

impl CardId {
    #[inline]
    pub fn model_entry_sort_rank(self) -> u16 {
        let ranks = CARD_MODEL_ENTRY_SORT_RANKS.get_or_init(|| {
            let mut entries: Vec<(usize, &'static str)> = (0..CardId::COUNT)
                .map(|idx| {
                    // SAFETY: `CardId` is `#[repr(u16)]` with contiguous discriminants `0..CardId::COUNT`.
                    let cid = unsafe { core::mem::transmute::<u16, CardId>(idx as u16) };
                    (idx, cid.model_entry())
                })
                .collect();
            entries.sort_by(|a, b| a.1.as_bytes().cmp(b.1.as_bytes()));
            let mut out = [0u16; CardId::COUNT];
            let mut rank = 0u16;
            for i in 0..entries.len() {
                if i > 0 && entries[i].1.as_bytes() != entries[i - 1].1.as_bytes() {
                    rank += 1;
                }
                out[entries[i].0] = rank;
            }
            out
        });
        ranks[self as usize]
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn card_model_entry_sort_rank_isomorphic_to_compare_model_id() {
        for i in 0..CardId::COUNT {
            let a = unsafe { core::mem::transmute::<u16, CardId>(i as u16) };
            for j in (i + 1)..CardId::COUNT {
                let b = unsafe { core::mem::transmute::<u16, CardId>(j as u16) };
                assert_eq!(
                    a.model_entry_sort_rank().cmp(&b.model_entry_sort_rank()),
                    a.compare_model_id(b)
                );
            }
        }
    }
}
