use crate::arena::{NodeArena, NodeId};
use crate::hash::{hash_u64_slice, StateHash};
use crate::packer::{pack_state, unpack_state};
use crate::tt::TranspositionLabel;
use hashbrown::HashMap;
use parking_lot::RwLock;
use serde::{Deserialize, Serialize};
use sts2_kernel::state::{BattleState, FieldValue, GrowthCounters};
use sts2_kernel::{CombatEngine, ContentDispatcher};
use sts2_types::{
    CardId, CardKeywords, CardStaticFlags, CardType, GameMode, OrbId, PileType, PlayerRngType,
    PotionId, PowerId, RelicId, RoomType,
};

pub mod weights {
    pub const DEATH_PENALTY: f64 = -1_000_000_000_000.0;
    pub const VICTORY_BONUS: f64 = 10_000_000_000.0;
    pub const HP: f64 = 30_000.0;
    pub const ENEMY_HP: f64 = -10_000.0;
    pub const RISK_PENALTY: f64 = -2_000.0;
    pub const ACTION_PENALTY: f64 = -1.0;

    pub const VULNERABLE_ATTACK_WINDOW_CAP: i32 = 24;
    pub const VULNERABLE_ATTACK_MULTIPLIER_BEAM_VALUE: f64 = 5_000.0;
    pub const OFF_TARGET_VULNERABLE_ATTACK_MULTIPLIER_BEAM_VALUE: f64 = 1_000.0;

    pub const CURRENT_ENERGY_BEAM_CAP: i32 = 6;
    pub const CURRENT_ENERGY_BEAM_VALUE: f64 = 60_000.0;

    pub const PERSISTENT_BUFF_DELTA_BEAM_CAP: i32 = 32;
    pub const PERSISTENT_BUFF_DELTA_BEAM_VALUE: f64 = 50_000.0;
    pub const STANDARD_PERSISTENT_BUFF_DELTA_BEAM_CAP: i32 = 4;
    pub const STANDARD_PERSISTENT_BUFF_DELTA_BEAM_VALUE: f64 = 150_000.0;

    pub const LATENT_SETUP_BEAM_CAP: i32 = 24;
    pub const LATENT_SETUP_BEAM_VALUE: f64 = 12_000.0;

    pub const REPLAY_POTENTIAL_BEAM_CAP: i32 = 64;
    pub const REPLAY_POTENTIAL_BEAM_VALUE: f64 = 10_000.0;

    pub const LONG_TERM_RESOURCE_BEAM_VALUE: f64 = 25_000.0;
    pub const LONG_TERM_RESOURCE_BEAM_CAP: f64 = 25_000.0;

    pub const MINIMUM_TURN_LAYER_EXPANDED_NODES: usize = 500;
    pub const NO_VICTORY_ESCALATION_FACTOR: usize = 2;
    pub const MAXIMUM_NO_VICTORY_ESCALATIONS: usize = 2;
    pub const MAXIMUM_ESCALATED_BEAM_WIDTH: usize = 512;
    pub const MAXIMUM_ESCALATED_BRANCHES_PER_ACTION: usize = 100;

    pub const ANGER_COPY_BEAM_PENALTY: f64 = -15_000.0;
    pub const RETAINED_ATTACK_GROWTH_BEAM_CAP: i32 = 16;
    pub const RETAINED_ATTACK_GROWTH_BEAM_VALUE: f64 = 20_000.0;
    pub const FUTURE_RESOURCE_BEAM_VALUE: f64 = 10_000.0;
    pub const DELAYED_DAMAGE_BEAM_VALUE: f64 = 10_000.0;
    pub const SANDPIT_TURN_BEAM_VALUE: f64 = 30_000.0;

    pub const ENEMY_STRENGTH_SUPPRESSION_BEAM_CAP: i32 = 16;
    pub const ENEMY_WEAK_TURNS_BEAM_CAP: i32 = 16;
    pub const STANDARD_ENEMY_STRENGTH_SUPPRESSION_HORIZON: i32 = 4;
    pub const BOSS_ENEMY_STRENGTH_SUPPRESSION_HORIZON: i32 = 8;
    pub const STANDARD_ENEMY_WEAK_EXPECTED_HP_SAVED: i32 = 1;
    pub const BOSS_ENEMY_WEAK_EXPECTED_HP_SAVED: i32 = 2;

    pub const LIVE_DECK_CLUTTER_PENALTY: f64 = -8_000.0;
    pub const OUTSTANDING_STOLEN_RESOURCE_PENALTY: f64 = -1_000_000.0;
    pub const SOLD_HP_PENALTY: f64 = -20_000.0;

    pub const POTION_MINIMUM_HP_SAVED: i32 = 9;
    pub const POTION_HIGH_VALUE_HP_SAVED: i32 = POTION_MINIMUM_HP_SAVED * 2;
    pub const POTION_ELEVATED_VALUE_HP_SAVED: i32 = (POTION_MINIMUM_HP_SAVED * 3 + 1) / 2;
    pub const DEATH_SAVE_PREMIUM_PERCENT: i32 = 900;

    pub const SETUP_VALUE_HORIZON_TURNS: i32 = 16;
    pub const MAXIMUM_SEARCH_MAX_DEGREE_OF_PARALLELISM: usize = 16;
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Serialize, Deserialize)]
pub enum BossHpStrategy {
    #[default]
    ProgressionFirst,
    MinimizeHpLoss,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Serialize, Deserialize)]
pub enum SolverPotionDirective {
    #[default]
    Smart,
    Force,
    Disabled,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PotionSlotDirective {
    pub slot: usize,
    pub potion_id: PotionId,
    pub directive: SolverPotionDirective,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Ord, PartialOrd, Serialize, Deserialize)]
#[repr(u8)]
pub enum RelicCounterId {
    HappyFlower = 0,
    FakeHappyFlower = 1,
    Pendulum = 2,
    PollinousCore = 3,
    PenNib = 4,
    Nunchaku = 5,
    TuningFork = 6,
    JossPaper = 7,
    IronClub = 8,
    GalacticDust = 9,
    MeatOnTheBone = 10,
}

impl RelicCounterId {
    pub fn from_str_name(s: &str) -> Option<Self> {
        match s {
            "HappyFlower" => Some(Self::HappyFlower),
            "FakeHappyFlower" => Some(Self::FakeHappyFlower),
            "Pendulum" => Some(Self::Pendulum),
            "PollinousCore" => Some(Self::PollinousCore),
            "PenNib" => Some(Self::PenNib),
            "Nunchaku" => Some(Self::Nunchaku),
            "TuningFork" => Some(Self::TuningFork),
            "JossPaper" => Some(Self::JossPaper),
            "IronClub" => Some(Self::IronClub),
            "GalacticDust" => Some(Self::GalacticDust),
            "MeatOnTheBone" => Some(Self::MeatOnTheBone),
            _ => None,
        }
    }

    pub const fn period(self) -> i32 {
        match self {
            Self::HappyFlower | Self::Pendulum => 3,
            Self::FakeHappyFlower | Self::JossPaper => 5,
            Self::PollinousCore | Self::IronClub => 4,
            Self::PenNib | Self::Nunchaku | Self::TuningFork | Self::GalacticDust => 10,
            Self::MeatOnTheBone => 2,
        }
    }

    pub const fn relic_id(self) -> RelicId {
        match self {
            Self::HappyFlower => RelicId::HappyFlower,
            Self::FakeHappyFlower => RelicId::FakeHappyFlower,
            Self::Pendulum => RelicId::Pendulum,
            Self::PollinousCore => RelicId::PollinousCore,
            Self::PenNib => RelicId::PenNib,
            Self::Nunchaku => RelicId::Nunchaku,
            Self::TuningFork => RelicId::TuningFork,
            Self::JossPaper => RelicId::JossPaper,
            Self::IronClub => RelicId::IronClub,
            Self::GalacticDust => RelicId::GalacticDust,
            Self::MeatOnTheBone => RelicId::MeatOnTheBone,
        }
    }

    pub fn read_counter(self, state: &BattleState) -> i32 {
        let read_field = |relic_id: RelicId, key: &str| -> i32 {
            state
                .player
                .relics
                .iter()
                .find(|r| !r.is_melted && r.id == relic_id)
                .and_then(|r| r.private_fields.get(key))
                .and_then(|v| match v {
                    FieldValue::Int(x) => Some(*x),
                    _ => None,
                })
                .unwrap_or(0)
        };
        let raw = match self {
            Self::HappyFlower => read_field(RelicId::HappyFlower, "HappyFlower._turnCounter"),
            Self::FakeHappyFlower => {
                read_field(RelicId::FakeHappyFlower, "FakeHappyFlower._turnsSeen")
            }
            Self::Pendulum => read_field(RelicId::Pendulum, "Pendulum._turnCounter"),
            Self::PollinousCore => read_field(RelicId::PollinousCore, "PollinousCore._handsSeen"),
            Self::PenNib => read_field(RelicId::PenNib, "PenNib._attacksPlayed"),
            Self::Nunchaku => read_field(RelicId::Nunchaku, "Nunchaku._attacksPlayed"),
            Self::TuningFork => read_field(RelicId::TuningFork, "TuningFork._skillsPlayed"),
            Self::JossPaper => read_field(RelicId::JossPaper, "JossPaper._cardsExhausted"),
            Self::IronClub => read_field(RelicId::IronClub, "IronClub._cardsPlayed"),
            Self::GalacticDust => read_field(RelicId::GalacticDust, "GalacticDust._starsSpent"),
            Self::MeatOnTheBone => {
                let pc = state.player_creature();
                let hp = pc.map_or(state.player.current_hp, |c| c.current_hp);
                let max_hp = pc.map_or(state.player.max_hp, |c| c.max_hp);
                i32::from(hp * 100 <= max_hp * 50)
            }
        };
        raw.rem_euclid(self.period())
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RelicCounterRule {
    pub id: RelicCounterId,
    pub enabled: bool,
    pub minimum: i32,
    pub maximum: i32,
    pub hp_allowance: i32,
    pub priority: i32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct RelicCounterTarget {
    pub id: RelicCounterId,
    pub minimum: i32,
    pub maximum: i32,
    pub hp_allowance: i32,
    pub period: i32,
    pub priority: i32,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct RelicCounterEvaluation {
    pub satisfied_mask: u64,
    pub target_mask: u64,
    pub hp_credit: i32,
    pub healing_hp_credit: i32,
    pub distance: i32,
    pub satisfied_priority: i32,
}

impl RelicCounterEvaluation {
    #[inline]
    pub fn all_satisfied(&self) -> bool {
        self.satisfied_mask == self.target_mask
    }

    #[inline]
    pub fn total_hp_credit(&self) -> i32 {
        self.hp_credit.saturating_add(self.healing_hp_credit)
    }
}

pub struct RelicCounterPolicy;

impl RelicCounterPolicy {
    pub fn capture_targets(
        state: &BattleState,
        enabled: bool,
        rules: &[RelicCounterRule],
    ) -> Vec<RelicCounterTarget> {
        if !enabled {
            return Vec::new();
        }
        let mut targets: Vec<RelicCounterTarget> = rules
            .iter()
            .filter(|r| {
                r.enabled
                    && state
                        .player
                        .relics
                        .iter()
                        .any(|owned| !owned.is_melted && owned.id == r.id.relic_id())
            })
            .filter_map(|r| {
                let period = r.id.period();
                if r.minimum > 0 || r.maximum < period - 1 {
                    Some(RelicCounterTarget {
                        id: r.id,
                        minimum: r.minimum,
                        maximum: r.maximum,
                        hp_allowance: r.hp_allowance,
                        period,
                        priority: r.priority,
                    })
                } else {
                    None
                }
            })
            .collect();
        targets.sort_by_key(|t| t.id);
        targets
    }

    pub fn evaluate(
        state: &BattleState,
        targets: &[RelicCounterTarget],
        won: bool,
        post_combat_heal: PostCombatRelicHealProfile,
        strategic_boss_hp_relief: BossHpRelief,
    ) -> RelicCounterEvaluation {
        if targets.is_empty() {
            return RelicCounterEvaluation::default();
        }
        let mut eval = RelicCounterEvaluation::default();
        for &target in targets {
            let value = target.id.read_counter(state);
            let bit = 1u64 << (target.id as u32);
            let satisfied = value >= target.minimum && value <= target.maximum;
            let reward_goal = satisfied && target.id != RelicCounterId::MeatOnTheBone;
            let distance = if satisfied {
                0
            } else if value < target.minimum {
                target.minimum - value
            } else {
                target.period - value + target.minimum
            };
            eval.target_mask |= bit;
            if satisfied {
                eval.satisfied_mask |= bit;
            }
            if reward_goal {
                eval.hp_credit = eval.hp_credit.saturating_add(target.hp_allowance);
                eval.satisfied_priority = eval
                    .satisfied_priority
                    .saturating_add(1i32 << ((target.priority.clamp(1, 3) - 1) * 4));
            }
            eval.distance = eval.distance.saturating_add(distance);
        }
        if won
            && (eval.satisfied_mask & (1u64 << (RelicCounterId::MeatOnTheBone as u32))) != 0
        {
            let pc = state.player_creature();
            let hp = pc.map_or(state.player.current_hp, |c| c.current_hp);
            let max_hp = pc.map_or(state.player.max_hp, |c| c.max_hp);
            let threshold_heal = post_combat_heal.heal_for(hp, max_hp)
                - post_combat_heal.monotone_heal_for(hp, max_hp);
            eval.healing_hp_credit = ActEndingBossPolicy::persistent_value_of_recovered_hp(
                threshold_heal,
                strategic_boss_hp_relief,
            );
        }
        eval
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct GrowthOpportunityTargets {
    pub required_rewards: GrowthCounters,
    pub is_bounded: bool,
    pub has_targets: bool,
}

impl GrowthOpportunityTargets {
    pub fn is_satisfied_by(&self, rewards: &GrowthCounters) -> bool {
        self.is_bounded && rewards.satisfies(&self.required_rewards)
    }

    pub fn capture(state: &BattleState, content: &dyn ContentDispatcher) -> Self {
        use sts2_kernel::state::CardInst;
        use sts2_types::EnchantmentId;

        let is_mad_science_improvement = |c: &CardInst| -> bool {
            if c.id != CardId::MadScience {
                return false;
            }
            let Some(ext) = state.card_ext(c.ext_index) else {
                return false;
            };
            let is_power = matches!(
                ext.private_fields.get("MadScience._type"),
                Some(FieldValue::String(s)) if s == "Power"
            ) || matches!(
                ext.private_fields.get("MadScience._type"),
                Some(FieldValue::Int(2))
            );
            let is_improvement = matches!(
                ext.private_fields.get("MadScience.<Rider>k__BackingField"),
                Some(FieldValue::String(s)) if s == "Improvement"
            );
            is_power && is_improvement
        };

        let is_goopy = |c: &CardInst| -> bool {
            c.deck_card_handle.is_some()
                && state
                    .card_ext(c.ext_index)
                    .and_then(|e| e.enchantment.as_ref())
                    .is_some_and(|en| en.id == EnchantmentId::Goopy)
        };

        let is_target_card = |c: &CardInst| -> bool {
            matches!(
                c.id,
                CardId::HandOfGreed
                    | CardId::TheHunt
                    | CardId::Feed
                    | CardId::Royalties
                    | CardId::Alchemize
                    | CardId::ForbiddenGrimoire
            ) || is_mad_science_improvement(c)
                || (c.deck_card_handle.is_some()
                    && matches!(
                        c.id,
                        CardId::GeneticAlgorithm | CardId::TheScythe | CardId::ShowOff
                    ))
                || is_goopy(c)
        };

        let available: Vec<&CardInst> = state
            .cards
            .iter()
            .filter(|c| !c.removed_from_state && c.pile != PileType::Exhaust && c.pile != PileType::Deck)
            .collect();

        let ms_cap = mad_science_upgrade_capacity(state, content);
        let has_any = available.iter().any(|c| is_target_card(c));
        if !has_any {
            return Self {
                required_rewards: GrowthCounters::default(),
                is_bounded: true,
                has_targets: false,
            };
        }

        let has_replay_or_copy_card = available.iter().any(|c| {
            matches!(
                c.id,
                CardId::Burst
                    | CardId::EchoForm
                    | CardId::OneTwoPunch
                    | CardId::SignalBoost
                    | CardId::TagTeam
                    | CardId::KiVerse
                    | CardId::Nightmare
                    | CardId::DualWield
                    | CardId::HeirloomHammer
                    | CardId::Juggling
                    | CardId::ImitationLearning
                    | CardId::HowlFromBeyond
            )
        });
        let has_replay_power = state.player_creature().is_some_and(|pc| {
            pc.powers.iter().any(|p| {
                p.amount > 0
                    && matches!(
                        p.id,
                        PowerId::BurstPower
                            | PowerId::DuplicationPower
                            | PowerId::EchoFormPower
                            | PowerId::OneTwoPunchPower
                            | PowerId::SignalBoostPower
                            | PowerId::TagTeamPower
                            | PowerId::JugglingPower
                            | PowerId::ImitationLearningPower
                            | PowerId::NightmarePower
                            | PowerId::KiVersePower
                    )
            })
        });
        let has_replay_relic = state.player.relics.iter().any(|r| {
            !r.is_melted && matches!(r.id, RelicId::ThrowingAxe | RelicId::BurningSticks)
        });
        let has_dup_potion = state
            .player
            .potion_slots
            .iter()
            .any(|p| *p == Some(PotionId::Duplicator));

        if has_replay_or_copy_card || has_replay_power || has_replay_relic || has_dup_potion {
            return Self {
                required_rewards: GrowthCounters::default(),
                is_bounded: false,
                has_targets: true,
            };
        }

        let enemy_count = state
            .enemy_handles
            .iter()
            .filter(|&&h| state.creature(h).is_some_and(|c| c.is_alive() && state.contains_creature(h)))
            .count() as i32;

        let mut required = GrowthCounters::default();
        let has_hog = available.iter().any(|c| c.id == CardId::HandOfGreed);
        let has_hunt = available.iter().any(|c| c.id == CardId::TheHunt);
        let has_feed = available.iter().any(|c| c.id == CardId::Feed);
        let fatal_kinds = usize::from(has_hog) + usize::from(has_hunt) + usize::from(has_feed);
        if fatal_kinds > 1 {
            return Self {
                required_rewards: GrowthCounters::default(),
                is_bounded: false,
                has_targets: true,
            };
        } else if has_hog {
            required.hand_of_greed = enemy_count;
        } else if has_hunt {
            let n = available.iter().filter(|c| c.id == CardId::TheHunt).count() as i32;
            required.the_hunt = enemy_count.min(n);
        } else if has_feed {
            let n = available.iter().filter(|c| c.id == CardId::Feed).count() as i32;
            required.feed = enemy_count.min(n);
        }

        let count_plays = |pred: &dyn Fn(&CardInst) -> bool| -> i32 {
            available
                .iter()
                .filter(|c| pred(c))
                .map(|c| 1 + c.base_replay_count.max(0))
                .sum()
        };

        required.royalties = count_plays(&|c| c.id == CardId::Royalties);
        required.alchemize = count_plays(&|c| c.id == CardId::Alchemize);
        required.genetic_algorithm =
            count_plays(&|c| c.id == CardId::GeneticAlgorithm && c.deck_card_handle.is_some());
        required.the_scythe =
            count_plays(&|c| c.id == CardId::TheScythe && c.deck_card_handle.is_some());
        required.goopy = count_plays(&is_goopy);
        required.forbidden_grimoire = count_plays(&|c| c.id == CardId::ForbiddenGrimoire);
        required.mad_science = ms_cap.min(count_plays(&is_mad_science_improvement));

        let show_off_plays =
            count_plays(&|c| c.id == CardId::ShowOff && c.deck_card_handle.is_some())
                + state
                    .player_creature()
                    .map_or(0, |pc| {
                        pc.powers
                            .iter()
                            .filter(|p| p.id == PowerId::ShowOffPower && p.amount > 0)
                            .count() as i32
                    });

        let has_targets = required.total() > 0 || show_off_plays > 0;
        Self {
            required_rewards: required,
            is_bounded: show_off_plays == 0,
            has_targets,
        }
    }
}

pub fn mad_science_upgrade_capacity(state: &BattleState, content: &dyn ContentDispatcher) -> i32 {
    let upgradable = state
        .player
        .deck
        .iter()
        .filter(|&&h| {
            state.card(h).is_some_and(|c| {
                content
                    .card_meta(c.id)
                    .is_ok_and(|m| c.upgrade_level < m.max_upgrade_level)
            })
        })
        .count() as i32;
    let committed: i32 = state.player_creature().map_or(0, |pc| {
        pc.powers
            .iter()
            .filter(|p| p.id == PowerId::ImprovementPower && p.amount > 0)
            .map(|p| p.amount)
            .sum()
    });
    (upgradable - committed).max(0)
}

#[inline]
pub fn allows_brightest_flame(limit: Option<i32>, spent_at_root: i32, spent_after_action: i32) -> bool {
    match limit {
        None => true,
        Some(cap) => spent_after_action <= cap.max(spent_at_root),
    }
}

pub fn predict_potion_replacement_hp_credit(
    state: &BattleState,
    policy: &crate::SearchPolicy,
) -> i32 {
    if !policy.predict_potion_reward {
        return 0;
    }
    if let Some(credit) = policy.potion_reward_replacement_hp_credit {
        return credit.max(0);
    }
    let belt_full = !state.player.potion_slots.is_empty()
        && state.player.potion_slots.iter().all(|s| s.is_some());
    let procure_blocked = state
        .player
        .relics
        .iter()
        .any(|r| !r.is_melted && r.id == RelicId::Sozu);
    let is_final_boss = state.static_ctx.room_type == RoomType::Boss
        && state.static_ctx.act_index >= 2
        && !policy.is_non_final_act3_boss;
    if !belt_full || procure_blocked || is_final_boss {
        return 0;
    }
    let room = state.static_ctx.room_type;
    if !matches!(room, RoomType::Monster | RoomType::Elite | RoomType::Boss) {
        return 0;
    }
    let forced = state
        .player
        .relics
        .iter()
        .any(|r| !r.is_melted && r.id == RelicId::WhiteBeastStatue)
        || (state.static_ctx.game_mode == GameMode::Blitz
            && (room == RoomType::Boss
                || (room == RoomType::Monster && !state.static_ctx.encounter_is_weak)));
    let drop = if forced {
        true
    } else {
        let chance = (0.40f32 + if room == RoomType::Elite { 0.125 } else { 0.0 }).clamp(0.0, 1.0);
        let mut rng = *state.player_rngs.get(PlayerRngType::Rewards);
        rng.random.next_float() < chance
    };
    if drop {
        weights::POTION_MINIMUM_HP_SAVED
    } else {
        0
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Serialize, Deserialize)]
pub enum BossHpRelief {
    #[default]
    None,
    ActClearHeal,
    RunEnding,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Serialize, Deserialize)]
pub enum SolverPotionPolicy {
    Disabled,
    RequireAtLeastOne,
    #[default]
    Smart,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Serialize, Deserialize)]
pub enum SolverTheftPolicy {
    #[default]
    Ignore,
    PreserveResources,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct PostCombatRelicHealProfile {
    pub unconditional_heal: i32,
    pub wounded_heal: i32,
    pub wounded_hp_percent: i32,
}

impl PostCombatRelicHealProfile {
    pub fn from_state(state: &BattleState) -> Self {
        let mut unconditional_heal = 0i32;
        let mut wounded_heal = 0i32;
        let mut wounded_hp_percent = 0i32;
        for r in &state.player.relics {
            if r.is_melted {
                continue;
            }
            match r.id {
                RelicId::BurningBlood => unconditional_heal += 6,
                RelicId::BlackBlood => unconditional_heal += 12,
                RelicId::MeatOnTheBone => {
                    wounded_heal += 12;
                    wounded_hp_percent = 50;
                }
                _ => {}
            }
        }
        Self {
            unconditional_heal,
            wounded_heal,
            wounded_hp_percent,
        }
    }

    #[inline]
    pub fn heal_for(self, final_hp: i32, final_max_hp: i32) -> i32 {
        let mut capacity = self.unconditional_heal.max(0);
        if self.wounded_heal > 0 && final_hp <= (final_max_hp.max(0) * self.wounded_hp_percent.max(0)) / 100 {
            capacity += self.wounded_heal;
        }
        capacity.clamp(0, (final_max_hp - final_hp).max(0))
    }

    #[inline]
    pub fn monotone_heal_for(self, final_hp: i32, final_max_hp: i32) -> i32 {
        self.unconditional_heal
            .max(0)
            .clamp(0, (final_max_hp - final_hp).max(0))
    }
}

pub struct ActEndingBossPolicy;

impl ActEndingBossPolicy {
    #[inline]
    pub fn resolve_strategic_hp_relief(
        encounter_relief: BossHpRelief,
        act_transition_strategy: BossHpStrategy,
        final_boss_strategy: BossHpStrategy,
    ) -> BossHpRelief {
        match encounter_relief {
            BossHpRelief::ActClearHeal if act_transition_strategy == BossHpStrategy::MinimizeHpLoss => {
                BossHpRelief::None
            }
            BossHpRelief::RunEnding if final_boss_strategy == BossHpStrategy::MinimizeHpLoss => {
                BossHpRelief::None
            }
            other => other,
        }
    }

    #[inline]
    pub fn raw_hp_required_for_persistent_value(persistent_hp_value: i32, relief: BossHpRelief) -> i32 {
        if persistent_hp_value <= 0 {
            return 0;
        }
        match relief {
            BossHpRelief::ActClearHeal => persistent_hp_value.saturating_mul(5),
            BossHpRelief::RunEnding => i32::MAX / 4,
            BossHpRelief::None => persistent_hp_value,
        }
    }

    #[inline]
    pub fn persistent_value_of_recovered_hp(recovered_hp: i32, relief: BossHpRelief) -> i32 {
        if recovered_hp <= 0 {
            return 0;
        }
        let denom = Self::raw_hp_required_for_persistent_value(1, relief);
        if denom <= 0 {
            0
        } else {
            recovered_hp / denom
        }
    }

    #[inline]
    pub fn death_save_premium(death_save_hp_restored: i32) -> i32 {
        death_save_hp_restored.max(0) * weights::DEATH_SAVE_PREMIUM_PERCENT / 100
    }

    #[inline]
    pub fn death_save_beam_cost(death_save_hp_restored: i32) -> i32 {
        let r = death_save_hp_restored.max(0);
        r + Self::death_save_premium(r)
    }

    #[inline]
    pub fn strategic_hp_deficit(
        cumulative_hp_lost: i32,
        max_hp_deficit: i32,
        recovered_hp: i32,
        relief: BossHpRelief,
        death_save_hp_restored: i32,
    ) -> i32 {
        cumulative_hp_lost + max_hp_deficit
            - Self::persistent_value_of_recovered_hp(recovered_hp - death_save_hp_restored, relief)
            + Self::death_save_premium(death_save_hp_restored)
    }

    #[inline]
    pub fn ranked_post_combat_relic_heal(
        profile: PostCombatRelicHealProfile,
        complete_victory: bool,
        final_hp: i32,
        final_max_hp: i32,
    ) -> i32 {
        if complete_victory && final_hp > 0 {
            profile.monotone_heal_for(final_hp, final_max_hp)
        } else {
            0
        }
    }
}

pub struct PotionValuationRegistry;

impl PotionValuationRegistry {
    #[inline]
    pub fn strategic_hp_cost(id: PotionId, renewable_potion_shaped_rock: bool) -> i32 {
        if renewable_potion_shaped_rock && id == PotionId::PotionShapedRock {
            return 0;
        }
        match id {
            PotionId::GlowwaterPotion
            | PotionId::SwiftPotion
            | PotionId::GamblersBrew
            | PotionId::Duplicator
            | PotionId::OrobicAcid
            | PotionId::PotOfGhouls => weights::POTION_HIGH_VALUE_HP_SAVED,
            PotionId::DistilledChaos
            | PotionId::Clarity
            | PotionId::RadiantTincture
            | PotionId::CureAll
            | PotionId::LiquidMemories
            | PotionId::BottledPotential
            | PotionId::TouchOfInsanity => weights::POTION_ELEVATED_VALUE_HP_SAVED,
            _ => weights::POTION_MINIMUM_HP_SAVED,
        }
    }

    #[inline]
    pub fn strategic_hp_cost_with_overrides(
        id: PotionId,
        renewable_potion_shaped_rock: bool,
        overrides: &std::collections::BTreeMap<PotionId, i32>,
    ) -> i32 {
        if renewable_potion_shaped_rock && id == PotionId::PotionShapedRock {
            return 0;
        }
        if let Some(&cost) = overrides.get(&id) {
            return cost;
        }
        Self::strategic_hp_cost(id, false)
    }

    #[inline]
    pub fn requires_opening_use(id: PotionId) -> bool {
        matches!(
            id,
            PotionId::DexterityPotion
                | PotionId::FocusPotion
                | PotionId::FyshOil
                | PotionId::LiquidBronze
                | PotionId::MazalethsGift
                | PotionId::PotionOfCapacity
                | PotionId::SoldiersStew
                | PotionId::StrengthPotion
        )
    }
}

pub struct PotionUsePolicy;

impl PotionUsePolicy {
    #[inline]
    pub fn additional_required_use_strategic_hp_cost(strategic_hp_cost: i32) -> i32 {
        (strategic_hp_cost - weights::POTION_MINIMUM_HP_SAVED).max(0)
    }

    #[inline]
    pub fn apply_replacement_credit(
        optional_potion_strategic_cost: i32,
        optional_potion_count: i32,
        replacement_hp_credit: i32,
    ) -> i32 {
        if optional_potion_strategic_cost > 0
            && optional_potion_count > 0
            && replacement_hp_credit > 0
        {
            (optional_potion_strategic_cost - replacement_hp_credit).max(1)
        } else {
            optional_potion_strategic_cost
        }
    }

    #[inline]
    pub fn hp_saved(potion_free_hp_deficit: i32, potion_route_hp_deficit: i32) -> i32 {
        (potion_free_hp_deficit - potion_route_hp_deficit).max(0)
    }

    #[inline]
    pub fn smart_required_hp_saved(strategic_hp_cost: i32, relief: BossHpRelief) -> i32 {
        ActEndingBossPolicy::raw_hp_required_for_persistent_value(strategic_hp_cost, relief)
    }

    #[inline]
    pub fn ambergris_required_hp_saved(maximum_hp: i32) -> i32 {
        // Math.Ceiling(maximumHp * 0.40m) == (maximumHp * 4 + 9) / 10
        if maximum_hp <= 0 {
            0
        } else {
            (maximum_hp * 4 + 9) / 10
        }
    }

    #[inline]
    pub fn effective_strategic_hp_cost(
        strategic_hp_cost: i32,
        ambergris_count: i32,
        maximum_hp: i32,
    ) -> i32 {
        strategic_hp_cost
            + ambergris_count
                * (Self::ambergris_required_hp_saved(maximum_hp) - weights::POTION_MINIMUM_HP_SAVED)
    }

    #[inline]
    pub fn meets_ambergris_restriction(
        has_potion_free_baseline: bool,
        ambergris_count: i32,
        strategic_hp_cost: i32,
        maximum_hp: i32,
        potion_free_player_hp: i32,
        potion_route_player_hp: i32,
    ) -> bool {
        if ambergris_count == 0 {
            return true;
        }
        if !has_potion_free_baseline {
            return false;
        }
        let required =
            Self::effective_strategic_hp_cost(strategic_hp_cost, ambergris_count, maximum_hp);
        (potion_route_player_hp - potion_free_player_hp).max(0) >= required
    }

    #[allow(clippy::too_many_arguments)]
    #[inline]
    pub fn is_eligible(
        policy: SolverPotionPolicy,
        explicit_potion_count: i32,
        strategic_hp_cost: i32,
        potion_free_won: bool,
        potion_free_hp_deficit: i32,
        any_route_won: bool,
        potion_route_won: bool,
        potion_route_hp_deficit: i32,
    ) -> bool {
        match policy {
            SolverPotionPolicy::Disabled => explicit_potion_count == 0,
            SolverPotionPolicy::RequireAtLeastOne => {
                explicit_potion_count > 0
                    && (!any_route_won || potion_route_won)
                    && (explicit_potion_count == 1
                        || !potion_free_won
                        || Self::hp_saved(potion_free_hp_deficit, potion_route_hp_deficit)
                            >= Self::additional_required_use_strategic_hp_cost(strategic_hp_cost))
            }
            SolverPotionPolicy::Smart => {
                explicit_potion_count == 0
                    || (potion_route_won && !potion_free_won)
                    || Self::hp_saved(potion_free_hp_deficit, potion_route_hp_deficit)
                        >= Self::smart_required_hp_saved(strategic_hp_cost, BossHpRelief::None)
            }
        }
    }
}

/// Power card valuation commitment priorities matching `PowerCardValuationContracts.cs`.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Hash, Ord, PartialOrd)]
pub enum PowerRoutePriority {
    #[default]
    Low = 0,
    Normal = 1,
    Strong = 2,
    Core = 3,
    Dedicated = 4,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct PowerRouteAdmissionPolicy {
    pub priority: PowerRoutePriority,
    pub minimum_projection: i32,
}

pub struct PowerCardValuationRegistry;

impl PowerCardValuationRegistry {
    pub fn contains_card_id(id: CardId) -> bool {
        Self::admission(id).is_some()
    }

    pub fn admission(id: CardId) -> Option<PowerRouteAdmissionPolicy> {
        match id {
            CardId::EchoForm | CardId::Corruption | CardId::BiasedCognition => {
                Some(PowerRouteAdmissionPolicy {
                    priority: PowerRoutePriority::Dedicated,
                    minimum_projection: 1,
                })
            }
            CardId::Afterimage | CardId::DarkEmbrace | CardId::FeelNoPain | CardId::Barricade => {
                Some(PowerRouteAdmissionPolicy {
                    priority: PowerRoutePriority::Core,
                    minimum_projection: 1,
                })
            }
            CardId::DemonForm | CardId::Defragment | CardId::Footwork | CardId::Inflame
            | CardId::Accuracy | CardId::NoxiousFumes | CardId::Buffer => {
                Some(PowerRouteAdmissionPolicy {
                    priority: PowerRoutePriority::Strong,
                    minimum_projection: 1,
                })
            }
            CardId::InfiniteBlades | CardId::Juggernaut | CardId::Rupture | CardId::HelloWorld
            | CardId::Loop | CardId::ToolsOfTheTrade | CardId::WellLaidPlans => {
                Some(PowerRouteAdmissionPolicy {
                    priority: PowerRoutePriority::Normal,
                    minimum_projection: 1,
                })
            }
            _ => None,
        }
    }

    pub fn has_reachable_power(state: &BattleState) -> bool {
        state
            .cards
            .iter()
            .any(|c| !c.removed_from_state && c.pile != PileType::Exhaust && Self::contains_card_id(c.id))
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct RouteQuality {
    pub won: bool,
    pub survives: bool,
    pub death_save_use_count: i32,
    pub outstanding_stolen_resource: i32,
    pub strategic_hp_deficit: i32,
    pub growth_hp_credit: i32,
    pub growth_reward_count: i32,
    pub combat_ended_turn: Option<i32>,
    pub projected_battle_hp_lost: i32,
    pub potion_strategic_cost: i32,
    pub projected_battle_potion_count: i32,
    pub enemy_hp: i32,
    pub score: f64,
    pub theft_policy: Option<SolverTheftPolicy>,
    pub forfeits_encounter_gold: bool,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RouteQualityProjection {
    Primary,
    Interim,
    PotionPolicy,
}

pub struct RouteQualityPolicy;

impl RouteQualityPolicy {
    #[inline]
    pub fn is_complete_victory(
        action_count: usize,
        all_enemies_dead: bool,
        player_dead: bool,
        projected_player_hp: i32,
    ) -> bool {
        action_count > 0 && all_enemies_dead && !player_dead && projected_player_hp > 0
    }

    #[inline]
    pub fn is_resource_trade_improvement(
        candidate_hp_deficit: i32,
        candidate_potion_cost: i32,
        current_hp_deficit: i32,
        current_potion_cost: i32,
    ) -> bool {
        let candidate_burden = candidate_hp_deficit.saturating_add(candidate_potion_cost);
        let current_burden = current_hp_deficit.saturating_add(current_potion_cost);
        candidate_burden < current_burden
            || (candidate_burden == current_burden && candidate_hp_deficit < current_hp_deficit)
    }

    pub fn compare(
        candidate: &RouteQuality,
        current: &RouteQuality,
        projection: RouteQualityProjection,
    ) -> std::cmp::Ordering {
        use std::cmp::Ordering;
        if projection == RouteQualityProjection::Primary {
            return Self::compare_primary(candidate, current);
        }

        let cmp = current.won.cmp(&candidate.won);
        if cmp != Ordering::Equal {
            return cmp;
        }
        let cmp = current.survives.cmp(&candidate.survives);
        if cmp != Ordering::Equal {
            return cmp;
        }
        let cmp = candidate.death_save_use_count.cmp(&current.death_save_use_count);
        if cmp != Ordering::Equal {
            return cmp;
        }

        if projection == RouteQualityProjection::Interim {
            if Self::is_resource_trade_improvement(
                candidate.strategic_hp_deficit,
                candidate.potion_strategic_cost,
                current.strategic_hp_deficit,
                current.potion_strategic_cost,
            ) {
                return Ordering::Less;
            }
            if Self::is_resource_trade_improvement(
                current.strategic_hp_deficit,
                current.potion_strategic_cost,
                candidate.strategic_hp_deficit,
                candidate.potion_strategic_cost,
            ) {
                return Ordering::Greater;
            }
            let cmp = current.growth_hp_credit.cmp(&candidate.growth_hp_credit);
            if cmp != Ordering::Equal {
                return cmp;
            }
            let cmp = current.growth_reward_count.cmp(&candidate.growth_reward_count);
            if cmp != Ordering::Equal {
                return cmp;
            }
            if candidate.strategic_hp_deficit == current.strategic_hp_deficit
                && candidate.potion_strategic_cost == current.potion_strategic_cost
                && candidate.projected_battle_potion_count == current.projected_battle_potion_count
                && candidate.projected_battle_hp_lost != current.projected_battle_hp_lost
            {
                return candidate
                    .projected_battle_hp_lost
                    .cmp(&current.projected_battle_hp_lost);
            }
            let cmp = candidate
                .combat_ended_turn
                .unwrap_or(i32::MAX)
                .cmp(&current.combat_ended_turn.unwrap_or(i32::MAX));
            if cmp != Ordering::Equal {
                return cmp;
            }
            let cmp = candidate
                .projected_battle_potion_count
                .cmp(&current.projected_battle_potion_count);
            if cmp != Ordering::Equal {
                return cmp;
            }
            let cmp = candidate.enemy_hp.cmp(&current.enemy_hp);
            return if cmp != Ordering::Equal {
                cmp
            } else {
                Self::compare_score(candidate.score, current.score)
            };
        }

        if projection == RouteQualityProjection::PotionPolicy {
            let candidate_cost =
                candidate.strategic_hp_deficit as i64 + candidate.potion_strategic_cost as i64;
            let current_cost =
                current.strategic_hp_deficit as i64 + current.potion_strategic_cost as i64;
            let cmp = candidate_cost.cmp(&current_cost);
            if cmp != Ordering::Equal {
                return cmp;
            }
            if candidate.won
                && current.won
                && candidate.strategic_hp_deficit == current.strategic_hp_deficit
                && candidate.potion_strategic_cost == current.potion_strategic_cost
                && candidate.projected_battle_potion_count == current.projected_battle_potion_count
                && candidate.growth_hp_credit == current.growth_hp_credit
                && candidate.growth_reward_count == current.growth_reward_count
                && candidate.projected_battle_hp_lost != current.projected_battle_hp_lost
            {
                return candidate
                    .projected_battle_hp_lost
                    .cmp(&current.projected_battle_hp_lost);
            }
        }

        let cmp = Self::compare_primary(candidate, current);
        if cmp != Ordering::Equal {
            return cmp;
        }
        let cmp = candidate
            .projected_battle_potion_count
            .cmp(&current.projected_battle_potion_count);
        if cmp != Ordering::Equal {
            cmp
        } else {
            Self::compare_score(candidate.score, current.score)
        }
    }

    #[inline]
    pub fn compare_primary(candidate: &RouteQuality, current: &RouteQuality) -> std::cmp::Ordering {
        use std::cmp::Ordering;
        let cmp = current.won.cmp(&candidate.won);
        if cmp != Ordering::Equal {
            return cmp;
        }
        let cmp = candidate.death_save_use_count.cmp(&current.death_save_use_count);
        if cmp != Ordering::Equal {
            return cmp;
        }
        let cmp = candidate.strategic_hp_deficit.cmp(&current.strategic_hp_deficit);
        if cmp != Ordering::Equal {
            return cmp;
        }
        let cmp = current.growth_hp_credit.cmp(&candidate.growth_hp_credit);
        if cmp != Ordering::Equal {
            return cmp;
        }
        let cmp = current.growth_reward_count.cmp(&candidate.growth_reward_count);
        if cmp != Ordering::Equal {
            return cmp;
        }
        candidate
            .combat_ended_turn
            .unwrap_or(i32::MAX)
            .cmp(&current.combat_ended_turn.unwrap_or(i32::MAX))
    }

    #[inline]
    fn compare_score(candidate: f64, current: f64) -> std::cmp::Ordering {
        if candidate > current {
            std::cmp::Ordering::Less
        } else if current > candidate {
            std::cmp::Ordering::Greater
        } else {
            std::cmp::Ordering::Equal
        }
    }
}

/// Stage-1 fast single-pass summary scanned directly from `BattleState` before heavy Stage-2 materialization.
#[derive(Clone, Copy, Debug, Default)]
pub struct FastEvalSummary {
    pub player_hp: i32,
    pub player_max_hp: i32,
    pub player_block: i32,
    pub projected_player_hp: i32,
    pub cumulative_hp_lost: i32,
    pub recovered_hp: i32,
    pub enemy_hp: i32,
    pub raw_enemy_hp: i32,
    pub enemy_block: i32,
    pub alive_enemy_count: i32,
    pub alive_enemy_mask: u64,
    pub player_dead: bool,
    pub all_enemies_dead: bool,
    pub won: bool,
    pub has_pending_choice: bool,
    pub death_save_use_count: i32,
    pub death_save_hp_restored: i32,
    pub growth_hp_credit: i32,
    pub growth_reward_count: i32,
    pub raw_growth_reward_count: i32,
    pub growth_rewards: GrowthCounters,
    pub relic_eval: RelicCounterEvaluation,
    pub strategy_goal_hp_credit: i32,
    pub strategic_hp_credit: i32,
    pub strategy_goal_count: i32,
    pub brightest_flame_max_hp_spent: i32,
    pub energy: i32,
    pub stars: i32,
    pub hand_count: i32,
    pub live_deck_size: i32,
    pub live_deck_clutter: i32,
    pub combat_ended_turn: Option<i32>,
    pub base_score: f64,
}

impl FastEvalSummary {
    #[inline]
    pub fn transposition_label(
        &self,
        potion_count: u16,
        potion_strategic_cost: i32,
        future_sold_hp: i32,
        action_count: u16,
    ) -> TranspositionLabel {
        TranspositionLabel {
            potion_count: potion_count as i32,
            potion_strategic_cost,
            future_sold_hp,
            cumulative_player_hp_lost: self.cumulative_hp_lost,
            action_count: action_count as i32,
            score: self.base_score,
        }
    }
}

#[derive(Clone, Copy, Debug, Default)]
pub struct StandPatEvaluation {
    pub projected_hp: i32,
    pub death_save_hp_restored: i32,
    pub all_enemies_dead: bool,
    pub resource_value: i32,
    pub delayed_damage: i32,
}

/// 256-sharded thread-safe stand-pat threat projection cache keyed by `(StateHash, round_index)`.
pub struct StandPatCache {
    shards: Vec<RwLock<HashMap<(StateHash, i32), StandPatEvaluation, ahash::RandomState>>>,
}

impl Default for StandPatCache {
    fn default() -> Self {
        Self::new()
    }
}

impl StandPatCache {
    pub fn new() -> Self {
        let mut shards = Vec::with_capacity(256);
        for _ in 0..256 {
            shards.push(RwLock::new(HashMap::with_hasher(
                ahash::RandomState::with_seeds(1, 2, 3, 4),
            )));
        }
        Self { shards }
    }

    pub fn get_or_compute(
        &self,
        state_hash: StateHash,
        round_index: i32,
        state: &BattleState,
        content: &dyn ContentDispatcher,
        scratch: &mut BattleState,
        pack_buf: &mut Vec<u8>,
    ) -> StandPatEvaluation {
        let shard = &self.shards[state_hash.shard_index()];
        if let Some(hit) = shard.read().get(&(state_hash, round_index)).copied() {
            return hit;
        }
        let val = compute_stand_pat(state, content, scratch, pack_buf);
        let mut w = shard.write();
        if w.len() < 1024 {
            w.insert((state_hash, round_index), val);
        }
        val
    }
}

fn compute_stand_pat(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    scratch: &mut BattleState,
    pack_buf: &mut Vec<u8>,
) -> StandPatEvaluation {
    if !state.is_in_progress {
        let hp = state
            .player_creature()
            .map_or(state.player.current_hp, |c| c.current_hp);
        return StandPatEvaluation {
            projected_hp: hp,
            death_save_hp_restored: 0,
            all_enemies_dead: state.won,
            resource_value: state.player.energy * 16 + state.player.stars * 8,
            delayed_damage: 0,
        };
    }

    pack_state(state, pack_buf);
    if unpack_state(pack_buf, state.static_ctx.clone(), scratch).is_err() {
        let hp = state
            .player_creature()
            .map_or(state.player.current_hp, |c| c.current_hp);
        return StandPatEvaluation {
            projected_hp: hp,
            ..Default::default()
        };
    }

    let _ = CombatEngine::end_player_turn(scratch, content);
    let hp = scratch
        .player_creature()
        .map_or(scratch.player.current_hp, |c| c.current_hp);
    let all_dead = scratch
        .enemy_handles
        .iter()
        .all(|&h| scratch.creature(h).is_none_or(|c| c.is_dead()));
    let mut delayed_damage = 0i32;
    for &eh in &scratch.enemy_handles {
        if let Some(c) = scratch.creature(eh) {
            if c.is_alive() {
                for p in &c.powers {
                    if matches!(p.id, PowerId::PoisonPower | PowerId::DoomPower) {
                        delayed_damage += p.amount.max(0);
                    }
                }
            }
        }
    }
    StandPatEvaluation {
        projected_hp: hp,
        death_save_hp_restored: 0,
        all_enemies_dead: all_dead,
        resource_value: scratch.player.energy * 16 + scratch.player.stars * 8,
        delayed_damage,
    }
}

#[derive(Clone, Debug, Default)]
pub struct RootEvalBaseline {
    pub initial_player_hp: i32,
    pub initial_player_max_hp: i32,
    pub initial_cumulative_hp_lost: i32,
    pub initial_enemy_count: usize,
    pub start_turn_number: i32,
    pub initial_persistent_buff_value: i32,
    pub initial_retained_attack_value: i32,
    pub initial_enemy_strength_suppression: i32,
    pub initial_enemy_weak_turns: i32,
    pub boss_hp_relief: BossHpRelief,
    pub post_combat_relic_heal: PostCombatRelicHealProfile,
    pub is_act_ending_boss: bool,
    pub renewable_potion_shaped_rock: bool,
    pub potion_replacement_hp_credit: i32,
    pub active_relic_targets: Vec<RelicCounterTarget>,
    pub ignore_long_term_rewards: bool,
    pub effective_growth_budgets: GrowthCounters,
    pub growth_opportunity_targets: GrowthOpportunityTargets,
    pub effective_has_growth_targets: bool,
    pub mad_science_upgrade_capacity: i32,
    pub initial_growth_rewards: GrowthCounters,
    pub initial_brightest_flame_max_hp_spent: i32,
    pub root_state_brightest_flame_spent: i32,
    pub prior_battle_hp_lost: i32,
    pub all_forced_potion_mask: u64,
    pub minimum_required_potion_uses: usize,
    pub can_stop_at_hp_target: bool,
}

impl RootEvalBaseline {
    pub fn from_state(state: &BattleState, content: &dyn ContentDispatcher) -> Self {
        Self::from_state_with_policy(state, content, &crate::SearchPolicy::default())
    }

    pub fn from_state_with_policy(
        state: &BattleState,
        content: &dyn ContentDispatcher,
        policy: &crate::SearchPolicy,
    ) -> Self {
        let initial_player_hp = state
            .player_creature()
            .map_or(state.player.current_hp, |c| c.current_hp);
        let initial_player_max_hp = state
            .player_creature()
            .map_or(state.player.max_hp, |c| c.max_hp);
        let encounter_relief = if state.static_ctx.room_type == RoomType::Boss {
            if state.static_ctx.act_index < 2 {
                BossHpRelief::ActClearHeal
            } else if policy.is_non_final_act3_boss {
                BossHpRelief::None
            } else {
                BossHpRelief::RunEnding
            }
        } else {
            BossHpRelief::None
        };
        let boss_hp_relief = ActEndingBossPolicy::resolve_strategic_hp_relief(
            encounter_relief,
            policy.act_transition_boss_hp_strategy,
            policy.final_boss_hp_strategy,
        );
        let renewable_potion_shaped_rock = state
            .player
            .relics
            .iter()
            .any(|r| !r.is_melted && r.id == RelicId::PetrifiedToad);
        let potion_replacement_hp_credit = predict_potion_replacement_hp_credit(state, policy);
        let active_relic_targets = RelicCounterPolicy::capture_targets(
            state,
            policy.relic_strategy_enabled,
            &policy.relic_counter_rules,
        );
        let growth_opportunity_targets = GrowthOpportunityTargets::capture(state, content);
        let effective_has_growth_targets =
            !policy.ignore_long_term_rewards && growth_opportunity_targets.has_targets;
        let effective_growth_budgets = if policy.ignore_long_term_rewards {
            GrowthCounters::default()
        } else {
            policy.growth_budgets
        };
        let can_stop_at_hp_target = policy.stop_at_acceptable_battle_hp_loss
            && (!effective_has_growth_targets || growth_opportunity_targets.is_bounded);

        let mut baseline = Self {
            initial_player_hp,
            initial_player_max_hp,
            initial_cumulative_hp_lost: state
                .player_creature()
                .map_or(state.player.cumulative_hp_lost, |c| c.cumulative_hp_lost),
            initial_enemy_count: state.enemy_handles.len(),
            start_turn_number: state.player.turn_number,
            initial_persistent_buff_value: 0,
            initial_retained_attack_value: 0,
            initial_enemy_strength_suppression: 0,
            initial_enemy_weak_turns: 0,
            boss_hp_relief,
            post_combat_relic_heal: PostCombatRelicHealProfile::from_state(state),
            is_act_ending_boss: state.static_ctx.room_type == RoomType::Boss,
            renewable_potion_shaped_rock,
            potion_replacement_hp_credit,
            active_relic_targets,
            ignore_long_term_rewards: policy.ignore_long_term_rewards,
            effective_growth_budgets,
            growth_opportunity_targets,
            effective_has_growth_targets,
            mad_science_upgrade_capacity: mad_science_upgrade_capacity(state, content),
            initial_growth_rewards: state.semantic_history.growth_rewards,
            initial_brightest_flame_max_hp_spent: policy.initial_brightest_flame_max_hp_spent,
            root_state_brightest_flame_spent: state.semantic_history.brightest_flame_max_hp_spent,
            prior_battle_hp_lost: policy.prior_battle_hp_lost,
            all_forced_potion_mask: policy.all_forced_potion_mask(),
            minimum_required_potion_uses: policy.minimum_required_potion_uses(),
            can_stop_at_hp_target,
        };
        let fast = evaluate_fast(state, content, &baseline, None);
        let full = evaluate_full(state, content, &fast, &baseline);
        baseline.initial_persistent_buff_value = full.persistent_buff_value;
        baseline.initial_retained_attack_value = full.retained_attack_value;
        baseline.initial_enemy_strength_suppression = full.enemy_strength_suppression;
        baseline.initial_enemy_weak_turns = full.enemy_weak_turns;
        baseline
    }

    #[inline]
    pub fn growth_target_satisfied(&self, rewards: &GrowthCounters) -> bool {
        self.can_stop_at_hp_target
            && (!self.effective_has_growth_targets
                || self.growth_opportunity_targets.is_satisfied_by(rewards))
    }

    #[inline]
    pub fn relic_targets_satisfied(&self, eval: &RelicCounterEvaluation) -> bool {
        eval.all_satisfied()
    }

    #[inline]
    pub fn brightest_flame_spent(&self, state: &BattleState) -> i32 {
        self.initial_brightest_flame_max_hp_spent
            + (state.semantic_history.brightest_flame_max_hp_spent
                - self.root_state_brightest_flame_spent)
                .max(0)
    }
}

/// Fast single-pass scan over `BattleState` with zero heap allocations.
pub fn evaluate_fast(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    root: &RootEvalBaseline,
    stand_pat_hp: Option<i32>,
) -> FastEvalSummary {
    let pc = state.player_creature();
    let player_hp = pc.map_or(state.player.current_hp, |c| c.current_hp);
    let player_max_hp = pc.map_or(state.player.max_hp, |c| c.max_hp);
    let player_block = pc.map_or(state.player.block, |c| c.block);
    let cumulative_hp_lost = pc.map_or(state.player.cumulative_hp_lost, |c| c.cumulative_hp_lost);

    let mut enemy_hp = 0i32;
    let mut raw_enemy_hp = 0i32;
    let mut enemy_block = 0i32;
    let mut alive_enemy_count = 0i32;
    let mut alive_enemy_mask = 0u64;

    for (idx, &eh) in state.enemy_handles.iter().enumerate() {
        if let Some(ec) = state.creature(eh) {
            let hp = ec.current_hp.max(0);
            raw_enemy_hp += hp;
            if ec.is_alive() && state.contains_creature(eh) {
                enemy_hp += hp;
                enemy_block += ec.block.max(0);
                alive_enemy_count += 1;
                if idx < 64 {
                    alive_enemy_mask |= 1u64 << idx;
                }
            }
        }
    }

    let has_pending_choice = state.pending.is_some();
    let player_dead = pc.is_none_or(|c| c.is_dead()) || (!state.is_in_progress && !state.won);
    let all_enemies_dead = alive_enemy_count == 0;
    let won = !player_dead && !has_pending_choice && (!state.is_in_progress && state.won || all_enemies_dead);

    let projected_player_hp = if won {
        player_hp
    } else if player_dead {
        0
    } else {
        stand_pat_hp.unwrap_or(player_hp)
    };

    let mut live_deck_size = 0i32;
    let mut live_deck_clutter = 0i32;
    for pile in [&state.player.hand[..], &state.player.draw[..], &state.player.discard[..]] {
        for &ch in pile {
            live_deck_size += 1;
            if let Some(card) = state.card(ch) {
                if let Ok(meta) = content.card_meta(card.id) {
                    if matches!(meta.card_type, CardType::Status | CardType::Curse) {
                        live_deck_clutter += 1;
                    }
                }
            }
        }
    }

    let recovered_hp = (player_hp - root.initial_player_hp
        + (cumulative_hp_lost - root.initial_cumulative_hp_lost))
        .max(0);

    let mut raw_growth = state.semantic_history.growth_rewards;
    let delta_ms = (raw_growth.mad_science - root.initial_growth_rewards.mad_science)
        .clamp(0, root.mad_science_upgrade_capacity);
    raw_growth.mad_science = root.initial_growth_rewards.mad_science + delta_ms;
    let raw_growth_reward_count = raw_growth.total();

    let (growth_rewards, growth_hp_credit, growth_reward_count) = if root.ignore_long_term_rewards {
        (GrowthCounters::default(), 0, 0)
    } else {
        let credit = root.effective_growth_budgets.credit(&raw_growth);
        (raw_growth, credit, raw_growth_reward_count)
    };

    let relic_eval = RelicCounterPolicy::evaluate(
        state,
        &root.active_relic_targets,
        won,
        root.post_combat_relic_heal,
        root.boss_hp_relief,
    );
    let strategy_goal_hp_credit = growth_hp_credit.saturating_add(relic_eval.hp_credit);
    let strategic_hp_credit = strategy_goal_hp_credit.saturating_add(relic_eval.healing_hp_credit);
    let strategy_goal_count = growth_reward_count.saturating_add(relic_eval.satisfied_priority);
    let brightest_flame_max_hp_spent = root.brightest_flame_spent(state);

    let mut base_score = if player_dead || projected_player_hp <= 0 {
        weights::DEATH_PENALTY
    } else {
        projected_player_hp as f64 * weights::HP
    };
    base_score += (player_max_hp - root.initial_player_max_hp) as f64 * weights::HP;
    base_score -= cumulative_hp_lost as f64 * weights::HP;
    base_score += (growth_hp_credit + relic_eval.total_hp_credit()) as f64 * weights::HP;
    base_score += relic_eval.satisfied_priority as f64 * 0.1 - relic_eval.distance as f64 * 0.001;
    if won {
        base_score += weights::VICTORY_BONUS;
    }
    base_score += enemy_hp as f64 * weights::ENEMY_HP;
    base_score += live_deck_clutter as f64 * weights::LIVE_DECK_CLUTTER_PENALTY;

    FastEvalSummary {
        player_hp,
        player_max_hp,
        player_block,
        projected_player_hp,
        cumulative_hp_lost,
        recovered_hp,
        enemy_hp,
        raw_enemy_hp,
        enemy_block,
        alive_enemy_count,
        alive_enemy_mask,
        player_dead,
        all_enemies_dead,
        won,
        has_pending_choice,
        death_save_use_count: 0,
        death_save_hp_restored: 0,
        growth_hp_credit,
        growth_reward_count,
        raw_growth_reward_count,
        growth_rewards,
        relic_eval,
        strategy_goal_hp_credit,
        strategic_hp_credit,
        strategy_goal_count,
        brightest_flame_max_hp_spent,
        energy: state.player.energy,
        stars: state.player.stars,
        hand_count: state.player.hand.len() as i32,
        live_deck_size,
        live_deck_clutter,
        combat_ended_turn: if won {
            Some(state.player.turn_number)
        } else {
            None
        },
        base_score,
    }
}

/// Stage-2 full evaluation details materialized lazily ONLY for nodes that survive TT & pruning.
#[derive(Clone, Copy, Debug, Default)]
pub struct FullEvalDetails {
    pub persistent_buff_value: i32,
    pub latent_setup_value: i32,
    pub replay_potential_value: i32,
    pub retained_attack_value: i32,
    pub reachable_hand_value: i32,
    pub future_resource_value: i32,
    pub delayed_damage_value: i32,
    pub reactive_damage_value: i32,
    pub enemy_strength_suppression: i32,
    pub enemy_weak_turns: i32,
    pub enemy_vulnerable_turns: i32,
    pub focus_target_pressure: i32,
    pub focus_target_remaining_hp: i32,
    pub osty_hp: i32,
    pub osty_max_hp: i32,
    pub playable_attack_count: i32,
    pub enemy_combat_dist_hash: u64,
    pub unordered_pile_hash: u64,
    pub best_power_priority: PowerRoutePriority,
    pub beam_rank_score: f64,
}

#[inline]
fn card_base_heuristic_value(card_id: CardId, upgrade_level: i32, meta_type: CardType) -> i32 {
    let upg = if upgrade_level > 0 { 3 } else { 0 };
    match card_id {
        CardId::Bash => 8 + upg,
        CardId::Neutralize => 4 + upg,
        CardId::Survivor => 8 + upg,
        CardId::Bodyguard => 5 + upg,
        CardId::Unleash => 6 + upg,
        CardId::FallingStar => 7 + upg,
        _ => match meta_type {
            CardType::Attack => 6 + upg,
            CardType::Skill => 5 + upg,
            CardType::Power => 8 + upg,
            _ => 0,
        },
    }
}

pub fn evaluate_full(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    fast: &FastEvalSummary,
    root: &RootEvalBaseline,
) -> FullEvalDetails {
    let mut persistent_buff_value = 0i32;
    let mut latent_setup_value = 0i32;
    let mut replay_potential_value = 0i32;
    let mut retained_attack_value = 0i32;
    let mut reachable_hand_value = 0i32;
    let mut future_resource_value = 0i32;
    let mut delayed_damage_value = 0i32;
    let mut enemy_strength_suppression = 0i32;
    let mut enemy_weak_turns = 0i32;
    let mut enemy_vulnerable_turns = 0i32;
    let mut playable_attack_count = 0i32;
    let mut best_power_priority = PowerRoutePriority::Low;

    if let Some(pc) = state.player_creature() {
        for p in &pc.powers {
            let amt = p.amount.max(0);
            match p.id {
                PowerId::StrengthPower | PowerId::DexterityPower => {
                    persistent_buff_value += amt * 2;
                }
                PowerId::AfterimagePower
                | PowerId::DarkEmbracePower
                | PowerId::CorruptionPower
                | PowerId::BufferPower => {
                    persistent_buff_value += amt * 4;
                }
                PowerId::PoisonPower => {}
                _ => {
                    if let Ok(m) = content.power_meta(p.id) {
                        if matches!(m.power_type, sts2_types::PowerType::Buff) {
                            persistent_buff_value += amt;
                        }
                    }
                }
            }
        }
    }

    for orb in &state.player.orbs {
        persistent_buff_value += match orb.id {
            OrbId::DarkOrb => 4,
            OrbId::PlasmaOrb => 6,
            _ => 2,
        };
    }

    for &ch in &state.player.hand {
        if let Some(card) = state.card(ch) {
            if let Ok(meta) = content.card_meta(card.id) {
                let val = card_base_heuristic_value(card.id, card.upgrade_level, meta.card_type);
                reachable_hand_value += val;
                if meta.card_type == CardType::Attack {
                    playable_attack_count += 1;
                }
                if card.has_local_keyword(CardKeywords::RETAIN) {
                    future_resource_value += val * 2;
                }
            }
        }
    }

    let mut pile_words: smallvec::SmallVec<[u64; 32]> = smallvec::SmallVec::new();
    for (pile_tag, pile) in [
        (1u64, &state.player.hand[..]),
        (2u64, &state.player.draw[..]),
        (3u64, &state.player.discard[..]),
    ] {
        for &ch in pile {
            if let Some(card) = state.card(ch) {
                pile_words.push((pile_tag << 32) | ((card.id as u64) << 8) | (card.upgrade_level as u64 & 0xff));
                if let Ok(meta) = content.card_meta(card.id) {
                    let val = card_base_heuristic_value(card.id, card.upgrade_level, meta.card_type);
                    if meta.card_type == CardType::Attack {
                        retained_attack_value += val.max(1);
                    } else if meta.card_type == CardType::Power {
                        latent_setup_value += 2;
                        if let Some(adm) = PowerCardValuationRegistry::admission(card.id) {
                            if adm.priority > best_power_priority {
                                best_power_priority = adm.priority;
                            }
                        }
                    }
                    if meta.flags.contains(CardStaticFlags::HAS_GROWTH) {
                        replay_potential_value += 1;
                    }
                }
            }
        }
    }
    pile_words.sort_unstable();
    let unordered_pile_hash = hash_u64_slice(&pile_words);

    let mut enemy_words: smallvec::SmallVec<[u64; 16]> = smallvec::SmallVec::new();
    let mut focus_target_remaining_hp = i32::MAX;
    for &eh in &state.enemy_handles {
        if let Some(ec) = state.creature(eh) {
            let alive_bit = u64::from(ec.is_alive() && state.contains_creature(eh));
            enemy_words.push(
                ((ec.combat_id.unwrap_or(eh.0) as u64) << 32)
                    | ((ec.current_hp.max(0) as u64) << 16)
                    | ((ec.block.max(0) as u64) << 1)
                    | alive_bit,
            );
            if ec.is_alive() && state.contains_creature(eh) {
                if ec.current_hp < focus_target_remaining_hp {
                    focus_target_remaining_hp = ec.current_hp;
                }
                for p in &ec.powers {
                    match p.id {
                        PowerId::PoisonPower => delayed_damage_value += p.amount.max(0),
                        PowerId::DoomPower => {
                            let cur = ec.current_hp.max(1);
                            let d = p.amount.max(0).min(cur);
                            delayed_damage_value += ((d as i64 * d as i64) / cur as i64) as i32;
                        }
                        PowerId::WeakPower => enemy_weak_turns += p.amount.max(0),
                        PowerId::VulnerablePower => enemy_vulnerable_turns += p.amount.max(0),
                        PowerId::StrengthPower if p.amount < 0 => {
                            enemy_strength_suppression += (-p.amount).min(16);
                        }
                        _ => {}
                    }
                }
            }
        }
    }
    if focus_target_remaining_hp == i32::MAX {
        focus_target_remaining_hp = 0;
    }
    let enemy_combat_dist_hash = hash_u64_slice(&enemy_words);

    let (osty_hp, osty_max_hp) = state
        .osty_handle()
        .and_then(|h| state.creature(h))
        .map_or((0, 0), |c| (c.current_hp.max(0), c.max_hp.max(0)));

    let mut details = FullEvalDetails {
        persistent_buff_value,
        latent_setup_value,
        replay_potential_value,
        retained_attack_value,
        reachable_hand_value,
        future_resource_value,
        delayed_damage_value,
        reactive_damage_value: 0,
        enemy_strength_suppression,
        enemy_weak_turns,
        enemy_vulnerable_turns,
        focus_target_pressure: (root.initial_enemy_count as i32 * 100 - fast.enemy_hp).max(0),
        focus_target_remaining_hp,
        osty_hp,
        osty_max_hp,
        playable_attack_count,
        enemy_combat_dist_hash,
        unordered_pile_hash,
        best_power_priority,
        beam_rank_score: 0.0,
    };

    details.beam_rank_score = compute_beam_rank_score(
        fast,
        &details,
        root,
        false,
        false,
        None,
    );
    details
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BeamWeightTerm {
    CurrentEnergy,
    PersistentBuffDelta,
    EnemyHp,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct BeamWeightPerturbation {
    pub term: BeamWeightTerm,
    pub scale: f64,
}

pub fn compute_beam_rank_score(
    fast: &FastEvalSummary,
    full: &FullEvalDetails,
    root: &RootEvalBaseline,
    base_score_only: bool,
    boss_tempo_hp_pricing: bool,
    perturbation: Option<BeamWeightPerturbation>,
) -> f64 {
    if base_score_only {
        return fast.base_score;
    }
    let boss_features = root.is_act_ending_boss || boss_tempo_hp_pricing;
    let persistent_buff_cap = if boss_features {
        weights::PERSISTENT_BUFF_DELTA_BEAM_CAP
    } else {
        weights::STANDARD_PERSISTENT_BUFF_DELTA_BEAM_CAP
    };
    let persistent_buff_value = if boss_features {
        weights::PERSISTENT_BUFF_DELTA_BEAM_VALUE
    } else {
        weights::STANDARD_PERSISTENT_BUFF_DELTA_BEAM_VALUE
    };
    let use_latent_setup = boss_features || root.initial_enemy_count > 1;
    let strength_horizon = if boss_features {
        weights::BOSS_ENEMY_STRENGTH_SUPPRESSION_HORIZON
    } else {
        weights::STANDARD_ENEMY_STRENGTH_SUPPRESSION_HORIZON
    };
    let weak_hp_saved = if boss_features {
        weights::BOSS_ENEMY_WEAK_EXPECTED_HP_SAVED
    } else {
        weights::STANDARD_ENEMY_WEAK_EXPECTED_HP_SAVED
    };

    let retained_attack_growth = (full.retained_attack_value - root.initial_retained_attack_value)
        .max(0)
        .min(weights::RETAINED_ATTACK_GROWTH_BEAM_CAP);

    let mut score = fast.base_score
        + fast.energy.min(weights::CURRENT_ENERGY_BEAM_CAP) as f64 * weights::CURRENT_ENERGY_BEAM_VALUE
        + (full.persistent_buff_value - root.initial_persistent_buff_value)
            .max(0)
            .min(persistent_buff_cap) as f64
            * persistent_buff_value
        + if use_latent_setup {
            full.latent_setup_value.min(weights::LATENT_SETUP_BEAM_CAP) as f64
                * weights::LATENT_SETUP_BEAM_VALUE
        } else {
            0.0
        }
        + if boss_features {
            full.future_resource_value as f64 * weights::FUTURE_RESOURCE_BEAM_VALUE
        } else {
            0.0
        }
        + full.replay_potential_value.min(weights::REPLAY_POTENTIAL_BEAM_CAP) as f64
            * weights::REPLAY_POTENTIAL_BEAM_VALUE
        + retained_attack_growth as f64 * weights::RETAINED_ATTACK_GROWTH_BEAM_VALUE
        + full.delayed_damage_value as f64 * weights::DELAYED_DAMAGE_BEAM_VALUE
        + (full.enemy_strength_suppression - root.initial_enemy_strength_suppression)
            .max(0)
            .min(weights::ENEMY_STRENGTH_SUPPRESSION_BEAM_CAP) as f64
            * strength_horizon as f64
            * weights::HP
        + (full.enemy_weak_turns - root.initial_enemy_weak_turns)
            .max(0)
            .min(weights::ENEMY_WEAK_TURNS_BEAM_CAP) as f64
            * weak_hp_saved as f64
            * weights::HP;

    if !fast.won && !fast.player_dead {
        if let Some(p) = perturbation {
            if (p.scale - 1.0).abs() > f64::EPSILON {
                let term = match p.term {
                    BeamWeightTerm::CurrentEnergy => {
                        fast.energy.min(weights::CURRENT_ENERGY_BEAM_CAP) as f64
                            * weights::CURRENT_ENERGY_BEAM_VALUE
                    }
                    BeamWeightTerm::PersistentBuffDelta => {
                        (full.persistent_buff_value - root.initial_persistent_buff_value)
                            .max(0)
                            .min(persistent_buff_cap) as f64
                            * persistent_buff_value
                    }
                    BeamWeightTerm::EnemyHp => fast.enemy_hp as f64 * weights::ENEMY_HP,
                };
                score += (p.scale - 1.0) * term;
            }
        }
    }

    score
}

#[derive(Clone, Copy, Debug)]
pub struct FinalPlanSelection {
    pub node_id: NodeId,
    pub won: bool,
    pub strategic_hp_deficit: i32,
    pub hp_lost: i32,
    pub potion_count: usize,
    pub score: i64,
    pub all_forced_uses_satisfied: bool,
    pub strategy_goal_hp_credit: i32,
    pub strategy_goal_count: i32,
    pub growth_rewards: GrowthCounters,
    pub relic_eval: RelicCounterEvaluation,
    pub recovered_hp: i32,
}

/// 1:1 port of `CombatBeamSolver.FinalPlanOrdering.cs`.
pub fn select_final_plan(
    arena: &NodeArena,
    candidates: &[NodeId],
    root: &RootEvalBaseline,
    potion_policy: SolverPotionPolicy,
    theft_policy: Option<SolverTheftPolicy>,
    potion_replacement_hp_credit: i32,
    minimum_potion_uses: usize,
) -> Option<FinalPlanSelection> {
    if candidates.is_empty() {
        return None;
    }

    #[derive(Clone, Copy)]
    struct EvaluatedCandidate {
        node_id: NodeId,
        quality: RouteQuality,
        explicit_potion_count: i32,
        forced_uses_satisfied: bool,
        forced_use_count: i32,
        optional_potion_count: i32,
        optional_potion_strategic_cost: i32,
        optional_ambergris_count: i32,
        effective_potion_policy: SolverPotionPolicy,
        policy_hp_deficit: i32,
        health_resource_cost: i32,
        raw_growth_reward_count: i32,
        growth_rewards: GrowthCounters,
        relic_eval: RelicCounterEvaluation,
        recovered_hp: i32,
        boundary_rank: u8,
        action_count: u16,
        player_hp: i32,
        state_hash: StateHash,
    }

    let enforce_forced = root.all_forced_potion_mask != 0;
    let evaluated: Vec<EvaluatedCandidate> = candidates
        .iter()
        .map(|&nid| {
            let node = arena.get(nid);
            let f = &node.fast_eval;
            let explicit_potion_count = node.explicit_potion_count as i32;
            let matched_forced = node.forced_used_mask & root.all_forced_potion_mask;
            let forced_uses_satisfied = matched_forced == root.all_forced_potion_mask;
            let forced_use_count = if enforce_forced {
                matched_forced.count_ones() as i32
            } else {
                0
            };
            let forced_strategic_cost = if enforce_forced {
                node.forced_strategic_hp_cost
            } else {
                0
            };
            let forced_ambergris_count = if enforce_forced {
                node.forced_ambergris_count as i32
            } else {
                0
            };
            let optional_potion_count = (explicit_potion_count - forced_use_count).max(0);
            let optional_potion_strategic_cost = PotionUsePolicy::apply_replacement_credit(
                (node.explicit_potion_strategic_cost - forced_strategic_cost).max(0),
                optional_potion_count,
                potion_replacement_hp_credit,
            );
            let optional_ambergris_count =
                (node.ambergris_count as i32 - forced_ambergris_count).max(0);
            let effective_potion_policy = match potion_policy {
                SolverPotionPolicy::RequireAtLeastOne if forced_use_count > 0 => {
                    SolverPotionPolicy::Smart
                }
                SolverPotionPolicy::Disabled if optional_potion_count > 0 => {
                    SolverPotionPolicy::Smart
                }
                other => other,
            };
            let max_hp_deficit = (root.initial_player_max_hp - f.player_max_hp).max(0);
            let complete_victory = RouteQualityPolicy::is_complete_victory(
                node.action_count as usize,
                f.all_enemies_dead,
                f.player_dead,
                f.projected_player_hp,
            );
            let relic_heal = ActEndingBossPolicy::ranked_post_combat_relic_heal(
                root.post_combat_relic_heal,
                complete_victory,
                f.player_hp,
                f.player_max_hp,
            );
            let strategic_hp_deficit = ActEndingBossPolicy::strategic_hp_deficit(
                f.cumulative_hp_lost,
                max_hp_deficit,
                f.recovered_hp + relic_heal,
                root.boss_hp_relief,
                f.death_save_hp_restored,
            ) - f.strategic_hp_credit;
            let health_resource_cost =
                (root.initial_player_hp - f.player_hp) + (root.initial_player_max_hp - f.player_max_hp);
            let policy_hp_deficit = strategic_hp_deficit
                + if effective_potion_policy == SolverPotionPolicy::RequireAtLeastOne {
                    PotionUsePolicy::additional_required_use_strategic_hp_cost(
                        optional_potion_strategic_cost,
                    )
                } else {
                    0
                };
            let quality = RouteQuality {
                won: complete_victory,
                survives: !f.player_dead && f.projected_player_hp > 0,
                death_save_use_count: f.death_save_use_count,
                outstanding_stolen_resource: 0,
                strategic_hp_deficit,
                growth_hp_credit: f.strategy_goal_hp_credit,
                growth_reward_count: f.strategy_goal_count,
                combat_ended_turn: if complete_victory {
                    f.combat_ended_turn
                } else {
                    None
                },
                projected_battle_hp_lost: f.cumulative_hp_lost,
                potion_strategic_cost: node.potion_strategic_cost,
                projected_battle_potion_count: node.potion_count as i32,
                enemy_hp: f.enemy_hp,
                score: f.base_score,
                theft_policy,
                forfeits_encounter_gold: false,
            };
            EvaluatedCandidate {
                node_id: nid,
                quality,
                explicit_potion_count,
                forced_uses_satisfied,
                forced_use_count,
                optional_potion_count,
                optional_potion_strategic_cost,
                optional_ambergris_count,
                effective_potion_policy,
                policy_hp_deficit,
                health_resource_cost,
                raw_growth_reward_count: f.raw_growth_reward_count,
                growth_rewards: f.growth_rewards,
                relic_eval: f.relic_eval,
                recovered_hp: f.recovered_hp,
                boundary_rank: node.boundary_rank,
                action_count: node.action_count,
                player_hp: f.player_hp,
                state_hash: node.state_hash,
            }
        })
        .collect();

    let mut potion_free_baseline_idx: Option<usize> = None;
    for (idx, c) in evaluated.iter().enumerate() {
        let is_baseline = if enforce_forced {
            c.forced_uses_satisfied && c.optional_potion_count == 0
        } else {
            c.explicit_potion_count == 0
        };
        if !is_baseline {
            continue;
        }
        let better = match potion_free_baseline_idx {
            None => true,
            Some(best_idx) => {
                let cur = &evaluated[best_idx];
                RouteQualityPolicy::compare(&c.quality, &cur.quality, RouteQualityProjection::Primary)
                    .is_lt()
                    || (RouteQualityPolicy::compare(
                        &c.quality,
                        &cur.quality,
                        RouteQualityProjection::Primary,
                    )
                    .is_eq()
                        && (c.health_resource_cost < cur.health_resource_cost
                            || (c.health_resource_cost == cur.health_resource_cost
                                && c.quality.score > cur.quality.score)))
            }
        };
        if better {
            potion_free_baseline_idx = Some(idx);
        }
    }

    let has_potion_free = potion_free_baseline_idx.is_some();
    let (potion_free_won, potion_free_hp_deficit, potion_free_player_hp, potion_free_death_saves) =
        match potion_free_baseline_idx {
            Some(idx) => {
                let b = &evaluated[idx];
                (
                    b.quality.won,
                    b.quality.strategic_hp_deficit,
                    b.player_hp,
                    b.quality.death_save_use_count,
                )
            }
            None => (false, root.initial_player_hp, 0, i32::MAX),
        };
    let any_route_won = potion_free_won || evaluated.iter().any(|c| c.quality.won);

    let mut eligible: Vec<&EvaluatedCandidate> = evaluated
        .iter()
        .filter(|c| {
            let protects_death_save = has_potion_free
                && potion_free_won
                && c.quality.won
                && potion_policy != SolverPotionPolicy::Disabled
                && c.optional_potion_count > 0
                && c.quality.death_save_use_count < potion_free_death_saves;
            let passes_soft = PotionUsePolicy::is_eligible(
                c.effective_potion_policy,
                c.optional_potion_count,
                PotionUsePolicy::smart_required_hp_saved(
                    c.optional_potion_strategic_cost,
                    root.boss_hp_relief,
                ),
                potion_free_won,
                potion_free_hp_deficit,
                any_route_won,
                c.quality.won,
                c.quality.strategic_hp_deficit,
            ) || protects_death_save;
            let passes_ambergris = protects_death_save
                || PotionUsePolicy::meets_ambergris_restriction(
                    has_potion_free,
                    c.optional_ambergris_count,
                    c.optional_potion_strategic_cost,
                    root.initial_player_max_hp,
                    potion_free_player_hp,
                    c.player_hp,
                );
            c.forced_uses_satisfied
                && c.explicit_potion_count >= minimum_potion_uses as i32
                && passes_soft
                && passes_ambergris
        })
        .collect();

    if eligible.is_empty() {
        eligible = evaluated.iter().collect();
    }

    eligible.sort_by(|a, b| {
        b.forced_uses_satisfied
            .cmp(&a.forced_uses_satisfied)
            .then_with(|| b.forced_use_count.cmp(&a.forced_use_count))
            .then_with(|| b.quality.won.cmp(&a.quality.won))
            .then_with(|| {
                let a_dead = u8::from(!a.quality.won && !a.quality.survives);
                let b_dead = u8::from(!b.quality.won && !b.quality.survives);
                a_dead.cmp(&b_dead)
            })
            .then_with(|| a.quality.death_save_use_count.cmp(&b.quality.death_save_use_count))
            .then_with(|| a.quality.strategic_hp_deficit.cmp(&b.quality.strategic_hp_deficit))
            .then_with(|| b.quality.growth_hp_credit.cmp(&a.quality.growth_hp_credit))
            .then_with(|| b.quality.growth_reward_count.cmp(&a.quality.growth_reward_count))
            .then_with(|| {
                a.quality
                    .combat_ended_turn
                    .unwrap_or(i32::MAX)
                    .cmp(&b.quality.combat_ended_turn.unwrap_or(i32::MAX))
            })
            .then_with(|| a.policy_hp_deficit.cmp(&b.policy_hp_deficit))
            .then_with(|| a.health_resource_cost.cmp(&b.health_resource_cost))
            .then_with(|| b.raw_growth_reward_count.cmp(&a.raw_growth_reward_count))
            .then_with(|| a.boundary_rank.cmp(&b.boundary_rank))
            .then_with(|| a.optional_potion_count.cmp(&b.optional_potion_count))
            .then_with(|| a.quality.enemy_hp.cmp(&b.quality.enemy_hp))
            .then_with(|| b.quality.score.total_cmp(&a.quality.score))
            .then_with(|| a.action_count.cmp(&b.action_count))
            .then_with(|| a.state_hash.cmp(&b.state_hash))
    });

    let best = eligible[0];
    Some(FinalPlanSelection {
        node_id: best.node_id,
        won: best.quality.won,
        strategic_hp_deficit: best.quality.strategic_hp_deficit,
        hp_lost: best.quality.projected_battle_hp_lost,
        potion_count: best.quality.projected_battle_potion_count as usize,
        score: best.quality.score.round() as i64,
        all_forced_uses_satisfied: best.forced_uses_satisfied,
        strategy_goal_hp_credit: best.quality.growth_hp_credit,
        strategy_goal_count: best.quality.growth_reward_count,
        growth_rewards: best.growth_rewards,
        relic_eval: best.relic_eval,
        recovered_hp: best.recovered_hp,
    })
}
