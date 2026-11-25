use serde::Deserialize;
use std::collections::BTreeMap;
use std::fmt::Write as _;
use std::fs;
use std::path::Path;

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OracleMetadataManifest {
    pub meta: OracleMetadataHeader,
    pub characters: Vec<CharacterExportEntry>,
    pub cards: Vec<CardExportEntry>,
    pub powers: Vec<PowerExportEntry>,
    pub relics: Vec<RelicExportEntry>,
    pub potions: Vec<PotionExportEntry>,
    pub monsters: Vec<MonsterExportEntry>,
    pub orbs: Vec<OrbExportEntry>,
    pub enchantments: Vec<EnchantmentExportEntry>,
    pub afflictions: Vec<AfflictionExportEntry>,
    pub dominions: Vec<DominionExportEntry>,
    pub run_models: Vec<RunModelExportEntry>,
    pub pools: PoolsExportEntry,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OracleMetadataHeader {
    pub game_version: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HookParticipationExport {
    #[serde(default)]
    pub overridden_hook_slots: Vec<String>,
    #[serde(default)]
    pub cost_hook_mask: u8,
    #[serde(default)]
    pub pile_hook_mask: u8,
    #[serde(default)]
    pub overridden_hooks: Vec<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CharacterExportEntry {
    pub id: String,
    pub class_name: String,
    pub is_playable: bool,
    pub starting_hp: i32,
    pub starting_gold: i32,
    pub max_energy: i32,
    pub base_orb_slot_count: i32,
    pub base_arrow_slot_count: i32,
    pub starting_deck_card_ids: Vec<String>,
    pub starting_relic_ids: Vec<String>,
}

fn default_true() -> bool {
    true
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CardLevelExport {
    #[serde(default)]
    pub keywords: Vec<String>,
    #[serde(default)]
    pub tags: Vec<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CardExportEntry {
    pub id: String,
    pub class_name: String,
    pub rarity: String,
    #[serde(rename = "type")]
    pub card_type: String,
    pub target_type: String,
    pub is_colorless: bool,
    pub is_multiplayer_only: bool,
    #[serde(default = "default_true")]
    pub is_allowed_in_blitz: bool,
    pub can_be_generated_by_modifiers: bool,
    pub can_be_generated_in_combat: bool,
    pub gains_block: bool,
    pub max_upgrade_level: i32,
    pub canonical_energy_cost: i32,
    pub costs_x_energy: bool,
    pub canonical_star_cost: i32,
    pub costs_x_star: bool,
    pub hooks: HookParticipationExport,
    pub level0: CardLevelExport,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PowerExportEntry {
    pub id: String,
    pub class_name: String,
    pub power_type: String,
    pub stack_type: String,
    pub instance_type: String,
    pub allow_negative: bool,
    pub owner_is_secondary_enemy: bool,
    pub hooks: HookParticipationExport,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RelicExportEntry {
    pub id: String,
    pub class_name: String,
    pub rarity: String,
    pub adds_pet: bool,
    pub is_stackable: bool,
    pub hooks: HookParticipationExport,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PotionExportEntry {
    pub id: String,
    pub class_name: String,
    pub target_type: String,
    pub can_be_generated_in_combat: bool,
    pub hooks: HookParticipationExport,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MonsterExportEntry {
    pub id: String,
    pub class_name: String,
    pub min_initial_hp: i32,
    pub max_initial_hp: i32,
    pub hooks: HookParticipationExport,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OrbExportEntry {
    pub id: String,
    pub class_name: String,
    pub hooks: HookParticipationExport,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EnchantmentExportEntry {
    pub id: String,
    pub class_name: String,
    pub hooks: HookParticipationExport,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AfflictionExportEntry {
    pub id: String,
    pub class_name: String,
    pub hooks: HookParticipationExport,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DominionExportEntry {
    pub id: String,
    pub class_name: String,
    pub hooks: HookParticipationExport,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RunModelExportEntry {
    pub id: String,
    pub class_name: String,
    pub hooks: HookParticipationExport,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NamedPoolExport {
    pub pool_name: String,
    pub ids: Vec<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PoolsExportEntry {
    pub card_pools: Vec<NamedPoolExport>,
    pub potion_pools: Vec<NamedPoolExport>,
    pub relic_pools: Vec<NamedPoolExport>,
    pub orb_pool: Vec<String>,
}

fn split_model_id(full_id: &str) -> (&str, &str) {
    match full_id.split_once('.') {
        Some((cat, entry)) => (cat, entry),
        None => ("", full_id),
    }
}

fn emit_id_enum<'a, T, FId, FClass>(
    out: &mut String,
    enum_name: &str,
    category: &str,
    repr_ty: &str,
    items: &'a [T],
    get_id: FId,
    get_class: FClass,
    with_compare: bool,
) -> Vec<(String, &'a T)>
where
    FId: Fn(&'a T) -> &'a str,
    FClass: Fn(&'a T) -> &'a str,
{
    let mut sorted: BTreeMap<String, (&'a str, &'a T)> = BTreeMap::new();
    for item in items {
        let (_, entry) = split_model_id(get_id(item));
        sorted.insert(entry.to_owned(), (get_class(item), item));
    }

    let _ = writeln!(
        out,
        "#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]"
    );
    let _ = writeln!(out, "#[repr({repr_ty})]");
    let _ = writeln!(out, "pub enum {enum_name} {{");
    for (idx, (_entry, (class_name, _))) in sorted.iter().enumerate() {
        let _ = writeln!(out, "    {class_name} = {idx},");
    }
    let _ = writeln!(out, "}}\n");

    let _ = writeln!(out, "impl {enum_name} {{");
    let _ = writeln!(out, "    pub const COUNT: usize = {};", sorted.len());
    let _ = writeln!(out, "    pub const fn model_entry(self) -> &'static str {{");
    let _ = writeln!(out, "        match self {{");
    for (entry, (class_name, _)) in &sorted {
        let _ = writeln!(out, "            Self::{class_name} => \"{entry}\",");
    }
    let _ = writeln!(out, "        }}");
    let _ = writeln!(out, "    }}\n");

    let _ = writeln!(out, "    pub const fn model_id_str(self) -> &'static str {{");
    let _ = writeln!(out, "        match self {{");
    for (entry, (class_name, _)) in &sorted {
        let _ = writeln!(
            out,
            "            Self::{class_name} => \"{category}.{entry}\","
        );
    }
    let _ = writeln!(out, "        }}");
    let _ = writeln!(out, "    }}\n");

    let _ = writeln!(
        out,
        "    pub fn from_model_id_str(s: &str) -> Option<Self> {{"
    );
    let _ = writeln!(
        out,
        "        let entry = s.strip_prefix(\"{category}.\").unwrap_or(s);"
    );
    let _ = writeln!(out, "        match entry {{");
    for (entry, (class_name, _)) in &sorted {
        let _ = writeln!(out, "            \"{entry}\" => Some(Self::{class_name}),");
    }
    let _ = writeln!(out, "            _ => None,");
    let _ = writeln!(out, "        }}");
    let _ = writeln!(out, "    }}");

    if with_compare {
        let _ = writeln!(
            out,
            "\n    #[inline]\n    pub fn compare_model_id(self, other: Self) -> Ordering {{\n        self.model_entry().as_bytes().cmp(other.model_entry().as_bytes())\n    }}"
        );
    }
    let _ = writeln!(out, "}}\n");

    sorted
        .into_iter()
        .map(|(_, (class_name, item))| (class_name.to_owned(), item))
        .collect()
}

fn hook_mask_expr(hooks: &HookParticipationExport) -> String {
    let mut parts = Vec::new();
    for slot in &hooks.overridden_hook_slots {
        parts.push(format!("HookSlot::{slot}"));
    }
    if (hooks.cost_hook_mask & 1) != 0 {
        parts.push("HookSlot::TryModifyEnergyCostInCombat".to_owned());
    }
    if (hooks.cost_hook_mask & 2) != 0 {
        parts.push("HookSlot::TryModifyEnergyCostInCombatLate".to_owned());
    }
    if (hooks.cost_hook_mask & 4) != 0 {
        parts.push("HookSlot::TryModifyStarCostInCombat".to_owned());
    }
    if (hooks.pile_hook_mask & 1) != 0 {
        parts.push("HookSlot::AfterCardChangedPiles".to_owned());
    }
    if (hooks.pile_hook_mask & 2) != 0 {
        parts.push("HookSlot::AfterCardChangedPilesLate".to_owned());
    }
    for name in &hooks.overridden_hooks {
        let mapped = match name.as_str() {
            "BeforeCombatStart" => Some("HookSlot::BeforeCombatStart"),
            "BeforeCombatStartLate" => Some("HookSlot::BeforeCombatStartLate"),
            "AfterCombatVictoryEarly" => Some("HookSlot::AfterCombatVictoryEarly"),
            "AfterCombatVictory" => Some("HookSlot::AfterCombatVictory"),
            "AfterCombatEnd" => Some("HookSlot::AfterCombatEnd"),
            "AfterRoomEntered" => Some("HookSlot::AfterRoomEntered"),
            "BeforeSideTurnStart" => Some("HookSlot::BeforeSideTurnStart"),
            "AfterPlayerTurnStartEarly" => Some("HookSlot::AfterPlayerTurnStartEarly"),
            "AfterPlayerTurnStart" => Some("HookSlot::AfterPlayerTurnStart"),
            "AfterPlayerTurnStartLate" => Some("HookSlot::AfterPlayerTurnStartLate"),
            "AfterAutoPrePlayPhaseEntered" => Some("HookSlot::AfterAutoPrePlayPhaseEntered"),
            "AfterAutoPrePlayPhaseEnteredLate" => Some("HookSlot::AfterAutoPrePlayPhaseEnteredLate"),
            "AfterAutoPostPlayPhaseEntered" => Some("HookSlot::AfterAutoPostPlayPhaseEntered"),
            "ShouldPlayerResetEnergy" => Some("HookSlot::ShouldPlayerResetEnergy"),
            "ModifyMaxEnergy" => Some("HookSlot::ModifyMaxEnergy"),
            "ModifyXValue" => Some("HookSlot::ModifyXValue"),
            "AfterEnergyReset" => Some("HookSlot::AfterEnergyReset"),
            "AfterEnergyResetLate" => Some("HookSlot::AfterEnergyResetLate"),
            "BeforeHandDraw" => Some("HookSlot::BeforeHandDraw"),
            "ModifyHandDraw" => Some("HookSlot::ModifyHandDraw"),
            "AfterModifyingHandDraw" => Some("HookSlot::AfterModifyingHandDraw"),
            "ShouldDraw" => Some("HookSlot::ShouldDraw"),
            "ShouldFlush" => Some("HookSlot::ShouldFlush"),
            "AfterFlush" => Some("HookSlot::AfterFlush"),
            "ShouldEtherealTrigger" => Some("HookSlot::ShouldEtherealTrigger"),
            "AfterTakingExtraTurn" => Some("HookSlot::AfterTakingExtraTurn"),
            "AfterPreventingBlockClear" => Some("HookSlot::AfterPreventingBlockClear"),
            "BeforeDeath" => Some("HookSlot::BeforeDeath"),
            "ShouldDie" => Some("HookSlot::ShouldDie"),
            "ShouldDieLate" => Some("HookSlot::ShouldDieLate"),
            "AfterPreventingDeath" => Some("HookSlot::AfterPreventingDeath"),
            "AfterDeath" => Some("HookSlot::AfterDeath"),
            "ShouldCreatureBeRemovedFromCombatAfterDeath" => {
                Some("HookSlot::ShouldCreatureBeRemovedFromCombatAfterDeath")
            }
            "ShouldPowerBeRemovedOnDeath" => Some("HookSlot::ShouldPowerBeRemovedOnDeath"),
            "ShouldStopCombatFromEnding" => Some("HookSlot::ShouldStopCombatFromEnding"),
            "AfterDiedToDoom" => Some("HookSlot::AfterDiedToDoom"),
            "ShouldDisappearFromDoom" => Some("HookSlot::ShouldDisappearFromDoom"),
            "ShouldPlay" => Some("HookSlot::ShouldPlay"),
            "BeforeCardPlayed" => Some("HookSlot::BeforeCardPlayed"),
            "ModifyCardPlayCount" => Some("HookSlot::ModifyCardPlayCount"),
            "AfterModifyingCardPlayCount" => Some("HookSlot::AfterModifyingCardPlayCount"),
            "ModifyCardPlayResultLocation" => Some("HookSlot::ModifyCardPlayResultLocation"),
            "AfterModifyingCardPlayResultLocation" => {
                Some("HookSlot::AfterModifyingCardPlayResultLocation")
            }
            "AfterCardPlayed" => Some("HookSlot::AfterCardPlayed"),
            "AfterCardPlayedLate" => Some("HookSlot::AfterCardPlayedLate"),
            "TryModifyKeywordsInCombat" => Some("HookSlot::TryModifyKeywordsInCombat"),
            "AfterCardDrawnEarly" => Some("HookSlot::AfterCardDrawnEarly"),
            "AfterCardDrawn" => Some("HookSlot::AfterCardDrawn"),
            "AfterCardDiscarded" => Some("HookSlot::AfterCardDiscarded"),
            "AfterCardExhausted" => Some("HookSlot::AfterCardExhausted"),
            "AfterCardGenerated" => Some("HookSlot::AfterCardGenerated"),
            "AfterCardGeneratedForCombat" => Some("HookSlot::AfterCardGeneratedForCombat"),
            "AfterCardEnteredCombat" => Some("HookSlot::AfterCardEnteredCombat"),
            "AfterCardOwnerChanged" => Some("HookSlot::AfterCardOwnerChanged"),
            "AfterHandEmptied" => Some("HookSlot::AfterHandEmptied"),
            "AfterShuffle" => Some("HookSlot::AfterShuffle"),
            "AfterEnergySpent" => Some("HookSlot::AfterEnergySpent"),
            "ModifyEnergyGain" => Some("HookSlot::ModifyEnergyGain"),
            "AfterModifyingEnergyGain" => Some("HookSlot::AfterModifyingEnergyGain"),
            "ShouldGainStars" => Some("HookSlot::ShouldGainStars"),
            "AfterStarsGained" => Some("HookSlot::AfterStarsGained"),
            "AfterStarsSpent" => Some("HookSlot::AfterStarsSpent"),
            "ModifyOrbValue" => Some("HookSlot::ModifyOrbValue"),
            "ModifyOrbPassiveTriggerCounts" => Some("HookSlot::ModifyOrbPassiveTriggerCounts"),
            "AfterModifyingOrbPassiveTriggerCount" => {
                Some("HookSlot::AfterModifyingOrbPassiveTriggerCount")
            }
            "AfterOrbChanneled" => Some("HookSlot::AfterOrbChanneled"),
            "AfterOrbEvoked" => Some("HookSlot::AfterOrbEvoked"),
            "AfterArrowShot" => Some("HookSlot::AfterArrowShot"),
            "AfterArrowImpaled" => Some("HookSlot::AfterArrowImpaled"),
            "AfterArrowDislodged" => Some("HookSlot::AfterArrowDislodged"),
            "ModifySummonAmount" => Some("HookSlot::ModifySummonAmount"),
            "AfterSummon" => Some("HookSlot::AfterSummon"),
            "AfterOstyRevived" => Some("HookSlot::AfterOstyRevived"),
            "ShouldProcurePotion" => Some("HookSlot::ShouldProcurePotion"),
            "BeforePotionUsed" => Some("HookSlot::BeforePotionUsed"),
            "AfterPotionUsed" => Some("HookSlot::AfterPotionUsed"),
            "AfterPotionProcured" => Some("HookSlot::AfterPotionProcured"),
            "AfterPotionDiscarded" => Some("HookSlot::AfterPotionDiscarded"),
            "ModifyGoldGained" => Some("HookSlot::ModifyGoldGained"),
            "AfterGoldGained" => Some("HookSlot::AfterGoldGained"),
            "AfterCreatureAddedToCombat" => Some("HookSlot::AfterCreatureAddedToCombat"),
            "AfterForge" => Some("HookSlot::AfterForge"),
            "BeforeBlockGained" => Some("HookSlot::BeforeBlockGained"),
            "AfterModifyingBlockAmount" => Some("HookSlot::AfterModifyingBlockAmount"),
            "AfterBlockGained" => Some("HookSlot::AfterBlockGained"),
            "AfterBlockBroken" => Some("HookSlot::AfterBlockBroken"),
            "BeforeApplied" => Some("HookSlot::BeforeApplied"),
            "AfterApplied" => Some("HookSlot::AfterApplied"),
            "AfterRemoved" => Some("HookSlot::AfterRemoved"),
            "ShouldOwnerDeathTriggerFatal" => Some("HookSlot::ShouldOwnerDeathTriggerFatal"),
            "ModifyVulnerableMultiplier" => Some("HookSlot::ModifyVulnerableMultiplier"),
            "ModifyWeakMultiplier" => Some("HookSlot::ModifyWeakMultiplier"),
            _ => None,
        };
        if let Some(m) = mapped {
            let s = m.to_owned();
            if !parts.contains(&s) {
                parts.push(s);
            }
        }
    }
    if parts.is_empty() {
        "HookMask::EMPTY".to_owned()
    } else {
        let mut s = "HookMask::EMPTY".to_owned();
        for p in parts {
            let _ = write!(s, ".with({p})");
        }
        s
    }
}

fn map_target_type(s: &str) -> &'static str {
    match s {
        "None" => "TargetType::None",
        "Self" => "TargetType::SelfTarget",
        "AnyEnemy" => "TargetType::AnyEnemy",
        "AllEnemies" => "TargetType::AllEnemies",
        "RandomEnemy" => "TargetType::RandomEnemy",
        "AnyPlayer" => "TargetType::AnyPlayer",
        "AnyAlly" => "TargetType::AnyAlly",
        "AllAllies" => "TargetType::AllAllies",
        "TargetedNoCreature" => "TargetType::TargetedNoCreature",
        "Osty" => "TargetType::Osty",
        _ => "TargetType::None",
    }
}

pub fn render_generated_module(manifest: &OracleMetadataManifest) -> String {
    let mut out = String::with_capacity(256 * 1024);
    let _ = writeln!(
        out,
        "// Generated deterministically by gen_metadata (gameVersion={}).\n",
        manifest.meta.game_version
    );
    let _ = writeln!(out, "use core::cmp::Ordering;");
    let _ = writeln!(out, "use serde::{{Deserialize, Serialize}};");
    let _ = writeln!(out, "use crate::flags::*;");
    let _ = writeln!(out, "use crate::hooks::{{HookMask, HookSlot}};");
    let _ = writeln!(out, "use crate::ids::*;\n");
    let _ = writeln!(
        out,
        "pub const GAME_VERSION: &str = \"{}\";\n",
        manifest.meta.game_version
    );

    let chars = emit_id_enum(
        &mut out,
        "CharacterId",
        "CHARACTER",
        "u8",
        &manifest.characters,
        |x| &x.id,
        |x| &x.class_name,
        false,
    );
    let _ = writeln!(out, "impl CharacterId {{");
    let _ = writeln!(out, "    pub const fn starting_hp(self) -> i32 {{");
    let _ = writeln!(out, "        match self {{");
    for (cls, c) in &chars {
        let _ = writeln!(out, "            Self::{cls} => {},", c.starting_hp);
    }
    let _ = writeln!(out, "        }}\n    }}");
    let _ = writeln!(out, "    pub const fn starting_gold(self) -> i32 {{");
    let _ = writeln!(out, "        match self {{");
    for (cls, c) in &chars {
        let _ = writeln!(out, "            Self::{cls} => {},", c.starting_gold);
    }
    let _ = writeln!(out, "        }}\n    }}");
    let _ = writeln!(out, "    pub const fn max_energy(self) -> i32 {{");
    let _ = writeln!(out, "        match self {{");
    for (cls, c) in &chars {
        let _ = writeln!(out, "            Self::{cls} => {},", c.max_energy);
    }
    let _ = writeln!(out, "        }}\n    }}");
    let _ = writeln!(out, "    pub const fn base_orb_slot_count(self) -> i32 {{");
    let _ = writeln!(out, "        match self {{");
    for (cls, c) in &chars {
        let _ = writeln!(out, "            Self::{cls} => {},", c.base_orb_slot_count);
    }
    let _ = writeln!(out, "        }}\n    }}");
    let _ = writeln!(out, "    pub const fn base_arrow_slot_count(self) -> i32 {{");
    let _ = writeln!(out, "        match self {{");
    for (cls, c) in &chars {
        let _ = writeln!(out, "            Self::{cls} => {},", c.base_arrow_slot_count);
    }
    let _ = writeln!(out, "        }}\n    }}");
    let _ = writeln!(out, "}}\n");

    let cards = emit_id_enum(
        &mut out,
        "CardId",
        "CARD",
        "u16",
        &manifest.cards,
        |x| &x.id,
        |x| &x.class_name,
        true,
    );
    let powers = emit_id_enum(
        &mut out,
        "PowerId",
        "POWER",
        "u16",
        &manifest.powers,
        |x| &x.id,
        |x| &x.class_name,
        false,
    );
    let relics = emit_id_enum(
        &mut out,
        "RelicId",
        "RELIC",
        "u16",
        &manifest.relics,
        |x| &x.id,
        |x| &x.class_name,
        false,
    );
    let potions = emit_id_enum(
        &mut out,
        "PotionId",
        "POTION",
        "u16",
        &manifest.potions,
        |x| &x.id,
        |x| &x.class_name,
        false,
    );
    let monsters = emit_id_enum(
        &mut out,
        "MonsterId",
        "MONSTER",
        "u16",
        &manifest.monsters,
        |x| &x.id,
        |x| &x.class_name,
        false,
    );
    let orbs = emit_id_enum(
        &mut out,
        "OrbId",
        "ORB",
        "u8",
        &manifest.orbs,
        |x| &x.id,
        |x| &x.class_name,
        false,
    );
    let _enchantments = emit_id_enum(
        &mut out,
        "EnchantmentId",
        "ENCHANTMENT",
        "u16",
        &manifest.enchantments,
        |x| &x.id,
        |x| &x.class_name,
        true,
    );
    let _afflictions = emit_id_enum(
        &mut out,
        "AfflictionId",
        "AFFLICTION",
        "u16",
        &manifest.afflictions,
        |x| &x.id,
        |x| &x.class_name,
        true,
    );
    let _dominions = emit_id_enum(
        &mut out,
        "DominionId",
        "DOMINION",
        "u16",
        &manifest.dominions,
        |x| &x.id,
        |x| &x.class_name,
        false,
    );
    let _run_models = emit_id_enum(
        &mut out,
        "RunModelId",
        "RUN",
        "u8",
        &manifest.run_models,
        |x| &x.id,
        |x| &x.class_name,
        false,
    );

    // Emit static metadata lookup arrays
    let _ = writeln!(out, "pub static CARD_META_TABLE: [CardMeta; CardId::COUNT] = [");
    for (_, c) in &cards {
        let mut kw_parts = Vec::new();
        for kw in &c.level0.keywords {
            let k = match kw.as_str() {
                "Exhaust" => "CardKeywords::EXHAUST",
                "Ethereal" => "CardKeywords::ETHEREAL",
                "Innate" => "CardKeywords::INNATE",
                "Retain" => "CardKeywords::RETAIN",
                "Sly" => "CardKeywords::SLY",
                "Unplayable" => "CardKeywords::UNPLAYABLE",
                "Eternal" => "CardKeywords::ETERNAL",
                _ => continue,
            };
            kw_parts.push(k);
        }
        let kw_expr = if kw_parts.is_empty() {
            "CardKeywords::empty()".to_owned()
        } else {
            let mut s = kw_parts[0].to_owned();
            for p in &kw_parts[1..] {
                let _ = write!(s, ".union({p})");
            }
            s
        };

        let mut tag_parts = Vec::new();
        for t in &c.level0.tags {
            let tg = match t.as_str() {
                "Strike" => "CardTags::STRIKE",
                "Defend" => "CardTags::DEFEND",
                "Shiv" => "CardTags::SHIV",
                "OstyAttack" => "CardTags::OSTY_ATTACK",
                "Shoot" => "CardTags::SHOOT",
                "Wah" => "CardTags::WAH",
                "Cho" => "CardTags::CHO",
                "Ki" => "CardTags::KI",
                "Minion" => "CardTags::MINION",
                _ => continue,
            };
            tag_parts.push(tg);
        }
        let tag_expr = if tag_parts.is_empty() {
            "CardTags::empty()".to_owned()
        } else {
            let mut s = tag_parts[0].to_owned();
            for p in &tag_parts[1..] {
                let _ = write!(s, ".union({p})");
            }
            s
        };

        let mut flag_parts = Vec::new();
        if c.gains_block {
            flag_parts.push("CardStaticFlags::GAINS_BLOCK");
        }
        if c.can_be_generated_in_combat {
            flag_parts.push("CardStaticFlags::CAN_BE_GENERATED_IN_COMBAT");
        }
        if c.can_be_generated_by_modifiers {
            flag_parts.push("CardStaticFlags::CAN_BE_GENERATED_BY_MODS");
        }
        if c.is_colorless {
            flag_parts.push("CardStaticFlags::IS_COLORLESS");
        }
        if c.is_multiplayer_only {
            flag_parts.push("CardStaticFlags::IS_MULTIPLAYER_ONLY");
        }
        if c.costs_x_energy {
            flag_parts.push("CardStaticFlags::IS_X_ENERGY_COST");
        }
        if c.costs_x_star {
            flag_parts.push("CardStaticFlags::IS_X_STAR_COST");
        }
        if c.is_allowed_in_blitz {
            flag_parts.push("CardStaticFlags::IS_ALLOWED_IN_BLITZ");
        }
        let flag_expr = if flag_parts.is_empty() {
            "CardStaticFlags::empty()".to_owned()
        } else {
            let mut s = flag_parts[0].to_owned();
            for p in &flag_parts[1..] {
                let _ = write!(s, ".union({p})");
            }
            s
        };

        let card_type = match c.card_type.as_str() {
            "Attack" => "CardType::Attack",
            "Skill" => "CardType::Skill",
            "Power" => "CardType::Power",
            "Status" => "CardType::Status",
            "Curse" => "CardType::Curse",
            "Quest" => "CardType::Quest",
            _ => "CardType::None",
        };
        let rarity = match c.rarity.as_str() {
            "Basic" => "CardRarity::Basic",
            "Common" => "CardRarity::Common",
            "Uncommon" => "CardRarity::Uncommon",
            "Rare" => "CardRarity::Rare",
            "Curse" => "CardRarity::Curse",
            "Status" => "CardRarity::Status",
            "Event" => "CardRarity::Event",
            "Ancient" => "CardRarity::Ancient",
            "Token" => "CardRarity::Token",
            "Quest" => "CardRarity::Quest",
            _ => "CardRarity::None",
        };
        let target_type = map_target_type(&c.target_type);
        let hook_expr = hook_mask_expr(&c.hooks);
        let _ = writeln!(
            out,
            "    CardMeta {{ card_type: {card_type}, rarity: {rarity}, target_type: {target_type}, canonical_energy_cost: {}, canonical_star_cost: {}, max_upgrade_level: {}, base_keywords: {kw_expr}, base_tags: {tag_expr}, flags: {flag_expr}, hook_mask: {hook_expr} }},",
            c.canonical_energy_cost, c.canonical_star_cost, c.max_upgrade_level
        );
    }
    let _ = writeln!(out, "];\n");
    let _ = writeln!(
        out,
        "#[inline]\npub fn generated_card_meta(id: CardId) -> &'static CardMeta {{\n    &CARD_META_TABLE[id as usize]\n}}\n"
    );

    let _ = writeln!(out, "pub static POWER_META_TABLE: [PowerMeta; PowerId::COUNT] = [");
    for (_, p) in &powers {
        let ptype = match p.power_type.as_str() {
            "Buff" => "PowerType::Buff",
            "Debuff" => "PowerType::Debuff",
            _ => "PowerType::None",
        };
        let stype = match p.stack_type.as_str() {
            "Counter" => "PowerStackType::Counter",
            "Single" => "PowerStackType::Single",
            _ => "PowerStackType::None",
        };
        let itype = match p.instance_type.as_str() {
            "Instanced" => "PowerInstanceType::Instanced",
            "InstancedPerApplier" => "PowerInstanceType::InstancedPerApplier",
            _ => "PowerInstanceType::None",
        };
        let mut flags = Vec::new();
        if p.allow_negative {
            flags.push("PowerStaticFlags::ALLOW_NEGATIVE");
        }
        if p.owner_is_secondary_enemy {
            flags.push("PowerStaticFlags::OWNER_IS_SECONDARY_ENEMY");
        }
        let flag_expr = if flags.is_empty() {
            "PowerStaticFlags::empty()".to_owned()
        } else {
            let mut s = flags[0].to_owned();
            for p in &flags[1..] {
                let _ = write!(s, ".union({p})");
            }
            s
        };
        let hook_expr = hook_mask_expr(&p.hooks);
        let _ = writeln!(
            out,
            "    PowerMeta {{ power_type: {ptype}, stack_type: {stype}, instance_type: {itype}, flags: {flag_expr}, hook_mask: {hook_expr} }},"
        );
    }
    let _ = writeln!(out, "];\n");
    let _ = writeln!(
        out,
        "#[inline]\npub fn generated_power_meta(id: PowerId) -> &'static PowerMeta {{\n    &POWER_META_TABLE[id as usize]\n}}\n"
    );

    let _ = writeln!(out, "pub static RELIC_META_TABLE: [RelicMeta; RelicId::COUNT] = [");
    for (_, r) in &relics {
        let mut flags = Vec::new();
        if r.is_stackable {
            flags.push("RelicStaticFlags::IS_STACKABLE");
        }
        if r.adds_pet {
            flags.push("RelicStaticFlags::ADDS_PET");
        }
        let flag_expr = if flags.is_empty() {
            "RelicStaticFlags::empty()".to_owned()
        } else {
            let mut s = flags[0].to_owned();
            for p in &flags[1..] {
                let _ = write!(s, ".union({p})");
            }
            s
        };
        let hook_expr = hook_mask_expr(&r.hooks);
        let _ = writeln!(
            out,
            "    RelicMeta {{ flags: {flag_expr}, hook_mask: {hook_expr} }},"
        );
    }
    let _ = writeln!(out, "];\n");
    let _ = writeln!(
        out,
        "#[inline]\npub fn generated_relic_meta(id: RelicId) -> &'static RelicMeta {{\n    &RELIC_META_TABLE[id as usize]\n}}\n"
    );

    let _ = writeln!(out, "pub static POTION_META_TABLE: [PotionMeta; PotionId::COUNT] = [");
    for (_, pot) in &potions {
        let tt = map_target_type(&pot.target_type);
        let hook_expr = hook_mask_expr(&pot.hooks);
        let _ = writeln!(
            out,
            "    PotionMeta {{ target_type: {tt}, uses_rng: false, may_heal: false, hook_mask: {hook_expr} }},"
        );
    }
    let _ = writeln!(out, "];\n");

    let _ = writeln!(out, "pub static MONSTER_META_TABLE: [MonsterMeta; MonsterId::COUNT] = [");
    for (_, m) in &monsters {
        let hook_expr = hook_mask_expr(&m.hooks);
        let _ = writeln!(out, "    MonsterMeta {{ hook_mask: {hook_expr} }},");
    }
    let _ = writeln!(out, "];\n");

    let _ = writeln!(out, "pub static ORB_META_TABLE: [OrbMeta; OrbId::COUNT] = [");
    for (cls, o) in &orbs {
        let hook_expr = hook_mask_expr(&o.hooks);
        let _ = writeln!(
            out,
            "    OrbMeta {{ orb_id: OrbId::{cls}, hook_mask: {hook_expr} }},"
        );
    }
    let _ = writeln!(out, "];\n");

    // Emit card partition enum + O(1) lookup table for conflict-free Wave 1 routing
    let mut pool_lookup: BTreeMap<String, &'static str> = BTreeMap::new();
    for pool in &manifest.pools.card_pools {
        let part = match pool.pool_name.as_str() {
            "IroncladCardPool" => Some("Ironclad"),
            "SilentCardPool" => Some("Silent"),
            "DefectCardPool" => Some("Defect"),
            "RegentCardPool" => Some("Regent"),
            "NecrobinderCardPool" => Some("Necrobinder"),
            "FoolCardPool" => Some("Fool"),
            _ => None,
        };
        if let Some(p) = part {
            for id in &pool.ids {
                pool_lookup.insert(id.clone(), p);
            }
        }
    }
    let _ = writeln!(out, "#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash)]");
    let _ = writeln!(out, "#[repr(u8)]");
    let _ = writeln!(out, "pub enum CardPartition {{");
    let _ = writeln!(out, "    Ironclad = 0,");
    let _ = writeln!(out, "    Silent = 1,");
    let _ = writeln!(out, "    Defect = 2,");
    let _ = writeln!(out, "    Regent = 3,");
    let _ = writeln!(out, "    Necrobinder = 4,");
    let _ = writeln!(out, "    Fool = 5,");
    let _ = writeln!(out, "    ColorlessSpecial = 6,");
    let _ = writeln!(out, "}}\n");
    let _ = writeln!(out, "pub static CARD_PARTITION_TABLE: [CardPartition; CardId::COUNT] = [");
    for (_, c) in &cards {
        let part = pool_lookup
            .get(&c.id)
            .copied()
            .unwrap_or("ColorlessSpecial");
        let _ = writeln!(out, "    CardPartition::{part},");
    }
    let _ = writeln!(out, "];\n");
    let _ = writeln!(
        out,
        "#[inline]\npub const fn card_partition(id: CardId) -> CardPartition {{\n    CARD_PARTITION_TABLE[id as usize]\n}}\n"
    );

    // Emit pool slices
    let card_class_map: BTreeMap<String, String> = manifest
        .cards
        .iter()
        .map(|c| (c.id.clone(), c.class_name.clone()))
        .collect();
    for pool in &manifest.pools.card_pools {
        let const_name = format!(
            "CARD_POOL_{}",
            pool.pool_name
                .replace("CardPool", "")
                .to_ascii_uppercase()
        );
        let _ = writeln!(out, "pub static {const_name}: &[CardId] = &[");
        for id in &pool.ids {
            if let Some(cls) = card_class_map.get(id) {
                let _ = writeln!(out, "    CardId::{cls},");
            }
        }
        let _ = writeln!(out, "];");
    }

    let _ = &manifest.pools.potion_pools;
    let _ = &manifest.pools.relic_pools;
    let _ = &manifest.pools.orb_pool;

    out
}

pub fn generate_from_file(input_path: &Path, output_dir: &Path) -> Result<String, String> {
    let raw = fs::read_to_string(input_path)
        .map_err(|e| format!("failed to read {}: {e}", input_path.display()))?;
    let manifest: OracleMetadataManifest = serde_json::from_str(&raw)
        .map_err(|e| format!("failed to parse {}: {e}", input_path.display()))?;
    let code = render_generated_module(&manifest);
    fs::create_dir_all(output_dir)
        .map_err(|e| format!("failed to create {}: {e}", output_dir.display()))?;
    let dest = output_dir.join("generated_ids.rs");
    fs::write(&dest, &code).map_err(|e| format!("failed to write {}: {e}", dest.display()))?;
    Ok(code)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn codegen_is_deterministic_on_sample_manifest() {
        let json = r#"{
            "meta": { "gameVersion": "v0.113.1.4" },
            "characters": [
                {
                    "id": "CHARACTER.IRONCLAD",
                    "className": "Ironclad",
                    "isPlayable": true,
                    "startingHp": 80,
                    "startingGold": 99,
                    "maxEnergy": 3,
                    "baseOrbSlotCount": 0,
                    "baseArrowSlotCount": 0,
                    "startingDeckCardIds": ["CARD.STRIKE_IRONCLAD"],
                    "startingRelicIds": ["RELIC.BURNING_BLOOD"]
                }
            ],
            "cards": [
                {
                    "id": "CARD.STRIKE_IRONCLAD",
                    "className": "StrikeIronclad",
                    "rarity": "Basic",
                    "type": "Attack",
                    "targetType": "AnyEnemy",
                    "isColorless": false,
                    "isMultiplayerOnly": false,
                    "canBeGeneratedByModifiers": true,
                    "canBeGeneratedInCombat": true,
                    "gainsBlock": false,
                    "maxUpgradeLevel": 1,
                    "canonicalEnergyCost": 1,
                    "costsXEnergy": false,
                    "canonicalStarCost": -1,
                    "costsXStar": false,
                    "hooks": { "overriddenHookSlots": [] },
                    "level0": { "keywords": [], "tags": ["Strike"] }
                }
            ],
            "powers": [
                {
                    "id": "POWER.STRENGTH_POWER",
                    "className": "StrengthPower",
                    "powerType": "Buff",
                    "stackType": "Counter",
                    "instanceType": "None",
                    "allowNegative": true,
                    "ownerIsSecondaryEnemy": false,
                    "hooks": { "overriddenHookSlots": ["ModifyDamageAdditive"] }
                }
            ],
            "relics": [
                {
                    "id": "RELIC.BURNING_BLOOD",
                    "className": "BurningBlood",
                    "rarity": "Starter",
                    "addsPet": false,
                    "isStackable": false,
                    "hooks": { "overriddenHooks": ["AfterCombatVictory"] }
                }
            ],
            "potions": [],
            "monsters": [],
            "orbs": [],
            "enchantments": [],
            "afflictions": [],
            "dominions": [],
            "runModels": [],
            "pools": { "cardPools": [], "potionPools": [], "relicPools": [], "orbPool": [] }
        }"#;
        let manifest: OracleMetadataManifest = serde_json::from_str(json).unwrap();
        let a = render_generated_module(&manifest);
        let b = render_generated_module(&manifest);
        assert_eq!(a, b);
        assert!(a.contains("StrikeIronclad = 0,"));
        assert!(a.contains("StrengthPower = 0,"));
        assert!(a.contains("HookSlot::AfterCombatVictory"));
    }
}
