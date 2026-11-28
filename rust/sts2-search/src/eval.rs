use crate::arena::{NodeArena, NodeId};
use crate::hash::StateHash;
use crate::packer::{pack_state, unpack_state};
use crate::tt::TranspositionLabel;
use hashbrown::HashMap;
use parking_lot::RwLock;
use serde::{Deserialize, Serialize};
use smallvec::SmallVec;
use std::borrow::Cow;
use sts2_kernel::state::{
    hot_keys, BattleState, CardTempFlags, CreatureInst, ENEMY_POWER_FLAG_GOLD_THEFT,
    ENEMY_POWER_FLAG_REVIVING, FieldValue, GrowthCounters, HOOK_DIRTY_CREATURES,
};
use sts2_kernel::{CombatEngine, ContentDispatcher, CreatureHandle};
#[cfg(debug_assertions)]
use sts2_types::Dec;
use sts2_types::{
    CardId, CardKeywords, CardStaticFlags, CardType, GameMode, MonsterId, OrbId, PileType,
    PlayerRngType, PotionId, PowerId, PowerStackType, PowerStaticFlags, PowerType, RelicId,
    RelicStaticFlags, RoomType,
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
    pub const HP_FIXED_SCALE: i32 = 100;
    pub const MAX_HP_GAIN_WEIGHT_CENTI: i32 = 200;
    pub const MAX_HP_LOSS_WEIGHT_CENTI: i32 = 100;
    pub const RUN_ENDING_POTION_TIE_BREAK_CENTI: i32 = 50;
    pub const REPLACED_POTION_FLOOR_CENTI: i32 = 50;
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
            Self::PenNib => read_field(RelicId::PenNib, hot_keys::PEN_NIB_ATTACKS_PLAYED),
            Self::Nunchaku => read_field(RelicId::Nunchaku, hot_keys::NUNCHAKU_ATTACKS_PLAYED),
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
                Some(FieldValue::Int(3))
            );
            let is_improvement = matches!(
                ext.private_fields.get("MadScience.<Rider>k__BackingField"),
                Some(FieldValue::String(s)) if s == "Improvement"
            ) || matches!(
                ext.private_fields.get("MadScience.<Rider>k__BackingField"),
                Some(FieldValue::Int(9))
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
                .map(|c| 1 + (c.base_replay_count as i32).max(0))
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
                state
                    .fast_card_meta(content, c.id)
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
    let has_white_beast = state
        .player
        .relics
        .iter()
        .any(|r| !r.is_melted && r.id == RelicId::WhiteBeastStatue);
    if policy.predict_potion_reward {
        if let Some(credit) = policy.potion_reward_replacement_hp_credit {
            return credit.max(0);
        }
        let forced = has_white_beast
            || (state.static_ctx.game_mode == GameMode::Blitz
                && (room == RoomType::Boss
                    || (room == RoomType::Monster && !state.static_ctx.encounter_is_weak)));
        if forced {
            return weights::POTION_MINIMUM_HP_SAVED;
        }
        let chance =
            (0.40f32 + if room == RoomType::Elite { 0.125 } else { 0.0 }).clamp(0.0, 1.0);
        let mut rng = *state.player_rngs.get(PlayerRngType::Rewards);
        return if rng.random.next_float() < chance {
            weights::POTION_MINIMUM_HP_SAVED
        } else {
            0
        };
    }
    if has_white_beast {
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
    pub const fn persistent_hp_scale(relief: BossHpRelief) -> i32 {
        match relief {
            BossHpRelief::ActClearHeal => 35,
            BossHpRelief::RunEnding => 20,
            BossHpRelief::None => 100,
        }
    }

    #[inline]
    pub const fn round_centi_to_hp(centi: i32) -> i32 {
        if centi >= 0 {
            (centi + 50) / weights::HP_FIXED_SCALE
        } else {
            -((-centi + 50) / weights::HP_FIXED_SCALE)
        }
    }

    #[allow(clippy::too_many_arguments)]
    #[inline]
    pub fn compute_net_persistent_hp_delta_centi(
        end_player_hp: i32,
        post_combat_monotone_heal: i32,
        initial_player_hp: i32,
        end_player_max_hp: i32,
        initial_player_max_hp: i32,
        death_save_hp_restored: i32,
        growth_hp_credit: i32,
        boss_hp_relief: BossHpRelief,
        ignore_long_term_rewards: bool,
        feed_max_hp_excluded: i32,
    ) -> i32 {
        let hp_scale = Self::persistent_hp_scale(boss_hp_relief);
        let death_save_hp = death_save_hp_restored.max(0);
        let raw_hp_delta =
            (end_player_hp + post_combat_monotone_heal - death_save_hp) - initial_player_hp;
        let max_hp_gain = (end_player_max_hp - initial_player_max_hp).max(0);
        let non_feed_max_hp_gain = (max_hp_gain - feed_max_hp_excluded.max(0)).max(0);
        let max_hp_loss = (initial_player_max_hp - end_player_max_hp).max(0);

        let max_hp_gain_credit_centi =
            if boss_hp_relief != BossHpRelief::RunEnding && !ignore_long_term_rewards {
                non_feed_max_hp_gain.saturating_mul(weights::MAX_HP_GAIN_WEIGHT_CENTI)
            } else {
                0
            };
        let max_hp_loss_penalty_centi = if boss_hp_relief != BossHpRelief::RunEnding {
            max_hp_loss.saturating_mul(weights::MAX_HP_LOSS_WEIGHT_CENTI)
        } else {
            0
        };
        let death_save_penalty_centi =
            Self::death_save_premium(death_save_hp).saturating_mul(weights::HP_FIXED_SCALE);

        raw_hp_delta
            .saturating_mul(hp_scale)
            .saturating_add(max_hp_gain_credit_centi)
            .saturating_sub(max_hp_loss_penalty_centi)
            .saturating_add(growth_hp_credit.saturating_mul(weights::HP_FIXED_SCALE))
            .saturating_sub(death_save_penalty_centi)
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
    pub fn effective_optional_potion_burden(
        optional_potion_count: i32,
        optional_potion_strategic_cost: i32,
        relief: BossHpRelief,
    ) -> i32 {
        if optional_potion_count <= 0 || optional_potion_strategic_cost <= 0 {
            return 0;
        }
        match relief {
            BossHpRelief::RunEnding => 0,
            BossHpRelief::ActClearHeal => optional_potion_strategic_cost.saturating_mul(5),
            BossHpRelief::None => optional_potion_strategic_cost,
        }
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
        if id == PotionId::PotionShapedRock
            || (renewable_potion_shaped_rock && id == PotionId::PotionShapedRock)
        {
            return 0;
        }
        match id {
            PotionId::Ambergris
            | PotionId::GlowwaterPotion
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
        if let Some(&cost) = overrides.get(&id) {
            return cost.clamp(0, 100);
        }
        Self::strategic_hp_cost(id, renewable_potion_shaped_rock)
    }

    #[inline]
    pub fn strategic_hp_cost_for_root(
        id: PotionId,
        renewable_potion_shaped_rock: bool,
        initial_player_max_hp: i32,
        overrides: &std::collections::BTreeMap<PotionId, i32>,
    ) -> i32 {
        if let Some(&cost) = overrides.get(&id) {
            return cost.clamp(0, 100);
        }
        if id == PotionId::PotionShapedRock
            || (renewable_potion_shaped_rock && id == PotionId::PotionShapedRock)
        {
            return 0;
        }
        if id == PotionId::Ambergris {
            let max_hp = initial_player_max_hp.max(1);
            let ceil_40 = (max_hp * 4 + 9) / 10;
            return weights::POTION_HIGH_VALUE_HP_SAVED.max(ceil_40);
        }
        if id == PotionId::BloodPotion {
            if initial_player_max_hp <= 0 {
                return weights::POTION_MINIMUM_HP_SAVED;
            }
            return weights::POTION_MINIMUM_HP_SAVED.max((initial_player_max_hp * 20) / 100 + 1);
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
    pub fn compute_net_potion_opportunity_cost_centi(
        optional_paid_raw_hp_cost: i32,
        optional_paid_count: i32,
        alchemize_count: i32,
        end_empty_potion_slots: i32,
        root: &RootEvalBaseline,
    ) -> i32 {
        if optional_paid_count <= 0 || optional_paid_raw_hp_cost <= 0 {
            return 0;
        }
        if root.encounter_boss_hp_relief == BossHpRelief::RunEnding {
            return optional_paid_count.saturating_mul(weights::RUN_ENDING_POTION_TIE_BREAK_CENTI);
        }
        let raw_centi = optional_paid_raw_hp_cost.saturating_mul(weights::HP_FIXED_SCALE);
        let mut replacement_centi = 0i32;
        if !root.has_sozu {
            let drop_lands = root.belt_full_at_root
                && root.base_replacement_credit_centi > 0
                && (end_empty_potion_slots > 0 || alchemize_count == 0);
            let mut remaining_slots = optional_paid_count;
            if drop_lands {
                replacement_centi += root.base_replacement_credit_centi;
                remaining_slots -= 1;
            }
            let alchemize_refills = remaining_slots
                .min((alchemize_count - i32::from(root.empty_root_potion_slots)).max(0));
            if alchemize_refills > 0 {
                replacement_centi += alchemize_refills
                    * (weights::POTION_MINIMUM_HP_SAVED * weights::HP_FIXED_SCALE);
            }
        }
        let after_replacement_centi = raw_centi - replacement_centi;
        if after_replacement_centi <= weights::REPLACED_POTION_FLOOR_CENTI {
            weights::REPLACED_POTION_FLOOR_CENTI
        } else {
            after_replacement_centi
        }
    }

    #[allow(clippy::too_many_arguments)]
    #[inline]
    pub fn can_any_paid_potion_improve_on(
        won: bool,
        survives: bool,
        death_save_use_count: i32,
        end_player_hp: i32,
        end_player_max_hp: i32,
        used_potion_slot_mask: u16,
        outstanding_stolen: i32,
        forfeits_gold: bool,
        theft_preserve_resources: bool,
        root: &RootEvalBaseline,
    ) -> bool {
        let remaining_paid_mask = root.paid_optional_slot_mask & !used_potion_slot_mask;
        if root.paid_optional_potion_count == 0 || remaining_paid_mask == 0 {
            return false;
        }
        if !won || !survives || death_save_use_count > 0 {
            return true;
        }
        if theft_preserve_resources && (outstanding_stolen > 0 || forfeits_gold) {
            return true;
        }
        if root.effective_has_growth_targets || !root.active_relic_targets.is_empty() {
            return true;
        }

        let hp_scale = ActEndingBossPolicy::persistent_hp_scale(root.boss_hp_relief);

        let min_alchemize_refill = i32::from(root.has_alchemize_in_deck && !root.has_sozu);
        let min_net_cost_centi = Self::compute_net_potion_opportunity_cost_centi(
            root.min_paid_potion_cost,
            1,
            min_alchemize_refill,
            1,
            root,
        );

        let has_remaining_heal = (remaining_paid_mask & root.paid_heal_potion_slot_mask) != 0;
        let has_remaining_fruit_juice = (remaining_paid_mask & root.paid_fruit_juice_slot_mask) != 0;
        let has_remaining_entropic = (remaining_paid_mask & root.paid_entropic_brew_slot_mask) != 0;

        let end_max_hp = if end_player_max_hp > 0 {
            end_player_max_hp
        } else {
            root.initial_player_max_hp
        };
        let end_post_heal_hp = end_player_hp
            + ActEndingBossPolicy::ranked_post_combat_relic_heal(
                root.post_combat_relic_heal,
                won,
                end_player_hp,
                end_max_hp,
            );

        let max_raw_hp_gain =
            if has_remaining_heal || has_remaining_entropic || root.has_any_heal_or_unknown_generation {
                (root.initial_player_max_hp - end_post_heal_hp).max(0)
            } else {
                let best_unhealed_end_hp = root
                    .initial_player_max_hp
                    .min(root.initial_player_hp + root.post_combat_relic_heal.unconditional_heal);
                (best_unhealed_end_hp - end_post_heal_hp).max(0)
            };

        let mut max_possible_gain_centi = (max_raw_hp_gain as i64) * (hp_scale as i64);

        let max_hp_loss = (root.initial_player_max_hp - end_max_hp).max(0);
        if max_hp_loss > 0 && root.boss_hp_relief != BossHpRelief::RunEnding {
            max_possible_gain_centi +=
                (max_hp_loss as i64) * (weights::MAX_HP_LOSS_WEIGHT_CENTI as i64);
        }

        if has_remaining_fruit_juice || has_remaining_entropic {
            let max_hp_gain_bonus =
                if root.boss_hp_relief != BossHpRelief::RunEnding && !root.ignore_long_term_rewards {
                    5 * weights::MAX_HP_GAIN_WEIGHT_CENTI
                } else {
                    0
                };
            max_possible_gain_centi += 5 * (hp_scale as i64) + (max_hp_gain_bonus as i64);
        }

        max_possible_gain_centi >= (min_net_cost_centi as i64)
    }

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
    pub is_event_defeat: bool,
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
        let cmp = candidate.is_event_defeat.cmp(&current.is_event_defeat);
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

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct RareNodeEval {
    pub growth_rewards: GrowthCounters,
    pub relic_eval: RelicCounterEvaluation,
}

impl RareNodeEval {
    #[inline]
    pub fn is_empty(&self) -> bool {
        self.growth_rewards.is_zero() && self.relic_eval.target_mask == 0
    }
}

pub const FAST_FLAG_PLAYER_DEAD: u8 = 1 << 0;
pub const FAST_FLAG_ALL_ENEMIES_DEAD: u8 = 1 << 1;
pub const FAST_FLAG_WON: u8 = 1 << 2;
pub const FAST_FLAG_HAS_PENDING_CHOICE: u8 = 1 << 3;
pub const FAST_FLAG_FORFEITS_GOLD: u8 = 1 << 4;
pub const FAST_FLAG_EVENT_DEFEAT: u8 = 1 << 5;

/// Compact 40-byte per-node summary stored inline inside `SearchNode`.
/// Infrequent `growth_rewards` and `relic_eval` spill to `NodeArena::rare_evals`.
#[derive(Clone, Copy, Debug)]
pub struct StoredFastEval {
    pub enemy_hp: i32,
    pub player_hp: i16,
    pub player_max_hp: i16,
    pub player_block: i16,
    pub projected_player_hp: i16,
    pub cumulative_hp_lost: i16,
    pub recovered_hp: i16,
    pub death_save_hp_restored: i16,
    pub strategy_goal_hp_credit: i16,
    pub strategic_hp_credit: i16,
    pub energy: i16,
    pub stars: i16,
    pub outstanding_stolen: i16,
    pub combat_ended_turn_encoded: u16,
    pub alive_enemy_count: u8,
    pub reviving_enemy_count: u8,
    pub death_save_use_count: u8,
    pub raw_growth_reward_count: u8,
    pub strategy_goal_count: u8,
    pub alchemize_count: u8,
    pub empty_potion_slots: u8,
    pub feed_count: u8,
    pub flags: u8,
}

const _: () = assert!(std::mem::size_of::<StoredFastEval>() <= 40);

impl Default for StoredFastEval {
    #[inline]
    fn default() -> Self {
        Self {
            enemy_hp: 0,
            player_hp: 0,
            player_max_hp: 0,
            player_block: 0,
            projected_player_hp: 0,
            cumulative_hp_lost: 0,
            recovered_hp: 0,
            death_save_hp_restored: 0,
            strategy_goal_hp_credit: 0,
            strategic_hp_credit: 0,
            energy: 0,
            stars: 0,
            outstanding_stolen: 0,
            combat_ended_turn_encoded: u16::MAX,
            alive_enemy_count: 0,
            reviving_enemy_count: 0,
            death_save_use_count: 0,
            raw_growth_reward_count: 0,
            strategy_goal_count: 0,
            alchemize_count: 0,
            empty_potion_slots: 0,
            feed_count: 0,
            flags: 0,
        }
    }
}

impl From<&FastEvalSummary> for StoredFastEval {
    #[inline]
    fn from(f: &FastEvalSummary) -> Self {
        let mut flags = 0u8;
        if f.player_dead {
            flags |= FAST_FLAG_PLAYER_DEAD;
        }
        if f.all_enemies_dead {
            flags |= FAST_FLAG_ALL_ENEMIES_DEAD;
        }
        if f.won {
            flags |= FAST_FLAG_WON;
        }
        if f.has_pending_choice {
            flags |= FAST_FLAG_HAS_PENDING_CHOICE;
        }
        if f.forfeits_gold {
            flags |= FAST_FLAG_FORFEITS_GOLD;
        }
        if f.is_event_defeat {
            flags |= FAST_FLAG_EVENT_DEFEAT;
        }
        Self {
            enemy_hp: f.enemy_hp,
            player_hp: f.player_hp.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
            player_max_hp: f.player_max_hp.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
            player_block: f.player_block.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
            projected_player_hp: f.projected_player_hp.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
            cumulative_hp_lost: f.cumulative_hp_lost.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
            recovered_hp: f.recovered_hp.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
            death_save_hp_restored: f.death_save_hp_restored.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
            strategy_goal_hp_credit: f.strategy_goal_hp_credit.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
            strategic_hp_credit: f.strategic_hp_credit.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
            energy: f.energy.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
            stars: f.stars.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
            outstanding_stolen: f.outstanding_stolen.clamp(i16::MIN as i32, i16::MAX as i32) as i16,
            combat_ended_turn_encoded: match f.combat_ended_turn {
                Some(t) => t.clamp(0, (u16::MAX - 1) as i32) as u16,
                None => u16::MAX,
            },
            alive_enemy_count: f.alive_enemy_count.clamp(0, u8::MAX as i32) as u8,
            reviving_enemy_count: f.reviving_enemy_count.clamp(0, u8::MAX as i32) as u8,
            death_save_use_count: f.death_save_use_count.clamp(0, u8::MAX as i32) as u8,
            raw_growth_reward_count: f.raw_growth_reward_count.clamp(0, u8::MAX as i32) as u8,
            strategy_goal_count: f.strategy_goal_count.clamp(0, u8::MAX as i32) as u8,
            alchemize_count: f.alchemize_count,
            empty_potion_slots: f.empty_potion_slots,
            feed_count: f.feed_count,
            flags,
        }
    }
}

impl StoredFastEval {
    #[inline(always)]
    pub const fn player_dead(&self) -> bool {
        (self.flags & FAST_FLAG_PLAYER_DEAD) != 0
    }

    #[inline(always)]
    pub const fn all_enemies_dead(&self) -> bool {
        (self.flags & FAST_FLAG_ALL_ENEMIES_DEAD) != 0
    }

    #[inline(always)]
    pub const fn won(&self) -> bool {
        (self.flags & FAST_FLAG_WON) != 0
    }

    #[inline(always)]
    pub const fn has_pending_choice(&self) -> bool {
        (self.flags & FAST_FLAG_HAS_PENDING_CHOICE) != 0
    }

    #[inline(always)]
    pub const fn forfeits_gold(&self) -> bool {
        (self.flags & FAST_FLAG_FORFEITS_GOLD) != 0
    }

    #[inline(always)]
    pub const fn is_event_defeat(&self) -> bool {
        (self.flags & FAST_FLAG_EVENT_DEFEAT) != 0
    }

    #[inline(always)]
    pub const fn combat_ended_turn(&self) -> Option<i32> {
        if self.combat_ended_turn_encoded == u16::MAX {
            None
        } else {
            Some(self.combat_ended_turn_encoded as i32)
        }
    }

    #[inline(always)]
    pub fn set_won(&mut self, v: bool) {
        if v {
            self.flags |= FAST_FLAG_WON | FAST_FLAG_ALL_ENEMIES_DEAD;
        } else {
            self.flags &= !FAST_FLAG_WON;
        }
    }

    #[inline(always)]
    pub fn set_player_dead(&mut self, v: bool) {
        if v {
            self.flags |= FAST_FLAG_PLAYER_DEAD;
        } else {
            self.flags &= !FAST_FLAG_PLAYER_DEAD;
        }
    }

    #[inline(always)]
    pub fn set_forfeits_gold(&mut self, v: bool) {
        if v {
            self.flags |= FAST_FLAG_FORFEITS_GOLD;
        } else {
            self.flags &= !FAST_FLAG_FORFEITS_GOLD;
        }
    }

    #[inline(always)]
    pub fn set_combat_ended_turn(&mut self, v: Option<i32>) {
        self.combat_ended_turn_encoded = match v {
            Some(t) => t.clamp(0, (u16::MAX - 1) as i32) as u16,
            None => u16::MAX,
        };
    }

    #[inline]
    pub fn transposition_label(
        &self,
        score: f64,
        potion_count: u8,
        potion_strategic_cost: i32,
        future_sold_hp: i32,
        action_count: u16,
        forced_used_mask: u64,
    ) -> TranspositionLabel {
        TranspositionLabel::new(
            forced_used_mask,
            self.death_save_use_count as i32,
            potion_count as u16,
            potion_strategic_cost,
            future_sold_hp,
            self.cumulative_hp_lost as i32,
            action_count,
            score,
        )
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
    pub reviving_enemy_count: i32,
    pub alive_enemy_mask: u64,
    pub player_dead: bool,
    pub all_enemies_dead: bool,
    pub won: bool,
    pub is_event_defeat: bool,
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
    pub alchemize_count: u8,
    pub empty_potion_slots: u8,
    pub feed_count: u8,
    pub outstanding_stolen: i32,
    pub forfeits_gold: bool,
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
        forced_used_mask: u64,
    ) -> TranspositionLabel {
        TranspositionLabel::new(
            forced_used_mask,
            self.death_save_use_count,
            potion_count,
            potion_strategic_cost,
            future_sold_hp,
            self.cumulative_hp_lost,
            action_count,
            self.base_score,
        )
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
    // SAFETY: `pack_buf` was just populated by `pack_state` in this process.
    if unsafe { unpack_state(pack_buf, &state.static_ctx, scratch) }.is_err() {
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

#[derive(Clone, Debug)]
pub struct RootEvalBaseline {
    pub initial_player_hp: i32,
    pub initial_player_max_hp: i32,
    pub initial_cumulative_hp_lost: i32,
    pub initial_enemy_count: usize,
    pub initial_live_deck_size: i32,
    pub start_turn_number: i32,
    pub initial_persistent_buff_value: i32,
    pub initial_retained_attack_value: i32,
    pub initial_enemy_strength_suppression: i32,
    pub initial_enemy_weak_turns: i32,
    pub encounter_boss_hp_relief: BossHpRelief,
    pub boss_hp_relief: BossHpRelief,
    pub effective_potion_policy: SolverPotionPolicy,
    pub theft_policy: SolverTheftPolicy,
    pub post_combat_relic_heal: PostCombatRelicHealProfile,
    pub is_act_ending_boss: bool,
    pub renewable_potion_shaped_rock: bool,
    pub has_gold_plated_cables: bool,
    pub has_paper_krane: bool,
    pub has_odd_mushroom: bool,
    pub has_sozu: bool,
    pub has_white_beast_statue: bool,
    pub has_on_potion_used_relic: bool,
    pub has_alchemize_in_deck: bool,
    pub belt_full_at_root: bool,
    pub empty_root_potion_slots: u8,
    pub potion_replacement_hp_credit: i32,
    pub base_replacement_credit_centi: i32,
    pub root_slot_costs: [i16; 16],
    pub paid_optional_slot_mask: u16,
    pub paid_heal_potion_slot_mask: u16,
    pub paid_fruit_juice_slot_mask: u16,
    pub paid_entropic_brew_slot_mask: u16,
    pub has_any_heal_or_unknown_generation: bool,
    pub active_relic_targets: Vec<RelicCounterTarget>,
    pub ignore_long_term_rewards: bool,
    pub effective_growth_budgets: GrowthCounters,
    pub growth_opportunity_targets: GrowthOpportunityTargets,
    pub effective_has_growth_targets: bool,
    pub mad_science_upgrade_capacity: i32,
    pub feed_max_hp_per_kill: i32,
    pub initial_growth_rewards: GrowthCounters,
    pub initial_brightest_flame_max_hp_spent: i32,
    pub root_state_brightest_flame_spent: i32,
    pub prior_battle_hp_lost: i32,
    pub all_forced_potion_mask: u64,
    pub forced_directive_count: usize,
    pub minimum_required_potion_uses: usize,
    pub has_eligible_usable_potion: bool,
    pub can_stop_at_hp_target: bool,
    pub paid_optional_potion_count: usize,
    pub min_paid_potion_cost: i32,
    pub has_paid_heal_or_growth_potion: bool,
    pub root_card_choice_quality: Vec<i16>,
}

impl Default for RootEvalBaseline {
    fn default() -> Self {
        Self {
            initial_player_hp: 0,
            initial_player_max_hp: 0,
            initial_cumulative_hp_lost: 0,
            initial_enemy_count: 0,
            initial_live_deck_size: 0,
            start_turn_number: 0,
            initial_persistent_buff_value: 0,
            initial_retained_attack_value: 0,
            initial_enemy_strength_suppression: 0,
            initial_enemy_weak_turns: 0,
            encounter_boss_hp_relief: BossHpRelief::None,
            boss_hp_relief: BossHpRelief::None,
            effective_potion_policy: SolverPotionPolicy::Smart,
            theft_policy: SolverTheftPolicy::Ignore,
            post_combat_relic_heal: PostCombatRelicHealProfile::default(),
            is_act_ending_boss: false,
            renewable_potion_shaped_rock: false,
            has_gold_plated_cables: false,
            has_paper_krane: false,
            has_odd_mushroom: false,
            has_sozu: false,
            has_white_beast_statue: false,
            has_on_potion_used_relic: false,
            has_alchemize_in_deck: false,
            belt_full_at_root: false,
            empty_root_potion_slots: 0,
            potion_replacement_hp_credit: 0,
            base_replacement_credit_centi: 0,
            root_slot_costs: [-1; 16],
            paid_optional_slot_mask: 0,
            paid_heal_potion_slot_mask: 0,
            paid_fruit_juice_slot_mask: 0,
            paid_entropic_brew_slot_mask: 0,
            has_any_heal_or_unknown_generation: false,
            active_relic_targets: Vec::new(),
            ignore_long_term_rewards: false,
            effective_growth_budgets: GrowthCounters::default(),
            growth_opportunity_targets: GrowthOpportunityTargets::default(),
            effective_has_growth_targets: false,
            mad_science_upgrade_capacity: 0,
            feed_max_hp_per_kill: 3,
            initial_growth_rewards: GrowthCounters::default(),
            initial_brightest_flame_max_hp_spent: 0,
            root_state_brightest_flame_spent: 0,
            prior_battle_hp_lost: 0,
            all_forced_potion_mask: 0,
            forced_directive_count: 0,
            minimum_required_potion_uses: 0,
            has_eligible_usable_potion: false,
            can_stop_at_hp_target: false,
            paid_optional_potion_count: 0,
            min_paid_potion_cost: 0,
            has_paid_heal_or_growth_potion: false,
            root_card_choice_quality: Vec::new(),
        }
    }
}

fn detect_any_heal_or_unknown_generation(
    state: &BattleState,
    content: &dyn ContentDispatcher,
) -> bool {
    for c in &state.cards {
        if c.removed_from_state || c.pile == PileType::Deck {
            continue;
        }
        if state
            .fast_card_meta(content, c.id)
            .is_ok_and(|m| m.flags.contains(CardStaticFlags::MAY_HEAL))
        {
            return true;
        }
        if matches!(
            c.id,
            CardId::Abundance
                | CardId::Alchemize
                | CardId::BlotOutTheSky
                | CardId::BundleOfJoy
                | CardId::CreativeAi
                | CardId::Discovery
                | CardId::Distraction
                | CardId::Feed
                | CardId::HelloWorld
                | CardId::InfernalBlade
                | CardId::JackOfAllTrades
                | CardId::Jackpot
                | CardId::Largesse
                | CardId::MadScience
                | CardId::ManifestAuthority
                | CardId::Metamorphosis
                | CardId::NotYet
                | CardId::Quasar
                | CardId::Splash
                | CardId::Spur
                | CardId::Stoke
                | CardId::TheHierophant
                | CardId::TheLovers
                | CardId::WhiteNoise
        ) {
            return true;
        }
    }

    for cr in &state.creatures {
        for pw in &cr.powers {
            if state
                .fast_power_meta(content, pw.id)
                .is_ok_and(|m| m.flags.contains(PowerStaticFlags::MAY_HEAL))
                || matches!(
                    pw.id,
                    PowerId::RegenPower | PowerId::ReattachPower | PowerId::IllusionPower
                )
            {
                return true;
            }
        }
    }

    for r in &state.player.relics {
        if r.is_melted {
            continue;
        }
        if matches!(r.id, RelicId::BurningBlood | RelicId::BlackBlood) {
            continue;
        }
        if content
            .relic_meta(r.id)
            .is_ok_and(|m| {
                m.flags
                    .intersects(RelicStaticFlags::MAY_HEAL | RelicStaticFlags::HAS_GROWTH)
            })
            || matches!(
                r.id,
                RelicId::AlchemicalCoffer
                    | RelicId::ArcaneScroll
                    | RelicId::Astrolabe
                    | RelicId::BathRobes
                    | RelicId::BathTowel
                    | RelicId::BigHat
                    | RelicId::BloodVial
                    | RelicId::BookOfFiveRings
                    | RelicId::BookRepairKnife
                    | RelicId::ChoicesParadox
                    | RelicId::Crossbow
                    | RelicId::DelicateFrond
                    | RelicId::DemonTongue
                    | RelicId::EternalFeather
                    | RelicId::FakeBloodVial
                    | RelicId::FakeLeesWaffle
                    | RelicId::HeftyTablet
                    | RelicId::LastingCandy
                    | RelicId::LeadPaperweight
                    | RelicId::LeesWaffle
                    | RelicId::LizardTail
                    | RelicId::MassiveScroll
                    | RelicId::MealTicket
                    | RelicId::MeatOnTheBone
                    | RelicId::Ocarina
                    | RelicId::OrangeDough
                    | RelicId::PandorasBox
                    | RelicId::Pantograph
                    | RelicId::PhialHolster
                    | RelicId::Planisphere
                    | RelicId::RegalPillow
                    | RelicId::SeaGlass
                    | RelicId::TeaOfDiscourtesy
                    | RelicId::Toolbox
                    | RelicId::VexingPuzzlebox
                    | RelicId::Wuppy
            )
        {
            return true;
        }
    }

    for slot in &state.player.potion_slots {
        let Some(pid) = *slot else { continue };
        if content.potion_meta(pid).is_ok_and(|m| m.may_heal)
            || matches!(
                pid,
                PotionId::Ambergris
                    | PotionId::AttackPotion
                    | PotionId::BloodPotion
                    | PotionId::BottledSong
                    | PotionId::ColorlessPotion
                    | PotionId::CosmicConcoction
                    | PotionId::DistilledChaos
                    | PotionId::EntropicBrew
                    | PotionId::FairyInABottle
                    | PotionId::FruitJuice
                    | PotionId::OrobicAcid
                    | PotionId::PowerPotion
                    | PotionId::RegenPotion
                    | PotionId::SkillPotion
                    | PotionId::SneckoOil
            )
        {
            return true;
        }
    }

    false
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
        let encounter_boss_hp_relief = if state.static_ctx.room_type == RoomType::Boss {
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
            encounter_boss_hp_relief,
            policy.act_transition_boss_hp_strategy,
            policy.final_boss_hp_strategy,
        );
        let effective_potion_policy = if policy.theft_policy == SolverTheftPolicy::PreserveResources
        {
            SolverPotionPolicy::Smart
        } else {
            policy.potion_policy
        };
        let mut renewable_potion_shaped_rock = false;
        let mut has_gold_plated_cables = false;
        let mut has_paper_krane = false;
        let has_odd_mushroom = false;
        let mut has_sozu = false;
        let mut has_white_beast_statue = false;
        let mut has_on_potion_used_relic = false;
        for r in &state.player.relics {
            if r.is_melted {
                continue;
            }
            match r.id {
                RelicId::PetrifiedToad => renewable_potion_shaped_rock = true,
                RelicId::GoldPlatedCables => has_gold_plated_cables = true,
                RelicId::PaperKrane => has_paper_krane = true,
                RelicId::Sozu => has_sozu = true,
                RelicId::WhiteBeastStatue => has_white_beast_statue = true,
                RelicId::ReptileTrinket | RelicId::BeltBuckle => has_on_potion_used_relic = true,
                _ => {}
            }
        }
        let has_alchemize_in_deck = state
            .cards
            .iter()
            .any(|c| !c.removed_from_state && c.id == CardId::Alchemize);
        let belt_full_at_root = !state.player.potion_slots.is_empty()
            && state.player.potion_slots.iter().all(|s| s.is_some());
        let empty_root_potion_slots = state
            .player
            .potion_slots
            .iter()
            .take(16)
            .filter(|s| s.is_none())
            .count() as u8;
        let potion_replacement_hp_credit = predict_potion_replacement_hp_credit(state, policy);
        let base_replacement_credit_centi =
            potion_replacement_hp_credit.saturating_mul(weights::HP_FIXED_SCALE);

        let mut root_slot_costs = [-1i16; 16];
        let mut paid_optional_potion_count = 0usize;
        let mut paid_optional_slot_mask = 0u16;
        let mut paid_heal_potion_slot_mask = 0u16;
        let mut paid_fruit_juice_slot_mask = 0u16;
        let mut paid_entropic_brew_slot_mask = 0u16;
        let mut min_paid_potion_cost = i32::MAX;
        let mut has_paid_heal_or_growth_potion = false;
        let mut has_eligible_usable_potion = false;
        let allow_optional_at_root =
            policy.allow_potions && effective_potion_policy != SolverPotionPolicy::Disabled;
        for (slot, maybe_pid) in state.player.potion_slots.iter().enumerate().take(16) {
            let Some(pid) = *maybe_pid else { continue };
            let cost = PotionValuationRegistry::strategic_hp_cost_for_root(
                pid,
                renewable_potion_shaped_rock,
                initial_player_max_hp,
                &policy.potion_hp_threshold_overrides,
            );
            root_slot_costs[slot] = cost.clamp(0, i16::MAX as i32) as i16;
            if pid != PotionId::FairyInABottle && policy.allows_explicit_potion_use(slot, pid, false)
            {
                has_eligible_usable_potion = true;
            }
            if pid == PotionId::FairyInABottle || !allow_optional_at_root {
                continue;
            }
            if !policy.allows_explicit_potion_use(slot, pid, false) {
                continue;
            }
            if policy.forced_bit_for(slot, pid).is_some() {
                continue;
            }
            if cost > 0 {
                let slot_bit = 1u16 << slot;
                paid_optional_potion_count += 1;
                paid_optional_slot_mask |= slot_bit;
                min_paid_potion_cost = min_paid_potion_cost.min(cost);
                let is_heal_pot = matches!(
                    pid,
                    PotionId::BloodPotion
                        | PotionId::RegenPotion
                        | PotionId::Ambergris
                        | PotionId::FruitJuice
                ) || (content.potion_meta(pid).is_ok_and(|m| m.may_heal)
                    && pid != PotionId::HeartOfIron);
                if is_heal_pot {
                    paid_heal_potion_slot_mask |= slot_bit;
                    has_paid_heal_or_growth_potion = true;
                }
                if pid == PotionId::FruitJuice {
                    paid_fruit_juice_slot_mask |= slot_bit;
                    has_paid_heal_or_growth_potion = true;
                }
                if pid == PotionId::EntropicBrew {
                    paid_entropic_brew_slot_mask |= slot_bit;
                    has_paid_heal_or_growth_potion = true;
                }
            }
        }
        if min_paid_potion_cost == i32::MAX {
            min_paid_potion_cost = 0;
        }
        if has_alchemize_in_deck && !has_sozu && allow_optional_at_root {
            has_eligible_usable_potion = true;
        }
        let has_any_heal_or_unknown_generation =
            detect_any_heal_or_unknown_generation(state, content);
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
        let feed_max_hp_per_kill = if state
            .cards
            .iter()
            .any(|c| !c.removed_from_state && c.id == CardId::Feed && c.upgrade_level > 0)
        {
            4
        } else {
            3
        };
        let root_card_choice_quality = if let Some(qt) =
            content.hook_mask_tables().map(|t| t.card_generic_choice_quality)
        {
            let n = state.static_ctx.root_combat_delta_len;
            let mut v = Vec::with_capacity(n);
            for (i, c) in state.cards.iter().take(n).enumerate() {
                if !state.is_card_meta_dirty(i)
                    && c.ext_index.is_none()
                    && c.canonical_base_energy_cost >= 0
                    && c.base_energy_cost >= 0
                {
                    let cid_idx = c.id as usize;
                    if c.has_local_keyword(CardKeywords::UNPLAYABLE) {
                        v.push(-450);
                    } else if cid_idx < qt.len() {
                        let e = unsafe { qt.get_unchecked(cid_idx) };
                        if (e.flags & 1) != 0 {
                            v.push(-400);
                        } else {
                            let base_q = match c.base_energy_cost {
                                0 => e.q_cost0,
                                1 => e.q_cost1,
                                _ => e.q_cost2p,
                            };
                            let upg = if c.upgrade_level > 0 {
                                e.upg_bonus as i16
                            } else {
                                0
                            };
                            v.push(base_q + upg);
                        }
                    } else {
                        v.push(i16::MIN);
                    }
                } else {
                    v.push(i16::MIN);
                }
            }
            v
        } else {
            Vec::new()
        };

        let mut baseline = Self {
            initial_player_hp,
            initial_player_max_hp,
            initial_cumulative_hp_lost: state
                .player_creature()
                .map_or(state.player.cumulative_hp_lost, |c| c.cumulative_hp_lost),
            initial_enemy_count: state.enemy_handles.len(),
            initial_live_deck_size: (state.player.hand.len()
                + state.player.draw.len()
                + state.player.discard.len()) as i32,
            start_turn_number: state.player.turn_number,
            initial_persistent_buff_value: 0,
            initial_retained_attack_value: 0,
            initial_enemy_strength_suppression: 0,
            initial_enemy_weak_turns: 0,
            encounter_boss_hp_relief,
            boss_hp_relief,
            effective_potion_policy,
            theft_policy: policy.theft_policy,
            post_combat_relic_heal: PostCombatRelicHealProfile::from_state(state),
            is_act_ending_boss: state.static_ctx.room_type == RoomType::Boss,
            renewable_potion_shaped_rock,
            has_gold_plated_cables,
            has_paper_krane,
            has_odd_mushroom,
            has_sozu,
            has_white_beast_statue,
            has_on_potion_used_relic,
            has_alchemize_in_deck,
            belt_full_at_root,
            empty_root_potion_slots,
            potion_replacement_hp_credit,
            base_replacement_credit_centi,
            root_slot_costs,
            paid_optional_slot_mask,
            paid_heal_potion_slot_mask,
            paid_fruit_juice_slot_mask,
            paid_entropic_brew_slot_mask,
            has_any_heal_or_unknown_generation,
            active_relic_targets,
            ignore_long_term_rewards: policy.ignore_long_term_rewards,
            effective_growth_budgets,
            growth_opportunity_targets,
            effective_has_growth_targets,
            mad_science_upgrade_capacity: mad_science_upgrade_capacity(state, content),
            feed_max_hp_per_kill,
            initial_growth_rewards: state.semantic_history.growth_rewards,
            initial_brightest_flame_max_hp_spent: policy.initial_brightest_flame_max_hp_spent,
            root_state_brightest_flame_spent: state.semantic_history.brightest_flame_max_hp_spent,
            prior_battle_hp_lost: policy.prior_battle_hp_lost,
            all_forced_potion_mask: policy.all_forced_potion_mask(),
            forced_directive_count: policy.forced_directive_count(),
            minimum_required_potion_uses: policy.minimum_required_potion_uses(),
            has_eligible_usable_potion,
            can_stop_at_hp_target,
            paid_optional_potion_count,
            min_paid_potion_cost,
            has_paid_heal_or_growth_potion,
            root_card_choice_quality,
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
    pub fn has_active_hard_potion_constraint(&self) -> bool {
        self.all_forced_potion_mask != 0
            || (self.effective_potion_policy == SolverPotionPolicy::RequireAtLeastOne
                && self.minimum_required_potion_uses > 0)
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

#[inline]
pub(crate) fn is_creature_reviving(
    state: &BattleState,
    ec: &sts2_kernel::state::CreatureInst,
) -> bool {
    if !state.is_in_progress || state.won || !ec.is_dead() || state.power_extensions.is_empty() {
        return false;
    }
    if (state.hook_cache.dirty_mask & HOOK_DIRTY_CREATURES) == 0
        && state.hook_cache.cached_tables.is_some()
        && (state.hook_cache.enemy_power_flags & ENEMY_POWER_FLAG_REVIVING) == 0
    {
        #[cfg(debug_assertions)]
        debug_assert!(
            !ec.powers.iter().any(|p| {
                let key = match p.id {
                    PowerId::ReattachPower if p.amount > 0 => hot_keys::REATTACH_IS_REVIVING,
                    PowerId::AdaptablePower => hot_keys::ADAPTABLE_IS_REVIVING,
                    _ => return false,
                };
                p.ext_index.is_some_and(|ext_idx| {
                    state
                        .power_extensions
                        .get(ext_idx as usize)
                        .and_then(|pe| pe.private_fields.get_bool_static(key))
                        .unwrap_or(false)
                })
            }),
            "enemy_power_flags missed reviving enemy power!"
        );
        return false;
    }
    for p in &ec.powers {
        let key = match p.id {
            PowerId::ReattachPower if p.amount > 0 => hot_keys::REATTACH_IS_REVIVING,
            PowerId::AdaptablePower => hot_keys::ADAPTABLE_IS_REVIVING,
            _ => continue,
        };
        if let Some(ext_idx) = p.ext_index {
            if state
                .power_extensions
                .get(ext_idx as usize)
                .and_then(|pe| pe.private_fields.get_bool_static(key))
                .unwrap_or(false)
            {
                return true;
            }
        }
    }
    false
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
    let mut reviving_enemy_count = 0i32;
    let mut alive_enemy_mask = 0u64;
    let mut outstanding_stolen = 0i32;
    let check_gold_theft = !((state.hook_cache.dirty_mask & HOOK_DIRTY_CREATURES) == 0
        && state.hook_cache.cached_tables.is_some()
        && (state.hook_cache.enemy_power_flags & ENEMY_POWER_FLAG_GOLD_THEFT) == 0);

    for (idx, &eh) in state.enemy_handles.iter().enumerate() {
        if let Some(ec) = state.creature(eh) {
            #[cfg(debug_assertions)]
            debug_assert!(state.contains_creature(eh));
            let hp = ec.current_hp.max(0);
            raw_enemy_hp += hp;
            if ec.is_alive() {
                enemy_hp += hp;
                enemy_block += ec.block.max(0);
                alive_enemy_count += 1;
                if idx < 64 {
                    alive_enemy_mask |= 1u64 << idx;
                }
                if check_gold_theft && !ec.powers.is_empty() {
                    for p in &ec.powers {
                        if p.id == PowerId::ThieveryPower {
                            if let Some(ext_idx) = p.ext_index {
                                if let Some(FieldValue::Int(gold)) = state
                                    .power_extensions
                                    .get(ext_idx as usize)
                                    .and_then(|pe| pe.private_fields.get("ThieveryPower._goldStolen"))
                                {
                                    outstanding_stolen += (*gold).max(0);
                                }
                            }
                        } else if p.id == PowerId::SwipePower && p.amount > 0 {
                            outstanding_stolen += p.amount;
                        }
                    }
                } else {
                    #[cfg(debug_assertions)]
                    debug_assert!(
                        !ec.powers.iter().any(|p| {
                            (p.id == PowerId::ThieveryPower && p.ext_index.is_some())
                                || (p.id == PowerId::SwipePower && p.amount > 0)
                        }),
                        "enemy_power_flags missed ThieveryPower/SwipePower on {:?}",
                        eh
                    );
                }
            } else if is_creature_reviving(state, ec) {
                reviving_enemy_count += 1;
            }
        }
    }
    let forfeits_gold = state.escaped_handles.iter().any(|&eh| {
        state.creature(eh).is_some_and(|ec| {
            ec.powers
                .iter()
                .any(|p| matches!(p.id, PowerId::ThieveryPower | PowerId::SwipePower))
        })
    });

    let has_pending_choice = state.pending.is_some();
    let player_dead = pc.is_none_or(|c| c.is_dead())
        || (!state.is_in_progress && !state.won && !state.battleworn_dummy_timed_out);
    let is_event_defeat = !player_dead && state.battleworn_dummy_timed_out;
    if is_event_defeat {
        for &eh in &state.escaped_handles {
            if let Some(ec) = state.creature(eh) {
                let hp = ec.current_hp.max(0);
                enemy_hp += hp;
                raw_enemy_hp += hp;
            }
        }
    }
    let all_enemies_dead = !is_event_defeat && alive_enemy_count == 0 && reviving_enemy_count == 0;
    let won = !player_dead
        && !is_event_defeat
        && !has_pending_choice
        && (!state.is_in_progress && state.won || all_enemies_dead);

    let projected_player_hp = if won {
        player_hp
    } else if player_dead {
        0
    } else {
        stand_pat_hp.unwrap_or(player_hp)
    };

    let live_deck_size =
        (state.player.hand.len() + state.player.draw.len() + state.player.discard.len()) as i32;
    let mut live_deck_clutter = 0i32;
    if live_deck_size > 0 {
        if let Some(tables) = state.hook_cache.cached_tables {
            let sc_bits = tables.card_is_status_or_curse_bits;
            let sctx = &state.static_ctx;
            const _: () = assert!(
                PileType::Draw as u8 == 1
                    && PileType::Hand as u8 == 2
                    && PileType::Discard as u8 == 3
            );
            if sctx.has_root_clutter_indices && state.cards.len() >= sctx.root_initial_cards_len {
                let root_indices = if !state.deck_dirty.0 {
                    &sctx.root_combat_clutter_indices[..]
                } else {
                    &sctx.root_all_clutter_indices[..]
                };
                for &ci in root_indices {
                    let card = unsafe { state.cards.get_unchecked(ci as usize) };
                    live_deck_clutter += i32::from(
                        !card.removed_from_state && (card.pile as u8).wrapping_sub(1) < 3,
                    );
                }
                if state.extra_clutter_hint.0 {
                    if CardId::COUNT <= sc_bits.len() * 64 {
                        for card in &state.cards[sctx.root_initial_cards_len..] {
                            let active = i32::from(
                                !card.removed_from_state && (card.pile as u8).wrapping_sub(1) < 3,
                            );
                            let idx = card.id as usize;
                            let is_sc = ((unsafe { *sc_bits.get_unchecked(idx >> 6) } >> (idx & 63))
                                & 1) as i32;
                            live_deck_clutter += active & is_sc;
                        }
                    } else {
                        for card in &state.cards[sctx.root_initial_cards_len..] {
                            if !card.removed_from_state && (card.pile as u8).wrapping_sub(1) < 3 {
                                let idx = card.id as usize;
                                let w = idx >> 6;
                                if w < sc_bits.len() && ((sc_bits[w] >> (idx & 63)) & 1) != 0 {
                                    live_deck_clutter += 1;
                                }
                            }
                        }
                    }
                }
            } else {
                let card_start = if !state.deck_dirty.0 {
                    sctx.root_deck_prefix_len.min(state.cards.len())
                } else {
                    0
                };
                if card_start == 0 && (live_deck_size as usize) * 2 < state.cards.len() {
                    for pile in [
                        &state.player.hand[..],
                        &state.player.draw[..],
                        &state.player.discard[..],
                    ] {
                        for &ch in pile {
                            if let Some(card) = state.card(ch) {
                                let idx = card.id as usize;
                                let w = idx >> 6;
                                if w < sc_bits.len() && ((sc_bits[w] >> (idx & 63)) & 1) != 0 {
                                    live_deck_clutter += 1;
                                }
                            }
                        }
                    }
                } else if CardId::COUNT <= sc_bits.len() * 64 {
                    for card in &state.cards[card_start..] {
                        let active = i32::from(
                            !card.removed_from_state && (card.pile as u8).wrapping_sub(1) < 3,
                        );
                        let idx = card.id as usize;
                        let is_sc = ((unsafe { *sc_bits.get_unchecked(idx >> 6) } >> (idx & 63))
                            & 1) as i32;
                        live_deck_clutter += active & is_sc;
                    }
                } else {
                    for card in &state.cards[card_start..] {
                        if !card.removed_from_state && (card.pile as u8).wrapping_sub(1) < 3 {
                            let idx = card.id as usize;
                            let w = idx >> 6;
                            if w < sc_bits.len() && ((sc_bits[w] >> (idx & 63)) & 1) != 0 {
                                live_deck_clutter += 1;
                            }
                        }
                    }
                }
            }
            #[cfg(debug_assertions)]
            {
                let mut expected = 0i32;
                for pile in [
                    &state.player.hand[..],
                    &state.player.draw[..],
                    &state.player.discard[..],
                ] {
                    for &ch in pile {
                        if let Some(card) = state.card(ch) {
                            if tables.card_is_status_or_curse(card.id) {
                                expected += 1;
                            }
                        }
                    }
                }
                debug_assert_eq!(live_deck_clutter, expected);
            }
        } else {
            for pile in [
                &state.player.hand[..],
                &state.player.draw[..],
                &state.player.discard[..],
            ] {
                for &ch in pile {
                    if let Some(card) = state.card(ch) {
                        if let Ok(meta) = content.card_meta(card.id) {
                            if matches!(meta.card_type, CardType::Status | CardType::Curse) {
                                live_deck_clutter += 1;
                            }
                        }
                    }
                }
            }
        }
    }

    let recovered_hp = (player_hp - root.initial_player_hp
        + (cumulative_hp_lost - root.initial_cumulative_hp_lost))
        .max(0);

    let (raw_growth_reward_count, growth_rewards, growth_hp_credit, growth_reward_count) =
        if state.semantic_history.growth_rewards.is_zero()
            && root.initial_growth_rewards.is_zero()
        {
            (0, GrowthCounters::default(), 0, 0)
        } else {
            let mut raw_growth = state.semantic_history.growth_rewards;
            let delta_ms = (raw_growth.mad_science - root.initial_growth_rewards.mad_science)
                .clamp(0, root.mad_science_upgrade_capacity);
            raw_growth.mad_science = root.initial_growth_rewards.mad_science + delta_ms;
            let raw_cnt = raw_growth.total();
            if root.ignore_long_term_rewards {
                (raw_cnt, GrowthCounters::default(), 0, 0)
            } else {
                let credit = root.effective_growth_budgets.credit(&raw_growth);
                (raw_cnt, raw_growth, credit, raw_cnt)
            }
        };

    let relic_eval = if root.active_relic_targets.is_empty() {
        RelicCounterEvaluation::default()
    } else {
        RelicCounterPolicy::evaluate(
            state,
            &root.active_relic_targets,
            won,
            root.post_combat_relic_heal,
            root.boss_hp_relief,
        )
    };
    let strategy_goal_hp_credit = growth_hp_credit.saturating_add(relic_eval.hp_credit);
    let strategic_hp_credit = strategy_goal_hp_credit.saturating_add(relic_eval.healing_hp_credit);
    let strategy_goal_count = growth_reward_count.saturating_add(relic_eval.satisfied_priority);
    let brightest_flame_max_hp_spent = root.brightest_flame_spent(state);
    let feed_count = state
        .semantic_history
        .growth_rewards
        .feed
        .clamp(0, u8::MAX as i32) as u8;

    let hp_scale = ActEndingBossPolicy::persistent_hp_scale(root.boss_hp_relief) as f64 / 100.0;
    let base_score = if player_dead || projected_player_hp <= 0 {
        weights::DEATH_PENALTY
            + state.player.turn_number as f64 * 100_000.0
            - enemy_hp as f64 * 100.0
            - (alive_enemy_count + reviving_enemy_count) as f64 * 50_000.0
    } else if is_event_defeat {
        -500_000_000_000.0
            - enemy_hp as f64 * 10_000.0
            - cumulative_hp_lost as f64 * 50_000.0
    } else {
        let raw_hp_delta = projected_player_hp - root.initial_player_hp;
        let max_hp_gain = (player_max_hp - root.initial_player_max_hp).max(0);
        let non_feed_max_hp_gain =
            (max_hp_gain - i32::from(feed_count) * root.feed_max_hp_per_kill).max(0);
        let max_hp_loss = (root.initial_player_max_hp - player_max_hp).max(0);
        let max_hp_term = if root.boss_hp_relief != BossHpRelief::RunEnding {
            (if !root.ignore_long_term_rewards {
                non_feed_max_hp_gain as f64 * 2.0 * weights::HP
            } else {
                0.0
            }) - max_hp_loss as f64 * weights::HP
        } else {
            0.0
        };
        let mut s = raw_hp_delta as f64 * hp_scale * weights::HP
            + max_hp_term
            + (growth_hp_credit + relic_eval.total_hp_credit()) as f64 * weights::HP
            + relic_eval.satisfied_priority as f64 * 0.1
            - relic_eval.distance as f64 * 0.001
            + enemy_hp as f64 * weights::ENEMY_HP;
        if won {
            s += weights::VICTORY_BONUS - state.player.turn_number as f64 * 5_000.0;
        }
        s
    };

    let empty_potion_slots = state
        .player
        .potion_slots
        .iter()
        .take(16)
        .filter(|s| s.is_none())
        .count() as u8;
    let alchemize_count = state.semantic_history.growth_rewards.alchemize.clamp(0, 255) as u8;

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
        reviving_enemy_count,
        alive_enemy_mask,
        player_dead,
        all_enemies_dead,
        won,
        is_event_defeat,
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
        alchemize_count,
        empty_potion_slots,
        feed_count,
        outstanding_stolen,
        forfeits_gold,
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
    pub setup_score: i32,
    pub expected_regen_heal: i32,
    pub board_investment: i32,
    pub enemy_debuff_value: i32,
    pub enemy_threat_power_penalty: i32,
    pub orb_score: i32,
    pub orb_capacity: i32,
    pub max_dark_evoke: i32,
    pub total_poison_and_doom: i32,
    pub doom_lethal_count: i32,
    pub effective_doom_value: i32,
    pub next_turn_stars: i32,
    pub next_turn_summon: i32,
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
    pub wuppy_hp: i32,
    pub min_sandpit_turns: i32,
    pub furnace_power: i32,
    pub retained_blade_bonus_base: i64,
    pub ready_arrows: i32,
    pub melody_cards_finished: i32,
    pub playable_attack_count: i32,
    pub incoming_damage: i32,
    pub end_turn_block: i32,
    pub effective_defense: i32,
    pub unblocked_incoming: i32,
    pub reviving_hp_penalty: i32,
    pub all_reattach_encounter: bool,
    pub highest_alive_enemy_hp: i32,
    pub preserves_excess_block: bool,
    pub next_turn_block: i32,
    pub next_turn_energy: i32,
    pub next_turn_draw: i32,
    pub playable_card_count: i32,
    pub zero_cost_playable_count: i32,
    pub hand_clutter: i32,
    pub enemy_combat_dist_hash: u64,
    pub unordered_pile_hash: u64,
    pub best_power_priority: PowerRoutePriority,
    pub beam_rank_score: f64,
    pub rollout_score: Option<f64>,
    pub choice_lookahead_score: Option<f64>,
    pub has_choice_lookahead: bool,
}

/// Compact Stage-2 fields retained in `NodeArena::full_evals` across waves.
#[derive(Clone, Copy, Debug, Default)]
pub struct StoredNodeEval {
    pub beam_rank_score: f64,
    pub rollout_score: Option<f64>,
    pub choice_lookahead_score: Option<f64>,
    pub effective_defense: i32,
    pub board_investment: i32,
    pub setup_score: i32,
    pub optionality_cards: i32,
    pub persistent_buff_value: i32,
    pub reachable_hand_value: i32,
    pub max_dark_evoke: i32,
    pub total_poison_and_doom: i32,
    pub best_power_priority: PowerRoutePriority,
    pub has_choice_lookahead: bool,
}

impl StoredNodeEval {
    #[inline]
    pub fn effective_turn_boundary_score(&self) -> f64 {
        match self.rollout_score {
            Some(rs) => 0.35 * self.beam_rank_score + 0.65 * rs,
            None => self.beam_rank_score,
        }
    }
}

impl FullEvalDetails {
    #[inline]
    pub fn to_stored(&self) -> StoredNodeEval {
        StoredNodeEval {
            beam_rank_score: self.beam_rank_score,
            rollout_score: self.rollout_score,
            choice_lookahead_score: self.choice_lookahead_score,
            effective_defense: self.effective_defense,
            board_investment: self.board_investment,
            setup_score: self.setup_score,
            optionality_cards: self.playable_card_count + self.next_turn_draw,
            persistent_buff_value: self.persistent_buff_value,
            reachable_hand_value: self.reachable_hand_value,
            max_dark_evoke: self.max_dark_evoke,
            total_poison_and_doom: self.total_poison_and_doom,
            best_power_priority: self.best_power_priority,
            has_choice_lookahead: self.has_choice_lookahead,
        }
    }

    #[inline]
    pub fn effective_turn_boundary_score(&self) -> f64 {
        match self.rollout_score {
            Some(rs) => 0.35 * self.beam_rank_score + 0.65 * rs,
            None => self.beam_rank_score,
        }
    }
}

#[inline]
fn catalog_power_setup_weight(id: PowerId) -> i32 {
    match id {
        PowerId::AccuracyPower => 18,
        PowerId::AimPower => 16,
        PowerId::ArtifactPower => 16,
        PowerId::ChildOfTheStarsPower => 22,
        PowerId::CorruptionPower => 40,
        PowerId::CreativeAiPower => 32,
        PowerId::DanseMacabrePower => 24,
        PowerId::DarkEmbracePower => 28,
        PowerId::DemesnePower => 28,
        PowerId::DexterityPower => 18,
        PowerId::DoubleDamagePower => 22,
        PowerId::EnvenomPower => 24,
        PowerId::FeelNoPainPower => 24,
        PowerId::FlameBarrierPower => 12,
        PowerId::FocusPower => 24,
        PowerId::GenesisPower => 28,
        PowerId::HelloWorldPower => 14,
        PowerId::InfiniteBladesPower => 18,
        PowerId::JuggernautPower => 20,
        PowerId::LethalityPower => 20,
        PowerId::LoopPower => 24,
        PowerId::LullabyPower => 20,
        PowerId::MachineLearningPower => 20,
        PowerId::NightmarePower => 28,
        PowerId::NoxiousFumesPower => 22,
        PowerId::PaleBlueDotPower => 24,
        PowerId::RagePower => 10,
        PowerId::RegenPower => 16,
        PowerId::RitualPower => 28,
        PowerId::RupturePower => 18,
        PowerId::ShowOffPower => 18,
        PowerId::ShroudPower => 22,
        PowerId::SleightOfFleshPower => 20,
        PowerId::SoulboundPower => 16,
        PowerId::SpectrumShiftPower => 22,
        PowerId::StormPower => 22,
        PowerId::StrengthPower => 18,
        PowerId::SwordSagePower => 24,
        PowerId::TheSealedThronePower => 30,
        PowerId::ToolsOfTheTradePower => 22,
        PowerId::TyrannyPower => 24,
        PowerId::ViciousPower => 22,
        PowerId::VigorPower => 8,
        PowerId::WellLaidPlansPower => 26,
        PowerId::YahPower => 20,
        _ => 0,
    }
}

#[inline]
fn compute_player_power_setup_weight(
    id: PowerId,
    amount: i32,
    meta: Option<&sts2_types::PowerMeta>,
) -> i32 {
    if meta.is_some_and(|m| m.power_type == PowerType::Debuff) {
        let mag = amount.abs().clamp(1, 6);
        return match id {
            PowerId::WeakPower | PowerId::VulnerablePower | PowerId::FrailPower | PowerId::MindRotPower => {
                -mag * 3
            }
            PowerId::WraithFormPower | PowerId::BiasedCognitionPower | PowerId::ChainsOfBindingPower => {
                -mag * 2
            }
            PowerId::NoBlockPower => -mag * 4,
            PowerId::NoDrawPower | PowerId::BorrowedTimePower | PowerId::TangledPower => -3,
            PowerId::SlothPower => -4,
            PowerId::ConstrictPower => -amount.abs().min(12),
            PowerId::SlowPower | PowerId::TheGambitPower => -2,
            _ => -mag.min(3),
        };
    }

    match id {
        PowerId::BlockNextTurnPower
        | PowerId::SelfFormingClayPower
        | PowerId::ToricToughnessPower => return (amount.max(0) / 4).min(14),
        PowerId::EnergyNextTurnPower => return (amount.max(0) * 3).min(12),
        PowerId::DrawCardsNextTurnPower => return (amount.max(0) * 2).min(10),
        PowerId::StarNextTurnPower | PowerId::SummonNextTurnPower => return (amount.max(0) * 2).min(8),
        _ => {}
    }

    let pos_amt = amount.clamp(0, 16);
    let explicit_weight = match id {
        PowerId::VoidFormPower => pos_amt.min(3) * 14,
        PowerId::SerpentFormPower => pos_amt.min(12) * 3,
        PowerId::SpeedsterPower => pos_amt.min(10) * 2,
        PowerId::SpiritOfAshPower => pos_amt.min(12) * 2,
        PowerId::VeilpiercerPower => pos_amt.min(4) * 5,
        PowerId::MayhemPower => pos_amt.min(3) * 10,
        PowerId::AutomationPower => pos_amt.min(3) * 6,
        PowerId::ReaperFormPower => pos_amt.min(3) * 12,
        PowerId::FurnacePower => 8 + pos_amt.min(12) * 2,
        PowerId::OrbitPower => pos_amt.min(4) * 12,
        PowerId::PagestormPower => pos_amt.min(4) * 10,
        PowerId::PlatingPower => pos_amt.min(16) * 2,
        PowerId::ThornsPower => pos_amt.min(12) * 2,
        PowerId::IntangiblePower => pos_amt.min(4) * 16,
        PowerId::EchoFormPower => pos_amt.min(3) * 16,
        PowerId::DemonFormPower => pos_amt.min(6) * 6,
        PowerId::BufferPower | PowerId::AfterimagePower | PowerId::DuplicationPower => {
            pos_amt.min(4) * 8
        }
        PowerId::BarricadePower => 14,
        PowerId::BlurPower => pos_amt.min(4) * 5,
        PowerId::CalcifyPower | PowerId::PhantomBladesPower | PowerId::ThunderPower => {
            pos_amt.min(10) * 2
        }
        PowerId::DevourLifePower
        | PowerId::HauntPower
        | PowerId::NecroMasteryPower
        | PowerId::ArsenalPower
        | PowerId::RadiancePower => pos_amt.min(6) * 4,
        PowerId::CountdownPower
        | PowerId::BlackHolePower
        | PowerId::PillarOfCreationPower
        | PowerId::CoolantPower
        | PowerId::CrimsonMantlePower
        | PowerId::InfernoPower => pos_amt.min(8) * 3,
        PowerId::MonarchsGazePower
        | PowerId::StratagemPower
        | PowerId::NostalgiaPower
        | PowerId::FeralPower
        | PowerId::IterationPower
        | PowerId::EntropyPower
        | PowerId::FreeSkillPower
        | PowerId::FreeAttackPower => pos_amt.min(4) * 5,
        PowerId::SeekingEdgePower | PowerId::MasterPlannerPower | PowerId::HellraiserPower => 10,
        PowerId::HammerTimePower => 12,
        PowerId::ParryPower => pos_amt.min(8) * 2,
        PowerId::CrueltyPower => pos_amt.min(6) * 3,
        PowerId::AccelerantPower => pos_amt.min(4) * 4,
        PowerId::FanOfKnivesPower => 8,
        PowerId::PanachePower => pos_amt.min(16),
        PowerId::SubroutinePower
        | PowerId::CalamityPower
        | PowerId::BurstPower
        | PowerId::SignalBoostPower
        | PowerId::OneTwoPunchPower
        | PowerId::FreePowerPower => pos_amt.min(4) * 6,
        PowerId::RetainHandPower => pos_amt.min(4) * 4,
        _ => 0,
    };
    if explicit_weight != 0 {
        return explicit_weight.clamp(-24, 30);
    }

    let catalog_weight = catalog_power_setup_weight(id);
    if catalog_weight != 0 {
        if meta.is_some_and(|m| m.stack_type == PowerStackType::Single) {
            return (catalog_weight / 2).clamp(-24, 28);
        }
        let clamped_amt = amount.clamp(-10, 10);
        return ((catalog_weight * clamped_amt) / 3).clamp(-24, 28);
    }

    0
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

struct IntentHitsEntry {
    monster_id: MonsterId,
    move_id: Cow<'static, str>,
    hits_i32: SmallVec<[(i32, i32); 2]>,
    #[cfg(debug_assertions)]
    hits: SmallVec<[(Dec, i32); 2]>,
}

pub(crate) struct IntentHitsCache {
    game_mode: Option<GameMode>,
    ascension: u8,
    last_pos: u8,
    fast_idx: [u8; 32],
    entries: Vec<IntentHitsEntry>,
    dyn_buf: SmallVec<[(i32, i32); 4]>,
}

impl Default for IntentHitsCache {
    #[inline]
    fn default() -> Self {
        Self::new()
    }
}

impl IntentHitsCache {
    #[inline]
    pub(crate) fn new() -> Self {
        Self {
            game_mode: None,
            ascension: 0,
            last_pos: u8::MAX,
            fast_idx: [u8::MAX; 32],
            entries: Vec::with_capacity(16),
            dyn_buf: SmallVec::new(),
        }
    }

    #[inline]
    pub(crate) fn clear(&mut self) {
        self.game_mode = None;
        self.last_pos = u8::MAX;
        self.fast_idx.fill(u8::MAX);
        self.entries.clear();
        self.dyn_buf.clear();
    }

    #[inline(always)]
    fn hint_slot(mid: MonsterId, mb: &[u8]) -> usize {
        let len = mb.len();
        let b0 = mb.first().copied().unwrap_or(0) as usize;
        let b_last = mb.last().copied().unwrap_or(0) as usize;
        ((mid as usize).wrapping_mul(31) ^ (len << 3) ^ b0 ^ b_last.wrapping_mul(7)) & 31
    }

    #[inline]
    pub(crate) fn lookup_i32<'a>(
        &'a mut self,
        state: &BattleState,
        content: &dyn ContentDispatcher,
        eh: CreatureHandle,
        ec: &CreatureInst,
    ) -> &'a [(i32, i32)] {
        let Some(m) = &ec.monster else {
            return &[];
        };
        let mid = m.monster_id;
        if matches!(
            mid,
            MonsterId::WaterfallGiant | MonsterId::TestSubject | MonsterId::TheForgotten
        ) {
            self.dyn_buf.clear();
            for (d, c) in content.monster_attack_intent_hits(state, eh).unwrap_or_default() {
                self.dyn_buf.push((d.to_i32_trunc().unwrap_or(0), c));
            }
            return self.dyn_buf.as_slice();
        }
        let gm = state.static_ctx.game_mode;
        let asc = state.static_ctx.ascension;
        if self.game_mode != Some(gm) || self.ascension != asc {
            self.game_mode = Some(gm);
            self.ascension = asc;
            self.last_pos = u8::MAX;
            self.fast_idx.fill(u8::MAX);
            self.entries.clear();
        }
        let move_cow = m.next_move_id.as_ref().unwrap_or(&m.current_state_id);
        let mb = move_cow.as_bytes();
        let lp = self.last_pos as usize;
        let mru_hit = lp < self.entries.len() && {
            let e = &self.entries[lp];
            let eb = e.move_id.as_bytes();
            e.monster_id == mid && eb.len() == mb.len() && eb.as_ptr() == mb.as_ptr()
        };
        if mru_hit {
            let entry = &self.entries[lp];
            #[cfg(debug_assertions)]
            {
                let fresh = content
                    .monster_attack_intent_hits(state, eh)
                    .unwrap_or_default();
                debug_assert_eq!(
                    entry.hits,
                    fresh,
                    "IntentHitsCache mismatch for {mid:?} move={}",
                    move_cow.as_ref()
                );
            }
            return entry.hits_i32.as_slice();
        }
        let hint_slot = Self::hint_slot(mid, mb);
        let hint_pos = self.fast_idx[hint_slot] as usize;
        let pos_opt = if hint_pos < self.entries.len() {
            let e = &self.entries[hint_pos];
            let eb = e.move_id.as_bytes();
            if e.monster_id == mid
                && eb.len() == mb.len()
                && (eb.as_ptr() == mb.as_ptr() || eb == mb)
            {
                Some(hint_pos)
            } else {
                self.entries.iter().position(|e| {
                    let eb = e.move_id.as_bytes();
                    e.monster_id == mid
                        && eb.len() == mb.len()
                        && (eb.as_ptr() == mb.as_ptr() || eb == mb)
                })
            }
        } else {
            self.entries.iter().position(|e| {
                let eb = e.move_id.as_bytes();
                e.monster_id == mid
                    && eb.len() == mb.len()
                    && (eb.as_ptr() == mb.as_ptr() || eb == mb)
            })
        };
        if let Some(pos) = pos_opt {
            self.last_pos = pos as u8;
            self.fast_idx[hint_slot] = pos as u8;
            let entry = &self.entries[pos];
            #[cfg(debug_assertions)]
            {
                let fresh = content
                    .monster_attack_intent_hits(state, eh)
                    .unwrap_or_default();
                debug_assert_eq!(
                    entry.hits,
                    fresh,
                    "IntentHitsCache mismatch for {mid:?} move={}",
                    move_cow.as_ref()
                );
            }
            return entry.hits_i32.as_slice();
        }
        let hits = content
            .monster_attack_intent_hits(state, eh)
            .unwrap_or_default();
        let mut hits_i32: SmallVec<[(i32, i32); 2]> = SmallVec::with_capacity(hits.len());
        for &(d, c) in &hits {
            hits_i32.push((d.to_i32_trunc().unwrap_or(0), c));
        }
        if self.entries.len() < 64 {
            let pos = self.entries.len();
            self.entries.push(IntentHitsEntry {
                monster_id: mid,
                move_id: move_cow.clone(),
                hits_i32,
                #[cfg(debug_assertions)]
                hits,
            });
            self.last_pos = pos as u8;
            self.fast_idx[hint_slot] = pos as u8;
            return self.entries.last().unwrap().hits_i32.as_slice();
        }
        self.dyn_buf.clear();
        self.dyn_buf.extend_from_slice(&hits_i32);
        self.dyn_buf.as_slice()
    }
}

pub fn evaluate_full_details(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    fast: &FastEvalSummary,
    root: &RootEvalBaseline,
) -> FullEvalDetails {
    evaluate_full_details_cached(state, content, fast, root, None)
}

pub(crate) fn evaluate_full_details_cached(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    fast: &FastEvalSummary,
    root: &RootEvalBaseline,
    mut intent_cache: Option<&mut IntentHitsCache>,
) -> FullEvalDetails {
    let eval_tables = state.hook_cache.cached_tables;
    let power_metas = eval_tables.map(|t| t.power_metas);
    let card_metas = eval_tables.map(|t| t.card_metas);
    let mut persistent_buff_value = 0i32;
    let mut expected_regen_heal = 0i32;
    let regen_cap = (fast.player_max_hp - fast.player_hp).max(0) + 12;
    let mut player_buff_stacks = 0i32;
    let mut enemy_debuff_stacks = 0i32;
    let mut enemy_debuff_value = 0i32;
    let mut enemy_threat_power_penalty = 0i32;
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

    let mut player_focus = 0i32;
    let mut player_vuln = 0i32;
    let mut player_intangible = 0i32;
    let mut player_accelerant = 0i32;
    let mut player_end_turn_block = 0i32;
    let mut preserves_excess_block = false;
    let mut next_turn_block = 0i32;
    let mut next_turn_energy = 0i32;
    let mut next_turn_draw = 0i32;
    let mut next_turn_stars = 0i32;
    let mut next_turn_summon = 0i32;
    let mut furnace_power = 0i32;
    let mut min_sandpit_turns = i32::MAX;

    if let Some(pc) = state.player_creature() {
        for p in &pc.powers {
            if p.amount == 0 {
                continue;
            }
            let amt = p.amount.max(0);
            let mut is_one_turn_carryover = false;
            match p.id {
                PowerId::BlockNextTurnPower
                | PowerId::SelfFormingClayPower
                | PowerId::ToricToughnessPower => {
                    is_one_turn_carryover = true;
                    next_turn_block += amt;
                    future_resource_value += amt * 2;
                }
                PowerId::EnergyNextTurnPower => {
                    is_one_turn_carryover = true;
                    next_turn_energy += amt;
                    future_resource_value += amt * 3;
                }
                PowerId::DrawCardsNextTurnPower => {
                    is_one_turn_carryover = true;
                    next_turn_draw += amt;
                    future_resource_value += amt * 2;
                }
                PowerId::StarNextTurnPower => {
                    is_one_turn_carryover = true;
                    next_turn_stars += amt;
                }
                PowerId::SummonNextTurnPower => {
                    is_one_turn_carryover = true;
                    next_turn_summon += amt;
                }
                PowerId::FurnacePower => furnace_power += p.amount,
                PowerId::VulnerablePower => player_vuln += amt,
                PowerId::IntangiblePower => player_intangible += amt,
                PowerId::AccelerantPower => player_accelerant += amt,
                PowerId::BarricadePower | PowerId::BlurPower => preserves_excess_block = true,
                PowerId::PlatingPower => player_end_turn_block += amt,
                PowerId::FocusPower => player_focus += p.amount,
                PowerId::RegenPower if p.amount > 0 => {
                    let capped_stacks = amt.min(10);
                    let tri = (capped_stacks * (capped_stacks + 1) / 2).min(25);
                    expected_regen_heal += tri.min(regen_cap);
                }
                _ => {}
            }
            let meta = match power_metas.and_then(|pm| pm.get(p.id as usize)) {
                Some(pm) => Some(pm),
                None => content.power_meta(p.id).ok(),
            };
            if !is_one_turn_carryover
                && p.amount > 0
                && meta.is_some_and(|m| m.power_type == PowerType::Buff)
            {
                player_buff_stacks += 6 + p.amount.min(10_000);
            }
            persistent_buff_value += compute_player_power_setup_weight(p.id, p.amount, meta);
        }
    }

    let has_cables = root.has_gold_plated_cables;
    let orb_capacity = state.player.orb_capacity.clamp(0, 10);
    let mut orb_score = 0i32;
    let mut frost_passive_block = 0i32;
    let mut dark_evoke_sum = 0i32;
    let mut max_dark_evoke = 0i32;
    let mut total_poison_and_doom = 0i32;
    for (o_idx, orb) in state.player.orbs.iter().enumerate() {
        let val = match orb.id {
            OrbId::LightningOrb => {
                let passive = (orb
                    .raw_passive_value
                    .and_then(|d| d.to_i32_trunc().ok())
                    .unwrap_or(3)
                    + player_focus)
                    .max(0);
                let evoke = (orb
                    .raw_evoke_value
                    .and_then(|d| d.to_i32_trunc().ok())
                    .unwrap_or(8)
                    + player_focus)
                    .max(0);
                passive * 2 + evoke
            }
            OrbId::FrostOrb => {
                let passive = (orb
                    .raw_passive_value
                    .and_then(|d| d.to_i32_trunc().ok())
                    .unwrap_or(2)
                    + player_focus)
                    .max(0);
                let evoke = (orb
                    .raw_evoke_value
                    .and_then(|d| d.to_i32_trunc().ok())
                    .unwrap_or(5)
                    + player_focus)
                    .max(0);
                let mult = if o_idx == 0 && has_cables { 2 } else { 1 };
                frost_passive_block += passive * mult;
                passive * 3 + evoke
            }
            OrbId::DarkOrb => {
                let passive = (orb
                    .raw_passive_value
                    .and_then(|d| d.to_i32_trunc().ok())
                    .unwrap_or(6)
                    + player_focus)
                    .max(0);
                let evoke = orb
                    .raw_evoke_value
                    .and_then(|d| d.to_i32_trunc().ok())
                    .unwrap_or(6)
                    .max(0);
                dark_evoke_sum += evoke;
                max_dark_evoke = max_dark_evoke.max(evoke);
                evoke + passive * 2
            }
            OrbId::PlasmaOrb => 18,
            OrbId::GlassOrb => {
                let passive = (orb
                    .raw_passive_value
                    .and_then(|d| d.to_i32_trunc().ok())
                    .unwrap_or(4)
                    + player_focus)
                    .max(0);
                let evoke = (orb
                    .raw_evoke_value
                    .and_then(|d| d.to_i32_trunc().ok())
                    .unwrap_or(8)
                    + player_focus)
                    .max(0);
                passive * 2 + evoke
            }
        };
        orb_score += val;
    }
    if orb_capacity > 0 {
        persistent_buff_value += orb_capacity * 2;
    }
    if orb_score > 0 {
        persistent_buff_value += soft_cap_positive(orb_score / 3, 24, 72);
    }

    let mut playable_card_count = 0i32;
    let mut zero_cost_playable_count = 0i32;
    let mut retained_cards = 0i32;
    let mut hand_clutter = 0i32;
    let mut retained_blade_bonus_base = 0i64;
    for &ch in &state.player.hand {
        if let Some(card) = state.card(ch) {
            let meta_opt = match card_metas.and_then(|cm| cm.get(card.id as usize)) {
                Some(m) => Some(m),
                None => content.card_meta(card.id).ok(),
            };
            if let Some(meta) = meta_opt {
                if matches!(meta.card_type, CardType::Status | CardType::Curse) {
                    hand_clutter += 1;
                    continue;
                }
                let kw = if card.keywords_added_from_outside.is_empty()
                    && card.keywords_removed_from_outside.is_empty()
                {
                    card.local_keywords
                } else {
                    card.effective_keywords_base()
                };
                let is_retained = kw.contains(CardKeywords::RETAIN)
                    || card.temp_flags.contains(CardTempFlags::TEMPORARY_RETAIN_TURN);
                if is_retained {
                    retained_cards += 1;
                }
                if card.id == CardId::SovereignBlade && is_retained && furnace_power > 0 {
                    let blade_dmg = state
                        .card_ext(card.ext_index)
                        .and_then(|e| e.private_fields.get("SovereignBlade._damage"))
                        .and_then(|fv| match fv {
                            FieldValue::Decimal(d) => d.to_i32_trunc().ok(),
                            FieldValue::Int(x) => Some(*x),
                            _ => None,
                        })
                        .unwrap_or(10)
                        .max(0);
                    if fast.enemy_hp > blade_dmg.saturating_mul(2) {
                        retained_blade_bonus_base += (blade_dmg + furnace_power).min(70) as i64;
                    }
                }
                let val = card_base_heuristic_value(card.id, card.upgrade_level, meta.card_type);
                reachable_hand_value += val;
                if meta.card_type == CardType::Attack {
                    playable_attack_count += 1;
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
                if is_retained {
                    future_resource_value += val * 2;
                }
                if !kw.contains(CardKeywords::UNPLAYABLE) && card.canonical_base_energy_cost >= -1 {
                    let is_x_e = meta.flags.contains(CardStaticFlags::IS_X_ENERGY_COST);
                    let cost = if is_x_e {
                        state.player.energy.max(0)
                    } else if card.canonical_base_energy_cost < 0 {
                        i32::MAX
                    } else if card.ext_index.is_none() {
                        (card.base_energy_cost as i32).max(0)
                    } else {
                        sts2_kernel::ordering::resolve_local_energy_cost_card(state, card, false)
                    };
                    let star_cost = if meta.flags.contains(CardStaticFlags::IS_X_STAR_COST) {
                        state.player.stars.max(0)
                    } else if card.temp_star_cost_override_this_turn.is_none()
                        && card.temp_flags.is_empty()
                    {
                        meta.canonical_star_cost.max(0)
                    } else {
                        sts2_kernel::ordering::resolve_local_star_cost_card(
                            card,
                            meta.canonical_star_cost,
                        )
                        .max(0)
                    };
                    if cost <= state.player.energy && star_cost <= state.player.stars {
                        playable_card_count += 1;
                        if cost == 0 && star_cost == 0 {
                            zero_cost_playable_count += 1;
                        }
                    }
                }
            }
        }
    }

    let mut focus_target_remaining_hp = i32::MAX;
    let mut highest_alive_enemy_hp = 0i32;
    let mut reviving_hp_penalty = 0i32;
    let mut reattach_alive_count = 0i32;
    let mut doom_lethal_count = 0i32;
    let mut effective_doom_value = 0i32;
    let mut incoming_damage = 0i32;

    for &eh in &state.enemy_handles {
        if let Some(ec) = state.creature(eh) {
            #[cfg(debug_assertions)]
            debug_assert!(state.contains_creature(eh));
            let alive = ec.is_alive();
            if ec.is_dead() {
                if is_creature_reviving(state, ec) {
                    let rev_hp = ec
                        .powers
                        .iter()
                        .find(|p| p.id == PowerId::ReattachPower && p.amount > 0)
                        .map_or_else(|| 15.max(ec.max_hp / 2), |rp| rp.amount);
                    reviving_hp_penalty += rev_hp;
                }
                continue;
            }
            if alive {
                let cur_hp = ec.current_hp.max(0);
                if cur_hp > highest_alive_enemy_hp {
                    highest_alive_enemy_hp = cur_hp;
                }
                let mut enemy_str = 0i32;
                let mut enemy_weak = 0i32;
                let mut enemy_poison = 0i32;
                let mut enemy_doom = 0i32;
                let mut has_reattach = false;
                let mut has_damage_cap = false;

                for p in &ec.powers {
                    if p.amount == 0 {
                        continue;
                    }
                    if p.amount > 0 {
                        let pm_opt = match power_metas.and_then(|pm| pm.get(p.id as usize)) {
                            Some(pm) => Some(pm),
                            None => content.power_meta(p.id).ok(),
                        };
                        if let Some(pm) = pm_opt {
                            if matches!(pm.power_type, sts2_types::PowerType::Debuff) {
                                let is_dot =
                                    matches!(p.id, PowerId::PoisonPower | PowerId::DoomPower);
                                enemy_debuff_stacks += if is_dot {
                                    p.amount.min(10_000)
                                } else {
                                    p.amount.min(4)
                                };
                            }
                        }
                    } else if p.id == PowerId::StrengthPower && p.amount < 0 {
                        enemy_debuff_stacks += (-p.amount).min(8);
                    }

                    match p.id {
                        PowerId::PoisonPower if p.amount > 0 => {
                            let amt = p.amount;
                            enemy_poison += amt;
                            delayed_damage_value += amt;
                            enemy_debuff_value += soft_cap_positive(amt, 40, 160) * 3;
                        }
                        PowerId::DoomPower if p.amount > 0 => {
                            let amt = p.amount;
                            enemy_doom += amt;
                            let d = amt.min(cur_hp.max(1));
                            delayed_damage_value += ((d as i64 * d as i64) / cur_hp.max(1) as i64) as i32;
                        }
                        PowerId::WeakPower if p.amount > 0 => {
                            enemy_weak += p.amount;
                            enemy_weak_turns += p.amount;
                            enemy_debuff_value += p.amount.min(5) * 4;
                        }
                        PowerId::VulnerablePower if p.amount > 0 => {
                            enemy_vulnerable_turns += p.amount;
                            enemy_debuff_value += p.amount.min(5) * 5;
                        }
                        PowerId::StrengthPower => {
                            enemy_str += p.amount;
                            if p.amount < 0 {
                                let sup = (-p.amount).min(16);
                                enemy_strength_suppression += sup;
                            } else if p.amount > 0 {
                                enemy_threat_power_penalty += p.amount.min(20) * 3;
                            }
                        }
                        PowerId::RitualPower if p.amount > 0 => {
                            enemy_threat_power_penalty += p.amount.min(10) * 5;
                        }
                        PowerId::SandpitPower if p.amount > 0 => {
                            min_sandpit_turns = min_sandpit_turns.min(p.amount);
                        }
                        PowerId::ReattachPower if p.amount > 0 => {
                            has_reattach = true;
                        }
                        PowerId::IntangiblePower
                        | PowerId::BufferPower
                        | PowerId::SlipperyPower
                        | PowerId::HardToKillPower
                        | PowerId::HardenedShellPower
                            if p.amount > 0 =>
                        {
                            has_damage_cap = true;
                        }
                        _ => {}
                    }
                }
                if has_reattach {
                    reattach_alive_count += 1;
                }
                total_poison_and_doom += enemy_poison + enemy_doom;
                if enemy_doom > 0 {
                    if enemy_doom >= cur_hp && !has_reattach {
                        doom_lethal_count += 1;
                        effective_doom_value += cur_hp;
                    } else {
                        effective_doom_value += enemy_doom.min(cur_hp);
                    }
                }
                let ticks = enemy_poison.min(1 + player_accelerant.max(0));
                let poison_dmg = if ticks > 0 {
                    ticks * (2 * enemy_poison - ticks + 1) / 2
                } else {
                    0
                };
                let effective_enemy_hp =
                    (cur_hp - poison_dmg.min(cur_hp) - enemy_doom).max(0);
                if effective_enemy_hp < focus_target_remaining_hp {
                    focus_target_remaining_hp = effective_enemy_hp;
                }
                let poison_lethal = cur_hp > 0 && poison_dmg >= cur_hp && !has_reattach && !has_damage_cap;
                if !poison_lethal {
                    let mut accum_hit = |base_dmg: i32, count: i32| {
                        let mut per_hit = (base_dmg + enemy_str).max(0);
                        if enemy_weak > 0 {
                            per_hit = if root.has_paper_krane {
                                (per_hit * 60) / 100
                            } else {
                                let res = (per_hit * 3) >> 2;
                                #[cfg(debug_assertions)]
                                debug_assert_eq!(res, (per_hit * 75) / 100);
                                res
                            };
                        }
                        if player_vuln > 0 {
                            per_hit = if root.has_odd_mushroom {
                                let res = (per_hit * 5) >> 2;
                                #[cfg(debug_assertions)]
                                debug_assert_eq!(res, (per_hit * 125) / 100);
                                res
                            } else {
                                let res = (per_hit * 3) >> 1;
                                #[cfg(debug_assertions)]
                                debug_assert_eq!(res, (per_hit * 150) / 100);
                                res
                            };
                        }
                        if player_intangible > 0 && per_hit > 1 {
                            per_hit = 1;
                        }
                        incoming_damage += per_hit.max(0) * count.max(0);
                    };
                    if let Some(cache) = intent_cache.as_deref_mut() {
                        for &(base_dmg, count) in cache.lookup_i32(state, content, eh, ec) {
                            accum_hit(base_dmg, count);
                        }
                    } else {
                        for (d, count) in content
                            .monster_attack_intent_hits(state, eh)
                            .unwrap_or_default()
                        {
                            accum_hit(d.to_i32_trunc().unwrap_or(0), count);
                        }
                    }
                }
            }
        }
    }
    if focus_target_remaining_hp == i32::MAX {
        focus_target_remaining_hp = 0;
    }

    let (osty_hp, osty_max_hp, wuppy_hp) = if state.pet_handles.is_empty() {
        (0, 0, 0)
    } else {
        let (o_hp, o_max) = state
            .osty_handle()
            .and_then(|h| state.creature(h))
            .filter(|c| c.is_alive())
            .map_or((0, 0), |c| (c.current_hp.max(0), c.max_hp.max(0)));
        if o_hp > 0 {
            persistent_buff_value += soft_cap_positive(o_hp / 4, 12, 48);
        }
        let w_hp: i32 = state
            .pet_handles
            .iter()
            .filter_map(|&h| state.creature(h))
            .filter(|c| c.is_alive() && c.monster_id == Some(MonsterId::Wuppy))
            .map(|c| c.current_hp.max(0))
            .sum();
        (o_hp, o_max, w_hp)
    };
    let ready_arrows = if state.player.arrows.is_empty() {
        0
    } else {
        state
            .player
            .arrows
            .iter()
            .filter(|a| a.is_available())
            .count() as i32
    };
    let melody_cards_finished = if state.player.arrows.is_empty() {
        0
    } else {
        state.player.counters.melody_cards_finished_this_turn.max(0)
    };

    let end_turn_block = fast.player_block + player_end_turn_block + frost_passive_block;
    let effective_defense = end_turn_block + osty_hp + wuppy_hp;
    let unblocked_incoming = (incoming_damage - effective_defense).max(0);
    let all_reattach_encounter = reattach_alive_count > 0
        && (reattach_alive_count + fast.reviving_enemy_count) >= fast.alive_enemy_count;

    let setup_score =
        (persistent_buff_value + enemy_debuff_value - enemy_threat_power_penalty).clamp(-200, 2000);
    let raw_thin = (root.initial_live_deck_size - fast.live_deck_size).max(0);
    let thin_deck_delta = ((raw_thin + 1) >> 1).min(6);
    let board_investment = player_buff_stacks
        + enemy_debuff_stacks
        + state.player.orbs.len() as i32
        + orb_capacity
        + (dark_evoke_sum / 6)
        + (osty_hp / 4)
        + (retained_cards * 3)
        + thin_deck_delta
        + melody_cards_finished;

    FullEvalDetails {
        persistent_buff_value,
        setup_score,
        expected_regen_heal,
        board_investment,
        enemy_debuff_value,
        enemy_threat_power_penalty,
        orb_score,
        orb_capacity,
        max_dark_evoke,
        total_poison_and_doom,
        doom_lethal_count,
        effective_doom_value,
        next_turn_stars,
        next_turn_summon,
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
        wuppy_hp,
        min_sandpit_turns,
        furnace_power,
        retained_blade_bonus_base,
        ready_arrows,
        melody_cards_finished,
        playable_attack_count,
        incoming_damage,
        end_turn_block,
        effective_defense,
        unblocked_incoming,
        reviving_hp_penalty,
        all_reattach_encounter,
        highest_alive_enemy_hp,
        preserves_excess_block,
        next_turn_block,
        next_turn_energy,
        next_turn_draw,
        playable_card_count,
        zero_cost_playable_count,
        hand_clutter,
        enemy_combat_dist_hash: 0,
        unordered_pile_hash: 0,
        best_power_priority,
        beam_rank_score: 0.0,
        rollout_score: None,
        choice_lookahead_score: None,
        has_choice_lookahead: false,
    }
}

pub fn evaluate_full(
    state: &BattleState,
    content: &dyn ContentDispatcher,
    fast: &FastEvalSummary,
    root: &RootEvalBaseline,
) -> FullEvalDetails {
    let mut details = evaluate_full_details(state, content, fast, root);
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

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub enum EvalMode {
    #[default]
    Balanced,
    SetupAndSurvival,
    Offense,
}

pub fn compute_beam_rank_score(
    fast: &FastEvalSummary,
    full: &FullEvalDetails,
    root: &RootEvalBaseline,
    base_score_only: bool,
    boss_tempo_hp_pricing: bool,
    perturbation: Option<BeamWeightPerturbation>,
) -> f64 {
    compute_beam_rank_score_with_mode(
        fast,
        full,
        root,
        base_score_only,
        boss_tempo_hp_pricing,
        perturbation,
        EvalMode::Balanced,
        0,
        0,
    )
}

#[inline]
fn soft_cap_positive(value: i32, knee: i32, second_knee: i32) -> i32 {
    if value <= knee {
        return value;
    }
    let first_band = knee + ((value - knee) >> 1);
    if first_band <= second_knee {
        return first_band;
    }
    let tail = second_knee + ((first_band - second_knee) >> 1);
    tail.min(10_000)
}

#[allow(clippy::too_many_arguments)]
pub fn compute_beam_rank_score_with_mode(
    fast: &FastEvalSummary,
    full: &FullEvalDetails,
    root: &RootEvalBaseline,
    base_score_only: bool,
    boss_tempo_hp_pricing: bool,
    perturbation: Option<BeamWeightPerturbation>,
    eval_mode: EvalMode,
    potion_strategic_cost: i32,
    action_count: u16,
) -> f64 {
    let net_potion_cost_centi = potion_strategic_cost
        .max(0)
        .saturating_mul(weights::HP_FIXED_SCALE);
    compute_beam_rank_score_with_centi(
        fast,
        full,
        root,
        base_score_only,
        boss_tempo_hp_pricing,
        perturbation,
        eval_mode,
        net_potion_cost_centi,
        action_count,
    )
}

#[allow(clippy::too_many_arguments)]
pub fn compute_beam_rank_score_with_centi(
    fast: &FastEvalSummary,
    full: &FullEvalDetails,
    root: &RootEvalBaseline,
    base_score_only: bool,
    boss_tempo_hp_pricing: bool,
    perturbation: Option<BeamWeightPerturbation>,
    eval_mode: EvalMode,
    net_potion_cost_centi: i32,
    action_count: u16,
) -> f64 {
    if base_score_only || fast.player_dead || fast.projected_player_hp <= 0 {
        return fast.base_score;
    }
    if fast.is_event_defeat {
        return -500_000_000_000.0
            - fast.death_save_use_count as f64 * 500_000_000.0
            - fast.enemy_hp as f64 * 10_000.0
            - fast.cumulative_hp_lost as f64 * 50_000.0
            - net_potion_cost_centi as f64 * 300.0;
    }
    if fast.won {
        let relic_heal = ActEndingBossPolicy::ranked_post_combat_relic_heal(
            root.post_combat_relic_heal,
            true,
            fast.player_hp,
            fast.player_max_hp,
        );
        let net_hp_delta_centi = ActEndingBossPolicy::compute_net_persistent_hp_delta_centi(
            fast.player_hp,
            relic_heal,
            root.initial_player_hp,
            fast.player_max_hp,
            root.initial_player_max_hp,
            fast.death_save_hp_restored,
            fast.strategic_hp_credit,
            root.boss_hp_relief,
            root.ignore_long_term_rewards,
            i32::from(fast.feed_count) * root.feed_max_hp_per_kill,
        );
        let v_centi = (net_hp_delta_centi as i64) - (net_potion_cost_centi as i64);
        let mut s = weights::VICTORY_BONUS
            - fast.death_save_use_count as f64 * 500_000_000.0
            + v_centi as f64 * 300.0
            + net_hp_delta_centi as f64 * 2.0
            - fast.combat_ended_turn.unwrap_or(root.start_turn_number) as f64 * 5_000.0
            - action_count as f64 * 10.0
            + fast.growth_reward_count as f64 * 5_000.0
            + fast.relic_eval.satisfied_priority as f64 * 0.1
            - fast.relic_eval.distance as f64 * 0.001;
        if root.theft_policy == SolverTheftPolicy::PreserveResources {
            s -= fast.outstanding_stolen as f64 * 1_000_000.0;
            if fast.forfeits_gold {
                s -= 2_000_000.0;
            }
        }
        return s;
    }

    let _ = (
        boss_tempo_hp_pricing,
        root.is_act_ending_boss,
        root.initial_persistent_buff_value,
        root.initial_retained_attack_value,
        root.initial_enemy_strength_suppression,
        root.initial_enemy_weak_turns,
    );

    let (setup_scale, defense_scale, offense_scale) = match eval_mode {
        EvalMode::Balanced => (1.0, 1.0, 1.0),
        EvalMode::SetupAndSurvival => (1.55, 1.20, 0.92),
        EvalMode::Offense => (0.85, 0.85, 1.40),
    };

    let ongoing_net_hp_delta_centi = ActEndingBossPolicy::compute_net_persistent_hp_delta_centi(
        fast.player_hp,
        0,
        root.initial_player_hp,
        fast.player_max_hp,
        root.initial_player_max_hp,
        fast.death_save_hp_restored,
        fast.strategic_hp_credit,
        root.boss_hp_relief,
        root.ignore_long_term_rewards,
        i32::from(fast.feed_count) * root.feed_max_hp_per_kill,
    );
    let ongoing_hp_score = (ongoing_net_hp_delta_centi as f64) * 300.0
        - (net_potion_cost_centi as f64) * 300.0
        - fast.death_save_use_count as f64 * 500_000_000.0
        + fast.relic_eval.satisfied_priority as f64 * 0.1
        - fast.relic_eval.distance as f64 * 0.001
        + fast.enemy_hp as f64 * weights::ENEMY_HP;

    let mut threat_score_delta = 0.0;
    if full.unblocked_incoming > 0 {
        let scaled_threat = match root.boss_hp_relief {
            BossHpRelief::RunEnding => {
                if full.unblocked_incoming >= fast.player_hp {
                    full.unblocked_incoming
                } else {
                    full.unblocked_incoming / 4
                }
            }
            BossHpRelief::ActClearHeal => {
                if full.unblocked_incoming >= fast.player_hp {
                    full.unblocked_incoming
                } else {
                    (full.unblocked_incoming + 2) / 5
                }
            }
            BossHpRelief::None => full.unblocked_incoming,
        };
        if full.unblocked_incoming >= fast.player_hp {
            threat_score_delta -= if fast.energy == 0 {
                4_000_000.0
            } else {
                800_000.0
            };
        }
        threat_score_delta -= scaled_threat as f64 * 24_000.0 * defense_scale;
    } else if full.preserves_excess_block && full.end_turn_block > full.incoming_damage {
        let carried = (full.end_turn_block - full.incoming_damage).min(60);
        threat_score_delta += carried as f64 * 14_000.0;
    }

    let carryover_scale = if eval_mode == EvalMode::SetupAndSurvival {
        1.25
    } else {
        1.0
    };
    let carryover_bonus = (full.next_turn_block.clamp(0, 60) as f64 * 14_000.0
        + full.next_turn_energy.clamp(0, 6) as f64 * 34_000.0
        + full.next_turn_draw.clamp(0, 6) as f64 * 18_000.0)
        * carryover_scale
        + full.next_turn_stars.clamp(0, 8) as f64 * 14_000.0
        + full.next_turn_summon.clamp(0, 20) as f64 * 6_000.0;

    let permanent_kills = (root.initial_enemy_count as i32
        - (fast.alive_enemy_count + fast.reviving_enemy_count))
        .max(0)
        + full.doom_lethal_count;
    let enemy_kill_bonus = permanent_kills as f64 * 85_000.0;
    let reviving_penalty = full.reviving_hp_penalty.max(0) as f64 * 9_200.0 * offense_scale;

    let total_setup =
        (full.persistent_buff_value + full.enemy_debuff_value - full.enemy_threat_power_penalty)
            .clamp(-200, 2000);
    let eff_setup = if total_setup >= 0 {
        soft_cap_positive(total_setup, 80, 240)
    } else {
        total_setup.max(-64)
    };
    let buff_term = eff_setup as f64 * 14_000.0;

    let orb_and_osty_bonus = full.orb_capacity as f64 * 18_000.0
        + if full.orb_score > 0 {
            soft_cap_positive(full.orb_score, 120, 360) as f64 * 2_500.0
        } else {
            0.0
        }
        + if full.osty_hp > 0 {
            soft_cap_positive(full.osty_hp, 80, 320) as f64 * 4_500.0
        } else {
            0.0
        };

    let focus_or_reattach_term = if fast.alive_enemy_count > 1 {
        if full.all_reattach_encounter {
            -(full.highest_alive_enemy_hp.max(0) as f64 * 2_500.0)
        } else {
            let avg_hp = fast.enemy_hp / fast.alive_enemy_count;
            let focus_gap = (avg_hp - full.focus_target_remaining_hp).clamp(0, 60);
            focus_gap as f64 * 1_200.0
        }
    } else {
        0.0
    };
    let sandpit_bonus = if full.min_sandpit_turns < i32::MAX {
        let mut sb = full.min_sandpit_turns.min(6) as f64 * 30_000.0;
        if full.min_sandpit_turns <= 1 && fast.enemy_hp > 60 {
            sb -= 140_000.0;
        }
        sb
    } else {
        0.0
    };
    let fool_bonus = full.ready_arrows as f64 * 6_000.0 + full.melody_cards_finished as f64 * 5_000.0;
    let retained_attack_bonus = if full.retained_blade_bonus_base > 0 {
        let per_dmg = if eval_mode == EvalMode::SetupAndSurvival {
            6_500.0
        } else {
            4_800.0
        };
        full.retained_blade_bonus_base as f64 * per_dmg
    } else {
        0.0
    };
    let hand_potential_bonus = full.playable_card_count.clamp(0, 10) as f64 * 18_000.0
        + full.zero_cost_playable_count.clamp(0, 8) as f64 * 11_000.0
        + retained_attack_bonus
        - full.hand_clutter as f64 * 4_000.0
        - fast.live_deck_clutter as f64 * 4_500.0;

    let mut score = ongoing_hp_score
        + threat_score_delta
        + carryover_bonus
        + fast.energy.clamp(0, 6) as f64 * 55_000.0
        + fast.stars.clamp(0, 8) as f64 * 18_000.0
        + buff_term * setup_scale
        + enemy_kill_bonus
        - fast.enemy_block.max(0) as f64 * 2_000.0
        - reviving_penalty
        + full.effective_doom_value.max(0) as f64 * 5_500.0
        + sandpit_bonus
        + fool_bonus
        + orb_and_osty_bonus
        + focus_or_reattach_term
        + hand_potential_bonus
        + fast.growth_reward_count as f64 * 5_000.0
        - action_count as f64 * 5.0
        + (offense_scale - 1.0) * (fast.enemy_hp as f64 * weights::ENEMY_HP);

    if root.theft_policy == SolverTheftPolicy::PreserveResources {
        score -= fast.outstanding_stolen as f64 * 1_000_000.0;
        if fast.forfeits_gold {
            score -= 2_000_000.0;
        }
    }

    if let Some(p) = perturbation {
        if (p.scale - 1.0).abs() > f64::EPSILON {
            let term = match p.term {
                BeamWeightTerm::CurrentEnergy => fast.energy.clamp(0, 6) as f64 * 55_000.0,
                BeamWeightTerm::PersistentBuffDelta => buff_term,
                BeamWeightTerm::EnemyHp => fast.enemy_hp as f64 * weights::ENEMY_HP,
            };
            score += (p.scale - 1.0) * term;
        }
    }

    score
}

#[derive(Clone, Copy, Debug)]
pub struct FinalPlanSelection {
    pub node_id: NodeId,
    pub turn: i32,
    pub won: bool,
    pub survives: bool,
    pub is_event_defeat: bool,
    pub death_save_use_count: i32,
    pub player_hp: i32,
    pub end_player_max_hp: i32,
    pub strategic_hp_deficit: i32,
    pub net_persistent_hp_delta_centi: i32,
    pub net_potion_opportunity_cost_centi: i32,
    pub hp_lost: i32,
    pub potion_count: usize,
    pub optional_potion_count: usize,
    pub optional_paid_potion_count: usize,
    pub optional_potion_strategic_cost: i32,
    pub optional_ambergris_count: i32,
    pub used_potion_slot_mask: u16,
    pub alchemize_count: u8,
    pub empty_potion_slots: u8,
    pub outstanding_stolen: i32,
    pub forfeits_gold: bool,
    pub score: i64,
    pub all_forced_uses_satisfied: bool,
    pub strategy_goal_hp_credit: i32,
    pub strategy_goal_count: i32,
    pub growth_rewards: GrowthCounters,
    pub relic_eval: RelicCounterEvaluation,
    pub recovered_hp: i32,
}

#[derive(Clone, Copy)]
pub(crate) struct EvaluatedCandidate {
    pub(crate) node_id: NodeId,
    pub(crate) quality: RouteQuality,
    pub(crate) explicit_potion_count: i32,
    pub(crate) forced_uses_satisfied: bool,
    pub(crate) forced_use_count: i32,
    pub(crate) optional_paid_potion_count: i32,
    pub(crate) optional_potion_strategic_cost: i32,
    pub(crate) optional_ambergris_count: i32,
    pub(crate) used_potion_slot_mask: u16,
    pub(crate) alchemize_count: u8,
    pub(crate) empty_potion_slots: u8,
    pub(crate) net_persistent_hp_delta_centi: i32,
    pub(crate) net_potion_opportunity_cost_centi: i32,
    pub(crate) end_player_max_hp: i32,
    pub(crate) raw_growth_reward_count: i32,
    pub(crate) rare: RareNodeEval,
    pub(crate) recovered_hp: i32,
    pub(crate) boundary_rank: u8,
    pub(crate) action_count: u16,
    pub(crate) turn: i32,
    pub(crate) player_hp: i32,
    pub(crate) state_hash: StateHash,
}

#[inline]
pub(crate) fn cmp_eval_cand(
    a: &EvaluatedCandidate,
    b: &EvaluatedCandidate,
    check_theft: bool,
) -> std::cmp::Ordering {
    let a_v = (a.net_persistent_hp_delta_centi as i64)
        - (a.net_potion_opportunity_cost_centi as i64);
    let b_v = (b.net_persistent_hp_delta_centi as i64)
        - (b.net_potion_opportunity_cost_centi as i64);
    b.forced_uses_satisfied
        .cmp(&a.forced_uses_satisfied)
        .then_with(|| b.forced_use_count.cmp(&a.forced_use_count))
        .then_with(|| b.quality.won.cmp(&a.quality.won))
        .then_with(|| b.quality.survives.cmp(&a.quality.survives))
        .then_with(|| a.quality.is_event_defeat.cmp(&b.quality.is_event_defeat))
        .then_with(|| a.quality.death_save_use_count.cmp(&b.quality.death_save_use_count))
        .then_with(|| {
            if check_theft {
                a.quality
                    .outstanding_stolen_resource
                    .cmp(&b.quality.outstanding_stolen_resource)
                    .then_with(|| {
                        a.quality
                            .forfeits_encounter_gold
                            .cmp(&b.quality.forfeits_encounter_gold)
                    })
            } else {
                std::cmp::Ordering::Equal
            }
        })
        .then_with(|| b_v.cmp(&a_v))
        .then_with(|| {
            b.net_persistent_hp_delta_centi
                .cmp(&a.net_persistent_hp_delta_centi)
        })
        .then_with(|| b.quality.growth_hp_credit.cmp(&a.quality.growth_hp_credit))
        .then_with(|| {
            b.quality
                .growth_reward_count
                .cmp(&a.quality.growth_reward_count)
        })
        .then_with(|| {
            if !a.quality.won && !a.quality.survives && !b.quality.won && !b.quality.survives {
                b.turn.cmp(&a.turn)
            } else {
                a.quality
                    .combat_ended_turn
                    .unwrap_or(i32::MAX)
                    .cmp(&b.quality.combat_ended_turn.unwrap_or(i32::MAX))
            }
        })
        .then_with(|| {
            a.quality
                .projected_battle_hp_lost
                .cmp(&b.quality.projected_battle_hp_lost)
        })
        .then_with(|| b.raw_growth_reward_count.cmp(&a.raw_growth_reward_count))
        .then_with(|| a.boundary_rank.cmp(&b.boundary_rank))
        .then_with(|| {
            a.quality
                .projected_battle_potion_count
                .cmp(&b.quality.projected_battle_potion_count)
        })
        .then_with(|| a.explicit_potion_count.cmp(&b.explicit_potion_count))
        .then_with(|| a.quality.enemy_hp.cmp(&b.quality.enemy_hp))
        .then_with(|| b.quality.score.total_cmp(&a.quality.score))
        .then_with(|| a.action_count.cmp(&b.action_count))
        .then_with(|| a.state_hash.cmp(&b.state_hash))
}

pub(crate) struct FinalPlanTracker {
    effective_policy: SolverPotionPolicy,
    enforce_forced: bool,
    require_hard: bool,
    forced_directive_count: i32,
    check_theft: bool,
    theft_policy: Option<SolverTheftPolicy>,
    minimum_potion_uses: usize,
    pub(crate) best_hard: Option<EvaluatedCandidate>,
    pub(crate) best_any: Option<EvaluatedCandidate>,
}

impl FinalPlanTracker {
    pub(crate) fn new(
        root: &RootEvalBaseline,
        potion_policy: SolverPotionPolicy,
        theft_policy: Option<SolverTheftPolicy>,
        minimum_potion_uses: usize,
    ) -> Self {
        let effective_policy = match theft_policy {
            Some(SolverTheftPolicy::PreserveResources) => SolverPotionPolicy::Smart,
            _ => potion_policy,
        };
        let require_hard = root.all_forced_potion_mask != 0
            || (effective_policy == SolverPotionPolicy::RequireAtLeastOne
                && minimum_potion_uses > 0
                && root.has_eligible_usable_potion);
        Self {
            effective_policy,
            enforce_forced: root.all_forced_potion_mask != 0,
            require_hard,
            forced_directive_count: root.forced_directive_count as i32,
            check_theft: theft_policy == Some(SolverTheftPolicy::PreserveResources),
            theft_policy,
            minimum_potion_uses,
            best_hard: None,
            best_any: None,
        }
    }

    #[inline(always)]
    pub(crate) fn satisfies_hard_from_raw(
        &self,
        forced_uses_satisfied: bool,
        explicit_potion_count: i32,
        optional_paid_potion_count: i32,
    ) -> bool {
        let satisfies_policy_hard = match self.effective_policy {
            SolverPotionPolicy::Disabled => {
                optional_paid_potion_count == 0
                    && explicit_potion_count <= self.forced_directive_count
            }
            SolverPotionPolicy::RequireAtLeastOne => {
                explicit_potion_count >= self.minimum_potion_uses as i32
            }
            SolverPotionPolicy::Smart => true,
        };
        forced_uses_satisfied
            && explicit_potion_count >= self.minimum_potion_uses as i32
            && satisfies_policy_hard
    }

    #[inline(always)]
    pub(crate) fn can_fast_reject_non_victory(
        &self,
        f: &StoredFastEval,
        action_count: u16,
        forced_used_mask: u64,
        explicit_potion_count: u8,
        optional_paid_potion_count: u8,
        root: &RootEvalBaseline,
    ) -> bool {
        let Some(ref bh) = self.best_hard else {
            return false;
        };
        let complete_victory = !f.is_event_defeat()
            && RouteQualityPolicy::is_complete_victory(
                action_count as usize,
                f.all_enemies_dead() || f.won(),
                f.player_dead(),
                f.projected_player_hp as i32,
            );
        if bh.quality.won && !complete_victory {
            return true;
        }
        let forced_uses_satisfied =
            (forced_used_mask & root.all_forced_potion_mask) == root.all_forced_potion_mask;
        let satisfies_hard = self.satisfies_hard_from_raw(
            forced_uses_satisfied,
            explicit_potion_count as i32,
            optional_paid_potion_count as i32,
        );
        !satisfies_hard
    }

    #[allow(clippy::too_many_arguments)]
    pub(crate) fn evaluate_raw(
        &self,
        node_id: NodeId,
        f: &StoredFastEval,
        rare: RareNodeEval,
        action_count: u16,
        turn: i32,
        potion_count: u8,
        explicit_potion_count_u8: u8,
        optional_paid_potion_count_u8: u8,
        optional_paid_potion_cost: i16,
        ambergris_count: u8,
        forced_used_mask: u64,
        forced_ambergris_count_u8: u8,
        used_potion_slot_mask: u16,
        boundary_rank: u8,
        beam_rank_score: f64,
        state_hash: StateHash,
        root: &RootEvalBaseline,
    ) -> (EvaluatedCandidate, bool) {
        let explicit_potion_count = explicit_potion_count_u8 as i32;
        let matched_forced = forced_used_mask & root.all_forced_potion_mask;
        let forced_uses_satisfied = matched_forced == root.all_forced_potion_mask;
        let forced_use_count = if self.enforce_forced {
            matched_forced.count_ones() as i32
        } else {
            0
        };
        let forced_ambergris_count = if self.enforce_forced {
            forced_ambergris_count_u8 as i32
        } else {
            0
        };
        let optional_paid_potion_count = optional_paid_potion_count_u8 as i32;
        let optional_ambergris_count =
            (ambergris_count as i32 - forced_ambergris_count).max(0);
        let end_player_max_hp = if f.player_max_hp > 0 {
            f.player_max_hp as i32
        } else {
            root.initial_player_max_hp.max(1)
        };
        let complete_victory = !f.is_event_defeat()
            && RouteQualityPolicy::is_complete_victory(
                action_count as usize,
                f.all_enemies_dead() || f.won(),
                f.player_dead(),
                f.projected_player_hp as i32,
            );
        let relic_heal = ActEndingBossPolicy::ranked_post_combat_relic_heal(
            root.post_combat_relic_heal,
            complete_victory,
            f.player_hp as i32,
            end_player_max_hp,
        );
        let net_persistent_hp_delta_centi = ActEndingBossPolicy::compute_net_persistent_hp_delta_centi(
            f.player_hp as i32,
            relic_heal,
            root.initial_player_hp,
            end_player_max_hp,
            root.initial_player_max_hp,
            f.death_save_hp_restored as i32,
            f.strategic_hp_credit as i32,
            root.boss_hp_relief,
            root.ignore_long_term_rewards,
            i32::from(f.feed_count) * root.feed_max_hp_per_kill,
        );
        let net_potion_opportunity_cost_centi =
            PotionUsePolicy::compute_net_potion_opportunity_cost_centi(
                optional_paid_potion_cost as i32,
                optional_paid_potion_count,
                f.alchemize_count as i32,
                f.empty_potion_slots as i32,
                root,
            );
        let strategic_hp_deficit =
            ActEndingBossPolicy::round_centi_to_hp(-net_persistent_hp_delta_centi);
        let optional_potion_strategic_cost = if net_potion_opportunity_cost_centi > 0 {
            ActEndingBossPolicy::round_centi_to_hp(net_potion_opportunity_cost_centi).max(1)
        } else {
            0
        };
        let satisfies_hard = self.satisfies_hard_from_raw(
            forced_uses_satisfied,
            explicit_potion_count,
            optional_paid_potion_count,
        );
        let quality = RouteQuality {
            won: complete_victory,
            survives: !f.player_dead() && f.projected_player_hp > 0,
            is_event_defeat: f.is_event_defeat(),
            death_save_use_count: f.death_save_use_count as i32,
            outstanding_stolen_resource: f.outstanding_stolen as i32,
            strategic_hp_deficit,
            growth_hp_credit: f.strategy_goal_hp_credit as i32,
            growth_reward_count: f.strategy_goal_count as i32,
            combat_ended_turn: if complete_victory {
                f.combat_ended_turn()
            } else {
                None
            },
            projected_battle_hp_lost: f.cumulative_hp_lost as i32,
            potion_strategic_cost: optional_potion_strategic_cost,
            projected_battle_potion_count: potion_count as i32,
            enemy_hp: f.enemy_hp,
            score: beam_rank_score,
            theft_policy: self.theft_policy,
            forfeits_encounter_gold: f.forfeits_gold(),
        };
        let cand = EvaluatedCandidate {
            node_id,
            quality,
            explicit_potion_count,
            forced_uses_satisfied,
            forced_use_count,
            optional_paid_potion_count,
            optional_potion_strategic_cost,
            optional_ambergris_count,
            used_potion_slot_mask,
            alchemize_count: f.alchemize_count,
            empty_potion_slots: f.empty_potion_slots,
            net_persistent_hp_delta_centi,
            net_potion_opportunity_cost_centi,
            end_player_max_hp,
            raw_growth_reward_count: f.raw_growth_reward_count as i32,
            rare,
            recovered_hp: f.recovered_hp as i32,
            boundary_rank,
            action_count,
            turn,
            player_hp: f.player_hp as i32,
            state_hash,
        };
        (cand, satisfies_hard)
    }

    #[inline(always)]
    pub(crate) fn would_improve(&self, cand: &EvaluatedCandidate, satisfies_hard: bool) -> bool {
        if satisfies_hard {
            self.best_hard
                .as_ref()
                .is_none_or(|cur| cmp_eval_cand(cand, cur, self.check_theft).is_lt())
        } else {
            !self.require_hard
                && self.best_hard.is_none()
                && self
                    .best_any
                    .as_ref()
                    .is_none_or(|cur| cmp_eval_cand(cand, cur, self.check_theft).is_lt())
        }
    }

    #[inline]
    pub(crate) fn record(
        &mut self,
        nid: NodeId,
        mut cand: EvaluatedCandidate,
        satisfies_hard: bool,
    ) {
        cand.node_id = nid;
        if satisfies_hard {
            self.best_hard = Some(cand);
        } else if !self.require_hard && self.best_hard.is_none() {
            self.best_any = Some(cand);
        }
    }

    #[inline]
    pub(crate) fn observe_node(&mut self, arena: &NodeArena, nid: NodeId, root: &RootEvalBaseline) {
        let node = arena.get(nid);
        let rare = arena.rare_eval(nid);
        let (cand, satisfies_hard) = self.evaluate_raw(
            nid,
            &node.fast_eval,
            rare,
            node.action_count,
            node.turn as i32,
            node.potion_count,
            node.explicit_potion_count,
            node.optional_paid_potion_count,
            node.optional_paid_potion_cost,
            node.ambergris_count,
            node.forced_used_mask,
            node.forced_ambergris_count,
            node.used_potion_slot_mask,
            node.boundary_rank,
            node.beam_rank_score_or_base(),
            node.state_hash,
            root,
        );
        if self.would_improve(&cand, satisfies_hard) {
            self.record(nid, cand, satisfies_hard);
        }
    }
}

/// Unified centi-HP route selection matching `HotRouteQuality` + `HotPotionPolicy.SatisfiesHardConstraints`:
/// zero post-hoc Smart/Ambergris vetoes; ranks strictly by `V = NetPersistentHpDeltaCenti - NetPotionCostCenti`.
pub fn select_final_plan(
    arena: &NodeArena,
    candidates: &[NodeId],
    root: &RootEvalBaseline,
    potion_policy: SolverPotionPolicy,
    theft_policy: Option<SolverTheftPolicy>,
    _potion_replacement_hp_credit: i32,
    minimum_potion_uses: usize,
) -> Option<FinalPlanSelection> {
    if candidates.is_empty() {
        return None;
    }

    let mut tracker =
        FinalPlanTracker::new(root, potion_policy, theft_policy, minimum_potion_uses);
    for &nid in candidates {
        tracker.observe_node(arena, nid, root);
    }

    let best = if tracker.require_hard {
        tracker.best_hard?
    } else {
        tracker.best_hard.or(tracker.best_any)?
    };
    Some(FinalPlanSelection {
        node_id: best.node_id,
        turn: best.turn,
        won: best.quality.won,
        survives: best.quality.survives,
        is_event_defeat: best.quality.is_event_defeat,
        death_save_use_count: best.quality.death_save_use_count,
        player_hp: best.player_hp,
        end_player_max_hp: best.end_player_max_hp,
        strategic_hp_deficit: best.quality.strategic_hp_deficit,
        net_persistent_hp_delta_centi: best.net_persistent_hp_delta_centi,
        net_potion_opportunity_cost_centi: best.net_potion_opportunity_cost_centi,
        hp_lost: best.quality.projected_battle_hp_lost,
        potion_count: best.quality.projected_battle_potion_count as usize,
        optional_potion_count: best.optional_paid_potion_count.max(0) as usize,
        optional_paid_potion_count: best.optional_paid_potion_count.max(0) as usize,
        optional_potion_strategic_cost: best.optional_potion_strategic_cost,
        optional_ambergris_count: best.optional_ambergris_count,
        used_potion_slot_mask: best.used_potion_slot_mask,
        alchemize_count: best.alchemize_count,
        empty_potion_slots: best.empty_potion_slots,
        outstanding_stolen: best.quality.outstanding_stolen_resource,
        forfeits_gold: best.quality.forfeits_encounter_gold,
        score: best.quality.score.round() as i64,
        all_forced_uses_satisfied: best.forced_uses_satisfied,
        strategy_goal_hp_credit: best.quality.growth_hp_credit,
        strategy_goal_count: best.quality.growth_reward_count,
        growth_rewards: best.rare.growth_rewards,
        relic_eval: best.rare.relic_eval,
        recovered_hp: best.recovered_hp,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::arena::SearchNode;

    #[test]
    fn final_plan_tracker_matches_select_final_plan_across_mixed_terminals() {
        let mut root = RootEvalBaseline::default();
        root.initial_player_hp = 80;
        root.initial_player_max_hp = 80;
        root.all_forced_potion_mask = 0b01;
        root.forced_directive_count = 1;
        root.minimum_required_potion_uses = 1;

        let mut arena_all = NodeArena::default();
        let mut arena_filtered = NodeArena::default();
        let mut tracker = FinalPlanTracker::new(
            &root,
            SolverPotionPolicy::Smart,
            Some(SolverTheftPolicy::PreserveResources),
            1,
        );

        let make_node = |won: bool,
                         dead: bool,
                         hp: i16,
                         hp_lost: i16,
                         forced_mask: u64,
                         explicit_potions: u8,
                         opt_paid: u8,
                         turn: i16,
                         actions: u16,
                         hash: u64| {
            let mut f = StoredFastEval::default();
            f.player_hp = hp;
            f.player_max_hp = 80;
            f.projected_player_hp = hp;
            f.cumulative_hp_lost = hp_lost;
            if won {
                f.flags |= FAST_FLAG_WON | FAST_FLAG_ALL_ENEMIES_DEAD;
                f.combat_ended_turn_encoded = (turn as u16).saturating_add(1);
            }
            if dead {
                f.flags |= FAST_FLAG_PLAYER_DEAD;
            }
            SearchNode {
                state_hash: StateHash::from_parts(hash, hash.wrapping_mul(3)),
                forced_used_mask: forced_mask,
                beam_rank_score: 1000.0 - f64::from(hp_lost),
                fast_eval: f,
                turn,
                action_count: actions,
                potion_count: explicit_potions,
                explicit_potion_count: explicit_potions,
                optional_paid_potion_count: opt_paid,
                optional_paid_potion_cost: (opt_paid as i16) * 5,
                node_flags: crate::arena::NODE_FLAG_IS_TERMINAL,
                ..Default::default()
            }
        };

        // Sequence tests: non-hard, loss, worse win, better win, equal-tie, post-win loss, post-win non-hard
        let cases = [
            make_node(false, true, 0, 80, 0, 0, 0, 2, 4, 10),
            make_node(false, true, 0, 80, 1, 1, 0, 3, 5, 11),
            make_node(true, false, 60, 20, 1, 1, 1, 4, 9, 12),
            make_node(true, false, 70, 10, 1, 1, 0, 3, 7, 13),
            make_node(true, false, 70, 10, 1, 1, 0, 3, 7, 14), // loses tie-break on hash
            make_node(true, false, 70, 10, 1, 1, 0, 3, 7, 5),  // wins tie-break on hash
            make_node(false, true, 0, 80, 1, 1, 0, 4, 8, 1),
            make_node(true, false, 79, 1, 0, 0, 0, 2, 4, 2), // violates forced constraint
        ];

        let mut all_ids = Vec::new();
        let mut filtered_ids = Vec::new();
        for n in cases {
            all_ids.push(arena_all.push_node(n.clone()));
            let reject = !n.fast_eval.won()
                && tracker.can_fast_reject_non_victory(
                    &n.fast_eval,
                    n.action_count,
                    n.forced_used_mask,
                    n.explicit_potion_count,
                    n.optional_paid_potion_count,
                    &root,
                );
            if reject {
                continue;
            }
            let (cand, hard) = tracker.evaluate_raw(
                NodeId(0),
                &n.fast_eval,
                RareNodeEval::default(),
                n.action_count,
                n.turn as i32,
                n.potion_count,
                n.explicit_potion_count,
                n.optional_paid_potion_count,
                n.optional_paid_potion_cost,
                n.ambergris_count,
                n.forced_used_mask,
                n.forced_ambergris_count,
                n.used_potion_slot_mask,
                n.boundary_rank,
                n.beam_rank_score_or_base(),
                n.state_hash,
                &root,
            );
            if tracker.would_improve(&cand, hard) {
                let nid = arena_filtered.push_node(n);
                tracker.record(nid, cand, hard);
                filtered_ids.push(nid);
            }
        }

        assert!(filtered_ids.len() < all_ids.len());
        let sel_all = select_final_plan(
            &arena_all,
            &all_ids,
            &root,
            SolverPotionPolicy::Smart,
            Some(SolverTheftPolicy::PreserveResources),
            0,
            1,
        )
        .unwrap();
        let sel_filt = select_final_plan(
            &arena_filtered,
            &filtered_ids,
            &root,
            SolverPotionPolicy::Smart,
            Some(SolverTheftPolicy::PreserveResources),
            0,
            1,
        )
        .unwrap();
        assert_eq!(arena_all.get(sel_all.node_id).state_hash, arena_filtered.get(sel_filt.node_id).state_hash);
        assert_eq!(sel_all.won, sel_filt.won);
        assert_eq!(sel_all.player_hp, sel_filt.player_hp);
        assert_eq!(sel_all.hp_lost, sel_filt.hp_lost);
        assert_eq!(sel_all.net_persistent_hp_delta_centi, sel_filt.net_persistent_hp_delta_centi);
        assert_eq!(sel_all.net_potion_opportunity_cost_centi, sel_filt.net_potion_opportunity_cost_centi);
    }

    #[test]
    fn select_final_plan_returns_none_when_hard_potion_constraint_unsatisfied() {
        let mut root = RootEvalBaseline::default();
        root.initial_player_hp = 80;
        root.initial_player_max_hp = 80;
        root.all_forced_potion_mask = 0b01;
        root.forced_directive_count = 1;
        root.minimum_required_potion_uses = 1;

        let mut arena = NodeArena::default();
        let mut f = StoredFastEval::default();
        f.player_hp = 80;
        f.player_max_hp = 80;
        f.projected_player_hp = 80;
        f.cumulative_hp_lost = 0;
        f.flags |= FAST_FLAG_WON | FAST_FLAG_ALL_ENEMIES_DEAD;
        f.combat_ended_turn_encoded = 2;

        let non_compliant_win = arena.push_node(SearchNode {
            state_hash: StateHash::from_parts(1, 3),
            forced_used_mask: 0,
            beam_rank_score: 1000.0,
            fast_eval: f,
            turn: 1,
            action_count: 2,
            potion_count: 0,
            explicit_potion_count: 0,
            node_flags: crate::arena::NODE_FLAG_IS_TERMINAL,
            ..Default::default()
        });

        let selected = select_final_plan(
            &arena,
            &[non_compliant_win],
            &root,
            SolverPotionPolicy::Smart,
            None,
            0,
            1,
        );
        assert!(
            selected.is_none(),
            "must not fall back to best_any when a forced potion constraint is unsatisfied"
        );
    }
}
