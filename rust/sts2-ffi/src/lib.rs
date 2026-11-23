pub mod host_wire;

use host_wire::{HostProgressDto, solve_in_combat_host_v1_json};
use serde::{Deserialize, Serialize};
use smallvec::SmallVec;
use std::ffi::c_void;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::slice;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use sts2_content::Sts2Content;
use sts2_kernel::hooks;
use sts2_kernel::snapshot::{
    LiveStateJson, OracleRngStreamsDto, export_live_state, export_rng_streams, import_live_state,
};
use sts2_kernel::{
    BattleState, CardHandle, ChoiceRequest, CombatEngine, CreatureHandle, KernelError, StepOutcome,
};
use sts2_search::{InCombatSolveRequest, InCombatSolveResponse, SearchEngine, SearchProgress};
use sts2_types::Dec;

pub const ABI_VERSION: u32 = 1;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CapabilitiesJson {
    pub abi_version: u32,
    pub engine_id: &'static str,
    pub supported_game_version: &'static str,
    pub starter_characters: Vec<&'static str>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "kind")]
pub enum OracleActionJson {
    StartCombat,
    #[serde(rename_all = "camelCase")]
    PlayCard {
        card_handle: i32,
        #[serde(default)]
        target_handle: Option<i32>,
        #[serde(default)]
        choices: Option<Vec<Vec<usize>>>,
    },
    #[serde(rename_all = "camelCase")]
    UsePotion {
        potion_slot: usize,
        #[serde(default)]
        target_handle: Option<i32>,
        #[serde(default)]
        choices: Option<Vec<Vec<usize>>>,
    },
    EndTurn {
        #[serde(default)]
        choices: Option<Vec<Vec<usize>>>,
    },
    #[serde(rename_all = "camelCase")]
    ResumeChoice {
        selected_indices: Vec<usize>,
    },
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OracleStepRequest {
    pub state: LiveStateJson,
    pub actions: Vec<OracleActionJson>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OracleStepResponse {
    pub abi_version: u32,
    pub outcome: String,
    pub won: Option<bool>,
    pub state: Option<LiveStateJson>,
    pub rng_streams: Option<OracleRngStreamsDto>,
    pub pending_choice: Option<ChoiceRequest>,
    pub error_code: Option<String>,
    pub error: Option<String>,
}

fn error_response(code: &str, msg: String) -> OracleStepResponse {
    OracleStepResponse {
        abi_version: ABI_VERSION,
        outcome: if code == "Unsupported" {
            "Unsupported".to_owned()
        } else {
            "Error".to_owned()
        },
        won: None,
        state: None,
        rng_streams: None,
        pending_choice: None,
        error_code: Some(code.to_owned()),
        error: Some(msg),
    }
}

fn run_action_with_choices<F>(
    state: &mut BattleState,
    content: &Sts2Content,
    choices: Option<Vec<Vec<usize>>>,
    run_initial: F,
) -> Result<StepOutcome, KernelError>
where
    F: FnOnce(&mut BattleState, &mut SmallVec<[Vec<usize>; 2]>) -> Result<StepOutcome, KernelError>,
{
    let mut q: SmallVec<[Vec<usize>; 2]> = choices.unwrap_or_default().into_iter().collect();
    let mut outcome = run_initial(state, &mut q)?;
    while matches!(outcome, StepOutcome::NeedsChoice(_)) && !q.is_empty() {
        let sel = q.remove(0);
        outcome = CombatEngine::resume_choice(state, content, &sel)?;
    }
    Ok(outcome)
}

pub fn step_oracle_json(req_json: &[u8]) -> OracleStepResponse {
    let req: OracleStepRequest = match serde_json::from_slice(req_json) {
        Ok(r) => r,
        Err(e) => return error_response("InvalidRequestJson", e.to_string()),
    };

    let content = Sts2Content;
    let mut state: BattleState = match import_live_state(&req.state, &content) {
        Ok(s) => s,
        Err(KernelError::Unsupported { kind, id }) => {
            return error_response("Unsupported", format!("unsupported {kind}: {id}"));
        }
        Err(e) => return error_response("SnapshotImport", e.to_string()),
    };

    let mut last_outcome = StepOutcome::Completed;
    for action in req.actions {
        let step_res = match action {
            OracleActionJson::StartCombat => CombatEngine::start_combat(&mut state, &content),
            OracleActionJson::PlayCard {
                card_handle,
                target_handle,
                choices,
            } => run_action_with_choices(&mut state, &content, choices, |s, q| {
                CombatEngine::play_card(
                    s,
                    &content,
                    CardHandle(card_handle as u32),
                    target_handle.map(|h| CreatureHandle(h as u32)),
                    q,
                )
            }),
            OracleActionJson::UsePotion {
                potion_slot,
                target_handle,
                choices,
            } => run_action_with_choices(&mut state, &content, choices, |s, q| {
                CombatEngine::use_potion(
                    s,
                    &content,
                    potion_slot,
                    target_handle.map(|h| CreatureHandle(h as u32)),
                    q,
                )
            }),
            OracleActionJson::EndTurn { choices } => {
                run_action_with_choices(&mut state, &content, choices, |s, _q| {
                    CombatEngine::end_player_turn(s, &content)
                })
            }
            OracleActionJson::ResumeChoice { selected_indices } => {
                CombatEngine::resume_choice(&mut state, &content, &selected_indices)
            }
        };

        match step_res {
            Ok(outcome) => {
                last_outcome = outcome;
                if matches!(
                    last_outcome,
                    StepOutcome::NeedsChoice(_) | StepOutcome::CombatEnded { .. }
                ) {
                    break;
                }
            }
            Err(KernelError::Unsupported { kind, id }) => {
                return error_response("Unsupported", format!("unsupported {kind}: {id}"));
            }
            Err(e) => return error_response("ExecutionError", e.to_string()),
        }
    }

    let mut exported = match export_live_state(&state) {
        Ok(s) => s,
        Err(e) => return error_response("ExportError", e.to_string()),
    };
    let base_max = Dec::from_i32(state.player.max_energy);
    if let Ok(eff) = hooks::modify_max_energy(&mut state, &content, base_max) {
        if let Ok(eff_i32) = eff.to_i32_trunc() {
            exported.player.effective_max_energy = Some(eff_i32);
        }
    }
    let rng_streams = export_rng_streams(&state);

    match last_outcome {
        StepOutcome::Completed => OracleStepResponse {
            abi_version: ABI_VERSION,
            outcome: "Ok".to_owned(),
            won: None,
            state: Some(exported),
            rng_streams: Some(rng_streams),
            pending_choice: None,
            error_code: None,
            error: None,
        },
        StepOutcome::NeedsChoice(req) => OracleStepResponse {
            abi_version: ABI_VERSION,
            outcome: "NeedsChoice".to_owned(),
            won: None,
            state: Some(exported),
            rng_streams: Some(rng_streams),
            pending_choice: Some(req),
            error_code: None,
            error: None,
        },
        StepOutcome::CombatEnded { won } => OracleStepResponse {
            abi_version: ABI_VERSION,
            outcome: "CombatEnded".to_owned(),
            won: Some(won),
            state: Some(exported),
            rng_streams: Some(rng_streams),
            pending_choice: None,
            error_code: None,
            error: None,
        },
    }
}

fn write_owned_bytes(bytes: Vec<u8>, out_ptr: *mut *mut u8, out_len: *mut usize) -> i32 {
    if out_ptr.is_null() || out_len.is_null() {
        return -1;
    }
    let mut boxed = bytes.into_boxed_slice();
    let len = boxed.len();
    let ptr = boxed.as_mut_ptr();
    std::mem::forget(boxed);
    unsafe {
        *out_ptr = ptr;
        *out_len = len;
    }
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn sts2_rs_abi_version() -> u32 {
    ABI_VERSION
}

#[unsafe(no_mangle)]
pub extern "C" fn sts2_rs_capabilities(out_ptr: *mut *mut u8, out_len: *mut usize) -> i32 {
    let res = catch_unwind(AssertUnwindSafe(|| {
        let caps = CapabilitiesJson {
            abi_version: ABI_VERSION,
            engine_id: "sts2-combat-rs",
            supported_game_version: "v0.113.1",
            starter_characters: vec![
                "IRONCLAD",
                "SILENT",
                "DEFECT",
                "REGENT",
                "NECROBINDER",
                "FOOL",
            ],
        };
        let bytes = serde_json::to_vec(&caps).unwrap_or_default();
        write_owned_bytes(bytes, out_ptr, out_len)
    }));
    res.unwrap_or(-2)
}

#[unsafe(no_mangle)]
pub extern "C" fn sts2_rs_step_oracle(
    req_ptr: *const u8,
    req_len: usize,
    out_ptr: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    if req_ptr.is_null() || out_ptr.is_null() || out_len.is_null() {
        return -1;
    }
    let res = catch_unwind(AssertUnwindSafe(|| {
        let req_slice = unsafe { slice::from_raw_parts(req_ptr, req_len) };
        let resp = step_oracle_json(req_slice);
        let bytes = serde_json::to_vec(&resp).unwrap_or_default();
        write_owned_bytes(bytes, out_ptr, out_len)
    }));
    res.unwrap_or(-2)
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SolveInCombatJsonResponse {
    pub abi_version: u32,
    pub outcome: String,
    pub solution: Option<InCombatSolveResponse>,
    pub error_code: Option<String>,
    pub error: Option<String>,
}

pub fn solve_in_combat_json(req_json: &[u8]) -> SolveInCombatJsonResponse {
    let req: InCombatSolveRequest = match serde_json::from_slice(req_json) {
        Ok(r) => r,
        Err(e) => {
            return SolveInCombatJsonResponse {
                abi_version: ABI_VERSION,
                outcome: "Error".to_owned(),
                solution: None,
                error_code: Some("InvalidRequestJson".to_owned()),
                error: Some(e.to_string()),
            };
        }
    };
    let content = Sts2Content;
    match SearchEngine.solve(&req, &content) {
        Ok(sol) => SolveInCombatJsonResponse {
            abi_version: ABI_VERSION,
            outcome: "Ok".to_owned(),
            solution: Some(sol),
            error_code: None,
            error: None,
        },
        Err(KernelError::Unsupported { kind, id }) => SolveInCombatJsonResponse {
            abi_version: ABI_VERSION,
            outcome: "Unsupported".to_owned(),
            solution: None,
            error_code: Some("Unsupported".to_owned()),
            error: Some(format!("unsupported {kind}: {id}")),
        },
        Err(e) => SolveInCombatJsonResponse {
            abi_version: ABI_VERSION,
            outcome: "Error".to_owned(),
            solution: None,
            error_code: Some("SearchError".to_owned()),
            error: Some(e.to_string()),
        },
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn sts2_rs_solve_in_combat(
    req_ptr: *const u8,
    req_len: usize,
    out_ptr: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    if req_ptr.is_null() || out_ptr.is_null() || out_len.is_null() {
        return -1;
    }
    let res = catch_unwind(AssertUnwindSafe(|| {
        let req_slice = unsafe { slice::from_raw_parts(req_ptr, req_len) };
        let resp = solve_in_combat_json(req_slice);
        let bytes = serde_json::to_vec(&resp).unwrap_or_default();
        write_owned_bytes(bytes, out_ptr, out_len)
    }));
    res.unwrap_or(-2)
}

#[unsafe(no_mangle)]
pub extern "C" fn sts2_rs_cancel_token_create() -> *mut c_void {
    let token = Arc::new(AtomicBool::new(false));
    Arc::into_raw(token) as *mut c_void
}

#[unsafe(no_mangle)]
pub extern "C" fn sts2_rs_cancel_token_cancel(token: *mut c_void) {
    if token.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        let flag = unsafe { &*(token as *const AtomicBool) };
        flag.store(true, Ordering::Release);
    }));
}

#[unsafe(no_mangle)]
pub extern "C" fn sts2_rs_cancel_token_free(token: *mut c_void) {
    if token.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| unsafe {
        drop(Arc::from_raw(token as *const AtomicBool));
    }));
}

pub fn solve_in_combat_with_controls_json(
    req_json: &[u8],
    cancel: &AtomicBool,
    progress_cb: Option<&dyn Fn(&SearchProgress)>,
) -> SolveInCombatJsonResponse {
    let req: InCombatSolveRequest = match serde_json::from_slice(req_json) {
        Ok(r) => r,
        Err(e) => {
            return SolveInCombatJsonResponse {
                abi_version: ABI_VERSION,
                outcome: "Error".to_owned(),
                solution: None,
                error_code: Some("InvalidRequestJson".to_owned()),
                error: Some(e.to_string()),
            };
        }
    };
    let Some(ref live_json) = req.root_state_json else {
        return SolveInCombatJsonResponse {
            abi_version: ABI_VERSION,
            outcome: "Unsupported".to_owned(),
            solution: None,
            error_code: Some("Unsupported".to_owned()),
            error: Some("unsupported SearchEngine: missing_root_state_json".to_owned()),
        };
    };
    let content = Sts2Content;
    let state = match import_live_state(live_json, &content) {
        Ok(s) => s,
        Err(KernelError::Unsupported { kind, id }) => {
            return SolveInCombatJsonResponse {
                abi_version: ABI_VERSION,
                outcome: "Unsupported".to_owned(),
                solution: None,
                error_code: Some("Unsupported".to_owned()),
                error: Some(format!("unsupported {kind}: {id}")),
            };
        }
        Err(e) => {
            return SolveInCombatJsonResponse {
                abi_version: ABI_VERSION,
                outcome: "Error".to_owned(),
                solution: None,
                error_code: Some("SnapshotImport".to_owned()),
                error: Some(e.to_string()),
            };
        }
    };
    match SearchEngine.solve_state_with_controls(&state, &req.policy, &content, cancel, progress_cb) {
        Ok(sol) => SolveInCombatJsonResponse {
            abi_version: ABI_VERSION,
            outcome: "Ok".to_owned(),
            solution: Some(sol),
            error_code: None,
            error: None,
        },
        Err(KernelError::Unsupported { kind, id }) => SolveInCombatJsonResponse {
            abi_version: ABI_VERSION,
            outcome: "Unsupported".to_owned(),
            solution: None,
            error_code: Some("Unsupported".to_owned()),
            error: Some(format!("unsupported {kind}: {id}")),
        },
        Err(e) => SolveInCombatJsonResponse {
            abi_version: ABI_VERSION,
            outcome: "Error".to_owned(),
            solution: None,
            error_code: Some("SearchError".to_owned()),
            error: Some(e.to_string()),
        },
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn sts2_rs_solve_in_combat_with_controls(
    req_ptr: *const u8,
    req_len: usize,
    cancel_token: *const c_void,
    progress_cb: Option<extern "C" fn(*const u8, usize, *mut c_void)>,
    user_data: *mut c_void,
    out_ptr: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    if req_ptr.is_null() || out_ptr.is_null() || out_len.is_null() {
        return -1;
    }
    let res = catch_unwind(AssertUnwindSafe(|| {
        let req_slice = unsafe { slice::from_raw_parts(req_ptr, req_len) };
        let cancel_holder: Arc<AtomicBool> = if cancel_token.is_null() {
            Arc::new(AtomicBool::new(false))
        } else {
            unsafe {
                let raw = cancel_token as *const AtomicBool;
                Arc::increment_strong_count(raw);
                Arc::from_raw(raw)
            }
        };
        let forwarder = |p: &SearchProgress| {
            if let Some(cb) = progress_cb {
                if let Ok(bytes) = serde_json::to_vec(p) {
                    cb(bytes.as_ptr(), bytes.len(), user_data);
                }
            }
        };
        let progress_ref: Option<&dyn Fn(&SearchProgress)> = progress_cb.map(|_| &forwarder as &dyn Fn(&SearchProgress));
        let resp = solve_in_combat_with_controls_json(req_slice, &cancel_holder, progress_ref);
        let bytes = serde_json::to_vec(&resp).unwrap_or_default();
        write_owned_bytes(bytes, out_ptr, out_len)
    }));
    res.unwrap_or(-2)
}

#[unsafe(no_mangle)]
pub extern "C" fn sts2_rs_solve_in_combat_host_v1(
    req_ptr: *const u8,
    req_len: usize,
    cancel_token: *const c_void,
    progress_cb: Option<extern "C" fn(*const u8, usize, *mut c_void)>,
    user_data: *mut c_void,
    out_ptr: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    if req_ptr.is_null() || out_ptr.is_null() || out_len.is_null() {
        return -1;
    }
    let res = catch_unwind(AssertUnwindSafe(|| {
        let req_slice = unsafe { slice::from_raw_parts(req_ptr, req_len) };
        let cancel_holder: Arc<AtomicBool> = if cancel_token.is_null() {
            Arc::new(AtomicBool::new(false))
        } else {
            unsafe {
                let raw = cancel_token as *const AtomicBool;
                Arc::increment_strong_count(raw);
                Arc::from_raw(raw)
            }
        };
        let forwarder = |p: &HostProgressDto| {
            if let Some(cb) = progress_cb {
                if let Ok(bytes) = serde_json::to_vec(p) {
                    cb(bytes.as_ptr(), bytes.len(), user_data);
                }
            }
        };
        let progress_ref: Option<&dyn Fn(&HostProgressDto)> =
            progress_cb.map(|_| &forwarder as &dyn Fn(&HostProgressDto));
        let resp = solve_in_combat_host_v1_json(req_slice, &cancel_holder, progress_ref);
        let bytes = serde_json::to_vec(&resp).unwrap_or_default();
        write_owned_bytes(bytes, out_ptr, out_len)
    }));
    match res {
        Ok(rc) => rc,
        Err(payload) => {
            let msg = payload
                .downcast_ref::<&str>()
                .copied()
                .or_else(|| payload.downcast_ref::<String>().map(|s| s.as_str()))
                .unwrap_or("unknown panic");
            let resp = host_wire::HostSolveResponse {
                abi_version: ABI_VERSION as i32,
                outcome: "Failed".to_owned(),
                failure_code: Some("rust_kernel_panic".to_owned()),
                field_path: None,
                error: Some(format!("Rust combat kernel panicked: {msg}")),
                result: None,
                best_so_far: None,
                effective_degree_of_parallelism: 1,
                total_elapsed_milliseconds: 0.0,
            };
            match serde_json::to_vec(&resp) {
                Ok(bytes) => write_owned_bytes(bytes, out_ptr, out_len),
                Err(_) => -2,
            }
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn sts2_rs_free_buffer(ptr: *mut u8, len: usize) {
    if ptr.is_null() {
        return;
    }
    unsafe {
        let slice = slice::from_raw_parts_mut(ptr, len);
        let _ = Box::from_raw(slice as *mut [u8]);
    }
}
