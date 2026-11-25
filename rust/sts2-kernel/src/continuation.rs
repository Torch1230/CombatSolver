use crate::content_dispatch::HookListener;
use crate::state::{CardHandle, CreatureHandle};
use serde::{Deserialize, Serialize};
use smallvec::SmallVec;
use std::borrow::Cow;
use sts2_types::{CardPilePosition, PileType, PotionId, PowerId};

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub enum ChoiceTiming {
    Action,
    PlayerTurnEnd,
    EnemyTurn,
    PlayerTurnStart,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ChoiceRequest {
    pub timing: ChoiceTiming,
    pub source_id: Cow<'static, str>,
    pub source_pile: PileType,
    pub min_count: usize,
    pub max_count: usize,
    pub candidates: SmallVec<[CardHandle; 16]>,
    pub can_skip: bool,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CardPlayResources {
    pub energy_spent: i32,
    pub energy_value: i32,
    pub stars_spent: i32,
    pub star_value: i32,
    pub captured_energy_x: Option<i32>,
    pub captured_star_x: Option<i32>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CardSelectActionKind {
    DiscardSelected,
    ExhaustSelected,
    MoveSelectedToPile {
        pile: PileType,
        position: CardPilePosition,
    },
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ContinuationFrame {
    CardPlayResume {
        card: CardHandle,
        original_owner_creature: CreatureHandle,
        target: Option<CreatureHandle>,
        play_index: i32,
        play_count: i32,
        is_auto_play: bool,
        result_pile: PileType,
        result_position: CardPilePosition,
        resources: CardPlayResources,
        on_play_resume_step: u8,
    },
    PotionUseResume {
        potion_slot: usize,
        potion_id: PotionId,
        target: Option<CreatureHandle>,
        resume_step: u8,
    },
    BeforeHandDrawResume {
        paused_listener: HookListener,
        remaining_listeners: Vec<HookListener>,
        is_extra_turn: bool,
    },
    AfterShuffleResume {
        paused_listener: HookListener,
        remaining_listeners: Vec<HookListener>,
        remaining_draws: i32,
        from_hand_draw: bool,
        resume_turn_start: Option<bool>,
    },
    AfterPlayerTurnStartResume {
        paused_listener: HookListener,
        remaining_listeners: Vec<HookListener>,
        is_extra_turn: bool,
    },
    CardSelectResume {
        source_card: CardHandle,
        action_kind: CardSelectActionKind,
    },
    AutoPlayResume {
        remaining_cards: SmallVec<[CardHandle; 8]>,
        fixed_target: Option<CreatureHandle>,
        spend_resources: bool,
        force_exhaust: Option<bool>,
        move_to_play: bool,
    },
    EndTurnResume {
        stage: u8,
    },
    DeathRevivalResume {
        target: CreatureHandle,
        recursion: u32,
    },
    EnemyTurnResume {
        current_enemy: CreatureHandle,
        remaining_enemies: SmallVec<[CreatureHandle; 8]>,
    },
    AfterAutoPrePlayResume {
        remaining_listeners: Vec<HookListener>,
        stage: u8,
    },
    AfterAutoPostPlayResume {
        remaining_listeners: Vec<HookListener>,
    },
    AfterAutoPostPlayPowerResume {
        creature: CreatureHandle,
        power_id: PowerId,
        remaining_iterations: i32,
    },
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PendingContinuation {
    pub request: ChoiceRequest,
    pub frames: SmallVec<[ContinuationFrame; 2]>,
}

const _: () = assert!(std::mem::size_of::<PendingContinuation>() <= 400);

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum StepOutcome {
    Completed,
    NeedsChoice(Box<ChoiceRequest>),
    CombatEnded { won: bool },
}

const _: () = assert!(std::mem::size_of::<StepOutcome>() <= 16);
