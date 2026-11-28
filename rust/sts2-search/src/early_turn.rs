use crate::arena::{CompactActionKind, NodeArena, NodeId};
use crate::eval::PowerRoutePriority;
use crate::hash::StateHash;
use crate::pruning::PrimarySearchIncumbent;
use crate::PlannedAction;
use hashbrown::HashMap;
use sts2_types::PotionId;

pub const EARLY_TURN_CANDIDATES_PER_LAYER: usize = 24;
pub const EARLY_TURN_EXPLORATION_NODE_BUDGET: usize = 1_000_000;
pub const EARLY_TURN_EXPLORATION_INITIAL_RANK_LIMIT: usize = 4;
pub const EARLY_TURN_EXPLORATION_MAX_RANK_LIMIT: usize = 6;

#[derive(Clone, Debug)]
pub struct EarlyTurnFrontierCandidate {
    pub completed_turns: i32,
    pub actions: Vec<PlannedAction>,
    pub state_hash: StateHash,
    pub score: f64,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash)]
struct EarlyTurnOpeningKey {
    kind: u8,
    card_handle: i32,
    potion_id: Option<PotionId>,
    target_handle: Option<i32>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash)]
struct EarlyTurnStrategyKey {
    opening: EarlyTurnOpeningKey,
    potion_count: usize,
    persistent_buff_band: i32,
    hand_value_band: i32,
    power_priority: PowerRoutePriority,
}

fn first_action_of(arena: &NodeArena, mut id: NodeId) -> Option<CompactActionKind> {
    let mut first = None;
    loop {
        let n = arena.get(id);
        if let Some(k) = n.action.kind() {
            first = Some(k);
        }
        match n.parent_id() {
            Some(p) => id = p,
            None => return first,
        }
    }
}

/// 1:1 port of `CombatBeamSolver.EarlyTurnFrontier.cs`.
pub fn select_early_turn_frontier(
    arena: &NodeArena,
    ended: &[NodeId],
    start_turn_number: i32,
    completed_turns: i32,
) -> Vec<EarlyTurnFrontierCandidate> {
    let mut states: HashMap<StateHash, NodeId, ahash::RandomState> =
        HashMap::with_hasher(ahash::RandomState::with_seeds(31, 32, 33, 34));
    for &nid in ended {
        let node = arena.get(nid);
        if node.is_terminal()
            || node.fast_eval.player_dead()
            || node.boundary_rank != 0
            || (node.turn as i32) != start_turn_number + completed_turns
        {
            continue;
        }
        match states.get_mut(&node.state_hash) {
            Some(prior) => {
                if node.beam_rank_score_or_base() > arena.get(*prior).beam_rank_score_or_base() {
                    *prior = nid;
                }
            }
            None => {
                states.insert(node.state_hash, nid);
            }
        }
    }

    struct Entry {
        node_id: NodeId,
        opening: EarlyTurnOpeningKey,
        strategy: EarlyTurnStrategyKey,
        score: f64,
    }

    let mut by_strategy: HashMap<EarlyTurnStrategyKey, Vec<Entry>, ahash::RandomState> =
        HashMap::with_hasher(ahash::RandomState::with_seeds(35, 36, 37, 38));

    for &nid in states.values() {
        let Some(first) = first_action_of(arena, nid) else {
            continue;
        };
        let opening = match first {
            CompactActionKind::PlayCard {
                card_handle,
                target_handle,
            } => EarlyTurnOpeningKey {
                kind: 0,
                card_handle: card_handle as i32,
                potion_id: None,
                target_handle: target_handle.map(|h| h as i32),
            },
            CompactActionKind::UsePotion {
                potion_id,
                target_handle,
                ..
            } => EarlyTurnOpeningKey {
                kind: 1,
                card_handle: 0,
                potion_id: Some(potion_id),
                target_handle: target_handle.map(|h| h as i32),
            },
            CompactActionKind::TurnSetup => EarlyTurnOpeningKey {
                kind: 3,
                card_handle: 0,
                potion_id: None,
                target_handle: None,
            },
            CompactActionKind::EndTurn => EarlyTurnOpeningKey {
                kind: 2,
                card_handle: 0,
                potion_id: None,
                target_handle: None,
            },
        };
        let node = arena.get(nid);
        let strategy = EarlyTurnStrategyKey {
            opening,
            potion_count: (node.potion_count as usize).min(3),
            persistent_buff_band: (node.persistent_buff_value as i32) / 8,
            hand_value_band: (node.reachable_hand_value as i32) / 8,
            power_priority: node.best_power_priority,
        };
        by_strategy.entry(strategy).or_default().push(Entry {
            node_id: nid,
            opening,
            strategy,
            score: node.beam_rank_score_or_base(),
        });
    }

    let mut by_opening: HashMap<EarlyTurnOpeningKey, Vec<Entry>, ahash::RandomState> =
        HashMap::with_hasher(ahash::RandomState::with_seeds(39, 40, 41, 42));
    for (_, mut group) in by_strategy {
        group.sort_by(|a, b| b.score.total_cmp(&a.score));
        for e in group.into_iter().take(2) {
            let _ = e.strategy;
            by_opening.entry(e.opening).or_default().push(e);
        }
    }

    let mut openings: Vec<Vec<Entry>> = by_opening
        .into_values()
        .map(|mut g| {
            g.sort_by(|a, b| b.score.total_cmp(&a.score));
            g
        })
        .collect();
    openings.sort_by(|a, b| b[0].score.total_cmp(&a[0].score));

    let mut selected = Vec::new();
    let mut rank = 0usize;
    while selected.len() < EARLY_TURN_CANDIDATES_PER_LAYER {
        let mut found = false;
        for group in &openings {
            if rank >= group.len() {
                continue;
            }
            found = true;
            let nid = group[rank].node_id;
            let node = arena.get(nid);
            selected.push(EarlyTurnFrontierCandidate {
                completed_turns,
                actions: arena.reconstruct_actions(nid),
                state_hash: node.state_hash,
                score: node.beam_rank_score_or_base(),
            });
            if selected.len() == EARLY_TURN_CANDIDATES_PER_LAYER {
                break;
            }
        }
        if !found {
            break;
        }
        rank += 1;
    }
    selected
}

/// Interleaves potion-using and potion-free early-turn prefixes (`CombatSearchCoordinator.EarlyTurnExploration.cs`).
pub fn interleave_potion_frontiers(
    candidates: Vec<EarlyTurnFrontierCandidate>,
) -> Vec<EarlyTurnFrontierCandidate> {
    let mut with_potion = Vec::new();
    let mut without_potion = Vec::new();
    for c in candidates {
        if c.actions
            .iter()
            .any(|a| matches!(a, PlannedAction::UsePotion { .. }))
        {
            with_potion.push(c);
        } else {
            without_potion.push(c);
        }
    }
    let max_len = with_potion.len().max(without_potion.len());
    let mut ordered = Vec::with_capacity(with_potion.len() + without_potion.len());
    let mut wp = with_potion.into_iter();
    let mut wop = without_potion.into_iter();
    for _ in 0..max_len {
        if let Some(a) = wp.next() {
            ordered.push(a);
        }
        if let Some(b) = wop.next() {
            ordered.push(b);
        }
    }
    ordered
}

pub fn select_early_turn_continuation_incumbent(
    incumbent: Option<PrimarySearchIncumbent>,
    preserve_resources: bool,
    selected_has_explicit_potions: bool,
    prefix_uses_potion: bool,
) -> Option<PrimarySearchIncumbent> {
    let inc = incumbent?;
    if preserve_resources || (selected_has_explicit_potions && !prefix_uses_potion) {
        return None;
    }
    Some(PrimarySearchIncumbent {
        combat_ended_turn: i32::MAX,
        ..inc
    })
}
