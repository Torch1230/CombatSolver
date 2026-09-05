#!/usr/bin/env bash
# No game: execute the real matrix against a disposable mock request launcher.
# The launcher owns protocol/PID validation; these tests cover matrix routing,
# lifecycle boundaries, serialization and fail-closed ownership checks.
set -Eeuo pipefail
if [[ ${0##*/} == run-unattended-test.sh ]]; then
    declare -A args=()
    while (($#)); do
        key=$1; shift
        case "$key" in
            --exit-on-complete|--stop-after-combat-root-snapshot-assertion|--stop-instance) args[$key]=true ;;
            *) args[$key]=$1; shift ;;
        esac
    done
    root=${COMBATSOLVER_HEADLESS_ROOT:?}
    [[ ${args[--stop-instance]:-} != true ]] || args[--scenario-id]=STOP-INSTANCE
    jq -cn --arg root "$root" --arg instance "${args[--headless-instance]}" \
        --arg scenario "${args[--scenario-id]}" --arg mode "${args[--headless-execution-mode]}" \
        --arg build "${args[--combat-solver-build-dir]:-}" --arg memory "${args[--headless-memory-reservation-mib]}" \
        --arg cpu "${args[--headless-cpu-reservation]}" --arg queue "${args[--headless-queue-timeout-seconds]}" \
        '{root:$root,instance:$instance,scenario:$scenario,mode:$mode,build:$build,memory:$memory,cpu:$cpu,queue:$queue}' >>"$MATRIX_TEST_LOG"
    if [[ ${args[--scenario-id]} == STOP-INSTANCE ]]; then
        rm -- "$root/process.json"
        exit 0
    fi
    jq -n --arg exe "$root/game/SlayTheSpire2" --arg data "$root/data/SlayTheSpire2" \
        '{pid:999999999,procStartTimeTicks:"123",executable:$exe,dataDir:$data}' >"$root/process.json"
    case ${MATRIX_TEST_MODE:-normal} in
        foreign) jq '.executable="/foreign/game/SlayTheSpire2"' "$root/process.json" >"$root/changed.json"; mv "$root/changed.json" "$root/process.json" ;;
        owner) jq '.worktree="/foreign/worktree"' "$root/runtime-owner.json" >"$root/changed.json"; mv "$root/changed.json" "$root/runtime-owner.json" ;;
        legacy) printf '%s\n' '{"pid":999999999}' >"$root/process.json" ;;
        hang)
            trap 'rm -f -- "$root/process.json"; exit 143' TERM INT HUP
            printf '%s\n' ready >"$root/mock-ready"
            sleep 20 & wait $!
            ;;
    esac
    exit 0
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
if [[ ${1:-} == --stop-only ]]; then
    # Exercise the real launcher and real /proc checks, not the mock launcher.
    # Only private copies of native sleep are started; there is no game/DLL.
    stop_root="$(mktemp -d -t combatsolver-stop.XXXXXXXX)"
    export COMBATSOLVER_HEADLESS_HOST_ROOT="$stop_root/host"
    source "$script_dir/headless-runtime.sh"
    HR_WORKTREE="$(cd -- "$script_dir/.." && pwd)"
    native_pids=(); native_births=()
    cleanup_native_fixtures() {
        local i
        for i in "${!native_pids[@]}"; do
            if hr_identity_state "${native_pids[$i]}" "${native_births[$i]}"; then
                kill -TERM "${native_pids[$i]}"
            fi
            wait "${native_pids[$i]}" 2>/dev/null || true
        done
    }
    trap cleanup_native_fixtures EXIT
    write_stop_marker() {
        jq -n --argjson pid "$2" --arg birth "$3" --arg exe "$1/game/SlayTheSpire2" --arg data "$1/data/SlayTheSpire2" \
            '{pid:$pid,procStartTimeTicks:$birth,executable:$exe,dataDir:$data}' >"$1/process.json"
    }
    start_native_fixture() {
        local root=$1 id=$2
        hr_init "$root" "$id" "$root/game/SlayTheSpire2" "$root/data" parallel 2048 1 1
        mkdir -p "$root/game"
        cp -- "$(command -v sleep)" "$root/game/SlayTheSpire2"
        env COMBATSOLVER_HEADLESS=1 XDG_DATA_HOME="$root/data" "$root/game/SlayTheSpire2" 60 &
        fixture_pid=$!; fixture_birth="$(hr_start_time "$fixture_pid")"
        native_pids+=("$fixture_pid"); native_births+=("$fixture_birth")
        write_stop_marker "$root" "$fixture_pid" "$fixture_birth"
        # Running reservation belongs to the game, not this request launcher.
        jq -n --arg root "$root" --arg exe "$HR_EXECUTABLE" --argjson pid "$fixture_pid" --arg birth "$fixture_birth" \
            '{schemaVersion:1,state:"running",token:"native-fixture",instanceRoot:$root,executable:$exe,gamePid:$pid,gameStart:$birth}' >"$HR_LEASE_PATH"
        flock -u "$HR_INSTANCE_FD"; exec {HR_INSTANCE_FD}>&-
    }
    stop_command() {
        COMBATSOLVER_HEADLESS_ROOT="$1" bash "$script_dir/run-unattended-test.sh" --stop-instance --headless-instance "$2" \
            --sts2-game-root "$stop_root/missing-source" --ritsu-workshop-root "$stop_root/missing-dependency" \
            --combat-solver-build-dir "$stop_root/missing-build" >"$stop_root/$3.log" 2>&1
    }
    expect_stop_rejected() {
        if stop_command "$1" "$2" "$3"; then echo "Expected stop rejection: $3" >&2; exit 1; fi
        hr_identity_state "$peer_pid" "$peer_birth"
    }
    start_native_fixture "$stop_root/a" a
    own_pid=$fixture_pid; own_birth=$fixture_birth; own_lease=$HR_LEASE_PATH
    start_native_fixture "$stop_root/b" b
    peer_pid=$fixture_pid; peer_birth=$fixture_birth; peer_lease=$HR_LEASE_PATH
    exec {busy_fd}>"$stop_root/a/launcher.lock"; flock -n "$busy_fd"
    expect_stop_rejected "$stop_root/a" a locked
    hr_identity_state "$own_pid" "$own_birth"
    flock -u "$busy_fd"; exec {busy_fd}>&-
    stop_command "$stop_root/a" a owned
    ! hr_identity_state "$own_pid" "$own_birth"
    hr_identity_state "$peer_pid" "$peer_birth"
    [[ ! -e $stop_root/a/process.json && ! -e $own_lease && -e $peer_lease ]]
    [[ ! -e $stop_root/a/data && ! -e $stop_root/missing-source && ! -e $stop_root/missing-build ]]
    printf '%s\n' 'STOP_NATIVE_PASS exact-owned-only/no-build-no-request-no-admission/lease-isolation/producer-lock'
    write_stop_marker "$stop_root/a" "$own_pid" "$own_birth"
    stop_command "$stop_root/a" a stale
    [[ ! -e $stop_root/a/process.json ]]
    stop_command "$stop_root/a" a absent
    printf '%s\n' 'STOP_NATIVE_PASS stale-and-absent-idempotent/no-new-process'
    printf '%s\n' '{}' >"$stop_root/a/process.json"
    expect_stop_rejected "$stop_root/a" a no-pid
    [[ $(<"$stop_root/a/process.json") == '{}' ]]
    write_stop_marker "$stop_root/a" "$peer_pid" "$((peer_birth+1))"
    expect_stop_rejected "$stop_root/a" a reused-birth
    write_stop_marker "$stop_root/a" "$peer_pid" "$peer_birth"
    expect_stop_rejected "$stop_root/a" a peer-executable
    mv "$stop_root/b/process.json" "$stop_root/b/saved-marker.json"
    expect_stop_rejected "$stop_root/b" b markerless
    mv "$stop_root/b/saved-marker.json" "$stop_root/b/process.json"
    stop_command "$stop_root/b" b peer-final
    [[ ! -e $peer_lease ]]
    printf 'STOP_NATIVE_PASS no-pid/reused-birth/peer-executable/markerless-preserved evidence=%s\n' "$stop_root"
    exit 0
fi
test_root="$(mktemp -d -t combatsolver-matrix.XXXXXXXX)"
repo="$test_root/repo with space"
mkdir -p "$repo/tools" "$repo/docs" "$test_root/source/mods/.combatsolver-headless-ritsulib"
cp "$script_dir/run-headless-matrix.sh" "$script_dir/headless-runtime.sh" "$repo/tools/"
cp "$script_dir/test-headless-matrix-runtime.sh" "$repo/tools/run-unattended-test.sh"
chmod +x "$repo/tools/run-unattended-test.sh"
printf '%s\n' source-sentinel >"$test_root/source/mods/.combatsolver-headless-ritsulib/sentinel"
printf '%s\n' 'tools/run-unattended-test.sh --scenario-id MOCK-A' 'tools/run-unattended-test.sh --scenario-id MOCK-B' >"$repo/docs/TEST_MATRIX.md"
export XDG_STATE_HOME="$test_root/state" COMBATSOLVER_HEADLESS_HOST_ROOT="$test_root/host"
unset COMBATSOLVER_HEADLESS_ROOT
export MATRIX_TEST_LOG="$test_root/default.jsonl" MATRIX_TEST_MODE=normal
runner="$repo/tools/run-headless-matrix.sh"
common=(--sts2-game-root "$test_root/source" --ritsu-workshop-root "$test_root/ritsu"
    --headless-execution-mode parallel --headless-memory-reservation-mib 2048
    --headless-cpu-reservation 1 --headless-queue-timeout-seconds 3
    --combat-solver-build-dir "$test_root/frozen build")
bash "$runner" "${common[@]}" >"$test_root/default.log" 2>&1
jq -se --arg state "$XDG_STATE_HOME" --arg build "$test_root/frozen build" '
    length==3 and ([.[].root]|unique|length)==1 and ([.[].instance]|unique|length)==1 and
    (.[0].root|startswith($state+"/CombatSolver/headless-instances/worktree-")) and
    .[2].scenario=="STOP-INSTANCE" and all(.[];.mode=="parallel" and .memory=="2048" and .cpu=="1" and .queue=="3" and .build==$build)' "$MATRIX_TEST_LOG" >/dev/null
printf '%s\n' 'MATRIX_MOCK_PASS default-instance/warm-final-exit/parameter-forwarding'

export COMBATSOLVER_HEADLESS_ROOT="$test_root/explicit" MATRIX_TEST_LOG="$test_root/explicit.jsonl"
mkdir -p "$test_root/peer"; printf '%s\n' peer-sentinel >"$test_root/peer/process.json"
bash "$runner" "${common[@]}" --headless-instance explicit >"$test_root/explicit.log" 2>&1
jq -se --arg root "$COMBATSOLVER_HEADLESS_ROOT" 'length==3 and all(.[];.root==$root and .instance=="explicit")' "$MATRIX_TEST_LOG" >/dev/null
[[ $(<"$test_root/peer/process.json") == peer-sentinel ]]
printf '%s\n' 'MATRIX_MOCK_PASS explicit-instance/peer-preserved'

for mode in foreign owner legacy; do
    export COMBATSOLVER_HEADLESS_ROOT="$test_root/$mode" MATRIX_TEST_LOG="$test_root/$mode.jsonl" MATRIX_TEST_MODE=$mode
    if bash "$runner" "${common[@]}" --headless-instance "$mode" --max-cases 1 >"$test_root/$mode.log" 2>&1; then
        echo "Expected ownership rejection: $mode" >&2; exit 1
    fi
    [[ -f $COMBATSOLVER_HEADLESS_ROOT/process.json ]]
    jq -se 'length==1 and .[0].scenario=="MOCK-A"' "$MATRIX_TEST_LOG" >/dev/null
    printf 'MATRIX_MOCK_PASS %s-rejected-and-preserved\n' "$mode"
done

export COMBATSOLVER_HEADLESS_ROOT="$test_root/cancel" MATRIX_TEST_LOG="$test_root/cancel.jsonl" MATRIX_TEST_MODE=hang
setsid bash "$runner" "${common[@]}" --headless-instance cancel --max-cases 1 >"$test_root/cancel.log" 2>&1 & matrix_pid=$!
deadline=$((SECONDS+8))
until [[ -f $COMBATSOLVER_HEADLESS_ROOT/mock-ready ]]; do
    ((SECONDS<deadline)) || { kill -TERM "$matrix_pid"; wait "$matrix_pid" || true; exit 1; }
    sleep 0.05
done
if bash "$runner" "${common[@]}" --headless-instance cancel --max-cases 1 >"$test_root/duplicate.log" 2>&1; then
    kill -TERM "$matrix_pid"; wait "$matrix_pid" || true; exit 1
fi
kill -TERM "$matrix_pid"
status=0; wait "$matrix_pid" || status=$?
[[ $status == 143 && ! -f $COMBATSOLVER_HEADLESS_ROOT/process.json ]]
[[ $(<"$test_root/source/mods/.combatsolver-headless-ritsulib/sentinel") == source-sentinel ]]
[[ $(<"$test_root/peer/process.json") == peer-sentinel ]]
printf 'MATRIX_MOCK_PASS same-instance-serialized/cancel/source-and-peer-preserved evidence=%s\n' "$test_root"
