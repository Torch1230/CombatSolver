#!/usr/bin/env bash
set -Eeuo pipefail

usage() {
    cat <<'EOF'
Usage: tools/run-visible-steam-benchmark.sh [options]

Options:
  --timeout-seconds SECONDS
  --search-max-degree-of-parallelism 1..16
  --verify-base-lib-card-modifier-boundary
  --verify-baselib-card-modifier-boundary  Deprecated compatibility alias
  --logging-fixture  Run the short native logging/export fixture (at most 120 seconds)
  --evidence-directory DIRECTORY
  --checkpoint-archive-path ZIP --checkpoint-selector latest --replay-mode RestoreOnly
  --replay-policy-override-path JSON
  --steam-root DIRECTORY
  --steam-command FILE
  --game-root DIRECTORY
  --data-dir DIRECTORY
  -h, --help

Path defaults target a native Linux Steam install. Set COMBATSOLVER_STEAM_ROOT
or pass --steam-root when the Steam library is elsewhere.
EOF
}

die() {
    printf 'error: %s\n' "$*" >&2
    exit 1
}

require_option_value() {
    local option_name="$1"
    local option_value="${2-}"
    [[ -n "$option_value" ]] || die "$option_name requires a value"
}

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(realpath -e -- "$script_dir/..")"
timeout_seconds=360
timeout_explicit=false
search_max_degree_of_parallelism=2
verify_baselib_card_modifier_boundary=false
logging_fixture=false
evidence_directory=""
checkpoint_archive=""
replay_policy_override=""
checkpoint_selector="latest"
replay_mode="RestoreOnly"
steam_root_arg=""
steam_command_arg=""
game_root_arg=""
data_dir_arg=""

while (($# > 0)); do
    case "$1" in
        --checkpoint-archive-path) require_option_value "$1" "${2-}"; checkpoint_archive="$(realpath -e -- "$2")"; shift 2 ;;
        --checkpoint-selector) require_option_value "$1" "${2-}"; checkpoint_selector="$2"; shift 2 ;;
        --replay-mode) require_option_value "$1" "${2-}"; replay_mode="$2"; shift 2 ;;
        --replay-policy-override-path) require_option_value "$1" "${2-}"; replay_policy_override="$(realpath -e -- "$2")"; shift 2 ;;
        --logging-fixture) logging_fixture=true; shift ;;
        --evidence-directory)
            require_option_value "$1" "${2-}"
            evidence_directory="$(realpath -m -- "$2")"; shift 2 ;;
        --timeout-seconds)
            require_option_value "$1" "${2-}"
            timeout_seconds="$2"
            timeout_explicit=true
            shift 2
            ;;
        --timeout-seconds=*)
            timeout_seconds="${1#*=}"
            timeout_explicit=true
            shift
            ;;
        --search-max-degree-of-parallelism)
            require_option_value "$1" "${2-}"
            search_max_degree_of_parallelism="$2"
            shift 2
            ;;
        --search-max-degree-of-parallelism=*)
            search_max_degree_of_parallelism="${1#*=}"
            shift
            ;;
        --verify-base-lib-card-modifier-boundary|--verify-baselib-card-modifier-boundary)
            verify_baselib_card_modifier_boundary=true
            shift
            ;;
        --steam-root)
            require_option_value "$1" "${2-}"
            steam_root_arg="$2"
            shift 2
            ;;
        --steam-root=*)
            steam_root_arg="${1#*=}"
            shift
            ;;
        --steam-command)
            require_option_value "$1" "${2-}"
            steam_command_arg="$2"
            shift 2
            ;;
        --steam-command=*)
            steam_command_arg="${1#*=}"
            shift
            ;;
        --game-root)
            require_option_value "$1" "${2-}"
            game_root_arg="$2"
            shift 2
            ;;
        --game-root=*)
            game_root_arg="${1#*=}"
            shift
            ;;
        --data-dir)
            require_option_value "$1" "${2-}"
            data_dir_arg="$2"
            shift 2
            ;;
        --data-dir=*)
            data_dir_arg="${1#*=}"
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            die "unknown option: $1"
            ;;
    esac
done

[[ "$timeout_seconds" =~ ^[1-9][0-9]*$ ]] || die "--timeout-seconds must be a positive integer"
[[ "$search_max_degree_of_parallelism" =~ ^[0-9]+$ ]] \
    && ((search_max_degree_of_parallelism >= 1 && search_max_degree_of_parallelism <= 16)) || \
    die "--search-max-degree-of-parallelism must be between 1 and 16"
command -v jq >/dev/null 2>&1 || die "jq is required"
if [[ "$logging_fixture" == true ]] && ((timeout_seconds > 120)); then timeout_seconds=120; fi
if [[ -n "$checkpoint_archive" && "$timeout_explicit" == false ]]; then timeout_seconds=120; fi

if [[ -n "$steam_root_arg" ]]; then
    steam_root="$steam_root_arg"
elif [[ -n "${COMBATSOLVER_STEAM_ROOT:-}" ]]; then
    steam_root="$COMBATSOLVER_STEAM_ROOT"
elif [[ -d "${HOME}/.local/share/Steam" ]]; then
    steam_root="${HOME}/.local/share/Steam"
elif [[ -d "${HOME}/.steam/steam" ]]; then
    steam_root="${HOME}/.steam/steam"
else
    die "Steam root not found; pass --steam-root or set COMBATSOLVER_STEAM_ROOT"
fi
steam_root="$(realpath -e -- "$steam_root")"

if [[ -n "$steam_command_arg" ]]; then
    if [[ "$steam_command_arg" == */* ]]; then
        steam_command="$(realpath -e -- "$steam_command_arg")"
    else
        steam_command="$(command -v -- "$steam_command_arg" || true)"
    fi
elif command -v steam >/dev/null 2>&1; then
    steam_command="$(command -v steam)"
elif [[ -x "$steam_root/steam.sh" ]]; then
    steam_command="$steam_root/steam.sh"
else
    steam_command=""
fi
[[ -n "$steam_command" && -x "$steam_command" ]] || die "Steam command not found or not executable"

if [[ -n "$game_root_arg" ]]; then
    game_root="$game_root_arg"
else
    game_root="$steam_root/steamapps/common/Slay the Spire 2"
fi
[[ -d "$game_root" ]] || die "game root not found: $game_root"
game_root="$(realpath -e -- "$game_root")"
[[ -e "$game_root/SlayTheSpire2" ]] || die "native Linux game executable not found: $game_root/SlayTheSpire2"
game_executable="$(realpath -e -- "$game_root/SlayTheSpire2")"
[[ -x "$game_executable" ]] || die "native Linux game executable is not executable: $game_executable"

if [[ -n "$data_dir_arg" ]]; then
    data_dir="$data_dir_arg"
else
    data_dir="${XDG_DATA_HOME:-${HOME}/.local/share}/SlayTheSpire2"
fi
mkdir -p -- "$data_dir"
data_dir="$(realpath -e -- "$data_dir")"

run_snapshot_path="$(realpath -e -- "$repository_root/coverage/unattended/mecha-knight-memory-run-snapshot.json")"
request_path="$data_dir/combat_solver_test_request.json"
result_path="$data_dir/combat_solver_test_result.json"

list_game_pids() {
    local proc_exe resolved_executable proc_id
    for proc_exe in /proc/[0-9]*/exe; do
        resolved_executable="$(readlink -f -- "$proc_exe" 2>/dev/null || true)"
        [[ "$resolved_executable" == "$game_executable" ]] || continue
        proc_id="${proc_exe#/proc/}"
        proc_id="${proc_id%/exe}"
        printf '%s\n' "$proc_id"
    done
}

is_game_pid() {
    local proc_id="$1"
    local resolved_executable
    resolved_executable="$(readlink -f -- "/proc/$proc_id/exe" 2>/dev/null || true)"
    [[ "$resolved_executable" == "$game_executable" ]]
}

wait_for_game_exit() {
    local proc_id="$1"
    local wait_seconds="$2"
    local deadline
    deadline=$(( $(date +%s) + wait_seconds ))
    while is_game_pid "$proc_id" && (( $(date +%s) < deadline )); do
        sleep 0.25
    done
    ! is_game_pid "$proc_id"
}

stop_game_process() {
    local proc_id="$1"
    is_game_pid "$proc_id" || return 0
    kill -TERM "$proc_id" 2>/dev/null || true
    if ! wait_for_game_exit "$proc_id" 15; then
        kill -KILL "$proc_id" 2>/dev/null || true
        wait_for_game_exit "$proc_id" 10
    fi
}

mapfile -t existing_game_pids < <(list_game_pids)
if ((${#existing_game_pids[@]} > 0)); then
    die "refusing to start the benchmark while Slay the Spire 2 is already running (pid=${existing_game_pids[*]})"
fi

run_id="$(tr -d '-' </proc/sys/kernel/random/uuid)"
request_temp_path="$request_path.$run_id.tmp"
scratch_dir="$(mktemp -d "${TMPDIR:-/tmp}/combatsolver-visible.XXXXXX")"
request_backup="$scratch_dir/request.backup"
result_backup="$scratch_dir/result.backup"
request_existed=false
result_existed=false
cleanup_armed=false
game_pid=""

restore_protocol_file() {
    local destination="$1"
    local backup="$2"
    local existed="$3"
    local restore_temp="$destination.$run_id.restore.tmp"
    if [[ "$existed" == true ]]; then
        cp -p -- "$backup" "$restore_temp"
        mv -f -- "$restore_temp" "$destination"
    else
        rm -f -- "$destination" "$restore_temp"
    fi
}

cleanup() {
    local exit_code=$?
    local preserve_scratch=false
    trap - EXIT
    set +e
    if [[ -n "$game_pid" ]]; then
        if ! stop_game_process "$game_pid"; then
            printf 'error: failed to stop benchmark game process pid=%s\n' "$game_pid" >&2
            exit_code=1
        fi
    fi
    rm -f -- "$request_temp_path"
    if [[ "$cleanup_armed" == true ]]; then
        if ! restore_protocol_file "$request_path" "$request_backup" "$request_existed"; then
            printf 'error: failed to restore %s\n' "$request_path" >&2
            exit_code=1
            preserve_scratch=true
        fi
        if ! restore_protocol_file "$result_path" "$result_backup" "$result_existed"; then
            printf 'error: failed to restore %s\n' "$result_path" >&2
            exit_code=1
            preserve_scratch=true
        fi
    fi
    if [[ "$preserve_scratch" == true ]]; then
        printf 'error: protocol backups retained at %s\n' "$scratch_dir" >&2
    else
        rm -rf -- "$scratch_dir"
    fi
    exit "$exit_code"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

if [[ -f "$request_path" ]]; then
    cp -p -- "$request_path" "$request_backup"
    request_existed=true
fi
if [[ -f "$result_path" ]]; then
    cp -p -- "$result_path" "$result_backup"
    result_existed=true
fi
cleanup_armed=true

jq -n \
    --arg run_id "$run_id" \
    --arg run_snapshot_path "$run_snapshot_path" \
    --argjson timeout_seconds "$timeout_seconds" \
    --argjson search_max_degree_of_parallelism "$search_max_degree_of_parallelism" \
    --argjson verify_baselib "$verify_baselib_card_modifier_boundary" \
    '{
        schemaVersion: 1,
        runId: $run_id,
        scenarioId: "MECHA-NO-RESCAN-247",
        characterId: "SILENT",
        encounterId: "MECHA_KNIGHT_ELITE",
        runSnapshotPath: $run_snapshot_path,
        seed: "BJCZX3J13PZJ",
        ascension: 0,
        enemyCurrentHp: 300,
        initialPlayerHp: 65,
        cards: [],
        powers: [],
        orbs: [],
        relics: [],
        combatRelics: [],
        potions: [],
        potionCheck: null,
        monsterMoveCheck: null,
        monsterMoveChecks: [],
        additionalMonsterIds: [],
        initialEnemyMoveIds: [],
        timeoutSeconds: $timeout_seconds,
        expectedFinishedTurn: 7,
        expectedFinishedTurnAtMost: null,
        clearPlayerHand: false,
        clearPlayerPiles: false,
        verifyIncrementalSearch: false,
        verifyBaseLibCardModifierBoundary: $verify_baselib,
        performancePresetForTest: "Medium",
        enableNoGcRegionForTest: true,
        noGcRegionBudgetGigabytesForTest: 16,
        deploymentFastModeForTest: "Instant",
        deploymentInterActionDelaySecondsForTest: 0,
        assertDeploymentSpeedRestored: true,
        forceShortSearchOnly: false,
        measureSearchPhases: true,
        searchMaxDegreeOfParallelismForTest: $search_max_degree_of_parallelism,
        holdAfterInitialSearch: false,
        shortSearchBudgetOverrideMilliseconds: 5000,
        deepSearchBudgetOverrideMilliseconds: 60000,
        expectedInitialSearchPhase: "Deep",
        expectedInitialDeepSearchTriggered: true,
        expectedInitialDeepSearchImprovedResult: null,
        expectedInitialTotalElapsedMillisecondsAtMost: 20000,
        expectedInitialTotalAllocatedBytesAtMost: 5500000000,
        expectedInitialGen2CollectionsAtMost: 20,
        expectedInitialTotalGcPauseMillisecondsAtMost: 8000,
        expectedInitialMaxGcPauseMillisecondsAtMost: 50,
        expectedInitialMaxMainThreadFrameGapMillisecondsAtMost: 100,
        expectedInitialMainThreadFramesOver50MillisecondsAtMost: 5,
        expectedInitialMainThreadFramesOver100MillisecondsAtMost: 0,
        expectedInitialTransitionCacheHitsAtLeast: null,
        expectedInitialExecutableActionCountAtLeast: null,
        expectedInitialSoldHp: 0,
        expectedInitialSoldHpAtMost: null,
        expectedInitialSoldHpBranchesPrunedAtLeast: null,
        expectedInitialPotionCount: 0,
        expectedInitialPotionHpSavedAtLeast: null,
        expectedInitialPotionBranchesRejectedAtLeast: null,
        expectedInitialSearchedTurnsAtLeast: 7,
        expectedInitialShufflesCrossedAtLeast: null,
        expectedInitialUnmirroredCount: null,
        expectedInitialHpLostAtMost: null,
        expectedInitialProjectedBattleHpLostAtMost: 43,
        expectedInitialMaxBlockAtLeast: null,
        expectedInitialActualBlockAtLeast: null,
        expectedInitialActionCardId: null,
        expectedInitialActionTitle: null,
        expectedReusedTurn: 3,
        expectedUnexpectedReplansAtMost: 0,
        expectedNativeChoiceOwnerPrefix: "turn_setup:",
        expectedNativeChoiceSurface: "Hand",
        expectedNativeChoiceVisibleAtLeast: 1,
        expectedNativeChoiceSearchStartedAtMost: 0,
        expectedPlayedCardId: null,
        expectedUsedPotionId: null,
        exitOnComplete: true
    }' >"$request_temp_path"
if [[ "$logging_fixture" == true ]]; then
    jq --arg runId "$run_id" --argjson timeout "$timeout_seconds" \
        '. + {runId: $runId, timeoutSeconds: $timeout}' \
        "$repository_root/coverage/unattended/logging-short-combat.json" >"$request_temp_path"
fi
if [[ -n "$evidence_directory" ]]; then
    jq --arg path "$evidence_directory" '. + {evidenceDirectory: $path}' "$request_temp_path" >"$request_temp_path.evidence"
    mv -f -- "$request_temp_path.evidence" "$request_temp_path"
fi
if [[ -n "$checkpoint_archive" ]]; then
    jq -n --arg runId "$run_id" --arg archive "$checkpoint_archive" --arg selector "$checkpoint_selector" \
        --arg mode "$replay_mode" --arg evidence "$evidence_directory" --argjson timeout "$timeout_seconds" \
        --arg policy "$replay_policy_override" \
        '{schemaVersion: 1, runId: $runId, scenarioId: "VISIBLE-CHECKPOINT-REPLAY", checkpointArchivePath: $archive,
          checkpointSelector: $selector, replayMode: $mode, replayPolicyOverridePath: $policy, evidenceDirectory: $evidence, timeoutSeconds: $timeout, exitOnComplete: true}' >"$request_temp_path"
fi
mv -f -- "$request_temp_path" "$request_path"

if ! pgrep -x steam >/dev/null 2>&1; then
    [[ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]] || die "Steam is not running and no graphical display is available"
    "$steam_command" >/dev/null 2>&1 &
    steam_deadline=$(( $(date +%s) + 30 ))
    while ! pgrep -x steam >/dev/null 2>&1 && (( $(date +%s) < steam_deadline )); do
        sleep 0.25
    done
    pgrep -x steam >/dev/null 2>&1 || die "Steam did not start within 30 seconds"
fi

"$steam_command" -applaunch 2868840 >/dev/null 2>&1 || true
launch_deadline=$(( $(date +%s) + 60 ))
while (( $(date +%s) < launch_deadline )); do
    mapfile -t launched_game_pids < <(list_game_pids)
    if ((${#launched_game_pids[@]} > 0)); then
        game_pid="${launched_game_pids[0]}"
        break
    fi
    sleep 0.25
done
[[ -n "$game_pid" ]] || die "Steam did not launch Slay the Spire 2 within 60 seconds"
printf 'VISIBLE_STEAM_STARTED run_id=%s pid=%s\n' "$run_id" "$game_pid"

result_deadline=$(( $(date +%s) + timeout_seconds ))
result_status=""
while (( $(date +%s) < result_deadline )); do
    if [[ -f "$result_path" ]]; then
        candidate_run_id="$(jq -er '.runId // empty' "$result_path" 2>/dev/null || true)"
        if [[ "$candidate_run_id" == "$run_id" ]]; then
            jq . "$result_path"
            result_status="$(jq -r '.status // empty' "$result_path")"
            if [[ "$result_status" != "Passed" ]]; then
                result_stage="$(jq -r '.stage // "unknown"' "$result_path")"
                result_error="$(jq -r '.error // "unknown error"' "$result_path")"
                die "visible Steam benchmark failed at stage '$result_stage': $result_error"
            fi
            break
        fi
    fi
    is_game_pid "$game_pid" || die "the Steam game process exited before publishing benchmark result $run_id"
    sleep 0.25
done
[[ "$result_status" == "Passed" ]] || die "visible Steam benchmark timed out after $timeout_seconds seconds"

wait_for_game_exit "$game_pid" 30 || true
