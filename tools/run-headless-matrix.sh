#!/usr/bin/env bash
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
cd -- "$repo_root"

start_at=1
max_cases=0
continue_on_failure=0
home_directory="${HOME:?HOME is required}"
if [[ -n "${COMBATSOLVER_STEAM_ROOT:-}" ]]; then
    steam_root="$COMBATSOLVER_STEAM_ROOT"
elif [[ -d "$home_directory/.local/share/Steam" ]]; then
    steam_root="$home_directory/.local/share/Steam"
elif [[ -d "$home_directory/.steam/steam" ]]; then
    steam_root="$home_directory/.steam/steam"
else
    steam_root="$home_directory/.local/share/Steam"
fi
sts2_game_root="$steam_root/steamapps/common/Slay the Spire 2"
ritsu_workshop_root="$steam_root/steamapps/workshop/content/2868840/3747602295"
results_path=".local/headless-matrix-results.jsonl"
headless_instance=""
headless_execution_mode="${COMBATSOLVER_HEADLESS_EXECUTION_MODE:-exclusive}"
headless_memory_reservation_mib=4096
headless_cpu_reservation=2
headless_queue_timeout_seconds=120
combat_solver_build_dir=""
[[ $(getconf _NPROCESSORS_ONLN) != 1 ]] || headless_cpu_reservation=1

usage() {
    cat <<'EOF'
Usage: tools/run-headless-matrix.sh [options]

Options:
  --start-at NUMBER              First documented command (1-based; default: 1)
  --max-cases NUMBER             Maximum attempted commands; 0 means all
  --continue-on-failure          Continue with a fresh process after a failure
  --sts2-game-root PATH          Native Linux Slay the Spire 2 directory
  --ritsu-workshop-root PATH     Native Linux RitsuLib workshop directory
  --results-path PATH            JSONL result path
  --headless-instance ID         Stable instance shared by every case and cleanup
  --headless-execution-mode MODE exclusive (default) or parallel
  --headless-memory-reservation-mib NUMBER  Host reservation, not a hard limit
  --headless-cpu-reservation NUMBER         Host reservation, not Search DOP
  --headless-queue-timeout-seconds NUMBER   Admission deadline (default: 120)
  --combat-solver-build-dir PATH Frozen DLL/manifest directory
  -h, --help                     Show this help

Commands keep the lifecycle boundaries documented in docs/TEST_MATRIX.md. Cases
within a group reuse one managed headless process only after a matching
quiescence/ready ACK. Failed or interrupted commands destroy their process before
the matrix proceeds.
EOF
}

die() {
    echo "run-headless-matrix.sh: $*" >&2
    exit 2
}

while (($# > 0)); do
    case "$1" in
        --start-at|--max-cases|--sts2-game-root|--ritsu-workshop-root|--results-path|--headless-instance|--headless-execution-mode|--headless-memory-reservation-mib|--headless-cpu-reservation|--headless-queue-timeout-seconds|--combat-solver-build-dir)
            (($# >= 2)) || die "missing value for $1"
            option_name="${1#--}"
            option_name="${option_name//-/_}"
            printf -v "$option_name" '%s' "$2"
            shift 2
            ;;
        --start-at=*|--max-cases=*|--sts2-game-root=*|--ritsu-workshop-root=*|--results-path=*|--headless-instance=*|--headless-execution-mode=*|--headless-memory-reservation-mib=*|--headless-cpu-reservation=*|--headless-queue-timeout-seconds=*|--combat-solver-build-dir=*)
            option_name="${1%%=*}"
            option_name="${option_name#--}"
            option_name="${option_name//-/_}"
            printf -v "$option_name" '%s' "${1#*=}"
            shift
            ;;
        --continue-on-failure)
            continue_on_failure=1
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *) die "unknown argument: $1" ;;
    esac
done

[[ "$start_at" =~ ^[0-9]+$ ]] && ((start_at >= 1 && start_at <= 10000)) || \
    die "--start-at must be between 1 and 10000"
[[ "$max_cases" =~ ^[0-9]+$ ]] && ((max_cases <= 10000)) || \
    die "--max-cases must be between 0 and 10000"
for command_name in jq setsid realpath flock sha256sum; do
    command -v "$command_name" >/dev/null 2>&1 || die "$command_name is required"
done
lifecycle_mode=documented

mapfile -t commands < <(
    awk '/^(\.\/)?tools\/run-unattended-test\.sh([[:space:]]|$)/ { print }' docs/TEST_MATRIX.md
)
((${#commands[@]} > 0)) || die "no native unattended commands were found in docs/TEST_MATRIX.md"
((start_at <= ${#commands[@]})) || \
    die "--start-at exceeds the ${#commands[@]} documented commands"

missing_fixture_reason() {
    local scenario_id=$1 choices_run="${CHOICES_PARADOX_RUN_SNAPSHOT_PATH:-}"
    local choices_progress="${CHOICES_PARADOX_PROGRESS_SNAPSHOT_PATH:-}"
    local user_run="${RUN_SNAPSHOT_PATH:-}"
    case "$scenario_id" in
        CHOICES-PARADOX-SCROLLS-0160)
            if [[ -z "$choices_run" || -z "$choices_progress" \
                || ! -f "$choices_run" || ! -f "$choices_progress" ]]; then
                printf '%s' 'missing external choices-paradox run/progress snapshots'
            fi
            ;;
        QUEEN-CHAINS-REUSE-FINAL-085|CORPSE-SLUGS-USER-RUN-073)
            if [[ -z "$user_run" || ! -f "$user_run" ]]; then
                printf '%s' 'missing external user profile save'
            fi
            ;;
    esac
}

results_full_path="$(realpath -m -- "$results_path")"
mkdir -p -- "$(dirname -- "$results_full_path")"
: >"$results_full_path"
runner="$repo_root/tools/run-unattended-test.sh"
[[ -x "$runner" ]] || die "unattended launcher is not executable: $runner"

attempted=0
passed=0
failed=0
skipped=0
ran_case=0
cleanup_status=0
suite_started_ms="$(date +%s%3N)"
signal_status=0
case_pid=""
case_pgid=""

if [[ -z $headless_instance ]]; then
    headless_instance="$(printf '%s' "$repo_root" | sha256sum)"
    headless_instance="worktree-${headless_instance:0:16}"
fi
headless_root="${COMBATSOLVER_HEADLESS_ROOT:-${XDG_STATE_HOME:-${HOME}/.local/state}/CombatSolver/headless-instances/$headless_instance}"
headless_root="$(realpath -m -- "$headless_root")"
source_game_root="$(realpath -m -- "$sts2_game_root")"
[[ $headless_root != "$source_game_root" && $headless_root != "$source_game_root/"* \
    && $source_game_root != "$headless_root/"* ]] || die 'matrix runtime must be separate from the source game'
source "$script_dir/headless-runtime.sh"
HR_WORKTREE="$repo_root"
hr_init "$headless_root" "$headless_instance" "$headless_root/game/SlayTheSpire2" \
    "$headless_root/data" "$headless_execution_mode" "$headless_memory_reservation_mib" \
    "$headless_cpu_reservation" "$headless_queue_timeout_seconds" || die 'cannot claim matrix instance'
[[ ! -L $headless_root/matrix.lock ]] || die 'matrix lock cannot be a symlink'
exec {matrix_fd}>"$headless_root/matrix.lock"
flock -n "$matrix_fd" || die 'instance already has a matrix producer'
# Cases retain the ordinary launcher lock; the matrix lock spans the gaps.
flock -u "$HR_INSTANCE_FD"
exec {HR_INSTANCE_FD}>&-
export COMBATSOLVER_HEADLESS_ROOT="$headless_root"
process_marker_path="$headless_root/process.json"
runtime_arguments=(--headless-instance "$headless_instance"
    --headless-execution-mode "$headless_execution_mode"
    --headless-memory-reservation-mib "$headless_memory_reservation_mib"
    --headless-cpu-reservation "$headless_cpu_reservation"
    --headless-queue-timeout-seconds "$headless_queue_timeout_seconds")
if [[ -n $combat_solver_build_dir ]]; then
    runtime_arguments+=(--combat-solver-build-dir "$(realpath -m -- "$combat_solver_build_dir")")
fi

cleanup_headless_process() {
    local allow_before_first_case=${1:-0} marker_pid="" cleanup_status=0 candidate
    if [[ ! -f $process_marker_path ]]; then
        for candidate in /proc/[0-9]*/exe; do
            if [[ $(readlink -f -- "$candidate" 2>/dev/null) == "$HR_EXECUTABLE" ]]; then
                echo 'run-headless-matrix.sh: markerless instance process preserved; cleanup cannot prove ownership' >&2
                return 1
            fi
        done
        return 0
    fi
    if ((ran_case == 1 || allow_before_first_case == 1)) \
        && [[ -f "$process_marker_path" ]]; then
        jq -e --arg root "$headless_root" --arg id "$headless_instance" --arg worktree "$repo_root" \
            '.schemaVersion == 1 and .root == $root and .instance == $id and .worktree == $worktree' \
            "$headless_root/runtime-owner.json" >/dev/null || {
            echo 'run-headless-matrix.sh: runtime owner changed; cleanup preserved the process' >&2; return 1;
        }
        jq -e --arg executable "$HR_EXECUTABLE" --arg data "$headless_root/data/SlayTheSpire2" \
            '(.pid | type == "number" and . > 0 and floor == .) and
             (.procStartTimeTicks | type == "string" and test("^[0-9]+$")) and
             .executable == $executable and .dataDir == $data' "$process_marker_path" >/dev/null || {
            echo 'run-headless-matrix.sh: invalid or foreign marker preserved; cleanup refused' >&2; return 1;
        }
        marker_pid="$(jq -er '.pid | select(type == "number" and . > 0 and floor == .)' \
            "$process_marker_path" 2>/dev/null || true)"
        [[ -n "$marker_pid" ]] || marker_pid=unknown
        echo "MATRIX_CLEANUP_BEGIN pid=$marker_pid"
        "$runner" \
            --scenario-id MATRIX-CLEANUP \
            --sts2-game-root "$sts2_game_root" \
            --ritsu-workshop-root "$ritsu_workshop_root" \
            "${runtime_arguments[@]}" \
            --stop-after-combat-root-snapshot-assertion \
            --timeout-seconds 90 \
            --exit-on-complete {matrix_fd}>&- || cleanup_status=$?
        echo "MATRIX_CLEANUP_END exit_code=$cleanup_status"
    fi
    return "$cleanup_status"
}

cleanup_on_exit() {
    local original_status=$? cleanup_status=0 owned_case_cleanup_status=0
    # Keep the signal handlers installed while cleanup runs. They only latch a
    # status, so a second Ctrl+C cannot interrupt the owned-process cleanup.
    trap - EXIT
    if [[ "$case_pid" =~ ^[0-9]+$ ]]; then
        stop_owned_case_after_signal || owned_case_cleanup_status=$?
    fi
    cleanup_headless_process || cleanup_status=$?
    if ((original_status == 0 && owned_case_cleanup_status != 0)); then
        original_status=$owned_case_cleanup_status
    fi
    if ((original_status == 0 && cleanup_status != 0)); then
        original_status=$cleanup_status
    fi
    if ((original_status == 0 && signal_status != 0)); then
        original_status=$signal_status
    fi
    trap - INT TERM HUP
    exit "$original_status"
}

latch_signal() {
    local requested_status=$1
    ((signal_status != 0)) || signal_status=$requested_status
}

case_process_is_alive() {
    local pid=$1 stat_line process_state
    [[ "$pid" =~ ^[0-9]+$ && -r "/proc/$pid/stat" ]] || return 1
    IFS= read -r stat_line <"/proc/$pid/stat" || return 1
    stat_line="${stat_line##*) }"
    process_state="${stat_line%% *}"
    [[ "$process_state" != Z ]]
}

case_process_group_id() {
    local pid=$1 stat_line
    local -a stat_fields=()
    [[ "$pid" =~ ^[0-9]+$ && -r "/proc/$pid/stat" ]] || return 1
    IFS= read -r stat_line <"/proc/$pid/stat" || return 1
    stat_line="${stat_line##*) }"
    read -r -a stat_fields <<<"$stat_line"
    # After stripping comm, element 0 is proc(5) field 3; pgrp is field 5.
    ((${#stat_fields[@]} >= 3)) || return 1
    [[ "${stat_fields[2]}" =~ ^[0-9]+$ ]] || return 1
    printf '%s\n' "${stat_fields[2]}"
}

signal_owned_case() {
    local signal_name=$1 current_pgid=""
    [[ "$case_pid" =~ ^[0-9]+$ ]] || return 0
    case_process_is_alive "$case_pid" || return 0
    if [[ "$case_pgid" =~ ^[0-9]+$ ]] \
        && ((case_pgid == case_pid)) \
        && current_pgid="$(case_process_group_id "$case_pid" 2>/dev/null)" \
        && [[ "$current_pgid" == "$case_pgid" ]]; then
        kill -"$signal_name" -- "-$case_pgid" 2>/dev/null || true
    else
        # This remains safe when group inspection is unavailable: case_pid is
        # our unreaped direct child and therefore cannot have been reused.
        kill -"$signal_name" "$case_pid" 2>/dev/null || true
    fi
}

stop_owned_case_after_signal() {
    local stop_deadline
    [[ "$case_pid" =~ ^[0-9]+$ ]] || return 0
    signal_owned_case TERM
    stop_deadline=$((SECONDS + 25))
    while case_process_is_alive "$case_pid" && ((SECONDS < stop_deadline)); do
        sleep 0.1
    done
    if case_process_is_alive "$case_pid"; then
        signal_owned_case KILL
        stop_deadline=$((SECONDS + 10))
        while case_process_is_alive "$case_pid" && ((SECONDS < stop_deadline)); do
            sleep 0.1
        done
    fi
    if case_process_is_alive "$case_pid"; then
        echo "run-headless-matrix.sh: owned case process did not exit: pid=$case_pid" >&2
        return 1
    fi
    wait "$case_pid" 2>/dev/null || true
    case_pid=""
    case_pgid=""
}

trap cleanup_on_exit EXIT
trap 'latch_signal 130' INT
trap 'latch_signal 143' TERM
trap 'latch_signal 129' HUP

echo "MATRIX_BEGIN total=${#commands[@]} start_at=$start_at max_cases=$max_cases lifecycle_mode=$lifecycle_mode"

preflight_cleanup_status=0
cleanup_headless_process 1 || preflight_cleanup_status=$?
if ((preflight_cleanup_status != 0)); then
    cleanup_status=$preflight_cleanup_status
    echo "run-headless-matrix.sh: refusing to start after preflight cleanup failed with code $preflight_cleanup_status" >&2
    exit "$preflight_cleanup_status"
fi

for ((offset = start_at - 1; offset < ${#commands[@]}; offset++)); do
    ((signal_status == 0)) || exit "$signal_status"
    index=$((offset + 1))
    command_line="${commands[$offset]}"
    if [[ "$command_line" =~ --scenario-id(=|[[:space:]]+)([^[:space:]]+) ]]; then
        scenario_id="${BASH_REMATCH[2]}"
        scenario_id="${scenario_id#\'}"
        scenario_id="${scenario_id%\'}"
        scenario_id="${scenario_id#\"}"
        scenario_id="${scenario_id%\"}"
    else
        die "command $index does not contain --scenario-id: $command_line"
    fi

    fixture_reason="$(missing_fixture_reason "$scenario_id")"
    if [[ -n "$fixture_reason" ]]; then
        skipped=$((skipped + 1))
        jq -cn \
            --argjson index "$index" \
            --arg scenarioId "$scenario_id" \
            --arg reason "$fixture_reason" \
            '{index: $index, scenarioId: $scenarioId, status: "SkippedMissingFixture", reason: $reason, elapsedMilliseconds: 0, exitCode: null}' \
            >>"$results_full_path"
        echo "MATRIX_SKIP index=$index scenario=$scenario_id reason=$fixture_reason"
        if [[ "$command_line" =~ (^|[[:space:]])--exit-on-complete($|[[:space:]]) ]]; then
            skipped_boundary_cleanup_status=0
            cleanup_headless_process 1 || skipped_boundary_cleanup_status=$?
            if ((skipped_boundary_cleanup_status != 0)); then
                cleanup_status=$skipped_boundary_cleanup_status
                echo "run-headless-matrix.sh: skipped exit boundary cleanup failed with code $skipped_boundary_cleanup_status" >&2
                break
            fi
        fi
        continue
    fi

    if ((max_cases > 0 && attempted >= max_cases)); then
        break
    fi

    attempted=$((attempted + 1))
    ran_case=1
    case_command="$command_line"
    printf -v game_root_quoted '%q' "$sts2_game_root"
    printf -v ritsu_root_quoted '%q' "$ritsu_workshop_root"
    case_command+=" --sts2-game-root $game_root_quoted --ritsu-workshop-root $ritsu_root_quoted"
    printf -v runtime_arguments_quoted ' %q' "${runtime_arguments[@]}"
    case_command+="$runtime_arguments_quoted"

    case_started_ms="$(date +%s%3N)"
    echo "MATRIX_CASE_BEGIN index=$index scenario=$scenario_id"
    exit_code=0
    setsid bash -c "exec $case_command" {matrix_fd}>&- &
    case_pid=$!
    # Non-interactive Bash starts background children in its own process group,
    # so GNU setsid can exec directly and establishes PGID == child PID. Recheck
    # /proc before every group signal; fall back to the unreaped direct PID while
    # setsid is still in its small pre-exec window.
    case_pgid=$case_pid

    if ((signal_status != 0)); then
        stop_owned_case_after_signal || true
        exit "$signal_status"
    fi
    if wait "$case_pid"; then
        exit_code=0
    else
        exit_code=$?
    fi
    if ((signal_status != 0)); then
        # wait is interruptible; retain the unreaped direct-child identity until
        # the verified process group has stopped and the launcher has cleaned
        # any markerless game it started.
        stop_owned_case_after_signal || true
        exit "$signal_status"
    fi
    case_pid=""
    case_pgid=""
    case_finished_ms="$(date +%s%3N)"
    elapsed_ms=$((case_finished_ms - case_started_ms))

    if ((exit_code == 0)); then
        status=Passed
        passed=$((passed + 1))
    else
        status=Failed
        failed=$((failed + 1))
    fi
    jq -cn \
        --argjson index "$index" \
        --arg scenarioId "$scenario_id" \
        --arg status "$status" \
        --argjson elapsedMilliseconds "$elapsed_ms" \
        --argjson exitCode "$exit_code" \
        --arg command "$case_command" \
        --arg documentedCommand "$command_line" \
        --arg lifecycleMode "$lifecycle_mode" \
        '{index: $index, scenarioId: $scenarioId, status: $status, elapsedMilliseconds: $elapsedMilliseconds, exitCode: $exitCode, command: $command, documentedCommand: $documentedCommand, lifecycleMode: $lifecycleMode}' \
        >>"$results_full_path"
    echo "MATRIX_CASE_END index=$index scenario=$scenario_id status=$status elapsed_ms=$elapsed_ms exit_code=$exit_code"

    if ((exit_code != 0)); then
        failure_cleanup_status=0
        cleanup_headless_process || failure_cleanup_status=$?
        ((signal_status == 0)) || exit "$signal_status"
        if ((failure_cleanup_status != 0)); then
            cleanup_status=$failure_cleanup_status
            echo "run-headless-matrix.sh: refusing to continue after cleanup failed with code $failure_cleanup_status" >&2
            break
        fi
        if ((continue_on_failure == 0)); then
            break
        fi
    fi
done

final_cleanup_status=0
cleanup_headless_process || final_cleanup_status=$?
((signal_status == 0)) || exit "$signal_status"
if ((cleanup_status == 0)); then
    cleanup_status=$final_cleanup_status
fi
trap - EXIT INT TERM HUP
suite_finished_ms="$(date +%s%3N)"
suite_elapsed_ms=$((suite_finished_ms - suite_started_ms))
echo "MATRIX_END total=${#commands[@]} attempted=$attempted passed=$passed failed=$failed skipped=$skipped cleanup_exit_code=$cleanup_status elapsed_ms=$suite_elapsed_ms results=$results_full_path"
((failed == 0 && cleanup_status == 0))
