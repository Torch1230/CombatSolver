#!/usr/bin/env bash
# Linux-only, no-game integration tests for the real headless admission helper.
# The fake game is an ordinary native sleep process, never SlayTheSpire2.
set -Eeuo pipefail

# Only the disposable test PATH symlink invokes this strict external-command
# stub. Real host games are outside the test domain; the helper's admission,
# lease files, flock calls and /proc identity checks are not stubbed.
if [[ "${0##*/}" == pgrep ]]; then
    [[ -n "${COMBATSOLVER_HEADLESS_TEST_DOMAIN:-}" && \
        -f "$COMBATSOLVER_HEADLESS_TEST_DOMAIN/mock-domain" ]] || exit 64
    (($# == 2)) && [[ "$1" == -x && "$2" == SlayTheSpire2 ]] || exit 64
    if [[ -f "$COMBATSOLVER_HEADLESS_TEST_DOMAIN/enumerated-game.pid" ]]; then
        IFS= read -r enumerated_pid <"$COMBATSOLVER_HEADLESS_TEST_DOMAIN/enumerated-game.pid"
        [[ "$enumerated_pid" =~ ^[1-9][0-9]*$ ]] || exit 65
        printf '%s\n' "$enumerated_pid"
        exit 0
    fi
    exit 1
fi

# The warm-memory case controls only these three /proc memory readings. Locks,
# leases, process birth/executable checks and native fixture children stay real.
if [[ "${0##*/}" == awk ]]; then
    [[ -n "${COMBATSOLVER_HEADLESS_TEST_DOMAIN:-}" &&
        -f "$COMBATSOLVER_HEADLESS_TEST_DOMAIN/mock-domain" ]] || exit 64
    (($# == 2)) || exit 64
    case "$1" in
        '/^MemTotal:/ {print int($2/1024)}')
            [[ $2 == /proc/meminfo ]] || exit 64
            memory_file=memory-total-mib ;;
        '/^MemAvailable:/ {print int($2/1024)}')
            [[ $2 == /proc/meminfo ]] || exit 64
            memory_file=memory-available-mib ;;
        '/^VmRSS:/ {print int($2/1024)}')
            [[ $2 =~ ^/proc/[1-9][0-9]*/status$ ]] || exit 64
            memory_file=memory-rss-mib ;;
        *) exit 64 ;;
    esac
    IFS= read -r memory_value <"$COMBATSOLVER_HEADLESS_TEST_DOMAIN/$memory_file"
    [[ $memory_value =~ ^[0-9]+$ ]] || exit 65
    printf '%s\n' "$memory_value"
    exit 0
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
test_script="$script_dir/test-headless-runtime.sh"
runtime_helper="$script_dir/headless-runtime.sh"

fail() {
    echo "test-headless-runtime.sh: $*" >&2
    exit 1
}

process_live() {
    local pid=$1 stat_line
    [[ -r "/proc/$pid/stat" ]] || return 1
    IFS= read -r stat_line <"/proc/$pid/stat" || return 1
    stat_line="${stat_line##*) }"
    [[ "${stat_line%% *}" != Z ]]
}

process_birth() {
    local pid=$1 stat_line
    local -a stat_fields=()
    [[ -r "/proc/$pid/stat" ]] || return 1
    IFS= read -r stat_line <"/proc/$pid/stat" || return 1
    stat_line="${stat_line##*) }"
    read -r -a stat_fields <<<"$stat_line"
    ((${#stat_fields[@]} >= 20)) || return 1
    printf '%s\n' "${stat_fields[19]}"
}

stop_fixture_process() {
    local pid=$1 expected_birth=$2 deadline current_birth
    process_live "$pid" || return 0
    if ! current_birth="$(process_birth "$pid")"; then
        ! process_live "$pid"
        return
    fi
    [[ "$current_birth" == "$expected_birth" ]] || return 1
    kill -TERM "$pid" || return 1
    deadline=$((SECONDS + 5))
    while process_live "$pid"; do
        if ! current_birth="$(process_birth "$pid")"; then
            ! process_live "$pid"
            return
        fi
        [[ "$current_birth" == "$expected_birth" ]] || return 0
        ((SECONDS < deadline)) || return 1
        sleep 0.025
    done
}

# Each driver is a distinct producer shell. It sources the production helper;
# there is no duplicate admission or lock algorithm in this test harness.
if [[ "${1:-}" == --fixture-driver ]]; then
    shift
    (($# == 10)) || fail "invalid internal driver arguments"
    case_root=$1
    driver_name=$2
    instance_id=$3
    mode=$4
    action=$5
    reused_pid=$6
    reused_birth=$7
    queue_seconds=$8
    memory_mib=$9
    cpu_count=${10}
    driver_dir="$case_root/drivers/$driver_name"
    export COMBATSOLVER_HEADLESS_HOST_ROOT="$case_root/host"
    export COMBATSOLVER_HEADLESS_TEST_DOMAIN="$case_root"
    export PATH="$case_root/bin:$PATH"
    export HR_WORKTREE="$case_root/worktree-$instance_id"
    # This is exclusively a temporary test domain, not a real game directory.
    instance_root="$case_root/instances/$instance_id"
    fixture_executable="$instance_root/game/SlayTheSpire2"
    mkdir -p -- "$driver_dir" "$HR_WORKTREE"
    [[ -r "$runtime_helper" ]] || fail "missing runtime helper: $runtime_helper"
    source "$runtime_helper"

    mock_pid=""
    mock_birth=""
    mock_is_child=0
    leave_warm=0
    initialized=0
    driver_cleanup() {
        local status=$? cleanup_status=0 child_status=0
        trap - EXIT INT TERM HUP
        # Match the launcher cancellation boundary: do not recursively wait
        # for a coordinator flock still held by this very producer.
        if [[ -n "${HR_HOST_FD:-}" ]]; then
            hr_host_unlock || cleanup_status=1
        fi
        if [[ -n "$mock_pid" && -n "$mock_birth" ]] && ((leave_warm == 0)); then
            if ! stop_fixture_process "$mock_pid" "$mock_birth"; then
                echo "could not safely stop fixture PID $mock_pid birth $mock_birth" >&2
                cleanup_status=1
            fi
            if ((mock_is_child == 1)); then
                wait "$mock_pid" || child_status=$?
                if ((child_status != 0 && child_status != 143)); then
                    echo "unexpected fixture process exit: $child_status" >&2
                    cleanup_status=1
                fi
            fi
        fi
        if ((initialized == 1)); then
            if ! hr_release; then
                echo "helper did not release producer lease" >&2
                cleanup_status=1
            fi
        fi
        if ((status == 0 && cleanup_status != 0)); then
            status=$cleanup_status
        fi
        exit "$status"
    }
    trap driver_cleanup EXIT
    trap 'exit 130' INT
    trap 'exit 143' TERM
    trap 'exit 129' HUP

    hr_init "$instance_root" "$instance_id" "$fixture_executable" \
        "$instance_root/data" "$mode" "$memory_mib" "$cpu_count" "$queue_seconds"
    initialized=1
    mkdir -p -- "$instance_root/game"
    if [[ ! -f "$fixture_executable" ]]; then
        cp -- "$(realpath -- "$(command -v sleep)")" "$fixture_executable"
    fi
    : >"$driver_dir/initialized"
    if [[ -n "$reused_pid" ]]; then
        hr_acquire "$reused_pid" "$reused_birth"
        mock_pid=$reused_pid
        mock_birth=$reused_birth
    else
        hr_acquire
        if [[ "$action" != pending ]]; then
            (
                # Match the launcher contract: the warm game must not keep
                # the producer's instance flock after its producer exits.
                exec {HR_INSTANCE_FD}>&-
                exec "$fixture_executable" 180
            ) &
            mock_pid=$!
            mock_is_child=1
            deadline=$((SECONDS + 5))
            until [[ "$(readlink -f -- "/proc/$mock_pid/exe")" == "$fixture_executable" ]]; do
                ((SECONDS < deadline)) || fail "native fixture process did not start"
                sleep 0.025
            done
            mock_birth="$(hr_start_time "$mock_pid")"
            printf '%s %s\n' "$mock_pid" "$mock_birth" >"$driver_dir/game.identity"
            if [[ "$action" != unbound ]]; then
                hr_bind "$mock_pid" "$mock_birth"
            fi
        fi
    fi
    if [[ -n "$mock_pid" ]]; then
        printf '%s %s\n' "$mock_pid" "$mock_birth" >"$driver_dir/game.identity"
    fi
    jq -cn --arg lease "$HR_LEASE_PATH" --arg token "$HR_TOKEN" \
        --arg pid "$mock_pid" --arg birth "$mock_birth" \
        --arg instance "$instance_id" --arg root "$instance_root" \
        '{lease:$lease,token:$token,pid:$pid,birth:$birth,instance:$instance,root:$root}' \
        >"$driver_dir/acquired.json.tmp"
    mv -- "$driver_dir/acquired.json.tmp" "$driver_dir/acquired.json"
    while [[ ! -f "$driver_dir/control" ]]; do
        sleep 0.025
    done
    IFS= read -r command <"$driver_dir/control"
    case "$command" in
        finish) exit 0 ;;
        fail) exit 23 ;;
        warm)
            [[ -n "$mock_pid" ]] || fail "cannot detach a pending-only lease"
            leave_warm=1
            exit 0
            ;;
        *) fail "unknown fixture command: $command" ;;
    esac
fi

test_selection=leases
case "${1:-}" in
    -h|--help)
        echo 'Usage: bash tools/test-headless-runtime.sh [--leases | --warm-memory | --snapshots | --snapshot-failures]'
        echo 'Runs strict native-process mocks only; never builds or launches the game.'
        echo 'The default --leases tests admission; the snapshot selections are independently runnable.'
        exit 0
        ;;
    --leases) shift ;;
    --warm-memory) test_selection=warm-memory; shift ;;
    --snapshots) test_selection=snapshots; shift ;;
    --snapshot-failures) test_selection=snapshot-failures; shift ;;
    '') ;;
    *) fail "unknown argument: $1" ;;
esac
(($# == 0)) || fail "unexpected arguments"
[[ "$(uname -s)" == Linux ]] || fail "Linux /proc is required"
for executable in bash jq flock realpath readlink sleep mktemp rg cp ln cmp diff; do
    command -v "$executable" >/dev/null 2>&1 || fail "missing command: $executable"
done
[[ -r "$runtime_helper" ]] || fail "missing runtime helper: $runtime_helper"

suite_root="$(mktemp -d -t combatsolver-headless-tests.XXXXXXXX)"
declare -A driver_pids=()
declare -A driver_births=()
declare -A driver_dirs=()
case_number=0
passed=0

suite_cleanup() {
    local status=$? cleanup_status=0 name pid birth child_status identity_path
    trap - EXIT INT TERM HUP
    # The registry contains only child producers launched by this invocation.
    for name in "${!driver_pids[@]}"; do
        pid=${driver_pids[$name]}
        birth=${driver_births[$name]}
        stop_fixture_process "$pid" "$birth" || cleanup_status=1
        child_status=0
        wait "$pid" || child_status=$?
        if ((child_status != 0 && child_status != 143)); then
            echo "unfinished driver $name exited $child_status during cleanup" >&2
            cleanup_status=1
        fi
    done
    # Warm games intentionally outlive producers. Do not enumerate or kill any
    # host-wide process name; inspect only identities created below our mktemp.
    shopt -s nullglob
    for identity_path in "$suite_root"/case-*/drivers/*/game.identity; do
        read -r pid birth <"$identity_path"
        stop_fixture_process "$pid" "$birth" || cleanup_status=1
    done
    if ((status == 0 && cleanup_status != 0)); then
        status=$cleanup_status
    fi
    echo "HEADLESS_RUNTIME_TEST_ARTIFACTS path=$suite_root"
    exit "$status"
}
trap suite_cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP

new_case() {
    case_number=$((case_number + 1))
    case_root="$suite_root/case-$case_number"
    mkdir -p -- "$case_root/bin"
    : >"$case_root/mock-domain"
    ln -s -- "$test_script" "$case_root/bin/pgrep"
}

start_driver() {
    local name=$1 instance=$2 mode=$3 action=${4:-hold}
    local reused_pid=${5:-} reused_birth=${6:-} queue_seconds=${7:-10}
    local memory_mib=${8:-1} cpu_count=${9:-1}
    [[ -z "${driver_pids[$name]:-}" ]] || fail "duplicate test driver name: $name"
    driver_dirs[$name]="$case_root/drivers/$name"
    mkdir -p -- "${driver_dirs[$name]}"
    bash "$test_script" --fixture-driver "$case_root" "$name" "$instance" \
        "$mode" "$action" "$reused_pid" "$reused_birth" "$queue_seconds" \
        "$memory_mib" "$cpu_count" >"${driver_dirs[$name]}/driver.log" 2>&1 &
    driver_pids[$name]=$!
    driver_births[$name]="$(process_birth "${driver_pids[$name]}")"
}

await_file() {
    local name=$1 filename=$2 deadline=$((SECONDS + 12))
    while [[ ! -f "${driver_dirs[$name]}/$filename" ]]; do
        process_live "${driver_pids[$name]}" || \
            fail "$name exited before $filename; see ${driver_dirs[$name]}/driver.log"
        ((SECONDS < deadline)) || fail "$name timed out waiting for $filename"
        sleep 0.025
    done
}

await_acquired() { await_file "$1" acquired.json; }

assert_queued() {
    local name=$1 observation deadline=$((SECONDS + 10))
    await_file "$name" initialized
    until rg -q '^HEADLESS_QUEUED ' "${driver_dirs[$name]}/driver.log"; do
        [[ ! -e "${driver_dirs[$name]}/acquired.json" ]] || fail "$name bypassed admission"
        process_live "${driver_pids[$name]}" || fail "$name exited instead of queueing"
        ((SECONDS < deadline)) || fail "$name did not reach the real helper's queue"
        sleep 0.025
    done
    # This bounded negative observation is paired with positive acquisition
    # after the blocker is released, not used as proof of scheduling speed.
    for observation in {1..10}; do
        [[ ! -e "${driver_dirs[$name]}/acquired.json" ]] || fail "$name bypassed admission"
        process_live "${driver_pids[$name]}" || fail "$name exited instead of queueing"
        sleep 0.025
    done
}

field() { jq -er ".$2" "${driver_dirs[$1]}/acquired.json"; }

send_control() {
    local name=$1 command=$2
    process_live "${driver_pids[$name]}" || fail "$name already exited"
    printf '%s\n' "$command" >"${driver_dirs[$name]}/control.tmp"
    mv -- "${driver_dirs[$name]}/control.tmp" "${driver_dirs[$name]}/control"
}

await_exit() {
    local name=$1 expected=$2 status=0 deadline=$((SECONDS + 12))
    while process_live "${driver_pids[$name]}"; do
        ((SECONDS < deadline)) || fail "$name did not exit"
        sleep 0.025
    done
    wait "${driver_pids[$name]}" || status=$?
    unset 'driver_pids[$name]' 'driver_births[$name]'
    if [[ "$expected" == nonzero ]]; then
        ((status != 0)) || fail "$name unexpectedly succeeded"
    else
        ((status == expected)) || fail "$name exited $status, expected $expected"
    fi
}

finish_driver() {
    send_control "$1" finish
    await_exit "$1" 0
}

assert_game_live() {
    local name=$1 pid birth
    pid="$(field "$name" pid)"
    birth="$(field "$name" birth)"
    process_live "$pid" || fail "$name's game was stopped by another instance"
    [[ "$(process_birth "$pid")" == "$birth" ]] || fail "$name's process identity changed"
}

pass() {
    passed=$((passed + 1))
    echo "PASS $passed - $*"
}

run_snapshot_tests() {
    new_case
    local source_root="$case_root/source-game"
    local artifacts="$case_root/artifacts"
    local a_root="$case_root/instances/snapshot-a"
    local b_root="$case_root/instances/snapshot-b"
    local a_id b_id a_next_id a_rejected_id live_pid live_birth live_status=0
    local -a retired_games=()
    mkdir -p -- "$source_root/mods/CombatSolver" \
        "$source_root/mods/CombatSolverHeadlessRitsuLib" \
        "$source_root/mods/OtherMod" "$artifacts/a" "$artifacts/b" "$artifacts/ritsu"
    cp -- "$(realpath -- "$(command -v sleep)")" "$source_root/SlayTheSpire2"
    printf '%s\n' 'installed-source-dll' >"$source_root/mods/CombatSolver/CombatSolver.dll"
    printf '%s\n' '{"id":"CombatSolver","version":"source"}' >"$source_root/mods/CombatSolver/CombatSolver.json"
    printf '%s\n' 'installed-source-ritsu' >"$source_root/mods/CombatSolverHeadlessRitsuLib/STS2-RitsuLib.dll"
    printf '%s\n' 'other-mod-payload' >"$source_root/mods/OtherMod/OtherMod.dll"
    printf '%s\n' 'source-game-data' >"$source_root/game-data.pck"
    printf '%s\n' 'artifact-a-v1' >"$artifacts/a/CombatSolver.dll"
    printf '%s\n' '{"id":"CombatSolver","version":"a"}' >"$artifacts/a/CombatSolver.json"
    printf '%s\n' 'artifact-b-v1' >"$artifacts/b/CombatSolver.dll"
    printf '%s\n' '{"id":"CombatSolver","version":"b"}' >"$artifacts/b/CombatSolver.json"
    printf '%s\n' 'frozen-ritsu-payload' >"$artifacts/ritsu/STS2-RitsuLib.dll"
    printf '%s\n' '{"id":"STS2-RitsuLib"}' >"$artifacts/ritsu/STS2-RitsuLib.json"
    cp -a -- "$source_root" "$case_root/source-before"

    snapshot_id() (
        source "$runtime_helper"
        hr_snapshot_id "$source_root" "$artifacts/$1/CombatSolver.dll" \
            "$artifacts/$1/CombatSolver.json" "$artifacts/ritsu/STS2-RitsuLib.dll" \
            "$artifacts/ritsu/STS2-RitsuLib.json"
    )
    prepare_snapshot() (
        local instance_id="snapshot-$1" instance_root="$case_root/instances/snapshot-$1"
        export COMBATSOLVER_HEADLESS_HOST_ROOT="$case_root/host"
        export HR_WORKTREE="$case_root/worktree-$1"
        source "$runtime_helper"
        hr_init "$instance_root" "$instance_id" "$instance_root/game/SlayTheSpire2" \
            "$instance_root/data" parallel 1 1 10 || exit
        hr_prepare_snapshot "$source_root" "$artifacts/$1/CombatSolver.dll" \
            "$artifacts/$1/CombatSolver.json" "$artifacts/ritsu/STS2-RitsuLib.dll" \
            "$artifacts/ritsu/STS2-RitsuLib.json" "$2"
    )

    a_id="$(snapshot_id a)"
    b_id="$(snapshot_id b)"
    [[ "$a_id" != "$b_id" ]] || fail 'different build payloads produced the same snapshot ID'
    prepare_snapshot a "$a_id" >"$case_root/prepare-a.log" 2>&1
    prepare_snapshot b "$b_id" >"$case_root/prepare-b.log" 2>&1
    cmp -s -- "$artifacts/a/CombatSolver.dll" "$a_root/game/mods/CombatSolver/CombatSolver.dll" || fail 'A did not load its private build payload'
    cmp -s -- "$artifacts/b/CombatSolver.dll" "$b_root/game/mods/CombatSolver/CombatSolver.dll" || fail 'B did not load its private build payload'
    cmp -s -- "$artifacts/a/CombatSolver.json" "$a_root/game/mods/CombatSolver/CombatSolver.json" || fail 'A manifest was not frozen with A DLL'
    cmp -s -- "$artifacts/b/CombatSolver.json" "$b_root/game/mods/CombatSolver/CombatSolver.json" || fail 'B manifest was not frozen with B DLL'
    cmp -s -- "$artifacts/ritsu/STS2-RitsuLib.dll" "$a_root/game/mods/CombatSolverHeadlessRitsuLib/STS2-RitsuLib.dll" || fail 'private dependency override failed'
    cmp -s -- "$source_root/mods/OtherMod/OtherMod.dll" "$a_root/game/mods/OtherMod/OtherMod.dll" || fail 'unrelated source mod was not copied'
    [[ "$(<"$a_root/snapshot-id")" == "$a_id" && "$(<"$b_root/snapshot-id")" == "$b_id" ]] || fail 'snapshot publication ID did not match frozen inputs'
    cp -a -- "$b_root/game" "$case_root/b-before"
    pass 'distinct DLL/manifest inputs publish into independent private game/mod trees'

    printf '%s\n' 'artifact-a-v2' >"$artifacts/a/CombatSolver.dll"
    a_next_id="$(snapshot_id a)"
    [[ "$a_next_id" != "$a_id" ]] || fail 'changed A build was not detected'
    prepare_snapshot a "$a_next_id" >"$case_root/update-a.log" 2>&1
    [[ "$(<"$a_root/game/mods/CombatSolver/CombatSolver.dll")" == artifact-a-v2 ]] || fail 'A update was not published'
    retired_games=("$a_root"/retired.*/game)
    ((${#retired_games[@]} == 1)) && [[ -d "${retired_games[0]}" ]] || fail 'old A snapshot was not retained exactly once'
    [[ "$(<"${retired_games[0]}/mods/CombatSolver/CombatSolver.dll")" == artifact-a-v1 ]] || fail 'retired A DLL cannot be recovered'
    [[ -x "${retired_games[0]}/SlayTheSpire2" ]] || fail 'retired snapshot lost its executable'
    pass 'updating A preserves its previous snapshot as a recoverable retired tree'

    "$a_root/game/SlayTheSpire2" 180 &
    live_pid=$!
    live_birth="$(process_birth "$live_pid")"
    driver_pids[snapshot-live]=$live_pid
    driver_births[snapshot-live]=$live_birth
    local deadline=$((SECONDS + 5))
    until [[ "$(readlink -f -- "/proc/$live_pid/exe")" == "$a_root/game/SlayTheSpire2" ]]; do
        ((SECONDS < deadline)) || fail 'private native snapshot fixture did not start'
        sleep 0.025
    done
    printf '%s\n' 'artifact-a-v3' >"$artifacts/a/CombatSolver.dll"
    a_rejected_id="$(snapshot_id a)"
    if prepare_snapshot a "$a_rejected_id" >"$case_root/live-refusal.log" 2>&1; then
        fail 'snapshot was replaced while its private executable was alive'
    fi
    rg -q 'cannot publish snapshot while its executable is alive' "$case_root/live-refusal.log" || fail 'live snapshot refused for a different reason'
    process_live "$live_pid" && [[ "$(process_birth "$live_pid")" == "$live_birth" ]] || fail 'refusing publication killed the private fixture'
    [[ "$(<"$a_root/snapshot-id")" == "$a_next_id" && \
        "$(<"$a_root/game/mods/CombatSolver/CombatSolver.dll")" == artifact-a-v2 ]] || fail 'refused publication changed active A artifacts'
    stop_fixture_process "$live_pid" "$live_birth" || fail 'could not stop private snapshot fixture'
    wait "$live_pid" || live_status=$?
    unset 'driver_pids[snapshot-live]' 'driver_births[snapshot-live]'
    ((live_status == 143)) || fail "private snapshot fixture exited $live_status, expected TERM (143)"
    pass 'live private executable prevents snapshot replacement without being stopped'

    diff -r -- "$case_root/source-before" "$source_root" >"$case_root/source.diff" || fail 'snapshot publication mutated the shared source'
    diff -r -- "$case_root/b-before" "$b_root/game" >"$case_root/b.diff" || fail 'A update changed B private files'
    [[ "$(<"$b_root/snapshot-id")" == "$b_id" ]] || fail 'A update changed B provenance'
    pass 'source tree and B remain unchanged across A update and rejected live publication'
}

run_snapshot_failure_tests() {
    new_case
    local source_root="$case_root/source-game" artifacts="$case_root/artifacts"
    local fault instance_root expected_old expected_new log_path hit_path
    local -a retired_games=()
    mkdir -p -- "$source_root/mods/CombatSolver" "$artifacts"
    cp -- "$(realpath -- "$(command -v sleep)")" "$source_root/SlayTheSpire2"
    printf '%s\n' 'source-mod-payload' >"$source_root/mods/CombatSolver/CombatSolver.dll"
    printf '%s\n' 'source-game-data' >"$source_root/game-data.pck"
    printf '%s\n' 'original-build' >"$artifacts/CombatSolver.dll"
    printf '%s\n' '{"id":"CombatSolver"}' >"$artifacts/CombatSolver.json"
    printf '%s\n' 'dependency-payload' >"$artifacts/STS2-RitsuLib.dll"
    printf '%s\n' '{"id":"STS2-RitsuLib"}' >"$artifacts/STS2-RitsuLib.json"

    failure_snapshot_id() (
        source "$runtime_helper"
        hr_snapshot_id "$source_root" "$artifacts/CombatSolver.dll" \
            "$artifacts/CombatSolver.json" "$artifacts/STS2-RitsuLib.dll" \
            "$artifacts/STS2-RitsuLib.json"
    )
    failure_prepare() (
        source "$runtime_helper"
        export COMBATSOLVER_HEADLESS_HOST_ROOT="$case_root/host"
        export HR_WORKTREE="$case_root/worktree-$fault"
        hr_init "$instance_root" "$fault" "$instance_root/game/SlayTheSpire2" \
            "$instance_root/data" parallel 1 1 10 || exit
        hr_prepare_snapshot "$source_root" "$artifacts/CombatSolver.dll" \
            "$artifacts/CombatSolver.json" "$artifacts/STS2-RitsuLib.dll" \
            "$artifacts/STS2-RitsuLib.json" "$1"
    )
    injected_hash() (
        source "$runtime_helper"
        find() {
            command find "$@" || return
            if [[ "$fault" == hash-find ]]; then
                builtin printf '%s\n' "$fault" >"$hit_path"
                return 17
            fi
        }
        sha256sum() {
            if [[ "$fault" == hash-intermediate && "$#" == 2 && \
                "$1" == -- && "$2" == "$artifacts/CombatSolver.dll" ]]; then
                builtin printf '%s\n' "$fault" >"$hit_path"
                return 19
            fi
            command sha256sum "$@"
        }
        # The enclosing conditional deliberately disables implicit errexit,
        # matching a real caller that explicitly handles this helper's status.
        hr_snapshot_id "$source_root" "$artifacts/CombatSolver.dll" \
            "$artifacts/CombatSolver.json" "$artifacts/STS2-RitsuLib.dll" \
            "$artifacts/STS2-RitsuLib.json"
    )
    injected_prepare() (
        source "$runtime_helper"
        export COMBATSOLVER_HEADLESS_HOST_ROOT="$case_root/host"
        export HR_WORKTREE="$case_root/worktree-$fault"
        hr_init "$instance_root" "$fault" "$instance_root/game/SlayTheSpire2" \
            "$instance_root/data" parallel 1 1 10 || exit
        injection_hit() { builtin printf '%s\n' "$fault" >"$hit_path"; }
        rm() {
            if [[ "$fault" == remove-mod && "${@: -1}" == "$instance_root"/snapshot.*/mods/CombatSolver ]]; then
                injection_hit
                return 21
            fi
            command rm "$@"
        }
        mkdir() {
            if [[ "$fault" == create-mod && "${@: -1}" == "$instance_root"/snapshot.*/mods/CombatSolver ]]; then
                injection_hit
                return 22
            fi
            command mkdir "$@"
        }
        cp() {
            if [[ "$fault" == copy-dll && "$#" == 3 && \
                "$1" == -- && "$2" == "$artifacts/CombatSolver.dll" ]]; then
                injection_hit
                return 23
            fi
            command cp "$@"
        }
        mktemp() {
            local argument
            if [[ "$fault" == retired-temp ]]; then
                for argument in "$@"; do
                    if [[ "$argument" == retired.XXXXXX ]]; then
                        injection_hit
                        return 24
                    fi
                done
            fi
            command mktemp "$@"
        }
        mv() {
            (($# >= 2)) || return 64
            local source_path="${@: -2:1}" target_path="${@: -1}"
            # A regression must not turn a failed retired mktemp into a real
            # move to /game (or any other path outside this temporary instance).
            if [[ "$target_path" != "$instance_root/"* ]]; then
                builtin printf '%s\n' "$target_path" >"$hit_path.unsafe-target"
                return 97
            fi
            case "$fault" in
                retire-move)
                    if [[ "$source_path" == "$instance_root/game" && "$target_path" == "$instance_root"/retired.*/game ]]; then
                        injection_hit
                        return 25
                    fi
                    ;;
                publish-move)
                    if [[ "$source_path" == "$instance_root"/snapshot.* && "$target_path" == "$instance_root/game" ]]; then
                        injection_hit
                        return 26
                    fi
                    ;;
                publish-id)
                    if [[ "$target_path" == "$instance_root/snapshot-id" ]]; then
                        injection_hit
                        return 27
                    fi
                    ;;
            esac
            command mv "$@"
        }
        # Keep the caller's || context: relying on set -e inside production
        # snapshot functions would incorrectly let these injected errors pass.
        local preparation_status=0
        hr_prepare_snapshot "$source_root" "$artifacts/CombatSolver.dll" \
            "$artifacts/CombatSolver.json" "$artifacts/STS2-RitsuLib.dll" \
            "$artifacts/STS2-RitsuLib.json" "$expected_new" || preparation_status=$?
        exit "$preparation_status"
    )

    for fault in hash-find hash-intermediate; do
        hit_path="$case_root/$fault.hit"
        log_path="$case_root/$fault.log"
        if injected_hash >"$log_path" 2>&1; then
            fail "$fault was masked by a later successful hash"
        fi
        [[ -f "$hit_path" ]] || fail "$fault failed without exercising its injected error"
        pass "$fault propagates failure even when later inputs are readable"
    done

    for fault in remove-mod create-mod copy-dll retired-temp retire-move publish-move publish-id; do
        instance_root="$case_root/instances/$fault"
        hit_path="$case_root/$fault.hit"
        log_path="$case_root/$fault.log"
        printf '%s\n' 'original-build' >"$artifacts/CombatSolver.dll"
        expected_old="$(failure_snapshot_id)"
        failure_prepare "$expected_old" >"$case_root/$fault.setup.log" 2>&1
        printf '%s\n' 'replacement-build' >"$artifacts/CombatSolver.dll"
        expected_new="$(failure_snapshot_id)"
        if injected_prepare >"$log_path" 2>&1; then
            fail "$fault was ignored and snapshot preparation reported success"
        fi
        [[ -f "$hit_path" ]] || fail "$fault failed without exercising its injected error"
        [[ ! -f "$hit_path.unsafe-target" ]] || fail "$fault attempted a move outside its temporary instance"
        if [[ -f "$instance_root/snapshot-id" ]]; then
            [[ "$(<"$instance_root/snapshot-id")" != "$expected_new" ]] || fail "$fault published the failed input ID"
        fi
        case "$fault" in
            publish-move|publish-id)
                retired_games=("$instance_root"/retired.*/game)
                ((${#retired_games[@]} == 1)) && [[ -d "${retired_games[0]}" ]] || fail "$fault lost its recoverable old tree"
                [[ "$(<"${retired_games[0]}/mods/CombatSolver/CombatSolver.dll")" == original-build ]] || fail "$fault damaged its retired old payload"
                if [[ -f "$instance_root/snapshot-id" && -d "$instance_root/game" ]]; then
                    [[ "$(<"$instance_root/game/mods/CombatSolver/CombatSolver.dll")" == original-build && \
                        "$(<"$instance_root/snapshot-id")" == "$expected_old" ]] || fail "$fault left a new game tree paired with stale cache provenance"
                fi
                ;;
            *)
                [[ "$(<"$instance_root/game/mods/CombatSolver/CombatSolver.dll")" == original-build ]] || fail "$fault replaced the previously valid game tree"
                if [[ -f "$instance_root/snapshot-id" ]]; then
                    [[ "$(<"$instance_root/snapshot-id")" == "$expected_old" ]] || fail "$fault changed the previous valid snapshot ID"
                elif [[ "$fault" != retire-move ]]; then
                    fail "$fault invalidated provenance before the retirement boundary"
                fi
                ;;
        esac
        pass "$fault returns failure and cannot publish a falsely valid snapshot"
    done
}

if [[ "$test_selection" == snapshot-failures ]]; then
    run_snapshot_failure_tests
    echo "HEADLESS_RUNTIME_TESTS_PASSED count=$passed scope=helper_snapshot_failure_injection game_started=false"
    exit 0
fi

if [[ "$test_selection" == snapshots ]]; then
    run_snapshot_tests
    echo "HEADLESS_RUNTIME_TESTS_PASSED count=$passed scope=helper_snapshot_mock game_started=false"
    exit 0
fi

run_warm_memory_tests() {
    local warm_pid warm_birth warm_token warm_lease fixture_host_fd
    new_case
    ln -s -- "$test_script" "$case_root/bin/awk"
    printf '%s\n' 32768 >"$case_root/memory-total-mib"
    printf '%s\n' 12288 >"$case_root/memory-available-mib"
    printf '%s\n' 3072 >"$case_root/memory-rss-mib"
    start_driver memory-first warm parallel hold '' '' 10 4096
    await_acquired memory-first
    warm_pid="$(field memory-first pid)"
    warm_birth="$(field memory-first birth)"
    warm_token="$(field memory-first token)"
    send_control memory-first warm
    await_exit memory-first 0
    start_driver memory-peer peer parallel hold '' '' 10 4096
    await_acquired memory-peer

    # Each existing game has 1 GiB of its reservation left to consume. Available
    # memory must cover both outstanding portions and 2 GiB of host headroom.
    printf '%s\n' 4095 >"$case_root/memory-available-mib.next"
    mv -- "$case_root/memory-available-mib.next" "$case_root/memory-available-mib"
    start_driver memory-reuse warm parallel hold "$warm_pid" "$warm_birth" 10 4096
    assert_queued memory-reuse
    assert_game_live memory-peer
    [[ ! -e "${driver_dirs[memory-reuse]}/acquired.json" ]] || fail 'warm reuse ignored peer outstanding memory or host headroom'
    printf '%s\n' 4096 >"$case_root/memory-available-mib.next"
    mv -- "$case_root/memory-available-mib.next" "$case_root/memory-available-mib"
    await_acquired memory-reuse
    [[ "$(field memory-reuse pid)" == "$warm_pid" && "$(field memory-reuse token)" == "$warm_token" ]] ||
        fail 'warm memory admission changed the owned game or reservation'
    send_control memory-reuse warm
    await_exit memory-reuse 0

    # RSS beyond the reservation must not become negative outstanding memory
    # that consumes the mandatory host headroom. No memory is actually allocated.
    printf '%s\n' 8192 >"$case_root/memory-rss-mib.next"
    mv -- "$case_root/memory-rss-mib.next" "$case_root/memory-rss-mib"
    printf '%s\n' 2047 >"$case_root/memory-available-mib.next"
    mv -- "$case_root/memory-available-mib.next" "$case_root/memory-available-mib"
    start_driver memory-clamped warm parallel hold "$warm_pid" "$warm_birth" 10 4096
    assert_queued memory-clamped
    [[ ! -e "${driver_dirs[memory-clamped]}/acquired.json" ]] || fail 'excess warm RSS incorrectly credited host headroom'
    printf '%s\n' 2048 >"$case_root/memory-available-mib.next"
    mv -- "$case_root/memory-available-mib.next" "$case_root/memory-available-mib"
    await_acquired memory-clamped
    start_driver memory-cold cold parallel hold '' '' 10 4096
    assert_queued memory-cold
    finish_driver memory-clamped
    assert_queued memory-cold
    [[ ! -e "${driver_dirs[memory-cold]}/acquired.json" ]] || fail 'a new game received warm RSS credit'
    printf '%s\n' 6144 >"$case_root/memory-available-mib.next"
    mv -- "$case_root/memory-available-mib.next" "$case_root/memory-available-mib"
    await_acquired memory-cold
    assert_game_live memory-peer
    finish_driver memory-cold
    finish_driver memory-peer
    pass 'warm reuse accounts for own/peer outstanding memory; RSS credit is clamped; cold requests keep full reservation'

    new_case
    ln -s -- "$test_script" "$case_root/bin/awk"
    printf '%s\n' 32768 >"$case_root/memory-total-mib"
    printf '%s\n' 12288 >"$case_root/memory-available-mib"
    printf '%s\n' 3072 >"$case_root/memory-rss-mib"
    start_driver memory-token-first token-warm parallel hold '' '' 10 4096
    await_acquired memory-token-first
    warm_pid="$(field memory-token-first pid)"
    warm_birth="$(field memory-token-first birth)"
    warm_lease="$(field memory-token-first lease)"
    send_control memory-token-first warm
    await_exit memory-token-first 0
    printf '%s\n' 3071 >"$case_root/memory-available-mib"
    start_driver memory-token-reuse token-warm parallel hold "$warm_pid" "$warm_birth" 10 4096
    assert_queued memory-token-reuse
    # Publish the replacement and available memory under the same real host
    # lock, so no in-flight admission can still be observing the old token.
    exec {fixture_host_fd}>"$case_root/host/coordinator.lock"
    flock -w 5 "$fixture_host_fd" || fail 'cannot lock fixture coordinator'
    jq '.token = "replacement-token"' "$warm_lease" >"$warm_lease.next"
    mv -- "$warm_lease.next" "$warm_lease"
    printf '%s\n' 12288 >"$case_root/memory-available-mib"
    flock -u "$fixture_host_fd"
    exec {fixture_host_fd}>&-
    await_exit memory-token-reuse nonzero
    [[ ! -e "${driver_dirs[memory-token-reuse]}/acquired.json" ]] || fail 'warm reuse admitted after its lease changed'
    jq -e '.token == "replacement-token"' "$warm_lease" >/dev/null || fail 'rejected reuse removed a replacement lease'
    assert_game_live memory-token-first
    stop_fixture_process "$warm_pid" "$warm_birth" || fail 'cannot stop the owned warm fixture'
    pass 'waiting warm reuse rejects a replaced token and preserves its live game and replacement lease'
}

if [[ "$test_selection" == warm-memory ]]; then
    run_warm_memory_tests
    echo "HEADLESS_RUNTIME_TESTS_PASSED count=$passed scope=helper_warm_memory_mock game_started=false"
    exit 0
fi

new_case
start_driver concurrent-a alpha parallel
await_acquired concurrent-a
start_driver concurrent-b beta parallel
await_acquired concurrent-b
assert_game_live concurrent-a
[[ "$(field concurrent-a root)" != "$(field concurrent-b root)" ]] || fail "instance roots overlap"
[[ "$(field concurrent-a lease)" != "$(field concurrent-b lease)" ]] || fail "instance leases overlap"
start_driver duplicate-a alpha parallel
await_exit duplicate-a nonzero
assert_game_live concurrent-a
assert_game_live concurrent-b
pass 'two instances coexist; a simultaneous producer for the same instance is rejected'

start_driver third gamma parallel
assert_queued third
send_control concurrent-a fail
await_exit concurrent-a 23
[[ ! -e "$(field concurrent-a lease)" ]] || fail 'failed instance leaked its lease'
await_acquired third
assert_game_live concurrent-b
pass 'third parallel waits; failure releases only A and leaves B alive'

start_driver exclusive-one delta exclusive
assert_queued exclusive-one
finish_driver concurrent-b
assert_queued exclusive-one
assert_game_live third
finish_driver third
await_acquired exclusive-one
start_driver behind-exclusive epsilon parallel
assert_queued behind-exclusive
finish_driver exclusive-one
await_acquired behind-exclusive
finish_driver behind-exclusive
pass 'exclusive waits for all parallel games; parallel cannot enter an active exclusive lease'

new_case
start_driver warm-first warm parallel
await_acquired warm-first
warm_pid="$(field warm-first pid)"
warm_birth="$(field warm-first birth)"
warm_token="$(field warm-first token)"
warm_lease="$(field warm-first lease)"
send_control warm-first warm
await_exit warm-first 0
[[ -f "$warm_lease" ]] || fail 'warm game lost its lease when its producer exited'
start_driver warm-peer peer parallel
await_acquired warm-peer
start_driver warm-third third parallel
assert_queued warm-third
start_driver warm-reuse warm parallel hold "$warm_pid" "$warm_birth"
await_acquired warm-reuse
[[ "$(field warm-reuse token)" == "$warm_token" ]] || fail 'warm reuse replaced its reservation token'
[[ "$(field warm-reuse pid)" == "$warm_pid" ]] || fail 'warm reuse started a different process'
assert_queued warm-third
finish_driver warm-reuse
await_acquired warm-third
assert_game_live warm-peer
finish_driver warm-peer
finish_driver warm-third
pass 'warm game retains capacity; reuse keeps one lease while the pool is full'

run_warm_memory_tests

new_case
start_driver cancel-blocker blocker exclusive
await_acquired cancel-blocker
start_driver cancel-queued canceled parallel
assert_queued cancel-queued
kill -TERM "${driver_pids[cancel-queued]}"
await_exit cancel-queued 143
finish_driver cancel-blocker
start_driver cancel-retry canceled parallel
await_acquired cancel-retry
start_driver cancel-other other parallel
await_acquired cancel-other
finish_driver cancel-retry
finish_driver cancel-other
pass 'queue cancellation releases producer ownership and does not leak pool capacity'

new_case
start_driver timeout-blocker blocker exclusive
await_acquired timeout-blocker
start_driver queue-timeout timed-out parallel hold '' '' 1
assert_queued queue-timeout
await_exit queue-timeout nonzero
rg -q 'queue timeout:' "${driver_dirs[queue-timeout]}/driver.log" || fail 'queue timeout failed for another reason'
finish_driver timeout-blocker
start_driver timeout-retry timed-out parallel
await_acquired timeout-retry
finish_driver timeout-retry
pass 'queue timeout is explicit and releases the instance for a later producer'

new_case
start_driver pending-only pending parallel pending
await_acquired pending-only
pending_lease="$(field pending-only lease)"
[[ -f "$pending_lease" ]] || fail 'pending reservation was never recorded'
finish_driver pending-only
[[ ! -e "$pending_lease" ]] || fail 'unbound reservation was not released'
pass 'a producer exiting before game bind releases its pending reservation'

new_case
start_driver orphan-first orphan parallel unbound
await_acquired orphan-first
orphan_pid="$(field orphan-first pid)"
orphan_birth="$(field orphan-first birth)"
orphan_lease="$(field orphan-first lease)"
send_control orphan-first warm
await_exit orphan-first 0
[[ -f "$orphan_lease" ]] || fail 'spawn-before-bind orphan lost its pending reservation'
start_driver orphan-peer peer parallel
assert_queued orphan-peer
assert_game_live orphan-first
stop_fixture_process "$orphan_pid" "$orphan_birth" || fail 'could not end unbound fixture game'
await_acquired orphan-peer
[[ ! -e "$orphan_lease" ]] || fail 'exited unbound game still occupies a pending reservation'
finish_driver orphan-peer
pass 'spawn-before-bind orphan keeps its reservation until the private executable exits'

new_case
start_driver stale-warm stale parallel
await_acquired stale-warm
stale_pid="$(field stale-warm pid)"
stale_birth="$(field stale-warm birth)"
stale_lease="$(field stale-warm lease)"
send_control stale-warm warm
await_exit stale-warm 0
stop_fixture_process "$stale_pid" "$stale_birth" || fail 'could not end stale fixture game'
[[ -f "$stale_lease" ]] || fail 'stale test needs the warm lease left behind'
start_driver stale-reaper replacement parallel
await_acquired stale-reaper
[[ ! -e "$stale_lease" ]] || fail 'exited game lease was not scavenged'
finish_driver stale-reaper
pass 'admission scavenges a lease after its exact game identity exits'

new_case
start_driver identity-live identity parallel
await_acquired identity-live
identity_pid="$(field identity-live pid)"
identity_birth="$(field identity-live birth)"
identity_status=0
(
    source "$runtime_helper"
    hr_identity_state "$identity_pid" "$((identity_birth + 1))"
) || identity_status=$?
((identity_status == 1)) || fail "mismatched birth returned $identity_status, expected exited identity (1)"
assert_game_live identity-live
identity_lease="$(field identity-live lease)"
send_control identity-live warm
await_exit identity-live 0
jq --arg birth "$((identity_birth + 1))" '.gameStart = $birth' "$identity_lease" >"$identity_lease.test.tmp"
mv -- "$identity_lease.test.tmp" "$identity_lease"
start_driver identity-reaper another parallel
await_acquired identity-reaper
[[ ! -e "$identity_lease" ]] || fail 'mismatched-birth stale lease was not removed'
assert_game_live identity-live
finish_driver identity-reaper
stop_fixture_process "$identity_pid" "$identity_birth" || fail 'could not stop owned identity fixture'
pass 'stale PID birth is scavenged without signaling the unrelated live identity'

new_case
start_driver known-game known parallel
await_acquired known-game
known_pid="$(field known-game pid)"
known_birth="$(field known-game birth)"
known_lease="$(field known-game lease)"
send_control known-game warm
await_exit known-game 0
# Remove only the fixture's admission record to model an unregistered process.
# The test's independent PID/birth registry continues to own final cleanup.
mv -- "$known_lease" "$case_root/lease.saved"
printf '%s\n' "$known_pid" >"$case_root/enumerated-game.pid"
start_driver unknown-blocked blocked parallel
assert_queued unknown-blocked
rg -q 'reason=unregistered game process' "${driver_dirs[unknown-blocked]}/driver.log" || fail 'unknown-game guard was not the blocker'
assert_game_live known-game
mv -- "$case_root/lease.saved" "$known_lease"
await_acquired unknown-blocked
assert_game_live known-game
finish_driver unknown-blocked
stop_fixture_process "$known_pid" "$known_birth" || fail 'could not stop enumerated fixture game'
pass 'strict pgrep stub exercises unknown-game blocking and registered-game recognition'

echo "HEADLESS_RUNTIME_TESTS_PASSED count=$passed scope=helper_native_process_mock game_started=false"
