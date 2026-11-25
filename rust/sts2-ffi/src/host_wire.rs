use crate::ABI_VERSION;
use serde::{Deserialize, Serialize};
use smallvec::SmallVec;
use std::collections::{BTreeMap, BTreeSet};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Instant;
use sts2_content::Sts2Content;
use sts2_kernel::snapshot::{LiveStateJson, import_live_state};
use sts2_kernel::{
    BattleState, CardHandle, ChoiceRequest, ChoiceTiming, CombatEngine, ContentDispatcher,
    CreatureHandle, KernelErrorKind, StepOutcome,
};
use sts2_search::eval::{
    BossHpStrategy, PostCombatRelicHealProfile, PotionSlotDirective, PotionValuationRegistry,
    RelicCounterId, RelicCounterRule, SolverPotionDirective, SolverPotionPolicy, SolverTheftPolicy,
};
use sts2_search::novelty::{NoveltyPortfolioBudget, NoveltySearchOptions};
use sts2_search::portfolio::PortfolioSchedulingMode;
use sts2_search::{PlannedAction, SearchEngine, SearchPolicy, SearchProgress};
use sts2_types::{CombatSide, PileType, PotionId, RelicId, RunRngType};

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct HostPotionDirectiveDto {
    pub slot: i32,
    pub potion_id: String,
    pub directive: String,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct HostRelicCounterRuleDto {
    pub id: String,
    pub enabled: bool,
    pub minimum: i32,
    pub maximum: i32,
    pub hp_allowance: i32,
    pub priority: i32,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct HostGrowthBudgetsDto {
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
    pub show_off: i32,
    #[serde(default)]
    pub third_party: Option<BTreeMap<String, i32>>,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct HostNoveltySearchDto {
    pub width: i32,
    pub familiar_allowance: i32,
    pub max_open: i32,
    pub max_novelty_entries: i32,
    pub max_actions: i32,
    pub max_turns: i32,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct HostNoveltyBudgetDto {
    pub time_divisor: i32,
    pub maximum_milliseconds: i32,
    pub maximum_expanded_nodes: i32,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct HostPolicyDto {
    pub potion_policy: String,
    #[serde(default)]
    pub potion_directives: Vec<HostPotionDirectiveDto>,
    #[serde(default)]
    pub potion_hp_threshold_overrides: BTreeMap<String, i32>,
    #[serde(default)]
    pub predict_potion_reward: bool,
    #[serde(default = "default_true")]
    pub relic_strategy_enabled: bool,
    #[serde(default)]
    pub relic_counter_rules: Vec<HostRelicCounterRuleDto>,
    #[serde(default)]
    pub growth_budgets: HostGrowthBudgetsDto,
    #[serde(default)]
    pub brightest_flame_max_hp_loss_limit: Option<i32>,
    #[serde(default)]
    pub ignore_long_term_rewards: bool,
    #[serde(default)]
    pub act_transition_boss_hp_strategy: Option<String>,
    #[serde(default)]
    pub final_boss_hp_strategy: Option<String>,
    pub acceptable_battle_hp_loss: i32,
    pub stop_at_acceptable_battle_hp_loss: bool,
    pub use_beam_width_portfolio: bool,
    pub use_novelty_portfolio: bool,
    pub use_early_turn_exploration: bool,
    pub beam_width: i32,
    #[serde(default)]
    pub potion_free_beam_width: Option<i32>,
    #[serde(default)]
    pub potion_beam_width: Option<i32>,
    pub max_expanded_nodes: i32,
    pub max_card_branches_per_node: i32,
    pub max_pile_choice_branches_per_action: i32,
    pub max_hand_choice_branches_per_action: i32,
    pub soft_time_budget_milliseconds: i32,
    #[serde(default)]
    pub theft_policy: Option<String>,
    #[serde(default)]
    pub portfolio_options_version: Option<i32>,
    #[serde(default)]
    pub beam_width_portfolio_widths: Option<Vec<i32>>,
    #[serde(default)]
    pub beam_width_portfolio_plain_baseline_member: Option<bool>,
    #[serde(default)]
    pub reallocated_refinement_portfolio: Option<bool>,
    #[serde(default)]
    pub offensive_refinement_portfolio: Option<bool>,
    #[serde(default)]
    pub bounded_offensive_refinement_portfolio: Option<bool>,
    #[serde(default)]
    pub stop_portfolio_at_hp_target: Option<bool>,
    #[serde(default)]
    pub adaptive_novelty_refinement: Option<bool>,
    #[serde(default)]
    pub novelty_search: Option<HostNoveltySearchDto>,
    #[serde(default)]
    pub novelty_budget: Option<HostNoveltyBudgetDto>,
    #[serde(default)]
    pub early_turn_exploration_depth: Option<i32>,
    #[serde(default)]
    pub early_turn_exploration_budget_milliseconds: Option<i32>,
    #[serde(default)]
    pub portfolio_scheduling_mode: Option<String>,
}

fn default_true() -> bool {
    true
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostSolveRequest {
    pub abi_version: i32,
    pub request_id: String,
    pub live_state: LiveStateJson,
    pub character_id: String,
    pub ascension: u8,
    pub game_mode: String,
    #[serde(default)]
    pub dominion_id: Option<String>,
    #[serde(default)]
    pub run_model_ids: Vec<String>,
    pub encounter_is_weak: bool,
    pub initial_encounter_monster_count: usize,
    pub total_floor: i32,
    pub act_index: i32,
    pub map_col: i32,
    pub map_row: i32,
    pub prior_battle_hp_lost: i32,
    pub search_budget_milliseconds: i32,
    pub overall_timeout_milliseconds: i32,
    pub max_degree_of_parallelism: i32,
    pub policy: serde_json::Value,
    #[serde(default)]
    pub potion_reward_replacement_hp_credit: Option<i32>,
    #[serde(default)]
    pub is_non_final_act3_boss: Option<bool>,
    #[serde(default)]
    pub initial_brightest_flame_max_hp_spent: Option<i32>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostChoiceCardTokenDto {
    pub card_id: String,
    pub upgrade_level: i32,
    pub option_occurrence: i32,
    pub source_occurrence: i32,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostChoiceTraceDto {
    pub timing: String,
    pub source_id: String,
    pub source_pile: String,
    pub cards: Vec<HostChoiceCardTokenDto>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostActionTraceDto {
    pub turn: i32,
    pub kind: String,
    pub card_id: String,
    pub card_occurrence: i32,
    pub card_upgrade_level: i32,
    pub card_enchantment_id: String,
    pub target_index: i32,
    pub target_combat_id: Option<u32>,
    pub potion_id: String,
    pub potion_slot: i32,
    pub choices: Vec<HostChoiceTraceDto>,
    pub flashed_relic_ids: Vec<String>,
    pub killed_enemy_combat_ids: Vec<u32>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostTurnOutcomeDto {
    pub turn: i32,
    pub hp_lost: i32,
    pub hp_recovered: i32,
    pub enemy_hp_lost: i32,
    pub sold_hp: i32,
    pub max_block: i32,
    pub actual_block: i32,
    pub energy_left: i32,
    pub potion_count: i32,
    pub potion_strategic_cost: i32,
    pub combat_ended: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostTerminalSnapshotDto {
    pub player_dead: bool,
    pub all_enemies_dead: bool,
    pub player_hp: i32,
    pub player_max_hp: i32,
    pub cumulative_player_hp_lost: i32,
    pub recovered_player_hp: i32,
    pub projected_player_hp: i32,
    pub player_block: i32,
    pub enemy_hp: i32,
    pub alive_enemy_count: i32,
    pub energy: i32,
    pub stars: i32,
    pub hand_count: i32,
    pub outstanding_stolen_resource: i32,
    pub turn: i32,
    pub shuffles_crossed: i32,
    pub post_combat_relic_heal: i32,
    pub projected_battle_hp_lost: i32,
    pub combat_ended_turn: Option<i32>,
    pub death_turn: Option<i32>,
    pub only_death_routes: bool,
    pub confidence: String,
    pub search_boundary: String,
    pub has_risk: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostPotionUseDto {
    pub id: String,
    pub turn: Option<i32>,
    pub slot: Option<i32>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostResultDto {
    pub actions: Vec<HostActionTraceDto>,
    pub turn_outcomes: Vec<HostTurnOutcomeDto>,
    pub terminal: HostTerminalSnapshotDto,
    pub potion_uses: Vec<HostPotionUseDto>,
    pub expanded_nodes: i64,
    pub search_elapsed_milliseconds: f64,
    pub expanded_parents: Option<i64>,
    pub transition_count: Option<i64>,
    pub choice_replay_attempts: Option<i64>,
    pub observed_peak_parallelism: Option<i32>,
    pub active_phase_label: Option<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostProgressDto {
    pub expanded_nodes: i64,
    pub max_turn_reached: i32,
    pub elapsed_milliseconds: f64,
    pub current_best_projected_hp_loss: i32,
    pub current_best_won: bool,
    pub current_best_combat_ended_turn: Option<i32>,
    pub current_best_potion_count: i32,
    pub current_best: Option<HostResultDto>,
    pub expanded_parents: Option<i64>,
    pub transition_count: Option<i64>,
    pub choice_replay_attempts: Option<i64>,
    pub observed_peak_parallelism: Option<i32>,
    pub active_phase_label: Option<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostSolveResponse {
    pub abi_version: i32,
    pub outcome: String,
    pub failure_code: Option<String>,
    pub field_path: Option<String>,
    pub error: Option<String>,
    pub result: Option<HostResultDto>,
    pub best_so_far: Option<HostResultDto>,
    pub effective_degree_of_parallelism: i32,
    pub total_elapsed_milliseconds: f64,
}

fn failed_resp(
    field_path: Option<&str>,
    msg: impl Into<String>,
    dop: i32,
    elapsed_ms: f64,
) -> HostSolveResponse {
    HostSolveResponse {
        abi_version: ABI_VERSION as i32,
        outcome: "Failed".to_owned(),
        failure_code: None,
        field_path: field_path.map(str::to_owned),
        error: Some(msg.into()),
        result: None,
        best_so_far: None,
        effective_degree_of_parallelism: dop.clamp(1, 64),
        total_elapsed_milliseconds: elapsed_ms.max(0.0),
    }
}

fn capability_miss_resp(
    failure_code: &str,
    field_path: Option<&str>,
    msg: impl Into<String>,
    dop: i32,
    elapsed_ms: f64,
) -> HostSolveResponse {
    HostSolveResponse {
        abi_version: ABI_VERSION as i32,
        outcome: "CapabilityMiss".to_owned(),
        failure_code: Some(failure_code.to_owned()),
        field_path: field_path.map(str::to_owned),
        error: Some(msg.into()),
        result: None,
        best_so_far: None,
        effective_degree_of_parallelism: dop.clamp(1, 64),
        total_elapsed_milliseconds: elapsed_ms.max(0.0),
    }
}

fn map_unsupported_code(kind: &str) -> &'static str {
    match kind {
        "Encounter" => "encounter_unmapped",
        "Card" | "Relic" | "Power" | "Potion" | "Orb" | "Monster" | "Character"
        | "Enchantment" | "Affliction" | "Dominion" | "RunModel" => "model_unmapped",
        _ => "projection_unsupported",
    }
}

fn parse_boss_hp_strategy(
    val: Option<&str>,
    field: &'static str,
) -> Result<BossHpStrategy, (&'static str, String)> {
    match val {
        None | Some("ProgressionFirst") => Ok(BossHpStrategy::ProgressionFirst),
        Some("MinimizeHpLoss") => Ok(BossHpStrategy::MinimizeHpLoss),
        Some(other) => Err((field, format!("unsupported bossHpStrategy '{other}'"))),
    }
}

fn map_policy(
    req: &HostSolveRequest,
    p: &HostPolicyDto,
    effective_dop: usize,
) -> Result<SearchPolicy, (&'static str, String)> {
    let potion_policy = match p.potion_policy.as_str() {
        "Disabled" => SolverPotionPolicy::Disabled,
        "Smart" => SolverPotionPolicy::Smart,
        "RequireAtLeastOne" => SolverPotionPolicy::RequireAtLeastOne,
        other => {
            return Err((
                "policy.potionPolicy",
                format!("unsupported potionPolicy '{other}'"),
            ));
        }
    };
    let theft_policy = match p.theft_policy.as_deref() {
        None | Some("Ignore") => SolverTheftPolicy::Ignore,
        Some("PreserveResources") => SolverTheftPolicy::PreserveResources,
        Some(other) => {
            return Err((
                "policy.theftPolicy",
                format!("unsupported theftPolicy '{other}'"),
            ));
        }
    };

    let mut potion_directives = Vec::with_capacity(p.potion_directives.len());
    let mut seen_directive_keys = BTreeSet::new();
    for d in &p.potion_directives {
        if d.slot < 0 {
            return Err((
                "policy.potionDirectives",
                format!("negative potion directive slot {}", d.slot),
            ));
        }
        let pid = PotionId::from_model_id_str(d.potion_id.trim()).ok_or_else(|| {
            (
                "policy.potionDirectives",
                format!("unknown potionId '{}'", d.potion_id),
            )
        })?;
        let directive = match d.directive.as_str() {
            "Smart" => SolverPotionDirective::Smart,
            "Force" => SolverPotionDirective::Force,
            "Disabled" | "Forbid" => SolverPotionDirective::Disabled,
            other => {
                return Err((
                    "policy.potionDirectives",
                    format!("unsupported potion directive '{other}'"),
                ));
            }
        };
        if !seen_directive_keys.insert((d.slot as usize, pid)) {
            return Err((
                "policy.potionDirectives",
                format!("duplicate potion directive {}@{}", d.potion_id, d.slot),
            ));
        }
        potion_directives.push(PotionSlotDirective {
            slot: d.slot as usize,
            potion_id: pid,
            directive,
        });
    }

    let mut potion_hp_threshold_overrides = BTreeMap::new();
    for (k, &v) in &p.potion_hp_threshold_overrides {
        let pid = PotionId::from_model_id_str(k.trim()).ok_or_else(|| {
            (
                "policy.potionHpThresholdOverrides",
                format!("unknown potionId '{k}'"),
            )
        })?;
        if !(0..=100).contains(&v) {
            return Err((
                "policy.potionHpThresholdOverrides",
                format!("potionHpThresholdOverrides['{k}']={v} must be in 0..=100"),
            ));
        }
        potion_hp_threshold_overrides.insert(pid, v);
    }

    let mut relic_counter_rules = Vec::with_capacity(p.relic_counter_rules.len());
    let mut seen_relic_ids = BTreeSet::new();
    for r in &p.relic_counter_rules {
        let id = RelicCounterId::from_str_name(r.id.trim()).ok_or_else(|| {
            (
                "policy.relicCounterRules",
                format!("unsupported relic counter rule '{}'", r.id),
            )
        })?;
        if !seen_relic_ids.insert(id) {
            return Err((
                "policy.relicCounterRules",
                format!("duplicate relic counter rule '{}'", r.id),
            ));
        }
        if r.minimum < 0
            || r.maximum < r.minimum
            || r.maximum >= id.period()
            || !(0..=1000).contains(&r.hp_allowance)
            || !(1..=3).contains(&r.priority)
        {
            return Err((
                "policy.relicCounterRules",
                format!("out-of-range relic counter rule '{}'", r.id),
            ));
        }
        relic_counter_rules.push(RelicCounterRule {
            id,
            enabled: r.enabled,
            minimum: r.minimum,
            maximum: r.maximum,
            hp_allowance: r.hp_allowance,
            priority: r.priority,
        });
    }

    let gb = &p.growth_budgets;
    for (name, val) in [
        ("handOfGreed", gb.hand_of_greed),
        ("theHunt", gb.the_hunt),
        ("feed", gb.feed),
        ("royalties", gb.royalties),
        ("alchemize", gb.alchemize),
        ("geneticAlgorithm", gb.genetic_algorithm),
        ("theScythe", gb.the_scythe),
        ("goopy", gb.goopy),
        ("forbiddenGrimoire", gb.forbidden_grimoire),
        ("madScience", gb.mad_science),
        ("showOff", gb.show_off),
    ] {
        if !(0..=1000).contains(&val) {
            return Err((
                "policy.growthBudgets",
                format!("growth budget {name}={val} must be in 0..=1000"),
            ));
        }
    }
    if gb.show_off != 0 {
        return Err((
            "policy.growthBudgets.showOff",
            "ShowOff growth budget is not supported".to_owned(),
        ));
    }
    if let Some(tp) = &gb.third_party {
        if tp.values().any(|&v| v != 0) {
            return Err((
                "policy.growthBudgets.thirdParty",
                "non-zero third-party growth budgets are not supported".to_owned(),
            ));
        }
    }
    let growth_budgets = sts2_kernel::GrowthCounters {
        hand_of_greed: gb.hand_of_greed,
        the_hunt: gb.the_hunt,
        feed: gb.feed,
        royalties: gb.royalties,
        alchemize: gb.alchemize,
        genetic_algorithm: gb.genetic_algorithm,
        the_scythe: gb.the_scythe,
        goopy: gb.goopy,
        forbidden_grimoire: gb.forbidden_grimoire,
        mad_science: gb.mad_science,
    };

    if let Some(limit) = p.brightest_flame_max_hp_loss_limit {
        if !(0..=1000).contains(&limit) {
            return Err((
                "policy.brightestFlameMaxHpLossLimit",
                format!("brightestFlameMaxHpLossLimit {limit} must be in 0..=1000"),
            ));
        }
    }

    let act_transition_boss_hp_strategy = parse_boss_hp_strategy(
        p.act_transition_boss_hp_strategy.as_deref(),
        "policy.actTransitionBossHpStrategy",
    )?;
    let final_boss_hp_strategy = parse_boss_hp_strategy(
        p.final_boss_hp_strategy.as_deref(),
        "policy.finalBossHpStrategy",
    )?;

    if let Some(pf) = p.potion_free_beam_width {
        if pf <= 0 {
            return Err((
                "policy.potionFreeBeamWidth",
                "potionFreeBeamWidth must be positive".to_owned(),
            ));
        }
    }
    if let Some(pb) = p.potion_beam_width {
        if pb <= 0 {
            return Err((
                "policy.potionBeamWidth",
                "potionBeamWidth must be positive".to_owned(),
            ));
        }
    }
    let beam_width = if p.beam_width > 0 {
        p.beam_width as usize
    } else if let (Some(pf), Some(pb)) = (p.potion_free_beam_width, p.potion_beam_width) {
        (pf + pb).max(1) as usize
    } else {
        return Err(("policy.beamWidth", "beamWidth must be positive".to_owned()));
    };

    if p.max_expanded_nodes < 1 {
        return Err((
            "policy.maxExpandedNodes",
            "maxExpandedNodes must be positive".to_owned(),
        ));
    }
    if p.max_card_branches_per_node < 1 {
        return Err((
            "policy.maxCardBranchesPerNode",
            "maxCardBranchesPerNode must be positive".to_owned(),
        ));
    }
    if p.max_pile_choice_branches_per_action < 1 {
        return Err((
            "policy.maxPileChoiceBranchesPerAction",
            "maxPileChoiceBranchesPerAction must be positive".to_owned(),
        ));
    }
    if p.max_hand_choice_branches_per_action < 1 {
        return Err((
            "policy.maxHandChoiceBranchesPerAction",
            "maxHandChoiceBranchesPerAction must be positive".to_owned(),
        ));
    }

    if let Some(ver) = p.portfolio_options_version {
        if ver != 1 && ver != 2 {
            return Err((
                "policy.portfolioOptionsVersion",
                format!("unsupported portfolioOptionsVersion {ver}"),
            ));
        }
    }
    let effective_portfolio_version = p.portfolio_options_version.unwrap_or(1);
    let has_v2_options = p.use_novelty_portfolio
        || p.adaptive_novelty_refinement == Some(true)
        || p.use_early_turn_exploration
        || p.early_turn_exploration_depth.is_some_and(|d| d > 0)
        || p.early_turn_exploration_budget_milliseconds.is_some_and(|b| b > 0)
        || p.beam_width_portfolio_widths.is_some()
        || p.beam_width_portfolio_plain_baseline_member == Some(false)
        || p.offensive_refinement_portfolio == Some(true)
        || p.bounded_offensive_refinement_portfolio == Some(true)
        || p.reallocated_refinement_portfolio == Some(false)
        || p.stop_portfolio_at_hp_target == Some(false)
        || p.novelty_search.is_some()
        || p.novelty_budget.is_some()
        || p.portfolio_scheduling_mode.is_some();
    if effective_portfolio_version < 2 && has_v2_options {
        return Err((
            "policy.portfolioOptionsVersion",
            "v2 portfolio options require portfolioOptionsVersion >= 2".to_owned(),
        ));
    }

    if let Some(ws) = &p.beam_width_portfolio_widths {
        if ws.len() > 16 || ws.iter().any(|&w| w < 1) {
            return Err((
                "policy.beamWidthPortfolioWidths",
                "invalid beamWidthPortfolioWidths".to_owned(),
            ));
        }
    }
    if p.offensive_refinement_portfolio == Some(true)
        && p.bounded_offensive_refinement_portfolio == Some(true)
    {
        return Err((
            "policy.offensiveRefinementPortfolio",
            "offensiveRefinementPortfolio and boundedOffensiveRefinementPortfolio cannot both be true"
                .to_owned(),
        ));
    }

    let portfolio_scheduling_mode = match p.portfolio_scheduling_mode.as_deref() {
        None | Some("Sequential") => PortfolioSchedulingMode::Sequential,
        Some("Parallel") => PortfolioSchedulingMode::Parallel,
        Some(other) => {
            return Err((
                "policy.portfolioSchedulingMode",
                format!("unsupported portfolioSchedulingMode '{other}'"),
            ));
        }
    };

    let novelty_options = match &p.novelty_search {
        Some(ns) => {
            if !(1..=2).contains(&ns.width)
                || ns.familiar_allowance < 0
                || ns.max_open < 1
                || ns.max_novelty_entries < 1
                || ns.max_actions < 1
                || ns.max_turns < 1
            {
                return Err((
                    "policy.noveltySearch",
                    "noveltySearch options contain out-of-range values".to_owned(),
                ));
            }
            NoveltySearchOptions {
                width: ns.width as usize,
                familiar_allowance: ns.familiar_allowance as usize,
                max_open: ns.max_open as usize,
                max_novelty_entries: ns.max_novelty_entries as usize,
                max_actions: ns.max_actions as usize,
                max_turns: ns.max_turns,
            }
        }
        None => NoveltySearchOptions::default(),
    };

    let novelty_budget = match &p.novelty_budget {
        Some(nb) => {
            if nb.time_divisor < 2 || nb.maximum_milliseconds < 1 || nb.maximum_expanded_nodes < 1 {
                return Err((
                    "policy.noveltyBudget",
                    "noveltyBudget options contain out-of-range values".to_owned(),
                ));
            }
            NoveltyPortfolioBudget {
                time_divisor: nb.time_divisor,
                maximum_milliseconds: nb.maximum_milliseconds as u64,
                maximum_expanded_nodes: nb.maximum_expanded_nodes as usize,
            }
        }
        None => NoveltyPortfolioBudget::default(),
    };

    if let Some(depth) = p.early_turn_exploration_depth {
        if !(0..=8).contains(&depth) {
            return Err((
                "policy.earlyTurnExplorationDepth",
                format!("earlyTurnExplorationDepth {depth} must be in 0..=8"),
            ));
        }
    }
    if let Some(etc_ms) = p.early_turn_exploration_budget_milliseconds {
        if etc_ms < 0 {
            return Err((
                "policy.earlyTurnExplorationBudgetMilliseconds",
                format!("earlyTurnExplorationBudgetMilliseconds cannot be negative ({etc_ms})"),
            ));
        }
    }
    let effective_etc_depth = if p.use_early_turn_exploration {
        p.early_turn_exploration_depth.unwrap_or(2)
    } else {
        p.early_turn_exploration_depth.unwrap_or(0)
    };
    if effective_etc_depth > 0
        && p.early_turn_exploration_budget_milliseconds
            .is_some_and(|b| b < 1)
    {
        return Err((
            "policy.earlyTurnExplorationBudgetMilliseconds",
            "earlyTurnExplorationBudgetMilliseconds must be positive when depth > 0".to_owned(),
        ));
    }

    let budget_ms = p
        .soft_time_budget_milliseconds
        .max(req.search_budget_milliseconds)
        .max(1) as u64;

    Ok(SearchPolicy {
        beam_width,
        max_nodes: p.max_expanded_nodes as usize,
        time_budget_ms: budget_ms,
        allow_potions: potion_policy != SolverPotionPolicy::Disabled
            || potion_directives
                .iter()
                .any(|d| d.directive != SolverPotionDirective::Disabled),
        allow_random_cards: true,
        max_degree_of_parallelism: effective_dop,
        potion_policy,
        potion_directives,
        potion_hp_threshold_overrides,
        predict_potion_reward: p.predict_potion_reward,
        potion_reward_replacement_hp_credit: req.potion_reward_replacement_hp_credit,
        relic_strategy_enabled: p.relic_strategy_enabled,
        relic_counter_rules,
        growth_budgets,
        brightest_flame_max_hp_loss_limit: p.brightest_flame_max_hp_loss_limit,
        initial_brightest_flame_max_hp_spent: req
            .initial_brightest_flame_max_hp_spent
            .unwrap_or(0)
            .max(0),
        ignore_long_term_rewards: p.ignore_long_term_rewards,
        act_transition_boss_hp_strategy,
        final_boss_hp_strategy,
        is_non_final_act3_boss: req.is_non_final_act3_boss.unwrap_or(false),
        prior_battle_hp_lost: req.prior_battle_hp_lost.max(0),
        theft_policy,
        use_beam_width_portfolio: p.use_beam_width_portfolio,
        beam_width_portfolio_widths: p
            .beam_width_portfolio_widths
            .as_ref()
            .map(|ws| ws.iter().map(|&w| w as usize).collect()),
        beam_width_portfolio_plain_baseline_member: p
            .beam_width_portfolio_plain_baseline_member
            .unwrap_or(true),
        reallocated_refinement_portfolio: p.reallocated_refinement_portfolio.unwrap_or(true),
        offensive_refinement_portfolio: p.offensive_refinement_portfolio.unwrap_or(false),
        bounded_offensive_refinement_portfolio: p
            .bounded_offensive_refinement_portfolio
            .unwrap_or(false),
        stop_portfolio_at_hp_target: p.stop_portfolio_at_hp_target.unwrap_or(true),
        portfolio_scheduling_mode,
        use_novelty_search: p.use_novelty_portfolio,
        adaptive_novelty_refinement: p.adaptive_novelty_refinement.unwrap_or(false),
        novelty_options,
        novelty_budget,
        early_turn_exploration_depth: effective_etc_depth,
        early_turn_exploration_budget_ms: p
            .early_turn_exploration_budget_milliseconds
            .unwrap_or(0)
            .max(0) as u64,
        use_boss_tempo_search: false,
        acceptable_battle_hp_loss: p.acceptable_battle_hp_loss.max(0),
        stop_at_acceptable_battle_hp_loss: p.stop_at_acceptable_battle_hp_loss,
        potion_free_beam_width: p.potion_free_beam_width.map(|v| v as usize),
        potion_beam_width: p.potion_beam_width.map(|v| v as usize),
        max_turns: if req.live_state.room_type == "Boss" { 40 } else { 28 },
        max_card_branches_per_node: p.max_card_branches_per_node as usize,
        max_pile_choice_branches_per_action: p.max_pile_choice_branches_per_action as usize,
        max_hand_choice_branches_per_action: p.max_hand_choice_branches_per_action as usize,
        ..Default::default()
    })
}

fn sum_alive_enemy_hp(state: &BattleState) -> i32 {
    state
        .enemy_handles
        .iter()
        .filter_map(|&h| state.creature(h))
        .filter(|c| c.is_alive())
        .map(|c| c.current_hp.max(0))
        .sum()
}

fn count_alive_enemies(state: &BattleState) -> i32 {
    state
        .enemy_handles
        .iter()
        .filter_map(|&h| state.creature(h))
        .filter(|c| c.is_alive())
        .count() as i32
}

fn resolve_target_index_and_combat_id(
    state: &BattleState,
    target_handle: Option<i32>,
) -> Result<(i32, Option<u32>), String> {
    let Some(th) = target_handle else {
        return Ok((-1, None));
    };
    let ch = CreatureHandle(th as u32);
    if let Some(pos) = state.enemy_handles.iter().position(|&h| h == ch) {
        let cid = state.creature(ch).and_then(|c| c.combat_id);
        return Ok((pos as i32, cid));
    }
    if let Some(pos) = state.ally_handles.iter().position(|&h| h == ch) {
        let cid = state.creature(ch).and_then(|c| c.combat_id);
        return Ok((-2 - pos as i32, cid));
    }
    Err(format!("target handle {th} not found in active roster"))
}

fn identify_source_pile<'a>(
    state: &'a BattleState,
    req: &ChoiceRequest,
) -> (&'static str, Option<&'a [CardHandle]>) {
    if req.candidates.is_empty() {
        return ("None", None);
    }
    let all_in = |pile: &[CardHandle]| -> bool {
        req.candidates.iter().all(|c| pile.contains(c))
    };
    if all_in(&state.player.hand) {
        return ("Hand", Some(&state.player.hand));
    }
    if all_in(&state.player.draw) {
        return ("Draw", Some(&state.player.draw));
    }
    if all_in(&state.player.discard) {
        return ("Discard", Some(&state.player.discard));
    }
    if all_in(&state.player.exhaust) {
        return ("Exhaust", Some(&state.player.exhaust));
    }
    match req.source_pile {
        PileType::Hand => ("Hand", Some(&state.player.hand)),
        PileType::Draw => ("Draw", Some(&state.player.draw)),
        PileType::Discard => ("Discard", Some(&state.player.discard)),
        PileType::Exhaust => ("Exhaust", Some(&state.player.exhaust)),
        _ => ("None", None),
    }
}

fn count_preceding_matching_card(
    state: &BattleState,
    list: &[CardHandle],
    target: CardHandle,
) -> i32 {
    let Some(target_card) = state.card(target) else {
        return -1;
    };
    let mut count = 0i32;
    for &h in list {
        if h == target {
            return count;
        }
        if let Some(c) = state.card(h) {
            if c.id == target_card.id && c.upgrade_level == target_card.upgrade_level {
                count += 1;
            }
        }
    }
    -1
}

#[derive(Default)]
struct TurnAcc {
    hp_lost: i32,
    hp_recovered: i32,
    enemy_hp_lost: i32,
    max_block: i32,
    actual_block: i32,
    energy_left: i32,
    potion_count: i32,
    potion_strategic_cost: i32,
    combat_ended: bool,
}

fn has_renewable_potion_shaped_rock(state: &BattleState) -> bool {
    state
        .player
        .relics
        .iter()
        .any(|r| !r.is_melted && r.id == RelicId::PetrifiedToad)
}

fn replay_solution_trace(
    root: &BattleState,
    sol: &sts2_search::InCombatSolveResponse,
    prior_battle_hp_lost: i32,
    effective_dop: i32,
    potion_hp_threshold_overrides: &BTreeMap<PotionId, i32>,
    content: &dyn ContentDispatcher,
) -> Result<HostResultDto, String> {
    let mut sim = root.clone();
    let root_turn = root.player.turn_number.max(1);
    let root_initial_cum_hp_lost = root.player.cumulative_hp_lost;
    let root_initial_hp = root.player.current_hp;
    let root_shuffle_counter = root.run_rngs.get(RunRngType::Shuffle).counter;
    let renewable_rock = has_renewable_potion_shaped_rock(root);

    let mut turn_accs: BTreeMap<i32, TurnAcc> = BTreeMap::new();
    turn_accs.insert(
        root_turn,
        TurnAcc {
            max_block: sim.player.block,
            actual_block: sim.player.block,
            energy_left: sim.player.energy,
            ..Default::default()
        },
    );

    let mut traced_actions = Vec::with_capacity(sol.actions.len());
    let mut potion_uses = Vec::new();
    let mut action_turns = BTreeSet::new();

    for action in &sol.actions {
        if !sim.is_in_progress {
            return Err("replay encountered action after combat ended".to_owned());
        }

        let turn_before = sim.player.turn_number.max(1);
        let round_before = sim.round_number;
        let hp_lost_before = sim.player.cumulative_hp_lost;
        let hp_before = sim.player.current_hp;
        let block_before = sim.player.block;
        let energy_before = sim.player.energy;
        let raw_enemy_hp_before = sum_alive_enemy_hp(&sim);
        let slots_before = sim.player.potion_slots.clone();
        let alive_before: Vec<(u32, CreatureHandle)> = sim
            .enemy_handles
            .iter()
            .filter_map(|&h| {
                let c = sim.creature(h)?;
                if c.is_alive() {
                    c.combat_id.map(|cid| (cid, h))
                } else {
                    None
                }
            })
            .collect();

        let (
            kind_str,
            card_id_str,
            card_occurrence,
            card_upgrade_level,
            card_enchantment_id,
            target_index,
            target_combat_id,
            potion_id_str,
            potion_slot_i32,
            action_choices_vec,
        ) = match action {
            PlannedAction::TurnSetup { choices } => (
                "TurnSetup".to_owned(),
                String::new(),
                0,
                0,
                String::new(),
                -1,
                None,
                String::new(),
                -1,
                choices.clone().unwrap_or_default(),
            ),
            PlannedAction::PlayCard {
                card_handle,
                target_handle,
                choices,
            } => {
                let ch = CardHandle(*card_handle as u32);
                let card = sim
                    .card(ch)
                    .ok_or_else(|| format!("missing card handle {card_handle} during trace"))?;
                let cid_entry = card.id.model_entry().to_owned();
                let upg = card.upgrade_level;
                let ench_entry = sim
                    .card_ext(card.ext_index)
                    .and_then(|e| e.enchantment.as_ref())
                    .map(|en| en.id.model_entry().to_owned())
                    .unwrap_or_default();
                let mut occ = 0i32;
                let mut found_in_hand = false;
                for &hh in &sim.player.hand {
                    if hh == ch {
                        found_in_hand = true;
                        break;
                    }
                    if sim.card(hh).is_some_and(|hc| hc.id == card.id) {
                        occ += 1;
                    }
                }
                if !found_in_hand {
                    return Err(format!("card '{cid_entry}'#{card_handle} not found in hand"));
                }
                let (t_idx, t_cid) = resolve_target_index_and_combat_id(&sim, *target_handle)?;
                (
                    "PlayCard".to_owned(),
                    cid_entry,
                    occ,
                    upg,
                    ench_entry,
                    t_idx,
                    t_cid,
                    String::new(),
                    -1,
                    choices.clone().unwrap_or_default(),
                )
            }
            PlannedAction::UsePotion {
                potion_slot,
                potion_id,
                target_handle,
                choices,
            } => {
                let (t_idx, t_cid) = resolve_target_index_and_combat_id(&sim, *target_handle)?;
                let pid_entry = potion_id.model_entry().to_owned();
                (
                    "UsePotion".to_owned(),
                    String::new(),
                    0,
                    0,
                    String::new(),
                    t_idx,
                    t_cid,
                    pid_entry,
                    *potion_slot as i32,
                    choices.clone().unwrap_or_default(),
                )
            }
            PlannedAction::EndTurn { choices } => (
                "EndTurn".to_owned(),
                String::new(),
                0,
                0,
                String::new(),
                -1,
                None,
                String::new(),
                -1,
                choices.clone().unwrap_or_default(),
            ),
        };

        let mut empty_q: SmallVec<[Vec<usize>; 2]> = SmallVec::new();
        let mut step_outcome = match action {
            PlannedAction::TurnSetup { .. } => {
                let Some(pending) = sim.pending.as_ref() else {
                    return Err("TurnSetup action encountered with no pending choice".to_owned());
                };
                Ok(StepOutcome::NeedsChoice(Box::new(pending.request.clone())))
            }
            PlannedAction::PlayCard {
                card_handle,
                target_handle,
                ..
            } => CombatEngine::play_card(
                &mut sim,
                content,
                CardHandle(*card_handle as u32),
                target_handle.map(|h| CreatureHandle(h as u32)),
                &mut empty_q,
            ),
            PlannedAction::UsePotion {
                potion_slot,
                target_handle,
                ..
            } => CombatEngine::use_potion(
                &mut sim,
                content,
                *potion_slot,
                target_handle.map(|h| CreatureHandle(h as u32)),
                &mut empty_q,
            ),
            PlannedAction::EndTurn { .. } => CombatEngine::end_player_turn(&mut sim, content),
        }
        .map_err(|e| format!("trace execution error: {e}"))?;

        let mut choice_iter = action_choices_vec.into_iter();
        let mut traced_choices = Vec::new();
        while let StepOutcome::NeedsChoice(choice_req) = step_outcome {
            let sel_indices = choice_iter
                .next()
                .ok_or_else(|| "missing planned choice indices during trace".to_owned())?;
            let timing = if sim.current_side == CombatSide::Enemy {
                "EnemyTurn"
            } else if sim.player.turn_number > turn_before || sim.round_number > round_before {
                "PlayerTurnStart"
            } else {
                match choice_req.timing {
                    ChoiceTiming::Action => "Action",
                    ChoiceTiming::PlayerTurnEnd => "PlayerTurnEnd",
                    ChoiceTiming::EnemyTurn => "EnemyTurn",
                    ChoiceTiming::PlayerTurnStart => "PlayerTurnStart",
                }
            };
            let source_id = choice_req
                .source_id
                .rsplit_once('.')
                .map_or(choice_req.source_id.as_ref(), |(_, entry)| entry)
                .to_owned();
            let (source_pile_name, matching_pile) = identify_source_pile(&sim, &choice_req);
            let mut card_tokens = Vec::with_capacity(sel_indices.len());
            for &opt_idx in &sel_indices {
                let &cand_h = choice_req
                    .candidates
                    .get(opt_idx)
                    .ok_or_else(|| format!("choice index {opt_idx} out of bounds"))?;
                let cand_card = sim
                    .card(cand_h)
                    .ok_or_else(|| format!("choice card handle {:?} missing", cand_h))?;
                let opt_occ = count_preceding_matching_card(&sim, &choice_req.candidates, cand_h);
                let src_occ = match matching_pile {
                    Some(pile) => count_preceding_matching_card(&sim, pile, cand_h),
                    None => -1,
                };
                card_tokens.push(HostChoiceCardTokenDto {
                    card_id: cand_card.id.model_entry().to_owned(),
                    upgrade_level: cand_card.upgrade_level,
                    option_occurrence: opt_occ.max(0),
                    source_occurrence: src_occ,
                });
            }
            traced_choices.push(HostChoiceTraceDto {
                timing: timing.to_owned(),
                source_id,
                source_pile: source_pile_name.to_owned(),
                cards: card_tokens,
            });
            step_outcome = CombatEngine::resume_choice(&mut sim, content, &sel_indices)
                .map_err(|e| format!("trace resume_choice error: {e}"))?;
        }
        if choice_iter.next().is_some() {
            return Err("unused planned choice indices remained after trace action".to_owned());
        }

        // Capture both explicit UsePotion and any automatic potion consumption (e.g., FairyInABottle).
        let explicit_slot = match action {
            PlannedAction::UsePotion { potion_slot, .. } => Some(*potion_slot),
            _ => None,
        };
        if let PlannedAction::UsePotion {
            potion_slot,
            potion_id,
            ..
        } = action
        {
            potion_uses.push(HostPotionUseDto {
                id: potion_id.model_entry().to_owned(),
                turn: Some(turn_before),
                slot: Some(*potion_slot as i32),
            });
            let acc = turn_accs.entry(turn_before).or_default();
            acc.potion_count += 1;
            acc.potion_strategic_cost +=
                PotionValuationRegistry::strategic_hp_cost_for_root(
                    *potion_id,
                    renewable_rock,
                    root.player.max_hp,
                    potion_hp_threshold_overrides,
                );
        }
        for (slot_idx, (before_opt, after_opt)) in slots_before
            .iter()
            .zip(sim.player.potion_slots.iter())
            .enumerate()
        {
            if explicit_slot == Some(slot_idx) {
                continue;
            }
            if let (Some(pid), None) = (before_opt, after_opt) {
                if *pid == PotionId::FairyInABottle {
                    potion_uses.push(HostPotionUseDto {
                        id: pid.model_entry().to_owned(),
                        turn: Some(turn_before),
                        slot: Some(slot_idx as i32),
                    });
                    let acc = turn_accs.entry(turn_before).or_default();
                    acc.potion_count += 1;
                    acc.potion_strategic_cost +=
                        PotionValuationRegistry::strategic_hp_cost_with_overrides(
                            *pid,
                            renewable_rock,
                            potion_hp_threshold_overrides,
                        );
                }
            }
        }

        let mut killed_ids = Vec::new();
        for (cid, eh) in alive_before {
            if sim.escaped_handles.contains(&eh) {
                continue;
            }
            if sim.creature(eh).is_some_and(|c| c.is_dead()) {
                killed_ids.push(cid);
            }
        }

        action_turns.insert(turn_before);
        traced_actions.push(HostActionTraceDto {
            turn: turn_before,
            kind: kind_str,
            card_id: card_id_str,
            card_occurrence,
            card_upgrade_level,
            card_enchantment_id,
            target_index,
            target_combat_id,
            potion_id: potion_id_str,
            potion_slot: potion_slot_i32,
            choices: traced_choices,
            flashed_relic_ids: Vec::new(),
            killed_enemy_combat_ids: killed_ids,
        });

        let hp_loss_delta = (sim.player.cumulative_hp_lost - hp_lost_before).max(0);
        let hp_recovered_delta = (sim.player.current_hp - hp_before + hp_loss_delta).max(0);
        let enemy_hp_loss_delta = (raw_enemy_hp_before - sum_alive_enemy_hp(&sim)).max(0);
        let turn_advanced = matches!(action, PlannedAction::EndTurn { .. })
            || sim.player.turn_number > turn_before;

        {
            let acc = turn_accs.entry(turn_before).or_default();
            acc.hp_lost += hp_loss_delta;
            acc.hp_recovered += hp_recovered_delta;
            acc.enemy_hp_lost += enemy_hp_loss_delta;
            if turn_advanced {
                acc.actual_block = block_before;
                acc.energy_left = energy_before;
                acc.max_block = acc.max_block.max(block_before);
            } else {
                acc.actual_block = sim.player.block;
                acc.energy_left = sim.player.energy;
                acc.max_block = acc.max_block.max(sim.player.block);
            }
            if !sim.is_in_progress {
                acc.combat_ended = sim.won && sim.has_living_player_creature();
            }
        }
        if sim.is_in_progress && turn_advanced {
            let next_turn = sim.player.turn_number.max(1);
            turn_accs.entry(next_turn).or_insert_with(|| TurnAcc {
                max_block: sim.player.block,
                actual_block: sim.player.block,
                energy_left: sim.player.energy,
                ..Default::default()
            });
        }
    }

    let turn_outcomes: Vec<HostTurnOutcomeDto> = if traced_actions.is_empty() {
        let acc = turn_accs.get(&root_turn).cloned().unwrap_or_default();
        vec![HostTurnOutcomeDto {
            turn: root_turn,
            hp_lost: acc.hp_lost,
            hp_recovered: acc.hp_recovered,
            enemy_hp_lost: acc.enemy_hp_lost,
            sold_hp: 0,
            max_block: acc.max_block,
            actual_block: acc.actual_block,
            energy_left: acc.energy_left,
            potion_count: acc.potion_count,
            potion_strategic_cost: acc.potion_strategic_cost,
            combat_ended: acc.combat_ended,
        }]
    } else {
        action_turns
            .iter()
            .map(|&t| {
                let acc = turn_accs.get(&t).cloned().unwrap_or_default();
                HostTurnOutcomeDto {
                    turn: t,
                    hp_lost: acc.hp_lost,
                    hp_recovered: acc.hp_recovered,
                    enemy_hp_lost: acc.enemy_hp_lost,
                    sold_hp: 0,
                    max_block: acc.max_block,
                    actual_block: acc.actual_block,
                    energy_left: acc.energy_left,
                    potion_count: acc.potion_count,
                    potion_strategic_cost: acc.potion_strategic_cost,
                    combat_ended: acc.combat_ended,
                }
            })
            .collect()
    };

    let player_dead = !sim.has_living_player_creature() || sim.player.current_hp <= 0;
    let is_event_defeat = !player_dead && sim.battleworn_dummy_timed_out;
    let all_enemies_dead = sim.won && !player_dead && !is_event_defeat;
    let post_root_hp_loss = (sim.player.cumulative_hp_lost - root_initial_cum_hp_lost).max(0);
    let post_root_recovered =
        (sim.player.current_hp - root_initial_hp + post_root_hp_loss).max(0);
    let projected_battle_hp_lost = prior_battle_hp_lost + post_root_hp_loss;
    let final_turn = sim.player.turn_number.max(1);
    let combat_ended_turn = if all_enemies_dead {
        Some(final_turn)
    } else {
        None
    };
    let death_turn = if player_dead { Some(final_turn) } else { None };
    let only_death_routes = player_dead && !all_enemies_dead;
    let post_combat_relic_heal = if all_enemies_dead {
        PostCombatRelicHealProfile::from_state(&sim)
            .heal_for(sim.player.current_hp, sim.player.max_hp)
    } else {
        0
    };
    let search_boundary = if is_event_defeat {
        "EventDefeat"
    } else if all_enemies_dead || player_dead {
        "None"
    } else {
        match sol.stats.termination.as_str() {
            "EventDefeat" => "EventDefeat",
            "TimeLimit" => "TimeLimit",
            "TurnLimit" => "TurnLimit",
            _ => "NodeLimit",
        }
    };
    let confidence = if only_death_routes {
        "DeathOnly"
    } else if combat_ended_turn.is_some() || is_event_defeat {
        "Complete"
    } else {
        "Bounded"
    };
    let has_risk = !all_enemies_dead && !is_event_defeat && search_boundary != "None";
    let shuffles_crossed = (sim.run_rngs.get(RunRngType::Shuffle).counter - root_shuffle_counter).max(0);
    let mut enemy_hp = sum_alive_enemy_hp(&sim);
    if is_event_defeat {
        let escaped_hp: i32 = sim
            .escaped_handles
            .iter()
            .filter_map(|&h| sim.creature(h))
            .map(|c| c.current_hp.max(0))
            .sum();
        enemy_hp += escaped_hp;
    }

    let terminal = HostTerminalSnapshotDto {
        player_dead,
        all_enemies_dead,
        player_hp: sim.player.current_hp,
        player_max_hp: sim.player.max_hp,
        cumulative_player_hp_lost: post_root_hp_loss,
        recovered_player_hp: post_root_recovered,
        projected_player_hp: sim.player.current_hp,
        player_block: sim.player.block,
        enemy_hp,
        alive_enemy_count: count_alive_enemies(&sim),
        energy: sim.player.energy,
        stars: sim.player.stars,
        hand_count: sim.player.hand.len() as i32,
        outstanding_stolen_resource: 0,
        turn: final_turn,
        shuffles_crossed,
        post_combat_relic_heal,
        projected_battle_hp_lost,
        combat_ended_turn,
        death_turn,
        only_death_routes,
        confidence: confidence.to_owned(),
        search_boundary: search_boundary.to_owned(),
        has_risk,
    };

    Ok(HostResultDto {
        actions: traced_actions,
        turn_outcomes,
        terminal,
        potion_uses,
        expanded_nodes: sol.nodes_expanded as i64,
        search_elapsed_milliseconds: sol.stats.elapsed_ms as f64,
        expanded_parents: Some(sol.nodes_expanded as i64),
        transition_count: Some(sol.stats.transition_count as i64),
        choice_replay_attempts: Some(sol.stats.choice_replay_attempts as i64),
        observed_peak_parallelism: Some(effective_dop),
        active_phase_label: Some("RustKernel".to_owned()),
    })
}

impl Clone for TurnAcc {
    fn clone(&self) -> Self {
        Self {
            hp_lost: self.hp_lost,
            hp_recovered: self.hp_recovered,
            enemy_hp_lost: self.enemy_hp_lost,
            max_block: self.max_block,
            actual_block: self.actual_block,
            energy_left: self.energy_left,
            potion_count: self.potion_count,
            potion_strategic_cost: self.potion_strategic_cost,
            combat_ended: self.combat_ended,
        }
    }
}

pub fn solve_in_combat_host_v1_json(
    req_json: &[u8],
    cancel: &AtomicBool,
    progress_cb: Option<&(dyn Fn(&HostProgressDto) + Sync)>,
) -> HostSolveResponse {
    let started = Instant::now();
    let mut req: HostSolveRequest = match serde_json::from_slice(req_json) {
        Ok(r) => r,
        Err(e) => {
            return failed_resp(None, format!("invalid host request JSON: {e}"), 1, 0.0);
        }
    };

    if req.abi_version != ABI_VERSION as i32 {
        return failed_resp(
            Some("abiVersion"),
            format!(
                "unsupported ABI version {}; expected {}",
                req.abi_version, ABI_VERSION
            ),
            1,
            started.elapsed().as_secs_f64() * 1000.0,
        );
    }
    if req.request_id.trim().is_empty() {
        return failed_resp(
            Some("requestId"),
            "requestId cannot be empty",
            1,
            started.elapsed().as_secs_f64() * 1000.0,
        );
    }
    if req.character_id.trim().is_empty() {
        return failed_resp(
            Some("characterId"),
            "characterId cannot be empty",
            1,
            started.elapsed().as_secs_f64() * 1000.0,
        );
    }
    if req.game_mode.trim().is_empty() {
        return failed_resp(
            Some("gameMode"),
            "gameMode cannot be empty",
            1,
            started.elapsed().as_secs_f64() * 1000.0,
        );
    }
    if req.prior_battle_hp_lost < 0 {
        return failed_resp(
            Some("priorBattleHpLost"),
            "priorBattleHpLost cannot be negative",
            1,
            started.elapsed().as_secs_f64() * 1000.0,
        );
    }

    let hw = std::thread::available_parallelism()
        .map(|n| n.get())
        .unwrap_or(1);
    let effective_dop = (req.max_degree_of_parallelism.max(1) as usize)
        .min(hw)
        .clamp(1, 16);
    let dop_i32 = effective_dop as i32;

    let policy_dto: HostPolicyDto = match serde_json::from_value(req.policy.clone()) {
        Ok(p) => p,
        Err(e) => {
            let msg = e.to_string();
            if msg.contains("unknown field") {
                return capability_miss_resp(
                    "policy_unsupported",
                    Some("policy"),
                    format!("unsupported policy field: {msg}"),
                    dop_i32,
                    started.elapsed().as_secs_f64() * 1000.0,
                );
            }
            return failed_resp(
                Some("policy"),
                format!("invalid host policy JSON: {msg}"),
                dop_i32,
                started.elapsed().as_secs_f64() * 1000.0,
            );
        }
    };

    let search_policy = match map_policy(&req, &policy_dto, effective_dop) {
        Ok(p) => p,
        Err((field, msg)) => {
            return capability_miss_resp(
                "policy_unsupported",
                Some(field),
                msg,
                dop_i32,
                started.elapsed().as_secs_f64() * 1000.0,
            );
        }
    };

    req.live_state.character_id = Some(req.character_id.clone());
    req.live_state.ascension = req.ascension;
    req.live_state.game_mode = Some(req.game_mode.clone());
    req.live_state.dominion_id = req.dominion_id.clone();
    req.live_state.run_model_ids = req
        .run_model_ids
        .iter()
        .filter(|id| {
            let entry = id
                .strip_prefix("RUN.")
                .or_else(|| id.strip_prefix("RUN_MODEL."))
                .unwrap_or(id.as_str());
            !matches!(entry, "MULTIPLAYER_SCALING_MODEL" | "MULTIPLAYER_SCALING")
        })
        .cloned()
        .collect();
    req.live_state.encounter_is_weak = req.encounter_is_weak;
    req.live_state.initial_encounter_monster_count = req.initial_encounter_monster_count;
    req.live_state.total_floor = req.total_floor;

    let content = Sts2Content;
    let mut root_state = match import_live_state(&req.live_state, &content) {
        Ok(s) => s,
        Err(e) => match e.kind() {
            KernelErrorKind::Unsupported { kind, id } => {
                return capability_miss_resp(
                    map_unsupported_code(kind),
                    None,
                    format!("unsupported {kind}: {id}"),
                    dop_i32,
                    started.elapsed().as_secs_f64() * 1000.0,
                );
            }
            _ => {
                return failed_resp(
                    Some("liveState"),
                    e.to_string(),
                    dop_i32,
                    started.elapsed().as_secs_f64() * 1000.0,
                );
            }
        },
    };
    {
        let ctx = Arc::make_mut(&mut root_state.static_ctx);
        ctx.act_index = req.act_index;
        ctx.map_col = req.map_col;
        ctx.map_row = req.map_row;
    }

    let root_turn = root_state.player.turn_number.max(1);
    let prior_loss = req.prior_battle_hp_lost;
    let forward_progress = |p: &SearchProgress| {
        if let Some(cb) = progress_cb {
            cb(&HostProgressDto {
                expanded_nodes: p.nodes_expanded as i64,
                max_turn_reached: p.current_turn.max(root_turn),
                elapsed_milliseconds: p.elapsed_ms as f64,
                current_best_projected_hp_loss: prior_loss + p.best_hp_lost.unwrap_or(0),
                current_best_won: p.best_won,
                current_best_combat_ended_turn: None,
                current_best_potion_count: 0,
                current_best: None,
                expanded_parents: Some(p.nodes_expanded as i64),
                transition_count: Some(p.transition_count as i64),
                choice_replay_attempts: Some(p.choice_replay_attempts as i64),
                observed_peak_parallelism: Some(dop_i32),
                active_phase_label: Some("RustKernel".to_owned()),
            });
        }
    };
    let progress_ref: Option<&(dyn Fn(&SearchProgress) + Sync)> =
        progress_cb.map(|_| &forward_progress as &(dyn Fn(&SearchProgress) + Sync));

    let sol = match SearchEngine.solve_state_with_controls(
        &root_state,
        &search_policy,
        &content,
        cancel,
        progress_ref,
    ) {
        Ok(s) => s,
        Err(e) => match e.kind() {
            KernelErrorKind::Unsupported { kind, id } => {
                return capability_miss_resp(
                    map_unsupported_code(kind),
                    None,
                    format!("unsupported {kind}: {id}"),
                    dop_i32,
                    started.elapsed().as_secs_f64() * 1000.0,
                );
            }
            _ => {
                return failed_resp(
                    None,
                    e.to_string(),
                    dop_i32,
                    started.elapsed().as_secs_f64() * 1000.0,
                );
            }
        },
    };

    let is_cancelled = cancel.load(Ordering::Relaxed) || sol.stats.termination == "Cancelled";
    let trace_res = replay_solution_trace(
        &root_state,
        &sol,
        prior_loss,
        dop_i32,
        &search_policy.potion_hp_threshold_overrides,
        &content,
    );
    let elapsed_ms = started.elapsed().as_secs_f64() * 1000.0;

    if is_cancelled {
        let best_so_far = match trace_res {
            Ok(r) if !r.actions.is_empty() => Some(r),
            _ => None,
        };
        return HostSolveResponse {
            abi_version: ABI_VERSION as i32,
            outcome: "Cancelled".to_owned(),
            failure_code: None,
            field_path: None,
            error: Some("The Rust combat kernel search was cancelled.".to_owned()),
            result: None,
            best_so_far,
            effective_degree_of_parallelism: dop_i32,
            total_elapsed_milliseconds: elapsed_ms,
        };
    }

    match trace_res {
        Ok(result_dto) => HostSolveResponse {
            abi_version: ABI_VERSION as i32,
            outcome: "Succeeded".to_owned(),
            failure_code: None,
            field_path: None,
            error: None,
            result: Some(result_dto),
            best_so_far: None,
            effective_degree_of_parallelism: dop_i32,
            total_elapsed_milliseconds: elapsed_ms,
        },
        Err(msg) => failed_resp(Some("result"), msg, dop_i32, elapsed_ms),
    }
}
