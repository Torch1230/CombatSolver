use crate::content_dispatch::ContentDispatcher;
use crate::error::KernelError;
use crate::ordering;
use crate::state::{
    ArrowSlotInst, BattleState, CardExtension, CardHandle, CardInst, CardTempFlags, CreatureHandle,
    CreatureInst, DamageHistoryEntry, EnchantmentInst, FieldValue, LocalEnergyModifier,
    MonsterInst, OrbInst, PlayerCounters, PlayerInst, PowerExtension, PowerInst, RelicInst,
    RootStaticContext, SemanticHistory, TurnKey,
};
use serde::{Deserialize, Serialize};
use smallvec::SmallVec;
use std::collections::{BTreeMap, HashMap};
use std::sync::Arc;
use sts2_types::{
    AfflictionId, ArrowStatus, CardId, CardKeywords, CharacterId, CombatSide, Dec, DominionId,
    EnchantmentId, EnchantmentStatus, GameMode, LocalEnergyModifierKind, MonsterId, OrbId,
    PileType, PlayerRngSet, PlayerRngType, PlayerTurnPhase, PotionId, PowerId, RelicId, Rng,
    RngWords, RoomType, RunModelId, RunRngSet, RunRngType, ValueProp,
};

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CardIntEntryDto {
    pub card_handle: i32,
    pub value: i32,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreatureDecimalEntryDto {
    pub creature_handle: i32,
    pub value: Dec,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CardPairEntryDto {
    pub original_card_handle: i32,
    pub clone_card_handle: i32,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FieldValueDto {
    pub kind: String,
    #[serde(default)]
    pub int_value: Option<i32>,
    #[serde(default)]
    pub decimal_value: Option<Dec>,
    #[serde(default)]
    pub bool_value: Option<bool>,
    #[serde(default)]
    pub string_value: Option<String>,
    #[serde(default)]
    pub int_array_value: Option<Vec<i32>>,
    #[serde(default)]
    pub card_int_map_value: Option<Vec<CardIntEntryDto>>,
    #[serde(default)]
    pub creature_decimal_map_value: Option<Vec<CreatureDecimalEntryDto>>,
    #[serde(default)]
    pub card_pair_array_value: Option<Vec<CardPairEntryDto>>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LocalEnergyModifierDto {
    pub kind: String,
    pub amount: i32,
    pub is_reduce_only: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EnchantmentDto {
    pub enchantment_id: String,
    pub amount: i32,
    pub status: String,
    #[serde(default)]
    pub private_fields: BTreeMap<String, FieldValueDto>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AfflictionDto {
    pub affliction_id: String,
    pub amount: i32,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CardDto {
    pub handle: i32,
    pub card_id: String,
    pub upgrade_level: i32,
    pub deck_card_handle: Option<i32>,
    pub clone_of_handle: Option<i32>,
    pub is_dupe: bool,
    pub exhaust_on_next_play: bool,
    pub base_replay_count: i32,
    pub captured_energy_x_value: i32,
    pub floor_added_to_deck: Option<i32>,
    pub canonical_base_energy_cost: i32,
    pub base_energy_cost: i32,
    pub costs_x_energy: bool,
    #[serde(default)]
    pub local_energy_modifiers: Vec<LocalEnergyModifierDto>,
    pub temporary_star_cost_override_this_turn: Option<i32>,
    pub temporary_free_this_turn: bool,
    pub temporary_free_until_played: bool,
    pub temporary_free_this_combat: bool,
    pub temporary_cost_override_this_turn: Option<i32>,
    pub temporary_cost_override_this_turn_or_until_played: Option<i32>,
    pub temporary_cost_override_this_combat: Option<i32>,
    pub temporary_retain_this_turn: bool,
    pub temporary_sly_this_turn: bool,
    #[serde(default)]
    pub local_keywords: Vec<String>,
    #[serde(default)]
    pub keywords_added_from_outside: Vec<String>,
    #[serde(default)]
    pub keywords_removed_from_outside: Vec<String>,
    pub enchantment: Option<EnchantmentDto>,
    pub affliction: Option<AfflictionDto>,
    #[serde(default)]
    pub private_fields: BTreeMap<String, FieldValueDto>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RelicDto {
    pub relic_id: String,
    pub is_wax: bool,
    pub is_melted: bool,
    pub stack_count: i32,
    pub floor_added_to_deck: Option<i32>,
    #[serde(default)]
    pub private_fields: BTreeMap<String, FieldValueDto>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OrbDto {
    pub orb_id: String,
    pub raw_evoke_value: Option<Dec>,
    pub raw_passive_value: Option<Dec>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ArrowSlotDto {
    pub status: String,
    pub destroy_after_impaled: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerDto {
    pub current_hp: i32,
    pub max_hp: i32,
    pub block: i32,
    pub cumulative_hp_lost: i32,
    pub gold: i32,
    pub max_energy: i32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub effective_max_energy: Option<i32>,
    pub base_orb_slot_count: i32,
    pub is_active_for_hooks: bool,
    pub can_use_or_remove_potions: bool,
    pub energy: i32,
    pub stars: i32,
    pub phase: String,
    pub turn_number: i32,
    pub counters: PlayerCounters,
    pub hand_handles: Vec<i32>,
    pub draw_handles: Vec<i32>,
    pub discard_handles: Vec<i32>,
    pub exhaust_handles: Vec<i32>,
    pub play_handles: Vec<i32>,
    pub deck_handles: Vec<i32>,
    pub potion_slots: Vec<Option<String>>,
    pub orb_capacity: i32,
    pub orbs: Vec<OrbDto>,
    pub arrows: Vec<ArrowSlotDto>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PowerDto {
    pub power_id: String,
    pub amount: i32,
    pub amount_on_turn_start: i32,
    pub skip_next_duration_tick: bool,
    pub applier_creature_handle: Option<i32>,
    #[serde(default)]
    pub private_fields: BTreeMap<String, FieldValueDto>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MonsterDto {
    pub rng: RngWords,
    pub is_performing_move: bool,
    pub spawned_this_turn: bool,
    pub current_state_id: String,
    pub next_move_id: Option<String>,
    pub next_move_can_transition_away: bool,
    pub performed_first_move: bool,
    #[serde(default)]
    pub state_log_ids: Vec<String>,
    pub transient_move_follow_up_state_id: Option<String>,
    #[serde(default)]
    pub setup_fields: BTreeMap<String, FieldValueDto>,
    #[serde(default)]
    pub private_fields: BTreeMap<String, FieldValueDto>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreatureDto {
    pub handle: i32,
    pub combat_id: Option<u32>,
    pub side: String,
    pub slot_name: Option<String>,
    pub is_player: bool,
    pub pet_owner_player_index: Option<usize>,
    pub monster_id: Option<String>,
    pub current_hp: i32,
    pub max_hp: i32,
    pub block: i32,
    pub cumulative_hp_lost: i32,
    #[serde(default)]
    pub is_detached: bool,
    pub monster: Option<MonsterDto>,
    #[serde(default)]
    pub powers: Vec<PowerDto>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DamageHistoryEntryDto {
    pub receiver_creature_handle: i32,
    pub dealer_creature_handle: Option<i32>,
    pub original_target_creature_handle: i32,
    pub props: i32,
    pub intended_hp_loss: i32,
    pub blocked_damage: i32,
    pub unblocked_damage: i32,
    pub overkill_damage: i32,
    pub was_target_killed: bool,
    pub was_block_broken: bool,
    pub was_fully_blocked: bool,
    pub card_source_handle: Option<i32>,
    pub round_number: i32,
    pub current_side: String,
    pub player_turn_numbers: Vec<i32>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TurnKeyDto {
    pub round_number: i32,
    pub side: String,
    pub player_turn_numbers: Vec<Option<i32>>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExhaustHistoryDto {
    pub turn: TurnKeyDto,
    pub actor_index: usize,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BlockGainHistoryDto {
    pub turn: TurnKeyDto,
    pub actor_index: usize,
    pub play_ordinal: i32,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OrbChannelHistoryDto {
    pub turn: TurnKeyDto,
    pub actor_index: usize,
    pub orb_id: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EnergySpentHistoryDto {
    pub turn: TurnKeyDto,
    pub actor_index: usize,
    pub amount: i32,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CardDrawnHistoryDto {
    pub turn: TurnKeyDto,
    pub actor_index: usize,
    pub card_type: String,
    pub from_hand_draw: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ActorEventHistoryDto {
    pub kind: String,
    pub turn: TurnKeyDto,
    pub actor_index: usize,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SemanticHistoryDto {
    #[serde(default)]
    pub exhausts: Vec<ExhaustHistoryDto>,
    #[serde(default)]
    pub card_block_gains: Vec<BlockGainHistoryDto>,
    #[serde(default)]
    pub orb_channels: Vec<OrbChannelHistoryDto>,
    #[serde(default)]
    pub energy_spent: Vec<EnergySpentHistoryDto>,
    #[serde(default)]
    pub card_drawn: Vec<CardDrawnHistoryDto>,
    #[serde(default)]
    pub actor_events: Vec<ActorEventHistoryDto>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LiveStateJson {
    #[serde(default)]
    pub character_id: Option<String>,
    #[serde(default)]
    pub ascension: u8,
    #[serde(default)]
    pub game_mode: Option<String>,
    #[serde(default)]
    pub dominion_id: Option<String>,
    #[serde(default)]
    pub run_model_ids: Vec<String>,
    pub encounter_id: String,
    #[serde(default)]
    pub encounter_is_weak: bool,
    #[serde(default)]
    pub initial_encounter_monster_count: usize,
    #[serde(default)]
    pub total_floor: i32,
    pub room_type: String,
    #[serde(default)]
    pub encounter_slots: Vec<String>,
    pub current_side: String,
    pub round_number: i32,
    pub is_player_extra_turn: bool,
    pub gold_was_stolen: bool,
    pub next_creature_id: u32,
    pub run_rng_seed: String,
    pub run_rngs: BTreeMap<String, RngWords>,
    pub player_rng_seed: u64,
    pub player_rngs: BTreeMap<String, RngWords>,
    pub player: PlayerDto,
    pub cards: Vec<CardDto>,
    pub relics: Vec<RelicDto>,
    pub creatures: Vec<CreatureDto>,
    pub ally_handles: Vec<i32>,
    pub enemy_handles: Vec<i32>,
    #[serde(default)]
    pub escaped_handles: Vec<i32>,
    #[serde(default)]
    pub removed_handles: Vec<i32>,
    #[serde(default)]
    pub spawned_enemy_handles: Vec<i32>,
    #[serde(default)]
    pub pet_handles: Vec<i32>,
    #[serde(default)]
    pub damage_history: Vec<DamageHistoryEntryDto>,
    #[serde(default)]
    pub semantic_history: SemanticHistoryDto,
}

/// Complete 10 combat RNG stream words for oracle diff checks.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OracleRngStreamsDto {
    pub shuffle: RngWords,
    pub combat_card_generation: RngWords,
    pub combat_potion_generation: RngWords,
    pub combat_card_selection: RngWords,
    pub combat_energy_costs: RngWords,
    pub combat_targets: RngWords,
    pub monster_ai: RngWords,
    pub niche: RngWords,
    pub combat_orbs: RngWords,
    pub non_combat_generation: RngWords,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct VerifiedCardDto {
    pub card_id: String,
    pub upgrade_level: i32,
    pub resolved_energy_cost: i32,
    pub local_keywords: Vec<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct VerifiedPowerDto {
    pub power_id: String,
    pub amount: i32,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct VerifiedCreatureDto {
    pub combat_id: Option<u32>,
    pub monster_id: Option<String>,
    pub current_hp: i32,
    pub max_hp: i32,
    pub block: i32,
    pub next_move_id: Option<String>,
    pub powers: Vec<VerifiedPowerDto>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CanonicalOracleStateDto {
    pub live_state: LiveStateJson,
    pub rng_streams: OracleRngStreamsDto,
    pub is_in_progress: bool,
    pub won: bool,
    pub hand_cards: Vec<VerifiedCardDto>,
    pub draw_cards: Vec<VerifiedCardDto>,
    pub discard_cards: Vec<VerifiedCardDto>,
    pub exhaust_cards: Vec<VerifiedCardDto>,
    pub enemies: Vec<VerifiedCreatureDto>,
}

fn parse_side(s: &str, path: &str) -> Result<CombatSide, KernelError> {
    match s {
        "Player" => Ok(CombatSide::Player),
        "Enemy" => Ok(CombatSide::Enemy),
        _ => Err(KernelError::SnapshotImport {
            field_path: path.to_owned(),
            message: format!("unknown CombatSide '{s}'"),
        }),
    }
}

fn parse_card_type(s: &str, path: &str) -> Result<sts2_types::CardType, KernelError> {
    match s {
        "None" => Ok(sts2_types::CardType::None),
        "Attack" => Ok(sts2_types::CardType::Attack),
        "Skill" => Ok(sts2_types::CardType::Skill),
        "Power" => Ok(sts2_types::CardType::Power),
        "Status" => Ok(sts2_types::CardType::Status),
        "Curse" => Ok(sts2_types::CardType::Curse),
        "Quest" => Ok(sts2_types::CardType::Quest),
        _ => Err(KernelError::SnapshotImport {
            field_path: path.to_owned(),
            message: format!("unknown CardType '{s}'"),
        }),
    }
}

fn side_to_str(side: CombatSide) -> &'static str {
    match side {
        CombatSide::Player => "Player",
        CombatSide::Enemy => "Enemy",
    }
}

fn parse_phase(s: &str) -> Result<PlayerTurnPhase, KernelError> {
    match s {
        "None" => Ok(PlayerTurnPhase::None),
        "Start" => Ok(PlayerTurnPhase::Start),
        "AutoPrePlay" => Ok(PlayerTurnPhase::AutoPrePlay),
        "Play" => Ok(PlayerTurnPhase::Play),
        "AutoPostPlay" => Ok(PlayerTurnPhase::AutoPostPlay),
        "End" => Ok(PlayerTurnPhase::End),
        _ => Err(KernelError::SnapshotImport {
            field_path: "player.phase".to_owned(),
            message: format!("unknown PlayerTurnPhase '{s}'"),
        }),
    }
}

fn phase_to_str(phase: PlayerTurnPhase) -> &'static str {
    match phase {
        PlayerTurnPhase::None => "None",
        PlayerTurnPhase::Start => "Start",
        PlayerTurnPhase::AutoPrePlay => "AutoPrePlay",
        PlayerTurnPhase::Play => "Play",
        PlayerTurnPhase::AutoPostPlay => "AutoPostPlay",
        PlayerTurnPhase::End => "End",
    }
}

fn parse_room_type(s: &str) -> Result<RoomType, KernelError> {
    match s {
        "Unassigned" => Ok(RoomType::Unassigned),
        "Monster" => Ok(RoomType::Monster),
        "Elite" => Ok(RoomType::Elite),
        "Boss" => Ok(RoomType::Boss),
        "Treasure" => Ok(RoomType::Treasure),
        "Shop" => Ok(RoomType::Shop),
        "Event" => Ok(RoomType::Event),
        "RestSite" => Ok(RoomType::RestSite),
        "Map" => Ok(RoomType::Map),
        _ => Err(KernelError::SnapshotImport {
            field_path: "roomType".to_owned(),
            message: format!("unknown RoomType '{s}'"),
        }),
    }
}

fn room_type_to_str(rt: RoomType) -> &'static str {
    match rt {
        RoomType::Unassigned => "Unassigned",
        RoomType::Monster => "Monster",
        RoomType::Elite => "Elite",
        RoomType::Boss => "Boss",
        RoomType::Treasure => "Treasure",
        RoomType::Shop => "Shop",
        RoomType::Event => "Event",
        RoomType::RestSite => "RestSite",
        RoomType::Map => "Map",
    }
}

fn parse_keywords(names: &[String], path: &str) -> Result<CardKeywords, KernelError> {
    let mut out = CardKeywords::empty();
    for name in names {
        let kw = CardKeywords::from_keyword_name(name).ok_or_else(|| KernelError::SnapshotImport {
            field_path: path.to_owned(),
            message: format!("unknown CardKeyword '{name}'"),
        })?;
        out |= kw;
    }
    Ok(out)
}

fn keywords_to_vec(kw: CardKeywords) -> Vec<String> {
    let all = [
        CardKeywords::EXHAUST,
        CardKeywords::ETHEREAL,
        CardKeywords::INNATE,
        CardKeywords::RETAIN,
        CardKeywords::SLY,
        CardKeywords::UNPLAYABLE,
        CardKeywords::ETERNAL,
    ];
    all.into_iter()
        .filter(|flag| kw.contains(*flag))
        .filter_map(|flag| flag.as_name().map(str::to_owned))
        .collect()
}

fn parse_local_energy_mod_kind(s: &str) -> Result<LocalEnergyModifierKind, KernelError> {
    match s {
        "FreeThisTurn" => Ok(LocalEnergyModifierKind::FreeThisTurn),
        "FreeUntilPlayed" => Ok(LocalEnergyModifierKind::FreeUntilPlayed),
        "FreeThisCombat" => Ok(LocalEnergyModifierKind::FreeThisCombat),
        "SetThisTurn" => Ok(LocalEnergyModifierKind::SetThisTurn),
        "SetUntilPlayed" => Ok(LocalEnergyModifierKind::SetUntilPlayed),
        "SetThisTurnOrUntilPlayed" => Ok(LocalEnergyModifierKind::SetThisTurnOrUntilPlayed),
        "SetThisCombat" => Ok(LocalEnergyModifierKind::SetThisCombat),
        "AddThisCombat" => Ok(LocalEnergyModifierKind::AddThisCombat),
        "AddThisTurn" => Ok(LocalEnergyModifierKind::AddThisTurn),
        "AddUntilPlayed" => Ok(LocalEnergyModifierKind::AddUntilPlayed),
        "AddThisTurnOrUntilPlayed" => Ok(LocalEnergyModifierKind::AddThisTurnOrUntilPlayed),
        _ => Err(KernelError::SnapshotImport {
            field_path: "cards[].localEnergyModifiers[].kind".to_owned(),
            message: format!("unknown LocalEnergyModifierKind '{s}'"),
        }),
    }
}

fn local_energy_mod_kind_to_str(kind: LocalEnergyModifierKind) -> &'static str {
    match kind {
        LocalEnergyModifierKind::FreeThisTurn => "FreeThisTurn",
        LocalEnergyModifierKind::FreeUntilPlayed => "FreeUntilPlayed",
        LocalEnergyModifierKind::FreeThisCombat => "FreeThisCombat",
        LocalEnergyModifierKind::SetThisTurn => "SetThisTurn",
        LocalEnergyModifierKind::SetUntilPlayed => "SetUntilPlayed",
        LocalEnergyModifierKind::SetThisTurnOrUntilPlayed => "SetThisTurnOrUntilPlayed",
        LocalEnergyModifierKind::SetThisCombat => "SetThisCombat",
        LocalEnergyModifierKind::AddThisCombat => "AddThisCombat",
        LocalEnergyModifierKind::AddThisTurn => "AddThisTurn",
        LocalEnergyModifierKind::AddUntilPlayed => "AddUntilPlayed",
        LocalEnergyModifierKind::AddThisTurnOrUntilPlayed => "AddThisTurnOrUntilPlayed",
    }
}

fn parse_field_value(dto: &FieldValueDto) -> Result<FieldValue, KernelError> {
    match dto.kind.as_str() {
        "Int" => Ok(FieldValue::Int(dto.int_value.unwrap_or(0))),
        "Decimal" => Ok(FieldValue::Decimal(dto.decimal_value.unwrap_or(Dec::ZERO))),
        "Bool" => Ok(FieldValue::Bool(dto.bool_value.unwrap_or(false))),
        "String" => Ok(FieldValue::String(
            dto.string_value.clone().unwrap_or_default(),
        )),
        "NullableInt" => Ok(FieldValue::NullableInt(dto.int_value)),
        "CardHandle" => Ok(FieldValue::CardHandle(dto.int_value.unwrap_or(0))),
        "CreatureHandle" => Ok(FieldValue::CreatureHandle(dto.int_value.unwrap_or(0))),
        "IntArray" => Ok(FieldValue::IntArray(
            dto.int_array_value.clone().unwrap_or_default(),
        )),
        "CardHandleArray" => Ok(FieldValue::CardHandleArray(
            dto.int_array_value.clone().unwrap_or_default(),
        )),
        "CreatureHandleArray" => Ok(FieldValue::CreatureHandleArray(
            dto.int_array_value.clone().unwrap_or_default(),
        )),
        "CardIntMap" => Ok(FieldValue::CardIntMap(
            dto.card_int_map_value
                .as_deref()
                .unwrap_or(&[])
                .iter()
                .map(|x| (x.card_handle, x.value))
                .collect(),
        )),
        "CreatureDecimalMap" => Ok(FieldValue::CreatureDecimalMap(
            dto.creature_decimal_map_value
                .as_deref()
                .unwrap_or(&[])
                .iter()
                .map(|x| (x.creature_handle, x.value))
                .collect(),
        )),
        "CardPairArray" => Ok(FieldValue::CardPairArray(
            dto.card_pair_array_value
                .as_deref()
                .unwrap_or(&[])
                .iter()
                .map(|x| (x.original_card_handle, x.clone_card_handle))
                .collect(),
        )),
        other => Err(KernelError::SnapshotImport {
            field_path: "privateFields".to_owned(),
            message: format!("unknown LiveFieldKind '{other}'"),
        }),
    }
}

fn export_field_value(fv: &FieldValue) -> FieldValueDto {
    match fv {
        FieldValue::Int(v) => FieldValueDto {
            kind: "Int".to_owned(),
            int_value: Some(*v),
            decimal_value: None,
            bool_value: None,
            string_value: None,
            int_array_value: None,
            card_int_map_value: None,
            creature_decimal_map_value: None,
            card_pair_array_value: None,
        },
        FieldValue::Decimal(d) => FieldValueDto {
            kind: "Decimal".to_owned(),
            int_value: None,
            decimal_value: Some(*d),
            bool_value: None,
            string_value: None,
            int_array_value: None,
            card_int_map_value: None,
            creature_decimal_map_value: None,
            card_pair_array_value: None,
        },
        FieldValue::Bool(b) => FieldValueDto {
            kind: "Bool".to_owned(),
            int_value: None,
            decimal_value: None,
            bool_value: Some(*b),
            string_value: None,
            int_array_value: None,
            card_int_map_value: None,
            creature_decimal_map_value: None,
            card_pair_array_value: None,
        },
        FieldValue::String(s) => FieldValueDto {
            kind: "String".to_owned(),
            int_value: None,
            decimal_value: None,
            bool_value: None,
            string_value: Some(s.clone()),
            int_array_value: None,
            card_int_map_value: None,
            creature_decimal_map_value: None,
            card_pair_array_value: None,
        },
        FieldValue::NullableInt(v) => FieldValueDto {
            kind: "NullableInt".to_owned(),
            int_value: *v,
            decimal_value: None,
            bool_value: None,
            string_value: None,
            int_array_value: None,
            card_int_map_value: None,
            creature_decimal_map_value: None,
            card_pair_array_value: None,
        },
        FieldValue::CardHandle(h) => FieldValueDto {
            kind: "CardHandle".to_owned(),
            int_value: Some(*h),
            decimal_value: None,
            bool_value: None,
            string_value: None,
            int_array_value: None,
            card_int_map_value: None,
            creature_decimal_map_value: None,
            card_pair_array_value: None,
        },
        FieldValue::CreatureHandle(h) => FieldValueDto {
            kind: "CreatureHandle".to_owned(),
            int_value: Some(*h),
            decimal_value: None,
            bool_value: None,
            string_value: None,
            int_array_value: None,
            card_int_map_value: None,
            creature_decimal_map_value: None,
            card_pair_array_value: None,
        },
        FieldValue::IntArray(a) => FieldValueDto {
            kind: "IntArray".to_owned(),
            int_value: None,
            decimal_value: None,
            bool_value: None,
            string_value: None,
            int_array_value: Some(a.clone()),
            card_int_map_value: None,
            creature_decimal_map_value: None,
            card_pair_array_value: None,
        },
        FieldValue::CardHandleArray(a) => FieldValueDto {
            kind: "CardHandleArray".to_owned(),
            int_value: None,
            decimal_value: None,
            bool_value: None,
            string_value: None,
            int_array_value: Some(a.clone()),
            card_int_map_value: None,
            creature_decimal_map_value: None,
            card_pair_array_value: None,
        },
        FieldValue::CreatureHandleArray(a) => FieldValueDto {
            kind: "CreatureHandleArray".to_owned(),
            int_value: None,
            decimal_value: None,
            bool_value: None,
            string_value: None,
            int_array_value: Some(a.clone()),
            card_int_map_value: None,
            creature_decimal_map_value: None,
            card_pair_array_value: None,
        },
        FieldValue::CardIntMap(m) => FieldValueDto {
            kind: "CardIntMap".to_owned(),
            int_value: None,
            decimal_value: None,
            bool_value: None,
            string_value: None,
            int_array_value: None,
            card_int_map_value: Some(
                m.iter()
                    .map(|(k, v)| CardIntEntryDto {
                        card_handle: *k,
                        value: *v,
                    })
                    .collect(),
            ),
            creature_decimal_map_value: None,
            card_pair_array_value: None,
        },
        FieldValue::CreatureDecimalMap(m) => FieldValueDto {
            kind: "CreatureDecimalMap".to_owned(),
            int_value: None,
            decimal_value: None,
            bool_value: None,
            string_value: None,
            int_array_value: None,
            card_int_map_value: None,
            creature_decimal_map_value: Some(
                m.iter()
                    .map(|(k, v)| CreatureDecimalEntryDto {
                        creature_handle: *k,
                        value: *v,
                    })
                    .collect(),
            ),
            card_pair_array_value: None,
        },
        FieldValue::CardPairArray(p) => FieldValueDto {
            kind: "CardPairArray".to_owned(),
            int_value: None,
            decimal_value: None,
            bool_value: None,
            string_value: None,
            int_array_value: None,
            card_int_map_value: None,
            creature_decimal_map_value: None,
            card_pair_array_value: Some(
                p.iter()
                    .map(|(a, b)| CardPairEntryDto {
                        original_card_handle: *a,
                        clone_card_handle: *b,
                    })
                    .collect(),
            ),
        },
    }
}

pub fn import_live_state(
    dto: &LiveStateJson,
    content: &dyn ContentDispatcher,
) -> Result<BattleState, KernelError> {
    let character_id = match dto.character_id.as_deref() {
        Some(s) => CharacterId::from_model_id_str(s).ok_or_else(|| KernelError::Unsupported {
            kind: "Character",
            id: s.to_owned(),
        })?,
        None => CharacterId::Ironclad,
    };
    let game_mode = match dto.game_mode.as_deref() {
        Some("Dread") => GameMode::Dread,
        Some("Blitz") => GameMode::Blitz,
        Some("Standard") | None => GameMode::Standard,
        Some(other) => {
            return Err(KernelError::Unsupported {
                kind: "GameMode",
                id: other.to_owned(),
            });
        }
    };

    let dominion_id = match dto.dominion_id.as_deref() {
        Some(s) if !s.is_empty() => Some(DominionId::from_model_id_str(s).ok_or_else(|| {
            KernelError::Unsupported {
                kind: "Dominion",
                id: s.to_owned(),
            }
        })?),
        _ => None,
    };
    let mut run_model_ids = Vec::with_capacity(dto.run_model_ids.len());
    for rm in &dto.run_model_ids {
        let id = RunModelId::from_model_id_str(rm).ok_or_else(|| KernelError::Unsupported {
            kind: "RunModel",
            id: rm.clone(),
        })?;
        run_model_ids.push(id);
    }

    let static_ctx = Arc::new(RootStaticContext {
        encounter_id: dto.encounter_id.clone(),
        encounter_is_weak: dto.encounter_is_weak || dto.encounter_id.ends_with("_WEAK"),
        room_type: parse_room_type(&dto.room_type)?,
        encounter_slots: dto.encounter_slots.clone(),
        game_mode,
        ascension: dto.ascension,
        character_id,
        dominion_id,
        run_model_ids,
        card_generation_pools: Vec::new(),
        transformation_pools: Vec::new(),
        map_col: 0,
        map_row: 0,
        act_index: 0,
        total_floor: dto.total_floor,
    });

    let mut run_rngs = RunRngSet::from_string_seed(&dto.run_rng_seed);
    for (k, words) in &dto.run_rngs {
        if let Some(rt) = RunRngType::from_pascal_str(k) {
            *run_rngs.get_mut(rt) = Rng::from_words(*words);
        }
    }

    let mut player_rngs = PlayerRngSet::from_seed(dto.player_rng_seed);
    for (k, words) in &dto.player_rngs {
        if let Some(pt) = PlayerRngType::from_pascal_str(k) {
            *player_rngs.get_mut(pt) = Rng::from_words(*words);
        }
    }

    let mut cards = Vec::with_capacity(dto.cards.len());
    let mut card_extensions = Vec::new();
    let mut max_card_handle = 0u32;
    let mut handle_to_pile: HashMap<i32, PileType> = HashMap::new();
    for &h in &dto.player.hand_handles {
        handle_to_pile.insert(h, PileType::Hand);
    }
    for &h in &dto.player.draw_handles {
        handle_to_pile.insert(h, PileType::Draw);
    }
    for &h in &dto.player.discard_handles {
        handle_to_pile.insert(h, PileType::Discard);
    }
    for &h in &dto.player.exhaust_handles {
        handle_to_pile.insert(h, PileType::Exhaust);
    }
    for &h in &dto.player.play_handles {
        handle_to_pile.insert(h, PileType::Play);
    }
    for &h in &dto.player.deck_handles {
        handle_to_pile.insert(h, PileType::Deck);
    }

    for c in &dto.cards {
        let card_id = CardId::from_model_id_str(&c.card_id).ok_or_else(|| KernelError::Unsupported {
            kind: "Card",
            id: c.card_id.clone(),
        })?;
        let _ = content.card_meta(card_id)?;
        let handle_u = u32::try_from(c.handle).map_err(|_| KernelError::SnapshotImport {
            field_path: "cards[].handle".to_owned(),
            message: "negative card handle".to_owned(),
        })?;
        max_card_handle = max_card_handle.max(handle_u + 1);

        let mut flags = CardTempFlags::empty();
        if c.exhaust_on_next_play {
            flags |= CardTempFlags::EXHAUST_ON_NEXT_PLAY;
        }
        if c.is_dupe {
            flags |= CardTempFlags::IS_DUPE;
        }
        if c.costs_x_energy {
            flags |= CardTempFlags::COSTS_X_ENERGY;
        }
        if c.temporary_free_this_turn {
            flags |= CardTempFlags::TEMPORARY_FREE_TURN;
        }
        if c.temporary_free_until_played {
            flags |= CardTempFlags::TEMPORARY_FREE_PLAY;
        }
        if c.temporary_free_this_combat {
            flags |= CardTempFlags::TEMPORARY_FREE_COMBAT;
        }
        if c.temporary_retain_this_turn {
            flags |= CardTempFlags::TEMPORARY_RETAIN_TURN;
        }
        if c.temporary_sly_this_turn {
            flags |= CardTempFlags::TEMPORARY_SLY_TURN;
        }

        let enchantment = match &c.enchantment {
            Some(e) => {
                let eid = EnchantmentId::from_model_id_str(&e.enchantment_id).ok_or_else(|| {
                    KernelError::Unsupported {
                        kind: "Enchantment",
                        id: e.enchantment_id.clone(),
                    }
                })?;
                let mut pf = BTreeMap::new();
                for (pk, pv) in &e.private_fields {
                    pf.insert(pk.clone(), parse_field_value(pv)?);
                }
                Some(EnchantmentInst {
                    id: eid,
                    amount: e.amount,
                    status: match e.status.as_str() {
                        "Disabled" => EnchantmentStatus::Disabled,
                        _ => EnchantmentStatus::Normal,
                    },
                    private_fields: pf,
                })
            }
            None => None,
        };

        let affliction = match &c.affliction {
            Some(a) => {
                let aid = AfflictionId::from_model_id_str(&a.affliction_id).ok_or_else(|| {
                    KernelError::Unsupported {
                        kind: "Affliction",
                        id: a.affliction_id.clone(),
                    }
                })?;
                Some(crate::state::AfflictionInst {
                    id: aid,
                    amount: a.amount,
                })
            }
            None => None,
        };

        let mut ext = CardExtension::default();
        for m in &c.local_energy_modifiers {
            ext.local_energy_modifiers.push(LocalEnergyModifier {
                kind: parse_local_energy_mod_kind(&m.kind)?,
                amount: m.amount,
                is_reduce_only: m.is_reduce_only,
            });
        }
        ext.enchantment = enchantment;
        for (pk, pv) in &c.private_fields {
            ext.private_fields.insert(pk.clone(), parse_field_value(pv)?);
        }
        let ext_index = if ext.local_energy_modifiers.is_empty()
            && ext.enchantment.is_none()
            && ext.private_fields.is_empty()
        {
            None
        } else {
            let idx = card_extensions.len() as u32;
            card_extensions.push(ext);
            Some(idx)
        };

        let local_kw = parse_keywords(&c.local_keywords, "cards[].localKeywords")?;

        cards.push(CardInst {
            handle: CardHandle(handle_u),
            id: card_id,
            upgrade_level: c.upgrade_level,
            canonical_base_energy_cost: c.canonical_base_energy_cost,
            base_energy_cost: c.base_energy_cost,
            captured_energy_x_value: c.captured_energy_x_value,
            base_replay_count: c.base_replay_count,
            floor_added_to_deck: c.floor_added_to_deck,
            deck_card_handle: c.deck_card_handle.map(|h| CardHandle(h as u32)),
            clone_of_handle: c.clone_of_handle.map(|h| CardHandle(h as u32)),
            temp_star_cost_override_this_turn: c.temporary_star_cost_override_this_turn,
            temp_cost_override_this_turn: c.temporary_cost_override_this_turn,
            temp_cost_override_this_turn_or_until_played: c
                .temporary_cost_override_this_turn_or_until_played,
            temp_cost_override_this_combat: c.temporary_cost_override_this_combat,
            local_keywords: local_kw,
            keywords_added_from_outside: parse_keywords(
                &c.keywords_added_from_outside,
                "cards[].keywordsAddedFromOutside",
            )?,
            keywords_removed_from_outside: parse_keywords(
                &c.keywords_removed_from_outside,
                "cards[].keywordsRemovedFromOutside",
            )?,
            temp_flags: flags,
            affliction,
            ext_index,
            pile: handle_to_pile.get(&c.handle).copied().unwrap_or(PileType::None),
            removed_from_state: false,
            transform_into_after_play: None,
        });
    }

    let mut relics = SmallVec::new();
    for r in &dto.relics {
        let rid = RelicId::from_model_id_str(&r.relic_id).ok_or_else(|| KernelError::Unsupported {
            kind: "Relic",
            id: r.relic_id.clone(),
        })?;
        let _ = content.relic_meta(rid)?;
        let mut pf = BTreeMap::new();
        for (pk, pv) in &r.private_fields {
            pf.insert(pk.clone(), parse_field_value(pv)?);
        }
        relics.push(RelicInst {
            id: rid,
            is_wax: r.is_wax,
            is_melted: r.is_melted,
            stack_count: r.stack_count,
            floor_added_to_deck: r.floor_added_to_deck,
            private_fields: pf,
        });
    }

    let mut potion_slots = SmallVec::new();
    for slot in &dto.player.potion_slots {
        match slot {
            None => potion_slots.push(None),
            Some(pid_str) => {
                let pid = PotionId::from_model_id_str(pid_str).ok_or_else(|| {
                    KernelError::Unsupported {
                        kind: "Potion",
                        id: pid_str.clone(),
                    }
                })?;
                let _ = content.potion_meta(pid)?;
                potion_slots.push(Some(pid));
            }
        }
    }

    let mut orbs = SmallVec::new();
    for o in &dto.player.orbs {
        let oid = OrbId::from_model_id_str(&o.orb_id).ok_or_else(|| KernelError::Unsupported {
            kind: "Orb",
            id: o.orb_id.clone(),
        })?;
        let _ = content.orb_meta(oid)?;
        orbs.push(OrbInst {
            id: oid,
            raw_evoke_value: o.raw_evoke_value,
            raw_passive_value: o.raw_passive_value,
        });
    }

    let mut arrows = SmallVec::new();
    for a in &dto.player.arrows {
        let status = match a.status.as_str() {
            "Retrieved" => ArrowStatus::Retrieved,
            "Away" => ArrowStatus::Away,
            "Destroyed" => ArrowStatus::Destroyed,
            other => {
                return Err(KernelError::SnapshotImport {
                    field_path: "player.arrows[].status".to_owned(),
                    message: format!("unknown ArrowStatus '{other}'"),
                });
            }
        };
        arrows.push(ArrowSlotInst {
            status,
            destroy_after_impaled: a.destroy_after_impaled,
        });
    }

    let mut creatures = Vec::with_capacity(dto.creatures.len());
    let mut power_extensions = Vec::new();
    let mut max_creature_handle = 0u32;
    let mut next_power_handle = 1u32;
    let mut player_creature_handle: Option<CreatureHandle> = None;

    for cr in &dto.creatures {
        let ch = CreatureHandle(u32::try_from(cr.handle).map_err(|_| {
            KernelError::SnapshotImport {
                field_path: "creatures[].handle".to_owned(),
                message: "negative creature handle".to_owned(),
            }
        })?);
        max_creature_handle = max_creature_handle.max(ch.0 + 1);
        if cr.is_player {
            player_creature_handle = Some(ch);
        }

        let monster = match &cr.monster {
            Some(m) => {
                let m_str = cr.monster_id.as_deref().ok_or_else(|| {
                    KernelError::SnapshotImport {
                        field_path: "creatures[].monsterId".to_owned(),
                        message: "monster creature missing monsterId".to_owned(),
                    }
                })?;
                let mid = MonsterId::from_model_id_str(m_str).ok_or_else(|| {
                    KernelError::Unsupported {
                        kind: "Monster",
                        id: m_str.to_owned(),
                    }
                })?;
                let _ = content.monster_meta(mid)?;
                let mut setup_fields = BTreeMap::new();
                for (k, v) in &m.setup_fields {
                    setup_fields.insert(k.clone(), parse_field_value(v)?);
                }
                let mut private_fields = BTreeMap::new();
                for (k, v) in &m.private_fields {
                    private_fields.insert(k.clone(), parse_field_value(v)?);
                }
                Some(MonsterInst {
                    monster_id: mid,
                    rng: Rng::from_words(m.rng),
                    is_performing_move: m.is_performing_move,
                    spawned_this_turn: m.spawned_this_turn,
                    current_state_id: m.current_state_id.clone(),
                    next_move_id: m.next_move_id.clone(),
                    next_move_can_transition_away: m.next_move_can_transition_away,
                    performed_first_move: m.performed_first_move,
                    state_log_ids: m.state_log_ids.iter().cloned().collect(),
                    transient_move_follow_up_state_id: m.transient_move_follow_up_state_id.clone(),
                    setup_fields,
                    private_fields,
                })
            }
            None => None,
        };

        let mut powers = SmallVec::new();
        for p in &cr.powers {
            let pid = PowerId::from_model_id_str(&p.power_id).ok_or_else(|| {
                KernelError::Unsupported {
                    kind: "Power",
                    id: p.power_id.clone(),
                }
            })?;
            let _ = content.power_meta(pid)?;
            let mut pf = BTreeMap::new();
            for (k, v) in &p.private_fields {
                pf.insert(k.clone(), parse_field_value(v)?);
            }
            let ext_index = if pid == PowerId::ImpaledPower || !pf.is_empty() {
                let idx = power_extensions.len() as u32;
                let mut ext = PowerExtension {
                    private_fields: pf,
                    ..Default::default()
                };
                if pid == PowerId::ImpaledPower {
                    ext.hit_counter = match ext.private_fields.get("ImpaledPower._hitCounter") {
                        Some(FieldValue::Int(v)) => *v,
                        _ => 3,
                    };
                    if let Some(FieldValue::IntArray(arr)) =
                        ext.private_fields.get("ImpaledPower._arrowSources")
                    {
                        ext.impaled_arrows = arr.iter().map(|&v| v as usize).collect();
                    }
                }
                power_extensions.push(ext);
                Some(idx)
            } else {
                None
            };
            powers.push(PowerInst {
                handle: next_power_handle,
                id: pid,
                amount: p.amount,
                amount_on_turn_start: p.amount_on_turn_start,
                skip_next_duration_tick: p.skip_next_duration_tick,
                applier_handle: p.applier_creature_handle.map(|h| CreatureHandle(h as u32)),
                ext_index,
            });
            next_power_handle += 1;
        }

        let monster_id = match cr.monster_id.as_deref() {
            Some(ms) => Some(MonsterId::from_model_id_str(ms).ok_or_else(|| {
                KernelError::Unsupported {
                    kind: "Monster",
                    id: ms.to_owned(),
                }
            })?),
            None => None,
        };

        creatures.push(CreatureInst {
            handle: ch,
            combat_id: cr.combat_id,
            side: parse_side(&cr.side, "creatures[].side")?,
            slot_name: cr.slot_name.clone(),
            is_player: cr.is_player,
            pet_owner_player_index: cr.pet_owner_player_index,
            monster_id,
            current_hp: cr.current_hp,
            max_hp: cr.max_hp,
            block: cr.block,
            cumulative_hp_lost: cr.cumulative_hp_lost,
            is_detached: cr.is_detached,
            monster,
            powers,
        });
    }

    let player_ch = player_creature_handle.ok_or_else(|| KernelError::SnapshotImport {
        field_path: "creatures".to_owned(),
        message: "no player creature found".to_owned(),
    })?;

    let player = PlayerInst {
        creature_handle: player_ch,
        current_hp: dto.player.current_hp,
        max_hp: dto.player.max_hp,
        block: dto.player.block,
        cumulative_hp_lost: dto.player.cumulative_hp_lost,
        gold: dto.player.gold,
        max_energy: dto.player.max_energy,
        base_orb_slot_count: dto.player.base_orb_slot_count,
        is_active_for_hooks: dto.player.is_active_for_hooks,
        can_use_or_remove_potions: dto.player.can_use_or_remove_potions,
        energy: dto.player.energy,
        stars: dto.player.stars,
        phase: parse_phase(&dto.player.phase)?,
        turn_number: dto.player.turn_number,
        counters: dto.player.counters,
        hand: dto.player.hand_handles.iter().map(|&h| CardHandle(h as u32)).collect(),
        draw: dto.player.draw_handles.iter().map(|&h| CardHandle(h as u32)).collect(),
        discard: dto.player.discard_handles.iter().map(|&h| CardHandle(h as u32)).collect(),
        exhaust: dto.player.exhaust_handles.iter().map(|&h| CardHandle(h as u32)).collect(),
        play: dto.player.play_handles.iter().map(|&h| CardHandle(h as u32)).collect(),
        deck: dto.player.deck_handles.iter().map(|&h| CardHandle(h as u32)).collect(),
        potion_slots,
        orb_capacity: dto.player.orb_capacity,
        orbs,
        arrows,
        relics,
        card_or_potion_effect_depth: 0,
    };

    let damage_history = dto
        .damage_history
        .iter()
        .map(|d| -> Result<DamageHistoryEntry, KernelError> {
            Ok(DamageHistoryEntry {
                receiver_creature_handle: CreatureHandle(d.receiver_creature_handle as u32),
                dealer_creature_handle: d.dealer_creature_handle.map(|h| CreatureHandle(h as u32)),
                original_target_creature_handle: CreatureHandle(
                    d.original_target_creature_handle as u32,
                ),
                props: ValueProp::from_bits_truncate(d.props as u8),
                intended_hp_loss: d.intended_hp_loss,
                blocked_damage: d.blocked_damage,
                unblocked_damage: d.unblocked_damage,
                overkill_damage: d.overkill_damage,
                was_target_killed: d.was_target_killed,
                was_block_broken: d.was_block_broken,
                was_fully_blocked: d.was_fully_blocked,
                card_source_handle: d.card_source_handle.map(|h| CardHandle(h as u32)),
                round_number: d.round_number,
                current_side: parse_side(&d.current_side, "damageHistory[].currentSide")?,
                player_turn_numbers: d.player_turn_numbers.iter().copied().collect(),
            })
        })
        .collect::<Result<Vec<_>, _>>()?;

    let parse_turn_key = |tk: &TurnKeyDto, path: &str| -> Result<TurnKey, KernelError> {
        Ok(TurnKey {
            round_number: tk.round_number,
            side: parse_side(&tk.side, path)?,
            player_turn_numbers: tk.player_turn_numbers.iter().copied().collect(),
        })
    };

    let mut semantic_history = SemanticHistory::default();
    for e in &dto.semantic_history.exhausts {
        semantic_history.exhausts.push((
            parse_turn_key(&e.turn, "semanticHistory.exhausts[].turn.side")?,
            e.actor_index,
        ));
    }
    for bg in &dto.semantic_history.card_block_gains {
        semantic_history.card_block_gains.push((
            parse_turn_key(&bg.turn, "semanticHistory.cardBlockGains[].turn.side")?,
            bg.actor_index,
            bg.play_ordinal,
        ));
    }
    for oc in &dto.semantic_history.orb_channels {
        let oid = OrbId::from_model_id_str(&oc.orb_id).ok_or_else(|| KernelError::Unsupported {
            kind: "Orb",
            id: oc.orb_id.clone(),
        })?;
        semantic_history.orb_channels.push((
            parse_turn_key(&oc.turn, "semanticHistory.orbChannels[].turn.side")?,
            oc.actor_index,
            oid,
        ));
    }
    for es in &dto.semantic_history.energy_spent {
        semantic_history.energy_spent.push((
            parse_turn_key(&es.turn, "semanticHistory.energySpent[].turn.side")?,
            es.actor_index,
            es.amount,
        ));
    }
    for cd in &dto.semantic_history.card_drawn {
        semantic_history.card_drawn.push((
            parse_turn_key(&cd.turn, "semanticHistory.cardDrawn[].turn.side")?,
            cd.actor_index,
            parse_card_type(&cd.card_type, "semanticHistory.cardDrawn[].cardType")?,
            cd.from_hand_draw,
        ));
    }
    for ae in &dto.semantic_history.actor_events {
        semantic_history.actor_events.push((
            ae.kind.clone(),
            parse_turn_key(&ae.turn, "semanticHistory.actorEvents[].turn.side")?,
            ae.actor_index,
        ));
    }

    Ok(BattleState {
        static_ctx,
        current_side: parse_side(&dto.current_side, "currentSide")?,
        round_number: dto.round_number,
        is_in_progress: true,
        is_starting: false,
        won: false,
        is_player_extra_turn: dto.is_player_extra_turn,
        gold_was_stolen: dto.gold_was_stolen,
        end_player_turn_requested: false,
        card_action_boundary_depth: 0,
        next_creature_id: dto.next_creature_id,
        initial_encounter_monster_count: if dto.initial_encounter_monster_count > 0 {
            dto.initial_encounter_monster_count
        } else {
            dto.enemy_handles.len()
        },
        next_card_handle: max_card_handle.max(1),
        next_creature_handle: max_creature_handle.max(1),
        next_power_handle,
        next_attack_id: 1,
        run_rngs,
        player_rngs,
        player,
        cards,
        card_extensions,
        creatures,
        power_extensions,
        ally_handles: dto.ally_handles.iter().map(|&h| CreatureHandle(h as u32)).collect(),
        enemy_handles: dto.enemy_handles.iter().map(|&h| CreatureHandle(h as u32)).collect(),
        escaped_handles: dto.escaped_handles.iter().map(|&h| CreatureHandle(h as u32)).collect(),
        removed_handles: dto.removed_handles.iter().map(|&h| CreatureHandle(h as u32)).collect(),
        spawned_enemy_handles: dto
            .spawned_enemy_handles
            .iter()
            .map(|&h| CreatureHandle(h as u32))
            .collect(),
        pet_handles: dto.pet_handles.iter().map(|&h| CreatureHandle(h as u32)).collect(),
        damage_history,
        semantic_history,
        pending: None,
        vakuu_auto_select_depth: 0,
    })
}

pub fn export_rng_streams(state: &BattleState) -> OracleRngStreamsDto {
    OracleRngStreamsDto {
        shuffle: state.run_rngs.get(RunRngType::Shuffle).to_words(),
        combat_card_generation: state.run_rngs.get(RunRngType::CombatCardGeneration).to_words(),
        combat_potion_generation: state.run_rngs.get(RunRngType::CombatPotionGeneration).to_words(),
        combat_card_selection: state.run_rngs.get(RunRngType::CombatCardSelection).to_words(),
        combat_energy_costs: state.run_rngs.get(RunRngType::CombatEnergyCosts).to_words(),
        combat_targets: state.run_rngs.get(RunRngType::CombatTargets).to_words(),
        monster_ai: state.run_rngs.get(RunRngType::MonsterAi).to_words(),
        niche: state.run_rngs.get(RunRngType::Niche).to_words(),
        combat_orbs: state.run_rngs.get(RunRngType::CombatOrbs).to_words(),
        non_combat_generation: state
            .player_rngs
            .get(PlayerRngType::NonCombatGeneration)
            .to_words(),
    }
}

pub fn export_live_state(state: &BattleState) -> Result<LiveStateJson, KernelError> {
    let mut run_rngs_map = BTreeMap::new();
    for rt in RunRngType::ALL {
        run_rngs_map.insert(
            rt.as_pascal_str().to_owned(),
            state.run_rngs.get(rt).to_words(),
        );
    }
    let mut player_rngs_map = BTreeMap::new();
    for pt in PlayerRngType::ALL {
        player_rngs_map.insert(
            pt.as_pascal_str().to_owned(),
            state.player_rngs.get(pt).to_words(),
        );
    }

    let cards = state
        .cards
        .iter()
        .filter(|c| !c.removed_from_state)
        .map(|c| {
            let ext = state.card_ext(c.ext_index);
            CardDto {
                handle: c.handle.0 as i32,
                card_id: c.id.model_id_str().to_owned(),
                upgrade_level: c.upgrade_level,
                deck_card_handle: c.deck_card_handle.map(|h| h.0 as i32),
                clone_of_handle: c.clone_of_handle.map(|h| h.0 as i32),
                is_dupe: c.temp_flags.contains(CardTempFlags::IS_DUPE),
                exhaust_on_next_play: c.temp_flags.contains(CardTempFlags::EXHAUST_ON_NEXT_PLAY),
                base_replay_count: c.base_replay_count,
                captured_energy_x_value: c.captured_energy_x_value,
                floor_added_to_deck: c.floor_added_to_deck,
                canonical_base_energy_cost: c.canonical_base_energy_cost,
                base_energy_cost: c.base_energy_cost,
                costs_x_energy: c.temp_flags.contains(CardTempFlags::COSTS_X_ENERGY),
                local_energy_modifiers: ext
                    .map(|e| {
                        e.local_energy_modifiers
                            .iter()
                            .map(|m| LocalEnergyModifierDto {
                                kind: local_energy_mod_kind_to_str(m.kind).to_owned(),
                                amount: m.amount,
                                is_reduce_only: m.is_reduce_only,
                            })
                            .collect()
                    })
                    .unwrap_or_default(),
                temporary_star_cost_override_this_turn: c.temp_star_cost_override_this_turn,
                temporary_free_this_turn: c.temp_flags.contains(CardTempFlags::TEMPORARY_FREE_TURN),
                temporary_free_until_played: c
                    .temp_flags
                    .contains(CardTempFlags::TEMPORARY_FREE_PLAY),
                temporary_free_this_combat: c
                    .temp_flags
                    .contains(CardTempFlags::TEMPORARY_FREE_COMBAT),
                temporary_cost_override_this_turn: c.temp_cost_override_this_turn,
                temporary_cost_override_this_turn_or_until_played: c
                    .temp_cost_override_this_turn_or_until_played,
                temporary_cost_override_this_combat: c.temp_cost_override_this_combat,
                temporary_retain_this_turn: c
                    .temp_flags
                    .contains(CardTempFlags::TEMPORARY_RETAIN_TURN),
                temporary_sly_this_turn: c.temp_flags.contains(CardTempFlags::TEMPORARY_SLY_TURN),
                local_keywords: keywords_to_vec(c.local_keywords),
                keywords_added_from_outside: keywords_to_vec(c.keywords_added_from_outside),
                keywords_removed_from_outside: keywords_to_vec(c.keywords_removed_from_outside),
                enchantment: ext.and_then(|e| {
                    e.enchantment.as_ref().map(|en| EnchantmentDto {
                        enchantment_id: en.id.model_id_str().to_owned(),
                        amount: en.amount,
                        status: match en.status {
                            EnchantmentStatus::Normal => "Normal".to_owned(),
                            EnchantmentStatus::Disabled => "Disabled".to_owned(),
                        },
                        private_fields: en
                            .private_fields
                            .iter()
                            .map(|(k, v)| (k.clone(), export_field_value(v)))
                            .collect(),
                    })
                }),
                affliction: c.affliction.map(|a| AfflictionDto {
                    affliction_id: a.id.model_id_str().to_owned(),
                    amount: a.amount,
                }),
                private_fields: ext
                    .map(|e| {
                        e.private_fields
                            .iter()
                            .map(|(k, v)| (k.clone(), export_field_value(v)))
                            .collect()
                    })
                    .unwrap_or_default(),
            }
        })
        .collect();

    let relics = state
        .player
        .relics
        .iter()
        .map(|r| RelicDto {
            relic_id: r.id.model_id_str().to_owned(),
            is_wax: r.is_wax,
            is_melted: r.is_melted,
            stack_count: r.stack_count,
            floor_added_to_deck: r.floor_added_to_deck,
            private_fields: r
                .private_fields
                .iter()
                .map(|(k, v)| (k.clone(), export_field_value(v)))
                .collect(),
        })
        .collect();

    let creatures = state
        .creatures
        .iter()
        .map(|cr| CreatureDto {
            handle: cr.handle.0 as i32,
            combat_id: cr.combat_id,
            side: side_to_str(cr.side).to_owned(),
            slot_name: cr.slot_name.clone(),
            is_player: cr.is_player,
            pet_owner_player_index: cr.pet_owner_player_index,
            monster_id: cr.monster_id.map(|m| m.model_id_str().to_owned()),
            current_hp: cr.current_hp,
            max_hp: cr.max_hp,
            block: cr.block,
            cumulative_hp_lost: cr.cumulative_hp_lost,
            is_detached: cr.is_detached,
            monster: cr.monster.as_ref().map(|m| MonsterDto {
                rng: m.rng.to_words(),
                is_performing_move: m.is_performing_move,
                spawned_this_turn: m.spawned_this_turn,
                current_state_id: m.current_state_id.clone(),
                next_move_id: m.next_move_id.clone(),
                next_move_can_transition_away: m.next_move_can_transition_away,
                performed_first_move: m.performed_first_move,
                state_log_ids: m.state_log_ids.iter().cloned().collect(),
                transient_move_follow_up_state_id: m.transient_move_follow_up_state_id.clone(),
                setup_fields: m
                    .setup_fields
                    .iter()
                    .map(|(k, v)| (k.clone(), export_field_value(v)))
                    .collect(),
                private_fields: m
                    .private_fields
                    .iter()
                    .map(|(k, v)| (k.clone(), export_field_value(v)))
                    .collect(),
            }),
            powers: cr
                .powers
                .iter()
                .map(|p| {
                    let mut pf: BTreeMap<String, FieldValueDto> = p
                        .ext_index
                        .and_then(|idx| state.power_extensions.get(idx as usize))
                        .map(|ext| {
                            ext.private_fields
                                .iter()
                                .map(|(k, v)| (k.clone(), export_field_value(v)))
                                .collect()
                        })
                        .unwrap_or_default();
                    if p.id == PowerId::ImpaledPower {
                        if let Some(ext) = p.ext_index.and_then(|idx| state.power_extensions.get(idx as usize)) {
                            pf.insert(
                                "ImpaledPower._hitCounter".to_owned(),
                                export_field_value(&FieldValue::Int(ext.hit_counter)),
                            );
                            pf.insert(
                                "ImpaledPower._arrowSources".to_owned(),
                                export_field_value(&FieldValue::IntArray(
                                    ext.impaled_arrows.iter().map(|&i| i as i32).collect(),
                                )),
                            );
                        }
                    }
                    PowerDto {
                        power_id: p.id.model_id_str().to_owned(),
                        amount: p.amount,
                        amount_on_turn_start: p.amount_on_turn_start,
                        skip_next_duration_tick: p.skip_next_duration_tick,
                        applier_creature_handle: p.applier_handle.map(|h| h.0 as i32),
                        private_fields: pf,
                    }
                })
                .collect(),
        })
        .collect();

    Ok(LiveStateJson {
        character_id: Some(state.static_ctx.character_id.model_entry().to_owned()),
        ascension: state.static_ctx.ascension,
        game_mode: Some(match state.static_ctx.game_mode {
            GameMode::Standard => "Standard".to_owned(),
            GameMode::Dread => "Dread".to_owned(),
            GameMode::Blitz => "Blitz".to_owned(),
        }),
        dominion_id: state
            .static_ctx
            .dominion_id
            .map(|d| d.model_id_str().to_owned()),
        run_model_ids: state
            .static_ctx
            .run_model_ids
            .iter()
            .map(|r| r.model_id_str().to_owned())
            .collect(),
        encounter_id: state.static_ctx.encounter_id.clone(),
        encounter_is_weak: state.static_ctx.encounter_is_weak,
        initial_encounter_monster_count: state.initial_encounter_monster_count,
        total_floor: state.static_ctx.total_floor,
        room_type: room_type_to_str(state.static_ctx.room_type).to_owned(),
        encounter_slots: state.static_ctx.encounter_slots.clone(),
        current_side: side_to_str(state.current_side).to_owned(),
        round_number: state.round_number,
        is_player_extra_turn: state.is_player_extra_turn,
        gold_was_stolen: state.gold_was_stolen,
        next_creature_id: state.next_creature_id,
        run_rng_seed: state.run_rngs.string_seed.clone(),
        run_rngs: run_rngs_map,
        player_rng_seed: state.player_rngs.seed,
        player_rngs: player_rngs_map,
        player: PlayerDto {
            current_hp: state.player.current_hp,
            max_hp: state.player.max_hp,
            block: state.player.block,
            cumulative_hp_lost: state.player.cumulative_hp_lost,
            gold: state.player.gold,
            max_energy: state.player.max_energy,
            effective_max_energy: None,
            base_orb_slot_count: state.player.base_orb_slot_count,
            is_active_for_hooks: state.player.is_active_for_hooks,
            can_use_or_remove_potions: state.player.can_use_or_remove_potions,
            energy: state.player.energy,
            stars: state.player.stars,
            phase: phase_to_str(state.player.phase).to_owned(),
            turn_number: state.player.turn_number,
            counters: state.player.counters,
            hand_handles: state.player.hand.iter().map(|h| h.0 as i32).collect(),
            draw_handles: state.player.draw.iter().map(|h| h.0 as i32).collect(),
            discard_handles: state.player.discard.iter().map(|h| h.0 as i32).collect(),
            exhaust_handles: state.player.exhaust.iter().map(|h| h.0 as i32).collect(),
            play_handles: state.player.play.iter().map(|h| h.0 as i32).collect(),
            deck_handles: state.player.deck.iter().map(|h| h.0 as i32).collect(),
            potion_slots: state
                .player
                .potion_slots
                .iter()
                .map(|p| p.map(|id| id.model_id_str().to_owned()))
                .collect(),
            orb_capacity: state.player.orb_capacity,
            orbs: state
                .player
                .orbs
                .iter()
                .map(|o| OrbDto {
                    orb_id: o.id.model_id_str().to_owned(),
                    raw_evoke_value: o.raw_evoke_value,
                    raw_passive_value: o.raw_passive_value,
                })
                .collect(),
            arrows: state
                .player
                .arrows
                .iter()
                .map(|a| ArrowSlotDto {
                    status: match a.status {
                        ArrowStatus::Retrieved => "Retrieved".to_owned(),
                        ArrowStatus::Away => "Away".to_owned(),
                        ArrowStatus::Destroyed => "Destroyed".to_owned(),
                    },
                    destroy_after_impaled: a.destroy_after_impaled,
                })
                .collect(),
        },
        cards,
        relics,
        creatures,
        ally_handles: state.ally_handles.iter().map(|h| h.0 as i32).collect(),
        enemy_handles: state.enemy_handles.iter().map(|h| h.0 as i32).collect(),
        escaped_handles: state.escaped_handles.iter().map(|h| h.0 as i32).collect(),
        removed_handles: state.removed_handles.iter().map(|h| h.0 as i32).collect(),
        spawned_enemy_handles: state
            .spawned_enemy_handles
            .iter()
            .map(|h| h.0 as i32)
            .collect(),
        pet_handles: state.pet_handles.iter().map(|h| h.0 as i32).collect(),
        damage_history: state
            .damage_history
            .iter()
            .map(|d| DamageHistoryEntryDto {
                receiver_creature_handle: d.receiver_creature_handle.0 as i32,
                dealer_creature_handle: d.dealer_creature_handle.map(|h| h.0 as i32),
                original_target_creature_handle: d.original_target_creature_handle.0 as i32,
                props: d.props.bits() as i32,
                intended_hp_loss: d.intended_hp_loss,
                blocked_damage: d.blocked_damage,
                unblocked_damage: d.unblocked_damage,
                overkill_damage: d.overkill_damage,
                was_target_killed: d.was_target_killed,
                was_block_broken: d.was_block_broken,
                was_fully_blocked: d.was_fully_blocked,
                card_source_handle: d.card_source_handle.map(|h| h.0 as i32),
                round_number: d.round_number,
                current_side: side_to_str(d.current_side).to_owned(),
                player_turn_numbers: d.player_turn_numbers.iter().copied().collect(),
            })
            .collect(),
        semantic_history: SemanticHistoryDto {
            exhausts: state
                .semantic_history
                .exhausts
                .iter()
                .map(|(tk, actor)| ExhaustHistoryDto {
                    turn: TurnKeyDto {
                        round_number: tk.round_number,
                        side: side_to_str(tk.side).to_owned(),
                        player_turn_numbers: tk.player_turn_numbers.iter().copied().collect(),
                    },
                    actor_index: *actor,
                })
                .collect(),
            card_block_gains: state
                .semantic_history
                .card_block_gains
                .iter()
                .map(|(tk, actor, ord)| BlockGainHistoryDto {
                    turn: TurnKeyDto {
                        round_number: tk.round_number,
                        side: side_to_str(tk.side).to_owned(),
                        player_turn_numbers: tk.player_turn_numbers.iter().copied().collect(),
                    },
                    actor_index: *actor,
                    play_ordinal: *ord,
                })
                .collect(),
            orb_channels: state
                .semantic_history
                .orb_channels
                .iter()
                .map(|(tk, actor, oid)| OrbChannelHistoryDto {
                    turn: TurnKeyDto {
                        round_number: tk.round_number,
                        side: side_to_str(tk.side).to_owned(),
                        player_turn_numbers: tk.player_turn_numbers.iter().copied().collect(),
                    },
                    actor_index: *actor,
                    orb_id: oid.model_id_str().to_owned(),
                })
                .collect(),
            energy_spent: state
                .semantic_history
                .energy_spent
                .iter()
                .map(|(tk, actor, amt)| EnergySpentHistoryDto {
                    turn: TurnKeyDto {
                        round_number: tk.round_number,
                        side: side_to_str(tk.side).to_owned(),
                        player_turn_numbers: tk.player_turn_numbers.iter().copied().collect(),
                    },
                    actor_index: *actor,
                    amount: *amt,
                })
                .collect(),
            card_drawn: state
                .semantic_history
                .card_drawn
                .iter()
                .map(|(tk, actor, ct, fhd)| CardDrawnHistoryDto {
                    turn: TurnKeyDto {
                        round_number: tk.round_number,
                        side: side_to_str(tk.side).to_owned(),
                        player_turn_numbers: tk.player_turn_numbers.iter().copied().collect(),
                    },
                    actor_index: *actor,
                    card_type: format!("{ct:?}"),
                    from_hand_draw: *fhd,
                })
                .collect(),
            actor_events: state
                .semantic_history
                .actor_events
                .iter()
                .map(|(kind, tk, actor)| ActorEventHistoryDto {
                    kind: kind.clone(),
                    turn: TurnKeyDto {
                        round_number: tk.round_number,
                        side: side_to_str(tk.side).to_owned(),
                        player_turn_numbers: tk.player_turn_numbers.iter().copied().collect(),
                    },
                    actor_index: *actor,
                })
                .collect(),
        },
    })
}

pub fn export_canonical_oracle_state(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
) -> Result<CanonicalOracleStateDto, KernelError> {
    let project_pile = |state: &mut BattleState, handles: &[CardHandle]| -> Result<Vec<VerifiedCardDto>, KernelError> {
        let mut out = Vec::with_capacity(handles.len());
        for &h in handles {
            let resolved_cost = ordering::resolve_energy_cost(state, content, h)?;
            let c = state.card(h).unwrap();
            out.push(VerifiedCardDto {
                card_id: c.id.model_id_str().to_owned(),
                upgrade_level: c.upgrade_level,
                resolved_energy_cost: resolved_cost,
                local_keywords: keywords_to_vec(c.effective_keywords_base()),
            });
        }
        Ok(out)
    };

    let hand_handles = state.player.hand.clone();
    let draw_handles = state.player.draw.clone();
    let discard_handles = state.player.discard.clone();
    let exhaust_handles = state.player.exhaust.clone();

    let hand_cards = project_pile(state, &hand_handles)?;
    let draw_cards = project_pile(state, &draw_handles)?;
    let discard_cards = project_pile(state, &discard_handles)?;
    let exhaust_cards = project_pile(state, &exhaust_handles)?;

    let enemies = state
        .enemy_handles
        .iter()
        .filter_map(|&h| state.creature(h))
        .map(|cr| VerifiedCreatureDto {
            combat_id: cr.combat_id,
            monster_id: cr.monster_id.map(|m| m.model_id_str().to_owned()),
            current_hp: cr.current_hp,
            max_hp: cr.max_hp,
            block: cr.block,
            next_move_id: cr.monster.as_ref().and_then(|m| m.next_move_id.clone()),
            powers: cr
                .powers
                .iter()
                .map(|p| VerifiedPowerDto {
                    power_id: p.id.model_id_str().to_owned(),
                    amount: p.amount,
                })
                .collect(),
        })
        .collect();

    Ok(CanonicalOracleStateDto {
        live_state: export_live_state(state)?,
        rng_streams: export_rng_streams(state),
        is_in_progress: state.is_in_progress,
        won: state.won,
        hand_cards,
        draw_cards,
        discard_cards,
        exhaust_cards,
        enemies,
    })
}
