use crate::eval::{BeamWeightPerturbation, BeamWeightTerm};
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Serialize, Deserialize)]
pub enum PortfolioSchedulingMode {
    #[default]
    Sequential,
    Parallel,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct BeamWidthPortfolioMemberSpec {
    pub beam_width: usize,
    pub second_rank_band: bool,
    pub base_score_only: bool,
    pub aggressive_power_commitment: bool,
    pub offensive_refinement: bool,
    pub bounded_refinement: bool,
}

#[derive(Clone, Debug)]
pub struct SolverSearchProfile {
    pub beam_width: usize,
    pub potion_free_beam_width: Option<usize>,
    pub potion_beam_width: Option<usize>,
    pub max_expanded_nodes: usize,
    pub max_card_branches_per_node: usize,
    pub max_pile_choice_branches_per_action: usize,
    pub max_hand_choice_branches_per_action: usize,
    pub soft_time_budget_ms: u64,
    pub second_rank_band: bool,
    pub base_score_only: bool,
    pub aggressive_power_commitment: bool,
    pub boss_tempo_hp_pricing: bool,
    pub continuous_threat_ranking: bool,
    pub reallocated_refinement_portfolio: bool,
    pub stop_portfolio_at_hp_target: bool,
    pub beam_weight_perturbation: Option<BeamWeightPerturbation>,
}

impl Default for SolverSearchProfile {
    fn default() -> Self {
        Self {
            beam_width: 60,
            potion_free_beam_width: None,
            potion_beam_width: None,
            max_expanded_nodes: 120_000,
            max_card_branches_per_node: 32,
            max_pile_choice_branches_per_action: 18,
            max_hand_choice_branches_per_action: 24,
            soft_time_budget_ms: 120_000,
            second_rank_band: false,
            base_score_only: false,
            aggressive_power_commitment: false,
            boss_tempo_hp_pricing: false,
            continuous_threat_ranking: false,
            reallocated_refinement_portfolio: true,
            stop_portfolio_at_hp_target: true,
            beam_weight_perturbation: None,
        }
    }
}

#[derive(Clone, Debug)]
pub struct BeamWidthPortfolioRun<T> {
    pub result: T,
    pub expanded_nodes: usize,
    pub transition_count: u64,
    pub termination: &'static str,
    pub terminal: bool,
    pub won: bool,
    pub battle_hp_lost: Option<i32>,
    pub potion_count: usize,
    pub stop_portfolio: bool,
    pub memory_truncated: bool,
}

#[derive(Clone, Debug)]
pub struct BeamWidthPortfolioMember {
    pub spec: BeamWidthPortfolioMemberSpec,
    pub node_budget: usize,
    pub ran: bool,
    pub expanded_nodes: usize,
    pub transition_count: u64,
    pub termination: Option<&'static str>,
    pub terminal: Option<bool>,
    pub won: Option<bool>,
    pub battle_hp_lost: Option<i32>,
    pub potion_count: Option<usize>,
    pub compared: bool,
    pub skipped_reason: Option<&'static str>,
}

#[derive(Clone, Debug)]
pub struct BeamWidthPortfolioOutcome<T> {
    pub selected: T,
    pub selected_index: usize,
    pub selection_reason: &'static str,
    pub members: Vec<BeamWidthPortfolioMember>,
    pub total_expanded_nodes: usize,
    pub total_transition_count: u64,
}

#[derive(Clone, Copy, Debug, Default)]
pub struct BeamWidthPortfolioBaseline {
    pub frontier_exhausted: bool,
    pub proven_zero_damage: bool,
    pub elapsed_ms: u64,
    pub expanded_nodes: usize,
    pub beam_width: usize,
}

pub struct BeamWidthPortfolioGate;

impl BeamWidthPortfolioGate {
    pub const SKIPPED_BASELINE_NOT_FRONTIER_EXHAUSTED: &'static str = "BaselineNotFrontierExhausted";
    pub const SKIPPED_BASELINE_PROVEN_ZERO_DAMAGE: &'static str = "BaselineProvenZeroDamage";
    pub const SKIPPED_BASELINE_TIME_SHARE_EXCEEDED: &'static str = "BaselineTimeShareExceeded";
    pub const SKIPPED_NODE_HEADROOM: &'static str = "NodeHeadroomInsufficient";
    pub const SKIPPED_TIME_HEADROOM: &'static str = "TimeHeadroomInsufficient";

    pub const BASELINE_TIME_SHARE_DIVISOR: u64 = 4;
    pub const SAFETY_NUMERATOR: u64 = 3;
    pub const SAFETY_DENOMINATOR: u64 = 2;

    #[inline]
    pub fn estimate_member_cost(
        baseline_cost: u64,
        baseline_beam_width: usize,
        member_beam_width: usize,
    ) -> u64 {
        let num = baseline_cost
            .saturating_mul(member_beam_width as u64)
            .saturating_mul(Self::SAFETY_NUMERATOR);
        let den = (baseline_beam_width.max(1) as u64) * Self::SAFETY_DENOMINATOR;
        (num + den - 1) / den
    }

    pub fn reject_refinement(
        baseline: &BeamWidthPortfolioBaseline,
        member_beam_width: usize,
        remaining_nodes: usize,
        remaining_ms: u64,
        time_budget_ms: u64,
    ) -> Option<&'static str> {
        if baseline.proven_zero_damage {
            return Some(Self::SKIPPED_BASELINE_PROVEN_ZERO_DAMAGE);
        }
        if !baseline.frontier_exhausted {
            return Some(Self::SKIPPED_BASELINE_NOT_FRONTIER_EXHAUSTED);
        }
        if baseline.elapsed_ms.saturating_mul(Self::BASELINE_TIME_SHARE_DIVISOR) > time_budget_ms {
            return Some(Self::SKIPPED_BASELINE_TIME_SHARE_EXCEEDED);
        }
        if remaining_nodes < baseline.expanded_nodes {
            return Some(Self::SKIPPED_NODE_HEADROOM);
        }
        if Self::estimate_member_cost(
            baseline.elapsed_ms,
            baseline.beam_width,
            member_beam_width,
        ) > remaining_ms
        {
            return Some(Self::SKIPPED_TIME_HEADROOM);
        }
        None
    }
}

pub struct PowerCommitmentPortfolioGate;

impl PowerCommitmentPortfolioGate {
    pub const SKIPPED_NO_REACHABLE_POWER: &'static str = "NoReachableRegisteredPower";

    #[inline]
    pub fn reject(has_reachable_power: bool) -> Option<&'static str> {
        if has_reachable_power {
            None
        } else {
            Some(Self::SKIPPED_NO_REACHABLE_POWER)
        }
    }
}

pub struct BeamWidthPortfolio;

impl BeamWidthPortfolio {
    pub const SKIPPED_BUDGET_EXHAUSTED: &'static str = "BudgetExhausted";
    pub const SKIPPED_NODE_LIMIT_NOT_TERMINAL: &'static str = "NodeLimitNotTerminal";
    pub const SKIPPED_POWER_MEMBER_NOT_TERMINAL: &'static str = "PowerMemberNotTerminal";
    pub const NODE_LIMIT_TERMINATION: &'static str = "NodeLimit";
    pub const SKIPPED_MEMORY_TRUNCATED: &'static str = "MemoryTruncated";
    pub const SELECTION_BASELINE_FALLBACK: &'static str = "BaselineFallback";
    pub const SELECTION_BEST: &'static str = "Best";
    pub const SELECTION_STOPPED: &'static str = "StopPortfolio";

    pub const NARROW_REFINEMENT_RATIO: f64 = 2.0 / 3.0;
    pub const WIDE_REFINEMENT_RATIO: f64 = 3.0 / 2.0;
    pub const OFFENSIVE_REFINEMENT_RATIO: f64 = 2.0 / 5.0;
    pub const BOUNDED_REFINEMENT_WORK_DIVISOR: usize = 8;

    #[inline]
    pub fn dedicated_power_node_reserve(shared_max_expanded_nodes: usize) -> usize {
        (shared_max_expanded_nodes / 5).max(1)
    }

    #[inline]
    pub fn scaled_width(baseline_beam_width: usize, ratio: f64) -> usize {
        ((baseline_beam_width as f64) * ratio).round().max(1.0) as usize
    }

    pub fn production_members(
        baseline_beam_width: usize,
        configured_widths: Option<&[usize]>,
        include_plain_baseline: bool,
        include_power_commitment_member: bool,
        use_offensive_refinement: bool,
        append_bounded_offensive_refinement: bool,
    ) -> Vec<BeamWidthPortfolioMemberSpec> {
        let base_w = baseline_beam_width.max(1);
        let mut members = Vec::new();
        if include_plain_baseline {
            members.push(BeamWidthPortfolioMemberSpec {
                beam_width: base_w,
                ..Default::default()
            });
        }
        if include_power_commitment_member {
            members.push(BeamWidthPortfolioMemberSpec {
                beam_width: base_w,
                aggressive_power_commitment: true,
                ..Default::default()
            });
        }
        if let Some(widths) = configured_widths {
            if !widths.is_empty() {
                for &w in widths {
                    if w >= 1 {
                        let m = BeamWidthPortfolioMemberSpec {
                            beam_width: w,
                            ..Default::default()
                        };
                        if !members.contains(&m) {
                            members.push(m);
                        }
                    }
                }
                return members;
            }
        }
        let narrow = BeamWidthPortfolioMemberSpec {
            beam_width: Self::scaled_width(base_w, Self::NARROW_REFINEMENT_RATIO),
            ..Default::default()
        };
        let second = if use_offensive_refinement {
            BeamWidthPortfolioMemberSpec {
                beam_width: Self::scaled_width(base_w, Self::OFFENSIVE_REFINEMENT_RATIO),
                offensive_refinement: true,
                ..Default::default()
            }
        } else {
            BeamWidthPortfolioMemberSpec {
                beam_width: Self::scaled_width(base_w, Self::WIDE_REFINEMENT_RATIO),
                ..Default::default()
            }
        };
        for m in [narrow, second] {
            if !members.contains(&m) {
                members.push(m);
            }
        }
        members.push(BeamWidthPortfolioMemberSpec {
            beam_width: base_w,
            second_rank_band: true,
            ..Default::default()
        });
        members.push(BeamWidthPortfolioMemberSpec {
            beam_width: base_w,
            base_score_only: true,
            ..Default::default()
        });
        if append_bounded_offensive_refinement {
            members.push(BeamWidthPortfolioMemberSpec {
                beam_width: Self::scaled_width(base_w, Self::OFFENSIVE_REFINEMENT_RATIO),
                offensive_refinement: true,
                bounded_refinement: true,
                ..Default::default()
            });
        }
        members
    }

    /// 1:1 port of `BeamWidthPortfolio.MoveLeadingBandToTail`.
    pub fn move_leading_band_to_tail<T>(ranked: &mut Vec<T>, band_width: usize) {
        if band_width == 0 || ranked.len() <= band_width {
            return;
        }
        ranked.rotate_left(band_width);
    }

    pub fn run<T, Solve, IsBetter, Reject>(
        member_specs: &[BeamWidthPortfolioMemberSpec],
        shared_max_expanded_nodes: usize,
        base_profile: &SolverSearchProfile,
        mut solve: Solve,
        mut is_better: IsBetter,
        mut reject_member: Reject,
    ) -> BeamWidthPortfolioOutcome<T>
    where
        Solve: FnMut(&SolverSearchProfile) -> BeamWidthPortfolioRun<T>,
        IsBetter: FnMut(&T, &T) -> bool,
        Reject: FnMut(&BeamWidthPortfolioMemberSpec) -> Option<&'static str>,
    {
        let mut members = Vec::with_capacity(member_specs.len());
        let mut remaining_nodes = shared_max_expanded_nodes;
        let mut total_expanded = 0usize;
        let mut total_transitions = 0u64;
        let mut selected: Option<T> = None;
        let mut selected_index: Option<usize> = None;
        let mut first_ran: Option<T> = None;
        let mut first_ran_index: Option<usize> = None;
        let mut stopped = false;

        for (index, &spec) in member_specs.iter().enumerate() {
            if let Some(rejection) = reject_member(&spec) {
                members.push(BeamWidthPortfolioMember {
                    spec,
                    node_budget: 0,
                    ran: false,
                    expanded_nodes: 0,
                    transition_count: 0,
                    termination: None,
                    terminal: None,
                    won: None,
                    battle_hp_lost: None,
                    potion_count: None,
                    compared: false,
                    skipped_reason: Some(rejection),
                });
                continue;
            }
            let mut member_node_budget = if spec.aggressive_power_commitment {
                remaining_nodes.max(Self::dedicated_power_node_reserve(shared_max_expanded_nodes))
            } else {
                remaining_nodes
            };
            if spec.bounded_refinement {
                member_node_budget =
                    member_node_budget.min(total_expanded / Self::BOUNDED_REFINEMENT_WORK_DIVISOR);
            }
            if member_node_budget == 0 {
                members.push(BeamWidthPortfolioMember {
                    spec,
                    node_budget: 0,
                    ran: false,
                    expanded_nodes: 0,
                    transition_count: 0,
                    termination: None,
                    terminal: None,
                    won: None,
                    battle_hp_lost: None,
                    potion_count: None,
                    compared: false,
                    skipped_reason: Some(Self::SKIPPED_BUDGET_EXHAUSTED),
                });
                continue;
            }

            let mut member_profile = base_profile.clone();
            member_profile.beam_width = spec.beam_width;
            member_profile.second_rank_band = spec.second_rank_band;
            member_profile.base_score_only = spec.base_score_only;
            member_profile.aggressive_power_commitment = spec.aggressive_power_commitment;
            member_profile.beam_weight_perturbation = if spec.offensive_refinement {
                Some(BeamWeightPerturbation {
                    term: BeamWeightTerm::EnemyHp,
                    scale: 1.5,
                })
            } else {
                base_profile.beam_weight_perturbation
            };
            member_profile.max_expanded_nodes = member_node_budget;

            let run = solve(&member_profile);
            remaining_nodes = remaining_nodes.saturating_sub(run.expanded_nodes);
            total_expanded += run.expanded_nodes;
            total_transitions += run.transition_count;

            if run.stop_portfolio {
                members.push(BeamWidthPortfolioMember {
                    spec,
                    node_budget: member_profile.max_expanded_nodes,
                    ran: true,
                    expanded_nodes: run.expanded_nodes,
                    transition_count: run.transition_count,
                    termination: Some(run.termination),
                    terminal: Some(run.terminal),
                    won: Some(run.won),
                    battle_hp_lost: run.battle_hp_lost,
                    potion_count: Some(run.potion_count),
                    compared: true,
                    skipped_reason: None,
                });
                selected = Some(run.result);
                selected_index = Some(index);
                stopped = true;
                break;
            }

            let comparable = if spec.aggressive_power_commitment {
                run.terminal
            } else {
                run.terminal
                    || (!run.memory_truncated && run.termination != Self::NODE_LIMIT_TERMINATION)
            };

            let skipped_reason = if comparable {
                None
            } else if run.memory_truncated {
                Some(Self::SKIPPED_MEMORY_TRUNCATED)
            } else if spec.aggressive_power_commitment {
                Some(Self::SKIPPED_POWER_MEMBER_NOT_TERMINAL)
            } else {
                Some(Self::SKIPPED_NODE_LIMIT_NOT_TERMINAL)
            };

            members.push(BeamWidthPortfolioMember {
                spec,
                node_budget: member_profile.max_expanded_nodes,
                ran: true,
                expanded_nodes: run.expanded_nodes,
                transition_count: run.transition_count,
                termination: Some(run.termination),
                terminal: Some(run.terminal),
                won: Some(run.won),
                battle_hp_lost: run.battle_hp_lost,
                potion_count: Some(run.potion_count),
                compared: comparable,
                skipped_reason,
            });

            let result = run.result;
            if comparable {
                let take_new = match &selected {
                    None => true,
                    Some(cur) => is_better(&result, cur),
                };
                if take_new {
                    selected = Some(result);
                    selected_index = Some(index);
                } else if first_ran.is_none() {
                    first_ran = Some(result);
                    first_ran_index = Some(index);
                }
            } else if first_ran.is_none() {
                first_ran = Some(result);
                first_ran_index = Some(index);
            }
        }

        let (final_selected, final_idx, reason) = if stopped {
            (selected.unwrap(), selected_index.unwrap(), Self::SELECTION_STOPPED)
        } else if let (Some(sel), Some(idx)) = (selected, selected_index) {
            (sel, idx, Self::SELECTION_BEST)
        } else {
            (
                first_ran.expect("portfolio requires at least one ran member"),
                first_ran_index.unwrap(),
                Self::SELECTION_BASELINE_FALLBACK,
            )
        };

        BeamWidthPortfolioOutcome {
            selected: final_selected,
            selected_index: final_idx,
            selection_reason: reason,
            members,
            total_expanded_nodes: total_expanded,
            total_transition_count: total_transitions,
        }
    }

    #[allow(clippy::too_many_arguments)]
    pub fn run_with_mode<T, Solve, IsBetter, Reject>(
        member_specs: &[BeamWidthPortfolioMemberSpec],
        shared_max_expanded_nodes: usize,
        base_profile: &SolverSearchProfile,
        scheduling_mode: PortfolioSchedulingMode,
        max_degree_of_parallelism: usize,
        solve: Solve,
        mut is_better: IsBetter,
        mut reject_member: Reject,
    ) -> BeamWidthPortfolioOutcome<T>
    where
        T: Send,
        Solve: Fn(&SolverSearchProfile) -> BeamWidthPortfolioRun<T> + Sync,
        IsBetter: FnMut(&T, &T) -> bool,
        Reject: FnMut(&BeamWidthPortfolioMemberSpec) -> Option<&'static str>,
    {
        if scheduling_mode == PortfolioSchedulingMode::Sequential
            || max_degree_of_parallelism <= 1
            || member_specs.len() <= 2
        {
            return Self::run(
                member_specs,
                shared_max_expanded_nodes,
                base_profile,
                solve,
                is_better,
                reject_member,
            );
        }

        // 1. Run baseline member (index 0) sequentially to establish gate & incumbent.
        let mut members = Vec::with_capacity(member_specs.len());
        let mut remaining_nodes = shared_max_expanded_nodes;
        let mut total_expanded = 0usize;
        let mut total_transitions = 0u64;
        let mut selected: Option<T> = None;
        let mut selected_index: Option<usize> = None;
        let mut first_ran: Option<T> = None;
        let mut first_ran_index: Option<usize> = None;

        let base_spec = member_specs[0];
        let base_budget = if base_spec.aggressive_power_commitment {
            remaining_nodes.max(Self::dedicated_power_node_reserve(shared_max_expanded_nodes))
        } else {
            remaining_nodes
        };
        let mut first_profile = base_profile.clone();
        first_profile.beam_width = base_spec.beam_width;
        first_profile.second_rank_band = base_spec.second_rank_band;
        first_profile.base_score_only = base_spec.base_score_only;
        first_profile.aggressive_power_commitment = base_spec.aggressive_power_commitment;
        first_profile.beam_weight_perturbation = if base_spec.offensive_refinement {
            Some(BeamWeightPerturbation {
                term: BeamWeightTerm::EnemyHp,
                scale: 1.5,
            })
        } else {
            base_profile.beam_weight_perturbation
        };
        first_profile.max_expanded_nodes = base_budget;

        let first_run = solve(&first_profile);
        remaining_nodes = remaining_nodes.saturating_sub(first_run.expanded_nodes);
        total_expanded += first_run.expanded_nodes;
        total_transitions += first_run.transition_count;

        let first_comparable = if base_spec.aggressive_power_commitment {
            first_run.terminal
        } else {
            first_run.terminal
                || (!first_run.memory_truncated
                    && first_run.termination != Self::NODE_LIMIT_TERMINATION)
        };
        members.push(BeamWidthPortfolioMember {
            spec: base_spec,
            node_budget: first_profile.max_expanded_nodes,
            ran: true,
            expanded_nodes: first_run.expanded_nodes,
            transition_count: first_run.transition_count,
            termination: Some(first_run.termination),
            terminal: Some(first_run.terminal),
            won: Some(first_run.won),
            battle_hp_lost: first_run.battle_hp_lost,
            potion_count: Some(first_run.potion_count),
            compared: first_run.stop_portfolio || first_comparable,
            skipped_reason: if first_run.stop_portfolio || first_comparable {
                None
            } else if first_run.memory_truncated {
                Some(Self::SKIPPED_MEMORY_TRUNCATED)
            } else {
                Some(Self::SKIPPED_NODE_LIMIT_NOT_TERMINAL)
            },
        });

        let stop_early = first_run.stop_portfolio;
        if stop_early {
            selected = Some(first_run.result);
            selected_index = Some(0);
            return BeamWidthPortfolioOutcome {
                selected: selected.unwrap(),
                selected_index: selected_index.unwrap(),
                selection_reason: Self::SELECTION_STOPPED,
                members,
                total_expanded_nodes: total_expanded,
                total_transition_count: total_transitions,
            };
        }
        if first_comparable {
            selected = Some(first_run.result);
            selected_index = Some(0);
        } else {
            first_ran = Some(first_run.result);
            first_ran_index = Some(0);
        }

        // 2. Partition remaining members via reject_member and run eligible ones concurrently.
        let mut pending: Vec<(usize, BeamWidthPortfolioMemberSpec, usize)> = Vec::new();
        let mut skipped_map: Vec<(usize, BeamWidthPortfolioMember)> = Vec::new();
        for (offset, &spec) in member_specs.iter().enumerate().skip(1) {
            if let Some(rejection) = reject_member(&spec) {
                skipped_map.push((
                    offset,
                    BeamWidthPortfolioMember {
                        spec,
                        node_budget: 0,
                        ran: false,
                        expanded_nodes: 0,
                        transition_count: 0,
                        termination: None,
                        terminal: None,
                        won: None,
                        battle_hp_lost: None,
                        potion_count: None,
                        compared: false,
                        skipped_reason: Some(rejection),
                    },
                ));
                continue;
            }
            let mut budget = if spec.aggressive_power_commitment {
                remaining_nodes.max(Self::dedicated_power_node_reserve(shared_max_expanded_nodes))
            } else {
                remaining_nodes
            };
            if spec.bounded_refinement {
                budget = budget.min(total_expanded / Self::BOUNDED_REFINEMENT_WORK_DIVISOR);
            }
            if budget == 0 {
                skipped_map.push((
                    offset,
                    BeamWidthPortfolioMember {
                        spec,
                        node_budget: 0,
                        ran: false,
                        expanded_nodes: 0,
                        transition_count: 0,
                        termination: None,
                        terminal: None,
                        won: None,
                        battle_hp_lost: None,
                        potion_count: None,
                        compared: false,
                        skipped_reason: Some(Self::SKIPPED_BUDGET_EXHAUSTED),
                    },
                ));
                continue;
            }
            pending.push((offset, spec, budget));
        }

        let eligible_count = pending.len();
        let mut parallel_results: Vec<(usize, BeamWidthPortfolioMemberSpec, usize, BeamWidthPortfolioRun<T>)> =
            Vec::with_capacity(eligible_count);
        if eligible_count > 0 {
            let shared_split = (remaining_nodes / eligible_count.max(1)).max(1);
            let slots: Vec<parking_lot::Mutex<Option<BeamWidthPortfolioRun<T>>>> = (0..eligible_count)
                .map(|_| parking_lot::Mutex::new(None))
                .collect();
            let pool = rayon::ThreadPoolBuilder::new()
                .num_threads(eligible_count.min(max_degree_of_parallelism.max(1)))
                .build()
                .ok();
            let exec_all = || {
                rayon::scope(|s| {
                    for (slot_idx, &(_, spec, raw_budget)) in pending.iter().enumerate() {
                        let slot_ref = &slots[slot_idx];
                        let solve_ref = &solve;
                        let budget = if spec.aggressive_power_commitment || spec.bounded_refinement {
                            raw_budget
                        } else {
                            raw_budget.min(shared_split)
                        };
                        s.spawn(move |_| {
                            let mut mp = base_profile.clone();
                            mp.beam_width = spec.beam_width;
                            mp.second_rank_band = spec.second_rank_band;
                            mp.base_score_only = spec.base_score_only;
                            mp.aggressive_power_commitment = spec.aggressive_power_commitment;
                            mp.beam_weight_perturbation = if spec.offensive_refinement {
                                Some(BeamWeightPerturbation {
                                    term: BeamWeightTerm::EnemyHp,
                                    scale: 1.5,
                                })
                            } else {
                                base_profile.beam_weight_perturbation
                            };
                            mp.max_expanded_nodes = budget;
                            let run = solve_ref(&mp);
                            *slot_ref.lock() = Some(run);
                        });
                    }
                });
            };
            if let Some(p) = pool {
                p.install(exec_all);
            } else {
                exec_all();
            }
            for ((offset, spec, raw_budget), slot) in pending.into_iter().zip(slots.into_iter()) {
                if let Some(run) = slot.into_inner() {
                    let budget = if spec.aggressive_power_commitment || spec.bounded_refinement {
                        raw_budget
                    } else {
                        raw_budget.min(shared_split)
                    };
                    parallel_results.push((offset, spec, budget, run));
                }
            }
        }

        // Merge members and select deterministically in member_specs index order.
        let mut all_entries: Vec<(usize, BeamWidthPortfolioMember, Option<BeamWidthPortfolioRun<T>>)> =
            Vec::with_capacity(member_specs.len());
        for (idx, m) in skipped_map {
            all_entries.push((idx, m, None));
        }
        for (idx, spec, budget, run) in parallel_results {
            total_expanded += run.expanded_nodes;
            total_transitions += run.transition_count;
            let comparable = if spec.aggressive_power_commitment {
                run.terminal
            } else {
                run.terminal
                    || (!run.memory_truncated && run.termination != Self::NODE_LIMIT_TERMINATION)
            };
            let skipped_reason = if comparable || run.stop_portfolio {
                None
            } else if run.memory_truncated {
                Some(Self::SKIPPED_MEMORY_TRUNCATED)
            } else if spec.aggressive_power_commitment {
                Some(Self::SKIPPED_POWER_MEMBER_NOT_TERMINAL)
            } else {
                Some(Self::SKIPPED_NODE_LIMIT_NOT_TERMINAL)
            };
            let member = BeamWidthPortfolioMember {
                spec,
                node_budget: budget,
                ran: true,
                expanded_nodes: run.expanded_nodes,
                transition_count: run.transition_count,
                termination: Some(run.termination),
                terminal: Some(run.terminal),
                won: Some(run.won),
                battle_hp_lost: run.battle_hp_lost,
                potion_count: Some(run.potion_count),
                compared: comparable || run.stop_portfolio,
                skipped_reason,
            };
            all_entries.push((idx, member, Some(run)));
        }
        all_entries.sort_by_key(|(idx, _, _)| *idx);

        let mut stopped = false;
        for (idx, member, maybe_run) in all_entries {
            let comparable = member.compared;
            members.push(member);
            if let Some(run) = maybe_run {
                if run.stop_portfolio {
                    selected = Some(run.result);
                    selected_index = Some(idx);
                    stopped = true;
                    break;
                }
                if comparable {
                    let take = match &selected {
                        None => true,
                        Some(cur) => is_better(&run.result, cur),
                    };
                    if take {
                        selected = Some(run.result);
                        selected_index = Some(idx);
                    } else if first_ran.is_none() {
                        first_ran = Some(run.result);
                        first_ran_index = Some(idx);
                    }
                } else if first_ran.is_none() {
                    first_ran = Some(run.result);
                    first_ran_index = Some(idx);
                }
            }
        }

        let (final_selected, final_idx, reason) = if stopped {
            (selected.unwrap(), selected_index.unwrap(), Self::SELECTION_STOPPED)
        } else if let (Some(sel), Some(idx)) = (selected, selected_index) {
            (sel, idx, Self::SELECTION_BEST)
        } else {
            (
                first_ran.expect("portfolio requires at least one ran member"),
                first_ran_index.unwrap(),
                Self::SELECTION_BASELINE_FALLBACK,
            )
        };

        BeamWidthPortfolioOutcome {
            selected: final_selected,
            selected_index: final_idx,
            selection_reason: reason,
            members,
            total_expanded_nodes: total_expanded,
            total_transition_count: total_transitions,
        }
    }
}
