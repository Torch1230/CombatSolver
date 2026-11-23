use crate::continuation::PendingContinuation;
use bitflags::bitflags;
use serde::{Deserialize, Serialize};
use smallvec::SmallVec;
use std::collections::BTreeMap;
use std::sync::Arc;
use sts2_types::{
    AfflictionId, ArrowStatus, AscensionLevel, CardId, CardKeywords, CardType, CharacterId,
    CombatSide, Dec, DominionId, EnchantmentId, EnchantmentStatus, GameMode,
    LocalEnergyModifierKind, MonsterId, OrbId, PileType, PlayerRngSet, PlayerTurnPhase, PotionId,
    PowerId, RelicId, RoomType, Rng, RunModelId, RunRngSet, ValueProp, is_ascension_active_in_mode,
};

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(transparent)]
pub struct CardHandle(pub u32);

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(transparent)]
pub struct CreatureHandle(pub u32);

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub enum FieldValue {
    Int(i32),
    Decimal(Dec),
    Bool(bool),
    String(String),
    NullableInt(Option<i32>),
    CardHandle(i32),
    CreatureHandle(i32),
    IntArray(Vec<i32>),
    CardHandleArray(Vec<i32>),
    CreatureHandleArray(Vec<i32>),
    CardIntMap(Vec<(i32, i32)>),
    CreatureDecimalMap(Vec<(i32, Dec)>),
    CardPairArray(Vec<(i32, i32)>),
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct LocalEnergyModifier {
    pub kind: LocalEnergyModifierKind,
    pub amount: i32,
    pub is_reduce_only: bool,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct EnchantmentInst {
    pub id: EnchantmentId,
    pub amount: i32,
    pub status: EnchantmentStatus,
    pub private_fields: BTreeMap<String, FieldValue>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct AfflictionInst {
    pub id: AfflictionId,
    pub amount: i32,
}

bitflags! {
    #[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Default)]
    pub struct CardTempFlags: u16 {
        const EXHAUST_ON_NEXT_PLAY   = 1 << 0;
        const IS_DUPE                = 1 << 1;
        const COSTS_X_ENERGY         = 1 << 2;
        const TEMPORARY_FREE_TURN    = 1 << 3;
        const TEMPORARY_FREE_PLAY    = 1 << 4;
        const TEMPORARY_FREE_COMBAT  = 1 << 5;
        const TEMPORARY_RETAIN_TURN  = 1 << 6;
        const TEMPORARY_SLY_TURN     = 1 << 7;
    }
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct CardExtension {
    pub local_energy_modifiers: SmallVec<[LocalEnergyModifier; 2]>,
    pub enchantment: Option<EnchantmentInst>,
    pub private_fields: BTreeMap<String, FieldValue>,
}

/// Compact hot struct for a card instance; infrequent/variable fields spill into `card_extensions`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CardInst {
    pub handle: CardHandle,
    pub id: CardId,
    pub upgrade_level: i32,
    pub canonical_base_energy_cost: i32,
    pub base_energy_cost: i32,
    pub captured_energy_x_value: i32,
    pub base_replay_count: i32,
    pub floor_added_to_deck: Option<i32>,
    pub deck_card_handle: Option<CardHandle>,
    pub clone_of_handle: Option<CardHandle>,
    pub temp_star_cost_override_this_turn: Option<i32>,
    pub temp_cost_override_this_turn: Option<i32>,
    pub temp_cost_override_this_turn_or_until_played: Option<i32>,
    pub temp_cost_override_this_combat: Option<i32>,
    pub local_keywords: CardKeywords,
    pub keywords_added_from_outside: CardKeywords,
    pub keywords_removed_from_outside: CardKeywords,
    pub temp_flags: CardTempFlags,
    pub affliction: Option<AfflictionInst>,
    pub ext_index: Option<u32>,
    pub pile: PileType,
    pub removed_from_state: bool,
    pub transform_into_after_play: Option<CardId>,
}

impl CardInst {
    pub fn has_local_keyword(&self, kw: CardKeywords) -> bool {
        if self.keywords_removed_from_outside.contains(kw) {
            return false;
        }
        self.local_keywords.contains(kw) || self.keywords_added_from_outside.contains(kw)
    }

    pub fn effective_keywords_base(&self) -> CardKeywords {
        (self.local_keywords | self.keywords_added_from_outside)
            & !self.keywords_removed_from_outside
    }
}

/// Exact 26 counters matching `InCombatPlayerCountersDto` / `LivePlayerCombatCountersSpec`.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerCounters {
    pub stars_gained_this_turn: Dec,
    pub skill_cards_played_this_turn: i32,
    pub attack_cards_played_this_turn: i32,
    pub shivs_played_this_turn: i32,
    pub cards_discarded_this_turn: i32,
    pub cards_played_this_combat: i32,
    pub cards_generated_this_combat: i32,
    pub sadness_generated_this_combat: i32,
    pub cards_drawn_this_combat: i32,
    pub cards_played_this_turn: i32,
    pub manual_cards_played_this_turn: i32,
    pub card_plays_started_this_turn: i32,
    pub attack_or_skill_card_plays_started_this_turn: i32,
    pub attack_cards_started_this_turn: i32,
    pub zero_cost_attacks_started_this_turn: i32,
    pub first_in_series_card_plays_started_this_turn: i32,
    pub wah_started_this_turn: i32,
    pub cho_started_this_turn: i32,
    pub ki_started_this_turn: i32,
    pub first_wah_play_ordinal_this_turn: i32,
    pub first_cho_play_ordinal_this_turn: i32,
    pub first_ki_play_ordinal_this_turn: i32,
    pub wah_finished_this_turn: i32,
    pub cho_finished_this_turn: i32,
    pub ki_finished_this_turn: i32,
    pub melody_cards_finished_this_turn: i32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct OrbInst {
    pub id: OrbId,
    pub raw_evoke_value: Option<Dec>,
    pub raw_passive_value: Option<Dec>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ArrowSlotInst {
    pub status: ArrowStatus,
    pub destroy_after_impaled: bool,
}

impl ArrowSlotInst {
    #[inline]
    pub const fn is_available(self) -> bool {
        matches!(self.status, ArrowStatus::Retrieved)
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RelicInst {
    pub id: RelicId,
    pub is_wax: bool,
    pub is_melted: bool,
    pub stack_count: i32,
    pub floor_added_to_deck: Option<i32>,
    pub private_fields: BTreeMap<String, FieldValue>,
}

/// Inline smallvec storage keeps standard runs stack/flat without capacity truncation on 2305+ card decks.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PlayerInst {
    pub creature_handle: CreatureHandle,
    pub current_hp: i32,
    pub max_hp: i32,
    pub block: i32,
    pub cumulative_hp_lost: i32,
    pub gold: i32,
    pub max_energy: i32,
    pub base_orb_slot_count: i32,
    pub is_active_for_hooks: bool,
    pub can_use_or_remove_potions: bool,
    pub energy: i32,
    pub stars: i32,
    pub phase: PlayerTurnPhase,
    pub turn_number: i32,
    pub counters: PlayerCounters,
    pub hand: SmallVec<[CardHandle; 12]>,
    pub draw: SmallVec<[CardHandle; 32]>,
    pub discard: SmallVec<[CardHandle; 32]>,
    pub exhaust: SmallVec<[CardHandle; 16]>,
    pub play: SmallVec<[CardHandle; 4]>,
    pub deck: SmallVec<[CardHandle; 32]>,
    pub potion_slots: SmallVec<[Option<PotionId>; 5]>,
    pub orb_capacity: i32,
    pub orbs: SmallVec<[OrbInst; 10]>,
    pub arrows: SmallVec<[ArrowSlotInst; 4]>,
    pub relics: SmallVec<[RelicInst; 16]>,
    pub card_or_potion_effect_depth: i32,
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct PowerExtension {
    pub private_fields: BTreeMap<String, FieldValue>,
    pub attack_command_id: Option<u64>,
    pub amount_when_attack_started: i32,
    pub impaled_arrows: SmallVec<[usize; 4]>,
    pub hit_counter: i32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct PowerInst {
    pub handle: u32,
    pub id: PowerId,
    pub amount: i32,
    pub amount_on_turn_start: i32,
    pub skip_next_duration_tick: bool,
    pub applier_handle: Option<CreatureHandle>,
    pub ext_index: Option<u32>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MonsterInst {
    pub monster_id: MonsterId,
    pub rng: Rng,
    pub is_performing_move: bool,
    pub spawned_this_turn: bool,
    pub current_state_id: String,
    pub next_move_id: Option<String>,
    pub next_move_can_transition_away: bool,
    pub performed_first_move: bool,
    pub state_log_ids: SmallVec<[String; 8]>,
    pub transient_move_follow_up_state_id: Option<String>,
    pub setup_fields: BTreeMap<String, FieldValue>,
    pub private_fields: BTreeMap<String, FieldValue>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CreatureInst {
    pub handle: CreatureHandle,
    pub combat_id: Option<u32>,
    pub side: CombatSide,
    pub slot_name: Option<String>,
    pub is_player: bool,
    pub pet_owner_player_index: Option<usize>,
    pub monster_id: Option<MonsterId>,
    pub current_hp: i32,
    pub max_hp: i32,
    pub block: i32,
    pub cumulative_hp_lost: i32,
    pub is_detached: bool,
    pub monster: Option<MonsterInst>,
    pub powers: SmallVec<[PowerInst; 8]>,
}

impl CreatureInst {
    #[inline]
    pub const fn is_alive(&self) -> bool {
        self.current_hp > 0
    }

    #[inline]
    pub const fn is_dead(&self) -> bool {
        self.current_hp <= 0
    }

    #[inline]
    pub const fn is_pet(&self) -> bool {
        self.pet_owner_player_index.is_some()
    }

    #[inline]
    pub const fn is_monster(&self) -> bool {
        self.monster.is_some()
    }

    #[inline]
    pub fn is_secondary_enemy(&self) -> bool {
        self.side == CombatSide::Enemy
            && self.powers.iter().any(|p| {
                p.id == PowerId::MinionPower
                    || sts2_types::generated_power_meta(p.id)
                        .flags
                        .contains(sts2_types::PowerStaticFlags::OWNER_IS_SECONDARY_ENEMY)
            })
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DamageResult {
    pub receiver: CreatureHandle,
    pub original_target: CreatureHandle,
    pub props: ValueProp,
    pub intended_hp_loss: i32,
    pub blocked_damage: i32,
    pub unblocked_damage: i32,
    pub overkill_damage: i32,
    pub was_target_killed: bool,
    pub was_block_broken: bool,
    pub was_fully_blocked: bool,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DamageHistoryEntry {
    pub receiver_creature_handle: CreatureHandle,
    pub dealer_creature_handle: Option<CreatureHandle>,
    pub original_target_creature_handle: CreatureHandle,
    pub props: ValueProp,
    pub intended_hp_loss: i32,
    pub blocked_damage: i32,
    pub unblocked_damage: i32,
    pub overkill_damage: i32,
    pub was_target_killed: bool,
    pub was_block_broken: bool,
    pub was_fully_blocked: bool,
    pub card_source_handle: Option<CardHandle>,
    pub round_number: i32,
    pub current_side: CombatSide,
    pub player_turn_numbers: SmallVec<[i32; 2]>,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TurnKey {
    pub round_number: i32,
    pub side: CombatSide,
    pub player_turn_numbers: SmallVec<[Option<i32>; 2]>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Serialize, Deserialize)]
pub enum GrowthSource {
    HandOfGreed,
    TheHunt,
    Feed,
    Royalties,
    Alchemize,
    GeneticAlgorithm,
    TheScythe,
    Goopy,
    ForbiddenGrimoire,
    MadScience,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GrowthCounters {
    pub hand_of_greed: i32,
    pub the_hunt: i32,
    pub feed: i32,
    pub royalties: i32,
    pub alchemize: i32,
    pub genetic_algorithm: i32,
    pub the_scythe: i32,
    pub goopy: i32,
    pub forbidden_grimoire: i32,
    pub mad_science: i32,
}

impl GrowthCounters {
    #[inline]
    pub fn total(&self) -> i32 {
        self.hand_of_greed
            .saturating_add(self.the_hunt)
            .saturating_add(self.feed)
            .saturating_add(self.royalties)
            .saturating_add(self.alchemize)
            .saturating_add(self.genetic_algorithm)
            .saturating_add(self.the_scythe)
            .saturating_add(self.goopy)
            .saturating_add(self.forbidden_grimoire)
            .saturating_add(self.mad_science)
    }

    #[inline]
    pub fn is_zero(&self) -> bool {
        *self == Self::default()
    }

    #[inline]
    pub fn credit(&self, rewards: &Self) -> i32 {
        self.hand_of_greed
            .saturating_mul(rewards.hand_of_greed)
            .saturating_add(self.the_hunt.saturating_mul(rewards.the_hunt))
            .saturating_add(self.feed.saturating_mul(rewards.feed))
            .saturating_add(self.royalties.saturating_mul(rewards.royalties))
            .saturating_add(self.alchemize.saturating_mul(rewards.alchemize))
            .saturating_add(self.genetic_algorithm.saturating_mul(rewards.genetic_algorithm))
            .saturating_add(self.the_scythe.saturating_mul(rewards.the_scythe))
            .saturating_add(self.goopy.saturating_mul(rewards.goopy))
            .saturating_add(self.forbidden_grimoire.saturating_mul(rewards.forbidden_grimoire))
            .saturating_add(self.mad_science.saturating_mul(rewards.mad_science))
    }

    #[inline]
    pub fn satisfies(&self, required: &Self) -> bool {
        self.hand_of_greed >= required.hand_of_greed
            && self.the_hunt >= required.the_hunt
            && self.feed >= required.feed
            && self.royalties >= required.royalties
            && self.alchemize >= required.alchemize
            && self.genetic_algorithm >= required.genetic_algorithm
            && self.the_scythe >= required.the_scythe
            && self.goopy >= required.goopy
            && self.forbidden_grimoire >= required.forbidden_grimoire
            && self.mad_science >= required.mad_science
    }

    #[inline]
    pub fn get(&self, source: GrowthSource) -> i32 {
        match source {
            GrowthSource::HandOfGreed => self.hand_of_greed,
            GrowthSource::TheHunt => self.the_hunt,
            GrowthSource::Feed => self.feed,
            GrowthSource::Royalties => self.royalties,
            GrowthSource::Alchemize => self.alchemize,
            GrowthSource::GeneticAlgorithm => self.genetic_algorithm,
            GrowthSource::TheScythe => self.the_scythe,
            GrowthSource::Goopy => self.goopy,
            GrowthSource::ForbiddenGrimoire => self.forbidden_grimoire,
            GrowthSource::MadScience => self.mad_science,
        }
    }

    #[inline]
    pub fn inc(&mut self, source: GrowthSource) {
        match source {
            GrowthSource::HandOfGreed => self.hand_of_greed = self.hand_of_greed.saturating_add(1),
            GrowthSource::TheHunt => self.the_hunt = self.the_hunt.saturating_add(1),
            GrowthSource::Feed => self.feed = self.feed.saturating_add(1),
            GrowthSource::Royalties => self.royalties = self.royalties.saturating_add(1),
            GrowthSource::Alchemize => self.alchemize = self.alchemize.saturating_add(1),
            GrowthSource::GeneticAlgorithm => {
                self.genetic_algorithm = self.genetic_algorithm.saturating_add(1)
            }
            GrowthSource::TheScythe => self.the_scythe = self.the_scythe.saturating_add(1),
            GrowthSource::Goopy => self.goopy = self.goopy.saturating_add(1),
            GrowthSource::ForbiddenGrimoire => {
                self.forbidden_grimoire = self.forbidden_grimoire.saturating_add(1)
            }
            GrowthSource::MadScience => self.mad_science = self.mad_science.saturating_add(1),
        }
    }
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct SemanticHistory {
    pub exhausts: Vec<(TurnKey, usize)>,
    pub card_block_gains: Vec<(TurnKey, usize, i32)>,
    pub orb_channels: Vec<(TurnKey, usize, OrbId)>,
    pub energy_spent: Vec<(TurnKey, usize, i32)>,
    pub card_drawn: Vec<(TurnKey, usize, CardType, bool)>,
    pub actor_events: Vec<(String, TurnKey, usize)>,
    pub growth_rewards: GrowthCounters,
    pub brightest_flame_max_hp_spent: i32,
}

impl SemanticHistory {
    pub fn current_turn_key(state: &BattleState) -> TurnKey {
        TurnKey {
            round_number: state.round_number,
            side: state.current_side,
            player_turn_numbers: smallvec::smallvec![Some(state.player.turn_number)],
        }
    }

    pub fn record_exhaust(&mut self, state: &BattleState) {
        self.exhausts.push((Self::current_turn_key(state), 0));
    }

    pub fn record_block_gain(&mut self, state: &BattleState, props: ValueProp, play_ordinal: Option<i32>) {
        if props.is_powered_block() {
            if let Some(ordinal) = play_ordinal {
                self.card_block_gains
                    .push((Self::current_turn_key(state), 0, ordinal));
            }
        }
    }

    pub fn record_orb_channel(&mut self, state: &BattleState, orb_id: OrbId) {
        self.orb_channels
            .push((Self::current_turn_key(state), 0, orb_id));
    }

    pub fn record_energy_spent(&mut self, state: &BattleState, amount: i32) {
        if amount > 0 {
            self.energy_spent
                .push((Self::current_turn_key(state), 0, amount));
        }
    }

    pub fn record_card_drawn(&mut self, state: &BattleState, card_type: CardType, from_hand_draw: bool) {
        self.card_drawn
            .push((Self::current_turn_key(state), 0, card_type, from_hand_draw));
    }

    pub fn record_actor_event(&mut self, state: &BattleState, kind: &str) {
        self.actor_events
            .push((kind.to_owned(), Self::current_turn_key(state), 0));
    }

    #[inline]
    pub fn record_growth(&mut self, source: GrowthSource) {
        self.growth_rewards.inc(source);
    }

    #[inline]
    pub fn record_brightest_flame_max_hp_loss(&mut self, amount: i32) {
        self.brightest_flame_max_hp_spent = self
            .brightest_flame_max_hp_spent
            .saturating_add(amount.max(0));
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RootStaticContext {
    pub encounter_id: String,
    pub encounter_is_weak: bool,
    pub room_type: RoomType,
    pub encounter_slots: Vec<String>,
    pub game_mode: GameMode,
    pub ascension: u8,
    pub character_id: CharacterId,
    pub dominion_id: Option<DominionId>,
    pub run_model_ids: Vec<RunModelId>,
    pub card_generation_pools: Vec<CardId>,
    pub transformation_pools: Vec<CardId>,
    pub map_col: i32,
    pub map_row: i32,
    pub act_index: i32,
    pub total_floor: i32,
}

impl Default for RootStaticContext {
    fn default() -> Self {
        Self {
            encounter_id: String::new(),
            encounter_is_weak: false,
            room_type: RoomType::Monster,
            encounter_slots: Vec::new(),
            game_mode: GameMode::Standard,
            ascension: 0,
            character_id: CharacterId::Ironclad,
            dominion_id: None,
            run_model_ids: Vec::new(),
            card_generation_pools: Vec::new(),
            transformation_pools: Vec::new(),
            map_col: 0,
            map_row: 0,
            act_index: 0,
            total_floor: 0,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BattleState {
    pub static_ctx: Arc<RootStaticContext>,
    pub current_side: CombatSide,
    pub round_number: i32,
    pub is_in_progress: bool,
    pub is_starting: bool,
    pub won: bool,
    pub is_player_extra_turn: bool,
    pub gold_was_stolen: bool,
    pub end_player_turn_requested: bool,
    pub card_action_boundary_depth: i32,
    pub next_creature_id: u32,
    pub initial_encounter_monster_count: usize,
    pub next_card_handle: u32,
    pub next_creature_handle: u32,
    pub next_power_handle: u32,
    pub next_attack_id: u64,
    pub run_rngs: RunRngSet,
    pub player_rngs: PlayerRngSet,
    pub player: PlayerInst,
    pub cards: Vec<CardInst>,
    pub card_extensions: Vec<CardExtension>,
    pub creatures: Vec<CreatureInst>,
    pub power_extensions: Vec<PowerExtension>,
    pub ally_handles: SmallVec<[CreatureHandle; 4]>,
    pub enemy_handles: SmallVec<[CreatureHandle; 8]>,
    pub escaped_handles: SmallVec<[CreatureHandle; 4]>,
    pub removed_handles: SmallVec<[CreatureHandle; 4]>,
    pub spawned_enemy_handles: SmallVec<[CreatureHandle; 8]>,
    pub pet_handles: SmallVec<[CreatureHandle; 2]>,
    pub damage_history: Vec<DamageHistoryEntry>,
    pub semantic_history: SemanticHistory,
    pub pending: Option<PendingContinuation>,
    pub vakuu_auto_select_depth: u8,
}

impl BattleState {
    #[inline]
    pub fn is_vakuu_auto_select_active(&self) -> bool {
        self.vakuu_auto_select_depth > 0
    }

    pub fn with_vakuu_auto_select<T, F>(&mut self, f: F) -> Result<T, crate::error::KernelError>
    where
        F: FnOnce(&mut BattleState) -> Result<T, crate::error::KernelError>,
    {
        self.vakuu_auto_select_depth = self.vakuu_auto_select_depth.saturating_add(1);
        let res = f(self);
        self.vakuu_auto_select_depth = self.vakuu_auto_select_depth.saturating_sub(1);
        res
    }

    #[inline]
    pub fn ascension_active(&self, level: AscensionLevel) -> bool {
        is_ascension_active_in_mode(self.static_ctx.game_mode, self.static_ctx.ascension, level)
    }

    #[inline]
    pub fn ascension_value(&self, level: AscensionLevel, asc_val: i32, fallback: i32) -> i32 {
        if self.ascension_active(level) {
            asc_val
        } else {
            fallback
        }
    }

    #[inline]
    pub fn card(&self, handle: CardHandle) -> Option<&CardInst> {
        self.cards.iter().find(|c| c.handle == handle)
    }

    #[inline]
    pub fn card_mut(&mut self, handle: CardHandle) -> Option<&mut CardInst> {
        self.cards.iter_mut().find(|c| c.handle == handle)
    }

    #[inline]
    pub fn card_ext(&self, ext_idx: Option<u32>) -> Option<&CardExtension> {
        ext_idx.and_then(|i| self.card_extensions.get(i as usize))
    }

    pub fn card_ext_mut(&mut self, handle: CardHandle) -> Option<&mut CardExtension> {
        let idx = self.cards.iter().position(|c| c.handle == handle)?;
        let ext_idx = match self.cards[idx].ext_index {
            Some(i) => i as usize,
            None => {
                let i = self.card_extensions.len();
                self.card_extensions.push(CardExtension::default());
                self.cards[idx].ext_index = Some(i as u32);
                i
            }
        };
        self.card_extensions.get_mut(ext_idx)
    }

    #[inline]
    pub fn creature(&self, handle: CreatureHandle) -> Option<&CreatureInst> {
        self.creatures.iter().find(|c| c.handle == handle)
    }

    #[inline]
    pub fn creature_mut(&mut self, handle: CreatureHandle) -> Option<&mut CreatureInst> {
        self.creatures.iter_mut().find(|c| c.handle == handle)
    }

    #[inline]
    pub fn player_creature(&self) -> Option<&CreatureInst> {
        self.creature(self.player.creature_handle)
    }

    #[inline]
    pub fn player_creature_mut(&mut self) -> Option<&mut CreatureInst> {
        let h = self.player.creature_handle;
        self.creature_mut(h)
    }

    #[inline]
    pub fn has_living_player_creature(&self) -> bool {
        self.ally_handles.iter().any(|&h| {
            self.creature(h).is_some_and(|c| {
                let is_player_creature = c.is_player || (!c.is_monster() && !c.is_pet());
                is_player_creature && c.is_alive()
            })
        })
    }

    #[inline]
    pub fn contains_creature(&self, handle: CreatureHandle) -> bool {
        self.ally_handles.contains(&handle) || self.enemy_handles.contains(&handle)
    }

    #[inline]
    pub fn is_live_combat(&self) -> bool {
        self.is_in_progress
    }

    #[inline]
    pub fn osty_handle(&self) -> Option<CreatureHandle> {
        self.pet_handles.iter().copied().find(|&h| {
            self.creature(h)
                .is_some_and(|c| c.monster_id == Some(MonsterId::Osty))
        })
    }

    #[inline]
    pub fn is_osty_alive(&self) -> bool {
        self.osty_handle()
            .and_then(|h| self.creature(h))
            .is_some_and(|c| c.is_alive())
    }

    pub fn sync_player_hp_to_creature(&mut self) {
        if let Some((hp, max_hp, block, cum)) = self
            .player_creature()
            .map(|c| (c.current_hp, c.max_hp, c.block, c.cumulative_hp_lost))
        {
            self.player.current_hp = hp;
            self.player.max_hp = max_hp;
            self.player.block = block;
            self.player.cumulative_hp_lost = cum;
        }
    }

    pub fn current_turn_key(&self) -> TurnKey {
        TurnKey {
            round_number: self.round_number,
            side: self.current_side,
            player_turn_numbers: smallvec::smallvec![Some(self.player.turn_number)],
        }
    }

    pub fn record_exhaust(&mut self) {
        let tk = self.current_turn_key();
        self.semantic_history.exhausts.push((tk, 0));
    }

    pub fn record_block_gain(&mut self, props: ValueProp, play_ordinal: Option<i32>) {
        if props.is_powered_block() {
            if let Some(ordinal) = play_ordinal {
                let tk = self.current_turn_key();
                self.semantic_history.card_block_gains.push((tk, 0, ordinal));
            }
        }
    }

    pub fn record_orb_channel(&mut self, orb_id: OrbId) {
        let tk = self.current_turn_key();
        self.semantic_history.orb_channels.push((tk, 0, orb_id));
    }

    pub fn record_energy_spent(&mut self, amount: i32) {
        if amount > 0 {
            let tk = self.current_turn_key();
            self.semantic_history.energy_spent.push((tk, 0, amount));
        }
    }

    pub fn record_card_drawn(&mut self, card_type: CardType, from_hand_draw: bool) {
        let tk = self.current_turn_key();
        self.semantic_history
            .card_drawn
            .push((tk, 0, card_type, from_hand_draw));
    }

    pub fn record_actor_event(&mut self, kind: &str) {
        let tk = self.current_turn_key();
        self.semantic_history
            .actor_events
            .push((kind.to_owned(), tk, 0));
    }

    pub fn pile_vec_mut(&mut self, pile: PileType) -> Option<&mut SmallVec<[CardHandle; 32]>> {
        // Hand, exhaust, play have different inline sizes so we provide explicit helpers below.
        match pile {
            PileType::Draw => Some(&mut self.player.draw),
            PileType::Discard => Some(&mut self.player.discard),
            PileType::Deck => Some(&mut self.player.deck),
            _ => None,
        }
    }

    pub fn remove_card_from_current_pile(&mut self, handle: CardHandle) -> PileType {
        let old = self.card(handle).map_or(PileType::None, |c| c.pile);
        match old {
            PileType::Hand => {
                self.player.hand.retain(|h| *h != handle);
            }
            PileType::Draw => {
                self.player.draw.retain(|h| *h != handle);
            }
            PileType::Discard => {
                self.player.discard.retain(|h| *h != handle);
            }
            PileType::Exhaust => {
                self.player.exhaust.retain(|h| *h != handle);
            }
            PileType::Play => {
                self.player.play.retain(|h| *h != handle);
            }
            PileType::Deck => {
                self.player.deck.retain(|h| *h != handle);
            }
            PileType::None => {}
        }
        if let Some(card) = self.card_mut(handle) {
            card.pile = PileType::None;
        }
        old
    }

    pub fn add_card_to_pile_at(&mut self, handle: CardHandle, pile: PileType, index: Option<usize>) {
        let insert_into = |vec_len: usize| -> usize {
            match index {
                Some(i) if i <= vec_len => i,
                _ => vec_len,
            }
        };
        match pile {
            PileType::Hand => {
                let idx = insert_into(self.player.hand.len());
                self.player.hand.insert(idx, handle);
            }
            PileType::Draw => {
                let idx = insert_into(self.player.draw.len());
                self.player.draw.insert(idx, handle);
            }
            PileType::Discard => {
                let idx = insert_into(self.player.discard.len());
                self.player.discard.insert(idx, handle);
            }
            PileType::Exhaust => {
                let idx = insert_into(self.player.exhaust.len());
                self.player.exhaust.insert(idx, handle);
            }
            PileType::Play => {
                let idx = insert_into(self.player.play.len());
                self.player.play.insert(idx, handle);
            }
            PileType::Deck => {
                let idx = insert_into(self.player.deck.len());
                self.player.deck.insert(idx, handle);
            }
            PileType::None => {}
        }
        if let Some(card) = self.card_mut(handle) {
            card.pile = pile;
        }
    }
}
