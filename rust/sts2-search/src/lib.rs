pub mod arena;
pub mod boss_tempo;
pub mod coordinator;
pub mod early_turn;
pub mod eval;
pub mod hash;
pub mod novelty;
pub mod packer;
pub mod por;
pub mod portfolio;
pub mod pruning;
pub mod tt;

use boss_tempo::BossTempoSearchOptions;
use coordinator::CombatSearchCoordinator;
use eval::{
    BossHpStrategy, PotionSlotDirective, RelicCounterRule, SolverPotionPolicy, SolverTheftPolicy,
};
use novelty::{NoveltyPortfolioBudget, NoveltySearchOptions};
use portfolio::PortfolioSchedulingMode;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::sync::atomic::AtomicBool;
use sts2_kernel::snapshot::import_live_state;
use sts2_kernel::{BattleState, ContentDispatcher, GrowthCounters, KernelError};
use sts2_types::{CardId, PotionId};

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AccelerationFlags {
    pub enable_por: bool,
    pub enable_hand_potion_dedup: bool,
    pub enable_multiset_choice_dedup: bool,
    pub enable_end_turn_prefix_reuse: bool,
    pub enable_forced_end_turn_compression: bool,
    pub enable_closure_pruning: bool,
}

impl Default for AccelerationFlags {
    fn default() -> Self {
        Self {
            enable_por: true,
            enable_hand_potion_dedup: true,
            enable_multiset_choice_dedup: true,
            enable_end_turn_prefix_reuse: true,
            enable_forced_end_turn_compression: true,
            enable_closure_pruning: true,
        }
    }
}

impl AccelerationFlags {
    pub fn none() -> Self {
        Self {
            enable_por: false,
            enable_hand_potion_dedup: false,
            enable_multiset_choice_dedup: false,
            enable_end_turn_prefix_reuse: false,
            enable_forced_end_turn_compression: false,
            enable_closure_pruning: false,
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SearchPolicy {
    pub beam_width: usize,
    pub max_nodes: usize,
    pub time_budget_ms: u64,
    pub allow_potions: bool,
    pub allow_random_cards: bool,
    #[serde(default = "default_dop")]
    pub max_degree_of_parallelism: usize,
    #[serde(default)]
    pub potion_policy: SolverPotionPolicy,
    #[serde(default)]
    pub potion_directives: Vec<PotionSlotDirective>,
    #[serde(default)]
    pub potion_hp_threshold_overrides: BTreeMap<PotionId, i32>,
    #[serde(default)]
    pub predict_potion_reward: bool,
    #[serde(default)]
    pub potion_reward_replacement_hp_credit: Option<i32>,
    #[serde(default = "default_true")]
    pub relic_strategy_enabled: bool,
    #[serde(default)]
    pub relic_counter_rules: Vec<RelicCounterRule>,
    #[serde(default)]
    pub growth_budgets: GrowthCounters,
    #[serde(default)]
    pub brightest_flame_max_hp_loss_limit: Option<i32>,
    #[serde(default)]
    pub initial_brightest_flame_max_hp_spent: i32,
    #[serde(default)]
    pub ignore_long_term_rewards: bool,
    #[serde(default)]
    pub act_transition_boss_hp_strategy: BossHpStrategy,
    #[serde(default)]
    pub final_boss_hp_strategy: BossHpStrategy,
    #[serde(default)]
    pub is_non_final_act3_boss: bool,
    #[serde(default)]
    pub prior_battle_hp_lost: i32,
    #[serde(default)]
    pub theft_policy: SolverTheftPolicy,
    #[serde(default)]
    pub use_beam_width_portfolio: bool,
    #[serde(default)]
    pub beam_width_portfolio_widths: Option<Vec<usize>>,
    #[serde(default = "default_true")]
    pub beam_width_portfolio_plain_baseline_member: bool,
    #[serde(default = "default_true")]
    pub reallocated_refinement_portfolio: bool,
    #[serde(default)]
    pub offensive_refinement_portfolio: bool,
    #[serde(default)]
    pub bounded_offensive_refinement_portfolio: bool,
    #[serde(default = "default_true")]
    pub stop_portfolio_at_hp_target: bool,
    #[serde(default)]
    pub portfolio_scheduling_mode: PortfolioSchedulingMode,
    #[serde(default)]
    pub use_novelty_search: bool,
    #[serde(default)]
    pub adaptive_novelty_refinement: bool,
    #[serde(default)]
    pub novelty_options: NoveltySearchOptions,
    #[serde(default)]
    pub novelty_budget: NoveltyPortfolioBudget,
    #[serde(default)]
    pub early_turn_exploration_depth: i32,
    #[serde(default)]
    pub early_turn_exploration_budget_ms: u64,
    #[serde(default)]
    pub use_boss_tempo_search: bool,
    #[serde(default)]
    pub boss_tempo_options: BossTempoSearchOptions,
    #[serde(default)]
    pub acceptable_battle_hp_loss: i32,
    #[serde(default = "default_true")]
    pub stop_at_acceptable_battle_hp_loss: bool,
    #[serde(default)]
    pub potion_free_beam_width: Option<usize>,
    #[serde(default)]
    pub potion_beam_width: Option<usize>,
    #[serde(default = "default_max_turns")]
    pub max_turns: i32,
    #[serde(default = "default_max_card_branches")]
    pub max_card_branches_per_node: usize,
    #[serde(default = "default_max_pile_choice_branches")]
    pub max_pile_choice_branches_per_action: usize,
    #[serde(default = "default_max_hand_choice_branches")]
    pub max_hand_choice_branches_per_action: usize,
    #[serde(default)]
    pub accelerations: AccelerationFlags,
}

fn default_true() -> bool {
    true
}

fn default_dop() -> usize {
    1
}

fn default_max_turns() -> i32 {
    16
}

fn default_max_card_branches() -> usize {
    32
}

fn default_max_pile_choice_branches() -> usize {
    18
}

fn default_max_hand_choice_branches() -> usize {
    24
}

impl SearchPolicy {
    pub fn allows_explicit_potion_use(
        &self,
        slot: usize,
        potion_id: PotionId,
        force_all_disabled: bool,
    ) -> bool {
        if force_all_disabled || !self.allow_potions {
            return false;
        }
        if let Some(d) = self
            .potion_directives
            .iter()
            .find(|d| d.slot == slot && d.potion_id == potion_id)
        {
            d.directive != eval::SolverPotionDirective::Disabled
        } else {
            self.potion_policy != SolverPotionPolicy::Disabled
        }
    }

    pub fn allows_any_potion(&self) -> bool {
        if !self.allow_potions {
            return false;
        }
        self.potion_policy != SolverPotionPolicy::Disabled
            || self
                .potion_directives
                .iter()
                .any(|d| d.directive != eval::SolverPotionDirective::Disabled)
    }

    pub fn potion_directive_for(
        &self,
        slot: usize,
        potion_id: PotionId,
    ) -> eval::SolverPotionDirective {
        if let Some(d) = self
            .potion_directives
            .iter()
            .find(|d| d.slot == slot && d.potion_id == potion_id)
        {
            d.directive
        } else if self.potion_policy == SolverPotionPolicy::Disabled {
            eval::SolverPotionDirective::Disabled
        } else {
            eval::SolverPotionDirective::Smart
        }
    }

    pub fn forced_bit_for(&self, slot: usize, potion_id: PotionId) -> Option<u32> {
        self.potion_directives
            .iter()
            .enumerate()
            .find(|(_, d)| {
                d.directive == eval::SolverPotionDirective::Force
                    && d.slot == slot
                    && d.potion_id == potion_id
            })
            .map(|(i, _)| i as u32)
    }

    pub fn all_forced_potion_mask(&self) -> u64 {
        let mut mask = 0u64;
        for (i, d) in self.potion_directives.iter().enumerate().take(64) {
            if d.directive == eval::SolverPotionDirective::Force {
                mask |= 1u64 << i;
            }
        }
        mask
    }

    pub fn forced_directive_count(&self) -> usize {
        self.potion_directives
            .iter()
            .filter(|d| d.directive == eval::SolverPotionDirective::Force)
            .count()
    }

    pub fn minimum_required_potion_uses(&self) -> usize {
        self.forced_directive_count().max(usize::from(
            self.potion_policy == SolverPotionPolicy::RequireAtLeastOne,
        ))
    }
}

impl Default for SearchPolicy {
    fn default() -> Self {
        Self {
            beam_width: 60,
            max_nodes: 120_000,
            time_budget_ms: 120_000,
            allow_potions: true,
            allow_random_cards: false,
            max_degree_of_parallelism: 1,
            potion_policy: SolverPotionPolicy::Smart,
            potion_directives: Vec::new(),
            potion_hp_threshold_overrides: BTreeMap::new(),
            predict_potion_reward: false,
            potion_reward_replacement_hp_credit: None,
            relic_strategy_enabled: true,
            relic_counter_rules: Vec::new(),
            growth_budgets: GrowthCounters::default(),
            brightest_flame_max_hp_loss_limit: None,
            initial_brightest_flame_max_hp_spent: 0,
            ignore_long_term_rewards: false,
            act_transition_boss_hp_strategy: BossHpStrategy::ProgressionFirst,
            final_boss_hp_strategy: BossHpStrategy::ProgressionFirst,
            is_non_final_act3_boss: false,
            prior_battle_hp_lost: 0,
            theft_policy: SolverTheftPolicy::Ignore,
            use_beam_width_portfolio: false,
            beam_width_portfolio_widths: None,
            beam_width_portfolio_plain_baseline_member: true,
            reallocated_refinement_portfolio: true,
            offensive_refinement_portfolio: false,
            bounded_offensive_refinement_portfolio: false,
            stop_portfolio_at_hp_target: true,
            portfolio_scheduling_mode: PortfolioSchedulingMode::Sequential,
            use_novelty_search: false,
            adaptive_novelty_refinement: false,
            novelty_options: NoveltySearchOptions::default(),
            novelty_budget: NoveltyPortfolioBudget::default(),
            early_turn_exploration_depth: 0,
            early_turn_exploration_budget_ms: 0,
            use_boss_tempo_search: false,
            boss_tempo_options: BossTempoSearchOptions::default(),
            acceptable_battle_hp_loss: 0,
            stop_at_acceptable_battle_hp_loss: true,
            potion_free_beam_width: None,
            potion_beam_width: None,
            max_turns: default_max_turns(),
            max_card_branches_per_node: default_max_card_branches(),
            max_pile_choice_branches_per_action: default_max_pile_choice_branches(),
            max_hand_choice_branches_per_action: default_max_hand_choice_branches(),
            accelerations: AccelerationFlags::default(),
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InCombatSolveRequest {
    pub policy: SearchPolicy,
    pub root_state_json: Option<sts2_kernel::snapshot::LiveStateJson>,
    pub trace_tag: Option<String>,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(tag = "kind")]
pub enum PlannedAction {
    #[serde(rename_all = "camelCase")]
    PlayCard {
        card_handle: i32,
        target_handle: Option<i32>,
        choices: Option<Vec<Vec<usize>>>,
    },
    #[serde(rename_all = "camelCase")]
    UsePotion {
        potion_slot: usize,
        potion_id: PotionId,
        target_handle: Option<i32>,
        choices: Option<Vec<Vec<usize>>>,
    },
    #[serde(rename_all = "camelCase")]
    EndTurn {
        choices: Option<Vec<Vec<usize>>>,
    },
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SearchStatistics {
    pub won: bool,
    pub strategic_hp_deficit: i32,
    pub hp_lost: i32,
    pub potion_count: usize,
    pub transition_count: u64,
    pub tt_branches_pruned: u64,
    pub incumbent_branches_pruned: u64,
    pub por_branches_pruned: u64,
    pub avg_packed_state_bytes: u64,
    pub elapsed_ms: u64,
    pub closure_healing_certified: bool,
    pub closure_growth_zero_certified: bool,
    pub termination: String,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SearchProgress {
    pub nodes_expanded: usize,
    pub transition_count: u64,
    pub current_turn: i32,
    pub best_won: bool,
    pub best_hp_lost: Option<i32>,
    pub elapsed_ms: u64,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InCombatSolveResponse {
    pub actions: Vec<PlannedAction>,
    pub score: i64,
    pub nodes_expanded: usize,
    pub terminal_cards_hint: Vec<CardId>,
    #[serde(default)]
    pub stats: SearchStatistics,
}

#[derive(Clone, Copy, Debug, Default)]
pub struct SearchEngine;

impl SearchEngine {
    pub fn solve_state(
        &self,
        root: &BattleState,
        policy: &SearchPolicy,
        content: &(dyn ContentDispatcher + Sync),
    ) -> Result<InCombatSolveResponse, KernelError> {
        let cancel = AtomicBool::new(false);
        self.solve_state_with_controls(root, policy, content, &cancel, None)
    }

    pub fn solve_state_with_controls(
        &self,
        root: &BattleState,
        policy: &SearchPolicy,
        content: &(dyn ContentDispatcher + Sync),
        cancel: &AtomicBool,
        progress_cb: Option<&dyn Fn(&SearchProgress)>,
    ) -> Result<InCombatSolveResponse, KernelError> {
        CombatSearchCoordinator::solve(root, policy, content, cancel, progress_cb)
    }

    pub fn solve(
        &self,
        request: &InCombatSolveRequest,
        content: &(dyn ContentDispatcher + Sync),
    ) -> Result<InCombatSolveResponse, KernelError> {
        let Some(ref live_json) = request.root_state_json else {
            return Err(KernelError::Unsupported {
                kind: "SearchEngine",
                id: "missing_root_state_json".to_owned(),
            });
        };
        let state = import_live_state(live_json, content)?;
        self.solve_state(&state, &request.policy, content)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn search_scaffold_types_and_round_trip() {
        let req = InCombatSolveRequest {
            policy: SearchPolicy {
                beam_width: 64,
                max_nodes: 2048,
                time_budget_ms: 250,
                allow_potions: true,
                allow_random_cards: false,
                ..Default::default()
            },
            root_state_json: None,
            trace_tag: Some("smoke".to_owned()),
        };
        let json = serde_json::to_string(&req).unwrap();
        let round_trip: InCombatSolveRequest = serde_json::from_str(&json).unwrap();
        assert_eq!(round_trip.policy.beam_width, 64);

        let action = PlannedAction::PlayCard {
            card_handle: 1,
            target_handle: Some(2),
            choices: Some(vec![vec![0]]),
        };
        let ajson = serde_json::to_string(&action).unwrap();
        assert!(ajson.contains("PlayCard"));
    }
}
